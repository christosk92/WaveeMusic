# Wavee sidebar — architecture

Every path, type and member below exists on disk. Paths are relative to `src/apps/Wavee/` unless they start with
`Wavee.Tests/`. The design rationale is in [docs/guide/sidebar.md](../../../docs/guide/sidebar.md) (§1–§8) and the
plan in `docs/plans/wavee/sidebar-rework-implementation.md`.

## 1. Layers

| Layer | Where | Engine-free? |
|---|---|---|
| Layout model: catalogue, overlays, ops, rules | `Shell/Sidebar.Layout.cs` | yes |
| Pane modes, window bands, resize | `Shell/Sidebar.Resize.cs` | yes |
| Planner and row plan | `Shell/Sidebar.Planner.cs`, `Shell/Sidebar.cs` (`SidebarRowPlan`, `SidebarRowGeometry`, `SidebarRowShape`) | yes |
| Rules: pills, type-ahead, subtitles, label fit, pin rules | `Shell/Sidebar.Rules.cs`, `Shell/Sidebar.cs` (`RootlistOps`, `RootlistSlotResolver`) | yes |
| Library head rules and shaper | `Shell/Sidebar.Library.cs` | yes |
| Edit rules, menu model, undo ring | `Shell/Sidebar.Edit.cs` | yes |
| Feeds: demand and recents resolution | `Shell/Sidebar.Feeds.cs` | yes (`ISidebarEntityPeek` is the seam) |
| File store, DTOs, account key | `Shell/Sidebar.Store.cs`, `Shell/Sidebar.Store.V2.cs` | mostly (the store moves bytes) |
| Migration v2 → v3 | `Shell/Sidebar.Migration.cs` | yes (`SidebarMigrationV2`); `SidebarMigrationHost` is the impure edge |
| The `Sidebar` service: signals, binder, pin store, account swap | `Shell/Sidebar.Host.cs`, `Shell/Sidebar.Accounts.cs` | no |
| Pin bridge to Spotify | `Spotify/Spotify.Encode.cs` (`LibraryPinSync`) | no |
| Renderer and components | `Shell/Sidebar.UI*.cs` | no |

The engine-free files are source-included or referenced by `Wavee.Tests`. Keep new decision logic in one of them.

## 2. Component tree

`Sidebar.Pane()` and `Sidebar.DrawerPane()` (both in `Sidebar.UI.cs`) are the two mount points: the docked pane and
the narrow overlay drawer (`inDrawer: true`). Both mount a `PaneHost`.

```
PaneHost(inDrawer)                                  Sidebar.UI.cs
└── ClassicMode(inDrawer) | LibraryMode(inDrawer)   Sidebar.UI.cs, Sidebar.UI.Library.cs
    ├── LibraryHead ─ LibraryChips, LibraryToolbar, LibrarySearchBox   (Library only; Sidebar.UI.Library.cs)
    └── PaneView(config, inDrawer)                  Sidebar.UI.cs (+ .Drop, .Flyout, .Menus, .Slot, .Rows)
        ├── PaneSlot (one per visible row)          Sidebar.UI.Slot.cs
        ├── PaneFooter                              Sidebar.UI.Footer.cs
        ├── EditPane (Edit mode: the Outline and the edit bar)   Sidebar.UI.Edit.cs
        ├── PaneSectionFlyout / PaneFolderFlyout    Sidebar.UI.Flyout.cs (collapsed rail)
        ├── PaneFilterBox, DragPeekWatcher          Sidebar.UI.cs
        └── SidebarMenus                            Sidebar.UI.Menus.cs (pane ⋯ / header ⋯ / row menus)
```

Row content is built from `Sidebar.UI.Rows.cs` (`EntityRow`, `Cover`, `LikedArt`, `Chevron`, `SelectionPill`,
`Counts`, `Skeletons`). `Sidebar.UI.Drop.cs` owns the drag, the drop slot and the commit, and `Sidebar.UI.Edit.cs` owns
Edit mode. A `PaneView` is one keyed `Component`: its children are not rebuilt by the parent, so parent props freeze
at mount (see [pitfalls.md](pitfalls.md)).

## 3. The layout model (`Shell/Sidebar.Layout.cs`)

- **`SidebarCatalogue`** says what each layout can hold: the section kinds, which are hideable, movable and collapsible
  (`Movable`, `Collapsible`), and which items each section may contain. Pinned is hideable but locked while it holds a
  route or module pin.
