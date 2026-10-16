using Dapper;
using Npgsql;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions.Persistence;

namespace TicTacToeUltimate.Server.Persistence.Postgres;

/// <summary>Postgres friends/requests/blocks.</summary>
public sealed class PgFriendRepository(PgDb db) : IFriendRepository
{
    private static (Guid Lo, Guid Hi) Pair(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);
    private sealed class ReqRow { public Guid id; public Guid from_id; public Guid to_id; public DateTime created_at; }

    public async Task<FriendRequestResult> RequestAsync(Guid from, Guid to, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (await c.ExecuteScalarAsync<bool>("select exists(select 1 from blocks where (blocker=@from and blocked=@to) or (blocker=@to and blocked=@from))", new { from, to }, tx))
            return FriendRequestResult.Blocked;
        var (lo, hi) = Pair(from, to);
        if (await c.ExecuteScalarAsync<bool>("select exists(select 1 from friendships where user_lo=@lo and user_hi=@hi)", new { lo, hi }, tx))
            return FriendRequestResult.AlreadyFriends;
        if (await c.ExecuteScalarAsync<bool>("select exists(select 1 from friend_requests where from_id=@from and to_id=@to)", new { from, to }, tx))
            return FriendRequestResult.AlreadyRequested;
        var reverse = await c.ExecuteAsync("delete from friend_requests where from_id=@to and to_id=@from", new { from, to }, tx);
        if (reverse > 0)
        {
            await c.ExecuteAsync("insert into friendships (user_lo, user_hi, created_at) values (@lo, @hi, @at) on conflict do nothing", new { lo, hi, at = at.UtcDateTime }, tx);
            await tx.CommitAsync(ct);
            return FriendRequestResult.AutoAccepted;
        }
        await c.ExecuteAsync("insert into friend_requests (id, from_id, to_id, created_at) values (@id, @from, @to, @at)", new { id = Guid.NewGuid(), from, to, at = at.UtcDateTime }, tx);
        await tx.CommitAsync(ct);
        return FriendRequestResult.Created;
    }

    public async Task<bool> RespondAsync(Guid requestId, Guid responder, bool accept, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<ReqRow>("delete from friend_requests where id=@requestId and to_id=@responder returning *", new { requestId, responder }, tx);
        if (r is null) return false;
        if (accept)
        {
            var (lo, hi) = Pair(r.from_id, r.to_id);
            await c.ExecuteAsync("insert into friendships (user_lo, user_hi) values (@lo, @hi) on conflict do nothing", new { lo, hi }, tx);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task RemoveAsync(Guid a, Guid b, CancellationToken ct)
    {
        var (lo, hi) = Pair(a, b);
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("delete from friendships where user_lo=@lo and user_hi=@hi", new { lo, hi });
    }

    public async Task<IReadOnlyList<Guid>> ListFriendsAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Guid>("select case when user_lo=@userId then user_hi else user_lo end from friendships where user_lo=@userId or user_hi=@userId", new { userId })).ToList();
    }

    public async Task<IReadOnlyList<FriendRequestRecord>> ListRequestsAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<ReqRow>("select * from friend_requests where from_id=@userId or to_id=@userId order by created_at", new { userId }))
            .Select(r => new FriendRequestRecord(r.id, r.from_id, r.to_id, PgDb.Dto(r.created_at))).ToList();
    }

    public async Task<bool> AreFriendsAsync(Guid a, Guid b, CancellationToken ct)
    {
        var (lo, hi) = Pair(a, b);
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>("select exists(select 1 from friendships where user_lo=@lo and user_hi=@hi)", new { lo, hi });
    }

    public async Task BlockAsync(Guid blocker, Guid blocked, CancellationToken ct)
    {
        var (lo, hi) = Pair(blocker, blocked);
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync("insert into blocks (blocker, blocked) values (@blocker, @blocked) on conflict do nothing", new { blocker, blocked }, tx);
        await c.ExecuteAsync("delete from friendships where user_lo=@lo and user_hi=@hi", new { lo, hi }, tx);
        await c.ExecuteAsync("delete from friend_requests where (from_id=@blocker and to_id=@blocked) or (from_id=@blocked and to_id=@blocker)", new { blocker, blocked }, tx);
        await tx.CommitAsync(ct);
    }

    public async Task UnblockAsync(Guid blocker, Guid blocked, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("delete from blocks where blocker=@blocker and blocked=@blocked", new { blocker, blocked });
    }

    public async Task<IReadOnlyList<Guid>> ListBlockedAsync(Guid blocker, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Guid>("select blocked from blocks where blocker=@blocker", new { blocker })).ToList();
    }

    public async Task<bool> IsBlockedEitherWayAsync(Guid a, Guid b, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>("select exists(select 1 from blocks where (blocker=@a and blocked=@b) or (blocker=@b and blocked=@a))", new { a, b });
    }
}

