# The sidebar

> **Scope.** Wavee's left sidebar: two layouts over one renderer, three pane modes driven by the window width, one
> Edit mode, and per-account pins. This is app architecture; the engine is documented in the FluentGpu guides.
>
> Agent-facing companion (file map, pitfalls, test inventory): `.claude/skills/wavee-sidebar/`.

---

## 1. What the sidebar is

- **Two layouts.** Classic (Home, Pinned, Collections, Playlists, and optional Recently played and New releases)
  and Library (one list with filters, like Spotify's Your Library). Pick one in Settings › Appearance or from the
  sidebar's ⋯ menu.
- **One renderer.** `PaneView` draws every layout. `SidebarRowPlanner.Build` turns a `SidebarLayoutDoc` and the
  live library into a flat list of rows; `PaneView` only paints that list.
- **Three pane modes** (Expanded, Compact, Minimal), chosen from the window band (section 4).
- **One Edit mode** and **one undo ring** (section 8).

## 2. The model

```
SidebarOp ──► SidebarLayoutRules.Apply ──► SidebarLayoutState ──► Resolve ──► SidebarLayoutDoc
                                                                                  │
SidebarProjectionInput (binder) ─────────────────────────────────────────► SidebarRowPlanner.Build
                                                                                  │
                                                                             SidebarRowPlan ──► PaneView
```

- **`SidebarCatalogue`** (`Shell/Sidebar.Layout.cs`) says what each layout can hold: the section kinds
  (`SidebarSectionKind`), their default state, and what is locked.
- **`SidebarLayoutState`** holds two `LayoutOverlay`s, one per layout. An overlay stores only what the user changed
  (order, hidden, collapsed, limit, item order, library sort and view).
- **`SidebarOp`** is every edit: a record per action, applied by **`SidebarLayoutRules.Apply`**. Nothing else
  writes the overlays.
- **`SidebarLayoutDoc`** is what the planner reads: the resolved section list for the current layout.
- **Density** (`SidebarDensity`: Default or Compact) applies to entity rows only.

Section kinds per layout, and what the user may do with each:

| Section | Classic | Library | Hideable | Movable | Collapsible |
|---|:-:|:-:|:-:|:-:|:-:|
| Home | yes | yes | no | no | no |
| Pinned | yes | yes | yes (locked while it holds a route or module pin) | Classic | yes |
| Collections | yes | no | yes | Classic | yes |
| Playlists | yes | no | no | Classic | yes |
| Library ("Filters") | no | yes | no | no | no |
| Recent | yes | no | yes | Classic | yes |
| New releases | yes | no | yes | Classic | yes |
| Settings | yes | yes | yes | no | no |

Recent and New releases ship hidden. Library's section has no header collapse; its hideable items are the four
library kinds (Albums, Artists, Podcasts, Audiobooks).

## 3. Rows and metrics

Row metrics come from the WinUI ladder, held in constants on `SidebarRowGeometry` (`Shell/Sidebar.cs`), and are
pinned by `SidebarRowGeometryTests`:

- Row height 36 (two-line entity rows 40), row margin 2 top and bottom, pane edge 4.
- Icon column 40, glyph 16, entity art 32 (two-line) or 24 (one-line). Label gap 4, trailing pad 14.
- Indent step 31, capped at depth 3.
- Header 40 tall, separator 8, rail width 48.
- Selection pill 3 × 16 with radius 2.

`SidebarRowShape` (Glyph, EntityTwoLine, EntityOneLine) picks the metrics for a row. Coordinates are local to the
pane's list: the 4-px pane edge is the only inset the list adds.

## 4. Modes and the window band

`SidebarPaneModeRules` (`Shell/Sidebar.Resize.cs`) maps the window width to a band, and the band to a mode:

| Band | Width | Mode |
|---|---|---|
| Wide | ≥ 700 (leaves Narrow at < 660) | Expanded, or Compact if the user collapsed it |
| Narrow | 528 to 660 (leaves Wide at ≥ 700, Tiny below 528) | Compact |
| Tiny | < 528 (leaves at ≥ 568) | Minimal |

The 40-px hysteresis keeps the band from flickering at a threshold. Narrow and Tiny force their mode whatever the
user wants. In Narrow and Tiny, Edit mode opens the sidebar as an overlay pane instead.

`userCollapsed` is written only in the Wide band and not while editing. In Narrow and Tiny the collapse is
recomputed, never saved over. The overlay pane slides over the page; the pane animates on `FluentPane`
(200 ms open, 100 ms close).

## 5. Data

- **`SidebarProjectionBinder`** (`Shell/Sidebar.Host.cs`) watches the library and builds a `SidebarProjectionInput`:
  the pins, playlists, collections, folders and feeds the planner needs.
- **Feeds.** `SidebarFeedDemand` says whether a feed is shown. Recent and New releases fetch only while shown, so a
  hidden feed costs no network.
- **Recents resolution** goes in order: the index, then the resident peek, then the logged title, then skip. A row
  whose title cannot be resolved is left out rather than painted blank.

## 6. Pins

- Pins are **per account**. `SidebarPinStore` holds the list for the loaded account and is saved to that account's
  file (section 7).
- **`LibraryPinSync`** mirrors Spotify's pin set (`ylpin`) for playlists, albums, artists, shows and folders.
  **`SidebarPinLatch`** guards it: a pin write happens only when the loaded account key equals the live account key.
  There is no cross-account apply.
- **Liked Songs and Home are fixed routes, never pins.**
- The server pin set is written only for entity pins. Route and module pins stay local.

## 7. Persistence

Two JSON files and one store class:

- **`sidebar.json`** (v3, device-wide): the two layout overlays (per-section shown/collapsed/limit/hidden items/item
  order, Library's sort, view and Liked toggle). Its DTO is `SidebarDeviceFileDto`.
- **`settings.json`** keys (device-wide, `Platform.Keys`): the active layout (`sidebar.layout.id`), the pane width
  (`sidebar.pane.width`), the user's collapse (`sidebar.pane.userCollapsed`), density (`sidebar.pane.density`) and
  Your Library's chip (`sidebar.library.filter`).
- **`sidebar.acct-<fnv1a64>.json`** (v1, one per account): pins (in the user's order), local pins, the pin name
  cache, expanded folders, the New releases seen stamp, first-seen stamps and the migration latch. Its DTO is
  `SidebarAccountFileDto`. The file name is a hash of the account key; the key itself is stored inside for
  diagnostics. Signed out, there is no account file: no pins load and none are written. A migration that runs
  signed out writes `sidebar.acct-pending.json`, which the first account to sign in adopts.
- **`SidebarFileStore`** (`Shell/Sidebar.Store.cs`) writes atomically: temp file, then rename, with one rotated
  `.bak`. An unreadable file is set aside as `.corrupt` (its bytes are kept) and defaults are written over it.
  Writes are coalesced per file.

Example of `sidebar.json` (shape only, values abbreviated):

```json
{
  "v": 3,
  "layouts": {
    "classic": { "sections": [ { "id": "home" }, { "id": "pinned", "collapsed": true } ] },
    "library": { "sections": [ { "id": "library", "sort": "recents", "view": "list", "showLiked": true } ] }
  }
}
```

Example of `sidebar.acct-<hash>.json` (shape only):

```json
{
  "v": 1,
  "account": "spotify:user:example",
  "migratedToServer": true,
  "pins": [ "spotify:playlist:3cEYpjA9oz9GiPac4AsH4n", "<local pin id>" ],
  "local": [ { "id": "<local pin id>", "kind": "<SidebarPinWire kind>", "uri": "<pin uri>", "name": "<name>", "addedAtMs": 1760000000000 } ],
  "names": { "spotify:playlist:3cEYpjA9oz9GiPac4AsH4n": "Road trip" },
  "expandedFolders": [ "folder-1" ],
  "newReleasesSeenMs": 1760000000000
}
```

**One-time migration.** On first start of this version, `Shell/Sidebar.Migration.cs` reads the old three-design
settings and `sidebar-layout.json` (v2) once, writes the two layouts and the account file, and latches with
`sidebar.bootstrap.version = 2`. A toast says which parts of a custom layout could not be kept. The old values are
never deleted from `settings.json`: the latch stops a second read, and removing them would lose a downgrade.

## 8. Customizing

**Edit mode** (`Shell/Sidebar.Edit.cs`, `Shell/Sidebar.UI.Edit.cs`) shows an Outline of every section and item and an
edit bar. Keyboard: Space lifts a row, arrows move it, Alt+↑ and Alt+↓ move it by one, Enter toggles shown. Focus lands
in the Outline on enter and returns to the row by its key on Done or Esc. A section header is clickable only to focus
it in the Outline; it never toggles from there.

**Menus** are built by `SidebarMenuModel` (a pure model) and run by `SidebarMenus.Run` (`Shell/Sidebar.UI.Menus.cs`):
the pane ⋯ menu, the header ⋯ menu and the row menus. To add an entry, add it to the model and to `Run`.

**Undo** is `SidebarUndoRing` (`Shell/Sidebar.Edit.cs`), 50 entries deep. One user action is one entry. A multi-part
action is a `Batch` entry, and each action gives one undo and one toast. Collapse toggles are not recorded.

Ctrl+Z and Ctrl+Y are handled by the sidebar's own key handlers, and only when focus is in the pane, the header or
the edit bar. They are never a frame-level chord.

**Disclosure.** Sections and folders open and close through `PaneView.StartDisclosure` (`Shell/Sidebar.UI.cs`),
which holds one disclosure in flight: the same key reverses it, another key completes it and queues. The disclosure
motion belongs to the smooth-reveal work (`feat/smooth-reveal`), which moves this bookkeeping into `SidebarDisclosures`
(`Shell/Sidebar.Disclosures.cs`) and runs disclosures concurrently; it is never a WinUI-style tween.

## 9. Library

The Library layout has a head: the Home button, a page dropdown (navigation), the filter chips (filtering), and a
toolbar under them. The "Your Library" button opens your Albums, Artists, Podcasts or Audiobooks page.

- **Custom order** is the rootlist order. Dragging is allowed only under the Custom sort; other sorts refuse the drop.
- **Pins** sit at the top of the list with a pin mark.
- **Hidden kinds** drop out of the list and out of the chips.

Shaping is done by `SidebarLibraryShaper` and `SidebarLibraryHeadRules` (`Shell/Sidebar.Library.cs`), both pure.

## 10. Where to change what

| To change | Edit |
|---|---|
| Add a section kind, or a Collections page | `SidebarSectionKind` and `SidebarCatalogue` (kind rules, items, loc key) |
| Change what a layout holds or locks | `SidebarCatalogue`, then `SidebarLayoutRules.Apply` for the edit it allows |
| Add an edit | a new `SidebarOp` record, handled in `SidebarLayoutRules.Apply` and in `Sidebar.Host.cs`'s dispatch |
| Change a row metric | `SidebarRowGeometry` (`Shell/Sidebar.cs`) |
| Add a menu item | `SidebarMenuModel`, then `SidebarMenus.Run` |
| Add a pane mode or band rule | `SidebarPaneModeRules` (`Shell/Sidebar.Resize.cs`) |
| Add a persisted field | the DTO in `Shell/Sidebar.Store.cs`, `SidebarStoreJsonCtx`, and a round-trip test |
| Add a device preference (not per layout) | a `SettingKey` in `Platform.Keys` (Platform.cs) and its signal in `Sidebar.Host.cs` |
| Change the account pin rules | `SidebarPinStore`, `SidebarPinLatch`, `LibraryPinSync` |

## 11. Tests

Pure classes are engine-free and covered by `Wavee.Tests`. Each test file (`src/apps/Wavee.Tests/<name>.cs`) covers one area:

- **Layout and model:** `SidebarCatalogueTests`, `SidebarLayoutRulesTests`, `SidebarVisibilityRulesTests`,
  `SidebarStoreV3Tests`, `SidebarMigrationV2Tests`.
- **Rows and geometry:** `SidebarRowGeometryTests`, `SidebarPlannerTests` (fixture `PlanFixture`), `SidebarPillRulesTests`,
  `SidebarSubtitleRulesTests`, `SidebarProjectionTests`.
- **Modes and sizing:** `SidebarPaneModeRulesTests`, `SidebarResizeRulesTests`.
- **Edit, menus and undo:** `SidebarEditRulesTests`, `SidebarMenuModelTests`, `SidebarUndoRingTests`,
  `SidebarDropTests`, `SidebarTypeAheadRulesTests`.
- **Library:** `SidebarLibraryShaperTests`, `SidebarLibraryHeadRulesTests`, `SidebarLibraryStateTests`.
- **Pins and accounts:** `SidebarPinTests`, `SidebarPinSyncTests`, `SidebarAccountStoreTests`.
- **Feeds and cards:** `SidebarFeedsTests`, `SidebarCardsTests`.
- **Persistence:** `SidebarFileStoreTests` (against a temp folder).
- **Wiring:** `SidebarWiringTests`.
- **Revisions and census:** `SidebarRevisionTests`, `SidebarReplanCensusTests`.

Tests assert behaviour through the pure classes. There are no source-text tests.
