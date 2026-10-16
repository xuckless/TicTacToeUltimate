using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Ratings;
using TicTacToeUltimate.Server.Social;

namespace TicTacToeUltimate.Server.Auth;

/// <summary>Username normalization and validation (§6.7 Usernames).</summary>
public sealed partial class UsernameRules(ProfanityFilter profanity)
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "admin", "administrator", "mod", "moderator", "support", "staff", "system", "root", "ttt", "tttu", "guest", "bot", "null",
        "undefined", "anonymous", "official",
    };

    [GeneratedRegex("^[a-z0-9_]{3,16}$")]
    private static partial Regex Pattern();

    /// <summary>Lowercase + trim.</summary>
    public static string Normalize(string? s) => (s ?? "").Trim().ToLowerInvariant();

    /// <summary>True when the normalized name is allowed.</summary>
    public bool IsValid(string normalized) => Pattern().IsMatch(normalized) && !Reserved.Contains(normalized) && !profanity.ContainsBlocked(normalized);
}

/// <summary>Result with an error code (ErrorCodes) on failure.</summary>
public readonly record struct OpResult<T>(T? Value, string? Error, int Status = 200)
{
    /// <summary>True on success.</summary>
    public bool Ok => Error is null;
    public static OpResult<T> Fail(string code, int status) => new(default, code, status);
}

