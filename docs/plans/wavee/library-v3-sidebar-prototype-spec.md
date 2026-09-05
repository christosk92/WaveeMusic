# Library V3 sidebar rework — prototype spec (HTML first, engine later)

**Status:** spec for a **published HTML Artifact prototype**. No engine/app code changes are in scope. §9 is the short
brief a later `library-v3-sidebar-implementation.md` would start from; building it in FluentGpu is explicitly OUT of
scope now.

**Why prototype first.** The current Library V3 chrome has four visible defects the user called out — the collapsed
rail is bare stacked icons + raw circular avatars with no cap; the "Your Library" title truncates to "Your.." with room
to spare; the sort/view pill drops its label under no real width pressure; the search box reads half-active; and the
combined "Sort by / View as" flyout pairs a radio list with four unlabelled icon toggles. The research synthesis
(Discord rail, WinUI NavigationView/Mica, VS Code/Finder/Explorer) gives concrete numbers. The prototype exists to make
those numbers visible and adjustable **before** touching `LibraryV3Header.cs`, `V3SortViewFlyout.cs`, `LibraryV3Search.cs`
or `SidebarPaneRail.cs`.

Current engine facts the prototype must stay compatible with (so the numbers below are not fantasy):
`LibraryV3Metrics.HeaderHeight = 44`, `ToolbarHeight = 36`, `ChipRailHeight = 40`, `NavRowHeight = 40`
(`src/apps/Wavee/Features/Sidebar/Modes/LibraryV3/LibraryV3Metrics.cs:22-35`); rail tile `SidebarRailItem.Box = 40`,
`ArtEdge = 36` inside a 56-DIP strip (`Shared/SidebarRailItem.cs:24-27`, `Pane/SidebarPaneRail.cs:11`); search shapes
decided by `LibraryV3SearchRules.Resolve` with `ClosedWidth = 32`, `InlineWidth = 300`, icon-only sort below 280
(`Modes/LibraryV3/LibraryV3SearchRules.cs:15-45`); the row height ladder Compact 32 · Cozy 40/44 · Comfortable 44/48
(`LibraryV3Metrics.cs:12-13`).

---

## 1. Deliverable

One self-contained `library-v3-prototype.html` (inline CSS + vanilla JS, no framework, no external assets except
optional Google Fonts fallback to `Segoe UI Variable, "Segoe UI", system-ui`). Published as an Artifact; light and dark
via `prefers-color-scheme` + `data-theme`. A **control strip** at the top of the page drives every state in §6 so a
reviewer can walk the matrix without editing code:

```
┌ controls ─────────────────────────────────────────────────────────────────────────────────────────┐
│ pane width ◐────────●──── 320   [collapsed ☐]   pins: (0)(3)(12)(30)   header: wide│narrow│tiny   │
│ search: idle│expanded│focused   flyout: none│sort│view(grid)│view(list)   theme: auto│light│dark   │
└───────────────────────────────────────────────────────────────────────────────────────────────────┘
┌ window mock 1280×800 ───────────────────────────────────────────────────────────────────────────────┐
│ ┌ pane (Mica base) ─┐ ┌ content layer (card) ──────────────────────────────────────────────────────┐ │
│ │ RailColumn  or    │ │  radius 8 top-left only, 1px stroke top+left, LayerFillColorDefault          │ │
│ │ ExpandedPane      │ │                                                                             │ │
│ └───────────────────┘ └─────────────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

The pane width slider range is **56 (rail) and 180–480 (expanded)**; the collapsed toggle snaps to 56.

---

## 2. Tokens (CSS custom properties — one source, both themes)

| Token | Light | Dark | Use |
|---|---|---|---|
| `--mica-base` | `#F3F3F3` @ 100 % over a soft desktop wash (§7) | `#202020` | pane ground |
| `--layer-fill` (LayerFillColorDefault) | `rgba(255,255,255,.70)` | `rgba(58,58,58,.30)` | content card |
| `--card-stroke` (CardStrokeColorDefault) | `rgba(0,0,0,.0578)` | `rgba(0,0,0,.10)` | content card top/left stroke, chip outline |
| `--acrylic-inapp` (AcrylicInAppFillColorDefault) | `rgba(252,252,252,.85)` + `backdrop-filter: blur(30px) saturate(125%)` | `rgba(44,44,44,.96)` + same blur | overlay pane (compact/overlay mode), flyouts |
| `--text-primary` / `--text-secondary` / `--text-tertiary` | `#1A1A1A` / `rgba(0,0,0,.61)` / `rgba(0,0,0,.45)` | `#FFFFFF` / `rgba(255,255,255,.79)` / `rgba(255,255,255,.55)` | type ranks |
| `--accent` | `#0067C0` | `#4CC2FF` | selection pill, active chip, slider thumb, active view segment |
| `--accent-subtle` | `rgba(0,103,192,.08)` | `rgba(76,194,255,.10)` | hover/selected row plate |
| `--fill-subtle` / `--fill-subtle-hover` | `rgba(0,0,0,.04)` / `rgba(0,0,0,.06)` | `rgba(255,255,255,.06)` / `rgba(255,255,255,.08)` | icon buttons, segments |
| `--divider` | `rgba(0,0,0,.08)` | `rgba(255,255,255,.08)` | 1px rules |
| `--radius-control` / `--radius-card` / `--radius-flyout` | `4px` / `8px` / `8px` | | |
| `--shadow-flyout` | `0 8px 16px rgba(0,0,0,.14)` | `0 8px 16px rgba(0,0,0,.26)` | flyouts |
| `--font` | `"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif` | | |

