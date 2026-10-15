using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Analysis;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;

namespace TicTacToeUltimate.Server.Analysis;

/// <summary>Maps a stored game to <see cref="GameAnalysisDto"/> via <see cref="GameAnalyzer"/>.</summary>
public sealed partial class AnalysisService : IAnalysisService
{
    private readonly ITablebaseCache cache;
    private readonly ILogger<AnalysisService> _log;

    /// <summary>Creates the service.</summary>
    public AnalysisService(ITablebaseCache cache, ILogger<AnalysisService> log)
    {
        this.cache = cache;
        _log = log;
    }

    /// <inheritdoc />
    public Task<GameAnalysisDto?> AnalyzeAsync(GameRecord game, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(game);
        ct.ThrowIfCancellationRequested();
        var rules = game.Setup.Rules;
        if (!cache.TryGet(rules, out var tablebase)) return Task.FromResult<GameAnalysisDto?>(null);
        try
        {
            return Task.FromResult<GameAnalysisDto?>(Map(game, tablebase));
        }
        catch (Exception ex) when (ex is InvalidActionException or InvalidOperationException)
        {
            LogBadGame(ex, game.Id);
            return Task.FromResult<GameAnalysisDto?>(null);
        }
    }

    /// <summary>Pure mapping (used by tests).</summary>
    public static GameAnalysisDto Map(GameRecord game, Tablebase tablebase)
    {
        ArgumentNullException.ThrowIfNull(game);
        var rules = game.Setup.Rules;
        var actions = game.Actions.Select(a => a.Action).ToList();
        var plies = GameAnalyzer.Analyze(tablebase, rules, actions);
        var blunders = new int[2];
        var dtos = new PlyAnalysisDto[plies.Length];
        for (int i = 0; i < plies.Length; i++)
        {
            var p = plies[i];
            int seat = game.Actions[i].Seat;
            if (p.Class == MoveClass.Blunder && seat is 0 or 1) blunders[seat]++;
            dtos[i] = new PlyAnalysisDto(p.Ply, seat, p.Class, new PositionValueDto(p.Before.Value, p.Before.Distance),
                [.. p.BestActions.Select(Notation.Token)]);
        }
        return new GameAnalysisDto(game.Id, rules.Id, dtos, blunders);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Game {GameId} could not be analyzed")]
    private partial void LogBadGame(Exception ex, Guid gameId);
}
