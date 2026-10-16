using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Engine.Analysis;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Server.Abstractions.Persistence;

public sealed record UserRecord(Guid Id, AccountKind Kind, string? Username, string DisplayName, string AvatarId,
    string? Country, string TimeZone, int RenamesLeft, DateTimeOffset? TosAcceptedAt, string? DeviceId,
    DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);
public sealed record ProfilePatch(string? AvatarId, string? Country, string? TimeZone);
public enum UsernameResult { Ok, Taken, Invalid, NoRenamesLeft }

public interface IUserRepository
{
    Task<UserRecord?> GetAsync(Guid id, CancellationToken ct);
    Task<UserRecord?> GetByUsernameAsync(string username, CancellationToken ct);     // case-insensitive
    Task<UserRecord?> GetByDeviceIdAsync(string deviceId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, UserRecord>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<bool> CreateAsync(UserRecord user, CancellationToken ct);                    // false if id or username exists
    /// <summary>First set (Guest->Account upgrade or Account onboarding) does not consume a rename; later sets do.</summary>
    Task<UsernameResult> SetUsernameAsync(Guid id, string username, DateTimeOffset? tosAcceptedAt, CancellationToken ct);
    Task UpdateProfileAsync(Guid id, ProfilePatch patch, CancellationToken ct);
    /// <summary>Store: a bought name change adds renames.</summary>
    Task AddRenamesAsync(Guid id, int count, CancellationToken ct);
    /// <summary>Moderation: drop the username (the player must pick a new one, for free) and show a neutral name meanwhile.</summary>
    Task ResetUsernameAsync(Guid id, CancellationToken ct);
    /// <summary>UI language for pushes and notifications (players.locale).</summary>
    Task SetLocaleAsync(Guid id, string locale, CancellationToken ct);
    Task<string?> GetLocaleAsync(Guid id, CancellationToken ct);
    Task TouchAsync(Guid id, DateTimeOffset lastSeenAt, CancellationToken ct);
    Task<IReadOnlyList<UserRecord>> SearchAsync(string prefix, int take, CancellationToken ct);
    Task<IReadOnlyList<UserRecord>> ListAsync(int take, CancellationToken ct);        // dev user picker
}

public sealed record RatingRecord(Guid UserId, RatingPool Pool, double Rating, double Rd, double Volatility,
    int Games, int Wins, int Losses, int Draws, double Peak, DateTimeOffset? LastGameAt)
{
    public static RatingRecord Initial(Guid userId, RatingPool pool) => new(userId, pool, 1200, 350, 0.06, 0, 0, 0, 0, 1200, null);
}
public sealed record LeaderboardQuery(RatingPool Pool, LeaderboardScope Scope, string? Country, Guid Viewer,
    IReadOnlyCollection<Guid>? RestrictTo /* friends scope */, int MinGames, double? MinRating /* master */, int Page, int PageSize);
public sealed record LeaderboardEntry(int Rank, Guid UserId, double Rating, int Games);
public sealed record LeaderboardPage(IReadOnlyList<LeaderboardEntry> Rows, int TotalCount, LeaderboardEntry? Viewer);

public interface IRatingRepository
{
    Task<RatingRecord> GetAsync(Guid userId, RatingPool pool, CancellationToken ct);  // Initial(...) if missing
    Task<IReadOnlyList<RatingRecord>> GetAllAsync(Guid userId, CancellationToken ct);
    Task PutAsync(RatingRecord record, CancellationToken ct);                          // single writer: PostGameProcessor
    Task<LeaderboardPage> QueryAsync(LeaderboardQuery query, CancellationToken ct);
}

public sealed record RatingSnapshot(int Seat, RatingPool Pool, double Before, double After);
public sealed record GameRecord(Guid Id, MatchSetup Setup, bool Swapped, IReadOnlyList<StoredAction> Actions,
    MatchResultDto Result, DateTimeOffset StartedAt, DateTimeOffset EndedAt, IReadOnlyList<RatingSnapshot> Ratings);
public sealed record GameListItem(Guid Id, MatchSetup Setup, bool Swapped, MatchResultDto Result, int Plies,
    DateTimeOffset EndedAt, IReadOnlyList<RatingSnapshot> Ratings);

public interface IGameRepository
{
    Task<bool> SaveCompletedAsync(GameRecord game, CancellationToken ct);              // false if already saved (idempotency)
    Task<GameRecord?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<GameListItem>> ListForUserAsync(Guid userId, DateTimeOffset? before, int take, CancellationToken ct);
    Task<int> CountForUserAsync(Guid userId, CancellationToken ct);
}

/// <summary>Durable live state for Daily matches only.</summary>
public sealed record StoredDailyMatch(MatchSetup Setup, IReadOnlyList<StoredAction> Actions, long Version,
    DateTimeOffset? TurnDeadline, bool Ended, DateTimeOffset StartedAt);
public sealed class ConcurrencyException(Guid matchId) : Exception($"Version conflict on {matchId}");
public interface IDailyMatchRepository
{
    Task CreateAsync(MatchSetup setup, DateTimeOffset startedAt, DateTimeOffset firstDeadline, CancellationToken ct);
    /// <summary>Returns new version; throws ConcurrencyException if expectedVersion mismatches.</summary>
    Task<long> AppendActionAsync(Guid matchId, long expectedVersion, StoredAction action, DateTimeOffset? nextDeadline, CancellationToken ct);
    Task MarkEndedAsync(Guid matchId, CancellationToken ct);
    Task<StoredDailyMatch?> LoadAsync(Guid matchId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<StoredDailyMatch>> ListActiveForUserAsync(Guid userId, CancellationToken ct);
}

public enum FriendRequestResult { Created, AutoAccepted, AlreadyFriends, AlreadyRequested, Blocked, NotFound }
public sealed record FriendRequestRecord(Guid Id, Guid From, Guid To, DateTimeOffset CreatedAt);
public interface IFriendRepository
{
    Task<FriendRequestResult> RequestAsync(Guid from, Guid to, DateTimeOffset at, CancellationToken ct);   // mutual pending -> auto-accept
    Task<bool> RespondAsync(Guid requestId, Guid responder, bool accept, CancellationToken ct);
    Task RemoveAsync(Guid a, Guid b, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListFriendsAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<FriendRequestRecord>> ListRequestsAsync(Guid userId, CancellationToken ct);       // incoming + outgoing
    Task<bool> AreFriendsAsync(Guid a, Guid b, CancellationToken ct);
    Task BlockAsync(Guid blocker, Guid blocked, CancellationToken ct);                                  // also removes friendship/requests
    Task UnblockAsync(Guid blocker, Guid blocked, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListBlockedAsync(Guid blocker, CancellationToken ct);
    Task<bool> IsBlockedEitherWayAsync(Guid a, Guid b, CancellationToken ct);
}

public sealed record DirectMessageRecord(long Id, Guid From, Guid To, string Body, DateTimeOffset SentAt, DateTimeOffset? ReadAt);
public interface IChatRepository
{
    Task<DirectMessageRecord> AppendAsync(Guid from, Guid to, string body, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlyList<DirectMessageRecord>> PageAsync(Guid a, Guid b, long? beforeId, int take, CancellationToken ct); // newest first
    Task MarkReadAsync(Guid reader, Guid other, long upToId, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, int>> UnreadCountsAsync(Guid reader, CancellationToken ct);
    /// <summary>One message (hidden ones included: moderation evidence).</summary>
    Task<DirectMessageRecord?> GetAsync(long id, CancellationToken ct);
    /// <summary>Moderation: hide a message (kept as evidence, never shown again).</summary>
    Task HideAsync(long id, DateTimeOffset at, CancellationToken ct);
    /// <summary>Moderation "purge chat": hide everything a player sent. Returns how many.</summary>
    Task<int> HideAllFromAsync(Guid sender, DateTimeOffset at, CancellationToken ct);
}

public sealed record StreakRecord(Guid UserId, int Current, int Best, DateOnly? LastPlayedDay, int Freezes);
public sealed record QuestDayRecord(Guid UserId, DateOnly Day, int Wins, bool FreezeAwarded, int? BonusTarget /* wins target of bonus quest */, bool BonusClaimed);
public interface IProgressRepository
{
    Task<StreakRecord> GetStreakAsync(Guid userId, CancellationToken ct);
    Task PutStreakAsync(StreakRecord record, CancellationToken ct);
    Task<QuestDayRecord> GetQuestDayAsync(Guid userId, DateOnly day, CancellationToken ct);
    Task PutQuestDayAsync(QuestDayRecord record, CancellationToken ct);
}

public sealed record ChallengeRecord(string Code, Guid CreatorId, Guid? TargetUserId, TimeControl TimeControl, string RulesId,
    ChallengeStatus Status, Guid? MatchId, Guid? AcceptedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public interface IChallengeRepository
{
    Task<bool> CreateAsync(ChallengeRecord record, CancellationToken ct);           // false on code collision
    Task<ChallengeRecord?> GetAsync(string code, CancellationToken ct);
    /// <summary>Compare-and-set on Status. Returns false if current status != expected.</summary>
    Task<bool> TryTransitionAsync(string code, ChallengeStatus expected, ChallengeStatus next, Guid? acceptedBy, Guid? matchId, CancellationToken ct);
    Task<IReadOnlyList<ChallengeRecord>> ListOpenForTargetAsync(Guid targetUserId, DateTimeOffset now, CancellationToken ct);
}

public sealed record BotKeyRecord(string KeyId, Guid BotUserId, Guid OwnerUserId, string BotName, byte[] SecretHash,
    DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
public interface IBotKeyRepository
{
    Task<BotKeyRecord?> FindAsync(string keyId, CancellationToken ct);
    Task CreateAsync(BotKeyRecord record, CancellationToken ct);
    Task<bool> RevokeAsync(string keyId, Guid ownerUserId, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlyList<BotKeyRecord>> ListForOwnerAsync(Guid ownerUserId, CancellationToken ct);
}

public sealed record RewardGrantRecord(string Nonce, Guid UserId, RewardKind Kind, Guid? GameId, DateTimeOffset GrantedAt);
public interface IRewardLedger
{
    Task<bool> TryGrantAsync(RewardGrantRecord grant, CancellationToken ct);           // false if nonce already used
    Task<bool> HasGrantAsync(Guid userId, RewardKind kind, Guid? gameId, CancellationToken ct);
    /// <summary>Grants of <paramref name="kind"/> for the user at or after <paramref name="since"/> (daily caps).</summary>
    Task<int> CountSinceAsync(Guid userId, RewardKind kind, DateTimeOffset since, CancellationToken ct);
}

// ---------- economy ----------
public sealed record WalletRecord(Guid UserId, int Balance);
/// <summary>One ledger line. (UserId, Reason, Ref) is unique: appending the same grant twice is a no-op.</summary>
public sealed record LedgerEntry(Guid UserId, int Delta, string Reason, string Ref, DateTimeOffset At);
public sealed record InventoryItem(Guid UserId, string ItemId, DateTimeOffset AcquiredAt, DateTimeOffset? ExpiresAt);
public interface IWalletRepository
{
    Task<WalletRecord> GetAsync(Guid userId, CancellationToken ct);
    /// <summary>False when the (user, reason, ref) exists or the debit would overdraw.</summary>
    Task<bool> AppendAsync(LedgerEntry entry, CancellationToken ct);
    Task<int> CountSinceAsync(Guid userId, string reason, DateTimeOffset since, CancellationToken ct);
    Task<int> SumSinceAsync(Guid userId, DateTimeOffset since, bool positiveOnly, CancellationToken ct);
    Task<IReadOnlyList<InventoryItem>> ListInventoryAsync(Guid userId, CancellationToken ct);
    Task AddInventoryAsync(InventoryItem item, CancellationToken ct);
}

public sealed record SeasonRecord(int Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record MasterSeasonResult(int SeasonId, Guid UserId, double FinalRating, int Rank);
public interface ISeasonRepository
{
    Task<SeasonRecord> GetCurrentAsync(DateTimeOffset now, CancellationToken ct);      // creates first season if none
    Task CloseAsync(SeasonRecord season, IReadOnlyList<MasterSeasonResult> results, SeasonRecord next, CancellationToken ct);
}
