using System.Globalization;

namespace TicTacToeUltimate.Admin.Components;

public static class Fmt
{
    public static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
    public static string Pct(double v) => (v * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => (b / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (b / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (b / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => b + " B",
    };
    public static string Duration(long seconds) => seconds >= 86400 ? $"{seconds / 86400}d {seconds % 86400 / 3600}h" : seconds >= 3600 ? $"{seconds / 3600}h {seconds % 3600 / 60}m" : $"{seconds / 60}m";

    /// <summary>Stat points summed per day (all dims together).</summary>
    public static Dictionary<DateOnly, long> PerDay(IEnumerable<TicTacToeUltimate.Contracts.Admin.StatPointDto>? points) =>
        (points ?? []).GroupBy(p => p.Day).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));

    /// <summary>Stat points summed per dim over the whole range, largest first.</summary>
    public static List<(string Dim, long Value)> PerDim(IEnumerable<TicTacToeUltimate.Contracts.Admin.StatPointDto>? points) =>
        (points ?? []).GroupBy(p => p.Dim).Select(g => (g.Key, g.Sum(p => p.Value))).OrderByDescending(x => x.Item2).ToList();

    /// <summary>Validated categorical slots in fixed order; past three, everything folds into "Other".</summary>
    public static string Slot(int i) => i switch { 0 => "--s1", 1 => "--s2", 2 => "--s3", _ => "--s-other" };
}
