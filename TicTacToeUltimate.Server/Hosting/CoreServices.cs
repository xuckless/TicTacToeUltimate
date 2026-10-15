using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Analysis;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Realtime;

namespace TicTacToeUltimate.Server.Hosting;

/// <summary>SignalR user id = JWT "sub".</summary>
public sealed class SubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
        => connection.User?.FindFirst(TttClaims.Sub)?.Value ?? connection.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}

/// <summary>Delivers to every connection of a user via SignalR user groups.</summary>
public sealed class UserNotifier(IHubContext<GameHub, IGameClient> hub) : IUserNotifier
{
    public Task ToUserAsync(Guid userId, Func<IGameClient, Task> send)
        => send(hub.Clients.User(userId.ToString()));

    public Task ToUsersAsync(IEnumerable<Guid> userIds, Func<IGameClient, Task> send)
    {
        var ids = userIds.Select(u => u.ToString()).Distinct().ToArray();
        return ids.Length == 0 ? Task.CompletedTask : send(hub.Clients.Users(ids));
    }
}

/// <summary>Stub push: logs only.</summary>
public sealed class LoggingPushSender(ILogger<LoggingPushSender> logger) : IPushSender
{
    public Task SendAsync(Guid userId, NotificationDto notification, CancellationToken ct)
    {
        logger.LogInformation("Push to {UserId}: {Kind} {Title}", userId, notification.Kind, notification.Title);
        return Task.CompletedTask;
    }
}

/// <summary>Fallback until WP3: every card is "Player".</summary>
public sealed class DefaultPlayerCardService : IPlayerCardService
{
    private static PlayerCardDto Card(Guid id) => new(id, "Player", "default", null, AccountKind.Account, null, null, false);

    public Task<PlayerCardDto> GetCardAsync(Guid userId, RatingPool pool, CancellationToken ct) => Task.FromResult(Card(userId));

    public Task<IReadOnlyDictionary<Guid, PlayerCardDto>> GetCardsAsync(IReadOnlyCollection<Guid> userIds, RatingPool pool, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<Guid, PlayerCardDto>>(userIds.Distinct().ToDictionary(id => id, Card));
}

public sealed class NullMatchResultSink : IMatchResultSink
{
    public ValueTask EnqueueAsync(CompletedMatch match, CancellationToken ct) => ValueTask.CompletedTask;
}

public sealed class NullPresenceSink : IPresenceSink
{
    public ValueTask OnPresenceChangedAsync(PresenceDto presence, CancellationToken ct) => ValueTask.CompletedTask;
}

public sealed class NullBotPool : IBotPool
{
    public bool TryReserve(TimeControl timeControl, int rating, [NotNullWhen(true)] out BotReservation? reservation)
    {
        reservation = null;
        return false;
    }
    public void Release(Guid botUserId) { }
    public ValueTask NotifyMatchCreatedAsync(Guid botUserId, Guid matchId, CancellationToken ct) => ValueTask.CompletedTask;
}

public sealed class NullTablebaseCache : ITablebaseCache
{
    public TablebaseStatus GetStatus(RuleSet rules) => TablebaseStatus.Unavailable;
    public bool TryGet(RuleSet rules, [NotNullWhen(true)] out Tablebase? tablebase)
    {
        tablebase = null;
        return false;
    }
}

public sealed class NullAnalysisService : IAnalysisService
{
    public Task<GameAnalysisDto?> AnalyzeAsync(GameRecord game, CancellationToken ct) => Task.FromResult<GameAnalysisDto?>(null);
}
