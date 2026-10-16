using System.Collections.Concurrent;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Ratings;

namespace TicTacToeUltimate.Server.Social;

/// <summary>IPlayerCardService backed by users + ratings. Rating/tier are shown only for visible pools.</summary>
public sealed class PlayerCardService(IUserRepository users, IRatingRepository ratings, RatingService ratingService, TierCalculator tiers) : IPlayerCardService
{
    public async Task<PlayerCardDto> GetCardAsync(Guid userId, RatingPool pool, CancellationToken ct)
        => (await GetCardsAsync([userId], pool, ct))[userId];

    public async Task<IReadOnlyDictionary<Guid, PlayerCardDto>> GetCardsAsync(IReadOnlyCollection<Guid> userIds, RatingPool pool, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        var map = await users.GetManyAsync(ids, ct);
        var result = new Dictionary<Guid, PlayerCardDto>();
        var visible = RatingPools.IsVisible(pool);
        foreach (var id in ids)
        {
            if (!map.TryGetValue(id, out var u)) { result[id] = new PlayerCardDto(id, "Player", "a1", null, AccountKind.Account, null, null, false); continue; }
            TierDto? tier = null; int? rating = null; var provisional = false;
            if (visible && u.Kind != AccountKind.Guest)
            {
                var r = await ratings.GetAsync(id, pool, ct);
                rating = (int)Math.Round(r.Rating);
                provisional = ratingService.IsProvisional(r);
                tier = tiers.For(r.Rating);
            }
            result[id] = new PlayerCardDto(id, u.DisplayName, u.AvatarId, u.Country, u.Kind, tier, rating, provisional);
        }
        return result;
    }
}

/// <summary>Optional presence lookup: WP2's IPresenceTracker when registered, else Offline.</summary>
public sealed class PresenceLookup(IServiceProvider services)
{
    /// <summary>Presence for a user.</summary>
    public PresenceDto Get(Guid userId) =>
        services.GetService<IPresenceTracker>()?.Get(userId) ?? new PresenceDto(userId, PresenceStatus.Offline, null);
}

/// <summary>IPresenceSink: fans PresenceChanged out to the user's friends (fire and forget).</summary>
public sealed class PresenceSink(IFriendRepository friends, IUserNotifier notifier, ILogger<PresenceSink> logger) : IPresenceSink
{
    public ValueTask OnPresenceChangedAsync(PresenceDto presence, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var list = await friends.ListFriendsAsync(presence.UserId, CancellationToken.None);
                if (list.Count > 0) await notifier.ToUsersAsync(list, c => c.PresenceChanged(presence));
            }
            catch (Exception e) { logger.LogWarning(e, "Presence fan-out failed"); }
        }, CancellationToken.None);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Per-user token bucket (rate 1/s, burst 5 by default).</summary>
public sealed class ChatRateLimiter(TimeProvider time, double perSecond = 1, int burst = 5)
{
    private readonly ConcurrentDictionary<Guid, (double Tokens, long At)> _buckets = new();

    /// <summary>Takes one token; false when limited.</summary>
    public bool TryTake(Guid userId)
    {
        var now = time.GetTimestamp();
        while (true)
        {
            var had = _buckets.TryGetValue(userId, out var b);
            var tokens = had ? Math.Min(burst, b.Tokens + time.GetElapsedTime(b.At, now).TotalSeconds * perSecond) : burst;
            if (tokens < 1) return false;
            var next = (tokens - 1, now);
            if (had ? _buckets.TryUpdate(userId, next, b) : _buckets.TryAdd(userId, next)) return true;
        }
    }
}

