using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.Local;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.Client.Stores;

/// <summary>
/// Open matches: applies envelopes in Seq order (gap → 2 s buffer → resync), optimistic placement with rollback,
/// live clocks from server time, and match sounds/haptics.
/// </summary>
public sealed class MatchStore : StoreBase<MatchesState>, IMatchStore, IDisposable
{
    /// <summary>How long a Seq gap is buffered before a resync.</summary>
    public static readonly TimeSpan GapTimeout = TimeSpan.FromSeconds(2);
    private const int MaxEmotes = 4;

    private sealed class Entry
    {
        public long LastSeq = -1;
        public bool Opened;
        public readonly SortedDictionary<long, MatchEventEnvelope> Buffer = new();
        public ITimer? GapTimer;
        public int LastWarnSecond = -1;
    }

    private readonly IRealtimeTransport _rt;
    private readonly ClockSync _clock;
    private readonly ISettingsStore _settings;
    private readonly ISoundService _sound;
    private readonly IHaptics _haptics;
    private readonly TimeProvider _time;
    private readonly ILogger<MatchStore> _logger;
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly Dictionary<Guid, PostGameSummaryDto> _summaries = new();
    private ITimer? _ticker;
    private int? _heartbeat;

    /// <inheritdoc />
    public event Action? ClockTick;

    /// <summary>Creates the store.</summary>
    public MatchStore(IRealtimeTransport rt, ClockSync clock, ISettingsStore settings, ISoundService sound, IHaptics haptics,
        TimeProvider time, ILogger<MatchStore> logger) : base(MatchesState.Empty)
    {
        _rt = rt; _clock = clock; _settings = settings; _sound = sound; _haptics = haptics; _time = time; _logger = logger;
        rt.MatchEventReceived += OnEnvelope;
        rt.PostGameSummaryReceived += OnSummary;
        rt.Connected += reconnect => { if (reconnect) ReopenAll(); };
    }

    /// <summary>Heartbeat bpm for sudden death on your turn: 70 + 10·(10 − allowanceSeconds), clamped to [70, 150].</summary>
    public static int HeartbeatBpm(int allowanceSeconds) => Math.Clamp(70 + 10 * (10 - allowanceSeconds), 70, 150);

    /// <inheritdoc />
    public async Task<MatchView?> OpenAsync(Guid matchId)
    {
        lock (Gate)
        {
            if (!_entries.ContainsKey(matchId)) _entries[matchId] = new Entry();
        }

        MatchSnapshotDto snap;
        try
        {
            snap = await _rt.JoinMatch(matchId);
        }
        catch (Exception ex)
        {
            var code = ErrorText.Code(ex);
            _logger.LogInformation(ex, "JoinMatch {MatchId} failed: {Code}", matchId, code);
            Update(s =>
            {
                if (s.Matches.TryGetValue(matchId, out var v))
                    return s with { Matches = s.Matches.SetItem(matchId, v with { LastError = code, Resyncing = false }) };
                if (_entries.TryGetValue(matchId, out var e) && !e.Opened) _entries.Remove(matchId);
                return s;
            });
            return null;
        }

        var effects = new List<Action>();
        MatchView? result = null;
        Update(s =>
        {
            if (!_entries.TryGetValue(matchId, out var e)) return s;      // closed meanwhile
            e.Opened = true;
            e.LastSeq = snap.Seq;
            e.GapTimer?.Dispose();
            e.GapTimer = null;
            s.Matches.TryGetValue(matchId, out var prev);
            var view = FromSnapshot(snap, prev);
            view = Drain(view, e, effects);
            if (_summaries.TryGetValue(matchId, out var summary)) view = view with { Summary = summary };
            result = Derive(view);
            return s with { Matches = s.Matches.SetItem(matchId, result) };
        });
        RunEffects(effects);
        EnsureTicker();
        return result;
    }

