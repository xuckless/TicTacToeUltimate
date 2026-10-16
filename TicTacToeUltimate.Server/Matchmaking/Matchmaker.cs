using System.Threading.Channels;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;

namespace TicTacToeUltimate.Server.Matchmaking;

/// <summary>A queued player.</summary>
public sealed class QueueEntry(Guid userId, string displayName, AccountKind kind, TimeControl tc, int rating, string? region, long enqueuedAtMs)
{
    /// <summary>User.</summary>
    public Guid UserId { get; } = userId;
    /// <summary>Display name.</summary>
    public string DisplayName { get; } = displayName;
    /// <summary>Account kind.</summary>
    public AccountKind Kind { get; } = kind;
    /// <summary>Time control.</summary>
    public TimeControl TimeControl { get; } = tc;
    /// <summary>Rating in RatingPools.For(tc).</summary>
    public int Rating { get; } = rating;
    /// <summary>IANA prefix.</summary>
    public string? Region { get; } = region;
    /// <summary>Enqueue time, epoch ms.</summary>
    public long EnqueuedAtMs { get; } = enqueuedAtMs;
    /// <summary>Set when all connections dropped.</summary>
    public long? DisconnectedAtMs { get; set; }
    /// <summary>Guest pool.</summary>
    public bool IsGuest => Kind == AccountKind.Guest;
}

/// <summary>Queue commands.</summary>
public abstract record QueueCommand;
/// <summary>Enter.</summary>
public sealed record EnterQueueCommand(QueueEntry Entry) : QueueCommand;
/// <summary>Leave.</summary>
public sealed record LeaveQueueCommand(Guid UserId) : QueueCommand;
/// <summary>All connections dropped.</summary>
public sealed record DisconnectQueueCommand(Guid UserId) : QueueCommand;
/// <summary>A connection came back.</summary>
public sealed record ReconnectQueueCommand(Guid UserId) : QueueCommand;

