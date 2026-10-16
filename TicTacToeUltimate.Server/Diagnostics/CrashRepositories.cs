using Dapper;
using TicTacToeUltimate.Server.Persistence.Postgres;

namespace TicTacToeUltimate.Server.Diagnostics;

public sealed record CrashGroupRecord(string Fingerprint, string Kind, string Title, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Count,
    string Status /* open | ignored | fixed */, string? GhIssueUrl, string[] Platforms);

public sealed record CrashReportRecord(long Id, string Fingerprint, string Kind, Guid? UserId, string? Username, string Platform, string AppVersion,
    string? Os, string? Device, string? Browser, string? Locale, string? Screen, string? Route, string? Component,
    string? ExceptionType, string? Message, string? Stack, string InnerJson, string LogsJson, string TrailJson, string? Description,
    string? ScreenshotPath, DateTimeOffset At);

public interface ICrashRepository
{
    /// <summary>Creates the group or bumps its count/last seen; a fixed group that happens again reopens.</summary>
    Task<CrashGroupRecord> UpsertGroupAsync(string fingerprint, string kind, string title, string platform, DateTimeOffset at, CancellationToken ct);
    Task<long> AddReportAsync(CrashReportRecord report, CancellationToken ct);
    Task<CrashGroupRecord?> GetGroupAsync(string fingerprint, CancellationToken ct);
    Task<IReadOnlyList<CrashGroupRecord>> ListGroupsAsync(string? kind, string? status, int take, CancellationToken ct);
    Task<IReadOnlyList<CrashReportRecord>> ListReportsAsync(string fingerprint, int take, CancellationToken ct);
    Task<CrashReportRecord?> GetReportAsync(long id, CancellationToken ct);
    Task SetStatusAsync(string fingerprint, string status, CancellationToken ct);
    Task SetIssueAsync(string fingerprint, string url, CancellationToken ct);
    /// <summary>Distinct players (or devices for anonymous reports: counted once) hitting the group since a time.</summary>
    Task<int> DistinctUsersSinceAsync(string fingerprint, DateTimeOffset since, CancellationToken ct);
    /// <summary>Screenshot paths older than a time (to delete), cleared on the rows.</summary>
    Task<IReadOnlyList<string>> TakeScreenshotsBeforeAsync(DateTimeOffset before, CancellationToken ct);
    Task<int> CountGroupsSinceAsync(DateTimeOffset since, CancellationToken ct);
}

