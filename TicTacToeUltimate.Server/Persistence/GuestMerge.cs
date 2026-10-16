using Dapper;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Persistence.InMemory;
using TicTacToeUltimate.Server.Persistence.Postgres;

namespace TicTacToeUltimate.Server.Persistence;

/// <summary>
/// Moves a guest's history into an account when the guest signs in (POST /api/auth/claim-guest): finished games (and so
/// replays), hidden casual ratings when the account has none, the streak when it is better, and quest days. The guest row
/// is then deleted, so the device mints a fresh guest after sign-out.
/// </summary>
public interface IGuestMergeStore
{
    /// <summary>Returns the number of games moved.</summary>
    Task<int> MergeAsync(Guid guestId, Guid accountId, CancellationToken ct);
}

/// <summary>Postgres: one transaction.</summary>
public sealed class PgGuestMergeStore(PgDb db) : IGuestMergeStore
{
    /// <inheritdoc />
    public async Task<int> MergeAsync(Guid guestId, Guid accountId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var p = new { g = guestId, a = accountId, gs = guestId.ToString(), acc = accountId.ToString() };
        var moved = await c.ExecuteAsync("""
            update games set
              seat_a = case when seat_a = @g then @a else seat_a end,
              seat_b = case when seat_b = @g then @a else seat_b end,
              rules = case when seat_a = @g then jsonb_set(jsonb_set(rules, '{seatA,userId}', to_jsonb(@acc::text)), '{seatA,kind}', '"Account"')
                           else jsonb_set(jsonb_set(rules, '{seatB,userId}', to_jsonb(@acc::text)), '{seatB,kind}', '"Account"') end
            where seat_a = @g or seat_b = @g
            """, p, tx);
        await c.ExecuteAsync("""
            update ratings set user_id = @a where user_id = @g
              and pool not in (select pool from ratings where user_id = @a);
            delete from ratings where user_id = @g;
            insert into streaks (user_id, current, best, last_played_day, freezes)
              select @a, current, best, last_played_day, freezes from streaks where user_id = @g
              on conflict (user_id) do update set current = excluded.current, best = greatest(streaks.best, excluded.best),
                last_played_day = excluded.last_played_day, freezes = greatest(streaks.freezes, excluded.freezes)
              where excluded.current > streaks.current;
            delete from streaks where user_id = @g;
            insert into quest_days (user_id, day, wins, freeze_awarded, bonus_target, bonus_claimed)
              select @a, day, wins, freeze_awarded, bonus_target, bonus_claimed from quest_days where user_id = @g
              on conflict (user_id, day) do update set wins = quest_days.wins + excluded.wins;
            delete from quest_days where user_id = @g;
            delete from players where id = @g and kind = 'guest';
            """, p, tx);
        await tx.CommitAsync(ct);
        return moved;
    }
}

/// <summary>InMemory: delegates to each store.</summary>
public sealed class InMemoryGuestMergeStore(IGameRepository games, IRatingRepository ratings, IProgressRepository progress, IUserRepository users)
    : IGuestMergeStore
{
    /// <inheritdoc />
    public Task<int> MergeAsync(Guid guestId, Guid accountId, CancellationToken ct)
    {
        var n = (games as InMemoryGameRepository)?.Reassign(guestId, accountId) ?? 0;
        (ratings as InMemoryRatingRepository)?.Reassign(guestId, accountId);
        (progress as InMemoryProgressRepository)?.Reassign(guestId, accountId);
        (users as InMemoryUserRepository)?.DeleteGuest(guestId);
        return Task.FromResult(n);
    }
}
