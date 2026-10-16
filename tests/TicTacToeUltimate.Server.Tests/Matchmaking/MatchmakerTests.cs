using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Matchmaking;
using TicTacToeUltimate.Server.Tests.Matches;

namespace TicTacToeUltimate.Server.Tests.Matchmaking;

public class MatchmakerTests
{
    private sealed class FakeGateway : IMatchGateway
    {
        public ConcurrentQueue<MatchSetup> Created { get; } = new();
        public Task<Guid> CreateMatchAsync(MatchSetup setup, CancellationToken ct) { Created.Enqueue(setup); return Task.FromResult(setup.MatchId); }
        public Task<AttachResult> AttachAsync(Guid matchId, Guid userId, IMatchObserver observer, CancellationToken ct) => throw new NotSupportedException();
        public Task<MatchSnapshotDto?> GetSnapshotAsync(Guid matchId, Guid? viewerUserId, CancellationToken ct) => throw new NotSupportedException();
        public Task<MoveAckDto> SubmitActionAsync(Guid matchId, Guid userId, int expectedPly, GameAction action, Guid actionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommandAckDto> ResignAsync(Guid matchId, Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<Guid> ActiveMatchesFor(Guid userId) => [];
    }

    private sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        public FakeGateway Gateway { get; } = new();
        public RecordingNotifier Notifier { get; } = new();
        public FakeBotPool Bots { get; } = new() { Available = false };
        public UserActivityRegistry Activity { get; } = new();
        public TttOptions Options { get; } = new();
        public RecordingDispatcher Dispatcher { get; } = new();
        public TicTacToeUltimate.Server.Matches.ServerDrain Drain { get; } = new();
        public Matchmaker Mm { get; }
        public Rig() => Mm = new Matchmaker(Gateway, Activity, Notifier, new FakeCards(), Bots, Time, Options, NullLogger<Matchmaker>.Instance, Dispatcher, Drain);

        public Guid Enter(int rating, TimeControl tc = TimeControl.Ranked, AccountKind kind = AccountKind.Account, string? region = null)
        {
            var id = Guid.NewGuid();
            Assert.Null(Activity.TryEnterQueue(id, tc));
            Mm.Post(new EnterQueueCommand(new QueueEntry(id, "u", kind, tc, rating, region, Time.GetUtcNow().ToUnixTimeMilliseconds())));
            return id;
        }

        public async Task At(double seconds)
        {
            Time.SetUtcNow(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds));
            await Mm.TickAsync(default);
        }
    }

    [Fact]
    public void WindowWidensEveryThreeSeconds()
    {
        var o = new MatchmakingOptions();
        Assert.Equal(100, Matchmaker.Window(0, o));
        Assert.Equal(100, Matchmaker.Window(2_999, o));
        Assert.Equal(150, Matchmaker.Window(3_000, o));
        Assert.Equal(200, Matchmaker.Window(6_000, o));
        Assert.Equal(250, Matchmaker.Window(9_000, o));
    }

    [Theory]
    [InlineData(140, 3)]
    [InlineData(190, 6)]
    [InlineData(240, 9)]
    public async Task PairsWhenBothWindowsCoverTheGap(int gap, int seconds)
    {
        var r = new Rig();
        r.Enter(1200);
        r.Enter(1200 + gap);
        await r.At(seconds - 0.1);
        Assert.Empty(r.Gateway.Created);
        await r.At(seconds);
        var setup = Assert.Single(r.Gateway.Created);
        Assert.Equal(MatchMode.Ranked, setup.Mode);
        Assert.True(setup.Rated);
        Assert.Equal(RuleSet.RankedDefault.Id, setup.Rules.Id);
        Assert.Equal(2, r.Notifier.Calls.Count(c => c.Method == "MatchFound"));
    }

    [Fact]
    public async Task NeverDoublePairs()
    {
        var r = new Rig();
        var users = Enumerable.Range(0, 11).Select(_ => r.Enter(1200)).ToList();
        r.Mm.Post(new EnterQueueCommand(new QueueEntry(users[0], "dup", AccountKind.Account, TimeControl.Ranked, 1200, null, 0)));
        await r.At(0.25);
        Assert.Equal(5, r.Gateway.Created.Count);
        var seated = r.Gateway.Created.SelectMany(s => new[] { s.SeatA.UserId, s.SeatB.UserId }).ToList();
        Assert.Equal(seated.Count, seated.Distinct().Count());
        Assert.Single(r.Mm.Entries);
        await r.At(1);
        Assert.Equal(5, r.Gateway.Created.Count);
        foreach (var u in seated) Assert.IsType<InMatchActivity>(r.Activity.Get(u));
    }

    [Fact]
    public async Task PrefersSameRegion()
    {
        var r = new Rig();
        var me = r.Enter(1200, region: "Europe");
        r.Enter(1200, region: "America");
        var eu = r.Enter(1250, region: "Europe");
        await r.At(0.25);
        var s = Assert.Single(r.Gateway.Created);
        Assert.Contains(eu, new[] { s.SeatA.UserId, s.SeatB.UserId });
        Assert.Contains(me, new[] { s.SeatA.UserId, s.SeatB.UserId });
    }

