using static TicTacToeUltimate.Engine.Tests.TestSupport;

namespace TicTacToeUltimate.Engine.Tests;

public class RulesTests
{
    /// <summary>Legacy FIFO-7 ghost rule (still the Arena board); the launch default is covered by the Default* tests below.</summary>
    private static readonly RuleSet D = RuleSet.LegacyFifo7Ghost;
    private static readonly RuleSet Legacy = new(VanishingPieceCanWin: true);

    [Fact]
    public void Case_WindowFull_OToMove()
    {
        var s = Play(D, "4,0,8,2,6,3,5");
        Assert.Equal(GamePhase.AwaitingPlacement, s.Phase);
        Assert.Equal(Color.O, s.ToMove);
        Assert.Equal(7, s.Ply);
        Assert.Equal("O.OOXXX.X", Notation.Board(s));
        Assert.Equal(4, Rules.NextToVanish(s));
        Assert.Equal(new[] { 1, 4, 7 }, Rules.LegalCells(s));
    }

    [Fact]
    public void Case_OwnVanishingPieceDoesNotCount()
    {
        var s = Play(D, "4,0,8,2,6,3,5,1");
        Assert.Equal(Outcome.None, s.Outcome);
        Assert.Equal(Color.X, s.ToMove);
        Assert.Equal(8, s.Ply);
        Assert.Equal("OOOO.XX.X", Notation.Board(s));
        Assert.Equal(0, Rules.NextToVanish(s));
        Assert.Equal(new[] { 0, 4, 7 }, Rules.LegalCells(s));
        Assert.Equal(4, s.LastVanishedCell);

        var legacy = Play(Legacy, "4,0,8,2,6,3,5,1");
        Assert.Equal(Outcome.OWins, legacy.Outcome);
        Assert.Equal(new[] { 0, 1, 2 }, legacy.WinLine);
    }

    [Fact]
    public void Case_GhostExcludedForX()
    {
        var s = Play(D, "4,0,8,2,6,3,5,1,7");
        Assert.Equal(Outcome.None, s.Outcome);
        Assert.Equal(Color.O, s.ToMove);
        Assert.Equal(9, s.Ply);
        Assert.Equal(".OOO.XXXX", Notation.Board(s));
        Assert.Equal(8, Rules.NextToVanish(s));
        Assert.Equal(new[] { 0, 4, 8 }, Rules.LegalCells(s));
    }

    [Fact]
    public void Case_EarlyWin()
    {
        var s = Play(D, "0,4,1,8,2");
        Assert.Equal(Outcome.XWins, s.Outcome);
        Assert.Equal(GamePhase.Ended, s.Phase);
        Assert.Equal(5, s.Ply);
        Assert.Equal(new[] { 0, 1, 2 }, s.WinLine);
    }

    [Fact]
    public void Case_WindowBoundaryAndVacatedReplay()
    {
        var s = Play(D, "4,0,8,2,6,3,5,7");
        Assert.Equal(Color.X, s.ToMove);
        Assert.Equal("O.OO.XXOX", Notation.Board(s));
        Assert.Equal(0, Rules.NextToVanish(s));
        Assert.Equal(new[] { 0, 1, 4 }, Rules.LegalCells(s));

        var v = Play(D, "4,0,8,2,6,3,5,4");
        Assert.Equal("O.OOOXX.X", Notation.Board(v));
        Assert.Equal(Color.X, v.ToMove);
        Assert.Equal(new[] { 0, 1, 7 }, Rules.LegalCells(v));

        var blocked = D with { VacatedCellPlayable = false };
        var ex = Assert.Throws<InvalidActionException>(() => Play(blocked, "4,0,8,2,6,3,5,4"));
        Assert.Equal(7, ex.Index);
        Assert.Equal(MoveError.CellJustVacated, ex.Error);
    }

    [Fact]
    public void Case_Occupied()
    {
        var ex = Assert.Throws<InvalidActionException>(() => Play(D, "4,4"));
        Assert.Equal(1, ex.Index);
        Assert.Equal(MoveError.Occupied, ex.Error);
    }

    [Fact]
    public void ErrorsAndPhases()
    {
        var s0 = Rules.Initial(D);
        Assert.Equal(MoveError.OutOfBounds, Rules.Apply(s0, GameAction.Place(9)).Error);
        Assert.Equal(MoveError.OutOfBounds, Rules.Apply(s0, GameAction.Place(-1)).Error);
        Assert.Equal(MoveError.WrongActionKind, Rules.Apply(s0, GameAction.Swap).Error);
        var won = Play(D, "0,4,1,8,2");
        Assert.Equal(MoveError.GameOver, Rules.Apply(won, GameAction.Place(3)).Error);
        Assert.Null(Rules.NextToVanish(won));

        var ban = D with { Opening = OpeningRule.BanCenter };
        Assert.Equal(MoveError.BannedOpening, Rules.Apply(Rules.Initial(ban), GameAction.Place(4)).Error);
        Assert.True(Rules.Apply(Play(ban, "0"), GameAction.Place(4)).Ok);
    }

