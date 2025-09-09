using System.Text;
using AspNet.Security.OAuth.Apple;
using BackendWebAPI.Config;
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

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.Auth.json", optional: true)
    .AddJsonFile("appsettings.Stripe.json", optional: true);

// Postgres + EF Core
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(builder.Configuration.GetValue<string>("Redis")));
builder.Services.AddSingleton<BackendWebAPI.Services.RedisStore>();

// Stripe
builder.Services.Configure<StripeSettings>(builder.Configuration.GetSection("Stripe"));
var stripe = builder.Configuration.GetSection("Stripe").Get<StripeSettings>();
if (!string.IsNullOrWhiteSpace(stripe?.SecretKey))
    Stripe.StripeConfiguration.ApiKey = stripe.SecretKey;

// Auth
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

// SignalR
builder.Services.AddSignalR().AddHubOptions<GameHub>(o => o.EnableDetailedErrors = true);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Controllers
builder.Services.AddControllers();

// Game Engine
builder.Services.AddSingleton<IGameEngine, GameEngine>();

// CORS (dev-friendly)
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("AllowFrontend", p => p
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        .SetIsOriginAllowed(_ => true)); // DEV ONLY
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Minimal API & SignalR
app.MapGet("/ping", () => "pong");
app.MapHub<GameHub>("/hubs/game");
app.MapHub<TestHub>("/hubs/test");

// OAuth kickoffs & Stripe endpoints unchanged…

app.Run();

// EF DbContext unchanged…
public record CreatePaymentRequest(decimal Amount);