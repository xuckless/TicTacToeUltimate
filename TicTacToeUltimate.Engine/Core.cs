using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;

namespace TicTacToeUltimate.Engine;

public enum Color : byte { X = 0, O = 1 }
public enum RemovalKind : byte { None = 0, GlobalFifo = 1, PerPlayerCap = 2 }
public enum OpeningRule : byte { None = 0, PieSwap = 1, BanCenter = 2 }
public enum GamePhase : byte { AwaitingPlacement = 0, AwaitingSwapDecision = 1, Ended = 2 }
public enum Outcome : byte { None = 0, XWins = 1, OWins = 2, Draw = 3 }
public enum ActionKind : byte { Place = 0, Swap = 1, KeepSides = 2 }
public enum MoveError : byte { None = 0, GameOver, WrongActionKind, OutOfBounds, Occupied, CellJustVacated, BannedOpening }

/// <summary>Immutable rule configuration. Window = total pieces on board (GlobalFifo) or pieces per player (PerPlayerCap); ignored for None.</summary>
public sealed record RuleSet(
    int BoardSize = 3,
    int WinLength = 3,
    RemovalKind Removal = RemovalKind.GlobalFifo,
    int Window = 7,
    bool VacatedCellPlayable = true,
    bool VanishingPieceCanWin = false,
    OpeningRule Opening = OpeningRule.None)
{
    /// <summary>3x3, K3, FIFO 7, vacated playable, vanishing piece CANNOT win (owner's rule), no opening rule.</summary>
    public static RuleSet Default { get; } = new();
    /// <summary>Default + PieSwap (Ranked/Daily default).</summary>
    public static RuleSet RankedDefault { get; } = new(Opening: OpeningRule.PieSwap);

    /// <summary>Number of cells (N²).</summary>
    public int CellCount => BoardSize * BoardSize;

    /// <summary>Canonical id: "n{N}k{K}-{fifo{W}|cap{W}|none}-v{0|1}-w{0|1}-{none|pie|ban}", e.g. "n3k3-fifo7-v1-w0-none".</summary>
    public string Id => string.Create(CultureInfo.InvariantCulture,
        $"n{BoardSize}k{WinLength}-{RemovalId}-v{(VacatedCellPlayable ? 1 : 0)}-w{(VanishingPieceCanWin ? 1 : 0)}-{OpeningId}");

    private string RemovalId => Removal switch
    {
        RemovalKind.GlobalFifo => string.Create(CultureInfo.InvariantCulture, $"fifo{Window}"),
        RemovalKind.PerPlayerCap => string.Create(CultureInfo.InvariantCulture, $"cap{Window}"),
        _ => "none",
    };

    private string OpeningId => Opening switch { OpeningRule.PieSwap => "pie", OpeningRule.BanCenter => "ban", _ => "none" };

    /// <summary>Parses a canonical id (see <see cref="Id"/>). Only canonical ids of valid rulesets are accepted.</summary>
    public static bool TryParse(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RuleSet? rules)
    {
        rules = null;
        if (string.IsNullOrEmpty(id)) return false;
        var parts = id.Split('-');
        if (parts.Length != 5) return false;
        var nk = parts[0];
        int kIdx = nk.IndexOf('k', StringComparison.Ordinal);
        if (!nk.StartsWith('n') || kIdx < 2) return false;
        if (!TryInt(nk[1..kIdx], out int n) || !TryInt(nk[(kIdx + 1)..], out int k)) return false;

        RemovalKind removal;
        int window = 7;
        if (parts[1] == "none") removal = RemovalKind.None;
        else if (parts[1].StartsWith("fifo", StringComparison.Ordinal) && TryInt(parts[1][4..], out window)) removal = RemovalKind.GlobalFifo;
        else if (parts[1].StartsWith("cap", StringComparison.Ordinal) && TryInt(parts[1][3..], out window)) removal = RemovalKind.PerPlayerCap;
        else return false;

        bool vacated;
        if (parts[2] == "v1") vacated = true; else if (parts[2] == "v0") vacated = false; else return false;
        bool vanishWin;
        if (parts[3] == "w1") vanishWin = true; else if (parts[3] == "w0") vanishWin = false; else return false;
        OpeningRule opening;
        switch (parts[4])
        {
            case "none": opening = OpeningRule.None; break;
            case "pie": opening = OpeningRule.PieSwap; break;
            case "ban": opening = OpeningRule.BanCenter; break;
            default: return false;
        }

        var r = new RuleSet(n, k, removal, window, vacated, vanishWin, opening);
        if (r.Id != id || r.Validate().Count != 0) return false;
        rules = r;
        return true;
    }

    private static bool TryInt(string s, out int value)
        => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>Empty = valid. Checks 3&lt;=N&lt;=7, 3&lt;=K&lt;=N, GlobalFifo: 2K-1&lt;=Window&lt;=N², PerPlayerCap: K&lt;=Window&lt;=N²/2, None: Window ignored.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (BoardSize is < 3 or > 7) errors.Add("BoardSize must be in [3, 7].");
        if (WinLength < 3 || WinLength > BoardSize) errors.Add("WinLength must be in [3, BoardSize].");
        switch (Removal)
        {
            case RemovalKind.GlobalFifo:
                if (Window < 2 * WinLength - 1 || Window > CellCount) errors.Add("GlobalFifo Window must be in [2K-1, N²].");
                break;
            case RemovalKind.PerPlayerCap:
                if (Window < WinLength || Window > CellCount / 2) errors.Add("PerPlayerCap Window must be in [K, N²/2].");
                break;
            case RemovalKind.None:
                break;
            default:
                errors.Add("Unknown Removal.");
                break;
        }
        if (!Enum.IsDefined(Opening)) errors.Add("Unknown Opening.");
        return errors;
    }
}

