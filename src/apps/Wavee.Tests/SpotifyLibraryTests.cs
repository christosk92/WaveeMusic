// ── Wavee.Tests/SpotifyLibraryTests.cs — the library host's CORE half and the staging arms it needs ─────────────────
//
// Gap batch B2. `Spotify.Library.cs` is SHELL and is not driven here; everything it decides is, as pure values over a
// fresh in-memory scope:
//   · G-062  the staged `Relation.Pins` arm (cross-kind targets, the kind byte, the non-row pins, the refused shapes),
//            the generic `Relation.Rootlist` arm's folder id + ownership, `User.ReplaceRootlist`'s AddRef, the pin kind
//            `User.Add` writes;
//   · G-043  the live rootlist edge ↔ marker stream round trip a rootlist write indexes into (`RootlistEntries`,
//            `LandRootlist`), position gaps included;
//   · G-049  the Pins edge read back as wire uris (`PinWires`);
//   · G-042  when a login syncs (`LibrarySyncRules`) and which relation a dealer push names (`LibraryPushRules`);
//   · G-048  the owned rootlist rows' capability block (`LibraryCaps`);
//   · G-089  a saved prerelease as a drop link (`LibraryDrops`).
// Every class here touches `Entities`, so it joins `EntitiesCollection`.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class SpotifyLibraryTests
{
    static EntityId Gid(EntityKind kind, byte seed, EntityIdFlags flags = EntityIdFlags.None)
    {
        Span<byte> gid = stackalloc byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return EntityId.ForGid(kind, gid, flags);
    }

    /// <summary>The account row, and the scope pointed at it (EdgesStagingTests' own shape).</summary>
    static User Me()
    {
        var me = Entities.User(EntityUri.Parse("spotify:user:b2-tester".AsSpan()));
        Entities.Current.MeSlot = me.Slot;
        return me;
    }

    static StringId Owned(string text) => Entities.Strings.Intern(text);

    // ── G-062: the pins arm ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_pins_run_lands_every_representable_kind_with_its_kind_byte_and_drops_the_rest()
    {
        TestScope.Fresh();
        var me = Me();
        var s = Staging.Rent();
        var run = s.Run(Relation.Pins);
        run.Add(Gid(EntityKind.Playlist, 10)).At = 100;
        run.Add(Gid(EntityKind.Album, 20)).At = 200;
        run.Add(s.Text("spotify:collection")).At = 300;
        run.Add(s.Text("spotify:folder:abc123")).At = 400;
        run.Add(Gid(EntityKind.Track, 30)).At = 500;                                    // a track is never a pin
        run.Add(Gid(EntityKind.Album, 40, EntityIdFlags.Prerelease)).At = 600;          // an album row, but never a pin
        run.Add(s.Text("spotify:collection:your-episodes")).At = 700;                  // not the Liked collection
        run.Add(s.Text("spotify:station:x")).At = 800;
        StagedId parent = me.Id;
        run.End(in parent);
        TestScope.CommitAndPublish(s);

        Assert.Equal(4, me.PinSlots.Length);
        Assert.Equal(new[] { PinKind.Playlist, PinKind.Album, PinKind.Liked, PinKind.Folder },
                     Enumerable.Range(0, 4).Select(i => me.PinKindAt(i)).ToArray());
        Assert.Equal(Entities.Playlist(Gid(EntityKind.Playlist, 10)).Slot, me.PinSlots[0]);
        Assert.Equal(Entities.Album(Gid(EntityKind.Album, 20)).Slot, me.PinSlots[1]);
        Assert.Equal(Table.None, me.PinSlots[2]);
        Assert.Equal("abc123", Entities.Strings.Resolve(new StringId(me.PinSlots[3])));
        Assert.Equal(new[] { 100, 200, 300, 400 }, me.PinEdges.ToArray().Select(e => e.AddedAt).ToArray());
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.Pins.State(me.Slot));
    }

    [Fact]
    public void PinWires_reads_the_edge_back_as_the_wire_uris_the_bridge_converges()
    {
        TestScope.Fresh();
        var me = Me();
        var s = Staging.Rent();
        var run = s.Run(Relation.Pins);
        run.Add(Gid(EntityKind.Playlist, 10)).At = 5;
        run.Add(s.Text("spotify:collection")).At = 6;
        run.Add(s.Text("spotify:folder:abc123")).At = 7;
        StagedId parent = me.Id;
        run.End(in parent);
        TestScope.CommitAndPublish(s);

        var wires = new List<PinWire>();
        Spotify.Encode.PinWires(me, wires);

        Assert.Equal(new[]
        {
            new PinWire(Gid(EntityKind.Playlist, 10).Text, 5000),
            new PinWire("spotify:collection", 6000),
            new PinWire("spotify:folder:abc123", 7000),
        }, wires.ToArray());
    }

    [Fact]
    public void Adding_a_pin_through_the_user_writes_its_kind_byte()
    {
        TestScope.Fresh();
        var me = Me();
        var album = Gid(EntityKind.Album, 20);
        me.Add(LibraryEdgeKind.Pins, Entities.Album(album).Slot, album);   // a fake scope: the model half only, no network

        Assert.Equal(PinKind.Album, me.PinKindAt(0));
        Assert.Equal(EdgePending.Add, me.PendingOf(LibraryEdgeKind.Pins, Entities.Album(album).Slot));
    }

    [Theory]
    [InlineData("spotify:collection", true)]
    [InlineData("spotify:collection:tracks", true)]
    [InlineData("spotify:user:bob:collection", true)]
    [InlineData("spotify:collection:your-episodes", false)]
    [InlineData("spotify:playlist:x", false)]
    public void The_liked_pin_spellings(string uri, bool liked)
        => Assert.Equal(liked, User.IsLikedPinUri(System.Text.Encoding.UTF8.GetBytes(uri)));

    [Theory]
    [InlineData("spotify:folder:abc123", "abc123")]
    [InlineData("spotify:folder:", "")]
    [InlineData("spotify:folder:not-hex", "")]
    [InlineData("spotify:playlist:abc", "")]
    public void The_folder_pin_id(string uri, string id)
        => Assert.Equal(id, System.Text.Encoding.UTF8.GetString(User.FolderIdOf(System.Text.Encoding.UTF8.GetBytes(uri))));

    // ── G-062: the rootlist arms own their folder text ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_generic_rootlist_arm_lands_the_folder_id_and_owns_both_strings()
    {
        TestScope.Fresh();
        var me = Me();
        var s = Staging.Rent();
        var run = s.Run(Relation.Rootlist);
        ref var start = ref run.Add();
        start.B1 = (byte)RootlistKind.FolderStart;
        start.Text = s.Text("SpotifyLibraryTests/generic-folder");
        start.Aux = s.Text("feedface00112233");
        ref var item = ref run.Add(Gid(EntityKind.Playlist, 10));
        item.U0 = 1;
        item.B0 = 1;
        ref var end = ref run.Add();
        end.U0 = 2;
        end.B1 = (byte)RootlistKind.FolderEnd;
        end.Aux = s.Text("feedface00112233");
        StagedId parent = me.Id;
        run.End(in parent);
        TestScope.CommitAndPublish(s);

        var rows = me.Rootlist;
        Assert.Equal(3, rows.Length);
        Assert.Equal("feedface00112233", Entities.Strings.Resolve(rows[0].FolderId));
        Assert.Equal("feedface00112233", Entities.Strings.Resolve(rows[2].FolderId));
        StringId name = rows[0].FolderName;
        Assert.Equal("SpotifyLibraryTests/generic-folder", Entities.Strings.Resolve(name));

        // Rewrite the list without the folder: the name was OWNED, so it is given back (a re-intern mints a new id).
        var again = Staging.Rent();
        var empty = again.Run(Relation.Rootlist);
        empty.EndEvenIfEmpty(in parent);
        TestScope.CommitAndPublish(again);
        Assert.Equal(0, me.Rootlist.Length);
        Assert.NotEqual(name, Owned("SpotifyLibraryTests/generic-folder"));
    }

    [Fact]
    public void ReplaceRootlist_retains_the_folder_strings_it_is_handed_and_releases_the_list_it_replaces()
    {
        TestScope.Fresh();
        var me = Me();
        StringId name = Owned("SpotifyLibraryTests/replace-folder");
        StringId id = Owned("0123456789abcdef");
        RootlistEdge[] folder =
        [
            new(0, 0, (byte)RootlistKind.FolderStart, name, 0, id),
            new(1, 0, (byte)RootlistKind.FolderEnd, default, 0, id),
        ];
        me.ReplaceRootlist([Table.None, Table.None], folder);
        // The same list again: AddRef first, release second — the strings survive a rewrite that keeps them.
        me.ReplaceRootlist([Table.None, Table.None], folder);
        Assert.Equal(name, Owned("SpotifyLibraryTests/replace-folder"));

        me.ReplaceRootlist([], []);
        Assert.NotEqual(name, Owned("SpotifyLibraryTests/replace-folder"));
    }

    // ── G-043: the live edge ↔ the marker stream ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_landed_stream_reads_back_as_the_same_stream()
    {
        TestScope.Fresh();
        var me = Me();
        string a = Gid(EntityKind.Playlist, 10).Text, b = Gid(EntityKind.Playlist, 11).Text;
        var stream = Spotify.Encode.EntriesFromUris(
            [a, "spotify:start-group:0011aabbccddeeff:Road+Trips", b, "spotify:end-group:0011aabbccddeeff"],
            [0, 1786796469000, 0, 1786796469000]);

        Spotify.Encode.LandRootlist(me, stream);
        var back = new List<RootlistEntry>();
        Spotify.Encode.RootlistEntries(me, back);

        Assert.Equal(stream.Select(e => (e.Kind, e.Uri, e.Depth)).ToArray(), back.Select(e => (e.Kind, e.Uri, e.Depth)).ToArray());
        Assert.Equal("Road Trips", back[1].GroupName);
        Assert.Equal(1786796469000, back[1].AddedAtMs);                     // the create stamp a rename must resend
        Assert.Equal("0011aabbccddeeff", Entities.Strings.Resolve(me.Rootlist[3].FolderId));
    }

    [Fact]
    public void A_position_gap_stays_an_index_the_ops_count()
    {
        TestScope.Fresh();
        var me = Me();
        int p = Entities.Playlist(Gid(EntityKind.Playlist, 10)).Slot;
        int q = Entities.Playlist(Gid(EntityKind.Playlist, 11)).Slot;
        me.ReplaceRootlist([p, q], [new RootlistEdge(0, 0, 0, default, 0), new RootlistEdge(2, 0, 0, default, 0)]);

        var entries = new List<RootlistEntry>();
        Spotify.Encode.RootlistEntries(me, entries);

        Assert.Equal(new[] { 0, Spotify.Encode.OtherKind, 0 }, entries.Select(e => e.Kind).ToArray());
        Assert.Equal(2, Spotify.Encode.FindPlaylistIndex(entries, Gid(EntityKind.Playlist, 11).Text));
    }

    // ── G-042: when a login syncs ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_login_syncs_once_per_session_epoch_and_scope()
    {
        var memo = default(LibrarySyncMemo);
        Assert.Equal(LibrarySyncVerdict.None, LibrarySyncRules.Decide(ref memo, online: false, accountScope: true, 1, 1, 0));
        Assert.Equal(LibrarySyncVerdict.None, LibrarySyncRules.Decide(ref memo, online: true, accountScope: false, 1, 1, 0));
        Assert.Equal(LibrarySyncVerdict.Sync, LibrarySyncRules.Decide(ref memo, true, true, 1, 1, 1_000));
        Assert.Equal(LibrarySyncVerdict.None, LibrarySyncRules.Decide(ref memo, true, true, 1, 1, 2_000));
        // A scope switch (a market change) syncs at once, whatever the window says.
        Assert.Equal(LibrarySyncVerdict.Sync, LibrarySyncRules.Decide(ref memo, true, true, 1, 2, 3_000));
    }

    [Fact]
    public void A_reconnect_of_the_same_scope_is_rate_limited_inside_the_window()
    {
        var memo = default(LibrarySyncMemo);
        Assert.Equal(LibrarySyncVerdict.Sync, LibrarySyncRules.Decide(ref memo, true, true, 1, 1, 1_000));
        Assert.Equal(LibrarySyncVerdict.RateLimited, LibrarySyncRules.Decide(ref memo, true, true, 2, 1, 10_000));
        Assert.Equal(LibrarySyncVerdict.None, LibrarySyncRules.Decide(ref memo, true, true, 2, 1, 11_000));
        Assert.Equal(LibrarySyncVerdict.Sync,
                     LibrarySyncRules.Decide(ref memo, true, true, 3, 1, 1_000 + LibrarySyncRules.ReconnectWindowMs));
    }

    [Theory]
    [InlineData("hm://collection/collection/bob/json", LibraryPush.Liked | LibraryPush.SavedAlbums)]
    [InlineData("hm://collection/artist/bob", LibraryPush.FollowedArtists)]
    [InlineData("hm://collection/show/bob/json", LibraryPush.SavedShows)]
    [InlineData("hm://collection/ylpin/bob", LibraryPush.Pins)]
    [InlineData("hm://collection/listenlater/bob", LibraryPush.None)]
    [InlineData("hm://playlist/v2/user/bob/rootlist", LibraryPush.Rootlist)]
    [InlineData("hm://playlist/user/bob/rootlist/", LibraryPush.Rootlist)]
    [InlineData("hm://playlist/v2/playlist/37i9dQZF1DXcBWIGoYBM5M", LibraryPush.None)]
    [InlineData("hm://connect-state/v1/cluster", LibraryPush.None)]
    public void A_dealer_push_names_its_relations(string topic, LibraryPush expected)
        => Assert.Equal(expected, LibraryPushRules.Classify(System.Text.Encoding.UTF8.GetBytes(topic)));

    // ── G-048: the owned rows' capabilities ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_owned_rootlist_playlist_with_unknown_capabilities_reads_owned_and_editable()
    {
        TestScope.Fresh();
        var me = Me();
        var mine = Gid(EntityKind.Playlist, 10);
        var theirs = Gid(EntityKind.Playlist, 11);
        var s = Staging.Rent();
        ref var own = ref s.Playlists.RowFor(new StagedId(mine), Authority.Full, (uint)PlaylistFields.Identity);
        own.Title = s.Text("Mine");
        own.OwnerUri = new StagedId(me.Id);
        ref var other = ref s.Playlists.RowFor(new StagedId(theirs), Authority.Full, (uint)PlaylistFields.Identity);
        other.Title = s.Text("Theirs");
        other.OwnerUri = s.Text("spotify:user:someone-else");
        TestScope.CommitAndPublish(s);
        int mineSlot = Entities.Playlist(mine).Slot, theirsSlot = Entities.Playlist(theirs).Slot;
        me.ReplaceRootlist([mineSlot, theirsSlot], [new RootlistEdge(0, 0, 0, default, 0), new RootlistEdge(1, 0, 0, default, 0)]);

        Assert.False(new Playlist(mineSlot).Editable);                            // a thin header: unknown is not editable
        var caps = Staging.Rent();
        Assert.Equal(1, LibraryCaps.StageOwned(Entities.Current, caps));
        TestScope.CommitAndPublish(caps);

        Assert.True(new Playlist(mineSlot).IsOwner);
        Assert.True(new Playlist(mineSlot).Editable);
        Assert.False(new Playlist(theirsSlot).Knows(PlaylistFields.Capabilities));  // nobody's guess
        Assert.Equal(0, LibraryCaps.StageOwned(Entities.Current, Staging.Rent()));  // stated once, then known
    }

    // ── G-089: a saved prerelease as a drop ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_saved_prerelease_is_a_drop_link_upcoming_until_its_instant()
    {
        TestScope.Fresh();
        var me = Me();
        var pre = Gid(EntityKind.Album, 40, EntityIdFlags.Prerelease);
        var plain = Gid(EntityKind.Album, 50);
        var s = Staging.Rent();
        ref var row = ref s.Albums.RowFor(new StagedId(pre), Authority.Full, (uint)(AlbumFields.Identity | AlbumFields.Release));
        row.Title = s.Text("Soon");
        row.ReleaseAt = 2_000_000_000;
        ref var old = ref s.Albums.RowFor(new StagedId(plain), Authority.Full, (uint)AlbumFields.Identity);
        old.Title = s.Text("Out");
        TestScope.CommitAndPublish(s);
        me.Replace(LibraryEdgeKind.SavedAlbums, [Entities.Album(pre).Slot, Entities.Album(plain).Slot],
                   [new LibraryEdge(1, 0), new LibraryEdge(2, 0)]);

        var links = new List<Notify.DropLink>();
        LibraryDrops.Resolve(Entities.Current, 1_900_000_000, links, static _ => "cover");

        var link = Assert.Single(links);
        Assert.Equal(pre, link.PreRelease.Id);
        Assert.False(link.Play.IsValid);                                        // a prerelease id names no playable album
        Assert.Equal("Soon", link.Name);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000), link.ReleaseAt);
        Assert.True(link.IsUpcoming);
        Assert.Equal("cover", link.CoverUrl);
        Assert.False(LibraryDrops.LinkOf(new Album(Entities.Album(pre).Slot), 2_100_000_000).IsUpcoming);
    }
}
