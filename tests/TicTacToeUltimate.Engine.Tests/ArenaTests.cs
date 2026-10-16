using TicTacToeUltimate.Engine.Arena;

namespace TicTacToeUltimate.Engine.Tests;

/// <summary>Arena (health) rules. Mirrors tools/arena-sim/sim/test_arena.py (ArenaRules, VariantRules) plus the clock rules of ruleset C.</summary>
public class ArenaTests
{
    /// <summary>The simulator's default Rules(): 100 HP, 25→35→50, crit +15, consecutive combo, no red-alert multiplier.</summary>
    // These cases were derived on the legacy 7-piece ghost board (tools/arena-sim default) and stay as regression tests for it.
    private static readonly ArenaRules Draft = new() { Board = RuleSet.LegacyFifo7Ghost, Hp = 100, Combo = ComboKind.Consecutive, RedAlertMultiplier = 1, Id = "draft" };
    private static readonly ArenaRules C = ArenaRules.RulesetC with { Board = RuleSet.LegacyFifo7Ghost };

    private static ArenaRules W(ArenaRules r, int window) => r with { Board = r.Board with { Window = window } };

    private static ArenaState Run(ArenaRules r, string cells, int thinkMs = 1000, ArenaState? from = null)
    {
        var s = from ?? ArenaEngine.Initial(r);
        foreach (var c in Notation.Parse(cells))
        {
            Assert.Contains(c.Cell, ArenaEngine.LegalCells(s));
            var step = ArenaEngine.Apply(s, c.Cell, thinkMs);
            Assert.True(step.Ok, step.Error.ToString());
            s = step.State;
        }
        return s;
    }

    private static ArenaStep Step(ArenaState s, int cell, int thinkMs = 1000)
    {
        var step = ArenaEngine.Apply(s, cell, thinkMs);
        Assert.True(step.Ok, step.Error.ToString());
        return step;
    }

    /// <summary>Sim-style position: X cells, O cells, queue (oldest first).</summary>
    private static ArenaState Pos(ArenaRules r, int[] x, int[] queue, Color toMove, int hpX = 100, int hpO = 100, int stX = 0, int stO = 0)
        => ArenaEngine.FromPosition(r, queue.Select(c => (c, x.Contains(c) ? Color.X : Color.O)), toMove, hpX, hpO, stX, stO);

    private static int[] Queue(ArenaState s) => [.. s.Pieces.Select(p => p.Cell)];

    // ---------------------------------------------------------------- base rules unchanged (ghost on)

    [Fact]
    public void BaseCases_MatchClassicWhileNoLine()
    {
        var s = Run(C, "4,0,8,2,6,3,5");
        Assert.Equal("O.OOXXX.X", ArenaEngine.Board(s));
        Assert.Equal(4, ArenaEngine.NextToVanish(s));
        Assert.Equal(new[] { 1, 4, 7 }, ArenaEngine.LegalCells(s));

        s = Run(C, "4,0,8,2,6,3,5,1");          // ghost blocks O's 0-1-2
        Assert.Null(s.LastHit);
        Assert.Equal("OOOO.XX.X", ArenaEngine.Board(s));
        Assert.Equal((125, 125), (s.HpX, s.HpO));

        s = Run(C, "4,0,8,2,6,3,5,1,7");        // ghost blocks X's 6-7-8
        Assert.Null(s.LastHit);
        Assert.Equal(".OOO.XXXX", ArenaEngine.Board(s));
        Assert.Equal(new[] { 0, 4, 8 }, ArenaEngine.LegalCells(s));
    }

    [Fact]
    public void GhostOff_Counts()
    {
        var r = Draft with { Board = Draft.Board with { VanishingPieceCanWin = true } };
        var s = Run(r, "4,0,8,2,6,3,5,1");
        Assert.Equal(1, s.LastHit!.Lines);
        Assert.Equal(new[] { 0, 1, 2 }, s.LastHit.Shattered);
        Assert.Equal(75, s.HpX);
    }

    [Fact]
    public void VacatedCellReplayable()
    {
        var s = Run(C, "4,0,8,2,6,3,5,4");
        Assert.Equal("O.OOOXX.X", ArenaEngine.Board(s));
        Assert.Equal(new[] { 0, 1, 7 }, ArenaEngine.LegalCells(s));
    }

    // ---------------------------------------------------------------- shatter

