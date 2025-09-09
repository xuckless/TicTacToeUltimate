namespace TicTacToeUltimate.Shared.Models;

public class GameSnapshotDto
{
    // e.g., "a11;b22;b13"
    public string RenderString { get; set; } = string.Empty;

    // winner declared on the current (last-6) board
    public bool Victory { get; set; }

    // [[row,col],[row,col],[row,col]] to highlight green
    public int[][] VictoryRow { get; set; } = Array.Empty<int[]>();
}