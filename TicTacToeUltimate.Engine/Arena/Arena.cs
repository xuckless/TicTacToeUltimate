using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace TicTacToeUltimate.Engine.Arena;

/// <summary>How the combo streak survives turns.</summary>
public enum ComboKind : byte
{
    /// <summary>First draft: any own turn without a line resets your streak.</summary>
    Consecutive = 0,
    /// <summary>Ruleset B/C: your streak survives your own blank turns; only an opponent line resets it.</summary>
    Unanswered = 1,
}

/// <summary>Who moves first in sudden-death overtime.</summary>
public enum OvertimeStarter : byte
{
    /// <summary>Natural turn order (X, since O made the last regular move).</summary>
    NextInTurn = 0,
    /// <summary>The player who conceded the last line (owner decision, 2026-10-15). X when nobody scored.</summary>
    Conceder = 1,
}

/// <summary>Arena phase.</summary>
public enum ArenaPhase : byte { Round = 0, Overtime = 1, Ended = 2 }

/// <summary>How an arena game ended.</summary>
public enum ArenaEnd : byte
{
    /// <summary>Still running.</summary>
    None = 0,
    /// <summary>The opponent reached 0 HP.</summary>
    Knockout = 1,
    /// <summary>Round time-out with unequal HP: higher HP wins.</summary>
    Decision = 2,
    /// <summary>Sudden-death overtime: the next line won.</summary>
    Overtime = 3,
    /// <summary>No legal move (cannot happen with W &lt; N², kept for completeness).</summary>
    Draw = 4,
}

/// <summary>
/// Arena (health) scoring layer over the classic board rules (AGENTS.md "Arena (health) mode", docs/arena-mode.md).
/// Pure configuration: the classic <see cref="Rules"/> engine is untouched.
/// </summary>
public sealed record ArenaRules
{
    /// <summary>Board rules (must be GlobalFifo, no opening rule). Ghost rule = <see cref="RuleSet.VanishingPieceCanWin"/> false.</summary>
    public RuleSet Board { get; init; } = RuleSet.Default;
    /// <summary>Starting HP per player.</summary>
    public int Hp { get; init; } = 125;
    /// <summary>Line damage by combo step (1st, 2nd, 3rd+ unanswered line).</summary>
    public ImmutableArray<int> Ramp { get; init; } = [25, 35, 50];
    /// <summary>Crit (2+ lines in one move) = lines × base + this.</summary>
    public int CritBonus { get; init; } = 15;
    /// <summary>Combo semantics.</summary>
    public ComboKind Combo { get; init; } = ComboKind.Unanswered;
    /// <summary>Damage multiplier in the red-alert window (1 = off).</summary>
    public int RedAlertMultiplier { get; init; } = 2;
    /// <summary>Round length (game elapsed = sum of credited think times).</summary>
    public int RoundMs { get; init; } = 120_000;
    /// <summary>Red alert covers the last this-many ms of the round.</summary>
    public int RedAlertMs { get; init; } = 30_000;
    /// <summary>Per-move limit.</summary>
    public int MoveMs { get; init; } = 5_000;
    /// <summary>Who starts overtime.</summary>
    public OvertimeStarter OvertimeStarter { get; init; } = OvertimeStarter.Conceder;
    /// <summary>Named id ("arena-c", "arena-b") or "arena-custom".</summary>
    public string Id { get; init; } = "arena-custom";

    /// <summary>Ruleset C (Casual Arena + Arena Ranked Ladder): 125 HP, 25→35→50, unanswered combo, crit +15, ×2 red alert.</summary>
    public static ArenaRules RulesetC { get; } = new() { Id = "arena-c" };

    /// <summary>Ruleset B (stored for a future Competitive mode, not exposed): 100 HP, 25→35→50, unanswered combo, crit +15, no red-alert multiplier.</summary>
    public static ArenaRules RulesetB { get; } = new() { Id = "arena-b", Hp = 100, RedAlertMultiplier = 1 };

    /// <summary>Named rulesets by id.</summary>
    public static IReadOnlyList<ArenaRules> Named { get; } = [RulesetC, RulesetB];

    /// <summary>Looks up a named ruleset.</summary>
    public static bool TryParse(string? id, [NotNullWhen(true)] out ArenaRules? rules)
    {
        rules = Named.FirstOrDefault(r => string.Equals(r.Id, id?.Trim(), StringComparison.Ordinal));
        return rules is not null;
    }

    /// <summary>Game elapsed (ms) from which red alert applies.</summary>
    public int RedAlertAtMs => Math.Max(0, RoundMs - RedAlertMs);

