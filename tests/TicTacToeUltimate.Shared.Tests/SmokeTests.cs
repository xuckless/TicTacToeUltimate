using Bunit;
using TicTacToeUltimate.Shared;

namespace TicTacToeUltimate.Shared.Tests;

public class SmokeTests
{
    [Fact]
    public void RoutesPlaceholderRenders()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<Routes>();
        Assert.Contains("Rebuilding", cut.Markup, StringComparison.Ordinal);
    }
}
