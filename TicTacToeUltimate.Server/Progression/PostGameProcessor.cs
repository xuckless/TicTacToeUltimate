using System.Threading.Channels;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Ratings;

namespace TicTacToeUltimate.Server.Progression;

/// <summary>
/// IMatchResultSink (§6.7): single-consumer channel; ratings, game save (idempotency gate), tiers, streaks, quests,
/// analysis and PostGameSummary delivery. <see cref="ProcessAsync"/> is also serialized so direct callers are safe.
/// </summary>
public sealed class PostGameProcessor(
    IGameRepository games, IRatingRepository ratings, RatingService ratingService, TierCalculator tiers, ProgressService progress,
    ITablebaseCache tablebases, IAnalysisService analysis, IUserNotifier notifier, TimeProvider time, ILogger<PostGameProcessor> logger)
    : BackgroundService, IMatchResultSink
{
    private readonly Channel<CompletedMatch> _queue = Channel.CreateUnbounded<CompletedMatch>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _serial = new(1, 1);

    public ValueTask EnqueueAsync(CompletedMatch match, CancellationToken ct) => _queue.Writer.WriteAsync(match, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var m in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await ProcessAsync(m, stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogError(e, "Post-game processing failed for {MatchId}", m.Setup.MatchId); }
        }
    }

    private static bool IsHuman(SeatSetup s) => s.Kind != AccountKind.Bot;

    /// <summary>Processes one completed match. Returns false when it was already processed.</summary>
    public async Task<bool> ProcessAsync(CompletedMatch match, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try { return await ProcessCoreAsync(match, ct); }
        finally { _serial.Release(); }
    }

    private async Task<bool> ProcessCoreAsync(CompletedMatch m, CancellationToken ct)
    {
        var s = m.Setup;
        var seats = new[] { s.SeatA, s.SeatB };
        var aborted = m.Result.Reason == EndReason.Aborted;
        var pool = RatingPools.For(s.TimeControl);
        var visible = RatingPools.IsVisible(pool);
        var bothHuman = IsHuman(s.SeatA) && IsHuman(s.SeatB);
        var rate = !aborted && s.Mode != MatchMode.Challenge && (visible ? s.Rated : bothHuman);

        // 1. compute
        var before = new RatingRecord?[2];
        var after = new RatingRecord?[2];
        var snapshots = new List<RatingSnapshot>();
        if (rate)
        {
            before[0] = await ratings.GetAsync(s.SeatA.UserId, pool, ct);
            before[1] = await ratings.GetAsync(s.SeatB.UserId, pool, ct);
            // Bots are rating-matched to their opponent (bots/), so a bot counts as an equal: the human's rating with a firm RD.
            for (var seat = 0; seat < 2; seat++)
                if (!IsHuman(seats[seat]) && IsHuman(seats[1 - seat]))
                    before[seat] = before[1 - seat]! with { UserId = seats[seat].UserId, Rd = Math.Min(before[1 - seat]!.Rd, 150) };
            for (var seat = 0; seat < 2; seat++)
            {
                if (!IsHuman(seats[seat])) continue;
                after[seat] = ratingService.Apply(before[seat]!, before[1 - seat]!, Score(m.Result, seat), m.EndedAt);
                snapshots.Add(new RatingSnapshot(seat, pool, before[seat]!.Rating, after[seat]!.Rating));
            }
        }

        // 2. save (idempotency gate)
        var record = new GameRecord(s.MatchId, s, m.Swapped, m.Actions, m.Result, m.StartedAt, m.EndedAt, snapshots);
        if (!await games.SaveCompletedAsync(record, ct)) return false;

        // 3. ratings + tier notifications
        var tierChanges = new TierChangeDto?[2];
        for (var seat = 0; seat < 2; seat++)
        {
            if (after[seat] is not { } a) continue;
            await ratings.PutAsync(a, ct);
            if (!visible) continue;
            var change = tiers.Change(before[seat]!.Rating, a.Rating);
            tierChanges[seat] = change;
            if (change.Promoted || change.Demoted)
            {
                var n = new NotificationDto(change.Promoted ? NotificationKind.Promotion : NotificationKind.Demotion,
                    change.Promoted ? "Promoted!" : "Demoted", $"{change.After.Tier} {Roman(change.After.Division)}".Trim(), s.MatchId, null);
                await Safe(() => notifier.ToUserAsync(seats[seat].UserId, c => c.Notification(n)));
            }
        }

        // 4-5. streaks and quests
        var progressBySeat = new ProgressDto?[2];
        if (!aborted)
            for (var seat = 0; seat < 2; seat++)
                if (IsHuman(seats[seat]))
                    progressBySeat[seat] = await progress.ApplyGameAsync(seats[seat].UserId, m.Result.WinnerSeat == seat, m.EndedAt, ct);

        // 6. analysis
        // Arena games are not classic games: the tablebase cannot judge them.
        var analysisReady = !aborted && !TicTacToeUltimate.Engine.Clocks.MoveClock.IsArena(s.TimeControl) && tablebases.GetStatus(s.Rules) == TablebaseStatus.Ready;
        int[]? blunders = null;
        if (analysisReady)
        {
            try { blunders = (await analysis.AnalyzeAsync(record, ct))?.BlundersBySeat; }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning(e, "Analysis failed for {MatchId}", s.MatchId); }
        }

        // 7. summaries
        var rematch = s.Mode is MatchMode.Casual or MatchMode.Unlimited or MatchMode.Challenge or MatchMode.Arena && !s.Rated;
        for (var seat = 0; seat < 2; seat++)
        {
            if (!IsHuman(seats[seat])) continue;
            var userId = seats[seat].UserId;
            RatingChangeDto? rc = null;
            if (visible && after[seat] is { } a && before[seat] is { } b)
            {
                int bi = (int)Math.Round(b.Rating), ai = (int)Math.Round(a.Rating);
                rc = new RatingChangeDto(pool, bi, ai, ai - bi, ratingService.IsProvisional(a));
            }
            var winStreak = visible ? await WinStreakAsync(userId, pool, ct) : 0;
            var summary = new PostGameSummaryDto(s.MatchId, m.Result, ResultFor(m.Result, seat), rc, tierChanges[seat], winStreak,
                progressBySeat[seat], analysisReady, blunders is { Length: 2 } ? blunders[seat] : null, rematch);
            await Safe(() => notifier.ToUserAsync(userId, c => c.PostGameSummary(summary)));
        }
        return true;
    }

    private async Task<int> WinStreakAsync(Guid userId, RatingPool pool, CancellationToken ct)
    {
        var list = await games.ListForUserAsync(userId, null, 100, ct);
        var n = 0;
        foreach (var g in list)
        {
            if (RatingPools.For(g.Setup.TimeControl) != pool || g.Result.Reason == EndReason.Aborted || g.Setup.Mode == MatchMode.Challenge) continue;
            var seat = g.Setup.SeatA.UserId == userId ? 0 : 1;
            if (g.Result.WinnerSeat == seat) n++;
            else break;
        }
        return n;
    }

    /// <summary>Glicko score for a seat.</summary>
    public static double Score(MatchResultDto r, int seat) => r.WinnerSeat is { } w ? (w == seat ? 1 : 0) : 0.5;

    /// <summary>Result from one seat's perspective.</summary>
    public static GameResultForUser ResultFor(MatchResultDto r, int seat) =>
        r.Reason == EndReason.Aborted ? GameResultForUser.Aborted
        : r.WinnerSeat is { } w ? (w == seat ? GameResultForUser.Win : GameResultForUser.Loss)
        : r.Outcome == Outcome.Draw || r.Reason == EndReason.Draw ? GameResultForUser.Draw : GameResultForUser.Aborted;

    private static string Roman(int d) => d switch { 1 => "I", 2 => "II", 3 => "III", _ => "" };

    private async Task Safe(Func<Task> send)
    {
        try { await send(); }
        catch (Exception e) { logger.LogWarning(e, "Notify failed"); }
    }

    /// <summary>Current time (for tests/diagnostics).</summary>
    internal DateTimeOffset Now => time.GetUtcNow();
}
