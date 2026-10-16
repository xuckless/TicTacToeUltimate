using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Auth;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Persistence;
using TicTacToeUltimate.Server.Progression;
using TicTacToeUltimate.Server.Ratings;
using TicTacToeUltimate.Server.Rewards;
using TicTacToeUltimate.Server.Social;

namespace TicTacToeUltimate.Server.Platform;

/// <summary>ProblemDetails with extension "code" = ErrorCodes value.</summary>
public static class ApiErrors
{
    /// <summary>Problem result.</summary>
    public static IResult Problem(int status, string code) =>
        Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code });
}

/// <summary>REST surface of WP3 (§6.7 table).</summary>
public static class PlatformEndpoints
{
    /// <summary>Builds /api/config.</summary>
    public static AppConfigDto BuildConfig(TttOptions o, TierCalculator tiers)
    {
        var modes = Enum.GetValues<TimeControl>().Select(tc =>
        {
            var m = o.Modes.TryGetValue(tc.ToString(), out var x) ? x : new ModeOptions();
            var mode = tc switch
            {
                TimeControl.Ranked => MatchMode.Ranked, TimeControl.Unlimited => MatchMode.Unlimited, TimeControl.Daily => MatchMode.Daily,
                TimeControl.ArenaCasual => MatchMode.Arena, TimeControl.ArenaRanked => MatchMode.ArenaRanked, _ => MatchMode.Casual,
            };
            var arena = MatchModes.ArenaFor(o, tc);
            return new ModeConfigDto(tc, mode, RuleSetDto.From(RuleIds.ParseOrDefault(m.Rules)), m.Rated, m.GuestsAllowed,
                arena is null ? null : ArenaRulesDto.From(arena));
        }).ToArray();
        var s = o.Auth.Supabase;
        return new AppConfigDto(ProtocolInfo.Version, o.Auth.Dev.Enabled, s.Enabled, s.Enabled ? s.Url : null, s.Enabled ? s.AnonKey : null, modes,
            EmoteCatalog.All.ToArray(), tiers.Bands, o.Ratings.MinRatedGames, new AdConfigDto(o.Ads.InterstitialEveryGames, o.Ads.SkipAfterTwoLosses, o.Ads.MinSecondsBetween, o.Ads.FirstAfterGames), o.Web.BaseUrl,
            s.Enabled && o.Auth.Review.Enabled ? o.Auth.Review.Email.Trim().ToLowerInvariant() : null);
    }

    private static IResult From<T>(OpResult<T> r) => r.Ok ? Results.Ok(r.Value) : ApiErrors.Problem(r.Status, r.Error!);

    private static bool TryEnum<T>(string? s, out T value) where T : struct, Enum => Enum.TryParse(s, ignoreCase: true, out value) && Enum.IsDefined(value);

