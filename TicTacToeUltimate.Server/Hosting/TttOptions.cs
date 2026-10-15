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
    public CorsOptions Cors { get; set; } = new();
    public WebOptions Web { get; set; } = new();
}

public sealed class AuthOptions
{
    public DevAuthOptions Dev { get; set; } = new();
    public SupabaseAuthOptions Supabase { get; set; } = new();
}
public sealed class DevAuthOptions { public bool Enabled { get; set; } public string SigningKey { get; set; } = ""; public int TokenDays { get; set; } = 30; }
public sealed class SupabaseAuthOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string AnonKey { get; set; } = "";
    public string JwtSecret { get; set; } = "";
    public string Audience { get; set; } = "authenticated";
}
public sealed class PersistenceOptions
{
    public string Provider { get; set; } = "InMemory";
    public bool SeedFixtures { get; set; } = true;
    public PostgresOptions Postgres { get; set; } = new();
}
public sealed class PostgresOptions { public string ConnectionString { get; set; } = ""; }
public sealed class ModeOptions { public string Rules { get; set; } = "n3k3-fifo7-v1-w0-none"; public bool Rated { get; set; } public bool GuestsAllowed { get; set; } }
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
}
public sealed class ChallengesOptions { public int TtlHours { get; set; } = 24; }
public sealed class RematchOptions { public int WindowSeconds { get; set; } = 60; }
public sealed class AnalysisOptions { public long MaxStates { get; set; } = 5_000_000; public List<string> Precompute { get; set; } = []; }
public sealed class BotsOptions
{
    public bool Enabled { get; set; } = true;
    public bool Ranked { get; set; }
    public bool AllowQueryKey { get; set; }
    public List<BotDevKeyOptions> DevKeys { get; set; } = [];
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
public sealed class AdsOptions { public string Provider { get; set; } = "Stub"; public int InterstitialEveryGames { get; set; } = 3; public bool SkipAfterTwoLosses { get; set; } = true; }
public sealed class CorsOptions { public List<string> Origins { get; set; } = []; }
public sealed class WebOptions { public string BaseUrl { get; set; } = "http://localhost:5028"; }
