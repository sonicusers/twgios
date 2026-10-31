using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WogiOS;

// 게임 하단 안내줄(A 확인 · B 취소 · X 커맨드 · L 조작)의 색 알약 아이콘을
// "속이 빈 흰 윤곽선 + 글자" 모양으로 바꾼다.
//   GamePad.DrawCap 안의 FillRound 3번(그림자 · 색 칠 · 하이라이트)을
//     1번째(그림자)      -> 건너뜀
//     2번째(색 칠)       -> 윤곽선 그림(CapRing.Get)을 Hi.DrawImage 로 그림
//                           (Hi 화면을 안 쓰는 모드에서는 게임의 StrokeRound 로 1픽셀 윤곽선)
//     3번째(하이라이트)  -> 건너뜀
//   + 안내줄 뒤 어두운 네모 박스(GamePad.DrawGuide)를 20%p 더 투명하게
//   + 노란 Y 버튼용 어두운 글자색(0xFF201800)을 흰색으로
// 여러 번 실행해도 안전하다(이미 바뀌어 있으면 아무것도 하지 않는다).
internal static class CapHook
{
    // 옛 방식(흰 알약 + 어두운 알약 겹치기)으로 이미 패치된 patched 폴더를 한 번 비우기 위한 표식
    const string Stamp = "caphook-ring-image-1";

    const int RingColor = unchecked((int)0xF0F0F0F4);   // Hi 화면을 안 쓸 때의 StrokeRound 윤곽선 색

    // 안내줄 뒤의 어두운 네모 박스: 불투명도 약 63%(0xA0) -> 약 43%(0x6D), 즉 20%p 낮춤.
    // 더 투명하게 하려면 0x6D 를 낮추고(0x00 = 완전 투명), 진하게 하려면 높인다(0xA0 = 원래 값).
    const int BackdropOld = unchecked((int)0xA0000000);
    const int BackdropNew = unchecked((int)0x6D000000);

    // 이전 방식으로 패치된 patched 폴더가 남아 있으면 한 번 지워서 다시 만들게 한다.
    public static void ResetIfOutdated(string patchedDir, Action<string> log)
    {
        try
        {
            string dir = Path.GetDirectoryName(patchedDir.TrimEnd(Path.DirectorySeparatorChar));
            string stampFile = Path.Combine(dir, "caphook.stamp");
            string was = File.Exists(stampFile) ? File.ReadAllText(stampFile).Trim() : null;
            if (was == Stamp) return;
            if (Directory.Exists(patchedDir))
            {
                Directory.Delete(patchedDir, true);
                log("cap hook: 안내줄 패치 방식이 바뀌어 patched 폴더를 한 번 다시 만듭니다");
            }
            File.WriteAllText(stampFile, Stamp);
        }
        catch (Exception ex) { log("cap hook: reset check failed: " + ex.Message); }
    }

    public static void Apply(string gameDllPath, Action<string> log)
    {
        try
        {
            if (!File.Exists(gameDllPath)) { log("cap hook: file missing " + gameDllPath); return; }

            using var asm = AssemblyDefinition.ReadAssembly(gameDllPath, new ReaderParameters { ReadWrite = true });
            ModuleDefinition module = asm.MainModule;
            TypeDefinition pad = module.GetTypes()
                .FirstOrDefault(t => t.Name == "GamePad" && t.Methods.Any(m => m.Name == "DrawCap"));
            MethodDefinition drawCap = pad?.Methods.FirstOrDefault(m => m.Name == "DrawCap" && m.HasBody);
            MethodDefinition capText = pad?.Methods.FirstOrDefault(m => m.Name == "CapText" && m.HasBody);
            if (drawCap == null || capText == null)
            {
                log("cap hook: GamePad.DrawCap/CapText not found (game version changed?) - skipped");
                return;
            }

            bool changed = false;
            var body = drawCap.Body;

            bool alreadyRing = body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType != null && m.DeclaringType.Name == "CapRing");
            bool oldStyle = body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4 && i.Operand is int v && v == unchecked((int)0xE8101018));

