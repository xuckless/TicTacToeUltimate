using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace TicTacToeUltimate.Shared.Services;

public sealed class GameClient : IAsyncDisposable
{
    private readonly NavigationManager _nav;
    private string _hubUrl;

    public HubConnection Hub { get; private set; }

    public GameClient(NavigationManager nav)
    {
        _nav = nav;
        _hubUrl = _nav.ToAbsoluteUri("/hubs/game").ToString();
        Hub = BuildHub(_hubUrl);
    }

    // Call this BEFORE wiring handlers (e.g., at OnInitialized)
    public void UseLocalhostDevPort(int httpPort = 5091)
    {
        _hubUrl = $"http://localhost:{httpPort}/hubs/game";
        RebuildHub();
    }

    public void SetHubUrl(string hubUrl)
    {
        _hubUrl = hubUrl;
        RebuildHub();
    }

    private HubConnection BuildHub(string url) =>
        new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect()
            .Build();

    private void RebuildHub()
    {
        // If you change URL after handlers are already bound in the page,
        // re-bind them to the new Hub instance in the page (see GameTest.razor).
        _ = Hub?.DisposeAsync();
        Hub = BuildHub(_hubUrl);
    }

    public async Task Connect(string gameId = "default")
    {
        if (Hub.State == HubConnectionState.Connected) return;

        await Hub.StartAsync();
        await Hub.InvokeAsync("Join", gameId);
    }

    public async Task Disconnect()
    {
        if (Hub.State != HubConnectionState.Disconnected)
            await Hub.StopAsync();
    }

    public Task Move(int row, int col, string gameId = "default")
        => Hub.InvokeAsync("Move", row, col, gameId);

    public Task NewGame(string gameId = "default")
        => Hub.InvokeAsync("NewGame", gameId);

    public async ValueTask DisposeAsync()
    {
        if (Hub is not null)
            await Hub.DisposeAsync();
    }
}