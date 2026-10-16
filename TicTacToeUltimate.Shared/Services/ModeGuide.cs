namespace TicTacToeUltimate.Shared.Services;

/// <summary>One mode's explanation, shown behind a (?) button instead of long text in menus.</summary>
public sealed record ModeGuideEntry(string Key, string Title, string Icon, string Accent, string Summary, string[] Points);

/// <summary>Detailed explanations for every mode and option. Menus show a one-line subtitle; the (?) opens this.</summary>
public static class ModeGuide
{
    private static readonly Dictionary<string, ModeGuideEntry> Entries = new ModeGuideEntry[]
    {
        new("ranked", "Ranked", "ranked", "#7442ef", "Arena battles that count toward your rating and tier.",
        [
            "Health-bar Arena on the classic board: 3 pieces each, your oldest vanishes when you place a 4th.",
            "Completing a line shatters it and deals damage: 25, then 35, then 50 on a combo. Each player has 125 HP.",
            "2:00 round, 5 seconds per move. In the last 30 seconds damage is doubled (red alert).",
            "KO ends the game. At time-out, higher HP wins. Tied? Overtime: the next line wins, and whoever conceded the last line moves first.",
            "Rated (Glicko-2), with tiers from Bronze to Master. Running out of time loses. No rematches.",
            "Account required.",
        ]),
        new("daily", "Daily", "calendar", "#ff8a2a", "Correspondence games: one move per day.",
        [
            "Up to 24 hours per move; up to 10 games at once.",
            "Classic Ultimate rules with the swap (pie) rule.",
            "Rated in its own Daily rating. Missing the 24h deadline loses.",
            "Account required.",
        ]),
        new("casual", "Casual", "casual", "#2f9be0", "Tic Tac Toe Ultimate for fun, on the sudden-death clock.",
        [
            "3 pieces each. Placing your 4th removes your own oldest, and you can't replay that cell this turn. Any 3 in a row you can see wins.",
            "15 seconds per move. At 1:00 it drops to 10s, then shrinks by 1s every 12s down to 2s. The screen edge glows red in sudden death.",
            "If you run out of time, a random move is played for you.",
            "Unrated. Guests welcome. Rematches allowed.",
        ]),
        new("arena", "Arena", "arena", "#e0457b", "Health-bar battles, just for fun.",
        [
            "Same rules as Ranked Arena: lines shatter and deal damage, 125 HP, 2:00 rounds, ×2 damage in the last 30s.",
            "If you run out of time, a random move is played for you.",
            "Unrated. Guests welcome.",
        ]),
        new("oldschool", "Old school", "play", "#1fb5a0", "Plain tic-tac-toe, no vanishing pieces.",
        [
            "Pieces stay where they are. Three in a row wins.",
            "A full board with no line is a draw.",
            "Same casual clock as Casual. Unrated. Guests welcome.",
        ]),
        new("unlimited", "Unlimited", "unlimited", "#8a6cff", "Tic Tac Toe Ultimate with no clock.",
        [
            "Classic Ultimate rules: 3 pieces each, any visible line wins.",
            "Take as long as you like. Leaving for 10 minutes forfeits.",
            "Unrated. Guests welcome.",
        ]),
        new("passplay", "Pass & Play", "passplay", "#2ec27e", "Two players on one device. Works offline.",
        [
            "Hand the device back and forth; a banner shows whose turn it is.",
            "Choose the clock (Unlimited, Casual sudden death or Arena) and the rules (Classic, Old school or Legacy 7-piece).",
            "Colours swap on every rematch and a session score is kept.",
            "No rating, no streaks or quests, no ads during a game.",
        ]),
        new("classic", "Classic rules", "casual", "#2f9be0", "The Ultimate rule: 3 pieces each.",
        [
            "Each player holds at most 3 pieces.",
            "Placing your 4th first removes your own oldest piece, and you can't place on that cell this turn.",
            "Any 3 in a row you can see wins immediately.",
        ]),
        new("legacy7", "Legacy 7-piece", "history", "#8a6cff", "The original prototype rule.",
        [
            "Up to 7 pieces on the board in total; placing an 8th removes the oldest one.",
            "The faded piece that vanishes next can't complete a line.",
        ]),
        new("pie", "Pie rule", "rematch", "#7442ef", "Keeps the first move fair.",
        [
            "After X's first move, O may swap sides and take X's position instead.",
            "So X should open with a move that isn't too strong. Perfect play is then a draw.",
        ]),
        new("guided", "Guided mode", "guided", "#ffc63d", "Learning aid for casual games.",
        [
            "Highlights cells you must block to stop an immediate line.",
            "Casual, Unlimited, challenges and Pass & Play only. Never in Ranked, Daily or Arena.",
        ]),
    }.ToDictionary(e => e.Key);

    /// <summary>Entry by key, or null.</summary>
    public static ModeGuideEntry? Get(string key) => Entries.GetValueOrDefault(key);
}
