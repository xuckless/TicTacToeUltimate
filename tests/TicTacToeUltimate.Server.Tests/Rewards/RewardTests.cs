using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Progression;
using TicTacToeUltimate.Server.Rewards;
using TicTacToeUltimate.Server.Tests.Persistence;

namespace TicTacToeUltimate.Server.Tests.Rewards;

public class RewardTests
{
    private static string Receipt() => "stub:ad:" + Guid.NewGuid();

    [Fact]
    public async Task StreakFreezeCappedAtTwo_NonceSingleUse()
    {
        using var p = new TestPlatform();
        var u = await p.UserAsync("rw" + Guid.NewGuid().ToString("N")[..8]);
        var r = p.Get<RewardService>();
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, "fake"), default)).Error);
        var receipt = Receipt();
        var first = await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, receipt), default);
        Assert.True(first.Granted);
        Assert.Equal(1, first.Progress!.Streak.FreezesBanked);
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, receipt), default)).Error);
        Assert.True((await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, Receipt()), default)).Granted);
        Assert.Equal(ErrorCodes.RewardCapped, (await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.StreakFreeze, null, Receipt()), default)).Error);
        Assert.Equal(2, (await p.Get<IProgressRepository>().GetStreakAsync(u.Id, default)).Freezes);
    }

    [Fact]
    public async Task ExtraQuestOncePerDay()
    {
        using var p = new TestPlatform();
        var u = await p.UserAsync("eq" + Guid.NewGuid().ToString("N")[..8]);
        var r = p.Get<RewardService>();
        var ok = await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.ExtraQuest, null, Receipt()), default);
        Assert.True(ok.Granted);
        var bonus = ok.Progress!.Quests.Single(q => q.Bonus);
        Assert.Equal(2, bonus.Target);
        Assert.False(ok.Progress.ExtraQuestAvailable);
        Assert.Equal(ErrorCodes.RewardCapped, (await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.ExtraQuest, null, Receipt()), default)).Error);
        p.Time.Advance(TimeSpan.FromDays(1));
        Assert.True((await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.ExtraQuest, null, Receipt()), default)).Granted);
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
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.ClaimAsync(c.Id, new RewardClaimDto(RewardKind.Analysis, m.Setup.MatchId, Receipt()), default)).Error);
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.ClaimAsync(a.Id, new RewardClaimDto(RewardKind.Analysis, null, Receipt()), default)).Error);
        Assert.True((await r.ClaimAsync(a.Id, new RewardClaimDto(RewardKind.Analysis, m.Setup.MatchId, Receipt()), default)).Granted);
        Assert.True(await p.Get<IRewardLedger>().HasGrantAsync(a.Id, RewardKind.Analysis, m.Setup.MatchId, default));
    }
}