Type ramp (px/line): Caption 12/16 · Body 14/20 · BodyStrong 14/20 600 · Subtitle 20/28 600. Header title uses **15/20 600**
(the current engine value). Spacing grid: 4 (XXS) · 8 (S) · 12 (M) · 16 (L) · 24 (XXL) · 32 (XXXL).

---

## 3. Component specs

### 3.1 `RailColumn` (collapsed pane, width 56)

```
 56 ─────────────►
┌──────┐  ▲ 8 top pad
│ [≡]  │  36×36 pane-toggle button (glyph 16)                      ┐
│ [⌂]  │  36×36 Home                                                │ system tier (fixed)
│ [⌕]  │  36×36 Search                                              │ 36 pitch (36 + 0 gap? no: 32 + 4)
│ [♫]  │  36×36 Your Library (selected → pill)                      ┘
│ ──── │  divider 24×1, margins 6 above/below
│ (●)  │  pinned avatar 40, pitch 44 (40 + 4 gap)   ┐
│ (●)  │                                              │ pinned tier — up to `fitPins`
│ (●)  │  ◂ now-playing badge 12 px, bottom-right      ┘
│ ──── │  divider
│ (●)  │  recents tier — fills remaining; last slot may be the "…" tile
│ (…)  │  overflow tile 40 (glyph 16) → flyout list of everything that did not fit
│      │  16–24 px fade mask top/bottom instead of a scrollbar
└──────┘  ▼ 12 bottom pad
```

- Tile: 40×40, radius 8 for avatars (playlist/album art), **circle** for artists; hover: `--fill-subtle-hover` plate
  40×40 radius 8 behind the art; tooltip to the RIGHT after **300 ms**, arrow-less WinUI tooltip (Caption text,
  8×6 padding, radius 4, `--acrylic-inapp`).
- System-item buttons: 36×36 hit box, 16 px glyph (20 for Home), radius 4, centred in the 56 column (10 px side margin).
- **Selection**: WinUI pill — 3 px wide × 16 px tall, radius 1.5, `--accent`, vertically centred, hugging the pane's
  left edge (x = 0). Only ONE pill in the rail at a time; applies to system items AND tiles.
- **Now-playing badge**: 12 px circle, `--accent` fill, 2 px `--mica-base` ring, anchored bottom-right of the tile
  (offset −2, −2); inside it a 6 px equalizer glyph or a plain dot.
- **Capacity rule** (the "never stack forever" fix): `available = paneHeight − topPad − systemTier − 2·divider − bottomPad`;
  `slots = floor((available + 4) / 44)`. Pinned tier takes `min(pins, slots − 1)` (always leave ≥1 slot for recents or
  the overflow tile). If `pins + recents > slots`, the LAST slot is the `…` tile whose flyout lists the remainder with
  full labels. Pins overflow before recents do (recents are lower priority).
- Scrollbar hidden (`scrollbar-width: none`); `mask-image: linear-gradient(transparent, black 20px, black calc(100% − 20px), transparent)`
  when content overflows (the mask is only applied if the column actually scrolls — an overflow tile should make that
  rare).

### 3.2 `ExpandedPane` (width 180–480; WinUI OpenPaneLength default 320)

Vertical stack, every band full-width, `--mica-base` ground, no right-edge divider (the content card's stroke is the
separation — §3.9):

