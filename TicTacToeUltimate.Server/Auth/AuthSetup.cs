using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Auth;

/// <summary>Scheme names.</summary>
public static class TttSchemes
{
    public const string Default = "Ttt", Dev = "Dev", Supabase = "Supabase", BotKey = "BotKey";
}

/// <summary>Registers authentication (Dev, Supabase, Ttt policy scheme), policies and claims transformation (§6.7 Auth).</summary>
public static class AuthSetup
{
    /// <summary>Adds auth services. Throws when Dev auth is enabled in Production.</summary>
    public static IServiceCollection AddTttAuth(this IServiceCollection services, IConfiguration cfg, IHostEnvironment env)
    {
        var dev = cfg.GetSection("Ttt:Auth:Dev").Get<DevAuthOptions>() ?? new DevAuthOptions();
        var supa = cfg.GetSection("Ttt:Auth:Supabase").Get<SupabaseAuthOptions>() ?? new SupabaseAuthOptions();
        if (dev.Enabled && env.IsProduction())
            throw new InvalidOperationException("Ttt:Auth:Dev:Enabled must be false in Production.");

        services.AddSingleton<DevTokenIssuer>();
        services.AddScoped<IClaimsTransformation, TttClaimsTransformation>();

        var auth = services.AddAuthentication(TttSchemes.Default);
        auth.AddPolicyScheme(TttSchemes.Default, "Ttt", o =>
        {
            o.ForwardDefaultSelector = ctx =>
            {
                var token = ReadToken(ctx.Request);
                if (token is not null && supa.Enabled)
                {
                    try
                    {
                        if (new JsonWebToken(token).Issuer != DevTokenIssuer.Issuer) return TttSchemes.Supabase;
                    }
                    catch (Exception) { /* malformed: let the Dev handler reject it */ }
                }
                return TttSchemes.Dev;
            };
        });
        auth.AddJwtBearer(TttSchemes.Dev, _ => { });
        services.AddOptions<JwtBearerOptions>(TttSchemes.Dev).Configure<DevTokenIssuer>((o, issuer) =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = issuer.ValidationParameters();
            o.Events = QueryTokenEvents();
        });
        if (supa.Enabled)
        {
            auth.AddJwtBearer(TttSchemes.Supabase, o =>
            {
                var authority = supa.Url.TrimEnd('/') + "/auth/v1";
                o.Authority = authority;
                o.MetadataAddress = authority + "/.well-known/openid-configuration";
                o.RequireHttpsMetadata = authority.StartsWith("https", StringComparison.OrdinalIgnoreCase);
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = authority, ValidAudience = string.IsNullOrEmpty(supa.Audience) ? "authenticated" : supa.Audience,
                    NameClaimType = TttClaims.Name,
                    IssuerSigningKey = string.IsNullOrEmpty(supa.JwtSecret) ? null : new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(supa.JwtSecret)),
                };
                o.Events = QueryTokenEvents();
            });
        }

        services.AddAuthorization(o =>
        {
            o.AddPolicy(TttPolicies.Account, p => p.RequireAuthenticatedUser().RequireAssertion(c => HasKind(c.User, AccountKind.Account)));
            o.AddPolicy(TttPolicies.Bot, p => p.RequireAuthenticatedUser().RequireAssertion(c => HasKind(c.User, AccountKind.Bot)));
        });
        return services;
    }

    private static bool HasKind(ClaimsPrincipal user, AccountKind kind)
        => string.Equals(user.FindFirst(TttClaims.Kind)?.Value, AuthKinds.ToClaim(kind), StringComparison.OrdinalIgnoreCase);

    private static string? ReadToken(HttpRequest req)
    {
        var h = req.Headers.Authorization.ToString();
        if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return h["Bearer ".Length..].Trim();
        if (req.Path.StartsWithSegments("/hubs") && req.Query.TryGetValue("access_token", out var q)) return q.ToString();
        return null;
    }

    private static JwtBearerEvents QueryTokenEvents() => new()
    {
        OnMessageReceived = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.TryGetValue("access_token", out var t) && !string.IsNullOrEmpty(t))
                ctx.Token = t;
            return Task.CompletedTask;
        },
    };
}

/// <summary>
/// Refreshes ttt_kind/name from the user store on every authenticated request (so a guest upgrade applies to old tokens),
/// and creates the player row for first-time Supabase users (anonymous → Guest#NNNN, else email local part).
/// </summary>
public sealed class TttClaimsTransformation(IUserRepository users, TimeProvider time) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity) return principal;
        var sub = principal.FindFirst(TttClaims.Sub)?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var id)) return principal;
        var kindClaim = principal.FindFirst(TttClaims.Kind)?.Value;
        if (string.Equals(kindClaim, "bot", StringComparison.OrdinalIgnoreCase)) return principal;   // WP4 bot principals are authoritative

        var u = await users.GetAsync(id, CancellationToken.None);
        if (u is null && kindClaim is null)
        {
            var anon = string.Equals(principal.FindFirst("is_anonymous")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            var email = principal.FindFirst("email")?.Value;
            var now = time.GetUtcNow();
            var name = anon ? AccountService.GuestName() : DisplayFromEmail(email);
            await users.CreateAsync(new UserRecord(id, anon ? AccountKind.Guest : AccountKind.Account, null, name, "a1", null, "UTC", 1, null, null, now, now),
                CancellationToken.None);
            u = await users.GetAsync(id, CancellationToken.None);
        }
        if (u is null) return principal;
        if (u.Kind == AccountKind.Deleted) return new ClaimsPrincipal(new ClaimsIdentity());   // deleted accounts never authenticate again

        var clone = principal.Clone();
        var ci = (ClaimsIdentity)clone.Identity!;
        foreach (var c in ci.FindAll(TttClaims.Kind).ToList()) ci.RemoveClaim(c);
        foreach (var c in ci.FindAll(TttClaims.Name).ToList()) ci.RemoveClaim(c);
        ci.AddClaim(new Claim(TttClaims.Kind, AuthKinds.ToClaim(u.Kind)));
        ci.AddClaim(new Claim(TttClaims.Name, u.DisplayName));
        return clone;
    }

    private static string DisplayFromEmail(string? email)
    {
        var local = (email ?? "").Split('@')[0];
        var clean = new string(local.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.').Take(24).ToArray());
        return clean.Length == 0 ? "Player" : clean;
    }
}

/// <summary>Request helpers.</summary>
public static class HttpUserExtensions
{
    /// <summary>Current TttUser.</summary>
    public static TttUser Me(this HttpContext ctx) => ctx.User.ToTttUser();
}

/// <summary>Exposes resolved options to static helpers.</summary>
internal static class OptionsAccess
{
    internal static TttOptions Get(IServiceProvider sp) => sp.GetRequiredService<IOptions<TttOptions>>().Value;
}
