using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Persistence.InMemory;
using TicTacToeUltimate.Server.Persistence.Postgres;

namespace TicTacToeUltimate.Server.Tests.Persistence.Contracts;

public sealed record Repos(IUserRepository Users, IRatingRepository Ratings, IGameRepository Games, IDailyMatchRepository Daily,
    IFriendRepository Friends, IChatRepository Chat, IProgressRepository Progress, IChallengeRepository Challenges, IBotKeyRepository BotKeys,
    IRewardLedger Rewards, ISeasonRepository Seasons);

public interface IRepoFactory { Repos Create(); }

public sealed class InMemoryFactory : IRepoFactory
{
    public Repos Create()
    {
        var users = new InMemoryUserRepository();
        return new Repos(users, new InMemoryRatingRepository(users), new InMemoryGameRepository(), new InMemoryDailyMatchRepository(),
            new InMemoryFriendRepository(), new InMemoryChatRepository(), new InMemoryProgressRepository(), new InMemoryChallengeRepository(),
            new InMemoryBotKeyRepository(), new InMemoryRewardLedger(), new InMemorySeasonRepository(Options.Create(new TttOptions())));
    }
}

/// <summary>Runs against a migrated database when TTT_TEST_PG is set; otherwise skips.</summary>
public sealed class PostgresFactory : IRepoFactory
{
    public Repos Create()
    {
        var cs = Environment.GetEnvironmentVariable("TTT_TEST_PG");
        if (string.IsNullOrEmpty(cs)) Assert.Skip("TTT_TEST_PG not set");
        var db = new PgDb(cs);
        return new Repos(new PgUserRepository(db), new PgRatingRepository(db), new PgGameRepository(db), new PgDailyMatchRepository(db),
            new PgFriendRepository(db), new PgChatRepository(db), new PgProgressRepository(db), new PgChallengeRepository(db),
            new PgBotKeyRepository(db), new PgRewardLedger(db), new PgSeasonRepository(db, Options.Create(new TttOptions())));
    }
}

public abstract class ContractBase<TF> where TF : IRepoFactory, new()
{
    protected static readonly CancellationToken Ct = CancellationToken.None;
    protected static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private Repos? _r;
    protected Repos R => _r ??= new TF().Create();

    protected static string Name() => "u" + Guid.NewGuid().ToString("N")[..12];

    protected async Task<UserRecord> NewUserAsync(string? username = null, AccountKind kind = AccountKind.Account, string? device = null)
    {
        var u = new UserRecord(Guid.NewGuid(), kind, username, username ?? "Guest#0001", "a1", null, "UTC", 1, null, device, T0, T0);
        Assert.True(await R.Users.CreateAsync(u, Ct));
        return u;
    }

    protected static MatchSetup Setup(UserRecord a, UserRecord b, Guid? id = null) => new(id ?? Guid.NewGuid(), MatchMode.Ranked, TimeControl.Ranked,
        RuleSet.RankedDefault, true, new SeatSetup(a.Id, a.DisplayName, a.Kind), new SeatSetup(b.Id, b.DisplayName, b.Kind), 123UL << 60, MatchOrigin.Queue, null, null);
}

public abstract class UserRepositoryContractTests<TF> : ContractBase<TF> where TF : IRepoFactory, new()
{
    [Fact]
    public async Task CreateGetAndUniqueness()
    {
        var name = Name();
        var u = await NewUserAsync(name);
        Assert.Equal(name, (await R.Users.GetAsync(u.Id, Ct))!.Username);
        Assert.Equal(u.Id, (await R.Users.GetByUsernameAsync(name.ToUpperInvariant(), Ct))!.Id);
        Assert.False(await R.Users.CreateAsync(u with { Id = Guid.NewGuid() }, Ct));
        Assert.False(await R.Users.CreateAsync(u with { Username = Name() }, Ct));
        var many = await R.Users.GetManyAsync([u.Id, Guid.NewGuid()], Ct);
        Assert.Single(many);
    }

