using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Persistence.Postgres;

/// <summary>Shared Npgsql data source and helpers for the Postgres repositories (§9).</summary>
public sealed class PgDb : IAsyncDisposable
{
    /// <summary>Opens connections.</summary>
    public NpgsqlDataSource Source { get; }

    public PgDb(string connectionString) => Source = NpgsqlDataSource.Create(connectionString);
    public PgDb(IOptions<TttOptions> options) : this(options.Value.Persistence.Postgres.ConnectionString) { }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    internal static DateTimeOffset Dto(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
    internal static DateTimeOffset? Dto(DateTime? d) => d is { } x ? Dto(x) : null;
    internal static DateTime Utc(DateTimeOffset d) => d.UtcDateTime;
    internal static DateTime? Utc(DateTimeOffset? d) => d?.UtcDateTime;
    internal static string Kind(AccountKind k) => k.ToString().ToLowerInvariant();
    internal static AccountKind Kind(string k) => Enum.Parse<AccountKind>(k, ignoreCase: true);

    public ValueTask DisposeAsync() => Source.DisposeAsync();
}

internal sealed class PlayerRow
{
    public Guid id; public string kind = ""; public string? username; public string display_name = ""; public string avatar_id = "";
    public string? country; public string time_zone = "UTC"; public short renames_left; public DateTime? tos_accepted_at;
    public string? device_id; public DateTime created_at; public DateTime last_seen_at;

    public UserRecord ToRecord() => new(id, PgDb.Kind(kind), username, display_name, avatar_id, country?.Trim(), time_zone, renames_left,
        PgDb.Dto(tos_accepted_at), device_id, PgDb.Dto(created_at), PgDb.Dto(last_seen_at));
}

/// <summary>Postgres users (table players).</summary>
public sealed class PgUserRepository(PgDb db) : IUserRepository
{
    private const string Cols = "id, kind, username::text as username, display_name, avatar_id, country, time_zone, renames_left, tos_accepted_at, device_id, created_at, last_seen_at";

    public async Task<UserRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<PlayerRow>($"select {Cols} from players where id = @id", new { id }))?.ToRecord();
    }

    public async Task<UserRecord?> GetByUsernameAsync(string username, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<PlayerRow>($"select {Cols} from players where username = @u::citext", new { u = username.ToLowerInvariant() }))?.ToRecord();
    }

    public async Task<UserRecord?> GetByDeviceIdAsync(string deviceId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<PlayerRow>($"select {Cols} from players where device_id = @deviceId", new { deviceId }))?.ToRecord();
    }

    public async Task<IReadOnlyDictionary<Guid, UserRecord>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<Guid, UserRecord>();
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<PlayerRow>($"select {Cols} from players where id = any(@ids)", new { ids = ids.ToArray() });
        return rows.Select(r => r.ToRecord()).ToDictionary(r => r.Id);
    }

    public async Task<bool> CreateAsync(UserRecord u, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var n = await c.ExecuteAsync("""
            insert into players (id, kind, username, display_name, avatar_id, country, time_zone, renames_left, tos_accepted_at, device_id, created_at, last_seen_at)
            values (@Id, @kind, @username, @DisplayName, @AvatarId, @Country, @TimeZone, @RenamesLeft, @tos, @DeviceId, @created, @seen)
            on conflict do nothing
            """, new
        {
            u.Id, kind = PgDb.Kind(u.Kind), username = u.Username?.ToLowerInvariant(), u.DisplayName, u.AvatarId, u.Country, u.TimeZone,
            RenamesLeft = (short)u.RenamesLeft, tos = PgDb.Utc(u.TosAcceptedAt), u.DeviceId, created = PgDb.Utc(u.CreatedAt), seen = PgDb.Utc(u.LastSeenAt),
        });
        return n == 1;
    }

    public async Task<UsernameResult> SetUsernameAsync(Guid id, string username, DateTimeOffset? tosAcceptedAt, CancellationToken ct)
    {
        var key = username.ToLowerInvariant();
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var u = await c.QuerySingleOrDefaultAsync<PlayerRow>($"select {Cols} from players where id = @id for update", new { id }, tx);
        if (u is null) return UsernameResult.Invalid;
        if (u.username == key) return UsernameResult.Ok;
        var first = u.username is null;
        if (!first && u.renames_left <= 0) return UsernameResult.NoRenamesLeft;
        try
        {
            await c.ExecuteAsync("""
                update players set username = @key, display_name = @key,
                  renames_left = renames_left - @dec, tos_accepted_at = coalesce(tos_accepted_at, @tos)
                where id = @id
                """, new { id, key, dec = (short)(first ? 0 : 1), tos = PgDb.Utc(tosAcceptedAt) }, tx);
            await tx.CommitAsync(ct);
            return UsernameResult.Ok;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return UsernameResult.Taken; }
    }

    public async Task AddRenamesAsync(Guid id, int count, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("update players set renames_left = renames_left + @count where id = @id", new { id, count = (short)count });
    }

    public async Task UpdateProfileAsync(Guid id, ProfilePatch p, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("update players set avatar_id = coalesce(@AvatarId, avatar_id), country = coalesce(@Country, country), time_zone = coalesce(@TimeZone, time_zone) where id = @id",
            new { id, p.AvatarId, p.Country, p.TimeZone });
    }

    public async Task TouchAsync(Guid id, DateTimeOffset lastSeenAt, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("update players set last_seen_at = @t where id = @id", new { id, t = PgDb.Utc(lastSeenAt) });
    }

    public async Task<IReadOnlyList<UserRecord>> SearchAsync(string prefix, int take, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var like = prefix.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        return (await c.QueryAsync<PlayerRow>($"select {Cols} from players where username::text like @like order by username limit @take", new { like, take }))
            .Select(r => r.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<UserRecord>> ListAsync(int take, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<PlayerRow>($"select {Cols} from players order by created_at, id limit @take", new { take })).Select(r => r.ToRecord()).ToList();
    }
}

