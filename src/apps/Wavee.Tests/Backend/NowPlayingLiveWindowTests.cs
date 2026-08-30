using System;
using Wavee.Backend;
using Wavee.Backend.Modules;
using Wavee.Core;
using Wavee.Sdk;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The ENGINE-AUTHORITATIVE half of live-ness: <see cref="NowPlayingProjection.SetLiveWindow"/>. It is scoped, cleared
/// and folded exactly like the duration override, but it carries two things a bool cannot — the DVR window that decides
/// whether the seek bar is a rail or a line, and the live edge the "GO LIVE" button jumps to.
/// <para>The union with <see cref="NowPlayingProjection.SetLiveOverride"/> is the point of these tests: a module states
/// live-ness at resolve, the engine states the window once metadata loads, and neither answer may erase the other.</para>
/// </summary>
public class NowPlayingLiveWindowTests
{
    const string StreamUri = "wavee:module:wavee.youtube:aGVsbG8";
    const string SongUri = "spotify:track:abc";

    static NowPlayingProjection New() => new("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());

    static Track TrackFor(string uri, long durationMs = 0) => new(
        Id: uri, Uri: uri, Title: "t", Artists: Array.Empty<ArtistRef>(), Album: new AlbumRef("", "", ""),
        DurationMs: durationMs, IsExplicit: false, Image: null);

    static void Start(NowPlayingProjection p, string uri, long durationMs = 0)
        => p.OnEvent(new PlaybackEvent(EvKind.Started, TrackFor(uri, durationMs), 0));

    /// <summary>A four-hour DVR window — a real YouTube live stream's shape.</summary>
    static LiveWindow Dvr(long positionMs = 25_800_000, bool atEdge = true) => new(
        IsLive: true, SeekableStartMs: 11_400_000, SeekableEndMs: 25_800_000,
        LiveEdgeMs: 25_800_000, PositionMs: positionMs, IsAtLiveEdge: atEdge);

    /// <summary>A live source with nothing to rewind — an ICY station, a low-latency channel.</summary>
    static LiveWindow NoRewind() => new(
        IsLive: true, SeekableStartMs: 0, SeekableEndMs: 0, LiveEdgeMs: 0, PositionMs: 0, IsAtLiveEdge: true);

    [Fact]
    public void Default_IsNotLive_AndPublishesNoWindow()
    {
        using var p = New();
        Start(p, SongUri, 200_000);

        Assert.False(p.IsLive);
        Assert.Equal(LiveWindow.None, p.Live);
        Assert.True(p.CanSeek);
    }

    [Fact]
    public void Window_StatesLiveness_OnItsOwn()
    {
        // The ENGINE alone is enough. No module said anything; MF's timeline did, and the LIVE chip must follow it.
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveWindow(StreamUri, Dvr());

        Assert.True(p.IsLive);
        Assert.Equal(Dvr(), p.Live);
    }

    [Fact]
    public void ADvrWindow_ReArmsSeeking()
    {
        // The named regression: "live" used to mean "no scrubbing, ever". A four-hour rewindable window is exactly the
        // case where that is wrong — there IS a past, and the rail scrubs inside it.
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveWindow(StreamUri, Dvr());

        Assert.True(p.Live.HasWindow);
        Assert.True(p.CanSeek);
    }

    [Fact]
    public void ALiveSourceWithNoWindow_StillRefusesSeeks()
    {
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveWindow(StreamUri, NoRewind());

        Assert.True(p.IsLive);
        Assert.False(p.Live.HasWindow);
        Assert.False(p.CanSeek);
    }

    [Fact]
    public void Window_IsScopedToItsOwnPlayable()
    {
        using var p = New();
        Start(p, SongUri, 200_000);
        p.SetLiveWindow(StreamUri, Dvr());

        Assert.False(p.IsLive);
        Assert.Equal(LiveWindow.None, p.Live);
        Assert.True(p.CanSeek);
    }

    [Fact]
    public void Window_IsDroppedAtTheNextTrackChange()
    {
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveWindow(StreamUri, Dvr());
        Assert.True(p.IsLive);

        Start(p, SongUri, 200_000);

        Assert.False(p.IsLive);
        Assert.Equal(LiveWindow.None, p.Live);
        Assert.True(p.CanSeek);
    }

    [Fact]
    public void Window_ClearsExplicitly()
    {
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveWindow(StreamUri, Dvr());
        p.SetLiveWindow(StreamUri, LiveWindow.None);   // what the video host retracts on teardown

        Assert.False(p.IsLive);
        Assert.Equal(LiveWindow.None, p.Live);
        Assert.True(p.CanSeek);
    }

    [Fact]
    public void ANullUri_ClearsTheWindow()
    {
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveWindow(StreamUri, Dvr());
        p.SetLiveWindow(null, Dvr());

        Assert.False(p.IsLive);
        Assert.Equal(LiveWindow.None, p.Live);
    }

    // ── the union with the source-level override ────────────────────────────────────────────────────────────────────

    [Fact]
    public void SourceOverrideAlone_IsStillLive_ButHasNoWindowToScrub()
    {
        // The module said "live" at resolve; the engine has not published a timeline yet. The chip lights immediately
        // and the rail stays disarmed until a window arrives — which is the honest ordering, not a race.
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveOverride(StreamUri, true);

        Assert.True(p.IsLive);
        Assert.False(p.Live.HasWindow);
        Assert.False(p.CanSeek);
    }

    [Fact]
    public void ASourceOnlyLiveSource_ReportsALiveWindowWithNothingToRewind()
    {
        // Internet radio. Live is the RICHER value and a surface may branch on it alone, so it must agree with IsLive —
        // otherwise the LIVE chip silently disappears for every audio-only broadcast. There is no broadcast clock to
        // report, so the positions are 0 and the playhead is at the edge by definition.
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveOverride(StreamUri, true);

        Assert.True(p.Live.IsLive);
        Assert.Equal(p.IsLive, p.Live.IsLive);
        Assert.False(p.Live.HasWindow);
        Assert.True(p.Live.IsAtLiveEdge);
        Assert.Equal(0, p.Live.WindowMs);
    }

    [Fact]
    public void TheWindowArrivingLater_UpgradesTheSameBroadcastToADvrRail()
    {
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveOverride(StreamUri, true);
        Assert.False(p.CanSeek);

        p.SetLiveWindow(StreamUri, Dvr());

        Assert.True(p.IsLive);
        Assert.True(p.CanSeek);
    }

    [Fact]
    public void ClearingTheWindow_DoesNotEraseTheSourcesOwnAnswer()
    {
        // A variant switch can retract the timeline for a moment. The module's isLive did not stop being true, so the
        // LIVE chip must not blink off — only the rail folds back to a line.
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveOverride(StreamUri, true);
        p.SetLiveWindow(StreamUri, Dvr());

        p.SetLiveWindow(StreamUri, LiveWindow.None);

        Assert.True(p.IsLive);
        Assert.False(p.CanSeek);
    }

    [Fact]
    public void ClearingTheSourceOverride_DoesNotEraseTheEnginesWindow()
    {
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveOverride(StreamUri, true);
        p.SetLiveWindow(StreamUri, Dvr());

        p.SetLiveOverride(StreamUri, false);

        Assert.True(p.IsLive);
        Assert.True(p.CanSeek);
        Assert.Equal(Dvr(), p.Live);
    }

    // ── races and republishes ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Window_SurvivesAPositionFold()
    {
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveWindow(StreamUri, Dvr());

        p.OnEvent(new PlaybackEvent(EvKind.Resumed, null, 1234));

        Assert.True(p.IsLive);
        Assert.Equal(Dvr(), p.Live);
    }

    [Fact]
    public void RepublishingTheSameWindow_FiresOnce()
    {
        using var p = New();
        Start(p, StreamUri);
        int changes = 0;
        using IDisposable sub = p.Changes.Subscribe(Observers.From<IPlaybackState>(_ => changes++));

        p.SetLiveWindow(StreamUri, Dvr());
        int afterFirst = changes;
        p.SetLiveWindow(StreamUri, Dvr());

        Assert.Equal(afterFirst, changes);
    }

    [Fact]
    public void AMovedEdge_Republishes()
    {
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveWindow(StreamUri, Dvr());
        int changes = 0;
        using IDisposable sub = p.Changes.Subscribe(Observers.From<IPlaybackState>(_ => changes++));

        p.SetLiveWindow(StreamUri, Dvr() with { LiveEdgeMs = 25_805_000 });

        Assert.True(changes > 0);
        Assert.Equal(25_805_000, p.Live.LiveEdgeMs);
    }

    [Fact]
    public void AWindowThatArrivesForTheWRONGPlayable_NeverApplies()
    {
        // The relay reads "the current uri" on the ticker thread; a track change in between must not let the previous
        // broadcast's window land on the new song.
        using var p = New();
        Start(p, StreamUri);
        Start(p, SongUri, 200_000);

        p.SetLiveWindow(StreamUri, Dvr());

        Assert.False(p.IsLive);
        Assert.True(p.CanSeek);
    }

    [Fact]
    public void AWindowSetBeforeItsTrackStarts_AppliesWhenTheTrackArrives()
    {
        // The resolve/open ordering genuinely produces this: the host can publish a timeline for a playable the
        // projection has not folded yet. Scoping by uri (not by "is it current right now") is what makes it land.
        using var p = New();
        p.SetLiveWindow(StreamUri, Dvr());
        Start(p, StreamUri);

        Assert.True(p.IsLive);
        Assert.True(p.CanSeek);
    }

    [Fact]
    public void ARemoteRestrictionStillWins_EvenWithADvrWindow()
    {
        // CanSeek folds the cluster restriction FIRST: a window cannot grant a permission the active device refused.
        using var p = New();
        Start(p, StreamUri);
        p.SetLiveWindow(StreamUri, Dvr());
        p.OnEvent(new PlaybackEvent(EvKind.Started, TrackFor(StreamUri), 0));

        Assert.True(p.CanSeek);   // local playback re-arms every capability
    }
}

/// <summary>
/// The module cache's live answer as the relay reads it — and specifically the three-valued reading that fixes the
/// latched-false defect: <c>null</c> means "not resolved yet", which is NOT the same as "not live".
/// </summary>
public class ModuleLivenessProbeTests
{
    const string StreamUri = "wavee:module:wavee.youtube:aGVsbG8";

