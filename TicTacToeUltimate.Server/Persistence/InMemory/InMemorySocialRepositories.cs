using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;

namespace TicTacToeUltimate.Server.Persistence.InMemory;

/// <summary>In-memory friends, requests and blocks.</summary>
public sealed class InMemoryFriendRepository : IFriendRepository
{
    private readonly Lock _gate = new();
    private readonly HashSet<(Guid, Guid)> _friends = [];            // (lo, hi)
    private readonly Dictionary<Guid, FriendRequestRecord> _requests = [];
    private readonly HashSet<(Guid Blocker, Guid Blocked)> _blocks = [];

    private static (Guid, Guid) Pair(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);

    public Task<FriendRequestResult> RequestAsync(Guid from, Guid to, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_blocks.Contains((from, to)) || _blocks.Contains((to, from))) return Task.FromResult(FriendRequestResult.Blocked);
            if (_friends.Contains(Pair(from, to))) return Task.FromResult(FriendRequestResult.AlreadyFriends);
            if (_requests.Values.Any(r => r.From == from && r.To == to)) return Task.FromResult(FriendRequestResult.AlreadyRequested);
            var reverse = _requests.Values.FirstOrDefault(r => r.From == to && r.To == from);
            if (reverse is not null)
            {
                _requests.Remove(reverse.Id);
                _friends.Add(Pair(from, to));
                return Task.FromResult(FriendRequestResult.AutoAccepted);
            }
            var id = Guid.NewGuid();
            _requests[id] = new FriendRequestRecord(id, from, to, at);
            return Task.FromResult(FriendRequestResult.Created);
        }
    }

    /// <summary>Adds a pending request with a fixed id (fixtures).</summary>
    internal void SeedRequest(FriendRequestRecord r) { lock (_gate) _requests[r.Id] = r; }

    public Task<bool> RespondAsync(Guid requestId, Guid responder, bool accept, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(requestId, out var r) || r.To != responder) return Task.FromResult(false);
            _requests.Remove(requestId);
            if (accept) _friends.Add(Pair(r.From, r.To));
            return Task.FromResult(true);
        }
    }

    public Task RemoveAsync(Guid a, Guid b, CancellationToken ct)
    {
        lock (_gate) _friends.Remove(Pair(a, b));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> ListFriendsAsync(Guid userId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Guid>>(_friends.Where(p => p.Item1 == userId || p.Item2 == userId)
                .Select(p => p.Item1 == userId ? p.Item2 : p.Item1).ToList());
    }

    public Task<IReadOnlyList<FriendRequestRecord>> ListRequestsAsync(Guid userId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<FriendRequestRecord>>(_requests.Values.Where(r => r.From == userId || r.To == userId)
                .OrderBy(r => r.CreatedAt).ToList());
    }

    public Task<bool> AreFriendsAsync(Guid a, Guid b, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_friends.Contains(Pair(a, b)));
    }

    public Task BlockAsync(Guid blocker, Guid blocked, CancellationToken ct)
    {
        lock (_gate)
        {
            _blocks.Add((blocker, blocked));
            _friends.Remove(Pair(blocker, blocked));
            foreach (var r in _requests.Values.Where(r => (r.From == blocker && r.To == blocked) || (r.From == blocked && r.To == blocker)).ToList())
                _requests.Remove(r.Id);
        }
        return Task.CompletedTask;
    }

    public Task UnblockAsync(Guid blocker, Guid blocked, CancellationToken ct)
    {
        lock (_gate) _blocks.Remove((blocker, blocked));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> ListBlockedAsync(Guid blocker, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<Guid>>(_blocks.Where(b => b.Blocker == blocker).Select(b => b.Blocked).ToList());
    }

    public Task<bool> IsBlockedEitherWayAsync(Guid a, Guid b, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_blocks.Contains((a, b)) || _blocks.Contains((b, a)));
    }
}

/// <summary>In-memory direct messages.</summary>
public sealed class InMemoryChatRepository : IChatRepository
{
    private readonly Lock _gate = new();
    private readonly List<DirectMessageRecord> _messages = [];
    private long _nextId;

