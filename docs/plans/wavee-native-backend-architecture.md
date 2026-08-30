# Wavee backend — technical architecture (five engines over a queryable store)

> **Supersedes** the earlier port-shaped draft. That version mirrored WaveeMusic's subsystem list (download manager,
> play-history store, gabo subsystem, client-token subsystem, rootlist tree…) — a 1:1 transcription of Spotify's protocol
> layering. This version asks what those subsystems *do* and collapses them into **five general engines over one queryable
> store**. WaveeMusic's "subsystems" become **configurations** of these engines, not bespoke code.
>
> The [`wavee-backend-architecture-gaps.md`](./wavee-backend-architecture-gaps.md) companion is the **requirements
> checklist** (13 capabilities + corrections, code-grounded). §9 here maps every one onto an engine — that mapping is the
> proof the design is complete *and* that it isn't a port.
>
> **Locked:** local-first; built inside `src/apps/Wavee` (no new library csproj); `Wavee.Core` stays the zero-dep seam; NativeAOT
> + low-alloc + signals-first (no `System.Reactive`); protocol *mechanics* (handshake/Shannon/protobuf/queue-ladder/gabo)
> lifted as pure functions, *structure* redesigned; **audio is in-process** behind a swappable `IAudioEngine` seam (an
> out-of-process flip stays possible; a separate x64 PlayPlay key-oracle is an *optional* narrow helper, never load-bearing).
> **Deferred (out of scope now):** the offline download manager ("make available offline"/pin UX), synced-lyrics, and the
> **audio engine itself** — built now only as a **stub decrypt / silent `IAudioEngine`** (engine ④'s queue/reducer/projection
> logic still runs against the stub).

---

## 0. The shape in one picture

```
                    Wavee.Core seam (facets)  ← thin adapters over the engines
   ┌──────────────────────────────────────────────────────────────────────────────────────────┐
   │  ISpotifySession   IMusicLibrary/IMutationSource   ICatalogSource   IConnectDevices   IPlaybackPlayer │
   └───────┬───────────────────┬────────────────────────────┬────────────────────┬───────────┬──┘
           │                   │                            │                    │           │
   ┌───────▼───────┐  ┌────────▼────────┐  ┌────────────────▼───────┐  ┌─────────▼──────┐  ┌─▼──────────────┐
   │ ⑤ SessionCtx  │  │ ② Mutation      │  │ ① Resource engine      │  │ ③ Transport    │  │ ④ Playback     │
   │ account·mkt·  │  │   engine        │  │   reactive cached reads │  │   channels +   │  │   reducer →    │
   │ locale·tier   │  │   durable intent│  │   keyed + freshness pol │  │   middleware   │  │   event log    │
   │ (Signal<T>)   │  │   per-type strat│  │   (configs = surfaces)  │  │   (3 channels) │  │   → projections│
   └───────┬───────┘  └────────┬────────┘  └────────────┬───────────┘  └───────┬────────┘  └───────┬────────┘
           │  keys/gates/partitions │ writes             │ reads/projects        │ I/O               │ events
           └──────────────────────┬─┴────────────────────┴───────────────────────┘                   │
                                  ▼                                                                    │
   ┌───────────────────────────── THE STORE (queryable spine, source of truth) ────────────────────────▼──────┐
   │  SQLite (durable, per-account)  +  hot SoA cache  ·  entities are INDEXED COLUMNS (offline search/sort)   │
   │  structured tables (queried/joined) + a generic resource_cache (opaque view-blobs) + per-entity signals   │
   └─────────────────────────────────────────────────────────────────────────────────────────────────────────┘
                                  │ pipe                          ▲ filesystem
                          ┌───────▼────────┐               (LocalSource scanner = another populator)
                          │ IAudioEngine   │ ← IN-PROCESS decode/decrypt/output (RT thread) · STUB now · x64 key-oracle optional (PlayPlay)
                          └────────────────┘
```

Every read is `Resource.Use(key) → Loadable<T>`; every write is `Mutation.Apply(intent)`; every network op is
`Transport.Request(channel, …)`; playback emits an event log that projections consume; `SessionContext` is the ambient
key/gate/partition for all of it. Five orthogonal engines; the protocol surfaces (Mercury/Pathfinder/Connect/
extended-metadata) live *inside* the Transport and the Resource configs, not as top-level subsystems.

