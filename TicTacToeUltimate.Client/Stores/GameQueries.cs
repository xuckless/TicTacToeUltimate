using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Stores;

/// <summary>Stateless REST queries.</summary>
public sealed class GameQueries(TttApiClient api) : IGameQueries
{
    /// <inheritdoc />
    public Task<ProfileDto?> GetProfileAsync(string username) => api.GetProfileAsync(username);
    /// <inheritdoc />
    public Task<IReadOnlyList<GameSummaryDto>> GetHistoryAsync(Guid userId, DateTimeOffset? before = null, int take = 20)
        => api.GetHistoryAsync(userId, before, take);
    /// <inheritdoc />
    public Task<GameRecordDto?> GetGameAsync(Guid gameId) => api.GetGameAsync(gameId);

    /// <inheritdoc />
    public async Task<(GameAnalysisDto? Analysis, string? Error)> GetAnalysisAsync(Guid gameId)
    {
        try { return (await api.GetAnalysisAsync(gameId), null); }
        catch (Exception ex) { return (null, ErrorText.Code(ex)); }
    }

    /// <inheritdoc />
    public Task<LeaderboardPageDto> GetLeaderboardAsync(RatingPool pool, LeaderboardScope scope, string? country, int page)
        => api.GetLeaderboardAsync(pool, scope, country, page);
    /// <inheritdoc />
    public Task<IReadOnlyList<DailyGameDto>> GetDailyGamesAsync() => api.GetDailyGamesAsync();
    /// <inheritdoc />
    public Task<IReadOnlyList<LiveMatchDto>> GetLiveFriendMatchesAsync() => api.GetLiveFriendMatchesAsync();
}
