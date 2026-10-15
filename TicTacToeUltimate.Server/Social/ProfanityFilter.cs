using System.Text;
using System.Text.RegularExpressions;

namespace TicTacToeUltimate.Server.Social;

/// <summary>Blocked-word matching (usernames) and masking (chat). Words come from Social/Data/blocked-words.txt when present.</summary>
public sealed class ProfanityFilter
{
    internal static readonly string[] BuiltIn =
        ["fuck", "shit", "bitch", "cunt", "nigger", "nigga", "faggot", "asshole", "dickhead", "pussy", "whore", "slut", "retard", "nazi", "hitler"];

    private readonly string[] _words;
    private readonly Regex? _mask;

    public ProfanityFilter() : this(Load()) { }

    public ProfanityFilter(IEnumerable<string> words)
    {
        _words = words.Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length > 0 && !w.StartsWith('#')).Distinct().ToArray();
        if (_words.Length > 0)
            _mask = new Regex(string.Join("|", _words.Select(Regex.Escape)), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    }

    private static IEnumerable<string> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Social", "Data", "blocked-words.txt");
        try { if (File.Exists(path)) return File.ReadAllLines(path); }
        catch (IOException) { }
        return BuiltIn;
    }

    /// <summary>True when the text (stripped of '_' and digits) contains a blocked word.</summary>
    public bool ContainsBlocked(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant()) if (ch != '_' && !char.IsDigit(ch)) sb.Append(ch);
        var s = sb.ToString();
        return _words.Any(w => s.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>Replaces each blocked word with '*' of equal length.</summary>
    public string Mask(string text) => _mask is null ? text : _mask.Replace(text, m => new string('*', m.Length));
}
