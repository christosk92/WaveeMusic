// ── Wavee.Tests/SidebarAccountStoreTests.cs — the per-account sidebar data: the account key, the file, the migration latch,
// the account guard that stops one account's pins reaching another, and the pin list the account file holds ──────────
//
// The pin list facts (SidebarPinStoreTests, SidebarPinIdentityTests) are the ones the account file persists.
// Pure: the file's bytes are produced and parsed in memory; the only disk the bridge reads is the fixture's temp folder
// (none here). The pin bridge is LibraryPinSync, driven with a recording write and a latch built from two account keys.

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SidebarAccountStoreTests
{
    const string PlaylistA = "spotify:playlist:37i9dQZF1DX4sWSpwq3LiO";
    const string PlaylistB = "spotify:playlist:1TSZDcvlPtAnekTaItI3qO";
    const string AlbumB = "spotify:album:6dVIqQ8qmQ5GBnJ9shOYGE";

    static CatalogScope Scope(string account = "wiring", string locale = "en-US", string market = "US", byte tier = 0)
        => new("spotify", account, locale, market, tier, true);

    static SidebarPinLatch Latch(string loaded, string live) => new(() => loaded, () => live, () => false, _ => { });

    // ── the account key and the file name ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Key_ProviderColonAccount()
        => Assert.Equal("spotify:wiring", SidebarAccountKey.Of(Scope()));

    [Fact]
    public void Key_EmptyAccountIsSignedOut()
    {
        string key = SidebarAccountKey.Of(CatalogScope.Fake());
        Assert.Equal("", key);
        Assert.True(SidebarAccountKey.IsSignedOut(key));
    }

    [Fact]
    public void Key_MarketAndLocaleDoNotChangeIt()
    {
        // Two scopes that differ only in market, locale and tier are the same account: no swap, no reload.
        Assert.Equal(SidebarAccountKey.Of(Scope(locale: "en-US", market: "US", tier: 0)),
                     SidebarAccountKey.Of(Scope(locale: "de-DE", market: "DE", tier: 2)));
    }

    [Fact]
    public void Hash_Stable16Hex()
    {
        string h = SidebarAccountKey.Hash("spotify:wiring");
        Assert.Equal(16, h.Length);
        Assert.Matches("^[0-9a-f]{16}$", h);
        Assert.Equal(h, SidebarAccountKey.Hash("spotify:wiring"));
        Assert.NotEqual(h, SidebarAccountKey.Hash("spotify:other"));
    }

    [Fact]
    public void FileName_SignedOutIsPending()
    {
        Assert.Equal(SidebarAccountStore.PendingFileName, SidebarAccountStore.FileNameOf(""));
        Assert.Equal("sidebar.acct-" + SidebarAccountKey.Hash("spotify:wiring") + ".json",
                     SidebarAccountStore.FileNameOf("spotify:wiring"));
    }

    [Fact]
    public void SameKeyScopeSwitch_NoSwap()
    {
        // The pure half of the swap: equal keys mean EnsureAccount returns false at the key compare, before any reload.
        Assert.Equal(SidebarAccountKey.Of(Scope(market: "US")), SidebarAccountKey.Of(Scope(market: "FR", locale: "fr-FR")));
    }

    // ── the file: round trip and what a reader refuses ───────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_SyncedNamesLocalRecordsAndOrder()
    {
        var pins = new List<SidebarPin>
        {
            new("pl:" + PlaylistA, SidebarEntryKind.Playlist, PlaylistA, "A", 0),
            new("search", SidebarEntryKind.AppRoute, "", "Search", 5),
            new("module:radio", SidebarEntryKind.AppRoute, "", "Radio", 7),
            new("album:" + AlbumB, SidebarEntryKind.Album, AlbumB, "B", 0),
        };
        var data = new SidebarAccountData("spotify:a", true, pins, ["folder:f"], 42,
            [new SidebarFirstSeenDto("pl:" + PlaylistA, 9)]);

        Assert.True(SidebarAccountStore.TryParse(SidebarAccountStore.Serialize(data), "spotify:a", out var back));

        Assert.Equal(pins, back.Pins);                                  // ids, kinds, uris, names and local stamps, in order
        Assert.True(back.MigratedToServer);
        Assert.Equal(new[] { "folder:f" }, back.ExpandedFolders);
        Assert.Equal(42, back.NewReleasesSeenMs);
        Assert.Equal(9, Assert.Single(back.FirstSeen).Ms);
    }

    [Fact]
    public void Parse_DropsLikedAndHome()
    {
        // Q1a: a file that lists Home or Liked as pins still loads, with those two dropped.
        string json = "{\"v\":1,\"pins\":[\"home\",\"liked\",\"search\",\"pl:" + PlaylistA + "\"],"
                    + "\"local\":[{\"id\":\"search\",\"kind\":\"appRoute\",\"name\":\"Search\"}],"
                    + "\"names\":{\"pl:" + PlaylistA + "\":\"Mix\"}}";

        Assert.True(SidebarAccountStore.TryParse(Encoding.UTF8.GetBytes(json), "spotify:a", out var back));

        Assert.Equal(new[] { "search", "pl:" + PlaylistA }, back.Pins.Select(p => p.Id).ToArray());
        Assert.Equal("Mix", back.Pins[1].Name);
    }

    [Fact]
    public void Parse_CapsAt2000()
    {
        var pins = new List<SidebarPin>(SidebarAccountStore.MaxPins + 100);
        for (int i = 0; i < SidebarAccountStore.MaxPins + 100; i++)
            pins.Add(new SidebarPin("module:m" + i, SidebarEntryKind.AppRoute, "", "M", 0));

        Assert.True(SidebarAccountStore.TryParse(SidebarAccountStore.Serialize(new SidebarAccountData("spotify:a", false, pins, [], 0, [])),
                                                 "spotify:a", out var back));

        Assert.Equal(SidebarAccountStore.MaxPins, back.Pins.Count);
    }

    [Fact]
    public void Parse_DropsUnknownLocalKind()
    {
        string json = "{\"v\":1,\"pins\":[\"x\"],\"local\":[{\"id\":\"x\",\"kind\":\"bogus\",\"name\":\"X\"}]}";

        Assert.True(SidebarAccountStore.TryParse(Encoding.UTF8.GetBytes(json), "spotify:a", out var back));
        Assert.Empty(back.Pins);
    }

    [Fact]
    public void WrongVersion_NotParsed()
    {
        Assert.False(SidebarAccountStore.TryParse(Encoding.UTF8.GetBytes("{\"v\":2,\"pins\":[]}"), "spotify:a", out _));
        Assert.False(SidebarAccountStore.TryParse("not json"u8.ToArray(), "spotify:a", out _));
    }

    [Fact]
    public void Latch_IsPerAccount()
    {
        // Each account carries its own migration latch: one account's migration never marks another's.
        var a = new SidebarAccountData("spotify:a", true, [], [], 0, []);
        var b = new SidebarAccountData("spotify:b", false, [], [], 0, []);

        Assert.True(SidebarAccountStore.TryParse(SidebarAccountStore.Serialize(a), "spotify:a", out var backA));
        Assert.True(SidebarAccountStore.TryParse(SidebarAccountStore.Serialize(b), "spotify:b", out var backB));
        Assert.True(backA.MigratedToServer);
        Assert.False(backB.MigratedToServer);
    }

    // ── the account guard (Q1b) ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("spotify:a", "spotify:a", true)]    // the store holds the live account's pins
    [InlineData("spotify:a", "spotify:b", false)]   // the store still holds another account's pins: nothing crosses
    [InlineData("", "", false)]                     // signed out: no pins, no guard to pass
    public void PinLatch_Matches_OnlyWhenLoadedEqualsLive_AndSignedIn(string loaded, string live, bool expected)
        => Assert.Equal(expected, Latch(loaded, live).Matches);

    [Fact]
    public void NoCrossAccountApplyServer()
    {
        // The store holds A's pins while the live account is B: B's server set is refused, nothing is written, and a
        // local pin made in this window is not written to B's server either.
        var store = new SidebarPinStore();
        store.LoadFrom([new SidebarPin("pl:" + PlaylistA, SidebarEntryKind.Playlist, PlaylistA, "A's list", 0)]);
        bool migrated = true;
        var latch = new SidebarPinLatch(() => "spotify:a", () => "spotify:b", () => migrated, v => migrated = v);
        var writes = new List<(string Uri, bool Pinned)>();
        using var sync = new LibraryPinSync(store, latch, (uri, pinned) => writes.Add((uri, pinned)), _ => false);
        string[] before = store.Select(p => p.Id).ToArray();

        Assert.False(sync.ApplyServer([new PinWire(PlaylistB, 1)], converged: true));
        Assert.Equal(before, store.Select(p => p.Id).ToArray());
        Assert.Empty(writes);

        Assert.True(store.Pin(new SidebarPin("pl:" + PlaylistB, SidebarEntryKind.Playlist, PlaylistB, "B's list", 0)));
        Assert.Empty(writes);
        Assert.True(migrated);
    }
}

