using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.Bots;

/// <summary>A participant the pool can hand matches to (a connected bot socket).</summary>
public interface IBotSeeker
{
    /// <summary>Bot user id.</summary>
    Guid BotUserId { get; }
    /// <summary>Bot display name.</summary>
    string BotName { get; }
    /// <summary>Atomically reserves a slot if the bot seeks <paramref name="timeControl"/> and has capacity.</summary>
    bool TryReserveSlot(TimeControl timeControl);
    /// <summary>Returns a reserved slot that never turned into a match.</summary>
    void ReleaseSlot();
    /// <summary>Converts a reservation into a running match.</summary>
    ValueTask StartReservedMatchAsync(Guid matchId, CancellationToken ct);
}

/// <summary>Tracks connected, seeking bots and their free capacity (one seeker per bot user; the newest connection wins).</summary>
public sealed class BotPool(ILogger<BotPool> log) : IBotPool
{
    private readonly ConcurrentDictionary<Guid, IBotSeeker> _seekers = new();

    /// <summary>Registers (or replaces) the seeker for its bot user.</summary>
    public void Register(IBotSeeker seeker) => _seekers[seeker.BotUserId] = seeker;

    /// <summary>Removes the seeker if it is still the registered one.</summary>
    public void Unregister(IBotSeeker seeker) =>
        ((ICollection<KeyValuePair<Guid, IBotSeeker>>)_seekers).Remove(new KeyValuePair<Guid, IBotSeeker>(seeker.BotUserId, seeker));

    /// <summary>Number of registered seekers.</summary>
    public int Count => _seekers.Count;

    /// <inheritdoc />
    public bool TryReserve(TimeControl timeControl, int rating, [NotNullWhen(true)] out BotReservation? reservation)
    {
        foreach (var seeker in _seekers.Values)
        {
            if (!seeker.TryReserveSlot(timeControl)) continue;
            reservation = new BotReservation(seeker.BotUserId, seeker.BotName);
            return true;
        }
        reservation = null;
        return false;
    }

    /// <inheritdoc />
    public void Release(Guid botUserId)
    {
        if (_seekers.TryGetValue(botUserId, out var seeker)) seeker.ReleaseSlot();
    }

    /// <inheritdoc />
    public async ValueTask NotifyMatchCreatedAsync(Guid botUserId, Guid matchId, CancellationToken ct)
    {
        if (!_seekers.TryGetValue(botUserId, out var seeker))
        {
            log.LogWarning("Bot {Bot} disconnected before match {Match} started", botUserId, matchId);
            return;
        }
        await seeker.StartReservedMatchAsync(matchId, ct).ConfigureAwait(false);
    }
}
