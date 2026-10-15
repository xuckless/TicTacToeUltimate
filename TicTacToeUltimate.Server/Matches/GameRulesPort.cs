using System.Diagnostics.CodeAnalysis;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Engine;

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

/// <summary>Ruleset-id helpers over the engine's <see cref="RuleSet.TryParse"/> (single source of truth for ids).</summary>
public static class RuleIds
{
    /// <summary>Parses an id such as "n3k3-cap3-v0-w1-pie"; false when malformed.</summary>
    public static bool TryParse(string? id, [NotNullWhen(true)] out RuleSet? rules)
    {
        rules = null;
        return !string.IsNullOrWhiteSpace(id) && RuleSet.TryParse(id.Trim(), out rules);
    }

    /// <summary>Parses an id; falls back to <paramref name="fallback"/> on bad input.</summary>
    public static RuleSet Parse(string? id, RuleSet fallback) => TryParse(id, out var r) ? r : fallback;

    /// <summary>Parses or falls back to <see cref="RuleSet.Default"/>.</summary>
    public static RuleSet ParseOrDefault(string? id) => Parse(id, RuleSet.Default);
}
