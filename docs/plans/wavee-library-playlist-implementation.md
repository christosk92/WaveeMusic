# Wavee Library + Playlists â€” Technical Implementation (fluent-gpu rebuild)

**How this fits the current codebase.** The app has two distinct layers that are *parallel and unconnected today*: a source-agnostic **Wavee.Core catalog seam** the UI binds against (`ICatalogSource`/`AggregateCatalog` implementing `IMusicLibrary`, `Library.cs:29-63`, `AggregateCatalog.cs:6`; sidebar/stats shapes in `Sidebar.cs`; collection deltas via `ICollectionEvents`, `CollectionEvents.cs:10`) and a **persistent Backend Store** (`IStore`/`InMemoryStore`/`CachedStore` over `SqliteColdStore`) that today is touched only by the Mutations facet (`EngineMutationSource`, `Seam.cs:28-49`). Catalog sources currently read FakeData/SpotifyExport, never the Store. This plan **wires the two together** by adding one Store-backed catalog source and generalizing the already-set-based seam to all collection types, so the heavy lifting is *composition of existing seams, not new subsystems*. What is **reused**: the `entities` spine and generic `saved(setid,uri,sync)` membership table, the `Resource<TKey,long>`+Store projection pattern (`MetadataService.cs:17`), `MetadataService.SyncAllAsync` batch hydration, the `MutationEngine` reconcile/dead-letter/rollback spine, the engine GPU media pipeline (`ImageCache`+`ImageTextureStore`), and the `AggregateCatalog`â†’`LibraryStore` in-place-refresh path. What is **added**: per-type/ordered membership tables, a migration runner, a single dealer firehose router, a bounded residency governor, and a durable outbox with a second (playlist) strategy. **Verification corrections folded in:** (1) Spotify collection sync is **token-based delta** (`collection2v2 DeltaRequest.last_sync_token â†’ DeltaResponse.sync_token`), *not* a content checksum â€” so `collection_rev.revision` stores the opaque token; (2) the design-doc `ResidencyManager` **does not exist** â€” residency is the `ImageCache`(CPU LRU/pin) + `ImageTextureStore`(GPU pool) split, which we reuse verbatim; (3) `MetadataService.SyncAllAsync` is reusable in shape but **currently hydrates only Track/Album/Artist** â€” Show/Episode are silently dropped (`ExtendedMetadataSource.cs:90,102-108`), so Show/Episode projection is genuinely *added* work, not free; (4) `ITransport.Events(prefix)` is one firehose only because `StubTransport` *ignores* the prefix (`Transport.cs:39`) â€” the real transport must add the prefix filter the interface already promises.

---

## 1. The model â€” unified collections (all types) + playlist membership

Every library type and playlists collapse onto **two membership disciplines over one stored-once entity spine**. The spine already exists (`entities(uri,kind,payload)`, `SqliteColdStore.cs:31`); `saved(setid,uri,sync)` (`SqliteColdStore.cs:32`) is already a generic set-keyed membership table â€” it *is* `collection_items` minus three columns. The model is therefore an **additive evolution of `saved`**, not a new store.

```
   STORED ONCE      entities(uri PK, kind, payload)   â† Track/Album/Artist/Playlist/Show/Episode
   (the spine)      one row per URI, kind âˆˆ EntityKind   (Metadata.cs:14 â€” all six enum'd)
        â–² join by item_uri at read (Store.Get*)
   â”Œâ”€â”€â”€â”€â”´â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”
   UNORDERED sets (by item_uri)              ORDERED lists (by item_id)
   collection_items(account,set_id,          playlists(uri PK, base_rev, â€¦)
        item_uri, added_at, sync)            playlist_items(playlist_uri, position PK,
   collection_rev(account,set_id,revision)        item_id, item_uri, added_by, added_at)
   liked/albums/artists/shows/episodes       rootlist(account, position PK, kind, uri, group_name, depth)
```

Heavy records are **never duplicated into a collection** â€” membership rows carry only the URI plus thin per-membership attributes; the UI join hydrates the entity from the spine. This is exactly the existing seam pattern (`EngineMutationSource.Saved` is a `HashSet<string>` of URIs, `Seam.cs:26`).

**The entity spine â€” all six kinds.** `EntityKind` (`Metadata.cs:14`) and `EntityRef.Parse` (`Metadata.cs:33-42`) already classify all six URI schemes allocation-free. Two purely-additive gaps make Show/Episode silently drop today: (a) `IStore` has no Show/Episode slots (`Store.cs:24-33` stop at Playlist; `InMemoryStore` has no `_shows/_episodes`, `Store.cs:49-52`) â€” add `UpsertShow/GetShow/UpsertEpisode/GetEpisode` mirroring the existing four; (b) cold replay/serialize skips them (`CachedStore.Replay` switches on four kinds, `CachedStore.cs:31-37`; `EntityJson` registers four, `:68-72`) â€” add the two `EntityKind` cases + `[JsonSerializable(typeof(Show/Episode))]`. The `Show`/`Episode` records already exist (`Models.cs:124-131`). **No schema change** â€” `entities` is kind-polymorphic by construction. (Metadata *fetch* for Show/Episode is separate added work; see the verification correction above.)

**Unordered collections** (liked tracks, saved albums, followed artists, saved shows/episodes) are unordered, set-scoped membership: `collection_items` + `collection_rev` (DDL in Â§2). Decisions baked in:
- **One logical `set_id` per UI kind** â€” `liked`, `albums`, `artists`, `shows`, `episodes` â€” *not* Spotify's wire grouping. This makes `SavedUris(set_id)` a direct O(set) read with zero URI-prefix filtering, matching the existing `_savedBySet` dict (`Store.cs:55`) and the seam's `setId` (`Seam.cs:28`). The logicalâ†”wire map (e.g. `liked`â†’wire `collection`+`spotify:track:` filter) lives **only at the sync/write boundary**, mirroring WaveeMusic's `GetSetForItemType`. *(Resolves a cross-section contradiction: the resource-graph draft keyed sets by wire names `collection:Track`/`collection:Album`; we standardize on logical-per-kind set_ids and treat wire grouping as a transport detail.)* New collection types (ban, listen-later, ylpin) become new `set_id` strings with zero schema change.
- **`revision` is the opaque delta token**, not a checksum (verified). It both gates "did anything change" and seeds the next `/delta`. Storing it is what finally makes `FreshnessPolicy.RevisionDelta` load-bearing (today `IsStale` drops it into the always-stale `_ => true` arm, `Resource.cs:149`).
- **`sync` survives unchanged** â€” already drives optimistic state via `SetReplayStrategy` (`Mutation.cs:34,44`).

