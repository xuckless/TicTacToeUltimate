using TicTacToeUltimate.Client.Abstractions;
using System.Collections.Immutable;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.Client.State;

public interface IStore<out TState> where TState : class
{
    TState State { get; }
    event Action? Changed;            // raised after State is replaced; may fire on any thread; UI must InvokeAsync
}

public enum ConnectionPhase { Idle, Connecting, Online, Reconnecting, Offline }
public enum SessionStatus { Initializing, SignedOut, SignedIn }
public sealed record SessionState(SessionStatus Status, MeDto? Me, AppConfigDto? Config, ConnectionPhase Connection,
    int ReconnectAttempt, ImmutableArray<Guid> ActiveMatchIds, int DailyYourTurn, string? Error)
{
    public static SessionState Initial { get; } = new(SessionStatus.Initializing, null, null, ConnectionPhase.Idle, 0, [], 0, null);
    public bool IsGuest => Me?.Kind == AccountKind.Guest;
    /// <summary>Seconds spent waiting for a sleeping/restarting server at startup (0 = not waiting).</summary>
    public int WakingSeconds { get; init; }
}
public interface ISessionStore : IStore<SessionState>
{
    Task InitializeAsync();                                   // load config, restore token, connect, Hello; idempotent
    Task<IReadOnlyList<DevUserDto>> GetDevUsersAsync();
    Task SignInDevAsync(Guid userId);
    Task SignInDevNewUserAsync(string username);
    Task SignInGuestAsync();
    /// <summary>Email + password (Supabase). Returns an error text or null.</summary>
    Task<string?> SignInEmailAsync(string email, string password);
    /// <summary>Creates the account; returns (needsConfirmation, error).</summary>
    Task<(bool NeedsConfirmation, string? Error)> SignUpEmailAsync(string email, string password);
    /// <summary>Sends a password reset email. Returns an error text or null.</summary>
    Task<string?> ResetPasswordAsync(string email);
    /// <summary>Completes a confirmation/recovery redirect (URL fragment with tokens). True when signed in.</summary>
    Task<bool> CompleteRedirectAsync(string fragment);
    /// <summary>Sets a new password for the signed-in email account.</summary>
    Task<string?> SetPasswordAsync(string password);
    Task SignOutAsync();
    Task<CommandAckDto> SetUsernameAsync(string username, bool acceptTos);
    Task<CommandAckDto> UpdateProfileAsync(UpdateProfileDto patch);
}

public enum QueuePhase { Idle, Entering, Searching, Found }
public sealed record QueueState(QueuePhase Phase, TimeControl? TimeControl, QueueStatusDto? Status, MatchFoundDto? Found, string? Error)
{ public static QueueState Idle { get; } = new(QueuePhase.Idle, null, null, null, null); }
public interface IQueueStore : IStore<QueueState>
{
    Task EnterAsync(TimeControl timeControl);
    Task LeaveAsync();
    void Acknowledge();                                       // Found -> Idle after navigation
}

public enum ClockUrgency { Idle, Normal, Warning /* <= 5 s */, Critical /* <= 3 s */ }
public sealed record ClockView(int? RemainingMs, int? AllowanceMs, int GameElapsedMs, bool SuddenDeath,
    int? NextAllowanceMs, int? NextStepInMs, ClockUrgency Urgency, int ToMoveSeat);
public sealed record EmoteView(int Seat, string EmoteId, string Text, long ShownAtMs);
public sealed record MatchView(
    Guid MatchId,
    MatchSnapshotDto Snapshot,                // last authoritative snapshot with events folded in
    GameState Engine,                         // Rules.Replay of Snapshot.Actions (authoritative)
    int? PendingCell,                         // optimistic placement awaiting ack
    int? MySeat, bool IsSpectator, bool IsMyTurn,
    ImmutableArray<int> ThreatCells,          // BoardHints.Threats (ImmediateWins after each of my moves, blockable cells only); empty if hints off or Ranked/Daily
    ImmutableArray<EmoteView> RecentEmotes,
    MatchResultDto? Result, PostGameSummaryDto? Summary,
    bool RematchOfferedByMe, bool RematchOfferedByOpponent, Guid? RematchMatchId,
    bool Resyncing, string? LastError);