    /// <summary>Empty = valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(Board.Validate());
        if (Board.Removal != RemovalKind.GlobalFifo) errors.Add("Arena needs GlobalFifo removal.");
        if (Board.Opening != OpeningRule.None) errors.Add("Arena has no opening rule.");
        if (Hp <= 0) errors.Add("Hp must be positive.");
        if (Ramp.IsDefaultOrEmpty || Ramp.Any(d => d < 0)) errors.Add("Ramp must be non-empty and non-negative.");
        if (CritBonus < 0 || RedAlertMultiplier < 1 || RoundMs <= 0 || RedAlertMs < 0 || MoveMs <= 0) errors.Add("Bad timing/damage settings.");
        return errors;
    }

    /// <inheritdoc />
    public bool Equals(ArenaRules? other) => other is not null && Id == other.Id && Board == other.Board && Hp == other.Hp
        && Ramp.SequenceEqual(other.Ramp) && CritBonus == other.CritBonus && Combo == other.Combo
        && RedAlertMultiplier == other.RedAlertMultiplier && RoundMs == other.RoundMs && RedAlertMs == other.RedAlertMs
        && MoveMs == other.MoveMs && OvertimeStarter == other.OvertimeStarter;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Board, Hp, CritBonus, Combo, RedAlertMultiplier, RoundMs, MoveMs);
}

/// <summary>One scoring move.</summary>
public sealed record ArenaHit(
    Color Attacker,
    int Lines,
    ImmutableArray<int> Shattered,     // sorted cells removed from board and queue
    int Damage,                        // final damage dealt (after red alert)
    int Combo,                         // attacker's streak including this line
    bool Crit,
    bool RedAlert,
    int BrokenStreak,                  // victim's streak that this line reset (unanswered combo), 0 if none
    bool Overtime);

/// <summary>Immutable arena state. Pieces are in FIFO order (oldest first).</summary>
public sealed record ArenaState(
    ArenaRules Rules,
    ImmutableArray<Piece> Pieces,
    Color ToMove,
    int Ply,
    int HpX, int HpO,
    int StreakX, int StreakO,
    int MovesX, int MovesO,
    int LinesX, int LinesO,
    int ElapsedMs,
    ArenaPhase Phase,
    ArenaEnd End,
    Color? Winner,
    Color? LastScorer,
    int? LastVanishedCell,
    ArenaHit? LastHit)
{
    /// <summary>HP of a colour.</summary>
    public int Hp(Color c) => c == Color.X ? HpX : HpO;
    /// <summary>Combo streak of a colour.</summary>
    public int Streak(Color c) => c == Color.X ? StreakX : StreakO;
    /// <summary>Moves made by a colour.</summary>
    public int Moves(Color c) => c == Color.X ? MovesX : MovesO;
    /// <summary>Lines scored by a colour.</summary>
    public int LinesOf(Color c) => c == Color.X ? LinesX : LinesO;
    /// <summary>True once ended.</summary>
    public bool IsOver => Phase == ArenaPhase.Ended;
    /// <summary>True when the next move would land in the red-alert window.</summary>
    public bool RedAlert => Phase == ArenaPhase.Round && ElapsedMs >= Rules.RedAlertAtMs;
}

/// <summary>Result of <see cref="ArenaEngine.Apply"/>.</summary>
public readonly record struct ArenaStep(ArenaState State, MoveError Error, int? VanishedCell, ArenaHit? Hit)
{
    /// <summary>True when accepted.</summary>
    public bool Ok => Error == MoveError.None;
}