**Ordered playlists** get a dedicated table with a stable per-row key because a URI can repeat and positions shift on edit. The load-bearing normalization (why two tables, not one):

| axis | `collection_items` (unordered) | `playlist_items` (ordered) |
|---|---|---|
| identity | by `item_uri` (once per set) | by `item_id` (URI may repeat; position drifts) |
| ops | ADD / REM | ADD / REM / **MOV** + attribute updates |
| remove key | the URI itself | `item_id` â€” survives position drift |
| revision | delta token (`collection_rev`) | `base_rev` bytes on the `playlists` row |

A library set never needs `item_id`; a playlist *must* have it â€” index-based reorder rebase is the drift hazard (`wavee-playlist-subsystem-analysis.md:307`), so key-based REM + recomputed-absolute MOV is why the row carries `item_id`.

**`Domain.Models` alignment (additive, but do it before the UI binds):**
- **Drop `Playlist.Tracks`** (`Models.cs:109`). `CachedStore.UpsertPlaylist` serializes the whole record to one blob (`CachedStore.cs:59`); a single-track edit would rewrite a multi-MB LOH blob and N-fold-duplicate entities. Latent today (callers pass `Tracks=null`), so removal is free now.
- **Move `Track.AddedAt`/`AddedBy` off `Track`** (`Models.cs:82`) onto the `playlist_items`/`collection_items` row. The same `Track` cannot hold two different added-at values across two playlists â€” the type-level proof that *membership â‰  metadata*, and the join corrupts the moment a URI belongs to more than one collection.

**Read-time join â†’ the seam.** Each per-type read in `IMusicLibrary` (`Library.cs:40-51`) becomes a `SavedUris(set_id) Ã— Store.Get*` join, federated by `AggregateCatalog` as today:

| `IMusicLibrary` read | membership source | entity join |
|---|---|---|
| `GetLikedSongsAsync` | `SavedUris("liked")` | `Store.GetTrack` |
| `GetAlbumsAsync` | `SavedUris("albums")` | `Store.GetAlbum` |
| `GetArtistsAsync` | `SavedUris("artists")` | `Store.GetArtist` |
| `GetShowsAsync` | `SavedUris("shows")` | `Store.GetShow` (new) |
| *(saved episodes)* | `SavedUris("episodes")` | `Store.GetEpisode` (new) |
| `GetPlaylistsAsync` | `rootlist`â†’`playlists` | sidebar `PlaylistSummary`; detail joins `playlist_items Ã— Store.GetTrack` |
| `GetStatsAsync` | `SavedUris(set).Count` | â†’ `LibraryStats` |

Offline-first read = the INNER-JOIN guard: a member URI with no `entities` row is skipped from the rendered list, and the missing URIs are handed to `MetadataService.SyncAllAsync(uris)` (`MetadataService.cs:37-49`) which batch-hydrates, `Seed`s the Store, then the list re-reads on the resulting `Bump`.

---

## 2. Persistence schema (additive to the current cold store)

Full v1 DDL, additive to `SqliteColdStore.cs:31`:

```sql
-- spine (unchanged; C# projection grows Show/Episode cases)
entities(uri TEXT PRIMARY KEY, kind INTEGER NOT NULL, payload BLOB NOT NULL);

-- unordered library sets (replaces `saved`)
collection_items(account TEXT NOT NULL, set_id TEXT NOT NULL, item_uri TEXT NOT NULL,
                 added_at INTEGER NOT NULL DEFAULT 0, position INTEGER, sync INTEGER NOT NULL,
                 PRIMARY KEY(account, set_id, item_uri));
CREATE INDEX ix_collection_added ON collection_items(account, set_id, added_at);
collection_rev(account TEXT NOT NULL, set_id TEXT NOT NULL, revision TEXT, synced_at INTEGER,
               PRIMARY KEY(account, set_id));

-- ordered playlists
playlists(uri TEXT PRIMARY KEY, owner TEXT, kind INTEGER, base_rev BLOB, name TEXT, blob BLOB);
playlist_items(playlist_uri TEXT NOT NULL, position INTEGER NOT NULL, item_id TEXT,
               item_uri TEXT NOT NULL, added_by TEXT, added_at INTEGER,
               PRIMARY KEY(playlist_uri, position));
rootlist(account TEXT NOT NULL, position INTEGER NOT NULL, kind INTEGER, uri TEXT,
         group_name TEXT, depth INTEGER, PRIMARY KEY(account, position));

-- versioning + durable outbox (Â§5)
meta(key TEXT PRIMARY KEY, value TEXT);   -- schema_version = '1'
outbox(id INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT, entity_key TEXT, op BLOB,
       base_rev BLOB, logical_ts INTEGER, status INTEGER, attempts INTEGER,
       idem_key TEXT, created_at INTEGER);
dead_letter(id INTEGER PRIMARY KEY, type TEXT, entity_key TEXT, op BLOB, reason TEXT, created_at INTEGER);
```

Notes: `base_rev` is opaque **bytes** (formatted `counter,hash` only for the `/diff` request); `position` on `collection_items` is nullable/unused for v1 unordered sets (order = `added_at DESC` via the index) and exists only so the table *could* host an ordered set without migration. `playlists`/`playlist_items` are keyed by globally-unique playlist URI and need no `account` column (the `rootlist` scopes them per account).

