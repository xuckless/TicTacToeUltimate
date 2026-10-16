using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;
using TicTacToeUltimate.Contracts.Admin;

namespace TicTacToeUltimate.Admin.Services;

/// <summary>Typed client for /api/admin. Cloudflare Access adds its signed header at the edge; in development the server's bypass reads X-Admin-Email.</summary>
public sealed class AdminApi(HttpClient http, string? devEmail)
{
    private static readonly JsonSerializerOptions Json = AdminJsonContext.Default.Options;

    /// <summary>Raised when a call fails (shown as a banner by the layout).</summary>
    public event Action<string>? Failed;

    public Task<AdminMeDto?> MeAsync() => Get<AdminMeDto>("me");
    public Task<LiveDto?> LiveAsync() => Get<LiveDto>("live");
    public Task<OverviewDto?> OverviewAsync(int days) => Get<OverviewDto>($"overview?days={days}");
    public Task<StatPointDto[]?> StatsAsync(string metric, int days) => Get<StatPointDto[]>($"stats/{metric}?days={days}");
    public Task<AdminActionResultDto?> RollupAsync() => Post<AdminActionResultDto>("stats/rollup", null);
    public Task<AdminPlayerDto[]?> PlayersAsync(string? q) => Get<AdminPlayerDto[]>($"players?q={Uri.EscapeDataString(q ?? "")}");
    public Task<AdminPlayerDetailDto?> PlayerAsync(Guid id) => Get<AdminPlayerDetailDto>($"players/{id}");
    public Task<AdminActionResultDto?> PenalizeAsync(Guid id, PenaltyRequestDto req) => Post<AdminActionResultDto>($"players/{id}/penalties", req);
    public Task<AdminActionResultDto?> RevokeAsync(long penaltyId) => Post<AdminActionResultDto>($"penalties/{penaltyId}/revoke", null);
    public Task<AdminActionResultDto?> PurgeChatAsync(Guid id) => Post<AdminActionResultDto>($"players/{id}/purge-chat", null);
    public Task<AdminActionResultDto?> ShadowAsync(Guid id, bool on) => Post<AdminActionResultDto>($"players/{id}/shadow", new ShadowRequestDto(on));
    public Task<AdminReportDto[]?> ReportsAsync(string status) => Get<AdminReportDto[]>($"reports?status={status}");
    public Task<AdminActionResultDto?> ResolveAsync(long id, ResolveReportDto req) => Post<AdminActionResultDto>($"reports/{id}/resolve", req);
    public Task<AdminModEventDto[]?> ModEventsAsync() => Get<AdminModEventDto[]>("moderation/events");
    public Task<AdminAppealDto[]?> AppealsAsync(string status) => Get<AdminAppealDto[]>($"appeals?status={status}");
    public Task<AdminActionResultDto?> DecideAsync(long id, DecideAppealDto req) => Post<AdminActionResultDto>($"appeals/{id}/decide", req);
    public Task<SuspicionRowDto[]?> SuspiciousAsync() => Get<SuspicionRowDto[]>("suspicious");
    public Task<CrashGroupDto[]?> CrashesAsync(string kind, string status) => Get<CrashGroupDto[]>($"crashes?kind={kind}&status={status}");
    public Task<CrashGroupDetailDto?> CrashAsync(string fp) => Get<CrashGroupDetailDto>($"crashes/{Uri.EscapeDataString(fp)}");
    public Task<AdminActionResultDto?> CrashStatusAsync(string fp, string status) => Post<AdminActionResultDto>($"crashes/{Uri.EscapeDataString(fp)}/status", new CrashStatusDto(status));
    public Task<AdminActionResultDto?> FileIssueAsync(string fp) => Post<AdminActionResultDto>($"crashes/{Uri.EscapeDataString(fp)}/issue", null);
    public string ScreenshotUrl(string path) => new Uri(http.BaseAddress!, "api/admin/screenshots/" + path).ToString();
    public Task<CampaignDto[]?> CampaignsAsync() => Get<CampaignDto[]>("campaigns");
    public Task<AdminActionResultDto?> CreateCampaignAsync(CreateCampaignDto req) => Post<AdminActionResultDto>("campaigns", req);
    public Task<AdminActionResultDto?> ArchiveCampaignAsync(string slug) => Post<AdminActionResultDto>($"campaigns/{slug}/archive", null);
    public Task<AuditDto[]?> AuditAsync() => Get<AuditDto[]>("audit");
    public Task<InfraDto?> InfraAsync() => Get<InfraDto>("infra");
    public Task<LogEntryDto[]?> LogsAsync(string service, string? level, string? q) =>
        Get<LogEntryDto[]>($"logs?service={service}&level={level}&q={Uri.EscapeDataString(q ?? "")}");

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, "api/admin/" + path);
        if (devEmail is { Length: > 0 }) req.Headers.Add("X-Admin-Email", devEmail);
        return req;
    }

    private async Task<T?> Get<T>(string path)
    {
        try
        {
            using var res = await http.SendAsync(Request(HttpMethod.Get, path));
            if (!res.IsSuccessStatusCode) { Failed?.Invoke(res.StatusCode == System.Net.HttpStatusCode.Forbidden ? "Not signed in as an admin." : $"{path}: {(int)res.StatusCode}"); return default; }
            return await res.Content.ReadFromJsonAsync<T>(Json);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { Failed?.Invoke($"{path}: {e.Message}"); return default; }
    }

    private async Task<T?> Post<T>(string path, object? body)
    {
        try
        {
            using var req = Request(HttpMethod.Post, path);
            if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.BadRequest) { Failed?.Invoke($"{path}: {(int)res.StatusCode}"); return default; }
            return await res.Content.ReadFromJsonAsync<T>(Json);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { Failed?.Invoke($"{path}: {e.Message}"); return default; }
    }
}

/// <summary>Light / dark / system, persisted by the page script.</summary>
public sealed class Theme(Microsoft.JSInterop.IJSRuntime js)
{
    public ValueTask SetAsync(string mode) => js.InvokeVoidAsync("adminTheme.set", mode);
}
