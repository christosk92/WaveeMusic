# Navigation/hydration live retest — 2026-09-08

Status: the publication scaling defect and the artist reveal boundary are fixed and re-measured (see the last
section); the transactional mount/update work is still unfinished.

## Binary and reproduction

- Launched the user's requested real Release app, PID 24200, from
  `src/apps/Wavee.Tests/bin/nav-hydration/Release/net10.0/Wavee.exe`.
- App DLL timestamp at launch: 00:33:12 local; engine DLL: 00:30:45. Launch: 00:33:30.
- No cache reset, environment switches, playlist edits or playback commands used by the agent.
- User exercised artists and playlists and reported Q-top 1500 scrolling as severely slow.
- Added `ops/tools/nav-live-check.ps1` to send navigation-only WM_COPYDATA to an exact process, without launching
  additional executables or terminating the app. The initial helper mistakenly included a trailing NUL in cbData;
  those malformed-route measurements are INVALID and excluded. Fixed to the production sender's exact byte count.
- User interaction and scripted navigation overlap in portions of this run; do not describe it as a controlled A/B.

## Plain Release observations before profiling

Always-on log: `%LOCALAPPDATA%/Wavee/logs/wavee-20260908.log`, PID 24200.

| Evidence | Observation |
|---|---|
| seq 181, artist `4rQTEdG6hDVOlDUFKs9EjZ` | 130.1 ms frame, 119.5 ms flush, 118.8 ms reactive work |
| seq 907/1004, playlist `2pnt79m93NytfAj2lByLlQ` | 122.5 ms worst frame, 112.6 ms reactive work; 10 frames above 33.4 ms in the initial four-second window |
| seq 123/160, two other playlist updates | 67.2/89.2 ms worst frames, predominantly reactive work |

These are UI work/navigation windows, not measured display FPS or scroll-active percentiles. The current frame
diagnostic also subtracts asynchronous fence measurements from a UI total, producing negative unaccounted time;
that residual must not be used as an accurate phase attribution.

## Sampled-thread trace: expensive scene publication

Captured 30 seconds from the existing PID with `dotnet-trace --profile dotnet-sampled-thread-time`, output under
ignored `src/apps/Wavee.Tests/bin/nav-hydration/scroll-24200.{nettrace,speedscope.json}`. This profile adds overhead;
it identifies stacks, not ordinary scroll cadence. The interval includes user activity and a later navigation,
so it is not an isolated Q-top benchmark. Trace is local only, not uploaded.

The UI thread (OS TID 10224) prominently samples:

- `SceneRecordingSnapshot.Capture`, including repeated scene-column copies and sparse lookups;
- `RecordingScrollBinds.CaptureChain`;
- `SceneRecordingSnapshot.RetainStrings`;
- memory copy/clear and GC polling.

In this profiled interval Capture has about 10.55 seconds of inclusive CPU-tagged sampled wall time, versus about
0.082 seconds under ReactiveRuntime.Flush. These are profiler classifications, not precise CPU-cycle timings;
Capture's inclusive samples include GC-poll time. Global top-N percentages are unsuitable because they include
all waiting worker threads. Render-thread work was analyzed separately.

Code confirms a scaling defect in the new implementation: `SceneStore.RecordingNodeCount` returns `_high`;
`SceneRecordingSnapshot.Capture` loops across that entire node high-water range on every publication, copying
columns and probing side tables even for parked page nodes. Dead slots are repeatedly cleared. `RetainStrings`
then scans the range again. Moving recording to a separate thread did not remove this UI publication cost.

Next implementation needs immutable snapshot reuse/change tracking with correct per-slot generations and resource
pins, avoiding repeated unchanged-node/parked-page copying. A warmed, multi-page-retained scroll benchmark must
cover snapshot preparation, not just recorder allocations or standalone 10k track sorting.

## Artist skeleton failure

