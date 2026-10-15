using System.Collections.Concurrent;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Services;

/// <summary>In-memory key/value store (fallback and tests).</summary>
public class InMemoryKeyValueStore : IKeyValueStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();
    /// <inheritdoc />
    public ValueTask<string?> GetAsync(string key) => ValueTask.FromResult(_values.TryGetValue(key, out var v) ? v : null);
    /// <inheritdoc />
    public ValueTask SetAsync(string key, string value) { _values[key] = value; return ValueTask.CompletedTask; }
    /// <inheritdoc />
    public ValueTask RemoveAsync(string key) { _values.TryRemove(key, out _); return ValueTask.CompletedTask; }
}

/// <summary>In-memory secure store (fallback and tests).</summary>
public sealed class InMemorySecureStore : InMemoryKeyValueStore, ISecureStore;

/// <summary>No haptics.</summary>
public sealed class NoopHaptics : IHaptics
{
    /// <inheritdoc />
    public bool IsSupported => false;
    /// <inheritdoc />
    public void Play(HapticCue cue) { }
}

/// <summary>No sound.</summary>
public sealed class NoopSoundService : ISoundService
{
    /// <inheritdoc />
    public ValueTask InitAsync() => ValueTask.CompletedTask;
    /// <inheritdoc />
    public ValueTask UnlockAsync() => ValueTask.CompletedTask;
    /// <inheritdoc />
    public void Play(SoundCue cue) { }
    /// <inheritdoc />
    public void SetHeartbeat(int? beatsPerMinute) { }
    /// <inheritdoc />
    public void SetMusic(bool on) { }
    /// <inheritdoc />
    public void SetVolumes(double sfx, double music) { }
}

/// <summary>Platform info derived from <see cref="ClientOptions"/>.</summary>
public sealed class DefaultPlatformInfo(ClientOptions options) : IPlatformInfo
{
    /// <inheritdoc />
    public ClientPlatform Platform => options.Platform;
    /// <inheritdoc />
    public bool IsTouch => options.Platform is ClientPlatform.iOS or ClientPlatform.Android;
    /// <inheritdoc />
    public bool SupportsHaptics => false;
    /// <inheritdoc />
    public string AppVersion => options.ClientVersion;
}

/// <summary>Always foreground and online.</summary>
public sealed class StaticAppLifecycle : IAppLifecycle
{
    /// <inheritdoc />
    public bool IsForeground => true;
    /// <inheritdoc />
    public bool IsOnline => true;
#pragma warning disable CS0067
    /// <inheritdoc />
    public event Action? Resumed;
    /// <inheritdoc />
    public event Action? Suspended;
    /// <inheritdoc />
    public event Action<bool>? NetworkChanged;
#pragma warning restore CS0067
}

/// <summary>No deep links.</summary>
public sealed class NullDeepLinkSource : IDeepLinkSource
{
    /// <inheritdoc />
    public Uri? InitialLink => null;
#pragma warning disable CS0067
    /// <inheritdoc />
    public event Action<Uri>? LinkOpened;
#pragma warning restore CS0067
}

/// <summary>Push stub: permission denied, no token.</summary>
public sealed class StubPushService : IPushService
{
    /// <inheritdoc />
    public ValueTask<bool> RequestPermissionAsync() => ValueTask.FromResult(false);
    /// <inheritdoc />
    public ValueTask<string?> GetTokenAsync() => ValueTask.FromResult<string?>(null);
}

/// <summary>Share stub.</summary>
public sealed class NullShareService : IShareService
{
    /// <inheritdoc />
    public ValueTask<string> ShareLinkAsync(string title, string text, Uri url) => ValueTask.FromResult("failed");
}

/// <summary>Ad overlay stub that completes immediately (used only when no UI overlay is registered).</summary>
public sealed class ImmediateAdOverlay : IAdOverlay
{
    /// <inheritdoc />
    public Task<bool> ShowAsync(AdOverlayRequest request, CancellationToken ct) => Task.FromResult(true);
}