internal sealed class RatingRow
{
    public Guid user_id; public string pool = ""; public double rating; public double rd; public double volatility; public int games;
    public int wins; public int losses; public int draws; public double peak; public DateTime? last_game_at;
    public RatingRecord ToRecord() => new(user_id, Enum.Parse<RatingPool>(pool), rating, rd, volatility, games, wins, losses, draws, peak, PgDb.Dto(last_game_at));
}

/// <summary>Postgres ratings and leaderboards.</summary>
public sealed class PgRatingRepository(PgDb db) : IRatingRepository
{
    public async Task<RatingRecord> GetAsync(Guid userId, RatingPool pool, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<RatingRow>("select * from ratings where user_id = @userId and pool = @p", new { userId, p = pool.ToString() });
        return r?.ToRecord() ?? RatingRecord.Initial(userId, pool);
    }

    public async Task<IReadOnlyList<RatingRecord>> GetAllAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<RatingRow>("select * from ratings where user_id = @userId", new { userId })).Select(r => r.ToRecord()).OrderBy(r => r.Pool).ToList();
    }

    public async Task PutAsync(RatingRecord r, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("""
            insert into ratings (user_id, pool, rating, rd, volatility, games, wins, losses, draws, peak, last_game_at)
            values (@UserId, @pool, @Rating, @Rd, @Volatility, @Games, @Wins, @Losses, @Draws, @Peak, @last)
            on conflict (user_id, pool) do update set rating = excluded.rating, rd = excluded.rd, volatility = excluded.volatility,
              games = excluded.games, wins = excluded.wins, losses = excluded.losses, draws = excluded.draws, peak = excluded.peak, last_game_at = excluded.last_game_at
            """, new { r.UserId, pool = r.Pool.ToString(), r.Rating, r.Rd, r.Volatility, r.Games, r.Wins, r.Losses, r.Draws, r.Peak, last = PgDb.Utc(r.LastGameAt) });
    }

    private sealed class EntryRow { public long rank; public Guid user_id; public double rating; public int games; public long total; }

    public async Task<LeaderboardPage> QueryAsync(LeaderboardQuery q, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        const string ranked = """
            with b as (
              select r.user_id, r.rating, r.games, row_number() over (order by r.rating desc, r.user_id) as rank, count(*) over () as total
              from ratings r join players p on p.id = r.user_id
              where r.pool = @pool and r.games >= @minGames and (@minRating::float8 is null or r.rating >= @minRating::float8)
                and (@country::text is null or upper(p.country) = upper(@country::text))
                and (@restrict::uuid[] is null or r.user_id = any(@restrict::uuid[])))
            """;
        var args = new
        {
            pool = q.Pool.ToString(), minGames = q.MinGames, minRating = q.MinRating, country = q.Country,
            restrict = q.RestrictTo?.ToArray(), skip = (Math.Max(1, q.Page) - 1) * q.PageSize, take = q.PageSize, viewer = q.Viewer,
        };
        var rows = (await c.QueryAsync<EntryRow>(ranked + " select * from b order by rank offset @skip limit @take", args)).ToList();
        var me = await c.QuerySingleOrDefaultAsync<EntryRow>(ranked + " select * from b where user_id = @viewer", args);
        var total = rows.Count > 0 ? (int)rows[0].total : (int)(await c.ExecuteScalarAsync<long?>(ranked + " select count(*) from b", args) ?? 0);
        static LeaderboardEntry E(EntryRow r) => new((int)r.rank, r.user_id, r.rating, r.games);
        return new LeaderboardPage(rows.Select(E).ToList(), total, me is null ? null : E(me));
    }
}

