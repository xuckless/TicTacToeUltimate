using System.Text.Json;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Persistence;

namespace TicTacToeUltimate.Server.Moderation;

/// <summary>
/// Player reports (owner decision 2026-10-16): chat messages and profiles (accounts), cheating after a match (guests too).
/// Evidence is attached server-side. A reported message goes straight to the AI: a confirmed one is hidden, the sender gets a
/// strike and the report closes itself; everything else waits in the admin queue.
/// </summary>
public sealed class ReportService(IModerationRepository repo, IUserRepository users, IChatRepository chat, IGameRepository games,
    ModerationService moderation, AiModerator ai, IUserNotifier notifier, TimeProvider time, ILogger<ReportService> log)
{
    /// <summary>Validates, attaches evidence, stores (repeats fold into the open report). Returns (id, error code).</summary>
    public async Task<(long? Id, string? Error, int Status)> ReportAsync(TttUser me, ReportRequestDto req, CancellationToken ct)
    {
        if (!ReportKinds.Reasons.TryGetValue(req.Kind ?? "", out var reasons) || !reasons.Contains(req.Reason)) return (null, ErrorCodes.Validation, 400);
        if (req.Kind != ReportKinds.Cheating && me.Kind != AccountKind.Account) return (null, ErrorCodes.GuestNotAllowed, 403);
        if (req.Target == me.UserId || await users.GetAsync(req.Target, ct) is not { } target) return (null, ErrorCodes.Validation, 400);
        var note = req.Note?.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 500)] : null;

        object evidence;
        DirectMessageRecord? message = null;
        switch (req.Kind)
        {
            case ReportKinds.Chat:
                if (req.MessageId is not { } mid || await chat.GetAsync(mid, ct) is not { } m || m.From != req.Target || m.To != me.UserId)
                    return (null, ErrorCodes.Validation, 400);
                message = m;
                var context = await chat.PageAsync(me.UserId, req.Target, m.Id + 6, 12, ct);
                evidence = new { message = new { m.Id, m.Body, m.SentAt }, context = context.OrderBy(x => x.Id).Select(x => new { x.Id, from = x.From == me.UserId ? "reporter" : "reported", x.Body }) };
                break;
            case ReportKinds.Cheating:
                if (req.GameId is not { } gid || await games.GetAsync(gid, ct) is not { } g
                    || !(Seats(g).Contains(me.UserId) && Seats(g).Contains(req.Target))) return (null, ErrorCodes.Validation, 400);
                evidence = new { game = gid, mode = g.Setup.Mode.ToString(), timeControl = g.Setup.TimeControl.ToString(), plies = g.Actions.Count, g.Result.Outcome };
                break;
            default:
                evidence = new { target.Username, target.DisplayName, target.AvatarId, target.Country };
                break;
        }

        var report = await repo.AddReportAsync(new ReportRecord(0, me.UserId, req.Target, req.Kind!, req.Reason, note, req.MessageId, req.GameId,
            JsonSerializer.Serialize(evidence), "open", null, time.GetUtcNow(), null, null), ct);
        if (message is not null && report.AiVerdictJson is null) _ = ReviewMessageAsync(report, message);
        return (report.Id, null, 200);
    }

    private static Guid[] Seats(GameRecord g) => [g.Setup.SeatA.UserId, g.Setup.SeatB.UserId];

    private async Task ReviewMessageAsync(ReportRecord report, DirectMessageRecord message)
    {
        try
        {
            var verdict = await ai.CheckAsync(message.Body, isUsername: false, CancellationToken.None);
            if (verdict is null) return;
            var json = JsonSerializer.Serialize(verdict);
            if (verdict is not { Abusive: true, Severity: >= 2 }) { await repo.UpdateReportAsync(report.Id, "open", null, json, null, CancellationToken.None); return; }
            await chat.HideAsync(message.Id, time.GetUtcNow(), CancellationToken.None);
            try
            {
                await notifier.ToUserAsync(message.From, c => c.MessageHidden(new MessageHiddenDto(message.Id, message.To)));
                await notifier.ToUserAsync(message.To, c => c.MessageHidden(new MessageHiddenDto(message.Id, message.From)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "MessageHidden push failed"); }
            var p = await moderation.StrikeAsync(message.From, "report_confirmed", message.Body, verdict, CancellationToken.None, report.Id);
            await repo.UpdateReportAsync(report.Id, "actioned", $"auto: {verdict.Category} → {PenaltyKinds.ToDb(p.Kind)}", json, time.GetUtcNow(), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "AI review of report {Id} failed", report.Id); }
    }
}
