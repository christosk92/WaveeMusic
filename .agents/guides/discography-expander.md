---
guide: discography-expander
scope: The artist-page inline album expander — ExpandableAlbumGrid, ExpandingGridLayout, and the AlbumDetailPanel overlay that opens a tracklist inline between discography rows.
last_verified: 2026-05-21
verified_by: read+grep over src/Wavee.UI.WinUI/Controls/AlbumDetailPanel, the ArtistPage discography section, and ArtistDiscographyViewModel
root_index: AGENTS.md (Codex) and CLAUDE.md (Claude Code)
---

# Wavee Discography Inline Expander

On the artist page, the **Albums** and **Singles & EPs** grids let you click a
release to open an `AlbumDetailPanel` (album art + tracklist + Play/Shuffle)
*inline*, pushing the rows below it down — Apple-Music style. This guide owns
that subsystem.

It is three controls, all in `Controls/AlbumDetailPanel/`:

- **`ExpandableAlbumGrid`** — the entry-point control. A virtualizing card grid
  that hosts a single inline detail panel. This is what `ArtistPage` places.
- **`ExpandingGridLayout`** — a custom `VirtualizingLayout` that lays album
  cards in a uniform grid and reserves an empty full-width band below the
  expanded row.
- **`AlbumDetailPanel`** — the inline detail panel itself (album header,
  Play/Shuffle, `TrackListView`, composition-mask album art, upward notch).

## How To Use This Guide

1. Read **Architecture — and what NOT to do** first. This subsystem was
   rewritten twice; the dead approaches caused image flashing and mis-rendered
   panels. Do not re-introduce them.
2. **Quick-find table** locates the two live surfaces.
3. **Expand / collapse data flow** is the path a click takes.
4. **ExpandingGridLayout** covers the custom layout maths.

Re-verification commands:

```
rg -n "ExpandableAlbumGrid" src/Wavee.UI.WinUI -g "*.xaml" -g "*.cs"
rg -n "ExpandingGridLayout|AlbumDetailPanel" src/Wavee.UI.WinUI/Controls/AlbumDetailPanel
rg -n "ExpandAlbumCommand|CollapseAlbumCommand|ExpandedAlbum|ExpandedAlbumTracks" src/Wavee.UI.WinUI/ViewModels/Artist/ArtistDiscographyViewModel.cs
```

Scope:
- Included: `ExpandableAlbumGrid`, `ExpandingGridLayout`, `AlbumDetailPanel`,
  the `ArtistPage` discography wiring, and the `ArtistDiscographyViewModel`
  expand/collapse surface (`ExpandedAlbum`, `ExpandedAlbumTracks`,
  `IsLoadingExpandedTracks`, `ExpandAlbumCommand`, `CollapseAlbumCommand`).
- Not included: the `ContentCard`s themselves (`.agents/guides/content-card.md`),
  the `TrackListView` inside the panel (`.agents/guides/track-and-episode-ui.md`),
  `CompositionImage` (`.agents/guides/composition-image.md`). The artist page's
  **Appears-on** strip and the full **ArtistDiscographyPage** grid are plain
  `ItemsRepeater`s of `ContentCard`s and do **not** expand — they are out of
  scope.

## Quick-find Table

| Surface | Host file:line | Data source | Notes |
| --- | --- | --- | --- |
| Artist page — Albums grid | `Views/ArtistPage.xaml:724` | `LazyReleaseItem` ← `ArtistDiscographyViewModel.AlbumsCapped` | `x:Name="AlbumsGrid"`. Capped at 30 (full list lives on `ArtistDiscographyPage`). |
| Artist page — Singles & EPs grid | `Views/ArtistPage.xaml:783` | `LazyReleaseItem` ← `ArtistDiscographyViewModel.SinglesCapped` | `x:Name="SinglesGrid"`. Shares one `ViewModel.Discography.ExpandedAlbum` with `AlbumsGrid`, so expanding in one auto-collapses the other. |

