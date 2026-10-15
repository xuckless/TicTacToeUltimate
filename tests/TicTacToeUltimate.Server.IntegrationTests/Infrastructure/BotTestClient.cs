using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Contracts.Bot;

namespace TicTacToeUltimate.Server.IntegrationTests.Infrastructure;

/// <summary>Thin bot-protocol client over a TestServer WebSocket.</summary>
public sealed class BotTestClient : IAsyncDisposable
{
    public const string DevKey = "tttb_devkey01_devsecret01";
    private readonly WebSocket _socket;
    private readonly byte[] _buffer = new byte[64 * 1024];

    private BotTestClient(WebSocket socket) => _socket = socket;

    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    public static async Task<BotTestClient> ConnectAsync(TestServer server, string? key = DevKey, string query = "?protocol=1")
    {
        var client = server.CreateWebSocketClient();
        if (key is not null) client.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + key;
        var socket = await client.ConnectAsync(new Uri(server.BaseAddress, BotRoutes.WebSocket.TrimStart('/') + query), CancellationToken.None);
        return new BotTestClient(socket);
    }

    public Task SendAsync(BotClientMessage message) =>
        _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, ContractsJsonContext.Default.BotClientMessage),
            WebSocketMessageType.Text, true, CancellationToken.None);

    public Task SendRawAsync(string json) =>
        _socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Next message, or null when the server closed the socket.</summary>
    public async Task<BotServerMessage?> ReceiveAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await _socket.ReceiveAsync(_buffer, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(_buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize(ms.ToArray(), ContractsJsonContext.Default.BotServerMessage);
    }

    public async Task<T> ReceiveAsync<T>(TimeSpan? timeout = null) where T : BotServerMessage
    {
        var m = await ReceiveAsync(timeout);
        return m as T ?? throw new Xunit.Sdk.XunitException($"Expected {typeof(T).Name}, got {m?.GetType().Name ?? "close"}: {m}");
    }

    /// <summary>Answers pings with pongs and returns the next message of type <typeparamref name="T"/>.</summary>
    public async Task<T> ExpectAsync<T>(TimeSpan? timeout = null) where T : BotServerMessage
    {
        while (true)
        {
            var m = await ReceiveAsync(timeout);
            if (m is BotPing ping) { await SendAsync(new BotPong(ping.T)); continue; }
            return m as T ?? throw new Xunit.Sdk.XunitException($"Expected {typeof(T).Name}, got {m?.GetType().Name ?? "close"}: {m}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch (WebSocketException) { }
        _socket.Dispose();
    }
}
