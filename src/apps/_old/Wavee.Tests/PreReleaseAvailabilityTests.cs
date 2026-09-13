using System;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// ── The ONE "is this row out yet?" predicate (TrackAvailability.IsNotYetOut) ─────────────────────────────────────────
// Three surfaces read it and none may disagree: the greyed row (Components/TrackRow), the play gate
// (DetailTracks.PlayRow) and the "N of M songs" fact tile (DetailTrailing). Two properties make it correct:
//
//   1. only a CONFIRMED Unavailable counts — a null Availability means "no response ever stated a verdict", and every
//      write except getAlbum/getTrack leaves it null, so treating unknown as pending would grey whole libraries;
//   2. the AvailableAt clause makes the RELEASE-DROP transition self-healing — the server's verdict is frozen at fetch
//      time but earliest_live_timestamp is an INSTANT, so a row whose moment has passed goes playable on the next
//      render instead of staying grey until the album is read again.
public class PreReleaseAvailabilityTests
{
    static readonly DateTimeOffset Future = DateTimeOffset.UtcNow.AddDays(30);
    static readonly DateTimeOffset Past = DateTimeOffset.UtcNow.AddDays(-30);

    static Track Row(Availability? availability, DateTimeOffset? availableAt) =>
        new("t", "spotify:track:t", "T", Array.Empty<ArtistRef>(),
            new AlbumRef("al", "spotify:album:al", "Al"), 200_000, false, null,
            Availability: availability, AvailableAt: availableAt);

    // ── the truth table ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unavailable_WithNoInstant_IsPending()
        => Assert.True(Row(Availability.Unavailable, null).IsNotYetOut());

    [Fact]
    public void Unavailable_WithAFutureInstant_IsPending()
        => Assert.True(Row(Availability.Unavailable, Future).IsNotYetOut());

    [Fact]
    public void Unavailable_WithAPASTInstant_IsOut_TheReleaseDropHeal()
    {
        // THE case the AvailableAt clause exists for. The server said "unavailable" when the page was fetched; the
        // moment named on the same payload has since passed, so the row is out — no refetch, no stale grey.
        Assert.False(Row(Availability.Unavailable, Past).IsNotYetOut());
    }

    [Theory]
    [InlineData(0)]   // no instant
    [InlineData(1)]   // future instant — a confirmed Playable still wins outright: the timestamp is only ever consulted
    [InlineData(2)]   // past instant     to RELEASE a row, never to hold one back
    public void Playable_IsNeverPending_WhateverTheInstantSays(int shape)
        => Assert.False(Row(Availability.Playable, Instant(shape)).IsNotYetOut());

    [Theory]
    [InlineData(0)]   // no instant
    [InlineData(1)]   // future instant
    [InlineData(2)]   // past instant
    public void UnknownAvailability_IsNeverPending(int shape)
    {
        // Null = "nobody has told us". A cluster / library / extended-metadata write carries no verdict at all, and
        // those are the majority of writes.
        Assert.False(Row(null, Instant(shape)).IsNotYetOut());
    }

    static DateTimeOffset? Instant(int shape) => shape == 0 ? null : shape == 1 ? Future : Past;

    // ── the exclusions the album page computes from it ────────────────────────────────────────────────────────────────

    [Fact]
    public void APartlyReleasedTracklist_SplitsIntoNOfM()
    {
        // The "5 of 11 Songs" tile and the count/length exclusion are both this predicate applied per row. Asserted
        // over the real predicate so the fact tile can never drift from the greying and the play gate.
        Track[] album =
        {
            Row(Availability.Playable, Past),
            Row(Availability.Playable, null),
            Row(Availability.Unavailable, Past),        // dropped since the fetch → counts as OUT
            Row(Availability.Unavailable, Future),      // pending
            Row(Availability.Unavailable, null),        // pending
            Row(null, null),                            // unknown → counted, never hidden
        };

        int outNow = 0;
        foreach (var t in album) if (!t.IsNotYetOut()) outNow++;

        Assert.Equal(4, outNow);
        Assert.Equal(6, album.Length);
    }

    // ── the shared predicate as the list filter reads it ──────────────────────────────────────────────────────────────
    // TrackFilterModel.PlayableOnly routes through the SAME predicate, which is why a pending row disappears from a
    // "playable only" list and a just-dropped one reappears — without either surface knowing about the other.

    static bool PassesPlayableOnly(Track t) =>
        TrackFilterModel.Matches(t, "", new TrackFilterState(Flags: TrackFilterFlags.PlayableOnly),
                                 hasVideo: false, isSaved: false, now: DateTimeOffset.UtcNow);

    [Fact]
    public void PlayableOnly_HidesAPendingRow()
        => Assert.False(PassesPlayableOnly(Row(Availability.Unavailable, Future)));

    [Fact]
    public void PlayableOnly_HidesARegionBlockedRow()
        => Assert.False(PassesPlayableOnly(Row(Availability.Unavailable, null)));

    [Fact]
    public void PlayableOnly_KEEPSARowWhoseReleaseMomentHasPassed()
    {
        // Same heal, reached through the filter: the stale server verdict no longer hides a row that is now out.
        // NOTE: the predicate reads DateTimeOffset.UtcNow internally, NOT the `now` the filter is handed — so this is
        // exercised with far-past / far-future instants rather than by moving the filter's clock.
        Assert.True(PassesPlayableOnly(Row(Availability.Unavailable, Past)));
    }

    [Fact]
    public void PlayableOnly_KeepsARowWithNoVerdict()
        => Assert.True(PassesPlayableOnly(Row(null, null)));
}
