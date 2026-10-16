using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Progression;
using TicTacToeUltimate.Server.Rewards;
using TicTacToeUltimate.Server.Economy;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
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
        r.AllowLegacyAdPerks = true;
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
        r.AllowLegacyAdPerks = true;
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
        r.AllowLegacyAdPerks = true;
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.ClaimAsync(c.Id, new RewardClaimDto(RewardKind.Analysis, m.Setup.MatchId, Receipt(p, c.Id, RewardKind.Analysis, m.Setup.MatchId)), default)).Error);
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.ClaimAsync(a.Id, new RewardClaimDto(RewardKind.Analysis, null, Receipt(p, a.Id, RewardKind.Analysis)), default)).Error);
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.IssueNonceAsync(c.Id, new RewardNonceRequestDto(RewardKind.Analysis, m.Setup.MatchId), default)).Error);
        Assert.True((await r.ClaimAsync(a.Id, new RewardClaimDto(RewardKind.Analysis, m.Setup.MatchId, Receipt(p, a.Id, RewardKind.Analysis, m.Setup.MatchId)), default)).Granted);
        Assert.True(await p.Get<IRewardLedger>().HasGrantAsync(a.Id, RewardKind.Analysis, m.Setup.MatchId, default));
    }

    [Fact]
    public async Task AdsPayDiamondsOnly_CappedPerDay()
    {
        using var p = new TestPlatform();
        var u = await p.UserAsync("ad" + Guid.NewGuid().ToString("N")[..8]);
        var r = p.Get<RewardService>();
        // Direct perks via ads are gone in production.
        Assert.Equal(ErrorCodes.RewardInvalid, (await r.IssueNonceAsync(u.Id, new RewardNonceRequestDto(RewardKind.StreakFreeze, null), default)).Error);
        for (var i = 0; i < EconomyRules.AdViewsPerDay; i++)
        {
            var n = await r.IssueNonceAsync(u.Id, new RewardNonceRequestDto(RewardKind.Diamonds, null), default);
            Assert.NotNull(n.Nonce);
            var claim = await r.ClaimAsync(u.Id, new RewardClaimDto(RewardKind.Diamonds, null, n.Nonce!), default);
            Assert.True(claim.Granted);
            Assert.Equal(EconomyRules.AdReward * (i + 1), claim.Wallet!.Diamonds);
        }
        Assert.Equal(ErrorCodes.RewardCapped, (await r.IssueNonceAsync(u.Id, new RewardNonceRequestDto(RewardKind.Diamonds, null), default)).Error);
        p.Time.Advance(TimeSpan.FromDays(1));
        Assert.NotNull((await r.IssueNonceAsync(u.Id, new RewardNonceRequestDto(RewardKind.Diamonds, null), default)).Nonce);
    }
}

public class WalletTests
{
    [Fact]
    public async Task GrantsAreIdempotentAndCapped_PurchasesDebitAndApply()
    {
        using var p = new TestPlatform();
        var u = await p.UserAsync("wa" + Guid.NewGuid().ToString("N")[..8]);
        var w = p.Get<WalletService>();
        Assert.Equal(5, await w.GrantGameAsync(u.Id, true, Guid.NewGuid(), TestPlatform.Start, default));
        var g = Guid.NewGuid();
        Assert.Equal(5, await w.GrantGameAsync(u.Id, true, g, TestPlatform.Start, default));
        Assert.Equal(0, await w.GrantGameAsync(u.Id, true, g, TestPlatform.Start, default));   // same game twice pays once
        for (var i = 0; i < 20; i++) await w.GrantGameAsync(u.Id, true, Guid.NewGuid(), TestPlatform.Start, default);
        var wallet = await w.GetAsync(u.Id, default);
        Assert.Equal(EconomyRules.WinReward * EconomyRules.WinsPaidPerDay, wallet.Diamonds);   // daily cap

        // Not enough for a freeze (150) yet: refused, balance untouched.
        var buy = await w.PurchaseAsync(u.Id, new PurchaseDto(StoreItems.StreakFreeze, null), default);
        Assert.Equal((false, ErrorCodes.InsufficientDiamonds), (buy.Ok, buy.Error));
        Assert.Equal(50, (await w.GetAsync(u.Id, default)).Diamonds);

        await w.GrantAsync(u.Id, 500, "test", "topup", default);
        buy = await w.PurchaseAsync(u.Id, new PurchaseDto(StoreItems.StreakFreeze, null), default);
        Assert.True(buy.Ok, buy.Error);
        Assert.Equal(1, buy.Progress!.Streak.FreezesBanked);
        Assert.Equal(400, buy.Store!.Wallet.Diamonds);
        buy = await w.PurchaseAsync(u.Id, new PurchaseDto(StoreItems.StreakFreeze, null), default);
        Assert.True(buy.Ok);
        Assert.Equal((false, ErrorCodes.ItemUnavailable), ((await w.PurchaseAsync(u.Id, new PurchaseDto(StoreItems.StreakFreeze, null), default)).Ok,
            (await w.PurchaseAsync(u.Id, new PurchaseDto(StoreItems.StreakFreeze, null), default)).Error));   // bank full (2)
        Assert.Contains(buy.Store!.Items, i => i.Id == StoreItems.StreakFreeze && !i.Available && i.Owned == 2);

        // Ad-free pass: inventory + wallet flag.
        await w.GrantAsync(u.Id, 400, "test", "topup2", default);
        var pass = await w.PurchaseAsync(u.Id, new PurchaseDto(StoreItems.AdFree, null), default);
        Assert.True(pass.Ok, pass.Error);
        Assert.NotNull(pass.Store!.Wallet.AdFreeUntilMs);
        Assert.Equal((false, ErrorCodes.ItemUnavailable), ((await w.PurchaseAsync(u.Id, new PurchaseDto(StoreItems.AdFree, null), default)).Ok, (await w.PurchaseAsync(u.Id, new PurchaseDto(StoreItems.AdFree, null), default)).Error));
    }

    [Fact]
    public async Task PostGamePaysWinAndQuests_NotForChallenges()
    {
        using var p = new TestPlatform();
        var a = await p.UserAsync("pa" + Guid.NewGuid().ToString("N")[..8]);
        var b = await p.UserAsync("pb" + Guid.NewGuid().ToString("N")[..8]);
        var w = p.Get<WalletService>();
        await p.Get<PostGameProcessor>().ProcessAsync(TestPlatform.Match(a, b, 0, MatchMode.Casual, TimeControl.Casual, rated: false), default);
        // Winner: 5 (win) + 10 (win1 quest) + 0 streak (day 1 is no milestone). Loser: 1. (Casual: no tier promotion bonus.)
        Assert.Equal(15, (await w.GetAsync(a.Id, default)).Diamonds);
        Assert.Equal(1, (await w.GetAsync(b.Id, default)).Diamonds);
        var ch = TestPlatform.Match(a, b, 0, MatchMode.Casual, TimeControl.Casual, rated: false);
        ch = ch with { Setup = ch.Setup with { MatchId = Guid.NewGuid(), Mode = MatchMode.Challenge, Origin = MatchOrigin.Challenge } };
        await p.Get<PostGameProcessor>().ProcessAsync(ch, default);
        Assert.Equal(15, (await w.GetAsync(a.Id, default)).Diamonds);   // challenge games pay nothing
    }
}
