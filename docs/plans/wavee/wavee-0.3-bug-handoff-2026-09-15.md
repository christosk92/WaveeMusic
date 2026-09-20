# Wavee 0.3 — bug handoff, 2026-09-15 (round 2)

> **Status of the tree this document describes.** Branch `feat/0.3-structure` at `771fce00`, with **177 modified
> and ~200 untracked files, all uncommitted** (69k insertions). The last full gate on this exact tree: Debug and
> Release build clean, `Wavee.Tests` **8,939 passed / 0 failed / 1 skipped**, arm64 NativeAOT publish with 0 IL
> warnings. Nothing has been committed. Six new bugs were then found by the user in that build; **four of them are
> regressions introduced by that same day's work.** This document is the complete state of the investigation into
> those six; no code has been written for any of them.

## How to use this document

Sections are ordered: what the user said (verbatim) → the traps → each bug's measured evidence, root cause,
confidence, and what is still unverified → the measurement plan the user asked for → a paste-able prompt for the
next session. **Read "Traps" before writing any code** — three fixes on 2026-09-15 were wrong in ways the test
suite actively blessed, and the same traps are waiting for the next person.

Related documents (all in this repo unless noted):
- `docs/plans/wavee/scroll-feel-investigation-2026-09-10.md` — the prior scroll investigation, **with real
  measurements**. Its ranked cause #4 is the "ignored flick". Read before touching scroll.
- `docs/plans/wavee-scroll-feel-diagnostics-plan.md` — the ScrollTrace plan. **Partly rotted**: it cites
  `ScrollIntegrator.cs`, `ScrollTuning.cs` and `InputDispatcher.cs:2938/3099/3521`, which no longer exist in the
  pinned engine (see §8.5).
- `docs/plans/wavee/scroll-cover-jank-2026-09-15.md` — **untracked, written by a different session the same day**,
  findings complete, no code landed. Covers cover tiles flashing empty on the Weak/UMA tier during scroll. Related
  to bug E's "feels heavy" but a separate mechanism (GPU image budget, decode stall). Not folded into this document.
- `docs/plans/wavee/wavee-0.3-gap-register.md` — G-007 (persistence) was re-opened and re-closed today with an
  honest description; G-254 and G-259 were corrected from open to fixed.

Engine: the app builds against `C:\WAVEE\fluent-gpu-pin` at `ba24aac6d`. **Engine fixes land in
`C:\WAVEE\fluent-gpu-combined` on `feat/0.3-engine-combined`, then the pin moves. Never edit the pin in place.**

---

## 1. What the user reported, verbatim

1. *"anaylze this mp4, there are a lot of bugs iwth navigating to artist.. why is it not shimmering fully? also
   herel ook [3 sidebar screenshots] its not fully loading everything here until i click on it"*
   (`C:\Users\ChristosKarapasias\Videos\buggggg.mp4`)
2. *"also check logs, scrolling is still slow fps sometimes, especially for some reason in the left navrail (?)?"*
3. *"look how bad the left nav rail here scrolls"* (`C:\Users\ChristosKarapasias\Videos\bad_scroll_left.mp4`)
4. *"other bug: this playlist is missing artist/album data? … but not every track (?)"* (screenshots of
   "My Playlist #9": rows 9 and 10 show "Mokita · London" / "LP · Lost On You"; rows 1-8 and 11 show the title alone)
5. *"this is loading for like 5 seconds until it suddenly gets resolved. another regression"* (Doja Cat →
   "Singles & EPs" tab, 8 shimmer cards for ~5 s)
6. *"in general, scrolling also just feels a bit like not responsive? this is not an fps issue, but rather on the
   input side. sometimes my flick gets ignored and no scroll momentum, it just stops. the initial scroll lift feels
   heavy, but is smooth when its eventually scrolling"*
7. *"perhaps we can launch a quick measurement of scroll, from the moment of input until computation and actual
   scroll effect, measure the whole pipeline, then also somehow compare it to WinUI apps or other stuff outside of
   fluent-gpu?"*

### Evidence captured
- Video frames were extracted to a session scratchpad at 4-5 fps. **Those are gone**; re-extract with
  `ffmpeg -i <mp4> -vf fps=4 out%02d.png` if needed. What the frames showed is recorded under each bug.
- Log: `%LOCALAPPDATA%\Wavee\logs\wavee-20260915.log`. **Current-build sessions are `sid=0f32b52c` and
  `sid=fe2d43d2`** — everything older predates the day's fixes and must not be used as evidence for these bugs.
- The user runs an unpackaged `dotnet run` / JIT build for these reports. Startup numbers from it are relative only.

### Bug index

| Bug | One line | Regression from 2026-09-15? | RCA status |
|---|---|---|---|
| **C** | playlist rows lose "Artist · Album" once cached | **yes — entity persistence** | complete, high confidence |
| **F** | artist page reveals in patches, grey avatar | **yes — artist tint/hero fix** | complete |
| **D** | "Singles & EPs" shimmers ~5 s | **yes — discography priority demotion** | complete; fix needs a new primitive |
| **A** | sidebar "0 songs" / blank tile / "0 items" | fix landed today **cannot work** | complete, high confidence |
| **E** | navrail 16-18 ms scroll frames | no (pre-existing) | complete; hidden second sidebar |
| **B** | scroll input: ignored flick, heavy lift | no (engine) | code-read ranking + prior measured art |

Fix order recommended in §12. C first: it is the only one that makes the app show *less* data than it did before.

---

## 2. TRAPS — read before writing code

Each of these produced a wrong fix **that the test suite approved**. Ordered by how likely they are to bite again.

1. **A green suite proves very little here.** Three separate fixes today were wrong while all tests passed.
   Always ask *what state does the user actually have*, and construct that state in the test.

2. **`Asked` is never cleared on a successful answer** (`Fetch.cs:262` `NeedOf = wanted & ~Known & ~Asked`; marked
   at `:476`; cleared only on terminal failure at `:1003`). **So a field group that is asked, and then answered by
   a route that does not carry it, is never retried for the lifetime of the scope.** This is the engine behind
   bug A1 and it silently defeats any "just ensure it again" fix. Re-asking at a higher priority is also a no-op
   for the same reason (bug D).

3. **A test can enshrine the bug with a persuasive comment.**
   `SidebarProjectionTests.KnownIdentity_WithZeroTracks_StillSaysZeroSongs` asserts `SongCount(0)` for exactly the
   state the user is complaining about, and its doc comment argues well for why that is correct. The argument is
   sound; its premise (identity known ⇒ count known) is false. **Fixing A1 requires deleting that test.**

4. **Never infer "we know X" from "we know the group X usually ships in".** A route can stamp a group applied
   while carrying none of its optional fields (A1), and a disk load can restore a group's bit without the side
   effects the live answer had (C). Gate on a bit written by the decoder that actually produced the value.

5. **A "0" that means "not yet known" must not render as a confident zero.** Three instances already: the song
   count (A1), the folder item count (A3), and — before today — the album page's facts line.

6. **Do not trade a working feature for a startup or render win.** Two fixes today did this; both were reverted.
   Lazy page registration silently disabled three features to save ~5% of a frame budget dominated by the engine.

7. **`StagedId` has an implicit conversion from `EntityId`.** Assigning a live table's `Id[slot]` compiles, then
   the store drops the row for any text-form identity because the store thread cannot touch the interner. A
   `Debug.Assert` now guards `RowWriter.Emit(in StagedId, …)`; Release still drops silently.

8. **Never add a sqlite column for small state.** The schema fingerprint covers the whole generated DDL; a
   mismatch **deletes `library.db` once**. Use `meta` rows (`Store.MetaGet`/`MetaSet`).

