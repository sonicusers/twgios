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

// 안드로이드 0.20 의 "단축키 모드"(ControlsOverlay / Key / TextBar / GameMenu)를 UIKit 으로 옮긴 것.
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
        Label = label; Hint = hint; Kind = kind; Dim = dim;
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
        _label.Text = Label;
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
        public readonly float InsTop, InsLeft, InsRight, InsBottom;
        public Lay(float w, float h, CGRect game, bool wide, float side, UIEdgeInsets ins)
        {
            W = w; H = h; Game = game; Wide = wide; Side = side;
            InsTop = (float)ins.Top; InsLeft = (float)ins.Left; InsRight = (float)ins.Right; InsBottom = (float)ins.Bottom;
        }
        public float L => Math.Max(10f, InsLeft + 4f);          // 왼쪽 여백
        public float Bot => H - Math.Max(6f, InsBottom);        // 아래 기준선
    }

    const string ModeKeyName = "controlMode";
    static readonly int[] AbilityKeys = { 49, 50, 51, 52, 81, 87, 69, 82 };
    static readonly string[] AbilityKeyNames = { "1", "2", "3", "4", "Q", "W", "E", "R" };
    const float TopBarH = 28f;

    readonly UIViewController _owner;
    readonly float _scale;
    readonly List<(UIView View, Func<Lay, CGRect?> Place)> _placed = new();
    readonly ShortcutKey[] _right = new ShortcutKey[10];
    readonly ShortcutKey[] _modeTabs = new ShortcutKey[3];
    ShortcutKey _menuKey;     // ETC (기존 게임 메뉴)
    ShortcutKey _opacityTab;  // 불투명도 탭 (◐ 아이콘)
    ShortcutKey _toggleKey;   // 새 "메뉴" 버튼: 누르면 [ETC 단축키 패드 숨김] 이 펼쳐진다
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

    // 터치 UI(단축키 버튼) 불투명도. 상단 [메뉴/ETC/탭] 줄은 항상 또렷하게 둔다
    public void SetUiOpacity(float a)
    {
        foreach (var (v, _) in _placed)
        {
            if (v is not ShortcutKey k) continue;
            bool top = ReferenceEquals(k, _toggleKey) || ReferenceEquals(k, _menuKey) || ReferenceEquals(k, _opacityTab)
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

    // index: -2 = 메뉴(동그라미 토글), -1 = ETC, 0..2 = 단축키/패드/숨김, 3 = 불투명도(◐)
    // 펼친 순서: 메뉴 · ETC · 단축키 · 패드 · ◐ · 숨김
    CGRect TopSlot(Lay f, int index)
    {
        float bar = TopBarH, gap = 6f, d = 28.8f;   // d = 메뉴 동그라미 지름 (36 의 80%)
        bool open = _expanded && !_physical;
        float y = f.Game.Top >= bar + 6f ? ((float)f.Game.Top - bar) / 2f : 4f + f.InsTop;
        float w = Math.Min(60f, (f.W - 16f - d - gap * 5f) / 5f);
        if (index == -2)
        {
            float total = open ? d + gap + w * 5f + gap * 4f : d;
            float x0 = (f.W - total) / 2f;
            return new CGRect(x0, Math.Max(0f, y - (d - bar) / 2f), d, d);
        }
        if (!open) return new CGRect((f.W - w) / 2f, y, w, bar);   // 컨트롤러 연결 시 ETC 만
        float t = d + gap + w * 5f + gap * 4f;
        float x = (f.W - t) / 2f + d + gap;
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
        float w = Math.Min(avail - 16f, 124f);
        float x = left ? inset + (avail - w) / 2f : f.W - f.Side + (avail - w) / 2f;
        return (x, w);
    }

    CGRect? Left(Lay f, int row, int col, int span)
    {
        if (Mode != ControlMode.Keys) return null;
        if (f.Wide)
        {
            float unit = Unit(f), gap = 6f;
            var (x0, w) = SideBox(f, true);
            float half = (w - gap) / 2f;
            float top = RowTop(f) + row * (unit + gap);
            float x = x0 + col * (half + gap);
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
        float n, cx, cy;
        if (f.Wide)
        {
            var (x0, w) = SideBox(f, true);
            n = Math.Min(Unit(f), (w) / 3f);
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
        if (f.Wide)
        {
            float unit = Unit(f), gap = 6f;
            var (x0, w) = SideBox(f, false);
            float half = (w - gap) / 2f;
            int r = slot / 2, c = slot % 2;
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
        Display.TopLimit = topNeeded ? (int)((TopBarH + 8f + (float)ins.Top) * _scale) : -1;
        var frame = new Lay(w, h, game, wide, Math.Max((float)game.Left, 140f), ins);

        foreach (var (view, place) in _placed)
        {
            bool isTab = Array.IndexOf((Array)_modeTabs, (object)view) >= 0 || ReferenceEquals(view, _opacityTab);
            bool isMenu = ReferenceEquals(view, _menuKey);
            bool isText = ReferenceEquals(view, _textBar);
            CGRect? rect;
            bool isToggle = ReferenceEquals(view, _toggleKey);
            if (isToggle) rect = _physical ? null : place(frame);                 // 컨트롤러 연결 시엔 ETC 만 남긴다
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
