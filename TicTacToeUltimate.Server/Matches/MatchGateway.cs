using System.Collections.Concurrent;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.Matches;

/// <summary>Single-flight actor registry (§6.5).</summary>
internal sealed class MatchRegistry(Func<Guid, Task<MatchActor?>> activate)
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<MatchActor?>>> _actors = new();

    public async Task<MatchActor?> GetOrActivateAsync(Guid id)
    {
        var lazy = _actors.GetOrAdd(id, k => new Lazy<Task<MatchActor?>>(() => activate(k)));
        try
        {
            var actor = await lazy.Value.ConfigureAwait(false);
            if (actor is null) _actors.TryRemove(KeyValuePair.Create(id, lazy));
            return actor;
        }
        catch
        {
            _actors.TryRemove(KeyValuePair.Create(id, lazy));
            throw;
        }
    }

    public void Add(MatchActor actor) => _actors[actor.Id] = new Lazy<Task<MatchActor?>>(Task.FromResult<MatchActor?>(actor));

    public void Remove(MatchActor actor)
    {
        if (_actors.TryGetValue(actor.Id, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully
            && ReferenceEquals(lazy.Value.Result, actor))
            _actors.TryRemove(KeyValuePair.Create(actor.Id, lazy));
    }

    public MatchActor? TryGetLoaded(Guid id)
        => _actors.TryGetValue(id, out var l) && l.IsValueCreated && l.Value.IsCompletedSuccessfully ? l.Value.Result : null;

    public IEnumerable<MatchActor> Loaded()
    {
        foreach (var l in _actors.Values)
            if (l.IsValueCreated && l.Value.IsCompletedSuccessfully && l.Value.Result is { } a) yield return a;
    }
}

