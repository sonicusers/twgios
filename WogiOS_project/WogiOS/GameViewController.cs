using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using GameController;
using ObjCRuntime;
using UIKit;
using WogShim;

namespace WogiOS;

[Register("GameView")]
internal sealed class GameView : UIView
{
    public TouchInput Touch;

    [Export("layerClass")]
    public static Class GetLayerClass() => new Class(typeof(CAEAGLLayer));

    public GameView()
    {
        MultipleTouchEnabled = true;
        BackgroundColor = UIColor.Black;
        var l = (CAEAGLLayer)Layer;
        l.Opaque = true;
        ContentScaleFactor = UIScreen.MainScreen.Scale;
    }

    public CAEAGLLayer EaglLayer => (CAEAGLLayer)Layer;

    public override void TouchesBegan(NSSet touches, UIEvent evt) => Touch?.Began(this, evt);
    public override void TouchesMoved(NSSet touches, UIEvent evt) => Touch?.Moved(this, evt);
    public override void TouchesEnded(NSSet touches, UIEvent evt) => Touch?.Ended(this, evt);
    public override void TouchesCancelled(NSSet touches, UIEvent evt) => Touch?.Cancelled(this, evt);
}

internal sealed class GameViewController : UIViewController
{
    GameView _view;
    UILabel _status;
    SplashOverlay _splash;
    PadOverlay _pad;
    ShortcutOverlay _keys;
    UIButton _editBtn;
    UISlider _opacitySlider;
    bool _menuOpen, _sliderOpen;
    float _padAlpha;
    Thread _game;
    static readonly Queue<string> Lines = new();

    public override bool PrefersStatusBarHidden() => true;
    public override bool PrefersHomeIndicatorAutoHidden => true;
    public override UIInterfaceOrientationMask GetSupportedInterfaceOrientations() => UIInterfaceOrientationMask.Landscape;

    public override void LoadView()
    {
        _view = new GameView();
        _status = new UILabel
        {
            Lines = 0,
            TextColor = UIColor.LightGray,
            BackgroundColor = UIColor.Black,
            Font = UIFont.SystemFontOfSize(14),
            TextAlignment = UITextAlignment.Left,
            Frame = _view.Bounds,
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
        };
        _view.AddSubview(_status);
        BuildPad();
        // 실행 연출: 일러스트를 맨 위에 덮어 두고, 게임 첫 화면이 뜨면 서서히 사라진다
        _splash = SplashOverlay.Create(_view.Bounds);
        if (_splash != null) _view.AddSubview(_splash);
        View = _view;
    }

    public void ShowStatus(string line, bool error = false)
    {
        lock (Lines)
        {
            if (line != null)
            {
                Lines.Enqueue(line);
                while (Lines.Count > 14) Lines.Dequeue();
            }
        }
        var text = "창세기전3 파트2 — iOS 시제품\n\n" + string.Join("\n", Lines.ToArray());
        InvokeOnMainThread(() =>
        {
            _status.Text = text;
            if (error)
            {
                _status.TextColor = UIColor.FromRGB(255, 140, 140);
                _splash?.Dismiss();
            }
        });
    }

    // ---- 화면 패드 / 단축키 ----
    // 위쪽 [메뉴][단축키|패드|숨김] 은 ShortcutOverlay 가 그린다.
    // 단축키 모드 = 키보드 단축키 버튼, 패드 모드 = 기존 LT/LB/XYAB 화면 패드, 숨김 = 아무것도 안 보임.
    void BuildPad()
    {
        _pad = new PadOverlay();
        _view.AddSubview(_pad);

        _keys = new ShortcutOverlay(this, (float)UIScreen.MainScreen.Scale);
        _keys.ModeChanged += () => { _menuOpen = _sliderOpen = false; ApplyPad(); };
        // 패드 모드에서 '패드' 탭을 한 번 더 누르면 불투명도/편집 메뉴가 펼쳐진다
        _keys.PadTabTapped += () =>
        {
            _menuOpen = !_menuOpen;
            if (!_menuOpen) _sliderOpen = false;
            ApplyPad();
        };
        // 상단 '불투명도' 탭: 터치 UI(패드·단축키 버튼) 불투명도 슬라이더를 열고 닫는다
        _keys.OpacityTabTapped += () =>
        {
            _sliderOpen = !_sliderOpen;
            ApplyPad();
        };
        _view.AddSubview(_keys);

        // 패드 투명도 조절 (◐ 버튼을 누르면 슬라이더가 나타남)
        _padAlpha = NSUserDefaults.StandardUserDefaults.FloatForKey("padOpacity");
        if (_padAlpha < 0.15f || _padAlpha > 1f) _padAlpha = 0.6f;
        _pad.SetOpacity(_padAlpha);

        _opacitySlider = new UISlider
        {
            MinValue = 0.15f,
            MaxValue = 1f,
            Value = _padAlpha,
            Hidden = true,
            BackgroundColor = PadStyle.C(28, 36, 58, 0.85f),
        };
        _opacitySlider.Layer.CornerRadius = 12;
        _opacitySlider.ValueChanged += (s, e) =>
        {
            _padAlpha = _opacitySlider.Value;
            ApplyPadAlpha();
            NSUserDefaults.StandardUserDefaults.SetFloat(_padAlpha, "padOpacity");
        };
        _view.AddSubview(_opacitySlider);

        // 패드 배치 편집 (위치/크기를 직접 바꾸고 저장)
        _editBtn = MenuButton("편집");
        _editBtn.TouchUpInside += (s, e) =>
        {
            _menuOpen = _sliderOpen = false;
            _pad.SetEditing(true);
            ApplyPad();
        };
        _view.AddSubview(_editBtn);

        _pad.EditEnded += ApplyPad;
        ApplyPadAlpha();

        // 블루투스 컨트롤러가 연결/해제되면 화면 패드·단축키를 숨기거나 되살린다
        NSNotificationCenter.DefaultCenter.AddObserver(GCController.DidConnectNotification, _ => InvokeOnMainThread(ApplyPad));
        NSNotificationCenter.DefaultCenter.AddObserver(GCController.DidDisconnectNotification, _ => InvokeOnMainThread(ApplyPad));
        ApplyPad();
    }

