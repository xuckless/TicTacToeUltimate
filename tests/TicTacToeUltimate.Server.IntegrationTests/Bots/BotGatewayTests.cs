using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TicTacToeUltimate.Contracts.Bot;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Bots;
using TicTacToeUltimate.Server.IntegrationTests.Infrastructure;

namespace TicTacToeUltimate.Server.IntegrationTests.Bots;

/// <summary>Bot gateway against the real host with a fake IMatchGateway (passes without WP1-WP3).</summary>
public sealed class BotGatewayTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private FakeMatchGateway _gateway = null!;

    public ValueTask InitializeAsync()
    {
        _gateway = new FakeMatchGateway();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IMatchGateway>(_gateway);
            s.AddSingleton(new BotSessionSettings(TimeSpan.FromMilliseconds(200), 3, 64 * 1024));
        }));
        _ = _factory.Server;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private Guid DevBotId => BotKeyResolver.DevBotUserId("devkey01");

    [Fact]
    public async Task BadKey_Closes4401()
    {
        await using var bot = await BotTestClient.ConnectAsync(_factory.Server, "tttb_devkey01_wrongsecret");
        Assert.Null(await bot.ReceiveAsync());
        Assert.Equal((WebSocketCloseStatus)BotCloseCodes.BadKey, bot.CloseStatus);
    }

    [Fact]
    public async Task MissingKey_Closes4401()
    {
        await using var bot = await BotTestClient.ConnectAsync(_factory.Server, key: null);
        Assert.Null(await bot.ReceiveAsync());
        Assert.Equal((WebSocketCloseStatus)BotCloseCodes.BadKey, bot.CloseStatus);
    }

    [Fact]
    public async Task ProtocolMismatch_Closes4426()
    {
        await using var bot = await BotTestClient.ConnectAsync(_factory.Server, query: "?protocol=2");
        Assert.Null(await bot.ReceiveAsync());
        Assert.Equal((WebSocketCloseStatus)BotCloseCodes.ProtocolMismatch, bot.CloseStatus);
    }

    [Fact]
    public async Task MissedPings_Close4408()
    {
        await using var bot = await BotTestClient.ConnectAsync(_factory.Server);
        await bot.ReceiveAsync<BotHello>();
        var pings = 0;
        while (await bot.ReceiveAsync() is { } m) { if (m is BotPing) pings++; }
        Assert.Equal(3, pings);
        Assert.Equal((WebSocketCloseStatus)BotCloseCodes.PingTimeout, bot.CloseStatus);
    }

    [Fact]
    public async Task Hello_Seek_MalformedAndUnknownMatch()
    {
        await using var bot = await BotTestClient.ConnectAsync(_factory.Server);
        var hello = await bot.ReceiveAsync<BotHello>();
        Assert.Equal(1, hello.Protocol);
        Assert.Equal(DevBotId, hello.BotId);
        Assert.Equal("dev_bot", hello.BotName);

        await bot.SendRawAsync("{\"type\":\"nope\"}");
        Assert.Equal("validation", (await bot.ExpectAsync<BotError>()).Code);

        await bot.SendAsync(new BotSeek("s1", [], 1));
        Assert.Equal("validation", (await bot.ExpectAsync<BotError>()).Code);

        await bot.SendAsync(new BotAction("m1", Guid.NewGuid(), 0, ActionKind.Place, 4));
        var err = await bot.ExpectAsync<BotError>();
        Assert.Equal("m1", err.Id);
        Assert.Equal("match_not_found", err.Code);
    }

    [Fact]
    public async Task Seek_Reserve_FullGame_AckBeforeState_StalePly()
    {
        var pool = _factory.Services.GetRequiredService<BotPool>();
        Assert.Same(pool, _factory.Services.GetRequiredService<IBotPool>());
        await using var bot = await BotTestClient.ConnectAsync(_factory.Server);
        await bot.ReceiveAsync<BotHello>();
        await bot.SendAsync(new BotSeek("s1", [TimeControl.CasualX2], 1));
        Assert.Equal("s1", (await bot.ExpectAsync<BotAck>()).Id);

        Assert.False(pool.TryReserve(TimeControl.Ranked, 1200, out _));
        Assert.True(pool.TryReserve(TimeControl.CasualX2, 1200, out var reservation));
        Assert.Equal(DevBotId, reservation.BotUserId);
        Assert.False(pool.TryReserve(TimeControl.CasualX2, 1200, out _));   // capacity 1

        var human = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        var matchId = await _gateway.CreateMatchAsync(FakeMatchGateway.Setup(human, DevBotId), ct);
        await pool.NotifyMatchCreatedAsync(DevBotId, matchId, ct);

        var start = await bot.ExpectAsync<BotGameStart>();
        Assert.Equal(matchId, start.MatchId);
        Assert.Equal(1, start.Seat);
        Assert.Equal(Color.O, start.You);
        Assert.Equal("human", start.Opponent.DisplayName);
        Assert.Equal("n3k3-fifo7-v1-w0-none", start.Rules.Id);
        var s0 = await bot.ExpectAsync<BotState>();
        Assert.False(s0.YourTurn);
        Assert.Equal(0, s0.Ply);

        await _gateway.SubmitActionAsync(matchId, human, 0, GameAction.Place(0), Guid.NewGuid(), ct);
        var s1 = await bot.ExpectAsync<BotState>();
        Assert.True(s1.YourTurn);
        Assert.Equal(1, s1.Ply);
        Assert.Equal("X........", s1.Board);
        Assert.Equal("0", s1.Actions);
        Assert.DoesNotContain(0, s1.Legal);

        await bot.SendAsync(new BotAction("m1", matchId, 1, ActionKind.Place, 0));
        var occupied = await bot.ExpectAsync<BotError>();
        Assert.Equal(("m1", "occupied"), (occupied.Id, occupied.Code));

        await bot.SendAsync(new BotAction("m2", matchId, 0, ActionKind.Place, 3));
        Assert.Equal("stale_ply", (await bot.ExpectAsync<BotError>()).Code);

        await bot.SendAsync(new BotAction("m3", matchId, 1, ActionKind.Place, 3));
        var ack = await bot.ExpectAsync<BotAck>();       // ack strictly before the resulting state
        Assert.Equal("m3", ack.Id);
        var s2 = await bot.ExpectAsync<BotState>();
        Assert.Equal(2, s2.Ply);
        Assert.False(s2.YourTurn);
        Assert.Equal(ack.Seq, s2.Seq);
        Assert.Equal("0,3", s2.Actions);

        await _gateway.SubmitActionAsync(matchId, human, 2, GameAction.Place(1), Guid.NewGuid(), ct);
        var s3 = await bot.ExpectAsync<BotState>();
        await bot.SendAsync(new BotAction("m4", matchId, s3.Ply, ActionKind.Place, 4));
        await bot.ExpectAsync<BotAck>();
        await bot.ExpectAsync<BotState>();
        await _gateway.SubmitActionAsync(matchId, human, 4, GameAction.Place(2), Guid.NewGuid(), ct);

        var final = await bot.ExpectAsync<BotState>();
        Assert.Equal(GamePhase.Ended, final.Phase);
        Assert.False(final.YourTurn);
        var end = await bot.ExpectAsync<BotGameEnd>();
        Assert.Equal(Outcome.XWins, end.Outcome);
        Assert.Equal([0, 1, 2], end.WinLine);
        Assert.False(end.YouWon);

        Assert.True(pool.TryReserve(TimeControl.CasualX2, 1200, out _));   // slot freed after gameEnd
        pool.Release(DevBotId);
    }

    [Fact]
    public async Task Disconnect_RemovesFromPool()
    {
        var pool = _factory.Services.GetRequiredService<BotPool>();
        var bot = await BotTestClient.ConnectAsync(_factory.Server);
        await bot.ReceiveAsync<BotHello>();
        await bot.SendAsync(new BotSeek("s1", [TimeControl.Unlimited], 2));
        await bot.ExpectAsync<BotAck>();
        Assert.Equal(1, pool.Count);
        await bot.DisposeAsync();
        for (var i = 0; i < 100 && pool.Count > 0; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(0, pool.Count);
    }
}
