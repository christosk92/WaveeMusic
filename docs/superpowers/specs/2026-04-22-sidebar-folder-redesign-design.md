# Sidebar Folder Redesign — Design Spec

**Date:** 2026-04-22
**Scope:** `Wavee.UI.WinUI/Controls/Sidebar/*`, `Wavee.UI.WinUI/ViewModels/ShellViewModel.cs` (sidebar population helpers only)
**Status:** Approved for plan

## Problem

User-created folders in the sidebar render as undifferentiated "caption" rows — no folder icon (despite `IsFolder` and `IconSource` being wired in the model), text styled identically to top-level section headers, and tree-connector lines that break across row boundaries because the per-row stitching strategy can't survive the 2-px `ChildrenPresenter` margin and the 12-px post-folder-header gap. Visually, a folder reads as "another caption called New Folder," and the rails look broken in the common case.

Current mechanics in `Wavee.UI.WinUI/Controls/Sidebar/SidebarStyles.xaml`:

- Every folder-header visual state (`Expanded`, `Collapsed`, `NoChildren`) hard-sets `IconPresenter.Visibility=Collapsed` and restyles the row text to `FontSize=12, FontWeight=SemiBold, Foreground=TextFillColorTertiary` — the same treatment as non-interactive section labels.
- The `TreeConnectorStrip` `ItemsControl` (lines 96–154) composes a tree rail from per-cell rectangles. `Margin="0,-2,0,-2"` bleeds past the `ElementGrid`'s 2-px vertical padding, but cannot bleed past the `ChildrenPresenter.Margin="-4,2"` or the `RootPanel.Margin="0,0,0,12"` that an expanded folder header injects. Gaps are inevitable by construction.
- `SidebarItemModel` carries `IsLastSibling`, `AncestorContinuations`, `TreeCells`, and `TreeConnectorKind` purely to feed the connector strip. `ShellViewModel.BuildTreeCells` + `AppendBool` + parameter-threading exists only for this.

## Research-driven direction

Consensus across Spotify (2023→), Apple Music (Mac), Microsoft Fluent guidance, and the Windows `TreeView`/Files-app precedents: tree rails add visual clutter and hurt more than they help in a content-sidebar context. Microsoft's own Win32 Tree View guidance explicitly recommends reconsidering connecting lines. Spotify and Apple Music both render folders as plain indented rows with no rails; Windows File Explorer has never shipped rails in its navigation pane.

The chosen direction is an indent-driven layout with a single faint depth guide per ancestor folder — no per-row stitching.

## Design

### Three-tier visual hierarchy

**Tier 1 — Section label** (`Your Library`, `Playlists`, `Made For You`)
Chromeless, non-row. Rendered via a new `SectionHeader` visual state:

- `ElementGrid.Background = Transparent` (no hover fill)
- `ElementGrid.Height = 28`
- `IconPresenter.Visibility = Collapsed`
- `ExpandCollapseChevron.Visibility = Collapsed`
- Text: `FontSize=11`, `FontWeight=SemiBold`, `CharacterCasing=Upper`, `CharacterSpacing=50` (~0.05 em tracking — WinUI `CharacterSpacing` is thousandths of an em), `Foreground=TextFillColorTertiaryBrush` @ 0.7 opacity (new `SidebarSectionHeaderForegroundBrush`).
- Click behavior preserved: collapsing the section still works (no visual affordance signals it; keyboard users still get it via `Enter`).

Only the three top-level groups get this state. Driven by a new `IsSectionHeader` flag on `SidebarItemModel` set explicitly by `ShellViewModel` when constructing those three groups.

**Tier 2 — Folder row** (user-created folders, `IsFolder=true`)
Full row chrome matching playlists:

- 44 px row height (matches playlist row height with 32 px artwork).
- **Icon slot:** a 32×32 rounded tile (`CornerRadius=6`) filled with `AccentFillColorTertiaryBrush` (the Fluent system brush — already subtly transparent; do not stack additional opacity). Contains a 16 px folder glyph centered, glyph swaps on expand: `E8B7` (closed) ↔ `E838` (open). `FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"`. If visual test shows the brush is too loud against the sidebar background, the plan may substitute a new `SidebarFolderTileBrush` resource — but that's an implementation-level fallback, not a design requirement.
- **Text:** default sidebar row text — primary-weight, `TextFillColorPrimaryBrush`, 14 px (inherited from the default, no per-state override).
- **Chevron:** right-side, rotates down when expanded (unchanged position; current layout).
- **Hover / selection / drag-over states:** identical to playlist rows (`PointerOver`, `Pressed`, `NormalSelected`, `DragOnTop`, etc.).
- **Click:** row-click toggles expansion. No folder-destination page (out of scope).

