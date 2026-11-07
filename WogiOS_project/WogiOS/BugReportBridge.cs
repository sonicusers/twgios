using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CoreGraphics;
using Foundation;
using UIKit;
using WogShim;

namespace WogiOS;

// 버그 리포트 창의 iOS 판.
//
// PC 판의 창(DuelDx.BugReportDialog)은 WinForms 라서 WogShim 에서 돌릴 수 없다.
// 그래서 패치된 WarOfGenesis.dll 의 BugReport.Open() 이 이 클래스의 Ask() 를 부르게 바꿔 두었다(BugReportHook).
//   - 글 쓰는 창 · 「같이 보내는 것」 · 「내 리포트」 만 UIKit 으로 새로 그린다.
//   - 글을 모아 줄에 세우고 서버로 보내는 일(Context · Summary · Submit · LoadMine)은 게임 쪽 코드를 그대로 쓴다.
// 게임 스레드에서 불리며, 창이 닫힐 때까지 기다렸다가 쓴 글(취소하면 null)을 돌려준다.
public static class BugReportBridge
{
    internal const int MaxText = 8000;

    public static string Ask(string summary)
    {
        try
        {
            if (NSThread.IsMain) return null;   // 메인 스레드에서 기다리면 멈춘다
            string result = null;
            using var done = new ManualResetEventSlim(false);
            UIApplication.SharedApplication.InvokeOnMainThread(() =>
            {
                UIViewController top = TopController();
                if (top == null) { done.Set(); return; }
                var vc = new BugReportController(summary ?? "", text => { result = text; done.Set(); });
                top.PresentViewController(vc, true, null);
            });
            done.Wait();
            return result;
        }
        catch (Exception ex)
        {
            Host.Log("bugreport: native dialog failed " + ex);
            return null;
        }
    }

    // ---- 기기 정보 ----
    // 게임이 적는 os 값은 「Unix 27.2.0 x64」(커널 버전 · 항상 x64)라 기기를 알 수 없다.
    // 패치된 Open() 이 Context() 직후에 이 메서드를 불러 「iOS 26.1 · iPhone15,2」 꼴로 바꾼다
    // (화면의 「같이 보내는 것」과 서버로 가는 값이 모두 이 값을 쓴다). IMEI · 시리얼 같은 고유 번호는 읽지도 보내지도 않는다.
    public static void AddDevice(Dictionary<string, object> ctx)
    {
        try { ctx["os"] = DeviceText(); }
        catch (Exception ex) { Host.Log("bugreport: device info failed " + ex.Message); }
    }

    [DllImport("libSystem.dylib")]
    static extern int sysctlbyname(string name, byte[] oldp, ref nint oldlenp, IntPtr newp, nint newlen);

    static string Machine()
    {
        try
        {
            nint len = 0;
            if (sysctlbyname("hw.machine", null, ref len, IntPtr.Zero, 0) != 0 || len <= 0) return "";
            var buf = new byte[len];
            if (sysctlbyname("hw.machine", buf, ref len, IntPtr.Zero, 0) != 0) return "";
            return System.Text.Encoding.ASCII.GetString(buf, 0, (int)len).TrimEnd('\0');
        }
        catch (Exception) { return ""; }
    }

    static string DeviceText()
    {
        string model = Machine();   // 예: iPhone15,2 · iPad13,4 (시뮬레이터는 arm64 · x86_64)
        var v = NSProcessInfo.ProcessInfo.OperatingSystemVersion;
        string name = model.StartsWith("iPad", StringComparison.Ordinal) ? "iPadOS" : "iOS";
        string ver = $"{name} {v.Major}.{v.Minor}" + (v.PatchVersion > 0 ? "." + v.PatchVersion : "");
        return model.Length > 0 ? ver + " · " + model : ver;
    }

    internal static UIViewController TopController()
    {
        UIViewController v = UIApplication.SharedApplication.KeyWindow?.RootViewController;
        while (v?.PresentedViewController != null) v = v.PresentedViewController;
        return v;
    }

