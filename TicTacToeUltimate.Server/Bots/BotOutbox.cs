using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Contracts.Bot;

namespace TicTacToeUltimate.Server.Bots;

/// <summary>
/// Per-socket send queue. Every send (including the close frame) goes through one writer task,
/// so concurrent producers never call <see cref="WebSocket.SendAsync(ArraySegment{byte}, WebSocketMessageType, bool, CancellationToken)"/> concurrently.
/// </summary>
public sealed class BotOutbox
{
    private readonly WebSocket _socket;
    private readonly Channel<BotServerMessage> _channel =
        Channel.CreateUnbounded<BotServerMessage>(new UnboundedChannelOptions { SingleReader = true });
    private (WebSocketCloseStatus Status, string Description)? _close;
    private readonly object _gate = new();

    /// <summary>Starts the writer task.</summary>
    public BotOutbox(WebSocket socket, CancellationToken ct)
    {
        _socket = socket;
        Completion = Task.Run(() => WriteLoopAsync(ct), CancellationToken.None);
    }

    /// <summary>Completes when the writer has drained the queue and sent the close frame.</summary>
    public Task Completion { get; }

    /// <summary>Queues a message; false once the outbox is closing.</summary>
    public bool Enqueue(BotServerMessage message) => _channel.Writer.TryWrite(message);

    /// <summary>Requests a close after queued messages are flushed. The first request wins.</summary>
    public void Close(WebSocketCloseStatus status, string description)
    {
        lock (_gate)
        {
            _close ??= (status, description);
        }
        _channel.Writer.TryComplete();
    }

    /// <summary>Serializes one message as a UTF-8 JSON text frame payload.</summary>
    public static byte[] Serialize(BotServerMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, ContractsJsonContext.Default.BotServerMessage);

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_socket.State != WebSocketState.Open) continue;
                await _socket.SendAsync(Serialize(message), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            (WebSocketCloseStatus Status, string Description)? close;
            lock (_gate) close = _close;
            close ??= (WebSocketCloseStatus.NormalClosure, "bye");
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseOutputAsync(close.Value.Status, close.Value.Description, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            _channel.Writer.TryComplete();
        }
    }
}