Both grids bind the same four inputs and three events; the host glue is
`ArtistPage.xaml.cs` `OnDiscographyExpandRequested` / `OnDiscographyCollapseRequested`
/ `OnDiscographyExpandLayoutReady` (~`ArtistPage.xaml.cs:1181`).

## Architecture — and what NOT to do

The whole subsystem exists to satisfy one hard constraint:

> **An expand/collapse must never unload an album card.** A `ContentCard`
> leaving and re-entering the visual tree blanks and reloads its
> `CompositionImage` surface — that is a visible artwork flash.

The current design achieves it like this:

```
ExpandableAlbumGrid : UserControl
  └─ Grid
       ├─ ItemsRepeater          ItemsSource = _items (ContentCards only)
       │     └─ Layout = ExpandingGridLayout   ← reserves an empty band
       └─ AlbumDetailPanel       ← ONE persistent overlay child, Margin-positioned
```

- The repeater hosts **only** `ContentCard`s, via **one** `DataTemplate`.
- The `AlbumDetailPanel` is a **single persistent child** of the root `Grid`,
  not a repeater item. Expanding flips its `Visibility`, sets its `Album`, and
  moves it (`Margin.Top`) into the band the layout reserves. It is never
  realized, recycled, or moved between containers.
- Expanding changes only `ExpandingGridLayout.ExpandedAlbumOrdinal` /
  `ExpanderHeight` → the layout reserves a gap → realized cards re-arrange
  (and glide). No collection mutation touches the cards.

**Do NOT re-introduce any of these — each was tried and removed:**

1. **Splitting one `ItemsRepeater` into two**, or reassigning a repeater's
   `ItemsSource`, to insert the panel between rows. Reassigning `ItemsSource`
   resets the repeater → every card's `ElementClearing` fires → every card
   reloads → full-grid artwork flash.
2. **Hosting the panel as a sentinel item** inside the repeater's collection
   with a `DataTemplateSelector` (card template vs panel template).
   `ItemsRepeater` + `DataTemplateSelector` mis-recycles when the sentinel item
   moves between rows — `AlbumDetailPanel`s get handed to card slots and render
   as narrow columns. The selector and the sentinel were both deleted.
3. **`ObservableCollection.Move` on a repeater-hosted panel item.** Switching
   the expanded album by moving a sentinel confused the repeater's
   element↔item mapping. There is no sentinel now; switching just updates the
   one persistent panel.

If you ever need the panel to be richer, edit `AlbumDetailPanel` — never put it
back into the repeater.

## Core Structure

| File | Owns |
| --- | --- |
| `ExpandableAlbumGrid.xaml` | Root `Grid`: the `ItemsRepeater` with one `DiscographyCardTemplate` (`ContentCard`, `AutoNavigateOnTap=False`, `SecondaryActionVisible=True`), the `ExpandingGridLayout`, and the persistent `AlbumDetailPanel` overlay (`Visibility=Collapsed` at rest). |
| `ExpandableAlbumGrid.xaml.cs` | The control. `_items` mirror collection + identity diff (`SyncFromSource`), the `ExpandedAlbum` reaction (`OnExpandedAlbumChanged`), panel positioning (`OnExpanderGeometryChanged`), band-height feedback (`OnDetailPanelSizeChanged`), card glide, accent-colour fetch, `CollapseNow()`. |
| `ExpandingGridLayout.cs` | Custom `VirtualizingLayout`: uniform virtualized card grid + reserved expander band. Constants `MinItemWidth=160`, `ColumnSpacing=16`, `RowSpacing=20`, `MinCardHeight=220`. |
| `AlbumDetailPanel.xaml` / `.xaml.cs` | The inline detail panel: album name/type/year, Play/Shuffle, `TrackListView`, composition alpha-mask album art, upward `NotchTriangle`. Implicit show/hide animations. Also an `INavCacheSurfaceParticipant`. |

### `ExpandableAlbumGrid` public surface

