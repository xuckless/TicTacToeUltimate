using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Persistence.InMemory;
using TicTacToeUltimate.Server.Ratings;
using TicTacToeUltimate.Server.Tests.Persistence;

namespace TicTacToeUltimate.Server.Tests.Ratings;

public class Glicko2Tests
{
    [Fact]
    public void GlickmanPaperVector()
    {
        var r = Glicko2.Update(new GlickoRating(1500, 200, 0.06),
            [new GlickoGame(1400, 30, 1), new GlickoGame(1550, 100, 0), new GlickoGame(1700, 300, 0)], tau: 0.5);
        Assert.Equal(1464.06, r.Rating, 0.01);
        Assert.Equal(151.52, r.Rd, 0.01);
        Assert.Equal(0.05999, r.Volatility, 0.00001);
    }

    [Fact]
    public void IdleInflationIsCapped()
    {
        Assert.Equal(350, Glicko2.InflateRd(340, 0.06, 1000));
        Assert.True(Glicko2.InflateRd(50, 0.06, 4) > 50);
        Assert.Equal(50, Glicko2.InflateRd(50, 0.06, 0));
    }

    [Fact]
    public void ProvisionalUntilTenGames()
    {
        using var p = new TestPlatform();
        var svc = p.Get<RatingService>();
        var id = Guid.NewGuid();
        Assert.True(svc.IsProvisional(RatingRecord.Initial(id, RatingPool.Ranked) with { Games = 9 }));
        Assert.False(svc.IsProvisional(RatingRecord.Initial(id, RatingPool.Ranked) with { Games = 10 }));
        var won = svc.Apply(RatingRecord.Initial(id, RatingPool.Ranked), RatingRecord.Initial(Guid.NewGuid(), RatingPool.Ranked), 1, TestPlatform.Start);
        Assert.True(won.Rating > 1200);
        Assert.Equal((1, 1, 0), (won.Games, won.Wins, won.Losses));
    }
}

public class TierTests
{
    private static readonly TierCalculator Calc = new(new TiersOptions());

    [Theory]
    [InlineData(1552, Tier.Gold, 2, 48)]
    [InlineData(2031, Tier.Diamond, 3, 69)]
    [InlineData(1400, Tier.Gold, 3, 100)]
    [InlineData(1699, Tier.Gold, 1, 1)]
    [InlineData(850, Tier.Bronze, 3, 50)]
    [InlineData(1050, Tier.Bronze, 1, 50)]
    [InlineData(2300, Tier.Master, 0, 0)]
    public void Bands(int rating, Tier tier, int division, int toNext)
    {
        var t = Calc.For(rating);
        Assert.Equal((tier, division, toNext), (t.Tier, t.Division, t.PointsToNext));
    }

    [Fact]
    public void PromotionAndDemotion()
    {
        Assert.True(Calc.Change(1595, 1605).Promoted);
        Assert.True(Calc.Change(1405, 1395).Demoted);
        var same = Calc.Change(1510, 1520);
        Assert.False(same.Promoted || same.Demoted);
    }
}

public class SeasonTests
{
    [Fact]
    public async Task SeasonEndResetsOnlyMasterPlayers()
    {
        using var p = new TestPlatform();
        var ratings = p.Get<IRatingRepository>();
        var seasons = p.Get<ISeasonRepository>();
        var master = Guid.NewGuid();
        var gold = Guid.NewGuid();
        await ratings.PutAsync(RatingRecord.Initial(master, RatingPool.Ranked) with { Rating = 2450, Rd = 60, Games = 300 }, default);
        await ratings.PutAsync(RatingRecord.Initial(gold, RatingPool.Ranked) with { Rating = 1552, Rd = 60, Games = 300 }, default);
        var svc = p.Get<SeasonService>();
        Assert.False(await svc.CheckAsync(default));
        p.Time.Advance(TimeSpan.FromDays(91));
        Assert.True(await svc.CheckAsync(default));
        var m = await ratings.GetAsync(master, RatingPool.Ranked, default);
        Assert.Equal((2300d, 150d), (m.Rating, m.Rd));
        var g = await ratings.GetAsync(gold, RatingPool.Ranked, default);
        Assert.Equal((1552d, 60d), (g.Rating, g.Rd));
        var results = ((InMemorySeasonRepository)seasons).Results;
        Assert.Equal(master, Assert.Single(results).UserId);
        Assert.Equal(2, (await seasons.GetCurrentAsync(p.Time.GetUtcNow(), default)).Id);
    }
}
