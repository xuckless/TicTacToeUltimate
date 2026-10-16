using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.Shared.Services;

/// <summary>A mode a friendly game (invite link or targeted friend challenge) can use. Friendly games are unrated.
/// Name and Sub are localization keys resolved at render time.</summary>
public sealed record LobbyMode(TimeControl Tc, string NameKey, string SubKey, string Icon, string Guide, bool AccountOnly)
{
    public string Name => L.T(NameKey);
    public string Sub => L.T(SubKey);
}

/// <summary>The one list both the invite-link sheet and the friend-challenge picker show, so friends get the same options as links.</summary>
public static class LobbyModes
{
    public static readonly LobbyMode[] All =
    [
        new(TimeControl.Casual, "modeGrid.casual", "lobbyModes.casual_sub", "casual", "casual", false),
        new(TimeControl.ArenaCasual, "modeGrid.arena", "lobbyModes.arena_sub", "arena", "arena", false),
        new(TimeControl.OldSchool, "modeGrid.old_school", "lobbyModes.oldschool_sub", "play", "oldschool", false),
        new(TimeControl.Unlimited, "tc.unlimited", "lobbyModes.unlimited_sub", "unlimited", "unlimited", false),
        new(TimeControl.Daily, "friendRow.daily", "lobbyModes.daily_sub", "calendar", "daily", true),
    ];
}
