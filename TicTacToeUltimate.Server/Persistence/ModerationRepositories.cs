using Dapper;
using Npgsql;
using TicTacToeUltimate.Server.Persistence.Postgres;

namespace TicTacToeUltimate.Server.Persistence;

public enum PenaltyKind { Warn, ChatMute, Ban, UsernameReset, Shadow }

public static class PenaltyKinds
{
    public static string ToDb(PenaltyKind k) => k switch
    {
        PenaltyKind.ChatMute => "chat_mute", PenaltyKind.UsernameReset => "username_reset", _ => k.ToString().ToLowerInvariant(),
    };
    public static PenaltyKind FromDb(string s) => s switch
    {
        "chat_mute" => PenaltyKind.ChatMute, "username_reset" => PenaltyKind.UsernameReset, _ => Enum.Parse<PenaltyKind>(s, ignoreCase: true),
    };
}

/// <summary>A penalty. <see cref="EndsAt"/> null = permanent (ban, shadow) or instant (warn, username reset).</summary>
public sealed record PenaltyRecord(long Id, Guid UserId, PenaltyKind Kind, string Reason, DateTimeOffset StartsAt, DateTimeOffset? EndsAt,
    string Source /* auto | admin */, long? ReportId, string? CreatedBy, DateTimeOffset? RevokedAt)
{
    /// <summary>In force at <paramref name="now"/> (warn / username reset are instant and never "active").</summary>
    public bool ActiveAt(DateTimeOffset now) => RevokedAt is null && StartsAt <= now && Kind is PenaltyKind.ChatMute or PenaltyKind.Ban or PenaltyKind.Shadow
        && (EndsAt is null || EndsAt > now);
}

public sealed record ReportRecord(long Id, Guid Reporter, Guid Target, string Kind /* chat | profile | cheating */, string Reason, string? Note,
    long? MessageId, Guid? GameId, string EvidenceJson, string Status /* open | actioned | dismissed */, string? AiVerdictJson,
    DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, string? Resolution);

public sealed record ModerationEventRecord(long Id, Guid UserId, string Kind /* filter_block | ai_block | ai_hide | username_block | report_confirmed */,
    string? Text, string DetailJson, DateTimeOffset At);

public sealed record AppealRecord(long Id, long PenaltyId, Guid UserId, string Text, string Status /* open | approved | denied */,
    DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt, string? DecisionNote);

public sealed record AuditRecord(long Id, string AdminEmail, string Action, string? Target, string DetailJson, DateTimeOffset At);

/// <summary>Moderation storage: reports, penalties, strikes, appeals, devices, shadow pool, admin audit.</summary>
public interface IModerationRepository
{
    /// <summary>Adds a report; an open report with the same reporter/target/kind/message is returned instead (dedupe).</summary>
    Task<ReportRecord> AddReportAsync(ReportRecord report, CancellationToken ct);
    Task<ReportRecord?> GetReportAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<ReportRecord>> ListReportsAsync(string? status, Guid? target, int take, CancellationToken ct);
    Task<int> CountReportsAsync(string status, CancellationToken ct);
    Task UpdateReportAsync(long id, string status, string? resolution, string? aiVerdictJson, DateTimeOffset? resolvedAt, CancellationToken ct);

    Task<PenaltyRecord> AddPenaltyAsync(PenaltyRecord penalty, CancellationToken ct);
    Task<PenaltyRecord?> GetPenaltyAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<PenaltyRecord>> PenaltiesForAsync(Guid userId, int take, CancellationToken ct);
    Task<bool> RevokePenaltyAsync(long id, string by, DateTimeOffset at, CancellationToken ct);

    Task AddEventAsync(ModerationEventRecord e, CancellationToken ct);
    /// <summary>Auto-moderation strikes since a time (every event kind counts as one strike).</summary>
    Task<int> CountStrikesSinceAsync(Guid userId, DateTimeOffset since, CancellationToken ct);
    Task<IReadOnlyList<ModerationEventRecord>> ListEventsAsync(Guid? userId, int take, CancellationToken ct);

