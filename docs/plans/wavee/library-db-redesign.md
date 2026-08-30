Anchors confirmed (CachedStore.cs:38/124/212, SqliteColdStore.cs:199-245/511-513 match both research streams). Producing the final design.

# Wavee Library Persistence Redesign â€” Final Design

**Problem:** 168 MB `library.db` and 8â€“12 s cold launch for a 216-liked-track library, caused by (a) persisting every hydrated entity forever as full JSON with no pin/TTL/LRU policy (76â€“92 % orphan rates), (b) a frozen dual-table generation read via a 147k-row windowed UNION every launch, and (c) bulk-loading + JSON-deserializing the entire cold tier synchronously inside `root()` before `window.Show()`.

**Thesis:** Invert the contract. The cold tier becomes the source of truth with indexed point reads; the hot tier becomes a bounded cache seeded from the pin set. Identity (membership, revisions, outbox, overrides) is durable forever; entity/extension metadata is an evictable, byte-budgeted, TTL'd cache. Startup becomes O(pin set), then O(viewport) â€” independent of how much was ever hydrated.

---

## A. Target architecture

### A.1 Tiers

```
â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ library.db (single file, WAL) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”
â”‚                                                                                    â”‚
â”‚  IDENTITY TIER (durable, never evicted, loaded eagerly â€” it is tiny)              â”‚
â”‚    collection_items, collection_rev      liked/saved sets            ~216 + albums â”‚
â”‚    playlists, playlist_items             membership + base_rev       ~26.5k rows   â”‚
â”‚    rootlist                              sidebar structure                          â”‚
â”‚    outbox, dead_letter                   pending mutations                          â”‚
â”‚    video_override                        user video prefs                           â”‚
â”‚    recent_surfaces  (NEW)                last-opened detail pages    â‰¤ 50 rows     â”‚
â”‚    meta                                  schema_version, cache accounting           â”‚
â”‚                                                                                    â”‚
â”‚  CACHE TIER (evictable, byte-budgeted, TTL'd, safe to wipe)                       â”‚
â”‚    entity            (ONE table; replaces entities + localized_entities)           â”‚
â”‚    artist_overview   (NEW; fat artist facets, own TTL)                             â”‚
â”‚    localized_extension_cache             raw proto, swept + capped                 â”‚
â”‚    video_assoc                           swept with entity GC                      â”‚
â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜

MEMORY:  InMemoryStore (hot) = bounded LRU cache over the cache tier,
         seeded at startup with the pin head-set only; ColdFallback is the
         universal miss path (gate opened unconditionally).
```

**One DB file, not two.** The cache/identity split is *by table*, not by file (rejection rationale in Â§H). Cross-tier transactions (e.g. playlist adopt = membership + entity writes) stay atomic on the existing single writer thread.

### A.2 Schema sketch (end state, schema v8)

