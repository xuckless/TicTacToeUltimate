using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Analysis;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Persistence;
using TicTacToeUltimate.Server.Persistence.Postgres;
using EngineRules = TicTacToeUltimate.Engine.Rules;

namespace TicTacToeUltimate.Server.Moderation;

/// <summary>One player's numbers for one game.</summary>
public sealed record GameMetricRecord(Guid GameId, Guid UserId, int Critical, int Accurate, int ThinkMsAvg, bool Won, DateTimeOffset At);

public interface IGameMetricsRepository
{
    Task AddAsync(GameMetricRecord metric, CancellationToken ct);
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<GameMetricRecord>> RecentAsync(Guid userId, int take, CancellationToken ct);
}

/// <summary>Ttt:AntiCheat. Defaults: the last 20 games, at least 30 critical positions, 95%+ of them right and 75%+ wins.</summary>
public sealed class AntiCheatOptions
{
    public bool Enabled { get; set; } = true;
    public int WindowGames { get; set; } = 20;
    public int MinCritical { get; set; } = 30;
    public double MinAccuracy { get; set; } = 0.95;
    public double MinWinRate { get; set; } = 0.75;
}

/// <summary>
/// Anti-cheat (owner decision 2026-10-16): the game is solved, so after every game we count, per human player, the critical
/// positions they faced (positions where some legal move loses value) and how often they kept the value. Sustained
/// near-perfect play with a high win rate puts the player (guests too) in the shadow pool: they keep playing and progressing,
/// but only against strong bots or other flagged players. Never an automatic ban; the admin "Suspicious" queue decides.
/// </summary>
public sealed class CheatScorer(ITablebaseCache tablebases, IGameMetricsRepository metrics, ModerationService moderation,
    IOptions<AntiCheatOptions> options, ILogger<CheatScorer> log)
{
    private AntiCheatOptions Opts => options.Value;

    /// <summary>Scores a finished game and flags players that cross the thresholds.</summary>
    public async Task ScoreAsync(GameRecord game, CancellationToken ct)
    {
        if (!Opts.Enabled || game.Actions.Count == 0 || !tablebases.TryGet(game.Setup.Rules, out var tb)) return;
        var per = Measure(tb, game);
        if (per is null) return;
        Guid[] seats = [game.Setup.SeatA.UserId, game.Setup.SeatB.UserId];
        AccountKind[] kinds = [game.Setup.SeatA.Kind, game.Setup.SeatB.Kind];
        for (var seat = 0; seat < 2; seat++)
        {
            if (kinds[seat] is not (AccountKind.Account or AccountKind.Guest)) continue;
            var (critical, accurate, think) = per[seat];
            await metrics.AddAsync(new GameMetricRecord(game.Id, seats[seat], critical, accurate, think, game.Result.WinnerSeat == seat, game.EndedAt), ct);
            await EvaluateAsync(seats[seat], ct);
        }
    }

    /// <summary>Per seat: (critical positions, kept the value, average think ms there). Null when the game can't be replayed.</summary>
    public static (int Critical, int Accurate, int ThinkMs)[]? Measure(Tablebase tb, GameRecord game)
    {
        try
        {
            var plies = GameAnalyzer.Analyze(tb, game.Setup.Rules, game.Actions.Select(a => a.Action).ToList());
            var critical = new int[2]; var accurate = new int[2]; var think = new long[2];
            var state = EngineRules.Initial(game.Setup.Rules);
            for (var i = 0; i < plies.Length; i++)
            {
                var seat = game.Actions[i].Seat;
                if (seat is 0 or 1 && game.Actions[i].Action.Kind == ActionKind.Place && tb.EvaluateMoves(state).Any(m => m.Class == MoveClass.Blunder))
                {
                    critical[seat]++;
                    if (plies[i].Class != MoveClass.Blunder) accurate[seat]++;
                    think[seat] += game.Actions[i].ThinkMs;
                }
                state = EngineRules.Apply(state, game.Actions[i].Action).State;
            }
            return [(critical[0], accurate[0], critical[0] == 0 ? 0 : (int)(think[0] / critical[0])),
                    (critical[1], accurate[1], critical[1] == 0 ? 0 : (int)(think[1] / critical[1]))];
        }
        catch (Exception ex) when (ex is InvalidActionException or InvalidOperationException) { return null; }
    }

    /// <summary>Rolling check over the player's recent games.</summary>
    public async Task<SuspicionDto> EvaluateAsync(Guid userId, CancellationToken ct)
    {
        var recent = await metrics.RecentAsync(userId, Opts.WindowGames, ct);
        var critical = recent.Sum(r => r.Critical);
        var accuracy = critical == 0 ? 0 : recent.Sum(r => r.Accurate) / (double)critical;
        var winRate = recent.Count == 0 ? 0 : recent.Count(r => r.Won) / (double)recent.Count;
        var think = critical == 0 ? 0 : (int)(recent.Sum(r => (long)r.ThinkMsAvg * r.Critical) / critical);
        var flagged = critical >= Opts.MinCritical && accuracy >= Opts.MinAccuracy && winRate >= Opts.MinWinRate;
        if (flagged && !await moderation.IsShadowAsync(userId, ct))
        {
            var reason = $"Anti-cheat: {accuracy:P0} perfect in {critical} critical positions over {recent.Count} games, {winRate:P0} wins, {think} ms average think.";
            await moderation.ApplyAsync(userId, PenaltyKind.Shadow, reason, null, "auto", "anti-cheat", null, ct);
            log.LogInformation("Shadow pool: {User} ({Reason})", userId, reason);
        }
        return new SuspicionDto(userId, recent.Count, critical, accuracy, winRate, think, flagged);
    }
}