    [Fact]
    public void Shatter_RemovesFromBoardAndQueue()
    {
        var s = Run(Draft, "0,4,1,8,2");
        Assert.Equal(1, s.LastHit!.Lines);
        Assert.Equal(25, s.LastHit.Damage);
        Assert.Equal("....O...O", ArenaEngine.Board(s));
        Assert.Equal(new[] { 4, 8 }, Queue(s));
        Assert.Equal((100, 75), (s.HpX, s.HpO));
        Assert.Equal(1, s.StreakX);
        Assert.Equal(Color.O, s.ToMove);
        Assert.Equal(ArenaPhase.Round, s.Phase);
    }

    [Fact]
    public void Shatter_KeepsOrderMidQueue()
    {
        var s = Run(W(Draft, 9), "3,0,6,1,4,8,5");
        Assert.Equal(1, s.LastHit!.Lines);
        Assert.Equal(new[] { 0, 6, 1, 8 }, Queue(s));
    }

    [Fact]
    public void QueueNotFullAfterShatter_NoVanish()
    {
        var s = Run(Draft, "0,4,1,8,2");
        var step = Step(s, 3);
        Assert.Null(step.VanishedCell);
        Assert.Equal(new[] { 4, 8, 3 }, Queue(step.State));
    }

    // ---------------------------------------------------------------- combo

    [Fact]
    public void ConsecutiveCombo_RampsAndResets()
    {
        var r = W(Draft, 9);
        var s = Pos(r, [0, 1, 3, 4], [0, 6, 1, 8, 3, 4], Color.X);
        var st = Step(s, 2);
        Assert.Equal((1, 25, 1, 75), (st.Hit!.Lines, st.Hit.Damage, st.State.StreakX, st.State.HpO));
        st = Step(st.State, 7);                         // O 6-7-8
        Assert.Equal((1, 25, 1, 75), (st.Hit!.Lines, st.Hit.Damage, st.State.StreakO, st.State.HpX));
        st = Step(st.State, 5);                         // X 3-4-5: second consecutive scoring turn
        Assert.Equal((1, 35, 2, 40), (st.Hit!.Lines, st.Hit.Damage, st.State.StreakX, st.State.HpO));
        st = Step(st.State, 0);                         // O blank -> O streak resets
        Assert.Null(st.Hit);
        Assert.Equal(0, st.State.StreakO);
        st = Step(st.State, 1);                         // X blank -> resets
        Assert.Equal(0, st.State.StreakX);
    }

    [Fact]
    public void ComboRamp_Caps()
    {
        var r = Draft with { Ramp = [10, 20, 30] };
        var s = Pos(r, [0, 1], [0, 1], Color.X, stX: 5);
        var st = Step(s, 2);
        Assert.Equal((30, 6), (st.Hit!.Damage, st.State.StreakX));
    }

    [Fact]
    public void UnansweredCombo_SurvivesOwnBlankTurns()
    {
        var r = W(Draft with { Combo = ComboKind.Unanswered }, 9);
        var s = Pos(r, [0, 1, 3, 4], [0, 7, 1, 3, 4], Color.X);
        var st = Step(s, 2);
        Assert.Equal((25, 1), (st.Hit!.Damage, st.State.StreakX));
        st = Step(st.State, 8);                         // O blank
        st = Step(st.State, 6);                         // X blank: does NOT reset
        Assert.Equal(1, st.State.StreakX);
        st = Step(st.State, 0);                         // O blank
        st = Step(st.State, 5);                         // X 3-4-5: unanswered 2nd line
        Assert.Equal((1, 35, 2), (st.Hit!.Lines, st.Hit.Damage, st.State.StreakX));
    }

    [Fact]
    public void UnansweredCombo_ResetByOpponentLine()
    {
        var r = W(Draft with { Combo = ComboKind.Unanswered }, 9);
        var s = Pos(r, [0, 1], [0, 6, 1, 7], Color.O, stX: 3);
        var st = Step(s, 8);
        Assert.Equal((1, 0, 1), (st.Hit!.Lines, st.State.StreakX, st.State.StreakO));
        Assert.Equal(3, st.Hit.BrokenStreak);
    }

    // ---------------------------------------------------------------- crit + ghost

    [Fact]
    public void Crit_DoubleLine()
    {
        var r = W(Draft, 9);
        var s = Pos(r, [0, 1, 4, 6], [0, 3, 1, 5, 4, 6], Color.X);
        var st = Step(s, 2);
        Assert.Equal(2, st.Hit!.Lines);
        Assert.True(st.Hit.Crit);
        Assert.Equal(2 * 25 + 15, st.Hit.Damage);
        Assert.Equal(new[] { 0, 1, 2, 4, 6 }, st.Hit.Shattered);
        Assert.Equal(new[] { 3, 5 }, Queue(st.State));
        Assert.Equal(35, st.State.HpO);
    }

