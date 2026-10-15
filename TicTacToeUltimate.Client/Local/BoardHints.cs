using System.Collections.Immutable;
using TicTacToeUltimate.Engine;

namespace TicTacToeUltimate.Client.Local;

/// <summary>
/// Derived, rules-exact board hints shared by online and pass-and-play boards (engine only, no state of its own).
/// Launch rule (n3k3-cap3-v0-w1): each player holds at most 3 pieces; placing a 4th removes the mover's own oldest first,
/// that just-vacated cell is not playable this turn, and any visible line wins.
/// </summary>
public static class BoardHints
{
    /// <summary>Cell whose piece vanishes when the side to move places now (drawn faded + dashed); null if none.</summary>
    public static int? MoverVanish(GameState? state)
        => state is { Phase: GamePhase.AwaitingPlacement } s ? Rules.NextToVanish(s) : null;

    /// <summary>
    /// Under a per-player cap: the other side's oldest piece when they are at the cap, i.e. the piece that vanishes on
    /// their next placement (drawn with a subtler marker). Null for other removal kinds (a global FIFO's next piece
    /// depends on the mover's placement) or when not at the cap.
    /// </summary>
    public static int? WaitingVanish(GameState? state)
    {
        if (state is null || state.Phase == GamePhase.Ended || state.Rules.Removal != RemovalKind.PerPlayerCap) return null;
        var other = state.Phase == GamePhase.AwaitingSwapDecision ? Color.X : Opp(state.ToMove);
        int count = 0, first = -1;
        foreach (var p in state.Pieces)
        {
            if (p.Color != other) continue;
            if (first < 0) first = p.Cell;
            count++;
        }
        return count >= state.Rules.Window ? first : null;
    }

    /// <summary>True when the mover may not place on the cell its own vanishing piece frees this turn.</summary>
    public static bool VacatedBlocked(GameState? state) => state is not null && !state.Rules.VacatedCellPlayable && MoverVanish(state) is not null;

    /// <summary>
    /// "Block it" cells for <paramref name="me"/> to move: cells where the opponent would win on their next placement
    /// whatever else I play, computed with <see cref="Rules.ImmediateWins"/> on each position after my move (so a cell
    /// freed by my own vanishing piece counts, and nothing the rules would not honour is shown). Only cells I can
    /// actually play are returned. Empty when I can win right now (winning beats blocking) or it's not my placement.
    /// </summary>
    public static ImmutableArray<int> Threats(GameState? state, Color me)
    {
        if (state is not { Phase: GamePhase.AwaitingPlacement } s || s.ToMove != me) return [];
        if (!Rules.ImmediateWins(s, me).IsEmpty) return [];
        var legal = Rules.LegalCells(s);
        var set = new SortedSet<int>();
        var opp = Opp(me);
        foreach (var m in legal)
        {
            var after = Rules.Apply(s, GameAction.Place(m));
            if (!after.Ok || after.State.Phase != GamePhase.AwaitingPlacement) continue;
            foreach (var c in Rules.ImmediateWins(after.State, opp))
                if (legal.Contains(c)) set.Add(c);
        }
        return [.. set];
    }

    /// <summary>Hint for a click on a cell the mover can't play (null when the cell is playable).</summary>
    public static string? BlockedReason(GameState? state, int cell)
    {
        if (state is not { Phase: GamePhase.AwaitingPlacement } s) return null;
        if (Rules.LegalCells(s).Contains(cell)) return null;
        var mine = s.ToMove;
        if (MoverVanish(s) == cell && !s.Rules.VacatedCellPlayable)
            return $"Your faded {mine} vanishes when you place — you can't play there this turn";
        return s.At(cell) is null ? "You can't play there" : "That cell is taken";
    }

    /// <summary>Caption for the mover's faded piece; <paramref name="moverIsYou"/> false = an opponent is moving (online).</summary>
    public static string? MoverCaption(GameState? state, bool moverIsYou = true)
    {
        if (MoverVanish(state) is not int cell || state!.At(cell) is not Color c) return null;
        var blocked = !state.Rules.VacatedCellPlayable;
        return moverIsYou
            ? $"Your faded {c} vanishes when you place{(blocked ? " — you can't play there this turn" : "")}"
            : $"Their faded {c} vanishes when they place";
    }

    /// <summary>Caption for the waiting side's marked (⏱) piece; <paramref name="waitingIsYou"/> true = it is yours (online, their turn).</summary>
    public static string? WaitingCaption(GameState? state, bool waitingIsYou = false)
    {
        if (WaitingVanish(state) is not int cell || state!.At(cell) is not Color c) return null;
        return waitingIsYou
            ? $"⏱ Your oldest {c} vanishes on your next move"
            : $"⏱ {c}'s oldest vanishes when {c} places next";
    }

    private static Color Opp(Color c) => c == Color.X ? Color.O : Color.X;
}
