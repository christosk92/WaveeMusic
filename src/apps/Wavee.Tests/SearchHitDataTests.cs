// ── Wavee.Tests/SearchHitDataTests.cs — the search hit row's pure decisions (shared media surface, wave 2b) ──────────
//
// `Search.HitData` (Entities/Search.UI.cs) is the ONE adapter behind every search row: the All tab's list, Best matches,
// the Songs grid and the Artists / Podcasts / Episodes / Profiles lists all hand its CardData to `Controls.Surface`. The
// element tree and the table reads need the engine; what the row DECIDES does not, and lives in `SearchHitRules`:
// the surface shape (the large flag), whether the art shows, the "Lyrics match" eyebrow, the trailing control a kind
// carries and which kinds drag. People being circular is `Search.RoundArt`'s rule, pinned here against the kinds the
// adapter feeds it.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SearchHitDataTests
{
    // ── the surface shape ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASmallRow_IsTheRowShapeAt48InTheStandard64Floor()
    {
        var shape = SearchHitRules.ShapeOf(large: false, artHidden: false);

        Assert.Equal(Shape.Row(48f) with { Menu = MenuPlacement.TrailingLane }, shape);
        Assert.Equal(48f, shape.ArtEdge);
        Assert.Equal(64f, shape.MinHeight);                 // the standard row floor (Shape.RowFloorFor)
        Assert.Equal(1, shape.TitleLines);
        Assert.Equal(30f, shape.Fab);
        Assert.Equal(PlateKind.ListRow, shape.Plate);       // transparent at rest, the subtle ladder on hover and press
        Assert.False(shape.IsLargeRow);
    }

    [Fact]
    public void TheLargeFlag_IsTheRowLargePreset()
    {
        var shape = SearchHitRules.ShapeOf(large: true, artHidden: false);

        Assert.Equal(Shape.RowLarge with { Menu = MenuPlacement.TrailingLane }, shape);
        Assert.Equal(84f, shape.ArtEdge);
        Assert.Equal(112f, shape.MinHeight);
        Assert.Equal(44f, shape.Fab);
        Assert.True(shape.IsLargeRow);                      // the page-hero title rung
    }

    [Fact]
    public void ALargeRow_IgnoresHiddenArtwork()
    {
        // Only a TRACK hides its artwork and a track is never the large lead row; were it ever one, the lead keeps its
        // 84 square rather than collapsing to the play glyph's.
        Assert.Equal(SearchHitRules.ShapeOf(large: true, artHidden: false), SearchHitRules.ShapeOf(large: true, artHidden: true));
    }

    // ── where the "…" goes ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void EveryRow_PutsItsMenuButtonInALaneOfItsOwn(bool large, bool artHidden)
    {
        // Beside the heart / Follow pill, never the overlay over the row's end (it cut "Follow" to "Fol" and hid the heart).
        var shape = SearchHitRules.ShapeOf(large, artHidden);
        Assert.Equal(MenuPlacement.TrailingLane, shape.Menu);
        Assert.True(SurfaceRules.MenuReservesWidth(shape, hasMenu: true, showMenu: true));
    }

    [Theory]
    [InlineData(EntityKind.Track)]
    [InlineData(EntityKind.Album)]
    [InlineData(EntityKind.Artist)]
    [InlineData(EntityKind.Playlist)]
    [InlineData(EntityKind.Show)]
    [InlineData(EntityKind.Episode)]
    [InlineData(EntityKind.User)]
    [InlineData(EntityKind.Unknown)]
    public void EveryKindWithAMenu_EndsInAControl_WhichIsWhyTheMenuNeedsItsOwnLane(EntityKind kind)
    {
        // The overlay "…" lands on the row's end; on search that end is ALWAYS a control when there is a menu at all.
        if (Search.HasMenu(kind))
            Assert.NotEqual(SearchHitRules.Trailing.None, SearchHitRules.TrailingOf(kind, hasUri: true));
    }

    [Fact]
    public void AHiddenArtworkRow_HoldsThePlayGlyphSquare_InTheSame64Floor()
    {
        var shape = SearchHitRules.ShapeOf(large: false, artHidden: true);

        Assert.Equal(TrackRowRules.NoArtworkEdge, shape.ArtEdge);
        Assert.Equal(32f, shape.ArtEdge);
        Assert.Equal(64f, shape.MinHeight);                 // the floor does not shrink with the art: every row aligns
        Assert.Equal(30f, shape.Fab);
        Assert.False(shape.IsLargeRow);
    }

    // ── whether the art shows ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EntityKind.Track, false, true)]
    [InlineData(EntityKind.Track, true, false)]             // the one kind the "hide track artwork" setting reaches
    [InlineData(EntityKind.Album, true, true)]
    [InlineData(EntityKind.Artist, true, true)]
    [InlineData(EntityKind.Playlist, true, true)]
    [InlineData(EntityKind.Show, true, true)]
    [InlineData(EntityKind.Episode, true, true)]
    [InlineData(EntityKind.User, true, true)]
    public void OnlyATrackHitHidesItsArtwork(EntityKind kind, bool hideTrackArt, bool shows)
        => Assert.Equal(shows, SearchHitRules.ShowsArt(kind, hideTrackArt));

    // ── the eyebrow ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, SearchHitFlags.MatchedLyrics, true)]
    [InlineData(false, SearchHitFlags.MatchedLyrics | SearchHitFlags.VideoMedia, true)]
    [InlineData(false, SearchHitFlags.MatchedLyrics | SearchHitFlags.MatchedTitle, true)]
    [InlineData(false, SearchHitFlags.None, false)]
    [InlineData(false, SearchHitFlags.MatchedTitle, false)]  // a title match is the hero's chip, never a row's eyebrow
    [InlineData(false, SearchHitFlags.VideoMedia, false)]
    [InlineData(true, SearchHitFlags.MatchedLyrics, false)]  // the large lead row leaves it to the Top Result's chip
    [InlineData(true, SearchHitFlags.None, false)]
    public void TheLyricsEyebrow_SitsOnASmallRowThatMatchedLyrics(bool large, SearchHitFlags flags, bool eyebrow)
        => Assert.Equal(eyebrow, SearchHitRules.LyricsEyebrow(large, flags));

    // ── the trailing control ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EntityKind.Artist, SearchHitRules.Trailing.Follow)]
    [InlineData(EntityKind.Playlist, SearchHitRules.Trailing.Follow)]
    [InlineData(EntityKind.Show, SearchHitRules.Trailing.Follow)]
    [InlineData(EntityKind.Track, SearchHitRules.Trailing.Save)]
    [InlineData(EntityKind.Album, SearchHitRules.Trailing.Save)]
    [InlineData(EntityKind.Episode, SearchHitRules.Trailing.None)]
    [InlineData(EntityKind.User, SearchHitRules.Trailing.None)]
    [InlineData(EntityKind.Collection, SearchHitRules.Trailing.None)]
    [InlineData(EntityKind.Unknown, SearchHitRules.Trailing.None)]
    public void TheTrailingControlFollowsTheKind(EntityKind kind, SearchHitRules.Trailing expected)
        => Assert.Equal(expected, SearchHitRules.TrailingOf(kind, hasUri: true));

    [Theory]
    [InlineData(EntityKind.Artist)]
    [InlineData(EntityKind.Track)]
    [InlineData(EntityKind.Show)]
    public void AHitWithNoUri_HasNothingToFollowOrSave(EntityKind kind)
        => Assert.Equal(SearchHitRules.Trailing.None, SearchHitRules.TrailingOf(kind, hasUri: false));

    // ── drag ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EntityKind.Track, true)]
    [InlineData(EntityKind.Album, true)]
    [InlineData(EntityKind.Artist, true)]
    [InlineData(EntityKind.Playlist, true)]
    [InlineData(EntityKind.Show, true)]
    [InlineData(EntityKind.Episode, true)]
    [InlineData(EntityKind.Collection, true)]
    [InlineData(EntityKind.User, false)]                    // a person has no Wavee resource behind them
    [InlineData(EntityKind.Unknown, false)]
    public void EveryHitButAProfileIsADragSource(EntityKind kind, bool drags)
        => Assert.Equal(drags, SearchHitRules.Drags(kind));

    // ── people are circles (the adapter's Circular is Search.RoundArt) ──────────────────────────────────────────────

    [Theory]
    [InlineData(EntityKind.Artist, true)]
    [InlineData(EntityKind.User, true)]
    [InlineData(EntityKind.Track, false)]
    [InlineData(EntityKind.Album, false)]
    [InlineData(EntityKind.Playlist, false)]
    [InlineData(EntityKind.Show, false)]
    [InlineData(EntityKind.Episode, false)]
    public void OnlyPeopleAreCircular(EntityKind kind, bool circular)
        => Assert.Equal(circular, Search.RoundArt(kind));

    // ── the rules agree with the gates the adapter reads for play and menu ──────────────────────────────────────────

    [Fact]
    public void AProfileRow_HasNoPlayNoMenuNoDrag_ButStillACircle_AndOpensItsPage()
    {
        // The Profiles facet's row: a circle with a name; what OpenHit does with it is open the profile page (#161),
        // so the top result offers "Open page" — and still no Play, no menu, no drag, no Follow trailing.
        Assert.True(Search.CanOpen(EntityKind.User));
        Assert.False(Search.CanPlay(EntityKind.User));
        Assert.False(Search.HasMenu(EntityKind.User));
        Assert.False(SearchHitRules.Drags(EntityKind.User));
        Assert.True(Search.RoundArt(EntityKind.User));
        Assert.Equal(SearchHitRules.Trailing.None, SearchHitRules.TrailingOf(EntityKind.User, hasUri: true));
    }
}
