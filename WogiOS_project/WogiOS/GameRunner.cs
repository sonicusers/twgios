using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Foundation;
using UIKit;
using WogPatcher;
using WogShim;

namespace WogiOS;

internal static class GameRunner
{
    public static void Run(IosHost host, GameViewController ui)
    {
        try { File.WriteAllText(LogPath(), ""); } catch (Exception) { }
        Host.LogSink = text =>
        {
            Console.WriteLine(text);
            FileLog(text);
            if (text.StartsWith("[wog] [form] ", StringComparison.Ordinal))
                ui.ShowStatus(text.Substring("[wog] [form] ".Length));
        };

        IosHost.RestorePickedFolders();

        string docs = NSFileManager.DefaultManager
            .GetUrls(NSSearchPathDirectory.DocumentDirectory, NSSearchPathDomain.User)[0].Path;
        string assets = Path.Combine(docs, "assets");
        var layout = new GameLayout(
            Path.Combine(docs, "release"),
            Path.Combine(docs, "patched"),
            Path.Combine(docs, "save"),
            Directory.Exists(Path.Combine(assets, "characters")) ? assets : null,
            ExtractShim());
        try
        {
            ui.ShowStatus("게임 파일 확인 중…");
            WipeIfNewerRelease(ui, layout);
            FetchRelease(host, ui, layout.ReleaseDir, layout.ShimPath, docs);
            DropStalePatched(layout, docs);
            ui.ShowStatus("게임을 준비하는 중… (처음에는 자료를 받느라 시간이 걸립니다)");
            host.StartGl();
            Host.Use(host);
            Launch(layout);
            Host.Log("game exited");
            QuitApp();
        }
        catch (Exception ex)
        {
            Host.Log("FATAL " + ex);
            try { File.WriteAllText(Path.Combine(docs, "crash.txt"), ex.ToString()); } catch (IOException) { }
            ui.ShowStatus("\n오류가 났습니다. 이 화면을 캡처해 보내 주세요.\n\n" +
                string.Join("\n", ex.ToString().Split('\n').Take(12)), error: true);
        }
    }

    // 타이틀의 EXIT 를 누르면 게임의 Main 이 끝나고 게임 스레드만 종료된다.
    // 그러면 화면에는 마지막으로 그린 프레임(타이틀)이 멈춘 채 남아 "종료가 안 되는" 것처럼 보이므로 앱 자체를 끝낸다.
    static void QuitApp()
    {
        Thread.Sleep(300); // wog.log 에 마지막 줄이 기록될 시간
        Environment.Exit(0);
    }

    // 진단용: 게임/shim 로그를 파일 앱 > WogiOS > wog.log 로도 남긴다 (실행할 때마다 새로 씀)
    static string LogPath() => Path.Combine(NSFileManager.DefaultManager
        .GetUrls(NSSearchPathDirectory.DocumentDirectory, NSSearchPathDomain.User)[0].Path, "wog.log");

    static void FileLog(string text)
    {
        try { File.AppendAllText(LogPath(), DateTime.Now.ToString("HH:mm:ss.fff ") + text + "\n"); }
        catch (Exception) { }
    }

    // GameLauncher.Run 과 같은 동작. 단, iOS 경로 표기(/var vs /private/var) 차이로
    // 실패하던 "패치본인지 확인" 부분만 느슨하게 바꿨다.
    static void Launch(GameLayout layout)
    {
        string patched = GameLauncher.Prepare(layout, Host.Log);
        MarkPatchedVersion(layout);
        // BGM(Bink) 디코딩을 앱 본체(AOT)의 FastBink 로 연결 — 인터프리터로 곡 전체를 푸느라 생기던 지연을 없앤다
        BinkHook.Apply(Path.Combine(patched, "WarOfGenesis.Assets.dll"), Host.Log);
        Directory.CreateDirectory(layout.SaveDir);
        Environment.SetEnvironmentVariable("DUELDX_SAVEDIR", layout.SaveDir);
        // 앞서 고른 원본 파트2 폴더가 있으면 게임에 알려 준다 (3버튼 창을 건너뛴다)
        string part2 = IosHost.RestorePart2Folder();
        if (part2 != null)
        {
            Environment.SetEnvironmentVariable("WAROFGENESIS_ORIGINAL", part2);
            Host.Log("original part2 folder: " + part2);
        }
        if (layout.AssetsDir != null)
            Environment.SetEnvironmentVariable("WAROFGENESIS_ASSETS", layout.AssetsDir);
        string rel = layout.ReleaseDir.EndsWith(Path.DirectorySeparatorChar)
            ? layout.ReleaseDir : layout.ReleaseDir + Path.DirectorySeparatorChar;
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", rel);

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (string dir in new[] { patched, layout.ReleaseDir })
            {
                string p = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
            }
            return null;
        };

