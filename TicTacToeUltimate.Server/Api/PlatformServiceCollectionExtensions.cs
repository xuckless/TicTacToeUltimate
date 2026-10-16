using Microsoft.Extensions.Options;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Auth;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Persistence.InMemory;
using TicTacToeUltimate.Server.Persistence.Postgres;
using TicTacToeUltimate.Server.Progression;
using TicTacToeUltimate.Server.Ratings;
using TicTacToeUltimate.Server.Rewards;
using TicTacToeUltimate.Server.Social;

namespace TicTacToeUltimate.Server.Platform;

/// <summary>WP3 wiring: auth, persistence, ratings, progression, social, rewards, REST.</summary>
public static class PlatformServiceCollectionExtensions
{
    /// <summary>Registers WP3 services. Throws when Dev auth is enabled in Production.</summary>
    public static IServiceCollection AddTttPlatform(this IServiceCollection services, IConfiguration cfg, IHostEnvironment env)
    {
        services.AddTttAuth(cfg, env);
        services.AddTttPersistence(cfg);

        services.AddSingleton(sp => new TierCalculator(sp.GetRequiredService<IOptions<TttOptions>>()));
        services.AddSingleton<RatingService>();
        services.AddSingleton<SeasonService>();
        services.AddHostedService(sp => sp.GetRequiredService<SeasonService>());

        services.AddSingleton<UserLocks>();
        services.AddSingleton<ProgressService>();
        services.AddSingleton<PostGameProcessor>();
        services.AddSingleton<IMatchResultSink>(sp => sp.GetRequiredService<PostGameProcessor>());
        services.AddHostedService(sp => sp.GetRequiredService<PostGameProcessor>());

        services.AddSingleton(_ => new ProfanityFilter());
        services.AddSingleton<UsernameRules>();
        services.AddOptions<TicTacToeUltimate.Server.Push.PushOptions>().Bind(cfg.GetSection("Ttt:Push"));
        services.AddHttpClient(nameof(TicTacToeUltimate.Server.Push.ApnsPushSender));
        services.AddSingleton<IPushSender, TicTacToeUltimate.Server.Push.ApnsPushSender>();
        services.AddSingleton<TicTacToeUltimate.Server.Push.RetentionNotifier>();
        services.AddHostedService(sp => sp.GetRequiredService<TicTacToeUltimate.Server.Push.RetentionNotifier>());
        services.AddHttpClient(nameof(SupabaseAdminClient));
        services.AddSingleton<SupabaseAdminClient>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<IPlayerCardService, PlayerCardService>();
        services.AddSingleton<PresenceLookup>();
        services.AddSingleton<IPresenceSink, PresenceSink>();
        services.AddSingleton(sp => new ChatRateLimiter(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<SocialService>();
        services.AddSingleton<GameViews>();

        services.AddSingleton<TicTacToeUltimate.Server.Economy.WalletService>();
        services.AddSingleton<RewardNonceService>();   // server-minted single-use nonces; Ttt:Ads:Provider only steers the client
        services.AddSingleton<RewardService>();
        return services;
    }

    /// <summary>InMemory (default) or Postgres repositories (Ttt:Persistence:Provider).</summary>
    public static IServiceCollection AddTttPersistence(this IServiceCollection services, IConfiguration cfg)
    {
        var provider = cfg["Ttt:Persistence:Provider"] ?? "InMemory";
        if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<PgDb>();
            services.AddSingleton<IUserRepository, PgUserRepository>();
            services.AddSingleton<IRatingRepository, PgRatingRepository>();
            services.AddSingleton<IGameRepository, PgGameRepository>();
            services.AddSingleton<IDailyMatchRepository, PgDailyMatchRepository>();
            services.AddSingleton<IFriendRepository, PgFriendRepository>();
            services.AddSingleton<IChatRepository, PgChatRepository>();
            services.AddSingleton<IProgressRepository, PgProgressRepository>();
            services.AddSingleton<IChallengeRepository, PgChallengeRepository>();
            services.AddSingleton<IBotKeyRepository, PgBotKeyRepository>();
            services.AddSingleton<IRewardLedger, PgRewardLedger>();
            services.AddSingleton<IWalletRepository, TicTacToeUltimate.Server.Persistence.PgWalletRepository>();
            services.AddSingleton<TicTacToeUltimate.Server.Push.IPushTokenRepository, TicTacToeUltimate.Server.Push.PgPushTokenRepository>();
            services.AddSingleton<ISeasonRepository, PgSeasonRepository>();
            services.AddSingleton<TicTacToeUltimate.Server.Persistence.IGuestMergeStore, TicTacToeUltimate.Server.Persistence.PgGuestMergeStore>();
            services.AddSingleton<TicTacToeUltimate.Server.Persistence.IAccountDeletionStore, TicTacToeUltimate.Server.Persistence.PgAccountDeletionStore>();
        }
        else
        {
            services.AddSingleton<IUserRepository, InMemoryUserRepository>();
            services.AddSingleton<IRatingRepository, InMemoryRatingRepository>();
            services.AddSingleton<IGameRepository, InMemoryGameRepository>();
            services.AddSingleton<IDailyMatchRepository, InMemoryDailyMatchRepository>();
            services.AddSingleton<IFriendRepository, InMemoryFriendRepository>();
            services.AddSingleton<IChatRepository, InMemoryChatRepository>();
            services.AddSingleton<IProgressRepository, InMemoryProgressRepository>();
            services.AddSingleton<IChallengeRepository, InMemoryChallengeRepository>();
            services.AddSingleton<IBotKeyRepository, InMemoryBotKeyRepository>();
            services.AddSingleton<IRewardLedger, InMemoryRewardLedger>();
            services.AddSingleton<IWalletRepository, TicTacToeUltimate.Server.Persistence.InMemoryWalletRepository>();
            services.AddSingleton<TicTacToeUltimate.Server.Push.IPushTokenRepository, TicTacToeUltimate.Server.Push.InMemoryPushTokenRepository>();
            services.AddSingleton<ISeasonRepository, InMemorySeasonRepository>();
            services.AddSingleton<TicTacToeUltimate.Server.Persistence.IGuestMergeStore, TicTacToeUltimate.Server.Persistence.InMemoryGuestMergeStore>();
            services.AddSingleton<TicTacToeUltimate.Server.Persistence.IAccountDeletionStore, TicTacToeUltimate.Server.Persistence.InMemoryAccountDeletionStore>();
            services.AddHostedService<FixtureSeeder>();
        }
        return services;
    }

    /// <summary>Maps WP3 REST endpoints.</summary>
    public static WebApplication MapTttPlatform(this WebApplication app) => app.MapPlatformEndpoints();
}
