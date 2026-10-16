using System.Collections.Immutable;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Stores;

/// <summary>
/// Unfinished games of the signed-in player. Refreshes after Hello (sign-in / reconnect), when a match is found, when a game
/// ends, and on demand from the pages that show it. The owner reported a live browser game that "didn't appear for so long":
/// nothing listed active games before.
/// </summary>
public sealed class ActiveMatchesStore : StoreBase<ActiveMatchesState>, IActiveMatchesStore
{
    private readonly TttApiClient _api;
    private readonly ISessionStore _session;
    private Task? _inflight;

    /// <summary>Creates the store.</summary>
    public ActiveMatchesStore(TttApiClient api, IRealtimeTransport rt, ISessionStore session) : base(ActiveMatchesState.Empty)
    {
        _api = api; _session = session;
        if (session is SessionStore concrete) concrete.HelloReceived += h => _ = RefreshAsync();
        rt.MatchFoundReceived += f => _ = RefreshAsync();
        rt.PostGameSummaryReceived += p => _ = RefreshAsync();
        session.Changed += () => { if (session.State.Status == SessionStatus.SignedOut && State.Matches.Length > 0) Update(_ => ActiveMatchesState.Empty); };
    }

    /// <inheritdoc />
    public Task RefreshAsync()
    {
        if (_session.State.Status != SessionStatus.SignedIn) return Task.CompletedTask;
        return _inflight ??= Load();
    }

    private async Task Load()
    {
        try
        {
            var list = await _api.GetActiveMatchesAsync();
            Update(_ => new ActiveMatchesState([.. list], true));
        }
        catch (Exception) { Update(s => s with { Loaded = true }); }
        finally { _inflight = null; }
    }

    /// <inheritdoc />
    public void Forget(Guid matchId) => Update(s => s with { Matches = [.. s.Matches.Where(m => m.MatchId != matchId)] });
}