- **`SidebarLayoutState`** is the persisted document: two `LayoutOverlay`s, one for `Classic` and one for `Library`. A
  `LayoutOverlay` holds `SectionState` records (shown, order, per-item hidden set, collapsed).
- **`SidebarOp`** is the closed set of edits (`SetSectionShown`, and its siblings). Every edit is one op.
- **`SidebarLayoutRules.Apply(state, op, pinnedLocked)`** returns a `SidebarOpResult`. It enforces the caps
  (`MaxSections = 16`, `MaxHiddenItems = 64`, `MaxOverlayBytes = 8 KiB`): an over-cap write is refused, never
  truncated. A refused op changes nothing, so the caller pushes no undo entry and writes no file.
- **`SidebarLayoutDoc`** is what the planner reads: the overlay resolved against the catalogue, with the section order
  and the item lists in their final form. Cache it by overlay reference. A fresh doc per render re-skins the window.
- **`SidebarVisibilityRules`** answers "is this section or item shown", which the planner and the menus both use.

## 4. The planner and the row plan

- **`SidebarProjectionInput`** is the immutable snapshot the binder hands the planner: playlist tree, folder state,
  pins, feeds, library counts, recents, the drop context (`PinDropArmed`, see `SidebarPlanOptions`), and the search.
- **`SidebarRowPlanner.Build(doc, input, options)`** flattens (document × projection) into one `SidebarRow` list. It
  is pure and POD. It emits the visible subtree of an expanded pinned folder at relative depth, honours nested
  disclosure state, and shares the section cap.
- **`SidebarRowPlan`** is the list plus the measured geometry. **`SidebarRowGeometry`** is the one metrics ladder
  (row height 36 / two-line 40, icon column 40, label gap 4, trailing pad 14, pane edge 4, rail 48, tile 40).
  **`SidebarRowShape`** says which of the shapes a row takes. Geometry lives there and nowhere else.
- **`SidebarRowExtents`** turns a plan into the extents `ItemsView` virtualizes over.

Coordinate space: all pane metrics are in DIP of the pane's own coordinate space (the coordinate-space note in
`docs/guide/sidebar.md` §3).

## 5. Modes and the window band (`Shell/Sidebar.Resize.cs`)

- **`SidebarPaneMode`**: `Expanded`, `Compact`, `Minimal`. **`SidebarWindowBand`**: `Wide`, `Narrow`, `Tiny`.
- **`SidebarPaneModeRules`** decides the band with hysteresis. Narrow is `528 ≤ w < 660` and is left at 700 (a forced
  48-DIP rail whose toggle opens the overlay pane). Tiny is `w < 528` and is left at 568 (no rail, the drawer).
- **`SidebarResizeRules`** owns the live width, the settle target and the fade. `UserCollapsed` is written only in the
  Wide band and only when not editing; a collapse forced by the band never reaches the saved setting.
- The overlay pane and the rail share one list (the rail is the same `PaneView` at 48 DIP, not a second renderer).
  `FluentPane` opens and closes at 200 ms and 100 ms.

## 6. State signals on `Sidebar` (`Shell/Sidebar.Host.cs`)

Public signals (read them from the renderer so the pane subscribes):

| Signal | Holds |
|---|---|
| `Layout` | `SidebarLayoutId` (Classic or Library) |
| `Density` | `SidebarDensity` |
| `Width`, `Seam`, `PresentedWidth` | the saved width, the drag seam, the width on screen |
| `UserCollapsed` | the user's collapse choice (Wide band only) |
| `Band`, `Mode` | the window band and the pane mode derived from it |
| `OverlayOpen` | the drawer pane is open in Narrow or Tiny |
| `Editing` | Edit mode is on |
| `DragPeek` | a drag is live near the pane edge |
| `LibraryFilter`, `LibrarySearch`, `LibrarySearchOpen` | the Library chips, the search text and whether the search box is open |
| `State` | the `SidebarLayoutState` (both overlays); written only through `Dispatch(op)` |
| `Doc` | the `SidebarLayoutDoc` for the active layout, cached by overlay reference |
| `LayoutVersion`, `PinsVersion`, `FolderVersion`, `RingVersion` | `IReadSignal<int>` bumps: layout state, pin store, expanded folders, undo ring |

