using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

/// <summary>C8.4 — the shared pin store. Drives the REAL <see cref="SidebarPinStore"/> (engine-free apart from
/// <c>Signal&lt;int&gt;</c>, which the test assembly's <c>VirtualCollectionSignalShim</c> supplies) plus the real
/// <c>SidebarPinId</c> mapping, so the contracts every Pinned section and the projection's pin band depend on are pinned
/// against production code rather than a copy of it.</summary>
public class SidebarPinStoreTests
{
    static SidebarPin Pin(string id, SidebarEntryKind kind = SidebarEntryKind.Playlist,
                          string uri = "spotify:playlist:x", string name = "n", long addedAtMs = 0)
        => new(id, kind, uri, name, addedAtMs);

    static SidebarPinStore StoreOf(params string[] ids)
    {
        var s = new SidebarPinStore();
        for (int i = 0; i < ids.Length; i++) Assert.True(s.Pin(Pin(ids[i])));
        return s;
    }

    static List<string> IdsOf(SidebarPinStore s)
    {
        var ids = new List<string>(s.Count);
        for (int i = 0; i < s.Count; i++) ids.Add(s[i].Id);
        return ids;
    }

    [Fact]
    public void Pin_AppendsAtEnd()
    {
        var s = StoreOf("a", "b", "c");
        Assert.Equal(new[] { "a", "b", "c" }, IdsOf(s));
        Assert.Equal(2, s.IndexOf("c"));
    }

    [Fact]
    public void Pin_Twice_IsDeduped_AndKeepsOriginalPosition()
    {
        var s = StoreOf("a", "b", "c");
        int version = s.Version.Peek();

        // A second Pin of the same id is a silent no-op (the menu shows Unpin in that state), and critically it must not
        // move the pin to the end — a double invoke can never reorder the list.
        Assert.False(s.Pin(Pin("a", name: "renamed")));
        Assert.Equal(new[] { "a", "b", "c" }, IdsOf(s));
        Assert.Equal(0, s.IndexOf("a"));
        Assert.Equal("n", s[0].Name);                 // the rejected pin did not overwrite the cached display name
        Assert.Equal(version, s.Version.Peek());      // a rejected mutation does not bump the version (⇒ no commit, no re-render)
    }

    [Fact]
    public void Unpin_ReturnsFormerIndex_ForTheUndoRestore()
    {
        var s = StoreOf("a", "b", "c");
        Assert.Equal(1, s.Unpin("b"));
        Assert.Equal(new[] { "a", "c" }, IdsOf(s));
        Assert.Equal(1, s.IndexOf("c"));              // the tail was reindexed
    }

    [Fact]
    public void Unpin_Missing_IsNoOp()
    {
        var s = StoreOf("a");
        int version = s.Version.Peek();
        Assert.Equal(-1, s.Unpin("nope"));
        Assert.Equal(-1, s.Unpin(null));
        Assert.Equal(1, s.Count);
        Assert.Equal(version, s.Version.Peek());
    }

    [Fact]
    public void InsertPin_RestoresAtFormerIndex_AndClampsOutOfRange()
    {
        var s = StoreOf("a", "b", "c");
        int at = s.Unpin("b");
        Assert.True(s.Insert(Pin("b"), at));
        Assert.Equal(new[] { "a", "b", "c" }, IdsOf(s));

        // An undo that arrives after other pins were removed must still land somewhere sane, never throw.
        Assert.True(s.Insert(Pin("z"), 999));
        Assert.Equal("z", s[^1].Id);
        Assert.True(s.Insert(Pin("y"), -5));
        Assert.Equal("y", s[0].Id);
        Assert.False(s.Insert(Pin("a"), 0));          // already pinned → rejected, not duplicated
        Assert.Equal(5, s.Count);
    }

