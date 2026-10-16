using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using TicTacToeUltimate.Server.Persistence.Postgres;

namespace TicTacToeUltimate.Server.Analytics;

public sealed record EventRecord(DateTimeOffset At, Guid? UserId, string? DeviceHash, string? SessionId, string? Platform, string? AppVersion,
    string? Locale, string? Country, string? Campaign, string Kind, string Name, string PropsJson);

public sealed record StatRow(DateOnly Day, string Metric, string Dim, long Value);

public sealed record CampaignRecord(string Slug, string Name, string Channel, string? IosUrl, string? AndroidUrl, string? WebUrl, DateTimeOffset CreatedAt, bool Archived);

public interface IAnalyticsRepository
{
    Task AddEventsAsync(IReadOnlyList<EventRecord> events, CancellationToken ct);
    /// <summary>Recomputes every metric for one UTC day (idempotent).</summary>
    Task RollupAsync(DateOnly day, CancellationToken ct);
    Task<int> PurgeEventsBeforeAsync(DateTimeOffset before, CancellationToken ct);
    Task<IReadOnlyList<StatRow>> StatsAsync(string metric, DateOnly from, DateOnly to, CancellationToken ct);
    /// <summary>Attribution: the first campaign a player came from (kept once set).</summary>
    Task AttributeAsync(Guid userId, string campaign, CancellationToken ct);

    Task<IReadOnlyList<CampaignRecord>> ListCampaignsAsync(CancellationToken ct);
    Task<CampaignRecord?> GetCampaignAsync(string slug, CancellationToken ct);
    Task<bool> AddCampaignAsync(CampaignRecord c, CancellationToken ct);
    Task SetArchivedAsync(string slug, bool archived, CancellationToken ct);
    Task AddClickAsync(string slug, DateTimeOffset at, string platform, string? country, CancellationToken ct);
}

/// <summary>Dev store: events in memory; rollups cover the event-based metrics (players and games need Postgres).</summary>
public sealed class InMemoryAnalyticsRepository : IAnalyticsRepository
{
    private readonly Lock _gate = new();
    private readonly List<EventRecord> _events = [];
    private readonly ConcurrentDictionary<(DateOnly, string, string), long> _stats = new();
    private readonly Dictionary<string, CampaignRecord> _campaigns = [];
    private readonly List<(string Slug, DateTimeOffset At, string Platform, string? Country)> _clicks = [];
    private readonly Dictionary<Guid, string> _attribution = [];

    public Task AddEventsAsync(IReadOnlyList<EventRecord> events, CancellationToken ct) { lock (_gate) _events.AddRange(events); return Task.CompletedTask; }

    public Task RollupAsync(DateOnly day, CancellationToken ct)
    {
        List<EventRecord> evs; List<(string Slug, DateTimeOffset At, string Platform, string? Country)> clicks;
        lock (_gate)
        {
            evs = _events.Where(e => DateOnly.FromDateTime(e.At.UtcDateTime) == day).ToList();
            clicks = _clicks.Where(c => DateOnly.FromDateTime(c.At.UtcDateTime) == day).ToList();
        }
        foreach (var k in _stats.Keys.Where(k => k.Item1 == day).ToList()) _stats.TryRemove(k, out _);
        static string Who(EventRecord e) => e.UserId?.ToString() ?? e.DeviceHash ?? e.SessionId ?? "";
        void Put(string metric, string dim, long v) => _stats[(day, metric, dim)] = v;
        Put("dau", "", evs.Select(Who).Distinct().Count());
        Put("sessions", "", evs.Count(e => e.Kind == "session" && e.Name == "start"));
        foreach (var g in evs.GroupBy(e => e.Platform ?? "?")) Put("dau_platform", g.Key, g.Select(Who).Distinct().Count());
        foreach (var g in evs.GroupBy(e => e.Locale ?? "?")) Put("dau_locale", g.Key, g.Select(Who).Distinct().Count());
        foreach (var g in evs.GroupBy(e => e.Country ?? "?")) Put("dau_country", g.Key, g.Select(Who).Distinct().Count());
        foreach (var g in evs.Where(e => e.Kind == "page_view").GroupBy(e => e.Name)) Put("page_views", g.Key, g.Count());
        foreach (var g in evs.Where(e => e.Kind == "ad").GroupBy(e => $"{e.Name}|{e.Platform}")) Put("ads", g.Key, g.Count());
        foreach (var g in evs.Where(e => e.Kind == "match" && e.Name == "end").GroupBy(e => JsonDoc(e.PropsJson, "mode"))) Put("games", g.Key, g.Count());
        foreach (var g in evs.Where(e => e.Campaign is not null).GroupBy(e => e.Campaign!)) Put("campaign_active", g.Key, g.Select(Who).Distinct().Count());
        foreach (var g in clicks.GroupBy(c => c.Slug)) Put("campaign_clicks", g.Key, g.Count());
        return Task.CompletedTask;
    }

