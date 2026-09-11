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

public class PreparedTransitionTests : PlaybackCatalogTestBase
{
    [Fact]
    public async Task NaturalHandoff_AdvancesExactPreparedItem_Once_WithoutReload()
    {
        var host = new PreparedHost();
        var projection = Catalog.Projection("dev");
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b", "spotify:track:c"), "dev");

        await controller.PlayAsync("spotify:playlist:test");
        await WaitUntilAsync(() => host.Prepared.Count >= 1);
        var prepared = host.Prepared.First();
        Assert.Equal("spotify:track:b", prepared.Start.TrackUri);
        Assert.False(prepared.AllowOverlap); // unknown album metadata conservatively keeps the boundary gapless
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
        var projection = Catalog.Projection("dev");
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
        var projection = Catalog.Projection("dev");
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
        var projection = Catalog.Projection("dev");
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

    [Fact]
    public async Task Invalidated_ReSchedulesPreparedNext_ForTheSameUpcomingTrack()
    {
        // Device-reopen gapless fix (A2): the host tells the controller a prepared slot it already disposed (built for
        // a mixer rate the reopened session no longer runs at) is gone. The controller must react exactly like a Missed
        // hand-off — re-resolve a FRESH prepare for the same upcoming item — never leave the boundary unprepared.
        var host = new PreparedHost();
        var projection = Catalog.Projection("dev");
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b"), "dev");

        await controller.PlayAsync("spotify:playlist:test");
        await WaitUntilAsync(() => host.Prepared.Count >= 1);
        var stale = host.Prepared.First();
        Assert.Equal("spotify:track:b", stale.Start.TrackUri);

        host.EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Invalidated, stale.Token,
            stale.Start.TrackUri, 0, 0, "format-changed"));

        await WaitUntilAsync(() => host.Prepared.Count >= 2);
        var fresh = host.Prepared.Last();
        Assert.Equal("spotify:track:b", fresh.Start.TrackUri);   // re-prepared the SAME upcoming item
        Assert.NotEqual(stale.Token, fresh.Token);                // under a fresh token — the stale one is provably dead

        // The re-prepared slot still hands off cleanly (proves the controller didn't wedge on the invalidation).
        host.EmitTransition(new AudioTransitionSignal(AudioTransitionKind.Started, fresh.Token,
            fresh.Start.TrackUri, 0, 0));
        await WaitUntilAsync(() => projection.CurrentTrack?.Uri == "spotify:track:b");
    }

    [Fact]
    public async Task ManualNextPromotesTheExactPreparedItem_AndPreservesPause()
    {
        var host = new PreparedHost { PromotePrepared = true };
        using var projection = Catalog.Projection("dev");
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b", "spotify:track:c"), "dev");
        await controller.PlayAsync("spotify:playlist:test");
        await WaitUntilAsync(() => host.Prepared.Count > 0);
        var prepared = host.Prepared.First();
        await controller.PauseAsync();
        await controller.NextAsync();
        var promoted = Assert.Single(host.Promoted);
        Assert.Equal(prepared.Token, promoted.Token);
        Assert.Equal(prepared.TargetItem, promoted.TargetItem);
        Assert.False(promoted.PlayWhenReady);
        Assert.Equal("spotify:track:b", projection.CurrentTrack?.Uri);
        Assert.False(projection.Transport.PlayWhenReady);
        Assert.Equal(new[] { "spotify:track:a" }, host.Loaded.ToArray());
    }

    [Fact]
    public async Task NaturalBoundaryReportedDuringManualSkip_IsCommittedBeforeTheManualQueueStep()
    {
        var host = new PreparedHost { PromotePrepared = true };
        using var projection = Catalog.Projection("dev");
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b", "spotify:track:c"), "dev");
        await controller.PlayAsync("spotify:playlist:test");
        await WaitUntilAsync(() => host.Prepared.Count > 0);
        var prepared = host.Prepared.First();
        host.OnSkip = () => host.EmitTransition(new(AudioTransitionKind.Started, prepared.Token, prepared.Start.TrackUri, 0));
        await controller.NextAsync();
        Assert.Equal("spotify:track:c", projection.CurrentTrack?.Uri);
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b" }, controller.SnapForTest.History.Select(x => x.Track.Uri));
    }

    static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    sealed class PreparedHost : IAudioHost, IPreparedAudioHost
    {
        public Action? OnSkip;
        public bool PromotePrepared;
        public ConcurrentQueue<AudioPromoteRequest> Promoted { get; } = new();
        public PlaybackCommandReceipt Submit(AudioTransportRequest request)
        {
            if (request.Action == AudioTransportAction.Skip) { var callback = OnSkip; OnSkip = null; callback?.Invoke(); }
            return global::Wavee.Tests.RecordingHostOperations.Submit(this, request, _signals.OnNext);
        }
        public void Load(AudioLoadRequest request) => global::Wavee.Tests.RecordingHostOperations.Load(this, request, _signals.OnNext);
        public bool PlayIntent => IsPlaying;

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

        public Task<bool> TryPromotePreparedAsync(AudioPromoteRequest request, CancellationToken ct = default)
        {
            if (!PromotePrepared) return Task.FromResult(false);
            Promoted.Enqueue(request);
            _signals.OnNext(new AudioHostSignal(request.PlayWhenReady ? AudioHostSignalKind.Playing : AudioHostSignalKind.Paused,
                0, request.PlayWhenReady, false, false) { Command = request.Command, PlayWhenReady = request.PlayWhenReady });
            return Task.FromResult(true);
        }

        public Task<AudioPrepareCancelResult> CancelPreparedAsync(string token, CancellationToken ct = default)
        {
            Cancelled.Enqueue(token);
            return Task.FromResult(AudioPrepareCancelResult.Cancelled);
        }

        public void EmitTransition(AudioTransitionSignal signal) => _transitions.OnNext(signal);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
