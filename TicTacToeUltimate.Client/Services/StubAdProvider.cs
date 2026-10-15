using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Services;

/// <summary>Stub ads rendered through <see cref="IAdOverlay"/>; rewarded receipts are <c>stub:{kind}:{guid}</c>.</summary>
public sealed class StubAdProvider(IAdOverlay overlay) : IAdProvider
{
    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public Task<bool> ShowInterstitialAsync(CancellationToken ct) => overlay.ShowAsync(new AdOverlayRequest("interstitial", null, 5), ct);

    /// <inheritdoc />
    public async Task<RewardedAdResult> ShowRewardedAsync(RewardKind kind, CancellationToken ct)
    {
        var completed = await overlay.ShowAsync(new AdOverlayRequest("rewarded", kind, 15), ct);
        return completed
            ? new RewardedAdResult(true, $"stub:{kind.ToString().ToLowerInvariant()}:{Guid.NewGuid()}")
            : new RewardedAdResult(false, null);
    }
}
