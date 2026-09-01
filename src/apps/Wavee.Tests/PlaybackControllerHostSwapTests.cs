using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// M0 — "Truth: one media, one host, one player". These tests drive the REAL PlaybackController (source-included here via
// ..\Wavee\Backend\**) over TWO fake IMediaHosts that append to ONE shared, ordered call log — so "the outgoing host was
// stopped BEFORE the incoming host played" is a provable ordering fact, not two independent counters.
//
// What is pinned:
//   (a) at most ONE host is in a playing state at any point across audio→video→audio,
//   (b) the outgoing host received Stop BEFORE the incoming host received Play,
//   (c) transport verbs (Play/Pause/Seek) route to the CURRENT host only, and exactly one host feeds the signal channel,
//   (d) KindFor follows ShouldPlayAsVideo (and stays Audio when the hooks are unwired — the kill-switch / audio-only shape),
//   (e) a video playable with no resolvable source falls back to AUDIO instead of leaving the user in silence.
public class PlaybackControllerHostSwapTests
{
    // ── the fakes ────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A host signal channel that reports its live subscriber count, so "exactly one host feeds OnHostSignal"
    /// is assertable structurally (the controller moves its subscription at every swap).</summary>
    sealed class TestSignals : IObservable<AudioHostSignal>
    {
        readonly List<IObserver<AudioHostSignal>> _subs = new();
        public int SubscriberCount => _subs.Count;

        public IDisposable Subscribe(IObserver<AudioHostSignal> observer)
        {
            _subs.Add(observer);
            return new Unsub(this, observer);
        }

        public void Emit(AudioHostSignal s)
        {
            foreach (var o in _subs.ToArray()) o.OnNext(s);
        }

        sealed class Unsub(TestSignals owner, IObserver<AudioHostSignal> observer) : IDisposable
        {
            public void Dispose() => owner._subs.Remove(observer);
        }
    }

    sealed class FakeAudioHost(List<string> shared) : IAudioHost
    {
        public readonly List<string> Calls = new();
        public readonly TestSignals Sig = new();
        void Note(string call) { Calls.Add(call); shared.Add("audio:" + call); }

