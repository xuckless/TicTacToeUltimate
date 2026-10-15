using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;

namespace TicTacToeUltimate.Server.Tests.Matches;

/// <summary>Reference rules (§4.2, GlobalFifo) so WP2 tests do not depend on WP1's engine.</summary>
public sealed class RefRules : IGameRules
{
    private sealed record S(int[] Cells, Color[] Colors, Color ToMove, int Ply, int Placements, GamePhase Phase, Outcome Outcome, int[] WinLine, bool Swapped);

    public RulesSnapshot Initial(RuleSet rules) => Snap(rules, new S([], [], Color.X, 0, 0, GamePhase.AwaitingPlacement, Outcome.None, [], false));

    public RulesStep Apply(RulesSnapshot snap, GameAction a)
    {
        var r = snap.Rules;
        var s = (S)snap.Inner;
        RulesStep Err(MoveError e) => new(snap, e, null);
        if (s.Phase == GamePhase.Ended) return Err(MoveError.GameOver);
        if (s.Phase == GamePhase.AwaitingSwapDecision)
        {
            if (a.Kind == ActionKind.Place) return Err(MoveError.WrongActionKind);
            var n0 = s with { Ply = s.Ply + 1, Phase = GamePhase.AwaitingPlacement, Swapped = a.Kind == ActionKind.Swap };
            return new RulesStep(Snap(r, n0), MoveError.None, null);
        }
        if (a.Kind != ActionKind.Place) return Err(MoveError.WrongActionKind);
        if (a.Cell < 0 || a.Cell >= r.CellCount) return Err(MoveError.OutOfBounds);
        var cells = s.Cells.ToList();
        var colors = s.Colors.ToList();
        int? removed = null;
        if (r.Removal == RemovalKind.GlobalFifo && cells.Count == r.Window)
        {
            removed = cells[0];
            cells.RemoveAt(0);
            colors.RemoveAt(0);
        }
        if (cells.Contains(a.Cell)) return Err(MoveError.Occupied);
        if (!r.VacatedCellPlayable && removed == a.Cell) return Err(MoveError.CellJustVacated);
        cells.Add(a.Cell);
        colors.Add(s.ToMove);
        var mover = s.ToMove;
        var mine = new HashSet<int>();
        for (var i = 0; i < cells.Count; i++)
        {
            if (colors[i] != mover) continue;
            if (!r.VanishingPieceCanWin && i == 0 && r.Removal == RemovalKind.GlobalFifo && cells.Count == r.Window) continue;
            mine.Add(cells[i]);
        }
        var n = s with { Cells = [.. cells], Colors = [.. colors], Ply = s.Ply + 1, Placements = s.Placements + 1 };
        foreach (var line in Lines(r.BoardSize, r.WinLength))
        {
            if (line.All(mine.Contains))
            {
                n = n with { Phase = GamePhase.Ended, Outcome = mover == Color.X ? Outcome.XWins : Outcome.OWins, WinLine = line };
                return new RulesStep(Snap(r, n), MoveError.None, removed);
            }
        }
        if (r.Opening == OpeningRule.PieSwap && n.Placements == 1)
            n = n with { ToMove = Color.O, Phase = GamePhase.AwaitingSwapDecision };
        else
            n = n with { ToMove = mover == Color.X ? Color.O : Color.X };
        if (n.Phase == GamePhase.AwaitingPlacement && Legal(r, n).Length == 0) n = n with { Phase = GamePhase.Ended, Outcome = Outcome.Draw };
        return new RulesStep(Snap(r, n), MoveError.None, removed);
    }

    private static int[] Legal(RuleSet r, S s)
    {
        if (s.Phase != GamePhase.AwaitingPlacement) return [];
        var occupied = s.Cells.ToList();
        int? vac = null;
        if (r.Removal == RemovalKind.GlobalFifo && occupied.Count == r.Window) { vac = occupied[0]; occupied.RemoveAt(0); }
        return [.. Enumerable.Range(0, r.CellCount).Where(c => !occupied.Contains(c) && (r.VacatedCellPlayable || c != vac))];
    }

    private static int? NextToVanish(RuleSet r, S s)
        => s.Phase == GamePhase.AwaitingPlacement && r.Removal == RemovalKind.GlobalFifo && s.Cells.Length == r.Window ? s.Cells[0] : null;

