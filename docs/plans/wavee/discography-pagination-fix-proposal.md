# Artist Discography Pagination — Diagnosis + Fix Proposal

**Status: IMPLEMENTED (2026-07-03) — all phases (1 + 2) landed in one pass, uncommitted.** Evidence: engine `FluentGpu.slnx` 0 errors + VerticalSlice ALL CHECKS PASSED (549); app `Wavee.csproj` 0 errors; `Wavee.Tests` 440/440 green incl. 17 new `DiscographyPaginationTests` (mapper totals from the maroon5 fixture, probe/no-clamp/fast-path/live-failure source semantics, and the provisional-seed reconciliation matrix — converge down, grow up, post-real-page no-op, firm-seed sticks, corrected-to-empty still bumps Version). Two implementation notes vs. the text below: `KindMatches` is `public` (cross-assembly, no InternalsVisibleTo) on `AggregateCatalog`; a namespace-local `using DiscographyPage = Wavee.Core.DiscographyPage;` alias disambiguates from the UI component of the same name in `StoreLibrarySource.cs`/`LiveSessionHost.cs`. Remaining evidence gap: on-device run against live Pathfinder (shared-hash discography ops observed on the wire).

Symptom: artist page discography sections (Albums / Singles & EPs / Compilations) always show only the top ~10 items and never more; "See all N" never appears; the dedicated DiscographyPage is unreachable.

Provenance: multi-agent research pass (recon → diagnose → adversarial per-claim verification → design → adversarial critique). Every root cause below was independently re-verified against the code; one claimed cause was **refuted** and is recorded as such. The critique's two CRITICAL amendments are folded into the design.

---

## 1. Verified root causes

### PRIMARY

**RC1 — `GetDiscographyAsync` pages over an in-memory ~10-item slice and reports that as the grand total.** `Wavee.Core/Sources/AggregateCatalog.cs:55-64` filters `artist.TopAlbums` by kind and returns `new DiscographyPage(items, filtered.Count)`. `TopAlbums` only ever holds what the one-shot `queryArtistOverview` response carried (~10 per facet). The "pagination" is a fake window over that slice. Smoking gun in the fixture `Wavee/assets/spotify/artist-maroon5.json`: `discography.albums` has `items=10, totalCount=18`; `singles` has `items=10, totalCount=46` — we show 10, never 18/46.