    [Fact]
    public void MovePin_ReordersWithinTheList()
    {
        var s = StoreOf("a", "b", "c", "d");

        s.Move(0, 2);                                  // forward
        Assert.Equal(new[] { "b", "c", "a", "d" }, IdsOf(s));

        s.Move(3, 1);                                  // backward (from > to)
        Assert.Equal(new[] { "b", "d", "c", "a" }, IdsOf(s));

        s.Move(0, s.Count);                            // to == count ⇒ "move to the end", clamped, never out of range
        Assert.Equal(new[] { "d", "c", "a", "b" }, IdsOf(s));

        for (int i = 0; i < s.Count; i++) Assert.Equal(i, s.IndexOf(s[i].Id));   // the index map tracked every move
    }

    [Fact]
    public void MovePin_NoOpAndOutOfRange_DoNotBumpTheVersion()
    {
        var s = StoreOf("a", "b");
        int version = s.Version.Peek();
        s.Move(1, 1);
        s.Move(7, 0);
        s.Move(-1, 0);
        Assert.Equal(new[] { "a", "b" }, IdsOf(s));
        Assert.Equal(version, s.Version.Peek());
    }

    [Fact]
    public void Pins_AreUnlimited()
    {
        // Locked decision 4: unlimited, no cap, no eviction.
        var s = new SidebarPinStore();
        for (int i = 0; i < 1000; i++) Assert.True(s.Pin(Pin("p" + i)));
        Assert.Equal(1000, s.Count);
        Assert.Equal("p0", s[0].Id);
        Assert.Equal("p999", s[999].Id);
        Assert.Equal(500, s.IndexOf("p500"));
    }

    [Fact]
    public void PinOrder_IsStable_AcrossUnrelatedMutations()
    {
        // The precondition the projection's "pins first, in pin-store order" band relies on: removing or adding an
        // unrelated pin never permutes the surviving pins' relative order.
        var s = StoreOf("a", "b", "c", "d");
        s.Unpin("c");
        Assert.True(s.Pin(Pin("e")));
        Assert.Equal(new[] { "a", "b", "d", "e" }, IdsOf(s));
        Assert.True(s.IndexOf("a") < s.IndexOf("b"));
        Assert.True(s.IndexOf("b") < s.IndexOf("d"));
    }

    [Fact]
    public void RouteAndEntityPins_Coexist_InInsertionOrder()
    {
        var s = new SidebarPinStore();
        Assert.True(s.Pin(Pin("liked", SidebarEntryKind.AppRoute, "spotify:collection:tracks", "Liked Songs")));
        Assert.True(s.Pin(Pin("pl:spotify:playlist:1", SidebarEntryKind.Playlist)));
        Assert.True(s.Pin(Pin("folder:6a1f2c", SidebarEntryKind.Folder, "", "Cafe & chill")));
        Assert.True(s.Pin(Pin("artist:spotify:artist:1", SidebarEntryKind.Artist, "spotify:artist:1", "Daft Punk")));

        Assert.Equal(new[] { "liked", "pl:spotify:playlist:1", "folder:6a1f2c", "artist:spotify:artist:1" }, IdsOf(s));
        Assert.Equal(SidebarEntryKind.AppRoute, s[0].Kind);
        Assert.Equal(SidebarEntryKind.Folder, s[2].Kind);
    }

    [Fact]
    public void Touch_RefreshesTheCachedName_WithoutBumpingTheVersion()
    {
        // A display-cache refresh must never commit on its own (commit point #2) and must never invalidate a render
        // mid-projection — so it reports "changed" to the caller but does not bump the version.
        var s = StoreOf("a");
        int version = s.Version.Peek();

        Assert.True(s.Touch("a", "New Name"));
        Assert.Equal("New Name", s[0].Name);
        Assert.Equal(version, s.Version.Peek());

        Assert.False(s.Touch("a", "New Name"));       // unchanged → no-op
        Assert.False(s.Touch("a", ""));               // an empty name is never a refresh
        Assert.False(s.Touch("missing", "x"));
    }

    [Fact]
    public void LoadFrom_DropsIdlessAndDuplicateRows()
    {
        // A hand-edited document must never produce two rows with one identity.
        var s = new SidebarPinStore();
        s.LoadFrom([Pin("a"), Pin(""), Pin("b"), Pin("a", name: "dupe")]);
        Assert.Equal(new[] { "a", "b" }, IdsOf(s));
        Assert.Equal("n", s[0].Name);
    }

