using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Engine.Analysis;

namespace TicTacToeUltimate.Contracts;

public sealed record GameSummaryDto(Guid GameId, MatchMode Mode, TimeControl TimeControl, PlayerCardDto Opponent,
    Color YourColor, GameResultForUser Result, EndReason Reason, int? RatingDelta, int Plies, DateTimeOffset EndedAt);
public sealed record GameRecordDto(Guid GameId, MatchMode Mode, TimeControl TimeControl, bool Rated, RuleSetDto Rules,
    SeatDto[] Seats, bool Swapped, ActionDto[] Actions, MatchResultDto Result, DateTimeOffset StartedAt, DateTimeOffset EndedAt);
public sealed record PositionValueDto(Value Value, int Distance);
public sealed record PlyAnalysisDto(int Ply, int Seat, MoveClass Class, PositionValueDto Before, string[] BestActions /* Notation tokens */);
public sealed record GameAnalysisDto(Guid GameId, string RulesId, PlyAnalysisDto[] Plies, int[] BlundersBySeat);

public sealed record LeaderboardRowDto(int Rank, PlayerCardDto Player, int Rating, int Games);
public sealed record LeaderboardPageDto(RatingPool Pool, LeaderboardScope Scope, string? Country, int Page, int PageSize,
    int TotalCount, LeaderboardRowDto[] Rows, LeaderboardRowDto? Me);

public sealed record PresenceDto(Guid UserId, PresenceStatus Status, Guid? MatchId);
public sealed record FriendDto(PlayerCardDto Player, PresenceDto Presence, int UnreadMessages, DateTimeOffset? LastSeenAt);
public sealed record FriendRequestDto(Guid RequestId, PlayerCardDto From, PlayerCardDto To, FriendRequestDirection Direction, DateTimeOffset CreatedAt);
public sealed record FriendsOverviewDto(FriendDto[] Friends, FriendRequestDto[] Requests, PlayerCardDto[] Blocked);
/// <summary>By username, or by user id (e.g. the opponent on the result screen, whose card carries no username).</summary>
public sealed record SendFriendRequestDto(string? Username, Guid? UserId = null);
public sealed record DirectMessageDto(long Id, Guid From, Guid To, string Body, DateTimeOffset SentAt, DateTimeOffset? ReadAt);
public sealed record SendDirectMessageDto(Guid ToUserId, string Body);

public sealed record CreateChallengeDto(TimeControl TimeControl /* not Ranked */, Guid? ToUserId);
public sealed record ChallengeDto(string Code, string Url, PlayerCardDto From, Guid? ToUserId, TimeControl TimeControl,
    RuleSetDto Rules, ChallengeStatus Status, DateTimeOffset ExpiresAt, Guid? MatchId);
public sealed record AcceptChallengeResultDto(bool Accepted, string? Error, Guid? MatchId);

/// <summary>Step 1 of a rewarded ad: ask the server for a single-use nonce (it also checks eligibility before the ad plays).</summary>
public sealed record RewardNonceRequestDto(RewardKind Kind, Guid? GameId);
/// <summary>The nonce to send back as the claim receipt after the ad; Error (reward_capped, reward_invalid) when not eligible.</summary>
public sealed record RewardNonceDto(string? Nonce, long ExpiresAtMs, string? Error);
public sealed record RewardClaimDto(RewardKind Kind, Guid? GameId, string Receipt);
public sealed record RewardClaimResultDto(bool Granted, string? Error, ProgressDto? Progress)
{
    /// <summary>Wallet after a Diamonds grant.</summary>
    public WalletDto? Wallet { get; init; }
}

public sealed record BotKeyDto(string KeyId, Guid BotUserId, string BotName, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
public sealed record CreateBotKeyDto(string BotName);
public sealed record CreatedBotKeyDto(string KeyId, string ApiKey /* "tttb_{keyId}_{secret}", shown once */, Guid BotUserId);

public sealed record NotificationDto(NotificationKind Kind, string Title, string Body, Guid? MatchId, string? Link);
