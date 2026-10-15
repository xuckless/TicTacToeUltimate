using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Progression;
using TicTacToeUltimate.Server.Tests.Persistence;

namespace TicTacToeUltimate.Server.Tests.Progression;

public class StreakTests
{
    private static readonly DateOnly D = new(2026, 10, 15);

    [Fact]
    public void ConsecutiveDaysIncrement_SameDayNoChange()
    {
        var s = new StreakRecord(Guid.NewGuid(), 4, 4, D.AddDays(-1), 0);
        var next = StreakRules.Played(s, D);
        Assert.Equal((5, 5), (next.Current, next.Best));
        Assert.Equal(next, StreakRules.Played(next, D));
    }

    [Fact]
    public void GapConsumesFreezes_OrResets()
    {
        var s = new StreakRecord(Guid.NewGuid(), 10, 20, D.AddDays(-3), 2);
        var kept = StreakRules.Played(s, D);
        Assert.Equal((11, 0), (kept.Current, kept.Freezes));
        var broken = StreakRules.Played(s with { LastPlayedDay = D.AddDays(-4) }, D);
        Assert.Equal((1, 2, 20), (broken.Current, broken.Freezes, broken.Best));
    }

    [Fact]
    public void QuestFreezeAwardOnceAndCapped()
    {
        var id = Guid.NewGuid();
        var q = new QuestDayRecord(id, D, 4, false, null, false);
        var (q5, s5) = StreakRules.Won(q, new StreakRecord(id, 1, 1, D, 0));
        Assert.True(q5.FreezeAwarded);
        Assert.Equal(1, s5.Freezes);
        var (q6, s6) = StreakRules.Won(q5, s5);
        Assert.Equal((6, 1), (q6.Wins, s6.Freezes));
        var (capped, sc) = StreakRules.Won(q, new StreakRecord(id, 1, 1, D, 2));
        Assert.False(capped.FreezeAwarded);
        Assert.Equal(2, sc.Freezes);
    }

    [Fact]
    public void BonusQuestCompletesAtTarget()
    {
        var id = Guid.NewGuid();
        var (q, _) = StreakRules.Won(new QuestDayRecord(id, D, 2, false, 3, false), new StreakRecord(id, 1, 1, D, 0));
        Assert.True(q.BonusClaimed);
    }

    [Theory]
    [InlineData("Pacific/Honolulu", 8, 12, 14, 15)]    // UTC-10: 08:00Z is Oct 14 local, 12:00Z is Oct 15 local
    [InlineData("Pacific/Kiritimati", 9, 11, 15, 16)]  // UTC+14: 09:00Z is Oct 15 23:00, 11:00Z is Oct 16 01:00
    public async Task StreakAcrossTimeZones(string tz, int hour1, int hour2, int day1, int day2)
    {
        using var p = new TestPlatform();
        var u = await p.UserAsync("tz" + Guid.NewGuid().ToString("N")[..8], tz: tz);
        var svc = p.Get<ProgressService>();
        var t1 = new DateTimeOffset(2026, 10, 15, hour1, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 10, 15, hour2, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, day1), LocalDays.Day(t1, tz));
        Assert.Equal(new DateOnly(2026, 10, day2), LocalDays.Day(t2, tz));
        await svc.ApplyGameAsync(u.Id, true, t1, default);
        var prog = await svc.ApplyGameAsync(u.Id, true, t2, default);
        Assert.Equal(2, prog.Streak.Current);
        Assert.Equal(1, prog.Quests.Single(q => q.QuestId == "win1").Progress);   // new local day → quests reset
    }
}

public class PostGameProcessorTests
{
    [Fact]
    public async Task SameMatchTwiceChangesRatingOnce()
    {
        using var p = new TestPlatform();
        var a = await p.UserAsync("pa" + Guid.NewGuid().ToString("N")[..8]);
        var b = await p.UserAsync("pb" + Guid.NewGuid().ToString("N")[..8]);
        var proc = p.Get<PostGameProcessor>();
        var m = TestPlatform.Match(a, b, 0);
        Assert.True(await proc.ProcessAsync(m, default));
        Assert.False(await proc.ProcessAsync(m, default));
        var ra = await p.Get<IRatingRepository>().GetAsync(a.Id, RatingPool.Ranked, default);
        Assert.Equal(1, ra.Games);
        Assert.True(ra.Rating > 1200);
        var summary = Assert.Single(p.Notifier.Of<PostGameSummaryDto>(a.Id));
        Assert.Equal(GameResultForUser.Win, summary.YourResult);
        Assert.True(summary.Rating!.Delta > 0);
        Assert.False(summary.RematchAllowed);
        Assert.Equal(1, summary.WinStreak);
        Assert.Equal(GameResultForUser.Loss, Assert.Single(p.Notifier.Of<PostGameSummaryDto>(b.Id)).YourResult);
        Assert.Equal(1, (await p.Get<IProgressRepository>().GetStreakAsync(a.Id, default)).Current);
    }

