using System.Collections.Immutable;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.Client.Local;

/// <summary>Setup for an offline pass-and-play session. Seat 0 = Player 1, seat 1 = Player 2.</summary>
public sealed record LocalGameOptions
{
    /// <summary>Default name for seat 0.</summary>
    public const string DefaultPlayer1 = "Player 1";
    /// <summary>Default name for seat 1.</summary>
    public const string DefaultPlayer2 = "Player 2";
    /// <summary>Longest accepted name.</summary>
    public const int MaxNameLength = 20;

    /// <summary>Seat 0 name.</summary>
    public string Player1 { get; init; } = DefaultPlayer1;
    /// <summary>Seat 1 name.</summary>
    public string Player2 { get; init; } = DefaultPlayer2;
    /// <summary>Unlimited, CasualX2, CasualX4 or Ranked (Daily is treated as Unlimited).</summary>
    public TimeControl TimeControl { get; init; } = TimeControl.Unlimited;
    /// <summary>Pie (swap) opening rule.</summary>
    public bool Pie { get; init; }

    /// <summary>Rules preset (Classic = launch rule).</summary>
    public LocalRulePreset Preset { get; init; } = LocalRulePreset.Classic;

    /// <summary>Classic: the launch rule <c>n3k3-cap3-v0-w1-none</c> (or <c>…-pie</c>). Legacy7: <c>n3k3-fifo7-v1-w0-none</c> (or <c>…-pie</c>).</summary>
    public RuleSet Rules
    {
        get
        {
            var r = Preset switch { LocalRulePreset.Legacy7 => RuleSet.LegacyFifo7Ghost, LocalRulePreset.OldSchool => RuleSet.OldSchool, _ => RuleSet.Default };
            return r with { Opening = Pie ? OpeningRule.PieSwap : OpeningRule.None };
        }
    }

    /// <summary>The clock actually used (Daily makes no sense on one device).</summary>
    public TimeControl EffectiveTimeControl => TimeControl == TimeControl.Daily ? TimeControl.Unlimited : TimeControl;

    /// <summary>Trimmed, length-limited names with defaults for blanks.</summary>
    public LocalGameOptions Normalized() => this with
    {
        Player1 = Clean(Player1, DefaultPlayer1),
        Player2 = Clean(Player2, DefaultPlayer2),
        TimeControl = EffectiveTimeControl,
    };

    private static string Clean(string? name, string fallback)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return fallback;
        return n.Length > MaxNameLength ? n[..MaxNameLength] : n;
    }
}

/// <summary>Pass-and-play rule presets.</summary>
public enum LocalRulePreset
{
    /// <summary>Launch rule: 3 pieces each, your own oldest vanishes when you place a 4th, any visible line wins.</summary>
    Classic = 0,
    /// <summary>Superseded rule: 7 pieces on the board, the oldest vanishes and can't complete a line.</summary>
    Legacy7 = 1,
    /// <summary>Plain tic-tac-toe: pieces never vanish; a full board is a draw.</summary>
    OldSchool = 2,
}

/// <summary>How a local game ended.</summary>
public enum LocalEndReason { Line, Timeout, Resign, Draw }

/// <summary>Result of a finished local game. WinnerSeat is null for a draw.</summary>
public sealed record LocalGameResult(int? WinnerSeat, Color? WinnerColor, LocalEndReason Reason, ImmutableArray<int> WinLine);

/// <summary>Things the UI reacts to with sound, haptics or impact frames.</summary>
public enum LocalEventKind { Placed, Swapped, KeptSides, AutoMoved, SuddenDeath, Ended }

/// <summary>One controller event. Cell/VanishedCell are set for placements.</summary>
public sealed record LocalGameEvent(LocalEventKind Kind, int Seat, Color Color, int? Cell = null, int? VanishedCell = null);

