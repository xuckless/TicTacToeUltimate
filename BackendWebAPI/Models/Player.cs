namespace BackendWebAPI.Models;
public class Player
{
    public Guid PlayerId { get; set; }
    public Guid Sub { get; set; }
    public string Username { get; set; } = null!;
    public int Elo { get; set; } = 500;
    public string Email { get; set; } = null!;
    public int? Age { get; set; }
}