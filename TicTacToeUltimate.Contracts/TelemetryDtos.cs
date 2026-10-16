namespace TicTacToeUltimate.Contracts;

/// <summary>Event kinds the server accepts (first-party analytics, owner decision 2026-10-16).</summary>
public static class TelemetryKinds
{
    public const string PageView = "page_view", Session = "session", Ad = "ad", Queue = "queue", Match = "match", Account = "account",
        Quest = "quest", Store = "store";
    public static readonly HashSet<string> All = [PageView, Session, Ad, Queue, Match, Account, Quest, Store];
}

/// <summary>One event. Name examples: page_view "/play"; ad "interstitial_shown" / "rewarded_nofill" / "banner_impression".</summary>
public sealed record TelemetryEventDto(long AtMs, string Kind, string Name, Dictionary<string, string>? Props = null);

/// <summary>POST /api/telemetry: a batch from one app session.</summary>
public sealed record TelemetryBatchDto(string SessionId, string Platform, string AppVersion, string? Locale, string? Campaign, TelemetryEventDto[] Events);

/// <summary>PUT /api/me/locale.</summary>
public sealed record SetLocaleDto(string Locale);

/// <summary>GET /api/c/{slug}: where a campaign short link sends this visitor.</summary>
public sealed record CampaignRedirectDto(string Url);
