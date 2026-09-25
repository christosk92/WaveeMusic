# Detail hero: the description leaves the identity column · The collapsed rail shows the whole library

Status: **proposed, awaiting the owner's go** (2026-09-22). Two independent parts; they touch disjoint files and can
run as two parallel subagents. The orchestrator builds and tests.

---

## Part 1 — the empty band under the cover on playlist pages

### What happens today

The row-flow hero is `[artwork | identity column]`, `AlignItems = Start` (`Entities/Detail.UI.Hero.cs:280`). The
identity column is designed to end at the cover's bottom edge, because the title's type plan is sized from the space
the cover leaves: `VerticalLayout.TitleHeightBudgetFor` = cover edge − chrome − gaps (`Entities/Detail.cs:403`). The
description is the one block **excluded** from that budget ("a tail may run past the cover", `:401-402`), yet it is
still placed **inside** the column (`Detail.UI.Hero.cs:243-249`). So every page with a description overruns the cover
by up to `3 × 18 + gap` ≈ 58 DIP, and that overrun is the empty band under the cover. Albums have no description,
which is why they look right.

```
 today (playlist)                               today (album)
 ┌─────────┐ Playlist · private                 ┌─────────┐ Single · 2023
 │         │ throwback pop 2010s tuesday        │         │ That Summer
 │  cover  │ night                              │  cover  │ ──
 │         │ ──  (S)                            │         │ SHAUN
 │         │ 50 songs · 18.8M saves · 3 hr      │         │ 1 song · 3 min · 2023
 │         │ 02:44:10 Next update at 00:56      │         │ [▶ Play] ⤮ ♥ ↗ ⋯
 └─────────┘ [▶ Play] ⤮ ♥ ↗ ⋯                   └─────────┘
 ░░░░░░░░░░░ Here's some throwback pop, …   ◄── the band
 ░░░░░░░░░░░ inspired by your listening …
```

### The change

The description becomes a **sibling of the hero row**, placed after it inside the padded hero box, in BOTH flows. The
tree is then identical for stacked and row flow, so the reflow stays one gesture with no reparenting. In row flow it
spans the full hero width, capped at a reading measure. In stacked flow it keeps the content width it has today.

```
 after (row flow)
 ┌─────────┐ Playlist · private
 │         │ throwback pop 2010s tuesday night      ← the title plan already fills exactly
 │  cover  │ ──  (S)                                  the cover height (unchanged math)
 │         │ 50 songs · 18.8M saves · 3 hr 4 min
 │         │ 02:44:10 Next update at 00:56
 └─────────┘ [▶ Play] ⤮ ♥ ↗ ⋯
 Here's some throwback pop, 2010s, high-spirited, summer camp, nostalgia, birthday — inspired by your
 listening on Tuesdays at night                                         (≤ DescriptionWMax, 2 lines + more)
 ⇅ Custom order   ⋮ Default   ☰ Select   ⋯                                                    ⌕  ⚲  ▥
```

Component tree (`HeroHost.Render`):

```
expanded (Direction 1, OnBoundsChanged = _measure)
├─ padded box (Padding pad,pad,pad,HeroBottomPad; Direction 1; Gap 0)
│  ├─ hero   (row|column)  [artwork, identity]           ← identity no longer holds the description
│  └─ Block("hero-description", description)             ← NEW position; Margin top = DescriptionGapFor(...)
└─ toolbar row
```

Because the title budget already excludes the description, **the identity column now ends at the cover edge by
construction**. No other block moves.

### Code

`src/apps/Wavee/Entities/Detail.cs`, `VerticalLayout`:

```csharp
/// <summary>The description's widest reading measure in row flow (≈ 100 characters at 13 px). It spans under the
/// cover AND the column, so the cap is on reading comfort, not on the column.</summary>
public const float DescriptionWMax = 720f;
/// <summary>Space between the hero row (or stacked column) and the description beneath it.</summary>
public const float DescriptionGapRow = 12f;

/// <summary>Row flow: two lines at the wide measure hold what three held in the column; stacked keeps four.</summary>
public static int DescriptionMaxLines(bool rowFlow) => rowFlow ? 2 : 4;

/// <summary>The description's measure: row flow spans the padded hero width (capped); stacked keeps the content
/// width the identity column already uses.</summary>
public static float DescriptionWidthFor(float colW, bool rowFlow)
    => rowFlow ? MathF.Min(DescriptionWMax, MathF.Max(ContentWMin, colW - 2f * HeroPadFor(colW, rowFlow)))
               : ContentWidthFor(colW, rowFlow);

/// <summary>The gap above the description: stacked keeps the identity rhythm it had inside the column.</summary>
public static float DescriptionGapFor(bool rowFlow) => rowFlow ? DescriptionGapRow : IdentityGap;

/// <summary>The description band's full height (gap + lines), 0 when there is none.</summary>
public static float DescriptionBandHeight(bool rowFlow, bool description)
    => description ? DescriptionGapFor(rowFlow) + DescriptionMaxLines(rowFlow) * DescriptionLineHeight : 0f;
```

