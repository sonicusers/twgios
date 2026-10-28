using AVFoundation;
using Foundation;
using UIKit;

namespace WogiOS;

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public override UIWindow Window { get; set; }
    internal static IosHost Host;

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.Playback, out _);
        session.SetActive(true, out _);

        Window = new UIWindow(UIScreen.MainScreen.Bounds) { BackgroundColor = UIColor.Black };
        Window.RootViewController = new GameViewController();
        Window.MakeKeyAndVisible();
        UIApplication.SharedApplication.IdleTimerDisabled = true;
        return true;
    }

    // 백그라운드에서는 GL 호출이 금지되므로 게임 스레드를 멈춘다
    public override void DidEnterBackground(UIApplication application) => Host?.SetActive(false);
    public override void WillEnterForeground(UIApplication application) => Host?.SetActive(true);
}
