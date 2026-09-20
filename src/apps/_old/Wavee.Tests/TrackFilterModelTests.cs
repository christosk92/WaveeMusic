using System;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class TrackFilterModelTests
{
    static Track Song(
        string title = "Blue Monday", string artist = "New Order", string album = "Power, Corruption & Lies",
        long duration = 450_000, bool explicitTrack = false,
        TrackOrigin origin = TrackOrigin.Streamed, Availability availability = Availability.Playable,
        DateTimeOffset? added = null) =>
        new("1", "spotify:track:1", title,
            [new ArtistRef("a", "spotify:artist:a", artist)],
            new AlbumRef("b", "spotify:album:b", album),
            duration, explicitTrack, null, added, Origin: origin, Availability: availability);

    [Fact]
    public void SearchScope_UsesOnlySelectedMetadata()
    {
        var song = Song();
        var title = new TrackFilterState(SearchScope: TrackSearchScope.Title);
        var artist = new TrackFilterState(SearchScope: TrackSearchScope.Artist);

        Assert.False(TrackFilterModel.Matches(song, "New Order", title, false, false, DateTimeOffset.UtcNow));
        Assert.True(TrackFilterModel.Matches(song, "New Order", artist, false, false, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AlbumTrack_SupportsDurationAndAvailabilityFacets()
    {
        var song = Song(availability: Availability.Unavailable);
        var longOnly = new TrackFilterState(Duration: TrackDurationRange.OverFiveMinutes);
        var playable = new TrackFilterState(Flags: TrackFilterFlags.PlayableOnly);

        Assert.True(TrackFilterModel.Matches(song, "", longOnly, false, false, DateTimeOffset.UtcNow));
        Assert.False(TrackFilterModel.Matches(song, "", playable, false, false, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PlaylistTrack_SupportsDateAndTraitModes()
    {
        var now = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        var song = Song(explicitTrack: true, added: now.AddDays(-3));
        var filter = new TrackFilterState(
            ExplicitMode: TrackTraitMode.Hide,
            VideoMode: TrackTraitMode.Only,
            Added: TrackAddedRange.LastSevenDays);

        // hasVideo is a PARAMETER by design: the caller answers it from VideoPresence (the association plane ∪
        // overrides), never from a track field — true here mirrors a video-bearing row.
        Assert.False(TrackFilterModel.Matches(song, "", filter, hasVideo: true, false, now));
        Assert.Equal(3, filter.ActiveCount);
    }

    [Theory]
    [InlineData(TrackTraitMode.All, false, true)]
    [InlineData(TrackTraitMode.All, true, true)]
    [InlineData(TrackTraitMode.Hide, false, true)]
    [InlineData(TrackTraitMode.Hide, true, false)]
    [InlineData(TrackTraitMode.Only, false, false)]
    [InlineData(TrackTraitMode.Only, true, true)]
    public void ExplicitTraitMode_ImplementsAllHideOnly(TrackTraitMode mode, bool isExplicit, bool expected)
    {
        var song = Song(explicitTrack: isExplicit);
        var filter = new TrackFilterState(ExplicitMode: mode);

        Assert.Equal(expected, TrackFilterModel.Matches(song, "", filter, false, false, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(TrackTraitMode.All, false, true)]
    [InlineData(TrackTraitMode.All, true, true)]
    [InlineData(TrackTraitMode.Hide, false, true)]
    [InlineData(TrackTraitMode.Hide, true, false)]
    [InlineData(TrackTraitMode.Only, false, false)]
    [InlineData(TrackTraitMode.Only, true, true)]
    public void VideoTraitMode_ImplementsAllHideOnly(TrackTraitMode mode, bool hasVideo, bool expected)
    {
        var song = Song();
        var filter = new TrackFilterState(VideoMode: mode);

        Assert.Equal(expected, TrackFilterModel.Matches(song, "", filter, hasVideo, false, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void OriginAndLikedFacetsCompose()
    {
        var song = Song(origin: TrackOrigin.Local);
        var filter = new TrackFilterState(
            Flags: TrackFilterFlags.LikedOnly,
            Origin: TrackOriginFilter.Local);

        Assert.True(TrackFilterModel.Matches(song, "", filter, false, true, DateTimeOffset.UtcNow));
        Assert.False(TrackFilterModel.Matches(song, "", filter, false, false, DateTimeOffset.UtcNow));
    }

    // ── The Liked rail's lenses: an arbitrary saved-date window, and an exact artist ────────────────────────────────

    /// <summary>The window a sparkline bar hands over. Anchored on a fixed instant so the rows below are dates, not
    /// arithmetic against "now".</summary>
    static readonly DateTimeOffset WindowStart = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    static long AfterMs => WindowStart.ToUnixTimeMilliseconds();
    static long BeforeMs => WindowStart.AddDays(7).ToUnixTimeMilliseconds();

    /// <summary>Half-open, as <c>(after, before]</c>. The rolling week buckets are laid end to end — bucket k ends
    /// exactly where k+1 begins — so a like on the seam has to belong to exactly one of them, or the twelve bar lenses
    /// would return more rows between them than the twelve bars counted.</summary>
    [Theory]
    [InlineData(-1, false)]     // an hour before the window opens
    [InlineData(0, false)]      // EXACTLY `after` — the open end
    [InlineData(1, true)]
    [InlineData(84, true)]      // mid-window
    [InlineData(167, true)]
    [InlineData(168, true)]     // EXACTLY `before` — the closed end
    [InlineData(169, false)]    // an hour after it closes
    public void AddedWindowIsHalfOpenOnTheLowerEnd(int offsetHours, bool expected)
    {
        var song = Song(added: WindowStart.AddHours(offsetHours));
        var filter = TrackFilterState.Default.WithAddedWindow(AfterMs, BeforeMs);

        Assert.Equal(expected, TrackFilterModel.Matches(song, "", filter, false, false, DateTimeOffset.UtcNow));
    }

    /// <summary>A row with no saved date can never satisfy a window — the bar that produced the lens counted stamped
    /// likes only, so an undated row was never part of the number that was clicked. With the window off it is back in.</summary>
    [Fact]
    public void AWindowNeverAdmitsAnUndatedRow()
    {
        var song = Song(added: null);

        Assert.False(TrackFilterModel.Matches(song, "", TrackFilterState.Default.WithAddedWindow(AfterMs, BeforeMs),
                                              false, false, DateTimeOffset.UtcNow));
        Assert.True(TrackFilterModel.Matches(song, "", TrackFilterState.Default, false, false, DateTimeOffset.UtcNow));
    }

    /// <summary>Either endpoint alone is a valid open-ended half.</summary>
    [Theory]
    [InlineData(true, false, -24, false)]
    [InlineData(true, false, 24, true)]
    [InlineData(false, true, 24, true)]
    [InlineData(false, true, 240, false)]
    public void AWindowMayBeOpenOnOneSide(bool hasAfter, bool hasBefore, int offsetHours, bool expected)
    {
        var song = Song(added: WindowStart.AddHours(offsetHours));
        var filter = TrackFilterState.Default.WithAddedWindow(hasAfter ? AfterMs : 0L, hasBefore ? BeforeMs : 0L);

        Assert.Equal(expected, TrackFilterModel.Matches(song, "", filter, false, false, DateTimeOffset.UtcNow));
    }

    /// <summary>The window and the coarse preset answer the same question, so setting one retires the other. Left
    /// ANDed together they would quietly return fewer rows than either lens promised.</summary>
    [Fact]
    public void TheWindowAndTheCoarsePresetAreOneFacet()
    {
        var preset = TrackFilterState.Default.WithAddedRange(TrackAddedRange.LastSevenDays);
        Assert.Equal(0L, preset.AddedAfterMs);
        Assert.Equal(0L, preset.AddedBeforeMs);

        var window = preset.WithAddedWindow(AfterMs, BeforeMs);
        Assert.Equal(TrackAddedRange.Any, window.Added);
        Assert.Equal(AfterMs, window.AddedAfterMs);

        Assert.Equal(TrackAddedRange.LastYear, window.WithAddedRange(TrackAddedRange.LastYear).Added);
        Assert.Equal(0L, window.WithAddedRange(TrackAddedRange.LastYear).AddedAfterMs);
    }

    static Track Credited(params ArtistRef[] artists)
        => new("1", "spotify:track:1", "Blue Monday", artists, new AlbumRef("b", "spotify:album:b", "Power"),
               450_000, false, null, null);

    /// <summary>The artist lens is EXACT, and it counts every credit rather than only the lead — a feature credit is a
    /// real reason a track is in the library, and the fact that produced the lens counted it the same way.</summary>
    [Theory]
    [InlineData("spotify:artist:a", true)]
    [InlineData("spotify:artist:b", true)]
    [InlineData("spotify:artist:c", false)]
    public void TheArtistLensMatchesAnyCredit(string lens, bool expected)
    {
        var song = Credited(new ArtistRef("a", "spotify:artist:a", "New Order"),
                            new ArtistRef("b", "spotify:artist:b", "Guest"));
        var filter = TrackFilterState.Default.WithArtist(lens);

        Assert.Equal(expected, TrackFilterModel.Matches(song, "", filter, false, false, DateTimeOffset.UtcNow));
    }

    /// <summary>Name is the LAST rung of the identity ladder and only reachable when the credit carries no identifier
    /// at all. Otherwise two different artists who happen to share a display name would collapse into one lens.</summary>
    [Fact]
    public void TheArtistLensFallsBackToNameOnlyForAnUnidentifiedCredit()
    {
        var nameless = Credited(new ArtistRef("", "", "Nameless"));
        var identified = Credited(new ArtistRef("a", "spotify:artist:a", "Nameless"));

        Assert.True(TrackFilterModel.Matches(nameless, "", TrackFilterState.Default.WithArtist("Nameless"),
                                             false, false, DateTimeOffset.UtcNow));
        Assert.False(TrackFilterModel.Matches(identified, "", TrackFilterState.Default.WithArtist("Nameless"),
                                              false, false, DateTimeOffset.UtcNow));
    }

    /// <summary>The display name travels with the id and is dropped with it, so a stale name can never outlive the
    /// filter it described.</summary>
    [Fact]
    public void ClearingTheArtistLensDropsItsDisplayName()
    {
        var lens = TrackFilterState.Default.WithArtist("spotify:artist:a", "New Order");
        Assert.Equal("New Order", lens.ArtistName);

        var cleared = lens.WithArtist(null);
        Assert.Null(cleared.ArtistId);
        Assert.Null(cleared.ArtistName);
        Assert.True(cleared.IsDefault);
    }

    /// <summary>What the Filter affordance's badge counts. A window is ONE facet however many endpoints it names, and
    /// a display name on its own is inert — it filters nothing, so it must not put a number on the funnel.</summary>
    [Theory]
    [InlineData(false, false, false, false, 0)]
    [InlineData(true, false, false, false, 1)]
    [InlineData(false, true, false, false, 1)]   // one endpoint is still one window
    [InlineData(false, false, true, false, 1)]
    [InlineData(false, false, false, true, 0)]   // a name with no id
    [InlineData(true, true, true, false, 2)]     // window + artist
    public void LensFacetsEachCountOnce(bool bothEnds, bool oneEnd, bool artist, bool nameOnly, int expected)
    {
        var filter = TrackFilterState.Default;
        if (bothEnds) filter = filter.WithAddedWindow(AfterMs, BeforeMs);
        if (oneEnd) filter = filter.WithAddedWindow(AfterMs, 0L);
        if (artist) filter = filter.WithArtist("spotify:artist:a", "New Order");
        if (nameOnly) filter = filter with { ArtistName = "New Order" };

        Assert.Equal(expected, filter.ActiveCount);
    }

    [Fact]
    public void ReleaseYearWindowIsInclusiveAndIgnoresUnknownYears()
    {
        var song = Song();
        var dated = new Track("1", "spotify:track:1", "Blue Monday",
            [new ArtistRef("a", "spotify:artist:a", "New Order")],
            new AlbumRef("b", "spotify:album:b", "Power, Corruption & Lies"),
            450_000, false, null, Year: 1983);
        var filter = TrackFilterState.Default.WithReleaseYear(1980, 1989);

        Assert.False(TrackFilterModel.Matches(song, "", filter, false, false, DateTimeOffset.UtcNow));
        Assert.True(TrackFilterModel.Matches(dated, "", filter, false, false, DateTimeOffset.UtcNow));
        Assert.Equal(1, filter.ActiveCount);
        Assert.Equal(0, filter.WithReleaseYear(0, 0).ActiveCount);
    }
}
