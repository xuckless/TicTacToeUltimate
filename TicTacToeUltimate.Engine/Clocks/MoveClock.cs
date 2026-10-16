namespace TicTacToeUltimate.Engine.Clocks;

/// <summary>Queue time controls. Ranked/CasualX2/CasualX4 are retired from the UI (2026-10-15 mode restructure) but kept for compatibility.
/// Casual = Ultimate on the sudden-death schedule, unrated, timeout auto-moves. OldSchool = plain tic-tac-toe on the same clock.</summary>
public enum TimeControl : byte { Ranked = 0, CasualX2 = 1, CasualX4 = 2, Unlimited = 3, Daily = 4, ArenaCasual = 5, ArenaRanked = 6, Casual = 7, OldSchool = 8 }

/// <summary>Pure move-clock schedule (integer tick arithmetic only).</summary>
public static class MoveClock
{
    /// <summary>Daily allowance per move.</summary>
    public static readonly TimeSpan DailyPerMove = TimeSpan.FromHours(24);

    /// <summary>Arena per-move limit (flat, no sudden-death schedule; the 2:00 round is handled by the arena engine).</summary>
    public static readonly TimeSpan ArenaPerMove = TimeSpan.FromSeconds(5);

    /// <summary>True for the Arena (health) time controls.</summary>
    public static bool IsArena(TimeControl tc) => tc is TimeControl.ArenaCasual or TimeControl.ArenaRanked;

    private const long Sec = TimeSpan.TicksPerSecond;

    /// <summary>1 for Ranked/Casual/OldSchool, 2 for CasualX2, 4 for CasualX4, 0 otherwise.</summary>
    public static int Scale(TimeControl tc) => tc switch
    {
        TimeControl.Ranked or TimeControl.Casual or TimeControl.OldSchool => 1,
        TimeControl.CasualX2 => 2,
        TimeControl.CasualX4 => 4,
        _ => 0,
    };

    /// <summary>Allowance for a turn that STARTS at gameElapsed. null for Unlimited; DailyPerMove for Daily.
    /// Sudden-death (s = Scale): e &lt; 60s·s -> 15s·s; else max(2s·s, 10s·s - floor((e-60s·s)/(12s·s))·1s·s).</summary>
    public static TimeSpan? Allowance(TimeControl tc, TimeSpan gameElapsed)
    {
        if (tc == TimeControl.Unlimited) return null;
        if (tc == TimeControl.Daily) return DailyPerMove;
        if (IsArena(tc)) return ArenaPerMove;
        long s = Scale(tc);
        if (s == 0) return null;
        long e = Math.Max(0, gameElapsed.Ticks);
        if (e < 60 * Sec * s) return TimeSpan.FromTicks(15 * Sec * s);
        long steps = (e - 60 * Sec * s) / (12 * Sec * s);
        long secs = Math.Max(2, 10 - steps);
        return TimeSpan.FromTicks(secs * Sec * s);
    }

    /// <summary>True for scaled sudden-death controls once gameElapsed &gt;= 60s·s.</summary>
    public static bool IsSuddenDeath(TimeControl tc, TimeSpan gameElapsed)
    {
        long s = Scale(tc);
        return s > 0 && gameElapsed.Ticks >= 60 * Sec * s;
    }

    /// <summary>Next point (in game-elapsed time) where the allowance changes, and the new allowance; null if none (floor reached, Unlimited, Daily).</summary>
    public static (TimeSpan AtGameElapsed, TimeSpan Allowance)? NextStep(TimeControl tc, TimeSpan gameElapsed)
    {
        long s = Scale(tc);
        if (s == 0) return null;
        long e = Math.Max(0, gameElapsed.Ticks);
        if (e < 60 * Sec * s) return (TimeSpan.FromTicks(60 * Sec * s), TimeSpan.FromTicks(10 * Sec * s));
        long steps = (e - 60 * Sec * s) / (12 * Sec * s);
        long current = 10 - steps;
        if (current <= 2) return null;
        return (TimeSpan.FromTicks(60 * Sec * s + (steps + 1) * 12 * Sec * s), TimeSpan.FromTicks((current - 1) * Sec * s));
    }
}