/// <summary>Rolling anti-cheat numbers (admin "Suspicious" queue).</summary>
public sealed record SuspicionDto(Guid UserId, int Games, int Critical, double Accuracy, double WinRate, int ThinkMsAvg, bool OverThreshold);

public sealed class InMemoryGameMetricsRepository : IGameMetricsRepository
{
    private readonly Lock _gate = new();
    private readonly List<GameMetricRecord> _rows = [];
    public Task AddAsync(GameMetricRecord m, CancellationToken ct) { lock (_gate) if (!_rows.Any(r => r.GameId == m.GameId && r.UserId == m.UserId)) _rows.Add(m); return Task.CompletedTask; }
    public Task<IReadOnlyList<GameMetricRecord>> RecentAsync(Guid userId, int take, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<GameMetricRecord>>(_rows.Where(r => r.UserId == userId).OrderByDescending(r => r.At).Take(take).ToList());
    }
}

#pragma warning disable CS0649 // Dapper fills the row fields
public sealed class PgGameMetricsRepository(PgDb db) : IGameMetricsRepository
{
    private sealed class Row { public Guid game_id; public Guid user_id; public int critical; public int accurate; public int think_ms_avg; public bool won; public DateTime at; }

    public async Task AddAsync(GameMetricRecord m, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("""
            insert into game_metrics (game_id, user_id, critical, accurate, think_ms_avg, won, at) values (@GameId, @UserId, @Critical, @Accurate, @ThinkMsAvg, @Won, @at)
            on conflict do nothing
            """, new { m.GameId, m.UserId, m.Critical, m.Accurate, m.ThinkMsAvg, m.Won, at = m.At.UtcDateTime });
    }

    public async Task<IReadOnlyList<GameMetricRecord>> RecentAsync(Guid userId, int take, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<Row>("select * from game_metrics where user_id=@userId order by at desc limit @take", new { userId, take });
        return rows.Select(r => new GameMetricRecord(r.game_id, r.user_id, r.critical, r.accurate, r.think_ms_avg, r.won, PgDb.Dto(r.at))).ToList();
    }
}
