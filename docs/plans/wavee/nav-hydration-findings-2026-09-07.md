# Navigation, hydration and the frame pipeline — handoff (2026-09-07, evening)

**Scope.** Why navigation "is sometimes fast and other times slow", why launching is slow, why an uncached page
shows nothing and then flashes, and why the artist chart renders no play counts. Investigation only — **no
production code was changed for this document**. The only file added is this one.

**Tree.** Branch `feat/library-v3-1`, 485 uncommitted files, **Debug and Release both build clean**.

**Read this first if you are picking the work up:** findings are ordered by what has to be fixed first, not by
severity. Finding 1 is an engine defect, finding 2 is a seam defect, findings 3–4 are contract defects in the query
definitions, finding 5 is startup sequencing, finding 6 is why none of them tripped an alarm.

---

## How this was measured

The real Release build was launched and driven through `WM_COPYDATA` deep links (window class `FluentGpuWindow`,
`dwData = 0x46474143`, payload = the `wavee://` string — the `SingleInstanceGate` receive path). That costs no
process spawn per navigation, so the numbers are the app's own. Instrumentation used is already in the tree:
`NavigationFrameWatch` (`nav.frames`, `frame.stall`), `HydrationGaps` (`hydration.gaps`, `hydration.settled`),
`DemandWaveDiagnostics` (`catalog.demand.wave`).

| Run | Contents | pid |
|---|---|---|
| A | `ops/tools/nav-measure.ps1` — 10 deep links, warm catalog | 30992 |
| B | 6 entities the catalog had **never** seen (Taylor Swift, Radiohead, RAM, Kendrick, The Weeknd) | 47484 |
| C | 72 navigations at four pacings (3000 / 1200 / 500 / 3000 ms dwell) | 54896 |
| D | The 1,494-track *Q-top 1500*, then 8 never-opened artists, 8 never-opened albums, then a 600 ms rapid interleave | 37752 |

Run D's targets were harvested read-only from the app's own cache, which is what makes them the honest
"clicked from the artist column of a playlist" case:

```sql
-- facet 4 = ArtistIdentity, 21 = ArtistOverview, 3 = AlbumIdentity, 20 = AlbumDetail
select subject from catalog_resource where facet=4
  and subject not in (select subject from catalog_resource where facet=21);
```

**845 artists are cached by identity but only 16 have an overview; 1,124 albums by identity, only 6 with detail.**

---

## Verdict

1. **Route changes are not slow.** Warm, never-cached and never-opened pages all paint in **2.6–15 ms**. Rapid
   600 ms interleaved navigation produced **zero** stalls. Navigation machinery, layout, virtualization and the GPU
   path are not the problem and should not be re-investigated.
2. **The engine lets managed code freeze the frame** (finding 1). The reactive flush — every component `Render()`
   and every computed — runs inline on the UI thread with **no budget**, while the engine's *own* phases are
   budget-bounded. The render thread is submit-only and animation ticks downstream of the flush, so a long managed
   recompute stops content *and* motion.
3. **Per-publication UI cost is O(total rows), not O(visible rows)** (finding 2). The 1,494-row playlist spends
   **721 ms of reactive flush in one frame** because each publication hands the page a fresh resources dictionary
   that invalidates its whole-list projection.
4. **Pages paint fast because they paint nothing** (finding 3). `HasPrimaryData` is `identity != null` for the
   artist and album queries, so an entity whose identity is already resident goes Ready with zero content, then
   flashes one round trip later.
5. **A page renders facts it never demands** (finding 4) — the missing play counts.
6. **Startup runs the whole query graph before a session exists** (finding 5): 65 demand waves and 51 replica
   deferrals in the 2,051 ms before `protocol-installed`, then everything re-runs.
7. **The hydration gauge counts titles only** (finding 6), so it reports `settled` on a page whose plays column is
   entirely blank.

---

## Finding 1 — the engine runs unbounded managed code inside the frame

**This is the answer to "why does developer code affect how a page renders".**

`AppHost`'s frame is a single synchronous pipeline on the UI thread, and app code is a phase inside it:

