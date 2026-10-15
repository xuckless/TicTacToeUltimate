using System.Globalization;
using System.Text.RegularExpressions;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.Server.Matches;

/// <summary>Immutable view of a game position as the match runtime needs it. <see cref="Inner"/> is the adapter's own state.</summary>
public sealed record RulesSnapshot(
    RuleSet Rules, string Board, int Ply, int Placements, GamePhase Phase, Color ToMove, Outcome Outcome,
    int[] WinLine, bool Swapped, int? NextToVanish, int[] LegalCells, object Inner);

/// <summary>Result of applying one action.</summary>
public readonly record struct RulesStep(RulesSnapshot State, MoveError Error, int? VanishedCell)
{
    /// <summary>True when the action was legal.</summary>
    public bool Ok => Error == MoveError.None;
}

/// <summary>Seam over the engine rules so the match runtime can be tested before/independently of WP1.</summary>
public interface IGameRules
{
    /// <summary>Initial position.</summary>
    RulesSnapshot Initial(RuleSet rules);
    /// <summary>Applies an action; never throws for illegal input.</summary>
    RulesStep Apply(RulesSnapshot state, GameAction action);
}

/// <summary>Seam over <see cref="MoveClock"/>.</summary>
public interface IClockSchedule
{
    /// <summary>See <see cref="MoveClock.Allowance"/>.</summary>
    TimeSpan? Allowance(TimeControl tc, TimeSpan gameElapsed);
    /// <summary>See <see cref="MoveClock.IsSuddenDeath"/>.</summary>
    bool IsSuddenDeath(TimeControl tc, TimeSpan gameElapsed);
    /// <summary>See <see cref="MoveClock.NextStep"/>.</summary>
    (TimeSpan AtGameElapsed, TimeSpan Allowance)? NextStep(TimeControl tc, TimeSpan gameElapsed);
}

/// <summary>Production adapter over <see cref="Rules"/>.</summary>
public sealed class EngineGameRules : IGameRules
{
    /// <inheritdoc />
    public RulesSnapshot Initial(RuleSet rules) => Map(TicTacToeUltimate.Engine.Rules.Initial(rules));

    /// <inheritdoc />
    public RulesStep Apply(RulesSnapshot state, GameAction action)
    {
        var r = TicTacToeUltimate.Engine.Rules.Apply((GameState)state.Inner, action);
        return r.Ok ? new RulesStep(Map(r.State), MoveError.None, r.VanishedCell) : new RulesStep(state, r.Error, null);
    }

    private static RulesSnapshot Map(GameState s) => new(
        s.Rules, Notation.Board(s), s.Ply, s.Placements, s.Phase, s.ToMove, s.Outcome, [.. s.WinLine], s.Swapped,
        s.Phase == GamePhase.AwaitingPlacement ? TicTacToeUltimate.Engine.Rules.NextToVanish(s) : null,
        [.. TicTacToeUltimate.Engine.Rules.LegalCells(s)], s);
}

/// <summary>Production adapter over <see cref="MoveClock"/>.</summary>
public sealed class EngineClockSchedule : IClockSchedule
{
    /// <inheritdoc />
    public TimeSpan? Allowance(TimeControl tc, TimeSpan gameElapsed) => MoveClock.Allowance(tc, gameElapsed);
    /// <inheritdoc />
    public bool IsSuddenDeath(TimeControl tc, TimeSpan gameElapsed) => MoveClock.IsSuddenDeath(tc, gameElapsed);
    /// <inheritdoc />
    public (TimeSpan AtGameElapsed, TimeSpan Allowance)? NextStep(TimeControl tc, TimeSpan gameElapsed) => MoveClock.NextStep(tc, gameElapsed);
}

/// <summary>Canonical rules-id parser local to WP2 (mirrors <see cref="RuleSet.Id"/>).</summary>
public static partial class RuleIds
{
    [GeneratedRegex(@"^n(\d+)k(\d+)-(?:fifo(\d+)|cap(\d+)|(none))-v([01])-w([01])-(none|pie|ban)$")]
    private static partial Regex IdRegex();

    /// <summary>Parses an id such as "n3k3-fifo7-v1-w0-pie"; falls back to <paramref name="fallback"/> on bad input.</summary>
    public static RuleSet Parse(string? id, RuleSet fallback)
    {
        if (string.IsNullOrWhiteSpace(id)) return fallback;
        var m = IdRegex().Match(id.Trim());
        if (!m.Success) return fallback;
        int I(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        var removal = m.Groups[3].Success ? RemovalKind.GlobalFifo : m.Groups[4].Success ? RemovalKind.PerPlayerCap : RemovalKind.None;
        var window = m.Groups[3].Success ? I(3) : m.Groups[4].Success ? I(4) : 0;
        var opening = m.Groups[8].Value switch { "pie" => OpeningRule.PieSwap, "ban" => OpeningRule.BanCenter, _ => OpeningRule.None };
        return new RuleSet(I(1), I(2), removal, window, m.Groups[6].Value == "1", m.Groups[7].Value == "1", opening);
    }
}
