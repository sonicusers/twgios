using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using UIKit;
using WogPatcher;
using WogShim;
using WogShim.Win32;
using MenuEntry = WogShim.Win32.User32.MenuEntry;   // User32 안에 들어 있는 형식

namespace WogiOS;

// Ios 0.20 의 "단축키 모드"(ControlsOverlay / Key / TextBar / GameMenu)를 UIKit 으로 옮긴 것.
// 화면 위에 키보드 단축키 버튼을 놓고, 누르면 게임에 키 입력(WogShim.Win32.Messages.PostKey)을 보낸다.
// 기존 PadOverlay(LT/LB/XYAB 화면 패드)는 그대로 두고, 위쪽 [단축키|패드|숨김] 탭으로 모드를 고른다.

internal enum ControlMode { Keys, Pad, Hidden }

internal enum KeyKind { Plain, Accent, Ability, Faint }

// 버튼 하나. 누르는 동안 Pressed(true), 떼면 Pressed(false)
internal sealed class ShortcutKey : UIView
{
    readonly UILabel _label = new UILabel();
    readonly UILabel _hint = new UILabel();
    int _touches;
    bool _isDown;

    public string Label { get; private set; } = "";
    public string Hint { get; private set; } = "";
    public KeyKind Kind { get; private set; }
    public bool Dim { get; private set; }
    public bool Round { get; set; }
    public Action<bool> Pressed { get; set; }

    // 글자 대신 직접 그린 불투명도 아이콘(◐). 글자 아이콘은 폰트 때문에 아래로 치우쳐서 정중앙에 그린다
    readonly CAShapeLayer _iconRing = new CAShapeLayer();
    readonly CAShapeLayer _iconHalf = new CAShapeLayer();
    bool _opacityIcon;
    public bool OpacityIcon
    {
        get => _opacityIcon;
        set
        {
            if (_opacityIcon == value) return;
            _opacityIcon = value;
            if (value)
            {
                _iconRing.FillColor = null;
                _iconRing.LineWidth = 1.4f;
                Layer.AddSublayer(_iconRing);
                Layer.AddSublayer(_iconHalf);
            }
            else { _iconRing.RemoveFromSuperLayer(); _iconHalf.RemoveFromSuperLayer(); }
            _label.Hidden = value;
            SetNeedsLayout();
        }
    }

    // 터치 UI 불투명도 (상단 버튼줄은 건드리지 않고 단축키 버튼에만 적용)
    float _baseAlpha = 1f;
    public float BaseAlpha
    {
        get => _baseAlpha;
        set { if (Math.Abs(_baseAlpha - value) < 0.001f) return; _baseAlpha = value; Refresh(); }
    }

    // 글자가 버튼 폭보다 길면 잘라내지 않고 두 줄로 나눠 쓴다 (띄어쓰기가 있으면 거기서, 없으면 가운데에서)
    string _shown;
    static string SplitTwo(string t)
    {
        int mid = t.Length / 2, best = -1;
        for (int i = 1; i < t.Length - 1; i++)
            if (t[i] == ' ' && (best < 0 || Math.Abs(i - mid) < Math.Abs(best - mid))) best = i;
        if (best >= 0) return t.Substring(0, best) + "\n" + t.Substring(best + 1);
        int cut = (t.Length + 1) / 2;
        return t.Substring(0, cut) + "\n" + t.Substring(cut);
    }

    public ShortcutKey(string label, string hint = "", KeyKind kind = KeyKind.Plain)
    {
        MultipleTouchEnabled = true;
        UserInteractionEnabled = true;
        Layer.BorderWidth = 1.2f;
        ClipsToBounds = true;
        _label.TextAlignment = UITextAlignment.Center;
        _label.AdjustsFontSizeToFitWidth = true;
        _label.MinimumScaleFactor = 0.5f;
        _label.UserInteractionEnabled = false;
        _hint.TextAlignment = UITextAlignment.Center;
        _hint.AdjustsFontSizeToFitWidth = true;
        _hint.MinimumScaleFactor = 0.5f;
        _hint.UserInteractionEnabled = false;
        AddSubview(_label);
        AddSubview(_hint);
        Set(label, hint, kind);
    }

    public void Set(string label, string hint, KeyKind kind, bool dim = false)
    {
        if (Label == label && Hint == hint && Kind == kind && Dim == dim) return;
        Label = label; Hint = hint; Kind = kind; Dim = dim; _shown = null;
        Refresh();
        SetNeedsLayout();
    }

