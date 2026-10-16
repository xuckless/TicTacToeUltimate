using System.Collections.Concurrent;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Matches;
using static TicTacToeUltimate.Server.Tests.Matches.Harness;

namespace TicTacToeUltimate.Server.Tests.Matches;

public class MatchActorTests
{
    [Fact]
    public async Task PieSwapRemapsSeats()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Ranked, RuleSet.RankedDefault, rated: true);
        Assert.True((await h.PlaceAsync(id, A, 0, 4)).Accepted);
        var mid = await h.SnapAsync(id);
        Assert.Equal(GamePhase.AwaitingSwapDecision, mid.Phase);
        Assert.Equal(1, mid.ToMoveSeat);
        var ack = await h.Gateway.SubmitActionAsync(id, B, 1, GameAction.Swap, Guid.NewGuid(), default);
        Assert.True(ack.Accepted, ack.Error);
        var snap = await h.SnapAsync(id, A);
        Assert.True(snap.Swapped);
        Assert.Equal(Color.O, snap.Seats[0].Color);
        Assert.Equal(Color.X, snap.Seats[1].Color);
        Assert.Equal(Color.O, snap.ToMove);
        Assert.Equal(0, snap.ToMoveSeat);
        Assert.Equal(0, snap.YourSeat);
        Assert.Equal(ActionKind.Swap, snap.Actions[1].Kind);
        Assert.Null(snap.Actions[1].Color);
        Assert.Equal(ErrorCodes.NotYourTurn, (await h.PlaceAsync(id, B, 2, 0)).Error);
        Assert.True((await h.PlaceAsync(id, A, 2, 0)).Accepted);
    }

    [Fact]
    public async Task StalePlyAndNotYourTurnAndNotParticipant()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Unlimited);
        Assert.Equal(ErrorCodes.StalePly, (await h.PlaceAsync(id, A, 5, 0)).Error);
        Assert.Equal(ErrorCodes.NotYourTurn, (await h.PlaceAsync(id, B, 0, 0)).Error);
        Assert.Equal(ErrorCodes.NotAParticipant, (await h.PlaceAsync(id, Guid.NewGuid(), 0, 0)).Error);
        Assert.True((await h.PlaceAsync(id, A, 0, 0)).Accepted);
        Assert.Equal(ErrorCodes.Occupied, (await h.PlaceAsync(id, B, 1, 0)).Error);
        Assert.Equal(ErrorCodes.MatchNotFound, (await h.PlaceAsync(Guid.NewGuid(), A, 0, 0)).Error);
    }

    [Fact]
    public async Task ActionIdDedupeReturnsOriginalAck()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Unlimited);
        var actionId = Guid.NewGuid();
        var first = await h.PlaceAsync(id, A, 0, 0, actionId);
        var again = await h.PlaceAsync(id, A, 0, 0, actionId);
        Assert.True(first.Accepted);
        Assert.Equal(first, again);
        Assert.Equal(1, (await h.SnapAsync(id)).Ply);
    }

    [Fact]
    public async Task StormOfParallelSubmitsAcceptsExactlyOnePerPly()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Unlimited);
        var observers = new[] { new RecordingObserver(), new RecordingObserver(), new RecordingObserver() };
        var subs = new List<AttachResult>
        {
            await h.Gateway.AttachAsync(id, A, observers[0], default),
            await h.Gateway.AttachAsync(id, A, observers[1], default),
            await h.Gateway.AttachAsync(id, B, observers[2], default),
        };
        var acks = new ConcurrentBag<MoveAckDto>();
        var tasks = Enumerable.Range(0, 1000).Select(i => Task.Run(async () =>
        {
            var user = i % 2 == 0 ? A : B;
            var snap = await h.SnapAsync(id, user);
            var cell = snap.LegalCells.Length == 0 ? 0 : snap.LegalCells[Random.Shared.Next(snap.LegalCells.Length)];
            acks.Add(await h.PlaceAsync(id, user, snap.Ply, cell));
        })).ToArray();
        await Task.WhenAll(tasks);
        await h.FlushAsync(id);
        var final = await h.SnapAsync(id);
        var accepted = acks.Where(a => a.Accepted).ToList();
        Assert.Equal(final.Ply, accepted.Count);
        Assert.Equal(accepted.Count, accepted.Select(a => a.CurrentPly).Distinct().Count());
        Assert.All(acks.Where(a => !a.Accepted), a => Assert.NotEqual(ErrorCodes.Internal, a.Error));
        foreach (var o in observers)
        {
            var seqs = o.Events.Select(e => e.Seq).ToArray();
            Assert.NotEmpty(seqs);
            for (var i = 1; i < seqs.Length; i++) Assert.True(seqs[i] > seqs[i - 1]);
            Assert.Equal(final.Seq, seqs[^1]);
        }
        foreach (var s in subs) await s.Subscription.DisposeAsync();
    }

    [Fact]
    public async Task SnapshotPlusLaterEventsEqualsActorState()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Unlimited);
        Assert.True((await h.PlaceAsync(id, A, 0, 0)).Accepted);
        Assert.True((await h.PlaceAsync(id, B, 1, 4)).Accepted);
        var obs = new RecordingObserver();
        var att = await h.Gateway.AttachAsync(id, A, obs, default);
        Assert.True((await h.PlaceAsync(id, A, 2, 8)).Accepted);
        Assert.True((await h.PlaceAsync(id, B, 3, 1)).Accepted);
        Assert.True((await h.PlaceAsync(id, A, 4, 7)).Accepted);
        await h.FlushAsync(id);
        var final = await h.SnapAsync(id);
        var rebuilt = att.Snapshot.Actions.ToList();
        rebuilt.AddRange(obs.Events.Where(e => e.Seq > att.Snapshot.Seq).Select(e => e.Event).OfType<ActionApplied>().Select(a => a.Action));
        Assert.Equal(final.Actions, rebuilt);
        Assert.All(obs.Events, e => Assert.True(e.Seq > att.Snapshot.Seq));
        var lastApplied = obs.Events.Select(e => e.Event).OfType<ActionApplied>().Last();
        Assert.Equal(final.Board, lastApplied.Board);
    }

    [Fact]
    public async Task ConnectionAttachAddsGroupBeforeLaterEvents()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Unlimited);
        var snap = await h.Gateway.AttachConnectionAsync(id, A, "conn-1", false, default);
        Assert.Equal(0, snap.YourSeat);
        Assert.True(snap.Seats[0].Connected);
        await Assert.ThrowsAsync<MatchAccessException>(() => h.Gateway.AttachConnectionAsync(id, Guid.NewGuid(), "conn-2", false, default));
        var spec = await h.Gateway.AttachConnectionAsync(id, Guid.NewGuid(), "conn-3", true, default);
        Assert.Null(spec.YourSeat);
        await h.FlushAsync(id);
        Assert.Contains(h.Broadcaster.Groups, g => g.Conn == "conn-1" && g.Add);
        Assert.Equal(1, (await h.SnapAsync(id)).SpectatorCount);
        h.Gateway.DetachConnectionEverywhere("conn-1");
        await h.FlushAsync(id);
        Assert.False((await h.SnapAsync(id)).Seats[0].Connected);
    }

    [Fact]
    public async Task DailyRehydrationIsSingleFlight()
    {
        var h = new Harness(withDailyRepo: true);
        var setup = h.Setup(TimeControl.Daily, RuleSet.RankedDefault, rated: true);
        var now = h.Time.GetUtcNow();
        h.Daily.Put(new StoredDailyMatch(setup, [new StoredAction(0, 0, GameAction.Place(4), 1000, false, null, now)], 1, now.AddHours(24), false, now));
        h.Daily.LoadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t1 = h.Gateway.GetOrActivateAsync(setup.MatchId);
        var t2 = h.Gateway.GetOrActivateAsync(setup.MatchId);
        h.Daily.LoadGate.SetResult();
        var a1 = await t1;
        var a2 = await t2;
        Assert.NotNull(a1);
        Assert.Same(a1, a2);
        Assert.Equal(1, h.Daily.LoadCount);
        var snap = await h.SnapAsync(setup.MatchId, B);
        Assert.Equal(1, snap.Ply);
        Assert.Equal(GamePhase.AwaitingSwapDecision, snap.Phase);
        Assert.Equal(1, snap.YourSeat);
    }

    [Fact]
    public async Task DailyPersistsActionsAndSweeperTimesOut()
    {
        var h = new Harness(withDailyRepo: true);
        var id = await h.CreateAsync(TimeControl.Daily, RuleSet.RankedDefault, rated: true);
        Assert.True((await h.PlaceAsync(id, A, 0, 4)).Accepted);
        Assert.Single(h.Daily.Peek(id)!.Actions);
        await h.FlushAsync(id);
        Assert.Contains(h.Notifier.Calls, c => c.User == B && c.Payload is NotificationDto { Kind: NotificationKind.DailyYourTurn });

        h.Time.Advance(TimeSpan.FromMinutes(11));            // passivates (no attachments, idle)
        await Harness.EventuallyAsync(() => Task.FromResult(h.Gateway.TryGetLoaded(id) is null ? "gone" : null));

        h.Time.Advance(TimeSpan.FromHours(24));
        var sweeper = new DailyDeadlineSweeper(h.Gateway, h.Time, h.Options, Microsoft.Extensions.Logging.Abstractions.NullLogger<DailyDeadlineSweeper>.Instance);
        await sweeper.SweepOnceAsync(default);
        await h.FlushAsync(id);
        Assert.True(h.Daily.Peek(id)!.Ended);
        var done = Assert.Single(h.Sink.Completed);
        Assert.Equal(EndReason.Timeout, done.Result.Reason);
        Assert.Equal(0, done.Result.WinnerSeat);
    }

    [Fact]
    public async Task UnlimitedDisconnectForfeit()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Unlimited);
        var att = await h.Gateway.AttachAsync(id, A, new RecordingObserver(), default);
        Assert.True((await h.PlaceAsync(id, A, 0, 0)).Accepted);
        h.Time.Advance(TimeSpan.FromMinutes(9));
        await h.FlushAsync(id);
        Assert.Null((await h.SnapAsync(id)).Result);
        h.Time.Advance(TimeSpan.FromMinutes(1.5));
        await h.FlushAsync(id);
        var snap = await h.SnapAsync(id);
        Assert.Equal(EndReason.Abandoned, snap.Result!.Reason);
        Assert.Equal(0, snap.Result.WinnerSeat);
        await att.Subscription.DisposeAsync();
    }

    [Fact]
    public async Task RematchWorksForCasualAndIsRefusedForRated()
    {
        var h = new Harness();
        var ranked = await h.CreateAsync(TimeControl.Ranked, rated: true);
        Assert.True((await h.PlaceAsync(ranked, A, 0, 0)).Accepted);
        Assert.True((await h.Gateway.ResignAsync(ranked, B, default)).Ok);
        Assert.Equal(ErrorCodes.RematchNotAllowed, (await h.Gateway.OfferRematchAsync(ranked, A, default)).Error);

        var casual = await h.CreateAsync(TimeControl.CasualX2);
        Assert.True((await h.PlaceAsync(casual, A, 0, 0)).Accepted);
        Assert.True((await h.Gateway.ResignAsync(casual, B, default)).Ok);
        Assert.True((await h.Gateway.OfferRematchAsync(casual, A, default)).Ok);
        Assert.True((await h.Gateway.OfferRematchAsync(casual, B, default)).Ok);
        await h.FlushAsync(casual);
        var started = h.Broadcaster.Sent.Where(e => e.MatchId == casual).Select(e => e.Event).OfType<RematchStarted>().Single();
        var next = await Harness.EventuallyAsync(() => h.Gateway.GetSnapshotAsync(started.NewMatchId, null, default));
        Assert.Equal(B, next.Seats[0].Player.UserId);
        Assert.Equal(A, next.Seats[1].Player.UserId);
    }

    [Fact]
    public async Task EventsHaveStrictlyIncreasingSeqAndEmotesAreLimited()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Unlimited);
        Assert.True((await h.Gateway.EmoteAsync(id, A, "bert-gg", default)).Ok);
        Assert.Equal(ErrorCodes.EmoteUnknown, (await h.Gateway.EmoteAsync(id, A, "nope", default)).Error);
        Assert.True((await h.Gateway.EmoteAsync(id, A, "bert-gg", default)).Ok);
        Assert.True((await h.Gateway.EmoteAsync(id, A, "bert-gg", default)).Ok);
        Assert.Equal(ErrorCodes.RateLimited, (await h.Gateway.EmoteAsync(id, A, "bert-gg", default)).Error);
        h.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await h.Gateway.EmoteAsync(id, A, "wp", default)).Ok);
        await h.FlushAsync(id);
        var seqs = h.Broadcaster.Sent.Where(e => e.MatchId == id).Select(e => e.Seq).ToArray();
        Assert.Equal(Enumerable.Range(1, seqs.Length).Select(i => (long)i), seqs);
    }

    [Fact]
    public async Task ShutdownAbortsLiveMatches()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.CasualX4);
        Assert.True((await h.PlaceAsync(id, A, 0, 0)).Accepted);
        await h.Gateway.ShutdownAsync(default);
        var done = Assert.Single(h.Sink.Completed);
        Assert.Equal(EndReason.Aborted, done.Result.Reason);
        Assert.Equal("server_restart", done.Result.Detail);
    }
}
