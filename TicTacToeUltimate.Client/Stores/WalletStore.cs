using System.Collections.Immutable;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Stores;

/// <summary>Diamonds wallet and store. Server-authoritative: every change comes back from the server.</summary>
public sealed class WalletStore : StoreBase<WalletState>, IWalletStore
{
    private readonly TttApiClient _api;
    private readonly IProgressStore _progress;
    private readonly IAdCoordinator _ads;
    private readonly ISessionStore _session;

    /// <summary>Creates the store.</summary>
    public WalletStore(TttApiClient api, IProgressStore progress, IAdCoordinator ads, IRealtimeTransport rt, ISessionStore session) : base(WalletState.Empty)
    {
        _api = api; _progress = progress; _ads = ads; _session = session;
        rt.PostGameSummaryReceived += s => { if (s.Wallet is { } w) SetWallet(w); };
        if (session is SessionStore concrete) concrete.HelloReceived += h => { _ = LoadAsync(); };
        session.Changed += () => { if (session.State.Status == SessionStatus.SignedOut && State.Loaded) Update(_ => WalletState.Empty); };
    }

    private void SetWallet(WalletDto w)
    {
        _ads.SetAdFreeUntil(w.AdFreeUntilMs);
        Update(s => s with { Wallet = w, Loaded = true });
    }

    /// <inheritdoc />
    public async Task LoadAsync()
    {
        if (_session.State.Status != SessionStatus.SignedIn) return;
        try
        {
            var store = await _api.GetStoreAsync();
            _ads.SetAdFreeUntil(store.Wallet.AdFreeUntilMs);
            Update(_ => new WalletState(store.Wallet, [.. store.Items], true));
        }
        catch (Exception) { Update(s => s with { Loaded = true }); }
    }

    /// <inheritdoc />
    public async Task<PurchaseResultDto> BuyAsync(string itemId, Guid? gameId = null)
    {
        PurchaseResultDto r;
        try { r = await _api.BuyAsync(new PurchaseDto(itemId, gameId)); }
        catch (Exception ex) { return new PurchaseResultDto(false, ErrorText.Code(ex), null, null); }
        if (r.Store is { } st) { _ads.SetAdFreeUntil(st.Wallet.AdFreeUntilMs); Update(_ => new WalletState(st.Wallet, [.. st.Items], true)); }
        if (r.Progress is { } p) _progress.Apply(p);
        return r;
    }

    /// <inheritdoc />
    public async Task<RewardClaimResultDto> WatchAdAsync()
    {
        var r = await _progress.ClaimRewardAsync(RewardKind.Diamonds);
        if (r.Granted) await LoadAsync();   // balance + today's view count
        return r;
    }
}
