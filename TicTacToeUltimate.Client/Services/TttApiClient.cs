using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Services;

/// <summary>REST failure with HTTP status and the ProblemDetails <c>code</c> extension.</summary>
public sealed class TttApiException(HttpStatusCode status, string? code)
    : Exception($"API error {(int)status}: {code}")
{
    /// <summary>HTTP status.</summary>
    public HttpStatusCode Status { get; } = status;
    /// <summary>Error code from <see cref="ErrorCodes"/>, if any.</summary>
    public string? Code { get; } = code;
}

/// <summary>Typed REST client for §6.7. Raises <see cref="Unauthorized"/> on any 401.</summary>
public sealed class TttApiClient(HttpClient http, TokenStore tokens)
{
    /// <summary>Raised after a 401 response.</summary>
    public event Action? Unauthorized;

    private static readonly JsonSerializerOptions Json = ClientJson.Options;

    /// <summary>GET /api/config.</summary>
    public Task<AppConfigDto> GetConfigAsync(CancellationToken ct = default) => Get<AppConfigDto>("api/config", ct)!;
    /// <summary>GET /api/auth/dev/users.</summary>
    public async Task<IReadOnlyList<DevUserDto>> GetDevUsersAsync() => await Get<DevUserDto[]>("api/auth/dev/users") ?? [];
    /// <summary>POST /api/auth/dev/token.</summary>
    public Task<TokenResponseDto> DevTokenAsync(DevTokenRequestDto request) => Send<TokenResponseDto>(HttpMethod.Post, "api/auth/dev/token", request)!;
    /// <summary>POST /api/auth/guest.</summary>
    public Task<TokenResponseDto> GuestTokenAsync(GuestTokenRequestDto request) => Send<TokenResponseDto>(HttpMethod.Post, "api/auth/guest", request)!;
    /// <summary>POST /api/auth/claim-guest.</summary>
    public Task<ClaimGuestResultDto?> ClaimGuestAsync(ClaimGuestDto request) => Send<ClaimGuestResultDto>(HttpMethod.Post, "api/auth/claim-guest", request);
    /// <summary>GET /api/me.</summary>
    public Task<MeDto> GetMeAsync() => Get<MeDto>("api/me")!;
    /// <summary>PUT /api/me/username.</summary>
    public Task<MeDto> SetUsernameAsync(SetUsernameDto request) => Send<MeDto>(HttpMethod.Put, "api/me/username", request)!;
    public Task DeleteAccountAsync() => Send<object>(HttpMethod.Delete, "api/me", null);
    public Task RegisterPushAsync(RegisterPushDto request) => Send<object>(HttpMethod.Post, "api/push/register", request);
    /// <summary>PATCH /api/me.</summary>
    public Task<MeDto> UpdateProfileAsync(UpdateProfileDto request) => Send<MeDto>(HttpMethod.Patch, "api/me", request)!;
    /// <summary>GET /api/players/{username}; null on 404.</summary>
    public Task<ProfileDto?> GetProfileAsync(string username) => GetOrNull<ProfileDto>($"api/players/{Uri.EscapeDataString(username)}");
    /// <summary>GET /api/players/search.</summary>
    public async Task<IReadOnlyList<PlayerCardDto>> SearchAsync(string prefix)
        => await Get<PlayerCardDto[]>($"api/players/search?q={Uri.EscapeDataString(prefix)}") ?? [];
    /// <summary>GET /api/players/{userId}/games.</summary>
    public async Task<IReadOnlyList<GameSummaryDto>> GetHistoryAsync(Guid userId, DateTimeOffset? before, int take)
    {
        var q = $"api/players/{userId}/games?take={take}";
        if (before is { } b) q += $"&before={Uri.EscapeDataString(b.ToString("O"))}";
        return await Get<GameSummaryDto[]>(q) ?? [];
    }
    /// <summary>GET /api/games/{id}; null on 404.</summary>
    public Task<GameRecordDto?> GetGameAsync(Guid id) => GetOrNull<GameRecordDto>($"api/games/{id}");
    /// <summary>GET /api/games/{id}/analysis.</summary>
    public Task<GameAnalysisDto> GetAnalysisAsync(Guid id) => Get<GameAnalysisDto>($"api/games/{id}/analysis")!;
    /// <summary>GET /api/leaderboards/{pool}.</summary>
    public Task<LeaderboardPageDto> GetLeaderboardAsync(RatingPool pool, LeaderboardScope scope, string? country, int page)
    {
        var q = $"api/leaderboards/{pool}?scope={scope}&page={page}";
        if (!string.IsNullOrEmpty(country)) q += $"&country={Uri.EscapeDataString(country)}";
        return Get<LeaderboardPageDto>(q)!;
    }
    /// <summary>GET /api/friends.</summary>
    public Task<FriendsOverviewDto> GetFriendsAsync() => Get<FriendsOverviewDto>("api/friends")!;
    /// <summary>POST /api/friends/requests.</summary>
    public Task<CommandAckDto> SendFriendRequestAsync(string? username, Guid? userId = null)
        => Send<CommandAckDto>(HttpMethod.Post, "api/friends/requests", new SendFriendRequestDto(username, userId))!;
    /// <summary>POST /api/friends/requests/{id}/accept|decline.</summary>
    public Task<CommandAckDto> RespondFriendRequestAsync(Guid id, bool accept)
        => Send<CommandAckDto>(HttpMethod.Post, $"api/friends/requests/{id}/{(accept ? "accept" : "decline")}", null)!;
    /// <summary>DELETE /api/friends/{userId}.</summary>
    public Task RemoveFriendAsync(Guid userId) => Send<object>(HttpMethod.Delete, $"api/friends/{userId}", null);
    /// <summary>POST /api/blocks/{userId}.</summary>
    public Task BlockAsync(Guid userId) => Send<object>(HttpMethod.Post, $"api/blocks/{userId}", null);
    /// <summary>DELETE /api/blocks/{userId}.</summary>
    public Task UnblockAsync(Guid userId) => Send<object>(HttpMethod.Delete, $"api/blocks/{userId}", null);
    /// <summary>GET /api/chat/{friendId}.</summary>
    public async Task<IReadOnlyList<DirectMessageDto>> GetChatAsync(Guid friendId, long? before, int take = 50)
    {
        var q = $"api/chat/{friendId}?take={take}";
        if (before is { } b) q += $"&before={b}";
        return await Get<DirectMessageDto[]>(q) ?? [];
    }
    /// <summary>GET /api/progress.</summary>
    public Task<ProgressDto> GetProgressAsync() => Get<ProgressDto>("api/progress")!;
    /// <summary>POST /api/rewards/claim.</summary>
    public Task<WalletDto> GetWalletAsync() => Get<WalletDto>("api/wallet")!;
    public Task<StoreDto> GetStoreAsync() => Get<StoreDto>("api/store")!;
    public Task<PurchaseResultDto> BuyAsync(PurchaseDto request) => Send<PurchaseResultDto>(HttpMethod.Post, "api/store/buy", request)!;
    public Task<RewardNonceDto> RequestRewardNonceAsync(RewardNonceRequestDto request) => Send<RewardNonceDto>(HttpMethod.Post, "api/rewards/nonce", request)!;
    public Task<RewardClaimResultDto> ClaimRewardAsync(RewardClaimDto claim) => Send<RewardClaimResultDto>(HttpMethod.Post, "api/rewards/claim", claim)!;
    /// <summary>GET /api/challenges/{code}; null on 404.</summary>
    public Task<ChallengeDto?> GetChallengeAsync(string code) => GetOrNull<ChallengeDto>($"api/challenges/{Uri.EscapeDataString(code)}");
    /// <summary>GET /api/daily; empty when the endpoint is missing.</summary>
    public async Task<IReadOnlyList<DailyGameDto>> GetDailyGamesAsync() => await GetOrNull<DailyGameDto[]>("api/daily") ?? [];
    /// <summary>GET /api/me/matches: the caller's unfinished games (live + Daily).</summary>
    public async Task<IReadOnlyList<ActiveMatchDto>> GetActiveMatchesAsync() => await GetOrNull<ActiveMatchDto[]>("api/me/matches") ?? [];
    /// <summary>GET /api/live?friends=true (§6.5); empty when the endpoint is missing.</summary>
    public async Task<IReadOnlyList<LiveMatchDto>> GetLiveFriendMatchesAsync() => await GetOrNull<LiveMatchDto[]>("api/live?friends=true") ?? [];

    private Task<T?> Get<T>(string path, CancellationToken ct = default) => Send<T>(HttpMethod.Get, path, null, ct);

    private async Task<T?> GetOrNull<T>(string path)
    {
        try { return await Get<T>(path); }
        catch (TttApiException e) when (e.Status == HttpStatusCode.NotFound) { return default; }
    }

    private async Task<T?> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, path);
        if (tokens.Token is { Length: > 0 } token) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Unauthorized) Unauthorized?.Invoke();
        if (!res.IsSuccessStatusCode) throw new TttApiException(res.StatusCode, await ReadCode(res, ct).ConfigureAwait(false));
        if (typeof(T) == typeof(object) || res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0) return default;
        return await res.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReadCode(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return DefaultCode(res.StatusCode);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                return c.GetString();
        }
        catch (JsonException) { }
        return DefaultCode(res.StatusCode);
    }

    private static string DefaultCode(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => "not_found",
        HttpStatusCode.Unauthorized => "unauthorized",
        HttpStatusCode.Forbidden => "forbidden",
        _ => ErrorCodes.Internal,
    };
}
