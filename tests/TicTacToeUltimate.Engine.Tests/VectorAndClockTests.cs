using System.Text.Json;
using TicTacToeUltimate.Engine.Analysis;
using TicTacToeUltimate.Engine.Clocks;
using static TicTacToeUltimate.Engine.Tests.TestSupport;

namespace TicTacToeUltimate.Engine.Tests;

public class VectorTests
{
    public static TheoryData<string> VectorFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(SpecPath("rules", "vectors"), "*.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(f));
        return data;
    }

    [Fact]
    public void AllVectorFilesExist()
    {
        string[] expected =
        [
            "n3k3-fifo7-v1-w0-none", "n3k3-fifo7-v1-w0-pie", "n3k3-fifo7-v1-w1-none", "n3k3-fifo7-v1-w1-pie", "n3k3-fifo7-v1-w1-ban",
            "n3k3-fifo7-v0-w1-none", "n3k3-fifo5-v1-w1-none", "n3k3-fifo6-v1-w1-none", "n3k3-fifo9-v0-w1-none", "n3k3-cap3-v1-w1-none",
            "n4k3-fifo8-v1-w1-none",
        ];
        foreach (var id in expected) Assert.True(File.Exists(SpecPath("rules", "vectors", id + ".json")), id);
    }

    [Theory]
    [MemberData(nameof(VectorFiles))]
    public void VectorsConform(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SpecPath("rules", "vectors", id + ".json")));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        var r = Parse(id);
        var rj = root.GetProperty("rules");
        Assert.Equal(id, rj.GetProperty("id").GetString());
        Assert.Equal(r.BoardSize, rj.GetProperty("boardSize").GetInt32());
        Assert.Equal(r.WinLength, rj.GetProperty("winLength").GetInt32());
        Assert.Equal(r.Removal.ToString(), rj.GetProperty("removal").GetString());
        Assert.Equal(r.Window, rj.GetProperty("window").GetInt32());
        Assert.Equal(r.VacatedCellPlayable, rj.GetProperty("vacatedCellPlayable").GetBoolean());
        Assert.Equal(r.VanishingPieceCanWin, rj.GetProperty("vanishingPieceCanWin").GetBoolean());
        Assert.Equal(r.Opening.ToString(), rj.GetProperty("opening").GetString());

        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString();
            var actions = Notation.Parse(c.GetProperty("actions").GetString()!);
            if (c.TryGetProperty("error", out var err))
            {
                var ex = Assert.Throws<InvalidActionException>(() => Rules.Replay(r, actions));
                Assert.Equal(err.GetProperty("atIndex").GetInt32(), ex.Index);
                Assert.Equal(err.GetProperty("code").GetString(), ex.Error.ToString());
                continue;
            }
            var s = Rules.Replay(r, actions);
            var e = c.GetProperty("expect");
            Assert.True(e.GetProperty("ply").GetInt32() == s.Ply, name);
            Assert.Equal(e.GetProperty("placements").GetInt32(), s.Placements);
            Assert.Equal(e.GetProperty("phase").GetString(), s.Phase.ToString());
            Assert.Equal(e.GetProperty("toMove").GetString(), s.ToMove.ToString());
            Assert.Equal(e.GetProperty("outcome").GetString(), s.Outcome.ToString());
            Assert.Equal(e.GetProperty("board").GetString(), Notation.Board(s));
            Assert.Equal(e.GetProperty("legal").EnumerateArray().Select(x => x.GetInt32()), Rules.LegalCells(s));
            Assert.Equal(NullableInt(e.GetProperty("nextToVanish")), Rules.NextToVanish(s));
            var wl = e.GetProperty("winLine");
            if (wl.ValueKind == JsonValueKind.Null) Assert.True(s.WinLine.IsEmpty, name);
            else Assert.Equal(wl.EnumerateArray().Select(x => x.GetInt32()), s.WinLine);
            Assert.Equal(e.GetProperty("swapped").GetBoolean(), s.Swapped);
            Assert.Equal(NullableInt(e.GetProperty("lastVanished")), s.LastVanishedCell);
        }

        int playouts = 0;
        foreach (var p in root.GetProperty("playouts").EnumerateArray())
        {
            playouts++;
            var rng = new SplitMix64(ulong.Parse(p.GetProperty("seed").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
            var s = Rules.Initial(r);
            var played = new List<GameAction>();
            while (s.Phase != GamePhase.Ended && played.Count < 60)
            {
                var legal = Rules.LegalActions(s);
                var a = legal[rng.NextInt(legal.Length)];
                played.Add(a);
                s = Rules.Apply(s, a).State;
            }
            Assert.Equal(p.GetProperty("actions").GetString(), Notation.Format(played));
            Assert.Equal(p.GetProperty("finalBoard").GetString(), Notation.Board(s));
            Assert.Equal(p.GetProperty("outcome").GetString(), s.Outcome.ToString());
        }
        Assert.Equal(200, playouts);

        if (root.TryGetProperty("tablebase", out var tbj))
        {
            var tb = Table(id.EndsWith("-pie", StringComparison.Ordinal) ? id[..^3] + "none" : id);
            Assert.Equal(tbj.GetProperty("reachable").GetInt32(), tb.StateCount);
            Assert.Equal(tbj.GetProperty("nonTerminal").GetInt32(), tb.NonTerminalCount);
            foreach (var pos in tbj.GetProperty("positions").EnumerateArray())
            {
                var s = Rules.Replay(r, Notation.Parse(pos.GetProperty("actions").GetString()!));
                var v = tb.Evaluate(s);
                Assert.Equal(pos.GetProperty("value").GetString(), v.Value.ToString());
                Assert.Equal(pos.GetProperty("distance").GetInt32(), v.Distance);
            }
        }
    }

    private static int? NullableInt(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();
}

public class ClockTests
{
    [Theory]
    [InlineData(TimeControl.Ranked, 0, 15)]
    [InlineData(TimeControl.Ranked, 59_999, 15)]
    [InlineData(TimeControl.Ranked, 60_000, 10)]
    [InlineData(TimeControl.Ranked, 71_999, 10)]
    [InlineData(TimeControl.Ranked, 72_000, 9)]
    [InlineData(TimeControl.Ranked, 84_000, 8)]
    [InlineData(TimeControl.Ranked, 91_000, 8)]
    [InlineData(TimeControl.Ranked, 96_000, 7)]
    [InlineData(TimeControl.Ranked, 156_000, 2)]
    [InlineData(TimeControl.Ranked, 3_600_000, 2)]
    [InlineData(TimeControl.CasualX2, 0, 30)]
    [InlineData(TimeControl.CasualX2, 119_999, 30)]
    [InlineData(TimeControl.CasualX2, 120_000, 20)]
    [InlineData(TimeControl.CasualX2, 144_000, 18)]
    [InlineData(TimeControl.CasualX2, 312_000, 4)]
    [InlineData(TimeControl.CasualX4, 0, 60)]
    [InlineData(TimeControl.CasualX4, 240_000, 40)]
    [InlineData(TimeControl.CasualX4, 624_000, 8)]
    [InlineData(TimeControl.CasualX4, 5_000_000, 8)]
    public void AllowanceOracles(TimeControl tc, long elapsedMs, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), MoveClock.Allowance(tc, TimeSpan.FromMilliseconds(elapsedMs)));

    [Fact]
    public void NextStepAndSuddenDeath()
    {
        Assert.Equal((TimeSpan.FromSeconds(96), TimeSpan.FromSeconds(7)), MoveClock.NextStep(TimeControl.Ranked, TimeSpan.FromSeconds(91)));
        Assert.Equal((TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10)), MoveClock.NextStep(TimeControl.Ranked, TimeSpan.Zero));
        Assert.Null(MoveClock.NextStep(TimeControl.Ranked, TimeSpan.FromSeconds(156)));
        Assert.Null(MoveClock.NextStep(TimeControl.Unlimited, TimeSpan.Zero));
        Assert.Null(MoveClock.NextStep(TimeControl.Daily, TimeSpan.Zero));
        Assert.Null(MoveClock.Allowance(TimeControl.Unlimited, TimeSpan.Zero));
        Assert.Equal(MoveClock.DailyPerMove, MoveClock.Allowance(TimeControl.Daily, TimeSpan.FromDays(3)));
        Assert.False(MoveClock.IsSuddenDeath(TimeControl.Ranked, TimeSpan.FromMilliseconds(59_999)));
        Assert.True(MoveClock.IsSuddenDeath(TimeControl.Ranked, TimeSpan.FromSeconds(60)));
        Assert.False(MoveClock.IsSuddenDeath(TimeControl.CasualX2, TimeSpan.FromSeconds(100)));
        Assert.False(MoveClock.IsSuddenDeath(TimeControl.Daily, TimeSpan.FromDays(1)));
        Assert.Equal(1, MoveClock.Scale(TimeControl.Ranked));
        Assert.Equal(0, MoveClock.Scale(TimeControl.Daily));
    }

    [Fact]
    public void ScheduleJsonMatches()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SpecPath("clock", "schedule.json")));
        int n = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            n++;
            var tc = Enum.Parse<TimeControl>(e.GetProperty("timeControl").GetString()!);
            var at = TimeSpan.FromMilliseconds(e.GetProperty("gameElapsedMs").GetInt64());
            var allowance = MoveClock.Allowance(tc, at);
            var a = e.GetProperty("allowanceMs");
            Assert.Equal(a.ValueKind == JsonValueKind.Null ? (long?)null : a.GetInt64(), allowance is { } x ? (long)x.TotalMilliseconds : null);
            Assert.Equal(e.GetProperty("suddenDeath").GetBoolean(), MoveClock.IsSuddenDeath(tc, at));
            var next = MoveClock.NextStep(tc, at);
            var ns = e.GetProperty("nextStepAtMs");
            var na = e.GetProperty("nextAllowanceMs");
            Assert.Equal(ns.ValueKind == JsonValueKind.Null ? (long?)null : ns.GetInt64(), next is { } y ? (long?)(long)y.AtGameElapsed.TotalMilliseconds : null);
            Assert.Equal(na.ValueKind == JsonValueKind.Null ? (long?)null : na.GetInt64(), next is { } z ? (long?)(long)z.Allowance.TotalMilliseconds : null);
        }
        Assert.True(n >= 3 * 701);
    }
}
