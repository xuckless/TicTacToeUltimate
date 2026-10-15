using Microsoft.AspNetCore.SignalR;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.Realtime;

// WP2: challenges.
public sealed partial class GameHub
{
    private IChallengeService Challenges
    {
        get
        {
            try { return services.GetRequiredService<IChallengeService>(); }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "Challenge service unavailable");
                throw new HubException(ErrorCodes.Internal);
            }
        }
    }

    /// <summary>Creates a challenge link (accounts with a username).</summary>
    public async Task<ChallengeDto> CreateChallenge(CreateChallengeDto request)
    {
        try { return await Challenges.CreateAsync(Me, request, Context.ConnectionAborted); }
        catch (MatchAccessException e) { throw new HubException(e.Code); }
    }

    /// <summary>Accepts by code; first acceptor wins.</summary>
    public Task<AcceptChallengeResultDto> AcceptChallenge(string code) => Challenges.AcceptAsync(Me, code, Context.ConnectionAborted);

    /// <summary>Declines a targeted challenge.</summary>
    public Task<CommandAckDto> DeclineChallenge(string code) => Challenges.DeclineAsync(Me, code, Context.ConnectionAborted);

    /// <summary>Cancels one's own challenge.</summary>
    public Task<CommandAckDto> CancelChallenge(string code) => Challenges.CancelAsync(Me, code, Context.ConnectionAborted);
}
