using System.Text.Json;
using System.Text.Json.Serialization;

namespace TicTacToeUltimate.Contracts.Admin;

// Admin site (admin.alidirect.dev) <-> /api/admin/*. Separate from the game contracts: only the admin app and server use these.

public sealed record AdminMeDto(string Email, bool DevBypass);

public sealed record AdminLiveMatchDto(Guid MatchId, string Mode, string TimeControl, string[] Players, string[] Kinds, int Ply, int Spectators, long? DeadlineAtMs);
public sealed record LiveDto(int Online, int Connections, int LiveMatches, int BotMatches, int BotsSeeking, Dictionary<string, int> Queues,
    int ShadowPlayers, AdminLiveMatchDto[] Matches, long ServerNowMs);

public sealed record StatPointDto(DateOnly Day, string Dim, long Value);
public sealed record OverviewDto(LiveDto Live, int OpenReports, int OpenAppeals, int CrashGroups24h, int ShadowPlayers, Dictionary<string, StatPointDto[]> Series);

public sealed record AdminPlayerDto(Guid Id, string Kind, string DisplayName, string? Username, string? Country, DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt, bool Shadow, bool Online);
public sealed record AdminPenaltyDto(long Id, Guid UserId, string Kind, string Reason, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, string Source,
    string? CreatedBy, DateTimeOffset? RevokedAt, bool Active, string? AppealStatus);
public sealed record AdminReportDto(long Id, Guid Reporter, string ReporterName, Guid Target, string TargetName, string Kind, string Reason, string? Note,
    long? MessageId, Guid? GameId, string EvidenceJson, string Status, string? AiVerdictJson, DateTimeOffset CreatedAt, string? Resolution);
public sealed record AdminModEventDto(long Id, Guid UserId, string UserName, string Kind, string? Text, string DetailJson, DateTimeOffset At);
public sealed record AdminAppealDto(long Id, long PenaltyId, Guid UserId, string UserName, string Text, string Status, DateTimeOffset CreatedAt,
    AdminPenaltyDto? Penalty, string? DecisionNote);
public sealed record SuspicionRowDto(Guid UserId, string Name, string Kind, bool Shadow, int Games, int Critical, double Accuracy, double WinRate,
    int ThinkMsAvg, bool OverThreshold, int CheatingReports);
public sealed record AdminGameDto(Guid Id, string Mode, string TimeControl, string Opponent, string Result, string EndReason, DateTimeOffset EndedAt, int Plies);
public sealed record AdminPlayerDetailDto(AdminPlayerDto Player, string[] Devices, Guid[] SameDevicePlayers, AdminPenaltyDto[] Penalties,
    AdminReportDto[] ReportsAgainst, AdminModEventDto[] Events, AdminGameDto[] Games, SuspicionRowDto Suspicion, int? Rating);

/// <summary>Kind: warn | chat_mute | ban | username_reset | shadow. DurationMinutes null = permanent (ban, shadow).</summary>
public sealed record PenaltyRequestDto(string Kind, string Reason, int? DurationMinutes);
/// <summary>Status: actioned | dismissed. Penalty optional (applied to the reported player).</summary>
public sealed record ResolveReportDto(string Status, string? Resolution, PenaltyRequestDto? Penalty);
public sealed record DecideAppealDto(bool Approve, string? Note);
public sealed record ShadowRequestDto(bool On);
public sealed record CrashStatusDto(string Status);

public sealed record CrashGroupDto(string Fingerprint, string Kind, string Title, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Count, string Status,
    string? GhIssueUrl, string[] Platforms);
public sealed record CrashReportAdminDto(long Id, Guid? UserId, string? Username, string Platform, string AppVersion, string? Os, string? Device, string? Browser,
    string? Locale, string? Screen, string? Route, string? Component, string? ExceptionType, string? Message, string? Stack, string InnerJson, string LogsJson,
    string TrailJson, string? Description, string? ScreenshotPath, DateTimeOffset At);
