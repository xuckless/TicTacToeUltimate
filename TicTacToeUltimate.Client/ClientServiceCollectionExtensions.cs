using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Client.Stores;

namespace TicTacToeUltimate.Client;

/// <summary>Registers the client core (stores, realtime, REST, clock sync, ads).</summary>
public static class ClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers all client services as singletons. Platform services (storage, sound, haptics, lifecycle, ad overlay)
    /// come from <c>AddTttSharedUi</c> and host overrides; call <see cref="AddTttClientFallbacks"/> last to fill any gaps.
    /// </summary>
    public static IServiceCollection AddTttClient(this IServiceCollection services, ClientOptions options)
    {
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<TokenStore>();
        services.AddSingleton<ClockSync>();
        services.AddSingleton(_ => new SupabaseAuthClient(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }));
        services.AddSingleton<TicTacToeUltimate.Client.Diagnostics.DiagnosticsTrail>();
        services.AddSingleton<TicTacToeUltimate.Client.Diagnostics.RingLoggerProvider>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<TicTacToeUltimate.Client.Diagnostics.RingLoggerProvider>());
        services.AddSingleton<TicTacToeUltimate.Client.Diagnostics.CrashReporter>();
        services.AddSingleton<TicTacToeUltimate.Client.Diagnostics.Telemetry>();
        services.AddSingleton(sp => new TttApiClient(new HttpClient { BaseAddress = options.BackendBaseUrl }, sp.GetRequiredService<TokenStore>(),
            sp.GetRequiredService<TicTacToeUltimate.Client.Diagnostics.DiagnosticsTrail>()));
        services.AddSingleton<RealtimeClient>(sp => new RealtimeClient(options, sp.GetRequiredService<TokenStore>(),
            sp.GetRequiredService<ClockSync>(), sp.GetRequiredService<ILogger<RealtimeClient>>(), sp.GetService<IAppLifecycle>()));
        services.AddSingleton<IRealtimeTransport>(sp => sp.GetRequiredService<RealtimeClient>());

        services.AddSingleton<SessionStore>();
        services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<SessionStore>());
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<SettingsStore>());
        services.AddSingleton<QueueStore>();
        services.AddSingleton<IQueueStore>(sp => sp.GetRequiredService<QueueStore>());
        services.AddSingleton<MatchStore>();
        services.AddSingleton<IMatchStore>(sp => sp.GetRequiredService<MatchStore>());
        services.AddSingleton<SocialStore>();
        services.AddSingleton<ISocialStore>(sp => sp.GetRequiredService<SocialStore>());
        services.AddSingleton<WalletStore>();
        services.AddSingleton<IWalletStore>(sp => sp.GetRequiredService<WalletStore>());
        services.AddSingleton<ActiveMatchesStore>();
        services.AddSingleton<IActiveMatchesStore>(sp => sp.GetRequiredService<ActiveMatchesStore>());
        services.AddSingleton<ProgressStore>();
        services.AddSingleton<IProgressStore>(sp => sp.GetRequiredService<ProgressStore>());
        services.AddSingleton<ModerationStore>();
        services.AddSingleton<IModerationStore>(sp => sp.GetRequiredService<ModerationStore>());
        services.AddSingleton<IGameQueries, GameQueries>();
        services.AddSingleton<AdCoordinator>();
        services.AddSingleton<IAdCoordinator>(sp => sp.GetRequiredService<AdCoordinator>());
        services.TryAddSingleton<IAdProvider, StubAdProvider>();
        services.AddSingleton<Local.LocalPlaySession>();
        return services;
    }

    /// <summary>TryAdd no-op/in-memory platform services for anything the UI layer and host did not register. Call last.</summary>
    public static IServiceCollection AddTttClientFallbacks(this IServiceCollection services)
    {
        services.TryAddSingleton<IKeyValueStore, InMemoryKeyValueStore>();
        services.TryAddSingleton<ISecureStore, InMemorySecureStore>();
        services.TryAddSingleton<IHaptics, NoopHaptics>();
        services.TryAddSingleton<ISoundService, NoopSoundService>();
        services.TryAddSingleton<IPlatformInfo, DefaultPlatformInfo>();
        services.TryAddSingleton<IAppLifecycle, StaticAppLifecycle>();
        services.TryAddSingleton<IDeepLinkSource, NullDeepLinkSource>();
        services.TryAddSingleton<IPushService, StubPushService>();
        services.TryAddSingleton<IShareService, NullShareService>();
        services.TryAddSingleton<IAdOverlay, ImmediateAdOverlay>();
        return services;
    }
}
