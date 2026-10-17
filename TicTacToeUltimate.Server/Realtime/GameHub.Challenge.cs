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
        ChallengeDto challenge;
        try { challenge = await Challenges.CreateAsync(Me, request, Context.ConnectionAborted); }
        catch (MatchAccessException e) { throw new HubException(e.Code); }
        if (challenge.ToUserId is { } to) await PostChallengeToChatAsync(to, challenge.Code);
        return challenge;
    }

    /// <summary>Friend challenges show up as a card in the chat thread; the friend gets the usual message notification or push.</summary>
    private async Task PostChallengeToChatAsync(Guid to, string code)
    {
        try
        {
            var me = Me;
            var posted = await services.GetRequiredService<TicTacToeUltimate.Server.Social.SocialService>().PostChallengeAsync(me.UserId, to, code, Context.ConnectionAborted);
            if (posted is null) return;
            var locale = await services.GetRequiredService<TicTacToeUltimate.Server.Abstractions.Persistence.IUserRepository>().GetLocaleAsync(to, Context.ConnectionAborted);
            var n = new NotificationDto(NotificationKind.Message, me.DisplayName, TicTacToeUltimate.Server.Localization.ServerText.Get(locale, "notify.challenge"), null, $"social/chat/{me.UserId}");
            if (PresenceTracker.IsOnline(to)) _ = services.GetRequiredService<IUserNotifier>().ToUserAsync(to, c => c.Notification(n));
            else _ = services.GetRequiredService<IPushSender>().SendAsync(to, n, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Posting challenge {Code} to chat failed", code); }
    }

    /// <summary>Accepts by code; first acceptor wins.</summary>
    public Task<AcceptChallengeResultDto> AcceptChallenge(string code) => Draining
        ? Task.FromResult(new AcceptChallengeResultDto(false, ErrorCodes.ServerUpdating, null))
        : Challenges.AcceptAsync(Me, code, Context.ConnectionAborted);

    /// <summary>Declines a targeted challenge.</summary>
    public Task<CommandAckDto> DeclineChallenge(string code) => Challenges.DeclineAsync(Me, code, Context.ConnectionAborted);

    /// <summary>Cancels one's own challenge.</summary>
    public Task<CommandAckDto> CancelChallenge(string code) => Challenges.CancelAsync(Me, code, Context.ConnectionAborted);
}
