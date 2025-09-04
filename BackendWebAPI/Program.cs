using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using System.Text;
using AspNet.Security.OAuth.Apple;

var builder = WebApplication.CreateBuilder(args);

// Load local (untracked) secrets if present
builder.Configuration.AddJsonFile("appsettings.Auth.json", optional: true);

// Postgres + EF Core
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(builder.Configuration.GetValue<string>("Redis")));

// Auth: JWT for API, Cookie for external OAuth handshakes
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
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
    };
})
.AddCookie("External") // transient cookie for Google/Apple sign-in
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

    var pem = builder.Configuration["Auth:Apple:PrivateKey"]!;
    if (!pem.Contains("BEGIN PRIVATE KEY"))
        pem = $"-----BEGIN PRIVATE KEY-----\n{pem}\n-----END PRIVATE KEY-----";

    o.PrivateKey = (keyId, _) => Task.FromResult(pem.AsMemory());
});

// SignalR
builder.Services.AddSignalR();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()!)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();

// Minimal API & SignalR
app.MapGet("/ping", () => "pong");
app.MapHub<GameHub>("/hubs/game");

// (Optional) OAuth endpoints to initiate sign-in
app.MapGet("/auth/google", () => Results.Challenge(
    new AuthenticationProperties { RedirectUri = "/auth/external/callback" },
    new[] { "Google" }));
app.MapGet("/auth/apple", () => Results.Challenge(
    new AuthenticationProperties { RedirectUri = "/auth/external/callback" },
    new[] { AppleAuthenticationDefaults.AuthenticationScheme }));

app.Run();

// EF DbContext + Hub
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
}
public class GameHub : Microsoft.AspNetCore.SignalR.Hub { }