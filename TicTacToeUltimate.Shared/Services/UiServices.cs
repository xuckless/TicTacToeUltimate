using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Shared.Services;

/// <summary>Stub ad overlay (§8.2 <see cref="IAdOverlay"/>): <see cref="Layout.AdOverlayHost"/> renders the modal and countdown.</summary>
public sealed class AdOverlayService : IAdOverlay
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _pending;

    /// <summary>The request being shown, or null.</summary>
    public AdOverlayRequest? Current { get; private set; }

    /// <summary>Raised when <see cref="Current"/> changes.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public async Task<bool> ShowAsync(AdOverlayRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        TaskCompletionSource<bool> tcs;
        lock (_gate)
        {
            _pending?.TrySetResult(false);
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            Current = request;
        }
        Changed?.Invoke();
        using (ct.Register(() => Complete(false)))
            return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Completes the current ad: true when the countdown finished, false when closed early.</summary>
    public void Complete(bool completed)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_gate)
        {
            tcs = _pending;
            _pending = null;
            Current = null;
        }
        if (tcs is null) return;
        tcs.TrySetResult(completed);
        Changed?.Invoke();
    }
}

/// <summary>Toast severity.</summary>
public enum ToastKind { Info, Success, Error }

/// <summary>One toast.</summary>
public sealed record ToastMessage(Guid Id, string Text, ToastKind Kind);

/// <summary>Transient notifications rendered by <see cref="Layout.ToastHost"/>.</summary>
public sealed class ToastService
{
    private readonly List<ToastMessage> _items = [];
    private readonly object _gate = new();

    /// <summary>Visible toasts.</summary>
    public IReadOnlyList<ToastMessage> Items { get { lock (_gate) return _items.ToArray(); } }

    /// <summary>Raised on change.</summary>
    public event Action? Changed;

    /// <summary>Shows a toast for <paramref name="ms"/> milliseconds.</summary>
    public void Show(string text, ToastKind kind = ToastKind.Info, int ms = 3000)
    {
        var t = new ToastMessage(Guid.NewGuid(), text, kind);
        lock (_gate) _items.Add(t);
        Changed?.Invoke();
        _ = DismissLaterAsync(t.Id, ms);
    }

    /// <summary>Removes a toast.</summary>
    public void Dismiss(Guid id)
    {
        lock (_gate) _items.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }

    private async Task DismissLaterAsync(Guid id, int ms)
    {
        await Task.Delay(ms).ConfigureAwait(false);
        Dismiss(id);
    }
}

/// <summary>Display helpers shared by components.</summary>
public static class Ui
{
    /// <summary>In-feed ads in lists: one after every 8th row (owner decision 2026-10-16).</summary>
    public static bool FeedAdAfter(int index) => index % 8 == 7;

    /// <summary>"Gold II", "Master".</summary>
    public static string TierName(TierDto? t) => t is null ? L.T("tier.unranked")
        : t.Tier == Tier.Master || t.Division <= 0 ? TierWord(t.Tier) : TierWithDivision(t.Tier, t.Division);

    /// <summary>Localized tier name without a division ("Gold").</summary>
    public static string TierWord(Tier t) => L.T("tier." + t.ToString().ToLowerInvariant());

    private static string TierWithDivision(Tier t, int division) => L.F("tier.with_division", TierWord(t), Roman(division));

    /// <summary>CSS class for a tier.</summary>
    public static string TierClass(Tier t) => "tier-" + t.ToString().ToLowerInvariant();

    /// <summary>Roman numerals for divisions 1..3.</summary>
    public static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>Next tier name for "48 to Gold I".</summary>
    public static string NextTierName(TierDto t) => t.Tier == Tier.Master ? TierWord(Tier.Master)
        : t.Division > 1 ? TierWithDivision(t.Tier, t.Division - 1)
        : t.Tier + 1 == Tier.Master ? TierWord(Tier.Master) : TierWithDivision(t.Tier + 1, 3);

