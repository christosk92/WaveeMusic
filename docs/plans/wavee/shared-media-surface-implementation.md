# Shared media surface — ONE card/row/tile surface with dials — implementation plan

Status: **PLAN, awaiting owner approval** (2026-10-01). Repo `C:\wavee\waveemusic`, engine `C:\wavee\fluent-gpu`.
Issues (filed 2026-10-01, milestone 0.3 Crest, Appendix B): **#156** Browse fix (precondition) · **#157** section/Browse
grid cards · **#158** library heads · **#159** shelf double tab stop · **#160** the shared-surface umbrella (rounds 2–4).
Every CHANGELOG bullet ends ` (#n)` and every commit body carries `Fixes #n`.

This is the ONE plan for three read-only designs made on 2026-10-01, plus the all-cards sweep:

| Design | Where it lives now |
|---|---|
| Section-grid cards ("these cards have no hover states… are they hand rolled?") | folded into **wave 1** here; its adapter + screen code, generalised to `BoundSurface<T>`, is **Appendix A** |
| Library › Artists narrow heads ("`Tro…`", "`P…`") | its own file `library-reader-narrow-heads-implementation.md`, run as **track L** beside wave 0; its `Controls.TrimTip` is built ONCE, here, in wave 0c |
| One shared surface with dials ("one surface with a lot of dials and switches") | the body of this file (§0–§8) |
| Sweep ("are there more hand-rolled cards?") | §1 taxonomy + the wave lists: 27 (C) surfaces missing affordances, ~10 twins, 8 duplicate skeletons, ~470 dead lines |

## Programme at a glance

| Round | Waves (parallel agents, disjoint files) | What the owner sees after it | Engine (`..\fluent-gpu`) |
|---|---|---|---|
| 1 | **0** foundation · **1** section grid + Browse grid + skeletons · **L** library heads | section/Browse cards hover, play, "…", pill, drag, hand cursor; ONE tab stop per shelf card; library names and album titles readable at 360–720; tooltips on trimmed titles | E1, E2, E3 |
| 2 | **2** rail, queue, search rows, podcasts, video, album rows | rail "Next up" rows click/play; menus on recommendation + album trailing rows; hand cursor everywhere invokable | — |
| 3 | **3** sidebar hero / tile / collapsed rail | sidebar cards drag, focus, now-playing pill, tooltips on rail tiles | — |
| 4 | **4** delete the remaining twins, duplicate skeletons, dead code | nothing visible; the drift has nowhere left to live | — |

## Owner decisions (the default is the recommendation)

1. **`MediaRow`: merge** (§2.8). It already consumes `CardData`; its 7 call sites (`Zones.UI.cs` ×5, `Modules.UI.cs:409`,
   `Album.Page.cs:1124`) become `Controls.Surface(data, Shape.Row…)`. What it lacks (slot mode, lazy chrome, "…",
   menu attach, trim tooltip, selected skin, seed face) is exactly the drift. Alternative: keep it as a thin wrapper
   over `SurfaceHost` — two entry points stay and callers keep hand-wrapping `WithContextMenu`.
2. **Wave order: round 1 = waves 0 + 1 + L together.** Their files are disjoint (§4), the APIs they share are fixed in
   this plan, and both owner complaints land in the first build. Alternative: wave 0 alone first (one extra
   build/verify cycle; a cleaner regression read on the existing shelves and rows before anything else moves).
3. **Engine seams E1 + E2 + E3** (§6). E1/E2 are what let a card inside a `PagedShelf` learn slot mode — without them
   the section grid can still ship on the earlier app-only `SlotFocus` prop, but every shelf keeps its double tab stop.
   E3 gives `.Interactive(...)` a hand-cursor default when the box is clickable — without it each (B) surface sets
   `Cursor` by hand.
4. **Library design calls (track L)**: the artist band stacks under 720 DIP (168 tall, name up to 2 lines); the album
   head stacks under 640 (72 tall); the meta caption moves beside the circles in the WIDE arm too (a visible change
   from the W3 prototype). Each is one constant if vetoed.

## Checked in source after the designs (corrections are applied in the body)

- **`BlocksDragArm`** exists on `Element` (`Element.cs:431`), and `DragController.TryArm`'s upward walk STOPS at a
  barrier ancestor (`DragController.cs:190-206`; VerticalSlice `e5dragdrop.armblock`). So `SurfaceParts.Action(el)` =
  `BoxEl { BlocksDragArm = true }` around the trailing slot and "…" works as designed; `IconAction` needs no change
  (§8 4b is resolved).
- **Call sites**: `ShelfCard` 7 (`Browse.Cards.cs:83`, `Artist.Page.cs:1190/1279/1375`, `Episode.Reader.cs:110`,
  `Search.UI.cs:563`, `Modules.UI.cs:436`); `GridCard` 2 (`Search.UI.cs:631`, `Artist.Discography.cs:1114`); `MediaRow`
  7 (above).
- **Adapter names**: `Track.MenuOf`, `Track.DragOf`, `Episode.MenuOf` and `Track.ArtistSpans` do NOT exist. The real
  builders are `Track.Menu(IReadOnlyList<Track>, in Track.MenuOptions)` (`Track.Menu.cs:78`),
  `Episode.Menu(Episode, in Episode.MenuOptions)` (`Episode.Menu.cs:38`) and `Drag.Source(Func<object?>)`
  (`Platform/Drag.cs:211`); a track row's subtitle is whatever `Track.ArtCard` (`Track.UI.cs:1196`) renders today.
- **`RowScope`** (`SelectorVisualsBound.cs:29`) is a positional `readonly record struct` built at ONE site
  (`ItemsView.cs:1815`) with `Runtime` as an `init` property. E1 follows that pattern —
  `public IReadSignal<bool>? IsFocused { get; init; }` (null ⇒ not focused) — so no other construction breaks.
- **Context keys** are `Context<T>` declared `new(null)` and provided with `Ctx.Provide(context, value, child)`
  (`Hooks/Context.cs:476`); E2 is `public static readonly Context<RowScope?> SlotRow = new(null);` (the body's
  `ContextKey<…>` spelling is corrected below).

Every other engine/app claim below was read in source on 2026-10-01 by the design agents; what could not be verified
without a build or a run is listed in §8.

Rules baked in: **props freeze at mount** (every changing input is a re-pushed props record, a signal, or context);
**no source-text tests**; **no legacy paths** (the twins and the dead code are deleted, not kept behind a flag);
**derived facts live on the model** (a card never probes data state — the adapter reads the model, the surface renders
what it is given); **zero-alloc on frame paths** (hover/press/focus are field writes + one equality-gated signal; nothing
allocates per frame or per pointer move); **a page demands its whole model** (nothing here adds a fetch window);
**pages simple, engine patched** (the two small engine seams in §6 are made in `..\fluent-gpu`, not worked around);
**subagents never build, test or touch git** — the orchestrator runs the gates once per wave.

---

## 0. The decision in one screen

**ONE surface: `Controls.Surface(CardData data, SurfaceShape shape)` → `SurfaceHost` (one component).** What a surface
SHOWS is `CardData` (today's adapter contract, data-only equality, trampolined delegates — unchanged in spirit, extended
with the row slots). HOW it is laid out is `SurfaceShape`: a small immutable value with typed dials (layout, plate,
art edge, label budget, FAB size, menu placement, play reveal, row floor) and NAMED PRESETS (`Shape.Shelf(w)`,
`Shape.Grid`, `Shape.Video`, `Shape.Row(edge)`, `Shape.RowLarge`, `Shape.RowTile`, `Shape.RowOutline`,
`Shape.EpisodeRow`, `Shape.SidebarHero`, `Shape.SidebarTile`, `Shape.RailTile`). Everything that must never drift is
NOT a dial: hover plate, press, hand cursor, focus ring + role + keyboard invoke, lazy chrome (FAB + "…" + equalizer
only while hot or relating), tooltip-on-trimmed-title, drag/menu wiring, the selected skin, the skeleton face, the
now-playing relation. The host discovers its OWN hosting mode from context: inside a bound `ItemsView`/`PagedShelf` slot
the slot root owns invoke and focus (ONE RELEASE, ONE OWNER) and the host renders click-less and focus-less; anywhere
else it owns them itself. `ShelfCard`, `GridCard`, `MediaRow` become presets of this one host and their bodies are
deleted; the 27 hand-rolled surfaces migrate in four waves; the twins, duplicate skeletons and ~470 dead lines go.

Why this and not the alternatives (details in §7):

| Option | Why rejected |
|---|---|
| A. Keep `ShelfCard`/`GridCard`/`MediaRow` as three components and just fix each (C) site to call one of them | Three hot/cold folds, two label blocks, two plate mechanisms, eager vs lazy chrome — the drift the owner is complaining about is BETWEEN these three today (ShelfCard double tab stop; MediaRow eager overlay; GridCard no skeleton) |
| B. A bag of booleans on `CardData` (`ShowFab`, `ShowMenu`, `Hoverable`, …) | A boolean per affordance is how the 27 sites drifted: each caller flips one off. Affordances are derived from DATA presence + the shape; the only knobs are typed layout dials |
| C. Bound-native (Prop-bind) twins for the virtualized hosts | Second implementation of every affordance (the earlier plan's rejected (b)); the hook-free sidebar slots explicitly ALLOW hook-owning child components (`Sidebar.UI.Slot.cs:28-30`), so no twin is needed |
| D. One giant `MediaSurface` with every surface in the app (doors, nav tiles, hero covers, chart tiles) | Those are a different grammar (text-first door, selection-first navigator, page subject); forcing them in makes the dial set a second framework. They stay (B) and consume the pure RULES and the shared PARTS only |

---

## 1. Taxonomy — what actually exists

### 1.1 Shapes (from the 2026-10-01 inventory; every (C) site maps to exactly one)

| # | Shape | Geometry | Used by (today's code) |
|---|---|---|---|
| S1 | **Shelf card** | fixed `cardW`, plate pad 8, cover = inner (square, or `CoverAspect` wide), title 14/20 × 1, caption 12/16 × 1–2, optional meta line | `ShelfCard` users (Home/Browse/Search/Artist/Episode.Reader/Modules shelves); `Concert PromoCard` (:585); `Modules VideoCard` (:594); `Album VideoShelfCard` (:1388); `Artist VideoCard` (:1211) |
| S2 | **Grid card** | fluid width (`AspectRatio 1` cover, `ArtworkFill`), pad 8/8/8/12, title × `TitleLines`, subtitle × 1 | `GridCard` users (Search facet grid, discography); `SectionScreen.SectionCard` (:207); `Browse SectionGridHost.Cell` (:225); `Album.Pane` "Also by" tile (:487, art 96); `Sidebar GridCell` (:780, small) |
| S3 | **Video card** | S1/S2 with `CoverAspect = 16/9`, FAB visible at rest, duration in the caption | `Artist VideoCard`, `Album VideoHero` (:1312, row form) / `VideoShelfCard`, `Modules VideoCard` |
| S4 | **Media row** | horizontal: art `edge` (40/44/48/56/84) · text column (eyebrow? · title × 1–2 · subtitle · meta?) · trailing; floor 52/64/112 | `MediaRow` users (Home recents/release/cluster/episode rows, module playables); Search `HitRow` (:362) / `ArtistRow` (:677); Rail `UpNextCard`+`Track.ArtCard` (:777/:1196); Recents `EpisodeRow` (:425); Playlist `RecRow` (:1126); Library `SearchRow`/`TrackHitRow` (:625/:654); Queue rows (:525) and `NowPlayingCard` (:570); Album trailing rows (:1083-1133); Rail "About the artist" (:592, large art band); Sidebar `HeroCard` (:652) |
| S5 | **Episode row** | S4 at edge 56, title × 2, meta row (E · video · date/length), optional progress BELOW | Home `EpisodeRow` (:835); Recents `EpisodeRow`; Episode reader rows; playlist-table episode rows |
| S6 | **Rail tile** | 40×40 art (or glyph), NO label — the tooltip IS the label | Sidebar collapsed `ArtTile`/`IconTile` (:329/:298) |
| S7 | **Door** | r8 text-first card: eyebrow · title × 2 · why × 2 · foot (32 disc + meta) · ghost numeral · lead gradient | `Controls.Door` (:268) — **(B)** keeps its tree; consumes the rules |
| — | Top Result hero (`Search.UI.cs:424`), Detail hero cover (:1255/:1657), daylist hero, category/fold tiles, Charts `SettingsCard` tiles (`Zones.UI.cs:795`), library `NavRow`/`NavCard`, sidebar `EntityRow` (:165), chrome cards | **(B)** different grammar — see §4.6 |

Observations that shape the dials: a circle is DATA (`CardData.Circular`, honoured only on a square cover —
`CoverShape.IsCircular`), never a shape; a wide cover is DATA (`CoverAspect`); the row's extra slots (eyebrow, meta,
trailing, below) are DATA; what differs per SHAPE is only layout, art edge, label budget, FAB size, where the "…" goes,
whether the FAB is revealed or always on, and the row floor. That is the whole dial set.

### 1.2 Affordances