    /// <inheritdoc />
    public void Close(Guid matchId)
    {
        bool had;
        lock (Gate)
        {
            had = _entries.Remove(matchId, out var e);
            e?.GapTimer?.Dispose();
            _summaries.Remove(matchId);
        }
        Update(s => s.Matches.ContainsKey(matchId) ? s with { Matches = s.Matches.Remove(matchId) } : s);
        if (had) _ = LeaveQuietly(matchId);
        EnsureTicker();
    }

    private async Task LeaveQuietly(Guid matchId)
    {
        try { await _rt.LeaveMatch(matchId); } catch (Exception ex) { _logger.LogDebug(ex, "LeaveMatch failed"); }
    }

    /// <inheritdoc />
    public async Task PlaceAsync(Guid matchId, int cell)
    {
        if (!State.Matches.TryGetValue(matchId, out var v) || !v.IsMyTurn || v.PendingCell is not null) return;
        var error = LocalValidate(v, cell);
        if (error is not null)
        {
            SetError(matchId, error);
            return;
        }
        Update(s => s.Matches.TryGetValue(matchId, out var cur)
            ? s with { Matches = s.Matches.SetItem(matchId, cur with { PendingCell = cell, LastError = null }) } : s);
        var mine = MyColor(v);
        Sfx(mine == Color.O ? SoundCue.PlaceO : SoundCue.PlaceX);
        Haptic(HapticCue.Place);
        await SubmitAsync(matchId, new SubmitActionDto(matchId, v.Snapshot.Ply, ActionKind.Place, cell, Guid.NewGuid()), cell);
    }

    /// <inheritdoc />
    public async Task DecideSwapAsync(Guid matchId, bool swap)
    {
        if (!State.Matches.TryGetValue(matchId, out var v) || !v.IsMyTurn || v.Snapshot.Phase != GamePhase.AwaitingSwapDecision) return;
        await SubmitAsync(matchId, new SubmitActionDto(matchId, v.Snapshot.Ply, swap ? ActionKind.Swap : ActionKind.KeepSides, null, Guid.NewGuid()), null);
    }

    private async Task SubmitAsync(Guid matchId, SubmitActionDto request, int? pendingCell)
    {
        MoveAckDto ack;
        try { ack = await _rt.SubmitAction(request); }
        catch (Exception ex) { ack = new MoveAckDto(false, ErrorText.Code(ex), 0, request.ExpectedPly); }

        if (ack.Accepted)
        {
            Update(s => s.Matches.TryGetValue(matchId, out var cur) && cur.PendingCell is not null && cur.PendingCell == pendingCell
                ? s with { Matches = s.Matches.SetItem(matchId, cur with { PendingCell = null }) } : s);
            return;
        }
        SetError(matchId, ack.Error ?? ErrorCodes.IllegalMove, clearPending: true);
    }

    private void SetError(Guid matchId, string code, bool clearPending = false)
    {
        Update(s => s.Matches.TryGetValue(matchId, out var cur)
            ? s with { Matches = s.Matches.SetItem(matchId, cur with { LastError = code, PendingCell = clearPending ? null : cur.PendingCell }) } : s);
        Haptic(HapticCue.Error);
        Sfx(SoundCue.Error);
    }

    /// <inheritdoc />
    public async Task ResignAsync(Guid matchId)
    {
        try
        {
            var ack = await _rt.Resign(matchId);
            if (!ack.Ok) SetError(matchId, ack.Error ?? ErrorCodes.Internal);
        }
        catch (Exception ex) { SetError(matchId, ErrorText.Code(ex)); }
    }

    /// <inheritdoc />
    public async Task SendEmoteAsync(Guid matchId, string emoteId)
    {
        try
        {
            var ack = await _rt.SendEmote(matchId, emoteId);
            if (!ack.Ok) SetError(matchId, ack.Error ?? ErrorCodes.Internal);
        }
        catch (Exception ex) { SetError(matchId, ErrorText.Code(ex)); }
    }

