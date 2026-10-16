using Dapper;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Persistence.InMemory;
using TicTacToeUltimate.Server.Persistence.Postgres;

namespace TicTacToeUltimate.Server.Persistence;

/// <summary>
/// In-app account deletion (Apple 5.1.1(v), Google Play account-deletion policy). PII goes immediately: username, display name,
/// avatar, country, time zone, device binding, ratings, streaks, quests, reward grants, friends, requests, blocks, messages,
/// open challenges, bot keys. Finished games stay (the opponent's history and replays are theirs too) with the seat anonymised
/// to "Deleted player"; the player row is kept as <see cref="AccountKind.Deleted"/> so game foreign keys hold and the id can
/// never sign in again.
/// </summary>
public interface IAccountDeletionStore
{
    /// <summary>Anonymises the player and purges their personal data. Idempotent.</summary>
    Task DeleteAsync(Guid userId, CancellationToken ct);
}

/// <summary>Display name shown on the seats of a deleted player's finished games.</summary>
public static class DeletedPlayer
{
    public const string DisplayName = "Deleted player";
}

/// <summary>Postgres: one transaction.</summary>
public sealed class PgAccountDeletionStore(PgDb db) : IAccountDeletionStore
{
    /// <inheritdoc />
    public async Task DeleteAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var p = new { id = userId, name = DeletedPlayer.DisplayName };
        await c.ExecuteAsync("""
            update players set kind = 'deleted', username = null, display_name = @name, avatar_id = 'a1', country = null,
              time_zone = 'UTC', renames_left = 0, tos_accepted_at = null, device_id = null where id = @id;
            delete from ratings where user_id = @id;
            delete from streaks where user_id = @id;
            delete from quest_days where user_id = @id;
            delete from reward_grants where user_id = @id;
            delete from friend_requests where from_id = @id or to_id = @id;
            delete from friendships where user_lo = @id or user_hi = @id;
            delete from blocks where blocker = @id or blocked = @id;
            delete from direct_messages where sender = @id or recipient = @id;
            delete from challenges where (creator_id = @id or target_user_id = @id) and status <> 'Accepted';
            update bot_keys set revoked_at = now() where owner_user_id = @id and revoked_at is null;
            update games set rules = jsonb_set(jsonb_set(rules, '{seatA,displayName}', to_jsonb(@name::text)), '{seatA,kind}', '"Deleted"')
              where seat_a = @id;
            update games set rules = jsonb_set(jsonb_set(rules, '{seatB,displayName}', to_jsonb(@name::text)), '{seatB,kind}', '"Deleted"')
              where seat_b = @id;
            """, p, tx);
        await tx.CommitAsync(ct);
    }
}

/// <summary>InMemory: delegates to each store.</summary>
public sealed class InMemoryAccountDeletionStore(IUserRepository users, IRatingRepository ratings, IGameRepository games, IProgressRepository progress,
    IFriendRepository friends, IChatRepository chat, IChallengeRepository challenges, IBotKeyRepository botKeys, IRewardLedger ledger,
    TimeProvider time) : IAccountDeletionStore
{
    /// <inheritdoc />
    public Task DeleteAsync(Guid userId, CancellationToken ct)
    {
        (users as InMemoryUserRepository)?.Anonymise(userId);
        (ratings as InMemoryRatingRepository)?.RemoveUser(userId);
        (games as InMemoryGameRepository)?.AnonymiseSeats(userId);
        (progress as InMemoryProgressRepository)?.RemoveUser(userId);
        (friends as InMemoryFriendRepository)?.RemoveUser(userId);
        (chat as InMemoryChatRepository)?.RemoveUser(userId);
        (challenges as InMemoryChallengeRepository)?.RemoveUser(userId);
        (botKeys as InMemoryBotKeyRepository)?.RevokeOwner(userId, time.GetUtcNow());
        (ledger as InMemoryRewardLedger)?.RemoveUser(userId);
        return Task.CompletedTask;
    }
}
