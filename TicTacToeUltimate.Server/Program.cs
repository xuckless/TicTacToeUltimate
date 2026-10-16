using TicTacToeUltimate.Server.Analysis;
using TicTacToeUltimate.Server.Analytics;
using TicTacToeUltimate.Server.Bots;
using TicTacToeUltimate.Server.Diagnostics;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Moderation;
using TicTacToeUltimate.Server.Platform;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddTttPlatform(cfg, builder.Environment);   // WP3: auth, persistence, ratings, progression, social, rewards, REST
builder.Services.AddTttAnalysis(cfg);                        // WP1: tablebase cache, analysis
builder.Services.AddTttMatches(cfg);                         // WP2: actors, registry, matchmaking, challenges, presence
builder.Services.AddTttBots(cfg);                            // WP4: bot gateway
builder.Services.AddTttModeration(cfg);                         // moderation: penalties, bans, reports
builder.Services.AddTttDiagnostics(cfg);                        // crash + bug reports, GitHub issues
builder.Services.AddTttAnalytics(cfg);                          // first-party analytics, campaign links
builder.Services.AddTttCore(cfg, builder.Environment);       // WP0: MUST BE LAST (options, TimeProvider, SignalR, CORS, TryAdd null fallbacks)

var app = builder.Build();
app.UseTttCore();                 // WP0: exception handler/ProblemDetails, CORS, WebSockets
app.UseAuthentication();
app.UseAuthorization();
app.UseTttModeration();           // bans (account + device), device tracking
app.UseRateLimiter();             // after auth so per-user partitions see the principal
app.MapTttPlatform();             // WP3
app.MapTttMatches();              // WP2
app.MapTttBots();                 // WP4
app.MapTttModeration();           // standing, appeals, reports
app.MapTttDiagnostics();          // crash + bug reports
app.MapTttAnalytics();            // telemetry, campaign redirects
app.MapTttCore();                 // WP0: hub at HubRoutes.Game, GET /health, OpenAPI in Development
app.Run();

public partial class Program;