    /// <summary>Null when this penalty already has an appeal (one per penalty).</summary>
    Task<AppealRecord?> AddAppealAsync(AppealRecord appeal, CancellationToken ct);
    Task<AppealRecord?> GetAppealAsync(long id, CancellationToken ct);
    Task<AppealRecord?> AppealForPenaltyAsync(long penaltyId, CancellationToken ct);
    Task<IReadOnlyList<AppealRecord>> ListAppealsAsync(string? status, int take, CancellationToken ct);
    Task DecideAppealAsync(long id, string status, string? note, DateTimeOffset at, CancellationToken ct);

    Task TouchDeviceAsync(Guid userId, string deviceHash, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlyList<string>> DevicesForAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> UsersForDeviceAsync(string deviceHash, CancellationToken ct);
    Task AddDeviceBanAsync(string deviceHash, long penaltyId, CancellationToken ct);
    /// <summary>Penalty ids banning this device (active or not; the caller checks).</summary>
    Task<IReadOnlyList<long>> DeviceBanPenaltiesAsync(string deviceHash, CancellationToken ct);

    Task SetShadowAsync(Guid userId, bool on, CancellationToken ct);
    Task<IReadOnlyCollection<Guid>> ShadowUsersAsync(CancellationToken ct);

    Task AuditAsync(AuditRecord entry, CancellationToken ct);
    Task<IReadOnlyList<AuditRecord>> ListAuditAsync(int take, CancellationToken ct);

    /// <summary>Account deletion: drop personal moderation data (reports, events, appeals, devices). Penalties and device bans stay.</summary>
    Task PurgeUserAsync(Guid userId, CancellationToken ct);
}

/// <summary>Thread-safe in-memory moderation store.</summary>
public sealed class InMemoryModerationRepository : IModerationRepository
{
    private readonly Lock _gate = new();
    private readonly List<ReportRecord> _reports = [];
    private readonly List<PenaltyRecord> _penalties = [];
    private readonly List<ModerationEventRecord> _events = [];
    private readonly List<AppealRecord> _appeals = [];
    private readonly Dictionary<(Guid, string), DateTimeOffset> _devices = [];
    private readonly HashSet<(string, long)> _deviceBans = [];
    private readonly HashSet<Guid> _shadow = [];
    private readonly List<AuditRecord> _audit = [];
    private long _id;

    public Task<ReportRecord> AddReportAsync(ReportRecord r, CancellationToken ct)
    {
        lock (_gate)
        {
            var dup = _reports.FirstOrDefault(x => x.Status == "open" && x.Reporter == r.Reporter && x.Target == r.Target && x.Kind == r.Kind
                && (x.MessageId ?? 0) == (r.MessageId ?? 0));
            if (dup is not null) return Task.FromResult(dup);
            var rec = r with { Id = ++_id };
            _reports.Add(rec);
            return Task.FromResult(rec);
        }
    }

    public Task<ReportRecord?> GetReportAsync(long id, CancellationToken ct) { lock (_gate) return Task.FromResult(_reports.FirstOrDefault(r => r.Id == id)); }

    public Task<IReadOnlyList<ReportRecord>> ListReportsAsync(string? status, Guid? target, int take, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ReportRecord>>(_reports.Where(r => (status is null || r.Status == status) && (target is null || r.Target == target))
                .OrderByDescending(r => r.Id).Take(take).ToList());
    }

    public Task<int> CountReportsAsync(string status, CancellationToken ct) { lock (_gate) return Task.FromResult(_reports.Count(r => r.Status == status)); }

    public Task UpdateReportAsync(long id, string status, string? resolution, string? aiVerdictJson, DateTimeOffset? resolvedAt, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _reports.FindIndex(r => r.Id == id);
            if (i >= 0) _reports[i] = _reports[i] with { Status = status, Resolution = resolution ?? _reports[i].Resolution,
                AiVerdictJson = aiVerdictJson ?? _reports[i].AiVerdictJson, ResolvedAt = resolvedAt ?? _reports[i].ResolvedAt };
        }
        return Task.CompletedTask;
    }

