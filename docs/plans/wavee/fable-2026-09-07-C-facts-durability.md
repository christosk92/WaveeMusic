# Facts durability — why a chart row's play count is "sometimes" there (2026-09-07, lane C)

Branch `feat/library-v3-1` (uncommitted hydration-facade refactor). Read-only investigation; this file is the
implementation spec. Line anchors are against the tree as of 2026-09-07 08:45. Another lane is rewriting the
cold-read path in `CatalogRepository.cs` / `SqliteCatalogPersistence.cs` (batched reads outside the queue,
`PreloadAsync`); every patch below is written against the CURRENT method names and §C.6 lists what that lane's
rewrite changes.

Symptom under investigation: an artist page's Top tracks show play counts for one artist (Coldplay, every visible
row) and for another only on row 1 — a liked track — with the other eight rows empty; "the entire point of the
refactor was to eliminate this error".

---

## 0. The answer in two paragraphs

**Mechanism.** The Top-tracks chart never *demands* `PlayCount`. The artist page's lease is
`new QueryDemand(…, [tracks/popular/appears windows], [])` — `OptionalFacets` is empty (`Features/Detail/ArtistPage.cs:86-88,
98-103`) — so `ArtistDefinition.Requirements` (`Backend/Queries/CatalogQueryDefinitions.cs:246-264` → `RowKeys` `:43-73`)
names a chart row's `TrackIdentity`, `AlbumIdentity` and `ArtistIdentity` keys and nothing else. The only writer of a
chart row's `PlayCount` is the **ArtistOverview** envelope's inline child seeds (`SpotifyLive/Catalog/SpotifyCatalogEnvelopes.cs:146-148`
→ `CatalogDomainSeeds.Track` `:31`, ten tracks, count > 0 only) applied by `CatalogRepository.AcceptAsync` `:339-351`
as `Provenance = InlineSeed`. That seed **is** persisted (`records[seed.Key] = ToRecord(seeded)` `:350`,
`WriteCatalogRecordLocked` `SqliteCatalogPersistence.cs:343-394`) — but it is never read back for the artist page:
`CatalogReadView.Fact` reads with `cold: false` (`Backend/Queries/CatalogReadView.cs:43-44`) and `CatalogReadView.Track`
re-registers every joined key with `allowColdRead: false` (`:60`), so a joined fact becomes a cold candidate only when it
is a *requirement* (the demand path, `QueryService.RunDemandAsync` → `ResourceCoordinator.EnsureAsync` →
`CatalogRepository.ReadManyAsync` → `PreloadAsync`). `ArtistOverview` itself is Provider-fresh for **12 hours**
(`ResourcePolicy.FreshFor` `:21`), so a second process opening the same artist inside that window never re-fetches
the overview (log: sessions `0164a7af` and `4d6f60ee` open artists `3eVa…`/`4lxf…` with `ArtistDetailQuery keys=7 … ready=7`
and no `envelope:ArtistOverview` batch at all) and therefore never re-seeds the counts into memory. The counts sit in
`catalog_resource` as `provenance=1` rows nobody ever asks for. **Row 1 has a count because it is liked**: the Liked
Songs page's Plays column demands `PlayCount` as an optional facet (`Features/Detail/DetailTracks.cs:326` →
`LikedDefinition.Requirements` → `RowKeys` `:55-56`), which the provider answered from
`OnPlatformReputationTrait` (log `2669`: `group=extended-metadata keys=72 subjects=36 … facet=PlayCount … ready=72`,
`2670`: `spec=LikedSongsQuery … PlayCount=36`) — a `Provider` value that IS in memory. Coldplay shows counts on every
row because its overview was fetched in *this* process (`envelope:ArtistOverview` batches at `2472/2482`, `2617/2629`,
…) — and even then only the overview's ten rows (`OverviewSeedCap = 10`, exactly the 2 × 5 rows the chart shows before
paging); the chart endpoint (`FetchChartAsync` `:179-197`) returns URIs only, `seeds: []`, so rows 11–50 have no count
from any source. Two further, independent ways a seeded count vanishes inside one process are §A.4 (resident trim
evicts `InlineSeed` entries FIRST because their `FetchedAt` is `default`, and a live node's `_coldAsked` set is never
cleared so it never re-reads) and §A.5 (SQLite LRU sweep over budget, where liked tracks are pinned and the others are
not — the same "row 1 survives" shape).