9. **Flex shrink acts on the MAIN axis.** The detail rail is a column, so `Shrink = 1` there squashes a cover out
   of square rather than narrowing it. Clip at the wrapper instead.

10. **Subagent reports are reliable about *what* was written and unreliable about *whether it is right*.** Every
    wrong fix today came with a confident, well-written report. Verify the claim, not the prose.

11. **Doc line numbers rot.** The scroll diagnostics plan cites files the scroll-v3 rewrite deleted. Grep before
    trusting any `file:line` in a plan older than the engine pin — including the ones in this document.

---

## 3. BUG C — playlist rows missing "Artist · Album"  ·  **REGRESSION FROM TODAY'S PERSISTENCE WORK. HIGHEST PRIORITY.**

User: *"this playlist is missing artist/album data? … but not every track (?)"*

**The disk cache seals `Identity` as known without the data that `Identity` implies, so the app permanently shows
LESS than it would with no cache at all.** Rows 9 and 10 looked right precisely because they were cache *misses*.

The chain, all verified by reading the code:
1. `TrackShape.PersistedFields = Identity | Extras` (`Track.cs:684`); Identity persists title / image / album_uri /
   duration / flags.
2. `TrackShape.Load` does `row.Known = r.Known & PersistedFields` (`Track.cs:756`), so the **`Artists` bit inside
   `Identity`** (`Track.cs:57`) comes back marked known.
3. `Fetch.NeedOf = wanted & ~Known & ~Asked` (`Fetch.cs:262`) → a cached track needs nothing for
   `TrackFields.Row`, and `Select` drops it (`:276`).
4. No kind-10 `TrackV4` request goes out — and `TrackV4` is the **only** producer of both the
   `Relation.TrackArtists` run and the thin Album row (`Spotify.Decode.cs:472,478,489,555`).
5. `MetadataLine` (`Track.UI.cs:557`) builds from `t.ArtistSlots` + the album's title; `BuildLinkSpans`
   (`:749`) returns **null** when both are empty, so the leaf is not emitted at all — the blank you see.

