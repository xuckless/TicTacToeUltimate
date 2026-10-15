using System.Collections.Concurrent;
using TicTacToeUltimate.Engine.Analysis;

namespace TicTacToeUltimate.Engine.Tests;

internal static class TestSupport
{
    private static readonly ConcurrentDictionary<string, Lazy<Tablebase>> Tables = new();

    public static string RepoRoot { get; } = FindRoot();

    public static string SpecPath(params string[] parts) => Path.Combine([RepoRoot, "spec", .. parts]);

    public static Tablebase Table(string rulesId)
        => Tables.GetOrAdd(rulesId, id => new Lazy<Tablebase>(() =>
        {
            Assert.True(RuleSet.TryParse(id, out var r), id);
            return Tablebase.Build(r, 5_000_000, CancellationToken.None);
        })).Value;

    public static RuleSet Parse(string id)
    {
        Assert.True(RuleSet.TryParse(id, out var r), id);
        return r;
    }

    public static GameState Play(RuleSet r, string actions) => Rules.Replay(r, Notation.Parse(actions));

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TicTacToeUltimate.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
