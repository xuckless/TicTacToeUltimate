using System;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace BackendWebAPI.GameEngine;

public class GameEngine : IGameEngine
{
    private const int Window = 7; // visible queue size (oldest drops first)

    public GameState StartNewGame() => new();

    public GameState ApplyMove(GameState state, int row, int col, char _player)
    {
        if (state.Victory) return state;
        if (row is < 1 or > 3 || col is < 1 or > 3) return state;
        
        // (row, col) = (col, row);

        // --- 1) PREVIEW (non-mutating): as-if the oldest were dropped when full ---
        var preview = state.Moves.Count >= Window ? state.Moves.Skip(1) : state.Moves.AsEnumerable();

        // Build occupancy over the preview window
        var occ = new bool[3,3];
        foreach (var mv in preview)
            if (TryParseMove(mv, out _, out var r, out var c))
                occ[r-1, c-1] = true;

        // If the target is occupied in the preview, reject WITHOUT mutating state
        if (occ[row-1, col-1]) return state;

        // --- 2) MUTATE: drop oldest if full, then add new move (single pass) ---
        if (state.Moves.Count >= Window)
            state.Moves.RemoveAt(0);

        state.Moves.Add($"{state.Turn}{row}{col}");

        // --- 3) REBUILD current board from the visible window & decide victory ---
        var board = new char[3,3]; // '\0' means empty
        foreach (var mv in state.Moves)
            if (TryParseMove(mv, out var p, out var r, out var c))
                board[r-1, c-1] = p; // 'a' or 'b'

        (int r,int c)[] win = new (int,int)[3];
        bool Cell(char p, int r, int c) => board[r, c] == p;
        bool Line(char p, (int,int) a, (int,int) b, (int,int) d)
        {
            var ok = Cell(p, a.Item1, a.Item2) && Cell(p, b.Item1, b.Item2) && Cell(p, d.Item1, d.Item2);
            if (ok) win = new[] { a, b, d };
            return ok;
        }

        var me = state.Turn;
        state.Victory =
            Line(me,(0,0),(0,1),(0,2)) || Line(me,(1,0),(1,1),(1,2)) || Line(me,(2,0),(2,1),(2,2)) ||
            Line(me,(0,0),(1,0),(2,0)) || Line(me,(0,1),(1,1),(2,1)) || Line(me,(0,2),(1,2),(2,2)) ||
            Line(me,(0,0),(1,1),(2,2)) || Line(me,(0,2),(1,1),(2,0));

        state.VictoryRow = state.Victory
            ? win.Select(x => new[] { x.r + 1, x.c + 1 }).ToArray()
            : Array.Empty<int[]>();

        if (!state.Victory)
            state.Turn = state.Turn == 'a' ? 'b' : 'a';

        // --- 4) LOG: render string + pretty board ---
        var render = string.Join(';', state.Moves);
        Console.WriteLine($"[GameEngine] Moves[{state.Moves.Count}] = {render}");
        Console.WriteLine(BoardPretty(board));

        Debug.Assert(state.Moves.Count <= Window, "Window invariant violated");
        return state;
    }

    // mv format "<player><row><col>" e.g., "a11", "b23"
    private static bool TryParseMove(string mv, out char player, out int row, out int col)
    {
        player = '\0'; row = col = 0;
        if (string.IsNullOrWhiteSpace(mv) || mv.Length < 3) return false;

        player = mv[0];
        if (!char.IsLetter(player)) return false;

        if (!int.TryParse(mv[1].ToString(), out row)) return false;
        if (!int.TryParse(mv[2].ToString(), out col)) return false;
        if (row is < 1 or > 3 || col is < 1 or > 3) return false;

        return true;
    }

    private static string BoardPretty(char[,] b)
    {
        // Render as 3x3 grid using X/O for a/b
        string Sym(int r, int c) => b[r,c] switch
        {
            'a' or 'A' => "X",
            'b' or 'B' => "O",
            _ => "."
        };

        var sb = new StringBuilder();
        sb.AppendLine("   c1 c2 c3");
        for (int r = 0; r < 3; r++)
        {
            sb.Append($"r{r+1} ");
            for (int c = 0; c < 3; c++)
                sb.Append(' ').Append(Sym(r,c));
            sb.AppendLine();
        }
        return sb.ToString();
    }
}