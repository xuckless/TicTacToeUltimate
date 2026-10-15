using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Social;

namespace TicTacToeUltimate.Server.Realtime;

// WP3: direct messages over the hub.
public sealed partial class GameHub
{
    /// <summary>Sends a DM to a mutual, non-blocked friend. Throws HubException(code) on rejection.</summary>
    public async Task<DirectMessageDto> SendDirectMessage(SendDirectMessageDto request)
    {
        var me = Me;
        if (me.Kind != AccountKind.Account) throw new HubException(ErrorCodes.GuestNotAllowed);
        var social = services.GetRequiredService<SocialService>();
        var (message, error) = await social.SendAsync(me.UserId, request.ToUserId, request.Body, Context.ConnectionAborted);
        if (error is not null)
        {
            logger.LogDebug("DM rejected: {Code}", error);
            throw new HubException(error);
        }
        return message!;
    }

    /// <summary>Marks messages from a friend as read up to an id.</summary>
    public async Task MarkConversationRead(Guid friendId, long upToMessageId)
    {
        var me = Me;
        if (me.Kind != AccountKind.Account) throw new HubException(ErrorCodes.GuestNotAllowed);
        await services.GetRequiredService<SocialService>().MarkReadAsync(me.UserId, friendId, upToMessageId, Context.ConnectionAborted);
    }
}
