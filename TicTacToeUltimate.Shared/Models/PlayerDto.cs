namespace TicTacToeUltimate.Shared.Models;

public record PlayerDto(Guid PlayerId, Guid Sub, string Username, int Elo, string Email, int? Age);