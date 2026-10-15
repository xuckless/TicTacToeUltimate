using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Matches;
using static TicTacToeUltimate.Server.Tests.Matches.Harness;

namespace TicTacToeUltimate.Server.Tests.Matches;

public class ArenaMatchTests
{
    [Fact]
    public async Task LineShattersAndDamages_GameContinues()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.ArenaCasual);
        int[] cells = [0, 4, 1, 8, 2];
        for (var ply = 0; ply < cells.Length; ply++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.True((await h.PlaceAsync(id, ply % 2 == 0 ? A : B, ply, cells[ply])).Accepted);
        }
        var snap = await h.SnapAsync(id, A);
        Assert.Null(snap.Result);
        Assert.Equal(MatchMode.Arena, snap.Mode);
        Assert.Equal("arena-c", snap.ArenaRules!.Id);
        Assert.Equal("....O...O", snap.Board);
        Assert.Equal([125, 100], snap.Arena!.Hp);
        Assert.Equal([1, 0], snap.Arena.Streak);
        Assert.Equal([0, 1, 2], snap.Arena.LastHit!.Shattered);
        Assert.Equal(25, snap.Arena.LastHit.Damage);
        Assert.Equal(0, snap.Arena.LastHit.Seat);
        Assert.Equal(1, snap.ToMoveSeat);
        Assert.Equal(5_000, snap.Clock!.AllowanceMs);       // 5 s per move
        await h.FlushAsync(id);
        var applied = h.Broadcaster.Sent.Select(e => e.Event).OfType<ActionApplied>().Last();
        Assert.Equal(25, applied.Arena!.LastHit!.Damage);
    }

    [Fact]
    public async Task CasualArenaTimeout_AutoMoves()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.ArenaCasual, seed: 7);
        Assert.True((await h.PlaceAsync(id, A, 0, 4)).Accepted);
        await h.FlushAsync(id);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.FlushAsync(id);
        var snap = await h.SnapAsync(id);
        Assert.Null(snap.Result);
        Assert.Equal(2, snap.Ply);
        Assert.True(snap.Actions[1].Auto);
        Assert.Equal(5_000, snap.Arena!.ElapsedMs);
    }

    [Fact]
    public async Task ArenaRankedTimeout_Loses()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.ArenaRanked, rated: true);
        Assert.True((await h.PlaceAsync(id, A, 0, 4)).Accepted);
        await h.FlushAsync(id);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.FlushAsync(id);
        var snap = await h.SnapAsync(id);
        Assert.Equal(EndReason.Timeout, snap.Result!.Reason);
        Assert.Equal(0, snap.Result.WinnerSeat);
        Assert.True(snap.Result.Rated);
    }

    [Fact]
    public async Task RoundTimeOut_HigherHpWinsByDecision()
    {
        var h = new Harness();
        var id = await h.CreateAsync(TimeControl.ArenaCasual);
        int[] cells = [0, 3, 1, 4, 2, 8];
        for (var ply = 0; ply < cells.Length; ply++)
        {
            // Lag credit is 0 (no RTT samples), so each move credits exactly its think time; 4.99 s stays under the 5 s limit.
            h.Time.Advance(TimeSpan.FromMilliseconds(4_990));
            Assert.True((await h.PlaceAsync(id, ply % 2 == 0 ? A : B, ply, cells[ply])).Accepted);
        }
        // 6 moves = 29.94 s: far from 2:00, so play on with blank moves until the round ends.
        var snap = await h.SnapAsync(id);
        Assert.Null(snap.Result);
        var ply2 = snap.Ply;
        var rng = new SplitMix64(3);
        while (snap.Result is null && ply2 < 200)
        {
            var seat = snap.ToMoveSeat!.Value;
            var legal = snap.LegalCells;
            h.Time.Advance(TimeSpan.FromMilliseconds(4_990));
            Assert.True((await h.PlaceAsync(id, seat == 0 ? A : B, ply2, legal[rng.NextInt(legal.Length)])).Accepted);
            snap = await h.SnapAsync(id);
            ply2 = snap.Ply;
        }
        Assert.NotNull(snap.Result);
        Assert.Contains(snap.Result!.Reason, new[] { EndReason.Decision, EndReason.Knockout, EndReason.Overtime });
        if (snap.Result.Reason == EndReason.Decision)
        {
            Assert.Equal(snap.Actions.Count(a => a.Seat == 0), snap.Actions.Count(a => a.Seat == 1));   // equal turns
            Assert.True(snap.Actions.Sum(a => a.ThinkMs) >= 120_000);
        }
    }

    [Fact]
    public void ArenaModes_AreConfigured()
    {
        var o = new Hosting.TttOptions();
        Assert.Equal(MatchMode.Arena, MatchModes.ModeFor(TimeControl.ArenaCasual));
        Assert.Equal(MatchMode.ArenaRanked, MatchModes.ModeFor(TimeControl.ArenaRanked));
        Assert.Equal("arena-c", MatchModes.ArenaFor(o, TimeControl.ArenaRanked)!.Id);
        Assert.Null(MatchModes.ArenaFor(o, TimeControl.Ranked));
        Assert.True(MatchModes.Options(o, TimeControl.ArenaRanked).Rated);
        Assert.False(MatchModes.Options(o, TimeControl.ArenaRanked).GuestsAllowed);
        Assert.True(MatchModes.Options(o, TimeControl.ArenaCasual).GuestsAllowed);
        Assert.Equal(RatingPool.ArenaRanked, RatingPools.For(TimeControl.ArenaRanked));
        Assert.True(RatingPools.IsVisible(RatingPool.ArenaRanked));
        Assert.False(RatingPools.IsVisible(RatingPool.ArenaCasual));
    }
}
