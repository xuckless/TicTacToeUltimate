using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Server.Realtime;

// WP2 stub.
public sealed partial class GameHub
{
    public Task<MatchSnapshotDto> JoinMatch(Guid matchId) => throw new HubException("not_implemented");
    public Task LeaveMatch(Guid matchId) => throw new HubException("not_implemented");
    public Task<MoveAckDto> SubmitAction(SubmitActionDto request) => throw new HubException("not_implemented");
    public Task<CommandAckDto> Resign(Guid matchId) => throw new HubException("not_implemented");
    public Task<CommandAckDto> SendEmote(Guid matchId, string emoteId) => throw new HubException("not_implemented");
    public Task<CommandAckDto> OfferRematch(Guid matchId) => throw new HubException("not_implemented");
}
