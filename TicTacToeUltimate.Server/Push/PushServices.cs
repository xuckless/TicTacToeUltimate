using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Persistence.Postgres;

namespace TicTacToeUltimate.Server.Push;

/// <summary>A device push token.</summary>
public sealed record PushTokenRecord(Guid UserId, string Platform /* ios | android */, string Token, DateTimeOffset UpdatedAt);

/// <summary>Push tokens per user (one row per device).</summary>
public interface IPushTokenRepository
{
    Task UpsertAsync(PushTokenRecord token, CancellationToken ct);
    Task RemoveAsync(string token, CancellationToken ct);
    Task<IReadOnlyList<PushTokenRecord>> ListForUserAsync(Guid userId, CancellationToken ct);
    /// <summary>Distinct users with at least one token (retention scheduler).</summary>
    Task<IReadOnlyList<Guid>> UsersWithTokensAsync(CancellationToken ct);
    Task RemoveUserAsync(Guid userId, CancellationToken ct);
}

public sealed class InMemoryPushTokenRepository : IPushTokenRepository
{
    private readonly ConcurrentDictionary<string, PushTokenRecord> _byToken = new(StringComparer.Ordinal);
    public Task UpsertAsync(PushTokenRecord t, CancellationToken ct) { _byToken[t.Token] = t; return Task.CompletedTask; }
    public Task RemoveAsync(string token, CancellationToken ct) { _byToken.TryRemove(token, out _); return Task.CompletedTask; }
    public Task<IReadOnlyList<PushTokenRecord>> ListForUserAsync(Guid userId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PushTokenRecord>>(_byToken.Values.Where(t => t.UserId == userId).ToList());
    public Task<IReadOnlyList<Guid>> UsersWithTokensAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Guid>>(_byToken.Values.Select(t => t.UserId).Distinct().ToList());
    public Task RemoveUserAsync(Guid userId, CancellationToken ct)
    {
        foreach (var k in _byToken.Where(kv => kv.Value.UserId == userId).Select(kv => kv.Key).ToArray()) _byToken.TryRemove(k, out _);
        return Task.CompletedTask;
    }
}

public sealed class PgPushTokenRepository(PgDb db) : IPushTokenRepository
{
    private sealed class Row { public Guid user_id; public string platform = ""; public string token = ""; public DateTime updated_at; }

    public async Task UpsertAsync(PushTokenRecord t, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("""
            insert into push_tokens (token, user_id, platform, updated_at) values (@Token, @UserId, @Platform, @at)
            on conflict (token) do update set user_id = excluded.user_id, platform = excluded.platform, updated_at = excluded.updated_at
            """, new { t.Token, t.UserId, t.Platform, at = t.UpdatedAt.UtcDateTime });
    }
    public async Task RemoveAsync(string token, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("delete from push_tokens where token = @token", new { token });
    }
    public async Task<IReadOnlyList<PushTokenRecord>> ListForUserAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<Row>("select token, user_id, platform, updated_at from push_tokens where user_id = @userId", new { userId });
        return rows.Select(r => new PushTokenRecord(r.user_id, r.platform, r.token, PgDb.Dto(r.updated_at))).ToList();
    }
    public async Task<IReadOnlyList<Guid>> UsersWithTokensAsync(CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Guid>("select distinct user_id from push_tokens")).ToList();
    }
    public async Task RemoveUserAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync("delete from push_tokens where user_id = @userId", new { userId });
    }
}

