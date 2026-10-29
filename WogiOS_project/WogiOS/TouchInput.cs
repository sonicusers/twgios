using System;
using System.Collections.Generic;
using System.Linq;
using Foundation;
using UIKit;
using WogPatcher;
using WogShim;
using WogShim.Win32;
using WinMessages = WogShim.Win32.Messages;

namespace WogiOS;

// Android TouchInput 을 UIKit 이벤트로 옮긴 것 (탭=클릭, 드래그, 길게=우클릭, 두 손가락=휠/우클릭)
internal sealed class TouchInput
{
    enum Mode { None, Pending, Drag, Pan, Hold, TwoFinger }

    const uint MOVE = 512, LDOWN = 513, LUP = 514, RDOWN = 516, RUP = 517, WHEEL = 522;

    readonly float _scale, _slop, _wheelStep;
    Mode _mode;
    float _downX, _downY, _lastX, _lastY, _curX, _curY;
    double _downAt;
    bool _twoMoved;
    NSTimer _lp;

    public TouchInput(float scale) { _scale = scale; _slop = 12 * scale; _wheelStep = 36 * scale; }

    static List<UITouch> Active(UIView v, UIEvent e)
    {
        var l = e.AllTouches.ToArray<UITouch>()
            .Where(t => t.View == v && t.Phase != UITouchPhase.Ended && t.Phase != UITouchPhase.Cancelled).ToList();
        l.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return l;
    }

    (float x, float y) Pos(UIView v, UITouch t)
    {
        var p = t.LocationInView(v);
        return ((float)(p.X * _scale), (float)(p.Y * _scale));
    }

    public void Began(UIView v, UIEvent e)
    {
        var a = Active(v, e);
        if (a.Count == 1)
        {
            (_downX, _downY) = Pos(v, a[0]);
            _curX = _downX; _curY = _downY;
            _mode = Mode.Pending;
            _downAt = e.Timestamp * 1000;
            Post(MOVE, _downX, _downY);
            StartLongPress();
        }
        else if (a.Count == 2)
        {
            CancelLongPress();
            if (_mode == Mode.Drag) Post(LUP, _curX, _curY);
            if (_mode == Mode.Hold) Post(RUP, _downX, _downY);
            _mode = Mode.TwoFinger;
            _twoMoved = false;
            _lastY = (Pos(v, a[0]).y + Pos(v, a[1]).y) / 2f;
            _downAt = e.Timestamp * 1000;
        }
    }

    public void Moved(UIView v, UIEvent e)
    {
        var a = Active(v, e);
        if (a.Count == 0) return;
        var (x, y) = Pos(v, a[0]);
        _curX = x; _curY = y;

        if (_mode == Mode.TwoFinger && a.Count >= 2)
        {
            float m = (Pos(v, a[0]).y + Pos(v, a[1]).y) / 2f;
            while (Math.Abs(m - _lastY) >= _wheelStep)
            {
                int s = m > _lastY ? 1 : -1;
                _lastY += s * _wheelStep;
                _twoMoved = true;
                var (cx, cy) = Display.ToClient(x, y);
                WinMessages.PostMouse(WHEEL, cx, cy, (nint)((s * 120) << 16));
            }
        }
        else if (_mode == Mode.Pending && Math.Max(Math.Abs(x - _downX), Math.Abs(y - _downY)) > _slop)
        {
            CancelLongPress();
            if (GameHooks.CanPan) { _mode = Mode.Pan; PanTo(x, y, true); }
            else { _mode = Mode.Drag; Post(LDOWN, _downX, _downY); Post(MOVE, x, y); }
        }
        else if (_mode == Mode.Pan) PanTo(x, y, false);
        else if (_mode == Mode.Pending || _mode == Mode.Drag || _mode == Mode.Hold) Post(MOVE, x, y);
    }

    public void Ended(UIView v, UIEvent e)
    {
        var a = Active(v, e);
        if (a.Count == 0)
        {
            CancelLongPress();
            switch (_mode)
            {
                case Mode.Pending: Post(LDOWN, _downX, _downY); Post(LUP, _downX, _downY); break;
                case Mode.Drag: Post(LUP, _curX, _curY); break;
                case Mode.Hold: Post(RUP, _downX, _downY); break;
            }
            GameHooks.BlockEdgeScroll();
            _mode = Mode.None;
        }
        else if (a.Count == 1 && _mode == Mode.TwoFinger && !_twoMoved && e.Timestamp * 1000 - _downAt < 350)
        {
            Post(RDOWN, _curX, _curY);
            Post(RUP, _curX, _curY);
        }
    }

    public void Cancelled(UIView v, UIEvent e)
    {
        CancelLongPress();
        if (_mode == Mode.Drag) Post(LUP, _downX, _downY);
        if (_mode == Mode.Hold) Post(RUP, _downX, _downY);
        _mode = Mode.None;
    }

    void PanTo(float x, float y, bool first)
    {
        float px = first ? _downX : _lastX, py = first ? _downY : _lastY;
        _lastX = x; _lastY = y;
        GameHooks.Pan(Display.ToClientDistance(x - px), Display.ToClientDistance(y - py));
    }

    void StartLongPress()
    {
        CancelLongPress();
        _lp = NSTimer.CreateScheduledTimer(0.45, _ =>
        {
            if (_mode != Mode.Pending) return;
            _mode = Mode.Hold;
            new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Medium).ImpactOccurred();
            Post(RDOWN, _downX, _downY);
        });
    }

    void CancelLongPress() { _lp?.Invalidate(); _lp = null; }

 static void Post(uint msg, float x, float y)
    {
        var (cx, cy) = Display.ToClient(x, y);
        WinMessages.PostMouse(msg, cx, cy);
    }
}