---

## 1. The Store — queryable spine

The one structural correction from the gap hunt: **entities are first-class indexed columns, not an opaque blob** — so
the store doubles as the offline search/sort/filter index. The store has two halves: **structured tables** (the data you
query/join/sort) and a **generic `resource_cache`** (opaque view-blobs you fetch-and-render but never query).

### 1.1 Durable schema (SQLite, WAL, per-account DB file; `Microsoft.Data.Sqlite`; migrations before first paint)

```sql
-- STRUCTURED (queryable). Real columns + COLLATE NOCASE indexes => offline LIKE/sort/filter without FTS5.
entities(uri TEXT, market TEXT, locale TEXT, kind INT,
         title TEXT COLLATE NOCASE, artist_name TEXT COLLATE NOCASE, album_name TEXT COLLATE NOCASE,
         publisher TEXT COLLATE NOCASE, duration_ms INT, flags INT,         -- availability/explicit/relinked
         relinked_uri TEXT, blob BLOB,                                       -- blob = the rich tail not needed for query/sort
         etag TEXT, fetched_at INT, ttl INT, PRIMARY KEY(uri, market, locale));
CREATE INDEX ix_entities_title  ON entities(title  COLLATE NOCASE);
CREATE INDEX ix_entities_artist ON entities(artist_name COLLATE NOCASE);

collection_items(account TEXT, set_id TEXT, item_uri TEXT, added_at INT, sync INT,   -- sync: Confirmed/Pending/Failed
                 PRIMARY KEY(account, set_id, item_uri));
collection_rev(account TEXT, set_id TEXT, revision TEXT, synced_at INT, PRIMARY KEY(account, set_id));

playlists(uri TEXT PRIMARY KEY, owner TEXT, kind INT, base_rev BLOB, name TEXT, blob BLOB);  -- base_rev: bytes, not snapshot_id string
playlist_items(playlist_uri TEXT, position INT, item_id TEXT, item_uri TEXT, added_by TEXT, added_at INT,
               PRIMARY KEY(playlist_uri, position));   -- item_id = stable per-row identity for remove/reorder by key
rootlist(account TEXT, position INT, kind INT, uri TEXT, group_name TEXT, depth INT,  -- folders + playlist URIs interleaved
         PRIMARY KEY(account, position));

play_history(account TEXT, played_at INT, uri TEXT, context_uri TEXT, ms_played INT, reason_end TEXT);  -- ring; retention by age
episode_progress(account TEXT, uri TEXT, position_ms INT, updated_at INT, PRIMARY KEY(account, uri));
audio_keys(file_id TEXT PRIMARY KEY, key BLOB);                       -- 16-byte AudioKeys, valid forever (FileIds immutable)
audio_cache(file_id TEXT PRIMARY KEY, path TEXT, bytes INT, chunks BLOB, complete INT, last_used INT);  -- chunks = resume bitmap

-- GENERIC view-blob cache (Home, Pathfinder shelves — fetched & rendered, never queried)
resource_cache(rkey TEXT PRIMARY KEY, blob BLOB, etag TEXT, revision TEXT, fetched_at INT, ttl INT);

-- writes
outbox(id INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT, entity_key TEXT, op BLOB, logical_ts INT,
       status INT, attempts INT, idem_key TEXT, created_at INT);   -- AUTOINCREMENT id = the monotonic drain tiebreak
dead_letter(id INTEGER PRIMARY KEY, type TEXT, entity_key TEXT, op BLOB, reason TEXT, at INT);

meta(key TEXT PRIMARY KEY, value TEXT);
```

Notes the gap hunt forced and that now live in the schema: per-`account` scoping on every user table (engine ⑤ picks the
DB file — see §7); `(uri, market, locale)` PK so a travel/VPN/tier flip doesn't serve stale availability; `relinked_uri`
+ `flags` so the offline-playability gate and play-resolve agree on the *effective* file; `playlist_items.item_id` for
key-stable reorder/remove; `base_rev BLOB` + ops (not a `snapshot_id` string); `outbox.id AUTOINCREMENT` as the
within-second tiebreak; `audio_keys` separate from `audio_cache` so a cached file is decryptable after restart;
`play_history`/`episode_progress` as real tables.

