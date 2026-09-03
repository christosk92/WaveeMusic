using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The "About this release" block (<c>Features/Detail/AlbumReleaseFactsRules.cs</c>) — the fixed-shape record
/// that replaced the tile arithmetic <c>DetailTrailing.AlbumFactTiles</c>/<c>ReleaseNotes</c> used to run on every
/// render. The point of pinning this here is the composition itself: Songs/Length/Released are a FIXED set of
/// tiles (a value simply starts null and later refines in place), Label is a note rather than a fourth tile, and
/// <c>now</c> is always the value the caller injects — never a live clock read inside the rule.</summary>
public class AlbumReleaseFactsRulesTests
{
    static readonly DateTimeOffset Now = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

    static Track Released(long durationMs) => MakeTrack(durationMs, notYetOut: false);
    static Track NotYetOut() => MakeTrack(0, notYetOut: true);

    static Track MakeTrack(long durationMs, bool notYetOut) => new(
        Id: "t", Uri: "spotify:track:t", Title: "Title",
        Artists: Array.Empty<ArtistRef>(), Album: new AlbumRef("", "", ""),
        DurationMs: durationMs, IsExplicit: false, Image: null,
        // IsNotYetOut() is Availability.Unavailable AND no AvailableAt that has already passed — Availability alone
        // is enough for a track that has never been given an AvailableAt at all.
        Availability: notYetOut ? Availability.Unavailable : Availability.Playable);

    static IReadOnlyList<Track> Tracks(params Track[] tracks) => tracks;

    // ── Songs ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Songs_CountsOnlyReleasedTracks_AsNOfTotal()
    {
        var tracks = Tracks(Released(180_000), Released(200_000), NotYetOut());
        var facts = AlbumReleaseFactsRules.For(tracks, null, null, null, null, null, null, null, Now);
        Assert.Equal("2 of 3", facts.Songs);
    }

    [Fact]
    public void Songs_PlainCount_WhenEveryTrackIsOut()
    {
        var tracks = Tracks(Released(180_000), Released(200_000));
        var facts = AlbumReleaseFactsRules.For(tracks, null, null, null, null, null, null, null, Now);
        Assert.Equal("2", facts.Songs);
    }

    [Fact]
    public void Songs_Null_WhenNoTracks()
    {
        var facts = AlbumReleaseFactsRules.For(Array.Empty<Track>(), null, null, null, null, null, null, null, Now);
        Assert.Null(facts.Songs);
    }

    // ── Length ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Length_NullWhenNoDurations()
    {
        // Two OUT tracks, both duration-less: the count exists, the length must not lie "0 min".
        var tracks = Tracks(Released(0), Released(0));
        var facts = AlbumReleaseFactsRules.For(tracks, null, null, null, null, null, null, null, Now);
        Assert.Null(facts.Length);
        Assert.NotNull(facts.Songs);
    }

    [Fact]
    public void Length_SumsOnlyOutTracks()
    {
        // 47 min out + a not-yet-out track whose unknown duration must NOT be counted.
        var tracks = Tracks(Released(47 * 60_000), NotYetOut());
        var facts = AlbumReleaseFactsRules.For(tracks, null, null, null, null, null, null, null, Now);
        Assert.Equal("47 min", facts.Length);
    }

    // ── Released ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("YEAR", "2014")]
    [InlineData("MONTH", "November 2014")]
    [InlineData("DAY", "November 4, 2014")]
    [InlineData(null, "November 4, 2014")]   // default precision reads as full day
    public void Released_ByPrecision_YearMonthDay(string? precision, string expected)
    {
        var facts = AlbumReleaseFactsRules.For(Array.Empty<Track>(), "2014-11-04", precision, null, null, null, null, null, Now);
        Assert.Equal(expected, facts.Released);
    }