    public Task<PenaltyRecord> AddPenaltyAsync(PenaltyRecord p, CancellationToken ct)
    {
        lock (_gate) { var rec = p with { Id = ++_id }; _penalties.Add(rec); return Task.FromResult(rec); }
    }

    public Task<PenaltyRecord?> GetPenaltyAsync(long id, CancellationToken ct) { lock (_gate) return Task.FromResult(_penalties.FirstOrDefault(p => p.Id == id)); }

    public Task<IReadOnlyList<PenaltyRecord>> PenaltiesForAsync(Guid userId, int take, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<PenaltyRecord>>(_penalties.Where(p => p.UserId == userId).OrderByDescending(p => p.Id).Take(take).ToList());
    }

    public Task<bool> RevokePenaltyAsync(long id, string by, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _penalties.FindIndex(p => p.Id == id && p.RevokedAt is null);
            if (i < 0) return Task.FromResult(false);
            _penalties[i] = _penalties[i] with { RevokedAt = at };
            return Task.FromResult(true);
        }
    }

    public Task AddEventAsync(ModerationEventRecord e, CancellationToken ct) { lock (_gate) _events.Add(e with { Id = ++_id }); return Task.CompletedTask; }

    public Task<int> CountStrikesSinceAsync(Guid userId, DateTimeOffset since, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_events.Count(e => e.UserId == userId && e.At >= since));
    }

    public Task<IReadOnlyList<ModerationEventRecord>> ListEventsAsync(Guid? userId, int take, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<ModerationEventRecord>>(_events.Where(e => userId is null || e.UserId == userId).OrderByDescending(e => e.Id).Take(take).ToList());
    }

    public Task<AppealRecord?> AddAppealAsync(AppealRecord a, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_appeals.Any(x => x.PenaltyId == a.PenaltyId)) return Task.FromResult<AppealRecord?>(null);
            var rec = a with { Id = ++_id };
            _appeals.Add(rec);
            return Task.FromResult<AppealRecord?>(rec);
        }
    }

    public Task<AppealRecord?> GetAppealAsync(long id, CancellationToken ct) { lock (_gate) return Task.FromResult(_appeals.FirstOrDefault(a => a.Id == id)); }
    public Task<AppealRecord?> AppealForPenaltyAsync(long penaltyId, CancellationToken ct) { lock (_gate) return Task.FromResult(_appeals.FirstOrDefault(a => a.PenaltyId == penaltyId)); }

    public Task<IReadOnlyList<AppealRecord>> ListAppealsAsync(string? status, int take, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<AppealRecord>>(_appeals.Where(a => status is null || a.Status == status).OrderByDescending(a => a.Id).Take(take).ToList());
    }

    public Task DecideAppealAsync(long id, string status, string? note, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _appeals.FindIndex(a => a.Id == id);
            if (i >= 0) _appeals[i] = _appeals[i] with { Status = status, DecisionNote = note, DecidedAt = at };
        }
        return Task.CompletedTask;
    }

    public Task TouchDeviceAsync(Guid userId, string deviceHash, DateTimeOffset at, CancellationToken ct) { lock (_gate) _devices[(userId, deviceHash)] = at; return Task.CompletedTask; }

    public Task<IReadOnlyList<string>> DevicesForAsync(Guid userId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<string>>(_devices.Keys.Where(k => k.Item1 == userId).Select(k => k.Item2).ToList());
    }

    public Task<IReadOnlyList<Guid>> UsersForDeviceAsync(string deviceHash, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<Guid>>(_devices.Keys.Where(k => k.Item2 == deviceHash).Select(k => k.Item1).Distinct().ToList());
    }

    public Task AddDeviceBanAsync(string deviceHash, long penaltyId, CancellationToken ct) { lock (_gate) _deviceBans.Add((deviceHash, penaltyId)); return Task.CompletedTask; }

    public Task<IReadOnlyList<long>> DeviceBanPenaltiesAsync(string deviceHash, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<long>>(_deviceBans.Where(b => b.Item1 == deviceHash).Select(b => b.Item2).ToList());
    }

    public Task SetShadowAsync(Guid userId, bool on, CancellationToken ct)
    {
        lock (_gate) { if (on) _shadow.Add(userId); else _shadow.Remove(userId); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<Guid>> ShadowUsersAsync(CancellationToken ct) { lock (_gate) return Task.FromResult<IReadOnlyCollection<Guid>>(_shadow.ToList()); }

    public Task AuditAsync(AuditRecord entry, CancellationToken ct) { lock (_gate) _audit.Add(entry with { Id = ++_id }); return Task.CompletedTask; }

    public Task<IReadOnlyList<AuditRecord>> ListAuditAsync(int take, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<AuditRecord>>(_audit.OrderByDescending(a => a.Id).Take(take).ToList());
    }

    public Task PurgeUserAsync(Guid userId, CancellationToken ct)
    {
        lock (_gate)
        {
            _reports.RemoveAll(r => r.Reporter == userId || r.Target == userId);
            _events.RemoveAll(e => e.UserId == userId);
            _appeals.RemoveAll(a => a.UserId == userId);
            foreach (var k in _devices.Keys.Where(k => k.Item1 == userId).ToList()) _devices.Remove(k);
            _shadow.Remove(userId);
        }
        return Task.CompletedTask;
    }
}

#pragma warning disable CS0649 // Dapper fills the row fields
/// <summary>Postgres moderation store (migration 0010).</summary>
public sealed class PgModerationRepository(PgDb db) : IModerationRepository
{
    private sealed class ReportRow
    {
        public long id; public Guid reporter; public Guid target; public string kind = ""; public string reason = ""; public string? note;
        public long? message_id; public Guid? game_id; public string evidence = "{}"; public string status = ""; public string? ai_verdict;
        public DateTime created_at; public DateTime? resolved_at; public string? resolution;
        public ReportRecord ToRecord() => new(id, reporter, target, kind, reason, note, message_id, game_id, evidence, status, ai_verdict,
            PgDb.Dto(created_at), PgDb.Dto(resolved_at), resolution);
    }
    private sealed class PenaltyRow
    {
        public long id; public Guid user_id; public string kind = ""; public string reason = ""; public DateTime starts_at; public DateTime? ends_at;
        public string source = ""; public long? report_id; public string? created_by; public DateTime? revoked_at;
        public PenaltyRecord ToRecord() => new(id, user_id, PenaltyKinds.FromDb(kind), reason, PgDb.Dto(starts_at), PgDb.Dto(ends_at), source, report_id,
            created_by, PgDb.Dto(revoked_at));
    }
    private sealed class EventRow
    {
        public long id; public Guid user_id; public string kind = ""; public string? text; public string detail = "{}"; public DateTime at;
        public ModerationEventRecord ToRecord() => new(id, user_id, kind, text, detail, PgDb.Dto(at));
    }
    private sealed class AppealRow
    {
        public long id; public long penalty_id; public Guid user_id; public string text = ""; public string status = ""; public DateTime created_at;
        public DateTime? decided_at; public string? decision_note;
        public AppealRecord ToRecord() => new(id, penalty_id, user_id, text, status, PgDb.Dto(created_at), PgDb.Dto(decided_at), decision_note);
    }
    private sealed class AuditRow
    {
        public long id; public string admin_email = ""; public string action = ""; public string? target; public string detail = "{}"; public DateTime at;
        public AuditRecord ToRecord() => new(id, admin_email, action, target, detail, PgDb.Dto(at));
    }

    private const string ReportCols = "id, reporter, target, kind, reason, note, message_id, game_id, evidence::text as evidence, status, ai_verdict::text as ai_verdict, created_at, resolved_at, resolution";
    private const string PenaltyCols = "id, user_id, kind, reason, starts_at, ends_at, source, report_id, created_by, revoked_at";

    public async Task<ReportRecord> AddReportAsync(ReportRecord r, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<ReportRow>($"""
            insert into reports (reporter, target, kind, reason, note, message_id, game_id, evidence, status, created_at)
            values (@Reporter, @Target, @Kind, @Reason, @Note, @MessageId, @GameId, @EvidenceJson::jsonb, 'open', @at)
            on conflict do nothing returning {ReportCols}
            """, new { r.Reporter, r.Target, r.Kind, r.Reason, r.Note, r.MessageId, r.GameId, r.EvidenceJson, at = r.CreatedAt.UtcDateTime });
        row ??= await c.QuerySingleAsync<ReportRow>($"""
            select {ReportCols} from reports where status='open' and reporter=@Reporter and target=@Target and kind=@Kind and coalesce(message_id,0)=coalesce(@MessageId,0)
            """, new { r.Reporter, r.Target, r.Kind, r.MessageId });
        return row.ToRecord();
    }

    public async Task<ReportRecord?> GetReportAsync(long id, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<ReportRow>($"select {ReportCols} from reports where id=@id", new { id }))?.ToRecord();
    }

    public async Task<IReadOnlyList<ReportRecord>> ListReportsAsync(string? status, Guid? target, int take, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<ReportRow>($"""
            select {ReportCols} from reports where (@status::text is null or status=@status) and (@target::uuid is null or target=@target)
            order by id desc limit @take
            """, new { status, target, take });
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<int> CountReportsAsync(string status, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>("select count(*) from reports where status=@status", new { status });
    }

    public async Task UpdateReportAsync(long id, string status, string? resolution, string? aiVerdictJson, DateTimeOffset? resolvedAt, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("""
            update reports set status=@status, resolution=coalesce(@resolution, resolution), ai_verdict=coalesce(@ai::jsonb, ai_verdict),
              resolved_at=coalesce(@resolvedAt, resolved_at) where id=@id
            """, new { id, status, resolution, ai = aiVerdictJson, resolvedAt = PgDb.Utc(resolvedAt) });
    }

    public async Task<PenaltyRecord> AddPenaltyAsync(PenaltyRecord p, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var row = await c.QuerySingleAsync<PenaltyRow>($"""
            insert into penalties (user_id, kind, reason, starts_at, ends_at, source, report_id, created_by)
            values (@UserId, @kind, @Reason, @starts, @ends, @Source, @ReportId, @CreatedBy) returning {PenaltyCols}
            """, new { p.UserId, kind = PenaltyKinds.ToDb(p.Kind), p.Reason, starts = p.StartsAt.UtcDateTime, ends = PgDb.Utc(p.EndsAt), p.Source, p.ReportId, p.CreatedBy });
        return row.ToRecord();
    }

    public async Task<PenaltyRecord?> GetPenaltyAsync(long id, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<PenaltyRow>($"select {PenaltyCols} from penalties where id=@id", new { id }))?.ToRecord();
    }

    public async Task<IReadOnlyList<PenaltyRecord>> PenaltiesForAsync(Guid userId, int take, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<PenaltyRow>($"select {PenaltyCols} from penalties where user_id=@userId order by id desc limit @take", new { userId, take });
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<bool> RevokePenaltyAsync(long id, string by, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteAsync("update penalties set revoked_at=@at, revoked_by=@by where id=@id and revoked_at is null", new { id, by, at = at.UtcDateTime }) > 0;
    }

    public async Task AddEventAsync(ModerationEventRecord e, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("insert into moderation_events (user_id, kind, text, detail, at) values (@UserId, @Kind, @Text, @DetailJson::jsonb, @at)",
            new { e.UserId, e.Kind, e.Text, e.DetailJson, at = e.At.UtcDateTime });
    }

    public async Task<int> CountStrikesSinceAsync(Guid userId, DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>("select count(*) from moderation_events where user_id=@userId and at >= @since", new { userId, since = since.UtcDateTime });
    }

    public async Task<IReadOnlyList<ModerationEventRecord>> ListEventsAsync(Guid? userId, int take, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<EventRow>("""
            select id, user_id, kind, text, detail::text as detail, at from moderation_events where (@userId::uuid is null or user_id=@userId)
            order by id desc limit @take
            """, new { userId, take });
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<AppealRecord?> AddAppealAsync(AppealRecord a, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<AppealRow>("""
            insert into appeals (penalty_id, user_id, text, status, created_at) values (@PenaltyId, @UserId, @Text, 'open', @at)
            on conflict (penalty_id) do nothing returning id, penalty_id, user_id, text, status, created_at, decided_at, decision_note
            """, new { a.PenaltyId, a.UserId, a.Text, at = a.CreatedAt.UtcDateTime });
        return row?.ToRecord();
    }

    private const string AppealCols = "id, penalty_id, user_id, text, status, created_at, decided_at, decision_note";

    public async Task<AppealRecord?> GetAppealAsync(long id, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<AppealRow>($"select {AppealCols} from appeals where id=@id", new { id }))?.ToRecord();
    }

    public async Task<AppealRecord?> AppealForPenaltyAsync(long penaltyId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<AppealRow>($"select {AppealCols} from appeals where penalty_id=@penaltyId", new { penaltyId }))?.ToRecord();
    }

    public async Task<IReadOnlyList<AppealRecord>> ListAppealsAsync(string? status, int take, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<AppealRow>($"select {AppealCols} from appeals where (@status::text is null or status=@status) order by id desc limit @take",
            new { status, take });
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task DecideAppealAsync(long id, string status, string? note, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("update appeals set status=@status, decision_note=@note, decided_at=@at where id=@id", new { id, status, note, at = at.UtcDateTime });
    }

    public async Task TouchDeviceAsync(Guid userId, string deviceHash, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("""
            insert into player_devices (user_id, device_hash, first_seen, last_seen) values (@userId, @deviceHash, @at, @at)
            on conflict (user_id, device_hash) do update set last_seen = excluded.last_seen
            """, new { userId, deviceHash, at = at.UtcDateTime });
    }

    public async Task<IReadOnlyList<string>> DevicesForAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<string>("select device_hash from player_devices where user_id=@userId", new { userId })).ToList();
    }

    public async Task<IReadOnlyList<Guid>> UsersForDeviceAsync(string deviceHash, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Guid>("select distinct user_id from player_devices where device_hash=@deviceHash", new { deviceHash })).ToList();
    }

    public async Task AddDeviceBanAsync(string deviceHash, long penaltyId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("insert into device_bans (device_hash, penalty_id) values (@deviceHash, @penaltyId) on conflict do nothing", new { deviceHash, penaltyId });
    }

    public async Task<IReadOnlyList<long>> DeviceBanPenaltiesAsync(string deviceHash, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<long>("select penalty_id from device_bans where device_hash=@deviceHash", new { deviceHash })).ToList();
    }

    public async Task SetShadowAsync(Guid userId, bool on, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("update players set shadow_pool=@on where id=@userId", new { userId, on });
    }

    public async Task<IReadOnlyCollection<Guid>> ShadowUsersAsync(CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Guid>("select id from players where shadow_pool")).ToList();
    }

    public async Task AuditAsync(AuditRecord e, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("insert into admin_audit (admin_email, action, target, detail, at) values (@AdminEmail, @Action, @Target, @DetailJson::jsonb, @at)",
            new { e.AdminEmail, e.Action, e.Target, e.DetailJson, at = e.At.UtcDateTime });
    }

    public async Task<IReadOnlyList<AuditRecord>> ListAuditAsync(int take, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<AuditRow>("select id, admin_email, action, target, detail::text as detail, at from admin_audit order by id desc limit @take", new { take });
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task PurgeUserAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("""
            delete from reports where reporter=@userId or target=@userId;
            delete from moderation_events where user_id=@userId;
            delete from appeals where user_id=@userId;
            delete from player_devices where user_id=@userId;
            """, new { userId });
    }
}
