using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WogiOS;

// WogShim 에는 버그 리포트 창에 쓰는 WinForms 부품(RichTextBox · TableLayoutPanel · TextBox · Button …)이 없다.
// 그대로 두면 아래 두 곳에서 TypeLoadException 으로 게임이 멈춘다. 패치된 WarOfGenesis.dll 에서 둘 다 손본다.
//
// 1) DuelDx.MyBugReportsDialog (「내 리포트」 창, RichTextBox)
//      ShotIfAsked() -> return false      (Program.Main 맨 앞에서 불리는 개발자용 시험 훅 — 시작하자마자 죽던 원인)
//      Show(owner)   -> 아무것도 안 함
//      RichTextBox 가 걸린 나머지 메서드 · 중첩 클래스 -> 제거
//      (「내 리포트」는 iOS 판 창 BugReportBridge 가 따로 그린다)
// 2) DuelDx.BugReport.Open() (타이틀 「버그 리포트」 단추 · 시스템 메뉴 · F12 가 모두 여기로 온다)
//      -> 원래의 WinForms 창 대신 앱 본체의 iOS 창(WogiOS.BugReportBridge.Ask)을 띄우고,
//         쓴 글은 게임의 Submit() 으로 그대로 보낸다.
//         ( var ctx = Context(); BugReportBridge.AddDevice(ctx); string text = BugReportBridge.Ask(Summary(ctx)); if (text != null) Submit(text, ctx); )
//         게임에서 Context / Summary / Submit 을 못 찾으면(게임 판이 바뀐 경우) 예전처럼 안내 문구(토스트)만 띄운다.
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
            changed |= PatchDialog(module, log);

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

        // 이미 (기기 정보까지 넣는) 최신 iOS 창으로 바꿨으면 그대로
        if (open.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.DeclaringType != null && mr.DeclaringType.Name == "BugReportBridge" && mr.Name == "AddDevice")) return false;

        MethodDefinition context = br.Methods.FirstOrDefault(m => m.Name == "Context" && !m.IsStatic && m.Parameters.Count == 0 && m.HasBody);
        MethodDefinition summary = br.Methods.FirstOrDefault(m => m.Name == "Summary" && m.IsStatic && m.Parameters.Count == 1 && m.HasBody);
        MethodDefinition submit = br.Methods.FirstOrDefault(m => m.Name == "Submit" && !m.IsStatic && m.Parameters.Count == 2
            && m.Parameters[0].ParameterType.FullName == "System.String" && m.HasBody);

        if (context != null && summary != null && submit != null)
        {
            // 앱 본체 어셈블리를 참조로 추가 (BinkHook 과 같은 길)
            var self = typeof(BugReportHook).Assembly.GetName();
            AssemblyNameReference hostRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == self.Name);
            if (hostRef == null)
            {
                hostRef = new AssemblyNameReference(self.Name, self.Version);
                module.AssemblyReferences.Add(hostRef);
            }
            TypeSystem ts = module.TypeSystem;
            var bridgeType = new TypeReference("WogiOS", "BugReportBridge", module, hostRef);
            var ask = new MethodReference("Ask", ts.String, bridgeType) { HasThis = false };
            ask.Parameters.Add(new ParameterDefinition(ts.String));   // summary
            var addDevice = new MethodReference("AddDevice", ts.Void, bridgeType) { HasThis = false };
            addDevice.Parameters.Add(new ParameterDefinition(context.ReturnType));   // Dictionary<string, object>

            MethodBody body = open.Body;
            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            body.InitLocals = true;
            var ctxVar = new VariableDefinition(context.ReturnType);
            var textVar = new VariableDefinition(ts.String);
            body.Variables.Add(ctxVar);
            body.Variables.Add(textVar);

            ILProcessor il = body.GetILProcessor();
            Instruction end = il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, context); il.Emit(OpCodes.Stloc, ctxVar);   // ctx = Context()
            il.Emit(OpCodes.Ldloc, ctxVar); il.Emit(OpCodes.Call, addDevice);                             // 기기 정보로 os 값 교체
            il.Emit(OpCodes.Ldloc, ctxVar); il.Emit(OpCodes.Call, summary);                               // Summary(ctx)
            il.Emit(OpCodes.Call, ask); il.Emit(OpCodes.Stloc, textVar);                                  // text = BugReportBridge.Ask(...)
            il.Emit(OpCodes.Ldloc, textVar); il.Emit(OpCodes.Brfalse, end);                               // 취소했으면 끝
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, textVar); il.Emit(OpCodes.Ldloc, ctxVar);
            il.Emit(OpCodes.Call, submit);                                                                // Submit(text, ctx)
            il.Append(end);
            log("bugreport hook: BugReport.Open -> iOS dialog");
            return true;
        }

        // ---- 예비: 필요한 메서드를 못 찾으면 예전처럼 안내 문구만 ----
        if (open.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (i.Operand as string) == OpenNotice)) return false;

        // 원래 몸통이 부르던 host.Toast(string) 참조와 host 필드를 재사용한다 (몸통을 비우기 전에 먼저 찾아 둔다)
        Instruction toastCall = open.Body.Instructions.FirstOrDefault(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference mr && mr.Name == "Toast");
        FieldDefinition hostField = br.Fields.FirstOrDefault(f => f.Name == "<host>P");
        OpCode toastOp = toastCall?.OpCode ?? OpCodes.Callvirt;
        var toastRef = toastCall?.Operand as MethodReference;

        MethodBody fallback = open.Body;
        fallback.Instructions.Clear();
        fallback.ExceptionHandlers.Clear();
        fallback.Variables.Clear();
        ILProcessor fil = fallback.GetILProcessor();
        if (hostField != null && toastRef != null)
        {
            fil.Emit(OpCodes.Ldarg_0);
            fil.Emit(OpCodes.Ldfld, hostField);
            fil.Emit(OpCodes.Ldstr, OpenNotice);
            fil.Emit(toastOp, toastRef);
        }
        else log("bugreport hook: host/Toast not found - Open() just returns");
        fil.Emit(OpCodes.Ret);
        log("bugreport hook: Context/Summary/Submit not found (game version changed?) - Open() only shows a notice");
        return true;
    }

    // ---- 3) BugReportDialog (1.0.88 부터 WinForms 창 본체가 별도 클래스) ----------------------------
    // Open() 을 iOS 창으로 바꾼 뒤에는 아무도 부르지 않는다. TableLayoutPanel · TextBox 등
    // WogShim 에 없는 부품이 통째로 이 클래스 안에 있으므로 클래스째 지운다.
    // 다른 곳에서 아직 참조하고 있으면(게임 판이 바뀐 경우) 건드리지 않는다.
    static bool PatchDialog(ModuleDefinition module, Action<string> log)
    {
        TypeDefinition dlg = module.Types.FirstOrDefault(t => t.Name == "BugReportDialog");
        if (dlg == null) return false;

        foreach (TypeDefinition t in module.GetTypes())
        {
            if (t == dlg || t.DeclaringType == dlg) continue;
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (Instruction i in m.Body.Instructions)
                {
                    string owner = i.Operand switch
                    {
                        MethodReference mr => mr.DeclaringType?.FullName,
                        FieldReference fr => fr.DeclaringType?.FullName,
                        TypeReference tr => tr.FullName,
                        _ => null,
                    };
                    if (owner != null && owner == dlg.FullName)
                    {
                        log("bugreport hook: BugReportDialog still referenced by " + t.FullName + "." + m.Name + " - kept");
                        return false;
                    }
                }
            }
        }
        module.Types.Remove(dlg);
        log("bugreport hook: BugReportDialog removed");
        return true;
    }

    // ---- 검사용: 훅을 적용한 뒤에도 게임 DLL 이 참조하는 "빠진 부품" 찾기 -------------------------------
    // missing = WogPatcher 가 돌려준 빠진 항목 문자열들 (type X / member Y). 남아 있는 것만 돌려준다.
    public static List<string> ResidualMissing(string gameDllPath, IEnumerable<string> missing)
    {
        var types = new HashSet<string>();
        var members = new HashSet<string>();
        foreach (string line in missing)
        {
            int k = line.IndexOf(": type ", StringComparison.Ordinal);
            if (k >= 0) { types.Add(line.Substring(k + 7).Trim()); continue; }
            k = line.IndexOf(": member ", StringComparison.Ordinal);
            if (k >= 0) members.Add(line.Substring(k + 9).Trim());
        }
        var found = new List<string>();
        using var asm = AssemblyDefinition.ReadAssembly(gameDllPath);
        void Check(TypeReference tr, string where)
        {
            while (tr is TypeSpecification spec) tr = spec.ElementType;
            if (tr != null && types.Contains(tr.FullName)) found.Add(where + " -> type " + tr.FullName);
        }
        foreach (TypeDefinition t in asm.MainModule.GetTypes())
        {
            foreach (FieldDefinition f in t.Fields) Check(f.FieldType, t.FullName);
            foreach (MethodDefinition m in t.Methods)
            {
                string where = t.FullName + "." + m.Name;
                Check(m.ReturnType, where);
                foreach (ParameterDefinition p in m.Parameters) Check(p.ParameterType, where);
                if (!m.HasBody) continue;
                foreach (VariableDefinition v in m.Body.Variables) Check(v.VariableType, where);
                foreach (Instruction i in m.Body.Instructions)
                {
                    if (i.Operand is MethodReference mr)
                    {
                        if (members.Contains(mr.FullName)) found.Add(where + " -> member " + mr.FullName);
                        Check(mr.DeclaringType, where);
                    }
                    else if (i.Operand is FieldReference fr) Check(fr.DeclaringType, where);
                    else if (i.Operand is TypeReference tr2) Check(tr2, where);
                }
            }
        }
        return found;
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