        public IObservable<AudioHostSignal> Signals => Sig;
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsBuffering => false;
        public bool ClockValid => true;
        public void Load(in AudioStreamHandle s) => Note("load:" + s.TrackUri);
        public void LoadFastStart(in AudioFastStart s) => Note("faststart:" + s.TrackUri);
        public void SupplyBody(in AudioStreamHandle s) => Note("body:" + s.TrackUri);
        public void Play() { IsPlaying = true; Note("play"); }
        public void Pause() { IsPlaying = false; Note("pause"); }
        public void Stop() { IsPlaying = false; Note("stop"); }
        public void Seek(long ms, SeekMode mode) { PositionMs = ms; Note("seek:" + ms); }
        public void SetVolume(double v) => Note("vol");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The video half of the ONE current media: only the COMMON IMediaHost transport (loading is kind-specific and
    /// happens through the controller's LoadCurrentVideoAsync hook — exactly like FluentVideoMediaHost.LoadVideo).</summary>
    sealed class FakeVideoHost(List<string> shared) : IMediaHost
    {
        public readonly List<string> Calls = new();
        public readonly TestSignals Sig = new();
        void Note(string call) { Calls.Add(call); shared.Add("video:" + call); }

        public IObservable<AudioHostSignal> Signals => Sig;
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool ClockValid => true;
        public void Play() { IsPlaying = true; Note("play"); }
        public void Pause() { IsPlaying = false; Note("pause"); }
        public void Stop() { IsPlaying = false; Note("stop"); }
        public void Seek(long ms, SeekMode mode) { PositionMs = ms; Note("seek:" + ms); }
        public void SetVolume(double v) => Note("vol");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>Mirrors FluentVideoMediaHost.LoadVideo: the HOST owns the player; a surface only presents it. The
        /// controller never sees a player, so the fake just records that a source was loaded.</summary>
        public void LoadVideo(string sourceKey) => Note("loadvideo:" + sourceKey);
    }

    sealed class Harness : IDisposable
    {
        public readonly List<string> Log = new();
        public readonly FakeAudioHost Audio;
        public readonly FakeVideoHost? Video;
        public readonly NowPlayingProjection Projection;
        public readonly PlaybackController Controller;
        public readonly FakeContextResolver Contexts;

        /// <summary>The app-level "play this as video" intent the bridge would supply (PlaybackBridge.ShouldPlayAsVideo).</summary>
        public bool VideoIntent;
        /// <summary>The app-level "this playable has NO audio form at all" answer (ModulePlayables.HasVideo →
        /// PlaybackController.IsVideoOnlyPlayable). Only consulted when the harness wired that hook (<c>videoOnly: true</c>).</summary>
        public bool VideoOnlyPlayable;
        /// <summary>Whether the video source resolve succeeds (false = the account isn't served a playable video).</summary>
        public bool VideoSourceAvailable = true;
        public int LoadVideoCalls;
        /// <summary>The start position the LAST video load was asked to begin at. The position carried across an
        /// audio→video switch rides the LOAD (not a follow-up Seek) because the real host has no player yet at that
        /// moment — so this, not a "video:seek:N" log entry, is where the carry is observable.</summary>
        public long LastVideoStartAtMs = -1;
        /// <summary>The most recent detached video-load task (StartVideoLoadDetached → RunVideoLoadAsync,
        /// video-smooth-switching Milestone C). The controller never awaits this itself — the fake hooks below
        /// complete synchronously, so a SUCCESSFUL load has already settled by the time PlayAsync/RefreshCurrentMediaKindAsync
        /// returns; only the no-source→audio fallback re-acquires the controller's lock (still held by the outer call at
        /// the moment it is spawned) and therefore genuinely completes later — <see cref="AwaitVideoLoadAsync"/> is for
        /// exactly that case.</summary>
        Task? _pendingVideoLoad;

        public Harness(bool wireHooks = true, bool injectVideoHost = true, bool wireLoadHook = true,
            string[]? contextTracks = null, bool videoOnly = false)
        {
            Audio = new FakeAudioHost(Log);
            Video = injectVideoHost ? new FakeVideoHost(Log) : null;
            Projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
            Contexts = new FakeContextResolver(contextTracks ?? ["spotify:track:a", "spotify:track:b"]);
            Controller = new PlaybackController(Audio, new StubTrackResolver(), Projection, Contexts, "us", videoHost: Video);
            Controller.VideoLoadSpawnedForTest = t => _pendingVideoLoad = t;
            VideoOnlyPlayable = videoOnly;
            // Left NULL unless asked for, so every other test keeps the "every playable is choosable" default verbatim.
            if (videoOnly) Controller.IsVideoOnlyPlayable = _ => VideoOnlyPlayable;
            if (!wireHooks) return;
            Controller.ShouldPlayAsVideo = _ => VideoIntent;
            if (!wireLoadHook) return;
            Controller.LoadCurrentVideoAsync = (track, startAtMs, ct) =>
            {
                LoadVideoCalls++;
                LastVideoStartAtMs = startAtMs;
                if (!VideoSourceAvailable) return Task.FromResult(false);
                Video?.LoadVideo(track.Uri);
                return Task.FromResult(true);
            };
        }

        /// <summary>Await the most recently spawned detached video load — needed ONLY when it can fall back to audio
        /// (VideoSourceAvailable = false), since that fallback re-enters the controller's lock and therefore cannot
        /// finish until the outer PlayAsync/RefreshCurrentMediaKindAsync call has released it.</summary>
        public async Task AwaitVideoLoadAsync()
        {
            if (_pendingVideoLoad is { } t) await t;
        }

        public void Dispose() => Controller.Dispose();
    }

    // ── ordering / exclusivity invariants over the shared log ────────────────────────────────────────────────────────
    /// <summary>Replay the shared call log and assert that at NO prefix are both hosts in a playing state. This is the
    /// "one audio stream" invariant, checked at every step rather than only at the end.</summary>
    static void AssertNeverTwoHostsPlaying(IReadOnlyList<string> log)
    {
        bool audio = false, video = false;
        for (int i = 0; i < log.Count; i++)
        {
            switch (log[i])
            {
                case "audio:play": audio = true; break;
                case "audio:pause":
                case "audio:stop": audio = false; break;
                case "video:play": video = true; break;
                case "video:pause":
                case "video:stop": video = false; break;
            }
            Assert.False(audio && video,
                $"two hosts playing after step {i} ('{log[i]}') — log: {string.Join(" → ", log)}");
        }
    }

    static int First(IReadOnlyList<string> log, string call)
    {
        for (int i = 0; i < log.Count; i++) if (log[i] == call) return i;
        return -1;
    }

    static int Last(IReadOnlyList<string> log, string call)
    {
        for (int i = log.Count - 1; i >= 0; i--) if (log[i] == call) return i;
        return -1;
    }

    // ── (d) KindFor follows ShouldPlayAsVideo ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task UnwiredHooks_EveryPlayableIsAudio_AndTheVideoHostIsNeverTouched()
    {
        using var h = new Harness(wireHooks: false);
        await h.Controller.PlayAsync("spotify:playlist:p");

        Assert.Equal(PlayableKind.Audio, h.Controller.CurrentMediaKind);
        Assert.Contains("audio:load:spotify:track:a", h.Log);
        Assert.Contains("audio:play", h.Log);
        Assert.Empty(h.Video!.Calls);            // the swap is inert without the hooks (the unit-test / audio-only shape)
        Assert.Equal(0, h.LoadVideoCalls);
    }

    [Fact]
    public async Task VideoIntent_MakesTheCurrentKindVideo_AndTheAudioIntentMakesItAudioAgain()
    {
        using var h = new Harness();
        await h.Controller.PlayAsync("spotify:playlist:p");
        Assert.Equal(PlayableKind.Audio, h.Controller.CurrentMediaKind);

        h.VideoIntent = true;
        await h.Controller.RefreshCurrentMediaKindAsync();
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);

        h.VideoIntent = false;
        await h.Controller.RefreshCurrentMediaKindAsync();
        Assert.Equal(PlayableKind.Audio, h.Controller.CurrentMediaKind);
    }