    [Fact]
    public async Task UsernameFirstSetFreeThenOneRename_KindUnchanged()
    {
        var device = Guid.NewGuid().ToString();
        var g = await NewUserAsync(null, AccountKind.Guest, device);
        Assert.Equal(g.Id, (await R.Users.GetByDeviceIdAsync(device, Ct))!.Id);
        var n1 = Name();
        Assert.Equal(UsernameResult.Ok, await R.Users.SetUsernameAsync(g.Id, n1, T0, Ct));
        var after = (await R.Users.GetAsync(g.Id, Ct))!;
        Assert.Equal(AccountKind.Guest, after.Kind);   // the repository never promotes a guest; only a verified sign-in + claim does
        Assert.Equal(1, after.RenamesLeft);
        Assert.Equal(UsernameResult.Ok, await R.Users.SetUsernameAsync(g.Id, Name(), null, Ct));
        Assert.Equal(0, (await R.Users.GetAsync(g.Id, Ct))!.RenamesLeft);
        Assert.Equal(UsernameResult.NoRenamesLeft, await R.Users.SetUsernameAsync(g.Id, Name(), null, Ct));
        var other = await NewUserAsync(Name());
        Assert.Equal(UsernameResult.Taken, await R.Users.SetUsernameAsync((await NewUserAsync(null, AccountKind.Guest)).Id, other.Username!, T0, Ct));
    }

    [Fact]
    public async Task ProfileTouchSearch()
    {
        var prefix = "s" + Guid.NewGuid().ToString("N")[..8];
        var u = await NewUserAsync(prefix + "_a");
        await NewUserAsync(prefix + "_b");
        await R.Users.UpdateProfileAsync(u.Id, new ProfilePatch(null, "CA", "America/Toronto"), Ct);
        await R.Users.TouchAsync(u.Id, T0.AddHours(1), Ct);
        var got = (await R.Users.GetAsync(u.Id, Ct))!;
        Assert.Equal(("CA", "America/Toronto", "a1"), (got.Country, got.TimeZone, got.AvatarId));
        Assert.Equal(T0.AddHours(1), got.LastSeenAt);
        Assert.Equal(2, (await R.Users.SearchAsync(prefix, 10, Ct)).Count);
        Assert.NotEmpty(await R.Users.ListAsync(10, Ct));
    }
}

public abstract class RatingRepositoryContractTests<TF> : ContractBase<TF> where TF : IRepoFactory, new()
{
    [Fact]
    public async Task InitialPutAndLeaderboard()
    {
        var a = await NewUserAsync(Name());
        var b = await NewUserAsync(Name());
        Assert.Equal(1200, (await R.Ratings.GetAsync(a.Id, RatingPool.Ranked, Ct)).Rating);
        await R.Ratings.PutAsync(RatingRecord.Initial(a.Id, RatingPool.Ranked) with { Rating = 9001, Games = 30 }, Ct);
        await R.Ratings.PutAsync(RatingRecord.Initial(b.Id, RatingPool.Ranked) with { Rating = 9000, Games = 30 }, Ct);
        Assert.Single(await R.Ratings.GetAllAsync(a.Id, Ct));
        var page = await R.Ratings.QueryAsync(new LeaderboardQuery(RatingPool.Ranked, LeaderboardScope.Friends, null, b.Id, [a.Id, b.Id], 20, 8999, 1, 10), Ct);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(a.Id, page.Rows[0].UserId);
        Assert.Equal(2, page.Viewer!.Rank);
    }
}

public abstract class GameRepositoryContractTests<TF> : ContractBase<TF> where TF : IRepoFactory, new()
{
    [Fact]
    public async Task SaveIsIdempotentAndRoundTrips()
    {
        var a = await NewUserAsync(Name());
        var b = await NewUserAsync(Name());
        var setup = Setup(a, b);
        var g = new GameRecord(setup.MatchId, setup, true,
            [new StoredAction(0, 0, GameAction.Place(4), 1200, false, null, T0), new StoredAction(1, 1, GameAction.Swap, 800, false, null, T0.AddSeconds(2))],
            new MatchResultDto(Outcome.XWins, 0, EndReason.Line, [0, 4, 8], true, null), T0.AddMinutes(-1), T0,
            [new RatingSnapshot(0, RatingPool.Ranked, 1500, 1510)]);
        Assert.True(await R.Games.SaveCompletedAsync(g, Ct));
        Assert.False(await R.Games.SaveCompletedAsync(g, Ct));
        var back = (await R.Games.GetAsync(g.Id, Ct))!;
        Assert.Equal(2, back.Actions.Count);
        Assert.Equal(ActionKind.Swap, back.Actions[1].Action.Kind);
        Assert.Equal(setup.Seed, back.Setup.Seed);
        Assert.Equal("n3k3-cap3-v0-w1-pie", back.Setup.Rules.Id);
        Assert.Equal([0, 4, 8], back.Result.WinLine);
        Assert.Equal(1510, back.Ratings[0].After);
        var list = await R.Games.ListForUserAsync(b.Id, null, 10, Ct);
        Assert.Single(list);
        Assert.Equal(2, list[0].Plies);
        Assert.Empty(await R.Games.ListForUserAsync(b.Id, T0.AddMinutes(-5), 10, Ct));
        Assert.Equal(1, await R.Games.CountForUserAsync(a.Id, Ct));
    }
}

