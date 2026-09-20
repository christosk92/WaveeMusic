// ── Wavee.Tests/SidebarPinTests.cs — the pin identity, the ylpin wire mapping, and the menu-row decision ────────────
//
// Ported from the 0.2.9 suite (src/apps/_old/Wavee.Tests: PinSyncRulesTests.cs, SidebarPinKindWireTests.cs, the
// PinRowRule facts inside WaveeExtensionRegistryTests.cs) against Shell/Sidebar.cs's PINS section (search
// `SidebarPinId`). Three pure, engine-free surfaces, in file order:
//
//   SidebarPinId   the pin id IS the nav route key — a pinned row resolves its label/glyph with no extra plumbing,
//                  and a pin survives a library refresh because it never depends on a list index. Here: IsPinnable /
//                  FromEntry, the one screen every pin-creation call site (menu, drag, touch) funnels a row through
//                  — Track is the one refusal.
//   PinSyncRules   the pin id <-> Spotify's ylpin wire uri map. Liked Songs is the bare `spotify:collection` on the
//                  wire, not the `:tracks`-suffixed or user-namespaced forms the catalog/routing layer uses
//                  elsewhere.
//   PinRowRule     Decide(hasStore, pinId, isPinned) is an ABSOLUTE-state pair, never a toggle: no store (the
//                  feature's kill switch) or an unpinnable target -> PinRowKind.None — the menu OMITS the row
//                  rather than showing a dead one.
//
// NOT ported here (see the porting agent's handoff for the full accounting):
//   - SidebarPinSyncTests.cs, in full: every fact there drives SidebarPinStore / SidebarPinSync / InMemoryStore /
//     MemoryAppSettings — a live store-and-network bridge, none of which exist in 0.3 yet (still parked under
//     src/apps/_old). That is not a pure rule and belongs in a separate integration-style port once the bridge
//     lands.
//   - SidebarPinKindWireTests.cs's wire-string/legacy-int round trips (PinKindName / TryParsePinKind /
//     TryLegacyPinKind / LegacyPinKindInt): SidebarDocTests.cs already drives those; only the IsPinnable/FromEntry
//     half is ported here.
//   - WaveeExtensionRegistryTests.cs's ATrackActionTargetYieldsNoPinRow (needs
//     SidebarPinId.FromTarget(in ActionTarget) — the Actions platform was not ported to 0.3) and
//     APinnedThenUnpinnedTargetFlipsTheRowBackThroughTheRealStore (drives the live SidebarPinStore).
//
// Pure rules over strings and enums: no TestScope.Fresh, no EntitiesCollection, no engine, no store, no network.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SidebarPinTests
{
    // ── SidebarPinId: pinnability and pin-id derivation ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_track_is_never_pinnable()
    {
        Assert.False(SidebarPinId.IsPinnable(SidebarEntryKind.Track));

        // The pin-creation path itself: SidebarPinId.FromEntry is the ONE screen every RowForEntry/menu/drag call
        // site funnels through.
        var track = new SidebarLibraryEntry("queue:1", SidebarEntryKind.Track, "spotify:track:x", "A Song", "",
            StringId.Empty, null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0,
            Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);
        Assert.Null(SidebarPinId.FromEntry(in track));
    }

    [Theory]
    [InlineData(SidebarEntryKind.AppRoute)]
    [InlineData(SidebarEntryKind.Playlist)]
    [InlineData(SidebarEntryKind.Album)]
    [InlineData(SidebarEntryKind.Artist)]
    [InlineData(SidebarEntryKind.Show)]
    [InlineData(SidebarEntryKind.Folder)]
    public void Every_other_kind_is_pinnable(SidebarEntryKind kind)
        => Assert.True(SidebarPinId.IsPinnable(kind));

    // ── PinSyncRules: the pin id <-> ylpin wire uri map ─────────────────────────────────────────────────────────────

    const string User = "bob";

    [Theory]
    [InlineData("pl:spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", "spotify:playlist:37i9dQZF1DX4sWSpwq3LiO")]
    [InlineData("album:spotify:album:4aawyAB79vO75wG7WLfDzB", "spotify:album:4aawyAB79vO75wG7WLfDzB")]
    [InlineData("artist:spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", "spotify:artist:4tZwfgrHOc3mvqYlEYSvVi")]
    [InlineData("show:spotify:show:4rOoJ6Egrf8K2IrywzwOMk", "spotify:show:4rOoJ6Egrf8K2IrywzwOMk")]
    public void Spotify_kind_pins_map_to_their_bare_uri(string pinId, string expectedUri)
        => Assert.Equal(expectedUri, PinSyncRules.TryWireUri(pinId, User));

    [Fact]
    public void Liked_route_maps_to_the_bare_collection_uri()
        => Assert.Equal("spotify:collection", PinSyncRules.TryWireUri("liked", User));

    [Fact]
    public void Liked_route_is_syncable_even_before_the_username_is_known()
    {
        Assert.Equal("spotify:collection", PinSyncRules.TryWireUri("liked", ""));
        Assert.True(PinSyncRules.IsSyncable("liked", ""));
    }

    [Theory]
    [InlineData("pl:wavee:playlist:x")]     // session-local provider — never syncs
    [InlineData("home")]
    [InlineData("search")]
    [InlineData("albums")]
    [InlineData("prerelease:spotify:prerelease:1")]
    [InlineData("browse:spotify:page:music")]
    [InlineData("module:wavee:module:x:eQ")]
    [InlineData(null)]
    [InlineData("")]
    public void Local_only_pins_never_produce_a_wire_uri(string? pinId)
    {
        Assert.Null(PinSyncRules.TryWireUri(pinId, User));
        Assert.False(PinSyncRules.IsSyncable(pinId, User));
    }

    [Fact]
    public void Folder_pin_maps_to_the_spotify_folder_uri()
        => Assert.Equal("spotify:folder:36405e1711f88d9c",
            PinSyncRules.TryWireUri("folder:36405e1711f88d9c", User));

    [Fact]
    public void Folder_wire_uri_maps_back_to_the_folder_pin()
        => Assert.Equal("folder:36405e1711f88d9c", PinSyncRules.TryPinId("spotify:folder:36405e1711f88d9c"));

    [Theory]
    [InlineData("pl:spotify:playlist:x", true)]
    [InlineData("album:spotify:album:x", true)]
    [InlineData("artist:spotify:artist:x", true)]
    [InlineData("show:spotify:show:x", true)]
    [InlineData("liked", true)]
    [InlineData("folder:x", true)]
    [InlineData("home", false)]
    [InlineData("pl:wavee:playlist:x", false)]
    public void IsSyncable_agrees_with_TryWireUri(string pinId, bool expected)
        => Assert.Equal(expected, PinSyncRules.IsSyncable(pinId, User));

    [Theory]
    [InlineData("spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", "pl:spotify:playlist:37i9dQZF1DX4sWSpwq3LiO")]
    [InlineData("spotify:album:4aawyAB79vO75wG7WLfDzB", "album:spotify:album:4aawyAB79vO75wG7WLfDzB")]
    [InlineData("spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", "artist:spotify:artist:4tZwfgrHOc3mvqYlEYSvVi")]
    [InlineData("spotify:show:4rOoJ6Egrf8K2IrywzwOMk", "show:spotify:show:4rOoJ6Egrf8K2IrywzwOMk")]
    public void Wire_uris_map_back_to_their_pin_id(string wireUri, string expectedId)
        => Assert.Equal(expectedId, PinSyncRules.TryPinId(wireUri));

    // Every spelling EntityUri.IsLikedCollection recognises collapses onto the one "liked" route pin, plus the bare
    // "spotify:collection" the ylpin set actually carries on the wire.
    [Theory]
    [InlineData("spotify:collection")]
    [InlineData("spotify:collection:tracks")]
    [InlineData("spotify:user:bob:collection")]
    [InlineData("spotify:user:bob:collection:tracks")]
    public void Every_liked_spelling_maps_to_the_liked_route_pin(string wireUri)
        => Assert.Equal("liked", PinSyncRules.TryPinId(wireUri));

    [Theory]
    [InlineData("spotify:track:4cOdK2wGLETKBW3PvgPWqT")]    // tracks are never pinnable
    [InlineData("spotify:episode:512ojhOuo1ktJprKbVcKyQ")]  // nor episodes
    [InlineData("wavee:playlist:x")]                        // not a spotify: uri
    [InlineData("spotify:folder:")]                         // no hex id
    [InlineData("spotify:folder:zz:1")]                     // non-hex + trailing segment
    [InlineData("spotify:folder:36405e1711f88d9c:x")]       // trailing segment after a valid id
    [InlineData("")]
    [InlineData(null)]
    public void Unpinnable_or_unrecognised_wire_uris_yield_no_pin_id(string? wireUri)
        => Assert.Null(PinSyncRules.TryPinId(wireUri));

    // ── PinRowRule: the menu-row decision is an absolute-state pair, never a toggle ─────────────────────────────────

    [Fact]
    public void A_pinnable_unpinned_target_shows_pin()
        => Assert.Equal(PinRowKind.Pin, PinRowRule.Decide(hasStore: true, "album:spotify:album:a1", isPinned: false));

    [Fact]
    public void A_pinned_target_shows_unpin()
        => Assert.Equal(PinRowKind.Unpin, PinRowRule.Decide(hasStore: true, "album:spotify:album:a1", isPinned: true));

    [Fact]
    public void No_pin_store_means_no_row_at_all()
    {
        // The kill switch: no pin store (ActionServices.Sidebar null in production) -> the row is OMITTED, not
        // rendered disabled.
        Assert.Equal(PinRowKind.None, PinRowRule.Decide(hasStore: false, "album:spotify:album:a1", isPinned: false));
        Assert.Equal(PinRowKind.None, PinRowRule.Decide(hasStore: false, "album:spotify:album:a1", isPinned: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_unpinnable_target_shows_no_row(string? pinId)
        => Assert.Equal(PinRowKind.None, PinRowRule.Decide(hasStore: true, pinId, isPinned: false));

    [Theory]
    [InlineData("spotify:track:t1")]
    [InlineData("spotify:episode:e1")]
    [InlineData("")]
    public void Tracks_and_episodes_are_never_pinnable_so_they_never_get_a_row(string uri)
    {
        string? pinId = SidebarPinId.FromUri(uri);
        Assert.Null(pinId);
        Assert.Equal(PinRowKind.None, PinRowRule.Decide(hasStore: true, pinId, isPinned: false));
    }
}
