using Foundation;
using TicTacToeUltimate.Services;
using UIKit;

namespace TicTacToeUltimate;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp()
    {
        var app = MauiProgram.CreateMauiApp();
        Google.MobileAds.MobileAds.SharedInstance.Start(completionHandler: null);
        return app;
    }

    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
    {
        MauiDeepLinkSource.Current?.Publish(url.AbsoluteString);
        return base.OpenUrl(application, url, options) || url.Scheme == MauiDeepLinkSource.Scheme;
    }

    /// <summary>Universal links (https://tictactoeultimate.alidirect.dev/c/CODE).</summary>
    public override bool ContinueUserActivity(UIApplication application, NSUserActivity userActivity, UIApplicationRestorationHandler completionHandler)
    {
        if (userActivity.ActivityType == NSUserActivityType.BrowsingWeb && userActivity.WebPageUrl is { } url)
        {
            MauiDeepLinkSource.Current?.Publish(url.AbsoluteString);
            return true;
        }
        return base.ContinueUserActivity(application, userActivity, completionHandler);
    }
}