    void Refresh()
    {
        UIColor fill, ring;
        switch (Kind)
        {
            case KeyKind.Accent:
                fill = PadStyle.C(127, 119, 221, 90 / 255f); ring = PadStyle.C(175, 169, 236, 205 / 255f); break;
            case KeyKind.Ability:
                fill = PadStyle.C(29, 158, 117, 77 / 255f); ring = PadStyle.C(93, 202, 165, 215 / 255f); break;
            case KeyKind.Faint:
                fill = PadStyle.C(255, 255, 255, 14 / 255f); ring = PadStyle.C(255, 255, 255, 46 / 255f); break;
            default:
                fill = PadStyle.C(255, 255, 255, 20 / 255f); ring = PadStyle.C(255, 255, 255, 56 / 255f); break;
        }
        BackgroundColor = _isDown ? PadStyle.C(175, 169, 236, 150 / 255f) : fill;
        Layer.BorderColor = ring.CGColor;
        _label.Text = _shown ?? Label;
        _hint.Text = Hint;
        _label.TextColor = PadStyle.C(241, 238, 250, 235 / 255f);
        _hint.TextColor = PadStyle.C(169, 163, 187, 200 / 255f);
        Alpha = (Dim ? 0.43f : 1f) * _baseAlpha;
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        float w = (float)Bounds.Width, h = (float)Bounds.Height;
        Layer.CornerRadius = Round ? Math.Min(w, h) / 2f : 9f;
        bool showHint = Hint.Length > 0 && h > 30f;
        float labelSize = Math.Min(13f, Math.Min(h * (showHint ? 0.36f : 0.45f), w / Math.Max(2.2f, Label.Length * 0.95f)));
        string text = Label;
        bool wrap = false;
        if (!Round && !_opacityIcon && Label.Length >= 3 && h >= 28f)
        {
            float baseSize = Math.Min(13f, h * (showHint ? 0.36f : 0.45f));   // 폭에 맞춰 줄이기 전의 글자 크기로 넘치는지 본다
            var f0 = Kind == KeyKind.Faint ? UIFont.SystemFontOfSize(baseSize) : UIFont.BoldSystemFontOfSize(baseSize);
            float tw = (float)((NSString)Label).GetSizeUsingAttributes(new UIStringAttributes { Font = f0 }).Width;
            if (tw > w - 8f) { wrap = true; text = SplitTwo(Label); }
        }
        if (wrap)
        {
            showHint = false;   // 두 줄로 나눈 버튼은 아래 작은 힌트 글자를 숨긴다
            int nl = text.IndexOf('\n');
            int per = Math.Max(nl, text.Length - nl - 1);
            labelSize = Math.Max(9f, Math.Min(13f, Math.Min((w - 8f) / (per * 1.05f), (h - 6f) / 2.5f)));
        }
        _shown = wrap ? text : null;
        _label.Lines = wrap ? 2 : 1;
        _label.AdjustsFontSizeToFitWidth = !wrap;
        _label.Text = text;
        _label.Font = Kind == KeyKind.Faint ? UIFont.SystemFontOfSize(labelSize) : UIFont.BoldSystemFontOfSize(labelSize);
        _hint.Font = UIFont.SystemFontOfSize(Math.Min(10.5f, h * 0.26f));
        _hint.Hidden = !showHint;
        if (_opacityIcon)
        {
            float r = Math.Min(8f, Math.Min(w, h) * 0.3f);
            var c = new CGPoint(w / 2f, h / 2f);
            var color = PadStyle.C(241, 238, 250, 235 / 255f).CGColor;
            _iconRing.Frame = Bounds; _iconHalf.Frame = Bounds;
            _iconRing.Path = UIBezierPath.FromOval(new CGRect(c.X - r, c.Y - r, r * 2, r * 2)).CGPath;
            _iconRing.StrokeColor = color;
            var half = UIBezierPath.FromArc(c, r, (nfloat)(Math.PI / 2), (nfloat)(Math.PI * 1.5), true);   // 왼쪽 반원
            half.ClosePath();
            _iconHalf.Path = half.CGPath;
            _iconHalf.FillColor = color;
            _iconHalf.StrokeColor = null;
        }
        if (showHint)
        {
            _label.Frame = new CGRect(3, 0, w - 6, h * 0.62f);
            _hint.Frame = new CGRect(3, h * 0.55f, w - 6, h * 0.40f);
        }
        else
        {
            _label.Frame = new CGRect(3, 0, w - 6, h);
        }
    }

    void SetDown(bool down)
    {
        if (_isDown == down) return;
        _isDown = down;
        Refresh();
        if (down) PadStyle.Tap();
        Pressed?.Invoke(down);
    }

    public void Reset() { _touches = 0; SetDown(false); }

    public override void TouchesBegan(NSSet touches, UIEvent evt)
    {
        _touches += (int)touches.Count;
        if (_touches > 0) SetDown(true);
    }

    void Release(NSSet touches)
    {
        _touches = Math.Max(0, _touches - (int)touches.Count);
        if (_touches == 0) SetDown(false);
    }

    public override void TouchesEnded(NSSet touches, UIEvent evt) => Release(touches);
    public override void TouchesCancelled(NSSet touches, UIEvent evt) => Release(touches);
}

// 에디터/검색창에 글자를 칠 때 나오는 입력칸. 친 글자를 게임에 WM_CHAR 로 보낸다.
internal sealed class ShortcutTextBar : UITextField
{
    string _sent = "";
    bool _syncing;
    int _max = 20;

    public ShortcutTextBar()
    {
        Placeholder = "검색어 — 눌러서 입력";
        TextColor = PadStyle.C(241, 238, 250, 240 / 255f);
        Font = UIFont.SystemFontOfSize(14);
        BackgroundColor = PadStyle.C(20, 24, 44, 225 / 255f);
        Layer.BorderWidth = 1.5f;
        Layer.BorderColor = PadStyle.C(175, 169, 236, 220 / 255f).CGColor;
        Layer.CornerRadius = 9;
        LeftView = new UIView(new CGRect(0, 0, 12, 10));
        LeftViewMode = UITextFieldViewMode.Always;
        ReturnKeyType = UIReturnKeyType.Done;
        AutocorrectionType = UITextAutocorrectionType.No;
        AutocapitalizationType = UITextAutocapitalizationType.None;
        SpellCheckingType = UITextSpellCheckingType.No;
        ShouldReturn = tf => { Done(); return true; };
        ShouldChangeCharacters = (tf, range, replacement) =>
        {
            int len = (tf.Text ?? "").Length - (int)range.Length + (replacement ?? "").Length;
            return len <= _max;
        };
        EditingChanged += (s, e) =>
        {
            if (_syncing) return;
            string text = Text ?? "";
            int i = 0;
            while (i < text.Length && i < _sent.Length && text[i] == _sent[i]) i++;
            for (int j = i; j < _sent.Length; j++) WogShim.Win32.Messages.PostChar('\b');
            for (int k = i; k < text.Length; k++) WogShim.Win32.Messages.PostChar(text[k]);
            _sent = text;
        };
    }

    public void Sync(string gameText, int max)
    {
        _max = max;
        if (!IsFirstResponder && gameText != _sent)
        {
            _syncing = true;
            Text = gameText;
            _syncing = false;
            _sent = gameText;
        }
    }

    public void Done() => ResignFirstResponder();
}

