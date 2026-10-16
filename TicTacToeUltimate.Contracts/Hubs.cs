using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Engine.Analysis;

namespace TicTacToeUltimate.Contracts;

/// <summary>Client -> server. Hub at HubRoutes.Game. Method names are the C# names (SignalR default).</summary>
public interface IGameHub
{
    // session (WP2)
    Task<ServerHelloDto> Hello(ClientHelloDto hello);
    Task Pong(int pingId);
    Task<long> ServerTime();                                   // server epoch ms; used by ClockSync
    // queue (WP2)
    Task<QueueTicketDto> EnterQueue(QueueRequestDto request);
    Task LeaveQueue();
    // match (WP2)
    Task<MatchSnapshotDto> JoinMatch(Guid matchId);            // participant, or spectator if allowed; throws HubException(code) otherwise
    Task LeaveMatch(Guid matchId);                             // detach this connection only; clocks keep running
    Task<MoveAckDto> SubmitAction(SubmitActionDto request);
    Task<CommandAckDto> Resign(Guid matchId);
    Task<CommandAckDto> SendEmote(Guid matchId, string emoteId);
    Task<CommandAckDto> OfferRematch(Guid matchId);
    // challenges (WP2)
    Task<ChallengeDto> CreateChallenge(CreateChallengeDto request);
    Task<AcceptChallengeResultDto> AcceptChallenge(string code);
    Task<CommandAckDto> DeclineChallenge(string code);
    Task<CommandAckDto> CancelChallenge(string code);
    // social (WP3)
    Task<DirectMessageDto> SendDirectMessage(SendDirectMessageDto request);
    Task MarkConversationRead(Guid friendId, long upToMessageId);
}

/// <summary>Server -> client.</summary>
public interface IGameClient
{
    Task Ping(int pingId);
    Task QueueStatus(QueueStatusDto status);
    Task MatchFound(MatchFoundDto found);
    Task MatchEvent(MatchEventEnvelope envelope);              // strictly increasing Seq per match
    Task PostGameSummary(PostGameSummaryDto summary);
    Task ChallengeReceived(ChallengeDto challenge);
    Task ChallengeUpdated(ChallengeDto challenge);
    Task PresenceChanged(PresenceDto presence);
    Task FriendRequestReceived(FriendRequestDto request);
    Task FriendsChanged();
    Task DirectMessage(DirectMessageDto message);
    Task Notification(NotificationDto notification);
    Task PenaltyApplied(PenaltyDto penalty);
    Task MessageHidden(MessageHiddenDto hidden);
}
