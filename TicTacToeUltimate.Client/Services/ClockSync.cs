namespace TicTacToeUltimate.Client.Services;

/// <summary>Estimates the server clock offset from ServerTime() round trips (best RTT among the last 8 samples).</summary>
public sealed class ClockSync(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Queue<(long Rtt, long Offset)> _samples = new();

    /// <summary>Current offset (server − local) in ms.</summary>
    public long OffsetMs { get; private set; }

    /// <summary>Number of samples kept.</summary>
    public int SampleCount { get { lock (_gate) return _samples.Count; } }

    /// <summary>Local epoch ms.</summary>
    public long LocalNowMs() => time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Estimated server epoch ms.</summary>
    public long ServerNowMs() => LocalNowMs() + OffsetMs;

    /// <summary>Adds one sample: local send time <paramref name="t0"/>, server time, local receive time <paramref name="t1"/>.</summary>
    public void AddSample(long t0, long serverMs, long t1)
    {
        lock (_gate)
        {
            var rtt = Math.Max(0, t1 - t0);
            var offset = serverMs - (t0 + t1) / 2;
            _samples.Enqueue((rtt, offset));
            while (_samples.Count > 8) _samples.Dequeue();
            OffsetMs = _samples.MinBy(s => s.Rtt).Offset;
        }
    }

    /// <summary>Takes <paramref name="count"/> samples using <paramref name="serverTime"/>.</summary>
    public async Task SampleAsync(Func<Task<long>> serverTime, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var t0 = LocalNowMs();
            var server = await serverTime().ConfigureAwait(false);
            AddSample(t0, server, LocalNowMs());
        }
    }
}