**Contract.** (R1) A page that renders a fact demands its facet: the artist lease declares
`OptionalFacets = [FacetKind.PlayCount]` for the `popular` window, so a chart row's identity and count are named in
the same `Requirements`, captured in the same `EnsureAsync`, fetched in the same `extended-metadata` batch (TrackV4 +
OnPlatformReputationTrait for the same 50 subjects), accepted in ONE `AcceptAsync` and published as ONE
`CatalogChangeSet` — no partial rows, counts for all 50 rows, pinned against the resident trim, cold-loaded after a
restart, refreshed on the facet's own TTL. (R2) Every fact a definition *joins* is a cold candidate (`Fact`/`Track` read
with `allowColdRead: true`): a persisted answer of any provenance is read back for the page that renders it. (R3) Three
provenance classes: **Provider** (the facet's own fetch; fresh for `FreshFor`), **Provider-by-document** (a seed decoded
from the *same* extended-metadata document the facet's own recipe would fetch — `Availability`←TrackV4,
`EpisodeDetail`←EpisodeV4, `AlbumIdentity`←AlbumV4/AlbumTracks, `ArtistIdentity`←ArtistV4 relations,
`ShowIdentity`←ShowV4/ShowEpisodes — stamped `Provider`, FULL replacement, fresh for the child facet's TTL from the
document's `FetchedAt`, never re-requested unless the parent revalidates), and **InlineSeed** (cross-document, first-paint
filler: fill-unknown-only, never fresh, persisted with the *parent's* `FetchedAt`, blocked by a provider `Absent` only
for that answer's negative-cache window). (R4) An eviction is a distinct publication (`CatalogChangeKind.Evicted`) that
every node — parked or active — uses to forget its `_coldAsked` entries, so an evicted-but-persisted fact is read
again. (R5) A seeded relation page (`Total == null`) never names a next page; the chart is the provider's answer.

---

## A. Lifecycle of a seeded fact (InlineSeed) — accept to the next process

### A.1 Where a chart row's play count comes from today (verified)

| Source | File:line | What it writes | Provenance | Rows covered |
|---|---|---|---|---|
| `queryArtistOverview` envelope | `SpotifyCatalogEnvelopes.cs:135-160` → `CatalogDomainSeeds.Track` `:12-36` | `TrackIdentity` (title, artists, album uri, duration, explicit, image), `ArtistIdentity` refs, `AlbumIdentity` (name-less: `MapArtistTrack` builds a name-less `AlbumRef`, `SpotifyExportMapper.cs:1804-1834`), `Availability` (never — `MapArtistTrack` sets none), **`PlayCount` only when `track.PlayCount > 0`** (`:31`), a Partial `ArtistPopular` page under `Arguments(0, 50)` (`:152-155`) | `InlineSeed` | the overview's 10 (`ArtistPopularTracks.OverviewSeedCap`) |
| `artist-top-tracks-extensions` chart | `FetchChartAsync` `:179-197` | one `ArtistPopular` relation page: URIs only, `seeds: []` | `Provider` (page) | up to 50 (`ExtendedCap`), NO facts |
| `OnPlatformReputationTrait` (XM) | `SpotifyCatalogDecoder.ExtensionFor(PlayCount)` `:34`, decode `:289-304` (tag 24, a present zero is a real answer) | `PlayCount` | `Provider`, `FreshFor` 6 h (`ResourcePolicy.cs:22`) | whatever some page **demanded** |
| Native/local provider | `NativeCatalogResourceProvider.cs:78-90` | `PlayCountValue(track.PlayCount)` | `Provider` | local tracks only |

Timing in every session today: the chart lands BEFORE the overview (`envelope:ArtistPopular fetchMs=58…316` vs
`envelope:ArtistOverview fetchMs=186…635 + acceptMs 8…249`, e.g. sid `2fd4d2e0` lines `2472` → `2482`). They are two
batch groups (`SpotifyCatalogResourceProvider.BatchGroup` `:48-50` → `envelope:ArtistPopular` / `envelope:ArtistOverview`),
so two `FetchBatchAsync` → two `AcceptAsync` → two publications: rows paint from the chart (titles from the follow-up
TrackV4 batch), counts pop in ~300 ms later for ten of them. That is the "partial row" the contract in §C removes.

### A.2 Accept → reduce → persist (the seed IS durable)

`CatalogRepository.AcceptAsync` (`Backend/Catalog/CatalogRepository.cs:294-361`): for a `Present`/`NotModified` parent with
`Error is null`, each child seed is `LoadCoreAsync`'d, reduced by `ReduceSeed` (`:443-455`) and, when it changed the entry,
written into `records[seed.Key] = ToRecord(seeded)` (`:350`). `ReduceSeed` for a `ReplaceFacetPatch` (`PlayCount`,
`Availability`, `Descriptors`, `AudioAttributes`, `Publishing`, relation pages): `HasReadinessField` → `_ => true`
(`:460-469`), `patch.Apply(old.Value, fillUnknownOnly: true)` → `CatalogInlineSeeds.Merge` → `_ => current ?? seed`
(`Wavee.Core/Catalog/CatalogInlineSeeds.cs:24`) — a seed never overwrites an existing value of any provenance; on an
Unknown entry it promotes to `Present` with `Provenance = InlineSeed` (`:453-454`). What it does NOT do: touch
`FetchedAt`/`ExpiresAt` — a promoted seed keeps `ResourceSnapshot.Unknown`'s `FetchedAt = default` (0001-01-01) and
`ExpiresAt = default`. `Commit` (`:588-598`) stamps the revision; `SqliteColdStore.CommitAsync` →
`WriteCatalogCommitLocked` → `WriteCatalogRecordLocked` (`SqliteCatalogPersistence.cs:343-394`) writes
`state=1, provenance=1, fetched_at=-62135596800000, expires_at=-62135596800000, updated_at=now, last_access=now`.
`ReadCatalogPass` (`:142-244`) reads it back as `CatalogRecord(…, InlineSeed, FetchedAt = MinValue, …)` and
`Install` (`:577-586`) puts it into an Unknown entry. **Verdict: persisted, readable, durable across processes.**

### A.3 The cold path never asks for it (the primary mechanism)

`QueryReadContext.Read(key, allowColdRead)` adds a key to `ColdCandidates` only when `allowColdRead` is true
(`Backend/Queries/QueryDefinition.cs:59-66`). `QueryService.Node.Recompute` turns cold candidates that are still `Unknown`
into `LoadColdAsync` batches (`QueryService.cs:325-336, 364`) — exactly once per node (`_coldAsked.Add(key)`, `:327`;
the set is never cleared, `:208`). Everything else is loaded by the DEMAND path: `RunDemandAsync` `:542-590` →
`ResourceCoordinator.EnsureAsync` `:58-166` → `_repository.ReadManyAsync(distinct)` `:72` → `PreloadAsync` `:520-556`.

`CatalogReadView` (`Backend/Queries/CatalogReadView.cs`) passes `cold: false` from `Fact<T>` (`:43-44`) unless a caller
opts in (`Playlist` header with rows `:109`, `ArtistOverview` `:174`); `Track()` computes the join in an isolated scope and
re-registers every key with `read.Read(key, false)` (`:58-61`; memo path `:51-57` likewise). So for the artist page:

- `ArtistDefinition.Read` (`CatalogQueryDefinitions.cs:229-245`) cold-candidates: the artist's `ArtistIdentity` (`:231`),
  the five relation page keys (`RelationProjection.Read` → `read.Read<RelationPageValue>(key)` `:58`), `ArtistOverview`
  (`:174`, `cold: true`). Chart rows: **none**.
- Requirements (`:257-264`): identity keys for rows (`RowKeys` with `IdentityLookahead 300`), album/artist identities,
  optional facets **from `demand.OptionalFacets` = `[]`** → no `PlayCount` key is ever a requirement.
- Therefore after a restart: `AcceptAsync`'s seeds are on disk; `Peek(PlayCount key)` → `ResourceSnapshot.Unknown`
  (`:101-102`, entry absent); `TrackCore` `:78,84` → `count?.Count ?? 0` → `hasPlays = t.PlayCount > 0` false
  (`ArtistPopular.cs:353`) → no "plays" text. Rows whose `PlayCount` some *other* live node demanded (`LikedDefinition`,
  `AlbumDefinition` with the Plays column — log waves `AlbumDetailQuery … PlayCount=31`, `PlaylistDetailQuery … PlayCount=50`,
  `LikedSongsQuery … PlayCount=36`) are `Present/Provider` in memory and DO render.

Log proof (today, `%LOCALAPPDATA%\Wavee\logs\wavee-20260907.log`):

```
sid=0164a7af  ArtistDetailQuery keys=7 … ready=7  facets=[ArtistDiscography=3,ArtistIdentity=1,ArtistOverview=1,ArtistPopular=1,ArtistAppearsOn=1]   ← all cached, no fetch
              (no "group=envelope:ArtistOverview" batch anywhere in this session)
              hydration.settled surface=ArtistPopular uri=spotify:artist:3eVa… rows=12 sinceNavMs=315                   ← titles fine (demanded)
sid=4d6f60ee  same shape for 3eVa… and 4lxf…                                                                              ← counts absent (nothing demands them)
sid=2fd4d2e0  envelope:ArtistPopular keys=1 … first=…7AaGbSgUxJFuZ49VvclNH6 (2472) → envelope:ArtistOverview (2482)        ← overview fetched: counts for 10 rows
sid=2fd4d2e0  fetch batch group=extended-metadata keys=72 subjects=36 facet=PlayCount ready=72  (2669)                     ← the LIKED page's demand
              demand wave spec=LikedSongsQuery keys=219 … facets=[TrackIdentity=63,AlbumIdentity=55,PlayCount=36,AudioAttributes=36,ArtistIdentity=29] (2670)
```

`hydration.gaps` counts titles only (`App/HydrationGaps.cs:19-27`), so this class of gap is invisible to the always-on
log today — §F adds the field.

### A.4 In-memory eviction: seeds are the FIRST victims and a live node never re-reads

`QueryService.TrimInactiveAsync` (`:106-120`) runs on every catalog change, release, `SetDemand` and finite read
(`:136,156,712,821`) and calls `CatalogRepository.TrimUnpinned(GetActiveResourceKeys, ResidentTarget = 8000)` once
`ResidentCount > ResidentHighWater = 12000`. `MemoryGovernor` registers a second arm at priority 3
(`App/Services.cs:409-410`, cap `EntityResidencyCap = 4000`, `:260`) that `WaveeApp.cs:253-262` fires at
`MemoryPressure.Moderate` (≥ 85 % of the high-memory-load threshold) every 30 s.

`TrimUnpinned` (`CatalogRepository.cs:55-76`): pins = every active node's `Requirements(...).Catalog`
(`GetActiveResourceKeys` `:99-104` — parked leases contribute nothing, `MergeDemand` `:452-460`); victims = unpinned,
`Idle/Offline`, `Loaded` entries **ordered by `Snapshot.FetchedAt` ascending** (`:67`). Every `InlineSeed` entry has
`FetchedAt = default` (§A.2) — so seeds and Unknown placeholders are evicted before any Provider entry, whatever their
age. The eviction publishes `CatalogChangeKind.Activity` (`:73`); a node depending on the key recomputes
(`OnCatalogChange` `:122-137` → `RecomputeCatalog` → `Recompute(false)` → `resourceValuesChanged` true → re-project),
reads `Peek` → Unknown, and `_coldAsked.Add(key)` returns false (`:327`) — **no `LoadColdAsync`, ever again for this
node**. The demand path reloads only requirements; `PlayCount` is not one. A parked node (KeepAlive) skips the
notification entirely (`HasActiveLease` false, `:134`) and hits the same dead `_coldAsked` on reactivation.

Is 12 000 reached? `EntryOf` creates an entry for every key ever preloaded (`PreloadAsync` `:534`), captured, or seeded.
Today's two big sessions demanded 28 467 (`ec100c65`) and 28 828 (`a6c0b733`) keys through `catalog.demand.wave`
(re-asks included); one artist open alone demands `AlbumIdentity=251` (`1813`), and every ArtistV4 document seeds an
`AlbumIdentity` per discography group plus every `ArtistRef`. Yes — within minutes of browsing. There is no trim log
line to prove the instant; §C adds one.

Row-1-survives shape: the liked track's `PlayCount` is a `Provider` entry with a real `FetchedAt` — evicted only after
every seed and every older Provider entry; the eight seeded rows go first.

### A.5 SQLite retention (durable rows)

`SqliteColdStore.RunCatalogGcBatch` (`Backend/Persistence/SqliteCatalogMaintenance.cs:88-167`), every 5 min after a 30 s
delay (`CatalogCacheMaintenance.RunAsync` `:65-89`): candidates are rows NOT in `temp.catalog_pins` with
`updated_at < now − 15 min` AND (`$over` = catalog_bytes > budget, default `64 MiB` `SqliteColdStore.cs:15`, floor 32 MiB
`Services.cs:264`, OR `last_access` older than 30 d / 14 d relations / 7 d overviews), LRU by `last_access` (write time;
`ReadCatalogPass` is on the read-only connection and never bumps it; `BuildCatalogPins` `:84-85` refreshes it only for
pinned subjects). Pins (`:54-86`): active demand, rootlist playlists AND their members (finding #7 leg restored),
**`replica_collection_item` (liked tracks / saved albums / followed artists)**, outbox, `recent_surfaces` (the last 50
`album:/playlist:/show:/artist:` routes, `WaveeShell.cs:1887-1896`, `RecentSurfaceRoute.TryClassify` `:26-31`), plus ONE
relation hop per root (`RelationHopPerRootLimit = 1000`, so a recently opened artist pins its chart's 50 tracks).
`ForgetEvicted` (`CatalogRepository.cs:78-86`) drops the evicted keys from memory and publishes `Durable`.

Verdict: not today's mechanism (the DB was reset to v12 on 09-06 18:18 and is unlikely to be over 64 MiB; nothing is
30 days old), but it is the SAME "liked survives, others vanish" shape the moment the budget is exceeded: an
artist not among the last 50 surfaces loses its seeded counts (and its overview, so a re-open re-seeds them — unless
the overview row survives by LRU luck while the track rows do not; both are written in one commit with equal
`last_access`, ordered `subject, facet`, so `spotify:artist:*` rows go before `spotify:track:*` rows and the pair
normally leaves together). With R1 the count is a demanded, refetchable fact and the sweep is harmless.

### A.6 NotModified / transport cache — does a cached overview re-apply seeds?

- **Extended-metadata (XM) path**: `FetchRawAsync` (`SpotifyCatalogResourceProvider.cs:171-205`) sends the cached
  `etag`; a 304 with matching cached bytes is rewritten to `Status = 200, Payload = cached bytes` (`:192-197`) and decoded
  again by `CreateDocumentDecoder` — child seeds are re-derived and re-applied on every revalidation. The repository's
  `NotModified` branch (`Reduce` `:486-491`) is unreachable from the Spotify provider (no producer in `Wavee/` outside
  the repository); `AcceptAsync` `:339-340` would apply seeds for it anyway.
- **Pathfinder envelopes (ArtistOverview, album document, search, home)**: no conditional request, no transport
  record; re-fetched only when `IsFresh` fails — 12 h for `ArtistOverview`. Between those fetches the overview's seeds
  are re-applied by NOTHING. This is the window in which the counts depend entirely on persistence + cold reads (§A.3).

### A.7 What a later provider answer does to a seeded count

`Reduce` (`CatalogRepository.cs:472-501`): `Present` → value replaced, `Provider`, `FetchedAt = now`, 6 h; `Absent` →
`Knowledge.Absent, Value = null`, negative-cached `FreshFor(absent: true)` = 24 h by default (`ResourcePolicy.cs:10`) —
the row shows no count, and `ReduceSeed` `:445` refuses every later seed while the entry is `Absent` **for ever** (it
does not consult `ExpiresAt`); `Unsupported` keeps the value (`:497`) and `EnsureAsync` treats it as terminal
(`ResourceCoordinator.cs:110`). The Spotify provider answers `PlayCount` on the XM path only (`ExtensionFor` non-null →
never the `Unsupported` fallthrough at `FetchEnvelopeAsync:176`); in today's log every `PlayCount` batch is
`absent=0 failed=0`. The extension is really served: `ExtensionFor(PlayCount) = OnPlatformReputationTrait` (`:34`),
batch group `extended-metadata` (`:48`), `FetchRawAsync` asks `(subject, OnPlatformReputationTrait)`, decoder `:88,289-304`;
log `1197` (single key, `ready=1`) and `2669` (36 subjects, `ready=72` = 36 counts + 36 audio attributes).

### A.8 "Row 1 has a count because it is liked" — reproduced from code

1. `DetailTracks.PublishViewportDemand` (`Features/Detail/DetailTracks.cs:305-345`): `if (columns.Plays) optional.Add(FacetKind.PlayCount)`
   (`:326`) — the Liked Songs page (and every playlist/album page with the Plays lane, `DetailTrackTableRules` step 0)
   publishes `new QueryDemand(true, Visible, windows, [PlayCount, AudioAttributes…])` (`:345`).
2. `LikedDefinition.Requirements` (`CatalogQueryDefinitions.cs:364-365`) → `RowKeys(value, demand)` → for every index in the
   visible windows `yield return View.Key(tracks[index].Uri, facet)` (`:55-56`).
3. `RunDemandAsync` → `EnsureAsync` → not fresh → captured → `TakeBatchLocked` groups by `extended-metadata` → one POST
   carrying `TrackV4`, `OnPlatformReputationTrait`, `AudioAttributesV2` for 36 subjects → `AcceptAsync` → `Provider`,
   persisted, in memory (log `2669-2670`).
4. The artist page's `View.Track(read, uri)` (`ArtistDefinition.Read:236`) → `Fact<PlayCountValue>` → `Peek` → Present →
   `PlayCount` rendered (`ArtistPopular.cs:353,367-374`). The eight unliked rows: nothing demanded their counts in this
   process, the overview is cached → §A.3.

### A.9 "Sometimes" — the exact decision table

| Situation when the artist page opens | Overview fetched in this process? | Chart rows with a count |
|---|---|---|
| First open of the artist since the last 12 h (or after a schema reset) | yes | the overview's 10 (rows with `playcount > 0`); rows 11–50 never |
| Re-open in the same process, no trim in between | no (fresh) | still the 10 (in memory) |
| Re-open in the same process after `TrimUnpinned` / governor trim, same node (KeepAlive-parked or shared) | no | only rows some *demanding* page fetched (`Provider` entries survive the seed-first ordering); §A.4 |
| Re-open in the same process after a trim, NEW node | no | the 10 come back (fresh `_coldAsked`, demand preloads identities; seeds cold-read? **no** — still not candidates; they come back only if the node's identity preload happens to… it does not: only the demanded identity keys are preloaded) → **only demanded rows** |
| New process inside the overview's 12 h | no | **only rows another page demanded** — the user's "row 1, liked" case |
| New process after 12 h | yes | the 10 again |
| Any of the above and the DB over budget | — | pinned subjects only (liked / recent surfaces + one hop) |

---

## B. Every other seeded / optional fact the UI shows — the same audit

Legend — **Seeded by**: who writes it without a fetch of its own; **Demanded**: which definition names its key
(pinned, preloaded, fetched); **Cold after restart**: read back from persistence for that page (today: only if
demanded, §A.3); **Durable**: survives trim/sweep as a re-loadable fact; **Verdict** under today's code.

| Fact (facet) | Rendered | Seeded by (provenance) | Demanded by | Cold after restart | Can flicker / vanish today | Verdict / fix |
|---|---|---|---|---|---|---|
| Track title, duration, explicit, image (`TrackIdentity`) | every track row | overview/search/home/suggestions (`CatalogDomainSeeds.Track`), AlbumV4 disc tracks (`SpotifyCatalogDecoder.AlbumTracks:194-202`), `SeedManyAsync` (queue) — all `InlineSeed` | `RowKeys` identity + 300 lookahead in every list definition | yes (demanded) | no: a Provider `ReplaceFacetPatch` replaces field-for-field; unspecified fields keep the seed (`TrackIdentityPatch.Choose`). Nameless seeds no longer promote (D.4) | OK |
| Artist names on rows / album header (`ArtistIdentity`) | rows, headers, cards | `ArtistRefs` in TrackV4/AlbumV4/ArtistV4 (`:261-272`, names carried), `CatalogDomainSeeds.ArtistRef` | `RowKeys` (`:53`), `AlbumKeys`, `AlbumDefinition:198`, `LibraryDefinition:603-605` | yes | the bare "," was the nameless-ref promotion — gated now (D.4). Names arrive in the SAME accept as the row identity (TrackV4 carries them) | OK |
| Album name/cover/year on rows (`AlbumIdentity`) | rows, cards | TrackV4 (`Track:108-115`), ArtistV4 discography (`ArtistAlbums:224-226`), home/search cards | `RowKeys` (`:52`), `AlbumKeys` | yes | ArtistV4 album seeds are card-complete but not row-complete (no `ArtistUris`); `AlbumKeys` demands the identity → AlbumV4 per album (log `AlbumIdentity=251` for one artist). Under R3 the ArtistV4 seed stays `InlineSeed` (different document) — the AlbumV4 re-fetch is what supplies artists/track count, so it is not waste | OK |
| Availability (`Availability`) | greyed rows (`DetailTracks`), expanded facts, sidebar planner | TrackV4 — the SAME document (`Track:116-118`), Pathfinder `getTrack` fallback | only `GlobalFacets` when the PlayableOnly filter is on (`DetailTracks:344`) | **no** (not demanded) | unavailable styling disappears after a restart until the identity's 1 h TTL re-fetch re-seeds it; and today's seed is `InlineSeed`, so a PlayableOnly filter re-POSTs TrackV4 for every row it just fetched | R3 (Provider-by-document) + R2 (cold candidate). Do NOT add it to identity demand: a `FacetKind.Availability` request whose TrackV4 lacks the file plane falls to a per-track Pathfinder `getTrack` (`SpotifyCatalogResourceProvider.cs:143-149`, `SpotifyCatalogEnvelopes.cs:161-173`) — a 300-GET storm on a playlist |
| Play count (`PlayCount`) | detail Plays lane, chart, expanded facts | overview (10 rows) — `InlineSeed` | `DetailTracks` (Plays lane) → playlist/album/liked; **not** the artist page | only for demanding pages | §A | R1: artist lease `OptionalFacets = [PlayCount]`; R2; R4 |
| Audio attributes (`AudioAttributes`) | BPM·Key lane, expanded facts, tempo filter | Native provider only; nothing on Spotify | `DetailTracks` when the lane/filter is on | only when demanded | expanded drawer shows BPM/Key only if the lane was on at some point — a demand gap (drawer is not a window) | follow-up: the expanded row adds `AudioAttributes`+`Descriptors` to the page's `OptionalFacets` while open (out of scope here) |
| Descriptors (`Descriptors`) | expanded facts chips, Tag filter | nothing on Spotify | `GlobalFacets` on Tag filter only | only when demanded | same drawer gap | same follow-up |
| Publishing (`Publishing`) | album facts, liked facts | Pathfinder album document (`CatalogDomainSeeds.Album:47-49`, `InlineSeed`) | `AlbumDefinition:193-194` (always) | yes | seed → XM `PublishingMetadataTrait` replaces; values agree | OK |
| Monthly listeners / verified / bio (`ArtistOverview`) | hero, biography, Home artists module | — (its own fetch) | `ArtistDefinition:257`, `cold: true` (`CatalogReadView:174`) | yes | 12 h TTL; Absent for a NotFound union | OK |
| Episode title/duration/show (`EpisodeIdentity`) | episode rows | home cards, search, EpisodeV4 | `ShowDefinition:333` (visible indices, no lookahead) | yes | rows outside the window are blank until scrolled (no lookahead for episodes — a smaller instance of the playlist finding of 09-07; not in scope) | OK |
| Episode description (`EpisodeDetail`) | `EpisodeList.cs:205` | EpisodeV4 — the SAME document (`Episode:147-149`), `InlineSeed` | **nobody** | **no** | description present in the first process, gone after a restart, back after the identity's 1 h re-fetch | R3 + R2 |
| Playlist owner / collaborators (`UserIdentity`) | header, rail, sidebar subtitle | `CatalogDomainSeeds.Playlist:66-68` (owner name) | `PlaylistDefinition._headerProfiles` (`:155-156,162`) | yes | XM `UserProfile` (`ExtensionFor:33`) with the `user-profile-view` fallback (`FetchUserAsync`) | OK |
| Home / Home-section cards (identity + album-card artist subtitle) | Home | Home document `SeedCard` (`:219-246`, `InlineSeed`; album cards seed NO artists) | `HomeDefinition:532` / `HomeSectionDefinition:503-504` — the card's own identity only | identity yes; **album-card artist names no** (the `ArtistIdentity` keys come from AlbumV4's `ArtistRefs` seeds and are never demanded) | after a restart inside the album identity's 1 h TTL an album card's subtitle is empty; rows re-fill on the next AlbumV4 fetch | R2 makes the persisted `ArtistRef` seeds cold candidates (they were written in the AlbumV4 accept) — no extra demand needed |
| Sidebar rows (`AlbumIdentity`/`ArtistIdentity`/`PlaylistHeader`/`ShowIdentity`) | sidebar | LibrarySync headers, home/search seeds | `LibraryDefinition:568-613` (visible + album artists) | yes | (4.2 scope handover, separate lane) | OK |
| Relation pages (`ArtistPopular` seed) | chart order | overview (`Arguments(0, 50)`, Partial, `Total = null`) | `_popular.Require` | yes (page keys are candidates) | see D.1 — the seed's `Total == null` makes `RelationProjection` name page `(10, 50)` | R5 |

---

## C. The contract

### C.1 Rules

- **R1 — render ⇒ demand.** A page that shows a fact declares the facet in its lease (`OptionalFacets` for per-row
  facts on requested windows; `GlobalFacets` only for a whole-collection sort/filter). The definition's `RowKeys` names
  the facet key for exactly the indices of the demanded windows (no lookahead for optional facets — identity looks 300
  ahead/50 behind, `CatalogQueryDefinitions.cs:79-88`, the extras stay visible-only). The artist chart's `popular`
  window is `(0, ExtendedCap = 50)`, so all 50 rows are named at once.
- **R2 — join ⇒ cold candidate.** Every key a definition joins through `CatalogReadView` is a cold candidate
  (`allowColdRead: true`), so a persisted fact of any provenance is read back once per node. Demand still owns the
  network; the cold read is a batched persistence round trip (`PreloadAsync` skips `Loaded` entries, so the second
  node for the same keys costs nothing).
- **R3 — three provenances.**
  - `Provider`: the facet's own fetch. `IsFresh` for `FreshFor(facet)`.
  - Provider-by-document (`CatalogSeed.Authoritative = true`): a child seed decoded from the SAME extended-metadata
    document `(subject, ExtensionKind)` the child's own recipe would fetch. Reduced like a `Present` answer (full
    replacement), `Provenance = Provider`, `FetchedAt = parent.FetchedAt`, `ExpiresAt = ResourcePolicy.ExpiresAt(value, parent.FetchedAt)`;
    never re-requested while fresh; a newer own answer of the child wins by `FetchedAt`. Sites: `SpotifyCatalogDecoder`
    `Track` (Availability, TrackV4 document only), `Episode` (EpisodeDetail), `AlbumTracks` (AlbumIdentity), `ArtistAlbums`
    (ArtistIdentity), `ShowEpisodes` (ShowIdentity).
  - `InlineSeed` (everything else, including every `CatalogDomainSeeds` seed and every cross-subject `ArtistRef`):
    fill-unknown-only, never fresh, promotes Unknown → Present only with a readiness field (D.4), stamped
    `FetchedAt = parent.FetchedAt` (trim/sweep ordering treats it as its parent's age), `ExpiresAt = default`.
    A provider `Absent` blocks seeds only until the absence's `ExpiresAt`.
- **R4 — eviction is a publication.** `TrimUnpinned` and `ForgetEvicted` publish `CatalogChangeKind.Evicted`; every
  node, parked or not, removes those keys from `_coldAsked` so the next recompute reads them again. Both trims log
  one always-on line.
- **R5 — a seeded relation page names no successor.** `RelationProjection.Read` treats `Total == null` (only the
  overview seed produces it; every provider page carries `Total`) as "nothing further is knowable": `_nextPage = null`,
  `Loaded = true` with the seed's items, `Require` returns page 0 only, which is not fresh and is replaced by the chart.
- **R6 — one wave per row.** Identity and optional facets of a window travel in one `Requirements`, one
  `EnsureAsync`, one `extended-metadata` batch (`TakeBatchLocked` groups by provider batch group and counts
  SUBJECTS against `MaximumBatchUris = 300`, `ResourceCoordinator.cs:246-269`; `FetchRawAsync` dedupes to distinct
  `(subject, kind)` pairs, `SpotifyCatalogResourceProvider.cs:66-69`; `MetadataChunking.ExtensionRanges` may split the
  POST bodies but `FetchAsync` returns them together), one `AcceptAsync`, one `CatalogChangeSet`.

### C.2 Code — `Wavee.Core/Catalog/Resources.cs`

```csharp
// :107  — one new kind. Evicted: the repository dropped the resident entry (resident trim) or the durable row
// (cache sweep); a query node forgets its cold-read bookkeeping for the keys so a persisted fact is read again.
public enum CatalogChangeKind : byte { Durable, ColdRead, Activity, Session, Evicted }

// :127  — Authoritative: decoded from the SAME transport document the child's own recipe would fetch (see
// SpotifyCatalogDecoder). Reduced as a provider answer, never as a fill-unknown-only seed.
public sealed record CatalogSeed(ResourceKey Key, CatalogPatch Patch, bool Authoritative = false);
```

`IsFresh` (`:99-101`) is unchanged: authoritative seeds carry `Provider` provenance, inline seeds never satisfy it.

### C.3 Code — `Backend/Catalog/CatalogRepository.cs`

Replace `TrimUnpinned` (`:55-76`) and `ForgetEvicted` (`:78-86`):

```csharp
    public long TrimUnpinned(Func<IReadOnlyCollection<ResourceKey>> pins, int maximum)
    {
        ArgumentNullException.ThrowIfNull(pins);
        long freed = 0;
        int before, after, seeds = 0;
        var removed = new List<ResourceKey>();
        _commits.Publish(() =>
        {
            // Demand changes use the same publication gate. Capture pins here, not before waiting for it.
            var retained = new HashSet<ResourceKey>(pins());
            lock (_gate)
            {
                before = _entries.Count;
                // Oldest answer first. Inline seeds carry their parent's FetchedAt (ReduceSeed), so they are no
                // longer a block of 0001-01-01 entries that goes before every real answer regardless of age.
                foreach (var key in _entries.Where(pair => !retained.Contains(pair.Key)
                    && pair.Value.Snapshot.Activity is ResourceActivity.Idle or ResourceActivity.Offline
                    && pair.Value.Loaded).OrderBy(pair => pair.Value.Snapshot.FetchedAt).Select(pair => pair.Key).ToArray())
                {
                    if (_entries.Count <= maximum) break;
                    var entry = _entries[key];
                    if (entry.Snapshot.Provenance == CatalogProvenance.InlineSeed) seeds++;
                    _entries.Remove(key); _residentBytes -= entry.EstimatedBytes; freed += entry.EstimatedBytes; removed.Add(key);
                }
                after = _entries.Count;
            }
            if (removed.Count > 0) PublishKeys(removed, CatalogChangeKind.Evicted);
        });
        if (removed.Count > 0)
            WaveeLog.Instance.Event(WaveeLogLevel.Info, "catalog", "catalog.resident.trim",
                "resident trim before=" + before + " after=" + after + " evicted=" + removed.Count + " seeds=" + seeds
                + " freedKB=" + (freed / 1024) + " cap=" + maximum,
                fields: [WaveeLogField.Of("before", before), WaveeLogField.Of("after", after), WaveeLogField.Of("evicted", removed.Count),
                    WaveeLogField.Of("seeds", seeds), WaveeLogField.Of("freedKB", freed / 1024), WaveeLogField.Of("cap", maximum)]);
        return freed;
        // `before`/`after` are assigned inside Publish; declare them as `int before = 0, after = 0;` above the lambda
        // so definite assignment holds (the compiler cannot see through the delegate).
    }

    internal void ForgetEvicted(IReadOnlyList<ResourceKey> keys)
    {
        _commits.Publish(() =>
        {
            lock (_gate) foreach (var key in keys)
                if (_entries.Remove(key, out var entry)) _residentBytes -= entry.EstimatedBytes;
            PublishKeys(keys, CatalogChangeKind.Evicted);
        });
        if (keys.Count > 0)
            WaveeLog.Instance.Event(WaveeLogLevel.Info, "catalog", "catalog.durable.evicted",
                "cache sweep removed " + keys.Count + " catalog rows from memory and disk",
                fields: [WaveeLogField.Of("evicted", keys.Count)]);
    }
```

(Implementer note: write `int before = 0, after = 0;` before `_commits.Publish` — the snippet spells the intent; the
compiler needs the initialisers.)

Replace the child-seed loop in `AcceptAsync` (`:339-351`):

```csharp
                if (next.Error is null && response.Result.Status is (ResourceFetchStatus.Present or ResourceFetchStatus.NotModified)
                    && response.Seeds is not null)
                    foreach (var seed in response.Seeds)
                    {
                        if (seed.Key.Scope != (key.Scope with { Provider = seed.Key.Scope.Provider }) || seed.Patch.Facet != seed.Key.Facet)
                            throw new ArgumentException("Child observations must belong to the origin scope and their own facet.");
                        await LoadCoreAsync(seed.Key, token).ConfigureAwait(false);
                        var child = updates.TryGetValue(seed.Key, out var stagedChild) ? stagedChild : Peek(seed.Key);
                        // Authoritative: the same transport document the child's own recipe decodes — a provider
                        // answer with the parent's timestamp. Otherwise a first-paint filler stamped with the parent's
                        // FetchedAt so eviction order treats it as its parent's age, never as the oldest thing resident.
                        var seeded = seed.Authoritative ? ReduceAuthoritativeSeed(child, seed.Patch, next)
                            : ReduceSeed(child, seed.Patch, next.FetchedAt);
                        if (seeded == child) continue;
                        updates[seed.Key] = seeded;
                        records[seed.Key] = ToRecord(seeded);
                    }
```

`SeedManyAsync` (`:384`): `var next = ReduceSeed(old, seed.Patch, _time.GetUtcNow(), gateReadiness: false);`
`PrepareObservationsAsync` (`:423`): `var next = observation.FillUnknownOnly ? ReduceSeed(old, observation.Patch, _time.GetUtcNow()) : Reduce(old, ResourceFetchResult.Present(observation.Patch));`

Replace `ReduceSeed` + `HasReadinessField` (`:438-470`) with:

```csharp
    // gateReadiness: true for a CHILD reference seed — a byproduct of decoding something else (an ArtistRef, a
    // disc track from AlbumTracks, a library-sync header's referenced item), never independently confirmed on
    // its own. false for the app's own directly observed data about a subject it owns (SeedManyAsync's queue/
    // playback tracks) — gating those would silently drop every OTHER field too (Knowledge stays Unknown ⇒
    // CatalogReadView.Fact returns null for the whole value, not just the name), not merely decline a fake label.
    // observedAt: the parent answer's FetchedAt (a child seed) or now (the app's own observation) — stamped on a
    // PROMOTED entry so resident-trim / cache-sweep ordering ranks the seed by its parent's age. ExpiresAt stays
    // default: an inline seed is never fresh (IsFresh), the facet's own fetch still runs when demanded.
    ResourceSnapshot ReduceSeed(ResourceSnapshot old, CatalogPatch patch, DateTimeOffset observedAt, bool gateReadiness = true)
    {
        if (old.Knowledge == Knowledge.Unsupported) return old;
        // A provider's explicit absence is authoritative for exactly its negative-cache window (Reduce stamps
        // ExpiresAt for Absent); after it a later observation may fill the entry again — Absent is an answer with a
        // TTL, not a permanent fact about the subject.
        if (old.Knowledge == Knowledge.Absent && _time.GetUtcNow() < old.ExpiresAt) return old;
        // An Unknown (or expired-Absent) entry only promotes to Present when the seed actually carries the field
        // that makes it nameable — a gid-only reference (a LeanAlbum disc track, an ArtistRef) must stay Unknown,
        // so the UI reads "never asked" (not "seeded but nameless") and the normal fetch still proceeds. A seed
        // that ADDS fields to an already-Present entry always applies (fill-unknown-only).
        bool present = old.Knowledge == Knowledge.Present;
        if (gateReadiness && !present && !HasReadinessField(patch)) return old;
        var current = present ? old.Value : null;
        var value = CatalogValueRules.Normalize(patch.Apply(current, fillUnknownOnly: true), current);
        CatalogValueRules.Validate(value);
        if (present) return ReferenceEquals(value, old.Value) || value == old.Value ? old : old with { Value = value };
        return old with { Knowledge = Knowledge.Present, Value = value, Provenance = CatalogProvenance.InlineSeed,
            FetchedAt = observedAt, ExpiresAt = default, Error = null };
    }

    /// <summary>A child seed decoded from the SAME extended-metadata document the child's own recipe would fetch
    /// (<see cref="CatalogSeed.Authoritative"/>): the provider's answer for that facet, with the parent's timestamp.
    /// Full replacement (a document is a complete answer for the facets it owns), Provider provenance, fresh for the
    /// child facet's own policy counted from the document's FetchedAt. A NEWER answer the child obtained on its own
    /// (its FetchedAt is later than the document's) is left alone.</summary>
    ResourceSnapshot ReduceAuthoritativeSeed(ResourceSnapshot child, CatalogPatch patch, ResourceSnapshot parent)
    {
        if (child.Knowledge == Knowledge.Present && child.Provenance == CatalogProvenance.Provider
            && child.FetchedAt > parent.FetchedAt) return child;
        var value = CatalogValueRules.Normalize(patch.Apply(child.Value), child.Value);
        CatalogValueRules.Validate(value);
        return child with { Knowledge = Knowledge.Present, Value = value, Provenance = CatalogProvenance.Provider,
            FetchedAt = parent.FetchedAt, ExpiresAt = ResourcePolicy.ExpiresAt(value, parent.FetchedAt), Error = null };
    }

    /// <summary>The one field per facet that makes an entity nameable — TrackIdentity/EpisodeIdentity: Title;
    /// ArtistIdentity/AlbumIdentity/ShowIdentity/PlaylistHeader: Name — whether the seed is a field patch or a
    /// complete replacement value. Every other patch (relations, availability, play count, …) always counts — it
    /// never represents a partial identity guess, only a complete answer.</summary>
    static bool HasReadinessField(CatalogPatch patch) => patch switch
    {
        TrackIdentityPatch p => IsNamed(p.Title),
        EpisodeIdentityPatch p => IsNamed(p.Title),
        ArtistIdentityPatch p => IsNamed(p.Name),
        AlbumIdentityPatch p => IsNamed(p.Name),
        ShowIdentityPatch p => IsNamed(p.Name),
        PlaylistHeaderPatch p => IsNamed(p.Name),
        ReplaceFacetPatch { Value: TrackIdentityValue v } => !string.IsNullOrEmpty(v.Title),
        ReplaceFacetPatch { Value: EpisodeIdentityValue v } => !string.IsNullOrEmpty(v.Title),
        ReplaceFacetPatch { Value: ArtistIdentityValue v } => !string.IsNullOrEmpty(v.Name),
        ReplaceFacetPatch { Value: AlbumIdentityValue v } => !string.IsNullOrEmpty(v.Name),
        ReplaceFacetPatch { Value: ShowIdentityValue v } => !string.IsNullOrEmpty(v.Name),
        ReplaceFacetPatch { Value: PlaylistHeaderValue v } => !string.IsNullOrEmpty(v.Name),
        _ => true,
    };
    static bool IsNamed(FieldChange<string?> field) => field.IsSpecified && !string.IsNullOrEmpty(field.Value);
```

Notes for the implementer: `ReduceSeed` is now an instance method (it reads `_time`); `Reduce` is unchanged;
`ToRecord` (`:613-614`) already carries `FetchedAt`/`ExpiresAt`/`Provenance`, so `WriteCatalogRecordLocked` writes the
parent's timestamp with no persistence change. The cold-read lane's `PreloadAsync`/`LoadCoreAsync`/`Install` are
untouched by this patch.

### C.4 Code — `Backend/Queries/QueryService.cs`

`OnCatalogChange` (`:122-137`) and the node:

```csharp
    void OnCatalogChange(CatalogChangeSet change)
    {
        INode[] nodes;
        lock (_gate) nodes = _nodeSnapshot;
        bool session = change.Kind == CatalogChangeKind.Session;
        // An eviction reaches EVERY node, parked ones included: a parked binding recomputes on reactivation
        // (Handle.SetDemand → Recompute(project:true)) and must be able to cold-read the evicted keys again then —
        // its _coldAsked set is the only thing standing between it and a persisted fact.
        if (change.Kind == CatalogChangeKind.Evicted)
            foreach (var node in nodes) node.ForgetColdAsked(change.Keys);
        foreach (var node in nodes)
            if (node.HasActiveLease && (session || node.DependsOn(change.Keys)))
                node.RecomputeCatalog(change.Keys, change.Kind);
        _ = TrimInactiveAsync();
    }

    interface INode : IDisposable
    {
        int LeaseCount { get; }
        bool HasActiveLease { get; }
        bool DependsOn(IReadOnlyList<ResourceKey> keys);
        bool DependsOnReplica(string id);
        void Recompute();
        void RecomputeCatalog(IReadOnlyList<ResourceKey> keys, CatalogChangeKind kind);
        void ForgetColdAsked(IReadOnlyList<ResourceKey> keys);
        IReadOnlyList<ResourceKey> DemandedResources();
        void RecomputeReplica(string id);
    }

    // inside Node<T>:
        public void ForgetColdAsked(IReadOnlyList<ResourceKey> keys)
        {
            lock (_gate)
            {
                if (_coldAsked.Count == 0) return;
                foreach (var key in keys) _coldAsked.Remove(key);
            }
        }
```

`RecomputeCatalog` (`:264-272`) is unchanged: `Evicted` is neither `Durable` nor `Session`, so an async definition's
preparation is not invalidated by a trim; `Recompute(false)` sees `resourceValuesChanged` and re-projects, and the
now-Unknown dependency is a cold candidate again.

### C.5 Code — `Backend/Queries/CatalogReadView.cs` (R2)

```csharp
    // :43-44 — every joined fact is a cold candidate: the node reads persisted answers of ANY provenance back for
    // the page that renders them (batched once per node, PreloadAsync skips already-loaded entries). Demand still
    // owns the network.
    T? Fact<T>(QueryReadContext read, string uri, FacetKind facet) where T : CatalogValue
        => read.Read<T>(Key(uri, facet));

    // :49-63 — the memo path and the isolated join re-register with allowColdRead: true (was false on both).
    public Track Track(QueryReadContext read, string uri)
    {
        if (_tracks.TryGetValue(uri, out var previous))
        {
            bool unchanged = true;
            foreach (var dependency in previous.Dependencies)
                unchanged &= ReferenceEquals(dependency.Value.Value, read.Read(dependency.Key).Value);
            if (unchanged) return previous.Value;
        }
        var isolated = read.CreateDependencyScope();
        var value = TrackCore(isolated, uri);
        foreach (var key in isolated.Resources.Keys) read.Read(key);
        _tracks[uri] = new(value, new Dictionary<ResourceKey, ResourceSnapshot>(isolated.Resources));
        return value;
    }
```

Drop the `bool cold = false` parameter of `Fact` and the two `, true` call sites (`:109` header, `:174` overview) —
they are now the default. `QueryReadContext.Read`'s `allowColdRead` parameter stays for the status-only path in
`QueryService.Recompute` (`:309`, `read.Read(key, allowColdRead: false)`), which must not re-ask.

Cost: a 300-row playlist projects ~6 keys per row → ~1 800 cold candidates → 8 `LoadColdAsync` batches of 256 → each
one `ReadManyAsync` → `PreloadAsync` → one `ReadCatalogBatch` grouped by `(scope, facet)` (≈ 6 statements of ≤ 400
subjects). The demand path preloads the identity keys first anyway (they are requirements), so the extra work is the
optional facets only. This is the exact surface the cold-read lane is rewriting — see §C.6.

### C.6 What the cold-read lane's rewrite changes

`PreloadAsync`/`LoadCoreAsync`/`Install`/`ReadManyAsync` are being rewritten (batched reads outside the commit
worker). The patches above depend on three properties that the rewrite must keep: (1) a cold read never replaces a
non-Unknown entry (`Install` `:579`), (2) `Loaded` dedupes a second preload of the same key, (3) `ColdRead` is published
only for keys whose knowledge changed. Nothing in §C.3 touches those methods; `ForgetEvicted` still runs after the
sweep. If the rewrite renames `PreloadAsync`, the R2 change is purely on the read-view side and needs no edit.

### C.7 Code — `Backend/Catalog/SpotifyCatalogDecoder.cs` (R3, the five same-document sites)

```csharp
    // :103-129 — sameDocument: true when this LeanTrack IS the TrackV4 document for key.Subject (DecodeCore's
    // TrackIdentity arm); false when it is a disc track inside an AlbumV4 (AlbumTracks below) — that lean track is
    // a different document from the one an Availability request would fetch, and its file plane may be absent.
    public static TrackIdentityPatch Track(ResourceKey key, Lean.LeanTrack track, List<CatalogSeed> seeds, bool sameDocument = true)
    {
        …
        var availability = Availability(track);
        if (availability is not null)
            seeds.Add(new(key with { Facet = FacetKind.Availability, Arguments = default }, new ReplaceFacetPatch(availability),
                Authoritative: sameDocument));
        …
    }

    // :141-154 — the episode's own EpisodeV4 body is the description's document.
        if (episode.HasDescription)
            seeds.Add(new(key with { Facet = FacetKind.EpisodeDetail, Arguments = default },
                new ReplaceFacetPatch(new EpisodeDetailValue(episode.Description)), Authoritative: true));

    // :177-206 AlbumTracks — the album's identity comes out of the very AlbumV4 body an AlbumIdentity request decodes.
        seeds.Add(new(key with { Facet = FacetKind.AlbumIdentity, Arguments = default }, Album(key, album, seeds), Authoritative: true));
        …
                var patch = Track(trackKey, track, seeds, sameDocument: false) with { … };

    // :208-239 ArtistAlbums — same ArtistV4 body as an ArtistIdentity request.
        seeds.Add(new(key with { Facet = FacetKind.ArtistIdentity, Arguments = default }, Artist(artist), Authoritative: true));

    // :241-249 ShowEpisodes — same ShowV4 body as a ShowIdentity request.
        seeds.Add(new(key with { Facet = FacetKind.ShowIdentity, Arguments = default }, Show(show), Authoritative: true));
```

Every other seed in the decoder (`ArtistRefs` `:261-272`, the album seed inside `Track` `:112-114`, the show seed inside
`Episode` `:144-146`, discography album seeds `:224-226`, disc-track identities `:202`) stays `Authoritative: false`:
different subject ⇒ different document. `CatalogDomainSeeds` (Pathfinder envelopes) never produces an authoritative
seed — an overview's play count is not the reputation trait's bytes.

### C.8 Code — `Backend/Queries/RelationProjection.cs` (R5)

```csharp
    // :65-72 — a page without Total is a partial observation (the overview's ten-track seed is the only producer;
    // every provider page carries Total): nothing further is knowable from it, so it names no successor. It is not
    // fresh either (InlineSeed), so the coordinator fetches page 0 from the provider and the chart replaces it.
            candidate.AddRange(page.Items);
            total = page.Total;
            complete = page.Coverage == RelationCoverage.Complete
                || page.NextCursor is null && page.Total == candidate.Count;
            bool knowable = page.Total is not null;
            _nextPage = complete || page.Items.Count == 0 || !knowable ? null
                : PageKey(candidate.Count, page.NextCursor);
            if (complete || !knowable || candidate.Count >= _requestedEnd || page.Items.Count == 0) break;
            cursor = page.NextCursor;
```

`Require` (`:100-120`) is unchanged: with `_nextPage == null` it returns `_needed` = `[page 0]`. The replacement logic
(`:80-88`) already publishes the chart's 50 over the seed's 10 in one step (`_replacementPending` false because
`candidate.Count (50) ≥ min(_items.Count (10), _requestedEnd)`).

### C.9 Code — `Features/Detail/ArtistPage.cs` (R1)

```csharp
        // :86-88 — the initial lease
        var artistView = QueryHooks.Use(Context, svc.Queries, new ArtistDetailQuery(svc.CatalogScope, uri),
            pendingShape, new QueryDemand(true, QueryPriority.Visible,
                [new("tracks", 0, 10), new("popular", 0, ArtistPopularTracks.ExtendedCap), new("appears", 0, 12)],
                ArtistPopularFacets));

        // :98-103 — the live demand
        UseEffect(() => { artistBinding?.SetDemand(new QueryDemand(true, QueryPriority.Visible,
            [new("tracks", 0, 10), new("popular", 0, ArtistPopularTracks.ExtendedCap),
                new("appears", appearsRange.First, appearsRange.Last),
                new("related", relatedRange.First, relatedRange.Last), new("playlists", playlistsRange.First, playlistsRange.Last),
                new("videos", videosRange.First, videosRange.Last)], ArtistPopularFacets)); },
            DepKey.From(HashCode.Combine(artistBinding, appearsRange, relatedRange, playlistsRange, videosRange)));

    // next to UriOf(): the chart renders a play count on every row (ArtistPopular.Row), so the page DEMANDS it —
    // a rendered fact that is only ever seeded (the overview's ten) vanishes on the next process (findings lane C).
    // Window-scoped: RowKeys names it for exactly the "popular"/"tracks" indices, in the same wave as the identity.
    static readonly FacetKind[] ArtistPopularFacets = [FacetKind.PlayCount];
```

`SidebarArtistTopTracksSource` (`:74-76`) keeps `[]`: `MergeDemand` unions optional facets across leases, so when the
page is open the sidebar's `popular` rows (a subset) ride the page's facet; alone, the sidebar shows no counts and
demands none.

`ArtistDefinition` needs no change: `RowKeys(value.TopTracks, CollectionDemand(demand, "tracks", "popular"))` (`:261`)
already yields `View.Key(uri, PlayCount)` for the window indices (`:55-56`); `_popular.Require(End(…))` (`:258`) names the
chart page for the same window.

### C.10 `RowKeys` / `Indices` / lookahead interaction — what the one wave contains

For the artist chart with the page's demand (`popular (0, 50)`, `tracks (0, 10)`, `OptionalFacets [PlayCount]`):

1. `RowIdentityIndices(50, demand)` = `Indices(…, behind 50, ahead 300)` = 0..49 (clamped) → `TrackIdentity` ×50,
   `AlbumIdentity` for known album uris, `ArtistIdentity` for known refs (known only after the identity lands — the
   second wave, whose names TrackV4 already seeded in the first accept, so no visible gap).
2. `Indices(50, demand)` (no lookahead) = 0..49 → `PlayCount` ×50.
3. `Required(...)` distincts; `RunDemandAsync` takes `DemandBatchSize = 256` new keys per wave → all ~100 in wave 1.
4. `EnsureAsync` → one `ReadManyAsync` preload → `CaptureRequestsAsync` → `AdmitLocked`; the worker's
   `TakeBatchLocked` picks every pending job with `BatchGroup == "extended-metadata"` and ≤ 300 distinct subjects →
   50 subjects, 100 jobs, ONE `FetchAsync`.
5. `FetchRawAsync` → `keys` = 100 distinct `(subject, kind)` → `GetExtensionsWithHeadersAsync` (chunked by body size,
   `MetadataChunking.ExtensionRanges`) → per-key decode → `AcceptAsync(responses)` (one commit, `records` for both
   facets and the authoritative `Availability` children) → `PublishUpdates` → one `CatalogChangeSet(Durable)` with
   the identity key AND the play-count key of every row → `ArtistDefinition.Read` once → `ArtistPopular` re-renders once
   with title + count.

The chart page (`envelope:ArtistPopular`) and the overview (`envelope:ArtistOverview`) remain their own waves — the
former is the row list itself (wave 0), the latter is the hero. A row never paints a title without its count.

Lookahead stays identity-only on purpose: `PlayCount` for 300 rows ahead is a second extension per subject in the
same POST (cheap on the wire) but persists 300 more rows per window move; the measured 09-07 problem was untitled
rows, not uncounted ones. If the Plays lane ever needs it, widen `Indices(count, demand)` for optional facets to
`IdentityLookahead` in one place (`RowKeys:55`).

### C.11 Code — `Backend/Catalog/ResourcePolicy.cs`

No change. Authoritative seeds use `ExpiresAt(value, parent.FetchedAt)` = the child facet's own default TTL
(`Availability`/`EpisodeDetail` 6 h, `AlbumIdentity`/`ArtistIdentity`/`ShowIdentity` 1 h) counted from the document's
fetch time — the same horizon their own fetch would have had.

---

## D. Are the Part 5 fixes in the tree, and are they correct?

| Item | State | Evidence | Verdict |
|---|---|---|---|
| 3b `ArtistPopular` seed key `Arguments = new(0, RelationProjection.PageSize)` | in tree | `SpotifyCatalogEnvelopes.cs:149-155` | Correct key. **Gap:** the seed is `Partial, Total = null, NextCursor = null`; `RelationProjection.Read:69-70` computes `_nextPage = PageKey(10, null)` and `Require:110-118` (Total null → `limit = _requestedEnd`) names `(10, 50)` — a second `artist-top-tracks-extensions` GET whose page is an orphan once the chart replaces page 0. Rare online (the chart lands first, so the seed never becomes page 0 — `ReduceSeed` keeps the Provider page), certain when the overview lands first (slow chart, or offline-first-open after the seed). Fix R5 (§C.8). |
| 3f `"popular"` window honoured by `ArtistDefinition` | in tree | `CatalogQueryDefinitions.cs:258,261` `CollectionDemand(demand, "tracks", "popular")`; test `CatalogQueryTests.PopularWindowYieldsATrackIdentityKeyForEveryVisibleChartRow` | Correct. `hydration.settled … rows=50` in every session today. |
| 3d `TrackMetadataReadiness`: seeded-nameless → `Unavailable` | in tree | `Wavee.Core/Catalog/TrackMetadataReadiness.cs:15-21` (Present + Idle + `not Provider`); `TrackMetadataReadinessTests.AlbumMembershipAloneDoesNotMakeBlankRowsReady`, `SeededNamelessIsUnavailable_…` | Correct. Under R3 an *authoritative* seed carries `Provider` provenance and is a complete answer, so a nameless one is `Unavailable` via the `Provider` arm — same outcome. |
| 3d `ReduceSeed` readiness gating | in tree | `CatalogRepository.cs:443-470`; `CatalogRepositoryTests.SeedWithoutATitle_LeavesAnUnknownEntryUnknown_…`, `SeedManyAsync_IsNotReadinessGated_…` | Correct for field patches. **Hole:** `HasReadinessField` returns `true` for a `ReplaceFacetPatch` whose value is a name-less identity (no production producer today; the tests' `new ReplaceFacetPatch(new ArtistIdentityValue("Artist"))` are named) — closed in §C.3. |
| 3e `CompleteLocked` activity reset | in tree | `ResourceCoordinator.cs:444-469` (`resets` list → `ResetAbandonedActivityAsync` → `CatalogRepository.ClearAbandonedActivityAsync:259-274`, key-gated on Queued/Fetching), callers `:107,175,189,238,249` | Correct; `AwaitJobAsync`'s finally (`:201-218`) still handles the waiter-count path. |
| 3a relation paging single wave | in tree | `RelationProjection.Require:100-120`; `SpotifyCatalogResourceProvider.FetchAsync:74-107` decodes one document per `(subject, kind)`; `RelationProjectionTests.OnceTotalIsKnown_RequireNamesEveryFurtherOffsetPageInOneWave` | Correct. Interaction with the seed: see 3b. |
| Schema reset v12 | in tree | `SqliteColdStore.cs:14,46-52,66-90`; `SqliteSchemaResetTests` | Correct. Interaction with cached seeds: a reset drops `catalog_resource`/`catalog_relation_item`/`extension_cache` wholesale (seeds included), keeps `cache_budget_bytes`, re-seeds the `catalog_bytes` counter (`EnsureCatalogAccountingLocked`). No partial state can survive. The user's reset (09-06 18:18) explains why today's DB is small and §A.5 is not yet active. |
| 3g `catalog.fetch.batch` facet/args | in tree | `ResourceCoordinator.LogBatch:356-409` | Correct (the log lines quoted in §A carry `facet=… args=…`). |

---

## E. Tests (behavioural; no source-text tests, no environment switches)

All fixtures exist: `CatalogFixture`/`CatalogMemoryPersistence`/`CatalogClock` (`CatalogRepositoryTests.cs:224-364`,
`Clock.Advance`), `CatalogQueryTestHost`/`QueryTestProvider` (`CatalogQueryTestHost.cs`), `QueryServiceBehaviorTests.Fixture`,
`ResourceCoordinatorTests.TestProvider`, `CatalogResourceProviderTests.Wire/Provider/Request`, `MemoryDataPersistence`.

### E.1 `Wavee.Tests/CatalogRepositoryTests.cs` — append

```csharp
    [Fact]
    public async Task ChildSeed_CarriesItsParentsFetchedAt_AndIsNotTheFirstTrimVictim()
    {
        await using var fixture = new CatalogFixture();
        var old = fixture.Key("old");
        await fixture.AcceptCountAsync(old, 1);                       // Provider, FetchedAt = T0
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        var parent = fixture.Key("parent", FacetKind.TrackIdentity);
        var count = fixture.Key("parent");                            // the seeded PlayCount of the same subject
        var request = await fixture.Repository.CaptureRequestAsync(parent, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(request, ResourceFetchResult.Present(new TrackIdentityPatch(
            Title: FieldChange<string?>.Set("Song"))), [new(count, new ReplaceFacetPatch(new PlayCountValue(42)))])]);

        var seeded = fixture.Repository.Peek(count);
        Assert.Equal(CatalogProvenance.InlineSeed, seeded.Provenance);
        Assert.Equal(fixture.Clock.GetUtcNow(), seeded.FetchedAt);    // the parent's timestamp, not 0001-01-01
        Assert.False(seeded.IsFresh(fixture.Clock.GetUtcNow()));
        Assert.Equal(seeded.FetchedAt, fixture.Persistence.Records[count].FetchedAt);

        // Trim to two entries with nothing pinned: the OLDER provider answer goes, the seed and its parent stay.
        var changes = new List<CatalogChangeSet>();
        using var subscription = fixture.Repository.Changes.Subscribe(new CatalogObserver(changes.Add));
        Assert.True(fixture.Repository.TrimUnpinned(() => [], 2) > 0);
        Assert.Null(fixture.Repository.Peek(old).Value);
        Assert.Equal(42, Assert.IsType<PlayCountValue>(fixture.Repository.Peek(count).Value).Count);
        var evicted = Assert.Single(changes);
        Assert.Equal(CatalogChangeKind.Evicted, evicted.Kind);
        Assert.Equal(new[] { old }, evicted.Keys.ToArray());
    }

    [Fact]
    public async Task AuthoritativeSameDocumentSeed_IsAFreshProviderAnswer_AndReplacesTheChild()
    {
        await using var fixture = new CatalogFixture();
        var identity = fixture.Key("t", FacetKind.TrackIdentity);
        var availability = fixture.Key("t", FacetKind.Availability);
        var first = await fixture.Repository.CaptureRequestAsync(identity, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(first, ResourceFetchResult.Present(new TrackIdentityPatch(
            Title: FieldChange<string?>.Set("Song"))),
            [new(availability, new ReplaceFacetPatch(new AvailabilityValue(Availability.Playable)), Authoritative: true)])]);
        var seeded = fixture.Repository.Peek(availability);
        Assert.Equal(CatalogProvenance.Provider, seeded.Provenance);
        Assert.True(seeded.IsFresh(fixture.Clock.GetUtcNow()));
        Assert.Equal(fixture.Repository.Peek(identity).FetchedAt, seeded.FetchedAt);
        Assert.Equal(CatalogProvenance.Provider, fixture.Persistence.Records[availability].Provenance);

        // A later document REPLACES the child (a complete answer), it does not fill-unknown-only.
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await fixture.Repository.CaptureRequestAsync(identity, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(second, ResourceFetchResult.Present(new TrackIdentityPatch(
            Title: FieldChange<string?>.Set("Song"))),
            [new(availability, new ReplaceFacetPatch(new AvailabilityValue(Availability.Unavailable)), Authoritative: true)])]);
        Assert.Equal(Availability.Unavailable, Assert.IsType<AvailabilityValue>(fixture.Repository.Peek(availability).Value).Verdict);

        // …but never an answer the child obtained on its own AFTER the document.
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var own = await fixture.Repository.CaptureRequestAsync(availability, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(own, ResourceFetchResult.Present(new ReplaceFacetPatch(new AvailabilityValue(Availability.Playable))))]);
        var third = await fixture.Repository.CaptureRequestAsync(identity, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(third, ResourceFetchResult.Present(new TrackIdentityPatch(
            Title: FieldChange<string?>.Set("Song"))),
            [new(availability, new ReplaceFacetPatch(new AvailabilityValue(Availability.Unavailable)), Authoritative: true)])]);
        Assert.Equal(Availability.Playable, Assert.IsType<AvailabilityValue>(fixture.Repository.Peek(availability).Value).Verdict);
    }

    [Fact]
    public async Task ProviderAbsence_BlocksSeedsOnlyForItsNegativeCacheWindow()
    {
        await using var fixture = new CatalogFixture();
        var count = fixture.Key("t");
        var parent = fixture.Key("t", FacetKind.TrackIdentity);
        var request = await fixture.Repository.CaptureRequestAsync(count, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(request, ResourceFetchResult.Absent(TimeSpan.FromHours(1)))]);
        async Task SeedAsync(long value)
        {
            var seed = await fixture.Repository.CaptureRequestAsync(parent, ResourcePriority.Visible);
            await fixture.Repository.AcceptAsync([new(seed, ResourceFetchResult.Present(new TrackIdentityPatch(
                Title: FieldChange<string?>.Set("Song"))), [new(count, new ReplaceFacetPatch(new PlayCountValue(value)))])]);
        }
        await SeedAsync(7);
        Assert.Equal(Knowledge.Absent, fixture.Repository.Peek(count).Knowledge);      // inside the window: the provider wins
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        await SeedAsync(8);
        var filled = fixture.Repository.Peek(count);
        Assert.Equal(Knowledge.Present, filled.Knowledge);                              // after it: an answer with a TTL, not a fact
        Assert.Equal(8, Assert.IsType<PlayCountValue>(filled.Value).Count);
        Assert.Equal(CatalogProvenance.InlineSeed, filled.Provenance);
    }

    [Fact]
    public async Task NamelessReplacementSeed_DoesNotPromoteAnUnknownIdentity()
    {
        await using var fixture = new CatalogFixture();
        var parent = fixture.Key("track", FacetKind.TrackIdentity);
        var artist = fixture.Key("artist", FacetKind.ArtistIdentity);
        var request = await fixture.Repository.CaptureRequestAsync(parent, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(request, ResourceFetchResult.Present(new TrackIdentityPatch(
            Title: FieldChange<string?>.Set("Song"))), [new(artist, new ReplaceFacetPatch(new ArtistIdentityValue(Image: new("https://img"))))])]);
        Assert.Equal(Knowledge.Unknown, fixture.Repository.Peek(artist).Knowledge);
    }
```

### E.2 `Wavee.Tests/EntityResidencyTests.cs` — replace the first test's tail

```csharp
        Assert.True(fixture.Repository.TrimUnpinned(() => [first], 1) > 0);
        Assert.NotNull(fixture.Repository.Peek(first).Value);
        Assert.Null(fixture.Repository.Peek(second).Value);
        // Evicted, not Activity: a query node must be able to tell "dropped from memory" from a real activity change.
        Assert.Equal(CatalogChangeKind.Evicted, Assert.Single(changes, change => change.Keys.Contains(second)).Kind);
        Assert.Equal(2, Assert.IsType<Wavee.Core.Catalog.PlayCountValue>((await fixture.Repository.ReadAsync(second)).Value).Count);
```

(with `var changes = new List<CatalogChangeSet>(); using var sub = fixture.Repository.Changes.Subscribe(new CatalogObserver(changes.Add));`
declared before the trim; `CatalogObserver` is the helper `CatalogRepositoryTests.cs` already uses.)

### E.3 `Wavee.Tests/QueryServiceBehaviorTests.cs` — append (uses the file's `Fixture`)

```csharp
    // R2 + R4: a fact the definition JOINS but does not REQUIRE (the artist chart's play count before R1; any
    // seeded extra) is cold-read once, evicted by a resident trim, and — because the trim is an Evicted publication
    // that clears the node's cold-read bookkeeping — read back from persistence instead of staying Unknown for the
    // node's lifetime.
    sealed record ExtraSpec(CatalogScope Scope) : QuerySpec<(string Title, long Count)>(Scope);
    sealed class ExtraDefinition(CatalogScope scope) : IQueryDefinition<(string Title, long Count)>
    {
        public readonly ResourceKey Root = new(scope, "spotify:track:extra", FacetKind.TrackIdentity);
        public readonly ResourceKey Extra = new(scope, "spotify:track:extra", FacetKind.PlayCount);
        public QueryReadResult<(string, long)> Read(QueryReadContext read)
        {
            var track = read.Read<TrackIdentityValue>(Root);
            var count = read.Read<PlayCountValue>(Extra);           // joined (cold candidate) …
            return new((track?.Title ?? "", count?.Count ?? 0), 0, track is not null);
        }
        public QueryRequirements Requirements((string, long) value, QueryDemand demand) => new([Root], []);   // … but not required
    }

    [Fact]
    public async Task AnEvictedJoinedFact_IsColdReadAgainByTheSameNode()
    {
        await using var f = new Fixture();
        var definition = new ExtraDefinition(f.Catalog.Scope);
        f.Queries.Register<ExtraSpec, (string Title, long Count)>(_ => definition);
        var identity = await f.Catalog.Repository.CaptureRequestAsync(definition.Root, ResourcePriority.Visible);
        await f.Catalog.Repository.AcceptAsync([new(identity, ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue("Song"))),
            [new(definition.Extra, new ReplaceFacetPatch(new PlayCountValue(42)))])]);

        using var handle = f.Queries.Acquire(new ExtraSpec(f.Catalog.Scope));
        handle.SetDemand(QueryDemand.Initial);
        Assert.Equal(42, handle.Current.Value.Count);

        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<long>();
        using var sub = handle.Changes.Subscribe(Observers.From<QuerySnapshot<(string Title, long Count)>>(snapshot =>
        { seen.Add(snapshot.Value.Count); if (seen.Count > 1 && snapshot.Value.Count == 42) restored.TrySetResult(); }));

        // Pins = the node's requirements (Root only): the joined PlayCount is unpinned and goes.
        Assert.True(f.Catalog.Repository.TrimUnpinned(f.Queries.GetActiveResourceKeys, 1) > 0);
        Assert.Null(f.Catalog.Repository.Peek(definition.Extra).Value);

        await restored.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(42, handle.Current.Value.Count);
        Assert.Contains(0L, seen);                                    // it DID publish the gap once …
        Assert.Equal(42, seen[^1]);                                   // … and healed from persistence, not the network
        Assert.Empty(f.Provider.Requests.Where(key => key.Facet == FacetKind.PlayCount));
    }

    [Fact]
    public async Task AParkedNode_ReadsAnEvictedJoinedFactBackOnReactivation()
    {
        await using var f = new Fixture();
        var definition = new ExtraDefinition(f.Catalog.Scope);
        f.Queries.Register<ExtraSpec, (string Title, long Count)>(_ => definition);
        var identity = await f.Catalog.Repository.CaptureRequestAsync(definition.Root, ResourcePriority.Visible);
        await f.Catalog.Repository.AcceptAsync([new(identity, ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue("Song"))),
            [new(definition.Extra, new ReplaceFacetPatch(new PlayCountValue(42)))])]);
        using var handle = f.Queries.Acquire(new ExtraSpec(f.Catalog.Scope));
        handle.SetDemand(QueryDemand.Initial);
        Assert.Equal(42, handle.Current.Value.Count);
        handle.SetDemand(QueryDemand.None);                            // parked: no pins, no recompute on changes

        Assert.True(f.Catalog.Repository.TrimUnpinned(f.Queries.GetActiveResourceKeys, 0) > 0);
        Assert.Null(f.Catalog.Repository.Peek(definition.Extra).Value);

        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = handle.Changes.Subscribe(Observers.From<QuerySnapshot<(string Title, long Count)>>(snapshot =>
        { if (snapshot.Value.Count == 42) restored.TrySetResult(); }));
        handle.SetDemand(QueryDemand.Initial);                         // reactivation recomputes; the evicted keys were forgotten
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(42, handle.Current.Value.Count);
    }
```

### E.4 `Wavee.Tests/RelationProjectionTests.cs` — append

```csharp
    [Fact]
    public async Task ASeededPageWithoutTotal_NamesNoSuccessor_AndIsReplacedByTheProvidersPageZero()
    {
        await using var fixture = new CatalogFixture();
        var projection = new RelationProjection(args => new(fixture.Scope, "spotify:artist:a", FacetKind.ArtistPopular, args));
        projection.Require(50);
        // The overview's partial observation: ten rows, no Total, no cursor (SpotifyCatalogEnvelopes.cs:152-155).
        var page0 = new ResourceKey(fixture.Scope, "spotify:artist:a", FacetKind.ArtistPopular, new(0, 50));
        var parent = new ResourceKey(fixture.Scope, "spotify:artist:a", FacetKind.ArtistOverview);
        var overview = await fixture.Repository.CaptureRequestAsync(parent, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(overview, ResourceFetchResult.Present(new ReplaceFacetPatch(new ArtistOverviewValue(Bio: "x"))),
            [new(page0, new ReplaceFacetPatch(new RelationPageValue(FacetKind.ArtistPopular, "overview", null, 0, null, null, RelationCoverage.Partial,
                Enumerable.Range(0, 10).Select(i => new CatalogRelationItem("popular:" + i, "spotify:track:" + i)).ToArray())))])]);

        projection.Read(new(fixture.Repository));
        Assert.True(projection.Loaded);
        Assert.Equal(10, projection.Items.Count);
        Assert.Null(projection.Total);
        // Only page 0 — never (10, 50): a seed knows nothing beyond itself; page 0 is not fresh and is re-fetched.
        Assert.Equal(new[] { page0 }, projection.Require(50).ToArray());
        Assert.False(fixture.Repository.Peek(page0).IsFresh(fixture.Clock.GetUtcNow()));

        // The chart replaces it in one step.
        var chart = await fixture.Repository.CaptureRequestAsync(page0, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync([new(chart, ResourceFetchResult.Present(new ReplaceFacetPatch(new RelationPageValue(
            FacetKind.ArtistPopular, "chart", null, 0, 50, null, RelationCoverage.Complete,
            Enumerable.Range(0, 50).Select(i => new CatalogRelationItem("row:" + i, "spotify:track:" + i)).ToArray()))))]);
        projection.Read(new(fixture.Repository));
        Assert.Equal(50, projection.Items.Count);
        Assert.Equal(50, projection.Total);
        Assert.True(projection.Complete);
    }
```

### E.5 `Wavee.Tests/CatalogQueryTests.cs` — append

```csharp
    // R1 + R6 for the chart: the page's popular window with OptionalFacets [PlayCount] names identity AND count for
    // every chart row, both land in ONE provider call and ONE publication — never a titled row without its count.
    sealed class BatchRecorder(Func<ResourceRequest, ResourceResponse> fetch) : ICatalogResourceProvider
    {
        public string Provider => "spotify";
        public readonly List<ResourceKey[]> Batches = [];
        public ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
        {
            lock (Batches) Batches.Add(requests.Select(request => request.Key).ToArray());
            return ValueTask.FromResult<IReadOnlyList<ResourceResponse>>(requests.Select(fetch).ToArray());
        }
    }
    static ResourceResponse ChartFacts(ResourceRequest request) => request.Key.Facet switch
    {
        FacetKind.TrackIdentity => new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue("Song " + EntityUri.IdOf(request.Key.Subject))))),
        FacetKind.PlayCount => new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(new PlayCountValue(1_000 + EntityUri.IdOf(request.Key.Subject).Length)))),
        _ => new(request, ResourceFetchResult.Absent()),
    };
    static readonly QueryDemand ChartDemand = new(true, QueryPriority.Visible,
        [new("tracks", 0, 10), new("popular", 0, ArtistPopularTracks.ExtendedCap)], [FacetKind.PlayCount]);

    [Fact]
    public async Task PopularWindowWithPlayCountFacet_FetchesEveryRowsIdentityAndCountInOneBatchAndOnePublication()
    {
        var provider = new BatchRecorder(ChartFacts);
        await using var host = new CatalogQueryTestHost(provider);
        const string artist = "spotify:artist:counted";
        var items = Enumerable.Range(0, 50).Select(i => new CatalogRelationItem("row:" + i, "spotify:track:c" + i)).ToArray();
        await host.AcceptAsync(new(host.Scope, artist, FacetKind.ArtistPopular, new(0, 50)),
            new RelationPageValue(FacetKind.ArtistPopular, "chart", null, 0, 50, null, RelationCoverage.Complete, items));
        var publications = new List<CatalogChangeSet>();
        using var changes = host.Data.Catalog.Changes.Subscribe(Observers.From<CatalogChangeSet>(change =>
        { if (change.Kind == CatalogChangeKind.Durable) lock (publications) publications.Add(change); }));

        var result = await host.Data.Queries.ReadOnceAsync(new ArtistDetailQuery(host.Scope, artist), ChartDemand, TestContext.Current.CancellationToken);

        var rows = result.Value.TopTracks!;
        Assert.Equal(50, rows.Count);
        Assert.All(rows, row => { Assert.StartsWith("Song ", row.Title); Assert.True(row.PlayCount > 0); });
        // One batch carried both facets of every row …
        ResourceKey[] batch;
        lock (provider.Batches) batch = Assert.Single(provider.Batches, b => b.Any(key => key.Facet == FacetKind.PlayCount));
        Assert.All(items, item =>
        {
            Assert.Contains(batch, key => key.Subject == item.EntityUri && key.Facet == FacetKind.TrackIdentity);
            Assert.Contains(batch, key => key.Subject == item.EntityUri && key.Facet == FacetKind.PlayCount);
        });
        // … and one publication delivered both — a snapshot can never hold a titled row without its count.
        CatalogChangeSet durable;
        lock (publications) durable = Assert.Single(publications, p => p.Keys.Any(key => key.Facet == FacetKind.PlayCount));
        Assert.All(items, item =>
        {
            Assert.Contains(durable.Keys, key => key.Subject == item.EntityUri && key.Facet == FacetKind.TrackIdentity);
            Assert.Contains(durable.Keys, key => key.Subject == item.EntityUri && key.Facet == FacetKind.PlayCount);
        });
    }

    [Fact]
    public async Task PopularWindowWithoutTheFacet_NeverDemandsPlayCount()
    {
        // The sidebar's top-tracks source leases "popular" with no optional facets; alone it must cost no counts.
        var provider = new BatchRecorder(ChartFacts);
        await using var host = new CatalogQueryTestHost(provider);
        const string artist = "spotify:artist:uncounted";
        await host.AcceptAsync(new(host.Scope, artist, FacetKind.ArtistPopular, new(0, 50)),
            new RelationPageValue(FacetKind.ArtistPopular, "chart", null, 0, 3, null, RelationCoverage.Complete,
                Enumerable.Range(0, 3).Select(i => new CatalogRelationItem("row:" + i, "spotify:track:u" + i)).ToArray()));
        await host.Data.Queries.ReadOnceAsync(new ArtistDetailQuery(host.Scope, artist),
            new(true, QueryPriority.Visible, [new("popular", 0, 3)], []), TestContext.Current.CancellationToken);
        lock (provider.Batches) Assert.DoesNotContain(provider.Batches.SelectMany(b => b), key => key.Facet == FacetKind.PlayCount);
    }
```

### E.6 New file `Wavee.Tests/ArtistTopTracksDurabilityTests.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>The user's complaint, end to end: an artist re-opened in the NEXT process inside the overview's 12 h
/// TTL must still show its chart's play counts — from persistence, without re-fetching the overview and without
/// re-fetching counts that are still fresh — and a first open must show counts on every chart row, not the
/// overview's ten.</summary>
public sealed class ArtistTopTracksDurabilityTests
{
    static readonly CatalogScope Scope = new("spotify", "me", "en", "NL", "premium", 1, false);
    const string Artist = "spotify:artist:durable";
    static readonly string[] Chart = Enumerable.Range(0, 12).Select(i => "spotify:track:d" + i).ToArray();
    static readonly QueryDemand PageDemand = new(true, QueryPriority.Visible,
        [new("tracks", 0, 10), new("popular", 0, ArtistPopularTracks.ExtendedCap)], [FacetKind.PlayCount]);

    /// <summary>The Spotify shapes that matter here: the overview seeds the first ten rows' identity + count, the
    /// chart is a bare URI list, extended metadata answers identity and count per subject.</summary>
    sealed class SpotifyShapes : ICatalogResourceProvider
    {
        public string Provider => "spotify";
        public readonly List<ResourceKey> Requests = [];
        public ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
        {
            lock (Requests) Requests.AddRange(requests.Select(request => request.Key));
            return ValueTask.FromResult<IReadOnlyList<ResourceResponse>>(requests.Select(Answer).ToArray());
        }
        static ResourceResponse Answer(ResourceRequest request)
        {
            var key = request.Key;
            switch (key.Facet)
            {
                case FacetKind.ArtistOverview:
                    var seeds = new List<CatalogSeed>();
                    foreach (var uri in Chart.Take(ArtistPopularTracks.OverviewSeedCap))
                        CatalogDomainSeeds.Track(key.Scope, new Track(EntityUri.IdOf(uri), uri, "Seeded " + EntityUri.IdOf(uri), [],
                            new("", "", ""), 200_000, false, null, PlayCount: 5_000), seeds);
                    return new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(new ArtistOverviewValue(MonthlyListeners: 1))), seeds);
                case FacetKind.ArtistPopular:
                    return new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(new RelationPageValue(FacetKind.ArtistPopular,
                        "chart", null, 0, Chart.Length, null, RelationCoverage.Complete,
                        Chart.Select((uri, i) => new CatalogRelationItem("row:" + i, uri)).ToArray()))));
                case FacetKind.TrackIdentity:
                    return new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue("Fetched " + EntityUri.IdOf(key.Subject), DurationMs: 200_000))));
                case FacetKind.PlayCount:
                    return new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(new PlayCountValue(9_000))));
                case FacetKind.ArtistIdentity:
                    return new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(new ArtistIdentityValue("Artist"))));
                default:
                    return new(request, ResourceFetchResult.Absent());
            }
        }
    }

    static CatalogRuntime Runtime(MemoryDataPersistence persistence, SpotifyShapes provider)
        => new(Scope, Scope.ProviderAccount, persistence, persistence, new MemoryReplicaProjection(new InMemoryStore()), [provider]);

    [Fact]
    public async Task FirstOpen_CountsEveryChartRow_NotJustTheOverviewsTen()
    {
        var persistence = new MemoryDataPersistence();
        var provider = new SpotifyShapes();
        await using var runtime = Runtime(persistence, provider);
        var result = await runtime.Queries.ReadOnceAsync(new ArtistDetailQuery(Scope, Artist), PageDemand, TestContext.Current.CancellationToken);
        var rows = result.Value.TopTracks!;
        Assert.Equal(Chart.Length, rows.Count);
        Assert.All(rows, row => Assert.True(row.PlayCount > 0));          // rows 11 and 12 too — the chart demanded them
        Assert.All(rows, row => Assert.StartsWith("Fetched ", row.Title));  // the provider's answer replaced the seed
        lock (provider.Requests) Assert.Equal(Chart.Length, provider.Requests.Count(key => key.Facet == FacetKind.PlayCount));
    }

    [Fact]
    public async Task ReopenedInTheNextProcess_InsideTheOverviewTtl_StillShowsEveryCount_WithoutRefetching()
    {
        var persistence = new MemoryDataPersistence();
        var first = new SpotifyShapes();
        await using (var runtime = Runtime(persistence, first))
            await runtime.Queries.ReadOnceAsync(new ArtistDetailQuery(Scope, Artist), PageDemand, TestContext.Current.CancellationToken);

        var second = new SpotifyShapes();
        await using var reopened = Runtime(persistence, second);
        var result = await reopened.Queries.ReadOnceAsync(new ArtistDetailQuery(Scope, Artist), PageDemand, TestContext.Current.CancellationToken);

        Assert.All(result.Value.TopTracks!, row => { Assert.StartsWith("Fetched ", row.Title); Assert.True(row.PlayCount > 0); });
        lock (second.Requests)
        {
            Assert.DoesNotContain(second.Requests, key => key.Facet == FacetKind.ArtistOverview);   // 12 h fresh
            Assert.DoesNotContain(second.Requests, key => key.Facet == FacetKind.PlayCount);        // 6 h fresh, read from persistence
            Assert.DoesNotContain(second.Requests, key => key.Facet == FacetKind.TrackIdentity);    // 1 h fresh
        }
    }

    [Fact]
    public async Task ReopenedWithoutTheFacetDemand_StillReadsThePersistedSeedBack()
    {
        // R2 alone (join ⇒ cold candidate): even a lease that does NOT demand PlayCount — the sidebar source — reads a
        // persisted seed back. Only the overview's ten rows have one; that is what the seed is for.
        var persistence = new MemoryDataPersistence();
        var first = new SpotifyShapes();
        var sidebar = new QueryDemand(true, QueryPriority.Visible, [new("popular", 0, 10)], []);
        await using (var runtime = Runtime(persistence, first))
            await runtime.Queries.ReadOnceAsync(new ArtistDetailQuery(Scope, Artist), sidebar, TestContext.Current.CancellationToken);
        await using var reopened = Runtime(persistence, new SpotifyShapes());
        var result = await reopened.Queries.ReadOnceAsync(new ArtistDetailQuery(Scope, Artist), sidebar, TestContext.Current.CancellationToken);
        Assert.All(result.Value.TopTracks!.Take(ArtistPopularTracks.OverviewSeedCap), row => Assert.Equal(5_000, row.PlayCount));
        Assert.Equal(CatalogProvenance.InlineSeed, reopened.Catalog.Peek(new(Scope, Chart[0], FacetKind.PlayCount)).Provenance);
    }
}
```

### E.7 `Wavee.Tests/ResourceCoordinatorTests.cs` — append (uses the file's `TestProvider`)

```csharp
    [Fact]
    public async Task TwoFacetsOfOneSubject_ShareOneProviderCallAndOnePublication()
    {
        await using var fixture = new CatalogFixture();
        var identity = fixture.Key("t", FacetKind.TrackIdentity);
        var count = fixture.Key("t", FacetKind.PlayCount);
        var provider = new TestProvider((requests, _) => Task.FromResult<IReadOnlyList<ResourceResponse>>(requests.Select(request =>
            new ResourceResponse(request, ResourceFetchResult.Present(new ReplaceFacetPatch(request.Key.Facet == FacetKind.PlayCount
                ? new PlayCountValue(9) : new TrackIdentityValue("Song"))))).ToArray()));
        await using var coordinator = new ResourceCoordinator(fixture.Repository, [provider], fixture.Clock);
        var durable = new List<CatalogChangeSet>();
        using var sub = fixture.Repository.Changes.Subscribe(new CatalogObserver(change => { if (change.Kind == CatalogChangeKind.Durable) durable.Add(change); }));

        var results = await coordinator.EnsureAsync([identity, count]).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.Equal(ResourceEnsureStatus.Ready, result.Status));
        Assert.Equal(1, provider.Calls);
        var publication = Assert.Single(durable);
        Assert.Contains(identity, publication.Keys);
        Assert.Contains(count, publication.Keys);
    }
