using System.Text.Json;
using BackendWebAPI.GameEngine;
using StackExchange.Redis;

namespace BackendWebAPI.Services;

public class RedisStore(IConnectionMultiplexer mux)
{
    private readonly IDatabase _db = mux.GetDatabase();
    private static string Key(string gameId) => $"game:{gameId}:state";

    public async Task<GameState> GetOrCreateAsync(string gameId, IGameEngine engine)
    {
        var v = await _db.StringGetAsync(Key(gameId));
        if (v.HasValue) return JsonSerializer.Deserialize<GameState>(v!)!;
        var s = engine.StartNewGame();
        await SaveAsync(gameId, s);
        return s;
    }

    public Task SaveAsync(string gameId, GameState s) =>
        _db.StringSetAsync(Key(gameId), JsonSerializer.Serialize(s));

    public async Task<GameState> ResetAsync(string gameId, IGameEngine engine)
    {
        var s = engine.StartNewGame();
        await SaveAsync(gameId, s);
        return s;
    }
}