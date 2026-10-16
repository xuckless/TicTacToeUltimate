namespace TicTacToeUltimate.Server.Hosting;

/// <summary>Mirrors the "Ttt" configuration section (§6.10).</summary>
public sealed class TttOptions
{
    public const string Section = "Ttt";
    public AuthOptions Auth { get; set; } = new();
    public PersistenceOptions Persistence { get; set; } = new();
    public Dictionary<string, ModeOptions> Modes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ClockOptions Clock { get; set; } = new();
    public UnlimitedOptions Unlimited { get; set; } = new();
    public DailyOptions Daily { get; set; } = new();
    public MatchmakingOptions Matchmaking { get; set; } = new();
    public ChallengesOptions Challenges { get; set; } = new();
    public RematchOptions Rematch { get; set; } = new();
    public AnalysisOptions Analysis { get; set; } = new();
    public BotsOptions Bots { get; set; } = new();
    public RatingsOptions Ratings { get; set; } = new();
    public TiersOptions Tiers { get; set; } = new();
    public SeasonsOptions Seasons { get; set; } = new();
    public AdsOptions Ads { get; set; } = new();
    public ShutdownOptions Shutdown { get; set; } = new();
    public CorsOptions Cors { get; set; } = new();
    public WebOptions Web { get; set; } = new();
    public ModerationOptions Moderation { get; set; } = new();
}

/// <summary>Ttt:Moderation. Strike ladder (owner decision 2026-10-16): warn, then chat mutes of 1 h / 24 h / 7 d; bans only by an admin.</summary>
public sealed class ModerationOptions
{
    /// <summary>Strikes count within this window.</summary>
    public int StrikeWindowDays { get; set; } = 30;
    /// <summary>Mute length in minutes for strike 2, 3, 4+ (strike 1 is a warning).</summary>
    public int[] MuteMinutes { get; set; } = [60, 24 * 60, 7 * 24 * 60];
    /// <summary>Salt for device id hashes (secret in production; falls back to the dev signing key).</summary>
    public string DeviceSalt { get; set; } = "";
    public AnthropicModerationOptions Anthropic { get; set; } = new();
}
/// <summary>Ttt:Moderation:Anthropic. Without an ApiKey the AI check is off (the word filter still runs).</summary>
public sealed class AnthropicModerationOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-haiku-5-5";
    public int TimeoutMs { get; set; } = 3000;
}

public sealed class AuthOptions
{
    public DevAuthOptions Dev { get; set; } = new();
    public SupabaseAuthOptions Supabase { get; set; } = new();
    public ReviewAuthOptions Review { get; set; } = new();
}
/// <summary>
/// App Store / Play reviewer account: <see cref="Email"/> signs in with the fixed <see cref="Code"/> instead of an emailed one.
/// Code and Password are secrets (Ttt__Auth__Review__Code / __Password); the server keeps the Supabase user's password in sync.
/// </summary>
public sealed class ReviewAuthOptions
{
    public string Email { get; set; } = "";
    public string Code { get; set; } = "";
    public string Password { get; set; } = "";
    public bool Enabled => Email.Contains('@') && Code.Length >= 6 && Password.Length >= 12;
}
public sealed class DevAuthOptions { public bool Enabled { get; set; } public string SigningKey { get; set; } = ""; public int TokenDays { get; set; } = 30; }
public sealed class SupabaseAuthOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string AnonKey { get; set; } = "";
    public string JwtSecret { get; set; } = "";
    public string Audience { get; set; } = "authenticated";
    /// <summary>Service-role key (secret) for the auth admin API: deleting auth users on account deletion.</summary>
    public string ServiceRoleKey { get; set; } = "";
}
public sealed class PersistenceOptions
{
    public string Provider { get; set; } = "InMemory";
    public bool SeedFixtures { get; set; } = true;
    public PostgresOptions Postgres { get; set; } = new();
}
public sealed class PostgresOptions { public string ConnectionString { get; set; } = ""; }
public sealed class ModeOptions { public string Rules { get; set; } = "n3k3-cap3-v0-w1-none"; public bool Rated { get; set; } public bool GuestsAllowed { get; set; } /// <summary>Arena ruleset id ("arena-c"); arena time controls only.</summary>
    public string? Arena { get; set; } }
