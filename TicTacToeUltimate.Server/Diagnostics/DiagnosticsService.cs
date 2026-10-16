using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Diagnostics;

/// <summary>Ttt:GitHub. Without a token, issues are not filed (the admin button says so).</summary>
public sealed class GitHubOptions
{
    public string Token { get; set; } = "";
    public string Repo { get; set; } = "xuckless/TicTacToeUltimate";
    /// <summary>A crash group seen by this many players within 24 h is filed automatically.</summary>
    public int AutoFileUsers { get; set; } = 5;
    /// <summary>
    /// Copy the player's own bug description into the issue. Off by default: the repository is public, so issues carry
    /// only technical data (stack, route, trail, logs) and the admin site keeps the player's words.
    /// </summary>
    public bool IncludePlayerText { get; set; }
}

/// <summary>Bug screenshots: Supabase Storage (private bucket) when configured, else memory (dev).</summary>
public interface IScreenshotStore
{
    Task<string?> PutAsync(string path, byte[] png, CancellationToken ct);
    Task<byte[]?> GetAsync(string path, CancellationToken ct);
    Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct);
}

public sealed class InMemoryScreenshotStore : IScreenshotStore
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();
    public Task<string?> PutAsync(string path, byte[] png, CancellationToken ct) { _files[path] = png; return Task.FromResult<string?>(path); }
    public Task<byte[]?> GetAsync(string path, CancellationToken ct) => Task.FromResult(_files.GetValueOrDefault(path));
    public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct) { foreach (var p in paths) _files.TryRemove(p, out _); return Task.CompletedTask; }
}

/// <summary>Supabase Storage REST with the service-role key (bucket bug-screenshots, migration 0012).</summary>
public sealed class SupabaseScreenshotStore(IHttpClientFactory http, IOptions<TttOptions> options, ILogger<SupabaseScreenshotStore> log) : IScreenshotStore
{
    private const string Bucket = "bug-screenshots";
    private SupabaseAuthOptions S => options.Value.Auth.Supabase;

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, $"{S.Url.TrimEnd('/')}/storage/v1/object/{path}");
        req.Headers.Add("apikey", S.ServiceRoleKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", S.ServiceRoleKey);
        return req;
    }

    public async Task<string?> PutAsync(string path, byte[] png, CancellationToken ct)
    {
        using var client = http.CreateClient(nameof(SupabaseScreenshotStore));
        using var req = Request(HttpMethod.Post, $"{Bucket}/{path}");
        req.Content = new ByteArrayContent(png);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        try
        {
            using var res = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode) return path;
            log.LogWarning("Screenshot upload returned {Status}", (int)res.StatusCode);
        }
        catch (HttpRequestException ex) { log.LogWarning(ex, "Screenshot upload failed"); }
        return null;
    }

    public async Task<byte[]?> GetAsync(string path, CancellationToken ct)
    {
        using var client = http.CreateClient(nameof(SupabaseScreenshotStore));
        using var req = Request(HttpMethod.Get, $"authenticated/{Bucket}/{path}");
        using var res = await client.SendAsync(req, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : null;
    }

    public async Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0) return;
        using var client = http.CreateClient(nameof(SupabaseScreenshotStore));
        using var req = Request(HttpMethod.Delete, Bucket);
        req.Content = JsonContent.Create(new { prefixes = paths });
        try { using var _ = await client.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { log.LogWarning(ex, "Screenshot cleanup failed"); }
    }
}