| Id | Affordance | Mechanism today (shared parts) |
|---|---|---|
| A1 | hover plate | `CardShell`'s non-hit-testable sibling (fill-only, 83 ms), or `Interactive(Interaction.X)` fill ramp on the root (rows) — TWO mechanisms today |
| A2 | press feedback | plate `PressedFill` / recipe pressed leg; rows never scale (`MediaRow` comment) |
| A3 | hand cursor | `CardPhysics` → `Cursor = Hand`; `Interactive` never sets it (engine: "clickability does NOT imply the hand", `Element.cs:219-223`) |
| A4 | focus ring · role · keyboard invoke | `Focusable + Role.Button + FocusVisualMargin` on the shell (free) or on the slot root (bound) |
| A5 | play FAB | `NowPlayingOverlay(uri, onPlay, fab, centred)` — eager in ShelfCard/MediaRow, lazy in GridCard |
| A6 | equalizer / now-playing pill | the same overlay, `RelatesTo` coarse-first; `RowSkin.Tile` also tints its stroke via `RelatesNow` |
| A7 | "…" + context menu | `MoreCorner()` (corner over the cover, `ClickRequestsContext`) + `ContextMenu.Attach` / `WithContextMenu` |
| A8 | drag source | `Draggable = DragSource(kind, payloadFactory)`; FAB/"…" carry `BlocksDragArm` |
| A9 | tooltip on trimmed text | none app-wide; `Controls.TrimTip` designed in the library plan (not yet landed) |
| A10 | selected skin | `CardData.Selected` + `SelectedAccent` → 2-DIP accent border + `FillCardDefault` (grid card; sidebar hero does the same by hand) |
| A11 | disabled / blank seed | `IsEnabled=false` + `ItemContainer.DisabledOpacity` (bound slots); skeletons: `SkeletonProxy` (ShelfCard) or hand-rolled bones (8 copies) |

### 1.3 Shape × affordance — what is USED (✓ always, ◐ when data present, – never)

| | A1 plate | A2 press | A3 hand | A4 focus/role | A5 FAB | A6 pill | A7 "…" | A8 drag | A9 trim tip | A10 selected | A11 seed |
|---|---|---|---|---|---|---|---|---|---|---|---|
| S1 shelf | ✓ | ✓ | ✓ | ✓ (slot root in a PagedShelf) | ◐ OnPlay | ✓ | ◐ Menu, corner | ◐ Drag | ✓ title | – | ✓ |
| S2 grid | ✓ | ✓ | ✓ | ✓ | ◐ | ✓ | ◐ corner | ◐ | ✓ title | ◐ (discography opened) | ✓ |
| S3 video | ✓ | ✓ | ✓ | ✓ | ✓ always | ✓ | ◐ corner | ◐ (track) | ✓ | – | ✓ |
| S4 row | ✓ | ✓ | ✓ | ✓ | ◐ (30, or 44 large) | ✓ | ◐ trailing "…" (hot-revealed) | ◐ | ✓ title | ◐ (queue current, library search) | ✓ |
| S5 episode row | ✓ | ✓ | ✓ | ✓ | ◐ 30 | ✓ | ◐ trailing | ◐ (episode = none) | ✓ | – | ✓ |
| S6 rail tile | ✓ | ✓ | ✓ | ✓ | – | ✓ (ring/pill 12) | ◐ right-click only | ◐ | ✓ ALWAYS (no label) | ✓ (2-px ring) | ✓ |

Every cell is decided by data presence (`OnPlay`, `Menu`, `Drag`, `Selected`) or by the shape; no caller flips an
affordance off. The one exception kept as data is `ShowMenu` (a `Menu` factory present but the surface explicitly
without a "…" corner, e.g. module cards) — it already exists and is honoured.

### 1.4 What is REFUSED as a dial (so this does not become a second framework)

- **Motion**: one hover rung (83 ms fill fade), one press fill, no lift/scale/elevation on any media surface
  (`Controls.Art.cs` rule 2). The door's 1-DIP lift stays the door's — it is (B).
- **Colours**: one plate table (§2.5); no per-caller fills, strokes or accents except the data-driven
  `SelectedAccent`.
- **Eager vs lazy chrome**: not a dial. Chrome mounts by `CardChromeRules.Mounted(hot, relates)` everywhere. The two
  historical failures that made the shelf/row overlays eager are fixed in the engine (subtree-scoped enter/exit,
  reveal seeded on mount — `Controls.Art.cs:204-214`), and the grid card has run lazy since.
- **FAB size / "…" placement per caller**: derived from the shape (44 on covers, 30 on row art ≤ 56, 44 on large).
- **Cursor**: never a parameter. Invokable ⇒ hand; a surface with no `OnClick` and no slot ⇒ arrow (and no role).
- **Hover plate shape, corner radius, focus margin**: from the shape (card radius + `FocusInsetBordered` for stacks;
  control radius + `FocusInsetRow` for rows).
- **Arbitrary children in the cover**: only `CoverOverride` (exists) — no overlay slot.
- **Tooltip policy**: title tooltip only when trimmed; the rail tile's "always" is the one rule-derived exception (no
  label). No `Tip` string on `CardData`.
- **Selection ramp variants**: one selected skin (accent 2-DIP border + card fill). The library navigator's accent pill
  and the sidebar `EntityRow`'s four-state fill are (B) surfaces.
- **A `Plain` skin**: `RowSkin.Plain` exists only so `Album.Page.Plated` could re-plate a row by hand; `Plated` IS the
  tile skin. Deleted.

---

## 2. The surface

### 2.1 Types (`Platform/Surface.Shape.cs` — NEW, engine-free except `Spacing`/`Radii` literals restated as constants)

```csharp
namespace Wavee;

/// <summary>How a media surface is laid out. A VALUE: the presets below are the only shapes the app uses; a caller
/// composes `with` only for the dials that are genuinely per-site (a row's art edge, a grid card's title lines).</summary>
public enum SurfaceLayout : byte { Stack, Row }

/// <summary>The plate a surface sits on. ONE table (SurfacePlate) maps each kind to its rest fill, stroke, hover and
/// press fills; the renderer applies them through ONE mechanism (root carries rest fill + stroke + selected; a
/// non-hit-testable sibling carries hover/press). CardPlate = transparent root + subtle plate (every card); ListRow =
/// the same ramp on a row (the list hover every list has); Tile = opaque card fill + card stroke, stroke turns accent
/// while the row relates to playback; Outline = dashed, fill-less "go somewhere" frame.</summary>
public enum PlateKind : byte { CardPlate, ListRow, Tile, Outline }

/// <summary>Where the "…" lives: over the cover's top-right (Stack) or as a hot-revealed 32 icon in the trailing
/// cluster (Row). None = right-click only (the rail tile has no room).</summary>
public enum MenuPlacement : byte { None, Corner, Trailing }

/// <summary>Reveal = the FAB fades in while hot (every card and row); Always = the FAB is visible at rest (video).</summary>
public enum PlayReveal : byte { Reveal, Always }

public readonly record struct SurfaceShape(
    SurfaceLayout Layout,
    PlateKind Plate,
    float ArtEdge,            // Row: the art square; Stack: NaN = fluid (grid) or the shelf's inner width
    int TitleLines,
    int CaptionLines,         // the subtitle/caption cap (0 = no caption line reserved)
    bool MetaLine,            // a separate tertiary meta line (shelf wide tile)
    float Fab,                // 44 / 30 / 28 — derived by the presets, pinned by tests
    MenuPlacement Menu,
    PlayReveal Play,
    float MinHeight,          // Row floor (52/64/112); Stack: NaN
    bool Labels)              // false = art only (the rail tile); the tooltip then carries the title
{
    public bool IsRow => Layout == SurfaceLayout.Row;
}

/// <summary>THE presets. Names are the inventory's shapes; nothing else is a shape.</summary>
public static class Shape
{
    public static readonly SurfaceShape Grid        = new(SurfaceLayout.Stack, PlateKind.CardPlate, float.NaN, 1, 1, false, 44f, MenuPlacement.Corner, PlayReveal.Reveal, float.NaN, true);
    public static SurfaceShape Shelf(int captionLines = 1, bool metaLine = false)
        => Grid with { CaptionLines = captionLines, MetaLine = metaLine };          // ArtEdge comes from the host's Width prop
    public static readonly SurfaceShape Video       = Grid with { Play = PlayReveal.Always };   // CoverAspect 16:9 is DATA
    public static SurfaceShape Row(float edge = 48f) => new(SurfaceLayout.Row, PlateKind.ListRow, edge, 1, 1, false, FabFor(edge), MenuPlacement.Trailing, PlayReveal.Reveal, RowFloorFor(edge), true);
    public static readonly SurfaceShape RowLarge    = Row(84f) with { MinHeight = 112f, Fab = 44f, TitleLines = 1 };
    public static readonly SurfaceShape RowTile     = Row(48f) with { Plate = PlateKind.Tile };
    public static readonly SurfaceShape RowOutline  = Row(48f) with { Plate = PlateKind.Outline };
    public static readonly SurfaceShape EpisodeRow  = Row(56f) with { TitleLines = 2, MinHeight = 72f };
    public static readonly SurfaceShape SidebarHero = Row(48f) with { Fab = 28f, MinHeight = 64f };
    public static readonly SurfaceShape SidebarTile = Grid with { TitleLines = 1, CaptionLines = 1, Fab = 28f };
    public static readonly SurfaceShape RailTile    = new(SurfaceLayout.Row, PlateKind.ListRow, 36f, 0, 0, false, 0f, MenuPlacement.None, PlayReveal.Reveal, 40f, Labels: false);

    /// <summary>The FAB a row's art carries: 30 up to a 56 square, 44 above (the "top result" hero).</summary>
    public static float FabFor(float artEdge) => artEdge > 56f ? 44f : 30f;
    /// <summary>The row floor: art + 2 × 8 padding, never under 52 (the rail's 40-art row).</summary>
    public static float RowFloorFor(float artEdge) => MathF.Max(52f, artEdge + 2f * 8f);
}
```

`CardData` (in `Controls.Art.cs`, kept — it is the adapter contract) gains the ROW SLOTS that `MediaRow`'s parameters
carried, as init-only members that compare like `Subtitle` (record value; an element with a per-render closure simply
re-renders, the safe direction), plus the search highlight from the section-grid plan and a drop target for the rail
tile:

```csharp
public sealed record CardData(/* unchanged positional members */)
{
    // … Height, Selected, SelectedAccent, Menu, CoverAspect, Meta, Caption, CaptionLines (unchanged) …
    /// <summary>Row slots (formerly MediaRow parameters). Eyebrow sits over the title; MetaRow under the subtitle (an
    /// element — the episode "E · video · date" row); Trailing is the right cluster; Below spans the row's full width
    /// under the text (an episode progress bar). A Trailing child that is itself clickable (a Follow toggle, a heart)
    /// is its own gesture owner by the dispatcher's rule and MUST carry BlocksDragArm (PlayFab/CoverActionFab/
    /// IconAction already do).</summary>
    public Element? Eyebrow { get; init; }
    public Element? MetaRow { get; init; }
    public Element? Trailing { get; init; }
    public Element? Below { get; init; }
    /// <summary>A title match to highlight (Browse charts search, library search) — (0,0) = none. Data.</summary>
    public (int Start, int Length) Highlight { get; init; }
    /// <summary>A drop target the surface is (a playlist tile accepting tracks). Counts by presence.</summary>
    public DropTargetSpec? Drop { get; init; }
    /// <summary>A SEED (blank/unresolved) surface: the host renders its bone face from its own geometry — no title,
    /// no chrome, disabled. The one skeleton description every shape has.</summary>
    public bool IsSeed { get; init; }
    public static readonly CardData Seed = new("", "", null, null, static () => { }) { IsSeed = true };
    // Equals/GetHashCode: + Highlight, IsSeed, presence of Drop, value-equality of the four slots (same as Subtitle).
}
```

`ShelfCardProps`/`GridCardProps` are replaced by ONE props record:

```csharp
/// <summary>The host's re-pushed props: the data (data-only equality), the shape (value), and the shelf's card width
/// (NaN for every fluid/row shape). No focus signal: the host learns slot mode from context (§2.3).</summary>
public sealed record SurfaceProps(CardData Data, SurfaceShape Shape, float Width = float.NaN)
{
    public bool Equals(SurfaceProps? o) => o is not null && (ReferenceEquals(this, o)
        || (Shape.Equals(o.Shape) && Width.Equals(o.Width) && Data.Equals(o.Data)));
    public override int GetHashCode() => HashCode.Combine(Data, Shape, Width);
}

public static Element Surface(CardData d, SurfaceShape shape, float width = float.NaN)
    => Embed.Comp(new SurfaceProps(d, shape, width), static () => new SurfaceHost()) with
    {
        // The skeleton face is THIS tree, derived (SkeletonDeriver: TextEl → bar, ImageEl keeps its aspect, chrome and
        // handlers stripped). One geometry, one description — the hand-rolled bones go.
        SkeletonProxy = () => Embed.Comp(new SurfaceProps(d, shape, width), static () => new SurfaceHost()) with { DeriveRenderedOutput = true },
    };
```