internal sealed class GameRow
{
    public Guid id; public string mode = ""; public string time_control = ""; public bool rated; public string rules_id = ""; public long seed;
    public Guid seat_a; public Guid seat_b; public bool swapped; public string origin = ""; public string outcome = ""; public string end_reason = "";
    public short? winner_seat; public short[] win_line = []; public string? detail; public DateTime started_at; public DateTime ended_at;
    public string rating_snapshots = "[]"; public string setup_extra = "{}"; public int plies;
}

internal sealed class ActionRow
{
    public Guid game_id; public int ply; public short seat; public string kind = ""; public short? cell; public int think_ms; public bool auto;
    public short? vanished_cell; public DateTime at;
    public StoredAction ToStored() => new(ply, seat, new GameAction(Enum.Parse<ActionKind>(kind), cell ?? -1), think_ms, auto, vanished_cell, PgDb.Dto(at));
}

/// <summary>Seat names live in the jsonb column "rules" alongside the ruleset so the schema stays as in §9.</summary>
internal sealed record GameRulesBlob(RuleSet Rules, SeatSetup SeatA, SeatSetup SeatB, Guid? RematchOf, string? ChallengeCode);

/// <summary>Postgres finished games (games + game_actions).</summary>
public sealed class PgGameRepository(PgDb db) : IGameRepository
{
    internal static async Task InsertActionsAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid gameId, IEnumerable<StoredAction> actions)
    {
        foreach (var a in actions)
            await c.ExecuteAsync("""
                insert into game_actions (game_id, ply, seat, kind, cell, think_ms, auto, vanished_cell, at)
                values (@gameId, @Ply, @seat, @kind, @cell, @ThinkMs, @Auto, @vanished, @at) on conflict do nothing
                """, new
            {
                gameId, a.Ply, seat = (short)a.Seat, kind = a.Action.Kind.ToString(), cell = a.Action.Kind == ActionKind.Place ? (short?)a.Action.Cell : null,
                a.ThinkMs, a.Auto, vanished = (short?)a.VanishedCell, at = PgDb.Utc(a.At),
            }, tx);
    }

    public async Task<bool> SaveCompletedAsync(GameRecord g, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var s = g.Setup;
        var blob = JsonSerializer.Serialize(new GameRulesBlob(s.Rules, s.SeatA, s.SeatB, s.RematchOf, s.ChallengeCode), PgDb.Json);
        var n = await c.ExecuteAsync("""
            insert into games (id, mode, time_control, rated, rules_id, rules, seed, seat_a, seat_b, swapped, origin, outcome, end_reason,
              winner_seat, win_line, detail, started_at, ended_at, rating_snapshots)
            values (@Id, @mode, @tc, @rated, @rulesId, @blob::jsonb, @seed, @a, @b, @Swapped, @origin, @outcome, @reason,
              @winner, @line, @detail, @started, @ended, @snaps::jsonb)
            on conflict (id) do nothing
            """, new
        {
            g.Id, mode = s.Mode.ToString(), tc = s.TimeControl.ToString(), rated = s.Rated, rulesId = s.Rules.Id, blob, seed = unchecked((long)s.Seed),
            a = s.SeatA.UserId, b = s.SeatB.UserId, g.Swapped, origin = s.Origin.ToString(), outcome = g.Result.Outcome.ToString(),
            reason = g.Result.Reason.ToString(), winner = (short?)g.Result.WinnerSeat, line = g.Result.WinLine.Select(x => (short)x).ToArray(),
            detail = g.Result.Detail, started = PgDb.Utc(g.StartedAt), ended = PgDb.Utc(g.EndedAt), snaps = JsonSerializer.Serialize(g.Ratings, PgDb.Json),
        }, tx);
        if (n == 0) { await tx.RollbackAsync(ct); return false; }
        await InsertActionsAsync(c, tx, g.Id, g.Actions);
        await tx.CommitAsync(ct);
        return true;
    }

    private static (MatchSetup Setup, MatchResultDto Result, IReadOnlyList<RatingSnapshot> Ratings) Map(GameRow r)
    {
        var blob = JsonSerializer.Deserialize<GameRulesBlob>(r.setup_extra, PgDb.Json)!;
        var setup = new MatchSetup(r.id, Enum.Parse<MatchMode>(r.mode), Enum.Parse<TimeControl>(r.time_control), blob.Rules, r.rated,
            blob.SeatA, blob.SeatB, unchecked((ulong)r.seed), Enum.Parse<MatchOrigin>(r.origin), blob.RematchOf, blob.ChallengeCode);
        var result = new MatchResultDto(Enum.Parse<Outcome>(r.outcome), r.winner_seat, Enum.Parse<EndReason>(r.end_reason),
            r.win_line.Select(x => (int)x).ToArray(), r.rated, r.detail);
        var snaps = JsonSerializer.Deserialize<List<RatingSnapshot>>(r.rating_snapshots, PgDb.Json) ?? [];
        return (setup, result, snaps);
    }

    private const string Cols = "g.id, g.mode, g.time_control, g.rated, g.rules_id, g.rules::text as setup_extra, g.seed, g.seat_a, g.seat_b, g.swapped, g.origin, g.outcome, g.end_reason, g.winner_seat, g.win_line, g.detail, g.started_at, g.ended_at, g.rating_snapshots::text as rating_snapshots";

    public async Task<GameRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<GameRow>($"select {Cols} from games g where g.id = @id", new { id });
        if (r is null) return null;
        var actions = (await c.QueryAsync<ActionRow>("select * from game_actions where game_id = @id order by ply", new { id })).Select(a => a.ToStored()).ToList();
        var (setup, result, snaps) = Map(r);
        return new GameRecord(r.id, setup, r.swapped, actions, result, PgDb.Dto(r.started_at), PgDb.Dto(r.ended_at), snaps);
    }

    public async Task<IReadOnlyList<GameListItem>> ListForUserAsync(Guid userId, DateTimeOffset? before, int take, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<GameRow>($"""
            select {Cols}, (select count(*)::int from game_actions a where a.game_id = g.id) as plies from games g
            where (g.seat_a = @userId or g.seat_b = @userId) and (@before::timestamptz is null or g.ended_at < @before::timestamptz)
            order by g.ended_at desc, g.id desc limit @take
            """, new { userId, before = PgDb.Utc(before), take });
        return rows.Select(r => { var (s, res, sn) = Map(r); return new GameListItem(r.id, s, r.swapped, res, r.plies, PgDb.Dto(r.ended_at), sn); }).ToList();
    }

    public async Task<int> CountForUserAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>("select count(*)::int from games where seat_a = @userId or seat_b = @userId", new { userId });
    }
}

