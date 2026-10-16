using System.Text.Json;
using Microsoft.Extensions.Logging;
using TicTacToeUltimate.Client;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Services;
using TicTacToeUltimate.Shared;

namespace TicTacToeUltimate;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#if IOS || ANDROID
        ConfigureAdMob(builder);
#endif
        builder.Services.AddMauiBlazorWebView();
#if IOS || MACCATALYST
        // Edge-to-edge web view: CSS env(safe-area-inset-*) owns the safe areas. Without this, WKWebView also insets the
        // scroll content, which doubles the top gap and makes the tab bar float on overscroll.
        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping("EdgeToEdge", (handler, _) =>
        {
            handler.PlatformView.ScrollView.ContentInsetAdjustmentBehavior = UIKit.UIScrollViewContentInsetAdjustmentBehavior.Never;
            handler.PlatformView.ScrollView.Bounces = false;
            handler.PlatformView.Opaque = false;
            handler.PlatformView.BackgroundColor = UIKit.UIColor.FromRGB(0x12, 0x0a, 0x22);
            // iOS edge swipe = back. Blazor uses pushState history, which WKWebView's gesture navigates.
            handler.PlatformView.AllowsBackForwardNavigationGestures = true;
        });
#endif
#if ANDROID
        // Hardware/gesture back goes back in the app's history before leaving the app (MainActivity.OnBackPressed).
        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping("BackHistory", (handler, _) =>
            TicTacToeUltimate.Platforms.Android.BackNavigation.WebView = handler.PlatformView);
#endif

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var options = new ClientOptions
        {
            BackendBaseUrl = new Uri(ReadBackendBaseUrl() ?? DefaultBackendUrl()),
#if DEBUG
            WebBaseUrl = new Uri(DeviceInfo.DeviceType == DeviceType.Physical ? ProductionUrl : "http://localhost:5028"),
#else
            WebBaseUrl = new Uri(ProductionUrl),
#endif
            Platform = CurrentPlatform(),
#if DEBUG
            IsDevelopment = true,
#endif
        };

        builder.Services.AddTttClient(options);
        builder.Services.AddTttSharedUi();
        // Host overrides: the last registration wins over the Shared JS defaults.
        var s = builder.Services;
        s.AddSingleton<MauiLifecycle>();
        s.AddSingleton<MauiDeepLinkSource>();
        s.Replace(ServiceDescriptor.Singleton<ISecureStore, MauiSecureStore>());
        s.Replace(ServiceDescriptor.Singleton<IKeyValueStore, MauiKeyValueStore>());
        s.Replace(ServiceDescriptor.Singleton<IHaptics, MauiHaptics>());
        s.Replace(ServiceDescriptor.Singleton<IPlatformInfo, MauiPlatformInfo>());
        s.Replace(ServiceDescriptor.Singleton<IAppLifecycle>(sp => sp.GetRequiredService<MauiLifecycle>()));
        s.Replace(ServiceDescriptor.Singleton<IDeepLinkSource>(sp => sp.GetRequiredService<MauiDeepLinkSource>()));
        s.Replace(ServiceDescriptor.Singleton<IShareService, MauiShareService>());
        s.Replace(ServiceDescriptor.Singleton<IPushService, PushService>());
        s.Replace(ServiceDescriptor.Singleton<TicTacToeUltimate.Client.Diagnostics.IDeviceDescription, MauiDeviceDescription>());
        s.AddSingleton<IBannerAds, NativeBannerAds>();   // the native AdMob banner lives under the web view; the HTML slot renders nothing
#if IOS || ANDROID
        s.AddSingleton<AdMobAdProvider>();
        s.Replace(ServiceDescriptor.Singleton<IAdProvider>(sp => sp.GetRequiredService<AdMobAdProvider>()));
#else
        s.Replace(ServiceDescriptor.Singleton<IAdProvider, NoAdsProvider>());