**Per-account scoping.** `SqliteColdStore` takes only `path` today (`SqliteColdStore.cs:26`) with no account column. Decided end state: **per-account DB *file* `{dataDir}/{account}.db`** (logout/cache-clear = delete one file; zero cross-account leakage; no `WHERE account=?` on hot reads) **plus an `account` column/PK** on `collection_items`/`collection_rev`/`rootlist` (PK consistency + the Spotify write needs the username). `entities` stays account-agnostic in shape but lives inside the per-account file (no shared global metadata cache in v1). *Resolution of a cross-section tension:* the schema carries `account` from L2, but the multi-file routing is **deferred** past the initial vertical â€” a single file with the `account` column is correct in the interim and the file split is a later, mechanical change to the composition root.

**Migration / versioning.** Persistence today is `CREATE TABLE IF NOT EXISTS` only (`SqliteColdStore.cs:31-32`) â€” it cannot evolve a column. Add `meta(key,value)` + an **ordered migration runner that runs before first paint** (the unified read path must not bind to a half-migrated DB):
- **v0** = today (`entities` + `saved`, single file).
- **v1**, one transaction: create the new tables; `INSERT INTO collection_items(account,set_id,item_uri,added_at,position,sync) SELECT @account,setid,uri,0,NULL,sync FROM saved`; `DROP TABLE saved`; `meta.schema_version='1'`.
- Legacy account-less `*.db` â†’ on first signed-in run, import once into the active `{account}.db`, then retire.

`LoadAllSaved` (`SqliteColdStore.cs:60-72`) becomes `LoadAllCollectionItems` (returns `account,set_id,item_uri,added_at,sync`); the write-behind batcher (`SqliteColdStore.cs:89`, â‰¤2000 ops/tx) absorbs the v1 copy and any 10k-row bulk insert as tiny `WriteOp`s with no new machinery. Extend `IColdStore` (`ColdStore.cs:15-22`) with `LoadAllMembership`/`ReplaceMembership(uri,rows,revision)`/`LoadAllOutbox`.

---

## 3. Resource graph, dataflow & the single dealer firehose

The unified library is a graph of `Resource<TKey,TValue>` (`Resource.cs:34`) projecting into the one queryable `IStore` spine, read by the catalog seam. A Resource's *value* is a cheap revision `long` (the proven `Resource<string,long>` pattern, `MetadataService.cs:17,23`); the UI never reads the value â€” it reads the Store, and the Resource only coordinates freshness + dedup.

**Four Resource families over the one Store:**

| Resource family | Key | Value | Freshness | Projects into Store |
|---|---|---|---|---|
| **Library index** (anchor) | `account` | `LibraryIndex` (set_idâ†’{rev,count} + rootlist URIs) | `RevisionDelta` | discovery surface â€” which sets/playlists exist (sidebar/`LibraryStats`); no member hydration |
| **per-set Collection** | `set_id` (`liked`/`albums`/`artists`/`shows`/`episodes`) | `long` (per-set sync token) | `RevisionDelta` | `SetSaved(set_id,uri,!removed,Confirmed)` inside `BeginBulk()` (`Store.cs:35,43,172`) |
| **per-playlist Playlist** | `playlist-uri` | `long` (snapshot rev) | `SnapshotRevision(ParentRevGate=true)` | `UpsertPlaylist` + `playlist_items` rows |
| **Rootlist** | `rootlist-uri` | `long` | `RevisionDelta` | playlist-discovery spine |