- `IdentityChrome(...)`: **drop the `description` term entirely.** Remove the parameter and fix every call site;
  per "no legacy paths", no overload is kept. The column never contains it now.
- `IdentityHeightFor` / `IdentityGapFor`: lose the `description` parameter the same way. The slack spread in
  `IdentityGapFor` then measures the real column.
- `HeroBandHeight(colW, rowFlow, title, …, description, …)`:
  ```csharp
  float hero = rowFlow ? MathF.Max(art, identity) : art + HeroGapFor(w, rowFlow) + identity;
  return pad + hero + DescriptionBandHeight(rowFlow, description) + HeroBottomPad
       + ExpandedToolbarTopPad + ToolbarRowHeight + ExpandedToolbarBottomPad;
  ```

`src/apps/Wavee/Entities/Detail.UI.Hero.cs`:

- `HeroHost.Render`: build `description` with `VerticalLayout.DescriptionWidthFor(bw, rowFlow)` in place of
  `contentW`, for both `editDescription(...)` and `Controls.ExpandableRichText(...)`. **Do not** add it to `blocks`.
  Then:
  ```csharp
  Element heroBox = new BoxEl
  {
      Direction = 1, Animate = HeroReflowMotion,
      Padding = new Edges4(pad, pad, pad, VerticalLayout.HeroBottomPad),
      Children = description is null
          ? [hero]
          : [hero, Block("hero-description", description, late: true) with
                { Margin = new Edges4(0f, VerticalLayout.DescriptionGapFor(rowFlow), 0f, 0f) }],
  };
  ```
  `identityGap` is computed without the description (the new `IdentityGapFor` signature).
- `HeroSkeleton`: the same move. The description `Lines(...)` bar group leaves `blocks` and becomes the second child
  of the padded box, at `DescriptionWidthFor` × `DescriptionMaxLines(rowFlow)` with the same top margin. The
  skeleton's `MinHeight` already calls `HeroBandHeight`, which now includes the band. The D49 rule ("the three
  numbers must agree") holds by construction: all three read `DescriptionBandHeight`.
- `FlagsOf` is unchanged; `Description` still gates presence.

### Tests (`src/apps/Wavee.Tests`)

- `DetailHeroWhitespaceTests`: update the "dropping the description costs exactly its block" facts. In row flow the
  cost is now `DescriptionBandHeight(true, true)` on the band, and **zero** on the identity column.
- New `DetailHeroDescriptionTests` (pure `VerticalLayout`):
  - `Row_flow_identity_never_outgrows_the_cover_when_a_description_is_present`: sweep widths 424 to 1400 with
    playlist flags (eyebrow, attribution, meta, pulse, description). `IdentityHeightFor(plan…) ≤ ArtworkFor + 0.5`
    for a representative title set (short, long, CJK).
  - `Band_height_is_cover_plus_description_band_in_row_flow`.
  - `Description_measure_is_capped_and_never_below_ContentWMin`.
  - `Stacked_flow_description_band_equals_the_old_in_column_block` (the gap and line count are unchanged there).
- Grep the other `VerticalLayout` consumers for the removed `description` parameter (`ArtistHeroLayoutTests`,
  `ContextBandLayoutTests`, `DetailCoverStabilityTests`, the artist hero if it shares `IdentityChrome`) and fix
  compile fallout.

### Live check

Open a daylist, an ordinary playlist with a long description, an album, and a show (a show's long description gains
the most from the wide measure). The cover bottom should line up with the Play row. The collapse (`hero.measured`) is
still smooth, and expanding "more" on the description grows the band without a jump.

---

## Part 2 — the collapsed (56-DIP) rail shows only part of the library

### What happens today

The rail is **planned with hard caps and rendered unvirtualized** (`Shell/Sidebar.cs:1566-1583`,
`Shell/Sidebar.UI.Rail.cs:24-86`):

| Cap | Value | Effect in the screenshot |
|---|---|---|
| `RailEntityListCap` | 20 | Library V3's list stops at the 20th entry ("90's Nederlandstalig"). League of Legends, Sacrifice, vaultboy and everything after never get a tile. |
| `RailTreeCap` / `RailTreeTiles` | 20 (`Sidebar.UI.cs:502, 926`) | Classic's rootlist shows 20 top-level playlists. |
| `RailPinnedCap` | 8 | A 9th pin has no tile. |
| `RailJumpBackInCap` | 4 | Recents. This one is intentional and stays. |
| `RailTileCap` | 40 | A global ceiling across all sections. |

