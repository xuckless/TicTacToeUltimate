using TicTacToeUltimate.Client.State;

namespace TicTacToeUltimate.Client.Tests;

public class SmokeTests
{
    [Fact]
    public void InitialStatesAreEmpty()
    {
        Assert.Equal(SessionStatus.Initializing, SessionState.Initial.Status);
        Assert.Equal(QueuePhase.Idle, QueueState.Idle.Phase);
        Assert.Empty(MatchesState.Empty.Matches);
    }
}