public abstract class DailyMatchRepositoryContractTests<TF> : ContractBase<TF> where TF : IRepoFactory, new()
{
    [Fact]
    public async Task OptimisticAppendDueAndEnd()
    {
        var a = await NewUserAsync(Name());
        var b = await NewUserAsync(Name());
        var s = Setup(a, b) with { Mode = MatchMode.Daily, TimeControl = TimeControl.Daily };
        await R.Daily.CreateAsync(s, T0, T0.AddDays(1), Ct);
        var v = await R.Daily.AppendActionAsync(s.MatchId, 0, new StoredAction(0, 0, GameAction.Place(0), 10, false, null, T0), T0.AddHours(2), Ct);
        Assert.Equal(1, v);
        await Assert.ThrowsAsync<ConcurrencyException>(() => R.Daily.AppendActionAsync(s.MatchId, 0, new StoredAction(1, 1, GameAction.Place(1), 10, false, null, T0), null, Ct));
        Assert.Contains(s.MatchId, await R.Daily.ListDueAsync(T0.AddHours(3), Ct));
        Assert.DoesNotContain(s.MatchId, await R.Daily.ListDueAsync(T0.AddHours(1), Ct));
        Assert.Single(await R.Daily.ListActiveForUserAsync(b.Id, Ct));
        var loaded = (await R.Daily.LoadAsync(s.MatchId, Ct))!;
        Assert.Single(loaded.Actions);
        await R.Daily.MarkEndedAsync(s.MatchId, Ct);
        Assert.Empty(await R.Daily.ListActiveForUserAsync(b.Id, Ct));
    }
}

public abstract class FriendRepositoryContractTests<TF> : ContractBase<TF> where TF : IRepoFactory, new()
{
    [Fact]
    public async Task RequestsAutoAcceptBlock()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        Assert.Equal(FriendRequestResult.Created, await R.Friends.RequestAsync(a, b, T0, Ct));
        Assert.Equal(FriendRequestResult.AlreadyRequested, await R.Friends.RequestAsync(a, b, T0, Ct));
        Assert.Equal(FriendRequestResult.AutoAccepted, await R.Friends.RequestAsync(b, a, T0, Ct));
        Assert.True(await R.Friends.AreFriendsAsync(b, a, Ct));
        Assert.Equal(FriendRequestResult.AlreadyFriends, await R.Friends.RequestAsync(a, b, T0, Ct));
        Assert.Equal(FriendRequestResult.Created, await R.Friends.RequestAsync(c, a, T0, Ct));
        var req = (await R.Friends.ListRequestsAsync(a, Ct)).Single();
        Assert.False(await R.Friends.RespondAsync(req.Id, c, true, Ct));
        Assert.True(await R.Friends.RespondAsync(req.Id, a, true, Ct));
        Assert.Equal(2, (await R.Friends.ListFriendsAsync(a, Ct)).Count);
        await R.Friends.BlockAsync(a, b, Ct);
        Assert.False(await R.Friends.AreFriendsAsync(a, b, Ct));
        Assert.True(await R.Friends.IsBlockedEitherWayAsync(b, a, Ct));
        Assert.Equal(FriendRequestResult.Blocked, await R.Friends.RequestAsync(b, a, T0, Ct));
        Assert.Equal([b], await R.Friends.ListBlockedAsync(a, Ct));
        await R.Friends.UnblockAsync(a, b, Ct);
        Assert.False(await R.Friends.IsBlockedEitherWayAsync(a, b, Ct));
        await R.Friends.RemoveAsync(c, a, Ct);
        Assert.Empty(await R.Friends.ListFriendsAsync(a, Ct));
    }
}

