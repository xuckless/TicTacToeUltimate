using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Arena;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Realtime;

namespace TicTacToeUltimate.Server.Matches;

/// <summary>Delivers match events and group membership. Production: SignalR groups "m:{matchId}".</summary>
public interface IMatchBroadcaster
{
    /// <summary>Adds a connection to the match group.</summary>
    Task AddToGroupAsync(string connectionId, Guid matchId, CancellationToken ct);
    /// <summary>Removes a connection from the match group.</summary>
    Task RemoveFromGroupAsync(string connectionId, Guid matchId, CancellationToken ct);
    /// <summary>Sends an envelope to the match group.</summary>
    Task SendAsync(MatchEventEnvelope envelope, CancellationToken ct);
}

/// <summary>SignalR implementation of <see cref="IMatchBroadcaster"/>.</summary>
public sealed class HubMatchBroadcaster(IHubContext<GameHub, IGameClient> hub) : IMatchBroadcaster
{
    /// <summary>Group name for a match.</summary>
    public static string Group(Guid matchId) => "m:" + matchId.ToString("N");
    /// <inheritdoc />
    public Task AddToGroupAsync(string connectionId, Guid matchId, CancellationToken ct) => hub.Groups.AddToGroupAsync(connectionId, Group(matchId), ct);
    /// <inheritdoc />
    public Task RemoveFromGroupAsync(string connectionId, Guid matchId, CancellationToken ct) => hub.Groups.RemoveFromGroupAsync(connectionId, Group(matchId), ct);
    /// <inheritdoc />
    public Task SendAsync(MatchEventEnvelope envelope, CancellationToken ct) => hub.Clients.Group(Group(envelope.MatchId)).MatchEvent(envelope);
}

/// <summary>What a user is doing right now (live, non-Daily).</summary>
public abstract record UserActivity;
/// <summary>Waiting in a queue.</summary>
public sealed record QueuedActivity(TimeControl TimeControl) : UserActivity;
/// <summary>Playing a live match.</summary>
public sealed record InMatchActivity(Guid MatchId) : UserActivity;

/// <summary>At most one queue entry or live non-Daily match per user; Daily queue tracked separately.</summary>
public sealed class UserActivityRegistry
{
    private readonly ConcurrentDictionary<Guid, UserActivity> _live = new();
    private readonly ConcurrentDictionary<Guid, byte> _dailyQueue = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, byte>> _daily = new();

    /// <summary>Raised when a user's live activity changes (presence InGame).</summary>
    public event Action<Guid>? Changed;

    /// <summary>Returns null on success or an error code.</summary>
    public string? TryEnterQueue(Guid userId, TimeControl tc)
    {
        if (tc == TimeControl.Daily) return _dailyQueue.TryAdd(userId, 0) ? null : ErrorCodes.AlreadyInQueue;
        if (_live.TryAdd(userId, new QueuedActivity(tc))) { Changed?.Invoke(userId); return null; }
        return _live.TryGetValue(userId, out var a) && a is InMatchActivity ? ErrorCodes.AlreadyInMatch : ErrorCodes.AlreadyInQueue;
    }

    /// <summary>Removes a queue entry (live or daily).</summary>
    public void LeaveQueue(Guid userId, TimeControl tc)
    {
        if (tc == TimeControl.Daily) { _dailyQueue.TryRemove(userId, out _); return; }
        if (_live.TryRemove(KeyValuePair.Create(userId, (UserActivity)new QueuedActivity(tc)))) Changed?.Invoke(userId);
    }

    /// <summary>Queue entry becomes a match (overwrites).</summary>
    public void SetMatch(Guid userId, Guid matchId)
    {
        _live[userId] = new InMatchActivity(matchId);
        Changed?.Invoke(userId);
    }

    /// <summary>Starts a match only if the user is idle.</summary>
    public bool TryStartMatch(Guid userId, Guid matchId)
    {
        if (!_live.TryAdd(userId, new InMatchActivity(matchId))) return false;
        Changed?.Invoke(userId);
        return true;
    }

    /// <summary>Releases the match activity if it is still this match.</summary>
    public void EndMatch(Guid userId, Guid matchId)
    {
        if (_live.TryRemove(KeyValuePair.Create(userId, (UserActivity)new InMatchActivity(matchId)))) Changed?.Invoke(userId);
    }

    /// <summary>Current live match, if any.</summary>
    public Guid? LiveMatch(Guid userId) => _live.TryGetValue(userId, out var a) && a is InMatchActivity m ? m.MatchId : null;

    /// <summary>Current activity.</summary>
    public UserActivity? Get(Guid userId) => _live.TryGetValue(userId, out var a) ? a : null;

    /// <summary>Tracks an active daily match (in-memory count; repository count wins when available).</summary>
    public void AddDaily(Guid userId, Guid matchId) => _daily.GetOrAdd(userId, _ => new()).TryAdd(matchId, 0);
    /// <summary>Untracks a daily match.</summary>
    public void RemoveDaily(Guid userId, Guid matchId) { if (_daily.TryGetValue(userId, out var s)) s.TryRemove(matchId, out _); }
    /// <summary>In-memory daily count.</summary>
    public int DailyCount(Guid userId) => _daily.TryGetValue(userId, out var s) ? s.Count : 0;
}