/// <summary>Postgres direct messages.</summary>
public sealed class PgChatRepository(PgDb db) : IChatRepository
{
    private sealed class Row { public long id; public Guid sender; public Guid recipient; public string body = ""; public DateTime sent_at; public DateTime? read_at; }
    private static DirectMessageRecord Map(Row r) => new(r.id, r.sender, r.recipient, r.body, PgDb.Dto(r.sent_at), PgDb.Dto(r.read_at));

    public async Task<DirectMessageRecord> AppendAsync(Guid from, Guid to, string body, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return Map(await c.QuerySingleAsync<Row>("insert into direct_messages (sender, recipient, body, sent_at) values (@from, @to, @body, @at) returning *",
            new { from, to, body, at = at.UtcDateTime }));
    }

    public async Task<IReadOnlyList<DirectMessageRecord>> PageAsync(Guid a, Guid b, long? beforeId, int take, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Row>("""
            select * from direct_messages where least(sender, recipient) = least(@a::uuid, @b::uuid) and greatest(sender, recipient) = greatest(@a::uuid, @b::uuid)
              and (@beforeId::bigint is null or id < @beforeId::bigint) order by id desc limit @take
            """, new { a, b, beforeId, take })).Select(Map).ToList();
    }

    public async Task MarkReadAsync(Guid reader, Guid other, long upToId, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("update direct_messages set read_at=@at where recipient=@reader and sender=@other and id <= @upToId and read_at is null",
            new { reader, other, upToId, at = at.UtcDateTime });
    }

    private sealed class CountRow { public Guid sender; public int n; }

    public async Task<IReadOnlyDictionary<Guid, int>> UnreadCountsAsync(Guid reader, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<CountRow>("select sender, count(*)::int as n from direct_messages where recipient=@reader and read_at is null group by sender", new { reader }))
            .ToDictionary(r => r.sender, r => r.n);
    }
}

/// <summary>Postgres streaks and quest days.</summary>
public sealed class PgProgressRepository(PgDb db) : IProgressRepository
{
    private sealed class StreakRow { public Guid user_id; public int current; public int best; public DateTime? last_played_day; public short freezes; }
    private sealed class DayRow { public Guid user_id; public DateTime day; public int wins; public bool freeze_awarded; public int? bonus_target; public bool bonus_claimed; }

    public async Task<StreakRecord> GetStreakAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<StreakRow>("select * from streaks where user_id=@userId", new { userId });
        return r is null ? new StreakRecord(userId, 0, 0, null, 0)
            : new StreakRecord(userId, r.current, r.best, r.last_played_day is { } d ? DateOnly.FromDateTime(d) : null, r.freezes);
    }

    public async Task PutStreakAsync(StreakRecord s, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("""
            insert into streaks (user_id, current, best, last_played_day, freezes) values (@UserId, @Current, @Best, @day::date, @f)
            on conflict (user_id) do update set current=excluded.current, best=excluded.best, last_played_day=excluded.last_played_day, freezes=excluded.freezes
            """, new { s.UserId, s.Current, s.Best, day = s.LastPlayedDay?.ToDateTime(TimeOnly.MinValue), f = (short)s.Freezes });
    }

    public async Task<QuestDayRecord> GetQuestDayAsync(Guid userId, DateOnly day, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<DayRow>("select * from quest_days where user_id=@userId and day=@d::date", new { userId, d = day.ToDateTime(TimeOnly.MinValue) });
        return r is null ? new QuestDayRecord(userId, day, 0, false, null, false) : new QuestDayRecord(userId, day, r.wins, r.freeze_awarded, r.bonus_target, r.bonus_claimed);
    }

    public async Task PutQuestDayAsync(QuestDayRecord q, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("""
            insert into quest_days (user_id, day, wins, freeze_awarded, bonus_target, bonus_claimed) values (@UserId, @d::date, @Wins, @FreezeAwarded, @BonusTarget, @BonusClaimed)
            on conflict (user_id, day) do update set wins=excluded.wins, freeze_awarded=excluded.freeze_awarded, bonus_target=excluded.bonus_target, bonus_claimed=excluded.bonus_claimed
            """, new { q.UserId, d = q.Day.ToDateTime(TimeOnly.MinValue), q.Wins, q.FreezeAwarded, q.BonusTarget, q.BonusClaimed });
    }
}

/// <summary>Postgres challenges.</summary>
public sealed class PgChallengeRepository(PgDb db) : IChallengeRepository
{
    private sealed class Row
    {
        public string code = ""; public Guid creator_id; public Guid? target_user_id; public string time_control = ""; public string rules_id = ""; public string status = "";
        public Guid? match_id; public Guid? accepted_by; public DateTime created_at; public DateTime expires_at;
        public ChallengeRecord ToRecord() => new(code, creator_id, target_user_id, Enum.Parse<TimeControl>(time_control), rules_id, Enum.Parse<ChallengeStatus>(status),
            match_id, accepted_by, PgDb.Dto(created_at), PgDb.Dto(expires_at));
    }

