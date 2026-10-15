// RulesTool: gen | gen --check | solve <rulesId>
using System.Diagnostics;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Analysis;
using TicTacToeUltimate.RulesTool;

var command = args.Length > 0 ? args[0] : "";
var stdout = Console.Out;
var stderr = Console.Error;

switch (command)
{
    case "gen":
    {
        bool check = args.Contains("--check");
        var root = FindRoot();
        if (root is null)
        {
            stderr.WriteLine("Repository root (TicTacToeUltimate.sln) not found.");
            return 2;
        }
        stdout.WriteLine(check ? "Checking generated specs..." : "Generating specs...");
        var files = Generator.GenerateAll(stdout);
        int diffs = 0;
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            if (existing == content) continue;
            if (check)
            {
                diffs++;
                stderr.WriteLine($"DIFF {rel}");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
                stdout.WriteLine($"wrote {rel}");
            }
        }
        if (check && diffs > 0)
        {
            stderr.WriteLine($"{diffs} file(s) out of date. Run: dotnet run --project tools/TicTacToeUltimate.RulesTool -- gen");
            return 1;
        }
        stdout.WriteLine("OK");
        return 0;
    }
    case "solve":
    {
        if (args.Length < 2 || !RuleSet.TryParse(args[1], out var rules))
        {
            stderr.WriteLine("Usage: solve <rulesId>   e.g. solve n3k3-fifo7-v1-w0-none");
            return 2;
        }
        if (!Tablebase.CanBuild(rules))
        {
            stderr.WriteLine($"{rules.Id}: state key does not fit 64 bits.");
            return 1;
        }
        var sw = Stopwatch.StartNew();
        var tb = Tablebase.Build(rules, long.MaxValue, CancellationToken.None);
        var start = Rules.Initial(rules);
        var v = tb.Evaluate(start);
        stdout.WriteLine($"{rules.Id}: reachable {tb.StateCount}, nonTerminal {tb.NonTerminalCount}, start {v.Value} in {v.Distance} (X to move), built in {sw.ElapsedMilliseconds} ms");
        bool anyDrawOpening = false;
        foreach (var a in Rules.LegalActions(start))
        {
            var child = Rules.Apply(start, a).State;
            var cv = tb.Evaluate(child);
            if (cv.Value == Value.Draw) anyDrawOpening = true;
            stdout.WriteLine($"  opening {Notation.Token(a)}: {cv.Value} in {cv.Distance} for O");
        }
        stdout.WriteLine($"  pie: {(anyDrawOpening ? "draw" : "chooser wins")}");
        return 0;
    }
    default:
        stderr.WriteLine("Usage: gen [--check] | solve <rulesId>");
        return 2;
}

static string? FindRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TicTacToeUltimate.sln"))) return dir.FullName;
    }
    return null;
}
