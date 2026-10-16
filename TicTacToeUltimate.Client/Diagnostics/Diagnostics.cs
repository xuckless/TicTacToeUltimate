using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Diagnostics;

/// <summary>The last few things the player did (navigation, taps, network): attached to crash and bug reports. No chat text.</summary>
public sealed class DiagnosticsTrail
{
    private const int Max = 40;
    private readonly ConcurrentQueue<TrailEntryDto> _items = new();

    /// <summary>Adds an entry (kind: nav | tap | net | hub | info).</summary>
    public void Add(string kind, string text)
    {
        _items.Enqueue(new TrailEntryDto(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind, text.Length > 200 ? text[..200] : text));
        while (_items.Count > Max && _items.TryDequeue(out _)) { }
    }

    /// <summary>Oldest first.</summary>
    public TrailEntryDto[] Snapshot() => [.. _items];
}

/// <summary>Keeps the last 100 log lines (Information and above) for crash reports.</summary>
public sealed class RingLoggerProvider : ILoggerProvider
{
    private const int Max = 100;
    private readonly ConcurrentQueue<LogLineDto> _lines = new();

    /// <summary>Oldest first.</summary>
    public LogLineDto[] Snapshot() => [.. _lines];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RingLogger(this, categoryName);
    /// <inheritdoc />
    public void Dispose() { }

    private void Add(LogLineDto line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > Max && _lines.TryDequeue(out _)) { }
    }

    private sealed class RingLogger(RingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += $" | {exception.GetType().Name}: {exception.Message}";
            owner.Add(new LogLineDto(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), logLevel.ToString(), category.Split('.').Last(), msg.Length > 1000 ? msg[..1000] : msg));
        }
    }
}

/// <summary>The current app route (Shared provides it from NavigationManager).</summary>
public interface ICurrentRoute { string? Route { get; } }

/// <summary>OS / device / browser / screen for reports. Hosts provide it (web: JS, MAUI: DeviceInfo).</summary>
public interface IDeviceDescription { ValueTask<DeviceDescriptionDto> DescribeAsync(); }
public sealed record DeviceDescriptionDto(string? Os, string? Device, string? Browser, string? Screen, string? Locale);

/// <summary>Captures the current screen as PNG (bug reports). Web: html2canvas; MAUI: platform screenshot.</summary>
public interface IScreenshotProvider { Task<string?> CaptureBase64PngAsync(); }

