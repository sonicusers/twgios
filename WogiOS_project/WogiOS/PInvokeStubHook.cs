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

    static bool SameMethod(MethodReference r, MethodDefinition v)
    {
        if (r == null) return false;
        if (r is MethodDefinition d) return ReferenceEquals(d, v);
        return r.Name == v.Name && r.DeclaringType != null && r.DeclaringType.FullName == v.DeclaringType.FullName;
    }

    public static void Apply(string gameDllPath, Action<string> log)
    {
        string stage = "start";
        try
        {
            if (!File.Exists(gameDllPath)) { log("pinvoke stub hook: file missing " + gameDllPath); return; }

            stage = "read";
            using var asm = AssemblyDefinition.ReadAssembly(gameDllPath, new ReaderParameters { ReadWrite = true });
            ModuleDefinition module = asm.MainModule;

            stage = "find victims";
            var victims = new System.Collections.Generic.List<MethodDefinition>();
            foreach (TypeDefinition type in module.GetTypes())
                foreach (MethodDefinition m in type.Methods)
                {
                    bool hit;
                    try { hit = IsTarget(m); }
                    catch (Exception ex) { log("pinvoke stub hook: IsTarget 실패 " + type.FullName + "." + m.Name + " : " + ex.GetType().Name); hit = false; }
                    if (hit) victims.Add(m);
                }
            log("pinvoke stub hook: victims " + victims.Count);
            if (victims.Count == 0) { log("pinvoke stub hook: nothing to do"); return; }

            // 1) 부르는 자리를 바꾼다
            stage = "rewrite call sites";
            int sites = 0;
            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (MethodDefinition m in type.Methods)
                {
                    if (!m.HasBody || victims.Contains(m)) continue;
                    var body = m.Body;
                    var il = body.GetILProcessor();
                    foreach (Instruction ins in body.Instructions.ToList())
                    {
                        if (ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt) continue;
                        if (ins.Operand is not MethodReference mr) continue;
                        MethodDefinition target = null;
                        foreach (MethodDefinition v in victims) if (SameMethod(mr, v)) { target = v; break; }
                        if (target == null) continue;

                        int n = target.Parameters.Count;
                        var seq = new System.Collections.Generic.List<Instruction>();
                        for (int i = 0; i < n; i++) seq.Add(il.Create(OpCodes.Pop));
                        string rt = target.ReturnType?.FullName ?? "System.Void";
                        if (rt != "System.Void") seq.Add(rt == "System.Int64" || rt == "System.UInt64" ? il.Create(OpCodes.Ldc_I8, 0L) : il.Create(OpCodes.Ldc_I4_0));
                        if (seq.Count == 0) seq.Add(il.Create(OpCodes.Nop));

                        ins.OpCode = seq[0].OpCode; ins.Operand = seq[0].Operand;
                        Instruction prev = ins;
                        for (int i = 1; i < seq.Count; i++) { il.InsertAfter(prev, seq[i]); prev = seq[i]; }
                        sites++;
                    }
                }
            }
            log("pinvoke stub hook: call sites " + sites);

            // 2) 선언은 지우지 않는다 — 부르는 자리가 모두 바뀌었으므로 아무도 부르지 않아 그대로 두어도 안전하고,
            //    지우는 과정에서 오류가 나던(NullReferenceException) 부분을 없앤다.
            int removed = 0;

            stage = "write";
            asm.Write();
            log("pinvoke stub hook: applied (call sites " + sites + ")");
        }
        catch (Exception ex)
        {
            log("pinvoke stub hook FAILED at stage '" + stage + "' (game stays unpatched here): " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    static bool IsTarget(MethodDefinition m)
    {
        if (m.IsPInvokeImpl)
        {
            // 예전 판(2판)이 만든 망가진 메서드: P/Invoke 표시는 남았는데 어느 DLL 인지 적힌 곳(ImplMap)만 지워진 것.
            // 이것을 부르면 앱이 아무 오류 창도 없이 꺼진다(원인). 이름만 보고 대상으로 잡는다.
            if (m.PInvokeInfo == null) return Targets.Any(t => t.Func == m.Name);
            string dll = m.PInvokeInfo.Module?.Name ?? "";
            string func = m.PInvokeInfo.EntryPoint ?? m.Name;
            return Targets.Any(t => dll.Equals(t.Dll, StringComparison.OrdinalIgnoreCase) && func == t.Func);
        }
        // 예전 판이 만든 빈 메서드
        if (!Targets.Any(t => t.Func == m.Name) || !m.HasBody) return false;
        var ins = m.Body.Instructions;
        return ins.Count is >= 1 and <= 4 && ins[ins.Count - 1].OpCode == OpCodes.Ret;
    }
}
