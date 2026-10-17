namespace TicTacToeUltimate.Shared.Services;

/// <summary>One mode's explanation, shown behind a (?) button instead of long text in menus.
/// Text is looked up at render time ("modeGuide.&lt;key&gt;.summary", "modeGuide.&lt;key&gt;.point1"…) so language switches apply.</summary>
public sealed record ModeGuideEntry(string Key, string TitleKey, string Icon, string Accent, int PointCount)
{
    public string Title => L.T(TitleKey);
    public string Summary => L.T($"modeGuide.{Key}.summary");
    public string[] Points => Enumerable.Range(1, PointCount).Select(i => L.T($"modeGuide.{Key}.point{i}")).ToArray();
}

/// <summary>Detailed explanations for every mode and option. Menus show a one-line subtitle; the (?) opens this.</summary>
public static class ModeGuide
{
    private static readonly Dictionary<string, ModeGuideEntry> Entries = new ModeGuideEntry[]
    {
        new("ranked", "statGrid.ranked", "ranked", "#7442ef", 6),
        new("daily", "friendRow.daily", "calendar", "#ff8a2a", 4),
        new("casual", "modeGrid.casual", "casual", "#2f9be0", 4),
        new("arena", "modeGrid.arena", "arena", "#e0457b", 3),
        new("oldschool", "modeGrid.old_school", "play", "#1fb5a0", 3),
        new("unlimited", "tc.unlimited", "unlimited", "#8a6cff", 3),
        new("passplay", "localSetup.pass_play", "passplay", "#2ec27e", 4),
        new("classic", "modeGuide.classic.title", "casual", "#2f9be0", 3),
        new("legacy7", "modeGuide.legacy7.title", "history", "#8a6cff", 2),
        new("pie", "localSetup.pie_rule", "rematch", "#7442ef", 2),
        new("guided", "settings.guided_mode", "guided", "#ffc63d", 2),
    }.ToDictionary(e => e.Key);

    /// <summary>Entry by key, or null.</summary>
    public static ModeGuideEntry? Get(string key) => Entries.GetValueOrDefault(key);
}
