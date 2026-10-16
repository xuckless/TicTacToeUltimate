using System.Collections.Concurrent;
using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.Matchmaking;

/// <summary>
/// Who is around, by rating. Fed at Hello (headline rating) and read by the matchmaker: when few players sit near a queued
/// player's rating, a bot is requested at once and paired within a few seconds instead of after the normal wait. Humans
/// are still paired first on every tick, so a real opponent who shows up always wins over the bot.
/// </summary>
public sealed class PlayerPopulation(IPresenceTracker presence)
{
    private readonly ConcurrentDictionary<Guid, int> _ratings = new();

    /// <summary>Remembers a user's headline rating.</summary>
    public void Record(Guid userId, int rating) => _ratings[userId] = rating;

    /// <summary>Forgets a user (account deletion / long offline).</summary>
    public void Forget(Guid userId) => _ratings.TryRemove(userId, out _);

    /// <summary>Online players (other than <paramref name="except"/>) within ±<paramref name="window"/> of <paramref name="rating"/>.</summary>
    public int CountOnlineNear(int rating, int window, Guid except)
    {
        var n = 0;
        foreach (var (id, r) in _ratings)
            if (id != except && Math.Abs(r - rating) <= window && presence.IsOnline(id)) n++;
        return n;
    }
}
