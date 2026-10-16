using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Services;

/// <summary>Counters the ad policy carries between games (per app session).</summary>
public readonly record struct AdPolicyState(int GamesSinceInterstitial, int ConsecutiveLosses, int SessionGames = 0, long? LastShownMs = null);

/// <summary>
/// Pure interstitial policy (D17, mobile-game pacing): every N completed games, at least MinSecondsBetween since the last
/// one, never in the session's first FirstAfterGames games, never right after the 2nd loss in a row, never on match
/// routes or while queued. A break skipped for the cooldown or a loss streak is taken at the next eligible game.
/// </summary>
public static class AdPolicy
{
    /// <summary>Folds one completed game into the state and decides whether to show an interstitial now.</summary>
    public static (bool Show, AdPolicyState Next) OnGameCompleted(AdPolicyState state, GameResultForUser result, AdConfigDto config, bool blocked,
        long nowMs = 0)
    {
        if (result == GameResultForUser.Aborted) return (false, state);
        var losses = result == GameResultForUser.Loss ? state.ConsecutiveLosses + 1 : 0;
        var games = state.GamesSinceInterstitial + 1;
        var session = state.SessionGames + 1;
        var every = Math.Max(1, config.InterstitialEveryGames);
        var cooled = state.LastShownMs is not { } last || nowMs - last >= config.MinSecondsBetween * 1000L;
        var show = games >= every && session > config.FirstAfterGames && cooled
                   && !(config.SkipAfterTwoLosses && losses >= 2) && !blocked;
        return (show, new AdPolicyState(show ? 0 : games, losses, session, show ? nowMs : state.LastShownMs));
    }
}