// ── SidebarPinStore: the one ordered pin list of the live account; identity; the canonical id scheme ───────────────────

public class SidebarPinIdentityTests
{
    static SidebarPin Pin(string id, SidebarEntryKind kind = SidebarEntryKind.Playlist,
                          string uri = PlaylistUri, string name = "n", long addedAtMs = 0)
        => new(id, kind, uri, name, addedAtMs);

    const string PlaylistUri = "spotify:playlist:37i9dQZF1DX4sWSpwq3LiO";

    [Theory]
    [InlineData("spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", "pl:spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", SidebarEntryKind.Playlist)]
    [InlineData("spotify:album:6dVIqQ8qmQ5GBnJ9shOYGE", "album:spotify:album:6dVIqQ8qmQ5GBnJ9shOYGE", SidebarEntryKind.Album)]
    [InlineData("spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", "artist:spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", SidebarEntryKind.Artist)]
    [InlineData("spotify:show:4rOoJ6Egrf8K2IrywzwOMk", "show:spotify:show:4rOoJ6Egrf8K2IrywzwOMk", SidebarEntryKind.Show)]
    public void Entity_uris_map_to_a_prefixed_id_and_back_to_their_kind(string uri, string expectedId, SidebarEntryKind expectedKind)
    {
        string? id = SidebarPinId.FromUri(uri);
        Assert.Equal(expectedId, id);
        Assert.Equal(expectedKind, SidebarPinId.KindOf(id!));
    }

