using System.Collections.Immutable;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Arena;

namespace TicTacToeUltimate.Client.Local;

/// <summary>
/// Offline Arena (health) game for Pass &amp; Play: two players on one device, no server. Runs the pure
/// <see cref="ArenaEngine"/> with a local per-move timer; a timeout auto-plays a random legal move (casual rule).
/// Rated nothing, credits nothing.
/// </summary>
public sealed class LocalArenaGame
{
    private readonly TimeProvider _time;
    private long _moveStartTicks;
    private long _pausedAtTicks;

    /// <summary>Starts a session with the given names and rules (default ruleset C).</summary>
    public LocalArenaGame(string player1, string player2, TimeProvider time, ArenaRules? rules = null)
    {
        _time = time;
        Names = [Clean(player1, LocalGameOptions.DefaultPlayer1), Clean(player2, LocalGameOptions.DefaultPlayer2)];
        Rules = rules ?? ArenaRules.RulesetC;
        StartGame(0);
    }

    /// <summary>Arena rules in use.</summary>
    public ArenaRules Rules { get; }
    /// <summary>Seat 0 / seat 1 display names.</summary>
    public string[] Names { get; }
    /// <summary>Session score per seat.</summary>
    public int[] Score { get; } = [0, 0];
    /// <summary>Seat playing X this game.</summary>
    public int XSeat { get; private set; }
    /// <summary>1-based game number in this session.</summary>
    public int GameNumber { get; private set; } = 1;
    /// <summary>Current engine state.</summary>
    public ArenaState State { get; private set; } = null!;
    /// <summary>The most recent line hit (for shatter/impact effects), if any.</summary>
    public ArenaHit? LastHit { get; private set; }
    /// <summary>Increments on every hit so the UI can replay effects.</summary>
    public int HitKey { get; private set; }
    /// <summary>Cell placed by the last move.</summary>
    public int? LastPlaced { get; private set; }
    /// <summary>True while paused.</summary>
    public bool Paused => _pausedAtTicks != 0;
    /// <summary>Raised after any state change.</summary>
    public event Action? Changed;

    /// <summary>True until the game ends.</summary>
    public bool IsLive => !State.IsOver;
    /// <summary>Seat to move.</summary>
    public int ToMoveSeat => State.ToMove == Color.X ? XSeat : 1 - XSeat;
    /// <summary>Colour played by a seat.</summary>
    public Color ColorOf(int seat) => seat == XSeat ? Color.X : Color.O;
    /// <summary>Name of a seat.</summary>
    public string NameOf(int seat) => Names[seat];
    /// <summary>Board string (".XO").</summary>
    public string Board => ArenaEngine.Board(State);
    /// <summary>Legal cells for the side to move.</summary>
    public ImmutableArray<int> LegalCells => IsLive ? ArenaEngine.LegalCells(State) : [];
    /// <summary>Piece that vanishes on the next placement.</summary>
    public int? Ghost => IsLive ? ArenaEngine.NextToVanish(State) : null;
    /// <summary>Winning seat, if decided.</summary>
    public int? WinnerSeat => State.Winner is { } w ? (w == Color.X ? XSeat : 1 - XSeat) : null;

    /// <summary>Milliseconds the current mover has been thinking.</summary>
    public int ThinkMs => IsLive ? (int)Math.Min(int.MaxValue, _time.GetElapsedTime(_moveStartTicks, Paused ? _pausedAtTicks : _time.GetTimestamp()).TotalMilliseconds) : 0;
    /// <summary>Time left on the per-move timer.</summary>
    public int MoveLeftMs => Math.Max(0, Rules.MoveMs - ThinkMs);
    /// <summary>Round time left (round clock advances by credited think time).</summary>
    public int RoundLeftMs => State.Phase == ArenaPhase.Round ? Math.Max(0, Rules.RoundMs - State.ElapsedMs - ThinkMs) : 0;
    /// <summary>True during the red-alert window (×multiplier damage).</summary>
    public bool RedAlert => State.Phase == ArenaPhase.Round && State.ElapsedMs + ThinkMs >= Rules.RedAlertAtMs;

    /// <summary>Places for the side to move. Returns false for an illegal cell.</summary>
    public bool Place(int cell)
    {
        if (!IsLive || Paused) return false;
        var think = Math.Min(ThinkMs, Rules.MoveMs);
        var step = ArenaEngine.Apply(State, cell, think);
        if (!step.Ok) return false;
        Commit(step, cell);
        return true;
    }

    /// <summary>Advances the clock: a mover past the per-move limit gets a random legal move played for them.</summary>
    public void Tick()
    {
        if (!IsLive || Paused || ThinkMs < Rules.MoveMs) return;
        var legal = ArenaEngine.LegalCells(State);
        if (legal.IsEmpty) return;
        var cell = legal[Random.Shared.Next(legal.Length)];
        var step = ArenaEngine.Apply(State, cell, Rules.MoveMs);
        if (step.Ok) Commit(step, cell);
    }

    /// <summary>Pauses or resumes the move timer.</summary>
    public void TogglePause()
    {
        if (!IsLive) return;
        if (Paused)
        {
            _moveStartTicks += _time.GetTimestamp() - _pausedAtTicks;
            _pausedAtTicks = 0;
        }
        else _pausedAtTicks = _time.GetTimestamp();
        Changed?.Invoke();
    }

    /// <summary>Next game with colours swapped; session score kept.</summary>
    public void Rematch()
    {
        StartGame(1 - XSeat);
        GameNumber++;
        Changed?.Invoke();
    }

    private void Commit(ArenaStep step, int cell)
    {
        State = step.State;
        LastPlaced = step.Hit is null ? cell : null;
        if (step.Hit is not null) { LastHit = step.Hit; HitKey++; }
        if (State.IsOver && WinnerSeat is int w) Score[w]++;
        _moveStartTicks = _time.GetTimestamp();
        Changed?.Invoke();
    }

    private void StartGame(int xSeat)
    {
        XSeat = xSeat;
        State = ArenaEngine.Initial(Rules);
        LastHit = null;
        LastPlaced = null;
        _pausedAtTicks = 0;
        _moveStartTicks = _time.GetTimestamp();
    }

    private static string Clean(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name.Trim()[..Math.Min(name.Trim().Length, LocalGameOptions.MaxNameLength)];
}