**Tier 3 — Playlist row** (leaf)
Unchanged. 32 px artwork tile, primary text, existing hover/selection.

### Indent + depth guide

- Indent per nesting level: **20 px** (was 16 via the 16-px connector cells).
- `ElementBorder.Padding.Left = Depth * 20`, driven from `SidebarItemModel.Depth`. Either via a `DepthToIndentConverter(20)` or a computed `IndentLeft` property that fires alongside `Depth` changes — implementation choice defers to the plan.
- **Depth guide:** one 1-px `Rectangle` rendered inside each folder's `ChildrenPresenter`. Not per-row, not per-ancestor composition — one rectangle per expanded folder.
  - `Fill = TextFillColorTertiaryBrush`, `Opacity = 0.25`.
  - `HorizontalAlignment = Left`, `Margin.Left = 10`, `Width = 1`, `VerticalAlignment = Stretch`.
  - Because it's a child of `ChildrenPresenter`, its height is driven by the existing `MaxHeight` storyboard and collapses with the folder automatically.
  - Visible only when the host `SidebarItem` is a folder (`IsFolder=true`).

Depth composition emerges from nesting: folder A's children include folder B; A's `ChildrenPresenter` renders one guide for A; B's `ChildrenPresenter` renders one guide for B. The viewer sees two guides at the right offsets without any depth-aware composition logic in the template.

### What goes away

- **XAML (`SidebarStyles.xaml`):**
  - `TreeConnectorStrip` `ItemsControl` and its column definition (current lines 67–68, 82–154).
  - `ChildrenPresenter.Margin="-4,2"` → `"0,0"` (line 289).
  - `RootPanel.Margin="0,0,0,12"` setters in `Expanded` and `NoExpansionWithPadding` states (lines 340, 371, 412) → `"0"`.
  - `IconPresenter.Visibility=Collapsed` setters in `Expanded` and `Collapsed` states (lines 373, 385).
  - `ItemNameTextBlock` font-size / font-weight / foreground overrides in `Expanded`, `Collapsed`, `NoChildren`, `NoChildrenWithPlaceholder` states — text inherits the default.
  - `TreeConnectorMatchConverter` references (and the converter class itself if unused elsewhere — verify via grep before removing).

- **C# (`SidebarItemModel`, `ISidebarItemModel`, `ShellViewModel`):**
  - `SidebarItemModel.IsLastSibling`, `AncestorContinuations`, `TreeCells` properties and their backing fields.
  - `TreeConnectorKind.cs` — delete file.
  - `ShellViewModel.BuildTreeCells` (current line 719), `AppendBool`, and the `ancestorContinuations`/`isLastSibling` parameter threading through `AppendNodeChildren` and `BuildFolderSidebarItem`.
  - The `hasChildSelection` propagation path in `SidebarItem.cs:498` that borrows a collapsed folder's selection indicator from a selected descendant. Once folders are themselves proper rows, selection stays on the actual selected leaf.

- **Interfaces:** corresponding member removals from `ISidebarItemModel`.

### What's added / modified

- **New `SectionHeader` visual state** in `SidebarStyles.xaml`'s `ExpansionStates` group.
- **New `IsSectionHeader` property** on `SidebarItemModel` (and `ISidebarItemModel`). `ShellViewModel` sets this to `true` when constructing the three top-level groups; defaults to `false` everywhere else.
- **New state-dispatch branch** in `SidebarItem.UpdateExpansionState` (`SidebarItem.cs`): if `IsSectionHeader`, `VisualStateManager.GoToState(this, "SectionHeader", …)` short-circuiting the `Expanded`/`Collapsed` path.
- **`SidebarItem.CreateFolderIcon(bool isOpen)` helper** mirroring the structure of `CreateArtworkIcon`: 32×32 `Grid` host (`Tag="FolderIcon"`), 32×32 `Border` with accent-tinted fill, centered 16 px `FontIcon` with the folder glyph. `UpdateIconPresenter` special-cases `Item.IsFolder` to route through this helper instead of the default icon pipeline.
- **Indent binding** on `ElementBorder.Padding`, driven from `Depth`.
- **Depth-guide `Rectangle`** in the `ChildrenPresenter` region of the control template, visibility-bound to `IsFolder`.

