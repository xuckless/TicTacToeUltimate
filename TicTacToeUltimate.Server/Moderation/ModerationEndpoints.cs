using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Platform;
using TicTacToeUltimate.Server.Auth;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Moderation;

/// <summary>Player-facing moderation: standing (penalties in force), appeals, reports.</summary>
public static class ModerationEndpoints
{
    public static IServiceCollection AddTttModeration(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<AntiCheatOptions>(cfg.GetSection("Ttt:AntiCheat"));
        services.AddSingleton<CheatScorer>();
        if (string.Equals(cfg["Ttt:Persistence:Provider"], "Postgres", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IGameMetricsRepository, PgGameMetricsRepository>();
        else
            services.AddSingleton<IGameMetricsRepository, InMemoryGameMetricsRepository>();
        services.AddSingleton<ModerationService>();
        services.AddSingleton(_ => new ChatFilter());   // not the IEnumerable<string> ctor (DI would pass an empty list)
        services.AddSingleton<AiModerator>();
        services.AddSingleton<ReportService>();
        return services;
    }

    public static WebApplication UseTttModeration(this WebApplication app)
    {
        app.UseMiddleware<ModerationGate>();
        return app;
    }

    public static WebApplication MapTttModeration(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireRateLimiting(RateLimits.Api);
        api.MapGet("/me/standing", async (HttpContext http, ModerationService m, CancellationToken ct) =>
            Results.Ok(await m.StandingAsync(http.Me().UserId, ct))).RequireAuthorization();
        api.MapPost("/appeals", async (AppealRequestDto req, HttpContext http, ModerationService m, CancellationToken ct) =>
            await m.AppealAsync(http.Me().UserId, req, ct) is { } error ? ApiErrors.Problem(error == ErrorCodes.AlreadyAppealed ? 409 : 400, error) : Results.NoContent())
            .RequireAuthorization().RequireRateLimiting(RateLimits.Social);
        api.MapPost("/reports", async (ReportRequestDto req, HttpContext http, ReportService reports, CancellationToken ct) =>
        {
            var (id, error, status) = await reports.ReportAsync(http.Me(), req, ct);
            return error is null ? Results.Ok(new ReportResultDto(id!.Value)) : ApiErrors.Problem(status, error);
        }).RequireAuthorization().RequireRateLimiting(RateLimits.Social);
        return app;
    }
}