public sealed record MatchesState(ImmutableDictionary<Guid, MatchView> Matches)
{ public static MatchesState Empty { get; } = new(ImmutableDictionary<Guid, MatchView>.Empty); }
public interface IMatchStore : IStore<MatchesState>
{
    Task<MatchView?> OpenAsync(Guid matchId);                 // JoinMatch; null + LastError on failure
    void Close(Guid matchId);                                 // LeaveMatch, drop from state
    Task PlaceAsync(Guid matchId, int cell);
    Task DecideSwapAsync(Guid matchId, bool swap);
    Task ResignAsync(Guid matchId);
    Task SendEmoteAsync(Guid matchId, string emoteId);
    Task OfferRematchAsync(Guid matchId);
    ClockView? GetClock(Guid matchId);                        // live value computed from server time
    event Action? ClockTick;                                  // 10 Hz, only while any opened match has a running clock
}

public sealed record SocialState(ImmutableArray<FriendDto> Friends, ImmutableArray<FriendRequestDto> Requests,
    ImmutableArray<PlayerCardDto> Blocked, ImmutableArray<ChallengeDto> IncomingChallenges,
    ImmutableDictionary<Guid, ImmutableArray<DirectMessageDto>> Threads, bool Loaded, string? Error)
{ public static SocialState Empty { get; } = new([], [], [], [], ImmutableDictionary<Guid, ImmutableArray<DirectMessageDto>>.Empty, false, null); }
public interface ISocialStore : IStore<SocialState>
{
    Task LoadAsync();
    Task<IReadOnlyList<PlayerCardDto>> SearchAsync(string prefix);
    Task<CommandAckDto> SendRequestAsync(string username);
    Task<CommandAckDto> RespondAsync(Guid requestId, bool accept);
    Task RemoveFriendAsync(Guid userId);
    Task BlockAsync(Guid userId);
    Task UnblockAsync(Guid userId);
    Task LoadThreadAsync(Guid friendId, long? beforeId = null);
    Task<CommandAckDto> SendMessageAsync(Guid friendId, string body);
    Task MarkReadAsync(Guid friendId);
    Task<ChallengeDto> CreateChallengeAsync(CreateChallengeDto request);
    Task<ChallengeDto?> GetChallengeAsync(string code);
    Task<AcceptChallengeResultDto> AcceptChallengeAsync(string code);
    Task DeclineChallengeAsync(string code);
}

public sealed record ProgressState(ProgressDto? Progress, bool Loaded)
{ public static ProgressState Empty { get; } = new(null, false); }
public interface IProgressStore : IStore<ProgressState>
{
    Task LoadAsync();
    /// <summary>Shows a rewarded ad via IAdCoordinator, then POST /api/rewards/claim. Returns the claim result.</summary>
    Task<RewardClaimResultDto> ClaimRewardAsync(RewardKind kind, Guid? gameId = null);
}

/// <summary>Stateless queries (no caching contract).</summary>
public interface IGameQueries
{
    Task<ProfileDto?> GetProfileAsync(string username);
    Task<IReadOnlyList<GameSummaryDto>> GetHistoryAsync(Guid userId, DateTimeOffset? before = null, int take = 20);
    Task<GameRecordDto?> GetGameAsync(Guid gameId);
    Task<(GameAnalysisDto? Analysis, string? Error)> GetAnalysisAsync(Guid gameId);
    Task<LeaderboardPageDto> GetLeaderboardAsync(RatingPool pool, LeaderboardScope scope, string? country, int page);
    Task<IReadOnlyList<DailyGameDto>> GetDailyGamesAsync();
    Task<IReadOnlyList<LiveMatchDto>> GetLiveFriendMatchesAsync();
}

public enum ThemePreference { System, Dark, Light }
public enum MotionPreference { System, Reduced, Full }
public sealed record SettingsState(ThemePreference Theme, bool Sfx, bool Music, bool Haptics, bool ImpactFrames,
    MotionPreference Motion, bool ThreatHints, bool MuteOpponentEmotes, double SfxVolume, double MusicVolume)
{ public static SettingsState Default { get; } = new(ThemePreference.Dark, true, false, true, true, MotionPreference.System, false, false, 0.8, 0.5); }
public interface ISettingsStore : IStore<SettingsState>
{
    Task LoadAsync();
    Task UpdateAsync(Func<SettingsState, SettingsState> change);   // persists via IKeyValueStore, applies to ISoundService
}

public interface IAdCoordinator
{
    bool BannersAllowed { get; }                     // false while a match route is shown or queueing
    void SetMatchRouteActive(bool active);           // called by MatchLayout (WP6)
    Task OnGameCompletedAsync(GameResultForUser result);   // AdPolicy decides; never shows during a match route
    Task<RewardedAdResult> ShowRewardedAsync(RewardKind kind);
}