    /// <summary>m:ss, rounding seconds up (a clock showing 0:00 means time is out).</summary>
    public static string Clock(int? ms)
    {
        if (ms is null) return "--:--";
        var s = (int)Math.Ceiling(Math.Max(0, ms.Value) / 1000.0);
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>m:ss, rounding down (elapsed time).</summary>
    public static string Elapsed(int ms)
    {
        var s = Math.Max(0, ms) / 1000;
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>Whole seconds, rounded.</summary>
    public static int Seconds(int ms) => (int)Math.Round(ms / 1000.0);

    /// <summary>Human label for a time control.</summary>
    public static string TimeControlName(Engine.Clocks.TimeControl tc) => tc switch
    {
        Engine.Clocks.TimeControl.Ranked => L.T("tc.ranked"),
        Engine.Clocks.TimeControl.CasualX2 => L.T("tc.casual_x2"),
        Engine.Clocks.TimeControl.CasualX4 => L.T("tc.casual_x4"),
        Engine.Clocks.TimeControl.Casual => L.T("modeGrid.casual"),
        Engine.Clocks.TimeControl.OldSchool => L.T("modeGrid.old_school"),
        Engine.Clocks.TimeControl.Unlimited => L.T("tc.unlimited"),
        Engine.Clocks.TimeControl.ArenaCasual => L.T("modeGrid.arena"),
        Engine.Clocks.TimeControl.ArenaRanked => L.T("statGrid.ranked"),
        _ => L.T("friendRow.daily"),
    };

    /// <summary>Header label for a match ("Ranked · Sudden death").</summary>
    public static string ModeLabel(MatchMode mode, Engine.Clocks.TimeControl tc) => mode switch
    {
        MatchMode.Ranked => L.T("mode.ranked"),
        MatchMode.Arena => L.T("mode.arena"),
        MatchMode.ArenaRanked => L.T("mode.arena_ranked"),
        MatchMode.Challenge => L.F("mode.challenge", TimeControlName(tc)),
        _ => TimeControlName(tc),
    };

    /// <summary>Initial for avatars.</summary>
    public static string Initial(string? name) => string.IsNullOrEmpty(name) ? "?" : char.ToUpperInvariant(name[0]).ToString();

    /// <summary>Signed delta with a real minus sign.</summary>
    public static string Delta(int d) => d > 0 ? $"+{d}" : d < 0 ? $"−{-d}" : "±0";

    /// <summary>"Line"/"Timeout" etc. as player-facing copy.</summary>
    /// <summary>Shown when the server refuses new games because a deploy is draining.</summary>
    public static string ServerUpdatingText => L.T("error.server_updating");

    /// <summary>Toast text when a rewarded ad didn't pay out.</summary>
    public static string RewardFailText(RewardClaimResultDto r, string fallback) => r.Error switch
    {
        ErrorCodes.AdBlocked => L.T("error.ad_blocked"),
        ErrorCodes.RewardRequired => fallback,
        null => fallback,
        _ => L.T("ui.reward_failed"),
    };

    /// <summary>One toast line for a server error code (the pages used to swallow these or show a generic failure).</summary>
    public static string ErrorToastText(string? code, string fallback) => code switch
    {
        ErrorCodes.GuestNotAllowed => L.T("error.guest_not_allowed"),
        ErrorCodes.UsernameRequired => L.T("error.username_required"),
        ErrorCodes.AlreadyInMatch => L.T("error.already_in_match"),
        ErrorCodes.AlreadyInQueue => L.T("error.already_in_queue"),
        ErrorCodes.DailyLimit => L.T("error.daily_limit"),
        ErrorCodes.NotFriends => L.T("error.not_friends"),
        ErrorCodes.Blocked => L.T("error.blocked"),
        ErrorCodes.CannotChallengeSelf => L.T("error.cannot_challenge_self"),
        ErrorCodes.ChallengeExpired => L.T("error.challenge_expired"),
        ErrorCodes.ChallengeTaken => L.T("error.challenge_taken"),
        ErrorCodes.RateLimited => L.T("error.rate_limited"),
        ErrorCodes.ServerUpdating => ServerUpdatingText,
        ErrorCodes.ChatMuted => L.T("error.chat_muted"),
        ErrorCodes.MessageBlocked => L.T("error.message_blocked"),
        ErrorCodes.UsernameBlocked => L.T("error.username_blocked"),
        ErrorCodes.Banned => L.T("error.banned"),
        "offline" => L.T("error.offline"),
        _ => fallback,
    };

    /// <summary>Reason line with the viewer's perspective: "Opponent left" is wrong when you were the one who left.</summary>
    public static string ReasonText(EndReason r, GameResultForUser? mine) => r == EndReason.Abandoned && mine == GameResultForUser.Loss ? L.T("reason.you_left") : ReasonText(r);

    public static string ReasonText(EndReason r) => r switch
    {
        EndReason.Line => L.T("reason.line"),
        EndReason.Timeout => L.T("reason.timeout"),
        EndReason.Resign => L.T("reason.resign"),
        EndReason.Abandoned => L.T("reason.abandoned"),
        EndReason.Draw => L.T("reason.draw"),
        EndReason.Knockout => L.T("reason.knockout"),
        EndReason.Decision => L.T("reason.decision"),
        EndReason.Overtime => L.T("reason.overtime"),
        _ => L.T("reason.aborted"),
    };

    /// <summary>Player-facing text of an emote: phrase emotes are localized ("emote.&lt;id&gt;"); Bert emotes keep their "bert:&lt;mood&gt;" token.</summary>
    public static string EmoteText(EmoteDto e) => EmoteText(e.Id, e.Text);

    /// <summary>Emote text by id (e.g. from <c>EmoteView.EmoteId</c>), falling back to <paramref name="fallbackText"/> when there is no translation.</summary>
    public static string EmoteText(string emoteId, string fallbackText)
    {
        if (fallbackText.StartsWith("bert:", StringComparison.Ordinal)) return fallbackText;
        var key = "emote." + emoteId;
        var s = L.T(key);
        return s == key ? fallbackText : s;
    }
}