`Relation.TrackArtists` is deliberately **not** a `FetchEdge` (`Fetch.Routes.cs:63-108`: a relation that arrives
as a side effect of a row answer is asked by asking for the row's group). And the artists edge is **not
persisted** — `EdgeRelation.TrackArtists = 1` exists in the schema (`Store.cs:450`) but is never saved or read.

**The shape's own doc comment (`Track.cs:636-643`) names this hazard and dismisses it** by saying the credit line
"is left to re-arrive with the network's next Identity answer". **That premise is false**: marking Identity known
is exactly what guarantees there is no next Identity answer. See traps 2 and 4.

### Fix options (pick one, do not do all three)
- **Do not mark `Artists` known on a disk load** — mask it out of the restored `Known`, so the planner still asks.
  Cheapest and most correct; costs one network round trip per cached track, which is what 0.2.10 paid anyway.
  Check whether `Album` (the thin album row from `TrackV4`) has the same problem: a cached track's `album_uri`
  points at a slot that may itself be a bare row.
- **Persist `TrackArtists`** as a sixth edge relation. Bigger, but removes the round trip.
- Persist a flat `artist_line` column and accept it as display-only.

### Two secondary defects on the same line — fix with it
- **`Track.cs:512` is unguarded**: `t.SetText(ref t.ArtistLine, slot, s.Intern(row.ArtistLine))` has no
  `if (!row.X.IsEmpty)` guard, while `Title` (`:511`) and `Image` (`:515`) both do. **A disk-loaded row actively
  blanks a credit line a live answer had already interned.** Blast radius: `Queue.UI.cs:88,944`, `Rail.UI.cs:787`,
  `Shell.UI.cs:409`, `Stage.UI.cs:644`, `Deck.UI.cs:590`, `Drag.cs:104,561`.
- **`TrackTable.Present` (`Track.Table.cs:1795-1814`) reads `t.ArtistSlots` but never subscribes to
  `Edges.TrackArtists.Changed`** — unlike `Album.UI.cs:85`, `User.Page.Liked.cs:106`, `Sidebar.UI.cs:145`. Masked
  today only because the edge lands in the same drain as the row. Once the artists arrive in a *later* drain
  (which any fix above causes), the table will not repaint without this subscription.

### Ruled out (do not re-investigate)
Fetch dedup, the `CloseArtists` deferred close, and the `ReplacePage` terminal shrink were each traced and cleared.
`TrackArtists` is always a whole-list `Replace`, never a page, so the shrink is unreachable for it.

### Verification
On a real account: open a playlist, quit, relaunch, open it again. Every row must show its credit line on the
second open. Then the negative: with the store present, a track whose live answer set a credit line must keep it
after a disk load lands on top (the `:512` guard). Add a StoreTests fact that persists a Track, frees the slot,
reloads, and asserts `Knows(Artists)` is **false**.

---

## 4. BUG F — the artist page reveals in patches  ·  **REGRESSION FROM TODAY. RCA COMPLETE.**

User: *"why is it not shimmering fully?"* Video frame (`buggggg.mp4`, playlist → artist → back): hero fully real,
"Artist pick" panel fully real **with a flat grey avatar circle**, "Top tracks" still a shimmer grid — all in one
frame.

**Root cause is today's artist tint/hero fix.** `Artist.Page.cs:306` changed the page gate to
`_bodyReady = _ready || Detail.CoverLatch.IsUsable(_heroUrl)`. `_ready` was `ArtistReadiness.Overview`, whose own
docstring is the contract that was broken:

> `Artist.Rules.cs:43` — *"The overview renders as a UNIT: stats, bio, pick, upcoming, latest and tour, or none
> of them."*

The page now flips to Content as soon as a hero URL exists, and below it the readiness topology is uneven:
- **Top tracks** has its **own** `SkelRegionEl` with **`Group: null`** (`Artist.UI.Chart.cs:186-189`) and the
  strictest gate on the page — so it is always last.
- **Artist pick, Upcoming, Latest, Tour, Biography, all facets and shelves** have **no gate at all** — they render
  whatever is known mid-flight.
- Sections are also **presence-gated at plan level** (`Artist.Page.cs:362-379`): a section with no data is absent
  from the array and **pops in** later. A second, structural source of patchiness.

### The mechanism that fixes it already exists: `SkelGroupCoordinator`
`fluent-gpu-pin/src/FluentGpu.Engine/Hooks/SkeletonRegion.cs:229-270` — sibling `SkelRegionEl`s sharing a `Group`
token all reveal **together, in one settle window**. The artist page already passes `Group: routeKey`
(`Artist.Page.cs:315`) but is the **only member**, so it fires immediately; the chart passes `Group: null`.
**`Concert.Page.cs` is the in-repo precedent that does this correctly** (`"concert-hub"`,
`"artist-schedule:" + RouteKey`, …).

### Recommended: do both halves
- **(a) Restore the unit, keep the fast hero.** Keep `_bodyReady` (it fixed a real bug — the photo unmounting to
  a flat placeholder on every artist→artist nav). Add a `SkelRegionEl` around the band inside `TopBand` with
  `Group: routeKey`, and change `Artist.UI.Chart.cs:188` from `Group: null` to the same `routeKey`. Hero appears
  at once; pick and top-tracks then reveal together.
- **(b) Fix the frozen grey leaf.** `PersonPicture.Create` (`fluent-gpu-pin/.../PersonPicture.cs:103-110`) uses a
  **static theme grey** (`Tok.FillControlAltQuaternary`) with no watch subscription, so it never repaints when the
  grading lands. Route the pick avatar through `Controls.Artwork` (which stacks `Shimmer` → `WatchedPlaceholder`
  at this size) or pass a bound fill. **Note `Controls.Shimmer` deliberately refuses below
  `Design.ShimmerMinEdge = 80f`** and hands back a static tile — so a 32 DIP avatar wants
  `Design.WatchedPlaceholder`, not the breathing shimmer. The same frozen-placeholder defect exists at
  `Artist.UI.cs:381` (hero no-url arm) and `:716` (pick background).

(a) alone leaves grey circles inside a coherent band; (b) alone leaves the page revealing in three waves.

### Verification
`--fake` is enough for (a): navigate artist→artist and record; no frame may show a real section beside a shimmer
section below the hero. (b) needs a real session with a cold palette cache: the pick avatar must tint when the
grading lands, without navigation.

---

## 5. BUG D — "Singles & EPs" shimmers ~5 s  ·  **REGRESSION FROM TODAY. Confirmed.**

User: *"this is loading for like 5 seconds until it suddenly gets resolved. another regression"*

Today's change demoted discography facets 2 and 3 to `FetchPriority.Prefetch`. **The artist page's tabs are a
scroll-spy, not a view switch** — all three facet sections render inline, stacked (`Artist.Page.cs:482-484`), and
clicking a tab only scrolls (`GoToSection`, `:169`). **Nothing reads selection**, so priority is hard-coded by
facet identity at `Artist.Discography.cs:544`, `:584-585`, `:421-426`. The facet you are looking at stays
`Prefetch` forever, and `Pump` picks strictly by priority with no aging (`Fetch.cs:716-728`).

The ~5 s is a **two-hop serialized chain, both hops at Prefetch**: the facet edge page first (`:439`), then
`AlbumFields.Card` for its releases (`:442-444`). The grid's gate is all-or-nothing — `DiscoGridHost.Count`
(`:826-839`) needs the edge `Complete` **and** every target knowing `Card` before one card paints.

### The blocker: re-asking at a higher priority is a NO-OP. Promotion needs a new primitive.
Verified at four levels: `PlanEdge` dedupes on `WasAsked` before priority is read (`Fetch.Edges.cs:52`);
`NeedOf` masks `~Asked` (`Fetch.cs:262`); `Bucket`'s key **packs priority into the shape word** (`Fetch.cs:646`)
so a higher-priority ask mints a new empty bucket and leaves the parent in the old one; and `s_active` has no
priority the runner re-reads. `Entities.RefreshEdge` is a refetch, not a promotion — wrong tool.

**Minimal fix:** add `Fetch.Promote(...)` / `Fetch.PromoteEdge(...)` that moves an entry between buckets, OR drop
priority out of the bucket key and make `Demand.Priority` a max-of-asks. Then thread the scroll-spy's active
section into `FacetProps` (`:505-510`, include it in `Equals`/`GetHashCode`) and read it at `:544` and `:584`.
`GridProps.Priority` already exists and already participates in equality, so the grid side propagates free.

**Cheapest acceptable fix if the primitive is out of scope: revert the demotion** (`:421-426`) and keep only the
scroll-paced page-2+ paging (which is a genuine win and has no visible cost). Because the grid gate is
all-or-nothing, a demoted facet shows 8 shimmer cards rather than nothing, so the demotion's cost is visible.

**Not affected:** the standalone `DiscographyPage` (`:1316-1380`) — it demands and renders one facet, always at
`Visible`, and switches by navigating.

### Verification
Real session, Doja Cat (or any artist with 3 populated facets). Click "Singles & EPs" within 1 s of the page
appearing: cards must land within the same window as "Albums", not seconds later. Pure test: a facet marked active
must plan at `Visible` regardless of its index.

---

## 6. BUG A — sidebar playlists: "0 songs", blank tile, "0 items"  ·  RCA COMPLETE, HIGH CONFIDENCE

**A fix for this landed today and cannot possibly work.** It targets the wrong data on both halves. Screenshots:
"Eurodance Mix" and "Nostalgia 2000s Mix" with cover art and "0 songs"; "My Playlist #9" (43 songs) with a blank
grey tile; "New Folder — 0 items". Clicking a row fixes it; `bad_scroll_left.mp4` shows one "0 songs" row amid
correct neighbours.

### A1 — "0 songs" on a row that HAS cover art

`Entities.Ensure(p, PlaylistFields.Identity)` resolves to `Metadata(ListMetadataV2)` (ext kind 205) because it is
the only route whose `primary` covers `Identity` (`Fetch.Routes.cs:246`, gate at `:322`). **That proto has no
length field at all** (`Protos/list_metadata_v2.proto:6-28`), so `Spotify.Decode.cs:840` stamps `Identity` applied
while `Playlist.cs:654`'s `if (row.TrackCount > 0)` leaves the count at 0.

Result: `IdentityKnown == true` **and** `TrackCount == 0` → the new guard at `Sidebar.UI.Rows.cs:1415` passes and
paints "0 songs".

Two aggravators:
- The new ensure is a **permanent no-op in production**: `Spotify.Library.cs:303-309` already ensures
  `PlaylistFields.Row` (= `Identity|Accent`, same kind-205 route) for every rootlist playlist, so by the time the
  sidebar rebuilds, `ShouldEnsureIdentity` returns false.
- The one ensure that *would* fix it is gated off for covered rows (`Sidebar.cs:4401`, `:4436`).

Clicking works because `Playlist.Page.cs:330` asks `PlaylistFields.All`, which includes `Capabilities` — primary
on the **PlaylistRead** route → `Decode.cs:1477` writes the real count.

**Fix targets:** ensure `FetchEdge.PlaylistTracks` (→ PlaylistRead) or `PlaylistFields.Capabilities` for visible
rows; drop the `!hasCover` gate for the count path; add a **`CountKnown` bit** set only by the two decoders that
actually write `TrackCount` — `IdentityKnown` is not a usable proxy. Consider the fallback the detail page already
has (`Playlist.Page.cs:287`: `Knows(Identity) && TrackCount > 0 ? TrackCount : slots.Length`). Delete
`KnownIdentity_WithZeroTracks_StillSaysZeroSongs` (trap 3). Batch the ensures into one span-form call after the
walk (see E3).

### A2 — blank mosaic tile on a COVER-LESS row

`PlaylistMosaicTiles` (`Sidebar.cs:4156-4175`) needs three things: membership, each member track's `AlbumSlot`,
and its `ImageId`. The membership fetch supplies **only the first**: `Decode.PlaylistRevision`'s item decoder
(`Spotify.Decode.cs:1538-1562`) reads the target uri, `added_by`, timestamp and `item_id` — **no title, no album,
no image**. Member tracks land as bare slots with `Known == 0`, so the tile loop `continue`s past every one.

**Fix targets:** `FetchEdge.PlaylistTracks` **and then** a second `Entities.Ensure(tracks, TrackFields.Identity)`
over `p.TrackSlots` — inherently two round trips. **Plus** fold the member track rows' versions into
`SidebarLibraryFingerprint.PlaylistRow` (`Sidebar.cs:4679`), or the tiles will land with no rebuild to show them.
(That missing fold is also why clicking "fixes" it: navigation bumps `HistoryVersion` and forces a rebuild.)
**Bug C interacts here**: a member track restored from disk has `Identity` known and therefore will not be
re-asked; but it does carry `ImageId` and `AlbumSlot`, so the mosaic is fine once C is fixed either way.

### A3 — "New Folder — 0 items"

`WalkRootlist` adds a folder row with a literal `0` at `FolderStart` (`Sidebar.cs:4327-4329`) and only writes the
real count at `FolderEnd` (`:4354`). The loop ends at `:4423` **without draining the open stack**, so a folder
whose `spotify:end-group:` marker never arrives shows "0 items" forever. Same class as A1: an unknown rendered as
a confident zero.

### The pin band has the same defects
`ResolvePins` overlays rows built by `ResolveLivePin`/`ResolveUnlistedPin` — mosaic from bare tracks
(`Sidebar.Host.cs:3070`), `ChildCount: p.TrackCount` (`:3074`), and `Sidebar.cs:4802` **hard-codes
`IdentityKnown = true`**.

### Verification
Real session, fresh launch, no clicks: every rootlist playlist with tracks shows a non-zero count and a cover or
mosaic within the first sync. A genuinely empty playlist still says "0 songs". Tests must construct the actual
state: `Identity` known, `TrackCount == 0`, count *not* known → subtitle omitted.

---

## 7. BUG E — the left navrail costs 16-18 ms on a scroll frame  ·  RCA COMPLETE (pre-existing)

User: *"scrolling is still slow fps sometimes, especially for some reason in the left navrail"*, *"look how bad
the left nav rail here scrolls"*. Measured, current build (`sid=0f32b52c`/`fe2d43d2`): content scroll is 120 fps /
`overBudget=0` / `avgFrameMs=0.5`, but the worst frames are `PaneView×2 (r=16.17)` — 16 ms inside two render
bodies, ~8 ms each — plus `RailPanel r=24.2 ms / 3.4 MB` and steady idle churn re-rendering tooltip and overlay
hosts hundreds of nodes at a time.

### E1 — **Half of that cost is a sidebar the user cannot see.** Fix this first.
`Shell.UI.cs:590` mounts `NarrowDrawer` **unconditionally**. `NarrowDrawer.Render` (`:1282`) computes
`open = NarrowShell && DrawerOpen` but uses it **only for `HitTestVisible`** — the subtree is built regardless
(`:1318-1330`), ending in a second full `Sidebar.DrawerPane()` (`:1380`).

So on a desktop-width window there is a **second, permanently hidden `PaneView` planning the whole sidebar on
every invalidation**, and `PumpBinder` is registered **twice**, so every drain folds the whole-library trigger
twice. Returning an empty box when `!Ui.NarrowShell.Value` halves `r`, halves the binder, halves the rail's 33
tooltips. Near-zero risk on desktop. Check that `DrawerOpen` toggling on a narrow window still mounts it.

### E2 — `Revision` is bumped unconditionally; it gates the expensive work
`SidebarProjectionBinder.Rebuild` ends with a bare `_revision++` (`Sidebar.Host.cs:2909`) — even though the two
things that *trigger* a re-render are carefully gated (`Entries.Publish` bumps `Version` only on byte-changed
content, `:3409`; `PublishInput` only when a feed moved, `:2956`).

`Revision` then feeds two heavy paths:
- `PaneView.PlanDep` (`Sidebar.UI.cs:772-776`) → `BuildStage` re-runs `Sidebar.Plan` (full document → flat rows)
  **plus** `PlanRail`.
- `V3Session.ShapeInput` (`Sidebar.UI.LibraryV3.cs:218-225`) → `View.Build` — a **full re-bucket and re-group of
  the entire published library**, with a recursive folder walk, **inside `PaneView.Render`**.

So a rebuild that changed only the recency feed still forces a whole-library re-group and a whole-document
re-plan, twice. **That is the 8 ms per pane.** Bump `_revision` only when a publish actually flipped.

### E3 — the theory I went in with was WRONG; do not re-chase it
Suspected: table-wide invalidation and a self-triggering `Ensure` loop from today's sidebar change. **Both ruled
out, definitively:**
- The sidebar's render path reads only **3** table-wide counters (`Sidebar.UI.Slot.cs:1029`, `Rows.cs:571`,
  `LibraryV3.cs:904`). `PaneView.Render` and `PaneSlot.Render` read **zero**. The 17 counters in `PumpBinder`
  (`Sidebar.UI.cs:133-150`) are **wakes, not work** — `Sync()` folds per-row fingerprints and returns early.
  The sidebar already implements the RowStamp discipline, at the gate rather than the render.
