using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Persistence.InMemory;

/// <summary>Thread-safe in-memory users. Usernames are unique case-insensitively.</summary>
public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, UserRecord> _byId = [];
    private readonly Dictionary<string, Guid> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _byDevice = new(StringComparer.Ordinal);

    public Task<UserRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_byId.GetValueOrDefault(id));
    }

    public Task<UserRecord?> GetByUsernameAsync(string username, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_byName.TryGetValue(username.ToLowerInvariant(), out var id) ? _byId[id] : null);
    }

    public Task<UserRecord?> GetByDeviceIdAsync(string deviceId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_byDevice.TryGetValue(deviceId, out var id) ? _byId[id] : null);
    }

    public Task<IReadOnlyDictionary<Guid, UserRecord>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        lock (_gate)
        {
            var map = new Dictionary<Guid, UserRecord>();
            foreach (var id in ids) if (_byId.TryGetValue(id, out var u)) map[id] = u;
            return Task.FromResult<IReadOnlyDictionary<Guid, UserRecord>>(map);
        }
    }

    public Task<bool> CreateAsync(UserRecord user, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_byId.ContainsKey(user.Id)) return Task.FromResult(false);
            var key = user.Username?.ToLowerInvariant();
            if (key is not null && _byName.ContainsKey(key)) return Task.FromResult(false);
            if (user.DeviceId is not null && _byDevice.ContainsKey(user.DeviceId)) return Task.FromResult(false);
            _byId[user.Id] = user with { Username = key };
            if (key is not null) _byName[key] = user.Id;
            if (user.DeviceId is not null) _byDevice[user.DeviceId] = user.Id;
            return Task.FromResult(true);
        }
    }

    public Task<UsernameResult> SetUsernameAsync(Guid id, string username, DateTimeOffset? tosAcceptedAt, CancellationToken ct)
    {
        var key = username.ToLowerInvariant();
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var u)) return Task.FromResult(UsernameResult.Invalid);
            if (u.Username == key) return Task.FromResult(UsernameResult.Ok);
            if (_byName.ContainsKey(key)) return Task.FromResult(UsernameResult.Taken);
            var first = u.Username is null;
            if (!first && u.RenamesLeft <= 0) return Task.FromResult(UsernameResult.NoRenamesLeft);
            if (u.Username is not null) _byName.Remove(u.Username);
            _byName[key] = id;
            _byId[id] = u with
            {
                Username = key, DisplayName = key,
                Kind = u.Kind == AccountKind.Guest ? AccountKind.Account : u.Kind,
                RenamesLeft = first ? u.RenamesLeft : u.RenamesLeft - 1,
                TosAcceptedAt = u.TosAcceptedAt ?? tosAcceptedAt,
            };
            return Task.FromResult(UsernameResult.Ok);
        }
    }

    public Task UpdateProfileAsync(Guid id, ProfilePatch patch, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var u))
                _byId[id] = u with
                {
                    AvatarId = patch.AvatarId ?? u.AvatarId, Country = patch.Country ?? u.Country, TimeZone = patch.TimeZone ?? u.TimeZone,
                };
        }
        return Task.CompletedTask;
    }

    public Task TouchAsync(Guid id, DateTimeOffset lastSeenAt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var u)) _byId[id] = u with { LastSeenAt = lastSeenAt };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UserRecord>> SearchAsync(string prefix, int take, CancellationToken ct)
    {
        var p = prefix.ToLowerInvariant();
        lock (_gate)
            return Task.FromResult<IReadOnlyList<UserRecord>>(_byName.Where(kv => kv.Key.StartsWith(p, StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).Take(take).Select(kv => _byId[kv.Value]).ToList());
    }

    public Task<IReadOnlyList<UserRecord>> ListAsync(int take, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<UserRecord>>(_byId.Values.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).Take(take).ToList());
    }
}