    [Fact]
    public void Liked_songs_is_a_route_pin_not_a_playlist_pin()
    {
        // The one special case: the Liked Songs collection uri is the "liked" ROUTE, because the pin id IS the nav key.
        string? id = SidebarPinId.FromUri("spotify:collection:tracks");
        Assert.Equal("liked", id);
        Assert.Equal(SidebarEntryKind.AppRoute, SidebarPinId.KindOf(id!));
        Assert.Equal("liked", SidebarPinId.Canonical("spotify:collection:tracks"));
    }

    [Fact]
    public void Canonical_maps_a_bare_entity_uri_onto_the_prefixed_pin_id()
    {
        Assert.Equal("pl:spotify:playlist:x", SidebarPinId.Canonical("spotify:playlist:x"));
        Assert.Equal("pl:spotify:playlist:x", SidebarPinId.Canonical("pl:spotify:playlist:x"));
        Assert.Equal("album:spotify:album:x", SidebarPinId.Canonical("spotify:album:x"));
        Assert.Equal("artist:spotify:artist:x", SidebarPinId.Canonical("spotify:artist:x"));
        Assert.Null(SidebarPinId.Canonical("spotify:track:x"));
    }

    [Fact]
    public void Pinnable_routes_are_their_own_pin_ids_and_shell_surfaces_are_not_destinations()
    {
        for (int i = 0; i < SidebarPinId.PinnableRoutes.Length; i++)
        {
            string route = SidebarPinId.PinnableRoutes[i];
            Assert.Equal(route, SidebarPinId.FromRoute(route));
            Assert.Equal(SidebarEntryKind.AppRoute, SidebarPinId.KindOf(route));
        }
        Assert.Null(SidebarPinId.FromRoute("settings"));
        Assert.False(SidebarPinId.IsPinnableRoute("settings"));
        Assert.Null(SidebarPinId.FromRoute("playback-diagnostics"));
        Assert.Null(SidebarPinId.FromRoute(""));
        Assert.Null(SidebarPinId.FromRoute(null));

        // FromRoute is the app's route recogniser, not just a policy filter: anything it does not recognise is refused.
        Assert.Null(SidebarPinId.FromRoute("acme:widget:7"));
        Assert.Null(SidebarPinId.FromRoute("not-a-route"));
        Assert.Null(SidebarPinId.FromRoute("spotify:album:5"));                    // an entity URI is FromUri's job
        Assert.Null(SidebarPinId.FromRoute("concert:spotify:concert:9"));          // one dated event is not a destination
    }

