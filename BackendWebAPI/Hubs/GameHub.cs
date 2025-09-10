using BackendWebAPI.GameEngine;
using BackendWebAPI.Services;
using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Shared.Models;

namespace BackendWebAPI.Hubs;

public class GameHub(IGameEngine engine, RedisStore store, ILogger<GameHub> log) : Hub
{
    private static GameSnapshotDto ToSnapshot(GameState s) => new()
    {
        RenderString = s.RenderString,   // no recompute here
        Victory      = s.Victory,
        VictoryRow   = s.VictoryRow
    };

    private static bool IsInBounds(int row, int col)
        => row is >= 1 and <= 3 && col is >= 1 and <= 3;

    [HubMethodName("Join")]
    public async Task Join(string gameId = "default")
    {
        try
        {
            // put this connection in the game's group so we can broadcast to that game only
            await Groups.AddToGroupAsync(Context.ConnectionId, gameId);

            var state = await store.GetOrCreateAsync(gameId, engine);
            await Clients.Caller.SendAsync("Snapshot", ToSnapshot(state));

            log.LogInformation("Client {Conn} joined game '{GameId}'", Context.ConnectionId, gameId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Join failed for {Conn} in game '{GameId}'", Context.ConnectionId, gameId);
            await Clients.Caller.SendAsync("Error", $"Join failed: {ex.Message}");
        }
    }

    [HubMethodName("Move")]
    public async Task Move(int row, int col, string gameId = "default")
    {
        if (!IsInBounds(row, col))
        {
            await Clients.Caller.SendAsync("Error", "Move out of bounds (row/col must be 1..3).");
            return;
        }

        try
        {
            var state = await store.GetOrCreateAsync(gameId, engine);
            var before = string.Join(';', state.Moves);   // or: var before = state.RenderString;
            state = engine.ApplyMove(state, row, col, state.Turn);
            var after  = string.Join(';', state.Moves);   // or: var after  = state.RenderString;

            if (after == before)
            {
                await Clients.Caller.SendAsync("Error", "Cell is occupied on the current board.");
                return;
            }

            await store.SaveAsync(gameId, state);
            await Clients.Group(gameId).SendAsync("Snapshot", ToSnapshot(state));
            
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Move failed for {Conn} in game '{GameId}' ({Row},{Col})",
                Context.ConnectionId, gameId, row, col);
            await Clients.Caller.SendAsync("Error", $"Move failed: {ex.Message}");
        }
    }

    [HubMethodName("NewGame")]
    public async Task NewGame(string gameId = "default")
    {
        try
        {
            var state = await store.ResetAsync(gameId, engine);
            await Clients.Group(gameId).SendAsync("Snapshot", ToSnapshot(state));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "NewGame failed for {Conn} in game '{GameId}'", Context.ConnectionId, gameId);
            await Clients.Caller.SendAsync("Error", $"NewGame failed: {ex.Message}");
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // optional: remove from all groups if you track them; SignalR auto-clears on disconnect
        if (exception is not null)
            log.LogWarning(exception, "Client {Conn} disconnected with error.", Context.ConnectionId);
        else
            log.LogInformation("Client {Conn} disconnected.", Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }
}