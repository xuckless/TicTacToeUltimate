using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;

namespace TicTacToeUltimate.Shared.Services;

/// <summary>One replay position.</summary>
public sealed record ReplayFrame(int Ply, string Board, int? Ghost, int? LastPlaced);

/// <summary>
/// Rebuilds read-only replay positions from a recorded action log. The server recorded each placement's colour and vanished cell,
/// so no rules evaluation is needed; the ghost (next to vanish) is the oldest piece once a FIFO window is full.
/// </summary>
public static class ReplayFrames
{
    /// <summary>Frames for plies 0..Actions.Length.</summary>
    public static IReadOnlyList<ReplayFrame> Build(RuleSetDto rules, IReadOnlyList<ActionDto> actions)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(actions);
        var n = rules.BoardSize * rules.BoardSize;
        var board = new char[n];
        Array.Fill(board, '.');
        var order = new List<int>();
        var frames = new List<ReplayFrame>(actions.Count + 1) { new(0, new string(board), null, null) };
        foreach (var a in actions)
        {
            int? placed = null;
            if (a.Kind == ActionKind.Place && a.Cell is int cell && cell >= 0 && cell < n)
            {
                if (a.VanishedCell is int v && v >= 0 && v < n)
                {
                    board[v] = '.';
                    order.Remove(v);
                }
                board[cell] = a.Color == Color.O ? 'O' : 'X';
                order.Add(cell);
                placed = cell;
            }
            int? ghost = rules.Removal == RemovalKind.GlobalFifo && order.Count >= rules.Window && order.Count > 0 ? order[0] : null;
            frames.Add(new ReplayFrame(a.Ply + 1, new string(board), ghost, placed));
        }
        return frames;
    }
}