    [Fact]
    public async Task GuestFallsBackToAccountPoolAfterTenSeconds()
    {
        var r = new Rig();
        var guest = r.Enter(1200, TimeControl.CasualX2, AccountKind.Guest);
        var acct = r.Enter(1200, TimeControl.CasualX2);
        await r.At(9.9);
        Assert.Empty(r.Gateway.Created);
        await r.At(10);
        var s = Assert.Single(r.Gateway.Created);
        Assert.False(s.Rated);
        Assert.Equal(new[] { guest, acct }.OrderBy(x => x), new[] { s.SeatA.UserId, s.SeatB.UserId }.OrderBy(x => x));
    }

    [Fact]
    public async Task BotFallbackAfterTwentySeconds()
    {
        var r = new Rig();
        r.Bots.Available = true;
        var u = r.Enter(1200, TimeControl.CasualX2);
        var ranked = r.Enter(1200, TimeControl.Ranked);
        await r.At(19.9);
        Assert.Empty(r.Gateway.Created);
        await r.At(20);
        var s = Assert.Single(r.Gateway.Created);
        Assert.Equal(MatchOrigin.BotFallback, s.Origin);
        Assert.Contains(FakeBotPool.BotId, new[] { s.SeatA.UserId, s.SeatB.UserId });
        Assert.Contains(u, new[] { s.SeatA.UserId, s.SeatB.UserId });
        Assert.Single(r.Bots.Notified);
        Assert.Contains(r.Mm.Entries, e => e.UserId == ranked);   // Ranked never falls back to bots unless Bots:Ranked
    }

    private sealed class RecordingDispatcher : TicTacToeUltimate.Server.Bots.IBotDispatcher
    {
        public List<(TimeControl Tc, Guid User)> Requests { get; } = [];
        public ValueTask RequestAsync(TimeControl timeControl, Guid forUserId, int forRating, CancellationToken ct)
        {
            Requests.Add((timeControl, forUserId));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task DispatchesOneBotPerWaitingPlayerAndHumansPairFirst()
    {
        var r = new Rig();
        r.Options.Bots.Ranked = true;
        var a = r.Enter(1200, TimeControl.Casual);
        var arena = r.Enter(1500, TimeControl.ArenaRanked);
        var daily = r.Enter(1200, TimeControl.Daily);
        await r.At(5.9);
        Assert.Empty(r.Dispatcher.Requests);
        await r.At(6);
        await r.At(7);
        Assert.Equal(2, r.Dispatcher.Requests.Count);                       // once each, never for Daily
        Assert.Contains((TimeControl.Casual, a), r.Dispatcher.Requests);
        Assert.Contains((TimeControl.ArenaRanked, arena), r.Dispatcher.Requests);
        Assert.DoesNotContain(r.Dispatcher.Requests, q => q.User == daily);
        // A human arriving before the fallback still wins over the bot.
        r.Bots.Available = true;
        var b = r.Enter(1210, TimeControl.Casual);
        await r.At(8);
        var s = Assert.Single(r.Gateway.Created);
        Assert.Equal(MatchOrigin.Queue, s.Origin);
        Assert.Contains(b, new[] { s.SeatA.UserId, s.SeatB.UserId });
        await r.At(20);
        Assert.Contains(r.Gateway.Created, m => m.Origin == MatchOrigin.BotFallback && (m.SeatA.UserId == arena || m.SeatB.UserId == arena));
    }

    [Fact]
    public async Task DrainingStartsNothingNewButKeepsTheQueue()
    {
        var r = new Rig();
        r.Bots.Available = true;
        r.Drain.Begin();
        var a = r.Enter(1200, TimeControl.Casual);
        var b = r.Enter(1200, TimeControl.Casual);
        await r.At(30);
        Assert.Empty(r.Gateway.Created);                 // no human pair, no bot fallback
        Assert.Empty(r.Dispatcher.Requests);
        Assert.Equal(2, r.Mm.Entries.Count);              // still queued
        Assert.True(r.Notifier.Of<QueueStatusDto>(a).Last().ServerUpdating);
    }

    [Fact]
    public async Task DisconnectedEntryIsDroppedAfterGrace()
    {
        var r = new Rig();
        var u = r.Enter(1200);
        await r.At(0.25);
        r.Mm.Post(new DisconnectQueueCommand(u));
        await r.At(1);
        Assert.Single(r.Mm.Entries);
        await r.At(6);
        Assert.Empty(r.Mm.Entries);
        Assert.Null(r.Activity.Get(u));
    }

    [Fact]
    public async Task SendsQueueStatus()
    {
        var r = new Rig();
        var u = r.Enter(1200);
        await r.At(3);
        var st = r.Notifier.Of<QueueStatusDto>(u).Last();
        Assert.Equal(3000, st.WaitedMs);
        Assert.Equal(150, st.RatingWindow);
    }
}
