using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Tests.Matches;
using static TicTacToeUltimate.Server.Tests.Matches.Harness;

namespace TicTacToeUltimate.Server.Tests.Clocks;

public class MatchClockTests
{
    [Fact]
    public async Task AllowanceIsFixedAtTurnStart()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Ranked);
        int[] cells = [0, 4, 8, 1, 7];
        for (var ply = 0; ply < cells.Length; ply++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(11));
            var ack = await h.PlaceAsync(id, ply % 2 == 0 ? A : B, ply, cells[ply]);
            Assert.True(ack.Accepted, ack.Error);
        }
        var snap = await h.SnapAsync(id);
        Assert.Equal(55_000, snap.Clock!.GameElapsedMsAtTurnStart);
        Assert.Equal(15_000, snap.Clock.AllowanceMs);   // turn started at 0:55 -> 15 s, even though it will end past 1:00
        h.Time.Advance(TimeSpan.FromSeconds(14));
        var late = await h.PlaceAsync(id, B, 5, 6);
        Assert.True(late.Accepted, late.Error);
        snap = await h.SnapAsync(id);
        Assert.Equal(69_000, snap.Clock!.GameElapsedMsAtTurnStart);
        Assert.Equal(10_000, snap.Clock.AllowanceMs);
        Assert.True(snap.Clock.SuddenDeath);
    }

    [Fact]
    public async Task TimeoutExactlyAtDeadlineLoses()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Ranked, rated: true);
        Assert.True((await h.PlaceAsync(id, A, 0, 0)).Accepted);
        await h.FlushAsync(id);
        h.Time.Advance(TimeSpan.FromSeconds(15));
        await h.FlushAsync(id);
        var snap = await h.SnapAsync(id);
        Assert.NotNull(snap.Result);
        Assert.Equal(EndReason.Timeout, snap.Result!.Reason);
        Assert.Equal(0, snap.Result.WinnerSeat);
        Assert.True(snap.Result.Rated);
        var late = await h.PlaceAsync(id, B, 1, 4);
        Assert.Equal(ErrorCodes.MatchOver, late.Error);
    }

    [Fact]
    public async Task MoveOneTickBeforeDeadlineIsAccepted()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Ranked);
        Assert.True((await h.PlaceAsync(id, A, 0, 0)).Accepted);
        h.Time.Advance(TimeSpan.FromSeconds(15) - TimeSpan.FromTicks(1));
        var ack = await h.PlaceAsync(id, B, 1, 4);
        Assert.True(ack.Accepted, ack.Error);
        h.Time.Advance(TimeSpan.FromTicks(1));
        await h.FlushAsync(id);
        Assert.Null((await h.SnapAsync(id)).Result);
    }

    [Fact]
    public async Task MoveStampedBeforeDeadlineWinsOverLaterTick()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Ranked);
        Assert.True((await h.PlaceAsync(id, A, 0, 0)).Accepted);
        await h.FlushAsync(id);
        h.Time.Advance(TimeSpan.FromSeconds(15) - TimeSpan.FromMilliseconds(1));
        var pending = h.PlaceAsync(id, B, 1, 4);          // stamped + enqueued now (ingress lock)
        h.Time.Advance(TimeSpan.FromMilliseconds(1));     // the deadline timer posts its Tick after the move
        var ack = await pending;
        Assert.True(ack.Accepted, ack.Error);
        await h.FlushAsync(id);
        var snap = await h.SnapAsync(id);
        Assert.Null(snap.Result);
        Assert.Equal(2, snap.Ply);
    }

    [Fact]
    public async Task CreditDrainsBudgetPerMove()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Ranked);
        h.Gateway.PostRtt(A, 600, 300);
        h.Gateway.PostRtt(B, 600, 300);
        Assert.True((await h.PlaceAsync(id, A, 0, 0)).Accepted);   // A's credit was frozen at 0 before the sample
        var snap = await h.SnapAsync(id);
        Assert.Equal([4000, 4000], snap.Clock!.LagBudgetLeftMs);
        h.Time.Advance(TimeSpan.FromMilliseconds(15_200));           // 200 ms past nominal, inside the 300 ms credit
        var ack = await h.PlaceAsync(id, B, 1, 4);
        Assert.True(ack.Accepted, ack.Error);
        snap = await h.SnapAsync(id);
        Assert.Equal(14_900, snap.Actions[1].ThinkMs);
        Assert.Equal([4000, 3800], snap.Clock!.LagBudgetLeftMs);   // only the 200 ms actually used past the allowance is debited
        Assert.True((await h.PlaceAsync(id, A, 2, 8)).Accepted);
        snap = await h.SnapAsync(id);
        Assert.Equal([4000, 3800], snap.Clock!.LagBudgetLeftMs);   // an instant move costs no budget
        Assert.Equal(0, snap.Actions[2].ThinkMs);
    }

    [Fact]
    public async Task CasualTimeoutAutoMoveIsDeterministic()
    {
        async Task<ActionDto> Run()
        {
            var h = new Harness();
            var id = await h.CreateAsync(TimeControl.CasualX2, seed: 1234);
            Assert.True((await h.PlaceAsync(id, A, 0, 0)).Accepted);
            await h.FlushAsync(id);
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await h.FlushAsync(id);
            var snap = await h.SnapAsync(id);
            Assert.Null(snap.Result);
            Assert.Equal(2, snap.Ply);
            return snap.Actions[1];
        }
        var first = await Run();
        var second = await Run();
        Assert.True(first.Auto);
        Assert.Equal(30_000, first.ThinkMs);
        Assert.Equal(first.Cell, second.Cell);
        var rules = new RefRules();
        var expected = MatchActor.AutoAction(1234, rules.Apply(rules.Initial(RuleSet.Default), GameAction.Place(0)).State);
        Assert.Equal(expected.Cell, first.Cell);
    }

    [Fact]
    public async Task PlyZeroTimeoutAborts()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Ranked, rated: true);
        await h.FlushAsync(id);
        h.Time.Advance(TimeSpan.FromSeconds(15));
        await h.FlushAsync(id);
        var snap = await h.SnapAsync(id);
        Assert.Equal(EndReason.Aborted, snap.Result!.Reason);
        Assert.False(snap.Result.Rated);
        Assert.Single(h.Sink.Completed);
        Assert.Equal(EndReason.Aborted, h.Sink.Completed.Single().Result.Reason);
    }

    [Fact]
    public async Task PlyZeroResignAborts()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Ranked, rated: true);
        var ack = await h.Gateway.ResignAsync(id, A, default);
        Assert.True(ack.Ok);
        var snap = await h.SnapAsync(id);
        Assert.Equal(EndReason.Aborted, snap.Result!.Reason);
        Assert.Null(snap.Result.WinnerSeat);
    }

    [Fact]
    public async Task ResignAfterFirstMoveIsLoss()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.Ranked, rated: true);
        Assert.True((await h.PlaceAsync(id, A, 0, 0)).Accepted);
        Assert.True((await h.Gateway.ResignAsync(id, A, default)).Ok);
        var snap = await h.SnapAsync(id);
        Assert.Equal(EndReason.Resign, snap.Result!.Reason);
        Assert.Equal(1, snap.Result.WinnerSeat);
        Assert.Equal(Outcome.OWins, snap.Result.Outcome);
    }
}
