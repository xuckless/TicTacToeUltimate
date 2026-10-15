using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace TicTacToeUltimate.Shared.Services;

/// <summary>
/// Provides a client for managing SignalR connections to the game hub.
/// </summary>
public sealed class GameClient : IAsyncDisposable
{
    private readonly NavigationManager _nav;
    private string _hubUrl;

    /// <summary>
    /// Gets the SignalR hub connection instance.
    /// </summary>
    public HubConnection Hub { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GameClient"/> class.
    /// </summary>
    /// <param name="nav">The navigation manager for generating hub URLs.</param>
    public GameClient(NavigationManager nav)
    {
        _nav = nav;
        _hubUrl = _nav.ToAbsoluteUri("/hubs/game").ToString();
        Hub = BuildHub(_hubUrl);
    }

    /// <summary>
    /// Configures the hub to use localhost with the specified port for development.
    /// Call this BEFORE wiring handlers (e.g., at OnInitialized).
    /// </summary>
    /// <param name="httpPort">The HTTP port to use. Default is 5091.</param>
    public void UseLocalhostDevPort(int httpPort = 5091)
    {
        _hubUrl = $"http://localhost:{httpPort}/hubs/game";
        RebuildHub();
    }

    /// <summary>
    /// Sets a custom hub URL and rebuilds the connection.
    /// </summary>
    /// <param name="hubUrl">The new hub URL to use.</param>
    public void SetHubUrl(string hubUrl)
    {
        _hubUrl = hubUrl;
        RebuildHub();
    }

    /// <summary>
    /// Builds a new SignalR hub connection with the specified URL.
    /// </summary>
    /// <param name="url">The hub URL.</param>
    /// <returns>A configured HubConnection instance.</returns>
    private HubConnection BuildHub(string url) =>
        new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect()
            .Build();

    /// <summary>
    /// Rebuilds the hub connection with the current URL.
    /// If you change URL after handlers are already bound in the page,
    /// re-bind them to the new Hub instance in the page (see GameTest.razor).
    /// </summary>
    private void RebuildHub()
    {
        _ = Hub?.DisposeAsync();
        Hub = BuildHub(_hubUrl);
    }

    /// <summary>
    /// Connects to the game hub and joins the specified game.
    /// </summary>
    /// <param name="gameId">The game ID to join. Default is "default".</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task Connect(string gameId = "default")
    {
        if (Hub.State == HubConnectionState.Connected) return;

        await Hub.StartAsync();
        await Hub.InvokeAsync("Join", gameId);
    }

    /// <summary>
    /// Disconnects from the game hub.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task Disconnect()
    {
        if (Hub.State != HubConnectionState.Disconnected)
            await Hub.StopAsync();
    }

    /// <summary>
    /// Sends a move command to the game hub.
    /// </summary>
    /// <param name="row">The row index of the move.</param>
    /// <param name="col">The column index of the move.</param>
    /// <param name="gameId">The game ID. Default is "default".</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task Move(int row, int col, string gameId = "default")
        => Hub.InvokeAsync("Move", row, col, gameId);

    /// <summary>
    /// Requests a new game from the game hub.
    /// </summary>
    /// <param name="gameId">The game ID. Default is "default".</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task NewGame(string gameId = "default")
        => Hub.InvokeAsync("NewGame", gameId);

    /// <summary>
    /// Disposes the hub connection asynchronously.
    /// </summary>
    /// <returns>A ValueTask representing the asynchronous disposal operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Hub is not null)
            await Hub.DisposeAsync();
    }
}
