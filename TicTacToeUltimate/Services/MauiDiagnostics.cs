using System.Text.Json;
using TicTacToeUltimate.Client.Diagnostics;

namespace TicTacToeUltimate.Services;

/// <summary>Device model and OS from MAUI (the web view's user agent hides them).</summary>
public sealed class MauiDeviceDescription : IDeviceDescription
{
    /// <inheritdoc />
    public ValueTask<DeviceDescriptionDto> DescribeAsync()
    {
        var d = DeviceDisplay.Current.MainDisplayInfo;
        return ValueTask.FromResult(new DeviceDescriptionDto($"{DeviceInfo.Platform} {DeviceInfo.VersionString}", $"{DeviceInfo.Manufacturer} {DeviceInfo.Model}",
            "BlazorWebView", $"{d.Width:0}x{d.Height:0}@{d.Density:0.#}", System.Globalization.CultureInfo.CurrentUICulture.Name));
    }
}

/// <summary>
/// Native crashes kill the process before an HTTP request can finish: the crash is written to a file synchronously and sent
/// on the next launch (owner decision 2026-10-16: crash reports on every platform).
/// </summary>
public static class NativeCrashFile
{
    private static string PathFor => System.IO.Path.Combine(FileSystem.AppDataDirectory, "pending-crash.json");

    /// <summary>Hooks the platform's last-chance handlers.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Save(ex); };
#if ANDROID
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Save(e.Exception);
#endif
#if IOS || MACCATALYST
        ObjCRuntime.Runtime.MarshalManagedException += (_, e) => { if (e.Exception is { } ex) Save(ex); };
#endif
    }

    private static void Save(Exception ex)
    {
        try
        {
            File.WriteAllText(PathFor, JsonSerializer.Serialize(new Pending(ex.GetType().FullName ?? "Exception", ex.Message, ex.ToString(),
                CrashReporter.ComponentOf(ex))));
        }
        catch (Exception) { /* nothing more we can do while crashing */ }
    }

    /// <summary>Sends a crash saved by the previous run, if any.</summary>
    public static async Task SendPendingAsync(CrashReporter reporter)
    {
        try
        {
            if (!File.Exists(PathFor)) return;
            var p = JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(PathFor));
            File.Delete(PathFor);
            if (p is not null) await reporter.ReportCrashAsync("native:" + p.Type, p.Message, p.Stack, p.Component);
        }
        catch (Exception) { }
    }

    private sealed record Pending(string Type, string Message, string Stack, string? Component);
}