    public async Task<bool> CreateAsync(ChallengeRecord r, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteAsync("""
            insert into challenges (code, creator_id, target_user_id, time_control, rules_id, status, match_id, accepted_by, created_at, expires_at)
            values (@Code, @CreatorId, @TargetUserId, @tc, @RulesId, @status, @MatchId, @AcceptedBy, @created, @expires) on conflict do nothing
            """, new
        {
            r.Code, r.CreatorId, r.TargetUserId, tc = r.TimeControl.ToString(), r.RulesId, status = r.Status.ToString(), r.MatchId, r.AcceptedBy,
            created = r.CreatedAt.UtcDateTime, expires = r.ExpiresAt.UtcDateTime,
        }) == 1;
    }

    public async Task<ChallengeRecord?> GetAsync(string code, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<Row>("select * from challenges where code=@code", new { code }))?.ToRecord();
    }

    public async Task<bool> TryTransitionAsync(string code, ChallengeStatus expected, ChallengeStatus next, Guid? acceptedBy, Guid? matchId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteAsync("update challenges set status=@n, accepted_by=coalesce(@acceptedBy, accepted_by), match_id=coalesce(@matchId, match_id) where code=@code and status=@e",
            new { code, e = expected.ToString(), n = next.ToString(), acceptedBy, matchId }) == 1;
    }

    public async Task<IReadOnlyList<ChallengeRecord>> ListOpenForTargetAsync(Guid targetUserId, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Row>("select * from challenges where target_user_id=@targetUserId and status='Open' and expires_at > @now order by created_at",
            new { targetUserId, now = now.UtcDateTime })).Select(r => r.ToRecord()).ToList();
    }
}

/// <summary>Postgres bot keys.</summary>
public sealed class PgBotKeyRepository(PgDb db) : IBotKeyRepository
{
    private sealed class Row
    {
        public string key_id = ""; public Guid bot_user_id; public Guid owner_user_id; public string bot_name = ""; public byte[] secret_hash = [];
        public DateTime created_at; public DateTime? revoked_at;
        public BotKeyRecord ToRecord() => new(key_id, bot_user_id, owner_user_id, bot_name, secret_hash, PgDb.Dto(created_at), PgDb.Dto(revoked_at));
    }

    public async Task<BotKeyRecord?> FindAsync(string keyId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<Row>("select * from bot_keys where key_id=@keyId", new { keyId }))?.ToRecord();
    }

    public async Task CreateAsync(BotKeyRecord r, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("""
            insert into bot_keys (key_id, bot_user_id, owner_user_id, bot_name, secret_hash, created_at, revoked_at)
            values (@KeyId, @BotUserId, @OwnerUserId, @BotName, @SecretHash, @created, @revoked)
            on conflict (key_id) do update set secret_hash=excluded.secret_hash, revoked_at=excluded.revoked_at
            """, new { r.KeyId, r.BotUserId, r.OwnerUserId, r.BotName, r.SecretHash, created = r.CreatedAt.UtcDateTime, revoked = r.RevokedAt?.UtcDateTime });
    }

    public async Task<bool> RevokeAsync(string keyId, Guid ownerUserId, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteAsync("update bot_keys set revoked_at=@at where key_id=@keyId and owner_user_id=@ownerUserId and revoked_at is null",
            new { keyId, ownerUserId, at = at.UtcDateTime }) == 1;
    }

    public async Task<IReadOnlyList<BotKeyRecord>> ListForOwnerAsync(Guid ownerUserId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Row>("select * from bot_keys where owner_user_id=@ownerUserId order by created_at", new { ownerUserId })).Select(r => r.ToRecord()).ToList();
    }
}

/// <summary>Postgres reward ledger.</summary>
public sealed class PgRewardLedger(PgDb db) : IRewardLedger
{
    public async Task<bool> TryGrantAsync(RewardGrantRecord g, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteAsync("insert into reward_grants (nonce, user_id, kind, game_id, granted_at) values (@Nonce, @UserId, @kind, @GameId, @at) on conflict do nothing",
            new { g.Nonce, g.UserId, kind = g.Kind.ToString(), g.GameId, at = g.GrantedAt.UtcDateTime }) == 1;
    }

    public async Task<bool> HasGrantAsync(Guid userId, RewardKind kind, Guid? gameId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>("select exists(select 1 from reward_grants where user_id=@userId and kind=@k and game_id is not distinct from @gameId::uuid)",
            new { userId, k = kind.ToString(), gameId });
    }

    public async Task<int> CountSinceAsync(Guid userId, RewardKind kind, DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>("select count(*) from reward_grants where user_id=@userId and kind=@k and granted_at >= @since",
            new { userId, k = kind.ToString(), since = since.UtcDateTime });
    }
}

/// <summary>Silences an unused-using analyzer in some configurations.</summary>
internal static class PgTypes { internal static Type Conn => typeof(NpgsqlConnection); }
