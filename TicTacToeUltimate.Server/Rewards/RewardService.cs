using System.Text.RegularExpressions;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Progression;

namespace TicTacToeUltimate.Server.Rewards;

/// <summary>Verifies rewarded-ad receipts.</summary>
public interface IRewardVerifier
{
    /// <summary>True when the receipt is genuine for the kind.</summary>
    bool Verify(RewardKind kind, string receipt);
}

/// <summary>Stub provider ("Ttt:Ads:Provider = Stub"): accepts "stub:{word}:{uuid}".</summary>
public sealed partial class StubRewardVerifier : IRewardVerifier
{
    [GeneratedRegex("^stub:[a-z]+:[0-9a-f-]{36}$")]
    private static partial Regex Pattern();

    public bool Verify(RewardKind kind, string receipt) => receipt is not null && Pattern().IsMatch(receipt);
}

/// <summary>POST /api/rewards/claim logic (§6.7 Rewards).</summary>
public sealed class RewardService(IRewardVerifier verifier, IRewardLedger ledger, IProgressRepository progress, IUserRepository users,
    IGameRepository games, ProgressService progressService, UserLocks locks, TimeProvider time)
{
    /// <summary>Claims a reward for a user.</summary>
    public async Task<RewardClaimResultDto> ClaimAsync(Guid userId, RewardClaimDto claim, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(claim.Receipt) || !verifier.Verify(claim.Kind, claim.Receipt)) return new(false, ErrorCodes.RewardInvalid, null);
        var now = time.GetUtcNow();
        using (await locks.AcquireAsync(userId, ct))
        {
            var grant = new RewardGrantRecord(claim.Receipt, userId, claim.Kind, null, now);
            switch (claim.Kind)
            {
                case RewardKind.StreakFreeze:
                {
                    var s = await progress.GetStreakAsync(userId, ct);
                    if (s.Freezes >= StreakRules.MaxFreezes) return new(false, ErrorCodes.RewardCapped, null);
                    if (!await ledger.TryGrantAsync(grant, ct)) return new(false, ErrorCodes.RewardInvalid, null);
                    await progress.PutStreakAsync(s with { Freezes = s.Freezes + 1 }, ct);
                    break;
                }
                case RewardKind.ExtraQuest:
                {
                    var day = LocalDays.Day(now, (await users.GetAsync(userId, ct))?.TimeZone);
                    var q = await progress.GetQuestDayAsync(userId, day, ct);
                    if (q.BonusTarget is not null) return new(false, ErrorCodes.RewardCapped, null);
                    if (!await ledger.TryGrantAsync(grant, ct)) return new(false, ErrorCodes.RewardInvalid, null);
                    await progress.PutQuestDayAsync(q with { BonusTarget = q.Wins + 2, BonusClaimed = false }, ct);
                    break;
                }
                case RewardKind.Analysis:
                {
                    if (claim.GameId is not { } gameId) return new(false, ErrorCodes.RewardInvalid, null);
                    var g = await games.GetAsync(gameId, ct);
                    if (g is null || (g.Setup.SeatA.UserId != userId && g.Setup.SeatB.UserId != userId)) return new(false, ErrorCodes.RewardInvalid, null);
                    if (await ledger.HasGrantAsync(userId, RewardKind.Analysis, gameId, ct)) break;
                    if (!await ledger.TryGrantAsync(grant with { GameId = gameId }, ct)) return new(false, ErrorCodes.RewardInvalid, null);
                    break;
                }
                default:
                    return new(false, ErrorCodes.RewardInvalid, null);
            }
        }
        return new(true, null, await progressService.GetAsync(userId, ct));
    }
}
