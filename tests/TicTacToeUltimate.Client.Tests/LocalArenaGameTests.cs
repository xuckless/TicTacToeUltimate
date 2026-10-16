using Microsoft.Extensions.Time.Testing;
using TicTacToeUltimate.Client.Local;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Arena;

namespace TicTacToeUltimate.Client.Tests;

public sealed class LocalArenaGameTests
{
    private static (LocalArenaGame G, FakeTimeProvider T) Make()
    {
        var t = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        return (new LocalArenaGame("Ana", "Ben", t), t);
    }

    [Fact]
    public void Starts_full_health_with_seat0_as_x()
    {
        var (g, _) = Make();
        Assert.Equal(ArenaRules.RulesetC.Hp, g.State.HpX);
        Assert.Equal(ArenaRules.RulesetC.Hp, g.State.HpO);
        Assert.Equal(0, g.ToMoveSeat);
        Assert.True(g.IsLive);
    }

    [Fact]
    public void A_line_shatters_and_damages_the_opponent()
    {
        var (g, _) = Make();
        foreach (var c in new[] { 0, 3, 1, 4, 2 }) Assert.True(g.Place(c));
        Assert.NotNull(g.LastHit);
        Assert.Equal(Color.X, g.LastHit!.Attacker);
        Assert.True(g.State.HpO < ArenaRules.RulesetC.Hp);
        Assert.Equal('.', g.Board[0]);
        Assert.True(g.IsLive);
    }

    [Fact]
    public void Timeout_auto_plays_a_move_and_advances_the_round_clock()
    {
        var (g, t) = Make();
        t.Advance(TimeSpan.FromMilliseconds(ArenaRules.RulesetC.MoveMs + 10));
        g.Tick();
        Assert.Equal(1, g.State.Ply);
        Assert.Equal(1, g.ToMoveSeat);
        Assert.Equal(ArenaRules.RulesetC.MoveMs, g.State.ElapsedMs);
    }

    [Fact]
    public void Paused_game_ignores_moves_and_timeouts()
    {
        var (g, t) = Make();
        g.TogglePause();
        Assert.False(g.Place(4));
        t.Advance(TimeSpan.FromSeconds(30));
        g.Tick();
        Assert.Equal(0, g.State.Ply);
    }

    [Fact]
    public void Rematch_swaps_colours_and_keeps_score()
    {
        var (g, _) = Make();
        g.Rematch();
        Assert.Equal(1, g.XSeat);
        Assert.Equal(1, g.ToMoveSeat);
        Assert.Equal(2, g.GameNumber);
    }
}
