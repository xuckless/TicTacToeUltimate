using System.Net.Http.Json;
using TicTacToeUltimate.Shared.Models;

namespace TicTacToeUltimate.Shared.Services;

public class ApiClient(HttpClient http)
{
    public Task<string> PingAsync() => http.GetStringAsync("/ping");

    public async Task<List<PlayerDto>> GetPlayersAsync()
        => await http.GetFromJsonAsync<List<PlayerDto>>("/api/players") ?? new();
}