    [Fact]
    public void Ghost_BlocksOneLineOfCrit()
    {
        var s = Pos(Draft, [0, 1, 4, 6], [0, 3, 1, 5, 4, 6], Color.X);
        var st = Step(s, 2);
        Assert.Equal(1, st.Hit!.Lines);
        Assert.Equal(new[] { 2, 4, 6 }, st.Hit.Shattered);
    }

    [Fact]
    public void Invariant_NoCountedLineLeftAfterMove()
    {
        ArenaRules[] configs = [Draft, Draft with { Board = Draft.Board with { VanishingPieceCanWin = true } }, W(Draft, 9), C,
            ArenaRules.RulesetB with { Board = RuleSet.LegacyFifo7Ghost }, ArenaRules.RulesetC, ArenaRules.RulesetB];
        foreach (var cfg in configs)
        {
            var rules = cfg with { Hp = 1_000_000, RoundMs = int.MaxValue };
            var lines = LineTable.For(3, 3);
            for (ulong g = 0; g < 200; g++)
            {
                var rng = new SplitMix64(g + 1);
                var s = ArenaEngine.Initial(rules);
                for (int ply = 0; ply < 60; ply++)
                {
                    var legal = ArenaEngine.LegalCells(s);
                    var mover = s.ToMove;
                    s = ArenaEngine.Apply(s, legal[rng.NextInt(legal.Length)], 1000).State;
                    int ghost = -1;
                    if (!rules.Board.VanishingPieceCanWin && s.Pieces.Length == rules.Board.Window && s.Pieces[0].Color == mover) ghost = s.Pieces[0].Cell;
                    var mine = s.Pieces.Where(p => p.Color == mover && p.Cell != ghost).Select(p => p.Cell).ToHashSet();
                    Assert.DoesNotContain(lines, l => l.All(mine.Contains));
                    Assert.Equal(s.Pieces.Length, s.Pieces.Select(p => p.Cell).Distinct().Count());
                    Assert.True(s.Pieces.Length <= (rules.Board.Removal == RemovalKind.PerPlayerCap ? 2 * rules.Board.Window : rules.Board.Window));
                }
            }
        }
    }

    // ---------------------------------------------------------------- ruleset C clock rules

    [Fact]
    public void RulesetC_And_B_AreNamed()
    {
        Assert.True(ArenaRules.TryParse("arena-c", out var c));
        Assert.Equal((125, 2, 15, ComboKind.Unanswered, 120_000, 30_000, 5_000), (c.Hp, c.RedAlertMultiplier, c.CritBonus, c.Combo, c.RoundMs, c.RedAlertMs, c.MoveMs));
        Assert.Equal(new[] { 25, 35, 50 }, c.Ramp);
        Assert.Equal(RuleSet.Default, c.Board);          // 3 pieces each, no ghost (owner 2026-10-15)
        Assert.Equal(RuleSet.Default, ArenaRules.RulesetB.Board);
        Assert.Equal(OvertimeStarter.Conceder, c.OvertimeStarter);
        Assert.True(ArenaRules.TryParse("arena-b", out var b));
        Assert.Equal((100, 1, ComboKind.Unanswered), (b.Hp, b.RedAlertMultiplier, b.Combo));
        Assert.Empty(c.Validate());
        Assert.Empty(b.Validate());
    }

    [Fact]
    public void RedAlert_DoublesDamage_InLast30s()
    {
        var early = Run(C, "0,4,1,8,2", 17_000);          // elapsed 85s after the line
        Assert.Equal(25, early.LastHit!.Damage);
        Assert.False(early.LastHit.RedAlert);
        var late = Run(C, "0,4,1,8,2", 18_000);           // elapsed 90s: red alert
        Assert.Equal(50, late.LastHit!.Damage);
        Assert.True(late.LastHit.RedAlert);
        Assert.Equal(75, late.HpO);
    }

    [Fact]
    public void RedAlert_DoublesCritToo()
    {
        var r = W(C, 9);
        var s = Pos(r, [0, 1, 4, 6], [0, 3, 1, 5, 4, 6], Color.X, 125, 125) with { ElapsedMs = 95_000 };
        var st = Step(s, 2);
        Assert.Equal(2 * (2 * 25 + 15), st.Hit!.Damage);
    }

    [Fact]
    public void TimeOut_OnlyAfterOMoves_EqualTurns()
    {
        var s = Run(C, "0,4,8,2,6", 25_000);              // X's 3rd move ends at 125s: O still gets its turn
        Assert.Equal(ArenaPhase.Round, s.Phase);
        Assert.Equal(Color.O, s.ToMove);
        s = Step(s, 3, 1000).State;                        // O moves: equal turns, HP level -> overtime
        Assert.Equal(s.MovesX, s.MovesO);
        Assert.Equal(ArenaPhase.Overtime, s.Phase);
        Assert.Equal(Color.X, s.ToMove);                   // nobody conceded a line: X starts
    }