    [Fact]
    public async Task RefreshCurrentMediaKind_IsANoOp_WhenTheKindIsUnchanged()
    {
        using var h = new Harness();
        await h.Controller.PlayAsync("spotify:playlist:p");
        int before = h.Log.Count;

        await h.Controller.RefreshCurrentMediaKindAsync();   // intent still audio → nothing to swap

        Assert.Equal(before, h.Log.Count);
    }

    [Fact]
    public async Task RefreshCurrentMediaKind_IsANoOp_WhenTheHooksAreUnwired()
    {
        using var h = new Harness(wireHooks: false);
        await h.Controller.PlayAsync("spotify:playlist:p");
        int before = h.Log.Count;

        h.VideoIntent = true;                                // ignored — ShouldPlayAsVideo is null (kill switch off)
        await h.Controller.RefreshCurrentMediaKindAsync();

        Assert.Equal(before, h.Log.Count);
        Assert.Equal(PlayableKind.Audio, h.Controller.CurrentMediaKind);
    }

    // ── (a) + (b) one host playing, stop strictly before play ────────────────────────────────────────────────────────
    [Fact]
    public async Task AudioToVideoToAudio_NeverHasTwoHostsPlaying()
    {
        using var h = new Harness();
        await h.Controller.PlayAsync("spotify:playlist:p");
        h.VideoIntent = true;
        await h.Controller.RefreshCurrentMediaKindAsync();
        h.VideoIntent = false;
        await h.Controller.RefreshCurrentMediaKindAsync();

        AssertNeverTwoHostsPlaying(h.Log);
        Assert.False(h.Audio.IsPlaying && h.Video!.IsPlaying);
    }

