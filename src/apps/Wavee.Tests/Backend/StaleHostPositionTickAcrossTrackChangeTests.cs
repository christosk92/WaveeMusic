using System;
using System.Collections.Immutable;
using System.Diagnostics;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE "3:04 / -0:24" TRACK-CHANGE FLASH (audit finding S1 #4). The reused audio host is just pointed at a new stream
/// on a track change (<c>PlaybackController</c> never swaps the host object for an ordinary audio→audio transition),
/// so a <see cref="AudioHostSignalKind.PositionTick"/> the OLD stream had already queued can still reach
/// <see cref="NowPlayingProjection.OnHostSignal"/> a frame or two AFTER the boundary landed — carrying the PREVIOUS
/// track's position. <c>OnHostSignal</c> used to fold <c>s.PositionMs</c> unconditionally, so that stale tick clobbered
/// the just-reset 0 with the old position, and the ticks stream published it against the NEW track's duration: an old
/// ~3:04 position over a ~3:28 duration reads as an 88%-full bar and "-0:24" remaining, exactly the observed flash
/// before the next (correct) tick puts it back.
///
/// The fix: <see cref="NowPlayingProjection"/> stamps a <c>Stopwatch.GetTimestamp()</c> boundary on every REAL local
/// track change, and <c>OnHostSignal</c> drops such a tick outright — neither folds it into <c>_posMs</c> nor
/// republishes it — for one whose OWN sample instant (<see cref="AudioHostSignal.SampleQpc"/>, stamped by the host at
/// construction) predates the boundary; a tick can never legitimately describe an instant before the track it claims
/// to belong to started. (Publishing the CORRECTED position paired with the tick's own STALE SampleQpc would just
/// trade one bug for another — the lyrics clock extrapolates from that pair, so it must describe one real instant.)
///
/// The SAME finding also covers the transport glyph flipping pause→play→pause across an ordinary track change: the
/// (reused) audio host reports <c>Prebuffering</c>/<c>Buffering</c> while it loads the new file, and
/// <see cref="AudioHostSignal"/>'s 2-arg constructor — every real Buffering/Prebuffering call site — only sets
/// <c>IsPlaying</c> true for Playing/PositionTick/Recovering. <c>OnHostSignal</c> used to fold that false straight
/// into <c>_isPlaying</c>, flipping the glyph to Play for the sub-second it takes to prebuffer even though the user
/// never asked to pause. The fix: Buffering/Prebuffering update the dedicated buffering flags only, never
/// <c>_isPlaying</c> — a genuine pause (its own, distinct <c>AudioHostSignalKind</c>) still folds normally.
/// </summary>
public class StaleHostPositionTickAcrossTrackChangeTests
{
    static Track Local(string uri, long durationMs) => new(
        Id: uri, Uri: uri, Title: "t", Artists: Array.Empty<ArtistRef>(),
        Album: new AlbumRef("", "", ""), DurationMs: durationMs, IsExplicit: false, Image: null);

    static QueueSnapshot Snap(Track track) => new(
        Revision: 1, ContextUri: "spotify:album:al", AutoplayContextUri: null,
        Current: new QueueEntry(QueueItemId.None, "now", track, QueueBucket.NowPlaying, QueueProvider.Context, false, "u-now"),
        History: ImmutableArray<QueueEntry>.Empty, UserQueue: ImmutableArray<QueueEntry>.Empty,
        Upcoming: ImmutableArray<QueueEntry>.Empty, Shuffle: false, Repeat: RepeatMode.Off,
        ClusterQueueRevision: "", ContextCursor: 0);

    [Fact]
    public void LatePositionTick_FromTheOutgoingTrack_DoesNotClobberTheNewTracksPosition()
    {
        const long oldDurationMs = 184_000;   // 3:04
        const long newDurationMs = 208_000;   // 3:28
        const long oldPositionMs = 184_000;   // the old track, nearly finished, when the boundary lands
        var oldTrack = Local("spotify:track:klaas", oldDurationMs);
        var newTrack = Local("spotify:track:attention", newDurationMs);
        long now = 1_000_000;   // fixed wall clock — isolates the assertion from real elapsed time between statements
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), clock: () => now);
        p.Ownership.Claim(ClaimCause.UserResume);   // host signals fold only while WE own playback

        // The stale tick's own sample instant is captured BEFORE the track boundary below — exactly what a queued
        // tick from the outgoing stream would carry.
        long staleQpc = Stopwatch.GetTimestamp();

        p.ApplyLocalSnapshot(Snap(oldTrack), new PlaybackEvent(EvKind.Started, oldTrack, 0));
        // The boundary: the queue advances to the new track, position resets to 0 like any real track change.
        p.ApplyLocalSnapshot(Snap(newTrack), new PlaybackEvent(EvKind.Started, newTrack, 0));
        Assert.Equal(0, p.PositionMs);

        long? observed = null;
        using var sub = p.PositionTicks.Subscribe(Observers.From<PositionSample>(v => observed = v.PositionMs));

        // The straggling PositionTick: the OLD track's position, sampled before the boundary, arriving after it.
        var staleTick = new AudioHostSignal(AudioHostSignalKind.PositionTick, oldPositionMs) with { SampleQpc = staleQpc };
        p.OnHostSignal(staleTick);

        Assert.Equal(0, p.PositionMs);   // must still read the new track's (reset) position, not the old one
        Assert.Null(observed);           // …and nothing republished at all — never a "0 position, stale instant" pair
    }

    [Fact]
    public void APositionTick_SampledAfterTheBoundary_StillFoldsNormally()
    {
        // The fix must not swallow legitimate ticks for the track that is actually playing.
        const long durationMs = 208_000;
        var track = Local("spotify:track:attention", durationMs);
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        p.Ownership.Claim(ClaimCause.UserResume);

        p.ApplyLocalSnapshot(Snap(track), new PlaybackEvent(EvKind.Started, track, 0));

        long? observed = null;
        using var sub = p.PositionTicks.Subscribe(Observers.From<PositionSample>(v => observed = v.PositionMs));

        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.PositionTick, 5_000));   // sampled now — after the boundary

        Assert.Equal(5_000, p.PositionMs);
        Assert.Equal(5_000, observed);
    }

    [Fact]
    public void PrebufferingTheNextTrack_DoesNotFlipTheTransportGlyphToPlay()
    {
        // A normal, fast local track change: the host starts prebuffering the new file while the user's intent is
        // still "playing" — the glyph must hold at Pause (IsPlaying=true) throughout, never flash Play.
        const long durationMs = 208_000;
        var track = Local("spotify:track:attention", durationMs);
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        p.Ownership.Claim(ClaimCause.UserResume);

        p.ApplyLocalSnapshot(Snap(track), new PlaybackEvent(EvKind.Started, track, 0));
        Assert.True(p.IsPlaying);

        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 0));
        Assert.True(p.IsPlaying);        // still true — Prebuffering is not a pause
        Assert.True(p.IsPrebuffering);   // …but the dedicated flag (the top-edge sweep) still sees it

        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Buffering, 0));
        Assert.True(p.IsPlaying);
        Assert.True(p.IsBuffering);
    }

    [Fact]
    public void APausedTrack_ThatThenBuffers_StaysPaused()
    {
        // The exclusion is narrow: a GENUINE pause (its own AudioHostSignalKind, not Buffering/Prebuffering) must
        // still fold, and buffering while already paused must not resurrect a stale "playing" reading either.
        const long durationMs = 208_000;
        var track = Local("spotify:track:attention", durationMs);
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        p.Ownership.Claim(ClaimCause.UserResume);

        p.ApplyLocalSnapshot(Snap(track), new PlaybackEvent(EvKind.Started, track, 0));
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Paused, 1_000));
        Assert.False(p.IsPlaying);

        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Buffering, 1_000));
        Assert.False(p.IsPlaying);
    }
}
