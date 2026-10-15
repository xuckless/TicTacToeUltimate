using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.Realtime;

[Authorize]
public sealed partial class GameHub(IServiceProvider services, ILogger<GameHub> logger) : Hub<IGameClient>, IGameHub
{
    private TttUser Me => Context.User!.ToTttUser();

    public override async Task OnConnectedAsync()
    {
        await OnConnectedSessionAsync();     // WP2, GameHub.Session.cs (presence, pinger registration)
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await OnDisconnectedSessionAsync(exception);   // WP2, GameHub.Session.cs (detach from matches, presence)
        await base.OnDisconnectedAsync(exception);
    }

    private partial Task OnConnectedSessionAsync();
    private partial Task OnDisconnectedSessionAsync(Exception? exception);
}
