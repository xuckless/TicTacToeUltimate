using Microsoft.Extensions.Logging;
using Plugin.AdMob;
using Plugin.AdMob.Services;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Services;

/// <summary>
/// AdMob on iOS and Android (Plugin.AdMob). Interstitial and rewarded ads are preloaded so they show instantly when the
/// pacing policy (AdPolicy) asks for one. Every wait has a timeout, and a failed or slow ad just means "no ad".
/// Ad unit ids come from Resources/Raw/appsettings.json (Ads:AdMob); without them Google's test units are used.
/// </summary>
public sealed class AdMobAdProvider(IInterstitialAdService interstitials, IRewardedAdService rewardeds, ILogger<AdMobAdProvider> log) : IAdProvider
{
    private IInterstitialAd? _interstitial;
    private IRewardedAd? _rewarded;
    private bool _trackingAsked;

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <summary>Starts loading both formats (call once the UI is up).</summary>
    public void Preload() => MainThread.BeginInvokeOnMainThread(() =>
    {
        try
        {
            _interstitial ??= NewInterstitial();
            _rewarded ??= NewRewarded();
        }
        catch (Exception ex) { log.LogWarning(ex, "AdMob preload failed"); }
    });

    /// <inheritdoc />
    public async Task<bool> ShowInterstitialAsync(CancellationToken ct)
    {
        await AskTrackingOnceAsync();
        var ad = await MainThread.InvokeOnMainThreadAsync(() => _interstitial ??= NewInterstitial());
        if (!ad.IsLoaded) { Preload(); return false; }   // not ready: skip this break, don't make the player wait
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler dismissed = (_, _) => done.TrySetResult(true);
        EventHandler<IAdError> failed = (_, e) => { log.LogInformation("Interstitial failed to show: {Error}", e.Message); done.TrySetResult(false); };
        ad.OnAdDismissed += dismissed;
        ad.OnAdFailedToShow += failed;
        try
        {
            await MainThread.InvokeOnMainThreadAsync(ad.Show);
            return await done.Task.WaitAsync(TimeSpan.FromMinutes(2), ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { return false; }
        finally
        {
            ad.OnAdDismissed -= dismissed;
            ad.OnAdFailedToShow -= failed;
            _interstitial = null;
            Preload();
        }
    }

    /// <inheritdoc />
    public async Task<RewardedAdResult> ShowRewardedAsync(RewardKind kind, CancellationToken ct)
    {
        await AskTrackingOnceAsync();
        var ad = await MainThread.InvokeOnMainThreadAsync(() => _rewarded ??= NewRewarded());
        if (!ad.IsLoaded && !await WaitLoadedAsync(ad, TimeSpan.FromSeconds(8), ct))
        {
            _rewarded = null;
            Preload();
            // No fill (common before an app is approved, or offline-ish): don't block the perk on Google's inventory.
            return new RewardedAdResult(true, $"admob:{Kind(kind)}:{Guid.NewGuid()}");
        }
        var earned = false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<RewardItem> reward = (_, _) => earned = true;
        EventHandler dismissed = (_, _) => done.TrySetResult();
        EventHandler<IAdError> failed = (_, _) => done.TrySetResult();
        ad.OnUserEarnedReward += reward;
        ad.OnAdDismissed += dismissed;
        ad.OnAdFailedToShow += failed;
        try
        {
            await MainThread.InvokeOnMainThreadAsync(ad.Show);
            await done.Task.WaitAsync(TimeSpan.FromMinutes(3), ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        finally
        {
            ad.OnUserEarnedReward -= reward;
            ad.OnAdDismissed -= dismissed;
            ad.OnAdFailedToShow -= failed;
            _rewarded = null;
            Preload();
        }
        // Receipt is a one-time nonce; AdMob server-side verification can replace it later.
        return earned ? new RewardedAdResult(true, $"admob:{Kind(kind)}:{Guid.NewGuid()}") : new RewardedAdResult(false, null);
    }

    private static string Kind(RewardKind kind) => kind.ToString().ToLowerInvariant();

    private IInterstitialAd NewInterstitial()
    {
        var ad = interstitials.CreateAd();
        ad.OnAdFailedToLoad += (_, e) => log.LogInformation("Interstitial failed to load: {Error}", e.Message);
        ad.Load();
        return ad;
    }

    private IRewardedAd NewRewarded()
    {
        var ad = rewardeds.CreateAd();
        ad.OnAdFailedToLoad += (_, e) => log.LogInformation("Rewarded failed to load: {Error}", e.Message);
        ad.Load();
        return ad;
    }

    private static async Task<bool> WaitLoadedAsync(IRewardedAd ad, TimeSpan timeout, CancellationToken ct)
    {
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler ok = (_, _) => loaded.TrySetResult(true);
        EventHandler<IAdError> fail = (_, _) => loaded.TrySetResult(false);
        ad.OnAdLoaded += ok;
        ad.OnAdFailedToLoad += fail;
        try
        {
            if (ad.IsLoaded) return true;
            return await loaded.Task.WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { return false; }
        finally { ad.OnAdLoaded -= ok; ad.OnAdFailedToLoad -= fail; }
    }

    // iOS: App Tracking Transparency prompt, once, right before the first ad (Apple requires asking before using the IDFA).
    private async Task AskTrackingOnceAsync()
    {
        if (_trackingAsked) return;
        _trackingAsked = true;
#if IOS
        try
        {
            if (AppTrackingTransparency.ATTrackingManager.TrackingAuthorizationStatus == AppTrackingTransparency.ATTrackingManagerAuthorizationStatus.NotDetermined)
                await MainThread.InvokeOnMainThreadAsync(() => AppTrackingTransparency.ATTrackingManager.RequestTrackingAuthorizationAsync());
        }
        catch (Exception ex) { log.LogInformation(ex, "ATT request failed"); }
#else
        await Task.CompletedTask;
#endif
    }
}

/// <summary>Mac Catalyst has no AdMob: no interstitials, and rewards are granted without an ad.</summary>
public sealed class NoAdsProvider : IAdProvider
{
    /// <inheritdoc />
    public bool IsAvailable => true;
    /// <inheritdoc />
    public Task<bool> ShowInterstitialAsync(CancellationToken ct) => Task.FromResult(false);
    /// <inheritdoc />
    public Task<RewardedAdResult> ShowRewardedAsync(RewardKind kind, CancellationToken ct)
        => Task.FromResult(new RewardedAdResult(true, $"stub:{kind.ToString().ToLowerInvariant()}:{Guid.NewGuid()}"));
}
