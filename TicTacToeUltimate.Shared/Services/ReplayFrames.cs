using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Arena;

namespace TicTacToeUltimate.Shared.Services;

/// <summary>One replay position. <c>ThinkMs</c> is how long the mover took before this position appeared (drives timed playback).</summary>
public sealed record ReplayFrame(int Ply, string Board, int? Ghost, int? LastPlaced, int ThinkMs = 0, bool Swapped = false);

/// <summary>
/// Replay positions rebuilt by the real engine from the recorded action log, so the replay is exactly what the players saw:
/// the same board, the same faded "next to vanish" piece, the same colours after a pie swap. (The previous version repainted
/// the board from recorded colours and vanish cells, which drifted from the engine in edge cases.) Arena games go through
/// the arena engine, which re-derives shatters from the think times.
/// </summary>
public static class ReplayFrames
{
    /// <summary>Frames for plies 0..Actions.Length.</summary>
    public static IReadOnlyList<ReplayFrame> Build(RuleSetDto rules, IReadOnlyList<ActionDto> actions, ArenaRules? arena = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(actions);
        if (arena is not null) return BuildArena(arena, actions);
        var rs = rules.ToEngine();
        var state = Rules.Initial(rs);
        var frames = new List<ReplayFrame>(actions.Count + 1) { new(0, Notation.Board(state), Rules.NextToVanish(state), null) };
        foreach (var a in actions)
        {
            var action = a.Kind switch
            {
                ActionKind.Place when a.Cell is int c => GameAction.Place(c),
                ActionKind.Swap => GameAction.Swap,
                ActionKind.KeepSides => GameAction.KeepSides,
                _ => (GameAction?)null,
            };
            if (action is null) break;
            var step = Rules.Apply(state, action.Value);
            if (!step.Ok) break;   // a corrupt log: stop at the last consistent position instead of inventing one
            state = step.State;
            frames.Add(new ReplayFrame(a.Ply + 1, Notation.Board(state), state.Phase == GamePhase.Ended ? null : Rules.NextToVanish(state),
                a.Kind == ActionKind.Place ? a.Cell : null, a.ThinkMs, state.Swapped));
        }
        return frames;
    }

    /// <summary>Arena replay: shatters are not in the action log, so the arena engine re-derives them (think times drive red alert).</summary>
    private static List<ReplayFrame> BuildArena(ArenaRules arena, IReadOnlyList<ActionDto> actions)
    {
        var s = ArenaEngine.Initial(arena);
        var frames = new List<ReplayFrame> { new(0, ArenaEngine.Board(s), null, null) };
        foreach (var a in actions)
        {
            if (a.Kind != ActionKind.Place || a.Cell is not int cell) continue;
            var step = ArenaEngine.Apply(s, cell, a.ThinkMs);
            if (!step.Ok) break;
            s = step.State;
            frames.Add(new ReplayFrame(a.Ply + 1, ArenaEngine.Board(s), ArenaEngine.NextToVanish(s), cell, a.ThinkMs));
        }
        return frames;
    }
}
