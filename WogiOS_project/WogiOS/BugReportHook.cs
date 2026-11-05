using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WogiOS;

// WogShim 에는 버그 리포트 창에 쓰는 WinForms 부품(RichTextBox · TableLayoutPanel · TextBox · Button …)이 없다.
// 그대로 두면 아래 두 곳에서 TypeLoadException 으로 게임이 멈춘다. 패치된 WarOfGenesis.dll 에서 둘 다 막는다.
//
// 1) DuelDx.MyBugReportsDialog (「내 리포트」 창, RichTextBox)
//      ShotIfAsked() -> return false      (Program.Main 맨 앞에서 불리는 개발자용 시험 훅 — 시작하자마자 죽던 원인)
//      Show(owner)   -> 아무것도 안 함
//      RichTextBox 가 걸린 나머지 메서드 · 중첩 클래스 -> 제거
// 2) DuelDx.BugReport.Open() (타이틀 「버그 리포트」 단추 · 시스템 메뉴 · F12 가 모두 여기로 온다)
//      -> 창 대신 화면에 안내 문구(토스트)만 띄운다
//
// 여러 번 실행해도 안전하다(이미 바뀌어 있으면 아무것도 하지 않는다).
internal static class BugReportHook
{
    static readonly string[] MissingInShim = { "RichTextBox", "RichTextBoxScrollBars" };
    const string OpenNotice = "iOS 판에서는 버그 리포트 창을 지원하지 않습니다";

    public static void Apply(string gameDllPath, Action<string> log)
    {
        try
        {
            if (!File.Exists(gameDllPath)) { log("bugreport hook: file missing " + gameDllPath); return; }

            using var asm = AssemblyDefinition.ReadAssembly(gameDllPath, new ReaderParameters { ReadWrite = true });
            ModuleDefinition module = asm.MainModule;

            bool changed = PatchMyBugReports(module, log);
            changed |= PatchOpen(module, log);

            if (changed)
            {
                asm.Write();
                log("bugreport hook: applied");
            }
            else log("bugreport hook: already applied");
        }
        catch (Exception ex)
        {
            // 실패해도 원래 상태 그대로 둔다
            log("bugreport hook FAILED (game stays unpatched here): " + ex);
        }
    }

    // ---- 1) MyBugReportsDialog ---------------------------------------------------------------

    static bool PatchMyBugReports(ModuleDefinition module, Action<string> log)
    {
        TypeDefinition type = module.GetTypes()
            .FirstOrDefault(t => t.DeclaringType == null && t.Name == "MyBugReportsDialog");
        if (type == null) { log("bugreport hook: MyBugReportsDialog not found (game version changed?) - skipped"); return false; }

        bool changed = false;

        // 진입점 두 개는 몸통만 비운다 (호출하는 쪽은 그대로 두려고 지우지 않는다)
        foreach (MethodDefinition m in type.Methods.Where(x => x.HasBody))
        {
            if (m.Name == "ShotIfAsked" && !IsTrivial(m, returnsBool: true)) { Stub(m, returnsBool: true); changed = true; }
            else if (m.Name == "Show" && !IsTrivial(m, returnsBool: false)) { Stub(m, returnsBool: false); changed = true; }
        }

        // RichTextBox 가 서명이나 몸통에 걸린 나머지 메서드는 지운다 (이제 아무도 부르지 않는다)
        foreach (MethodDefinition m in type.Methods.ToList())
        {
            if (m.Name == "ShotIfAsked" || m.Name == "Show") continue;
            if (!Touches(m)) continue;
            type.Methods.Remove(m);
            changed = true;
        }

        // 컴파일러가 만든 중첩 클래스(클로저 · async 상태 기계) 중 RichTextBox 가 걸린 것도 지운다
        foreach (TypeDefinition nested in type.NestedTypes.ToList())
        {
            if (!NestedTouches(nested)) continue;
            type.NestedTypes.Remove(nested);
            changed = true;
        }
        return changed;
    }

    // ---- 2) BugReport.Open -------------------------------------------------------------------

    static bool PatchOpen(ModuleDefinition module, Action<string> log)
    {
        TypeDefinition br = module.GetTypes().FirstOrDefault(t => t.DeclaringType == null && t.Name == "BugReport");
        MethodDefinition open = br?.Methods.FirstOrDefault(m => m.Name == "Open" && !m.IsStatic && m.Parameters.Count == 0 && m.HasBody);
        if (open == null) { log("bugreport hook: BugReport.Open not found (game version changed?) - skipped"); return false; }

        // 이미 바꿨으면 그대로
        if (open.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (i.Operand as string) == OpenNotice)) return false;

        // 원래 몸통이 부르던 host.Toast(string) 참조와 host 필드를 재사용한다 (몸통을 비우기 전에 먼저 찾아 둔다)
        Instruction toastCall = open.Body.Instructions.FirstOrDefault(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference mr && mr.Name == "Toast");
        FieldDefinition hostField = br.Fields.FirstOrDefault(f => f.Name == "<host>P");
        OpCode toastOp = toastCall?.OpCode ?? OpCodes.Callvirt;
        var toastRef = toastCall?.Operand as MethodReference;

        MethodBody body = open.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        ILProcessor il = body.GetILProcessor();
        if (hostField != null && toastRef != null)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, hostField);
            il.Emit(OpCodes.Ldstr, OpenNotice);
            il.Emit(toastOp, toastRef);
        }
        else log("bugreport hook: host/Toast not found - Open() just returns");
        il.Emit(OpCodes.Ret);
        return true;
    }

    // ---- 도우미 -------------------------------------------------------------------------------

    static bool IsMissing(TypeReference t)
    {
        while (t is TypeSpecification spec) t = spec.ElementType;   // out RichTextBox&, RichTextBox[] 등
        return t != null && MissingInShim.Contains(t.Name);
    }

    static bool Touches(MethodDefinition m)
    {
        if (IsMissing(m.ReturnType) || m.Parameters.Any(p => IsMissing(p.ParameterType))) return true;
        if (!m.HasBody) return false;
        if (m.Body.Variables.Any(v => IsMissing(v.VariableType))) return true;
        return m.Body.Instructions.Any(i => i.Operand switch
        {
            TypeReference tr => IsMissing(tr),
            MethodReference mr => IsMissing(mr.DeclaringType) || IsMissing(mr.ReturnType) || mr.Parameters.Any(p => IsMissing(p.ParameterType)),
            FieldReference fr => IsMissing(fr.DeclaringType) || IsMissing(fr.FieldType),
            _ => false,
        });
    }

    static bool NestedTouches(TypeDefinition t) =>
        t.Fields.Any(f => IsMissing(f.FieldType))
        || t.Methods.Any(Touches)
        || t.NestedTypes.Any(NestedTouches);

    static bool IsTrivial(MethodDefinition m, bool returnsBool)
    {
        var ins = m.Body.Instructions;
        return returnsBool
            ? ins.Count == 2 && ins[0].OpCode == OpCodes.Ldc_I4_0 && ins[1].OpCode == OpCodes.Ret
            : ins.Count == 1 && ins[0].OpCode == OpCodes.Ret;
    }

    static void Stub(MethodDefinition m, bool returnsBool)
    {
        MethodBody body = m.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        ILProcessor il = body.GetILProcessor();
        if (returnsBool) il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }
}