/// <summary>Account lifecycle: dev users, guest login, me, username, profile.</summary>
public sealed class AccountService(IUserRepository users, RatingService ratings, UsernameRules rules, DevTokenIssuer issuer, TimeProvider time,
    TicTacToeUltimate.Server.Persistence.IGuestMergeStore merge, TicTacToeUltimate.Server.Persistence.IAccountDeletionStore deletion,
    SupabaseAdminClient supabaseAdmin, IServiceProvider services, ILogger<AccountService> log)
{
    /// <summary>
    /// DELETE /api/me: resigns live games, anonymises the player and purges personal data (see IAccountDeletionStore), then deletes
    /// the Supabase auth user when the admin key is configured. The caller must sign out; the id can never authenticate again.
    /// </summary>
    public async Task<OpResult<bool>> DeleteAccountAsync(Guid userId, CancellationToken ct)
    {
        var u = await users.GetAsync(userId, ct);
        if (u is null || u.Kind == AccountKind.Bot) return OpResult<bool>.Fail(ErrorCodes.Validation, 400);
        if (services.GetService(typeof(TicTacToeUltimate.Server.Abstractions.IMatchGateway)) is TicTacToeUltimate.Server.Abstractions.IMatchGateway gateway)
            foreach (var matchId in gateway.ActiveMatchesFor(userId))
            {
                try { await gateway.ResignAsync(matchId, userId, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Resign on delete failed for {Match}", matchId); }
            }
        await deletion.DeleteAsync(userId, ct);
        if (u.Kind == AccountKind.Account) await supabaseAdmin.DeleteUserAsync(userId, ct);
        log.LogInformation("Account {User} deleted", userId);
        return new(true, null);
    }

    /// <summary>Builds MeDto.</summary>
    public async Task<MeDto?> GetMeAsync(Guid userId, CancellationToken ct)
    {
        var u = await users.GetAsync(userId, ct);
        if (u is null) return null;
        RatingDto[] visible = u.Kind == AccountKind.Guest ? [] : await ratings.VisibleAsync(userId, time.GetUtcNow(), ct);
        return new MeDto(u.Id, u.Kind, u.DisplayName, u.Username, u.Kind == AccountKind.Account && u.Username is null, u.AvatarId, u.Country,
            u.TimeZone, u.RenamesLeft, visible);
    }

    /// <summary>Dev user picker (non-guest users).</summary>
    public async Task<DevUserDto[]> DevUsersAsync(CancellationToken ct)
    {
        var list = await users.ListAsync(200, ct);
        var result = new List<DevUserDto>();
        foreach (var u in list.Where(u => u.Kind == AccountKind.Account))
        {
            var me = await GetMeAsync(u.Id, ct);
            var ranked = me?.Ratings.FirstOrDefault(r => r.Pool == RatingPools.Headline);
            result.Add(new DevUserDto(u.Id, u.DisplayName, u.Kind, ranked?.Tier, ranked?.Rating));
        }
        return result.ToArray();
    }

    /// <summary>POST /api/auth/dev/token.</summary>
    public async Task<OpResult<TokenResponseDto>> DevTokenAsync(DevTokenRequestDto req, CancellationToken ct)
    {
        if ((req.UserId is null) == string.IsNullOrWhiteSpace(req.NewUsername)) return OpResult<TokenResponseDto>.Fail(ErrorCodes.Validation, 400);
        UserRecord? u;
        if (req.UserId is { } id)
        {
            u = await users.GetAsync(id, ct);
            if (u is null) return OpResult<TokenResponseDto>.Fail(ErrorCodes.Validation, 404);
        }
        else
        {
            var name = UsernameRules.Normalize(req.NewUsername);
            if (!rules.IsValid(name)) return OpResult<TokenResponseDto>.Fail(ErrorCodes.UsernameInvalid, 400);
            var now = time.GetUtcNow();
            u = new UserRecord(Guid.NewGuid(), AccountKind.Account, name, name, "a1", null, "UTC", 1, now, null, now, now);
            if (!await users.CreateAsync(u, ct)) return OpResult<TokenResponseDto>.Fail(ErrorCodes.UsernameTaken, 409);
        }
        return new(await TokenAsync(u, ct), null);
    }

    /// <summary>POST /api/auth/guest: create or load the guest for a device id.</summary>
    public async Task<OpResult<TokenResponseDto>> GuestTokenAsync(GuestTokenRequestDto req, CancellationToken ct)
    {
        var device = (req.DeviceId ?? "").Trim();
        if (device.Length is 0 or > 64) return OpResult<TokenResponseDto>.Fail(ErrorCodes.Validation, 400);
        var u = await users.GetByDeviceIdAsync(device, ct);
        if (u is null)
        {
            var now = time.GetUtcNow();
            u = new UserRecord(Guid.NewGuid(), AccountKind.Guest, null, GuestName(), "a1", null, "UTC", 1, null, device, now, now);
            if (!await users.CreateAsync(u, ct)) u = await users.GetByDeviceIdAsync(device, ct);
            if (u is null) return OpResult<TokenResponseDto>.Fail(ErrorCodes.Internal, 500);
        }
        return new(await TokenAsync(u, ct), null);
    }

    /// <summary>POST /api/auth/claim-guest: merge the guest behind <paramref name="guestToken"/> into <paramref name="accountId"/>.</summary>
    public async Task<OpResult<ClaimGuestResultDto>> ClaimGuestAsync(Guid accountId, string? guestToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(guestToken)) return OpResult<ClaimGuestResultDto>.Fail(ErrorCodes.Validation, 400);
        var check = await new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler()
            .ValidateTokenAsync(guestToken, issuer.ValidationParameters() is var p ? WithoutLifetime(p) : null);
        if (!check.IsValid || !Guid.TryParse((check.Claims.TryGetValue(TicTacToeUltimate.Server.Abstractions.TttClaims.Sub, out var sub) ? sub?.ToString() : null), out var guestId) || guestId == accountId)
            return OpResult<ClaimGuestResultDto>.Fail(ErrorCodes.Validation, 400);
        var guest = await users.GetAsync(guestId, ct);
        if (guest is null || guest.Kind != AccountKind.Guest) return new(new ClaimGuestResultDto(0), null);
        var moved = await merge.MergeAsync(guestId, accountId, ct);
        return new(new ClaimGuestResultDto(moved), null);
    }

    // A guest token found in storage may be past its expiry; the signature is what proves ownership.
    private static Microsoft.IdentityModel.Tokens.TokenValidationParameters WithoutLifetime(Microsoft.IdentityModel.Tokens.TokenValidationParameters p)
    {
        var c = p.Clone();
        c.ValidateLifetime = false;
        return c;
    }

    /// <summary>"Guest#NNNN".</summary>
    public static string GuestName() => $"Guest#{RandomNumberGenerator.GetInt32(1000, 10000)}";

    private async Task<TokenResponseDto> TokenAsync(UserRecord u, CancellationToken ct)
    {
        var (token, exp) = issuer.Issue(u.Id, u.Kind, u.DisplayName);
        return new TokenResponseDto(token, exp, (await GetMeAsync(u.Id, ct))!);
    }

    /// <summary>PUT /api/me/username (accounts only; guests upgrade through Supabase sign-in + claim-guest).</summary>
    public async Task<OpResult<MeDto>> SetUsernameAsync(Guid userId, SetUsernameDto req, CancellationToken ct)
    {
        var u = await users.GetAsync(userId, ct);
        if (u is null || u.Kind == AccountKind.Bot) return OpResult<MeDto>.Fail(ErrorCodes.Validation, 400);
        // A guest token proves only possession of a device id. Accounts come from a verified Supabase sign-in (then claim-guest);
        // letting a guest pick a username used to mint unlimited "accounts" with ranked/social/bot-key access.
        if (u.Kind == AccountKind.Guest) return OpResult<MeDto>.Fail(ErrorCodes.GuestNotAllowed, 403);
        var name = UsernameRules.Normalize(req.Username);
        if (!rules.IsValid(name)) return OpResult<MeDto>.Fail(ErrorCodes.UsernameInvalid, 400);
        if (u.Username is null && u.TosAcceptedAt is null && !req.AcceptTos) return OpResult<MeDto>.Fail(ErrorCodes.TosRequired, 400);
        var result = await users.SetUsernameAsync(userId, name, req.AcceptTos ? time.GetUtcNow() : null, ct);
        return result switch
        {
            UsernameResult.Ok => new((await GetMeAsync(userId, ct))!, null),
            UsernameResult.Taken => OpResult<MeDto>.Fail(ErrorCodes.UsernameTaken, 409),
            UsernameResult.NoRenamesLeft => OpResult<MeDto>.Fail(ErrorCodes.NoRenamesLeft, 409),
            _ => OpResult<MeDto>.Fail(ErrorCodes.UsernameInvalid, 400),
        };
    }

    /// <summary>PATCH /api/me.</summary>
    public async Task<OpResult<MeDto>> UpdateProfileAsync(Guid userId, UpdateProfileDto req, CancellationToken ct)
    {
        if (req.AvatarId is { } a && (a.Length is 0 or > 32)) return OpResult<MeDto>.Fail(ErrorCodes.Validation, 400);
        string? country = null;
        if (req.Country is { } c)
        {
            if (c.Length != 2 || !c.All(char.IsAsciiLetter)) return OpResult<MeDto>.Fail(ErrorCodes.Validation, 400);
            country = c.ToUpperInvariant();
        }
        if (req.TimeZone is { } tz)
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(tz); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return OpResult<MeDto>.Fail(ErrorCodes.Validation, 400); }
        }
        await users.UpdateProfileAsync(userId, new ProfilePatch(req.AvatarId, country, req.TimeZone), ct);
        var me = await GetMeAsync(userId, ct);
        return me is null ? OpResult<MeDto>.Fail(ErrorCodes.Validation, 404) : new(me, null);
    }
}
