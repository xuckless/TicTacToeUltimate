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
        });
#endif

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var options = new ClientOptions
        {
            BackendBaseUrl = new Uri(ReadBackendBaseUrl() ?? DefaultBackendUrl()),
            WebBaseUrl = new Uri("http://localhost:5028"),
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
        s.Replace(ServiceDescriptor.Singleton<IPushService, StubPushService>());
        s.Replace(ServiceDescriptor.Singleton<IAdProvider, StubAdProvider>());
        s.AddTttClientFallbacks();

        return builder.Build();
    }

    private static string DefaultBackendUrl()
        => DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5091" : "http://localhost:5091";

    internal static ClientPlatform CurrentPlatform()
    {
        if (DeviceInfo.Platform == DevicePlatform.Android) return ClientPlatform.Android;
        if (DeviceInfo.Platform == DevicePlatform.MacCatalyst) return ClientPlatform.MacCatalyst;
        return ClientPlatform.iOS;
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
