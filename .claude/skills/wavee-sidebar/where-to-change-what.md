# Wavee sidebar — where to change what

Paths are relative to `src/apps/Wavee/` unless they start with `Wavee.Tests/`. Each task lists the files to edit, in
order, and the test file that guards it. Keep the edit order: the model comes before the planner, the planner before the
renderer.

## Tasks

| Task | Edit, in order | Guard test |
|---|---|---|
| Add a Collections page (a new library route) | `Shell/Sidebar.Layout.cs` (`SidebarCatalogue.ItemsOf`, the item list in `s_collectionItems`); the page's loc key in `assets/loc/en-US.json`; `Shell/Sidebar.Library.cs` if it is a Library page (`SidebarLibraryPage`) | `SidebarCatalogueTests.cs`, `SidebarLibraryHeadRulesTests.cs` |
| Add a section kind | `Shell/Sidebar.Layout.cs` (`SidebarSectionKind`, the catalogue, `Movable`/`Collapsible`); `Shell/Sidebar.Store.cs` (the section DTO mapping); `Shell/Sidebar.Planner.cs` (a plan branch); `Shell/Sidebar.UI.Rows.cs` or `Shell/Sidebar.UI.Slot.cs` (the header) | `SidebarCatalogueTests.cs`, `SidebarPlannerTests.cs`, `SidebarStoreV3Tests.cs` |
| Change a row metric (height, indent, icon, label gap) | `Shell/Sidebar.cs` (`SidebarRowGeometry` — the one ladder; `SidebarRowExtents` if the extents change) | `SidebarRowGeometryTests.cs` |
| Add a menu item | `Shell/Sidebar.UI.Menus.cs` (the `SidebarMenuAction` value in `Sidebar.Edit.cs`, the row in `SidebarMenuModel`, the case in `SidebarMenus.Run`) | `SidebarMenuModelTests.cs` |
| Add an Edit mode key or verb | `Shell/Sidebar.UI.Edit.cs` (the key handler); `Shell/Sidebar.Edit.cs` (`SidebarEditRules` for the decision); the `SidebarOp` in `Sidebar.Layout.cs` if it edits the layout | `SidebarEditRulesTests.cs` |
| Add a pane mode rule or change a band | `Shell/Sidebar.Resize.cs` (`SidebarPaneModeRules` for the band; `SidebarResizeRules` for the width) | `SidebarPaneModeRulesTests.cs`, `SidebarResizeRulesTests.cs` |
| Change the Library head, chips or toolbar | `Shell/Sidebar.Library.cs` (`SidebarLibraryHeadRules`, `SidebarLibraryEmptyRules`, `SidebarLibraryShaper`); `Shell/Sidebar.UI.Library.cs` (the visuals) | `SidebarLibraryHeadRulesTests.cs`, `SidebarLibraryShaperTests.cs`, `SidebarLibraryStateTests.cs` |
| Change a pin rule or the Liked and Home guard | `Shell/Sidebar.cs` (`SidebarPinId`); `Shell/Sidebar.Rules.cs` (`SidebarPinRules`); `Shell/Sidebar.Host.cs` (`SidebarPinStore`) | `SidebarPinTests.cs` |
| Change pin sync with Spotify | `Spotify/Spotify.Encode.cs` (`LibraryPinSync`, the write marks); `Shell/Sidebar.Store.cs` (`SidebarPinLatch`) | `SidebarPinSyncTests.cs`, `SidebarAccountStoreTests.cs` |
| Add a persisted field | the DTO in `Shell/Sidebar.Store.cs` (v1 account) or `Shell/Sidebar.Store.V2.cs` (the v2 layout DTOs used by migration); `SidebarStoreJsonCtx` registration; the mapping in `SidebarStoreV3` (`Sidebar.Store.cs`) | `SidebarStoreV3Tests.cs` (round-trip) |
| Change the file store (temp, rotate, `.bak`, `.corrupt`, size budget) | `Shell/Sidebar.Store.cs` (`SidebarFileStore`) | `SidebarFileStoreTests.cs` |
| Change the v2 → v3 migration | `Shell/Sidebar.Migration.cs` (`SidebarMigrationV2`, `SidebarMigrationHost`) | `SidebarMigrationV2Tests.cs` |
| Add a feed (a section that fetches) | `Shell/Sidebar.Feeds.cs` (`SidebarFeedDemand` and its demand rule); `Shell/Sidebar.Host.cs` (the binder input) | `SidebarFeedsTests.cs`, `SidebarProjectionTests.cs` |
| Change how a recent item resolves its title | `Shell/Sidebar.Feeds.cs` (`SidebarRecentsRules`, `ISidebarEntityPeek`) | `SidebarFeedsTests.cs` |
| Change a drop, the drop cue or the rootlist slot | `Shell/Sidebar.cs` (`RootlistSlotResolver`, `RootlistSlotMapper`); `Shell/Sidebar.UI.Drop.cs` (publish, commit, `MoveRootlist`) | `SidebarDropTests.cs` |
| Change the account swap or signed-out behaviour | `Shell/Sidebar.Accounts.cs` (`EnsureAccount`, `SwapAccount`); `Shell/Sidebar.Store.cs` (`SidebarAccountKey`) | `SidebarAccountStoreTests.cs` |
| Change re-plan causes or the census line | `Shell/Sidebar.Census.cs` (`SidebarReplanCause`, `SidebarReplanCensus`); the binder revision in `Shell/Sidebar.Host.cs` | `SidebarReplanCensusTests.cs`, `SidebarRevisionTests.cs` |
| Change the section or folder disclosure | `Shell/Sidebar.Disclosures.cs` (the bookkeeping, in `feat/smooth-reveal`); `Shell/Sidebar.UI.cs` (`PaneView.StartDisclosure`) | `SidebarDisclosuresTests.cs` (in `feat/smooth-reveal`) |
| Change a collapsed-rail flyout | `Shell/Sidebar.UI.Flyout.cs` | renderer, no unit test |
| Change a card surface (Home-style tiles in the pane) | `Shell/Sidebar.Cards.cs` (`SidebarCardRules`); `Shell/Sidebar.UI.Slot.cs` (the surface) | `SidebarCardsTests.cs` |
| Change the footer or the Settings pill | `Shell/Sidebar.UI.Footer.cs` | renderer, no unit test |

