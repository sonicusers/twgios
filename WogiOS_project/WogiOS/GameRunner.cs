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
            ApplyPin(ui, layout.ReleaseDir);   // 되돌리기/이전 버전 고정이 걸려 있으면 먼저 그 버전으로 맞춘다
            ui.ShowStatus("최신 버전 확인 중…");
            var pick = UpdateChoice.Prompt(host, layout.ReleaseDir, CheckKey());   // 새 버전이 있으면 [받기] [이전 버전 받기] [나중에]
            if (pick.kind == UpdateChoice.Kind.Skip)
                ui.ShowStatus("v" + before + " 으로 시작합니다");
            else if (pick.kind == UpdateChoice.Kind.Force)
                ForceInstall(host, ui, layout, docs, null);
            else
            {
                string tag = pick.kind == UpdateChoice.Kind.Older ? pick.tag : null;
                // true = 거부된 버전을 사용자가 "그래도 받기"로 골랐다
                if (FetchRelease(host, ui, layout.ReleaseDir, layout.ShimPath, docs, tag))
                    ForceInstall(host, ui, layout, docs, tag);
            }
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

    // Run() 의 release 폴더와 같은 곳 (시작 실패 표시 파일 등 Documents 바로 아래 파일의 위치도 여기서 구한다)
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

            // 이 기기에 보관된 버전 중 실패한 버전보다 낮은 것 가운데 가장 높은 버전.
            // 낮은 게 없으면(예: 낮은 버전을 받았다가 실패한 경우) 보관된 것 중 가장 높은 버전 — 보관은 바로 전에 쓰던 버전 하나뿐이다.
            Version failedV = OrderVer(failed);
            var others = ReleaseFetcher.KnownVersions(layout.ReleaseDir).Where(v => v != failed).ToList();
            string prev = others.Where(v => OrderVer(v) < failedV).OrderByDescending(v => OrderVer(v)).FirstOrDefault()
                          ?? others.OrderByDescending(v => OrderVer(v)).FirstOrDefault();
            if (prev == null) return;

            Host.Log("지난번 v" + failed + " 실행이 첫 화면 전에 멈춤 -> 이전 버전 v" + prev + " 제안");
            int choice = host.Ask(
                "지난번 실행이 멈췄어요",
                "v" + failed + " 로 시작하다가 게임 화면이 뜨기 전에 멈춘 것 같습니다.\n\n" +
                "이전에 쓰던 버전 v" + prev + " 으로 시작할까요? (자동 업데이트는 꺼집니다. 새 버전 안내 창에서 [받기]를 누르면 다시 켜져요)\n\n" +
                "로딩이 길어 앱을 직접 종료했을 뿐이라면 '그대로 계속'을 누르세요.",
                new[] { "v" + prev + " 으로 시작", "그대로 계속" });
            if (choice == 0)
                ReleaseFetcher.Pin(layout.ReleaseDir, prev);   // 이어지는 ApplyPin/FetchRelease 가 고정한 버전으로 바꿔 놓는다. 풀려면 새 버전 안내 창의 [받기]
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

    // 이전 버전을 받아 고정(version.pin)해 두었다면 그 버전으로 바꿔 놓고 true.
    // 고정한 버전이 이 기기에 더는 없으면 고정을 풀고 false (평소처럼 업데이트).
    static bool ApplyPin(GameViewController ui, string releaseDir)
    {
        string pinned = ReleaseFetcher.PinnedVersion(releaseDir);
        if (pinned == null) return false;
        if (ReleaseFetcher.KnownVersions(releaseDir).Contains(pinned))
        {
            ReleaseFetcher.Activate(releaseDir, pinned);
            ui.ShowStatus("고정한 버전 v" + pinned + " 으로 시작합니다 (자동 업데이트 꺼짐)");
            return true;
        }
        ReleaseFetcher.Pin(releaseDir, null);
        return false;
    }

    // "그래도 받기": ReleaseFetcher 는 지금 버전이 있으면 이 앱이 지원하지 못한다고 판정된 새 버전을 설치하지 않고,
    // 지금 버전이 없을 때만 경고를 띄우고 설치한다. 그래서 지금 버전을 보관(versions) 폴더로 옮겨 release 를 비운 채 받는다.
    // 받은 버전이 첫 화면 전에 멈추면 다음 실행 때 "이전 버전으로 시작할까요?"(OfferRollbackIfLastLaunchFailed)가 보관해 둔 버전으로 되돌려 준다.
    static void ForceInstall(IosHost host, GameViewController ui, GameLayout layout, string files, string tag)
    {
        string releaseDir = layout.ReleaseDir;
        string cur = ReleaseFetcher.InstalledVersion(releaseDir);
        string archive = null;
        if (cur != null && Directory.Exists(releaseDir))
        {
            string versions = Path.Combine(Path.GetDirectoryName(releaseDir.TrimEnd(Path.DirectorySeparatorChar)), "versions");
            archive = Path.Combine(versions, cur);
            Directory.CreateDirectory(versions);
            if (Directory.Exists(archive)) Directory.Delete(archive, true);
            Directory.Move(releaseDir, archive);
            Host.Log("그래도 받기: v" + cur + " 을 보관으로 옮기고 새로 받습니다");
        }
        try { FetchRelease(host, ui, releaseDir, layout.ShimPath, files, tag, force: true); }
        finally
        {
            // 받지 못해 release 가 비어 있으면 원래대로 돌려놓는다
            if (archive != null && ReleaseFetcher.InstalledVersion(releaseDir) == null && Directory.Exists(archive))
            {
                try
                {
                    if (Directory.Exists(releaseDir)) Directory.Delete(releaseDir, true);
                    Directory.Move(archive, releaseDir);
                    Host.Log("그래도 받기 실패 -> v" + cur + " 으로 되돌렸습니다");
                }
                catch (Exception ex) { Host.Log("restore failed: " + ex.Message); }
            }
        }
    }

    // tag != null 이면 "이전 버전 받기": 그 태그의 릴리스를 받고, 성공하면 그 버전으로 고정한다(자동 업데이트 꺼짐)
    // 반환값 true = 새 버전이 이 앱에서 지원되지 않는다고 거부됐고 사용자가 "그래도 받기"를 골랐다 (호출한 쪽이 ForceInstall 을 이어서 한다)
    static bool FetchRelease(IosHost host, GameViewController ui, string releaseDir, string shimPath, string files, string tag = null, bool force = false)
    {
        if (tag == null && !force && ApplyPin(ui, releaseDir)) return false;   // 고정한 버전이 있으면 그 버전으로 맞추고 업데이트 확인은 건너뛴다

        using var http = tag == null
            ? new HttpClient { Timeout = TimeSpan.FromMinutes(5) }
            : new HttpClient(new TagRewriteHandler(tag)) { Timeout = TimeSpan.FromMinutes(5) };   // 고른 태그의 파일을 받도록 주소를 바꿔 보낸다
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WogiOS/0.1");
        string probe = Path.Combine(files, "patch-probe");
        string checkKey = CheckKey();

        PatchReport Check(string staging)
        {
            try { return GamePatcher.Patch(staging, shimPath, probe); }
            finally { try { Directory.Delete(probe, true); } catch (IOException) { } }
        }

        try
        {
            var fetcher = new ReleaseFetcher(http, s => { ui.ShowStatus(s); Host.Log("[update] " + s); });   // 업데이트 판단 과정을 wog.log 에도 남긴다
            fetcher.UpdateAsync(releaseDir, Check, checkKey).GetAwaiter().GetResult();
            bool rejected = fetcher.Rejected != null;
            if (rejected)
            {
                string cur = ReleaseFetcher.InstalledVersion(releaseDir) ?? "?";
                Host.Log("new release rejected:\n" + fetcher.Rejected);
                string missing = string.Join("\n", fetcher.Rejected.Missing.Take(6));
                if (force)
                {
                    // 지금 버전이 없어서 ReleaseFetcher 가 경고만 하고 설치했다
                    host.Ask("일부 기능이 아직 지원되지 않습니다",
                        "v" + cur + " 으로 실행합니다. 아래 부분이 아직 없어 일부 기능에서 멈출 수 있습니다. " +
                        "문제가 생기면 다음에 켤 때 이전 버전으로 되돌릴 수 있습니다.\n\n빠진 부분(pc 버전 관련 : 화면 보호기, 창 배율, 윈도우 전용 창 관련입니다.)\n" + missing, new[] { "확인" });
                }
                else
                {
                    string what = tag == null
                        ? "개발자의 새 버전에 iOS판이 아직 지원하지 않는 부분이 있어"
                        : "고른 버전(" + tag + ")에 iOS판이 지원하지 않는 부분이 있어";
                    string forceLabel = tag == null ? "현재 최신 버전 받기" : "그래도 받기";
                    int choice = host.Ask("새 버전은 데이터 다운로드가 필요합니다",
                        what + ", 지금 버전(v" + cur + ")으로 실행합니다.\n\n빠진 부분(pc 버전 관련 : 화면 보호기, 창 배율, 윈도우 전용 창 관련입니다.)\n" + missing +
                        "\n\n" + forceLabel + ": 받아서 실행해 봅니다. 일부 기능에서 멈출 수 있고, 문제가 생기면 다음에 켤 때 이전 버전으로 되돌릴 수 있습니다.",
                        new[] { "현재 버전 머무르기", forceLabel });
                    if (choice == 1) return true;
                }
            }
            if ((!rejected || force) && tag != null)
            {
                // 이전 버전을 받았으니 그 버전에 머문다 (풀려면 새 버전 안내 창의 [받기])
                string v = ReleaseFetcher.InstalledVersion(releaseDir);
                if (v != null)
                {
                    ReleaseFetcher.Pin(releaseDir, v);
                    Host.Log("이전 버전 받기: " + tag + " -> v" + v + " 고정");
                    ui.ShowStatus("v" + v + " 으로 고정했습니다 (자동 업데이트 꺼짐)");
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
        {
            Host.Log("update failed (" + ex.GetType().Name + "): " + ex);   // 이유를 wog.log 에 남긴다
            if (force)
            {
                ui.ShowStatus("받지 못해 이전 버전으로 시작합니다");   // ForceInstall 이 보관해 둔 버전을 되돌려 놓는다
                return false;
            }
            if (ReleaseFetcher.InstalledVersion(releaseDir) == null)
                throw new IOException("게임 파일을 받지 못했습니다 — 인터넷 연결을 확인해 주세요.", ex);
            ui.ShowStatus(tag == null
                ? "업데이트를 확인하지 못해 v" + ReleaseFetcher.InstalledVersion(releaseDir) + " 으로 시작합니다"
                : "고른 버전(" + tag + ")을 받지 못해 v" + ReleaseFetcher.InstalledVersion(releaseDir) + " 으로 시작합니다");
            return false;
        }
    }

    // 업데이트 거부 기록(rejected.version)이 "같은 앱 빌드"인지 가리는 값 — FetchRelease 와 새 버전 안내 창이 같은 값을 쓴다
    static string CheckKey() =>
        typeof(Host).Assembly.ManifestModule.ModuleVersionId + "/" +
        typeof(GamePatcher).Assembly.ManifestModule.ModuleVersionId;
}
