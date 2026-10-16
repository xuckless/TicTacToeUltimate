using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Progression;
using TicTacToeUltimate.Server.Rewards;
using TicTacToeUltimate.Server.Tests.Persistence;

namespace TicTacToeUltimate.Server.Tests.Rewards;

public class RewardTests
{
    /// <summary>A valid nonce for the user (what the client gets from POST /api/rewards/nonce).</summary>
    private static string Receipt(TestPlatform p, Guid user, RewardKind kind, Guid? game = null) => p.Get<RewardNonceService>().Issue(user, kind, game).Nonce;

    [Fact]
    public async Task StreakFreezeCappedAtTwo_NonceSingleUse()
    {
        using var p = new TestPlatform();
        var u = await p.UserAsync("rw" + Guid.NewGuid().ToString("N")[..8]);
        var r = p.Get<RewardService>();
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, "fake"), default)).Error);
        // Client-fabricated receipts of the old formats are refused; so is a nonce minted for another user or kind.
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, "web:streakfreeze:" + Guid.NewGuid()), default)).Error);
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, Receipt(p, Guid.NewGuid(), RewardKind.StreakFreeze)), default)).Error);
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, Receipt(p, u.Id, RewardKind.ExtraQuest)), default)).Error);

        var nonce = await r.IssueNonceAsync(u.Id, new RewardNonceRequestDto(RewardKind.StreakFreeze, null), default);
        Assert.NotNull(nonce.Nonce);
        var first = await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, nonce.Nonce!), default);
        Assert.True(first.Granted);
        Assert.Equal(1, first.Progress!.Streak.FreezesBanked);
        Assert.False((await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, nonce.Nonce!), default)).Granted);   // single use (capped or invalid, never a 2nd freeze)
        // One ad-earned freeze per local day: the nonce endpoint says capped BEFORE the ad.
        Assert.Equal(ErrorCodes.RewardCapped, (await r.IssueNonceAsync(u.Id, new RewardNonceRequestDto(RewardKind.StreakFreeze, null), default)).Error);
        Assert.Equal(ErrorCodes.RewardCapped, (await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, Receipt(p, u.Id, RewardKind.StreakFreeze)), default)).Error);
        p.Time.Advance(TimeSpan.FromDays(1));
        Assert.True((await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, Receipt(p, u.Id, RewardKind.StreakFreeze)), default)).Granted);
        p.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(ErrorCodes.RewardCapped, (await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, Receipt(p, u.Id, RewardKind.StreakFreeze)), default)).Error);   // bank full (2)
        Assert.Equal(2, (await p.Get<IProgressRepository>().GetStreakAsync(u.Id, default)).Freezes);
    }

    [Fact]
    public async Task NonceExpires()
    {
        using var p = new TestPlatform();
        var u = await p.UserAsync("nx" + Guid.NewGuid().ToString("N")[..8]);
        var nonce = Receipt(p, u.Id, RewardKind.ExtraQuest);
        p.Time.Advance(RewardNonceService.Ttl + TimeSpan.FromSeconds(1));
        Assert.Equal(ErrorCodes.RewardInvalid, (await p.Get<RewardService>().ClaimAsync(u.Id, new RewardClaimDto(RewardKind.ExtraQuest, null, nonce), default)).Error);
    }

    [Fact]
    public async Task ExtraQuestOncePerDay()
    {
        using var p = new TestPlatform();
        var u = await p.UserAsync("eq" + Guid.NewGuid().ToString("N")[..8]);
        var r = p.Get<RewardService>();
        var ok = await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.ExtraQuest, null, Receipt(p, u.Id, RewardKind.ExtraQuest)), default);
        Assert.True(ok.Granted);
        var bonus = ok.Progress!.Quests.Single(q => q.Bonus);
        Assert.Equal(2, bonus.Target);
        Assert.False(ok.Progress.ExtraQuestAvailable);
        Assert.Equal(ErrorCodes.RewardCapped, (await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.ExtraQuest, null, Receipt(p, u.Id, RewardKind.ExtraQuest)), default)).Error);
        p.Time.Advance(TimeSpan.FromDays(1));
        Assert.True((await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.ExtraQuest, null, Receipt(p, u.Id, RewardKind.ExtraQuest)), default)).Granted);
    }

    [Fact]
    public async Task AnalysisRequiresOwnGame()
    {
        using var p = new TestPlatform();
        var a = await p.UserAsync("aa" + Guid.NewGuid().ToString("N")[..8]);
        var b = await p.UserAsync("ab" + Guid.NewGuid().ToString("N")[..8]);
        var c = await p.UserAsync("ac" + Guid.NewGuid().ToString("N")[..8]);
        var m = TestPlatform.Match(a, b, 0);
        await p.Get<PostGameProcessor>().ProcessAsync(m, default);
        var r = p.Get<RewardService>();
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.ClaimAsync(c.Id, new RewardClaimDto(RewardKind.Analysis, m.Setup.MatchId, Receipt(p, c.Id, RewardKind.Analysis, m.Setup.MatchId)), default)).Error);
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.ClaimAsync(a.Id, new RewardClaimDto(RewardKind.Analysis, null, Receipt(p, a.Id, RewardKind.Analysis)), default)).Error);
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.IssueNonceAsync(c.Id, new RewardNonceRequestDto(RewardKind.Analysis, m.Setup.MatchId), default)).Error);
        Assert.True((await r.ClaimAsync(a.Id, new RewardClaimDto(RewardKind.Analysis, m.Setup.MatchId, Receipt(p, a.Id, RewardKind.Analysis, m.Setup.MatchId)), default)).Granted);
        Assert.True(await p.Get<IRewardLedger>().HasGrantAsync(a.Id, RewardKind.Analysis, m.Setup.MatchId, default));
    }
}
