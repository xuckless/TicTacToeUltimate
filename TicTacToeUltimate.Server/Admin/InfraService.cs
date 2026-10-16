using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts.Admin;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Persistence.Postgres;

namespace TicTacToeUltimate.Server.Admin;

/// <summary>The server's own recent log lines (searchable in the admin site even after Docker rotates its logs).</summary>
public sealed class ServerLogRing : ILoggerProvider
{
    private const int Max = 2000;
    private readonly ConcurrentQueue<LogEntryDto> _lines = new();

    public LogEntryDto[] Snapshot() => [.. _lines];
    public ILogger CreateLogger(string categoryName) => new Ring(this, categoryName);
    public void Dispose() { }

    private void Add(LogEntryDto e)
    {
        _lines.Enqueue(e);
        while (_lines.Count > Max && _lines.TryDequeue(out _)) { }
    }

    private sealed class Ring(ServerLogRing owner, string category) : ILogger
    {
        private readonly bool _ours = category.StartsWith("TicTacToeUltimate", StringComparison.Ordinal);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        // Our own Information lines (game flow) plus everyone's warnings and errors.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= (_ours ? LogLevel.Information : LogLevel.Warning) && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = $"{category.Split('.').Last()}: {formatter(state, exception)}";
            if (exception is not null) text += $"\n{exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}";
            owner.Add(new LogEntryDto(DateTimeOffset.UtcNow, "server", logLevel.ToString(), text.Length > 4000 ? text[..4000] : text));
        }
    }
}

