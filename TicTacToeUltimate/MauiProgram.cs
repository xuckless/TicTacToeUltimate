using System.Text.Json;
using Microsoft.Extensions.Logging;
using TicTacToeUltimate.Client;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Shared;

namespace TicTacToeUltimate;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();

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
        // WP5: Replace(...) with Maui* platform services here.

        return builder.Build();
    }

    private static string DefaultBackendUrl()
        => DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5091" : "http://localhost:5091";

    private static ClientPlatform CurrentPlatform()
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