```sql
-- THE one entity table (v5 merges entities -> localized_entities; v8 restructures to this)
CREATE TABLE entity(
  uri          TEXT NOT NULL,          -- canonical spotify URI (opaque; never parse scheme here)
  locale       TEXT NOT NULL,          -- kept for future locale switches; all 'en' today
  kind         INTEGER NOT NULL,       -- EntityKind
  -- thin display-critical scalars (list rendering never touches payload):
  title        TEXT,
  subtitle     TEXT,                   -- joined artist names / owner name
  image_url    TEXT,
  duration_ms  INTEGER,
  flags        INTEGER NOT NULL DEFAULT 0,  -- explicit, has_video, availability bits
  album_uri    TEXT,                   -- pin-closure join column (tracks only)
  -- payload = everything else, compact:
  fmt          INTEGER NOT NULL DEFAULT 0,  -- 0=raw STJ JSON, 1=zstd(JSON), 2=zstd+dict
  payload      BLOB,                   -- LAST column: scalar reads never touch overflow pages
  -- cache accounting:
  size         INTEGER NOT NULL,       -- length(payload)+row overhead estimate
  updated_at   INTEGER NOT NULL,
  last_access  INTEGER NOT NULL,       -- DAY granularity, batched writes (Â§C.5)
  PRIMARY KEY(uri, locale)
) ;  -- rowid table (payload ~1KB >> WITHOUT ROWID threshold)

CREATE INDEX ix_entity_gc ON entity(kind, last_access);       -- victim selection
-- covering index for library list rendering (Phase 3):
CREATE INDEX ix_entity_list ON entity(uri, title, subtitle, image_url, duration_ms, flags);

CREATE TABLE entity_refs(               -- pin closure: pinned track -> its album/artists
  parent_uri TEXT NOT NULL, child_uri TEXT NOT NULL,
  PRIMARY KEY(parent_uri, child_uri));  -- WITHOUT ROWID ok (tiny composite rows)

CREATE TABLE artist_overview(           -- fat facets, split out of Artist
  uri         TEXT PRIMARY KEY,
  locale      TEXT NOT NULL,
  fmt         INTEGER NOT NULL DEFAULT 1,
  payload     BLOB,                    -- {discography refs, appears_on refs (â‰¤100), top_track uris,
                                       --  bio, extras, stats} â€” refs are (uri,kind,name,year,cover_url)
  size        INTEGER NOT NULL,
  fetched_at  INTEGER NOT NULL,        -- TTL 7d; disposable (ArtistV4 SWR re-derives cheaply)
  last_access INTEGER NOT NULL);

CREATE TABLE recent_surfaces(           -- pin reason: open/recent detail pages
  uri TEXT PRIMARY KEY, kind INTEGER, last_opened INTEGER);   -- capped at 50, LRU by last_opened

-- meta rows: 'schema_version', 'cache_budget_bytes' (default 64 MiB),
-- 'cache_bytes' (running SUM(size) of unpinned entity+overview+extension rows),
-- 'gc_last_run', 'vacuum_pending'
```

### A.3 Pin/cache split

**Pins are derived, not stored as a boolean** (Notion reason-model, computed at GC time â€” a stale pin bit can never leak). The pin set:

```sql
-- P0: directly pinned URIs (pure SQL)
SELECT item_uri FROM collection_items WHERE account=$a
UNION SELECT item_uri FROM playlist_items
UNION SELECT uri FROM playlists
UNION SELECT uri FROM rootlist WHERE uri IS NOT NULL
UNION SELECT entity_key FROM outbox
UNION SELECT uri FROM video_override
UNION SELECT uri FROM recent_surfaces
-- P1: closure â€” albums/artists of pinned tracks
UNION SELECT child_uri FROM entity_refs WHERE parent_uri IN (P0)   -- v8
-- until v8 lands, closure via JSON (payloads are still STJ):
--   SELECT json_extract(payload,'$.Album.Uri') FROM entity WHERE kind=$track AND uri IN (P0)
--   plus json_each(payload,'$.Artists') for artist refs
```

Plus the in-memory pins (now-playing, queue, current context) already enumerated by `Services.BuildPinSet` (Services.cs:199-212) â€” exported to GC as an exempt-URI set. Pins are **exempt from TTL and from the byte budget** but their bytes are tracked separately for the storage UI. Unpinning (unlike, playlist removal) does **not** delete â€” the row simply falls back into the TTL+LRU pool (browser "unpin â‰  purge" semantics; avoids re-fetch storms).

### A.4 Write path (end state)

All six `CachedStore.Upsert*` methods (CachedStore.cs:215-243) remain the single chokepoint, with three new behaviors:

1. **Pin-reachability gate (Option B from research â€” zero caller changes):** cold-persist iff URI âˆˆ hot saved sets âˆª resident membership âˆª rootlist âˆª outbox âˆª `BuildPinSet` âˆª recent_surfaces; albums/artists persist iff themselves pinned OR referenced by a pinned track's `AlbumRef`/`ArtistRefs` (available un-serialized at the chokepoint). Non-pinned writes go to hot only (memory, governed by the existing 4000-entity cap). Write ordering is safe: `SetMembership`/`SetSaved` land before hydration (PlaylistFetcher.cs:174â†’:44, CollectionFetcher.cs:138â†’:142).
2. **No-op elision:** 64-bit payload hash per URI in hot; skip `_cold.UpsertEntity` when unchanged (mirrors `SetSaved` elision at CachedStore.cs:244-252). Kills the `ProjectCachedExtensions` re-persist storm (MetadataService.cs:85-108).
3. **Thin-at-persist:** Album strips Tracks (exists) + MoreByArtist/ArtistsDetailed/OtherVersions (new); Artist strips TopAlbums/AppearsOn/TopTracks/Extras/Bio into `artist_overview` refs (Â§D).

