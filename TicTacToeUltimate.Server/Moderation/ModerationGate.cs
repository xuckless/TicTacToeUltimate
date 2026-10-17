using System.Security.Claims;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.Moderation;

/// <summary>
/// Runs after authentication on /api and /hubs: remembers which devices an account uses (X-Ttt-Device header, or ?device= on
/// the hub where browsers can't set headers) and refuses banned accounts and banned devices with 403 <c>banned</c> plus the
/// penalty, so the client can show the ban notice. Standing, appeals and config stay reachable.
/// </summary>
public sealed class ModerationGate(RequestDelegate next)
{
    private static readonly string[] Open = ["/api/me/standing", "/api/appeals", "/api/config", "/api/diagnostics"];

    public async Task InvokeAsync(HttpContext ctx, ModerationService moderation)
    {
        var path = ctx.Request.Path;
        if (!path.StartsWithSegments("/api") && !path.StartsWithSegments("/hubs")) { await next(ctx); return; }
        var device = ctx.Request.Headers["X-Ttt-Device"].FirstOrDefault() ?? ctx.Request.Query["device"].FirstOrDefault();
        var hash = device is { Length: > 0 and <= 128 } ? moderation.DeviceHash(device) : null;
        Guid? userId = null;
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            var me = ctx.User.ToTttUser();
            if (me.Kind == AccountKind.Bot) { await next(ctx); return; }
            userId = me.UserId;
            if (hash is not null) await moderation.TouchDeviceAsync(me.UserId, hash, ctx.RequestAborted);
        }
        if (userId is null && hash is null) { await next(ctx); return; }
        var ban = await moderation.BanAsync(userId, hash, ctx.RequestAborted);
        if (ban is null || Open.Any(o => path.StartsWithSegments(o))) { await next(ctx); return; }
        var dto = await moderation.ToDtoAsync(ban, ctx.RequestAborted);
        await Results.Problem(statusCode: 403, title: ErrorCodes.Banned,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Banned, ["penalty"] = dto }).ExecuteAsync(ctx);
    }
}
