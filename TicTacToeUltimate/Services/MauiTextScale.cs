using TicTacToeUltimate.Client.Abstractions;

namespace TicTacToeUltimate.Services;

/// <summary>Reads the system text size so the web view's rem-based text follows it (accessibility).</summary>
public sealed class MauiTextScale : ITextScaleProvider
{
    /// <inheritdoc />
    public double Scale
    {
        get
        {
#if IOS || MACCATALYST
            return UIKit.UIFontMetrics.DefaultMetrics.GetScaledValue(16) / 16.0;
#elif ANDROID
            return Android.App.Application.Context.Resources?.Configuration?.FontScale ?? 1f;
#else
            return 1;
#endif
        }
    }
}
