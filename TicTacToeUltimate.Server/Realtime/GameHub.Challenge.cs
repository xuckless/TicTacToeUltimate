using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Server.Realtime;

// WP2 stub.
public sealed partial class GameHub
{
    public Task<ChallengeDto> CreateChallenge(CreateChallengeDto request) => throw new HubException("not_implemented");
    public Task<AcceptChallengeResultDto> AcceptChallenge(string code) => throw new HubException("not_implemented");
    public Task<CommandAckDto> DeclineChallenge(string code) => throw new HubException("not_implemented");
    public Task<CommandAckDto> CancelChallenge(string code) => throw new HubException("not_implemented");
}
