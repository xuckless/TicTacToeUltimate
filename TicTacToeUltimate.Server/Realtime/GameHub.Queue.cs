using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Matchmaking;

namespace TicTacToeUltimate.Server.Realtime;

// WP2: queue.
public sealed partial class GameHub
{
    /// <summary>Enters the matchmaking queue for a time control.</summary>
    public async Task<QueueTicketDto> EnterQueue(QueueRequestDto request)
    {
        var me = Me;
        var ct = Context.ConnectionAborted;
        var tc = request.TimeControl;
        var now = NowMs;
        QueueTicketDto Reject(string e) => new(false, e, tc, now);
        var opts = services.GetRequiredService<TttOptions>();
        var mode = MatchModes.Options(opts, tc);
        if (me.IsBot) return Reject(ErrorCodes.Validation);
        if (services.GetService<TicTacToeUltimate.Server.Matches.ServerDrain>() is { Draining: true }) return Reject(ErrorCodes.ServerUpdating);
        if (me.IsGuest && (!mode.GuestsAllowed || MatchModes.IsRankedLike(tc))) return Reject(ErrorCodes.GuestNotAllowed);
        var users = services.GetService<IUserRepository>();
        var rec = users is null ? null : await users.GetAsync(me.UserId, ct);
        if (!me.IsGuest && users is not null && rec?.Username is null) return Reject(ErrorCodes.UsernameRequired);
        var activity = services.GetRequiredService<UserActivityRegistry>();
        if (tc == TimeControl.Daily)
        {
            var daily = services.GetService<IDailyMatchRepository>();
            var count = daily is null ? activity.DailyCount(me.UserId) : (await daily.ListActiveForUserAsync(me.UserId, ct)).Count;
            if (count >= opts.Daily.MaxConcurrent) return Reject(ErrorCodes.DailyLimit);
        }
        var rating = 1200;
        if (!me.IsGuest && services.GetService<IRatingRepository>() is { } ratings)
            rating = (int)Math.Round((await ratings.GetAsync(me.UserId, RatingPools.For(tc), ct)).Rating);
        if (activity.TryEnterQueue(me.UserId, tc) is { } err) return Reject(err);
        Matchmaker.Post(new EnterQueueCommand(new QueueEntry(me.UserId, rec?.DisplayName ?? me.DisplayName, me.Kind, tc, rating,
            PresenceTracker.Region(me.UserId), now)));
        return new QueueTicketDto(true, null, tc, now);
    }

    /// <summary>Leaves every queue.</summary>
    public Task LeaveQueue()
    {
        Matchmaker.Post(new LeaveQueueCommand(Me.UserId));
        return Task.CompletedTask;
    }
}