/// <summary>Postgres live Daily state (daily_matches + game_actions).</summary>
public sealed class PgDailyMatchRepository(PgDb db) : IDailyMatchRepository
{
    private sealed class Row { public Guid id; public string setup = ""; public long version; public DateTime? turn_deadline; public bool ended; public DateTime started_at; }

    public async Task CreateAsync(MatchSetup setup, DateTimeOffset startedAt, DateTimeOffset firstDeadline, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("""
            insert into daily_matches (id, setup, version, turn_deadline, ended, started_at, seat_a, seat_b)
            values (@id, @setup::jsonb, 0, @deadline, false, @started, @a, @b) on conflict do nothing
            """, new
        {
            id = setup.MatchId, setup = JsonSerializer.Serialize(setup, PgDb.Json), deadline = PgDb.Utc(firstDeadline),
            started = PgDb.Utc(startedAt), a = setup.SeatA.UserId, b = setup.SeatB.UserId,
        });
    }

    public async Task<long> AppendActionAsync(Guid matchId, long expectedVersion, StoredAction action, DateTimeOffset? nextDeadline, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var n = await c.ExecuteAsync("update daily_matches set version = version + 1, turn_deadline = @d where id = @matchId and version = @expectedVersion",
            new { matchId, expectedVersion, d = PgDb.Utc(nextDeadline) }, tx);
        if (n == 0) { await tx.RollbackAsync(ct); throw new ConcurrencyException(matchId); }
        await PgGameRepository.InsertActionsAsync(c, tx, matchId, [action]);
        await tx.CommitAsync(ct);
        return expectedVersion + 1;
    }

