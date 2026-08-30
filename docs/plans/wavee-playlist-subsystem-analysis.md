# Wavee Playlist Subsystem â€” Refined Analysis & Design

**What changed from the prior draft.** One claim the prior draft leaned on is now *refuted*: there is **no per-playlist dealer subscription and no client-side "bounded rootlist + viewed/hot tracked-set"** in the reference client â€” that mechanism was fabricated. The reference holds exactly **one** global `hm://`-prefixed dealer subscription; the server alone decides fan-out, and the op-apply baseline is read from the *full* persisted cache, not a hot subset. So the bounded resource was never a socket â€” it is **resident-baseline RAM** and **per-push fetch work**, which is what this design actually bounds. Four smaller corrections also land: (1) playlist Ops are **five** kinds, not four â€” `UPDATE` splits into `UPDATE_ITEM_ATTRIBUTES` (index-wise) and `UPDATE_LIST_ATTRIBUTES` (no index, list-level merge); (2) the reference's 409 handling is a **message string only** â€” it does *not* implement refetch+retry, and it durably outboxes *only* bulk-add, so our offline-first rebase **exceeds** the reference rather than copying it; (3) our `OutboxOp` is not literally "boolean" â€” it is a 7-field record whose only mutation *payload* is `bool TargetSaved`; (4) the SQLite write-behind is **near**-alloc-free, not zero-alloc â€” the drain boxes the per-op `int Kind`. Finally, the fat-blob `Playlist.Tracks` hazard is **latent, not live**: every backend caller passes `Tracks=null` today, so the cost becomes real only when a UI first populates it â€” which is exactly why the thin shape must land *before* any playlist UI binds.

---

## 1. Executive summary (the verified mental model)

A Spotify playlist is **two different things wearing one name**, and the storage model must reflect that:

- **Metadata** â€” the heavy, shared `Track`/`Album`/`Artist` entities. Content-addressed by URI, stored **once** regardless of how many lists reference them, slow-changing. **We already own this half**: `MetadataService.SyncAllAsync(uris)` (`MetadataService.cs:37-49`) hydrates an ordered URI list, partial-cache-aware, body-size-batched, 10k-tested, landing one shared `Track` per URI in the Store (`Store.cs:49`).
- **Membership** â€” the thin, ordered, per-list spine: bare URIs plus tiny `(item_id, added_by, added_at)` attributes, **no** title/art/duration, bumped on every edit via a server-authoritative **revision**. **This half is absent** in our rebuild.

The reference client splits them on the wire (`SelectedListContent.contents.items[]` is membership only) and joins at render. **The wire shape is the normalized shape** â€” co-locating the two is the defect.

Real-time change tracking is **not** a per-playlist subscription problem. It is **one** dealer firehose, filtered client-side by `hm://` prefix; the server decides what to push. Client socket cost is **O(1) regardless of playlist count**. The two things that genuinely cost-per-playlist â€” and that the reference leaves *unbounded* â€” are **resident membership RAM** and **per-push fetch fan-out**. This design bounds exactly those two via a HOT/WARM/COLD residency tier, where "HOT" means *holds a resident baseline and applies ops in place* â€” **never a socket**.

Correctness flows entirely through the **revision + `GET /diff`** path, which runs on every open/reconnect independently of any push. The dealer is a **latency optimization, not a correctness dependency**: disable it and every playlist degrades to poll-on-access, still correct â€” which *is* the offline-first contract (cold store = truth; live push = optional accelerant).

## 2. How it actually works (fetch â†’ revision/diff â†’ dealer, grounded)

**Fetch (un-paginated, stream-parsed).** `GET {spclient}/playlist/v2/{path}?decorate=revision,attributes,length,owner,capabilities` returns a `SelectedListContent` protobuf (`SpClient.cs:1598-1651`), where `path = uri.Replace("spotify:","").Replace(":","/")`. **Bearer only, no client-token** on this GET. Even a 10k-item list returns in a single request and is stream-parsed, not paged (`SpClient.cs:1646-1649`). The `decorate` set is caller-supplied (`PlaylistCacheService.cs:21-22`). The reference never reads `ListItems.truncated`; we honor it defensively (`from=`/`length=` are plumbed at `SpClient.cs:1608-1615` but unread).