**RC2 — The mapper discards `discography.<facet>.totalCount` and flattens each release-group to its first release.** `Wavee.Core/Spotify/SpotifyExportMapper.cs` — `MapArtist` (987-989) reads only `discography.{albums,compilations,singles}.items`; a file-wide grep confirms the sibling `totalCount` is never read. `AddReleases` (1033-1041) takes `releases.items[0]` per group (consistent with the reference client's "one row per group"; fine, keep it — `totalCount` is a *group* count so rows align).

### CONTRIBUTING

**RC3 — `Artist` domain model has no per-facet total field.** `Wavee.Core/Domain/Models.cs:56-74` — nowhere to carry `totalCount` even if the mapper read it.

**RC4 — No paginated discography API operation is wired anywhere, and `GetDiscographyAsync` is not on `ICatalogSource`.** `ICatalogSource.cs:23-49` exposes only `GetArtistAsync`; `GetDiscographyAsync` lives only on `IMusicLibrary` (`Library.cs:84`) with `AggregateCatalog` its sole implementation — so the live source (`StoreLibrarySource`) *cannot* override discography paging. No `queryArtistDiscography*` Pathfinder call exists (grep: zero). `FakeData.Discography` (`FakeData.cs:106`) synthesizes large facets but has **zero call sites** — dead code that was apparently meant to be the real path.

### LATENT (will matter once totals are real)

**RC5 — "See all N" gate is `total > 50`** (`ArtistPage.AlbumExpand.cs:476,486,493`; `PreviewCap=50` at `DiscographyPage.cs:20`). Since `total` is today the ~10-item slice count, the button never renders and `DiscographyPage` is unreachable. Starts behaving correctly the moment the true facet total flows.

**RC6 — Pure offset paging, no id-based dedup.** `VirtualCollection<Album>` (`src/FluentGpu.Controls/VirtualCollection.cs`) windows purely by offset with page-level request dedup only. Cross-page duplicate/missing items on mid-session catalog mutation is an **accepted low-risk tradeoff** (the reference client behaves the same); avoid making it worse by never splitting one page across two sources (see §3.6). Optional hardening later: dedup by album `Uri` when materializing the visible window.

### REFUTED (do not "fix")

~~"DiscographyPage reuses the capped VC and seeks to index 50 of a 10-item collection → empty page."~~ Wrong: `DiscographyPage` builds its own `_vc` (`DiscographyPage.cs:72`), `initialIndex` only triggers a one-time scroll guarded by `count > _initialIndex` (`LazyGrid.cs:138`), and the scroll target is clamped to content bounds (`LazyGrid.cs:226`). No empty-page mechanism exists here.

## 2. Key facts the design rests on

- **The total is already in the very first response.** `data.artistUnion.discography.<facet>.totalCount` arrives with the overview (maroon5: albums 18, singles 46, compilations 2, appearsOn 811) alongside the first ~10 items. Shimmer-up-to-N needs no extra round-trip — the number is simply dropped at `MapArtist` today.
- **The UI paging machinery is already correct and starved, not missing.** `DiscoGrid` renders `Placeholder(cardW)` shimmer cells for every `_vc[idx] is null` slot and calls `_vc.EnsureRange(first, last)` every render from live scroll geometry (`overscanRows: 2`); `VirtualCollection` (pageSize 60) handles chunked storage, request dedup, and clears the in-flight guard on a thrown fetch (retry on next re-window). It pages correctly the moment `GetDiscographyAsync` returns a real total + real windows.
- **The API contract (from the reference client, `C:\WAVEE\WaveeMusic` — contract only, no code copied):** four Pathfinder ops `queryArtistDiscography{All,Albums,Singles,Compilations}` **all share one persisted hash** `5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599`; the server disambiguates by `operationName` (getting this wrong returns the wrong facet). Variables: `uri`, `offset`, `limit`, `order:"DATE_DESC"` (no `preReleaseV2` — that's overview-only). Platform: `Platform.Desktop` (`app-platform: Win32_x86_64`; discography is not a WebPlayer-surface op). Response shape mirrors the overview: `data.artistUnion.discography.<facet>.{items,totalCount}`, items are release-groups wrapping `releases.items[]`.
- **`VirtualCollection.Fill` only calls `SetCount` when `_count < 0`** (`VirtualCollection.cs:158-174`). This is the critical constraint: once a count is seeded, a live page's differing total can never correct it (critique defect A). It also means the un-seeded path is self-correcting: `LazyGrid` bootstraps page 0 at `totalRows==0` (`LazyGrid.cs:141-145`) and learns the true total from the first page.

## 3. The design

Data-layer fix; the UI needs almost nothing. Ship in **two phases** — Phase 1 is complete and correct on its own; Phase 2 adds instant shimmer-up-to-N and carries the one genuinely new hazard, so it lands separately with its reconciliation fix and test.

### Phase 1 — real totals + real paged endpoint (fixes RC1–RC5)

**3.1 Model (`Wavee.Core/Domain/Models.cs`).** Add to `Artist`, inserted **before** `FetchedAt` (all defaulted; no caller passes `FetchedAt` positionally — verified): `int AlbumsTotal = 0, int SinglesTotal = 0, int CompilationsTotal = 0` (0 = unknown → use in-memory count). Add `FacetTotal(this Artist, DiscographyKind)` switch helper. STJ source-gen (`CachedStore.EntityJson`) picks the new fields up automatically; old cached JSON deserializes via defaults.

**3.2 Mapper (`SpotifyExportMapper.cs`).** `MapArtist`: read `discography.<facet>.totalCount` (×3) into the new fields. Add `DiscographyPageFromResponse(JsonElement root, DiscographyKind kind)`: dig `data.artistUnion.discography.<facet>`, reuse the existing `AddReleases` flattening, `Total = max(totalCount, items.Count)`. **JSON stays `JsonDocument` + hand-walk** — no `JsonSerializer`, no new `[JsonSerializable]` context (AOT/trimmer convention).

**3.3 Store merge (`Wavee/Backend/Store.cs`, `StoreEntityMerge.Artist`).** Preserve totals across thin writes, mirroring `MonthlyListeners`: `AlbumsTotal = incoming.AlbumsTotal > 0 ? incoming.AlbumsTotal : current.AlbumsTotal` (×3).

**3.4 Pathfinder ops (`Wavee/SpotifyLive/PathfinderClient.cs`).** Three op-name consts (`queryArtistDiscographyAlbums/Singles/Compilations`) + the one shared hash const. Live fetch in `LiveSessionHost.cs`, mirroring the `LiveSearch` delegate pattern: `FetchDiscographyPageAsync(pf, uri, kind, offset, limit, ct)` → `pf.QueryAsync(op, hash, w => { uri; offset; limit; order:"DATE_DESC"; }, Platform.Desktop, ct)` → `DiscographyPageFromResponse`. Wire `libSrc.LiveDiscography = ...` in the real-source block. 429/503 backoff already lives in `PathfinderClient.SendWithRetryAsync`.

**3.5 Source seam (fixes RC4).**
- `ICatalogSource`: add `GetDiscographyAsync(uri, kind, offset, limit, ct)` with a default interface impl that serves from the overview slice — correct for any non-paging source (fake/export/local). The default must define **`Window(limit <= 0) → empty`** so a probe routed to a non-overriding source returns `(empty, total)` and never materializes the whole list as a bogus page.
- Shared kind filter **must reuse the existing `AggregateCatalog.KindMatches` semantics** (Singles ⇒ Single OR EP) — a diverging helper makes the offline count disagree with the live `singles` facet grouping (which includes EPs, per the fixture).
- `AggregateCatalog.GetDiscographyAsync`: replace the slicing body with owning-source routing (mirrors `GetArtistAsync`): `foreach (s in _reg.CatalogSources) if (s.Owns(uri)) return await s.GetDiscographyAsync(...)`.
- `StoreLibrarySource`: add `LiveDiscography` delegate property + the real override:
  1. `artist = await GetArtistAsync(uri, ct)` (cached; ensures overview fetched);
  2. `filtered = KindMatches`-filter of `TopAlbums`; `facet = artist.FacetTotal(kind)`;
  3. deliverable `total = (LiveDiscography != null && facet > 0) ? Math.Max(facet, filtered.Count) : filtered.Count` — deliverability lives in the *source*: offline can only promise what it holds, so no permanent trailing shimmer in export/demo mode;
  4. `limit <= 0` → **total-only probe**: `(empty, total)`, no network;
  5. `filtered.Count >= total || LiveDiscography == null` → serve the window from memory, zero network (the exactly-10-items facet case);
  6. else call `LiveDiscography(uri, kind, offset, limit, ct)`; `null` → throw (VC clears the page guard → retries on next user-driven `EnsureRange`); otherwise **return the live page with its own `Total` untouched** — ⚠️ do **not** `Math.Max(page.Total, cachedTotal)`: clamping the fresh authoritative total up to a stale cached one freezes trailing shimmer when the facet shrank (critique CRITICAL #2). The cached facet total is a *pre-fetch estimate only*, never an override of a delivered page.

**3.6 One source per page, no stitching.** The live endpoint owns every requested window including page 0; overview items are never stitched into a live page and items are never seeded. Both surfaces are `DATE_DESC` release-group-indexed, so offsets are consistent; because a single page is never split across sources, the overview-vs-paged dedup hazard cannot occur. (RC6's residual — cross-page drift on mid-session catalog mutation — remains an accepted tradeoff, stated honestly, not "eliminated".)

**3.7 Cancellation (`ArtistPage.AlbumExpand.cs` `DiscoVc.Make`).** Today it passes `ct: default` — a latent leak. Add a `CancellationToken` parameter threaded into the `VirtualCollection` ctor; owning components hold a CTS created once per component and cancelled via `Reactive.OnCleanup` (the `HomePage`/`LyricsTicker` house pattern). A cancelled fetch throws OCE, which `VirtualCollection.Await` catches generically and clears the guard — a remounted fresh VC refetches cleanly.

**Phase 1 works end-to-end with no other UI change:** `LazyGrid` bootstraps page 0 at `totalRows==0`, `VirtualCollection` learns the true total from that page's `Total` (`_count < 0` → `SetCount`), the grid re-windows to N, shimmers render for unloaded slots, scroll fills pages of 60. "See all N" appears whenever the true facet total > 50 (RC5 fixed). The only gap vs. the full requirement: shimmers appear after the page-0 round-trip rather than instantly.

### Phase 2 — instant shimmer-up-to-N (the seed), with its reconciliation fix

The probe/seed is **additive, not load-bearing** — land it only with both critique CRITICALs addressed:

- **Seed shape.** On mount, `DiscographySection` / `DiscographyPage` probe `GetDiscographyAsync(uri, kind, 0, 0, ct)` (resolves same-tick from the cached artist — overview totals are in memory because the page header needed the artist) and seed the expected N so shimmers render immediately. `ArtistPage` already holds `a` — pass `a.AlbumsTotal`/`a.SinglesTotal` into the two sections.
- **CRITICAL — total reconciliation.** `VirtualCollection.Fill` only `SetCount`s when `_count < 0`, so a seeded count is frozen and a live page reporting `M ≠ N` can never correct it: seed-too-high → permanent trailing shimmer; seed-too-low → `Fill` truncates the chunk to `min(pageSize, _count-offset)` → items permanently unreachable. Fix one of:
  - **(a) preferred:** teach `Seed()` to mark the count *provisional*; `Fill` calls `SetCount` (re-sizing `_chunks`/`_requested`) whenever a real page reports a different total, then clears the provisional flag; or
  - **(b) zero-VC-change:** don't seed the VC count at all — keep a UI-side `expectedN` used purely to render placeholder cells while `count == 0`; the VC still learns the authoritative total from page 0.
- **Seed latch.** `Seed()` calls `Bump()`, and the render bodies subscribe to `_vc.Version.Value` — an unguarded `Seed` in `Render` is an infinite Bump→Version→re-render→Seed loop. One-shot `bool _seeded` latch; a cancelled/failed probe must leave `_seeded == false` so a remount retries.
- **Regression test (the exact defect an unamended design would ship):** seed total=N, live page returns total=M≠N with M items; assert convergence to M — no null slots below M, no unreachable items above `min(M,N)`.

### UX flow (end state)

1. Land on artist → totals already cached → N shimmer cells instantly (Phase 2) or after one page-0 round-trip (Phase 1).
2. `EnsureRange` fills pages of 60 as scroll geometry demands; each landed page bumps `Version`, real `MediaCard.GridCard`s replace shimmers.
3. Near-end trigger stays the house pattern: render-time geometry read + `EnsureRange` request dedup, `overscanRows: 2`. No `OnScrollNearEnd` event — the engine has none by design.
4. Failed page → throw → guard cleared → retry only on the next user-driven re-window (no hot spin against a rate-limited endpoint); the total survives, shimmers persist.
5. Facet with exactly ≤10 (== overview window): memory fast-path, zero network, no shimmer, no See-all. Maroon 5: 18 albums / 46 singles → shimmer to N, one 60-item fetch fills; See-all appears only past 50.

## 4. File-by-file plan

| Phase | File | Change | ~Size |
|---|---|---|---|
| 1 | `Wavee.Core/Domain/Models.cs` | 3 total fields on `Artist` (before `FetchedAt`) + `FacetTotal` helper | 6 |
| 1 | `Wavee.Core/Spotify/SpotifyExportMapper.cs` | read `totalCount` ×3 in `MapArtist`; add `DiscographyPageFromResponse` | 20 |
| 1 | `Wavee/Backend/Store.cs` | merge-preserve totals (mirror `MonthlyListeners`) | 3 |
| 1 | `Wavee/SpotifyLive/PathfinderClient.cs` | 3 op consts + shared hash | 5 |
| 1 | `Wavee/SpotifyLive/LiveSessionHost.cs` | `FetchDiscographyPageAsync` + wire `libSrc.LiveDiscography` | 25 |
| 1 | `Wavee.Core/Sources/ICatalogSource.cs` | add `GetDiscographyAsync` DIM (overview-slice; `Window(limit≤0)=empty`); shared `KindMatches`-based filter | 20 |
| 1 | `Wavee.Core/Sources/AggregateCatalog.cs` | route to owning source | 6 |
| 1 | `Wavee/Backend/Library/StoreLibrarySource.cs` | `LiveDiscography` + override (probe/fast-path/live, no `Math.Max` clamp) | 30 |
| 1 | `Wavee/Features/Detail/ArtistPage.AlbumExpand.cs` | thread `ct` through `DiscoVc.Make`; CTS + `OnCleanup` | 10 |
| 2 | `src/FluentGpu.Controls/VirtualCollection.cs` | provisional-count `Seed` + `Fill` reconciliation (option a) | 15 |
| 2 | `ArtistPage.AlbumExpand.cs` / `ArtistPage.cs` / `DiscographyPage.cs` | probe + latched seed; pass facet totals into sections | 25 |
| 1+2 | `Wavee.Tests/` | mapper totals (assert 18/46/2 from `artist-maroon5.json`); `DiscographyPageFromResponse`; routing; probe returns `(empty,total)` no-fetch; offline total == in-memory; Phase 2 seed/total-mismatch convergence | 70 |

No new files required. `FakeData.Discography` stays dead (removable). Do **not** bother setting synthetic totals on `FakeData` artists — the DIM default computes `total = filtered.Count`, so the fake path never shimmers regardless (critique: that change is dead).

## 5. Explicitly out / do-not

- **No WaveeMusic code copying** (its `DiscographyPaginationService`/placeholder-id merge-back is WinUI MVVM machinery; our `VirtualCollection` + `LazyGrid` already do that job). Reference = API contract only.
- **Never seed overview *items* into the VC** — pageSize 60 ≫ overview 10 → a partially-filled "loaded" page = permanent shimmers, plus it reopens the stitch hazard.
- **Never fabricate totals from the loaded slice** (that *is* the bug), and never clamp a delivered page's total with a cached estimate.
- **No `OnScrollNearEnd` event, no reflection JSON, no `order` variation, no `preReleaseV2` on discography ops, and never send the shared hash without the right `operationName`.**
- **Known accepted behavior:** in export/demo (no live delegate), deliverable total = in-memory count (~10 ≤ 50) → See-all/DiscographyPage unreachable offline. Net-neutral vs. today; the dedicated page is exercised only against a live session.