    private static string JsonDoc(string json, string key)
    {
        try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty(key, out var v) ? v.GetString() ?? "" : ""; }
        catch (JsonException) { return ""; }
    }

    public Task<int> PurgeEventsBeforeAsync(DateTimeOffset before, CancellationToken ct) { lock (_gate) return Task.FromResult(_events.RemoveAll(e => e.At < before)); }

    public Task<IReadOnlyList<StatRow>> StatsAsync(string metric, DateOnly from, DateOnly to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StatRow>>(_stats.Where(kv => kv.Key.Item2 == metric && kv.Key.Item1 >= from && kv.Key.Item1 <= to)
            .Select(kv => new StatRow(kv.Key.Item1, metric, kv.Key.Item3, kv.Value)).OrderBy(r => r.Day).ToList());

    public Task AttributeAsync(Guid userId, string campaign, CancellationToken ct) { lock (_gate) _attribution.TryAdd(userId, campaign); return Task.CompletedTask; }

    public Task<IReadOnlyList<CampaignRecord>> ListCampaignsAsync(CancellationToken ct) { lock (_gate) return Task.FromResult<IReadOnlyList<CampaignRecord>>(_campaigns.Values.OrderByDescending(c => c.CreatedAt).ToList()); }
    public Task<CampaignRecord?> GetCampaignAsync(string slug, CancellationToken ct) { lock (_gate) return Task.FromResult(_campaigns.GetValueOrDefault(slug)); }
    public Task<bool> AddCampaignAsync(CampaignRecord c, CancellationToken ct) { lock (_gate) return Task.FromResult(_campaigns.TryAdd(c.Slug, c)); }
    public Task SetArchivedAsync(string slug, bool archived, CancellationToken ct) { lock (_gate) if (_campaigns.TryGetValue(slug, out var c)) _campaigns[slug] = c with { Archived = archived }; return Task.CompletedTask; }
    public Task AddClickAsync(string slug, DateTimeOffset at, string platform, string? country, CancellationToken ct) { lock (_gate) _clicks.Add((slug, at, platform, country)); return Task.CompletedTask; }
}

#pragma warning disable CS0649 // Dapper fills the row fields
public sealed class PgAnalyticsRepository(PgDb db) : IAnalyticsRepository
{
    private sealed class StatDbRow { public DateTime day; public string metric = ""; public string dim = ""; public long value; }
    private sealed class CampaignRow
    {
        public string slug = ""; public string name = ""; public string channel = ""; public string? ios_url; public string? android_url; public string? web_url;
        public DateTime created_at; public bool archived;
        public CampaignRecord ToRecord() => new(slug, name, channel, ios_url, android_url, web_url, PgDb.Dto(created_at), archived);
    }

    public async Task AddEventsAsync(IReadOnlyList<EventRecord> events, CancellationToken ct)
    {
        if (events.Count == 0) return;
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("""
            insert into events (at, user_id, device_hash, session_id, platform, app_version, locale, country, campaign, kind, name, props)
            values (@at, @UserId, @DeviceHash, @SessionId, @Platform, @AppVersion, @Locale, @Country, @Campaign, @Kind, @Name, @PropsJson::jsonb)
            """, events.Select(e => new { at = e.At.UtcDateTime, e.UserId, e.DeviceHash, e.SessionId, e.Platform, e.AppVersion, e.Locale, e.Country, e.Campaign, e.Kind, e.Name, e.PropsJson }));
    }