| Line (`src/FluentGpu.Engine/Hosting/AppHost.cs`) | Phase | Bounded? |
|---|---|---|
| `:3236` | `_runtime.Flush()` — component renders, computeds, bindings | **no budget** |
| `:3240` | `_reconciler.ReRealizeVirtuals(_frameBudget.DeadlineTicks)` | budget-bounded |
| `:3353` | `_anim.Tick(dtMs)` — phase 7, **on the UI thread** | downstream of the flush |
| `:3441` | `_images.Pump(_frameBudget.DeadlineTicks)` | budget-bounded |

The asymmetry is the defect: **the engine budgets its own work and does not budget the app's.** A component whose
`Render()` or memo goes O(n) *is* the frame — nothing interrupts it, defers it, or runs it at lower priority.

The render thread cannot cover for it either. `src/FluentGpu.Engine/Hosting/Threading/RenderFrame.cs` is the
**Cut A (submit-only)** carrier: the UI thread records the DrawList into an arena and the render thread submits and
presents it. There is no independent producer that can re-present the last good scene, and because `_anim.Tick` sits
after the flush on the same thread, a long flush freezes **animation as well as content**. That is why a data-layer
inefficiency reads to the user as the whole window hanging rather than one row updating late.

**The fix for this was designed and never built.** `ReconcileSlicer` and `RenderPriorityPolicy` — the canon's
time-sliced reconcile (`docs/design/subsystems/threading-render-seam.md` §12.1) — appear in `docs/design/*` and in
**zero files under `src/`** (verified 2026-09-07). The frame-budget primitive already exists and is used at two call
sites; it was simply never extended to the app-facing half of the flush. A related engine design already written and
awaiting a decision: `..\fluent-gpu\docs\plans\render-thread-animation-design.md` (787 lines).

**Consequence for how the two repos split this work.** Findings 2–4 remove the wasted work; finding 1 removes the
*blast radius*. Fixing only the seam leaves the next O(n) recompute free to hang the window; fixing only the engine
turns the hang into a late update while the waste remains. Both are wanted, and finding 1 is the one that makes the
platform's promise true — *a page author must not be able to freeze the compositor from managed code, and today they
can.*

---

## Finding 2 — a publication invalidates the whole list (per-publication cost is O(total rows))

The worst frame in the entire investigation — first open of the 1,494-track playlist:

```
frameMs=824.6  flush=721.2  reactive=721.1  realize=0.0  layout=1.3  layoutSolve=1.2
record=1.0  imagePump=0.0  submit=0.0  fenceWait=0.0  present=0.0  gpu=25.7
comps=46  nodes=743  draw=157  cmds=557
```

The engine visited **743 nodes** and rendered **46 components**, and it took 721 ms. Tree size cannot explain it:
layout + record + submit + present total 3.5 ms. The chain, verified in this tree:

1. `QueryService.Recompute` (`Backend/Queries/QueryService.cs:312-402`) builds a **fresh `Resources` dictionary on
   every recompute** (`read.Resources.ToDictionary(...)`) and publishes whenever any entry differs — including a
   pure activity transition (`Queued → Fetching → Present`), which `CatalogRepository` publishes **per key**
   (`Backend/Catalog/CatalogRepository.cs:249, 272`).
2. `DetailTracks.View(snapshot)` (`Features/Detail/DetailTracks.cs:435-441`) caches the sorted/filtered index array
   with a key that includes `snapshot.Resources` **by reference**. A new dictionary every publication ⇒ cache miss ⇒
   **a full sort + filter over all 1,494 rows on the UI thread**, inside the unbounded flush of finding 1.

Note what is *not* at fault: the backend projection is already doing the right thing — `ChunkedRows` structural
sharing keeps unchanged row objects reference-stable (`Backend/Queries/CatalogReadView.cs:122-133`,
`CatalogQueryDefinitions.cs:354`), and the engine compares by value what it is handed.

**What is missing is a platform primitive for "these 1,494 rows are the same except three."** Because none exists,
every page hand-rolls its own gate — `ChunkedRows`, `Memo<TrackRowsSnapshot>`, `ShelfProps.Equals`,
`QuerySignalBinding`'s pool projection, `_viewKey`/`_viewResources`. Where one gate is keyed on something that
changes every publication, the whole list recomputes and nothing tells the developer it happened.

