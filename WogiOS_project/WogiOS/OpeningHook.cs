using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WogiOS;

// 게임을 켤 때 오프닝(0008.wgm) 앞에 소프트맥스 로고 영상(0032.wgm)을 먼저 튼다.
//   CutscenePlayer.StartMovie(int movie) 맨 앞에
//     if (movie == 8) StartMovie(32);
//   를 끼워 넣는다. 로고가 먼저 큐에 들어가고, 이어서 원래 코드가 오프닝(8)을 큐에 넣는다.
//   0032.wgm 이 없으면 StartMovie(32) 가 false 만 돌려주고 아무것도 안 하므로 전처럼 오프닝만 나온다.
//   영상 파일 위치(파일 앱): WogiOS/save/assets/effects/cut/0032.wgm, 0008.wgm
// 여러 번 실행해도 안전하다(이미 끼워져 있으면 아무것도 하지 않는다).
internal static class OpeningHook
{
    const int OpeningMovie = 8;
    const int LogoMovie = 32;

    public static void Apply(string gameDllPath, Action<string> log)
    {
        try
        {
            if (!File.Exists(gameDllPath)) { log("opening hook: file missing " + gameDllPath); return; }

            using var asm = AssemblyDefinition.ReadAssembly(gameDllPath, new ReaderParameters { ReadWrite = true });
            ModuleDefinition module = asm.MainModule;

            TypeDefinition player = module.GetTypes()
                .FirstOrDefault(t => t.Name == "CutscenePlayer" && t.Methods.Any(m => m.Name == "StartMovie"));
            MethodDefinition start = player?.Methods.FirstOrDefault(m =>
                m.Name == "StartMovie" && m.HasBody && !m.IsStatic && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32);
            if (start == null)
            {
                log("opening hook: CutscenePlayer.StartMovie not found (game version changed?) - skipped");
                return;
            }

            MethodBody body = start.Body;
            bool already = body.Instructions.Any(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference mr && mr.Name == "StartMovie");
            if (already) { log("opening hook: already applied"); return; }

            ILProcessor il = body.GetILProcessor();
            Instruction first = body.Instructions[0];

            // if (movie != 8) goto 원래 코드;
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, OpeningMovie));
            il.InsertBefore(first, il.Create(OpCodes.Bne_Un, first));
            // this.StartMovie(32);  (결과는 버린다)
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, LogoMovie));
            il.InsertBefore(first, il.Create(OpCodes.Call, start));
            il.InsertBefore(first, il.Create(OpCodes.Pop));

            asm.Write();
            log("opening hook: applied (오프닝 앞에 로고 영상 0032 추가)");
        }
        catch (Exception ex)
        {
            // 실패해도 게임은 원래대로(오프닝만) 돌아가야 한다
            log("opening hook FAILED (game keeps the original opening): " + ex);
        }
    }
}
