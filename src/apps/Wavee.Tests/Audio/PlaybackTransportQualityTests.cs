using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Audio;

public sealed class PlaybackTransportQualityTests : PlaybackCatalogTestBase
{
    [Fact]
    public async Task PauseThenResumeWhileTheFirstContextResolvesPreservesLatestIntent()
    {
        var context = new ControlledContext();
        using var projection = Projection();
        var host = new Host();
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection, context, "test");
        var play = controller.PlayAsync("spotify:playlist:test");
        await context.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(projection.CurrentTrack);
        await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await controller.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(projection.Transport.PlayWhenReady);
        context.Ready.SetResult();
        await play.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Assert.Single(host.Loads).PlayWhenReady);
        Assert.True(projection.IsPlaying);
    }

    [Fact]
    public async Task EmptyContextRetiresResolvingIntent()
    {
        using var projection = Projection();
        var host = new Host();
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection, new FakeContextResolver(), "test");
        await controller.PlayAsync("spotify:playlist:empty");
        Assert.False(projection.Transport.PlayWhenReady);
        Assert.Equal(PlaybackPhase.Idle, projection.Transport.Phase);
        Assert.False(projection.IsLoading);
        Assert.Empty(host.Loads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyContextKeepsTheExistingItemAndItsActualPlayIntent(bool paused)
    {
        var context = new ControlledContext();
        context.Ready.SetResult();
        using var projection = Projection();
        var host = new Host();
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection, context, "test");
        await controller.PlayAsync("spotify:playlist:test");
        if (paused) await controller.PauseAsync();
        context.EmptyAnswer = true;
        await controller.PlayAsync("spotify:playlist:empty");
        Assert.Equal("spotify:track:a", projection.CurrentTrack?.Uri);
        Assert.Equal(!paused, projection.Transport.PlayWhenReady);
        Assert.Equal(!paused, projection.IsPlaying);
        Assert.False(projection.IsLoading);
        Assert.Single(host.Loads);
    }

    [Fact]
    public async Task AnOlderFailedContextCannotRetireTheNewItemsIntent()
    {
        var context = new ControlledContext();
        using var projection = Projection();
        var host = new Host();
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection, context, "test");
        var oldPlay = controller.PlayAsync("spotify:playlist:test");
        await context.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await controller.PlayTrackAsync("spotify:track:b");
        context.Ready.SetException(new InvalidOperationException("old context failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => oldPlay);
        Assert.Equal("spotify:track:b", projection.CurrentTrack?.Uri);
        Assert.True(projection.Transport.PlayWhenReady);
        Assert.True(projection.IsPlaying);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledContextRetiresResolvingIntent(bool canceled)
    {
        var context = new ControlledContext();
        using var projection = Projection();
        var host = new Host();
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection, context, "test");
        using var cancellation = new CancellationTokenSource();
        var play = controller.PlayAsync("spotify:playlist:test", ct: cancellation.Token);
        await context.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (canceled)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);
        }
        else
        {
            context.Ready.SetException(new InvalidOperationException("context unavailable"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => play);
        }
        Assert.False(projection.Transport.PlayWhenReady);
        Assert.Equal(PlaybackPhase.Idle, projection.Transport.Phase);
        Assert.False(projection.IsLoading);
        Assert.Empty(host.Loads);
    }

    [Fact]
    public void AcceptedPauseDoesNotClaimTheOutputHasAlreadyStopped()
    {
        using var projection = Projection();
        var playing = new PlaybackCommandId(7, 1);
        var pause = new PlaybackCommandId(7, 2);
        projection.SetTransportIntent(playing, true, PlaybackPhase.Playing);
        projection.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Playing, 1000) { Command = playing });
        projection.SetTransportIntent(pause, false, PlaybackPhase.Pausing);
        projection.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Paused, 1000)
            { Command = pause, OperationStatus = PlaybackOperationStatus.Accepted });
        Assert.True(projection.Transport.OutputAdvancing);
        Assert.Equal(PlaybackPhase.Pausing, projection.Transport.Phase);
        projection.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Paused, 1020)
            { Command = pause, OperationStatus = PlaybackOperationStatus.Applied });
        Assert.False(projection.Transport.OutputAdvancing);
        Assert.Equal(PlaybackPhase.Paused, projection.Transport.Phase);
    }

    [Fact]
    public async Task PauseIsAcceptedWhileResolveWaits_AndTheCompletedLoadStaysPaused()
    {
        var resolver = new ControlledResolver();
        using var projection = Projection();
        var host = new Host();
        using var controller = new PlaybackController(host, resolver, projection, new FakeContextResolver("spotify:track:a"), "test");
        var play = controller.PlayAsync("spotify:playlist:test");
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(projection.Transport.PlayWhenReady);
        resolver.Complete("spotify:track:a");
        await play.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(Assert.Single(host.Loads).PlayWhenReady);
        Assert.False(projection.Transport.PlayWhenReady);
        Assert.False(host.IsPlaying);
    }

    [Fact]
    public async Task RepeatedNextDoesNotWaitForThePreviousResolve_AndDoesNotRecordUnheardItems()
    {
        var resolver = new ControlledResolver();
        resolver.Complete("spotify:track:a");
        using var projection = Projection();
        var host = new Host();
        using var controller = new PlaybackController(host, resolver, projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b", "spotify:track:c"), "test");
        await controller.PlayAsync("spotify:playlist:test");
        var firstNext = controller.NextAsync();
        await resolver.WaitUntilEnteredAsync("spotify:track:b");
        Assert.Equal("spotify:track:b", projection.CurrentTrack?.Uri);
        // The outgoing voice is still fading, even though the latest transport request belongs to B. Those frames
        // cannot prove B was heard before its source has even resolved.
        host.Report(new AudioHostSignal(AudioHostSignalKind.Playing, 1000)
            { Command = new(projection.Transport.ItemGeneration, 0) });
        var secondNext = controller.NextAsync();
        await resolver.WaitUntilEnteredAsync("spotify:track:c");
        Assert.Equal("spotify:track:c", projection.CurrentTrack?.Uri);
        resolver.Complete("spotify:track:c");
        await Task.WhenAll(firstNext, secondNext).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:c" }, host.Loads.Select(x => x.Source.Start.TrackUri));
        Assert.Equal(new[] { "spotify:track:a" }, controller.SnapForTest.History.Select(x => x.Track.Uri));
    }

    // A carried seek settles off the LOAD's host outcome, and that completion re-acquires the controller lock the
    // load path still holds when the fake host acknowledges synchronously — so it lands a beat after PlayAsync
    // returns. Wait for the terminal status instead of asserting it on the very next line (a suite-load flake).
    static async Task<PlaybackSeekState> SettledSeekAsync(NowPlayingProjection projection)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            var seek = projection.Transport.Seek;
            if (seek is { Status: not PlaybackOperationStatus.Accepted } settled) return settled;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the carried seek never left Accepted");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task SeekDuringResolveTravelsWithTheLoad_AndAcknowledgesThatExactRequest()
    {
        var resolver = new ControlledResolver();
        using var projection = Projection();
        var host = new Host();
        using var controller = new PlaybackController(host, resolver, projection, new FakeContextResolver("spotify:track:a"), "test");
        var play = controller.PlayAsync("spotify:playlist:test");
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var receipt = await controller.SeekAsync(new(12000, SeekMode.Accurate, PlaybackSeekKind.Commit));
        Assert.Empty(host.Seeks);
        Assert.Equal(PlaybackOperationStatus.Accepted, projection.Transport.Seek!.Value.Status);
        resolver.Complete("spotify:track:a");
        await play.WaitAsync(TimeSpan.FromSeconds(2));
        var load = Assert.Single(host.Loads);
        Assert.Equal(12000, load.StartPositionMs);
        // Finding #4 (library-v3-1): the load always mints its OWN fresh command now — reusing the seek's older id
        // risked FluentMediaAudioHost.Load dropping it as IsOlder if anything else (a Pause) minted a newer sequence
        // while the resolve was in flight. The seek still resolves off the LOAD's outcome via the controller's
        // internal seek-carrier, matched below by the seek's own id/status rather than by command identity.
        Assert.NotEqual(receipt.Id, load.Command);
        var settled = await SettledSeekAsync(projection);
        Assert.Equal(receipt.Id, settled.Id);
        Assert.Equal(PlaybackOperationStatus.Applied, settled.Status);
    }

    // Finding #4 / item 4 (library-v3-1): PlaybackController.cs:~2835 used to reuse the parked seek's OWN command id
    // as the eventual Load's command (`_pendingSeek?.Id ?? NextTransportCommand()`); a Pause landing while the
    // resolve was still in flight mints two NEWER transport sequences (SetPlayIntent + the host Submit), so that
    // reused id was already stale by the time the load reached the host — FluentMediaAudioHost.Load treats an older
    // command as a silent no-op (no media opens, no signal, nothing to react to). The fix always mints a fresh
    // command for the load and settles the parked seek off THAT command's own outcome instead.
    [Fact]
    public async Task SeekDuringResolve_ThenPauseDuringResolve_LoadStillOpensMedia_AndSeekResolves()
    {
        var resolver = new ControlledResolver();
        using var projection = Projection();
        var host = new Host();
        using var controller = new PlaybackController(host, resolver, projection, new FakeContextResolver("spotify:track:a"), "test");
        var play = controller.PlayAsync("spotify:playlist:test");
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var receipt = await controller.SeekAsync(new(15000, SeekMode.Accurate, PlaybackSeekKind.Commit));
        Assert.Equal(PlaybackOperationStatus.Accepted, projection.Transport.Seek!.Value.Status);

        // Mints newer transport sequences while the resolve is still pending — exactly the race that used to make
        // the eventual Load's reused command stale.
        await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(projection.Transport.PlayWhenReady);

        resolver.Complete("spotify:track:a");
        await play.WaitAsync(TimeSpan.FromSeconds(2));

        var load = Assert.Single(host.Loads);
        Assert.Equal(15000, load.StartPositionMs);   // the parked seek's target still carries into the load
        var settled = await SettledSeekAsync(projection);
        Assert.Equal(receipt.Id, settled.Id);
        Assert.Equal(PlaybackOperationStatus.Applied, settled.Status);
        Assert.Equal(15000, settled.ActualPositionMs);
    }

    [Fact]
    public void StaleSeekAndPauseAcknowledgementsCannotCompleteTheLatestSeek()
    {
        using var projection = Projection();
        var first = new PlaybackCommandId(7, 1);
        var latest = new PlaybackCommandId(7, 2);
        projection.SetTransportIntent(first, true, PlaybackPhase.Seeking, new(1000, SeekMode.Keyframe, PlaybackSeekKind.Preview));
        projection.SetTransportIntent(latest, true, PlaybackPhase.Seeking, new(5000, SeekMode.Accurate, PlaybackSeekKind.Commit));
        projection.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.PositionTick, 1000)
            { Command = first, OperationStatus = PlaybackOperationStatus.Applied });
        projection.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Paused, 1000)
            { Command = new(7, 3), OperationStatus = PlaybackOperationStatus.Applied });
        Assert.Equal(PlaybackOperationStatus.Accepted, projection.Transport.Seek!.Value.Status);
        projection.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.PositionTick, 5000)
            { Command = latest, OperationStatus = PlaybackOperationStatus.Applied });
        Assert.Equal(5000, projection.Transport.Seek!.Value.ActualPositionMs);
    }

    [Fact]
    public async Task NextWhilePausedPreservesIntent_AndExplicitNextBypassesRepeatOne()
    {
        using var projection = Projection();
        var host = new Host();
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b"), "test");
        await controller.PlayAsync("spotify:playlist:test");
        await controller.SetRepeatAsync(RepeatMode.Track);
        await controller.PauseAsync();
        await controller.NextAsync();
        Assert.Equal("spotify:track:b", projection.CurrentTrack?.Uri);
        Assert.False(host.Loads.Last().PlayWhenReady);
        Assert.False(projection.Transport.PlayWhenReady);
    }

    [Fact]
    public void OutputPositionStopsAtSubmittedFrames_AndResetsForANewItem()
    {
        long now = 0;
        using var projection = Catalog.Projection("test", () => now);
        var command = new PlaybackCommandId(7, 1);
        projection.SetTransportIntent(command, true, PlaybackPhase.Playing);
        projection.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Playing, 1000)
            { Command = command, PositionUpperBoundMs = 1050 });
        now = 200;
        Assert.Equal(1050, projection.PositionMs);
        Assert.Equal(7, projection.Transport.ItemGeneration);

        command = new(8, 2);
        projection.SetTransportIntent(command, true, PlaybackPhase.Resolving);
        Assert.Equal(8, projection.Transport.ItemGeneration);
        Assert.Null(projection.Transport.PositionUpperBoundMs);
        projection.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Playing, 2000) { Command = command });
        now = 400;
        Assert.Equal(2200, projection.PositionMs); // video / unbounded synthetic clocks still interpolate
    }

    [Fact]
    public async Task ReachingTheEndOfTheQueueClearsPlayIntent()
    {
        using var projection = Projection();
        var host = new Host();
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a"), "test");
        await controller.PlayAsync("spotify:playlist:test");
        await controller.NextAsync();
        Assert.False(projection.Transport.PlayWhenReady);
        Assert.False(projection.IsPlaying);
        Assert.Equal(PlaybackPhase.Ended, projection.Transport.Phase);
    }

    [Fact]
    public void RemoteSeekWaitsForObservedClusterPosition()
    {
        using var projection = Projection();
        Catalog.Cluster(projection, RemoteCluster(1000, 100));
        var id = new PlaybackCommandId(1, 1);
        projection.BeginRemoteSeek(id, new(20000, SeekMode.Accurate, PlaybackSeekKind.Commit), "phone");
        Assert.Equal(PlaybackOperationStatus.Accepted, projection.Transport.Seek!.Value.Status);
        Catalog.Cluster(projection, RemoteCluster(2000, 200));
        Assert.Equal(PlaybackOperationStatus.Accepted, projection.Transport.Seek!.Value.Status);
        Catalog.Cluster(projection, RemoteCluster(20000, 300));
        Assert.Equal(PlaybackOperationStatus.Applied, projection.Transport.Seek!.Value.Status);
        Assert.Equal(20000, projection.Transport.Seek.Value.ActualPositionMs);
    }

    // Finding #9 / item 5 (library-v3-1): a remote device that CLAMPS or ignores the seek_to target never told the
    // fold anything different — every later cluster snapshot just keeps reporting wherever it actually is, forever
    // short of the target, so the old fold left `_remoteSeek` at Accepted permanently and the seek bar froze at the
    // (never-reached) target. The fix settles on the first non-buffering fold that is either close enough OR has had
    // RemoteSeekSettleTimeoutMs to catch up — never leaving the operation hanging.
    [Fact]
    public void RemoteSeekSettlesAtTheClampedPositionAfterATimeout_InsteadOfFreezingAtAccepted()
    {
        long now = 0;
        using var projection = Catalog.Projection("test", () => now);
        Catalog.Cluster(projection, RemoteCluster(1000, 100));
        var id = new PlaybackCommandId(1, 1);
        projection.BeginRemoteSeek(id, new(20000, SeekMode.Accurate, PlaybackSeekKind.Commit), "phone");
        Assert.Equal(PlaybackOperationStatus.Accepted, projection.Transport.Seek!.Value.Status);

        // The device clamped the request to 5000 and keeps reporting it — nowhere near "close enough" to 20000, but
        // it also hasn't had a fair chance yet: stays Accepted (never a premature snap-back).
        now = 500;
        Catalog.Cluster(projection, RemoteCluster(5000, 200));
        Assert.Equal(PlaybackOperationStatus.Accepted, projection.Transport.Seek!.Value.Status);

        // Past the settle window with the SAME clamped position reported again — the operation must settle now
        // instead of freezing the seek bar at 20000 forever.
        now = 3000;
        Catalog.Cluster(projection, RemoteCluster(5000, 300));
        Assert.Equal(PlaybackOperationStatus.Applied, projection.Transport.Seek!.Value.Status);
        Assert.Equal(5000, projection.Transport.Seek.Value.ActualPositionMs);
    }

    [Fact]
    public void RemoteSeekIsSupersededWhenTheDeviceChanges()
    {
        using var projection = Projection();
        Catalog.Cluster(projection, RemoteCluster(1000, 100));
        projection.BeginRemoteSeek(new(1, 1), new(20000, SeekMode.Accurate, PlaybackSeekKind.Commit), "phone");
        Catalog.Cluster(projection, RemoteCluster(2000, 200) with { ActiveDeviceId = "speaker" });
        Assert.Equal(PlaybackOperationStatus.Superseded, projection.Transport.Seek!.Value.Status);
    }

    static ClusterDelta RemoteCluster(long position, long timestamp) => new("phone", true,
        new RemoteTrack("spotify:track:a", "A", "Artist", "spotify:artist:a", "Album", "spotify:album:a", null, 60000),
        "spotify:playlist:test", true, false, false, position, timestamp, timestamp, 60000, false, RepeatMode.Off,
        Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

    NowPlayingProjection Projection() => Catalog.Projection("test");

    sealed class ControlledResolver : ITrackResolver
    {
        readonly ConcurrentDictionary<string, TaskCompletionSource<AudioStreamHandle>> _pending = new();
        readonly ConcurrentDictionary<string, TaskCompletionSource> _entered = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<AudioStreamHandle> Pending(string uri) => _pending.GetOrAdd(uri, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        public Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default)
        { Entered.TrySetResult(); EnteredFor(track.Uri).TrySetResult(); return Pending(track.Uri).Task.WaitAsync(ct); }
        TaskCompletionSource EnteredFor(string uri) => _entered.GetOrAdd(uri, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        public Task WaitUntilEnteredAsync(string uri) => EnteredFor(uri).Task.WaitAsync(TimeSpan.FromSeconds(2));
        public void Complete(string uri) => Pending(uri).TrySetResult(new(uri, "file", "https://test.invalid/audio", default, AudioFormat.Flac, 200000, 0));
    }

    sealed class ControlledContext : IContextResolver
    {
        readonly FakeContextResolver _inner = new("spotify:track:a");
        public bool EmptyAnswer { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ResolvedContext> ResolveAsync(ContextSpec spec, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Ready.Task.WaitAsync(ct);
            if (EmptyAnswer) return ResolvedContext.Empty;
            return await _inner.ResolveAsync(spec, ct);
        }
        public Task<ContextPage> LoadMoreAsync(string url, CancellationToken ct = default) => _inner.LoadMoreAsync(url, ct);
        public Task<ResolvedContext> ResolveAutoplayAsync(string uri, IReadOnlyList<string> recent, CancellationToken ct = default) => _inner.ResolveAutoplayAsync(uri, recent, ct);
        public Task<ResolvedContext> ResolveAutopodcastAsync(string uri, IReadOnlyList<string> recent, CancellationToken ct = default) => _inner.ResolveAutopodcastAsync(uri, recent, ct);
        public Task<string?> ResolveRadioSeedAsync(string uri, CancellationToken ct = default) => _inner.ResolveRadioSeedAsync(uri, ct);
        public Task<IReadOnlyList<QueuedTrack>> HydrateAsync(IReadOnlyList<QueuedRef> refs, CancellationToken ct = default) => _inner.HydrateAsync(refs, ct);
    }

    sealed class Host : IAudioHost
    {
        readonly SimpleSubject<AudioHostSignal> _signals = new();
        public ConcurrentQueue<AudioLoadRequest> Loads { get; } = new();
        public ConcurrentQueue<long> Seeks { get; } = new();
        public PlaybackCommandReceipt Submit(AudioTransportRequest request) => RecordingHostOperations.Submit(this, request, _signals.OnNext);
        public void Load(AudioLoadRequest request) { Loads.Enqueue(request); RecordingHostOperations.Load(this, request, _signals.OnNext); }
        public void Load(in AudioStreamHandle stream) { ClockValid = true; PositionMs = 0; }
        public void LoadFastStart(in AudioFastStart start) { ClockValid = true; PositionMs = 0; }
        public void SupplyBody(in AudioStreamHandle body) { }
        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;
        public void Stop() { IsPlaying = false; ClockValid = false; }
        public void Seek(long positionMs, SeekMode mode) { Seeks.Enqueue(positionMs); PositionMs = positionMs; }
        public void SetVolume(double volume01) { }
        public long PositionMs { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsBuffering => false;
        public bool PlayIntent => IsPlaying;
        public bool ClockValid { get; private set; }
        public IObservable<AudioHostSignal> Signals => _signals;
        public void Report(AudioHostSignal signal) => _signals.OnNext(signal);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