/// <summary>Pure arena rules. Mirrors tools/arena-sim/sim/arena.py (apply + play) for ruleset B/C.</summary>
public static class ArenaEngine
{
    /// <summary>Empty board, full HP, X to move.</summary>
    public static ArenaState Initial(ArenaRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Validate() is { Count: > 0 } errors) throw new ArgumentException(string.Join(" ", errors), nameof(rules));
        return new ArenaState(rules, [], Color.X, 0, rules.Hp, rules.Hp, 0, 0, 0, 0, 0, 0, 0, ArenaPhase.Round, ArenaEnd.None, null, null, null, null);
    }

    /// <summary>Arbitrary position (tests, tools). <paramref name="queue"/> is the FIFO (oldest first).</summary>
    public static ArenaState FromPosition(ArenaRules rules, IEnumerable<(int Cell, Color Color)> queue, Color toMove,
        int hpX, int hpO, int streakX = 0, int streakO = 0)
    {
        var s = Initial(rules);
        var pieces = queue.Select((p, i) => new Piece(p.Cell, p.Color, i)).ToImmutableArray();
        return s with { Pieces = pieces, ToMove = toMove, HpX = hpX, HpO = hpO, StreakX = streakX, StreakO = streakO, Ply = pieces.Length };
    }

    /// <summary>Applies a placement by the side to move. <paramref name="thinkMs"/> is the credited think time of this move
    /// (it advances the round clock; red alert and time-out are evaluated at elapsed-after-move). Never throws for bad input.</summary>
    public static ArenaStep Apply(ArenaState state, int cell, int thinkMs)
    {
        ArgumentNullException.ThrowIfNull(state);
        var r = state.Rules.Board;
        if (state.Phase == ArenaPhase.Ended) return new(state, MoveError.GameOver, null, null);
        if (cell < 0 || cell >= r.CellCount) return new(state, MoveError.OutOfBounds, null, null);

        int removedIndex = Rules.RemovalIndex(r, state.Pieces, state.ToMove);
        var err = CheckPlacement(state, removedIndex, cell);
        if (err != MoveError.None) return new(state, err, null, null);

        var mover = state.ToMove;
        var opponent = Rules.Other(mover);
        int? vanished = removedIndex >= 0 ? state.Pieces[removedIndex].Cell : null;
        var b = ImmutableArray.CreateBuilder<Piece>(state.Pieces.Length + 1);
        for (int i = 0; i < state.Pieces.Length; i++)
            if (i != removedIndex) b.Add(state.Pieces[i]);
        b.Add(new Piece(cell, mover, state.Ply));
        var pieces = b.ToImmutable();

        // Ghost rule: the mover's piece that vanishes on the next placement cannot count.
        int ghostCell = -1;
        if (!r.VanishingPieceCanWin)
        {
            int nv = Rules.RemovalIndex(r, pieces, opponent);
            if (nv >= 0 && pieces[nv].Color == mover) ghostCell = pieces[nv].Cell;
        }
        ulong mine = 0;
        foreach (var p in pieces)
            if (p.Color == mover && p.Cell != ghostCell) mine |= 1UL << p.Cell;
        var masks = LineTable.Masks(r.BoardSize, r.WinLength);
        ulong union = 0;
        int lines = 0;
        ulong cellBit = 1UL << cell;
        foreach (var m in masks)
        {
            if ((m & cellBit) == 0 || (mine & m) != m) continue;   // only lines through the placed cell (sim invariant)
            union |= m;
            lines++;
        }

        var rules = state.Rules;
        int elapsed = (int)Math.Min(int.MaxValue, (long)state.ElapsedMs + Math.Max(0, thinkMs));
        int hpX = state.HpX, hpO = state.HpO, stX = state.StreakX, stO = state.StreakO;
        int linesX = state.LinesX, linesO = state.LinesO;
        var phase = state.Phase;
        var end = ArenaEnd.None;
        Color? winner = null;
        var lastScorer = state.LastScorer;
        ArenaHit? hit = null;

        if (lines > 0)
        {
            var kept = ImmutableArray.CreateBuilder<Piece>(pieces.Length);
            var shattered = new List<int>();
            foreach (var p in pieces)
            {
                if ((union & (1UL << p.Cell)) != 0) shattered.Add(p.Cell);
                else kept.Add(p);
            }
            pieces = kept.ToImmutable();
            shattered.Sort();

            int streak = (mover == Color.X ? stX : stO) + 1;
            int broken = 0;
            if (rules.Combo == ComboKind.Unanswered)
            {
                if (mover == Color.X) { broken = stO; stO = 0; }
                else { broken = stX; stX = 0; }
            }
            int baseDmg = rules.Ramp[Math.Min(streak, rules.Ramp.Length) - 1];
            int dmg = lines * baseDmg + (lines >= 2 ? rules.CritBonus : 0);
            bool red = phase == ArenaPhase.Round && rules.RedAlertMultiplier > 1 && elapsed >= rules.RedAlertAtMs;
            if (red) dmg *= rules.RedAlertMultiplier;
            if (mover == Color.X) { hpO -= dmg; stX = streak; linesX += lines; }
            else { hpX -= dmg; stO = streak; linesO += lines; }
            lastScorer = mover;
            hit = new ArenaHit(mover, lines, [.. shattered], dmg, streak, lines >= 2, red, broken, phase == ArenaPhase.Overtime);
            if (phase == ArenaPhase.Overtime) { end = ArenaEnd.Overtime; winner = mover; }
        }
        else if (rules.Combo == ComboKind.Consecutive)
        {
            if (mover == Color.X) stX = 0; else stO = 0;
        }

        int movesX = state.MovesX + (mover == Color.X ? 1 : 0);
        int movesO = state.MovesO + (mover == Color.O ? 1 : 0);
        var toMove = opponent;

        if (end == ArenaEnd.None && (hpX <= 0 || hpO <= 0))
        {
            // KO is checked before time-out and ends the game at once.
            end = ArenaEnd.Knockout;
            winner = hpO <= 0 ? Color.X : Color.O;
        }
        if (end == ArenaEnd.None && phase == ArenaPhase.Round && elapsed >= rules.RoundMs && movesX == movesO)
        {
            // Equal turns: time-out is only checked after O moves.
            if (hpX != hpO)
            {
                end = ArenaEnd.Decision;
                winner = hpX > hpO ? Color.X : Color.O;
            }
            else
            {
                phase = ArenaPhase.Overtime;
                toMove = rules.OvertimeStarter == OvertimeStarter.Conceder && lastScorer is { } ls ? Rules.Other(ls) : Color.X;
            }
        }
        if (end != ArenaEnd.None) phase = ArenaPhase.Ended;

        var next = new ArenaState(rules, pieces, toMove, state.Ply + 1, hpX, hpO, stX, stO, movesX, movesO, linesX, linesO,
            elapsed, phase, end, winner, lastScorer, vanished, hit);
        if (next.Phase != ArenaPhase.Ended && LegalCells(next).IsEmpty)
            next = next with { Phase = ArenaPhase.Ended, End = ArenaEnd.Draw };
        return new(next, MoveError.None, vanished, hit);
    }

    private static MoveError CheckPlacement(ArenaState state, int removedIndex, int cell)
    {
        for (int i = 0; i < state.Pieces.Length; i++)
            if (i != removedIndex && state.Pieces[i].Cell == cell) return MoveError.Occupied;
        if (!state.Rules.Board.VacatedCellPlayable && removedIndex >= 0 && state.Pieces[removedIndex].Cell == cell) return MoveError.CellJustVacated;
        return MoveError.None;
    }

    /// <summary>Legal cells for the side to move; empty once ended.</summary>
    public static ImmutableArray<int> LegalCells(ArenaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Phase == ArenaPhase.Ended) return [];
        int removedIndex = Rules.RemovalIndex(state.Rules.Board, state.Pieces, state.ToMove);
        var b = ImmutableArray.CreateBuilder<int>();
        for (int c = 0; c < state.Rules.Board.CellCount; c++)
            if (CheckPlacement(state, removedIndex, c) == MoveError.None) b.Add(c);
        return b.ToImmutable();
    }

    /// <summary>Cell whose piece vanishes on the next placement, or null.</summary>
    public static int? NextToVanish(ArenaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Phase == ArenaPhase.Ended) return null;
        int i = Rules.RemovalIndex(state.Rules.Board, state.Pieces, state.ToMove);
        return i >= 0 ? state.Pieces[i].Cell : null;
    }

    /// <summary>Damage a single line by <paramref name="attacker"/> would deal on the next move (red-alert aware); 0 in overtime (any line wins).</summary>
    public static int NextLineDamage(ArenaState state, Color attacker, int lines = 1)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Phase != ArenaPhase.Round || lines <= 0) return 0;
        var r = state.Rules;
        int streak = state.Streak(attacker) + 1;
        if (r.Combo == ComboKind.Consecutive && state.ToMove != attacker) streak = 1;
        int dmg = lines * r.Ramp[Math.Min(streak, r.Ramp.Length) - 1] + (lines >= 2 ? r.CritBonus : 0);
        return state.RedAlert ? dmg * r.RedAlertMultiplier : dmg;
    }

    /// <summary>N² chars, 'X', 'O' or '.'.</summary>
    public static string Board(ArenaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var chars = new char[state.Rules.Board.CellCount];
        Array.Fill(chars, '.');
        foreach (var p in state.Pieces) chars[p.Cell] = p.Color == Color.X ? 'X' : 'O';
        return new string(chars);
    }

    /// <summary>Fold of <see cref="Apply"/>; throws <see cref="InvalidActionException"/> on the first illegal move.</summary>
    public static ArenaState Replay(ArenaRules rules, IEnumerable<(int Cell, int ThinkMs)> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);
        var s = Initial(rules);
        int i = 0;
        foreach (var (cell, think) in moves)
        {
            var step = Apply(s, cell, think);
            if (!step.Ok) throw new InvalidActionException(i, step.Error);
            s = step.State;
            i++;
        }
        return s;
    }
}
