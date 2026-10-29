using System;
using CoreGraphics;
using Foundation;
using UIKit;

namespace WogiOS;

internal static class PadStyle
{
    static UIImpactFeedbackGenerator _gen;

    public static UIColor C(int r, int g, int b, float a = 1f) => UIColor.FromRGBA(r / 255f, g / 255f, b / 255f, a);
    public static UIColor Border => UIColor.FromWhiteAlpha(0.78f, 1f);

    public static void Tap()
    {
        _gen ??= new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Light);
        _gen.ImpactOccurred();
    }
}

// 버튼 하나 (LT/LB/RT/RB/XYAB/Back/Start). 누르는 동안 해당 비트를 켠다.
internal sealed class PadButton : UIView
{
    readonly int _bit;
    readonly UILabel _label;
    int _touches;
    bool _down;
    float _base = 0.8f;

    public PadButton(string text, int bit, UIColor fill, float fontSize = 20)
    {
        _bit = bit;
        MultipleTouchEnabled = true;
        UserInteractionEnabled = true;
        BackgroundColor = fill;
        Alpha = _base;
        Layer.BorderWidth = 3;
        Layer.BorderColor = PadStyle.Border.CGColor;
        _label = new UILabel
        {
            Text = text,
            TextColor = UIColor.White,
            TextAlignment = UITextAlignment.Center,
            Font = UIFont.BoldSystemFontOfSize(fontSize),
            UserInteractionEnabled = false,
        };
        AddSubview(_label);
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _label.Frame = Bounds;
        Layer.CornerRadius = Bounds.Height / 2;
    }

    void Down()
    {
        if (_down) return;
        _down = true;
        Alpha = Math.Min(1f, _base + 0.3f);
        PadInput.SetBits(0, _bit);
        PadStyle.Tap();
    }

    void Up()
    {
        if (!_down) return;
        _down = false;
        Alpha = _base;
        PadInput.SetBits(_bit, 0);
    }

    // 평소(안 눌렀을 때) 투명도. 눌리면 조금 더 진해진다
    public void SetBaseAlpha(float a)
    {
        _base = a;
        Alpha = _down ? Math.Min(1f, a + 0.3f) : a;
    }

    public void Reset() { _touches = 0; Up(); }

    public override void TouchesBegan(NSSet touches, UIEvent evt)
    {
        _touches += (int)touches.Count;
        if (_touches > 0) Down();
    }

    void Release(NSSet touches)
    {
        _touches = Math.Max(0, _touches - (int)touches.Count);
        if (_touches == 0) Up();
    }

    public override void TouchesEnded(NSSet touches, UIEvent evt) => Release(touches);
    public override void TouchesCancelled(NSSet touches, UIEvent evt) => Release(touches);
}

// 십자 방향키. 손가락 위치로 8방향을 판단한다.
internal sealed class DPadView : UIView
{
    readonly UIView _up = Arm(), _down = Arm(), _left = Arm(), _right = Arm(), _mid = Arm();
    UITouch _tracked;
    int _mask;

    static UIView Arm() => new UIView { BackgroundColor = PadStyle.C(120, 120, 135, 0.55f), UserInteractionEnabled = false };

    public DPadView()
    {
        MultipleTouchEnabled = true;
        UserInteractionEnabled = true;
        BackgroundColor = PadStyle.C(0, 0, 0, 0.25f);
        AddSubview(_mid); AddSubview(_up); AddSubview(_down); AddSubview(_left); AddSubview(_right);
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        nfloat s = Bounds.Width, a = s / 3;
        Layer.CornerRadius = s / 2;
        _mid.Frame = new CGRect(a, a, a, a);
        _up.Frame = new CGRect(a, 0, a, a);
        _down.Frame = new CGRect(a, 2 * a, a, a);
        _left.Frame = new CGRect(0, a, a, a);
        _right.Frame = new CGRect(2 * a, a, a, a);
    }

    void Apply(int m)
    {
        if (m == _mask) return;
        bool newDir = (m & ~_mask) != 0;
        _mask = m;
        PadInput.SetBits(PadInput.DAll, m);
        Tint(_up, PadInput.DUp); Tint(_down, PadInput.DDown); Tint(_left, PadInput.DLeft); Tint(_right, PadInput.DRight);
        if (newDir) PadStyle.Tap();
    }

    void Tint(UIView v, int bit) =>
        v.BackgroundColor = (_mask & bit) != 0 ? PadStyle.C(200, 200, 215, 0.9f) : PadStyle.C(120, 120, 135, 0.55f);

    void Update(UITouch t)
    {
        var p = t.LocationInView(this);
        nfloat cx = Bounds.Width / 2, cy = Bounds.Height / 2, dz = Bounds.Width * 0.12f;
        int m = 0;
        if (p.X - cx > dz) m |= PadInput.DRight; else if (cx - p.X > dz) m |= PadInput.DLeft;
        if (p.Y - cy > dz) m |= PadInput.DDown; else if (cy - p.Y > dz) m |= PadInput.DUp;
        Apply(m);
    }