/// <summary>Shared dependencies of every match actor.</summary>
public sealed class MatchRuntime(
    TimeProvider time, IGameRules rules, IClockSchedule clock, TttOptions options, IMatchBroadcaster broadcaster,
    IMatchResultSink sink, IUserNotifier notifier, IPushSender push, IPlayerCardService cards,
    UserActivityRegistry activity, ILoggerFactory loggers, IDailyMatchRepository? dailyRepo, Func<Guid, int>? halfRtt = null,
    IUserRepository? users = null)
{
    /// <summary>Players (for their language in notifications); null in minimal hosts.</summary>
    public IUserRepository? Users { get; } = users;
    /// <summary>Known halfRtt per user for seeding a new match's lag credit (null = none).</summary>
    public Func<Guid, int>? HalfRtt { get; } = halfRtt;
    /// <summary>Clock.</summary>
    public TimeProvider Time { get; } = time;
    /// <summary>Rules seam.</summary>
    public IGameRules Rules { get; } = rules;
    /// <summary>Clock schedule seam.</summary>
    public IClockSchedule Clock { get; } = clock;
    /// <summary>Options.</summary>
    public TttOptions Options { get; } = options;
    /// <summary>Event delivery.</summary>
    public IMatchBroadcaster Broadcaster { get; } = broadcaster;
    /// <summary>Result sink.</summary>
    public IMatchResultSink Sink { get; } = sink;
    /// <summary>User notifier.</summary>
    public IUserNotifier Notifier { get; } = notifier;
    /// <summary>Push.</summary>
    public IPushSender Push { get; } = push;
    /// <summary>Cards.</summary>
    public IPlayerCardService Cards { get; } = cards;
    /// <summary>Activity registry.</summary>
    public UserActivityRegistry Activity { get; } = activity;
    /// <summary>Loggers.</summary>
    public ILoggerFactory Loggers { get; } = loggers;
    /// <summary>Daily persistence; null when not registered.</summary>
    public IDailyMatchRepository? DailyRepo { get; } = dailyRepo;

    /// <summary>Server wall clock in epoch ms.</summary>
    public long NowMs => Time.GetUtcNow().ToUnixTimeMilliseconds();
}

/// <summary>Mode/time-control helpers.</summary>
public static class MatchModes
{
    /// <summary>Mode for a queue time control.</summary>
    public static MatchMode ModeFor(TimeControl tc) => tc switch
    {
        TimeControl.Ranked => MatchMode.Ranked,
        TimeControl.CasualX2 or TimeControl.CasualX4 or TimeControl.Casual or TimeControl.OldSchool => MatchMode.Casual,
        TimeControl.Unlimited => MatchMode.Unlimited,
        TimeControl.ArenaCasual => MatchMode.Arena,
        TimeControl.ArenaRanked => MatchMode.ArenaRanked,
        _ => MatchMode.Daily,
    };

    /// <summary>Configured mode options (falls back to spec defaults).</summary>
    public static ModeOptions Options(TttOptions o, TimeControl tc)
    {
        if (o.Modes.TryGetValue(tc.ToString(), out var m)) return m;
        var pie = tc is TimeControl.Ranked or TimeControl.Daily;
        if (MoveClock.IsArena(tc))
            return new ModeOptions { Rules = ArenaRules.RulesetC.Board.Id, Rated = tc == TimeControl.ArenaRanked, GuestsAllowed = tc == TimeControl.ArenaCasual, Arena = ArenaRules.RulesetC.Id };
        return new ModeOptions { Rules = pie ? RuleSet.RankedDefault.Id : RuleSet.Default.Id, Rated = pie, GuestsAllowed = !pie };
    }

    /// <summary>Arena (health) scoring for a time control; null for classic modes. Board rules come from <see cref="RulesFor"/>.</summary>
    public static ArenaRules? ArenaFor(TttOptions o, TimeControl tc)
    {
        if (!MoveClock.IsArena(tc)) return null;
        var named = ArenaRules.TryParse(Options(o, tc).Arena, out var a) ? a : ArenaRules.RulesetC;
        var board = RulesFor(o, tc);
        board = board with { Opening = OpeningRule.None };
        return named with { Board = board };
    }

    /// <summary>True for ranked-like queues (no guests, no bots unless enabled, no rematch).</summary>
    public static bool IsRankedLike(TimeControl tc) => tc is TimeControl.Ranked or TimeControl.Daily or TimeControl.ArenaRanked;

    /// <summary>Ruleset for a queue time control.</summary>
    public static RuleSet RulesFor(TttOptions o, TimeControl tc)
        => RuleIds.Parse(Options(o, tc).Rules, tc is TimeControl.Ranked or TimeControl.Daily ? RuleSet.RankedDefault
            : MoveClock.IsArena(tc) ? ArenaRules.RulesetC.Board : tc == TimeControl.OldSchool ? RuleSet.OldSchool : RuleSet.Default);

    /// <summary>True for casual-like controls where guests may fall back into the account pool.</summary>
    public static bool IsCasualLike(TimeControl tc) => tc is TimeControl.CasualX2 or TimeControl.CasualX4 or TimeControl.Unlimited or TimeControl.ArenaCasual
        or TimeControl.Casual or TimeControl.OldSchool;

    /// <summary>8 random bytes.</summary>
    public static ulong NewSeed() => BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));

    /// <summary>Card fallback from a seat.</summary>
    public static PlayerCardDto FallbackCard(SeatSetup s) => new(s.UserId, s.DisplayName, "default", null, s.Kind, null, null, false);
}