The two-level split (Library index owns the *set-of-sets*; each Collection owns *one set's membership + token*) keys Collection Resources by the plain `set_id` string that `SetSaved`/`SavedUris` already take â€” **zero new Store API**.

**Container-join-entities** at read: membership (thin, fast-changing) in `_savedBySet[set_id]` (`Store.cs:55`) read via `SavedUris(set_id)`; entities (heavy, shared once) in `entities`/`_tracksâ€¦` read via `Get*`; join â†’ for misses, `MetadataService.SyncAllAsync` (partial-cache-aware: `Peek` partitions fresh-vs-miss, `Seed`s back, `MetadataService.cs:42-48`). The INNER-JOIN guard falls out naturally â€” a member URI with no entity row has no card until hydration `Bump`s it.

**ONE `hm://` dealer firehose routes BOTH arms.** There is exactly one `ITransport.Events` subscription for the whole library (`Transport.cs:22`; the stub already delivers every topic on one observable, `:39`; `PushEvent` at `:44` is the test hook). *The real transport must add the prefix filter the interface promises.* Decode+route is one switch over `WireEvent.Topic`:

```
events = transport.Events("hm://");                       // ONE subscription, both arms
switch prefix-of(e.Topic):
  hm://playlist/v2/playlist/{b62}     -> PlaylistModificationInfo{uri,parent,new,ops} -> Playlist Resource
  hm://playlist/.../rootlist          -> RootlistModificationInfo{parent,new,ops}      -> Rootlist Resource
  hm://collection/{user}/collection/{set} -> collection2v2 Delta/PubSub{set,parent,new,items} -> Collection[set]
```

Per arm, **one** two-step policy (no parallel library-vs-playlist machinery):
1. **Parent-rev gate (apply-in-place, zero network).** If stored revision is byte-equal to the push's `parent_revision`, apply ops in place â€” positional ADD/REM/MOV splice for a playlist, ADD/REM on `_savedBySet` for a set â€” then store `new_revision`. This is what `SnapshotRevision(ParentRevGate)` names (`Resource.cs:29`).
2. **Anti-herd (mark stale only, never eager-fetch on COLD).** A push with no ops, or a parent-rev mismatch on a non-eager collection, **only marks the entry dirty**. Eager `/diff` runs only for `{rootlist} âˆª {open/mounted} âˆª {outbox-pending}` (`wavee-playlist-subsystem-analysis.md:196`), and every fetch drains through one **global 6-permit revalidation semaphore** (`:184`).

**The load-bearing fix:** `IsStale` currently drops both revision policies into `_ => true` (always eager-revalidate, `Resource.cs:149`) â€” the exact herd this design forbids. Add a per-`Entry` **`NeedsRevalidate`** bit (set by the dealer route in step 2, cleared after a successful fetch); `IsStale` consults it for the revision policies. Then `Use` (`Resource.cs:60-80`) serves the resident baseline instantly (SWR) and refetches **only** when the dealer marked it dirty â€” bounding work to parent-rev continuity + the semaphore, not library size.

**Collection delta and playlist `/diff` are siblings on the same machinery**, differing only in the fetch closure's wire route â€” both on `Channel.Spclient`, both deduped by `Resource.Revalidate`'s in-flight coalescing (`Resource.cs:82-98`), both under the same semaphore. Collection arm: `/collection/v2/delta?set={set}&last={token}` â†’ `DeltaResponse{delta_update_possible, items, sync_token}`; if `delta_update_possible == false`, fall to the full page loop (`/collection/v2/paging`, limit 300). Both project under `BeginBulk()` so a 10k-row delta coalesces to one `StoreChange.Bulk`, not 10k signals. **The dealer is a latency optimization, not a correctness dependency** (`wavee-playlist-subsystem-analysis.md:131`): disable it and every collection degrades to revision-gated poll-on-access via the same closures â€” still correct, which *is* the offline-first contract.

**Bridging the seam to the Store** (the catalogâ†”Store gap): a single new **`StoreLibrarySource : ICatalogSource, ISourceCollectionEvents`**, registered in `SourceRegistry` where `LocalMutationSource` sits today. Its reads project the container-join; it raises `CollectionsChanged(kind)` (via the `set_id`â†’`CollectionKind` map) whenever a Store `Bump` lands for one of its sets. The signalsâ†’repaint loop is then already wired end to end: dealer/mutation â†’ `Bump`/`Bulk` â†’ `StoreLibrarySource` raises `CollectionsChanged` â†’ `AggregateCatalog` fans it into its one aggregate stream (`AggregateCatalog.cs:21-26`) â†’ `LibraryStore.OnCollectionsChanged` routes by `CollectionKind` and calls `Refresh` (re-read **without** flipping to Pending, `LibraryStore.cs:73-81,108-119`) â€” off-page deltas update Albums/Artists/Liked/Shows/Playlists/Stats with **no skeleton flash**. Offline-first falls out of the cold tier: startup `LoadAll*` bulk-loads the in-memory mirror so the full library paints from disk on frame one; Collection Resources revalidate in the background (SWR). The Store is truth; live push is the accelerant.

---

## 4. Unified residency, prefetch & cache

**One cross-type residency system, four arenas under one governor and one host-RAM ceiling** â€” not separate caches for library vs playlists. The arenas: (1) collection membership baselines (playlist tracklists *and* unordered set indexes), (2) shared metadata entity rows, (3) decoded album-art textures, (4) the registry/bookkeeping slab. The art arena **reuses the engine `ImageCache`/`ImageTextureStore` verbatim** (the design-doc `ResidencyManager` does not exist â€” verified); we add only the *hook* and the *cross-arena eviction coordination*.

Three disciplines, every cached thing assigned to one:
- **PINNED** (never evicted while pinned): rootlist anchor; per-set library index (membership URIs behind Liked/Albums/Artists/Shows); entities referenced by a visible row; art for a visible/overscan row.
- **LRU** (byte/count-budgeted): WARM playlist baselines; metadata rows beyond the pinned working set; decoded art beyond the pinned working set.
- **POLL-ON-ACCESS** (zero resident RAM; rehydrate from SQLite on next touch): COLD playlist baselines; entities evicted from the in-memory mirror. The cold store (`SqliteColdStore.cs:31-32`) is the tier of record.

**The budget regime** â€” one `MemoryGovernor` owns four disjoint sub-budgets (a row counts in exactly one arena) under a global ceiling:

| Arena | Soft budget | Per-entry basis | Cap mechanism |
|---|---|---|---|
| **DATA-MEM** (membership + registry) | 48 MB | membership â‰ˆ 40 B SoA (`uriHandle`+`itemId`+`addedAtMs`+`addedByIdx`); registry â‰ˆ 150â€“200 B | WARM byte-LRU **24 MB âˆ§ â‰¤128 entries**, per-list admission cap **50k items**; registry pinned â‰¤12 MB; set-index pinned â‰¤8 MB |
| **META** (shared entity rows) | 64 MB (Track 32 / Album 16 / Artist 8 / Show+Episode 8) | Track â‰ˆ1 KB, Album â‰ˆ1.5 KB, Artist â‰ˆ0.8 KB, Show â‰ˆ1 KB | per-kind byte-LRU over the mirror; evict â†’ drop from RAM, keep in `entities` |
| **ART** (decoded art) | 96 MB today (`ImageCache.cs:133`); hardened 192 soft / 384 hard | bucketed `decodePxÂ²Ã—4`: 64px=16 KB Â· 128=64 KB Â· 256=256 KB Â· 512=1 MB (`ImageCache.cs:219`) | `ImageCache.EvictToBudget` (`:298-319`), `Refs==0` gate |
| **GPU pool** (VRAM, separate) | â‰ˆ52 MB reserved | pooled bucket textures + 4 atlas pages | startup-fixed; grows only on pool exhaustion |

Steady host ceiling â‰ˆ **48+64+96 â‰ˆ 208 MB soft** (ART may spike toward its 384 MB hard cap before force-trim; worst-case host envelope â‰ˆ496 MB) + 52 MB reserved VRAM. A 50k-playlist / 100k-liked power user is trivially resident on the *data* side (registry ~10 MB + indexes ~5 MB + WARM 24 MB â‰ˆ 39 MB). Byte budgets cap memory; count caps bound the LRU/registry walk; the admission cap stops one mega-list from evicting the arena. Byte budgets are why the reference's count-only `HotCache(64)`/`maxSize=200` under/over-shoot real RAM ~200Ã— (`wavee-playlist-subsystem-analysis.md:114,317`).

**Tiers, generalized.** PIN-HOT spine = rootlist anchor + the few (5â€“9) library-set indexes (consulted constantly: `IsSaved(set_id,uri)` on every row heart/checkmark, `SavedUris` for the page table-of-contents, `Store.cs:36-37,55`; a 200k-liked index â‰ˆ8 MB) + visible-row entities/art. Unordered sets carry **ADD/REM only** (no MOV) so their baseline is a flat SoA URI array. Playlist baselines tier **HOT** (open âˆª outbox-pending; applies dealer ops in place, zero network on parent-rev match) / **WARM** (recently-HOT, page closed; 24 MB âˆ§ â‰¤128 byte-LRU; serves instantly on reopen) / **COLD** (thousands; zero resident baseline; push marks stale only; revalidate lazily via `/diff?revision=stored`).

**Bounding the two unbounded maps that leak today** (`Resource._cache` is a bare `Dictionary`, `Resource.cs:40`; `InMemoryStore` dicts are unbounded, `Store.cs:49-52`): (1) the **Store becomes a bounded in-memory mirror** over SQLite â€” per-kind byte-LRU (META budgets), entities pinned while a member of a resident baseline or visible row, else evicted to disk and rehydrated on next `Get*` miss (zero network when cold-fresh); (2) **`Resource._cache` is reduced to a freshness-bookkeeping map** (its `Entry` holds only the `long` version) with a count cap keyed to the Store residency â€” when the Store evicts a row, drop the matching Resource entry so a stale `Ready` can never point at a vanished row; the next `Peek` is one rehydrate. Per-type freshness stays per-config: `Entities`â†’`Etag`, `LibrarySet`â†’`RevisionDelta`+`hm://collection/*`, `Playlist`â†’`SnapshotRevision(gate)`+`hm://playlist/*`, dispatched on `FreshnessPolicy` (`Resource.cs:144-150`).

**Art residency â€” reuse, don't duplicate.** Route all collection art through the existing `ImageCache`: ref-counted `Pin`/`Unpin` (`:262-263`, `Refs==0` evict gate), byte-budget LRU (`:298-319`) with GPU free via `SetEvictSink` (`:148`), decode-size bucketing (`:219`), priority lanes `Visible`/`Overscan`/`Prefetch` (`:9`) that drop the lowest off-screen lane first, **never Visible**, and `Cancel` on recycle (`:216`). **The hook:** collection prefetch calls `ImageCache.Prefetch(url,w,h)` (`:205`) â€” identical to the Home warm loop (`HomePage.cs:102-107`); generalizing that one loop to every collection grid is the whole integration. Prefetched art is unpinned until a real row pins it.

**Eviction coordination (one coherent lifecycle).** Art and the row's metadata entity are pinned/unpinned from the **same visible-range signal**: row mounts â†’ `ImageCache.Pin` + META pin; row recycles â†’ both `Unpin` together, LRU-eligible in lockstep, so art never outlives its row's data. The arenas are disjoint (art = GPU-texture bytes via `UsedBytes`; entities = managed heap; membership = SoA) so budgets sum without overlap, and one OS-pressure escalation sheds across both in a fixed order. Mosaic art (2Ã—2) stays one `MosaicGroup` residency unit â€” one pin, one cancel, all-or-nothing channel drop â€” so a cover built from 4 tiles can't leave a permanent 3-of-4.

**Prefetch policy.** *WHAT:* collection art (kind-matched bucket), playlist tracklists (thin baseline + revision on hover/overscan/scroll-ahead â†’ COLDâ†’WARM without a pin), album tracklists. *WHEN:* on land (warm below-the-fold art), on hover/scroll-ahead (next-page art + membership enqueue), on sidebar-nav intent (rootlist children); all coalesced on a **100 ms debounce**. *BOUNDED + CANCELLABLE:* art rides `ImagePriority.Prefetch` (dropped before Visible under backpressure, `Cancel` on recycle); membership/tracklist prefetch drains through the **same global 6-permit semaphore** that gates every `/diff`, full-GET, *and* `MetadataService.SyncAllAsync` bulk hydration â€” the single cap that prevents the reference's thundering herd (which has only per-URI throttles, no global cap); per-visible-range `CancellationTokenSource` abandons stale work on scroll; session dedup is a bounded â‰¤1024 LRU (vs the reference's unbounded per-URI dictionaries).