    /// <inheritdoc />
    public async Task OfferRematchAsync(Guid matchId)
    {
        try
        {
            var ack = await _rt.OfferRematch(matchId);
            if (!ack.Ok) { SetError(matchId, ack.Error ?? ErrorCodes.RematchNotAllowed); return; }
            Update(s => s.Matches.TryGetValue(matchId, out var cur)
                ? s with { Matches = s.Matches.SetItem(matchId, cur with { RematchOfferedByMe = true }) } : s);
        }
        catch (Exception ex) { SetError(matchId, ErrorText.Code(ex)); }
    }

    /// <inheritdoc />
    public ClockView? GetClock(Guid matchId)
        => State.Matches.TryGetValue(matchId, out var v) ? ComputeClock(v, _clock.ServerNowMs()) : null;

    /// <summary>Pure clock projection at server time <paramref name="serverNowMs"/>.</summary>
    public static ClockView? ComputeClock(MatchView view, long serverNowMs)
    {
        var snap = view.Snapshot;
        var c = snap.Clock;
        if (c is null) return null;
        var running = IsRunning(view);
        var seat = snap.ToMoveSeat ?? -1;
        int? remaining = null;
        var elapsed = c.GameElapsedMsAtTurnStart;
        if (running)
        {
            elapsed += (int)Math.Max(0, serverNowMs - c.TurnStartedAtMs!.Value);
            if (c.DeadlineAtMs is { } deadline) remaining = (int)Math.Clamp(deadline - serverNowMs, 0, int.MaxValue);
        }
        else
        {
            remaining = c.AllowanceMs;
        }
        int? nextStepIn = c.NextStepAtGameElapsedMs is { } step ? Math.Max(0, step - elapsed) : null;
        var urgency = !running || remaining is null ? ClockUrgency.Idle
            : remaining <= 3000 ? ClockUrgency.Critical
            : remaining <= 5000 ? ClockUrgency.Warning
            : ClockUrgency.Normal;
        return new ClockView(remaining, c.AllowanceMs, elapsed, c.SuddenDeath, c.NextAllowanceMs, nextStepIn, urgency, seat);
    }

    private static bool IsRunning(MatchView v)
        => v.Snapshot.Clock is { TurnStartedAtMs: not null } && v.Snapshot.Phase != GamePhase.Ended && v.Result is null
           && v.Snapshot.ToMoveSeat is not null;

    // ---------------------------------------------------------------- events

    private void OnEnvelope(MatchEventEnvelope env)
    {
        var effects = new List<Action>();
        Update(s =>
        {
            if (!_entries.TryGetValue(env.MatchId, out var e)) return s;
            if (!e.Opened || !s.Matches.TryGetValue(env.MatchId, out var view))
            {
                e.Buffer[env.Seq] = env;
                return s;
            }
            if (env.Seq <= e.LastSeq) return s;
            e.Buffer[env.Seq] = env;
            var next = Drain(view, e, effects);
            if (ReferenceEquals(next, view)) return s;
            return s with { Matches = s.Matches.SetItem(env.MatchId, Derive(next)) };
        });
        RunEffects(effects);
        EnsureTicker();
    }

    /// <summary>Applies buffered envelopes in order; arms the gap timer when a hole remains. Caller holds Gate.</summary>
    private MatchView Drain(MatchView view, Entry e, List<Action> effects)
    {
        foreach (var stale in e.Buffer.Keys.Where(k => k <= e.LastSeq).ToList()) e.Buffer.Remove(stale);
        while (e.Buffer.Remove(e.LastSeq + 1, out var next))
        {
            view = ApplyEvent(view, next, e, effects);
            e.LastSeq = next.Seq;
        }
        if (e.Buffer.Count == 0)
        {
            e.GapTimer?.Dispose();
            e.GapTimer = null;
        }
        else if (e.GapTimer is null)
        {
            var id = view.MatchId;
            e.GapTimer = _time.CreateTimer(_ => _ = ResyncAsync(id), null, GapTimeout, Timeout.InfiniteTimeSpan);
        }
        return view;
    }

