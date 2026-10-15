using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Services;

/// <summary>The hub surface the stores use (seam for tests). <see cref="RealtimeClient"/> is the only real implementation.</summary>
public interface IRealtimeTransport : IGameHub
{
    /// <summary>Connection phase.</summary>
    ConnectionPhase Phase { get; }
    /// <summary>Reconnect attempt counter (0 when online).</summary>
    int ReconnectAttempt { get; }
    /// <summary>Raised when <see cref="Phase"/> or <see cref="ReconnectAttempt"/> changes.</summary>
    event Action? PhaseChanged;
    /// <summary>Raised after a (re)connection and the <see cref="Handshake"/> completed.</summary>
    event Action<bool>? Connected;   // arg: true when this was a reconnect

    /// <summary>Runs after every connect before <see cref="Connected"/> (SessionStore sends Hello here).</summary>
    Func<Task>? Handshake { get; set; }

    /// <summary>Connects (and keeps reconnecting) until <see cref="StopAsync"/>.</summary>
    Task StartAsync();
    /// <summary>Disconnects and stops reconnecting.</summary>
    Task StopAsync();

    /// <summary>Server pushes.</summary>
    event Action<QueueStatusDto>? QueueStatusReceived;
    /// <summary>Server pushes.</summary>
    event Action<MatchFoundDto>? MatchFoundReceived;
    /// <summary>Server pushes.</summary>
    event Action<MatchEventEnvelope>? MatchEventReceived;
    /// <summary>Server pushes.</summary>
    event Action<PostGameSummaryDto>? PostGameSummaryReceived;
    /// <summary>Server pushes.</summary>
    event Action<ChallengeDto>? ChallengeReceived;
    /// <summary>Server pushes.</summary>
    event Action<ChallengeDto>? ChallengeUpdated;
    /// <summary>Server pushes.</summary>
    event Action<PresenceDto>? PresenceChanged;
    /// <summary>Server pushes.</summary>
    event Action<FriendRequestDto>? FriendRequestReceived;
    /// <summary>Server pushes.</summary>
    event Action? FriendsChanged;
    /// <summary>Server pushes.</summary>
    event Action<DirectMessageDto>? DirectMessageReceived;
    /// <summary>Server pushes.</summary>
    event Action<NotificationDto>? NotificationReceived;
}
