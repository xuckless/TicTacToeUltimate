using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Challenges;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Tests.Matches;

namespace TicTacToeUltimate.Server.Tests.Challenges;

public class ChallengeServiceTests
{
    private sealed class FakeChallengeRepo : IChallengeRepository
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, ChallengeRecord> _rows = new(StringComparer.OrdinalIgnoreCase);

        public Task<bool> CreateAsync(ChallengeRecord record, CancellationToken ct)
        {
            lock (_gate) return Task.FromResult(_rows.TryAdd(record.Code, record));
        }

        public Task<ChallengeRecord?> GetAsync(string code, CancellationToken ct)
        {
            lock (_gate) return Task.FromResult(_rows.GetValueOrDefault(code));
        }

        public async Task<bool> TryTransitionAsync(string code, ChallengeStatus expected, ChallengeStatus next, Guid? acceptedBy, Guid? matchId, CancellationToken ct)
        {
            await Task.Yield();
            lock (_gate)
            {
                if (!_rows.TryGetValue(code, out var r) || r.Status != expected) return false;
                _rows[code] = r with { Status = next, AcceptedBy = acceptedBy ?? r.AcceptedBy, MatchId = matchId ?? r.MatchId };
                return true;
            }
        }

        public Task<IReadOnlyList<ChallengeRecord>> ListOpenForTargetAsync(Guid targetUserId, DateTimeOffset now, CancellationToken ct)
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<ChallengeRecord>>([.. _rows.Values.Where(r => r.TargetUserId == targetUserId && r.Status == ChallengeStatus.Open && r.ExpiresAt > now)]);
        }
    }

    private sealed class FakeGateway : IMatchGateway
    {
        public ConcurrentQueue<MatchSetup> Created { get; } = new();
        public Task<Guid> CreateMatchAsync(MatchSetup setup, CancellationToken ct) { Created.Enqueue(setup); return Task.FromResult(setup.MatchId); }
        public Task<AttachResult> AttachAsync(Guid matchId, Guid userId, IMatchObserver observer, CancellationToken ct) => throw new NotSupportedException();
        public Task<MatchSnapshotDto?> GetSnapshotAsync(Guid matchId, Guid? viewerUserId, CancellationToken ct) => throw new NotSupportedException();
        public Task<MoveAckDto> SubmitActionAsync(Guid matchId, Guid userId, int expectedPly, GameAction action, Guid actionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommandAckDto> ResignAsync(Guid matchId, Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<Guid> ActiveMatchesFor(Guid userId) => [];
    }

    private static readonly TttUser Creator = new(Guid.NewGuid(), AccountKind.Account, "creator");

    private sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        public FakeChallengeRepo Repo { get; } = new();
        public FakeGateway Gateway { get; } = new();
        public RecordingNotifier Notifier { get; } = new();
        public UserActivityRegistry Activity { get; } = new();
        public ChallengeService Service { get; }
        public Rig() => Service = new ChallengeService(Repo, Gateway, Activity, Notifier, new FakeCards(), Time, new TttOptions(),
            NullLogger<ChallengeService>.Instance);
    }

    [Fact]
    public void CodesAreEightCrockfordChars()
    {
        var code = ChallengeService.NewCode();
        Assert.Equal(8, code.Length);
        Assert.All(code, ch => Assert.Contains(ch, "0123456789ABCDEFGHJKMNPQRSTVWXYZ"));
    }

    [Fact]
    public async Task TwoAcceptorsOnlyOneWins()
    {
        var r = new Rig();
        var c = await r.Service.CreateAsync(Creator, new CreateChallengeDto(TimeControl.CasualX2, null), default);
        Assert.Equal(ChallengeStatus.Open, c.Status);
        Assert.EndsWith("/c/" + c.Code, c.Url);
        var u1 = new TttUser(Guid.NewGuid(), AccountKind.Account, "one");
        var u2 = new TttUser(Guid.NewGuid(), AccountKind.Guest, "two");
        var results = await Task.WhenAll(
            Task.Run(() => r.Service.AcceptAsync(u1, c.Code, default)),
            Task.Run(() => r.Service.AcceptAsync(u2, c.Code.ToLowerInvariant(), default)));
        Assert.Single(results, x => x.Accepted);
        Assert.Single(results, x => !x.Accepted && x.Error == ErrorCodes.ChallengeTaken);
        var setup = Assert.Single(r.Gateway.Created);
        Assert.Equal(MatchMode.Challenge, setup.Mode);
        Assert.False(setup.Rated);
        Assert.Equal(RuleSet.Default.Id, setup.Rules.Id);
        Assert.Equal(ChallengeStatus.Accepted, (await r.Service.GetAsync(c.Code, default))!.Status);
    }

    [Fact]
    public async Task ExpiresLazilyAfterTtl()
    {
        var r = new Rig();
        var c = await r.Service.CreateAsync(Creator, new CreateChallengeDto(TimeControl.Unlimited, null), default);
        r.Time.Advance(TimeSpan.FromHours(24));
        Assert.Equal(ChallengeStatus.Expired, (await r.Service.GetAsync(c.Code, default))!.Status);
        var res = await r.Service.AcceptAsync(new TttUser(Guid.NewGuid(), AccountKind.Account, "late"), c.Code, default);
        Assert.Equal(ErrorCodes.ChallengeExpired, res.Error);
        Assert.Contains(r.Notifier.Calls, x => x.Method == "ChallengeUpdated");
    }

    [Fact]
    public async Task SweepExpiresOpenChallenges()
    {
        var r = new Rig();
        var c = await r.Service.CreateAsync(Creator, new CreateChallengeDto(TimeControl.Unlimited, null), default);
        r.Time.Advance(TimeSpan.FromHours(25));
        await r.Service.SweepAsync(default);
        Assert.Equal(ChallengeStatus.Expired, (await r.Repo.GetAsync(c.Code, default))!.Status);
    }

    [Fact]
    public async Task RulesForCreatorAndAcceptor()
    {
        var r = new Rig();
        await Assert.ThrowsAsync<MatchAccessException>(() => r.Service.CreateAsync(new TttUser(Guid.NewGuid(), AccountKind.Guest, "g"),
            new CreateChallengeDto(TimeControl.CasualX2, null), default));
        var ranked = await Assert.ThrowsAsync<MatchAccessException>(() => r.Service.CreateAsync(Creator, new CreateChallengeDto(TimeControl.Ranked, null), default));
        Assert.Equal(ErrorCodes.Validation, ranked.Code);
        var c = await r.Service.CreateAsync(Creator, new CreateChallengeDto(TimeControl.CasualX4, null), default);
        Assert.Equal(ErrorCodes.CannotChallengeSelf, (await r.Service.AcceptAsync(Creator, c.Code, default)).Error);
        Assert.Equal(ErrorCodes.ChallengeNotFound, (await r.Service.AcceptAsync(Creator, "ZZZZZZZZ", default)).Error);
        Assert.True((await r.Service.CancelAsync(Creator, c.Code, default)).Ok);
        Assert.Equal(ErrorCodes.ChallengeTaken, (await r.Service.AcceptAsync(new TttUser(Guid.NewGuid(), AccountKind.Account, "x"), c.Code, default)).Error);
    }

    [Fact]
    public async Task TargetedChallengePushesToTarget()
    {
        var r = new Rig();
        var target = Guid.NewGuid();
        var c = await r.Service.CreateAsync(Creator, new CreateChallengeDto(TimeControl.CasualX2, target), default);
        Assert.Single(r.Notifier.Of<ChallengeDto>(target));
        Assert.Equal(ErrorCodes.NotAParticipant, (await r.Service.AcceptAsync(new TttUser(Guid.NewGuid(), AccountKind.Account, "other"), c.Code, default)).Error);
        Assert.Single(await r.Service.PendingForAsync(target, default));
        Assert.True((await r.Service.DeclineAsync(new TttUser(target, AccountKind.Account, "t"), c.Code, default)).Ok);
    }
}
