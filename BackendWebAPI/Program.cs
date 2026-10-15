using System.Text;
using AspNet.Security.OAuth.Apple;
using BackendWebAPI.Data;
using BackendWebAPI.GameEngine;
using BackendWebAPI.Models;
using BackendWebAPI.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

// <summary>
// Entry point for the BackendWebAPI application.
// Configures services, authentication, database connections, and middleware pipeline.
// </summary>
var builder = WebApplication.CreateBuilder(args);

// Configuration setup - loads additional JSON files for auth and payment settings
builder.Configuration
    .AddJsonFile("appsettings.Auth.json", optional: true);

// Postgres + EF Core - configures Entity Framework with PostgreSQL database
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// Redis - configures Redis connection for caching and session storage
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(builder.Configuration.GetValue<string>("Redis")));
builder.Services.AddSingleton<BackendWebAPI.Services.RedisStore>();

// Authentication - configures JWT Bearer tokens with Google and Apple OAuth providers
builder.Services.AddAuthentication(o =>
{
    o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
    };
})
.AddCookie("External")
.AddGoogle(o =>
{
    o.SignInScheme = "External";
    o.ClientId = builder.Configuration["Auth:Google:ClientId"]!;
    o.ClientSecret = builder.Configuration["Auth:Google:ClientSecret"]!;
})
.AddApple(o =>
{
    o.SignInScheme = "External";
    o.ClientId = builder.Configuration["Auth:Apple:ClientId"]!;
    o.TeamId   = builder.Configuration["Auth:Apple:TeamId"]!;
    o.KeyId    = builder.Configuration["Auth:Apple:KeyId"]!;
    o.GenerateClientSecret = true;

    var pem = builder.Configuration["Auth:Apple:PrivateKey"] ?? "";
    if (!pem.Contains("BEGIN PRIVATE KEY"))
        pem = $"-----BEGIN PRIVATE KEY-----\n{pem}\n-----END PRIVATE KEY-----";
    o.PrivateKey = (keyId, _) => Task.FromResult(pem.AsMemory());
});

// SignalR - configures real-time communication with detailed error reporting
builder.Services.AddSignalR().AddHubOptions<GameHub>(o => o.EnableDetailedErrors = true);

// Swagger - configures API documentation generation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Controllers - registers MVC controllers
builder.Services.AddControllers();

// Game Engine - registers the game logic service as singleton
builder.Services.AddSingleton<IGameEngine, GameEngine>();

// CORS - configures cross-origin requests (development-friendly, allows all origins)
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("AllowFrontend", p => p
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        .SetIsOriginAllowed(_ => true)); // DEV ONLY
});

var app = builder.Build();

// Swagger UI - enables API documentation interface
app.UseSwagger();
app.UseSwaggerUI();

// HTTPS redirection - enforces HTTPS in production environments
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Middleware pipeline - configures request processing order
app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Minimal API endpoints and SignalR hubs
app.MapGet("/ping", () => "pong"); // Health check endpoint
app.MapHub<GameHub>("/hubs/game"); // Game logic SignalR hub

app.Run();

