using System.Threading.Channels;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;

namespace TicTacToeUltimate.Server.Matches;

/// <summary>
/// One match = one actor (§6.5). All state is owned by the single reader of <see cref="_inbox"/>; the only lock is the ingress lock
/// that stamps and enqueues atomically, so channel order equals stamp order.
/// </summary>
internal sealed class MatchActor
{
    private const int LingerSeconds = 120;
    private const int AbandonCheckSeconds = 30;

    private readonly Lock _ingress = new();
    private readonly Channel<MatchCommand> _inbox = Channel.CreateUnbounded<MatchCommand>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<OutboxItem> _outbox = Channel.CreateUnbounded<OutboxItem>(new UnboundedChannelOptions { SingleReader = true });
    private bool _closed;

    private readonly MatchRuntime _rt;
    private readonly MatchActorHooks _hooks;
    private readonly ILogger _log;
    private readonly long _baseTs;
    private readonly long _baseWallMs;
    private readonly PlayerCardDto[] _cards;

    private RulesSnapshot _state;
    private readonly List<StoredAction> _actions = [];
    private readonly List<ActionDto> _actionDtos = [];
    private long _seq;
    private long _turnToken;
    private long _turnStartMs;
    private long? _deadlineMs;
    private int? _allowanceMs;
    private int _credit;
    private readonly int[] _budget = new int[2];
    private readonly int[] _halfRtt = new int[2];
    private readonly int?[] _lastRtt = new int?[2];
    private long _gameElapsedMs;
    private long _startedAtMs;

    private readonly Dictionary<string, (Guid UserId, int? Seat, string? ConnectionId, bool Observer)> _attachments = [];
    private readonly long?[] _detachedSinceMs = new long?[2];
    private readonly Dictionary<Guid, MoveAckDto> _acks = [];

    private MatchResultDto? _result;
    private long _endedAtMs;
    private readonly bool[] _rematchOffered = new bool[2];
    private bool _rematchStarted;

    private readonly double[] _emoteTokens = [3, 3];
    private readonly long[] _emoteLastMs = new long[2];
    private readonly int[] _emoteCount = new int[2];

    private int _lastSpecSent;
    private long _lastSpecSentMs = long.MinValue / 2;
    private bool _specArmed;

    private ITimer? _turnTimer, _lingerTimer, _abandonTimer, _idleTimer, _specTimer;
    private long _lastActivityMs;

    private long _dailyVersion;
    private readonly List<StoredAction> _pendingPersist = [];
    private bool _pendingEnded;
    private Action? _pendingReply;

    public MatchSetup Setup { get; }
    public Guid Id => Setup.MatchId;
    public LiveMatchInfo Info { get; private set; } = null!;
    private TimeControl Tc => Setup.TimeControl;
    private bool IsDaily => Tc == TimeControl.Daily;

    private MatchActor(MatchRuntime rt, MatchSetup setup, PlayerCardDto[] cards, MatchActorHooks hooks)
    {
        _rt = rt;
        _hooks = hooks;
        Setup = setup;
        _cards = cards;
        _log = rt.Loggers.CreateLogger<MatchActor>();
        _baseTs = rt.Time.GetTimestamp();
        _baseWallMs = rt.NowMs;
        _budget[0] = _budget[1] = Math.Max(0, rt.Options.Clock.LagBudgetMs);
        _state = rt.Rules.Initial(setup.Rules);
    }

    /// <summary>New match: first turn starts now.</summary>
    public static MatchActor CreateNew(MatchRuntime rt, MatchSetup setup, PlayerCardDto[] cards, MatchActorHooks hooks)
    {
        var a = new MatchActor(rt, setup, cards, hooks);
        var now = a.NowMs();
        a._startedAtMs = now;
        a._lastActivityMs = now;
        a._detachedSinceMs[0] = a._detachedSinceMs[1] = now;
        a._emoteLastMs[0] = a._emoteLastMs[1] = now;
        a.StartTurn(now);
        a.Start();
        return a;
    }

