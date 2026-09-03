# Artist page album expander — deterministic, compact, snappy

## Context — what `expander_bug.mp4` shows and why

Clicking an album cover on the artist page opens an inline drawer under that row of cards with the album's tracks. One click in a different row produced, in order: the **old** album's tracks (rows 7–10) sitting under the new row's highlight → the whole drawer turning into a 10-row skeleton → the drawer relocating under the new row and filling in → a scroll nudge per step. Three layout jumps and two scrolls for one click, the open itself takes ~0.5 s, and the drawer is 526 DIP tall (56 header + 10×44 rows + 30 gap) — 90 % of the usable height at 1280×720.

Everything lives in `src/apps/Wavee/Features/Detail/ArtistPage.AlbumExpand.cs` (`AlbumLoader`, `AlbumDrawerPanel`, `DiscoGrid`, `DiscographySection`) plus the engine grid `..\fluent-gpu\src\FluentGpu.Controls\LazyGrid.cs`, which splits the realized rows around ONE stable `"disco-drawer"` node (`LazyGrid.cs:296-315`).

| # | Cause (verified) | Where |
|---|---|---|
| C1 | **No identity check between the loaded album and the selected card.** The panel's tracks are `_full.Loadable.Value.Value?.Tracks ?? al.Tracks ?? []` (`:508-512`, same in the `DrawerState` memo `:370-388`) — `al` is the NEW card, the resource is whatever it last held; `DrawerState.Uri` (`:317`) is never compared to `al.Uri`. And `loading = tracks.Count == 0 && pending` (`:384`) is false whenever a stale non-empty list is in hand, so the old rows render under the new title instead of a placeholder. | `ArtistPage.AlbumExpand.cs:370-388, 508-512` |
| C2 | **Two readers of one signal, no ordering.** `_expanded` (a `Signal<int>` *index*, `:293`) is subscribed by `DiscoGrid` (re-keys the `UseResource`, `:356-362`) AND by the engine's `LazyGrid` (`LazyGrid.cs:195`), which builds the drawer through frozen delegates (`drawer: DrawerFor`, `:397`). Whichever runs first wins; the card highlight (`_expanded.Peek()` in `Cell`, `:435`) always moves on the click frame while the content may lag a frame. `DiscographySection.ReplaceSnapshot` (`:579-586`) never resets `_expanded`, so late discography data can re-point the index at a different album. | |
| C3 | **The resource is never warm.** `UseResource` deps change → `Reload(keepPrevious: false)` → `Pending` with a null seed (`RenderContext.cs:1043`, `Loadable.cs:51`) even though `StoreLibrarySource.GetAlbumAsync` short-circuits an already-Open album — so a re-open of a cached album still flashes shimmer. | `ArtistPage.AlbumExpand.cs:356-362` |
| C4 | **Height is measured after the fact and the scroll chases it.** `BringExpandedIntoView` runs on `DepKey.From(drawerH, expandedIndex)` (`LazyGrid.cs:259-263`): the height moves shimmer-estimate → real rows → the effect fires again → a second glide. | |
| C5 | **Slow, untuned motion.** Outer node: `DrawerResize` = `Size` on the `ContentResize` **spring** (response 0.40 s, ζ 0.90 — `MotionTok.cs:171`), `SizeMode.Reflow`, no `Enter/Exit Active`, no `SuppressDescendantTransitions`; inner ZStack: `DrawerPresence` Dy −8 + opacity, 300 ms in / 200 ms out (`:323-334`). The detail table's own inline drawer already uses the corrected spec (`DetailTracks.cs:3197-3212`: 250/150 ms eased, `Active: true`, `SuppressDescendantTransitions: true`) and cites this drawer as its ancestor. | |
| C6 | **Geometry.** Header 56 (30-DIP Play + 32-DIP open button in a 40 row + 8/8 pad), row pitch 44 (`TrackRow.CompactListItemExtent`, 40 content), `BottomGap` 30, `RowCap` 10, always one column (`:297-312, :92-95, :182, :487-496`). 12-track album = 526 DIP at every window size. | |

Rules: derived facts on the model (a pure verdict, one reader), props freeze at mount (`Key` per album for scoped state, re-pushed `Props` for data), no legacy paths, comments explain WHY, no source-text tests. Engine untouched (all the primitives exist). Decisions taken with the user: **32-DIP rows, 40-DIP header; two track columns when the album grid has ≥ 5 columns.**

---

## 1. One pure verdict — `Features/Detail/AlbumDrawerVerdict.cs` (replaces `AlbumDrawerRows.cs`)