    [Fact]
    public void Released_FallsBackToYear_BeforeRichRung()
    {
        // Open rung: no ReleaseDate/precision yet, only the year the tracklist carries.
        var facts = AlbumReleaseFactsRules.For(Array.Empty<Track>(), null, null, 2014, null, null, null, null, Now);
        Assert.Equal("2014", facts.Released);
    }

    [Fact]
    public void Released_PrefersFormattedDate_OverYear()
    {
        var facts = AlbumReleaseFactsRules.For(Array.Empty<Track>(), "2025-02-07", "DAY", 2014, null, null, null, null, Now);
        Assert.Equal("February 7, 2025", facts.Released);
    }

    [Fact]
    public void Released_Null_WhenNothingKnown()
    {
        var facts = AlbumReleaseFactsRules.For(Array.Empty<Track>(), null, null, 0, null, null, null, null, Now);
        Assert.Null(facts.Released);
    }

    // ── ReleasesInFuture ─────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, true)]     // a day ahead of the injected clock
    [InlineData(-1, false)]   // a day behind it
    [InlineData(0, false)]    // exactly now is not "in the future"
    public void ReleasesInFuture_UsesInjectedNow(int daysFromNow, bool expected)
    {
        var facts = AlbumReleaseFactsRules.For(Array.Empty<Track>(), null, null, null, Now.AddDays(daysFromNow),
            null, null, null, Now);
        Assert.Equal(expected, facts.ReleasesInFuture);
    }

    // ── Label ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Label_IsANote_NotATile()
    {
        // A label with nothing else known must not make HasTiles true — Label never reshapes the tile grid.
        var facts = AlbumReleaseFactsRules.For(Array.Empty<Track>(), null, null, null, null, "BLØF", null, null, Now);
        Assert.Equal("BLØF", facts.Label);
        Assert.False(facts.HasTiles);
        Assert.False(facts.IsEmpty);   // Label alone is still something to show
    }

    [Fact]
    public void Label_Null_WhenEmpty()
    {
        var facts = AlbumReleaseFactsRules.For(Array.Empty<Track>(), null, null, null, null, "", null, null, Now);
        Assert.Null(facts.Label);
    }

    // ── Notes ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Notes_CourtesyBeforeCopyright_SkipsEmpty()
    {
        var facts = AlbumReleaseFactsRules.For(Array.Empty<Track>(), null, null, null, null, null,
            "Courtesy of Some Label", "(P) 2025 Some Label\n(C) 2025 Some Label", Now);
        Assert.Equal(new[] { "Courtesy of Some Label", "(P) 2025 Some Label\n(C) 2025 Some Label" }, facts.Notes);
    }

    [Fact]
    public void Notes_SkipsCourtesy_WhenAbsent()
    {
        var facts = AlbumReleaseFactsRules.For(Array.Empty<Track>(), null, null, null, null, null, null, "(C) 2025", Now);
        Assert.Equal(new[] { "(C) 2025" }, facts.Notes);
    }

    [Fact]
    public void Notes_Empty_WhenBothAbsent()
    {
        var facts = AlbumReleaseFactsRules.For(Array.Empty<Track>(), null, null, null, null, null, null, null, Now);
        Assert.Empty(facts.Notes);
    }

    // ── Empty / HasTiles ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_WhenNothingKnown()
    {
        var facts = AlbumReleaseFactsRules.For(Array.Empty<Track>(), null, null, null, null, null, null, null, Now);
        Assert.True(facts.IsEmpty);
        Assert.Equal(AlbumReleaseFacts.Empty, facts);
    }

    [Fact]
    public void HasTiles_FalseUntilOpen()
    {
        // Before the tracklist lands there is no Songs/Length/Released at all.
        var before = AlbumReleaseFactsRules.For(Array.Empty<Track>(), null, null, null, null, null, null, null, Now);
        Assert.False(before.HasTiles);

        // The Open rung: tracks land, Songs/Length populate — the grid may now mount.
        var after = AlbumReleaseFactsRules.For(Tracks(Released(180_000)), null, null, null, null, null, null, null, Now);
        Assert.True(after.HasTiles);
    }
}