### A.5 Read path (end state)

- Hot hit â†’ return (unchanged).
- Hot miss â†’ `ColdFallback` PK lookup (`SqliteColdStore.GetEntity`, single-table probe after v5) â†’ decompress â†’ promote to hot. **The `HasEvictedEntities` gate at CachedStore.cs:124 is deleted** â€” ColdFallback is always live. Point lookups are microseconds (sqlite.org/np1queryprob); this is the whole "lazy tier".
- Artist page â†’ artist core from `entity` + async `artist_overview` fetch on open; refs joined to standalone album rows by `ArtistDiscography.Assemble` (which already knows how, ArtistDiscography.cs:47-57); null facets hide (the page's existing contract).
- List rendering (Phase 3) â†’ covering-index scalar projection, payload never decoded for scrolling.

---

## B. Startup sequence redesign

No engine change required. `FluentApp.RunCore` keeps calling `root()` at FluentApp.cs:149 before `Show()` at :191 â€” we make `root()` cheap instead. (Hoisting `Show()` is optional polish, Â§H.)

**Ordered sequence:**

| # | Step | Blocks first paint? | Code hook |
|---|------|--------------------|-----------|
| 1 | `Program.Main` preamble (settings, locale, theme) | yes (ms) | unchanged |
| 2 | Engine bring-up (window, D3D12, DirectWrite, images) | yes (~0.3-0.7 s) | FluentApp.cs:114-147, unchanged |
| 3 | `root()` â†’ `WaveeApp` ctor â†’ `Services.CreateReal` â†’ `new SqliteColdStore(...)` â€” open + PRAGMAs + `Migrate()` (v5-v7 are cheap after the one-time run) | yes (ms) | Services.cs:295 |
| 4 | `new CachedStore(cold, deferred: true)` â€” ctor loads **only**: `LoadAllSaved()` (216 + saved albums/artists â€” feeds `SavedUris`/`IsSaved`/counts at first render), `LoadAllVideoOverrides()`, rootlist stays lazy. Sets `_coldNotFullyResident = true`. **No entity replay. No video_assoc bulk load** (moved to warm task). | yes (tens of ms) | CachedStore.cs:33-42 rewritten; the `foreach Replay` at :38 deleted |
| 5 | `window.Show()` + first frame = LoginView or shell skeleton (first authenticated-off frame needs zero entities â€” WaveeApp.cs:215-221) | â€” | FluentApp.cs:191 |
| 6 | **Warm task** (background, `Task.Run` from `CachedStore` ctor): under `using (_hot.BeginBulk())` (CachedStore.cs:212 â€” fixes the O(n log n) backstop thrash), load the **pin head-set** via one query (`SELECT ... FROM entity WHERE uri IN (pin P0 âˆª P1)` â€” after GC this is ~20-30k rows, sub-second) + video_assoc; replay; clear `_coldNotFullyResident`; fire one bulk `Store.Changes` signal. InMemoryStore upserts are lock-guarded and documented cross-thread-safe (Services.cs:22-24). | no | new `CachedStore.WarmAsync()` |
| 7 | Go-live: `LiveSessionHost.StartAsync` (already backgrounded, WaveeApp.cs:81-96) â€” `ExtensionEtagCache` seeds with `LIMIT` (Â§F Phase 0); silent resume, dealer, sync | no | LiveSessionHost.cs:145 |
| 8 | "Eventually": GC pass, extension sweep, `incremental_vacuum` slices, touch-flush timer | no | writer thread (SqliteColdStore.cs:494) |

**Readiness signals:**
- `CachedStore.WarmComplete` (a `Task` or signal). While pending: `ColdFallback` serves all misses (gate at CachedStore.cs:124 now checks `_hot.HasEvictedEntities || _coldNotFullyResident` â€” one-line change, later just `true`); `InMemoryStore.QueryTracks` (offline search fallback, StoreLibrarySource.cs:289) awaits `WarmComplete` before answering â€” the only surface that genuinely needs the full pin set resident.
- Go-live sync does not need to wait (all its reads go through ColdFallback), but gating `InitialHydrate` on `WarmComplete` avoids redundant refetches â€” do gate it.

**Budgets (VS Code / Electron pattern):** window visible < 1 s; restored view showing cached data < 1.5 s; warm task lands in one publish (single bulk signal, no per-row churn). Emit per-phase `Stopwatch` marks to wavee.log (Â§G).

---

## C. Eviction / GC design

### C.1 Model

Two-stage victim selection (Firefox cache2): **TTL is the filter, LRU is the ranker.** No W-TinyLFU/SIEVE machinery â€” a periodic SQL pass is the whole engine (Â§H).

### C.2 Pin enumeration

The query in Â§A.3, materialized into a temp table at GC start (short read txn). In-memory pins (`BuildPinSet`: now-playing + queue + context + detail cache) passed as an exempt list. New rows get a grace period: `updated_at > now - 15 min` is never a victim (Firefox bug 913808 lesson).

### C.3 TTL tiers by kind

| Kind | TTL | Rationale |
|---|---|---|
| Track / Album / Artist core (unpinned) | 30 d since `last_access` | Spotify's own SPTPersistentCache default; near-immutable catalog |
| `artist_overview` | 7 d since `fetched_at` | page-open re-derives via ArtistV4 SWR/etag cheaply |
| `localized_extension_cache` | `expires_at` + 7 d grace | grace keeps ETag 304-revalidation working |
| `video_assoc` (unpinned track) | 30 d | rides entity GC |
| Queue/radio/autoplay/browse hydration | **never written to disk** (Phase 1 gate) | memory-only by policy |
| Pinned anything | no TTL | still SWR-refreshed when online |

### C.4 Byte-budget LRU

- `cache_budget_bytes` meta row, **default 64 MiB**, user-settable (Â§G).
- `cache_bytes` running counter maintained in the same transaction as every cache-tier insert/delete (O(1) check; reconciled against `SUM(size)` at each GC).
- Trigger: `cache_bytes > budget` â†’ delete `ORDER BY last_access ASC LIMIT 1000` per transaction until `â‰¤ 0.9 Ã— budget` (Chromium watermark pattern), expired rows first, pins and grace-period rows excluded.

### C.5 Last-access without write-per-read

In-proc `HashSet<string>` of touched URIs (appended in `ColdFallback` and hot hits); flushed as one `UPDATE entity SET last_access=$day WHERE uri IN (...)` on the existing writer thread every 60 s / on idle-park; **day granularity** â€” skip the add if the hot-side cached `last_access` is already today. Read path stays read-only; flush rides the single-writer WAL queue.

### C.6 Scheduling

- Startup + 30 s (after warm completes), on the writer thread: expired sweep â†’ orphan/TTL GC â†’ budget LRU. Batches of â‰¤ 1000 rows per DELETE transaction (WAL: readers never block).
- Then every 6 h of session, and on "Clear cache" (Â§G).
- Never on the UI thread, never before first paint.

### C.7 Vacuum strategy

- Migration v6 (one-time, background, gated on a `vacuum_pending` meta flag): `PRAGMA auto_vacuum=INCREMENTAL;` + one full `VACUUM` (required to activate the mode and to reclaim the ~100+ MB freed by the big GC). Runs minutes after launch, not in `Migrate()`.
- Steady state: `PRAGMA incremental_vacuum(200)` slices on idle after each GC batch + `wal_checkpoint(TRUNCATE)` on idle. **No routine full VACUUM** (Spotify SSD-wear incident); manual full VACUUM only behind the escape hatch. Rare defrag VACUUM (quarterly, only if `freelist_count/page_count > 0.25`).
- `mmap_size`: leave 0 for now (Windows truncation vs VACUUM interaction); revisit post-Phase-3.

---

## D. Data-model changes

### D.1 Thin-row split per kind (persisted shape; hot tier keeps full records)

| Kind | Persisted core | Moved out / dropped at persist |
|---|---|---|
| Track | as-is (already thin, Models.cs:194-213) | â€” |
| Album | Id/Uri/Name/Cover/ArtistRefs/Year/TrackCount/Kind/Label/Copyright/Palette | Tracks (already), **MoreByArtist, ArtistsDetailed, OtherVersions â†’ dropped** (rebuilt from standalone rows / refetch) |
| Artist | Id/Uri/Name/Image/HeaderImage/Palette/Verified/MonthlyListeners/Followers (~1 KB) | TopAlbums/AppearsOn/PopularReleases/LatestRelease/Pinned/TopTracks/Bio/Extras â†’ `artist_overview` as **(uri,kind,name,year,cover_url) refs** + bio/extras/stats; AppearsOn capped at 100 stubs at projection (ExtendedMetadataSource.cs:370-375; UI hydrates only 20) |
| Playlist | as today (Tracks stripped) | â€” |
| Show/Episode | as today | â€” |

**SWR-gate preservation (the two real hazards from research):** `StoreEntityMerge.Artist` (Store.cs:160-189) keeps its FetchedAt/thin-write-must-not-clobber rules but operates on the core; `MergeAlbumCards` and the stats gate keyed on `TopTracks` presence (ExtendedMetadataSource.cs:378) move to the overview merge with the same semantics. `ArtistPage` reads become core + async overview; null facets already hide.

### D.2 Encoding decision

**Chosen: keep System.Text.Json source-gen (`EntityJson`), add `DefaultIgnoreCondition = WhenWritingDefault`, wrap payloads in ZstdSharp level 3 with a 1-byte format prefix (0=raw JSON, 1=zstd, 2=zstd+trained dictionary).**

Justification:
- **AOT:** STJ source-gen is the fully supported TrimMode=full path and is already in place (CachedStore.cs:260-268); ZstdSharp.Port 0.8.6 is pure managed, AOT-safe, **already referenced** (Wavee.csproj:93). Zero new dependencies, zero codegen risk. (MessagePack/protobuf rejected â€” Â§H.)
- **Size:** WhenWritingDefault shaves ~25-40 % off stub-heavy blobs; zstd gives 4-6Ã— on repetitive JSON (shared keys, scdn URL prefixes); a trained ~100 KB shared dictionary (fmt=2, stored in `meta`, ideal workload: 100k near-identical small JSON rows) pushes toward 8-10Ã— â€” added lazily, old rows stay readable via the prefix byte, recompressed on next write.
- **Speed:** zstd decompress of a 1 KB row is single-digit Âµs; combined with the point-read model, per-row materialization stays well under 1 ms.
- Thin scalar **columns** (v8) carry list rendering, so payload decode frequency drops to detail-page opens only; the covering index keeps scrolling off the payload pages entirely.

### D.3 Size math

Measured averages: localized track/album rows â‰ˆ 860 B raw JSON; artist rows avg ~36 KB (max 370 KB, ~700 rows â‰ˆ 25 MB); extension rows â‰ˆ 880 B.

**Today's library (216 liked, ~20.7 k membership URIs), after full redesign:**

```
Pinned tracks:  ~21,000 rows
Pinned albums+artists (closure, est. ~0.35 albums + ~0.25 artists per track distinct):
                ~7,000 + ~3,500 rows
Entity rows:    ~31,500 Ã— (860 B â†’ WhenWritingDefault ~600 B â†’ zstd ~200 B payload
                + ~120 B scalar columns + row overhead)     â‰ˆ 31,500 Ã— ~380 B â‰ˆ 12.0 MB
Artist cores:   ~700 Ã— 1 KB                                  â‰ˆ 0.7 MB
artist_overview (pinned artists only, refs-not-cards ~10-20 KBâ†’zstd ~4 KB):
                ~700 Ã— 4 KB                                  â‰ˆ 2.8 MB
Extension cache (capped 4096 Ã— 880 B)                        â‰ˆ 3.6 MB
Identity tables + indexes                                    â‰ˆ 6-8 MB
TOTAL                                                        â‰ˆ 25-28 MB   (vs 168 MB)
Startup: warm-load ~31.5k thin rows â‰ˆ 12 MB read + ~31.5k fast deserializes,
         off the paint path; first paint < 1 s; warm lands < 1 s after.
```

**100 k-track library:** 100k tracks + ~35k albums + ~20k artists â‰ˆ 155k rows Ã— ~380 B â‰ˆ **59 MB** + overviews/extension/identity â‰ˆ **~75 MB total**. First paint unchanged (< 1 s â€” nothing scales with library size on the paint path); warm task ~155k thin rows â‰ˆ 2-4 s **in background**; with Phase 3 the warm set shrinks to viewport + saved heads and even that goes away.

**1 M-track library:** 1M + ~350k + ~150k â‰ˆ 1.5M rows Ã— ~350 B â‰ˆ **~525 MB + ~60 MB indexes**. Acceptable for a 1M-track library (identity data, not waste); bulk warm is **abandoned at this scale by design** â€” Phase 3's covering-index scalar projection + point reads mean startup touches only the restored view's rows (a few hundred page reads, milliseconds). The cache-class budget still caps non-library debris at 64 MiB. This is the "survives by construction" claim: no code path is O(total rows) on the paint path, and the only O(library) paths (GC, warm) are background and batched.

---

## E. Migration plan

Each step is an independent schema-version bump in the existing `Migrate()` runner (SqliteColdStore.cs:88-191), shippable alone, and reversible in the stated sense. Offline-critical data (`collection_items`, `playlist_items`, `rootlist`, `outbox`, `video_override`, `playlists.base_rev`) is **never touched by any step**.

1. **v5 â€” kill dual storage.** Gated on `_spotifyLocale is not null` (test opens keep `entities`):
   `INSERT INTO localized_entities(uri,locale,kind,payload,updated_at) SELECT uri,$locale,kind,payload,0 FROM entities ON CONFLICT DO NOTHING; DELETE FROM entities;`
   Semantics exactly preserved: migrated rows get `updated_at=0` = the priority the 3-leg UNION already gave them; existing localized rows win the conflict. Simplify `LoadAllEntities` (:203-215) and `GetEntity` (:236-243) to a plain `WHERE locale=$locale` (+ rare cross-locale fallback probe). Drop dead indexes: `ix_localized_entities_locale`, `ix_localized_entities_updated`, `ix_localized_extension_locale` (keep `ix_localized_extension_expiry`). *Reversible:* superseded rows were shadowed; any lost non-shadowed row is re-fetchable cache. âˆ’42.6 MB (post-vacuum), UNION-rank gone.
2. **v6 â€” one-time GC + vacuum (background, after first paint, writer thread, gated on meta flags):** expired-extension sweep (+7 d grace) â†’ cap extension table to newest 4096 â†’ orphan entity GC: pin query from Â§A.3 (JSON closure via `json_extract`/`json_each` â€” payloads are still raw JSON at this point, so closure is pure SQL), delete unpinned rows with `updated_at < now âˆ’ 30 d`, exempting `BuildPinSet` + outbox. Then `auto_vacuum=INCREMENTAL` + one full VACUUM. *Reversible:* every deleted row is re-fetchable by definition (the codebase already treats cold writes as "non-fatal: re-fetchable", SqliteColdStore.cs:502). â‰ˆ âˆ’90 MB.
3. **v7 â€” cache accounting + recent_surfaces:** add `size`/`last_access` columns (backfill `size=length(payload)`, `last_access=now`), `meta` counters, `recent_surfaces` table. Pure additive; reversible by ignoring the columns.
4. **v8 â€” thin columns + entity rename + artist_overview + entity_refs + compression:** create `entity` with the Â§A.2 shape; copy rows from `localized_entities`, populating scalar columns by one-pass JSON extraction and recompressing payloads (fmt=1); rows that fail extraction copy as fmt=0 (readable forever via the prefix byte). Create `artist_overview`; fat artist payloads split on first post-migration write (old fat rows still deserialize â€” the Artist JSON shape stays readable; rows shrink on next touch). Drop `localized_entities` after copy. *Reversible:* fmt=0 fallback + the split-on-write strategy means no flag-day; a rollback build reads fmt=0 rows and refetches the rest.

Offline-data guarantee: pinned rows are exempt from every delete; the outbox shield keeps in-flight mutation targets; a failed migration step leaves the previous version's tables intact (each step commits atomically under the schema-version bump).

---

## F. Phased rollout

### Phase 0 â€” immediate bugfixes (this week, low-risk)

| Change | Anchor | Impact | Risk / verification |
|---|---|---|---|
| Wrap ctor replay in `using (_hot.BeginBulk())` | CachedStore.cs:38 | kills ~450 no-op O(n log n) backstop scans; likely seconds off launch | one line; EntityResidencyTests + PersistenceCacheTests |
| `LIMIT $n` on `LoadAllExtensions` | SqliteColdStore.cs:260-261; ExtensionEtagCache.cs:64-78 | stops reading ~24.8k rows / ~22 MB at go-live | near-zero; store already returns newest-first |
| v5 dual-table merge + dead-index drops | SqliteColdStore.cs after :189; :203-215; :236-243 | âˆ’42.6 MB; UNION-rank removed (~30-50 % of SQL-side startup) | low; PersistenceCacheTests locale suite extended |
| Expired-extension sweep at open | SqliteColdStore ctor ~:70, uses ix_localized_extension_expiry | bounds 23.6 MB table | very low; expired rows already needs-revalidate |
| No-op elision (payload hash) before `_cold.UpsertEntity` | CachedStore.cs:215-243, mirror :244-252 | kills steady-state rewrite storm from MetadataService.cs:85-108 | low |

**Measure:** startup ms (log mark), DB MB, `dotnet run Wavee.Tests`, slice green.

### Phase 1 â€” stop the bleeding (write-path policy)

| Change | Anchor | Impact | Risk / verification |
|---|---|---|---|
| Pin-reachability gate in the six `Upsert*` (Option B) | CachedStore.cs:215-243; pin sources Â§A.3; ordering safe per PlaylistFetcher.cs:174â†’:44, CollectionFetcher.cs:138â†’:142 | zero new orphans; no caller changes | medium: verify collection-delta ordering; new gate tests in Wavee.Tests (`<Compile Include>` needed per project memory) |
| Hot-only routing for DiscographyPrefetcher + LiveContextResolver | DiscographyPrefetcher.cs:20-47 (wired LiveSessionHost.cs:349-358); LiveContextResolver.cs:354 | biggest orphan sources gone; per-login rewrite churn gone | low: offline discography degrades to on-open heal (its own comment :49-51 anticipates this) |
| Skip embedded-track cold writes for non-pinned albums | ExtendedMetadataSource.cs:334 | 1+N â†’ 1 row per browsed album | low; tracks still hit hot tier |
| Thin Artist persist (cap AppearsOn 100 at projection; stub refs at persist) | CachedStore.cs:231-236; ExtendedMetadataSource.cs:370-375 | ~25 MB â†’ ~2-5 MB; no more 370 KB writes per artist open | medium-low: MetadataSourceTests.cs:206-226 round-trip must be updated; Assemble re-fattens from standalone rows |
| v6 one-time GC + vacuum (background) | Â§E step 2 | â‰ˆ âˆ’90 MB; startup rows 127k â†’ ~30k | low-medium; feel-test artist/queue pages cold-refetch |

**Measure:** DB MB before/after GC, orphan ratio (Â§G query), startup ms.

### Phase 2 â€” startup rework

| Change | Anchor | Impact | Risk / verification |
|---|---|---|---|
| Open ColdFallback gate (`_coldNotFullyResident`) | CachedStore.cs:124 | partial hot tier becomes correct | low; ColdFallback path already tested |
| Deferred CachedStore ctor + background `WarmAsync` (pin head-set only, under BeginBulk, one bulk signal) | CachedStore.cs:33-42; Services.cs:296 | window < 1 s; ~85-95 % of the stall removed | medium: `QueryTracks` awaits WarmComplete (StoreLibrarySource.cs:289); gate `InitialHydrate` on WarmComplete; feel-test login + offline-start flows |
| recent_surfaces writes on detail-page nav (v7) | detail navigation in WaveeShell/Detail | recent pages warm-start offline | low |

**Measure:** first-paint ms, warm-complete ms, per-phase marks; slice green; offline-start manual test.

### Phase 3 â€” schema/encoding rework

| Change | Anchor | Impact | Risk / verification |
|---|---|---|---|
| v8 table restructure (thin columns, payload-last, fmt prefix, zstd, entity_refs, artist_overview) | Â§A.2/Â§E step 4; CachedStore.cs:80-132 Replay/ColdFallback branch on fmt | 25-28 MB total today; 100k library ~75 MB; decode off the scroll path | medium: staged behind fmt byte; MetadataSourceTests + new round-trip tests per fmt |
| Covering-index list projection + point-read materialization for library lists | StoreLibrarySource + DetailTracks binding | startup O(viewport) at any scale; warm task shrinks to saved heads | medium; virtualization layer drives on-demand rows |
| GC engine full form (budget LRU, touch batching, incremental_vacuum slices) | Â§C; writer thread SqliteColdStore.cs:494 | bounded forever | low-medium; GC unit tests + soak |
| Optional: trained zstd dictionary (fmt=2), engine `ShowBeforeRoot` polish | meta dict blob; FluentApp.cs:149/191 | further 2Ã— size; instant window frame | low / medium (white-flash check) |

---

## G. Metrics & escape hatches

**Metrics** (logged at startup + on demand via a diag command; all O(1) or one indexed scan):
- `cold_mb` = `page_count Ã— page_size`; `reclaimable_mb` = `freelist_count Ã— page_size`
- rows by kind: `SELECT kind, COUNT(*), SUM(size) FROM entity GROUP BY kind`
- orphan ratio: unpinned-row count / total (pin query vs entity)
- `pin_bytes` vs `cache_bytes` (meta counters)
- startup marks: `boot.sqlite_open_ms`, `boot.identity_load_ms`, `boot.first_paint_ms`, `boot.warm_ms`, `boot.warm_rows`
- GC report per run: expired/orphan/lru rows deleted, bytes freed, duration

**Escape hatches:**
- **Cache budget setting** (`cache_budget_bytes`, default 64 MiB, Settings â†’ Storage slider â€” Spotify precedent).
- **Clear metadata cache:** delete all unpinned cache-tier rows + all `artist_overview` + extension cache; **never touches identity tables**; followed by incremental_vacuum. Guaranteed-safe by the tier split.
- **Vacuum now** (manual full VACUUM, hidden/diag).
- **`FG_NO_ENTITY_GC=1`** env kill-switch disabling the GC pass and the write gate (falls back to persist-everything) for bisection.

---

## H. Rejected alternatives

- **Separate cache DB file (metacache.db, ATTACH):** rejected. Loses single-transaction atomicity across membership+entity writes on the existing single writer thread, doubles connection/WAL/pragma management, and the two real benefits (independent vacuum policy, wipe-by-delete) are matched by auto_vacuum=INCREMENTAL + the table-level "Clear cache" (identity tables are tiny, so vacuuming the shared file is cheap post-GC). Revisit only if cache churn measurably inflates library-table query cost.
- **Full normalization (tracks/albums/artists as 3NF column tables, no payload blob):** rejected. Massive rewrite of the merge/SWR machinery (StoreEntityMerge encodes subtle clobber gates over record shapes), and the hybrid thin-columns+blob gets the same read performance for list surfaces at a fraction of the risk. The blob remains the single-writer merge unit.
- **MessagePack (Nerdbank or MessagePack-CSharp v3):** rejected for now. 20-35 % size win is strictly dominated by zstd-on-JSON (4-6Ã—, zero new deps); adds a new source-gen dependency to a TrimMode=full app for less benefit. protobuf-net: AOT-incompatible, hard no. Google.Protobuf cold schemas: viable end-state but a second schema to maintain; only worth it if .proto entity schemas are wanted for other reasons.
- **Memory-mapped custom binary library format (foobar2000-style):** rejected. Rebuilds durability, migration, partial update, and crash-safety that SQLite already provides; the covering-index projection achieves the same "compact sequential index load" property inside SQLite.
- **W-TinyLFU / ARC / SIEVE eviction machinery:** rejected. For a disk cache swept in periodic batches, stored last-access-day + expired-first ranking is within noise of fancy policies and costs zero frame-path allocations; SIEVE's visited-bit could be adopted later if touch-flush writes ever measure hot.
- **WITHOUT ROWID entity table:** rejected â€” ~1 KB rows are 5-25Ã— over the ~200 B/4 KiB-page threshold; kept only for tiny composite-key side tables (`entity_refs`).
- **Engine `ShowBeforeRoot` as the startup fix:** rejected as the mechanism (kept as optional polish) â€” the app-only deferred-ctor path achieves sub-second paint with no engine contract change and no white-flash risk.
- **Filter-only bulk load (load referenced URIs, keep the eager contract):** rejected as the end state â€” still O(library) before paint, fails the 1M constraint; used transiently as the Phase 2 warm set until Phase 3's viewport-driven reads land.
- **Pin flag column maintained on write:** rejected in favor of derived pins at GC time â€” a persisted boolean drifts (un-like, playlist deletion, account switch) and would need its own reconciliation job anyway; the derivation query is cheap at GC frequency.