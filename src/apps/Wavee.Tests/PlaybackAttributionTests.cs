using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// ── "the app jumped to another song by itself" — the attribution + no-spontaneous-play regression suite ───────────────
//
// A 2026-07-27 session (sid=226ade49) executed a full context re-resolution 2.4 s after the user closed a video, with no
// input. The log could not name the caller: ExecutePlayAsync, LocalPlaySpecAsync and the (network-latency-bearing) context
// resolve were ALL silent, so the first evidence was a bare `head … fetch start` — which looks identical whether it came
// from a click, a dealer command, a media key, or a queue step. These tests pin:
//
//   1. every play intent is attributed AT THE CHOKEPOINT (origin + context + skip target + route), and
//   2. a queue STEP is distinguishable from a fresh context PLAY, and
//   3. the paths that fold REMOTE state (a cluster push, a PutState echo) can never execute a play by themselves.
//
// (3) is the load-bearing one: it fences the whole "the server corrected us" hypothesis in code rather than in prose.
public class PlaybackAttributionTests
{
    sealed class RecordingHost : IAudioHost
    {
        public readonly List<string> Calls = new();
        readonly SimpleSubject<AudioHostSignal> _sig = new();
        public IObservable<AudioHostSignal> Signals => _sig;
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsBuffering { get; private set; }
        public bool ClockValid => true;
        public void Load(in AudioStreamHandle s) => Calls.Add("load:" + s.TrackUri);
        public void LoadFastStart(in AudioFastStart s) => Calls.Add("faststart:" + s.TrackUri);
        public void SupplyBody(in AudioStreamHandle s) => Calls.Add("body:" + s.TrackUri);
        public void Play() { IsPlaying = true; Calls.Add("play"); }
        public void Pause() { IsPlaying = false; Calls.Add("pause"); }
        public void Stop() { IsPlaying = false; Calls.Add("stop"); }
        public void Seek(long ms, SeekMode mode) { PositionMs = ms; Calls.Add("seek:" + ms); }
        public void SetVolume(double v) => Calls.Add("vol");
        public void Emit(AudioHostSignal s) { IsBuffering = s.IsBuffering; _sig.OnNext(s); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A stand-in for FluentVideoMediaHost: the controller only needs the common IMediaHost verbs to swap onto it.
    sealed class RecordingVideoHost : IMediaHost
    {
        public readonly List<string> Calls = new();
        readonly SimpleSubject<AudioHostSignal> _sig = new();
        public IObservable<AudioHostSignal> Signals => _sig;
        public long PositionMs => 0;
        public bool IsPlaying { get; private set; }
        public bool ClockValid => true;
        public void Play() { IsPlaying = true; Calls.Add("play"); }
        public void Pause() { IsPlaying = false; Calls.Add("pause"); }
        public void Stop() { IsPlaying = false; Calls.Add("stop"); }
        public void Seek(long ms, SeekMode mode) => Calls.Add("seek:" + ms);
        public void SetVolume(double v) => Calls.Add("vol");
        public void Emit(AudioHostSignal s) => _sig.OnNext(s);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static RemoteTrack Remote(string uri, long dur = 200000) =>
        new(uri, "G", "A", "spotify:artist:a", "Al", "spotify:album:al", null, dur);

    static ClusterDelta Cluster(string active, RemoteTrack? track = null, long pos = 0, bool playing = true) =>
        new(active, track is not null, track ?? default, "spotify:playlist:remote",
            playing, !playing, false, pos, 0, 0, track?.DurationMs ?? 0, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

    static (PlaybackController C, RecordingHost H, NowPlayingProjection P, CapturingWaveeLog L) Make(
        IContextResolver? ctx = null, IOutboundControl? outbound = null, IMediaHost? video = null)
    {
        var host = new RecordingHost();
        var proj = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var log = new CapturingWaveeLog();
        var c = new PlaybackController(host, new StubTrackResolver(), proj,
            ctx ?? new FakeContextResolver(new[] { "spotify:track:a", "spotify:track:b", "spotify:track:c" }),
            "us", outbound, null, new WaveeLogger(log, "playback"), videoHost: video);
        return (c, host, proj, log);
    }

    static bool Has(CapturingWaveeLog log, string fragment) =>
        log.Entries.Any(e => e.Level == WaveeLogLevel.Info && e.Message.Contains(fragment, StringComparison.Ordinal));

    static string? Line(CapturingWaveeLog log, string fragment) =>
        log.Entries.FirstOrDefault(e => e.Message.Contains(fragment, StringComparison.Ordinal)).Message;

    // ── 1. attribution ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LocalContextPlay_LogsOneAttributedPlayIntent()
    {
        var (c, _, _, log) = Make();
        using (c) await c.PlayAsync("spotify:playlist:p", 0);

        var line = Line(log, "play intent");
        Assert.NotNull(line);
        Assert.Contains("origin=play-context", line);
        Assert.Contains("route=local", line);
        Assert.Contains("ctx=spotify:playlist:p", line);
        // …and the other half of the pair: what the (silent) resolve actually decided to play.
        Assert.Contains("play resolved", Line(log, "play resolved") ?? "");
        Assert.Contains("current=spotify:track:a", Line(log, "play resolved")!);
    }

    [Fact]
    public async Task RowClickPlay_CarriesTheSkipTargetIntoTheLog()
    {
        var (c, _, _, log) = Make();
        using (c)
            await c.PlayContextTrackAsync("spotify:playlist:p", new PlaybackContextTrack("spotify:track:b", "uid-b"), 1);

        var line = Line(log, "play intent");
        Assert.NotNull(line);
        Assert.Contains("origin=play-context-track", line);
        Assert.Contains("skipUri=spotify:track:b", line);
        Assert.Contains("skipUid=uid-b", line);
    }

    [Fact]
    public async Task ForwardedPlay_IsLoggedAsForward_NotLocal()
    {
        var outbound = new NullOutbound();
        var (c, host, proj, log) = Make(outbound: outbound);
        using (c)
        {
            proj.OnCluster(Cluster("other-device", Remote("spotify:track:remote")));
            await c.PlayAsync("spotify:playlist:p", 0);
        }
        Assert.Contains("route=forward", Line(log, "play intent") ?? "");
        Assert.DoesNotContain("faststart:", string.Join(",", host.Calls));
        Assert.DoesNotContain("load:", string.Join(",", host.Calls));
    }

    // ── 2. a queue STEP is not a context PLAY ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task QueueAdvance_IsLoggedAsAStep_AndNeverAsAPlayIntent()
    {
        var (c, _, _, log) = Make();
        using (c)
        {
            await c.PlayAsync("spotify:playlist:p", 0);
            int playIntentsAfterPlay = log.Entries.Count(e => e.Message.StartsWith("play intent", StringComparison.Ordinal));
            await c.NextAsync();

            Assert.True(Has(log, "queue advance → spotify:track:b"));
            // A Next must NOT look like a fresh context resolution in the log — that ambiguity is what made the
            // 2026-07-27 jump unattributable.
            Assert.Equal(playIntentsAfterPlay, log.Entries.Count(e => e.Message.StartsWith("play intent", StringComparison.Ordinal)));
        }
    }

    // ── 3. remote STATE folds can never start playback ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClusterFold_RightAfterALocalReload_DoesNotReExecuteAPlay()
    {
        // The exact shape of the reported bug: a local operation settles, then remote state (a dealer cluster push, or the
        // Cluster body a PutState response carries — both walk the SAME fold path) arrives naming us active with a
        // DIFFERENT current track. Folding it must move the projection only; it must never resolve a context or touch the
        // host. If this ever fails, "the server corrected us" becomes a live hypothesis again.
        var (c, host, proj, log) = Make();
        using (c)
        {
            await c.PlayAsync("spotify:playlist:p", 0);
            host.Calls.Clear();
            int before = log.Entries.Count(e => e.Message.StartsWith("play intent", StringComparison.Ordinal));

            for (int i = 0; i < 5; i++)
            {
                proj.OnCluster(Cluster("us", Remote("spotify:track:previous"), pos: 190488));
                proj.OnCluster(Cluster("", Remote("spotify:track:previous"), pos: 190488, playing: false));
            }
            await Task.Delay(30);

            // A fold may STOP us (an empty/foreign active device deactivates local playback — that is the routing spine),
            // but it must never load, resolve, or start anything.
            Assert.DoesNotContain(host.Calls, s => s.StartsWith("load:", StringComparison.Ordinal)
                                                || s.StartsWith("faststart:", StringComparison.Ordinal)
                                                || s == "play");
            Assert.Equal(before, log.Entries.Count(e => e.Message.StartsWith("play intent", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task ClusterFold_NamingAnotherDeviceActive_StopsLocally_ButNeverStartsAnything()
    {
        var (c, host, proj, _) = Make();
        using (c)
        {
            await c.PlayAsync("spotify:playlist:p", 0);
            host.Calls.Clear();
            proj.OnCluster(Cluster("phone", Remote("spotify:track:elsewhere")));
            await Task.Delay(30);

            Assert.DoesNotContain(host.Calls, s => s.StartsWith("load:", StringComparison.Ordinal)
                                                || s.StartsWith("faststart:", StringComparison.Ordinal)
                                                || s == "play");
        }
    }

    // ── 4. the buffering indicator cannot outlive the host that raised it ────────────────────────────────────────────

    [Fact]
    public async Task HostSwap_RetiresABufferingStateTheOutgoingHostCanNoLongerClear()
    {
        // The video ✕ case: the video host is stopped (and its signal subscription disposed) while it is still reporting
        // Buffering, so the Playing/Ended edge that would clear the spinner is never delivered — the indicator latched
        // over the audio that took over. The swap itself must retire it.
        var video = new RecordingVideoHost();
        var (c, host, proj, _) = Make(video: video);
        using (c)
        {
            bool asVideo = true;
            c.ShouldPlayAsVideo = _ => asVideo;
            c.LoadCurrentVideoAsync = (_, _, _) => Task.FromResult(true);

            await c.PlayAsync("spotify:playlist:p", 0);
            video.Emit(new AudioHostSignal(AudioHostSignalKind.Buffering, 0));
            Assert.True(proj.IsBuffering);

            asVideo = false;                       // the user closed the surface → sticky-off
            await c.RefreshCurrentMediaKindAsync();

            Assert.False(proj.IsBuffering);        // …and the spinner went with it
            Assert.Contains("stop", video.Calls);
        }
    }

    [Fact]
    public async Task CompletedLoad_LeavesNoLatchedBuffering()
    {
        var (c, host, proj, _) = Make();
        using (c)
        {
            await c.PlayAsync("spotify:playlist:p", 0);
            host.Emit(new AudioHostSignal(AudioHostSignalKind.Buffering, 0));
            Assert.True(proj.IsBuffering);
            host.Emit(new AudioHostSignal(AudioHostSignalKind.Playing, 120));
            Assert.False(proj.IsBuffering);
        }
    }

    // ── 5. a video that never happens must tell the app, or its surface waits forever ────────────────────────────────

    [Fact]
    public async Task NoPlayableVideoSource_NotifiesTheAppAndPlaysAudio()
    {
        var video = new RecordingVideoHost();
        var (c, host, _, _) = Make(video: video);
        using (c)
        {
            var dead = new List<string>();
            Task? pendingVideoLoad = null;
            c.ShouldPlayAsVideo = _ => true;
            c.LoadCurrentVideoAsync = (_, _, _) => Task.FromResult(false);   // resolved to nothing → fall back to audio
            c.OnVideoMediaUnavailable = t => dead.Add(t.Uri);
            c.VideoLoadSpawnedForTest = t => pendingVideoLoad = t;

            await c.PlayAsync("spotify:playlist:p", 0);
            // The video branch resolves off-lock now (video-smooth-switching Milestone C): the "no source" fallback
            // re-acquires the controller's lock, which PlayAsync itself is still holding at the moment this spawns —
            // so it only settles AFTER PlayAsync returns. Await it explicitly rather than assuming it already ran.
            if (pendingVideoLoad is { } t) await t;

            Assert.Equal(new[] { "spotify:track:a" }, dead);
            Assert.Contains("load:spotify:track:a", host.Calls);   // the song still plays — just as audio
        }
    }

    [Fact]
    public async Task VideoOpenError_NotifiesTheApp()
    {
        var video = new RecordingVideoHost();
        var (c, _, _, _) = Make(video: video);
        using (c)
        {
            var dead = new List<string>();
            c.ShouldPlayAsVideo = _ => true;
            c.LoadCurrentVideoAsync = (_, _, _) => Task.FromResult(true);
            c.OnVideoMediaUnavailable = t => dead.Add(t.Uri);

            await c.PlayAsync("spotify:playlist:p", 0);
            video.Emit(new AudioHostSignal(AudioHostSignalKind.Error, 0));
            await Task.Delay(30);

            Assert.Equal(new[] { "spotify:track:a" }, dead);
        }
    }

    [Fact]
    public async Task HealthyVideo_NeverReportsUnavailable()
    {
        var video = new RecordingVideoHost();
        var (c, _, _, _) = Make(video: video);
        using (c)
        {
            var dead = new List<string>();
            c.ShouldPlayAsVideo = _ => true;
            c.LoadCurrentVideoAsync = (_, _, _) => Task.FromResult(true);
            c.OnVideoMediaUnavailable = t => dead.Add(t.Uri);

            await c.PlayAsync("spotify:playlist:p", 0);
            video.Emit(new AudioHostSignal(AudioHostSignalKind.Playing, 500));
            await Task.Delay(20);

            Assert.Empty(dead);
        }
    }

    sealed class NullOutbound : IOutboundControl
    {
        public Task<OutboundResult> SendAsync(string t, string j, CancellationToken ct = default)
            => Task.FromResult(new OutboundResult(true, "ack", 200));
        public Task<OutboundResult> SetVolumeAsync(string t, int v, CancellationToken ct = default)
            => Task.FromResult(new OutboundResult(true, "ack", 200));
        public Task<OutboundResult> TransferAsync(string f, string t, CancellationToken ct = default, bool hostingVideo = false)
            => Task.FromResult(new OutboundResult(true, "ack", 200));
    }
}
