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
// (1) below pins the PUMP's half of the fix against PRODUCTION VideoLoadPump code (it is engine-free on purpose, so
// these tests drive the real class rather than a mock of it — the PlacementCore/MediaSwitchLogic discipline):
//   • every clear and every apply run ONE AT A TIME, never overlapping — the process-global session is never touched by
//     two in-flight operations at once;
//   • a request that is already superseded before it is DEQUEUED is never applied at all (latest-wins coalescing);
//   • a RequestClear() (the host's Stop) overtakes any load already queued behind it.
// The video-smooth-switching rework moved the OLDER "teardown always precedes build" guarantee out of the pump and into
// the host (FluentVideoMediaHost.ApplyAsync + VideoSwitchPolicy, covered by VideoSwitchPolicyTests) — a video→video
// switch to a different, healthy source no longer tears anything down at all, which is exactly the "apply for a new key
// does not invoke clear" case pinned below.
//
// (2) pins VideoStartWatchdog: a load that never reaches a playing/advancing state raises exactly ONE fault, never fires
// for a deliberately paused session, and disarms on progress and on teardown.
// (3) pins the routing leg: that fault travels the ordinary AudioHostSignal channel into PlaybackController.OnHostSignal
// and out through the existing error path, so the paused-at-0:00 zombie state is impossible.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public class VideoLoadSupersessionTests
{
    // ── the pump rig: fake clear/apply steps that append to ONE ordered log, so "A finished before B started" is a
    //    provable ordering fact rather than two independent counters (the HostSwap-tests shape).
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
        public TaskCompletionSource? ClearGate;
        public TaskCompletionSource? ApplyGate;
        public readonly List<long> ApplyEpochs = new();
        public readonly List<bool> ApplySawStaleness = new();
        public int ClearCalls;
        public int ApplyCalls;
        public readonly VideoLoadPump<FakeSource> Pump;

        public Rig()
        {
            Pump = new VideoLoadPump<FakeSource>(ClearAsync, ApplyAsync);
        }

        public string[] Log { get { lock (_g) return _log.ToArray(); } }
        void Note(string s) { lock (_g) _log.Add(s); }

        async Task ClearAsync(long epoch)
        {
            Interlocked.Increment(ref ClearCalls);
            Note("clear:start:" + Volatile.Read(ref LiveKey));
            if (ClearGate is { } gate) await gate.Task.ConfigureAwait(false);
            Volatile.Write(ref LiveKey, "");
            Note("clear:end");
        }

        async Task ApplyAsync(FakeSource src, long epoch)
        {
            Interlocked.Increment(ref ApplyCalls);
            Note("apply:start:" + src.Key);
            lock (_g) ApplyEpochs.Add(epoch);
            if (ApplyGate is { } gate) await gate.Task.ConfigureAwait(false);
            lock (_g) ApplySawStaleness.Add(Pump.IsStale(epoch));
            Volatile.Write(ref LiveKey, src.Key);
            Note("apply:end:" + src.Key);
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

    /// <summary>The core invariant: no apply may start while a clear is in flight (and vice versa). Replayed over the
    /// whole log, this is the "the process-global session is never touched by two in-flight operations at once"
    /// guarantee.</summary>
    static void AssertNeverOverlapping(IReadOnlyList<string> log)
    {
        bool clearing = false;
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].StartsWith("clear:start", StringComparison.Ordinal)) clearing = true;
            else if (log[i] == "clear:end") clearing = false;
            else if (log[i].StartsWith("apply:start", StringComparison.Ordinal))
                Assert.False(clearing, $"an apply started while a clear was still in flight at step {i} — log: {string.Join(" → ", log)}");
        }
    }

    // ── (1) serialization + coalescing over the clear/apply contract ────────────────────────────────────────────────

    [Fact]
    public async Task SingleLoad_AppliesDirectly_NoImplicitClear()
    {
        var rig = new Rig();
        rig.Pump.Request(new FakeSource("A"));
        await rig.Pump.WhenIdleAsync();

        // No teardown precedes the very first load anymore — clear only ever runs for a RequestClear() (the host's
        // Stop) or when the HOST itself, not the pump, decides a Rebuild needs one.
        Assert.Equal(new[] { "apply:start:A", "apply:end:A" }, rig.Log);
        Assert.Equal(0, rig.ClearCalls);
        Assert.Equal("A", rig.LiveKey);
    }

    [Fact]
    public async Task ApplyForANewKey_DoesNotInvokeClear()
    {
        var rig = new Rig();
        rig.Pump.Request(new FakeSource("A"));
        await rig.Pump.WhenIdleAsync();

        // The video→video switch-in-place shape: a different key, no teardown anywhere in the pump.
        rig.Pump.Request(new FakeSource("B"));
        await rig.Pump.WhenIdleAsync();

        Assert.Equal(new[] { "apply:start:A", "apply:end:A", "apply:start:B", "apply:end:B" }, rig.Log);
        Assert.Equal(0, rig.ClearCalls);
        Assert.Equal("B", rig.LiveKey);
    }

    [Fact]
    public async Task ThreeLoadsInFlight_OnlyTheLatestIsEverApplied()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.ApplyGate = gate;

        rig.Pump.Request(new FakeSource("A"));
        await WaitUntilAsync(() => rig.Log.Contains("apply:start:A"));
        rig.Pump.Request(new FakeSource("B"));
        rig.Pump.Request(new FakeSource("C"));

        rig.ApplyGate = null;
        gate.SetResult();
        await rig.Pump.WhenIdleAsync();

        // A's own apply was already in flight when B/C arrived, so the pump lets it finish (an apply in flight is never
        // aborted mid-way — that is the HOST's job via IsStale). B never even reaches the coalescing slot's front: only
        // the LATEST pending request (C) survives it.
        Assert.Contains("apply:end:A", rig.Log);
        Assert.DoesNotContain("apply:start:B", rig.Log);
        Assert.Contains("apply:start:C", rig.Log);
        Assert.Contains("apply:end:C", rig.Log);
        Assert.Equal("C", rig.LiveKey);
    }

    [Fact]
    public async Task ARequestArrivingDuringAnApply_MakesThatApplyObserveItselfStale()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.ApplyGate = gate;

        rig.Pump.Request(new FakeSource("A"));
        await WaitUntilAsync(() => rig.Log.Contains("apply:start:A"));
        rig.Pump.Request(new FakeSource("B"));   // the user skips again mid-open

        rig.ApplyGate = null;
        gate.SetResult();
        await rig.Pump.WhenIdleAsync();

        // The in-flight apply sees IsStale == true and (in the host) abandons publishing/opening its player.
        Assert.True(rig.ApplySawStaleness[0], "the superseded apply did not observe its own staleness");
        Assert.Equal("B", rig.LiveKey);
    }

    [Fact]
    public async Task Clear_InvalidatesAQueuedLoad_AndNeverApplies()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.ApplyGate = gate;

        rig.Pump.Request(new FakeSource("A"));
        await WaitUntilAsync(() => rig.Log.Contains("apply:start:A"));
        rig.Pump.Request(new FakeSource("B"));       // queued behind A's in-flight apply
        rig.Pump.RequestClear();                     // must overtake B — the host's Stop() call

        rig.ApplyGate = null;
        gate.SetResult();
        await rig.Pump.WhenIdleAsync();

        Assert.Contains("apply:end:A", rig.Log);      // A, already in flight, still finishes
        Assert.DoesNotContain("apply:start:B", rig.Log);
        Assert.Contains(rig.Log, l => l.StartsWith("clear:start", StringComparison.Ordinal));
        Assert.Contains("clear:end", rig.Log);
        Assert.Equal("", rig.LiveKey);
        AssertNeverOverlapping(rig.Log);
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