    private async Task ResyncAsync(Guid matchId)
    {
        Update(s =>
        {
            if (_entries.TryGetValue(matchId, out var e)) { e.GapTimer?.Dispose(); e.GapTimer = null; }
            return s.Matches.TryGetValue(matchId, out var v)
                ? s with { Matches = s.Matches.SetItem(matchId, v with { Resyncing = true }) } : s;
        });
        await OpenAsync(matchId);
    }

    private void ReopenAll()
    {
        List<Guid> ids;
        lock (Gate) ids = _entries.Keys.ToList();
        foreach (var id in ids) _ = ResyncAsync(id);
    }

    private void OnSummary(PostGameSummaryDto summary)
    {
        Update(s =>
        {
            _summaries[summary.MatchId] = summary;
            return s.Matches.TryGetValue(summary.MatchId, out var v)
                ? s with { Matches = s.Matches.SetItem(summary.MatchId, v with { Summary = summary }) } : s;
        });
    }

    private MatchView ApplyEvent(MatchView v, MatchEventEnvelope env, Entry e, List<Action> effects)
    {
        var snap = v.Snapshot with { Seq = env.Seq, ServerNowMs = env.ServerTimeMs };
        switch (env.Event)
        {
            case ActionApplied a:
            {
                var mine = v.MySeat is { } my && a.Action.Seat == my;
                snap = snap with
                {
                    Actions = [.. snap.Actions, a.Action], Board = a.Board, NextToVanish = a.NextToVanish, Swapped = a.Swapped,
                    Seats = a.Seats, Arena = a.Arena ?? snap.Arena, Ply = a.Action.Ply + 1, ToMoveSeat = null, LegalCells = [],
                    Clock = snap.Clock is null ? null : snap.Clock with { TurnStartedAtMs = null, DeadlineAtMs = null },
                };
                if (a.Action.Kind == ActionKind.Place)
                {
                    if (!mine)
                    {
                        var cue = a.Action.Color == Color.O ? SoundCue.PlaceO : SoundCue.PlaceX;
                        effects.Add(() => { Sfx(cue); Haptic(HapticCue.OpponentPlace); });
                    }
                    if (a.Action.VanishedCell is not null) effects.Add(() => { Sfx(SoundCue.Vanish); Haptic(HapticCue.Vanish); });
                }
                if (a.Arena is { } arena) AddArenaEffects(v.Snapshot.Arena, arena, a.Action.Ply, effects);
                v = v with { Snapshot = snap, PendingCell = mine ? null : v.PendingCell };
                break;
            }
            case TurnStarted t:
            {
                var wasSudden = v.Snapshot.Clock?.SuddenDeath ?? false;
                snap = snap with { Ply = t.Ply, ToMove = t.ToMove, ToMoveSeat = t.ToMoveSeat, Phase = t.Phase, Clock = t.Clock ?? snap.Clock, LegalCells = t.LegalCells };
                if (t.Clock is { SuddenDeath: true } && !wasSudden) effects.Add(() => Sfx(SoundCue.SuddenDeath));
                e.LastWarnSecond = -1;
                v = v with { Snapshot = snap };
                break;
            }
            case MatchEnded m:
            {
                snap = snap with
                {
                    Result = m.Result, Phase = GamePhase.Ended, ToMoveSeat = null, LegalCells = [],
                    Clock = snap.Clock is null ? null : snap.Clock with { TurnStartedAtMs = null, DeadlineAtMs = null },
                };
                if (v.MySeat is { } my && m.Result.WinnerSeat is { } winner)
                {
                    var won = winner == my;
                    effects.Add(() => { Sfx(won ? SoundCue.Win : SoundCue.Loss); Haptic(won ? HapticCue.Win : HapticCue.Loss); });
                }
                v = v with { Snapshot = snap, Result = m.Result, PendingCell = null };
                break;
            }
            case SeatConnectionChanged c:
                v = v with { Snapshot = snap with { Seats = [.. snap.Seats.Select(s => s.Seat == c.Seat ? s with { Connected = c.Connected, RttMs = c.RttMs } : s)] } };
                break;
            case EmoteShown em:
            {
                v = v with { Snapshot = snap };
                var muted = _settings.State.MuteOpponentEmotes && em.Seat != v.MySeat;
                if (!muted)
                {
                    var text = EmoteCatalog.All.FirstOrDefault(x => x.Id == em.EmoteId)?.Text ?? em.EmoteId;
                    var emotes = v.RecentEmotes.Add(new EmoteView(em.Seat, em.EmoteId, text, env.ServerTimeMs));
                    if (emotes.Length > MaxEmotes) emotes = emotes.RemoveRange(0, emotes.Length - MaxEmotes);
                    v = v with { RecentEmotes = emotes };
                    effects.Add(() => Sfx(SoundCue.Emote));
                }
                break;
            }
            case RematchOffered r:
                v = v with
                {
                    Snapshot = snap,
                    RematchOfferedByMe = v.RematchOfferedByMe || r.Seat == v.MySeat,
                    RematchOfferedByOpponent = v.RematchOfferedByOpponent || r.Seat != v.MySeat,
                };
                break;
            case RematchStarted rs:
                v = v with { Snapshot = snap, RematchMatchId = rs.NewMatchId };
                break;
            case SpectatorCountChanged sc:
                v = v with { Snapshot = snap with { SpectatorCount = sc.Count } };
                break;
            default:
                v = v with { Snapshot = snap };
                break;
        }
        return v;
    }

