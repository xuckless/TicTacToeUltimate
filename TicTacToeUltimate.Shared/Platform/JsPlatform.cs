using System.Collections.Concurrent;
using Microsoft.JSInterop;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Shared.Platform;

/// <summary>localStorage-backed <see cref="IKeyValueStore"/> with an in-memory fallback when JS is unavailable.</summary>
public class JsKeyValueStore(TttJsModule js) : IKeyValueStore
{
    private readonly ConcurrentDictionary<string, string> _fallback = new();
    /// <summary>The JS module.</summary>
    protected TttJsModule Js { get; } = js;

    /// <inheritdoc />
    public virtual async ValueTask<string?> GetAsync(string key)
        => await Js.InvokeAsync<string?>("storageGet", "local", key) ?? (_fallback.TryGetValue(key, out var v) ? v : null);

    /// <inheritdoc />
    public virtual async ValueTask SetAsync(string key, string value)
    {
        _fallback[key] = value;
        await Js.InvokeVoidAsync("storageSet", "local", key, value);
    }

    /// <inheritdoc />
    public virtual async ValueTask RemoveAsync(string key)
    {
        _fallback.TryRemove(key, out _);
        await Js.InvokeVoidAsync("storageRemove", "local", key);
    }
}

/// <summary>Web token store: reads sessionStorage first (per tab), then localStorage; writes both (§8.6).</summary>
public sealed class JsSecureStore(TttJsModule js) : JsKeyValueStore(js), ISecureStore
{
    /// <inheritdoc />
    public override async ValueTask<string?> GetAsync(string key)
        => await Js.InvokeAsync<string?>("storageGet", "session", key) ?? await base.GetAsync(key);

    /// <inheritdoc />
    public override async ValueTask SetAsync(string key, string value)
    {
        await Js.InvokeVoidAsync("storageSet", "session", key, value);
        await base.SetAsync(key, value);
    }

    /// <inheritdoc />
    public override async ValueTask RemoveAsync(string key)
    {
        await Js.InvokeVoidAsync("storageRemove", "session", key);
        await base.RemoveAsync(key);
    }
}

/// <summary>navigator.vibrate haptics (Android browsers; silently ignored elsewhere).</summary>
public sealed class JsHaptics(TttJsModule js) : IHaptics
{
    /// <inheritdoc />
    public bool IsSupported => true;

    /// <summary>Vibration pattern for a cue (ms on/off).</summary>
    public static int[] Pattern(HapticCue cue) => cue switch
    {
        HapticCue.Tap => [8],
        HapticCue.Place => [15],
        HapticCue.OpponentPlace => [10],
        HapticCue.Vanish => [6, 30, 6],
        HapticCue.Tick => [5],
        HapticCue.Warning => [30, 40, 30],
        HapticCue.Win => [40, 60, 80],
        HapticCue.Loss => [120],
        HapticCue.Promotion => [30, 40, 30, 40, 90],
        HapticCue.Error => [60, 30, 60],
        HapticCue.Shatter => [25, 20, 40],
        HapticCue.Crit => [40, 30, 40, 30, 120],
        _ => [10],
    };

    /// <inheritdoc />
    public void Play(HapticCue cue) => js.Fire("vibrate", (object)Pattern(cue));
}

/// <summary>WebAudio-synthesized SFX, heartbeat and music (all scheduled in JS on the audio clock).</summary>
public sealed class JsSoundService(TttJsModule js) : ISoundService
{
    /// <inheritdoc />
    public ValueTask InitAsync() => js.InvokeVoidAsync("audioInit");
    /// <inheritdoc />
    public ValueTask UnlockAsync() => js.InvokeVoidAsync("audioUnlock");
    /// <inheritdoc />
    public void Play(SoundCue cue) { js.Fire("audioPlay", cue.ToString()); Played?.Invoke(cue); }

