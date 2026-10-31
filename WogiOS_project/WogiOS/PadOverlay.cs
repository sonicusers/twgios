using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
    readonly UILabel _sub;      // 글자 아래 작은 기능 설명 (없으면 null)
    readonly bool _rounded;     // true 면 둥근 사각형, false 면 원/알약
    readonly float _fontSize;
    public readonly float BorderW;
    readonly UIColor _fill;
    static readonly UIColor PressTint = UIColor.FromRGBA(90 / 255f, 80 / 255f, 170 / 255f, 0.75f);
    int _touches;
    bool _down;
    float _base = 0.8f;

    public PadButton(string text, int bit, UIColor fill, float fontSize = 20, string sub = null, bool rounded = false, float borderW = 3)
    {
        BorderW = borderW;
        _fill = fill;
        _bit = bit;
        _fontSize = fontSize;
        _rounded = rounded;
        MultipleTouchEnabled = true;
        UserInteractionEnabled = true;
        BackgroundColor = fill;
        Alpha = _base;
        Layer.BorderWidth = borderW;
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
        if (!string.IsNullOrEmpty(sub))
        {
            _sub = new UILabel
            {
                Text = sub,
                TextColor = UIColor.FromWhiteAlpha(0.85f, 1f),
                TextAlignment = UITextAlignment.Center,
                Font = UIFont.BoldSystemFontOfSize(SubSize(1f)),
                UserInteractionEnabled = false,
            };
            AddSubview(_sub);
        }
    }

    float SubSize(float scale) => Math.Max(8f, _fontSize * 0.5f) * scale;

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        if (_sub != null)
        {
            // 큰 글자 + 작은 설명을 세로 가운데로 쌓는다
            nfloat mh = _label.Font.LineHeight, sh = _sub.Font.LineHeight;
            nfloat y0 = (Bounds.Height - (mh + sh)) / 2;
            _label.Frame = new CGRect(0, y0, Bounds.Width, mh);
            _sub.Frame = new CGRect(0, y0 + mh - 2, Bounds.Width, sh);
        }
        else _label.Frame = Bounds;
        Layer.CornerRadius = _rounded ? (nfloat)Math.Min(14, Bounds.Height / 3) : Bounds.Height / 2;
    }

    void Down()
    {
        if (_down) return;
        _down = true;
        Alpha = Math.Min(1f, _base + 0.3f);
        if (_fill.CGColor.Alpha < 0.01f) BackgroundColor = PressTint;   // 투명 버튼은 누를 때만 살짝 색이 찬다
        PadInput.SetBits(0, _bit);
        PadStyle.Tap();
    }

    void Up()
    {
        if (!_down) return;
        _down = false;
        Alpha = _base;
        BackgroundColor = _fill;
        PadInput.SetBits(_bit, 0);
    }

    // 평소(안 눌렀을 때) 투명도. 눌리면 조금 더 진해진다
    public void SetBaseAlpha(float a)
    {
        _base = a;
        Alpha = _down ? Math.Min(1f, a + 0.3f) : a;
    }

    public void ApplyScale(float scale)
    {
        _label.Font = UIFont.BoldSystemFontOfSize(_fontSize * scale);
        if (_sub != null) _sub.Font = UIFont.BoldSystemFontOfSize(SubSize(scale));
        SetNeedsLayout();
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
// 편집 모드에서는 모든 터치를 이 뷰가 받아서 요소를 끌어 옮기고(한 손가락) 크기를 바꾼다(두 손가락 벌리기).
internal sealed class PadOverlay : UIView
{
    sealed class Slot { public float X, Y, S = 1f; }   // 중심 위치(화면 비율), 크기 배율

    static readonly string[] Ids = { "dpad", "lt", "lb", "rt", "rb", "y", "x", "b", "a", "back", "start" };
    const string PrefKey = "padLayout";

    readonly Dictionary<string, UIView> _els = new();
    readonly Dictionary<string, Slot> _slots = new();
    readonly DPadView _dpad;
    readonly UILabel _hint;
    readonly UIButton _resetBtn, _doneBtn;
    bool _editing;
    string _sel;

    public event Action EditEnded;
    public bool Editing => _editing;

    public PadOverlay()
    {
        BackgroundColor = UIColor.Clear;
        MultipleTouchEnabled = true;
        var navy = PadStyle.C(28, 36, 58);
        var gray = UIColor.Clear;   // XYAB 는 속이 빈 투명 버튼 (테두리 + 글자만)
        _dpad = new DPadView();
        _els["dpad"] = _dpad;
        // 세 번째 인자 뒤: (글자 크기, 아래 설명, 둥근사각형 여부). 설명을 빼면 예전 모양 그대로.
        _els["lt"] = new PadButton("LT", PadInput.LTrigger, navy, 11, "어빌", true, 2);
        _els["lb"] = new PadButton("LB", PadInput.LB, navy, 11, "이전", true, 2);
        _els["rt"] = new PadButton("RT", PadInput.RTrigger, navy, 11, "공격", true, 2);
        _els["rb"] = new PadButton("RB", PadInput.RB, navy, 11, "다음", true, 2);
        _els["y"] = new PadButton("Y", PadInput.BtnY, gray, 24, "휴식", false, 2);
        _els["x"] = new PadButton("X", PadInput.BtnX, gray, 24, "링", false, 2);
        _els["b"] = new PadButton("B", PadInput.BtnB, gray, 24, "취소", false, 2);
        _els["a"] = new PadButton("A", PadInput.BtnA, gray, 24, "확인", false, 2);
        _els["back"] = new PadButton("◁ 상태", PadInput.Back, navy, 9, null, true, 2);
        _els["start"] = new PadButton("≡ 시스템", PadInput.Start, navy, 9, null, true, 2);
        foreach (var id in Ids) AddSubview(_els[id]);

        // 편집 모드 전용 UI
        _hint = new UILabel
        {
            Text = "끌어서 이동 · 두 손가락으로 크기 조절",
            TextColor = UIColor.White,
            Font = UIFont.SystemFontOfSize(13),
            TextAlignment = UITextAlignment.Center,
            BackgroundColor = PadStyle.C(0, 0, 0, 0.6f),
            Hidden = true,
        };
        _hint.Layer.CornerRadius = 13;
        _hint.Layer.MasksToBounds = true;
        AddSubview(_hint);

        _resetBtn = MakeButton("초기화");
        _doneBtn = MakeButton("완료");
        _resetBtn.TouchUpInside += (s, e) =>
        {
            _slots.Clear();
            NSUserDefaults.StandardUserDefaults.RemoveObject(PrefKey);
            SetNeedsLayout();
        };
        _doneBtn.TouchUpInside += (s, e) =>
        {
            SetEditing(false);
            EditEnded?.Invoke();
        };
        AddSubview(_resetBtn);
        AddSubview(_doneBtn);

        var pan = new UIPanGestureRecognizer(OnPan) { MaximumNumberOfTouches = 1 };
        var pinch = new UIPinchGestureRecognizer(OnPinch);
        var tap = new UITapGestureRecognizer(OnTap);
        foreach (UIGestureRecognizer g in new UIGestureRecognizer[] { pan, pinch, tap })
        {
            g.ShouldReceiveTouch = (r, t) => _editing && !(t.View is UIButton);
            AddGestureRecognizer(g);
        }

        Load();
    }

    static UIButton MakeButton(string title)
    {
        var b = UIButton.FromType(UIButtonType.System);
        b.SetTitle(title, UIControlState.Normal);
        b.SetTitleColor(UIColor.White, UIControlState.Normal);
        b.TitleLabel.Font = UIFont.BoldSystemFontOfSize(15);
        b.BackgroundColor = PadStyle.C(28, 36, 58, 0.9f);
        b.Layer.CornerRadius = 17;
        b.Layer.BorderWidth = 2;
        b.Layer.BorderColor = PadStyle.Border.CGColor;
        b.Hidden = true;
        return b;
    }

    public override UIView HitTest(CGPoint point, UIEvent uievent)
    {
        var v = base.HitTest(point, uievent);
        if (!_editing) return v == this ? null : v;
        // 편집 중에는 패드 버튼이 눌리지 않게 모든 터치를 이 뷰가 받는다 (초기화/완료 버튼만 예외)
        return v is UIButton ? v : this;
    }

    public void SetOpacity(float a)
    {
        foreach (var v in _els.Values)
            if (v is PadButton b) b.SetBaseAlpha(a);
        _dpad.Alpha = Math.Min(1f, a / 0.8f);
    }

    public void Show(bool on)
    {
        Hidden = !on;
        if (on) return;
        SetEditing(false);
        ReleaseAllElements();
    }

    void ReleaseAllElements()
    {
        foreach (var v in _els.Values)
            if (v is PadButton b) b.Reset();
        _dpad.Reset();
        PadInput.ReleaseAll();
    }

    public void SetEditing(bool on)
    {
        if (_editing == on) return;
        _editing = on;
        if (on) ReleaseAllElements();
        else { Select(null); Save(); }
        _hint.Hidden = _resetBtn.Hidden = _doneBtn.Hidden = !on;
    }

    // ---- 선택 / 편집 ----
    void Select(string id)
    {
        if (_sel != null) Highlight(_sel, false);
        _sel = id;
        if (_sel != null) Highlight(_sel, true);
    }

    void Highlight(string id, bool on)
    {
        var v = _els[id];
        if (on)
        {
            v.Layer.BorderColor = PadStyle.C(255, 220, 60).CGColor;
            v.Layer.BorderWidth = 5;
        }
        else
        {
            v.Layer.BorderColor = PadStyle.Border.CGColor;
            v.Layer.BorderWidth = v is PadButton pb ? pb.BorderW : 0;
        }
    }

    string ElementAt(CGPoint p)
    {
        string best = null;
        double bd = double.MaxValue;
        foreach (var id in Ids)
        {
            var v = _els[id];
            var f = v.Frame;
            var padded = new CGRect(f.X - 12, f.Y - 12, f.Width + 24, f.Height + 24);
            if (!padded.Contains(p)) continue;
            double dx = v.Center.X - p.X, dy = v.Center.Y - p.Y;
            double d = dx * dx + dy * dy;
            if (d < bd) { bd = d; best = id; }
        }
        return best;
    }

    Slot EnsureSlot(string id)
    {
        if (!_slots.TryGetValue(id, out var s))
        {
            var c = _els[id].Center;
            s = new Slot { X = (float)(c.X / Bounds.Width), Y = (float)(c.Y / Bounds.Height), S = 1f };
            _slots[id] = s;
        }
        return s;
    }

    void OnTap(UITapGestureRecognizer g)
    {
        if (!_editing) return;
        Select(ElementAt(g.LocationInView(this)));
    }

    void OnPan(UIPanGestureRecognizer g)
    {
        if (!_editing) return;
        var tr = g.TranslationInView(this);
        switch (g.State)
        {
            case UIGestureRecognizerState.Began:
                var p = g.LocationInView(this);
                // 인식이 시작된 위치가 아니라 처음 손가락을 댄 위치로 요소를 찾는다
                var start = new CGPoint(p.X - tr.X, p.Y - tr.Y);
                var id = ElementAt(start);
                if (id != null) Select(id);
                break;
            case UIGestureRecognizerState.Changed:
                if (_sel == null) break;
                var s = EnsureSlot(_sel);
                s.X = Math.Clamp(s.X + (float)(tr.X / Bounds.Width), 0.02f, 0.98f);
                s.Y = Math.Clamp(s.Y + (float)(tr.Y / Bounds.Height), 0.02f, 0.98f);
                g.SetTranslation(new CGPoint(0, 0), this);
                SetNeedsLayout();
                break;
            case UIGestureRecognizerState.Ended:
            case UIGestureRecognizerState.Cancelled:
                Save();
                break;
        }
    }

    void OnPinch(UIPinchGestureRecognizer g)
    {
        if (!_editing) return;
        switch (g.State)
        {
            case UIGestureRecognizerState.Began:
                var id = ElementAt(g.LocationInView(this));
                if (id != null) Select(id);
                break;
            case UIGestureRecognizerState.Changed:
                if (_sel == null) break;
                var s = EnsureSlot(_sel);
                s.S = Math.Clamp(s.S * (float)g.Scale, 0.5f, 2.5f);
                g.Scale = 1;
                SetNeedsLayout();
                break;
            case UIGestureRecognizerState.Ended:
            case UIGestureRecognizerState.Cancelled:
                Save();
                break;
        }
    }

    // ---- 저장 / 불러오기 ----
    void Save()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        foreach (var kv in _slots)
            sb.Append(kv.Key).Append(':')
              .Append(kv.Value.X.ToString("0.####", ci)).Append(',')
              .Append(kv.Value.Y.ToString("0.####", ci)).Append(',')
              .Append(kv.Value.S.ToString("0.###", ci)).Append(';');
        NSUserDefaults.StandardUserDefaults.SetString(sb.ToString(), PrefKey);
    }

    void Load()
    {
        var str = NSUserDefaults.StandardUserDefaults.StringForKey(PrefKey);
        if (string.IsNullOrEmpty(str)) return;
        var ci = CultureInfo.InvariantCulture;
        foreach (var part in str.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length != 2 || !_els.ContainsKey(kv[0])) continue;
            var n = kv[1].Split(',');
            if (n.Length != 3) continue;
            if (float.TryParse(n[0], NumberStyles.Float, ci, out var x) &&
                float.TryParse(n[1], NumberStyles.Float, ci, out var y) &&
                float.TryParse(n[2], NumberStyles.Float, ci, out var sc))
                _slots[kv[0]] = new Slot { X = Math.Clamp(x, 0.02f, 0.98f), Y = Math.Clamp(y, 0.02f, 0.98f), S = Math.Clamp(sc, 0.5f, 2.5f) };
        }
    }

    // ---- 배치 ----
    // 기본 배치 (저장된 값이 없는 요소에 쓴다)
    Dictionary<string, CGRect> Defaults()
    {
        var d = new Dictionary<string, CGRect>();
        var ins = SafeAreaInsets;
        nfloat w = Bounds.Width, h = Bounds.Height, m = 20;
        nfloat l = ins.Left + m, r = w - ins.Right - m, t = ins.Top + m, bt = h - ins.Bottom - m;

        nfloat bw = 48, bh = 30, gap = 8;
        d["lt"] = new CGRect(l, t, bw, bh);
        d["lb"] = new CGRect(l, t + bh + gap, bw, bh);
        d["rt"] = new CGRect(r - bw, t, bw, bh);
        d["rb"] = new CGRect(r - bw, t + bh + gap, bw, bh);

        // 상태/시스템 버튼은 십자키·A 버튼 바로 아래에 둔다. 그만큼 위쪽을 비워 둔다.
        nfloat sw = 54, sh = 22, sgap = 8;
        nfloat low = sh + sgap;

        nfloat ds = 150;
        d["dpad"] = new CGRect(l + 10, bt - low - ds, ds, ds);

        nfloat db = 58, rad = 46;
        nfloat cx = r - db / 2 - rad - 4, cy = bt - low - db / 2 - rad - 4;
        d["y"] = new CGRect(cx - db / 2, cy - rad - db / 2, db, db);
        d["a"] = new CGRect(cx - db / 2, cy + rad - db / 2, db, db);
        d["x"] = new CGRect(cx - rad - db / 2, cy - db / 2, db, db);
        d["b"] = new CGRect(cx + rad - db / 2, cy - db / 2, db, db);

        d["back"] = new CGRect(l + 10 + ds / 2 - sw / 2, bt - sh, sw, sh);   // 십자키 아래
        d["start"] = new CGRect(cx - sw / 2, bt - sh, sw, sh);              // A 버튼 아래
        return d;
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var def = Defaults();
        foreach (var id in Ids)
        {
            var v = _els[id];
            var f = def[id];
            float scale = 1f;
            if (_slots.TryGetValue(id, out var sl))
            {
                scale = sl.S;
                nfloat w = f.Width * sl.S, h = f.Height * sl.S;
                nfloat cx = sl.X * Bounds.Width, cy = sl.Y * Bounds.Height;
                f = new CGRect(cx - w / 2, cy - h / 2, w, h);
            }
            v.Frame = f;
            if (v is PadButton pb) pb.ApplyScale(scale);
        }

        var ins = SafeAreaInsets;
        nfloat ty = ins.Top + 6;
        nfloat bwid = Bounds.Width;
        _resetBtn.Frame = new CGRect(bwid / 2 - 78, ty, 72, 34);
        _doneBtn.Frame = new CGRect(bwid / 2 + 6, ty, 72, 34);
        _hint.Frame = new CGRect((bwid - 300) / 2, ty + 42, 300, 26);
    }
}