    [Fact]
    public void PieSwapAndKeep()
    {
        var r = RuleSet.RankedDefault;
        var s = Play(r, "4");
        Assert.Equal(GamePhase.AwaitingSwapDecision, s.Phase);
        Assert.Equal(Color.O, s.ToMove);
        Assert.Empty(Rules.LegalCells(s));
        Assert.Equal(new[] { GameAction.Swap, GameAction.KeepSides }, Rules.LegalActions(s));
        Assert.Equal(MoveError.WrongActionKind, Rules.Apply(s, GameAction.Place(0)).Error);

        var swapped = Rules.Apply(s, GameAction.Swap).State;
        Assert.True(swapped.Swapped);
        Assert.Equal(Color.O, swapped.ToMove);
        Assert.Equal(2, swapped.Ply);
        Assert.Equal(1, swapped.Placements);
        Assert.Equal(GamePhase.AwaitingPlacement, swapped.Phase);
        Assert.Equal(MoveError.WrongActionKind, Rules.Apply(swapped, GameAction.Swap).Error);

        var kept = Rules.Apply(s, GameAction.KeepSides).State;
        Assert.False(kept.Swapped);
        Assert.Equal(Color.O, kept.ToMove);
        Assert.Equal("4,swap,0", Notation.Format(Notation.Parse("4,swap,0")));
    }

    [Fact]
    public void PerPlayerCapRemovesMoversOldest()
    {
        var r = new RuleSet(Removal: RemovalKind.PerPlayerCap, Window: 3, VanishingPieceCanWin: true);
        var s = Play(r, "0,4,1,5,8,6");
        Assert.Equal(0, Rules.NextToVanish(s));   // X holds 3: its oldest (0) goes
        var s2 = Rules.Apply(s, GameAction.Place(2)).State;
        Assert.Equal(0, s2.LastVanishedCell);
        Assert.Equal(4, Rules.NextToVanish(s2));  // O's oldest
    }

    [Fact]
    public void KLessThanNWins()
    {
        var r = new RuleSet(BoardSize: 4, WinLength: 3, Window: 8, VanishingPieceCanWin: true);
        var s = Play(r, "0,4,1,5,2");
        Assert.Equal(Outcome.XWins, s.Outcome);
        Assert.Equal(new[] { 0, 1, 2 }, s.WinLine);
    }

    [Fact]
    public void StuckBoardIsDraw()
    {
        var r = new RuleSet(Window: 9, VacatedCellPlayable: false, VanishingPieceCanWin: true);
        var s = Play(r, "0,1,2,4,3,5,7,6,8");
        Assert.Equal(Outcome.Draw, s.Outcome);
        Assert.Equal(GamePhase.Ended, s.Phase);
    }

    [Fact]
    public void ImmediateWinsAreVanishAware()
    {
        var s = Play(D, "0,4,1,8");
        Assert.Equal(new[] { 2 }, Rules.ImmediateWins(s, Color.X));
        // O holds 4,8: 0 is X's, so O's threat is none until... O can't win on X-occupied cells.
        Assert.DoesNotContain(0, Rules.ImmediateWins(s, Color.O));
        // Window full, X's own oldest becomes ghost after placing: X5,X6 + ghost cannot win.
        var g = Play(D, "4,0,8,2,6,3,5");
        Assert.Empty(Rules.ImmediateWins(g, Color.O));
        Assert.Empty(Rules.ImmediateWins(Play(Legacy, "4,0,8,2,6,3,5,1"), Color.X));
    }

    [Fact]
    public void ReplayEqualsStepwiseApply_IllegalDoesNotMutate_CapacityHolds()
    {
        foreach (var id in new[] { "n3k3-cap3-v0-w1-none", "n3k3-cap3-v0-w1-pie", "n3k3-fifo7-v1-w0-none", "n3k3-fifo7-v1-w0-pie", "n3k3-cap3-v1-w1-none", "n4k3-fifo8-v1-w1-none" })
        {
            var r = Parse(id);
            int capacity = r.Removal == RemovalKind.PerPlayerCap ? 2 * r.Window : r.Window;
            for (ulong seed = 1; seed <= 100; seed++)
            {
                var rng = new SplitMix64(seed);
                var s = Rules.Initial(r);
                var actions = new List<GameAction>();
                while (s.Phase != GamePhase.Ended && actions.Count < 40)
                {
                    var boardBefore = Notation.Board(s);
                    var bad = Rules.Apply(s, GameAction.Place(r.CellCount));
                    Assert.False(bad.Ok);
                    Assert.Same(s, bad.State);
                    Assert.Equal(boardBefore, Notation.Board(s));

                    var legal = Rules.LegalActions(s);
                    var a = legal[rng.NextInt(legal.Length)];
                    actions.Add(a);
                    s = Rules.Apply(s, a).State;
                    Assert.True(s.Pieces.Length <= capacity);
                }
                var replayed = Rules.Replay(r, actions);
                Assert.Equal(Notation.Board(s), Notation.Board(replayed));
                Assert.Equal(s.Ply, replayed.Ply);
                Assert.Equal(s.Outcome, replayed.Outcome);
                Assert.Equal(s.ToMove, replayed.ToMove);
                Assert.Equal(s.Pieces, replayed.Pieces);
            }
        }
    }