The version signals are public read signals backed by `s_layoutVersion`, `s_ringVersion` and `s_folderVersion`, plus
`s_binderEpoch` in `Sidebar.UI.cs`. They invalidate caches. A `PaneView` re-plans when a version it
read moves; `SidebarReplanCensus` names which planner input moved (see [testing.md](testing.md)).

## 7. Stores, persistence and the account swap

- **`sidebar.json`** (`SidebarStoreV3`, `Sidebar.Store.cs`, `FileName`): the device document, v3: the two layout overlays
  only. Unknown section ids are dropped on read, and returned for logging. The active layout, width, userCollapsed,
  density and Library chip are `Platform.Keys` settings (`sidebar.layout.id`, `sidebar.pane.*`, `sidebar.library.filter`).
- **`sidebar.acct-<hash>.json`** (`SidebarAccountStore.FileNameOf(key)`, hash from `SidebarAccountKey.Hash`): the
  per-account document. It holds the pins, folders, first-seen times and the migration latch. Signed-out sessions
  have no account file (`s_accountFile = null`). The signed-out key maps to `sidebar.acct-pending.json`
  (`PendingFileName`), which the first account to sign in adopts (see the account swap below).
- **`SidebarFileStore`** writes temp, rotates the previous file to `.bak`, and sets an unreadable file aside as
  `.corrupt` (defaults are written over it). It enforces `MaxDocumentBytes = 256 KiB` against the serialized bytes
  before it touches any file. It moves bytes only: callers serialize on the UI thread and parse what they read.
- **`SidebarStoreJsonCtx`** is the source-generated JSON context. Every persisted DTO is registered there. Adding a
  persisted field means the DTO, the context and a round-trip test (`SidebarStoreV3Tests`).
- **`SidebarMigrationV2`** runs once before the pane first mounts, when `sidebar.bootstrap.version` is not 2. It maps
  the three old designs and the v2 layout file onto the two layouts, writes `sidebar.json` v3 and the account files,
  latches `sidebar.bootstrap.version = 2`, and raises one toast for anything it could not keep. The old values stay in
  users' `settings.json`: the latch prevents a second read, and deleting them would make a downgrade lose the layout.
- **The account swap** (`Sidebar.Accounts.cs`): `AccountKey` is the live key (`SidebarAccountKey.Of(Entities.Current.Key)`).
  `EnsureAccount(scope)` returns true when the key changed and calls `SwapAccount`. The swap writes the outgoing account
  from its own store first, exits Edit mode, clears the undo ring and the Library search, loads the incoming file
  silently (`Pins.LoadFrom`, so no pin write fires), and resets the expanded-folder set. On the first sign-in it adopts
  `sidebar.acct-pending.json` if the account file does not exist yet. Signed out means no account file, so no pins are
  loaded and none are written.
- **Pins** (`SidebarPinStore`, `SidebarPin(Id, Kind, Uri, Name, AddedAtMs)`): per account, ordered, with `SidebarPinLatch`
  (`Sidebar.Store.cs`) gating the server apply. `LibraryPinSync` (Spotify.Encode.cs) pushes entity pins to the server pin set and applies
  the server set only when the loaded key equals the live key. Liked and Home are never written.

## 8. Feeds and data

`SidebarProjectionBinder` (in `Sidebar.Host.cs`) is the one rebuild driver. It folds the account's version counters into
one revision and emits a new `SidebarProjectionInput` only when something it reads moved. `SidebarFeedDemand` says
which feeds are needed: Recent and New releases fetch only while their section is shown (`SidebarFeedDemands`). Recents
resolve in this order: the index, then the resident peek (`SidebarResidentPeek`), then the logged title, then skip
(`SidebarRecentsRules`).

## 9. Edit mode, menus and undo (`Shell/Sidebar.Edit.cs`, `Sidebar.UI.Edit.cs`, `Sidebar.UI.Menus.cs`)

- Edit mode swaps the pane contents for the Outline (`SidebarOutlineRow`, `SidebarOutlineRowKind`) and an edit bar. The
  Outline lists sections, items and pins in document order. Keyboard: Space lifts, arrows move the focus, Alt+↑/↓
  moves the row, Enter toggles shown. A header in the Outline only takes focus; it never toggles.
- **`SidebarMenuModel`** builds the menu rows (`SidebarMenuRow`) for the pane ⋯ menu, the header ⋯ menu and the row menus
  from `SidebarMenuAction` values. **`SidebarMenus.Run`** (`Sidebar.UI.Menus.cs`) executes them. A new menu entry is one
  enum member, one model row and one `Run` case.
