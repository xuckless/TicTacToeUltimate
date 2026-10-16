using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.Client.Stores;

/// <summary>Matchmaking queue state.</summary>
public sealed class QueueStore : StoreBase<QueueState>, IQueueStore
{
    private readonly IRealtimeTransport _rt;
    private bool _wasOnline;

    /// <summary>Creates the store.</summary>
    public QueueStore(IRealtimeTransport rt, ISoundService sound, IHaptics haptics, ISettingsStore settings) : base(QueueState.Idle)
    {
        _rt = rt;
        rt.QueueStatusReceived += status => Update(s => s.Phase is QueuePhase.Searching or QueuePhase.Entering
            ? s with { Phase = QueuePhase.Searching, Status = status } : s);
        // After a reconnect (e.g. the server restarted for a deploy) the server may have lost the queue: re-enter it.
        rt.PhaseChanged += () =>
        {
            var online = rt.Phase == ConnectionPhase.Online;
            if (online && !_wasOnline && State is { Phase: QueuePhase.Searching, TimeControl: { } tc }) _ = ReenterAsync(tc);
            _wasOnline = online;
        };
        rt.MatchFoundReceived += found =>
        {
            Update(s => s with { Phase = QueuePhase.Found, Found = found, Error = null });
            if (settings.State.Sfx) sound.Play(SoundCue.MatchFound);
            if (settings.State.Haptics) haptics.Play(HapticCue.Promotion);
        };
    }

    private async Task ReenterAsync(TimeControl timeControl)
    {
        try
        {
            var ticket = await _rt.EnterQueue(new Contracts.QueueRequestDto(timeControl));
            // AlreadyInQueue: the server kept us (short blip). Anything else ends the search with that error.
            if (!ticket.Accepted && ticket.Error != Contracts.ErrorCodes.AlreadyInQueue)
                Update(s => s.Phase == QueuePhase.Searching ? QueueState.Idle with { TimeControl = timeControl, Error = ticket.Error } : s);
        }
        catch (Exception) { /* next reconnect retries */ }
    }

    /// <inheritdoc />
    public async Task EnterAsync(TimeControl timeControl)
    {
        Update(_ => new QueueState(QueuePhase.Entering, timeControl, null, null, null));
        try
        {
            var ticket = await _rt.EnterQueue(new Contracts.QueueRequestDto(timeControl));
            // AlreadyInQueue (double tap, quick re-entry): the server still has us, so keep searching instead of showing Idle.
            var accepted = ticket.Accepted || ticket.Error == Contracts.ErrorCodes.AlreadyInQueue;
            Update(s => s.Phase == QueuePhase.Found ? s : accepted
                ? s with { Phase = QueuePhase.Searching, Error = null }
                : QueueState.Idle with { TimeControl = timeControl, Error = ticket.Error });
        }
        catch (Exception ex)
        {
            Update(_ => QueueState.Idle with { TimeControl = timeControl, Error = ErrorText.Code(ex) });
        }
    }

    /// <inheritdoc />
    public async Task LeaveAsync()
    {
        Update(s => s.Phase == QueuePhase.Found ? s : QueueState.Idle);
        try { await _rt.LeaveQueue(); } catch (Exception) { /* offline: server drops the ticket on disconnect */ }
    }

    /// <inheritdoc />
    public void Acknowledge() => Update(_ => QueueState.Idle);
}
