using System.Diagnostics;
using System.Globalization;
using System.Text;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Analysis;
using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.RulesTool;

/// <summary>Generates spec/rules/vectors/*.json and spec/clock/schedule.json. Output is byte-deterministic.</summary>
public static class Generator
{
    /// <summary>Vector files (§7.2).</summary>
    public static readonly string[] VectorIds =
    [
        "n3k3-fifo7-v1-w0-none",
        "n3k3-fifo7-v1-w0-pie",
        "n3k3-fifo7-v1-w1-none",
        "n3k3-fifo7-v1-w1-pie",
        "n3k3-fifo7-v1-w1-ban",
        "n3k3-fifo7-v0-w1-none",
        "n3k3-fifo5-v1-w1-none",
        "n3k3-fifo6-v1-w1-none",
        "n3k3-fifo9-v0-w1-none",
        "n3k3-cap3-v1-w1-none",
        "n4k3-fifo8-v1-w1-none",
    ];

    /// <summary>Rulesets that get no tablebase block.</summary>
    public static readonly HashSet<string> NoTablebase = ["n4k3-fifo8-v1-w1-none"];

    /// <summary>Playouts per vector file.</summary>
    public const int PlayoutCount = 200;
    /// <summary>Playout length cap (draws by repetition never end).</summary>
    public const int PlayoutMaxActions = 60;
    /// <summary>First playout seed; playout i uses seed PlayoutSeedBase + i.</summary>
    public const ulong PlayoutSeedBase = 1000;