/// <summary>Ttt:Push:Apns. Empty PrivateKeyP8 = pushes are only logged.</summary>
public sealed class PushOptions
{
    public ApnsOptions Apns { get; set; } = new();
    /// <summary>Local hour (user time zone) for the streak reminder.</summary>
    public int StreakReminderHour { get; set; } = 19;
    /// <summary>Local hour for the "come back" challenge; sent after InactiveDays without a game.</summary>
    public int ComebackHour { get; set; } = 18;
    public int InactiveDays { get; set; } = 3;
}
public sealed class ApnsOptions
{
    public string TeamId { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string BundleId { get; set; } = "com.wedirectsoftwares.tictactoeultimate";
    /// <summary>Contents of the .p8 key (PEM), or a path to it.</summary>
    public string PrivateKeyP8 { get; set; } = "";
    /// <summary>Development builds register against the sandbox gateway.</summary>
    public bool Sandbox { get; set; }
}

/// <summary>
/// APNs over HTTP/2 with token (ES256 JWT) authentication. Sends to every iOS token the user has; drops tokens APNs reports
/// as gone. Android/FCM is a follow-up: those tokens are stored but skipped.
/// </summary>
public sealed class ApnsPushSender(IPushTokenRepository tokens, IHttpClientFactory http, IOptions<TttOptions> ttt, IOptions<PushOptions> push,
    TimeProvider time, ILogger<ApnsPushSender> log) : IPushSender
{
    private string? _jwt;
    private long _jwtIssuedAtSec;
    private readonly Lock _gate = new();

    /// <summary>True when APNs credentials are configured.</summary>
    public bool Enabled => push.Value.Apns is { TeamId: { Length: > 0 }, KeyId: { Length: > 0 }, PrivateKeyP8: { Length: > 0 } };

    public async Task SendAsync(Guid userId, NotificationDto n, CancellationToken ct)
    {
        var list = await tokens.ListForUserAsync(userId, ct);
        if (list.Count == 0) return;
        if (!Enabled)
        {
            log.LogInformation("Push (not configured) to {User}: {Title} — {Body}", userId, n.Title, n.Body);
            return;
        }
        foreach (var t in list.Where(t => t.Platform == "ios"))
        {
            try { await SendApnsAsync(t, n, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "APNs send failed for {User}", userId); }
        }
    }

    private async Task SendApnsAsync(PushTokenRecord t, NotificationDto n, CancellationToken ct)
    {
        var o = push.Value.Apns;
        var host = o.Sandbox ? "https://api.sandbox.push.apple.com" : "https://api.push.apple.com";
        using var client = http.CreateClient(nameof(ApnsPushSender));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{host}/3/device/{t.Token}") { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        req.Headers.Authorization = new AuthenticationHeaderValue("bearer", Jwt());
        req.Headers.Add("apns-topic", o.BundleId);
        req.Headers.Add("apns-push-type", "alert");
        req.Headers.Add("apns-priority", "10");
        var payload = new Dictionary<string, object?>
        {
            ["aps"] = new Dictionary<string, object?> { ["alert"] = new { title = n.Title, body = n.Body }, ["sound"] = "default", ["badge"] = 1 },
            ["kind"] = n.Kind.ToString(), ["link"] = n.Link, ["matchId"] = n.MatchId,
        };
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var res = await client.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode is HttpStatusCode.Gone || body.Contains("BadDeviceToken", StringComparison.Ordinal) || body.Contains("Unregistered", StringComparison.Ordinal))
        {
            await tokens.RemoveAsync(t.Token, ct);
            log.LogInformation("APNs token removed ({Status})", (int)res.StatusCode);
        }
        else log.LogWarning("APNs {Status}: {Body}", (int)res.StatusCode, body);
    }

    /// <summary>Provider token, refreshed every 50 minutes (APNs accepts a token for 60).</summary>
    private string Jwt()
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        lock (_gate)
        {
            if (_jwt is not null && now - _jwtIssuedAtSec < 50 * 60) return _jwt;
            var o = push.Value.Apns;
            var pem = o.PrivateKeyP8.Contains("BEGIN", StringComparison.Ordinal) ? o.PrivateKeyP8 : File.ReadAllText(o.PrivateKeyP8);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            var header = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = o.KeyId }));
            var claims = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = o.TeamId, iat = now }));
            var signing = Encoding.ASCII.GetBytes(header + "." + claims);
            var sig = ecdsa.SignData(signing, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            _jwt = header + "." + claims + "." + Base64Url.Encode(sig);
            _jwtIssuedAtSec = now;
            return _jwt;
        }
    }

    private static class Base64Url
    {
        public static string Encode(byte[] bytes) => Base64UrlEncoder.Encode(bytes);
    }
}
