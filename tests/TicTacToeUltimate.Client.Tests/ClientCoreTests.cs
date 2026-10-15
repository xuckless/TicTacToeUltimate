using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Time.Testing;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Client.Stores;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.Client.Tests;

public class MatchStoreTests
{
    [Fact]
    public async Task Applies_envelopes_in_seq_order_and_drops_duplicates()
    {
        var h = new MatchHarness();
        h.Rt.OnJoin = _ => Task.FromResult(h.Snapshot(seq: 5));
        await h.Store.OpenAsync(MatchHarness.MatchId);

        h.Rt.Push(h.Env(7, new SpectatorCountChanged(3)));            // out of order: buffered
        Assert.Equal(5, h.Store.State.Matches[MatchHarness.MatchId].Snapshot.Seq);
        h.Rt.Push(h.Env(6, MatchHarness.Placed(0, 0, 4, Color.X)));   // fills the gap, drains 7
        h.Rt.Push(h.Env(5, new SpectatorCountChanged(99)));           // stale: dropped
        h.Rt.Push(h.Env(6, new SpectatorCountChanged(99)));           // duplicate: dropped

        var v = h.Store.State.Matches[MatchHarness.MatchId];
        Assert.Equal(7, v.Snapshot.Seq);
        Assert.Equal(3, v.Snapshot.SpectatorCount);
        Assert.Single(v.Snapshot.Actions);
        Assert.Equal(1, v.Snapshot.Ply);
        Assert.False(v.IsMyTurn);                                    // no TurnStarted yet
        Assert.Equal(1, h.Rt.JoinCalls);
    }

    [Fact]
    public async Task Gap_buffers_two_seconds_then_resyncs()
    {
        var h = new MatchHarness();
        h.Rt.OnJoin = _ => Task.FromResult(h.Snapshot(seq: h.Rt.JoinCalls == 1 ? 5 : 9));
        await h.Store.OpenAsync(MatchHarness.MatchId);

        h.Rt.Push(h.Env(8, new SpectatorCountChanged(2)));
        h.Time.Advance(TimeSpan.FromMilliseconds(1900));
        Assert.Equal(1, h.Rt.JoinCalls);
        h.Time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(2, h.Rt.JoinCalls);

        var v = h.Store.State.Matches[MatchHarness.MatchId];
        Assert.Equal(9, v.Snapshot.Seq);
        Assert.False(v.Resyncing);
        h.Rt.Push(h.Env(10, new SpectatorCountChanged(4)));
        Assert.Equal(4, h.Store.State.Matches[MatchHarness.MatchId].Snapshot.SpectatorCount);
    }

    [Fact]
    public async Task Rejected_placement_rolls_back_and_plays_error()
    {
        var h = new MatchHarness();
        h.Rt.OnJoin = _ => Task.FromResult(h.Snapshot());
        var ack = new TaskCompletionSource<MoveAckDto>();
        h.Rt.OnSubmit = _ => ack.Task;
        await h.Store.OpenAsync(MatchHarness.MatchId);
        Assert.True(h.Store.State.Matches[MatchHarness.MatchId].IsMyTurn);

        var place = h.Store.PlaceAsync(MatchHarness.MatchId, 4);
        Assert.Equal(4, h.Store.State.Matches[MatchHarness.MatchId].PendingCell);
        Assert.True(h.Rt.Submitted.TryPeek(out var sent));
        Assert.Equal(0, sent.ExpectedPly);
        Assert.Equal(4, sent.Cell);
        Assert.NotEqual(Guid.Empty, sent.ActionId);

        ack.SetResult(new MoveAckDto(false, ErrorCodes.StalePly, 5, 1));
        await place;
        var v = h.Store.State.Matches[MatchHarness.MatchId];
        Assert.Null(v.PendingCell);
        Assert.Equal(ErrorCodes.StalePly, v.LastError);
        Assert.Contains(HapticCue.Error, h.Haptics.Played);
    }

    [Fact]
    public async Task Accepted_placement_clears_pending_on_ack()
    {
        var h = new MatchHarness();
        h.Rt.OnJoin = _ => Task.FromResult(h.Snapshot());
        await h.Store.OpenAsync(MatchHarness.MatchId);
        await h.Store.PlaceAsync(MatchHarness.MatchId, 2);
        Assert.Null(h.Store.State.Matches[MatchHarness.MatchId].PendingCell);
        Assert.Null(h.Store.State.Matches[MatchHarness.MatchId].LastError);
    }