```

### E.8 `Wavee.Tests/CatalogResourceProviderTests.cs` — append (uses `Wire`/`Provider`/`Request`)

```csharp
    [Fact]
    public async Task OneExtendedMetadataPost_AsksTrackV4AndReputationForTheSameSubject()
    {
        var asks = new List<Xm.BatchedEntityRequest>();
        var http = new FakeExchange((request, _) =>
        {
            asks.Add(Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(request.Body!)));
            var response = new Xm.BatchedExtensionResponse();
            var track = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.TrackV4 };
            track.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:track:t",
                Header = new Xm.EntityExtensionDataHeader { StatusCode = 200 },
                ExtensionData = new Any { Value = new Lean.LeanTrack { Name = "Song", EarliestLiveTimestamp = 1_800_000_000 }.ToByteString() } });
            var plays = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.OnPlatformReputationTrait };
            plays.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:track:t",
                Header = new Xm.EntityExtensionDataHeader { StatusCode = 200 },
                ExtensionData = new Any { Value = ByteString.CopyFrom(new byte[] { 24, 42 }) } });
            response.ExtendedMetadata.Add(track); response.ExtendedMetadata.Add(plays);
            return Ok(response.ToByteArray());
        });
        var responses = await Provider(http).FetchAsync([Request(FacetKind.TrackIdentity, id: 1), Request(FacetKind.PlayCount, id: 2)],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, http.Calls);
        var ask = Assert.Single(asks);
        var entity = Assert.Single(ask.EntityRequest);
        Assert.Equal(new[] { Xm.ExtensionKind.TrackV4, Xm.ExtensionKind.OnPlatformReputationTrait }.ToHashSet(),
            entity.Query.Select(query => query.ExtensionKind).ToHashSet());
        Assert.Equal(42, Assert.IsType<PlayCountValue>(responses[1].Result.Patch!.Apply(null)).Count);
        // R3: the TrackV4 body's availability is the same document an Availability request decodes — authoritative;
        // nothing else in that response is.
        var availability = Assert.Single(responses[0].Seeds!, seed => seed.Key.Facet == FacetKind.Availability);
        Assert.True(availability.Authoritative);
        Assert.Equal("spotify:track:t", availability.Key.Subject);
        Assert.All(responses[0].Seeds!.Where(seed => seed.Key.Facet != FacetKind.Availability), seed => Assert.False(seed.Authoritative));
    }