    /// <summary>Arena cues: shatter / crit / combo break on a new hit; alarm when red alert starts.</summary>
    private void AddArenaEffects(ArenaStateDto? before, ArenaStateDto after, int ply, List<Action> effects)
    {
        if (after.LastHit is { } hit && hit.Ply == ply)
        {
            if (hit.Crit) effects.Add(() => { Sfx(SoundCue.Crit); Haptic(HapticCue.Crit); });
            else effects.Add(() => { Sfx(SoundCue.Shatter); Haptic(HapticCue.Shatter); });
            if (hit.BrokenStreak > 0) effects.Add(() => Sfx(SoundCue.ComboBreak));
        }
        if (after.RedAlert && before is { RedAlert: false }) effects.Add(() => Sfx(SoundCue.RedAlert));
        if (after.Overtime && before is { Overtime: false }) effects.Add(() => Sfx(SoundCue.SuddenDeath));
    }

    // ---------------------------------------------------------------- derivation

    private static MatchView FromSnapshot(MatchSnapshotDto snap, MatchView? prev) => new(
        snap.MatchId, snap, prev?.Engine!, null, snap.YourSeat, snap.YourSeat is null, false, [],
        prev?.RecentEmotes ?? [], snap.Result, prev?.Summary,
        prev?.RematchOfferedByMe ?? false, prev?.RematchOfferedByOpponent ?? false, prev?.RematchMatchId, false, null);

    private MatchView Derive(MatchView v)
    {
        var engine = TryReplay(v.Snapshot) ?? v.Engine;
        var snap = v.Snapshot;
        var myTurn = v.MySeat is { } my && snap.ToMoveSeat == my && snap.Phase != GamePhase.Ended && v.Result is null && snap.Result is null;
        var threats = ImmutableArray<int>.Empty;
        if (myTurn && engine is not null && _settings.State.ThreatHints && snap.Mode is not (MatchMode.Ranked or MatchMode.Daily or MatchMode.ArenaRanked)
            && snap.Phase == GamePhase.AwaitingPlacement)
        {
            try { threats = BoardHints.Threats(engine, MyColor(v)); }
            catch (Exception) { threats = []; }
        }
        return v with { Engine = engine!, IsMyTurn = myTurn, ThreatCells = threats, Result = v.Result ?? snap.Result };
    }

