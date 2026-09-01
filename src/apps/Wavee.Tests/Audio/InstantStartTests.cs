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

    // AMENDED when FastStartBodySupplyGrace was deleted. This used to assert the body was WITHHELD for a hardcoded
    // 250 ms ("deferring supply {n}ms so clear-head decode can queue first PCM") and that a "deferring supply" line was
    // logged. That grace was a wall-clock guess: it behaved differently on every machine, and the ordering it was
    // supposed to buy is already guaranteed by the host's serialized Enqueue pump (LoadFastStartAsync is queued before
    // SupplyBodyAsync). Bandwidth shaping moved to SpotifyAudioStream's PauseReadAhead lease. So the test now pins the
    // real contract — the body is supplied promptly AND never before the clear head — instead of the delay that used
    // to stand in for it.
    [Fact]
    public async Task BodyAlreadyReady_IsSuppliedPromptly_AfterTheClearHead()
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

        await host.SupplyBodySignaled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(host.SupplyBodyCalled);
        // The ordering the deleted grace existed to protect: the clear head is always loaded first.
        Assert.True(host.LoadFastStartPrecededBody);
        // And nothing withholds the body on a wall clock any more.
        Assert.DoesNotContain(sink.Entries, e => e.Message.Contains("deferring supply", StringComparison.Ordinal));
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
