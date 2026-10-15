using System.Collections.Immutable;

namespace TicTacToeUltimate.Engine.Analysis;

public enum Value : sbyte { Loss = -1, Draw = 0, Win = 1 }                 // side-to-move perspective
/// <summary>Plies to forced end; 0 for Draw and terminal.</summary>
public readonly record struct PositionValue(Value Value, int Distance);
// Best: keeps the value with optimal distance (fastest win / slowest loss / any draw-keeping move).
// Good: keeps the value but not optimal distance. Blunder: value worsens for the mover (Win->Draw/Loss, Draw->Loss).
public enum MoveClass : byte { Best = 0, Good = 1, Blunder = 2 }

/// <summary>Evaluation of one legal action.</summary>
public sealed record MoveEval(GameAction Action, Value ResultForMover, int Distance, MoveClass Class);
/// <summary>Analysis of one ply.</summary>
public sealed record PlyAnalysis(int Ply, GameAction Played, PositionValue Before, MoveClass Class, ImmutableArray<GameAction> BestActions);

/// <summary>Thrown when a build exceeds its state limit.</summary>
public sealed class TablebaseTooLargeException(string rulesId, long limit) : Exception($"{rulesId} exceeds {limit} states");

/// <summary>Solved table for one ruleset (WP1).</summary>
public sealed class Tablebase
{
    private Tablebase(RuleSet rules) => Rules = rules;

    /// <summary>True when the state key fits 64 bits: N² &lt;= 16 and piece capacity fits (FIFO: Window, Cap: 2·Window, None: N²) with 4 bits/cell + 5-bit count + 1 to-move bit (cap packs two queues).</summary>
    public static bool CanBuild(RuleSet rules) => throw new NotImplementedException();
    /// <summary>Forward BFS from Initial over AwaitingPlacement positions (pie ignored), then counter-based retrograde labelling; unresolved = Draw.</summary>
    public static Tablebase Build(RuleSet rules, long maxStates, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Ruleset.</summary>
    public RuleSet Rules { get; }
    /// <summary>Reachable incl. terminal; Default = 369,802.</summary>
    public int StateCount { get; }
    /// <summary>Default = 331,066.</summary>
    public int NonTerminalCount { get; }
    /// <summary>For AwaitingPlacement: value for ToMove. For AwaitingSwapDecision: value for the chooser if it keeps (= value of the position for O to place). Ended: (Loss,0) for ToMove if won by the opponent, (Draw,0) for Draw.</summary>
    public PositionValue Evaluate(GameState state) => throw new NotImplementedException();
    /// <summary>Per legal action. Swap-phase: Keep = Evaluate(state); Swap = negated value (chooser becomes the other side).</summary>
    public ImmutableArray<MoveEval> EvaluateMoves(GameState state) => throw new NotImplementedException();
}

/// <summary>Game analysis (WP1).</summary>
public static class GameAnalyzer
{
    /// <summary>Analyzes each ply.</summary>
    public static ImmutableArray<PlyAnalysis> Analyze(Tablebase tablebase, RuleSet rules, IReadOnlyList<GameAction> actions) => throw new NotImplementedException();
}