    static ResolvedPlayable Resolved(bool isLive, long? expiresAt = null) => new(
        PlayableId: "hello", Title: "t", Artists: Array.Empty<string>(), ArtworkUrl: null, DurationMs: 0,
        IsLive: isLive, Form: Wavee.Sdk.MediaForm.Video,
        Media: MediaLocator.FromUrl("https://x/y.m3u8", MediaLocator.ContainerHls),
        ExpiresAtUnixMs: expiresAt, Caps: Array.Empty<string>());

    [Fact]
    public void NonModuleUris_AnswerFalseImmediately()
    {
        Assert.False(ModuleProjectionRelay.LivenessOf(new ModulePlayableCache(), "spotify:track:abc"));
        Assert.False(ModuleProjectionRelay.LivenessOf(new ModulePlayableCache(), ""));
        Assert.False(ModuleProjectionRelay.LivenessOf(new ModulePlayableCache(), null));
    }

    [Fact]
    public void AModuleUriWithNoCacheEntry_IsPENDING_NotFalse()
    {
        // THE DEFECT. The projection publishes the track change before the resolve lands, so this is what the first
        // probe sees. Reading it as "not live" is what latched the LIVE chip off for the whole broadcast.
        Assert.Null(ModuleProjectionRelay.LivenessOf(new ModulePlayableCache(), StreamUri));
    }

    [Fact]
    public void NoHostWired_IsPENDING_NotFalse()
        => Assert.Null(ModuleProjectionRelay.LivenessOf(null, StreamUri));

    [Fact]
    public void AResolvedPlayable_AnswersItsOwnFlag()
    {
        var cache = new ModulePlayableCache();
        cache.Put(StreamUri, Resolved(isLive: true));
        Assert.True(ModuleProjectionRelay.LivenessOf(cache, StreamUri));

        cache.Put(StreamUri, Resolved(isLive: false));
        Assert.False(ModuleProjectionRelay.LivenessOf(cache, StreamUri));
    }

    [Fact]
    public void AnEXPIREDEntryStillAnswers()
    {
        // A signed url dying does not make a broadcast stop being a broadcast. Dropping the LIVE chip while the app
        // re-resolves would be a visible lie, so the probe reads through expiry.
        long now = 1_000_000;
        var cache = new ModulePlayableCache(() => now);
        cache.Put(StreamUri, Resolved(isLive: true, expiresAt: now - 1));

        Assert.Null(cache.Get(StreamUri));                                   // expired for the locator's purposes…
        Assert.True(ModuleProjectionRelay.LivenessOf(cache, StreamUri));     // …but the FACT survives
    }
}
