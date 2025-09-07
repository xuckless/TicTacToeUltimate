namespace TicTacToeUltimate.Shared.Models;

// this is just a struct - immutable data models
public record PlayerDto(Guid PlayerId, Guid Sub, string Username, int Elo, string Email, int? Age);