using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Server.Realtime;

// WP2 stub.
public sealed partial class GameHub
{
    private partial Task OnConnectedSessionAsync() => Task.CompletedTask;
    private partial Task OnDisconnectedSessionAsync(Exception? exception) => Task.CompletedTask;

    public Task<ServerHelloDto> Hello(ClientHelloDto hello) => throw new HubException("not_implemented");
    public Task Pong(int pingId) => throw new HubException("not_implemented");
    public Task<long> ServerTime() => throw new HubException("not_implemented");
}
