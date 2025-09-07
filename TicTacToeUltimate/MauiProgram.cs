using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;              // DI extensions (AddSingleton, AddHttpClient, etc.)
using TicTacToeUltimate.Shared.Services;                    // ApiClient lives here
using TicTacToeUltimate.Services;                           // FormFactor service
using Plugin.AdMob;                                         // AdMob plugin bootstrap
using Plugin.AdMob.Configuration;                           // AdMob test IDs
using Microsoft.AspNetCore.SignalR.Client;                  // SignalR client
using Microsoft.Maui.Devices;                               // DeviceInfo & DeviceType
using System.Net.Http;

namespace TicTacToeUltimate;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Create the MAUI app builder
        var builder = MauiApp.CreateBuilder();

        // Core app + plugins + fonts
        builder
            .UseMauiApp<App>()                               // App entry point
            .UseAdMob()                                      // Initialize AdMob plugin
            .ConfigureFonts(f => f.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));

        // App-wide services
        builder.Services.AddSingleton<IFormFactor, FormFactor>(); // Helper for device sizing / platform quirks
        builder.Services.AddMauiBlazorWebView();                  // BlazorWebView host for UI

#if DEBUG
        // Dev-only helpers
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();                           // Console debug logs
        AdConfig.UseTestAdUnitIds = true;                    // Always use test ad units in debug
#endif

        // -------------------------------
        // Backend endpoint discovery
        // -------------------------------
        // We probe a few candidate base URLs depending on platform/emulator.
        // • MacCatalyst: localhost is fine
        // • iOS simulator: localhost on the Mac is visible
        // • iOS device: use your LAN IP
        // • Android emulator: 10.0.2.2 is the host loopback
        const string LAN_IP = "192.168.1.123"; // set if testing on a physical iPhone
        string[] candidates;

        if (DeviceInfo.Platform == DevicePlatform.MacCatalyst)
        {
            candidates = new[] { "https://localhost:7086", "http://localhost:5091" };
        }
        else if (DeviceInfo.Platform == DevicePlatform.iOS)
        {
            bool isSimulator = DeviceInfo.DeviceType == DeviceType.Virtual;
            candidates = isSimulator
                ? new[] { "https://localhost:7086", "http://localhost:5091" }
                : new[] { $"https://{LAN_IP}:7086", $"http://{LAN_IP}:5091" };
        }
        else if (DeviceInfo.Platform == DevicePlatform.Android)
        {
            candidates = new[] { "http://10.0.2.2:5091", "https://10.0.2.2:7086" };
        }
        else
        {
            candidates = new[] { "https://localhost:7086", "http://localhost:5091" };
        }

        // Lightweight logger for the probe
        var loggerFactory = LoggerFactory.Create(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Information));
        var logger = loggerFactory.CreateLogger("BackendProbe");

        // Pick the first candidate that replies to GET /ping with "pong"
        string backendBase = ProbeCandidates(candidates, logger);

        // -------------------------------
        // Typed HttpClient for our API
        // -------------------------------
        // Register AFTER backendBase is known so the client has the correct BaseAddress.
        builder.Services.AddHttpClient<ApiClient>(c => c.BaseAddress = new Uri(backendBase))
#if DEBUG
            // In debug, relax HTTPS cert validation to simplify localhost testing.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true
            })
#endif
        ; // keep semicolon outside the #if

        // -------------------------------
        // SignalR client to Test hub
        // -------------------------------
        // LongPolling is chosen so the same HttpClientHandler/cert bypass can apply in debug.
        builder.Services.AddSingleton(sp =>
            new HubConnectionBuilder()
                .WithUrl($"{backendBase}/hubs/test", o =>
                {
                    o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
#if DEBUG
                    o.HttpMessageHandlerFactory = _ => new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true
                    };
#endif
                })
                .WithAutomaticReconnect()
                .ConfigureLogging(lb => lb.SetMinimumLevel(LogLevel.Debug))
                .Build());

        // Build the MAUI app
        return builder.Build();
    }

    /// <summary>
    /// Tries each URL in <paramref name="urls"/> by calling GET /ping and returns the first base that responds with "pong".
    /// Falls back to the first URL if none respond (so the app still boots and logs an error).
    /// </summary>
    private static string ProbeCandidates(string[] urls, ILogger logger)
    {
        using var handler = new HttpClientHandler();
#if DEBUG
        // Accept self-signed certs in debug for local HTTPS endpoints.
        handler.ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true;
#endif
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(1500) };

        foreach (var url in urls)
        {
            try
            {
                var resp = client.GetAsync($"{url}/ping").Result;
                if (resp.IsSuccessStatusCode)
                {
                    var txt = resp.Content.ReadAsStringAsync().Result.Trim();
                    if (txt.Equals("pong", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogInformation("Backend reachable: {Url}", url);
                        return url;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Probe failed for {Url}: {Msg}", url, ex.Message);
            }
        }

        logger.LogError("No backend found, using first candidate: {Url}", urls.First());
        return urls.First();
    }
}