using TicTacToeUltimate.Client.State;

namespace TicTacToeUltimate.Client.Stores;

/// <summary>Immutable-state store base: atomic replace, then <see cref="Changed"/>.</summary>
public abstract class StoreBase<TState>(TState initial) : IStore<TState> where TState : class
{
    /// <summary>Guards state transitions.</summary>
    protected readonly Lock Gate = new();
    private TState _state = initial;

    /// <inheritdoc />
    public TState State => Volatile.Read(ref _state);

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>Atomically replaces the state and raises <see cref="Changed"/>.</summary>
    protected TState Update(Func<TState, TState> change)
    {
        TState next;
        lock (Gate)
        {
            next = change(_state);
            if (ReferenceEquals(next, _state)) return next;
            Volatile.Write(ref _state, next);
        }
        RaiseChanged();
        return next;
    }

    /// <summary>Raises <see cref="Changed"/>, swallowing subscriber failures.</summary>
    protected void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception) { /* a broken subscriber must not break the store */ }
        }
    }
}
