using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// P2 of the universal video overrides — the DECISION + PLAYBACK half. Everything load-bearing about an override at play
// time is engine-free by construction, so it is pinned here against production code rather than against a mock of it:
//   • the tier-1 precedence walk (attachment wins → missing file falls through → a session-quarantined file is skipped),
//   • the has-video latch, whose one job on a REMOVAL is to stop suppressing the true→false it normally suppresses,
//   • the controller's forced same-kind reload (an override swap changes the SOURCE, not the KIND),
//   • the open-failure recovery hook, its ordering, and its one-attempt-per-playable loop guard,
//   • the media-authoritative duration surviving a queue republish,
// plus the standing rule that every one of these paths is INERT when its hook/service is unwired.
public class VideoOverrideTests
{
    static Track T(string uri) => new(uri[(uri.LastIndexOf(':') + 1)..], uri, uri,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);

    static VideoOverrideService Svc(params (string Uri, string Path)[] attached)
    {
        var store = new InMemoryStore();
        var svc = new VideoOverrideService(store);
        svc.FileExists = _ => true;                       // the default probe for these tests; overridden per case
        foreach (var (uri, path) in attached) svc.Attach(uri, path);
        return svc;
    }

    // ── tier-1 precedence walk ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Decide_NoAttachment_FallsThroughToTheSourceTier()
    {
        var svc = Svc();
        Assert.Equal(VideoOverrideTier.None, svc.Decide("spotify:track:a").Tier);
        Assert.False(svc.Decide("spotify:track:a").Wins);
        Assert.False(svc.Has("spotify:track:a"));
        Assert.False(svc.Has(null));
    }

    [Fact]
    public void Decide_AttachedAndPresent_Wins_OverAnySourceVideo()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\a.mp4"));

        var d = svc.Decide("spotify:track:a");

