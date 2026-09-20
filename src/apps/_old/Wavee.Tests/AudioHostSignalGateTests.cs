using System.Collections.Immutable;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class AudioHostSignalGateTests
{
    [Theory]
    [InlineData(AudioHostSignalKind.PositionTick)]
    [InlineData(AudioHostSignalKind.Playing)]
    [InlineData(AudioHostSignalKind.Recovering)]
    [InlineData(AudioHostSignalKind.Buffering)]
    [InlineData(AudioHostSignalKind.Prebuffering)]
    public void PauseWinsOverReportsSampledWhilePhysicalPauseIsStillQueued(AudioHostSignalKind lateKind)
    {
        using var projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        projection.Ownership.Claim(ClaimCause.UserPlay);
        var track = new Track("track", "spotify:track:track", "Track", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 400_000, false, null);
        var snapshot = new QueueSnapshot(1, "spotify:album:album", null,
            new QueueEntry(QueueItemId.None, "now", track, QueueBucket.NowPlaying, QueueProvider.Context, false, "now"),
            ImmutableArray<QueueEntry>.Empty, ImmutableArray<QueueEntry>.Empty, ImmutableArray<QueueEntry>.Empty,
            false, RepeatMode.Off, "", 0);
        projection.ApplyLocalSnapshot(snapshot, new PlaybackEvent(EvKind.Started, track, 0));
        var reports = new AudioHostSignalGate(signal => projection.OnHostSignal(signal));
        reports.SetPlayIntent(true);
        reports.Publish(new AudioHostSignal(AudioHostSignalKind.Playing, 320_000));
        Assert.True(projection.IsPlaying);

        reports.Pause(322_769);
        // Construct AFTER Pause, as the real timer can sample the still-Playing audio core while PauseAsync waits.
        // A timestamp-only stale-sample guard cannot reject this report.
        reports.Publish(new AudioHostSignal(lateKind, 322_800));
        Assert.False(reports.PlayIntent);
        Assert.False(projection.IsPlaying);
        Assert.False(projection.IsBuffering);
        Assert.False(projection.IsPrebuffering);
        Assert.Equal(322_769, projection.PositionMs);

        reports.SetPlayIntent(true);
        reports.Publish(new AudioHostSignal(AudioHostSignalKind.Playing, 322_769));
        Assert.True(projection.IsPlaying);
    }

    [Fact]
    public void PausePublishesAnEdgeEvenWhenTheTimerWillNeverTickAgain()
    {
        var received = new List<AudioHostSignal>();
        var reports = new AudioHostSignalGate(received.Add);
        reports.SetPlayIntent(true);
        reports.Pause(1234);
        var pause = Assert.Single(received);
        Assert.Equal(AudioHostSignalKind.Paused, pause.Kind);
        Assert.Equal(1234, pause.PositionMs);
        Assert.False(pause.IsPlaying);
    }

    [Fact]
    public void ErrorsAndEndedRemainVisibleWithoutPlayIntent()
    {
        var received = new List<AudioHostSignal>();
        var reports = new AudioHostSignalGate(received.Add);
        reports.Publish(AudioHostSignal.Fault(1234, AudioKeyFailureReason.None, "failed"));
        reports.Publish(new AudioHostSignal(AudioHostSignalKind.Ended, 1234));
        Assert.Equal(2, received.Count);
        Assert.Equal(AudioHostSignalKind.Error, received[0].Kind);
        Assert.Equal(AudioHostSignalKind.Ended, received[1].Kind);
    }

    [Fact]
    public void PlayFailureIsPublishedAfterOpeningTheIntentGate()
    {
        var received = new List<AudioHostSignal>();
        var reports = new AudioHostSignalGate(received.Add);
        reports.SetPlayIntent(true);
        reports.Publish(new AudioHostSignal(AudioHostSignalKind.Buffering, 0));
        reports.Publish(AudioHostSignal.Fault(0, AudioKeyFailureReason.None, "session open failed"));
        Assert.Equal(2, received.Count);
        Assert.Equal(AudioHostSignalKind.Buffering, received[0].Kind);
        Assert.Equal(AudioHostSignalKind.Error, received[1].Kind);
        Assert.False(received[1].IsPlaying);
        Assert.Equal("session open failed", received[1].Detail);
    }

    [Fact]
    public void ObserverCanPauseReentrantlyDuringAPositionReport()
    {
        var received = new List<AudioHostSignal>();
        AudioHostSignalGate reports = null!;
        reports = new AudioHostSignalGate(signal =>
        {
            received.Add(signal);
            if (signal.Kind == AudioHostSignalKind.PositionTick) reports.Pause(signal.PositionMs);
        });
        reports.SetPlayIntent(true);
        reports.Publish(new AudioHostSignal(AudioHostSignalKind.PositionTick, 1200));
        reports.Publish(new AudioHostSignal(AudioHostSignalKind.PositionTick, 1400));
        Assert.Equal(2, received.Count);
        Assert.Equal(AudioHostSignalKind.Paused, received[^1].Kind);
        Assert.False(reports.PlayIntent);
    }

    [Fact]
    public async Task ConcurrentTimerAndTransportReportsCompleteAndLeavePauseLast()
    {
        AudioHostSignal last = default;
        var reports = new AudioHostSignalGate(signal => last = signal);
        reports.SetPlayIntent(true);
        using var start = new ManualResetEventSlim();
        var timer = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 1000; i++)
                reports.Publish(new AudioHostSignal(AudioHostSignalKind.PositionTick, i));
        });
        var transport = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 1000; i++)
            {
                reports.SetPlayIntent(true);
                reports.Pause(i);
            }
        });
        start.Set();
        await Task.WhenAll(timer, transport).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(reports.PlayIntent);
        Assert.Equal(AudioHostSignalKind.Paused, last.Kind);
        Assert.Equal(999, last.PositionMs);
    }
}
