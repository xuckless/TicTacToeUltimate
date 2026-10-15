using Microsoft.Extensions.Logging;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Services;

/// <summary>Applies <see cref="AdPolicy"/> and talks to the <see cref="IAdProvider"/>.</summary>
public sealed class AdCoordinator(IAdProvider provider, ISessionStore session, IQueueStore queue, ILogger<AdCoordinator> logger) : IAdCoordinator
{
    private static readonly AdConfigDto DefaultConfig = new(3, true);
    private readonly Lock _gate = new();
    private AdPolicyState _state;
    private volatile bool _matchRoute;

    /// <summary>Current policy counters.</summary>
    public AdPolicyState PolicyState { get { lock (_gate) return _state; } }

    /// <inheritdoc />
    public bool BannersAllowed => !_matchRoute && queue.State.Phase == QueuePhase.Idle;

    /// <inheritdoc />
    public void SetMatchRouteActive(bool active) => _matchRoute = active;

    /// <inheritdoc />
    public async Task OnGameCompletedAsync(GameResultForUser result)
    {
        bool show;
        lock (_gate)
        {
            var blocked = _matchRoute || queue.State.Phase != QueuePhase.Idle || !provider.IsAvailable;
            (show, _state) = AdPolicy.OnGameCompleted(_state, result, session.State.Config?.Ads ?? DefaultConfig, blocked);
        }
        if (!show) return;
        try { await provider.ShowInterstitialAsync(CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Interstitial failed"); }
    }

    /// <inheritdoc />
    public async Task<RewardedAdResult> ShowRewardedAsync(RewardKind kind)
    {
        if (!provider.IsAvailable) return new RewardedAdResult(false, null);
        try { return await provider.ShowRewardedAsync(kind, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Rewarded ad failed"); return new RewardedAdResult(false, null); }
    }
}
