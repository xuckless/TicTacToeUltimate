using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.Shared.Services;

/// <summary>A mode a friendly game (invite link or targeted friend challenge) can use. Friendly games are unrated.</summary>
public sealed record LobbyMode(TimeControl Tc, string Name, string Sub, string Icon, string Guide, bool AccountOnly);

/// <summary>The one list both the invite-link sheet and the friend-challenge picker show, so friends get the same options as links.</summary>
public static class LobbyModes
{
    public static readonly LobbyMode[] All =
    [
        new(TimeControl.Casual, "Casual", "15s clock", "casual", "casual", false),
        new(TimeControl.ArenaCasual, "Arena", "Health bars", "arena", "arena", false),
        new(TimeControl.OldSchool, "Old school", "No vanishing", "play", "oldschool", false),
        new(TimeControl.Unlimited, "Unlimited", "No clock", "unlimited", "unlimited", false),
        new(TimeControl.Daily, "Daily", "1 move a day", "calendar", "daily", true),
    ];
}