    /// <summary>
    /// One transaction per day: delete that day's rows, then insert every metric. Retention counts, for the cohort of players
    /// created N days before <paramref name="day"/>, how many were active on <paramref name="day"/> (stored under the cohort's day).
    /// </summary>
    public async Task RollupAsync(DateOnly day, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var p = new { day = day.ToDateTime(TimeOnly.MinValue), next = day.AddDays(1).ToDateTime(TimeOnly.MinValue) };
        await c.ExecuteAsync("delete from daily_stats where day = @day::date and metric not like 'retention_%'", p, tx);
        await c.ExecuteAsync("""
            with ev as (select *, coalesce(user_id::text, device_hash, session_id) as who from events where at >= @day and at < @next)
            insert into daily_stats (day, metric, dim, value)
            select @day::date, 'dau', '', count(distinct who) from ev
            union all select @day::date, 'sessions', '', count(*) from ev where kind = 'session' and name = 'start'
            union all select @day::date, 'dau_platform', coalesce(platform, '?'), count(distinct who) from ev group by platform
            union all select @day::date, 'dau_locale', coalesce(locale, '?'), count(distinct who) from ev group by locale
            union all select @day::date, 'dau_country', coalesce(country, '?'), count(distinct who) from ev group by country
            union all select @day::date, 'page_views', name, count(*) from ev where kind = 'page_view' group by name
            union all select @day::date, 'ads', name || '|' || coalesce(platform, '?'), count(*) from ev where kind = 'ad' group by name, platform
            union all select @day::date, 'campaign_active', campaign, count(distinct who) from ev where campaign is not null group by campaign
            union all select @day::date, 'new_users', kind, count(*) from players where created_at >= @day and created_at < @next and kind in ('guest','account') group by kind
            union all select @day::date, 'campaign_signups', campaign, count(*) from players where created_at >= @day and created_at < @next and campaign is not null group by campaign
            union all select @day::date, 'games', mode, count(*) from games where ended_at >= @day and ended_at < @next group by mode
            union all select @day::date, 'campaign_clicks', slug, count(*) from campaign_clicks where at >= @day and at < @next group by slug
            on conflict (day, metric, dim) do update set value = excluded.value
            """, p, tx);
        foreach (var n in new[] { 1, 7, 30 })
        {
            var cohort = day.AddDays(-n);
            await c.ExecuteAsync("""
                insert into daily_stats (day, metric, dim, value)
                select @cohort::date, @metric, '', count(distinct p.id) from players p
                where p.created_at >= @cohort and p.created_at < @cohortNext and p.kind in ('guest','account')
                  and exists (select 1 from events e where e.user_id = p.id and e.at >= @day and e.at < @next)
                on conflict (day, metric, dim) do update set value = excluded.value
                """, new { cohort = cohort.ToDateTime(TimeOnly.MinValue), cohortNext = cohort.AddDays(1).ToDateTime(TimeOnly.MinValue), metric = $"retention_d{n}", p.day, p.next }, tx);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<int> PurgeEventsBeforeAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteAsync("delete from events where at < @before", new { before = before.UtcDateTime });
    }

    public async Task<IReadOnlyList<StatRow>> StatsAsync(string metric, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<StatDbRow>("select day, metric, dim, value from daily_stats where metric=@metric and day between @from::date and @to::date order by day",
            new { metric, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) });
        return rows.Select(r => new StatRow(DateOnly.FromDateTime(r.day), r.metric, r.dim, r.value)).ToList();
    }

    public async Task AttributeAsync(Guid userId, string campaign, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("update players set campaign = @campaign where id = @userId and campaign is null", new { userId, campaign });
    }

    public async Task<IReadOnlyList<CampaignRecord>> ListCampaignsAsync(CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<CampaignRow>("select * from campaigns order by created_at desc")).Select(r => r.ToRecord()).ToList();
    }

    public async Task<CampaignRecord?> GetCampaignAsync(string slug, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QuerySingleOrDefaultAsync<CampaignRow>("select * from campaigns where slug=@slug", new { slug }))?.ToRecord();
    }

    public async Task<bool> AddCampaignAsync(CampaignRecord r, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await c.ExecuteAsync("""
            insert into campaigns (slug, name, channel, ios_url, android_url, web_url, created_at) values (@Slug, @Name, @Channel, @IosUrl, @AndroidUrl, @WebUrl, @at)
            on conflict do nothing
            """, new { r.Slug, r.Name, r.Channel, r.IosUrl, r.AndroidUrl, r.WebUrl, at = r.CreatedAt.UtcDateTime }) > 0;
    }

    public async Task SetArchivedAsync(string slug, bool archived, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("update campaigns set archived=@archived where slug=@slug", new { slug, archived });
    }

    public async Task AddClickAsync(string slug, DateTimeOffset at, string platform, string? country, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("insert into campaign_clicks (slug, at, platform, country) values (@slug, @at, @platform, @country)",
            new { slug, at = at.UtcDateTime, platform, country });
    }
}