    [Fact]
    public void TimeOut_HigherHpWins()
    {
        var s = Run(C, "0,3,1,4,2,8", 20_000);
        Assert.Equal(ArenaEnd.Decision, s.End);
        Assert.Equal(ArenaPhase.Ended, s.Phase);
        Assert.Equal(Color.X, s.Winner);
        Assert.Equal(75, s.HpO);                           // the line at 100s was in red alert
    }

    [Fact]
    public void Knockout_EndsAtOnce()
    {
        var s = Run(C with { Hp = 25 }, "0,4,1,8,2");
        Assert.Equal(ArenaEnd.Knockout, s.End);
        Assert.Equal(Color.X, s.Winner);
        Assert.Empty(ArenaEngine.LegalCells(s));
        Assert.Equal(MoveError.GameOver, ArenaEngine.Apply(s, 3, 1000).Error);
    }

    [Fact]
    public void Knockout_CheckedBeforeTimeOut()
    {
        var s = Run(C with { Hp = 50 }, "0,3,1,4,8,5", 20_000);   // O's line lands exactly at 2:00 (red alert, 50 dmg)
        Assert.Equal(ArenaEnd.Knockout, s.End);
        Assert.Equal(Color.O, s.Winner);
    }

    [Fact]
    public void Overtime_ConcederMovesFirst_NextLineWins()
    {
        // O scores at 90s (red: X 75), X answers at 105s (red: O 75); O's blank move at 120s ends the round level.
        var s = Run(C, "0,3,1,4,8,5,2,7", 15_000);
        Assert.Equal((75, 75), (s.HpX, s.HpO));
        Assert.Equal(ArenaPhase.Overtime, s.Phase);
        Assert.Equal(Color.X, s.LastScorer);
        Assert.Equal(Color.O, s.ToMove);                    // O conceded the last line -> O starts overtime
        Assert.Equal(0, ArenaEngine.NextLineDamage(s, Color.O));

        s = Run(C, "3,0,4,1,5", 1000, s);                   // first line in overtime wins
        Assert.Equal(ArenaEnd.Overtime, s.End);
        Assert.Equal(Color.O, s.Winner);
        Assert.True(s.LastHit!.Overtime);
    }

    [Fact]
    public void Overtime_NextInTurnOption_StartsWithX()
    {
        var s = Run(C with { OvertimeStarter = OvertimeStarter.NextInTurn }, "0,3,1,4,8,5,2,7", 15_000);
        Assert.Equal(ArenaPhase.Overtime, s.Phase);
        Assert.Equal(Color.X, s.ToMove);
    }

    [Fact]
    public void NextLineDamage_PreviewsComboAndRedAlert()
    {
        var s = Run(C, "0,4,1,8,2");                         // X streak 1
        Assert.Equal(35, ArenaEngine.NextLineDamage(s, Color.X));
        Assert.Equal(25, ArenaEngine.NextLineDamage(s, Color.O));
        Assert.Equal(70, ArenaEngine.NextLineDamage(s with { ElapsedMs = 95_000 }, Color.X));
    }

    [Fact]
    public void Replay_FoldsApply()
    {
        var s = ArenaEngine.Replay(C, [(0, 1000), (4, 1000), (1, 1000), (8, 1000), (2, 1000)]);
        Assert.Equal(100, s.HpO);
        Assert.Throws<InvalidActionException>(() => ArenaEngine.Replay(C, [(4, 0), (4, 0)]));
    }
}

/// <summary>Arena on the default "3 pieces each" board.</summary>
public class ArenaCap3Tests
{
    private static ArenaState Play(ArenaRules r, params int[] cells)
    {
        var s = ArenaEngine.Initial(r);
        foreach (var c in cells)
        {
            var st = ArenaEngine.Apply(s, c, 1000);
            Assert.True(st.Ok, $"cell {c}: {st.Error}");
            s = st.State;
        }
        return s;
    }

    [Fact]
    public void Visible_line_scores_and_shatters_all_three_pieces()
    {
        var s = Play(ArenaRules.RulesetC, 0, 3, 1, 4, 2);
        Assert.Equal(25, s.LastHit!.Damage);
        Assert.Equal(new[] { 0, 1, 2 }, s.LastHit.Shattered);
        Assert.DoesNotContain(s.Pieces, p => p.Color == Color.X);   // the scorer rebuilds from zero
        Assert.Equal(2, s.Pieces.Count(p => p.Color == Color.O));
    }

