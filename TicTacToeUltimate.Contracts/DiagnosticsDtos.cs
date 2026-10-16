namespace TicTacToeUltimate.Contracts;

/// <summary>One line of the recent-actions trail (navigation, tap, network). Never contains chat text.</summary>
public sealed record TrailEntryDto(long AtMs, string Kind /* nav | tap | net | hub | info */, string Text);

/// <summary>One recent log line (Information and above).</summary>
public sealed record LogLineDto(long AtMs, string Level, string Category, string Message);

/// <summary>
/// POST /api/diagnostics/report: a crash (captured automatically) or a bug the player reported. Owner decision 2026-10-16:
/// user id + username (from the token), screenshot (bugs, optional), recent actions, device and app version, logs, exception
/// and stack trace including the Blazor component that failed.
/// </summary>
public sealed record CrashReportDto(
    string Kind /* crash | bug */,
    string Platform, string AppVersion, string? Os, string? Device, string? Browser, string? Locale, string? Screen,
    string? Route, string? Component,
    string? ExceptionType, string? Message, string? Stack, string[]? InnerExceptions,
    LogLineDto[]? Logs, TrailEntryDto[]? Trail,
    string? Description,
    string? ScreenshotPngBase64,
    long AtMs);

/// <summary>Accepted report: its group (crashes with the same fingerprint share one).</summary>
public sealed record CrashReportResultDto(string Fingerprint);