The caps exist only because `Rail.Build` returns one `BoxEl` with every tile as a child inside a plain `ScrollView`
("Not virtualized: bounded by construction"). The expanded pane does not have this limit: it renders the same
document through `ItemsView.CreateBound` + `PaneSlot` with a `RepeatLayout.Extents` layout, and virtualizes a
10k-entry list.

### The change

**The rail is a virtualized list over an uncapped rail plan**, built the same way as the expanded pane: one bound
slot per plan row, recycled by content type. Library sections plan every tile. Jump Back In keeps its 4, and a
section's own `MaxItems` is still honoured. The rail's head and footer become fixed chrome **outside** the scroller.
A 10k-entry rail then keeps the "+", Expand and layout controls reachable, where today they scroll away at the bottom
of the strip.

```
 56-DIP rail (after)
 ┌────┐
 │ ⌂  │  RailHead (V3 nav tiles)        ← fixed, not scrolled
 │ ── │
 │ ▢  │  ┐
 │ ◯  │  │ ItemsView.CreateBound over RailPlan.Rows
 │ ▢  │  │ pitch: tile 40 + gap 6 = 46; divider 13
 │ …  │  │ virtualized: realizes only what's visible + overscan
 │ ▢  │  ┘ (the whole library, same order/filter/sort as the expanded list)
 │ ── │
 │ ＋  │  RailFooter (+, Expand) + layout menu   ← fixed, not scrolled
 └────┘
```

Component tree:

```
RailHost (re-renders on _railVersion, culture, binder epoch, rail-head epoch; NOT on route)
└─ BoxEl column (Grow 1, AlignItems Center)
   ├─ head?  + Divider
   ├─ ItemsView.CreateBound(RailPlan.Rows.Count, scope => Embed.Comp(() => new RailSlot(owner, scope)), _railLayout, …)
   └─ Divider + footer?  (+ Divider + LayoutMenu.Button)
RailSlot (NO hooks, like PaneSlot) — reads scope.Index + SubscribeRailEpoch(index); draws Divider | EntryTile |
         FolderTile | RouteTile | SectionTile via the existing Rail.Tile(...)
```

### Code

`src/apps/Wavee/Shell/Sidebar.cs`, `SidebarRowPlanner`:

```csharp
// Removed: RailTileCap, RailPinnedCap, RailEntityListCap, and SidebarProjectionInput.RailTreeCap.
// The rail is virtualized (Sidebar.UI.Rail.cs), so a library section tiles in full like the pane lists it;
// DynamicSectionRowCap stays the one finite guard, exactly as for the pane.
public const int RailJumpBackInCap = 4;   // recents are a glance, not a list — intentional

static bool AddTile(ref PlanState st, in SidebarRow row) { Add(ref st, row); return true; }
```

- `RailFrom(s, input.Pins, Cap(s, int.MaxValue) …)` for Pinned: `Cap` applies only a section's own `MaxItems`.
  `RailEntityList` uses `s.Opts.MaxItems > 0 ? MaxItems : DynamicSectionRowCap`. `RailTree` uses
  `DynamicSectionRowCap`.
- Drop the `ref int tiles` threading and every `if (!AddTile(...)) { RemoveAt; return; }` orphan-cleanup branch,
  because `AddTile` can no longer refuse. Simplify rather than keep dead arms (no legacy paths).
- `Sidebar.UI.cs`: delete `RailTreeTiles` and the `input with { RailTreeCap = … }` line (`:925-926`).

`src/apps/Wavee/Shell/Sidebar.UI.Rail.cs`:

- `Rail.Build(owner, plan)` becomes `Rail.Frame(owner)`: head, the list, and footer, as in the tree above. The
  per-row tile builder `Tile(...)` and its helpers (`EntryTile`, `FolderTile`, `RouteTile`, `SectionTile`, `ArtTile`,
  `IconTile`, `Divider`) stay and are called from the slot.
