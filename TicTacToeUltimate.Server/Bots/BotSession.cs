using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Contracts.Bot;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.Bots;

/// <summary>WebSocket close codes used by the bot gateway.</summary>
public static class BotCloseCodes
{
    /// <summary>Bad or missing key.</summary>
    public const int BadKey = 4401;
    /// <summary>Missing or unsupported <c>?protocol=</c>.</summary>
    public const int ProtocolMismatch = 4426;
    /// <summary>Three pings went unanswered.</summary>
    public const int PingTimeout = 4408;
}

/// <summary>Tunables for a bot session (defaults match spec §6.9).</summary>
public sealed record BotSessionSettings(TimeSpan PingInterval, int MaxMissedPings, int MaxMessageBytes)
{
    /// <summary>Ping every 15 s; close after 3 missed; 64 KiB max inbound frame.</summary>
    public static BotSessionSettings Default { get; } = new(TimeSpan.FromSeconds(15), 3, 64 * 1024);
}

/// <summary>
/// One connected bot socket. One receive loop, one writer (<see cref="BotOutbox"/>). Multiplexes up to
/// <c>maxConcurrentGames</c> matches and maps match envelopes to <c>state</c>/<c>gameEnd</c>.
/// </summary>
public sealed class BotSession : IMatchObserver, IBotSeeker
{
    private readonly WebSocket _socket;
    private readonly TttUser _bot;
    private readonly IMatchGateway? _gateway;
    private readonly IChallengeService? _challenges;
    private readonly BotPool _pool;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly BotSessionSettings _settings;
    private readonly ConcurrentDictionary<Guid, BotMatch> _matches = new();
    private readonly object _capacityGate = new();
    private HashSet<TimeControl> _seeking = [];
    private int _maxGames;
    private int _reserved;
    private int _missedPings;
    private BotOutbox? _outbox;
    private CancellationTokenSource? _cts;

    /// <summary>Creates a session for an authenticated bot socket.</summary>
    private readonly Func<Guid, bool>? _isShadow;

    public BotSession(WebSocket socket, TttUser bot, IMatchGateway? gateway, IChallengeService? challenges, BotPool pool,
        TimeProvider time, ILogger log, BotSessionSettings? settings = null, bool trusted = false, Func<Guid, bool>? isShadow = null)
    {
        _isShadow = isShadow;
        _socket = socket; _bot = bot; _gateway = gateway; _challenges = challenges; _pool = pool; _time = time; _log = log;
        _settings = settings ?? BotSessionSettings.Default;
        Trusted = trusted;
    }

    /// <inheritdoc />
    public bool Trusted { get; }