```
 ┌───────────────────────────────────────────┐
 │ HeaderRow          44                     │
 │ ToolbarRow         36  (search + sort/view)│
 │ ChipRow            40  (wrap → 72)         │
 │ divider 1                                  │
 │ list rows          40 (Cozy)  …            │
 └───────────────────────────────────────────┘
```

### 3.3 `HeaderRow` (44 tall, padding-left 14, gap 4) — the Priority+ toolbar

```
 [☰ 16] Your Library ─────────── [+] [⏷filter] [⇅sort] [▦view] [⋯] [‹]
  32       15/20 600, flex:1 1 auto, min-width:0, ellipsis      36×36 each, glyph 16 (28×28 boxes at gap 4 also acceptable —
           the LAST thing to shrink                             pick 32×32 / glyph 16 for the prototype)
```

Rules (from the WinUI CommandBar / Priority+ pattern):

1. The title is `flex: 1 1 auto; min-width: 0; white-space: nowrap; text-overflow: ellipsis`. **No control's
   visibility may be derived from whether the title truncated.** Visibility is a function of the pane width only.
2. Demotion order into the `⋯` overflow menu as width shrinks: `view-toggle` → `sort` → `filter` → `+`. The
   collapse chevron `‹` **never** hides; `⋯` is **always present** (it also carries the layout-switch submenu, as today).
3. Breakpoints on the PANE width, with **8 px hysteresis** on the way back up (a control re-appears only when the width
   exceeds its threshold + 8): `≥ 320` all visible · `≥ 280` view-toggle demoted · `≥ 240` sort demoted · `≥ 200` filter
   demoted · `< 200` `+` demoted (header = glyph · title · ⋯ · ‹). Expose the thresholds as constants at the top of the JS.
4. Minimum title width before demotion kicks in is irrelevant — demotion is width-driven; the title simply gets whatever
   is left and ellipsises last.

### 3.4 `SearchField` — three explicit states, no in-between

| State | Geometry | Visual |
|---|---|---|
| **icon-only** (pane content width < 200, or narrow + not opened) | 36×36 button, glyph 16 | `--fill-subtle` on hover only; tooltip "Search in Your Library" |
| **expanded** (available width ≥ 200 → always shown, OR user opened it on a narrow pane) | height 32, `min-width: 120px`, `flex: 1 1 auto`; leading magnifier 16 at x=8; placeholder "Search in Your Library" Body 14 `--text-tertiary` | 1px `--divider` bottom hairline ONLY (WinUI TextBox rest), radius 4, transparent fill |
| **focused** | same box | 2px `--accent` bottom stroke, `--layer-fill` plate, caret visible, trailing ✕ clear button 24×24 when text present |

Never render an unfocused field with a caret, and never a field narrower than 120 — below that it is the button.
Escape ladder (already the engine's rule, `LibraryV3SearchRules.OnEscape`): first Escape clears, second closes.

### 3.5 `ToolbarRow` (36 tall)

`[SearchField (flex 1)] [gap 4] [SortButton 32×32] [ViewSegmented | ViewButton]`. When the header already shows sort/view
(≥ 320 in §3.3), the toolbar shows only the search field — the prototype must offer both placements
(`toolbar: search-only | search+controls`) so the team can decide; the recommendation is **controls in the header,
toolbar = search only**, which frees the toolbar from the icon-only compromise entirely.

### 3.6 `ChipRow` (Material 3 chips)

- Chip: height 32, radius 8, 1px `--card-stroke` outline, padding 0 12 (0 8 with an 18 px leading icon), gap 8 between
  chips, label Body 14. Selected: `--accent-subtle` fill + `--accent` outline + leading ✓ 18. 48 px minimum touch target
  achieved by 8 px vertical hit padding.
- Four chips: Playlists · Podcasts · Albums · Artists. At pane ≥ 320 they fit on one row (4×~78 + 3×8 + 2×14 = 362 —
  so the real threshold is ~364; **the prototype must show the wrap at 320 honestly** and let the team see whether
  labels shorten or the row wraps). Preference order: (1) no scrolling, fit; (2) wrap to a second row (row height
  32 + 8 gap → band 72); (3) only if scrolling is ever chosen: 16 px edge fade + hidden scrollbar + `scroll-snap-type: x mandatory`
  + a static trailing 24 px chevron.
- The selected facet's qualifier (By you / By Spotify) fuses INTO its chip as a trailing segment (today's grammar,
  `LibraryV3Metrics.cs:24-28`) — keep; render as a 1px-divided inner segment.

