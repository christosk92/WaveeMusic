// ── Wavee.Tests/ArtistDrawerVerdictTests.cs — the ARTIST half of the drawer verdict (WP-5.N contract §9) ─────────────
//
// `Album.DrawerVerdict.For` itself is owner M's, ported verbatim with its own 10 facts (AlbumDrawerVerdictTests). What
// this file pins is the ADAPTER the discography grid calls — `Artist.DrawerVerdictFor` — which turns the 0.3 edge model
// (the expanded album's `AlbumTracks` readiness and count, the card's `TrackCount`, the grid columns) into that call's
// 0.2.9 inputs. The mapping IS the drawer's behaviour: get it wrong and a warm album shimmers, a failed one spins
// forever, or a loading one shows another album's rows.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ArtistDrawerVerdictTests
{
    const string Uri = "spotify:album:drawer";

    [Fact]
    public void An_unanswered_tracklist_is_loading_sized_from_the_cards_own_count()
    {
        var v = Artist.DrawerVerdictFor(Uri, EdgeState.Unknown, tracksCount: 0, cardTrackCount: 5, gridColumns: 3);
        Assert.True(v.Loading);
        Assert.False(v.ReadyEmpty);
        Assert.Equal(5, v.Shown);                                        // the slot is FINAL on the click frame
        Assert.Equal(5, v.Rows);
    }

    [Fact]
    public void An_unanswered_tracklist_with_no_advertised_count_shimmers_three_rows()
    {
        var v = Artist.DrawerVerdictFor(Uri, EdgeState.Unknown, 0, 0, 3);
        Assert.True(v.Loading);
        Assert.Equal(Album.DrawerVerdict.FallbackShimmerRows, v.Shown);
    }

    [Fact]
    public void A_complete_tracklist_is_the_selected_album_loaded_with_no_shimmer()
    {
        // A warm (seeded, or re-opened) album: complete on the click frame, rows at once (ch 08 parity 51).
        var v = Artist.DrawerVerdictFor(Uri, EdgeState.Complete, tracksCount: 6, cardTrackCount: 6, gridColumns: 3);
        Assert.False(v.Loading);
        Assert.Equal(6, v.Shown);
        Assert.Equal(6, v.Rows);
        Assert.Equal(Uri, v.Uri);
    }

    [Fact]
    public void Columns_and_the_show_all_row_follow_the_grid()
    {
        var wide = Artist.DrawerVerdictFor(Uri, EdgeState.Complete, 13, 13, gridColumns: 5);
        Assert.Equal(2, wide.Columns);
        Assert.False(wide.ShowAllRow);
        Assert.Equal(7, wide.Rows);

        var narrow = Artist.DrawerVerdictFor(Uri, EdgeState.Complete, 13, 13, gridColumns: 3);
        Assert.Equal(1, narrow.Columns);
        Assert.Equal(12, narrow.Shown);
        Assert.True(narrow.ShowAllRow);
        Assert.Equal(456f, narrow.PanelHeight);
        Assert.Equal(472f, narrow.SlotHeight);
    }

    [Fact]
    public void A_failed_read_is_ready_empty_two_rows_with_nothing_pending()
    {
        // The Retry note (ch 08 W16, parity 86): never a spinner that waits for an answer that already failed.
        var v = Artist.DrawerVerdictFor(Uri, EdgeState.Failed, tracksCount: 0, cardTrackCount: 12, gridColumns: 5);
        Assert.True(v.ReadyEmpty);
        Assert.False(v.Loading);
        Assert.Equal(2, v.Rows);
        Assert.Equal(104f, v.PanelHeight);
        Assert.Equal(120f, v.SlotHeight);
    }

    [Fact]
    public void A_complete_but_empty_tracklist_is_ready_empty()
    {
        var v = Artist.DrawerVerdictFor(Uri, EdgeState.Complete, 0, 4, 3);
        Assert.True(v.ReadyEmpty);
        Assert.Equal(2, v.Rows);
    }

    [Fact]
    public void A_partial_tracklist_with_rows_renders_them_and_without_rows_still_loads()
    {
        var some = Artist.DrawerVerdictFor(Uri, EdgeState.Partial, tracksCount: 4, cardTrackCount: 30, gridColumns: 3);
        Assert.False(some.Loading);
        Assert.Equal(4, some.Shown);

        var none = Artist.DrawerVerdictFor(Uri, EdgeState.Partial, 0, 30, 3);
        Assert.True(none.Loading);
    }

    [Fact]
    public void A_closed_drawer_is_the_default_verdict()
        => Assert.Equal(default, Artist.DrawerVerdictFor("", EdgeState.Complete, 3, 3, 5));
}

[Collection(EntitiesCollection.Name)]
public class ArtistDrawerVerdictTableTests
{
    [Fact]
    public void The_handle_overload_reads_the_albums_tracklist_and_card_count()
    {
        TestScope.Fresh();
        var albums = Entities.Current.Albums;
        int album = albums.Slot("spotify:album:drawer-table".AsSpan());
        albums.TrackCount[album] = 9;
        var tracks = Entities.Current.Edges.AlbumTracks;

        var cold = Artist.DrawerVerdictFor(new Album(album), "spotify:album:drawer-table", 3);
        Assert.True(cold.Loading);
        Assert.Equal(9, cold.Shown);

        int t1 = Entities.Current.Tracks.Slot("spotify:track:drawer-1".AsSpan());
        int t2 = Entities.Current.Tracks.Slot("spotify:track:drawer-2".AsSpan());
        tracks.ReplaceRun(album, [t1, t2], default);
        var warm = Artist.DrawerVerdictFor(new Album(album), "spotify:album:drawer-table", 3);
        Assert.False(warm.Loading);
        Assert.Equal(2, warm.Shown);

        Assert.Equal(default, Artist.DrawerVerdictFor(default(Album), "spotify:album:drawer-table", 3));
    }
}
