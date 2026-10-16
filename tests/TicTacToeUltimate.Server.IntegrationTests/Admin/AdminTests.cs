using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Contracts.Admin;
using TicTacToeUltimate.Server.Admin;
using TicTacToeUltimate.Server.IntegrationTests.Infrastructure;

namespace TicTacToeUltimate.Server.IntegrationTests.Admin;

public sealed class AdminTests : IAsyncLifetime
{
    private readonly TttAppFactory _f = new();
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private static readonly System.Text.Json.JsonSerializerOptions Json = AdminJsonContext.Default.Options;

    [Fact]
    public async Task Admin_CanBan_ThenTheAuditLogShowsIt()
    {
        _f.RequirePlatform();
        await using var p = await _f.SignInAsync(TttAppFactory.NewUsername("ad"));
        using var admin = _f.Http();   // Development: Ttt:Admin:DevBypass
        Assert.Equal("xuckless@gmail.com", (await admin.GetFromJsonAsync<AdminMeDto>("/api/admin/me", Json))!.Email);

        var players = await admin.GetFromJsonAsync<AdminPlayerDto[]>($"/api/admin/players?q={p.Username}", Json);
        Assert.Contains(players!, x => x.Id == p.Id);
        var res = await admin.PostAsJsonAsync($"/api/admin/players/{p.Id}/penalties", new PenaltyRequestDto("ban", "Cheating", 60), Json);
        Assert.True((await res.Content.ReadFromJsonAsync<AdminActionResultDto>(Json))!.Ok);

        using var player = _f.Http(p.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync("/api/me")).StatusCode);

        var detail = await admin.GetFromJsonAsync<AdminPlayerDetailDto>($"/api/admin/players/{p.Id}", Json);
        var ban = Assert.Single(detail!.Penalties, x => x.Kind == "ban");
        Assert.True(ban.Active);
        Assert.True((await (await admin.PostAsync($"/api/admin/penalties/{ban.Id}/revoke", null)).Content.ReadFromJsonAsync<AdminActionResultDto>(Json))!.Ok);
        Assert.Equal(HttpStatusCode.OK, (await player.GetAsync("/api/me")).StatusCode);

        var audit = await admin.GetFromJsonAsync<AuditDto[]>("/api/admin/audit", Json);
        Assert.Contains(audit!, a => a.Action == "penalty.ban");
        Assert.Contains(audit!, a => a.Action == "penalty.revoke");

        var live = await admin.GetFromJsonAsync<LiveDto>("/api/admin/live", Json);
        Assert.NotNull(live);   // the ban dropped the player's socket, so nobody needs to be online here
        Assert.NotNull(await admin.GetFromJsonAsync<OverviewDto>("/api/admin/overview", Json));
        Assert.NotNull(await admin.GetFromJsonAsync<InfraDto>("/api/admin/infra", Json));
    }

    [Fact]
    public async Task Without_access_or_bypass_the_gate_refuses()
    {
        var gate = new AdminGate(Options.Create(new AdminOptions { Emails = ["owner@test"], Access = new() { TeamDomain = "x.cloudflareaccess.com", Aud = "aud" } }),
            _f.Services.GetRequiredService<IHttpClientFactory>(), new ProdEnv(), TimeProvider.System, NullLogger<AdminGate>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Admin-Email"] = "owner@test";   // only honoured by the dev bypass
        Assert.Null(await gate.AuthorizeAsync(ctx));
        ctx.Request.Headers["Cf-Access-Jwt-Assertion"] = "not.a.jwt";
        Assert.Null(await gate.AuthorizeAsync(ctx));
    }

    private sealed class ProdEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
