using Microsoft.Extensions.Time.Testing;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Tests;

public class SmokeTests
{
    [Fact]
    public void NullBotPoolNeverReserves()
    {
        var time = new FakeTimeProvider();
        Assert.False(new NullBotPool().TryReserve(TimeControl.CasualX2, 1200, out var r));
        Assert.Null(r);
        Assert.True(time.GetUtcNow() > DateTimeOffset.MinValue);
    }
}
