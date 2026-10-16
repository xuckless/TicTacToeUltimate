using TicTacToeUltimate.Engine.Clocks;
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
/// <summary>
/// Set while a deploy drains: no new live matches (queue, rematch, challenge) until the new server is up. Also tracks
/// long-lived connections (SignalR, bot sockets) so they can be closed once the drain ends; otherwise Kestrel would wait
/// for every client to leave on its own.
/// </summary>
public sealed class ServerDrain
{
    private volatile bool _draining;
    private readonly CancellationTokenSource _closeAll = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Microsoft.AspNetCore.SignalR.HubCallerContext> _hubs = new();

    /// <summary>True once shutdown started.</summary>
    public bool Draining => _draining;
    /// <summary>Cancelled when the drain is over and every connection should close.</summary>
    public CancellationToken CloseAll => _closeAll.Token;
    /// <summary>Starts draining.</summary>
    public void Begin() => _draining = true;
    /// <summary>Tracks a hub connection.</summary>
    public void Track(Microsoft.AspNetCore.SignalR.HubCallerContext ctx) => _hubs[ctx.ConnectionId] = ctx;
    /// <summary>Stops tracking a hub connection.</summary>
    public void Untrack(string connectionId) => _hubs.TryRemove(connectionId, out _);

    /// <summary>Closes every tracked connection; clients reconnect to the next server.</summary>
    public void CloseConnections()
    {
        _closeAll.Cancel();
        foreach (var ctx in _hubs.Values) ctx.Abort();
    }
}

/// <summary>
/// Stops first (registered last). Graceful deploy: flag the drain, wait up to Ttt:Shutdown:DrainSeconds for live (non-Daily)
/// matches to end so nobody loses a game to a restart, then shut the actors down. Kestrel and SignalR keep running meanwhile.
/// </summary>
public sealed class MatchShutdownService(MatchGateway gateway, ServerDrain drain, TttOptions options, TimeProvider time,
    ILogger<MatchShutdownService>? log = null) : IHostedLifecycleService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs before ANY hosted service stops, including Kestrel, so players keep their connections and can finish the game.
    /// </summary>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        drain.Begin();
        var budget = TimeSpan.FromSeconds(Math.Max(0, options.Shutdown.DrainSeconds));
        var start = time.GetUtcNow();
        while (time.GetUtcNow() - start < budget && !cancellationToken.IsCancellationRequested)
        {
            var live = gateway.LiveMatches().Where(m => m.Setup.TimeControl != TimeControl.Daily).ToList();
            if (live.Count == 0) break;
            log?.LogInformation("Draining: {Live} live match(es) still running ({Matches})", live.Count,
                string.Join(", ", live.Select(m => $"{m.Setup.TimeControl} ply {m.Ply} {string.Join(" v ", m.Setup.SeatA.DisplayName, m.Setup.SeatB.DisplayName)}")));
            try { await Task.Delay(TimeSpan.FromSeconds(2), time, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        drain.CloseConnections();
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => gateway.ShutdownAsync(cancellationToken);
}