- No feedback loop: `Entities.Ensure` never bumps `Version` or calls `MarkDirty` (`Entities.cs:1911`);
  `PlanEdge`'s marks don't move `Version(parent)`; and `Sync` snapshots triggers **after** `Rebuild` (`:2812`).

**Real but smaller:** today's change made one `Fetch.Plan` call **per un-identified rootlist playlist** per
rebuild, each renting two pooled buffers and calling `Fetch.Pump()`. Batch the slots and issue one span-form
`Entities.Ensure` after the walk (`Fetch.Plan` is already span-shaped).

### E4 — the 140 mounts are fine
Rows **are** properly virtualized (`ItemsView.CreateBound`, `Overscan = 2`, per-kind recycle pools) and there is
no scroll-dependent remount. 140 mounts ≈ 23 scene nodes × 6 newly realized rows — a first-realization burst from
the plan republish widening the window. To cut it, reduce per-row node depth: `DropPlate`, `InsertionLine` and
`SelectionPill` are mounted on **every** row whether or not a drag is live.

**Fix order:** E1 (hidden pane) → E2 (conditional revision) → split the `ViewEpoch` key from the plan key
(`LibraryV3.cs:232-243`) → batch the ensures → then the per-row node depth.

### Verification
Same log field: after E1+E2 the worst `PaneView` render on a sidebar scroll must be under 8.3 ms and appear once
per frame, not twice. `ops/tools/perf-tour.ps1` reports against the same gates.

---

## 8. BUG B — scroll INPUT feel  ·  code-read ranking + prior measured art

User, verbatim: *"scrolling also just feels a bit like not responsive? this is not an fps issue, but rather on the
input side. sometimes my flick gets ignored and no scroll momentum, it just stops. the initial scroll lift feels
heavy, but is smooth when its eventually scrolling"*

**Confirmed not a frame-rate problem** (`scroll.frames`: 120 fps, `overBudget=0`, `avgFrameMs=0.5`). But note the
caveat in §8.5: that field is structurally blind to both symptoms.

**Everything in this section is engine-side.** There is no app-side hook for any scroll constant: one feel
profile, `ScrollFeel.Shipping` (`FluentGpu.Engine/Scroll/ScrollFeel.cs:31-51`), constructed at
`Hosting/AppHost.cs:2580`. Fixes land in `fluent-gpu-combined`, then the pin moves.

**Provenance.** §8.1–8.6 were read from the pinned engine's code by an explorer, not measured. The following
were independently re-verified in `fluent-gpu-pin` on 2026-09-15 and are safe to cite: `LatchSlopDip = 8f`
(`ScrollInputRouter.cs:96`, enforced `:251`); `if (_count < 2) return;` (`ScrollPhysics.cs:408`); `Cap = 8`,
`HorizonMs = 40f` (`:353-354`); `UseOsInertiaStopFallback = false` (`Win32DirectManipulation.cs:74`); the
"nothing reads them any more" comment (`InputDispatcher.cs:61`); the `subNotch` rule (`Win32Platform.cs:1394`);
`FlingSeedGate: 50f` (`ScrollFeel.cs:33`); and **zero** live emit sites for `ScrollTrace.VelSample`, `Release`,
`Latch` and `Phase`. Everything else carries the explorer's own confidence rating.

### 8.0 The prior investigation — READ IT FIRST
`docs/plans/wavee/scroll-feel-investigation-2026-09-10.md` (5 days old, **with real measurements**). Its ranked
cause #4 is exactly the "ignored flick":

