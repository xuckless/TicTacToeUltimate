using Foundation;
using TicTacToeUltimate.Services;
using UIKit;

namespace TicTacToeUltimate;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
    {
        MauiDeepLinkSource.Current?.Publish(url.AbsoluteString);
        return base.OpenUrl(application, url, options) || url.Scheme == MauiDeepLinkSource.Scheme;
    }
}
