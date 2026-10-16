using TicTacToeUltimate.Client.Abstractions;

namespace TicTacToeUltimate.Services;

/// <summary>
/// Remote notifications. iOS: UNUserNotificationCenter permission + APNs device token (AppDelegate hands it over).
/// Android: FCM is a follow-up (returns no token). The token goes to POST /api/push/register.
/// </summary>
public sealed class PushService : IPushService
{
    private static readonly TaskCompletionSource<string?> TokenSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Called by the platform when the OS hands out (or refuses) a device token.</summary>
    public static void SetToken(string? hexToken)
    {
        TokenSource.TrySetResult(hexToken);
        Current = hexToken;
    }

    /// <summary>Last token (hex), if any.</summary>
    public static string? Current { get; private set; }

    /// <inheritdoc />
    public async ValueTask<bool> RequestPermissionAsync()
    {
#if IOS
        var center = UserNotifications.UNUserNotificationCenter.Current;
        var settings = await center.GetNotificationSettingsAsync();
        bool granted;
        if (settings.AuthorizationStatus == UserNotifications.UNAuthorizationStatus.NotDetermined)
        {
            var (ok, _) = await center.RequestAuthorizationAsync(UserNotifications.UNAuthorizationOptions.Alert | UserNotifications.UNAuthorizationOptions.Badge | UserNotifications.UNAuthorizationOptions.Sound);
            granted = ok;
        }
        else granted = settings.AuthorizationStatus is UserNotifications.UNAuthorizationStatus.Authorized or UserNotifications.UNAuthorizationStatus.Provisional;
        if (granted) MainThread.BeginInvokeOnMainThread(() => UIKit.UIApplication.SharedApplication.RegisterForRemoteNotifications());
        return granted;
#else
        await Task.CompletedTask;
        return false;
#endif
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetTokenAsync()
    {
        if (Current is not null) return Current;
        var done = await Task.WhenAny(TokenSource.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        return done == TokenSource.Task ? TokenSource.Task.Result : null;
    }
}
