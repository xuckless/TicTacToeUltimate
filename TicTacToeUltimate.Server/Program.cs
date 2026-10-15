using TicTacToeUltimate.Server.Analysis;
using TicTacToeUltimate.Server.Bots;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Platform;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddTttPlatform(cfg, builder.Environment);   // WP3: auth, persistence, ratings, progression, social, rewards, REST
builder.Services.AddTttAnalysis(cfg);                        // WP1: tablebase cache, analysis
builder.Services.AddTttMatches(cfg);                         // WP2: actors, registry, matchmaking, challenges, presence
builder.Services.AddTttBots(cfg);                            // WP4: bot gateway
builder.Services.AddTttCore(cfg, builder.Environment);       // WP0: MUST BE LAST (options, TimeProvider, SignalR, CORS, TryAdd null fallbacks)

var app = builder.Build();
app.UseTttCore();                 // WP0: exception handler/ProblemDetails, CORS, WebSockets
app.UseAuthentication();
app.UseAuthorization();
app.MapTttPlatform();             // WP3
app.MapTttMatches();              // WP2
app.MapTttBots();                 // WP4
app.MapTttCore();                 // WP0: hub at HubRoutes.Game, GET /health, OpenAPI in Development
app.Run();

public partial class Program;
