using System.Collections.Immutable;

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
    public string Id => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"n{BoardSize}k{WinLength}-{RemovalId}-v{(VacatedCellPlayable ? 1 : 0)}-w{(VanishingPieceCanWin ? 1 : 0)}-{OpeningId}");

    private string RemovalId => Removal switch
    {
        RemovalKind.GlobalFifo => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"fifo{Window}"),
        RemovalKind.PerPlayerCap => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"cap{Window}"),
        _ => "none",
    };

    private string OpeningId => Opening switch { OpeningRule.PieSwap => "pie", OpeningRule.BanCenter => "ban", _ => "none" };

    /// <summary>Parses a canonical id (see <see cref="Id"/>).</summary>
    public static bool TryParse(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RuleSet? rules)
        => throw new NotImplementedException();

    /// <summary>Empty = valid. Checks 3&lt;=N&lt;=7, 3&lt;=K&lt;=N, GlobalFifo: 2K-1&lt;=Window&lt;=N², PerPlayerCap: K&lt;=Window&lt;=N²/2, None: Window ignored.</summary>
    public IReadOnlyList<string> Validate() => throw new NotImplementedException();
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
    /// <summary>Creates a state (WP1 decides construction details).</summary>
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
    public Color? At(int cell) => throw new NotImplementedException();
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

/// <summary>Rules engine (WP1).</summary>
public static class Rules
{
    /// <summary>Initial state.</summary>
    public static GameState Initial(RuleSet rules) => throw new NotImplementedException();
    /// <summary>Never throws for bad input; on error returns the unchanged state and the error.</summary>
    public static ApplyResult Apply(GameState state, GameAction action) => throw new NotImplementedException();
    /// <summary>Empty unless Phase == AwaitingPlacement.</summary>
    public static ImmutableArray<int> LegalCells(GameState state) => throw new NotImplementedException();
    /// <summary>Place actions for LegalCells, or [Swap, KeepSides] in AwaitingSwapDecision, or empty.</summary>
    public static ImmutableArray<GameAction> LegalActions(GameState state) => throw new NotImplementedException();
    /// <summary>Cell whose piece is removed when ToMove next places; null if none.</summary>
    public static int? NextToVanish(GameState state) => throw new NotImplementedException();
    /// <summary>Cells where colour c would win if it placed there now (vanish-aware). Used for threat hints.</summary>
    public static ImmutableArray<int> ImmediateWins(GameState state, Color c) => throw new NotImplementedException();
    /// <summary>Fold of Apply; throws InvalidActionException on the first illegal action.</summary>
    public static GameState Replay(RuleSet rules, IEnumerable<GameAction> actions) => throw new NotImplementedException();
}

/// <summary>Text notation.</summary>
public static class Notation
{
    /// <summary>Tokens joined by ',': a cell number for Place, "swap", "keep". Example "4,0,swap,8".</summary>
    public static string Format(IEnumerable<GameAction> actions) => throw new NotImplementedException();
    /// <summary>"" -> empty.</summary>
    public static ImmutableArray<GameAction> Parse(string text) => throw new NotImplementedException();
    /// <summary>N² chars, row-major: 'X', 'O' or '.'.</summary>
    public static string Board(GameState state) => throw new NotImplementedException();
}

/// <summary>Win lines.</summary>
public static class LineTable
{
    /// <summary>All K-in-a-row lines (rows, columns, both diagonal directions), cached per (n, k), in a deterministic order.</summary>
    public static ImmutableArray<ImmutableArray<int>> For(int boardSize, int winLength) => throw new NotImplementedException();
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
