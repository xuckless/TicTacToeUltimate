using Microsoft.AspNetCore.Components;
using TicTacToeUltimate.Client.State;

namespace TicTacToeUltimate.Shared.Components;

/// <summary>Base component: subscribes to stores' <c>Changed</c> and re-renders on the renderer's dispatcher; unsubscribes on dispose.</summary>
public abstract class StoreComponent : ComponentBase, IDisposable
{
    private readonly List<IStore<object>> _stores = [];
    private bool _disposed;

    /// <summary>True after dispose.</summary>
    protected bool IsDisposed => _disposed;

    /// <summary>Subscribes to the given stores (call from OnInitialized).</summary>
    protected void Subscribe(params IStore<object>[] stores)
    {
        foreach (var s in stores)
        {
            if (_stores.Contains(s)) continue;
            _stores.Add(s);
            s.Changed += OnStoreChanged;
        }
    }

    /// <summary>Called on the renderer thread after any subscribed store changed, before re-render.</summary>
    protected virtual void OnStoresChanged() { }

    private void OnStoreChanged()
    {
        if (_disposed) return;
        _ = InvokeAsync(() =>
        {
            if (_disposed) return;
            OnStoresChanged();
            StateHasChanged();
        });
    }

    /// <summary>Re-render from any thread.</summary>
    protected void Refresh() { if (!_disposed) _ = InvokeAsync(StateHasChanged); }

    /// <summary>Override to release extra resources; call base.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var s in _stores) s.Changed -= OnStoreChanged;
        _stores.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
