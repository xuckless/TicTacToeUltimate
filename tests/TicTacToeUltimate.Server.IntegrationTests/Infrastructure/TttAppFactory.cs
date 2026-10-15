using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.IntegrationTests.Infrastructure;

/// <summary>Real host: InMemory persistence, Dev auth, FakeTimeProvider in DI.</summary>
public sealed class TttAppFactory : WebApplicationFactory<Program>
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Ttt:Persistence:Provider", "InMemory");
        builder.UseSetting("Ttt:Auth:Dev:Enabled", "true");
        builder.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Time));
    }

    /// <summary>Skips the calling test unless WP1-WP3 are merged (real gateway and dev auth present).</summary>
    public void RequirePlatform()
    {
        Assert.SkipWhen(Services.GetService<IMatchGateway>() is null, "E2E: needs WP2 IMatchGateway (runs after WP1-WP3 merge)");
    }

    public HttpClient Http(string? token = null)
    {
        var c = CreateClient();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public static string NewUsername(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    public async Task<TestPlayer> SignInAsync(string username)
    {
        using var http = Http();
        var res = await http.PostAsJsonAsync("api/auth/dev/token", new DevTokenRequestDto(null, username), ContractsJsonContext.Default.Options);
        Assert.SkipWhen(res.StatusCode == HttpStatusCode.NotFound, "E2E: dev auth not available (needs WP3)");
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<TokenResponseDto>(ContractsJsonContext.Default.Options))!;
        return await TestPlayer.ConnectAsync(this, token.AccessToken, token.Me, username);
    }

    public async Task<TestPlayer> GuestAsync()
    {
        using var http = Http();
        var res = await http.PostAsJsonAsync("api/auth/guest", new GuestTokenRequestDto(Guid.NewGuid().ToString()), ContractsJsonContext.Default.Options);
        Assert.SkipWhen(res.StatusCode == HttpStatusCode.NotFound, "E2E: guest auth not available (needs WP3)");
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<TokenResponseDto>(ContractsJsonContext.Default.Options))!;
        return await TestPlayer.ConnectAsync(this, token.AccessToken, token.Me, null);
    }

    /// <summary>Advances fake time in steps until the condition holds.</summary>
    public async Task PumpUntilAsync(Func<bool> condition, TimeSpan step, int maxSteps = 200)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            Time.Advance(step);
            await Task.Delay(15);
        }
        Assert.True(condition(), "condition not reached while pumping fake time");
    }

    /// <summary>Waits in real time (no fake time advance).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
        Assert.True(condition(), "condition not reached in time");
    }
}

/// <summary>An authenticated user with a live hub connection (LongPolling over the TestServer handler).</summary>
public sealed class TestPlayer : IAsyncDisposable
{
    private readonly TttAppFactory _factory;
    public string Token { get; }
    public MeDto Me { get; }
    public string? Username { get; }
    public HubConnection Hub { get; private set; } = null!;
    public ConcurrentQueue<MatchFoundDto> Found { get; } = new();
    public ConcurrentQueue<MatchEventEnvelope> Events { get; } = new();
    public ConcurrentQueue<PostGameSummaryDto> Summaries { get; } = new();
    public ConcurrentQueue<DirectMessageDto> Messages { get; } = new();

    private TestPlayer(TttAppFactory factory, string token, MeDto me, string? username)
    {
        _factory = factory; Token = token; Me = me; Username = username;
    }

    public Guid Id => Me.UserId;

    public static async Task<TestPlayer> ConnectAsync(TttAppFactory factory, string token, MeDto me, string? username)
    {
        var p = new TestPlayer(factory, token, me, username);
        await p.ReconnectAsync();
        return p;
    }

    public async Task ReconnectAsync()
    {
        if (Hub is not null) await Hub.DisposeAsync();
        var server = _factory.Server;
        Hub = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, HubRoutes.Game.TrimStart('/')), o =>
            {
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(Token);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default))
            .Build();
        Hub.On<MatchFoundDto>(nameof(IGameClient.MatchFound), Found.Enqueue);
        Hub.On<MatchEventEnvelope>(nameof(IGameClient.MatchEvent), Events.Enqueue);
        Hub.On<PostGameSummaryDto>(nameof(IGameClient.PostGameSummary), Summaries.Enqueue);
        Hub.On<DirectMessageDto>(nameof(IGameClient.DirectMessage), Messages.Enqueue);
        Hub.On<int>(nameof(IGameClient.Ping), id => _ = Hub.InvokeAsync(nameof(IGameHub.Pong), id));
        await Hub.StartAsync();
        await Hub.InvokeAsync<ServerHelloDto>(nameof(IGameHub.Hello),
            new ClientHelloDto(ProtocolInfo.Version, "e2e", ClientPlatform.Web, "UTC", null));
    }

    public Task<T> InvokeAsync<T>(string method, params object?[] args) => Hub.InvokeCoreAsync<T>(method, args);

    public IEnumerable<MatchEventEnvelope> EventsFor(Guid matchId) => Events.Where(e => e.MatchId == matchId);
    public bool HasEnded(Guid matchId) => EventsFor(matchId).Any(e => e.Event is MatchEnded);

    public async ValueTask DisposeAsync()
    {
        if (Hub is not null) await Hub.DisposeAsync();
    }
}