        Assert.Equal(VideoOverrideTier.UseOverride, d.Tier);
        Assert.True(d.Wins);
        Assert.True(svc.Has("spotify:track:a"));
        Assert.StartsWith("local:video:", d.Override.SourceKey, StringComparison.Ordinal);
        Assert.Equal(VideoOverrideService.IdFor(VideoOverrideService.NormalizePath(@"C:\v\a.mp4")), d.Override.Id);
    }

    [Fact]
    public void Decide_MissingFile_IsBroken_KeepsTheLink_AndFallsThrough()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\gone.mp4"));
        svc.FileExists = _ => false;

        var d = svc.Decide("spotify:track:a");

        Assert.Equal(VideoOverrideTier.Broken, d.Tier);
        Assert.False(d.Wins);
        Assert.True(svc.Has("spotify:track:a"));          // the record survives — it is repairable, not garbage
        Assert.Single(svc.All());
    }

    [Fact]
    public void Decide_ProbeThatThrows_IsTreatedAsMissing_NotAsAFailure()
    {
        var svc = Svc(("spotify:track:a", @"\\offline-nas\v\a.mp4"));
        svc.FileExists = _ => throw new IOException("the network path is unreachable");

        Assert.Equal(VideoOverrideTier.Broken, svc.Decide("spotify:track:a").Tier);
    }

    [Fact]
    public void Decide_QuarantinedPair_IsSkipped_EvenThoughTheFileExists()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\a.mp4"));
        var key = svc.Decide("spotify:track:a").Override.SourceKey;

        svc.Quarantine("spotify:track:a", key);

        Assert.True(svc.IsQuarantined("spotify:track:a", key));
        Assert.Equal(VideoOverrideTier.Quarantined, svc.Decide("spotify:track:a").Tier);
        // Scoped to the exact PAIR: the same file attached to a different playable is unaffected.
        svc.Attach("spotify:episode:e", @"C:\v\a.mp4");
        Assert.Equal(VideoOverrideTier.UseOverride, svc.Decide("spotify:episode:e").Tier);
    }

    [Fact]
    public void Replacing_TheAttachment_ReArmsIt_AndDropsTheQuarantine()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\a.mp4"));
        svc.Quarantine("spotify:track:a", svc.Decide("spotify:track:a").Override.SourceKey);
        Assert.Equal(VideoOverrideTier.Quarantined, svc.Decide("spotify:track:a").Tier);

        svc.Attach("spotify:track:a", @"C:\v\a.mp4");      // re-pick the same path after fixing it

        Assert.Equal(VideoOverrideTier.UseOverride, svc.Decide("spotify:track:a").Tier);
        Assert.Single(svc.All());                          // uri is the PK — a duplicate attach IS the replace
    }

    [Fact]
    public void Attach_NormalizesAndCaseFoldsTheIdentity_SoTheRemountKeyIsStable()
    {
        Assert.Equal(VideoOverrideService.IdFor(@"c:\v\a.mp4"), VideoOverrideService.IdFor(@"C:\V\A.MP4"));
        Assert.NotEqual(VideoOverrideService.IdFor(@"c:\v\a.mp4"), VideoOverrideService.IdFor(@"c:\v\b.mp4"));
        Assert.Equal(16, VideoOverrideService.IdFor(@"c:\v\a.mp4").Length);
    }

    [Fact]
    public void AttachAndReplace_NotifyWithTheCorrectMutationKind()
    {
        var store = new InMemoryStore();
        var svc = new VideoOverrideService(store) { FileExists = _ => true };
        var changed = new List<OverrideMutationKind>();
        svc.OnChanged = (_, kind) => changed.Add(kind);

        svc.Attach("spotify:track:a", @"C:\v\a.mp4");
        svc.Attach("spotify:track:a", @"C:\v\b.mp4");

        Assert.Equal(new[] { OverrideMutationKind.Attach, OverrideMutationKind.Replace }, changed.ToArray());
    }

    [Fact]
    public void Remove_IsANoOpWhenNothingIsAttached_AndSilencesTheChangeNotification()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\a.mp4"));
        var changed = new List<(string Uri, OverrideMutationKind Kind)>();
        svc.OnChanged = (uri, kind) => changed.Add((uri, kind));

        Assert.True(svc.Remove("spotify:track:a"));
        Assert.False(svc.Remove("spotify:track:a"));

        Assert.Equal(new[] { ("spotify:track:a", OverrideMutationKind.Remove) }, changed.ToArray());
        Assert.False(svc.Has("spotify:track:a"));
        Assert.Equal(VideoOverrideTier.None, svc.Decide("spotify:track:a").Tier);
    }

    [Fact]
    public void NoteBroken_WarnsAtMostOncePerSessionPerAttachment()
    {
        var svc = Svc(("spotify:track:a", @"C:\v\gone.mp4"));
        svc.FileExists = _ => false;
        int warned = 0;
        svc.OnBrokenLink = _ => warned++;

        var d = svc.Decide("spotify:track:a");
        svc.NoteBroken("spotify:track:a", d.Override);
        svc.NoteBroken("spotify:track:a", d.Override);
        svc.NoteBroken("spotify:track:a", d.Override);

        Assert.Equal(1, warned);   // a broken link is a quiet fallback, not a repeated interruption
    }

    [Fact]
    public void Attach_PersistsThroughTheStore_SoAFreshServiceSeesTheSameRoster()
    {
        var store = new InMemoryStore();
        new VideoOverrideService(store).Attach("spotify:track:a", @"C:\v\a.mp4");

        var reloaded = new VideoOverrideService(store);

        Assert.True(reloaded.Has("spotify:track:a"));
        Assert.Equal(1, reloaded.Count);
        Assert.Equal(@"C:\v\a.mp4", store.GetVideoOverride("spotify:track:a")!.Value.Path);
    }

    // ── the has-video latch (what ShouldPlayAsVideo / RecomputeHasVideo fold) ─────────────────────────────────────────

    [Fact]
    public void Latch_SuppressesATransientDowngrade_ButNotARealRemoval()
    {
        string? latched = null;

        Assert.True(HasVideoLatch.Apply(true, "spotify:track:a", ref latched));      // the association lands
        Assert.Equal("spotify:track:a", latched);
        Assert.True(HasVideoLatch.Apply(false, "spotify:track:a", ref latched));     // read glitch → suppressed
        Assert.True(HasVideoLatch.Apply(false, null, ref latched));                  // mid-push null → suppressed

        HasVideoLatch.ClearFor("spotify:track:a", ref latched);                      // the user detached the override
        Assert.Null(latched);
        Assert.False(HasVideoLatch.Apply(false, "spotify:track:a", ref latched));    // …and NOW the true→false commits
    }

    [Fact]
    public void Latch_ClearFor_OnlyEndsTheLatchForItsOwnPlayable()
    {
        string? latched = null;
        HasVideoLatch.Apply(true, "spotify:track:a", ref latched);

        HasVideoLatch.ClearFor("spotify:track:b", ref latched);

        Assert.Equal("spotify:track:a", latched);
        Assert.True(HasVideoLatch.Apply(false, "spotify:track:a", ref latched));
    }

    // ── the dead-video latch (what unmounts a surface the backend has stopped feeding) ───────────────────────────────
    // A surface mounts on AVAILABILITY ("does this uri have a video"), and availability stays true when the video simply
    // does not happen — the source resolved to nothing, or the session failed to open. The surface then presents an
    // indeterminate "Loading" poster with no timeout and no error state, forever, over audio that is already playing.
    // This latch is the missing fact channel; it is scoped to ONE playable and never touches the user's intent.

    [Fact]
    public void DeadVideoLatch_ForcesHasVideoFalse_ForThatPlayableOnly()
    {
        string? dead = null;
        Assert.True(VideoMediaLatch.MarkDead("spotify:track:a", ref dead));

        Assert.False(VideoMediaLatch.Apply(true, "spotify:track:a", dead));   // the proven fact wins
        Assert.True(VideoMediaLatch.Apply(true, "spotify:track:b", dead));    // …and is scoped to its own playable
        Assert.False(VideoMediaLatch.Apply(false, "spotify:track:b", dead));  // never invents availability
    }

    [Fact]
    public void DeadVideoLatch_BeatsTheGlitchSuppressionLatch()
    {
        // HasVideoLatch exists to absorb a transient true→false read; it must NOT resurrect a video the backend has
        // proven is not playing, or the surface comes straight back and spins again.
        string? latched = null, dead = null;
        Assert.True(HasVideoLatch.Apply(true, "spotify:track:a", ref latched));
        VideoMediaLatch.MarkDead("spotify:track:a", ref dead);

        bool has = HasVideoLatch.Apply(false, "spotify:track:a", ref latched);   // glitch latch says "still true"
        Assert.True(has);
        Assert.False(VideoMediaLatch.Apply(has, "spotify:track:a", dead));       // …the fact overrules it
    }

    [Fact]
    public void DeadVideoLatch_IsNotSticky_ATrackChangeOrAnAttachClearsIt()
    {
        string? dead = null;
        VideoMediaLatch.MarkDead("spotify:track:a", ref dead);

        VideoMediaLatch.ClearFor("spotify:track:b", ref dead);                   // a DIFFERENT playable: not ours
        Assert.True(VideoMediaLatch.IsDead("spotify:track:a", dead));

        VideoMediaLatch.ClearFor("spotify:track:a", ref dead);                   // the user attached a video to it
        Assert.False(VideoMediaLatch.IsDead("spotify:track:a", dead));

        VideoMediaLatch.MarkDead("spotify:track:a", ref dead);
        VideoMediaLatch.ClearFor(null, ref dead);                                // a real track change wipes it
        Assert.Null(dead);
    }

    [Fact]
    public void DeadVideoLatch_MarkDeadReportsOnlyRealEdges()
    {
        string? dead = null;
        Assert.True(VideoMediaLatch.MarkDead("spotify:track:a", ref dead));
        Assert.False(VideoMediaLatch.MarkDead("spotify:track:a", ref dead));   // idempotent → no republish storm
        Assert.False(VideoMediaLatch.MarkDead("", ref dead));
        Assert.False(VideoMediaLatch.MarkDead(null, ref dead));
    }

    // ── the controller: forced same-kind reload ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ForcedRefresh_ReloadsAVideoPlayable_WhenTheKindDidNotChange()
    {
        using var h = new Harness { VideoIntent = true };
        await h.Controller.PlayAsync("spotify:playlist:p");
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);
        int loadsBefore = h.LoadVideoCalls;

        await h.Controller.RefreshCurrentMediaKindAsync();          // unforced: same kind → the proven early return
        Assert.Equal(loadsBefore, h.LoadVideoCalls);

        await h.Controller.RefreshCurrentMediaKindAsync(forceReloadIfVideo: true);
        Assert.Equal(loadsBefore + 1, h.LoadVideoCalls);            // the source really was re-resolved
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);
    }

    [Fact]
    public async Task ForcedRefresh_NeverReloadsAnAudioPlayable()
    {
        using var h = new Harness();
        await h.Controller.PlayAsync("spotify:playlist:p");
        int before = h.Log.Count;

        await h.Controller.RefreshCurrentMediaKindAsync(forceReloadIfVideo: true);

        Assert.Equal(before, h.Log.Count);   // forcing is a VIDEO-source concern only
    }

    // ── the controller: open-failure recovery ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VideoError_WithNoRecoveryHook_ReportsError_WithoutSilentAudioFallback()
    {
        // Local (non-Connect) video faults without a recovery hook surface as a typed playback error. Automatic
        // video→audio fallback is reserved for Connect-originated sessions (see FallbackVideoToAudioAsync).
        using var h = new Harness { VideoIntent = true };
        await h.Controller.PlayAsync("spotify:playlist:p");

        h.Video!.Sig.Emit(AudioHostSignal.Fault(0, AudioKeyFailureReason.None, "decode failed"));
        await WaitUntilAsync(() => h.Errors.Count > 0);

        Assert.Single(h.Errors);
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);
    }

    [Fact]
    public async Task VideoError_RecoveryDeclines_ReportsError_AfterAskingOnce()
    {
        using var h = new Harness { VideoIntent = true };
        await h.Controller.PlayAsync("spotify:playlist:p");
        h.Controller.TryRecoverVideoAsync = (t, _) => { h.RecoveryAsks.Add(t.Uri); return Task.FromResult(false); };

        h.Video!.Sig.Emit(AudioHostSignal.Fault(0, AudioKeyFailureReason.None, "decode failed"));
        await WaitUntilAsync(() => h.Errors.Count > 0);

        Assert.Equal(new[] { "spotify:track:a" }, h.RecoveryAsks.ToArray());
        Assert.Single(h.Errors);
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);
    }

    [Fact]
    public async Task VideoError_RecoverySucceeds_ReloadsInsteadOfReportingAnError()
    {
        using var h = new Harness { VideoIntent = true };
        await h.Controller.PlayAsync("spotify:playlist:p");
        int loadsBefore = h.LoadVideoCalls;
        h.Controller.TryRecoverVideoAsync = (t, _) => { h.RecoveryAsks.Add(t.Uri); return Task.FromResult(true); };

        h.Video!.Sig.Emit(AudioHostSignal.Fault(0, AudioKeyFailureReason.None, "decode failed"));
        await WaitUntilAsync(() => h.LoadVideoCalls > loadsBefore);

        Assert.Empty(h.Errors);                       // the user sees a fallback, not the player-bar error state
        Assert.Single(h.RecoveryAsks);
    }

    [Fact]
    public async Task VideoError_RecoveryIsAttemptedAtMostOncePerPlayable()
    {
        using var h = new Harness { VideoIntent = true };
        await h.Controller.PlayAsync("spotify:playlist:p");
        h.Controller.TryRecoverVideoAsync = (t, _) => { h.RecoveryAsks.Add(t.Uri); return Task.FromResult(true); };

        h.Video!.Sig.Emit(AudioHostSignal.Fault(0, AudioKeyFailureReason.None, "decode failed"));
        await WaitUntilAsync(() => h.RecoveryAsks.Count == 1);
        await Task.Delay(60);                         // let the recovery reload settle (it keeps the same playable)

        h.Video.Sig.Emit(AudioHostSignal.Fault(0, AudioKeyFailureReason.None, "decode failed again"));
        await WaitUntilAsync(() => h.Errors.Count > 0);

        Assert.Single(h.RecoveryAsks);                // one video recovery; a second fault reports rather than looping
        Assert.Single(h.Errors);
    }

    [Fact]
    public async Task AudioError_NeverConsultsTheVideoRecoveryHook()
    {
        using var h = new Harness();
        await h.Controller.PlayAsync("spotify:playlist:p");
        h.Controller.TryRecoverVideoAsync = (t, _) => { h.RecoveryAsks.Add(t.Uri); return Task.FromResult(true); };

        h.Audio.Sig.Emit(AudioHostSignal.Fault(0, AudioKeyFailureReason.Network, "cdn 503"));
        await WaitUntilAsync(() => h.Errors.Count > 0);

        Assert.Empty(h.RecoveryAsks);                 // the hook is scoped to Video — audio is byte-identical to today
        Assert.Equal(AudioKeyFailureReason.Network, h.Errors[0].Reason);
    }

    // ── the projection: MP4-authoritative duration ───────────────────────────────────────────────────────────────────

    [Fact]
    public void DurationOverride_OutranksTheCatalogLength_AndSurvivesAQueueRepublish()
    {
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var track = T("spotify:track:a") with { DurationMs = 200_000 };
        p.OnEvent(new PlaybackEvent(EvKind.Started, track, 0));
        Assert.Equal(200_000, p.DurationMs);

        p.SetDurationOverride("spotify:track:a", 247_500);
        Assert.Equal(247_500, p.DurationMs);

        // A queue mutation republishes the whole snapshot, carrying the CATALOG duration. Without the re-apply at this
        // write site the seek bar would silently revert to the wrong length mid-video.
        p.ApplyLocalSnapshot(Snap(track));
        Assert.Equal(247_500, p.DurationMs);

        p.OnEvent(new PlaybackEvent(EvKind.Seeked, track, 1000));
        Assert.Equal(247_500, p.DurationMs);
    }

    [Fact]
    public void DurationOverride_IsDroppedWhenTheTrackChanges()
    {
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var a = T("spotify:track:a") with { DurationMs = 200_000 };
        var b = T("spotify:track:b") with { DurationMs = 180_000 };
        p.OnEvent(new PlaybackEvent(EvKind.Started, a, 0));
        p.SetDurationOverride("spotify:track:a", 247_500);

        p.OnEvent(new PlaybackEvent(EvKind.TrackChanged, b, 0));

        Assert.Equal(180_000, p.DurationMs);          // the next song must never inherit the video's length
        Assert.Equal(0, p.DurationOverrideMs);
    }

    [Fact]
    public void DurationOverride_ForAnotherPlayable_NeverAppliesToTheCurrentOne()
    {
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var a = T("spotify:track:a") with { DurationMs = 200_000 };
        p.OnEvent(new PlaybackEvent(EvKind.Started, a, 0));

        p.SetDurationOverride("spotify:track:zzz", 999_000);

        Assert.Equal(200_000, p.DurationMs);
        Assert.Equal(0, p.DurationOverrideMs);
    }

    [Fact]
    public async Task DurationOverride_ClearedWhenLeavingVideoKind()
    {
        using var h = new Harness { VideoIntent = true };
        await h.Controller.PlayAsync("spotify:playlist:p");
        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);
        h.Projection.SetDurationOverride("spotify:track:a", 255_095);
        Assert.Equal(255_095, h.Projection.DurationOverrideMs);

        h.VideoIntent = false;
        await h.Controller.RefreshCurrentMediaKindAsync();

        Assert.Equal(PlayableKind.Audio, h.Controller.CurrentMediaKind);
        Assert.Equal(0, h.Projection.DurationOverrideMs);
        Assert.NotEqual(255_095, h.Projection.DurationMs);   // must not keep the mp4 length on audio
        Assert.True(h.Projection.DurationMs > 0);
    }

    [Fact]
    public async Task DurationOverride_ReAdoptedWhenVideoReloads()
    {
        using var h = new Harness { VideoIntent = true };
        await h.Controller.PlayAsync("spotify:playlist:p");
        h.Projection.SetDurationOverride("spotify:track:a", 255_095);
        h.VideoIntent = false;
        await h.Controller.RefreshCurrentMediaKindAsync();
        Assert.Equal(0, h.Projection.DurationOverrideMs);

        h.VideoIntent = true;
        await h.Controller.RefreshCurrentMediaKindAsync();
        h.Projection.SetDurationOverride("spotify:track:a", 255_095);

        Assert.Equal(PlayableKind.Video, h.Controller.CurrentMediaKind);
        Assert.Equal(255_095, h.Projection.DurationOverrideMs);
    }

    [Fact]
    public void DurationOverride_NonPositiveOrEmpty_ClearsIt()
    {
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var a = T("spotify:track:a") with { DurationMs = 200_000 };
        p.OnEvent(new PlaybackEvent(EvKind.Started, a, 0));
        p.SetDurationOverride("spotify:track:a", 247_500);

        p.SetDurationOverride("spotify:track:a", 0);

        Assert.Equal(0, p.DurationOverrideMs);
        p.ApplyLocalSnapshot(Snap(a));
        Assert.Equal(200_000, p.DurationMs);
    }

    // ── support ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static QueueSnapshot Snap(Track t) => new(
        Revision: 1, ContextUri: "spotify:playlist:p", AutoplayContextUri: null,
        Current: new QueueEntry(QueueItemId.None, "cur", t, QueueBucket.NowPlaying, QueueProvider.Context, false, "u-cur"),
        History: ImmutableArray<QueueEntry>.Empty,
        UserQueue: ImmutableArray<QueueEntry>.Empty,
        Upcoming: ImmutableArray<QueueEntry>.Empty,
        Shuffle: false, Repeat: RepeatMode.Off, ClusterQueueRevision: "");

    static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    sealed class TestSignals : IObservable<AudioHostSignal>
    {
        readonly List<IObserver<AudioHostSignal>> _subs = new();
        public IDisposable Subscribe(IObserver<AudioHostSignal> observer) { _subs.Add(observer); return new Unsub(this, observer); }
        public void Emit(AudioHostSignal s) { foreach (var o in _subs.ToArray()) o.OnNext(s); }
        sealed class Unsub(TestSignals owner, IObserver<AudioHostSignal> observer) : IDisposable
        {
            public void Dispose() => owner._subs.Remove(observer);
        }
    }

    sealed class FakeAudioHost(List<string> shared) : IAudioHost
    {
        public readonly TestSignals Sig = new();
        void Note(string call) => shared.Add("audio:" + call);
        public IObservable<AudioHostSignal> Signals => Sig;
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsBuffering => false;
        public bool ClockValid => true;
        public void Load(in AudioStreamHandle s) => Note("load:" + s.TrackUri);
        public void LoadFastStart(in AudioFastStart s) => Note("faststart:" + s.TrackUri);
        public void SupplyBody(in AudioStreamHandle s) { }
        public void Play() { IsPlaying = true; Note("play"); }
        public void Pause() { IsPlaying = false; Note("pause"); }
        public void Stop() { IsPlaying = false; Note("stop"); }
        public void Seek(long ms, SeekMode mode) => Note("seek:" + ms);
        public void SetVolume(double v) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FakeVideoHost(List<string> shared) : IMediaHost
    {
        public readonly TestSignals Sig = new();
        void Note(string call) => shared.Add("video:" + call);
        public IObservable<AudioHostSignal> Signals => Sig;
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool ClockValid => true;
        public void Play() { IsPlaying = true; Note("play"); }
        public void Pause() { IsPlaying = false; Note("pause"); }
        public void Stop() { IsPlaying = false; Note("stop"); }
        public void Seek(long ms, SeekMode mode) => Note("seek:" + ms);
        public void SetVolume(double v) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Harness : IDisposable
    {
        public readonly List<string> Log = new();
        public readonly FakeAudioHost Audio;
        public readonly FakeVideoHost? Video;
        public readonly NowPlayingProjection Projection;
        public readonly PlaybackController Controller;
        public readonly List<PlaybackErrorInfo> Errors = new();
        public readonly List<string> RecoveryAsks = new();

        public bool VideoIntent;
        public int LoadVideoCalls;

        public Harness()
        {
            Audio = new FakeAudioHost(Log);
            Video = new FakeVideoHost(Log);
            Projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
            Controller = new PlaybackController(Audio, new StubTrackResolver(), Projection,
                new FakeContextResolver("spotify:track:a", "spotify:track:b"), "us", videoHost: Video);
            Controller.ShouldPlayAsVideo = _ => VideoIntent;
            Controller.LoadCurrentVideoAsync = (_, _, _) => { LoadVideoCalls++; return Task.FromResult(true); };
            Controller.OnPlaybackError = e => { lock (Errors) Errors.Add(e); };
        }

        public void Dispose() => Controller.Dispose();
    }
}