/// <summary>
/// Crash and bug intake (owner decision 2026-10-16). Crashes are grouped by fingerprint (exception type + the top three frames
/// in our own code); every bug report is its own group. Groups can become GitHub issues written for an agent to act on.
/// </summary>
public sealed partial class DiagnosticsService(ICrashRepository repo, IScreenshotStore screenshots, IUserRepository users,
    IHttpClientFactory http, IOptions<GitHubOptions> github, TimeProvider time, ILogger<DiagnosticsService> log)
{
    private const int MaxText = 8000, MaxScreenshotBytes = 1_500_000;

    /// <summary>Stores a report. <paramref name="userId"/> is null before sign-in.</summary>
    public async Task<(string? Fingerprint, string? Error)> AcceptAsync(CrashReportDto r, Guid? userId, CancellationToken ct)
    {
        if (r.Kind is not ("crash" or "bug")) return (null, ErrorCodes.Validation);
        if (r.Kind == "bug" && string.IsNullOrWhiteSpace(r.Description)) return (null, ErrorCodes.Validation);
        var now = time.GetUtcNow();
        var fingerprint = r.Kind == "bug" ? "bug-" + Guid.NewGuid().ToString("N")[..12] : Fingerprint(r.ExceptionType, r.Stack, r.Message);
        var title = r.Kind == "bug"
            ? "Bug: " + Cut(r.Description!.Trim().ReplaceLineEndings(" "), 90)
            : $"{ShortType(r.ExceptionType)}: {Cut((r.Message ?? "").ReplaceLineEndings(" "), 80)}" + (r.Component is { Length: > 0 } c ? $" in {c}" : "");
        var username = userId is { } id ? (await users.GetAsync(id, ct))?.Username : null;

        string? shot = null;
        if (r.Kind == "bug" && r.ScreenshotPngBase64 is { Length: > 0 } b64)
        {
            try
            {
                var png = Convert.FromBase64String(b64);
                if (png.Length <= MaxScreenshotBytes && png.AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47]))
                    shot = await screenshots.PutAsync($"{now:yyyy/MM/dd}/{Guid.NewGuid():N}.png", png, ct);
            }
            catch (FormatException) { }
        }

        await repo.UpsertGroupAsync(fingerprint, r.Kind, title, Cut(r.Platform, 20), now, ct);
        await repo.AddReportAsync(new CrashReportRecord(0, fingerprint, r.Kind, userId, username, Cut(r.Platform, 20), Cut(r.AppVersion, 40),
            Cut(r.Os), Cut(r.Device), Cut(r.Browser, 300), Cut(r.Locale, 20), Cut(r.Screen, 40), Cut(r.Route, 300), Cut(r.Component, 200),
            Cut(r.ExceptionType, 300), Cut(r.Message, 2000), Cut(r.Stack, MaxText),
            JsonSerializer.Serialize((r.InnerExceptions ?? []).Take(5).Select(x => Cut(x, 2000))),
            JsonSerializer.Serialize((r.Logs ?? []).TakeLast(100).Select(l => l with { Message = Cut(l.Message, 1000)! })),
            JsonSerializer.Serialize((r.Trail ?? []).TakeLast(40).Select(t => t with { Text = Cut(t.Text, 200)! })),
            Cut(r.Description, 4000), shot, now), ct);

        if (r.Kind == "crash") _ = AutoFileAsync(fingerprint);
        return (fingerprint, null);
    }

    /// <summary>Files the group as a GitHub issue once enough players hit it within 24 h.</summary>
    private async Task AutoFileAsync(string fingerprint)
    {
        try
        {
            var g = await repo.GetGroupAsync(fingerprint, CancellationToken.None);
            if (g is null || g.GhIssueUrl is not null || g.Status != "open" || github.Value.Token.Length == 0) return;
            var users24 = await repo.DistinctUsersSinceAsync(fingerprint, time.GetUtcNow().AddHours(-24), CancellationToken.None);
            if (users24 >= github.Value.AutoFileUsers) await FileIssueAsync(fingerprint, auto: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Auto-file of {Group} failed", fingerprint); }
    }

    /// <summary>Creates the GitHub issue for a group (admin button or auto). Returns its URL, or an error code.</summary>
    public async Task<(string? Url, string? Error)> FileIssueAsync(string fingerprint, bool auto, CancellationToken ct)
    {
        var g = await repo.GetGroupAsync(fingerprint, ct);
        if (g is null) return (null, "not_found");
        if (g.GhIssueUrl is { } existing) return (existing, null);
        if (github.Value.Token.Length == 0) return (null, "github_not_configured");
        var reports = await repo.ListReportsAsync(fingerprint, 20, ct);
        var users24 = await repo.DistinctUsersSinceAsync(fingerprint, time.GetUtcNow().AddHours(-24), ct);
        var body = IssueBody(g, reports, users24, auto, github.Value.IncludePlayerText);
        using var client = http.CreateClient(nameof(DiagnosticsService));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/repos/{github.Value.Repo}/issues")
        {
            Content = JsonContent.Create(new { title = Cut(g.Kind == "bug" && !github.Value.IncludePlayerText ? $"Bug report {g.Fingerprint}" : g.Title, 240), body, labels = new[] { g.Kind, "agent-ready" } }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", github.Value.Token);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("ttt-server", "1.0"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var res = await client.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("GitHub issue for {Group} returned {Status}", fingerprint, (int)res.StatusCode);
            return (null, "github_failed");
        }
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var url = doc.RootElement.GetProperty("html_url").GetString()!;
        await repo.SetIssueAsync(fingerprint, url, ct);
        return (url, null);
    }

    /// <summary>The issue text: everything an agent needs to reproduce and fix it, plus acceptance criteria.</summary>
    public static string IssueBody(CrashGroupRecord g, IReadOnlyList<CrashReportRecord> reports, int users24, bool auto, bool includePlayerText = false)
    {
        var r = reports.FirstOrDefault();
        var sb = new StringBuilder();
        sb.AppendLine(auto ? $"_Filed automatically: {users24} players hit this in the last 24 h._" : "_Filed from the admin site._").AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine($"- **Kind:** {g.Kind} · **Seen:** {g.Count}× · **First:** {g.FirstSeen:u} · **Last:** {g.LastSeen:u}");
        sb.AppendLine($"- **Platforms:** {string.Join(", ", g.Platforms)}");
        sb.AppendLine($"- **Fingerprint:** `{g.Fingerprint}`");
        if (r is null) return sb.ToString();
        sb.AppendLine().AppendLine("## Environment (latest report)");
        sb.AppendLine($"- App {r.AppVersion} on {r.Platform} · {r.Os} · {r.Device} · {r.Browser}");
        sb.AppendLine($"- Locale {r.Locale} · screen {r.Screen} · route `{r.Route}`");
        if (r.Description is { Length: > 0 } d)
            sb.AppendLine().AppendLine("## Player's description").AppendLine(includePlayerText ? "> " + d.ReplaceLineEndings("\n> ") : "_In the admin site (Bugs) — kept out of public issues._");
        if (r.ExceptionType is not null)
        {
            sb.AppendLine().AppendLine("## Exception");
            sb.AppendLine($"`{r.ExceptionType}`: {r.Message}");
            if (r.Component is { Length: > 0 } c) sb.AppendLine($"- **Failing component:** `{c}`");
            var files = SuspectedFiles(r.Stack);
            if (files.Count > 0) sb.AppendLine("- **Suspected files:** " + string.Join(", ", files.Select(f => $"`{f}`")));
            sb.AppendLine().AppendLine("```").AppendLine(Cut(r.Stack, 6000)).AppendLine("```");
        }
        sb.AppendLine().AppendLine("## Recent actions before it happened").AppendLine("```").AppendLine(Lines(r.TrailJson, 40)).AppendLine("```");
        sb.AppendLine().AppendLine("## Recent logs").AppendLine("```").AppendLine(Lines(r.LogsJson, 40)).AppendLine("```");
        sb.AppendLine().AppendLine("## Acceptance criteria");
        sb.AppendLine("- [ ] Reproduce from the trail above (or explain why it can't be reproduced).");
        sb.AppendLine("- [ ] Fix the root cause, not just the symptom.");
        sb.AppendLine("- [ ] Add a regression test (bUnit for Shared components, xUnit for client/server).");
        sb.AppendLine("- [ ] All test suites pass; mark the crash group fixed in the admin site.");
        return sb.ToString();
    }

    private static string Lines(string json, int max)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return string.Join('\n', doc.RootElement.EnumerateArray().TakeLast(max).Select(e => string.Join(" | ", e.EnumerateObject().Select(p => p.Value.ToString()))));
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>"TicTacToeUltimate.Shared.Pages.MatchPage.OnClick" → "TicTacToeUltimate.Shared/Pages/MatchPage.razor (or .cs)".</summary>
    public static IReadOnlyList<string> SuspectedFiles(string? stack)
    {
        var files = new List<string>();
        foreach (Match m in OurFrame().Matches(stack ?? ""))
        {
            var parts = m.Groups["type"].Value.Split('.');
            var project = parts.Length > 1 && parts[1] is "Shared" or "Client" or "Server" or "Engine" or "Contracts" or "Web" ? $"TicTacToeUltimate.{parts[1]}" : "TicTacToeUltimate";
            var rest = parts.Skip(project == "TicTacToeUltimate" ? 1 : 2).Select(p => p.Split('+', '<', '`')[0]).Where(p => p.Length > 0).ToArray();
            if (rest.Length == 0) continue;
            var f = $"{project}/{string.Join('/', rest)}.{(project.EndsWith("Shared", StringComparison.Ordinal) ? "razor" : "cs")}";
            if (!files.Contains(f)) files.Add(f);
            if (files.Count == 4) break;
        }
        return files;
    }

    /// <summary>Exception type + top three frames in our own code (no line numbers, no async noise).</summary>
    public static string Fingerprint(string? type, string? stack, string? message)
    {
        var frames = OurFrame().Matches(stack ?? "").Select(m => m.Groups["type"].Value + "." + m.Groups["method"].Value).Distinct().Take(3);
        var basis = $"{type}|{string.Join('|', frames)}";
        if (!basis.Contains('.') ) basis += "|" + DigitsRegex().Replace(message ?? "", "#");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(basis)).AsSpan(0, 8));
    }

    private static string ShortType(string? t) => t?.Split('.').Last() ?? "Error";
    private static string? Cut(string? s, int max = 300) => s is null ? null : s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"at (?<type>TicTacToeUltimate\.[\w.+`<>]+)\.(?<method>[\w<>]+)\(", RegexOptions.None, 500)]
    private static partial Regex OurFrame();
    [GeneratedRegex(@"\d+", RegexOptions.None, 200)]
    private static partial Regex DigitsRegex();
}

/// <summary>Deletes bug screenshots after 90 days (owner decision 2026-10-16).</summary>
public sealed class ScreenshotJanitor(ICrashRepository repo, IScreenshotStore store, TimeProvider time, ILogger<ScreenshotJanitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var old = await repo.TakeScreenshotsBeforeAsync(time.GetUtcNow().AddDays(-90), stoppingToken);
                await store.DeleteAsync(old, stoppingToken);
                if (old.Count > 0) log.LogInformation("Deleted {Count} old bug screenshots", old.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Screenshot cleanup failed"); }
            try { await Task.Delay(TimeSpan.FromHours(6), time, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
