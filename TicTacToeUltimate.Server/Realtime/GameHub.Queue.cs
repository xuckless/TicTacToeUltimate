using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Server.Realtime;

// WP2 stub.
public sealed partial class GameHub
{
    public Task<QueueTicketDto> EnterQueue(QueueRequestDto request) => throw new HubException("not_implemented");
    public Task LeaveQueue() => throw new HubException("not_implemented");
}
