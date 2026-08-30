using Wavee;
using Xunit;

namespace Wavee.Tests;

// The select-in-place rule behind "Your Library" search: an artist/album hit commits into the BROWSE selection
// (the persisted _selectedKey/_albumKey pair) instead of navigating away from the master-detail panes.
public class LibrarySelectionCommitTests
{
    [Fact]
    public void Artist_SelectsAndResetsTheDiscographyKey()
    {
        var c = LibrarySelectionCommit.ForArtist(artistsView: true, collapsed: false, "spotify:artist:a1");
        Assert.Equal("artist:spotify:artist:a1", c.SelectedKey);
        // "" not null: a new artist has no chosen release yet, and the empty key is what re-arms SyncDisco's auto-select.
        Assert.Equal("", c.AlbumKey);
        Assert.True(c.ClearFilter);
        Assert.Null(c.Depth);
    }

    [Fact]
    public void Album_InAlbumsView_IsTheMasterSelection()
    {
        var c = LibrarySelectionCommit.ForAlbum(artistsView: false, collapsed: false, "al1", ownerArtistUri: "");
        Assert.Equal("album:al1", c.SelectedKey);
        Assert.Null(c.AlbumKey);   // the albums view has no third column — leave the discography key alone
        Assert.True(c.ClearFilter);
    }

    [Fact]
    public void Album_InArtistsView_IsTheDiscographyPickAndCarriesItsArtist()
    {
        var c = LibrarySelectionCommit.ForAlbum(artistsView: true, collapsed: false, "al1", ownerArtistUri: "a1");
        Assert.Equal("artist:a1", c.SelectedKey);
        Assert.Equal("album:al1", c.AlbumKey);
        Assert.True(c.ClearFilter);
    }

    [Fact]
    public void Album_InArtistsView_WithoutAnOwnerLeavesTheArtistAlone()
    {
        var c = LibrarySelectionCommit.ForAlbum(artistsView: true, collapsed: false, "al1", ownerArtistUri: "");
        Assert.Null(c.SelectedKey);
        Assert.Equal("album:al1", c.AlbumKey);
    }

    [Fact]
    public void Collapsed_DrillsToTheLevelTheHitBelongsTo()
    {
        // artist → its discography; album in the albums view → the detail pane; album in the artists view → the tracks.
        Assert.Equal(1, LibrarySelectionCommit.ForArtist(true, collapsed: true, "a1").Depth);
        Assert.Equal(1, LibrarySelectionCommit.ForAlbum(false, collapsed: true, "al1", "").Depth);
        Assert.Equal(2, LibrarySelectionCommit.ForAlbum(true, collapsed: true, "al1", "a1").Depth);
    }

    [Fact]
    public void Wide_NeverTouchesTheDrillDepth()
    {
        Assert.Null(LibrarySelectionCommit.ForArtist(true, collapsed: false, "a1").Depth);
        Assert.Null(LibrarySelectionCommit.ForAlbum(false, collapsed: false, "al1", "").Depth);
        Assert.Null(LibrarySelectionCommit.ForAlbum(true, collapsed: false, "al1", "a1").Depth);
    }

    [Fact]
    public void EmptyUri_WritesNothing()
    {
        var artist = LibrarySelectionCommit.ForArtist(true, collapsed: true, "");
        var album = LibrarySelectionCommit.ForAlbum(true, collapsed: true, "", "a1");
        Assert.True(artist.IsNone);
        Assert.True(album.IsNone);
        Assert.Equal(LibrarySelectionCommit.None, artist);
        Assert.False(artist.ClearFilter);   // no hit ⇒ the query the user is refining survives
    }

    [Fact]
    public void EveryCommitClearsTheFilter()
    {
        // The search view is gated on a non-empty query, so a commit that left the filter up would hide the very panes
        // the selection was written into.
        Assert.True(LibrarySelectionCommit.ForArtist(true, false, "a1").ClearFilter);
        Assert.True(LibrarySelectionCommit.ForAlbum(true, true, "al1", "a1").ClearFilter);
        Assert.True(LibrarySelectionCommit.ForAlbum(false, true, "al1", "").ClearFilter);
    }
}