    /// <summary>Daily rehydration from the store; null if the stored match is over.</summary>
    public static MatchActor? Rehydrate(MatchRuntime rt, StoredDailyMatch stored, PlayerCardDto[] cards, MatchActorHooks hooks)
    {
        if (stored.Ended) return null;
        var a = new MatchActor(rt, stored.Setup, cards, hooks);
        a.LoadFrom(stored);
        if (a._state.Phase == GamePhase.Ended) return null;
        var now = a.NowMs();
        a._lastActivityMs = now;
        a._detachedSinceMs[0] = a._detachedSinceMs[1] = now;
        a._emoteLastMs[0] = a._emoteLastMs[1] = now;
        a.Start();
        return a;
    }

    private void LoadFrom(StoredDailyMatch stored)
    {
        var s = _rt.Rules.Initial(Setup.Rules);
        _actions.Clear();
        _actionDtos.Clear();
        _gameElapsedMs = 0;
        foreach (var act in stored.Actions)
        {
            var step = _rt.Rules.Apply(s, act.Action);
            if (!step.Ok) throw new InvalidActionException(act.Ply, step.Error);
            _actions.Add(act);
            _actionDtos.Add(ToDto(act, s.ToMove));
            _gameElapsedMs += act.ThinkMs;
            s = step.State;
        }
        _state = s;
        _dailyVersion = stored.Version;
        _startedAtMs = stored.StartedAt.ToUnixTimeMilliseconds();
        _turnToken++;
        _turnStartMs = stored.Actions.Count > 0 ? stored.Actions[^1].At.ToUnixTimeMilliseconds() : _startedAtMs;
        _allowanceMs = (int)MoveClock.DailyPerMove.TotalMilliseconds;
        _deadlineMs = stored.TurnDeadline?.ToUnixTimeMilliseconds() ?? _turnStartMs + _allowanceMs;
        _credit = 0;
        ArmTurnTimer();
        UpdateInfo();
    }

    private void Start()
    {
        UpdateInfo();
        if (Tc == TimeControl.Unlimited)
            _abandonTimer = _rt.Time.CreateTimer(_ => TryPost(ts => new AbandonCheckCmd(ts)), null,
                TimeSpan.FromSeconds(AbandonCheckSeconds), TimeSpan.FromSeconds(AbandonCheckSeconds));
        if (IsDaily)
        {
            var p = TimeSpan.FromMinutes(Math.Max(1, _rt.Options.Daily.PassivateMinutes));
            _idleTimer = _rt.Time.CreateTimer(_ => TryPost(ts => new CloseCmd(ts)), null, p, p);
        }
        _ = Task.Run(RunAsync);
        _ = Task.Run(PublishAsync);
    }

    // ---------------- ingress ----------------

    /// <summary>Stamp + enqueue under one lock (§6.5). False when the actor is closed.</summary>
    internal bool TryPost(Func<long, MatchCommand> create)
    {
        lock (_ingress)
        {
            if (_closed) return false;
            return _inbox.Writer.TryWrite(create(_rt.Time.GetTimestamp()));
        }
    }

