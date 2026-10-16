using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Server.Economy;

/// <summary>
/// Faucets and sinks (owner decision 2026-10-16: one currency, all perks priced in Diamonds, ads only ever grant Diamonds).
/// Numbers are a starting point to tune with live data: a rewarded view is worth roughly 1/10 of a streak freeze, so a
/// freeze costs about 10 views (~$0.06–0.20 of ad revenue at blended eCPMs) and a typical active day earns 60–120 without ads.
/// </summary>
public static class EconomyRules
{
    // ---- earn ----
    public const int WinReward = 5, WinsPaidPerDay = 10;
    public const int LossReward = 1, LossesPaidPerDay = 10;
    public const int Quest1Reward = 10, Quest3Reward = 20, Quest5Reward = 40, BonusQuestReward = 30;
    public const int AdReward = 15, AdViewsPerDay = 5;
    public const int TierPromotionReward = 50;
    public const int FirstUsernameReward = 25;
    /// <summary>Streak milestones (days → diamonds), paid once per milestone per streak run.</summary>
    public static readonly (int Days, int Reward)[] StreakMilestones = [(3, 20), (7, 50), (14, 100), (30, 250), (100, 1000)];

    /// <summary>Daily earning ceiling from gameplay sources (wins + losses); quests, milestones and ads are on top.</summary>
    public const int PlayEarnCapPerDay = WinReward * WinsPaidPerDay + LossReward * LossesPaidPerDay;

    // ---- ledger reasons ----
    public const string ReasonWin = "win", ReasonLoss = "loss", ReasonQuest = "quest", ReasonStreak = "streak", ReasonAd = "ad",
        ReasonTier = "tier", ReasonFirstUsername = "first-username", ReasonPurchase = "purchase", ReasonMerge = "merge";
}

/// <summary>Catalogue item ids.</summary>
public static class StoreItems
{
    public const string StreakFreeze = "streak-freeze", StreakRepair = "streak-repair", ExtraQuest = "extra-quest", Analysis = "analysis",
        AnalysisPass = "analysis-pass-30d", RenameToken = "rename-token", AdFree = "ad-free-7d";

    /// <summary>Static catalogue (title, description, price, icon, kind). Availability is computed per user by the store.</summary>
    public static readonly (string Id, string Title, string Description, int Price, string Icon, string Kind)[] Catalog =
    [
        (StreakFreeze, "Streak freeze", "Skip a day without losing your streak. You can bank 2.", 150, "streak", "perk"),
        (StreakRepair, "Streak repair", "Bring back a streak you lost in the last 2 days.", 300, "flame", "perk"),
        (ExtraQuest, "Bonus quest", "One more quest today: reach 2 more wins for 30 diamonds.", 50, "quest", "perk"),
        (Analysis, "Perfect-play review", "Unlock the engine review of one game.", 20, "chart", "perk"),
        (AnalysisPass, "Review pass · 30 days", "Unlimited perfect-play reviews for a month.", 300, "chart", "pass"),
        (AdFree, "No interstitials · 7 days", "No between-game ads for a week. Rewarded ads stay available.", 400, "tv", "pass"),
        (RenameToken, "Name change", "Change your username once more.", 500, "profile", "perk"),
    ];
}
