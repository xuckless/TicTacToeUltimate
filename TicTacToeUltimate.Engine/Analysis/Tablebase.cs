using System.Collections.Immutable;
using Rules_ = TicTacToeUltimate.Engine.Rules;

namespace TicTacToeUltimate.Engine.Analysis;

public enum Value : sbyte { Loss = -1, Draw = 0, Win = 1 }                 // side-to-move perspective
/// <summary>Plies to forced end; 0 for Draw and terminal.</summary>
public readonly record struct PositionValue(Value Value, int Distance);
// Best: keeps the value with optimal distance (fastest win / slowest loss / any draw-keeping move).
// Good: keeps the value but not optimal distance. Blunder: value worsens for the mover (Win->Draw/Loss, Draw->Loss).
public enum MoveClass : byte { Best = 0, Good = 1, Blunder = 2 }

/// <summary>Evaluation of one legal action.</summary>
public sealed record MoveEval(GameAction Action, Value ResultForMover, int Distance, MoveClass Class);
/// <summary>Analysis of one ply.</summary>
public sealed record PlyAnalysis(int Ply, GameAction Played, PositionValue Before, MoveClass Class, ImmutableArray<GameAction> BestActions);

/// <summary>Thrown when a build exceeds its state limit.</summary>
public sealed class TablebaseTooLargeException(string rulesId, long limit) : Exception($"{rulesId} exceeds {limit} states");

/// <summary>Solved table for one ruleset. Pie is ignored (it does not change the reachable placement positions).</summary>
public sealed class Tablebase
{
    private const byte NonTerminal = 0, Lost = 1, Stuck = 2;

    private readonly Dictionary<ulong, int> _index;
    private readonly sbyte[] _values;
    private readonly ushort[] _dist;

    private Tablebase(RuleSet rules, Dictionary<ulong, int> index, sbyte[] values, ushort[] dist, int nonTerminal)
    {
        Rules = rules; _index = index; _values = values; _dist = dist;
        StateCount = values.Length;
        NonTerminalCount = nonTerminal;
    }

