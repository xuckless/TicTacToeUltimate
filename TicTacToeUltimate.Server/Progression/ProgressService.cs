using System.Collections.Concurrent;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;

namespace TicTacToeUltimate.Server.Progression;

/// <summary>Local-day helpers (user time zone; invalid ids fall back to UTC).</summary>
public static class LocalDays
{
    /// <summary>Resolves a time zone id.</summary>
    public static TimeZoneInfo Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>The user's local calendar day at <paramref name="now"/>.</summary>
    public static DateOnly Day(DateTimeOffset now, string? tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone(tz)).DateTime);

    /// <summary>Next local midnight as an absolute instant.</summary>
    public static DateTimeOffset NextMidnight(DateTimeOffset now, string? tz)
    {
        var zone = Zone(tz);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var midnight = local.Date.AddDays(1);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }
}

/// <summary>Pure streak and quest rules (§6.7 steps 4-5).</summary>
public static class StreakRules
{
    /// <summary>Max banked freezes.</summary>
    public const int MaxFreezes = 2;

    /// <summary>Applies one played (non-aborted) game on local day <paramref name="d"/>.</summary>
    public static StreakRecord Played(StreakRecord s, DateOnly d)
    {
        if (s.LastPlayedDay == d) return s;
        int current, freezes = s.Freezes;
        if (s.LastPlayedDay is { } last && last < d)
        {
            var gap = d.DayNumber - last.DayNumber - 1;
            if (gap == 0) current = s.Current + 1;
            else if (gap <= freezes) { freezes -= gap; current = s.Current + 1; }
            else current = 1;
        }
        else current = s.LastPlayedDay is null ? 1 : Math.Max(1, s.Current);   // clock moved backwards: keep
        return s with { Current = current, Best = Math.Max(s.Best, current), LastPlayedDay = s.LastPlayedDay is { } l && l > d ? l : d, Freezes = freezes };
    }

    /// <summary>Applies a win to the quest day; may award a freeze into <paramref name="streak"/>.</summary>
    public static (QuestDayRecord Day, StreakRecord Streak) Won(QuestDayRecord q, StreakRecord streak)
    {
        var wasComplete = q.Wins >= 5;
        q = q with { Wins = q.Wins + 1 };
        if (!wasComplete && q.Wins >= 5 && !q.FreezeAwarded && streak.Freezes < MaxFreezes)
        {
            q = q with { FreezeAwarded = true };
            streak = streak with { Freezes = streak.Freezes + 1 };
        }
        if (q.BonusTarget is { } t && !q.BonusClaimed && q.Wins >= t) q = q with { BonusClaimed = true };
        return (q, streak);
    }

    /// <summary>Streak as displayed today: broken streaks show 0.</summary>
    public static StreakDto ToDto(StreakRecord s, DateOnly today)
    {
        var secured = s.LastPlayedDay == today;
        var alive = s.LastPlayedDay is { } last && (secured || today.DayNumber - last.DayNumber - 1 <= s.Freezes);
        var current = alive ? s.Current : 0;
        var atRisk = !secured && current > 0;
        return new StreakDto(current, s.Best, s.Freezes, secured, atRisk);
    }
}

/// <summary>Per-user async locks so progress read-modify-write is serialized across PostGame and rewards.</summary>
public sealed class UserLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Acquires the user's lock.</summary>
    public async Task<IDisposable> AcquireAsync(Guid userId, CancellationToken ct)
    {
        var s = _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await s.WaitAsync(ct);
        return new Releaser(s);
    }

    private sealed class Releaser(SemaphoreSlim s) : IDisposable { public void Dispose() => s.Release(); }
}

/// <summary>Builds <see cref="ProgressDto"/> and applies post-game progress.</summary>
public sealed class ProgressService(IProgressRepository progress, IUserRepository users, UserLocks locks, TimeProvider time)
{
    /// <summary>Current progress for a user.</summary>
    public async Task<ProgressDto> GetAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var tz = (await users.GetAsync(userId, ct))?.TimeZone;
        var day = LocalDays.Day(now, tz);
        var s = await progress.GetStreakAsync(userId, ct);
        var q = await progress.GetQuestDayAsync(userId, day, ct);
        return Build(s, q, day, LocalDays.NextMidnight(now, tz));
    }

    internal static ProgressDto Build(StreakRecord s, QuestDayRecord q, DateOnly day, DateTimeOffset resetsAt)
    {
        var quests = new List<QuestDto>
        {
            new("win1", "Win 1 game", Math.Min(q.Wins, 1), 1, q.Wins >= 1, false),
            new("win3", "Win 3 games", Math.Min(q.Wins, 3), 3, q.Wins >= 3, false),
            new("win5", "Win 5 games", Math.Min(q.Wins, 5), 5, q.Wins >= 5, false),
        };
        if (q.BonusTarget is { } t)
            quests.Add(new QuestDto("bonus", $"Bonus: reach {t} wins", Math.Min(q.Wins, t), t, q.Wins >= t, true));
        return new ProgressDto(StreakRules.ToDto(s, day), quests.ToArray(), day, resetsAt.ToUnixTimeMilliseconds(), q.BonusTarget is null);
    }

    /// <summary>Applies a finished (non-aborted) game for one user. Returns the new progress.</summary>
    public async Task<ProgressDto> ApplyGameAsync(Guid userId, bool won, DateTimeOffset at, CancellationToken ct)
    {
        using var _ = await locks.AcquireAsync(userId, ct);
        var tz = (await users.GetAsync(userId, ct))?.TimeZone;
        var day = LocalDays.Day(at, tz);
        var s = StreakRules.Played(await progress.GetStreakAsync(userId, ct), day);
        var q = await progress.GetQuestDayAsync(userId, day, ct);
        if (won) (q, s) = StreakRules.Won(q, s);
        await progress.PutStreakAsync(s, ct);
        await progress.PutQuestDayAsync(q, ct);
        return Build(s, q, day, LocalDays.NextMidnight(at, tz));
    }
}