/// <summary>Single-loop matchmaker (§6.6). Single-threaded, so it cannot double-pair.</summary>
public sealed class Matchmaker(IMatchGateway gateway, UserActivityRegistry activity, IUserNotifier notifier, IPlayerCardService cards,
    IBotPool bots, TimeProvider time, TttOptions options, ILogger<Matchmaker> log, TicTacToeUltimate.Server.Bots.IBotDispatcher? dispatcher = null) : BackgroundService
{
    private readonly Channel<QueueCommand> _commands = Channel.CreateUnbounded<QueueCommand>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<QueueEntry> _entries = [];
    private long _lastStatusMs = long.MinValue / 2;
    private readonly HashSet<QueueEntry> _dispatched = [];

    /// <summary>Posts a command; applied on the next tick.</summary>
    public void Post(QueueCommand command) => _commands.Writer.TryWrite(command);

    /// <summary>Current entries (test/diagnostics; only call from the loop or tests).</summary>
    internal IReadOnlyList<QueueEntry> Entries => _entries;

    /// <summary>Rating window after waiting <paramref name="waitMs"/>.</summary>
    public static int Window(long waitMs, MatchmakingOptions o)
        => o.InitialWindow + o.WidenBy * (int)(Math.Max(0, waitMs) / 1000 / Math.Max(1, o.WidenEverySeconds));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(10, options.Matchmaking.TickMs)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await TickAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Matchmaker tick failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One tick: apply commands, drop stale disconnects, pair, bot fallback, statuses.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var o = options.Matchmaking;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        while (_commands.Reader.TryRead(out var cmd)) Apply(cmd, now);

        foreach (var e in _entries.Where(e => e.DisconnectedAtMs is { } d && now - d >= o.DisconnectGraceSeconds * 1000L).ToArray())
            Remove(e);

        foreach (var (a, b) in FindPairs(_entries, now, o))
        {
            _entries.Remove(a);
            _entries.Remove(b);
            await CreatePairAsync(a, b, ct).ConfigureAwait(false);
        }

        // Bots: real people get priority (pairing above). A player still waiting after DispatchAfterSeconds triggers one bot
        // dispatch; after BotFallbackSeconds the longest-waiting player is paired with a seeking bot. Nobody is left alone.
        _dispatched.RemoveWhere(e => !_entries.Contains(e));
        foreach (var e in _entries.Where(e => e.DisconnectedAtMs is null && !BotsExcluded(e.TimeControl) && !_dispatched.Contains(e)
                     && now - e.EnqueuedAtMs >= options.Bots.Service.DispatchAfterSeconds * 1000L).ToArray())
        {
            _dispatched.Add(e);
            if (dispatcher is not null) _ = dispatcher.RequestAsync(e.TimeControl, e.UserId, e.Rating, CancellationToken.None).AsTask();
        }
        foreach (var e in _entries.Where(e => now - e.EnqueuedAtMs >= o.BotFallbackSeconds * 1000L).OrderBy(e => e.EnqueuedAtMs).ToArray())
        {
            if (BotsExcluded(e.TimeControl) || e.DisconnectedAtMs is not null) continue;
            if (!bots.TryReserve(e.TimeControl, e.Rating, out var bot)) continue;
            _entries.Remove(e);
            await CreateBotMatchAsync(e, bot, ct).ConfigureAwait(false);
        }

        if (now - _lastStatusMs >= 1000)
        {
            _lastStatusMs = now;
            foreach (var e in _entries.Where(e => e.DisconnectedAtMs is null).ToArray())
            {
                var status = new QueueStatusDto(e.TimeControl, (int)Math.Min(int.MaxValue, now - e.EnqueuedAtMs), Window(now - e.EnqueuedAtMs, o), e.IsGuest);
                await SafeAsync(() => notifier.ToUserAsync(e.UserId, c => c.QueueStatus(status))).ConfigureAwait(false);
            }
        }
    }

    private bool BotsExcluded(TimeControl tc) => !options.Bots.Enabled || tc == TimeControl.Daily
        || ((tc == TimeControl.Ranked || tc == TimeControl.ArenaRanked) && !options.Bots.Ranked);

    private void Apply(QueueCommand cmd, long now)
    {
        switch (cmd)
        {
            case EnterQueueCommand c:
                if (_entries.Any(e => e.UserId == c.Entry.UserId && e.TimeControl == c.Entry.TimeControl)) return;
                _entries.Add(c.Entry);
                break;
            case LeaveQueueCommand c:
                foreach (var e in _entries.Where(e => e.UserId == c.UserId).ToArray()) Remove(e);
                break;
            case DisconnectQueueCommand c:
                foreach (var e in _entries.Where(e => e.UserId == c.UserId)) e.DisconnectedAtMs ??= now;
                break;
            case ReconnectQueueCommand c:
                foreach (var e in _entries.Where(e => e.UserId == c.UserId)) e.DisconnectedAtMs = null;
                break;
        }
    }

    private void Remove(QueueEntry e)
    {
        _entries.Remove(e);
        activity.LeaveQueue(e.UserId, e.TimeControl);
    }

    /// <summary>Pure pairing pass: oldest first; prefer same region, then smallest gap, then longest wait.</summary>
    public static IReadOnlyList<(QueueEntry A, QueueEntry B)> FindPairs(IReadOnlyList<QueueEntry> entries, long nowMs, MatchmakingOptions o)
    {
        var pairs = new List<(QueueEntry, QueueEntry)>();
        var taken = new HashSet<QueueEntry>();
        var ordered = entries.Where(e => e.DisconnectedAtMs is null).OrderBy(e => e.EnqueuedAtMs).ToList();
        foreach (var e in ordered)
        {
            if (taken.Contains(e)) continue;
            QueueEntry? best = null;
            foreach (var c in ordered)
            {
                if (ReferenceEquals(c, e) || taken.Contains(c) || !CanPair(e, c, nowMs, o)) continue;
                if (best is null || Better(e, c, best)) best = c;
            }
            if (best is null) continue;
            taken.Add(e);
            taken.Add(best);
            pairs.Add((e, best));
        }
        return pairs;
    }

    private static bool Better(QueueEntry e, QueueEntry c, QueueEntry best)
    {
        var cr = e.Region is not null && e.Region == c.Region;
        var br = e.Region is not null && e.Region == best.Region;
        if (cr != br) return cr;
        var cg = Math.Abs(e.Rating - c.Rating);
        var bg = Math.Abs(e.Rating - best.Rating);
        if (cg != bg) return cg < bg;
        return c.EnqueuedAtMs < best.EnqueuedAtMs;
    }

    /// <summary>Pair validity: same time control, pool rules (guest fallback), and |r1 − r2| &lt;= min(w1, w2).</summary>
    public static bool CanPair(QueueEntry a, QueueEntry b, long nowMs, MatchmakingOptions o)
    {
        if (a.UserId == b.UserId || a.TimeControl != b.TimeControl) return false;
        if (a.IsGuest != b.IsGuest)
        {
            var guest = a.IsGuest ? a : b;
            if (!MatchModes.IsCasualLike(a.TimeControl) || nowMs - guest.EnqueuedAtMs < o.GuestFallbackSeconds * 1000L) return false;
        }
        var w = Math.Min(Window(nowMs - a.EnqueuedAtMs, o), Window(nowMs - b.EnqueuedAtMs, o));
        return Math.Abs(a.Rating - b.Rating) <= w;
    }

    private async Task CreatePairAsync(QueueEntry x, QueueEntry y, CancellationToken ct)
    {
        var seed = MatchModes.NewSeed();
        var xFirst = (new SplitMix64(seed).Next() & 1) == 0;
        var (a, b) = xFirst ? (x, y) : (y, x);
        var tc = a.TimeControl;
        var setup = new MatchSetup(Guid.NewGuid(), MatchModes.ModeFor(tc), tc, MatchModes.RulesFor(options, tc), MatchModes.Options(options, tc).Rated,
            new SeatSetup(a.UserId, a.DisplayName, a.Kind), new SeatSetup(b.UserId, b.DisplayName, b.Kind), seed, MatchOrigin.Queue, null, null);
        await StartAsync(setup, ct).ConfigureAwait(false);
    }

    private async Task CreateBotMatchAsync(QueueEntry e, BotReservation bot, CancellationToken ct)
    {
        var seed = MatchModes.NewSeed();
        var humanFirst = (new SplitMix64(seed).Next() & 1) == 0;
        var human = new SeatSetup(e.UserId, e.DisplayName, e.Kind);
        var botSeat = new SeatSetup(bot.BotUserId, bot.BotName, AccountKind.Bot);
        var tc = e.TimeControl;
        var setup = new MatchSetup(Guid.NewGuid(), MatchModes.ModeFor(tc), tc, MatchModes.RulesFor(options, tc), MatchModes.Options(options, tc).Rated,
            humanFirst ? human : botSeat, humanFirst ? botSeat : human, seed, MatchOrigin.BotFallback, null, null);
        if (!await StartAsync(setup, ct).ConfigureAwait(false)) { bots.Release(bot.BotUserId); return; }
        await SafeAsync(() => bots.NotifyMatchCreatedAsync(bot.BotUserId, setup.MatchId, ct).AsTask()).ConfigureAwait(false);
    }

    private async Task<bool> StartAsync(MatchSetup setup, CancellationToken ct)
    {
        var daily = setup.TimeControl == TimeControl.Daily;
        foreach (var s in new[] { setup.SeatA, setup.SeatB })
        {
            if (s.Kind == AccountKind.Bot) continue;
            if (daily) activity.LeaveQueue(s.UserId, TimeControl.Daily);
            else activity.SetMatch(s.UserId, setup.MatchId);
        }
        try
        {
            await gateway.CreateMatchAsync(setup, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Create match {MatchId} failed", setup.MatchId);
            foreach (var s in new[] { setup.SeatA, setup.SeatB }) activity.EndMatch(s.UserId, setup.MatchId);
            return false;
        }
        IReadOnlyDictionary<Guid, PlayerCardDto> map;
        try { map = await cards.GetCardsAsync([setup.SeatA.UserId, setup.SeatB.UserId], RatingPools.For(setup.TimeControl), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Cards failed"); map = new Dictionary<Guid, PlayerCardDto>(); }
        for (var seat = 0; seat < 2; seat++)
        {
            var me = seat == 0 ? setup.SeatA : setup.SeatB;
            var opp = seat == 0 ? setup.SeatB : setup.SeatA;
            if (me.Kind == AccountKind.Bot) continue;
            var found = new MatchFoundDto(setup.MatchId, setup.Mode, setup.TimeControl,
                map.TryGetValue(opp.UserId, out var card) ? card : MatchModes.FallbackCard(opp), seat == 0 ? Color.X : Color.O, seat);
            await SafeAsync(() => notifier.ToUserAsync(me.UserId, c => c.MatchFound(found))).ConfigureAwait(false);
        }
        return true;
    }

    private async Task SafeAsync(Func<Task> f)
    {
        try { await f().ConfigureAwait(false); }
        catch (Exception ex) { log.LogWarning(ex, "Matchmaker notification failed"); }
    }
}