```

### E.9 `Wavee.Tests/CatalogResourceDecoderTests.cs` — append (uses `Decode`/`Gid`)

```csharp
    [Fact]
    public void SameDocumentSeedsAreAuthoritative_CrossDocumentReferencesAreNot()
    {
        // TrackV4 → Availability (same subject, same document) yes; its ArtistRef / album ref no.
        var track = new Lean.LeanTrack { Name = "Song", EarliestLiveTimestamp = 1_800_000_000, Album = new Lean.LeanAlbumRef { Gid = Gid(2), Name = "Album" } };
        track.Artist.Add(new Lean.LeanArtistRef { Gid = Gid(3), Name = "Artist" });
        var decoded = Decode(FacetKind.TrackIdentity, track.ToByteString());
        Assert.True(Assert.Single(decoded.Seeds, seed => seed.Key.Facet == FacetKind.Availability).Authoritative);
        Assert.All(decoded.Seeds.Where(seed => seed.Key.Facet != FacetKind.Availability), seed => Assert.False(seed.Authoritative));

        // AlbumV4 (AlbumTracks) → its own AlbumIdentity yes; the disc tracks' identities and THEIR availability no.
        var album = new Lean.LeanAlbum { Gid = Gid(1), Name = "Album" };
        var disc = new Lean.LeanDisc { Number = 1 };
        disc.Track.Add(new Lean.LeanTrack { Gid = Gid(2), Name = "Track", EarliestLiveTimestamp = 1_800_000_000 });
        album.Disc.Add(disc);
        var pages = Decode(FacetKind.AlbumTracks, album.ToByteString(), uri: "spotify:album:" + Base62.Encode(Gid(1).Span));
        Assert.True(Assert.Single(pages.Seeds, seed => seed.Key.Facet == FacetKind.AlbumIdentity && seed.Key.Subject.StartsWith("spotify:album:")).Authoritative);
        Assert.All(pages.Seeds.Where(seed => seed.Key.Facet is FacetKind.TrackIdentity or FacetKind.Availability), seed => Assert.False(seed.Authoritative));
    }
