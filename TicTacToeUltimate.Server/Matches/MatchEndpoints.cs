using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;

namespace TicTacToeUltimate.Server.Matches;

/// <summary>REST: /api/challenges/{code}, /api/daily, /api/live.</summary>
public static class MatchEndpoints
{
    /// <summary>Maps WP2 REST endpoints.</summary>
    public static IEndpointRouteBuilder MapMatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/challenges/{code}", async (string code, IChallengeService challenges, CancellationToken ct) =>
            await challenges.GetAsync(code, ct) is { } dto ? Results.Ok(dto) : Results.NotFound()).AllowAnonymous();

        app.MapGet("/api/daily", async (HttpContext http, MatchGateway gateway, CancellationToken ct) =>
            Results.Ok(await DailyGamesAsync(http.User.ToTttUser().UserId, gateway, ct))).RequireAuthorization();

        app.MapGet("/api/live", async (HttpContext http, MatchGateway gateway, bool? friends, CancellationToken ct) =>
        {
            var me = http.User.ToTttUser();
            var list = gateway.LiveMatches().Where(i => i.Setup.TimeControl != TimeControl.Daily);
            if (friends != false)
            {
                var repo = http.RequestServices.GetService<IFriendRepository>();
                var ids = repo is null ? new HashSet<Guid>() : (await repo.ListFriendsAsync(me.UserId, ct)).ToHashSet();
                list = list.Where(i => ids.Contains(i.Setup.SeatA.UserId) || ids.Contains(i.Setup.SeatB.UserId));
            }
            return Results.Ok(list.Select(i => new LiveMatchDto(i.Setup.MatchId, i.Setup.TimeControl, i.Cards, i.Ply, i.SpectatorCount)).ToArray());
        }).RequireAuthorization();
        return app;
    }

    /// <summary>The user's active Daily games.</summary>
    public static async Task<DailyGameDto[]> DailyGamesAsync(Guid userId, MatchGateway gateway, CancellationToken ct)
    {
        var rt = gateway.Runtime;
        var result = new List<DailyGameDto>();
        if (rt.DailyRepo is { } repo)
        {
            foreach (var m in await repo.ListActiveForUserAsync(userId, ct))
            {
                if (m.Ended) continue;
                var seat = m.Setup.SeatA.UserId == userId ? 0 : 1;
                var opp = seat == 0 ? m.Setup.SeatB : m.Setup.SeatA;
                var state = rt.Rules.Initial(m.Setup.Rules);
                foreach (var a in m.Actions) { var s = rt.Rules.Apply(state, a.Action); if (s.Ok) state = s.State; }
                var myColor = (seat == 0) ^ state.Swapped ? Color.X : Color.O;
                PlayerCardDto card;
                try { card = await rt.Cards.GetCardAsync(opp.UserId, RatingPool.Daily, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { card = MatchModes.FallbackCard(opp); }
                result.Add(new DailyGameDto(m.Setup.MatchId, card, myColor, state.Phase != GamePhase.Ended && state.ToMove == myColor,
                    m.TurnDeadline?.ToUnixTimeMilliseconds(), state.Ply));
            }
            return [.. result];
        }
        foreach (var i in gateway.LiveMatches().Where(i => i.Setup.TimeControl == TimeControl.Daily))
        {
            int? seat = i.Setup.SeatA.UserId == userId ? 0 : i.Setup.SeatB.UserId == userId ? 1 : null;
            if (seat is not { } s) continue;
            var myColor = (s == 0) ^ i.Swapped ? Color.X : Color.O;
            result.Add(new DailyGameDto(i.Setup.MatchId, i.Cards[1 - s], myColor, i.ToMoveSeat == s, i.DeadlineAtMs, i.Ply));
        }
        return [.. result];
    }
}
