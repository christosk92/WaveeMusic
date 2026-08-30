using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Audio;

public class PreparedTransitionTests
{
    [Fact]
    public async Task NaturalHandoff_AdvancesExactPreparedItem_Once_WithoutReload()
    {
        var host = new PreparedHost();
        var projection = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b", "spotify:track:c"), "dev");

        await controller.PlayAsync("spotify:playlist:test");
        await WaitUntilAsync(() => host.Prepared.Count >= 1);
        var prepared = host.Prepared.First();
        Assert.Equal("spotify:track:b", prepared.Start.TrackUri);
        Assert.True(prepared.AllowOverlap);
        Assert.Equal(new[] { "spotify:track:a" }, host.Loaded.ToArray());

        host.EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Started, prepared.Token,
            prepared.Start.TrackUri, 0, 5000));
        await WaitUntilAsync(() => projection.CurrentTrack?.Uri == "spotify:track:b");

        Assert.Equal(new[] { "spotify:track:a" }, host.Loaded.ToArray()); // prepared audio was promoted; no active reload
        host.EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Started, prepared.Token,
            prepared.Start.TrackUri, 0, 5000));
        await Task.Delay(20);
        Assert.Equal("spotify:track:b", projection.CurrentTrack?.Uri);   // duplicate/stale Started cannot advance to c
    }

    [Fact]
    public async Task QueueEdit_CancelsOldIdentity_AndOnlyNewTokenCanAdvance()
    {
        var host = new PreparedHost();
        var projection = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b"), "dev");

        await controller.PlayAsync("spotify:playlist:test");
        await WaitUntilAsync(() => host.Prepared.Count >= 1);
        var stale = host.Prepared.First();

        await controller.PlayNextAsync([new PlaybackContextTrack("spotify:track:q")]);
        await WaitUntilAsync(() => host.Prepared.Count >= 2);
        var current = host.Prepared.Last();
        Assert.NotEqual(stale.Token, current.Token);
        Assert.Equal("spotify:track:q", current.Start.TrackUri);
        Assert.Contains(stale.Token, host.Cancelled);

        host.EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Started, stale.Token,
            stale.Start.TrackUri, 0, 5000));
        await Task.Delay(20);
        Assert.Equal("spotify:track:a", projection.CurrentTrack?.Uri);

        host.EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Started, current.Token,
            current.Start.TrackUri, 0, 5000));
        await WaitUntilAsync(() => projection.CurrentTrack?.Uri == "spotify:track:q");
    }

    [Fact]
    public async Task EpisodesPrepareGaplessButNeverRequestOverlap_AndManualNextReloads()
    {
        var host = new PreparedHost();
        var projection = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:episode:a", "spotify:episode:b"), "dev");

        await controller.PlayAsync("spotify:show:test");
        await WaitUntilAsync(() => host.Prepared.Count >= 1);
        Assert.False(host.Prepared.First().AllowOverlap);

        await controller.NextAsync();
        Assert.Equal(new[] { "spotify:episode:a", "spotify:episode:b" }, host.Loaded.ToArray());
    }

    [Fact]
    public async Task AZeroMsHandoff_AdvancesTheSession_WithoutReloadingTheActiveTrack()
    {
        // W2 killer 4: with crossfade off (the DEFAULT), the boundary is a butt-join, not a fade — the host announces the
        // join with EffectiveFadeMs = 0 and the session must advance off the PREPARED audio. A reload here is the audible
        // gap: it means the WASAPI session was torn down and reopened between two tracks.
        var host = new PreparedHost();
        var projection = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b"), "dev");

        await controller.PlayAsync("spotify:playlist:test");
        await WaitUntilAsync(() => host.Prepared.Count >= 1);
        var prepared = host.Prepared.First();

        host.EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Started, prepared.Token,
            prepared.Start.TrackUri, 0, EffectiveFadeMs: 0));
        await WaitUntilAsync(() => projection.CurrentTrack?.Uri == "spotify:track:b");

        Assert.Equal(new[] { "spotify:track:a" }, host.Loaded.ToArray());   // no second load == no session reopen
    }

    static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    sealed class PreparedHost : IAudioHost, IPreparedAudioHost
    {
        readonly SimpleSubject<AudioHostSignal> _signals = new();
        readonly SimpleSubject<AudioTransitionSignal> _transitions = new();
        public ConcurrentQueue<string> Loaded { get; } = new();
        public ConcurrentQueue<AudioPrepareRequest> Prepared { get; } = new();
        public ConcurrentQueue<string> Cancelled { get; } = new();

        public void Load(in AudioStreamHandle stream) => Loaded.Enqueue(stream.TrackUri);
        public void LoadFastStart(in AudioFastStart start) => Loaded.Enqueue(start.TrackUri);
        public void SupplyBody(in AudioStreamHandle body) { }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void Seek(long positionMs, SeekMode mode) { }
        public void SetVolume(double volume01) { }
        public long PositionMs => 0;
        public bool IsPlaying => true;
        public bool IsBuffering => false;
        public bool ClockValid => true;
        public IObservable<AudioHostSignal> Signals => _signals;
        public IObservable<AudioTransitionSignal> Transitions => _transitions;

        public Task PrepareNextAsync(AudioPrepareRequest request, CancellationToken ct = default)
        {
            Prepared.Enqueue(request);
            return Task.CompletedTask;
        }

        public Task SupplyNextBodyAsync(string token, AudioStreamHandle body, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AudioPrepareCancelResult> CancelPreparedAsync(string token, CancellationToken ct = default)
        {
            Cancelled.Enqueue(token);
            return Task.FromResult(AudioPrepareCancelResult.Cancelled);
        }

        public void EmitTransition(AudioTransitionSignal signal) => _transitions.OnNext(signal);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
