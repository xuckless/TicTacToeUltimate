using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Abstractions;

public interface IPlatformInfo { ClientPlatform Platform { get; } bool IsTouch { get; } bool SupportsHaptics { get; } string AppVersion { get; } }
public interface IKeyValueStore            // non-secret prefs; web: localStorage, MAUI: Preferences
{
    ValueTask<string?> GetAsync(string key);
    ValueTask SetAsync(string key, string value);
    ValueTask RemoveAsync(string key);
}
public interface ISecureStore : IKeyValueStore { }   // tokens; web: sessionStorage with localStorage fallback (§8.6), MAUI: SecureStorage
public enum HapticCue { Tap, Place, OpponentPlace, Vanish, Tick, Warning, Win, Loss, Promotion, Error }
public interface IHaptics { bool IsSupported { get; } void Play(HapticCue cue); }
public enum SoundCue { Tap, PlaceX, PlaceO, Vanish, Tick, Warning, SuddenDeath, Win, Loss, Promotion, QuestDone, Emote, Error, MatchFound }
public interface ISoundService
{
    ValueTask InitAsync();
    ValueTask UnlockAsync();                      // call on first user gesture
    void Play(SoundCue cue);
    void SetHeartbeat(int? beatsPerMinute);       // null = off; scheduled inside JS on the audio clock
    void SetMusic(bool on);
    void SetVolumes(double sfx, double music);
}
public sealed record RewardedAdResult(bool Earned, string? Receipt);
public interface IAdProvider
{
    bool IsAvailable { get; }
    Task<bool> ShowInterstitialAsync(CancellationToken ct);
    Task<RewardedAdResult> ShowRewardedAsync(RewardKind kind, CancellationToken ct);
}
public sealed record AdOverlayRequest(string Kind /* "interstitial" | "rewarded" */, RewardKind? Reward, int Seconds);
/// <summary>Implemented by Shared (WP6): renders the stub ad modal; returns true when the countdown completed (false if closed early).</summary>
public interface IAdOverlay { Task<bool> ShowAsync(AdOverlayRequest request, CancellationToken ct); }
public interface IShareService { ValueTask<string> ShareLinkAsync(string title, string text, Uri url); } // "shared" | "copied" | "failed"
public interface IAppLifecycle { bool IsForeground { get; } bool IsOnline { get; } event Action? Resumed; event Action? Suspended; event Action<bool>? NetworkChanged; }
public interface IDeepLinkSource { Uri? InitialLink { get; } event Action<Uri>? LinkOpened; }
public interface IPushService { ValueTask<bool> RequestPermissionAsync(); ValueTask<string?> GetTokenAsync(); }
public sealed record ClientOptions
{
    public required Uri BackendBaseUrl { get; init; }
    public required Uri WebBaseUrl { get; init; }
    public required ClientPlatform Platform { get; init; }
    public string ClientVersion { get; init; } = "1.0";
    public bool IsDevelopment { get; init; }
}