    public Task<DirectMessageRecord> AppendAsync(Guid from, Guid to, string body, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var m = new DirectMessageRecord(++_nextId, from, to, body, at, null);
            _messages.Add(m);
            return Task.FromResult(m);
        }
    }

    public Task<IReadOnlyList<DirectMessageRecord>> PageAsync(Guid a, Guid b, long? beforeId, int take, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<DirectMessageRecord>>(_messages
                .Where(m => ((m.From == a && m.To == b) || (m.From == b && m.To == a)) && (beforeId is null || m.Id < beforeId))
                .OrderByDescending(m => m.Id).Take(take).ToList());
    }

    public Task MarkReadAsync(Guid reader, Guid other, long upToId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            for (var i = 0; i < _messages.Count; i++)
            {
                var m = _messages[i];
                if (m.To == reader && m.From == other && m.Id <= upToId && m.ReadAt is null) _messages[i] = m with { ReadAt = at };
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Guid, int>> UnreadCountsAsync(Guid reader, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyDictionary<Guid, int>>(_messages.Where(m => m.To == reader && m.ReadAt is null)
                .GroupBy(m => m.From).ToDictionary(g => g.Key, g => g.Count()));
    }
}

/// <summary>In-memory streaks and quest days.</summary>
public sealed class InMemoryProgressRepository : IProgressRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, StreakRecord> _streaks = [];
    private readonly Dictionary<(Guid, DateOnly), QuestDayRecord> _days = [];

    /// <summary>Guest merge: better streak wins; quest wins add up.</summary>
    internal void Reassign(Guid from, Guid to)
    {
        lock (_gate)
        {
            if (_streaks.Remove(from, out var gs) && (!_streaks.TryGetValue(to, out var acc) || gs.Current > acc.Current))
                _streaks[to] = gs with { UserId = to, Best = Math.Max(gs.Best, _streaks.GetValueOrDefault(to)?.Best ?? 0) };
            foreach (var key in _days.Keys.Where(k => k.Item1 == from).ToArray())
            {
                var d = _days[key];
                _days.Remove(key);
                _days[(to, key.Item2)] = _days.TryGetValue((to, key.Item2), out var have) ? have with { Wins = have.Wins + d.Wins } : d with { UserId = to };
            }
        }
    }

    public Task<StreakRecord> GetStreakAsync(Guid userId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_streaks.GetValueOrDefault(userId) ?? new StreakRecord(userId, 0, 0, null, 0));
    }

    public Task PutStreakAsync(StreakRecord record, CancellationToken ct)
    {
        lock (_gate) _streaks[record.UserId] = record;
        return Task.CompletedTask;
    }

    public Task<QuestDayRecord> GetQuestDayAsync(Guid userId, DateOnly day, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_days.GetValueOrDefault((userId, day)) ?? new QuestDayRecord(userId, day, 0, false, null, false));
    }

    public Task PutQuestDayAsync(QuestDayRecord record, CancellationToken ct)
    {
        lock (_gate) _days[(record.UserId, record.Day)] = record;
        return Task.CompletedTask;
    }
}

/// <summary>In-memory challenges with CAS transitions.</summary>
public sealed class InMemoryChallengeRepository : IChallengeRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ChallengeRecord> _map = new(StringComparer.Ordinal);

    public Task<bool> CreateAsync(ChallengeRecord record, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_map.TryAdd(record.Code, record));
    }

    public Task<ChallengeRecord?> GetAsync(string code, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_map.GetValueOrDefault(code));
    }

    public Task<bool> TryTransitionAsync(string code, ChallengeStatus expected, ChallengeStatus next, Guid? acceptedBy, Guid? matchId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(code, out var c) || c.Status != expected) return Task.FromResult(false);
            _map[code] = c with { Status = next, AcceptedBy = acceptedBy ?? c.AcceptedBy, MatchId = matchId ?? c.MatchId };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<ChallengeRecord>> ListOpenForTargetAsync(Guid targetUserId, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ChallengeRecord>>(_map.Values
                .Where(c => c.TargetUserId == targetUserId && c.Status == ChallengeStatus.Open && c.ExpiresAt > now)
                .OrderBy(c => c.CreatedAt).ToList());
    }
}

/// <summary>In-memory bot keys.</summary>
public sealed class InMemoryBotKeyRepository : IBotKeyRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, BotKeyRecord> _map = new(StringComparer.Ordinal);

    public Task<BotKeyRecord?> FindAsync(string keyId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_map.GetValueOrDefault(keyId));
    }

    public Task CreateAsync(BotKeyRecord record, CancellationToken ct)
    {
        lock (_gate) _map[record.KeyId] = record;
        return Task.CompletedTask;
    }

    public Task<bool> RevokeAsync(string keyId, Guid ownerUserId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(keyId, out var k) || k.OwnerUserId != ownerUserId || k.RevokedAt is not null) return Task.FromResult(false);
            _map[keyId] = k with { RevokedAt = at };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<BotKeyRecord>> ListForOwnerAsync(Guid ownerUserId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<BotKeyRecord>>(_map.Values.Where(k => k.OwnerUserId == ownerUserId).OrderBy(k => k.CreatedAt).ToList());
    }
}

/// <summary>In-memory reward ledger (nonce-unique).</summary>
public sealed class InMemoryRewardLedger : IRewardLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RewardGrantRecord> _map = new(StringComparer.Ordinal);

    public Task<bool> TryGrantAsync(RewardGrantRecord grant, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_map.TryAdd(grant.Nonce, grant));
    }

    public Task<bool> HasGrantAsync(Guid userId, RewardKind kind, Guid? gameId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_map.Values.Any(g => g.UserId == userId && g.Kind == kind && g.GameId == gameId));
    }

    public Task<int> CountSinceAsync(Guid userId, RewardKind kind, DateTimeOffset since, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_map.Values.Count(g => g.UserId == userId && g.Kind == kind && g.GrantedAt >= since));
    }
}