    [Fact]
    public async Task AudioToVideo_StopsTheAudioHostBeforeTheVideoHostPlays()
    {
        using var h = new Harness();
        await h.Controller.PlayAsync("spotify:playlist:p");
        Assert.True(h.Audio.IsPlaying);

        h.VideoIntent = true;
        await h.Controller.RefreshCurrentMediaKindAsync();

        int audioPause = First(h.Log, "audio:pause");
        int audioStop = First(h.Log, "audio:stop");
        int videoPlay = First(h.Log, "video:play");
        Assert.True(audioPause >= 0, "the outgoing audio host was never paused: " + string.Join(" → ", h.Log));
        Assert.True(audioStop > audioPause, "Pause must precede Stop on the outgoing host: " + string.Join(" → ", h.Log));
        Assert.True(videoPlay > audioStop, "the incoming video host played BEFORE the audio host stopped: " + string.Join(" → ", h.Log));
        Assert.False(h.Audio.IsPlaying);
        Assert.True(h.Video!.IsPlaying);
    }

    [Fact]
    public async Task VideoToAudio_StopsTheVideoHostBeforeTheAudioHostLoadsAndPlays()
    {
        using var h = new Harness();
        h.VideoIntent = true;
        await h.Controller.PlayAsync("spotify:playlist:p");
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);
        Assert.True(h.Video!.IsPlaying);

        h.VideoIntent = false;
        await h.Controller.RefreshCurrentMediaKindAsync();

        int videoStop = First(h.Log, "video:stop");
        int audioLoad = Last(h.Log, "audio:load:spotify:track:a");
        int audioPlay = Last(h.Log, "audio:play");
        Assert.True(videoStop >= 0, "the outgoing video host was never stopped: " + string.Join(" → ", h.Log));
        Assert.True(audioLoad > videoStop, "the incoming audio host loaded BEFORE the video host stopped: " + string.Join(" → ", h.Log));
        Assert.True(audioPlay > videoStop, "the incoming audio host played BEFORE the video host stopped: " + string.Join(" → ", h.Log));
        Assert.False(h.Video.IsPlaying);
        Assert.True(h.Audio.IsPlaying);
        AssertNeverTwoHostsPlaying(h.Log);
    }

    [Fact]
    public async Task VideoTrackBoundary_StopsTheAudioHostBeforeTheVideoSourceIsEvenLoaded()
    {
        // The stop happens at the SWITCH, i.e. before the (async, networked) source resolve — so there is no window in
        // which the song is still playing while the video is being resolved.
        using var h = new Harness();
        await h.Controller.PlayAsync("spotify:playlist:p");
        h.VideoIntent = true;
        await h.Controller.RefreshCurrentMediaKindAsync();

        int audioStop = First(h.Log, "audio:stop");
        int loadVideo = First(h.Log, "video:loadvideo:spotify:track:a");
        Assert.True(loadVideo > audioStop, "the video source was loaded before the audio host stopped: " + string.Join(" → ", h.Log));
    }

    // ── (c) transport routing + exactly one signal feed ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task TransportVerbs_RouteToTheCurrentHostOnly()
    {
        using var h = new Harness();
        h.VideoIntent = true;
        await h.Controller.PlayAsync("spotify:playlist:p");
        int audioCallsAfterSwap = h.Audio.Calls.Count;
        int videoCallsAfterSwap = h.Video!.Calls.Count;

        await h.Controller.PauseAsync();
        await h.Controller.SeekAsync(5000, SeekMode.Accurate);
        await h.Controller.ResumeAsync();

        // Every verb landed on the video host…
        Assert.Contains("pause", h.Video.Calls);
        Assert.Contains("seek:5000", h.Video.Calls);
        Assert.Contains("play", h.Video.Calls);
        Assert.True(h.Video.Calls.Count > videoCallsAfterSwap);
        // …and none of them reached the (stopped) audio host.
        Assert.Equal(audioCallsAfterSwap, h.Audio.Calls.Count);
    }