```

(If the album fixture's `Lean.LeanDisc` name differs, copy the disc construction from the file's first test at `:20-25`.)

### E.10 Existing tests to touch

- `CatalogRepositoryTests.Acceptance_PersistsBeforeSinglePrecisePublicationIncludingSeeds` (`:36-57`): still passes
  (a named `ArtistIdentityValue` seed; `InlineSeed`, not fresh). Add
  `Assert.Equal(fixture.Repository.Peek(parent).FetchedAt, seeded.FetchedAt);`.
- `QueryServiceBehaviorTests.SteadyResidentTrimPreservesActiveDemand_AndLeavesEvictedFactsDurable` (`:140-155`): unchanged
  behaviour; the seeds carry `now` as `FetchedAt` and the pinned root survives.
- `CatalogPreloginNativeTests:53` asserts a child seed is not fresh — still true (native provider seeds are not authoritative).

---

## F. Verification — what proves it in the app and the log

1. **Build/test gates** (orchestrator only): Debug + Release clean, `Wavee.Tests` green including E.1–E.9.
2. **First open of an artist (any process)**: `catalog.demand.wave spec=ArtistDetailQuery … facets=[TrackIdentity=50,PlayCount=50,…]`
   in the row wave (today: never `PlayCount`), followed by ONE
   `catalog.fetch.batch provider=spotify group=extended-metadata keys=~100 subjects=50 first=spotify:track:… facet=TrackIdentity … ready=~100`.
   Every chart row (both pager columns, all 50) shows "… plays". `hydration.settled surface=ArtistPopular` unchanged.
3. **Re-open the same artist in a NEW process within 12 h**: NO `group=envelope:ArtistOverview` batch for it (fresh),
   NO `facet=PlayCount` batch for its rows (fresh for 6 h, read from persistence), counts visible on every row.
   After 6 h: one `PlayCount`-bearing extended-metadata batch, counts unchanged or updated — never blank.
4. **Trim**: the new `catalog.resident.trim before=… after=… evicted=… seeds=…` line appears during long sessions;
   opening a KeepAlive-parked artist page afterwards shows its counts (E.3 is the unit proof; in the app: browse ten
   artists, go back through history, every chart still counted). `seeds=` should be small after the `FetchedAt` fix
   (seeds are no longer the front of the LRU).
5. **Sweep**: Settings → Diagnostics cache stats (`GetStatsAsync`: cache bytes vs budget, pinned rows). If `catalog_bytes`
   exceeds the budget, `catalog.durable.evicted` lines appear every 5 min; counts must still be present after the
   next open (they are demanded → refetched).
6. **Availability / episode descriptions**: after a restart, unavailable rows stay greyed and episode rows keep their
   descriptions (R2 + R3) — no `getTrack` Pathfinder storm (`group=envelope:Availability` must not appear for playlist
   pages without the PlayableOnly filter).
7. **`hydration.gaps`**: extend `HydrationGaps.Note` with `uncounted=` (rows with an identity but `PlayCount == 0`) for
   the `ArtistPopular` surface so this class of gap is visible in the always-on log; `hydration.settled` for the chart
   should only fire once every row has both a title and a count (or an explicit `Absent` for its count).