    private static RulesSnapshot Snap(RuleSet r, S s)
    {
        var board = new char[r.CellCount];
        Array.Fill(board, '.');
        for (var i = 0; i < s.Cells.Length; i++) board[s.Cells[i]] = s.Colors[i] == Color.X ? 'X' : 'O';
        return new RulesSnapshot(r, new string(board), s.Ply, s.Placements, s.Phase, s.ToMove, s.Outcome, s.WinLine, s.Swapped,
            NextToVanish(r, s), Legal(r, s), s);
    }

    private static IEnumerable<int[]> Lines(int n, int k)
    {
        for (var row = 0; row < n; row++)
            for (var c = 0; c + k <= n; c++) yield return [.. Enumerable.Range(0, k).Select(i => row * n + c + i)];
        for (var col = 0; col < n; col++)
            for (var row = 0; row + k <= n; row++) yield return [.. Enumerable.Range(0, k).Select(i => (row + i) * n + col)];
        for (var row = 0; row + k <= n; row++)
            for (var c = 0; c + k <= n; c++)
            {
                yield return [.. Enumerable.Range(0, k).Select(i => (row + i) * n + c + i)];
                yield return [.. Enumerable.Range(0, k).Select(i => (row + i) * n + c + k - 1 - i)];
            }
    }
}

/// <summary>Reference MoveClock (§4.3).</summary>
public sealed class RefClock : IClockSchedule
{
    private static int Scale(TimeControl tc) => tc switch { TimeControl.Ranked => 1, TimeControl.CasualX2 => 2, TimeControl.CasualX4 => 4, _ => 0 };

    public TimeSpan? Allowance(TimeControl tc, TimeSpan e)
    {
        if (tc == TimeControl.Unlimited) return null;
        if (tc == TimeControl.Daily) return MoveClock.DailyPerMove;
        var s = Scale(tc);
        if (e < TimeSpan.FromSeconds(60 * s)) return TimeSpan.FromSeconds(15 * s);
        var k = (long)((e - TimeSpan.FromSeconds(60 * s)).TotalMilliseconds / (12_000L * s));
        return TimeSpan.FromSeconds(Math.Max(2 * s, 10 * s - k * s));
    }

    public bool IsSuddenDeath(TimeControl tc, TimeSpan e) => Scale(tc) is var s and > 0 && e >= TimeSpan.FromSeconds(60 * s);

    public (TimeSpan AtGameElapsed, TimeSpan Allowance)? NextStep(TimeControl tc, TimeSpan e)
    {
        var s = Scale(tc);
        if (s == 0) return null;
        if (e < TimeSpan.FromSeconds(60 * s)) return (TimeSpan.FromSeconds(60 * s), TimeSpan.FromSeconds(10 * s));
        var k = (long)((e - TimeSpan.FromSeconds(60 * s)).TotalMilliseconds / (12_000L * s));
        var cur = 10 * s - k * s;
        if (cur <= 2 * s) return null;
        return (TimeSpan.FromSeconds(60 * s + (k + 1) * 12 * s), TimeSpan.FromSeconds(cur - s));
    }
}

public sealed class FakeBroadcaster : IMatchBroadcaster
{
    public ConcurrentQueue<MatchEventEnvelope> Sent { get; } = new();
    public ConcurrentQueue<(string Conn, Guid Match, bool Add)> Groups { get; } = new();
    public Task AddToGroupAsync(string connectionId, Guid matchId, CancellationToken ct) { Groups.Enqueue((connectionId, matchId, true)); return Task.CompletedTask; }
    public Task RemoveFromGroupAsync(string connectionId, Guid matchId, CancellationToken ct) { Groups.Enqueue((connectionId, matchId, false)); return Task.CompletedTask; }
    public Task SendAsync(MatchEventEnvelope envelope, CancellationToken ct) { Sent.Enqueue(envelope); return Task.CompletedTask; }
}

public sealed class RecordingNotifier : IUserNotifier
{
    public ConcurrentQueue<(Guid User, string Method, object? Payload)> Calls { get; } = new();
    public Task ToUserAsync(Guid userId, Func<IGameClient, Task> send) => send(new Client(userId, Calls));
    public async Task ToUsersAsync(IEnumerable<Guid> userIds, Func<IGameClient, Task> send)
    {
        foreach (var u in userIds.Distinct()) await send(new Client(u, Calls));
    }
    public IEnumerable<T> Of<T>(Guid user) => Calls.Where(c => c.User == user).Select(c => c.Payload).OfType<T>();

