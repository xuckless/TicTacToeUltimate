using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Engine.Analysis;

namespace TicTacToeUltimate.Contracts;

public sealed record RuleSetDto(string Id, int BoardSize, int WinLength, RemovalKind Removal, int Window,
    bool VacatedCellPlayable, bool VanishingPieceCanWin, OpeningRule Opening)
{
    public static RuleSetDto From(RuleSet r) => new(r.Id, r.BoardSize, r.WinLength, r.Removal, r.Window,
        r.VacatedCellPlayable, r.VanishingPieceCanWin, r.Opening);
    public RuleSet ToEngine() => new(BoardSize, WinLength, Removal, Window, VacatedCellPlayable, VanishingPieceCanWin, Opening);
}

public sealed record TierDto(Tier Tier, int Division /* 3..1; 0 for Master */, int PointsToNext /* 0 for Master */, double Progress /* 0..1 inside division */);
public sealed record TierChangeDto(TierDto Before, TierDto After, bool Promoted, bool Demoted);
public sealed record RatingDto(RatingPool Pool, int Rating, int Rd, int Games, bool Provisional, TierDto? Tier, int WeekDelta);
public sealed record RatingChangeDto(RatingPool Pool, int Before, int After, int Delta, bool Provisional);

public sealed record PlayerCardDto(Guid UserId, string DisplayName, string AvatarId, string? Country, AccountKind Kind,
    TierDto? Tier, int? Rating, bool Provisional);

public sealed record MeDto(Guid UserId, AccountKind Kind, string DisplayName, string? Username, bool NeedsUsername,
    string AvatarId, string? Country, string TimeZone, int RenamesLeft, RatingDto[] Ratings /* visible pools only */);

public sealed record ProfileStatsDto(int GamesPlayed, int Wins, int Losses, int Draws, double WinRate,
    int CurrentStreak, int BestStreak, int DailyInProgress);
public sealed record ProfileDto(PlayerCardDto Card, DateTimeOffset JoinedAt, RatingDto[] Ratings, ProfileStatsDto Stats,
    GameSummaryDto[] RecentGames, FriendStatus FriendStatus);

public sealed record StreakDto(int Current, int Best, int FreezesBanked, bool SecuredToday, bool AtRisk);
public sealed record QuestDto(string QuestId /* "win1"|"win3"|"win5"|"bonus" */, string Title, int Progress, int Target, bool Completed, bool Bonus);
public sealed record ProgressDto(StreakDto Streak, QuestDto[] Quests, DateOnly Day, long ResetsAtMs, bool ExtraQuestAvailable);

public sealed record TierBandDto(Tier Tier, int MinRating, int DivisionWidth);
public sealed record AdConfigDto(int InterstitialEveryGames, bool SkipAfterTwoLosses);
public sealed record ModeConfigDto(TimeControl TimeControl, MatchMode Mode, RuleSetDto Rules, bool Rated, bool GuestsAllowed);
public sealed record AppConfigDto(int ProtocolVersion, bool DevAuth, bool SupabaseAuth, string? SupabaseUrl, string? SupabaseAnonKey,
    ModeConfigDto[] Modes, EmoteDto[] Emotes, TierBandDto[] Tiers, int LeaderboardMinGames, AdConfigDto Ads, string WebBaseUrl);
