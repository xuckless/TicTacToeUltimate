using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Auth;
using TicTacToeUltimate.Server.Progression;

namespace TicTacToeUltimate.Server.Rewards;

/// <summary>
/// Server-issued single-use rewarded-ad nonces. Neither AdSense (Ad Placement API) nor the current AdMob wiring verifies
/// a view server-side, so the receipt the client returns must at least be something the server minted for THIS user, THIS
/// kind and THIS game, a few minutes ago, and never redeemed before (the ledger keys grants by nonce).
/// Format: <c>n1.{kind}.{expUnixSec}.{rand}.{hmac}</c>; the HMAC covers user id, kind, game id, exp and rand.
/// </summary>
public sealed class RewardNonceService(DevTokenIssuer issuer, TimeProvider time)
{
    /// <summary>Nonce lifetime: long enough for the ad (AdMob caps at 60 s), short enough to be useless to stockpile.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>Mints a nonce for the user.</summary>
    public (string Nonce, long ExpiresAtMs) Issue(Guid userId, RewardKind kind, Guid? gameId)
    {
        var exp = time.GetUtcNow().Add(Ttl);
        var expSec = exp.ToUnixTimeSeconds();
        var rand = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(9));
        var k = kind.ToString().ToLowerInvariant();
        var mac = Mac(userId, k, gameId, expSec, rand);
        return ($"n1.{k}.{expSec.ToString(CultureInfo.InvariantCulture)}.{rand}.{mac}", exp.ToUnixTimeMilliseconds());
    }

    /// <summary>True when <paramref name="nonce"/> was minted for this user/kind/game and has not expired.</summary>
    public bool Verify(Guid userId, RewardKind kind, Guid? gameId, string? nonce)
    {
        if (string.IsNullOrEmpty(nonce) || nonce.Length > 160) return false;
        var parts = nonce.Split('.');
        if (parts.Length != 5 || parts[0] != "n1") return false;
        if (!string.Equals(parts[1], kind.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expSec)) return false;
        if (time.GetUtcNow().ToUnixTimeSeconds() > expSec) return false;
        var expected = Mac(userId, parts[1], gameId, expSec, parts[3]);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[4]));
    }

    private string Mac(Guid userId, string kind, Guid? gameId, long expSec, string rand)
    {
        var data = Encoding.UTF8.GetBytes($"{userId:N}|{kind}|{gameId?.ToString("N") ?? "-"}|{expSec}|{rand}");
        return WebEncoders.Base64UrlEncode(HMACSHA256.HashData(issuer.Key.Key, data).AsSpan(0, 20));
    }
}

/// <summary>Daily caps for ad-granted perks (server-side; the client shows "n/cap today").</summary>
public static class RewardCaps
{
    /// <summary>Ad-earned streak freezes per local day (the 5-win quest can add another).</summary>
    public const int StreakFreezesPerDay = 1;
}

/// <summary>POST /api/rewards/nonce + /api/rewards/claim logic (§6.7 Rewards).</summary>
public sealed class RewardService(RewardNonceService nonces, IRewardLedger ledger, IProgressRepository progress, IUserRepository users,
    IGameRepository games, ProgressService progressService, UserLocks locks, TimeProvider time)
{
    /// <summary>Checks eligibility and mints a nonce; the client shows the ad only on success, so "capped" never follows an ad.</summary>
    public async Task<RewardNonceDto> IssueNonceAsync(Guid userId, RewardNonceRequestDto req, CancellationToken ct)
    {
        if (!Enum.IsDefined(req.Kind)) return new(null, 0, ErrorCodes.RewardInvalid);
        if (await EligibilityErrorAsync(userId, req.Kind, req.GameId, ct) is { } error) return new(null, 0, error);
        var (nonce, exp) = nonces.Issue(userId, req.Kind, req.GameId);
        return new(nonce, exp, null);
    }

    /// <summary>Claims a reward for a user. The receipt is the nonce from <see cref="IssueNonceAsync"/>.</summary>
    public async Task<RewardClaimResultDto> ClaimAsync(Guid userId, RewardClaimDto claim, CancellationToken ct)
    {
        if (!nonces.Verify(userId, claim.Kind, claim.GameId, claim.Receipt)) return new(false, ErrorCodes.RewardInvalid, null);
        var now = time.GetUtcNow();
        using (await locks.AcquireAsync(userId, ct))
        {
            if (await EligibilityErrorAsync(userId, claim.Kind, claim.GameId, ct) is { } error)
                return new(false, error == ErrorCodes.RewardInvalid ? error : ErrorCodes.RewardCapped, null);
            var grant = new RewardGrantRecord(claim.Receipt, userId, claim.Kind, claim.GameId, now);
            if (!await ledger.TryGrantAsync(grant, ct)) return new(false, ErrorCodes.RewardInvalid, null);   // nonce already redeemed
            switch (claim.Kind)
            {
                case RewardKind.StreakFreeze:
                {
                    var s = await progress.GetStreakAsync(userId, ct);
                    await progress.PutStreakAsync(s with { Freezes = s.Freezes + 1 }, ct);
                    break;
                }
                case RewardKind.ExtraQuest:
                {
                    var day = LocalDays.Day(now, (await users.GetAsync(userId, ct))?.TimeZone);
                    var q = await progress.GetQuestDayAsync(userId, day, ct);
                    await progress.PutQuestDayAsync(q with { BonusTarget = q.Wins + 2, BonusClaimed = false }, ct);
                    break;
                }
                case RewardKind.Analysis:
                    break;   // the grant row itself unlocks GET /api/games/{id}/analysis
            }
        }
        return new(true, null, await progressService.GetAsync(userId, ct));
    }

    /// <summary>Null when the user may claim <paramref name="kind"/> now; otherwise reward_capped / reward_invalid.</summary>
    private async Task<string?> EligibilityErrorAsync(Guid userId, RewardKind kind, Guid? gameId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        switch (kind)
        {
            case RewardKind.StreakFreeze:
            {
                if (gameId is not null) return ErrorCodes.RewardInvalid;
                var s = await progress.GetStreakAsync(userId, ct);
                if (s.Freezes >= StreakRules.MaxFreezes) return ErrorCodes.RewardCapped;
                var tz = (await users.GetAsync(userId, ct))?.TimeZone;
                var dayStart = LocalDays.NextMidnight(now, tz).AddDays(-1);
                if (await ledger.CountSinceAsync(userId, kind, dayStart, ct) >= RewardCaps.StreakFreezesPerDay) return ErrorCodes.RewardCapped;
                return null;
            }
            case RewardKind.ExtraQuest:
            {
                if (gameId is not null) return ErrorCodes.RewardInvalid;
                var day = LocalDays.Day(now, (await users.GetAsync(userId, ct))?.TimeZone);
                var q = await progress.GetQuestDayAsync(userId, day, ct);
                return q.BonusTarget is not null ? ErrorCodes.RewardCapped : null;
            }
            case RewardKind.Analysis:
            {
                if (gameId is not { } g) return ErrorCodes.RewardInvalid;
                var game = await games.GetAsync(g, ct);
                if (game is null || (game.Setup.SeatA.UserId != userId && game.Setup.SeatB.UserId != userId)) return ErrorCodes.RewardInvalid;
                return await ledger.HasGrantAsync(userId, RewardKind.Analysis, g, ct) ? ErrorCodes.RewardCapped : null;
            }
            default:
                return ErrorCodes.RewardInvalid;
        }
    }
}