    [Fact]
    public void OnChanged_FiresForAcceptedMutationsOnly()
    {
        var s = new SidebarPinStore();
        int commits = 0;
        s.OnChanged = () => commits++;

        s.Pin(Pin("a"));            // 1
        s.Pin(Pin("a"));            // rejected
        s.Insert(Pin("b"), 0);      // 2
        s.Move(0, 1);               // 3
        s.Move(1, 1);               // rejected (no-op)
        s.Unpin("zz");              // rejected
        s.Unpin("a");               // 4
        s.Touch("b", "x");          // never commits alone

        Assert.Equal(4, commits);
    }

    // ── SidebarPinId: what is pinnable at all (locked decision 4) ────────────────────────────────────────────────────

    [Theory]
    [InlineData("spotify:track:4cOdK2wGLETKBW3PvgPWqT")]
    [InlineData("spotify:episode:512ojhOuo1ktJprKbVcKyQ")]
    [InlineData("spotify:local:artist:album:track:180")]
    [InlineData("")]
    [InlineData(null)]
    public void Tracks_AndEpisodes_AreNeverPinnable(string? uri)
        => Assert.Null(SidebarPinId.FromUri(uri));

    [Theory]
    [InlineData("spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", "pl:spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", SidebarEntryKind.Playlist)]
    [InlineData("spotify:album:4aawyAB79vO75wG7WLfDzB", "album:spotify:album:4aawyAB79vO75wG7WLfDzB", SidebarEntryKind.Album)]
    [InlineData("spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", "artist:spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", SidebarEntryKind.Artist)]
    [InlineData("spotify:show:4rOoJ6Egrf8K2IrywzwOMk", "show:spotify:show:4rOoJ6Egrf8K2IrywzwOMk", SidebarEntryKind.Show)]
    public void EntityUris_MapToAPrefixedId_AndBackToTheirKind(string uri, string expectedId, SidebarEntryKind expectedKind)
    {
        string? id = SidebarPinId.FromUri(uri);
        Assert.Equal(expectedId, id);
        Assert.Equal(expectedKind, SidebarPinId.KindOf(id!));
    }

    [Fact]
    public void LikedSongs_IsARoutePin_NotAPlaylistPin()
    {
        // The one special case: the Liked Songs collection uri is the "liked" ROUTE, because the pin id IS the nav route key.
        string? id = SidebarPinId.FromUri("spotify:collection:tracks");
        Assert.Equal("liked", id);
        Assert.Equal(SidebarEntryKind.AppRoute, SidebarPinId.KindOf(id!));
    }

    [Fact]
    public void PinnableRoutes_SeedThePicker_WhileDynamicDestinationsRemainPinnable()
    {
        for (int i = 0; i < SidebarPinId.PinnableRoutes.Length; i++)
        {
            string route = SidebarPinId.PinnableRoutes[i];
            Assert.Equal(route, SidebarPinId.FromRoute(route));
            Assert.Equal(SidebarEntryKind.AppRoute, SidebarPinId.KindOf(route));
        }
        // The picker is intentionally curated, but the pin model accepts any durable app destination.
        Assert.Equal("browse:spotify:page:music", SidebarPinId.FromRoute("browse:spotify:page:music"));
        Assert.True(SidebarPinId.IsPinnableRoute("browse:spotify:page:music"));
        Assert.Equal("concerts", SidebarPinId.FromRoute("concerts"));
        Assert.Equal("artist-concerts:spotify:artist:x", SidebarPinId.FromRoute("artist-concerts:spotify:artist:x"));

        // Shell-internal surfaces are never sidebar destinations.
        Assert.Null(SidebarPinId.FromRoute("settings"));
        Assert.False(SidebarPinId.IsPinnableRoute("settings"));
        Assert.Null(SidebarPinId.FromRoute("api-console"));
        Assert.Null(SidebarPinId.FromRoute("sidebar-customize"));
        Assert.Null(SidebarPinId.FromRoute("home-customize"));
        Assert.Null(SidebarPinId.FromRoute("playback-diagnostics"));
        Assert.Null(SidebarPinId.FromRoute(""));
        Assert.Null(SidebarPinId.FromRoute(null));

        // …and so is anything the route vocabulary does not RECOGNISE. FromRoute is the app's route recogniser, not just
        // a policy filter: WaveeActionTargets.Resolve asks it "is this stored key a route?" before falling through to its
        // bare-uri arm, and SidebarPaneSlot / SidebarDestination gate on it. A version that returned every non-empty
        // string made a third-party entity uri resolve to a route pin with an EMPTY entity uri, and turned any typo into
        // a pin that painted as the "Your Library" fallback.
        Assert.Null(SidebarPinId.FromRoute("acme:widget:7"));
        Assert.Null(SidebarPinId.FromRoute("not-a-route"));
        Assert.Null(SidebarPinId.FromRoute("spotify:album:5"));   // an entity URI is FromUri's job, never a route

        // One dated event is not a durable destination — the hub and the artist schedule above are. See
        // SidebarDataSourceTests.Events_carry_title_venue_and_the_event_instant_and_are_not_pinnable.
        Assert.Null(SidebarPinId.FromRoute("concert:spotify:concert:9"));
    }

