using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Server.Realtime;

// WP3 stub.
public sealed partial class GameHub
{
    public Task<DirectMessageDto> SendDirectMessage(SendDirectMessageDto request) => throw new HubException("not_implemented");
    public Task MarkConversationRead(Guid friendId, long upToMessageId) => throw new HubException("not_implemented");
}