### 3.7 `SortFlyout` (own affordance — the split)

Anchored bottom-left under the sort button. Width 220, padding 4, radius 8, `--acrylic-inapp`, `--shadow-flyout`.
Rows 32 tall, radius 4, padding 0 8 0 10:

```
 SORT BY                    (eyebrow 12/16 600 tertiary, 8 6 8 2 padding)
 ✓ Recents
   Recently added
   Alphabetical
   Creator
   Custom order              (only under Playlists)
 ─────────────
 ● Ascending
 ○ Descending
```

Radio semantics: checkmark on the active sort (16, `--text-primary`); the direction pair is a second radio group,
disabled (40 % opacity) when Custom order is active. Tapping the active sort does NOT flip direction any more — that
implicit behaviour is exactly what the explicit pair replaces.

### 3.8 `ViewFlyout` / `ViewSegmented`

- **Wide header (≥ 320)**: a persistent labelled 2-segment control `[ ☰ List | ▦ Grid ]`, height 28, radius 4, segments
  padding 0 10, Body 13 600, active segment `--accent` fill + on-accent text, inactive `--fill-subtle`. Clicking the
  ACTIVE segment opens the flyout below for its options; clicking the inactive one switches view directly.
- **Narrower**: a single 32×32 view button (glyph reflects current mode) opening the same flyout.
- Flyout (width 240, same chrome as §3.7):

```
 VIEW AS
 ● List      ○ Grid            (radio row, 32)
 ─────────────
 [Grid selected]                    [List selected]
 TILE SIZE                          DENSITY
 ◦───◦───●───◦───◦                  ○ Compact      32 px rows
 56  72  96 128 160                 ● Default      40 px rows
 (slider, 5 ticks, thumb 16,        ○ Comfortable  48 px rows
  track 4, snap to tick)
```

Slider: `<input type=range min=0 max=4 step=1>` styled — track 4 px `--fill-subtle-hover`, filled portion `--accent`,
thumb 16 px circle `--accent` with 2 px `--mica-base` ring; tick labels Caption 12 tertiary. Changing it re-lays the
grid **live** behind the flyout (columns = `floor((paneContentWidth + 8) / (tile + 8))`, min 1).
Density radios change the list row height live (32/40/48) with the art edge 20/32/40.

The SAME flyout component is what search results and playlist pages would reuse — the prototype shows it once but names
it `ViewFlyout` and keeps it free of sidebar-specific state.

### 3.9 `ContentLayer` (the Mica two-layer model)

The pane sits directly on the Mica base (transparent). The content area is a separate card: `--layer-fill`, `border-top`
+ `border-left` 1px `--card-stroke` (no right/bottom), `border-radius: 8px 0 0 0`, starting 0 px after the pane —
**the separation is that elevation step, not a divider line**. The pane therefore has NO right border in any state.
Overlay/compact pane (pane width < 180 or a "peek" over content) uses `--acrylic-inapp` and a 1px `--divider` on its
right edge only while overlaying.

### 3.10 `Tooltip`, `ScrollFadeMask`, `SelectionPill`, `NowPlayingBadge`, `OverflowTile`, `Divider24`

Small shared pieces; sizes given in §3.1. All tooltips: 300 ms show delay, 0 ms hide, Caption 12, max-width 240.

---

## 4. List rows (context, unchanged geometry — so the chrome above reads against real rows)

Cozy 40: art 32 (radius 4; circle for artists), label Body 14, subtitle Caption 12 tertiary, 14 px left inset, 8 right;
hover `--fill-subtle`; selected `--accent-subtle` + the 3×16 pill at x=0. Grid tiles: art = tile size, label 12/16 below,
gap 8. Render 12 fake entries (mixed kinds, 2 folders) so density/size changes are legible.

---

## 5. Interaction rules

1. Rail tile hover → tooltip right at +8 px after 300 ms; leave cancels. Click → selection pill moves (120 ms ease-out).
2. Rail `…` tile → flyout to the right (anchor right edge + 8), width 260, list rows 36 with 24 px art + label; ↑/↓/Enter/Esc.
3. Header demotion: on width change, apply §3.3 thresholds with 8 px hysteresis; a demoted control's verb appears in `⋯`
   (sort/view as submenus, filter as a submenu, `+` as "New playlist / New folder").