### 2.2 Host modes — how the ONE host knows who owns the click

The engine's rule (`InputDispatcher.NearestGestureOwner`, :2631-2640; `docs/design/subsystems/input-a11y.md:405`): a
release resolves exactly one owner — the nearest enabled self-or-ancestor with a press/click handler — and the walk
stops there. Inside a bound `ItemsView` slot the slot ROOT must be that owner (`SelectorVisualsBound.cs:22-26`;
`PagedShelf.cs:586-593`: "the card template itself declares NEITHER Focusable NOR OnClick"). Today's `ShelfCard` breaks
that rule inside every `PagedShelf` (double tab stop; `current` does not follow clicks — the F21 twin bug the earlier
plan found).

The host reads ONE context, `ItemsView.SlotRow` (`Context<RowScope?>`, engine §6 E1/E2), and derives its mode:

| Mode | Who provides the context | Shell `OnClick` / `Focusable` / `Role` | Focus-within source | Keyboard |
|---|---|---|---|---|
| **Free** | nobody (null) | `_onClick` / true / Button | shell's own `OnFocusChanged` (self) + inner wrapper (within) | shell is the tab stop; Enter/Space = engine click |
| **Bound slot** (`BoundSurface<T>`, app) | `BoundSurface<T>`'s slot root provides `scope.Row` | null / false / None | `row.IsFocused` (engine E1) + inner wrapper (within) | slot root is the roving tab stop; Enter/Space → `Row.OnInteraction` → `OnInvokedTyped` |
| **Shelf slot** (`PagedShelf.BindCard`, engine) | `ShelfCardSlot` provides `scope.Row` (engine E2) | null / false / None | same | same, plus `FollowFocusToPage` |

