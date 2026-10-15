using Microsoft.AspNetCore.SignalR.Client;

namespace TicTacToeUltimate.Client.Services;

/// <summary>Reconnect schedule: 0, 1, 2, 5, 10 s, then 10 s ± 20% forever.</summary>
public sealed class RetryForeverPolicy(Func<double>? random = null) : IRetryPolicy
{
    private static readonly TimeSpan[] Initial =
        [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    /// <summary>Delay before retry number <paramref name="previousRetryCount"/> (0-based).</summary>
    public TimeSpan Delay(long previousRetryCount)
    {
        if (previousRetryCount < Initial.Length) return Initial[previousRetryCount];
        var jitter = (_random() * 2 - 1) * 0.2;          // [-0.2, 0.2)
        return TimeSpan.FromMilliseconds(10_000 * (1 + jitter));
    }

    /// <inheritdoc />
    public TimeSpan? NextRetryDelay(RetryContext retryContext) => Delay(retryContext.PreviousRetryCount);
}