public abstract class ChatRepositoryContractTests<TF> : ContractBase<TF> where TF : IRepoFactory, new()
{
    [Fact]
    public async Task PageNewestFirstAndUnread()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var m1 = await R.Chat.AppendAsync(a, b, "one", T0, Ct);
        var m2 = await R.Chat.AppendAsync(b, a, "two", T0, Ct);
        var m3 = await R.Chat.AppendAsync(a, b, "three", T0, Ct);
        var page = await R.Chat.PageAsync(b, a, null, 2, Ct);
        Assert.Equal([m3.Id, m2.Id], page.Select(m => m.Id));
        Assert.Equal([m1.Id], (await R.Chat.PageAsync(a, b, m2.Id, 10, Ct)).Select(m => m.Id));
        Assert.Equal(2, (await R.Chat.UnreadCountsAsync(b, Ct))[a]);
        await R.Chat.MarkReadAsync(b, a, m1.Id, T0, Ct);
        Assert.Equal(1, (await R.Chat.UnreadCountsAsync(b, Ct))[a]);
    }
}

public abstract class ProgressRepositoryContractTests<TF> : ContractBase<TF> where TF : IRepoFactory, new()
{
    [Fact]
    public async Task StreakAndQuestDayRoundTrip()
    {
        var id = Guid.NewGuid();
        Assert.Equal(0, (await R.Progress.GetStreakAsync(id, Ct)).Current);
        var day = new DateOnly(2026, 10, 15);
        await R.Progress.PutStreakAsync(new StreakRecord(id, 3, 7, day, 2), Ct);
        Assert.Equal(new StreakRecord(id, 3, 7, day, 2), await R.Progress.GetStreakAsync(id, Ct));
        await R.Progress.PutQuestDayAsync(new QuestDayRecord(id, day, 4, true, 6, false), Ct);
        Assert.Equal(new QuestDayRecord(id, day, 4, true, 6, false), await R.Progress.GetQuestDayAsync(id, day, Ct));
        Assert.Equal(0, (await R.Progress.GetQuestDayAsync(id, day.AddDays(1), Ct)).Wins);
    }
}

public abstract class ChallengeRepositoryContractTests<TF> : ContractBase<TF> where TF : IRepoFactory, new()
{
    [Fact]
    public async Task CreateCasAndListOpen()
    {
        var code = "C" + Guid.NewGuid().ToString("N")[..7];
        var target = Guid.NewGuid();
        var rec = new ChallengeRecord(code, Guid.NewGuid(), target, TimeControl.CasualX2, "n3k3-cap3-v0-w1-none", ChallengeStatus.Open, null, null, T0, T0.AddHours(24));
        Assert.True(await R.Challenges.CreateAsync(rec, Ct));
        Assert.False(await R.Challenges.CreateAsync(rec, Ct));
        Assert.Single(await R.Challenges.ListOpenForTargetAsync(target, T0, Ct));
        Assert.Empty(await R.Challenges.ListOpenForTargetAsync(target, T0.AddHours(25), Ct));
        var winner = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            R.Challenges.TryTransitionAsync(code, ChallengeStatus.Open, ChallengeStatus.Accepted, i == 0 ? winner : Guid.NewGuid(), Guid.NewGuid(), Ct)));
        Assert.Equal(1, results.Count(x => x));
        Assert.Equal(ChallengeStatus.Accepted, (await R.Challenges.GetAsync(code, Ct))!.Status);
    }
}

public abstract class BotKeyRepositoryContractTests<TF> : ContractBase<TF> where TF : IRepoFactory, new()
{
    [Fact]
    public async Task CreateFindRevoke()
    {
        var bot = await NewUserAsync(Name(), AccountKind.Bot);
        var owner = Guid.NewGuid();
        var key = "k" + Guid.NewGuid().ToString("N")[..8];
        await R.BotKeys.CreateAsync(new BotKeyRecord(key, bot.Id, owner, "b", [1, 2, 3], T0, null), Ct);
        Assert.Equal([1, 2, 3], (await R.BotKeys.FindAsync(key, Ct))!.SecretHash);
        Assert.False(await R.BotKeys.RevokeAsync(key, Guid.NewGuid(), T0, Ct));
        Assert.True(await R.BotKeys.RevokeAsync(key, owner, T0, Ct));
        Assert.NotNull((await R.BotKeys.ListForOwnerAsync(owner, Ct)).Single().RevokedAt);
    }
}