**Scaling, measured** (worst frame per page):

| Page | rows | worst frame | dominant phase |
|---|---|---|---|
| album `6FBl…` | 12 | 6–13 ms | — |
| album `5Qg2…` | 31 | 21–27 ms | reactive |
| playlist `37i9…` | 50 | 31–42 ms | reactive |
| Liked | 327 | 159–185 ms | reactive 42 + realize 29 + unaccounted 71 |
| **Q-top 1500** | **1,494** | **824.6 ms** (stalls 461, 327, 250, 144) | **reactive 721, layout 1.3** |

Warm re-open of the same 1,494-row playlist: `first=21.6 ms, slow33=0, stall100=0, worst=31.0 ms` — because the
publications have stopped.

**Two candidate contracts** (either removes the class):

- **Status out of the value.** Publish per-row resource status on its own channel, or keep the `Resources` map
  identity-stable when no entry changed, so a fetch-state transition never invalidates a page's row pipeline.
- **Diffable collections.** A row-collection type carrying `(revision, changed indices)` plus an engine-side binding
  that consumes it, so "3 of 1,494 changed" is expressible end to end and a page written the simple way — read the
  model, pass it — is cheap by construction.

**Secondary observation on the same page.** After 12 s it still reported `rows=1494 gaps=1140 unknown=1134
queued=0 fetching=0`: 1,134 rows Unknown with nobody fetching. That is the window + `IdentityLookahead = 300`
(`CatalogQueryDefinitions.cs:78`) working as designed, but it means the page carries ~1,494 row dependencies in every
snapshot while only ~350 are ever asked for. The dependency set, the demand set and the rendered set are three
different sizes and only the smallest is bounded by the viewport.

---

## Finding 3 — `HasPrimaryData` means "identity", so the page goes Ready empty ("0 data, then it flashes")

The third argument of `QueryReadResult` is the page's "I have primary data" flag:

| Query | Line (`Backend/Queries/CatalogQueryDefinitions.cs`) | Predicate | Verdict |
|---|---|---|---|
| Playlist | `:158` | `value.MembershipLoaded \|\| header is not null` | membership-aware — correct |
| Show | `:329` | identity, but passes `_episodes.Loaded ? Items : null` | distinguishes unknown from empty |
| **Album** | `:187` | `identity.Value is not null` | **Ready with zero tracks** |
| **Artist** | `:244` | `identity.Value is not null` | **Ready with zero sections** |

An artist or album opened from a track row already has its identity resident — the row's `TrackIdentity` carries the
album and artist references and `RowKeys` demands those identity facets. So on click the query is instantly Ready,
`ArtistPage.Body` evaluates `if (popular.Count > 0)`, `if (albums.Length > 0 || a.AlbumsTotal > 0)` … — **all
false** — and paints a hero with no body. One round trip later the overview lands and all 14 sections appear at once.

`Artist.TopTracks` / `TopAlbums` / `AppearsOn` are already nullable and `ArtistPage` already writes
`a.TopTracks ?? Array.Empty<Track>()`, so the model *can* express "unknown" — but `ArtistDefinition.Read:236-240`
always materialises an array, making unknown indistinguishable from authoritatively empty. The `Skel.Region` in
`ArtistPage.cs` is built for exactly one skeleton→content transition; the identity-only gate splits it into two.

**Measured empty→content window** (run D, fast network, warm process):

| Page kind | first frame | content arrives |
|---|---|---|
| never-opened artist ×8 | 4.1–5.7 ms | **61–154 ms later** |
| never-opened album ×8 | 2.6–9.2 ms | **44–88 ms later** |
| startup's restored page | 437–473 ms | **2,774 ms later** |

**Caveat for whoever fixes this.** `RelationProjection.Read` returns early at `RelationProjection.cs:79`
(`if (snapshot is null) return;`), so `Loaded` stays false when the relation resource is *absent*, not only when it
is unknown. Gating the page on `Loaded` alone would strand an artist with no chart in a permanent skeleton. The gate
must be "resolved" = loaded **or** authoritatively absent/unsupported.

---

## Finding 4 — a page renders a fact it never demands (the missing play counts)

