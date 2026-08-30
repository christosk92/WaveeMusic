using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class DeviceStatePublisherVolumeTests
{
    static Track T(string uri) => new(uri[(uri.LastIndexOf(':') + 1)..], uri, uri,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);

    sealed class Harness
    {
        public readonly StubTransport Transport = new();
        public readonly NowPlayingProjection Proj;
        public readonly SimpleSubject<string?> ConnId = new(null);
        public string? CurrentConnId = "c1";
        public readonly List<(PutStateReasonKind Reason, double Volume)> Publishes = new();
        TaskCompletionSource? _delayGate;
        public long Clock = 1000;
        public readonly DeviceStatePublisher Publisher;

        public Harness(int windowMs = 400)
        {
            Proj = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => Clock);
            Publisher = new DeviceStatePublisher(Transport, "us", Proj, ConnId, () => CurrentConnId,
                (reason, snap, _, _) =>
                {
                    Publishes.Add((reason, snap?.Volume01 ?? -1));
                    var s = reason + "|" + (snap?.Volume01.ToString("F3") ?? "-");
                    return Encoding.UTF8.GetBytes(s);
                },
                clock: () => Clock,
                volumePublishWindowMs: windowMs,
                delay: DelayAsync);
            ConnId.OnNext(CurrentConnId);
        }

        Task DelayAsync(int _, CancellationToken ct)
        {
            _delayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _delayGate.Task.WaitAsync(ct);
        }

        public void EmitVolume(double volume)
        {
            Proj.SetLocalVolume(volume);
            Publisher.OnEvent(new PlaybackEvent(EvKind.VolumeChanged, Proj.CurrentTrack, 0));
        }

        public void EmitPlayerState(EvKind kind = EvKind.Paused)
        {
            Publisher.OnEvent(new PlaybackEvent(kind, Proj.CurrentTrack, 0));
        }

        public void StartTrack()
        {
            var track = T("spotify:track:a");
            Proj.OnEvent(new PlaybackEvent(EvKind.Started, track, 0));
            Publisher.OnEvent(new PlaybackEvent(EvKind.Started, track, 0));
        }

        public Task CompleteDelayAsync() => _delayGate?.TrySetResult() is true ? Task.CompletedTask : Task.CompletedTask;

        public int VolumePublishCount => Publishes.Count(p => p.Reason == PutStateReasonKind.VolumeChanged);
    }

    [Fact]
    public async Task RapidVolumeChanges_CoalesceToLeadingAndTrailing()
    {
        var h = new Harness();
        h.StartTrack();
        h.Publishes.Clear();

        for (int i = 1; i <= 30; i++)
        {
            h.Clock = 1000 + i;
            h.EmitVolume(i / 100.0);
        }

        Assert.Equal(1, h.VolumePublishCount);
        Assert.Equal(0.01, h.Publishes.Last(p => p.Reason == PutStateReasonKind.VolumeChanged).Volume, 3);

        await h.CompleteDelayAsync();
        await Task.Delay(20);

        Assert.Equal(2, h.VolumePublishCount);
        Assert.Equal(0.30, h.Publishes.Last(p => p.Reason == PutStateReasonKind.VolumeChanged).Volume, 3);
    }

    [Fact]
    public async Task SameVolumeAfterWindow_Dedupes()
    {
        var h = new Harness();
        h.StartTrack();
        h.Publishes.Clear();

        h.EmitVolume(0.25);
        h.Clock = 1010;
        h.EmitVolume(0.30);
        await h.CompleteDelayAsync();
        await Task.Delay(20);
        Assert.Equal(2, h.VolumePublishCount);

        h.Clock += 500;
        h.EmitVolume(0.30);
        await Task.Delay(20);
        Assert.Equal(2, h.VolumePublishCount);
    }

    [Fact]
    public async Task PlayerStateChanged_NotBlockedByVolumeCoalescer()
    {
        var h = new Harness();
        h.StartTrack();
        h.Publishes.Clear();

        h.EmitVolume(0.2);
        h.EmitPlayerState(EvKind.Paused);
        await Task.Delay(20);

        Assert.Contains(h.Publishes, p => p.Reason == PutStateReasonKind.PlayerStateChanged);
        Assert.Equal(1, h.VolumePublishCount);
    }

    [Fact]
    public async Task Dispose_CancelsPendingVolumePublish()
    {
        var h = new Harness();
        h.StartTrack();
        h.Publishes.Clear();

        h.EmitVolume(0.2);
        h.Clock = 1100;
        h.EmitVolume(0.8);
        h.Publisher.Dispose();
        await h.CompleteDelayAsync();
        await Task.Delay(20);

        Assert.Equal(1, h.VolumePublishCount);
    }
}