    // The full recently-played page is CUSTOMIZABLE, not mandatory: it seeds the picker (so it can be added), and it
    // survives FromRoute (so a pin made from its own page chrome converges on the same record) — but the shell's
    // built-in top-bar band still ships Home alone. Putting it there would push it on every user who never asked.
    [Fact]
    public void Recents_IsPinnable_ButIsNotInTheBuiltInTopBar()
    {
        Assert.Contains("recents", SidebarPinId.PinnableRoutes);
        Assert.Equal("recents", SidebarPinId.FromRoute("recents"));
        Assert.True(SidebarPinId.IsPinnableRoute("recents"));
        Assert.Equal(SidebarEntryKind.AppRoute, SidebarPinId.KindOf("recents"));
        Assert.Equal("recents", SidebarPinId.RouteOf("recents"));
        Assert.Equal("", SidebarPinId.UriOf("recents"));            // a route pin carries no entity uri

        foreach (var item in Wavee.Core.Sidebar.SidebarCustomLayout.DefaultTopBar)
            Assert.NotEqual("recents", item.Key);
    }

    [Fact]
    public void Destination_CanonicalizesSearch_AndRetainsBrowseIdentity()
    {
        var search = SidebarDestination.FromRoute("search", "one query", "Search");
        Assert.NotNull(search);
        Assert.Null(search.Value.Arg);
        Assert.Equal("search", search.Value.PinId);

        var browse = SidebarDestination.FromRoute("browse:spotify:page:music", "Music", "Music");
        Assert.NotNull(browse);
        Assert.Equal("spotify:page:music", browse.Value.Uri);
        Assert.Equal("Music", browse.Value.Name);

        Assert.Null(SidebarDestination.FromRoute("sidebar-customize", null, "Customize sidebar"));
    }

    [Fact]
    public void Folder_PinsByRootlistGroupId_AndNeverNavigates()
    {
        string id = SidebarPinId.ForFolder("6a1f2c");
        Assert.Equal("folder:6a1f2c", id);
        Assert.Equal(SidebarEntryKind.Folder, SidebarPinId.KindOf(id));
        Assert.Equal("6a1f2c", SidebarPinId.FolderIdOf(id));
        Assert.Null(SidebarPinId.RouteOf(id));          // a folder expands in place — it has no route
        Assert.Equal("", SidebarPinId.UriOf(id));

        // …while every other kind's id IS its route key (which is what makes the recency join an identity lookup).
        string pl = SidebarPinId.FromUri("spotify:playlist:1")!;
        Assert.Equal(pl, SidebarPinId.RouteOf(pl));
    }