    [Fact]
    public async Task Clock_urgency_thresholds()
    {
        var h = new MatchHarness();
        ClockDto ClockWith(long remaining) => new(TimeControl.Ranked, h.Now - 1000, h.Now + remaining, 8000, 0, false, null, null, [3000, 3000]);
        foreach (var (remaining, expected) in new[] { (6000L, ClockUrgency.Normal), (5000L, ClockUrgency.Warning), (3001L, ClockUrgency.Warning), (3000L, ClockUrgency.Critical), (0L, ClockUrgency.Critical) })
        {
            h.Rt.OnJoin = _ => Task.FromResult(h.Snapshot(clock: ClockWith(remaining)));
            await h.Store.OpenAsync(MatchHarness.MatchId);
            var c = h.Store.GetClock(MatchHarness.MatchId)!;
            Assert.Equal(expected, c.Urgency);
            Assert.Equal((int)remaining, c.RemainingMs);
        }
        var idle = h.Snapshot(clock: new ClockDto(TimeControl.Ranked, null, null, 8000, 0, false, null, null, [3000, 3000]));
        h.Rt.OnJoin = _ => Task.FromResult(idle);
        await h.Store.OpenAsync(MatchHarness.MatchId);
        Assert.Equal(ClockUrgency.Idle, h.Store.GetClock(MatchHarness.MatchId)!.Urgency);
        h.Store.Dispose();
    }

    [Fact]
    public async Task Clock_next_step_8s_to_7s_in_5s_at_game_1_31()
    {
        var h = new MatchHarness();
        var clock = new ClockDto(TimeControl.Ranked, h.Now, h.Now + 8000, 8000, 91_000, true, 7000, 96_000, [3000, 3000]);
        h.Rt.OnJoin = _ => Task.FromResult(h.Snapshot(clock: clock));
        await h.Store.OpenAsync(MatchHarness.MatchId);

        var c = h.Store.GetClock(MatchHarness.MatchId)!;
        Assert.Equal(8000, c.AllowanceMs);
        Assert.Equal(7000, c.NextAllowanceMs);
        Assert.Equal(5000, c.NextStepInMs);
        Assert.Equal(91_000, c.GameElapsedMs);
        Assert.True(c.SuddenDeath);

        h.Time.Advance(TimeSpan.FromSeconds(2));
        c = h.Store.GetClock(MatchHarness.MatchId)!;
        Assert.Equal(3000, c.NextStepInMs);
        Assert.Equal(6000, c.RemainingMs);
        Assert.Equal(MatchStore.HeartbeatBpm(8), h.Sound.Heartbeat);   // sudden death on my turn
        h.Store.Dispose();
    }

    [Theory]
    [InlineData(10, 70)]
    [InlineData(15, 70)]
    [InlineData(9, 80)]
    [InlineData(8, 90)]
    [InlineData(5, 120)]
    [InlineData(2, 150)]
    [InlineData(1, 150)]
    public void Heartbeat_bpm_mapping(int allowanceSeconds, int bpm) => Assert.Equal(bpm, MatchStore.HeartbeatBpm(allowanceSeconds));
}

public class ClockSyncTests
{
    [Fact]
    public void Offset_uses_smallest_rtt_sample_among_last_eight()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
        var sync = new ClockSync(time);
        // true offset = +500 ms; jittered asymmetric delays distort high-RTT samples
        sync.AddSample(1000, 1000 + 500 + 90, 1000 + 120);   // rtt 120 -> offset 530
        sync.AddSample(2000, 2000 + 500 + 10, 2000 + 20);    // rtt 20  -> offset 500
        sync.AddSample(3000, 3000 + 500 + 70, 3000 + 80);    // rtt 80  -> offset 530
        Assert.Equal(500, sync.OffsetMs);

        for (var i = 0; i < 8; i++) sync.AddSample(10_000 + i * 100, 10_000 + i * 100 + 700 + 30, 10_000 + i * 100 + 40);
        Assert.Equal(8, sync.SampleCount);
        Assert.Equal(710, sync.OffsetMs);            // old best sample aged out
        Assert.Equal(1_000_000 + 710, sync.ServerNowMs());
    }

    [Fact]
    public async Task SampleAsync_takes_requested_samples()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(5_000));
        var sync = new ClockSync(time);
        await sync.SampleAsync(() => Task.FromResult(time.GetUtcNow().ToUnixTimeMilliseconds() + 250), 5);
        Assert.Equal(5, sync.SampleCount);
        Assert.Equal(250, sync.OffsetMs);
    }
}

public class AdPolicyTests
{
    private static readonly AdConfigDto Config = new(3, true);

