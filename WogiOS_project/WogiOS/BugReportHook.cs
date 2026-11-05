using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WogiOS;

// WogShim 에는 RichTextBox 가 없다. 그런데 게임의 DuelDx.MyBugReportsDialog(「내 리포트」 창)가
// RichTextBox 를 쓰고, Program.Main() 이 맨 앞에서 MyBugReportsDialog.ShotIfAsked()(개발자용 화면 밖 시험 훅)를
// 부르는 바람에 시작하자마자 TypeLoadException 으로 죽는다.
//
// 이 훅은 패치된 WarOfGenesis.dll 에서 그 클래스를 무해하게 만든다.
//   ShotIfAsked()  -> return false
//   Show(owner)    -> 아무것도 안 함 (「내 리포트」 단추는 iOS 에서 반응 없음)
//   RichTextBox 가 걸린 나머지 메서드 · 중첩 클래스(Build, Fill, 클로저 등) -> 제거
// 여러 번 실행해도 안전하다(이미 바뀌어 있으면 아무것도 하지 않는다).
internal static class BugReportHook
{
    static readonly string[] MissingInShim = { "RichTextBox", "RichTextBoxScrollBars" };

    public static void Apply(string gameDllPath, Action<string> log)
    {
        try
        {
            if (!File.Exists(gameDllPath)) { log("bugreport hook: file missing " + gameDllPath); return; }

            using var asm = AssemblyDefinition.ReadAssembly(gameDllPath, new ReaderParameters { ReadWrite = true });
            TypeDefinition type = asm.MainModule.GetTypes()
                .FirstOrDefault(t => t.DeclaringType == null && t.Name == "MyBugReportsDialog");
            if (type == null) { log("bugreport hook: MyBugReportsDialog not found (game version changed?) - skipped"); return; }

            bool changed = false;

            // 1) 진입점 두 개는 몸통만 비운다 (호출하는 쪽은 그대로 두려고 지우지 않는다)
            foreach (MethodDefinition m in type.Methods.Where(x => x.HasBody))
            {
                if (m.Name == "ShotIfAsked" && !IsTrivial(m, returnsBool: true)) { Stub(m, returnsBool: true); changed = true; }
                else if (m.Name == "Show" && !IsTrivial(m, returnsBool: false)) { Stub(m, returnsBool: false); changed = true; }
            }

            // 2) RichTextBox 가 서명이나 몸통에 걸린 나머지 메서드는 지운다 (이제 아무도 부르지 않는다)
            foreach (MethodDefinition m in type.Methods.ToList())
            {
                if (m.Name == "ShotIfAsked" || m.Name == "Show") continue;
                if (!Touches(m)) continue;
                type.Methods.Remove(m);
                changed = true;
            }

            // 3) 컴파일러가 만든 중첩 클래스(클로저 · async 상태 기계) 중 RichTextBox 가 걸린 것도 지운다
            foreach (TypeDefinition nested in type.NestedTypes.ToList())
            {
                if (!NestedTouches(nested)) continue;
                type.NestedTypes.Remove(nested);
                changed = true;
            }

            if (changed)
            {
                asm.Write();
                log("bugreport hook: applied (MyBugReportsDialog -> RichTextBox 의존 제거)");
            }
            else log("bugreport hook: already applied");
        }
        catch (Exception ex)
        {
            // 실패해도 원래 상태 그대로 둔다
            log("bugreport hook FAILED (game stays unpatched here): " + ex);
        }
    }

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
