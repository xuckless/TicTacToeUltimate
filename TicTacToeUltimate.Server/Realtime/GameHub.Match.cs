using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;

namespace TicTacToeUltimate.Server.Realtime;

// WP2: match surface.
public sealed partial class GameHub
{
    /// <summary>Attach this connection as participant, or as spectator (friend of either seat, accounts only).</summary>
    public async Task<MatchSnapshotDto> JoinMatch(Guid matchId)
    {
        var me = Me;
        var gw = Gateway;
        var actor = await gw.GetOrActivateAsync(matchId) ?? throw new HubException(ErrorCodes.MatchNotFound);
        var setup = actor.Setup;
        var spectator = me.UserId != setup.SeatA.UserId && me.UserId != setup.SeatB.UserId;
        if (spectator)
        {
            if (me.Kind != AccountKind.Account) throw new HubException(ErrorCodes.SpectateNotAllowed);
            var friends = services.GetService<IFriendRepository>();
            var ct = Context.ConnectionAborted;
            if (friends is null || !(await friends.AreFriendsAsync(me.UserId, setup.SeatA.UserId, ct)
                                     || await friends.AreFriendsAsync(me.UserId, setup.SeatB.UserId, ct)))
                throw new HubException(ErrorCodes.SpectateNotAllowed);
        }
        try { return await gw.AttachConnectionAsync(matchId, me.UserId, Context.ConnectionId, spectator, Context.ConnectionAborted); }
        catch (MatchAccessException e) { throw new HubException(e.Code); }
    }

    /// <summary>Detaches this connection only; clocks keep running.</summary>
    public Task LeaveMatch(Guid matchId)
    {
        Gateway.DetachConnection(matchId, Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>Submits a placement or pie decision.</summary>
    public Task<MoveAckDto> SubmitAction(SubmitActionDto request)
    {
        GameAction action;
        switch (request.Kind)
        {
            case ActionKind.Place when request.Cell is { } cell: action = GameAction.Place(cell); break;
            case ActionKind.Swap: action = GameAction.Swap; break;
            case ActionKind.KeepSides: action = GameAction.KeepSides; break;
            default: return Task.FromResult(new MoveAckDto(false, ErrorCodes.Validation, 0, -1));
        }
        return Gateway.SubmitActionAsync(request.MatchId, Me.UserId, request.ExpectedPly, action, request.ActionId, Context.ConnectionAborted);
    }

    /// <summary>Resigns (ply 0 = abort).</summary>
    public Task<CommandAckDto> Resign(Guid matchId) => Gateway.ResignAsync(matchId, Me.UserId, Context.ConnectionAborted);

    /// <summary>Sends an emote from <see cref="EmoteCatalog"/>.</summary>
    public Task<CommandAckDto> SendEmote(Guid matchId, string emoteId) => Gateway.EmoteAsync(matchId, Me.UserId, emoteId, Context.ConnectionAborted);

    /// <summary>Offers a rematch (unrated only, within the window).</summary>
    public Task<CommandAckDto> OfferRematch(Guid matchId) => Draining
        ? Task.FromResult(new CommandAckDto(false, ErrorCodes.ServerUpdating))
        : Gateway.OfferRematchAsync(matchId, Me.UserId, Context.ConnectionAborted);

    private bool Draining => services.GetService<TicTacToeUltimate.Server.Matches.ServerDrain>() is { Draining: true };
}
