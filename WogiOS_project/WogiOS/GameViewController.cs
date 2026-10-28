using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CoreAnimation;
using CoreGraphics;
using Foundation;
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

    public void HideStatus() => InvokeOnMainThread(() => _status.Hidden = true);

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        if (_game != null || _view.Bounds.Width < 1) return;

        var host = new IosHost(_view);
        AppDelegate.Host = host;
        _view.Touch = new TouchInput((float)_view.ContentScaleFactor);
        host.FirstFrame = HideStatus;
        _game = new Thread(() => GameRunner.Run(host, this), 64 * 1024 * 1024) { Name = "WarOfGenesis", IsBackground = true };
        _game.Start();
    }
}