Focus-within without a scene walk: the dispatcher fires `OnFocusChanged` on ancestors as a ROUTED boundary-crossing
event (`InputDispatcher.cs:3803-3821` — "an ancestor with an OnFocusChanged handler hears focus ENTERING/LEAVING its
SUBTREE"; the focused node itself keeps self semantics). The host therefore wraps its content in a NON-focusable
`BoxEl` with `OnFocusChanged = _within` (hover-scope transparent, layout-transparent `Direction=1, Grow=1`): a Tab from
the shell/slot root onto the card's own FAB fires `false` on the root (self) and `true` on the wrapper (entering) —
`CardChromeRules.FocusWithin(selfFocused, innerFocused)` is their OR. `GridCardHost.FocusInsideShell` and the earlier
plan's `Controls.FocusInside` walker are deleted. (`PipsPager.cs:134,192` builds its focus-within the same way.)

### 2.3 The host (`Platform/Surface.Host.cs` — NEW; replaces `ShelfCardHost`, `GridCardHost`, `MediaRow`)

```csharp
sealed class SurfaceHost : Component, IPropsHost
{
    SurfaceProps? _latest;
    readonly Signal<SurfaceProps?> _props = new(null);
    readonly Signal<bool> _hot = new(false);
    bool _pointerIn, _selfFocus, _innerFocus;
    RowScope? _row;                                   // non-null ⇒ slot mode (read from context in Render)
    readonly Action _onClick, _onPlay, _exit;
    readonly Action<Point2> _enter;
    readonly Action<bool> _selfFocusChanged, _innerFocusChanged;
    readonly Func<object?> _dragPayload;
    readonly Func<ContextMenuModel?> _menu;

    public SurfaceHost()
    {
        _onClick = () => _latest?.Data.OnClick();
        _onPlay = () => _latest?.Data.OnPlay?.Invoke();
        _dragPayload = () => _latest?.Data.Drag?.PayloadFactory();
        _menu = () => _latest?.Data.Menu?.Invoke();
        _enter = _ => { _pointerIn = true; Fold(); };
        _exit = () => { _pointerIn = false; Fold(); };
        _selfFocusChanged = got => { _selfFocus = got; Fold(); };
        _innerFocusChanged = got => { _innerFocus = got; Fold(); };
    }

    // Field writes + ONE equality-gated signal write: a pointer sweeping across a hot card schedules nothing.
    void Fold() => _hot.Value = CardChromeRules.Hot(_pointerIn, CardChromeRules.FocusWithin(_selfFocus, _innerFocus));

    public void ApplyProps(object props) { _latest = (SurfaceProps)props; _props.Value = _latest; }

    public override Element Render()
    {
        // Context reads BEFORE the early return: hook order never depends on the props being seeded.
        var overlay = UseContext(Overlay.Service);
        _row = UseContext(ItemsView.SlotRow);
        var p = _props.Value;
        if (p is null) return new BoxEl();
        var d = p.Data; var s = p.Shape;
        bool slot = _row is not null;

        if (d.IsSeed) return SurfaceParts.Seed(s, p.Width);                     // §2.7 — the bone face, disabled, no chrome

        // HOT first, then the relation COARSE-first (NowPlayingOverlayHost discipline). In slot mode the keyboard
        // focus bit is the slot root's (engine E1): reading it here subscribes this host, so a Tab onto the slot
        // re-renders exactly this surface hot.
        bool hot = _hot.Value || (slot && _row!.Value.IsFocused?.Value == true);
        bool relates = false;
        if (!hot && NowPlaying is { } pb && pb.HasActiveContext.Value) relates = pb.RelatesTo(d.Uri);
        bool chrome = CardChromeRules.Mounted(hot, relates) || (s.Play == PlayReveal.Always && d.OnPlay is not null);

        var mode = SurfaceRules.Ownership(slot, d.OnClick is not null);          // §3 — pure
        DragSource? drag = d.Drag is { } ds ? new DragSource(ds.Kind, _dragPayload) { Style = ds.Style } : null;

        Element content = s.IsRow
            ? SurfaceParts.RowBody(d, s, chrome, _onPlay, Loc.Get(Strings.Detail.Play))
            : SurfaceParts.StackBody(d, s, p.Width, chrome, _onPlay);
        // Focus-within wrapper (§2.2): non-focusable, hears focus entering/leaving the content subtree.
        content = new BoxEl { Direction = 1, Grow = 1f, MinWidth = 0f, HoverScopeTransparent = true,
                              OnFocusChanged = _innerFocusChanged, Children = [content] };

        BoxEl shell = SurfaceParts.Shell(content, s, mode, drag) with
        {
            OnClick = mode.OwnsClick ? _onClick : null,
            OnHoverMove = _enter, OnPointerExit = _exit,
            OnFocusChanged = mode.OwnsFocus ? _selfFocusChanged : null,
            Draggable = drag, DropTarget = d.Drop,
        };
        if (!float.IsNaN(d.Height)) shell = shell with { Height = d.Height, Grow = 0f, Shrink = 0f };   // rule 5
        if (d.Selected) shell = SurfaceParts.Selected(shell, _latest?.Data.SelectedAccent);
        if (!s.Labels) shell = ToolTip.Wrap(shell, d.Title) as BoxEl ?? shell;                   // rail tile: the tooltip IS the label
        return d.Menu is null || IsNullOverlay(overlay) ? shell : ContextMenu.Attach(shell, overlay, _menu);
    }
}
```

`SurfaceParts` (`Platform/Surface.Parts.cs` — NEW, static element builders; the sidebar's hook-free slot class and
the (B) surfaces may call these directly):

```csharp
public static class SurfaceParts
{
    /// <summary>The ONE shell: ZStack [plate sibling, content]; corners/focus margin from the shape; cursor and
    /// role/focus from the ownership rule — never from the caller.</summary>
    public static BoxEl Shell(Element content, in SurfaceShape s, in SurfaceOwnership mode, DragSource? drag)
    {
        var plate = SurfacePlate.For(s.Plate);
        float r = s.IsRow ? Radii.Control : Radii.Card;
        return new BoxEl
        {
            ZStack = true, Grow = s.IsRow ? 0f : 1f, MinWidth = 0f, Corners = CornerRadius4.All(r),
            MinHeight = s.MinHeight,
            Fill = plate.RootFill, BorderWidth = plate.StrokeWidth, BorderColor = plate.Stroke,
            BorderDashOn = plate.Dash, BorderDashOff = plate.Dash,
            Role = mode.Role, Focusable = mode.OwnsFocus,
            FocusVisualMargin = s.IsRow ? Design.FocusInsetRow : Design.FocusInsetBordered,
            Cursor = SurfaceRules.Cursor(mode),                 // Hand iff invokable (self or slot); Arrow otherwise
            Children =
            [
                new BoxEl   // the hover/press plate: fill-only, 83 ms, deepened while held — Controls.Art.cs rule 1
                {
                    Grow = 1f, HitTestVisible = false, Corners = CornerRadius4.All(r),
                    Opacity = 0f, HoverOpacity = 1f,
                    HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                    PressDurationMs = MotionTok.ControlFaster.DurationMs, PressEasing = MotionTok.ControlFaster.Easing,
                    Fill = plate.HoverFill, PressedFill = plate.PressedFill,
                },
                content,
            ],
        };
    }

    /// <summary>Stack body: cover stack (art · [overlay] · [corner "…"]) over the label block. Shelf = fixed inner
    /// width + 4/0 gutter; grid = fluid AspectRatio cover (`ArtworkFill`). Exactly the constants `SurfaceGeometry` sums.</summary>
    public static Element StackBody(CardData d, in SurfaceShape s, float width, bool chrome, Action onPlay) { /* ShelfCardHost/GridCardHost bodies merged: see §2.4 tree */ }

    /// <summary>Row body: art square (edge, overlay 30/44) · text column (eyebrow · title × TitleLines · subtitle ·
    /// MetaRow) · trailing (data Trailing, then the hot-revealed "…" when Menu is Trailing) · Below under the text.</summary>
    public static Element RowBody(CardData d, in SurfaceShape s, bool chrome, Action onPlay, string playName) { /* MediaRow body merged */ }

    /// <summary>The label block (today's `Labels`), with the title inside `Controls.TrimTip` (tooltip only when trimmed)
    /// and the search-highlight arm (`Controls.SearchHighlight`) when `Highlight.Length > 0`.</summary>
    public static Element Labels(CardData d, in SurfaceShape s, float inner) { … }

    public static BoxEl Selected(BoxEl shell, Func<ColorF>? accent)
        => shell with { BorderColor = (accent ?? (static () => Tok.AccentDefault))(), BorderWidth = 2f, Fill = Tok.FillCardDefault };

    /// <summary>The seed face: the SAME layout tree with bar leaves (cover = `FillSubtleSecondary` block of the cover's
    /// geometry; title = 13-high bar capped 150; caption = 11 × 92), disabled, no chrome, no handlers. Replaces the eight
    /// hand-rolled skeleton cells (§5 wave 1/4).</summary>
    public static Element Seed(in SurfaceShape s, float width) { … }
}
```

`SurfacePlate.For(PlateKind)` is the ONE fill table (theme-live `Tok.*` getters, like `Interaction.*`):

| kind | root fill | stroke | hover fill (plate) | pressed fill (plate) |
|---|---|---|---|---|
| CardPlate | transparent | none | `FillSubtleSecondary` | `FillSubtleTertiary` |
| ListRow | transparent | none | `FillSubtleSecondary` | `FillSubtleTertiary` |
| Tile | `FillCardDefault` | 1 · `StrokeCardDefault`, bound: accent@0.5 while `RelatesNow(uri)` | `FillControlSecondary` | `FillControlTertiary` |
| Outline | transparent | 1 dashed 3/3 · `StrokeControlSecondary` | `FillSubtleSecondary` | `FillSubtleTertiary` |

One mechanism (root = rest + stroke + selected; sibling = hover/press) for every kind — the card's rule 1 generalised,
and the reason `Interactive(Interaction.*)` is no longer called by any media surface. The engine-free twin
`PlateRules.Of(kind)` → `(HasRootFill, HasStroke, Dashed, StrokeFollowsPlayback)` is what the tests pin (§3).

### 2.4 Component trees

```
FREE (e.g. a Home episode row, a module card)                BOUND / SHELF SLOT (SectionScreen grid, every PagedShelf)
ComponentEl SurfaceHost ⇐ SurfaceProps(data, shape, w)       slot root BoxEl  [BoundSurface<T> | PagedShelf.BindCard]
└─ shell BoxEl [SurfaceParts.Shell]                           │  Focusable=false (engine toggles), Role=Button
   │  Role=Button Focusable=true Cursor=Hand                   │  OnPointerReleased/OnKeyDown → Row.OnInteraction
   │  OnClick=_onClick OnFocusChanged=_selfFocusChanged        │  OnFocusChanged → Row.OnFocusChanged (+ Row.IsFocused, E1)
   │  OnHoverMove=_enter OnPointerExit=_exit Draggable         │  Ctx.Provide(ItemsView.SlotRow, scope.Row)   (E2 in PagedShelf)
   ├─ plate BoxEl  Opacity 0→1 (hover) PressedFill             └─ ComponentEl SurfaceSlot<T> / ShelfCardSlot
   └─ within BoxEl  OnFocusChanged=_innerFocusChanged              reads item.Item.Value, adapt(item) → CardData | Seed
      └─ body                                                      └─ ComponentEl SurfaceHost ⇐ SurfaceProps
         STACK: BoxEl Direction=1 Pad 8/8/8/12 Gap 8                   └─ shell BoxEl  Role=None Focusable=false Cursor=Hand
           ├─ cover BoxEl ZStack Clip=!circular [AspectRatio]              OnClick=null OnFocusChanged=null
           │  ├─ ArtworkFill | Artwork(inner, coverH)                      OnHoverMove/OnPointerExit/Draggable as FREE
           │  ├─ [chrome] NowPlayingOverlay(uri, _onPlay, 44)              ├─ plate · └─ within · body (identical to FREE)
           │  └─ [chrome ∧ ShowMenu ∧ Menu] MoreCorner()
           └─ Labels: TrimTip(CardTitle × TitleLines) · caption/subtitle · [meta]
         ROW:   BoxEl Direction=0 Gap 12 AlignItems=Center Pad 8 MinHeight=floor
           ├─ art BoxEl ZStack edge×edge Clip=!circular
           │  ├─ Artwork(edge) · └─ [chrome] NowPlayingOverlay(uri, _onPlay, 30|44)
           ├─ text BoxEl Direction=1 Grow=1 Basis=0 MinWidth=0 Gap 2
           │  ├─ [Eyebrow] · TrimTip(TrackTitle × TitleLines) · [Subtitle] · [MetaRow] · [Below]
           └─ trailing BoxEl Direction=0 Gap 8 Shrink=0
              ├─ [Action(Trailing)] · └─ [chrome ∧ Menu=Trailing ∧ Menu] Action(IconAction(More, requestsContext)) Opacity 0→1
                 (Action = BoxEl{BlocksDragArm=true}: a clickable child never arms the row's drag — §8 4b)
   (ContextMenu.Attach(shell, overlay, _menu) when Menu present; ToolTip.Wrap(shell, Title) when !Labels)
```

### 2.5 ASCII wireframes

```
GRID / SHELF CARD                               ROW (48)                                        EPISODE ROW (56, 2-line title, progress)
rest            hot (▒ plate)   playing         ┌────────────────────────────────────────────┐  ┌──────────────────────────────────────────────┐
┌────────────┐  ┌────────────┐  ┌────────────┐  │ ┌────┐ Title that trims with a tooltip…  ⋯ │  │ ┌──────┐ Episode title wraps onto a       ⋯ │
│┌──────────┐│  │▒┌────────┐▒│  │┌──────────┐│  │ │ 48 │ subtitle · meta             (trail)  │  │ │  56  │ second line then ellipsis…           │
││  cover   ││  │▒│   (…) │▒│  ││(≡) cover ││  │ └────┘                                      │  │ │ (▶)  │ Show name                            │
││          ││  │▒│  (▶)   │▒│  ││          ││  └────────────────────────────────────────────┘  │ └──────┘ E · ▣ · 3 Oct · 42 min              │
│└──────────┘│  │▒└────────┘▒│  │└──────────┘│   hot: plate fill, (▶) on art, ⋯ trailing       │ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬░░░░░░░░░░░░░░░░░░░░░░░░ │
│ Title      │  │▒Title     ▒│  │ Title      │   rest: nothing but the text                    └──────────────────────────────────────────────┘
│ caption    │  │▒caption   ▒│  │ caption    │
└────────────┘  └────────────┘  └────────────┘  ROW TILE (Home recents)                         RAIL TILE (40, no label)   SEED (any shape)
                                                 ┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓      ┌────┐ ┌────┐             ┌────────────┐
VIDEO (16:9, FAB at rest)      SELECTED          ┃ ┌────┐ Liked Songs                   ┃      │ ▣  │ │ ▣  │ tooltip =   │ ░░░░░░░░░░ │
┌──────────────────┐           ╔════════════╗    ┃ │ 48 │ (≡) 1,204 songs · now         ┃      └────┘ └────┘ title       │ ░░░░░░░░░░ │
│┌────────────────┐│           ║┌──────────┐║    ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛      selected: 2-px accent ring │ ▬▬▬▬▬▬ (13) │
││      (▶)       ││ accent    ║│  cover   │║     stroke: StrokeCardDefault → accent@0.5                                   │ ▬▬▬ (11×92) │
│└────────────────┘│ 2-px      ║└──────────┘║     while RelatesNow(uri)                                                    └────────────┘
│ Title · 3:42     │ border    ║ Title      ║                                                                              disabled, dimmed
└──────────────────┘           ╚════════════╝
```

### 2.6 The rules the host applies (all in one place, none a caller's choice)

- **Hot/cold chrome** — `CardChromeRules.Mounted(hot, relates)` (exists) for every shape; `PlayReveal.Always` adds
  the FAB at rest for video. `hot = pointerIn || focusWithin(self, inner) || slotRow.IsFocused`.
- **Ownership** — `SurfaceRules.Ownership(inSlot, hasClick)` → `(OwnsClick, OwnsFocus, Role)`:
  free+click = (true, true, Button); slot = (false, false, None); free without click = (false, false, None) (a
  display-only surface — the queue's now-playing card body — gets no hand and no role).
- **Cursor** — `SurfaceRules.Cursor(mode)`: Hand iff `OwnsClick || InSlot`; Arrow otherwise. ONE place in the app;
  the engine gets the same default for `.Interactive(...)` (E3) so the (B) surfaces stop drifting too.
- **Fills** — `SurfacePlate.For(kind)` (§2.3 table).
- **Tooltip** — the title is always inside `Controls.TrimTip(link, title, style)` (library plan §3.5; one measure per
  (text, width), the bound `ToolTip.Wrap(Element, Prop<string?>)` overload mounts no trigger wiring while null —
  `ToolTip.cs:265, 578-582`). A label-less shape (`Labels=false`) wraps the whole shell in a plain `ToolTip.Wrap(title)`.
  No other tooltip; FAB/"…" keep their own names (`Named`).
- **Drag / menu** — `Draggable` from `Data.Drag` (adapter: `HomeCardNav.DragOf`, `Drag.Source(…)` in `Track.RowData`…), payload through the
  trampoline; `Menu` attached by the host with `ContextMenu.Attach` (free and slot alike — the right-click funnel is
  not a gesture owner); the "…" re-enters that funnel (`ClickRequestsContext`). `MenuPlacement` only decides WHERE.
- **Now-playing** — `NowPlayingOverlay` (unchanged host: pill when relates, FAB play/pause when owns); the Tile stroke
  binds `RelatesNow(uri)` (paint-only). No surface reads playback any other way.
- **Selection** — `Data.Selected` → `SurfaceParts.Selected` (2-DIP accent border + `FillCardDefault`); the rail tile's
  ring is the same rule on a 40 square.
- **Seed** — `Data.IsSeed` → `SurfaceParts.Seed(shape)`; `IsEnabled=false`; in a bound slot the caller also disables
  the row (`IsItemEnabledTyped`) so the slot root dims and refuses invoke.
- **Hover arming** — engine policy unchanged: a surface mounted under a still pointer arms on the next move;
  `RefreshHoverAfterScroll` re-asserts hover on a recycled slot (`InputDispatcher.cs:2909-2944`).

### 2.7 Adapters — who builds a `CardData`

| Adapter (exists / new) | Feeds | Notes |
|---|---|---|
| `HomeCards.ShelfCell` → `ShelfItemOf` (exists) | Home/Browse/Search/Artist shelves | becomes `Controls.Surface(data, Shape.Shelf(lines, meta), cardW)` inside `PagedShelf.BindCard`; the column wrapper + `WithContextMenu` go (the host attaches `Data.Menu`) |
| `HomeCards.GridCardData(in HomeCard, overlay, charts, open)` (section-grid plan, new) | SectionScreen grid, Browse category grid | unchanged from that plan; `Highlight` from `ChartTitleMatch` |
| `Track.RowData(Track, TrackRowOptions)` (NEW, `Track.UI.cs`) | Rail Up-next rows, queue rows, playlist `RecRow`, library `TrackHitRow`, Search song `HitRow` | title/artists (the subtitle `Track.ArtCard` renders today), `OnClick` = play-at-index or open, `OnPlay`, `Drag = Drag.Source(() => payload)`, `Menu = () => Track.Menu([t], opts)`, `Trailing` = heart/duration/add cluster; replaces `Track.ArtCard` + `UpNextCard` + `ArtCardSelectSkin` (dead) |
| `Episode.RowData(Episode, …)` (NEW, `Episode.UI.cs`) | Home/Recents/Reader/playlist-table episode rows | `MetaRow` = E · video · date/length; `Below` = progress when resumed; `Drag = null` (episodes are not drag sources — `HomeCardGridShape.CanDrag`); `Menu = () => Episode.Menu(e, new Episode.MenuOptions())` (`Track.Table.cs:1455` already does this) |
| `Search.HitData(EntityRef hit, …)` (NEW) | `HitRow`, `ArtistRow` | eyebrow "Lyrics match", circular for people, `Trailing` = follow/save; the row body itself goes |
| `Sidebar` projection → `CardData` (NEW `SidebarCards.Of(in SidebarRow, entry, spec)`) | `HeroCard`, `GridCell`, `ArtTile` | `Selected` from the projection's selected route; `OnPlay` when the entry has a context; `Drop` for playlists; `Menu` via the existing sidebar menu factories |
| `PageRules.AlbumVideo` / `ShelfEntity` → `CardData` | Album video hero/shelf, Artist videos, module video cards | `CoverAspect = 16/9`, `Caption = duration`, `Shape.Video` |

### 2.8 `Controls.MediaRow` — merge, with the evidence

`MediaRow` (`Controls.Art.cs:800-876`) is a static builder that already consumes `CardData`; it composes the SAME parts
the cards do — `Artwork` (edge square, circular arm), an eagerly mounted `NowPlayingOverlay(uri, d.OnPlay, 30|44)`, a
title from `Design.Type.TrackTitle` with the card's `TitleLines`/ellipsis, `d.Subtitle`, a root with
`Role=Button, Focusable, Cursor=Hand, OnClick=d.OnClick, Draggable=d.Drag` — and differs only in: direction (row),
floors (64/112), three extra element parameters (eyebrow/meta/trailing), and the `RowSkin` switch over `Interactive`
presets. Every one of those is a dial or a data slot in §2.1. What it LACKS is exactly the drift: no slot mode (a
`MediaRow` inside a bound slot would steal the click), eager overlay (~1 ms/68 KB per row, rule 6), no "…", no
`ContextMenu.Attach` (every caller wraps `WithContextMenu` by hand), no tooltip-on-trim, no selected skin, no seed
face. **Decision: merge.** `MediaRow` is deleted; its seven call sites become `Controls.Surface(data with { slots },
Shape.Row(edge) | RowTile | RowOutline | EpisodeRow)`. `RowSkin` is deleted (`PlateKind` replaces it; `Plain` is
refused — `Album.Page.Plated` is the Tile kind).

---

## 3. Pure rules and tests (engine-free; no source-text tests)

New file `Platform/Surface.Rules.cs` (engine-free; `Shape`, `SurfaceShape`, `PlateRules`, `SurfaceRules`,
`SurfaceGeometry`, `TextFit`) and `Wavee.Tests/SurfaceRulesTests.cs`. Existing `CardChromeRulesTests` and
`ControlsGeometryTests` are extended/moved; `ShelfHeight`/`GridCardChromeFor` move into `SurfaceGeometry` (callers
updated — no forwarders).

```csharp
public readonly record struct SurfaceOwnership(bool OwnsClick, bool OwnsFocus, bool InSlot)
{ public AutomationRole Role => OwnsClick ? AutomationRole.Button : AutomationRole.None; }

public static class SurfaceRules
{
    public static SurfaceOwnership Ownership(bool inSlot, bool hasClick) => new(OwnsClick: !inSlot && hasClick, OwnsFocus: !inSlot && hasClick, InSlot: inSlot);
    public static CursorId Cursor(in SurfaceOwnership m) => m.OwnsClick || m.InSlot ? CursorId.Hand : CursorId.Arrow;
    public static bool ChromeMounted(bool hot, bool relates, PlayReveal play, bool hasPlay) => CardChromeRules.Mounted(hot, relates) || (play == PlayReveal.Always && hasPlay);
    public static bool ShowsMenuCorner(in SurfaceShape s, bool hasMenu, bool showMenu) => hasMenu && showMenu && s.Menu == MenuPlacement.Corner;
    public static bool ShowsMenuTrailing(in SurfaceShape s, bool hasMenu, bool showMenu) => hasMenu && showMenu && s.Menu == MenuPlacement.Trailing;
    public static bool TitleTip(bool trimmed, bool hasLabels) => !hasLabels || trimmed;     // label-less ⇒ always
}

public readonly record struct PlateRules(bool HasRootFill, bool HasStroke, bool Dashed, bool StrokeFollowsPlayback)
{ public static PlateRules Of(PlateKind k) => k switch { PlateKind.Tile => new(true, true, false, true), PlateKind.Outline => new(false, true, true, false), _ => new(false, false, false, false) }; }

public static class SurfaceGeometry   // ShelfHeight/CoverHeight/GridCardChromeFor move here verbatim, plus:
{
    public static float RowHeight(in SurfaceShape s) => s.MinHeight;              // rows are floors; text never grows them past 2 lines + meta
    public static float StackExtent(in SurfaceShape s, float cardW, float coverAspect) => float.IsNaN(cardW)
        ? float.NaN /* grid: the layout measures */ : ShelfHeight(cardW, coverAspect, s.CaptionLines, s.MetaLine);
    public static float GridRowEstimate(float minColW, in SurfaceShape s, bool hasSubtitle) => minColW + GridCardChromeFor(s.TitleLines, hasSubtitle);
}
```

Tests (xUnit, `Wavee.Tests/SurfaceRulesTests.cs`):

- `Ownership_free_with_click_owns_click_focus_and_role` — `Ownership(false,true)` = (true,true,false), Role Button.
- `Ownership_in_slot_owns_nothing_and_has_no_role` — `Ownership(true,true)` = (false,false,true), Role None (the slot root carries Button).
- `Ownership_free_without_click_is_display_only` — (false,false,false); `Cursor` = Arrow.
- `Cursor_is_hand_whenever_invokable` — free+click → Hand; slot → Hand; display → Arrow.
- `Chrome_mounts_hot_or_relating_or_always_play` — table of (hot, relates, play, hasPlay) → expected, incl. `Always` without `OnPlay` = false.
- `Menu_placement_follows_the_shape` — Grid+Menu → corner not trailing; Row → trailing not corner; RailTile → neither; `showMenu:false` → neither.
- `Label_less_shapes_always_tip` — `TitleTip(false,false)` true; `TitleTip(false,true)` false; `TitleTip(true,true)` true.
- `Plate_table` — `Of(Tile)` = (true,true,false,true); `Of(Outline)` = (false,true,true,false); CardPlate/ListRow all false.
- `Presets_derive_fab_and_floor_from_the_art_edge` — `FabFor(48)=30`, `FabFor(56)=30`, `FabFor(84)=44`; `RowFloorFor(40)=56`, `(48)=64`, `(84)=112`→ RowLarge pins 112; `EpisodeRow.MinHeight=72`, `TitleLines=2`.
- `Presets_are_values` — `Shape.Row(48).Equals(Shape.Row(48))`; `Shape.RowTile != Shape.Row(48)`; `Shape.Video.Play == Always`.
- `Shelf_extent_is_unchanged` — the existing `cardW + 66` / `+ 50` / `+ 32` / wide-tile facts re-pointed at `SurfaceGeometry.ShelfHeight` (byte-identical numbers).
- `Grid_row_estimate_seeds_the_section_grid` — `GridRowEstimate(176, Shape.Grid, true) == 176 + 28 + 20 + 18`.
- `SurfaceProps_equality_is_data_shape_width` — equal data/shape/width ⇒ equal; different shape ⇒ not; NaN width equals NaN.
- `CardData_slots_and_highlight_are_data` — `d with { Highlight = (0,3) } != d`; two equal `Eyebrow` TextEls ⇒ equal; `IsSeed` differs ⇒ not equal; `Drop` presence counts.
- `FocusWithin_is_the_or_of_self_and_inner` — `CardChromeRules.FocusWithin(true,false)`, `(false,true)` true; `(false,false)` false (replaces `FocusIn`).
- `TextFit` facts from the library plan (`Overflows`, `Trimmed`) — unchanged.

Behaviour only the engine can establish (roving tab stop, slot-root Tap → invoke, hover within, routed focus) is
covered by the engine's `gate.shelf.keyboard.*` / `gate.virt.*` gates (+ the new E1/E2 gates in §6) and the live
checks in §5.

---

## 4. Migration plan — waves with disjoint file sets

Implementation is parallel Sonnet subagents on disjoint files; ONLY the orchestrator builds (`dotnet build Wavee.slnx`
Debug AND Release), tests (`Wavee.Tests` Debug AND Release) and launches (side-folder publish, never the owner's
instance). Each wave ends with the CHANGELOG bullet `(#n)` and `Fixes #n` in the commit body.

**Precondition:** the uncommitted Browse fix (Featured Charts / Food, drinks & music loading, the doubled title, the
`00s` breadcrumb — it touches `SectionScreen.UI.cs`, `Zones.UI.cs`, `Browse.Page.cs`) is committed first, under its own
issue, so round 1 starts from a clean tree.

**Round 1 = waves 0 + 1 + track L** (owner decision 2). Every agent below owns its files outright; the shared APIs
(§2.1 types, §2.3 `Controls.Surface`, Appendix A `BoundSurface<T>`, library plan §3.5 `TrimTip`) are fixed in this
plan, so agents code against them in parallel and the orchestrator reconciles at the one build. No forwards: the
16 `ShelfCard`/`GridCard`/`MediaRow` call sites are swapped in round 1 by the agent that owns each file (0f for the
files no other round-1 agent owns), so no old entry point survives the round (CLAUDE.md "no legacy paths").

### Wave 0 — foundation (same look; shelves gain one tab stop per card and lazy chrome)

| Agent | Files | Work |
|---|---|---|
| 0a | `Platform/Surface.Rules.cs` (NEW), `Wavee.Tests/SurfaceRulesTests.cs` (NEW), `Wavee.Tests/ControlsTests.cs` | §3 types + tests; move `ShelfHeight`/`CoverHeight`/`GridCardChromeFor`/constants into `SurfaceGeometry`; `CardChromeRules.FocusWithin` added, `FocusIn` deleted |
| 0b | `Platform/Surface.Parts.cs` (NEW), `Platform/Surface.Host.cs` (NEW), `Platform/Controls.Art.cs` | `SurfaceParts`, `SurfacePlate`, `SurfaceHost`, `Controls.Surface`; `CardData` slots/`Highlight`/`Drop`/`IsSeed`; `ShelfCard`, `GridCard`, `MediaRow`, `ShelfCardHost`, `GridCardHost`, `ShelfCardProps`, `GridCardProps`, `RowSkin`, `Labels`, `InlineCaption`, `FocusInsideShell`, `ShelfExtent` (its test moves to `SurfaceGeometry`) deleted; the file header's rules 1–6 restated for the ONE surface |
| 0c | `Platform/Controls.TrimTip.cs` (NEW) | library plan §3.5 verbatim (`TrimTip`, `TrimTipHost`); `TextFit` lives in `Surface.Rules.cs` (0a), its facts in `SurfaceRulesTests` |
| 0d | `Platform/Surface.Bound.cs` (NEW) | `Controls.BoundSurface<T>(in BoundItemScope<T>, SurfaceShape, Func<T, CardData?> adapt)` — the section-grid plan's `BoundGridCard<T>`/`BoundGridCardSlot<T>` generalised to any shape; the slot root provides `Ctx.Provide(ItemsView.SlotRow, item.Row, …)`; null from `adapt` ⇒ `CardData.Seed`. **Code: Appendix A.1** |
| 0e (engine, `..\fluent-gpu`) | `FluentGpu.Controls/SelectorVisualsBound.cs`, `ItemsView.cs`, `PagedShelf.cs`, `Interaction.cs`, VerticalSlice gates | §6 E1 (`RowScope.IsFocused`), E2 (`ItemsView.SlotRow` context, provided by `ShelfCardSlot`), E3 (`Interactive` cursor default; list the app sites that set `OnClick` AFTER `.Interactive(...)` via `with`, which miss the default); gates |
| 0f | `Home/Zones.UI.cs`, `Platform/Modules.UI.cs`, `Entities/Album.Page.cs`, `Entities/Episode.Reader.cs` | the mechanical call-site swap only: `MediaRow(data, …)` → `Surface(data with { Trailing/Eyebrow/MetaRow }, Shape.Row(edge) | RowTile | RowOutline | EpisodeRow)` (Zones ×5, Modules :409), `ShelfCard(data, w)` → `Surface(data, Shape.Shelf(), w)` (Modules :436, Episode.Reader :110); `Album.Page.cs:1124` `RowSkin.Plain` + `Plated` → `Shape.RowTile` (`Plated` deleted). The rest of these files' (C) work stays in wave 2 |

Tests: §3 list + all existing `ControlsTests` green with the moved symbols. Live check: Home, Browse, Search, an artist
page, a discography — every existing shelf/grid/row looks and behaves as before (same plate, same geometry), PLUS: Tab into a
shelf reaches ONE stop per card and arrows move between cards (F21 fixed by slot mode); shelf/row FAB and "…" now
mount lazily (`frame.slow hotAlloc=` on Home navigation drops — compare `nav.frames` before/after).

### Wave 1 — the owner's complaint: section grid, Browse grid, skeleton unification

| Agent | Files | Work |
|---|---|---|
| 1a | `Home/SectionScreen.UI.cs`, `Home/Items.Rules.cs`, `Entities/Browse.Cards.cs`, `Wavee.Tests/HomeUi/HomeCardGridShapeTests.cs` (NEW) | **Appendix A.2–A.4**: `HomeCardGridShape`, `HomeCards.GridCardData`, the screen on `BoundSurface(in item, Shape.Grid, _adapt)`; `ShelfCell`'s `ShelfCard` → `Surface(data, Shape.Shelf(…), cardW)` with the host attaching `Data.Menu` (its `WithContextMenu` wrapper goes); delete `SectionCard`, `ContentTypeOf`, `SectionCardDecodePx`, the GridItem paragraph |
| 1b | `Entities/Browse.Modules.cs`, `Entities/Browse.Page.cs` | `SectionGridHost.Cell` → `Controls.Surface(GridCardData(...) with { TitleLines, Highlight }, Shape.Grid)`; `ShimmerCard` (:693) → `Surface(CardData.Seed, Shape.Shelf(), cardW)` |
| 1c | `Entities/Search.UI.cs` (round 1: the shelf/grid half — `:563` `ShelfCard`, `:631` `GridCell`, `GridPlaceholder`, `ShimmerCardGrid`, `ShimmerRows`) | call sites → `Surface(…, Shape.Shelf()/Grid)`; placeholders → `CardData.Seed`; the Skel region content derives from the real shelf/grid trees. The rows half (`HitRow`, `ArtistRow`) is wave 2b |
| 1d | `Entities/Artist.Discography.cs`, `Entities/Artist.Page.cs` (round 1: the three `ShelfCard` sites :1190/:1279/:1375 + `MagazineShimmer`) | `DiscoCell` → `Surface(data, Shape.Grid)`, `DiscoCell.Placeholder` → Seed; `ShelfCard` → `Surface(…, Shape.Shelf())`, `WithCardMenu` → `Data.Menu`; `MagazineShimmer` → the real `Shape.Grid` seed grid. The videos shelf is wave 2e |

Deleted: `SectionCard`, `SectionGridHost.Cell`'s shell/labels, `GridPlaceholder`, `DiscoCell.Placeholder`,
`ShimmerCard`, `ShimmerCardGrid`, `ShimmerRows` (Search), `MagazineShimmer`. Tests: `HomeCardGridShapeTests` (4 facts
from the section-grid plan), `SurfaceProps`/`CardData` facts. Live: the section-grid plan §7 list (hover plate, FAB,
"…", pill, menu, drag, keyboard ONE stop + arrows + Enter, circles, 200-card scroll with `scroll.frames`/`frame.slow`
read, no `ReuseGuard` report).

### Track L — library heads (round 1, beside waves 0 and 1)

Full design, code, wireframes and tests: `library-reader-narrow-heads-implementation.md` (§3–§7). One agent:

| Agent | Files | Work |
|---|---|---|
| L | `Entities/Artist.Reader.Shape.cs` (NEW), `Entities/Artist.Reader.cs`, `Entities/Album.UI.cs` (`PaneCommands` only), `Wavee.Tests/ArtistReaderHeadLayoutTests.cs` (NEW), `Wavee.Tests/ArtistReaderShapeTests.cs` | library plan §3.2 `ReaderShape` (moved + new members), §3.3 stacked band and head trees, §3.4 renderer == estimator + the `library.reader.chrome` truthfulness line, §3.6 plumbing and deletions, §3.7 `PaneCommands` link shrink; uses `Controls.TrimTip` (0c) and `TextFit` (0a) — it does NOT define them |

Live: the library plan §7 width sweep (300 → 1000 reader DIP), no `library.reader.chrome` line, Follow flip moves
nothing, tooltip only on trimmed text, dark/light, `nl`.

### Wave 2 — rail, queue, search rows, podcast, video (the (C) list by visibility)

| Agent | Files | Work |
|---|---|---|
| 2a | `Shell/Rail.UI.cs`, `Entities/Track.UI.cs` (`ArtCard`, `TrackArtCardHost`, `ArtCardSelectSkin`) | `UpNextCard` → `Surface(Track.RowData(t, …), Shape.Row(40))` (row click = play at index — the missing affordance); "About the artist" (:592) → `Surface(artistData with { CoverOverride = hero band, Trailing = FollowToggle }, Shape.RowLarge)` with `Focusable/Role` from the rule; `Track.RowData` NEW; `ArtCard`/`ArtCardSelectSkin`/`TrackArtCardHost` deleted |
| 2b | `Entities/Search.UI.cs` (rows half: `HitRow`, `ArtistRow`, `TopResult` cursor) | `Search.HitData` NEW; `HitRow`/`ArtistRow` → `Surface(…, Shape.Row(48)|RowLarge)`; `TopResult` stays (B) but gets `Cursor` through the engine E3 default (`Interactive(Interaction.Card)` on a clickable box) |
| 2c | `Home/Zones.UI.cs`, `Entities/Recents.UI.cs`, `Entities/Episode.UI.cs` | `Episode.RowData` NEW (`MetaRow`, `Below` progress, `Menu` via `Episode.Menu(e, …)` — fixes `ShowMenu:false`); Home episode rows / recents rows / cluster / release rows → `Surface(…, Shape.EpisodeRow | Row(edge) | RowTile | RowOutline)`; the `WithContextMenu` wrappers go (host attaches); `Episode.UI.cs Numeral/ActionButton/MarkButton` deleted |
| 2d | `Entities/Album.Page.cs`, `Entities/Album.UI.cs`, `Entities/Album.Pane.cs` | `VideoHero`/`VideoShelfCard` → `Shape.Video` (real overlay, menu, drag); trailing rows (already `Shape.RowTile` since 0f) gain `Menu` (fixes "no menu"); "Also by" tiles → `Shape.Grid with { }` at 96 (`Height` pinned); `Album.UI.cs` §4 drawer (~470 dead lines) deleted |
| 2e | `Entities/Artist.Page.cs` (videos shelf), `Entities/Artist.UI.cs` (`PickCard` plate only), `Entities/Concert.Page.cs` (`PromoCard`), `Platform/Modules.UI.cs` (`VideoCard`, `PlayablesBlock`, `CardsBlock`) | → `Shape.Video` / `Shape.Shelf()` / `Shape.Row(40)`; `PickCard` keeps its panel (B) but takes `SurfaceParts.Shell`'s plate + cursor rule |
| 2f | `Entities/Playlist.UI.cs` (`RecRow`), `Entities/User.UI.cs` (`SearchRow`, `TrackHitRow`), `Entities/Queue.UI.cs` (rows, `NowPlayingCard`), `Platform/Controls.Podcast.cs` (`Door` uses `SurfaceRules.Cursor` + `TrimTip`), `Entities/Show.UI.cs` (`MiniCard` → `TrimTip`, cursor rule) | rows → `Shape.Row(40|44|36)` with `Selected` (library search selection, queue current); `RecRow` gains row click + menu; `NowPlayingCard` body = `Surface(data, Shape.Row(44))` with `OnClick` = open context |

Deleted twins: `HitRow`, `ArtistRow`, `UpNextCard`, `Track.ArtCard`, `ArtCardSelectSkin`, Recents `EpisodeRow` body,
queue row body, `RowsSkeleton` (Album), `Episode.UI.cs Numeral/ActionButton/MarkButton`, `Album.UI.cs` §4.
Tests: `Track.RowData`/`Episode.RowData` pure facts (which kinds drag, which have menus, FAB size per edge). Live (per
surface): hover plate + hand cursor; row click opens/plays ONCE; FAB → `card.play` line; pill follows playback; "…"
and right-click open the same menu; drag to the sidebar; Tab order shell → FAB → "…"; trimmed titles tip, short ones
do not; `nav.frames`/`frame.slow` on the rail with a 5-row queue unchanged or better.

### Wave 3 — sidebar surfaces (hook-free slot class; hook-owning children allowed)

Constraint verified: `Sidebar.UI.Slot.cs:28-30` forbids hooks IN THE SLOT CLASS ("every builder is a plain method, so
hook order is identical across every recycle") and explicitly lists hook-owning CHILD components (chevrons, pill, "+",
drop zone) as the sanctioned way; `:39-40` — child ctor args freeze at mount while the slot recycles, which is exactly
why `SurfaceHost` is props-driven (`Embed.Comp(props, factory)` re-pushes on every rebind).

| Agent | Files | Work |
|---|---|---|
| 3a | `Shell/Sidebar.UI.Slot.cs` (`HeroCard`, `GridCell`), `Shell/Sidebar.Cards.cs` (NEW adapter `SidebarCards.Of`) | → `Surface(SidebarCards.Of(...), Shape.SidebarHero | SidebarTile)`; `Selected` from the projection; `OnPlay` from the entry's context; `Menu` from the existing factories; `Drop` for playlists; the hand-rolled 3-state fill + border go |
| 3b | `Shell/Sidebar.UI.Rail.cs` (`ArtTile`, `IconTile`, `Rail.Skeleton`, `Skeletons.RailStack`) | `ArtTile` → `Surface(…, Shape.RailTile)` (tooltip = title, ring = `Selected`, `Drop` wash via `Drop`); `IconTile` stays (B — a glyph, not media) but takes the cursor rule; `Rail.Skeleton` (dead) deleted, the rail seed = `Surface(Seed, RailTile)` |
| 3c | `Shell/Sidebar.UI.Rows.cs` (`EntityRow`) | (B) — keep the row; apply `SurfaceRules.Cursor` (no cursor today), `Equalizer` 12 → the shared pill size constant, `TrimTip` on the label once the engine trimmed signal (E4) lands (a `TrimTipHost` per sidebar row is refused on cost until then) |

Cost rule: a sidebar slot that recycles every scroll now re-pushes ONE props record per rebind and re-renders ONE small
component — the same cost the chevron/pill children already pay; the `ContentType` recycle pools stay. Tests:
`SidebarCards.Of` facts (selected, play presence, drop presence per entry kind). Live: Classic / Library V3 / Curated
designs; a pinned playlist hero shows pill while playing; drag a track onto a playlist tile (drop wash); collapsed rail
tooltips; `sidebar.*` log lines unchanged in cadence; scroll the pane with 200 entries — `scroll.frames` jitter-free.

### Wave 4 — delete the remaining twins and the dead code

| Files | Delete |
|---|---|
| `Entities/Track.UI.cs` | `ArtCardSelectSkin` if any remnant; `Track.MoreCell` where the surface's trailing "…" replaced it |
| `Shell/Sidebar.UI.Rail.cs` | `Rail.Skeleton` |
| `Entities/Detail.UI.cs`, `Detail.UI.Hero.cs` | `SkeletonCover` duplicates where a `Surface` seed now serves; the hero cover itself stays (B) and gets `Role/Focusable` from `SurfaceRules.Ownership` when `CoverClick` is set (the (C)#27 gap) |
| `Entities/Album.UI.cs` | §4 drawer if wave 2d left any |
| pane header skeleton ×2, pending bar ×3 (`Album.UI.cs`/`Show.UI.cs`/`User.*`) | → one `Controls.PendingBar` / the derived pane header (listed here; owner may defer — not media surfaces) |

Gate: `grep`-free proof is not allowed (no source-text tests), so the proof is the build: every deleted symbol's
callers are gone or Release fails. CHANGELOG: one bullet per wave.

### 4.6 Surfaces that must NOT be merged (B) and why

| Surface | Why it stays its own tree (it consumes `SurfaceRules`/`SurfaceParts`/`TrimTip` only) |
|---|---|
| `Controls.Door` | text-first card (label · title · why · foot), ghost numeral, lead gradient, 1-DIP lift; no cover, no now-playing relation |
| Search `TopResult` | a hero (rotated cropped cover, radial wash, action row) — a page-like composition, "A static function, never a Component" |
| Daylist hero, category/fold tiles, Charts `SettingsCard` tiles, concert tiles | navigation/promo tiles without media identity; chrome cards keep `SettingsCard` |
| Library `NavRow`/`NavCard` | selection-first navigator (accent pill, letters, compact mode) — `SelectorVisualsBound.AccentPill` grammar |
| Sidebar `EntityRow`, entry/folder rows, `IconTile` | a navigation list row: selection gutter, multi-select plate, drop target, chevron, pin, modifier-aware activation — the sidebar platform's own grammar (`wavee-sidebar` skill) |
| Detail hero cover | the page SUBJECT, not an item; keeps its morph/shadow; gains role/focus from the ownership rule only |
| `Artist.UI.PickCard` panel | a quote panel with a footer; takes the shell plate + cursor only |

---

## 5. Live verification per wave (logs: `%LOCALAPPDATA%\Wavee\logs`)

Build to a side folder (`-o`) per `live-verification-workflow`; drive deep links from a scratchpad script; `Drive-WaveeWindow -Out`
for screen copies (PrintWindow is stale at idle). Never stop the owner's instance.

| Wave | Check | Evidence |
|---|---|---|
| 0 | Home → every shelf card: hover plate, FAB, "…", pill; Tab reaches ONE stop per card; arrows move; Enter opens | `nav.frames` on Home; `frame.slow hotAlloc=` lower than the pre-change run (eager overlays gone) |
| 0 | A row (Home recents tile): hover ramp identical, Tile stroke accent while playing | screen copy, `card.play` line |
| 1 | section-grid plan §7 (1–9) | `page.reveal`, `nav.frames`, `scroll.frames` on a 200-card section, no `frame.stall`, no `ReuseGuard` line |
| 2 | each (C) site: hand cursor; row click once; FAB; pill; "…" == right-click; drag; Tab order; trimmed-title tooltip only when trimmed | `card.play`, `nav.route`; the rail with queue 5: `frame.slow` unchanged |
| 3 | three sidebar designs; hero pill; tile drop wash; rail tooltips; 200-entry pane scroll | `sidebar.*`, `scroll.frames` |
| 4 | Release build clean; `Wavee.Tests` Debug + Release green | — |

---

## 6. Engine changes (`..\fluent-gpu`; gates: `dotnet build src/FluentGpu.slnx` Debug + Release, `dotnet run --project src/FluentGpu.VerticalSlice` → "ALL CHECKS PASSED", `dotnet test src/FluentGpu.Engine.Tests` Debug + Release)

| Id | Change | Needed? | Why / reference |
|---|---|---|---|
| **E1** | `RowScope.IsFocused : IReadSignal<bool>?` (an `init` property like `Runtime`; null ⇒ not focused) — a per-slot signal `ItemsView` writes from the slot root's focus edge (where `OnFocusChanged → Row.OnFocusChanged` is wired in `BuildListRow`/`SelectorVisualsBound.None`/`PagedShelf.BindCard`) | **needed** (wave 0) | the only way a card INSIDE a slot learns the roving tab stop reached its slot without a scene walk. WinUI: `ListViewItemPresenter` receives the container's focus state (`ListViewItem_themeresources.xaml:234-260`); Slint passes `has-focus` into the passive `ListItem` (`common/listview.slint:134-158`) |
| **E2** | `public static readonly Context<RowScope?> SlotRow = new(null)` on `ItemsView`; `PagedShelf.ShelfCardSlot.Render` wraps `template(item,index,width)` in `Ctx.Provide(ItemsView.SlotRow, scope.Row, …)` | **needed** (wave 0) | lets ONE card host discover slot mode in a `PagedShelf` without changing the `CardAt(item, index, width)` template signature (all app `ShelfOf` callers untouched); the app's `BoundSurface<T>` provides the same key. Gate: `gate.shelf.keyboard.slotrow` — a card that reads the context inside `BindCard` sees a non-null `RowScope` whose `IsFocused` flips with `FocusIndex` |
| **E3** | `Interaction.Interactive`: `Cursor = el.Cursor ?? ((el.OnClick is not null \|\| el.ClickRequestsContext) ? CursorId.Hand : null)` | needed for the (B) surfaces (wave 0), not for the surface itself | app-authoring surface only (framework controls never call it — `Interaction.cs:17-19`), caller-set cursor wins; the engine's "clickability does NOT imply the hand" stance (`Element.cs:219-223`, `Reconciler.cs:5173-5178`) is unchanged at the element level. Flutter `InkResponse` defaults `mouseCursor` to `SystemMouseCursors.click` when `onTap != null` (material not cloned locally — from memory); Zed `ListItem` applies `cursor_pointer()` only when an `on_click` exists (`list_item.rs:389`); egui `interact_cursor` (`button.rs:376-377`) |
| E4 | `TextEl` trimmed fact: `TextMetrics.Trimmed` / a `TextEl.OnTrimmedChanged` bound read | nice-to-have (deletes `TrimTipHost.Measure`; unblocks sidebar `EntityRow` tooltips) | no engine today (`Seams/Text/Text.cs:29`); WinUI `TextBlock.IsTextTrimmed` + `IsTextTrimmedChanged` is the reference |
| E5 | `InputHooks.IsFocusWithin` | **not needed** | routed `OnFocusChanged` (`InputDispatcher.cs:3803-3821`) already gives focus-within on a wrapper whose `FocusBit` comes from the handler alone (`Reconciler.cs:5159-5163`) — the walker is deleted instead |
| E6 | public `Skel.Derive(element)` | **not needed** | the seed face is the host's own (`CardData.IsSeed`); `SkeletonProxy` + `DeriveRenderedOutput` cover regions |

Reference engines for "one interactive surface, the container owns focus/selection, the item owns hover/ink":
WinUI `ListViewItemPresenter` as the single state surface with a passive content template and the states
PointerOver/Pressed/Selected/PointerOverSelected/PressedSelected/Disabled (`controls/dev/CommonStyles/ListViewItem_themeresources.xaml:261-568`);
WinUI `ItemContainer` with the typed dials `CanUserInvoke`/`CanUserSelect` (`controls/dev/ItemContainer/ItemContainer.cpp:574-578`,
states `ItemContainer.xaml:13-101`); Slint `StandardListViewBase` owning `current-item`/`focus-item` and passing
`is-selected`/`has-focus`/`has-hover`/`pressed` into a passive `ListItem` (`common/listview.slint:134-158`), Material
`StateLayer` as a reusable hover/press/focus layer (`material/components.slint:32-90`); Zed `ListItem` (start/end
slots, `on_click`, `on_secondary_mouse_down`, hover-only end slot via `group_hover`, `list_item.rs:157-465`) over
gpui's `InteractiveElement`/`StatefulInteractiveElement` (`div.rs:750-1546`); egui's one `Widgets` visuals-per-state
table with selection layered on top (`style.rs:1250-1320, 359-363`); Flutter `WidgetState` {hovered, focused, pressed,
dragged, selected, disabled} (`widgets/widget_state.dart:168-213`; `InkWell`/`ListTile` from memory — the material
package is not in the local clone). This plan is that split: the ItemsView/PagedShelf slot root owns focus, selection
and invoke; `SurfaceHost` owns hover/press/chrome and reads the container's focus bit.

---

## 7. Alternatives (brief)

| Option | Cost | Risk | Why rejected |
|---|---|---|---|
| Three fixed components (A) | low now | permanent 3-way drift; F21 double stop stays in shelves | the complaint is the drift |
| Boolean bag on `CardData` (B) | low | callers flip affordances off; 27 → 27 variants | affordances must derive from data + shape |
| Bound-native twins (C) | 2× | second overlay/labels/plate | the sidebar allows hook-owning children; no need |
| Everything is a surface (D) | high | door/navigator/hero dials explode | second framework |
| `SlotFocus` signal passed as a prop (earlier plan) | low | every slot host must remember to pass it; PagedShelf cannot (engine template signature) | context (E2) makes slot mode un-forgettable |
| Keep `MediaRow` as a wrapper over the host | low | two entry points, eager overlay stays | merge (§2.8) |
| `Interactive(Interaction.*)` for the surface's plate | low | fill lives on the root and fights the selected skin / Tile stroke | one plate mechanism (root rest + sibling hover) |

---

## 8. Risks and what I could not verify (read-only session; no build, no run)

1. **Per-rebind allocation on a 200-card section**: one `CardData` (+3 closures) + one props record + one element tree
   per boundary crossing — parity with every other grid today; watch `scroll.frames` / `frame.slow hotAlloc=`.
   Mitigation if hot: cache `(item, table epoch) → CardData` in `SurfaceSlot<T>`.
2. **Lazy chrome everywhere**: the shelf/row overlays were eager for two historical failures the file says the engine
   fixed; wave 0's live check (pointer stationary while a shelf pages; press the FAB during a playback edge) must
   confirm on the rows too. Fallback (not a dial): revert `PlayReveal.Reveal` rows to eager inside the host.
3. **Routed focus on a non-focusable wrapper** — VERIFIED: the reconciler sets `FocusBit` from `OnFocusChanged`
   alone (`Reconciler.cs:5159-5163`), independent of `Focusable`, so the wrapper hears focus entering/leaving its
   subtree and the scene walker is gone for good.
4. **E1/E2 engine seams** are unbuilt but small: `RowScope` is a `readonly record struct` built at ONE site
   (`ItemsView.cs:1815`, `new RowScope(index, isSelected, isCurrent, isEnabled, interact, focusChanged)`); E1 adds a
   per-slot `Signal<bool>` written inside that `focusChanged` closure (`focused.Value = got;` beside the existing
   `current` update) and passed as `IsFocused` — one allocation per persistent slot, none per rebind.
4b. **`BlocksDragArm`** — RESOLVED 2026-10-01: no engine CONTROL sets it (`IconAction`, `FollowToggle`, `SaveButton`
   do not), but `Element.BlocksDragArm` (`Element.cs:431`) is an ANCESTOR barrier: `DragController.TryArm` walks up
   from the pressed node and returns false at the first non-draggable node carrying the bit (`DragController.cs:190-206`,
   VerticalSlice `e5dragdrop.armblock`). So the host wraps the trailing "…" and the `Data.Trailing` slot in
   `SurfaceParts.Action(el)` = `new BoxEl { BlocksDragArm = true, Shrink = 0f, Children = [el] }` and a press on any
   control inside never arms the row's drag; `PlayFab`/`CoverActionFab` keep their own bit. No `Controls.Cta.cs` change.
5. **`ToolTip.Wrap(shell)` on the rail tile** returns a wrapper BoxEl (`Direction 0`, shrink-wrapping — SKILL rule 11):
   the 40×40 tile is fixed-size so it is safe; verify no `grow:` is needed.
6. **`CardData` equality with element slots**: a `Trailing` built with per-render closures re-renders the row on every
   parent render (the safe direction). Adapters build trailing handlers as stable fields (the `Track.RowData` options
   record carries them).
7. **Sidebar cost**: one props-driven component per hero/tile slot; `ContentType` pools keep the shape per kind;
   measure `scroll.frames` on a 200-entry pane (wave 3).
8. **a11y**: the slot root keeps `Role=Button`; the shell in slot mode carries none (no double announcement). The
   automation name rides the title text; FAB/"…" names ride their tooltips (`Named`). `AutomationRole` has no
   `ListItem` (`AutomationRole.cs:8-31`) — unchanged.
9. **Localisation**: no new strings; `TrimTip` measures with the default face (a few % error near the edge — never a
   layout error).
10. **Not verified**: the `Sidebar` menu factories' signatures for `SidebarCards.Of`; whether `PagedShelf`'s
    `HoverElevatePaint`/`ShelfLift` padding needs the card's `Grow=1` (kept); `Interactive`'s caller order for E3 (a
    box that gets its `OnClick` AFTER `.Interactive(...)` through `with` misses the default — wave 0e lists those
    sites); Flutter `InkWell`/`ListTile` line numbers (material package absent locally). Resolved since the design:
    the ancestor `BlocksDragArm` barrier, the `RowScope` construction site, the track/episode menu and drag builder names
    (see the header).

---

## Appendix A — the bound slot and the section grid (wave 0d + wave 1a/1b code)

Carried over from the section-grid design and generalised from `GridCardHost` slot mode to the ONE `SurfaceHost`.
What changed from that design: the per-slot `SlotFocus` signal prop and the `FocusInside` scene walker are gone
(E1 `RowScope.IsFocused` + the E2 `SlotRow` context + the routed focus-within wrapper of §2.2 replace them), the
blank seed is `CardData.Seed` rendered by the same host (no `GridCardPlaceholder`, no BoxEl ↔ ComponentEl type swap),
and the row-height seed comes from `SurfaceGeometry`.

**Resolved between the designs:** `TitleLines` is a SHAPE dial only. `CardData`'s positional `TitleLines` member
(`Controls.Art.cs:355`) is removed in 0b and its callers pass `shape with { TitleLines = n }` (the Browse grid's
`p.TitleLines`, the episode rows via `Shape.EpisodeRow`). `HomeModuleLayout.GridCardChromeFor`
(`Browse.Modules.cs:44`, a clamping forwarder) is deleted in 1b; its caller reads `SurfaceGeometry.GridCardChromeFor`.

### A.1 `Platform/Surface.Bound.cs` (wave 0d)

```csharp
namespace Wavee;

public static partial class Controls
{
    /// <summary>THE way a media surface enters a BOUND <see cref="ItemsView"/> slot. Returns the slot ROOT (E9: it owns
    /// invoke and focus — Focusable=false for the roving tab stop, press/Enter/Space → <see cref="RowScope.OnInteraction"/>,
    /// focus → <see cref="RowScope.OnFocusChanged"/>, which also drives E1's <c>IsFocused</c>) and provides
    /// <see cref="ItemsView.SlotRow"/> to the ONE <see cref="SurfaceHost"/> inside it, so the host renders click-less and
    /// focus-less (§2.2). <paramref name="adapt"/> runs inside the slot component's render: its subscribing reads (a
    /// table's <c>Changed</c>) re-describe a hydrating card. Null from <paramref name="adapt"/> = a blank/seed item → the
    /// host's seed face (the caller ALSO disables the row through <c>IsItemEnabledTyped</c>, which dims this root and
    /// refuses invoke). Built once per persistent slot; a rebind costs the slot's re-render and nothing else.</summary>
    public static BoxEl BoundSurface<T>(in BoundItemScope<T> item, SurfaceShape shape, Func<T, CardData?> adapt)
    {
        var slot = new SurfaceSlot<T>(item, shape, adapt);         // the template runs once per slot: the instance IS the mount
        RowScope row = item.Row;
        Func<bool> isEn = row.IsEnabled;
        var interact = row.OnInteraction;
        return new BoxEl
        {
            Direction = 1, Focusable = false, Role = AutomationRole.Button,
            FocusVisualMargin = shape.IsRow ? Design.FocusInsetRow : Design.FocusInsetBordered,
            Opacity = Prop.Of(() => isEn() ? 1f : ItemContainer.DisabledOpacity),
            OnPointerReleased = args => interact(args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { interact(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { interact(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = row.OnFocusChanged,
            Children = [Ctx.Provide<RowScope?>(ItemsView.SlotRow, row, Embed.Comp(() => slot))],
        };
    }

    /// <summary>The per-slot component: reads the slot's equality-gated item memo, adapts it, re-pushes the ONE host.
    /// Always the same element type (a <c>Surface</c> ComponentEl), so blank → live is a props re-push, not a remount.</summary>
    sealed class SurfaceSlot<T> : Component
    {
        readonly BoundItemScope<T> _item;
        readonly SurfaceShape _shape;
        readonly Func<T, CardData?> _adapt;
        public SurfaceSlot(BoundItemScope<T> item, SurfaceShape shape, Func<T, CardData?> adapt) { _item = item; _shape = shape; _adapt = adapt; }

        public override Element Render()
        {
            var t = _item.Item.Value;                              // subscribes the per-slot memo: a recycle re-renders exactly this slot
            return Surface(_adapt(t) ?? CardData.Seed, _shape);    // the adapter's own subscribing reads ride this render
        }
    }
}
```

The cursor is NOT set on the slot root: the shell inside covers the slot and carries `Cursor = Hand` through
`SurfaceRules.Cursor` (InSlot ⇒ Hand). The engine's `PagedShelf.ShelfCardSlot` provides the same context (E2), so a
`Surface` inside `PagedShelf.BindCard` goes slot-mode with no change to any `CardAt(item, index, width)` template.

### A.2 `Home/Items.Rules.cs` — the pure shape rule (wave 1a)

```csharp
/// <summary>What a HomeCard grid cell SHOWS, as a value: round art for an artist, no subtitle on a chart, no drag or
/// menu for the kinds HomeCardNav.DragOf/MenuOf refuse (an episode has no menu; a track/episode is not a drag source).</summary>
public readonly record struct HomeCardGridShape(bool Circular, bool ShowSubtitle, bool CanDrag, bool CanMenu)
{
    public static HomeCardGridShape Of(HomeCardKind kind, bool charts) => new(
        Circular: kind == HomeCardKind.Artist,
        ShowSubtitle: !charts,
        CanDrag: kind is not (HomeCardKind.Track or HomeCardKind.Episode),
        CanMenu: kind != HomeCardKind.Episode);
}
```

### A.3 `Entities/Browse.Cards.cs` — THE `HomeCard → CardData` grid adapter (wave 1a; next to `ShelfCell`)

```csharp
/// <summary>The GRID surface's data for a Home/Browse card — the one adapter every HomeCard grid uses (the drill page's
/// bound slots, Browse's category grid). Null for a blank/seed card. Runs inside a render: the subscribing reads below
/// re-describe the card when its title/cover hydrate (a HomeCard is a (Target, SectionSlot) value — the slot's item memo
/// does not fire on a column landing). <paramref name="charts"/> blanks the subtitle (a chart's cards carry none).
/// <paramref name="open"/> defaults to <see cref="HomeCardNav.Open"/> from the Home origin.</summary>
public static Controls.CardData? GridCardData(in HomeCard c, IOverlayService? menuHost, bool charts = false,
                                              Action<HomeCard>? open = null)
{
    if (c.IsBlank) return null;
    var card = c;                                                   // a struct copy the closures can hold
    _ = Entities.ScopeEpoch.Value;
    _ = SectionTable.CardTable(card.Target.Kind)?.Changed.Value;    // Liked lives in Playlists: CardTable, not TableFor
    var shape = HomeCardGridShape.Of(card.Kind, charts);
    var menu = shape.CanMenu ? HomeCardNav.MenuOf(in card) : null;
    bool hasMenu = menu is not null && !Controls.IsNullOverlay(menuHost);
    string sub = shape.ShowSubtitle ? HomeCards.PlainText(card.Subtitle) : "";
    Action onClick = open is null ? () => HomeCardNav.Open(in card, HomeCardNav.HomeOrigin) : () => open(card);
    return new Controls.CardData(card.Uri, card.Title,
        sub.Length > 0 ? Design.Type.TrackMeta(sub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f } : null,
        card.ImageUrl, onClick, () => HomeCardNav.Play(in card),
        Circular: shape.Circular, Drag: shape.CanDrag ? HomeCardNav.DragOf(in card) : null, ShowMenu: hasMenu)
        { Menu = hasMenu ? menu : null };
}
```

`HomeCardNav.Open/Play/DragOf/MenuOf` (`Browse.Cards.cs:99-173`) are reused unchanged. Verify first:
`SectionTable.CardTable(EntityKind)` is reachable from `Browse.Cards.cs` (if internal to `Home.cs`, expose a
`HomeCard.TableChanged` read instead).

### A.4 `Home/SectionScreen.UI.cs` — the grid becomes the shared surface (wave 1a)

```csharp
// fields
readonly Func<BoundItemScope<HomeCard>, Element> _cardTemplate;
readonly Func<HomeCard, Controls.CardData?> _adapt;
IOverlayService? _overlay;      // Render: _overlay = UseContext(Overlay.Service); read by the adapter at slot render time
bool _charts;                   // Sync: _charts = s.IsChart; beside _total/_liveTitle

// ctor
_cardTemplate = CardSlot;
_adapt = c => HomeCards.GridCardData(in c, _overlay, _charts);
// _contentTypeOf / ContentTypeOf / SectionCardDecodePx / SectionCard: DELETED

// the row-height seed: the surface's own geometry, never a local sum
const float SectionCardMinWidth = 176f;
static readonly float SectionCardRowEstimate = SurfaceGeometry.GridRowEstimate(SectionCardMinWidth, Shape.Grid, hasSubtitle: true);

Element Grid() => ItemsView.CreateBound(_cardsSource, _cardTemplate,
    RepeatLayout.GridFit(SectionCardMinWidth, Spacing.Card, SectionCardRowEstimate),
    new ListOptions<HomeCard>
    {
        Grow = 1f, SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None,
        IsItemInvokedEnabled = true, OnInvokedTyped = _onInvoked, IsItemEnabledTyped = _isEnabled,
        Scroll = new ScrollOptions { ScrollKey = _scrollScope + "home:section:" + (_p?.Uri ?? "") },
        // ContentType: DELETED — a circle is CardData.Circular; the host re-renders for it; no recycle pools.
    });

/// <summary>The bound cell: the app's ONE surface inside the slot root. A blank seed renders the seed face and is
/// disabled through <c>IsItemEnabledTyped</c>.</summary>
Element CardSlot(BoundItemScope<HomeCard> item) => Controls.BoundSurface(in item, Shape.Grid, _adapt);
```

Flow on rebind: slot index signal → the per-slot `Memo<HomeCard>` recomputes → differs → `SurfaceSlot.Render` →
`GridCardData` (one `CardData` + 3 closures) → `Surface(...)` re-push → `SurfaceHost.ApplyProps` → data-only equality
→ render only if the card changed. The header's GridItem "deviation / follow-up" paragraph goes.

### A.5 `Entities/Browse.Modules.cs` — the Browse category grid (wave 1b)

```csharp
Element Cell(HomeCard card, int index, string tier, GridProps p, IOverlayService? host)
{
    ChartTitleMatch.TryFind(card.Title, p.Query, out int start, out int len);
    var data = HomeCards.GridCardData(in card, host, p.Charts, open: c => (_latest?.Open ?? p.Open)(c)) ?? Controls.CardData.Seed;
    return Controls.Surface(data with { Highlight = (start, len) }, Shape.Grid with { TitleLines = p.TitleLines })
        with { Key = "home-section-card:" + tier + ":" + (card.Uri.Length > 0 ? card.Uri : index.ToString(CultureInfo.InvariantCulture)) };
}
```

Removes the eager overlay/"…" mount per cell (`Controls.Art.cs` header rule 6: ~1 ms + ~68 KB per card) and the
second label block. Verify first: `Browse.Page.cs:578` `_openCard`'s `NavOrigin` — the `open` parameter preserves it.

### A.6 Component tree (one bound slot) and card states

```
ItemsView (bound, GridFit 176 / gap Spacing.Card)                 — owns roving tab stop, arrows, typeahead, invoke
└─ slot root  BoxEl  [BoundSurface]  Focusable=false (engine toggles), Role=Button, Opacity⇐IsEnabled
   │          OnPointerReleased→Row.OnInteraction(Tap/DoubleTap)  OnKeyDown Enter/Space→Row.OnInteraction
   │          OnFocusChanged→Row.OnFocusChanged  (E1 writes Row.IsFocused from the same edge)
   └─ ContextProviderEl  ItemsView.SlotRow = row                    (E2 does the same inside PagedShelf)
      └─ ComponentEl  SurfaceSlot<HomeCard>   reads item.Item.Value, adapt(item) ?? CardData.Seed
         └─ ComponentEl SurfaceHost ⇐ SurfaceProps(data, Shape.Grid)
            └─ shell BoxEl  Role=None Focusable=false OnClick=null Cursor=Hand Draggable OnHoverMove/OnPointerExit
               ├─ plate BoxEl  Opacity 0→1 (hover) PressedFill
               └─ within BoxEl OnFocusChanged=_innerFocusChanged
                  └─ STACK body: cover [ArtworkFill · (hot|relates) NowPlayingOverlay 44 · MoreCorner] · Labels (TrimTip title)
```

```
REST                     HOT (pointer / focus within / slot focused)   PLAYING (relates)        SEED (disabled)
┌──────────────────┐     ┌──────────────────┐ plate fill               ┌──────────────────┐     ┌──────────────────┐
│┌────────────────┐│     │▒┌──────────────┐▒│                          │┌────────────────┐│     │ ░░░░░░░░░░░░░░░░ │
││                ││     │▒│          (…) │▒│ "…" corner               ││ (≡) equalizer  ││     │ ░░░░░░░░░░░░░░░░ │
││     cover      ││     │▒│     (▶)      │▒│ play FAB, tooltip        ││     cover      ││     │ ░░░░░░░░░░░░░░░░ │
│└────────────────┘│     │▒└──────────────┘▒│                          │└────────────────┘│     │ ▬▬▬▬▬▬▬▬▬ (13)   │
│ Title            │     │▒Title           ▒│ cursor: hand             │ Title            │     │ ▬▬▬▬ (11×92)     │
│ subtitle         │     │▒subtitle        ▒│ trimmed title → tooltip  │ subtitle         │     └──────────────────┘
└──────────────────┘     └──────────────────┘                          └──────────────────┘
```

### A.7 Tests (wave 1a)

`Wavee.Tests/HomeUi/HomeCardGridShapeTests.cs`:
- `Artist_IsCircular_OthersAreNot` — `Of(Artist,false).Circular` true; Playlist/Album/Podcast false.
- `Charts_HideTheSubtitle` — `Of(Playlist, charts:true).ShowSubtitle` false; `charts:false` true.
- `TrackAndEpisode_AreNotDragSources` — CanDrag false for Track/Episode; true for Playlist/Album/Artist/Liked/Podcast/Audiobook.
- `Episode_HasNoMenu_TrackDoes` — CanMenu false for Episode only.

Plus §3's `Grid_row_estimate_seeds_the_section_grid` (`GridRowEstimate(176, Shape.Grid, true) == 176 + 28 + 20 + 18`).
Engine-only behaviour (roving tab stop, slot-root Tap → invoke, hover within) is the engine gates' job
(`gate.shelf.keyboard.*`, `gate.virt.*`, the new `gate.shelf.keyboard.slotrow`) and the §5 live checks.

---

## Appendix B — issues (filed 2026-10-01: B0 = #156, B1 = #157, B2 = #158, B3 = #159, B4 = #160)

| # | Title | Covers | CHANGELOG bullet (ends ` (#n)`) |
|---|---|---|---|
| B0 | Browse: Featured Charts and "Food, drinks & music" fail to load; doubled section title; `00s` loses its parent in the breadcrumb | the uncommitted Browse fix (precondition) | "Browse: Featured Charts and Food, drinks & music load again, section pages show their title once, and a decade drill keeps its parent in the breadcrumb (#n)" |
| B1 | Section drill cards are hand-rolled: no hover, play, menu, now-playing, drag or hand cursor | waves 0 + 1 | "Section and Browse grids use the shared media card: hover, play, \"…\" menu, now-playing and drag on every card (#n)" |
| B2 | Library › Artists: the artist name and album titles crush to one letter in a narrow reader | track L | "Library › Artists: the artist band and album heads stack under 720/640 DIP instead of crushing the name to one letter; trimmed names and titles get a tooltip (#n)" |
| B3 | Shelf cards take two tab stops and clicks don't move the shelf's current item | wave 0 (E1/E2 slot mode) | "Shelves: one tab stop per card, and arrow keys continue from the card you clicked (#n)" |
| B4 | One shared media surface: hand-rolled cards and rows drift (umbrella; the 27 (C) sites as a checklist) | waves 2–4 | one bullet per wave, each `(#160)` |

The engine change (E1–E3) is committed in `..\fluent-gpu` with its own message referencing #157/#159.

---

## As built (2026-10-01, rounds 1–4)

Rounds 1–4 were implemented the same day by parallel agents on disjoint files; the orchestrator alone built (app
Debug + Release; engine Debug + Release, VerticalSlice, Engine.Tests). What differs from the design above:

**Engine (`..\fluent-gpu`).** E1 `RowScope.IsFocused` tracks the item index focus arrived on (a recycle fires no
focus edge, so a plain bool would stay true over another item; `ItemsView.FocusIndex` re-stamps). E2 is provided on
`PagedShelf.BindCard`'s once-per-slot root — and ONLY by a shelf that invokes (`onInvoke` frozen at mount): a shelf
without `onInvoke` keeps its cards in free mode instead of making them click-less and dead. E3 as designed. New:
`InputDispatcher.NearestGestureOwner` skips a `HoverScopeTransparent` pointer listener (the ToolTip wrapper), so a
click on a tool-tipped, trimmed title inside a slot reaches the slot root (gate `E2.p`). Gates:
`gate.shelf.keyboard.slotrow` (+ a non-invoking shelf), `gate.virt.rowFocus.{follows,recycle,perSlot}`,
`gate.ctl.recipe.cursor`, `E2.p`; `virtualization.md`, `controls.md`, `input-a11y.md` updated.

**Surface.** Row floor `max(64, edge + 16)` (today's rows were 64, not 52). A seed's skeleton is its own seed face
(`SkeletonOverride`) — re-deriving collapsed the fluid cover. Every shelf of surface cards passes `onInvoke` (the
card's own open action, one named method each). `CardData.OnClick` is nullable: a surface without a click is
display-only (no hand, no role, no tab stop). `TitleLines` lives on the shape only. `Controls.TrimmedTitle`,
`Controls.EqualizerH`, `Controls.PendingBar`, `Album.PaneHeaderSkeleton` are the single copies.

**The card playback seam.** `Controls.NowPlaying` had never been installed (no pill, no pause-from-card anywhere).
`Playback.InstallCardSeam` wires it; `CardRelation` is kind-split (a track/episode card compares with the item on
deck, every other card with the playing context), and the coarse gate is an equality-gated `HasCardContext` written
once per drain.

**Adapters.** `Track.RowData(Track, in RowDataOptions)`, `Episode.RowData(Episode, in RowFacts, in RowOptions)`,
`Search.HitData`, `Sidebar.SidebarCards.{Hero,Tile,RailOf}` + `SidebarCardRules`, `HomeCards.GridCardData`,
`Queue.PlayItem` (the rail's "Next up" rows skip exactly as the queue panel does).

**Kept as their own tree on purpose (B), beyond §4.6:** the rail's "About the artist" card (hero band over text; its
Follow toggle moved out of the click owner), the album video hero (thumb beside text; real overlay, menu, drag), the
Recents grouped card (stacked cover + chevron lane), the queue's Classic skin and on-media stage rows, the show
page's `Episode.ReaderRow` (multi-select), concert tiles, Show mini cards. Each takes `SurfaceRules`
(cursor/role/focus) and `TrimTip`.

**Visible changes the shared surface imposed:** queue rows 44 → 54, recommendation rows 48 → 56, library track hits
44 → 52, artist band stacks below 772 DIP (not 720 — the cover spine was missing from the sum), album "More by"
rows / video cells / sidebar heroes / library selection take the shared plate and accent selection skin.

**Also fixed on the way:** `Album.Page.cs` was double-encoded (82 lines of mojibake, one user-visible "Â·").

**Open follow-ups:** the context-menu key on a slot-focused card does not reach the card's menu (pre-existing in
shelves); `ops/loc/work` drafts still carry keys this round did not touch.