    public async Task MarkEndedAsync(Guid matchId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("update daily_matches set ended = true, turn_deadline = null where id = @matchId", new { matchId });
    }

    private async Task<StoredDailyMatch> MapAsync(NpgsqlConnection c, Row r)
    {
        var actions = (await c.QueryAsync<ActionRow>("select * from game_actions where game_id = @id order by ply", new { r.id })).Select(a => a.ToStored()).ToList();
        return new StoredDailyMatch(JsonSerializer.Deserialize<MatchSetup>(r.setup, PgDb.Json)!, actions, r.version, PgDb.Dto(r.turn_deadline), r.ended, PgDb.Dto(r.started_at));
    }

    public async Task<StoredDailyMatch?> LoadAsync(Guid matchId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<Row>("select id, setup::text as setup, version, turn_deadline, ended, started_at from daily_matches where id = @matchId", new { matchId });
        return r is null ? null : await MapAsync(c, r);
    }

    public async Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Guid>("select id from daily_matches where not ended and turn_deadline <= @now", new { now = PgDb.Utc(now) })).ToList();
    }

    public async Task<IReadOnlyList<StoredDailyMatch>> ListActiveForUserAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<Row>("select id, setup::text as setup, version, turn_deadline, ended, started_at from daily_matches where not ended and (seat_a = @userId or seat_b = @userId)", new { userId });
        var list = new List<StoredDailyMatch>();
        foreach (var r in rows) list.Add(await MapAsync(c, r));
        return list;
    }
}

/// <summary>Postgres seasons.</summary>
public sealed class PgSeasonRepository(PgDb db, IOptions<TttOptions> options) : ISeasonRepository
{
    private sealed class Row { public int id; public DateTime starts_at; public DateTime ends_at; }

    public async Task<SeasonRecord> GetCurrentAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<Row>("select * from seasons order by id desc limit 1");
        r ??= await c.QuerySingleAsync<Row>("insert into seasons (starts_at, ends_at) values (@s, @e) returning *",
            new { s = PgDb.Utc(now), e = PgDb.Utc(now.AddDays(Math.Max(1, options.Value.Seasons.LengthDays))) });
        return new SeasonRecord(r.id, PgDb.Dto(r.starts_at), PgDb.Dto(r.ends_at));
    }

    public async Task CloseAsync(SeasonRecord season, IReadOnlyList<MasterSeasonResult> results, SeasonRecord next, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        foreach (var r in results)
            await c.ExecuteAsync("insert into master_season_results (season_id, user_id, final_rating, rank) values (@SeasonId, @UserId, @FinalRating, @Rank) on conflict do nothing", r, tx);
        await c.ExecuteAsync("insert into seasons (starts_at, ends_at) values (@s, @e)", new { s = PgDb.Utc(next.StartsAt), e = PgDb.Utc(next.EndsAt) }, tx);
        await tx.CommitAsync(ct);
    }
}
