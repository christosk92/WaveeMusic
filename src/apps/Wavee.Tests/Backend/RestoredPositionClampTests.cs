using System;
using System.Collections.Immutable;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE PLAYER-BAR "35:32 / −0:00" BUG. A restored, paused track showed an elapsed readout far past its own length and
/// a negative-reading remainder. <c>NowPlayingProjection.Pos()</c> DID already skip forward interpolation while
/// paused ("paused ⇒ no interpolation" was never broken) — but its paused/buffering branch returned <c>_posMs</c>
/// completely UNCLAMPED, unlike the playing branch right below it, which has always clamped to <c>[0, duration]</c>.
/// <c>_posMs</c> is folded from several places (a remote cluster snapshot aged forward from its OWN timestamp, a host
/// signal, a restored launch snapshot) and none of them is guaranteed to land inside the track's length — a stale or
/// corrupt upstream value then reached the player bar verbatim.
///
/// The fix: <c>Pos()</c> clamps to <c>[0, duration]</c> on EVERY read, playing or not — mirrors
/// <see cref="LaunchRecoveryBufferingTests"/>'s fixture shape.
/// </summary>
public class RestoredPositionClampTests
{
    static Track Local(string uri, long durationMs) => new(
        Id: uri, Uri: uri, Title: "Ik Wil Dat Je Liegt", Artists: Array.Empty<ArtistRef>(),
        Album: new AlbumRef("", "", ""), DurationMs: durationMs, IsExplicit: false, Image: null);

    static QueueSnapshot Snap(Track track) => new(
        Revision: 1, ContextUri: "spotify:album:al", AutoplayContextUri: null,
        Current: new QueueEntry(QueueItemId.None, "now", track, QueueBucket.NowPlaying, QueueProvider.Context, false, "u-now"),
        History: ImmutableArray<QueueEntry>.Empty, UserQueue: ImmutableArray<QueueEntry>.Empty,
        Upcoming: ImmutableArray<QueueEntry>.Empty, Shuffle: false, Repeat: RepeatMode.Off,
        ClusterQueueRevision: "", ContextCursor: 0);

    [Fact]
    public void PausedRestore_PositionPastDuration_IsClampedToDuration()
    {
        // A 2:54 (174_000 ms) track restored paused at a corrupt 35:32 (2_132_000 ms) — the exact reported shape.
        const long durationMs = 174_000;
        const long corruptPositionMs = 2_132_000;
        var track = Local("spotify:track:liegt", durationMs);
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());

        p.ApplyLocalSnapshot(Snap(track), new PlaybackEvent(EvKind.Paused, track, corruptPositionMs));

        Assert.False(p.IsPlaying);
        Assert.Equal(durationMs, p.PositionMs);   // clamped to the track's own length, never past it
        Assert.Equal(durationMs, p.DurationMs);
    }

    [Fact]
    public void PausedRestore_PositionWithinDuration_IsUnaffected()
    {
        // The ordinary case must not regress: a legitimate mid-song paused position passes through untouched.
        const long durationMs = 174_000;
        const long positionMs = 90_000;
        var track = Local("spotify:track:liegt", durationMs);
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());

        p.ApplyLocalSnapshot(Snap(track), new PlaybackEvent(EvKind.Paused, track, positionMs));

        Assert.Equal(positionMs, p.PositionMs);
    }

    [Fact]
    public void PausedRestore_NegativePosition_IsClampedToZero()
    {
        const long durationMs = 174_000;
        var track = Local("spotify:track:liegt", durationMs);
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());

        p.ApplyLocalSnapshot(Snap(track), new PlaybackEvent(EvKind.Paused, track, -500));

        Assert.Equal(0, p.PositionMs);
    }

    [Fact]
    public void PausedRestore_PositionNeverAdvancesWithWallClock()
    {
        // "Paused ⇒ no interpolation" — pins the OTHER half of the bug's hypothesis (already correct, kept honest by a
        // test): even though _posAnchorWall is stamped fresh at fold time, a paused position must be frozen no matter
        // how much wall-clock time passes before the player bar is actually looked at.
        const long durationMs = 174_000;
        const long positionMs = 90_000;
        var track = Local("spotify:track:liegt", durationMs);
        long now = 1_000_000;
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), clock: () => now);

        p.ApplyLocalSnapshot(Snap(track), new PlaybackEvent(EvKind.Paused, track, positionMs));
        Assert.Equal(positionMs, p.PositionMs);

        now += 35 * 60_000 + 32_000;   // +35:32 of wall-clock time, e.g. the app just sat open

        Assert.Equal(positionMs, p.PositionMs);   // unchanged — still paused, still frozen
    }

    [Fact]
    public void Playing_PositionPastDuration_IsStillClampedToDuration()
    {
        // The playing branch already clamped before this fix; pin it stays true now that both branches share one cap.
        const long durationMs = 174_000;
        var track = Local("spotify:track:liegt", durationMs);
        long now = 1_000_000;
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), clock: () => now);

        p.ApplyLocalSnapshot(Snap(track), new PlaybackEvent(EvKind.Resumed, track, durationMs - 1000));
        now += 60_000;   // a full minute of "playing" — well past the track's own end

        Assert.Equal(durationMs, p.PositionMs);
    }

    [Fact]
    public void HostPositionTick_PastDuration_IsClampedOnTheTicksStream()
    {
        // A3: OnHostSignal used to push the host's RAW, unclamped s.PositionMs straight onto PositionTicks (the 200 ms
        // live tick), while the 1 Hz Tick()/PositionMs readers already went through Pos()'s clamp — two derivations of
        // "now" disagreeing whenever the raw host value strayed outside [0, duration] (a device-reopen soft reload
        // landing between a fresh session's clock=0 and its restoring seek is exactly such a moment). The fix routes
        // the ticks stream through the SAME clamped Pos() every other reader uses.
        const long durationMs = 174_000;
        var track = Local("spotify:track:liegt", durationMs);
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        p.ApplyLocalSnapshot(Snap(track), new PlaybackEvent(EvKind.Resumed, track, 0));

        long? observed = null;
        using var sub = p.PositionTicks.Subscribe(Observers.From<long>(v => observed = v));

        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.PositionTick, durationMs + 5_000));

        Assert.Equal(durationMs, observed);
    }
}
