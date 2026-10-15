using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Ratings;

/// <summary>Applies Glicko-2 to stored records and builds rating DTOs.</summary>
public sealed class RatingService(IOptions<TttOptions> options, TierCalculator tiers, IRatingRepository ratings, IGameRepository games)
{
    private RatingsOptions O => options.Value.Ratings;

    /// <summary>True while the record has fewer than ProvisionalGames games.</summary>
    public bool IsProvisional(RatingRecord r) => r.Games < O.ProvisionalGames;

    /// <summary>One-game update of <paramref name="me"/> against <paramref name="opponent"/> (both pre-game).</summary>
    public RatingRecord Apply(RatingRecord me, RatingRecord opponent, double score, DateTimeOffset at)
    {
        var rd = InflatedRd(me, at);
        var oppRd = InflatedRd(opponent, at);
        var next = Glicko2.Update(new GlickoRating(me.Rating, rd, me.Volatility), [new GlickoGame(opponent.Rating, oppRd, score)], O.Tau);
        return me with
        {
            Rating = next.Rating, Rd = next.Rd, Volatility = next.Volatility, Games = me.Games + 1,
            Wins = me.Wins + (score >= 1 ? 1 : 0), Losses = me.Losses + (score <= 0 ? 1 : 0), Draws = me.Draws + (score is > 0 and < 1 ? 1 : 0),
            Peak = Math.Max(me.Peak, next.Rating), LastGameAt = at,
        };
    }

    private double InflatedRd(RatingRecord r, DateTimeOffset at)
    {
        if (r.LastGameAt is not { } last) return r.Rd;
        var idleDays = (int)Math.Floor((at - last).TotalDays);
        return Glicko2.InflateRd(r.Rd, r.Volatility, idleDays / Math.Max(1, O.IdlePeriodDays));
    }

    /// <summary>Visible-pool DTO (tier only for visible pools) with the 7-day delta from game history.</summary>
    public async Task<RatingDto> ToDtoAsync(RatingRecord r, DateTimeOffset now, CancellationToken ct)
    {
        var visible = RatingPools.IsVisible(r.Pool);
        var weekDelta = 0;
        if (visible && r.Games > 0)
        {
            var since = now.AddDays(-7);
            var recent = await games.ListForUserAsync(r.UserId, null, 200, ct);
            foreach (var g in recent.Where(g => g.EndedAt >= since))
            {
                var seat = g.Setup.SeatA.UserId == r.UserId ? 0 : 1;
                foreach (var s in g.Ratings.Where(s => s.Seat == seat && s.Pool == r.Pool)) weekDelta += (int)Math.Round(s.After - s.Before);
            }
        }
        return new RatingDto(r.Pool, (int)Math.Round(r.Rating), (int)Math.Round(r.Rd), r.Games, IsProvisional(r), visible ? tiers.For(r.Rating) : null, weekDelta);
    }

    /// <summary>DTOs for the visible pools (Ranked, Daily).</summary>
    public async Task<RatingDto[]> VisibleAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var list = new List<RatingDto>();
        foreach (var pool in new[] { RatingPool.Ranked, RatingPool.Daily })
            list.Add(await ToDtoAsync(await ratings.GetAsync(userId, pool, ct), now, ct));
        return list.ToArray();
    }
}

/// <summary>Hourly Master season roll-over (§6.7).</summary>
public sealed class SeasonService(ISeasonRepository seasons, IRatingRepository ratings, TierCalculator tiers, IOptions<TttOptions> options,
    TimeProvider time, ILogger<SeasonService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try { await CheckAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogError(e, "Season check failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Closes the current season if it has ended. Returns true when a season was closed.</summary>
    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var season = await seasons.GetCurrentAsync(now, ct);
        if (now < season.EndsAt) return false;
        var page = await ratings.QueryAsync(new LeaderboardQuery(RatingPool.Ranked, LeaderboardScope.MasterSeason, null, Guid.Empty, null, 0,
            tiers.MasterMin, 1, int.MaxValue / 2), ct);
        var results = page.Rows.Select(r => new MasterSeasonResult(season.Id, r.UserId, r.Rating, r.Rank)).ToList();
        foreach (var r in page.Rows)
        {
            var rec = await ratings.GetAsync(r.UserId, RatingPool.Ranked, ct);
            await ratings.PutAsync(rec with { Rating = tiers.MasterMin, Rd = options.Value.Seasons.MasterResetRd }, ct);
        }
        var next = new SeasonRecord(season.Id + 1, season.EndsAt, season.EndsAt.AddDays(Math.Max(1, options.Value.Seasons.LengthDays)));
        await seasons.CloseAsync(season, results, next, ct);
        logger.LogInformation("Season {Id} closed with {Count} Master players", season.Id, results.Count);
        return true;
    }
}