public abstract class RewardLedgerContractTests<TF> : ContractBase<TF> where TF : IRepoFactory, new()
{
    [Fact]
    public async Task NonceIsUnique()
    {
        var user = Guid.NewGuid();
        var game = Guid.NewGuid();
        var nonce = "stub:x:" + Guid.NewGuid();
        Assert.True(await R.Rewards.TryGrantAsync(new RewardGrantRecord(nonce, user, RewardKind.Analysis, game, T0), Ct));
        Assert.False(await R.Rewards.TryGrantAsync(new RewardGrantRecord(nonce, user, RewardKind.Analysis, game, T0), Ct));
        Assert.True(await R.Rewards.HasGrantAsync(user, RewardKind.Analysis, game, Ct));
        Assert.False(await R.Rewards.HasGrantAsync(user, RewardKind.Analysis, Guid.NewGuid(), Ct));
    }
}

public abstract class SeasonRepositoryContractTests<TF> : ContractBase<TF> where TF : IRepoFactory, new()
{
    [Fact]
    public async Task CreatesFirstSeasonAndCloses()
    {
        var s = await R.Seasons.GetCurrentAsync(T0, Ct);
        Assert.True(s.EndsAt > s.StartsAt);
        var next = new SeasonRecord(s.Id + 1, s.EndsAt, s.EndsAt.AddDays(90));
        await R.Seasons.CloseAsync(s, [new MasterSeasonResult(s.Id, Guid.NewGuid(), 2400, 1)], next, Ct);
        Assert.True((await R.Seasons.GetCurrentAsync(T0, Ct)).Id > s.Id);
    }
}

public sealed class InMemoryUserRepositoryTests : UserRepositoryContractTests<InMemoryFactory>;
public sealed class InMemoryRatingRepositoryTests : RatingRepositoryContractTests<InMemoryFactory>;
public sealed class InMemoryGameRepositoryTests : GameRepositoryContractTests<InMemoryFactory>;
public sealed class InMemoryDailyMatchRepositoryTests : DailyMatchRepositoryContractTests<InMemoryFactory>;
public sealed class InMemoryFriendRepositoryTests : FriendRepositoryContractTests<InMemoryFactory>;
public sealed class InMemoryChatRepositoryTests : ChatRepositoryContractTests<InMemoryFactory>;
public sealed class InMemoryProgressRepositoryTests : ProgressRepositoryContractTests<InMemoryFactory>;
public sealed class InMemoryChallengeRepositoryTests : ChallengeRepositoryContractTests<InMemoryFactory>;
public sealed class InMemoryBotKeyRepositoryTests : BotKeyRepositoryContractTests<InMemoryFactory>;
public sealed class InMemoryRewardLedgerTests : RewardLedgerContractTests<InMemoryFactory>;
public sealed class InMemorySeasonRepositoryTests : SeasonRepositoryContractTests<InMemoryFactory>;

public sealed class PostgresUserRepositoryTests : UserRepositoryContractTests<PostgresFactory>;
public sealed class PostgresRatingRepositoryTests : RatingRepositoryContractTests<PostgresFactory>;
public sealed class PostgresGameRepositoryTests : GameRepositoryContractTests<PostgresFactory>;
public sealed class PostgresDailyMatchRepositoryTests : DailyMatchRepositoryContractTests<PostgresFactory>;
public sealed class PostgresFriendRepositoryTests : FriendRepositoryContractTests<PostgresFactory>;
public sealed class PostgresChatRepositoryTests : ChatRepositoryContractTests<PostgresFactory>;
public sealed class PostgresProgressRepositoryTests : ProgressRepositoryContractTests<PostgresFactory>;
public sealed class PostgresChallengeRepositoryTests : ChallengeRepositoryContractTests<PostgresFactory>;
public sealed class PostgresBotKeyRepositoryTests : BotKeyRepositoryContractTests<PostgresFactory>;
public sealed class PostgresRewardLedgerTests : RewardLedgerContractTests<PostgresFactory>;
public sealed class PostgresSeasonRepositoryTests : SeasonRepositoryContractTests<PostgresFactory>;
