// ── Wavee.Tests/LibrarySearchRowTests.cs — the library search rows' pure decisions ───────────────────────────────────
//
// The library's search column (artist / album hits and the track hits under a chosen album) is the shared media surface
// now (Classic's `ClassicLibraryPage.SearchRow` / `TrackHitRow` feed `Controls.Surface`). What those adapters DECIDE lives in
// `ClassicLibrarySearchRules` (`Themes\Classic\Structure\Rules\ClassicLibrarySearchRules.cs`, T10c1): the row shape of each kind of hit — the art edge and the floor it renders
// at, not the 64 every other media row has — and which hits wear a circular cover. That is what is pinned here. The element
// tree (the menu, the drag, the highlight, the track row's links) reads the entity tables, so it is the live check's.
//
// No window, no loop, no table is touched.

using Wavee;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests;

public class LibrarySearchRowTests
{
    // ── the artist / album hit ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_name_hit_keeps_the_56_row_it_has_always_had_with_a_40_cover()
    {
        var s = ClassicLibrarySearchRules.NameShape;
        Assert.Equal(40f, s.ArtEdge);
        Assert.Equal(56f, s.MinHeight);
        // The floor is the honest content height (art + 8 + 8): the row never grows past what it states.
        Assert.Equal(s.ArtEdge + 2f * SurfaceGeometry.RowPad, s.MinHeight);
    }

    [Theory]
    [InlineData(EntityKind.Artist, true)]
    [InlineData(EntityKind.Album, false)]
    [InlineData(EntityKind.Show, false)]
    [InlineData(EntityKind.Track, false)]
    public void Only_an_artist_wears_a_circular_cover(EntityKind kind, bool circular)
        => Assert.Equal(circular, ClassicLibrarySearchRules.IsCircular(kind));

    // ── the track hit ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_track_hit_with_artwork_is_a_36_cover_row()
    {
        var s = ClassicLibrarySearchRules.TrackShape(showArtwork: true);
        Assert.Equal(ClassicLibrarySearchRules.TrackArt, s.ArtEdge);
        Assert.Equal(36f, s.ArtEdge);
        Assert.Equal(36f + 2f * SurfaceGeometry.RowPad, s.MinHeight);
    }

    [Fact]
    public void A_track_hit_with_artwork_hidden_shrinks_to_the_play_glyph_square()
    {
        // The hidden-artwork rule is the track row adapter's (`TrackRowRules.ArtEdge`): the cover's square holds a play glyph.
        var s = ClassicLibrarySearchRules.TrackShape(showArtwork: false);
        Assert.Equal(TrackRowRules.ArtEdge(ClassicLibrarySearchRules.TrackArt, showArtwork: false), s.ArtEdge);
        Assert.Equal(TrackRowRules.NoArtworkEdge, s.ArtEdge);
        Assert.Equal(TrackRowRules.NoArtworkEdge + 2f * SurfaceGeometry.RowPad, s.MinHeight);
    }

    [Fact]
    public void Every_hit_row_states_a_floor_under_the_default_media_row_floor()
    {
        // The default row floor is 64; a library hit would otherwise render 8-20 DIP taller than it ever did.
        Assert.True(ClassicLibrarySearchRules.NameShape.MinHeight < Shape.RowFloor);
        Assert.True(ClassicLibrarySearchRules.TrackShape(true).MinHeight < Shape.RowFloor);
        Assert.True(ClassicLibrarySearchRules.TrackShape(false).MinHeight < Shape.RowFloor);
    }

    [Fact]
    public void Every_hit_row_is_a_list_row_with_a_trailing_menu_slot()
    {
        // The shape inherits the shared row: the list-row plate (the browse-list hover) and the hot-revealed trailing "…".
        foreach (var s in new[]
                 {
                     ClassicLibrarySearchRules.NameShape, ClassicLibrarySearchRules.TrackShape(true), ClassicLibrarySearchRules.TrackShape(false),
                 })
        {
            Assert.True(s.IsRow);
            Assert.Equal(PlateKind.ListRow, s.Plate);
            Assert.Equal(MenuPlacement.Trailing, s.Menu);
        }
    }
}
