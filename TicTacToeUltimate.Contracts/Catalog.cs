using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Engine.Analysis;

namespace TicTacToeUltimate.Contracts;

public enum MatchMode { Ranked, Casual, Unlimited, Daily, Challenge, Arena, ArenaRanked }
public enum EndReason { Line, Timeout, Resign, Abandoned, Draw, Aborted, Knockout, Decision, Overtime }
public enum RatingPool { Ranked, CasualX2, CasualX4, Unlimited, Daily, ArenaCasual, ArenaRanked }
public enum Tier { Bronze, Silver, Gold, Platinum, Diamond, Master }
public enum PresenceStatus { Offline, Online, InGame }
/// <summary>Deleted: anonymised row kept for game history; can never authenticate.</summary>
public enum AccountKind { Guest, Account, Bot, Deleted }
public enum ClientPlatform { Web, iOS, Android, MacCatalyst }
public enum LeaderboardScope { Global, Friends, Country, MasterSeason }
/// <summary>Rewarded ads grant Diamonds only (2026-10-16); the other kinds stay for old clients and return reward_invalid.</summary>
public enum RewardKind { StreakFreeze, ExtraQuest, Analysis, Diamonds }
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
        // Hidden matchmaking pools for the new casual queues.
        TimeControl.Casual => RatingPool.CasualX2, TimeControl.OldSchool => RatingPool.CasualX4,
        _ => RatingPool.Daily,
    };
    /// <summary>Daily is unrated since 2026-10-16 (24 h per move in a solved game is a solver contest).</summary>
    public static bool IsVisible(RatingPool p) => p is RatingPool.Ranked or RatingPool.ArenaRanked;
    /// <summary>Visible pools in display order (Me / profile ratings). Classic Ranked is retired from the UI (2026-10-15).</summary>
    public static readonly RatingPool[] Visible = [RatingPool.ArenaRanked];
    /// <summary>The headline "Ranked" rating and tier (Home, Profile, Leaderboards, player cards): Arena.</summary>
    public const RatingPool Headline = RatingPool.ArenaRanked;
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
        AnalysisUnavailable = "analysis_unavailable", RewardRequired = "reward_required", AdBlocked = "ad_blocked", ServerUpdating = "server_updating", RewardInvalid = "reward_invalid",
        RewardCapped = "reward_capped", RematchNotAllowed = "rematch_not_allowed", EmoteUnknown = "emote_unknown",
        Timeout = "timeout", ProtocolVersion = "protocol_version", Validation = "validation", Internal = "internal",
        Forbidden = "forbidden", InsufficientDiamonds = "insufficient_diamonds", ItemUnavailable = "item_unavailable";

    public static string FromMoveError(MoveError e) => e switch
    {
        MoveError.Occupied => Occupied, MoveError.CellJustVacated => CellJustVacated,
        MoveError.BannedOpening => BannedOpening, MoveError.WrongActionKind => WrongActionKind,
        MoveError.GameOver => MatchOver, _ => IllegalMove,
    };
}

public sealed record EmoteDto(string Id, string Kind /* "bert" | "phrase" (legacy "emote") */, string Text);

public static class EmoteCatalog
{
    public static readonly IReadOnlyList<EmoteDto> All =
    [
        // Bert (mascot) emotes. Text is "bert:<mood>": clients render the animated Bert; bots/tools see a readable token.
        new("bert-happy", "bert", "bert:happy"), new("bert-laugh", "bert", "bert:laugh"), new("bert-jumpy", "bert", "bert:jumpy"),
        new("bert-love", "bert", "bert:love"), new("bert-cool", "bert", "bert:cool"), new("bert-think", "bert", "bert:think"),
        new("bert-wave", "bert", "bert:wave"), new("bert-gg", "bert", "bert:gg"), new("bert-sad", "bert", "bert:sad"),
        new("bert-cry", "bert", "bert:cry"), new("bert-angry", "bert", "bert:angry"), new("bert-shocked", "bert", "bert:shocked"),
        new("hi", "phrase", "Hi!"), new("glhf", "phrase", "Good luck!"), new("wp", "phrase", "Well played!"),
        new("nice", "phrase", "Nice move!"), new("oops", "phrase", "Oops!"), new("thanks", "phrase", "Thanks!"),
        new("hurry", "phrase", "Tick tock…"), new("rematch", "phrase", "Rematch?"),
    ];
    public static bool IsKnown(string id) => All.Any(e => e.Id == id);
}