public sealed class InMemoryCrashRepository : ICrashRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CrashGroupRecord> _groups = [];
    private readonly List<CrashReportRecord> _reports = [];
    private long _id;

    public Task<CrashGroupRecord> UpsertGroupAsync(string fingerprint, string kind, string title, string platform, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            var g = _groups.TryGetValue(fingerprint, out var x)
                ? x with { LastSeen = at, Count = x.Count + 1, Status = x.Status == "fixed" ? "open" : x.Status, Platforms = x.Platforms.Contains(platform) ? x.Platforms : [.. x.Platforms, platform] }
                : new CrashGroupRecord(fingerprint, kind, title, at, at, 1, "open", null, [platform]);
            _groups[fingerprint] = g;
            return Task.FromResult(g);
        }
    }

    public Task<long> AddReportAsync(CrashReportRecord r, CancellationToken ct) { lock (_gate) { var rec = r with { Id = ++_id }; _reports.Add(rec); return Task.FromResult(rec.Id); } }
    public Task<CrashGroupRecord?> GetGroupAsync(string fingerprint, CancellationToken ct) { lock (_gate) return Task.FromResult(_groups.GetValueOrDefault(fingerprint)); }

    public Task<IReadOnlyList<CrashGroupRecord>> ListGroupsAsync(string? kind, string? status, int take, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<CrashGroupRecord>>(_groups.Values.Where(g => (kind is null || g.Kind == kind) && (status is null || g.Status == status))
            .OrderByDescending(g => g.LastSeen).Take(take).ToList());
    }

    public Task<IReadOnlyList<CrashReportRecord>> ListReportsAsync(string fingerprint, int take, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<CrashReportRecord>>(_reports.Where(r => r.Fingerprint == fingerprint).OrderByDescending(r => r.Id).Take(take).ToList());
    }

    public Task<CrashReportRecord?> GetReportAsync(long id, CancellationToken ct) { lock (_gate) return Task.FromResult(_reports.FirstOrDefault(r => r.Id == id)); }
    public Task SetStatusAsync(string fingerprint, string status, CancellationToken ct) { lock (_gate) if (_groups.TryGetValue(fingerprint, out var g)) _groups[fingerprint] = g with { Status = status }; return Task.CompletedTask; }
    public Task SetIssueAsync(string fingerprint, string url, CancellationToken ct) { lock (_gate) if (_groups.TryGetValue(fingerprint, out var g)) _groups[fingerprint] = g with { GhIssueUrl = url }; return Task.CompletedTask; }

    public Task<int> DistinctUsersSinceAsync(string fingerprint, DateTimeOffset since, CancellationToken ct)
    {
        lock (_gate)
        {
            var rows = _reports.Where(r => r.Fingerprint == fingerprint && r.At >= since).ToList();
            return Task.FromResult(rows.Where(r => r.UserId is not null).Select(r => r.UserId).Distinct().Count() + rows.Count(r => r.UserId is null));
        }
    }

    public Task<IReadOnlyList<string>> TakeScreenshotsBeforeAsync(DateTimeOffset before, CancellationToken ct)
    {
        lock (_gate)
        {
            var paths = new List<string>();
            for (var i = 0; i < _reports.Count; i++)
                if (_reports[i] is { ScreenshotPath: { } p } r && r.At < before) { paths.Add(p); _reports[i] = r with { ScreenshotPath = null }; }
            return Task.FromResult<IReadOnlyList<string>>(paths);
        }
    }

    public Task<int> CountGroupsSinceAsync(DateTimeOffset since, CancellationToken ct) { lock (_gate) return Task.FromResult(_groups.Values.Count(g => g.LastSeen >= since && g.Kind == "crash")); }
}

#pragma warning disable CS0649 // Dapper fills the row fields
public sealed class PgCrashRepository(PgDb db) : ICrashRepository
{
    private sealed class GroupRow
    {
        public string fingerprint = ""; public string kind = ""; public string title = ""; public DateTime first_seen; public DateTime last_seen; public int count;
        public string status = ""; public string? gh_issue_url; public string[] platforms = [];
        public CrashGroupRecord ToRecord() => new(fingerprint, kind, title, PgDb.Dto(first_seen), PgDb.Dto(last_seen), count, status, gh_issue_url, platforms);
    }
    private sealed class ReportRow
    {
        public long id; public string fingerprint = ""; public string kind = ""; public Guid? user_id; public string? username; public string platform = "";
        public string app_version = ""; public string? os; public string? device; public string? browser; public string? locale; public string? screen;
        public string? route; public string? component; public string? exception_type; public string? message; public string? stack;
        public string inner_exceptions = "[]"; public string logs = "[]"; public string trail = "[]"; public string? description; public string? screenshot_path; public DateTime at;
        public CrashReportRecord ToRecord() => new(id, fingerprint, kind, user_id, username, platform, app_version, os, device, browser, locale, screen, route,
            component, exception_type, message, stack, inner_exceptions, logs, trail, description, screenshot_path, PgDb.Dto(at));
    }

    private const string ReportCols = "id, fingerprint, kind, user_id, username, platform, app_version, os, device, browser, locale, screen, route, component, exception_type, message, stack, inner_exceptions::text as inner_exceptions, logs::text as logs, trail::text as trail, description, screenshot_path, at";

