using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Stores;

/// <summary>Friends, requests, blocks, challenges and DM threads.</summary>
public sealed class SocialStore : StoreBase<SocialState>, ISocialStore
{
    private readonly TttApiClient _api;
    private readonly IRealtimeTransport _rt;
    private readonly ISessionStore _session;
    private readonly ILogger<SocialStore> _logger;

    /// <summary>Creates the store.</summary>
    public SocialStore(TttApiClient api, IRealtimeTransport rt, ISessionStore session, ILogger<SocialStore> logger) : base(SocialState.Empty)
    {
        _api = api; _rt = rt; _session = session; _logger = logger;
        rt.FriendsChanged += ReloadIfLoaded;
        rt.FriendRequestReceived += _ => ReloadIfLoaded();
        rt.PresenceChanged += p => Update(s => s with
        {
            Friends = [.. s.Friends.Select(f => f.Player.UserId == p.UserId ? f with { Presence = p } : f)],
        });
        rt.DirectMessageReceived += OnMessage;
        rt.ChallengeReceived += c => Update(s => s with { IncomingChallenges = [.. s.IncomingChallenges.Where(x => x.Code != c.Code), c] });
        rt.ChallengeUpdated += c => Update(s => s with
        {
            IncomingChallenges = c.Status == ChallengeStatus.Open
                ? [.. s.IncomingChallenges.Select(x => x.Code == c.Code ? c : x)]
                : [.. s.IncomingChallenges.Where(x => x.Code != c.Code)],
        });
        if (session is SessionStore concrete)
            concrete.HelloReceived += hello => Update(s => s with { IncomingChallenges = [.. hello.PendingChallenges.Where(c => c.Status == ChallengeStatus.Open)] });
    }

    private void ReloadIfLoaded()
    {
        if (State.Loaded) _ = LoadAsync();
    }

    private void OnMessage(DirectMessageDto m)
    {
        var me = _session.State.Me?.UserId;
        var other = m.From == me ? m.To : m.From;
        Update(s =>
        {
            var friends = m.From == me ? s.Friends
                : [.. s.Friends.Select(f => f.Player.UserId == other ? f with { UnreadMessages = f.UnreadMessages + 1 } : f)];
            return s with { Threads = Merge(s.Threads, other, [m]), Friends = friends };
        });
    }

    private static ImmutableDictionary<Guid, ImmutableArray<DirectMessageDto>> Merge(
        ImmutableDictionary<Guid, ImmutableArray<DirectMessageDto>> threads, Guid friendId, IEnumerable<DirectMessageDto> messages)
    {
        var existing = threads.TryGetValue(friendId, out var t) ? t : [];
        var merged = existing.Concat(messages).GroupBy(x => x.Id).Select(g => g.Last()).OrderBy(x => x.Id).ToImmutableArray();
        return threads.SetItem(friendId, merged);
    }

    /// <inheritdoc />
    public async Task LoadAsync()
    {
        try
        {
            var o = await _api.GetFriendsAsync();
            Update(s => s with { Friends = [.. o.Friends], Requests = [.. o.Requests], Blocked = [.. o.Blocked], Loaded = true, Error = null });
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Friends load failed");
            Update(s => s with { Error = ErrorText.Code(ex) });
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlayerCardDto>> SearchAsync(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return [];
        try { return await _api.SearchAsync(prefix.Trim()); }
        catch (Exception) { return []; }
    }

    /// <inheritdoc />
    public async Task<CommandAckDto> SendRequestAsync(string username)
    {
        var ack = await Ack(() => _api.SendFriendRequestAsync(username));
        await LoadAsync();
        return ack;
    }

    /// <inheritdoc />
    public async Task<CommandAckDto> RespondAsync(Guid requestId, bool accept)
    {
        var ack = await Ack(() => _api.RespondFriendRequestAsync(requestId, accept));
        await LoadAsync();
        return ack;
    }

    /// <inheritdoc />
    public async Task RemoveFriendAsync(Guid userId) { await Quiet(() => _api.RemoveFriendAsync(userId)); await LoadAsync(); }
    /// <inheritdoc />
    public async Task BlockAsync(Guid userId) { await Quiet(() => _api.BlockAsync(userId)); await LoadAsync(); }
    /// <inheritdoc />
    public async Task UnblockAsync(Guid userId) { await Quiet(() => _api.UnblockAsync(userId)); await LoadAsync(); }

    /// <inheritdoc />
    public async Task LoadThreadAsync(Guid friendId, long? beforeId = null)
    {
        try
        {
            var messages = await _api.GetChatAsync(friendId, beforeId);
            Update(s => s with { Threads = Merge(s.Threads, friendId, messages) });
        }
        catch (Exception ex) { Update(s => s with { Error = ErrorText.Code(ex) }); }
    }

    /// <inheritdoc />
    public async Task<CommandAckDto> SendMessageAsync(Guid friendId, string body)
    {
        try
        {
            var m = await _rt.SendDirectMessage(new SendDirectMessageDto(friendId, body));
            Update(s => s with { Threads = Merge(s.Threads, friendId, [m]) });
            return new CommandAckDto(true, null);
        }
        catch (Exception ex) { return new CommandAckDto(false, ErrorText.Code(ex)); }
    }

    /// <inheritdoc />
    public async Task MarkReadAsync(Guid friendId)
    {
        if (!State.Threads.TryGetValue(friendId, out var t) || t.IsEmpty) return;
        try { await _rt.MarkConversationRead(friendId, t[^1].Id); } catch (Exception) { return; }
        Update(s => s with { Friends = [.. s.Friends.Select(f => f.Player.UserId == friendId ? f with { UnreadMessages = 0 } : f)] });
    }

    /// <inheritdoc />
    public Task<ChallengeDto> CreateChallengeAsync(CreateChallengeDto request) => _rt.CreateChallenge(request);

    /// <inheritdoc />
    public async Task<ChallengeDto?> GetChallengeAsync(string code)
    {
        try { return await _api.GetChallengeAsync(code); }
        catch (Exception) { return State.IncomingChallenges.FirstOrDefault(c => c.Code == code); }
    }

    /// <inheritdoc />
    public async Task<AcceptChallengeResultDto> AcceptChallengeAsync(string code)
    {
        try
        {
            var r = await _rt.AcceptChallenge(code);
            if (r.Accepted) Update(s => s with { IncomingChallenges = [.. s.IncomingChallenges.Where(c => c.Code != code)] });
            return r;
        }
        catch (Exception ex) { return new AcceptChallengeResultDto(false, ErrorText.Code(ex), null); }
    }

    /// <inheritdoc />
    public async Task DeclineChallengeAsync(string code)
    {
        try { await _rt.DeclineChallenge(code); } catch (Exception) { /* best effort */ }
        Update(s => s with { IncomingChallenges = [.. s.IncomingChallenges.Where(c => c.Code != code)] });
    }

    private static async Task<CommandAckDto> Ack(Func<Task<CommandAckDto>> call)
    {
        try { return await call(); }
        catch (Exception ex) { return new CommandAckDto(false, ErrorText.Code(ex)); }
    }

    private async Task Quiet(Func<Task> call)
    {
        try { await call(); }
        catch (Exception ex) { Update(s => s with { Error = ErrorText.Code(ex) }); }
    }
}