    [Fact]
    public void Recents_is_pinnable_as_a_route()
    {
        Assert.Contains("recents", SidebarPinId.PinnableRoutes);
        Assert.Equal("recents", SidebarPinId.FromRoute("recents"));
        Assert.True(SidebarPinId.IsPinnableRoute("recents"));
        Assert.Equal(SidebarEntryKind.AppRoute, SidebarPinId.KindOf("recents"));
        Assert.Equal("recents", SidebarPinId.RouteOf("recents"));
        Assert.Equal("", SidebarPinId.UriOf("recents"));            // a route pin carries no entity uri
    }

    [Fact]
    public void Folder_pins_by_the_rootlist_group_id_and_never_navigates()
    {
        string id = SidebarPinId.ForFolder("6a1f2c");
        Assert.Equal("folder:6a1f2c", id);
        Assert.Equal(SidebarEntryKind.Folder, SidebarPinId.KindOf(id));
        Assert.Equal("6a1f2c", SidebarPinId.FolderIdOf(id));
        Assert.Null(SidebarPinId.RouteOf(id));                      // a folder expands in place: it has no route
        Assert.Equal("", SidebarPinId.UriOf(id));

        // Every other kind's id IS its route key, which is what makes the recency join an identity lookup.
        string pl = SidebarPinId.FromUri(PlaylistUri)!;
        Assert.Equal(pl, SidebarPinId.RouteOf(pl));
    }

    /// <summary>Folder CRUD is live, so a pinned folder can genuinely VANISH under its pin. The store keeps it: a missing
    /// entity renders visible-but-disabled with a reason, and only an explicit unpin removes a user's row.</summary>
    [Fact]
    public void A_pin_to_a_vanished_folder_is_kept()
    {
        var s = new SidebarPinStore();
        string id = SidebarPinId.ForFolder("6a1f2c");
        Assert.True(s.Pin(Pin(id, SidebarEntryKind.Folder, "", "Late night")));

        // The folder is deleted on another device: nothing in the store is told, and nothing in the store reacts.
        Assert.True(s.IsPinned(id));
        Assert.Equal(0, s.IndexOf(id));
        Assert.Equal("Late night", s[0].Name);                      // the offline display cache still names the row

        // A rename does not disturb it either: the pin is keyed by the client-minted group id.
        s.Touch(id, "Very late night");
        Assert.True(s.IsPinned(id));
    }