    [Fact]
    public async Task ConcurrentCompletionsAreSerialized()
    {
        using var p = new TestPlatform();
        var a = await p.UserAsync("ca" + Guid.NewGuid().ToString("N")[..8]);
        var b = await p.UserAsync("cb" + Guid.NewGuid().ToString("N")[..8]);
        var proc = p.Get<PostGameProcessor>();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => proc.ProcessAsync(TestPlatform.Match(a, b, i % 2, at: TestPlatform.Start.AddSeconds(i)), default))));
        var ra = await p.Get<IRatingRepository>().GetAsync(a.Id, RatingPool.Ranked, default);
        var rb = await p.Get<IRatingRepository>().GetAsync(b.Id, RatingPool.Ranked, default);
        Assert.Equal((20, 10, 10), (ra.Games, ra.Wins, ra.Losses));
        Assert.Equal(20, rb.Games);
        Assert.Equal(20, await p.Get<IGameRepository>().CountForUserAsync(a.Id, default));
    }

    [Fact]
    public async Task ChannelConsumerProcessesEnqueuedMatches()
    {
        using var p = new TestPlatform();
        var a = await p.UserAsync("qa" + Guid.NewGuid().ToString("N")[..8]);
        var b = await p.UserAsync("qb" + Guid.NewGuid().ToString("N")[..8]);
        var proc = p.Get<PostGameProcessor>();
        await proc.StartAsync(default);
        for (var i = 0; i < 5; i++) await ((IMatchResultSink)proc).EnqueueAsync(TestPlatform.Match(a, b, 1), default);
        var games = p.Get<IGameRepository>();
        for (var spin = 0; spin < 200 && await games.CountForUserAsync(a.Id, default) < 5; spin++) await Task.Delay(10);
        await proc.StopAsync(default);
        Assert.Equal(5, (await p.Get<IRatingRepository>().GetAsync(b.Id, RatingPool.Ranked, default)).Wins);
    }

    [Fact]
    public async Task AbortedAndChallengeAndCasualRules()
    {
        using var p = new TestPlatform();
        var a = await p.UserAsync("ra" + Guid.NewGuid().ToString("N")[..8]);
        var b = await p.UserAsync("rb" + Guid.NewGuid().ToString("N")[..8]);
        var proc = p.Get<PostGameProcessor>();
        var ratings = p.Get<IRatingRepository>();
        await proc.ProcessAsync(TestPlatform.Match(a, b, null, reason: EndReason.Aborted), default);
        Assert.Equal(0, (await ratings.GetAsync(a.Id, RatingPool.Ranked, default)).Games);
        Assert.Equal(0, (await p.Get<IProgressRepository>().GetStreakAsync(a.Id, default)).Current);
        Assert.Equal(GameResultForUser.Aborted, p.Notifier.Of<PostGameSummaryDto>(a.Id).Single().YourResult);

        await proc.ProcessAsync(TestPlatform.Match(a, b, 0, MatchMode.Challenge, TimeControl.CasualX2, rated: false), default);
        Assert.Equal(0, (await ratings.GetAsync(a.Id, RatingPool.CasualX2, default)).Games);

        await proc.ProcessAsync(TestPlatform.Match(a, b, 0, MatchMode.Casual, TimeControl.CasualX2, rated: false), default);
        Assert.Equal(1, (await ratings.GetAsync(a.Id, RatingPool.CasualX2, default)).Games);   // hidden pool
        var casual = p.Notifier.Of<PostGameSummaryDto>(a.Id).Last();
        Assert.Null(casual.Rating);
        Assert.True(casual.RematchAllowed);
    }

    [Fact]
    public async Task PromotionSendsNotification()
    {
        using var p = new TestPlatform();
        var a = await p.UserAsync("na" + Guid.NewGuid().ToString("N")[..8]);
        var b = await p.UserAsync("nb" + Guid.NewGuid().ToString("N")[..8]);
        var ratings = p.Get<IRatingRepository>();
        await ratings.PutAsync(RatingRecord.Initial(a.Id, RatingPool.Ranked) with { Rating = 1598, Rd = 200, Games = 50 }, default);
        await ratings.PutAsync(RatingRecord.Initial(b.Id, RatingPool.Ranked) with { Rating = 1700, Rd = 60, Games = 50 }, default);
        await p.Get<PostGameProcessor>().ProcessAsync(TestPlatform.Match(a, b, 0), default);
        var n = Assert.Single(p.Notifier.Of<NotificationDto>(a.Id));
        Assert.Equal(NotificationKind.Promotion, n.Kind);
        Assert.True(p.Notifier.Of<PostGameSummaryDto>(a.Id).Single().Tier!.Promoted);
    }
}
