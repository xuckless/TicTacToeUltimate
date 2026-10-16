using Foundation;
using TicTacToeUltimate.Services;
using UIKit;

namespace TicTacToeUltimate;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate, UserNotifications.IUNUserNotificationCenterDelegate
{
    protected override MauiApp CreateMauiApp()
    {
        var app = MauiProgram.CreateMauiApp();
        Google.MobileAds.MobileAds.SharedInstance.Start(completionHandler: null);
        UserNotifications.UNUserNotificationCenter.Current.Delegate = this;
        return app;
    }

    /// <summary>APNs device token → PushService (sent to the server after Hello).</summary>
    [Export("application:didRegisterForRemoteNotificationsWithDeviceToken:")]
    public void RegisteredForRemoteNotifications(UIApplication application, NSData deviceToken)
        => PushService.SetToken(Convert.ToHexString(deviceToken.ToArray()).ToLowerInvariant());

    [Export("application:didFailToRegisterForRemoteNotificationsWithError:")]
    public void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error) => PushService.SetToken(null);

    /// <summary>A tapped notification opens its link (e.g. social/chat/{id}, play?tc=Casual).</summary>
    [Export("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:")]
    public void DidReceiveNotificationResponse(UserNotifications.UNUserNotificationCenter center, UserNotifications.UNNotificationResponse response, Action completionHandler)
    {
        if (response.Notification.Request.Content.UserInfo?["link"] is NSString link && link.Length > 0)
            MauiDeepLinkSource.Current?.Publish($"https://{MauiDeepLinkSource.WebHost}/{link.ToString().TrimStart('/')}");
        completionHandler();
    }

    /// <summary>Show banners for pushes that arrive while the app is open.</summary>
    [Export("userNotificationCenter:willPresentNotification:withCompletionHandler:")]
    public void WillPresentNotification(UserNotifications.UNUserNotificationCenter center, UserNotifications.UNNotification notification, Action<UserNotifications.UNNotificationPresentationOptions> completionHandler)
        => completionHandler(UserNotifications.UNNotificationPresentationOptions.Banner | UserNotifications.UNNotificationPresentationOptions.Sound);

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