    // ---- 「내 리포트」: 게임의 BugReport.LoadMine / QueuedCount 를 리플렉션으로 부른다 (internal 이라 직접 참조할 수 없다) ----

    internal sealed class MineRow
    {
        public long Id; public string CreatedAt, Version, Text, Status, Reply;
    }

    static Type FindGameType(string name)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name != "WarOfGenesis") continue;
            Type t = asm.GetType(name);
            if (t != null) return t;
        }
        return null;
    }

    // rows == null 이면 못 불러온 것
    internal static (List<MineRow> Rows, int Queued) FetchMine()
    {
        int queued = 0;
        try
        {
            Type t = FindGameType("DuelDx.BugReport");
            if (t == null) return (null, 0);
            const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try { queued = Convert.ToInt32(t.GetMethod("QueuedCount", F)?.Invoke(null, null) ?? 0); } catch (Exception) { }
            MethodInfo load = t.GetMethod("LoadMine", F);
            if (load == null) return (null, queued);
            var task = (Task)load.Invoke(null, null);
            if (!task.Wait(TimeSpan.FromSeconds(30))) return (null, queued);
            if (task.GetType().GetProperty("Result")?.GetValue(task) is not IEnumerable list) return (null, queued);

            var rows = new List<MineRow>();
            foreach (object item in list)
            {
                Type it = item.GetType();
                string S(string n) => it.GetProperty(n)?.GetValue(item) as string ?? "";
                rows.Add(new MineRow
                {
                    Id = Convert.ToInt64(it.GetProperty("Id")?.GetValue(item) ?? 0L),
                    CreatedAt = S("CreatedAt"), Version = S("Version"), Text = S("Text"), Status = S("Status"),
                    Reply = it.GetProperty("Reply")?.GetValue(item) as string,
                });
            }
            return (rows, queued);
        }
        catch (Exception ex)
        {
            Host.Log("bugreport: my reports failed " + ex.GetType().Name + ": " + ex.Message);
            return (null, queued);
        }
    }

    internal static string StatusName(string status) => status switch
    {
        "new" => "접수",
        "seen" => "확인함",
        "fixed" => "고침",
        "wontfix" => "고치지 않음",
        _ => status,
    };
}

// 두 창이 같이 쓰는 모양 도우미
internal static class BugUi
{
    public static readonly UIColor Panel = PadStyle.C(28, 36, 58, 0.98f);
    public static readonly UIColor Dim = UIColor.FromWhiteAlpha(0f, 0.6f);
    public static readonly UIColor Accent = PadStyle.C(70, 110, 220);
    public static readonly UIColor Blue = PadStyle.C(120, 165, 255);

    public static nfloat Min(nfloat a, nfloat b) => a < b ? a : b;
    public static nfloat Max(nfloat a, nfloat b) => a > b ? a : b;

    public static UIView Card()
    {
        var card = new UIView { BackgroundColor = Panel, ClipsToBounds = true };
        card.Layer.CornerRadius = 14;
        card.Layer.BorderWidth = 1.5f;
        card.Layer.BorderColor = PadStyle.Border.CGColor;
        return card;
    }

    public static UIButton Button(string title, bool primary = false)
    {
        var b = UIButton.FromType(UIButtonType.System);
        b.SetTitle(title, UIControlState.Normal);
        b.SetTitleColor(UIColor.White, UIControlState.Normal);
        b.SetTitleColor(UIColor.FromWhiteAlpha(1f, 0.4f), UIControlState.Disabled);
        b.TitleLabel.Font = UIFont.BoldSystemFontOfSize(14);
        b.TitleLabel.AdjustsFontSizeToFitWidth = true;
        b.BackgroundColor = primary ? Accent : PadStyle.C(55, 66, 96);
        b.Layer.CornerRadius = 8;
        return b;
    }

    public static UILabel Label(string text, nfloat size, bool bold, UIColor color) => new UILabel
    {
        Text = text, TextColor = color,
        Font = bold ? UIFont.BoldSystemFontOfSize(size) : UIFont.SystemFontOfSize(size),
    };
}

