// ── Wavee.Tests/TrackFilterModelTests.cs — the track-list filter predicate and its facet bookkeeping ────────────────
//
// Wave 4.5's gate for `Track.FilterState` / `Track.FilterRow` / `Track.FilterModel` (Entities/Track.Rules.cs), ported
// VERBATIM from 0.2.9's TrackFilterModelTests (251 lines). The call shape is the only change: 0.2.9 read a `Track`
// record plus `hasVideo` / `isSaved` / a `DateTimeOffset`; 0.3 reads one resolved `FilterRow` (video and saved ride on
// it) and unix SECONDS. The exact-artist lens is a SLOT, not a uri string, so 0.2.9's "name is the last rung for a credit
// with no identifier" fact has no 0.3 shape (every credit is a slot); its surviving half — two artists sharing a name
// never collapse into one lens — is kept.
//
// The last two facts build a row from a REAL handle through `FilterRow.Of`, which is the half 0.2.9 never had to test:
// the audio values read as unknown until `Knows(Audio)`, and an unruled row is never hidden by PlayableOnly.

using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class TrackFilterModelTests
{
    static readonly long Now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    static int Seconds(DateTimeOffset at) => (int)at.ToUnixTimeSeconds();

    /// <summary>0.2.9's <c>Song(...)</c>: a playable streamed track with one credit. <paramref name="unavailable"/> is a
    /// RULED verdict (0.2.9's <c>Availability.Unavailable</c>) with no release instant.</summary>
    static Track.FilterRow Song(
        string title = "Blue Monday", string artist = "New Order", string album = "Power, Corruption & Lies",
        int duration = 450_000, bool explicitTrack = false, bool local = false, bool unavailable = false,
        DateTimeOffset? added = null, bool hasVideo = false, bool saved = false, int year = 0, int[]? credits = null)
        => new()
        {
            Title = title,
            ArtistLine = artist,
            AlbumTitle = album,
            DurationMs = duration,
            Explicit = explicitTrack,
            Local = local,
            NotYetOut = Track.NotYetOutOf(availabilityKnown: true, unavailable, availableAt: 0, Now),
            Unplayable = Track.Unplayable(availabilityKnown: true, unavailable, availableAt: 0),
            AddedAt = added is { } at ? Seconds(at) : 0,
            HasVideo = hasVideo,
            Saved = saved,
            Year = year,
            ArtistSlots = credits ?? [1],
        };

    static bool Matches(in Track.FilterRow row, string query, in Track.FilterState filter, long now)
        => Track.FilterModel.Matches(in row, query, in filter, now);

    [Fact]
    public void SearchScope_UsesOnlySelectedMetadata()
    {
        var song = Song();
        var title = new Track.FilterState(SearchScope: Track.SearchScope.Title);
        var artist = new Track.FilterState(SearchScope: Track.SearchScope.Artist);

        Assert.False(Matches(song, "New Order", title, Now));
        Assert.True(Matches(song, "New Order", artist, Now));
    }

    [Fact]
    public void AlbumTrack_SupportsDurationAndAvailabilityFacets()
    {
        var song = Song(unavailable: true);
        var longOnly = new Track.FilterState(Duration: Track.DurationRange.OverFiveMinutes);
        var playable = new Track.FilterState(Flags: Track.FilterFlags.PlayableOnly);

        Assert.True(Matches(song, "", longOnly, Now));
        Assert.False(Matches(song, "", playable, Now));
    }

    [Fact]
    public void PlayableOnly_HidesAnUnplayableRowOnItsOwnBit()
    {
        // A row ruled unavailable with NO release instant (a terminal envelope verdict) is hidden by PlayableOnly even
        // when nothing marked it not-yet-out: the predicate reads `Unplayable` itself, not a derived flag. Every other
        // facet still sees the row — the Liked Songs table with the filter off lists it dimmed, not vanished.
        var withdrawn = new Track.FilterRow
        {
            Title = "Withdrawn", ArtistLine = "Nobody", DurationMs = 200_000, ArtistSlots = [1],
            NotYetOut = false, Unplayable = true,
        };
        Assert.False(Matches(withdrawn, "", new Track.FilterState(Flags: Track.FilterFlags.PlayableOnly), Now));
        Assert.True(Matches(withdrawn, "", Track.FilterState.Default, Now));
        Assert.True(Matches(withdrawn, "withdrawn", Track.FilterState.Default, Now));
    }

    [Fact]
    public void PlaylistTrack_SupportsDateAndTraitModes()
    {
        var now = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        // hasVideo rides on the row: the caller resolves it from the video planes, never from a text field.
        var song = Song(explicitTrack: true, added: now.AddDays(-3), hasVideo: true);
        var filter = new Track.FilterState(
            ExplicitMode: Track.TraitMode.Hide,
            VideoMode: Track.TraitMode.Only,
            Added: Track.AddedRange.LastSevenDays);

        Assert.False(Matches(song, "", filter, now.ToUnixTimeSeconds()));
        Assert.Equal(3, filter.ActiveCount);
    }

    [Theory]
    [InlineData(Track.TraitMode.All, false, true)]
    [InlineData(Track.TraitMode.All, true, true)]
    [InlineData(Track.TraitMode.Hide, false, true)]
    [InlineData(Track.TraitMode.Hide, true, false)]
    [InlineData(Track.TraitMode.Only, false, false)]
    [InlineData(Track.TraitMode.Only, true, true)]
    public void ExplicitTraitMode_ImplementsAllHideOnly(Track.TraitMode mode, bool isExplicit, bool expected)
    {
        var song = Song(explicitTrack: isExplicit);
        var filter = new Track.FilterState(ExplicitMode: mode);

        Assert.Equal(expected, Matches(song, "", filter, Now));
    }

    [Theory]
    [InlineData(Track.TraitMode.All, false, true)]
    [InlineData(Track.TraitMode.All, true, true)]
    [InlineData(Track.TraitMode.Hide, false, true)]
    [InlineData(Track.TraitMode.Hide, true, false)]
    [InlineData(Track.TraitMode.Only, false, false)]
    [InlineData(Track.TraitMode.Only, true, true)]
    public void VideoTraitMode_ImplementsAllHideOnly(Track.TraitMode mode, bool hasVideo, bool expected)
    {
        var song = Song(hasVideo: hasVideo);
        var filter = new Track.FilterState(VideoMode: mode);

        Assert.Equal(expected, Matches(song, "", filter, Now));
    }

    [Fact]
    public void OriginAndLikedFacetsCompose()
    {
        var filter = new Track.FilterState(
            Flags: Track.FilterFlags.LikedOnly,
            Origin: Track.OriginFilter.Local);

        Assert.True(Matches(Song(local: true, saved: true), "", filter, Now));
        Assert.False(Matches(Song(local: true, saved: false), "", filter, Now));
    }

    // ── The Liked rail's lenses: an arbitrary saved-date window, and an exact artist ────────────────────────────────

    /// <summary>Anchored on a fixed instant so the rows below are dates, not arithmetic against "now".</summary>
    static readonly DateTimeOffset WindowStart = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    static long AfterMs => WindowStart.ToUnixTimeMilliseconds();
    static long BeforeMs => WindowStart.AddDays(7).ToUnixTimeMilliseconds();

    /// <summary>Half-open, as <c>(after, before]</c>: the rolling week buckets are laid end to end, so a like on the seam
    /// has to belong to exactly one of them.</summary>
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
        var filter = Track.FilterState.Default.WithAddedWindow(AfterMs, BeforeMs);

        Assert.Equal(expected, Matches(song, "", filter, Now));
    }

    /// <summary>A row with no saved date can never satisfy a window; with the window off it is back in.</summary>
    [Fact]
    public void AWindowNeverAdmitsAnUndatedRow()
    {
        var song = Song(added: null);

        Assert.False(Matches(song, "", Track.FilterState.Default.WithAddedWindow(AfterMs, BeforeMs), Now));
        Assert.True(Matches(song, "", Track.FilterState.Default, Now));
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
        var filter = Track.FilterState.Default.WithAddedWindow(hasAfter ? AfterMs : 0L, hasBefore ? BeforeMs : 0L);

        Assert.Equal(expected, Matches(song, "", filter, Now));
    }

    /// <summary>The window and the coarse preset answer the same question, so setting one retires the other.</summary>
    [Fact]
    public void TheWindowAndTheCoarsePresetAreOneFacet()
    {
        var preset = Track.FilterState.Default.WithAddedRange(Track.AddedRange.LastSevenDays);
        Assert.Equal(0L, preset.AddedAfterMs);
        Assert.Equal(0L, preset.AddedBeforeMs);

        var window = preset.WithAddedWindow(AfterMs, BeforeMs);
        Assert.Equal(Track.AddedRange.Any, window.Added);
        Assert.Equal(AfterMs, window.AddedAfterMs);

        Assert.Equal(Track.AddedRange.LastYear, window.WithAddedRange(Track.AddedRange.LastYear).Added);
        Assert.Equal(0L, window.WithAddedRange(Track.AddedRange.LastYear).AddedAfterMs);
    }

    /// <summary>The artist lens is EXACT, and it counts every credit rather than only the lead.</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void TheArtistLensMatchesAnyCredit(int lens, bool expected)
    {
        var song = Song(credits: [1, 2]);                             // New Order, plus a guest credit
        var filter = Track.FilterState.Default.WithArtist(lens);

        Assert.Equal(expected, Matches(song, "", filter, Now));
    }

    /// <summary>The lens IS the slot: two different artists who happen to share a display name never collapse into one
    /// lens (0.2.9 needed an id-before-name ladder for this; a slot has no name rung to fall to).</summary>
    [Fact]
    public void TheArtistLensNeverCollapsesTwoArtistsWhoShareADisplayName()
    {
        var identified = Song(credits: [1]);                          // "Nameless", slot 1

        Assert.False(Matches(identified, "", Track.FilterState.Default.WithArtist(2, "Nameless"), Now));
        Assert.True(Matches(identified, "", Track.FilterState.Default.WithArtist(1, "Nameless"), Now));
    }

    /// <summary>The display name travels with the slot and is dropped with it.</summary>
    [Fact]
    public void ClearingTheArtistLensDropsItsDisplayName()
    {
        var lens = Track.FilterState.Default.WithArtist(7, "New Order");
        Assert.Equal("New Order", lens.ArtistName);

        var cleared = lens.WithArtist(0);
        Assert.Equal(0, cleared.ArtistSlot);
        Assert.Null(cleared.ArtistName);
        Assert.True(cleared.IsDefault);
    }

    /// <summary>A window is ONE facet however many endpoints it names, and a display name on its own is inert.</summary>
    [Theory]
    [InlineData(false, false, false, false, 0)]
    [InlineData(true, false, false, false, 1)]
    [InlineData(false, true, false, false, 1)]   // one endpoint is still one window
    [InlineData(false, false, true, false, 1)]
    [InlineData(false, false, false, true, 0)]   // a name with no slot
    [InlineData(true, true, true, false, 2)]     // window + artist
    public void LensFacetsEachCountOnce(bool bothEnds, bool oneEnd, bool artist, bool nameOnly, int expected)
    {
        var filter = Track.FilterState.Default;
        if (bothEnds) filter = filter.WithAddedWindow(AfterMs, BeforeMs);
        if (oneEnd) filter = filter.WithAddedWindow(AfterMs, 0L);
        if (artist) filter = filter.WithArtist(7, "New Order");
        if (nameOnly) filter = filter with { ArtistName = "New Order" };

        Assert.Equal(expected, filter.ActiveCount);
    }

    [Fact]
    public void ReleaseYearWindowIsInclusiveAndIgnoresUnknownYears()
    {
        var song = Song();
        var dated = Song(year: 1983);
        var filter = Track.FilterState.Default.WithReleaseYear(1980, 1989);

        Assert.False(Matches(song, "", filter, Now));
        Assert.True(Matches(dated, "", filter, Now));
        Assert.Equal(1, filter.ActiveCount);
        Assert.Equal(0, filter.WithReleaseYear(0, 0).ActiveCount);
    }

    // ── FilterRow.Of over a real handle ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FilterRow_Of_resolves_the_committed_columns_and_decides_not_yet_out_against_the_injected_clock()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Tracks.Add();
        row.Id = s.Text("spotify:track:filterof");
        row.Title = s.Text("Blue Monday");
        row.ArtistLine = s.Text("New Order");
        row.DurationMs = 450_000;
        row.Flags = (uint)(TrackFlags.Explicit | TrackFlags.Unavailable);
        row.AvailableAt = (int)(Now + 86_400);                        // announced for tomorrow
        row.Tempo = 1284;
        row.Camelot = 22;
        row.Known = (uint)(TrackFields.Identity | TrackFields.Availability | TrackFields.Audio);
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse("spotify:track:filterof"));
        var f = Track.FilterRow.Of(track, addedAt: Seconds(WindowStart), saved: true, Now);

        Assert.Equal("Blue Monday", f.Title);
        Assert.Equal("New Order", f.ArtistLine);
        Assert.True(f.Explicit);
        Assert.True(f.Saved);
        Assert.Equal(Seconds(WindowStart), f.AddedAt);
        Assert.Equal(450_000, f.DurationMs);
        Assert.Equal(128.4, f.TempoBpm);
        Assert.Equal((byte)22, f.Camelot);
        Assert.True(f.NotYetOut);                                     // ruled unavailable, release instant still ahead
        Assert.False(f.Unplayable);                                   // it names WHEN, so it is pending — not withdrawn
        Assert.True(Matches(f, "new order", Track.FilterState.Default with { Tempo = Track.TempoBand.From120To139 }, Now));
        Assert.False(Matches(f, "", new Track.FilterState(Flags: Track.FilterFlags.PlayableOnly), Now));

        // The release instant passes: the same verdict no longer hides the row (the release drop heals itself).
        var later = Track.FilterRow.Of(track, 0, false, Now + 2 * 86_400);
        Assert.False(later.NotYetOut);
        Assert.True(Matches(later, "", new Track.FilterState(Flags: Track.FilterFlags.PlayableOnly), Now + 2 * 86_400));
    }

    [Fact]
    public void FilterRow_Of_an_unruled_unenriched_row_is_never_hidden_and_never_swept_into_a_band()
    {
        TestScope.Fresh();
        var t = Entities.Current.Tracks;
        var track = Entities.Track(EntityUri.Parse("spotify:track:cold"));
        // A stray write with no Known bit behind it: neither the tempo nor the flag is an answer.
        t.Tempo[track.Slot] = 1284;
        t.Flags[track.Slot] |= (uint)TrackFlags.Unavailable;

        var f = Track.FilterRow.Of(track, 0, false, Now);

        Assert.Equal(0d, f.TempoBpm);
        Assert.False(f.NotYetOut);
        Assert.False(f.Unplayable);
        Assert.True(Matches(f, "", new Track.FilterState(Flags: Track.FilterFlags.PlayableOnly), Now));
        Assert.False(Matches(f, "", Track.FilterState.Default with { Tempo = Track.TempoBand.From120To139 }, Now));
        Assert.True(Matches(f, "", Track.FilterState.Default, Now));
    }
}
