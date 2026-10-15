namespace TicTacToeUltimate.Engine.Clocks;

public enum TimeControl : byte { Ranked = 0, CasualX2 = 1, CasualX4 = 2, Unlimited = 3, Daily = 4 }

/// <summary>Pure move-clock schedule (WP1).</summary>
public static class MoveClock
{
    /// <summary>Daily allowance per move.</summary>
    public static readonly TimeSpan DailyPerMove = TimeSpan.FromHours(24);

    /// <summary>1 for Ranked, 2 for CasualX2, 4 for CasualX4, 0 otherwise.</summary>
    public static int Scale(TimeControl tc) => throw new NotImplementedException();

    /// <summary>Allowance for a turn that STARTS at gameElapsed. null for Unlimited; DailyPerMove for Daily.
    /// Sudden-death (s = Scale): e &lt; 60s·s -> 15s·s; else max(2s·s, 10s·s - floor((e-60s·s)/(12s·s))·1s·s).</summary>
    public static TimeSpan? Allowance(TimeControl tc, TimeSpan gameElapsed) => throw new NotImplementedException();

    /// <summary>True for scaled sudden-death controls once gameElapsed &gt;= 60s·s.</summary>
    public static bool IsSuddenDeath(TimeControl tc, TimeSpan gameElapsed) => throw new NotImplementedException();

    /// <summary>Next point (in game-elapsed time) where the allowance changes, and the new allowance; null if none (floor reached, Unlimited, Daily).</summary>
    public static (TimeSpan AtGameElapsed, TimeSpan Allowance)? NextStep(TimeControl tc, TimeSpan gameElapsed) => throw new NotImplementedException();
}
