using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Client.Stores;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.Client.Tests;

internal sealed class FakeTransport : IRealtimeTransport
{
    public Func<Guid, Task<MatchSnapshotDto>> OnJoin { get; set; } = _ => throw new InvalidOperationException("no snapshot");
    public Func<SubmitActionDto, Task<MoveAckDto>> OnSubmit { get; set; } = r => Task.FromResult(new MoveAckDto(true, null, 0, r.ExpectedPly + 1));
    public int JoinCalls;
    public ConcurrentQueue<SubmitActionDto> Submitted { get; } = new();

    public ConnectionPhase Phase => ConnectionPhase.Online;
    public int ReconnectAttempt => 0;
    public Func<Task>? Handshake { get; set; }
#pragma warning disable CS0067
    public event Action? PhaseChanged;
    public event Action<bool>? Connected;
    public event Action<QueueStatusDto>? QueueStatusReceived;
    public event Action<MatchFoundDto>? MatchFoundReceived;
    public event Action<MatchEventEnvelope>? MatchEventReceived;
    public event Action<PostGameSummaryDto>? PostGameSummaryReceived;
    public event Action<ChallengeDto>? ChallengeReceived;
    public event Action<ChallengeDto>? ChallengeUpdated;
    public event Action<PresenceDto>? PresenceChanged;
    public event Action<FriendRequestDto>? FriendRequestReceived;
    public event Action? FriendsChanged;
    public event Action<DirectMessageDto>? DirectMessageReceived;
    public event Action<NotificationDto>? NotificationReceived;
#pragma warning restore CS0067

    public void Push(MatchEventEnvelope e) => MatchEventReceived?.Invoke(e);
    public void RaiseConnected(bool reconnect) => Connected?.Invoke(reconnect);

    public Task StartAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task<ServerHelloDto> Hello(ClientHelloDto hello) => throw new NotSupportedException();
    public Task Pong(int pingId) => Task.CompletedTask;
    public Task<long> ServerTime() => Task.FromResult(0L);
    public Task<QueueTicketDto> EnterQueue(QueueRequestDto request) => Task.FromResult(new QueueTicketDto(true, null, request.TimeControl, 0));
    public Task LeaveQueue() => Task.CompletedTask;
    public Task<MatchSnapshotDto> JoinMatch(Guid matchId) { Interlocked.Increment(ref JoinCalls); return OnJoin(matchId); }
    public Task LeaveMatch(Guid matchId) => Task.CompletedTask;
    public Task<MoveAckDto> SubmitAction(SubmitActionDto request) { Submitted.Enqueue(request); return OnSubmit(request); }
    public Task<CommandAckDto> Resign(Guid matchId) => Task.FromResult(new CommandAckDto(true, null));
    public Task<CommandAckDto> SendEmote(Guid matchId, string emoteId) => Task.FromResult(new CommandAckDto(true, null));
    public Task<CommandAckDto> OfferRematch(Guid matchId) => Task.FromResult(new CommandAckDto(true, null));
    public Task<ChallengeDto> CreateChallenge(CreateChallengeDto request) => throw new NotSupportedException();
    public Task<AcceptChallengeResultDto> AcceptChallenge(string code) => throw new NotSupportedException();
    public Task<CommandAckDto> DeclineChallenge(string code) => Task.FromResult(new CommandAckDto(true, null));
    public Task<CommandAckDto> CancelChallenge(string code) => Task.FromResult(new CommandAckDto(true, null));
    public Task<DirectMessageDto> SendDirectMessage(SendDirectMessageDto request) => throw new NotSupportedException();
    public Task MarkConversationRead(Guid friendId, long upToMessageId) => Task.CompletedTask;
}

internal sealed class RecordingHaptics : IHaptics
{
    public ConcurrentQueue<HapticCue> Played { get; } = new();
    public bool IsSupported => true;
    public void Play(HapticCue cue) => Played.Enqueue(cue);
}

internal sealed class RecordingSound : ISoundService
{
    public ConcurrentQueue<SoundCue> Played { get; } = new();
    public int? Heartbeat { get; private set; }
    public (double Sfx, double Music) Volumes { get; private set; }
    public ValueTask InitAsync() => ValueTask.CompletedTask;
    public ValueTask UnlockAsync() => ValueTask.CompletedTask;
    public void Play(SoundCue cue) => Played.Enqueue(cue);
    public void SetHeartbeat(int? beatsPerMinute) => Heartbeat = beatsPerMinute;
    public void SetMusic(bool on) { }
    public void SetVolumes(double sfx, double music) => Volumes = (sfx, music);
}

internal sealed class MatchHarness
{
    public static readonly Guid MatchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
    public FakeTransport Rt { get; } = new();
    public RecordingHaptics Haptics { get; } = new();
    public RecordingSound Sound { get; } = new();
    public ClockSync Clock { get; }
    public SettingsStore Settings { get; }
    public MatchStore Store { get; }

    public MatchHarness()
    {
        Clock = new ClockSync(Time);
        Settings = new SettingsStore(new InMemoryKeyValueStore(), Sound);
        Store = new MatchStore(Rt, Clock, Settings, Sound, Haptics, Time, NullLogger<MatchStore>.Instance);
    }

    public long Now => Time.GetUtcNow().ToUnixTimeMilliseconds();

    public static PlayerCardDto Card(string name) => new(Guid.NewGuid(), name, "a1", null, AccountKind.Account, null, 1500, false);

    public MatchSnapshotDto Snapshot(long seq = 5, int? yourSeat = 0, int toMoveSeat = 0, ClockDto? clock = null) => new(
        MatchId, seq, MatchMode.Casual, TimeControl.CasualX2, false, RuleSetDto.From(RuleSet.Default),
        [new SeatDto(0, Card("me"), Color.X, true, 20), new SeatDto(1, Card("opp"), Color.O, true, 20)],
        false, [], ".........", 0, GamePhase.AwaitingPlacement, Color.X, toMoveSeat, [0, 1, 2, 3, 4, 5, 6, 7, 8], null,
        clock, null, yourSeat, 0, true, Now);

    public MatchEventEnvelope Env(long seq, MatchEvent e) => new(MatchId, seq, Now, e);

    public static ActionApplied Placed(int ply, int seat, int cell, Color color) => new(
        new ActionDto(ply, seat, ActionKind.Place, cell, color, null, 1000, false), ".........", null, false,
        [new SeatDto(0, Card("me"), Color.X, true, 20), new SeatDto(1, Card("opp"), Color.O, true, 20)]);
}
