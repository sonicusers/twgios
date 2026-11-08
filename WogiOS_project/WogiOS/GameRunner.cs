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
            OfferRollbackIfLastLaunchFailed(host, ui, layout);
            string before = ReleaseFetcher.InstalledVersion(layout.ReleaseDir);
            FetchRelease(host, ui, layout.ReleaseDir, layout.ShimPath, docs);
            KeepOnlyPreviousVersion(layout, before);
            DropStalePatched(layout, docs);
            ui.ShowStatus("게임을 준비하는 중… (처음에는 자료를 받느라 시간이 걸립니다)");
            MarkLaunching(layout);
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

    // 게임 버전 메뉴(VersionSheet)가 ReleaseFetcher 의 고정/보관 버전을 조회할 때 쓴다 — Run() 의 release 폴더와 같은 곳
    internal static string ReleaseDirPath() => Path.Combine(
        NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.DocumentDirectory, NSSearchPathDomain.User)[0].Path,
        "release");

    // 보관 개수는 DLL(ReleaseFetcher)에 "지금 + 이전 3개"로 박혀 있어 바꿀 수 없다.
    // 대신 업데이트로 버전이 바뀐 직후에 바로 전에 쓰던 버전(before) 하나만 남기고 versions 폴더를 비운다.
    // 버전이 안 바뀌었으면(이미 최신이거나 업데이트 실패) 아무것도 지우지 않는다. 고정한 버전으로 바꿔 시작한 경우에도 같은 규칙(바로 전 버전만 남김)이 적용된다.
    static void KeepOnlyPreviousVersion(GameLayout layout, string before)
    {
        try
        {
            string now = ReleaseFetcher.InstalledVersion(layout.ReleaseDir);
            if (before == null || now == null || now == before) return;
            string archive = Path.Combine(
                Path.GetDirectoryName(layout.ReleaseDir.TrimEnd(Path.DirectorySeparatorChar)), "versions");
            if (!Directory.Exists(archive)) return;
            foreach (string dir in Directory.GetDirectories(archive))
            {
                if (Path.GetFileName(dir) == before) continue;
                Host.Log("오래된 보관 버전 삭제: " + Path.GetFileName(dir));
                Directory.Delete(dir, true);
            }
        }
        catch (Exception ex) { Host.Log("archive prune skipped: " + ex.Message); }
    }

    // ---- 시작 실패 안전장치 ----
    // 게임을 시작하기 직전에 launch.attempt 파일(내용: 실행하려는 버전)을 만들고, 게임 첫 화면이 그려지면(FirstFrame) 지운다.
    // 다음 실행 때 파일이 남아 있으면 지난번 실행이 화면이 뜨기 전에 멈춘 것이므로 이전 버전으로 시작할지 묻는다.
    static string LaunchFlag() =>
        Path.Combine(Path.GetDirectoryName(ReleaseDirPath()), "launch.attempt");

    static void MarkLaunching(GameLayout layout)
    {
        try
        {
            string cur = ReleaseFetcher.InstalledVersion(layout.ReleaseDir);
            if (cur != null) File.WriteAllText(LaunchFlag(), cur);
        }
        catch (Exception) { }
    }

    // 게임 첫 화면이 그려졌을 때 호출 (GameViewController 의 FirstFrame)
    internal static void MarkLaunchOk()
    {
        try { File.Delete(LaunchFlag()); } catch (Exception) { }
    }

    static Version OrderVer(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new Version(0, 0);
        return Version.TryParse(s.Trim().TrimStart('v', 'V').Split('-')[0], out var v) ? v : new Version(0, 0);
    }

    static void OfferRollbackIfLastLaunchFailed(IosHost host, GameViewController ui, GameLayout layout)
    {
        try
        {
            string flag = LaunchFlag();
            if (!File.Exists(flag)) return;
            string failed = File.ReadAllText(flag).Trim();
            File.Delete(flag);   // 한 번만 묻는다

            // 실패한 게 지금 설치된 버전이 아니거나(그 뒤로 버전이 바뀜), 이미 버전을 고정해 둔 상태면 묻지 않는다
            if (failed != ReleaseFetcher.InstalledVersion(layout.ReleaseDir)) return;
            if (ReleaseFetcher.PinnedVersion(layout.ReleaseDir) != null) return;

            // 이 기기에 보관된 버전 중 실패한 버전보다 낮은 것 가운데 가장 높은 버전
            Version failedV = OrderVer(failed);
            string prev = ReleaseFetcher.KnownVersions(layout.ReleaseDir)
                .Where(v => v != failed && OrderVer(v) < failedV)
                .OrderByDescending(v => OrderVer(v))
                .FirstOrDefault();
            if (prev == null) return;

            Host.Log("지난번 v" + failed + " 실행이 첫 화면 전에 멈춤 -> 이전 버전 v" + prev + " 제안");
            int choice = host.Ask(
                "지난번 실행이 멈췄어요",
                "v" + failed + " 로 시작하다가 게임 화면이 뜨기 전에 멈춘 것 같습니다.\n\n" +
                "이전 버전 v" + prev + " 으로 시작할까요? (자동 업데이트는 꺼집니다. 나중에 메뉴 > 게임 버전 에서 \"자동\"을 골라야 다시 켜져요)\n\n" +
                "로딩이 길어 앱을 직접 종료했을 뿐이라면 '그대로 계속'을 누르세요.",
                new[] { "v" + prev + " 으로 시작", "그대로 계속" });
            if (choice == 0)
                ReleaseFetcher.Pin(layout.ReleaseDir, prev);   // 이어지는 FetchRelease 가 고정한 버전으로 바꿔 놓는다. 풀려면 메뉴 > 게임 버전 > 자동
        }
        catch (Exception ex) { Host.Log("rollback check skipped: " + ex.Message); }
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
        // 안내줄 패치 방식이 바뀐 뒤 처음 실행이면 옛 patched 폴더를 한 번 비워 다시 만들게 한다
        CapHook.ResetIfOutdated(layout.PatchedDir, Host.Log);
        string patched = GameLauncher.Prepare(layout, Host.Log);
        MarkPatchedVersion(layout);
        // BGM(Bink) 디코딩을 앱 본체(AOT)의 FastBink 로 연결 — 인터프리터로 곡 전체를 푸느라 생기던 지연을 없앤다
        BinkHook.Apply(Path.Combine(patched, "WarOfGenesis.Assets.dll"), Host.Log);
        // 하단 안내줄의 색 알약 아이콘을 속이 빈 모양(글자만)으로
        CapHook.Apply(Path.Combine(patched, "WarOfGenesis.dll"), Host.Log);
        // WogShim 에 없는 RichTextBox 를 쓰는 「내 리포트」 창 때문에 시작하자마자 죽는 문제를 막는다
        BugReportHook.Apply(Path.Combine(patched, "WarOfGenesis.dll"), Host.Log);
        // WogShim 에 없는 Windows 전용 호출(SetThreadExecutionState · GetDpiForWindow)을 0 을 돌려주는 빈 메서드로 바꾼다
        PInvokeStubHook.Apply(Path.Combine(patched, "WarOfGenesis.dll"), Host.Log);
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
        // 메뉴 > 게임 버전 에서 특정 버전에 "머물기"를 골랐으면 그 버전으로 바꿔 놓고 업데이트 확인 자체를 건너뛴다 (안드로이드판과 같은 방식).
        // 고정한 버전이 이 기기에 더는 없으면 고정을 풀고 평소처럼 업데이트한다.
        string pinned = ReleaseFetcher.PinnedVersion(releaseDir);
        if (pinned != null)
        {
            if (ReleaseFetcher.KnownVersions(releaseDir).Contains(pinned))
            {
                ReleaseFetcher.Activate(releaseDir, pinned);
                ui.ShowStatus("고정한 버전 v" + pinned + " 으로 시작합니다 (자동 업데이트 꺼짐)");
                return;
            }
            ReleaseFetcher.Pin(releaseDir, null);
        }

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
            var fetcher = new ReleaseFetcher(http, s => { ui.ShowStatus(s); Host.Log("[update] " + s); });   // 업데이트 판단 과정을 wog.log 에도 남긴다
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
            Host.Log("update failed (" + ex.GetType().Name + "): " + ex);   // 이유를 wog.log 에 남긴다
            if (ReleaseFetcher.InstalledVersion(releaseDir) == null)
                throw new IOException("게임 파일을 받지 못했습니다 — 인터넷 연결을 확인해 주세요.", ex);
            ui.ShowStatus("업데이트를 확인하지 못해 v" + ReleaseFetcher.InstalledVersion(releaseDir) + " 으로 시작합니다");
        }
    }
}