    private static GameState? TryReplay(MatchSnapshotDto snap)
    {
        // Arena matches are scored by the arena layer (shatters): the classic engine would diverge, so use server legal cells.
        if (snap.ArenaRules is not null) return null;
        try
        {
            var actions = snap.Actions.Select(a => a.Kind switch
            {
                ActionKind.Swap => GameAction.Swap,
                ActionKind.KeepSides => GameAction.KeepSides,
                _ => GameAction.Place(a.Cell ?? -1),
            });
            return Rules.Replay(snap.Rules.ToEngine(), actions);
        }
        catch (Exception)
        {
            return null;   // engine unavailable or diverged: fall back to server-provided legal cells
        }
    }

    private static string? LocalValidate(MatchView v, int cell)
    {
        if (v.Snapshot.Phase != GamePhase.AwaitingPlacement) return ErrorCodes.WrongActionKind;
        if (v.Engine is not null)
        {
            try
            {
                var r = Rules.Apply(v.Engine, GameAction.Place(cell));
                return r.Ok ? null : ErrorCodes.FromMoveError(r.Error);
            }
            catch (Exception) { /* fall through */ }
        }
        return v.Snapshot.LegalCells.Contains(cell) ? null : ErrorCodes.IllegalMove;
    }

    private static Color MyColor(MatchView v)
        => v.Snapshot.Seats.FirstOrDefault(s => s.Seat == v.MySeat)?.Color ?? Color.X;

    private static Color Other(Color c) => c == Color.X ? Color.O : Color.X;

    // ---------------------------------------------------------------- ticker, cues

    private void EnsureTicker()
    {
        var running = State.Matches.Values.Any(IsRunning);
        lock (Gate)
        {
            if (running && _ticker is null)
                _ticker = _time.CreateTimer(_ => OnTick(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
            else if (!running && _ticker is not null)
            {
                _ticker.Dispose();
                _ticker = null;
            }
        }
        if (!running) SetHeartbeat(null);
    }

    private void OnTick()
    {
        try
        {
            var now = _clock.ServerNowMs();
            int? bpm = null;
            foreach (var v in State.Matches.Values)
            {
                if (!v.IsMyTurn || !IsRunning(v)) continue;
                var c = ComputeClock(v, now);
                if (c?.RemainingMs is not { } r || v.Snapshot.TimeControl == TimeControl.Daily) continue;
                if (r is > 0 and <= 5000)
                {
                    var sec = (r + 999) / 1000;
                    bool fire;
                    lock (Gate)
                    {
                        fire = _entries.TryGetValue(v.MatchId, out var e) && e.LastWarnSecond != sec;
                        if (fire) e!.LastWarnSecond = sec;
                    }
                    if (fire)
                    {
                        Sfx(sec == 5 ? SoundCue.Warning : SoundCue.Tick);
                        Haptic(sec == 5 ? HapticCue.Warning : HapticCue.Tick);
                    }
                }
                if (c.SuddenDeath && c.AllowanceMs is { } a) bpm = HeartbeatBpm(a / 1000);
            }
            SetHeartbeat(bpm);
            ClockTick?.Invoke();
        }
        catch (Exception ex) { _logger.LogError(ex, "Clock tick failed"); }
        if (!State.Matches.Values.Any(IsRunning)) EnsureTicker();
    }

    private void SetHeartbeat(int? bpm)
    {
        lock (Gate)
        {
            if (_heartbeat == bpm) return;
            _heartbeat = bpm;
        }
        try { _sound.SetHeartbeat(_settings.State.Sfx ? bpm : null); } catch (Exception) { /* audio optional */ }
    }

    private void Sfx(SoundCue cue)
    {
        if (!_settings.State.Sfx) return;
        try { _sound.Play(cue); } catch (Exception) { /* audio optional */ }
    }

    private void Haptic(HapticCue cue)
    {
        if (!_settings.State.Haptics) return;
        try { _haptics.Play(cue); } catch (Exception) { /* haptics optional */ }
    }

    private static void RunEffects(List<Action> effects)
    {
        foreach (var fx in effects) fx();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (Gate)
        {
            _ticker?.Dispose();
            _ticker = null;
            foreach (var e in _entries.Values) e.GapTimer?.Dispose();
        }
    }
}
