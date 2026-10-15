using TicTacToeUltimate.Engine;

namespace TicTacToeUltimate.Engine.Tests;

public class SmokeTests
{
    [Fact]
    public void DefaultRuleSetIdsMatchSpec()
    {
        Assert.Equal("n3k3-cap3-v0-w1-none", RuleSet.Default.Id);
        Assert.Equal("n3k3-cap3-v0-w1-pie", RuleSet.RankedDefault.Id);
        Assert.True(RuleSet.Default.VanishingPieceCanWin);
        Assert.False(RuleSet.Default.VacatedCellPlayable);
        Assert.Equal(RemovalKind.PerPlayerCap, RuleSet.Default.Removal);
    }
}
