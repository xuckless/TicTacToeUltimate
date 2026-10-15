using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Presence;

namespace TicTacToeUltimate.Server.Tests.Presence;

public class PresenceTrackerTests
{
    private sealed class Sink : IPresenceSink
    {
        public ConcurrentQueue<PresenceDto> Seen { get; } = new();
        public ValueTask OnPresenceChangedAsync(PresenceDto presence, CancellationToken ct) { Seen.Enqueue(presence); return ValueTask.CompletedTask; }
    }

    [Fact]
    public void OnlineImmediatelyOfflineAfterDebounce()
    {
        var time = new FakeTimeProvider();
        var sink = new Sink();
        var p = new PresenceTracker(time, sink, new UserActivityRegistry(), NullLogger<PresenceTracker>.Instance);
        var u = Guid.NewGuid();
        p.Connect(u, "c1");
        Assert.Equal(PresenceStatus.Online, sink.Seen.Last().Status);
        p.Disconnect(u, "c1");
        time.Advance(TimeSpan.FromMilliseconds(2900));
        Assert.Single(sink.Seen);
        Assert.False(p.IsOnline(u));
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(PresenceStatus.Offline, sink.Seen.Last().Status);
    }

    [Fact]
    public void ReconnectWithinDebounceStaysOnline()
    {
        var time = new FakeTimeProvider();
        var sink = new Sink();
        var p = new PresenceTracker(time, sink, new UserActivityRegistry(), NullLogger<PresenceTracker>.Instance);
        var u = Guid.NewGuid();
        p.Connect(u, "c1");
        p.Disconnect(u, "c1");
        time.Advance(TimeSpan.FromSeconds(1));
        p.Connect(u, "c2");
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.All(sink.Seen, s => Assert.Equal(PresenceStatus.Online, s.Status));
        Assert.True(p.IsOnline(u));
    }

    [Fact]
    public void InGameFollowsActivity()
    {
        var time = new FakeTimeProvider();
        var sink = new Sink();
        var activity = new UserActivityRegistry();
        var p = new PresenceTracker(time, sink, activity, NullLogger<PresenceTracker>.Instance);
        var u = Guid.NewGuid();
        var m = Guid.NewGuid();
        p.Connect(u, "c1");
        Assert.True(activity.TryStartMatch(u, m));
        Assert.Equal(new PresenceDto(u, PresenceStatus.InGame, m), p.Get(u));
        Assert.Equal(PresenceStatus.InGame, sink.Seen.Last().Status);
        activity.EndMatch(u, m);
        Assert.Equal(PresenceStatus.Online, sink.Seen.Last().Status);
        p.SetTimeZone(u, "Europe/Berlin");
        Assert.Equal("Europe", p.Region(u));
    }

    [Fact]
    public void RttUsesMinOfLastSamples()
    {
        var time = new FakeTimeProvider();
        var rtt = new RttTracker(time, Options.Create(new TttOptions()));
        var u = Guid.NewGuid();
        (int RttMs, int HalfRttMs)? last = null;
        foreach (var ms in new[] { 200, 100, 300, 400, 500, 600 })
        {
            var id = rtt.TryStartPing(u, TimeSpan.Zero)!.Value;
            time.Advance(TimeSpan.FromMilliseconds(ms));
            last = rtt.OnPong(u, id);
        }
        Assert.Equal(600, last!.Value.RttMs);
        Assert.Equal(50, last.Value.HalfRttMs);    // last 5 = 100..600 -> min 100 -> 50
        Assert.Null(rtt.OnPong(u, 999));
        Assert.Null(rtt.TryStartPing(u, TimeSpan.FromSeconds(2)));
    }
}