    [Fact]
    public void Fourth_piece_removes_own_oldest_and_blocks_that_cell()
    {
        var s = Play(ArenaRules.RulesetC, 0, 4, 1, 8, 5, 3);   // X: 0,1,5  O: 4,8,3 (no lines)
        Assert.Equal(0, ArenaEngine.NextToVanish(s));
        Assert.DoesNotContain(0, ArenaEngine.LegalCells(s));
        Assert.Equal(MoveError.CellJustVacated, ArenaEngine.Apply(s, 0, 1000).Error);
        var after = ArenaEngine.Apply(s, 6, 1000).State;        // X places 6, X's 0 vanishes
        Assert.DoesNotContain(after.Pieces, p => p.Cell == 0);
        Assert.Equal(3, after.Pieces.Count(p => p.Color == Color.X));
    }

    [Fact]
    public void Crit_is_impossible_with_three_pieces()
    {
        var rng = new Random(11);
        for (int g = 0; g < 400; g++)
        {
            var s = ArenaEngine.Initial(ArenaRules.RulesetC);
            for (int ply = 0; ply < 80 && !s.IsOver; ply++)
            {
                var legal = ArenaEngine.LegalCells(s);
                var st = ArenaEngine.Apply(s, legal[rng.Next(legal.Length)], 1500);
                Assert.False(st.Hit?.Crit ?? false);
                s = st.State;
            }
        }
    }
}

/// <summary>spec/arena/vectors.json (generated by RulesTool, cross-checked against tools/arena-sim) conformance.</summary>
public class ArenaVectorTests
{
    [Fact]
    public void ArenaVectorsConform()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(TestSupport.SpecPath("arena", "vectors.json")));
        int checkedCases = 0;
        foreach (var rs in doc.RootElement.GetProperty("rulesets").EnumerateArray())
        {
            Assert.True(ArenaRules.TryParse(rs.GetProperty("id").GetString(), out var r));
            Assert.Equal(r.Board.Id, rs.GetProperty("board").GetString());
            Assert.Equal(r.Hp, rs.GetProperty("hp").GetInt32());
            Assert.Equal(r.RedAlertMultiplier, rs.GetProperty("redAlertMultiplier").GetInt32());
            foreach (var c in rs.GetProperty("cases").EnumerateArray().Concat(rs.GetProperty("playouts").EnumerateArray()))
            {
                var name = c.GetProperty("name").GetString();
                var cells = c.GetProperty("cells").GetString()!.Split(',').Select(int.Parse).ToArray();
                var thinks = c.GetProperty("thinkMs").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                var moves = cells.Zip(thinks);
                if (c.TryGetProperty("error", out var err))
                {
                    var ex = Assert.Throws<InvalidActionException>(() => ArenaEngine.Replay(r, moves));
                    Assert.Equal(err.GetProperty("atIndex").GetInt32(), ex.Index);
                    continue;
                }
                var s = ArenaEngine.Replay(r, moves);
                var e = c.GetProperty("expect");
                Assert.True(e.GetProperty("board").GetString() == ArenaEngine.Board(s), name);
                Assert.Equal(e.GetProperty("queue").EnumerateArray().Select(x => x.GetInt32()), s.Pieces.Select(p => p.Cell));
                Assert.Equal(e.GetProperty("hp").EnumerateArray().Select(x => x.GetInt32()), new[] { s.HpX, s.HpO });
                Assert.Equal(e.GetProperty("streak").EnumerateArray().Select(x => x.GetInt32()), new[] { s.StreakX, s.StreakO });
                Assert.Equal(e.GetProperty("lines").EnumerateArray().Select(x => x.GetInt32()), new[] { s.LinesX, s.LinesO });
                Assert.Equal(e.GetProperty("elapsedMs").GetInt32(), s.ElapsedMs);
                Assert.Equal(e.GetProperty("phase").GetString(), s.Phase.ToString());
                Assert.Equal(e.GetProperty("end").GetString(), s.End.ToString());
                Assert.Equal(e.GetProperty("winner").ValueKind == System.Text.Json.JsonValueKind.Null ? null : e.GetProperty("winner").GetString(), s.Winner?.ToString());
                Assert.Equal(e.GetProperty("toMove").GetString(), s.ToMove.ToString());
                Assert.Equal(e.GetProperty("legal").EnumerateArray().Select(x => x.GetInt32()), ArenaEngine.LegalCells(s));
                checkedCases++;
            }
        }
        Assert.True(checkedCases >= 200);
    }
}