    /// <summary>True when the state key fits 64 bits: N² &lt;= 16 and piece capacity fits (FIFO: Window, Cap: 2·Window, None: N²) with 4 bits/cell + 5-bit count + 1 to-move bit (cap packs two queues).</summary>
    public static bool CanBuild(RuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Validate().Count != 0 || rules.CellCount > 16) return false;
        int capacity = rules.Removal switch
        {
            RemovalKind.GlobalFifo => rules.Window,
            RemovalKind.PerPlayerCap => 2 * rules.Window,
            _ => rules.CellCount,
        };
        return capacity < 32 && capacity * 4 + 6 <= 64;
    }

    /// <summary>Forward BFS from Initial over AwaitingPlacement positions (pie ignored), then counter-based retrograde labelling; unresolved = Draw.</summary>
    public static Tablebase Build(RuleSet rules, long maxStates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!CanBuild(rules)) throw new ArgumentException($"Tablebase cannot be built for {rules.Id}.", nameof(rules));
        var codec = new Codec(rules);

        var index = new Dictionary<ulong, int>(1 << 16);
        var keys = new List<ulong>(1 << 16);
        var flags = new List<byte>(1 << 16);
        var childStart = new List<int>(1 << 16);
        var childList = new List<int>(1 << 18);

        index[0UL] = 0; keys.Add(0UL); flags.Add(NonTerminal);
        Span<ulong> childKeys = stackalloc ulong[16];
        Span<bool> childWon = stackalloc bool[16];

        for (int i = 0; i < keys.Count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            childStart.Add(childList.Count);
            if (flags[i] == Lost) continue;
            int count = codec.Successors(keys[i], childKeys, childWon);
            if (count == 0) { flags[i] = Stuck; continue; }
            for (int c = 0; c < count; c++)
            {
                if (!index.TryGetValue(childKeys[c], out int ci))
                {
                    ci = keys.Count;
                    if (ci >= maxStates) throw new TablebaseTooLargeException(rules.Id, maxStates);
                    index[childKeys[c]] = ci;
                    keys.Add(childKeys[c]);
                    flags.Add(childWon[c] ? Lost : NonTerminal);
                }
                childList.Add(ci);
            }
        }
        childStart.Add(childList.Count);

        int n = keys.Count;
        // Reverse edges (CSR).
        var parentCount = new int[n + 1];
        foreach (var c in childList) parentCount[c + 1]++;
        for (int i = 0; i < n; i++) parentCount[i + 1] += parentCount[i];
        var parents = new int[childList.Count];
        var fill = new int[n];
        for (int p = 0; p < n; p++)
            for (int e = childStart[p]; e < childStart[p + 1]; e++)
            {
                int c = childList[e];
                parents[parentCount[c] + fill[c]++] = p;
            }

        var values = new sbyte[n];
        var dist = new ushort[n];
        var remaining = new int[n];
        var queue = new int[n];
        int head = 0, tail = 0, nonTerminal = 0;
        for (int i = 0; i < n; i++)
        {
            remaining[i] = childStart[i + 1] - childStart[i];
            if (flags[i] == Lost) { values[i] = -1; queue[tail++] = i; }
            else nonTerminal++;
        }
        ct.ThrowIfCancellationRequested();
        var resolved = new bool[n];
        for (int i = 0; i < tail; i++) resolved[queue[i]] = true;
        while (head < tail)
        {
            int c = queue[head++];
            for (int e = parentCount[c]; e < parentCount[c + 1]; e++)
            {
                int p = parents[e];
                if (resolved[p]) continue;
                if (values[c] == -1)
                {
                    values[p] = 1; dist[p] = (ushort)(dist[c] + 1); resolved[p] = true; queue[tail++] = p;
                }
                else if (--remaining[p] == 0)
                {
                    values[p] = -1; dist[p] = (ushort)(dist[c] + 1); resolved[p] = true; queue[tail++] = p;
                }
            }
        }

        return new Tablebase(rules, index, values, dist, nonTerminal);
    }

    /// <summary>Ruleset.</summary>
    public RuleSet Rules { get; }
    /// <summary>Reachable incl. terminal; Default = 369,802.</summary>
    public int StateCount { get; }
    /// <summary>Reachable positions not already won by the previous mover (stuck "no legal move" draws count as non-terminal, as in the study). Default = 331,066.</summary>
    public int NonTerminalCount { get; }

    /// <summary>Raw lookup of a placement position (pieces + colour to place).</summary>
    private PositionValue Lookup(GameState state, Color toMove)
    {
        var key = new Codec(Rules).Encode(state.Pieces, toMove);
        if (!_index.TryGetValue(key, out int i))
            throw new InvalidOperationException($"Position not in tablebase {Rules.Id}: {Notation.Board(state)}.");
        return _values[i] switch
        {
            1 => new PositionValue(Value.Win, _dist[i]),
            -1 => new PositionValue(Value.Loss, _dist[i]),
            _ => new PositionValue(Value.Draw, 0),
        };
    }

    /// <summary>For AwaitingPlacement: value for ToMove. For AwaitingSwapDecision: value for the chooser if it keeps (= value of the position for O to place). Ended: (Loss,0) for ToMove if won by the opponent, (Draw,0) for Draw.</summary>
    public PositionValue Evaluate(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Phase switch
        {
            GamePhase.Ended => state.Outcome switch
            {
                Outcome.XWins => new PositionValue(state.ToMove == Color.X ? Value.Win : Value.Loss, 0),
                Outcome.OWins => new PositionValue(state.ToMove == Color.O ? Value.Win : Value.Loss, 0),
                _ => new PositionValue(Value.Draw, 0),
            },
            GamePhase.AwaitingSwapDecision => Lookup(state, Color.O),
            _ => Lookup(state, state.ToMove),
        };
    }

    /// <summary>Per legal action. Swap-phase: Keep = Evaluate(state); Swap = negated value (chooser becomes the other side).
    /// A placement that hands the opponent a pie decision is valued as if the chooser picks its better side.</summary>
    public ImmutableArray<MoveEval> EvaluateMoves(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var raw = new List<(GameAction A, Value V, int D)>();
        if (state.Phase == GamePhase.AwaitingSwapDecision)
        {
            var keep = Evaluate(state);
            raw.Add((GameAction.Swap, Negate(keep.Value), keep.Distance));
            raw.Add((GameAction.KeepSides, keep.Value, keep.Distance));
        }
        else if (state.Phase == GamePhase.AwaitingPlacement)
        {
            var mover = state.ToMove;
            foreach (var action in Rules_.LegalActions(state))
            {
                var child = Rules_.Apply(state, action).State;
                if (child.Phase == GamePhase.Ended)
                {
                    bool won = child.Outcome == (mover == Color.X ? Outcome.XWins : Outcome.OWins);
                    raw.Add((action, won ? Value.Win : child.Outcome == Outcome.Draw ? Value.Draw : Value.Loss, won ? 1 : 0));
                }
                else if (child.Phase == GamePhase.AwaitingSwapDecision)
                {
                    var v = Evaluate(child);
                    raw.Add(v.Value == Value.Draw ? (action, Value.Draw, 0) : (action, Value.Loss, v.Distance + 1));
                }
                else
                {
                    var v = Evaluate(child);
                    raw.Add(v.Value == Value.Draw ? (action, Value.Draw, 0) : (action, Negate(v.Value), v.Distance + 1));
                }
            }
        }
        if (raw.Count == 0) return [];

        var best = raw.Max(r => r.V);
        int optimal = best switch
        {
            Value.Win => raw.Where(r => r.V == Value.Win).Min(r => r.D),
            Value.Loss => raw.Max(r => r.D),
            _ => 0,
        };
        var b = ImmutableArray.CreateBuilder<MoveEval>(raw.Count);
        foreach (var (a, v, d) in raw)
        {
            var cls = v < best ? MoveClass.Blunder : (v == Value.Draw || d == optimal) ? MoveClass.Best : MoveClass.Good;
            b.Add(new MoveEval(a, v, d, cls));
        }
        return b.ToImmutable();
    }

    private static Value Negate(Value v) => (Value)(-(sbyte)v);

    /// <summary>Packs placement positions into 64-bit keys and generates successors directly on keys.
    /// Layout: bit 0 to-move, bits 1..5 piece count, then 4 bits per cell (FIFO/None: placement order; Cap: X queue then O queue).</summary>
    private readonly struct Codec(RuleSet rules)
    {
        private readonly RuleSet _r = rules;

        public ulong Encode(ImmutableArray<Piece> pieces, Color toMove)
        {
            ulong key = (toMove == Color.O ? 1UL : 0UL) | ((ulong)(uint)pieces.Length << 1);
            int slot = 0;
            if (_r.Removal == RemovalKind.PerPlayerCap)
            {
                foreach (var p in pieces) if (p.Color == Color.X) key |= (ulong)p.Cell << (6 + 4 * slot++);
                foreach (var p in pieces) if (p.Color == Color.O) key |= (ulong)p.Cell << (6 + 4 * slot++);
            }
            else
            {
                foreach (var p in pieces) key |= (ulong)p.Cell << (6 + 4 * slot++);
            }
            return key;
        }

        private void Decode(ulong key, Span<int> cells, Span<byte> colors, out int n, out byte toMove)
        {
            toMove = (byte)(key & 1);
            n = (int)((key >> 1) & 31);
            for (int i = 0; i < n; i++) cells[i] = (int)((key >> (6 + 4 * i)) & 15);
            if (_r.Removal == RemovalKind.PerPlayerCap)
            {
                int xc = toMove == 0 ? n / 2 : (n + 1) / 2;
                for (int i = 0; i < n; i++) colors[i] = (byte)(i < xc ? 0 : 1);
            }
            else
            {
                // Colours alternate; the newest piece belongs to the colour that just moved.
                for (int i = 0; i < n; i++) colors[i] = (byte)(((n - 1 - i) & 1) == 0 ? 1 - toMove : toMove);
            }
        }

        private int RemovalIndex(ReadOnlySpan<byte> colors, int n, byte toMove)
        {
            switch (_r.Removal)
            {
                case RemovalKind.GlobalFifo: return n >= _r.Window ? 0 : -1;
                case RemovalKind.PerPlayerCap:
                    int count = 0, first = -1;
                    for (int i = 0; i < n; i++)
                    {
                        if (colors[i] != toMove) continue;
                        if (first < 0) first = i;
                        count++;
                    }
                    return count >= _r.Window ? first : -1;
                default: return -1;
            }
        }

        private ulong EncodeSpan(ReadOnlySpan<int> cells, ReadOnlySpan<byte> colors, int n, byte toMove)
        {
            ulong key = toMove | ((ulong)n << 1);
            int slot = 0;
            if (_r.Removal == RemovalKind.PerPlayerCap)
            {
                for (int i = 0; i < n; i++) if (colors[i] == 0) key |= (ulong)cells[i] << (6 + 4 * slot++);
                for (int i = 0; i < n; i++) if (colors[i] == 1) key |= (ulong)cells[i] << (6 + 4 * slot++);
            }
            else
            {
                for (int i = 0; i < n; i++) key |= (ulong)cells[i] << (6 + 4 * slot++);
            }
            return key;
        }

        /// <summary>Successor keys of a placement position; won[i] = the placement completed a counting line.</summary>
        public int Successors(ulong key, Span<ulong> childKeys, Span<bool> won)
        {
            Span<int> cells = stackalloc int[32];
            Span<byte> colors = stackalloc byte[32];
            Decode(key, cells, colors, out int n, out byte toMove);
            int removed = RemovalIndex(colors, n, toMove);

            Span<int> baseCells = stackalloc int[33];
            Span<byte> baseColors = stackalloc byte[33];
            int m = 0;
            ulong occ = 0;
            for (int i = 0; i < n; i++)
            {
                if (i == removed) continue;
                baseCells[m] = cells[i]; baseColors[m] = colors[i]; m++;
                occ |= 1UL << cells[i];
            }
            int vacated = removed >= 0 ? cells[removed] : -1;
            var masks = LineTable.Masks(_r.BoardSize, _r.WinLength);
            byte opp = (byte)(1 - toMove);
            int count = 0;
            for (int c = 0; c < _r.CellCount; c++)
            {
                if ((occ & (1UL << c)) != 0) continue;
                if (!_r.VacatedCellPlayable && c == vacated) continue;
                if (_r.Opening == OpeningRule.BanCenter && n == 0 && Rules_.IsCenter(_r.BoardSize, c)) continue;
                baseCells[m] = c; baseColors[m] = toMove;
                int cn = m + 1;
                int ghost = -1;
                if (!_r.VanishingPieceCanWin)
                {
                    int nv = RemovalIndex(baseColors, cn, opp);
                    if (nv >= 0 && baseColors[nv] == toMove) ghost = nv;
                }
                ulong mine = 0;
                for (int i = 0; i < cn; i++)
                    if (baseColors[i] == toMove && i != ghost) mine |= 1UL << baseCells[i];
                bool w = false;
                foreach (var lm in masks) if ((mine & lm) == lm) { w = true; break; }
                childKeys[count] = EncodeSpan(baseCells, baseColors, cn, opp);
                won[count] = w;
                count++;
            }
            return count;
        }
    }
}

/// <summary>Game analysis against a solved tablebase.</summary>
public static class GameAnalyzer
{
    /// <summary>Analyzes each ply. Throws InvalidActionException on the first illegal action.</summary>
    public static ImmutableArray<PlyAnalysis> Analyze(Tablebase tablebase, RuleSet rules, IReadOnlyList<GameAction> actions)
    {
        ArgumentNullException.ThrowIfNull(tablebase);
        ArgumentNullException.ThrowIfNull(actions);
        var s = Rules_.Initial(rules);
        var b = ImmutableArray.CreateBuilder<PlyAnalysis>(actions.Count);
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var res = Rules_.Apply(s, action);
            if (!res.Ok) throw new InvalidActionException(i, res.Error);
            var before = tablebase.Evaluate(s);
            var moves = tablebase.EvaluateMoves(s);
            var played = moves.First(m => m.Action.Kind == action.Kind && (action.Kind != ActionKind.Place || m.Action.Cell == action.Cell));
            var best = moves.Where(m => m.Class == MoveClass.Best).Select(m => m.Action).ToImmutableArray();
            b.Add(new PlyAnalysis(s.Ply, action, before, played.Class, best));
            s = res.State;
        }
        return b.ToImmutable();
    }
}
