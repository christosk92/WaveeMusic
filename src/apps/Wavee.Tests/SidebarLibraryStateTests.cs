using Wavee;
using Xunit;

namespace Wavee.Tests;

// ── SidebarLibraryState: when Your Library groups its playlists into the inline folder tree (design P.2, §P5.1) ─────────
//
// docs/plans/wavee/sidebar-rework-implementation.md §P5.1 and §P5.10. Pure: the state is a plain record.

public sealed class SidebarLibraryStateTests
{
    static SidebarLibraryState State(
        SidebarLibraryFilter filter = SidebarLibraryFilter.None,
        SidebarLibraryView view = SidebarLibraryView.List,
        bool searching = false,
        string? drillFolderId = null)
        => new(filter, SidebarLibrarySort.Recents, Descending: false, view, GridColumns: 3, searching, drillFolderId, HasPins: false);

    [Fact]
    public void FoldersApply_ListNoSearch_NoneOrPlaylists()
    {
        Assert.True(State(SidebarLibraryFilter.None).FoldersApply);
        Assert.True(State(SidebarLibraryFilter.Playlists).FoldersApply);
    }

    [Fact]
    public void FoldersApply_FalseInGrid()
    {
        Assert.False(State(SidebarLibraryFilter.None, SidebarLibraryView.Grid).FoldersApply);
    }

    [Fact]
    public void FoldersApply_FalseUnderAlbums()
    {
        Assert.False(State(SidebarLibraryFilter.Albums).FoldersApply);
    }

    [Fact]
    public void FoldersApply_FalseWhileSearching()
    {
        Assert.False(State(SidebarLibraryFilter.None, searching: true).FoldersApply);
    }

    [Fact]
    public void FoldersApply_FalseWhileDrilled()
    {
        // A drilled level is one folder's direct children: no inline tree under it.
        Assert.False(State(SidebarLibraryFilter.None, drillFolderId: "f1").FoldersApply);
    }
}