    [Fact]
    public void IsPinned_uses_the_stable_id_so_a_rename_cannot_unpin()
    {
        var s = new SidebarPinStore();
        string id = SidebarPinId.FromUri(PlaylistUri)!;
        Assert.True(s.Pin(Pin(id, SidebarEntryKind.Playlist, PlaylistUri, "Peaceful Piano")));

        s.Touch(id, "Peaceful Piano (2026)");
        Assert.True(s.IsPinned(id));
        Assert.Equal(0, s.IndexOf(id));
    }

    [Fact]
    public void A_raw_uri_pin_is_found_and_removed_through_its_canonical_id()
    {
        // Card drops used to persist the bare uri as SidebarPin.Id. The menu looks up pl:… and without the alias those
        // pins were immortal (Pin was a silent no-op, Unpin never appeared).
        var s = new SidebarPinStore();
        Assert.True(s.Pin(Pin("spotify:playlist:stuck", SidebarEntryKind.Playlist, "spotify:playlist:stuck", "My Playlist #6")));
        Assert.Equal("pl:spotify:playlist:stuck", s[0].Id);         // Pin canonicalizes on the way in
        Assert.True(s.IsPinned("spotify:playlist:stuck"));
        Assert.True(s.IsPinned("pl:spotify:playlist:stuck"));
        Assert.Equal(0, s.Unpin("pl:spotify:playlist:stuck"));
        Assert.Empty(s);
    }

    [Fact]
    public void LoadFrom_migrates_a_legacy_raw_uri_pin_and_dedupes_the_prefixed_twin()
    {
        var s = new SidebarPinStore();
        s.LoadFrom(
        [
            Pin("spotify:playlist:stuck", SidebarEntryKind.Playlist, "spotify:playlist:stuck", "My Playlist #6"),
            Pin("pl:spotify:playlist:stuck", SidebarEntryKind.Playlist, "spotify:playlist:stuck", "My Playlist #6"),
        ]);
        Assert.Equal(new[] { "pl:spotify:playlist:stuck" }, s.Select(p => p.Id).ToArray());
        Assert.Equal(0, s.Unpin("spotify:playlist:stuck"));
        Assert.Empty(s);
    }
}

