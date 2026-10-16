using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Contracts.Bot;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Bots;

namespace TicTacToeUltimate.Server.IntegrationTests.Bots;

public sealed class BotKeyFormatTests
{
    [Fact]
    public void ParsesDevKey()
    {
        Assert.True(BotKeyFormat.TryParse("tttb_devkey01_devsecret01", out var id, out var secret));
        Assert.Equal(("devkey01", "devsecret01"), (id, secret));
        Assert.True(BotKeyFormat.TryParse("tttb_abc_sec_ret", out id, out secret));
        Assert.Equal(("abc", "sec_ret"), (id, secret));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("tttb_")] [InlineData("tttb__x")] [InlineData("tttb_abc_")] [InlineData("eyJhbGciOi")]
    public void RejectsMalformed(string? key) => Assert.False(BotKeyFormat.TryParse(key, out _, out _));

    [Fact]
    public void VerifyIsHashBased()
    {
        var hash = BotKeyFormat.Hash("s3cret");
        Assert.True(BotKeyFormat.Verify("s3cret", hash));
        Assert.False(BotKeyFormat.Verify("s3creT", hash));
    }

    [Fact]
    public void GuidV5MatchesRfcVector()
    {
        // Python: uuid.uuid5(uuid.NAMESPACE_DNS, "python.org")
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        Assert.Equal(new Guid("886313e1-3b8a-5372-9b90-0c9aee199e5d"), GuidV5.Create(dns, "python.org"));
        Assert.Equal(BotKeyResolver.DevBotUserId("devkey01"), BotKeyResolver.DevBotUserId("devkey01"));
    }
}

public sealed class BotPoolTests
{
    private sealed class FakeSeeker(Guid id, TimeControl tc, int capacity) : IBotSeeker
    {
        public int Reserved;
        public List<Guid> Started { get; } = [];
        public Guid BotUserId { get; } = id;
        public string BotName => "fake";
        public bool Trusted => true;
        public bool TryReserveSlot(TimeControl timeControl)
        {
            if (timeControl != tc || Reserved >= capacity) return false;
            Reserved++;
            return true;
        }
        public void ReleaseSlot() => Reserved--;
        public ValueTask StartReservedMatchAsync(Guid matchId, CancellationToken ct) { Reserved--; Started.Add(matchId); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task ReserveReleaseNotify()
    {
        var pool = new BotPool(NullLogger<BotPool>.Instance);
        var seeker = new FakeSeeker(Guid.NewGuid(), TimeControl.CasualX2, 1);
        Assert.False(pool.TryReserve(TimeControl.CasualX2, 1200, out _));
        pool.Register(seeker);
        Assert.False(pool.TryReserve(TimeControl.CasualX4, 1200, out _));
        Assert.True(pool.TryReserve(TimeControl.CasualX2, 1200, out var r));
        Assert.Equal(seeker.BotUserId, r.BotUserId);
        Assert.False(pool.TryReserve(TimeControl.CasualX2, 1200, out _));
        pool.Release(seeker.BotUserId);
        Assert.True(pool.TryReserve(TimeControl.CasualX2, 1200, out _));
        var match = Guid.NewGuid();
        await pool.NotifyMatchCreatedAsync(seeker.BotUserId, match, CancellationToken.None);
        Assert.Equal([match], seeker.Started);
        pool.Unregister(seeker);
        Assert.Equal(0, pool.Count);
        await pool.NotifyMatchCreatedAsync(seeker.BotUserId, Guid.NewGuid(), CancellationToken.None);   // no throw
    }
}

public sealed class BotOutboxTests
{
    /// <summary>Records frames and fails if two SendAsync calls ever overlap.</summary>
    private sealed class OverlapDetectingSocket : WebSocket
    {
        private int _inSend;
        public int Overlaps;
        public List<byte[]> Frames { get; } = [];
        public WebSocketCloseStatus? Closed;
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => Closed;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            await EnterAsync();
            Closed = closeStatus;
            _state = WebSocketState.CloseSent;
            Interlocked.Decrement(ref _inSend);
        }
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Task.FromException<WebSocketReceiveResult>(new NotSupportedException());
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            await EnterAsync();
            lock (Frames) Frames.Add(buffer.ToArray());
            Interlocked.Decrement(ref _inSend);
        }
        private async Task EnterAsync()
        {
            if (Interlocked.Increment(ref _inSend) != 1) Interlocked.Increment(ref Overlaps);
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task ConcurrentEnqueues_AreSerializedOnTheSocket()
    {
        var socket = new OverlapDetectingSocket();
        var outbox = new BotOutbox(socket, CancellationToken.None);
        const int producers = 16, perProducer = 25;
        await Task.WhenAll(Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++) Assert.True(outbox.Enqueue(new BotPing(p * 1000 + i)));
        })));
        outbox.Close((WebSocketCloseStatus)BotCloseCodes.PingTimeout, "test");
        await outbox.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(0, socket.Overlaps);
        Assert.Equal(producers * perProducer, socket.Frames.Count);
        Assert.Equal((WebSocketCloseStatus)BotCloseCodes.PingTimeout, socket.Closed);
        Assert.False(outbox.Enqueue(new BotPing(1)));
        var parsed = socket.Frames.Select(f => (BotPing)JsonSerializer.Deserialize(f, ContractsJsonContext.Default.BotServerMessage)!).ToList();
        // per-producer FIFO order is preserved
        foreach (var g in parsed.GroupBy(p => p.T / 1000)) Assert.Equal(g.Select(p => p.T).Order(), g.Select(p => p.T));
    }

    [Fact]
    public void WireFormat_MatchesProtocolDoc()
    {
        var json = System.Text.Encoding.UTF8.GetString(BotOutbox.Serialize(new BotAck("m-17", null, 10)));
        Assert.Equal("{\"type\":\"ack\",\"id\":\"m-17\",\"seq\":10}", json);
        var action = JsonSerializer.Deserialize("{\"type\":\"action\",\"id\":\"m-18\",\"matchId\":\"" + Guid.Empty + "\",\"ply\":1,\"kind\":\"Swap\"}",
            ContractsJsonContext.Default.BotClientMessage);
        var a = Assert.IsType<BotAction>(action);
        Assert.Equal(TicTacToeUltimate.Engine.ActionKind.Swap, a.Kind);
        Assert.Null(a.Cell);
    }
}
