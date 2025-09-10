namespace BackendWebAPI.GameEngine;

public sealed class GameState
{
    // Oldest -> newest, items like "a11", "b23"
    public List<string> Moves { get; set; } = new();

    // Direct string representation of the current visible window
    public string RenderString { get; set; } = string.Empty;

    // 'a' = X (Player A), 'b' = O (Player B)
    public char Turn { get; set; } = 'a';

    public bool Victory { get; set; }

    // [[row,col],[row,col],[row,col]] (1-based)
    public int[][] VictoryRow { get; set; } = Array.Empty<int[]>();
}