**`MemoryGovernor` shedding** (UI-thread, OS-pressure-subscribed), fixed priority across both arenas so neither starves and pins survive: (0) routine self-trim to soft budgets each frame (dirty-worklist, idle = O(0)); (1) drop unpinned `Prefetch`-lane art + demote WARM over the count cap to COLD; (2) drop unpinned entity rows (persist in SQLite); (3) emergency `ClearUnpinned` across all three arenas â€” pinned art (visible rows) and pinned data (rootlist, set index, visible-row entities) survive. Hard caps backstop pins (ART force-trims at 384 MB even past pins); the registry/index pins are bounded by their admission caps so "pinned" never means "unbounded."

---

## 5. Mutations & the durable outbox

**One durable mutation system, one engine, two strategies, a store-backed outbox** (not parallel systems for library vs playlists). The engine's reconcile spine is already correct and reused verbatim: monotonic-id drain order (`Mutation.cs:81`), reconcile-by-identity so a newer coalesced Save mid-replay is never clobbered (`:91-102`), `MaxAttempts=10`â†’dead-letter (`:49,112`), rollback executed OUTSIDE the lock (`:117`). Today only `SetReplayStrategy` exists/registers (`Mutation.cs:28-45`, `Scaffold.cs:25`), `OpRebase` is a comment (`:12`), and the outbox is an in-memory `Dictionary` coalesced by `(type,setId,key)` (`:54,73`) â€” so an unflushed intent dies on exit (a correctness hole for offline-first).

**Widen `OutboxOp`** from `bool TargetSaved` (`Mutation.cs:15`) to `{ byte[] Op, byte[]? BaseRev, string IdemKey }` matching the durable `outbox` schema (Â§2). For `SetReplay` the op blob is one byte (the end-state) so the boolean fast-path survives allocation-free; for `OpRebase` it is the appended `ListChanges` op cursor + the `base_rev` captured at enqueue.