    [Fact]
    public async Task ExactlyOneHostFeedsTheSignalChannel_AcrossEverySwap()
    {
        using var h = new Harness();
        Assert.Equal(1, h.Audio.Sig.SubscriberCount);
        Assert.Equal(0, h.Video!.Sig.SubscriberCount);

        h.VideoIntent = true;
        await h.Controller.PlayAsync("spotify:playlist:p");
        Assert.Equal(0, h.Audio.Sig.SubscriberCount);
        Assert.Equal(1, h.Video.Sig.SubscriberCount);

        h.VideoIntent = false;
        await h.Controller.RefreshCurrentMediaKindAsync();
        Assert.Equal(1, h.Audio.Sig.SubscriberCount);
        Assert.Equal(0, h.Video.Sig.SubscriberCount);
    }

    // ── (e) graceful fallback when there is no playable video ────────────────────────────────────────────────────────
    [Fact]
    public async Task NoResolvableVideoSource_FallsBackToAudio_RatherThanSilence()
    {
        using var h = new Harness();
        h.VideoIntent = true;
        h.VideoSourceAvailable = false;

        await h.Controller.PlayAsync("spotify:playlist:p");
        // The video branch resolves off-lock now (video-smooth-switching Milestone C): "no source" is discovered
        // AFTER PlayAsync's own lock hold has already released, so the audio fallback lands on a separate, later
        // continuation — await it explicitly rather than assuming it already ran.
        await h.AwaitVideoLoadAsync();

        Assert.Equal(1, h.LoadVideoCalls);
        Assert.Equal(PlayableKind.Audio, h.Controller.CurrentMediaKind);
        Assert.Contains("audio:load:spotify:track:a", h.Log);
        Assert.True(h.Audio.IsPlaying);
        Assert.False(h.Video!.IsPlaying);
        AssertNeverTwoHostsPlaying(h.Log);
    }

    [Fact]
    public async Task VideoIntentWithNoLoadHook_FallsBackToAudio()
    {
        using var h = new Harness(wireLoadHook: false);
        h.VideoIntent = true;

        await h.Controller.PlayAsync("spotify:playlist:p");

        Assert.Equal(PlayableKind.Audio, h.Controller.CurrentMediaKind);
        Assert.True(h.Audio.IsPlaying);
        // The host swap DID happen (the kind was Video at the switch) — what must never happen is the video host PLAYING
        // a source it does not have. It is only ever paused/stopped on the way back to audio.
        Assert.DoesNotContain("play", h.Video!.Calls);
        Assert.False(h.Video.IsPlaying);
    }

    [Fact]
    public async Task VideoIntentWithNoVideoHostInjected_PlaysAudio_AndNeverStrandsTheKind()
    {
        // Audio-only build shape: HostFor(Video) degrades to the audio host, so there is no host to swap to and the kind
        // must not be left claiming Video (Connect would then advertise track_player="video" over an audio stream).
        using var h = new Harness(injectVideoHost: false, wireLoadHook: false);
        h.VideoIntent = true;

        await h.Controller.PlayAsync("spotify:playlist:p");

        Assert.Equal(PlayableKind.Audio, h.Controller.CurrentMediaKind);
        Assert.Contains("audio:load:spotify:track:a", h.Log);
        Assert.True(h.Audio.IsPlaying);
    }

    // ── the audio path must be untouched ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task WiredButAudioIntent_KeepsTheAudioPathIdenticalToTheUnwiredBuild()
    {
        using var wired = new Harness();
        using var unwired = new Harness(wireHooks: false);
        await wired.Controller.PlayAsync("spotify:playlist:p");
        await unwired.Controller.PlayAsync("spotify:playlist:p");

        Assert.Equal(unwired.Log, wired.Log);
        Assert.Equal(0, wired.LoadVideoCalls);
    }

    [Fact]
    public async Task ARemoteDeviceIsActive_RefreshNeverReloadsLocally()
    {
        using var h = new Harness();
        await h.Controller.PlayAsync("spotify:playlist:p");
        h.Projection.OnCluster(new ClusterDelta("other-device", false, default, "spotify:playlist:ctx",
            false, true, false, 0, 0, 0, 0, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>()));
        int videoCalls = h.Video!.Calls.Count;

        h.VideoIntent = true;
        await h.Controller.RefreshCurrentMediaKindAsync();

        Assert.Equal(PlayableKind.Audio, h.Controller.CurrentMediaKind);
        Assert.Equal(videoCalls, h.Video.Calls.Count);
    }

