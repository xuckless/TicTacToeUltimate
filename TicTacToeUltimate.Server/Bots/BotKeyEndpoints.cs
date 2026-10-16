using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;

namespace TicTacToeUltimate.Server.Bots;

/// <summary>REST endpoints for bot keys and the bot WebSocket endpoint.</summary>
public static partial class BotKeyEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9_]{3,24}$")]
    private static partial Regex BotNameRegex();

    /// <summary>Maps <c>GET/POST/DELETE /api/bots/keys</c> (Account policy).</summary>
    public static void MapBotKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/bots/keys").RequireAuthorization(TttPolicies.Account);

        group.MapGet("/", async (HttpContext http, CancellationToken ct) =>
        {
            if (http.RequestServices.GetService<IBotKeyRepository>() is not { } keys) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var user = http.User.ToTttUser();
            var list = await keys.ListForOwnerAsync(user.UserId, ct).ConfigureAwait(false);
            return Results.Ok(list.Select(k => new BotKeyDto(k.KeyId, k.BotUserId, k.BotName, k.CreatedAt, k.RevokedAt)).ToArray());
        });

        group.MapPost("/", async (HttpContext http, CreateBotKeyDto request, TimeProvider time, CancellationToken ct) =>
        {
            if (http.RequestServices.GetService<IBotKeyRepository>() is not { } keys ||
                http.RequestServices.GetService<IUserRepository>() is not { } users)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (request?.BotName is null || !BotNameRegex().IsMatch(request.BotName))
                return Results.Problem(statusCode: 400, title: ErrorCodes.Validation, detail: "BotName must be 3-24 letters, digits or '_'");
            var owner = http.User.ToTttUser();
            var now = time.GetUtcNow();
            var botUserId = Guid.NewGuid();
            var keyId = BotKeyFormat.RandomToken(10);
            var secret = BotKeyFormat.RandomToken(32);
            await users.CreateAsync(new UserRecord(botUserId, AccountKind.Bot, null, request.BotName, "bot", null, "UTC", 0,
                null, null, now, now), ct).ConfigureAwait(false);
            await keys.CreateAsync(new BotKeyRecord(keyId, botUserId, owner.UserId, request.BotName, BotKeyFormat.Hash(secret), now, null), ct)
                .ConfigureAwait(false);
            return Results.Ok(new CreatedBotKeyDto(keyId, $"{BotKeyDefaults.Prefix}{keyId}_{secret}", botUserId));
        });

        group.MapDelete("/{keyId}", async (HttpContext http, string keyId, TimeProvider time, CancellationToken ct) =>
        {
            if (http.RequestServices.GetService<IBotKeyRepository>() is not { } keys) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var owner = http.User.ToTttUser();
            return await keys.RevokeAsync(keyId, owner.UserId, time.GetUtcNow(), ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }

    /// <summary>Maps the bot WebSocket endpoint at <see cref="BotRoutes.WebSocket"/>.</summary>
    public static void MapBotWebSocket(this IEndpointRouteBuilder app) =>
        app.Map(BotRoutes.WebSocket, HandleWebSocketAsync).AllowAnonymous();

    private static async Task HandleWebSocketAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var auth = await http.AuthenticateAsync(BotKeyDefaults.Scheme).ConfigureAwait(false);
        var protocolOk = http.Request.Query["protocol"].ToString() == ProtocolInfo.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var sp = http.RequestServices;
        if (!auth.Succeeded || auth.Principal is null)
        {
            await CloseAsync(socket, BotCloseCodes.BadKey, "bad_key").ConfigureAwait(false);
            return;
        }
        if (!protocolOk)
        {
            await CloseAsync(socket, BotCloseCodes.ProtocolMismatch, "protocol_mismatch").ConfigureAwait(false);
            return;
        }
        var session = new BotSession(socket, auth.Principal.ToTttUser(), sp.GetService<IMatchGateway>(), sp.GetService<IChallengeService>(),
            sp.GetRequiredService<BotPool>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<BotSession>(), sp.GetService<BotSessionSettings>());
        // Closes with the request, or when a deploy's drain ends (botd reconnects to the next server).
        var closeAll = sp.GetService<TicTacToeUltimate.Server.Matches.ServerDrain>()?.CloseAll ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, closeAll);
        await session.RunAsync(linked.Token).ConfigureAwait(false);
    }

    private static async Task CloseAsync(WebSocket socket, int code, string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, cts.Token).ConfigureAwait(false);
            var buf = new byte[256];
            while (socket.State is WebSocketState.CloseSent)
            {
                var r = await socket.ReceiveAsync(buf, cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
    }
}
