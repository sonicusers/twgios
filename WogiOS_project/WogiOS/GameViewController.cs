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
    PadOverlay _pad;
    UIButton _padToggle, _opacityBtn;
    UISlider _opacitySlider;
    bool _padWanted;
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
            if (error) _status.TextColor = UIColor.FromRGB(255, 140, 140);
        });
    }

    // ---- 화면 패드 ----
    void BuildPad()
    {
        _padWanted = NSUserDefaults.StandardUserDefaults.BoolForKey("padVisible");
        _pad = new PadOverlay();
        _view.AddSubview(_pad);

        _padToggle = UIButton.FromType(UIButtonType.System);
        _padToggle.SetTitle("패드", UIControlState.Normal);
        _padToggle.SetTitleColor(UIColor.White, UIControlState.Normal);
        _padToggle.TitleLabel.Font = UIFont.BoldSystemFontOfSize(15);
        _padToggle.BackgroundColor = PadStyle.C(28, 36, 58, 0.85f);
        _padToggle.Layer.CornerRadius = 17;
        _padToggle.Layer.BorderWidth = 2;
        _padToggle.Layer.BorderColor = PadStyle.Border.CGColor;
        _padToggle.TouchUpInside += (s, e) =>
        {
            _padWanted = !_padWanted;
            NSUserDefaults.StandardUserDefaults.SetBool(_padWanted, "padVisible");
            ApplyPad();
        };
        _view.AddSubview(_padToggle);

        // 패드 투명도 조절 (◐ 버튼을 누르면 슬라이더가 나타남)
        _padAlpha = NSUserDefaults.StandardUserDefaults.FloatForKey("padOpacity");
        if (_padAlpha < 0.15f || _padAlpha > 1f) _padAlpha = 0.6f;
        _pad.SetOpacity(_padAlpha);

        _opacityBtn = UIButton.FromType(UIButtonType.System);
        _opacityBtn.SetTitle("◐", UIControlState.Normal);
        _opacityBtn.SetTitleColor(UIColor.White, UIControlState.Normal);
        _opacityBtn.TitleLabel.Font = UIFont.BoldSystemFontOfSize(18);
        _opacityBtn.BackgroundColor = PadStyle.C(28, 36, 58, 0.85f);
        _opacityBtn.Layer.CornerRadius = 17;
        _opacityBtn.Layer.BorderWidth = 2;
        _opacityBtn.Layer.BorderColor = PadStyle.Border.CGColor;
        _opacityBtn.TouchUpInside += (s, e) => _opacitySlider.Hidden = !_opacitySlider.Hidden;
        _view.AddSubview(_opacityBtn);

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
        ApplyPadAlpha();

        // 블루투스 컨트롤러가 연결/해제되면 화면 패드를 숨기거나 되살린다
        NSNotificationCenter.DefaultCenter.AddObserver(GCController.DidConnectNotification, _ => InvokeOnMainThread(ApplyPad));
        NSNotificationCenter.DefaultCenter.AddObserver(GCController.DidDisconnectNotification, _ => InvokeOnMainThread(ApplyPad));
        ApplyPad();
    }

    // 패드와 '패드'/◐ 버튼이 같은 투명도를 따른다. 버튼은 너무 연해서 못 찾는 일이 없게 최소 35%는 유지한다.
    void ApplyPadAlpha()
    {
        _pad.SetOpacity(_padAlpha);
        float ui = Math.Max(0.35f, _padAlpha);
        _padToggle.Alpha = ui;
        _opacityBtn.Alpha = ui;
    }

    void ApplyPad()
    {
        bool physical = PadInput.HasPhysical;
        bool show = _padWanted && !physical;
        _pad.Show(show);
        _padToggle.Hidden = physical;
        _opacityBtn.Hidden = !show;
        if (!show) _opacitySlider.Hidden = true;
        PadInput.SetOverlayActive(show);
    }

    void LayoutPad()
    {
        if (_pad == null) return;
        _pad.Frame = _view.Bounds;
        var ins = _view.SafeAreaInsets;
        nfloat tx = (_view.Bounds.Width - 84) / 2, ty = ins.Top + 6;
        _padToggle.Frame = new CGRect(tx, ty, 84, 34);
        _opacityBtn.Frame = new CGRect(tx + 84 + 8, ty, 34, 34);
        _opacitySlider.Frame = new CGRect((_view.Bounds.Width - 220) / 2, ty + 34 + 8, 220, 32);
    }

    public void HideStatus() => InvokeOnMainThread(() => _status.Hidden = true);

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        LayoutPad();
        if (_game != null || _view.Bounds.Width < 1) return;

        var host = new IosHost(_view);
        AppDelegate.Host = host;
        _view.Touch = new TouchInput((float)_view.ContentScaleFactor);
        host.FirstFrame = HideStatus;
        _game = new Thread(() => GameRunner.Run(host, this), 64 * 1024 * 1024) { Name = "WarOfGenesis", IsBackground = true };
        _game.Start();
    }
}
