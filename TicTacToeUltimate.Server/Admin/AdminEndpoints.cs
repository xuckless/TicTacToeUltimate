using System.Text.Json;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Contracts.Admin;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Analytics;
using TicTacToeUltimate.Server.Bots;
using TicTacToeUltimate.Server.Diagnostics;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Matchmaking;
using TicTacToeUltimate.Server.Moderation;
using TicTacToeUltimate.Server.Persistence;
using TicTacToeUltimate.Server.Presence;
using TicTacToeUltimate.Server.Progression;

namespace TicTacToeUltimate.Server.Admin;

/// <summary>
/// /api/admin/* for admin.alidirect.dev: live state, analytics, moderation (reports, penalties, appeals, auto-mod log, anti-cheat),
/// crashes and bugs (with GitHub issues), campaigns, audit log, infrastructure. Every request passes <see cref="AdminGate"/>;
/// every write is recorded in admin_audit.
/// </summary>
public static class AdminEndpoints
{
    private const string AdminKey = "ttt.admin";

    public static IServiceCollection AddTttAdmin(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<AdminOptions>(cfg.GetSection("Ttt:Admin"));
        services.AddHttpClient(nameof(AdminGate));
        services.AddHttpClient(nameof(InfraService));
        services.AddSingleton<AdminGate>();
        services.AddSingleton<InfraService>();
        services.AddSingleton<ServerLogRing>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<ServerLogRing>());
        return services;
    }

    public static WebApplication MapTttAdmin(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").AllowAnonymous().AddEndpointFilter(async (ctx, next) =>
        {
            var gate = ctx.HttpContext.RequestServices.GetRequiredService<AdminGate>();
            if (await gate.AuthorizeAsync(ctx.HttpContext) is not { } email) return Results.Problem(statusCode: 403, title: ErrorCodes.Forbidden);
            ctx.HttpContext.Items[AdminKey] = email;
            return await next(ctx);
        });

        admin.MapGet("/me", (HttpContext http, AdminGate gate) => Results.Ok(new AdminMeDto(Email(http), gate.DevBypass)));
        admin.MapGet("/live", (IServiceProvider sp) => Results.Ok(Live(sp)));
        admin.MapGet("/overview", Overview);
        admin.MapGet("/stats/{metric}", async (string metric, int? days, IAnalyticsRepository a, TimeProvider t, CancellationToken ct) =>
        {
            var to = DateOnly.FromDateTime(t.GetUtcNow().UtcDateTime);
            return Results.Ok((await a.StatsAsync(metric, to.AddDays(-Math.Clamp(days ?? 30, 1, 365)), to, ct)).Select(r => new StatPointDto(r.Day, r.Dim, r.Value)).ToArray());
        });

        // ---------- players ----------
        admin.MapGet("/players", async (string? q, IUserRepository users, IModerationRepository mod, PresenceTracker presence, CancellationToken ct) =>
        {
            var shadow = (await mod.ShadowUsersAsync(ct)).ToHashSet();
            IReadOnlyList<UserRecord> found = Guid.TryParse(q, out var id)
                ? (await users.GetAsync(id, ct) is { } one ? [one] : [])
                : string.IsNullOrWhiteSpace(q) ? await users.ListAsync(50, ct) : await users.SearchAsync(q.Trim().ToLowerInvariant(), 50, ct);
            return Results.Ok(found.Select(u => Player(u, shadow, presence)).ToArray());
        });
        admin.MapGet("/players/{id:guid}", PlayerDetail);
        admin.MapPost("/players/{id:guid}/penalties", async (Guid id, PenaltyRequestDto req, HttpContext http, ModerationService m, IModerationRepository repo, TimeProvider t, CancellationToken ct) =>
        {
            if (Kind(req.Kind) is not { } kind || string.IsNullOrWhiteSpace(req.Reason)) return Results.BadRequest(new AdminActionResultDto(false, "invalid"));
            var p = await m.ApplyAsync(id, kind, req.Reason.Trim(), req.DurationMinutes is int min and > 0 ? TimeSpan.FromMinutes(min) : null, "admin", Email(http), null, ct);
            await Audit(repo, http, t, "penalty." + req.Kind, id.ToString(), new { req.Reason, req.DurationMinutes, penaltyId = p.Id }, ct);
            return Results.Ok(new AdminActionResultDto(true));
        });
        admin.MapPost("/penalties/{id:long}/revoke", async (long id, HttpContext http, ModerationService m, IModerationRepository repo, TimeProvider t, CancellationToken ct) =>
        {
            var ok = await m.RevokeAsync(id, Email(http), ct);
            if (ok) await Audit(repo, http, t, "penalty.revoke", id.ToString(), new { }, ct);
            return Results.Ok(new AdminActionResultDto(ok, ok ? null : "not_found"));
        });
        admin.MapPost("/players/{id:guid}/purge-chat", async (Guid id, HttpContext http, IChatRepository chat, IModerationRepository repo, TimeProvider t, CancellationToken ct) =>
        {
            var n = await chat.HideAllFromAsync(id, t.GetUtcNow(), ct);
            await Audit(repo, http, t, "chat.purge", id.ToString(), new { hidden = n }, ct);
            return Results.Ok(new AdminActionResultDto(true));
        });
        admin.MapPost("/players/{id:guid}/shadow", async (Guid id, ShadowRequestDto req, HttpContext http, ModerationService m, IModerationRepository repo, TimeProvider t, CancellationToken ct) =>
        {
            if (req.On) await m.ApplyAsync(id, PenaltyKind.Shadow, "Shadow pool (admin)", null, "admin", Email(http), null, ct);
            else
                foreach (var p in (await repo.PenaltiesForAsync(id, 50, ct)).Where(p => p.Kind == PenaltyKind.Shadow && p.RevokedAt is null))
                    await m.RevokeAsync(p.Id, Email(http), ct);
            await Audit(repo, http, t, req.On ? "shadow.on" : "shadow.off", id.ToString(), new { }, ct);
            return Results.Ok(new AdminActionResultDto(true));
        });

        // ---------- moderation queues ----------
        admin.MapGet("/reports", async (string? status, IModerationRepository repo, IUserRepository users, CancellationToken ct) =>
            Results.Ok(await Reports(await repo.ListReportsAsync(status is "all" ? null : status ?? "open", null, 200, ct), users, ct)));
        admin.MapPost("/reports/{id:long}/resolve", async (long id, ResolveReportDto req, HttpContext http, IModerationRepository repo, ModerationService m, TimeProvider t, CancellationToken ct) =>
        {
            if (await repo.GetReportAsync(id, ct) is not { } r || req.Status is not ("actioned" or "dismissed")) return Results.BadRequest(new AdminActionResultDto(false, "invalid"));
            if (req.Penalty is { } pen && Kind(pen.Kind) is { } kind)
                await m.ApplyAsync(r.Target, kind, pen.Reason, pen.DurationMinutes is int min and > 0 ? TimeSpan.FromMinutes(min) : null, "admin", Email(http), r.Id, ct);
            await repo.UpdateReportAsync(id, req.Status, req.Resolution ?? req.Status, null, t.GetUtcNow(), ct);
            await Audit(repo, http, t, "report." + req.Status, id.ToString(), new { req.Resolution, penalty = req.Penalty?.Kind }, ct);
            return Results.Ok(new AdminActionResultDto(true));
        });
        admin.MapGet("/moderation/events", async (IModerationRepository repo, IUserRepository users, CancellationToken ct) =>
        {
            var events = await repo.ListEventsAsync(null, 300, ct);
            var names = await users.GetManyAsync(events.Select(e => e.UserId).Distinct().ToList(), ct);
            return Results.Ok(events.Select(e => new AdminModEventDto(e.Id, e.UserId, Name(names, e.UserId), e.Kind, e.Text, e.DetailJson, e.At)).ToArray());
        });
        admin.MapGet("/appeals", async (string? status, IModerationRepository repo, IUserRepository users, ModerationService m, TimeProvider t, CancellationToken ct) =>
        {
            var list = await repo.ListAppealsAsync(status is "all" ? null : status ?? "open", 200, ct);
            var names = await users.GetManyAsync(list.Select(a => a.UserId).Distinct().ToList(), ct);
            var result = new List<AdminAppealDto>();
            foreach (var a in list)
                result.Add(new AdminAppealDto(a.Id, a.PenaltyId, a.UserId, Name(names, a.UserId), a.Text, a.Status, a.CreatedAt,
                    await repo.GetPenaltyAsync(a.PenaltyId, ct) is { } p ? Penalty(p, a.Status, t.GetUtcNow()) : null, a.DecisionNote));
            return Results.Ok(result.ToArray());
        });
        admin.MapPost("/appeals/{id:long}/decide", async (long id, DecideAppealDto req, HttpContext http, IModerationRepository repo, ModerationService m, TimeProvider t, CancellationToken ct) =>
        {
            if (await repo.GetAppealAsync(id, ct) is not { } a) return Results.NotFound(new AdminActionResultDto(false, "not_found"));
            if (req.Approve) await m.RevokeAsync(a.PenaltyId, Email(http), ct);
            await repo.DecideAppealAsync(id, req.Approve ? "approved" : "denied", req.Note, t.GetUtcNow(), ct);
            await Audit(repo, http, t, req.Approve ? "appeal.approve" : "appeal.deny", id.ToString(), new { req.Note }, ct);
            return Results.Ok(new AdminActionResultDto(true));
        });
        admin.MapGet("/suspicious", Suspicious);

        // ---------- crashes + bugs ----------
        admin.MapGet("/crashes", async (string? kind, string? status, ICrashRepository repo, CancellationToken ct) =>
            Results.Ok((await repo.ListGroupsAsync(kind, status is "all" ? null : status ?? "open", 200, ct)).Select(Group).ToArray()));
        admin.MapGet("/crashes/{fingerprint}", async (string fingerprint, ICrashRepository repo, TimeProvider t, CancellationToken ct) =>
        {
            if (await repo.GetGroupAsync(fingerprint, ct) is not { } g) return Results.NotFound();
            var reports = await repo.ListReportsAsync(fingerprint, 50, ct);
            return Results.Ok(new CrashGroupDetailDto(Group(g), reports.Select(r => new CrashReportAdminDto(r.Id, r.UserId, r.Username, r.Platform, r.AppVersion,
                r.Os, r.Device, r.Browser, r.Locale, r.Screen, r.Route, r.Component, r.ExceptionType, r.Message, r.Stack, r.InnerJson, r.LogsJson, r.TrailJson,
                r.Description, r.ScreenshotPath, r.At)).ToArray(), await repo.DistinctUsersSinceAsync(fingerprint, t.GetUtcNow().AddHours(-24), ct)));
        });
        admin.MapPost("/crashes/{fingerprint}/status", async (string fingerprint, CrashStatusDto req, HttpContext http, ICrashRepository repo, IModerationRepository mod, TimeProvider t, CancellationToken ct) =>
        {
            if (req.Status is not ("open" or "ignored" or "fixed")) return Results.BadRequest(new AdminActionResultDto(false, "invalid"));
            await repo.SetStatusAsync(fingerprint, req.Status, ct);
            await Audit(mod, http, t, "crash." + req.Status, fingerprint, new { }, ct);
            return Results.Ok(new AdminActionResultDto(true));
        });
        admin.MapPost("/crashes/{fingerprint}/issue", async (string fingerprint, HttpContext http, DiagnosticsService d, IModerationRepository mod, TimeProvider t, CancellationToken ct) =>
        {
            var (url, error) = await d.FileIssueAsync(fingerprint, auto: false, ct);
            if (url is not null) await Audit(mod, http, t, "crash.issue", fingerprint, new { url }, ct);
            return Results.Ok(new AdminActionResultDto(url is not null, error, url));
        });
        admin.MapGet("/screenshots/{**path}", async (string path, IScreenshotStore store, CancellationToken ct) =>
            !path.Contains("..", StringComparison.Ordinal) && await store.GetAsync(path, ct) is { } png ? Results.File(png, "image/png") : Results.NotFound());

        // ---------- campaigns ----------
        admin.MapGet("/campaigns", Campaigns);
        admin.MapPost("/campaigns", async (CreateCampaignDto req, HttpContext http, IAnalyticsRepository a, IModerationRepository mod, IOptions<AdminOptions> o, TimeProvider t, CancellationToken ct) =>
        {
            if (AnalyticsService.Slug(req.Slug) is not { } slug || string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new AdminActionResultDto(false, "invalid_slug"));
            var (ios, android) = AnalyticsService.StoreLinks(slug, o.Value.AppStoreId, o.Value.PlayPackage);
            var ok = await a.AddCampaignAsync(new CampaignRecord(slug, req.Name.Trim(), string.IsNullOrWhiteSpace(req.Channel) ? "other" : req.Channel.Trim(),
                NonEmpty(req.IosUrl) ?? (o.Value.AppStoreId.Length > 0 ? ios : null), NonEmpty(req.AndroidUrl) ?? android, NonEmpty(req.WebUrl), t.GetUtcNow(), false), ct);
            if (ok) await Audit(mod, http, t, "campaign.create", slug, req, ct);
            return Results.Ok(new AdminActionResultDto(ok, ok ? null : "exists", o.Value.ShortLinkBase + slug));
        });
        admin.MapPost("/campaigns/{slug}/archive", async (string slug, HttpContext http, IAnalyticsRepository a, IModerationRepository mod, TimeProvider t, CancellationToken ct) =>
        {
            await a.SetArchivedAsync(slug, true, ct);
            await Audit(mod, http, t, "campaign.archive", slug, new { }, ct);
            return Results.Ok(new AdminActionResultDto(true));
        });

        // ---------- audit + infrastructure ----------
        admin.MapGet("/audit", async (IModerationRepository repo, CancellationToken ct) =>
            Results.Ok((await repo.ListAuditAsync(300, ct)).Select(a => new AuditDto(a.Id, a.AdminEmail, a.Action, a.Target, a.DetailJson, a.At)).ToArray()));
        admin.MapGet("/infra", async (InfraService infra, CancellationToken ct) => Results.Ok(await infra.GetAsync(ct)));
        admin.MapGet("/logs", async (string? service, string? level, string? q, InfraService infra, CancellationToken ct) =>
            Results.Ok(await infra.LogsAsync(service ?? "server", level, q, ct)));
        return app;
    }

    private static string Email(HttpContext http) => http.Items[AdminKey] as string ?? "?";
    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static Task Audit(IModerationRepository repo, HttpContext http, TimeProvider t, string action, string? target, object detail, CancellationToken ct) =>
        repo.AuditAsync(new AuditRecord(0, Email(http), action, target, JsonSerializer.Serialize(detail), t.GetUtcNow()), ct);

    private static PenaltyKind? Kind(string? k) => k switch
    {
        "warn" => PenaltyKind.Warn, "chat_mute" => PenaltyKind.ChatMute, "ban" => PenaltyKind.Ban, "username_reset" => PenaltyKind.UsernameReset,
        "shadow" => PenaltyKind.Shadow, _ => null,
    };

    private static LiveDto Live(IServiceProvider sp)
    {
        var presence = sp.GetService<PresenceTracker>();
        var gateway = sp.GetService<MatchGateway>();
        var matches = gateway?.LiveMatches() ?? [];
        var queues = sp.GetService<Matchmaker>()?.QueueCounts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) ?? [];
        var shadow = sp.GetService<IModerationRepository>()?.ShadowUsersAsync(CancellationToken.None).GetAwaiter().GetResult().Count ?? 0;
        return new LiveDto(presence?.OnlineUsers().Count ?? 0, sp.GetService<ServerDrain>()?.ConnectionCount ?? 0, matches.Count,
            matches.Count(m => m.Setup.SeatA.Kind == AccountKind.Bot || m.Setup.SeatB.Kind == AccountKind.Bot), sp.GetService<BotPool>()?.Count ?? 0, queues, shadow,
            matches.OrderByDescending(m => m.SpectatorCount).Take(100).Select(m => new AdminLiveMatchDto(m.Setup.MatchId, m.Setup.Mode.ToString(), m.Setup.TimeControl.ToString(),
                [m.Setup.SeatA.DisplayName, m.Setup.SeatB.DisplayName], [m.Setup.SeatA.Kind.ToString(), m.Setup.SeatB.Kind.ToString()], m.Ply, m.SpectatorCount, m.DeadlineAtMs)).ToArray(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static async Task<IResult> Overview(int? days, IServiceProvider sp, IAnalyticsRepository a, IModerationRepository mod, ICrashRepository crashes,
        TimeProvider t, CancellationToken ct)
    {
        var to = DateOnly.FromDateTime(t.GetUtcNow().UtcDateTime);
        var from = to.AddDays(-Math.Clamp(days ?? 30, 1, 365));
        var series = new Dictionary<string, StatPointDto[]>();
        foreach (var metric in new[] { "dau", "new_users", "games", "sessions", "ads", "page_views", "dau_platform" })
            series[metric] = (await a.StatsAsync(metric, from, to, ct)).Select(r => new StatPointDto(r.Day, r.Dim, r.Value)).ToArray();
        return Results.Ok(new OverviewDto(Live(sp), await mod.CountReportsAsync("open", ct), (await mod.ListAppealsAsync("open", 500, ct)).Count,
            await crashes.CountGroupsSinceAsync(t.GetUtcNow().AddHours(-24), ct), (await mod.ShadowUsersAsync(ct)).Count, series));
    }

    private static AdminPlayerDto Player(UserRecord u, HashSet<Guid> shadow, PresenceTracker presence) =>
        new(u.Id, u.Kind.ToString(), u.DisplayName, u.Username, u.Country, u.CreatedAt, u.LastSeenAt, shadow.Contains(u.Id), presence.IsOnline(u.Id));

    private static AdminPenaltyDto Penalty(PenaltyRecord p, string? appeal, DateTimeOffset now) =>
        new(p.Id, p.UserId, PenaltyKinds.ToDb(p.Kind), p.Reason, p.StartsAt, p.EndsAt, p.Source, p.CreatedBy, p.RevokedAt, p.ActiveAt(now), appeal);

    private static string Name(IReadOnlyDictionary<Guid, UserRecord> names, Guid id) => names.TryGetValue(id, out var u) ? u.Username ?? u.DisplayName : id.ToString()[..8];

    private static async Task<AdminReportDto[]> Reports(IReadOnlyList<ReportRecord> list, IUserRepository users, CancellationToken ct)
    {
        var names = await users.GetManyAsync(list.SelectMany(r => new[] { r.Reporter, r.Target }).Distinct().ToList(), ct);
        return list.Select(r => new AdminReportDto(r.Id, r.Reporter, Name(names, r.Reporter), r.Target, Name(names, r.Target), r.Kind, r.Reason, r.Note,
            r.MessageId, r.GameId, r.EvidenceJson, r.Status, r.AiVerdictJson, r.CreatedAt, r.Resolution)).ToArray();
    }

    private static async Task<IResult> PlayerDetail(Guid id, IUserRepository users, IModerationRepository mod, IGameRepository games, IRatingRepository ratings,
        CheatScorer cheat, PresenceTracker presence, TimeProvider t, CancellationToken ct)
    {
        if (await users.GetAsync(id, ct) is not { } u) return Results.NotFound();
        var now = t.GetUtcNow();
        var shadow = (await mod.ShadowUsersAsync(ct)).ToHashSet();
        var devices = await mod.DevicesForAsync(id, ct);
        var same = new HashSet<Guid>();
        foreach (var d in devices) foreach (var other in await mod.UsersForDeviceAsync(d, ct)) if (other != id) same.Add(other);
        var penalties = new List<AdminPenaltyDto>();
        foreach (var p in await mod.PenaltiesForAsync(id, 100, ct)) penalties.Add(Penalty(p, (await mod.AppealForPenaltyAsync(p.Id, ct))?.Status, now));
        var reports = await Reports(await mod.ListReportsAsync(null, id, 100, ct), users, ct);
        var events = (await mod.ListEventsAsync(id, 100, ct)).Select(e => new AdminModEventDto(e.Id, e.UserId, u.Username ?? u.DisplayName, e.Kind, e.Text, e.DetailJson, e.At)).ToArray();
        var list = await games.ListForUserAsync(id, null, 30, ct);
        var gameDtos = list.Select(g =>
        {
            var seat = g.Setup.SeatA.UserId == id ? 0 : 1;
            var opp = seat == 0 ? g.Setup.SeatB : g.Setup.SeatA;
            return new AdminGameDto(g.Id, g.Setup.Mode.ToString(), g.Setup.TimeControl.ToString(), $"{opp.DisplayName}{(opp.Kind == AccountKind.Bot ? " (bot)" : "")}",
                PostGameProcessor.ResultFor(g.Result, seat).ToString(), g.Result.Reason.ToString(), g.EndedAt, g.Plies);
        }).ToArray();
        var s = await cheat.ComputeAsync(id, ct);
        var cheatingReports = (await mod.ListReportsAsync(null, id, 200, ct)).Count(r => r.Kind == ReportKinds.Cheating);
        int? rating = u.Kind == AccountKind.Account ? (int)Math.Round((await ratings.GetAsync(id, RatingPools.Headline, ct)).Rating) : null;
        return Results.Ok(new AdminPlayerDetailDto(Player(u, shadow, presence), [.. devices], [.. same], [.. penalties], reports, events, gameDtos,
            new SuspicionRowDto(id, u.Username ?? u.DisplayName, u.Kind.ToString(), shadow.Contains(id), s.Games, s.Critical, s.Accuracy, s.WinRate, s.ThinkMsAvg,
                s.OverThreshold, cheatingReports), rating));
    }

    private static async Task<IResult> Suspicious(IModerationRepository mod, IUserRepository users, CheatScorer cheat, CancellationToken ct)
    {
        // Everyone in the shadow pool, plus everyone reported for cheating (the reports are the human signal).
        var reported = (await mod.ListReportsAsync(null, null, 500, ct)).Where(r => r.Kind == ReportKinds.Cheating).GroupBy(r => r.Target).ToDictionary(g => g.Key, g => g.Count());
        var shadow = (await mod.ShadowUsersAsync(ct)).ToHashSet();
        var ids = shadow.Concat(reported.Keys).Distinct().ToList();
        var names = await users.GetManyAsync(ids, ct);
        var rows = new List<SuspicionRowDto>();
        foreach (var id in ids)
        {
            var s = await cheat.ComputeAsync(id, ct);
            rows.Add(new SuspicionRowDto(id, Name(names, id), names.TryGetValue(id, out var u) ? u.Kind.ToString() : "?", shadow.Contains(id), s.Games, s.Critical,
                s.Accuracy, s.WinRate, s.ThinkMsAvg, s.OverThreshold, reported.GetValueOrDefault(id)));
        }
        return Results.Ok(rows.OrderByDescending(r => r.Shadow).ThenByDescending(r => r.Accuracy).ToArray());
    }

    private static CrashGroupDto Group(CrashGroupRecord g) => new(g.Fingerprint, g.Kind, g.Title, g.FirstSeen, g.LastSeen, g.Count, g.Status, g.GhIssueUrl, g.Platforms);

    private static async Task<IResult> Campaigns(IAnalyticsRepository a, IOptions<AdminOptions> o, TimeProvider t, CancellationToken ct)
    {
        var to = DateOnly.FromDateTime(t.GetUtcNow().UtcDateTime);
        var from = to.AddDays(-365);
        var clicks = (await a.StatsAsync("campaign_clicks", from, to, ct)).GroupBy(r => r.Dim).ToDictionary(g => g.Key, g => g.Sum(r => r.Value));
        var signups = (await a.StatsAsync("campaign_signups", from, to, ct)).GroupBy(r => r.Dim).ToDictionary(g => g.Key, g => g.Sum(r => r.Value));
        var active = (await a.StatsAsync("campaign_active", from, to, ct)).GroupBy(r => r.Dim).ToDictionary(g => g.Key, g => g.Max(r => r.Value));
        return Results.Ok((await a.ListCampaignsAsync(ct)).Select(c => new CampaignDto(c.Slug, c.Name, c.Channel, c.IosUrl, c.AndroidUrl, c.WebUrl, c.CreatedAt, c.Archived,
            o.Value.ShortLinkBase + c.Slug, clicks.GetValueOrDefault(c.Slug), signups.GetValueOrDefault(c.Slug), active.GetValueOrDefault(c.Slug))).ToArray());
    }
}
