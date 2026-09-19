// ── Wavee.Tests/AlbumDrawerVerdictTests.cs — the artist-page album drawer, decided in one place (ch 05 W19, §8 row 3) ──
//
// Ported VERBATIM from 0.2.9 `Wavee.Tests/AlbumDrawerVerdictTests.cs` (10 facts; the record and its statics are one
// type in 0.3, `Album.DrawerVerdict`). The three `Of` facts at the bottom are new: the verdict read off the album's own
// `AlbumTracks` edge — Unknown is loading, a landed list is its rows, a failed ask is ready-empty (the Retry arm).

using Wavee;
using Xunit;
using AlbumDrawerVerdict = Wavee.Album.DrawerVerdict;

namespace Wavee.Tests;

public class AlbumDrawerVerdictTests
{
    [Fact]
    public void LoadedOtherAlbum_IsLoading_NotStale()
    {
        // Resource still holds album A (20 tracks) while the user has already clicked album B (advertised 5 on its
        // thin card). Identity must win — B is "loading", never A's stale 20-row list.
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
        var v = AlbumDrawerVerdict.For(
            selectedUri: "spotify:album:I", loadedUri: "spotify:album:I", loadedTracks: 4,
            thinTracks: 0, thinTrackCount: 0, pending: false, gridCols: 3);

        Assert.Equal(AlbumDrawerVerdict.TopGap + AlbumDrawerVerdict.BottomGap, v.SlotHeight - v.PanelHeight);
    }

    [Fact]
    public void ReadyEmpty_TwoRows()
    {
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
        var v = AlbumDrawerVerdict.For(
            selectedUri: "spotify:album:G", loadedUri: null, loadedTracks: 0,
            thinTracks: 0, thinTrackCount: 0, pending: true, gridCols: 3);

        Assert.True(v.Loading);
        Assert.Equal(AlbumDrawerVerdict.FallbackShimmerRows, v.Shown);
        Assert.Equal(3, v.Shown);
    }
}

/// <summary>The verdict off the album's own edge (new in 0.3).</summary>
[Collection(EntitiesCollection.Name)]
public class AlbumDrawerVerdictOfTests
{
    static Album Titled(string uri, int trackCount)
    {
        var s = Staging.Rent();
        ref var row = ref s.Albums.RowFor(s.Text(uri), Authority.Full, (uint)AlbumFields.Identity);
        row.Title = s.Text("Drawer");
        row.TrackCount = trackCount;
        TestScope.CommitAndPublish(s);
        return Entities.Album(EntityUri.Parse(uri));
    }

    [Fact]
    public void An_unanswered_list_is_loading_sized_from_the_advertised_count()
    {
        TestScope.Fresh();
        var album = Titled("spotify:album:drawer-unknown", 7);
        var v = AlbumDrawerVerdict.Of(album, gridCols: 3);
        Assert.True(v.Loading);
        Assert.Equal(7, v.Shown);
        Assert.Equal("spotify:album:drawer-unknown", v.Uri);
    }

    [Fact]
    public void A_landed_list_is_its_rows()
    {
        TestScope.Fresh();
        var album = Titled("spotify:album:drawer-landed", 2);
        var tracks = Entities.Current.Tracks;
        Entities.Current.Edges.AlbumTracks.ReplaceRun(album.Slot,
            [tracks.Slot("spotify:track:d1".AsSpan()), tracks.Slot("spotify:track:d2".AsSpan())], default);

        var v = AlbumDrawerVerdict.Of(album, gridCols: 3);
        Assert.False(v.Loading);
        Assert.Equal(2, v.Rows);
        Assert.False(v.ReadyEmpty);
    }

    [Fact]
    public void A_failed_ask_is_ready_empty_the_retry_arm()
    {
        TestScope.Fresh();
        var album = Titled("spotify:album:drawer-failed", 9);
        Entities.Current.Edges.AlbumTracks.MarkFailed(album.Slot, 0, 503);

        var v = AlbumDrawerVerdict.Of(album, gridCols: 3);
        Assert.True(v.ReadyEmpty);
        Assert.False(v.Loading);
        Assert.Equal(2, v.Rows);
    }
}
