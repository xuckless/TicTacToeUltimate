using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.Bots;

/// <summary>WP4 wiring: bot pool, BotKey auth scheme, bot WebSocket and key endpoints.</summary>
public static class BotsServiceCollectionExtensions
{
    /// <summary>Registers <see cref="BotPool"/> as <see cref="IBotPool"/>, the BotKey scheme and the Bot policy.</summary>
    public static IServiceCollection AddTttBots(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton<BotPool>();
        services.AddSingleton<IBotPool>(sp => sp.GetRequiredService<BotPool>());
        // Dispatcher to the external bot service (bots/, Go): HTTP when Ttt:Bots:Service:Url is set, otherwise a no-op.
        services.AddHttpClient<HttpBotDispatcher>(c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<IBotDispatcher>(sp =>
            string.IsNullOrWhiteSpace(sp.GetRequiredService<TicTacToeUltimate.Server.Hosting.TttOptions>().Bots.Service.Url)
                ? new NullBotDispatcher()
                : sp.GetRequiredService<HttpBotDispatcher>());
        services.AddSingleton<BotKeyResolver>();
        services.AddHostedService<DevBotUserSeeder>();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, BotKeyAuthenticationHandler>(BotKeyDefaults.Scheme, null);

        services.Configure<AuthorizationOptions>(o =>
        {
            var botPolicy = new AuthorizationPolicyBuilder(BotKeyDefaults.Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(TttClaims.Kind, "bot", "Bot")
                .Build();
            o.AddPolicy(TttPolicies.Bot, botPolicy);
            if (o.GetPolicy(TttPolicies.Account) is null)
                o.AddPolicy(TttPolicies.Account, p => p.RequireAuthenticatedUser().RequireClaim(TttClaims.Kind, "account", "Account"));
        });
        return services;
    }

    /// <summary>Maps <c>/bot/ws</c> (when <c>Bots:Enabled</c>) and <c>/api/bots/keys</c>.</summary>
    public static WebApplication MapTttBots(this WebApplication app)
    {
        if (app.Configuration.GetValue("Ttt:Bots:Enabled", true)) app.MapBotWebSocket();
        app.MapBotKeyEndpoints();
        return app;
    }
}
