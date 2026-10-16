namespace TicTacToeUltimate.Platforms.Android;

/// <summary>The Blazor web view, so the activity's back handling can walk the app's history.</summary>
public static class BackNavigation
{
    public static global::Android.Webkit.WebView? WebView { get; set; }
}
