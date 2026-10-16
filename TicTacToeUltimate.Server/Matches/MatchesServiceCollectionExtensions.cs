using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Challenges;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matchmaking;
using TicTacToeUltimate.Server.Presence;

namespace TicTacToeUltimate.Server.Matches;

/// <summary>WP2 wiring: actors, registry, matchmaking, challenges, presence.</summary>
public static class MatchesServiceCollectionExtensions
{
    /// <summary>Registers WP2 services. Dependencies owned by other WPs are resolved lazily via factories.</summary>
    public static IServiceCollection AddTttMatches(this IServiceCollection services, IConfiguration cfg)
    {
        services.TryAddSingleton<IGameRules, EngineGameRules>();
        services.TryAddSingleton<IClockSchedule, EngineClockSchedule>();
        services.TryAddSingleton<IMatchBroadcaster, HubMatchBroadcaster>();
        services.AddSingleton<UserActivityRegistry>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<TttOptions>>().Value);

        services.AddSingleton(sp => new MatchRuntime(
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IGameRules>(), sp.GetRequiredService<IClockSchedule>(),
            sp.GetRequiredService<TttOptions>(), sp.GetRequiredService<IMatchBroadcaster>(), sp.GetRequiredService<IMatchResultSink>(),
            sp.GetRequiredService<IUserNotifier>(), sp.GetRequiredService<IPushSender>(), sp.GetRequiredService<IPlayerCardService>(),
            sp.GetRequiredService<UserActivityRegistry>(), sp.GetRequiredService<ILoggerFactory>(), sp.GetService<IDailyMatchRepository>()));
        services.AddSingleton(sp => new MatchGateway(sp.GetRequiredService<MatchRuntime>()));
        services.AddSingleton<IMatchGateway>(sp => sp.GetRequiredService<MatchGateway>());

        services.AddSingleton(sp => new PresenceTracker(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IPresenceSink>(),
            sp.GetRequiredService<UserActivityRegistry>(), sp.GetRequiredService<ILogger<PresenceTracker>>()));
        services.AddSingleton<IPresenceTracker>(sp => sp.GetRequiredService<PresenceTracker>());
        services.AddSingleton(sp => new RttTracker(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<TttOptions>>()));
        services.AddHostedService(sp => new RttPinger(sp.GetRequiredService<PresenceTracker>(), sp.GetRequiredService<RttTracker>(),
            sp.GetRequiredService<MatchGateway>(), sp.GetRequiredService<IUserNotifier>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<TttOptions>>(), sp.GetRequiredService<ILogger<RttPinger>>()));

        services.AddSingleton(sp => new Matchmaker(sp.GetRequiredService<IMatchGateway>(), sp.GetRequiredService<UserActivityRegistry>(),
            sp.GetRequiredService<IUserNotifier>(), sp.GetRequiredService<IPlayerCardService>(), sp.GetRequiredService<IBotPool>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<TttOptions>(), sp.GetRequiredService<ILogger<Matchmaker>>(), sp.GetService<TicTacToeUltimate.Server.Bots.IBotDispatcher>()));
        services.AddHostedService(sp => sp.GetRequiredService<Matchmaker>());

        services.AddSingleton(sp => new ChallengeService(sp.GetRequiredService<IChallengeRepository>(), sp.GetRequiredService<IMatchGateway>(),
            sp.GetRequiredService<UserActivityRegistry>(), sp.GetRequiredService<IUserNotifier>(), sp.GetRequiredService<IPlayerCardService>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<TttOptions>(), sp.GetRequiredService<ILogger<ChallengeService>>(),
            sp.GetService<IUserRepository>(), sp.GetService<IFriendRepository>(), sp.GetService<IDailyMatchRepository>()));
        services.AddSingleton<IChallengeService>(sp => sp.GetRequiredService<ChallengeService>());
        services.AddHostedService(sp => new ChallengeSweeper(
            () => sp.GetService<IChallengeRepository>() is null ? null : sp.GetRequiredService<ChallengeService>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ChallengeSweeper>>()));

        services.AddHostedService(sp => new DailyDeadlineSweeper(sp.GetRequiredService<MatchGateway>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<TttOptions>(), sp.GetRequiredService<ILogger<DailyDeadlineSweeper>>()));
        services.AddHostedService(sp => new MatchShutdownService(sp.GetRequiredService<MatchGateway>()));
        return services;
    }

    /// <summary>Maps WP2 REST endpoints.</summary>
    public static WebApplication MapTttMatches(this WebApplication app)
    {
        app.MapMatchEndpoints();
        return app;
    }
}
