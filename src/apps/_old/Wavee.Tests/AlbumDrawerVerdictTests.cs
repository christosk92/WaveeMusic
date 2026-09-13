using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class AlbumDrawerVerdictTests
{
    [Fact]
    public void LoadedOtherAlbum_IsLoading_NotStale()
    {
        // Resource still holds album A (20 tracks) while the user has already clicked album B (advertised 5 on its
        // thin card). C1: identity must win — B is "loading", never A's stale 20-row list.
        var v = AlbumDrawerVerdict.For(
            selectedUri: "spotify:album:B", loadedUri: "spotify:album:A", loadedTracks: 20,
            thinTracks: 0, thinTrackCount: 5, pending: true, gridCols: 3);

        Assert.True(v.Loading);
        Assert.Equal(5, v.Shown);
        Assert.NotEqual(20, v.Shown);
    }

    [Fact]
    public void ThinRowsWithPendingFetch_StillPlaceholder()
    {
        // The discography card already carries 8 thin tracks, but the full-detail fetch for THIS uri is still
        // pending: it must still read as Loading (a placeholder), just sized from what's already on hand.
        var v = AlbumDrawerVerdict.For(
            selectedUri: "spotify:album:C", loadedUri: null, loadedTracks: 0,
            thinTracks: 8, thinTrackCount: 8, pending: true, gridCols: 3);

        Assert.True(v.Loading);
        Assert.Equal(8, v.Shown);
        Assert.Equal(8, v.Rows);
    }

    [Fact]
    public void Match_Ready_Rows()
    {
        // Loaded album IS the selected one and the fetch has settled: rows reflect the real track count.
        var v = AlbumDrawerVerdict.For(
            selectedUri: "spotify:album:D", loadedUri: "spotify:album:D", loadedTracks: 6,
            thinTracks: 0, thinTrackCount: 0, pending: false, gridCols: 3);

        Assert.False(v.Loading);
        Assert.False(v.ShowAllRow);
        Assert.Equal(6, v.Shown);
        Assert.Equal(6, v.Rows);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(6, 2)]
    public void Columns_2_At5GridCols_1_Below(int gridCols, int expected)
        => Assert.Equal(expected, AlbumDrawerVerdict.ColumnsFor(gridCols));

    [Fact]
    public void Cap_And_ShowAllRow()
    {
        // 13-track album, one column: caps at 12 shown + a "Show all" row, so Rows counts all 13 slots.
        var oneColumn = AlbumDrawerVerdict.For(
            selectedUri: "spotify:album:E", loadedUri: "spotify:album:E", loadedTracks: 13,
            thinTracks: 0, thinTrackCount: 0, pending: false, gridCols: 3);

        Assert.Equal(12, oneColumn.Shown);
        Assert.True(oneColumn.ShowAllRow);
        Assert.Equal(13, oneColumn.Rows);
        Assert.Equal(456f, oneColumn.PanelHeight);
        Assert.Equal(472f, oneColumn.SlotHeight);

        // Same album, two columns (wide grid): 24-row cap easily fits 13, so no "Show all" and half the row count.
        var twoColumn = AlbumDrawerVerdict.For(
            selectedUri: "spotify:album:E", loadedUri: "spotify:album:E", loadedTracks: 13,
            thinTracks: 0, thinTrackCount: 0, pending: false, gridCols: 5);

        Assert.Equal(13, twoColumn.Shown);
        Assert.False(twoColumn.ShowAllRow);
        Assert.Equal(7, twoColumn.Rows);
    }

    [Fact]
    public void Heights_HeaderPlusRowsPlusGap()
    {
        // 4 tracks, one column ⇒ 4 rows; heights are a pure function of Rows so the panel and the reserved slot
        // (panel + the caret's TopGap + BottomGap) can never disagree with what the panel actually renders.
        var v = AlbumDrawerVerdict.For(
            selectedUri: "spotify:album:H", loadedUri: "spotify:album:H", loadedTracks: 4,
            thinTracks: 0, thinTrackCount: 0, pending: false, gridCols: 3);

        Assert.Equal(4, v.Rows);
        Assert.Equal(AlbumDrawerVerdict.HeaderH + 4 * AlbumDrawerVerdict.RowPitch, v.PanelHeight);
        Assert.Equal(v.PanelHeight + AlbumDrawerVerdict.TopGap + AlbumDrawerVerdict.BottomGap, v.SlotHeight);
        Assert.Equal(168f, v.PanelHeight);
        Assert.Equal(184f, v.SlotHeight);
    }

    [Fact]
    public void Slot_ReservesCaretRoomAboveThePanel()
    {
        // The reserved slot is taller than the panel by exactly TopGap + BottomGap — the caret band above the panel
        // (room for the "this card opened" wedge) plus the ordinary gap to the next card row — for any verdict.
        var v = AlbumDrawerVerdict.For(
            selectedUri: "spotify:album:I", loadedUri: "spotify:album:I", loadedTracks: 4,
            thinTracks: 0, thinTrackCount: 0, pending: false, gridCols: 3);

        Assert.Equal(AlbumDrawerVerdict.TopGap + AlbumDrawerVerdict.BottomGap, v.SlotHeight - v.PanelHeight);
    }

    [Fact]
    public void ReadyEmpty_TwoRows()
    {
        // Matched, settled, and genuinely empty (a single with zero tracks, say) — a fixed 2-row "no tracks" state,
        // not zero rows and not the shimmer placeholder.
        var v = AlbumDrawerVerdict.For(
            selectedUri: "spotify:album:F", loadedUri: "spotify:album:F", loadedTracks: 0,
            thinTracks: 0, thinTrackCount: 0, pending: false, gridCols: 3);

        Assert.True(v.ReadyEmpty);
        Assert.False(v.Loading);
        Assert.Equal(2, v.Rows);
    }

    [Fact]
    public void Closed_IsDefault()
    {
        var v = AlbumDrawerVerdict.For(
            selectedUri: "", loadedUri: null, loadedTracks: 0,
            thinTracks: 0, thinTrackCount: 0, pending: false, gridCols: 3);

        Assert.Equal(default, v);
    }

    [Fact]
    public void PlaceholderRows_FallbackThree_WhenCountUnknown()
    {
        // Nothing loaded yet, no thin tracks, no advertised count: the shimmer placeholder falls back to a fixed 3
        // rows rather than guessing or showing zero.
        var v = AlbumDrawerVerdict.For(
            selectedUri: "spotify:album:G", loadedUri: null, loadedTracks: 0,
            thinTracks: 0, thinTrackCount: 0, pending: true, gridCols: 3);

        Assert.True(v.Loading);
        Assert.Equal(AlbumDrawerVerdict.FallbackShimmerRows, v.Shown);
        Assert.Equal(3, v.Shown);
    }
}
