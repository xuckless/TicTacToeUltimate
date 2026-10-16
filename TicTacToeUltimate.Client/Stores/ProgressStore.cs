using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Stores;

/// <summary>Streak and quests; rewarded claims.</summary>
public sealed class ProgressStore : StoreBase<ProgressState>, IProgressStore
{
    private readonly IAdBlockDetector? _adBlock;
    private readonly TttApiClient _api;
    private readonly IAdCoordinator _ads;

    /// <summary>Creates the store.</summary>
    public ProgressStore(TttApiClient api, IAdCoordinator ads, IRealtimeTransport rt, IAdBlockDetector? adBlock = null) : base(ProgressState.Empty)
    {
        _adBlock = adBlock;
        _api = api; _ads = ads;
        rt.PostGameSummaryReceived += s =>
        {
            if (s.Progress is { } p) Update(_ => new ProgressState(p, true));
        };
    }

    /// <inheritdoc />
    public async Task LoadAsync()
    {
        try
        {
            var p = await _api.GetProgressAsync();
            Update(_ => new ProgressState(p, true));
        }
        catch (Exception)
        {
            Update(s => s with { Loaded = true });
        }
    }

    /// <inheritdoc />
    public async Task<RewardClaimResultDto> ClaimRewardAsync(RewardKind kind, Guid? gameId = null)
    {
        // 1) Ask the server for a single-use nonce. It also checks eligibility, so a capped reward is refused BEFORE the ad plays.
        RewardNonceDto nonce;
        try { nonce = await _api.RequestRewardNonceAsync(new RewardNonceRequestDto(kind, gameId)); }
        catch (Exception ex) { return new RewardClaimResultDto(false, ErrorText.Code(ex), State.Progress); }
        if (nonce.Nonce is null) return new RewardClaimResultDto(false, nonce.Error ?? ErrorCodes.RewardInvalid, State.Progress);

        // 2) Show the ad.
        RewardedAdResult ad = await _ads.ShowRewardedAsync(kind);
        if (!ad.Earned)
        {
            var blocked = _adBlock is not null && await _adBlock.IsBlockedAsync();
            return new RewardClaimResultDto(false, blocked ? ErrorCodes.AdBlocked : ErrorCodes.RewardRequired, State.Progress);
        }

        // 3) Redeem the nonce (the provider receipt is informational until AdMob SSV lands).
        try
        {
            var result = await _api.ClaimRewardAsync(new RewardClaimDto(kind, gameId, nonce.Nonce));
            if (result.Progress is { } p) Update(_ => new ProgressState(p, true));
            return result;
        }
        catch (Exception ex) { return new RewardClaimResultDto(false, ErrorText.Code(ex), State.Progress); }
    }
}
