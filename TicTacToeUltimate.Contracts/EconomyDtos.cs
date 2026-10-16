namespace TicTacToeUltimate.Contracts;

/// <summary>Diamonds: the single currency (earned by play, quests, streaks and rewarded ads; bought later). Server-authoritative.</summary>
public sealed record WalletDto(int Diamonds, int EarnedToday, int AdViewsToday, int AdViewsPerDay, int AdReward, long? AdFreeUntilMs, long? AnalysisPassUntilMs);

/// <summary>A store entry. <c>Available</c> false + <c>Unavailable</c> explains why (capped, already owned, needs account).</summary>
public sealed record StoreItemDto(string Id, string Title, string Description, int Price, string Icon, string Kind /* perk | pass | cosmetic */,
    bool Available, string? Unavailable, int? Owned);

public sealed record StoreDto(WalletDto Wallet, StoreItemDto[] Items);
public sealed record PurchaseDto(string ItemId, Guid? GameId);
public sealed record PurchaseResultDto(bool Ok, string? Error, StoreDto? Store, ProgressDto? Progress);
