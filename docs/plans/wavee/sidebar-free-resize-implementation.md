# Sidebar free-range resize + rail detents — design & implementation plan

Repo: `C:\wavee\waveemusic` (app), engine: `C:\wavee\fluent-gpu`. All paths below are absolute or repo-relative to those two roots. Every claim carries a `file:line` from the current tree (HEAD `c060e9b7`).

> Note on the skill docs: `.claude/skills/wavee-sidebar/*` still describe the 0.2.9 layout (`Features/Sidebar/Pane/*`, `SidebarPreferences`, `ShellResponsiveLayout`). The 0.3 code lives in `src/apps/Wavee/Shell/Sidebar*.cs` + `Shell.*.cs`; the service is the static `Sidebar` partial (`Shell/Sidebar.Host.cs:58`). This plan cites the 0.3 code.

---

## 0. The owner's ask, restated as requirements

1. The sidebar never changes width or regime on its own because the window got smaller (no window-width auto-collapse, no responsive tier ladder).
2. Above the icon rail, the width is free: whatever the user drags to, no snapping.
3. Close to the rail, the pane enters icon-only mode with three snap detents: Compact (tiny), Default (today's 56 rail), Large (bigger tiles, still no labels).
4. The user can pick the detent several ways and it persists.

---

## 1. Current behaviour map

### 1.1 Two auto-collapse mechanisms + one detent

**(A) The whole-shell narrow band — the real "auto collapse".**
- `Shell.Layout.NarrowFor(width, current, initialized)` — `Shell/Shell.Chrome.cs:227-232`; `NarrowEnterW = 720`, `NarrowLeaveW = 760` (`:28-29`). Hysteretic.
- Published as `Ui.NarrowShell` (`Shell/Shell.cs:1108`) by the effect at `Shell/Shell.UI.cs:235-242` (seeded at `:225-231`).
- Folded into `Ui.SidebarPresentedCompact = narrow || collapsed` (`Shell.cs:1112`; rule `FrameRules.PresentedCompact` `Shell.cs:1870`; effect `Shell.UI.cs:243-244`).
- The column width binds `FrameRules.SidebarPaneWidth(presentedCompact, dragPeek, expandedWidth)` (`Shell.cs:1859-1860`) at `Shell.UI.cs:493-494` → `Layout.CompactRailW = 56` when compact.
- In the band the seam vanishes (`FrameRules.SidebarSeamWidth(narrow) => narrow ? 0 : 16`, `Shell.cs:1867`, bound at `Shell.UI.cs:550`) and the hamburger opens the drawer instead of writing the preference (`ToggleSidebar`, `Shell.UI.cs:667-672`).
- The drawer: `NarrowDrawer` / `DrawerScrim` / `DrawerPane` (`Shell.UI.cs:1318-1454`), width `Layout.DrawerWidth(vp, preferred) = min(max(240, preferred), vp − 32)` (`Shell.Chrome.cs:241-245`; `DrawerMinW 240`, `DrawerViewportInset 32` at `:31-32`), mounted on narrowness alone (`NarrowDrawerMount.ShouldMount`, `:1400-1403`). **Screenshot 1 (≈310 DIP window, expanded-but-cramped sidebar with a tab strip above) is this drawer at `310 − 32 = 278` DIP**, overlaying the page; screenshot 2 is the inline 56-DIP rail the band forces.

**(B) The responsive tier ladder — the width "becoming smaller with the window".**
- `SidebarPaneBounds` (`Shell/Sidebar.cs:70-133`): `NavPaneMinW 180 / NavPaneMaxW 460` (`:73`), `CompactRailW 56` (`:76`), Classic tiers 240/280/320 (`:80`), breakpoints `NavPaneMidEnterW 1400 / NavPaneWideEnterW 1800` (`:84`), `NavPaneHysteresisDip 24` (`:88`), `NavPaneDefaultFor` (`:119-128`, widen at once, shrink after the dip).
- Per-design tiers `SidebarDesignInfo.Tiers` (`Shell/Sidebar.Modes.cs:88-95`): V3 300/340/380, Curated 280/320/360.
- Driven by the shell effect `Shell.UI.cs:248-258` → `Sidebar.SetResponsiveWidth` (`Shell/Sidebar.Host.cs:290-297`), silenced once `WidthUserSet` latches (`:261`, set by `CommitWidthDrag` `:271-278`). `ResetWidth` (`:301-306`) un-latches.
- **Duplicated ladder**: `Shell.Layout` redeclares the whole block — `NavPaneMinW/MaxW` (`Shell.Chrome.cs:173`), tiers/breakpoints/hysteresis (`:180-183`), `NavPaneDefaultFor` (`:210-223`), `CompactRailW` (`:30`) — despite `Sidebar.cs:64-65` saying the shell "READS these; it does not redeclare them". `Shell.UI.cs` uses `Layout.*` (`:154`, `:255`, `:331`); `Sidebar.Host.cs` uses `SidebarPaneBounds` (`:294`). Both go.

**(C) The seam's engine detent.**
- `Splitter.Create(Sidebar.Width, commit, SidebarSeamOptions, collapsed: Sidebar.Collapsed, fade: _sidebarFade, dragging: _sidebarDragging)` — `Shell.UI.cs:555-556`. Options (`:152-158`): `Min 180, Max 460, CompactWidth 56, FadeStart 240, FadeDistance 64, ForcePush 64, ReExpand 190`.
- Engine `SplitterCore.OnMove` (`C:\wavee\fluent-gpu\src\FluentGpu.Controls\Splitter.cs:244-288`): below `FadeStart` the width resists (`SplitterMath.ResistWidth`, 0.28) and the fade signal drops to 0.35; 64 DIP past it, `SetCollapsed(true)`; from collapsed, re-open at raw ≥ 190. Collapse writes `Sidebar.Collapsed` directly; the shell persists it from the effect at `Shell.UI.cs:260-264`.
- 1:1 during the gesture because the splitter suppresses layout transitions (`Splitter.cs:235`, lifted at `:292` *before* `OnCommit` fires at `:299`).

### 1.2 State and persistence
- `Sidebar.Width : Signal<float>` (expanded width, `Sidebar.Host.cs:251`), `Sidebar.Collapsed : Signal<bool>` (`:255`), `WidthUserSet` (plain bool, `:261`), `DragPeek` (`:266`).
- Per-design `IAppSettings` keys: `sidebar.<slug>.width` (default = the design's narrow tier), `.width.userSet`, `.collapsed` — `Platform/Platform.cs:370-375`; slugs `classic|v3|curated` (`Sidebar.Modes.cs:69-74`).
- `SidebarPaneState.Snapshot/Restore/ResetWidth/CommitWidth` (`Sidebar.Modes.cs:112-165`) behind `Sidebar.Boot` (`Sidebar.Host.cs:111-114`) and `SwitchDesign` (`:173-194`).
- **Not** in `sidebar-layout.json`: that document carries pins, the V3 overlay and the Curated document only (`Sidebar.Host.cs:831-848`; skill `architecture.md:93-94` "Scalars … live in IAppSettings"). `Dispatch` (`:495-511`) is the Curated document's undo-ringed mutation path — a width is deliberately not a command.

### 1.3 The rail renderer
- `Rail` (`Shell/Sidebar.UI.Rail.cs:39`): `Box = 40` (`:42`), `ArtEdge = 36` (`:45`), `Pitch = SidebarRailExtents.Pitch = 46` (`Sidebar.cs:862`), `DividerExtent = 9` (`:866`), `TileGap = 6` (`:870`), `HeadExtentOf` (`:878-882`), `ExtentOf` (`:903-916`). Divider 24×1 (`Rail.cs:354-357`). Icon tile glyph 16 (`:291`), corners 6 (`:301`). Art tile = `Controls.Surface(data, Shape.RailTile)` (`:328`) with `Shape.RailTile` = 36 art in a 40 box, no labels (`Platform/Surface.Rules.cs:116-117`); the cover is decoded at `Rail.ArtEdge` in `SidebarCards.RailOf` (`Shell/Sidebar.Cards.cs:223`).
- One virtualized `ItemsView` (`Rail.Frame`, `Rail.cs:61-98`) seeded by `RailExtentSeed` (`Sidebar.UI.cs:1589-1590`) with `estimatedExtent: Rail.Pitch` (`:565`), reseeded by `ReseedRailExtents` (`:1595-1599`).
- Both layers always mounted and cross-faded: expanded layer `Width = _expandedWidth` (`Sidebar.UI.cs:620-627`, "text never reflows through 56 DIP"), compact layer `Width = SidebarPaneBounds.CompactRailW` (`:635-642`). The fold: `compact = !InDrawer && Shell.Ui.SidebarPresentedCompact.Value && !_dragPeek.Value` (`:579`).

### 1.4 Per design
| | Classic | Library V3 | Curated |
|---|---|---|---|
| config | `ClassicMode` `Sidebar.UI.cs:188-201`; `RailFooter` = create "+" at `Rail.Box` | `LibraryV3Mode`; `RailHead` = `BuildRailHead` (`Sidebar.UI.LibraryV3.cs:427-432`, tiles `:438-468`), `RailFooter` = "expand" tile + "+" (`:412-422`) | `CuratedMode` `Sidebar.UI.cs:238-250`; nothing rail-specific |
| collapse affordance | hamburger only (`Shell.UI.cs:667`) | header "<" `IconButton(Icons.ChevronLeft, _s.CollapsePane)` (`:1064-1067`, tooltip `sidebar.v3.collapse` = "Collapse Your Library", `en-US.json:2395`), overflow row (`:1092-1093`), rail "expand" tile (`:418`) → `SetCollapsed` (`:371-372`) | hamburger; rail `SectionTile` click expands (`Rail.cs:275`); `ExpandFolderInPane` (`Sidebar.UI.cs:2198`) |
| width-adaptive content | row text ellipsis | `ComputeNarrow` (<320 drills instead of inline, `:185`; `LibraryV3Metrics.DrillInWidth 320` `Sidebar.Modes.cs:533`), `LibraryV3SearchRules.Resolve` (inline ≥300, icon-only sort <280, `:635-641`), chip rail is a `ScrollView` with edge fade (`:1366-1380`), grid columns from width (`:176-183`) | row text ellipsis, grid `GridCellMax 160` |

### 1.5 The right (queue/lyrics) rail and the content region
- `Ui.RailOpen / Mode / RailWidth(FloatSignal) / RailFits` (`Shell.cs:1058-1093`); `RailMinW 200 / RailMaxW 500 / RailDefaultW 340 / MinContentW 480` (`:1138-1139`); `CanFitRail = sidebarW + railW + minContentW <= viewportW` (`:1181-1183`). The fit effect uses `SidebarPresentedCompact ? 56 : Sidebar.Width` (`Shell.UI.cs:329-333`); a non-fitting rail floats (`FrameRules.RailFloats`, `Shell.cs:1882`).
- The content card is `Grow=1, Shrink=1, MinWidth=0` (`Shell.UI.cs:510, 516`) — **no hard content minimum exists except the 720 narrow band**.

### 1.6 Dead probe
`SidebarPaneInvariant` (`Sidebar.cs:1259-1313`) encodes "56 or [180,460]". Its runtime hook `Diagnostics.SidebarPaneFrame` (`Screens/Diagnostics.cs:381`) is read at `Screens/Diagnostics.Host.cs:279` but **assigned nowhere** (grep across `src/apps`): the invariant runs only in `SidebarPaneInvariantTests` (`Wavee.Tests/SidebarPlannerTests.cs:2922-3008`). This plan rewrites it and wires it.

### 1.7 Engine capabilities relied on (verified)
| need | where |
|---|---|
| pointer capture for the drag | `BoxEl.OnDrag` eager-captures (`Splitter.cs:12-13`; dispatcher `InputDispatcher.cs:1077-1081`, `:1486`) |
| resize cursor | `CursorId.SizeWE` (`Splitter.cs:177`); `Element.Cursor` (`Dsl/Element.cs:223`) |
| 1:1 drag + animated settle | `Motion.SetLayoutTransitionsSuppressed(AppResize)` on down/up (`Splitter.cs:235, 292`); `LayoutTransition` on the column (`Shell.UI.cs:138-140`, `Animate = SidebarPaneAnim` `:495`) |
| spring dynamics | `TransitionDynamics.Spring(response, damping)` (`Foundation/LayoutTransition.cs:64`); `MotionTok.StandardSpring` 0.35/0.85 (`Animation/MotionTok.cs:173`) |
| focusable node + arrow keys | `Element.Focusable` (`:315`), `TabStop` (`:319`), `OnKeyDown` (`:171`), `AllowFocusOnInteraction` (`:285`); Slider proves arrows reach a focused node's handler (`Controls/Slider.cs:158-168`) |
| double-click | `OnPointerPressed` carries `ClickCount` (`Element.cs:188`; `Events.cs:117`; `DoubleClickMs 500` `InputDispatcher.cs:154`) |
| context menu on the seam | `WithContextMenu(this BoxEl, svc, factory)` (`Controls/ContextMenu.cs:273`) |
| injecting the above into the splitter without an engine edit | `Splitter.Create(..., parts: TemplateParts)` (`Splitter.cs:91`); `Parts.Apply(PartRoot, root)` then re-asserts ONLY `Width/Height/Grow/AlignSelf/Cursor/OnRealized(chained)/OnPointerDown/OnDrag/OnClick/OnDragCanceled/Children` (`:194-205`) — `Focusable`, `TabStop`, `OnKeyDown`, `OnPointerPressed`, `Role`, the context-menu hook all survive |
| live region | `Announcer.Say/SayThrottled` (`Input/Announcer.cs:31-42`) → `Win32Uia.Announce` (`FluentGpu.Windows/Uia/Win32Uia.cs:55`) |
| automation role | `AutomationRole` has no `Separator`/`Thumb` (`Foundation/AutomationRole.cs:8-31`); UIA is a window-level live-region provider only (`Win32Uia.cs:10`), so a per-node control type is not expressible today → use `AutomationRole.Slider` (the keyboard contract matches). **No engine work package is required.** |

---

## 2. Proposed model

### 2.1 One continuous seam, two regimes, derived presentation

```
  raw seam W (pointer, 1:1) ──► SidebarResizeRules ──► (regime, detent, presentedWidth, fade)
                                                   ──► on release: Resolve → settle (snap or free)
  viewport width ───────────► SidebarResizeRules.Present ──► yield cap (degenerate window only)
```

Three stored facts, each with one owner:
- `Width` — the user's **expanded** width (per design, persisted). Written only by a commit while in the expanded regime, by the keyboard nudge, by "Reset width", or by Settings. Never by the window.
- `Regime` ∈ {Expanded, Rail} — per design, persisted in the existing `sidebar.<slug>.collapsed` bool (same meaning, no migration).
- `RailDetent` ∈ {Compact, Default, Large} — **global**, persisted (`sidebar.rail.detent`, int, default 1). A user who wants a big rail wants it in every design.

Derived (never persisted): `PresentedWidth` (what the column lays out at), `RailMetrics` (the tile geometry for the detent), `Seam` (the splitter's raw cell, transient).

### 2.2 Detents — derived from tokens, not invented

Every rail number comes from one formula over the thumbnail ladder `Design.Size.Thumb32/40/48/56/64` (`Platform/Design.cs:77`, "the ONLY sizes an in-content cover/avatar may take"):

| | tile (`Thumb*`) | art = tile − 2·ring(2) | glyph | corner (`Cover.Radius`) | strip = tile + 2·`PaneEdge`(8) | pitch = tile + `TileGap`(6) | divider = tile − 2·8 |
|---|---|---|---|---|---|---|---|
| Compact | **32** = `Thumb32` = `Design.Size.ControlH` (the WinUI 32×32 hit floor, `Shell.cs:1221-1223`) | 28 (= `Cover.S28`, `Sidebar.UI.Rows.cs:465`) | 16 (`Cover.GlyphSize(28)`, `:472`) | 6 | **48** | 38 | 16 |
| Default | **40** = `Thumb40` = today's `Rail.Box` | 36 (today's `ArtEdge`) | 16 (today) | 6 (today, `Rail.cs:301`) | **56** = `Design.Size.NavCompactW` (`Design.cs:65`) = today's `CompactRailW` | 46 (today's `Pitch`) | 24 (today's divider) |
| Large | **64** = `Thumb64` (= `Cover.S64` "grid / spotlight") | 60 | 20 (`ControlSize.Large.IconSize`, `Controls/ControlSize.cs:43`) | 8 | **80** | 70 | 48 |

Justification: the ring inset (2) is the existing `ArtEdge = Box − 4` relation (`Rail.cs:44-45`); the 8 side inset is `SidebarRowGeometry.PaneEdge` (`Sidebar.cs:418`) — `(56 − 40)/2 = 8` already; the pitch gap is `SidebarRailExtents.TileGap` (`Sidebar.cs:870`). The Default row reproduces today's rail byte-for-byte, so the migration is invisible. **Owner decision D2:** "double this size" would be a 112 strip = a 96 tile, which is on no ladder in the app; 80 (tile area 2.56× the default) is the largest token-derived rung. Recommend 80.

### 2.3 Thresholds and hysteresis (all derived from existing constants)

```
ExpandedMinW   = SidebarPaneBounds.NavPaneMinW            = 180   (issue #84 floor)
ExpandedMaxW   = SidebarPaneBounds.NavPaneMaxW            = 460
RegimePush     = today's SidebarSeamOptions.ForcePush      = 64    (Shell.UI.cs:157)
RailEnterW     = ExpandedMinW − RegimePush                 = 116   drag LEFT past this ⇒ Rail
ExpandedEnterW = RailEnterW + NavPaneHysteresisDip(24)     = 140   drag RIGHT past this ⇒ Expanded
RailFloorW     = strip(Compact)                            = 48
RailCeilW      = strip(Large)                              = 80
detent midpoints: 52 (Compact|Default), 68 (Default|Large), ± DetentHysteresis 4 (= RootlistSlotResolver.DepthHysteresis, Sidebar.cs:2942)
MinFade        = 0.35 (SplitterMath default, Splitter.cs:69)
```

Live tracking during a drag (`Track`):
- raw ≥ 180 → Expanded, presented = raw (free, no snap), fade 1.
- 116 ≤ raw < 180 while Expanded → still Expanded, presented **holds at 180**, fade = `SplitterMath.Fade(180 − raw, 64, 0.35)` (the "about to switch" cue, reusing the engine's own curve).
- raw < 116 → Rail. Presented = clamp(raw, 48, 80) — the strip follows the pointer; the **tile metrics** step at the midpoints with 4-DIP hysteresis (the "click" into a detent).
- 80 < raw < 140 while Rail → still Rail, presented holds at 80, fade = `Fade(raw − 80, 60, 0.35)` (symmetric cue).
- raw ≥ 140 while Rail → Expanded at 180, then free.

On release (`Resolve`):
- Expanded → width = clamp(raw, 180, 460), written to `Width` and persisted.
- Rail → detent = nearest by midpoints; with a flick (|velocity| ≥ `FlickDipPerSec`, direction toward a neighbour) → that neighbour. Target width = that detent's strip. `Width` (the expanded memory) is **untouched**.
- The column's `Animate = SidebarPaneAnim` carries the settle: the splitter lifts the AppResize suppression (`Splitter.cs:292`) before `OnCommit` (`:299`), so the write to the detent width animates. Recommend switching `SidebarPaneAnim`'s dynamics from the 300 ms tween (`Shell.UI.cs:134-140`) to `TransitionDynamics.Spring(0.30f, 0.85f)` so a second drag mid-settle retargets without a jump (`LayoutTransition.cs:49-50`) — **D5**. Reduced motion needs nothing: the slab reads it as a value.

### 2.4 Window-width behaviour (the degenerate case) — **D1**

Delete: the tier ladder (both copies), `WidthUserSet`, `SetResponsiveWidth`, the 720/760 band as a sidebar driver, `FrameRules.PresentedCompact`, `SidebarPaneWidth`, `Ui.SidebarPresentedCompact`.

Add one pure rule, `Present(state, viewportW, contentFloorW)`:
- Expanded: `presented = clamp(viewportW − contentFloorW, ExpandedMinW, Width)` — the pane **yields continuously** to keep `contentFloorW` for the page, never below 180, and the preference `Width` is not written (it springs back when the window grows). No regime flip, ever, from this rule.
- Rail: `presented = strip(detent)`; a rail is never yielded.
- `contentFloorW = Shell.MinContentW = 480` (`Shell.cs:1139`, "a minimum usable content region", today only used by the queue-rail fit). With it: 1440 → no yield up to 460; 756 → a 300 pane yields to 276; 700 → 220; ≤ 660 → 180.

Below `ExpandedMinW + contentFloorW` (= 660) the window physically cannot hold 180 + 480. Options:
- (a) do nothing: content shrinks under 480 (the player bar's own floor is 300, `Shell.cs:1227`); at a 310 window a 180 pane leaves 130 DIP of page.
- (b) overlay: the inline column shows the user's rail detent and the full pane is reachable as the existing drawer (today's narrow band, with a **derived** threshold `LastResortEnterW = 660 / LeaveW = 700` instead of 720/760, hysteresis 40 = `ChromePromotionHysteresisW`, `Shell.Chrome.cs:48`).
- (c) same as (b) but the inline column keeps the pane expanded at 180 and the page becomes the overlay — rejected (the page is the product).

**Recommendation: (b)**, because it is the only option that is never reached on a desktop window and still gives a 310-DIP window a usable page; it is explicitly the *last resort* — it is unreachable above 660 by construction, versus today's 720 band and ladder which fire on every ordinary resize. If the owner prefers (a), WP-3 deletes `NarrowDrawer*` and `Ui.NarrowShell/DrawerOpen` outright; the pure rules are identical.

### 2.5 Other ways to pick the regime/detent (all grounded in existing surfaces)
| surface | today | becomes |
|---|---|---|
| hamburger `ToggleSidebar` (`Shell.UI.cs:667-672`) | flips `Collapsed` | `Sidebar.ToggleRegime()`; in the last-resort band still toggles the drawer |
| V3 header "<" + overflow row + rail "expand" tile (`LibraryV3.cs:1066, 1093, 418`); `SectionTile` click (`Rail.cs:275`); `ExpandFolderInPane` (`Sidebar.UI.cs:2198`) | `SetCollapsed(bool)` | `Sidebar.SetRegime(SidebarRegime)` |
| double-click the seam | nothing | `OnPointerPressed` with `ClickCount == 2` → `ToggleRegime()` (injected via `TemplateParts[Splitter.PartRoot]`) |
| right-click the seam / the rail background | the rail's context menu is `LayoutMenu.Model` (`Sidebar.UI.cs:658-671`) | `LayoutMenu.Rows` (`Sidebar.UI.Menus.cs:59-77`) gains a **"Collapsed rail size ▸ ◉ Compact · ◉ Default · ◉ Large"** submenu; "Reset width" is repurposed (below). The seam gets `.WithContextMenu(svc, LayoutMenu.Model)` |
| keyboard | the seam is not focusable | seam `Focusable = true, TabStop = true, AllowFocusOnInteraction = false, Role = AutomationRole.Slider`; ←/→ nudge 8 (`FrameRules.RailGapW` = `Spacing.S`, `Shell.cs:1852-1853`), Shift+←/→ 40; in Rail ←/→ step detents, → past Large expands, ← below 180 enters Rail (Large); Home/End = 180/460; Enter/Space = toggle regime; every step → `Announcer.Say` |
| Settings → Appearance → Sidebar expander (`Screens/Settings.UI.Appearance.cs:488-496`) | design cards + "Customize sidebar" item | + `Item("Collapsed rail size", sub, ComboBox.Create([Compact, Default, Large], s_railDetent, 160f, onChange))` — the zoom-combo mirror pattern (`:209-221`) |
| "Reset width" (`Menus.cs:75`, dead unless latched) | un-latch → ladder | → `Width = SidebarDesignInfo.DefaultWidth(design)`, Regime Expanded; enabled when `Width ≠ default` — **D8** |

### 2.6 Persistence — **D7**

Stay in `IAppSettings` (the established home for geometry; `Dispatch` would push every drag into the 50-step undo ring and bump `LayoutVersion`, re-planning the pane for a width change). Schema:

```
sidebar.<slug>.width       float   KEEP  — the expanded width; default = SidebarDesignInfo.DefaultWidth(design)
sidebar.<slug>.collapsed   bool    KEEP  — true ⇔ Regime == Rail (unchanged meaning, no migration)
sidebar.<slug>.width.userSet       DELETE the factory + every reader (Platform.cs:371-374); the on-disk bytes are inert
sidebar.rail.detent        int     NEW   — 0 Compact · 1 Default · 2 Large; unknown ⇒ Default (append-only ints)
sidebar.width / .userSet / .collapsed (Legacy)   KEEP — Setup.cs:683 still reads them for the fresh-install probe
```
`sidebar-layout.json`: no change (no version bump, no reducer action). If the owner insists on the document: it would be a new envelope member `"geometry": {"railDetent": 1, "panes": {"classic": {"width": 280, "rail": false}, …}}` written by `BuildSnapshot` outside `Dispatch` — I recommend against it.

Default width without the ladder (**D4**): `SidebarDesignInfo.DefaultWidth` = the former MID tier — Classic 280, V3 340, Curated 320. (Narrow alternative 240/300/280.)

### 2.7 What each rail detent shows
Same renderer, same plan, one `SidebarRailMetrics` record read by `Rail.*` instead of the `Box/ArtEdge/Pitch` consts:
- **Compact 48**: 32 tiles, 28 art, 16 glyph, divider 16, pitch 38. Everything the Default rail shows, denser (≈21% more tiles per height). Hit target stays ≥ 32.
- **Default 56**: today, unchanged.
- **Large 80**: 64 tiles, 60 art (artist covers circular at 30 r), 20 glyph, divider 48, pitch 70. Still label-less (tooltips are the labels, `Shape.RailTile` `Labels: false`). The now-playing ring/equalizer come from the surface at the shape, unchanged.
The V3 rail head/footer tiles (`LibraryV3.cs:412-468`) and Classic's create "+" (`Sidebar.UI.cs:199-200`) read the same metrics.

---

## 3. Component tree + wireframes

### 3.1 Shell column (after)
```
FrameRoot.ContentRegion (Shell.UI.cs:481)
└─ row [MorphId shell.content-row]
   ├─ BoxEl sidebar column   Width = Prop.Of(() => DragPeek ? Sidebar.Width : Sidebar.PresentedWidth)   Animate = SidebarPaneAnim (spring)
   │  └─ BoxEl firewall (IsolateLayout, Opacity = _sidebarFade)
   │     └─ Sidebar.Pane() → PaneHost → {Classic|V3|Curated}Mode → PaneView
   │        ├─ "expanded-layer"  Width = _expandedWidth (= Sidebar.Width)   Opacity = compact ? 0 : 1
   │        ├─ "compact-layer"   Width = Prop.Of(() => Sidebar.PresentedWidth)  Opacity = compact ? 1 : 0
   │        │  └─ RailHost → Rail.Frame(owner)  (reads Sidebar.RailDetent → SidebarRailMetrics)
   │        └─ "drag-peek" watcher
   ├─ content card (Grow 1, Shrink 1, MinWidth 0) …
   └─ queue rail reservation …
seam strip  X = Prop.Of(() => Sidebar.PresentedWidth)   Splitter.Create(Sidebar.Seam, Sidebar.CommitSeam, SeamOptions, dragging: _sidebarDragging, parts: SeamParts)
```

### 3.2 Expanded at three widths (Library V3 chrome, content adapts by existing width rules)
```
W = 460                                  W = 300                          W = 180
┌──────────────────────────────────┐   ┌──────────────────────┐        ┌───────────┐
│ ⌂ Home                           │   │ ⌂ Home               │        │ ⌂ Home    │
│ Liked · Albums · Artists · Pod…  │   │ Liked · Albums · Ar▸ │        │ Liked · ▸ │  ← destination rail scrolls (DestinationRailFade)
│ ▤ Your Library        + … <      │   │ ▤ Your Library + … < │        │ ▤ Yo…+ …< │  ← title ellipsizes
│ [🔍 Search in Your Library][Rec▾]│   │ [🔍 Search…    ][≡]  │        │ [🔍]   [≡]│  ← Resolve(): inline ≥300 / icon-only <280
│ (Playlists)(Podcasts)(Albums)(Ar)│   │ (Playlists)(Podca▸   │        │ (Playl▸   │  ← chip rail scrolls, edge fade
│ ▣ That Summer   Album · SHAUN    │   │ ▣ That Summer  Alb…  │        │ ▣ That S… │
│ ▣ Daily Mix 1   Playlist · Spot… │   │ ▣ Daily Mix 1  Pla…  │        │ ▣ Daily … │
│ ▸ Folder A              12       │   │ ▸ Folder A       12  │        │ ▸ Folde…  │  ← <320: folders drill instead of inline
└──────────────────────────────────┘   └──────────────────────┘        └───────────┘
      free: any W in [180, 460], no snapping
```

### 3.3 The three rail detents
```
Compact 48            Default 56 (today)       Large 80
┌──────┐              ┌────────┐               ┌────────────┐
│  ≡   │ 32 tiles     │   ≡    │ 40 tiles      │     ≡      │ 64 tiles, 20 glyph
│  ♥   │              │   ♥    │               │     ♥      │
│  ▣   │ 28 art       │   ▣    │ 36 art        │   ▣▣▣▣     │ 60 art (circular artists r=30)
│ ──   │ 16 rule      │  ───   │ 24 rule       │  ──────    │ 48 rule
│ (▣)  │ ring/EQ      │  (▣)   │               │  (▣▣▣▣)    │ now-playing ring + EQ at the shape
│  ▣   │ pitch 38     │   ▣    │ pitch 46      │   ▣▣▣▣     │ pitch 70
│  +   │              │   +    │               │     +      │
└──────┘              └────────┘               └────────────┘
```

### 3.4 The drag, with thresholds and detents
```
 raw seam W →  48      52      56      68      80            116        140        180                                460
               │Compact│Default│Default│ Large │ Large holds │   Rail    │ Expanded │        free expanded (W = raw)      │
 presented     48 ◄──── follows pointer ─────► 80 ──────────── 80 ──────┤          ├── 180 holds (fade↓) ──┤ W ─────────► 460
 regime        ◄──────────────── RAIL ───────────────────────────────────┤◄ hyst 24 ►├────────── EXPANDED ─────────────────►
 going LEFT  : expanded holds at 180 with fade 1→0.35 over [180→116] ──► at 116 pops to Rail/Large(80) ──► 68 Default ──► 52 Compact ──► floor 48
 going RIGHT : Compact ──► 52 Default ──► 68 Large ──► holds 80 with fade over (80→140] ──► at 140 pops to Expanded(180) ──► free
 release     : Rail ⇒ snap to nearest detent (flick ⇒ neighbour), spring; Expanded ⇒ stays exactly where released
 detent edges carry ±4 DIP hysteresis so a pointer resting on 52 does not flicker
```

---

## 4. Real code

### 4.1 New CORE file `src/apps/Wavee/Shell/Sidebar.Resize.cs` (engine-free; `public` because `Wavee.Tests` is a `ProjectReference` with no `InternalsVisibleTo`, `Wavee.Tests.csproj:42`)

```csharp
namespace Wavee;

/// <summary>PERSISTED ints (Platform.Keys.SidebarRailDetent) — append only.</summary>
public enum SidebarRailDetent : byte { Compact = 0, Default = 1, Large = 2 }

/// <summary>Persisted as the existing per-design `sidebar.<slug>.collapsed` bool: Rail ⇔ true.</summary>
public enum SidebarRegime : byte { Expanded = 0, Rail = 1 }

/// <summary>The rail's whole geometry for one detent, derived from the thumb ladder — never a per-detent literal table.</summary>
public readonly record struct SidebarRailMetrics(
    float Tile, float Art, float Glyph, float Corner, float StripW, float Pitch, float DividerW)
{
    /// <summary>The 2-DIP accent ring's inset on each side (today's Box 40 → ArtEdge 36).</summary>
    public const float RingInset = 2f;

    public static float TileOf(SidebarRailDetent d) => d switch
    {
        SidebarRailDetent.Compact => Design.Size.Thumb32,   // = ControlH, the WinUI 32×32 hit floor
        SidebarRailDetent.Large => Design.Size.Thumb64,
        _ => Design.Size.Thumb40,                           // today's Rail.Box
    };

    public static SidebarRailMetrics For(SidebarRailDetent d) => Of(TileOf(d));

    public static SidebarRailMetrics Of(float tile) => new(
        Tile: tile,
        Art: tile - 2f * RingInset,
        Glyph: tile >= Design.Size.Thumb64 ? 20f : 16f,       // ControlSize.Large.IconSize : the stock 16
        Corner: Cover.Radius(tile, circular: false),           // 6 ≤ 40, else 8
        StripW: tile + 2f * SidebarRowGeometry.PaneEdge,       // (56 − 40)/2 = 8 today
        Pitch: tile + SidebarRailExtents.TileGap,
        DividerW: tile - 2f * SidebarRowGeometry.PaneEdge);

    public static SidebarRailDetent Coerce(int stored) => (uint)stored <= 2 ? (SidebarRailDetent)stored : SidebarRailDetent.Default;
}

/// <summary>THE pure resize rules: live tracking during a drag, the settle on release, the window yield, the
/// keyboard/toggle verbs. No engine type, no signal, no clock — Wavee.Tests drives every branch.</summary>
public static class SidebarResizeRules
{
    public const float ExpandedMinW = SidebarPaneBounds.NavPaneMinW;             // 180 (#84)
    public const float ExpandedMaxW = SidebarPaneBounds.NavPaneMaxW;             // 460
    /// <summary>Today's seam ForcePush: how far past the expanded floor the pointer travels before the regime flips.</summary>
    public const float RegimePush = 64f;
    public const float RegimeHysteresis = SidebarPaneBounds.NavPaneHysteresisDip; // 24
    /// <summary>RootlistSlotResolver.DepthHysteresis — the same anti-flicker band, around each detent edge.</summary>
    public const float DetentHysteresis = 4f;
    public const float MinFade = 0.35f;                                          // SplitterMath's default floor
    /// <summary>A release faster than this toward a neighbouring detent picks that neighbour, not the nearest. Tuning value (no token exists).</summary>
    public const float FlickDipPerSec = 800f;
    public const float NudgeW = 8f;        // Spacing.S — the shell's RailGapW
    public const float NudgeLargeW = 40f;  // ChromePromotionHysteresisW

    public static float RailFloorW => SidebarRailMetrics.For(SidebarRailDetent.Compact).StripW;   // 48
    public static float RailCeilW  => SidebarRailMetrics.For(SidebarRailDetent.Large).StripW;     // 80
    public static float RailEnterW => ExpandedMinW - RegimePush;                                   // 116
    public static float ExpandedEnterW => RailEnterW + RegimeHysteresis;                           // 140

    /// <summary>The user's stored facts.</summary>
    public readonly record struct State(SidebarRegime Regime, SidebarRailDetent Detent, float ExpandedWidth);
    /// <summary>What the frame presents mid-gesture.</summary>
    public readonly record struct Live(SidebarRegime Regime, SidebarRailDetent Detent, float PresentedWidth, float Fade);
    /// <summary>The release verdict. <see cref="TargetWidth"/> is what the column animates to.</summary>
    public readonly record struct Settle(SidebarRegime Regime, SidebarRailDetent Detent, float ExpandedWidth, float TargetWidth);

    public static float StripOf(SidebarRailDetent d) => SidebarRailMetrics.For(d).StripW;

    /// <summary>Live tracking. <paramref name="raw"/> is the seam cell the splitter writes 1:1 (clamped by it to [RailFloorW, ExpandedMaxW]).</summary>
    public static Live Track(float raw, in State prev)
    {
        if (!float.IsFinite(raw)) return new(prev.Regime, prev.Detent, prev.Regime == SidebarRegime.Rail ? StripOf(prev.Detent) : prev.ExpandedWidth, 1f);
        var regime = prev.Regime switch
        {
            SidebarRegime.Expanded => raw < RailEnterW ? SidebarRegime.Rail : SidebarRegime.Expanded,
            _ => raw >= ExpandedEnterW ? SidebarRegime.Expanded : SidebarRegime.Rail,
        };
        if (regime == SidebarRegime.Expanded)
        {
            float presented = Math.Clamp(raw, ExpandedMinW, ExpandedMaxW);
            float fade = raw >= ExpandedMinW ? 1f : SplitterFade(ExpandedMinW - raw, RegimePush);
            return new(regime, prev.Detent, presented, fade);
        }
        float strip = Math.Clamp(raw, RailFloorW, RailCeilW);
        var detent = NearestDetent(strip, prev.Regime == SidebarRegime.Rail ? prev.Detent : SidebarRailDetent.Large);
        float railFade = raw <= RailCeilW ? 1f : SplitterFade(raw - RailCeilW, ExpandedEnterW - RailCeilW);
        return new(regime, detent, strip, railFade);
    }

    /// <summary>Nearest detent by midpoint, holding <paramref name="prev"/> inside ±DetentHysteresis of an edge.</summary>
    public static SidebarRailDetent NearestDetent(float w, SidebarRailDetent prev)
    {
        float lo = Mid(SidebarRailDetent.Compact, SidebarRailDetent.Default);   // 52
        float hi = Mid(SidebarRailDetent.Default, SidebarRailDetent.Large);     // 68
        var nominal = w < lo ? SidebarRailDetent.Compact : w < hi ? SidebarRailDetent.Default : SidebarRailDetent.Large;
        if (nominal == prev) return prev;
        float edge = prev == SidebarRailDetent.Compact || nominal == SidebarRailDetent.Compact ? lo : hi;
        return MathF.Abs(w - edge) < DetentHysteresis ? prev : nominal;
    }

    /// <summary>The release. Expanded commits the raw width (clamped); Rail snaps to a detent and leaves the expanded memory alone.</summary>
    public static Settle Resolve(float raw, in State prev, float velocityDipPerSec = 0f)
    {
        var live = Track(raw, in prev);
        if (live.Regime == SidebarRegime.Expanded)
            return new(live.Regime, prev.Detent, live.PresentedWidth, live.PresentedWidth);
        var detent = live.Detent;
        if (MathF.Abs(velocityDipPerSec) >= FlickDipPerSec)
        {
            int dir = velocityDipPerSec < 0f ? -1 : 1;
            detent = (SidebarRailDetent)Math.Clamp((int)detent + dir, 0, 2);
        }
        return new(SidebarRegime.Rail, detent, prev.ExpandedWidth, StripOf(detent));
    }

    /// <summary>The column width at rest: a rail is never yielded; an expanded pane yields to keep <paramref name="contentFloorW"/>
    /// for the page, never below the expanded floor, and never by writing the preference.</summary>
    public static float Present(in State s, float viewportW, float contentFloorW)
    {
        if (s.Regime == SidebarRegime.Rail) return StripOf(s.Detent);
        float preferred = Math.Clamp(s.ExpandedWidth, ExpandedMinW, ExpandedMaxW);
        if (viewportW <= 0f || !float.IsFinite(viewportW)) return preferred;
        float cap = MathF.Max(ExpandedMinW, viewportW - contentFloorW);
        return MathF.Min(preferred, cap);
    }

    /// <summary>The LAST-RESORT band (D1 option b): the window cannot hold the expanded floor AND the content floor.
    /// Enter below (floor + floor); leave at +ChromePromotionHysteresisW. A zero width never moves it.</summary>
    public static bool LastResort(float viewportW, bool current, float contentFloorW)
    {
        if (viewportW <= 0f) return current;
        float enter = ExpandedMinW + contentFloorW;          // 660 with MinContentW 480
        return current ? viewportW < enter + NudgeLargeW : viewportW < enter;
    }

    /// <summary>Keyboard: ←/→ one step. Expanded nudges the width; at the floor a left step enters the rail at Large;
    /// Rail steps detents; a right step past Large expands at the floor.</summary>
    public static Settle Step(in State s, int direction, bool large)
    {
        if (direction == 0) return new(s.Regime, s.Detent, s.ExpandedWidth, s.Regime == SidebarRegime.Rail ? StripOf(s.Detent) : s.ExpandedWidth);
        if (s.Regime == SidebarRegime.Expanded)
        {
            float next = s.ExpandedWidth + direction * (large ? NudgeLargeW : NudgeW);
            if (next < ExpandedMinW && s.ExpandedWidth <= ExpandedMinW)
                return new(SidebarRegime.Rail, SidebarRailDetent.Large, s.ExpandedWidth, RailCeilW);
            float w = Math.Clamp(next, ExpandedMinW, ExpandedMaxW);
            return new(SidebarRegime.Expanded, s.Detent, w, w);
        }
        int i = (int)s.Detent + direction;
        if (i > 2) return new(SidebarRegime.Expanded, s.Detent, MathF.Max(s.ExpandedWidth, ExpandedMinW), MathF.Max(s.ExpandedWidth, ExpandedMinW));
        var d = (SidebarRailDetent)Math.Max(i, 0);
        return new(SidebarRegime.Rail, d, s.ExpandedWidth, StripOf(d));
    }

    /// <summary>Hamburger / double-click / "<": flip the regime, keeping both memories.</summary>
    public static Settle Toggle(in State s) => s.Regime == SidebarRegime.Rail
        ? new(SidebarRegime.Expanded, s.Detent, s.ExpandedWidth, Math.Clamp(s.ExpandedWidth, ExpandedMinW, ExpandedMaxW))
        : new(SidebarRegime.Rail, s.Detent, s.ExpandedWidth, StripOf(s.Detent));

    static float Mid(SidebarRailDetent a, SidebarRailDetent b) => 0.5f * (StripOf(a) + StripOf(b));

    /// <summary>SplitterMath.Fade restated (CORE must not reference FluentGpu.Controls): 1 at 0, MinFade at `over`.</summary>
    static float SplitterFade(float into, float over)
    {
        if (over <= 0f) return 1f;
        float t = Math.Clamp(into / over, 0f, 1f);
        return 1f - t * (1f - MinFade);
    }
}
```

### 4.2 `SidebarPaneBounds` trimmed (`Shell/Sidebar.cs:70-133`)
Keep `NavPaneMinW/MaxW`, `NavPaneHysteresisDip`, `Clamp`. **Delete** `CompactRailW`, the tier triple, `NavPaneMidEnterW/WideEnterW`, `ClassicTiers`, `NominalNavPaneDefaultFor`, `InitialNavPaneDefaultForViewport`, `NavPaneDefaultFor` (all overloads). Delete the duplicate block in `Shell.Chrome.cs:169-225` and `CompactRailW` at `:30`.

### 4.3 `SidebarRailExtents` parametrised (`Shell/Sidebar.cs:857-917`)
```csharp
public static class SidebarRailExtents
{
    public const float DividerExtent = 9f;
    public const float TileGap = 6f;
    public const float TopPad = …, BottomPad = …;   // unchanged
    public static float Pitch(in SidebarRailMetrics m) => m.Pitch;
    public static float HeadExtentOf(int tiles, in SidebarRailMetrics m)
        => TopPad + Math.Max(tiles, 0) * m.Tile + (tiles > 0 ? (tiles - 1) * TileGap : 0f) + DividerExtent;
    public static float ExtentOf(IReadOnlyList<SidebarRow> rows, int index, bool hasHead, bool hasFooter, int headTiles, in SidebarRailMetrics m) { … m.Pitch instead of Pitch … }
}
```
(`Pitch 46` as a const and `Tile = Pitch − TileGap` go; `Rail.Pitch`/`Rail.Box`/`Rail.ArtEdge` consts in `Sidebar.UI.Rail.cs:42-53` go.)

### 4.4 Invariant rewrite (`Shell/Sidebar.cs:1229-1313`)
```csharp
public readonly record struct SidebarPaneFrameSnapshot(
    SidebarDesign Design, SidebarRegime Regime, SidebarRailDetent Detent, bool LastResort,
    float PreferredExpandedWidth, float PresentedWidth, float RenderedPaneWidth,
    float ExpandedOpacity, float RailOpacity, bool ExpandedHitTestVisible, bool RailHitTestVisible);

[Flags] public enum SidebarPaneInvariantFault : ushort
{ None = 0, NonFiniteValue = 1, PreferredWidthOutOfRange = 2, RailWidthMismatch = 4, ExpandedWidthOutOfRange = 8,
  PresentedExceedsPreferred = 16, ExpandedWidthMismatch = 32, LayerOpacityMismatch = 64, HitTestOwnerMismatch = 128 }

public static class SidebarPaneInvariant
{
    public const float Tolerance = 0.5f;
    public static SidebarPaneInvariantFault Inspect(in SidebarPaneFrameSnapshot s)
    {
        … non-finite guard …
        bool rail = s.Regime == SidebarRegime.Rail || s.LastResort;
        if (!InExpandedRange(s.PreferredExpandedWidth)) f |= PreferredWidthOutOfRange;
        if (rail)
        {
            if (!Near(s.RenderedPaneWidth, SidebarResizeRules.StripOf(s.Detent))) f |= RailWidthMismatch;
            … opacity 0/1 + hit-test owner as today …
        }
        else
        {
            if (!InExpandedRange(s.PresentedWidth)) f |= ExpandedWidthOutOfRange;
            if (s.PresentedWidth > s.PreferredExpandedWidth + Tolerance) f |= PresentedExceedsPreferred;
            if (!Near(s.RenderedPaneWidth, s.PresentedWidth)) f |= ExpandedWidthMismatch;
            … opacity 1/0 + hit-test owner as today …
        }
        return f;
    }
}
```
WP-3 finally assigns `Diagnostics.SidebarPaneFrame = () => new SidebarPaneFrameSnapshot(Sidebar.Design.Peek(), Sidebar.Regime.Peek(), Sidebar.RailDetent.Peek(), Ui.LastResort.Peek(), Sidebar.Width.Peek(), Sidebar.PresentedWidth.Peek(), <column rect width>, …)` in `FrameRoot`'s mount effect (`Shell.UI.cs:297`), reading the column's node via `OnRealized`.

### 4.5 Service (`Shell/Sidebar.Host.cs:245-306` replaced; `Boot` `:107-114`; `SwitchDesign` `:178-189`)
```csharp
// ── pane state (ACTIVE design) + the global rail detent ──────────────────────────────────────────────────────────
public static readonly Signal<float> Width = new(SidebarDesignInfo.DefaultWidth(SidebarDesign.Classic));   // the EXPANDED width
public static readonly Signal<SidebarRegime> Regime = new(SidebarRegime.Expanded);
public static readonly Signal<SidebarRailDetent> RailDetent = new(SidebarRailDetent.Default);              // GLOBAL
/// <summary>The splitter's raw cell: 1:1 with the pointer during a drag, equal to PresentedWidth at rest.</summary>
public static readonly Signal<float> Seam = new(SidebarDesignInfo.DefaultWidth(SidebarDesign.Classic));
/// <summary>What the column lays out at — written ONLY by the shell's presentation effect (SidebarResizeRules.Present / Track).</summary>
public static readonly Signal<float> PresentedWidth = new(SidebarDesignInfo.DefaultWidth(SidebarDesign.Classic));
public static readonly Signal<bool> DragPeek = new(false);   // unchanged

public static SidebarResizeRules.State ResizeState() => new(Regime.Peek(), RailDetent.Peek(), Width.Peek());

/// <summary>Drag-end: resolve the seam into a settle, write the three facts, persist, and park the seam on the target.</summary>
public static void CommitSeam(float velocityDipPerSec = 0f)
    => Apply(SidebarResizeRules.Resolve(Seam.Peek(), ResizeState(), velocityDipPerSec));

public static void ToggleRegime() => Apply(SidebarResizeRules.Toggle(ResizeState()));
public static void SetRegime(SidebarRegime regime) { if (Regime.Peek() != regime) ToggleRegime(); }
public static void StepSeam(int direction, bool large) => Apply(SidebarResizeRules.Step(ResizeState(), direction, large));
public static void SetRailDetent(SidebarRailDetent d)
{
    RailDetent.SetIfChanged(d);
    Platform.Settings.Set(Platform.Keys.SidebarRailDetent, (int)d);
    if (Regime.Peek() == SidebarRegime.Rail) Seam.SetIfChanged(SidebarResizeRules.StripOf(d));
}
public static void ResetWidth() => Apply(new(SidebarRegime.Expanded, RailDetent.Peek(),
    SidebarDesignInfo.DefaultWidth(Design.Peek()), SidebarDesignInfo.DefaultWidth(Design.Peek())));

static void Apply(in SidebarResizeRules.Settle s)
{
    var design = Design.Peek();
    Width.SetIfChanged(s.ExpandedWidth);
    Regime.SetIfChanged(s.Regime);
    RailDetent.SetIfChanged(s.Detent);
    Seam.SetIfChanged(s.TargetWidth);
    SidebarPaneState.Snapshot(Platform.Settings, design, new SidebarPaneSnapshot(s.ExpandedWidth, s.Regime));
    if (s.Regime == SidebarRegime.Rail) Platform.Settings.Set(Platform.Keys.SidebarRailDetent, (int)s.Detent);
}
```
Delete: `Tiers`, `SetViewportWidth` + `s_viewportWidth`, `Collapsed`, `WidthUserSet`, `CommitWidthDrag`, `SetCollapsed`, `SetResponsiveWidth`. `Boot` reads `SidebarPaneState.Restore(settings, design)` + `RailDetent.Value = SidebarPaneState.RestoreDetent(settings)`; `SwitchDesign` snapshots `(Width, Regime)` and restores the incoming pair; the detent is not per design and is not touched.

`Shell/Sidebar.Modes.cs:85-165`:
```csharp
public static float DefaultWidth(SidebarDesign d) => d switch   // the former MID tier (D4)
{ SidebarDesign.LibraryV3 => 340f, SidebarDesign.Curated => 320f, _ => 280f };

public readonly record struct SidebarPaneSnapshot(float Width, SidebarRegime Regime);
public static class SidebarPaneState
{
    static SettingKey<float> WidthKey(SidebarDesign d) => Platform.Keys.SidebarWidth(SidebarDesignInfo.Slug(d), SidebarDesignInfo.DefaultWidth(d));
    public static void Snapshot(IAppSettings s, SidebarDesign d, in SidebarPaneSnapshot p)
    { s.Set(WidthKey(d), SidebarPaneBounds.Clamp(p.Width)); s.Set(Platform.Keys.SidebarCollapsed(SidebarDesignInfo.Slug(d)), p.Regime == SidebarRegime.Rail); }
    public static SidebarPaneSnapshot Restore(IAppSettings s, SidebarDesign d)
        => new(SidebarPaneBounds.Clamp(s.Get(WidthKey(d))), s.Get(Platform.Keys.SidebarCollapsed(SidebarDesignInfo.Slug(d))) ? SidebarRegime.Rail : SidebarRegime.Expanded);
    public static SidebarRailDetent RestoreDetent(IAppSettings s) => SidebarRailMetrics.Coerce(s.Get(Platform.Keys.SidebarRailDetent));
}
```
`Platform/Platform.cs:367-375`: delete `SidebarWidthUserSet(slug)`; add
```csharp
/// <summary>The collapsed rail's size, GLOBAL across designs (0 Compact · 1 Default · 2 Large; unknown ⇒ Default).</summary>
public static readonly SettingKey<int> SidebarRailDetent = new("sidebar.rail.detent", 1);
```

### 4.6 Shell frame (`Shell/Shell.UI.cs`)
Replace the effects at `:224-264` with:
```csharp
// LAST RESORT (D1-b): the window cannot hold the expanded floor and the content floor. Hysteretic; derived.
if (!_narrowSeeded) { _narrowSeeded = true; Ui.LastResort.SetIfChanged(SidebarResizeRules.LastResort(vp.Peek().Width, false, MinContentW)); }
UseSignalEffect(() =>
{
    bool cur = Ui.LastResort.Peek();
    bool next = SidebarResizeRules.LastResort(vp.Value.Width, cur, MinContentW);
    if (next == cur) return;
    Ui.LastResort.Value = next;
    if (!next) Ui.DrawerOpen.SetIfChanged(false);
});
// THE ONE PRESENTATION EFFECT: a live drag presents Track(seam); at rest, Present(state, viewport). Never writes Width.
UseSignalEffect(() =>
{
    float vpW = vp.Value.Width;
    float seam = Sidebar.Seam.Value;
    bool dragging = _sidebarDragging.Value;
    var state = new SidebarResizeRules.State(Sidebar.Regime.Value, Sidebar.RailDetent.Value, Sidebar.Width.Value);
    if (dragging)
    {
        var live = SidebarResizeRules.Track(seam, in state);
        Sidebar.Regime.SetIfChanged(live.Regime);          // the pane re-skins layers off this; persisted only at commit
        Sidebar.RailDetent.SetIfChanged(live.Detent);      // mid-drag "click" into a detent (persisted at commit)
        Sidebar.PresentedWidth.SetIfChanged(live.PresentedWidth);
        _sidebarFade.SetIfChanged(live.Fade);
        _seamVelocity = SeamVelocity(seam);                 // (seam − last) / dt from Clock.Elapsed; plain fields
        return;
    }
    _sidebarFade.SetIfChanged(1f);
    Sidebar.PresentedWidth.SetIfChanged(SidebarResizeRules.Present(in state, vpW, MinContentW));
});
UseSignalEffect(() => FgMotion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, _sidebarDragging.Value));   // unchanged
```
The column (`:488-496`): `Width = Prop.Of(static () => Sidebar.DragPeek.Value ? Sidebar.Width.Value : Sidebar.PresentedWidth.Value)` (`FrameRules.SidebarPaneWidth` becomes `SidebarPaneWidth(bool dragPeek, float expanded, float presented)`), `Animate = SidebarPaneAnim` with `TransitionDynamics.Spring(0.30f, 0.85f)` (D5).

The seam (`:546-558`):
```csharp
static readonly Splitter.SplitterOptions SidebarSeamOptions = new()
{   // CLAMP-ONLY: no `collapsed` argument ⇒ the engine detent is off (SplitterCore.Detent false) and the raw cell reaches the rail floor.
    Min = SidebarResizeRules.RailFloorW, Max = SidebarResizeRules.ExpandedMaxW, ShowIndicator = false,
};
// built once in Render (needs the overlay service for the menu)
var seamParts = UseMemo(() => new TemplateParts
{
    [Splitter.PartRoot] = b => (b with
    {
        Focusable = true, TabStop = true, AllowFocusOnInteraction = false, Role = AutomationRole.Slider,
        OnKeyDown = OnSeamKey,
        OnPointerPressed = static e => { if (e.ClickCount == 2) Sidebar.ToggleRegime(); },
    }).WithContextMenu(overlay, Sidebar.LayoutMenu.Model),
}, DepKey.Empty);
…
Transform = Prop.Of(static () => Affine2D.Translation(Sidebar.PresentedWidth.Value, 0f)),   // FrameRules.SidebarSeamX deleted
Width = Prop.Of(static () => FrameRules.SidebarSeamWidth(Ui.LastResort.Value)),
Children = [Splitter.Create(Sidebar.Seam, static () => Sidebar.CommitSeam(_seamVelocity), SidebarSeamOptions,
                            dragging: _sidebarDragging, parts: seamParts)],
```
```csharp
static void OnSeamKey(KeyEventArgs e)
{
    if (e.Handled) return;
    bool shift = (e.Mods & KeyModifiers.Shift) != 0;
    switch (e.KeyCode)
    {
        case Keys.Left:  Sidebar.StepSeam(-1, shift); break;
        case Keys.Right: Sidebar.StepSeam(+1, shift); break;
        case Keys.Home:  Sidebar.StepSeam(-1, large: true) /* loops to floor */; break;   // or a dedicated SetExpandedWidth(Min)
        case Keys.End:   Sidebar.SetExpandedWidth(SidebarResizeRules.ExpandedMaxW); break;
        case Keys.Enter or Keys.Space: Sidebar.ToggleRegime(); break;
        default: return;
    }
    e.Handled = true;
    Announcer.Say(Sidebar.Regime.Peek() == SidebarRegime.Rail
        ? Loc.Format("sidebar.seam.announceRail", Loc.Get(RailDetentLabelKey(Sidebar.RailDetent.Peek())))
        : Loc.Format("sidebar.seam.announceWidth", (int)Sidebar.Width.Peek()));
}
```
Hamburger (`:667-672`): `if (Ui.LastResort.Peek()) { drawer toggle } else Sidebar.ToggleRegime();`. The queue-rail fit (`:329-333`): `Ui.RailFits = CanFitRail(vp, Sidebar.PresentedWidth.Value, Ui.RailWidth.Value)`. `NarrowDrawer` (`:1318-1403`) reads `Ui.LastResort` instead of `Ui.NarrowShell`; `Layout.DrawerWidth` stays. `Shell.cs`: `Ui.NarrowShell` → `Ui.LastResort` (`:1108`), delete `Ui.SidebarPresentedCompact` (`:1112`), `FrameRules.PresentedCompact`/`SidebarSeamX` (`:1863-1870`).

### 4.7 Rail renderer (`Shell/Sidebar.UI.Rail.cs`, `Shell/Sidebar.UI.cs`, `Shell/Sidebar.Cards.cs`, `Platform/Surface.Rules.cs`)
- `Rail.Frame(owner)` and `RailSlot.Render` read `var m = SidebarRailMetrics.For(Sidebar.RailDetent.Value);` first (that read is the subscription; a detent change re-renders the ~25 realized tiles once). Signatures: `IconTile(key, glyph, selected, onClick, tooltip, in SidebarRailMetrics m, …)`, `ArtTile(key, data, in m, dropActive)`, `SeedTile(in m, key)`, `Divider(in m)` (`Width = m.DividerW`), the row item `Height = (isDivider ? DividerExtent : m.Pitch) + pads`.
- `Shape.RailTile` → `Shape.RailTileOf(float tile) => new(Row, ListRow, tile − 2·ring, 0, 0, false, 0f, MenuPlacement.None, PlayReveal.Reveal, tile, Labels: false)` (`Surface.Rules.cs:116-117`); `SidebarCards.RailOf(o, sectionId, in entry, sel, in SidebarRailMetrics m)` decodes at `m.Art` (`Sidebar.Cards.cs:223`).
- `PaneView`: `RailLayout = RepeatLayout.Extents(RailExtentSeed, estimatedExtent: SidebarRailMetrics.For(Default).Pitch)` (`Sidebar.UI.cs:565`); `RailExtentSeed` passes `SidebarRailMetrics.For(Sidebar.RailDetent.Peek())` (`:1589`); add `UseSignalEffect(() => { _ = Sidebar.RailDetent.Value; ReseedRailExtents(); RailVersion.Value = RailVersion.Peek() + 1; })`; the fold `:579` becomes `bool compact = !InDrawer && (Sidebar.Regime.Value == SidebarRegime.Rail || Shell.Ui.LastResort.Value) && !_dragPeek.Value;`; compact layer `Width = Prop.Of(static () => Sidebar.PresentedWidth.Value)` (`:637`); `SetCollapsed(false)` at `:2198` and `Rail.cs:275` → `SetRegime(SidebarRegime.Expanded)`.
- V3 (`Sidebar.UI.LibraryV3.cs`): `CollapsePane/ExpandPane` (`:371-372`) → `SetRegime(…)`; `BuildRailFooter/BuildRailHead/RailHeadTiles` (`:412-468`) take the metrics; `RailHeadTileCount` unchanged. Classic's `RailFooter` (`Sidebar.UI.cs:199-200`) → `box: m.Tile, glyph: m.Glyph` read inside the delegate.

### 4.8 Menus + Settings + loc
`Sidebar.UI.Menus.cs:59-77`:
```csharp
MenuFlyoutItem.Separator,
MenuFlyoutItem.SubMenu(Loc.Get("sidebar.rail.size"),
[
    MenuFlyoutItem.RadioItem(Loc.Get("sidebar.rail.compact"), detent == SidebarRailDetent.Compact, static () => SetRailDetent(SidebarRailDetent.Compact)),
    MenuFlyoutItem.RadioItem(Loc.Get("sidebar.rail.default"), detent == SidebarRailDetent.Default, static () => SetRailDetent(SidebarRailDetent.Default)),
    MenuFlyoutItem.RadioItem(Loc.Get("sidebar.rail.large"),   detent == SidebarRailDetent.Large,   static () => SetRailDetent(SidebarRailDetent.Large)),
], Icons.SplitView),
new(Loc.Get("sidebar.menu.resetWidth"), default, MathF.Abs(Width.Peek() - SidebarDesignInfo.DefaultWidth(design)) > 0.5f, ResetWidth),
```
Settings (`Settings.UI.Appearance.cs:479-486`): add `Item(Loc.Get(Strings.Settings.Sidebar.RailSize), Loc.Get(Strings.Settings.Sidebar.RailSizeSub), RailDetentPicker())` for every design, with `static readonly Signal<int> s_railDetent` mirrored from `Sidebar.RailDetent` in a `UseEffect` (the `s_zoomIndex` pattern `:46, :209-221`).

Loc keys (all three files, CRLF/UTF-8 no BOM): `sidebar.rail.size` "Collapsed rail size", `sidebar.rail.compact` "Compact", `sidebar.rail.default` "Default", `sidebar.rail.large` "Large", `sidebar.seam.announceWidth` "Sidebar width {0}", `sidebar.seam.announceRail` "Icon rail, {0}", `settings.sidebar.railSize` / `railSizeSub` "How wide the icon rail is when the sidebar is collapsed."; `sidebar.menu.resetWidth` kept.

---

## 5. Tests (pure classes only; no engine, no source text)

**New `src/apps/Wavee.Tests/SidebarResizeRulesTests.cs`**
- `Detents_DeriveFromTheThumbLadder` — `For(Compact)` = (32, 28, 16, 6, 48, 38, 16); `For(Default)` = (40, 36, 16, 6, 56, 46, 24) and `StripW == Design.Size.NavCompactW`; `For(Large)` = (64, 60, 20, 8, 80, 70, 48).
- `Thresholds_AreOrderedAndDerived` — `RailFloorW 48 < RailCeilW 80 < RailEnterW 116 < ExpandedEnterW 140 < ExpandedMinW 180 ≤ ExpandedMaxW 460`; `RailEnterW == NavPaneMinW − 64`; `ExpandedEnterW − RailEnterW == NavPaneHysteresisDip`.
- `Track_ExpandedIsFreeAboveTheFloor` (Theory 180/181/300/459.5/460 → presented == raw, fade 1, no detent change).
- `Track_HoldsAtTheFloorThroughTheApproachBand` (170 → Expanded, 180, 0.35 < fade < 1; 116.5 → fade ≈ 0.35).
- `Track_EntersTheRailPastRailEnter` (115.9 from Expanded(300) → Rail, Large, 80).
- `Track_RailStripFollowsThePointer` (60 → 60/Default; 49 → 49/Compact; 79 → 79/Large).
- `Track_DetentEdgesHaveHysteresis` (from Default: 50.5 stays Default, 47.9 → Compact; from Compact: 53.5 stays Compact, 56.1 → Default).
- `Track_LeavesTheRailOnlyPastExpandedEnter` (139 from Rail → Rail/80 with fade < 1; 140 → Expanded/180/fade 1).
- `Track_FadeCueIsSymmetricAcrossBothBands`.
- `Track_NonFiniteRawPresentsThePreviousState`.
- `Resolve_SnapsToTheNearestDetent` (60 → 56; 69 → 80; 49 → 48; target == StripOf(detent)).
- `Resolve_FlickPicksTheNeighbourInTheDirectionOfTravel` (58, v −900 → Compact; 58, +900 → Large; 58, −300 → Default; at Compact a left flick stays Compact).
- `Resolve_ExpandedCommitsTheClampedRawWidth` (300 → 300; 999 → 460).
- `Resolve_ApproachBandReleaseStaysExpandedAtTheFloor` (150 → Expanded 180).
- `Resolve_RailSettleNeverTouchesTheExpandedMemory` (prev 333 → Settle.ExpandedWidth 333).
- `Present_YieldsToTheContentFloorWithoutWritingThePreference` (300 @ 1440/480 → 300; @ 756 → 276; @ 700 → 220; @ 600 → 180; viewport 0 → 300).
- `Present_RailIsNeverYielded` (Large @ 400 → 80).
- `LastResort_IsHystereticAndDerived` (659.9 enters, 699 holds, 700 leaves, 0 holds).
- `Step_NudgesExpandedByEightOrForty`, `Step_LeftAtTheFloorEntersTheRailAtLarge`, `Step_RightPastLargeExpandsAtTheRememberedWidth`, `Step_WalksTheDetents`.
- `Toggle_RoundTripsKeepingBothMemories`.
- `Coerce_UnknownStoredDetentIsDefault` (−1, 3, int.MaxValue → Default).

**Edit `SidebarDesignTests.cs` region 1 (`:52-277`)**: delete the tier/ladder/latch facts (`EachDesign_HasItsOwnTierTriple`, `FirstVisitToADesign_UsesItsOwnDefaultTier`, `FreshProfile_At1770_…` → rewrite as `FreshProfile_RestoresTheDesignDefaultExpanded`, `Breakpoints_AndHysteresis_…`, `PreMeasureSeed_…`, `PinningOneDesignsWidth_…`, `TierLadderReSeeds_…`, `ResetWidth_ClearsUserSet_…`); rewrite `SwitchingDesigns_SnapshotsOutgoing_AndRestoresIncoming` on `(Width, Regime)`; add `RailDetent_IsGlobal_AndSurvivesADesignSwitch`, `CollapsedKey_MeansRailRegime`, `Restore_ClampsAHandEditedWidth`; keep the slug/key-name facts (drop the `WidthUserSetKey` line at `:317`).
**Edit `ShellNavTests.cs:293-325`**: delete `The_narrow_band_is_hysteretic`, `A_zero_width_never_moves_a_band`, `The_nav_pane_widens_at_once_…`, `An_unmeasured_viewport_takes_the_narrow_tier`; keep the drawer-width facts.
**Edit `ShellFrameRulesTests.cs:16-42`**: `SidebarPaneWidth(dragPeek, expanded, presented)`; delete the `PresentedCompact`/`SidebarSeamX` facts; `SidebarSeamWidth(lastResort)`.
**Edit `SidebarPlannerTests.cs:2918-3008`**: rebuild `Expanded()`/`Compact()` on the new snapshot; add `RailWidthMustMatchTheDetent` (Large rendered 56 → `RailWidthMismatch`), `PresentedMayNotExceedPreferred`, `LastResortPresentsTheRail`.
**`ShellNarrowDrawerTests.cs`**: `ShouldMount(lastResort, drawerOpen)` — same claims.

---

## 6. Work packages (disjoint files) + orchestrator verification

Fixed API contract every WP codes against (from §4): `SidebarRailDetent`, `SidebarRegime`, `SidebarRailMetrics.{For,Of,TileOf,Coerce}`, `SidebarResizeRules.{State,Live,Settle,Track,Resolve,Present,LastResort,Step,Toggle,StripOf,RailFloorW,RailCeilW,ExpandedMinW,ExpandedMaxW}`, `SidebarRailExtents.{Pitch(in m),HeadExtentOf(tiles,in m),ExtentOf(…,in m)}`, `Sidebar.{Width,Regime,RailDetent,Seam,PresentedWidth,DragPeek,ResizeState,CommitSeam,ToggleRegime,SetRegime,StepSeam,SetRailDetent,SetExpandedWidth,ResetWidth}`, `SidebarDesignInfo.DefaultWidth`, `SidebarPaneState.{Snapshot,Restore,RestoreDetent}`, `Platform.Keys.SidebarRailDetent`, `Shell.Ui.LastResort`, `FrameRules.SidebarPaneWidth(dragPeek, expanded, presented)`, `FrameRules.SidebarSeamWidth(lastResort)`.

| WP | files (edit only these) | work |
|---|---|---|
| **1 CORE rules** | NEW `Shell/Sidebar.Resize.cs`; `Shell/Sidebar.cs` (`:59-133` trim, `:857-917` parametrise, `:1229-1313` invariant) | §4.1–4.4 |
| **2 Service + keys** | `Shell/Sidebar.Host.cs` (`:91`, `:107-114`, `:159-194`, `:245-306`), `Shell/Sidebar.Modes.cs` (`:85-165`), `Platform/Platform.cs` (`:367-375`) | §4.5 |
| **3 Shell frame** | `Shell/Shell.UI.cs`, `Shell/Shell.cs` (`:1100-1115`, `:1846-1870`), `Shell/Shell.Chrome.cs` (`:26-35`, `:169-225`) | §4.6 + wire `Diagnostics.SidebarPaneFrame` |
| **4 Rail + pane** | `Shell/Sidebar.UI.Rail.cs`, `Shell/Sidebar.UI.cs` (`:188-201`, `:565`, `:579`, `:620-642`, `:1589-1599`, `:1996-2007`, `:2198`), `Shell/Sidebar.Cards.cs` (`:216-229`), `Platform/Surface.Rules.cs` (`:114-117`), `Shell/Sidebar.UI.LibraryV3.cs` (`:371-372`, `:412-468`, `:1064-1067`, `:1092-1093`) | §4.7 |
| **5 Pickers + loc** | `Shell/Sidebar.UI.Menus.cs` (`:52-81`), `Screens/Settings.UI.Appearance.cs` (`:466-498` + mirror signal), `assets/loc/{en-US,nl,ko-KR}.json` | §4.8 |
| **6 Tests** | NEW `Wavee.Tests/SidebarResizeRulesTests.cs`; `SidebarDesignTests.cs`, `ShellNavTests.cs`, `ShellFrameRulesTests.cs`, `SidebarPlannerTests.cs`, `ShellNarrowDrawerTests.cs` | §5 |
| **7 Docs (after)** | `.claude/skills/wavee-sidebar/{architecture,where-to-change-what,pitfalls}.md`, `docs/guide/sidebar-extension-platform.md` §3 scalars note, CHANGELOG `(#n)` | the 56-DIP rail is now "the rail at the user's detent"; the pane invariant contract |

No engine work package is needed (§1.7). Sidebar iron rule 11 holds.

**Orchestrator verification**
1. `dotnet build Wavee.slnx` and `-c Release` clean (TreatWarningsAsErrors); grep that `WidthUserSet`, `SetResponsiveWidth`, `NavPaneDefaultFor`, `SidebarPresentedCompact`, `PresentedCompact`, `CompactRailW`, `Tiers(` have zero references.
2. `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` Debug and Release green.
3. `dotnet run --project src/apps/Wavee -- --fake`, window at **1440**: drag the seam 460→180 — no snapping, release anywhere sticks; relaunch restores it; switch design → each design keeps its own width/regime; the queue rail still fits/floats off the presented width.
4. Drag left past 180: pane holds at 180 and dims; at ≈116 it pops to the Large rail (80); continue: Default at ≈68, Compact at ≈52; release between detents → springs to the nearest; a fast flick picks the neighbour. Drag right from Compact: Default → Large → holds at 80 dimming → pops expanded at 180 at ≈140 → free. Confirm the tiles re-skin (28/36/60 art, 16/16/20 glyphs, divider 16/24/48) and the rail list reseeds without a jump (V3 head seed = `HeadExtentOf(tiles, m)`).
5. Double-click the seam toggles regime; Tab to the seam, ←/→/Shift/Home/End/Enter work and announce (Narrator); right-click the seam/rail → "Collapsed rail size" radios; Settings → Appearance → Sidebar combo mirrors live.
6. Window at **756**: a 300 pane yields to 276 (page keeps 480); grow back → 300 returns; the preference key still says 300.
7. Window at **~310**: last-resort band (D1-b): inline column shows the rail at the user's detent (48/56/80), hamburger opens the drawer at `310−32 = 278`, Escape/scrim/navigation close it; grow past 700 → the user's expanded width returns, nothing persisted changed.
8. Log: `sidebar.pane.invariant_failed` never fires across the above (the probe is now assigned).
9. Hamburger / V3 "<" / V3 overflow / rail expand tile / `SectionTile` / `ExpandFolderInPane` all flip regime correctly; drag-peek onto the rail still presents the expanded pane for the drag and reverts.

---

## 7. Owner decisions (recommendation first)

**Decided 2026-10-02: every recommendation below is accepted.** D1 = (b) last-resort rail + drawer below 660/700 with a
480 content floor; D2 = Large 80 (64 tiles); D3–D10 as recommended.

| # | decision | recommendation |
|---|---|---|
| D1 | Degenerate window (sidebar floor 180 + content floor cannot both fit): (a) never flip, page starves; **(b) last-resort band** — inline rail at the user's detent + the existing drawer, entered only below `180 + contentFloor` with 40-DIP hysteresis; and the content floor: `Shell.MinContentW 480` vs the player bar's 300 floor | **(b) with 480** (band at 660/700, unreachable on any desktop window; today's 720 band and ladder fire constantly) |
| D2 | Large detent: 80 (`Thumb64` tile) vs "double" 112 (a 96 tile, on no ladder) | **80** |
| D3 | Expanded floor: keep 180 (#84) vs lower | **180** (below it V3's toolbar cannot seat search + sort — `LeadInset 21 + 32 + 4 + 28 + 16 ≈ 101` plus a readable label) |
| D4 | Default width per design with no ladder: former Mid (280/340/320) vs Narrow (240/300/280) | **Mid** |
| D5 | Settle dynamics: keep the 300 ms SplitView tween vs `TransitionDynamics.Spring(0.30, 0.85)` | **spring** (interruptible retarget) |
| D6 | Rail detent scope: global vs per design | **global** |
| D7 | Persistence: `IAppSettings` keys vs `sidebar-layout.json` + reducer | **IAppSettings** (a width must not enter the undo ring or bump `LayoutVersion`) |
| D8 | "Reset width": delete vs repurpose to "back to the design default" | **repurpose**, enabled when `Width ≠ DefaultWidth` |
| D9 | Flick-to-neighbour on release (800 DIP/s tuning constant) | **include** |
| D10 | Mid-drag detent "click" (tile metrics switch live at the midpoints) vs switch only on release | **live** (the strip already follows the pointer; the tile step is the feedback) |

### Critical Files for Implementation
- `C:\wavee\waveemusic\src\apps\Wavee\Shell\Sidebar.cs` (SidebarPaneBounds :70-133, SidebarRailExtents :857-917, SidebarPaneInvariant :1229-1313; new sibling `Shell\Sidebar.Resize.cs`)
- `C:\wavee\waveemusic\src\apps\Wavee\Shell\Shell.UI.cs` (effects :224-264, column :488-505, seam :546-558, hamburger :667-672, drawer :1318-1454)
- `C:\wavee\waveemusic\src\apps\Wavee\Shell\Sidebar.Host.cs` (pane state :245-306, Boot :107-114, SwitchDesign :173-194)
- `C:\wavee\waveemusic\src\apps\Wavee\Shell\Sidebar.UI.Rail.cs` (the rail renderer; metrics consts :42-53, tiles :286-357)
- `C:\wavee\waveemusic\src\apps\Wavee\Shell\Sidebar.Modes.cs` (SidebarDesignInfo.Tiers :88-95, SidebarPaneState :112-165) and `C:\wavee\waveemusic\src\apps\Wavee\Shell\Shell.Chrome.cs` (duplicate ladder :169-225, narrow band :28-35)