- New `RailSlot : Component` (same contract as `PaneSlot`: no hooks, recycled by `ContentType = row kind`,
  peeks `SelectedRoutePeek`, reads `owner.RailPlan` as a plain field):
  ```csharp
  internal sealed class RailSlot(PaneView o, RowScope scope) : Component
  {
      public override Element Render()
      {
          int index = scope.Index.Value;
          _ = o.SubscribeRailEpoch(index);
          var plan = o.RailPlan;
          if ((uint)index >= (uint)plan.Rows.Count) return Nothing;
          var row = plan.Rows[index];
          if (row.Kind == SidebarRowKind.Divider) return Rail.Divider();
          if (o.SectionOf(row.SectionId) is not { } section) return Nothing;
          var tile = Rail.Tile(o, section, in row, plan.Entries, o.SelectedRoutePeek) ?? Nothing;
          // Centres the 40-DIP tile inside the 56-DIP strip — the old column's AlignItems=Center job.
          return new BoxEl { Direction = 0, Justify = FlexJustify.Center, Height = Rail.Pitch, Children = [tile] };
      }
  }
  ```
- `const float Pitch = Box + 6f` (the old column `Gap`), with a divider extent from `Rail.DividerExtent`. Rail rows
  are therefore two fixed heights. `_railLayout = RepeatLayout.Extents(RailExtentOf, estimatedExtent: Rail.Pitch)` is
  seeded analytically from the plan, like `_rowLayout`.

`src/apps/Wavee/Shell/Sidebar.UI.cs` (`PaneView`):

- Per-row rail epochs, mirroring `SubscribeRowEpoch` / `BumpChangedRowEpochs` for the rail plan. A republish
  re-skins only the rail rows whose entry changed.
- The selection sweep bumps the epochs of the old and new selected rail tiles. This replaces the `SelectedRoute`
  subscription in `RailHost`, which today re-renders the whole strip on every navigation; with a recycled list it
  would re-render every realized slot.
- `_railRowCount` signal + a `ListController` for the rail, the same way the pane feeds its `ItemsView`.
- Rail drag and drop: the tile's drop spec already reads the bound `_railDropUri` (per tile, by uri), so it works
  unchanged on recycled slots. A rail tile is never a reorder source (`ReorderOptions` stays off for the rail).
- The scroll key is `Config.ScrollKeyPrefix + ".rail"`, so the rail keeps its scroll position across collapse and
  expand.

### Tests (`src/apps/Wavee.Tests/SidebarPlannerTests.cs`)

- Replace `PinnedCapsAtEight_EntityListCapsAtTwenty_TotalCapsAtForty` with
  `Rail_tiles_every_pin_and_every_library_entry`. With 20 pins and a 50-entry library the counts are 20 and 50. With
  pins + 3 EntityLists, the total is the sum, and every `EntryIndex` stays in range.
- `JumpBackInCapsAtFour_AndMaxItemsTightensTheCapFurther`: unchanged.
- `PinnedFolder_CountsAgainstTheSameEightTileCap_AsAnyOtherPin` becomes `PinnedFolder_tiles_like_any_other_pin` (a
  9th and 10th pin also tile).
- New `Rail_tree_tiles_every_top_level_entry` (a 200-playlist rootlist → 200 top-level tiles; nested stay folded) and
  `Rail_plan_order_matches_the_expanded_plan_for_V3` (the V3 document's rail entity order equals its pane order,
  minus chrome rows). The second one pins "the rail is the collapsed view of the same list".
- `Rail_extents_are_pitch_or_divider` (pure `RailExtentOf` over a plan).

### Live check

Collapse the pane in Library V3 with a large library and scroll the rail to the end. It should reach the same last
entry as the expanded list, and the footer "+" and Expand stay visible throughout. Switch the V3 filter chip to
Artists: the rail follows. Drop a track onto a playlist tile deep in the rail. Watch the `[wake]` / `frame.churn`
lines while scrolling the rail: slots realize and recycle, with no `ToolTip×N` churn.

---

## Execution and gates

| Agent (Sonnet) | Files |
|---|---|
| Hero | `Entities/Detail.cs` (VerticalLayout), `Entities/Detail.UI.Hero.cs`, compile fallout in other `VerticalLayout` callers, `DetailHeroWhitespaceTests.cs`, new `DetailHeroDescriptionTests.cs` |
| Rail | `Shell/Sidebar.cs` (planner), `Shell/Sidebar.UI.Rail.cs`, `Shell/Sidebar.UI.cs` (rail host/epochs/layout), `SidebarPlannerTests.cs` (+ new rail tests) |

Orchestrator: `dotnet build Wavee.slnx` Debug and Release, `dotnet test src/apps/Wavee.Tests`, then the live checks
above using the live-verification workflow. Neither part touches the engine, so no VerticalSlice gate is owed. The
sidebar skill docs (`.claude/skills/wavee-sidebar/architecture.md`, "not virtualized" / caps) and
`docs/guide/sidebar-extension-platform.md` are updated in the same change. CHANGELOG bullets need issue numbers from
the owner.