/// <summary>
/// Infrastructure view (owner request 2026-10-16): host CPU/RAM/disk/network and month-to-date egress from the ttt-ops
/// sidecar, container stats, the server's own health, Supabase database/storage size and Cloudflare Worker traffic, plus
/// alerts. Every source is optional; missing ones show as unavailable.
/// </summary>
public sealed class InfraService(IHttpClientFactory http, IOptions<AdminOptions> options, IServiceProvider services, ServerLogRing ring,
    IConfiguration cfg, ILogger<InfraService> log)
{
    /// <summary>GCP free tier: 1 GB of egress per month to most destinations.</summary>
    public const long EgressFreeTier = 1L << 30;
    private const long SupabaseFreeDb = 500L * 1024 * 1024;
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
    private AdminOptions O => options.Value;

    private sealed record OpsStats(HostStatsDto? Host, ContainerStatsDto[]? Containers, InfraSampleDto[]? Series);

    public async Task<InfraDto> GetAsync(CancellationToken ct)
    {
        OpsStats? ops = null;
        if (O.OpsUrl.Length > 0)
        {
            try { ops = await OpsClient().GetFromJsonAsync<OpsStats>($"{O.OpsUrl.TrimEnd('/')}/v1/stats", JsonSerializerOptions.Web, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { log.LogInformation(ex, "ttt-ops unavailable"); }
        }
        var server = Health();
        var usage = await UsageAsync(ct);
        var alerts = new List<InfraAlertDto>();
        if (ops?.Host is { } h)
        {
            if (h.CpuPercent > 90) alerts.Add(new("error", $"CPU at {h.CpuPercent:0}%"));
            if (h.MemTotalBytes > 0 && h.MemUsedBytes * 100 / h.MemTotalBytes > 90) alerts.Add(new("error", $"Memory at {h.MemUsedBytes * 100 / h.MemTotalBytes}%"));
            if (h.DiskTotalBytes > 0 && h.DiskUsedBytes * 100 / h.DiskTotalBytes > 85) alerts.Add(new("warn", $"Disk at {h.DiskUsedBytes * 100 / h.DiskTotalBytes}%"));
            if (h.EgressMonthBytes > EgressFreeTier * 8 / 10) alerts.Add(new(h.EgressMonthBytes > EgressFreeTier ? "error" : "warn",
                $"Egress this month {h.EgressMonthBytes / 1048576.0:0} MB of the 1 GB free tier"));
        }
        else if (O.OpsUrl.Length > 0) alerts.Add(new("warn", "The ttt-ops sidecar is not answering: host metrics unavailable."));
        foreach (var c in ops?.Containers ?? [])
            if (c.Restarts > 0 && c.StartedAt is { } s && DateTimeOffset.UtcNow - s < TimeSpan.FromHours(24)) alerts.Add(new("warn", $"{c.Name} restarted ({c.Restarts}×)"));
        if (usage is { DbBytes: > 0 } u && u.DbBytes > SupabaseFreeDb * 8 / 10) alerts.Add(new("warn", $"Database at {u.DbBytes / 1048576} MB of 500 MB"));
        return new InfraDto(ops is not null, ops?.Host, ops?.Containers ?? [], ops?.Series ?? [], server, usage, EgressFreeTier, [.. alerts]);
    }

    public async Task<LogEntryDto[]> LogsAsync(string service, string? level, string? q, CancellationToken ct)
    {
        IEnumerable<LogEntryDto> lines = [];
        if (service == "server") lines = ring.Snapshot();
        else if (O.OpsUrl.Length > 0)
        {
            try { lines = await OpsClient().GetFromJsonAsync<LogEntryDto[]>($"{O.OpsUrl.TrimEnd('/')}/v1/logs?service={Uri.EscapeDataString(service)}&tail=1000", JsonSerializerOptions.Web, ct) ?? []; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return [new(DateTimeOffset.UtcNow, service, "Warning", "ttt-ops unavailable: " + ex.Message)]; }
        }
        if (level is { Length: > 0 }) lines = lines.Where(l => string.Equals(l.Level, level, StringComparison.OrdinalIgnoreCase) || (level == "Warning" && l.Level is "Error" or "Critical"));
        if (q is { Length: > 0 }) lines = lines.Where(l => l.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
        return lines.OrderByDescending(l => l.At).Take(500).ToArray();
    }

    private HttpClient OpsClient()
    {
        var c = http.CreateClient(nameof(InfraService));
        c.Timeout = TimeSpan.FromSeconds(4);
        if (O.OpsToken.Length > 0) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", O.OpsToken);
        return c;
    }

    private ServerHealthDto Health()
    {
        var asm = Assembly.GetEntryAssembly();
        ThreadPool.GetAvailableThreads(out var workers, out _);
        ThreadPool.GetMaxThreads(out var maxWorkers, out _);
        var tablebase = services.GetService<ITablebaseCache>()?.GetStatus(RuleSet.Default).ToString() ?? "n/a";
        return new ServerHealthDto(asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
            Environment.GetEnvironmentVariable("TTT_BUILD_SHA") ?? "local", Started, GC.GetTotalMemory(false), maxWorkers - workers,
            services.GetService<ServerDrain>()?.ConnectionCount ?? 0, services.GetService<MatchGateway>()?.LiveMatches().Count ?? 0, tablebase);
    }

    private async Task<UsageDto?> UsageAsync(CancellationToken ct)
    {
        long db = 0, storage = 0; var tables = new Dictionary<string, long>();
        var pg = services.GetService<PgDb>();
        if (pg is not null)
        {
            try
            {
                await using var c = await pg.Source.OpenConnectionAsync(ct);
                db = await c.ExecuteScalarAsync<long>("select pg_database_size(current_database())");
                foreach (var r in await c.QueryAsync<(string Name, long Bytes)>("""
                    select relname, pg_total_relation_size(relid) from pg_catalog.pg_statio_user_tables order by 2 desc limit 12
                    """)) tables[r.Name] = r.Bytes;
                storage = await c.ExecuteScalarAsync<long?>("select coalesce(sum((metadata->>'size')::bigint), 0) from storage.objects") ?? 0;
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException) { log.LogInformation(ex, "Database usage query failed"); }
        }
        var (requests, errors) = await CloudflareAsync(ct);
        return pg is null && requests is null ? null : new UsageDto(db, SupabaseFreeDb, tables, storage, requests, errors);
    }

    /// <summary>Worker requests and errors in the last 24 h (Cloudflare GraphQL Analytics; Ttt:Cloudflare:ApiToken + AccountId).</summary>
    private async Task<(long? Requests, long? Errors)> CloudflareAsync(CancellationToken ct)
    {
        var token = cfg["Ttt:Cloudflare:ApiToken"]; var account = cfg["Ttt:Cloudflare:AccountId"];
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(account)) return (null, null);
        try
        {
            using var client = http.CreateClient(nameof(InfraService));
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.cloudflare.com/client/v4/graphql");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var since = DateTimeOffset.UtcNow.AddHours(-24).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var until = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            req.Content = JsonContent.Create(new
            {
                query = """
                    query($account: String!, $since: Time!, $until: Time!) { viewer { accounts(filter: {accountTag: $account}) {
                      workersInvocationsAdaptive(limit: 100, filter: {datetime_geq: $since, datetime_leq: $until}) { sum { requests errors } } } } }
                    """,
                variables = new { account, since, until },
            });
            using var res = await client.SendAsync(req, ct);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            long r = 0, e = 0;
            foreach (var row in doc.RootElement.GetProperty("data").GetProperty("viewer").GetProperty("accounts")[0].GetProperty("workersInvocationsAdaptive").EnumerateArray())
            {
                r += row.GetProperty("sum").GetProperty("requests").GetInt64();
                e += row.GetProperty("sum").GetProperty("errors").GetInt64();
            }
            return (r, e);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or TaskCanceledException)
        {
            log.LogInformation(ex, "Cloudflare analytics unavailable");
            return (null, null);
        }
    }
}
