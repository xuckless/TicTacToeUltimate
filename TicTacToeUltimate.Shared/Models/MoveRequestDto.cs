namespace TicTacToeUltimate.Shared.Models;

public class MoveRequestDto
{
    public int Row { get; set; }
    public int Col { get; set; }
    public string GameId { get; set; } = "default";
}