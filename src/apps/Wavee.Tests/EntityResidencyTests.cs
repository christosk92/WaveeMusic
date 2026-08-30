using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Backend.Spotify;
using Wavee.Core;
using EntityKind = Wavee.Backend.Metadata.EntityKind;   // the PERSISTED transport vocabulary (Wavee.Core.EntityKind is the routing one)
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// Bounded entity residency (the string-floor fix): LRU eviction with a reachability pin-set, the always-on 12k→8k upsert
// backstop, cold-fallback rehydration after eviction, the census accessors, and the Pathfinder request-body hit-path
// cleanup. All exercised against the source-included Backend (no engine, no GPU).
public class EntityResidencyTests
{
    static Track Trk(string id) => new(id, "spotify:track:" + id, "Title " + id,
        [new ArtistRef("a", "spotify:artist:a", "Artist")], new AlbumRef("al", "spotify:album:al", "Album"), 1000, false, null);
    static Album Alb(string id) => new(id, "spotify:album:" + id, "Album " + id, null, [], 2020, 1);
    static Artist Art(string id) => new(id, "spotify:artist:" + id, "Artist " + id, null);

    // ── LRU + pin-set ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EvictEntities_IsLruOrdered_OldestFirst()
    {
        var s = new InMemoryStore();
        s.UpsertAlbum(Alb("a"));   // seq 1
        s.UpsertAlbum(Alb("b"));   // seq 2
        s.UpsertAlbum(Alb("c"));   // seq 3
        s.GetAlbum("spotify:album:a");   // touch a → now the MRU; b is the oldest

        long freed = s.EvictEntities(new HashSet<string>(), maxResident: 2);

        Assert.True(freed > 0);
        Assert.Null(s.GetAlbum("spotify:album:b"));      // the least-recently-used went first
        Assert.NotNull(s.GetAlbum("spotify:album:a"));   // touched → survived despite being upserted first
        Assert.NotNull(s.GetAlbum("spotify:album:c"));
        Assert.True(s.HasEvictedEntities);
    }

    [Fact]
    public void EvictEntities_SkipsPinned_EvenWhenOldest()
    {
        var s = new InMemoryStore();
        s.UpsertAlbum(Alb("a"));   // oldest
        s.UpsertAlbum(Alb("b"));
        s.UpsertAlbum(Alb("c"));

        var pinned = new HashSet<string> { "spotify:album:a" };
        s.EvictEntities(pinned, maxResident: 2);

        Assert.NotNull(s.GetAlbum("spotify:album:a"));   // pinned survives despite being the oldest
        Assert.Null(s.GetAlbum("spotify:album:b"));      // the next-oldest UNPINNED is evicted instead
        Assert.NotNull(s.GetAlbum("spotify:album:c"));
    }

    [Fact]
    public void EvictEntities_NoOp_WhenUnderTarget()
    {
        var s = new InMemoryStore();
        s.UpsertAlbum(Alb("a"));
        s.UpsertAlbum(Alb("b"));
        Assert.Equal(0, s.EvictEntities(new HashSet<string>(), maxResident: 10));   // already under → 0 freed
        Assert.False(s.HasEvictedEntities);
        Assert.Equal(2, s.EntityCounts.Albums);
    }

    // ── census ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EntityCounts_And_Bytes_ReflectUpsertsAndEvictions()
    {
        var s = new InMemoryStore();
        s.UpsertTrack(Trk("t1"));
        s.UpsertAlbum(Alb("al"));
        s.UpsertArtist(Art("ar"));

        var c = s.EntityCounts;
        Assert.Equal((1, 1, 1), (c.Tracks, c.Albums, c.Artists));
        Assert.Equal(3, c.Versions);            // each upsert bumped a version row
        long before = s.EstimatedEntityBytes;
        Assert.True(before > 0);

        s.EvictEntities(new HashSet<string>(), maxResident: 2);   // evict the oldest (the track)

        var c2 = s.EntityCounts;
        Assert.Equal(2, c2.Tracks + c2.Albums + c2.Artists);
        Assert.Equal(0, c2.Tracks);                                // the oldest kind went
        Assert.Equal(2, c2.Versions);                              // its _versions row went with it
        Assert.True(s.EstimatedEntityBytes < before);
    }

    // ── the 12k → 8k upsert backstop ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UpsertBackstop_ShedsTo8k_PastThe12kHighWater()
    {
        var s = new InMemoryStore { BackstopProtectMs = 0 };   // zero the live-working-set guard so the shed is deterministic
        for (int i = 0; i < 12_001; i++) s.UpsertTrack(Trk("bk" + i));

        Assert.Equal(8_000, s.EntityCounts.Tracks);   // crossing 12k triggers the LRU shed down to 8k, on the upsert itself
        Assert.True(s.HasEvictedEntities);
    }

    [Fact]
    public void UpsertBackstop_ProtectsTheRecentWorkingSet()
    {
        var s = new InMemoryStore();   // default 60 s guard: everything just-upserted is "recent"
        for (int i = 0; i < 12_100; i++) s.UpsertTrack(Trk("recent" + i));

        // All entities were touched within the guard window, so the backstop protects them and sheds nothing — the correct
        // behavior for an active burst (the 30 s poll / a later quiescent backstop reclaims them once they age out).
        Assert.Equal(12_100, s.EntityCounts.Tracks);
    }

    // ── saved-set heads ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CollectSavedHeads_PinsUpToPerSetMembers()
    {
        var s = new InMemoryStore();
        for (int i = 0; i < 10; i++) s.SetSaved("liked", "spotify:track:s" + i, true, SyncState.Confirmed);
        var pins = new HashSet<string>();
        s.CollectSavedHeads(pins, perSet: 3);
        Assert.Equal(3, pins.Count);   // bounded to perSet
        foreach (var p in pins) Assert.StartsWith("spotify:track:s", p);
    }

