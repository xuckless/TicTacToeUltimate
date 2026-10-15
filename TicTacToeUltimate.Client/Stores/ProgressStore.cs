using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Stores;

/// <summary>Streak and quests; rewarded claims.</summary>
public sealed class ProgressStore : StoreBase<ProgressState>, IProgressStore
{
    private readonly TttApiClient _api;
    private readonly IAdCoordinator _ads;

    /// <summary>Creates the store.</summary>
    public ProgressStore(TttApiClient api, IAdCoordinator ads, IRealtimeTransport rt) : base(ProgressState.Empty)
    {
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
        RewardedAdResult ad = await _ads.ShowRewardedAsync(kind);
        if (!ad.Earned || ad.Receipt is null) return new RewardClaimResultDto(false, ErrorCodes.RewardRequired, State.Progress);
        try
        {
            var result = await _api.ClaimRewardAsync(new RewardClaimDto(kind, gameId, ad.Receipt));
            if (result.Progress is { } p) Update(_ => new ProgressState(p, true));
            return result;
        }
        catch (Exception ex) { return new RewardClaimResultDto(false, ErrorText.Code(ex), State.Progress); }
    }
}
