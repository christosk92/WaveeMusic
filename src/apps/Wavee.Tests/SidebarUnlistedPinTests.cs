using System.Collections.Generic;
using Wavee.Core;
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

    // An unlisted pin's resolved SidebarLibraryEntry rides the SAME entries list PlanPinned/EmitProjected build
    // rows from, so it renders through SidebarPaneSlot.EntryRow like any other projected entry — there is no
    // separate pin-band title path. This pins that a pin with neither a cached name (SidebarPin.Name, saved at pin
    // time) nor a hydrated overlay resolves to SidebarRowTitle.Skeleton through the SAME
    // SidebarRowTitlePresentation.Resolve rule EntryRow uses — never SidebarPaneText.ShortUri.
    [Fact]
    public void ResolveUnlistedPin_WithNoCachedNameAndNoHydratedOverlay_FeedsTheSameSkeletonRule()
    {
        var pin = new SidebarPin("itm_1", SidebarEntryKind.Playlist, "spotify:playlist:abc", Name: "", AddedAtMs: 0);

        var entry = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, current: null);

        Assert.Equal("", entry.Name);
        var title = SidebarRowTitlePresentation.Resolve(labelOverride: null, entry.Name);
        Assert.True(title.IsSkeleton);
    }

    // Once the pin carries its own cached name (the ordinary case — SidebarPin.Name is stamped when the user pins a
    // resolved entity) the same rule renders it as text, exactly like a projected library entry.
    [Fact]
    public void ResolveUnlistedPin_WithACachedName_FeedsTextThroughTheSameRule()
    {
        var pin = new SidebarPin("itm_2", SidebarEntryKind.Playlist, "spotify:playlist:abc", Name: "Discover Weekly", AddedAtMs: 0);

        var entry = SidebarBinderPipeline.ResolveUnlistedPin(pin, sourceOrder: 0, current: null);

        var title = SidebarRowTitlePresentation.Resolve(labelOverride: null, entry.Name);
        Assert.False(title.IsSkeleton);
        Assert.Equal("Discover Weekly", title.Text);
    }

    // Regression: a pin whose entity IS in the caller's library index (SidebarProjectionBinder._index) but whose
    // PlaylistHeader facet has not resolved yet — the mosaic cover and child count come off the playlist's replica,
    // independent of the header, so the library entry can carry a real Cover/ChildCount alongside an empty Name.
    // Before ResolveListedPin existed, the listed branch used that entry completely as-is, rendering a correct
    // mosaic/subtitle beside a permanently blank name instead of the pin's own cached display name.
    [Fact]
    public void ResolveListedPin_WithAnEmptyLibraryName_FallsBackToThePinsCachedName()
    {
        var pin = new SidebarPin("pl:spotify:playlist:abc", SidebarEntryKind.Playlist, "spotify:playlist:abc",
            Name: "Discover Weekly", AddedAtMs: 0);
        var entry = new SidebarLibraryEntry(pin.Id, SidebarEntryKind.Playlist, pin.Uri, Name: "",
            Creator: "", Cover: new Image("", MosaicTiles: ["a", "b", "c", "d"]), MosaicTiles: null,
            ChildCount: 42, AddedAtMs: 0, SortStamp: 1, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

        var resolved = SidebarBinderPipeline.ResolveListedPin(pin, sourceOrder: 3, entry);

        Assert.Equal("Discover Weekly", resolved.Name);
        Assert.Equal(entry.Cover, resolved.Cover);
        Assert.Equal(42, resolved.ChildCount);
        Assert.True(resolved.IsPinned);
        Assert.Equal(3, resolved.SourceOrder);
        var title = SidebarRowTitlePresentation.Resolve(labelOverride: null, resolved.Name);
        Assert.False(title.IsSkeleton);
    }

    // Once the library entry's own name resolves it always wins over the pin's (possibly stale) cached name — the
    // fallback is only for the gap while the header is in flight.
    [Fact]
    public void ResolveListedPin_WithALibraryName_PrefersTheLibraryNameOverTheCachedOne()
    {
        var pin = new SidebarPin("pl:spotify:playlist:abc", SidebarEntryKind.Playlist, "spotify:playlist:abc",
            Name: "Stale Cached Name", AddedAtMs: 0);
        var entry = new SidebarLibraryEntry(pin.Id, SidebarEntryKind.Playlist, pin.Uri, Name: "Renamed Playlist",
            Creator: "", Cover: null, MosaicTiles: null, ChildCount: 0, AddedAtMs: 0, SortStamp: 1,
            LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

        var resolved = SidebarBinderPipeline.ResolveListedPin(pin, sourceOrder: 0, entry);

        Assert.Equal("Renamed Playlist", resolved.Name);
    }

    // Neither cached nor library name available: the row still resolves through the same skeleton rule as an
    // unlisted pin — never a raw uri, never silently blank text with no loading affordance.
    [Fact]
    public void ResolveListedPin_WithNoNameAnywhere_FeedsTheSameSkeletonRule()
    {
        var pin = new SidebarPin("pl:spotify:playlist:abc", SidebarEntryKind.Playlist, "spotify:playlist:abc",
            Name: "", AddedAtMs: 0);
        var entry = new SidebarLibraryEntry(pin.Id, SidebarEntryKind.Playlist, pin.Uri, Name: "",
            Creator: "", Cover: null, MosaicTiles: null, ChildCount: 0, AddedAtMs: 0, SortStamp: 1,
            LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);

        var resolved = SidebarBinderPipeline.ResolveListedPin(pin, sourceOrder: 0, entry);

        Assert.Equal("", resolved.Name);
        var title = SidebarRowTitlePresentation.Resolve(labelOverride: null, resolved.Name);
        Assert.True(title.IsSkeleton);
    }
}