    // ── cold fallback (rehydration after eviction) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ColdFallback_RoundTripsThinnedAlbum_AfterHotEviction()
    {
        var cold = new FakeCold();
        var store = new CachedStore(cold);
        // A fat album (hydrated tracklist) upserts full into hot but THIN (Tracks=null) into cold — once it is
        // pin-reachable (the write gate is what decides a cold row now).
        store.SetSaved("albums", "spotify:album:al", true, SyncState.Confirmed);
        var fat = new Album("al", "spotify:album:al", "Al", null, [], 2020, 2, new[] { Trk("t1"), Trk("t2") });
        store.UpsertAlbum(fat);
        Assert.NotNull(store.GetAlbum("spotify:album:al")!.Tracks);   // hot still has the fat copy

        store.ShedEntities(new HashSet<string>(), maxResident: 0);    // evict everything from hot

        var got = store.GetAlbum("spotify:album:al");                 // hot miss → cold fallback → promote → return
        Assert.NotNull(got);
        Assert.Equal("Al", got!.Name);
        Assert.Null(got.Tracks);                                      // the cold blob is thin (membership joins at read)
        Assert.NotNull(store.GetAlbum("spotify:album:al")!.Name);     // now resident again in hot (promoted)
    }

    // The cold fallback is UNCONDITIONAL now (design §A.5): the deferred ctor starts the hot tier empty, so gating a miss
    // on "something was evicted this session" would answer null for the whole catalog. A hot HIT still never touches disk.
    [Fact]
    public async Task ColdFallback_IsUnconditional_NotGatedOnEviction()
    {
        var cold = new FakeCold();
        var store = new CachedStore(cold);
        store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        store.UpsertTrack(Trk("t1"));
        await store.WarmComplete;                    // let the (racy) background warm settle before counting probes
        cold.GetEntityCalls = 0;

        Assert.NotNull(store.GetTrack("spotify:track:t1"));
        Assert.Equal(0, cold.GetEntityCalls);        // a hot hit is still disk-free
        Assert.False(store.HasEvictedEntities);      // nothing was ever evicted…
        Assert.Null(store.GetTrack("spotify:track:missing"));
        Assert.True(cold.GetEntityCalls > 0);        // …yet the miss probed cold anyway (the old gate is gone)
    }

    // ── Pathfinder request-body hit-path cleanup ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pathfinder_CacheHit_DoesNotStrandTheRequestBody()
    {
        var http = new FakeExchange((req, n) => new HttpResp(200, new Dictionary<string, string>(),
            System.Text.Encoding.UTF8.GetBytes("{\"data\":1}")));
        var pf = new PathfinderResource(new PathfinderClient(http),
            () => new SessionContext("", "US", "premium", "en", Tier.Premium, false));

        var first = await pf.GetBytesAsync(PathfinderOps.GetAlbum, PathfinderOps.GetAlbumHash, null);
        Assert.NotNull(first);
        Assert.Equal(1, pf.FetchCount);        // the miss fetched
        Assert.Equal(0, pf.PendingBodyCount);  // FetchAsync removed its own body

        var second = await pf.GetBytesAsync(PathfinderOps.GetAlbum, PathfinderOps.GetAlbumHash, null);
        Assert.NotNull(second);
        Assert.Equal(1, pf.FetchCount);        // served from cache (no second fetch) → this was the HIT path
        Assert.Equal(0, pf.PendingBodyCount);  // …and the hit path cleaned up the body it set (the leak fix)
    }

    // A minimal in-memory cold tier (the CachedStoreTests MemCold shape) that counts GetEntity calls so a test can assert
    // the cold-fallback gate. Uses the interface's default GetEntity (a LoadAllEntities scan) wrapped to count.
    sealed class FakeCold : IColdStore
    {
        public readonly Dictionary<string, (EntityKind Kind, byte[] Payload)> Entities = new();
        public int GetEntityCalls;
        public IEnumerable<ColdEntity> LoadAllEntities() { foreach (var kv in Entities) yield return new ColdEntity(kv.Key, kv.Value.Kind, kv.Value.Payload); }
        public ColdEntity? GetEntity(string uri)
        {
            GetEntityCalls++;
            return Entities.TryGetValue(uri, out var e) ? new ColdEntity(uri, e.Kind, e.Payload) : null;
        }
        public void UpsertEntity(string uri, EntityKind kind, byte[] payload) => Entities[uri] = (kind, payload);
        public IEnumerable<ColdSaved> LoadAllSaved() { yield break; }
        public void UpsertSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs = 0) { }
        public IEnumerable<ColdVideoAssoc> LoadAllVideoAssociations() { yield break; }
        public void UpsertVideoAssociation(string uri, byte[] payload) { }
        public string? GetCollectionRevision(string setId) => null;
        public void SetCollectionRevision(string setId, string? revision, long syncedAt) { }
        public byte[]? GetRootlistRevision() => null;
        public void SetRootlistRevision(byte[]? rev) { }
        public IReadOnlyList<ColdPlaylistItem> LoadMembership(string playlistUri) => Array.Empty<ColdPlaylistItem>();
        public void ReplaceMembership(string playlistUri, IReadOnlyList<ColdPlaylistItem> rows, byte[]? baseRev) { }
        public byte[]? GetPlaylistRevision(string playlistUri) => null;
        public IReadOnlyList<ColdRootlistEntry> LoadRootlist() => Array.Empty<ColdRootlistEntry>();
        public void ReplaceRootlist(IReadOnlyList<ColdRootlistEntry> entries) { }
        public void Flush() { }
        public void Dispose() { }
    }
}