/// <summary>A piece on the board.</summary>
public readonly record struct Piece(int Cell, Color Color, int Placement);

/// <summary>One action: a placement or a pie decision.</summary>
public readonly record struct GameAction(ActionKind Kind, int Cell)
{
    /// <summary>Place on a cell.</summary>
    public static GameAction Place(int cell) => new(ActionKind.Place, cell);
    /// <summary>Pie: swap seats.</summary>
    public static GameAction Swap { get; } = new(ActionKind.Swap, -1);
    /// <summary>Pie: keep seats.</summary>
    public static GameAction KeepSides { get; } = new(ActionKind.KeepSides, -1);
}

/// <summary>Immutable game state.</summary>
public sealed class GameState
{
    /// <summary>Creates a state.</summary>
    internal GameState(RuleSet rules, ImmutableArray<Piece> pieces, Color toMove, int ply, int placements, GamePhase phase,
        Outcome outcome, ImmutableArray<int> winLine, bool swapped, int? lastVanishedCell)
    {
        Rules = rules; Pieces = pieces; ToMove = toMove; Ply = ply; Placements = placements; Phase = phase;
        Outcome = outcome; WinLine = winLine; Swapped = swapped; LastVanishedCell = lastVanishedCell;
    }

    /// <summary>Rules in effect.</summary>
    public RuleSet Rules { get; }
    /// <summary>On board now, oldest -> newest (placement order).</summary>
    public ImmutableArray<Piece> Pieces { get; }
    /// <summary>Colour that acts next (also the chooser in AwaitingSwapDecision).</summary>
    public Color ToMove { get; }
    /// <summary>Actions applied, including Swap/KeepSides.</summary>
    public int Ply { get; }
    /// <summary>Place actions applied.</summary>
    public int Placements { get; }
    /// <summary>Phase.</summary>
    public GamePhase Phase { get; }
    /// <summary>Outcome.</summary>
    public Outcome Outcome { get; }
    /// <summary>Empty unless won.</summary>
    public ImmutableArray<int> WinLine { get; }
    /// <summary>Pie swap taken: seats exchanged colours.</summary>
    public bool Swapped { get; }
    /// <summary>Cell removed by the most recent placement.</summary>
    public int? LastVanishedCell { get; }

    /// <summary>Colour on a cell, or null.</summary>
    public Color? At(int cell)
    {
        foreach (var p in Pieces)
            if (p.Cell == cell) return p.Color;
        return null;
    }

    internal GameState With(ImmutableArray<Piece>? pieces = null, Color? toMove = null, int? ply = null, int? placements = null,
        GamePhase? phase = null, Outcome? outcome = null, ImmutableArray<int>? winLine = null, bool? swapped = null,
        int? lastVanishedCell = null, bool setLastVanished = false)
        => new(Rules, pieces ?? Pieces, toMove ?? ToMove, ply ?? Ply, placements ?? Placements, phase ?? Phase,
            outcome ?? Outcome, winLine ?? WinLine, swapped ?? Swapped, setLastVanished ? lastVanishedCell : LastVanishedCell);
}