// 글 쓰는 창 — 여러 줄 글 칸 · 글자 수 · 같이 보내는 것 · 내 리포트 · 취소 / 보내기
internal sealed class BugReportController : UIViewController
{
    readonly string _summary;
    readonly Action<string> _done;
    UIView _card;
    UILabel _title, _count, _hint;
    UITextView _box;
    UIButton _mine, _info, _cancel, _send;
    nfloat _kb;
    NSObject _kbObserver;
    bool _finished;

    public BugReportController(string summary, Action<string> done)
    {
        _summary = summary;
        _done = done;
        ModalPresentationStyle = UIModalPresentationStyle.OverFullScreen;
        ModalTransitionStyle = UIModalTransitionStyle.CrossDissolve;
    }

    public override bool PrefersStatusBarHidden() => true;
    public override UIInterfaceOrientationMask GetSupportedInterfaceOrientations() => UIInterfaceOrientationMask.Landscape;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View.BackgroundColor = BugUi.Dim;

        _card = BugUi.Card();
        View.AddSubview(_card);

        _title = BugUi.Label("버그 리포트", 16, true, UIColor.White);
        _count = BugUi.Label("0 / " + BugReportBridge.MaxText, 12, false, UIColor.LightGray);
        _count.TextAlignment = UITextAlignment.Right;

        _box = new UITextView
        {
            BackgroundColor = UIColor.White,
            TextColor = UIColor.Black,
            Font = UIFont.SystemFontOfSize(15),
            TextContainerInset = new UIEdgeInsets(8, 6, 8, 6),
            AutocorrectionType = UITextAutocorrectionType.No,
            KeyboardDismissMode = UIScrollViewKeyboardDismissMode.None,
        };
        _box.Layer.CornerRadius = 8;
        _box.ShouldChangeText = (tv, range, text) => (int)tv.Text.Length - (int)range.Length + (text?.Length ?? 0) <= BugReportBridge.MaxText;
        _box.Changed += (s, e) => Refresh();

        _hint = new UILabel
        {
            Text = "어떤 일이 있었는지 적어 주세요 — 무엇을 하다가(어느 전투 · 어떤 기술), 무엇이 보였고, 원래는 어땠어야 하는지.",
            TextColor = UIColor.FromWhiteAlpha(0.55f, 1f),
            Font = UIFont.SystemFontOfSize(14),
            Lines = 0,
            UserInteractionEnabled = false,
        };
        _box.AddSubview(_hint);

        _mine = BugUi.Button("내 리포트");
        _info = BugUi.Button("보내는 정보");
        _cancel = BugUi.Button("취소");
        _send = BugUi.Button("보내기", primary: true);

        _mine.TouchUpInside += (s, e) => PresentViewController(new MyReportsController(), true, null);
        _info.TouchUpInside += (s, e) =>
        {
            var a = UIAlertController.Create("같이 보내는 것", _summary, UIAlertControllerStyle.Alert);
            a.AddAction(UIAlertAction.Create("확인", UIAlertActionStyle.Default, null));
            PresentViewController(a, true, null);
        };
        _cancel.TouchUpInside += (s, e) => OnCancel();
        _send.TouchUpInside += (s, e) =>
        {
            string text = (_box.Text ?? "").Trim();
            if (text.Length > 0) Finish(text);
        };

        foreach (UIView v in new UIView[] { _title, _count, _box, _mine, _info, _cancel, _send }) _card.AddSubview(v);

