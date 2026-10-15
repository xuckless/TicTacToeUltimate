using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Analysis;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Analysis;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Tests.Analysis;

public class AnalysisTests
{
    private static readonly Lazy<Tablebase> DefaultTable = new(() => Tablebase.Build(RuleSet.Default, 5_000_000, CancellationToken.None));

    private static TablebaseCache NewCache(params string[] precompute)
        => new(Options.Create(new TttOptions { Analysis = new AnalysisOptions { Precompute = [.. precompute] } }),
            NullLogger<TablebaseCache>.Instance);

    private static async Task WaitReady(TablebaseCache cache, RuleSet rules)
    {
        var deadline = Environment.TickCount64 + 60_000;
        while (cache.GetStatus(rules) != TablebaseStatus.Ready)
        {
            Assert.True(Environment.TickCount64 < deadline, "tablebase did not become ready");
            Assert.NotEqual(TablebaseStatus.Unavailable, cache.GetStatus(rules));
            cache.TryGet(rules, out _);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CacheWarmsInBackgroundAndNormalizesPie()
    {
        using var cache = NewCache("n3k3-fifo7-v1-w0-none");
        var start = cache.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(start.IsCompleted);   // never blocks host startup (/health)
        Assert.NotEqual(TablebaseStatus.Unavailable, cache.GetStatus(RuleSet.Default));

        await WaitReady(cache, RuleSet.Default);
        Assert.True(cache.TryGet(RuleSet.Default, out var a));
        Assert.True(cache.TryGet(RuleSet.RankedDefault, out var b));
        Assert.Same(a, b);
        Assert.Equal(TablebaseStatus.Ready, cache.GetStatus(RuleSet.RankedDefault));
        Assert.Equal(369_802, a.StateCount);

        var ban = RuleSet.Default with { Opening = OpeningRule.BanCenter };
        Assert.NotEqual(TablebaseStatus.Ready, cache.GetStatus(ban));   // BanCenter is not normalized
        await cache.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LazyBuildAndUnavailable()
    {
        using var cache = NewCache();
        await cache.StartAsync(TestContext.Current.CancellationToken);
        var big = new RuleSet(BoardSize: 5, WinLength: 4, Window: 9);
        Assert.False(cache.TryGet(big, out _));
        Assert.Equal(TablebaseStatus.Unavailable, cache.GetStatus(big));

        var legacy = RuleSet.Default with { VanishingPieceCanWin = true };
        Assert.False(cache.TryGet(legacy, out _));
        Assert.Equal(TablebaseStatus.Warming, cache.GetStatus(legacy));
        await WaitReady(cache, legacy);
        Assert.True(cache.TryGet(legacy, out var t));
        Assert.Equal(368_074, t.StateCount);
    }

    [Fact]
    public async Task AnalyzerMarksBlundersBySeat()
    {
        var svc = new AnalysisService(new FakeCache(DefaultTable.Value), NullLogger<AnalysisService>.Instance);
        // Seat A (X) opens in the centre: a blunder (edges win by force).
        var game = Game(RuleSet.Default, "4,0,8,2,6,3,5");
        var dto = await svc.AnalyzeAsync(game, TestContext.Current.CancellationToken);
        Assert.NotNull(dto);
        Assert.Equal(game.Id, dto.GameId);
        Assert.Equal("n3k3-fifo7-v1-w0-none", dto.RulesId);
        Assert.Equal(7, dto.Plies.Length);
        Assert.Equal(MoveClass.Blunder, dto.Plies[0].Class);
        Assert.Equal(0, dto.Plies[0].Seat);
        Assert.Equal(new PositionValueDto(Value.Win, 13), dto.Plies[0].Before);
        Assert.Contains("1", dto.Plies[0].BestActions);
        Assert.True(dto.BlundersBySeat[0] >= 1);
        Assert.Equal(dto.Plies.Count(p => p.Seat == 1 && p.Class == MoveClass.Blunder), dto.BlundersBySeat[1]);

        var none = new AnalysisService(new FakeCache(null), NullLogger<AnalysisService>.Instance);
        Assert.Null(await none.AnalyzeAsync(game, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PieGameIsAnalyzed()
    {
        var svc = new AnalysisService(new FakeCache(DefaultTable.Value), NullLogger<AnalysisService>.Instance);
        // X opens on an edge (a loss under pie), O keeps (blunder: swapping wins).
        var dto = await svc.AnalyzeAsync(Game(RuleSet.RankedDefault, "1,keep,4"), TestContext.Current.CancellationToken);
        Assert.NotNull(dto);
        Assert.Equal(MoveClass.Blunder, dto.Plies[0].Class);
        Assert.Equal(MoveClass.Blunder, dto.Plies[1].Class);
        Assert.Contains("swap", dto.Plies[1].BestActions);
    }

    private static GameRecord Game(RuleSet rules, string actions)
    {
        var parsed = Notation.Parse(actions);
        var s = Rules.Initial(rules);
        var stored = new List<StoredAction>();
        var at = DateTimeOffset.UnixEpoch;
        foreach (var a in parsed)
        {
            // Seat of the acting colour (no swap in these fixtures before the decision; after Swap seats flip).
            int seat = (s.ToMove == Color.X) ^ s.Swapped ? 0 : 1;
            var res = Rules.Apply(s, a);
            stored.Add(new StoredAction(s.Ply, seat, a, 1000, false, res.VanishedCell, at));
            s = res.State;
        }
        var setup = new MatchSetup(Guid.NewGuid(), MatchMode.Casual, TimeControl.CasualX2, rules, false,
            new SeatSetup(Guid.NewGuid(), "a", AccountKind.Account), new SeatSetup(Guid.NewGuid(), "b", AccountKind.Account),
            1, MatchOrigin.Queue, null, null);
        var result = new MatchResultDto(s.Outcome, null, EndReason.Resign, [], false, null);
        return new GameRecord(setup.MatchId, setup, s.Swapped, stored, result, at, at, []);
    }

    private sealed class FakeCache(Tablebase? table) : ITablebaseCache
    {
        public TablebaseStatus GetStatus(RuleSet rules) => table is null ? TablebaseStatus.Unavailable : TablebaseStatus.Ready;
        public bool TryGet(RuleSet rules, [NotNullWhen(true)] out Tablebase? tablebase)
        {
            tablebase = table;
            return table is not null;
        }
    }
}