        Assembly game = null;
        foreach (string item in GamePatcher.GameAssemblies.Reverse())
        {
            Assembly loaded = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(patched, item));
            if (item == "WarOfGenesis.dll") game = loaded;
        }

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(x => x.GetName().Name.StartsWith("WarOfGenesis", StringComparison.Ordinal)).ToList())
        {
            Host.Log("loaded " + asm.GetName().Name + " from " + asm.Location);
            if (!asm.Location.Contains("/patched/"))
                throw new InvalidOperationException(asm.GetName().Name + " was loaded unpatched from " + asm.Location);
            NativeLibrary.SetDllImportResolver(asm, (lib, _, _) =>
                throw new DllNotFoundException("unpatched P/Invoke into " + lib + " in " + asm.GetName().Name));
        }

        if (game == null) throw new FileNotFoundException("patched WarOfGenesis.dll missing");
        MethodInfo main = game.GetType("DuelDx.Program", true)
            .GetMethod("Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Host.Log("starting " + game.GetName().Name);
        try
        {
            main.Invoke(null, main.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() });
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    // 최신 버전이 설치된 버전보다 높으면, 다운로드 전에 release/patched 폴더를 먼저 지운다.
    // 확인 실패(오프라인 등)나 버전 해석 실패 시에는 아무것도 지우지 않는다.
    static void WipeIfNewerRelease(GameViewController ui, GameLayout layout)
    {
        try
        {
            Version installed = ParseVer(ReleaseFetcher.InstalledVersion(layout.ReleaseDir));
            if (installed == null) return;
            Version latest = FetchLatestVersion();
            if (latest == null || latest <= installed) return;
            Host.Log("새 버전 v" + latest + " 감지 (설치됨 v" + installed + ") -> release/patched 삭제 후 다운로드");
            ui.ShowStatus("새 버전 v" + latest + " 을 받기 위해 기존 파일을 정리하는 중…");
            foreach (string dir in new[] { layout.PatchedDir, layout.ReleaseDir })
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            try { File.Delete(PatchedMarker(layout)); } catch (Exception) { }
        }
        catch (Exception ex) { Host.Log("wipe check skipped: " + ex.Message); }
    }

    static Version FetchLatestVersion()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WogiOS/0.1");
        string json = http.GetStringAsync(
            "https://github.com/Kyeongrok/the-war-of-genesis-clicker/releases/latest/download/releases.win.json")
            .GetAwaiter().GetResult();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Version best = null;
        foreach (var root in doc.RootElement.EnumerateObject())
        {
            if (!root.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var asset in root.Value.EnumerateArray())
            {
                string ver = null, type = null;
                foreach (var f in asset.EnumerateObject())
                {
                    if (f.Name.Equals("Version", StringComparison.OrdinalIgnoreCase)) ver = f.Value.GetString();
                    else if (f.Name.Equals("Type", StringComparison.OrdinalIgnoreCase)) type = f.Value.ToString();
                }
                if (type != null && !type.Equals("Full", StringComparison.OrdinalIgnoreCase)) continue;
                Version v = ParseVer(ver);
                if (v != null && (best == null || v > best)) best = v;
            }
        }
        return best;
    }

    static Version ParseVer(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s.Substring(0, cut);
        return Version.TryParse(s, out var v) ? v : null;
    }

    // 새 버전을 받았는데 예전 patched 폴더가 "최신"으로 취급돼 옛 버전으로 실행되는 문제 방지:
    // patched 를 만들 때 쓴 release 버전을 patched.version 에 적어 두고, 지금 release 버전과 다르면 patched 를 지운다.
    // (앱이 다운로드 직후 꺼져도 다음 실행 때 다시 검사하므로 안전)
    static string PatchedMarker(GameLayout layout) =>
        Path.Combine(Path.GetDirectoryName(layout.PatchedDir.TrimEnd(Path.DirectorySeparatorChar)), "patched.version");

    static void DropStalePatched(GameLayout layout, string docs)
    {
        try
        {
            string cur = ReleaseFetcher.InstalledVersion(layout.ReleaseDir);
            string marker = PatchedMarker(layout);
            string was = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            if (cur != null && was != cur && Directory.Exists(layout.PatchedDir))
            {
                Host.Log("release v" + cur + " != patched v" + (was ?? "?") + " -> patched 폴더 삭제");
                Directory.Delete(layout.PatchedDir, true);
            }
        }
        catch (Exception ex) { Host.Log("stale patched cleanup failed: " + ex.Message); }
    }

    static void MarkPatchedVersion(GameLayout layout)
    {
        try
        {
            string cur = ReleaseFetcher.InstalledVersion(layout.ReleaseDir);
            if (cur != null) File.WriteAllText(PatchedMarker(layout), cur);
        }
        catch (Exception) { }
    }

    static string ExtractShim()
    {
        string src = NSBundle.MainBundle.PathForResource("WogShim", "bin");
        string dst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WogShim.dll");
        File.Copy(src, dst, overwrite: true);
        return dst;
    }

    static void FetchRelease(IosHost host, GameViewController ui, string releaseDir, string shimPath, string files)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WogiOS/0.1");
        string probe = Path.Combine(files, "patch-probe");
        string checkKey = typeof(Host).Assembly.ManifestModule.ModuleVersionId + "/" +
                          typeof(GamePatcher).Assembly.ManifestModule.ModuleVersionId;

        PatchReport Check(string staging)
        {
            try { return GamePatcher.Patch(staging, shimPath, probe); }
            finally { try { Directory.Delete(probe, true); } catch (IOException) { } }
        }

        try
        {
            var fetcher = new ReleaseFetcher(http, s => ui.ShowStatus(s));
            fetcher.UpdateAsync(releaseDir, Check, checkKey).GetAwaiter().GetResult();
            if (fetcher.Rejected != null)
            {
                string cur = ReleaseFetcher.InstalledVersion(releaseDir) ?? "?";
                Host.Log("new release rejected:\n" + fetcher.Rejected);
                host.Ask("새 버전은 앱 업데이트가 필요합니다",
                    "개발자의 새 버전에 iOS판이 아직 지원하지 않는 부분이 있어, 지금 버전(v" + cur + ")으로 실행합니다.\n\n빠진 부분:\n" +
                    string.Join("\n", fetcher.Rejected.Missing.Take(6)), new[] { "확인" });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
        {
            if (ReleaseFetcher.InstalledVersion(releaseDir) == null)
                throw new IOException("게임 파일을 받지 못했습니다 — 인터넷 연결을 확인해 주세요.", ex);
            ui.ShowStatus("업데이트를 확인하지 못해 v" + ReleaseFetcher.InstalledVersion(releaseDir) + " 으로 시작합니다");
        }
    }
}
