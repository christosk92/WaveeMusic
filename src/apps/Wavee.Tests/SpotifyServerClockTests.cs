using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

// The NTP-style server-clock skew estimator (Backend, proto-free): RTT-min probe, unsynced sentinel, passive bootstrap.
public class SpotifyServerClockTests
{
    [Fact]
    public async Task SyncAsync_PicksLowestRttSample_WithMidpointCorrection()
    {
        long now = 1000;
        const long trueOffset = 4000;
        var rtts = new Queue<long>(new long[] { 100, 40, 80 });   // sample 2 (rtt=40) is the most accurate → wins
        // The fetch advances the local clock by this sample's RTT, then stamps server time at the response instant.
        Task<long> Fetch(CancellationToken _) { now += rtts.Dequeue(); return Task.FromResult(now + trueOffset); }

        var clock = new SpotifyServerClock(Fetch, log: default, localNowUnixMs: () => now);
        await clock.SyncAsync(default);

        // sample 2: t1=1100, t2=1140, mid=1120, server=5140 → offset=4020 (true 4000 + half of the 40ms RTT)
        Assert.True(clock.IsSynced);
        Assert.True(clock.IsProbed);
        Assert.Equal(4020, clock.OffsetMs);
        Assert.Equal(40, clock.LastRttMs);
    }

    [Fact]
    public void ServerNowUnixMs_BeforeSync_ReturnsUnsyncedSentinel()
    {
        var clock = new SpotifyServerClock(_ => Task.FromResult(5000L), log: default, localNowUnixMs: () => 1000);
        Assert.False(clock.IsSynced);
        Assert.Equal(0, clock.ServerNowUnixMs());   // 0 ⇒ projection skips the offset-dependent term
    }

    [Fact]
    public async Task ServerNowUnixMs_AfterSync_IsLocalPlusOffset()
    {
        long now = 1000;
        var rtts = new Queue<long>(new long[] { 50, 50, 50 });
        Task<long> Fetch(CancellationToken _) { now += rtts.Dequeue(); return Task.FromResult(now + 4000); }
        var clock = new SpotifyServerClock(Fetch, log: default, localNowUnixMs: () => now);
        await clock.SyncAsync(default);
        long offset = clock.OffsetMs;

        now = 9_000;
        Assert.Equal(9_000 + offset, clock.ServerNowUnixMs());
    }

    [Fact]
    public void ObservePassive_BootstrapsOffset_WhenUnsynced()
    {
        long now = 2000;
        var clock = new SpotifyServerClock(_ => Task.FromResult(0L), log: default, localNowUnixMs: () => now);
        clock.ObservePassive(6000);   // passiveOffset = 6000 - 2000 = 4000

        Assert.True(clock.IsSynced);
        Assert.False(clock.IsProbed);                 // bootstrap is not an unbiased probe
        Assert.Equal(6000, clock.ServerNowUnixMs());  // 2000 + 4000
    }

    [Fact]
    public async Task ObservePassive_DoesNotOverrideProbedOffset()
    {
        long now = 1000;
        var rtts = new Queue<long>(new long[] { 20, 20, 20 });
        Task<long> Fetch(CancellationToken _) { now += rtts.Dequeue(); return Task.FromResult(now + 4000); }
        var clock = new SpotifyServerClock(Fetch, log: default, localNowUnixMs: () => now);
        await clock.SyncAsync(default);
        long probed = clock.OffsetMs;

        // A passive sample consistent with the probe (no drift) must leave the probed offset untouched.
        clock.ObservePassive(now + probed);
        Assert.Equal(probed, clock.OffsetMs);
    }
}