/// <summary>
/// Offline pass-and-play game: two players, one device, no server. Wraps the pure engine and runs a local move clock
/// (<see cref="MoveClock"/>). Unlimited: no timeouts. Casual: timeout plays a random legal move (or keeps sides).
/// Ranked schedule: timeout loses. No rating, streaks or quests are touched.
/// The host calls <see cref="Tick"/> periodically (e.g. 10 Hz) to enforce timeouts.
/// </summary>
public sealed class LocalGameController
{
    private readonly TimeProvider _time;
    private readonly Random _rng;
    private readonly int[] _score = new int[2];
    private DateTimeOffset _gameStart, _turnStart;
    private DateTimeOffset? _pausedAt;
    private TimeSpan? _turnAllowance;
    private bool _wasSuddenDeath;

    /// <summary>Creates a controller and starts game 1 (Player 1 plays X).</summary>
    public LocalGameController(LocalGameOptions options, TimeProvider? time = null, int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options.Normalized();
        _time = time ?? TimeProvider.System;
        _rng = seed is int s ? new Random(s) : new Random();
        StartGame(xSeat: 0);
        GameNumber = 1;
    }

    /// <summary>Raised after every state change (move, tick-driven timeout, pause, rematch).</summary>
    public event Action? Changed;
    /// <summary>Raised for notable moments (placements, swap, auto-move, sudden death, end).</summary>
    public event Action<LocalGameEvent>? Event;

    /// <summary>Normalized options.</summary>
    public LocalGameOptions Options { get; }
    /// <summary>Clock in use.</summary>
    public TimeControl TimeControl => Options.TimeControl;
    /// <summary>Engine state of the current game.</summary>
    public GameState State { get; private set; } = null!;
    /// <summary>Seat that opened as X in the current game (before any pie swap).</summary>
    public int XSeat { get; private set; }
    /// <summary>1-based game counter for this session.</summary>
    public int GameNumber { get; private set; }
    /// <summary>Set once the current game is over.</summary>
    public LocalGameResult? Result { get; private set; }
    /// <summary>Session wins for seat 0 and seat 1.</summary>
    public IReadOnlyList<int> Score => _score;
    /// <summary>Drawn games this session.</summary>
    public int Draws { get; private set; }
    /// <summary>Last placed cell (place animation).</summary>
    public int? LastPlaced { get; private set; }
    /// <summary>Clock paused (pass-and-play break).</summary>
    public bool Paused => _pausedAt is not null;

    /// <summary>Seat name.</summary>
    public string NameOf(int seat) => seat == 0 ? Options.Player1 : Options.Player2;

    /// <summary>Colour a seat currently plays (accounts for XSeat and the pie swap).</summary>
    public Color ColorOf(int seat) => (seat == XSeat) != State.Swapped ? Color.X : Color.O;

    /// <summary>Seat that currently plays a colour.</summary>
    public int SeatOf(Color color) => ColorOf(0) == color ? 0 : 1;

    /// <summary>Seat that acts next (also the pie chooser).</summary>
    public int ToMoveSeat => SeatOf(State.ToMove);

    /// <summary>True while the game runs.</summary>
    public bool IsLive => Result is null;

    /// <summary>The mover's faded piece: vanishes when the mover places now; null once ended.</summary>
    public int? Ghost => IsLive ? BoardHints.MoverVanish(State) : null;

    /// <summary>The waiting side's oldest piece that vanishes on their next placement (per-player cap only); null otherwise.</summary>
    public int? WaitingGhost => IsLive ? BoardHints.WaitingVanish(State) : null;

    /// <summary>Legal cells for the mover (empty unless placing).</summary>
    public ImmutableArray<int> LegalCells => IsLive ? Rules.LegalCells(State) : [];

    /// <summary>Awaiting the pie decision.</summary>
    public bool AwaitingSwap => IsLive && State.Phase == GamePhase.AwaitingSwapDecision;

    /// <summary>Board as N² chars row-major ('X', 'O', '.').</summary>
    public string Board
    {
        get
        {
            var chars = new char[State.Rules.CellCount];
            Array.Fill(chars, '.');
            foreach (var p in State.Pieces) chars[p.Cell] = p.Color == Color.X ? 'X' : 'O';
            return new string(chars);
        }
    }

    // ---- clock ----

    /// <summary>A move clock is running in this session.</summary>
    public bool HasClock => MoveClock.Allowance(TimeControl, TimeSpan.Zero) is not null;

