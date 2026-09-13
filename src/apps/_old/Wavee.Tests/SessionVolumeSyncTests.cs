using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Two-way Windows session-volume sync at the controller/sink level (plan §D1 SessionVolumeSyncTests).
public class SessionVolumeSyncTests
{
    sealed class RecAudioHost : IAudioHost
    {
        public readonly List<string> Calls = new();
        readonly SimpleSubject<AudioHostSignal> _sig = new();
        public IObservable<AudioHostSignal> Signals => _sig;
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsBuffering => false;
        public bool ClockValid => true;
        public void Load(in AudioStreamHandle s) { }
        public void LoadFastStart(in AudioFastStart s) { }
        public void SupplyBody(in AudioStreamHandle s) { }
        public void Play() { IsPlaying = true; Calls.Add("play"); }
        public void Pause() { IsPlaying = false; Calls.Add("pause"); }
        public void Stop() { IsPlaying = false; Calls.Add("stop"); }
        public void Seek(long ms, SeekMode mode) { PositionMs = ms; }
        public void SetVolume(double v) { Calls.Add("vol"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class RecOutbound : IOutboundControl
    {
        public readonly List<(string Target, string Json)> Sent = new();
        public readonly List<(string Target, int Volume)> Volumes = new();
        public int? LastVolume => Volumes.Count > 0 ? Volumes[^1].Volume : null;
        public Task<OutboundResult> SendAsync(string t, string j, CancellationToken ct = default) { Sent.Add((t, j)); return Task.FromResult(new OutboundResult(true, "ack", 200)); }
        public Task<OutboundResult> SetVolumeAsync(string t, int v, CancellationToken ct = default) { Volumes.Add((t, v)); return Task.FromResult(new OutboundResult(true, "ack", 200)); }
        public Task<OutboundResult> TransferAsync(string f, string t, CancellationToken ct = default, bool hostingVideo = false) => Task.FromResult(new OutboundResult(true, "ack", 200));
    }

    sealed class RecProj : IPlaybackProjection
    {
        public readonly List<PlaybackEvent> Events = new();
        public void OnEvent(in PlaybackEvent e) => Events.Add(e);
        public int Count(EvKind kind) => Events.Count(e => e.Kind == kind);
    }

    static ClusterDelta Cluster(string active) =>
        new(active, false, default, "spotify:playlist:ctx", false, true, false, 0, 0, 0, 0, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

    static PlaybackController Make(out RecAudioHost host, out NowPlayingProjection proj, out RecOutbound outbound, out RecProj extra)
    {
        host = new RecAudioHost();
        proj = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        outbound = new RecOutbound();
        extra = new RecProj();
        return new PlaybackController(host, new StubTrackResolver(), proj, new FakeContextResolver("spotify:track:a"), "us", outbound, new[] { extra });
    }

    static async Task PrimeLocalPlaybackWithStaleProjection(
        PlaybackController controller, RecAudioHost host, NowPlayingProjection projection)
    {
        await controller.PlayAsync("spotify:track:a");
        host.PositionMs = 15_000;   // the audio engine is authoritative at 0:15
        projection.OnEvent(new PlaybackEvent(EvKind.Seeked, projection.CurrentTrack, 60_000));
        host.Calls.Clear();         // ignore setup calls; the volume intent must write the host exactly once
    }

    [Fact]
    public void OnExternalVolumeChanged_MovesProjection_AnnouncesVolume_NoHostSet_NoOutboundPut()
    {
        using var c = Make(out var host, out var proj, out var outbound, out var extra);
        c.OnExternalVolumeChanged(0.5);

        Assert.Equal(0.5, proj.Volume, 3);                       // slider moved
        Assert.True(extra.Count(EvKind.VolumeChanged) >= 1);     // announced (DeviceStatePublisher input)
        Assert.DoesNotContain("vol", host.Calls);                // NOT echoed down to the host (epsilon guard)
        Assert.Empty(outbound.Volumes);                          // NOT a Connect volume PUT (we ARE the active device)
    }

    [Fact]
    public void ClusterEcho_InsideLocalCmdWindow_DoesNotSnapSliderBack()
    {
        using var c = Make(out _, out var proj, out _, out _);
        c.OnExternalVolumeChanged(0.5);                          // NoteLocalCommand + slider 0.5
        proj.OnCluster(Cluster("us") with { ActiveVolume0_65535 = 6553 });   // stale ~0.1 echo inside the window
        Assert.Equal(0.5, proj.Volume, 2);                       // not snapped back
    }

    [Fact]
    public async Task LocalVolumeChange_PublishesOnlyAuthoritativeHostPosition_AndSetsHostOnce()
    {
        using var c = Make(out var host, out var proj, out _, out var extra);
        await PrimeLocalPlaybackWithStaleProjection(c, host, proj);
        var positions = new List<long>();
        using var sub = proj.Changes.Subscribe(ConnectHarness.Obs<IPlaybackState>(s => positions.Add(s.PositionMs)));
        positions.Clear(); // SimpleSubject intentionally replays setup state; assert only this volume intent.

        await c.SetVolumeAsync(0.42);

        Assert.NotEmpty(positions);
        Assert.All(positions, p => Assert.Equal(15_000, p));     // no stale 1.0/end-frame before the host tick corrects it
        Assert.Equal(0.42, proj.Volume, 3);
        Assert.Equal(1, host.Calls.Count(x => x == "vol"));
        Assert.Equal(1, extra.Count(EvKind.VolumeChanged));
    }

    [Fact]
    public async Task ExternalVolumeChange_PublishesOnlyAuthoritativeHostPosition_WithoutEcho()
    {
        using var c = Make(out var host, out var proj, out var outbound, out var extra);
        await PrimeLocalPlaybackWithStaleProjection(c, host, proj);
        var positions = new List<long>();
        using var sub = proj.Changes.Subscribe(ConnectHarness.Obs<IPlaybackState>(s => positions.Add(s.PositionMs)));
        positions.Clear(); // SimpleSubject intentionally replays setup state; assert only this volume intent.

        c.OnExternalVolumeChanged(0.55);

        Assert.NotEmpty(positions);
        Assert.All(positions, p => Assert.Equal(15_000, p));
        Assert.DoesNotContain("vol", host.Calls);
        Assert.Empty(outbound.Volumes);
        Assert.Equal(1, extra.Count(EvKind.VolumeChanged));
    }

    [Fact]
    public async Task SetVolumeAsync_StillForwards_ToRemoteTarget()
    {
        using var c = Make(out _, out var proj, out var outbound, out _);
        proj.OnCluster(Cluster("other-device"));
        await c.SetVolumeAsync(0.25);
        Assert.Equal((int)Math.Round(0.25 * 65535), outbound.LastVolume);   // regression: remote volume PUT unchanged
    }

    [Fact]
    public void SinkFilter_OwnContextSuppressed_ForeignContextInverseTapered()
    {
        var own = Guid.NewGuid();
        Assert.False(SessionVolumeSync.TryReadExternal(own, own, 0.5f, 0, out _, out _));   // self-originated echo → suppressed

        Assert.True(SessionVolumeSync.TryReadExternal(Guid.NewGuid(), own, 0.125f, 1, out double slider, out bool muted));
        Assert.Equal(0.5, slider, 3);   // cbrt(0.125) = 0.5
        Assert.True(muted);
    }
}
