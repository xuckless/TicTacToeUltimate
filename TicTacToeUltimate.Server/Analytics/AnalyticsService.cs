using System.Text.Json;
using System.Text.RegularExpressions;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Moderation;
using TicTacToeUltimate.Server.Platform;

namespace TicTacToeUltimate.Server.Analytics;

/// <summary>
/// First-party analytics (owner decision 2026-10-16): cookie-free events from the apps, stored in our own Postgres. Raw
/// events live 14 days; <see cref="RollupJob"/> keeps daily numbers forever. Campaign short links count clicks and attach the
/// campaign to the players they bring.
/// </summary>
public sealed partial class AnalyticsService(IAnalyticsRepository repo, ModerationService moderation, TimeProvider time)
{
    private const int MaxEvents = 50;

    /// <summary>Stores a batch (unknown kinds dropped, timestamps clamped to "now" ± a day).</summary>
    public async Task IngestAsync(TelemetryBatchDto batch, Guid? userId, string? deviceId, string? country, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var device = deviceId is { Length: > 0 and <= 128 } ? moderation.DeviceHash(deviceId) : null;
        var campaign = Slug(batch.Campaign);
        var rows = (batch.Events ?? []).Take(MaxEvents).Where(e => TelemetryKinds.All.Contains(e.Kind) && e.Name is { Length: > 0 }).Select(e =>
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(e.AtMs);
            if (at > now || at < now.AddDays(-1)) at = now;
            var props = (e.Props ?? []).Take(10).ToDictionary(kv => Cut(kv.Key, 40), kv => Cut(kv.Value, 200));
            return new EventRecord(at, userId, device, Cut(batch.SessionId, 64), Cut(batch.Platform, 20), Cut(batch.AppVersion, 40), Cut(batch.Locale, 20),
                country is { Length: 2 } ? country.ToUpperInvariant() : null, campaign, e.Kind, Cut(e.Name, 120), JsonSerializer.Serialize(props));
        }).ToList();
        await repo.AddEventsAsync(rows, ct);
        if (userId is { } u && campaign is not null) await repo.AttributeAsync(u, campaign, ct);
    }

    /// <summary>GET /api/c/{slug}: counts the click and picks the destination by device.</summary>
    public async Task<string?> RedirectAsync(string slug, string? userAgent, string? country, string webBaseUrl, CancellationToken ct)
    {
        var c = await repo.GetCampaignAsync(slug, ct);
        if (c is null || c.Archived) return null;
        var platform = PlatformOf(userAgent);
        await repo.AddClickAsync(slug, time.GetUtcNow(), platform, country is { Length: 2 } ? country.ToUpperInvariant() : null, ct);
        return platform switch
        {
            "ios" when c.IosUrl is { Length: > 0 } ios => ios,
            "android" when c.AndroidUrl is { Length: > 0 } android => android,
            _ => c.WebUrl is { Length: > 0 } web ? web : $"{webBaseUrl.TrimEnd('/')}/?c={Uri.EscapeDataString(slug)}",
        };
    }

    /// <summary>Default store links for a new campaign: Apple campaign token and Play install referrer carry the slug.</summary>
    public static (string Ios, string Android) StoreLinks(string slug, string appStoreId, string playPackage) =>
        ($"https://apps.apple.com/app/id{appStoreId}?pt=0&ct={Uri.EscapeDataString(slug)}&mt=8",
         $"https://play.google.com/store/apps/details?id={playPackage}&referrer={Uri.EscapeDataString("utm_source=" + slug + "&utm_campaign=" + slug)}");

    public static string PlatformOf(string? ua) =>
        ua is null ? "web" : IosRegex().IsMatch(ua) ? "ios" : ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "android" : "web";

    public static string? Slug(string? s) => s is { Length: > 1 and <= 40 } && SlugRegex().IsMatch(s) ? s : null;
    private static string Cut(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];

    [GeneratedRegex("iPhone|iPad|iPod", RegexOptions.IgnoreCase, 200)]
    private static partial Regex IosRegex();
    [GeneratedRegex("^[a-z0-9-]{2,40}$", RegexOptions.None, 200)]
    private static partial Regex SlugRegex();
}

/// <summary>Hourly: recompute today and yesterday (and retention for the cohorts that end today); purge raw events after 14 days.</summary>
public sealed class RollupJob(IAnalyticsRepository repo, TimeProvider time, ILogger<RollupJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
                await repo.RollupAsync(today.AddDays(-1), stoppingToken);
                await repo.RollupAsync(today, stoppingToken);
                await repo.PurgeEventsBeforeAsync(time.GetUtcNow().AddDays(-14), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Analytics rollup failed"); }
            try { await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}

public static class AnalyticsEndpoints
{
    public static IServiceCollection AddTttAnalytics(this IServiceCollection services, IConfiguration cfg)
    {
        if (string.Equals(cfg["Ttt:Persistence:Provider"], "Postgres", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IAnalyticsRepository, PgAnalyticsRepository>();
        else
            services.AddSingleton<IAnalyticsRepository, InMemoryAnalyticsRepository>();
        services.AddSingleton<AnalyticsService>();
        services.AddHostedService<RollupJob>();
        return services;
    }

    public static WebApplication MapTttAnalytics(this WebApplication app)
    {
        app.MapPost("/api/telemetry", async (TelemetryBatchDto batch, HttpContext http, AnalyticsService a, CancellationToken ct) =>
        {
            Guid? user = http.User.Identity?.IsAuthenticated == true && http.User.ToTttUser() is { Kind: not AccountKind.Bot } me ? me.UserId : null;
            await a.IngestAsync(batch, user, http.Request.Headers["X-Ttt-Device"].FirstOrDefault(), http.Request.Headers["CF-IPCountry"].FirstOrDefault(), ct);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting(RateLimits.Api);

        app.MapGet("/api/c/{slug}", async (string slug, HttpContext http, AnalyticsService a, Microsoft.Extensions.Options.IOptions<TttOptions> o, CancellationToken ct) =>
        {
            // The apex worker passes the visitor's user agent and country through (?ua=&country=), since it makes the request.
            var ua = http.Request.Query["ua"].FirstOrDefault() ?? http.Request.Headers.UserAgent.ToString();
            var country = http.Request.Query["country"].FirstOrDefault() ?? http.Request.Headers["CF-IPCountry"].FirstOrDefault();
            var url = await a.RedirectAsync(slug.ToLowerInvariant(), ua, country, o.Value.Web.BaseUrl, ct);
            return url is null ? ApiErrors.Problem(404, ErrorCodes.Validation) : Results.Ok(new CampaignRedirectDto(url));
        }).AllowAnonymous().RequireRateLimiting(RateLimits.Auth);
        return app;
    }
}
