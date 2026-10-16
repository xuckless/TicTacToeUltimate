using System.Collections.Concurrent;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;
using Microsoft.Extensions.Options;

namespace TicTacToeUltimate.Server.Presence;

/// <summary>userId -> connections; Online immediately, Offline after a 3 s debounce; InGame from live activity.</summary>
public sealed class PresenceTracker : IPresenceTracker
{
    /// <summary>Offline debounce.</summary>
    public static readonly TimeSpan OfflineDebounce = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _connections = new();
    private readonly ConcurrentDictionary<Guid, string> _regions = new();
    private readonly ConcurrentDictionary<Guid, PresenceDto> _lastSent = new();
    private readonly ConcurrentDictionary<Guid, ITimer> _pendingOffline = new();
    private readonly TimeProvider _time;
    private readonly IPresenceSink _sink;
    private readonly UserActivityRegistry _activity;
    private readonly ILogger<PresenceTracker> _log;

    /// <summary>Raised (after debounce) when a user has no connections left.</summary>
    public event Action<Guid>? WentOffline;

    /// <summary>Creates the tracker.</summary>
    public PresenceTracker(TimeProvider time, IPresenceSink sink, UserActivityRegistry activity, ILogger<PresenceTracker> log)
    {
        _time = time;
        _sink = sink;
        _activity = activity;
        _log = log;
        activity.Changed += u => { if (IsOnline(u)) Publish(u); };
    }

    /// <inheritdoc />
    public PresenceDto Get(Guid userId)
    {
        if (!IsOnline(userId)) return new PresenceDto(userId, PresenceStatus.Offline, null);
        return _activity.LiveMatch(userId) is { } m
            ? new PresenceDto(userId, PresenceStatus.InGame, m)
            : new PresenceDto(userId, PresenceStatus.Online, null);
    }

    /// <inheritdoc />
    public bool IsOnline(Guid userId) => _connections.TryGetValue(userId, out var c) && !c.IsEmpty;

    /// <summary>Users with at least one connection.</summary>
    public IReadOnlyList<Guid> OnlineUsers() => [.. _connections.Where(kv => !kv.Value.IsEmpty).Select(kv => kv.Key)];

    /// <summary>Region hint (IANA prefix such as "Europe") from ClientHello.</summary>
    public string? Region(Guid userId) => _regions.TryGetValue(userId, out var r) ? r : null;

    /// <summary>Stores the client's time zone region.</summary>
    public void SetTimeZone(Guid userId, string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone)) return;
        var slash = timeZone.IndexOf('/', StringComparison.Ordinal);
        _regions[userId] = slash > 0 ? timeZone[..slash] : timeZone;
    }

    /// <summary>Registers a connection. Returns true if the user was offline (first connection).</summary>
    public bool Connect(Guid userId, string connectionId)
    {
        if (_pendingOffline.TryRemove(userId, out var t)) t.Dispose();
        var set = _connections.GetOrAdd(userId, _ => new());
        var first = set.IsEmpty;
        set.TryAdd(connectionId, 0);
        Publish(userId);
        return first;
    }

    /// <summary>Unregisters a connection; when none remain, schedules the debounced Offline.</summary>
    public void Disconnect(Guid userId, string connectionId)
    {
        if (!_connections.TryGetValue(userId, out var set)) return;
        set.TryRemove(connectionId, out _);
        if (!set.IsEmpty) return;
        var timer = _time.CreateTimer(_ => FireOffline(userId), null, OfflineDebounce, Timeout.InfiniteTimeSpan);
        if (_pendingOffline.TryGetValue(userId, out var old)) old.Dispose();
        _pendingOffline[userId] = timer;
    }

    private void FireOffline(Guid userId)
    {
        if (_pendingOffline.TryRemove(userId, out var t)) t.Dispose();
        if (IsOnline(userId)) return;
        Publish(userId);
        WentOffline?.Invoke(userId);
    }

    private void Publish(Guid userId)
    {
        var p = Get(userId);
        if (_lastSent.TryGetValue(userId, out var last) && last == p) return;
        _lastSent[userId] = p;
        _ = SendAsync(p);
    }

    private async Task SendAsync(PresenceDto p)
    {
        try { await _sink.OnPresenceChangedAsync(p, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "Presence sink failed for {UserId}", p.UserId); }
    }
}