## One line per file

Every `Shell/Sidebar*.cs` file, in alphabetical order.

| File | What lives there |
|---|---|
| `Shell/Sidebar.Accounts.cs` | The account swap: `EnsureAccount`, `SwapAccount`, the pending-file adoption on first sign-in. |
| `Shell/Sidebar.Cards.cs` | Card surfaces: `SidebarCardSurface`, `SidebarCardActivation`, `SidebarCardDrop`, `SidebarCardRules`. |
| `Shell/Sidebar.Census.cs` | The re-plan census: `SidebarReplanCause` (which planner input moved) and `SidebarReplanCensus` (the log line). |
| `Shell/Sidebar.Disclosures.cs` | In-flight section and folder disclosures by key, and the reversal. Lands with `feat/smooth-reveal`; not in this branch yet. |
| `Shell/Sidebar.Edit.cs` | Edit rules and the Outline model (`SidebarOutlineRow`, `SidebarEditRules`), the menu model (`SidebarMenuAction`, `SidebarMenuModel`), the undo ring (`SidebarUndoRing`, `SidebarUndoEntry`). |
| `Shell/Sidebar.Feeds.cs` | Feed demand (`SidebarFeedDemand`, `SidebarFeedDemands`), recents (`SidebarRecentsRules`, `SidebarPlayedContext`), new releases, and the resident peek. |
| `Shell/Sidebar.Host.cs` | The `Sidebar` service: the public signals, the pin store (`SidebarPinStore`), the binder (`SidebarProjectionBinder`), the persistence health, the Library writes delegate (`SidebarLibraryWrites`), and the drop-refusal helpers. |
| `Shell/Sidebar.Layout.cs` | The layout model: `SidebarCatalogue`, `SidebarLayoutState`, `LayoutOverlay`, `SidebarOp` and its records, `SidebarLayoutRules.Apply`, `SidebarLayoutDoc`, `SidebarVisibilityRules`. |
| `Shell/Sidebar.Library.cs` | The Library head and shaping: `SidebarLibraryPage`, `SidebarLibraryCounts`, `SidebarLibraryHeadRules`, `SidebarLibraryEmptyRules`, `SidebarLibraryMetrics`, `SidebarLibraryShaper`. |
| `Shell/Sidebar.Migration.cs` | The one-time v2 → v3 migration (`SidebarMigrationV2`), its input and result records, and the impure host (`SidebarMigrationHost`). |
| `Shell/Sidebar.Planner.cs` | `SidebarRowPlanner.Build` and `SidebarPlanOptions`: the (document × projection) → rows flattening. |
| `Shell/Sidebar.Resize.cs` | Pane modes and bands (`SidebarPaneMode`, `SidebarWindowBand`, `SidebarPaneModeRules`) and the resize and settle rules (`SidebarResizeRules`). |
| `Shell/Sidebar.Rules.cs` | Pure rules: pills (`SidebarPillRules`, `SidebarPillMotionRules`), subtitles, type-ahead, label fit, `SidebarPinRules`, `MenuLabel`. |
| `Shell/Sidebar.Store.V2.cs` | The v2 layout DTOs (read by the migration only) and `SidebarStoreV2`. |
| `Shell/Sidebar.Store.cs` | The file store (`SidebarFileStore`), the v3 device mapping (`SidebarStoreV3`), the v1 account DTOs and mapping, `SidebarAccountKey`, `SidebarPinLatch`, and `SidebarStoreJsonCtx`. |
| `Shell/Sidebar.UI.Drop.cs` | The `PaneView` drag path: drop slot publish, the commit, the rootlist move (`PaneView.MoveRootlist`). |
| `Shell/Sidebar.UI.Edit.cs` | Edit mode's view: `EditPane`, the Outline, the edit bar, and the Edit-mode key handlers (Ctrl+Z / Ctrl+Y included). |
| `Shell/Sidebar.UI.Flyout.cs` | The collapsed rail's section and folder flyouts (`PaneSectionFlyout`, `PaneFolderFlyout`). |
| `Shell/Sidebar.UI.Footer.cs` | `PaneFooter`: the footer row and its Settings pill. |
| `Shell/Sidebar.UI.Library.cs` | The Library mode's view: `LibraryMode`, `LibrarySession`, `LibraryHead`, `LibraryChips`, `LibraryToolbar`, `LibrarySearchBox`. |
| `Shell/Sidebar.UI.Menus.cs` | `SidebarMenus` (the menu run and labels) and the `PaneView` menu entry points. |
| `Shell/Sidebar.UI.Rows.cs` | Row content: `RowSpec`, `EntityRow`, `Cover`, `LikedArt`, `Chevron`, `SelectionPill`, `Counts`, `ShortcutCount`, `Skeletons`. |
| `Shell/Sidebar.UI.Slot.cs` | `PaneSlot`: the per-row component and the card and create-button slots. |
| `Shell/Sidebar.UI.cs` | `PaneHost`, `ClassicMode`, `PaneConfig`, `PaneView` (the one renderer, `PlanDep`, `StartDisclosure`), `PaneMetrics`, `PlanDiff`, `PaneFilterBox`, `DragPeekWatcher`. |
| `Shell/Sidebar.cs` | Engine-free types shared by the planner and the renderer: `SidebarRowPlan`, `SidebarRowGeometry`, `SidebarRowShape`, `SidebarRowExtents`, `SidebarProjectionInput`, `RootlistSlotResolver`, `RootlistSlotMapper`, `RootlistOps`, `RootlistMarkerStream`, `SidebarPinId`, `SidebarPin`, `SidebarPaneBounds`. |

Outside `Shell/`, the pin bridge is `Spotify/Spotify.Encode.cs` (`LibraryPinSync`, the pin write marks).
