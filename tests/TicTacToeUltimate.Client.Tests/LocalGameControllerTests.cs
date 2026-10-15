using Microsoft.Extensions.Time.Testing;
using TicTacToeUltimate.Client.Local;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.Client.Tests;

public class LocalGameControllerTests
{
    private static (LocalGameController G, FakeTimeProvider T) Make(TimeControl tc = TimeControl.Unlimited, bool pie = false,
        string p1 = "", string p2 = "")
    {
        var t = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var g = new LocalGameController(new LocalGameOptions { Player1 = p1, Player2 = p2, TimeControl = tc, Pie = pie }, t, seed: 7);
        return (g, t);
    }

    private static void Play(LocalGameController g, params int[] cells)
    {
        foreach (var c in cells) Assert.Equal(MoveError.None, g.Place(c));
    }

    [Fact]
    public void Defaults_names_and_classic_rules()
    {
        var (g, _) = Make(p1: "  ", p2: "Ben");
        Assert.Equal("Player 1", g.NameOf(0));
        Assert.Equal("Ben", g.NameOf(1));
        Assert.Equal("n3k3-fifo7-v1-w0-none", g.State.Rules.Id);
        Assert.False(g.HasClock);
        Assert.Equal(1, g.GameNumber);
    }

    [Fact]
    public void Pie_option_uses_pie_ruleset()
    {
        var (g, _) = Make(pie: true);
        Assert.Equal("n3k3-fifo7-v1-w0-pie", g.State.Rules.Id);
    }

    [Fact]
    public void Turns_alternate_between_seats()
    {
        var (g, _) = Make();
        Assert.Equal(0, g.ToMoveSeat);
        Assert.Equal(Color.X, g.ColorOf(0));
        Play(g, 4);
        Assert.Equal(1, g.ToMoveSeat);
        Assert.Equal(Color.O, g.State.ToMove);
        Assert.Equal(MoveError.Occupied, g.Place(4));
        Assert.Equal(1, g.ToMoveSeat);
        Play(g, 0);
        Assert.Equal(0, g.ToMoveSeat);
        Assert.Equal("O...X....", g.Board);
        Assert.Equal(0, g.LastPlaced);
    }

    [Fact]
    public void Win_scores_the_seat_and_stops_input()
    {
        var (g, _) = Make();
        Play(g, 0, 3, 1, 4, 2);
        Assert.NotNull(g.Result);
        Assert.Equal(0, g.Result!.WinnerSeat);
        Assert.Equal(LocalEndReason.Line, g.Result.Reason);
        Assert.Equal(new[] { 0, 1, 2 }, g.Result.WinLine);
        Assert.Equal(new[] { 1, 0 }, g.Score);
        Assert.Null(g.Ghost);
        Assert.Equal(MoveError.GameOver, g.Place(5));
    }

    [Fact]
    public void Ghost_piece_is_next_to_vanish_and_cannot_complete_a_line()
    {
        var (g, _) = Make();
        Play(g, 0, 4, 1, 8, 6, 3);
        Assert.Null(g.Ghost);              // 6 pieces: the FIFO window (7) is not full, nothing vanishes yet
        Play(g, 2);                        // X 0-1-2, but 0 is the ghost (vanishes on the next placement)
        Assert.Null(g.Result);
        Assert.Equal(0, g.Ghost);          // drawn faded: it vanishes on O's placement
        Assert.Equal(1, g.ToMoveSeat);
        Play(g, 5);                        // O places; X's cell 0 vanishes
        Assert.Null(g.State.At(0));
        Assert.Equal(4, g.Ghost);          // now O's oldest piece is next
    }

    [Fact]
    public void Unlimited_never_times_out()
    {
        var (g, t) = Make(TimeControl.Unlimited);
        t.Advance(TimeSpan.FromHours(3));
        Assert.False(g.Tick());
        Assert.Null(g.Remaining);
        Assert.Null(g.ClockView);
        Assert.Null(g.Result);
        Assert.Equal(0, g.State.Placements);
    }

    [Theory]
    [InlineData(TimeControl.CasualX2, 30)]
    [InlineData(TimeControl.CasualX4, 60)]
    public void Casual_timeout_auto_moves(TimeControl tc, int seconds)
    {
        var (g, t) = Make(tc);
        var events = new List<LocalGameEvent>();
        g.Event += events.Add;
        Assert.Equal(TimeSpan.FromSeconds(seconds), g.Allowance);
        t.Advance(TimeSpan.FromSeconds(seconds - 1));
        Assert.False(g.Tick());
        t.Advance(TimeSpan.FromSeconds(1));
        Assert.True(g.Tick());
        Assert.Null(g.Result);
        Assert.Equal(1, g.State.Placements);
        Assert.Equal(1, g.ToMoveSeat);
        Assert.Contains(events, e => e.Kind == LocalEventKind.AutoMoved && e.Seat == 0);
        Assert.Equal(TimeSpan.FromSeconds(seconds), g.Remaining);   // fresh turn for seat 1
    }

