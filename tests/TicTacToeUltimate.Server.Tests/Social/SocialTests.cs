using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Social;
using TicTacToeUltimate.Server.Tests.Persistence;

namespace TicTacToeUltimate.Server.Tests.Social;

public class SocialTests
{
    private static string N(string p) => p + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task DirectMessagesOnlyBetweenMutualFriendsNotBlocked()
    {
        using var p = new TestPlatform();
        var a = await p.UserAsync(N("da"));
        var b = await p.UserAsync(N("db"));
        var s = p.Get<SocialService>();

        Assert.Equal(ErrorCodes.NotFriends, (await s.SendAsync(a.Id, b.Id, "hi", default)).Error);

        Assert.True((await s.RequestAsync(a.Id, b.Username!, default)).Ok);
        Assert.Single(p.Notifier.Of<FriendRequestDto>(b.Id));
        Assert.Equal(ErrorCodes.NotFriends, (await s.SendAsync(a.Id, b.Id, "hi", default)).Error);   // pending is not mutual
        var req = (await s.OverviewAsync(b.Id, default)).Requests.Single();
        Assert.Equal(FriendRequestDirection.Incoming, req.Direction);
        Assert.True((await s.RespondAsync(b.Id, req.RequestId, true, default)).Ok);

        var (msg, err) = await s.SendAsync(a.Id, b.Id, "  you shit  ", default);
        Assert.Null(err);
        Assert.Equal("you ****", msg!.Body);
        Assert.Single(p.Notifier.Of<DirectMessageDto>(b.Id));
        Assert.Equal(1, (await s.OverviewAsync(b.Id, default)).Friends.Single().UnreadMessages);
        Assert.Equal(ErrorCodes.Validation, (await s.SendAsync(a.Id, b.Id, "   ", default)).Error);
        Assert.Equal(ErrorCodes.Validation, (await s.SendAsync(a.Id, b.Id, new string('x', 501), default)).Error);

        await s.BlockAsync(b.Id, a.Id, default);
        Assert.Equal(ErrorCodes.Blocked, (await s.SendAsync(a.Id, b.Id, "hi", default)).Error);
        Assert.Equal(ErrorCodes.Blocked, (await s.RequestAsync(a.Id, b.Username!, default)).Error);
    }

    [Fact]
    public async Task RateLimitOnePerSecondBurstFive()
    {
        using var p = new TestPlatform();
        var a = await p.UserAsync(N("ra"));
        var b = await p.UserAsync(N("rb"));
        await p.Get<IFriendRepository>().RequestAsync(a.Id, b.Id, p.Time.GetUtcNow(), default);
        await p.Get<IFriendRepository>().RequestAsync(b.Id, a.Id, p.Time.GetUtcNow(), default);
        var s = p.Get<SocialService>();
        for (var i = 0; i < 5; i++) Assert.Null((await s.SendAsync(a.Id, b.Id, "m" + i, default)).Error);
        Assert.Equal(ErrorCodes.RateLimited, (await s.SendAsync(a.Id, b.Id, "x", default)).Error);
        p.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null((await s.SendAsync(a.Id, b.Id, "y", default)).Error);
    }

    [Fact]
    public async Task MutualRequestAutoAcceptsAndStatus()
    {
        using var p = new TestPlatform();
        var a = await p.UserAsync(N("ma"));
        var b = await p.UserAsync(N("mb"));
        var s = p.Get<SocialService>();
        await s.RequestAsync(a.Id, b.Username!, default);
        Assert.Equal(FriendStatus.RequestSent, await s.StatusAsync(a.Id, b.Id, default));
        Assert.Equal(FriendStatus.RequestReceived, await s.StatusAsync(b.Id, a.Id, default));
        await s.RequestAsync(b.Id, a.Username!, default);
        Assert.Equal(FriendStatus.Friends, await s.StatusAsync(a.Id, b.Id, default));
        Assert.Contains(p.Notifier.Log, x => x.UserId == a.Id && x.Method == "FriendsChanged");
        Assert.Equal(FriendStatus.Self, await s.StatusAsync(a.Id, a.Id, default));
    }

    [Fact]
    public async Task PlayerCardsHideHiddenPoolRatings()
    {
        using var p = new TestPlatform();
        var a = await p.UserAsync(N("pc"));
        var cards = p.Get<TicTacToeUltimate.Server.Abstractions.IPlayerCardService>();
        var ranked = await cards.GetCardAsync(a.Id, RatingPool.Ranked, default);
        Assert.Equal(1200, ranked.Rating);
        Assert.True(ranked.Provisional);
        Assert.Null((await cards.GetCardAsync(a.Id, RatingPool.CasualX2, default)).Rating);
        Assert.Equal("Player", (await cards.GetCardAsync(Guid.NewGuid(), RatingPool.Ranked, default)).DisplayName);
    }

    [Fact]
    public void ProfanityMaskAndUsernameMatch()
    {
        var f = new ProfanityFilter();
        Assert.True(f.ContainsBlocked("sh_1_it"));
        Assert.False(f.ContainsBlocked("grape_99"));
        Assert.Equal("**** off", f.Mask("FUCK off"));
    }
}
