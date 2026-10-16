using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Realtime;

namespace TicTacToeUltimate.Server.Hosting;

/// <summary>WP0 core wiring. AddTttCore MUST be called last so TryAdd fallbacks lose to real implementations.</summary>
public static class CoreServiceCollectionExtensions
{
    public const string CorsPolicy = "web";

    public static IServiceCollection AddTttCore(this IServiceCollection services, IConfiguration cfg, IHostEnvironment env)
    {
        services.AddOptions<TttOptions>()
            .Bind(cfg.GetSection(TttOptions.Section))
            .Validate(o => o.Clock.LagCreditCapMs >= 0 && o.Clock.LagBudgetMs >= 0, "Ttt:Clock values must be >= 0")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        services.AddSignalR(o =>
            {
                o.MaximumReceiveMessageSize = 16 * 1024;
                o.EnableDetailedErrors = env.IsDevelopment();
                o.AddFilter<HubRateLimitFilter>();
            })
            .AddJsonProtocol(o => InsertContracts(o.PayloadSerializerOptions.TypeInfoResolverChain));
        services.Replace(ServiceDescriptor.Singleton<IUserIdProvider, SubUserIdProvider>());   // AddSignalR TryAdds DefaultUserIdProvider (NameIdentifier); we key users by "sub".

        services.AddAuthentication();
        services.AddAuthorization();
        services.AddTttRateLimiting(cfg);
        services.AddSingleton<HubRateLimitFilter>();

        var origins = cfg.GetSection("Ttt:Cors:Origins").Get<string[]>() ?? [];
        services.AddCors(o => o.AddPolicy(CorsPolicy, p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(o => InsertContracts(o.SerializerOptions.TypeInfoResolverChain));
        services.AddHealthChecks();
        services.AddOpenApi();

        services.TryAddSingleton<IMatchResultSink, NullMatchResultSink>();
        services.TryAddSingleton<IPresenceSink, NullPresenceSink>();
        services.TryAddSingleton<IBotPool, NullBotPool>();
        services.TryAddSingleton<ITablebaseCache, NullTablebaseCache>();
        services.TryAddSingleton<IAnalysisService, NullAnalysisService>();
        services.TryAddSingleton<IPushSender, LoggingPushSender>();
        services.TryAddSingleton<IPlayerCardService, DefaultPlayerCardService>();
        services.TryAddSingleton<IUserNotifier, UserNotifier>();
        return services;
    }

    private static void InsertContracts(IList<IJsonTypeInfoResolver> chain)
    {
        if (chain.Contains(ContractsJsonContext.Default)) return;
        chain.Insert(0, ContractsJsonContext.Default);
        if (chain.Count == 1) chain.Add(new DefaultJsonTypeInfoResolver());
    }

    public static WebApplication UseTttCore(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseCors(CorsPolicy);
        app.UseWebSockets();
        return app;
    }

    public static WebApplication MapTttCore(this WebApplication app)
    {
        app.MapHub<GameHub>(HubRoutes.Game).RequireCors(CorsPolicy);
        app.MapHealthChecks("/health").AllowAnonymous();
        if (app.Environment.IsDevelopment()) app.MapOpenApi();
        return app;
    }
}