public sealed record CrashGroupDetailDto(CrashGroupDto Group, CrashReportAdminDto[] Reports, int Users24h);

public sealed record CampaignDto(string Slug, string Name, string Channel, string? IosUrl, string? AndroidUrl, string? WebUrl, DateTimeOffset CreatedAt,
    bool Archived, string Link, long Clicks, long Signups, long Active);
public sealed record CreateCampaignDto(string Slug, string Name, string Channel, string? IosUrl, string? AndroidUrl, string? WebUrl);

public sealed record AuditDto(long Id, string AdminEmail, string Action, string? Target, string DetailJson, DateTimeOffset At);
public sealed record AdminActionResultDto(bool Ok, string? Error = null, string? Url = null);

// ---------- infrastructure (Phase 7b) ----------
public sealed record HostStatsDto(double CpuPercent, long MemTotalBytes, long MemUsedBytes, long SwapUsedBytes, double Load1, long DiskTotalBytes,
    long DiskUsedBytes, long RxBytesPerSec, long TxBytesPerSec, long EgressMonthBytes, long UptimeSeconds);
public sealed record ContainerStatsDto(string Name, string Image, string State, double CpuPercent, long MemBytes, long MemLimitBytes, int Restarts, DateTimeOffset? StartedAt);
public sealed record InfraSampleDto(long AtMs, double Cpu, long MemUsed, long Rx, long Tx);
public sealed record ServerHealthDto(string Version, string BuildSha, DateTimeOffset StartedAt, long GcHeapBytes, int ThreadPoolThreads, int Connections,
    int LiveMatches, string TablebaseStatus);
public sealed record UsageDto(long DbBytes, long DbLimitBytes, Dictionary<string, long> Tables, long StorageBytes, long? WorkerRequests24h, long? WorkerErrors24h);
public sealed record InfraAlertDto(string Level, string Text);
public sealed record InfraDto(bool OpsConnected, HostStatsDto? Host, ContainerStatsDto[] Containers, InfraSampleDto[] Series, ServerHealthDto Server, UsageDto? Usage,
    long EgressFreeTierBytes, InfraAlertDto[] Alerts);
public sealed record LogEntryDto(DateTimeOffset At, string Service, string Level, string Text);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AdminMeDto))]
[JsonSerializable(typeof(LiveDto))]
[JsonSerializable(typeof(OverviewDto))]
[JsonSerializable(typeof(StatPointDto[]))]
[JsonSerializable(typeof(AdminPlayerDto[]))]
[JsonSerializable(typeof(AdminPlayerDetailDto))]
[JsonSerializable(typeof(AdminPenaltyDto))]
[JsonSerializable(typeof(AdminReportDto[]))]
[JsonSerializable(typeof(AdminModEventDto[]))]
[JsonSerializable(typeof(AdminAppealDto[]))]
[JsonSerializable(typeof(SuspicionRowDto[]))]
[JsonSerializable(typeof(PenaltyRequestDto))]
[JsonSerializable(typeof(ResolveReportDto))]
[JsonSerializable(typeof(DecideAppealDto))]
[JsonSerializable(typeof(ShadowRequestDto))]
[JsonSerializable(typeof(CrashStatusDto))]
[JsonSerializable(typeof(CrashGroupDto[]))]
[JsonSerializable(typeof(CrashGroupDetailDto))]
[JsonSerializable(typeof(CampaignDto[]))]
[JsonSerializable(typeof(CampaignDto))]
[JsonSerializable(typeof(CreateCampaignDto))]
[JsonSerializable(typeof(AuditDto[]))]
[JsonSerializable(typeof(AdminActionResultDto))]
[JsonSerializable(typeof(InfraDto))]
[JsonSerializable(typeof(LogEntryDto[]))]
public sealed partial class AdminJsonContext : JsonSerializerContext;