/// <summary>Per-user RTT samples (server timestamps). halfRtt = min(last N)/2 clamped to [0, 1000].</summary>
public sealed class RttTracker(TimeProvider time, IOptions<TttOptions> options)
{
    private sealed class State
    {
        public readonly Lock Gate = new();
        public readonly Queue<int> Samples = new();
        public readonly Dictionary<int, long> Pending = [];
        public int NextId;
        public long LastPingTs;
        public int? LastRtt;
    }

    private readonly ConcurrentDictionary<Guid, State> _users = new();

    /// <summary>Allocates a ping id if one is due (interval elapsed); otherwise null.</summary>
    public int? TryStartPing(Guid userId, TimeSpan interval)
    {
        var s = _users.GetOrAdd(userId, _ => new State());
        var now = time.GetTimestamp();
        lock (s.Gate)
        {
            if (s.LastPingTs != 0 && time.GetElapsedTime(s.LastPingTs, now) < interval) return null;
            s.LastPingTs = now;
            var id = ++s.NextId;
            s.Pending[id] = now;
            if (s.Pending.Count > 16) s.Pending.Remove(s.Pending.Keys.Min());
            return id;
        }
    }

    /// <summary>Records a pong; returns (rtt, halfRtt) or null for unknown ids.</summary>
    public (int RttMs, int HalfRttMs)? OnPong(Guid userId, int pingId)
    {
        if (!_users.TryGetValue(userId, out var s)) return null;
        var now = time.GetTimestamp();
        lock (s.Gate)
        {
            if (!s.Pending.Remove(pingId, out var sent)) return null;
            var rtt = (int)Math.Clamp(time.GetElapsedTime(sent, now).TotalMilliseconds, 0, 60_000);
            s.Samples.Enqueue(rtt);
            while (s.Samples.Count > Math.Max(1, options.Value.Clock.RttSamples)) s.Samples.Dequeue();
            s.LastRtt = rtt;
            return (rtt, Math.Clamp(s.Samples.Min() / 2, 0, 1000));
        }
    }

    /// <summary>Current halfRtt estimate (min of the kept samples / 2), or 0 when nothing is known yet.</summary>
    public int HalfRtt(Guid userId)
    {
        if (!_users.TryGetValue(userId, out var s)) return 0;
        lock (s.Gate) return s.Samples.Count == 0 ? 0 : Math.Clamp(s.Samples.Min() / 2, 0, 1000);
    }

    /// <summary>Forgets a user.</summary>
    public void Forget(Guid userId) => _users.TryRemove(userId, out _);
}

/// <summary>Pings users every 2 s while in a live match, every 15 s otherwise.</summary>
public sealed class RttPinger(PresenceTracker presence, RttTracker rtt, MatchGateway gateway, IUserNotifier notifier,
    TimeProvider time, IOptions<TttOptions> options, ILogger<RttPinger> log) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await PingDueAsync().ConfigureAwait(false); }
                catch (Exception ex) { log.LogWarning(ex, "Ping round failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One ping round.</summary>
    internal async Task PingDueAsync()
    {
        var o = options.Value.Clock;
        foreach (var user in presence.OnlineUsers())
        {
            var live = gateway.ActiveMatchesFor(user).Count > 0;
            var interval = TimeSpan.FromSeconds(live ? o.PingLiveSeconds : o.PingIdleSeconds);
            if (rtt.TryStartPing(user, interval) is { } id)
                await notifier.ToUserAsync(user, c => c.Ping(id)).ConfigureAwait(false);
        }
    }
}
