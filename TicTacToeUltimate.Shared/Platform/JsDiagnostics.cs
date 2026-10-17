using TicTacToeUltimate.Client.Diagnostics;

namespace TicTacToeUltimate.Shared.Platform;

/// <summary>The current route for crash reports, kept up to date by <see cref="Components.Diagnostics.DiagnosticsHost"/>.</summary>
public sealed class CurrentRoute : ICurrentRoute
{
    /// <inheritdoc />
    public string? Route { get; set; }
}

/// <summary>Browser / web view description through ttt.js.</summary>
public sealed class JsDeviceDescription(TttJsModule js) : IDeviceDescription
{
    /// <inheritdoc />
    public async ValueTask<DeviceDescriptionDto> DescribeAsync() =>
        await js.InvokeAsync<DeviceDescriptionDto>("diagDescribe") ?? new DeviceDescriptionDto(null, null, null, null, null);
}

/// <summary>Screenshot of the page with html2canvas (web and the MAUI web view alike).</summary>
public sealed class JsScreenshotProvider(TttJsModule js) : IScreenshotProvider
{
    /// <inheritdoc />
    public async Task<string?> CaptureBase64PngAsync() => await js.InvokeAsync<string?>("diagScreenshot");
}