    /// <summary>Folder CRUD is live now, so a pinned folder can genuinely VANISH under its pin. The store keeps it: the
    /// sidebar's standing rule is that a missing entity renders visible-but-disabled with a reason, and only an explicit
    /// unpin removes a user's row. Auto-removing here would silently delete a pin the user would have re-created — and
    /// would do it on a transient cold boot where the tree simply has not loaded yet.</summary>
    [Fact]
    public void PinToAVanishedFolder_IsKept()
    {
        var s = new SidebarPinStore();
        string id = SidebarPinId.ForFolder("6a1f2c");
        Assert.True(s.Pin(Pin(id, SidebarEntryKind.Folder, "", "Late night")));

        // The folder is deleted on another device: nothing in the store is told, and nothing in the store reacts.
        Assert.True(s.IsPinned(id));
        Assert.Equal(0, s.IndexOf(id));
        Assert.Equal("Late night", s[0].Name);                       // the offline display cache still names the row

        // …and a RENAME does not disturb it either: the pin is keyed by the client-minted groupId.
        s.Touch(id, "Very late night");
        Assert.True(s.IsPinned(id));
        Assert.Equal(id, SidebarPinId.ForFolder("6a1f2c"));
    }

    [Fact]
    public void IsPinned_UsesTheStableId_SoARenameCannotUnpin()
    {
        var s = new SidebarPinStore();
        string id = SidebarPinId.FromUri("spotify:playlist:37i9dQZF1DX4sWSpwq3LiO")!;
        Assert.True(s.Pin(Pin(id, SidebarEntryKind.Playlist, "spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", "Peaceful Piano")));

        s.Touch(id, "Peaceful Piano (2026)");
        Assert.True(s.IsPinned(id));
        Assert.Equal(0, s.IndexOf(id));
    }

    [Fact]
    public void Canonical_MapsABareEntityUriOntoThePrefixedPinId()
    {
        Assert.Equal("pl:spotify:playlist:x", SidebarPinId.Canonical("spotify:playlist:x"));
        Assert.Equal("pl:spotify:playlist:x", SidebarPinId.Canonical("pl:spotify:playlist:x"));
        Assert.Equal("album:spotify:album:x", SidebarPinId.Canonical("spotify:album:x"));
        Assert.Equal("artist:spotify:artist:x", SidebarPinId.Canonical("spotify:artist:x"));
        Assert.Equal("liked", SidebarPinId.Canonical("spotify:collection:tracks"));
        Assert.Null(SidebarPinId.Canonical("spotify:track:x"));
    }

    [Fact]
    public void ARawUriPin_IsFoundAndRemovedThroughTheCanonicalId()
    {
        // Card/hero drops used to persist the bare uri as SidebarPin.Id. The menu looks up pl:… / album:… / artist:…
        // — without the alias those pins were immortal (Pin was a silent no-op, Unpin never appeared).
        var s = new SidebarPinStore();
        Assert.True(s.Pin(Pin("spotify:playlist:stuck", SidebarEntryKind.Playlist, "spotify:playlist:stuck", "My Playlist #6")));
        Assert.Equal("pl:spotify:playlist:stuck", s[0].Id);          // Pin canonicalizes on the way in
        Assert.True(s.IsPinned("spotify:playlist:stuck"));
        Assert.True(s.IsPinned("pl:spotify:playlist:stuck"));
        Assert.Equal(0, s.Unpin("pl:spotify:playlist:stuck"));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void LoadFrom_MigratesALegacyRawUriPin_AndDedupesThePrefixedTwin()
    {
        var s = new SidebarPinStore();
        s.LoadFrom(
        [
            Pin("spotify:playlist:stuck", SidebarEntryKind.Playlist, "spotify:playlist:stuck", "My Playlist #6"),
            Pin("pl:spotify:playlist:stuck", SidebarEntryKind.Playlist, "spotify:playlist:stuck", "My Playlist #6"),
        ]);
        Assert.Equal(new[] { "pl:spotify:playlist:stuck" }, IdsOf(s));
        Assert.Equal(0, s.Unpin("spotify:playlist:stuck"));
        Assert.Equal(0, s.Count);
    }
}
