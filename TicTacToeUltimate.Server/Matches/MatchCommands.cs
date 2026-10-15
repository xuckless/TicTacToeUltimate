using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.Matches;

/// <summary>Thrown to callers whose command reached an actor that closed; the gateway re-activates and retries.</summary>
internal sealed class MatchClosedException() : Exception("match_closed");

/// <summary>Actor command; <see cref="Ts"/> is the ingress timestamp (TimeProvider.GetTimestamp) taken under the ingress lock.</summary>
internal abstract record MatchCommand(long Ts)
{
    public virtual void Fail(Exception e) { }
}

internal abstract record MatchAsk<T>(long Ts, TaskCompletionSource<T> Reply) : MatchCommand(Ts)
{
    public override void Fail(Exception e) => Reply.TrySetException(e);
}

internal sealed record AttachCmd(long Ts, Guid UserId, string Key, string? ConnectionId, IMatchObserver? Observer, bool AllowSpectator,
    TaskCompletionSource<MatchSnapshotDto> R) : MatchAsk<MatchSnapshotDto>(Ts, R);
internal sealed record DetachCmd(long Ts, string Key) : MatchCommand(Ts);
internal sealed record SubmitCmd(long Ts, Guid UserId, int ExpectedPly, GameAction Action, Guid ActionId,
    TaskCompletionSource<MoveAckDto> R) : MatchAsk<MoveAckDto>(Ts, R);
internal sealed record ResignCmd(long Ts, Guid UserId, TaskCompletionSource<CommandAckDto> R) : MatchAsk<CommandAckDto>(Ts, R);
internal sealed record EmoteCmd(long Ts, Guid UserId, string EmoteId, TaskCompletionSource<CommandAckDto> R) : MatchAsk<CommandAckDto>(Ts, R);
internal sealed record RematchCmd(long Ts, Guid UserId, TaskCompletionSource<CommandAckDto> R) : MatchAsk<CommandAckDto>(Ts, R);
internal sealed record RttCmd(long Ts, Guid UserId, int RttMs, int HalfRttMs) : MatchCommand(Ts);
/// <summary>Token -1 = unconditional deadline check (Daily sweeper).</summary>
internal sealed record TickCmd(long Ts, long Token) : MatchCommand(Ts);
internal sealed record AbandonCheckCmd(long Ts) : MatchCommand(Ts);
internal sealed record SnapshotCmd(long Ts, Guid? Viewer, TaskCompletionSource<MatchSnapshotDto> R) : MatchAsk<MatchSnapshotDto>(Ts, R);
internal sealed record ShutdownCmd(long Ts, string Detail, TaskCompletionSource<bool> R) : MatchAsk<bool>(Ts, R);
internal sealed record CloseCmd(long Ts) : MatchCommand(Ts);
internal sealed record FlushSpectatorsCmd(long Ts) : MatchCommand(Ts);
internal sealed record FlushCmd(long Ts, TaskCompletionSource<bool> R) : MatchAsk<bool>(Ts, R);

internal abstract record OutboxItem;
internal sealed record GroupAddItem(string ConnectionId) : OutboxItem;
internal sealed record GroupRemoveItem(string ConnectionId) : OutboxItem;
internal sealed record ObserverAddItem(string Key, IMatchObserver Observer) : OutboxItem;
internal sealed record ObserverRemoveItem(string Key) : OutboxItem;
internal sealed record PublishItem(MatchEventEnvelope Envelope) : OutboxItem;
internal sealed record SinkItem(CompletedMatch Match) : OutboxItem;
internal sealed record CallbackItem(Func<Task> Run) : OutboxItem;
internal sealed record BarrierItem(TaskCompletionSource<bool> Done) : OutboxItem;

/// <summary>Thread-safe read-only summary of a live actor (for /api/live, /api/daily, presence).</summary>
public sealed record LiveMatchInfo(MatchSetup Setup, PlayerCardDto[] Cards, int Ply, int SpectatorCount, bool Ended,
    bool Swapped, Color ToMove, int? ToMoveSeat, long? DeadlineAtMs);

/// <summary>Callbacks from an actor into its owner (gateway).</summary>
internal sealed record MatchActorHooks(Action<MatchActor> OnEnded, Action<MatchActor> OnClosed, Func<MatchSetup, bool> CreateRematch);