/// <summary>WP2 implementation of <see cref="IMatchGateway"/> plus connection-level operations used by the hub.</summary>
public sealed class MatchGateway : IMatchGateway
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(2);
    private readonly MatchRuntime _rt;
    private readonly MatchRegistry _registry;
    private readonly ILogger<MatchGateway> _log;
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, byte>> _active = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> _connections = new();
    private readonly MatchActorHooks _hooks;

    /// <summary>Creates the gateway.</summary>
    public MatchGateway(MatchRuntime runtime)
    {
        _rt = runtime;
        _log = runtime.Loggers.CreateLogger<MatchGateway>();
        _registry = new MatchRegistry(ActivateAsync);
        _hooks = new MatchActorHooks(OnEnded, a => _registry.Remove(a), CreateRematch);
    }

    /// <summary>Shared runtime.</summary>
    public MatchRuntime Runtime => _rt;

    // ---------------- creation / activation ----------------

    /// <inheritdoc />
    public async Task<Guid> CreateMatchAsync(MatchSetup setup, CancellationToken ct)
    {
        var cards = await CardsAsync(setup, ct).ConfigureAwait(false);
        if (setup.TimeControl == TimeControl.Daily)
        {
            if (_rt.DailyRepo is { } repo)
            {
                var now = _rt.Time.GetUtcNow();
                await repo.CreateAsync(setup, now, now + MoveClock.DailyPerMove, ct).ConfigureAwait(false);
            }
            _rt.Activity.AddDaily(setup.SeatA.UserId, setup.MatchId);
            _rt.Activity.AddDaily(setup.SeatB.UserId, setup.MatchId);
        }
        var actor = MatchActor.CreateNew(_rt, setup, cards, _hooks);
        Track(actor);
        _registry.Add(actor);
        return setup.MatchId;
    }

    private bool CreateRematch(MatchSetup setup)
    {
        var a = setup.SeatA.Kind == AccountKind.Bot || _rt.Activity.TryStartMatch(setup.SeatA.UserId, setup.MatchId);
        if (!a) return false;
        if (setup.SeatB.Kind != AccountKind.Bot && !_rt.Activity.TryStartMatch(setup.SeatB.UserId, setup.MatchId))
        {
            _rt.Activity.EndMatch(setup.SeatA.UserId, setup.MatchId);
            return false;
        }
        _ = Task.Run(async () =>
        {
            try { await CreateMatchAsync(setup, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Rematch {MatchId} creation failed", setup.MatchId);
                _rt.Activity.EndMatch(setup.SeatA.UserId, setup.MatchId);
                _rt.Activity.EndMatch(setup.SeatB.UserId, setup.MatchId);
            }
        });
        return true;
    }

    private async Task<PlayerCardDto[]> CardsAsync(MatchSetup setup, CancellationToken ct)
    {
        try
        {
            var map = await _rt.Cards.GetCardsAsync([setup.SeatA.UserId, setup.SeatB.UserId], RatingPools.For(setup.TimeControl), ct).ConfigureAwait(false);
            return
            [
                map.TryGetValue(setup.SeatA.UserId, out var a) ? a : MatchModes.FallbackCard(setup.SeatA),
                map.TryGetValue(setup.SeatB.UserId, out var b) ? b : MatchModes.FallbackCard(setup.SeatB),
            ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Card lookup failed for {MatchId}", setup.MatchId);
            return [MatchModes.FallbackCard(setup.SeatA), MatchModes.FallbackCard(setup.SeatB)];
        }
    }

    private async Task<MatchActor?> ActivateAsync(Guid id)
    {
        if (_rt.DailyRepo is not { } repo) return null;
        var stored = await repo.LoadAsync(id, CancellationToken.None).ConfigureAwait(false);
        if (stored is null || stored.Ended) return null;
        var cards = await CardsAsync(stored.Setup, CancellationToken.None).ConfigureAwait(false);
        var actor = MatchActor.Rehydrate(_rt, stored, cards, _hooks);
        if (actor is not null) Track(actor);
        return actor;
    }

    internal Task<MatchActor?> GetOrActivateAsync(Guid id) => _registry.GetOrActivateAsync(id);
    internal MatchActor? TryGetLoaded(Guid id) => _registry.TryGetLoaded(id);
    internal IEnumerable<MatchActor> LoadedActors => _registry.Loaded();

    private void Track(MatchActor actor)
    {
        foreach (var u in new[] { actor.Setup.SeatA.UserId, actor.Setup.SeatB.UserId })
            _active.GetOrAdd(u, _ => new()).TryAdd(actor.Id, 0);
    }

    private void OnEnded(MatchActor actor)
    {
        foreach (var u in new[] { actor.Setup.SeatA.UserId, actor.Setup.SeatB.UserId })
            if (_active.TryGetValue(u, out var set)) set.TryRemove(actor.Id, out _);
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> ActiveMatchesFor(Guid userId) => _active.TryGetValue(userId, out var s) ? [.. s.Keys] : [];

    /// <summary>Live summaries of loaded, unfinished matches.</summary>
    public IReadOnlyList<LiveMatchInfo> LiveMatches() => [.. _registry.Loaded().Select(a => a.Info).Where(i => !i.Ended)];

    // ---------------- asks ----------------

    private async Task<T> AskAsync<T>(Guid matchId, Func<long, TaskCompletionSource<T>, MatchCommand> make, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var actor = await _registry.GetOrActivateAsync(matchId).ConfigureAwait(false)
                ?? throw new MatchAccessException(ErrorCodes.MatchNotFound);
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!actor.TryPost(ts => make(ts, tcs))) { _registry.Remove(actor); continue; }
            try { return await tcs.Task.WaitAsync(AskTimeout, ct).ConfigureAwait(false); }
            catch (MatchClosedException) { _registry.Remove(actor); }
            catch (TimeoutException) { throw new MatchAccessException(ErrorCodes.Internal); }
        }
        throw new MatchAccessException(ErrorCodes.MatchNotFound);
    }

    private bool Post(Guid matchId, Func<long, MatchCommand> make)
        => _registry.TryGetLoaded(matchId) is { } a && a.TryPost(make);

    /// <inheritdoc />
    public async Task<AttachResult> AttachAsync(Guid matchId, Guid userId, IMatchObserver observer, CancellationToken ct)
    {
        var key = "o:" + Guid.NewGuid().ToString("N");
        var snap = await AskAsync<MatchSnapshotDto>(matchId, (ts, t) => new AttachCmd(ts, userId, key, null, observer, false, t), ct).ConfigureAwait(false);
        return new AttachResult(snap, new Subscription(() => Post(matchId, ts => new DetachCmd(ts, key))));
    }

    /// <summary>Attaches a SignalR connection (participant, or spectator when <paramref name="allowSpectator"/>).</summary>
    public async Task<MatchSnapshotDto> AttachConnectionAsync(Guid matchId, Guid userId, string connectionId, bool allowSpectator, CancellationToken ct)
    {
        var snap = await AskAsync<MatchSnapshotDto>(matchId,
            (ts, t) => new AttachCmd(ts, userId, "c:" + connectionId, connectionId, null, allowSpectator, t), ct).ConfigureAwait(false);
        _connections.GetOrAdd(connectionId, _ => new()).TryAdd(matchId, 0);
        return snap;
    }

    /// <summary>Detaches one connection from one match.</summary>
    public void DetachConnection(Guid matchId, string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out var set)) set.TryRemove(matchId, out _);
        Post(matchId, ts => new DetachCmd(ts, "c:" + connectionId));
    }

    /// <summary>Detaches a closed connection from all its matches.</summary>
    public void DetachConnectionEverywhere(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var set)) return;
        foreach (var m in set.Keys) Post(m, ts => new DetachCmd(ts, "c:" + connectionId));
    }

    /// <inheritdoc />
    public async Task<MatchSnapshotDto?> GetSnapshotAsync(Guid matchId, Guid? viewerUserId, CancellationToken ct)
    {
        try { return await AskAsync<MatchSnapshotDto>(matchId, (ts, t) => new SnapshotCmd(ts, viewerUserId, t), ct).ConfigureAwait(false); }
        catch (MatchAccessException e) when (e.Code == ErrorCodes.MatchNotFound) { return null; }
    }

    /// <inheritdoc />
    public async Task<MoveAckDto> SubmitActionAsync(Guid matchId, Guid userId, int expectedPly, GameAction action, Guid actionId, CancellationToken ct)
    {
        try { return await AskAsync<MoveAckDto>(matchId, (ts, t) => new SubmitCmd(ts, userId, expectedPly, action, actionId, t), ct).ConfigureAwait(false); }
        catch (MatchAccessException e) { return new MoveAckDto(false, e.Code, 0, -1); }
    }

    /// <inheritdoc />
    public Task<CommandAckDto> ResignAsync(Guid matchId, Guid userId, CancellationToken ct)
        => CommandAsync(matchId, (ts, t) => new ResignCmd(ts, userId, t), ct);

    /// <summary>Sends an emote.</summary>
    public Task<CommandAckDto> EmoteAsync(Guid matchId, Guid userId, string emoteId, CancellationToken ct)
        => CommandAsync(matchId, (ts, t) => new EmoteCmd(ts, userId, emoteId, t), ct);

    /// <summary>Offers a rematch.</summary>
    public Task<CommandAckDto> OfferRematchAsync(Guid matchId, Guid userId, CancellationToken ct)
        => CommandAsync(matchId, (ts, t) => new RematchCmd(ts, userId, t), ct);

    private async Task<CommandAckDto> CommandAsync(Guid matchId, Func<long, TaskCompletionSource<CommandAckDto>, MatchCommand> make, CancellationToken ct)
    {
        try { return await AskAsync(matchId, make, ct).ConfigureAwait(false); }
        catch (MatchAccessException e) { return new CommandAckDto(false, e.Code); }
    }

    /// <summary>Feeds an RTT sample to the user's loaded matches.</summary>
    public void PostRtt(Guid userId, int rttMs, int halfRttMs)
    {
        foreach (var m in ActiveMatchesFor(userId)) Post(m, ts => new RttCmd(ts, userId, rttMs, halfRttMs));
    }

    /// <summary>Daily sweeper: activates and posts an unconditional deadline check.</summary>
    public async Task CheckDeadlineAsync(Guid matchId)
    {
        var actor = await _registry.GetOrActivateAsync(matchId).ConfigureAwait(false);
        actor?.TryPost(ts => new TickCmd(ts, -1));
    }

    /// <summary>Waits until a match's queued commands and outbox are processed (tests, shutdown).</summary>
    internal Task FlushAsync(Guid matchId) => _registry.TryGetLoaded(matchId)?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>Host shutdown: live non-Daily matches end Aborted ("server_restart").</summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        foreach (var actor in _registry.Loaded().ToArray())
        {
            if (actor.Setup.TimeControl == TimeControl.Daily) continue;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!actor.TryPost(ts => new ShutdownCmd(ts, "server_restart", tcs))) continue;
            try
            {
                await tcs.Task.WaitAsync(AskTimeout, ct).ConfigureAwait(false);
                await actor.FlushAsync().WaitAsync(AskTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or MatchClosedException)
            {
                _log.LogWarning("Shutdown of match {MatchId} did not complete: {Error}", actor.Id, ex.GetType().Name);
            }
        }
    }

    private sealed class Subscription(Func<bool> detach) : IAsyncDisposable
    {
        private int _done;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) detach();
            return ValueTask.CompletedTask;
        }
    }
}