            if (oldStyle)
            {
                log("cap hook: 옛 방식 패치가 남아 있음 - patched 폴더를 지우고 다시 실행해 주세요");
            }
            else if (!alreadyRing)
            {
                var fills = body.Instructions
                    .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                                && i.Operand is MethodReference mr && mr.Name == "FillRound"
                                && mr.ReturnType.MetadataType == MetadataType.Void
                                && mr.Parameters.Count == 6)
                    .ToList();

                FieldDefinition fHost = pad.Fields.FirstOrDefault(f => f.Name == "<host>P");
                MethodDefinition getOnLayer = pad.Methods.FirstOrDefault(m => m.Name == "get_OnLayer");
                MethodDefinition strokeRound = pad.Methods.FirstOrDefault(m => m.Name == "StrokeRound" && m.Parameters.Count == 6);
                TypeDefinition hostType = fHost?.FieldType.Resolve();
                MethodDefinition getHi = hostType?.Methods.FirstOrDefault(m => m.Name == "get_Hi");
                TypeDefinition hiType = getHi?.ReturnType.Resolve();
                MethodDefinition drawImage = hiType?.Methods.FirstOrDefault(m => m.Name == "DrawImage" && m.Parameters.Count == 10);

                if (fills.Count != 3 || fHost == null || getOnLayer == null || strokeRound == null
                    || getHi == null || drawImage == null)
                {
                    log("cap hook: expected members not found (FillRound x" + fills.Count + ") - skipped");
                }
                else
                {
                    ILProcessor il = body.GetILProcessor();
                    TypeSystem ts = module.TypeSystem;

                    // 앱 본체 어셈블리의 CapRing.Get(w, h, r) 참조
                    var self = typeof(CapHook).Assembly.GetName();
                    var hostRef = new AssemblyNameReference(self.Name, self.Version);
                    module.AssemblyReferences.Add(hostRef);
                    var ringType = new TypeReference("WogiOS", "CapRing", module, hostRef);
                    var ringGet = new MethodReference("Get", new ArrayType(ts.UInt32), ringType) { HasThis = false };
                    ringGet.Parameters.Add(new ParameterDefinition(ts.Int32));
                    ringGet.Parameters.Add(new ParameterDefinition(ts.Int32));
                    ringGet.Parameters.Add(new ParameterDefinition(ts.Int32));

                    // 1번째 · 3번째: 인자만 버리고 건너뜀
                    foreach (int idx in new[] { 0, 2 })
                    {
                        Instruction ins = fills[idx];
                        var mr = (MethodReference)ins.Operand;
                        int args = mr.Parameters.Count + (mr.HasThis ? 1 : 0);
                        ins.OpCode = OpCodes.Pop;
                        ins.Operand = null;
                        Instruction prev = ins;
                        for (int i = 1; i < args; i++)
                        {
                            Instruction p = il.Create(OpCodes.Pop);
                            il.InsertAfter(prev, p);
                            prev = p;
                        }
                    }

                    // 2번째: 인자를 지역 변수에 받아 두고 윤곽선 그림을 그린다
                    {
                        Instruction ins = fills[1];
                        Instruction end = ins.Next;
                        var vx = new VariableDefinition(ts.Int32);
                        var vy = new VariableDefinition(ts.Int32);
                        var vw = new VariableDefinition(ts.Int32);
                        var vh = new VariableDefinition(ts.Int32);
                        var vr = new VariableDefinition(ts.Int32);
                        var vc = new VariableDefinition(ts.UInt32);
                        foreach (var v in new[] { vx, vy, vw, vh, vr, vc }) body.Variables.Add(v);
                        body.InitLocals = true;

                        Instruction lowStart = il.Create(OpCodes.Ldarg_0);
                        int S = CapRing.Scale;

                        var seq = new List<Instruction>
                        {
                            // 스택: this, x, y, w, h, r, color  ->  거꾸로 꺼내 저장
                            il.Create(OpCodes.Stloc, vc),
                            il.Create(OpCodes.Stloc, vr),
                            il.Create(OpCodes.Stloc, vh),
                            il.Create(OpCodes.Stloc, vw),
                            il.Create(OpCodes.Stloc, vy),
                            il.Create(OpCodes.Stloc, vx),
                            il.Create(OpCodes.Pop),   // this

                            // Hi 화면을 쓰는 모드인가?
                            il.Create(OpCodes.Ldarg_0),
                            il.Create(OpCodes.Call, getOnLayer),
                            il.Create(OpCodes.Brfalse, lowStart),

                            // Hi.DrawImage(data, stride, imgW, imgH, x, y, w, h, opaque:false, dim:false)
                            il.Create(OpCodes.Ldarg_0),
                            il.Create(OpCodes.Ldfld, fHost),
                            il.Create(OpCodes.Callvirt, getHi),
                            il.Create(OpCodes.Ldloc, vw), il.Create(OpCodes.Ldloc, vh), il.Create(OpCodes.Ldloc, vr),
                            il.Create(OpCodes.Call, ringGet),
                            il.Create(OpCodes.Ldloc, vw), il.Create(OpCodes.Ldc_I4, S), il.Create(OpCodes.Mul),
                            il.Create(OpCodes.Ldloc, vw), il.Create(OpCodes.Ldc_I4, S), il.Create(OpCodes.Mul),
                            il.Create(OpCodes.Ldloc, vh), il.Create(OpCodes.Ldc_I4, S), il.Create(OpCodes.Mul),
                            il.Create(OpCodes.Ldloc, vx), il.Create(OpCodes.Ldloc, vy),
                            il.Create(OpCodes.Ldloc, vw), il.Create(OpCodes.Ldloc, vh),
                            il.Create(OpCodes.Ldc_I4_0), il.Create(OpCodes.Ldc_I4_0),
                            il.Create(OpCodes.Callvirt, drawImage),
                            il.Create(OpCodes.Br, end),

                            // Hi 화면을 안 쓰는 모드: 게임 자체의 1픽셀 윤곽선
                            lowStart,
                            il.Create(OpCodes.Ldloc, vx), il.Create(OpCodes.Ldloc, vy),
                            il.Create(OpCodes.Ldloc, vw), il.Create(OpCodes.Ldloc, vh),
                            il.Create(OpCodes.Ldloc, vr),
                            il.Create(OpCodes.Ldc_I4, RingColor),
                            il.Create(OpCodes.Call, strokeRound),
                        };

                        // 원래 call 자리는 첫 명령으로 바꿔 끼운다 (같은 Instruction 객체를 재사용)
                        Instruction first = seq[0];
                        ins.OpCode = first.OpCode;
                        ins.Operand = first.Operand;
                        Instruction prev = ins;
                        for (int i = 1; i < seq.Count; i++)
                        {
                            il.InsertAfter(prev, seq[i]);
                            prev = seq[i];
                        }
                    }
                    changed = true;
                }
            }

            // 안내줄 배경 박스(GamePad.DrawGuide 안의 0xA0000000 두 군데)를 더 투명하게
            MethodDefinition drawGuide = pad.Methods.FirstOrDefault(m => m.Name == "DrawGuide" && m.HasBody);
            if (drawGuide != null)
            {
                foreach (Instruction ins in drawGuide.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Ldc_I4 && ins.Operand is int bv && bv == BackdropOld)
                    {
                        ins.Operand = BackdropNew;
                        changed = true;
                    }
                }
            }
            else log("cap hook: GamePad.DrawGuide not found - backdrop unchanged");

            // Y 버튼 글자색(어두운 색)을 흰색(-1)으로
            const int darkText = unchecked((int)0xFF201800);
            foreach (Instruction ins in capText.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldc_I4 && ins.Operand is int v && v == darkText)
                {
                    ins.Operand = -1;
                    changed = true;
                }
            }

            if (changed)
            {
                asm.Write();
                log("cap hook: applied (안내줄 버튼 아이콘 -> 속이 빈 흰 윤곽선)");
            }
            else log("cap hook: already applied");
        }
        catch (Exception ex)
        {
            // 실패해도 게임은 원래 모양 그대로 돌아가야 한다
            log("cap hook FAILED (game keeps the original icons): " + ex);
        }
    }
}
