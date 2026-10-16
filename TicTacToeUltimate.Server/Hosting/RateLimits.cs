using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.Hosting;

/// <summary>Named rate-limit policies (REST) and the client-address resolver shared with the hub filter.</summary>
public static class RateLimits
{
    /// <summary>Anonymous token mints: POST /api/auth/guest, /api/auth/dev/token (per address).</summary>
    public const string Auth = "auth";
    /// <summary>Everything under /api (per user, else per address).</summary>
    public const string Api = "api";
    /// <summary>Friend requests and other notification-producing social writes (per user).</summary>
    public const string Social = "social";
    /// <summary>Reward nonces and claims (per user).</summary>
    public const string Rewards = "rewards";

    /// <summary>
    /// The caller's address. Production sits behind Cloudflare → Caddy (which only forwards requests carrying the origin secret),
    /// so CF-Connecting-IP / the first X-Forwarded-For hop are trustworthy there; a direct hit falls back to the socket address.
    /// </summary>
    public static string ClientAddress(HttpContext http)
    {
        var cf = http.Request.Headers["CF-Connecting-IP"].ToString();
        if (cf.Length > 0 && IPAddress.TryParse(cf, out var a)) return a.ToString();
        var xff = http.Request.Headers["X-Forwarded-For"].ToString();
        if (xff.Length > 0)
        {
            var first = xff.Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out var b)) return b.ToString();
        }
        return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>User id when authenticated, else the address.</summary>
    public static string UserOrAddress(HttpContext http)
    {
        var sub = http.User?.FindFirst(TttClaims.Sub)?.Value;
        return sub is { Length: > 0 } ? "u:" + sub : "ip:" + ClientAddress(http);
    }

    /// <summary>Registers the limiter. Limits come from Ttt:RateLimits (per minute unless named otherwise).</summary>
    public static IServiceCollection AddTttRateLimiting(this IServiceCollection services, IConfiguration cfg)
    {
        var o = cfg.GetSection("Ttt:RateLimits").Get<RateLimitOptions>() ?? new RateLimitOptions();
        services.AddSingleton(o);
        services.AddRateLimiter(r =>
        {
            r.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            r.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "5";
                await ctx.HttpContext.Response.WriteAsJsonAsync(new { title = ErrorCodes.RateLimited, status = 429, code = ErrorCodes.RateLimited }, ct);
            };
            // Floor under everything (hub negotiation, health, static 404s): per address.
            r.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                RateLimitPartition.GetSlidingWindowLimiter("g:" + ClientAddress(http), _ => Sliding(o.GlobalPerMinute, 60, 6)));
            r.AddPolicy(Auth, http => RateLimitPartition.GetFixedWindowLimiter("a:" + ClientAddress(http), _ => Fixed(o.AuthPerMinute, 60)));
            r.AddPolicy(Api, http => RateLimitPartition.GetSlidingWindowLimiter("p:" + UserOrAddress(http), _ => Sliding(o.ApiPerMinute, 60, 6)));
            r.AddPolicy(Social, http => RateLimitPartition.GetFixedWindowLimiter("s:" + UserOrAddress(http), _ => Fixed(o.SocialPerHour, 3600)));
            r.AddPolicy(Rewards, http => RateLimitPartition.GetFixedWindowLimiter("r:" + UserOrAddress(http), _ => Fixed(o.RewardsPerHour, 3600)));
        });
        return services;
    }

    private static FixedWindowRateLimiterOptions Fixed(int permits, int seconds) => new()
    {
        PermitLimit = Math.Max(1, permits), Window = TimeSpan.FromSeconds(seconds), QueueLimit = 0, AutoReplenishment = true,
    };

    private static SlidingWindowRateLimiterOptions Sliding(int permits, int seconds, int segments) => new()
    {
        PermitLimit = Math.Max(1, permits), Window = TimeSpan.FromSeconds(seconds), SegmentsPerWindow = segments, QueueLimit = 0, AutoReplenishment = true,
    };
}

/// <summary>Ttt:RateLimits. Generous by design: a real client never gets near these; scripts do.</summary>
public sealed class RateLimitOptions
{
    public int GlobalPerMinute { get; set; } = 1200;
    public int AuthPerMinute { get; set; } = 20;
    public int ApiPerMinute { get; set; } = 300;
    public int SocialPerHour { get; set; } = 60;
    public int RewardsPerHour { get; set; } = 60;
    /// <summary>Hub invocations per connection: sustained per second and burst.</summary>
    public int HubPerSecond { get; set; } = 10;
    public int HubBurst { get; set; } = 40;
}

/// <summary>Token bucket per SignalR connection. Fire-and-forget methods (Pong, LeaveMatch) used to let one socket flood the match actors.</summary>
public sealed class HubRateLimitFilter(RateLimitOptions options, TimeProvider time) : IHubFilter
{
    private sealed class Bucket { public double Tokens; public long LastMs; }
    private const string Key = "ttt.ratebucket";

    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext ctx, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var bucket = (Bucket)ctx.Context.Items.GetOrAdd(Key, _ => new Bucket { Tokens = options.HubBurst, LastMs = now });
        lock (bucket)
        {
            bucket.Tokens = Math.Min(options.HubBurst, bucket.Tokens + Math.Max(0, now - bucket.LastMs) * options.HubPerSecond / 1000.0);
            bucket.LastMs = now;
            if (bucket.Tokens < 1) throw new HubException(ErrorCodes.RateLimited);
            bucket.Tokens -= 1;
        }
        return next(ctx);
    }
}

file static class ItemsExtensions
{
    public static object GetOrAdd(this IDictionary<object, object?> items, string key, Func<string, object> make)
    {
        lock (items)
        {
            if (items.TryGetValue(key, out var v) && v is not null) return v;
            var created = make(key);
            items[key] = created;
            return created;
        }
    }
}
