using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Persistence;
using TicTacToeUltimate.Server.Persistence.InMemory;
using TicTacToeUltimate.Server.Platform;
using TicTacToeUltimate.Server.Ratings;
using TicTacToeUltimate.Server.Tests.Persistence;

namespace TicTacToeUltimate.Server.Tests.Api;

public class ConfigTests
{
    [Fact]
    public void ConfigCarriesModesTiersAndOwnerRuleIds()
    {
        using var p = new TestPlatform();
        var cfg = PlatformEndpoints.BuildConfig(p.Get<IOptions<TttOptions>>().Value, p.Get<TierCalculator>());
        Assert.True(cfg.DevAuth);
        Assert.Equal("n3k3-fifo7-v1-w0-pie", cfg.Modes.Single(m => m.TimeControl == TimeControl.Ranked).Rules.Id);
        Assert.False(cfg.Modes.Single(m => m.TimeControl == TimeControl.Ranked).Rules.VanishingPieceCanWin);
        Assert.Equal("n3k3-fifo7-v1-w0-none", cfg.Modes.Single(m => m.TimeControl == TimeControl.CasualX2).Rules.Id);
        Assert.Equal(6, cfg.Tiers.Length);
        Assert.Equal(EmoteCatalog.All.Count, cfg.Emotes.Length);
    }

    [Theory]
    [InlineData("n3k3-fifo7-v1-w0-none")]
    [InlineData("n3k3-fifo7-v1-w0-pie")]
    [InlineData("n4k3-cap4-v0-w1-ban")]
    [InlineData("n5k4-none-v1-w0-none")]
    public void RuleIdsRoundTrip(string id)
    {
        Assert.True(RuleIds.TryParse(id, out var r));
        Assert.Equal(id, r.Id);
    }

    [Fact]
    public async Task FixturesArePlayableAndDeterministic()
    {
        using var p = new TestPlatform();
        await DevFixtures.SeedAsync(p.Services, TestPlatform.Start, default);
        await DevFixtures.SeedAsync(p.Services, TestPlatform.Start, default);   // idempotent
        var games = p.Get<TicTacToeUltimate.Server.Abstractions.Persistence.IGameRepository>();
        var count = await games.CountForUserAsync(DevFixtures.AliXo, default);
        Assert.InRange(count, 8, 16);
        var friends = await p.Get<TicTacToeUltimate.Server.Social.SocialService>().OverviewAsync(DevFixtures.AliXo, default);
        Assert.Equal(4, friends.Friends.Length);
        Assert.Equal(DevFixtures.RookRequestId, friends.Requests.Single().RequestId);
        var progress = await p.Get<TicTacToeUltimate.Server.Progression.ProgressService>().GetAsync(DevFixtures.AliXo, default);
        Assert.Equal((12, 31, true), (progress.Streak.Current, progress.Streak.Best, progress.Streak.SecuredToday));
        Assert.Equal(2, progress.Quests.Single(q => q.QuestId == "win3").Progress);
    }
}

/// <summary>Boots a real Kestrel host (InMemory + Dev) on a loopback port and exercises the REST surface.</summary>
public class RestTests
{
    private static readonly System.Text.Json.JsonSerializerOptions J = ContractsJsonContext.Default.Options;

    private static async Task<(WebApplication App, HttpClient Http)> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ttt:Auth:Dev:Enabled"] = "true",
            ["Ttt:Persistence:SeedFixtures"] = "true",
            ["Ttt:Modes:Ranked:Rules"] = "n3k3-fifo7-v1-w0-pie", ["Ttt:Modes:Ranked:Rated"] = "true",
        });
        builder.Services.AddTttPlatform(builder.Configuration, builder.Environment);
        builder.Services.AddTttCore(builder.Configuration, builder.Environment);
        var app = builder.Build();
        app.UseTttCore();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTttPlatform();
        app.MapTttCore();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(address) });
    }

    [Fact]
    public async Task DevUsersTokenMeFriendsLeaderboardGuest()
    {
        var (app, http) = await StartAsync();
        await using var _ = app;
        using var __ = http;

        var users = await http.GetFromJsonAsync<DevUserDto[]>("/api/auth/dev/users", J);
        Assert.Equal(8, users!.Length);
        var ali = users.Single(u => u.DisplayName == "ali_xo");
        Assert.Equal((Tier.Gold, 2, 1552), (ali.Tier!.Tier, ali.Tier.Division, ali.Rating));

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me")).StatusCode);

        var tokenRes = await http.PostAsJsonAsync("/api/auth/dev/token", new DevTokenRequestDto(ali.UserId, null), J);
        var token = (await tokenRes.Content.ReadFromJsonAsync<TokenResponseDto>(J))!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var me = await http.GetFromJsonAsync<MeDto>("/api/me", J);
        Assert.Equal("ali_xo", me!.Username);
        Assert.Contains(me.Ratings, r => r.Pool == RatingPool.Daily && r.Rating == 1610);

        var friends = await http.GetFromJsonAsync<FriendsOverviewDto>("/api/friends", J);
        Assert.Equal(4, friends!.Friends.Length);
        var board = await http.GetFromJsonAsync<LeaderboardPageDto>("/api/leaderboards/ranked?scope=global", J);
        Assert.Equal("mira_o", board!.Rows[0].Player.DisplayName);
        var profile = await http.GetFromJsonAsync<ProfileDto>("/api/players/kestrel_99", J);
        Assert.Equal(FriendStatus.Friends, profile!.FriendStatus);
        var games = await http.GetFromJsonAsync<GameSummaryDto[]>($"/api/players/{ali.UserId}/games?take=5", J);
        Assert.Equal(5, games!.Length);
        var record = await http.GetFromJsonAsync<GameRecordDto>($"/api/games/{games[0].GameId}", J);
        Assert.NotEmpty(record!.Actions);
        var analysis = await http.GetAsync($"/api/games/{games[0].GameId}/analysis");
        Assert.Equal(HttpStatusCode.Forbidden, analysis.StatusCode);
        Assert.Contains(ErrorCodes.RewardRequired, await analysis.Content.ReadAsStringAsync());
        Assert.Equal(12, (await http.GetFromJsonAsync<ProgressDto>("/api/progress", J))!.Streak.Current);

        using var guestHttp = new HttpClient { BaseAddress = http.BaseAddress };
        var guest = (await (await guestHttp.PostAsJsonAsync("/api/auth/guest", new GuestTokenRequestDto(Guid.NewGuid().ToString()), J)).Content.ReadFromJsonAsync<TokenResponseDto>(J))!;
        guestHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", guest.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await guestHttp.GetAsync("/api/friends")).StatusCode);
        var upgraded = await guestHttp.PutAsJsonAsync("/api/me/username", new SetUsernameDto("guest_up_" + Random.Shared.Next(1000, 9999), true), J);
        Assert.Equal(HttpStatusCode.OK, upgraded.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guestHttp.GetAsync("/api/friends")).StatusCode);   // same token, kind refreshed
        Assert.Equal(guest.Me.UserId, (await guestHttp.GetFromJsonAsync<MeDto>("/api/me", J))!.UserId);

        var cfg = await http.GetFromJsonAsync<AppConfigDto>("/api/config", J);
        Assert.True(cfg!.DevAuth);
    }
}
