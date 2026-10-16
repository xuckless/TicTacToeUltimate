using TicTacToeUltimate.Engine;

namespace TicTacToeUltimate.Engine.Tests;

/// <summary>"Old school" mode: plain tic-tac-toe, pieces never vanish, full board = draw.</summary>
public class OldSchoolTests
{
    [Fact]
    public void Id_round_trips_and_validates()
    {
        Assert.Equal("n3k3-none-v1-w1-none", RuleSet.OldSchool.Id);
        Assert.True(RuleSet.TryParse(RuleSet.OldSchool.Id, out var r));
        Assert.Equal(RuleSet.OldSchool, r);
        Assert.Empty(RuleSet.OldSchool.Validate());
    }

    [Fact]
    public void Full_board_without_a_line_is_a_draw()
    {
        // X O X / X O O / O X X  (no line)
        var s = Rules.Replay(RuleSet.OldSchool, Notation.Parse("0,1,2,4,3,5,7,6,8"));
        Assert.Equal(GamePhase.Ended, s.Phase);
        Assert.Equal(Outcome.Draw, s.Outcome);
        Assert.Equal(9, s.Pieces.Length);
    }

    [Fact]
    public void Line_wins_and_pieces_never_vanish()
    {
        var s = Rules.Replay(RuleSet.OldSchool, Notation.Parse("0,3,1,4,2"));
        Assert.Equal(Outcome.XWins, s.Outcome);
        Assert.Equal(5, s.Pieces.Length);
    }
}