/// <summary>Friends, blocks and DMs (§6.7 Social).</summary>
public sealed class SocialService(
    IUserRepository users, IFriendRepository friends, IChatRepository chat, IPlayerCardService cards, PresenceLookup presence,
    IUserNotifier notifier, ProfanityFilter profanity, ChatRateLimiter limiter, TimeProvider time, ILogger<SocialService> logger,
    TicTacToeUltimate.Server.Moderation.ModerationService? moderation = null, TicTacToeUltimate.Server.Moderation.ChatFilter? filter = null,
    TicTacToeUltimate.Server.Moderation.AiModerator? ai = null)
{
    /// <summary>Max DM length.</summary>
    public const int MaxBody = 500;

    /// <summary>Friends overview for the friends page.</summary>
    public async Task<FriendsOverviewDto> OverviewAsync(Guid me, CancellationToken ct)
    {
        var friendIds = await friends.ListFriendsAsync(me, ct);
        var requests = await friends.ListRequestsAsync(me, ct);
        var blocked = await friends.ListBlockedAsync(me, ct);
        var all = friendIds.Concat(requests.SelectMany(r => new[] { r.From, r.To })).Concat(blocked).Append(me).Distinct().ToList();
        var cardMap = await cards.GetCardsAsync(all, RatingPools.Headline, ct);
        var userMap = await users.GetManyAsync(friendIds.ToList(), ct);
        var unread = await chat.UnreadCountsAsync(me, ct);
        var friendDtos = new List<FriendDto>();
        foreach (var f in friendIds)
        {
            var last = (await chat.PageAsync(me, f, null, 1, ct)).FirstOrDefault();
            friendDtos.Add(new FriendDto(cardMap[f], presence.Get(f), unread.GetValueOrDefault(f), userMap.TryGetValue(f, out var u) ? u.LastSeenAt : null)
            { LastMessage = last is null ? null : new DirectMessageDto(last.Id, last.From, last.To, last.Body, last.SentAt, last.ReadAt) });
        }
        friendDtos = friendDtos.OrderByDescending(f => f.Presence.Status).ThenBy(f => f.Player.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var reqDtos = requests.Select(r => new FriendRequestDto(r.Id, cardMap[r.From], cardMap[r.To],
            r.To == me ? FriendRequestDirection.Incoming : FriendRequestDirection.Outgoing, r.CreatedAt)).ToArray();
        return new FriendsOverviewDto(friendDtos.ToArray(), reqDtos, blocked.Select(b => cardMap[b]).ToArray());
    }

    /// <summary>Sends a friend request by username.</summary>
    public Task<CommandAckDto> RequestAsync(Guid me, string username, CancellationToken ct) => RequestAsync(me, username, null, ct);

    /// <summary>Sends a friend request to a username or a user id.</summary>
    public async Task<CommandAckDto> RequestAsync(Guid me, string? username, Guid? userId, CancellationToken ct)
    {
        var target = userId is { } id ? await users.GetAsync(id, ct) : await users.GetByUsernameAsync((username ?? "").Trim(), ct);
        if (target is null || target.Kind != AccountKind.Account) return new(false, ErrorCodes.Validation);
        if (target.Id == me) return new(false, ErrorCodes.Validation);
        var result = await friends.RequestAsync(me, target.Id, time.GetUtcNow(), ct);
        switch (result)
        {
            case FriendRequestResult.Blocked: return new(false, ErrorCodes.Blocked);
            case FriendRequestResult.NotFound: return new(false, ErrorCodes.Validation);
            case FriendRequestResult.AutoAccepted:
                await Safe(() => notifier.ToUsersAsync([me, target.Id], c => c.FriendsChanged()));
                break;
            case FriendRequestResult.Created:
                var req = (await friends.ListRequestsAsync(target.Id, ct)).LastOrDefault(r => r.From == me && r.To == target.Id);
                if (req is not null)
                {
                    var cardMap = await cards.GetCardsAsync([me, target.Id], RatingPools.Headline, ct);
                    var dto = new FriendRequestDto(req.Id, cardMap[me], cardMap[target.Id], FriendRequestDirection.Incoming, req.CreatedAt);
                    await Safe(() => notifier.ToUserAsync(target.Id, c => c.FriendRequestReceived(dto)));
                }
                break;
        }
        return new(true, null);
    }

    /// <summary>Accepts or declines an incoming request.</summary>
    public async Task<CommandAckDto> RespondAsync(Guid me, Guid requestId, bool accept, CancellationToken ct)
    {
        var req = (await friends.ListRequestsAsync(me, ct)).FirstOrDefault(r => r.Id == requestId && r.To == me);
        if (req is null || !await friends.RespondAsync(requestId, me, accept, ct)) return new(false, ErrorCodes.Validation);
        await Safe(() => notifier.ToUsersAsync([me, req.From], c => c.FriendsChanged()));
        return new(true, null);
    }

    /// <summary>Removes a friendship.</summary>
    public async Task RemoveAsync(Guid me, Guid other, CancellationToken ct)
    {
        await friends.RemoveAsync(me, other, ct);
        await Safe(() => notifier.ToUsersAsync([me, other], c => c.FriendsChanged()));
    }

    /// <summary>Blocks (and unfriends) a user.</summary>
    public async Task BlockAsync(Guid me, Guid other, CancellationToken ct)
    {
        await friends.BlockAsync(me, other, ct);
        await Safe(() => notifier.ToUsersAsync([me, other], c => c.FriendsChanged()));
    }

    /// <summary>Unblocks a user.</summary>
    public async Task UnblockAsync(Guid me, Guid other, CancellationToken ct)
    {
        await friends.UnblockAsync(me, other, ct);
        await Safe(() => notifier.ToUserAsync(me, c => c.FriendsChanged()));
    }

    /// <summary>Sends a DM. Returns (message, null) or (null, error code).</summary>
    public async Task<(DirectMessageDto? Message, string? Error)> SendAsync(Guid me, Guid to, string? body, CancellationToken ct)
    {
        var text = (body ?? "").Trim();
        if (text.Length is 0 or > MaxBody) return (null, ErrorCodes.Validation);
        // Game emotes in chat: "emote:<EmoteCatalog id>"; anything else with that prefix is refused.
        if (text.StartsWith("emote:", StringComparison.Ordinal) && !EmoteCatalog.IsKnown(text["emote:".Length..])) return (null, ErrorCodes.EmoteUnknown);
        if (await friends.IsBlockedEitherWayAsync(me, to, ct)) return (null, ErrorCodes.Blocked);
        if (!await friends.AreFriendsAsync(me, to, ct)) return (null, ErrorCodes.NotFriends);
        if (!limiter.TryTake(me)) return (null, ErrorCodes.RateLimited);
        if (moderation is not null && await moderation.ChatMuteAsync(me, ct) is not null) return (null, ErrorCodes.ChatMuted);
        // Auto-moderation (owner decision 2026-10-16): abusive words are never delivered and cost a strike; links, emails and
        // phone numbers are refused without one; anything doubtful is delivered, then checked by the AI and hidden if abusive.
        var check = text.StartsWith("emote:", StringComparison.Ordinal) || filter is null
            ? new TicTacToeUltimate.Server.Moderation.FilterResult(TicTacToeUltimate.Server.Moderation.FilterVerdict.Allow)
            : filter.Check(text);
        if (check.Verdict == TicTacToeUltimate.Server.Moderation.FilterVerdict.Block)
        {
            if (moderation is not null) await moderation.StrikeAsync(me, "filter_block", text, new { check.Term, check.Reason, to }, ct);
            return (null, ErrorCodes.MessageBlocked);
        }
        if (check.Verdict == TicTacToeUltimate.Server.Moderation.FilterVerdict.Reject) return (null, ErrorCodes.MessageBlocked);
        var rec = await chat.AppendAsync(me, to, filter is null ? profanity.Mask(text) : text, time.GetUtcNow(), ct);
        var dto = ToDto(rec);
        await Safe(() => notifier.ToUsersAsync([to, me], c => c.DirectMessage(dto)));
        if (check.Verdict == TicTacToeUltimate.Server.Moderation.FilterVerdict.Uncertain && ai is { Enabled: true }) _ = ReviewLaterAsync(rec, check);
        return (dto, null);
    }

    /// <summary>The AI looks at a delivered, doubtful message; if abusive it disappears for both sides and the sender gets a strike.</summary>
    private async Task ReviewLaterAsync(DirectMessageRecord rec, TicTacToeUltimate.Server.Moderation.FilterResult check)
    {
        try
        {
            var verdict = await ai!.CheckAsync(rec.Body, isUsername: false, CancellationToken.None);
            if (verdict is not { Abusive: true, Severity: >= 2 }) return;
            await chat.HideAsync(rec.Id, time.GetUtcNow(), CancellationToken.None);
            var hiddenForSender = new MessageHiddenDto(rec.Id, rec.To);
            var hiddenForRecipient = new MessageHiddenDto(rec.Id, rec.From);
            await Safe(() => notifier.ToUserAsync(rec.From, c => c.MessageHidden(hiddenForSender)));
            await Safe(() => notifier.ToUserAsync(rec.To, c => c.MessageHidden(hiddenForRecipient)));
            if (moderation is not null)
                await moderation.StrikeAsync(rec.From, "ai_hide", rec.Body, new { check.Term, verdict.Category, verdict.Severity, verdict.Reason, messageId = rec.Id }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "AI review of message {Id} failed", rec.Id); }
    }

    /// <summary>Marks messages from <paramref name="friendId"/> read.</summary>
    public Task MarkReadAsync(Guid me, Guid friendId, long upToId, CancellationToken ct) => chat.MarkReadAsync(me, friendId, upToId, time.GetUtcNow(), ct);

    /// <summary>History page (newest first).</summary>
    public async Task<(DirectMessageDto[]? Page, string? Error)> HistoryAsync(Guid me, Guid friendId, long? before, int take, CancellationToken ct)
    {
        if (!await friends.AreFriendsAsync(me, friendId, ct)) return (null, ErrorCodes.NotFriends);
        var page = await chat.PageAsync(me, friendId, before, Math.Clamp(take, 1, 100), ct);
        return (page.Select(ToDto).ToArray(), null);
    }

    /// <summary>Friend status of <paramref name="other"/> relative to <paramref name="me"/>.</summary>
    public async Task<FriendStatus> StatusAsync(Guid me, Guid other, CancellationToken ct)
    {
        if (me == other) return FriendStatus.Self;
        if ((await friends.ListBlockedAsync(me, ct)).Contains(other)) return FriendStatus.Blocked;
        if (await friends.AreFriendsAsync(me, other, ct)) return FriendStatus.Friends;
        var reqs = await friends.ListRequestsAsync(me, ct);
        if (reqs.Any(r => r.From == me && r.To == other)) return FriendStatus.RequestSent;
        if (reqs.Any(r => r.From == other && r.To == me)) return FriendStatus.RequestReceived;
        return FriendStatus.None;
    }

    private static DirectMessageDto ToDto(DirectMessageRecord r) => new(r.Id, r.From, r.To, r.Body, r.SentAt, r.ReadAt);

    private async Task Safe(Func<Task> send)
    {
        try { await send(); }
        catch (Exception e) { logger.LogWarning(e, "Notify failed"); }
    }
}