    private sealed class Client(Guid user, ConcurrentQueue<(Guid, string, object?)> log) : IGameClient
    {
        private Task L(string m, object? p) { log.Enqueue((user, m, p)); return Task.CompletedTask; }
        public Task Ping(int pingId) => L(nameof(Ping), pingId);
        public Task QueueStatus(QueueStatusDto status) => L(nameof(QueueStatus), status);
        public Task MatchFound(MatchFoundDto found) => L(nameof(MatchFound), found);
        public Task MatchEvent(MatchEventEnvelope envelope) => L(nameof(MatchEvent), envelope);
        public Task PostGameSummary(PostGameSummaryDto summary) => L(nameof(PostGameSummary), summary);
        public Task ChallengeReceived(ChallengeDto challenge) => L(nameof(ChallengeReceived), challenge);
        public Task ChallengeUpdated(ChallengeDto challenge) => L(nameof(ChallengeUpdated), challenge);
        public Task PresenceChanged(PresenceDto presence) => L(nameof(PresenceChanged), presence);
        public Task FriendRequestReceived(FriendRequestDto request) => L(nameof(FriendRequestReceived), request);
        public Task FriendsChanged() => L(nameof(FriendsChanged), null);
        public Task DirectMessage(DirectMessageDto message) => L(nameof(DirectMessage), message);
        public Task Notification(NotificationDto notification) => L(nameof(Notification), notification);
    }
}

public sealed class FakeSink : IMatchResultSink
{
    public ConcurrentQueue<CompletedMatch> Completed { get; } = new();
    public ValueTask EnqueueAsync(CompletedMatch match, CancellationToken ct) { Completed.Enqueue(match); return ValueTask.CompletedTask; }
}

public sealed class FakePush : IPushSender
{
    public ConcurrentQueue<(Guid, NotificationDto)> Sent { get; } = new();
    public Task SendAsync(Guid userId, NotificationDto notification, CancellationToken ct) { Sent.Enqueue((userId, notification)); return Task.CompletedTask; }
}

public sealed class FakeCards : IPlayerCardService
{
    public static PlayerCardDto Card(Guid id) => new(id, "p" + id.ToString("N")[..4], "default", null, AccountKind.Account, null, 1200, false);
    public Task<PlayerCardDto> GetCardAsync(Guid userId, RatingPool pool, CancellationToken ct) => Task.FromResult(Card(userId));
    public Task<IReadOnlyDictionary<Guid, PlayerCardDto>> GetCardsAsync(IReadOnlyCollection<Guid> userIds, RatingPool pool, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<Guid, PlayerCardDto>>(userIds.Distinct().ToDictionary(u => u, Card));
}

public sealed class FakeDailyRepo(TimeProvider time) : IDailyMatchRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, StoredDailyMatch> _store = [];
    public int LoadCount;
    public TaskCompletionSource? LoadGate { get; set; }

    public void Put(StoredDailyMatch m) { lock (_gate) _store[m.Setup.MatchId] = m; }
    public StoredDailyMatch? Peek(Guid id) { lock (_gate) return _store.GetValueOrDefault(id); }

    public Task CreateAsync(MatchSetup setup, DateTimeOffset startedAt, DateTimeOffset firstDeadline, CancellationToken ct)
    {
        Put(new StoredDailyMatch(setup, [], 0, firstDeadline, false, startedAt));
        return Task.CompletedTask;
    }

    public Task<long> AppendActionAsync(Guid matchId, long expectedVersion, StoredAction action, DateTimeOffset? nextDeadline, CancellationToken ct)
    {
        lock (_gate)
        {
            var m = _store[matchId];
            if (m.Version != expectedVersion) throw new ConcurrencyException(matchId);
            _store[matchId] = m with { Actions = [.. m.Actions, action], Version = m.Version + 1, TurnDeadline = nextDeadline };
            return Task.FromResult(m.Version + 1);
        }
    }

    public Task MarkEndedAsync(Guid matchId, CancellationToken ct)
    {
        lock (_gate) _store[matchId] = _store[matchId] with { Ended = true, TurnDeadline = null };
        return Task.CompletedTask;
    }