/// <summary>In-memory ratings and leaderboards.</summary>
public sealed class InMemoryRatingRepository(IUserRepository users) : IRatingRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid, RatingPool), RatingRecord> _map = [];

    public Task<RatingRecord> GetAsync(Guid userId, RatingPool pool, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_map.GetValueOrDefault((userId, pool)) ?? RatingRecord.Initial(userId, pool));
    }

    public Task<IReadOnlyList<RatingRecord>> GetAllAsync(Guid userId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<RatingRecord>>(_map.Values.Where(r => r.UserId == userId).OrderBy(r => r.Pool).ToList());
    }

    public Task PutAsync(RatingRecord record, CancellationToken ct)
    {
        lock (_gate) _map[(record.UserId, record.Pool)] = record;
        return Task.CompletedTask;
    }

    public async Task<LeaderboardPage> QueryAsync(LeaderboardQuery q, CancellationToken ct)
    {
        List<RatingRecord> pool;
        lock (_gate) pool = _map.Values.Where(r => r.Pool == q.Pool).ToList();
        IEnumerable<RatingRecord> rows = pool.Where(r => r.Games >= q.MinGames);
        if (q.MinRating is { } min) rows = rows.Where(r => r.Rating >= min);
        if (q.RestrictTo is { } only) rows = rows.Where(r => only.Contains(r.UserId));
        var list = rows.ToList();
        if (q.Country is not null)
        {
            var u = await users.GetManyAsync(list.Select(r => r.UserId).ToList(), ct);
            list = list.Where(r => u.TryGetValue(r.UserId, out var x) && string.Equals(x.Country, q.Country, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        var ranked = list.OrderByDescending(r => r.Rating).ThenBy(r => r.UserId)
            .Select((r, i) => new LeaderboardEntry(i + 1, r.UserId, r.Rating, r.Games)).ToList();
        var page = Math.Max(1, q.Page);
        var rowsPage = ranked.Skip((page - 1) * q.PageSize).Take(q.PageSize).ToList();
        return new LeaderboardPage(rowsPage, ranked.Count, ranked.FirstOrDefault(e => e.UserId == q.Viewer));
    }
}

/// <summary>In-memory finished games.</summary>
public sealed class InMemoryGameRepository : IGameRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, GameRecord> _games = [];

    public Task<bool> SaveCompletedAsync(GameRecord game, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_games.TryAdd(game.Id, game));
    }

    public Task<GameRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_games.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<GameListItem>> ListForUserAsync(Guid userId, DateTimeOffset? before, int take, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<GameListItem>>(_games.Values
                .Where(g => (g.Setup.SeatA.UserId == userId || g.Setup.SeatB.UserId == userId) && (before is null || g.EndedAt < before))
                .OrderByDescending(g => g.EndedAt).ThenByDescending(g => g.Id).Take(take)
                .Select(g => new GameListItem(g.Id, g.Setup, g.Swapped, g.Result, g.Actions.Count, g.EndedAt, g.Ratings)).ToList());
    }

    public Task<int> CountForUserAsync(Guid userId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_games.Values.Count(g => g.Setup.SeatA.UserId == userId || g.Setup.SeatB.UserId == userId));
    }
}

/// <summary>In-memory durable Daily state.</summary>
public sealed class InMemoryDailyMatchRepository : IDailyMatchRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, StoredDailyMatch> _map = [];

    public Task CreateAsync(MatchSetup setup, DateTimeOffset startedAt, DateTimeOffset firstDeadline, CancellationToken ct)
    {
        lock (_gate) _map.TryAdd(setup.MatchId, new StoredDailyMatch(setup, [], 0, firstDeadline, false, startedAt));
        return Task.CompletedTask;
    }

    public Task<long> AppendActionAsync(Guid matchId, long expectedVersion, StoredAction action, DateTimeOffset? nextDeadline, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(matchId, out var m) || m.Version != expectedVersion) throw new ConcurrencyException(matchId);
            var next = m with { Actions = [.. m.Actions, action], Version = m.Version + 1, TurnDeadline = nextDeadline };
            _map[matchId] = next;
            return Task.FromResult(next.Version);
        }
    }

    public Task MarkEndedAsync(Guid matchId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(matchId, out var m)) _map[matchId] = m with { Ended = true, TurnDeadline = null };
        }
        return Task.CompletedTask;
    }

    public Task<StoredDailyMatch?> LoadAsync(Guid matchId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_map.GetValueOrDefault(matchId));
    }

    public Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Guid>>(_map.Values.Where(m => !m.Ended && m.TurnDeadline <= now).Select(m => m.Setup.MatchId).ToList());
    }

    public Task<IReadOnlyList<StoredDailyMatch>> ListActiveForUserAsync(Guid userId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<StoredDailyMatch>>(_map.Values
                .Where(m => !m.Ended && (m.Setup.SeatA.UserId == userId || m.Setup.SeatB.UserId == userId)).ToList());
    }
}

/// <summary>In-memory seasons.</summary>
public sealed class InMemorySeasonRepository(IOptions<TttOptions> options) : ISeasonRepository
{
    private readonly Lock _gate = new();
    private SeasonRecord? _current;
    private readonly List<MasterSeasonResult> _results = [];

    /// <summary>Recorded results of closed seasons.</summary>
    public IReadOnlyList<MasterSeasonResult> Results { get { lock (_gate) return _results.ToList(); } }

    public Task<SeasonRecord> GetCurrentAsync(DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_current ??= new SeasonRecord(1, now, now.AddDays(Math.Max(1, options.Value.Seasons.LengthDays))));
    }

    public Task CloseAsync(SeasonRecord season, IReadOnlyList<MasterSeasonResult> results, SeasonRecord next, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_current?.Id != season.Id) return Task.CompletedTask;
            _results.AddRange(results);
            _current = next;
        }
        return Task.CompletedTask;
    }
}