**Revision (opaque, server-authoritative).** A revision is `[4-byte big-endian counter][20-byte SHA-1]` = 24 bytes, formatted to `"{counter},{hash}"` **only** for the `/diff` query (`SpClient.cs:2245-2247`). It is never recomputed client-side; **byte-equality is the only check**. (Caveat: the reference's `FormatRevision` hexes `AsSpan(4)` to-end with no length cap â€” the 20/24-byte widths live in docstrings, not in enforcement (`SpClient.cs:2242-2246`). We treat it as opaque bytes, so this is moot for us.)

**Diff (incremental, op-applied).** `GET {spclient}/playlist/v2/{path}/diff?revision={enc}&handlesContent=&hint_revision={enc}` returns ops to apply, a fresh `Contents` snapshot to replace, or `up_to_date`/304 (`SpClient.cs:1676-1779`). The comma in `counter,hash` **must** be `%2C`-encoded or the gateway 509s; `handlesContent=` must be empty. The new revision is taken **verbatim** (`to_revision ?? revision ?? existing`). Ops are **five** kinds â€” ADD/REM/MOV/UPDATE_ITEM_ATTRIBUTES (all index-positional, indices relative to the post-preceding-op list) plus UPDATE_LIST_ATTRIBUTES (no index; merged into list-level fields) â€” applied to a `List<CachedPlaylistItem>` of `{uri + attributes}` (`PlaylistDiffApplier.cs:43,58-119`, proto `:116-123`).

**Dealer (one firehose, push = diff).** A single WebSocket carries every frame as one hot stream (`DealerClient.cs:69`); a single prefix subscription (`hm://collection/` / `hm://playlist/` / `*collection-update*`, `LibraryChangeManager.cs:46-50`) and two Rx subs off it are the *entire* surface (`PlaylistCacheService.cs:1562-1607`). The dealer protocol has **no SUBSCRIBE frame** (`DEALER_PROTOCOL.md:51-169`); the documented `AddMessageListener("hm://playlist/")` has zero implementations. A push is a `PlaylistModificationInfo{uri, parent_revision, new_revision, ops}` (proto `:249-254`). **If stored `revision == parent_revision` â†’ apply ops in place, zero network** (`PlaylistCacheService.cs:1321â†’1335`); else fall back to a revision-gated `/diff` (or full GET when there's no baseline). Fallback is **reactive** (endpoint error / torn apply) â€” there is **no numeric "N revisions behind" threshold**. Reconnect re-opens the socket with a fresh token and runs **no** catch-up sweep (`DealerClient.cs:704-734`).

**Edits (POST /changes).** Edits `POST {spclient}/playlist/v2/{path}/changes` with `ListChanges.base_revision` from the cached revision (`SpClient.cs:1800`, proto `:179`). A 409 is server-authoritative â€” **but the reference only throws a message string `"Playlist revision conflict - refetch and retry"`** (`SpClient.cs:1824-1825`); no code refetches/retries, and only bulk-add is durably outboxed. Metadata edits are optimistic-with-rollback; everything else is online-only.

**Rootlist (same machinery).** `spotify:user:{u}:rootlist` â†’ the same `/playlist/v2/{path}` path and `SelectedListContent` proto (`PlaylistCacheService.cs:670`), reduced to three item kinds: `spotify:playlist:` URIs, `spotify:start-group:{id}:{name}`, `spotify:end-group:{id}` (`SelectedListContentMapper.cs:20-35`), rebuilt into a tree by a pop-by-id stack (`RootlistTreeBuilder.cs:30-42`). The reference ships **no** rootlist op-applier (index-based rootlist REM/MOV are drift-prone).

## 3. What we have vs. what's missing (the corrected table)

Status for **our rebuild** (`src/apps/Wavee/Backend` + `src/apps/Wavee.Core`): **have** (built + reachable), **partial** (built but wrong-shaped or unwired), **missing** (absent).

| # | Capability | Status | Evidence | Honest note |
|---|---|---|---|---|
| 1 | Normalized shared entity Store (Track/Album/Artist deduped, keyed by URI) | **have** | `Store.cs:49-52,60-64` | A Track is stored once in `_tracks` by URI regardless of references; `ExtendedMetadataSource` memoizes refs across a sync. The "heavy entity lives once, shared" half is already true. |
| 2 | Bulk URIâ†’metadata hydration (cache-aware, body-batched, 10k-tested) | **have** | `MetadataService.cs:37-49` | `SyncAllAsync(uris)` Peek-partitions fresh vs stale, fetches misses, Seeds results. The entire stage-2 (metadata) half is done and reusable as-is. |
| 3 | Durable offline-first cold tier (SQLite WAL, dual-write, write-behind â‰¤2000/tx) | **partial** | `SqliteColdStore.cs:31-33`; `CachedStore.cs:12-24` | Built + unit-tested but **not wired into the running app** â€” `BackendScaffold` uses `InMemoryStore` + `StubTransport` (`Scaffold.cs:22`). Writer is sound for 10k bulk insert (near-alloc-free on enqueue; the drain **boxes the per-op `int Kind`** â€” a minor fixable cost, *not* "zero per-op alloc"). The real ceiling is the **eager full in-memory mirror** (`CachedStore.cs:23,42`), not the writer. |
| 4 | SWR + in-flight-dedup read engine (`Resource`) | **have** | `Resource.cs:34-151` | `Peek`/`Seed` give batch cache-partitioning; freshness is a dispatched strategy. The `_cache` is an **unbounded `Dictionary`** (`Resource.cs:40`) â€” a slow leak at thousands of playlists. |
| 5 | Playlist **entity** model | **partial** | `Models.cs:107-112` | The `Playlist` record exists (refuting "no playlist model") â€” but `IReadOnlyList<Track>? Tracks` (`:109`) bakes hydrated Tracks, and `CachedStore.UpsertPlaylist` JSON-serializes the whole record into one `entities` blob (`CachedStore.cs:59`). **Latent, not live**: every caller passes `Tracks=null`. Separately, membership-scoped `AddedAt`/`AddedBy` sit on the **shared** `Track` (`Models.cs:82`) â€” sharing is already broken at the type level. |
| 6 | Thin ordered **membership table** | **missing** | `SqliteColdStore.cs:31-32` (only `entities`+`saved`) | Additive; already the decided schema `playlist_items(playlist_uri, position, item_id, item_uri, added_by, added_at, PK(playlist_uri,position))` (`wavee-native-backend-architecture.md:79-80`). |
| 7 | Playlist **revision** storage + parent-rev gate | **missing / inert** | `Resource.cs:29,149` | `SnapshotRevision(ParentRevGate=true)` is declared but never constructed; `IsStale` drops it into `_ => true` (always-stale); `ParentRevGate` is read nowhere; no revision column exists. |
| 8 | "List my playlists" query in the native backend | **missing** | `Store.cs:32-33` | `IStore` exposes `GetPlaylist(uri)` only. (`SavedUris(setId)` lists bare URIs for the "liked" set; `Wavee.Core.GetPlaylistsAsync` is a separate, unwired catalog layer.) |
| 9 | **Rootlist** (list-of-playlists + folder tree) | **missing** | backend grep â†’ no hits | Decided as `rootlist(account, position, kind, uri, group_name, depth)` (`architecture Â§1.1:81-82`); folders are inline `start-group`/`end-group` markers rebuilt at read. |
| 10 | Membership **fetch wire** (playlist4 proto) | **missing** | `SpotifyLive/Protos` = metadata/extended only | Track-metadata half is proven live; membership half has **no wire type vendored**. |
| 11 | Incremental **diff/ops apply** | **missing** | no proto, no applier | Target is **five** op kinds (UPDATE splits in two; UPDATE_LIST_ATTRIBUTES is list-level, not index-wise). Thin rows make this a direct index/key op. |
| 12 | Dealer real-time **firehose** | **partial** | `Transport.cs:22,14` | Seam shaped correctly (prefix-filtered `Events(topicPrefix)`). `StubTransport` has only a `PushEvent` test hook (`:44`). **Do not** build per-playlist subscriptions â€” one global subscription only. |
| 13 | Durable, **op-capable outbox** | **partial** | `Mutation.cs:12,15,28,54` | `OutboxOp` is 7 fields but its only mutation *payload* is `bool TargetSaved` â€” binary set-replace only, no Op list, no `base_revision`. `OpRebase` is **only a comment** (`:12`); outbox is an in-memory `Dictionary` (`:54`). |
| 14 | Per-account scoping + schema migration path | **missing** | `SqliteColdStore.cs:31-32` | No `account` column, no `meta` table, no `PRAGMA user_version`. Decided: per-account DB + "migrations before first paint" + `meta(key,value)` (`architecture Â§1.1:62,97`). |

**One-line read:** the *metadata* half (1, 2, 4) is done and reusable; the *membership* half (6â€“11) is absent; the connective tissue (3, 5, 12, 13, 14) is built-but-wrong-shaped or unwired. There is **no working playlist membership code** â€” so the corrections below are about choosing the right shape *before* any UI binds to the fat `Tracks` slot.

## 4. First-principles data model (membership vs. metadata)

**The defect, concretely.** A playlist's tracks live as `IReadOnlyList<Track>? Tracks` inside the `Playlist` record (`Models.cs:109`), and `CachedStore.UpsertPlaylist` serializes the *entire* record â€” Tracks included â€” into one `entities.payload` blob (`CachedStore.cs:59` â†’ `SqliteColdStore.cs:31`). Three consequences fall straight out of that shape:

1. **Write amplification.** A one-track add re-serializes the whole list and rewrites the whole blob. At 10k tracks (~200â€“400 B each) that is a multi-MB LOH allocation per edit, with a symmetric multi-MB materialization on load. A 4-byte revision bump costs a multi-MB rewrite.
2. **Duplication.** A track in five playlists is stored five times â€” once inside each blob â€” even though the Store already keeps exactly one shared `Track` per URI (`Store.cs:49`). The blob path *throws away* the normalization the Store has.
3. **Identity collision.** `AddedAt`/`AddedBy` are *per-playlist* facts but live on the **shared** `Track` (`Models.cs:82`, whose own comment calls them "per-playlist membership metadata"). The same track added to two playlists at two times cannot hold two `AddedAt` values on one shared entity â€” the type system is already telling us membership â‰  metadata.

**Why the split is forced, not stylistic.** Membership and metadata differ on every storage axis: *identity* (positional-within-a-list vs content-addressed-by-URI), *cardinality* (N rows across N playlists vs one shared row), *change rate* (a revision bump per edit vs slow editorial), and *weight* (a URI + three small fields vs heavy). Co-locating them couples the fast/light/per-list thing to the slow/heavy/shared thing â€” the canonical reason to normalize. The reference reached the same conclusion: `SelectedListContent.contents.items[]` is membership-only and hydrates URIs separately, joining at render. The wire shape *is* the normalized shape.

**We already own the expensive half.** `MetadataService.SyncAllAsync(uris)` is exactly "given an ordered URI list, hydrate Track metadata, partial-cache-aware, batched, 10k-tested," landing each Track as one shared Store row. So the only change is to stop *also* stuffing those Tracks into the playlist record.

**The shape (additive, already decided).** Beside the existing `entities`/`saved` cold tables, add:

```sql
playlists(uri TEXT PRIMARY KEY, owner TEXT, kind INT, base_rev BLOB, name TEXT, blob BLOB);
  -- base_rev = opaque revision BYTES (not a snapshot string); formatted to counter,hash only for /diff
playlist_items(playlist_uri TEXT, position INT, item_id TEXT, item_uri TEXT, added_by TEXT, added_at INT,
               PRIMARY KEY(playlist_uri, position));
  -- item_id = stable per-row identity for remove/reorder-by-key
rootlist(account TEXT, position INT, kind INT, uri TEXT, group_name TEXT, depth INT, PRIMARY KEY(account, position));
```

Note the two columns the prior tuple `(playlist_uri, position, track_uri, added_at, added_by)` omitted: **`item_id`** (remove-by-key and reorder need a handle that survives position shifts; the reference keys rows on proto field 12) and **`base_rev BLOB`** (opaque revision). This is **purely additive** â€” no migration of `entities`/`saved`; `IColdStore` gains membership methods; the existing write-behind batcher (â‰¤2000 ops/tx) absorbs a 10k-row bulk insert as 10k tiny struct `WriteOp`s. Then **`Playlist` drops `Tracks`**, and `AddedAt`/`AddedBy` move OFF `Track` ONTO the membership row â€” restoring `Track` to a pure shared entity.

**Why the revision is load-bearing.** It is what makes incremental sync correct *and* cheap: the dealer pushes ops, and we apply them in place **with zero network iff stored `base_rev == parent_revision`** â€” precisely what `SnapshotRevision(ParentRevGate=true)` (`Resource.cs:29`) names and currently does *not* do (it falls to `_ => true`). With thin rows, ADD/REM/MOV is a direct positional/keyed mutation; a normal edit rewrites one small row + a 4-byte column instead of a multi-MB blob.

**UI joins at read.** Membership rows are the ordered spine; the heavy `Track` is fetched by URI at render (`Store.GetTrack`) â€” the reference's skeleton Ã— Track join. Net effect: a 10k-track playlist persists 10k tiny URI rows (or one changed row); a track in five playlists is stored once; `AddedAt`/`AddedBy` are correct per playlist.

**Land this before any playlist UI binds to `Tracks`** â€” that is the moment the latent LOH cost becomes live.

## 5. Change-tracking & subscription lifecycle â€” the bounded design

### 5.0 The premise, corrected â€” then honored

The brief fears "a live subscription for every playlist (a user may have thousands)." **That resource does not exist** in the model we copy, and inventing it is the anti-pattern. There is **no per-playlist subscription and no clientâ†’server SUBSCRIBE frame at all**: real-time rides **one** dealer firehose, filtered client-side by `hm://` prefix; the server decides what to push. Client cost is **O(1) in sockets regardless of playlist count** (`DealerClient.cs:69`; `LibraryChangeManager.cs:46-50`; no SUBSCRIBE in `DEALER_PROTOCOL.md:51-169`; the documented `AddMessageListener("hm://playlist/")` has zero implementations).

So the brief's *instinct* â€” don't hold N live things â€” is **right**, but the bounded resource is not a socket. The two things that genuinely cost per playlist, and that the reference leaves **unbounded**, are:

1. **Resident baseline RAM** â€” the in-memory ordered membership kept so a push can apply ops *in place* with zero network. The reference bounds this only with a 64-entry content LRU (`HotCache<CachedPlaylist>(64)`, `PlaylistCacheService.cs:113`), and that LRU **unsubscribes nothing** â€” an evicted playlist still receives pushes and re-reads its baseline from SQLite (`:1292`).
2. **Per-push fetch fan-out** â€” an op-less/apply-failed push triggers a network fetch (`:1413`) gated only by **per-URI** throttles (`GroupBy(uri).Throttle(250ms)` `:1603-1604`; settle 5s `:35`; freshness 30s `:1553`; negative 5min `:40`) with **no global concurrency cap**, so a reconnect/editorial burst over N playlists fans out toward N concurrent fetches (a thundering herd), while several per-URI dictionaries (`_lastDealerRefreshAt :80`, `_negativeCache :83`, `_pendingPlaylistTouches :60`) **grow unbounded and never evict** â€” a slow leak at thousands-of-playlists scale.

**This design bounds those two.** Throughout, **"HOT / subscribed-live" means *holds a resident membership baseline and applies dealer ops to it in place + revalidates eagerly* â€” it is not a per-playlist socket.** The dealer stays a single shared firehose (`ITransport.Events("hm://playlist/")`, `Transport.cs:22`); the tiers decide who gets a resident baseline and who gets cheap poll-on-access.

> **Resolution of an apparent contradiction.** Â§4 says "do not model a tracked-set registry"; Â§5 *does* keep a registry. They agree: the Â§5 registry is a freshness/**residency** bookkeeping structure that mirrors the rootlist *universe* (entries appear/vanish only on a rootlist diff) and holds **no subscription handles**. It bounds RAM and work, not subscriptions.

### 5.1 The four residency tiers

The unit being tiered is **the resident membership baseline + eager-work eligibility**, keyed by playlist URI. Heavy `Track` metadata is **not** here â€” it lives once in the Store and is joined at read.

| Tier | Membership | Resident baseline? | Network on a push | Cap & cost basis |
|---|---|---|---|---|
| **(a) Rootlist anchor** | the one `spotify:user:{u}:rootlist` spine | **Always** (pinned) | apply if parent-eq, else revision-gated `/diff` | **1** entry, never evicted. Small; 24h TTL + push + reconnect-eager |
| **(b) HOT** | open/viewed playlists **âˆª** playlists with a pending outbox op | **Yes**, pinned (exempt from LRU) | **apply ops in place, zero network** (parent-eq); else `/diff` via the global semaphore | No fixed cap â€” **bounded by refcount**, realistically **â‰¤ ~4** (open page + maybe a peeked one + outbox-pending). Cost = the list you must render anyway |
| **(c) WARM** | recently-HOT, page closed (refcountâ†’0), baseline kept | **Yes**, LRU-eligible | apply in place if parent-eq; else `/diff` via semaphore | **Byte-budget LRU: 24 MB AND â‰¤128 entries**, plus a per-playlist admission cap of **50k items**. Per-item â‰ˆ **40 B** SoA â‡’ 500 items â‰ˆ 20 KB, 10k â‰ˆ 400 KB, 50k â‰ˆ 2 MB; 24 MB â‰ˆ **600k resident items** |
| **(d) COLD** | every other known playlist (the thousands) | **No** | **mark stale only â€” never fetch.** Revalidate lazily on next open via `GET /diff?revision=stored` | Unbounded count, **zero resident baseline** â€” only the thin registry entry (Â§5.3). The cold-store membership row is the tier of record |

**Why byte-budget, not the reference's count-of-64:** baselines span 10 KB â†’ 2 MB by playlist size, so a count-only cap under/over-shoots RAM by ~200Ã—. The **24 MB byte budget** caps real memory; the **128-entry count cap** bounds LRU/registry-walk bookkeeping; the **50k-item admission cap** stops one mega-playlist from evicting all of WARM (a 50k+ list, while open, is HOT and rendered; on close it drops straight to COLD, always poll-on-access).

**Transitions and who evicts:**

| From â†’ To | Trigger | Action |
|---|---|---|
| COLD â†’ HOT | `open` (rc 0â†’1) | load baseline from cold-store membership (instant) or full GET if absent; revalidate via `/diff`; pin |
| WARM â†’ HOT | `open` (rc 0â†’1) | serve resident baseline **instantly (SWR)**; revalidate via `/diff`; pin |
| HOT â†’ WARM | `close` (rc 1â†’0), size â‰¤ 50k, **not** outbox-pending | keep baseline; set `lastTrackedTick`; make LRU-eligible |
| HOT â†’ COLD | `close` with size > 50k | drop baseline immediately (don't squat RAM) |
| WARM â†’ COLD | byte budget **or** count cap exceeded | the **WARM LRU evictor** drops the least-recently-tracked baseline; registry entry + cold row persist |
| COLD â†’ COLD | dealer push | **mark stale only, no fetch** (anti-herd) |
| any â†’ **PIN-HOT** | outbox enqueue (user edit) | pin resident until outbox drains (need baseline to rebase) |
| registry add/remove | rootlist diff adds/removes a playlist | create/drop the thin registry entry â€” the **only** thing that grows/shrinks the registry |

### 5.2 Correctness without the dealer (dealer is OPTIONAL)

The dealer is a **latency optimization, not a correctness dependency.** Every guarantee flows through revision + `/diff`, which runs independently of any push:

- **On open** and **on reconnect-eager**, `GET /diff?revision=stored` reconciles a playlist to the server's *current* revision whether or not a push was received. The server-authoritative `new_revision` + the byte-equal parent gate make this exact.
- The push merely lets a **resident** HOT/WARM baseline skip the round-trip and refreshes an open page without a manual re-open â€” i.e. it lowers staleness latency and saves a fetch.
- **Disable the dealer entirely** â†’ every playlist behaves as COLD: poll-on-access via revision-gated `/diff`. Still correct; just higher staleness latency. That degraded mode **is** the offline-first contract.

Hence COLD needs no subscription, and "thousands of subscriptions" dissolves: **poll-on-access `/diff` is the correctness floor for all playlists; resident-baseline + op-apply is a bounded optimization for the few that are HOT/WARM.**

### 5.3 The registry data structure

Two structures, split by cardinality and lifetime.

**The registry** â€” one thin, fixed-size entry per *known* playlist (cardinality = rootlist size, thousands), **always resident, never LRU-evicted** (entries appear/vanish only on a rootlist diff). Consistent with the near-zero-alloc bet, it is a **slab / SoA**, not `Dictionary<string, object>`: an open-addressed `uri â†’ index` map over parallel columns:

```
struct PlaylistTrack {            // 1 slab row per known playlist â€” POD, no per-entry heap object
   Rev24    revision;             // 24 B INLINE (3Ã—ulong) â€” NOT a heap byte[] (avoids 10k tiny arrays)
   long     lastTrackedTick;      // LRU recency for WARM
   int      refcount;             // >0 â‡’ HOT-pinned (UI observers)
   int      baselineSlot;         // index into the resident-baseline arena, or -1 if COLD (NO per-playlist sub handle)
   byte     tier;                 // COLD | WARM | HOT
   byte     flags;                // NeedsRevalidate | OutboxPinned | RootlistAnchor
}
```

The field the brief proposed as a "subscription handle" **collapses to `baselineSlot`** â€” there is no per-playlist subscription handle; the LiveTopic subscription is **one** `IDisposable` for the whole Playlist Resource, shared by every entry. Keeping a per-playlist handle would re-introduce the phantom resource we're refuting.

**Memory at scale:** ~44 B slab row + the URI string (interned once, shared with the cold store and the Store's Track key) â‰ˆ **~150â€“200 B all-in per entry** â‡’ **~2 MB at 10k playlists, ~10 MB at a 50k power user.** Trivially resident. The heavy, bounded thing is the **baseline arena** (HOT+WARM only, Â§5.1's 24 MB), not the registry.

**Zero per-item alloc churn:** the registry is a contiguous slab (no GC graph, no boxing â€” `revision` is an inline value, not `byte[]`). Resident baselines live in **SoA parallel arrays** (`uriHandle[]`, packed `itemId[]`, `addedAtMs[]`, `addedByIdx[]`); applying ADD/REM/MOV **splices these arrays** over the engine's `ChunkedArena` (native-backed, no LOH cliff) â€” **zero per-item managed allocation per push**, consistent with the 0-alloc frame contract. `added_by` is a 4-byte index into a tiny intern pool (a handful of distinct adders), not a string per row. This is the correct home for the membership-scoped `AddedAt`/`AddedBy` that today wrongly sit on the shared `Track` (`Models.cs:82`).

### 5.4 The dealer-push policy â€” the actual WORK bound (the heart)

On every inbound push the handler does one registry lookup and branches **by residency**, never by "did the page open":

```
push(uri, parent_rev, new_rev, ops):
  e = registry[uri]                       // O(1); miss â‡’ unknown playlist â†’ create COLD, mark stale, return
  if e.tier == COLD:                      // the thousands
        e.flags |= NeedsRevalidate        // record dirtiness only â€” NO network; lazy /diff on next open
        return
  // e is HOT or WARM â‡’ has a resident baseline
  if RevEq(e.revision, parent_rev):       // parent-revision continuity gate (byte-equal)
        applyOpsInPlace(e.baselineSlot, ops)             // 0 network, 0 per-item alloc (SoA splice)
        e.revision = new_rev
        coldStore.ReplaceMembershipDelta(uri, ops, new_rev)   // thin persist
        store.Bump(uri)                                  // one UI signal
  else:                                    // missed a push / torn â€” catch up, gated
        enqueueRevalidate(uri)             // GET /diff?revision=stored, through the GLOBAL semaphore
```

Two hard caps the reference lacks:

- **Global revalidation semaphore = 6 concurrent `/diff`.** Every `/diff`/full-GET (push catch-up, reconnect, lazy-on-open) drains through it, so a reconnect or editorial burst over N playlists can never fan out to N concurrent fetches. This is the single fix for the reference's thundering-herd.
- **Bookkeeping is size-capped.** The dealer settle window (5 s, echo suppression) and any negative/debounce maps are **bounded LRUs (â‰¤1024)**, closing the reference's unbounded-dictionary leak.

The decisive rule is the COLD branch: **a push for a not-resident playlist marks freshness dirty and stops.** Offline-first makes this correct â€” the cold store is truth, a push only invalidates freshness, the next open reconciles cheaply. This is `SnapshotRevision(ParentRevGate=true)` (`Resource.cs:29`) made real, fixing today's inert behavior where `IsStale` sends it to the `_ => true` always-eager arm (`Resource.cs:144-150`) and the Resource's `_cache` never evicts (`Resource.cs:40`).

### 5.5 Offline + reconnect + conflict

**Offline:** every read serves from the resident baseline (HOT/WARM) or a cold-store membership join (COLD) â€” "stale but serviceable." No pushes arrive; nothing fetches. Writes apply optimistically to the local Store and enqueue a **durable** outbox op with `base_rev` captured at enqueue (the playlist is PIN-HOT until drained).

**Reconnect (decisive policy):**
1. **No per-playlist resubscribe** â€” re-open the **one** socket; the server resumes fan-out.
2. **Drain the outbox first**, with the **FullSync-preserves-Pending** carve-out (`architecture Â§3:219-223`): a stale revalidation must not delete optimistic rows before their ops land.
3. **Eager-revalidate ONLY the eager set** = `{rootlist} âˆª {refcount>0 HOT} âˆª {outbox-pending}` â€” typically **O(1â€“4)** â€” via revision-gated `GET /diff?revision=stored` through the 6-permit semaphore. **WARM and COLD do not sweep**; they revalidate lazily on next access. This caps reconnect work at O(eager set) regardless of library size.

**Catch-up `/diff` vs full refetch:** always prefer `GET /diff?revision=stored` (ops to apply, fresh `Contents` to replace, or `up_to_date`/304 to touch). **Full refetch is reactive only** â€” on a `/diff` endpoint error or a torn-op apply failure; deliberately **no numeric staleness threshold**. Because each registry entry holds the stored revision, the common reconnect case is a cheap `up_to_date`.

**Conflict (409) â€” exceeding the reference.** The reference only emits a message string and durably outboxes *only* bulk-add; everything else is online-only. Our offline-first mandate must actually rebase: refetch the revision, **rebase queued ops by `item_id`** (key-based REM + recomputed-absolute MOV â€” never replay raw indices, the drift hazard; this is precisely why the membership row carries `item_id`), re-POST; terminal failure â†’ rollback + `dead_letter`. `multiple_heads` (divergence) â†’ full refetch, never a local merge.

### 5.6 State machine

```
            open (rc 0â†’1): load cold baseline + /diff
   â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”
   â”‚                                                           â–¼
â”Œâ”€â”€â”€â”€â”€â”€â”   open (rc 0â†’1): serve resident (SWR) + /diff    â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”
â”‚ COLD â”‚â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â–º  â”‚   HOT   â”‚
â”‚ no   â”‚                                                  â”‚resident â”‚
â”‚resid.â”‚   â—„â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ â”‚ +eager  â”‚
â”‚basel.â”‚        close (rc 1â†’0), size > 50k: drop          â”‚ apply   â”‚
â””â”€â”€â”€â”€â”€â”€â”˜                                                  â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜
   â–²  â”‚                                                    â”‚     â–²
   â”‚  â”‚ open (rc 0â†’1)                  close (rc 1â†’0),     â”‚     â”‚ open (rc 0â†’1)
   â”‚  â–¼                                size â‰¤ 50k, !pinned â–¼     â”‚ (SWR)
   â”‚ (promote via HOT)                              â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”  â”‚
   â”‚                                                â”‚  WARM   â”‚â”€â”€â”˜
   â”‚   WARM LRU evict (24 MB / 128-cap exceeded)    â”‚resident â”‚
   â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”‚ LRU-eligâ”‚
                                                     â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜

PUSH (any tier, single O(1) lookup):
   resident & parent == stored  â†’ apply ops in place (0 net, 0 alloc); advance rev; persist; Bump
   resident & parent != stored  â†’ enqueue /diff via semaphore(6)
   COLD                         â†’ set NeedsRevalidate; NO fetch (lazy /diff on next open)

PIN  (rootlist anchor | outbox-pending): forced HOT-resident, exempt from LRU & HOTâ†’WARM demotion.
RECONNECT: re-open 1 socket; drain outbox; eager /diff ONLY {rootlist âˆª HOT âˆª outbox-pending}; WARM/COLD lazy.
```

### 5.7 Where this wires into existing seams

- **`FreshnessPolicy.SnapshotRevision(ParentRevGate)`** (`Resource.cs:29`, inert) â†’ honor it in `IsStale` (today `_ => true`, `Resource.cs:149`) and subscribe the **one** `LiveTopic` (`hm://playlist/*`, `architecture Â§2:175`); `RevalidationTopic? LiveTopic` already exists in `ResourceSpec` (`architecture Â§2:150,163-167`).
- **Bound the Playlist Resource's `_cache`** (today unbounded `Dictionary`, `Resource.cs:40`) with the WARM byte/count LRU; the cold store (`playlists`+`playlist_items`) is the unbounded tier of record.
- **Dealer firehose:** decode `hm://playlist/v2/playlist/{id}` + the `/rootlist` arm off `ITransport.Events("hm://playlist/")` (`Transport.cs:22`); `StubTransport.PushEvent` (`:44`) is the ready test hook.
- **Mutation:** generalize `OutboxOp` (boolean-payload today, `Mutation.cs:15`) to the durable **`OpRebase`** strategy (ops + `base_rev`, item_id rebase, `architecture Â§2:210-213`) and persist the outbox (RAM-only today, `Mutation.cs:54`).
- **Rootlist** uses `RevisionDelta` + its own LiveTopic; treat a rootlist push as "revision changed â†’ revision-gated `/diff`" â€” do **not** locally apply index-based rootlist ops (the documented drift hazard).

## 6. Phased implementation plan

> **Where this sits.** This is the playlist-vertical decomposition of `wavee-native-backend-architecture.md` Â§10 â€” it lands the `Playlist`/`Rootlist` Resource configs (Â§2) and the `OpRebase` Mutation strategy (Â§3) onto the already-decided schema (Â§1.1), closing gap items #6 (outbox two-disciplines), #13 (folders/rootlist), and the Â§4.7(3) parent-revision gate. Nothing here is a new structure â€” the membership/revision tables, `SnapshotRevision(ParentRevGate)`, and `ITransport.Events(prefix)` already exist and are inert; this makes them load-bearing.
>
> **One refinement that supersedes a literal Â§2/Â§9 reading:** a dealer push either **applies ops in place** (parent-rev match â†’ zero network) **or merely invalidates freshness** (lazy SWR on next open) â€” it does **not** eager-fetch. This closes the reference's unbounded fan-out.

| Phase | Ships | The gate that says "it works" |
|---|---|---|
| **P1 Read** | a real playlist + the sidebar tree, hydrated & rendered | `--spotify-playlist` / `--spotify-rootlist` live probes print the ordered tracklist + revision + folder tree |
| **P2 Persist + diff** | offline-first membership in SQLite + revision-gated `/diff` apply | reopen offline serves the list from disk; a second online open applies `N` ops (or `up_to_date`) with no full refetch |
| **P3 Track changes + edits** | the bounded change-tracking tier, dealer real-time, `OpRebase` edits | a synthetic dealer burst fans out **zero** network beyond the eager set; an add/remove round-trips optimistically and survives a 409 |

### P1 â€” Read a real playlist (the read spine, proven live)

The riskiest unknown is empirical: does our login â†’ client-token â†’ login5 â†’ spclient chain return a real `SelectedListContent`, and does our 10k hydration path join to it? P1 answers with a live probe before anything is persisted â€” the natural next milestone after the proven track/album/artist Milestone-0.

**Deliverables**
- **Vendor `playlist4_external.proto`** (+ `playlist_permission.proto` for `Capabilities`) under `src/apps/Wavee/SpotifyLive/Protos/`. P1 *consumes* only the read subset (`SelectedListContent{revision, length, attributes, contents, capabilities}`, `ListItems{pos, truncated, items}`, `Item{uri, attributes}`, `ItemAttributes{added_by, timestamp, item_id, format_attributes}`), leaving `Diff`/`Op`/`PlaylistModificationInfo` vendored-but-dormant for P2/P3.
- **`PlaylistSource`** = the fetch+project half of the Â§2 `Playlist` Resource. One GET `{spclient}/playlist/v2/{path}?decorate=revision,attributes,length,owner,capabilities`, **Bearer only** (no client-token, `SpClient.cs:1620-1623`). **Stream-parse** (a 10k-URI body is ~0.5â€“0.8 MB â†’ over the 85 KB LOH cliff), exactly the `ParseFrom(Stream)` discipline `ExtendedMetadataSource.ProjectResponse` already uses (`ExtendedMetadataSource.cs:115-116`).
- **Thin membership model split.** Project the wire `Item[]` into ordered `(item_uri, position, item_id, added_by, added_at)` + opaque `byte[] Revision`, **separate from the shared `Track`**. Removes the fat-blob hazard before it can land (`Models.cs:107-112`, `CachedStore.cs:59`); also move `AddedAt`/`AddedBy` off `Track` (`Models.cs:82`).
- **Reuse hydration verbatim.** Filter membership to `spotify:track:` â†’ `MetadataService.SyncAllAsync(trackUris)` (`MetadataService.cs:37-49`) â†’ **join membership Ã— `Store.GetTrack` at read** (the Â§2 "container Resource âˆ˜ Entities Resource" composition).
- **Rootlist fetch + tree (READ-ONLY).** Same machinery at `/playlist/v2/user/{u}/rootlist`. Parse the flat marker stream through a `RootlistTreeBuilder`-equivalent pop-by-id stack into the `PlaylistNode`/`PlaylistFolder` shape that already exists unused at `Wavee.Core/Library/Sidebar.cs`. This is the playlist-discovery surface (our Store has no list-my-playlists query).
- **Honor `ListItems.truncated` defensively.** If set, page via `from=`/`length=` (plumbed at `SpClient.cs:1608-1615` but never read). Cheap insurance closing a silent partial-list hole.
- **`--spotify-playlist <uri>` and `--spotify-rootlist` live probes**, mirroring `SpotifyMetadataProbe.RunAsync`.

**Acceptance:** headless deterministic `ProjectResponse`-style test asserting ordered membership + `counter,hash` revision + `Capabilities`, with a zero-alloc tripwire on the per-item parse loop; a fake `PlaylistSource` + `FakeBatchSource` proving a 10k-item playlist hydrates only misses once and joins by URI; rootlist self-heal on malformed marker input; **LIVE** (decisive) â€” the probes print the ordered tracklist + revision + folder tree.

### P2 â€” Persist + incremental diff (offline-first + revision-gated delta)

Offline-first is a hard contract. P2 makes membership durable and turns a re-open into a cheap revision-gated delta.

**Deliverables**
- **Adopt the Â§1.1 schema verbatim** (the three tables in Â§4), purely additive beside `entities`/`saved` (`SqliteColdStore.cs:31-32`). Carry `account` scoping per Â§1.1 / gap J.
- **Add a migration step.** The cold store has no versioning (only WAL + two `CREATE TABLE IF NOT EXISTS`). Land `meta(key,value)` + `PRAGMA user_version` "migrations before first paint"; since persistence isn't yet wired into the app, land the full decided shape at once rather than `ALTER` later.
- **Extend the `IColdStore` seam** (`ColdStore.cs:15-22`) with `LoadAllMembership` / `ReplaceMembership(playlistUri, rows, revision)` / get-set-revision + matching prepared commands + `WriteOp` variants. A 10k-row replace = 10k tiny struct `WriteOp`s over the existing â‰¤2000/tx channel â€” near-alloc-free on enqueue (the known small cost is the per-op `int` box, `SqliteColdStore.cs:130-131`).
- **`PlaylistDiffApplier`** â€” the positional op-applier mirroring reference semantics exactly: ADD (`add_first` > `add_last` > `from_index`), REM (`from_index`+`length`, positional), MOV (`to_index` post-removal), UPDATE_ITEM_ATTRIBUTES (merge), **UPDATE_LIST_ATTRIBUTES (accumulate-once, list-level, no index)**; any out-of-range op â†’ full-snapshot fallback.
- **Wire `SnapshotRevision` into freshness.** Today inert (`Resource.cs:29,149`). SWR serves cached membership instantly, then revalidates via `GET {spclient}/playlist/v2/{path}/diff?revision={enc}&handlesContent=&hint_revision={enc}` (the comma **must** be `%2C`-encoded; `handlesContent=` empty required) â€” applying `Diff.ops`, mapping a fresh `Contents`, or no-op on `up_to_date`/304. New revision taken **verbatim** from the server. Full-GET fallback is **reactive**, not a numeric threshold.
- **Rootlist uses `RevisionDelta` (no op-applier)** â€” full-GET / diff-`Contents` + 24h TTL, exactly as the reference does.

**Acceptance:** durable 10k round-trip across restart, **critically** asserting an edit re-writes only the changed rows (a single-track add touches O(1) `playlist_items` rows and **zero** `Track` rows â€” the fat-blob regression cannot reappear); table-driven diff correctness incl. out-of-range fallback; a `Resource` test proving a fresh `SnapshotRevision` serves from cache and a stale one issues exactly one `/diff`; **LIVE** â€” run `--spotify-playlist` twice (second prints `diff: N ops applied` or `up_to_date`, no full GET), then pull the network and reopen â†’ served from SQLite.

### P3 â€” Track changes + real-time + edits (the change-tracking design, folded in)

The decisive finding: **there is no per-playlist subscription to model.** We build **one** firehose + a tiny explicit eager set, not a per-playlist subscribe/refcount/LRU subscription registry.

**Deliverables**
- **One firehose, prefix-filtered.** Subscribe `ITransport.Events("hm://playlist/")` once (`Transport.cs:22`; test hook `:44`). Decode `hm://playlist/v2/playlist/{base62}` â†’ `spotify:playlist:{id}` and the `/rootlist` arm, parsing `PlaylistModificationInfo{uri, new_revision, parent_revision, ops}` and `RootlistModificationInfo{new_revision, parent_revision, ops}` (the latter has no `uri` â€” rootlist is implicit), mirroring `LibraryChangeManager`.
- **The bounded tier policy** (the full Â§5 design): parent-rev match â†’ apply in place, zero network (`SnapshotRevision(ParentRevGate=true)` made real); no-match/op-less push â†’ **invalidate freshness only, never eager-fetch**; eager set = `{rootlist} âˆª {open} âˆª {outbox-pending}`, only this revalidates on reconnect, through the global 6-permit semaphore; cap the bookkeeping the reference leaks (per-URI debounce/negative maps â†’ size-capped LRUs).
- **`OpRebase` mutation strategy + durable outbox.** Generalize beyond the boolean `SetReplay` (`Mutation.cs:12,15,54`). Add `OpRebase : IMutationStrategy` + a durable `outbox` table carrying an `Op` list + `base_revision` captured at enqueue. **Coalesce = append cursor-ops, MUST NOT dedupe** (Spotify allows duplicates), chunked 500/op, resume-not-replay. **Replay = POST `/changes`** â€” body = `ListChanges.ToByteArray()`, `Content-Type: application/x-www-form-urlencoded` (gateway quirk) + full first-party identity (`spotify-playlist-sync-reason=CAk=`, client-token), zstd response (`SpClient.cs:1788-1838`). **409 â†’ refetch revision + rebase by `item_id`** (key-based REM, recomputed-absolute MOV), re-POST. Optimistic-apply against the local Store immediately; the dealer echo / fresh `Contents` reconcile.
- **Capability-gate edits** on `Capabilities{can_edit_items}` decorated in P1 (gap K).

**Acceptance:** **anti-herd gate (headless headline)** â€” push a synthetic burst of `N` distinct events via `StubTransport.PushEvent`, assert `RequestCount` rises only for the eager set and the cold-tracked remainder fires **zero** fetches; zero-network apply on a parent-rev-match; edit round-trip + forced-409 rebase-by-`item_id` with the optimistic row preserved (FullSync-preserves-Pending); durable-outbox-survives-restart; **LIVE** â€” edit a real playlist and see the optimistic update, the `/changes` POST, and the dealer echo apply with no extra round-trip.

### Explicitly deferred (named, not omitted)

- **Rootlist incremental op-apply + `TreeOp` edits** (sidebar reorder, move-between-folders) â€” the reference ships no rootlist op-applier (index-based rootlist REM/MOV are drift-prone); defer to architecture Â§10 step 5.
- **Reorder-rebase under conflict** â€” P3 implements add/remove rebase-by-`item_id`; playlist **reorder** conflict-rebase is best-effort / last-writer (architecture Â§11).
- **Collaborative two-writer merge** beyond `can_edit_items` gating â€” `multiple_heads` divergence is parse-but-full-refetch (gap K / Â§8.4).
- **Cover-image upload (`OnlineOnly`) and pins** â€” not on the membership/edit path.
- **Searchable `entities` indexed columns (gap #1)** and **per-account DB partitioning (gap #2/J)** â€” orthogonal prerequisites the membership tables stay consistent with but do not own; the UI DataGrid is parity-roadmap Wave 2.

**Why this order.** Read before write before real-time mirrors architecture Â§10 and the offline-first mandate: P1 de-risks the live wire shape with the cheapest proof; P2 satisfies durability before any mutation can create unsynced local truth; P3's optimistic edits + dealer apply are only safe once the revision-gated membership of P2 exists to rebase against. Each gate is a real runnable check â€” a headless deterministic test, a 10k-scale durability test, and a credentialed live probe â€” not a claim.

## 7. Open decisions (THE recommendation per question)

**D1 â€” Split playlist membership from track metadata?** **Yes.** Add `playlists(uri, base_rev BLOB, â€¦)` + `playlist_items(playlist_uri, position, item_id, item_uri, added_by, added_at)` beside `entities`/`saved`; remove `Playlist.Tracks`; join `Track` from the Store by URI at read. *Why:* `Playlist` bakes `IReadOnlyList<Track>` (`Models.cs:109`) and `CachedStore` serializes the whole record to one blob (`CachedStore.cs:59`) â€” a multi-MB LOH rewrite per edit and N-fold duplication, discarding the Store's normalization (`Store.cs:49`). Purely additive; already the decided schema (`architecture Â§1.1:78-80`).

**D2 â€” Where do per-playlist `AddedAt`/`AddedBy` live, and must the tuple carry `item_id`?** **On the membership row, and yes â€” `item_id` is mandatory.** *Why:* `AddedAt`/`AddedBy` on the shared `Track` (`Models.cs:82`) cannot hold two values for a track in two playlists â€” the type-level signal that membership â‰  metadata. `item_id` is the stable handle remove-by-key and reorder need across position shifts (`architecture Â§1.1:80`; reference proto field 12); the prior `(playlist_uri, position, track_uri, added_at, added_by)` tuple omits it.

**D3 â€” Store the revision how, and wire `SnapshotRevision`?** **Store opaque `base_rev BLOB`; format to `counter,hash` only for `/diff`; honor `SnapshotRevision(ParentRevGate)` in `IsStale`** so dealer ops apply in place iff stored `base_rev == parent_revision`. *Why:* it is declared but inert â€” never constructed, `ParentRevGate` read nowhere, dropped into the always-stale `_ => true` arm (`Resource.cs:29,149`). The parent-rev gate is what makes incremental op-apply zero-network and a normal edit a small-row write.

**D4 â€” Generalize the outbox, or keep boolean set-replace?** **Generalize to a durable, op-capable intent (Op list + `base_rev` captured at enqueue) with `OpRebase` on 409; keep `SetReplay` for library saves.** *Why:* `OutboxOp`'s only mutation payload is `bool TargetSaved` (`Mutation.cs:15`) â€” it cannot express ADD/REM/MOV or a base_revision; `OpRebase` is only a comment (`:12`) and the outbox is an in-memory `Dictionary` (`:54`). Normalized membership rows are the precondition for cheap rebase.

**D5 â€” Model a per-playlist subscription or a bounded "tracked set"?** **No.** Build **one** global `hm://`-prefixed firehose (`Transport.cs:22`) and bound **work** (apply-in-place vs lazy revalidate) and **resident RAM**, not subscriptions. *Why:* the "bounded rootlist+viewed/hot tracked-set" was refuted as fabricated â€” the reference holds exactly one global subscription and reads its op-apply baseline from the *full* persisted cache; the effective bound is the server's push decision. A tracked-set registry models a resource that does not exist and misses the costs that actually scale.

**D6 â€” On a dealer push for a COLD (not-resident) playlist, fetch?** **No â€” mark freshness dirty and STOP.** Eager-apply ops only when a resident baseline exists AND `parent_revision == stored`; otherwise revalidate lazily on next open via `GET /diff?revision=stored`. *Why:* the reference eager-fetches any pushed URI with only per-URI throttles and no global cap (`PlaylistCacheService.cs:1413,1603-1604`) â€” its thundering herd. Offline-first wants the cold store as truth and a push to be cheap freshness invalidation.

**D7 â€” What caps the WARM tier?** **A byte-budget LRU: 24 MB AND â‰¤128 entries, with a per-playlist admission cap of 50k items** (bigger lists skip WARM, go straight to COLD on close). *Why:* baselines span 10 KB â†’ 2 MB, so the reference's count-only 64 (`PlaylistCacheService.cs:113`) under/over-shoots RAM ~200Ã—. Byte-budget caps memory; the count cap bounds bookkeeping; the admission cap stops one mega-playlist from evicting all of WARM. Per-item â‰ˆ 40 B SoA â‡’ ~600k resident items in 24 MB.

**D8 â€” Registry shape and size at scale?** **One POD slab row per known playlist over an open-addressed `uriâ†’index` map** (inline 24 B revision, `lastTrackedTick`, `refcount`, `baselineSlot`, `tier`, `flags`) â€” **no per-playlist subscription handle** (it collapses to `baselineSlot`; the LiveTopic sub is one shared `IDisposable`). Registry entries change only via rootlist diff; ~150â€“200 B all-in â‡’ ~2 MB at 10k, ~10 MB at 50k â€” trivially resident, never LRU-evicted. *Why:* inline revision + SoA baselines avoid 10k tiny `byte[]` allocs and keep op-apply a zero-per-item-alloc array splice, consistent with the engine's near-zero-alloc bet.

**D9 â€” Reconnect: resubscribe + catch-up sweep, or lazy?** **Re-open the one socket (no resubscribe; server resumes fan-out); drain the durable outbox first (FullSync-preserves-Pending); then eager-revalidate ONLY `{rootlist âˆª open âˆª outbox-pending}` via revision-gated `/diff` through the 6-permit semaphore; WARM/COLD lazy.** *Why:* caps reconnect work at O(1â€“4) regardless of library size; the stored revision makes each `/diff` a cheap `up_to_date`; the semaphore prevents the burst fan-out the reference is vulnerable to (`DealerClient.cs:704-734` re-opens with no catch-up and no global cap).

**D10 â€” Is the dealer a correctness dependency?** **No.** Correctness comes solely from the server-authoritative revision + `GET /diff?revision=stored` on every open and reconnect-eager; the dealer only lets a resident baseline skip the round-trip. Disabling it degrades to poll-on-access (COLD-for-all) and is still correct. *Why:* the byte-equal parent gate + verbatim server revision reconcile to current state without any push â€” exactly the offline-first contract.

**D11 â€” Phase ordering?** **Strict P1 (read) â†’ P2 (persist+diff) â†’ P3 (changes+edits), no overlap; each ships a green gate (live probe / 10k durability test / anti-herd test) before the next.** *Why:* mirrors architecture Â§10 (read spine before write spine); P2's durable revision-gated membership must exist before P3's optimistic edits can safely rebase (an edit before durable truth creates unsynced state with nothing to reconcile against).

**D12 â€” Rootlist incremental op-apply / `TreeOp` edits now, or read-only?** **Read-only in P1/P2 (fetch + tree + RevisionDelta full/diff-Contents); defer rootlist op-apply and `TreeOp` to architecture Â§10 step 5.** *Why:* the reference ships no rootlist op-applier because index-based rootlist REM/MOV are drift-prone; the rootlist is still needed in P1 as the playlist-discovery surface (our Store has no list-my-playlists query).

**D13 â€” Honor `ListItems.truncated` / page large playlists, when the reference never does?** **Yes â€” exceed the reference: single un-paginated GET by default, but check `truncated` and page via `from=`/`length=` when set.** *Why:* the reference requests everything in one GET and never reads `.Truncated`, so a server-side cap would silently yield a partial list; the params are already plumbed â€” cheap insurance for 10k+ lists.

**D14 â€” Conflict (409) reconciliation scope?** **P3 implements real 409 â†’ refetch-revision â†’ rebase-by-`item_id` (key-based REM, recomputed-absolute MOV) â†’ re-POST, with optimistic-apply + rollback; reorder-rebase and collaborative `multiple_heads` are best-effort/deferred.** *Why:* the reference only emits a "refetch and retry" message string with no implemented loop and durably outboxes only bulk-add â€” offline-first must actually rebase; `item_id` makes add/remove rebase deterministic, while index-based reorder rebase is the drift hazard (consistent with architecture Â§11).

**D15 â€” When do the additive membership tables land relative to UI?** **Now, before any playlist UI binds to `Playlist.Tracks`.** *Why:* the fat-blob cost is latent today (every caller passes `Tracks=null`); it becomes a live LOH regression the moment a UI populates `Tracks` and persists through `CachedStore.cs:59`. Landing the thin shape first avoids a costly retrofit.