internal sealed class ShortcutOverlay : UIView
{
    readonly struct Lay
    {
        public readonly float W, H, Side;
        public readonly CGRect Game;
        public readonly bool Wide;
        public readonly bool Tablet;   // 4:3 에 가까운 태블릿: 단축키를 양옆 검은 여백에 한 줄로 세운다
        public readonly float InsTop, InsLeft, InsRight, InsBottom;
        public Lay(float w, float h, CGRect game, bool wide, bool tablet, float side, UIEdgeInsets ins)
        {
            W = w; H = h; Game = game; Wide = wide; Tablet = tablet; Side = side;
            InsTop = (float)ins.Top; InsLeft = (float)ins.Left; InsRight = (float)ins.Right; InsBottom = (float)ins.Bottom;
        }
        public float L => Math.Max(10f, InsLeft + 4f);          // 왼쪽 여백
        public float Bot => H - Math.Max(6f, InsBottom);        // 아래 기준선
    }

    const string ModeKeyName = "controlMode";
    static readonly int[] AbilityKeys = { 49, 50, 51, 52, 81, 87, 69, 82 };
    static readonly string[] AbilityKeyNames = { "1", "2", "3", "4", "Q", "W", "E", "R" };
    const float TopBarH = 28f;
    const float TabletSide = 108f;   // 태블릿 모드에서 양옆에 비워 둘 검은 여백 폭(pt)

    readonly UIViewController _owner;
    readonly float _scale;
    readonly List<(UIView View, Func<Lay, CGRect?> Place)> _placed = new();
    readonly ShortcutKey[] _right = new ShortcutKey[10];
    readonly ShortcutKey[] _modeTabs = new ShortcutKey[3];
    ShortcutKey _menuKey;     // ETC (기존 게임 메뉴)
    ShortcutKey _opacityTab;  // 불투명도 탭 (◐ 아이콘)
    ShortcutKey _toggleKey;   // 새 "메뉴" 버튼: 누르면 [ETC 단축키 패드 숨김] 이 펼쳐진다
    ShortcutKey _speedKey;    // 배속 버튼 (메뉴 바로 오른쪽): 보통 → x1.5 → x2
    NSTimer _speedSync;       // 게임 설정 화면에서 바꾼 배속도 버튼 글자에 반영하려고 주기적으로 읽는다
    long _speedPressedAt;     // 방금 눌렀다면 게임이 처리할 시간을 준다 (글자가 잠깐 되돌아가는 것 방지)
    ShortcutTextBar _textBar;
    GameState.Snapshot _game = GameState.Current;
    bool _physical;
    bool _expanded;   // 메뉴를 눌러 [ETC 단축키 패드 숨김] 이 펼쳐져 있는가

    public ControlMode Mode { get; private set; }
    public event Action ModeChanged;
    public event Action PadTabTapped;
    public event Action OpacityTabTapped;

    public ShortcutOverlay(UIViewController owner, float scale)
    {
        _owner = owner;
        _scale = scale;
        BackgroundColor = UIColor.Clear;
        MultipleTouchEnabled = true;

        string saved = NSUserDefaults.StandardUserDefaults.StringForKey(ModeKeyName);
        if (saved == null)   // 예전 버전(패드 보임/숨김만 있던 때)에서 넘어온 경우
            saved = NSUserDefaults.StandardUserDefaults.BoolForKey("padVisible") ? "pad" : "keys";
        Mode = saved == "pad" ? ControlMode.Pad : saved == "hidden" ? ControlMode.Hidden : ControlMode.Keys;

        BuildTopBar();
        BuildShortcutKeys();

        Display.Changed += () => InvokeOnMainThread(SetNeedsLayout);
        GameState.Changed += snap => InvokeOnMainThread(() =>
        {
            bool relayout = snap.AbilityMenuOpen != _game.AbilityMenuOpen || (snap.TextEntry == null) != (_game.TextEntry == null);
            _game = snap;
            UpdateShortcutLabels();
            if (snap.TextEntry != null) _textBar.Sync(snap.TextEntry, snap.TextMax);
            else if (_textBar.IsFirstResponder) _textBar.Done();
            if (relayout) SetNeedsLayout();
        });
        ApplyMode(false);
    }

    // 블루투스 컨트롤러가 있으면 메뉴 버튼만 남긴다
    public void SetPhysical(bool on)
    {
        if (_physical == on) return;
        _physical = on;
        SetNeedsLayout();
    }

    public void ReleaseAll()
    {
        foreach (var (v, _) in _placed) if (v is ShortcutKey k) k.Reset();
    }

    // ---- 만들기 ----
    void Add(UIView v, Func<Lay, CGRect?> place)
    {
        _placed.Add((v, place));
        AddSubview(v);
    }

    ShortcutKey AddKey(ShortcutKey key, Func<Lay, CGRect?> place, Action<bool> pressed)
    {
        key.Pressed = pressed;
        Add(key, place);
        return key;
    }

    void BuildTopBar()
    {
        _toggleKey = new ShortcutKey("메뉴") { Round = true };
        Add(_toggleKey, f => TopSlot(f, -2));
        _toggleKey.Pressed = down => { if (!down) SetExpanded(!_expanded); };

        // 배속 버튼: 게임 [설정 > 게임 > 게임 속도] 와 같은 값을 돌려 쓴다 (GameSpeed.cs)
        _speedKey = new ShortcutKey("x1") { Round = true };
        Add(_speedKey, f => TopSlot(f, -3));
        _speedKey.Pressed = down =>
        {
            if (down) return;
            ShowSpeed(GameSpeed.Next());   // 명령은 게임 스레드에서 처리되므로 글자는 바로 바꾸고,
            _speedPressedAt = Environment.TickCount64;
            NSTimer.CreateScheduledTimer(0.7, _ => SyncSpeed());   // 잠시 뒤 실제 값으로 다시 맞춘다
        };
        SyncSpeed();
        _speedSync = NSTimer.CreateRepeatingScheduledTimer(0.5, _ => SyncSpeed());

        _menuKey = new ShortcutKey("ETC") { Round = true };
        Add(_menuKey, f => TopSlot(f, -1));
        _menuKey.Pressed = down =>
        {
            if (down) return;
            SetExpanded(false);
            GameMenuSheet.Open(_owner, _menuKey);   // 기존 게임 메뉴(시스템 메뉴 등)
        };

        string[] names = { "단축키", "패드", "숨김" };
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            var key = _modeTabs[i] = new ShortcutKey(names[i], "", KeyKind.Faint) { Round = true };
            Add(key, f => TopSlot(f, index));
            key.Pressed = down =>
            {
                if (down) return;
                SetExpanded(false);   // 고르면 다시 메뉴 하나로 접힌다
                if (index == (int)ControlMode.Pad && Mode == ControlMode.Pad) PadTabTapped?.Invoke();
                else SetMode((ControlMode)index);
            };
        }

