using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace TicTacToeUltimate.Shared.Platform;

/// <summary>
/// Lazily imports <c>_content/TicTacToeUltimate.Shared/js/ttt.js</c>. The runtime is attached by <see cref="Routes"/>
/// (works for scoped IJSRuntime in Blazor Hybrid); before that it is resolved from the root provider when possible.
/// Every call is failure-tolerant: when JS is unavailable the call is a no-op and returns the default.
/// </summary>
public sealed class TttJsModule : IAsyncDisposable, IDisposable
{
    /// <inheritdoc />
    public void Dispose() => _ = DisposeAsync().AsTask();

    /// <summary>Module path served by the RCL.</summary>
    public const string ModulePath = "./_content/TicTacToeUltimate.Shared/js/ttt.js";

    private readonly IServiceProvider _services;
    private IJSRuntime? _js;
    private Task<IJSObjectReference>? _module;
    private readonly object _gate = new();

    /// <summary>Creates the module wrapper.</summary>
    public TttJsModule(IServiceProvider services) => _services = services;

    /// <summary>Raised once a JS runtime has been attached.</summary>
    public event Action? Attached;

    /// <summary>True once a runtime is known.</summary>
    public bool IsAttached => _js is not null;

    /// <summary>Attaches the runtime of the current renderer (idempotent).</summary>
    public void Attach(IJSRuntime js)
    {
        lock (_gate)
        {
            if (_js is not null) return;
            _js = js;
        }
        Attached?.Invoke();
    }

    private IJSRuntime? Runtime
    {
        get
        {
            if (_js is not null) return _js;
            try { _js = _services.GetService<IJSRuntime>(); } catch (InvalidOperationException) { _js = null; }
            return _js;
        }
    }

    private Task<IJSObjectReference>? Module()
    {
        var js = Runtime;
        if (js is null) return null;
        lock (_gate) return _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();
    }

    /// <summary>Invokes a module function; returns default on any JS failure.</summary>
    public async ValueTask<T?> InvokeAsync<T>(string identifier, params object?[] args)
    {
        try
        {
            var m = Module();
            if (m is null) return default;
            var mod = await m.ConfigureAwait(false);
            if (mod is null) return default;
            return await mod.InvokeAsync<T>(identifier, args).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException or NotSupportedException)
        {
            lock (_gate) { if (_module is { IsFaulted: true }) _module = null; }
            return default;
        }
    }

    /// <summary>Invokes a void module function; swallows JS failures.</summary>
    public async ValueTask InvokeVoidAsync(string identifier, params object?[] args)
    {
        try
        {
            var m = Module();
            if (m is null) return;
            var mod = await m.ConfigureAwait(false);
            if (mod is null) return;
            await mod.InvokeVoidAsync(identifier, args).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException or NotSupportedException)
        {
            lock (_gate) { if (_module is { IsFaulted: true }) _module = null; }
        }
    }

    /// <summary>Fire-and-forget void call.</summary>
    public void Fire(string identifier, params object?[] args) => _ = InvokeVoidAsync(identifier, args).AsTask();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true })
        {
            try { await _module.Result.DisposeAsync().ConfigureAwait(false); }
            catch (JSDisconnectedException) { }
        }
    }
}
