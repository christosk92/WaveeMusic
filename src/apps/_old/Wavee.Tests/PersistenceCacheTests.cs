using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Core;
using EntityKind = Wavee.Backend.Metadata.EntityKind;   // the PERSISTED transport vocabulary (Wavee.Core.EntityKind is the routing one)
using Xunit;

namespace Wavee.Tests;

// CachedStore dual-write + reload logic, tested against a memory cold tier (no SQLite, fully deterministic).
public class CachedStoreTests
{
    // INTERNAL, not private: the show-paging suite drives a REAL CachedStore over this same double to prove the
    // recent-surface pin actually reaches the cold tier (the IStore member is a no-op DEFAULT, so an in-memory store
    // proves nothing about it).
    internal sealed class MemCold : IColdStore
    {
        public readonly Dictionary<string, (EntityKind Kind, byte[] Payload)> Entities = new();
        public readonly Dictionary<(string, string), SyncState> Saved = new();
        public IEnumerable<ColdEntity> LoadAllEntities() { foreach (var kv in Entities) yield return new ColdEntity(kv.Key, kv.Value.Kind, kv.Value.Payload); }
        public IEnumerable<ColdSaved> LoadAllSaved() { foreach (var kv in Saved) yield return new ColdSaved(kv.Key.Item1, kv.Key.Item2, kv.Value); }
        public int EntityWrites;
        public void UpsertEntity(string uri, EntityKind kind, byte[] payload) { EntityWrites++; Entities[uri] = (kind, payload); }
        // schema-v5 cache-tier side tables the artist thin split uses (raw JSON — the fmt framing is SQLite-side).
        public readonly Dictionary<string, byte[]> Overviews = new();
        public readonly Dictionary<string, IReadOnlyList<string>> Refs = new();
        public readonly List<ColdRecentSurface> Recent = new();
        public ColdArtistOverview? GetArtistOverview(string uri)
            => Overviews.TryGetValue(uri, out var p) ? new ColdArtistOverview(uri, "", p, 0) : null;
        public void UpsertArtistOverview(string uri, string locale, byte[] payloadJson, long nowUnixSeconds) => Overviews[uri] = payloadJson;
        public void ReplaceEntityRefs(string parentUri, IEnumerable<string> children) => Refs[parentUri] = new List<string>(children);
        public IReadOnlyList<ColdRecentSurface> LoadRecentSurfaces() => Recent;
        public void UpsertRecentSurface(string uri, int kind, long nowUnixSeconds) => Recent.Add(new ColdRecentSurface(uri, kind, nowUnixSeconds));
        public readonly Dictionary<string, byte[]> VideoAssoc = new();
        public IEnumerable<ColdVideoAssoc> LoadAllVideoAssociations() { foreach (var kv in VideoAssoc) yield return new ColdVideoAssoc(kv.Key, kv.Value); }
        public void UpsertVideoAssociation(string uri, byte[] payload) => VideoAssoc[uri] = payload;
        public void UpsertSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs = 0) { if (saved) Saved[(setId, uri)] = sync; else Saved.Remove((setId, uri)); }
        public readonly Dictionary<string, string?> Revisions = new();
        public string? GetCollectionRevision(string setId) => Revisions.TryGetValue(setId, out var r) ? r : null;
        public void SetCollectionRevision(string setId, string? revision, long syncedAt) => Revisions[setId] = revision;
        public byte[]? RootlistRev;
        public byte[]? GetRootlistRevision() => RootlistRev;
        public void SetRootlistRevision(byte[]? rev) => RootlistRev = rev;
        public readonly Dictionary<string, (IReadOnlyList<ColdPlaylistItem> Rows, byte[]? Rev)> Membership = new();
        public IReadOnlyList<ColdPlaylistItem> LoadMembership(string playlistUri) => Membership.TryGetValue(playlistUri, out var m) ? m.Rows : Array.Empty<ColdPlaylistItem>();
        public void ReplaceMembership(string playlistUri, IReadOnlyList<ColdPlaylistItem> rows, byte[]? baseRev) => Membership[playlistUri] = (rows, baseRev);
        public byte[]? GetPlaylistRevision(string playlistUri) => Membership.TryGetValue(playlistUri, out var m) ? m.Rev : null;
        public IReadOnlyList<ColdRootlistEntry> Rootlist = Array.Empty<ColdRootlistEntry>();
        public IReadOnlyList<ColdRootlistEntry> LoadRootlist() => Rootlist;
        public void ReplaceRootlist(IReadOnlyList<ColdRootlistEntry> entries) => Rootlist = entries;
        public void Flush() { }
        public void Dispose() { }
    }

    static Track Trk(string id) => new(id, "spotify:track:" + id, "Title " + id,
        [new ArtistRef("a", "spotify:artist:a", "Artist")], new AlbumRef("al", "spotify:album:al", "Album"), 1000, false, null);

    // ── owners (P4-C) ───────────────────────────────────────────────────────────────────────────────────────────────
    // An owner is the ONE entity kind that bypasses the pin-reachability gate: a row is ~150 B, the pin question is
    // unanswerable cheaply (an owner is reachable through any playlist header or any membership row's added_by, and
    // neither mirror is keyed by user), and an owner that is NOT on disk makes every byline render a raw base62 id on
    // the next offline launch. They age out through the ordinary unpinned 30-day entity TTL instead.
    [Fact]
    public void UpsertOwner_PersistsWithoutAPin_AndReadsBackAfterARestart()
    {
        var cold = new MemCold();
        var store = new CachedStore(cold);
        store.UpsertOwner(new Owner("alice", "Alice", new Image("https://img/alice")));

        // No pin of any kind was established, and it is on disk anyway — under the CANONICAL user uri, which is what
        // makes the hot key and the cold key the same string.
        Assert.True(cold.Entities.ContainsKey("spotify:user:alice"));
        Assert.Equal(EntityKind.User, cold.Entities["spotify:user:alice"].Kind);

        // A fresh store (the deferred ctor loads no entities at all) still answers the read — through the cold
        // fallback, under EITHER spelling.
        var restarted = new CachedStore(cold);
        Assert.Equal("Alice", restarted.GetOwner("spotify:user:alice")!.Name);
        Assert.Equal("https://img/alice", restarted.GetOwner("alice")!.Avatar!.Url);
    }

    [Fact]
    public void UpsertOwner_MergeKeepsANameAndAnAvatarAThinWriterDoesNotCarry()
    {
        var cold = new MemCold();
        var store = new CachedStore(cold);
        store.UpsertOwner(new Owner("alice", "Alice", new Image("https://img/alice")));
        // The REST arm answers some accounts with no image, and a display name can be absent entirely — neither may
        // blank what kind 15 already gave us (StoreEntityMerge.Owner: Name NonEmpty, Avatar null-coalesce).
        store.UpsertOwner(new Owner("alice", "", null));

        var owner = store.GetOwner("spotify:user:alice")!;
        Assert.Equal("Alice", owner.Name);
        Assert.Equal("https://img/alice", owner.Avatar!.Url);
    }

    [Fact]
    public void UpsertOwner_IsElided_WhenTheBytesAreUnchanged()
    {
        var cold = new MemCold();
        var store = new CachedStore(cold);
        store.UpsertOwner(new Owner("alice", "Alice", null));
        int writes = cold.EntityWrites;
        store.UpsertOwner(new Owner("alice", "Alice", null));

        Assert.Equal(writes, cold.EntityWrites);   // the payload-hash elision covers owners like every other kind
    }

    [Fact]
    public void UpsertOwner_IgnoresAnUnusableId()
    {
        var cold = new MemCold();
        var store = new CachedStore(cold);
        store.UpsertOwner(new Owner("", "Nobody", null));

        Assert.Empty(cold.Entities);
        Assert.Null(store.GetOwner(""));
    }

    [Fact]
    public void Write_DualWrites_MemoryAndCold()
    {
        var cold = new MemCold();
        var store = new CachedStore(cold);
        store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);   // pin-reachable → the gate lets it through
        store.UpsertTrack(Trk("t1"));
        Assert.NotNull(store.GetTrack("spotify:track:t1"));        // in memory
        Assert.True(cold.Entities.ContainsKey("spotify:track:t1")); // and persisted
    }

    // The pin-reachability write gate (§A.4.1): transient hydration (queue / radio / browse) is HOT-ONLY. That is the
    // whole point of the redesign — the disk stops growing with everything the session ever touched.
    [Fact]
    public void Write_IsHotOnly_WhenNotPinReachable()
    {
        var cold = new MemCold();
        var store = new CachedStore(cold);
        store.UpsertTrack(Trk("t1"));
        Assert.NotNull(store.GetTrack("spotify:track:t1"));          // memory: always
        Assert.False(cold.Entities.ContainsKey("spotify:track:t1")); // disk: only if pin-reachable
    }

    [Fact]
    public void Reload_ServesPersistedEntity_AfterRestart()
    {
        var cold = new MemCold();
        var s1 = new CachedStore(cold);
        s1.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        s1.UpsertTrack(Trk("t1"));                        // instance 1 persists
        var store2 = new CachedStore(cold);               // "restart" over the same cold tier
        var t = store2.GetTrack("spotify:track:t1");      // deferred ctor ⇒ served by the (unconditional) cold fallback
        Assert.NotNull(t);
        Assert.Equal("Title t1", t!.Title);
        Assert.Equal("Artist", t.Artists[0].Name);        // full record round-trips
    }

    // ── the stub gate (Phase 5a): a PINNED but title-less row is a synthetic placeholder, not a durable row ─────────────
    // A playlist-member/saved/recent write races ahead of the real hydration wave carrying nothing but a uri, so the
    // FIRST write for a newly-pinned playable is very often exactly that placeholder. Persisting it verbatim used to
    // make a blank row durable across restarts; skipping it costs nothing because `ColdPresent` self-pins, so the
    // first NAMED write still persists (below) and every later write still refreshes it.
    [Fact]
    public void UpsertTrack_PinnedButUnnamed_NotPersisted()
    {
        var cold = new MemCold();
        using var store = new CachedStore(cold);
        store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);   // pin-reachable
        store.UpsertTrack(new Track("t1", "spotify:track:t1", "", [], new AlbumRef("", "", ""), 0, false, null));
        store.Flush();

        Assert.NotNull(store.GetTrack("spotify:track:t1"));           // hot: the caller's row is always resident
        Assert.False(cold.Entities.ContainsKey("spotify:track:t1"));  // cold: a title-less stub is not worth a durable row
    }

    [Fact]
    public void UpsertTrack_NamedAfterStub_Persisted()
    {
        var cold = new MemCold();
        using var store = new CachedStore(cold);
        store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        store.UpsertTrack(new Track("t1", "spotify:track:t1", "", [], new AlbumRef("", "", ""), 0, false, null));   // stub: skipped
        store.Flush();
        Assert.False(cold.Entities.ContainsKey("spotify:track:t1"));

        store.UpsertTrack(Trk("t1"));   // the real metadata lands — the FIRST named write persists
        store.Flush();
        Assert.True(cold.Entities.ContainsKey("spotify:track:t1"));

        var restarted = new CachedStore(cold);
        Assert.Equal("Title t1", restarted.GetTrack("spotify:track:t1")!.Title);   // the persisted row is the NAMED one, not the stub
    }

    [Fact]
    public void UpsertPlaylist_EmptyName_NotPersisted()
    {
        var cold = new MemCold();
        using var store = new CachedStore(cold);
        store.SetSaved("playlists", "spotify:playlist:p", true, SyncState.Confirmed);   // pin-reachable
        store.UpsertPlaylist(new Playlist("p", "spotify:playlist:p", "", null, "Me", null, 0));
        store.Flush();

        Assert.False(cold.Entities.ContainsKey("spotify:playlist:p"));
    }

    [Fact]
    public void UpsertOwner_EmptyName_NotPersisted()
    {
        var cold = new MemCold();
        using var store = new CachedStore(cold);
        store.UpsertOwner(new Owner("alice", "", null));   // owners bypass the PIN gate (P4-C) but not the STUB gate

        Assert.False(cold.Entities.ContainsKey("spotify:user:alice"));
    }

    // The deferred ctor (§B step 4): NO entity replay, but the saved sets are loaded eagerly so IsSaved/SavedUris/counts
    // are correct at first render. The entity itself arrives via the warm pass (or the cold fallback, whichever is first).
    [Fact]
    public async Task DeferredCtor_LoadsSavedSets_ThenWarmsTheEntities()
    {
        var cold = new MemCold();
        var s1 = new CachedStore(cold);
        s1.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        s1.UpsertTrack(Trk("t1"));

        var s2 = new CachedStore(cold);
        Assert.True(s2.IsSaved("liked", "spotify:track:t1"));   // identity tier: eager
        await s2.WarmComplete;
        Assert.False(s2.ColdNotFullyResident);
        Assert.Equal("Title t1", Assert.Single(s2.QueryTracks(limit: 100)).Title);   // warmed into the hot mirror
    }

    // Pin-transition flush (critique #1): an entity hydrated hot-only that is LATER liked must reach disk — SetSaved is
    // the only write in that path, and a payload-hash-only elision would strand it forever.
    [Fact]
    public void PinTransition_FlushesHotOnlyEntity_OnSetSaved()
    {
        var cold = new MemCold();
        using var store = new CachedStore(cold);
        store.UpsertTrack(Trk("t1"));                                            // transient: hot-only
        Assert.False(cold.Entities.ContainsKey("spotify:track:t1"));
        store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);   // …then liked
        store.Flush();                                                            // drains the pin-transition lane
        Assert.True(cold.Entities.ContainsKey("spotify:track:t1"));

        using var restarted = new CachedStore(cold);                              // restart sim: it is really on disk
        Assert.Equal("Title t1", restarted.GetTrack("spotify:track:t1")!.Title);
    }

    // No-op elision keyed on (payload hash, cold-presence): re-upserting identical bytes writes nothing; a real change does.
    [Fact]
    public void Elision_SkipsUnchangedRewrite_ButNotAChangedOne()
    {
        var cold = new MemCold();
        using var store = new CachedStore(cold);
        store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        store.UpsertTrack(Trk("t1"));
        store.Flush();
        int writes = cold.EntityWrites;

        store.UpsertTrack(Trk("t1"));       // byte-identical → elided
        store.Flush();
        Assert.Equal(writes, cold.EntityWrites);

        store.UpsertTrack(Trk("t1") with { PlayCount = 42 });   // a real change → written
        store.Flush();
        Assert.Equal(writes + 1, cold.EntityWrites);
    }

    // Membership adoption pins the playlist AND its members (§A.3 P0: `playlists` ∪ `playlist_items`).
    [Fact]
    public void PinTransition_FlushesMembers_OnMembershipAdoption()
    {
        var cold = new MemCold();
        using var store = new CachedStore(cold);
        store.UpsertTrack(Trk("t1"));                                     // transient
        store.UpsertPlaylist(new Playlist("p", "spotify:playlist:p", "Mix", null, "Me", null, 1));
        Assert.False(cold.Entities.ContainsKey("spotify:track:t1"));
        Assert.False(cold.Entities.ContainsKey("spotify:playlist:p"));

        store.SetMembership("spotify:playlist:p",
            new[] { new Wavee.Backend.Playlists.PlaylistMember("id1", "spotify:track:t1", null, 0) }, null);
        store.Flush();

        Assert.True(cold.Entities.ContainsKey("spotify:playlist:p"));     // the header the adopt wrote before the membership
        Assert.True(cold.Entities.ContainsKey("spotify:track:t1"));       // …and every member
    }

    // The artist thin split (locked decision 17): the persisted core is small, the facets live in artist_overview, and a
    // cold-fallback read re-fattens the record from them.
    [Fact]
    public void ArtistSplit_PersistsCoreAndOverview_ThenRefattensOnRead()
    {
        var cold = new MemCold();
        const string uri = "spotify:artist:ar";
        var fat = new Artist("ar", uri, "The Artist", null,
            TopAlbums: [new Album("al1", "spotify:album:al1", "Debut", null, [], 2019, 10, Kind: AlbumKind.Album)],
            MonthlyListeners: 1234, Followers: 99, Bio: "A long biography.", Verified: true,
            TopTracks: [Trk("t1")], AlbumsTotal: 7);
        using (var store = new CachedStore(cold))
        {
            store.SetSaved("artists", uri, true, SyncState.Confirmed);
            store.UpsertArtist(fat);
            Assert.NotNull(store.GetArtist(uri)!.TopAlbums);   // the HOT record stays fat
            store.Flush();
        }

        Assert.True(cold.Overviews.ContainsKey(uri));                       // the facets went to artist_overview
        var core = System.Text.Encoding.UTF8.GetString(cold.Entities[uri].Payload);
        Assert.DoesNotContain("Debut", core);                               // …and NOT into the entity core
        Assert.DoesNotContain("A long biography.", core);
        Assert.Contains("The Artist", core);

        using var restarted = new CachedStore(cold);
        var got = restarted.GetArtist(uri);                                 // cold fallback → core + overview re-fatten
        Assert.NotNull(got);
        Assert.Equal("The Artist", got!.Name);
        Assert.Equal(1234, got.MonthlyListeners);
        Assert.Equal("A long biography.", got.Bio);
        Assert.Equal(7, got.AlbumsTotal);
        Assert.Equal("spotify:album:al1", Assert.Single(got.TopAlbums!).Uri);
        Assert.Equal("spotify:track:t1", Assert.Single(got.TopTracks!).Uri);   // top tracks ride along with the artist
    }

    // A saved artist's discography is pin-reachable through its ArtistRefs (Addendum A2) — this is what keeps critique
    // #7's "offline discography for saved artists" alive under the write gate.
    [Fact]
    public void PinGate_PersistsAlbumOfASavedArtist()
    {
        var cold = new MemCold();
        using var store = new CachedStore(cold);
        store.SetSaved("artists", "spotify:artist:a", true, SyncState.Confirmed);
        store.UpsertAlbum(new Album("al", "spotify:album:al", "Al", null,
            [new ArtistRef("a", "spotify:artist:a", "Artist")], 2020, 1));
        store.UpsertAlbum(new Album("x", "spotify:album:x", "X", null,
            [new ArtistRef("z", "spotify:artist:z", "Other")], 2020, 1));
        store.Flush();
        Assert.True(cold.Entities.ContainsKey("spotify:album:al"));    // saved artist → its releases persist
        Assert.False(cold.Entities.ContainsKey("spotify:album:x"));    // an unrelated artist's album does not
    }

    [Fact]
    public void SavedLibraryState_Persists()
    {
        var cold = new MemCold();
        new CachedStore(cold).SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        Assert.True(new CachedStore(cold).IsSaved("liked", "spotify:track:t1"));   // survives "restart"
    }

    [Fact]
    public void VideoAssociation_DualWrites_AndReloads()
    {
        var cold = new MemCold();
        var assoc = new VideoAssociation("spotify:track:t1", true, "spotify:track:vid",
            new[] { new VideoFileRef("ab6742d3000053b751ab106a1c8edd63fa934530", 0, 2560, 1440) },
            "etag1", DateTimeOffset.UtcNow, 2592000);
        new CachedStore(cold).UpsertVideoAssociation(assoc);
        Assert.True(cold.VideoAssoc.ContainsKey("spotify:track:t1"));   // persisted to its own cold table

        var a = new CachedStore(cold).GetVideoAssociation("spotify:track:t1");   // survives "restart"
        Assert.NotNull(a);
        Assert.True(a!.HasVideo);
        Assert.Equal("spotify:track:vid", a.CounterpartUri);
        Assert.Equal("ab6742d3000053b751ab106a1c8edd63fa934530", Assert.Single(a.Files).FileIdHex);
        Assert.Equal("etag1", a.Etag);
    }

    [Fact]
    public void VideoAssociation_VideoGidHex_RoundTrips_AndLegacyRowsWithoutItStillLoad()
    {
        var cold = new MemCold();
        const string gid = "3c14b1c9a7d94f0e9d2b8a6f5e4c3b2a";   // 32-hex associated-video gid (Connect's associated_video_id)
        new CachedStore(cold).UpsertVideoAssociation(new VideoAssociation("spotify:track:t1", true, "spotify:track:vid",
            Array.Empty<VideoFileRef>(), null, DateTimeOffset.UtcNow, 0, gid));
        Assert.Equal(gid, new CachedStore(cold).GetVideoAssociation("spotify:track:t1")!.VideoGidHex);

        // A row persisted BEFORE VideoGidHex existed must still deserialize (the field is optional and trails the record).
        cold.VideoAssoc["spotify:track:legacy"] = System.Text.Encoding.UTF8.GetBytes(
            "{\"Uri\":\"spotify:track:legacy\",\"HasVideo\":true,\"CounterpartUri\":\"spotify:track:vid\"," +
            "\"Files\":[],\"Etag\":\"e1\",\"FetchedAt\":\"2026-01-01T00:00:00+00:00\",\"OfflineTtlSeconds\":0}");
        var legacy = new CachedStore(cold).GetVideoAssociation("spotify:track:legacy");
        Assert.NotNull(legacy);
        Assert.True(legacy!.HasVideo);
        Assert.Equal("spotify:track:vid", legacy.CounterpartUri);
        Assert.Null(legacy.VideoGidHex);
    }

    [Fact]
    public void AllEntityKinds_RoundTrip()
    {
        var cold = new MemCold();
        var s = new CachedStore(cold);
        // Every kind is pinned through its own library set first — the write gate is what decides a cold row now.
        s.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        s.SetSaved("albums", "spotify:album:al", true, SyncState.Confirmed);
        s.SetSaved("artists", "spotify:artist:ar", true, SyncState.Confirmed);
        s.SetSaved("playlists", "spotify:playlist:p", true, SyncState.Confirmed);
        s.UpsertTrack(Trk("t1"));
        s.UpsertAlbum(new Album("al", "spotify:album:al", "Album", null, [], 2020, 1));
        s.UpsertArtist(new Artist("ar", "spotify:artist:ar", "The Artist", null, Followers: 99));
        s.UpsertPlaylist(new Playlist("p", "spotify:playlist:p", "Mix", null, "Me", null, 0));

        var s2 = new CachedStore(cold);
        Assert.NotNull(s2.GetTrack("spotify:track:t1"));
        Assert.Equal("Album", s2.GetAlbum("spotify:album:al")!.Name);
        Assert.Equal(99, s2.GetArtist("spotify:artist:ar")!.Followers);
        Assert.Equal("Mix", s2.GetPlaylist("spotify:playlist:p")!.Name);
    }

    [Fact]
    public void ArtistMerge_PreservesExistingExtras_WhenThinNpvWriteAddsOneFacet()
    {
        var s = new InMemoryStore();
        const string uri = "spotify:artist:ar";

        s.UpsertArtist(new Artist("ar", uri, "The Artist", null, Extras: new ArtistExtras(
            Merch: [new MerchItem("Tour Tee", "$25", null, null, "https://shop/tee")],
            ExternalLinks: [new ExternalLink("Instagram", "https://instagram.com/a", ExternalLinkKind.Instagram)],
            Gallery: [new Image("https://cdn/gallery")],
            Related: [new RelatedArtist("rel", "spotify:artist:rel", "Related", null)])));

        s.UpsertArtist(new Artist("ar", uri, "The Artist", null, Extras: new ArtistExtras(
            TopCities: [new TopCity("Athens", "GR", 1200)])));

        var merged = s.GetArtist(uri)!.Extras!;
        Assert.Equal("Tour Tee", Assert.Single(merged.Merch!).Name);
        Assert.Equal("Instagram", Assert.Single(merged.ExternalLinks!).Name);
        Assert.Equal("https://cdn/gallery", Assert.Single(merged.Gallery!).Url);
        Assert.Equal("Related", Assert.Single(merged.Related!).Name);
        Assert.Equal("Athens", Assert.Single(merged.TopCities!).City);
    }

    [Fact]
    public void Playlist_PersistsThin_NotTheFatTrackBlob()
    {
        var cold = new MemCold();
        var pl = new Playlist("p", "spotify:playlist:p", "Mix", null, "Me", null, 2, new[] { Trk("t1"), Trk("t2") });
        var writer = new CachedStore(cold);
        writer.SetSaved("playlists", "spotify:playlist:p", true, SyncState.Confirmed);
        writer.UpsertPlaylist(pl);

        var reloaded = new CachedStore(cold).GetPlaylist("spotify:playlist:p");   // re-deserialized from the persisted blob
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.Tracks);        // the hydrated tracklist is NOT baked into the entity blob (membership joins at read)
        Assert.Equal("Mix", reloaded.Name);   // header fields survive
    }

    [Fact]
    public void Album_PersistsThin_NotTheFatTrackBlob()
    {
        var cold = new MemCold();
        var al = new Album("al", "spotify:album:al", "Al", null, [], 2020, 2, new[] { Trk("t1"), Trk("t2") });
        var writer = new CachedStore(cold);
        writer.SetSaved("albums", "spotify:album:al", true, SyncState.Confirmed);
        writer.UpsertAlbum(al);

        var reloaded = new CachedStore(cold).GetAlbum("spotify:album:al");
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.Tracks);
        Assert.Equal("Al", reloaded.Name);
    }

    [Fact]
    public void ShowAndEpisode_RoundTrip()
    {
        var cold = new MemCold();
        var s = new CachedStore(cold);
        s.SetSaved("shows", "spotify:show:sh", true, SyncState.Confirmed);
        s.SetSaved("episodes", "spotify:episode:ep", true, SyncState.Confirmed);
        s.UpsertShow(new Show("sh", "spotify:show:sh", "My Show", "Acme Media", null));
        s.UpsertEpisode(new Episode("ep", "spotify:episode:ep", "Ep 1", "My Show", null, 5000, DateTimeOffset.UnixEpoch));

        var s2 = new CachedStore(cold);                       // "restart" over the same cold tier
        Assert.Equal("My Show", s2.GetShow("spotify:show:sh")!.Name);
        Assert.Equal("Acme Media", s2.GetShow("spotify:show:sh")!.Publisher);
        Assert.Equal("Ep 1", s2.GetEpisode("spotify:episode:ep")!.Title);
        Assert.Equal("My Show", s2.GetEpisode("spotify:episode:ep")!.ShowName);
    }
}

