using System.Net.Http.Json;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Bots;

/// <summary>Asks the external bot service (bots/, Go) to connect a bot for a waiting player.</summary>
public interface IBotDispatcher
{
    /// <summary>Requests one bot session for <paramref name="timeControl"/>. Fire-and-forget; never throws.</summary>
    ValueTask RequestAsync(TimeControl timeControl, Guid forUserId, int forRating, CancellationToken ct);
}

/// <summary>No bot service configured: dispatch is a no-op (bots that connect on their own still get matched).</summary>
public sealed class NullBotDispatcher : IBotDispatcher
{
    /// <inheritdoc />
    public ValueTask RequestAsync(TimeControl timeControl, Guid forUserId, int forRating, CancellationToken ct) => ValueTask.CompletedTask;
}

/// <summary>
/// HTTP dispatcher: <c>POST {Bots:Service:Url}/v1/sessions</c> with a bearer token. The bot service spawns one goroutine that
/// connects over <c>/bot/ws</c>, seeks <paramref name="timeControl"/> and plays exactly one game; the matchmaker pairs it with
/// the longest-waiting player once real people have had priority (<c>Matchmaking:BotFallbackSeconds</c>).
/// </summary>
public sealed class HttpBotDispatcher(HttpClient http, TttOptions options, ILogger<HttpBotDispatcher> log) : IBotDispatcher
{
    /// <summary>Request body (camelCase JSON).</summary>
    public sealed record SessionRequest(string TimeControl, Guid OpponentUserId, int OpponentRating, string RequestId);

    /// <inheritdoc />
    public async ValueTask RequestAsync(TimeControl timeControl, Guid forUserId, int forRating, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.Bots.Service.Url.TrimEnd('/') + "/"), "v1/sessions"))
            {
                Content = JsonContent.Create(new SessionRequest(timeControl.ToString(), forUserId, forRating, Guid.NewGuid().ToString("N"))),
            };
            req.Headers.Authorization = new("Bearer", options.Bots.Service.Token);
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) log.LogWarning("Bot service refused a session for {Tc}: {Status}", timeControl, (int)res.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            log.LogWarning("Bot service unreachable ({Message}); waiting players stay in the queue", ex.Message);
        }
    }
}