    /// <summary>Every cue played (sound captions listen here, whether or not sound is on).</summary>
    public static event Action<SoundCue>? Played;
    /// <inheritdoc />
    public void SetHeartbeat(int? beatsPerMinute) => js.Fire("audioHeartbeat", beatsPerMinute);
    /// <inheritdoc />
    public void SetMusic(bool on) => js.Fire("audioMusic", on);
    /// <inheritdoc />
    public void SetVolumes(double sfx, double music) => js.Fire("audioVolumes", Math.Clamp(sfx, 0, 1), Math.Clamp(music, 0, 1));
}

/// <summary>Web Share API with clipboard fallback.</summary>
public sealed class JsShareService(TttJsModule js) : IShareService
{
    /// <inheritdoc />
    public async ValueTask<string> ShareLinkAsync(string title, string text, Uri url)
        => await js.InvokeAsync<string>("share", title, text, url.ToString()) ?? "failed";
}

/// <summary>document.visibilitychange + online/offline lifecycle.</summary>
public sealed class JsLifecycle : IAppLifecycle, IDisposable
{
    private readonly TttJsModule _js;
    private DotNetObjectReference<JsLifecycle>? _ref;
    private int _subscribed;

    /// <summary>Creates the lifecycle source; subscribes once JS is attached.</summary>
    public JsLifecycle(TttJsModule js)
    {
        _js = js;
        if (js.IsAttached) _ = SubscribeAsync();
        else js.Attached += () => _ = SubscribeAsync();
    }

    /// <inheritdoc />
    public bool IsForeground { get; private set; } = true;
    /// <inheritdoc />
    public bool IsOnline { get; private set; } = true;
    /// <inheritdoc />
    public event Action? Resumed;
    /// <inheritdoc />
    public event Action? Suspended;
    /// <inheritdoc />
    public event Action<bool>? NetworkChanged;

    private async Task SubscribeAsync()
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 1) return;
        _ref = DotNetObjectReference.Create(this);
        var initial = await _js.InvokeAsync<LifecycleInitial>("lifecycleSubscribe", _ref);
        if (initial is not null) { IsForeground = initial.Visible; IsOnline = initial.Online; }
    }

    /// <summary>JS callback.</summary>
    [JSInvokable]
    public void OnVisibility(bool visible)
    {
        if (visible == IsForeground) return;
        IsForeground = visible;
        if (visible) Resumed?.Invoke(); else Suspended?.Invoke();
    }

    /// <summary>JS callback.</summary>
    [JSInvokable]
    public void OnOnline(bool online)
    {
        if (online == IsOnline) return;
        IsOnline = online;
        NetworkChanged?.Invoke(online);
    }

    /// <inheritdoc />
    public void Dispose() => _ref?.Dispose();

    private sealed record LifecycleInitial(bool Visible, bool Online);
}

/// <summary>No deep links on the web (routes are URLs).</summary>
public sealed class NoopDeepLinkSource : IDeepLinkSource
{
    /// <inheritdoc />
    public Uri? InitialLink => null;
    /// <inheritdoc />
    public event Action<Uri>? LinkOpened { add { } remove { } }
}

/// <summary>Push is mobile-only; web default does nothing.</summary>
public sealed class NoopPushService : IPushService
{
    /// <inheritdoc />
    public ValueTask<bool> RequestPermissionAsync() => ValueTask.FromResult(false);
    /// <inheritdoc />
    public ValueTask<string?> GetTokenAsync() => ValueTask.FromResult<string?>(null);
}

/// <summary>Default platform info for the browser (hosts override).</summary>
public sealed class DefaultPlatformInfo : IPlatformInfo
{
    /// <inheritdoc />
    public ClientPlatform Platform => ClientPlatform.Web;
    /// <inheritdoc />
    public bool IsTouch => false;
    /// <inheritdoc />
    public bool SupportsHaptics => false;
    /// <inheritdoc />
    public string AppVersion => "1.0";
}
