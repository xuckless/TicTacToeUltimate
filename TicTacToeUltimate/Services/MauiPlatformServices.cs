using Microsoft.Extensions.Logging;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Services;

/// <summary>Tokens in the platform keychain/keystore.</summary>
public sealed class MauiSecureStore(ILogger<MauiSecureStore> logger) : ISecureStore
{
    /// <inheritdoc />
    public async ValueTask<string?> GetAsync(string key)
    {
        try { return await SecureStorage.Default.GetAsync(key); }
        catch (Exception ex) { logger.LogWarning(ex, "SecureStorage read failed"); return Preferences.Default.Get<string?>("secure." + key, null); }
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(string key, string value)
    {
        try { await SecureStorage.Default.SetAsync(key, value); }
        catch (Exception ex)
        {
            // Unsigned simulator / Catalyst builds may lack keychain entitlements: fall back to preferences.
            logger.LogWarning(ex, "SecureStorage write failed; using Preferences");
            Preferences.Default.Set("secure." + key, value);
        }
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key)
    {
        try { SecureStorage.Default.Remove(key); } catch (Exception ex) { logger.LogDebug(ex, "SecureStorage remove failed"); }
        Preferences.Default.Remove("secure." + key);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Non-secret preferences.</summary>
public sealed class MauiKeyValueStore : IKeyValueStore
{
    /// <inheritdoc />
    public ValueTask<string?> GetAsync(string key) => ValueTask.FromResult(Preferences.Default.Get<string?>(key, null));
    /// <inheritdoc />
    public ValueTask SetAsync(string key, string value) { Preferences.Default.Set(key, value); return ValueTask.CompletedTask; }
    /// <inheritdoc />
    public ValueTask RemoveAsync(string key) { Preferences.Default.Remove(key); return ValueTask.CompletedTask; }
}

/// <summary>HapticFeedback/Vibration on phones; no-op on Mac Catalyst.</summary>
public sealed class MauiHaptics : IHaptics
{
    /// <inheritdoc />
    public bool IsSupported => DeviceInfo.Platform != DevicePlatform.MacCatalyst && HapticFeedback.Default.IsSupported;

    /// <inheritdoc />
    public void Play(HapticCue cue)
    {
        if (!IsSupported) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                switch (cue)
                {
                    case HapticCue.Win or HapticCue.Loss or HapticCue.Error or HapticCue.Promotion or HapticCue.Warning:
                        if (Vibration.Default.IsSupported) Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(cue == HapticCue.Error ? 60 : 120));
                        else HapticFeedback.Default.Perform(HapticFeedbackType.LongPress);
                        break;
                    default:
                        HapticFeedback.Default.Perform(HapticFeedbackType.Click);
                        break;
                }
            }
            catch (Exception) { /* haptics are best effort */ }
        });
    }
}

/// <summary>Platform info from MAUI Essentials.</summary>
public sealed class MauiPlatformInfo : IPlatformInfo
{
    /// <inheritdoc />
    public ClientPlatform Platform => MauiProgram.CurrentPlatform();
    /// <inheritdoc />
    public bool IsTouch => DeviceInfo.Platform != DevicePlatform.MacCatalyst;
    /// <inheritdoc />
    public bool SupportsHaptics => DeviceInfo.Platform != DevicePlatform.MacCatalyst;
    /// <inheritdoc />
    public string AppVersion => AppInfo.Current.VersionString;
}

/// <summary>Window lifecycle (wired by <see cref="App"/>) plus Connectivity.</summary>
public sealed class MauiLifecycle : IAppLifecycle
{
    /// <summary>Subscribes to connectivity changes.</summary>
    public MauiLifecycle()
    {
        Connectivity.Current.ConnectivityChanged += (_, e) => NetworkChanged?.Invoke(e.NetworkAccess == NetworkAccess.Internet);
    }

    /// <inheritdoc />
    public bool IsForeground { get; private set; } = true;
    /// <inheritdoc />
    public bool IsOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
    /// <inheritdoc />
    public event Action? Resumed;
    /// <inheritdoc />
    public event Action? Suspended;
    /// <inheritdoc />
    public event Action<bool>? NetworkChanged;

    /// <summary>Hooks a window's Resumed/Stopped events.</summary>
    public void Attach(Window window)
    {
        window.Resumed += (_, _) => { IsForeground = true; Resumed?.Invoke(); };
        window.Activated += (_, _) => IsForeground = true;
        window.Stopped += (_, _) => { IsForeground = false; Suspended?.Invoke(); };
    }
}

/// <summary>Deep links with scheme <c>tttu</c> (e.g. <c>tttu://c/{code}</c>), published by the platform entry points.</summary>
public sealed class MauiDeepLinkSource : IDeepLinkSource
{
    /// <summary>The scheme.</summary>
    public const string Scheme = "tttu";
    /// <summary>Web host whose https links the app also opens (universal / app links).</summary>
    public const string WebHost = "tictactoeultimate.alidirect.dev";

    /// <inheritdoc />
    public Uri? InitialLink { get; private set; }
    /// <inheritdoc />
    public event Action<Uri>? LinkOpened;

    /// <summary>Publishes a link from the platform (intent / openURL).</summary>
    public void Publish(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        var custom = string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase);
        var web = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) && string.Equals(uri.Host, WebHost, StringComparison.OrdinalIgnoreCase);
        if (!custom && !web) return;
        if (LinkOpened is null) InitialLink = uri;
        else LinkOpened.Invoke(uri);
    }

    /// <summary>Resolves the singleton from the running app, if any.</summary>
    public static MauiDeepLinkSource? Current => IPlatformApplication.Current?.Services.GetService<IDeepLinkSource>() as MauiDeepLinkSource;
}

/// <summary>Native share sheet.</summary>
public sealed class MauiShareService : IShareService
{
    /// <inheritdoc />
    public async ValueTask<string> ShareLinkAsync(string title, string text, Uri url)
    {
        try
        {
            await Share.Default.RequestAsync(new ShareTextRequest { Title = title, Text = text, Uri = url.ToString() });
            return "shared";
        }
        catch (Exception)
        {
            try { await Clipboard.Default.SetTextAsync(url.ToString()); return "copied"; }
            catch (Exception) { return "failed"; }
        }
    }
}