// The REAL SQLite tier — durability across process-like restarts + bulk write-behind.
public class SqliteColdStoreTests
{
    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }
    static Track Trk(string id) => new(id, "spotify:track:" + id, "Title " + id, [], new AlbumRef("", "", ""), 1000, false, null);

    [Fact]
    public void Persists_AcrossInstances()
    {
        var path = TempDb();
        try
        {
            using (var store = new CachedStore(new SqliteColdStore(path)))
            {
                store.UpsertTrack(Trk("t1"));                                                  // hot-only at first…
                store.UpsertAlbum(new Album("al", "spotify:album:al", "Al", null, [], 2020, 1));
                store.SetSaved("albums", "spotify:album:al", true, SyncState.Confirmed);
                store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);        // …the like flushes both
                store.Flush();   // make the pin-transition lane + write-behind durable before we drop the instance
            }   // Dispose drains + closes

            using var store2 = new CachedStore(new SqliteColdStore(path));   // reopen the same file
            Assert.Equal("Title t1", store2.GetTrack("spotify:track:t1")!.Title);
            Assert.NotNull(store2.GetAlbum("spotify:album:al"));
            Assert.True(store2.IsSaved("liked", "spotify:track:t1"));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task BulkDualWrite_10k_AllPersist()
    {
        var path = TempDb();
        try
        {
            using (var store = new CachedStore(new SqliteColdStore(path)))
            {
                for (int i = 0; i < 10_000; i++)
                {
                    store.SetSaved("liked", "spotify:track:t" + i, true, SyncState.Confirmed);   // pin first…
                    store.UpsertTrack(Trk("t" + i));                                             // …memory + enqueue
                }
                store.Flush();
            }
            using var store2 = new CachedStore(new SqliteColdStore(path));
            await store2.WarmComplete;   // the deferred ctor replays nothing; the warm pass loads the saved head-set
            Assert.Equal(10_000, store2.QueryTracks(limit: 20_000).Count);   // write-behind persisted every one
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Migration_V0_FoldsLegacySavedIntoCollectionItems()
    {
        var path = TempDb();
        try
        {
            // Seed a legacy v0 db by hand: the old `saved` table, no `meta` schema_version.
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE saved(setid TEXT NOT NULL, uri TEXT NOT NULL, sync INTEGER NOT NULL, PRIMARY KEY(setid,uri));" +
                    "INSERT INTO saved VALUES('liked','spotify:track:t1',0);";
                cmd.ExecuteNonQuery();
            }

            // Open with the current store → the migration runner folds saved → collection_items and drops saved.
            using (var store = new CachedStore(new SqliteColdStore(path)))
                Assert.True(store.IsSaved("liked", "spotify:track:t1"));   // migrated membership is loaded on startup

            // The legacy table is gone and the schema is versioned.
            using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            verify.Open();
            using var q = verify.CreateCommand();
            q.CommandText = "SELECT (SELECT count(*) FROM sqlite_master WHERE type='table' AND name='saved'), (SELECT value FROM meta WHERE key='schema_version');";
            using var r = q.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(0L, r.GetInt64(0));        // `saved` dropped
            // v5 = the one-`entity`-table consolidation, v6 = the `playlists.adopted_at` membership-GC clock.
            Assert.Equal(SqliteColdStore.CurrentSchemaVersion.ToString(), r.GetString(1));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Migration_V1_ClearsPlaylistAndRootlistRevisions()
    {
        var path = TempDb();
        try
        {
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);" +
                    "CREATE TABLE playlists(uri TEXT PRIMARY KEY, base_rev BLOB);" +
                    "INSERT INTO meta(key,value) VALUES('schema_version','1');" +
                    "INSERT INTO meta(key,value) VALUES('rootlist_rev','010203');" +
                    "INSERT INTO playlists(uri,base_rev) VALUES('spotify:playlist:p',X'01020304');";
                cmd.ExecuteNonQuery();
            }

            using (var s = new SqliteColdStore(path))
            {
                Assert.Null(s.GetPlaylistRevision("spotify:playlist:p"));
                Assert.Null(s.GetRootlistRevision());
            }

            using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            verify.Open();
            using var q = verify.CreateCommand();
            q.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
            // the runner walks the whole ladder: v1 → v2 → v3 → v4 → v5 → v6
            Assert.Equal(SqliteColdStore.CurrentSchemaVersion.ToString(), q.ExecuteScalar() as string);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void LocalizedEntities_PreferExactLocale_ThenCanonicalWithoutCrossLocaleLeakage()
    {
        var path = TempDb();
        const string uri = "spotify:track:localized";
        static byte[] Bytes(string value) => System.Text.Encoding.UTF8.GetBytes(value);
        static string Text(ColdEntity? value) => System.Text.Encoding.UTF8.GetString(value!.Value.Payload);
        try
        {
            using (var canonical = new SqliteColdStore(path))
            {
                canonical.UpsertEntity(uri, EntityKind.Track, Bytes("canonical"));
                canonical.Flush();
            }
            using (var dutch = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "nl-NL"))
            {
                dutch.UpsertEntity(uri, EntityKind.Track, Bytes("nederlands"));
                dutch.Flush();
            }
            using (var korean = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "ko-KR"))
            {
                korean.UpsertEntity(uri, EntityKind.Track, Bytes("korean"));
                korean.Flush();
            }

            using var dutchRead = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "nl");
            using var koreanRead = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "ko");
            using var unsupportedRead = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "fr");
            using var canonicalRead = new SqliteColdStore(path);
            Assert.Equal("nederlands", Text(dutchRead.GetEntity(uri)));
            Assert.Equal("korean", Text(koreanRead.GetEntity(uri)));
            Assert.Equal("canonical", Text(unsupportedRead.GetEntity(uri)));
            Assert.Equal("canonical", Text(canonicalRead.GetEntity(uri)));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ExtensionCache_RoundTripsOnlyWithinItsBoundLocale()
    {
        var path = TempDb();
        var saved = new ColdExtension(
            "spotify:album:localized", 42, [1, 2, 3], "etag-nl", 300,
            Missing: false, ExpiresAtUnixSeconds: 2_000_000_000, UpdatedAtUnixSeconds: 1_900_000_000);
        try
        {
            using (var dutch = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "nl-NL"))
            {
                dutch.UpsertExtension(saved);
                dutch.Flush();
            }

            using var dutchRead = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "nl");
            var restored = Assert.Single(dutchRead.LoadAllExtensions());
            // Member-wise, NOT Assert.Equal(saved, restored): ColdExtension is a record struct, so its synthesized equality
            // compares `byte[] Payload` by REFERENCE — a round-tripped row can never satisfy it, which used to make the
            // rest of this test (the cross-locale isolation assertion below) unreachable.
            Assert.Equal(saved.EntityUri, restored.EntityUri);
            Assert.Equal(saved.ExtensionKind, restored.ExtensionKind);
            Assert.Equal(saved.Payload, restored.Payload);
            Assert.Equal(saved.Etag, restored.Etag);
            Assert.Equal(saved.OfflineTtlSeconds, restored.OfflineTtlSeconds);
            Assert.Equal(saved.Missing, restored.Missing);
            Assert.Equal(saved.ExpiresAtUnixSeconds, restored.ExpiresAtUnixSeconds);
            Assert.Equal(saved.UpdatedAtUnixSeconds, restored.UpdatedAtUnixSeconds);

            using var englishRead = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "en-US");
            Assert.Empty(englishRead.LoadAllExtensions());
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void CollectionRevision_RoundTrips_AcrossInstances()
    {
        var path = TempDb();
        try
        {
            using (var s = new SqliteColdStore(path))
            {
                Assert.Null(s.GetCollectionRevision("liked"));   // unset → null
                s.SetCollectionRevision("liked", "5,abc123", 1700);
                s.Flush();
            }
            using var s2 = new SqliteColdStore(path);
            Assert.Equal("5,abc123", s2.GetCollectionRevision("liked"));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void PlaylistMembership_RoundTrips_OrderedWithRevision()
    {
        var path = TempDb();
        try
        {
            var rev = new byte[] { 1, 2, 3, 4, 0xAB };
            using (var s = new SqliteColdStore(path))
                s.ReplaceMembership("spotify:playlist:p", new[]
                {
                    new ColdPlaylistItem("id1", "spotify:track:a", "alice", 100),
                    new ColdPlaylistItem("id2", "spotify:track:b", null, 200),
                }, rev);

            using var s2 = new SqliteColdStore(path);   // reopen → membership + revision durable
            var rows = s2.LoadMembership("spotify:playlist:p");
            Assert.Equal(2, rows.Count);
            Assert.Equal("spotify:track:a", rows[0].ItemUri);     // ordered by position
            Assert.Equal("id1", rows[0].ItemId);
            Assert.Equal("alice", rows[0].AddedBy);
            Assert.Equal(100, rows[0].AddedAt);
            Assert.Null(rows[1].AddedBy);                          // a null added_by round-trips
            Assert.Equal(rev, s2.GetPlaylistRevision("spotify:playlist:p"));
        }
        finally { TryDelete(path); }
    }

    /// <summary>v9 — a chart playlist's per-row rank movement round-trips through a cold reopen: the "arrows" survive a
    /// restart instead of waiting for the live re-fetch. A row with no chart facts (every scalar 0) still round-trips
    /// as the ordinary non-chart shape.</summary>
    [Fact]
    public void PlaylistMembership_RoundTrips_ChartRankMovement()
    {
        var path = TempDb();
        try
        {
            using (var s = new SqliteColdStore(path))
                s.ReplaceMembership("spotify:playlist:chart", new[]
                {
                    new ColdPlaylistItem("id1", "spotify:track:a", null, 100, ChartStatus: (byte)ChartEntryStatus.Up, ChartCurrentPos: 3, ChartPreviousPos: 4, ChartRank: 41545),
                    new ColdPlaylistItem("id2", "spotify:track:b", null, 200, ChartStatus: (byte)ChartEntryStatus.New, ChartCurrentPos: 22, ChartPreviousPos: 0, ChartRank: 18579),
                    new ColdPlaylistItem("id3", "spotify:track:c", null, 300),   // no chart facts at all
                }, null);

            using var s2 = new SqliteColdStore(path);
            var rows = s2.LoadMembership("spotify:playlist:chart");
            Assert.Equal(3, rows.Count);
            Assert.Equal((byte)ChartEntryStatus.Up, rows[0].ChartStatus);
            Assert.Equal(3, rows[0].ChartCurrentPos);
            Assert.Equal(4, rows[0].ChartPreviousPos);
            Assert.Equal(41545, rows[0].ChartRank);
            Assert.Equal((byte)ChartEntryStatus.New, rows[1].ChartStatus);
            Assert.Equal(22, rows[1].ChartCurrentPos);
            Assert.Equal(0, rows[1].ChartPreviousPos);
            Assert.Equal(0, rows[2].ChartStatus);
            Assert.Equal(0, rows[2].ChartCurrentPos);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ReplaceMembership_ReplacesNotAppends()
    {
        var path = TempDb();
        try
        {
            using var s = new SqliteColdStore(path);
            s.ReplaceMembership("p", new[] { new ColdPlaylistItem("a", "spotify:track:a", null, 0), new ColdPlaylistItem("b", "spotify:track:b", null, 0) }, null);
            s.ReplaceMembership("p", new[] { new ColdPlaylistItem("c", "spotify:track:c", null, 0) }, null);
            var rows = s.LoadMembership("p");
            Assert.Single(rows);                                   // the prior two rows are gone, not appended to
            Assert.Equal("spotify:track:c", rows[0].ItemUri);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Rootlist_RoundTrips_OrderedWithFolders()
    {
        var path = TempDb();
        try
        {
            using (var s = new SqliteColdStore(path))
                s.ReplaceRootlist(new[]
                {
                    new ColdRootlistEntry(0, 1, "spotify:start-group:g1:Folder", "Folder", 0),
                    new ColdRootlistEntry(1, 0, "spotify:playlist:p1", null, 1),
                    new ColdRootlistEntry(2, 2, "spotify:end-group:g1", null, 0),
                });

            using var s2 = new SqliteColdStore(path);
            var rl = s2.LoadRootlist();
            Assert.Equal(3, rl.Count);
            Assert.Equal("spotify:playlist:p1", rl[1].Uri);
            Assert.Equal("Folder", rl[0].GroupName);
            Assert.Equal(1, rl[1].Depth);
        }
        finally { TryDelete(path); }
    }
}
