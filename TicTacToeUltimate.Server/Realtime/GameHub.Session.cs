using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Challenges;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Matchmaking;
using TicTacToeUltimate.Server.Presence;

namespace TicTacToeUltimate.Server.Realtime;

// WP2: session (presence, pinger, hello).
public sealed partial class GameHub
{
    private MatchGateway Gateway => services.GetRequiredService<MatchGateway>();
    private PresenceTracker PresenceTracker => services.GetRequiredService<PresenceTracker>();
    private Matchmaker Matchmaker => services.GetRequiredService<Matchmaker>();
    private long NowMs => services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeMilliseconds();

    private partial Task OnConnectedSessionAsync()
    {
        var me = Me;
        PresenceTracker.Connect(me.UserId, Context.ConnectionId);
        Matchmaker.Post(new ReconnectQueueCommand(me.UserId));
        return Task.CompletedTask;
    }

    private partial Task OnDisconnectedSessionAsync(Exception? exception)
    {
        var me = Me;
        Gateway.DetachConnectionEverywhere(Context.ConnectionId);
        PresenceTracker.Disconnect(me.UserId, Context.ConnectionId);
        if (!PresenceTracker.IsOnline(me.UserId)) Matchmaker.Post(new DisconnectQueueCommand(me.UserId));
        return Task.CompletedTask;
    }

    /// <summary>Handshake: protocol check, identity, active matches, daily turns, pending challenges.</summary>
    public async Task<ServerHelloDto> Hello(ClientHelloDto hello)
    {
        if (hello.ProtocolVersion != ProtocolInfo.Version) throw new HubException(ErrorCodes.ProtocolVersion);
        var me = Me;
        var ct = Context.ConnectionAborted;
        PresenceTracker.SetTimeZone(me.UserId, hello.TimeZone);
        var meDto = await BuildMeAsync(me, hello.TimeZone, ct);
        var gw = Gateway;
        var daily = await MatchEndpoints.DailyGamesAsync(me.UserId, gw, ct);
        ChallengeDto[] pending = [];
        if (services.GetService<IChallengeRepository>() is not null && me.Kind == AccountKind.Account)
        {
            try { pending = await services.GetRequiredService<ChallengeService>().PendingForAsync(me.UserId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Pending challenges failed"); }
        }
        return new ServerHelloDto(meDto, NowMs, [.. gw.ActiveMatchesFor(me.UserId)], daily.Count(d => d.YourTurn), pending);
    }

    private async Task<MeDto> BuildMeAsync(TttUser me, string timeZone, CancellationToken ct)
    {
        var users = services.GetService<IUserRepository>();
        var rec = users is null ? null : await users.GetAsync(me.UserId, ct);
        var ratings = new List<RatingDto>();
        if (me.Kind == AccountKind.Account && services.GetService<IRatingRepository>() is { } repo)
        {
            foreach (var pool in RatingPools.Visible)
            {
                var r = await repo.GetAsync(me.UserId, pool, ct);
                ratings.Add(new RatingDto(pool, (int)Math.Round(r.Rating), (int)Math.Round(r.Rd), r.Games, r.Games < 10, null, 0));
            }
        }
        return new MeDto(me.UserId, me.Kind, rec?.DisplayName ?? me.DisplayName, rec?.Username,
            me.Kind == AccountKind.Account && rec is not null && rec.Username is null, rec?.AvatarId ?? "default", rec?.Country,
            rec?.TimeZone ?? timeZone, rec?.RenamesLeft ?? 1, [.. ratings]);
    }

    /// <summary>Pong for a server Ping; feeds RTT to live matches.</summary>
    public Task Pong(int pingId)
    {
        var me = Me;
        if (services.GetRequiredService<RttTracker>().OnPong(me.UserId, pingId) is { } r)
            Gateway.PostRtt(me.UserId, r.RttMs, r.HalfRttMs);
        return Task.CompletedTask;
    }

    /// <summary>Server epoch ms.</summary>
    public Task<long> ServerTime() => Task.FromResult(NowMs);
}