- **DPs:** `ItemsSource` (releases), `ExpandedAlbum` (`LazyReleaseItem`, OneWay
  from the VM — the single source of truth), `ExpandedAlbumTracks`,
  `IsLoadingExpandedTracks`.
- **Events:** `ExpandRequested(LazyReleaseItem)`, `CollapseRequested`,
  `ExpandLayoutReady(AlbumDetailPanel)`.
- **Method:** `CollapseNow()` — synchronous teardown for page unload / dispose.

### `AlbumDetailPanel` public surface

- **DPs:** `Album` (`ArtistReleaseVm`), `Tracks`, `IsLoading`, `ColorHex`,
  `NotchOffsetX`. **Events:** `CloseRequested`, `PlayRequested`, `ShuffleRequested`.
- `Loaded`/`Unloaded` are re-runnable (composition mask is set up on `Loaded`,
  disposed on `Unloaded`). It is a permanent child today so `Loaded` fires
  once; the re-runnable wiring is harmless and kept defensively.

## Expand / collapse data flow

The view-model owns the truth; the control is a pure reaction.

1. User clicks a card → `ContentCard.CardClick` → `ExpandableAlbumGrid.OnCardClick`.
   The clicked release is resolved by `Repeater.GetElementIndex(card)` →
   `_items[index]` (never via `DataContext` — `ItemsRepeater` does not set it).
2. The control raises `ExpandRequested(item)` (or `CollapseRequested` if the
   item is already expanded).
3. `ArtistPage` routes it: `OnDiscographyExpandRequested` →
   `ViewModel.Discography.ExpandAlbumCommand.Execute(item)`.
4. `ArtistDiscographyViewModel.ExpandAlbum` sets `ExpandedAlbum`, clears and
   repopulates `ExpandedAlbumTracks` (placeholder rows synchronously, then the
   real tracks after an `await`), toggles `IsLoadingExpandedTracks`. It guards
   the post-`await` write with `if (ExpandedAlbum != album) return;` so a
   collapse / album-switch mid-fetch cannot overwrite the live list.
5. `ExpandedAlbum` change → x:Bind → **both** grids' `ExpandedAlbum` DP →
   `OnExpandedAlbumChanged`. The grid whose `_items` contains the album expands;
   the other collapses.
6. `OnExpandedAlbumChanged` (expand branch): set `DetailPanel.Album/Tracks/
   IsLoading/ColorHex`, `Visibility=Visible`, then
   `GridLayout.ExpandedAlbumOrdinal = _items.IndexOf(album)`.
7. The layout reflows and raises `ExpanderGeometryChanged(gapTop, notchX)` →
   `OnExpanderGeometryChanged` sets `DetailPanel.Margin.Top = gapTop` and
   `DetailPanel.NotchOffsetX`.
8. The panel measures itself → `SizeChanged` → `OnDetailPanelSizeChanged` sets
   `GridLayout.ExpanderHeight` so the reserved band matches the real panel
   height (a one-shot feedback; an initial estimate of 360 is used first).

Collapse is the mirror: VM `ExpandedAlbum = null` → `OnExpandedAlbumChanged`
collapse branch → `DetailPanel.Visibility=Collapsed`,
`GridLayout.ExpandedAlbumOrdinal = -1`.

### Source mirroring (`_items`)

`ExpandableAlbumGrid` does **not** bind the repeater straight to the VM
collection. It keeps a private `ObservableCollection<LazyReleaseItem> _items`
and mirrors `ItemsSource` into it with an identity diff (`SyncFromSource`).
Reason: the VM's `AlbumsCapped` / `SinglesCapped` are refreshed with
`ReplaceWith`, which fires a single `Reset` — and a `Reset` makes
`ItemsRepeater` rebuild *every* card container (a flash during pagination).
The diff emits granular Insert / Move / Remove instead, so unchanged card
containers are never rebuilt.

### Scroll behaviour