### Edge cases (handled)

- **Compact sidebar mode.** The `CompactGroupHeader` visual state already governs compact behavior for group-header rows and is unaffected. Folder rows in compact mode collapse to icon-only via the `Compact` state; the accent-tinted tile renders cleanly in the 48-px column.
- **Empty-folder placeholder.** The "Drop items here to pin" rectangle (lines 302–326) lives inside the indented `ChildrenPresenter`, so it automatically sits one indent level in and gets the depth guide to its left. No changes required.
- **Expand/collapse glyph swap.** `ShellViewModel.OnSidebarGroupPropertyChanged` already swaps `FolderOpen` ↔ `Folder` on `IsExpanded` changes; unchanged.
- **Drag-over auto-expand.** `SidebarItem.ItemBorder_DragOver` unchanged — folders still auto-expand on drag hover. Depth guide animates open with the children.
- **Drag-insert indicators.** `DragInsertAbove`, `DragInsertBelow`, `DragOnTop` visual states unchanged — they target `ElementGrid` and `DragTargetIndicator`, not the removed strip.
- **Persistence.** `ShellSessionService.UpdateSidebarGroupExpansion` keys off `Tag`, which is unchanged. Previously persisted expansion state for user folders continues to work.

### Non-goals (explicitly out of scope for v1)

- Folder badge counts (aggregate track-count roll-up).
- Folders as navigation destinations (clicking opens a folder-contents page).
- Nested-drag-reordering UX improvements.
- Per-section artist palette theming (existing `ThemeColorService` integration stays identical).
- Keyboard shortcut for collapse-all / expand-all.
- Replacing the right-side chevron with a left-side disclosure triangle.
- Hover-only variant of the depth guide.

## Success criteria

1. Folder rows render with a 32×32 accent-tinted tile and a 16 px folder glyph that toggles between `E8B7` (closed) and `E838` (open) on expand/collapse.
2. User-created folders match playlist rows in height (44 px), hover state, selection state, and text style (primary weight, primary color).
3. Section headers (`Your Library`, `Playlists`, `Made For You`) render as all-caps 11 px `SemiBold` labels at `TextFillColorTertiaryBrush` @ 0.7 opacity — visually distinct from folders at a glance.
4. Exactly one 1-px depth guide per expanded folder is visible beside its children, at `TextFillColorTertiaryBrush` @ 0.25 opacity. No gaps, no stitching artifacts, animates with expand/collapse.
5. `git grep -n "TreeCells\|AncestorContinuations\|BuildTreeCells\|TreeConnectorKind\|IsLastSibling"` returns zero hits after the change.
6. Playlist (leaf) row appearance is unchanged from before the redesign.
7. Drag-and-drop onto folders still auto-expands them and still accepts track drops (existing `ItemBorder_DragOver` / `ItemBorder_Drop` behavior preserved).
8. Compact sidebar mode still shows folders as icon-only tiles in the 48-px column with no horizontal overflow.
9. Previously persisted folder-expansion state (`ShellSessionService`) loads identically post-change — folders open on launch if they were open before.

## Affected files

- `Wavee.UI.WinUI/Controls/Sidebar/SidebarStyles.xaml`
- `Wavee.UI.WinUI/Controls/Sidebar/SidebarItem.cs`
- `Wavee.UI.WinUI/Controls/Sidebar/SidebarItemModel.cs`
- `Wavee.UI.WinUI/Controls/Sidebar/ISidebarItemModel.cs`
- `Wavee.UI.WinUI/Controls/Sidebar/TreeConnectorKind.cs` — **delete**
- `Wavee.UI.WinUI/ViewModels/ShellViewModel.cs` (sidebar-population helpers only)
- `Wavee.UI.WinUI/Converters/TreeConnectorMatchConverter.cs` — **delete if unused elsewhere** (verify first)

## Rollback

All changes are local to the sidebar control and its ViewModel feeder. No schema changes, no serialized-state changes, no API boundary changes. Revert the commit to restore prior behavior.
