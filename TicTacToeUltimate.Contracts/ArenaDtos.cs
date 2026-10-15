using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Arena;

namespace TicTacToeUltimate.Contracts;

/// <summary>Arena (health) ruleset as sent to clients.</summary>
public sealed record ArenaRulesDto(string Id, int Hp, int[] Ramp, int CritBonus, ComboKind Combo, int RedAlertMultiplier,
    int RoundMs, int RedAlertMs, int MoveMs, OvertimeStarter OvertimeStarter)
{
    public static ArenaRulesDto From(ArenaRules r) => new(r.Id, r.Hp, [.. r.Ramp], r.CritBonus, r.Combo, r.RedAlertMultiplier,
        r.RoundMs, r.RedAlertMs, r.MoveMs, r.OvertimeStarter);

    /// <summary>Engine rules over the given board rules.</summary>
    public ArenaRules ToEngine(RuleSet board) => new()
    {
        Id = Id, Board = board, Hp = Hp, Ramp = [.. Ramp], CritBonus = CritBonus, Combo = Combo, RedAlertMultiplier = RedAlertMultiplier,
        RoundMs = RoundMs, RedAlertMs = RedAlertMs, MoveMs = MoveMs, OvertimeStarter = OvertimeStarter,
    };
}

/// <summary>One scoring move (shatter). Seats, not colours.</summary>
public sealed record ArenaHitDto(int Ply, int Seat, int Lines, int[] Shattered, int Damage, int Combo, bool Crit, bool RedAlert,
    int BrokenStreak /* victim streak reset by this line; 0 = none */, bool Overtime);

/// <summary>Arena scoreboard after the latest action. Arrays are indexed by seat.</summary>
public sealed record ArenaStateDto(int[] Hp, int[] Streak, int[] Lines, int[] NextLineDamage, int ElapsedMs, bool RedAlert, bool Overtime,
    int? OvertimeFirstSeat, ArenaHitDto? LastHit);
