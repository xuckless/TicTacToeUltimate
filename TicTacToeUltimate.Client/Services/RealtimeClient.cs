using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Services;

/// <summary>The only HubConnection. Reconnects forever while started; answers Ping; samples the server clock.</summary>
public sealed class RealtimeClient : IRealtimeTransport, IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly TokenStore _tokens;
    private readonly ClockSync _clock;
    private readonly ILogger<RealtimeClient> _logger;
    private readonly RetryForeverPolicy _retry = new();
    private readonly Lock _gate = new();
    private HubConnection? _conn;
    private volatile bool _wanted;
    private bool _everConnected;
    private Task? _loop;
    private CancellationTokenSource _kick = new();
    private CancellationTokenSource? _sampling;

    /// <summary>Creates the client; <paramref name="lifecycle"/> triggers immediate reconnects on resume/online.</summary>
    public RealtimeClient(ClientOptions options, TokenStore tokens, ClockSync clock, ILogger<RealtimeClient> logger,
        IAppLifecycle? lifecycle = null)
    {
        _options = options; _tokens = tokens; _clock = clock; _logger = logger;
        if (lifecycle is not null)
        {
            lifecycle.Resumed += Kick;
            lifecycle.NetworkChanged += online => { if (online) Kick(); };
        }
    }

    /// <inheritdoc />
    public ConnectionPhase Phase { get; private set; } = ConnectionPhase.Idle;
    /// <inheritdoc />
    public int ReconnectAttempt { get; private set; }
    /// <inheritdoc />
    public event Action? PhaseChanged;
    /// <inheritdoc />
    public event Action<bool>? Connected;
    /// <inheritdoc />
    public Func<Task>? Handshake { get; set; }

    /// <inheritdoc />
    public event Action<QueueStatusDto>? QueueStatusReceived;
    /// <inheritdoc />
    public event Action<MatchFoundDto>? MatchFoundReceived;
    /// <inheritdoc />
    public event Action<MatchEventEnvelope>? MatchEventReceived;
    /// <inheritdoc />
    public event Action<PostGameSummaryDto>? PostGameSummaryReceived;
    /// <inheritdoc />
    public event Action<ChallengeDto>? ChallengeReceived;
    /// <inheritdoc />
    public event Action<ChallengeDto>? ChallengeUpdated;
    /// <inheritdoc />
    public event Action<PresenceDto>? PresenceChanged;
    /// <inheritdoc />
    public event Action<FriendRequestDto>? FriendRequestReceived;
    /// <inheritdoc />
    public event Action? FriendsChanged;
    /// <inheritdoc />
    public event Action<DirectMessageDto>? DirectMessageReceived;
    /// <inheritdoc />
    public event Action<NotificationDto>? NotificationReceived;

    /// <inheritdoc />
    public Task StartAsync()
    {
        TaskCompletionSource first;
        lock (_gate)
        {
            _wanted = true;
            _conn ??= Build();
            if (_conn.State != HubConnectionState.Disconnected || (_loop is { IsCompleted: false })) return Task.CompletedTask;
            first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _loop = ConnectLoopAsync(first);
        }
        return first.Task;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        HubConnection? conn;
        lock (_gate)
        {
            _wanted = false;
            conn = _conn;
            _sampling?.Cancel();
            _sampling = null;
        }
        Kick();
        if (conn is not null)
        {
            try { await conn.StopAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Stop failed"); }
        }
        SetPhase(ConnectionPhase.Idle, 0);
    }

    /// <summary>Skips the current back-off delay (resume / network back).</summary>
    public void Kick()
    {
        CancellationTokenSource old;
        lock (_gate) { old = _kick; _kick = new CancellationTokenSource(); }
        old.Cancel();
        if (_wanted && _conn?.State == HubConnectionState.Disconnected && _loop is { IsCompleted: true })
            _ = StartAsync();
    }

    private HubConnection Build()
    {
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(_options.BackendBaseUrl, HubRoutes.Game), o => o.AccessTokenProvider = () => Task.FromResult(_tokens.Token))
            .WithAutomaticReconnect(_retry)
            .AddJsonProtocol(o => ClientJson.Configure(o.PayloadSerializerOptions))
            .Build();

        conn.On<int>(nameof(IGameClient.Ping), id => conn.SendAsync(nameof(IGameHub.Pong), id));
        conn.On<QueueStatusDto>(nameof(IGameClient.QueueStatus), x => Raise(QueueStatusReceived, x));
        conn.On<MatchFoundDto>(nameof(IGameClient.MatchFound), x => Raise(MatchFoundReceived, x));
        conn.On<MatchEventEnvelope>(nameof(IGameClient.MatchEvent), x => Raise(MatchEventReceived, x));
        conn.On<PostGameSummaryDto>(nameof(IGameClient.PostGameSummary), x => Raise(PostGameSummaryReceived, x));
        conn.On<ChallengeDto>(nameof(IGameClient.ChallengeReceived), x => Raise(ChallengeReceived, x));
        conn.On<ChallengeDto>(nameof(IGameClient.ChallengeUpdated), x => Raise(ChallengeUpdated, x));
        conn.On<PresenceDto>(nameof(IGameClient.PresenceChanged), x => Raise(PresenceChanged, x));
        conn.On<FriendRequestDto>(nameof(IGameClient.FriendRequestReceived), x => Raise(FriendRequestReceived, x));
        conn.On(nameof(IGameClient.FriendsChanged), () => { SafeInvoke(() => FriendsChanged?.Invoke()); return Task.CompletedTask; });
        conn.On<DirectMessageDto>(nameof(IGameClient.DirectMessage), x => Raise(DirectMessageReceived, x));
        conn.On<NotificationDto>(nameof(IGameClient.Notification), x => Raise(NotificationReceived, x));

        conn.Reconnecting += _ =>
        {
            SetPhase(ConnectionPhase.Reconnecting, ReconnectAttempt + 1);
            return Task.CompletedTask;
        };
        conn.Reconnected += _ => OnConnectedAsync(true);
        conn.Closed += ex =>
        {
            if (ex is not null) _logger.LogInformation(ex, "Hub connection closed");
            if (_wanted)
            {
                lock (_gate)
                {
                    if (_loop is { IsCompleted: false }) return Task.CompletedTask;
                    _loop = ConnectLoopAsync(new TaskCompletionSource(), startAttempt: 1);
                }
            }
            else SetPhase(ConnectionPhase.Idle, 0);
            return Task.CompletedTask;
        };
        return conn;
    }

    private Task Raise<T>(Action<T>? handler, T value)
    {
        if (handler is not null) SafeInvoke(() => handler(value));
        return Task.CompletedTask;
    }

    private void SafeInvoke(Action a)
    {
        try { a(); } catch (Exception ex) { _logger.LogError(ex, "Hub event handler failed"); }
    }

    private async Task ConnectLoopAsync(TaskCompletionSource first, int startAttempt = 0)
    {
        var attempt = startAttempt;
        while (_wanted)
        {
            var conn = _conn!;
            try
            {
                SetPhase(_everConnected || attempt > 0 ? ConnectionPhase.Reconnecting : ConnectionPhase.Connecting, attempt);
                if (conn.State == HubConnectionState.Disconnected) await conn.StartAsync();
                await OnConnectedAsync(_everConnected);
                first.TrySetResult();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Hub connect attempt {Attempt} failed", attempt);
                SetPhase(ConnectionPhase.Offline, attempt + 1);
                first.TrySetResult();
                if (!_wanted) return;
                CancellationToken kick;
                lock (_gate) kick = _kick.Token;
                try { await Task.Delay(_retry.Delay(Math.Max(1, attempt)), kick); } catch (OperationCanceledException) { }
                attempt++;
            }
        }
    }

    private async Task OnConnectedAsync(bool reconnect)
    {
        _everConnected = true;
        SetPhase(ConnectionPhase.Online, 0);
        try
        {
            await _clock.SampleAsync(ServerTime, 5);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Clock sampling failed"); }
        StartPeriodicSampling();
        if (Handshake is { } hs)
        {
            try { await hs(); } catch (Exception ex) { _logger.LogWarning(ex, "Handshake failed"); }
        }
        SafeInvoke(() => Connected?.Invoke(reconnect));
    }

    private void StartPeriodicSampling()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_sampling is not null) return;
            cts = _sampling = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), cts.Token);
                    if (Phase == ConnectionPhase.Online) await _clock.SampleAsync(ServerTime, 1);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _logger.LogDebug(ex, "Clock sample failed"); }
            }
        });
    }

    private void SetPhase(ConnectionPhase phase, int attempt)
    {
        if (Phase == phase && ReconnectAttempt == attempt) return;
        Phase = phase; ReconnectAttempt = attempt;
        SafeInvoke(() => PhaseChanged?.Invoke());
    }

    private HubConnection Conn => _conn ?? throw new InvalidOperationException("offline");

    /// <inheritdoc />
    public Task<ServerHelloDto> Hello(ClientHelloDto hello) => Conn.InvokeAsync<ServerHelloDto>(nameof(Hello), hello);
    /// <inheritdoc />
    public Task Pong(int pingId) => Conn.SendAsync(nameof(Pong), pingId);
    /// <inheritdoc />
    public Task<long> ServerTime() => Conn.InvokeAsync<long>(nameof(ServerTime));
    /// <inheritdoc />
    public Task<QueueTicketDto> EnterQueue(QueueRequestDto request) => Conn.InvokeAsync<QueueTicketDto>(nameof(EnterQueue), request);
    /// <inheritdoc />
    public Task LeaveQueue() => Conn.InvokeAsync(nameof(LeaveQueue));
    /// <inheritdoc />
    public Task<MatchSnapshotDto> JoinMatch(Guid matchId) => Conn.InvokeAsync<MatchSnapshotDto>(nameof(JoinMatch), matchId);
    /// <inheritdoc />
    public Task LeaveMatch(Guid matchId) => Conn.InvokeAsync(nameof(LeaveMatch), matchId);
    /// <inheritdoc />
    public Task<MoveAckDto> SubmitAction(SubmitActionDto request) => Conn.InvokeAsync<MoveAckDto>(nameof(SubmitAction), request);
    /// <inheritdoc />
    public Task<CommandAckDto> Resign(Guid matchId) => Conn.InvokeAsync<CommandAckDto>(nameof(Resign), matchId);
    /// <inheritdoc />
    public Task<CommandAckDto> SendEmote(Guid matchId, string emoteId) => Conn.InvokeAsync<CommandAckDto>(nameof(SendEmote), matchId, emoteId);
    /// <inheritdoc />
    public Task<CommandAckDto> OfferRematch(Guid matchId) => Conn.InvokeAsync<CommandAckDto>(nameof(OfferRematch), matchId);
    /// <inheritdoc />
    public Task<ChallengeDto> CreateChallenge(CreateChallengeDto request) => Conn.InvokeAsync<ChallengeDto>(nameof(CreateChallenge), request);
    /// <inheritdoc />
    public Task<AcceptChallengeResultDto> AcceptChallenge(string code) => Conn.InvokeAsync<AcceptChallengeResultDto>(nameof(AcceptChallenge), code);
    /// <inheritdoc />
    public Task<CommandAckDto> DeclineChallenge(string code) => Conn.InvokeAsync<CommandAckDto>(nameof(DeclineChallenge), code);
    /// <inheritdoc />
    public Task<CommandAckDto> CancelChallenge(string code) => Conn.InvokeAsync<CommandAckDto>(nameof(CancelChallenge), code);
    /// <inheritdoc />
    public Task<DirectMessageDto> SendDirectMessage(SendDirectMessageDto request) => Conn.InvokeAsync<DirectMessageDto>(nameof(SendDirectMessage), request);
    /// <inheritdoc />
    public Task MarkConversationRead(Guid friendId, long upToMessageId) => Conn.InvokeAsync(nameof(MarkConversationRead), friendId, upToMessageId);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _wanted = false;
        _sampling?.Cancel();
        if (_conn is not null) await _conn.DisposeAsync();
    }
}