- **`SidebarUndoRing`** holds 50 `SidebarUndoEntry` records (`Layout`, `Density`, `Pin`, `Batch`). One user action pushes
  one entry, which may be a `Batch`, and shows one toast. Ctrl+Z and Ctrl+Y are taken by the sidebar's own key handlers,
  and only when focus is in the pane, the head or the edit bar. They are never a frame-level chord.

## 10. Library head (`Shell/Sidebar.Library.cs`, `Sidebar.UI.Library.cs`)

Library's head is one row of Home (a fixed route), a page dropdown (navigation only: Albums, Artists, Podcasts,
Audiobooks), filter chips (filtering only), and a toolbar (sort, list or grid, create). `SidebarLibraryShaper` shapes the
list from `SidebarLibraryState`. `SidebarLibraryHeadRules` owns the tab order of the head (`HeadTabStop`) and the
toolbar's fold (`ToolbarShape`: Full, IconSort, Folded). `SidebarLibraryEmptyRules` picks the empty state. Custom order
is the rootlist. Every other sort refuses a positional drop (see the rootlist section).

## 11. Disclosures

Sections and folders open and close through `PaneView`'s disclosure path. The disclosure motion belongs to the
smooth-reveal work, not to this rework. The guide describes it in one paragraph, never as a WinUI-style tween.

`PaneView.StartDisclosure` (Sidebar.UI.cs) is the one entry point. `SidebarDisclosures` (Sidebar.Disclosures.cs) tracks
the keys in flight: disclosures run concurrently on the engine's reveal bands, and a click on a key in flight reverses it
from where it stands (fluent-gpu `docs/plans/smooth-reveal-implementation.md` §10/§12). An expansion publishes on the
click frame; a collapse keeps its rows until the band rests, then commits. The chevron rides the same `MotionTok.Reveal`
spring, so the glyph and the rows land together.

## Rootlist drag & drop

Classic Playlists and Library Custom order are one list: the rootlist. Dragging, dropping, reordering and moving a row
in it all resolve to one slot and one commit.

- **One resolver.** `RootlistSlotResolver.Resolve` (`Shell/Sidebar.cs`, engine-free) turns a pointer position over a
  row into `(kind, depth, refusal)`. It uses an edge band of `clamp(0.30·h, 10, 16)` (capped at half the row) and a
  4-DIP depth hysteresis (`DepthHysteresis`). The zone is picked first, because the refusals depend on it. Pinned rows and Liked are
  skipped as targets for a positional insert: they are not in the rootlist order.
- **One published slot.** `PaneView` publishes the resolved slot once per hover (`_dropSlot`). Every drop consumes that
  published slot. It never recomputes one.
- **Sort refusal.** A non-Custom sort cannot show a positional insert. A deposit into a folder or playlist is still
  allowed under any sort. The refusal reads `drag.clearSortingToReorder`.
- **One mapping.** `RootlistSlotMapper.TryMap` turns a slot into a `RootlistMove`. It is called at hover (to show the cue)
  and at drop (to commit).
- **One commit.** Every move (drop, rail drop, menu verb, Alt+↑/↓, the "Move to folder…" picker) goes through
  `PaneView.MoveRootlist` (`Shell/Sidebar.UI.Drop.cs`), which calls the `LibraryWrites.MoveRootlist` delegate on
  `SidebarLibraryWrites` (`Shell/Sidebar.Host.cs`). The app's implementation owns the server write, the toast and Undo.
  The `wavee-playlist-mutations` skill links here for this contract.
- **Cue.** The row draws a line (Before, After, or EndOfList, indented to the resolved depth) or a plate (Inside). Never
  both.
- **Freeze.** While a drag is live, the plan holds its published stage so the dropped row does not snap home. Two
  publishes go through: any disclosure in flight (`_disclosures.Count != 0`; spring-loading a folder exists to
  reveal its children) and the pane's own reorder commit (`_publishThroughFreeze`, a one-shot latch that ends with the
  session).
- **Non-mouse verbs.** A drag is never the only way to move a row: Alt+↑/↓ and the menu verbs use the same mapping and
  the same commit.

Non-rootlist drags (a track onto a playlist, a card onto a folder) aim at a row's identity, not a position, and are
never frozen.
