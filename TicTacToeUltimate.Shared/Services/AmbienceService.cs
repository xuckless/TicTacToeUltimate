using TicTacToeUltimate.Client.State;

namespace TicTacToeUltimate.Shared.Services;

/// <summary>Where the player is, for background ambience (wallpaper speed/tint, sudden-death screen edge).</summary>
public enum AmbienceMode { Menu, Match }

/// <summary>Shared ambience state. Match pages report sudden death and clock urgency; the wallpaper and edge glow render it.</summary>
public sealed class AmbienceService
{
    /// <summary>Menu or match.</summary>
    public AmbienceMode Mode { get; private set; } = AmbienceMode.Menu;
    /// <summary>True while the running match is in sudden death.</summary>
    public bool SuddenDeath { get; private set; }
    /// <summary>Urgency of the clock of the player to move.</summary>
    public ClockUrgency Urgency { get; private set; } = ClockUrgency.Idle;
    /// <summary>Raised when any value changes.</summary>
    public event Action? Changed;

    /// <summary>Updates the ambience; no-op when nothing changed.</summary>
    public void Set(AmbienceMode mode, bool suddenDeath = false, ClockUrgency urgency = ClockUrgency.Idle)
    {
        if (mode == Mode && suddenDeath == SuddenDeath && urgency == Urgency) return;
        (Mode, SuddenDeath, Urgency) = (mode, suddenDeath, urgency);
        Changed?.Invoke();
    }
}
