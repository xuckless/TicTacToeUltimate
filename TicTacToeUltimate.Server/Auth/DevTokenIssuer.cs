using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Auth;

/// <summary>Issues HS256 dev/guest tokens (issuer ttt-dev, audience ttt).</summary>
public sealed class DevTokenIssuer
{
    /// <summary>Dev issuer.</summary>
    public const string Issuer = "ttt-dev";
    /// <summary>Dev audience.</summary>
    public const string Audience = "ttt";

    private readonly TimeProvider _time;
    private readonly int _days;

    /// <summary>Signing key (configured, or random per process).</summary>
    public SymmetricSecurityKey Key { get; }

    public DevTokenIssuer(IOptions<TttOptions> options, TimeProvider time, ILogger<DevTokenIssuer> logger)
    {
        _time = time;
        var dev = options.Value.Auth.Dev;
        _days = Math.Max(1, dev.TokenDays);
        byte[] bytes;
        if (string.IsNullOrEmpty(dev.SigningKey))
        {
            bytes = RandomNumberGenerator.GetBytes(32);
            if (dev.Enabled) logger.LogWarning("Ttt:Auth:Dev:SigningKey is empty; using a random key. Tokens are invalid after restart (clients re-authenticate on 401).");
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(dev.SigningKey);
            if (bytes.Length < 32) bytes = SHA256.HashData(bytes);
        }
        Key = new SymmetricSecurityKey(bytes);
    }

    /// <summary>Validation parameters for the "Dev" scheme.</summary>
    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = Issuer, ValidAudience = Audience, IssuerSigningKey = Key, ValidateIssuerSigningKey = true,
        NameClaimType = TttClaims.Name, ClockSkew = TimeSpan.FromMinutes(1),
    };

    /// <summary>Creates a token; returns (token, expiresAtMs).</summary>
    public (string Token, long ExpiresAtMs) Issue(Guid userId, AccountKind kind, string displayName)
    {
        var now = _time.GetUtcNow();
        var exp = now.AddDays(_days);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience, IssuedAt = now.UtcDateTime, NotBefore = now.UtcDateTime.AddMinutes(-1), Expires = exp.UtcDateTime,
            Subject = new ClaimsIdentity([
                new Claim(TttClaims.Sub, userId.ToString()),
                new Claim(TttClaims.Kind, AuthKinds.ToClaim(kind)),
                new Claim(TttClaims.Name, displayName),
            ]),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });
        return (token, exp.ToUnixTimeMilliseconds());
    }
}

/// <summary>ttt_kind claim values.</summary>
public static class AuthKinds
{
    /// <summary>"guest" | "account" | "bot".</summary>
    public static string ToClaim(AccountKind k) => k.ToString().ToLowerInvariant();
}