    public void Reset() { _tracked = null; Apply(0); }

    public override void TouchesBegan(NSSet touches, UIEvent evt)
    {
        if (_tracked == null)
        {
            _tracked = touches.AnyObject as UITouch;
            if (_tracked != null) Update(_tracked);
        }
    }

    public override void TouchesMoved(NSSet touches, UIEvent evt)
    {
        if (_tracked != null && touches.Contains(_tracked)) Update(_tracked);
    }

    public override void TouchesEnded(NSSet touches, UIEvent evt)
    {
        if (_tracked != null && touches.Contains(_tracked)) Reset();
    }

    public override void TouchesCancelled(NSSet touches, UIEvent evt) => TouchesEnded(touches, evt);
}

// 게임 화면 위에 올라가는 화면 패드 전체. 버튼이 없는 곳의 터치는 게임으로 그대로 통과시킨다.
internal sealed class PadOverlay : UIView
{
    readonly PadButton _lt, _lb, _rt, _rb, _a, _b, _x, _y, _back, _start;
    readonly DPadView _dpad;

    public PadOverlay()
    {
        BackgroundColor = UIColor.Clear;
        MultipleTouchEnabled = true;
        var navy = PadStyle.C(28, 36, 58);
        _lt = new PadButton("LT", PadInput.LTrigger, navy);
        _lb = new PadButton("LB", PadInput.LB, navy);
        _rt = new PadButton("RT", PadInput.RTrigger, navy);
        _rb = new PadButton("RB", PadInput.RB, navy);
        _y = new PadButton("Y", PadInput.BtnY, PadStyle.C(107, 85, 16), 26);
        _x = new PadButton("X", PadInput.BtnX, PadStyle.C(27, 47, 107), 26);
        _b = new PadButton("B", PadInput.BtnB, PadStyle.C(122, 28, 28), 26);
        _a = new PadButton("A", PadInput.BtnA, PadStyle.C(28, 90, 44), 26);
        _back = new PadButton("◁", PadInput.Back, navy, 22);
        _start = new PadButton("≡", PadInput.Start, navy, 22);
        _dpad = new DPadView();
        foreach (var v in new UIView[] { _dpad, _lt, _lb, _rt, _rb, _y, _x, _b, _a, _back, _start }) AddSubview(v);
    }

    public override UIView HitTest(CGPoint point, UIEvent uievent)
    {
        var v = base.HitTest(point, uievent);
        return v == this ? null : v;
    }

    public void SetOpacity(float a)
    {
        foreach (var b in new[] { _lt, _lb, _rt, _rb, _a, _b, _x, _y, _back, _start }) b.SetBaseAlpha(a);
        _dpad.Alpha = Math.Min(1f, a / 0.8f);
    }

    public void Show(bool on)
    {
        Hidden = !on;
        if (on) return;
        foreach (var b in new[] { _lt, _lb, _rt, _rb, _a, _b, _x, _y, _back, _start }) b.Reset();
        _dpad.Reset();
        PadInput.ReleaseAll();
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var ins = SafeAreaInsets;
        nfloat w = Bounds.Width, h = Bounds.Height, m = 20;
        nfloat l = ins.Left + m, r = w - ins.Right - m, t = ins.Top + m, bt = h - ins.Bottom - m;

        // 숄더/트리거 (왼쪽 위 LT 위, LB 아래 / 오른쪽 위 RT 위, RB 아래)
        nfloat bw = 88, bh = 46, gap = 10;
        _lt.Frame = new CGRect(l, t, bw, bh);
        _lb.Frame = new CGRect(l, t + bh + gap, bw, bh);
        _rt.Frame = new CGRect(r - bw, t, bw, bh);
        _rb.Frame = new CGRect(r - bw, t + bh + gap, bw, bh);

        // 방향키 (왼쪽 아래)
        nfloat ds = 150;
        _dpad.Frame = new CGRect(l + 10, bt - ds, ds, ds);

        // XYAB (오른쪽 아래, 마름모 배치)
        nfloat d = 58, rad = 46;
        nfloat cx = r - d / 2 - rad - 4, cy = bt - d / 2 - rad - 4;
        _y.Frame = new CGRect(cx - d / 2, cy - rad - d / 2, d, d);
        _a.Frame = new CGRect(cx - d / 2, cy + rad - d / 2, d, d);
        _x.Frame = new CGRect(cx - rad - d / 2, cy - d / 2, d, d);
        _b.Frame = new CGRect(cx + rad - d / 2, cy - d / 2, d, d);

        // Back / Start (아래 가운데 양옆)
        nfloat sw = 60, sh = 42;
        _back.Frame = new CGRect(w / 2 - 110 - sw / 2, bt - sh, sw, sh);
        _start.Frame = new CGRect(w / 2 + 110 - sw / 2, bt - sh, sw, sh);
    }
}
