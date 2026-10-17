using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Analysis;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matchmaking;
using TicTacToeUltimate.Server.Moderation;

namespace TicTacToeUltimate.Server.Tests.Moderation;

public sealed class AntiCheatTests
{
    private static readonly Lazy<Tablebase> Table = new(() => Tablebase.Build(RuleSet.Default, 5_000_000, CancellationToken.None));

    /// <summary>Both sides follow the tablebase (an engine): every critical position is answered correctly.</summary>
    private static GameRecord PerfectGame(bool xBlundersFirst)
    {
        var tb = Table.Value;
        var s = Rules.Initial(RuleSet.Default);
        var stored = new List<StoredAction>();
        var rng = new Random(3);
        while (s.Phase != GamePhase.Ended && stored.Count < 60)
        {
            var moves = tb.EvaluateMoves(s);
            var pick = xBlundersFirst && stored.Count == 0 && moves.Any(m => m.Class == MoveClass.Blunder)
                ? moves.First(m => m.Class == MoveClass.Blunder)
                : moves.Where(m => m.Class == MoveClass.Best).ElementAt(rng.Next(moves.Count(m => m.Class == MoveClass.Best)));
            var seat = s.ToMove == Color.X ? 0 : 1;
            var res = Rules.Apply(s, pick.Action);
            stored.Add(new StoredAction(s.Ply, seat, pick.Action, 900, false, res.VanishedCell, DateTimeOffset.UnixEpoch));
            s = res.State;
        }
        var setup = new MatchSetup(Guid.NewGuid(), MatchMode.Casual, TimeControl.Casual, RuleSet.Default, false,
            new SeatSetup(Guid.NewGuid(), "a", AccountKind.Account), new SeatSetup(Guid.NewGuid(), "b", AccountKind.Account), 1, MatchOrigin.Queue, null, null);
        return new GameRecord(setup.MatchId, setup, false, stored, new MatchResultDto(s.Outcome, s.Outcome == Outcome.XWins ? 0 : 1, EndReason.Line, [], false, null),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []);
    }

    [Fact]
    public void Engine_play_is_perfect_in_every_critical_position()
    {
        var m = CheatScorer.Measure(Table.Value, PerfectGame(xBlundersFirst: false))!;
        Assert.True(m[0].Critical + m[1].Critical > 0);
        Assert.Equal(m[0].Critical, m[0].Accurate);
        Assert.Equal(m[1].Critical, m[1].Accurate);
    }

    [Fact]
    public void A_blunder_is_counted()
    {
        var m = CheatScorer.Measure(Table.Value, PerfectGame(xBlundersFirst: true))!;
        Assert.Equal(m[0].Critical - 1, m[0].Accurate);
    }

    [Fact]
    public void Shadow_players_never_meet_clean_humans()
    {
        var o = new MatchmakingOptions();
        var clean = new QueueEntry(Guid.NewGuid(), "c", AccountKind.Account, TimeControl.Casual, 1500, null, 0);
        var flagged = new QueueEntry(Guid.NewGuid(), "f", AccountKind.Account, TimeControl.Casual, 1500, null, 0) { Shadow = true };
        var flagged2 = new QueueEntry(Guid.NewGuid(), "g", AccountKind.Account, TimeControl.Casual, 1500, null, 0) { Shadow = true };
        Assert.False(Matchmaker.CanPair(clean, flagged, 60_000, o));
        Assert.True(Matchmaker.CanPair(flagged, flagged2, 60_000, o));
    }
}