    /// <summary>Relative path (forward slashes) to file content.</summary>
    public static SortedDictionary<string, string> GenerateAll(TextWriter? log = null)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in VectorIds)
        {
            var sw = Stopwatch.StartNew();
            files[$"spec/rules/vectors/{id}.json"] = Vectors(id);
            log?.WriteLine($"  {id}: {sw.ElapsedMilliseconds} ms");
        }
        files["spec/clock/schedule.json"] = Schedule();
        return files;
    }

    /// <summary>Deterministic random playout as used in the vector files.</summary>
    public static (List<GameAction> Actions, GameState Final) Playout(RuleSet rules, ulong seed)
    {
        var rng = new SplitMix64(seed);
        var s = Rules.Initial(rules);
        var actions = new List<GameAction>();
        while (s.Phase != GamePhase.Ended && actions.Count < PlayoutMaxActions)
        {
            var legal = Rules.LegalActions(s);
            var a = legal[rng.NextInt(legal.Length)];
            actions.Add(a);
            s = Rules.Apply(s, a).State;
        }
        return (actions, s);
    }

    private static List<(string Name, string Actions)> CasesFor(RuleSet r)
    {
        var list = new List<(string, string)>
        {
            ("empty", ""),
            ("window-full-o-to-move", "4,0,8,2,6,3,5"),
            ("own-vanishing-piece-does-not-count", "4,0,8,2,6,3,5,1"),
            ("ghost-excluded-x", "4,0,8,2,6,3,5,1,7"),
            ("early-win-row", "0,4,1,8,2"),
            ("column-win-o", "0,1,3,4,8,7"),
            ("window-boundary", "4,0,8,2,6,3,5,7"),
            ("vacated-cell-replayed", "4,0,8,2,6,3,5,4"),
            ("occupied", "4,4"),
            ("out-of-bounds-high", r.CellCount.ToString(CultureInfo.InvariantCulture)),
            ("out-of-bounds-negative", "-1"),
            ("wrong-action-kind-swap-at-start", "swap"),
            ("wrong-action-kind-keep-at-start", "keep"),
            ("game-over", "0,4,1,8,2,3"),
        };
        if (r.Opening == OpeningRule.PieSwap)
        {
            list.Add(("pie-decision-pending", "4"));
            list.Add(("pie-swap", "4,swap"));
            list.Add(("pie-keep", "4,keep"));
            list.Add(("pie-swap-then-place", "4,swap,0"));
            list.Add(("pie-keep-then-place", "4,keep,0"));
            list.Add(("pie-place-during-decision", "4,0"));
            list.Add(("pie-double-decision", "4,swap,swap"));
            list.Add(("pie-swap-window-full", "4,swap,0,8,2,6,3,5,1"));
        }
        if (r.Opening == OpeningRule.BanCenter)
        {
            list.Add(("ban-center-opening", "4"));
            list.Add(("ban-center-later-allowed", "0,4"));
        }
        if (r.Removal == RemovalKind.PerPlayerCap)
        {
            list.Add(("cap-removal", "0,4,1,5,8,6,2"));
            list.Add(("cap-removal-both", "0,4,1,5,8,6,2,3"));
        }
        if (r.BoardSize > r.WinLength)
        {
            list.Add(("k-less-than-n-win", "0,4,1,5,2"));
            list.Add(("k-less-than-n-diagonal", "0,1,5,2,10"));
        }
        // First seeded playout that ends in a stuck-board draw (only some rulesets have one).
        for (ulong seed = 1; seed <= 5000; seed++)
        {
            var (acts, final) = Playout(r, seed);
            if (final.Outcome == Outcome.Draw)
            {
                list.Add(("stuck-draw", Notation.Format(acts)));
                break;
            }
        }
        return list;
    }

    private static string Vectors(string id)
    {
        if (!RuleSet.TryParse(id, out var r)) throw new InvalidOperationException($"Bad ruleset id {id}");
        var sb = new StringBuilder();
        sb.Append("{\n  \"schema\": 1,\n  \"rules\": ").Append(RulesJson(r)).Append(",\n  \"cases\": [\n");
        var cases = CasesFor(r);
        for (int i = 0; i < cases.Count; i++)
        {
            sb.Append("    ").Append(CaseJson(r, cases[i].Name, cases[i].Actions));
            sb.Append(i < cases.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("  ],\n  \"playouts\": [\n");
        for (int i = 0; i < PlayoutCount; i++)
        {
            ulong seed = PlayoutSeedBase + (ulong)i;
            var (acts, final) = Playout(r, seed);
            sb.Append("    {\"seed\": ").Append(Str(seed.ToString(CultureInfo.InvariantCulture)))
              .Append(", \"actions\": ").Append(Str(Notation.Format(acts)))
              .Append(", \"finalBoard\": ").Append(Str(Notation.Board(final)))
              .Append(", \"outcome\": ").Append(Str(final.Outcome.ToString())).Append('}');
            sb.Append(i < PlayoutCount - 1 ? ",\n" : "\n");
        }
        sb.Append("  ]");
        if (!NoTablebase.Contains(id))
        {
            var tb = Tablebase.Build(r, 5_000_000, CancellationToken.None);
            sb.Append(",\n  \"tablebase\": {\"reachable\": ").Append(Int(tb.StateCount))
              .Append(", \"nonTerminal\": ").Append(Int(tb.NonTerminalCount)).Append(", \"positions\": [\n");
            var lines = new List<string>();
            var start = Rules.Initial(r);
            lines.Add(PositionJson(tb, r, ""));
            foreach (var a in Rules.LegalActions(start)) lines.Add(PositionJson(tb, r, Notation.Token(a)));
            for (int i = 0; i < lines.Count; i++)
                sb.Append("    ").Append(lines[i]).Append(i < lines.Count - 1 ? ",\n" : "\n");
            sb.Append("  ]}");
        }
        sb.Append("\n}\n");
        return sb.ToString();
    }

    private static string PositionJson(Tablebase tb, RuleSet r, string actions)
    {
        var s = Rules.Replay(r, Notation.Parse(actions));
        var v = tb.Evaluate(s);
        return $"{{\"actions\": {Str(actions)}, \"value\": {Str(v.Value.ToString())}, \"distance\": {Int(v.Distance)}}}";
    }

    private static string CaseJson(RuleSet r, string name, string actionsText)
    {
        var actions = Notation.Parse(actionsText);
        var s = Rules.Initial(r);
        for (int i = 0; i < actions.Length; i++)
        {
            var res = Rules.Apply(s, actions[i]);
            if (!res.Ok)
                return $"{{\"name\": {Str(name)}, \"actions\": {Str(actionsText)}, \"error\": {{\"atIndex\": {Int(i)}, \"code\": {Str(res.Error.ToString())}}}}}";
            s = res.State;
        }
        var e = new StringBuilder();
        e.Append("{\"ply\": ").Append(Int(s.Ply))
         .Append(", \"placements\": ").Append(Int(s.Placements))
         .Append(", \"phase\": ").Append(Str(s.Phase.ToString()))
         .Append(", \"toMove\": ").Append(Str(s.ToMove.ToString()))
         .Append(", \"outcome\": ").Append(Str(s.Outcome.ToString()))
         .Append(", \"board\": ").Append(Str(Notation.Board(s)))
         .Append(", \"legal\": ").Append(IntArray(Rules.LegalCells(s)))
         .Append(", \"nextToVanish\": ").Append(NullableInt(Rules.NextToVanish(s)))
         .Append(", \"winLine\": ").Append(s.WinLine.IsEmpty ? "null" : IntArray(s.WinLine))
         .Append(", \"swapped\": ").Append(s.Swapped ? "true" : "false")
         .Append(", \"lastVanished\": ").Append(NullableInt(s.LastVanishedCell))
         .Append('}');
        return $"{{\"name\": {Str(name)}, \"actions\": {Str(actionsText)}, \"expect\": {e}}}";
    }

    /// <summary>RuleSetDto JSON (camelCase, enums as strings).</summary>
    public static string RulesJson(RuleSet r)
        => $"{{\"id\": {Str(r.Id)}, \"boardSize\": {Int(r.BoardSize)}, \"winLength\": {Int(r.WinLength)}, \"removal\": {Str(r.Removal.ToString())}, " +
           $"\"window\": {Int(r.Window)}, \"vacatedCellPlayable\": {Bool(r.VacatedCellPlayable)}, \"vanishingPieceCanWin\": {Bool(r.VanishingPieceCanWin)}, " +
           $"\"opening\": {Str(r.Opening.ToString())}}}";

    /// <summary>Clock oracle points (§0.3) in ms.</summary>
    public static readonly (TimeControl Tc, long Ms)[] ClockOracles =
    [
        (TimeControl.Ranked, 0), (TimeControl.Ranked, 59_999), (TimeControl.Ranked, 60_000), (TimeControl.Ranked, 71_999),
        (TimeControl.Ranked, 72_000), (TimeControl.Ranked, 84_000), (TimeControl.Ranked, 91_000), (TimeControl.Ranked, 96_000),
        (TimeControl.Ranked, 156_000), (TimeControl.Ranked, 3_600_000),
        (TimeControl.CasualX2, 0), (TimeControl.CasualX2, 119_999), (TimeControl.CasualX2, 120_000), (TimeControl.CasualX2, 144_000),
        (TimeControl.CasualX2, 312_000),
        (TimeControl.CasualX4, 0), (TimeControl.CasualX4, 240_000), (TimeControl.CasualX4, 624_000),
        (TimeControl.Unlimited, 0), (TimeControl.Unlimited, 100_000), (TimeControl.Daily, 0), (TimeControl.Daily, 100_000),
    ];

    private static string Schedule()
    {
        var points = new List<(TimeControl, long)>(ClockOracles);
        foreach (var tc in new[] { TimeControl.Ranked, TimeControl.CasualX2, TimeControl.CasualX4 })
            for (long s = 0; s <= 700; s++) points.Add((tc, s * 1000));
        var sb = new StringBuilder("[\n");
        for (int i = 0; i < points.Count; i++)
        {
            var (tc, ms) = points[i];
            var e = TimeSpan.FromMilliseconds(ms);
            var allowance = MoveClock.Allowance(tc, e);
            var next = MoveClock.NextStep(tc, e);
            sb.Append("  {\"timeControl\": ").Append(Str(tc.ToString()))
              .Append(", \"gameElapsedMs\": ").Append(Long(ms))
              .Append(", \"allowanceMs\": ").Append(allowance is { } a ? Long((long)a.TotalMilliseconds) : "null")
              .Append(", \"suddenDeath\": ").Append(Bool(MoveClock.IsSuddenDeath(tc, e)))
              .Append(", \"nextStepAtMs\": ").Append(next is { } n1 ? Long((long)n1.AtGameElapsed.TotalMilliseconds) : "null")
              .Append(", \"nextAllowanceMs\": ").Append(next is { } n2 ? Long((long)n2.Allowance.TotalMilliseconds) : "null")
              .Append('}').Append(i < points.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("]\n");
        return sb.ToString();
    }

    private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Long(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Bool(bool b) => b ? "true" : "false";
    private static string NullableInt(int? v) => v is { } x ? Int(x) : "null";
    private static string IntArray(IEnumerable<int> xs) => "[" + string.Join(",", xs.Select(Int)) + "]";
    private static string Str(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
