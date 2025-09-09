namespace BackendWebAPI.GameEngine;

public class GameEngine : IGameEngine
{
    public GameState StartNewGame() => new();

    public GameState ApplyMove(GameState state, int row, int col, char _player)
    {
        if (state.Victory) return state;

        // prevent playing on an occupied cell (considering only last 6 moves)
        var occupied = new bool[3, 3];
        foreach (var move in state.Moves)
            occupied[move[1] - '1', move[2] - '1'] = true;

        if (occupied[row - 1, col - 1]) return state;

        // add move and enforce “only last 6 renderable moves”
        var moveCode = $"{state.Turn}{row}{col}";
        state.Moves.Add(moveCode);
        if (state.Moves.Count > 6) state.Moves.RemoveAt(0);

        // rebuild board from last 6
        var board = new char[3, 3];
        foreach (var move in state.Moves)
            board[move[1] - '1', move[2] - '1'] = move[0]; // 'a' or 'b'

        // helpers
        bool CellIs(char player, (int r, int c) p) => board[p.r, p.c] == player;

        (int r, int c)[] winningCells = new (int, int)[3];
        bool Line(char player, (int r, int c) a, (int r, int c) b, (int r, int c) c)
        {
            var ok = CellIs(player, a) && CellIs(player, b) && CellIs(player, c);
            if (ok) winningCells = new[] { a, b, c };
            return ok;
        }

        var playerTurn = state.Turn;
        state.Victory =
            Line(playerTurn, (0, 0), (0, 1), (0, 2)) ||
            Line(playerTurn, (1, 0), (1, 1), (1, 2)) ||
            Line(playerTurn, (2, 0), (2, 1), (2, 2)) ||
            Line(playerTurn, (0, 0), (1, 0), (2, 0)) ||
            Line(playerTurn, (0, 1), (1, 1), (2, 1)) ||
            Line(playerTurn, (0, 2), (1, 2), (2, 2)) ||
            Line(playerTurn, (0, 0), (1, 1), (2, 2)) ||
            Line(playerTurn, (0, 2), (1, 1), (2, 0));

        state.VictoryRow = state.Victory
            ? winningCells.Select(rc => new[] { rc.r + 1, rc.c + 1 }).ToArray()
            : Array.Empty<int[]>();

        if (!state.Victory)
            state.Turn = state.Turn == 'a' ? 'b' : 'a';

        return state;
    }
}