**`SetReplayStrategy` covers ALL unordered library types** â€” liked tracks, saved albums, followed artists, saved shows/episodes, follows â€” distinguished only by the `set_id` string (mapped to the wire collection set at the boundary). Coalesce â†’ one row, latest end-state (LWW); `ApplyOptimistic` â†’ `store.SetSaved(set_id,uri,target,Pending)` (`Mutation.cs:34`, `Store.cs:125-142`); `Replay` â†’ idempotent POST of the desired state (`Mutation.cs:38-40`); `Rollback` â†’ flip back to `Confirmed` (`:43-44`). The `(account,set_id,item_uri)` key lets "unfollow artist but keep liked tracks" coexist. **No new strategy per type** â€” the only live-transport addition is the logical-`set_id`â†’wire-set map.

**`OpRebaseStrategy` (ordered playlist edits only).** Coalesce = **append** ops with a persisted cursor, **MUST NOT dedupe** (Spotify permits duplicates), chunk 500/op, resume-not-replay on partial; `Replay` â†’ POST `/changes` against `base_rev`; `Resolve` on 409/`multiple_heads` â†’ refetch revision + rebase membership **by `item_id`** (key-based REM, recomputed-absolute MOV), re-POST. Optimistic-apply writes local `playlist_items` immediately; the dealer echo / fresh `Contents` reconcile. The Â§1 schema (`item_id`, `base_rev`) is the precondition that makes this rebase possible.

**Routing.** `MutationEngine.Save` hard-selects `_strategies["set"]` today (`Mutation.cs:70`); generalize to dispatch by `op.Type` (already keyed, `:62`) with `set_id` as discriminator â€” a collection set_id â†’ `SetReplay`, a `spotify:playlist:*` set_id â†’ `OpRebase`. The `Drain` loop is already type-agnostic (`:83-119`), so adding `OpRebase` needs zero drain-loop change; register both in `Scaffold.cs:25`.

**The full contract (one mechanism for all types):**

| Concern | Mechanism | Cite |
|---|---|---|
| Optimistic apply | `SetSaved(set_id,uri,target,Pending)` synchronously; UI sees it next frame via Bumpâ†’Changes | `Store.cs:125-142,159-164`; `Mutation.cs:34,74` |
| Offline durability | enqueue to durable `outbox` BEFORE return; resume drain on startup from `LoadAllOutbox` | `outbox` (Â§2); new `IColdStore` ext |
| Reconnect drain | replay in id order; success â†’ `Confirmed` + remove; reconcile by identity | `Mutation.cs:81,91-102` (verbatim) |
| Conflict/retry | `SetReplay`: idempotent re-POST. `OpRebase`: 409 â†’ refetch + `item_id` rebase â†’ re-POST | `Mutation.cs:88` |
| Terminal failure | `Attempts â‰¥ 10` â†’ rollback + dead-letter | `Mutation.cs:49,112,117,57` |
| FullSync safety | a too-stale delta must not delete `Pending` rows before the outbox drains â†’ **drain-first on reconnect** | â€” |

---

## 6. How it ties into the Library + UI

The UI binds against `IMusicLibrary` (`Library.cs:31-63`) via `AggregateCatalog` and reacts via `IMutationSource.SavedChanged` + `ICollectionEvents.CollectionsChanged`. The persistent library plugs in **without changing those interfaces**:

- **Reads:** register `StoreLibrarySource` (Â§3) in `SourceRegistry` where `LocalMutationSource` sits (`Services.cs:79-90`). `AggregateCatalog` merges per-source by concat (`AggregateCatalog.cs:73-92,130-136`), so its collections appear in the library/sidebar/collection pages with no page edit. Pages keep reading `LibraryStore`'s `Loadable` cells (Albums/Artists/Liked/Shows/Playlists/Stats, `LibraryStore.cs:33-38`), which never refetch on navigation.
- **Off-page freshness (already wired):** `LibraryStore.Activate` subscribes `_mut.SavedChanged`â†’`Refresh(Liked/Stats)` (`:90-95`) and `_events.CollectionsChanged`â†’`OnCollectionsChanged(kind)` refreshing just that collection in place, no skeleton flash (`:102-119`). The only missing piece is a source that *raises* `CollectionsChanged` per `CollectionKind` â€” `StoreLibrarySource` implements `ISourceCollectionEvents` and `AggregateCatalog` fans it in automatically.
- **Mutations:** swap `LocalMutationSource` (in-process outbox, `LocalMutationSource.cs:27-37`) for a **multi-set `EngineMutationSource`**. It is single-`setId` today (`Seam.cs:28`); widen so `SetSavedAsync(uri,saved)` infers the `set_id` from the URI kind (trackâ†’`liked`, albumâ†’`albums`, artistâ†’`artists`, showâ†’`shows`) and routes through `MutationEngine.Save` (`:44-49`). `IMutationSource.Saved`/`IsSaved` stay a single aggregated membership snapshot (`SeamPorts.cs:61-70`); `OnStoreChange` already handles per-URI add/remove + bulk re-read (`Seam.cs:53-77`). The UI's `IsSaved(uri)` heart/save buttons bind unchanged. *(Resolution: one multi-set source, not N single-set sources â€” keeps the generic `IsSaved(uri)` binding and one `OnStoreChange` subscription; the write path already coalesces and routes per `(type,set_id,uri)`.)*
- **Composition root is the only file that changes:** a `Services.CreateReal` mirroring `CreateFake` (`Services.cs:50-95`) builds `CachedStore(new SqliteColdStore(accountDbPath))`, the `MetadataService`, the `MutationEngine` with both strategies, the firehose subscriptions, and registers `StoreLibrarySource` + the multi-set `EngineMutationSource`. Nothing downstream of the interfaces moves.
- **One record bump:** `LibraryStats` has 4 fields (`Sidebar.cs:16`) but `CollectionKind` has 5 (`CollectionEvents.cs:5`) â€” add `SavedShows` (and `Episodes` if surfaced) to `LibraryStats` + `GetStatsAsync` (`AggregateCatalog.cs:105-114`), isolated to those two files.

---

## 7. Phased implementation plan

