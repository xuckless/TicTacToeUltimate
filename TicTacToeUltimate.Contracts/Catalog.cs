using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Engine.Analysis;

namespace TicTacToeUltimate.Contracts;

public enum MatchMode { Ranked, Casual, Unlimited, Daily, Challenge, Arena, ArenaRanked }
public enum EndReason { Line, Timeout, Resign, Abandoned, Draw, Aborted, Knockout, Decision, Overtime }
public enum RatingPool { Ranked, CasualX2, CasualX4, Unlimited, Daily, ArenaCasual, ArenaRanked }
public enum Tier { Bronze, Silver, Gold, Platinum, Diamond, Master }
public enum PresenceStatus { Offline, Online, InGame }
public enum AccountKind { Guest, Account, Bot }
public enum ClientPlatform { Web, iOS, Android, MacCatalyst }
public enum LeaderboardScope { Global, Friends, Country, MasterSeason }
public enum RewardKind { StreakFreeze, ExtraQuest, Analysis }
public enum ChallengeStatus { Open, Accepted, Declined, Cancelled, Expired }
public enum FriendRequestDirection { Incoming, Outgoing }
public enum FriendStatus { Self, None, Friends, RequestSent, RequestReceived, Blocked }
public enum GameResultForUser { Win, Loss, Draw, Aborted }
public enum NotificationKind { DailyYourTurn, Promotion, Demotion, ChallengeExpired, Info }

public static class ProtocolInfo { public const int Version = 1; }

public static class HubRoutes { public const string Game = "/hubs/game"; }
public static class BotRoutes { public const string WebSocket = "/bot/ws"; }

public static class RatingPools
{
    public static RatingPool For(TimeControl tc) => tc switch
    {
        TimeControl.Ranked => RatingPool.Ranked, TimeControl.CasualX2 => RatingPool.CasualX2,
        TimeControl.CasualX4 => RatingPool.CasualX4, TimeControl.Unlimited => RatingPool.Unlimited,
        TimeControl.ArenaCasual => RatingPool.ArenaCasual, TimeControl.ArenaRanked => RatingPool.ArenaRanked,
        _ => RatingPool.Daily,
    };
    public static bool IsVisible(RatingPool p) => p is RatingPool.Ranked or RatingPool.Daily or RatingPool.ArenaRanked;
    /// <summary>Visible pools in display order (Me / profile ratings).</summary>
    public static readonly RatingPool[] Visible = [RatingPool.Ranked, RatingPool.Daily, RatingPool.ArenaRanked];
}

public static class ErrorCodes
{
    public const string NotYourTurn = "not_your_turn", StalePly = "stale_ply", IllegalMove = "illegal_move",
        Occupied = "occupied", CellJustVacated = "cell_just_vacated", BannedOpening = "banned_opening",
        WrongActionKind = "wrong_action_kind", MatchNotFound = "match_not_found", MatchOver = "match_over",
        NotAParticipant = "not_a_participant", SpectateNotAllowed = "spectate_not_allowed", RateLimited = "rate_limited",
        GuestNotAllowed = "guest_not_allowed", UsernameRequired = "username_required", AlreadyInQueue = "already_in_queue",
        AlreadyInMatch = "already_in_match", DailyLimit = "daily_limit", ChallengeNotFound = "challenge_not_found",
        ChallengeExpired = "challenge_expired", ChallengeTaken = "challenge_taken", CannotChallengeSelf = "cannot_challenge_self",
        NotFriends = "not_friends", Blocked = "blocked", UsernameTaken = "username_taken", UsernameInvalid = "username_invalid",
        NoRenamesLeft = "no_renames_left", TosRequired = "tos_required", AnalysisWarming = "analysis_warming",
        AnalysisUnavailable = "analysis_unavailable", RewardRequired = "reward_required", RewardInvalid = "reward_invalid",
        RewardCapped = "reward_capped", RematchNotAllowed = "rematch_not_allowed", EmoteUnknown = "emote_unknown",
        Timeout = "timeout", ProtocolVersion = "protocol_version", Validation = "validation", Internal = "internal";

    public static string FromMoveError(MoveError e) => e switch
    {
        MoveError.Occupied => Occupied, MoveError.CellJustVacated => CellJustVacated,
        MoveError.BannedOpening => BannedOpening, MoveError.WrongActionKind => WrongActionKind,
        MoveError.GameOver => MatchOver, _ => IllegalMove,
    };
}

public sealed record EmoteDto(string Id, string Kind /* "emote" | "phrase" */, string Text);

public static class EmoteCatalog
{
    public static readonly IReadOnlyList<EmoteDto> All =
    [
        new("gg", "emote", "🤝"), new("laugh", "emote", "😂"), new("wow", "emote", "😮"), new("think", "emote", "🤔"),
        new("fire", "emote", "🔥"), new("cry", "emote", "😭"), new("angry", "emote", "😤"), new("cool", "emote", "😎"),
        new("hi", "phrase", "Hi!"), new("glhf", "phrase", "Good luck!"), new("wp", "phrase", "Well played!"),
        new("nice", "phrase", "Nice move!"), new("oops", "phrase", "Oops!"), new("thanks", "phrase", "Thanks!"),
        new("hurry", "phrase", "Tick tock…"), new("rematch", "phrase", "Rematch?"),
    ];
    public static bool IsKnown(string id) => All.Any(e => e.Id == id);
}
