using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Services;

/// <summary>Counters the ad policy carries between games.</summary>
public readonly record struct AdPolicyState(int GamesSinceInterstitial, int ConsecutiveLosses);

/// <summary>Pure interstitial policy (D17): every N completed games, never right after the 2nd loss in a row, never on match routes or while queued.</summary>
public static class AdPolicy
{
    /// <summary>Folds one completed game into the state and decides whether to show an interstitial now.</summary>
    public static (bool Show, AdPolicyState Next) OnGameCompleted(AdPolicyState state, GameResultForUser result, AdConfigDto config, bool blocked)
    {
        if (result == GameResultForUser.Aborted) return (false, state);
        var losses = result == GameResultForUser.Loss ? state.ConsecutiveLosses + 1 : 0;
        var games = state.GamesSinceInterstitial + 1;
        var every = Math.Max(1, config.InterstitialEveryGames);
        var show = games >= every && !(config.SkipAfterTwoLosses && losses >= 2) && !blocked;
        return (show, new AdPolicyState(show ? 0 : games, losses));
    }
}