> *"Contacts landing during a stale DM INERTIA get stranded as pointer messages … the engine keeps DM parked in
> INERTIA (not Live, idle pump only) instead of stopping it, which is what the unused `UseOsInertiaStopFallback`
> arm does (`Stop()` at the INERTIA edge → READY at once → the next landing gets a fresh hit-test). Mitigation
> candidate, needs the cell-F probe run."*

It also records that the comment claiming *"DM never reports INERTIA"* is **false on this hardware — every run**.

**Verified today:** the const still reads `false`. The mitigation is written, compile-time gated, and **never
landed**. The probe that was supposed to settle it (`ops/tools/dm-probe` in the engine repo) exists and was never
run for cells E/F.

**UNVERIFIED:** whether the four engine fixes that investigation says landed on `feat/ultra-fast-engine` (§6 —
compositor-clock latch, per-window pacing, etc.) are ancestors of the current pin `ba24aac6d`. A grep for the
clock-debounce wording found nothing in the pin. **Check `git merge-base --is-ancestor` before re-diagnosing any
clock symptom.**

### 8.1 Map of the input path
Three producers, one kernel (`Scroll/ScrollInputRouter.cs` → `Scroll/ScrollKernel.cs`):

| Path | Produced by | Physics |
|---|---|---|
| DirectManipulation | `Win32DirectManipulation.cs` (needs `DM_POINTERHITTEST` + `PointerKindOf == Touchpad`) | drag 1:1, engine-owned fling |
| Hi-res wheel fallback | `Win32Platform.HandlePointerWheel` `_wheelHiRes` branch, `:1414-1462` | drag 1:1, engine-owned fling |
| Detented mouse wheel | `HandlePointerWheel` tail, `:1464-1492` → `ScrollInput.WheelNotch` | **`Driven` chase to a fixed target, hard-stop, no fling ever** |

Input is applied **once per frame**: the router accumulates the frame's deltas and flushes one `FrameDelta` at
`AppHost.cs:3519`, immediately before `_scrollKernel.Tick` at `:3530`.

### 8.2 Symptom A — flick ignored, no momentum. Ranked.