        _kbObserver = NSNotificationCenter.DefaultCenter.AddObserver(UIKeyboard.WillChangeFrameNotification, OnKeyboard);
        Refresh();
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        _box.BecomeFirstResponder();
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        LayoutCard();
    }

    void OnKeyboard(NSNotification n)
    {
        if (View == null) return;
        if (n.UserInfo?[UIKeyboard.FrameEndUserInfoKey] is NSValue value)
        {
            CGRect r = View.ConvertRectFromView(value.CGRectValue, null);
            _kb = BugUi.Max(0, View.Bounds.Height - r.Y);
            UIView.Animate(0.25, LayoutCard);
        }
    }

    void Refresh()
    {
        int len = (_box.Text ?? "").Length;
        _count.Text = len + " / " + BugReportBridge.MaxText;
        _hint.Hidden = len > 0;
        bool can = (_box.Text ?? "").Trim().Length > 0;
        _send.Enabled = can;
        _send.Alpha = can ? 1f : 0.5f;
    }

    void LayoutCard()
    {
        if (_card == null) return;
        UIEdgeInsets ins = View.SafeAreaInsets;
        nfloat W = View.Bounds.Width, H = View.Bounds.Height;
        nfloat usableW = W - ins.Left - ins.Right;
        nfloat cw = BugUi.Min(620, usableW - 24);
        nfloat top = BugUi.Max(ins.Top, 8) + 4;
        nfloat bottom = H - BugUi.Max(_kb, ins.Bottom) - 8;      // 키보드 위까지만 쓴다
        nfloat ch = BugUi.Max(150, BugUi.Min(400, bottom - top));
        nfloat cx = ins.Left + (usableW - cw) / 2;
        nfloat cy = _kb > 0 ? top : top + BugUi.Max(0, (bottom - top - ch) / 2);
        _card.Frame = new CGRect(cx, cy, cw, ch);

        const float pad = 12, gap = 8, barH = 36;
        _title.Frame = new CGRect(pad, 8, cw - 2 * pad - 120, 24);
        _count.Frame = new CGRect(cw - pad - 120, 8, 120, 24);
        _box.Frame = new CGRect(pad, 38, cw - 2 * pad, ch - 38 - barH - 16);
        _hint.Frame = new CGRect(11, 8, _box.Frame.Width - 22, 60);
        _hint.SizeToFit();
        _hint.Frame = new CGRect(11, 8, _box.Frame.Width - 22, _hint.Frame.Height);

        nfloat bw = BugUi.Min(100, (cw - 2 * pad - 3 * gap) / 4);
        nfloat by = ch - barH - 8;
        _mine.Frame = new CGRect(pad, by, bw, barH);
        _info.Frame = new CGRect(pad + bw + gap, by, bw, barH);
        _send.Frame = new CGRect(cw - pad - bw, by, bw, barH);
        _cancel.Frame = new CGRect(cw - pad - 2 * bw - gap, by, bw, barH);
    }

    void OnCancel()
    {
        if ((_box.Text ?? "").Trim().Length == 0) { Finish(null); return; }
        var a = UIAlertController.Create("작성한 글을 버릴까요?", "취소하면 지금까지 쓴 글이 사라집니다.", UIAlertControllerStyle.Alert);
        a.AddAction(UIAlertAction.Create("계속 쓰기", UIAlertActionStyle.Cancel, null));
        a.AddAction(UIAlertAction.Create("버리기", UIAlertActionStyle.Destructive, _ => Finish(null)));
        PresentViewController(a, true, null);
    }

    // 창을 완전히 닫은 뒤에 게임 스레드를 깨운다
    void Finish(string text)
    {
        if (_finished) return;
        _finished = true;
        if (_kbObserver != null) NSNotificationCenter.DefaultCenter.RemoveObserver(_kbObserver);
        _box.ResignFirstResponder();
        DismissViewController(true, () => _done(text));
    }
}

// 내 리포트 — 이 기기에서 보낸 리포트의 상태 · 받은 답
internal sealed class MyReportsController : UIViewController
{
    UIView _card;
    UILabel _title, _status;
    UITextView _list;
    UIButton _close;

    public MyReportsController()
    {
        ModalPresentationStyle = UIModalPresentationStyle.OverFullScreen;
        ModalTransitionStyle = UIModalTransitionStyle.CrossDissolve;
    }

    public override bool PrefersStatusBarHidden() => true;
    public override UIInterfaceOrientationMask GetSupportedInterfaceOrientations() => UIInterfaceOrientationMask.Landscape;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View.BackgroundColor = BugUi.Dim;
        _card = BugUi.Card();
        View.AddSubview(_card);