Strict **reads â†’ persist â†’ residency â†’ writes**; each phase ships a green gate (live probe / 10k durability / anti-herd) before the next, in the `BackendSelfTest.Check(name,bool)` evidence style (`Scaffold.cs:32-141`). Residency (L3) precedes mutations (L4) â€” inverting the engine roadmap's order â€” because L1/L2 produce 10kâ€“50k-item resident reads that the L4 real-time tier admits/evicts on every push, so the WARM LRU must exist first or L4 thrashes the unbounded `Dictionary` (`Resource.cs:40`); the existing in-process `LocalMutationSource` keeps optimistic saves working through L1â€“L3, so deferring the durable-outbox swap to L4 is safe.

| Phase | Ships | Gate |
|---|---|---|
| **L1 Read** | `StoreLibrarySource` reading liked/albums/artists/shows via `SavedUris(set_id)Ã—Get*` + sidebar rootlist tree + one playlist read; thin membership split (kill the fat-blob); rootlistâ†’`PlaylistNode`/`PlaylistFolder` (shape exists unused, `Sidebar.cs:9-13`) | `--spotify-collection/-playlist/-rootlist` **live** probes print ordered membership + revision + folder tree; a 10k-item collection hydrates only misses once and joins by URI |
| **L2 Persist + diff/delta** | Â§2 schema additively beside `entities`; `meta`+migration-before-first-paint; extend `IColdStore`; `CollectionDeltaApplier` (token-delta) + `PlaylistDiffApplier` (ADD/REM/MOV/UPDATE); wire `SnapshotRevision(gate)` + `RevisionDelta` into `IsStale` (the `NeedsRevalidate` fix) | reopen offline â†’ served from SQLite; second online open applies N ops (or `up_to_date`) with **no full refetch**; durable 10k round-trip across restart **asserting a single add touches O(1) `*_items` rows and ZERO `entities` rows** (fat-blob cannot reappear) |
| **L3 Prefetch + residency** | WARM byte-budget LRU (24 MB âˆ§ â‰¤128, 50k admission cap) + SoA registry slab replacing the unbounded Resource cache; per-kind META LRU over SQLite; collection art prefetch via existing `ImageCache.Prefetch` (reuse the GPU pipeline, don't duplicate) | 50k-power-user load stays under the 24 MB WARM budget while the registry stays resident; an admission-cap test proving one mega-list can't evict all of WARM; art prefetch issues `Prefetch`-priority decodes, evicted under pressure, never duplicating the glyph atlas |
| **L4 Mutations + real-time** | Â§5 unified outbox â€” widen `OutboxOp`, add `OpRebaseStrategy`, register both; persist `outbox`/`dead_letter` with resume-on-startup; subscribe the **one** `hm://` firehose with the two-step tier policy + 6-permit semaphore; reconnect = drain-first then revalidate only the eager set | **anti-herd:** burst N distinct `StubTransport.PushEvent`s â†’ `StubTransport.RequestCount` (`Transport.cs:31`) rises only for the eager set, cold remainder fires **zero** fetches; zero-network apply on parent-rev match; save/unsave + playlist add/remove round-trip with a forced-409 `item_id` rebase preserving the optimistic row; outbox survives restart |

**Explicitly deferred (named, not omitted):** rootlist incremental op-apply + `TreeOp` edits (L1/L2 ship rootlist READ-ONLY; index-based REM/MOV is drift-prone, `wavee-playlist-subsystem-analysis.md:295`); playlist **reorder** conflict (best-effort/last-writer; L4 does add/remove rebase only); collaborative `multiple_heads` merge beyond `can_edit_items` gating (parse-then-full-refetch); `OnlineOnly` strategy â€” pins/cover-image upload (never queued offline); per-account DB-file routing (carry the `account` column from L2; defer the multi-file split); searchable `entities` indexed columns + DataGrid multiselect (parity Wave 2); real audio engine (stub decrypt only).

---

## 8. Open decisions

Each is settled with THE recommendation.

1. **Evolve `saved` â†’ `collection_items` (additive), not a parallel table.** `saved(setid,uri,sync)` *is* `collection_items` minus three columns and already rides the write-behind batcher + `SetReplayStrategy`; the unified model is a column-add + data-copy, not a new subsystem (`SqliteColdStore.cs:32`, `Mutation.cs:34`).
2. **Two membership disciplines, not one table.** `collection_items` (unordered, keyed by `item_uri`, ADD/REM) vs `playlist_items` (ordered, keyed by `item_id`, ADD/REM/MOV). A URI is unique per set but repeats with drifting position in a playlist, which needs the stable `item_id` (`wavee-playlist-subsystem-analysis.md:307`).
3. **One logical `set_id` per UI kind** (`liked`/`albums`/`artists`/`shows`/`episodes`), wire grouping mapped only at the sync/write boundary. Keeps `SavedUris(set_id)` O(set) with zero prefix-filtering, matching `_savedBySet` (`Store.cs:55`). *Supersedes the resource-graph draft's wire-named keys.*
4. **Freshness = opaque delta token, not checksum.** Store `collection2v2.sync_token` in `collection_rev.revision` (verified: no checksum on the wire); it gates no-op-vs-delta and seeds the next `/delta`, finally making `RevisionDelta` load-bearing instead of the always-stale arm (`Resource.cs:149`).
5. **Per-account = per-account DB *file* + `account` column** on `collection_items`/`collection_rev`/`rootlist`. Schema carries `account` from L2; the multi-file split is the decided end state but **deferred** past the initial vertical (single file with the column is correct interim).
6. **Versioning = `meta(schema_version)` + ordered migration runner before first paint;** v0â†’v1 creates the tables, copies `saved`â†’`collection_items`, drops `saved`. `CREATE TABLE IF NOT EXISTS` today can't evolve a column (`SqliteColdStore.cs:31-32`).
7. **Drop `Playlist.Tracks`; move `Track.AddedAt`/`AddedBy` onto the membership row** â€” type-level proof that membership â‰  metadata; kills the multi-MB LOH fat-blob rewrite (`CachedStore.cs:59`) while latent (`Models.cs:82,109`).
8. **Resource graph = four families over the one Store** (account-keyed Library index; per-`set_id` Collection; per-playlist Playlist; Rootlist), reusing `Resource<string,long>`+Store projection with zero new Store API.
9. **Make the parent-rev gate load-bearing via a per-`Entry` `NeedsRevalidate` bit** consulted by `IsStale` for the revision policies, replacing the always-true arm (`Resource.cs:149`). Dealer applies ops in place iff `stored == parent_revision`, else marks dirty only.
10. **ONE dealer firehose routes both arms** â€” one `ITransport.Events` subscription, switch on `WireEvent.Topic` into three decoders, all funneling into one mark-stale + eager-set-only revalidate gated by one global 6-permit semaphore. (Real transport must add the prefix filter the stub ignores, `Transport.cs:39`.)
11. **Bridge the catalogâ†”Store gap with one `StoreLibrarySource : ICatalogSource, ISourceCollectionEvents`** reading `SavedUris(set_id)Ã—Get*` and raising `CollectionsChanged` on `Store.Bump`; `AggregateCatalog`â†’`LibraryStore.Refresh` repaints in place, no skeleton flash. Store is truth, push is accelerant.
12. **Total host ceiling â‰ˆ208 MB soft**, four disjoint arenas (DATA-MEM 48 / META 64 / ART 96) + 52 MB reserved VRAM; one `MemoryGovernor` sheds across all in a fixed 4-tier order, pinned working sets always survive.
13. **WARM membership cap = byte-LRU 24 MB âˆ§ â‰¤128 entries, per-list admission cap 50k items** (~40 B/item SoA â‡’ ~600k resident items); lists over 50k skip WARM to COLD on close. Byte budgets fix the reference's ~200Ã— count-cap miss.
14. **Pin the thin per-set membership index** (flat SoA, ADD/REM only); it backs `IsSaved` on every row and the page table-of-contents (`Store.cs:36-37,55`), so its few-set footprint (~8 MB at 200k-liked) is mandatory-resident.
15. **Bound both unbounded maps:** `Resource._cache` â†’ freshness-only map with a count cap keyed to the Store; the Store â†’ per-kind byte-LRU mirror over SQLite, evicting to disk and rehydrating on `Get*` miss with zero network when cold-fresh (`Resource.cs:40`, `Store.cs:49-52`).
16. **Reuse the engine `ImageCache`/`ImageTextureStore` unchanged** (the doc's `ResidencyManager` does not exist) â€” ref-count pin/LRU/bucketing/Prefetch-lane/evict-sink already solve the self-eviction and pin-leak bugs; the only addition is calling `Pin`/`Unpin`/`Prefetch` from the collection virtualizer on the visible-range signal.
17. **Art + data share one lifecycle:** the same visible-range signal pins/unpins both the row's art and its META entity, so they evict in lockstep; disjoint accounting means budgets sum without overlap.
18. **One global 6-permit revalidation semaphore** shared by `/diff`, full-GET, membership prefetch, AND `MetadataService.SyncAllAsync` â€” the single cap that kills the reference's herd (which has no global cap). Art uses the separate bounded decode channel that drops Prefetch before Visible.
19. **On a dealer push for a COLD collection, do NOT fetch** â€” set `NeedsRevalidate` and stop; revalidate lazily on next open. Eager-apply only when a resident baseline exists AND `parent_rev == stored`. Offline-first makes this correct.
20. **Prefetch:** collection art + playlist/album tracklists on land / hover-scroll-ahead / sidebar-intent, 100 ms-debounced, per-range CTS-cancellable, â‰¤1024-LRU session dedup (caps the reference's unbounded dedup map).
21. **One durable outbox, exactly two strategies:** `SetReplayStrategy` for ALL unordered library types (the per-type difference is the `set_id` string) and `OpRebaseStrategy` for ordered playlist edits; the `Drain` spine is already type-agnostic (`Mutation.cs:83-119`).
22. **Widen `OutboxOp` to `{byte[] Op, byte[]? BaseRev, string IdemKey}`** â€” `SetReplay` encodes one byte (allocation-trivial fast-path preserved); `OpRebase` uses the op blob + captured `base_rev` (`Mutation.cs:15`).
23. **Persist the outbox to SQLite** (`id AUTOINCREMENT` drain tiebreak, `idem_key`, `status`, `attempts`) with resume-on-startup; `SetReplay` coalesces to one LWW row, `OpRebase` appends ops (MUST NOT dedupe). The in-memory `_outbox` loses unflushed intents on exit (`Mutation.cs:54`).
24. **One multi-set `EngineMutationSource`** inferring `set_id` from URI kind, keeping `IMutationSource.Saved` a single aggregated snapshot â€” preserves the generic `IsSaved(uri)` binding and one `OnStoreChange` subscription (`Seam.cs:28`).
25. **L4 conflict scope:** `SetReplay` idempotent re-POST + `OpRebase` 409 â†’ refetch-revision â†’ rebase-by-`item_id` â†’ re-POST, with optimistic apply/rollback. Defer rootlist op-apply/`TreeOp`, reorder-rebase, `multiple_heads` merge, and `OnlineOnly` pins/cover-upload (drift-prone or off the membership/edit path).

### Critical Files for Implementation
- C:\WAVEE\fluent-gpu\src\apps\Wavee\Backend\Persistence\SqliteColdStore.cs
- C:\WAVEE\fluent-gpu\src\apps\Wavee\Backend\Store.cs
- C:\WAVEE\fluent-gpu\src\apps\Wavee\Backend\Resource.cs
- C:\WAVEE\fluent-gpu\src\apps\Wavee\Backend\Mutation.cs
- C:\WAVEE\fluent-gpu\src\apps\Wavee\Backend\Seam.cs
- C:\WAVEE\fluent-gpu\src\apps\Wavee.Core\Sources\AggregateCatalog.cs
- C:\WAVEE\fluent-gpu\src\apps\Wavee.Core\Domain\Models.cs
- C:\WAVEE\fluent-gpu\src\apps\Wavee\App\LibraryStore.cs
- C:\WAVEE\fluent-gpu\src\FluentGpu.Engine\Scene\ImageCache.cs