/// <summary>
/// Sends crashes (automatic) and bug reports (player) to /api/diagnostics/report with the trail, recent logs, device info and
/// the failing component. Reports that can't be sent are kept (max 5) and sent on the next launch.
/// </summary>
public sealed class CrashReporter(TttApiClient api, DiagnosticsTrail trail, RingLoggerProvider logs, IPlatformInfo platform,
    IKeyValueStore kv, ICurrentRoute? route = null, IDeviceDescription? device = null)
{
    private const string QueueKey = "ttt.crashq";
    private readonly ConcurrentDictionary<string, long> _recent = new();

    /// <summary>Last component that failed (for the "Send report" button on the crash panel).</summary>
    public CrashReportDto? LastCrash { get; private set; }

    /// <summary>Reports an exception. Same error within a minute is sent once.</summary>
    public async Task ReportCrashAsync(Exception ex, string? component = null)
    {
        var key = ex.GetType().FullName + "|" + ex.Message;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_recent.TryGetValue(key, out var at) && now - at < 60_000) return;
        _recent[key] = now;
        var dto = await BuildAsync("crash", ex, component ?? ComponentOf(ex), null, null);
        LastCrash = dto;
        await SendAsync(dto);
    }

    /// <summary>A crash report the platform layer captured as text (JS errors, native crash files).</summary>
    public async Task ReportCrashAsync(string type, string message, string? stack, string? component = null)
    {
        var d = await DescribeAsync();
        await SendAsync(new CrashReportDto("crash", platform.Platform.ToString(), platform.AppVersion, d.Os, d.Device, d.Browser, d.Locale, d.Screen,
            Route(), component, type, message, stack, null, logs.Snapshot(), trail.Snapshot(), null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    /// <summary>Player bug report (optional screenshot and the last crash, if any).</summary>
    public async Task<bool> ReportBugAsync(string description, string? screenshotBase64, bool attachLastCrash)
    {
        var dto = await BuildAsync("bug", null, null, description, screenshotBase64);
        if (attachLastCrash && LastCrash is { } c)
            dto = dto with { ExceptionType = c.ExceptionType, Message = c.Message, Stack = c.Stack, Component = c.Component, InnerExceptions = c.InnerExceptions };
        return await SendAsync(dto, queueOnFailure: false);
    }

    /// <summary>Sends reports saved while offline.</summary>
    public async Task FlushAsync()
    {
        var pending = await LoadQueueAsync();
        if (pending.Count == 0) return;
        await kv.RemoveAsync(QueueKey);
        foreach (var dto in pending) await SendAsync(dto);
    }

    /// <summary>Hooks AppDomain and TaskScheduler (call once at startup).</summary>
    public void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) _ = ReportCrashAsync(ex); };
        TaskScheduler.UnobservedTaskException += (_, e) => { _ = ReportCrashAsync(e.Exception.GetBaseException()); e.SetObserved(); };
    }

    private async Task<CrashReportDto> BuildAsync(string kind, Exception? ex, string? component, string? description, string? screenshot)
    {
        var d = await DescribeAsync();
        var inner = new List<string>();
        for (var e = ex?.InnerException; e is not null && inner.Count < 5; e = e.InnerException) inner.Add($"{e.GetType().FullName}: {e.Message}\n{e.StackTrace}");
        return new CrashReportDto(kind, platform.Platform.ToString(), platform.AppVersion, d.Os, d.Device, d.Browser, d.Locale, d.Screen,
            Route(), component, ex?.GetType().FullName, ex?.Message, ex?.ToString(), inner.ToArray(), logs.Snapshot(), trail.Snapshot(),
            description, screenshot, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private async Task<DeviceDescriptionDto> DescribeAsync()
    {
        try { if (device is not null) return await device.DescribeAsync(); }
        catch (Exception) { }
        return new DeviceDescriptionDto(Environment.OSVersion.ToString(), null, null, null, System.Globalization.CultureInfo.CurrentUICulture.Name);
    }

    private string? Route() => route?.Route;

    private async Task<bool> SendAsync(CrashReportDto dto, bool queueOnFailure = true)
    {
        try { await api.ReportDiagnosticsAsync(dto); return true; }
        catch (Exception e) when (e is TttApiException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            if (queueOnFailure)
            {
                var q = await LoadQueueAsync();
                q.Add(dto with { ScreenshotPngBase64 = null });
                await kv.SetAsync(QueueKey, JsonSerializer.Serialize(q.TakeLast(5).ToList(), ClientJson.Options));
            }
            return false;
        }
    }

    private async Task<List<CrashReportDto>> LoadQueueAsync()
    {
        try { return await kv.GetAsync(QueueKey) is { Length: > 0 } raw ? JsonSerializer.Deserialize<List<CrashReportDto>>(raw, ClientJson.Options) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    /// <summary>The Razor component whose code threw: the first stack frame whose type (or its enclosing type) is a component.</summary>
    public static string? ComponentOf(Exception ex)
    {
        try
        {
            foreach (var frame in new StackTrace(ex, false).GetFrames())
            {
                for (var t = frame.GetMethod()?.DeclaringType; t is not null; t = t.DeclaringType)
                    if (t.Namespace?.StartsWith("TicTacToeUltimate", StringComparison.Ordinal) == true
                        && t.GetInterfaces().Any(i => i.FullName == "Microsoft.AspNetCore.Components.IComponent"))
                        return t.FullName;
            }
        }
        catch (Exception) { }
        // Trimmed builds may lack frame metadata: fall back to the text of the stack.
        var line = ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("TicTacToeUltimate.Shared", StringComparison.Ordinal));
        return line?.Trim().Split('(')[0].Replace("at ", "", StringComparison.Ordinal);
    }
}
