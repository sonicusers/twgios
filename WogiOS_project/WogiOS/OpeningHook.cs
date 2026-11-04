using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using WogShim;

namespace WogiOS;

// 게임을 켤 때 오프닝(0008.wgm) 앞에 소프트맥스 로고 영상(0032.wgm)을 먼저 튼다.
//   CutscenePlayer.StartMovie(int movie) 맨 앞에
//     if (movie == 8) StartMovie(32);
//   를 끼워 넣는다. 로고가 먼저 큐에 들어가고, 이어서 원래 코드가 오프닝(8)을 큐에 넣는다.
//   0032.wgm 이 없으면 StartMovie(32) 가 false 만 돌려주고 아무것도 안 하므로 전처럼 오프닝만 나온다.
//   영상 파일 위치(파일 앱): Documents/save/assets/effects/cut/0032.wgm, 0008.wgm
//
// 진단: 영상이 안 나올 때 원인을 찾으려고 CutscenePlayer 의 StartMovie / Open / Decode / Skip 맨 앞에
//   OpeningTrace 호출을 끼워 wog.log 에 "opening trace: ..." 줄을 남긴다. (게임 동작은 바꾸지 않는다)
//
// 여러 번 실행해도 안전하다(이미 끼워져 있으면 그 부분은 아무것도 하지 않는다).
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

            bool changed = false;

            // 1) 로고 영상 추가
            bool already = start.Body.Instructions.Any(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference mr && mr.Name == "StartMovie");
            if (already) log("opening hook: already applied");
            else
            {
                ILProcessor il = start.Body.GetILProcessor();
                Instruction first = start.Body.Instructions[0];

                // if (movie != 8) goto 원래 코드;
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, OpeningMovie));
                il.InsertBefore(first, il.Create(OpCodes.Bne_Un, first));
                // this.StartMovie(32);  (결과는 버린다)
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, LogoMovie));
                il.InsertBefore(first, il.Create(OpCodes.Call, start));
                il.InsertBefore(first, il.Create(OpCodes.Pop));

                changed = true;
                log("opening hook: applied (오프닝 앞에 로고 영상 0032 추가)");
            }

            // 2) 진단용 로그 호출
            if (InjectTrace(module, player, log)) changed = true;

            if (changed) asm.Write();
        }
        catch (Exception ex)
        {
            // 실패해도 게임은 원래대로(오프닝만) 돌아가야 한다
            log("opening hook FAILED (game keeps the original opening): " + ex);
        }
    }

    // CutscenePlayer 의 StartMovie / Open / Decode / Skip 맨 앞에 OpeningTrace 호출을 끼운다.
    static bool InjectTrace(ModuleDefinition module, TypeDefinition player, Action<string> log)
    {
        bool done = player.Methods.Any(m => m.HasBody && m.Body.Instructions.Any(i =>
            i.Operand is MethodReference mr && mr.DeclaringType != null && mr.DeclaringType.Name == "OpeningTrace"));
        if (done) return false;

        var self = typeof(OpeningHook).Assembly.GetName();
        AssemblyNameReference hostRef = module.AssemblyReferences.FirstOrDefault(a => a.Name == self.Name);
        if (hostRef == null)
        {
            hostRef = new AssemblyNameReference(self.Name, self.Version);
            module.AssemblyReferences.Add(hostRef);
        }
        var traceType = new TypeReference("WogiOS", "OpeningTrace", module, hostRef);

        MethodReference Ref(string name, bool withInt)
        {
            var r = new MethodReference(name, module.TypeSystem.Void, traceType) { HasThis = false };
            if (withInt) r.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            return r;
        }

        bool IsInt(ParameterDefinition p) => p.ParameterType.MetadataType == MetadataType.Int32;

        int count = 0;
        void Put(MethodDefinition m, OpCode loadArg, string traceName, bool withInt)
        {
            if (m == null || !m.HasBody) return;
            ILProcessor il = m.Body.GetILProcessor();
            Instruction first = m.Body.Instructions[0];
            if (withInt) il.InsertBefore(first, il.Create(loadArg));
            il.InsertBefore(first, il.Create(OpCodes.Call, Ref(traceName, withInt)));
            count++;
        }

        // StartMovie(int movie)  — 인스턴스라서 인자 0 은 this, movie 는 인자 1
        Put(player.Methods.FirstOrDefault(m => m.Name == "StartMovie" && !m.IsStatic && m.Parameters.Count == 1 && IsInt(m.Parameters[0])),
            OpCodes.Ldarg_1, "Start", true);
        // Open(int movie)  — static 이라 movie 는 인자 0
        Put(player.Methods.FirstOrDefault(m => m.Name == "Open" && m.IsStatic && m.Parameters.Count == 1 && IsInt(m.Parameters[0])),
            OpCodes.Ldarg_0, "Open", true);
        // Decode(Clip clip, int index)  — index 는 인자 2
        Put(player.Methods.FirstOrDefault(m => m.Name == "Decode" && !m.IsStatic && m.Parameters.Count == 2 && IsInt(m.Parameters[1])),
            OpCodes.Ldarg_2, "Decode", true);
        // Skip()
        Put(player.Methods.FirstOrDefault(m => m.Name == "Skip" && !m.IsStatic && m.Parameters.Count == 0),
            OpCodes.Nop, "Skip", false);

        log("opening hook: trace 호출 " + count + "곳 추가");
        return count > 0;
    }

    // 진단용 — 영상 파일이 게임이 읽을 수 있는 모양인지, 지난번에 파트1 을 했는지를 wog.log 에 남긴다.
    // (게임은 영상 파일이 이상하면 아무 말 없이 건너뛴다)
    public static void Diagnose(string saveDir, Action<string> log)
    {
        try
        {
            string cut = Path.Combine(saveDir, "assets", "effects", "cut");
            log("opening diag: 영상 폴더 " + cut + " (" + (Directory.Exists(cut) ? "있음" : "없음") + ")");
            foreach (int n in new[] { LogoMovie, OpeningMovie })
            {
                string path = Path.Combine(cut, n.ToString("D4") + ".wgm");
                if (!File.Exists(path)) { log("opening diag: " + n.ToString("D4") + ".wgm 없음"); continue; }
                using var f = File.OpenRead(path);
                var head = new byte[26];
                int got = f.Read(head, 0, head.Length);
                if (got < head.Length || head[0] != 'W' || head[1] != 'G' || head[2] != 'M' || head[3] != '1')
                {
                    log("opening diag: " + n.ToString("D4") + ".wgm 머리말이 WGM1 이 아님 (" + f.Length + " bytes) - 변환이 잘못된 파일");
                    continue;
                }
                int w = BitConverter.ToUInt16(head, 4), h = BitConverter.ToUInt16(head, 6);
                int fn = BitConverter.ToUInt16(head, 8), fd = BitConverter.ToUInt16(head, 10);
                int count = BitConverter.ToInt32(head, 12), rate = BitConverter.ToInt32(head, 16);
                int ch = BitConverter.ToUInt16(head, 20), snd = BitConverter.ToInt32(head, 22);
                bool ok = w > 0 && w <= 2048 && h > 0 && h <= 2048 && fn > 0 && fd > 0 && count >= 0 && count <= 100000 && snd >= 0;
                log("opening diag: " + n.ToString("D4") + ".wgm " + f.Length + " bytes, " + w + "x" + h + ", " + fn + "/" + fd
                    + " fps, " + count + "컷, 소리 " + snd + " bytes(" + rate + "Hz," + ch + "ch) -> " + (ok ? "읽을 수 있음" : "게임이 거부하는 값"));
            }
            string last = Path.Combine(saveDir, "last-part.txt");
            log("opening diag: last-part.txt = " + (File.Exists(last) ? File.ReadAllText(last).Trim() : "(2)") + "  (1 이면 파트1 타이틀부터 열려 오프닝을 건너뜀)");
        }
        catch (Exception ex) { log("opening diag failed: " + ex.Message); }
    }
}

// 패치된 게임 DLL 이 부르는 진단용 로그 — 게임 쪽에서 불러야 해서 public 이다.
public static class OpeningTrace
{
    static int _decodes;

    public static void Start(int movie) => Host.Log("opening trace: StartMovie(" + movie + ")");

    public static void Open(int movie) => Host.Log("opening trace: Open(" + movie + ")");

    public static void Decode(int index)
    {
        if (_decodes++ < 3 || index % 100 == 0) Host.Log("opening trace: Decode frame " + index);
    }

    public static void Skip()
    {
        string trace = new System.Diagnostics.StackTrace().ToString();
        if (trace.Length > 500) trace = trace.Substring(0, 500);
        Host.Log("opening trace: Skip() 호출됨 " + trace.Replace('\n', ' '));
    }
}