    public async Task<StoredDailyMatch?> LoadAsync(Guid matchId, CancellationToken ct)
    {
        Interlocked.Increment(ref LoadCount);
        if (LoadGate is { } g) await g.Task;
        lock (_gate) return _store.GetValueOrDefault(matchId);
    }

    public Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Guid>>([.. _store.Values.Where(m => !m.Ended && m.TurnDeadline <= now).Select(m => m.Setup.MatchId)]);
    }

    public Task<IReadOnlyList<StoredDailyMatch>> ListActiveForUserAsync(Guid userId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<StoredDailyMatch>>([.. _store.Values.Where(m => !m.Ended && (m.Setup.SeatA.UserId == userId || m.Setup.SeatB.UserId == userId))]);
    }
}

public sealed class RecordingObserver : IMatchObserver
{
    public ConcurrentQueue<MatchEventEnvelope> Events { get; } = new();
    public ValueTask OnEventAsync(MatchEventEnvelope envelope, CancellationToken ct) { Events.Enqueue(envelope); return ValueTask.CompletedTask; }
}

public sealed class FakeBotPool : IBotPool
{
    public bool Available { get; set; } = true;
    public static readonly Guid BotId = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    public ConcurrentQueue<(Guid Bot, Guid Match)> Notified { get; } = new();
    public bool TryReserve(TimeControl timeControl, int rating, [NotNullWhen(true)] out BotReservation? reservation)
    {
        reservation = Available ? new BotReservation(BotId, "dev_bot") : null;
        return reservation is not null;
    }
    public void Release(Guid botUserId) { }
    public ValueTask NotifyMatchCreatedAsync(Guid botUserId, Guid matchId, CancellationToken ct) { Notified.Enqueue((botUserId, matchId)); return ValueTask.CompletedTask; }
}

/// <summary>Builds a gateway over fakes with a FakeTimeProvider.</summary>
public sealed class Harness
{
    public static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    public static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    public TttOptions Options { get; } = new();
    public FakeBroadcaster Broadcaster { get; } = new();
    public RecordingNotifier Notifier { get; } = new();
    public FakeSink Sink { get; } = new();
    public FakePush Push { get; } = new();
    public UserActivityRegistry Activity { get; } = new();
    public RefRules Rules { get; } = new();
    public FakeDailyRepo Daily { get; }
    public MatchGateway Gateway { get; }

    public Harness(bool withDailyRepo = false)
    {
        Daily = new FakeDailyRepo(Time);
        var rt = new MatchRuntime(Time, Rules, new RefClock(), Options, Broadcaster, Sink, Notifier, Push, new FakeCards(), Activity,
            NullLoggerFactory.Instance, withDailyRepo ? Daily : null);
        Gateway = new MatchGateway(rt);
    }

    public MatchSetup Setup(TimeControl tc, RuleSet rules, bool rated = false, ulong seed = 42) => new(Guid.NewGuid(), MatchModes.ModeFor(tc), tc, rules, rated,
        new SeatSetup(A, "alice", AccountKind.Account), new SeatSetup(B, "bob", AccountKind.Account), seed, MatchOrigin.Queue, null, null);

    public Task<Guid> CreateAsync(TimeControl tc, RuleSet? rules = null, bool rated = false, ulong seed = 42)
        => Gateway.CreateMatchAsync(Setup(tc, rules ?? RuleSet.Default, rated, seed), CancellationToken.None);

    public Task<MoveAckDto> PlaceAsync(Guid id, Guid user, int ply, int cell, Guid? actionId = null)
        => Gateway.SubmitActionAsync(id, user, ply, GameAction.Place(cell), actionId ?? Guid.NewGuid(), CancellationToken.None);

    public async Task<MatchSnapshotDto> SnapAsync(Guid id, Guid? viewer = null)
        => (await Gateway.GetSnapshotAsync(id, viewer, CancellationToken.None))!;

    public Task FlushAsync(Guid id) => Gateway.FlushAsync(id);

    public static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe) where T : class
    {
        for (var i = 0; i < 200; i++)
        {
            if (await probe() is { } v) return v;
            await Task.Delay(10);
        }
        throw new TimeoutException("condition not met");
    }
}
