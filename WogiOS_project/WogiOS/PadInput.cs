using System;
using System.Threading;
using GameController;
using WogShim.Win32;
using WinMessages = WogShim.Win32.Messages;

namespace WogiOS;

// 화면 패드와 블루투스 컨트롤러 입력을 게임의 XInput(WogShim.Win32.XInput)으로 넘겨준다.
// XInput.Poll 이 null 이면 게임은 "컨트롤러 없음"으로 인식하므로, 패드가 필요 없을 때는 null 로 둔다.
internal static class PadInput
{
    // XInput 버튼 비트
    public const int DUp = 0x0001, DDown = 0x0002, DLeft = 0x0004, DRight = 0x0008;
    public const int DAll = DUp | DDown | DLeft | DRight;
    public const int Start = 0x0010, Back = 0x0020, LThumb = 0x0040, RThumb = 0x0080;
    public const int LB = 0x0100, RB = 0x0200;
    public const int BtnA = 0x1000, BtnB = 0x2000, BtnX = 0x4000, BtnY = 0x8000;
    // 트리거는 XInput 버튼 비트가 아니라서 위쪽 비트를 따로 쓴다
    public const int LTrigger = 1 << 16, RTrigger = 1 << 17;

    // true: 화면 패드를 키보드 입력으로 게임에 보낸다 (게임이 컨트롤러를 인식하지 않아 컨트롤러용 선택 박스가 안 뜸)
    // false: 기존 방식 (XInput 컨트롤러로 전달)
    public static bool KeyMode = true;

    // 패드 버튼 -> 키보드 가상 키 코드. 게임이 쓰는 키에 맞춰 바꿔야 하는 임시 값이다.
    static readonly (int bit, int vk)[] KeyMap =
    {
        (DUp, 0x26), (DDown, 0x28), (DLeft, 0x25), (DRight, 0x27), // 방향키
        (BtnA, 0x0D),   // A  -> Enter
        (BtnB, 0x1B),   // B  -> Esc
        (BtnX, 0x20),   // X  -> Space
        (BtnY, 0x09),   // Y  -> Tab
        (Start, 0x77),  // ≡  -> F8 (임시)
        (Back, 0x08),   // ◁  -> Backspace
        (LB, 0x51),     // LB -> Q
        (RB, 0x45),     // RB -> E
    };

    static void SendKeys(int oldBits, int newBits)
    {
        int ch = oldBits ^ newBits;
        foreach (var (bit, vk) in KeyMap)
            if ((ch & bit) != 0) WinMessages.PostKey(vk, (newBits & bit) != 0);
    }

    static bool UseKeys => KeyMode && !HasPhysical;

    static int _bits;          // 화면 패드가 누르고 있는 버튼들
    static bool _overlayOn;    // 화면 패드가 보이는 중인가
    static uint _packet;
    static XInput.State _last;
    static readonly Func<XInput.State> PollFn = Poll;

    public static void SetBits(int clear, int set)
    {
        int o, n;
        do { o = Volatile.Read(ref _bits); n = (o & ~clear) | set; }
        while (Interlocked.CompareExchange(ref _bits, n, o) != o);
        if (UseKeys) SendKeys(o, n);
    }

    public static void ReleaseAll()
    {
        int o = Interlocked.Exchange(ref _bits, 0);
        if (KeyMode) SendKeys(o, 0);
    }

    public static bool HasPhysical => FindPhysical() != null;

    public static void SetOverlayActive(bool on)
    {
        _overlayOn = on;
        if (!on) ReleaseAll();
        Refresh();
    }

    // 화면 패드가 보이거나 실제 컨트롤러가 있으면 연결된 것으로, 아니면 연결 안 됨으로 알린다
    public static void Refresh() => XInput.Poll = (HasPhysical || (_overlayOn && !KeyMode)) ? PollFn : null;

    static GCExtendedGamepad FindPhysical()
    {
        var list = GCController.Controllers;
        if (list == null) return null;
        foreach (var c in list)
        {
            var g = c.ExtendedGamepad;
            if (g != null) return g;
        }
        return null;
    }

    // 게임 스레드에서 호출된다
    static XInput.State Poll()
    {
        var s = new XInput.State();
        var g = FindPhysical();
        if (g != null) Fill(ref s, g);
        else
        {
            int b = Volatile.Read(ref _bits);
            s.Buttons = (ushort)(b & 0xFFFF);
            s.LeftTrigger = (b & LTrigger) != 0 ? (byte)255 : (byte)0;
            s.RightTrigger = (b & RTrigger) != 0 ? (byte)255 : (byte)0;
        }
        if (!Same(s, _last)) _packet++;
        s.PacketNumber = _packet;
        _last = s;
        return s;
    }

    static bool Same(XInput.State a, XInput.State b) =>
        a.Buttons == b.Buttons && a.LeftTrigger == b.LeftTrigger && a.RightTrigger == b.RightTrigger &&
        a.ThumbLX == b.ThumbLX && a.ThumbLY == b.ThumbLY && a.ThumbRX == b.ThumbRX && a.ThumbRY == b.ThumbRY;

    static void Fill(ref XInput.State s, GCExtendedGamepad g)
    {
        int b = 0;
        var dp = g.DPad;
        if (dp.Up.IsPressed) b |= DUp;
        if (dp.Down.IsPressed) b |= DDown;
        if (dp.Left.IsPressed) b |= DLeft;
        if (dp.Right.IsPressed) b |= DRight;
        if (g.ButtonA.IsPressed) b |= BtnA;
        if (g.ButtonB.IsPressed) b |= BtnB;
        if (g.ButtonX.IsPressed) b |= BtnX;
        if (g.ButtonY.IsPressed) b |= BtnY;
        if (g.LeftShoulder.IsPressed) b |= LB;
        if (g.RightShoulder.IsPressed) b |= RB;
        if (g.ButtonMenu.IsPressed) b |= Start;
        var opt = g.ButtonOptions;
        if (opt != null && opt.IsPressed) b |= Back;
        var l3 = g.LeftThumbstickButton;
        if (l3 != null && l3.IsPressed) b |= LThumb;
        var r3 = g.RightThumbstickButton;
        if (r3 != null && r3.IsPressed) b |= RThumb;

        s.Buttons = (ushort)b;
        s.LeftTrigger = (byte)(Math.Clamp(g.LeftTrigger.Value, 0f, 1f) * 255f);
        s.RightTrigger = (byte)(Math.Clamp(g.RightTrigger.Value, 0f, 1f) * 255f);
        s.ThumbLX = Axis(g.LeftThumbstick.XAxis.Value);
        s.ThumbLY = Axis(g.LeftThumbstick.YAxis.Value);
        s.ThumbRX = Axis(g.RightThumbstick.XAxis.Value);
        s.ThumbRY = Axis(g.RightThumbstick.YAxis.Value);
    }

    static short Axis(float v) => (short)(Math.Clamp(v, -1f, 1f) * 32767f);
}