**Symptom.** The Weeknd's Top tracks: titles, artwork and durations present, **no play counts on any row**. On some
artists one or two rows show counts.

**Mechanism.**

- `Features/Detail/ArtistPage.cs:86-88` and `:99-104` both build `new QueryDemand(..., windows, [])` — the
  `OptionalFacets` list is **empty**.
- `ArtistDefinition.Requirements` (`CatalogQueryDefinitions.cs:246-264`) reaches per-row facets only through
  `RowKeys` → `foreach (var facet in demand.OptionalFacets)` (`:54-56`). Empty in ⇒ no `PlayCount` key out.
- `ArtistPopular.cs:353, 367-370` draws the "N plays" line only when `t.PlayCount > 0`, so an absent fact silently
  deletes the row's subtitle instead of showing a pending state.
- `CatalogReadView.Fact` reads with `cold: false` (`Backend/Queries/CatalogReadView.cs:43-44`), so a `PlayCount`
  already persisted in `catalog_resource` is **not** cold-read back unless it is a *requirement*. A count therefore
  survives only as an in-memory `ArtistOverview` inline seed (capped at 10 rows), and `ArtistOverview` is
  Provider-fresh for 12 h — so a restart inside that window never re-seeds it and every row goes blank. That is the
  "sometimes there, sometimes not".
- Log evidence from this tree: `hydration.tracks.gaps … n=50 … playcount=37 tempo=42
  reasons="playcount=trait_not_asked tempo=trait_not_asked"`.

**Scope.** `Features/Detail/DetailTracks.cs:326-328` is the **only** place in the app that populates
`OptionalFacets`. Passing `[]`: `ArtistPage.cs:88, 104`, `SidebarArtistTopTracksSource.cs:75`, `HomePage.cs:846`,
`HomeSectionPage.cs:172, 245`, `NowPlayingPanel.cs:50`, `RecentsEntityQueries.cs:32`, `LikedCoverArt.cs:78`,
`AlbumRelatedSections.cs:31, 71`.

**Contract to restore.** A surface that renders a fact declares it, next to the column/row definition that draws it
so the two cannot drift; and a row whose demanded fact has not arrived renders a pending affordance, never a
silently collapsed line.

---

## Finding 5 — startup runs the whole query graph before the session exists

One launch (`pid=47484`, times relative to the `[app] startup` line):

| t | event |
|---|---|
| 0 ms | `[app] startup` |
| 212–283 ms | catalog + replicas ready; `Services created` (database open 16 ms) |
| 347 ms | D3D12 adapter |
| **1,060 ms** | `Online; shell up from cache` — **the whole query graph mounts and plans demand** |
| 1,060–1,600 ms | `SidebarLibraryQuery` runs **8 demand waves with `keys=0` in 40 ms**; `PlaylistDetailQuery`, `LikedSongsQuery`, `EntityCardQuery` all return `deferred=1 offline=1`; `replica.demand.deferred — no protocol session installed` repeats |
| 1,117 ms | first `frame.stall` (364.9 ms) |
| 1,248–1,603 ms | AP connect, login, client-token, access token, dealer hosts |
| **2,051 ms** | `protocol-installed` — only now can that demand be served, so the graph re-plans and re-publishes |

**Counted before the session exists: 65 demand waves, 51 replica deferrals.** A second launch: 1,598 ms, 40 waves,
26 deferrals. The startup page then carries 16–21 slow frames and 9–12 stalls ≥100 ms in its first four seconds, and
its rows take **2,774 ms** to settle versus 31–434 ms for every later navigation.

None of this is fetch cost. It is the graph running twice, the first time against a session that cannot answer.

---

## Finding 6 — the gauge cannot see any of the above

`App/HydrationGaps.cs:22-27` counts a row as a gap only when its **title** is missing. Facts (`PlayCount`, tempo,
descriptors) are invisible to it, so `hydration.settled` fires on a page whose plays column is entirely blank. The
separate `hydration.tracks.gaps` probe *does* report `playcount=…  reason=trait_not_asked`, but nothing fails on it.
Any acceptance run based on the `hydration.*` lines will report success while finding 4 is fully present.

---

## What is *not* broken

