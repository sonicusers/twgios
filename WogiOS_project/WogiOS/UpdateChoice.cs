using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WogPatcher;
using WogShim;

namespace WogiOS;

// 새 버전이 있을 때 묻는 창 + "이전 버전 받기".
//   [받기]          지금까지처럼 최신 버전으로 업데이트
//   [이전 버전 받기]  GitHub 릴리스 목록에서 골라 받는다. 받으면 그 버전으로 고정되고(자동 업데이트 꺼짐), 지금 버전은 보관된다
//   [나중에]         이번에는 업데이트하지 않고 지금 버전으로 실행
internal static class UpdateChoice
{
    const string Repo = "Kyeongrok/the-war-of-genesis-clicker";

    internal enum Kind { Update, Skip, Older, Force }   // Force: 이 앱이 지원하지 못한다고 판정된 버전을 그래도 받는다

    // 묻지 않고 그냥 평소처럼 업데이트하게 두는 경우(Update):
    //   설치된 게 없음(첫 실행) · 최신을 못 알아냄(오프라인/504 등) · 이미 최신 · 창을 못 띄움
    // 버전을 고정해 둔 상태에서도 더 새로운 버전이 있으면 묻는다 — [받기] 가 고정을 풀고 자동 업데이트를 다시 켜는 유일한 입구다.
    internal static (Kind kind, string tag) Prompt(IosHost host, string releaseDir, string checkKey)
    {
        try
        {
            Version installed = ParseVer(ReleaseFetcher.InstalledVersion(releaseDir));
            if (installed == null) return (Kind.Update, null);
            string pinned = ReleaseFetcher.PinnedVersion(releaseDir);   // 이전 버전을 받아 고정해 둔 상태면 값이 있다

            string latestRaw = FetchLatestRaw(out Version latest);
            if (latest == null || latest <= installed) return (Kind.Update, null);

            string cur = ReleaseFetcher.InstalledVersion(releaseDir);
            // ReleaseFetcher 가 "이 앱으로는 못 쓰는 버전"이라고 기억해 둔 것과 같은 판정(같은 버전 + 같은 앱 빌드)
            string rejectedFile = Path.Combine(releaseDir, "rejected.version");
            bool rejected = File.Exists(rejectedFile) && File.ReadAllText(rejectedFile) == latestRaw + "|" + checkKey;

            int choice;
            if (rejected)
            {
                choice = host.Ask("새 버전은 데이터 다운로드가 필요합니다",
                    "v" + latestRaw + " 에는 현재 앱이 지원하지 못 할 수 있는 부분이 있어, 지금 v" + cur + " 으로 실행합니다.\n\n" +
                    "현재 최신 버전 받기: 받아서 실행해 봅니다. 일부 기능에서 멈출 수 있고, 문제가 생기면 다음에 켤 때 이전 버전으로 되돌릴 수 있습니다.\n" +
                    "이전 버전 받기: 다른 버전을 골라 받기 (받으면 선택하신 버전으로 고정됩니다)",
                    new[] { "현재 버전 머무르기", "현재 최신 버전 받기", "이전 버전 받기" });
                if (choice < 0 || choice == 0) return (Kind.Update, null);   // 평소 흐름이 알아서 "앱 업데이트가 필요해 …" 로 넘어간다
                if (choice == 1) return (Kind.Force, null);
            }
            else
            {
                choice = host.Ask("새 버전 v" + latestRaw + " 이 있습니다",
                    "지금 v" + cur + " 을 쓰고 있습니다." +
                    (pinned != null ? "\n(자동 업데이트 꺼짐 — 받기를 누르면 다시 켜집니다)" : "") + "\n\n" +
                    "받기: 새 버전으로 업데이트\n이전 버전 받기: 목록에서 골라 받기 (받으면 선택하신 버전으로 고정됩니다)\n나중에: 이번에는 지금 버전으로 실행",
                    new[] { "받기", "이전 버전 받기", "나중에" });
                if (choice < 0) return (Kind.Update, null);
                if (choice == 0)
                {
                    if (pinned != null) ReleaseFetcher.Pin(releaseDir, null);   // 받기 = 자동 업데이트를 다시 켜고 최신으로
                    return (Kind.Update, null);
                }
                if (choice == 2) return (Kind.Skip, null);
            }

            string tag = PickTag(host, installed);
            return tag != null ? (Kind.Older, tag) : (Kind.Skip, null);   // 취소하면 이번에는 지금 버전으로 실행
        }
        catch (Exception ex)
        {
            Host.Log("update prompt skipped: " + ex.Message);
            return (Kind.Update, null);
        }
    }

    const int Step = 5;    // 최신에서 몇 버전씩 건너뛰며 보여줄지
    const int Count = 3;   // 보여줄 개수

