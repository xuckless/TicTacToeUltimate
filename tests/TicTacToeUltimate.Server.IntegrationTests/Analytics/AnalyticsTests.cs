using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Analytics;
using TicTacToeUltimate.Server.IntegrationTests.Infrastructure;

namespace TicTacToeUltimate.Server.IntegrationTests.Analytics;

public sealed class AnalyticsTests : IAsyncLifetime
{
    private readonly TttAppFactory _f = new();
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await _f.DisposeAsync();

    [Fact]
    public async Task Telemetry_IsStored_AndRolledUp()
    {
        using var http = _f.Http();
        http.DefaultRequestHeaders.Add("X-Ttt-Device", "dev-analytics");
        var now = _f.Time.GetUtcNow().ToUnixTimeMilliseconds();
        var batch = new TelemetryBatchDto("s1", "Web", "1.0", "en-US", "tiktok-oct",
        [
            new(now, TelemetryKinds.Session, "start"),
            new(now, TelemetryKinds.PageView, "/play"),
            new(now, TelemetryKinds.PageView, "/play"),
            new(now, TelemetryKinds.Ad, "interstitial_shown"),
            new(now, "not-a-kind", "dropped"),
        ]);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsJsonAsync("/api/telemetry", batch, ContractsJsonContext.Default.Options)).StatusCode);

        var repo = _f.Services.GetRequiredService<IAnalyticsRepository>();
        var day = DateOnly.FromDateTime(_f.Time.GetUtcNow().UtcDateTime);
        await repo.RollupAsync(day, CancellationToken.None);
        Assert.Equal(1, (await repo.StatsAsync("dau", day, day, CancellationToken.None)).Single().Value);
        Assert.Equal(2, (await repo.StatsAsync("page_views", day, day, CancellationToken.None)).Single(r => r.Dim == "/play").Value);
        Assert.Equal(1, (await repo.StatsAsync("ads", day, day, CancellationToken.None)).Single().Value);
        Assert.Equal(1, (await repo.StatsAsync("campaign_active", day, day, CancellationToken.None)).Single(r => r.Dim == "tiktok-oct").Value);
    }

    [Fact]
    public async Task CampaignLinks_CountClicks_AndPickTheStore()
    {
        var repo = _f.Services.GetRequiredService<IAnalyticsRepository>();
        await repo.AddCampaignAsync(new CampaignRecord("asa-launch", "Apple Search Ads launch", "apple-search-ads",
            "https://apps.apple.com/app/id123?ct=asa-launch", "https://play.google.com/store/apps/details?id=x", null, _f.Time.GetUtcNow(), false), CancellationToken.None);
        using var http = _f.Http();
        var ios = await http.GetFromJsonAsync<CampaignRedirectDto>("/api/c/asa-launch?ua=Mozilla%2F5.0%20(iPhone)", ContractsJsonContext.Default.Options);
        Assert.StartsWith("https://apps.apple.com/", ios!.Url, StringComparison.Ordinal);
        var web = await http.GetFromJsonAsync<CampaignRedirectDto>("/api/c/asa-launch?ua=Mozilla%2F5.0%20(Macintosh)", ContractsJsonContext.Default.Options);
        Assert.Contains("?c=asa-launch", web!.Url, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/c/nope")).StatusCode);

        var day = DateOnly.FromDateTime(_f.Time.GetUtcNow().UtcDateTime);
        await repo.RollupAsync(day, CancellationToken.None);
        Assert.Equal(2, (await repo.StatsAsync("campaign_clicks", day, day, CancellationToken.None)).Single().Value);
    }
}
