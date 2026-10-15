using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Contracts.Bot;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.IntegrationTests.Infrastructure;

namespace TicTacToeUltimate.Server.IntegrationTests.E2E;

/// <summary>
/// End-to-end suite (spec §10, Server.IntegrationTests row). Runs against the real WP1-WP3 implementations;
/// each test skips itself while those packages are not merged.
/// </summary>
public sealed class EndToEndTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private static Task<MoveAckDto> Play(TestPlayer p, Guid matchId, int ply, ActionKind kind, int? cell) =>
        p.InvokeAsync<MoveAckDto>(nameof(IGameHub.SubmitAction), new SubmitActionDto(matchId, ply, kind, cell, Guid.NewGuid()));

    private static async Task<(TestPlayer Seat0, TestPlayer Seat1, Guid MatchId)> QueuePairAsync(TttAppFactory f, TestPlayer a, TestPlayer b, TimeControl tc)
    {
        var ta = await a.InvokeAsync<QueueTicketDto>(nameof(IGameHub.EnterQueue), new QueueRequestDto(tc));
        var tb = await b.InvokeAsync<QueueTicketDto>(nameof(IGameHub.EnterQueue), new QueueRequestDto(tc));
        Assert.True(ta.Accepted, ta.Error);
        Assert.True(tb.Accepted, tb.Error);
        await f.PumpUntilAsync(() => !a.Found.IsEmpty && !b.Found.IsEmpty, Tick, 120);
        a.Found.TryPeek(out var fa);
        b.Found.TryPeek(out var fb);
        Assert.Equal(fa!.MatchId, fb!.MatchId);
        Assert.NotEqual(fa.YourSeat, fb.YourSeat);
        return fa.YourSeat == 0 ? (a, b, fa.MatchId) : (b, a, fa.MatchId);
    }

    [Fact]
    public async Task Ranked_PieDecision_ForcedLine_IdenticalSeqStreams_RatedSummary_RematchRefused()
    {
        await using var f = new TttAppFactory();
        f.RequirePlatform();
        await using var a = await f.SignInAsync(TttAppFactory.NewUsername("ra"));
        await using var b = await f.SignInAsync(TttAppFactory.NewUsername("rb"));
        var (x, o, matchId) = await QueuePairAsync(f, a, b, TimeControl.Ranked);
        var snapX = await x.InvokeAsync<MatchSnapshotDto>(nameof(IGameHub.JoinMatch), matchId);
        await o.InvokeAsync<MatchSnapshotDto>(nameof(IGameHub.JoinMatch), matchId);
        Assert.True(snapX.Rated);
        Assert.Equal(OpeningRule.PieSwap, snapX.Rules.Opening);

        // X:0, O keeps sides, O:3, X:1, O:4, X:2 -> row 0-1-2.
        Assert.True((await Play(x, matchId, 0, ActionKind.Place, 0)).Accepted);
        Assert.True((await Play(o, matchId, 1, ActionKind.KeepSides, null)).Accepted);
        Assert.True((await Play(o, matchId, 2, ActionKind.Place, 3)).Accepted);
        Assert.True((await Play(x, matchId, 3, ActionKind.Place, 1)).Accepted);
        Assert.True((await Play(o, matchId, 4, ActionKind.Place, 4)).Accepted);
        var last = await Play(x, matchId, 5, ActionKind.Place, 2);
        Assert.True(last.Accepted, last.Error);

        await TttAppFactory.WaitUntilAsync(() => x.HasEnded(matchId) && o.HasEnded(matchId));
        var end = (MatchEnded)x.EventsFor(matchId).Last(e => e.Event is MatchEnded).Event;
        Assert.Equal(EndReason.Line, end.Result.Reason);
        Assert.Equal(0, end.Result.WinnerSeat);
        Assert.Equal([0, 1, 2], end.Result.WinLine);

        var seqX = x.EventsFor(matchId).Select(e => e.Seq).ToArray();
        var seqO = o.EventsFor(matchId).Select(e => e.Seq).ToArray();
        Assert.Equal(seqX, seqO);
        Assert.Equal(seqX.Order(), seqX);
        Assert.Equal(seqX.Distinct().Count(), seqX.Length);

        await f.PumpUntilAsync(() => !x.Summaries.IsEmpty && !o.Summaries.IsEmpty, TimeSpan.FromMilliseconds(50), 200);
        x.Summaries.TryPeek(out var sx);
        Assert.Equal(GameResultForUser.Win, sx!.YourResult);
        Assert.NotNull(sx.Rating);

        var rematch = await x.InvokeAsync<CommandAckDto>(nameof(IGameHub.OfferRematch), matchId);
        Assert.False(rematch.Ok);
        Assert.Equal(ErrorCodes.RematchNotAllowed, rematch.Error);
    }

    [Fact]
    public async Task Guest_RefusedRanked()
    {
        await using var f = new TttAppFactory();
        f.RequirePlatform();
        await using var g = await f.GuestAsync();
        try
        {
            var ticket = await g.InvokeAsync<QueueTicketDto>(nameof(IGameHub.EnterQueue), new QueueRequestDto(TimeControl.Ranked));
            Assert.False(ticket.Accepted);
            Assert.Equal(ErrorCodes.GuestNotAllowed, ticket.Error);
        }
        catch (HubException ex)
        {
            Assert.Contains(ErrorCodes.GuestNotAllowed, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Challenge_Spectators_Reconnect_CasualRematch_DirectMessage()
    {
        await using var f = new TttAppFactory();
        f.RequirePlatform();
        await using var a = await f.SignInAsync(TttAppFactory.NewUsername("ca"));
        await using var b = await f.SignInAsync(TttAppFactory.NewUsername("cb"));
        await using var friend = await f.SignInAsync(TttAppFactory.NewUsername("cf"));
        await using var stranger = await f.SignInAsync(TttAppFactory.NewUsername("cs"));

        // friend <-> a
        using (var http = f.Http(friend.Token))
            (await http.PostAsJsonAsync("api/friends/requests", new SendFriendRequestDto(a.Username!), ContractsJsonContext.Default.Options)).EnsureSuccessStatusCode();
        using (var http = f.Http(a.Token))
        {
            var overview = await http.GetFromJsonAsync<FriendsOverviewDto>("api/friends", ContractsJsonContext.Default.Options);
            var req = overview!.Requests.Single(r => r.Direction == FriendRequestDirection.Incoming);
            (await http.PostAsync($"api/friends/requests/{req.RequestId}/accept", null)).EnsureSuccessStatusCode();
        }

        // challenge by code
        var challenge = await a.InvokeAsync<ChallengeDto>(nameof(IGameHub.CreateChallenge), new CreateChallengeDto(TimeControl.CasualX2, null));
        Assert.False(string.IsNullOrEmpty(challenge.Code));
        var accepted = await b.InvokeAsync<AcceptChallengeResultDto>(nameof(IGameHub.AcceptChallenge), challenge.Code);
        Assert.True(accepted.Accepted, accepted.Error);
        var matchId = accepted.MatchId!.Value;
        var snapA = await a.InvokeAsync<MatchSnapshotDto>(nameof(IGameHub.JoinMatch), matchId);
        var snapB = await b.InvokeAsync<MatchSnapshotDto>(nameof(IGameHub.JoinMatch), matchId);
        Assert.False(snapA.Rated);
        var (first, second) = snapA.YourSeat == 0 ? (a, b) : (b, a);

        // spectators: friend allowed, stranger refused
        var spec = await friend.InvokeAsync<MatchSnapshotDto>(nameof(IGameHub.JoinMatch), matchId);
        Assert.Null(spec.YourSeat);
        var refused = await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync<MatchSnapshotDto>(nameof(IGameHub.JoinMatch), matchId));
        Assert.Contains(ErrorCodes.SpectateNotAllowed, refused.Message, StringComparison.Ordinal);

        Assert.True((await Play(first, matchId, 0, ActionKind.Place, 4)).Accepted);
        await TttAppFactory.WaitUntilAsync(() => friend.EventsFor(matchId).Any(e => e.Event is ActionApplied));

        // reconnect mid-game: snapshot reflects the move, clock still running
        await second.ReconnectAsync();
        var rejoin = await second.InvokeAsync<MatchSnapshotDto>(nameof(IGameHub.JoinMatch), matchId);
        Assert.Equal(1, rejoin.Ply);
        Assert.Null(rejoin.Result);
        Assert.NotNull(rejoin.Clock?.TurnStartedAtMs);
        Assert.True(rejoin.Seq > snapB.Seq);

        var resign = await second.InvokeAsync<CommandAckDto>(nameof(IGameHub.Resign), matchId);
        Assert.True(resign.Ok, resign.Error);
        await TttAppFactory.WaitUntilAsync(() => first.HasEnded(matchId));

        // casual rematch works
        Assert.True((await a.InvokeAsync<CommandAckDto>(nameof(IGameHub.OfferRematch), matchId)).Ok);
        Assert.True((await b.InvokeAsync<CommandAckDto>(nameof(IGameHub.OfferRematch), matchId)).Ok);
        await TttAppFactory.WaitUntilAsync(() =>
            a.EventsFor(matchId).Any(e => e.Event is RematchStarted) || a.Found.Any(m => m.MatchId != matchId));

        // DM between friends
        var dm = await a.InvokeAsync<DirectMessageDto>(nameof(IGameHub.SendDirectMessage), new SendDirectMessageDto(friend.Id, "gg"));
        Assert.Equal("gg", dm.Body);
        await TttAppFactory.WaitUntilAsync(() => friend.Messages.Any(m => m.Body == "gg"));
    }

    [Fact]
    public async Task Bot_BadKey_Then_SeekFallback_FullCasualGame_StalePly()
    {
        await using var f = new TttAppFactory();
        f.RequirePlatform();

        await using (var bad = await BotTestClient.ConnectAsync(f.Server, "tttb_nope_nope"))
        {
            Assert.Null(await bad.ReceiveAsync());
            Assert.Equal((System.Net.WebSockets.WebSocketCloseStatus)4401, bad.CloseStatus);
        }

        await using var bot = await BotTestClient.ConnectAsync(f.Server);
        await bot.ReceiveAsync<BotHello>();
        await bot.SendAsync(new BotSeek("s1", [TimeControl.CasualX2], 1));
        await bot.ExpectAsync<BotAck>();

        await using var human = await f.SignInAsync(TttAppFactory.NewUsername("hb"));
        var ticket = await human.InvokeAsync<QueueTicketDto>(nameof(IGameHub.EnterQueue), new QueueRequestDto(TimeControl.CasualX2));
        Assert.True(ticket.Accepted, ticket.Error);
        await f.PumpUntilAsync(() => !human.Found.IsEmpty, Tick, 200);   // BotFallbackSeconds = 20
        human.Found.TryPeek(out var found);
        Assert.Equal(AccountKind.Bot, found!.Opponent.Kind);
        var matchId = found.MatchId;
        await human.InvokeAsync<MatchSnapshotDto>(nameof(IGameHub.JoinMatch), matchId);

        var start = await bot.ExpectAsync<BotGameStart>();
        Assert.Equal(matchId, start.MatchId);

        var stalePlySeen = false;
        var botLoop = Task.Run(async () =>
        {
            while (true)
            {
                var m = await bot.ExpectAsync<BotServerMessage>(TimeSpan.FromSeconds(20));
                switch (m)
                {
                    case BotState { YourTurn: true } s when !stalePlySeen:
                        await bot.SendAsync(new BotAction("stale", matchId, s.Ply - 1, ActionKind.Place, s.Legal[0]));
                        var err = await bot.ExpectAsync<BotError>();
                        Assert.Equal(ErrorCodes.StalePly, err.Code);
                        stalePlySeen = true;
                        await bot.SendAsync(new BotAction("p" + s.Ply, matchId, s.Ply, ActionKind.Place, s.Legal[0]));
                        break;
                    case BotState { YourTurn: true } s:
                        await bot.SendAsync(new BotAction("p" + s.Ply, matchId, s.Ply, ActionKind.Place, s.Legal[^1]));
                        break;
                    case BotError e:
                        throw new Xunit.Sdk.XunitException($"bot error {e.Code}");
                    case BotGameEnd end:
                        return end;
                }
            }
        });

        var played = 0;
        for (var ply = 0; ply < 40 && !human.HasEnded(matchId); ply++)
        {
            await TttAppFactory.WaitUntilAsync(() => human.HasEnded(matchId) ||
                human.EventsFor(matchId).Any(e => e.Event is TurnStarted t && t.Ply == ply) || ply == 0, 10_000);
            if (human.HasEnded(matchId)) break;
            var snap = await human.InvokeAsync<MatchSnapshotDto>(nameof(IGameHub.JoinMatch), matchId);
            if (snap.Result is not null) break;
            if (snap.ToMoveSeat != found.YourSeat)
            {
                await TttAppFactory.WaitUntilAsync(() => human.HasEnded(matchId) ||
                    human.EventsFor(matchId).Any(e => e.Event is TurnStarted t && t.Ply > snap.Ply), 10_000);
                ply = snap.Ply;   // loop increments to the next ply
                continue;
            }
            if (played >= 8)
            {
                Assert.True((await human.InvokeAsync<CommandAckDto>(nameof(IGameHub.Resign), matchId)).Ok);
                break;
            }
            var ack = await Play(human, matchId, snap.Ply, ActionKind.Place, snap.LegalCells[0]);
            Assert.True(ack.Accepted, ack.Error);
            played++;
            ply = snap.Ply;
        }

        var gameEnd = await botLoop.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(matchId, gameEnd.MatchId);
        Assert.True(stalePlySeen);
    }
}