    public async Task<CrashGroupRecord> UpsertGroupAsync(string fingerprint, string kind, string title, string platform, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleAsync<GroupRow>("""
            insert into crash_groups (fingerprint, kind, title, first_seen, last_seen, count, platforms) values (@fingerprint, @kind, @title, @at, @at, 1, array[@platform])
            on conflict (fingerprint) do update set last_seen = excluded.last_seen, count = crash_groups.count + 1,
              status = case when crash_groups.status = 'fixed' then 'open' else crash_groups.status end,
              platforms = case when @platform = any(crash_groups.platforms) then crash_groups.platforms else crash_groups.platforms || @platform end
            returning *
            """, new { fingerprint, kind, title, platform, at = at.UtcDateTime })).ToRecord();
    }

    public async Task<long> AddReportAsync(CrashReportRecord r, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<long>("""
            insert into crash_reports (fingerprint, kind, user_id, username, platform, app_version, os, device, browser, locale, screen, route, component,
              exception_type, message, stack, inner_exceptions, logs, trail, description, screenshot_path, at)
            values (@Fingerprint, @Kind, @UserId, @Username, @Platform, @AppVersion, @Os, @Device, @Browser, @Locale, @Screen, @Route, @Component,
              @ExceptionType, @Message, @Stack, @InnerJson::jsonb, @LogsJson::jsonb, @TrailJson::jsonb, @Description, @ScreenshotPath, @at) returning id
            """, new { r.Fingerprint, r.Kind, r.UserId, r.Username, r.Platform, r.AppVersion, r.Os, r.Device, r.Browser, r.Locale, r.Screen, r.Route, r.Component,
                r.ExceptionType, r.Message, r.Stack, r.InnerJson, r.LogsJson, r.TrailJson, r.Description, r.ScreenshotPath, at = r.At.UtcDateTime });
    }

    public async Task<CrashGroupRecord?> GetGroupAsync(string fingerprint, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<GroupRow>("select * from crash_groups where fingerprint=@fingerprint", new { fingerprint }))?.ToRecord();
    }

    public async Task<IReadOnlyList<CrashGroupRecord>> ListGroupsAsync(string? kind, string? status, int take, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<GroupRow>("select * from crash_groups where (@kind::text is null or kind=@kind) and (@status::text is null or status=@status) order by last_seen desc limit @take",
            new { kind, status, take })).Select(r => r.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<CrashReportRecord>> ListReportsAsync(string fingerprint, int take, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<ReportRow>($"select {ReportCols} from crash_reports where fingerprint=@fingerprint order by id desc limit @take", new { fingerprint, take }))
            .Select(r => r.ToRecord()).ToList();
    }

    public async Task<CrashReportRecord?> GetReportAsync(long id, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<ReportRow>($"select {ReportCols} from crash_reports where id=@id", new { id }))?.ToRecord();
    }

    public async Task SetStatusAsync(string fingerprint, string status, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("update crash_groups set status=@status where fingerprint=@fingerprint", new { fingerprint, status });
    }

    public async Task SetIssueAsync(string fingerprint, string url, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("update crash_groups set gh_issue_url=@url where fingerprint=@fingerprint", new { fingerprint, url });
    }

    public async Task<int> DistinctUsersSinceAsync(string fingerprint, DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>("""
            select count(distinct user_id) + count(*) filter (where user_id is null) from crash_reports where fingerprint=@fingerprint and at >= @since
            """, new { fingerprint, since = since.UtcDateTime });
    }

    public async Task<IReadOnlyList<string>> TakeScreenshotsBeforeAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<string>("""
            with old as (select id, screenshot_path from crash_reports where screenshot_path is not null and at < @before)
            update crash_reports r set screenshot_path = null from old where r.id = old.id returning old.screenshot_path
            """, new { before = before.UtcDateTime })).ToList();
    }

    public async Task<int> CountGroupsSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>("select count(*) from crash_groups where kind='crash' and last_seen >= @since", new { since = since.UtcDateTime });
    }
}
