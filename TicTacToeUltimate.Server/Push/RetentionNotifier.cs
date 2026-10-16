using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Progression;

namespace TicTacToeUltimate.Server.Push;

/// <summary>
/// Duolingo-style nudges in Bert's voice, once per user per day, at the user's local hour:
///  - streak at risk (alive, not secured today) → "Your N-day streak ends at midnight";
///  - no game for InactiveDays → "Bert challenges you" (opens the Casual queue).
/// Only users with a push token are considered. Runs every 15 minutes.
/// </summary>
public sealed class RetentionNotifier(IPushTokenRepository tokens, IUserRepository users, IProgressRepository progress, IPushSender push,
    IOptions<PushOptions> options, TimeProvider time, ILogger<RetentionNotifier> log) : BackgroundService
{
    private readonly ConcurrentDictionary<(Guid, string, DateOnly), byte> _sent = new();

    /// <summary>Message pool for the streak reminder.</summary>
    public static readonly string[] StreakLines =
    [
        "Your {0}-day streak ends at midnight. One game keeps it alive!",
        "Bert here. {0} days strong… don't let it fade like an old piece.",
        "Psst — your {0}-day streak is waiting. One quick game?",
    ];
    /// <summary>Message pool for the comeback challenge.</summary>
    public static readonly string[] ComebackLines =
    [
        "Bert challenges you to a game. Think you still have it?",
        "The board misses you. A casual game takes 90 seconds.",
        "New opponents are online right now. Jump in?",
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await RunOnceAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Retention pass failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One pass (exposed for tests).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var o = options.Value;
        var sent = 0;
        foreach (var userId in await tokens.UsersWithTokensAsync(ct))
        {
            var u = await users.GetAsync(userId, ct);
            if (u is null || u.Kind == AccountKind.Deleted) continue;
            var zone = LocalDays.Zone(u.TimeZone);
            var local = TimeZoneInfo.ConvertTime(now, zone);
            var day = DateOnly.FromDateTime(local.DateTime);
            var s = await progress.GetStreakAsync(userId, ct);
            var dto = StreakRules.ToDto(s, day);
            if (local.Hour == o.StreakReminderHour && dto.AtRisk && dto.Current > 0 && _sent.TryAdd((userId, "streak", day), 0))
            {
                var line = string.Format(StreakLines[Math.Abs(userId.GetHashCode()) % StreakLines.Length], dto.Current);
                await push.SendAsync(userId, new NotificationDto(NotificationKind.StreakReminder, "Bert 👻", line, null, "play?tc=Casual"), ct);
                sent++;
                continue;
            }
            var idleDays = (now - u.LastSeenAt).TotalDays;
            if (local.Hour == o.ComebackHour && idleDays >= o.InactiveDays && _sent.TryAdd((userId, "comeback", day), 0))
            {
                var line = ComebackLines[Math.Abs(userId.GetHashCode()) % ComebackLines.Length];
                await push.SendAsync(userId, new NotificationDto(NotificationKind.Challenge, "Bert challenges you", line, null, "play?tc=Casual"), ct);
                sent++;
            }
        }
        // Keep the dedupe set small.
        foreach (var k in _sent.Keys.Where(k => k.Item3 < DateOnly.FromDateTime(now.UtcDateTime).AddDays(-2)).ToArray()) _sent.TryRemove(k, out _);
        return sent;
    }
}