    private static List<bool> Run(params GameResultForUser[] results) => Run(false, results);

    private static List<bool> Run(bool blocked, params GameResultForUser[] results)
    {
        var state = default(AdPolicyState);
        var shown = new List<bool>();
        foreach (var r in results)
        {
            (var show, state) = AdPolicy.OnGameCompleted(state, r, Config, blocked);
            shown.Add(show);
        }
        return shown;
    }

    [Fact]
    public void Every_third_game() => Assert.Equal([false, false, true, false, false, true],
        Run(GameResultForUser.Win, GameResultForUser.Draw, GameResultForUser.Win, GameResultForUser.Win, GameResultForUser.Loss, GameResultForUser.Win));

    [Fact]
    public void Skips_right_after_second_loss_in_a_row_then_shows_next() => Assert.Equal([false, false, false, true],
        Run(GameResultForUser.Win, GameResultForUser.Loss, GameResultForUser.Loss, GameResultForUser.Win));

    [Fact]
    public void Aborted_games_do_not_count() => Assert.Equal([false, false, false, true],
        Run(GameResultForUser.Win, GameResultForUser.Aborted, GameResultForUser.Win, GameResultForUser.Win));

    [Fact]
    public void Never_on_match_route() => Assert.All(Run(true, GameResultForUser.Win, GameResultForUser.Win, GameResultForUser.Win, GameResultForUser.Win), Assert.False);
}

public class RetryForeverPolicyTests
{
    [Fact]
    public void Schedule_is_0_1_2_5_10_then_10_plus_minus_20_percent()
    {
        var p = new RetryForeverPolicy();
        Assert.Equal([0, 1, 2, 5, 10], Enumerable.Range(0, 5).Select(i => p.Delay(i).TotalSeconds).ToArray());
        for (var i = 5; i < 200; i++)
        {
            var d = p.Delay(i).TotalSeconds;
            Assert.InRange(d, 8, 12);
        }
        Assert.Equal(8, new RetryForeverPolicy(() => 0).Delay(10).TotalSeconds, 3);
        Assert.Equal(12, new RetryForeverPolicy(() => 1).Delay(10).TotalSeconds, 3);
        Assert.Equal(TimeSpan.FromSeconds(2), ((IRetryPolicy)p).NextRetryDelay(new RetryContext { PreviousRetryCount = 2 }));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public async Task Settings_persist_and_reload()
    {
        var kv = new InMemoryKeyValueStore();
        var sound = new RecordingSound();
        var a = new SettingsStore(kv, sound);
        await a.LoadAsync();
        Assert.Equal(SettingsState.Default, a.State);

        await a.UpdateAsync(s => s with { Theme = ThemePreference.Light, Music = true, ThreatHints = false, SfxVolume = 0.25, Motion = MotionPreference.Reduced });
        Assert.Equal("light", await kv.GetAsync(SettingsStore.ThemeKey));
        Assert.Equal((0.25, 0.5), sound.Volumes);

        var b = new SettingsStore(kv, new RecordingSound());
        await b.LoadAsync();
        Assert.Equal(a.State, b.State);

        await b.UpdateAsync(s => s with { Theme = ThemePreference.System });
        Assert.Null(await kv.GetAsync(SettingsStore.ThemeKey));
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults()
    {
        Assert.Equal(SettingsState.Default, SettingsStore.Parse("{not json"));
        Assert.Equal(SettingsState.Default with { Sfx = false }, SettingsStore.Parse("{\"sfx\":false,\"theme\":42}"));
    }
}

public class StubAdProviderTests
{
    private sealed class Overlay(bool complete) : IAdOverlay
    {
        public AdOverlayRequest? Last;
        public Task<bool> ShowAsync(AdOverlayRequest request, CancellationToken ct) { Last = request; return Task.FromResult(complete); }
    }

    [Fact]
    public async Task Rewarded_receipt_matches_server_stub_format()
    {
        var overlay = new Overlay(true);
        var r = await new StubAdProvider(overlay).ShowRewardedAsync(RewardKind.StreakFreeze, CancellationToken.None);
        Assert.True(r.Earned);
        Assert.Matches("^stub:[a-z]+:[0-9a-f-]{36}$", r.Receipt!);
        Assert.Equal("rewarded", overlay.Last!.Kind);

        var closed = await new StubAdProvider(new Overlay(false)).ShowRewardedAsync(RewardKind.Analysis, CancellationToken.None);
        Assert.False(closed.Earned);
        Assert.Null(closed.Receipt);
    }
}