    static UIButton MenuButton(string title)
    {
        var b = UIButton.FromType(UIButtonType.System);
        b.SetTitle(title, UIControlState.Normal);
        b.SetTitleColor(UIColor.White, UIControlState.Normal);
        b.TitleLabel.Font = UIFont.BoldSystemFontOfSize(14);
        b.BackgroundColor = PadStyle.C(28, 36, 58, 0.85f);
        b.Layer.CornerRadius = 17;
        b.Layer.BorderWidth = 2;
        b.Layer.BorderColor = PadStyle.Border.CGColor;
        return b;
    }

    // 패드·단축키 버튼·편집 버튼이 같은 투명도를 따른다. 편집 버튼은 너무 연해서 못 찾는 일이 없게 최소 35%는 유지한다.
    void ApplyPadAlpha()
    {
        _pad.SetOpacity(_padAlpha);
        _keys.SetUiOpacity(_padAlpha);
        _editBtn.Alpha = Math.Max(0.35f, _padAlpha);
    }

    void ApplyPad()
    {
        bool physical = PadInput.HasPhysical;
        bool padMode = _keys.Mode == ControlMode.Pad;
        bool show = padMode && !physical;
        _pad.Show(show);
        bool editing = _pad.Editing;
        if (physical || editing || !padMode) _menuOpen = false;
        if (physical || editing || _keys.Mode == ControlMode.Hidden) _sliderOpen = false;
        _keys.SetPhysical(physical);
        _keys.Hidden = editing;                       // 패드 위치를 편집하는 동안에는 위쪽 줄도 숨김
        if (_keys.Mode != ControlMode.Keys || physical) _keys.ReleaseAll();
        _editBtn.Hidden = !(_menuOpen && show);
        _opacitySlider.Hidden = !_sliderOpen;
        PadInput.SetOverlayActive(show);
    }

    void LayoutPad()
    {
        if (_pad == null) return;
        _pad.Frame = _view.Bounds;
        _keys.Frame = _view.Bounds;
        var ins = _view.SafeAreaInsets;
        nfloat W = _view.Bounds.Width, my = ins.Top + 44, g = 8, w1 = 100, w2 = 56;
        _editBtn.Frame = new CGRect((W - w2) / 2, my, w2, 34);
        _opacitySlider.Frame = new CGRect((W - 220) / 2, my + 34 + 8, 220, 32);
        // 메뉴/슬라이더가 단축키·패드 위에 오도록
        _view.BringSubviewToFront(_editBtn);
        _view.BringSubviewToFront(_opacitySlider);
    }

    public void HideStatus() => InvokeOnMainThread(() =>
    {
        _status.Hidden = true;
        _splash?.RequestHide();
    });

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        LayoutPad();
        if (_splash?.Superview != null)
        {
            _splash.Frame = _view.Bounds;
            _view.BringSubviewToFront(_splash);
        }
        if (_game != null || _view.Bounds.Width < 1) return;

        var host = new IosHost(_view);
        AppDelegate.Host = host;
        _view.Touch = new TouchInput((float)_view.ContentScaleFactor);
        host.FirstFrame = () => { GameRunner.MarkLaunchOk(); HideStatus(); };   // 첫 화면이 떴으니 "시작 중" 표시를 지운다
        _game = new Thread(() => GameRunner.Run(host, this), 64 * 1024 * 1024) { Name = "WarOfGenesis", IsBackground = true };
        _game.Start();
    }
}
