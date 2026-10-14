// ── Wavee.Tests/AlbumSurfaceRulesTests.cs — the album page's and library pane's media-surface geometry ───────────────
//
// The album page and the library pane stopped hand-rolling their cards: the related-album rows and the music-video
// shelf cells are `Controls.Surface(…, Shape.RowTile / Shape.Video)`, the pane's "Also by" tiles are
// `Controls.Surface(…, Shape.Shelf())`, and the trailing skeleton's rows are the same shape's SEED face. What is left to
// DECIDE on those two files is a handful of numbers, and they are pinned here against the shared surface's own presets
// and extents, so a preset moving (a row floor, the Video FAB, the shelf chrome) shows up as a failing fact instead of a
// skeleton that no longer matches the rows or a strip that clips its tiles.
//
// No window, no loop, no element: Classic's `ClassicLibraryPaneRules` (the "Also by" strip's numbers, T10c1; they were `Album.SurfaceMetrics`) is engine-free by construction. (T9b: the music-video hero's own numbers moved with its picture to
// Classic's `ClassicAlbumLayout`; `AlbumPlaylistContractTests` pins them. What stays here is the pane's strip.)

using Wavee;
using Xunit;
using AlbumMetrics = Wavee.Themes.Classic.ClassicLibraryPaneRules;
using Rules = Wavee.Album.PageRules;

namespace Wavee.Tests;

public class AlbumSurfaceRulesTests
{
    // ── the trailing skeleton's rows ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SkeletonRow_is_as_tall_as_the_real_related_row()
    {
        // The skeleton draws `Surface(Seed, Shape.RowTile)` and `PageRules.SkeletonHeight` sums `SkelRowH` per row: the
        // two agree only while the real rows' shape floor IS that number (48 art + 2 × 8 padding = the 64 row floor).
        Assert.Equal(Rules.SkelRowH, Shape.RowTile.MinHeight);
        Assert.Equal(Rules.SkelRowH, Shape.RowTile.ArtEdge + 2f * SurfaceGeometry.RowPad);
    }

    // ── the pane's "Also by" strip ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AlsoBy_card_keeps_the_96_cover_inside_the_plate_padding()
    {
        // The surface pads its cover by 8 on every side, so a 96 cover is a 112-wide card.
        Assert.Equal(112f, AlbumMetrics.AlsoByCardW);
        Assert.Equal(AlbumMetrics.AlsoByArt, SurfaceParts.InnerOf(AlbumMetrics.AlsoByCardW));
    }

    [Fact]
    public void AlsoBy_strip_is_the_shelf_extent_of_that_card()
    {
        // A horizontal ScrollEl is a viewport with no content height of its own, so the strip states one: the surface's
        // shelf extent for a square cover with one caption line (the year) — gutter 4 + pad 8 + 96 + gap 8 + title 20 +
        // 2 + caption 16 + pad 8.
        Assert.Equal(162f, AlbumMetrics.AlsoByStripH);
        Assert.Equal(SurfaceGeometry.ShelfHeight(AlbumMetrics.AlsoByCardW, 1f, captionLines: 1, metaLine: false), AlbumMetrics.AlsoByStripH);
        // The tile pins its shell to the strip less the gutter over the plate, so a year-less tile is as tall as its neighbours.
        Assert.Equal(158f, AlbumMetrics.AlsoByCardH);
        Assert.Equal(AlbumMetrics.AlsoByStripH,
            AlbumMetrics.AlsoByCardH + SurfaceGeometry.ShelfGutterTop + SurfaceGeometry.ShelfGutterBottom);
    }
}
