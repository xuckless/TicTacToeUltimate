using System.Globalization;
using System.Text.RegularExpressions;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Diagnostics;

/// <summary>
/// First-party analytics (owner decision 2026-10-16): page views, sessions, ads, queue and match events, batched every 10 s
/// (or 20 events, or when the app goes to the background) to /api/telemetry. Cookie-free; the campaign a player arrived from
/// (?c= link or deep link) is remembered once and sent with every batch.
/// </summary>
public sealed partial class Telemetry : IAsyncDisposable
{
    private const string CampaignKey = "ttt.campaign";
    private readonly TttApiClient _api;
    private readonly IPlatformInfo _platform;
    private readonly IKeyValueStore _kv;
    private readonly Lock _gate = new();
    private readonly List<TelemetryEventDto> _pending = [];
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(10));
    private string? _campaign;
    private bool _started;

    /// <summary>Creates the service and hooks realtime match events.</summary>
    public Telemetry(TttApiClient api, IPlatformInfo platform, IKeyValueStore kv, IRealtimeTransport rt, IAppLifecycle? lifecycle = null)
    {
        _api = api; _platform = platform; _kv = kv;
        rt.MatchFoundReceived += f => Track(TelemetryKinds.Match, "start", ("tc", f.TimeControl.ToString()), ("mode", f.Mode.ToString()));
        rt.PostGameSummaryReceived += s => Track(TelemetryKinds.Match, "end", ("result", s.YourResult.ToString()), ("reason", s.Result.Reason.ToString()));
        if (lifecycle is not null)
        {
            lifecycle.Suspended += () => _ = FlushAsync();
            lifecycle.Resumed += () => Track(TelemetryKinds.Session, "resume");
        }
    }

    /// <summary>The UI language (set by the localizer) sent with every batch.</summary>
    public string? Locale { get; set; }

    /// <summary>Starts the session (idempotent) and the flush loop.</summary>
    public async Task StartAsync()
    {
        lock (_gate) { if (_started) return; _started = true; }
        _campaign = await _kv.GetAsync(CampaignKey);
        Track(TelemetryKinds.Session, "start");
        _ = LoopAsync();
    }

    /// <summary>First touch wins: a campaign from a ?c= link or a deep link.</summary>
    public async Task SetCampaignAsync(string? slug)
    {
        if (slug is null || !SlugRegex().IsMatch(slug) || _campaign is not null) return;
        _campaign = slug;
        await _kv.SetAsync(CampaignKey, slug);
        Track(TelemetryKinds.Session, "campaign", ("slug", slug));
    }

    /// <summary>Page view with ids replaced (/match/{guid} → /match/:id) so pages group.</summary>
    public void PageView(string path) => Track(TelemetryKinds.PageView, IdRegex().Replace(path, ":id"));

    /// <summary>Queues an event.</summary>
    public void Track(string kind, string name, params (string Key, string Value)[] props)
    {
        var e = new TelemetryEventDto(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind, name, props.Length == 0 ? null : props.ToDictionary(p => p.Key, p => p.Value));
        bool flush;
        lock (_gate) { _pending.Add(e); flush = _pending.Count >= 20; }
        if (flush) _ = FlushAsync();
    }

    /// <summary>Sends what is queued (dropped on failure: analytics must never pile up).</summary>
    public async Task FlushAsync()
    {
        TelemetryEventDto[] batch;
        lock (_gate) { if (_pending.Count == 0) return; batch = [.. _pending]; _pending.Clear(); }
        try
        {
            await _api.SendTelemetryAsync(new TelemetryBatchDto(_session, _platform.Platform.ToString(), _platform.AppVersion,
                Locale ?? CultureInfo.CurrentUICulture.Name, _campaign, batch));
        }
        catch (Exception ex) when (ex is TttApiException or HttpRequestException or TaskCanceledException or InvalidOperationException) { }
    }

    private async Task LoopAsync()
    {
        try { while (await _timer.WaitForNextTickAsync()) await FlushAsync(); }
        catch (ObjectDisposedException) { }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() { _timer.Dispose(); await FlushAsync(); }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|(?<=/c/)[A-Za-z0-9]+|(?<=/u/)[^/]+", RegexOptions.None, 200)]
    private static partial Regex IdRegex();
    [GeneratedRegex("^[a-z0-9-]{2,40}$", RegexOptions.None, 200)]
    private static partial Regex SlugRegex();
}
