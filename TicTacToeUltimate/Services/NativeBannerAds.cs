using TicTacToeUltimate.Client.Abstractions;

namespace TicTacToeUltimate.Services;

/// <summary>Mobile: the banner is a native AdMob view in MainPage, so every Blazor <c>AdSlot</c> renders nothing.</summary>
public sealed class NativeBannerAds : IBannerAds
{
    /// <inheritdoc />
    public bool Has(AdSlotKind kind) => false;
    /// <inheritdoc />
    public ValueTask RenderAsync(object host, AdSlotKind kind) => ValueTask.CompletedTask;
}