    // ── position carry across the kind boundary (both directions) ─────────────────────────────────────────────────────

    /// <summary>audio→video carries the audio position INTO THE LOAD. It must not arrive as a follow-up Seek: at the
    /// swap the real host has not built its player yet (teardown→build→open is serialized through its load pump), so a
    /// seek at that instant is dropped — which is why this switch used to restart every video at 0.</summary>
    [Fact]
    public async Task AudioToVideo_CarriesPositionIntoTheLoad()
    {
        using var h = new Harness();
        await h.Controller.PlayAsync("spotify:playlist:ctx");
        h.Audio.PositionMs = 80_000;   // 1:20 into the song

        h.VideoIntent = true;
        await h.Controller.RefreshCurrentMediaKindAsync();

        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);
        Assert.Equal(80_000, h.LastVideoStartAtMs);
        AssertNeverTwoHostsPlaying(h.Log);
    }

    /// <summary>video→audio restores the audio session at the video's position. The audio host loads synchronously, so
    /// here the carry IS observable as a seek on the incoming host.</summary>
    [Fact]
    public async Task VideoToAudio_CarriesPositionBack()
    {
        using var h = new Harness();
        h.VideoIntent = true;
        await h.Controller.PlayAsync("spotify:playlist:ctx");
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);
        h.Video!.PositionMs = 45_000;   // 0:45 into the video edit

        h.VideoIntent = false;
        await h.Controller.RefreshCurrentMediaKindAsync();

        Assert.Equal(PlayableKind.Audio, h.Controller.CurrentMediaKind);
        Assert.Contains("audio:seek:45000", h.Log);
        AssertNeverTwoHostsPlaying(h.Log);
    }

    /// <summary>A forced same-kind video reload (the user attached/replaced/removed a local override mid-song) keeps the
    /// place too — the reload is not a restart.</summary>
    [Fact]
    public async Task ForcedSameKindVideoReload_CarriesPosition()
    {
        using var h = new Harness();
        h.VideoIntent = true;
        await h.Controller.PlayAsync("spotify:playlist:ctx");
        h.Video!.PositionMs = 30_000;
        h.LastVideoStartAtMs = -1;

        await h.Controller.RefreshCurrentMediaKindAsync(forceReloadIfVideo: true);

        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);
        Assert.Equal(30_000, h.LastVideoStartAtMs);
    }

    /// <summary>Heading to AUDIO the catalog length is known, so an overrunning carry is clamped before the load rather
    /// than seeking past the end of the song.</summary>
    [Fact]
    public async Task VideoToAudio_ClampsCarryToCatalogDuration()
    {
        using var h = new Harness();
        h.VideoIntent = true;
        await h.Controller.PlayAsync("spotify:playlist:ctx");
        long catalogMs = h.Projection.DurationMs;
        Assert.True(catalogMs > 0, "the stub track must carry a catalog duration for this test to mean anything");
        h.Video!.PositionMs = catalogMs + 60_000;   // the video edit ran long past the song

        h.VideoIntent = false;
        await h.Controller.RefreshCurrentMediaKindAsync();

        Assert.Contains("audio:seek:" + catalogMs, h.Log);
    }

    // ── (f) NO AUDIO FORM ⇒ NO CHOICE — the video→audio regression ────────────────────────────────────────────────────
    const string ModuleContext = "wavee:module:wavee.youtube:tRsQsTMvPNg";
    const string ModulePlayable = "wavee:module:wavee.youtube:track:tRsQsTMvPNg";

    static bool IsAudioLoad(string call) =>
        call.StartsWith("audio:load:", StringComparison.Ordinal) || call.StartsWith("audio:faststart:", StringComparison.Ordinal);

    /// <summary>The BUG-2 regression. A module playable resolved as <c>MediaForm.Video</c> (a YouTube/Twitch stream) has no
    /// second, audio-only body: the audio host can only ever answer "Restricted: video playable; use the video host". Three
    /// re-entries into the load path used to re-host a playing video onto audio and stop it with an error toast —
    /// <list type="number">
    /// <item>the app's OWN masked Connect echo adopted as a track change, which cancelled the one-play video scope so
    /// <c>ShouldPlayAsVideo</c> flipped to false (the surface went down silently, <c>refreshKind: false</c>),</item>
    /// <item>the error toast's <b>Retry</b> (<c>LiveSessionHost</c> → <see cref="PlaybackController.RetryCurrentAsync"/>),</item>
    /// <item>any availability refresh (<c>RecomputeHasVideo</c> → <c>RequestMediaKindRefresh</c>).</item>
    /// </list>
    /// <c>IsVideoOnlyPlayable</c> pins the kind to Video BEFORE every audio-first rule, so all three stay on the video host
    /// and the audio host is never handed a body it cannot decode. The context is a module link, so the Spotify-only
    /// autoplay station must not be asked for one either (that request is the observed <c>autoplay endpoint failed (400)</c>).</summary>
    [Fact]
    public async Task VideoOnlyPlayable_StaysOnTheVideoHost_AcrossTheSurfaceTeardown_TheRetry_AndARefresh()
    {
        using var h = new Harness(contextTracks: [ModulePlayable], videoOnly: true);
        h.VideoIntent = true;

        await h.Controller.PlayAsync(ModuleContext);
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);
        Assert.True(h.Video!.IsPlaying);
        Assert.Equal(1, h.LoadVideoCalls);

        // 1 — the cluster echo took the one-play video scope down: the app no longer says "play this as video".
        h.VideoIntent = false;
        await h.Controller.RefreshCurrentMediaKindAsync();
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);

        // 2 — the playback-error toast's Retry re-enters LoadAndPlayCurrentAsync with the intent still false.
        await h.Controller.RetryCurrentAsync();
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);

        // 3 — an availability refresh on the same (intent-less) state.
        await h.Controller.RefreshCurrentMediaKindAsync();
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);

        Assert.DoesNotContain(h.Log, IsAudioLoad);
        Assert.DoesNotContain("play", h.Audio.Calls);      // the audio host was only ever stopped by the initial swap
        Assert.False(h.Audio.IsPlaying);
        Assert.True(h.Video.IsPlaying);
        Assert.True(h.LoadVideoCalls >= 2, "the Retry must have re-run the VIDEO load: " + string.Join(" → ", h.Log));
        AssertNeverTwoHostsPlaying(h.Log);
        Assert.Equal(0, h.Contexts.AutoplayCalls);         // a module context has no Spotify autoplay station
    }

    /// <summary>The same sequence WITHOUT the hook is the bug itself, kept as the control: a choosable playable follows the
    /// intent down to audio. This is what proves the test above is testing <c>IsVideoOnlyPlayable</c> and not the harness.</summary>
    [Fact]
    public async Task WithoutTheVideoOnlyHook_TheSameSequenceFallsToAudio()
    {
        using var h = new Harness(contextTracks: [ModulePlayable]);
        h.VideoIntent = true;

        await h.Controller.PlayAsync(ModuleContext);
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);

        h.VideoIntent = false;
        await h.Controller.RefreshCurrentMediaKindAsync();

        Assert.Equal(PlayableKind.Audio, h.Controller.CurrentMediaKind);
        Assert.Contains(h.Log, IsAudioLoad);
        Assert.Equal(0, h.Contexts.AutoplayCalls);         // …and the non-Spotify context is still never autoplayed
    }

    /// <summary>A one-item SPOTIFY context does reach the autoplay station at the same point in the load — so the assertion
    /// above is a property of the CONTEXT URI, not of "this harness never prefetches".</summary>
    [Fact]
    public async Task AOneItemSpotifyContext_StillAsksForItsAutoplayStation()
    {
        using var h = new Harness(contextTracks: ["spotify:track:a"]);

        await h.Controller.PlayAsync("spotify:playlist:p");

        Assert.True(h.Contexts.AutoplayCalls > 0, "a Spotify context at the end of its window must prefetch autoplay");
    }
}
