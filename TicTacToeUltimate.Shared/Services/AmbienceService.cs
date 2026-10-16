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
    /// <summary>Alert intensity classes for the screen-edge glow ("sd sd-2 crit", "sd sd-1 arena-red", ...); empty = none.</summary>
    public string Edge { get; private set; } = "";
    /// <summary>Raised when any value changes.</summary>
    public event Action? Changed;

    /// <summary>Updates the ambience; no-op when nothing changed.</summary>
    public void Set(AmbienceMode mode, bool suddenDeath = false, ClockUrgency urgency = ClockUrgency.Idle, string edge = "")
    {
        if (mode == Mode && suddenDeath == SuddenDeath && urgency == Urgency && edge == Edge) return;
        (Mode, SuddenDeath, Urgency, Edge) = (mode, suddenDeath, urgency, edge);
        Changed?.Invoke();
    }
}
