using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Bots;

/// <summary>Constants for the bot key authentication scheme.</summary>
public static class BotKeyDefaults
{
    /// <summary>Authentication scheme name.</summary>
    public const string Scheme = "BotKey";
    /// <summary>Key prefix: keys look like <c>tttb_{keyId}_{secret}</c>.</summary>
    public const string Prefix = "tttb_";
}

/// <summary>Parsing, hashing and id helpers for bot API keys.</summary>
public static class BotKeyFormat
{
    /// <summary>Splits <c>tttb_{keyId}_{secret}</c>. The key id never contains '_'; the secret may.</summary>
    public static bool TryParse(string? key, out string keyId, out string secret)
    {
        keyId = ""; secret = "";
        if (string.IsNullOrEmpty(key) || !key.StartsWith(BotKeyDefaults.Prefix, StringComparison.Ordinal)) return false;
        var rest = key.AsSpan(BotKeyDefaults.Prefix.Length);
        var sep = rest.IndexOf('_');
        if (sep <= 0 || sep == rest.Length - 1) return false;
        keyId = rest[..sep].ToString();
        secret = rest[(sep + 1)..].ToString();
        return true;
    }

    /// <summary>SHA-256 of the UTF-8 secret.</summary>
    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    /// <summary>Constant-time comparison of a secret with a stored hash.</summary>
    public static bool Verify(string secret, byte[] hash) => CryptographicOperations.FixedTimeEquals(Hash(secret), hash);

    /// <summary>Random lowercase alphanumeric token.</summary>
    public static string RandomToken(int length) =>
        RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", length);
}

/// <summary>RFC 4122 name-based (SHA-1, version 5) UUIDs.</summary>
public static class GuidV5
{
    /// <summary>Namespace for deterministic dev-bot user ids.</summary>
    public static readonly Guid BotNamespace = new("4b2d8c3e-7f1a-4e5b-9c6d-0a1b2c3d4e5f");

    /// <summary>Creates a v5 UUID from a namespace and a name.</summary>
    public static Guid Create(Guid ns, string name)
    {
        Span<byte> nsBytes = stackalloc byte[16];
        ns.TryWriteBytes(nsBytes, bigEndian: true, out _);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var data = new byte[16 + nameBytes.Length];
        nsBytes.CopyTo(data);
        nameBytes.CopyTo(data, 16);
        var hash = SHA1.HashData(data);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}

/// <summary>Resolves bot keys from <c>Bots:DevKeys</c> and, when registered, <see cref="IBotKeyRepository"/>.</summary>
public sealed class BotKeyResolver
{
    private readonly Dictionary<string, (byte[] Hash, TttUser User)> _devKeys = new(StringComparer.Ordinal);
    private readonly IServiceProvider _services;

    /// <summary>Creates the resolver and indexes the dev keys.</summary>
    public BotKeyResolver(IOptions<TttOptions> options, IServiceProvider services)
    {
        _services = services;
        foreach (var dev in options.Value.Bots.DevKeys)
        {
            if (!BotKeyFormat.TryParse(dev.Key, out var keyId, out var secret)) continue;
            _devKeys[keyId] = (BotKeyFormat.Hash(secret), new TttUser(DevBotUserId(keyId), AccountKind.Bot, dev.Name));
        }
    }

    /// <summary>The configured dev bots.</summary>
    public IEnumerable<TttUser> DevBots => _devKeys.Values.Select(v => v.User);

    /// <summary>Deterministic user id of a dev bot.</summary>
    public static Guid DevBotUserId(string keyId) => GuidV5.Create(GuidV5.BotNamespace, "dev-bot:" + keyId);

    /// <summary>Returns the bot user for a full key, or null if the key is unknown, revoked or wrong.</summary>
    public async Task<TttUser?> ResolveAsync(string? key, CancellationToken ct)
    {
        if (!BotKeyFormat.TryParse(key, out var keyId, out var secret)) return null;
        if (_devKeys.TryGetValue(keyId, out var dev))
            return BotKeyFormat.Verify(secret, dev.Hash) ? dev.User : null;
        var repo = _services.GetService<IBotKeyRepository>();
        if (repo is null) return null;
        var record = await repo.FindAsync(keyId, ct).ConfigureAwait(false);
        if (record is null || record.RevokedAt is not null || !BotKeyFormat.Verify(secret, record.SecretHash)) return null;
        return new TttUser(record.BotUserId, AccountKind.Bot, record.BotName);
    }
}

/// <summary>Authenticates <c>Authorization: Bearer tttb_…</c> (or <c>?key=</c> when <c>Bots:AllowQueryKey</c>).</summary>
public sealed class BotKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    BotKeyResolver resolver, IOptions<TttOptions> ttt)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? key = null;
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header["Bearer ".Length..].Trim();
            if (token.StartsWith(BotKeyDefaults.Prefix, StringComparison.Ordinal)) key = token;
        }
        if (key is null && ttt.Value.Bots.AllowQueryKey && Request.Query.TryGetValue("key", out var q)) key = q.ToString();
        if (key is null) return AuthenticateResult.NoResult();

        var user = await resolver.ResolveAsync(key, Context.RequestAborted).ConfigureAwait(false);
        if (user is null) return AuthenticateResult.Fail("bad_key");
        var identity = new ClaimsIdentity(
        [
            new Claim(TttClaims.Sub, user.UserId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new Claim(TttClaims.Kind, "bot"),
            new Claim(TttClaims.Name, user.DisplayName),
        ], Scheme.Name, TttClaims.Name, null);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

/// <summary>Creates the Bot user rows for dev keys at startup when a user repository is registered.</summary>
internal sealed class DevBotUserSeeder(BotKeyResolver resolver, IServiceProvider services, TimeProvider time, ILogger<DevBotUserSeeder> log)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var users = services.GetService<IUserRepository>();
        if (users is null) return;
        var now = time.GetUtcNow();
        foreach (var bot in resolver.DevBots)
        {
            try
            {
                if (await users.GetAsync(bot.UserId, cancellationToken).ConfigureAwait(false) is not null) continue;
                await users.CreateAsync(new UserRecord(bot.UserId, AccountKind.Bot, null, bot.DisplayName, "bot",
                    null, "UTC", 0, null, null, now, now), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not seed dev bot {Bot}", bot.DisplayName);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