#endif
        s.AddTttClientFallbacks();

        var app = builder.Build();
        NativeCrashFile.Install();
        var crashes = app.Services.GetRequiredService<TicTacToeUltimate.Client.Diagnostics.CrashReporter>();
        crashes.InstallGlobalHandlers();
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(5)); await NativeCrashFile.SendPendingAsync(crashes); });
#if DEBUG
        // Dev hook: SIMCTL_CHILD_TTT_DEV_ROUTE=dev/ads?run=rewarded xcrun simctl launch … opens a route at startup.
        if (Environment.GetEnvironmentVariable("TTT_DEV_ROUTE") is { Length: > 0 } route)
            (app.Services.GetService<IDeepLinkSource>() as MauiDeepLinkSource)?.Publish("tttu://" + route);
#endif
        return app;
    }

    /// <summary>Production site; it also proxies the game API (/api, /hubs).</summary>
    private const string ProductionUrl = "https://tictactoeultimate.alidirect.dev";

    // Debug on a simulator/emulator talks to the local dev server; Debug on a real phone (no "localhost" there) and every
    // Release build talk to production. Override with Backend.BaseUrl in Resources/Raw/appsettings.json.
    private static string DefaultBackendUrl()
#if DEBUG
        => DeviceInfo.DeviceType == DeviceType.Physical ? ProductionUrl
            : DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5091" : "http://localhost:5091";
#else
        => ProductionUrl;
#endif

    internal static ClientPlatform CurrentPlatform()
    {
        if (DeviceInfo.Platform == DevicePlatform.Android) return ClientPlatform.Android;
        if (DeviceInfo.Platform == DevicePlatform.MacCatalyst) return ClientPlatform.MacCatalyst;
        return ClientPlatform.iOS;
    }

#if IOS || ANDROID
    /// <summary>
    /// AdMob ad units from appsettings.json (Ads:AdMob:{Android|iOS}:{Interstitial|Rewarded}). Missing ids, or a Debug
    /// build, use Google's test units, so a dev build can never earn invalid traffic on the real account.
    /// </summary>
    private static void ConfigureAdMob(MauiAppBuilder builder)
    {
        var platform = DeviceInfo.Platform == DevicePlatform.Android ? "Android" : "iOS";
        var interstitial = ReadSetting("Ads", "AdMob", platform, "Interstitial");
        var rewarded = ReadSetting("Ads", "AdMob", platform, "Rewarded");
        var banner = ReadSetting("Ads", "AdMob", platform, "Banner");
        Plugin.AdMob.Configuration.AdConfig.DefaultInterstitialAdUnitId = interstitial;
        Plugin.AdMob.Configuration.AdConfig.DefaultRewardedAdUnitId = rewarded;
        Plugin.AdMob.Configuration.AdConfig.DefaultBannerAdUnitId = banner;
#if DEBUG
        Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds = true;
#else
        Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds = interstitial is null || rewarded is null;
#endif
        Plugin.AdMob.Config.UseAdMob(builder);
    }
#endif

    /// <summary>Reads a nested string from the embedded appsettings.json (null when missing or empty).</summary>
    private static string? ReadSetting(params string[] path)
    {
        try
        {
            using var stream = typeof(MauiProgram).Assembly.GetManifestResourceStream("appsettings.json");
            if (stream is null) return null;
            using var doc = JsonDocument.Parse(stream);
            var e = doc.RootElement;
            foreach (var key in path)
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out e)) return null;
            return e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } v ? v : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Reads Backend:BaseUrl from the embedded Resources/Raw/appsettings.json (empty = platform default).</summary>
    private static string? ReadBackendBaseUrl()
    {
        try
        {
            using var stream = typeof(MauiProgram).Assembly.GetManifestResourceStream("appsettings.json");
            if (stream is null) return null;
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("Backend", out var b) && b.TryGetProperty("BaseUrl", out var u)
                && u.GetString() is { Length: > 0 } url)
                return url;
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
