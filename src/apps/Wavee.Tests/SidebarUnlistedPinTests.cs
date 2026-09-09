using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

// A pin the library projection does not contain — the Liked Songs route pin, an editorial playlist never saved to the
// library, a pinned page — used to vanish from every Library V3 lens (the list is built from the library and PinsFirst
// cannot move what is absent), while Classic's band still drew it. These pin the two pure halves of the fix:
// SidebarEntryKinds.Admits (which lens admits which ENTRY) and SidebarBinderPipeline.AppendUnlistedPins.
public sealed class SidebarUnlistedPinTests
{
    static SidebarLibraryEntry Entry(string id, SidebarEntryKind kind, string name)
        => new(Id: id, Kind: kind, Uri: "", Name: name, Creator: "", Cover: null, MosaicTiles: null,
            ChildCount: 0, AddedAtMs: 0, SortStamp: 1, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

    static readonly SidebarLibraryEntry Liked = SidebarLibraryEntry.ForRoute("liked", "Liked Songs");
    static readonly SidebarLibraryEntry AlbumsPage = SidebarLibraryEntry.ForRoute("albums", "Albums");
    static readonly SidebarLibraryEntry Editorial = Entry("pl:spotify:playlist:ed", SidebarEntryKind.Playlist, "Discover Weekly");

    [Fact]
    public void LikedRoutePin_IsAPlaylist_ForTheLensFilter()
    {
        Assert.True(SidebarEntryKinds.Admits(SidebarEntryKindMask.All, in Liked));
        Assert.True(SidebarEntryKinds.Admits(SidebarEntryKinds.From(SidebarV3Filter.Playlists), in Liked));
        Assert.False(SidebarEntryKinds.Admits(SidebarEntryKinds.From(SidebarV3Filter.Albums), in Liked));
        Assert.False(SidebarEntryKinds.Admits(SidebarEntryKinds.From(SidebarV3Filter.Artists), in Liked));
        Assert.False(SidebarEntryKinds.Admits(SidebarEntryKinds.From(SidebarV3Filter.Podcasts), in Liked));
    }

    [Fact]
    public void AnyOtherRoutePin_IsAPage_AdmittedOnlyByTheUnfilteredLens()
    {
        Assert.True(SidebarEntryKinds.Admits(SidebarEntryKindMask.All, in AlbumsPage));
        Assert.False(SidebarEntryKinds.Admits(SidebarEntryKinds.From(SidebarV3Filter.Playlists), in AlbumsPage));
        Assert.False(SidebarEntryKinds.Admits(SidebarEntryKinds.From(SidebarV3Filter.Albums), in AlbumsPage));
    }

    [Fact]
    public void AProjectedKind_FollowsTheOrdinaryKindRule()
    {
        Assert.True(SidebarEntryKinds.Admits(SidebarEntryKinds.From(SidebarV3Filter.Playlists), in Editorial));
        Assert.True(SidebarEntryKinds.Admits(SidebarEntryKindMask.All, in Editorial));
        Assert.False(SidebarEntryKinds.Admits(SidebarEntryKinds.From(SidebarV3Filter.Albums), in Editorial));
    }

    [Fact]
    public void AppendUnlistedPins_AddsTheAdmittedRows_InPinOrder_AndReportsHowMany()
    {
        var list = new List<SidebarLibraryEntry> { Entry("pl:spotify:playlist:mine", SidebarEntryKind.Playlist, "Mine") };
        var unlisted = new[] { Liked, AlbumsPage, Editorial };

        int added = SidebarBinderPipeline.AppendUnlistedPins(list, unlisted, SidebarEntryKinds.From(SidebarV3Filter.Playlists));

        Assert.Equal(2, added);
        Assert.Equal(new[] { "pl:spotify:playlist:mine", "liked", "pl:spotify:playlist:ed" }, list.ConvertAll(e => e.Id));
    }

    [Fact]
    public void AppendUnlistedPins_UnderAll_AdmitsThePinnedPageToo()
    {
        var list = new List<SidebarLibraryEntry>();
        int added = SidebarBinderPipeline.AppendUnlistedPins(list, new[] { Liked, AlbumsPage, Editorial }, SidebarEntryKindMask.All);
        Assert.Equal(3, added);
    }

    [Fact]
    public void AppendUnlistedPins_NeverDuplicatesAnIdTheListAlreadyCarries()
    {
        var list = new List<SidebarLibraryEntry> { Liked };
        int added = SidebarBinderPipeline.AppendUnlistedPins(list, new[] { Liked }, SidebarEntryKindMask.All);
        Assert.Equal(0, added);
        Assert.Single(list);
    }

    [Fact]
    public void AppendUnlistedPins_WithNothingUnlisted_IsANoOp()
    {
        var list = new List<SidebarLibraryEntry> { Editorial };
        Assert.Equal(0, SidebarBinderPipeline.AppendUnlistedPins(list, System.Array.Empty<SidebarLibraryEntry>(), SidebarEntryKindMask.All));
        Assert.Single(list);
    }
}
