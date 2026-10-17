using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TicTacToeUltimate.Server.Admin;

/// <summary>Ttt:Admin. Emails is the allowlist; Access holds the Cloudflare Access team domain and application AUD tag.</summary>
public sealed class AdminOptions
{
    public string[] Emails { get; set; } = [];
    public AdminAccessOptions Access { get; set; } = new();
    /// <summary>Development only: skip Cloudflare Access (the admin app sends X-Admin-Email).</summary>
    public bool DevBypass { get; set; }
    public string AppStoreId { get; set; } = "";
    public string PlayPackage { get; set; } = "com.wedirectsoftwares.tictactoeultimate";
    public string ShortLinkBase { get; set; } = "https://alidirect.dev/go/";
    /// <summary>Base URL of the ttt-ops sidecar (Phase 7b), e.g. http://ops:8090. Empty = no host metrics.</summary>
    public string OpsUrl { get; set; } = "";
    public string OpsToken { get; set; } = "";
}

public sealed class AdminAccessOptions
{
    /// <summary>e.g. "yourteam.cloudflareaccess.com".</summary>
    public string TeamDomain { get; set; } = "";
    /// <summary>Application Audience (AUD) tag of the admin.alidirect.dev Access application.</summary>
    public string Aud { get; set; } = "";
}

/// <summary>
/// Two locks on /api/admin (owner decision 2026-10-16): Cloudflare Access must have signed the request
/// (<c>Cf-Access-Jwt-Assertion</c>, verified against the team's public keys and the app's AUD), and the email inside must be
/// on <see cref="AdminOptions.Emails"/>. Returns the admin's email, or null.
/// </summary>
public sealed class AdminGate(IOptions<AdminOptions> options, IHttpClientFactory http, IHostEnvironment env, TimeProvider time, ILogger<AdminGate> log)
{
    private readonly SemaphoreSlim _keysLock = new(1, 1);
    private IList<SecurityKey>? _keys;
    private DateTimeOffset _keysAt;
    private readonly JsonWebTokenHandler _handler = new();

    private AdminOptions O => options.Value;

    /// <summary>True when the dev bypass is active (Development environment and Ttt:Admin:DevBypass).</summary>
    public bool DevBypass => env.IsDevelopment() && O.DevBypass;

    public async Task<string?> AuthorizeAsync(HttpContext ctx)
    {
        if (DevBypass) return ctx.Request.Headers["X-Admin-Email"].FirstOrDefault() is { Length: > 0 } e ? e : O.Emails.FirstOrDefault() ?? "dev@localhost";
        if (O.Access.TeamDomain.Length == 0 || O.Access.Aud.Length == 0 || O.Emails.Length == 0) return null;
        var jwt = ctx.Request.Headers["Cf-Access-Jwt-Assertion"].FirstOrDefault();
        if (string.IsNullOrEmpty(jwt)) return null;
        var keys = await KeysAsync(ctx.RequestAborted);
        if (keys.Count == 0) return null;
        var result = await _handler.ValidateTokenAsync(jwt, new TokenValidationParameters
        {
            ValidIssuer = $"https://{O.Access.TeamDomain}",
            ValidAudience = O.Access.Aud,
            IssuerSigningKeys = keys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        });
        if (!result.IsValid) { log.LogWarning("Admin request with an invalid Access token: {Error}", result.Exception?.Message); return null; }
        var email = result.Claims.TryGetValue("email", out var v) ? v as string : null;
        return email is not null && O.Emails.Contains(email, StringComparer.OrdinalIgnoreCase) ? email : null;
    }

    /// <summary>Cloudflare Access signing keys, cached for an hour.</summary>
    private async Task<IList<SecurityKey>> KeysAsync(CancellationToken ct)
    {
        if (_keys is not null && time.GetUtcNow() - _keysAt < TimeSpan.FromHours(1)) return _keys;
        await _keysLock.WaitAsync(ct);
        try
        {
            if (_keys is not null && time.GetUtcNow() - _keysAt < TimeSpan.FromHours(1)) return _keys;
            using var client = http.CreateClient(nameof(AdminGate));
            var json = await client.GetStringAsync($"https://{O.Access.TeamDomain}/cdn-cgi/access/certs", ct);
            _keys = new JsonWebKeySet(json).GetSigningKeys();
            _keysAt = time.GetUtcNow();
            return _keys;
        }
        catch (HttpRequestException ex) { log.LogWarning(ex, "Cloudflare Access keys unavailable"); return _keys ?? []; }
        finally { _keysLock.Release(); }
    }
}
