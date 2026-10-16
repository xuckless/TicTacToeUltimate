using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.IntegrationTests.Infrastructure;
using TicTacToeUltimate.Server.Moderation;
using TicTacToeUltimate.Server.Persistence;

namespace TicTacToeUltimate.Server.IntegrationTests.Moderation;

/// <summary>Moderation round (2026-10-16): auto-mod strikes and mutes, bans on account and device, reports.</summary>
public sealed class ModerationTests : IAsyncLifetime
{
    private readonly TttAppFactory _f = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    private async Task BefriendAsync(TestPlayer a, TestPlayer b)
    {
        using (var http = _f.Http(a.Token))
            (await http.PostAsJsonAsync("api/friends/requests", new SendFriendRequestDto(b.Username!), ContractsJsonContext.Default.Options)).EnsureSuccessStatusCode();
        using (var http = _f.Http(b.Token))
        {
            var overview = await http.GetFromJsonAsync<FriendsOverviewDto>("api/friends", ContractsJsonContext.Default.Options);
            var req = overview!.Requests.Single(r => r.Direction == FriendRequestDirection.Incoming);
            (await http.PostAsync($"api/friends/requests/{req.RequestId}/accept", null)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task AbusiveMessages_AreBlocked_AndTheLadderMutes()
    {
        _f.RequirePlatform();
        await using var a = await _f.SignInAsync(TttAppFactory.NewUsername("ma"));
        await using var b = await _f.SignInAsync(TttAppFactory.NewUsername("mb"));
        await BefriendAsync(a, b);

        var first = await Assert.ThrowsAsync<HubException>(() => a.InvokeAsync<DirectMessageDto>(nameof(IGameHub.SendDirectMessage), new SendDirectMessageDto(b.Id, "you f u c k i n g idiot")));
        Assert.Contains(ErrorCodes.MessageBlocked, first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(b.Messages, m => m.Body.Contains("idiot", StringComparison.Ordinal));

        using var http = _f.Http(a.Token);
        var standing = await http.GetFromJsonAsync<StandingDto>("api/me/standing", ContractsJsonContext.Default.Options);
        Assert.Equal("warn", standing!.LastWarning?.Kind);
        Assert.Empty(standing.Active);

        await Assert.ThrowsAsync<HubException>(() => a.InvokeAsync<DirectMessageDto>(nameof(IGameHub.SendDirectMessage), new SendDirectMessageDto(b.Id, "sh1t player")));
        standing = await http.GetFromJsonAsync<StandingDto>("api/me/standing", ContractsJsonContext.Default.Options);
        var mute = Assert.Single(standing!.Active);
        Assert.Equal("chat_mute", mute.Kind);
        Assert.True(mute.CanAppeal);

        var muted = await Assert.ThrowsAsync<HubException>(() => a.InvokeAsync<DirectMessageDto>(nameof(IGameHub.SendDirectMessage), new SendDirectMessageDto(b.Id, "sorry, gg")));
        Assert.Contains(ErrorCodes.ChatMuted, muted.Message, StringComparison.Ordinal);

        // one appeal per penalty
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsJsonAsync("api/appeals", new AppealRequestDto(mute.Id, "my cat walked on the keyboard"), ContractsJsonContext.Default.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("api/appeals", new AppealRequestDto(mute.Id, "again"), ContractsJsonContext.Default.Options)).StatusCode);

        // links are refused without a strike
        await using var c = await _f.SignInAsync(TttAppFactory.NewUsername("mc"));
        await BefriendAsync(c, b);
        await Assert.ThrowsAsync<HubException>(() => c.InvokeAsync<DirectMessageDto>(nameof(IGameHub.SendDirectMessage), new SendDirectMessageDto(b.Id, "free gems at www.scam-site.xyz")));
        using var httpC = _f.Http(c.Token);
        Assert.Null((await httpC.GetFromJsonAsync<StandingDto>("api/me/standing", ContractsJsonContext.Default.Options))!.LastWarning);
    }

    [Fact]
    public async Task Ban_RefusesTheAccount_AndItsDevice()
    {
        _f.RequirePlatform();
        await using var a = await _f.SignInAsync(TttAppFactory.NewUsername("ba"));
        var device = "device-" + Guid.NewGuid().ToString("N");
        using var http = _f.Http(a.Token);
        http.DefaultRequestHeaders.Add("X-Ttt-Device", device);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/me")).StatusCode);   // the device is remembered

        var mod = _f.Services.GetRequiredService<ModerationService>();
        await mod.ApplyAsync(a.Id, PenaltyKind.Ban, "Cheating", TimeSpan.FromDays(7), "admin", "owner@test", null, CancellationToken.None);

        var res = await http.GetAsync("api/me");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(ErrorCodes.Banned, doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("ban", doc.RootElement.GetProperty("penalty").GetProperty("kind").GetString());
        // standing stays reachable so the player can read why and appeal
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/me/standing")).StatusCode);

        // a fresh guest on the same device is refused too
        using var anon = _f.Http();
        anon.DefaultRequestHeaders.Add("X-Ttt-Device", device);
        var guest = await anon.PostAsJsonAsync("api/auth/guest", new GuestTokenRequestDto(device), ContractsJsonContext.Default.Options);
        Assert.Equal(HttpStatusCode.Forbidden, guest.StatusCode);
    }

    [Fact]
    public async Task Reports_FoldRepeats_AndCheckEvidence()
    {
        _f.RequirePlatform();
        await using var a = await _f.SignInAsync(TttAppFactory.NewUsername("ra"));
        await using var b = await _f.SignInAsync(TttAppFactory.NewUsername("rb"));
        await BefriendAsync(a, b);
        var dm = await b.InvokeAsync<DirectMessageDto>(nameof(IGameHub.SendDirectMessage), new SendDirectMessageDto(a.Id, "you play like a potato"));

        using var http = _f.Http(a.Token);
        async Task<HttpResponseMessage> Report(ReportRequestDto r) => await http.PostAsJsonAsync("api/reports", r, ContractsJsonContext.Default.Options);
        var r1 = await (await Report(new ReportRequestDto(b.Id, ReportKinds.Chat, "harassment", null, dm.Id))).Content.ReadFromJsonAsync<ReportResultDto>(ContractsJsonContext.Default.Options);
        var r2 = await (await Report(new ReportRequestDto(b.Id, ReportKinds.Chat, "harassment", "again", dm.Id))).Content.ReadFromJsonAsync<ReportResultDto>(ContractsJsonContext.Default.Options);
        Assert.Equal(r1!.Id, r2!.Id);

        // you can only report messages sent to you, by the player you name
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(new ReportRequestDto(a.Id, ReportKinds.Chat, "harassment", null, dm.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(new ReportRequestDto(b.Id, ReportKinds.Chat, "not-a-reason", null, dm.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Report(new ReportRequestDto(b.Id, ReportKinds.Profile, "offensive_name"))).StatusCode);

        var stored = await _f.Services.GetRequiredService<IModerationRepository>().ListReportsAsync("open", b.Id, 10, CancellationToken.None);
        Assert.Equal(2, stored.Count);
        Assert.Contains("potato", stored.Single(r => r.Kind == ReportKinds.Chat).EvidenceJson, StringComparison.Ordinal);
    }
}
