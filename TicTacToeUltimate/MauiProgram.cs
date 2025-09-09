using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using TicTacToeUltimate.Shared.Services;
using TicTacToeUltimate.Services;
using Plugin.AdMob;
using Plugin.AdMob.Configuration;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Maui.Devices;
using System.Net.Http;

namespace TicTacToeUltimate;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseAdMob()
            .ConfigureFonts(f => f.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));

        builder.Services.AddSingleton<IFormFactor, FormFactor>();
        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
        AdConfig.UseTestAdUnitIds = true;
#endif

        const string LAN_IP = "192.168.1.123";
        string[] candidates;

        if (DeviceInfo.Platform == DevicePlatform.MacCatalyst)
            candidates = new[] { "https://localhost:7086", "http://localhost:5091" };
        else if (DeviceInfo.Platform == DevicePlatform.iOS)
            candidates = (DeviceInfo.DeviceType == DeviceType.Virtual)
                ? new[] { "https://localhost:7086", "http://localhost:5091" }
                : new[] { $"https://{LAN_IP}:7086", $"http://{LAN_IP}:5091" };
        else if (DeviceInfo.Platform == DevicePlatform.Android)
            candidates = new[] { "http://10.0.2.2:5091", "https://10.0.2.2:7086" };
        else
            candidates = new[] { "https://localhost:7086", "http://localhost:5091" };

        var loggerFactory = LoggerFactory.Create(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Information));
        var logger = loggerFactory.CreateLogger("BackendProbe");

        string backendBase = ProbeCandidates(candidates, logger);

        builder.Services.AddHttpClient<ApiClient>(c => c.BaseAddress = new Uri(backendBase))
#if DEBUG
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true
            })
#endif
        ;

        // TestHub connection
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

        // GameHub connection
        builder.Services.AddSingleton(sp =>
            new HubConnectionBuilder()
                .WithUrl($"{backendBase}/hubs/game", o =>
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

        builder.Services.AddSingleton<GameClient>();

        return builder.Build();
    }

    private static string ProbeCandidates(string[] urls, ILogger logger)
    {
        using var handler = new HttpClientHandler();
#if DEBUG
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
                    var txt = client.GetStringAsync($"{url}/ping").Result.Trim();
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