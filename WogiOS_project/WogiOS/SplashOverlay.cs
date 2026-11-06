using System;
using CoreGraphics;
using Foundation;
using UIKit;

namespace WogiOS;

// 실행 직후 일러스트(Splash.png)를 보여 주다가, 게임 첫 화면이 그려지면 서서히 사라지는 스플래시.
// 이미지는 비율을 유지한 채(AspectFit) 화면에 맞추고, 남는 곳은 흰색으로 채운다. (iPhone / iPad 모두 동일)
internal sealed class SplashOverlay : UIImageView
{
    // ---- 조절용 값 (초) ----
    const double FadeInTime = 0.5;   // 나타나는 시간
    const double MinShowTime = 2.0;  // 게임이 아무리 빨리 떠도 최소한 이만큼은 보여 줌
    const double FadeOutTime = 1.2;  // 사라지는 시간
    const double MaxWaitTime = 4.0;  // 첫 화면이 이때까지 안 뜨면(첫 실행 다운로드 등) 먼저 사라져서 진행 글자가 보이게 함

    DateTime _shownAt;
    bool _shown, _closing;

    public static SplashOverlay Create(CGRect frame)
    {
        var img = UIImage.FromBundle("Splash.png");
        return img == null ? null : new SplashOverlay(frame, img);
    }

    SplashOverlay(CGRect frame, UIImage img) : base(frame)
    {
        Image = img;
        ContentMode = UIViewContentMode.ScaleAspectFit;
        BackgroundColor = UIColor.White;
        AutoresizingMask = UIViewAutoresizing.FlexibleDimensions;
        UserInteractionEnabled = false;
        Alpha = 0;
    }

    public override void MovedToWindow()
    {
        base.MovedToWindow();
        if (Window == null || _shown) return;
        _shown = true;
        _shownAt = DateTime.UtcNow;
        Animate(FadeInTime, () => Alpha = 1);
        NSTimer.CreateScheduledTimer(MaxWaitTime, _ => FadeOut());
    }

    // 게임 첫 화면이 그려졌을 때 호출 (메인 스레드). 최소 노출 시간이 남았으면 기다렸다가 사라진다.
    public void RequestHide()
    {
        if (_closing) return;
        var left = MinShowTime - (_shown ? (DateTime.UtcNow - _shownAt).TotalSeconds : 0);
        if (left <= 0.01) FadeOut();
        else NSTimer.CreateScheduledTimer(left, _ => FadeOut());
    }

    // 오류 화면을 가리지 않도록 즉시 제거
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Layer.RemoveAllAnimations();
        RemoveFromSuperview();
    }

    void FadeOut()
    {
        if (_closing) return;
        _closing = true;
        Animate(FadeOutTime, 0,
            UIViewAnimationOptions.CurveEaseInOut | UIViewAnimationOptions.BeginFromCurrentState,
            () => Alpha = 0,
            () => RemoveFromSuperview());
    }
}
