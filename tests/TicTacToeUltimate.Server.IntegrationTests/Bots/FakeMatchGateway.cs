using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.IntegrationTests.Bots;

/// <summary>
/// In-file fake of WP2's IMatchGateway: plain 3x3 placement (no removal), seat 0 = X, seat 1 = O.
/// Events are delivered to observers synchronously, before SubmitActionAsync returns (worst case for ack/state ordering).
/// </summary>
internal sealed class FakeMatchGateway : IMatchGateway
{
    private static readonly int[][] Lines = [[0, 1, 2], [3, 4, 5], [6, 7, 8], [0, 3, 6], [1, 4, 7], [2, 5, 8], [0, 4, 8], [2, 4, 6]];
    private readonly Dictionary<Guid, FakeMatch> _matches = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    private sealed class FakeMatch(MatchSetup setup)
    {
        public MatchSetup Setup { get; } = setup;
        public char[] Board { get; } = ".........".ToCharArray();
        public List<ActionDto> Actions { get; } = [];
        public long Seq { get; set; }
        public int Ply { get; set; }
        public MatchResultDto? Result { get; set; }
        public Dictionary<Guid, IMatchObserver> Observers { get; } = [];
        public int ToMoveSeat => Ply % 2;
        public int[] Legal => Result is null ? Enumerable.Range(0, 9).Where(i => Board[i] == '.').ToArray() : [];
    }

    public static MatchSetup Setup(Guid humanId, Guid botId) => new(Guid.NewGuid(), MatchMode.Casual, TimeControl.CasualX2,
        RuleSet.Default, false, new SeatSetup(humanId, "human", AccountKind.Account), new SeatSetup(botId, "dev_bot", AccountKind.Bot),
        1, MatchOrigin.BotFallback, null, null);

    public Task<Guid> CreateMatchAsync(MatchSetup setup, CancellationToken ct)
    {
        _matches[setup.MatchId] = new FakeMatch(setup);
        return Task.FromResult(setup.MatchId);
    }

    public async Task<AttachResult> AttachAsync(Guid matchId, Guid userId, IMatchObserver observer, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_matches.TryGetValue(matchId, out var m)) throw new MatchAccessException(ErrorCodes.MatchNotFound);
            var seat = SeatOf(m, userId) ?? throw new MatchAccessException(ErrorCodes.NotAParticipant);
            m.Observers[userId] = observer;
            return new AttachResult(Snapshot(m, seat), new Detach(() => m.Observers.Remove(userId)));
        }
        finally { _lock.Release(); }
    }

    private sealed class Detach(Action a) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { a(); return ValueTask.CompletedTask; }
    }

    private static int? SeatOf(FakeMatch m, Guid userId) =>
        m.Setup.SeatA.UserId == userId ? 0 : m.Setup.SeatB.UserId == userId ? 1 : null;

    private static PlayerCardDto Card(SeatSetup s) => new(s.UserId, s.DisplayName, "a1", null, s.Kind, null, null, false);

    private static MatchSnapshotDto Snapshot(FakeMatch m, int? seat) => new(m.Setup.MatchId, m.Seq, m.Setup.Mode, m.Setup.TimeControl,
        false, RuleSetDto.From(m.Setup.Rules),
        [new SeatDto(0, Card(m.Setup.SeatA), Color.X, true, null), new SeatDto(1, Card(m.Setup.SeatB), Color.O, true, null)],
        false, [.. m.Actions], new string(m.Board), m.Ply, m.Result is null ? GamePhase.AwaitingPlacement : GamePhase.Ended,
        (Color)(m.Ply % 2), m.Result is null ? m.ToMoveSeat : null, m.Legal, null, null, m.Result, seat, 0, false, 0);

    public async Task<MatchSnapshotDto?> GetSnapshotAsync(Guid matchId, Guid? viewerUserId, CancellationToken ct)
    {
        await Task.Yield();
        return _matches.TryGetValue(matchId, out var m) ? Snapshot(m, viewerUserId is { } v ? SeatOf(m, v) : null) : null;
    }

    public async Task<MoveAckDto> SubmitActionAsync(Guid matchId, Guid userId, int expectedPly, GameAction action, Guid actionId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_matches.TryGetValue(matchId, out var m)) throw new MatchAccessException(ErrorCodes.MatchNotFound);
            var seat = SeatOf(m, userId) ?? throw new MatchAccessException(ErrorCodes.NotAParticipant);
            if (m.Result is not null) return new MoveAckDto(false, ErrorCodes.MatchOver, m.Seq, m.Ply);
            if (expectedPly != m.Ply) return new MoveAckDto(false, ErrorCodes.StalePly, m.Seq, m.Ply);
            if (seat != m.ToMoveSeat) return new MoveAckDto(false, ErrorCodes.NotYourTurn, m.Seq, m.Ply);
            if (action.Kind != ActionKind.Place) return new MoveAckDto(false, ErrorCodes.WrongActionKind, m.Seq, m.Ply);
            if (m.Board[action.Cell] != '.') return new MoveAckDto(false, ErrorCodes.Occupied, m.Seq, m.Ply);

            var color = (Color)seat;
            m.Board[action.Cell] = color == Color.X ? 'X' : 'O';
            var dto = new ActionDto(m.Ply, seat, ActionKind.Place, action.Cell, color, null, 0, false);
            m.Actions.Add(dto);
            m.Ply++;
            var events = new List<MatchEvent> { new ActionApplied(dto, new string(m.Board), null, false, []) };
            var line = Lines.FirstOrDefault(l => l.All(i => m.Board[i] == m.Board[action.Cell]));
            if (line is not null)
            {
                m.Result = new MatchResultDto(color == Color.X ? Outcome.XWins : Outcome.OWins, seat, EndReason.Line, line, false, null);
                events.Add(new MatchEnded(m.Result));
            }
            else
            {
                events.Add(new TurnStarted(m.Ply, (Color)(m.Ply % 2), m.ToMoveSeat, GamePhase.AwaitingPlacement, null, m.Legal));
            }
            foreach (var e in events)
            {
                m.Seq++;
                var env = new MatchEventEnvelope(matchId, m.Seq, 0, e);
                foreach (var o in m.Observers.Values.ToArray()) await o.OnEventAsync(env, ct);
            }
            return new MoveAckDto(true, null, m.Seq, m.Ply);
        }
        finally { _lock.Release(); }
    }

    public Task<CommandAckDto> ResignAsync(Guid matchId, Guid userId, CancellationToken ct) =>
        Task.FromResult(new CommandAckDto(false, ErrorCodes.Internal));

    public IReadOnlyList<Guid> ActiveMatchesFor(Guid userId) => [];
}