| # | Cause | Evidence | Confidence |
|---|---|---|---|
| A0 | **Stranded contact on stale DM INERTIA** (prior investigation's #4); fix already written behind `UseOsInertiaStopFallback` | measured 2026-09-10 | **Measured** |
| A1 | Touchpad gesture classified as a **detented mouse wheel** → `Driven` chase, no fling by construction. DM rejects the contact (not `PT_TOUCHPAD`) → OS synthesises exact ±120 packets → `subNotch` false → `_wheelHiRes` false → wheel branch. The latch is re-evaluated only after a 200 ms idle gap (`WheelGestureGapMs`), so the first packet decides the whole gesture. | `Win32Platform.cs:1394, 520-527, 2026-2028`; `ScrollKernel.cs:864-882`; `ScrollBody.cs:234-239` | High that the path exists; that the user's gestures take it is inferred — **settled instantly by `RawWheel` bit 64** |
| A2 | **Re-grab cancels the fling and restarts from zero velocity**, then needs 2 fresh frames to re-fling. The second flick of a flick-flick-flick sequence, if short, gives: fling killed, small 1:1 drag, dead stop. | `ScrollKernel.cs:766-789` (`b.Velocity` abandoned, `Impulse.Reset`) | High (read from code) |
| A3 | Release velocity is estimated from **one coalesced sample per frame** (40 ms window, 8-slot ring) and needs ≥ 2 → a flick under ~17 ms at 120 Hz releases at exactly 0. The pre-coalesce packet ring built for exactly this (`Pal.cs:296-340`, "scroll-v3-plan §5.4") is drained and **thrown away** — `FeedPanVelocity` feeds only swipe/FlipView. | `ScrollPhysics.cs:408`; `ScrollKernel.cs:788-789`; `InputDispatcher.cs:59-84` | High (read from code) — a regression against the engine's own design |
| A4 | A gesture that never crosses **8 DIP** total is discarded entirely; `EndPhaseGesture` posts nothing when unlatched | `ScrollInputRouter.cs:251, 303` | Medium |
| A5 | Fallback-path lift is a **50–120 ms silence timer**; content freezes, then flings (or does not) | `Win32Platform.cs:1301-1311, 302-303` | Medium — non-DM path only |
| A6 | Sidebar only: a `ScrollKey` change mid-gesture posts `ScrollTo(immediate)` + `Cancel`. The sidebar's key is `InDrawer ? prefix + ".drawer" : prefix` (`Sidebar.UI.cs:694`) | `Reconciler.cs:2707-2710` | Low/Medium (inferred) — **app-side** |

Also: a packet classified as detented while a phase gesture is open posts `ScrollInput.Cancel` on the live node
(`ScrollInputRouter.cs:344-351`), zeroing velocity — a second way a flick dies under A1.

### 8.3 Symptom B — heavy initial lift. Ranked.

| # | Cause | Evidence | Confidence |
|---|---|---|---|
| B1 | **8 DIP latch slop** ≈ 73 raw touchpad units (`HiResUnitDip = 0.11f`) of nothing, then a jump. The "no dead zone" comment at `:267-270` is true of distance, not time. | `ScrollInputRouter.cs:96, 251`; `Win32Platform.cs:295` | High |
| B2 | Detented path: `Carryover` emits **nothing until 120 raw units accumulate** and discards the partial on a direction change | `Win32Platform.cs:1345-1353` | High if A1 holds |
| B3 | DM engage round-trip: packets **swallowed** while `_awaitingEngage` (≤ 120 ms budget), then the first RUNNING update spent as baseline | `Win32DirectManipulation.cs:435, 207, 101, 558-563`; `Win32Platform.cs:1401-1406` | Medium — only if DM engages |
| B4 | Deltas applied **once per frame** at the boundary, not per message; up to one refresh before the pipeline starts. The message *wake* is prompt (`WM_POINTERWHEEL` is not deferrable, `:237-239`). | `AppHost.cs:3519, 3530` | High |
| B5 | **Ruled out**: no kernel dead zone, no rubber-band at rest, no ease-in ramp, no per-packet hit test | `ScrollKernel.cs:797-847`; `ScrollPhysics.cs:36-44` | High |

So B is **pure latency, not synthetic resistance**, which is cheap to fix.

### 8.4 Symptom C — smooth once moving: expected, nothing to fix
`FrameDelta` applies 1:1 with no filter (`ScrollKernel.cs:782`); `CoastStep` (`ScrollPhysics.cs:64-72`) is an
exact closed-form integral, frame-rate-independent.

### 8.5 What the diag build can and cannot settle today
`ScrollTrace` is compiled in under `DEBUG || FLUENTGPU_DIAG` (`Foundation/ScrollTrace.cs:118-126`), armed by
`FG_SCROLL_TRACE` (any value other than `"1"` is the output path; default `%TEMP%\fg-scrolltrace.csv`; ring
flushes on idle frames — **close the window cleanly or the tail is lost**). Build with
`ops\build\publish-wavee-aot.ps1 -Diag`.

**Live emit-site census in the pin** (the diagnostics plan doc is wrong about this):

| Kind | Live | Kind | Live |
|---|---|---|---|
| `RawWheel` | 4 (`Win32Platform.cs:1419,1443,1482`, swallow `:563`) | `Phase`, `Latch` | **0 — dead** |
| `FbLift` | 1 (`:1306`) | `VelSample`, `Release`, `GestureEnd` | **0 — dead** |
| `Coalesce`, `VelDeposit` | 2, 1 (`Pal.cs:285,308,330`) | `ApplyPan`, `WheelCancel`, `AnimTick`, `AnimEvent` | **0 — dead** |
| `WheelSeed`, `OffsetWrite` | 1, 1 | `Frame`/`FrameTiming`/`Latency`/`Note` | live |

- **A1 (classifier) is settled by a trace today.** `RawWheel.i1` bit flags: `horizontal|1, thisHiRes|2,
  streamIdle|4, _wheelHiRes|8, ptTouchpad|16, ctrl|32, phasePath|64, fbActiveBefore|128`, `swallowed|256`, swallow
  reason at `(i1>>9)&7` (`Win32Platform.cs:541-567`). Touchpad flicks with **bit 64 clear** = misclassification
  confirmed. `Note 109` (`Win32DirectManipulation.cs:551`) says whether `DM_POINTERHITTEST` went unserved.
- **A3 (impulse returning 0) is NOT measurable today.** No live `VelSample`/`Release` rows. Infer only from
  `OffsetWrite` (`i1` = activity): a flick with no `Activity == 2 (Ballistic)` run after the last `== 1 (Drag)` row
  is a dead fling — *that* it died, not *why*. **Re-wiring `ScrollTrace.VelSample` at `ScrollKernel.cs:789` and
  `ScrollTrace.Release` at `:685-698` is a ~1 h engine change and settles A3 definitively.**
- `Coalesce` + `VelDeposit` row counts show how many packets fold into each frame (how much A3 discards).
- Sidebar: `OffsetWrite` rows with `i2 == 2 (Reclamp)` mid-gesture would confirm A6.

**Two harnesses exist and are not equivalent:**
- `fluent-gpu-pin\ops\diag\wavee-scroll-session.ps1` — **use this one.** Sets `FG_BIND_CONTRACT=0` and
  `FG_BACKWARDS_WRITE=0` (both default-ON once compiled in and change the feel being measured) and clears
  `FG_SCROLL_LOG`. Post-process with `ops\diag\parse-scroll-csv.ps1`.
- `wavee-0.3\ops\tools\scroll-capture.ps1` — **has both hazards**: sets `FG_SCROLL_LOG='1'` (per-event console
  writes perturb pacing) and never clears the two guards; its docstring promises rows that no longer exist.
  **App-side fix: mirror the engine script's env block.**

**The `scroll.frames` log field is blind to both symptoms.** It accumulates only while `stats.ScrollActive`
(`Screens/Diagnostics.Host.cs:417-427`) and emits at ≥ `ScrollMinFrames` — exactly not the pre-latch dead time or
a flick that never flung.

### 8.6 Sidebar vs content
Same controller, same kernel, same physics — one feel profile process-wide. What differs is app-side: the expanded
pane is `ItemsView.CreateBound` with `Overscan = 2`, `CacheExtentPx = 240f`, plus `Reorder` and `Disclosure`
options (`Sidebar.UI.cs:676-708`); the collapsed navrail is a plain **non-virtualised** `ScrollView` with no
`ScrollKey` (`:595`); the chip rail is a horizontal `ScrollView` that `ScrollTo(0)`s on filter change
(`LibraryV3.cs:1334-1386`). `AccumulatePhaseDelta:252` picks the axis from `|Σdx| > |Σdy|` at the latch and never
re-evaluates, so a slightly diagonal start over the chip rail can latch the wrong scroller for the whole gesture.

### Recommended order for B
1. Build `-Diag`, run `wavee-scroll-session.ps1`, do 30 s of real touchpad flicks on Home and the sidebar. Read
   `RawWheel` bit 64 / bit 16 and `Note 109`. **This re-ranks everything.**
2. Run `ops/tools/dm-probe` cells E/F (engine repo). If cell F confirms the stranded contact, flip
   `UseOsInertiaStopFallback` in `fluent-gpu-combined`, verify, move the pin.
3. Re-wire `VelSample`/`Release` emit sites (1 h). Re-run step 1. Now A3 is measurable.
4. Then, in order of confidence: consume the pre-coalesce velocity ring in the kernel (A3), preserve or blend
   velocity on re-grab (A2), lower `LatchSlopDip` or apply pre-latch travel over time (B1). Each one a separate
   engine change with its own capture.

---

## 9. MEASUREMENT — end-to-end scroll latency, and a fair comparison to WinUI

User: *"measure the whole pipeline, then also somehow compare it to WinUI apps or other stuff outside of fluent-gpu"*.

**Feasible. Everything needed is already on the machine** (verified):

| tool | path | what it gives |
|---|---|---|
| PresentMon | winget shim on PATH | per-process present cadence and `MsUntilDisplayed`; **works on any app**, so it is the cross-app instrument |
| wpr | `C:\Windows\System32\wpr.exe` | ETW capture incl. input and DWM providers |
| xperf | Windows Performance Toolkit | the same, scriptable |
| ScrollTrace | engine, behind `FLUENTGPU_DIAG` | in-app per-event scroll state (§8.5) |

**Comparison targets, all checked out locally:** `C:\wavee\WinUI-Gallery`, `Files`, `Screenbox`, `ambie`. Prefer
an **installed** WinUI app (Windows Settings, the Store) over building one — the point is a reference curve, not a
fair fight between builds.

**Be honest about the ceiling:** without a high-speed camera you measure **input → present**, not input → photons.
Scanout adds a further fixed delay. That is fine for a *comparison*, because the same delay applies to every app —
but do not report it as click-to-photon.

**Suggested shape** (design not yet reviewed):
1. In-app: `ScrollTrace.RawWheel` carries the packet QPC and `OffsetWrite` the frame that applied it; `Latency`
   rows exist. Check whether that already yields input→present per packet before adding code.
2. Run PresentMon against Wavee and against the WinUI reference during the same gesture script.
3. Use `ops/tools/perf-tour.ps1`'s existing gesture model (`-InputMode Swipe`, seeded so two runs drive the same
   swipes) so the two apps get comparable input. Its own caveat: `SendInput` cannot post touchpad phase packets,
   so **a scripted run cannot reproduce A0/A1** — those need a human hand plus the dm-probe.

**The diag build changes behaviour:** clear `FG_BIND_CONTRACT=0` and `FG_BACKWARDS_WRITE=0` first, or you are
measuring the guards.

---

## 10. What 2026-09-15 changed (so the next person knows what regressed from what)

All uncommitted on `feat/0.3-structure`. ✔ = verified working, ✘ = caused one of the bugs above, ↺ = reverted.

- ✔ Page transitions sequenced again (exit 90 ms not moving, enter delayed 90 ms / 210 ms); five-style
  `PageMotionStyle` switcher in Settings › Appearance, default Spatial. Masthead re-coupled to the exit window.
- ✔ Artwork decode size is a hint, not the layout box (`Controls.ArtworkDecode`); zero-width slot guarded.
- ✔ One accent ladder (`Detail.AccentFor`); album "more by" prefetch no longer overwrites the tracklist
  (`landTracks: false`); leading artist run no longer discarded; `ReplacePage` terminal shrink; album-tracks kind
  filter skips in place.
- ✔ Detail rail cover clipped at the wrapper (bug B of the original plan).
- ✘ **Sidebar identity ensure + `IdentityKnown` subtitle gate** → bug A (cannot work; see §6).
- ✔ Queue seeded from cluster/context/current before Play (`Queue.DecideSeed`).
- ✘ **Artist tint on usable art + `_bodyReady` + hero keyed on uri** → bug F (fixed the flat-placeholder hero,
  broke the overview-as-a-unit contract; see §4).
- ✔ Album pending gate no longer waits on Prefetch relations; boot marks `boot.*` at seven points; jump list
  deferred via UI-thread timer (a thread-pool version was caught and rewritten — STA contract); search hero
  decode 300×260; ambient-power poll on `UseInterval`.
