using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Engine.Analysis;
using System.Diagnostics.CodeAnalysis;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;

namespace TicTacToeUltimate.Server.Abstractions;

// ---------- identity ----------
public static class TttClaims { public const string Kind = "ttt_kind"; public const string Name = "name"; public const string Sub = "sub"; }
public static class TttPolicies { public const string Account = "Account"; public const string Bot = "Bot"; }
public sealed record TttUser(Guid UserId, AccountKind Kind, string DisplayName)
{
    public bool IsGuest => Kind == AccountKind.Guest;
    public bool IsBot => Kind == AccountKind.Bot;
}
public static class ClaimsPrincipalExtensions
{
    /// <summary>Reads sub/ttt_kind/name. Throws HubException/UnauthorizedAccessException if missing.</summary>
    public static TttUser ToTttUser(this System.Security.Claims.ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var sub = principal.FindFirst(TttClaims.Sub)?.Value
            ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (sub is null || !Guid.TryParse(sub, out var userId))
            throw new Microsoft.AspNetCore.SignalR.HubException(ErrorCodes.NotAParticipant);
        var kindText = principal.FindFirst(TttClaims.Kind)?.Value;
        var kind = Enum.TryParse<AccountKind>(kindText, ignoreCase: true, out var k) ? k : AccountKind.Guest;   // missing kind fails closed
        var name = principal.FindFirst(TttClaims.Name)?.Value ?? "Player";
        return new TttUser(userId, kind, name);
    }
}

// ---------- match setup / results ----------
public enum MatchOrigin { Queue, Challenge, Rematch, BotFallback }
public sealed record SeatSetup(Guid UserId, string DisplayName, AccountKind Kind);
public sealed record MatchSetup(
    Guid MatchId, MatchMode Mode, TimeControl TimeControl, RuleSet Rules, bool Rated,
    SeatSetup SeatA /* starts as X */, SeatSetup SeatB, ulong Seed, MatchOrigin Origin,
    Guid? RematchOf, string? ChallengeCode);
public sealed record StoredAction(int Ply, int Seat, GameAction Action, int ThinkMs, bool Auto, int? VanishedCell, DateTimeOffset At);
public sealed record CompletedMatch(MatchSetup Setup, bool Swapped, IReadOnlyList<StoredAction> Actions,
    MatchResultDto Result, DateTimeOffset StartedAt, DateTimeOffset EndedAt);

/// <summary>Implemented by WP3 (PostGameProcessor). Called exactly once per ended match (incl. Aborted). Must not block: enqueue and return.</summary>
public interface IMatchResultSink { ValueTask EnqueueAsync(CompletedMatch match, CancellationToken ct); }

// ---------- match gateway (WP2 implements; WP4 bots and tests consume) ----------
public interface IMatchObserver
{
    ValueTask OnEventAsync(MatchEventEnvelope envelope, CancellationToken ct);
}
public sealed record AttachResult(MatchSnapshotDto Snapshot, IAsyncDisposable Subscription);   // disposing = detach (seat shows disconnected)

public interface IMatchGateway
{
    Task<Guid> CreateMatchAsync(MatchSetup setup, CancellationToken ct);
    /// <summary>Attach a non-SignalR participant (bot, test). Throws MatchAccessException(ErrorCodes.*) if not a participant.</summary>
    Task<AttachResult> AttachAsync(Guid matchId, Guid userId, IMatchObserver observer, CancellationToken ct);
    Task<MatchSnapshotDto?> GetSnapshotAsync(Guid matchId, Guid? viewerUserId, CancellationToken ct);
    Task<MoveAckDto> SubmitActionAsync(Guid matchId, Guid userId, int expectedPly, GameAction action, Guid actionId, CancellationToken ct);
    Task<CommandAckDto> ResignAsync(Guid matchId, Guid userId, CancellationToken ct);
    IReadOnlyList<Guid> ActiveMatchesFor(Guid userId);
}
public sealed class MatchAccessException(string code) : Exception(code) { public string Code { get; } = code; }

// ---------- challenges (WP2 implements; WP4 consumes) ----------
public interface IChallengeService
{
    Task<ChallengeDto> CreateAsync(TttUser creator, CreateChallengeDto request, CancellationToken ct);
    Task<ChallengeDto?> GetAsync(string code, CancellationToken ct);
    Task<AcceptChallengeResultDto> AcceptAsync(TttUser user, string code, CancellationToken ct);
    Task<CommandAckDto> DeclineAsync(TttUser user, string code, CancellationToken ct);
    Task<CommandAckDto> CancelAsync(TttUser user, string code, CancellationToken ct);
}

// ---------- presence (WP2 implements tracker; WP3 implements sink) ----------
public interface IPresenceTracker
{
    PresenceDto Get(Guid userId);
    bool IsOnline(Guid userId);
}
/// <summary>WP3 fans out to friends. Called from WP2 after debounce (3 s for offline). Must not block.</summary>
public interface IPresenceSink { ValueTask OnPresenceChangedAsync(PresenceDto presence, CancellationToken ct); }

// ---------- player cards (WP3 implements; WP2 consumes for seats/MatchFound) ----------
public interface IPlayerCardService
{
    Task<PlayerCardDto> GetCardAsync(Guid userId, RatingPool pool, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, PlayerCardDto>> GetCardsAsync(IReadOnlyCollection<Guid> userIds, RatingPool pool, CancellationToken ct);
}

// ---------- bots (WP4 implements; WP2 matchmaking consumes) ----------
public sealed record BotReservation(Guid BotUserId, string BotName);
public interface IBotPool
{
    /// <summary>Atomically reserves one game slot on a connected bot that seeks this time control.</summary>
    bool TryReserve(TimeControl timeControl, int rating, [NotNullWhen(true)] out BotReservation? reservation);
    void Release(Guid botUserId);
    /// <summary>Called by WP2 after CreateMatchAsync; the bot session then attaches via IMatchGateway.</summary>
    ValueTask NotifyMatchCreatedAsync(Guid botUserId, Guid matchId, CancellationToken ct);
}

// ---------- analysis (WP1 implements; WP3 consumes) ----------
public enum TablebaseStatus { Unavailable, Warming, Ready }
public interface ITablebaseCache
{
    TablebaseStatus GetStatus(RuleSet rules);
    bool TryGet(RuleSet rules, [NotNullWhen(true)] out Tablebase? tablebase);   // never blocks; may trigger lazy build
}
public interface IAnalysisService
{
    /// <summary>null when no tablebase for the ruleset (status says why).</summary>
    Task<GameAnalysisDto?> AnalyzeAsync(GameRecord game, CancellationToken ct);
}

// ---------- delivery (WP0 implements) ----------
public interface IUserNotifier
{
    Task ToUserAsync(Guid userId, Func<IGameClient, Task> send);
    Task ToUsersAsync(IEnumerable<Guid> userIds, Func<IGameClient, Task> send);
}
public interface IPushSender { Task SendAsync(Guid userId, NotificationDto notification, CancellationToken ct); }  // stub logs
