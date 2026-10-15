namespace TicTacToeUltimate.Shared.Models;

/// <summary>
/// Represents a snapshot of the game state for rendering purposes.
/// </summary>
public class GameSnapshotDto
{
    /// <summary>
    /// Gets or sets the render string representation of the game state.
    /// Example format: "a11;b22;b13"
    /// </summary>
    public string RenderString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a victory has been declared on the current (last-6) board.
    /// </summary>
    public bool Victory { get; set; }

    /// <summary>
    /// Gets or sets the victory row coordinates to highlight in green.
    /// Format: [[row,col],[row,col],[row,col]]
    /// </summary>
    public int[][] VictoryRow { get; set; } = Array.Empty<int[]>();
}