        _title = BugUi.Label("내 리포트", 16, true, UIColor.White);
        _status = BugUi.Label("불러오는 중입니다…", 12, false, UIColor.LightGray);
        _status.TextAlignment = UITextAlignment.Right;
        _list = new UITextView
        {
            Editable = false,
            BackgroundColor = PadStyle.C(16, 22, 38),
            TextColor = UIColor.White,
            Font = UIFont.SystemFontOfSize(14),
            TextContainerInset = new UIEdgeInsets(8, 6, 8, 6),
            Text = "이 기기에서 보내신 버그 리포트입니다. 고치거나 답을 달면 여기에 보입니다.",
        };
        _list.Layer.CornerRadius = 8;
        _close = BugUi.Button("닫기", primary: true);
        _close.TouchUpInside += (s, e) => DismissViewController(true, null);
        foreach (UIView v in new UIView[] { _title, _status, _list, _close }) _card.AddSubview(v);

        Task.Run(() =>
        {
            var (rows, queued) = BugReportBridge.FetchMine();
            InvokeOnMainThread(() => Fill(rows, queued));
        });
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        UIEdgeInsets ins = View.SafeAreaInsets;
        nfloat W = View.Bounds.Width, H = View.Bounds.Height;
        nfloat usableW = W - ins.Left - ins.Right;
        nfloat cw = BugUi.Min(620, usableW - 24);
        nfloat top = BugUi.Max(ins.Top, 8) + 4;
        nfloat bottom = H - BugUi.Max(ins.Bottom, 8) - 4;
        nfloat ch = BugUi.Max(150, BugUi.Min(400, bottom - top));
        _card.Frame = new CGRect(ins.Left + (usableW - cw) / 2, top + (bottom - top - ch) / 2, cw, ch);

        const float pad = 12, barH = 36;
        _title.Frame = new CGRect(pad, 8, 160, 24);
        _status.Frame = new CGRect(pad + 160, 8, cw - 2 * pad - 160, 24);
        _list.Frame = new CGRect(pad, 38, cw - 2 * pad, ch - 38 - barH - 16);
        _close.Frame = new CGRect(cw - pad - 100, ch - barH - 8, 100, barH);
    }

    void Fill(List<BugReportBridge.MineRow> rows, int queued)
    {
        string waiting = queued > 0 ? $"아직 못 보낸 것 {queued}건" : "";
        if (rows == null)
        {
            _list.Text = "불러오지 못했습니다. 인터넷 연결을 확인해 주세요.";
            _status.Text = waiting;
            return;
        }
        _status.Text = $"{rows.Count}건" + (waiting.Length > 0 ? " · " + waiting : "");
        if (rows.Count == 0) { _list.Text = "아직 보내신 리포트가 없습니다."; return; }

        var s = new NSMutableAttributedString();
        void Add(string text, UIColor color, bool bold = false) =>
            s.Append(new NSAttributedString(text, new UIStringAttributes
            {
                ForegroundColor = color,
                Font = bold ? UIFont.BoldSystemFontOfSize(14) : UIFont.SystemFontOfSize(14),
            }));

        foreach (var b in rows)
        {
            string when = DateTime.TryParse(b.CreatedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime at)
                ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : b.CreatedAt;
            Add($"#{b.Id}  ", UIColor.White, true);
            Add(BugReportBridge.StatusName(b.Status), b.Status == "fixed" ? BugUi.Blue : UIColor.White, true);
            Add($"  ·  {when}" + (b.Version.Length > 0 ? $"  ·  v{b.Version}" : "") + "\n", UIColor.LightGray);
            Add((b.Text.Length >= 300 ? b.Text + "…" : b.Text) + "\n", UIColor.White);
            if (!string.IsNullOrEmpty(b.Reply)) Add("답: " + b.Reply + "\n", BugUi.Blue, true);
            Add("\n", UIColor.White);
        }
        _list.AttributedText = s;
        _list.ScrollRangeToVisible(new NSRange(0, 0));
    }
}
