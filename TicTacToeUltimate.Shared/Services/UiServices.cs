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
    /// <summary>"Gold II", "Master".</summary>
    public static string TierName(TierDto? t) => t is null ? "Unranked"
        : t.Tier == Tier.Master || t.Division <= 0 ? t.Tier.ToString() : $"{t.Tier} {Roman(t.Division)}";

    /// <summary>CSS class for a tier.</summary>
    public static string TierClass(Tier t) => "tier-" + t.ToString().ToLowerInvariant();

    /// <summary>Roman numerals for divisions 1..3.</summary>
    public static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>Next tier name for "48 to Gold I".</summary>
    public static string NextTierName(TierDto t) => t.Tier == Tier.Master ? "Master"
        : t.Division > 1 ? $"{t.Tier} {Roman(t.Division - 1)}"
        : t.Tier + 1 == Tier.Master ? "Master" : $"{t.Tier + 1} III";

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
        Engine.Clocks.TimeControl.Ranked => "Ranked",
        Engine.Clocks.TimeControl.CasualX2 => "Casual x2",
        Engine.Clocks.TimeControl.CasualX4 => "Casual x4",
        Engine.Clocks.TimeControl.Unlimited => "Unlimited",
        _ => "Daily",
    };

    /// <summary>Header label for a match ("Ranked · Sudden death").</summary>
    public static string ModeLabel(MatchMode mode, Engine.Clocks.TimeControl tc) => mode switch
    {
        MatchMode.Ranked => "Ranked · Sudden death",
        MatchMode.Challenge => "Challenge · " + TimeControlName(tc),
        _ => TimeControlName(tc),
    };

    /// <summary>Initial for avatars.</summary>
    public static string Initial(string? name) => string.IsNullOrEmpty(name) ? "?" : char.ToUpperInvariant(name[0]).ToString();

    /// <summary>Signed delta with a real minus sign.</summary>
    public static string Delta(int d) => d > 0 ? $"+{d}" : d < 0 ? $"−{-d}" : "±0";

    /// <summary>"Line"/"Timeout" etc. as player-facing copy.</summary>
    public static string ReasonText(EndReason r) => r switch
    {
        EndReason.Line => "Three in a row",
        EndReason.Timeout => "On time",
        EndReason.Resign => "By resignation",
        EndReason.Abandoned => "Opponent left",
        EndReason.Draw => "Draw",
        _ => "Aborted",
    };
}
