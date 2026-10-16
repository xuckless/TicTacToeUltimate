using System.Net;
using System.Net.Http.Json;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.IntegrationTests.Infrastructure;

namespace TicTacToeUltimate.Server.IntegrationTests.E2E;

/// <summary>Anti-abuse guards added 2026-10-16 (audit C1/C2/M3).</summary>
public sealed class SecurityGuardsTests : IAsyncLifetime
{
    private readonly TttAppFactory _factory = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task UserBotKeys_AreOff_AndLiveListIsAccountsOnly()
    {
        _factory.RequirePlatform();
        var human = await _factory.SignInAsync(TttAppFactory.NewUsername("bk"));
        using var http = _factory.Http(human.Token);
        var res = await http.PostAsJsonAsync("/api/bots/keys", new CreateBotKeyDto("mybot"), ContractsJsonContext.Default.Options);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);   // Bots:AllowUserKeys is off: user bots must never reach the fallback pool
        var guest = await _factory.GuestAsync();
        using var guestHttp = _factory.Http(guest.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await guestHttp.GetAsync("/api/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/live")).StatusCode);
        // Guests cannot promote themselves by picking a username.
        var up = await guestHttp.PutAsJsonAsync("/api/me/username", new SetUsernameDto("g_" + Guid.NewGuid().ToString("N")[..8], true), ContractsJsonContext.Default.Options);
        Assert.Equal(HttpStatusCode.Forbidden, up.StatusCode);
    }

    [Fact]
    public async Task GuestTokenMint_IsRateLimited()
    {
        using var http = _factory.Http();
        http.DefaultRequestHeaders.Add("CF-Connecting-IP", "203.0.113.9");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 30; i++)
            codes.Add((await http.PostAsJsonAsync("/api/auth/guest", new GuestTokenRequestDto(Guid.NewGuid().ToString()), ContractsJsonContext.Default.Options)).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.Equal(HttpStatusCode.OK, codes[0]);
    }
}
