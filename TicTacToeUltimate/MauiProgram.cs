using Microsoft.Extensions.Logging;
using TicTacToeUltimate.Shared.Services;
using TicTacToeUltimate.Services;
using Plugin.AdMob;
using Plugin.AdMob.Configuration; // <-- needed for AdConfig

namespace TicTacToeUltimate;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseAdMob() // REQUIRED by Plugin.AdMob
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // Shared services
        builder.Services.AddSingleton<IFormFactor, FormFactor>();
        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();

        // Enable Google test AdUnitIds in Debug mode
        AdConfig.UseTestAdUnitIds = true;
#endif

        return builder.Build();
    }
}