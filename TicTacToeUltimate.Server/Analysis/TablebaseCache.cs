using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Analysis;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Analysis;

/// <summary>
/// Builds tablebases on background threads (never blocking callers). Precomputes <c>Ttt:Analysis:Precompute</c> at start;
/// other rulesets build lazily on first <see cref="TryGet"/>. At most one build runs at a time.
/// Cache key: the ruleset with PieSwap normalized to None (BanCenter is not normalized).
/// </summary>
public sealed partial class TablebaseCache : ITablebaseCache, IHostedService, IDisposable
{
    private sealed class Entry
    {
        public volatile TablebaseStatus Status = TablebaseStatus.Warming;
        public Tablebase? Table;
    }

    private readonly ConcurrentDictionary<RuleSet, Entry> _entries = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<TablebaseCache> _log;
    private readonly long _maxStates;
    private int _disposed;
    private readonly IReadOnlyList<string> _precompute;

    /// <summary>Creates the cache from options.</summary>
    public TablebaseCache(IOptions<TttOptions> options, ILogger<TablebaseCache> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _log = log;
        _maxStates = options.Value.Analysis.MaxStates;
        _precompute = [.. options.Value.Analysis.Precompute];
    }

    /// <summary>PieSwap does not change the reachable placement positions, so it shares the table of the no-opening ruleset.</summary>
    public static RuleSet Normalize(RuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.Opening == OpeningRule.PieSwap ? rules with { Opening = OpeningRule.None } : rules;
    }

    /// <inheritdoc />
    public TablebaseStatus GetStatus(RuleSet rules)
    {
        var key = Normalize(rules);
        if (_entries.TryGetValue(key, out var e)) return e.Status;
        return Tablebase.CanBuild(key) ? TablebaseStatus.Warming : TablebaseStatus.Unavailable;
    }

    /// <inheritdoc />
    public bool TryGet(RuleSet rules, [NotNullWhen(true)] out Tablebase? tablebase)
    {
        var key = Normalize(rules);
        tablebase = null;
        if (_entries.TryGetValue(key, out var e))
        {
            if (e.Status != TablebaseStatus.Ready) return false;
            tablebase = e.Table!;
            return true;
        }
        Schedule(key);
        return false;
    }

    /// <summary>Starts (or joins) a background build; returns immediately.</summary>
    private void Schedule(RuleSet key)
    {
        if (!Tablebase.CanBuild(key))
        {
            _entries.TryAdd(key, new Entry { Status = TablebaseStatus.Unavailable });
            return;
        }
        var entry = new Entry();
        if (!_entries.TryAdd(key, entry)) return;
        _ = Task.Run(() => BuildAsync(key, entry));
    }

    private async Task BuildAsync(RuleSet key, Entry entry)
    {
        var ct = _cts.Token;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var started = Environment.TickCount64;
                var table = Tablebase.Build(key, _maxStates, ct);
                entry.Table = table;
                entry.Status = TablebaseStatus.Ready;
                LogBuilt(key.Id, table.StateCount, Environment.TickCount64 - started);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            entry.Status = TablebaseStatus.Unavailable;
        }
        catch (Exception ex) when (ex is TablebaseTooLargeException or ArgumentException or OutOfMemoryException)
        {
            entry.Status = TablebaseStatus.Unavailable;
            LogFailed(ex, key.Id);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var id in _precompute)
        {
            if (RuleSet.TryParse(id, out var rules)) Schedule(Normalize(rules));
            else LogBadId(id);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 0) _cts.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _cts.Dispose();
        _gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tablebase {RulesId} ready: {States} states in {Ms} ms")]
    private partial void LogBuilt(string rulesId, int states, long ms);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tablebase {RulesId} build failed")]
    private partial void LogFailed(Exception ex, string rulesId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analysis:Precompute contains an invalid ruleset id '{Id}'")]
    private partial void LogBadId(string id);
}
