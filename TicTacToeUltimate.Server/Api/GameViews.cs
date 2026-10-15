using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Progression;

namespace TicTacToeUltimate.Server.Platform;

/// <summary>Maps stored games to REST DTOs.</summary>
public sealed class GameViews(IPlayerCardService cards)
{
    /// <summary>Colour of a seat (seat A starts as X; a pie swap exchanges colours).</summary>
    public static Color ColorOf(int seat, bool swapped) => (seat == 0) != swapped ? Color.X : Color.O;

    /// <summary>Stored actions → DTOs (placement colours alternate X, O, … by placement count).</summary>
    public static ActionDto[] Actions(IReadOnlyList<StoredAction> actions)
    {
        var placements = 0;
        var list = new ActionDto[actions.Count];
        for (var i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            Color? color = null;
            int? cell = null;
            if (a.Action.Kind == ActionKind.Place)
            {
                color = placements++ % 2 == 0 ? Color.X : Color.O;
                cell = a.Action.Cell;
            }
            list[i] = new ActionDto(a.Ply, a.Seat, a.Action.Kind, cell, color, a.VanishedCell, a.ThinkMs, a.Auto);
        }
        return list;
    }

    /// <summary>Summaries from a user's perspective.</summary>
    public async Task<GameSummaryDto[]> SummariesAsync(Guid userId, IReadOnlyList<GameListItem> items, CancellationToken ct)
    {
        var result = new List<GameSummaryDto>();
        foreach (var g in items)
        {
            var seat = g.Setup.SeatA.UserId == userId ? 0 : 1;
            var opponent = seat == 0 ? g.Setup.SeatB.UserId : g.Setup.SeatA.UserId;
            var pool = RatingPools.For(g.Setup.TimeControl);
            var card = await cards.GetCardAsync(opponent, pool, ct);
            var snap = RatingPools.IsVisible(pool) ? g.Ratings.FirstOrDefault(r => r.Seat == seat) : null;
            result.Add(new GameSummaryDto(g.Id, g.Setup.Mode, g.Setup.TimeControl, card, ColorOf(seat, g.Swapped), PostGameProcessor.ResultFor(g.Result, seat),
                g.Result.Reason, snap is null ? null : (int)Math.Round(snap.After) - (int)Math.Round(snap.Before), g.Plies, g.EndedAt));
        }
        return result.ToArray();
    }

    /// <summary>Full record DTO.</summary>
    public async Task<GameRecordDto> RecordAsync(GameRecord g, CancellationToken ct)
    {
        var pool = RatingPools.For(g.Setup.TimeControl);
        var map = await cards.GetCardsAsync([g.Setup.SeatA.UserId, g.Setup.SeatB.UserId], pool, ct);
        var seats = new[]
        {
            new SeatDto(0, map[g.Setup.SeatA.UserId], ColorOf(0, g.Swapped), false, null),
            new SeatDto(1, map[g.Setup.SeatB.UserId], ColorOf(1, g.Swapped), false, null),
        };
        return new GameRecordDto(g.Id, g.Setup.Mode, g.Setup.TimeControl, g.Setup.Rated, RuleSetDto.From(g.Setup.Rules), seats, g.Swapped,
            Actions(g.Actions), g.Result, g.StartedAt, g.EndedAt);
    }
}
