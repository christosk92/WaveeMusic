using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Stage H — the full headless pipeline through the REAL SilentAudioHost: controller -> host -> projection -> IPlaybackState,
// plus a bounded-allocation gate on the cluster-ingest hot path (the projection is the event-driven app tier — bounded
// Gen0, not zero; the engine's phase-6-13 tripwire never touches it).
public class ConnectEndToEndTests
{
    [Fact]
    public async Task PlayThenPause_ThroughRealSilentHost_ReflectsInProjection()
    {
        var host = new SilentAudioHost();
        var proj = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var c = new PlaybackController(host, new StubTrackResolver(), proj,
            new FakeContextResolver("spotify:track:a"), "us");

        await c.PlayAsync("spotify:playlist:p");
        await Task.Delay(50);
        Assert.True(proj.IsPlaying);
        Assert.Equal("spotify:track:a", proj.CurrentTrack!.Uri);
        Assert.True(host.IsPlaying);

        await c.PauseAsync();
        await Task.Delay(50);
        Assert.False(proj.IsPlaying);
        Assert.False(host.IsPlaying);
    }

    [Fact]
    public void ClusterIngest_HotPath_IsBoundedAllocation()
    {
        var proj = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var cluster = new ClusterDelta("other", true,
            new RemoteTrack("spotify:track:t", "T", "A", "spotify:artist:a", "Al", "spotify:album:al", null, 1000),
            "spotify:playlist:p", true, false, false, 0, 0, 0, 1000, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

        long delta = ConnectHarness.AllocDelta(() => proj.OnCluster(cluster), iters: 100);
        long perCluster = delta / 100;
        Assert.True(perCluster < 4096, $"cluster ingest should be bounded (~one Track map), was {perCluster} bytes/cluster");
    }

    [Fact]
    public void PositionRead_OnProjection_IsZeroAlloc()
    {
        long now = 0;
        var proj = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        proj.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Playing, 0));
        long delta = ConnectHarness.AllocDelta(() => { now += 10; _ = proj.PositionMs; }, iters: 1000);
        Assert.True(delta < 1024, $"position read should be ~zero-alloc, was {delta} bytes / 1000 reads");
    }
}
