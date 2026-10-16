using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Engine.Analysis;
using System.Text.Json.Serialization;

namespace TicTacToeUltimate.Contracts;

public sealed record SeatDto(int Seat /* 0 = A (starts as X), 1 = B */, PlayerCardDto Player, Color Color, bool Connected, int? RttMs);

public sealed record ClockDto(
    TimeControl TimeControl,
    long? TurnStartedAtMs,            // server epoch ms; null when no turn is running (ended / unlimited before start)
    long? DeadlineAtMs,               // nominal: TurnStartedAtMs + AllowanceMs; null for Unlimited
    int? AllowanceMs,
    int GameElapsedMsAtTurnStart,     // sum of credited ThinkMs of completed turns
    bool SuddenDeath,
    int? NextAllowanceMs,
    int? NextStepAtGameElapsedMs,     // drives "window 8s -> 7s in 5s"
    int[] LagBudgetLeftMs);           // [seatA, seatB]

public sealed record ActionDto(int Ply, int Seat, ActionKind Kind, int? Cell, Color? Color /* null for swap decisions */,
    int? VanishedCell, int ThinkMs, bool Auto);

public sealed record MatchResultDto(Outcome Outcome, int? WinnerSeat, EndReason Reason, int[] WinLine, bool Rated, string? Detail);

public sealed record MatchSnapshotDto(
    Guid MatchId, long Seq, MatchMode Mode, TimeControl TimeControl, bool Rated, RuleSetDto Rules,
    SeatDto[] Seats, bool Swapped, ActionDto[] Actions, string Board, int Ply, GamePhase Phase,
    Color ToMove, int? ToMoveSeat, int[] LegalCells, int? NextToVanish, ClockDto? Clock, MatchResultDto? Result,
    int? YourSeat /* null = spectator */, int SpectatorCount, bool RematchAllowed, long ServerNowMs,
    ArenaRulesDto? ArenaRules = null /* Arena (health) modes only */, ArenaStateDto? Arena = null);

public sealed record MatchEventEnvelope(Guid MatchId, long Seq, long ServerTimeMs, MatchEvent Event);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(ActionApplied), "action")]
[JsonDerivedType(typeof(TurnStarted), "turn")]
[JsonDerivedType(typeof(MatchEnded), "end")]
[JsonDerivedType(typeof(SeatConnectionChanged), "conn")]
[JsonDerivedType(typeof(EmoteShown), "emote")]
[JsonDerivedType(typeof(RematchOffered), "rematchOffer")]
[JsonDerivedType(typeof(RematchStarted), "rematch")]
[JsonDerivedType(typeof(SpectatorCountChanged), "spec")]
public abstract record MatchEvent;
public sealed record ActionApplied(ActionDto Action, string Board, int? NextToVanish, bool Swapped, SeatDto[] Seats, ArenaStateDto? Arena = null) : MatchEvent;
public sealed record TurnStarted(int Ply, Color ToMove, int ToMoveSeat, GamePhase Phase, ClockDto? Clock, int[] LegalCells) : MatchEvent;
public sealed record MatchEnded(MatchResultDto Result) : MatchEvent;
public sealed record SeatConnectionChanged(int Seat, bool Connected, int? RttMs) : MatchEvent;
public sealed record EmoteShown(int Seat, string EmoteId) : MatchEvent;
public sealed record RematchOffered(int Seat) : MatchEvent;
public sealed record RematchStarted(Guid NewMatchId) : MatchEvent;
public sealed record SpectatorCountChanged(int Count) : MatchEvent;

public sealed record SubmitActionDto(Guid MatchId, int ExpectedPly, ActionKind Kind, int? Cell, Guid ActionId);
public sealed record MoveAckDto(bool Accepted, string? Error, long Seq, int CurrentPly);
public sealed record CommandAckDto(bool Ok, string? Error);

public sealed record PostGameSummaryDto(Guid MatchId, MatchResultDto Result, GameResultForUser YourResult,
    RatingChangeDto? Rating, TierChangeDto? Tier, int WinStreak, ProgressDto? Progress,
    bool AnalysisAvailable, int? YourBlunders, bool RematchAllowed)
{
    /// <summary>Diamonds this game paid (win/loss payout + quests + streak milestones + promotion); 0 for challenges, rematches and bot games.</summary>
    public int DiamondsEarned { get; init; }
    /// <summary>Wallet after the payout.</summary>
    public WalletDto? Wallet { get; init; }
}

public sealed record QueueRequestDto(TimeControl TimeControl);
public sealed record QueueTicketDto(bool Accepted, string? Error, TimeControl TimeControl, long EnqueuedAtMs);
/// <summary>ServerUpdating: a deploy is draining live games; matching resumes right after the restart.</summary>
public sealed record QueueStatusDto(TimeControl TimeControl, int WaitedMs, int RatingWindow, bool GuestPool, bool ServerUpdating = false);
public sealed record MatchFoundDto(Guid MatchId, MatchMode Mode, TimeControl TimeControl, PlayerCardDto Opponent, Color YourColor, int YourSeat);

public sealed record ClientHelloDto(int ProtocolVersion, string ClientVersion, ClientPlatform Platform, string TimeZone, string? Locale);
public sealed record ServerHelloDto(MeDto Me, long ServerNowMs, Guid[] ActiveMatchIds, int DailyYourTurn, ChallengeDto[] PendingChallenges);

public sealed record DailyGameDto(Guid MatchId, PlayerCardDto Opponent, Color YourColor, bool YourTurn, long? DeadlineAtMs, int Ply);
public sealed record LiveMatchDto(Guid MatchId, TimeControl TimeControl, PlayerCardDto[] Players, int Ply, int SpectatorCount);
/// <summary>One of the caller's unfinished games (live or Daily), for the "Active games" list and the resume banner.</summary>
public sealed record ActiveMatchDto(Guid MatchId, MatchMode Mode, TimeControl TimeControl, PlayerCardDto Opponent, Color YourColor,
    bool YourTurn, int Ply, long? DeadlineAtMs, bool Daily);