    /// <summary>Waits until every command posted before this call is handled and its outbox items are published.</summary>
    internal async Task FlushAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryPost(ts => new FlushCmd(ts, tcs))) return;
        await tcs.Task.ConfigureAwait(false);
    }

    // ---------------- loops ----------------

    private async Task RunAsync()
    {
        await foreach (var cmd in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Handle(cmd);
                if (_pendingPersist.Count > 0 || _pendingEnded) await PersistAsync(cmd, retried: false).ConfigureAwait(false);
                var reply = _pendingReply;
                _pendingReply = null;
                reply?.Invoke();
            }
            catch (Exception ex)
            {
                _pendingReply = null;
                _log.LogError(ex, "Match {MatchId}: command {Command} failed", Id, cmd.GetType().Name);
                cmd.Fail(ex);
            }
            if (cmd is not CloseCmd) UpdateInfo();
        }
    }

    private async Task PublishAsync()
    {
        var observers = new Dictionary<string, IMatchObserver>();
        await foreach (var item in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (item)
                {
                    case GroupAddItem g: await _rt.Broadcaster.AddToGroupAsync(g.ConnectionId, Id, CancellationToken.None).ConfigureAwait(false); break;
                    case GroupRemoveItem g: await _rt.Broadcaster.RemoveFromGroupAsync(g.ConnectionId, Id, CancellationToken.None).ConfigureAwait(false); break;
                    case ObserverAddItem o: observers[o.Key] = o.Observer; break;
                    case ObserverRemoveItem o: observers.Remove(o.Key); break;
                    case PublishItem p:
                        try { await _rt.Broadcaster.SendAsync(p.Envelope, CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception ex) { _log.LogWarning(ex, "Match {MatchId}: group send failed", Id); }
                        foreach (var obs in observers.Values)
                        {
                            try { await obs.OnEventAsync(p.Envelope, CancellationToken.None).ConfigureAwait(false); }
                            catch (Exception ex) { _log.LogWarning(ex, "Match {MatchId}: observer failed", Id); }
                        }
                        break;
                    case SinkItem s: await _rt.Sink.EnqueueAsync(s.Match, CancellationToken.None).ConfigureAwait(false); break;
                    case CallbackItem c: await c.Run().ConfigureAwait(false); break;
                    case BarrierItem b: b.Done.TrySetResult(true); break;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Match {MatchId}: outbox item {Item} failed", Id, item.GetType().Name);
            }
        }
    }

    // ---------------- time ----------------

    private long Ms(long ts) => _baseWallMs + (long)Math.Floor(_rt.Time.GetElapsedTime(_baseTs, ts).TotalMilliseconds);
    private long NowMs() => Ms(_rt.Time.GetTimestamp());
    private static DateTimeOffset Wall(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    // ---------------- seats ----------------

    private int? SeatOf(Guid user) => user == Setup.SeatA.UserId ? 0 : user == Setup.SeatB.UserId ? 1 : null;
    private Color ColorOfSeat(int seat) => (seat == 0) ^ _state.Swapped ? Color.X : Color.O;
    private int SeatOfColor(Color c) => (c == Color.X) ^ _state.Swapped ? 0 : 1;
    private int? ToMoveSeat => _result is null && _state.Phase != GamePhase.Ended ? SeatOfColor(_state.ToMove) : null;
    private SeatSetup SeatSetupOf(int seat) => seat == 0 ? Setup.SeatA : Setup.SeatB;
    private bool SeatConnected(int seat) => _attachments.Values.Any(a => a.Seat == seat);
    private int SpectatorCount => _attachments.Values.Where(a => a.Seat is null).Select(a => a.UserId).Distinct().Count();

    private SeatDto[] Seats() =>
    [
        new SeatDto(0, _cards[0], ColorOfSeat(0), SeatConnected(0), _lastRtt[0]),
        new SeatDto(1, _cards[1], ColorOfSeat(1), SeatConnected(1), _lastRtt[1]),
    ];

    private bool RematchAllowed(long now) => !Setup.Rated && _result is not null && !_rematchStarted
        && now - _endedAtMs <= _rt.Options.Rematch.WindowSeconds * 1000L;

    // ---------------- handling ----------------

    private void Reply<T>(TaskCompletionSource<T> tcs, T value) => _pendingReply = () => tcs.TrySetResult(value);

    private void Handle(MatchCommand cmd)
    {
        var now = Ms(cmd.Ts);
        if (cmd is not CloseCmd and not TickCmd) _lastActivityMs = now;
        switch (cmd)
        {
            case AttachCmd c: HandleAttach(c, now); break;
            case DetachCmd c: HandleDetach(c, now); break;
            case SubmitCmd c: HandleSubmit(c, now); break;
            case ResignCmd c: HandleResign(c, now); break;
            case EmoteCmd c: HandleEmote(c, now); break;
            case RematchCmd c: HandleRematch(c, now); break;
            case RttCmd c:
                if (SeatOf(c.UserId) is { } rs)
                {
                    _halfRtt[rs] = Math.Clamp(c.HalfRttMs, 0, 1000);
                    _lastRtt[rs] = c.RttMs;
                }
                break;
            case TickCmd c: HandleTick(c, now); break;
            case AbandonCheckCmd: HandleAbandon(now); break;
            case SnapshotCmd c: Reply(c.Reply, BuildSnapshot(c.Viewer is { } v ? SeatOf(v) : null, now)); break;
            case ShutdownCmd c:
                if (_result is null && !IsDaily) End(Outcome.None, null, EndReason.Aborted, [], c.Detail, now);
                Reply(c.Reply, true);
                break;
            case CloseCmd: HandleClose(now); break;
            case FlushSpectatorsCmd: _specArmed = false; SpectatorsChanged(now); break;
            case FlushCmd c: _outbox.Writer.TryWrite(new BarrierItem(c.Reply)); break;
        }
    }

    private void HandleAttach(AttachCmd c, long now)
    {
        var seat = SeatOf(c.UserId);
        if (seat is null && !c.AllowSpectator)
        {
            _pendingReply = () => c.Reply.TrySetException(new MatchAccessException(ErrorCodes.NotAParticipant));
            return;
        }
        var wasConnected = seat is { } s0 && SeatConnected(s0);
        _attachments[c.Key] = (c.UserId, seat, c.ConnectionId, c.Observer is not null);
        if (seat is { } s)
        {
            _detachedSinceMs[s] = null;
            if (!wasConnected) Emit(new SeatConnectionChanged(s, true, _lastRtt[s]), now);
        }
        else SpectatorsChanged(now);
        var snapshot = BuildSnapshot(seat, now);
        // Join ordering: the group add / observer registration is queued before replying, so every later Seq reaches it.
        if (c.ConnectionId is not null) _outbox.Writer.TryWrite(new GroupAddItem(c.ConnectionId));
        if (c.Observer is not null) _outbox.Writer.TryWrite(new ObserverAddItem(c.Key, c.Observer));
        Reply(c.Reply, snapshot);
    }

    private void HandleDetach(DetachCmd c, long now)
    {
        if (!_attachments.Remove(c.Key, out var a)) return;
        if (a.ConnectionId is not null) _outbox.Writer.TryWrite(new GroupRemoveItem(a.ConnectionId));
        if (a.Observer) _outbox.Writer.TryWrite(new ObserverRemoveItem(c.Key));
        if (a.Seat is { } s)
        {
            if (!SeatConnected(s))
            {
                _detachedSinceMs[s] = now;
                Emit(new SeatConnectionChanged(s, false, _lastRtt[s]), now);
            }
        }
        else SpectatorsChanged(now);
    }

    private void HandleSubmit(SubmitCmd c, long now)
    {
        MoveAckDto Ack(string? error) => new(error is null, error, _seq, _state.Ply);
        if (_acks.TryGetValue(c.ActionId, out var prior)) { Reply(c.Reply, prior); return; }
        var seat = SeatOf(c.UserId);
        if (seat is null) { Reply(c.Reply, Ack(ErrorCodes.NotAParticipant)); return; }
        if (_result is not null) { Reply(c.Reply, Ack(ErrorCodes.MatchOver)); return; }
        if (seat != ToMoveSeat) { Reply(c.Reply, Ack(ErrorCodes.NotYourTurn)); return; }
        if (c.ExpectedPly != _state.Ply) { Reply(c.Reply, Ack(ErrorCodes.StalePly)); return; }
        if (_deadlineMs is { } d && now > d)
        {
            TimeoutPath(now);
            Reply(c.Reply, Ack(ErrorCodes.Timeout));
            return;
        }
        var step = _rt.Rules.Apply(_state, c.Action);
        if (!step.Ok) { Reply(c.Reply, Ack(ErrorCodes.FromMoveError(step.Error))); return; }
        var rawThink = (int)Math.Clamp(now - _turnStartMs, 0, int.MaxValue);
        var credit = _credit;
        var think = Math.Max(0, rawThink - credit);
        _budget[seat.Value] = Math.Max(0, _budget[seat.Value] - credit);
        Commit(seat.Value, c.Action, step, think, auto: false, now);
        var ack = new MoveAckDto(true, null, _seq, _state.Ply);
        _acks[c.ActionId] = ack;
        Reply(c.Reply, ack);
    }

    private void HandleResign(ResignCmd c, long now)
    {
        var seat = SeatOf(c.UserId);
        if (seat is null) { Reply(c.Reply, new CommandAckDto(false, ErrorCodes.NotAParticipant)); return; }
        if (_result is not null) { Reply(c.Reply, new CommandAckDto(false, ErrorCodes.MatchOver)); return; }
        if (_state.Ply == 0) End(Outcome.None, null, EndReason.Aborted, [], "resign", now);
        else EndWithWinner(1 - seat.Value, EndReason.Resign, now);
        Reply(c.Reply, new CommandAckDto(true, null));
    }

    private void HandleEmote(EmoteCmd c, long now)
    {
        var seat = SeatOf(c.UserId);
        if (seat is not { } s) { Reply(c.Reply, new CommandAckDto(false, ErrorCodes.NotAParticipant)); return; }
        if (!EmoteCatalog.IsKnown(c.EmoteId)) { Reply(c.Reply, new CommandAckDto(false, ErrorCodes.EmoteUnknown)); return; }
        _emoteTokens[s] = Math.Min(3, _emoteTokens[s] + Math.Max(0, now - _emoteLastMs[s]) / 2000.0);
        _emoteLastMs[s] = now;
        if (_emoteCount[s] >= 30 || _emoteTokens[s] < 1) { Reply(c.Reply, new CommandAckDto(false, ErrorCodes.RateLimited)); return; }
        _emoteTokens[s] -= 1;
        _emoteCount[s]++;
        Emit(new EmoteShown(s, c.EmoteId), now);
        Reply(c.Reply, new CommandAckDto(true, null));
    }

    private void HandleRematch(RematchCmd c, long now)
    {
        var seat = SeatOf(c.UserId);
        if (seat is not { } s) { Reply(c.Reply, new CommandAckDto(false, ErrorCodes.NotAParticipant)); return; }
        if (!RematchAllowed(now)) { Reply(c.Reply, new CommandAckDto(false, ErrorCodes.RematchNotAllowed)); return; }
        if (!_rematchOffered[s])
        {
            _rematchOffered[s] = true;
            Emit(new RematchOffered(s), now);
        }
        if (_rematchOffered[0] && _rematchOffered[1])
        {
            var next = Setup with
            {
                MatchId = Guid.NewGuid(), SeatA = Setup.SeatB, SeatB = Setup.SeatA, Seed = MatchModes.NewSeed(),
                Origin = MatchOrigin.Rematch, RematchOf = Setup.MatchId, ChallengeCode = null,
            };
            if (!_hooks.CreateRematch(next)) { Reply(c.Reply, new CommandAckDto(false, ErrorCodes.AlreadyInMatch)); return; }
            _rematchStarted = true;
            Emit(new RematchStarted(next.MatchId), now);
        }
        Reply(c.Reply, new CommandAckDto(true, null));
    }

    private void HandleTick(TickCmd c, long now)
    {
        if (_result is not null) return;
        if (c.Token >= 0 && c.Token != _turnToken) return;
        if (_deadlineMs is not { } d) return;
        if (now < d)
        {
            if (c.Token >= 0) ArmTurnTimer();   // early timer: re-arm for the remainder
            return;
        }
        TimeoutPath(now);
    }

    private void HandleAbandon(long now)
    {
        if (_result is not null || Tc != TimeControl.Unlimited || ToMoveSeat is not { } seat) return;
        if (SeatConnected(seat) || _detachedSinceMs[seat] is not { } since) return;
        if (now - since < _rt.Options.Unlimited.DisconnectForfeitMinutes * 60_000L) return;
        if (_state.Ply == 0) End(Outcome.None, null, EndReason.Aborted, [], "abandoned", now);
        else EndWithWinner(1 - seat, EndReason.Abandoned, now);
    }

    private void HandleClose(long now)
    {
        var idle = IsDaily && _result is null && _attachments.Count == 0
            && now - _lastActivityMs >= Math.Max(1, _rt.Options.Daily.PassivateMinutes) * 60_000L;
        var lingered = _result is not null && now - _endedAtMs >= LingerSeconds * 1000L;
        if (!idle && !lingered) return;
        lock (_ingress)
        {
            _closed = true;
            _inbox.Writer.TryComplete();
        }
        while (_inbox.Reader.TryRead(out var rest)) rest.Fail(new MatchClosedException());
        _turnTimer?.Dispose(); _lingerTimer?.Dispose(); _abandonTimer?.Dispose(); _idleTimer?.Dispose(); _specTimer?.Dispose();
        _outbox.Writer.TryComplete();
        _hooks.OnClosed(this);
    }

    // ---------------- turn machinery ----------------

    private void StartTurn(long now)
    {
        _turnToken++;
        _turnStartMs = now;
        _credit = 0;
        _allowanceMs = null;
        _deadlineMs = null;
        var seat = SeatOfColor(_state.ToMove);
        if (Tc == TimeControl.Daily)
        {
            _allowanceMs = (int)MoveClock.DailyPerMove.TotalMilliseconds;
            _deadlineMs = now + _allowanceMs.Value;
        }
        else if (Tc != TimeControl.Unlimited && _rt.Clock.Allowance(Tc, TimeSpan.FromMilliseconds(_gameElapsedMs)) is { } a)
        {
            _allowanceMs = (int)a.TotalMilliseconds;
            _credit = Math.Max(0, Math.Min(Math.Min(_halfRtt[seat], _rt.Options.Clock.LagCreditCapMs), _budget[seat]));
            _deadlineMs = now + _allowanceMs.Value + _credit;
        }
        ArmTurnTimer();
        Emit(new TurnStarted(_state.Ply, _state.ToMove, seat, _state.Phase, Clock(), _state.LegalCells), now);
        if (IsDaily && _state.Ply > 0 && SeatSetupOf(seat) is { Kind: not AccountKind.Bot } who)
        {
            var n = new NotificationDto(NotificationKind.DailyYourTurn, "Your move", $"It's your turn against {_cards[1 - seat].DisplayName}.", Id, null);
            _outbox.Writer.TryWrite(new CallbackItem(async () =>
            {
                await _rt.Notifier.ToUserAsync(who.UserId, cl => cl.Notification(n)).ConfigureAwait(false);
                await _rt.Push.SendAsync(who.UserId, n, CancellationToken.None).ConfigureAwait(false);
            }));
        }
    }

    private void ArmTurnTimer()
    {
        _turnTimer?.Dispose();
        _turnTimer = null;
        if (_deadlineMs is not { } d) return;
        var due = Math.Max(0, d - NowMs());
        var token = _turnToken;
        _turnTimer = _rt.Time.CreateTimer(_ => TryPost(ts => new TickCmd(ts, token)), null, TimeSpan.FromMilliseconds(due), Timeout.InfiniteTimeSpan);
    }

    private void TimeoutPath(long now)
    {
        var seat = SeatOfColor(_state.ToMove);
        if (_state.Ply == 0) { End(Outcome.None, null, EndReason.Aborted, [], "timeout", now); return; }
        if (Tc is TimeControl.CasualX2 or TimeControl.CasualX4)
        {
            var action = AutoAction(Setup.Seed, _state);
            var step = _rt.Rules.Apply(_state, action);
            if (step.Ok) { Commit(seat, action, step, _allowanceMs ?? 0, auto: true, now); return; }
            _log.LogError("Match {MatchId}: auto action {Action} rejected ({Error})", Id, action, step.Error);
        }
        EndWithWinner(1 - seat, EndReason.Timeout, now);
    }

    /// <summary>D10: Keep in the pie phase, else a SplitMix64(seed ^ ply) pick among legal cells.</summary>
    internal static GameAction AutoAction(ulong seed, RulesSnapshot state)
    {
        if (state.Phase == GamePhase.AwaitingSwapDecision) return GameAction.KeepSides;
        var rng = new SplitMix64(seed ^ (ulong)state.Ply);
        return GameAction.Place(state.LegalCells[rng.NextInt(state.LegalCells.Length)]);
    }

    private void Commit(int seat, GameAction action, RulesStep step, int thinkMs, bool auto, long now)
    {
        var prev = _state;
        _state = step.State;
        _gameElapsedMs += thinkMs;
        var stored = new StoredAction(prev.Ply, seat, action, thinkMs, auto, step.VanishedCell, Wall(now));
        _actions.Add(stored);
        var dto = ToDto(stored, prev.ToMove);
        _actionDtos.Add(dto);
        if (IsDaily) _pendingPersist.Add(stored);
        Emit(new ActionApplied(dto, _state.Board, _state.NextToVanish, _state.Swapped, Seats()), now);
        if (_state.Phase == GamePhase.Ended) EndFromOutcome(now);
        else StartTurn(now);
    }

    private static ActionDto ToDto(StoredAction a, Color mover) => a.Action.Kind == ActionKind.Place
        ? new ActionDto(a.Ply, a.Seat, a.Action.Kind, a.Action.Cell, mover, a.VanishedCell, a.ThinkMs, a.Auto)
        : new ActionDto(a.Ply, a.Seat, a.Action.Kind, null, null, null, a.ThinkMs, a.Auto);

    private void EndFromOutcome(long now)
    {
        switch (_state.Outcome)
        {
            case Outcome.XWins: End(Outcome.XWins, SeatOfColor(Color.X), EndReason.Line, _state.WinLine, null, now); break;
            case Outcome.OWins: End(Outcome.OWins, SeatOfColor(Color.O), EndReason.Line, _state.WinLine, null, now); break;
            default: End(Outcome.Draw, null, EndReason.Draw, [], null, now); break;
        }
    }

    private void EndWithWinner(int winnerSeat, EndReason reason, long now)
        => End(ColorOfSeat(winnerSeat) == Color.X ? Outcome.XWins : Outcome.OWins, winnerSeat, reason, [], null, now);

    private void End(Outcome outcome, int? winner, EndReason reason, int[] winLine, string? detail, long now)
    {
        if (_result is not null) return;
        _turnTimer?.Dispose();
        _turnTimer = null;
        _abandonTimer?.Dispose();
        _turnToken++;
        _deadlineMs = null;
        _result = new MatchResultDto(outcome, winner, reason, winLine, Setup.Rated && reason != EndReason.Aborted, detail);
        _endedAtMs = now;
        Emit(new MatchEnded(_result), now);
        _outbox.Writer.TryWrite(new SinkItem(new CompletedMatch(Setup, _state.Swapped, _actions.ToArray(), _result, Wall(_startedAtMs), Wall(now))));
        if (IsDaily) _pendingEnded = true;
        foreach (var s in new[] { Setup.SeatA, Setup.SeatB })
        {
            if (IsDaily) _rt.Activity.RemoveDaily(s.UserId, Id);
            else _rt.Activity.EndMatch(s.UserId, Id);
        }
        _hooks.OnEnded(this);
        _lingerTimer = _rt.Time.CreateTimer(_ => TryPost(ts => new CloseCmd(ts)), null, TimeSpan.FromSeconds(LingerSeconds), Timeout.InfiniteTimeSpan);
    }

    private void SpectatorsChanged(long now)
    {
        var count = SpectatorCount;
        if (count == _lastSpecSent) return;
        if (now - _lastSpecSentMs >= 1000)
        {
            _lastSpecSent = count;
            _lastSpecSentMs = now;
            Emit(new SpectatorCountChanged(count), now);
        }
        else if (!_specArmed)
        {
            _specArmed = true;
            _specTimer?.Dispose();
            _specTimer = _rt.Time.CreateTimer(_ => TryPost(ts => new FlushSpectatorsCmd(ts)), null,
                TimeSpan.FromMilliseconds(1000 - (now - _lastSpecSentMs)), Timeout.InfiniteTimeSpan);
        }
    }

    private void Emit(MatchEvent e, long now)
    {
        _seq++;
        _outbox.Writer.TryWrite(new PublishItem(new MatchEventEnvelope(Id, _seq, now, e)));
    }

    private ClockDto? Clock()
    {
        if (_result is not null) return null;
        var elapsed = TimeSpan.FromMilliseconds(_gameElapsedMs);
        var scaled = Tc is not (TimeControl.Daily or TimeControl.Unlimited);
        var next = scaled ? _rt.Clock.NextStep(Tc, elapsed) : null;
        return new ClockDto(Tc, _turnStartMs, _allowanceMs is { } a ? _turnStartMs + a : null, _allowanceMs, (int)_gameElapsedMs,
            scaled && _rt.Clock.IsSuddenDeath(Tc, elapsed),
            next is { } n ? (int)n.Allowance.TotalMilliseconds : null,
            next is { } n2 ? (int)n2.AtGameElapsed.TotalMilliseconds : null,
            [_budget[0], _budget[1]]);
    }

    private MatchSnapshotDto BuildSnapshot(int? yourSeat, long now)
    {
        var live = _result is null;
        return new MatchSnapshotDto(Id, _seq, Setup.Mode, Tc, Setup.Rated, RuleSetDto.From(Setup.Rules), Seats(), _state.Swapped,
            _actionDtos.ToArray(), _state.Board, _state.Ply, live ? _state.Phase : GamePhase.Ended, _state.ToMove, ToMoveSeat,
            live ? _state.LegalCells : [], live ? _state.NextToVanish : null, Clock(), _result, yourSeat, SpectatorCount,
            RematchAllowed(now), now);
    }

    private void UpdateInfo() => Info = new LiveMatchInfo(Setup, _cards, _state.Ply, SpectatorCount, _result is not null,
        _state.Swapped, _state.ToMove, ToMoveSeat, _deadlineMs);

    // ---------------- Daily persistence ----------------

    private async Task PersistAsync(MatchCommand cmd, bool retried)
    {
        var repo = _rt.DailyRepo;
        if (repo is null) { _pendingPersist.Clear(); _pendingEnded = false; return; }
        try
        {
            while (_pendingPersist.Count > 0)
            {
                var a = _pendingPersist[0];
                var last = _pendingPersist.Count == 1;
                DateTimeOffset? next = last && _result is null && _deadlineMs is { } d ? Wall(d) : null;
                _dailyVersion = await repo.AppendActionAsync(Id, _dailyVersion, a, next, CancellationToken.None).ConfigureAwait(false);
                _pendingPersist.RemoveAt(0);
            }
            if (_pendingEnded)
            {
                await repo.MarkEndedAsync(Id, CancellationToken.None).ConfigureAwait(false);
                _pendingEnded = false;
            }
        }
        catch (ConcurrencyException ex) when (!retried)
        {
            _log.LogError(ex, "Match {MatchId}: daily version conflict; reloading and re-applying once", Id);
            _pendingPersist.Clear();
            _pendingEnded = false;
            _pendingReply = null;
            var stored = await repo.LoadAsync(Id, CancellationToken.None).ConfigureAwait(false)
                ?? throw new MatchAccessException(ErrorCodes.MatchNotFound);
            _result = null;
            LoadFrom(stored);
            if (stored.Ended) { _result = new MatchResultDto(Outcome.None, null, EndReason.Aborted, [], false, "stale"); return; }
            Handle(cmd);
            if (_pendingPersist.Count > 0 || _pendingEnded) await PersistAsync(cmd, retried: true).ConfigureAwait(false);
        }
    }
}
