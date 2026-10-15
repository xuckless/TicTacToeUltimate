using TicTacToeUltimate.Engine;

namespace TicTacToeUltimate.Engine.Tests;

public class SmokeTests
{
    [Fact]
    public void DefaultRuleSetIdsMatchSpec()
    {
        Assert.Equal("n3k3-fifo7-v1-w0-none", RuleSet.Default.Id);
        Assert.Equal("n3k3-fifo7-v1-w0-pie", RuleSet.RankedDefault.Id);
        Assert.False(RuleSet.Default.VanishingPieceCanWin);
    }
}
