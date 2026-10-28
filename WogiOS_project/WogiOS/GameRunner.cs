using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Foundation;
using UIKit;
using WogPatcher;
using WogShim;

namespace WogiOS;

// Android 의 MainActivity.RunGame / FetchRelease 를 그대로 옮긴 것
internal static class GameRunner
{
    public static void Run(IosHost host, GameViewController ui)
    {
        Host.LogSink = text =>
        {
            Console.WriteLine(text);
            if (text.StartsWith("[wog] [form] ", StringComparison.Ordinal))
                ui.ShowStatus(text.Substring("[wog] [form] ".Length));
        };

        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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
            FetchRelease(host, ui, layout.ReleaseDir, layout.ShimPath, docs);
            ui.ShowStatus("게임을 준비하는 중… (처음에는 자료를 받느라 시간이 걸립니다)");
            host.StartGl();
            Host.Use(host);
            GameLauncher.Run(layout, Host.Log);
            Host.Log("game exited");
        }
        catch (Exception ex)
        {
            Host.Log("FATAL " + ex);
            try { File.WriteAllText(Path.Combine(docs, "crash.txt"), ex.ToString()); } catch (IOException) { }
            ui.InvokeOnMainThread(() => { });
            ui.ShowStatus("\n오류가 났습니다. 이 화면을 캡처해 보내 주세요.\n\n" +
                string.Join("\n", ex.ToString().Split('\n').Take(12)), error: true);
        }
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