    private DateTimeOffset Now => _pausedAt ?? _time.GetUtcNow();

    /// <summary>Game time elapsed (excludes pauses).</summary>
    public TimeSpan GameElapsed => IsLive ? Now - _gameStart : _turnStart - _gameStart;

    /// <summary>Allowance of the current turn; null without a clock.</summary>
    public TimeSpan? Allowance => _turnAllowance;

    /// <summary>Time left for the current turn; null without a clock.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (_turnAllowance is not TimeSpan a) return null;
            if (!IsLive) return a;
            var left = a - (Now - _turnStart);
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    /// <summary>Sudden-death phase of the schedule.</summary>
    public bool SuddenDeath => IsLive && MoveClock.IsSuddenDeath(TimeControl, GameElapsed);

    /// <summary>Urgency of the running clock (Warning ≤ 5 s, Critical ≤ 3 s).</summary>
    public ClockUrgency Urgency => Remaining switch
    {
        null => ClockUrgency.Idle,
        _ when !IsLive => ClockUrgency.Idle,
        { TotalMilliseconds: <= 3000 } => ClockUrgency.Critical,
        { TotalMilliseconds: <= 5000 } => ClockUrgency.Warning,
        _ => ClockUrgency.Normal,
    };

    /// <summary>Clock as the shared UI's <see cref="ClockView"/> (ToMoveSeat = seat index); null without a clock.</summary>
    public ClockView? ClockView
    {
        get
        {
            if (!HasClock) return null;
            var elapsed = GameElapsed;
            var next = MoveClock.NextStep(TimeControl, elapsed);
            return new ClockView(
                Ms(Remaining), Ms(Allowance), (int)elapsed.TotalMilliseconds, SuddenDeath,
                next is { } n ? (int)n.Allowance.TotalMilliseconds : null,
                next is { } n2 ? (int)(n2.AtGameElapsed - elapsed).TotalMilliseconds : null,
                Urgency, ToMoveSeat);
        }
    }

    private static int? Ms(TimeSpan? t) => t is TimeSpan v ? (int)Math.Ceiling(v.TotalMilliseconds) : null;

    // ---- intents ----

    /// <summary>Places for the seat to move. Returns the engine error (None when accepted).</summary>
    public MoveError Place(int cell)
    {
        if (!IsLive) return MoveError.GameOver;
        if (Paused) Resume();
        return ApplyPlacement(cell);
    }

    /// <summary>Pie decision by the chooser: true = swap seats' colours.</summary>
    public MoveError DecideSwap(bool swap)
    {
        if (!IsLive) return MoveError.GameOver;
        if (Paused) Resume();
        return ApplySwap(swap);
    }

    /// <summary>The seat to move resigns (the other seat wins).</summary>
    public void Resign()
    {
        if (!IsLive) return;
        int loser = ToMoveSeat;
        Finish(new LocalGameResult(1 - loser, ColorOf(1 - loser), LocalEndReason.Resign, []));
    }

    /// <summary>Pauses the clock (no-op without a clock or once ended).</summary>
    public void Pause()
    {
        if (!IsLive || Paused || !HasClock) return;
        _pausedAt = _time.GetUtcNow();
        Changed?.Invoke();
    }

    /// <summary>Resumes a paused clock; the paused span is not charged to anyone.</summary>
    public void Resume()
    {
        if (_pausedAt is not DateTimeOffset at) return;
        var span = _time.GetUtcNow() - at;
        _pausedAt = null;
        _gameStart += span;
        _turnStart += span;
        Changed?.Invoke();
    }

    /// <summary>Enforces the clock. Returns true when something changed (timeout handled).</summary>
    public bool Tick()
    {
        if (!IsLive || Paused || _turnAllowance is null) return false;
        if (SuddenDeath && !_wasSuddenDeath)
        {
            _wasSuddenDeath = true;
            Event?.Invoke(new LocalGameEvent(LocalEventKind.SuddenDeath, ToMoveSeat, State.ToMove));
        }
        if (Remaining > TimeSpan.Zero) return false;

        int seat = ToMoveSeat;
        if (TimeControl == TimeControl.Ranked)
        {
            Finish(new LocalGameResult(1 - seat, ColorOf(1 - seat), LocalEndReason.Timeout, []));
            return true;
        }

        // Casual: auto-play for the flagged player.
        Event?.Invoke(new LocalGameEvent(LocalEventKind.AutoMoved, seat, State.ToMove));
        if (State.Phase == GamePhase.AwaitingSwapDecision) ApplySwap(false);
        else
        {
            var legal = Rules.LegalCells(State);
            if (legal.IsEmpty) return false;
            ApplyPlacement(legal[_rng.Next(legal.Length)]);
        }
        return true;
    }