        _opacityTab = new ShortcutKey("◐", "", KeyKind.Faint) { Round = true, OpacityIcon = true };   // 불투명도 아이콘(직접 그림)
        Add(_opacityTab, f => TopSlot(f, 3));
        _opacityTab.Pressed = down =>
        {
            if (down) return;
            SetExpanded(false);
            OpacityTabTapped?.Invoke();
        };

        _textBar = new ShortcutTextBar();
        Add(_textBar, f => _game.TextEntry == null ? null : TextBarRect(f));
    }

    // 메뉴 버튼과 같은 반투명 스타일(Plain) 그대로, 글자만 바꾼다
    void ShowSpeed(int percent) => _speedKey.Set(GameSpeed.Label(percent), "", KeyKind.Plain);

    // 게임이 실제로 쓰는 배속을 읽어 버튼에 반영 (게임이 아직 안 떴으면 0 → 보통으로 표시)
    void SyncSpeed()
    {
        if (Environment.TickCount64 - _speedPressedAt < 600) return;
        ShowSpeed(GameSpeed.Current());
    }

    // 터치 UI(단축키 버튼) 불투명도. 상단 [메뉴/ETC/탭] 줄은 항상 또렷하게 둔다
    public void SetUiOpacity(float a)
    {
        foreach (var (v, _) in _placed)
        {
            if (v is not ShortcutKey k) continue;
            bool top = ReferenceEquals(k, _toggleKey) || ReferenceEquals(k, _speedKey) || ReferenceEquals(k, _menuKey) || ReferenceEquals(k, _opacityTab)
                       || Array.IndexOf((Array)_modeTabs, (object)k) >= 0;
            if (!top) k.BaseAlpha = a;
        }
    }

    void SetExpanded(bool on)
    {
        if (_expanded == on) return;
        _expanded = on;
        _toggleKey.Set(_toggleKey.Label, "", on ? KeyKind.Accent : KeyKind.Plain);
        SetNeedsLayout();
    }

    void SetMode(ControlMode mode)
    {
        Mode = mode;
        NSUserDefaults.StandardUserDefaults.SetString(mode == ControlMode.Pad ? "pad" : mode == ControlMode.Hidden ? "hidden" : "keys", ModeKeyName);
        ApplyMode(true);
    }

    void ApplyMode(bool notify)
    {
        for (int i = 0; i < 3; i++)
            _modeTabs[i].Set(_modeTabs[i].Label, "", Mode == (ControlMode)i ? KeyKind.Accent : KeyKind.Faint);
        ReleaseAll();
        SetNeedsLayout();
        if (notify) ModeChanged?.Invoke();
    }

    static int Vk(string action, int fallback) => GameState.Current.Key(action, fallback);
    static void Tap(int vk, bool down) => WogShim.Win32.Messages.PostKey(vk, down);
    static Action<bool> Sends(Func<int> vk) => down => Tap(vk(), down);

    void BuildShortcutKeys()
    {
        AddKey(new ShortcutKey("확인", "Enter", KeyKind.Accent), f => Left(f, 0, 0, 2), Sends(() => 13));
        AddKey(new ShortcutKey("취소", "Esc"), f => Left(f, 1, 0, 1), Sends(() => 27));
        AddKey(new ShortcutKey("링", "Space"), f => Left(f, 1, 1, 1), Sends(() => Vk("Ring", 32)));
        AddKey(new ShortcutKey("상태", "C"), f => Left(f, 2, 0, 1), Sends(() => Vk("Status", 67)));
        AddKey(new ShortcutKey("다음", "Tab"), f => Left(f, 2, 1, 1), Sends(() => Vk("NextUnit", 9)));
        AddKey(new ShortcutKey("▲", "", KeyKind.Faint), f => Arrow(f, 0, -1), Sends(() => 38));
        AddKey(new ShortcutKey("◀", "", KeyKind.Faint), f => Arrow(f, -1, 0), Sends(() => 37));
        AddKey(new ShortcutKey("▶", "", KeyKind.Faint), f => Arrow(f, 1, 0), Sends(() => 39));
        AddKey(new ShortcutKey("▼", "", KeyKind.Faint), f => Arrow(f, 0, 1), Sends(() => 40));
        for (int i = 0; i < _right.Length; i++)
        {
            int slot = i;
            _right[i] = AddKey(new ShortcutKey(""), f => Right(f, slot), down => OnRight(slot, down));
        }
        UpdateShortcutLabels();
    }

    // ---- 오른쪽 열(어빌리티/공격/…) ----
    (string Label, string Hint, KeyKind Kind, int Vk, bool Dim) RightSlot(int slot)
    {
        var abilities = _game.Abilities;
        if (_game.AbilityMenuOpen)
        {
            if (slot >= 8)
                return slot == 8 ? ("▲", "", KeyKind.Faint, 38, false) : ("▼", "", KeyKind.Faint, 40, false);
            string name, lv;
            if (slot < abilities.Count) (name, lv) = SplitLevel(abilities[slot].Name);
            else { name = (slot + 1).ToString(); lv = ""; }
            bool dim = slot >= abilities.Count || !abilities[slot].Enabled;
            return (name, Join($"{slot + 1}·{AbilityKeyNames[slot]}", lv), KeyKind.Ability, AbilityKeys[slot], dim);
        }
        if (slot < 4)
        {
            string name, lv;
            if (slot < abilities.Count) (name, lv) = SplitLevel(abilities[slot].Name);
            else { name = $"어빌 {slot + 1}"; lv = ""; }
            bool dim = _game.PlayerTurn && (slot >= abilities.Count || !abilities[slot].Enabled);
            return (name, Join($"{slot + 1}", lv), KeyKind.Ability, AbilityKeys[slot], dim);
        }
        return slot switch
        {
            4 => ("공격", "E", KeyKind.Accent, Vk("Attack", 69), false),
            5 => ("어빌 목록", "R", KeyKind.Accent, Vk("Ability", 82), false),
            6 => ("휴식", "Q", KeyKind.Plain, Vk("Rest", 81), false),
            7 => ("아이템", "Z", KeyKind.Plain, Vk("Item", 90), false),
            8 => ("시스템", "X", KeyKind.Plain, Vk("System", 88), false),
            _ => ("더보기", "⋯", KeyKind.Plain, 0, false),
        };
    }

    static (string Name, string Level) SplitLevel(string s)
    {
        var m = Regex.Match(s, @"^(.*?)\s+(Lv\s*\d+)$");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value.Replace(" ", "")) : (s, "");
    }

    static string Join(string a, string b) => b.Length != 0 ? a + " · " + b : a;

    void UpdateShortcutLabels()
    {
        for (int i = 0; i < _right.Length; i++)
        {
            var (label, hint, kind, _, dim) = RightSlot(i);
            _right[i].Set(label, hint, kind, dim);
        }
    }

    void OnRight(int slot, bool down)
    {
        int vk = RightSlot(slot).Vk;
        if (vk != 0) Tap(vk, down);
        else if (!down) ShowMore(_right[slot]);
    }

    void ShowMore(UIView anchor)
    {
        (string Name, Func<int> Vk)[] more =
        {
            ("상태 (C)", () => Vk("Status", 67)),
            ("다음 인물 (Tab)", () => Vk("NextUnit", 9)),
            ("아이템 (Z)", () => Vk("Item", 90)),
            ("시스템 메뉴 (X)", () => Vk("System", 88)),
            ("격자 켜기·끄기 (G)", () => Vk("Grid", 71)),
            ("체력바 켜기·끄기 (H)", () => Vk("Gauges", 72)),
            ("미니맵 접기·펴기 (M)", () => Vk("MiniMap", 77)),
            ("행군 — 파트1 (F)", () => Vk("March", 70)),
            ("전직 트리 — 파트1 (T)", () => Vk("JobTree", 84)),
            ("동료 정보 — 파트1 (P)", () => Vk("PartyTable", 80)),
            ("단축키 목록 (J)", () => Vk("KeyHelp", 74)),
        };
        var sheet = UIAlertController.Create(null, null, UIAlertControllerStyle.ActionSheet);
        foreach (var item in more)
        {
            var it = item;
            sheet.AddAction(UIAlertAction.Create(it.Name, UIAlertActionStyle.Default, _ =>
            {
                int vk = it.Vk();
                Tap(vk, true);
                Tap(vk, false);
            }));
        }
        sheet.AddAction(UIAlertAction.Create("닫기", UIAlertActionStyle.Cancel, null));
        GameMenuSheet.Present(_owner, sheet, anchor);
    }

    // ---- 배치 (안드로이드 ControlsOverlay 와 같은 수치, 단위는 pt) ----
    static float Unit(Lay f) => Math.Min(44f, (f.H - 48f) / 6.6f);
    float ActionsLeft(Lay f) => f.L + 34f * 3f + 14f;
    static float RowTop(Lay f) => 44f + f.InsTop;
    // 폰(가로) 단축키 한 칸 높이: 오른쪽 5줄과 왼쪽 3줄 + 방향키 십자가 세로에 들어가는 만큼 키운다 (최대 56)
    static float WideUnit(Lay f)
    {
        var (_, w) = SideBox(f, true);
        float avail = f.H - RowTop(f) - Math.Max(14f, f.InsBottom + 8f);
        float cross = 3f * Math.Min(w / 3f, 56f);
        float uR = (avail - 4f * 6f) / 5f;
        float uL = (avail - 2f * 6f - 8f - cross) / 3f;
        return Math.Clamp(Math.Min(uR, uL), 30f, 56f);
    }

    // index: -3 = 배속(동그라미), -2 = 메뉴(동그라미 토글), -1 = ETC, 0..2 = 단축키/패드/숨김, 3 = 불투명도(◐)
    // 펼친 순서: 메뉴 · 배속 · ETC · 단축키 · 패드 · ◐ · 숨김   (접힌 상태: 메뉴 · 배속)
    CGRect TopSlot(Lay f, int index)
    {
        float bar = TopBarH, gap = 6f, d = 28.8f;   // d = 메뉴·배속 동그라미 지름 (36 의 80%)
        bool open = _expanded && !_physical;
        float y = f.Game.Top >= bar + 6f ? ((float)f.Game.Top - bar) / 2f : 4f + f.InsTop;
        float w = Math.Min(60f, (f.W - 16f - d * 2f - gap * 6f) / 5f);
        float cy = Math.Max(0f, y - (d - bar) / 2f);   // 동그라미 세로 위치
        if (_physical)
        {
            // 컨트롤러 연결 시: 메뉴 동그라미는 없고 ETC 만 가운데, 배속은 그 오른쪽
            if (index == -3) return new CGRect((f.W + w) / 2f + gap, cy, d, d);
            return new CGRect((f.W - w) / 2f, y, w, bar);
        }
        float rowW = w * 5f + gap * 4f;
        float total = open ? d + gap + d + gap + rowW : d;   // 메뉴 · 배속 · (ETC … 숨김)
        float x0 = (f.W - total) / 2f;   // 접힌 상태에서는 메뉴가 가운데에 그대로 있다
        if (index == -2) return new CGRect(x0, cy, d, d);
        if (index == -3) return new CGRect(x0 + d + gap, cy, d, d);
        if (!open) return new CGRect((f.W - w) / 2f, y, w, bar);
        float x = x0 + d + gap + d + gap;
        int slot = index == -1 ? 0 : index == 0 ? 1 : index == 1 ? 2 : index == 3 ? 3 : 4;
        return new CGRect(x + (w + gap) * slot, y, w, bar);
    }

    CGRect TextBarRect(Lay f)
    {
        var menu = TopSlot(f, -1);
        float w = Math.Min(280f, f.W - 32f);
        float y = (float)menu.Bottom + 6f;
        return new CGRect((f.W - w) / 2f, y, w, 38f);
    }

    // 4:3 게임 양옆 검은 여백(왼쪽 [x0, Side], 오른쪽 [W-Side, W]) 안에서 노치/둥근 모서리를 피한 칸
    static (float X, float W) SideBox(Lay f, bool left)
    {
        float inset = left ? f.InsLeft : f.InsRight;
        float avail = Math.Max(40f, f.Side - inset);
        float w = (f.Tablet || f.Wide) ? Math.Min(avail - 8f, 160f) : Math.Min(avail - 16f, 124f);   // 태블릿·폰(가로): 게임 화면 바로 앞(4pt)까지
        float x = left ? inset + (avail - w) / 2f : f.W - f.Side + (avail - w) / 2f;
        return (x, w);
    }

    const float TabletGap = 5f;
    const float TabletArrowH = 47f, TabletArrowGap = 5f;   // 방향키 버튼 높이·간격
    // 태블릿 왼쪽 여백에 놓는 오른쪽열 칸 번호 (어빌 메뉴가 열리면 1~5번 칸)
    int[] TabletLeftSlots() => _game.AbilityMenuOpen ? new[] { 0, 1, 2, 3, 4 } : new[] { 0, 1, 2, 3, 5, 7 };
    // 태블릿 오른쪽 여백의 칸 수: 링·상태·다음 + 남은 칸 + 확인·취소. 맨 아래(취소)를 기준으로 위로 쌓는다
    // (시스템·더보기 = 8, 9번 칸은 오른쪽 맨 위에 따로 둔다)
    int TabletRightCount()
    {
        int n = 3 + 2;
        var left = TabletLeftSlots();
        for (int k = 0; k < 8; k++) if (Array.IndexOf(left, k) < 0) n++;
        return n;
    }
    CGRect TabletRightRow(Lay f, int i)
    {
        float rh = TabletRowH(f);
        float bottomY = f.H - Math.Max(14f, f.InsBottom + 8f) - rh;   // 취소 자리
        var (x0, w) = SideBox(f, false);
        int count = TabletRightCount();
        // 링~휴식 칸들은 세 칸 띄운다. 맨 위 시스템·더보기(또는 ▲▼)와 겹치면 그 아래까지만 올린다
        float firstY = bottomY - (count - 1) * (rh + TabletGap);
        float maxLift = Math.Max(0f, firstY - (RowTop(f) + 2f * (rh + TabletGap)));
        float lift = i < count - 2 ? Math.Min(3f * (rh + TabletGap), maxLift) : (rh + TabletGap);   // 확인·취소는 맨 아래에서 한 칸 위
        return new CGRect(x0, bottomY - (count - 1 - i) * (rh + TabletGap) - lift, w, rh);
    }
    // 태블릿 모드 버튼 높이: 오른쪽 10칸이 세로에 다 들어가도록
    float TabletRowH(Lay f)
    {
        float room = f.H - RowTop(f) - Math.Max(14f, f.InsBottom + 8f);
        return Math.Min(Unit(f), (room - TabletGap * 9f) / 10f);
    }

    CGRect? Left(Lay f, int row, int col, int span)
    {
        if (Mode != ControlMode.Keys) return null;
        if (f.Tablet)
        {
            // 오른쪽 여백: 링 · 상태 · 다음 은 위쪽, 확인 · 취소 는 맨 아래(엄지가 닿는 곳)
            if (row == 0) return TabletRightRow(f, TabletRightCount() - 2);            // 확인
            if (row == 1 && col == 0) return TabletRightRow(f, TabletRightCount() - 1); // 취소
            // 링(row1,col1) · 상태 · 다음. 평소엔 휴식=0, 상태=1, 다음=2, 링=3, 공격=4 순서(어빌 메뉴가 열리면 링=0)
            if (row == 1) return TabletRightRow(f, _game.AbilityMenuOpen ? 0 : 3);
            return TabletRightRow(f, 1 + col);
        }
        if (f.Wide)
        {
            float unit = WideUnit(f), gap = 6f;
            // 확인은 오른쪽 맨 아래 줄(시스템·더보기 와 자리 교환), 취소·링·상태·다음 은 오른쪽 맨 위 두 줄(어빌 1~4 와 자리 교환)
            var (x0, w) = SideBox(f, false);
            float half = (w - gap) / 2f;
            int rr = row == 0 ? 4 : row - 1;
            if (row == 2 && col == 1 && !_game.AbilityMenuOpen) rr = 0;   // 다음은 아이템 자리(맨 위 줄 오른쪽)로
            if (row == 1 && col == 1 && !_game.AbilityMenuOpen) rr = 2;   // 링은 아이템 자리로 옮기고, 세·네 번째 줄 교환으로 휴식 옆(세 번째 줄)
            float top = RowTop(f) + rr * (unit + gap);
            int cc = (row == 1 && col == 1 && !_game.AbilityMenuOpen) ? 0 : col;   // 링은 휴식과 좌우를 맞바꿔 왼쪽 칸
            float x = x0 + cc * (half + gap);
            return new CGRect(x, top, span == 2 ? w : half, unit);
        }
        if (row == 2) return GridCell(f, 1, col, 6);
        return ActionCell(f, row != 0 ? 1 + col : 0);
    }

    (float Top, float RowH, float Gap) BottomRows(Lay f)
    {
        float gap = 5f;
        float bottom = f.Bot;
        float rowH = Math.Clamp((bottom - (float)f.Game.Bottom - 4f - gap) / 2f, 28f, 38f);
        return (bottom - 2f * rowH - gap, rowH, gap);
    }

    CGRect ActionCell(Lay f, int index)
    {
        var (top, rowH, gap) = BottomRows(f);
        float x = ActionsLeft(f) + (index == 0 ? 0f : 82f + (index - 1) * 68f);
        return new CGRect(x, top, index == 0 ? 76f : 62f, 2f * rowH + gap);
    }

    CGRect? GridCell(Lay f, int row, int index, int count)
    {
        if (index >= count) return null;
        var (top, rowH, gap) = BottomRows(f);
        float start = ActionsLeft(f) + 82f + 2f * 68f + 4f;
        float end = f.W - Math.Max(8f, f.InsRight + 4f);
        float w = Math.Min(96f, (end - start - gap * (count - 1)) / count);
        float x = start + index * (w + gap);
        float y = top + row * (rowH + gap);
        return new CGRect(x, y, w, rowH);
    }

    CGRect? Arrow(Lay f, int dx, int dy)
    {
        if (Mode != ControlMode.Keys) return null;
        if (f.Tablet)
        {
            // 방향키: 왼쪽 여백 맨 아래에 ▲ ▼ ◀ ▶ 순서로 세로 한 줄
            float ah = TabletArrowH, ag = TabletArrowGap;
            var (ax, aw) = SideBox(f, true);
            int ai = dy < 0 ? 0 : dy > 0 ? 1 : dx < 0 ? 2 : 3;
            float bottom = f.H - Math.Max(14f, f.InsBottom + 8f);
            return new CGRect(ax, bottom - 4f * ah - 3f * ag + ai * (ah + ag), aw, ah);
        }
        float n, cx, cy;
        if (f.Wide)
        {
            var (x0, w) = SideBox(f, true);
            n = Math.Min(WideUnit(f), (w) / 3f);
            cx = x0 + w / 2f;
            cy = f.H - Math.Max(14f, f.InsBottom + 8f) - n * 1.5f;
        }
        else
        {
            n = 34f;
            cx = f.L + n * 1.5f;
            cy = f.Bot - n * 1.5f;
        }
        float x = cx + dx * n - n / 2f;
        float y = cy + dy * n - n / 2f;
        return new CGRect(x + 1, y + 1, n - 2, n - 2);
    }

    CGRect? Right(Lay f, int slot)
    {
        if (Mode != ControlMode.Keys) return null;
        if (f.Tablet)
        {
            // 왼쪽 여백: 어빌 1~4 · 어빌 목록 · 아이템 (어빌 메뉴가 열리면 1~5번 칸). 나머지는 오른쪽 확인~다음 아래에 잇는다
            int[] leftSlots = TabletLeftSlots();
            int li = Array.IndexOf(leftSlots, slot);
            float rowH = TabletRowH(f);
            if (slot >= 8)   // 시스템 · 더보기 (어빌 메뉴가 열리면 ▲ ▼): 오른쪽 맨 위
            {
                var (tx, tw) = SideBox(f, false);
                return new CGRect(tx, RowTop(f) + (slot - 8) * (rowH + TabletGap), tw, rowH);
            }
            if (li >= 0)
            {
                // 왼쪽 목록은 세로 가운데에 맞춘다 (아래 방향키와 겹치면 그 위로 올린다)
                var (lx, lw) = SideBox(f, true);
                float block = leftSlots.Length * rowH + (leftSlots.Length - 1) * TabletGap;
                float arrowsTop = f.H - Math.Max(14f, f.InsBottom + 8f) - (4f * TabletArrowH + 3f * TabletArrowGap);
                float y0 = Math.Max(RowTop(f), Math.Min(f.H / 2f - block / 2f, arrowsTop - 8f - block) - (rowH + TabletGap));   // 방향키와 떨어지게 한 칸 위로
                return new CGRect(lx, y0 + li * (rowH + TabletGap), lw, rowH);
            }
            if (!_game.AbilityMenuOpen) return TabletRightRow(f, slot == 6 ? 0 : 4);   // 휴식=맨 위, 공격=링 다음
            int ri = 3;   // 어빌 메뉴가 열리면 링·상태·다음 (3칸) 다음부터
            for (int k = 0; k < slot; k++) if (Array.IndexOf(leftSlots, k) < 0) ri++;
            return TabletRightRow(f, ri);
        }
        if (f.Wide)
        {
            float unit = WideUnit(f), gap = 6f;
            // 시스템·더보기(칸 8, 9)는 왼쪽 맨 위, 어빌 1~4(칸 0~3)는 그 아래 두 줄, 나머지는 오른쪽 세 번째 줄부터
            bool toLeft = slot < 4 || slot >= 8;
            var (x0, w) = SideBox(f, toLeft);
            float half = (w - gap) / 2f;
            int r = slot >= 8 ? 0 : toLeft ? 1 + slot / 2 : slot / 2, c = slot % 2;
            if (!toLeft && (r == 2 || r == 3)) r = 5 - r;               // 공격·어빌 목록 줄과 휴식·링 줄을 서로 교환
            if (slot == 6 && !_game.AbilityMenuOpen) c = 1;   // 휴식은 링과 좌우를 맞바꿔 오른쪽 칸
            if (slot == 7 && !_game.AbilityMenuOpen) r = 1;   // 아이템은 다음 자리(상태 옆)로
            return new CGRect(x0 + c * (half + gap), RowTop(f) + r * (unit + gap), half, unit);
        }
        if (_game.AbilityMenuOpen)
            return slot >= 8 ? null : GridCell(f, 0, slot, 8);
        if (slot >= 6) return GridCell(f, 1, slot - 4, 6);
        return GridCell(f, 0, slot, 6);
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        float w = (float)Bounds.Width, h = (float)Bounds.Height;
        if (w < 2 || h < 2) return;
        var ins = SafeAreaInsets;
        var (rx, ry, rw, rh) = Display.Rect;
        CGRect game = rw > 1
            ? new CGRect(rx / _scale, ry / _scale, rw / _scale, rh / _scale)
            : new CGRect(0, 0, w, h);
        bool wide = w >= h * 1.55f;
        // 폭이 좁은 화면(예: 4:3 에 가까운 iPad)에서는 게임 화면을 위쪽 버튼줄 아래로 내린다
        bool topNeeded = Mode == ControlMode.Keys && !wide;
        float topPt = TopBarH + 8f + (float)ins.Top;

        // 태블릿(가로, 4:3 에 가까움): 게임을 살짝 줄여 양옆에 검은 여백(TabletSide)을 만들고 버튼을 거기에 둔다.
        // 게임은 4:3 이고 TopLimit 만큼 위아래가 줄어드는 만큼 양옆 여백이 늘어난다.
        bool tablet = false;
        if (topNeeded && w >= h * 1.2f)
        {
            float gameH = (w - 2f * TabletSide) * 0.75f;
            float needTop = (h - gameH) / 2f;
            float top = Math.Max(topPt, needTop);
            if (h - 2f * top >= h * 0.68f) { tablet = true; topPt = top; }   // 게임이 너무 작아지면 예전 배치 유지
        }
        Display.TopLimit = topNeeded ? (int)(topPt * _scale) : -1;
        // 태블릿의 양옆 여백은 Display.Rect 가 아니라 계산으로 구한다(Rect 는 TopLimit 이 반영되기 전 값이 남아 있을 수 있다)
        // 게임은 위아래를 최소 5% 남기고 4:3 으로 들어간다 (폰·태블릿 캡처로 확인한 값)
        float topEff = Math.Max(topPt, h * 0.05f);
        // 태블릿도 폰(가로)과 같이 전투 화면(높이 100%, 4:3) 기준으로 여백을 고정한다 → 타이틀·전투에서 버튼 크기가 바뀌지 않는다
        float side = tablet ? Math.Max(60f, (w - h * 4f / 3f) / 2f)
                  : wide ? Math.Max(60f, (w - h * 4f / 3f) / 2f)   // 폰(가로): 전투 화면(높이 100%, 4:3) 기준으로 고정. 게임 화면 위치를 읽지 않으므로 화면이 바뀌어도 변하지 않는다
                  : Math.Max((float)game.Left, 140f);
        var frame = new Lay(w, h, game, wide, tablet, side, ins);

        foreach (var (view, place) in _placed)
        {
            bool isTab = Array.IndexOf((Array)_modeTabs, (object)view) >= 0 || ReferenceEquals(view, _opacityTab);
            bool isMenu = ReferenceEquals(view, _menuKey);
            bool isText = ReferenceEquals(view, _textBar);
            CGRect? rect;
            bool isToggle = ReferenceEquals(view, _toggleKey);
            if (ReferenceEquals(view, _speedKey)) rect = place(frame);                // 배속은 항상 보인다 (메뉴 옆)
            else if (isToggle) rect = _physical ? null : place(frame);                 // 컨트롤러 연결 시엔 ETC 만 남긴다
            else if (isMenu) rect = (_expanded || _physical) ? place(frame) : null;
            else if (isTab) rect = (_expanded && !_physical) ? place(frame) : null;
            else if (_physical) rect = null;
            else if (Mode == ControlMode.Hidden && !isText) rect = null;
            else rect = place(frame);
            if (rect == null) { view.Hidden = true; continue; }
            view.Hidden = false;
            view.Frame = rect.Value;
        }
    }

    // 버튼이 없는 곳의 터치는 아래 게임 화면으로 통과시킨다
    public override UIView HitTest(CGPoint point, UIEvent uievent)
    {
        for (int i = Subviews.Length - 1; i >= 0; i--)
        {
            var v = Subviews[i];
            if (v.Hidden || v.Alpha < 0.01f || !v.UserInteractionEnabled) continue;
            if (!v.Frame.Contains(point)) continue;
            var hit = v.HitTest(ConvertPointToView(point, v), uievent);
            if (hit != null) return hit;
        }
        return null;
    }
}

