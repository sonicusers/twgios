using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WogiOS;

// WogShim 에 없는 Windows 전용 호출(P/Invoke)이 게임에 새로 들어오면 DllNotFoundException 으로 멈춘다.
// (v1.0.72: kernel32!SetThreadExecutionState, user32!GetDpiForWindow)
// 예전 방식(2판)은 P/Invoke 선언을 "0 을 돌려주는 일반 메서드"로 고쳐 썼는데, 이 방식에서는 호출 직후 앱이 꺼졌다.
// 이번(3판)에는 선언을 건드리지 않고 "부르는 자리"를 바꾼다:
//     call X(a, b)  ->  pop, pop, ldc.i4.0      (인자를 버리고 0 을 올린다)
// 모든 부르는 자리를 바꾼 뒤에는 쓸모없어진 선언(P/Invoke 또는 예전 판이 만든 빈 메서드)을 지운다.
// 여러 번 실행해도 안전하다(이미 바뀌어 있으면 아무것도 하지 않는다).
internal static class PInvokeStubHook
{
    // 이름이 이것이고 (a) 해당 DLL 의 P/Invoke 이거나 (b) 예전 판이 만든 빈 메서드(ldc.i4.0 ; ret)인 것이 대상
    static readonly (string Dll, string Func)[] Targets =
    {
        ("kernel32.dll", "SetThreadExecutionState"),
        ("user32.dll", "GetDpiForWindow"),
    };

    public static void Apply(string gameDllPath, Action<string> log)
    {
        try
        {
            if (!File.Exists(gameDllPath)) { log("pinvoke stub hook: file missing " + gameDllPath); return; }

            using var asm = AssemblyDefinition.ReadAssembly(gameDllPath, new ReaderParameters { ReadWrite = true });
            ModuleDefinition module = asm.MainModule;

            var victims = module.GetTypes()
                .SelectMany(t => t.Methods)
                .Where(IsTarget)
                .ToList();
            if (victims.Count == 0) { log("pinvoke stub hook: nothing to do"); return; }

            // 1) 부르는 자리를 바꾼다
            int sites = 0;
            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (MethodDefinition m in type.Methods.Where(x => x.HasBody))
                {
                    var il = m.Body.GetILProcessor();
                    foreach (Instruction ins in m.Body.Instructions.ToList())
                    {
                        if (ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt) continue;
                        if (ins.Operand is not MethodReference mr) continue;
                        MethodDefinition target = victims.FirstOrDefault(v => v.FullName == mr.FullName);
                        if (target == null) continue;

                        // 호출 자리 하나를 [pop × 인자 수, ldc 0] 으로 바꾼다 (분기 대상이 이 자리를 가리켜도 유지되도록 첫 명령을 제자리에서 바꾼다)
                        int n = target.Parameters.Count;
                        var seq = new System.Collections.Generic.List<Instruction>();
                        for (int i = 0; i < n; i++) seq.Add(il.Create(OpCodes.Pop));
                        string rt = target.ReturnType.FullName;
                        if (rt != "System.Void") seq.Add(ZeroOf(il, rt, out Instruction conv) ?? il.Create(OpCodes.Ldc_I4_0));
                        if (seq.Count == 0) seq.Add(il.Create(OpCodes.Nop));

                        ins.OpCode = seq[0].OpCode; ins.Operand = seq[0].Operand;
                        Instruction prev = ins;
                        for (int i = 1; i < seq.Count; i++) { il.InsertAfter(prev, seq[i]); prev = seq[i]; }
                        sites++;
                    }
                }
            }

            // 2) 이제 아무도 부르지 않는 선언을 지운다 (남아 있으면 안 되는 호출이 있으면 지우지 않고 둔다)
            int removed = 0;
            foreach (MethodDefinition v in victims)
            {
                bool stillUsed = module.GetTypes().SelectMany(t => t.Methods).Where(x => x.HasBody)
                    .Any(x => x.Body.Instructions.Any(i => i.Operand is MethodReference r && r.FullName == v.FullName));
                if (stillUsed) { log("pinvoke stub hook: still referenced, kept " + v.FullName); continue; }
                v.DeclaringType.Methods.Remove(v);
                removed++;
                log("pinvoke stub hook: " + v.DeclaringType.FullName + "." + v.Name + " 호출 자리 교체 + 선언 제거");
            }

            asm.Write();
            log("pinvoke stub hook: applied (call sites " + sites + ", removed " + removed + ")");
        }
        catch (Exception ex)
        {
            log("pinvoke stub hook FAILED (game stays unpatched here): " + ex);
        }
    }

    static bool IsTarget(MethodDefinition m)
    {
        if (m.IsPInvokeImpl && m.PInvokeInfo != null)
        {
            string dll = m.PInvokeInfo.Module.Name ?? "";
            string func = m.PInvokeInfo.EntryPoint ?? m.Name;
            return Targets.Any(t => dll.Equals(t.Dll, StringComparison.OrdinalIgnoreCase) && func == t.Func);
        }
        // 예전 판이 만든 빈 메서드
        if (!Targets.Any(t => t.Func == m.Name) || !m.HasBody) return false;
        var ins = m.Body.Instructions;
        return ins.Count is >= 1 and <= 3 && ins[^1].OpCode == OpCodes.Ret
               && ins.Any(i => i.OpCode == OpCodes.Ldc_I4_0 || i.OpCode == OpCodes.Ldc_I8);
    }

    static Instruction ZeroOf(ILProcessor il, string returnType, out Instruction conv)
    {
        conv = null;
        if (returnType == "System.Int64" || returnType == "System.UInt64") return il.Create(OpCodes.Ldc_I8, 0L);
        return il.Create(OpCodes.Ldc_I4_0);   // uint / int / bool / byte 등 (IntPtr 반환은 이 대상에 없다)
    }
}