    /// <summary>Starts the next game with colours swapped (the other seat opens as X). Session score is kept.</summary>
    public void Rematch()
    {
        StartGame(1 - XSeat);
        GameNumber++;
        Changed?.Invoke();
    }

    // ---- internals ----

    private void StartGame(int xSeat)
    {
        XSeat = xSeat;
        State = Rules.Initial(Options.Rules);
        Result = null;
        LastPlaced = null;
        _pausedAt = null;
        _wasSuddenDeath = false;
        _gameStart = _time.GetUtcNow();
        StartTurn();
    }

    private void StartTurn()
    {
        _turnStart = Now;
        _turnAllowance = MoveClock.Allowance(TimeControl, _turnStart - _gameStart);
    }

    private MoveError ApplyPlacement(int cell)
    {
        var seat = ToMoveSeat;
        var color = State.ToMove;
        var res = Rules.Apply(State, GameAction.Place(cell));
        if (!res.Ok) return res.Error;
        State = res.State;
        LastPlaced = cell;
        Event?.Invoke(new LocalGameEvent(LocalEventKind.Placed, seat, color, cell, res.VanishedCell));
        AfterAction();
        return MoveError.None;
    }

    private MoveError ApplySwap(bool swap)
    {
        var seat = ToMoveSeat;
        var color = State.ToMove;
        var res = Rules.Apply(State, swap ? GameAction.Swap : GameAction.KeepSides);
        if (!res.Ok) return res.Error;
        State = res.State;
        Event?.Invoke(new LocalGameEvent(swap ? LocalEventKind.Swapped : LocalEventKind.KeptSides, seat, color));
        AfterAction();
        return MoveError.None;
    }

    private void AfterAction()
    {
        if (State.Phase == GamePhase.Ended)
        {
            var winner = State.Outcome switch { Outcome.XWins => Color.X, Outcome.OWins => (Color?)Color.O, _ => null };
            Finish(winner is Color w
                ? new LocalGameResult(SeatOf(w), w, LocalEndReason.Line, State.WinLine)
                : new LocalGameResult(null, null, LocalEndReason.Draw, []));
            return;
        }
        StartTurn();
        Changed?.Invoke();
    }

    private void Finish(LocalGameResult result)
    {
        _turnStart = Now;   // freezes GameElapsed
        _pausedAt = null;
        Result = result;
        if (result.WinnerSeat is int w) _score[w]++;
        else Draws++;
        Event?.Invoke(new LocalGameEvent(LocalEventKind.Ended, result.WinnerSeat ?? -1, result.WinnerColor ?? State.ToMove));
        Changed?.Invoke();
    }
}

/// <summary>Holds the current pass-and-play session so the setup page and the board page share it (singleton).</summary>
public sealed class LocalPlaySession(TimeProvider time)
{
    /// <summary>Current controller, if a session was started.</summary>
    public LocalGameController? Current { get; private set; }

    /// <summary>Starts a fresh session (score reset).</summary>
    public LocalGameController Start(LocalGameOptions options)
    {
        Current = new LocalGameController(options, time);
        return Current;
    }

    /// <summary>Current offline Arena session, if one was started.</summary>
    public LocalArenaGame? Arena { get; private set; }

    /// <summary>Starts a fresh offline Arena session (score reset).</summary>
    public LocalArenaGame StartArena(string player1, string player2)
    {
        Arena = new LocalArenaGame(player1, player2, time);
        return Arena;
    }

    /// <summary>Forgets the current session.</summary>
    public void End() { Current = null; Arena = null; }
}