### 1.2 Hot layer & query API

A hot SoA cache (`SlabAllocator<TrackRow>` + a segment-evictable string arena + an open-addressed `gid→handle` map) sits
read-through over SQLite for the 10k-row scroll path; **non-track entities (artist/album/show) are on-demand-minted blobs**
(honest: they aren't POD-SoA-able — ~20 scalars + nested facets). The store exposes:

```csharp
interface IStore {
  ref readonly TrackRow Row(TrackHandle h);                       // zero-copy hot read
  Loadable<T> Get<T>(string uri, SessionContext ctx);             // typed entity read (mints from blob if cold)
  QueryHandle Query(QuerySpec spec);                              // offline filter/sort/search over indexed columns -> handle page
  IStoreWriter Writer();                                          // projection target (parse-project-drop, bounded Gen0)
  long Version(string uri);                                       // per-entity monotonic version (drives signals)
  void Bump(string uri);                                          // raise version -> UI-edge signal refresh (marshalled via post)
}
```

`Query` is what makes offline search/sort/filter a **store operation**, not a "search subsystem": `WHERE title LIKE ?`
over the NOCASE index, or an in-memory handle sort once a container is fully materialized (engine ① drives the
completion-fetch). Online search is just a Resource (§3).

---

## 2. Engine ① — Resource (reactive cached revalidating reads)

The single abstraction behind entity hydration, library reads, playlists, Home, Pathfinder views, and online search. A
**Resource** is a typed reactive cache; you *configure* one with a spec — you don't write a subsystem.

```csharp
interface IResource<TKey, TValue> {
  Loadable<TValue> Use(TKey key);            // Loading | Ready(value, IsStale) | Error  — a reactive handle the UI binds
  ValueTask Revalidate(TKey key);            // SWR refresh per the freshness policy
  ValueTask MaterializeAll(TKey key);        // force full hydration (for sort/filter over a large container)
  void Invalidate(TKey key);                 // drop -> next Use refetches
}

// you DEFINE a resource by handing the engine a spec (data, not code):
record ResourceSpec<TKey, TWire, TValue>(
  string Name,
  CacheScope Scope,                                       // which SessionContext dims key the cache (see ⑤)
  FreshnessPolicy Freshness,                              // strategy, below
  Func<TKey, SessionContext, FetchPlan> Plan,            // how to ask the Transport (which channel, route, batching)
  Func<TWire, TKey, IStoreWriter, TValue> Project,       // wire bytes -> store rows + value (parse-project-drop)
  RevalidationTopic? LiveTopic = null);                  // a Transport event topic whose pushes invalidate keys

abstract record FreshnessPolicy {
  record Etag(TimeSpan Ttl)                              : FreshnessPolicy;  // conditional batch revalidate (extended-metadata)
  record RevisionDelta(Func<TKey,string> SetId)         : FreshnessPolicy;  // sync-token deltas, FullSync fallback (library sets)
  record SnapshotRevision(bool ParentRevGate = true)    : FreshnessPolicy;  // base-rev + ops; apply-without-/diff iff parent eq
  record PollWhole(TimeSpan Ttl, bool SuspendInPlayback) : FreshnessPolicy; // re-fetch whole + ApplyDiff (Home/Browse)
  record Immutable                                       : FreshnessPolicy; // gids, audio bytes
}
```

**What the engine provides (so configs stay tiny):** in-flight dedup + same-key coalescing; the stale-while-revalidate
lifecycle (serve cached instantly, revalidate by policy, patch on diff); conditional fetch (sends `etag`s, applies only
changed); **completion-fetch** for `MaterializeAll`; **live revalidation** (subscribes `LiveTopic` so a dealer push
invalidates keys — and for `SnapshotRevision`, applies ops in-place only when the **parent-revision-equality gate**
holds, else falls back to a delta/FullSync); request **batching** (the `FetchPlan` declares chunking — extended-metadata
is a hard `HttpChunkSize = 500` chunk-and-merge, *not* "POST-body unbounded"); and writing through `Project` into the
store with bounded-Gen0 parse-project-drop.

**The configs (WaveeMusic's "read subsystems" become these — note how thin):**

| Resource | Key (Scope) | Freshness | FetchPlan channel | LiveTopic |
|---|---|---|---|---|
| `Entities` | uri ·(market,locale) | `Etag(7d)` | ExtendedMetadata (chunk 500) | — |
| `LibrarySet` | set ·(account) | `RevisionDelta` | Spclient | `hm://collection/*` |
| `Playlist` | uri | `SnapshotRevision(gate)` | Spclient | `hm://playlist/*` |
| `Rootlist` | account | `RevisionDelta` | Spclient | `RootlistModificationInfo` |
| `Home` | (account,facet) | `PollWhole(5m,suspend)` → `resource_cache` | Pathfinder | — |
| `PathfinderView` | (opHash+appVer, vars) | `Etag/TTL` → `resource_cache` | Pathfinder | — |
| `OnlineSearch` | query ·(market,locale) | `Etag(60s)` | Pathfinder | — |

The two-phase open is now **composition**, not a special pipeline: a container Resource (`Playlist`/`LibrarySet` — the
ordered URI list, instant from cache) + the `Entities` Resource (the rows, visible-first batched). Sort/filter on a metadata
field → `Entities.MaterializeAll` then `Store.Query`. Discography/album/show ride `PathfinderView` (keys returned inline →
no shell+hydrate split). Pseudo-URI sets (ylpin) fetch per-item with placeholder rows. The per-surface variance the gap
hunt flagged is per-*config*, and the engine is one.

---

## 3. Engine ② — Mutation (durable intents + per-type sync strategy)

Every write — save, follow, add-to-playlist, reorder, move-in-folder — is a durable intent. The engine owns the optimistic
apply + the outbox + the reconnect drain + dead-letter; each *resource type* supplies a **strategy**.

```csharp
interface IMutationStrategy {
  OutboxOp Coalesce(OutboxOp? existing, Intent incoming);              // one-row LWW? or append cursor-ops (no dedupe)?
  void     ApplyOptimistic(Intent i, IStoreWriter store);             // store reflects it instantly (sync=Pending)
  ValueTask<ReplayResult> Replay(OutboxOp op, ITransport t, SessionContext ctx);
  ConflictResolution Resolve(OutboxOp op, ServerReject r);            // multiple_heads / 409 / stale base-rev
  void     Rollback(OutboxOp op, IStoreWriter store);                 // terminal failure -> revert + dead_letter
  bool     OfflineQueueable { get; }                                  // false => online-only (pins, cover upload)
}
```

Strategies (the only per-type code):

- **`SetReplay`** (saved tracks/albums/artists/shows, follows): coalesce → one row, latest end-state; replay → idempotent
  POST of the desired state; conflict → **local-intent-wins for touched items only**; the `(account, set_id, item_uri)`
  key lets "unpin but keep liked" coexist. Set membership uses the `hm://collection/<set>` path parse to route.
- **`OpRebase`** (playlist content edits): coalesce → **append** ops with a persisted cursor (**MUST NOT dedupe**, chunk
  500/op, resume-not-replay on partial); replay → ops against `base_rev`; conflict → `multiple_heads` ⇒ refetch +
  **rebase membership by `item_id`** (reorder rebase deferred → best-effort/last-writer); capability-gated for
  collaborative (CONTRIBUTOR vs owner — the `Capabilities{can_edit_items}` from `SelectedListContent`).
- **`TreeOp`** (rootlist: reorder sidebar, move playlist between folders): op-based against the rootlist revision; its own
  `LiveTopic`.
- **`OnlineOnly`** (`OfflineQueueable=false`: pin, cover-image upload — a server-minted two-step upload): never queued
  offline; immediate-or-toast.

Outbox: `id AUTOINCREMENT` tiebreak, `idem_key` (safe re-drain), `logical_ts`. **Reconnect** = drain (per type) →
`Resource.Revalidate` the affected sets/playlists (delta) → reconcile/clear `Pending` → resume live pushes, with the
**FullSync-preserves-Pending** carve-out (a too-stale delta must not delete optimistic rows before the outbox drains) and
the resumed-push revision-gate (no double-apply). Terminal failures → `dead_letter` + compensating store revert (the "saved
forever" bug closed). Downgrade safety: unknown `op` types are *kept*, never dropped, across migration.

---

## 4. Engine ③ — Transport (multiplexed channels + middleware pipeline)

Not "two primitives." One transport multiplexes three channel families and applies a uniform middleware stack.

```csharp
enum Channel { ApMercury, Spclient, Pathfinder, ExtendedMetadata, DealerWs, Login5, ClientToken }

interface ITransport {
  ValueTask<Resp> Request(Channel ch, Route route, ReadOnlyMemory<byte> body, CancellationToken ct);
  IObservable<WireEvent> Events(string topicPrefix);     // dealer pushes by hm:// prefix
  ValueTask Reply(RequestId id, ReplyFrame body);        // ③a — the dealer server→client REQUEST→reply primitive
  ValueTask Publish(PutState p);                         // ③b — device-state announce to connect-state/v1/devices
}
```

- **AP/Shannon socket** (one serialized worker, no locks): Mercury request/reply (a `Dictionary<seq,TCS>` correlation
  table), AudioKey `0x0C/0x0D`, `ProductInfo 0x50`, keep-alive `Ping/Pong/PongAck`, packet I/O over `ArrayPool`.
- **Dealer WebSocket** (its own ping cadence, *separate* from AP): `Events()` (cluster, `hm://collection/*`,
  `hm://playlist/*`, `RootlistModificationInfo`), the **REQUEST→reply** third primitive (ack-of-receipt within ~10 s or
  Spotify 502s; the real *result* rides a follow-up `Publish`), and `Publish(PutState)` (the device must embed a live
  `PlayerState` — see §5/§6 back-edge).
- **HTTPS channels** (`SocketsHttpHandler`, concurrent, limiter-gated): Spclient, Pathfinder, ExtendedMetadata, and the
  **side-channels** `Login5` + `ClientToken` — which are **HTTPS, not AP callers** (the doc's old error). 

**Middleware pipeline** (wraps every `Request`, so these stop being "subsystems"):

```
ContextMiddleware   → stamp Country + Catalogue + Locale from SessionContext (⑤)
ClientTokenMiddleware → attach client-token header; refresh ~5 min; hashcash-PoW; carry the allowlisted app-version
AuthMiddleware      → attach access-token; on 401 → Login5 refresh → retry once; persist rotated ReusableAuthCredentials
RateLimitMiddleware → shared limiter; 429 → Retry-After backoff (so the §2 completion-fetch fan-out can't self-DoS)
```

`client-token`, `login5`, `401-refresh`, `429-backoff`, and market-stamping are all *middleware*, applied once, uniformly —
not five scattered code paths.

---

## 5. Engine ④ — Playback as an event source with projections

The reducer stays **pure**: `(state, input) → (state', PlaybackEvent[])`. It does **no I/O**. The emitted event log fans to
independent subscribers — which dissolves both the "pure reducer can't emit gabo" conflict and the "two authorities" problem.

```csharp
// QueueCore stays pure (the lifted advance-ladder/buckets/shuffle); autoplay is NOT inside it (it's I/O):
//   the ladder terminates at EndOfContext and the reducer surfaces an async AutoplayRequest.
record PlaybackEvent(EvKind Kind, long AtMs, TrackRef Track, PlayReason Reason, /*…*/);
interface IPlaybackProjection { void OnEvent(in PlaybackEvent e); }
```

Subscribers (each independent, the only place I/O happens):

- **`GaboEmitter`** — interval-bracketed `trackstarted/trackdone`, per-play `PlaybackId`, monotonic `_sequenceId`,
  `reason_start/reason_end`, the anti-fraud context block; durable retry; the *only working transport* (mercury/HTTPS 404).
  Gated off by private-session `SuppressPlayHistory`.
- **`PlayHistoryWriter`** — appends `play_history` + `episode_progress`; feeds `PlayCount`, recently-played, listen-stats.
- **`PutStatePublisher`** — builds a live `PlayerState` from reducer state and calls `Transport.Publish` — the **Connect
  back-edge** the clean split previously had no channel for.
- **`NowPlayingSignals`** — the UI-edge: current track, 1 Hz position interpolated from `(PositionMs, ts, IsPlaying)`.

**Active-device duality, resolved:** the reducer has **one** input stream with two sources — local intents *or* the cluster
`PlayerState`/`RawNextQueue` from `Transport.Events` (when a remote device is active). Same projections either way. Cluster
`Restrictions` (grey controls) flow in as state. Volume is converted once at the edge (`0..1` seam ↔ `0..65535` wire), and
a `Publish` echo (`X-Wavee-Echo=self`) is de-duped.

**Resolve** (next track) is its own step feeding the reducer: relink → effective `fileId` → quality ladder → parallel
`cdnUrl + AudioKey(effectiveFileId)` → hand to `IAudioEngine`; gapless/crossfade is prefetch-staged (AudioKey-first).
Premium-gating (shuffle-only/no-seek/bitrate/greyed) is an **entitlement input** from ⑤.

**`IAudioEngine` — in-process, swappable, stubbed now.** Managed decode (NVorbis), AES-128-CTR decrypt, DSP/EQ, and WASAPI
output run **in-process** on a dedicated real-time-priority thread with a lock-free ring buffer and an **allocation-free
callback**; a 100–200 ms buffer absorbs any ephemeral-GC stop-the-world pause (a streaming player isn't a live-input DAW —
WaveeMusic's out-of-process split was driven by its MVVM-heap Gen2 stalls, which fluent-gpu's low-alloc design removes). The
boundary is a **seam** (`IAudioEngine` behind the `AudioHostClient` shape) so it can flip to out-of-process later if load
testing shows real GC-driven dropouts — a reversible decision, not a one-way door. The proprietary x64 **PlayPlay key-oracle**
(one function: challenge → 16-byte key) is the *only* thing that ever needs a separate process, and only for that fallback.
**Current scope:** none of this is built — `IAudioEngine` is a **silent stub with a stub decrypt function**, and engine ④
(reducer/queue/projections) runs against it on synthetic position/state, so control logic, history, gabo, and now-playing
stay exercisable. `IPlaybackState.Queue` is reducer state, not an audio fact.

---

## 6. Engine ⑤ — SessionContext (ambient key · gate · partition)

```csharp
record SessionContext(AccountId Account, Market Country, string Catalogue, string Locale,
                      Entitlement Tier, bool ExplicitFilter);
Signal<SessionContext> Session;   // reactive; sourced from login + the live ProductInfo 0x50 re-fold
```

One ambient value does three cross-cutting jobs that were three separate gap-hunt "fixes":

- **Keys** Resources — `CacheScope` declares which dims matter; a market/locale/tier change (travel, VPN, premium→free)
  invalidates exactly the scoped keys (the `(uri,market,locale)` correctness bug becomes an engine property).
- **Gates** features — the Playback resolve/reducer take `Tier`/`ExplicitFilter` as input (premium gating); the UI binds the
  entitlement signal so a live Free↔Premium flip reaches it reactively.
- **Partitions** storage — `Account` selects the SQLite DB file (per-account isolation); account switch = swap the file +
  clear hot caches. No cross-account leak of library/outbox/cached audio.

---

## 7. Wiring & seam mapping

`Services.CreateReal` (the hand-wired, reflection-free composition root — still the single swap from `CreateFake`) builds,
in order: `Signal<SessionContext>` → `Transport` (channels + middleware) → `Store` (opened on the account's DB file) →
`ResourceEngine` + the resource configs → `MutationEngine` + the strategies → `PlaybackReducer` + projections. Then thin
**facet adapters** present the engines as the seam:

| `Wavee.Core` facet | Built from |
|---|---|
| `ISpotifySession` | ⑤ SessionContext + ③ AuthMiddleware (login/refresh; `AuthChallenge` for device-code/QR) |
| `ICatalogSource` (`StreamTracksAsync`, paged) | ① `Playlist`/`LibrarySet` (list) ∘ `Entities` (rows) |
| `IMusicLibrary` / `IMutationSource` | ① `LibrarySet` reads + ② `SetReplay` writes |
| search / sort / filter | `Store.Query` (offline) + ① `OnlineSearch` |
| `IConnectDevices` / `IRemoteSource` | ③ Dealer events + `Reply` + `Publish` + ④ `PutStatePublisher` |
| `IPlaybackPlayer` / `IPlaybackState` | ④ reducer + `NowPlayingSignals` |
| Home / Browse | ① `Home` / `PathfinderView` → seam Home records |

`AggregateCatalog`/`PlaybackBridge`/`LibraryBridge`/the UI never change shape — the engines sit behind the existing seam.
`LocalSource` is just another federated source whose **populator is a filesystem scanner** instead of the Transport (same
store, same Resource shape; `Authority=Filesystem`); `WaveePlaylistSource` is `Authority=LocalDB` (writes are
`OfflineQueueable=Direct`, no outbox).

---

## 8. NativeAOT, allocation & threading

- **Engines are generic but AOT-clean:** `ResourceSpec`/`IMutationStrategy`/projections are closed delegates wired at the
  composition root — no reflection, no `System.Reactive`. Closed generics over concrete `TKey/TWire/TValue` per config; no
  `MakeGenericType`. Per-operation `JsonSerializerContext`; protobuf via generated `Google.Protobuf` with the `Any`/
  descriptor path **proven-by-publish** (it rides `Entities`' hydrate — the one unavoidable spot; pin feature-switches).
- **Bounded-Gen0 everywhere:** `Project` is parse-one→write-row→drop, in a tight loop; nothing retains a parse graph;
  `ArrayPool`/arena for wire+decode scratch; SoA + segment-evictable string arena for the retained hot set; the generic
  `resource_cache` blobs are written straight from a pooled `IBufferWriter`.
- **Threading:** the AP socket is one serialized worker; HTTPS channels are concurrent under the shared limiter; the store
  has a write connection + a separate read connection (WAL); **every** store `Bump` and Resource/Mutation completion
  marshals to the UI thread via `post` before touching a `Signal` (audit `LibraryBridge`/`LibraryStore`, not just
  `PlaybackBridge`). `SimpleSubject.OnNext` copies its subscriber list per publish — fine at these rates; full-sync's 10k
  version bumps are coalesced (150 ms) and split sidebar-shape vs content so the rootlist tree-diff doesn't run per save.

---

## 9. Requirements coverage (gap-hunt checklist → engine)

Proof the five engines satisfy the companion checklist *without* a per-item subsystem. (A = deferred.)

| Gap-hunt requirement | Satisfied by |
|---|---|
| B persisted AudioKey · opportunistic offline decode | Store `audio_keys` table; predicate `complete ∧ key-on-disk ∧ effective fileId` |
| C play-history/recently-played · D gabo reporting · PutState back-edge | ④ projections (`PlayHistoryWriter`, `GaboEmitter`, `PutStatePublisher`) |
| E offline search/sort/filter | Store **queryable indexed columns** + `Store.Query`; online = ① `OnlineSearch` |
| F Home/Browse offline + cold-launch | ① `Home`/`Browse` config (`PollWhole`, persisted to `resource_cache`) |
| G Pathfinder view-plane cache (30+ ops) | ① `PathfinderView` config, keyed `(opHash+appVer, vars)` |
| H playlist folders / rootlist tree | ① `Rootlist` Resource + ② `TreeOp` strategy + its `LiveTopic` |
| I Connect announce + REQUEST→reply third primitive + back-edge | ③ `Publish`/`Reply` + ④ `PutStatePublisher` |
| J per-account partitioning | ⑤ SessionContext picks the DB file |
| K collaborative permissions | ② `OpRebase` capability-gated variant |
| L client-token · login5 · 401/429 | ③ middleware (`ClientToken`/`Auth`/`RateLimit`) + HTTPS channels |
| M dead-letter / optimistic rollback | ② engine (`Rollback` + `dead_letter`) |
| cache key (country,catalogue,locale) · premium gating · ProductInfo re-fold | ⑤ SessionContext (key/gate/source) |
| outbox two-disciplines · `multiple_heads` · `item_id` · cover-upload-not-replayable | ② strategies (`SetReplay`/`OpRebase`/`OnlineOnly`) |
| relink + quality-ladder · gapless/crossfade · 5 s cadence · active-device duality | ④ resolve step + reducer input-source switch |
| two-phase varies by surface · `HttpChunkSize=500` · parent-rev gate | ① per-config `FetchPlan`/`FreshnessPolicy` |
| network-connectivity FSM (critic) · server-time offset (critic) | ③ Transport state (online/offline drives ① SWR cold/warm + ② drain) |

Net: thirteen "subsystems" + the corrections are **configs/strategies/middleware/projections** of five engines. New
surfaces (audiobooks, credits, concerts) are a new `Resource` config — not new code paths.

---

## 10. Roadmap (build engines first, then configs)

The sequencing inverts from the port plan: build the **engines** (small, generic, testable headlessly), then each
"subsystem" is a config. Milestone-0 still de-risks NativeAOT.

| # | Build | Effort |
|---|---|---|
| 0 | Store (queryable schema + hot SoA + `Query` + signals) + **Milestone-0**: AP login → one `Entities` hydrate → NativeAOT publish green | 2 wk |
| 1 | ③ Transport (3 channels + middleware) + ⑤ SessionContext | 2–3 wk |
| 2 | ① Resource engine + configs `Entities`/`LibrarySet`/`Playlist` (the read spine) | 2–3 wk |
| 3 | ② Mutation engine + `SetReplay`/`OpRebase` + reconcile/dead-letter (the offline write spine) | 2–3 wk |
| 4 | ④ Playback reducer + projections (`NowPlaying`/`PlayHistory`/`Gabo`/`PutState`) + resolve | 2–3 wk |
| 5 | Configs: `Home`/`PathfinderView` · `Rootlist`+`TreeOp` · `OnlineSearch` · `LocalSource` scanner · `WaveePlaylist` | 3–4 wk |
| 6 | Connect command taxonomy (enact + `Reply` + `Publish` loop) · premium gating · active-device | 2 wk |
| 7 | **`IAudioEngine`** real decode/decrypt/output (in-process, RT thread; x64 key-oracle iff PlayPlay) — **deferred; stub decrypt only now** | 1–2 wk + native pkg |

**Critical path:** Store → Transport/SessionContext → Resource → Mutation → Playback. **~7–10 months solo.** Offline browse
(Store + `LocalSource` + cached `Entities`) and offline library writes (② `SetReplay`) are green by step 5 — well before
any real audio (the engine is stubbed). **Deferred:** the **audio engine** (stub decrypt now), offline download manager, synced-lyrics. **Out of scope v1:** roaming local-file
overlay, on-device AI, WebView2/OS-Share.

---

## 11. Deferred & risks

- **Deferred now (per current scope):** the **audio engine** (`IAudioEngine` decode/decrypt/output) — a **stub decrypt /
  silent stub** only, with engine ④'s control logic running against it; the offline **download manager** (pin/"make
  available offline"); and **synced lyrics**. The opportunistic-offline-playback and local-file-playback *designs* hold
  for when `IAudioEngine` is built in-process behind the seam.
- **Prove-by-publish:** `Google.Protobuf` `Any`/descriptor on the `Entities` path; `SQLitePCLRaw`/`e_sqlite3` native asset
  under ilc; the `JsonExtensionData` V1/V2 trait re-deserialize.
- **Allocator coupling:** the SoA/arena/async-command primitives live in `Backend/Runtime/` (portable, no `FluentGpu.Engine`
  ref) to keep the backend host-agnostic.
- **Open conflict edge:** offline playlist *reorder* rebase is best-effort (deferred); two-writer collaborative beyond
  capability-gating is best-effort. **net11→net10** down-pin audit (`System.Threading.Lock`/preview BCL) before declaring
  the protocol-mechanics lift mechanical. DPAPI credential storage is Windows-only (flag vs the macOS ledger).
