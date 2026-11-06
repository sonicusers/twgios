using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WogiOS;

// WogShim 에 없는 Windows 전용 호출(P/Invoke)이 게임에 새로 들어오면 DllNotFoundException 으로 멈춘다.
// (v1.0.72: kernel32!SetThreadExecutionState, user32!GetDpiForWindow)
// iOS 에서는 의미가 없는 호출이므로, 패치된 WarOfGenesis.dll 안에서 해당 메서드를 "0 을 돌려주는 일반 메서드"로 바꾼다.
// 앞으로 비슷한 호출이 생기면 아래 Targets 에 한 줄만 더하면 된다.
// 여러 번 실행해도 안전하다(이미 바뀌어 있으면 아무것도 하지 않는다).
internal static class PInvokeStubHook
{
    // (DLL 이름, 함수 이름) — 반환 값은 모두 0 (uint / int / bool 어느 것이든 0)
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
            int count = 0;
            foreach (TypeDefinition type in asm.MainModule.GetTypes())
            {
                foreach (MethodDefinition m in type.Methods.Where(x => x.IsPInvokeImpl && x.PInvokeInfo != null))
                {
                    string dll = m.PInvokeInfo.Module.Name ?? "";
                    string func = m.PInvokeInfo.EntryPoint ?? m.Name;
                    if (!Targets.Any(t => dll.Equals(t.Dll, StringComparison.OrdinalIgnoreCase)
                                          && func.Equals(t.Func, StringComparison.Ordinal))) continue;
                    Stub(m);
                    count++;
                    log("pinvoke stub hook: " + type.FullName + "." + m.Name + " -> 0");
                }
            }
            if (count > 0) { asm.Write(); log("pinvoke stub hook: applied (" + count + ")"); }
            else log("pinvoke stub hook: nothing to do");
        }
        catch (Exception ex)
        {
            log("pinvoke stub hook FAILED (game stays unpatched here): " + ex);
        }
    }

    static void Stub(MethodDefinition m)
    {
        m.IsPInvokeImpl = false;
        m.PInvokeInfo = null;
        m.IsPreserveSig = false;
        m.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;

        m.Body = new MethodBody(m);
        ILProcessor il = m.Body.GetILProcessor();
        string rt = m.ReturnType.FullName;
        if (rt != "System.Void")
        {
            if (rt == "System.Int64" || rt == "System.UInt64") il.Emit(OpCodes.Ldc_I8, 0L);
            else if (rt == "System.IntPtr" || rt == "System.UIntPtr") { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_I); }
            else il.Emit(OpCodes.Ldc_I4_0);
        }
        il.Emit(OpCodes.Ret);
    }
}