```csharp
/// <summary>Everything the drawer needs, decided in ONE place from plain inputs, so the panel, the reserved slot height and
/// the bring-into-view scroll can never disagree — and so the verdict is the same whichever component renders first.
/// Selected vs loaded identity is checked HERE: a loaded album that is not the selected one is simply "not loaded".</summary>
public readonly record struct DrawerVerdict(
    string Uri, int Rows, int Columns, int Shown, int Total, bool Loading, bool ReadyEmpty, bool ShowAllRow, float PanelHeight, float SlotHeight);

public static class AlbumDrawerVerdict
{
    public const float HeaderH = 40f;          // 6 pad + 28 header row + 6 pad
    public const float RowPitch = 32f;         // compact list pitch; TrackRow content 28
    public const float BottomGap = 16f;        // drawer → next card row
    public const int CapPerColumn = 12;        // 1 column: 12 rows (= 424 DIP, fits a 720p viewport with the card row above)
    public const int TwoColumnMinGridCols = 5; // ≥ 5 album columns ⇒ the drawer is ≥ ~968 DIP wide ⇒ two track columns
    public const int FallbackShimmerRows = 3;

    public static int ColumnsFor(int gridCols) => gridCols >= TwoColumnMinGridCols ? 2 : 1;

    /// <param name="selectedUri">the card the user clicked ("" = closed)</param>
    /// <param name="loadedUri">the uri of the album the resource currently holds (null = nothing)</param>
    /// <param name="loadedTracks">that album's tracks</param>
    /// <param name="thinTracks">tracks already on the discography card itself (often present, sometimes empty)</param>
    /// <param name="thinTrackCount">the card's advertised count (used to size the placeholder before the fetch lands)</param>
    public static DrawerVerdict For(string selectedUri, string? loadedUri, int loadedTracks, int thinTracks, int thinTrackCount,
                                    bool pending, int gridCols)
    {
        if (selectedUri.Length == 0) return default;
        bool match = loadedUri == selectedUri;                        // C1: identity, not "whatever is in hand"
        int have = match ? loadedTracks : thinTracks;                 // never another album's list
        bool loading = !match && pending;                             // pending for THIS uri ⇒ placeholder, even if thin rows exist
        bool readyEmpty = match && !pending && have == 0;
        int columns = ColumnsFor(gridCols);
        int cap = CapPerColumn * columns;
        int total = have > 0 ? have : Math.Max(thinTrackCount, 0);
        int shown = loading ? Math.Min(total > 0 ? total : FallbackShimmerRows, cap) : Math.Min(have, cap);
        bool showAll = !loading && total > cap;
        int rows = readyEmpty ? 2 : (int)Math.Ceiling((shown + (showAll ? 1 : 0)) / (float)columns);
        float panel = HeaderH + rows * RowPitch;
        return new(selectedUri, rows, columns, shown, total, loading, readyEmpty, showAll, panel, panel + BottomGap);
    }
}
```

- `Total` known from the card's `TrackCount` ⇒ **`SlotHeight` is final on the click frame** (C4): the placeholder and the real rows reserve the same height, so the scroll effect can key on the selection alone.
- `ShowAllRow`: when an album exceeds the cap (12 single / 24 two-column), the last row is "Show all N tracks" → `go("album", uri)`. Deterministic, counted in `Rows`.

Tests `AlbumDrawerVerdictTests` (source-included): `LoadedOtherAlbum_IsLoading_NotStale` (loaded A, selected B, pending ⇒ `Loading`, `Shown` from B's count, never A's tracks), `ThinRowsWithPendingFetch_StillPlaceholder`, `Match_Ready_Rows`, `Columns_2_At5GridCols_1_Below`, `Cap_And_ShowAllRow` (13 tracks, 1 col ⇒ 12 shown + ShowAll, rows 13; 2 col ⇒ 7 rows), `Heights_HeaderPlusRowsPlusGap`, `ReadyEmpty_TwoRows`, `Closed_IsDefault`, `PlaceholderRows_FallbackThree_WhenCountUnknown`.

## 2. Selection by uri, one reader — `DiscoGrid`