Expanding never scrolls the page on its own. After an expand the control raises
`ExpandLayoutReady(panel)`; `ArtistPage.OnDiscographyExpandLayoutReady` does a
**single minimal** `PageScrollView.ScrollTo` *only* when the panel's bottom is
clipped below the viewport — no recenter, so the open never reads as a jump.

### Glide

When the layout reflows, the cards below the band glide to their new positions
via a composition implicit `Offset` animation. It is attached to the realized
`ContentCard` visuals only, for a ~300 ms window around each expand/collapse
(`PrepareGlide` / a `DispatcherTimer`), so it never animates first realization
or scroll re-layout.

## ExpandingGridLayout

A `VirtualizingLayout` (same family as `SafeUniformGridLayout` etc. in
`Controls/Layouts/`). It lays album cards in a uniform grid; row height is
uniform, grown to the tallest realized card and clamped to `MinCardHeight`
(220). Column count is derived from available width.

When `ExpandedAlbumOrdinal >= 0` it inserts an empty band of `ExpanderHeight`
directly below that album's row: every card row after the expanded row is
pushed down by `ExpanderHeight + RowSpacing`. The band itself holds no element
— the `AlbumDetailPanel` overlay (owned by `ExpandableAlbumGrid`) is positioned
into it.

- **`ExpandedAlbumOrdinal`** (int, -1 = collapsed) and **`ExpanderHeight`**
  (double) are set by `ExpandableAlbumGrid`; each setter calls
  `InvalidateMeasure`.
- **`ExpanderGeometryChanged(gapTop, notchX)`** is raised after arrange (only
  when the values change). `gapTop` is the band's top Y (or -1 collapsed);
  `notchX` is the X centre of the expanded album's column for the panel notch.

## Change Guidance

- **Add another expandable discography grid** → place `<adp:ExpandableAlbumGrid>`,
  bind `ItemsSource` / `ExpandedAlbum` (OneWay) / `ExpandedAlbumTracks` /
  `IsLoadingExpandedTracks` to the VM, and wire `ExpandRequested` /
  `CollapseRequested` / `ExpandLayoutReady` to the same `ArtistPage` handlers.
  All grids sharing one VM `ExpandedAlbum` cross-collapse automatically.
- **Change the detail panel's content / look** → edit `AlbumDetailPanel`. Keep
  it a standalone control; never host it inside the repeater.
- **Change the card** → it is a plain `ContentCard`; see
  `.agents/guides/content-card.md`. The discography card template lives in
  `ExpandableAlbumGrid.xaml`.
- **Change grid metrics** (column width, spacing, row height) → the constants
  in `ExpandingGridLayout.cs`.
- **Change expand/collapse semantics** (what loads, the track list) → the
  `[RelayCommand]`s in `ArtistDiscographyViewModel` — the control only reacts.
- **Gotcha — `LazyReleaseItem.Id` is intentionally not `required`.** A
  dependency property typed as a class with a `required` member makes the WinUI
  XAML type-info generator emit an uncompilable `new LazyReleaseItem()`
  activator. `ExpandedAlbum` is a `LazyReleaseItem`-typed DP, so `Id` must stay
  a plain (defaulted) property.

When the panel renders wrong:
- **Narrow `AlbumDetailPanel`s at card positions** → something put the panel
  back into the repeater (a sentinel item / `DataTemplateSelector`). See
  *Architecture — and what NOT to do*.
- **Cards flash artwork on expand** → something is unloading cards (a split
  repeater, an `ItemsSource` reassignment, or `ElementClearing` releasing
  images). The cards must stay realized across expand/collapse.
- **Band height wrong / panel overlaps cards** → the `ExpanderHeight` feedback
  (`OnDetailPanelSizeChanged`) isn't reaching the layout.

## Keeping This Guide Current

1. If you add/remove an `ExpandableAlbumGrid` consumer, update the **Quick-find
   table** with the new `file:line` + data source.
2. If you change the expand/collapse flow, the layout band maths, or the
   mirror diff, update the matching section.
3. Re-run the re-verification commands; bump `last_verified` / `verified_by`.
