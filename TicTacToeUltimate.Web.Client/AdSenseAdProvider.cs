using Microsoft.JSInterop;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Web.Client;

/// <summary>Web ads via AdSense (wwwroot/js/ads.js). Used when <c>Ads:AdSenseClient</c> is configured; otherwise the stub.</summary>
public sealed class AdSenseAdProvider(IJSRuntime js, AdSenseOptions options) : IAdProvider, IBannerAds, IAdBlockDetector
{
    private bool _init;

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public async Task<bool> ShowInterstitialAsync(CancellationToken ct)
    {
        try { return await EnsureAsync() && await js.InvokeAsync<bool>("tttAds.interstitial", ct); }
        catch (JSException) { return false; }
    }

    /// <inheritdoc />
    public async Task<RewardedAdResult> ShowRewardedAsync(RewardKind kind, CancellationToken ct)
    {
        try
        {
            if (!await EnsureAsync()) return new(false, null);
            var r = await js.InvokeAsync<string>("tttAds.rewarded", ct, kind.ToString());
            // The Ad Placement API has no server-side verification: the receipt is a one-time nonce (the server ledger dedups it).
            // No inventory (H5 Games Ads not approved yet, or no fill) and no blocker: grant anyway so rewards keep working.
            var earned = r == "viewed" || (r == "unavailable" && !await IsBlockedAsync());
            return earned ? new(true, $"web:{kind.ToString().ToLowerInvariant()}:{Guid.NewGuid()}") : new(false, null);
        }
        catch (JSException) { return new(false, null); }
    }

    /// <inheritdoc />
    public bool Has(AdSlotKind kind) => SlotFor(kind) is not null;

    /// <inheritdoc />
    public async ValueTask RenderAsync(object host, AdSlotKind kind)
    {
        try { if (await EnsureAsync() && SlotFor(kind) is { } slot) await js.InvokeAsync<bool>("tttAds.banner", host, slot, kind.ToString().ToLowerInvariant()); }
        catch (JSException) { /* blocked by an ad blocker: leave the slot empty */ }
    }

    // The display unit serves the strip and the in-feed card (its shape comes from the container); the vertical unit serves the rail.
    private string? SlotFor(AdSlotKind kind) => kind == AdSlotKind.Rail ? options.RailSlot : options.BannerSlot;

    private bool? _blocked;

    /// <inheritdoc />
    public async ValueTask<bool> IsBlockedAsync()
    {
        if (_blocked is { } b) return b;
        try { _blocked = await EnsureAsync() && await js.InvokeAsync<bool>("tttAds.blocked"); }
        catch (JSException) { _blocked = true; }
        return _blocked.Value;
    }

    private async ValueTask<bool> EnsureAsync()
    {
        if (_init) return true;
        _init = await js.InvokeAsync<bool>("tttAds.init", options.Client, options.TestMode);
        return _init;
    }
}

/// <summary>Ads:AdSenseClient (ca-pub-…), Ads:AdSenseBannerSlot (display unit), Ads:AdSenseRailSlot (vertical unit), Ads:TestMode.</summary>
public sealed record AdSenseOptions(string Client, string? BannerSlot, string? RailSlot, bool TestMode);
