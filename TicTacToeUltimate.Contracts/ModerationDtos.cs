namespace TicTacToeUltimate.Contracts;

/// <summary>Report kinds (owner decision 2026-10-16: chat messages, profiles, cheating after a match).</summary>
public static class ReportKinds
{
    public const string Chat = "chat", Profile = "profile", Cheating = "cheating";
    public static readonly string[] All = [Chat, Profile, Cheating];

    /// <summary>Reasons allowed per kind (ids; the client renders the text).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Reasons = new Dictionary<string, string[]>
    {
        [Chat] = ["harassment", "hate", "sexual", "spam", "threats", "personal_info", "other"],
        [Profile] = ["offensive_name", "impersonation", "harassment", "other"],
        [Cheating] = ["engine", "multiple_accounts", "other"],
    };
}

/// <summary>POST /api/reports.</summary>
public sealed record ReportRequestDto(Guid Target, string Kind, string Reason, string? Note = null, long? MessageId = null, Guid? GameId = null);
/// <summary>Report accepted (Id of the stored or folded report).</summary>
public sealed record ReportResultDto(long Id);

/// <summary>
/// A penalty as the player sees it. Kind: warn | chat_mute | ban (shadow pool is never shown to the player).
/// EndsAtMs null on a ban = permanent.
/// </summary>
public sealed record PenaltyDto(long Id, string Kind, string Reason, long StartsAtMs, long? EndsAtMs, bool CanAppeal, string? AppealStatus);

/// <summary>GET /api/me/standing: penalties in force and the latest warning (so the client can show a notice once).</summary>
public sealed record StandingDto(PenaltyDto[] Active, PenaltyDto? LastWarning);

/// <summary>POST /api/appeals.</summary>
public sealed record AppealRequestDto(long PenaltyId, string Text);

/// <summary>Server → client: a DM was removed by moderation.</summary>
public sealed record MessageHiddenDto(long MessageId, Guid FriendId);