4. Search: click icon-only → expanded+focused in one step (120 ms width tween on a narrow pane); blur with empty text →
   back to icon-only when the pane is narrow, stays expanded when wide; Esc ladder as §3.4.
5. Flyouts: light-dismiss, `Esc` closes, focus trapped, first row focused on open; only ONE flyout open at a time.
6. Sort/View changes apply live to the fake list (re-sort labels; switch list/grid; row height / tile size) so the
   controls are visibly connected to a result.
7. Chip row: single-select facets; selecting a facet shows its fused qualifier segment; a fifth "clear" affordance is
   NOT a chip — it lives in `⋯` as today ("Clear filters").
8. Keyboard: Tab order = header buttons → search → chips → list; arrow keys inside radio groups and the slider.

---

## 6. States matrix (every cell reachable from the control strip)

| Surface | State | What must be visible |
|---|---|---|
| Rail | empty (0 pins, 0 recents) | system tier + one divider + the `…`-less empty column; no dangling divider |
| Rail | few (3 pins) | pins tier, divider, recents fill; no overflow tile |
| Rail | many (12 pins, 800 px window) | pins capped at `slots−1`, recents get ≥1 slot or the `…` tile |
| Rail | overflow (30 pins) | `…` tile is the last slot; its flyout lists the rest; no scrollbar; fade mask only if scrolling |
| Rail | now-playing on a pinned tile | 12 px badge bottom-right |
| Header | wide (≥ 320) | glyph · title · + · filter · sort · view-segmented · ⋯ · ‹ ; title never ellipsised at 320 |
| Header | narrow (240–319) | view demoted (then sort at < 280); title still full at 240 for "Your Library" |
| Header | very narrow (180–199) | glyph · title(ellipsis allowed) · ⋯ · ‹ only |
| Search | idle | icon-only (< 200 content) or expanded-unfocused hairline (≥ 200); **never** a caret |
| Search | expanded | field ≥ 120, sort/view untouched (they live in the header) |
| Search | focused + text | accent underline, clear ✕, live filter of the fake rows |
| Sort flyout | open | radio list + separator + Asc/Desc pair; Custom present only under Playlists |
| View flyout | open, Grid | radio + tile-size slider; grid re-lays live |
| View flyout | open, List | radio + density radios; rows re-lay live |
| Chips | 4 chips at 320 / 364 / 480 | wrap at 320, one row at ≥ 364; never clipped |
| Theme | light / dark / auto | every token flips; Mica wash swaps (§7) |

---

## 7. Mica — what an HTML mockup can and cannot show