/// <summary>Result of <see cref="Rules.Apply"/>.</summary>
public readonly record struct ApplyResult(GameState State, MoveError Error, int? VanishedCell)
{
    /// <summary>True when accepted.</summary>
    public bool Ok => Error == MoveError.None;
}

/// <summary>Thrown by <see cref="Rules.Replay"/>.</summary>
public sealed class InvalidActionException(int index, MoveError error)
    : Exception($"Action #{index} rejected: {error}")
{
    /// <summary>Index of the rejected action.</summary>
    public int Index { get; } = index;
    /// <summary>Error.</summary>
    public MoveError Error { get; } = error;
}

/// <summary>Rules engine. Normative semantics: docs/rules.md (ARCHITECTURE §4.2).</summary>
public static class Rules
{
    /// <summary>Initial state: empty board, X to place.</summary>
    public static GameState Initial(RuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new GameState(rules, [], Color.X, 0, 0, GamePhase.AwaitingPlacement, Outcome.None, [], false, null);
    }

    /// <summary>Never throws for bad input; on error returns the unchanged state and the error.</summary>
    public static ApplyResult Apply(GameState state, GameAction action)
    {
        ArgumentNullException.ThrowIfNull(state);
        var r = state.Rules;
        if (state.Phase == GamePhase.Ended) return new(state, MoveError.GameOver, null);

        if (state.Phase == GamePhase.AwaitingSwapDecision)
        {
            if (action.Kind is not (ActionKind.Swap or ActionKind.KeepSides)) return new(state, MoveError.WrongActionKind, null);
            var s2 = state.With(ply: state.Ply + 1, phase: GamePhase.AwaitingPlacement, swapped: action.Kind == ActionKind.Swap);
            return new(CheckStuck(s2), MoveError.None, null);
        }

        if (action.Kind != ActionKind.Place) return new(state, MoveError.WrongActionKind, null);
        int cell = action.Cell;
        if (cell < 0 || cell >= r.CellCount) return new(state, MoveError.OutOfBounds, null);

        int removedIndex = RemovalIndex(r, state.Pieces, state.ToMove);
        var err = CheckPlacement(state, removedIndex, cell);
        if (err != MoveError.None) return new(state, err, null);

        int? removedCell = removedIndex >= 0 ? state.Pieces[removedIndex].Cell : null;
        var builder = ImmutableArray.CreateBuilder<Piece>(state.Pieces.Length + 1);
        for (int i = 0; i < state.Pieces.Length; i++)
            if (i != removedIndex) builder.Add(state.Pieces[i]);
        builder.Add(new Piece(cell, state.ToMove, state.Placements));
        var pieces = builder.ToImmutable();

        var mover = state.ToMove;
        var opponent = Other(mover);
        int placements = state.Placements + 1;

        // Win check for the mover. The excluded ("ghost") piece is the one that vanishes on the next placement,
        // i.e. NextToVanish of the position with the opponent to move.
        int excludeIndex = -1;
        if (!r.VanishingPieceCanWin)
        {
            int nv = RemovalIndex(r, pieces, opponent);
            if (nv >= 0 && pieces[nv].Color == mover) excludeIndex = nv;
        }
        ulong mask = 0;
        for (int i = 0; i < pieces.Length; i++)
            if (pieces[i].Color == mover && i != excludeIndex) mask |= 1UL << pieces[i].Cell;
        var lines = LineTable.For(r.BoardSize, r.WinLength);
        var masks = LineTable.Masks(r.BoardSize, r.WinLength);
        for (int li = 0; li < masks.Length; li++)
        {
            if ((mask & masks[li]) == masks[li])
            {
                var won = new GameState(r, pieces, mover, state.Ply + 1, placements, GamePhase.Ended,
                    mover == Color.X ? Outcome.XWins : Outcome.OWins, lines[li], state.Swapped, removedCell);
                return new(won, MoveError.None, removedCell);
            }
        }

        if (r.Opening == OpeningRule.PieSwap && placements == 1)
        {
            var pie = new GameState(r, pieces, Color.O, state.Ply + 1, placements, GamePhase.AwaitingSwapDecision,
                Outcome.None, [], state.Swapped, removedCell);
            return new(pie, MoveError.None, removedCell);
        }

        var next = new GameState(r, pieces, opponent, state.Ply + 1, placements, GamePhase.AwaitingPlacement,
            Outcome.None, [], state.Swapped, removedCell);
        return new(CheckStuck(next), MoveError.None, removedCell);
    }

