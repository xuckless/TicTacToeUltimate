using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Progression;

namespace TicTacToeUltimate.Server.Economy;

/// <summary>
/// Grants and spends Diamonds. Every mutation is an append-only ledger entry; <c>Ref</c> makes grants idempotent
/// (the same (user, reason, ref) never pays twice, so replays of post-game processing or reward claims are safe).
/// Balance reads come from the wallet row the repository keeps in step with the ledger.
/// </summary>
public sealed class WalletService(IWalletRepository wallets, IProgressRepository progress, IUserRepository users, IRewardLedger rewards,
    ProgressService progressService, UserLocks locks, TimeProvider time)
{
    /// <summary>Wallet summary for the UI.</summary>
    public async Task<WalletDto> GetAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var tz = (await users.GetAsync(userId, ct))?.TimeZone;
        var dayStart = LocalDays.NextMidnight(now, tz).AddDays(-1);
        var w = await wallets.GetAsync(userId, ct);
        var earnedToday = await wallets.SumSinceAsync(userId, dayStart, positiveOnly: true, ct);
        var adViews = await wallets.CountSinceAsync(userId, EconomyRules.ReasonAd, dayStart, ct);
        var inv = await wallets.ListInventoryAsync(userId, ct);
        long? Until(string item) => inv.FirstOrDefault(i => i.ItemId == item && (i.ExpiresAt is null || i.ExpiresAt > now))?.ExpiresAt?.ToUnixTimeMilliseconds();
        return new WalletDto(w.Balance, earnedToday, adViews, EconomyRules.AdViewsPerDay, EconomyRules.AdReward, Until(StoreItems.AdFree), Until(StoreItems.AnalysisPass));
    }

    /// <summary>Pays <paramref name="amount"/> once per (reason, ref). Returns the amount actually granted (0 when already paid or capped).</summary>
    public async Task<int> GrantAsync(Guid userId, int amount, string reason, string reference, CancellationToken ct)
    {
        if (amount <= 0) return 0;
        return await wallets.AppendAsync(new LedgerEntry(userId, amount, reason, reference, time.GetUtcNow()), ct) ? amount : 0;
    }

    /// <summary>Gameplay payout with daily caps (first N wins / losses per local day). Challenge, rematch and bot games pay nothing (anti-farm).</summary>
    public async Task<int> GrantGameAsync(Guid userId, bool won, Guid matchId, DateTimeOffset at, CancellationToken ct)
    {
        var tz = (await users.GetAsync(userId, ct))?.TimeZone;
        var dayStart = LocalDays.NextMidnight(at, tz).AddDays(-1);
        var reason = won ? EconomyRules.ReasonWin : EconomyRules.ReasonLoss;
        var paid = await wallets.CountSinceAsync(userId, reason, dayStart, ct);
        if (paid >= (won ? EconomyRules.WinsPaidPerDay : EconomyRules.LossesPaidPerDay)) return 0;
        return await GrantAsync(userId, won ? EconomyRules.WinReward : EconomyRules.LossReward, reason, $"game:{matchId:N}", ct);
    }

    /// <summary>True when the user may still earn from a rewarded ad today.</summary>
    public async Task<bool> CanWatchAdAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var tz = (await users.GetAsync(userId, ct))?.TimeZone;
        var dayStart = LocalDays.NextMidnight(now, tz).AddDays(-1);
        return await wallets.CountSinceAsync(userId, EconomyRules.ReasonAd, dayStart, ct) < EconomyRules.AdViewsPerDay;
    }

    /// <summary>Store catalogue with per-user availability.</summary>
    public async Task<StoreDto> StoreAsync(Guid userId, CancellationToken ct)
    {
        var wallet = await GetAsync(userId, ct);
        var u = await users.GetAsync(userId, ct);
        var streak = await progress.GetStreakAsync(userId, ct);
        var now = time.GetUtcNow();
        var day = LocalDays.Day(now, u?.TimeZone);
        var quest = await progress.GetQuestDayAsync(userId, day, ct);
        var items = new List<StoreItemDto>();
        foreach (var (id, title, desc, price, icon, kind) in StoreItems.Catalog)
        {
            string? why = null;
            int? owned = null;
            switch (id)
            {
                case StoreItems.StreakFreeze:
                    owned = streak.Freezes;
                    if (streak.Freezes >= StreakRules.MaxFreezes) why = "You already bank 2 freezes.";
                    break;
                case StoreItems.StreakRepair:
                    if (!RepairPossible(streak, day)) why = "No lost streak to repair.";
                    break;
                case StoreItems.ExtraQuest:
                    if (quest.BonusTarget is not null) why = "Already active today.";
                    break;
                case StoreItems.Analysis:
                    why = "Buy from a finished game's result or replay.";
                    break;
                case StoreItems.AnalysisPass:
                    if (wallet.AnalysisPassUntilMs is not null) why = "Active";
                    break;
                case StoreItems.AdFree:
                    if (wallet.AdFreeUntilMs is not null) why = "Active";
                    break;
                case StoreItems.RenameToken:
                    if (u is null || u.Kind != AccountKind.Account) why = "Accounts only.";
                    else if (u.RenamesLeft > 0) why = "You still have a free rename.";
                    break;
            }
            items.Add(new StoreItemDto(id, title, desc, price, icon, kind, why is null, why, owned));
        }
        return new StoreDto(wallet, items.ToArray());
    }

    /// <summary>Buys an item: debits the ledger, applies the effect. Serialized per user.</summary>
    public async Task<PurchaseResultDto> PurchaseAsync(Guid userId, PurchaseDto req, CancellationToken ct)
    {
        var item = StoreItems.Catalog.FirstOrDefault(c => c.Id == req.ItemId);
        if (item.Id is null) return new(false, ErrorCodes.ItemUnavailable, null, null);
        using (await locks.AcquireAsync(userId, ct))
        {
            var now = time.GetUtcNow();
            var u = await users.GetAsync(userId, ct);
            var tz = u?.TimeZone;
            var day = LocalDays.Day(now, tz);
            string reference;
            Func<Task> apply;
            switch (item.Id)
            {
                case StoreItems.StreakFreeze:
                {
                    var s = await progress.GetStreakAsync(userId, ct);
                    if (s.Freezes >= StreakRules.MaxFreezes) return new(false, ErrorCodes.ItemUnavailable, null, null);
                    reference = $"freeze:{Guid.NewGuid():N}";
                    apply = () => progress.PutStreakAsync(s with { Freezes = s.Freezes + 1 }, ct);
                    break;
                }
                case StoreItems.StreakRepair:
                {
                    var s = await progress.GetStreakAsync(userId, ct);
                    if (!RepairPossible(s, day)) return new(false, ErrorCodes.ItemUnavailable, null, null);
                    reference = $"repair:{day:yyyy-MM-dd}";
                    // Pretend the missed days were covered: the streak is alive again and continues with today's game.
                    apply = () => progress.PutStreakAsync(s with { LastPlayedDay = day.AddDays(-1) }, ct);
                    break;
                }
                case StoreItems.ExtraQuest:
                {
                    var q = await progress.GetQuestDayAsync(userId, day, ct);
                    if (q.BonusTarget is not null) return new(false, ErrorCodes.ItemUnavailable, null, null);
                    reference = $"extra-quest:{day:yyyy-MM-dd}";
                    apply = () => progress.PutQuestDayAsync(q with { BonusTarget = q.Wins + 2, BonusClaimed = false }, ct);
                    break;
                }
                case StoreItems.Analysis:
                {
                    if (req.GameId is not { } g) return new(false, ErrorCodes.Validation, null, null);
                    if (await rewards.HasGrantAsync(userId, RewardKind.Analysis, g, ct)) return new(false, ErrorCodes.ItemUnavailable, null, null);
                    reference = $"analysis:{g:N}";
                    apply = () => rewards.TryGrantAsync(new RewardGrantRecord($"buy:{g:N}:{userId:N}", userId, RewardKind.Analysis, g, now), ct);
                    break;
                }
                case StoreItems.AnalysisPass:
                case StoreItems.AdFree:
                {
                    var inv = await wallets.ListInventoryAsync(userId, ct);
                    if (inv.Any(i => i.ItemId == item.Id && i.ExpiresAt > now)) return new(false, ErrorCodes.ItemUnavailable, null, null);
                    var days = item.Id == StoreItems.AdFree ? 7 : 30;
                    reference = $"{item.Id}:{now:yyyyMMddHHmmss}";
                    apply = () => wallets.AddInventoryAsync(new InventoryItem(userId, item.Id, now, now.AddDays(days)), ct);
                    break;
                }
                case StoreItems.RenameToken:
                {
                    if (u is null || u.Kind != AccountKind.Account || u.RenamesLeft > 0) return new(false, ErrorCodes.ItemUnavailable, null, null);
                    reference = $"rename:{now:yyyyMMddHHmmss}";
                    apply = () => users.AddRenamesAsync(userId, 1, ct);
                    break;
                }
                default:
                    return new(false, ErrorCodes.ItemUnavailable, null, null);
            }
            // Debit first (atomic against the balance), then apply the effect.
            if (!await wallets.AppendAsync(new LedgerEntry(userId, -item.Price, EconomyRules.ReasonPurchase, reference, now), ct))
            {
                var w = await wallets.GetAsync(userId, ct);
                return new(false, w.Balance < item.Price ? ErrorCodes.InsufficientDiamonds : ErrorCodes.ItemUnavailable, null, null);
            }
            await apply();
        }
        return new(true, null, await StoreAsync(userId, ct), await progressService.GetAsync(userId, ct));
    }

    /// <summary>A streak is repairable when it is broken as of today, was alive within the last 2 days and had at least 1 day.</summary>
    internal static bool RepairPossible(StreakRecord s, DateOnly today)
    {
        if (s.LastPlayedDay is not { } last || s.Current <= 0) return false;
        var gap = today.DayNumber - last.DayNumber - 1;   // missed days
        return gap > s.Freezes && gap <= 2;
    }
}
