using System.Net.Http.Json;
using TicTacToeUltimate.Shared.Models;

namespace TicTacToeUltimate.Shared.Services;

/// <summary>
/// Provides HTTP client services for API communication.
/// </summary>
/// <param name="http">The HTTP client instance to use for requests.</param>
public class ApiClient(HttpClient http)
{
    /// <summary>
    /// Sends a ping request to the server to check connectivity.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains the ping response string.</returns>
    public Task<string> PingAsync() => http.GetStringAsync("/ping");

    /// <summary>
    /// Retrieves a list of all players from the API.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of player DTOs, or an empty list if the request fails.</returns>
    public async Task<List<PlayerDto>> GetPlayersAsync()
        => await http.GetFromJsonAsync<List<PlayerDto>>("/api/players") ?? new();
}