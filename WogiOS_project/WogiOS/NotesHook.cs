using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WogiOS;

// 패치된 WarOfGenesis.dll 의 ReleaseNotes.All(string text) 맨 앞에
//     text = WogiOS.NotesTrim.Head(text);
// 를 끼워 넣는다. (개발 > 패치 내역이 노트 전체를 줄바꿈하다 멈추는 문제)
// 여러 번 실행해도 안전하다(이미 끼워져 있으면 아무것도 하지 않는다).
internal static class NotesHook
{
    public static void Apply(string gameDllPath, Action<string> log)
    {
        try
        {
            if (!File.Exists(gameDllPath)) { log("notes hook: file missing " + gameDllPath); return; }

            using var asm = AssemblyDefinition.ReadAssembly(gameDllPath, new ReaderParameters { ReadWrite = true });
            ModuleDefinition module = asm.MainModule;

            TypeDefinition type = module.GetTypes().FirstOrDefault(t => t.Name == "ReleaseNotes" && t.DeclaringType == null);
            MethodDefinition all = type?.Methods.FirstOrDefault(m => m.Name == "All" && m.IsStatic && m.HasBody
                && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.String");
            if (all == null) { log("notes hook: ReleaseNotes.All not found (game version changed?) - skipped"); return; }

            if (all.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.DeclaringType != null && mr.DeclaringType.Name == "NotesTrim"))
            { log("notes hook: already applied"); return; }

            var self = typeof(NotesHook).Assembly.GetName();
            AssemblyNameReference hostRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == self.Name);
            if (hostRef == null)
            {
                hostRef = new AssemblyNameReference(self.Name, self.Version);
                module.AssemblyReferences.Add(hostRef);
            }

            TypeSystem ts = module.TypeSystem;
            var trimType = new TypeReference("WogiOS", "NotesTrim", module, hostRef);
            var head = new MethodReference("Head", ts.String, trimType) { HasThis = false };
            head.Parameters.Add(new ParameterDefinition(ts.String));

            ILProcessor il = all.Body.GetILProcessor();
            Instruction first = all.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Call, head));
            il.InsertBefore(first, il.Create(OpCodes.Starg, all.Parameters[0]));

            asm.Write();
            log("notes hook: applied (패치 내역 분량 제한)");
        }
        catch (Exception ex)
        {
            log("notes hook FAILED (game stays unpatched here): " + ex);
        }
    }
}
