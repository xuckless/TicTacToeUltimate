using System.Collections.Concurrent;
using System.Security.Cryptography;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;

namespace TicTacToeUltimate.Server.Challenges;

/// <summary>Friend challenges by link/room code (§6.6).</summary>
public sealed class ChallengeService(
    IChallengeRepository repo, IMatchGateway gateway, UserActivityRegistry activity, IUserNotifier notifier,
    IPlayerCardService cards, TimeProvider time, TttOptions options, ILogger<ChallengeService> log,
    IUserRepository? users = null, IFriendRepository? friends = null, IDailyMatchRepository? daily = null) : IChallengeService
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly TimeControl[] Allowed = [TimeControl.Casual, TimeControl.OldSchool, TimeControl.ArenaCasual, TimeControl.Unlimited, TimeControl.Daily,
        TimeControl.CasualX2, TimeControl.CasualX4];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>New 8-char Crockford base32 code.</summary>
    public static string NewCode()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < 8; i++) chars[i] = Alphabet[bytes[i] & 31];
        return new string(chars);
    }

    /// <summary>Normalizes user input (case, Crockford aliases).</summary>
    public static string Normalize(string code) => code.Trim().ToUpperInvariant().Replace('O', '0').Replace('I', '1').Replace('L', '1');

    /// <inheritdoc />
    public async Task<ChallengeDto> CreateAsync(TttUser creator, CreateChallengeDto request, CancellationToken ct)
    {
        // Guests may create open (untargeted) casual invite links; friend-targeted and Daily challenges need an account.
        var isGuest = creator.Kind != AccountKind.Account;
        if (isGuest && (request.ToUserId is not null || request.TimeControl == TimeControl.Daily))
            throw new MatchAccessException(ErrorCodes.GuestNotAllowed);
        if (!Allowed.Contains(request.TimeControl)) throw new MatchAccessException(ErrorCodes.Validation);
        if (!isGuest && users is not null && (await users.GetAsync(creator.UserId, ct).ConfigureAwait(false))?.Username is null)
            throw new MatchAccessException(ErrorCodes.UsernameRequired);
        if (request.ToUserId is { } target)
        {
            if (target == creator.UserId) throw new MatchAccessException(ErrorCodes.CannotChallengeSelf);
            if (friends is not null)
            {
                if (await friends.IsBlockedEitherWayAsync(creator.UserId, target, ct).ConfigureAwait(false)) throw new MatchAccessException(ErrorCodes.Blocked);
                if (!await friends.AreFriendsAsync(creator.UserId, target, ct).ConfigureAwait(false)) throw new MatchAccessException(ErrorCodes.NotFriends);
            }
        }
        var now = time.GetUtcNow();
        ChallengeRecord? rec = null;
        for (var i = 0; i < 5 && rec is null; i++)
        {
            var candidate = new ChallengeRecord(NewCode(), creator.UserId, request.ToUserId, request.TimeControl,
                MatchModes.RulesFor(options, request.TimeControl) with { Opening = OpeningRule.None } is var rs ? rs.Id : RuleSet.Default.Id,
                ChallengeStatus.Open, null, null, now, now.AddHours(options.Challenges.TtlHours));
            if (await repo.CreateAsync(candidate, ct).ConfigureAwait(false)) rec = candidate;
        }
        if (rec is null) throw new MatchAccessException(ErrorCodes.Internal);
        _open[rec.Code] = rec.ExpiresAt;
        var dto = await ToDtoAsync(rec, ct).ConfigureAwait(false);
        if (rec.TargetUserId is { } t) await SafeAsync(() => notifier.ToUserAsync(t, c => c.ChallengeReceived(dto))).ConfigureAwait(false);
        return dto;
    }

    /// <inheritdoc />
    public async Task<ChallengeDto?> GetAsync(string code, CancellationToken ct)
    {
        var rec = await LoadAsync(Normalize(code), ct).ConfigureAwait(false);
        return rec is null ? null : await ToDtoAsync(rec, ct).ConfigureAwait(false);
    }

    /// <summary>Loads with lazy expiry.</summary>
    private async Task<ChallengeRecord?> LoadAsync(string code, CancellationToken ct)
    {
        var rec = await repo.GetAsync(code, ct).ConfigureAwait(false);
        if (rec is null) return null;
        if (rec.Status == ChallengeStatus.Open && time.GetUtcNow() >= rec.ExpiresAt)
        {
            if (await repo.TryTransitionAsync(code, ChallengeStatus.Open, ChallengeStatus.Expired, null, null, ct).ConfigureAwait(false))
            {
                _open.TryRemove(code, out _);
                rec = rec with { Status = ChallengeStatus.Expired };
                var dto = await ToDtoAsync(rec, ct).ConfigureAwait(false);
                await SafeAsync(() => notifier.ToUsersAsync(Parties(rec), c => c.ChallengeUpdated(dto))).ConfigureAwait(false);
            }
            else rec = await repo.GetAsync(code, ct).ConfigureAwait(false);
        }
        return rec;
    }

    /// <inheritdoc />
    public async Task<AcceptChallengeResultDto> AcceptAsync(TttUser user, string code, CancellationToken ct)
    {
        code = Normalize(code);
        var rec = await LoadAsync(code, ct).ConfigureAwait(false);
        if (rec is null) return Fail(ErrorCodes.ChallengeNotFound);
        if (rec.Status == ChallengeStatus.Expired) return Fail(ErrorCodes.ChallengeExpired);
        if (rec.Status != ChallengeStatus.Open) return Fail(ErrorCodes.ChallengeTaken);
        if (rec.CreatorId == user.UserId) return Fail(ErrorCodes.CannotChallengeSelf);
        if (rec.TargetUserId is { } t && t != user.UserId) return Fail(ErrorCodes.NotAParticipant);
        if (friends is not null && await friends.IsBlockedEitherWayAsync(rec.CreatorId, user.UserId, ct).ConfigureAwait(false)) return Fail(ErrorCodes.Blocked);

        var matchId = Guid.NewGuid();
        var isDaily = rec.TimeControl == TimeControl.Daily;
        if (isDaily)
        {
            if (user.IsGuest) return Fail(ErrorCodes.GuestNotAllowed);
            if (await DailyCountAsync(user.UserId, ct).ConfigureAwait(false) >= options.Daily.MaxConcurrent
                || await DailyCountAsync(rec.CreatorId, ct).ConfigureAwait(false) >= options.Daily.MaxConcurrent)
                return Fail(ErrorCodes.DailyLimit);
        }

        // CAS first: the first acceptor wins regardless of activity races.
        if (!await repo.TryTransitionAsync(code, ChallengeStatus.Open, ChallengeStatus.Accepted, user.UserId, matchId, ct).ConfigureAwait(false))
            return Fail(ErrorCodes.ChallengeTaken);
        if (!isDaily)
        {
            var okUser = activity.TryStartMatch(user.UserId, matchId);
            var okCreator = okUser && activity.TryStartMatch(rec.CreatorId, matchId);
            if (!okCreator)
            {
                if (okUser) activity.EndMatch(user.UserId, matchId);
                await repo.TryTransitionAsync(code, ChallengeStatus.Accepted, ChallengeStatus.Open, null, null, ct).ConfigureAwait(false);
                return Fail(ErrorCodes.AlreadyInMatch);
            }
        }
        _open.TryRemove(code, out _);

        var creatorCard = await cards.GetCardAsync(rec.CreatorId, RatingPools.For(rec.TimeControl), ct).ConfigureAwait(false);
        var seed = MatchModes.NewSeed();
        var creatorSeat = new SeatSetup(rec.CreatorId, creatorCard.DisplayName, creatorCard.Kind);
        var acceptorSeat = new SeatSetup(user.UserId, user.DisplayName, user.Kind);
        var creatorFirst = (new SplitMix64(seed).Next() & 1) == 0;
        var setup = new MatchSetup(matchId, MatchMode.Challenge, rec.TimeControl, RuleIds.Parse(rec.RulesId, RuleSet.Default), false,
            creatorFirst ? creatorSeat : acceptorSeat, creatorFirst ? acceptorSeat : creatorSeat, seed, MatchOrigin.Challenge, null, rec.Code);
        try
        {
            await gateway.CreateMatchAsync(setup, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Challenge {Code}: match creation failed", code);
            Release(rec, user, matchId, isDaily);
            return Fail(ErrorCodes.Internal);
        }

        var accepted = rec with { Status = ChallengeStatus.Accepted, AcceptedBy = user.UserId, MatchId = matchId };
        var dto = await ToDtoAsync(accepted, ct).ConfigureAwait(false);
        await SafeAsync(() => notifier.ToUserAsync(rec.CreatorId, c => c.ChallengeUpdated(dto))).ConfigureAwait(false);
        var acceptorCard = await cards.GetCardAsync(user.UserId, RatingPools.For(rec.TimeControl), ct).ConfigureAwait(false);
        var creatorSeatNo = creatorFirst ? 0 : 1;
        var toCreator = new MatchFoundDto(matchId, MatchMode.Challenge, rec.TimeControl, acceptorCard, creatorSeatNo == 0 ? Color.X : Color.O, creatorSeatNo);
        var toAcceptor = new MatchFoundDto(matchId, MatchMode.Challenge, rec.TimeControl, creatorCard, creatorSeatNo == 1 ? Color.X : Color.O, 1 - creatorSeatNo);
        await SafeAsync(() => notifier.ToUserAsync(rec.CreatorId, c => c.MatchFound(toCreator))).ConfigureAwait(false);
        await SafeAsync(() => notifier.ToUserAsync(user.UserId, c => c.MatchFound(toAcceptor))).ConfigureAwait(false);
        return new AcceptChallengeResultDto(true, null, matchId);
    }

    private void Release(ChallengeRecord rec, TttUser user, Guid matchId, bool isDaily)
    {
        if (isDaily) return;
        activity.EndMatch(user.UserId, matchId);
        activity.EndMatch(rec.CreatorId, matchId);
    }

    private async Task<int> DailyCountAsync(Guid userId, CancellationToken ct)
        => daily is not null ? (await daily.ListActiveForUserAsync(userId, ct).ConfigureAwait(false)).Count : activity.DailyCount(userId);

    /// <inheritdoc />
    public async Task<CommandAckDto> DeclineAsync(TttUser user, string code, CancellationToken ct)
    {
        code = Normalize(code);
        var rec = await LoadAsync(code, ct).ConfigureAwait(false);
        if (rec is null) return new CommandAckDto(false, ErrorCodes.ChallengeNotFound);
        if (rec.TargetUserId != user.UserId) return new CommandAckDto(false, ErrorCodes.NotAParticipant);
        return await TransitionAsync(rec, ChallengeStatus.Declined, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CommandAckDto> CancelAsync(TttUser user, string code, CancellationToken ct)
    {
        code = Normalize(code);
        var rec = await LoadAsync(code, ct).ConfigureAwait(false);
        if (rec is null) return new CommandAckDto(false, ErrorCodes.ChallengeNotFound);
        if (rec.CreatorId != user.UserId) return new CommandAckDto(false, ErrorCodes.NotAParticipant);
        return await TransitionAsync(rec, ChallengeStatus.Cancelled, ct).ConfigureAwait(false);
    }

    private async Task<CommandAckDto> TransitionAsync(ChallengeRecord rec, ChallengeStatus next, CancellationToken ct)
    {
        if (rec.Status == ChallengeStatus.Expired) return new CommandAckDto(false, ErrorCodes.ChallengeExpired);
        if (rec.Status != ChallengeStatus.Open
            || !await repo.TryTransitionAsync(rec.Code, ChallengeStatus.Open, next, null, null, ct).ConfigureAwait(false))
            return new CommandAckDto(false, ErrorCodes.ChallengeTaken);
        _open.TryRemove(rec.Code, out _);
        var dto = await ToDtoAsync(rec with { Status = next }, ct).ConfigureAwait(false);
        await SafeAsync(() => notifier.ToUsersAsync(Parties(rec), c => c.ChallengeUpdated(dto))).ConfigureAwait(false);
        return new CommandAckDto(true, null);
    }

    /// <summary>Expires due open challenges created by this process (5-minute sweep).</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var (code, expires) in _open.ToArray())
            if (now >= expires) await LoadAsync(code, ct).ConfigureAwait(false);
    }

    /// <summary>Open challenges targeting a user (ServerHello).</summary>
    public async Task<ChallengeDto[]> PendingForAsync(Guid userId, CancellationToken ct)
    {
        var list = await repo.ListOpenForTargetAsync(userId, time.GetUtcNow(), ct).ConfigureAwait(false);
        var result = new List<ChallengeDto>(list.Count);
        foreach (var r in list) result.Add(await ToDtoAsync(r, ct).ConfigureAwait(false));
        return [.. result];
    }

    private static IEnumerable<Guid> Parties(ChallengeRecord r)
        => r.TargetUserId is { } t ? [r.CreatorId, t] : r.AcceptedBy is { } a ? [r.CreatorId, a] : [r.CreatorId];

    private async Task<ChallengeDto> ToDtoAsync(ChallengeRecord r, CancellationToken ct)
    {
        var from = await cards.GetCardAsync(r.CreatorId, RatingPools.For(r.TimeControl), ct).ConfigureAwait(false);
        var rules = RuleIds.Parse(r.RulesId, RuleSet.Default);
        return new ChallengeDto(r.Code, $"{options.Web.BaseUrl.TrimEnd('/')}/c/{r.Code}", from, r.TargetUserId, r.TimeControl,
            RuleSetDto.From(rules), r.Status, r.ExpiresAt, r.MatchId);
    }

    private static AcceptChallengeResultDto Fail(string code) => new(false, code, null);

    private async Task SafeAsync(Func<Task> f)
    {
        try { await f().ConfigureAwait(false); }
        catch (Exception ex) { log.LogWarning(ex, "Challenge notification failed"); }
    }
}

/// <summary>Runs <see cref="ChallengeService.SweepAsync"/> every 5 minutes.</summary>
public sealed class ChallengeSweeper(Func<ChallengeService?> service, TimeProvider time, ILogger<ChallengeSweeper> log) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { if (service() is { } svc) await svc.SweepAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Challenge sweep failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
