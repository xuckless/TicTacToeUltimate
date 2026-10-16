using TicTacToeUltimate.Client.Abstractions;

namespace TicTacToeUltimate.Services;

/// <summary>Mobile: the banner is a native AdMob view in MainPage, so the Blazor <c>AdSlot</c> renders nothing (BannerSlot null).</summary>
public sealed class NativeBannerAds : IBannerAds
{
    /// <inheritdoc />
    public string? BannerSlot => null;
    /// <inheritdoc />
    public ValueTask RenderBannerAsync(object host) => ValueTask.CompletedTask;
}