- `_expanded: Signal<int>` → `_expandedUri: Signal<string>` (the card click writes the uri; toggling compares uris). `DiscoGrid.Render` derives `expandedIndex = _vc.IndexOf(uri)` (−1 when the album left the snapshot) and hands **that** to `LazyGrid` through a `Signal<int>` it owns and writes in the same render — LazyGrid keeps its index contract untouched. `ReplaceSnapshot` needs no reset: the derivation re-points or closes.
- The verdict is the single `UseComputed` (inputs: `_expandedUri`, `_full.Loadable`, `_vc.Version`, `LazyGrid`'s reported `cols` — the grid already calls `onVisibleRangeChanged`; add `onColumnsChanged` or read `cols` from the `GridDrawerInfo` the drawer delegate receives (`LazyGrid.cs:307`, `info.Columns`) — use the latter, no engine change). `DrawerFor`, `DrawerHeight` and the panel all read the verdict; the `?? al.Tracks` chain and `DrawerState` are deleted.
- **Warm open (C3)**: `AlbumLoader.Peek(svc, uri)` — a synchronous store read (`StoreLibrarySource` already has `_store.GetAlbum(uri)` behind `GetAlbumAsync`; expose `TryPeekAlbum(uri, out Album?)` returning it only when hydration ≥ Open) — passed as the `UseResource` **seed** for the new uri, so a cached album is `Ready` on the click frame with zero shimmer; a cold one shows `Shown` placeholder rows sized by `TrackCount`.

## 3. Geometry — `AlbumDrawerPanel`

- Panel padding `12 / 6 / 12 / 6`; header row `Height = 28` (Play circle **26**, glyph 11; title 13/600 one line ellipsis; `AlbumNavAction` **28**); `Gap = Spacing.S`.
- Rows: `TrackRow.Grid(..., rowH: 28, ...)` in a **32-DIP pitch** (`AlbumDrawerVerdict.RowPitch`); columns `# 26 · ♥ · title ★ · time 44 · … 32`; shimmer row same pitch.
- **Two columns** when `verdict.Columns == 2`: `BoxEl { Direction = 0, Gap = Spacing.XL }` of two `BoxEl { Direction = 1, Grow = 1, Basis = 0, MinWidth = 0 }` columns, tracks **column-major** (1…⌈n/2⌉ left, rest right — numbered tracks read down, then across). Rows are plain keyed elements (≤ 24, no virtualisation; the per-album `Key = "drawer:" + uri` still scopes selection/swipe state). Single column keeps the same builder with one column.
- "Show all N tracks" row (`verdict.ShowAllRow`): a `HyperlinkButton`-styled row in the last slot → `_go("album", uri)`.
- Outer slot: `Height = verdict.SlotHeight`, connector bar unchanged (3 DIP accent, column-aligned).

## 4. Motion — snappy and single-purpose

```csharp
// Outer "disco-drawer" node — the ONLY geometry animation. 200 ms in, 150 ms out, eased; Reflow so the card rows below
// travel with it; SuppressDescendantTransitions so content landing mid-open cannot start a second wave (C5).
static readonly LayoutTransition DrawerResize = new(
    TransitionChannels.Size, TransitionDynamics.Tween(200f, Easing.SmoothOut),
    Enter: new EnterExit(Active: true), Exit: new EnterExit(Active: true),
    ExitDynamics: TransitionDynamics.Tween(150f, Easing.SmoothOut),
    Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, SuppressDescendantTransitions: true);

// Inner content — opacity only, no Dy (the drawer's own height reveal IS the movement). Same-row album switch = a
// 150 ms cross-fade of the per-album panel under the same slot; row switch = the keyed slot moves instantly to the
// new row (Position is deliberately NOT a channel) and its height tweens 200 ms.
static readonly LayoutTransition DrawerPresence = new(
    TransitionChannels.Opacity, TransitionDynamics.Tween(150f, Easing.EaseInOut),
    Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
    ExitDynamics: TransitionDynamics.Tween(100f, Easing.EaseInOut));
```

- Rows do not stagger (they never did; a stagger would push the "fully readable" moment past 200 ms).
- `LazyGrid` bring-into-view: `DepKey.From(expandedIndex)` only — the height is final on the click frame (§1), so one glide per click; `expandedRevealPeek` = `HeaderH + 2 × RowPitch` = 104. (One-line change in `LazyGrid.cs:262`; if the user prefers zero engine edits, `DiscoGrid` can pass a `drawerHeight` that is already final, which makes the existing key stable anyway — the plan takes the one-line engine edit so the contract is explicit.)
- Reduced motion: both specs collapse to 0 ms (engine policy), rects identical.

**Budget:** click → drawer at final height and readable in **200 ms** (was ~500); same-row switch **150 ms**; close **150 ms**.

## 5. Files

| File | Change |
|---|---|
| NEW `Features/Detail/AlbumDrawerVerdict.cs` | §1 (replaces `AlbumDrawerRows.cs`, deleted) |
| `Features/Detail/ArtistPage.AlbumExpand.cs` | §2 selection by uri + verdict memo + warm seed; §3 panel geometry/two columns/show-all; §4 specs; delete `DrawerState`, `PanelHeight/DrawerHeight` math, `RowCap`, `NoteRows`, `DrawerHeaderH`, `BottomGap`, `DrawerRowContentH` |
| `Backend/Library/StoreLibrarySource.cs` (+ its interface) | `TryPeekAlbum(uri, out Album?)` synchronous, Open-or-better only |
| `..\fluent-gpu\src\FluentGpu.Controls\LazyGrid.cs:262` | scroll effect keyed on `expandedIndex` only (engine gate `AnimSuite lazy-grid` unchanged — `MinRevealTarget` is untouched) |
| `Wavee.Tests/AlbumDrawerVerdictTests.cs` (new), `AlbumDrawerRowsTests.cs` (deleted), `Wavee.Tests.csproj` include | |
| `CHANGELOG.md` `[0.2.7]` Changed | "**The artist page's album drawer is compact and instant.** 32-DIP rows under a 40-DIP header (was 44 under 56), two track columns on wide windows so a whole album fits without scrolling, a "Show all" row past 12 / 24 tracks, and one 200 ms open — no more skeleton flash for an album you already opened, no more previous album's tracks under the new cover, no more drawer hopping between rows while the page scrolls after it. (#H)" |

## 6. Sequencing

| Lane | Files (disjoint) |
|---|---|
| A (Sonnet) | `AlbumDrawerVerdict.cs`, tests, csproj include, delete `AlbumDrawerRows*.cs` |
| B (Sonnet) | `ArtistPage.AlbumExpand.cs`, `StoreLibrarySource.cs` (+ interface) — codes against §1's record |
| C (Sonnet, engine) | `LazyGrid.cs:262` + engine gates (`--suite anim`) |
| orchestrator | CHANGELOG (issue #H, approval-gated), builds/tests/captures |

## 7. Verification

- App Debug + Release clean; `Wavee.Tests` green (+ verdict tests); engine Debug/Release + `VerticalSlice --suite anim` (lazy-grid gates) green.
- Real session captures (`Drive-WaveeWindow.ps1`): artist with ≥ 10 albums at 1500×900 (6 columns) and 1280×720 (5 columns). Sequence: click card in row 1 → `-Out` at +50 ms and +300 ms (drawer at final height in the second, tracks in two columns); click a card in row 2 → captures at +50/+300 ms: no frame shows the first album's tracks under the second cover, the slot height equals `40 + rows×32 + 16`, exactly one scroll change; click the same card → closed in ≤150 ms. Re-open the first album → rows visible on the very next frame (no shimmer). A 25-track album shows 24 + "Show all 25 tracks". Window narrower than 5 columns → one track column. Reduced motion → same rects, no fades.

---

## v2 — after `opening_bug.mp4` (2026-09-02, same day)

The compact/fast drawer was still "unclear where it opens, still jumpy". Frames showed two remaining causes and one design gap:

- **Section-level reflow.** The facet section's stock `Expander` applies its 333 ms disclosure `Reflow` to EVERY height change of its content, so each drawer open/close also played a section-height tween. → engine `ExpanderOptions.AnimateContentResize = false` (disclosure motion only while toggling); `DiscographySection` passes it.
- **Scroll target sampled mid-flight.** `LazyGrid.Geometry()` used `AbsoluteRect`, which folds in-flight compositor transforms (the Expander host's FLIP, the drawer's own `SizeMode.Reflow`), so the post-layout sample could be skewed on the click frame and once left the clicked card under the sticky band. → engine `SceneStore.AbsoluteLayoutRect` (bounds only) for `sectionTop`; scroll targets are content-space = layout-space.
- **Design (user's choice: "full width + always scroll the row to the top").** `LazyGrid` gains `ExpandedReveal { Minimal, AlignTop }` + `LazyGridMath.AlignRowTarget(currentOffset, viewportH, cardTop, topInset)` = `max(0, cardTop − inset)`; `DiscoGrid` passes `AlignTop`, so the clicked row always lands directly under the sticky band. The drawer gets a **16×8 caret** at the clicked card's centre (in the slot's new 8-DIP `TopGap`; `BottomGap` 8 — `SlotHeight` unchanged) and the **album cover + the card's own meta line** in its header, so the drawer reads as "this card, this album" without hunting.
