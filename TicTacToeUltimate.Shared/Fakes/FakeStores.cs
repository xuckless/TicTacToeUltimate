using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.Shared.Fakes;

/// <summary>Settable in-memory store base for bUnit tests and design previews.</summary>
public abstract class FakeStore<T> : IStore<T> where T : class
{
    /// <summary>Creates the store.</summary>
    protected FakeStore(T initial) => State = initial;
    /// <inheritdoc />
    public T State { get; private set; }
    /// <inheritdoc />
    public event Action? Changed;
    /// <summary>Recorded intent calls ("PlaceAsync:4").</summary>
    public List<string> Calls { get; } = [];
    /// <summary>Replaces the state and raises Changed.</summary>
    public void Set(T state) { State = state; Changed?.Invoke(); }
}

/// <summary>Fake <see cref="ISessionStore"/>.</summary>
public sealed class FakeSessionStore() : FakeStore<SessionState>(MockupFixtures.SignedInSession), ISessionStore
{
    /// <inheritdoc />
    public Task InitializeAsync() { Calls.Add("Initialize"); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task<IReadOnlyList<DevUserDto>> GetDevUsersAsync() => Task.FromResult<IReadOnlyList<DevUserDto>>(MockupFixtures.DevUsers);
    /// <inheritdoc />
    public Task SignInDevAsync(Guid userId) { Calls.Add($"SignInDev:{userId}"); Set(MockupFixtures.SignedInSession); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task SignInDevNewUserAsync(string username) { Calls.Add($"SignInDevNew:{username}"); Set(MockupFixtures.SignedInSession); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task SignInGuestAsync() { Calls.Add("SignInGuest"); Set(MockupFixtures.GuestSession); return Task.CompletedTask; }
    public Task<string?> SignInEmailAsync(string email, string password) { Calls.Add("SignInEmail:" + email); return Task.FromResult<string?>(null); }
    public Task<(bool NeedsConfirmation, string? Error)> SignUpEmailAsync(string email, string password) { Calls.Add("SignUpEmail:" + email); return Task.FromResult((true, (string?)null)); }
    /// <inheritdoc />
    public Task<string?> SendCodeAsync(string email) { Calls.Add("SendCode:" + email); return Task.FromResult<string?>(null); }
    /// <inheritdoc />
    public Task<string?> VerifyCodeAsync(string email, string code) { Calls.Add("VerifyCode:" + code); return Task.FromResult<string?>(code == "123456" ? null : "auth.code_invalid"); }
    public Task<string?> ResetPasswordAsync(string email) { Calls.Add("Reset:" + email); return Task.FromResult<string?>(null); }
    public Task<bool> CompleteRedirectAsync(string fragment) => Task.FromResult(false);
    public Task<string?> SetPasswordAsync(string password) => Task.FromResult<string?>(null);
    /// <inheritdoc />
    public Task SignOutAsync() { Calls.Add("SignOut"); Set(State with { Status = SessionStatus.SignedOut, Me = null }); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task<string?> DeleteAccountAsync() { Calls.Add("DeleteAccount"); Set(State with { Status = SessionStatus.SignedOut, Me = null }); return Task.FromResult<string?>(null); }
    /// <inheritdoc />
    public Task<CommandAckDto> SetUsernameAsync(string username, bool acceptTos) { Calls.Add($"SetUsername:{username}"); return Task.FromResult(new CommandAckDto(acceptTos, acceptTos ? null : ErrorCodes.TosRequired)); }
    /// <inheritdoc />
    public Task<CommandAckDto> UpdateProfileAsync(UpdateProfileDto patch) { Calls.Add("UpdateProfile"); return Task.FromResult(new CommandAckDto(true, null)); }
}

/// <summary>Fake <see cref="IModerationStore"/>.</summary>
public sealed class FakeModerationStore() : FakeStore<ModerationState>(ModerationState.Empty), IModerationStore
{
    /// <inheritdoc />
    public Task<string?> ReportAsync(ReportRequestDto report) { Calls.Add($"Report:{report.Kind}:{report.Reason}"); return Task.FromResult<string?>(null); }
    /// <inheritdoc />
    public Task<string?> AppealAsync(long penaltyId, string text) { Calls.Add($"Appeal:{penaltyId}"); return Task.FromResult<string?>(null); }
    /// <inheritdoc />
    public Task LoadStandingAsync() { Calls.Add("LoadStanding"); return Task.CompletedTask; }
    /// <inheritdoc />
    public void DismissNotice() => Set(State with { Notice = null });
}

/// <summary>Fake <see cref="IQueueStore"/>.</summary>
public sealed class FakeQueueStore() : FakeStore<QueueState>(QueueState.Idle), IQueueStore
{
    /// <inheritdoc />
    public Task EnterAsync(TimeControl timeControl)
    {
        Calls.Add($"Enter:{timeControl}");
        Set(new QueueState(QueuePhase.Searching, timeControl, new QueueStatusDto(timeControl, 4000, 150, false), null, null));
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task LeaveAsync() { Calls.Add("Leave"); Set(QueueState.Idle); return Task.CompletedTask; }
    /// <inheritdoc />
    public void Acknowledge() { Calls.Add("Acknowledge"); Set(QueueState.Idle); }
}

/// <summary>Fake <see cref="IMatchStore"/>; the clock is whatever <see cref="Clock"/> holds.</summary>
public sealed class FakeMatchStore() : FakeStore<MatchesState>(MatchesState.Empty), IMatchStore
{
    /// <summary>Clock returned by <see cref="GetClock"/>.</summary>
    public ClockView? Clock { get; set; }
    /// <inheritdoc />
    public event Action? ClockTick;
    /// <summary>Adds or replaces a match view.</summary>
    public void Put(MatchView view) => Set(new MatchesState(State.Matches.SetItem(view.MatchId, view)));
    /// <summary>Raises <see cref="ClockTick"/>.</summary>
    public void Tick() => ClockTick?.Invoke();
    /// <inheritdoc />
    public Task<MatchView?> OpenAsync(Guid matchId) { Calls.Add($"Open:{matchId}"); return Task.FromResult(State.Matches.GetValueOrDefault(matchId)); }
    /// <inheritdoc />
    public void Close(Guid matchId) => Calls.Add($"Close:{matchId}");
    /// <inheritdoc />
    public Task PlaceAsync(Guid matchId, int cell) { Calls.Add($"Place:{cell}"); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task DecideSwapAsync(Guid matchId, bool swap) { Calls.Add($"Swap:{swap}"); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task ResignAsync(Guid matchId) { Calls.Add("Resign"); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task SendEmoteAsync(Guid matchId, string emoteId) { Calls.Add($"Emote:{emoteId}"); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task OfferRematchAsync(Guid matchId) { Calls.Add("Rematch"); return Task.CompletedTask; }
    /// <inheritdoc />
    public ClockView? GetClock(Guid matchId) => Clock;
}

/// <summary>Fake <see cref="ISocialStore"/>.</summary>
public sealed class FakeSocialStore() : FakeStore<SocialState>(MockupFixtures.Social), ISocialStore
{
    /// <inheritdoc />
    public Task LoadAsync() { Calls.Add("Load"); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task<IReadOnlyList<PlayerCardDto>> SearchAsync(string prefix)
        => Task.FromResult<IReadOnlyList<PlayerCardDto>>(MockupFixtures.Friends.Select(f => f.Player).Where(p => p.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList());
    /// <inheritdoc />
    public Task<CommandAckDto> SendRequestAsync(string username, Guid? userId = null) { Calls.Add($"Request:{username}"); return Task.FromResult(new CommandAckDto(true, null)); }
    /// <inheritdoc />
    public Task<CommandAckDto> RespondAsync(Guid requestId, bool accept) { Calls.Add($"Respond:{accept}"); return Task.FromResult(new CommandAckDto(true, null)); }
    /// <inheritdoc />
    public Task RemoveFriendAsync(Guid userId) { Calls.Add("Remove"); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task BlockAsync(Guid userId) { Calls.Add("Block"); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task UnblockAsync(Guid userId) { Calls.Add("Unblock"); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task LoadThreadAsync(Guid friendId, long? beforeId = null) { Calls.Add("LoadThread"); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task<CommandAckDto> SendMessageAsync(Guid friendId, string body) { Calls.Add($"Send:{body}"); return Task.FromResult(new CommandAckDto(true, null)); }
    /// <inheritdoc />
    public Task MarkReadAsync(Guid friendId) => Task.CompletedTask;
    /// <inheritdoc />
    public Task<ChallengeDto> CreateChallengeAsync(CreateChallengeDto request)
    {
        Calls.Add($"Challenge:{request.TimeControl}");
        // Mirror the real store: a targeted challenge shows up as "waiting" until the friend answers.
        var c = MockupFixtures.Challenge with { Code = "WAIT01", ToUserId = request.ToUserId, TimeControl = request.TimeControl, From = MockupFixtures.Ali };
        if (request.ToUserId is not null) Set(State with { OutgoingChallenges = [.. State.OutgoingChallenges.Where(x => x.Code != c.Code), c] });
        return Task.FromResult(c);
    }
    /// <inheritdoc />
    public Task<ChallengeDto?> GetChallengeAsync(string code) => Task.FromResult<ChallengeDto?>(MockupFixtures.Challenge with { Code = code });
    /// <inheritdoc />
    public Task<AcceptChallengeResultDto> AcceptChallengeAsync(string code) { Calls.Add($"Accept:{code}"); return Task.FromResult(new AcceptChallengeResultDto(true, null, MockupFixtures.MatchId)); }
    /// <inheritdoc />
    public Task DeclineChallengeAsync(string code) { Calls.Add($"Decline:{code}"); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task CancelChallengeAsync(string code) { Calls.Add($"Cancel:{code}"); return Task.CompletedTask; }
}

/// <summary>Fake <see cref="IActiveMatchesStore"/> (no active games unless a test sets some).</summary>
public sealed class FakeActiveMatchesStore() : FakeStore<ActiveMatchesState>(ActiveMatchesState.Empty with { Loaded = true }), IActiveMatchesStore
{
    /// <inheritdoc />
    public Task RefreshAsync() => Task.CompletedTask;
    /// <inheritdoc />
    public void Forget(Guid matchId) => Set(State with { Matches = [.. State.Matches.Where(m => m.MatchId != matchId)] });
}

/// <summary>Fake <see cref="IProgressStore"/>.</summary>
public sealed class FakeProgressStore() : FakeStore<ProgressState>(new ProgressState(MockupFixtures.Progress, true)), IProgressStore
{
    /// <inheritdoc />
    public Task LoadAsync() => Task.CompletedTask;
    /// <inheritdoc />
    public void Apply(ProgressDto progress) => Set(new ProgressState(progress, true));
    /// <inheritdoc />
    public Task<RewardClaimResultDto> ClaimRewardAsync(RewardKind kind, Guid? gameId = null)
    { Calls.Add($"Claim:{kind}"); return Task.FromResult(new RewardClaimResultDto(true, null, State.Progress)); }
}

/// <summary>Fake <see cref="IGameQueries"/> returning mockup fixtures.</summary>
public sealed class FakeGameQueries : IGameQueries
{
    /// <inheritdoc />
    public Task<ProfileDto?> GetProfileAsync(string username) => Task.FromResult<ProfileDto?>(MockupFixtures.Profile);
    /// <inheritdoc />
    public Task<IReadOnlyList<GameSummaryDto>> GetHistoryAsync(Guid userId, DateTimeOffset? before = null, int take = 20)
        => Task.FromResult<IReadOnlyList<GameSummaryDto>>(before is null ? MockupFixtures.RecentGames : []);
    /// <inheritdoc />
    public Task<GameRecordDto?> GetGameAsync(Guid gameId) => Task.FromResult<GameRecordDto?>(null);
    /// <inheritdoc />
    public Task<(GameAnalysisDto? Analysis, string? Error)> GetAnalysisAsync(Guid gameId) => Task.FromResult<(GameAnalysisDto?, string?)>((null, ErrorCodes.AnalysisUnavailable));
    /// <inheritdoc />
    public Task<LeaderboardPageDto> GetLeaderboardAsync(RatingPool pool, LeaderboardScope scope, string? country, int page)
        => Task.FromResult(new LeaderboardPageDto(pool, scope, country, page, 50, 3,
            MockupFixtures.Friends.Select((f, i) => new LeaderboardRowDto(i + 1, f.Player, f.Player.Rating ?? 1200, 100)).ToArray(), null));
    /// <inheritdoc />
    public Task<IReadOnlyList<DailyGameDto>> GetDailyGamesAsync() => Task.FromResult<IReadOnlyList<DailyGameDto>>([]);
    /// <inheritdoc />
    public Task<IReadOnlyList<LiveMatchDto>> GetLiveFriendMatchesAsync() => Task.FromResult<IReadOnlyList<LiveMatchDto>>([]);
}

/// <summary>Fake <see cref="ISettingsStore"/>.</summary>
public sealed class FakeSettingsStore() : FakeStore<SettingsState>(SettingsState.Default), ISettingsStore
{
    /// <inheritdoc />
    public Task LoadAsync() => Task.CompletedTask;
    /// <inheritdoc />
    public Task UpdateAsync(Func<SettingsState, SettingsState> change) { Set(change(State)); return Task.CompletedTask; }
}

/// <summary>Fake <see cref="IAdCoordinator"/>.</summary>
public sealed class FakeAdCoordinator : IAdCoordinator
{
    /// <summary>Whether a match route is active.</summary>
    public bool MatchRouteActive { get; private set; }
    /// <summary>Completed games reported.</summary>
    public List<GameResultForUser> Completed { get; } = [];
    /// <inheritdoc />
    public bool BannersAllowed => !MatchRouteActive;
    /// <inheritdoc />
    public void SetMatchRouteActive(bool active) => MatchRouteActive = active;
    /// <inheritdoc />
    public Task OnGameCompletedAsync(GameResultForUser result) { Completed.Add(result); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task<RewardedAdResult> ShowRewardedAsync(RewardKind kind) => Task.FromResult(new RewardedAdResult(true, $"stub:{kind}:{Guid.Empty}"));
    /// <summary>Last ad-free pass end.</summary>
    public long? AdFreeUntilMs { get; private set; }
    /// <inheritdoc />
    public void SetAdFreeUntil(long? untilMs) => AdFreeUntilMs = untilMs;
}

/// <summary>Fake <see cref="IWalletStore"/> with a modest balance.</summary>
public sealed class FakeWalletStore() : FakeStore<WalletState>(new WalletState(MockupFixtures.Wallet, [.. MockupFixtures.StoreItems], true)), IWalletStore
{
    /// <inheritdoc />
    public Task LoadAsync() { Calls.Add("LoadWallet"); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task<PurchaseResultDto> BuyAsync(string itemId, Guid? gameId = null)
    {
        Calls.Add($"Buy:{itemId}");
        var item = State.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null || State.Wallet is null || State.Wallet.Diamonds < item.Price) return Task.FromResult(new PurchaseResultDto(false, ErrorCodes.InsufficientDiamonds, null, null));
        var w = State.Wallet with { Diamonds = State.Wallet.Diamonds - item.Price };
        Set(State with { Wallet = w });
        return Task.FromResult(new PurchaseResultDto(true, null, new StoreDto(w, [.. State.Items]), null));
    }
    /// <inheritdoc />
    public Task<RewardClaimResultDto> WatchAdAsync()
    {
        Calls.Add("WatchAd");
        var w = State.Wallet is { } x ? x with { Diamonds = x.Diamonds + x.AdReward, AdViewsToday = x.AdViewsToday + 1 } : null;
        if (w is not null) Set(State with { Wallet = w });
        return Task.FromResult(new RewardClaimResultDto(true, null, null) { Wallet = w });
    }
}

/// <summary>Registers every fake store (tests and design previews).</summary>
public static class FakeServiceCollectionExtensions
{
    /// <summary>Registers fakes as singletons (both as their fake type and their interface).</summary>
    public static IServiceCollection AddTttFakeStores(this IServiceCollection services)
    {
        services.TryAddSingleton<FakeSessionStore>(); services.TryAddSingleton<ISessionStore>(sp => sp.GetRequiredService<FakeSessionStore>());
        services.TryAddSingleton<FakeQueueStore>(); services.TryAddSingleton<IQueueStore>(sp => sp.GetRequiredService<FakeQueueStore>());
        services.TryAddSingleton<FakeMatchStore>(); services.TryAddSingleton<IMatchStore>(sp => sp.GetRequiredService<FakeMatchStore>());
        services.TryAddSingleton<FakeSocialStore>(); services.TryAddSingleton<ISocialStore>(sp => sp.GetRequiredService<FakeSocialStore>());
        services.TryAddSingleton<FakeProgressStore>(); services.TryAddSingleton<IProgressStore>(sp => sp.GetRequiredService<FakeProgressStore>());
        services.TryAddSingleton<FakeWalletStore>(); services.TryAddSingleton<IWalletStore>(sp => sp.GetRequiredService<FakeWalletStore>());
        services.TryAddSingleton<FakeActiveMatchesStore>(); services.TryAddSingleton<IActiveMatchesStore>(sp => sp.GetRequiredService<FakeActiveMatchesStore>());
        services.TryAddSingleton<FakeModerationStore>(); services.TryAddSingleton<IModerationStore>(sp => sp.GetRequiredService<FakeModerationStore>());
        services.TryAddSingleton<FakeSettingsStore>(); services.TryAddSingleton<ISettingsStore>(sp => sp.GetRequiredService<FakeSettingsStore>());
        services.TryAddSingleton<FakeAdCoordinator>(); services.TryAddSingleton<IAdCoordinator>(sp => sp.GetRequiredService<FakeAdCoordinator>());
        services.TryAddSingleton<IGameQueries, FakeGameQueries>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TicTacToeUltimate.Client.Local.LocalPlaySession>();
        return services;
    }
}
