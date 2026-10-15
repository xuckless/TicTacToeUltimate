using System.Globalization;
using System.Text.RegularExpressions;
using TicTacToeUltimate.Engine;

namespace TicTacToeUltimate.Server.Persistence;

/// <summary>Parses canonical ruleset ids ("n3k3-fifo7-v1-w0-none"). Local to WP3 so it works before the engine parser lands.</summary>
public static partial class RuleIds
{
    [GeneratedRegex(@"^n(\d+)k(\d+)-(fifo(\d+)|cap(\d+)|none)-v([01])-w([01])-(none|pie|ban)$")]
    private static partial Regex Pattern();

    /// <summary>Parses an id; false when malformed.</summary>
    public static bool TryParse(string? id, out RuleSet rules)
    {
        rules = RuleSet.Default;
        if (id is null) return false;
        var m = Pattern().Match(id);
        if (!m.Success) return false;
        int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
        var n = I(m.Groups[1].Value);
        var k = I(m.Groups[2].Value);
        var (removal, window) = m.Groups[4].Success ? (RemovalKind.GlobalFifo, I(m.Groups[4].Value))
            : m.Groups[5].Success ? (RemovalKind.PerPlayerCap, I(m.Groups[5].Value)) : (RemovalKind.None, 0);
        var opening = m.Groups[8].Value switch { "pie" => OpeningRule.PieSwap, "ban" => OpeningRule.BanCenter, _ => OpeningRule.None };
        rules = new RuleSet(n, k, removal, window, m.Groups[6].Value == "1", m.Groups[7].Value == "1", opening);
        return true;
    }

    /// <summary>Parses or falls back to <see cref="RuleSet.Default"/>.</summary>
    public static RuleSet ParseOrDefault(string? id) => TryParse(id, out var r) ? r : RuleSet.Default;
}