The user's paired Gaston screenshots show a partially populated hero/chart followed by changed hero geometry,
artwork, biography and completed rows. `ArtistPage` currently passes an unconditional `SetReady(value)` callback
to QueryHooks. Every non-seed publication, including partial cached data, releases the whole-page skeleton.
Individual row placeholders then remain visible. This is an incorrectly chosen developer readiness boundary,
not a reason to reintroduce framework completeness flags.

Required behavior remains one developer-owned initial-load completion, one whole-page reveal, and no Ready →
Pending reset for ordinary background refresh. Existing KeepAlive exit overlap and post-present query setup also
need a mounted/visual transition check before attributing every reported old-data flash to the same mechanism.

## Unfinished verification

No performance fix was applied during this diagnostic retest. App remains running for the user. Previous green
unit/headless tests do not establish live scroll performance, initial-load visual correctness or full transactional
mount/update. These issues remain open.

## Fix and re-measurement — later on 2026-09-08

### Scene publication copies the reachable tree, not the slot high-water mark

`SceneRecordingSnapshot.Capture` (sibling engine) now walks reachability from the scene root, the exit orphans,
the connected-animation overlays, the drag visuals and the popup subtree roots the render frame passes, plus each
root's ancestor chain (so absolute rects and reuse-block chains resolve exactly as a full copy did). Parked pages
and parked virtual rows are detached from that topology, so their slots cost nothing per publication. A slot the
previous capture reached and this one did not has its handle and topology row cleared, so nothing can chain through
it; `IsLive` is unchanged. Side-table probes are gated on the same flags the store's free path uses (`Scrollable`,
`InteractionAnim`, `SparsePaint`, `VisualKind.Text`). `RetainStrings` walks the captured list. `CopyRecordingHandles`
is deleted. Headless gates: `gate.scene-snapshot-parked`, `-parked-cost` (20,000 parked rows: 23 ticks parked vs
54,393 ticks attached), `-roots`; the existing isolation, generation, span/string lifetime and zero-allocation gates
still pass. Engine Debug + Release build clean; the Debug VerticalSlice passes 1340/1340; the Release slice showed
one different single-check failure on two consecutive runs (`w1controls.8` down-arrow, then the UseContext check)
and passed on the next four runs of the same suites — a pre-existing timing flake in checks unrelated to capture.

### Artist page reveals once, when its initial demand has resolved