- **Route-change machinery** — first frame 2.6–15 ms warm, never-cached and never-opened alike.
- **Fetch throughput** — never-opened artists and albums resolve in 44–154 ms; titles settle in 31–434 ms.
- **Virtualization, layout, GPU** — on the worst frame measured: layout 1.3 ms, record 1.0 ms, submit 0, present 0,
  743 nodes, 157 draw nodes.
- **Rapid navigation** — 12 navigations at 600 ms dwell, interrupting loads mid-flight, produced no stalls.
- **Parked-page accumulation** — `KeepAlive MaxEntries: 8` (`Features/Shell/ContentHost.cs:87-90`) is bounded, and
  `QueryService.OnCatalogChange` skips nodes with no active lease.
- **The backend row projection** — `ChunkedRows` structural sharing is in place for playlist, album and Liked rows.

---

## Suggested order of work

| # | Work | Repo | Why this order |
|---|---|---|---|
| 1 | **Seam fix** — status out of the published value, or an identity-stable `Resources` map | app | Removes the O(rows) work; unblocks big lists immediately |
| 2 | **Primary-data predicate** — `identity && primary-relation-resolved` (resolved covering authoritative absence), or carry `null` for unknown collections and render section skeletons | app | Removes the empty→flash on every artist and album |
| 3 | **Demanded-facet contract** — bind "renders a fact" to "demands the facet" at one place per surface | app | Fixes the play counts and the same class on every other surface |
| 4 | **Startup gating** — hold query demand until `protocol-installed`, then plan once | app | Halves perceived launch time |
| 5 | **Fix the gauge** — count demanded facts, not just titles | app | Do this before signing anything off |
| 6 | **Time-sliced reconcile + independent present/animation** (`ReconcileSlicer`, `RenderPriorityPolicy`; see `render-thread-animation-design.md`) | engine | The structural fix: makes 1–5 safety margin rather than the only thing standing between a page and a frozen window |

**Tests that would hold these** (no source-text tests — extract the decision and unit-test it, per `CLAUDE.md`):

- A `CatalogQueryTestHost` case asserting a chart row's `PlayCount` is populated from the page's own demand.
- A case asserting a query with resident identity and an unresolved primary relation reports
  `HasPrimaryData == false`, while an authoritatively absent relation reports `true`.
- A publication-cost case asserting an activity-only transition does not change the published row collection's
  identity (this is the regression guard for finding 2).
- A startup case asserting no demand wave is planned before the protocol session is installed.

---

## Appendix A — still open from `library-v3-1-findings-2026-09-06.md`

Not re-verified today; a spot check of the tree suggests all remain unfixed: seek during a deferred load plays the
previous track; an offline like is silently lost; one unreachable playlist aborts the whole outbox drain; rootlist
members are never pinned (a 30-day cache time bomb); one corrupt row poisons every read touching its key.

## Appendix B — reproduction

```powershell
# 1. Release build must exist
dotnet build Wavee.slnx -c Release

# 2. Drive the running app with no process spawn per navigation:
#    FindWindowW("FluentGpuWindow", null) + WM_COPYDATA, dwData = 0x46474143, payload = the wavee:// string.
#    Working scripts from this session: <scratchpad>/nav-stress.ps1, nav-stress2.ps1, cold-nav.ps1
#    (nav-stress2.ps1 = big playlist + never-opened artists/albums + rapid interleave)

# 3. Pick genuinely never-opened targets from the app's own cache (read-only):
sqlite3 "file:$env:LOCALAPPDATA\Wavee\library.db?mode=ro" `
  "select subject from catalog_resource where facet=4 and subject not in
   (select subject from catalog_resource where facet=21) order by random() limit 8;"

# 4. Read the result: nav.frames / frame.stall / hydration.* / catalog.demand.wave in
#    %LOCALAPPDATA%\Wavee\logs\wavee-<date>.log, filtered by the run's pid.
```

Engine anchors cited above were read at `..\fluent-gpu` on 2026-09-07 (47 uncommitted files in that tree):
`AppHost.cs:3236, 3240, 3353, 3441`, `Hosting/Threading/RenderFrame.cs` (Cut A header),
`grep -rn 'ReconcileSlicer\|RenderPriorityPolicy' src` → no matches.