    [Fact]
    public void Casual_timeout_during_pie_keeps_sides()
    {
        var (g, t) = Make(TimeControl.CasualX2, pie: true);
        Play(g, 4);
        Assert.True(g.AwaitingSwap);
        t.Advance(TimeSpan.FromSeconds(31));
        Assert.True(g.Tick());
        Assert.False(g.AwaitingSwap);
        Assert.False(g.State.Swapped);
        Assert.Equal(1, g.ToMoveSeat);
    }

    [Fact]
    public void Ranked_timeout_loses()
    {
        var (g, t) = Make(TimeControl.Ranked);
        Play(g, 4);
        t.Advance(TimeSpan.FromSeconds(15));
        Assert.True(g.Tick());
        Assert.NotNull(g.Result);
        Assert.Equal(LocalEndReason.Timeout, g.Result!.Reason);
        Assert.Equal(0, g.Result.WinnerSeat);
        Assert.Equal(new[] { 1, 0 }, g.Score);
    }

    [Fact]
    public void Ranked_schedule_shrinks_into_sudden_death()
    {
        var (g, t) = Make(TimeControl.Ranked);
        var events = new List<LocalGameEvent>();
        g.Event += events.Add;
        // Burn 60 s with 4 quick moves of 14 s plus 4 s.
        int[] moves = [0, 1, 2, 3];
        foreach (var m in moves) { t.Advance(TimeSpan.FromSeconds(14)); g.Tick(); Play(g, m); }
        t.Advance(TimeSpan.FromSeconds(4));
        Assert.False(g.Tick());
        Assert.True(g.SuddenDeath);
        Assert.Contains(events, e => e.Kind == LocalEventKind.SuddenDeath);
        Play(g, 4);
        Assert.Equal(TimeSpan.FromSeconds(10), g.Allowance);
    }

    [Fact]
    public void Pause_freezes_the_clock()
    {
        var (g, t) = Make(TimeControl.Ranked);
        t.Advance(TimeSpan.FromSeconds(5));
        g.Pause();
        t.Advance(TimeSpan.FromMinutes(5));
        Assert.False(g.Tick());
        Assert.Equal(TimeSpan.FromSeconds(10), g.Remaining);
        g.Resume();
        Assert.Equal(TimeSpan.FromSeconds(10), g.Remaining);
        Assert.Null(g.Result);
    }

    [Fact]
    public void Pie_swap_exchanges_seat_colours()
    {
        var (g, _) = Make(pie: true);
        Play(g, 4);
        Assert.Equal(1, g.ToMoveSeat);
        Assert.Equal(MoveError.None, g.DecideSwap(true));
        Assert.Equal(Color.X, g.ColorOf(1));
        Assert.Equal(Color.O, g.ColorOf(0));
        Assert.Equal(0, g.ToMoveSeat);       // the opener now plays O and moves
        Assert.Equal(Color.O, g.State.ToMove);
    }

    [Fact]
    public void Rematch_swaps_colours_and_keeps_score()
    {
        var (g, _) = Make();
        Play(g, 0, 3, 1, 4, 2);              // seat 0 wins as X
        g.Rematch();
        Assert.Equal(2, g.GameNumber);
        Assert.Null(g.Result);
        Assert.Equal(1, g.XSeat);
        Assert.Equal(Color.O, g.ColorOf(0));
        Assert.Equal(Color.X, g.ColorOf(1));
        Assert.Equal(1, g.ToMoveSeat);        // Player 2 opens as X
        Play(g, 0, 3, 1, 4, 2);              // seat 1 wins as X
        Assert.Equal(1, g.Result!.WinnerSeat);
        Assert.Equal(new[] { 1, 1 }, g.Score);
        g.Rematch();
        Assert.Equal(0, g.ToMoveSeat);
    }

    [Fact]
    public void Resign_gives_the_game_to_the_other_seat()
    {
        var (g, _) = Make();
        Play(g, 4);
        g.Resign();
        Assert.Equal(0, g.Result!.WinnerSeat);
        Assert.Equal(LocalEndReason.Resign, g.Result.Reason);
    }

    [Fact]
    public void Session_holds_current_controller()
    {
        var s = new LocalPlaySession(TimeProvider.System);
        Assert.Null(s.Current);
        var g = s.Start(new LocalGameOptions { Player1 = "Ana" });
        Assert.Same(g, s.Current);
        Assert.Equal("Ana", g.NameOf(0));
        s.End();
        Assert.Null(s.Current);
    }
}