- ✔ Memory governor ported (`Platform/Residency*.cs`), 30 s poll on the UI thread, two arenas.
- ✔ Fetch dedup: runner-level `(routeKey, parentId)` in-flight index with re-plan on a row answer that stages nothing.
- ✘ **Discography facets 2/3 at Prefetch** → bug D. ✔ Scroll-paced page-2+ paging is fine and should stay.
- ✔ `RowStamp` early-outs on the four worst pages, with a `Source` tag (a slot is per-table; the fans-row flip
  could alias without it).
- ✘ **Entity persistence** — eight `KindShape`s, `RowWriter.Id`, five library-edge appliers, `MetaGet/MetaSet`.
  Verified: `library.db` now has 13 tables incl. all eight catalog tables (was 5 / 36 KB). **Not verified: real
  row counts after a sign-in.** Causes bug C via the `Artists` bit (§3).
- ✔ Library sync token + collection delta with a three-guard drift ledger; per-page streaming decode; a walk that
  hits the page ceiling without a terminal is discarded, not committed. Delta path activated in `Fetch.FillRevisions`.
- ↺ Lazy page registration — reverted (silently disabled "View credits", omnibar suggestions, file-drop play). The
  `BootOrderingTests` guard and the install-on-miss safety net were kept.
- ✔ Gap register: G-007 re-described honestly; G-254 / G-259 corrected to fixed.
- Deliberately NOT done: album below-the-fold scroll gate (would stop short albums loading their lower band);
  `Store.Epoch` seeding from `Clock.SeedEpoch` (would stamp real rows weeks into the past — comment fixed instead).

---

## 11. Consolidated UNVERIFIED list

- C: that masking `Artists` out of the restored `Known` is sufficient — the thin Album row from `TrackV4` may have
  the same shape (a cached `album_uri` pointing at a bare slot).
- A2: whether bug C's fix alone makes the mosaic tiles appear (member tracks restored from disk do carry
  `ImageId`/`AlbumSlot`), or the two-round-trip path is still needed for cold rows.
- B/A0: whether the four engine fixes from `feat/ultra-fast-engine` are in pin `ba24aac6d`.
- B/A1: whether the user's touchpad actually takes the detented-wheel path (one `RawWheel` capture settles it).
- B/A3: whether the impulse estimator is really returning 0 on ignored flicks (needs the dead trace rows re-wired).
- B/B3: whether DM engages at all on this hardware for these gestures (`Note 109`).
- Persistence: real row counts in `library.db` after an interactive sign-in; pages painting from disk offline.
- Perf comparison vs 0.2.10: only valid from the **same profile root** (packaged `LocalCache` vs unpackaged
  `%LOCALAPPDATA%\Wavee`); never with `--fake`, which skips `Store.Use`.
- The untracked `scroll-cover-jank-2026-09-15.md` design (other session) has not been reconciled with bug E.

---

## 13. Round 3 (2026-09-15 afternoon) — what landed against this document

Orchestrated as Sonnet agents on disjoint files with every claim re-verified against the code before acceptance;
gates run by the orchestrator only. Everything below is uncommitted on `feat/0.3-structure`, except the engine
commit noted under B. **Read the "still open" list at the end before assuming anything else is fixed.**

| Bug | Landed | Verified how |
|---|---|---|
| C | `TrackShape` masks `Artists` out of Save+Load; `CommitTracks` applies `known & Identity` (without this the mask was a no-op — the commit re-granted the bit); `:512` guard; `TrackTable.Present` subscribes to `TrackArtists.Changed`. **Album variant** found by audit and fixed the same way: new `AlbumFields.Artists` inside `Identity`, producers mark it only when they close `AlbumArtists`, `AlbumShape` masks it, all `SetText`s in `CommitAlbums` guarded; `AlbumFields.DiscoCard = Card & ~Artists` for the discography grid and the artist page's latest-release gate; `Lyrics.cs` narrowed to `Title`; `AlbumV4` closes an empty artists run with `EndEvenIfEmpty`. **Artist variant** (top-artist chips as initials): `CommitArtistRows` had the same bare-constant `Applied`; `ArtistV4`, the overview decoder and the legacy pathfinder overview decoder now claim `Image` only when they read one; `FirstUrl` now descends `avatarImage`. | StoreTests round trips (persist → free slot → reload → `Knows(Artists)` false, Title kept), decoder facts, suite green |
| F | Whole page is one skeleton region gated on `ArtistReadiness.Overview` with FadeOnly reveal — the 0.2.10 model; the chart shares `Group: routeKey`; `_bodyReady = _ready` (the photo-first arm is gone, at the user's decision); frozen placeholders replaced by `Design.WatchedPlaceholder` / `Controls.Artwork`. | pure reveal facts; runtime launch on an artist deep link logs no fault |
| D | Facet demotion reverted: page one of all three facets at Visible (`DiscographyRules.FirstPagePriority`); scroll-paced page-2+ paging kept. | pure fact |
| A | `PlaylistFields.TrackCount` bit written only by the PlaylistRead decoder when the wire carried `length` (and never from a `changes_require_resync` diff answer — that was the Eurodance zero); `CommitPlaylists` lands a real zero only with the bit; `PlaylistShape` restores the bit and **distrusts a persisted zero on load** (self-heals existing library.db); sidebar asks `FetchEdge.PlaylistTracks` for visible uncounted rows, batched span-form after the walk, cover gate dropped; member-track versions folded into the fingerprint; folder counts drained at end-of-walk and gated on `CountKnown`; pending folder pin renders no subtitle; pending artist/album pin renders no raw id; transport `DiffVerdict` honours `changes_require_resync` and falls through to the full read; `KnownIdentity_WithZeroTracks_StillSaysZeroSongs` deleted. | facts built on the user's actual state; the persisted `Eurodance Mix` row (`track_count=0`, bit 9 set) was the evidence |
| E | `NarrowDrawer` mounts nothing on desktop widths (`NarrowDrawerMount.ShouldMount`); `Rebuild` bumps `_revision` only when a publish flipped (`SidebarRevisionGate`); `ViewEpoch` no longer keyed on `Revision`. Per-row node depth NOT changed (Slot.cs carries explicit "mounted ALWAYS" reasoning for a stale-capture bug). | pure facts; not yet re-measured on a scroll frame |
| B | **Engine commit `1db38ad75` on `feat/0.3-engine-combined`, pin moved to it** (was `ba24aac6d`): impulse estimator fed per pre-coalesce packet (`ScrollInput.ImpulseSample`), so a two-packet flick inside one frame flings; `LatchSlopDip` 8 → 3 DIP; `ScrollTrace` Phase/Latch/VelSample/Release/GestureEnd emit sites restored; 4 new VerticalSlice gates. `UseOsInertiaStopFallback` still OFF (needs dm-probe cell F). `ops/tools/scroll-capture.ps1` env block fixed. | engine Debug+Release clean on the pin; `--suite scroll` 225 checks pass; the full slice's 2 failures (`gate.timer.clamp-never-spins`, `gate.anim.orphan-render-owned-minimized`) **pre-exist at `ba24aac6d`** — verified in a throwaway worktree, not caused by this commit |
| Home | Returning to Home no longer re-runs the cold-boot reveal hold (1.5 s + 8 s fallback) for data already in the graph: `HomeFeedReadiness.ShouldPaintOnMount` + scope-owned reveal marks in `Home.Feeds`. | pure + remount-no-new-ask facts |
| Queue seed | Boot-time seed no longer 401s before the session is online: `Queue.SeedRetryOn(status)` (401/403 → retry on the `Spotify.Status` online transition, cluster seed cancels the arm). | facts; the 401 was seen live in the log |
| Measurement | `ops/tools/scroll-latency-compare.ps1` + `ScrollLatency.psm1` + 20 Pester facts. PresentMon needs elevation; **in this remote session PresentMon exposes only DWM's presents, not app swapchains**, so the WinUI comparison must run at the physical display. | Pester 372/0 |

| Autoplay | `RemotePlan.AutoplayContinues` now accepts bare track/episode contexts (a track continues through its station; on a 4xx the host resolves `StationUri` via `ContextResolve`); the reducer asks for autoplay when a finite context LANDS (`DoContextPages`, `!MorePages`, repeat not Track) instead of only in the last row's endgame; `DoEndingSoon` stays as the fallback. Root cause of "where is autoplay": in 0.2.10 the rows came from the Connect cluster's server-side `next_tracks`; 0.3's local path asked only at the endgame and never for a bare track. | 486 playback/queue facts; full suite 9,019 / 0 / 6 |

**Final coherent build: `srcpps\Waveein\Release
et10.0\win-arm64\publish\Wavee.exe` published 13:13 (ARM64, 36.7 MB, no IL warnings), against pin `1db38ad75`.** Publishing from an x64-emulated shell auto-detects `-Arch x64` (no `PROCESSOR_ARCHITEW6432` under x64-on-ARM emulation) and leaves an x64 `FluentGpu.SourceGen.dll` under `bind` that then breaks the native publish with CS8034 — always pass `-Arch arm64` from such a shell, or publish from a native one.

Persistence is now proven with real data: after the user's morning sign-in `library.db` holds 3,841 tracks, 2,964 albums, 1,858 artists, 344 playlists, 396 library edges and 6 meta rows.

**Still open after round 3**
- `UseOsInertiaStopFallback` — needs the dm-probe cell-E/F run with a real touchpad, then flip in `fluent-gpu-combined`.
- The two pre-existing engine gates above (`AppHost` idle-wait classification) — not scroll, not this branch's work.
- E's per-row node depth (`DropPlate`/`InsertionLine`/`SelectionPill` mounted on every row) — needs a non-remounting toggle designed against the stale-capture history in Slot.cs.
- Bug E's actual scroll-frame number has not been re-measured after E1/E2.
- A pending pinned artist/album row now shows a blank title rather than a shimmer; the pin descriptor carries no cached display name.
- The mixed 11:58 AOT publish the user tested is NOT a valid build of any of this. The user's own arm64 publish at 12:50 (`srcpps\Waveein\Release
et10.0\win-arm64\publish\Wavee.exe`, 36.7 MB) started after the last app edit (12:47) and after the pin moved (12:27), so it is the first coherent build. Final gates on that tree: Debug clean, `Wavee.Tests` 9,013 passed / 0 failed / 6 skipped (all pre-existing integration/audio-cache skips), Release clean (compiled to a side directory because the running instance held the Release DLLs). Visual verification of the sidebar and artist page on that publish was NOT done by the orchestrator: a user instance was running and the app is single-instance.

## 12. Handoff prompt — paste this into a fresh session

```
You are picking up Wavee 0.3 (C:\wavee\wavee-0.3, branch feat/0.3-structure, everything uncommitted). Read
CLAUDE.md, then read docs/plans/wavee/wavee-0.3-bug-handoff-2026-09-15.md IN FULL, especially §2 "Traps",
before touching code. Six user-reported bugs are fully investigated there; no code has been written for any.
Four are regressions from the previous session's own work.

Constraints (from CLAUDE.md, restated because they were violated once already):
- Engine edits go to C:\WAVEE\fluent-gpu-combined on feat/0.3-engine-combined, then move the pin. Never edit
  C:\WAVEE\fluent-gpu-pin in place.
- No source-text tests: extract the decision into an engine-free pure class and test that.
- Gates before claiming done: `dotnet build Wavee.slnx` Debug AND Release clean (TreatWarningsAsErrors),
  `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` green (baseline 8,939 / 0 / 1 skipped). Engine-touching
  work also needs the engine's own gates. arm64 NativeAOT via `ops\build\publish-wavee-aot.cmd` (Windows
  PowerShell; pwsh is not installed) with 0 IL warnings.