    /// <summary>Maps all WP3 endpoints.</summary>
    public static WebApplication MapPlatformEndpoints(this WebApplication app)
    {
        var opts = app.Services.GetRequiredService<IOptions<TttOptions>>().Value;
        var api = app.MapGroup("/api").RequireRateLimiting(RateLimits.Api);
        const string Account = TttPolicies.Account;

        api.MapGet("/config", (IOptions<TttOptions> o, TierCalculator t) => Results.Ok(BuildConfig(o.Value, t))).AllowAnonymous();

        if (opts.Auth.Dev.Enabled)
        {
            api.MapGet("/auth/dev/users", async (AccountService a, CancellationToken ct) => Results.Ok(await a.DevUsersAsync(ct))).AllowAnonymous();
            api.MapPost("/auth/dev/token", async (DevTokenRequestDto req, AccountService a, CancellationToken ct) => From(await a.DevTokenAsync(req, ct)))
                .AllowAnonymous().RequireRateLimiting(RateLimits.Auth);
        }
        // Guests (device-bound, server-signed tokens) exist in every environment.
        api.MapPost("/auth/guest", async (GuestTokenRequestDto req, AccountService a, CancellationToken ct) => From(await a.GuestTokenAsync(req, ct)))
            .AllowAnonymous().RequireRateLimiting(RateLimits.Auth);
        // App Store / Play review: a fixed account with a fixed code (the reviewer can't receive our emails).
        api.MapPost("/auth/review", async (ReviewSignInDto req, ReviewAccount review, CancellationToken ct) =>
            await review.SignInAsync(req, ct) is { } session ? Results.Ok(session) : ApiErrors.Problem(401, ErrorCodes.Forbidden))
            .AllowAnonymous().RequireRateLimiting(RateLimits.Auth);
        // After an email sign-in: move the device's guest history into the account (the guest token proves ownership).
        api.MapPost("/auth/claim-guest", async (ClaimGuestDto req, HttpContext http, AccountService a, CancellationToken ct) =>
            From(await a.ClaimGuestAsync(http.Me().UserId, req.GuestToken, ct))).RequireAuthorization(Account);

        // ---- me ----
        api.MapGet("/me", async (HttpContext http, AccountService a, IUserRepository users, TimeProvider time, CancellationToken ct) =>
        {
            var me = http.Me();
            await users.TouchAsync(me.UserId, time.GetUtcNow(), ct);
            var dto = await a.GetMeAsync(me.UserId, ct);
            return dto is null ? ApiErrors.Problem(404, ErrorCodes.Validation) : Results.Ok(dto);
        }).RequireAuthorization();
        api.MapPut("/me/username", async (SetUsernameDto req, HttpContext http, AccountService a, CancellationToken ct) => From(await a.SetUsernameAsync(http.Me().UserId, req, ct)))
            .RequireAuthorization();
        api.MapPatch("/me", async (UpdateProfileDto req, HttpContext http, AccountService a, CancellationToken ct) => From(await a.UpdateProfileAsync(http.Me().UserId, req, ct)))
            .RequireAuthorization();
        // In-app account deletion (store policies). Works for guests and accounts.
        api.MapDelete("/me", async (HttpContext http, AccountService a, CancellationToken ct) =>
        {
            var r = await a.DeleteAccountAsync(http.Me().UserId, ct);
            return r.Ok ? Results.NoContent() : ApiErrors.Problem(r.Status, r.Error!);
        }).RequireAuthorization().RequireRateLimiting(RateLimits.Auth);

        // ---- players ----
        api.MapGet("/players/search", async (string? q, IUserRepository users, IPlayerCardService cards, CancellationToken ct) =>
        {
            var prefix = UsernameRules.Normalize(q);
            if (prefix.Length == 0) return Results.Ok(Array.Empty<PlayerCardDto>());
            var found = (await users.SearchAsync(prefix, 20, ct)).Where(u => u.Kind == AccountKind.Account).Select(u => u.Id).ToList();
            var map = await cards.GetCardsAsync(found, RatingPools.Headline, ct);
            return Results.Ok(found.Select(id => map[id]).ToArray());
        }).RequireAuthorization(Account);

        api.MapGet("/players/{username}", async (string username, HttpContext http, IUserRepository users, IPlayerCardService cards, RatingService ratings,
            IGameRepository games, IProgressRepository progress, IDailyMatchRepository daily, SocialService social, GameViews views, TimeProvider time, CancellationToken ct) =>
        {
            var u = await users.GetByUsernameAsync(username, ct);
            if (u is null) return ApiErrors.Problem(404, ErrorCodes.Validation);
            var viewer = http.Me().UserId;
            var card = await cards.GetCardAsync(u.Id, RatingPools.Headline, ct);
            var visible = await ratings.VisibleAsync(u.Id, time.GetUtcNow(), ct);
            var list = await games.ListForUserAsync(u.Id, null, 500, ct);
            int wins = 0, losses = 0, draws = 0;
            foreach (var g in list)
            {
                switch (PostGameProcessor.ResultFor(g.Result, g.Setup.SeatA.UserId == u.Id ? 0 : 1))
                {
                    case GameResultForUser.Win: wins++; break;
                    case GameResultForUser.Loss: losses++; break;
                    case GameResultForUser.Draw: draws++; break;
                }
            }
            var played = wins + losses + draws;
            var streak = StreakRules.ToDto(await progress.GetStreakAsync(u.Id, ct), LocalDays.Day(time.GetUtcNow(), u.TimeZone));
            var stats = new ProfileStatsDto(played, wins, losses, draws, played == 0 ? 0 : Math.Round((double)wins / played, 4), streak.Current, streak.Best,
                (await daily.ListActiveForUserAsync(u.Id, ct)).Count);
            var recent = await views.SummariesAsync(u.Id, list.Take(10).ToList(), ct);
            var presence = u.Kind == AccountKind.Account ? http.RequestServices.GetService<PresenceLookup>()?.Get(u.Id) : null;
            return Results.Ok(new ProfileDto(card, u.CreatedAt, visible, stats, recent, await social.StatusAsync(viewer, u.Id, ct)) { Presence = presence });
        }).RequireAuthorization();

        api.MapGet("/players/{userId:guid}/games", async (Guid userId, DateTimeOffset? before, int? take, HttpContext http, IGameRepository games, GameViews views, CancellationToken ct) =>
        {
            var me = http.Me();
            if (me.IsGuest && me.UserId != userId) return ApiErrors.Problem(403, ErrorCodes.GuestNotAllowed);
            var list = await games.ListForUserAsync(userId, before, Math.Clamp(take ?? 20, 1, 50), ct);
            return Results.Ok(await views.SummariesAsync(userId, list, ct));
        }).RequireAuthorization();

        // ---- games ----
        api.MapGet("/games/{id:guid}", async (Guid id, IGameRepository games, GameViews views, CancellationToken ct) =>
        {
            var g = await games.GetAsync(id, ct);
            return g is null ? ApiErrors.Problem(404, ErrorCodes.MatchNotFound) : Results.Ok(await views.RecordAsync(g, ct));
        }).RequireAuthorization();

        api.MapGet("/games/{id:guid}/analysis", async (Guid id, HttpContext http, IGameRepository games, IRewardLedger ledger, ITablebaseCache cache,
            IAnalysisService analysis, CancellationToken ct) =>
        {
            var g = await games.GetAsync(id, ct);
            if (g is null) return ApiErrors.Problem(404, ErrorCodes.MatchNotFound);
            var meId = http.Me().UserId;
            if (!await ledger.HasGrantAsync(meId, RewardKind.Analysis, id, ct)
                && (await http.RequestServices.GetRequiredService<TicTacToeUltimate.Server.Economy.WalletService>().GetAsync(meId, ct)).AnalysisPassUntilMs is null)
                return ApiErrors.Problem(403, ErrorCodes.RewardRequired);
            if (MoveClock.IsArena(g.Setup.TimeControl)) return ApiErrors.Problem(404, ErrorCodes.AnalysisUnavailable);
            switch (cache.GetStatus(g.Setup.Rules))
            {
                case TablebaseStatus.Warming: return ApiErrors.Problem(503, ErrorCodes.AnalysisWarming);
                case TablebaseStatus.Unavailable: return ApiErrors.Problem(404, ErrorCodes.AnalysisUnavailable);
            }
            var dto = await analysis.AnalyzeAsync(g, ct);
            return dto is null ? ApiErrors.Problem(404, ErrorCodes.AnalysisUnavailable) : Results.Ok(dto);
        }).RequireAuthorization();

        // ---- leaderboards ----
        api.MapGet("/leaderboards/{pool}", async (string pool, string? scope, string? country, int? page, HttpContext http, IRatingRepository ratings,
            IFriendRepository friends, IUserRepository users, IPlayerCardService cards, TierCalculator tiers, IOptions<TttOptions> o, CancellationToken ct) =>
        {
            if (!TryEnum<RatingPool>(pool, out var p) || !RatingPools.IsVisible(p)) return ApiErrors.Problem(400, ErrorCodes.Validation);
            var sc = LeaderboardScope.Global;
            if (scope is not null && !TryEnum(scope, out sc)) return ApiErrors.Problem(400, ErrorCodes.Validation);
            var me = http.Me().UserId;
            var minGames = o.Value.Ratings.MinRatedGames;
            IReadOnlyCollection<Guid>? restrict = null;
            string? ctry = null;
            double? minRating = null;
            switch (sc)
            {
                case LeaderboardScope.Friends: restrict = [.. await friends.ListFriendsAsync(me, ct), me]; minGames = 0; break;
                case LeaderboardScope.Country:
                    ctry = (country ?? (await users.GetAsync(me, ct))?.Country)?.ToUpperInvariant();
                    if (ctry is null) return ApiErrors.Problem(400, ErrorCodes.Validation);
                    break;
                case LeaderboardScope.MasterSeason:
                    p = RatingPools.Headline; minRating = tiers.MasterMin; break;
            }
            const int PageSize = 50;
            var pg = Math.Max(1, page ?? 1);
            var res = await ratings.QueryAsync(new LeaderboardQuery(p, sc, ctry, me, restrict, minGames, minRating, pg, PageSize), ct);
            var ids = res.Rows.Select(r => r.UserId).ToList();
            if (res.Viewer is { } v) ids.Add(v.UserId);
            ids = ids.Distinct().ToList();
            var map = await cards.GetCardsAsync(ids, p, ct);
            LeaderboardRowDto Row(LeaderboardEntry e) => new(e.Rank, map[e.UserId], (int)Math.Round(e.Rating), e.Games);
            return Results.Ok(new LeaderboardPageDto(p, sc, ctry, pg, PageSize, res.TotalCount, res.Rows.Select(Row).ToArray(), res.Viewer is { } vv ? Row(vv) : null));
        }).RequireAuthorization(Account);

        // ---- friends / blocks / chat ----
        api.MapGet("/friends", async (HttpContext http, SocialService s, CancellationToken ct) => Results.Ok(await s.OverviewAsync(http.Me().UserId, ct)))
            .RequireAuthorization(Account);
        api.MapPost("/friends/requests", async (SendFriendRequestDto req, HttpContext http, SocialService s, CancellationToken ct) =>
            Results.Ok(await s.RequestAsync(http.Me().UserId, req.Username, req.UserId, ct))).RequireAuthorization(Account).RequireRateLimiting(RateLimits.Social);
        api.MapPost("/friends/requests/{id:guid}/accept", async (Guid id, HttpContext http, SocialService s, CancellationToken ct) =>
            Results.Ok(await s.RespondAsync(http.Me().UserId, id, true, ct))).RequireAuthorization(Account);
        api.MapPost("/friends/requests/{id:guid}/decline", async (Guid id, HttpContext http, SocialService s, CancellationToken ct) =>
            Results.Ok(await s.RespondAsync(http.Me().UserId, id, false, ct))).RequireAuthorization(Account);
        api.MapDelete("/friends/{userId:guid}", async (Guid userId, HttpContext http, SocialService s, CancellationToken ct) =>
        {
            await s.RemoveAsync(http.Me().UserId, userId, ct);
            return Results.NoContent();
        }).RequireAuthorization(Account);
        api.MapPost("/blocks/{userId:guid}", async (Guid userId, HttpContext http, SocialService s, CancellationToken ct) =>
        {
            if (userId == http.Me().UserId) return ApiErrors.Problem(400, ErrorCodes.Validation);
            await s.BlockAsync(http.Me().UserId, userId, ct);
            return Results.NoContent();
        }).RequireAuthorization(Account);
        api.MapDelete("/blocks/{userId:guid}", async (Guid userId, HttpContext http, SocialService s, CancellationToken ct) =>
        {
            await s.UnblockAsync(http.Me().UserId, userId, ct);
            return Results.NoContent();
        }).RequireAuthorization(Account);
        api.MapGet("/chat/{friendId:guid}", async (Guid friendId, long? before, int? take, HttpContext http, SocialService s, CancellationToken ct) =>
        {
            var (page, error) = await s.HistoryAsync(http.Me().UserId, friendId, before, take ?? 50, ct);
            return error is null ? Results.Ok(page) : ApiErrors.Problem(403, error);
        }).RequireAuthorization(Account);

        // ---- progress / rewards ----
        api.MapGet("/progress", async (HttpContext http, ProgressService p, CancellationToken ct) => Results.Ok(await p.GetAsync(http.Me().UserId, ct)))
            .RequireAuthorization();
        // ---- push ----
        api.MapPost("/push/register", async (RegisterPushDto req, HttpContext http, TicTacToeUltimate.Server.Push.IPushTokenRepository tokens, TimeProvider time, CancellationToken ct) =>
        {
            if (req.Platform is not ("ios" or "android") || string.IsNullOrWhiteSpace(req.Token) || req.Token.Length > 512) return ApiErrors.Problem(400, ErrorCodes.Validation);
            await tokens.UpsertAsync(new TicTacToeUltimate.Server.Push.PushTokenRecord(http.Me().UserId, req.Platform, req.Token.Trim(), time.GetUtcNow()), ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // ---- store / wallet ----
        api.MapGet("/wallet", async (HttpContext http, TicTacToeUltimate.Server.Economy.WalletService w, CancellationToken ct) => Results.Ok(await w.GetAsync(http.Me().UserId, ct)))
            .RequireAuthorization();
        api.MapGet("/store", async (HttpContext http, TicTacToeUltimate.Server.Economy.WalletService w, CancellationToken ct) => Results.Ok(await w.StoreAsync(http.Me().UserId, ct)))
            .RequireAuthorization();
        api.MapPost("/store/buy", async (PurchaseDto req, HttpContext http, TicTacToeUltimate.Server.Economy.WalletService w, CancellationToken ct) =>
            Results.Ok(await w.PurchaseAsync(http.Me().UserId, req, ct))).RequireAuthorization().RequireRateLimiting(RateLimits.Rewards);

        api.MapPost("/rewards/nonce", async (RewardNonceRequestDto req, HttpContext http, RewardService r, CancellationToken ct) =>
            Results.Ok(await r.IssueNonceAsync(http.Me().UserId, req, ct))).RequireAuthorization().RequireRateLimiting(RateLimits.Rewards);
        api.MapPost("/rewards/claim", async (RewardClaimDto req, HttpContext http, RewardService r, CancellationToken ct) =>
            Results.Ok(await r.ClaimAsync(http.Me().UserId, req, ct))).RequireAuthorization().RequireRateLimiting(RateLimits.Rewards);

        return app;
    }
}
