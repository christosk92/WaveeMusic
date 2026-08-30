using System;
using System.Linq;
using System.Threading.Tasks;
using Wavee;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Audio;

public class InstantStartTests
{
    [Fact]
    public async Task Play_StartsOnHead_BeforeBodyResolves_ThenSuppliesBody()
    {
        var host = new RecordingAudioHost();
        var proj = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        var bodyTcs = new TaskCompletionSource<AudioStreamHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new AudioFastStart("spotify:track:x", "fid", AudioFormat.OggVorbis320, 1000, 0f, new byte[10]);
        var fast = new FakeFastResolver(new FastStartPlan(start, bodyTcs.Task));
        var controller = new PlaybackController(host, new StubTrackResolver(), proj, EmptyContextResolver.Instance, "dev", fast: fast);

        await controller.PlayTrackAsync("spotify:track:x");

        // Head + play happen immediately; the body has NOT resolved yet → SupplyBody not called.
        Assert.True(host.LoadFastStartCalled);
        Assert.True(host.PlayCalled);
        Assert.False(host.SupplyBodyCalled);
        Assert.Equal(1, fast.Calls);

        // The parallel body lands → the controller supplies it to the host.
        bodyTcs.SetResult(new AudioStreamHandle("spotify:track:x", "fid", "https://cdn", new byte[16], AudioFormat.OggVorbis320, 1000, 0f, new[] { "https://cdn" }, 10));
        await host.SupplyBodySignaled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(host.SupplyBodyCalled);

        controller.Dispose();
    }

    [Fact]
    public async Task Play_FastResolveFailure_SurfacesError_NoStart()
    {
        var host = new RecordingAudioHost();
        var proj = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        var fast = new ThrowingFastResolver(AudioKeyFailureReason.RotationDrift);
        var controller = new PlaybackController(host, new StubTrackResolver(), proj, EmptyContextResolver.Instance, "dev", fast: fast);
        PlaybackErrorInfo? err = null;
        controller.OnPlaybackError = m => err = m;

        await controller.PlayTrackAsync("spotify:track:x");

        Assert.False(host.LoadFastStartCalled);
        Assert.Equal(AudioKeyFailureReason.RotationDrift, err?.Reason);
        Assert.Equal(AudioKeyFailureReason.RotationDrift.ToUserMessage(), err?.UserMessage);
        controller.Dispose();
    }

    [Fact]
    public async Task BodyFailureAfterHeadStart_StopsHost_SurfacesError_AndLogsContext()
    {
        var host = new RecordingAudioHost();
        var proj = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        var bodyTcs = new TaskCompletionSource<AudioStreamHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new AudioFastStart("spotify:track:x", "fid", AudioFormat.OggVorbis320, 1000, 0f, new byte[10]);
        var fast = new FakeFastResolver(new FastStartPlan(start, bodyTcs.Task));
        var sink = new CapturingWaveeLog();
        var log = new WaveeLogger(sink, "test");
        var controller = new PlaybackController(host, new StubTrackResolver(), proj, EmptyContextResolver.Instance, "dev", log: log, fast: fast);
        PlaybackErrorInfo? err = null;
        var errorSignaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.OnPlaybackError = e => { err = e; errorSignaled.TrySetResult(); };

        await controller.PlayTrackAsync("spotify:track:x");
        bodyTcs.SetException(new AudioPlaybackException(AudioKeyFailureReason.Network, "cdn down"));

        await host.StopSignaled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await errorSignaled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(host.StopCalled);
        Assert.Equal(AudioKeyFailureReason.Network, err?.Reason);
        Assert.Contains(sink.Entries, e => e.Message.Contains("fast-start body failed for active track=spotify:track:x", StringComparison.Ordinal));
        Assert.Contains(sink.Entries, e => e.Message.Contains("stopping audio host to unblock head stream", StringComparison.Ordinal));
        controller.Dispose();
    }

    [Fact]
    public async Task BodyAlreadyReady_IsSuppliedAfterShortHeadGrace()
    {
        var host = new RecordingAudioHost();
        var proj = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        var start = new AudioFastStart("spotify:track:x", "fid", AudioFormat.OggVorbis320, 1000, 0f, new byte[10]);
        var body = Task.FromResult(new AudioStreamHandle("spotify:track:x", "fid", "https://cdn", new byte[16],
            AudioFormat.OggVorbis320, 1000, 0f, new[] { "https://cdn" }, 10));
        var fast = new FakeFastResolver(new FastStartPlan(start, body));
        var sink = new CapturingWaveeLog();
        var log = new WaveeLogger(sink, "test");
        var controller = new PlaybackController(host, new StubTrackResolver(), proj, EmptyContextResolver.Instance, "dev", log: log, fast: fast);

        await controller.PlayTrackAsync("spotify:track:x");

        Assert.True(host.LoadFastStartCalled);
        Assert.True(host.PlayCalled);
        Assert.False(host.SupplyBodyCalled);
        Assert.Contains(sink.Entries, e => e.Message.Contains("deferring supply", StringComparison.Ordinal));

        await host.SupplyBodySignaled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(host.SupplyBodyCalled);
        controller.Dispose();
    }
}

sealed class ThrowingFastResolver : IFastTrackResolver
{
    readonly AudioKeyFailureReason _reason;
    public ThrowingFastResolver(AudioKeyFailureReason reason) => _reason = reason;
    public Task<FastStartPlan> ResolveFastAsync(Wavee.Core.Track track, System.Threading.CancellationToken ct = default)
        => throw new AudioPlaybackException(_reason, "meta failed");
}
