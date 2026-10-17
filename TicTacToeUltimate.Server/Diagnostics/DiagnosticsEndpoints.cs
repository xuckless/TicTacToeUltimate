using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Persistence.Postgres;
using TicTacToeUltimate.Server.Platform;

namespace TicTacToeUltimate.Server.Diagnostics;

public static class DiagnosticsEndpoints
{
    public static IServiceCollection AddTttDiagnostics(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<GitHubOptions>(cfg.GetSection("Ttt:GitHub"));
        services.AddHttpClient(nameof(DiagnosticsService));
        services.AddHttpClient(nameof(SupabaseScreenshotStore));
        services.AddSingleton<DiagnosticsService>();
        services.AddHostedService<ScreenshotJanitor>();
        var pg = string.Equals(cfg["Ttt:Persistence:Provider"], "Postgres", StringComparison.OrdinalIgnoreCase);
        if (pg) services.AddSingleton<ICrashRepository, PgCrashRepository>(); else services.AddSingleton<ICrashRepository, InMemoryCrashRepository>();
        if (cfg["Ttt:Auth:Supabase:ServiceRoleKey"] is { Length: > 0 } && cfg["Ttt:Auth:Supabase:Url"] is { Length: > 0 })
            services.AddSingleton<IScreenshotStore, SupabaseScreenshotStore>();
        else
            services.AddSingleton<IScreenshotStore, InMemoryScreenshotStore>();
        return services;
    }

    public static WebApplication MapTttDiagnostics(this WebApplication app)
    {
        // Anyone may report (crashes can happen before sign-in); per-address limit like the auth endpoints; 2 MB cap for screenshots.
        app.MapPost("/api/diagnostics/report", async (CrashReportDto report, HttpContext http, DiagnosticsService d, CancellationToken ct) =>
        {
            Guid? user = http.User.Identity?.IsAuthenticated == true ? http.User.ToTttUser().UserId : null;
            var (fp, error) = await d.AcceptAsync(report, user, ct);
            return error is null ? Results.Ok(new CrashReportResultDto(fp!)) : ApiErrors.Problem(400, error);
        }).AllowAnonymous().RequireRateLimiting(RateLimits.Auth)
          .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2_000_000));
        return app;
    }
}