public sealed class ClockOptions
{
    public int LagCreditCapMs { get; set; } = 300;
    public int LagBudgetMs { get; set; } = 4000;
    public int RttSamples { get; set; } = 5;
    public int PingLiveSeconds { get; set; } = 2;
    public int PingIdleSeconds { get; set; } = 15;
}
public sealed class UnlimitedOptions { public int DisconnectForfeitMinutes { get; set; } = 10; }
public sealed class DailyOptions { public int MaxConcurrent { get; set; } = 10; public int PassivateMinutes { get; set; } = 10; public int SweepSeconds { get; set; } = 60; }
public sealed class MatchmakingOptions
{
    public int TickMs { get; set; } = 250;
    public int InitialWindow { get; set; } = 100;
    public int WidenBy { get; set; } = 50;
    public int WidenEverySeconds { get; set; } = 3;
    public int GuestFallbackSeconds { get; set; } = 10;
    public int BotFallbackSeconds { get; set; } = 20;
    public int DisconnectGraceSeconds { get; set; } = 5;
    /// <summary>Fewer than this many online players within ±ThinBandWindow of a queued player = "thin band": request a bot immediately and pair after QuickBotSeconds.</summary>
    public int MinNearbyPlayers { get; set; } = 3;
    public int ThinBandWindow { get; set; } = 200;
    public int QuickBotSeconds { get; set; } = 3;
}
public sealed class ChallengesOptions { public int TtlHours { get; set; } = 24; }
public sealed class RematchOptions { public int WindowSeconds { get; set; } = 60; }
public sealed class AnalysisOptions { public long MaxStates { get; set; } = 5_000_000; public List<string> Precompute { get; set; } = []; }
public sealed class BotsOptions
{
    public bool Enabled { get; set; } = true;
    public bool Ranked { get; set; }
    public bool AllowQueryKey { get; set; }
    /// <summary>Let accounts create their own bot API keys (off: 403). Default off until the public bot API is reviewed.</summary>
    public bool AllowUserKeys { get; set; }
    public List<BotDevKeyOptions> DevKeys { get; set; } = [];
    /// <summary>External bot service (bots/, Go). Empty Url = no dispatch.</summary>
    public BotServiceOptions Service { get; set; } = new();
}
public sealed class BotServiceOptions
{
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    /// <summary>A waiting player triggers one bot dispatch after this many seconds (before the bot-fallback pairing).</summary>
    public int DispatchAfterSeconds { get; set; } = 6;
}
public sealed class BotDevKeyOptions { public string Name { get; set; } = ""; public string Key { get; set; } = ""; }
public sealed class RatingsOptions { public double Tau { get; set; } = 0.5; public int ProvisionalGames { get; set; } = 10; public int IdlePeriodDays { get; set; } = 7; public int MinRatedGames { get; set; } = 20; }
public sealed class TiersOptions
{
    public int Bronze { get; set; }
    public int Silver { get; set; } = 1100;
    public int Gold { get; set; } = 1400;
    public int Platinum { get; set; } = 1700;
    public int Diamond { get; set; } = 2000;
    public int Master { get; set; } = 2300;
    public int DivisionWidth { get; set; } = 100;
}
public sealed class SeasonsOptions { public int LengthDays { get; set; } = 90; public int MasterResetRd { get; set; } = 150; }
public sealed class AdsOptions
{
    public string Provider { get; set; } = "Stub";
    /// <summary>Mobile-game pacing (about one break per Duolingo lesson): every 2 games, 2+ min apart, none in a session's first 2.</summary>
    public int InterstitialEveryGames { get; set; } = 2;
    public bool SkipAfterTwoLosses { get; set; } = true;
    public int MinSecondsBetween { get; set; } = 120;
    public int FirstAfterGames { get; set; } = 2;
}
/// <summary>Graceful deploys: on SIGTERM stop new matches and wait up to DrainSeconds for live ones to finish (0 = no wait).</summary>
public sealed class ShutdownOptions { public int DrainSeconds { get; set; } }
public sealed class CorsOptions { public List<string> Origins { get; set; } = []; }
public sealed class WebOptions { public string BaseUrl { get; set; } = "http://localhost:5028"; }