    static string PickTag(IosHost host, Version installed)
    {
        List<(string tag, string label, Version ver)> list;
        try { list = ListReleases(); }
        catch (Exception ex)
        {
            Host.Log("release list failed: " + ex);
            host.Ask("목록을 불러오지 못했습니다", "GitHub에서 버전 목록을 가져오지 못했습니다. 잠시 뒤 다시 시도해 주세요.\n(" + ex.Message + ")", new[] { "확인" });
            return null;
        }
        if (list.Count == 0)
        {
            host.Ask("받을 수 있는 버전이 없습니다", "GitHub 릴리스에서 받을 수 있는 버전을 찾지 못했습니다.", new[] { "확인" });
            return null;
        }
        var shown = StepPick(list, Step, Count);
        string[] buttons = shown
            .Select(x => x.label + (installed != null && x.ver == installed ? "  (지금 실행 중)" : ""))
            .Append("취소").ToArray();
        int i = host.Ask("받을 버전을 고르세요",
            "최신 버전에서 " + Step + "버전씩 내려간 " + Count + "개입니다.\n받으면 선택하신 버전으로 고정됩니다. 지금 버전은 보관되어, 다시 고르면 되돌릴 수 있습니다.",
            buttons);
        return i >= 0 && i < shown.Count ? shown[i].tag : null;
    }

    // 최신 버전(릴리스 이름에서 읽은 가장 높은 번호)에서 step 씩 내려간 번호마다, 그 번호 이하 중 가장 높은 릴리스를 하나씩 고른다.
    // 예) 최신 1.0.92, step 5 -> 1.0.87 / 1.0.82 / 1.0.77 이하에서 가장 가까운 릴리스. 번호가 비어 있어도(중간 버전이 없어도) 가장 가까운 것을 고른다.
    static List<(string tag, string label, Version ver)> StepPick(List<(string tag, string label, Version ver)> list, int step, int count)
    {
        var withVer = list.Where(x => x.ver != null).ToList();
        if (withVer.Count == 0) return list.Take(count).ToList();   // 릴리스 이름에서 번호를 못 읽으면 예전처럼 최신순

        Version top = withVer.Max(x => x.ver);
        var res = new List<(string tag, string label, Version ver)>();
        for (int k = 1; k <= count; k++)
        {
            int build = top.Build - step * k;
            if (build < 0) break;
            var target = new Version(top.Major, top.Minor, build);
            var pick = withVer.Where(x => x.ver <= target && res.All(r => r.tag != x.tag))
                              .OrderByDescending(x => x.ver).FirstOrDefault();
            if (pick.tag != null) res.Add(pick);
        }
        if (res.Count == 0)   // 최신과 5 이내로 붙어 있는 릴리스뿐이면, 최신 바로 아래부터 보여준다
            res = withVer.OrderByDescending(x => x.ver).Skip(1).Take(count).ToList();
        return res;
    }

    // GitHub 릴리스 중 releases.win.json 이 올라와 있는(= 이 앱이 받을 수 있는) 것만, 최신순
    static List<(string tag, string label, Version ver)> ListReleases()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WogiOS/0.1");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        string json = http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases?per_page=30")
            .GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        var res = new List<(string tag, string label, Version ver)>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            bool hasList = false;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                    if (a.TryGetProperty("name", out var n) && n.GetString() == "releases.win.json") { hasList = true; break; }
            if (!hasList) continue;

            string tag = r.GetProperty("tag_name").GetString();
            string date = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && p.GetString().Length >= 10
                ? p.GetString().Substring(0, 10) : "";
            bool pre = r.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True;
            res.Add((tag, tag + (date != "" ? "  (" + date + ")" : "") + (pre ? "  시험판" : ""), TagVersion(tag)));
        }
        return res;
    }

    // 최신 릴리스의 releases.win.json 에서 가장 높은 Full 버전 (원문 문자열과 Version 둘 다)
    static string FetchLatestRaw(out Version best)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WogiOS/0.1");
        string json = http.GetStringAsync("https://github.com/" + Repo + "/releases/latest/download/releases.win.json")
            .GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        best = null;
        string bestRaw = null;
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
                if (v != null && (best == null || v > best)) { best = v; bestRaw = ver; }
            }
        }
        return bestRaw;
    }

    // 릴리스 이름(예: v1.0.92)에서 첫 번째 "숫자.숫자.숫자" 를 읽어 세 자리 버전으로 만든다. 못 읽으면 null
    static Version TagVersion(string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tag ?? "", @"\d+\.\d+(\.\d+)?");
        if (!m.Success || !Version.TryParse(m.Value, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    static Version ParseVer(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s.Substring(0, cut);
        return Version.TryParse(s, out var v) ? v : null;
    }
}

// ReleaseFetcher 는 "최신 릴리스" 주소(/releases/latest/download/)로만 받도록 되어 있다.
// 이 핸들러가 그 주소를 고른 태그의 주소(/releases/download/<태그>/)로 바꿔 보내서, ReleaseFetcher 를 고치지 않고도 이전 버전을 받게 한다.
internal sealed class TagRewriteHandler : DelegatingHandler
{
    const string From = "/releases/latest/download/";
    readonly string _to;

    public TagRewriteHandler(string tag) : base(new HttpClientHandler())
    {
        _to = "/releases/download/" + Uri.EscapeDataString(tag) + "/";
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string url = request.RequestUri?.AbsoluteUri;
        if (url != null && url.Contains(From))
            request.RequestUri = new Uri(url.Replace(From, _to));
        return base.SendAsync(request, ct);
    }
}