`ArtistPageReadiness` (Wavee.Core, pure, unit-tested) is the page's own boundary: every non-optional resource the
query is *demanding* must be resolved (Present, Absent, Unsupported, Offline, or an error) and the artist's identity
and overview facts must be among them; play counts and video flags never hold the page. The first cut waited on
every key in `QuerySnapshot.Resources`, which also lists keys merely READ while joining the value (a related
artist's identity, a popular-release album outside any demanded window); nothing fetches those until demand reaches
them, so the League of Legends artist page stayed on its skeleton for good (PID 51908). `QuerySnapshot.Demanded`
now publishes the query's active requirement keys and the rule iterates that; the demand waves in the log show every
demanded key reaching a terminal state within ~330 ms of the open. Second correction from the cold-start run (PID
14196): before the session installs, the keys are read under the pre-login scope and the transport marks them
`offline`, which the rule took as terminal, so a restored artist page revealed at 1.4 s with an empty chart and filled
in as background data. Offline now resolves only under a known catalog context, and a superseded snapshot never
reveals. `QueryHooks` logs the one reveal (`[ui] page.reveal spec=… demanded=… sinceNavMs=…`) so a log shows when a
page lifted its skeleton, or that it never did. `QueryHooks.Use/UseMapped`
take an optional `initialLoad` predicate: until it holds, a publication leaves the pending shape untouched; once
Ready, every later publication lands as background data (no Ready → Pending reset). Only `ArtistPage` opts in.

### Detail pages reveal once too

The user does not want partial lists: a playlist that revealed on membership painted each row as its identity landed
(blank placeholder bands for a few frames, then rows filling in out of order). The shared rule now lives in
`PageReadiness` (Wavee.Core): every demanded key except the per-field presentation facets (play count, audio
attributes, video, descriptors, availability, publishing, visual identity) must be resolved; `DetailPageReadiness`
applies exactly that to playlist, album, liked and show pages in `DetailPage.Publish`, and `ArtistPageReadiness`
adds the artist's identity + overview facts on top. The list therefore shows only once the header, the membership
page and every row identity in the initial window (rows 0–50, plus their album/artist identities) have a terminal
answer, then later windows load as background data. `page.reveal` is logged for these pages as well.

### Playlist rows read facts from their own publication

"Track details unavailable" flashing on rows that filled in a few frames later was not a fetch failure. The
hydration diagnostic counted those fields as failed because the identity fact was Present while the row's track had
no title: the binding publishes new facts and the new model in one flush, but the rows land from the background
projection later, and each row read the binding's live resources signal. `TrackRowsSnapshot` now carries the
resources its rows were projected against; the row title state, the plays column state and the hydration diagnostic
read that. Two further hardenings: a failed cold read of the local cache on a still-Unknown key stays Loading rather
than Unavailable (`TrackMetadataReadiness`, tested), and that failure is now logged (`catalog.cold-read.failed`).

### Live re-measurement (Release, JIT, this checkout's `src/apps/Wavee/bin/Release`)

Driven by deep links after a cold start; same always-on `nav.frames` lines as above. No cache reset.

| Route | Before (PID 24200) | After (PID 4868) |
|---|---|---|
| playlist `2pnt79m93NytfAj2lByLlQ` (1494 tracks), cold | 122.5 ms worst, 10 frames > 33 ms | 53.7 ms worst, 1 frame > 33 ms |
| same playlist, warm re-open | — | 40.6 ms worst, 1 frame > 33 ms |
| artist `4rQTEdG6hDVOlDUFKs9EjZ`, cold | 130.1 ms worst | 16.7 ms worst, 0 frames > 33 ms |
| artist `4rQTEdG6hDVOlDUFKs9EjZ`, warm | — | 6.5 ms worst |
| artist `1Hsdzj7Dlq2I7tHP7501T4`, `3eVa5w3URK5duf6eyVDbu9` | — | 6.7 / 5.8 ms worst |

The remaining worst frames on the playlist route are reactive flush (component re-renders on publication), not
publication copy; the start-up first navigation still pays a 200–300 ms layout-heavy first frame. Scroll-active
percentiles were not measured (the harness sends navigation only); the mechanism is covered by the parked-cost gate.

## Regression sweep against main — same day, later

A three-way research pass (engine seam, playlist query pipeline, sidebar/artist/startup) traced the remaining
stalls to the uncommitted data-layer rewrite and to specific engine-diff defects; the plan and evidence live in
the session plan file and the mechanisms are summarised here because they explain the numbers above.

**Engine.** The keyed-child reconcile plan sampled the clock per child and re-walked the sibling chain per
commit (now: no clock read on the non-budgeted path, validation under diagnostics only); the budgeted reactive
flush let `_flushGuard` survive yields and starved the post-realize rebind flushes (now: guard per flush, rebind
flushes run unbudgeted); any skipped scene publication disabled span reuse and forced full-screen repaint (now:
record-dirty bits and removals are stamped with their publication and cleared only once the renderer adopted it,
repaint damage carries across skipped publications, byte-identical frames are not resubmitted); popup bounds and
animate no longer park the render thread; image capture copies only referenced ids; string retention and
compositor capture are incremental; ~30 `Responsive.Of` lambda call sites use the gated overload.

**App pipeline.** Every row crossed while scrolling re-joined all 1494 members under the catalog lock the render
path also took from every row; every activity transition republished a fresh `Resources` map so every consumer's
identity gate failed; the sidebar rebuilt itself on every catalog publication. Now: `SetDemand` re-plans demand
without reading; activity-only churn publishes nothing unless status moves; unchanged reads return the same
playlist/album/show/library instances; requirements are bounded by the window (max(50, 2×window) rows ahead);
rows read facts from their own publication and `TrackRow` takes `hasVideo` as a value (the catalog keeps a
lock-free published view for the remaining probes); deliveries coalesce at 50 ms leading/trailing; the sidebar
rebuild is staged and its demand effect keyed; artist shelves own their windows and merge them into one demand.

| Route | Before (PID 24200) | Engine phase | App phase (PID 9268) |
|---|---|---|---|
| playlist `2pnt79m93NytfAj2lByLlQ` (1494), cold | 122.5 ms worst, 10 > 33 ms | 53.7 ms, 1 > 33 ms | 10.6 ms, 0 > 33 ms |
| same, warm | — | 40.6 ms | 7.5 ms |
| playlist `37i9dQZF1EIdh6MgVIhb8B` | 146.4 ms | 105.4 ms | 17.3 ms |
| artist `4rQTEdG6hDVOlDUFKs9EjZ`, cold | 130.1 ms | 41.0 ms | 54.5 ms (reveal frame) |
| artist `1Hsdzj7Dlq2I7tHP7501T4` | — | 225.7 ms | 37.0 ms |
| artist `47mIJdHORyRerp4os813jD` | never revealed | 35.6 ms | 41.5 ms |
| publications on the 1494 playlist in its first 2 s | ~89 in 4 s | — | ~13 |

Still open after these phases: the artist reveal frame (the whole magazine body mounts in one flush, 37–55 ms), the
start-up restore of a page (270–420 ms first frame), and the launch-from-cache work (the next phase). A
`scroll.frames` line now reports scroll bursts (≥5 frames stamped ScrollActive); synthetic wheel/keyboard input does
not drive the app, so that number comes from a real scroll.

## Scroll smoothness — same day, evening

The user's own `scroll.frames` bursts on the 1494-track playlist ran at 112–123 fps with 1–3 ms averages but 7–22 ms
worst frames on row recycles, which is still a dropped vblank at 120 Hz. The CPU sampling trace was startup-dominated
and useless for this, so the attribution came from three in-process counters (see the `scroll-probe-and-census-tooling`
memory): the `WAVEE_TRACKLIST_SCROLL_PROBE` frame CSV, a `GCAllocationTick` sampler by type and thread (the verify build
needs `-p:EventSourceSupport=true`), and the engine render census extended with a render/reconcile split and the scene
nodes touched (`FG_RENDER_CENSUS=1 FG_RENDER_CENSUS_MIN=8`).

**What the fake 1500-track probe showed before the fixes** (Release JIT, vsync suppressed, 280 painted frames):

| Symptom | Measured |
|---|---|
| pool-thread churn per row crossed | 47 MB `Entry[ResourceKey,ResourceSnapshot][]`, 8.7 MB `ResourceSnapshot`, 4 MB `Int32[]` in ~2 s; 1–4 MB process allocation per recycle frame |
| GC pauses landing on the UI thread | 6 gen0 / 5 gen1 / 2 gen2 in 280 frames; 4 of the 6 frames over 8.33 ms were GC frames (8–16 ms, attributed to whatever phase the pause hit — twice `layout`) |
| a wheel notch (5 rows recycled) | `BoundRowContent×5 (r=0.2–0.5 c=1.0–1.9 ms a=237K)`, `ExpandableRowSlot×5 (r=0.2 c=0.3–0.5 a=86K)`, `RowOrRecContent×5 (14K)` — three components per row, 70 KB per row, 2.2–4.4 ms flush + ~1.5 ms layout + ~1 ms submit |
| facts side panel settling mid-scroll | one 27–33 ms frame: `ToolTip×39, ArtistPortrait×5–8, TempoCard, DensityPlot, LikedArtistsCard, LikedBlendCard` |
| page reveal | 41 ms: `ItemsView×3`, 30 rows × 3 components, sidebar slots, tooltips |

**Mechanisms and fixes.**

- `Node.TryReplan` fell back to a full `Read` of all 1494 members whenever a required key was one the last join never
  observed — which is every row crossed once the viewport passes the initial lookahead. Now an unobserved key the
  repository does not know is collected into `_awaited` (the node depends on it) and demand is planned for it; only the
  catalog change that lands it re-joins, and an unobserved key the repository already knows still re-joins at once.
  A full pass also allocates far less: the read context is presized from the previous join, `Peek` hands back one
  cached Unknown snapshot per never-fetched key, and the join publishes its own observed map when no error overlay
  differs instead of copying it. Tests: `QueryServiceCostTests` (ten moves onto unfetched rows keep `Reads` bounded;
  seeded-but-unobserved rows still re-join; `Peek` returns the same Unknown instance).
- `RowOrRecContent` and `ExpandableRowSlot` read the slot index, the item and the expanded-row signal directly, so a
  recycle rebuilt the whole bound row skin (ten `Prop.Of` closures, a drop target, the swipe wrapper) per row. Both now
  gate on one equality-checked computed (branch kind; open-drawer row key) and a recycle re-renders `BoundRowContent`
  alone. `VerticalItemContent` got the same treatment.
- `LikedFactsPanel` pushed the raw track list into `TempoCard`/`LikedBlendCard` props (re-render per publication) and a
  fresh `Ranked` list into `LikedArtistsCard` (re-render of every portrait and tooltip when the summary settled). The
  list is now a mount-stable holder, the card props compare content, and `ArtistPortrait.Props` compares the seed by
  uri/name/image. Tests: `LikedFactsPanelPropsTests`.
- Engine: the DirectWrite shape cache held 256 runs — less than one screen of rows — so scroll-back re-shaped every
  string; now 2048 with batched eviction. The census reports `r=`/`c=`/`a=` per type and `nodes(upd wr plans mounts)`.

- The node's dependency set was a second 10k-entry `HashSet` rebuilt per full pass — a large-object-heap allocation
  per arrival wave, and LOH churn is what schedules gen2 collections mid-scroll. The dependency set is now the pass's
  own resource map (its key set is exactly what the join observed).

**After (same probe, Release JIT, 280 painted frames):**

| | before | after |
|---|---|---|
| frames over 8.33 ms | 6 (2 work, 4 GC) | 3 (3 work, 0 GC) — all three are the facts panel's first mount landing mid-scroll (JIT-inflated here: `TempoCard` 2.2 ms, `LikedBlendCard` 2.8 ms renders) |
| collections over the run | gen0 6 / gen1 5 / gen2 2 | gen0 2 / gen1 1 / gen2 1 |
| allocation per frame (mean) | 250–286 KB | 85 KB |
| pool-thread allocation in the drives | 64.5 MB (47 MB dictionary entries, 8.7 MB snapshots) | 17.9 MB (12.8 MB dictionary entries — one presized map per arrival wave, which the fake source answers per notch) |
| a wheel notch (5 rows recycled) | 15 components, 2.2–7.9 ms flush, 352 KB UI, frame 5–17 ms | 5 components (`BoundRowContent` only), 1.3–1.9 ms flush, ~250 KB UI, frame 4.3–5.5 ms, none over budget |
| p50 / p95 | 1.45 / 5.5 ms | 1.32 / 4.5 ms |

What a recycle frame still costs (5 rows): reactive 1.4 ms (render 0.05 + reconcile ~0.25 ms per row, ~60 elements),
layout 1.2–2.0 ms (about 1900 measures and ~100 text-measure misses per frame — text shaped twice per new string,
intrinsic then constrained), record 0.5–0.9 ms, submit ~1 ms. The next cut, if a real 120 Hz scroll still drops
frames, is the per-element reconcile and the double text measure — both engine work.

**Gates.** VerticalSlice Release 1352/1352; Engine.Tests 233 (two load flakes pass alone); Wavee.Tests 7551 (three load
flakes pass alone; the suites had been run concurrently); Release solution build clean. The user's own
`scroll.frames … presented= missedVblanks=` numbers need a relaunch of the arm64 publish with these changes.

