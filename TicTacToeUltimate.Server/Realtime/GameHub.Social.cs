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
        // Recipient: in-app notification when connected, push when not.
        var preview = request.Body.Length > 80 ? request.Body[..77] + "…" : request.Body;
        var n = new NotificationDto(NotificationKind.Message, me.DisplayName, preview, null, $"social/chat/{me.UserId}");
        if (PresenceTracker.IsOnline(request.ToUserId)) _ = services.GetRequiredService<TicTacToeUltimate.Server.Abstractions.IUserNotifier>().ToUserAsync(request.ToUserId, c => c.Notification(n));
        else _ = services.GetRequiredService<TicTacToeUltimate.Server.Abstractions.IPushSender>().SendAsync(request.ToUserId, n, CancellationToken.None);
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
