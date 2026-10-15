using System.Diagnostics;
using System.Text.Json;
using TicTacToeUltimate.Engine.Analysis;
using static TicTacToeUltimate.Engine.Tests.TestSupport;

namespace TicTacToeUltimate.Engine.Tests;

public class TablebaseTests
{
    public static TheoryData<string> SummaryIds()
    {
        var data = new TheoryData<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(SpecPath("rules", "summary.json")));
        foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray()) data.Add(row.GetProperty("rulesId").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(SummaryIds))]
    public void StudyTableIsReproduced(string rulesId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SpecPath("rules", "summary.json")));
        var row = doc.RootElement.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("rulesId").GetString() == rulesId);
        var r = Parse(rulesId);
        var tb = Table(rulesId);
        Assert.Equal(row.GetProperty("reachable").GetInt32(), tb.StateCount);
        Assert.Equal(row.GetProperty("nonTerminal").GetInt32(), tb.NonTerminalCount);
        var start = tb.Evaluate(Rules.Initial(r));
        Assert.Equal(Enum.Parse<Value>(row.GetProperty("start").GetString()!), start.Value);
        Assert.Equal(row.GetProperty("distance").GetInt32(), start.Distance);

        if (row.TryGetProperty("openings", out var openings))
        {
            foreach (var o in openings.EnumerateObject())
            {
                var v = tb.Evaluate(Play(r, o.Name));
                Assert.Equal(Enum.Parse<Value>(o.Value[0].GetString()!), v.Value);
                Assert.Equal(o.Value[1].GetInt32(), v.Distance);
            }
        }
        if (row.TryGetProperty("pie", out var pie))
        {
            // Under pie the chooser takes the better side of each opening; X picks the opening that is best for X.
            var pieRules = r with { Opening = OpeningRule.PieSwap };
            var best = Rules.LegalActions(Rules.Initial(pieRules))
                .Select(a => tb.EvaluateMoves(Rules.Apply(Rules.Initial(pieRules), a).State).Max(m => m.ResultForMover))
                .Select(v => v == Value.Draw ? Value.Draw : Value.Loss)   // value for X after the chooser's best decision
                .Max();
            Assert.Equal(Enum.Parse<Value>(pie.GetString()!), best);
        }
    }

    [Fact]
    public void DefaultOracles()
    {
        var tb = Table("n3k3-cap3-v0-w1-none");
        var d = RuleSet.Default;
        Assert.Equal(128_170, tb.StateCount);
        Assert.Equal(116_074, tb.NonTerminalCount);
        Assert.Equal(new PositionValue(Value.Win, 13), tb.Evaluate(Rules.Initial(d)));
        Assert.Equal(new PositionValue(Value.Loss, 12), tb.Evaluate(Play(d, "1")));
        Assert.Equal(new PositionValue(Value.Draw, 0), tb.Evaluate(Play(d, "4")));
        Assert.Equal(new PositionValue(Value.Draw, 0), tb.Evaluate(Play(d, "0")));

        var ghost = Table("n3k3-fifo7-v1-w0-none");
        var g = RuleSet.LegacyFifo7Ghost;
        Assert.Equal(new PositionValue(Value.Win, 13), ghost.Evaluate(Rules.Initial(g)));
        Assert.Equal(new PositionValue(Value.Loss, 12), ghost.Evaluate(Play(g, "1")));

        var legacy = Table("n3k3-fifo7-v1-w1-none");
        var l = Parse("n3k3-fifo7-v1-w1-none");
        Assert.Equal(new PositionValue(Value.Win, 9), legacy.Evaluate(Rules.Initial(l)));
        Assert.Equal(new PositionValue(Value.Loss, 8), legacy.Evaluate(Play(l, "4")));
        Assert.Equal(new PositionValue(Value.Win, 9), legacy.Evaluate(Play(l, "0")));
    }

    [Fact]
    public void DefaultBuildsUnderThreeSeconds()
    {
        Tablebase.Build(RuleSet.Default, 5_000_000, CancellationToken.None); // warm JIT
        var sw = Stopwatch.StartNew();
        var tb = Tablebase.Build(RuleSet.Default, 5_000_000, CancellationToken.None);
        sw.Stop();
        Assert.Equal(128_170, tb.StateCount);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"build took {sw.Elapsed}");
    }

    [Fact]
    public void CanBuildAndLimits()
    {
        Assert.True(Tablebase.CanBuild(RuleSet.Default));
        Assert.False(Tablebase.CanBuild(new RuleSet(BoardSize: 5, WinLength: 4, Window: 9)));
        Assert.False(Tablebase.CanBuild(new RuleSet(BoardSize: 4, Removal: RemovalKind.None)));
        Assert.Throws<TablebaseTooLargeException>(() => Tablebase.Build(RuleSet.Default, 1000, CancellationToken.None));
    }

    [Fact]
    public void MoveClassesAndPie()
    {
        var tb = Table("n3k3-cap3-v0-w1-none");
        var moves = tb.EvaluateMoves(Rules.Initial(RuleSet.Default));
        Assert.Equal(9, moves.Length);
        foreach (var m in moves)
        {
            bool edge = m.Action.Cell is 1 or 3 or 5 or 7;
            Assert.Equal(edge ? MoveClass.Best : MoveClass.Blunder, m.Class);
            Assert.Equal(edge ? Value.Win : Value.Draw, m.ResultForMover);
        }

        var pie = Play(RuleSet.RankedDefault, "1");
        var pm = tb.EvaluateMoves(pie);
        Assert.Equal(MoveClass.Blunder, pm.Single(m => m.Action == GameAction.KeepSides).Class);
        Assert.Equal(MoveClass.Best, pm.Single(m => m.Action == GameAction.Swap).Class);
        Assert.Equal(Value.Win, pm.Single(m => m.Action == GameAction.Swap).ResultForMover);

        var opening = tb.EvaluateMoves(Rules.Initial(RuleSet.RankedDefault));
        Assert.All(opening.Where(m => m.Action.Cell is 1 or 3 or 5 or 7), m => Assert.Equal(MoveClass.Blunder, m.Class));
        Assert.All(opening.Where(m => m.Action.Cell is 0 or 4), m => Assert.Equal(MoveClass.Best, m.Class));
    }

    [Fact]
    public void AnalyzerMarksBlunders()
    {
        var tb = Table("n3k3-cap3-v0-w1-none");
        var actions = Notation.Parse("4,0,8,2,6,3,5");
        var plies = GameAnalyzer.Analyze(tb, RuleSet.Default, actions);
        Assert.Equal(actions.Length, plies.Length);
        Assert.Equal(MoveClass.Blunder, plies[0].Class); // centre opening throws away X's forced win
        Assert.Equal(new PositionValue(Value.Win, 13), plies[0].Before);
        Assert.Contains(GameAction.Place(1), plies[0].BestActions);
        for (int i = 0; i < plies.Length; i++) Assert.Equal(i, plies[i].Ply);
        Assert.Throws<InvalidActionException>(() => GameAnalyzer.Analyze(tb, RuleSet.Default, Notation.Parse("4,4")));
    }
}
