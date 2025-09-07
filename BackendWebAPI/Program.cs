using System.Text;
using AspNet.Security.OAuth.Apple;
using BackendWebAPI.Config;
using BackendWebAPI.Models;            // + for Player
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
builder.Services.AddSignalR();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Controllers
builder.Services.AddControllers();

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

// Map Controllers
app.MapControllers();

// Minimal API & SignalR
app.MapGet("/ping", () => "pong");
app.MapHub<GameHub>("/hubs/game");
app.MapHub<TestHub>("/hubs/test");

// OAuth kickoffs
app.MapGet("/auth/google", () => Results.Challenge(
    new AuthenticationProperties { RedirectUri = "/auth/external/callback" },
    new[] { "Google" }));

app.MapGet("/auth/apple", () => Results.Challenge(
    new AuthenticationProperties { RedirectUri = "/auth/external/callback" },
    new[] { AppleAuthenticationDefaults.AuthenticationScheme }));

// Stripe endpoints
app.MapPost("/payments/create-intent", async
    (CreatePaymentRequest req, Microsoft.Extensions.Options.IOptions<StripeSettings> opt) =>
{
    var amount = (long)(req.Amount * 100);
    var svc = new Stripe.PaymentIntentService();
    var intent = await svc.CreateAsync(new Stripe.PaymentIntentCreateOptions
    {
        Amount = amount,
        Currency = opt.Value.Currency,
        AutomaticPaymentMethods = new Stripe.PaymentIntentAutomaticPaymentMethodsOptions { Enabled = true }
    });
    return Results.Ok(new { intent.ClientSecret, opt.Value.PublishableKey });
});

app.MapPost("/payments/webhook", async (HttpRequest r, Microsoft.Extensions.Options.IOptions<StripeSettings> opt) =>
{
    using var sr = new StreamReader(r.Body);
    var json = await sr.ReadToEndAsync();
    var sig = r.Headers["Stripe-Signature"];
    try
    {
        var e = Stripe.EventUtility.ConstructEvent(json, sig, opt.Value.WebhookSecret);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.Run();

// EF DbContext + Hubs (inline)
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // NEW: expose Players and map to existing table
    public DbSet<Player> Players => Set<Player>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("tttu");
        var e = b.Entity<Player>();
        e.ToTable("players");
        e.HasKey(x => x.PlayerId);
        e.Property(x => x.PlayerId).HasColumnName("player_id").ValueGeneratedOnAdd();
        e.Property(x => x.Sub).HasColumnName("sub").IsRequired();
        e.Property(x => x.Username).HasColumnName("username").HasMaxLength(32).IsRequired();
        e.Property(x => x.Elo).HasColumnName("elo").HasDefaultValue(500);
        e.Property(x => x.Email).HasColumnName("email").IsRequired();
        e.Property(x => x.Age).HasColumnName("age");
        e.HasIndex(x => x.Username).IsUnique();
        e.HasIndex(x => x.Email).IsUnique();
        e.HasIndex(x => x.Sub).IsUnique();
    }
}
public class GameHub : Microsoft.AspNetCore.SignalR.Hub { }
public class TestHub : Microsoft.AspNetCore.SignalR.Hub { }

public record CreatePaymentRequest(decimal Amount);