// ── SidebarPinStore: the one ordered pin list of the live account (Pin, Unpin, Move, Touch, LoadFrom, ApplyRemote) ─────────

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

    // ── identity, order, idempotent pin/unpin ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pin_appends_at_the_end()
    {
        var s = StoreOf("a", "b", "c");
        Assert.Equal(new[] { "a", "b", "c" }, IdsOf(s));
        Assert.Equal(2, s.IndexOf("c"));
    }

    [Fact]
    public void Pinning_twice_is_deduped_and_keeps_the_original_position()
    {
        var s = StoreOf("a", "b", "c");
        int version = s.Version.Peek();

        // A second Pin of the same id is a silent no-op (the menu shows Unpin in that state) — it is a MOVE candidate
        // that never happens, so a double invoke can never reorder the list.
        Assert.False(s.Pin(Pin("a", name: "renamed")));
        Assert.Equal(new[] { "a", "b", "c" }, IdsOf(s));
        Assert.Equal(0, s.IndexOf("a"));
        Assert.Equal("n", s[0].Name);                 // the rejected pin did not overwrite the cached display name
        Assert.Equal(version, s.Version.Peek());      // a rejected mutation does not bump the version (no commit, no re-render)
    }

    [Fact]
    public void Unpin_returns_the_former_index_for_the_undo_restore()
    {
        var s = StoreOf("a", "b", "c");
        Assert.Equal(1, s.Unpin("b"));
        Assert.Equal(new[] { "a", "c" }, IdsOf(s));
        Assert.Equal(1, s.IndexOf("c"));              // the tail was reindexed
    }

    [Fact]
    public void Unpinning_a_missing_id_is_a_no_op()
    {
        var s = StoreOf("a");
        int version = s.Version.Peek();
        Assert.Equal(-1, s.Unpin("nope"));
        Assert.Equal(-1, s.Unpin(null));
        Assert.Single(s);
        Assert.Equal(version, s.Version.Peek());
    }

    [Fact]
    public void Insert_restores_at_the_former_index_and_clamps_out_of_range()
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
    public void Move_reorders_within_the_list()
    {
        var s = StoreOf("a", "b", "c", "d");

        s.Move(0, 2);                                  // forward
        Assert.Equal(new[] { "b", "c", "a", "d" }, IdsOf(s));

        s.Move(3, 1);                                  // backward (from > to)
        Assert.Equal(new[] { "b", "d", "c", "a" }, IdsOf(s));

        s.Move(0, s.Count);                            // to == count -> "move to the end", clamped, never out of range
        Assert.Equal(new[] { "d", "c", "a", "b" }, IdsOf(s));

        for (int i = 0; i < s.Count; i++) Assert.Equal(i, s.IndexOf(s[i].Id));   // the index map tracked every move
    }

    [Fact]
    public void A_no_op_or_out_of_range_move_does_not_bump_the_version()
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
    public void Pins_are_unlimited()
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
    public void Pin_order_is_stable_across_unrelated_mutations()
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
    public void Route_and_entity_pins_coexist_in_insertion_order()
    {
        var s = new SidebarPinStore();
        Assert.True(s.Pin(Pin("search", SidebarEntryKind.AppRoute, "", "Search")));
        Assert.True(s.Pin(Pin("pl:spotify:playlist:1", SidebarEntryKind.Playlist)));
        Assert.True(s.Pin(Pin("folder:6a1f2c", SidebarEntryKind.Folder, "", "Cafe & chill")));
        Assert.True(s.Pin(Pin("artist:spotify:artist:1", SidebarEntryKind.Artist, "spotify:artist:1", "Daft Punk")));

        Assert.Equal(new[] { "search", "pl:spotify:playlist:1", "folder:6a1f2c", "artist:spotify:artist:1" }, IdsOf(s));
        Assert.Equal(SidebarEntryKind.AppRoute, s[0].Kind);
        Assert.Equal(SidebarEntryKind.Folder, s[2].Kind);
    }

    [Fact]
    public void Touch_refreshes_the_cached_name_without_bumping_the_version()
    {
        // A display-cache refresh must never commit on its own and must never invalidate a render mid-projection —
        // it reports "changed" to the caller but never bumps the version.
        var s = StoreOf("a");
        int version = s.Version.Peek();

        Assert.True(s.Touch("a", "New Name"));
        Assert.Equal("New Name", s[0].Name);
        Assert.Equal(version, s.Version.Peek());

        Assert.False(s.Touch("a", "New Name"));       // unchanged -> no-op
        Assert.False(s.Touch("a", ""));               // an empty name is never a refresh
        Assert.False(s.Touch("missing", "x"));
    }

    [Fact]
    public void LoadFrom_drops_idless_and_duplicate_rows()
    {
        // A hand-edited document must never produce two rows with one identity.
        var s = new SidebarPinStore();
        s.LoadFrom([Pin("a"), Pin(""), Pin("b"), Pin("a", name: "dupe")]);
        Assert.Equal(new[] { "a", "b" }, IdsOf(s));
        Assert.Equal("n", s[0].Name);
    }

    [Fact]
    public void OnChanged_fires_for_accepted_mutations_only()
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

    // ── OnLocalPinChanged + ApplyRemote (the server-membership convergence path) ────────────────────────────────────

    [Fact]
    public void OnLocalPinChanged_fires_for_pin_insert_and_unpin_with_the_right_flag_but_not_for_move_touch_or_load_from()
    {
        var s = new SidebarPinStore();
        var events = new List<(string Id, bool Pinned)>();
        s.OnLocalPinChanged = (p, pinned) => events.Add((p.Id, pinned));

        Assert.True(s.Pin(Pin("a")));
        Assert.False(s.Pin(Pin("a")));                  // rejected — no event
        Assert.True(s.Insert(Pin("b"), 0));
        int at = s.Unpin("a");
        Assert.True(s.Insert(Pin("a"), at));             // the undo re-insert also raises the event (true)

        s.Move(0, 1);                                    // no event — order is local by design
        s.Touch("a", "renamed");                         // no event — a display-cache refresh is not user intent
        s.LoadFrom([Pin("c")]);                           // no event — startup load is not user intent

        Assert.Equal(
            new[] { ("a", true), ("b", true), ("a", false), ("a", true) },
            events.Select(e => (e.Id, e.Pinned)).ToArray());
    }

    [Fact]
    public void ApplyRemote_never_raises_OnLocalPinChanged()
    {
        var s = new SidebarPinStore();
        bool fired = false;
        s.OnLocalPinChanged = (_, _) => fired = true;

        s.ApplyRemote([Pin("pl:spotify:playlist:1")], _ => true, removeMissing: false);
        Assert.False(fired);
    }

    [Fact]
    public void ApplyRemote_appends_missing_pins_in_the_given_order_and_keeps_existing_order()
    {
        var s = StoreOf("a");
        bool changed = s.ApplyRemote(
            [Pin("pl:spotify:playlist:1"), Pin("pl:spotify:playlist:2")],
            _ => true, removeMissing: false);
        Assert.True(changed);
        Assert.Equal(new[] { "a", "pl:spotify:playlist:1", "pl:spotify:playlist:2" }, IdsOf(s));
    }

    [Fact]
    public void ApplyRemote_with_remove_missing_false_removes_nothing()
    {
        var s = StoreOf("pl:spotify:playlist:1", "pl:spotify:playlist:2");
        bool changed = s.ApplyRemote([Pin("pl:spotify:playlist:1")], _ => true, removeMissing: false);
        Assert.False(changed);
        Assert.Equal(new[] { "pl:spotify:playlist:1", "pl:spotify:playlist:2" }, IdsOf(s));
    }

    [Fact]
    public void ApplyRemote_with_remove_missing_true_removes_only_syncable_ids_missing_from_the_server()
    {
        var s = new SidebarPinStore();
        Assert.True(s.Pin(Pin("pl:spotify:playlist:1")));
        Assert.True(s.Pin(Pin("pl:spotify:playlist:2")));      // missing from server, syncable → removed
        Assert.True(s.Pin(Pin("folder:x", SidebarEntryKind.Folder, "", "F")));   // missing, NOT syncable → survives
        Assert.True(s.Pin(Pin("search", SidebarEntryKind.AppRoute, "", "Search")));  // missing, NOT syncable → survives

        bool changed = s.ApplyRemote(
            [Pin("pl:spotify:playlist:1")],
            isSyncable: id => id.StartsWith("pl:", StringComparison.Ordinal),
            removeMissing: true);

        Assert.True(changed);
        Assert.Equal(new[] { "pl:spotify:playlist:1", "folder:x", "search" }, IdsOf(s));
    }

    [Fact]
    public void ApplyRemote_bumps_the_version_once_and_commits_once_per_call_and_returns_false_when_nothing_changed()
    {
        var s = StoreOf("pl:spotify:playlist:1");
        int commits = 0;
        s.OnChanged = () => commits++;
        int versionBefore = s.Version.Peek();

        Assert.True(s.ApplyRemote([Pin("pl:spotify:playlist:1"), Pin("pl:spotify:playlist:2")], _ => true, false));
        Assert.Equal(1, commits);
        Assert.Equal(versionBefore + 1, s.Version.Peek());

        // A second, no-op call (nothing new, nothing removed) neither commits nor bumps.
        Assert.False(s.ApplyRemote([Pin("pl:spotify:playlist:1"), Pin("pl:spotify:playlist:2")], _ => true, false));
        Assert.Equal(1, commits);
        Assert.Equal(versionBefore + 1, s.Version.Peek());
    }
}