    private static GameState CheckStuck(GameState s)
        => s.Phase == GamePhase.AwaitingPlacement && LegalCells(s).IsEmpty
            ? s.With(phase: GamePhase.Ended, outcome: Outcome.Draw)
            : s;

    private static MoveError CheckPlacement(GameState state, int removedIndex, int cell)
    {
        var r = state.Rules;
        for (int i = 0; i < state.Pieces.Length; i++)
            if (i != removedIndex && state.Pieces[i].Cell == cell) return MoveError.Occupied;
        if (!r.VacatedCellPlayable && removedIndex >= 0 && state.Pieces[removedIndex].Cell == cell) return MoveError.CellJustVacated;
        if (r.Opening == OpeningRule.BanCenter && state.Placements == 0 && IsCenter(r.BoardSize, cell)) return MoveError.BannedOpening;
        return MoveError.None;
    }

    internal static bool IsCenter(int n, int cell)
    {
        int row = cell / n, col = cell % n;
        if (n % 2 == 1) return row == n / 2 && col == n / 2;
        return (row == n / 2 || row == n / 2 - 1) && (col == n / 2 || col == n / 2 - 1);
    }

    /// <summary>Index into pieces of the piece removed when <paramref name="toMove"/> places next; -1 if none.</summary>
    internal static int RemovalIndex(RuleSet r, ImmutableArray<Piece> pieces, Color toMove)
    {
        switch (r.Removal)
        {
            case RemovalKind.GlobalFifo:
                return pieces.Length >= r.Window ? 0 : -1;
            case RemovalKind.PerPlayerCap:
                int count = 0, first = -1;
                for (int i = 0; i < pieces.Length; i++)
                {
                    if (pieces[i].Color != toMove) continue;
                    if (first < 0) first = i;
                    count++;
                }
                return count >= r.Window ? first : -1;
            default:
                return -1;
        }
    }

    internal static Color Other(Color c) => c == Color.X ? Color.O : Color.X;

    /// <summary>Empty unless Phase == AwaitingPlacement.</summary>
    public static ImmutableArray<int> LegalCells(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Phase != GamePhase.AwaitingPlacement) return [];
        int removedIndex = RemovalIndex(state.Rules, state.Pieces, state.ToMove);
        var b = ImmutableArray.CreateBuilder<int>();
        for (int c = 0; c < state.Rules.CellCount; c++)
            if (CheckPlacement(state, removedIndex, c) == MoveError.None) b.Add(c);
        return b.ToImmutable();
    }

    /// <summary>Place actions for LegalCells, or [Swap, KeepSides] in AwaitingSwapDecision, or empty.</summary>
    public static ImmutableArray<GameAction> LegalActions(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Phase switch
        {
            GamePhase.AwaitingSwapDecision => [GameAction.Swap, GameAction.KeepSides],
            GamePhase.AwaitingPlacement => [.. LegalCells(state).Select(GameAction.Place)],
            _ => [],
        };
    }

    /// <summary>Cell whose piece is removed when ToMove next places; null if none (or the game has ended).</summary>
    public static int? NextToVanish(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Phase == GamePhase.Ended) return null;
        int i = RemovalIndex(state.Rules, state.Pieces, state.ToMove);
        return i >= 0 ? state.Pieces[i].Cell : null;
    }

    /// <summary>Cells where colour c would win if it placed there now (vanish-aware). Used for threat hints.</summary>
    public static ImmutableArray<int> ImmediateWins(GameState state, Color c)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Phase == GamePhase.Ended) return [];
        var hypo = state.With(toMove: c, phase: GamePhase.AwaitingPlacement);
        var b = ImmutableArray.CreateBuilder<int>();
        foreach (var cell in LegalCells(hypo))
        {
            var res = Apply(hypo, GameAction.Place(cell));
            if (res.Ok && res.State.Outcome == (c == Color.X ? Outcome.XWins : Outcome.OWins)) b.Add(cell);
        }
        return b.ToImmutable();
    }

    /// <summary>Fold of Apply; throws InvalidActionException on the first illegal action.</summary>
    public static GameState Replay(RuleSet rules, IEnumerable<GameAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var s = Initial(rules);
        int i = 0;
        foreach (var a in actions)
        {
            var res = Apply(s, a);
            if (!res.Ok) throw new InvalidActionException(i, res.Error);
            s = res.State;
            i++;
        }
        return s;
    }
}