| Windows 11 visual | In the real app (FluentGpu / DWM) | In the prototype |
|---|---|---|
| **Mica base**: blurred, desaturated, tinted sample of the user's *desktop wallpaper*, static per window, alpha-blended with the theme base | real (window backdrop) | **cannot** — no access to the desktop. Fake with a fixed CSS gradient "wallpaper" behind the window mock (`radial-gradient` in two soft hues) blurred by `filter: blur(60px)`, then the pane ground `--mica-base` at ~80 % opacity over it. Label the control strip "wallpaper: A / B / none" so reviewers see the tint change. |
| **Content layer** (`LayerFillColorDefault`) | translucent card over Mica | **faked faithfully** — semi-opaque `--layer-fill` over the fake Mica; the elevation step reads correctly. |
| **In-app Acrylic** for flyouts / overlay pane (blur of the app's OWN content behind it) | real | **real in CSS** — `backdrop-filter: blur(30px) saturate(125%)` over the page content works in Chromium/WebKit; add the noise texture as a tiny inline data-URI PNG at 2 % opacity. |
| Mica Alt / tabbed variant | n/a here | n/a |
| Window inactive state (Mica falls back to a solid) | real | offer a "window inactive" toggle that swaps `--mica-base` to solid `#F3F3F3` / `#202020` — cheap and shows the fallback the design must survive. |
| Rounded window corners + 1 px system border | DWM | approximate with `border-radius: 8px; box-shadow` on the window mock; irrelevant to the sidebar decisions. |

Every "faked" item must be visually marked in a small legend on the page ("approximation") so nobody signs off on a
blur the OS will render differently.

---

## 8. Acceptance checklist for the prototype

- [ ] Single file, ≤ 200 KB, no external JS; loads in Edge/Chrome; light + dark + auto.
- [ ] Control strip reaches every row of §6.
- [ ] Pixel values match §2–§3 (spot-check with the browser ruler: rail 56, tile 40, pitch 44, pill 3×16, header 44,
      toolbar 36, chip 32/8/8, flyout row 32, slider ticks 56/72/96/128/160, rows 32/40/48).
- [ ] The title never truncates while any demotable control is still visible (§3.3 rule 1) — verified at 320 and 240.
- [ ] Search is never in a half state; exactly three renderings exist in the DOM.
- [ ] Sort and View are two affordances; the view flyout is one reusable component.
- [ ] The rail never grows past the window; overflow goes to the `…` tile.
- [ ] Mica legend present; fake vs real marked.

---

## 9. Brief for the future engine plan (`library-v3-sidebar-implementation.md`, not now)

When the prototype is approved, the engine plan should be written with real code, component trees and wireframes per
CLAUDE.md, covering — at minimum — these seams (file references are today's):

1. **Header Priority+** — replace the width-only `LibraryV3SearchRules.SortIconOnly` coupling with an engine-free
   `LibraryV3HeaderRules.Resolve(paneWidth, previous)` (thresholds + 8-DIP hysteresis → which of {create, filter, sort,
   view} render inline vs in `⋯`), consumed by `LibraryV3Header` (`Modes/LibraryV3/LibraryV3Header.cs:62-119`) whose
   `BuildOverflow` (`:136-164`) gains the demoted verbs. Title: keep `Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim`
   but **investigate the `Basis = 0f` at `:86`** — the "Your.." truncation with free space smells like the text being
   measured at its zero basis and the buttons' `Shrink` defaults winning; the fix is likely `Basis = float.NaN`
   (auto) + `Shrink = 1f` on the title and `Shrink = 0f` on every button, verified with the engine's flex tests.
2. **Search three-state** — `LibraryV3SearchRules.Resolve` (`:39-45`) becomes a three-value enum
   (`IconOnly | Expanded | Focused` is view state; rules decide `IconOnly | Expanded` from `contentWidth ≥ 200`) and
   `LibraryV3Search` (`Modes/LibraryV3/LibraryV3Search.cs`) drops the transparent-lane rest style for the WinUI hairline
   rest / accent-underline focus. The 32-DIP `ClosedWidth` becomes the 36 button; `OpenWidth` gets a 120 floor.
3. **Split the flyout** — `V3SortViewTrigger`/`V3SortViewPanel` (`Modes/LibraryV3/V3SortViewFlyout.cs`) become
   `V3SortFlyout` (radio list + explicit Ascending/Descending pair; `SetV3Sort(key, desc)` already takes the direction)
   and a shared `ViewFlyout` component under `Features/Shared` (or wherever `LibrarySortPanel` lives) reused by the
   library page and search results. **Decision to record:** the sidebar's cell size stops being purely derived
   (`LibraryV3Metrics.Columns` `:84-90`, comment `:81-83`) — the persisted `SidebarKeys.V3GridSize`
   (`SidebarPreferences.cs:95`, currently unread) becomes the tile-size tick, and `Columns` derives from it.
   `SidebarV3View` (4 values) collapses to `List | Grid` + density/size.
4. **Rail capacity + overflow** — `SidebarRowPlanner.BuildRail` already caps at 40 tiles (`Pane/SidebarPaneRail.cs:22-24`);
   the new rule is height-driven: `RailCapacity.Slots(paneHeight)` (pure) decides how many tiles render and emits a
   trailing overflow tile whose flyout is a `SidebarRailFolderFlyout`-style list (`Pane/SidebarRailFolderFlyout.cs`).
   Tile chrome (pill, badge, tooltip delay) lives in `SidebarRailItem` (`Shared/SidebarRailItem.cs`).
5. **Content layer** — the pane/content separation moves from a divider to the content card's top/left
   `StrokeCardDefault` + `CornerRadius(8,0,0,0)`; that is shell work (`Features/Shell`), one PR on its own, and the
   only piece with a real Mica dependency (`Tok` already has the layer/card brushes).
6. **Chips** — `LibraryV3Chips` (`Modes/LibraryV3/LibraryV3Chips.cs`) to 32/8/8 Material metrics with wrap-before-scroll;
   `ChipRailHeight` (`LibraryV3Metrics.cs:28`) becomes measured (40 or 72).
7. Tests: every rule above is an engine-free class with a table test (`LibraryV3HeaderRulesTests`, `RailCapacityTests`,
   extended `LibraryV3SearchRulesTests`); no source-text tests; Debug + Release + `Wavee.Tests`.
