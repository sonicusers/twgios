using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WogiOS;

// 패치된 WarOfGenesis.Assets.dll 의 BinkAudio.Decode() 를 앱 본체의 FastBink.Decode() 로 연결한다.
//   원래 Decode()  ->  DecodeSlow() 로 이름만 바꿔 남겨 둠
//   새 Decode()    ->  FastBink.Decode(...) 호출, null 이 돌아오면(실패하면) DecodeSlow() 호출
// 여러 번 실행해도 안전하다(이미 연결돼 있으면 아무것도 하지 않는다).
internal static class BinkHook
{
    public static void Apply(string assetsDllPath, Action<string> log)
    {
        try
        {
            if (!File.Exists(assetsDllPath)) { log("bink hook: file missing " + assetsDllPath); return; }

            using var asm = AssemblyDefinition.ReadAssembly(assetsDllPath, new ReaderParameters { ReadWrite = true });
            ModuleDefinition module = asm.MainModule;

            TypeDefinition bink = module.GetType("WarOfGenesis.Assets.BinkAudio");
            TypeDefinition info = module.GetType("WarOfGenesis.Assets.BinkInfo");
            TypeDefinition pcm = module.GetType("WarOfGenesis.Assets.PcmSound");
            if (bink == null || info == null || pcm == null) { log("bink hook: types not found"); return; }

            if (bink.Methods.Any(m => m.Name == "DecodeSlow")) { log("bink hook: already applied"); return; }

            MethodDefinition decode = bink.Methods.FirstOrDefault(m => m.Name == "Decode" && !m.IsStatic && !m.HasParameters);
            MethodDefinition getInfo = bink.Methods.FirstOrDefault(m => m.Name == "get_Info");
            FieldDefinition fData = bink.Fields.FirstOrDefault(f => f.Name == "_data");
            FieldDefinition fFramePos = bink.Fields.FirstOrDefault(f => f.Name == "_framePos");
            FieldDefinition fTracks = bink.Fields.FirstOrDefault(f => f.Name == "_audioTracks");
            MethodDefinition gRate = info.Methods.FirstOrDefault(m => m.Name == "get_SampleRate");
            MethodDefinition gCh = info.Methods.FirstOrDefault(m => m.Name == "get_Channels");
            MethodDefinition gFrames = info.Methods.FirstOrDefault(m => m.Name == "get_FrameCount");
            MethodDefinition gDct = info.Methods.FirstOrDefault(m => m.Name == "get_UseDct");
            MethodDefinition pcmCtor = pcm.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic
                && m.Parameters.Count == 3 && m.Parameters[0].ParameterType.IsArray);

            if (decode == null || getInfo == null || fData == null || fFramePos == null || fTracks == null
                || gRate == null || gCh == null || gFrames == null || gDct == null || pcmCtor == null)
            {
                log("bink hook: expected members not found (game version changed?) - skipped");
                return;
            }

            // 앱 본체 어셈블리를 참조로 추가
            var self = typeof(BinkHook).Assembly.GetName();
            var hostRef = new AssemblyNameReference(self.Name, self.Version);
            module.AssemblyReferences.Add(hostRef);

            TypeSystem ts = module.TypeSystem;
            var shortArray = new ArrayType(ts.Int16);
            var fastType = new TypeReference("WogiOS", "FastBink", module, hostRef);
            var fast = new MethodReference("Decode", shortArray, fastType) { HasThis = false };
            fast.Parameters.Add(new ParameterDefinition(new ArrayType(ts.Byte)));   // data
            fast.Parameters.Add(new ParameterDefinition(new ArrayType(ts.Int32)));  // framePos
            fast.Parameters.Add(new ParameterDefinition(ts.Int32));                 // sampleRate
            fast.Parameters.Add(new ParameterDefinition(ts.Int32));                 // channels
            fast.Parameters.Add(new ParameterDefinition(ts.Int32));                 // frameCount
            fast.Parameters.Add(new ParameterDefinition(ts.Int32));                 // audioTracks
            fast.Parameters.Add(new ParameterDefinition(ts.Boolean));               // useDct

            // 원본은 이름만 바꿔 남긴다
            decode.Name = "DecodeSlow";

            var nd = new MethodDefinition("Decode", decode.Attributes, decode.ReturnType);
            bink.Methods.Add(nd);
            nd.Body.InitLocals = true;
            var local = new VariableDefinition(shortArray);
            nd.Body.Variables.Add(local);
            ILProcessor il = nd.Body.GetILProcessor();

            Instruction slow = il.Create(OpCodes.Ldarg_0);

            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fData);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fFramePos);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getInfo); il.Emit(OpCodes.Callvirt, gRate);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getInfo); il.Emit(OpCodes.Callvirt, gCh);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getInfo); il.Emit(OpCodes.Callvirt, gFrames);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fTracks);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getInfo); il.Emit(OpCodes.Callvirt, gDct);
            il.Emit(OpCodes.Call, fast);
            il.Emit(OpCodes.Stloc, local);
            il.Emit(OpCodes.Ldloc, local);
            il.Emit(OpCodes.Brfalse, slow);
            il.Emit(OpCodes.Ldloc, local);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getInfo); il.Emit(OpCodes.Callvirt, gRate);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getInfo); il.Emit(OpCodes.Callvirt, gCh);
            il.Emit(OpCodes.Newobj, pcmCtor);
            il.Emit(OpCodes.Ret);
            il.Append(slow);
            il.Emit(OpCodes.Call, decode);
            il.Emit(OpCodes.Ret);

            asm.Write();
            log("bink hook: applied (BinkAudio.Decode -> FastBink)");
        }
        catch (Exception ex)
        {
            // 연결에 실패해도 게임은 원래 방식으로 계속 돌아가야 한다
            log("bink hook FAILED (game runs the original decoder): " + ex);
        }
    }
}