    [Fact]
    public void SplitMix64IsDeterministic()
    {
        var a = new SplitMix64(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, a.Next());
        var x = new SplitMix64(12345);
        var y = new SplitMix64(12345);
        for (int i = 0; i < 100; i++)
        {
            int v = x.NextInt(7);
            Assert.Equal(v, y.NextInt(7));
            Assert.InRange(v, 0, 6);
        }
    }

    [Theory]
    [InlineData("n3k3-fifo7-v1-w0-none")]
    [InlineData("n3k3-fifo7-v1-w0-pie")]
    [InlineData("n3k3-fifo7-v1-w1-ban")]
    [InlineData("n3k3-cap3-v0-w1-none")]
    [InlineData("n3k3-cap3-v0-w1-pie")]
    [InlineData("n4k3-fifo8-v1-w1-none")]
    [InlineData("n5k4-none-v1-w0-none")]
    public void RuleSetIdRoundTrips(string id)
    {
        Assert.True(RuleSet.TryParse(id, out var r));
        Assert.Equal(id, r.Id);
        Assert.Empty(r.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("n3k3-fifo7-v1-w0")]
    [InlineData("n3k3-fifo4-v1-w0-none")]
    [InlineData("n3k3-cap5-v1-w0-none")]
    [InlineData("n8k3-fifo7-v1-w0-none")]
    [InlineData("n3k3-fifo07-v1-w0-none")]
    [InlineData("n3k3-fifo7-v2-w0-none")]
    public void RuleSetParseRejectsInvalid(string id) => Assert.False(RuleSet.TryParse(id, out _));

    [Fact]
    public void DefaultIds()
    {
        Assert.Equal("n3k3-cap3-v0-w1-none", RuleSet.Default.Id);
        Assert.Equal("n3k3-cap3-v0-w1-pie", RuleSet.RankedDefault.Id);
        Assert.Equal("n3k3-fifo7-v1-w0-none", RuleSet.LegacyFifo7Ghost.Id);
        Assert.Equal(8, LineTable.For(3, 3).Length);
    }

    [Fact]
    public void Default_OwnOldestVanishesFirst_ThenVisibleLineWins()
    {
        // X: 8,0,1 (8 oldest); O: 3,7,6. X places 2: its oldest (8) vanishes first, then 0-1-2 wins.
        var s = Play(RuleSet.Default, "8,3,0,7,1,6");
        Assert.Equal(Color.X, s.ToMove);
        Assert.Equal(8, Rules.NextToVanish(s));
        Assert.Equal(new[] { 2 }, Rules.ImmediateWins(s, Color.X));
        var won = Rules.Apply(s, GameAction.Place(2));
        Assert.True(won.Ok);
        Assert.Equal(8, won.VanishedCell);
        Assert.Equal(Outcome.XWins, won.State.Outcome);
        Assert.Equal(new[] { 0, 1, 2 }, won.State.WinLine);
    }

    [Fact]
    public void Default_VisibleThreeInARowAlwaysWins()
    {
        // The playtest bug: under the ghost rule a visible line through the mover's oldest piece did not win.
        // Under the launch rule any line of the mover's pieces on the board after the placement wins.
        var r = RuleSet.Default;
        for (ulong seed = 1; seed <= 300; seed++)
        {
            var rng = new SplitMix64(seed);
            var s = Rules.Initial(r);
            while (s.Phase != GamePhase.Ended)
            {
                var legal = Rules.LegalActions(s);
                var mover = s.ToMove;
                var next = Rules.Apply(s, legal[rng.NextInt(legal.Length)]).State;
                bool visibleLine = LineTable.For(3, 3).Any(l => l.All(c => next.At(c) == mover));
                Assert.Equal(visibleLine, next.Outcome == (mover == Color.X ? Outcome.XWins : Outcome.OWins));
                Assert.True(next.Pieces.Count(p => p.Color == mover) <= 3);
                s = next;
                if (s.Ply > 60) break;
            }
        }
    }

    [Fact]
    public void Default_JustVacatedCellIsNotPlayable()
    {
        var s = Play(RuleSet.Default, "0,4,1,5,8,6");
        Assert.Equal(0, Rules.NextToVanish(s));
        Assert.DoesNotContain(0, Rules.LegalCells(s));
        Assert.Equal(MoveError.CellJustVacated, Rules.Apply(s, GameAction.Place(0)).Error);
        // Next turn the cell is free again for X.
        var s2 = Play(RuleSet.Default, "0,4,1,5,8,6,2,3");
        Assert.Contains(0, Rules.LegalCells(s2));
    }
}
