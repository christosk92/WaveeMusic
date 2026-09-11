using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The expanded track drawer's ROW SET — which is the drawer's HEIGHT, one
/// <c>TrackVersionsPanel.RowH</c> band per entry. The drawer lives inside a MEASURED virtualized row, so the number
/// the list reserves for the row and the number the panel renders have to come from this one decision; a row set that
/// changes shape after the reveal has solved its height is exactly what makes the reserved band and the painted
/// content disagree (content past the band, or an empty band).</summary>
public class TrackDrawerRowsTests
{
    static Track Song(string id = "t1", string title = "Boy With Luv") => new(
        id, "spotify:track:" + id, title, Array.Empty<ArtistRef>(), new AlbumRef("al", "spotify:album:al", "Persona"),
        229_000L, false, null);

    static TrackVersion Video(string id = "v1") =>
        new("spotify:track:" + id, TrackVersionKind.Video, "Music video", null, 235_000L);

    static TrackVersion Alternate(string id) =>
        new("spotify:track:" + id, TrackVersionKind.Audio, "Live", null, 240_000L);

    static TrackExpansion Expansion(params TrackVersion[] versions) =>
        new(versions, Array.Empty<AudioFormatOption>());

    static List<string> Keys(IReadOnlyList<TrackVersion> rows)
    {
        var keys = new List<string>(rows.Count);
        for (int i = 0; i < rows.Count; i++) keys.Add(TrackDrawerRows.KeyOf(rows[i]));
        return keys;
    }

    [Fact]
    public void TheTrackItselfIsAlwaysTheFirstRow()
    {
        // Without it, "play this track as FLAC" would have nowhere to live and the drawer would only ever be about
        // OTHER recordings. It is also the one row that needs no fetch, so it is the drawer's floor height.
        var rows = TrackDrawerRows.For(Song(), expansion: null, hasVideo: false);

        Assert.Single(rows);
        Assert.Equal(TrackVersionKind.Original, rows[0].Kind);
        Assert.Equal("spotify:track:t1", rows[0].Uri);
        Assert.False(TrackDrawerRows.IsPlaceholder(rows[0]));
    }

    [Fact]
    public void AKnownVideoIsReservedBeforeTheFetchAnswers()
    {
        // The association plane already said "this track has a music video", so the band is reserved up front rather
        // than waited for: the FIRST solved height is then the final height, instead of the reveal chasing a row that
        // mounts 200 ms into the animation.
        var rows = TrackDrawerRows.For(Song(), expansion: null, hasVideo: true);

        Assert.Equal(2, rows.Count);
        Assert.True(TrackDrawerRows.IsPlaceholder(rows[1]));
        Assert.Equal(TrackVersionKind.Video, rows[1].Kind);
    }

    [Fact]
    public void TheReservedBandAndTheHydratedVideoShareOneKey()
    {
        // The whole point of the reservation: data landing must PATCH that row rather than remove+insert one, so the
        // drawer's height never steps and the reconciler never mints a second node for the same band.
        var reserved = TrackDrawerRows.For(Song(), expansion: null, hasVideo: true);
        var hydrated = TrackDrawerRows.For(Song(), Expansion(Video()), hasVideo: true);

        Assert.Equal(Keys(reserved), Keys(hydrated));
        Assert.Equal(reserved.Count, hydrated.Count);
        Assert.False(TrackDrawerRows.IsPlaceholder(hydrated[1]));
    }

    [Fact]
    public void OnlyOneVideoRowHoweverManyThePayloadCarries()
    {
        // Kind 99 yields at most ONE counterpart, the reserved band accounts for exactly one, and VideoRowKey can only
        // name one. Appending every kind-99 entry (what the render path used to do) inserted bands no reservation
        // covered AND minted duplicate keys under one parent — a keyed-reconcile collision.
        var rows = TrackDrawerRows.For(Song(), Expansion(Video("v1"), Video("v2")), hasVideo: true);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "spotify:track:t1", TrackDrawerRows.VideoRowKey }, Keys(rows));
    }

    [Fact]
    public void VideoLeadsAlternateAudioAndTheSelfRowLeadsBoth()
    {
        // Render order is part of the contract: the reserved video band sits at a fixed offset, so late-arriving
        // alternate audio may only APPEND — never reorder what the reveal already solved a height for.
        var rows = TrackDrawerRows.For(
            Song(), Expansion(Alternate("a1"), Video("v1"), Alternate("a2")), hasVideo: true);

        Assert.Equal(
            new[] { "spotify:track:t1", TrackDrawerRows.VideoRowKey, "spotify:track:a1", "spotify:track:a2" },
            Keys(rows));
    }

    [Fact]
    public void AnAnsweredFetchWithNoVideoDropsTheReservedBand()
    {
        // Once the fetch has ANSWERED it is authoritative in both directions. A placeholder that can never resolve
        // would shimmer forever, which is a worse lie than the one-band correction — and the correction is real new
        // information, not the reveal chasing itself.
        var rows = TrackDrawerRows.For(Song(), Expansion(), hasVideo: true);

        Assert.Single(rows);
        Assert.Equal(TrackVersionKind.Original, rows[0].Kind);
    }

    [Fact]
    public void AVideoTheAssociationPlaneMissedStillRendersOnce()
    {
        // The reverse disagreement: no reservation was made, the fetch found a video. It appears exactly once, in its
        // fixed slot, so the drawer grows by exactly one band.
        var rows = TrackDrawerRows.For(Song(), Expansion(Video()), hasVideo: false);

        Assert.Equal(2, rows.Count);
        Assert.Equal(TrackVersionKind.Video, rows[1].Kind);
        Assert.False(TrackDrawerRows.IsPlaceholder(rows[1]));
    }

    [Fact]
    public void FillReusesTheCallersListAndLeavesNoResidue()
    {
        // The render path fills ONE retained list per panel instance (no per-render allocation), so a re-render after
        // the fetch must not leave the previous verdict's rows behind it.
        var rows = new List<TrackVersion>(3);
        TrackDrawerRows.Fill(rows, Song(), Expansion(Video(), Alternate("a1"), Alternate("a2")), hasVideo: true);
        Assert.Equal(4, rows.Count);

        TrackDrawerRows.Fill(rows, Song(), expansion: null, hasVideo: false);
        Assert.Single(rows);
    }

    [Fact]
    public void TheSelfRowCarriesTheTracksOwnFactsSoItsFormatHasAHome()
    {
        // The drawer's own track is projected as a version so ONE row factory renders every entry — and it must carry
        // the track's duration/tempo/key, or the drawer reads as less informative than the row it expanded from.
        var track = Song() with { DurationMs = 229_000L, TempoBpm = 120d, MusicalKey = "D", CamelotCode = "10B" };
        var rows = TrackDrawerRows.For(track, expansion: null, hasVideo: false);

        Assert.Equal(229_000L, rows[0].DurationMs);
        Assert.Equal(120d, rows[0].TempoBpm);
        Assert.Equal("D", rows[0].MusicalKey);
        Assert.Equal("10B", rows[0].CamelotCode);
    }
}
