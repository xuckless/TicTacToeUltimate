namespace BackendWebAPI.GameEngine;

public interface IGameEngine
{
    GameState StartNewGame();
    GameState ApplyMove(GameState state, int row, int col, char player);
}