/// <summary>Text notation.</summary>
public static class Notation
{
    /// <summary>Tokens joined by ',': a cell number for Place, "swap", "keep". Example "4,0,swap,8".</summary>
    public static string Format(IEnumerable<GameAction> actions)
        => string.Join(',', actions.Select(Token));

    /// <summary>Single token for one action.</summary>
    public static string Token(GameAction a) => a.Kind switch
    {
        ActionKind.Swap => "swap",
        ActionKind.KeepSides => "keep",
        _ => a.Cell.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>"" -> empty. Throws FormatException on an unknown token.</summary>
    public static ImmutableArray<GameAction> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var b = ImmutableArray.CreateBuilder<GameAction>();
        foreach (var raw in text.Split(','))
        {
            var t = raw.Trim();
            if (t == "swap") b.Add(GameAction.Swap);
            else if (t == "keep") b.Add(GameAction.KeepSides);
            else if (int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int c)) b.Add(GameAction.Place(c));
            else throw new FormatException($"Bad action token '{t}'.");
        }
        return b.ToImmutable();
    }

    /// <summary>N² chars, row-major: 'X', 'O' or '.'.</summary>
    public static string Board(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var chars = new char[state.Rules.CellCount];
        Array.Fill(chars, '.');
        foreach (var p in state.Pieces) chars[p.Cell] = p.Color == Color.X ? 'X' : 'O';
        return new string(chars);
    }
}

/// <summary>Win lines.</summary>
public static class LineTable
{
    private static readonly ConcurrentDictionary<(int, int), (ImmutableArray<ImmutableArray<int>> Lines, ulong[] Masks)> Cache = new();

    /// <summary>All K-in-a-row lines (rows, columns, both diagonal directions), cached per (n, k), in a deterministic order:
    /// for each start cell in row-major order, directions right, down, down-right, down-left.</summary>
    public static ImmutableArray<ImmutableArray<int>> For(int boardSize, int winLength) => Get(boardSize, winLength).Lines;

    internal static ulong[] Masks(int boardSize, int winLength) => Get(boardSize, winLength).Masks;

    private static (ImmutableArray<ImmutableArray<int>> Lines, ulong[] Masks) Get(int n, int k)
        => Cache.GetOrAdd((n, k), static key =>
        {
            var (n, k) = key;
            var lines = ImmutableArray.CreateBuilder<ImmutableArray<int>>();
            (int dr, int dc)[] dirs = [(0, 1), (1, 0), (1, 1), (1, -1)];
            for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            foreach (var (dr, dc) in dirs)
            {
                int er = r + dr * (k - 1), ec = c + dc * (k - 1);
                if (er < 0 || er >= n || ec < 0 || ec >= n) continue;
                var line = new int[k];
                for (int i = 0; i < k; i++) line[i] = (r + dr * i) * n + c + dc * i;
                lines.Add([.. line]);
            }
            var all = lines.ToImmutable();
            var masks = new ulong[all.Length];
            for (int i = 0; i < all.Length; i++)
                foreach (var cell in all[i]) masks[i] |= 1UL << cell;
            return (all, masks);
        });
}

/// <summary>Deterministic RNG for casual auto-moves and colour assignment. Never use System.Random for game logic.</summary>
public struct SplitMix64
{
    private ulong _state;

    /// <summary>Seeds the generator.</summary>
    public SplitMix64(ulong seed) => _state = seed;

    /// <summary>Next 64-bit value.</summary>
    public ulong Next()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, maxExclusive) by rejection sampling.</summary>
    public int NextInt(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        ulong bound = (ulong)maxExclusive;
        ulong limit = ulong.MaxValue - (ulong.MaxValue % bound);
        ulong r;
        do { r = Next(); } while (r >= limit);
        return (int)(r % bound);
    }
}
