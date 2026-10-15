using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Matches;

/// <summary>Every Daily:SweepSeconds: ListDueAsync(now) → activate → deadline check (§6.5).</summary>
public sealed class DailyDeadlineSweeper(MatchGateway gateway, TimeProvider time, TttOptions options, ILogger<DailyDeadlineSweeper> log) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Daily.SweepSeconds)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await SweepOnceAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Daily sweep failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One sweep.</summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        if (gateway.Runtime.DailyRepo is not { } repo) return;
        foreach (var id in await repo.ListDueAsync(time.GetUtcNow(), ct).ConfigureAwait(false))
        {
            try { await gateway.CheckDeadlineAsync(id).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "Daily deadline check failed for {MatchId}", id); }
        }
    }
}

/// <summary>On host shutdown, live non-Daily matches end Aborted ("server_restart").</summary>
public sealed class MatchShutdownService(MatchGateway gateway) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => gateway.ShutdownAsync(cancellationToken);
}