// 게임 메뉴(꽉 찬 화면, 게임 메뉴바 항목)를 iOS 액션 시트로 띄운다
internal static class GameMenuSheet
{
    const uint FullscreenCommand = 1174u;

    public static void Open(UIViewController owner, UIView anchor)
    {
        User32.OpenMenuBar(tree =>
        {
            owner.InvokeOnMainThread(() =>
            {
                var list = new List<MenuEntry>
                {
                    new MenuEntry("꽉 찬 화면 (게임 전체화면)", FullscreenCommand, null, GameState.Current.Fullscreen, true, false),
                };
                if (tree != null && tree.Count > 0)
                {
                    list.Add(new MenuEntry("", 0u, null, false, true, true));
                    list.AddRange(tree);
                }
                Show(owner, anchor, list);
            });
        });
    }

    static void Show(UIViewController owner, UIView anchor, IReadOnlyList<MenuEntry> items)
    {
        var sheet = UIAlertController.Create(null, null, UIAlertControllerStyle.ActionSheet);
        foreach (var e in items)
        {
            if (e.Separator || string.IsNullOrEmpty(e.Text)) continue;
            var entry = e;
            string title = (entry.Checked ? "✓ " : "") + entry.Text + (entry.Submenu != null ? "  ▸" : "");
            var action = UIAlertAction.Create(title, UIAlertActionStyle.Default, _ =>
            {
                if (entry.Submenu != null) Show(owner, anchor, entry.Submenu);
                else if (entry.Command != 0) User32.RunMenuCommand(entry.Command);
            });
            action.Enabled = entry.Enabled;
            sheet.AddAction(action);
        }
        sheet.AddAction(UIAlertAction.Create("닫기", UIAlertActionStyle.Cancel, null));
        Present(owner, sheet, anchor);
    }

    public static void Present(UIViewController owner, UIAlertController sheet, UIView anchor)
    {
        var pop = sheet.PopoverPresentationController;   // iPad 는 말풍선 위치가 필요하다
        if (pop != null)
        {
            pop.SourceView = anchor;
            pop.SourceRect = anchor.Bounds;
        }
        owner.PresentViewController(sheet, true, null);
    }
}