- Subagents write on disjoint files; only you build, test and launch. Verify every subagent claim against the
  code — every wrong fix so far came with a confident report.
- Do not trade a working feature for a perf win. Do not "just ensure again": `Asked` is never cleared on success.
- Every fix references its issue in CHANGELOG and commit body (github-triage skill) if it maps to one.

Work in this order, building and testing between items:

1. BUG C (§3) — cached tracks lose their credit line. Mask the `Artists` bit out of the restored `Known` in
   `TrackShape.Load`; guard `Track.cs:512` like its neighbours; subscribe `TrackTable.Present` to
   `Edges.TrackArtists.Changed`. Add the StoreTests fact (persist → free slot → reload → `Knows(Artists)` false).
   Check whether the thin Album row has the same shape. Verify on a real account across two launches.
2. BUG F (§4) — artist page reveals in patches. Wrap the top band in a `SkelRegionEl` sharing `Group: routeKey`
   and give the chart the same group (Concert.Page.cs is the precedent); route the pick avatar through
   `Design.WatchedPlaceholder` (not Shimmer — below ShimmerMinEdge). Keep `_bodyReady`.
3. BUG D (§5) — "Singles & EPs" 5 s. Either add `Fetch.Promote`/`PromoteEdge` and thread the scroll-spy's active
   section into `FacetProps`, or revert the facet-2/3 demotion at Artist.Discography.cs:421-426 and keep only the
   scroll-paced page-2+ paging. Do not re-ask at a higher priority — it is a no-op at four levels.
4. BUG A (§6) — sidebar "0 songs"/blank tile/"0 items". Add a `CountKnown` bit written only by the two decoders
   that write TrackCount; ensure `FetchEdge.PlaylistTracks` (→ PlaylistRead) for visible rows, batched span-form
   after the walk; drop the `!hasCover` gate; fold member-track versions into the fingerprint; drain the folder
   stack at end-of-walk; fix the pin band. DELETE `KnownIdentity_WithZeroTracks_StillSaysZeroSongs`.
5. BUG E (§7) — navrail 16 ms frames. E1: return an empty box from `NarrowDrawer` when `!Ui.NarrowShell.Value`.
   E2: bump `_revision` only when a publish flipped. Then split the ViewEpoch key, batch the ensures, cut per-row
   node depth. Verify with the `PaneView` render figure in the log and `ops/tools/perf-tour.ps1`.
6. BUG B (§8) — scroll input, ENGINE side. First build `-Diag` and capture with
   fluent-gpu-pin\ops\diag\wavee-scroll-session.ps1 (not ops\tools\scroll-capture.ps1) during real touchpad
   flicks; read RawWheel bit 64/16 and Note 109. Run dm-probe cells E/F; flip `UseOsInertiaStopFallback` in
   fluent-gpu-combined if F confirms. Re-wire the dead `ScrollTrace.VelSample`/`Release` emit sites. Only then
   change physics: consume the pre-coalesce velocity ring, preserve velocity on re-grab, then the 8 DIP latch.
7. MEASUREMENT (§9) — PresentMon against Wavee and an installed WinUI app on the same scripted gesture; report
   input→present, not click-to-photon; state the SendInput touchpad-phase caveat.

Then re-run the full gates, update docs/plans/wavee/wavee-0.3-gap-register.md for anything you fixed or found,
and report what is verified, what is not, and what you left out and why. Nothing has been committed all day;
ask before committing.
```
