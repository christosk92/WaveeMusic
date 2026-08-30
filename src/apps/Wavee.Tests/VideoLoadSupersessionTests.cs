using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The video→video SUPERSESSION WEDGE regression suite.
//
// The bug: the native PlayReady/CENC session is a PROCESS-GLOBAL singleton with a session-less ABI (FgPlayReadyRunEx /
// FgPlayReadyStop / FgPlayReadyGetSnapshot take no session handle). FluentVideoMediaHost.LoadVideo tore the previous
// player down FIRE-AND-FORGET and immediately opened the successor, so on a video→video track skip (two LoadVideo calls
// ~250ms apart, no host swap) the predecessor's global Stop could shut the SUCCESSOR down. RunEx then returned a SUCCESS
// hr — nothing reported an error — and the snapshot settled on native "stopped" → PlaybackState.Idle, a state the host's
// Tick switch has no case for. The host went silent forever: no signal, no fault, no position, transport paused at 0:00.
//
// The fix has two halves, both pinned here against PRODUCTION code (the two units are engine-free on purpose, so these
// tests drive the real classes rather than a mock of them — the PlacementCore/MediaSwitchLogic discipline):
//   • VideoLoadPump      — teardown(previous) is AWAITED TO COMPLETION before build(next), and a request that is already
//                          superseded is never built at all (latest-wins coalescing).
//   • VideoStartWatchdog — a load that never reaches a playing/advancing state raises exactly ONE fault, never fires for
//                          a deliberately paused session, and disarms on progress and on teardown.
// Plus the routing leg: that fault travels the ordinary AudioHostSignal channel into PlaybackController.OnHostSignal and
// out through the existing error path, so the paused-at-0:00 zombie state is impossible.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public class VideoLoadSupersessionTests
{
    // ── the pump rig: fake teardown/build steps that append to ONE ordered log, so "A was fully torn down BEFORE B was
    //    built" is a provable ordering fact rather than two independent counters (the HostSwap-tests shape).
    sealed class FakeSource(string key)
    {
        public string Key { get; } = key;
        public override string ToString() => Key;
    }

    sealed class Rig
    {
        readonly object _g = new();
        readonly List<string> _log = new();
        public string LiveKey = "";
        public TaskCompletionSource? TeardownGate;
        public TaskCompletionSource? BuildGate;
        public readonly List<long> BuildEpochs = new();
        public readonly List<bool> BuildSawStaleness = new();
        public readonly VideoLoadPump<FakeSource> Pump;

        public Rig()
        {
            Pump = new VideoLoadPump<FakeSource>(TeardownAsync, BuildAsync, s => s.Key == Volatile.Read(ref LiveKey));
        }

        public string[] Log { get { lock (_g) return _log.ToArray(); } }
        void Note(string s) { lock (_g) _log.Add(s); }

        async Task TeardownAsync(long epoch)
        {
            Note("teardown:start:" + LiveKey);
            if (TeardownGate is { } gate) await gate.Task.ConfigureAwait(false);
            Volatile.Write(ref LiveKey, "");
            Note("teardown:end");
        }

        async Task BuildAsync(FakeSource src, long epoch)
        {
            Note("build:start:" + src.Key);
            lock (_g) BuildEpochs.Add(epoch);
            if (BuildGate is { } gate) await gate.Task.ConfigureAwait(false);
            lock (_g) BuildSawStaleness.Add(Pump.IsStale(epoch));
            Volatile.Write(ref LiveKey, src.Key);
            Note("build:end:" + src.Key);
        }
    }

    static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 5_000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!cond())
        {
            Assert.True(Environment.TickCount64 < deadline, "condition never became true");
            await Task.Delay(5);
        }
    }

    /// <summary>The core invariant: no build may start while a teardown is in flight. Replayed over the whole log, this is
    /// the "a predecessor's process-global Stop can never land on its successor" guarantee.</summary>
    static void AssertNoBuildInsideATeardown(IReadOnlyList<string> log)
    {
        bool tearingDown = false;
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].StartsWith("teardown:start", StringComparison.Ordinal)) tearingDown = true;
            else if (log[i] == "teardown:end") tearingDown = false;
            else if (log[i].StartsWith("build:start", StringComparison.Ordinal))
                Assert.False(tearingDown, $"a build started while a teardown was still in flight at step {i} — log: {string.Join(" → ", log)}");
        }
    }

    // ── (1) serialization + coalescing ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SingleLoad_TearsDownThenBuilds_InThatOrder()
    {
        var rig = new Rig();
        rig.Pump.Request(new FakeSource("A"));
        await rig.Pump.WhenIdleAsync();

        Assert.Equal(new[] { "teardown:start:", "teardown:end", "build:start:A", "build:end:A" }, rig.Log);
    }

    [Fact]
    public async Task RapidVideoToVideo_FullyTearsTheFirstSessionDownBeforeTheSecondIsBuilt()
    {
        var rig = new Rig();
        rig.Pump.Request(new FakeSource("A"));
        await rig.Pump.WhenIdleAsync();

        // The skip: B arrives while A's teardown is still releasing the native session (the exact 250ms race from the log).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.TeardownGate = gate;
        rig.Pump.Request(new FakeSource("B"));
        await WaitUntilAsync(() => rig.Log.Contains("teardown:start:A"));

        // While A is still tearing down, nothing may have been built — the old code opened B's native session right here.
        Assert.DoesNotContain("build:start:B", rig.Log);

        rig.TeardownGate = null;
        gate.SetResult();
        await rig.Pump.WhenIdleAsync();

        AssertNoBuildInsideATeardown(rig.Log);
        Assert.Contains("build:end:B", rig.Log);
        Assert.Equal("B", rig.LiveKey);
    }

    [Fact]
    public async Task ThreeLoadsInFlight_OnlyTheLatestIsEverBuilt()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.TeardownGate = gate;

        rig.Pump.Request(new FakeSource("A"));
        await WaitUntilAsync(() => rig.Log.Contains("teardown:start:"));
        rig.Pump.Request(new FakeSource("B"));
        rig.Pump.Request(new FakeSource("C"));

        rig.TeardownGate = null;
        gate.SetResult();
        await rig.Pump.WhenIdleAsync();

        // A and B are both known-stale by the time their turn comes — a session we already know is stale is NEVER built.
        Assert.DoesNotContain("build:start:A", rig.Log);
        Assert.DoesNotContain("build:start:B", rig.Log);
        Assert.Contains("build:end:C", rig.Log);
        Assert.Equal("C", rig.LiveKey);
        AssertNoBuildInsideATeardown(rig.Log);
    }

    [Fact]
    public async Task ARequestArrivingDuringABuild_MakesThatBuildObserveItselfStale()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BuildGate = gate;

        rig.Pump.Request(new FakeSource("A"));
        await WaitUntilAsync(() => rig.Log.Contains("build:start:A"));
        rig.Pump.Request(new FakeSource("B"));   // the user skips again mid-open

        rig.BuildGate = null;
        gate.SetResult();
        await rig.Pump.WhenIdleAsync();

        // The in-flight build sees IsStale == true and (in the host) abandons publishing/opening its player.
        Assert.True(rig.BuildSawStaleness[0], "the superseded build did not observe its own staleness");
        Assert.Equal("B", rig.LiveKey);
        AssertNoBuildInsideATeardown(rig.Log);
    }

    [Fact]
    public async Task RedundantLoadOfTheLiveSource_IsDropped_NoTeardownNoRebuild()
    {
        var rig = new Rig();
        rig.Pump.Request(new FakeSource("A"));
        await rig.Pump.WhenIdleAsync();
        int before = rig.Log.Length;

        rig.Pump.Request(new FakeSource("A"));   // a placement flip / re-published source / kind re-evaluation
        await rig.Pump.WhenIdleAsync();

        Assert.Equal(before, rig.Log.Length);    // nothing happened — the video must never restart from 0
        Assert.Equal("A", rig.LiveKey);
    }

    [Fact]
    public async Task Clear_TearsDownWithoutBuilding_AndInvalidatesAQueuedLoad()
    {
        var rig = new Rig();
        rig.Pump.Request(new FakeSource("A"));
        await rig.Pump.WhenIdleAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.TeardownGate = gate;
        rig.Pump.Request(new FakeSource("B"));
        await WaitUntilAsync(() => rig.Log.Contains("teardown:start:A"));
        rig.Pump.RequestClear();                 // the host's Stop — it must not be overtaken by the load already on its way

        rig.TeardownGate = null;
        gate.SetResult();
        await rig.Pump.WhenIdleAsync();

        Assert.DoesNotContain("build:start:B", rig.Log);
        Assert.Equal("", rig.LiveKey);
    }

    // ── (2) the start watchdog ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LoadedButNeverStarts_FaultsOnce_AfterTheBound()
    {
        var wd = new VideoStartWatchdog(1_000);
        wd.Arm(0);

        // The wedge shape: play intent asserted, state never advances (PlaybackState.Idle publishes nothing at all).
        Assert.False(wd.ShouldFault(500, playIntent: true, progressed: false));
        Assert.False(wd.ShouldFault(1_000, playIntent: true, progressed: false));   // "> bound", not ">="
        Assert.True(wd.ShouldFault(1_001, playIntent: true, progressed: false));
        Assert.False(wd.ShouldFault(9_999, playIntent: true, progressed: false));   // exactly once — never a fault storm
        Assert.False(wd.IsArmed);
    }

    [Fact]
    public void PausedBeforeTheFirstFrame_NeverFaults_AndAResumeGetsAFreshBudget()
    {
        var wd = new VideoStartWatchdog(1_000);
        wd.Arm(0);

        // "Loaded, user paused before the first frame" is NOT a fault — the budget is re-based while intent is false.
        for (long t = 100; t <= 60_000; t += 100)
            Assert.False(wd.ShouldFault(t, playIntent: false, progressed: false));

        // Resuming starts the budget over from the resume instant rather than firing immediately.
        Assert.False(wd.ShouldFault(60_500, playIntent: true, progressed: false));
        Assert.True(wd.ShouldFault(61_101, playIntent: true, progressed: false));
    }

    [Fact]
    public void FirstProgress_DisarmsPermanently()
    {
        var wd = new VideoStartWatchdog(1_000);
        wd.Arm(0);

        Assert.False(wd.ShouldFault(500, playIntent: true, progressed: true));      // Playing / a positive position / Ended
        Assert.False(wd.IsArmed);
        Assert.False(wd.ShouldFault(999_999, playIntent: true, progressed: false)); // a later stall is not a START failure
    }

    [Fact]
    public void Teardown_Disarms_SoASupersededLoadNeverFaultsForItsSuccessor()
    {
        var wd = new VideoStartWatchdog(1_000);
        wd.Arm(0);
        wd.Disarm();
        Assert.False(wd.IsArmed);
        Assert.False(wd.ShouldFault(999_999, playIntent: true, progressed: false));

        wd.Arm(1_000_000);                                                          // re-armed by the successor's build
        Assert.False(wd.ShouldFault(1_000_500, playIntent: true, progressed: false));
        Assert.True(wd.ShouldFault(1_001_001, playIntent: true, progressed: false));
    }

    // ── (3) routing: the watchdog fault reaches the controller's existing error path ──────────────────────────────────

    sealed class TestSignals : IObservable<AudioHostSignal>
    {
        readonly List<IObserver<AudioHostSignal>> _subs = new();
        public IDisposable Subscribe(IObserver<AudioHostSignal> o) { _subs.Add(o); return new Unsub(this, o); }
        public void Emit(AudioHostSignal s) { foreach (var o in _subs.ToArray()) o.OnNext(s); }
        sealed class Unsub(TestSignals owner, IObserver<AudioHostSignal> o) : IDisposable
        { public void Dispose() => owner._subs.Remove(o); }
    }

    sealed class FakeAudioHost : IAudioHost
    {
        public readonly TestSignals Sig = new();
        public IObservable<AudioHostSignal> Signals => Sig;
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsBuffering => false;
        public bool ClockValid => true;
        public void Load(in AudioStreamHandle s) { }
        public void LoadFastStart(in AudioFastStart s) { }
        public void SupplyBody(in AudioStreamHandle s) { }
        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;
        public void Stop() => IsPlaying = false;
        public void Seek(long ms, SeekMode mode) { }
        public void SetVolume(double v) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FakeVideoHost : IMediaHost
    {
        public readonly TestSignals Sig = new();
        public IObservable<AudioHostSignal> Signals => Sig;
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool ClockValid => true;
        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;
        public void Stop() => IsPlaying = false;
        public void Seek(long ms, SeekMode mode) { }
        public void SetVolume(double v) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task WatchdogFault_WithNoRecoveryHook_SurfacesTheErrorAndLeavesNoPausedZombie()
    {
        var audio = new FakeAudioHost();
        var video = new FakeVideoHost();
        var projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var errors = new List<PlaybackErrorInfo>();
        using var controller = new PlaybackController(audio, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b"), "us", videoHost: video);
        controller.ShouldPlayAsVideo = _ => true;
        controller.LoadCurrentVideoAsync = (_, _, _) => Task.FromResult(true);
        controller.OnPlaybackError = e => { lock (errors) errors.Add(e); };

        await controller.PlayAsync("spotify:playlist:p");
        Assert.Equal(PlayableKind.Video, controller.CurrentMediaKind);

        // Exactly what the host's start watchdog emits when a load reports "loaded" but never advances.
        video.Sig.Emit(AudioHostSignal.Fault(0, AudioKeyFailureReason.None,
            "the video session never started playing (no progress within the start budget)"));
        await WaitUntilAsync(() => { lock (errors) return errors.Count > 0; });

        // The recovery hook is unwired here, so the error path runs — the user gets the error surface (with its retry),
        // never the silent paused-at-0:00 state the wedge produced.
        Assert.Single(errors);
        Assert.Contains("never started playing", errors[0].Detail);
        Assert.False(projection.IsPlaying);
    }

    [Fact]
    public async Task LegacyTransferWithoutSessionId_MarksPlaybackConnectOriginated_AndAllowsAudioFallback()
    {
        var audio = new FakeAudioHost();
        var video = new FakeVideoHost();
        var projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var errors = new List<PlaybackErrorInfo>();
        using var controller = new PlaybackController(audio, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b"), "us", videoHost: video);
        controller.ShouldPlayAsVideo = _ => true;
        controller.LoadCurrentVideoAsync = (_, _, _) => Task.FromResult(true);
        controller.OnPlaybackError = e => { lock (errors) errors.Add(e); };

        await controller.PlayAsync("spotify:playlist:p");
        Assert.Equal(PlayableKind.Video, controller.CurrentMediaKind);

        // Legacy/bare transfer has neither session_id nor inner data. It resumes from the cluster but is still a Connect
        // playback intent, so a subsequent video fault may fail soft to audio.
        var transfer = new ConnectCommand(
            ConnectCmd.Transfer, "transfer", "legacy-transfer", 9, "spotify-controller",
            0, false, "{}"u8.ToArray());
        await controller.HandleRemoteCommandAsync(transfer);
        video.Sig.Emit(AudioHostSignal.Fault(
            12_000, AudioKeyFailureReason.None, "legacy Connect video failed"));

        await WaitUntilAsync(() => controller.CurrentMediaKind == PlayableKind.Audio);

        lock (errors) Assert.Empty(errors);
    }

    [Fact]
    public async Task WatchdogFault_WithARecoveryHook_ReloadsInsteadOfLeavingTheTransportStuck()
    {
        var audio = new FakeAudioHost();
        var video = new FakeVideoHost();
        var projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var errors = new List<PlaybackErrorInfo>();
        int loads = 0;
        using var controller = new PlaybackController(audio, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b"), "us", videoHost: video);
        controller.ShouldPlayAsVideo = _ => true;
        controller.LoadCurrentVideoAsync = (_, _, _) => { Interlocked.Increment(ref loads); return Task.FromResult(true); };
        controller.OnPlaybackError = e => { lock (errors) errors.Add(e); };

        await controller.PlayAsync("spotify:playlist:p");
        int before = Volatile.Read(ref loads);
        controller.TryRecoverVideoAsync = (_, _) => Task.FromResult(true);

        video.Sig.Emit(AudioHostSignal.Fault(0, AudioKeyFailureReason.None,
            "the video session never started playing (no progress within the start budget)"));
        await WaitUntilAsync(() => Volatile.Read(ref loads) > before);

        lock (errors) Assert.Empty(errors);   // recovered → a reload, not the error surface
    }
}