    /// <summary>Unique id of this connection; namespace for idempotent action ids.</summary>
    public Guid SessionId { get; } = Guid.NewGuid();
    /// <inheritdoc />
    public Guid BotUserId => _bot.UserId;
    /// <inheritdoc />
    public string BotName => _bot.DisplayName;

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Runs the session until the socket closes.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _outbox = new BotOutbox(_socket, token);
        Send(new BotHello(ProtocolInfo.Version, _bot.UserId, _bot.DisplayName, NowMs));
        var pinger = PingLoopAsync(token);
        try
        {
            await ReceiveLoopAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            _pool.Unregister(this);
            _outbox.Close(WebSocketCloseStatus.NormalClosure, "bye");
            await _cts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(SwallowAsync(_outbox.Completion), SwallowAsync(pinger)).ConfigureAwait(false);
            foreach (var match in _matches.Values) await DetachAsync(match).ConfigureAwait(false);
            _matches.Clear();
            _cts.Dispose();
        }
    }

    private static async Task SwallowAsync(Task t)
    {
        try { await t.ConfigureAwait(false); } catch (Exception) { /* shutting down */ }
    }

    private void Send(BotServerMessage message) => _outbox?.Enqueue(message);

    private void CloseWith(int code, string description)
    {
        _outbox?.Close((WebSocketCloseStatus)code, description);
        _cts?.CancelAfter(TimeSpan.FromSeconds(5));
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_settings.PingInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (Interlocked.Increment(ref _missedPings) > _settings.MaxMissedPings)
            {
                CloseWith(BotCloseCodes.PingTimeout, "ping_timeout");
                return;
            }
            Send(new BotPing(NowMs));
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (_socket.State == WebSocketState.Open)
        {
            var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (message.Length > _settings.MaxMessageBytes)
            {
                CloseWith((int)WebSocketCloseStatus.MessageTooBig, "too_big");
                return;
            }
            if (!result.EndOfMessage) continue;
            var bytes = message.ToArray();
            message.SetLength(0);
            if (result.MessageType != WebSocketMessageType.Text)
            {
                Send(new BotError(null, null, ErrorCodes.Validation, "Text frames only"));
                continue;
            }
            BotClientMessage? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(bytes, ContractsJsonContext.Default.BotClientMessage);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                Send(new BotError(null, null, ErrorCodes.Validation, "Malformed message"));
                continue;
            }
            if (parsed is null) { Send(new BotError(null, null, ErrorCodes.Validation, "Empty message")); continue; }
            try
            {
                await HandleAsync(parsed, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Bot {Bot} message failed", _bot.UserId);
                Send(new BotError(IdOf(parsed), null, ErrorCodes.Internal, "Internal error"));
            }
        }
    }

    private static string? IdOf(BotClientMessage m) => m switch
    {
        BotSeek s => s.Id, BotUnseek u => u.Id, BotAcceptChallenge c => c.Id, BotAction a => a.Id,
        BotResign r => r.Id, BotGetState g => g.Id, BotEmote e => e.Id, _ => null,
    };

    /// <summary>Handles one parsed client message (exposed for tests).</summary>
    internal async Task HandleAsync(BotClientMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case BotPong:
                Interlocked.Exchange(ref _missedPings, 0);
                break;
            case BotSeek seek:
                if (seek.TimeControls is not { Length: > 0 } || seek.MaxConcurrentGames < 1 ||
                    seek.TimeControls.Any(tc => tc is TimeControl.Daily))
                {
                    Send(new BotError(seek.Id, null, ErrorCodes.Validation, "timeControls must be non-empty live controls and maxConcurrentGames >= 1"));
                    break;
                }
                lock (_capacityGate)
                {
                    _seeking = [.. seek.TimeControls];
                    _maxGames = Math.Min(seek.MaxConcurrentGames, 64);
                }
                _pool.Register(this);
                if (seek.Id is not null) Send(new BotAck(seek.Id, null, null));
                break;
            case BotUnseek unseek:
                lock (_capacityGate) _seeking = [];
                _pool.Unregister(this);
                if (unseek.Id is not null) Send(new BotAck(unseek.Id, null, null));
                break;
            case BotAcceptChallenge accept:
                await AcceptChallengeAsync(accept, ct).ConfigureAwait(false);
                break;
            case BotAction action:
                await SubmitAsync(action, ct).ConfigureAwait(false);
                break;
            case BotResign resign:
                await ResignAsync(resign, ct).ConfigureAwait(false);
                break;
            case BotEmote emote:
                if (_gateway is TicTacToeUltimate.Server.Matches.MatchGateway gw && _matches.ContainsKey(emote.MatchId))
                {
                    var ack = await gw.EmoteAsync(emote.MatchId, _bot.UserId, emote.EmoteId, ct).ConfigureAwait(false);
                    if (emote.Id is not null) Send(ack.Ok ? new BotAck(emote.Id, emote.MatchId, null) : new BotError(emote.Id, emote.MatchId, ack.Error ?? ErrorCodes.Validation, "Emote refused"));
                }
                break;
            case BotGetState get:
                if (!_matches.TryGetValue(get.MatchId, out var m))
                {
                    Send(new BotError(get.Id, get.MatchId, ErrorCodes.MatchNotFound, "Not in this match"));
                    break;
                }
                if (get.Id is not null) Send(new BotAck(get.Id, get.MatchId, null));
                lock (m.Gate) { if (m.Ready) Send(m.ToState(NowMs)); }
                break;
        }
    }

    // ---------- capacity (IBotSeeker) ----------

    /// <inheritdoc />
    public bool TryReserveSlot(TimeControl timeControl)
    {
        lock (_capacityGate)
        {
            if (!_seeking.Contains(timeControl)) return false;
            if (_matches.Count + _reserved >= _maxGames) return false;
            _reserved++;
            return true;
        }
    }

    /// <inheritdoc />
    public void ReleaseSlot()
    {
        lock (_capacityGate) { if (_reserved > 0) _reserved--; }
    }

    /// <inheritdoc />
    public async ValueTask StartReservedMatchAsync(Guid matchId, CancellationToken ct)
    {
        ReleaseSlot();
        await StartMatchAsync(matchId, ct).ConfigureAwait(false);
    }

    // ---------- matches ----------

    private async Task<bool> StartMatchAsync(Guid matchId, CancellationToken ct)
    {
        if (_gateway is null) return false;
        var match = new BotMatch(matchId);
        if (!_matches.TryAdd(matchId, match)) return true;
        AttachResult attached;
        try
        {
            attached = await _gateway.AttachAsync(matchId, _bot.UserId, this, ct).ConfigureAwait(false);
        }
        catch (MatchAccessException ex)
        {
            _matches.TryRemove(matchId, out _);
            Send(new BotError(null, matchId, ex.Code, "Cannot attach to match"));
            return false;
        }
        var s = attached.Snapshot;
        var seat = s.YourSeat ?? 0;
        var opponent = s.Seats.FirstOrDefault(x => x.Seat != seat)?.Player
            ?? new PlayerCardDto(Guid.Empty, "Player", "a1", null, AccountKind.Account, null, null, false);
        bool ended;
        lock (match.Gate)
        {
            match.Subscription = attached.Subscription;
            match.Init(s, seat);
            Send(new BotGameStart(matchId, s.Mode, s.TimeControl, s.Rules, seat, match.You, opponent, _isShadow?.Invoke(opponent.UserId) == true));
            Send(match.ToState(NowMs));
            if (s.Result is not null) match.EmitEnd(s.Result, Send);
            match.Ready = true;
            FlushLocked(match);
            ended = match.Ended;
        }
        if (ended) await FinishAsync(match).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask OnEventAsync(MatchEventEnvelope envelope, CancellationToken ct)
    {
        if (!_matches.TryGetValue(envelope.MatchId, out var match)) return;
        bool ended;
        lock (match.Gate)
        {
            match.Pending.Add(envelope);
            if (!match.Ready || match.InFlight > 0) return;
            FlushLocked(match);
            ended = match.Ended;
        }
        if (ended) await FinishAsync(match).ConfigureAwait(false);
    }

    private void FlushLocked(BotMatch match)
    {
        foreach (var env in match.Pending.OrderBy(e => e.Seq)) match.Apply(env, NowMs, Send);
        match.Pending.Clear();
    }

    private async Task FinishAsync(BotMatch match)
    {
        if (_matches.TryRemove(match.MatchId, out _)) await DetachAsync(match).ConfigureAwait(false);
    }

    private async Task DetachAsync(BotMatch match)
    {
        var sub = Interlocked.Exchange(ref match.Subscription, null);
        if (sub is null) return;
        try { await sub.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "Detach from {Match} failed", match.MatchId); }
    }

    private async Task AcceptChallengeAsync(BotAcceptChallenge accept, CancellationToken ct)
    {
        if (_challenges is null || _gateway is null)
        {
            Send(new BotError(accept.Id, null, ErrorCodes.Internal, "Challenges unavailable"));
            return;
        }
        var result = await _challenges.AcceptAsync(_bot, accept.Code, ct).ConfigureAwait(false);
        if (!result.Accepted || result.MatchId is not { } matchId)
        {
            Send(new BotError(accept.Id, null, result.Error ?? ErrorCodes.ChallengeNotFound, "Challenge not accepted"));
            return;
        }
        Send(new BotAck(accept.Id, matchId, null));
        await StartMatchAsync(matchId, ct).ConfigureAwait(false);
    }

    private async Task SubmitAsync(BotAction action, CancellationToken ct)
    {
        if (_gateway is null || !_matches.TryGetValue(action.MatchId, out var match))
        {
            Send(new BotError(action.Id, action.MatchId, ErrorCodes.MatchNotFound, "Not in this match"));
            return;
        }
        if (action.Kind == ActionKind.Place && action.Cell is null)
        {
            Send(new BotError(action.Id, action.MatchId, ErrorCodes.Validation, "cell is required for Place"));
            return;
        }
        var gameAction = action.Kind switch
        {
            ActionKind.Place => GameAction.Place(action.Cell!.Value),
            ActionKind.Swap => GameAction.Swap,
            _ => GameAction.KeepSides,
        };
        lock (match.Gate) match.InFlight++;
        BotServerMessage reply;
        try
        {
            var ack = await _gateway.SubmitActionAsync(action.MatchId, _bot.UserId, action.Ply, gameAction,
                GuidV5.Create(SessionId, action.Id ?? ""), ct).ConfigureAwait(false);
            reply = ack.Accepted
                ? new BotAck(action.Id ?? "", action.MatchId, ack.Seq)
                : new BotError(action.Id, action.MatchId, ack.Error ?? ErrorCodes.IllegalMove, Describe(ack.Error, action));
        }
        catch (MatchAccessException ex)
        {
            reply = new BotError(action.Id, action.MatchId, ex.Code, Describe(ex.Code, action));
        }
        bool ended;
        lock (match.Gate)
        {
            Send(reply);
            match.InFlight--;
            ended = false;
            if (match.InFlight == 0 && match.Ready)
            {
                FlushLocked(match);
                ended = match.Ended;
            }
        }
        if (ended) await FinishAsync(match).ConfigureAwait(false);
    }

    private static string Describe(string? code, BotAction a) => code switch
    {
        ErrorCodes.Occupied => $"Cell {a.Cell} is occupied",
        ErrorCodes.StalePly => $"Ply {a.Ply} is stale",
        ErrorCodes.NotYourTurn => "Not your turn",
        ErrorCodes.CellJustVacated => $"Cell {a.Cell} was just vacated",
        _ => code ?? "Rejected",
    };

    private async Task ResignAsync(BotResign resign, CancellationToken ct)
    {
        if (_gateway is null || !_matches.ContainsKey(resign.MatchId))
        {
            Send(new BotError(resign.Id, resign.MatchId, ErrorCodes.MatchNotFound, "Not in this match"));
            return;
        }
        CommandAckDto ack;
        try { ack = await _gateway.ResignAsync(resign.MatchId, _bot.UserId, ct).ConfigureAwait(false); }
        catch (MatchAccessException ex) { ack = new CommandAckDto(false, ex.Code); }
        if (!ack.Ok) Send(new BotError(resign.Id, resign.MatchId, ack.Error ?? ErrorCodes.MatchOver, "Resign rejected"));
        else if (resign.Id is not null) Send(new BotAck(resign.Id, resign.MatchId, null));
    }

    /// <summary>Per-match view kept by a session. All members are guarded by <see cref="Gate"/>.</summary>
    internal sealed class BotMatch(Guid matchId)
    {
        public readonly object Gate = new();
        public Guid MatchId { get; } = matchId;
        public IAsyncDisposable? Subscription;
        public bool Ready;
        public bool Ended;
        public int InFlight;
        public readonly List<MatchEventEnvelope> Pending = [];
        public int Seat;
        public long Seq;
        public int Ply;
        public GamePhase Phase;
        public Color ToMove;
        public int? ToMoveSeat;
        public string Board = "";
        public readonly List<string> Tokens = [];
        public int? NextToVanish;
        public int[] Legal = [];
        public bool Swapped;
        public long? DeadlineAtMs;
        public int? AllowanceMs;

        public Color You => (Seat == 0) != Swapped ? Color.X : Color.O;

        public void Init(MatchSnapshotDto s, int seat)
        {
            Seat = seat; Seq = s.Seq; Ply = s.Ply; Phase = s.Phase; ToMove = s.ToMove; ToMoveSeat = s.ToMoveSeat;
            Board = s.Board; NextToVanish = s.NextToVanish; Legal = s.LegalCells; Swapped = s.Swapped;
            DeadlineAtMs = s.Clock?.DeadlineAtMs; AllowanceMs = s.Clock?.AllowanceMs;
            Tokens.Clear();
            foreach (var a in s.Actions) Tokens.Add(Token(a));
        }

        public static string Token(ActionDto a) => a.Kind switch
        {
            ActionKind.Place => a.Cell?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
            ActionKind.Swap => "swap",
            _ => "keep",
        };

        public BotState ToState(long nowMs) => new(MatchId, Seq, Ply, Phase, ToMove, You,
            Phase != GamePhase.Ended && ToMoveSeat == Seat, Board, string.Join(',', Tokens), NextToVanish,
            Phase == GamePhase.Ended ? [] : Legal, Swapped, DeadlineAtMs, AllowanceMs, nowMs);

        public void Apply(MatchEventEnvelope env, long nowMs, Action<BotServerMessage> send)
        {
            if (env.Seq <= Seq || Ended) return;
            Seq = env.Seq;
            switch (env.Event)
            {
                case ActionApplied a:
                    Board = a.Board; NextToVanish = a.NextToVanish; Swapped = a.Swapped;
                    Tokens.Add(Token(a.Action));
                    Ply = a.Action.Ply + 1;
                    break;
                case TurnStarted t:
                    Ply = t.Ply; ToMove = t.ToMove; ToMoveSeat = t.ToMoveSeat; Phase = t.Phase; Legal = t.LegalCells;
                    DeadlineAtMs = t.Clock?.DeadlineAtMs; AllowanceMs = t.Clock?.AllowanceMs;
                    send(ToState(nowMs));
                    break;
                case EmoteShown em when em.Seat != Seat:
                    send(new BotOpponentEmote(MatchId, em.Seat, em.EmoteId));
                    break;
                case MatchEnded e:
                    EmitEnd(e.Result, send, nowMs);
                    break;
            }
        }

        public void EmitEnd(MatchResultDto r, Action<BotServerMessage> send, long? nowMs = null)
        {
            if (Ended) return;
            if (nowMs is { } now && Phase != GamePhase.Ended)
            {
                Phase = GamePhase.Ended; ToMoveSeat = null;
                send(ToState(now));
            }
            Phase = GamePhase.Ended;
            Ended = true;
            send(new BotGameEnd(MatchId, r.Outcome, r.Reason, r.WinnerSeat, r.WinLine, r.WinnerSeat == Seat));
        }
    }
}
