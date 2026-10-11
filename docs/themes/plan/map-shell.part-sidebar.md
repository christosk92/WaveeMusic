# Wavee sidebar family: map for the theme refactor (main)

> Agent summary: (1) **Layout** (`sidebar.layout.id`, Classic or Library): 24 comparison sites inside the family plus 4 `Config.Layout` consumers; read synchronously in `Sidebar.Boot`. (2) **Density** (`sidebar.pane.density`): 1 root branch (`ShapeFor`) fanning out to ~24 row-shape sites; 13 sites hardcode the two-line shape. (3) **Library View** (List or Grid, `"view"` in `sidebar.json`): 13 sites; the only "cards vs rows" switch. (4) **Pane mode** (Expanded, Compact rail, Minimal drawer): derived from the window band, not persisted; ~24 planner sites, ~17 slot sites, `InDrawer` ~31 sites. (5) **Pane width** (`sidebar.pane.width`, 180–460): ~10 sites incl. 3 width tiers. (6) Smaller: library chip, ShowLiked, user collapse, Edit mode. (7) **Store versions/migration are one-time only; no runtime branch on V2 vs V3.** (8) Not found on main: "Show covers" (0 hits), no Style/Cards toggle.

Path legend: `<S>` = `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\Shell\`, `<W>` = `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\`. So `Sidebar.UI.cs:85` means `<S>Sidebar.UI.cs` line 85.

Docs: `C:\wavee\fgpu\WaveeMusic\docs\guide\sidebar.md` (220 lines); `C:\wavee\fgpu\WaveeMusic\.claude\skills\wavee-sidebar\SKILL.md` (60 lines). The skill's rule 1 ("one renderer, no layout branches inside it", SKILL.md:33-35) is violated in places, listed in §3. All 25 files were read in full (18,032 lines).

---

## 1. Surfaces and sub-parts

### Mount and host
- `Sidebar.Pane()` (`Sidebar.UI.cs:46`) mounts the docked pane. `Sidebar.DrawerPane()` (`:51`) mounts the overlay drawer. Both create a `PaneHost(inDrawer)` (`:62-101`).
- `PaneHost.Render` picks the mode component: `LibraryMode` vs `ClassicMode` at `:85-87`; keyed per layout at `:95` (`"sidebar.library"` / `"sidebar.classic"`), so a layout switch is a full remount; enter fade uses `EnterExit(Opacity 0)` with `MotionTok.ControlFast` (`:96-97`).
- `ClassicMode` (`:178-196`) builds a `PaneConfig` with `Layout = Classic` (`:188`).
- `LibraryMode` (`Sidebar.UI.Library.cs:24-91`) builds a `PaneConfig` with 15 delegates (`:59-80`).
- `PaneConfig` (`Sidebar.UI.cs:204-237`) is "the only mode seam".

### Expanded pane: `PaneView`
`PaneView` (`Sidebar.UI.cs:290-2087`) is the one renderer.
- `Render` is at `:459-566`. The layer box is `:524-533` (`Width = _expandedWidth`, `HitTestVisible`, `DropTarget` = rail peek). Context-menu shield `:551-564`.
- `PaneChildren` (`:575-593`) lays out head + body + footer.
- `PaddedList` / `PlanList` (`:610-651`) is `ItemsView.CreateBound` with `ContentType` = row kind, a reorder displacement channel and disclosure options.
- `EmptyPane` (`:867-884`) uses `Icons.SplitView` 24, `Tok.TextTertiary`, 14/600 `TextSecondary`.

### Rows
`PaneSlot` (`Sidebar.UI.Slot.cs:37-883`) dispatches on row kind at `:79-96`.

| Row kind | Builder | Notes |
|---|---|---|
| SectionHeader | `HeaderRow` `Slot.cs:137-170`; visuals `SectionHeader.Header` `Sidebar.UI.Rows.cs:873-899` | 40 tall; title 14/600 `TextSecondary`, hover `TextPrimary` (`Rows.cs:880`); "+" for Playlists (`Slot.cs:144-148`); ⋯ via `InlineButton` (`Rows.cs:904-913`); chevron (`Rows.cs:595-657`); pill |
| Divider | `SectionHeader.Separator` `Rows.cs:917-924` | 8 tall, 1-px `Tok.StrokeDividerDefault` |
| IconRow (Home, Collections pages, glyph rows) | `RouteRow` `Slot.cs:493-527` | count badge `CountBadge` `Slot.cs:696-700`, `Counts.Live` `Rows.cs:748-775` |
| EntityRow (entity, pin, Liked, feed row) | `EntryRow` `Slot.cs:225-329` | built by `EntityRow.Create` `Rows.cs:174-293`, `Finish` `:298-334` |
| FolderHeader | `FolderRow` `Slot.cs:334-416` | missing folder: `MissingFolderRow` `:420-443`; folder "+" `:646-674` |
| GridStrip (Library Grid) | `GridStripRow` `Slot.cs:550-573`, `GridCell` `:579-584` | cell = `Controls.Surface(SidebarCards.Tile(...), Shape.SidebarTile)`; tile adapter `Sidebar.Cards.cs:108-130`; no-art cover `:134-145` |
| Empty | `EmptyRow` `Slot.cs:591-604` | |
| Skeleton | `Skeletons.Row` `Rows.cs:825-862` | |
| TreeEnd (drop gutter) | `TreeEndRow` `Slot.cs:835-852` | |
| SectionTile (rail, collapsed section) | `SectionTileRow` `Slot.cs:474-488` | glyph from `PaneIcon.SectionGlyph` `Rows.cs:1091-1099` |
| DropBand ("Drop here to pin") | `DropBandRow` `Slot.cs:531-539` | dashed `AccentDefault` border |

Row-level visual parts:
- **Indicator ZStack** (`Slot.cs:705-716`): drop plate (`:722-746`), then the row, then the `SelectionPill` (`Rows.cs:667-718`), then the insertion line (`Slot.cs:753-797`).
- **Covers** (`Rows.cs:414-578`): radius ladder `:421-422`; `Liked` mosaic `:453-462` rendered by `LikedArt` (`:522-577`); `Art`, `Glyph`, `Folder`, `RouteGlyph` `:468-508`.
- **Trailing cluster**: equalizer, pin mark (12-px `Tok.TextTertiary`) and count (`Rows.cs:237-259`); hover "…" (`:373-390`).
- **Track art hover scrim** (`Rows.cs:395-405`).

### Liked and pins
- **Classic Pinned section**: `PlanPins` (`Sidebar.Planner.cs:121-138`); a pinned folder expands inline (`:132-133`, `AppendPinnedFolderChildren` `:367-397`).
- **Library**: pins lead the list, no header (`Planner.cs:264-283`); Liked row `:285-291` with rule `SidebarVisibilityRules.ShowsLiked` (`Sidebar.Layout.cs:689-696`).
- **Pin mark rule**: `SidebarPinRules.ShowsPinMark` (`Sidebar.Rules.cs:173-175`).
- **Pin row state** (Resolved / Pending / Unavailable / Offline): `Sidebar.Layout.cs:752-772`, rendered at `Slot.cs:237-248, 304, 308`.

### Library layout head
`LibraryHead` (`Sidebar.UI.Library.cs:341-679`): Home row and pill `:427-456`; separator `:402`; "Your Library ▾" dropdown and pill `:458-523` (pages menu `:533-545`); `LibraryChips` `:683-775` (chip visuals `:729-750`: 32 tall, pill radius; active chip `AccentDefault`/`TextOnAccentPrimary` with ✕); `LibraryToolbar` `:779-927` (search, sort, List/Grid toggles `:891-907`, "+", ⋯; built from `TabOrder`); `LibrarySearchBox` `:931-998`; drill breadcrumb `:595-619`; rule line `:408-413`; error banner `:651-678`; `EmptyBand` `:623-648`; `CompactHead` (rail Home + search tile) `:571-592`.

### Classic transient filter box
`PaneFilterBox` (`Sidebar.UI.cs:790-848`), 32 tall, `FontSize 13`; opened with Ctrl+F (`:740-764`), hosted in the header (`Slot.cs:163`).

### Feeds
Recently played and New releases are planned as sections by `PlanFeed` (`Planner.cs:239-249`); data `Sidebar.Feeds.cs:52-143`; age badge `Slot.cs:680-685` using `PaneText.AgeBadge` (`Rows.cs:1046-1056`).

### Accounts
No visual surface. `Sidebar.Accounts.cs` is data only: swap `:40-68`, load `:70-90`, commit `:94-110`.

### Footer
`PaneFooter` (`Sidebar.UI.Footer.cs:31-122`): expanded = Settings row with ⋯ in its chevron column (`:97-104`) or ⋯ alone right-aligned (`:90-96`); compact = Settings tile over a ⋯ tile (`:84-89`); footer pill `:79-81`.

### Flyouts
- **Section flyout** (collapsed section in the rail): `PaneSectionFlyout` (`Sidebar.UI.Flyout.cs:68-149`); panel 300 wide, list capped at 420 high.
- **Folder flyout**: `PaneFolderFlyout` (`Flyout.cs:160-385`), header, back button, page slide.
- Open helpers `Flyout.cs:33-62` (`RightEdgeAlignedTop`, light dismiss). Pure drill stack `SidebarFolderFlyoutNav` (`Sidebar.cs:2198-2248`).

### Context menus
- **Model** (pure): `SidebarMenuModel` (`Sidebar.Edit.cs:194-395`).
- **Mapper**: `SidebarMenus.Map` / `Run` (`Sidebar.UI.Menus.cs:62-252`).
- **Pane menu**: `UI.Menus.cs:265-285` (footer ⋯ and background). **Header ⋯**: `Sidebar.UI.cs:855-864`.
- **Row menus**: `EntryMenu`, `FolderMenu`, `RouteMenu`, `MissingFolderMenu`, `GridCellMenu` (`UI.Menus.cs:290-341`); per-kind models `:345-478`.
- **Navbar extras**: Move to folder… and Select (`:501-523`). **"+" menus**: `CreateRootMenu`, `FolderCreateMenu` (`:692-740`). **Pickers/prompts**: folder picker (`:635-682`), rename prompt (`:717-727`).

### Edit mode
`EditPane` (`Sidebar.UI.Edit.cs:26-617`): edit bar `:103-128` (36 tall, `FillLayerDefault` + `StrokeCardDefault` border, Undo/Redo, Reset ▾, Done accent); Outline `:146-244`; section blocks `:256-322`; header row `:334-363`; checkboxes `:515-526`; pin row `:395-430`; item row `:434-458`; Settings row `:462-481`; locked head `:484-494`; hint row `:497-502`; show-all row `:505-510`; keys `:532-582`; drag chip `:589-606`. Pure model `SidebarEditRules` (`Sidebar.Edit.cs:46-162`).

### Drag and drop targets
All drop specs in `Sidebar.UI.Drop.cs`: `ResourceDropSpec` `:59-210`; `RailFolderDropSpec` `:213-258`; slot resolve/decide `:263-334`; `RailPeekDropSpec` `:428-434`; `HeaderCreateDropSpec` `:440-464`; `FolderCreateDropSpec` `:468-486`. Pure rules `Sidebar.cs:1228-1832`. Reorder bands: in-pane Pinned only `Sidebar.UI.cs:1067-1123`, `1625-1758`; Edit Outline `UI.Edit.cs:149-173`.

### Resize grip and pane column
Owned by the shell: splitter `<W>Shell\Shell.UI.cs:584-588`; column width `:525-527`; keyboard `:755-765`; double-click `:722`. Rules `Sidebar.Resize.cs:105-169`.

### Disclosures and expanders
`StartDisclosure` (`Sidebar.UI.cs:1172-1214`) uses `ItemsViewController.BeginDisclosure`; bookkeeping `SidebarDisclosures` (`Sidebar.Disclosures.cs:16-73`); chevron rotation `Rows.cs:595-657`; section toggle `UI.cs:1922-1932`; folder toggle `:1935-1946`.

---

## 2. States

### Loading
- Binder not yet built (`Revision == 0`) → sources Pending (`Sidebar.UI.cs:1056-1060`).
- Skeleton rows: Playlists `Planner.cs:156-159`; Library list `Planner.cs:294-299` (grid shows nothing while pending); visual `Rows.cs:825-862`, rendered from `Slot.cs:90-91`.
- Count still loading: pending plate (`Rows.cs:727-740`, `800`). Library counts null until known (`Sidebar.Library.cs:18-28`).
- Pending pin shows its kind noun in `TextTertiary` (`Slot.cs:242-248, 304`; `Layout.cs:764-772`); pending folder pin `Sidebar.cs:3099-3102`; unresolved grid tile shows a bone (`Cards.cs:85`).

### Empty
- A section with no rows is not rendered (`Planner.cs:91`), except Playlists. Classic Playlists Empty row `Planner.cs:161-170`, rendered by `Slot.cs:591-612`.
- Whole pane empty: `EmptyPane` (`UI.cs:867-884`); an empty rail shows a spacer (`UI.cs:571, 585`).
- Library empty state owned by the head: rules `SidebarLibraryEmptyRules` (`Library.cs:238-268`); UI `UI.Library.cs:389-397`, `:623-648` (`Controls.Vacancy` with Error, NoMatch, Empty voices).
- Folder flyout empty hint `Flyout.cs:314-325`; Edit hint rows `UI.Edit.cs:305-308`.

### Error
- Library failed load with rows → banner (`UI.Library.cs:416, 651-678`); with no rows → Vacancy Error (`:628`). The binder never publishes Failed (`Sidebar.Host.cs:1773-1776`, marked "GAP").
- Layout file corrupt: `Host.cs:122-127, 134`, surfaced in Settings (`<W>Screens\Settings.UI.Appearance.cs:573-574`).
- Write faults `Store.cs:17-23, 175-220`; `PersistenceHealth` `Host.cs:153`. Drop refusals: sentences `Sidebar.cs:1275-1290`, toasts `UI.Drop.cs:374-383`. Missing write seam `UI.cs:2001-2006`.

### Offline
`SidebarPinState.Offline` defined (`Layout.cs:752-760`), fed from `Spotify.Current.IsOnline` (`Slot.cs:239`); the slot only special-cases Unavailable (`:241`), so Offline renders like Pending.

### Signed out
Account key `""` → no pins load (`Accounts.cs:18-19, 56, 72`); pinning while signed out toasts (`UI.Drop.cs:499-503`); `InstallActionSeams` returns early (`UI.Menus.cs:41`); migration while signed out writes the pending account file (`Store.cs:484-490`; adopted at `Accounts.cs:74-79`).

### Row interaction states
- **Hover / pressed / selected / disabled**: `NavRow` `StateBrush` (`Rows.cs:162-170`): `FillSubtleTransparent`, `FillSubtleSecondary`, `FillSubtleTertiary`; brush cross-fade 83 ms; disabled opacity 0.3 (`:304`).
- **Selected**: the pill — rule `SidebarPillState` (`Sidebar.cs:746-780`); one-pill rule `Rules.cs:28-73`; visual 3×16, r2, `Tok.AccentDefault` (`Rows.cs:703-716`); lanes List/Head/Footer (`Rules.cs:76-84`).
- **Multi-select**: quiet plate via `NavRow(selected || MultiSelected)` (`Rows.cs:320`); check lane `Slot.cs:620-640`.
- **Focus**: roving stops `Rules.cs:118-127`, `UI.cs:660-720`; chip focus `UI.Library.cs:745`; Library head key ring `UI.Library.cs:549-567`.
- **Playing**: equalizer in `Tok.AccentDefault` (`Rows.cs:251`), driven by the play sweep (`UI.cs:1337-1378`) and `RowPlayState` (`:1278-1283`).
- **Hover reveals**: row "…" (`Rows.cs:377`), header ⋯ (hover or permanent after touch, `Rows.cs:910`), folder "+" (`Slot.cs:669-674`), track scrim (`Rows.cs:403`).

### Collapsed vs expanded, and window tiers
- Pane mode from `SidebarPaneModeRules` (`Sidebar.Resize.cs:26-100`): Wide (≥700 to enter, stays while ≥660) → Expanded, or Compact if user collapsed; Narrow (528–660) → forced Compact rail, toggle opens an overlay; Tiny (<528, leaves at ≥568) → Minimal, drawer only. Presented width `:66-73`.
- Overlay drawer is in the shell (`Shell.UI.cs:1417-1560`).
- Library width tiers: drill into folders below 240 (`Library.cs:275`; `UI.Library.cs:42`); toolbar Full / IconSort / Folded below 240 (`Library.cs:157, 174-178`); grid columns 2–4 from a 116-px cell (`Library.cs:53-61`), slot fallback (`Sidebar.cs:531-540`; `Slot.cs:556`).
- Section collapse persisted (`SetSectionCollapsed`, `Layout.cs:245`).

### Edit mode
`Sidebar.Editing` (`Host.cs:508`); enter `:449-458`, exit `:462-474`; pane swaps in the Outline (`UI.cs:498-520, 579`); in Narrow/Tiny the overlay is pinned (`Resize.cs:88`; `UI.cs:529`).

### Drag states
Drop plate and insertion line `Slot.cs:722-797`; drop band `Slot.cs:531-539`, armed `UI.cs:1895`; rail drag peek (250 ms) `UI.cs:1870-1906`, `UI.Drop.cs:428-434`; mid-drag freeze `UI.cs:979-993`; "+" cue `Rows.cs:993-999`; folder flyout row cue `Rows.cs:322-331`; spotlight `UI.Drop.cs:52, 204-206`; captions `UI.Drop.cs:113-136`; reorder lift Stationary at opacity 0 (`UI.cs:1631-1636`); Edit lift `SourceDimOpacity` (`UI.Edit.cs:31`); spring-loaded folder `Slot.cs:370-373`; entering Edit refused while a drag is live (`Host.cs:452-456`).

### Menus, flyouts, dialogs
Listed in §1. Popups use `PopupChrome.Popup`. Reset everything uses a `ContentDialog` (`UI.Menus.cs:90-98`). Undo toasts 6000 / 5000 ms (`Host.cs:347, 385-399`).

### Motion APIs used

| Motion | Where | API |
|---|---|---|
| Layout switch fade | `UI.cs:96-97` | `MotionTok.ControlFast`, `EnterExit` |
| Row reorder | `UI.cs:446` | `MotionTok.ItemPlacement.ToDynamics()` |
| Section/folder disclosure | `UI.cs:1182-1205` | `ItemsViewController.BeginDisclosure` (`MotionTok.Reveal`) |
| Chevron | `Rows.cs:639, 645` | `anim.SeedValue(Rotation, MotionTokenId.Reveal)` |
| Pill flight | `UI.cs:1536-1570`; `Rows.cs:700` | `NavigationSelectionMotion.StartVertical` / `SnapVertical` (600 ms) |
| Breadcrumb | `UI.Library.cs:344-347` | `MotionTok.ConnectedFly` |
| Folder flyout page slide | `Flyout.cs:195` | `MotionRecipes.PageSlideForward` / `Back` |
| Edit section band | `UI.Edit.cs:198` | `LayoutTransition.Slide` |
| Chips and view toggles | `UI.Library.cs:743, 901` | `Design.Motion.Fast` |
| Row and dropdown fills | `Rows.cs:169`; `UI.Library.cs:512` | `BrushMs = 83` |
| Pane column (shell) | `Shell.UI.cs:146-148` | `MotionTok.PaneOpen` / `PaneClose` with `Easing.FluentPane` |
| Drawer (shell) | `Shell.UI.cs:1543` | `UseTransition(TranslateX)` |
| Splitter drag (shell) | `Shell.UI.cs:283` | `FgMotion.SetLayoutTransitionsSuppressed` |

---

## 3. Logic vs presentation

Intended ownership: **CORE** `Sidebar.cs`, `.Layout.cs`, `.Planner.cs`, `.Rules.cs`, `.Resize.cs`, `.Edit.cs`, `.Library.cs`, `.Disclosures.cs`, `.Census.cs`, `.Store.V2.cs`, `.Migration.cs` (the `SidebarMigrationV2` part); **SHELL** `Sidebar.Host.cs`, `.Store.cs`, `.Accounts.cs`, `.Feeds.cs` (peek), `.Migration.cs` host part (`:242-316`); **UI** `Sidebar.UI*.cs`, `Sidebar.Cards.cs`.

### UI files that own logic (state, persistence, decisions)

**`Sidebar.UI.cs`**: `PaneHost` builds/starts the projection binder (`EnsureBinder` `:128`, `PumpBinder` `:138-172`, subscribing every entity table edge); account swap effect `EnsureAccount` on `ScopeEpoch` (`:75-79`); migration toast (`:107-117`); `DefaultReorderCommit` mutates the pin store (`:246-253`); the plan/publish pipeline, A/B buffers and freeze (`:952-1049`); selection/play-state/pill sweeps (`:1337-1498`); **`OwningSection` branches on `doc.Layout == Library` (`:1434`) — breaks the one-renderer rule**; Classic filter state and Ctrl+Z/Y → `Undo`/`Redo` (`:740-782`); `ToggleSection` dispatches and calls `MarkNewReleasesSeen` (`:1922-1932`); `ActivateFolder` (`:1935`); navigation and playback (`:1960-1980`); `CreatePlaylistFlow` / `RefuseWrite` (`:1992-2006`); `PayloadOf` (`:2010-2035`); marker cache (`:2062-2076`); tree-selection mutation (`:1783-1866`); pin drop (`:1746-1758`); writes the global `DragPeek` signal (`:1873-1878`); contains a public pure class `PlanDiff` (`:2098-2129`).

**`Sidebar.UI.Library.cs`**: `LibrarySession` (`:95-338`) — drill stack `:239-277`, `ActivateFolder` switches Grid→List `:281-292`, `CanReorderCustom` `:214-216`, `ShapeInput`/`View.Build` `:166-188`, `SwitchToCustomSortForReorder` `:232-237`, search close/retry `:324-337`; 90 ms search debounce + `Resync` (`:27, 51-57`); head self-correcting effects (`:360-368`); empty-state computation (`:387-397`); chip commit (`:720-725`); search-box Esc logic (`:967-983`).

**`Sidebar.UI.Edit.cs`**: writes `Sidebar.s_enabledEmptySection` (`:379-380`); `HasNothingToShow` reads binder state (`:385-392`); steps the library filter off a hidden kind (`:443-444`); replaces the global `Drag.ExtraChip` (`:54-65`); key handlers dispatch ops (`:532-582`); branches on layout at `:215`, `:232`.

**`Sidebar.UI.Menus.cs`**: `Run` dispatches every mutation (`:151-192`); `ToggleKind`, `MoveBy`, `HideItem` decide ops and toasts (`:196-245`); `InstallActionSeams` signed-out guard (`:35-48`); `TreeMoves` legality (`:577-595`); `CommitMove` (`:612-628`); `RenameAction` (`:744-759`); `LayoutName` "· modified" rule (`:52-56`).

**`Sidebar.UI.Drop.cs`**: all drop arm/commit decisions (`:59-210`); `PinWithToast` signed-out guard (`:496-506`).

**`Sidebar.UI.Slot.cs`**: pin state reads `Spotify.Current.IsOnline` (`:239`); label fallback rules (`:242-248`); `RootlistSection` rule (`:219-220`); `TreeRowFacts` (`:802-829`); `ShowsPinMark(_o.Config.Layout, …)` layout-dependent at `:309, 395, 437`.

**`Sidebar.UI.Rows.cs`**: `LikedArt` reads `User.Me` liked edges and the Tracks table (`:539-563`); `CountHost` reads `User.Relation` (`:764-771`); public pure class `ShortcutCount` (`:780-815`); `PaneText.AgeBadge` reads the clock (`:1049`).

**`Sidebar.UI.Flyout.cs`**: `ToggleFolder` persists expansion (`:137`); flyout navigation state (`:175-179`).

### Logic files holding visuals, colours, sizes

**`Sidebar.cs`**: `SidebarPaneBounds` (`:54-68`) 180/460/320/24; `SidebarRowGeometry` (`:340-586`) the whole pixel ladder (rows 36/40, row margin 2, pane edge 4, top inset 3; rail 48, tile 40, icon column 40, glyph 16, label gap 4; trailing pad 14, chevron column 40, indent 31; header 40, header text x 12, header button 24; separator 8, empty hint 40, tree end 24, pill 3×16 r2; art 32/24 `:395-400`); `SidebarRowExtents` (`:596-617`); `RootlistSlotResolver` edge bands 10–16 px, 4-px hysteresis (`:1339-1346`); `SidebarDropCue` line 2, corner 1, dot 6 (`:1483-1514`); `GridFallbackColumns` (`:531-540`).

**`Sidebar.Library.cs`**: `SidebarLibraryHeadRules` metrics (`:32-49`): home 40, separator 8, dropdown 40, chip row 40, chip 32, gap 8, toolbar 40, controls 32, rule 1, TitleX 16, inset 12; grid min cell 116, gap 8 (`:53-61`); toolbar icon button 28, gap 4, sort chrome 40, fold below 240 (`:147-152`); toolbar width estimate 7-px avg char (`:164-170`); `SidebarLibraryMetrics` drill width 240, breadcrumb 32 (`:275-278`).

**`Sidebar.Edit.cs`**: `SidebarEditRules` heights 40/40/44 (`:50-52`); `ExtentOf` (`:115-126`); the menu model chooses loc keys and radio states (`:203-395`).

**`Sidebar.Rules.cs`**: `SidebarLabelFit` 7 px/char (`:132-144`); `SidebarPillMotionRules.Slides` (`:78-84`); `MenuLabel` 28-char clip (`:182-200`).

**`Sidebar.Resize.cs`**: band widths (`:29-33`); fade minimum 0.35 and nudge 8/40 (`:109-113`).

**`Sidebar.Cards.cs`** (logic namespace, Role UI): builds a `BoxEl` with `Tok.FillSubtleSecondary` and `Tok.TextSecondary` (`:134-145`); kind glyphs (`:158-166`); `Design.Type.TrackMeta` (`:214-217`); tile cover size from `SurfaceGeometry.ShelfPlatePad` (`:92`); subtitle only for two-line rows (`:121`).

**`Sidebar.Host.cs`**: toast text/durations 6000/5000 ms (`:346-357, 385-399`); undo labels from loc keys (`:235-250`). **`Sidebar.Layout.cs`**: title loc keys (`:106-115`); fallback nouns (`:764-772`); `ShapeFor` maps density to the visual row shape (`:594-597`). **`Sidebar.Migration.cs`**: legacy default widths 280/340/320 (`:259`).

### Literal colours outside tokens
Track scrim `new ColorF(0,0,0,0.55)` and white play glyph (`Rows.cs:402-404`); `Tok.AccentDefault with { A = 0.18f }` drop plate (`Rows.cs:327, 996`; `Slot.cs:738`); `ColorF.Transparent` at `Rows.cs:329, 997`; `Slot.cs:738, 745`; `UI.Library.cs:899, 943`; `UI.cs:811`.

### Token usage per file

| File | Tokens |
|---|---|
| `UI.Rows.cs` | AccentDefault ×6, FillSubtleSecondary ×7, TextSecondary ×7, TextTertiary ×4, FillSubtleTertiary ×3, FillSubtleTransparent ×2, StrokeDividerDefault, TextPrimary ×2, AccentTextPrimary |
| `UI.Library.cs` | TextSecondary ×8, FillSubtleSecondary ×6, FillSubtleTertiary ×4, TextPrimary ×3, TextOnAccentPrimary ×2, AccentDefault, AccentSecondary, AccentTextPrimary, StrokeDividerDefault |
| `UI.Edit.cs` | TextTertiary ×9, TextSecondary ×8, TextPrimary ×5, StrokeCardDefault ×2, FillLayerDefault |
| `UI.Slot.cs` | AccentDefault ×6, TextTertiary ×4 |
| `UI.Flyout.cs` | TextSecondary ×3, TextPrimary, TextTertiary |
| `UI.cs` | TextSecondary, TextTertiary |
| `Cards.cs` | FillSubtleSecondary, TextSecondary |

Typography literals (`Size`, `Weight`, `FontSize`): `UI.Edit.cs:117, 341, 405, 447, 469, 492`; `UI.Library.cs:479, 485, 615, 663, 674, 733, 735, 964`; `UI.Rows.cs:880`; `UI.cs:832, 874`. Corner-radius literals: `Rows.cs:383` (13), `:861`, `:983` (4); `Slot.cs:727` (4).

---

## 4. Presentation switches

### 4.1 Layout (Classic or Library)
**Persisted**: key `sidebar.layout.id`, `SettingKey<int>` (0 Classic, 1 Library), `<W>Platform\Platform.cs:83`; read `Sidebar.Host.cs:78` in `Boot`; written `Host.cs:268` (`SwitchLayout`) and `Sidebar.Migration.cs:279`; signal `Sidebar.Layout` (`Host.cs:158`). **Available synchronously before the first frame: yes** (`Boot` runs from `App.cs:194` before `Shell.Run`).

**Branch sites in the family (24 comparison lines)**: Host/UI-structure `UI.cs:85-87` (mode component), `:95` (remount key), `:111` (toast name), `:1434` (**renderer branch**); Planner `Planner.cs:49`; Catalogue/rules `Layout.cs:67` (`KindsOf`), `:126` (`Movable`), `:138-139` (`ItemsOf`), `:219`, `:233, 236`, `:460`, `:665` (`DedupesPins`), `Rules.cs:174` (`ShowsPinMark`); Binder `Host.cs:1484`, `Host.cs:1826` (`PackV3` trigger, `Sidebar.cs:3240`); Menus/Edit `Edit.cs:212`, `:226, 228`, `UI.Edit.cs:215`, `:232`, `UI.Menus.cs:54`, `:145, 156`.

**Indirect**: `PaneConfig.Layout` fixed per mode (`UI.cs:188`; `UI.Library.cs:61`), consumed at `UI.Slot.cs:309, 395, 437` and `UI.cs:1929`; the whole Library mode hangs off `PaneConfig` delegates (`UI.Library.cs:59-80`).

**Outside the family (6)**: `<W>Shell\Shell.UI.cs:733`, `:837`; `<W>Screens\Settings.UI.Appearance.cs:551, 562-563, 588-625` (`SwitchLayout` at `:591`); `<W>Screens\Setup.UI.cs:753`; `<W>Shell\Shell.Palette.cs:102-103, 327-335`.

### 4.2 Density (Default or Compact)
**Persisted**: `sidebar.pane.density` (`Platform.cs:89`); read `Host.cs:79`; written `Host.cs:287`, `Migration.cs:282`; signal `Sidebar.Density` (`Host.cs:161`); `Doc` cache keys on it (`Host.cs:185-201`). Synchronous.
**Root branch (1)**: `SidebarSection.ShapeFor` (`Layout.cs:594-597`): Compact → `EntityOneLine` (36 tall, art 24, no subtitle); Default → `EntityTwoLine` (40 tall, art 32). Glyph sections unaffected.
**Shape fan-out (~24)**: `Sidebar.cs:389, 398, 427, 610`; `Cards.cs:121`; `UI.cs:1120`; `Slot.cs:90-91, 254, 294, 341, 347-348, 387, 423, 429, 433, 497, 512`; `Rows.cs:178, 180-181, 190, 404, 828`.
**Controls**: menu `Edit.cs:238-239`, `UI.Menus.cs:159`; undo `Host.cs:273-288, 420-422`; equality `Layout.cs:637`; Settings picker `Settings.UI.Appearance.cs:553, 559-560, 592-601`.
**Ignores density (hardcodes two-line)**: flyouts `Flyout.cs:89, 97, 143-144, 300-301`; extent fallbacks `UI.cs:456, 1102, 1590, 1592, 1604, 1607, 1609`; `Slot.cs:707`.

### 4.3 Library View (List or Grid)
**Persisted** in `sidebar.json` library section `"view": "list" | "grid"`: DTO `Store.cs:299`; written `:418`; read `:441`; default List (`Layout.cs:154`); resolved `Layout.cs:573`. Synchronous (`LoadDeviceFile` in `Boot`, `Host.cs:86, 107-131`).
**Branch sites (13)**: `Planner.cs:298`, `:314`; `Library.cs:307`; `UI.Library.cs:216, 284-286, 296, 854, 857, 896`; `Edit.cs:311-312`; `Host.cs:243`; `Migration.cs:62`. Rendering: grid `Slot.cs:550-584`, `Cards.cs:108-145`. **The only "cards vs rows" switch.**

### 4.4 Other Library options (`sidebar.json`, synchronous)
- **Sort / Descending**: `Store.cs:297-298, 416-417`; branches `Planner.cs:328-329`, `Library.cs:134-142, 174-178`, `UI.Library.cs:215, 792, 815-832`, `Sidebar.cs:2457`.
- **ShowLiked**: `Store.cs:300`; branches `Layout.cs:692`, `Edit.cs:315`, `UI.Menus.cs:168, 237-238`.
- **Hidden kinds**: `HiddenItems`; branches `Layout.cs:722-746`, `Library.cs:75, 90, 103-104`.
- **Library filter chip**: key `sidebar.library.filter` (`Platform.cs:91`); read `Host.cs:87`; written `:293`, `Migration.cs:283`; branches `Host.cs:1486`; `Layout.cs:672-696`; `Planner.cs:271, 329`; `UI.Library.cs:148, 193, 215, 380, 696, 791, 823`.

### 4.5 Pane width
`sidebar.pane.width` (`Platform.cs:85`); read `Host.cs:80`; written `Host.cs:572`, `Migration.cs:280`; synchronous. Branch sites (~10): layer width `UI.cs:327, 527`; `ContentWidth` `UI.cs:2049`; `RowExtentOf` `:1605`; label tooltip `Slot.cs:453`; grid `Slot.cs:552-557`; Library columns/narrow `UI.Library.cs:40, 42, 105-107`; toolbar shape `UI.Library.cs:819-826`; overlay width `Resize.cs:77-78`.

### 4.6 User collapse
`sidebar.pane.userCollapsed` (`Platform.cs:87`); read `Host.cs:81`; written `Host.cs:573`, `Migration.cs:281`; used by `Resize.cs:57-62`, `Shell.UI.cs:245, 268`.

### 4.7 Pane mode, window band, drawer (derived, not persisted)
`Sidebar.Band` / `Sidebar.Mode` / `Sidebar.PresentedWidth` (`Host.cs:499-515`); seeded in the shell's first render before the column mounts (`Shell.UI.cs:238-248`); updated by band/presentation effects (`:251-281`). Branch sites: `UI.cs:471, 529, 531, 579, 585, 698`; `Footer.cs:41, 52, 69, 74, 84`; `UI.Library.cs:370`; ~24 `st.Compact` sites in `Planner.cs:47-328`; slot `CompactPlan` sites `Slot.cs:228, 300, 350-357, 398-399, 432, 459, 464, 516`; `Layout.cs:665`. `InDrawer`: `UI.cs:46, 51, 86-87, 194, 471, 529, 537, 632`; `UI.Library.cs:29-42, 105-116, 370, 818-824`; `Footer.cs:41`.

### 4.8 Edit mode (session only)
`Host.cs:508`; `UI.cs:498-520, 529, 579`; `Edit.cs:226-244`; `Settings.UI.Appearance.cs:554-571`; `Shell.UI.cs:260, 1062, 1432`.

### 4.9 Store versions, V2 and migrations (one-time, at boot)
- Current: `sidebar.json` v3 (`Store.cs:372-468`; version check `:389`); account file v1 (`Store.cs:480-560`; `:534`).
- Legacy read-only: `sidebar-layout.json` v1–2 (`Store.V2.cs:178-197`); its display DTO had `Density` ("compact|cozy|comfortable"), `Artwork`, `Subtitles`, `CountBadges`, `Presentation` (`:120-137`). **The migration ignores all of these except `MaxItems`** (`Migration.cs:155, 158`).
- Latch `sidebar.bootstrap.version` = 2 (`Platform.cs:81`; checked `Migration.cs:256`, set `:285`).
- Legacy keys read once (`Migration.cs:245-265`): `sidebar.design` (0 Classic, 1 LibraryV3, 2 Curated), `sidebar.classic.section.{pinned,library,playlists}`, `sidebar.v3.{filter,sort,desc,view}`, `sidebar.<classic|v3|curated>.{width,collapsed}`, `sidebar.pins.migratedToServer` (`Platform.cs:97`).
- Branch sites (6): `Migration.cs:42, 45-50` (design→layout with `CuratedLooksLikeLibrary` `:124-141`), `:62, 64` (V3 view→density), `:258-259`. `sidebar.migration.dropped` (`Platform.cs:93`) shown in Settings (`Settings.UI.Appearance.cs:575-578`).
- **No runtime branch on V2 vs V3.** Only the `PackV3` trigger name (`Sidebar.cs:3240`; `Host.cs:1826`) plus `Strings.Sidebar.V3.*` loc keys.

### 4.10 Not present on main
"Show covers": 0 hits. "Style": only `DragVisualStyle` (`UI.Edit.cs:31`; `UI.cs:1635`). "Cards": only the Grid tile (`Cards.cs`).

---

## 5. Seams into pages and the shell

**Navigation**: single path `PaneView.Navigate` → `Shell.GoTo(Shell.Parse(route, arg))` (`UI.cs:1960-1968`); fallback `LibrarySession.Navigate` (`UI.Library.cs:313-317`); route reads `Shell.Current` (`UI.cs:1457-1460`; `UI.Library.cs:381, 554, 573`) and `Shell.NameOf`; titles/glyphs via `Shell.Dest(Shell.Parse())`: `Slot.cs:243, 306, 495`; `Cards.cs:161`; `Rows.cs:508`; `UI.cs:680, 1706`; `UI.Menus.cs:46, 86, 317`; `UI.Edit.cs:418, 446, 464`; `UI.Library.cs:383, 429, 574`; `Flyout.cs:125`; `Footer.cs:44`.

**Playback**: `Acts.Play` or `Shell.OnPlayContext` (`UI.cs:1973-1980`); `Controls.NowPlaying` (`UI.cs:1340-1342`).

**Entities**: projection over `User.Me` edges (`Sidebar.cs:2715-3034`); `Entities.Ensure`/`EnsureEdge` (`:2745-2758, 2839-2847`); pins `Host.cs:1583-1760`; feed peek `Feeds.cs:147-194`; `LikedArt` `Rows.cs:539-563`; `CountHost` `:764-771`; `Entities.Track` `UI.Menus.cs:456`; comment-only alignment with `Playlist.MosaicTiles` (`Sidebar.cs:2672-2679`).

**Actions/menus**: `Actions.Services` (`UI.cs:464`; `UI.Menus.cs:37, 257, 347`); `Actions.Menu.*` (`UI.Menus.cs:295-523`); `Menus.Container` (`:391, 432`); `AppActions.Find(RenamePlaylist)` (`:753`); `Actions.OpenPicker` (`:658`); `Controls.Prompt` (`:721`); `ContentDialog` (`:91`).

**Feeds/notify**: `Notify.Items`, `ReleasesState`, `RefreshFeeds` (`Host.cs:1511-1516, 1842-1845`; `UI.cs:159-160`); `Notify.Say` (`Host.cs:356, 454, 472`; `UI.cs:115, 2005`; `UI.Drop.cs:380-382, 501`).

**Shell logs**: `Shell.History.Store`, `Shell.PlayLog` (`Host.cs:1203-1214`; `UI.cs:157-158`). **Other**: `Shell.FocusedIsTextEditor` (`UI.cs:748`); `Diagnostics.BenchHooks.ToggleSidebarSection` (`UI.cs:1913-1918`); `Shell.SidebarReplanCensus` (`Census.cs`; `UI.cs:914-955, 1035`); `Spotify.Current.IsOnline` (`Slot.cs:239`); `Drag.*` throughout `UI.Drop.cs`.

**Library write seam**: `Sidebar.LibraryWrites` (`Host.cs:1967`), installed by `<W>Spotify\Spotify.Library.cs:111`; also called by `Entities\Playlist.Page.cs`, `Album.Page.cs`, `Track.Menu.cs`, `Browse.Cards.cs`, `User.UI.cs`, `Shell.UI.cs`.

**Inbound callers**: `<W>Shell\Shell.UI.cs` band/mode/presentation `:238-281`; pane column `:521-537`; seam `:584-588`; keys `:722-765`; pane menu `:732-734`; invariant `:836-838`; title-bar toggle `:1062-1074`; pin seams `:1265-1268`; overlay drawer `:1417-1560`. `<W>Screens\Settings.UI.Appearance.cs:542-640`. `<W>Screens\Setup.UI.cs:748-758`. `<W>Shell\Shell.Palette.cs:102-105, 324-336`. `<W>Shell\Shell.Host.cs:379` (`Activate`).

**Sidebar primitives reused by pages (move with the theme)**: `Sidebar.Chevron`: `Entities\Episode.Discussion.cs:155`, `Track.UI.Bound.cs:780`, `User.Facts.UI.cs:987, 1338`, `Screens\LogsPage.UI.cs:572`. `Sidebar.Cover.Liked`: `Entities\Recents.Page.cs:625`, `Recents.UI.cs:212`, `Home\Zones.UI.cs:499`.

---

## 6. Startup: `Sidebar.Boot()`
Called at `<W>App.cs:194`, after `Video.InstallMirror` and before `HomeLayout.Boot`, `InstallActionSeams` (`:196`), `Spotify.Library.Install` (`:197`) and `Shell.Run`. Body `Sidebar.Host.cs:72-94`.

**Synchronous, in order**: 1 profile dir (`:74`; `UseProfileDir` override, `--fake` demo temp dir `:100-105`, or `%LOCALAPPDATA%\Wavee\WaveeMusic`); 2 migration (`:75`, `SidebarMigrationHost.RunIfNeeded` `Migration.cs:254-291`: if the latch is below 2 it reads legacy keys and `sidebar-layout.json`(+`.bak`) with `File.ReadAllBytes` (`Store.V2.cs:182-197`), writes `sidebar.json` and the account file, then `FlushNow` and **blocks in `WaitForWrites(2000)` per file** (`Migration.cs:295-315`) — up to ~4 s on the boot thread); 3 settings (`:77-83`: `Layout`, `Density`, `Width` clamped, `UserCollapsed`, `Seam` = 48 if collapsed else width, `PresentedWidth` = `Seam`); 4 device file (`:85-86`, `LoadDeviceFile` `:107-131`: sync read, `.bak` fallback, `MarkCorrupt`, defaults; bumps `LayoutVersion`); 5 library filter (`:87`); 6 account (`:89-91`: `EnsureAccount` → `SwapAccount` `Accounts.cs:40-68` → `LoadAccount` `:70-90`; pending-file adopt, sync read, `Pins.LoadFrom`, expanded folders, first-seen stamps); 7 hook (`:92`: `Pins.OnChanged = CommitAccount`).

**Deferred**: UI-thread dispatcher `Sidebar.Activate(post)` at `Shell.Host.cs:379` (until then `ToUi` inline, `Host.cs:46`); projection binder constructed in the first `PaneHost` render (`UI.cs:72, 128`), first rebuild in `PumpBinder` (`UI.cs:73, 138-172`) — until then the pane plans skeletons (`UI.cs:1056-1060`); first-seen stamps lazy (`Host.cs:1447`); new releases on demand (`Host.cs:1508-1517`); migration toast after first mount (`UI.cs:81, 107-117`); Band/Mode/PresentedWidth seeded by the shell's first render (`Shell.UI.cs:238-248`); Library grid columns/narrow flag computed in `LibraryMode` render (`UI.Library.cs:39-48`); write seams `Sidebar.LibraryWrites` (`App.cs:197`; `Spotify.Library.cs:111`) and pin action seams (`App.cs:196`; `UI.Menus.cs:35-48`); shutdown `Sidebar.Shutdown` at `App.cs:241` (`Host.cs:758-776`).

**Available synchronously before the first frame**: layout id, density, width, user collapse, library filter, the whole `sidebar.json` overlay state (sections, collapse, limits, Library sort/view/ShowLiked/hidden kinds), pins with expanded folders. The window band and mode become available in the shell's first render, before the column mounts.
