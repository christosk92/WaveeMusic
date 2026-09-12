# Sidebar: pane renderer, rows, rail mode, the three designs (Classic / Library V3 / Wavee Curated) — 0.3 visual fidelity contract

> 0.2.9 sources: `Features/Sidebar/**` = 29,025 lines in 89 files + `Wavee.Core/Sidebar/**` 2,526 lines in 9 files.
> This chapter owns: the 14 root files (3,855), `Pane/**` (10 files, 6,470), `Shared/**` (11 files, 2,137),
> `Modes/**` (14 files, 3,664), and the pure rules in `Data/**` it renders from (6,311, shared with ch. 26).
> `Curated/**` (6 files, 4,247 — the customizer page) and `Persistence/**` (4 files, 1,491) are **chapter 26**.
> | 0.3 target — **one five-file set for the whole sidebar platform** (arbitration 2026-09-12, A4; ch. 26 §9.3 carries
> the same list): `Shell/Sidebar.cs` (CORE: planner, geometry, diff/resolve, the pure rules) · `Shell/Sidebar.Doc.cs`
> (the layout document + reducer + wire) · `Shell/Sidebar.UI.cs` (pane, slot, rows, rail, the three designs) ·
> `Shell/Sidebar.Customizer.UI.cs` (ch. 26’s page) · `Shell/Sidebar.Host.cs` (store, binder, the four sources)
> | Wave 4 owner J — **the customizer is sequenced LAST in the wave**

All 0.2.9 paths below are relative to `src/apps/Wavee/` unless they start with `docs/`. After Wave 0 they live at
`src/apps/_old/Wavee/<same relative path>`.

---

## 0. The non-negotiables

Twelve checkable facts. Lose any one and the rebuilt sidebar is not 0.2.9.

1. **One renderer, three documents.** Classic, Library V3 and Curated are a `SidebarCustomLayout` + a
   `SidebarPaneConfig` over ONE `SidebarPane` (`Pane/SidebarPane.cs:52`). The renderer never branches on
   `Config.Design` (`Pane/SidebarPaneConfig.cs:23`). If any 0.3 file contains `if (design == …)` inside the row or rail
   builder, the rebuild has re-created the four-left-edges bug the unification deleted.
2. **One inset owner.** `SidebarPaneMetrics.PanePad = (8,8,8,12)` is applied once, around the virtualized list
   (`Pane/SidebarPaneMetrics.cs:25`, applied at `Pane/SidebarPane.cs:686`). Every row's art/glyph column starts at pane
   x = **21** (`Data/SidebarRowGeometry.cs:114`, `ArtX(0) = PaneEdge 8 + IndentFor(0) 4 + SelGutter 3 + LeadingGap 6`)
   — art rows, bare-glyph rows, tree rows, the V3 header's library mark, the V3 nav rows, the closed search box and the
   first filter chip, all on the same vertical line. Verify with a ruler on a screenshot, not by reading code.
3. **One height per SECTION, never per row.** `SidebarPaneMetrics.RowHeight(section)` (`:87`) derives from
   `(Density, Subtitles)`: Compact 32 · Cozy 40 / 44-with-subtitle · Comfortable 44 / 48
   (`Data/SidebarRowGeometry.cs:57-62`). A mixed 40/44 band silently breaks both the `Reorderable` slot pitch and the
   virtualizing host's extent table.
4. **One flat plan through one `ItemsView.CreateBound`.** `SidebarRowPlanner` flattens (document × projection) into ONE
   `SidebarRow[]` of 15 kinds; the pane renders it with one bound list over a measured variable-extent layout
   (`Pane/SidebarPane.cs:692-736`). No nested scrollers, no `Flow.For` over the projection — that is what lets a
   10k-playlist rootlist virtualize.
5. **The 4-state selection-aware ramp.** rest `WaveeColors.SelectedRest` = `Tok.AccentSubtle` (accent @ 14% light /
   16% dark) · hover = the subtle veil composed OVER it · pressed = the quieter veil over it
   (`Design/WaveeTokens.cs:272-274`, applied `Shared/SidebarEntityRow.cs:373-378,515-519`). A selected row must DARKEN
   on hover; it must never flatten to its own rest fill.
6. **Selection geometry is the 3×16 pill.** Every selectable realized row permanently owns a `SidebarSelectionPill`
   (`Shared/SidebarSelectionPill.cs:28-29`) in the 3-DIP gutter at `IndentFor(depth)`; a route edge runs the WinUI
   NavigationView 600 ms paired Offset+Scale flight (`NavigationSelectionMotion.cs:13`, driven from
   `Pane/SidebarPane.cs:1555`). Its opacity is a BOUND read of `SidebarPillState` (`Data/SidebarPillState.cs:39`) —
   never a mount-time literal, or two pills light at once.
7. **One chevron, rotated, never a glyph swap.** `SidebarChevron` animates `AnimChannel.Rotation` on
   `MotionTokenId.DisclosureChevron` = 167 ms `cubic-bezier(.167,.167,0,1)` (`Shared/SidebarChevron.cs:41-72`,
   `MotionTok.cs:182`): headers 0→180°, folders 0→90°. A recycle SEEDS the resting angle with no motion.
8. **Drop cue: line ⟺ ordering, plate ⟺ Into, never both.** One published `SidebarDropSlot`
   (`Pane/SidebarPane.cs:154`) drives the row's 2-DIP accent caret with its 6-DIP terminal dot AND the accent@0.18
   plate; both are bound props off the LIVE slot index (`Pane/SidebarPaneSlot.cs:1368,1410`). The caret is indented to
   `TreeContentX(depth)` = 13/25/37/49/61 DIP (`Data/SidebarRowGeometry.cs:139`).
9. **Both layers always mounted.** Expanded (measured at the persisted OPEN width) and the 56-DIP rail are both mounted
   at all times, cross-faded by opacity + `HitTestVisible` (`Pane/SidebarPane.cs:523-596`), so text never reflows
   through a 56-DIP layout and `SidebarPaneInvariant` can assert a settled frame
   (`SidebarPaneInvariant.cs:52-71`).
10. **The rail is a real surface, not a stub.** 40-DIP tiles at a 6-DIP gap, 24×1 dividers, tooltip-as-label, a folder
    tile that opens a 300-DIP side flyout, playlist tiles that accept track deposits, and a 250 ms drag-peek dwell that
    slides the whole pane open for the rest of the gesture (`Shared/SidebarRailItem.cs`, `Pane/SidebarPaneRail.cs`,
    `Pane/SidebarRailFolderFlyout.cs`, `Pane/SidebarPane.cs:2440-2460`).
11. **Nothing ever vanishes.** A missing entity, an unresolvable action, a folder pin the rootlist lost — all render
    visible-but-disabled at 0.55 opacity with a reason tooltip and a one-verb menu
    (`Pane/SidebarPaneSlot.cs:554,703,769`). Only an explicit user verb removes a row.
12. **Quiet counts.** ONE count renderer: 11 px `Tok.TextTertiary` number, or a 20×12 shimmer plate while pending
    (`Shared/SidebarCounts.cs:27,36`). Classic's accent `InfoBadge` pill is gone and must not come back.
13. **V3's chrome is fixed, the list is not.** Nav band → header 44 → toolbar 36 → chip rail 40 → rule (→ breadcrumb 32)
    sit ABOVE the scroll surface through `SidebarPaneConfig.Head` (`Modes/LibraryV3/LibraryV3Chrome.cs:84-121`); a
    search, a filter or a drill level never moves, hides or reorders Home.
14. **Curated's customize mode is the LIVE pane.** One uniform 44-DIP card per section, one expands at a time, hidden
    sections stay dimmed in place with a "Hidden" tag, options open in a 320×520 popover anchored to that card's "…"
    (`Pane/SidebarPaneEditCard.cs`, `Data/SidebarEditPlan.cs`). There is no preview of a sidebar; there is the sidebar.
15. **Zero-allocation frames while dragging and scrolling.** Cues, pill opacity, rail drop washes and insertion lines
    are `Prop.Of` bound thunks reading the live slot index; a per-pointer-move re-render is a defect, not a tuning miss
    (`Pane/SidebarPaneSlot.cs:1365-1453`, `Shared/SidebarRailItem.cs:83-121`).

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
WaveeShell docked column  (WaveeShell.cs:1002)  Width = presentedCompact && !DragPeek ? 56 : _sidebarWidth
│                                               Animate = SidebarPaneAnim (Reveal, 300 ms, cubic-bezier(0,.35,.15,1)) — WaveeShell.cs:184
├─ opacity wrapper (_sidebarFade)               WaveeShell.cs:1026 · IsolateLayout=true (layout firewall)
└─ SidebarHost                                  SidebarHost.cs:15 — THE ONE MOUNT SEAM
   │  reads prefs.Design.Value (:37); mounts under Key "sidebar.classic|.v3|.curated" (:56)
   │  Enter = opacity 0→1, Transition = MotionTok.ControlFast (150 ms)                       (:57-58)
   ├─ WaveeSidebar            (Classic)   WaveeSidebar.cs:39   → doc = SidebarBuiltInDocuments.Classic (cached)
   ├─ LibraryV3Sidebar        (V3)        Modes/LibraryV3Sidebar.cs:39 → doc = LibraryV3Document.Build (cached)
   └─ CuratedSidebar          (Curated)   Modes/CuratedSidebar.cs:23  → doc = prefs.Layout + Shortcuts prepend
      └─ SidebarPane(config, route, go, compact, expandedWidth, inDrawer)     Pane/SidebarPane.cs:52
         ├─ "expanded-layer"  Width=ExpandedWidth, Opacity=compact?0:1, HitTestVisible=!compact   (:523)
         │  ├─ Config.Head?.Invoke()                     — V3 only  (:653)
         │  │   └─ LibraryV3Chrome                        Modes/LibraryV3/LibraryV3Chrome.cs:32
         │  │      ├─ LibraryV3NavBand      "v3-nav"      LibraryV3NavBand.cs:41
         │  │      │   ├─ DestinationRail (30)            :156  — 5 words, scrolls, fades, pager chevrons
         │  │      │   ├─ Route/Entity/Track/Action rows (40 each, from prefs.TopBar)   :338-461
         │  │      │   └─ Divider (content lane)          :112
         │  │      ├─ LibraryV3Header       "v3-header"   LibraryV3Header.cs:23   (44)
         │  │      ├─ LibraryV3Toolbar      "v3-toolbar"  LibraryV3Header.cs:169  (36)
         │  │      │   ├─ LibraryV3Search   "v3-search"   LibraryV3Search.cs:34
         │  │      │   ├─ spacer (Grow)
         │  │      │   └─ V3SortViewTrigger "v3-sortview" V3SortViewFlyout.cs:29
         │  │      ├─ LibraryV3Chips        "v3-chips"    LibraryV3Chips.cs:69    (40, horizontal scroll)
         │  │      ├─ Divider               "v3-chrome-rule"                       :92
         │  │      ├─ Breadcrumb (drilled)  "v3-breadcrumb"                        :129 (32)
         │  │      └─ ErrorBanner | EmptyBand                                      :201 | :162
         │  ├─ SidebarPaneSearchHead  "head"  — Classic:no · Curated:yes · V3:no    Pane/SidebarPaneInlineControls.cs:177
         │  │    rendered only when the document holds a VISIBLE EntityList, and never in edit mode (SidebarPane.cs:658)
         │  │    pad (8,8,8,4) — it is chrome OUTSIDE the padded list, so it carries the inset itself; field 32 tall, 13 px
         │  └─ "plan-pad" Padding=(8,8,8,12)                                        (:686)
         │     └─ ItemsView.CreateBound(Plan.Rows.Count, slot, _rowLayout, ListOptions)  (:692)
         │        │  Overscan 2 · CacheExtentPx 240 · ContentType = row kind · ScrollKey = prefix(+".drawer")
         │        │  Reorder = {ItemDisplacement, DisplacementVersion} · Disclosure = {Version, PendingExpand, …}
         │        └─ SidebarPaneSlot  (one Component per slot)                      Pane/SidebarPaneSlot.cs:33
         │           ├─ SectionHeader  → SidebarSectionHeader.Header + chevron + [sort][+][layout-menu]   (:153)
         │           │                   (+ SidebarPaneInlineControls.Chips under the band when Display.InlineControls)
         │           │                   Banded(): 8 above / 2 below, suppressed at index 0 · after Divider · after HeaderLabel
         │           ├─ HeaderLabel    → SidebarSectionHeader.Label                                        (:69)
         │           ├─ Divider        → SidebarSectionHeader.ExplicitDivider (16)                         (:71)
         │           ├─ IconRow/EntityRow/Placeholder → ItemOrEntity → EntryRow|TrackItemRow|RouteRow|
         │           │                                   ActionRow|MissingRow                              (:299)
         │           │   └─ ZStack[ DropPlate · SidebarEntityRow.Create · SidebarSelectionPill · InsertionLine ]  (:1335)
         │           ├─ FolderHeader   → FolderRow (+ SidebarChevron.Disclosure trailing, "+" button)      (:460)
         │           ├─ GridStrip      → wrapped cells, edge derived from pane width                       (:951)
         │           ├─ Empty         → SidebarPinDropZone | quiet 32-DIP hint | ActionCard row            (:1043)
         │           ├─ Skeleton      → SidebarSkeletons.Row                                               (:77)
         │           ├─ TreeEnd       → 24-DIP drop gutter + InsertionLine                                 (:1493)
         │           ├─ EntityCard    → Card (56/72/88) + hover play button                                (:822)
         │           ├─ PromptRow     → Prompt (48/56)                                                     (:1100)
         │           └─ SectionCard   → SidebarPaneEditCard.Build (44)                                     (:86)
         ├─ "compact-layer"  Width=56, Opacity=compact?1:0, DropTarget=RailPeekDropSpec(250 ms)  (:583)
         │  └─ UseMemo(ScrollView(SidebarPaneRail.Build))  deps (planVersion, Tok.Epoch, culture, railHead, route)  (:563)
         │     ├─ Config.RailHead?.Invoke()   — V3's nav tiles                       Pane/SidebarPaneRail.cs:43
         │     ├─ tiles from the RAIL plan: SidebarRailItem.Icon | .Art | .Divider    (:49-57)
         │     ├─ pending fallback: SidebarSkeletons.RailStack(4) when no tiles AND no binder  (:60)
         │     ├─ Config.RailFooter?.Invoke() — Classic "+" · V3 [Expand ▤][+]        (:64)
         │     └─ SidebarLayoutMenu.Button (box 40) — only when Config.RailLayoutMenu (:71)
         ├─ SidebarDragPeekWatcher "drag-peek" (zero-size; ends peek/cue/freeze on session end)  (:2474)
         └─ context-menu SHELL + childless SHIELD (Key "sidebar:context-shield")       (:628)

Services (context, not props): SidebarPreferences.Slot · ActionServices.Slot · Overlay.Service ·
PlaybackBridge.Slot · LibraryStore.Slot · NavPreviewStore.Slot · WaveeExtensionRegistry.Slot   (:459-468)
Pipeline (ch. 26): SidebarProjectionBinder.MountPoint() mounted once at the app root (WaveeShell.cs:1497).
```

### 1.2 The same tree in 0.3 terms

`Shell/Sidebar.cs` = CORE (pure, engine-free, source-included by tests: planner, geometry, diff/resolve, §8's rules) ·
`Shell/Sidebar.Doc.cs` = CORE (the layout document, the reducer and the `sidebar-layout.json` wire + migrations) ·
`Shell/Sidebar.UI.cs` = UI (Elements: pane, slot, rows, rail, the three designs) ·
`Shell/Sidebar.Customizer.UI.cs` = UI (ch. 26's page, sequenced last in the wave) ·
`Shell/Sidebar.Host.cs` = SHELL (services, persistence I/O, the binder pump, the four data sources).
Five files, one owner (J), arbitration 2026-09-12 — see the header and §9's budget. The rows below name three of
them because this chapter renders from three; `Sidebar.Doc.cs` and `Sidebar.Customizer.UI.cs` are ch. 26's halves of
the same set.

| 0.3 node | file · form | inputs | how data reaches it |
|---|---|---|---|
| `Sidebar.Host()` | Sidebar.Host.cs · `static Element Host(Signal<Route> route, Action<string,string?> go, Signal<bool> compact, Signal<float> width, bool inDrawer)` | signals | mounts the mode under `Key = Sidebar.MountKey(design)`; **Key remount** on design switch |
| `Sidebar.Classic/V3/Curated` | Sidebar.UI.cs · `static PaneConfig ClassicConfig()` etc. | none (delegates) | every member is a `Func`/`Action`; built once in `UseMemo(…, DepKey.Empty)` |
| `Sidebar.Pane` | Sidebar.UI.cs · `sealed class Pane : Component` | ctor `(PaneConfig, Signal<Route>, Action<string,string?>, Signal<bool> compact, Signal<float> width, bool inDrawer)` — **all frozen at mount** | live state arrives ONLY through `Config.Document()/Input()/ModeEpoch()/Edit()` invoked inside `Pane.Render` |
| `Sidebar.Slot` | Sidebar.UI.cs · `sealed class Slot : Component` | ctor `(Pane owner, RowScope scope)` | re-renders on `scope.Index.Value` (recycle) + `owner.SubscribeRowEpoch(index)` (per-row epoch) |
| row/rail primitives | Sidebar.UI.cs · pure statics over `in RowSpec` | value struct | never a Component per row (a mount per slot in a 10k list is the thing to avoid) |
| `Sidebar.Plan` (planner) | Sidebar.cs · `static RowPlan Build(in Layout doc, in Input p, PlanBuffers b)` | handles + edges | pure; ported 1:1 from `SidebarRowPlanner` |
| geometry/rules | Sidebar.cs | values | `RowGeometry`, `RowExtents`, `DropSlotResolver`, `DropCue`, `PillState`, `EditPlan`, `TreeSelection`, `NavLayout`, `ChipStrip`, `SearchRules`, `V3Document`, `V3View`, `FolderFlyoutNav` — all §8 |
| preferences/pins/edit session | Sidebar.Host.cs | `IAppSettings`, Store | `Sidebar.Prefs` is one reference-stable service provided at the app root (context, never a ctor arg) |

**Props freeze at mount — the three legal channels, per node:**

| Must update on… | Channel | 0.2.9 precedent |
|---|---|---|
| document / projection / pin / folder / mode-state change | the pane re-plans in a `UseMemo` keyed on `PlanDep` and publishes the plan as a **plain field**; realized rows re-render on their **own row-epoch signal** | `SidebarPane.PlanDep` (:791), `SubscribeRowEpoch` (:1331) |
| route change | one pane-level `UseSignalEffect` sweeps the plan and bumps only the rows that flipped | `RefreshSelection` (:1495) |
| playback change | one pane-level `UseSignalEffect` writes a packed per-row byte and bumps only changed rows | `RefreshPlayState` (:1391) |
| pointer-tracking cues (drop line/plate, rail wash, pill opacity, folder "+" reveal) | `Prop.Of` bound thunks that read `scope.Index.Value` — never a captured index | `DropPlate`/`InsertionLine` (:1368,:1410) |
| design switch | **Key remount** (`Sidebar.MountKey`) — fresh hooks, fresh section/scroll state | `SidebarHost.cs:56` |
| row count (frozen `ItemsView` prop) | `CountSignal`, written in a LAYOUT effect, never in render | `SidebarPane._rowCount` (:84, :903) |

---

## 2. Wireframes

Scale: ~8 DIP per monospace character. Pane widths annotated per frame. `│` at column 0 = the pane's left edge
(x = 0 of the sidebar column), not the window edge.

### W1 — Classic, docked, fully loaded @280 (the MID tier — viewport 1400–1799; < 1400 is the 240 narrow tier)

```
 x=0   8  12 15 21           53 59                                264 272  280
 │     │   │  │  │            │  │                                  │   │    │
 ├─────┴───┴──┴──┴────────────┴──┴──────────────────────────────────┴───┴────┤  ← PanePad.Top 8
 │  ▌ [⌂] Home                                                               │  44  the materialised Shortcuts band:
 │    [🔍] Search                                                            │  44  NO header row is planned for it
 │                                                                           │   8 section gap                (SidebarRowPlanner.cs:322)
 │    Pinned                                                       ⧉  ⌄      │  28  ⧉ = layout-menu button (24) — it hangs off
 │                                                                           │      the pane's FIRST SectionHeader row
 │                                                                           │   2
 │    [▩32] Chill Mix                                             ♪ 📌       │  44  art 32 · title 14/Body · sub 12/Caption
 │          50 songs                                                         │      trailing: equalizer 12 (playing) + pin 12
 │    [◯32] Hans Zimmer                                                      │  44  circular art (artist)
 │          Artist                                                           │
 │  ────────────────────────────────────────────────────────────             │  16 explicit Divider (hairline x 12→264)
 │    Your Library                                                 ⌄         │  28 (+8 band-top suppressed after Divider)
 │                                                                           │   2
 │    [▤] Albums                                                    64       │  40  glyph row (Subtitles:false ⇒ 40)
 │    [👤] Artists                                                  93       │  40  DOCUMENT ORDER, exactly:
 │    [♥] Liked Songs                                              128       │  40  albums · artists · liked · podcasts
 │    [📻] Podcasts                                                  7       │  40  · local  (SidebarBuiltInDocuments.cs:93-97)
 │    [📁] Local files                                                       │  40  local carries no count
 │  ────────────────────────────────────────────────────────────             │  16
 │    Playlists                                                    +  ⌄      │  28  + = SidebarCreateButton (24)
 │                                                                           │   2
 │    [📂32] Road trip                                           ⌄     +     │  44  FolderHeader — the disclosure chevron is
 │          3 items                                                          │      TRAILING, then (CountBadges only) a count,
 │    │  [▩32] Desert                                                        │  44  then the folder's own "+" (20 box, glyph 12)
 │    │  │     12 songs                                                      │      depth 1: one 12-DIP connector cell
 │    └─ [▩32] Coast                                                         │  44  elbow + half-height guide
 │          31 songs                                                         │
 │    [▩32] Deep Focus                                                       │  44
 │          88 songs                                                         │
 │  (24-DIP TreeEnd gutter — invisible, owns the "top level, at the end" drop slot)
 ├───────────────────────────────────────────────────────────────────────────┤  ← PanePad.Bottom 12
```

Left lane, exact (`Data/SidebarRowGeometry.cs`): `0..8` PanePad · `8..12` row inset (`IndentFor(0)=4`) · `12..15`
selection gutter (3) · `15..21` leading gap (6) · `21..53` art (32) · `53..59` gap (6) · `59..264` text ·
`264..272` row right inset (8) · `272..280` PanePad. Depth d adds `12·d` before the gutter (`IndentStep`, clamp 4).

Classic's document is fixed code (`Pane/SidebarBuiltInDocuments.cs:65-133`): **Pinned** (`Entities` + `ShowInRail`) ·
rule · **Your Library** (`Shortcuts` = Cozy, Artwork:false, Subtitles:false, CountBadges:**true**, items
`albums · artists · liked · podcasts · local` with icon overrides `Album/Contact/Heart/RadioTower/Folder`) · rule ·
**Playlists** (`Entities`) · [developer mode only] rule + a header-less **Tools** `StaticLinks` section (`Links with
{ShowInRail:false}`) carrying ~~the dev-tools route~~ (`api-console` — **DELETED, plan §9.6 Q7, 2026-09-12**; owner J
must either drop this link or repoint it to a surviving developer route when building the pane) with `IconOverride
"Code"`. Only "Your Library" carries counts —
`Entities.CountBadges` is false, so a playlist row's count lives in its subtitle ("50 songs") and a folder's in its own
("3 items"). The Pinned section is emitted even with ZERO pins so the quick-layout entry point is always reachable
(`Pane/SidebarBuiltInDocuments.cs:75-79`); the Tools divider is emitted WITH the section, never on its own.

### W1b — Classic/Curated: an editable `EntityList` header with `Display.InlineControls` @280

```
 │    My mixes                                            ⇅  ⌄     │  28  ⇅ = 24-DIP sort/view trigger (Icons.Sort 14,
 │  (Playlists) (Albums) (Artists) (Podcasts)                      │  28      Tok.TextSecondary, Interaction.Subtle,
 │    [▩32] Chill Mix                                              │  44      flyout BottomEdgeAlignedRight)
```

`Pane/SidebarPaneSlot.cs:225-240` mounts the chips as a second child of the header's band (`Direction 1, Gap 4`), so
the strip is HEADER CHROME, never a virtualized row. Chips (`Pane/SidebarPaneInlineControls.cs:33-77`): `Height =
SidebarRowGeometry.ChipHeight` 26, `Padding (10,0,10,0)`, `Radii.PillAll`, `Wrap = true`, `Gap 4`, strip padding
`(0,0,0,2)`; label 12 / weight 600-when-on; ON = `Tok.AccentDefault` + `Tok.TextOnAccentPrimary`, OFF =
`Tok.FillSubtleSecondary` + `Tok.TextSecondary`. The analytic extent the planner seeds is `ChipStripHeight` = 26 + 2
with a `ChipStripGap` of 4 — deliberately an approximation, because the strip WRAPS at a narrow pane and the measured
seam corrects it on realize (`Data/SidebarRowGeometry.cs:170-178`). Tapping the active chip clears back to "everything";
the last chip can never be toggled off into a query that matches nothing. Suppressed entirely when
`Config.ReadOnly` (Classic's locked document) or the section is collapsed.

### W2 — Classic, first paint / pending @280 (skeletons)

```
 │    Pinned                                                       ⧉  ⌄      │  28
 │    ░░░░  ░░░░░░░░░░░░░░░░                                                 │  44  SidebarSkeletons.Row: 32 tile r6,
 │          ░░░░░░░░                                                         │      bars 140×12 + 80×10 r4, pad (9,0,8,0), gap 10
 │    ░░░░  ░░░░░░░░░░░░░░░░                                                 │  44
 │    ░░░░  ░░░░░░░░░░░░░░░░                                                 │  44   ← exactly 3 rows (SkeletonRows)
 │    Your Library                                                 ⌄         │  28
 │    [♥] Liked Songs                                              ▭▭        │  40  count PENDING = 20×12 plate
```

Skeletons appear only while the section's source is `Pending` AND it has produced nothing yet
(`Data/SidebarRowPlanner.cs:472,483,513`). A warm list refreshing NEVER flashes a skeleton
(`SidebarProjectionBinder.cs:718`). Inside the row the two bars stack at gap **4**; the 10 is the row's own
art↔text gap (`Shared/SidebarSkeletons.cs:36-44`). The tile's radius is the shared cover ladder
(`SidebarCover.Radius(art,false)` — 4/6/8), not a literal.

### W3 — Classic, Playlists collapsed @280

```
 │    Playlists                                                    +  ⌄      │  28  chevron rotated back to 0° over 167 ms
 │  (no body rows planned at all — s.Collapsed returns before PlanBody)       │      SidebarRowPlanner.cs:325
 ├───────────────────────────────────────────────────────────────────────────┤
```

The collapse is **not** a clip-height reveal in the virtualized pane: the rows leave the PLAN. The choreography is the
`ItemsView` disclosure channel (`Pane/SidebarPane.cs:728-735`): the departing realized rows are kept alive while every
survivor below FLIPs upward, and the preference write is deferred to the settle (`WithPrefsCommit`, :1273).

### W4 — Classic, Pinned empty @280 · at rest and during a compatible drag

```
 at rest (56):                                    during a pin-eligible drag (72):
 │    Pinned                          ⧉  ⌄ │      │    Pinned                          ⧉  ⌄ │
 │  ┌───────────────────────────────────┐  │      │  ╔═══════════════════════════════════╗  │  dashed 4-on/2-off accent
 │  │ 📌  Drop items here to pin        │  │      │  ║ 📌  Drop items here to pin        ║  │  fill Tok.AccentSubtle
 │  │     Or use the pin action on any… │  │      │  ║     Or use the pin action on any… ║  │  ink AccentTextPrimary
 │  └───────────────────────────────────┘  │      │  ╚═══════════════════════════════════╝  │  56→72 ContentResize spring
```

`Shared/SidebarPinDropZone.cs:73-119`: plate spans the PanePad edge (margin 0, padding 12/0/12/0, `Radii.ControlAll`),
1-DIP `StrokeCardDefault` at rest → dashed `AccentDefault` (`BorderDashOn = Spacing.XS 4`, `Off = Spacing.XXS 2`) when
`compatible || hovering`; title 12 (weight 600 when active), hint 11 tertiary, single line ellipsised. Brush
cross-fade `MotionTok.ControlFaster` (83 ms), height on `MotionTok.ContentResize`.

### W5 — Rail (collapsed) @56 — Classic

```
 │  56  │
 ├──────┤  ← padding (0,8,0,12), AlignItems=Center, Gap 6
 │ ╭──╮ │  40×40 tile, corners 6 (Icon) — Home
 │ │⌂ │ │  glyph 16, TextSecondary; SELECTED tile: fill SelectedRest + glyph TextPrimary
 │ ╰──╯ │
 │ ╭──╮ │  40×40 — Search
 │ │🔍│ │
 │ ╰──╯ │
 │ ──── │  24×1 divider, TextTertiary @ A=0.30, margin 4/4
 │ ┌──┐ │  40×40 tile, corners 8 (Art) — pinned playlist cover, art edge 36 inset 2
 │ │▩ │ │  SELECTED: 2-DIP AccentDefault ring; armed drop: same ring + accent@0.35 wash over the cover
 │ └──┘ │
 │ ┌──┐ │
 │ │◯ │ │  artist → circular art
 │ └──┘ │
 │ ──── │
 │ ╭──╮ │  library shortcuts (glyph tiles), then up to 20 playlist-tree tiles (depth 0 only)
 │ │♥ │ │
 │ ╰──╯ │
 │ ╭──╮ │  folder tile → opens the side flyout (W6); tooltip = "Name · N items"
 │ │📁│ │
 │ ╰──╯ │
 │ ──── │
 │ ╭──╮ │  RailFooter: Classic's create "+" (box 40, glyph 16)
 │ │+ │ │
 │ ╰──╯ │
 │ ──── │
 │ ╭──╮ │  quick layout menu (box 40)
 │ │⧉ │ │
 │ ╰──╯ │
```

Caps (`Data/SidebarRowPlanner.cs:181-197` + `Pane/SidebarPane.cs:245`): 40 tiles total · Pinned 8 · JumpBackIn 4 ·
EntityList 20 · PlaylistTree 20. No virtualization (bounded by construction). Not mounted in the drawer
(`SidebarPane.cs:563`). The rail lives inside `ScrollView(...) with { Grow = 1, AutoEdgeFade = true,
SuppressScrollBar = true }` — the overlay bar would sit in the shell's resize-seam gutter and read as a page border
(`SidebarPane.cs:562-567`).

**Rail states and omissions the strip must reproduce** (`Pane/SidebarPaneRail.cs`):

| case | what the rail draws | source |
|---|---|---|
| nothing planned AND no binder yet | `SidebarSkeletons.RailStack(4)` — four 40×40 r8 `FillSubtleSecondary` tiles at gap 6 | `:28,60`, `Shared/SidebarSkeletons.cs:65-77` |
| an ACTION or TRACK shortcut | **omitted** — a 56-DIP strip cannot say what an unlabelled action would do; the expanded pane owns it | `:190` |
| a whole feed-shaped SECTION (`IconRow` on a non-shortcut kind) | ONE tile: Concerts → `Icons.Calendar`, navigates to the concerts hub · anything else (an extension contribution) → `Icons.Grid`, EXPANDS the pane | `:201-209` |
| a folder tile, right-clicked | the FULL folder menu (`Menus.SidebarEntry` + `SidebarFolderRows`), including **Expand folder** — the pane-expanding gesture the click used to be | `:154-171` |
| `Config.RailLayoutMenu == false` | no layout tile and no divider before it (V3 embeds the rows in its overflow menu instead) | `:71-75` |
| a playlist tile that is not `Kind == Playlist && CanEdit` | pure navigation, no drop target, and a drag crossing it stays **transparent** | `:129-135` |

### W6 — Rail folder flyout @300, anchored `RightEdgeAlignedTop` to the tile

```
 ┌────────────────────────────────────────────────┐  300 wide, padding 4 all round, ClipToBounds
 │ [‹28]  Road trip                               │  header ≥40, gap 4; back chevron only above level 1
 │        3 items                                 │  BodyStrong + Caption(secondary)
 │ ┌────────────────────────────────────────────┐ │
 │ │ [▩32] Desert                               │ │  SidebarEntityRow.Create, Density Cozy (44), art 32
 │ │       12 songs                             │ │  Subtitle = SidebarPaneText.SubtitleOf
 │ │ [▩32] Coast                      …         │ │  hover "…" = the same row menu the pane builds
 │ │       31 songs                             │ │
 │ │ [📁32] Sub-folder                 ›        │ │  folder → trailing ChevronRight 12, click drills in
 │ │       2 items                              │ │  (page-slide forward; back mirrors)
 │ └────────────────────────────────────────────┘ │  ScrollEl ContentSized, MaxHeight 420, row gap 1
 └────────────────────────────────────────────────┘
```

`Pane/SidebarRailFolderFlyout.cs:61-230`. Keys ↑/↓ rove (wrapping), Enter activates, → drills, ←/Backspace goes back
(swallowed at the root), Escape is the popup's own light dismiss (:276-307).

### W7 — Wavee Curated, loaded @320

```
 │  ▌ [⌂] Home                                                     │  44   (the Shortcuts band plans no header row)
 │                                                                 │   8
 │    Pinned                                               ⧉  ⌄    │  28
 │    [▩32] Chill Mix                                        📌    │  44
 │          50 songs                                               │
 │  ────────────────────────────────────────────────────           │  16   authored Divider
 │    Jump back in                                         ⌄       │  28
 │  ┌──────────────┐ ┌──────────────┐                              │       GridStrip: 2 cols @ 320
 │  │ ▩▩▩▩▩▩▩▩ 140│ │ ▩▩▩▩▩▩▩▩ 140│                              │       cell edge = min(160,(304−8)/2)=148
 │  │ ▩▩▩▩▩▩▩▩    │ │ ▩▩▩▩▩▩▩▩    │                              │ ~186  art = edge − 8 = 140
 │  │ Deep Focus   │ │ Road trip    │                              │       label 12/1 line, sub 11 tertiary
 │  └──────────────┘ └──────────────┘                              │       card: Radii.Card, Elevation.Card, pad 4, gap 4
 │    New releases                                         ⌄       │  28
 │    [▩32] Ekhidna                                        2d      │  44   age badge 11 tertiary
 │          Album · Ryuichi Sakamoto                               │
 │    Concerts near you                                    ⌄       │  28
 │  ┌───────────────────────────────────────────────────┐          │  48   PromptRow (no reason line)
 │  │ [📅28]  Set your location to see concerts      ›   │          │       12/600 + ChevronRight 10, Elevation.Card
 │  └───────────────────────────────────────────────────┘          │
 │    Playlists                                            +  ⌄    │  28
 │    … tree …                                                     │
```

### W8 — Curated, authored-empty pane @320

```
 ├─────────────────────────────────────────────────────────────────┤
 │                                                                 │
 │                          [⧉ 24]                                 │  Icons.SplitView, TextTertiary
 │                   Your sidebar is empty                         │  14/600 TextSecondary, ≤2 lines
 │             Add a section, or start from a template.            │  12 TextTertiary, ≤3 lines
 │                  ┌───────────────────────┐                      │  32 accent CTA, radius Control, pad 12
 │                  │  Customize sidebar…   │                      │  13/600 TextOnAccentPrimary
 │                  └───────────────────────┘                      │
 │                                                                 │
 ├─────────────────────────────────────────────────────────────────┤
```

`Pane/SidebarPane.cs:741-778`, centred, gap 8, padding (16,24,16,24). Under a LOCKED document (Classic) the CTA is
ABSENT, not disabled.

### W9 — Curated, customize canvas @320 (route == `sidebar-customize`)

```
 │  ┌─────────────────────────────────────────────────────────┐    │  44 card (plate inset 2 top/bottom)
 │  │      [▤24] Shortcuts                          1     ⌄   │    │     pinned head: NO grip, NO eye, NO "…"
 │  └─────────────────────────────────────────────────────────┘    │
 │  ┌─────────────────────────────────────────────────────────┐    │  44
 │  │ ⣿12  [📌24] Pinned                         👁  …   ⌄     │    │     grip 12 · kind tile 24 (r Control) ·
 │  └─────────────────────────────────────────────────────────┘    │     title 13/600 · count · eye 24 · "…" 24 · chevron 10
 │  ┌─────────────────────────────────────────────────────────┐    │  44
 │  │ ⣿   [▦24] Jump back in                4    👁  …   ⌄     │    │
 │  └─────────────────────────────────────────────────────────┘    │
 │  ┌─────────────────────────────────────────────────────────┐    │  44   HIDDEN: opacity 0.55, "Hidden" pill
 │  │ ⣿   [▥24] New releases            Hidden  👁  …   ⌄     │    │       (10/600 on FillSubtleSecondary, r Full)
 │  └─────────────────────────────────────────────────────────┘    │
```

Card plate: `Fill = open ? SelectedRest : FillCardDefault`, hover/pressed follow the same ladder, border 1 =
`open ? AccentSubtle : StrokeCardDefault`, `BrushTransitionMs = Motion.ControlFaster`
(`Pane/SidebarPaneEditCard.cs:122-145`).

### W10 — Curated canvas, one card expanded + options popover

```
 │  ┌─────────────────────────────────────────────────────────┐    │  the expanded card wears the SELECTED plate
 │  │ ⣿   [📌24] Pinned                         👁  …   ⌃     │    │  chevron 180°
 │  └─────────────────────────────────────────────────────────┘    │
 │    [▩32] Chill Mix                                    50        │  44  ← the section's REAL rows, same planners
 │    [◯32] Hans Zimmer                                            │  44
 │  ┌─────────────────────────────────────────────────────────┐    │  (the section-card drag band is DISARMED while
 │  │ ⣿   [▦24] Jump back in                4    👁  …   ⌄     │    │   any card is expanded — SectionsReorderable)
 │  └─────────────────────────────────────────────────────────┘    │
                                                   ┌──────────────────────────────┐  320×520 popover,
                                                   │ Section options              │  RightEdgeAlignedTop,
                                                   │ …SidebarPropertyPanel rows…  │  light dismiss, focus trap
                                                   └──────────────────────────────┘  (ch. 26 owns the panel)
```

### W11 — Library V3, list view @300 (inline search)

```
 │ x=0  8   13    21                                      284 292 300
 ├──────────────────────────────────────────────────────────────────┤  chrome padding (0,8,0,0)
 │   Liked Songs 128   Albums   Artists   Podcasts   Local fil⋯     │  30  DestinationRail: 13.5 px words, gap 14,
 │   ▔▔▔▔▔▔▔▔▔▔▔                                          ◄fade►   │      2-DIP accent underline on the active word,
 │                                                                  │      count on the ACTIVE word only, 20-DIP edge fade
 │  ▌ [⌂] Home                                                      │  40  nav rows (prefs.TopBar)
 │  ──────────────────────────────────────────────────────────      │   1 + 4/4 margins
 │   [▤32] Your Library                        [+28] [⋯28] [‹28]    │  44  title 15/600; buttons ControlSize.Small
 │   [🔍  Search in Your Library          ✕ ]        Recents ⇅│≡     │  36  inline field (pane ≥ 300) + full sort pill
 │   (Playlists) (Podcasts) (Albums) (Artists)                      │  40  28-DIP pills, gap 6, horizontal scroll
 │  ──────────────────────────────────────────────────────────      │   1 + 4/4
 ├──────────────────────────────────────────────────────────────────┤  ← the padded list starts here (PanePad 8/8/8/12)
 │    [▩32] Chill Mix                                    📌         │  44  Cozy+subtitles = 44 (View = List)
 │          50 songs                                                │
 │    [📂32] Road trip                                        ⌄     │  44  folder (wide pane ⇒ inline disclosure);
 │          3 items                                                 │      chevron is TRAILING, never leading
 │    [◯32] Hans Zimmer                                             │  44
 │          Artist                                                  │
```

V3 renders NO section headers (every V3 section is title-less — `Modes/LibraryV3/LibraryV3Document.cs:70,92,114`), so
the list is one continuous band: pin band, then the library.

**The pin band's visibility is a rule, not a presence test**: `PinsBandVisible = (HasPins || DragInFlight) && !Drilled
&& !Searching` (`LibraryV3Document.cs:297`). With zero pins the band is ABSENT — V3's chrome does not budget for the
56-DIP drop-zone card — until a Wavee **resource drag** starts anywhere in the app, at which point the band (and its
drop zone) appears for the duration (`LibraryV3Chrome.cs:52-59` is the ONE `UseDragState()` that feeds it, written in
a layout effect keyed on the drag EDGE). Drilling in or typing a query hides it again.

**The four V3 views are four row shapes**, all from the ONE ladder (`LibraryV3Document.cs:164-176`):

| view | density | subtitle | row h | art | grid min cell |
|---|---|---|---|---|---|
| `List` | Cozy | yes | 44 | 32 | — |
| `CompactList` | **Compact** | no (Compact suppresses them outright) | **32** | **20** | — |
| `Grid` | Cozy | yes | measured | — | **116** |
| `CompactGrid` | Cozy | no | measured | — | **84** |

`LibraryV3Metrics.MinCellWidth` (`:79`) is what makes CompactGrid denser — the cell EDGE is still derived, never
chosen, which is why the sort/view flyout has no S/M/L row and the persisted `V3GridSize` is deliberately unread.

### W12 — Library V3 @240 (narrow: closed search, icon-only sort, drilled in)

```
 │   Liked Son⋯  Albums  Artists ◄►      │  30  pager chevrons (20 dia, glyph 10) on hover, hidden where nothing to reach
 │  ▌ [⌂] Home                           │  40
 │  ─────────────────────────────────    │
 │   [▤] Your Library      [+] [⋯] [‹]   │  44
 │   [🔍32]                    [≡28]     │  36  closed magnifier 32 · spacer · icon-only sort pill 28
 │   (✕28)(Playlists)(By you)(By Spot⋯   │  40  filtered: leading ✕ + selected facet + spilled qualifier options
 │  ─────────────────────────────────    │
 │   [‹24] Road trip                     │  32  breadcrumb (Drilled): back 24 + level name 12/600 TextSecondary
 ├───────────────────────────────────────┤
 │    [▩32] Desert                       │  44  the folder's DIRECT children, flattened to depth 0
 │          12 songs                     │
```

Thresholds: search inline ⟺ pane ≥ **300** (`LibraryV3SearchRules.cs:29`); sort pill icon-only when the field is
expanded OR pane < **280** (`:44`); folders DRILL (not disclose) when pane < **320** or in the drawer
(`LibraryV3Metrics.cs:74`, `LibraryV3Sidebar.cs:493`). Open-field width = `paneW − (21 + 16) − 28 − 4`
(`LibraryV3SearchRules.cs:63`) — at 240 that is 171.

### W13 — Library V3, grid view @380

```
 ├──────────────────────────────────────────────────────────────────────────┤
 │  ┌───────────┐ ┌───────────┐ ┌───────────┐                               │  cols = floor((364+8)/(116+8)) = 3
 │  │  ▩ 108    │ │  ▩ 108    │ │  ▩ 108    │                               │  edge = min(160,(364−16)/3) = 116
 │  │ Chill Mix │ │ Road trip │ │ Deep Focus│                               │  art = edge − 8 = 108 · cell h ≈ 154
 │  │ 50 songs  │ │ 3 items   │ │ 88 songs  │                               │  label 12 · sub 11 (Grid only)
 │  └───────────┘ └───────────┘ └───────────┘                               │  cell pad 4 / inner gap 4 · strip gap 8, bottom 8
```

Column count is the PLANNER's (`GridColumns`, clamped [2,4] — `LibraryV3Document.cs:248`), derived from the pane width
by `LibraryV3Metrics.Columns` (`:84`); the slot may only WRAP fewer of the planner's cells per visual line when a cell
would fall under the 40-DIP floor (`SidebarRowGeometry.GridFallbackColumns`, `:310`). It must never re-derive the count.

### W14 — V3 chip rail: the three shapes

```
 idle       │ (Playlists) (Podcasts) (Albums) (Artists)                    │ 28-DIP pills, pad 12/12, gap 6, r Full
 filtered   │ (✕) [Playlists] (By you) (By Spotify) (Mixed)                │ ✕ 28 circle (Interaction.Control)
 fused      │ (✕) [✓ Playlists │ By you ✕]                                 │ ConcertUi.SegmentedPill, Sidebar register
```

Unselected pill: `FillControlDefault` + 1-DIP `StrokeControlDefault`, label 13 (facet) / 12 (option) weight 400
`TextPrimary`. Selected: `AccentDefault` fill + border, `Tok.OnAccent` ink, weight 600 — **same padding either way**, so
selecting never reflows the label (`Modes/LibraryV3/LibraryV3Chips.cs:265-302`). The qualifier row exists only under
Playlists AND only when the data evidences ≥2 provenance flavours (`LibraryV3ChipStrip.cs:73`).

### W15 — V3 degraded states @300

```
 search empty              │ filter empty                │ library empty            │ failure WITH rows
 ─────────────────────────────────────────────────────────────────────────────────────────────────────────
 No results for "jaz…"     │ No playlists in Your Library │ Nothing in Your Library │ ┌──────────────────┐
 Try a different spelling, │                              │ yet                     │ │⚠ Something went  │
 or clear the filter.      │                              │ Save an album, follow…  │ │  wrong    Retry  │
 [ Clear search ]          │ [ Clear filter ]             │ [ Create playlist ]     │ └──────────────────┘
 (EmptyState.Compact)      │ (EmptyState.Compact)         │ (EmptyState.Compact)    │  banner 12-ink card,
                           │                              │                         │  margin 8/0/8/4, pad 8/6
```

Priority order and the rule that loaded content is never blanked: `Modes/LibraryV3/LibraryV3Chrome.cs:101-113,162-197`.
A failure WITH rows present is the one-line banner; only a failure with nothing to show takes the pane. A **pending**
library shows the pane's own skeleton rows, never an empty state. The gate is exact: banner iff
`load == Failed && rows > 0`; an empty state iff `rows == 0 && !anyContributingKindPending && load != Pending`
(`:105-112`) — `rows` counts the pin band too.

Three more facts the four columns above do not show:

* **failure with NOTHING to show is not an `EmptyState.Compact`** — it is the shared full `ErrorState.Build(entries.Error,
  Retry)` (`:165-167`). Only the search/filter/library-empty arms are Compact.
* **the echoed query is truncated at 24 characters + "…"** before it is formatted into "No results for …" (`:171-172`),
  so a pasted paragraph cannot blow out a 240-DIP pane.
* **two self-correcting states**: a query RESETS any drill level (a search flattens the tree, so there is no folder to
  be inside of — `:79-82`), and a drilled-into folder that vanished from the projection POPS the stack rather than
  showing a breadcrumb pointing at nothing (`:69-73`). Both are layout effects, never render-body writes.

### W16 — Tree drag cues (the five outcomes) @280

```
 Before (own depth)        │    ●━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━      │ caret at row TOP, x = TreeContentX(d)
 │    [▩32] Desert                                                           │
 ─────────────────────────────────────────────────────────────────────────────
 Into (folder / editable   │ ╔═════════════════════════════════════════════╗ │ plate accent@0.18 + 1-DIP accent border,
 playlist centre)          │ ║  [📂32] Road trip                      ⌄    ║ │ UNDER the row (text never tinted), r 4
 ─────────────────────────────────────────────────────────────────────────────
 After, same depth         │    [▩32] Coast                                  │
                           │    ●━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━      │ caret at row BOTTOM (h − 2)
 ─────────────────────────────────────────────────────────────────────────────
 After, OUTDENT (pointer   │    │  [▩32] Coast                               │ caret jumps LEFT one connector cell
 travels left one cell)    │ ●━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━        │ chip says "Move out of Road trip"
 ─────────────────────────────────────────────────────────────────────────────
 EndOfList (TreeEnd, 24)   │ ●━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━        │ chip says "Move to end"
 ─────────────────────────────────────────────────────────────────────────────
 REFUSED                   │  (no line, no plate — the chip carries the sentence)
```

Bands: edge = `clamp(0.30·h, 10, 16)`, capped at h/2 (`Data/RootlistSlotResolver.cs:118-139`) — at h 44 that is 13.2,
so Before ≤ 0.30t, Into 0.30–0.70t, After ≥ 0.70t on a folder or a depositable playlist; a row with no centre splits
50/50. Depth is read from the pointer's X against `TreeContentX` with **4-DIP hysteresis** (`:125,:236-263`). Line: 2
DIP, radius 1, 6-DIP terminal dot, width `contentWidth − TreeContentX(depth) − 8` (`SidebarDropCue`, `:272-303`).

### W17 — Multi-selection (PlaylistTree) @280

```
 │    [☑24][▩32] Desert                                         │  44  check lane slides in from −28, leftMargin 0
 │          12 songs                                            │      row plate = FillSubtleSecondary (quiet), NOT accent
 │    [☑24][▩32] Coast                                          │  44
 │    [☐24][▩32] Deep Focus                                     │  44
        ↑ lane visible on every tree row at once (a shape change, so the whole tree re-skins)
```

Enter check mode: row menu ▸ **Select** (`Strings.Sidebar.Select`). Ctrl-click toggles, Shift-click ranges over the
VISIBLE order, plain click clears + activates, Escape clears (`Pane/SidebarPane.cs:377-402`,
`Pane/SidebarPaneSlot.cs:1188-1208`). Dragging a row that is IN the selection lifts the whole selection; dragging one
outside lifts only itself (`SidebarPane.cs:411`).

### W18 — One Cozy row, every lane (zoom, @280)

```
      0    8   12  15   21                   53  59                       238  264 272 280
      │    │   │   │    │                     │   │                         │    │   │   │
      │PanePad│ins│gut│gap│      art 32       │gap│ title / subtitle        │ovf │pad│Pad│
                  ▌▌▌                                                        (26)
  y:  0 ─────────────────────────────────────────────────────────────────────────────────  row top
      │            ╭──────────╮                Chill Mix            (14/20 Body, 1 line, CharacterEllipsis)
     14│  pill top │          │                50 songs             (12/16 Caption, TextSecondary, 1 line)
     30│  pill end ╰──────────╯                                   ♪ (equalizer 12, AccentDefault) 📌 (pin 12) 50 (11)
  y: 44 ─────────────────────────────────────────────────────────────────────────────────  row bottom
```

Pill: 3×16, `Margin = (IndentFor(depth), (h−16)/2, 0, 0)` → top 14 at h 44, radius 1.5, `Tok.AccentDefault`,
`TransformOriginY = 0` (`Shared/SidebarSelectionPill.cs:65-78`). Row corners 4, row padding
`(IndentFor(depth), 0, 8, 0)`, gap 6 (`Shared/SidebarEntityRow.cs:505-507`). The "…" overflow is a **ZStack overlay**
(`JustifySelf=End`), so it costs the title zero width and hovering never re-trims the label (`:669-685`); the trailing
cluster reserves 26 DIP only when the row actually carries it (`:448`).

### W19 — Row states

```
 rest        │    [▩] Chill Mix                       │  Fill transparent
 hover       │ ░░░[▩] Chill Mix ░░░░░░░░░░░░░░░ …░░░  │  FillSubtleSecondary, "…" fades in (HoverOpacity)
 pressed     │ ▒▒▒[▩] Chill Mix ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  │  FillSubtleTertiary
 selected    │ ▌██[▩] Chill Mix ██████████████████████ │  AccentSubtle + 3×16 pill
 sel+hover   │ ▌▓▓[▩] Chill Mix ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ │  SelectedHover (strictly stronger than rest)
 now playing │    [▩] Chill Mix                    ♪▮▮ │  equalizer 12 AccentDefault, animated iff IsPlaying
 disabled    │    [▩] Unavailable row                 │  Opacity 0.55, IsEnabled false, tooltip = the reason
 track row   │    [▶] Song title                      │  cover gains a 55 %-black scrim + white ▶ on ROW hover
```

### W20 — Narrow drawer (viewport ≤ 720) @ drawer width

```
 ┌──────────────────────────────────┐╳╳╳╳╳╳╳╳╳╳╳╳╳╳  scrim
 │  (the SAME SidebarHost, inDrawer)│╳  The drawer always renders the EXPANDED pane (compact is ignored),
 │  … identical rows …              │╳  the 56-DIP rail layer is NOT mounted, and the scroll key gets
 │                                  │╳  ".drawer" appended so the two mounts never share an offset.
 └──────────────────────────────────┘╳  WaveeShell.cs:2345 · SidebarPane.cs:479,564,717
```

### W21 — Design chooser / Settings picker (three cards)

```
 ┌─────────────────────────────────────────────────────────────────────┐  plate = 3·224 + 2·12 + 2·24 = 744,
 │  Choose your sidebar                                                │  clamped to [300, viewport−32]; radius Overlay,
 │  Pick the left-hand navigation that suits you. You can change this  │  Elevation.Dialog, FillSolidBase
 │  any time in Settings → General.                                    │
 │  ┌───────────┐  ┌───────────┐  ┌───────────┐                        │  cards 224 wide (compact 200); HEIGHT IS
 │  │ ▬▬▬▬▬     │  │ ▭▭ ▭ ▭▭   │  │ ▭▭▭ ▭▭▭   │                        │  CONTENT-SIZED (WaveePicker.Pane/.PaneCompact
 │  │ ▬▬▬       │  │ ▭▭        │  │ ───────   │                        │  Height = NaN, Inset 10, Gap 7)
 │  │ ───────   │  │ ▩ ▬▬▬     │  │ ▩▬ ▩▬     │                        │  preview 116 (compact 96), pad (8,7,8,0),
 │  │           │  │           │  │           │                        │  radius 6; fill AccentSubtle when selected,
 │  │           │  │           │  │           │                        │  else FillLayerDefault; border 1 accent/card
 │  │ ───────   │  │ ▩ ▬▬▬     │  │ ▩▬ ▩▬     │                        │
 │  │ ▩ ▬▬▬     │  │ ▩ ▬▬▬     │  │ ▬▬▬       │                        │  miniature rows: art edge 11 (compact 10)
 │  │ ▩ ▬▬▬     │  │ ▩ ▬▬▬     │  │ ▩ ▬▬▬     │                        │
 │  ├───────────┤  ├───────────┤  ├───────────┤                        │
 │  │Spotify    │  │LibraryV3  │  │Custom     │                        │  title 13 (compact 12) + "Active" pill
 │  │Classic ●  │  │           │  │           │                        │  (Spacing.L tall, radius Pill, 9.5/600)
 │  │The classic│  │The latest │  │Build your │                        │  subtitle 11 tertiary, 2 lines
 │  └───────────┘  └───────────┘  └───────────┘                        │
 ├─────────────────────────────────────────────────────────────────────┤  1-DIP StrokeCardDefault
 │                              [ Not now ]  [ Use this layout ]       │  pad 24, buttons 32 tall, 120/130 min
 └─────────────────────────────────────────────────────────────────────┘
```

`SidebarDesignPicker.cs:226-436`. The third card is currently **withheld** (`allowCustom: false`), not disabled:
`count = allowCustom ? 3 : 2` (`:159`). Confirming Curated swaps the command row in place for a two-line explanation
plus `[ Maybe later ] [ Customize now ]` (`:523`).

**The picker applies LIVE.** Clicking a card writes the design immediately, so the pane BEHIND the scrim visibly
changes under the dialog — that is the whole reason the choice is made here rather than against a static illustration
(`:490-493`). "Not now" and "Use this layout" therefore both keep whatever is applied; neither writes a design.

Two more forms of the same card, both fed by the same `Preview`/`StatusTag`/loc-key lookups (`:171-223, 415-434`):

| form | shell | preview | where |
|---|---|---|---|
| `Card` (above) | `WaveePicker.Pane` 224 / `PaneCompact` 200, content-height | 116 / 96 stacked over title over subtitle | the chooser + Settings |
| `RowCard` (`Variant.Rows`) | `WaveePicker.WideRow` **480 × 100**, inset 8 | a **120 × 84** thumbnail (`Metrics.Thumb`) BESIDE a title(+pill)/subtitle column | the setup wizard's radio list |
| `Metrics.Stage` | — | a **112-wide** live-preview pane, non-compact row counts, no title/sub/tag | `SetupSidebarPage.SidebarStageView` |

### W22 — V3 destination word rail, scrolled

```
 │ ⟨20⟩ Songs  Albums  Artists  Podcasts  Local fil⟩20⟨              │ 30 tall; words 13.5, gap 14; first word at x 21
 │      ▔▔▔▔▔                                                        │ active: weight 600 TextPrimary + 2-DIP underline
        ↑ pager chevrons appear on rail hover, hidden outright where there is nothing to reach
```

Fade mask is derived per render from live scroll geometry (`OnScrollGeometryChanged`, 2-bit edge key —
`Modes/LibraryV3/LibraryV3NavBand.cs:237-243`); a click scrolls 0.8 × the live viewport through
`ScrollIntoView.ScrollTo` (`:312-324`). Labels NEVER drop to glyphs — the rail scrolls instead. The count rides the
active word ONLY, from `LibraryStore.Stats` and only once `State == Ready` (`:330-336`) — no pending plate here; and
only four of the five destinations are countable (Local files never carries one).

### W23 — The seam: resist, fade, snap (the sidebar's own width gesture)

```
 460 ────────────────────────── Max (ShellResponsiveLayout.NavPaneMaxW)
  ⋮   full opacity
 240 ────────────────────────── FadeStart — the pane's CONTENT begins to dim from here down
  ⋮   ░ fading over FadeDistance 64   (the shell's _sidebarFade signal → the opacity wrapper, compositor-only)
 190 ────────────────────────── ReExpand — dragging back up past here re-opens the pane
 180 ────────────────────────── Min (NavPaneMinW, issue #84 lowered it from 240)
 176 ────────────────────────── ForcePush 64 past FadeStart ⇒ COLLAPSE to the 56-DIP rail
```

`WaveeShell.cs:1228-1237` — `Splitter.Create(width, commit, { Min 180, Max 460, CompactWidth 56, FadeStart 240,
FadeDistance 64, ForcePush 64, ReExpand 190, ShowIndicator false }, collapsed: _sidebarCompact, fade: _sidebarFade,
dragging: _sidebarDragging)`. The ~14-DIP band between 176 and 190 is the collapse hysteresis; a rebuild that drops
the fade/resist ladder turns a graded, reversible gesture into a hard snap. During the drag the suppression arbiter
snaps `SidebarPaneAnim` 1:1, so the pane tracks the cursor exactly (`WaveeShell.cs:1019-1023`). A COMMITTED drag pins
the width for that design (`WidthUserSet`) and the responsive tier ladder stops applying to it.

**The responsive ladder itself** (`Features/Shell/ShellResponsiveLayout.cs:172-223`), which the wireframe widths above
all sit inside:

| threshold | value | note |
|---|---|---|
| mid tier enters | viewport ≥ **1400** | Classic 280 · Curated 320 · V3 340 |
| wide tier enters | viewport ≥ **1800** | Classic 320 · Curated 360 · V3 380 |
| below both | — | Classic 240 · Curated 280 · V3 300 |
| tier SHRINK hysteresis | **24 DIP** | widen immediately; the mid tier holds down to 1376 (`NavPaneDefaultFor`) |
| narrow shell (drawer) | enter ≤ **720**, leave < **760** | `NarrowFor` — a hysteretic band, not one threshold |
| drawer width | `min(max(240, preferredWidth), viewport − 32)` | `DrawerMinW` 240, `DrawerViewportInset` 32 |
| pane clamp | **[180, 460]** | every writer (seam, probe, responsive default) clamps through this one pair |

### W24 — "Move to folder…" destination picker (`ContentDialog`, modal)

The **third non-mouse route into the rootlist**, beside `Move up` / `Move down` and `Alt+↑` / `Alt+↓`
(`RootlistFolderPicker.cs:14-30`). Move up/down walk one sibling and "Move out of {parent}" climbs exactly one level,
so filing a playlist into a folder that is not adjacent to it was a DRAG and nothing else — across a scrolling pane,
possibly into a collapsed folder. It is hosted in a `ContentDialog`, **not an anchored flyout**, because the context
menu that launched it is gone by invoke time and there is no anchor node left to place against (`:28-30`).

```
 ┌────────────────────────────────────────┐  card 320 wide: the picker never sets DialogWidth and shows ONE
 │                                        │  button, so cardW = clamp(320, [320,548]) = ContentDialog MinW
 │  Move to folder                        │  (ContentDialog.cs:283,110) — r8 OverlayAll · 1-DIP
 │                                        │  StrokeSurfaceDefault · Elevation.Dialog · MinHeight 184 ·
 │  Choose where “Chill Mix” should live. │  content region pad 24, Fill FillLayerAlt · page smoke #4D000000
 │                                        │  title 20/600 TextPrimary + 12 below (ContentDialogTitleMargin);
 │  ┌──────────────────────────────────┐  │  NOTE: the dialog title has NO ellipsis ("Move to folder") — the
 │  │ Find a folder                    │  │  MENU row does ("Move to folder…", loc `menu.moveToFolder`)
 │  └──────────────────────────────────┘  │  body 13 TextSecondary, MaxLines 2, CharacterEllipsis, and it
 │                                        │  names the SUBJECT: one row by name, a selection by "{n} items"
 │   [≡18] Top level                      │  40  EditableText 300 × 32, placeholder "Find a folder" — a LIVE
 │   [📁18] Road trip                     │  40      filter over the frozen list, not a re-query
 │     [📁18] Summer                      │  40  panel column 320, gap 4 (Spacing.XS) between body/field/list
 │       [📁18] 2019                      │  40  row: pad-left 6 + 12·depth · gap 10 · icon 18 TextSecondary
 │   [📁18] Deep work                     │  40      (Icons.List for Top level, Icons.Folder otherwise) ·
 │                                        │      label 14 TextPrimary, 1 line, CharacterEllipsis · r4 ·
 │                                        │      Role = Button · Interaction.Subtle
 │ ────────────────────────────────────── │  list = ScrollEl ContentSized, MaxHeight 360, EdgeCues.None,
 │                              [ Cancel ]│      row gap 2; separator 1-DIP StrokeCardDefault
 └────────────────────────────────────────┘  command row pad 24: ONE button, pushed right by a Grow spacer,
                                             min 130 × 32; DefaultButton = Close, initial focus lands on it
```

**The filtered-empty arm** (the only empty state this dialog has):

```
 │  ┌──────────────────────────────────┐  │
 │  │ zzz                              │  │  a query matching no folder…
 │  └──────────────────────────────────┘  │
 │   No folders yet                       │  44-DIP line, pad (8,0,8,0), 13 TextSecondary
```

Reachable **only** when the `Top level` row is absent — it is pinned first and is NEVER filtered by the query, because
a user who typed a folder name and changed their mind would otherwise lose the un-nest (`RootlistFolderPicker.cs:130-134`,
`:145-149`). `Top level` is itself conditional: `RootlistTreeNav.TryTopLevelAnchor` is false when the tree is empty or
the source already IS the last top-level entry (`Data/RootlistTreeNav.cs:120,175-183`).

**There is no "nothing to move into" arm**: `Open` returns in silence when the selection normalises to nothing or the
destination list is empty (`RootlistFolderPicker.cs:48,58`), and the menu row that reaches it is ABSENT in that case —
`RootlistTreeNav.HasDestinations` is the allocation-free question the menu asks first, precisely so a verb never opens
an empty picker (`Data/RootlistTreeNav.cs:130-147`). An empty dialog is a worse answer than a missing verb.

Rows and legality (all one authority, asked once):

| | rule | source |
|---|---|---|
| what the list contains | `Top level` (when legal) first, then every folder the WHOLE selection may be filed into, **in tree order**, indented by its real depth at 12/level — the pane's own `IndentStep`, so the picker reads as the shape on screen | `Data/RootlistTreeNav.cs:112-122,161-173`; `RootlistFolderPicker.cs:115,174` |
| legality | `RootlistDropDecision.Check` over the store's marker stream — the SAME authority the drop cue refuses with, so the picker cannot offer a destination a DRAG would refuse (the source's own subtree, and the folder it is already the last child of, both drop out) | `Data/RootlistTreeNav.cs:154-173` |
| the list is a SNAPSHOT | frozen at open (props freeze at mount; the projection is not a signal this panel subscribes to). The COMMIT re-reads the LIVE tree, so a mid-flight desktop rootlist change resolves to nothing rather than to the wrong folder | `RootlistFolderPicker.cs:49-58,83-88` |
| commit | `FolderActions.Commit` → `WaveeResourceDrop.MoveRootlist` — **the call a DROP makes**: one batch whatever the selection size, failure mapped by `PlaylistEditVerb.Reorder`, announce + toast + Undo | `Actions/FolderActions.cs:220-244`, `Features/DragDrop/WaveeResourceDrag.cs:452-473` |
| the `Top level` commit | `TryTopLevelAnchor` → the LAST top-level entry, placed `After` with an exclusive end, so it lands after a trailing FOLDER instead of inside it — the same anchor the tree-end drop slot uses | `RootlistFolderPicker.cs:89-96` |
| what the toast says | `Moved to {name}` · `Moved {n} items to {name}` · and for `Top level` (destination name `""`) **`Moved to Your Library`**, never an empty name | `Features/DragDrop/WaveeResourceDrag.cs:457-461` |

---

## 3. Tokens

| element | size (DIP) | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| pane column (expanded) | width = `prefs.Width` ∈ [180,460] | — | 0 | — | **no fill** (paint-site omission over window Mica) | Mica base | `WaveeShell.cs:1004-1015` |
| pane column (rail) | 56 | — | 0 | — | no fill | Mica base | `ShellResponsiveLayout.cs:11` |
| padded list | — | `(8,8,8,12)` | — | — | — | — | `Pane/SidebarPaneMetrics.cs:25` |
| row (Cozy+sub) | h 44 | pad `(4+12·d, 0, 8, 0)`, gap 6 | 4 | Body 14/20 + Caption 12/16 | rest transparent · hover `FillSubtleSecondary` · press `FillSubtleTertiary` | none | `Shared/SidebarEntityRow.cs:494-519` |
| row (Cozy, no sub) | h 40 | as above | 4 | Body 14/20 | as above | none | `Data/SidebarRowGeometry.cs:61` |
| row (Compact) | h 32, art 20 | as above | 4 | Body 14 (no subtitle) | as above | none | `:59`, `:122` |
| row (Comfortable) | h 44 / 48, art 40 | as above | 4 | Body + Caption | as above | none | `:60`, `:124` |
| row selected | — | — | 4 | Body (weight unchanged) | `SelectedRest`=`AccentSubtle` / `SelectedHover` / `SelectedPressed` | none | `Design/WaveeTokens.cs:272` |
| row disabled | — | — | — | — | transparent, `Opacity 0.55` | — | `Shared/SidebarEntityRow.cs:523` |
| selection pill | 3×16 | margin `(IndentFor(d), (h−16)/2,0,0)` | 1.5 | — | `Tok.AccentDefault` | — | `Shared/SidebarSelectionPill.cs:28,65` |
| leading art | 20/28/32/40/48/64 | gap 6 after gutter | 4 (≤28) / 6 (≤40) / 8 (>40); circular = h/2 | — | `Surfaces.Artwork` (decode bucket 64/128/256) | — | `Shared/SidebarCover.cs:26-42,106` |
| glyph tile | = art size | centred | same ladder | glyph 16 (≥28) / 12 | `Tok.FillSubtleSecondary` + `Tok.TextSecondary` | — | `Shared/SidebarCover.cs:114` |
| bare glyph (no art) | art-wide box, glyph 16 | — | — | — | `selected ? TextPrimary : TextSecondary` | — | `Shared/SidebarEntityRow.cs:386` |
| section header | h 28 (+8 above, +2 below) | `RowInset (4,0,8,0)`, gap 4 | 4 | 12 / weight 600 | `Tok.TextSecondary`; hover `FillSubtleSecondary` | — | `Shared/SidebarSectionHeader.cs:123-136` |
| header chevron | 10 | — | — | — | `Tok.TextTertiary` | — | `Shared/SidebarChevron.cs:41` |
| header actions | 24 box (rail 40, folder 20) | — | Control | glyph 14 (rail 16, folder 12) | `Tok.TextSecondary`, `Interaction.Subtle` | — | `Shared/SidebarPinDropZone.cs:191-203` |
| header title (long) | — | — | — | 12/600, `Shrink 1`, `MinWidth 0`, `MaxLines 1`, `CharacterEllipsis` | — | — | `Shared/SidebarSectionHeader.cs:104-108` (#84 — the one row family that used to overflow) |
| header sort/view trigger | 24 box | — | Control | glyph 14 (`Icons.Sort`) | `Tok.TextSecondary`, `Interaction.Subtle` | — | `Pane/SidebarPaneInlineControls.cs:159-170` |
| header filter chip | 26 | strip `(0,0,0,2)`, `Wrap`, gap 4; chip pad `(10,0,10,0)` | Pill | 12 (600 when on) | on `AccentDefault`+`TextOnAccentPrimary` · off `FillSubtleSecondary`+`TextSecondary` | — | `Pane/SidebarPaneInlineControls.cs:36-76`, extent `Data/SidebarRowGeometry.cs:174-178` |
| Curated search head | field 32 | band pad `(8,8,8,4)`; width `max(120, paneW − 16)` | Control | 13 | transparent, `EditableText` + `ShowDeleteButton`, `LeftAffix` = `Icons.Search` 14 | — | `Pane/SidebarPaneInlineControls.cs:190-208` |
| explicit divider | band 16, rule 1 | `RowInset` | 0 | — | `Tok.StrokeDividerDefault` | — | `Shared/SidebarSectionHeader.cs:55` |
| count | text 11 | — | — | 11 / weight 400 | `Tok.TextTertiary` | — | `Shared/SidebarCounts.cs:27` |
| count (pending) | 20×12 | — | Control | — | `Tok.FillSubtleSecondary` | — | `Shared/SidebarCounts.cs:36` |
| pin marker | glyph 12 | in trailing cluster, gap 6 | — | — | `Tok.TextTertiary` | — | `Shared/SidebarEntityRow.cs:440` |
| equalizer | 12 | trailing | — | — | `Tok.AccentDefault` | — | `Shared/SidebarEntityRow.cs:437` |
| overflow "…" | 26×26 overlay | — | 13 | glyph 14 | hover `FillSubtleTertiary`, `Opacity 0→1` on row hover | — | `Shared/SidebarEntityRow.cs:669` |
| tree connector | cell 12 wide, stroke 1 | butted, no gap | — | — | `Tok.StrokeDividerDefault` | — | `Shared/SidebarEntityRow.cs:605-637` |
| empty hint | h 32 | `RowInset` | — | 11 / 1 line | `Tok.TextTertiary` | — | `Pane/SidebarPaneSlot.cs:1074` |
| pin drop zone | 56 → 72 | margin `(0,0,0,4)`, pad `(12,0,12,0)`, gap 8 | Control | 12 (600 when active) + 11 | rest: transparent + `StrokeCardDefault`; active: `AccentSubtle` + dashed `AccentDefault` 4/2 | — | `Shared/SidebarPinDropZone.cs:73-119` |
| entity card | 56/72/88, cover h−16 | margin `(0,2,0,2)`, pad `(8,0,8,0)`, gap 12 | `Radii.Card` | 14/600 + 12 | `FillCardSecondary` / selected `SelectedRest`; border 1 → 2 accent when selected | — | `Pane/SidebarPaneSlot.cs:880-899` |
| card play button | 28 (Compact) / 32 | — | circle | glyph 14 — `Icons.Play`, **`Icons.Pause` while this card is the playing context** | `Tok.AccentDefault` + `TextOnAccentPrimary`; `Opacity 0→1` on hover, and **pinned at 1 while playing** | — | `Pane/SidebarPaneSlot.cs:905-928` |
| entity card (unresolved) | — | — | — | — | `Opacity 0.55`, `IsEnabled false`, no click, no context menu | — | `Pane/SidebarPaneSlot.cs:892-899` |
| prompt row | 48 / 56 | margin `(0,2,0,2)`, pad `(8,0,8,0)`, gap 8 | `Radii.Card` | 12/600 + 11 | `Interaction.Card` | `Elevation.Card` | `Pane/SidebarPaneSlot.cs:1133-1159` |
| grid cell | edge = `min(160, max(40, (paneW−16 − 8·(n−1))/n))`; art = `max(40, edge−8)` | cell pad 4, inner gap 4; strip gap 8, bottom 8 | `Radii.Card` | 12 + 11 | `Interaction.Card`; selected border 2 accent | `Elevation.Card` | `Pane/SidebarPaneSlot.cs:951-1028` |
| tree end gutter | 24 | — | — | — | invisible | — | `Data/SidebarRowGeometry.cs:195` |
| edit card | 44 (plate inset 2) | `RowInset`, gap 4, plate pad `(4,0,4,0)` | Control | 13/600 | `open ? SelectedRest : FillCardDefault`, border `AccentSubtle`/`StrokeCardDefault` | — | `Pane/SidebarPaneEditCard.cs:122-159` |
| edit kind tile | 24 | — | Control | glyph 13 | `open ? AccentSubtle+AccentTextPrimary : FillSubtleSecondary+TextSecondary` | — | `:70-77` |
| "Hidden" tag | — | pad `(6,1,6,2)` | Full | 10/600 | `FillSubtleSecondary` + `TextSecondary` | — | `:250` |
| rail tile (icon) | 40 | — | 6 | glyph 16 | selection ladder; `TextPrimary` when selected | — | `Shared/SidebarRailItem.cs:46-63` |
| rail tile (art) | 40, art 36 | — | 8 | — | border 2 `AccentDefault` when selected; drop wash accent@0.35 | — | `:78-123` |
| rail divider | 24×1 | margin `(0,4,0,4)` | — | — | `Tok.TextTertiary` @ A 0.30 | — | `:126` |
| rail tile (pending) | 40 | stack gap 6, centred | 8 | — | `Tok.FillSubtleSecondary` | — | `Shared/SidebarRailItem.cs:132`, `SidebarSkeletons.cs:65-77` |
| rail column | — | `(0,8,0,12)`, gap 6, centred | — | — | — | — | `Pane/SidebarPaneRail.cs:87-94` |
| V3 header | 44 | `LeadBandInset (21,0,16,0)`, gap 4 | — | 15/600 | `Tok.TextPrimary` | — | `Modes/LibraryV3/LibraryV3Header.cs:110-118` |
| V3 library mark | 32 box, glyph 16 | margin `(0,0,6,0)` — the extra 6 rides the glyph box, not the row gap | — | **`""` in the `Segoe MDL2 Assets` face, named explicitly** (the default Segoe Fluent face renders tofu) | `Tok.TextSecondary` | — | `Modes/LibraryV3/LibraryV3Header.cs:69-79` |
| V3 toolbar | 36 | `LeadBandInset`, gap 4 | — | — | — | — | `:200-207` |
| V3 search host | 32 tall; 32 closed / `OpenWidth` open / Grow inline | lane starts at 32 | Control | 14 | transparent; closed hover `FillSubtleSecondary` | — | `Modes/LibraryV3/LibraryV3Search.cs:180-201` |
| V3 sort pill | 28 (icon-only 28×28) | pad `(10,0,8,0)`, gap 5 | Control | 13/600 | `Tok.TextSecondary`, `Interaction.Subtle` | — | `Modes/LibraryV3/V3SortViewFlyout.cs:92-103` |
| V3 chip rail | 40 | `LeadBandInset`, gap 6 | — | — | horizontal `ScrollView`, `AutoEdgeFade`, key `sidebar.v3.chips` | — | `Modes/LibraryV3/LibraryV3Chips.cs:208-227` |
| V3 chip | 28 | pad `(12,0,12,0)`; `FocusVisualMargin (2,2,2,2)` | Full | 13 facet / 12 option | off: `FillControlDefault`/`FillControlSecondary`/`FillControlTertiary` + `StrokeControlDefault`; on: `AccentDefault`/`AccentSecondary`/`AccentTertiary` + `Tok.OnAccent` 600; `BrushTransitionMs = WaveeMotion.Fast` 167; `Role = RadioButton` | — | `:276-301` |
| V3 clear ✕ | 28 circle | `FocusVisualMargin (2,2,2,2)` | Full | glyph 12 (`Icons.Cancel`) | `Interaction.Control` + `Tok.TextPrimary`; tooltip `sidebar.v3.clearFilters` | — | `:245-258` |
| V3 nav row | 40 | row rules | 4 | Body 14 | row ladder | — | `Modes/LibraryV3/LibraryV3NavBand.cs:342-356` |
| V3 destination word | band 30 | gap 14, lead 13 | — | 13.5 (600 when active) | `TextPrimary` / `TextSecondary`; underline 2 `AccentDefault` | — | `:172-207` |
| V3 breadcrumb | 32 | `(ContentLane−6, 0, ContentLaneEnd, 0)`, gap 4 | — | 12/600 | `Tok.TextSecondary` | — | `Modes/LibraryV3/LibraryV3Chrome.cs:129-156` |
| V3 error banner | — | margin `(8,0,8,4)`, pad `(8,6,8,6)`, gap 8 | Control | 12 + 12/600 action | `FillSubtleSecondary`; action `AccentTextPrimary` | — | `:201-231` |
| rail folder flyout | 300 wide, header ≥40, list ≤420 | pad 4, gap 4/1 | Popup | BodyStrong + Caption | `PopupChrome.Popup` | popup | `Pane/SidebarRailFolderFlyout.cs:61-160` |
| section options popover | 320×520 | — | Popup | — | `PopupChrome.Popup`, light dismiss | popup | `Pane/SidebarPaneEditCard.cs:283-316` |
| picker card | **224 wide, content-height** (compact 200); shell inset 10, gap 7 | preview 116/96, pad `(8,7,8,0)` | 6 / `Radii.Card` | 13 + 11 + tag 9.5 (compact 12 + 10.5 + 9) | on `AccentSubtle`+`AccentDefault`; off `FillLayerDefault`+`StrokeCardDefault` | — | `SidebarDesignPicker.cs:232-263,421-427`, `Design/WaveePicker.cs:46-48` |
| picker ROW card (wizard) | 480 × **100**, inset 8 | thumbnail 120 × 84 | `Radii.Card` | 14 + 12 + tag 9 | as above | — | `SidebarDesignPicker.cs:176-223,429` |
| chooser plate | ≤744 × ≥184 | pad 24, gap 12 | Overlay | 20/600 + 14 | `FillSolidBase` + `StrokeSurfaceDefault` | `Elevation.Dialog` | `SidebarDesignPicker.cs:496-511` |
| folder-picker card | **320** (clamp [320,548], MinHeight 184) | content pad 24, title margin-bottom 12, command pad 24 | Overlay 8 | title 20/600 | `FillSolidBase` + `StrokeSurfaceDefault`; content region `FillLayerAlt`; page smoke `#4D000000` | `Elevation.Dialog` | `RootlistFolderPicker.cs:64-76`, `ContentDialog.cs:110,283,311-369` |
| folder-picker panel | column 320 | gap 4 (`Spacing.XS`) | — | body 13 / 2 lines / `CharacterEllipsis` | `Tok.TextSecondary` | — | `RootlistFolderPicker.cs:151-167` |
| folder-picker search | 300 × 32 | — | Control | 13 | `EditableText`, placeholder `sidebar.findFolder` | — | `:160-164` |
| folder-picker row | h 40, icon 18 | pad `(6 + 12·depth, 0, 8, 0)`, gap 10 | 4 | 14 / 1 line / `CharacterEllipsis` | icon `Tok.TextSecondary`, label `Tok.TextPrimary`, `Interaction.Subtle`, `Role = Button` | — | `:170-187` |
| folder-picker list | `ScrollEl` `ContentSized`, **MaxHeight 360**, `EdgeCues.None` | row gap 2 | — | — | no edge cue (it sits on the dialog's translucent overlay fill) | — | `:137-144` |
| folder-picker empty line | h 44 | pad `(8,0,8,0)` | — | 13 | `Tok.TextSecondary` (`sidebar.noFolders`) | — | `:145-149` |

---

## 4. Colour & material

The sidebar has **no fill of its own**. The pane column is a paint-site omission over the window's base layer (live
Mica) — `WaveeShell.cs:1004-1008`. Every plate in the pane therefore composites over Mica, which is why:

* **the row ramp composes instead of swapping.** `SelectedHover = ColorContrast.Over(Tok.FillSubtleSecondary,
  Tok.AccentSubtle)` and `SelectedPressed = ColorContrast.Over(Tok.FillSubtleTertiary, Tok.AccentSubtle)`
  (`Design/WaveeTokens.cs:272-274`). A row paints ONE `Fill`, never two stacked plates. Light `AccentSubtle` ≈ accent @
  14 %, dark ≈ 16 % of the light-blue ramp shade (`Tokens.cs:444`).
* **the overflow "…" may overlap a long title.** There is no opaque tone to fade into over Mica, so the hover-revealed
  button is an overlay and the title is allowed to run under it rather than re-trimming on hover
  (`Shared/SidebarEntityRow.cs:479-488`).
* **on-accent ink is contrast-picked, not theme-keyed.** Chips, view cells and anything on a live-accent plate use
  `Tok.OnAccent`; `Tok.TextOnAccentPrimary` is only correct on the fixed accent plates (the card play button, the empty
  CTA) — `Modes/LibraryV3/LibraryV3Chips.cs:274`, `V3SortViewFlyout.cs:216`.
* **scrims are literal.** A track row's hover play scrim is `ColorF(0,0,0,0.55)` with a literal white glyph, because a
  scrim is dark in BOTH themes (`Shared/SidebarEntityRow.cs:700-702`).
* **the separation is the CONTENT region's stroke, not the sidebar's edge.** Because the pane has no fill, what makes it
  read as a band at all is the 1-DIP `Tok.StrokeCardDefault` the content region paints on its LEFT + TOP only, over the
  `(Radii.Card,0,0,0)` silhouette (`WaveeShell.cs:151,161-176`). It is drawn as ONE ring on a box made 1 DIP larger on
  its right/bottom inside a clipping parent, because the engine has no per-side border thickness — two strips leave a
  notch at the arc. A rebuild that gives the sidebar a fill, or drops this stroke, loses the seam in both directions.

| input | function | applied where | transition |
|---|---|---|---|
| entity id / uri | `SidebarCover.SeedFrom(key)` → 31-hash, `& 0x7fffffff` (`Shared/SidebarCover.cs:162`) | the placeholder tint of every cover-less art slot; STABLE across re-sorts because the seed is the id, never the index | none (paint) |
| `Image.Url` / `MosaicTiles` | `SidebarCover.Art` → `Surfaces.Artwork(image, seed, size, size, radius, decodePx)` (`:96-107`) | every row/tile/card cover; a cover-less playlist with ≥4 tiles becomes a 2×2 mosaic | the artwork pipeline's own blurhash→image fade |
| liked collection | `LikedSongsArtwork.Dynamic(size, radius)` inside a hard-sized clipped box (`SidebarCover.cs:84-91`) | Liked Songs rows in a LIST (never the authored nav row, whose `IconOverride: "Heart"` wins first) | ch. 07 owns it |
| `entry.Kind` | `SidebarCover.ForEntry` (`:53`) | folder → folder tile · app route → `ShellNav` glyph tile · artist/`Circular` → circle · else cover | none |
| drag payload + pointer | `RootlistSlotResolver.Resolve` → `SidebarDropCue.DrawsPlate/DrawsLine` | plate `Tok.AccentDefault @ A 0.18` + 1-DIP `AccentDefault` border UNDER the row; caret `AccentDefault` | bound prop, compositor-only (no re-render) |
| drag payload (rail tile) | `SidebarPane.IsRailDropActive(uri)` | art tile: accent@0.35 wash over the cover + the 2-DIP accent ring; icon tile: accent@0.35 fill | bound prop |
| `prefs.Design` | `SidebarHost` Key | whole-pane cross-fade | opacity 0→1 on `MotionTok.ControlFast` (150 ms) |
| `Tok.Epoch` | rail memo dep (`Pane/SidebarPane.cs:572`) | the memoized rail subtree re-enters on a theme switch | instant (`RethemeAll`) |

Light/dark differences are entirely token-side (`Tok.*` resolves per theme); the sidebar authors **no** literal colour
except the track-art scrim and its white glyph.

---

## 5. Motion

Every animation below is engine-clocked (`AnimEngine` / `LayoutTransition` / `AnimScheduler`) — the frame-time clock,
never `Environment.TickCount64`. The one `TickCount64` read in this surface is the DEBUG binder-diag throttle
(`SidebarProjectionBinder.cs:873`), which draws nothing.

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| design switch | mode subtree | Opacity | 0 → 1 | 150 ms | `FluentStandard` (`MotionTok.ControlFast`) | none | `KeepFade` (fade survives) | `SidebarHost.cs:56-58`, `MotionTok.cs:165` |
| collapse toggle (56 ↔ expanded) | sidebar column | Size (+Position), `SizeMode.Reveal`, `SuppressDescendantTransitions` | 56 ↔ width | 300 ms | `cubic-bezier(0,.35,.15,1)` | none | token policy | `WaveeShell.cs:145,184` |
| seam drag | same | — | — | **snapped 1:1** by the suppression arbiter | — | — | — | `WaveeShell.cs:182` |
| seam drag below 240 | pane CONTENT (the `_sidebarFade` opacity wrapper) | Opacity | 1 → 0 over `FadeDistance` 64 | bound signal, no transition (tracks the pointer) | — | — | — | `WaveeShell.cs:124,1029,1228-1237` |
| seam drag past 176 (`ForcePush` 64) | the column | collapse to 56 | — | the 300 ms Reveal above, once released | — | — | — | same; re-expands at `ReExpand` 190 |
| section header click | chevron glyph | Rotation | 0 ↔ 180° | 167 ms | `cubic-bezier(.167,.167,0,1)` | none | token policy (snap) | `Shared/SidebarChevron.cs:41,72`; `MotionTok.cs:182` |
| folder disclosure | chevron glyph | Rotation | 0 ↔ 90° | 167 ms | same | none | same | `Shared/SidebarChevron.cs:46` |
| recycle onto another section | chevron | Rotation | seeded at target (`from: target`) | 0 | — | — | — | `Shared/SidebarChevron.cs:66-69` |
| section/folder expand | inserted band | ItemsView disclosure: rows fade + rise; survivors FLIP | — | engine disclosure channel | `DisclosureExpand` 333 ms `FluentPopOpen` | per-row stagger (engine) | `AnimScheduler.SeedValue` honours the preference | `Pane/SidebarPane.cs:728-735`, `:1166-1224` |
| section/folder collapse | departing rows | kept alive; survivors FLIP up by the old extent | — | `DisclosureCollapse` 167 ms `cubic-bezier(1,1,0,1)` | — | — | same | `:1218-1221`, `MotionTok.cs:181` |
| ~~hand-built section reveal~~ | body clip height | Size, `Reflow`, `Anchor Trailing` | 0 ↔ auto | spring `0.40 / 0.90` | `MotionTok.ContentResize` | — | token policy | `Shared/SidebarSectionHeader.cs:38` — **DEAD in 0.2.9**: `Section`/`Rule`/`RevealWrapper`/`Reveal` have zero call sites (only `Header`, `Label`, `ExplicitDivider` ship). Do not port. |
| route change | previous + next pills | TranslateY + ScaleY (+ Opacity on the outgoing) | `0→Δ` / `−Δ→0`, scale peak `abs(Δ)/16 + 1` | **600 ms**, stretch phase 0.333 | `FluentAccelerate` → `FluentDecelerate` (same lane) / linear (cross-depth) | none | `KeepFade` | `NavigationSelectionMotion.cs:13-99`, driven `Pane/SidebarPane.cs:1596-1601` |
| route change, CROSS-DEPTH | previous + next pills | TranslateY + ScaleY | scale `1→0` (out) / `0→1` (in), anchored at the edge facing the peer | 600 ms | **`Linear` on both channels; NO opacity leg** — the scale collapse IS the exit | none | `KeepFade` | `NavigationSelectionMotion.cs:42-67` |
| route change, no travel / off-plan / recycle | pill | Opacity | snap | 0 | — | — | — | `:1586-1590`, `Shared/SidebarSelectionPill.cs:62` |
| reorder drag | displaced siblings | Position (displacement channel) | ±extent | spring `0.40 / 0.85` | `MotionTok.ItemPlacement` | none | token policy | `Pane/SidebarPane.cs:445`, `:722-725` |
| reorder lift | the row itself | `DragLift.Stationary`, `Opacity 0` | 1 → 0 | engine | — | — | — | `Pane/SidebarPane.cs:1710` |
| pin drop zone arm | card | Height 56 → 72; Fill/Border cross-fade | — | `ContentResize` spring / 83 ms brush | — | — | token policy | `Shared/SidebarPinDropZone.cs:41,94` |
| drag dwell on the rail | pane presentation | (drives the 300 ms collapse reveal) | rail → expanded | after **250 ms** dwell | — | — | — | `Pane/SidebarPane.cs:2444,2450` |
| drag dwell on a collapsed folder | folder expansion | spring-load | after `WaveeResourceDrag.SpringLoadMs` (500 ms) | — | — | — | — | `Pane/SidebarPaneSlot.cs:505-512`, `SidebarPane.cs:2164` |
| V3 search open (narrow) | host | Width + Position, `SizeMode.Reflow`, `Axes: Width` | 32 → `OpenWidth` | 167 ms (`WaveeMotion.Fast`) | `SmoothOut` `cubic-bezier(.22,1,.36,1)` | none | token policy | `Modes/LibraryV3/LibraryV3Search.cs:46-49` |
| V3 search field layer | editor | Opacity | 0 ↔ 1 | in 167 ms / out 83 ms | `SmoothOut` / `FluentAccelerate` | none | — | `:53-56` |
| V3 chip: ✕ appears | clear pill | Position+Opacity, Scale 0.6 → 1 | — | 167 ms | `SmoothOut` | none | — | `Modes/LibraryV3/LibraryV3Chips.cs:75-79` |
| V3 chip: other facets leave | facet pills | Position+Opacity, `Dx −12` | — | 220 ms | `SmoothOut` | none | — | `:85-87` |
| V3 chip: options spill / fuse | option pills | enter `Dx +12`, exit `Dx −56` | — | 220 ms | `FluentAccelerate` | none | — | `:92-96` |
| V3 chip selection | pill fill/ink | brush | — | 167 ms (`BrushTransitionMs`) | — | — | — | `:288` |
| V3 filter/qualifier change | chip rail scroll | offset → 0 | — | `ScrollController.ScrollTo(0)` | — | — | — | `:149` |
| V3 drill in/out | breadcrumb | Position+Opacity, `Dx 12` | — | spring `0.45 / 1.0` (`ConnectedFly`) | — | — | token policy | `Modes/LibraryV3/LibraryV3Chrome.cs:35-38` |
| V3 rail folder flyout drill | page body | `MotionRecipes.PageSlideForward/Back` | — | engine recipe | — | — | — | `Pane/SidebarRailFolderFlyout.cs:92` |
| V3 destination pager click | rail scroll offset | `ScrollIntoView.ScrollTo(target, animate: !Motion.ReducedMotion)` | ±0.8 viewport | engine | — | — | explicit check | `Modes/LibraryV3/LibraryV3NavBand.cs:323` |
| "Move to folder…" opens (W24) | the dialog card | Scale + Opacity | scale `1.05 → 1.0`, opacity `0 → 1` | scale 250 ms (`ControlNormal`) · opacity 83 ms (`ControlFaster`, linear) | scale on `ControlFastOutSlowInKeySpline (0,0,0,1)` | none | engine (`OverlayHost` Modal chrome) | `ContentDialog.cs:21-24`, opened at `RootlistFolderPicker.cs:64-76` |
| picking a destination / Cancel / `Esc` closes it | same | Scale + Opacity | scale `1.0 → 1.05`, opacity `1 → 0` | scale 167 ms (`ControlFast`) · opacity 83 ms | same spline | none | same | `ContentDialog.cs:21-24`; the row's own `Pick` closes the handle (`RootlistFolderPicker.cs:74`) |
| hover on any row/tile/button | fill | brush cross-fade | — | 83 ms default (`BrushTransition`) | — | — | — | engine `Interaction` |
| now playing | equalizer bars | engine equalizer | — | continuous while `IsPlaying` | — | — | engine honours reduced motion | `Shared/SidebarEntityRow.cs:437` |

**Reduced motion is never branched on in authoring code.** Seeds go through `AnimScheduler.SeedValue` under a named
token, which reads the preference and snaps (`Shared/SidebarChevron.cs:17-18`, `Pane/SidebarPane.cs:1105-1107`).

---

## 6. Interaction

### Pointer

| gesture | result |
|---|---|
| click a row | navigate (`route`), or PLAY for a track row, or toggle/drill a folder; a playlist/album row also stashes a `DetailPreview` so the destination header paints on frame one (`Pane/SidebarPane.cs:2692-2719`) |
| click a section header | toggle collapse through the MODE's owner (Curated: `SetSectionCollapsed` command; Classic: the persisted flag) |
| click a card ("…"-less area) | navigate; the hover-revealed circular play button plays the entity as a CONTEXT |
| double-click a V3 facet chip | navigate to that library page (Albums/Artists/Podcasts only; Playlists has no page) — a single tap still only filters (`Modes/LibraryV3/LibraryV3Chips.cs:196-205`) |
| double-click a tree row while the check lane is up | activates plainly (WinUI's DoubleTap rule) — `Shared/SidebarEntityRow.cs:528-533` |
| right-click a row | the row's own menu (below) |
| right-click empty pane chrome | the quick layout menu, via the childless full-bleed SHIELD under the content (`Pane/SidebarPane.cs:628-637`) |
| hover a row | fill ramp + the 26-DIP "…" fades in; a track row's cover gains the scrim + ▶ |
| hover a folder row | its trailing "+" fades in (and stays lit while a rootlist drag is over the row — hover flags are frozen mid-drag) |
| hover the V3 destination rail | the two pager chevrons fade in where there is something to reach |

### Menus (exact rows, in order)

**Row menu** = `Menus.SidebarEntry(acts, entry, …)` (the shared entity menu, ch. 01/02) **+ `NavExtras`**
(`Pane/SidebarPaneSlot.cs:1581-1668`), which contributes, in this order:

1. `Move up` — `Icons.ChevronUp` — when the row is in a reorder band at index > 0, or (Pinned with the band disarmed)
   the pin store index > 0.
2. `Move down` — `Icons.ChevronDown` — mirror condition.
3. `Move up` / `Move down` (ROOTLIST) — same labels over `FolderActions.MoveUp/MoveDown`, only for a PlaylistTree row
   that is NOT inside a reorder band, decided against the real sibling run.
4. `Move to folder…` — `ActionIcons.Folder` — when the tree has any legal destination.
5. `Move N to folder…` (`Strings.Menu.MoveManyToFolder`) — **replaces** rows 3-4 when the clicked row is inside a
   multi-selection of ≥2.
6. `Select` — `Icons.Check` — only while the check lane is hidden; this is the ONLY pointer entry into check mode.
7. (trailing block) `Remove` — `sidebar.customizer.itemRemove` — authored items only (StaticLinks / CustomGroup /
   Shortcuts), never Pinned (whose remove is `Unpin`, already in the entity menu).

Rows 1-6 land in the `Organize ▸` submenu; `Remove` stays in the trailing destructive block (`SidebarMenuExtras`).

Rows 4-5 do not act — they OPEN the **destination picker (W24)**: `FolderActions.MoveTo` → `RootlistFolderPicker.Open`
for the single row (`Actions/FolderActions.cs:193-198`, `Pane/SidebarPaneSlot.cs:1636-1639`), and
`RootlistFolderPicker.Open(batchActs, _o.OrderedTreeSelection())` for the ≥2 selection (`:1641-1648`). The batch arm is
not a variant of the picker: the selection is normalised (tree order, no descendants of a selected folder) and a folder
is offered only if the WHOLE selection may be filed into it. `Move to folder…` is present only when
`RootlistTreeNav.HasDestinations` says the tree has somewhere legal to go — a verb that would open an empty picker is
absent, never present and dead (`Data/RootlistTreeNav.cs:130-147`).

**Destination picker rows** (W24, `RootlistFolderPicker.cs:117-187`): `Top level` (pinned first, `Icons.List`, never
filtered by the query) · then every legal folder in tree order, indented 12/level, `Icons.Folder`. A row is the only
affirmative action — the dialog carries **no primary button** (`PrimaryText = ""`), so clicking a row commits and
closes in one gesture (`:67,74`).

**Missing-entity row menu**: exactly one item — `Remove` (`Pane/SidebarPaneSlot.cs:783-790`).
**Missing folder pin**: exactly one item — `Unpin` (`:561-564`).
**Header "+" flyout**: `New playlist` (`Strings.Detail.NewPlaylist`, `ActionIcons.Add`) · `New folder`
(`sidebar.createFolder`, `ActionIcons.Folder`, enabled iff an overlay service exists) — `Pane/SidebarPane.cs:2734-2743`.
**Folder row "+" flyout**: `New playlist in this folder` (`sidebar.newPlaylistHere`) · `New folder inside`
(`sidebar.newFolderInside`) — `Pane/SidebarPaneSlot.cs:1251-1261`.
**Quick layout menu** (header button · rail button · pane background · V3 overflow submenu) — `SidebarLayoutMenu.cs:56-79`,
header `sidebar.layout.menuTitle`:
`◉ Spotify Classic` · `◉ LibraryV3` · `◉ Custom` · ─── · `Customize sidebar…` (`ActionIcons.Rename`) · ─── ·
`Reset width` (enabled iff a drag pinned the width).
**Edit card "…" menu** (`Pane/SidebarPaneEditCard.cs:203-228`): `Move up` · `Move down` · ─── · `Hide/Show section` ·
`Duplicate section` · ─── · `Remove section` (`Icons.Delete`).
**V3 overflow** (`Modes/LibraryV3/LibraryV3Header.cs:136-164`): `Sidebar layout ▸` (submenu, `Icons.SplitView`) · ─── ·
`Clear filters` (`Icons.Cancel`, enabled iff any filter/search is active) · `Collapse Your Library`
(`Icons.ChevronLeft`, absent in the drawer) · [dev mode only] ─── · `API Console` (`Icons.Code`, deliberately
unlocalized).
**Section header sort/view flyout** (an editable `EntityList` only — `Pane/SidebarPaneInlineControls.cs:86-119`,
anchored `BottomEdgeAlignedRight`, `FocusTrap` + light dismiss, rows built at OPEN time from the LIVE document):
`◉ Recents` · `◉ Recently added` · `◉ Alphabetical` · `◉ Creator` · [`◉ Custom order` only when the query is
playlists-ONLY] · `☑ Reversed` · ─── · `◉ List` (`Icons.ViewList`) · `◉ Grid` (`Icons.ViewGrid`). Every row dispatches
a `SetQuery`/`SetDisplayOption` COMMAND against that section's persisted spec (reducer → undo pre-image → autosave) —
it is not mode-global state, which is exactly why it is not V3's flyout. "Reversed" means "not this sort's NATURAL
direction": recency is naturally newest-first, collation naturally A→Z (`:106-107`).

**V3 sort/view flyout** (`Modes/LibraryV3/V3SortViewFlyout.cs:131-148`): header `Sort by` · `Recents` ·
`Recently added` · `Alphabetical` · `Creator` · [`Custom order` only under the Playlists lens] · divider · header
`View as` · the 4-cell view bank (`ViewList 14` · `ViewList 16` · `ViewGrid 12` · `ViewGrid 15`, each 40×30, accent when
on). **No size row** — the grid cell is derived from the pane width.

### Keyboard

| key | where | effect |
|---|---|---|
| `F2` | a renameable row (also makes it a focus stop) | inline rename through `Menus.SidebarRenameAction` |
| `Alt+↑` / `Alt+↓` | a rootlist tree row | move one position within its own sibling run |
| `Enter` | a multi-selectable tree row | activate plainly |
| `Space` | same | toggle into the selection (Ctrl synthesized) |
| `Escape` | same | clear the selection and leave check mode |
| `Space` / arrows / `Space` / `Esc` | a `Reorderable`-wrapped row or section card | engine keyboard lift → move → drop → cancel, announced via `Reorderable.AnnounceText` (`sidebar.customizer.reorder{Grabbed,Moved,Dropped,Cancelled}` + `sidebar.pin.position`) |
| `←` `→` `Home` `End` | the V3 chip rail (ONE tab stop, roving by KEY) | move the focus visual; selection does NOT follow focus |
| `Space` / `Enter` | the roved chip | commit the filter/qualifier |
| `Escape` | the V3 search field | 1st: clear the query · 2nd (empty): close the host and return focus to the magnifier (inline: the editor's own blur) |
| `↑` `↓` `Enter` `→` `←`/`Backspace` | the rail folder flyout | rove · activate · drill in · back (swallowed at the root) |
| `Tab` / `Shift+Tab` | the destination picker (W24) | CYCLE inside the card — a real dispatcher focus scope (`PopupOptions.FocusTrap`), so the tree behind it is unreachable while it is up (`ContentDialog.cs:25-28,184-186`) |
| `Enter` | same | the `DefaultButton` = **Close** — i.e. Enter DISMISSES; a destination is committed by activating its row, never by Enter on the dialog (`RootlistFolderPicker.cs:68-69`) |
| `Escape` | same | closes with `ContentDialogResult.None` (the overlay's Escape preview); modal, so there is no light dismiss (`ContentDialog.cs:9,25-26,184-186`) |

Initial focus in the picker lands on the default button, not on the search field (`ContentDialog.cs:28`) — the whole
point of the dialog is that the list is reachable without a pointer, so `Tab` must reach `Top level` and every folder
row (each is a `Role = AutomationRole.Button` with an `OnClick`, `RootlistFolderPicker.cs:176`). This is the
keyboard-accessible counterpart to tree drag: `Alt+↑/↓` nudges one position, and this files anywhere legal at once.

The row owns **one** key handler because `InputDispatcher` routes from the focused node upward
(`Shared/SidebarEntityRow.cs:314-340`). A row inside a `Reorderable` never adds a second focus stop or key handler.

### Drag and drop

Sources: any entity row (`WaveeResourceDragPayload.FromEntry`, `rootlistItem: true` for playlists/folders), route rows
that are pinnable (`SidebarPinId.FromRoute`), rail rows in the folder flyout, section cards (private kind
`wavee.sidebar.section`), palette chips (ch. 26). Mouse threshold ×2 (`Drag.ClickPrimaryThresholdMultiplier`) because
navigating is the constant intent (`Shared/SidebarEntityRow.cs:550-553`).

Targets and what each means:

| target | accepts | visual | commit |
|---|---|---|---|
| pinned band row / empty drop zone | anything `CanPin` | insertion by displacement; zone → dashed accent | `PinActions.Pin` + `MovePin` (already-pinned = a MOVE, never a duplicate) |
| editable playlist row centre | `CanCopyTracks` | the Into plate | `WaveeResourceDrop.DepositTracks` |
| non-editable playlist row | — | refusal chip "You can't edit this playlist" | — |
| album/artist/show/route row | — | **transparent** (a drag merely crossing is not an accusation) | — |
| tree row edges | rootlist filing | the caret at the resolved depth | `WaveeResourceDrop.MoveRootlist` (ONE mutation per drop, whatever the selection size) |
| folder row centre | rootlist filing | Into plate | file inside |
| collapsed folder row | (dwell) | spring-loads open after 500 ms even when it refuses the payload | — |
| `TreeEnd` gutter | rootlist filing | caret at depth 0 | "Move to end" |
| section header "+" | rootlist selection → new folder; track set → new playlist | bound accent plate on the button | `FolderActions.NewFolderWith` / `CreatePlaylistFromDragPayload` |
| folder row "+" | rootlist selection | bound accent plate | new SUB-folder inside |
| rail playlist tile | `CanCopyTracks` (editable only) | accent wash inside the ring | deposit |
| rail folder tile | rootlist filing | accent wash | **Into only** (a 56-DIP strip has no before/after) |
| rail band (anywhere) | nothing | — | pure waypoint: 250 ms dwell peeks the pane open for the rest of the gesture |
| section card (edit mode) | palette chips | "Add here" / "Your sidebar is full…" | `AddSection` above that card |

Refusal sentences, one table (`Pane/SidebarPane.cs:2263-2272`): `Self`/`Unavailable` → `drag.cantMoveHere` ·
`IntoItself`/`IntoDescendant` → `drag.cantMoveIntoItself` · `NoOp` → `drag.alreadyThere` · `SortedList` →
`drag.clearSortingToReorder` (+ an action button in the toast that switches V3 to Playlists+Custom) · `NotLoaded` →
`drag.stillLoading`. A drop that cannot be honoured **always** says so — it never returns silently (`:2405-2417`).

The spotlight scrim is **suppressed for intra-sidebar organisation drags** (`spotlightWhen: payload is not
{ RootlistItem: true }`) — dimming the app you are re-ordering inside promises something false.

### Tooltips and accessible names

The engine has no separate automation-name channel: the visible text IS the name, and a tooltip is the only place a
non-visual one can live. Every rail tile passes one (a 56-DIP strip has no label); a folder tile's is
`"{name} · {n} items"`. Others: `sidebar.layout.tooltip` (layout button), `sidebar.createPlaylistTooltip` /
`sidebar.createTooltip` (the "+", depending on whether it has a flyout), `sidebar.item.playTrack` (every track row),
`sidebar.customizer.missingEntity` (retention rows), `sidebar.v3.searchTooltip`, `sidebar.a11y.sortView`
(+ `· Reversed`), `sidebar.a11y.filterGroup` (a zero-size text node inside the chip scroller), `sidebar.v3.collapse`,
`sidebar.rail.folderFlyoutBack`, `sidebar.customizer.properties` ("…"), `sidebar.customizer.undo.hideSection` /
`showSection` (the eye).

### Inline edit

There is no in-place text editing in the pane itself: rename is a dialog from the row menu / `F2`. The only editable
fields are the Curated search head and the V3 search box (`EditableText`, chromeless, `ShowDeleteButton`).

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 source | 0.3 read | readiness predicate (skeleton until true) |
|---|---|---|---|
| pinned band rows | `SidebarPinStore` (from `sidebar-layout.json`) + projection join | `Edges.Pins.Targets(User.Me)` + `Edges.Pins.Payload` (order) | `Pins.State == complete` (it is local: complete at boot) |
| pin row title/cover | projection entry, else the pin's own display cache, else a hydration fetch | the handle's `Title`/`Image`; `handle.Knows(Identity)` | a pin whose handle is unknown paints from the **persisted display cache** and calls `Entities.Ensure(h, Identity)` — never a blank row |
| library shortcut counts | `LibraryStore.Stats` | `Edges.SavedAlbums.Length(me)` · `FollowedArtists` · `Liked` · `SavedShows`; use `Total` when `State == partial` | `State != unknown` else the 20×12 pending plate |
| playlist tree rows | `LibraryStore.PlaylistTree` → `SidebarProjection` | `Edges.Rootlist.Targets(User.Me)` + `RootlistEdge{Position, Depth, Kind, FolderName, AddedAt}` | `Edges.Rootlist.State != unknown`; `unknown` ⇒ 3 skeleton rows |
| folder row name / child count | `SidebarLibraryEntry.Name`/`ChildCount` | `RootlistEdge.FolderName`; child count = the count of edges whose parent-folder is this folder (computed at commit) | same as the tree |
| playlist subtitle "N songs" | `PlaylistSummary.TrackCount` | `Playlist.TrackCount` (`PlaylistFields.Identity`) | `p.Knows(Identity)`; else no subtitle line, never "0 songs" |
| album subtitle "Album · artist" | `entry.FirstArtistName`/`Creator` | `Album.Knows(Identity)` + `Edges.AlbumArtists.Targets(a)[0].Title` | `Knows(Identity)` |
| show subtitle "Podcast · publisher" | `entry.Publisher` | `Show.Publisher` | `Knows(Identity)` |
| row cover | `entry.Cover`/`MosaicTiles` | `handle.ImageId` (StringId) | `Knows(Image)`; else the seeded placeholder tile (NOT a shimmer — a row is never blank) |
| now-playing `♪` | `PlaybackBridge.Identity`/`IsPlaying` + `NowPlayingMatch.RelatesToPlaying` | `Playback.Current` / `Playback.ContextUri` compared once per pane in a signal effect | always available |
| selection pill | `Signal<Route>` | `Shell.Nav.Current` → route key | always |
| "Jump back in" / "Recently played" | `HistoryStore` visits · `PlayLogStore.Recency` | `Shell.Nav` visit log (SHELL) · a `LastPlayedAt` column per entity | `RecentsState`; empty ⇒ the quiet hint |
| New releases | `SidebarFeedSources` → Spotify feed | `Home`-style synthetic subject or a feed edge | source `State`; `Pending` ⇒ skeletons |
| Concerts | `SidebarFeedSources` + location | `Concert` handles | `ConcertsLocationUnset` ⇒ the actionable `PromptRow`, never an empty caption |
| V3 qualifier availability | `SidebarProjection.QualifiersAvailable` (≥2 flavours) | a fold over `Playlist.Flags` (Owned / BySpotify) across the rootlist | computed at publish; false ⇒ the qualifier options are NOT offered |
| V3 "Recents" order | `PlayLogStore.Recency` | `LastPlayedAt` | — |
| V3 "Recently added" | `SidebarFirstSeen` proxy map (persisted, cap 2000) | `RootlistEdge.AddedAt` when the wire carries one; else the same first-seen proxy | — |
| drop legality | `IStore.Rootlist()` marker stream | `Edges.Rootlist` payload (Position/Depth/Kind) — the CSR list IS the marker stream | `State == complete`; `unknown` ⇒ every ordering refuses `NotLoaded` |
| edit-card count | `SidebarEditPlan.CardCount` over the document | unchanged (document-only) | none |

The pane **demands its whole model on mount**: Classic/Curated warm stats + playlists + the tree; V3 warms all five
kinds plus added-at (`Modes/LibraryV3Sidebar.cs:93-102`, `Pane/SidebarPane.cs:470-471`). In 0.3 that becomes one
`Entities.Ensure(rows, Fields.Row)` per section on mount — never a visible-window fetch, and never a per-row fetch from
inside a slot.

### DATA GAPS

| element | 0.2.9 source | the plan's model holds | proposal |
|---|---|---|---|
| cover-less playlist **mosaic** (2×2 of 4 track covers) | `Image.MosaicTiles` on `PlaylistSummary` (decoder) | `Image` is one `StringId` per row | `Column<StringId> Mosaic0..3` on `PlaylistTable` (`PlaylistFields.Image`), filled by `Spotify.Decode` from the same payload |
| Liked Songs **dynamic cover** | `LikedSongsArtwork` over the newest liked tracks | — | derive from `Edges.Liked.Targets(me)[0..4]` → each `Track.ImageId`; no new column |
| **pin display cache** (name/uri/kind for a cold, offline launch) | `SidebarPin{Id,Kind,Uri,Name,AddedAtMs}` in `sidebar-layout.json` | pins are `Edges.Pins` only | keep the JSON cache as the offline seed, or add `Store.PinRow{uri, kind, name, image}` written at commit; a pin MUST paint before any fetch |
| **playlist flavour** (By you / By Spotify / Mixed) | `SidebarPlaylistFlavor` from the owner id | — | two `PlaylistFlags` bits, `Owned` + `ByService` (`PlaylistFields.Identity`) |
| **CanEdit / IsOwner** (deposit legality, the refusal sentence) | `PlaylistSummary.CanEdit/IsOwner` | — | `PlaylistFlags.Editable`, `PlaylistFlags.Owned` |
| **folder child count** | `SidebarProjection` walk | `RootlistEdge` has Depth/Kind but no count | compute at commit into a `Column<int> FolderCount` keyed by folder-start slot, or count the edge run (O(children), fine at rootlist size) |
| **release age** for the New-releases badge ("3d") | `entry.SortStamp` = release epoch ms | `Album.Year` (ushort) only | `Column<int> ReleaseAt` (seconds) on `AlbumTable` (`AlbumFields.Identity`) |
| **concert date + venue** | `entry.SortStamp` / `entry.Creator` | `Concert` handle is a stub in the plan | `Concert.StartsAt` (int) + `Concert.Venue` (StringId) |
| **first-seen stamp** (Recently-added proxy) | `SidebarFirstSeen` (persisted map, cap 2000, pruned on save) | — | keep as a Store table `first_seen(uri, ms)`; it is a local observation, not wire data |
| **last visited** (Recents-by-navigation, Classic's Jump back in) | `HistoryStore` | Shell nav log is in-memory | `Column<int> LastVisitedAt` per entity, or keep it a Shell-side ring (preferred: it is device-local) |
| **library totals while partial** | `LibraryStats` | `EdgeTable.Total` exists — good | use `Total` for the count badge whenever `State == partial`, so the number does not climb while paging |
| **`ArtistLineId`** (the album row's "Album · artist") | joined at render | plan mentions it for tracks only | compute the same interned line for albums at commit (P11) |

---

## 8. Pure rules to port verbatim

Every class below is engine-free and source-included by `Wavee.Tests` today (`Wavee.Tests.csproj:237-241,252,272,277-284`).
They are **ported, never re-derived**. 0.3 destination = the named CORE section of `Shell/Sidebar.cs` unless stated.

| name | 0.2.9 file | decides | tests | 0.3 destination |
|---|---|---|---|---|
| `SidebarRowGeometry` | `Data/SidebarRowGeometry.cs` (364) | the whole ladder: heights, art sizes, insets, the content lane, `ArtX`, `TreeContentX`, `IndentFor`, header/divider/empty/card/prompt/tree-end extents, `ContentYOf`, folder/section band resolvers, `ShowsPinGlyph`, `GridFallbackColumns` | `SidebarRowGeometryTests`, `SidebarPaneInvariantTests` | `Sidebar.cs` § GEOMETRY |
| `SidebarRowExtents` | `Data/SidebarRowExtents.cs` (118) | the analytic seed extent per plan row (NaN = measure) | `SidebarRowExtentsTests` | § GEOMETRY |
| `SidebarRowPlanner` | `Data/SidebarRowPlanner.cs` (1,379) | (document × projection) → `SidebarRow[]`, `BuildEdit`, `BuildRail`, all caps: `RailTileCap` 40 / `RailPinnedCap` 8 / `RailJumpBackInCap` 4 / `RailEntityListCap` 20 · `SkeletonRows` **3** · `SectionRowCap` **5 000** (hand-authored lists — the reducer already caps those at 500) · `DynamicSectionRowCap` **20 000** (a PROJECTED section; a real 10k library must plan in FULL, so the 5 000 guard deliberately does not apply to it) | `SidebarRowPlannerTests`, `SidebarRailPlannerTests` | `Sidebar.cs` § PLAN |
| `SidebarRowResolve` | `Data/SidebarRowResolve.cs` (136) | item↔row join; `SelectsRoute`; the selection `Sweep`/`Flipped` | `SidebarRowPlannerTests` | § PLAN |
| `SidebarRowDiff` | `Data/SidebarRowDiff.cs` (58) | which row indices changed between two plans | `SidebarRowDiffTests` | § PLAN |
| `SidebarPillState` | `Data/SidebarPillState.cs` (54) | the ONE lit/dark rule for the selection pill | (pinned via `SidebarPaneInvariantTests`) | § SELECTION |
| `RootlistSlotResolver` + `SidebarDropCue` | `Data/RootlistSlotResolver.cs` (435) | drop zones, depth picking + hysteresis, the refusal table, caret/plate geometry | `RootlistSlotResolverTests`, `SidebarDropCueTests`, `RootlistRefusalTests`, `RootlistDropScenarioTests` | `Sidebar.cs` § DROP |
| `RootlistDropDecision` | `Data/RootlistDropDecision.cs` (241) | cue → destination, checked against the real marker stream | `RootlistSlotToOpTests` | § DROP |
| `RootlistTreeNav` | `Data/RootlistTreeNav.cs` (349) | sibling runs, picker destinations, `RefOf`, `HasDestinations` | `RootlistTreeTests`, `RootlistFolderPickerTests` | § DROP |
| `SidebarReorderClamp` | `Data/SidebarReorderClamp.cs` (28) | the displacement offset when a gesture is clamped | `SidebarDragClampTests` | § REORDER |
| `SidebarTreeSelection` | `Data/SidebarTreeSelection.cs` (167) | WinUI Extended multi-select semantics + `Prune` | `SidebarTreeSelectionTests` | § SELECTION |
| `SidebarNavLayout` | `Data/SidebarNavLayout.cs` (29) | which Move up/down/Remove verbs a row offers (`ordered = index ≥ 0 && count > 1`) | `SidebarNavLayoutTests`, `SidebarNavExtrasTests` | § MENUS |
| `SidebarNavPreview` | `Data/SidebarNavPreview.cs` (56) | **which library record backs a row's navigation** — `FindPlaylist`/`FindAlbum` over the store's flat lists, and the row's own display cache shaped as a `PlaylistSummary`/`Album` when the uri resolves to nothing (an unlisted/editorial pin). This is what makes a sidebar click paint the destination header on frame ONE instead of a header-less skeleton — the same stash `HomeCardNav` does. The STASH and the navigate stay with the caller (`SidebarPane.Navigate`, `:2692-2719`) | `SidebarNavPreviewTests` | § NAV |
| `SidebarEditPlan` | `Data/SidebarEditPlan.cs` (253) | `ShowsBody`, `SectionsReorderable`, `IsPinnedCard`, `CardCount`, `Fold`, `ToMoveSection`, `ToAddSection` | `SidebarEditPlanTests` | § EDIT |
| `SidebarFolderFlyoutNav` + `SidebarFolderTree` | `Data/SidebarFolderFlyoutNav.cs` (140) | the rail flyout's stack, page key, direct-children lookup | `SidebarFolderFlyoutNavTests` | § RAIL |
| `SidebarStageHold<T>` | `Data/SidebarStageHold.cs` (49) | the mid-drag publish freeze | `SidebarDropFreezeTests` | § PLAN |
| `SidebarDesignInfo` / `SidebarPaneState` | `SidebarDesign.cs` (134) | slugs, mount keys, per-design width tiers, snapshot/restore/reset/commit | `SidebarModeStateTests` | `Sidebar.cs` § DESIGN |
| `SidebarDesignGating` | `SidebarDesignGating.cs` (82) | the one-time chooser gate + marker | `SidebarDesignGatingTests` | § DESIGN |
| `SidebarPaneInvariant` | `SidebarPaneInvariant.cs` (90) | the settled-frame terminal-state validator | `SidebarPaneInvariantTests` | § DIAG |
| `SidebarPinStore` | `SidebarPinStore.cs` (224) | pin identity, order, idempotent pin/unpin, `ApplyRemote` | `SidebarPinStoreTests`, `SidebarPinSyncTests` | `Sidebar.Host.cs` (edges-backed) |
| `SidebarNavBandModel` | `Shared/SidebarNavBandModel.cs` (105) | the shortcut band's tile kinds, route resolution, selection, truncation | `SidebarNavBandTests` | § NAVBAND |
| `SidebarBuiltInDocuments` | `Pane/SidebarBuiltInDocuments.cs` (220) | **Classic's entire IA** and its display options | `SidebarBuiltInDocumentTests` | § DOCUMENTS |
| `LibraryV3Document` | `Modes/LibraryV3/LibraryV3Document.cs` (305) | V3's synthesized document + every state→display mapping | `LibraryV3DocumentTests` | § DOCUMENTS |
| `LibraryV3View` + `LibraryV3Window` | `Modes/LibraryV3/LibraryV3View.cs` (358) | tree re-grouping, drill slicing, sibling clamp, `MaterializeOrder` | (via `LibraryV3DocumentTests`) | § V3 |
| `LibraryV3ChipStrip` | `Modes/LibraryV3/LibraryV3ChipStrip.cs` (121) | the chip rail's slot model (idle/filtered/fused) + the shared morph key | `LibraryV3ChipStripTests` | § V3 |
| `LibraryV3SearchRules` | `Modes/LibraryV3/LibraryV3SearchRules.cs` (65) | inline vs narrow, escape ladder, blur-close, open width | `LibraryV3SearchRulesTests` | § V3 |
| `LibraryV3Metrics` / `LibraryV3Labels` | `Modes/LibraryV3/LibraryV3Metrics.cs` (145) | chrome band heights, thresholds, derived columns, code coercion, labels | (used by the above) | § V3 |
| `SidebarPinId`, `SidebarSearch`, `SidebarSort`, `SidebarProjection`, `SidebarBinderPipeline`, `SidebarSourceMap`, `SidebarRecency`, `SidebarFirstSeen`, `PinSyncRules`, `SidebarEntriesShadow` | `Data/*.cs` | the projection/query pipeline | `SidebarProjectionTests`, `SidebarSortTests`, `SidebarChurnTests`, `PinSyncRulesTests`, … | **ch. 26** |
| `SidebarLayoutReducer`, `SidebarCustomLayout`, `SidebarSectionKinds`, `SidebarShortcutsSection`, `SidebarTemplates`, `SidebarUndo`, `SidebarIds` | `Wavee.Core/Sidebar/*.cs` (2,526) | the document model, the 18-command reducer, 20 undo labels, section-kind capability table | `SidebarLayoutReducerTests`, `SidebarShortcutsSectionTests`, `SidebarTemplateTests` | **`Shell/Sidebar.Doc.cs`** — CORE, with `Persistence/**`'s wire, migrations and defaults (A4; ch. 26 §8 ports them). Ported by **ch. 26**, consumed here |

---

## 9. Re-author notes

### Must not be simplified

* **The per-row epoch mechanism.** A bound slot is a FROZEN child: re-planning in the pane does not re-render it. The
  pane keeps `Signal<int>[] _rowEpochs` (grow-only), a packed `byte[] _rowPlay`, and the index sets `_rowSelSet` /
  `_rowPlaySet`, and bumps only the rows the diff found changed (`Pane/SidebarPane.cs:96-123,1331-1385`). Replacing it
  with "every slot subscribes the plan version" restores the storm this exists to kill: **Slot×52 + Pill×44 + Chevron×6
  re-renders per track boundary and ×3 per navigation.**
* **A/B plan buffers.** The plan ALIASES its buffers, so the outgoing rows must survive the diff. Two `SidebarPlanBuffers`
  for the pane and two for the rail, alternating (`:65-70,812-816`).
* **The mid-drag publish freeze.** A rootlist filing is aiming at these rows; a projection published mid-gesture re-keys
  them under the pointer. Park the newest stage and flush it on session end — drop, cancel and Escape alike
  (`:835-859`, `SidebarStageHold`). The one-shot `_publishThroughFreeze` latch lets the gesture's OWN commit through.
* **The disclosure prepare-on-the-click-frame path.** An expansion must publish its inserted rows in the INPUT phase
  (`RepublishNow`, `:1266`) so `ItemsView` can arm the opening band on the same frame; otherwise the chevron rotates
  about two frames before any row moves.
* **The context-menu SHIELD.** The pane menu must hang off a ZStack shell + a **childless** full-bleed shield beneath
  the content, never off the pane root — `ContextBit` makes an element a hit target, and a press on any dead spot would
  otherwise resolve the whole sidebar as the hover/press owner (`:609-638`; `SidebarPaneInvariantTests` pins the key
  literal `"sidebar:context-shield"`).
* **The rail memo and its dep audit.** `(planVersion, Tok.Epoch, CultureEpoch, inDrawer|binderPresent, railHeadEpoch,
  selectedRoute)` — 26 `ToolTip.Wrap` targets rebuilt per pane render otherwise defeat `ToolTipSlots`' reference-equality
  short-circuit (`:534-579`).
* **The layout firewall.** `IsolateLayout = true` on the wrapper INSIDE the animated pane (`WaveeShell.cs:1040`) — a
  sidebar publish marks dozens of tooltip-wrapped nodes dirty, and without the boundary each escapes to a full-tree
  relayout.
* **Every "no" is two answers.** Transparent (not my business) vs refused (you aimed here, and here is why) —
  `Pane/SidebarPane.cs:2112-2150`. Collapsing them back into one silent `return false` is the single biggest legibility
  regression available in this surface.
* **Two selections, two skins.** Route selection = accent plate + pill. Tree multi-selection = the quiet
  `FillSubtleSecondary` plate + the check lane. They must never be the same colour.
* **Section rhythm's THREE suppressions.** A header band carries 8 above / 2 below — suppressed for the pane's first
  row, after a `Divider`, **and after a bare `HeaderLabel`** (`Pane/SidebarPaneSlot.cs:230-238`). It is PADDING on a
  wrapper, never a margin on the header, so `RepeatLayout.VariableList`'s extent stays honest and scroll anchoring
  cannot drift. Dropping the third arm double-spaces every heading+section pair.
* **The empty row has FOUR arms, not three.** Pinned → the drop-zone card · `HideBody` → a literal **Blank** (V3's
  library section, so V3's own actionable state is the only empty message on screen) · `ActionCard` → a DISABLED
  `SidebarEntityRow` at the section's own row height with `Icons.Calendar`/`Icons.Grid` · otherwise → the quiet 32-DIP
  11 px tertiary hint. A `PlaylistTree` swaps its copy while a query is live (`Pane/SidebarPaneSlot.cs:1043-1090`).

### Traps

| trap | why it bites here | the 0.2.9 answer |
|---|---|---|
| props freeze at mount | `SidebarPaneConfig` is built in `UseMemo(…, DepKey.Empty)`; a VALUE member would pin frame 1 forever | every member is a delegate or a flag; `Document`/`Input`/`ModeEpoch`/`Edit` are invoked inside the PANE's render, which is also what subscribes it |
| bound thunks wire at MOUNT only | a recycled slot keeps the thunk it first mounted with — this produced two lit carets and a stale selected plate | every cue thunk reads `_scope.Index.Value` + `SubscribeRowEpoch(i)`, never a captured index or height |
| hook-owning children in a recycling slot | a chevron is a Component; a slot recycles onto a different section every scroll | the live-state probe captures the SLOT, never a section id (`SidebarPaneSlot.HeaderOpenLive/FolderOpenLive/CardOpenLive`) |
| `ReuseGuard` / frozen `ItemCount` | a render-time signal write is a backwards write | `_rowCount` is seeded once before the list exists, then written only in a layout effect |
| one transform owner per node | a `Reorderable.Item`-wrapped row must not also carry an authored offset hint | `SidebarRowSpec.Animate` is left null inside a band; `Reorderable` owns position |
| `Reorderable.Item`'s wrapper is a flex ROW | wrapped rows rendered visibly narrower than their neighbours | `content with { Grow = 1f, Shrink = 1f, MinWidth = 0f }` at both wrap sites; `ToolTip.Wrap(…, grow: 1f)` for the component-wrapped rows |
| uniform pitch | a `Reorderable` band and the extent table both assume one height | `SidebarRowSpec.Height` is PINNED from the section, never derived per row |
| a freshly-minted document per render | defeats `PublishStage`'s `!ReferenceEquals(stage.Document, Doc)` and makes every publish a whole-window re-skin | all three modes CACHE their document (`ClassicDocumentCache`, `LibraryV3Sidebar._docLayout`, `CuratedSidebar._renderDoc`) |
| `Loc.Get` at render time | subscribes static chrome to `CultureEpoch`, ×4 mounts | every menu builds its rows at OPEN time |
| a context-menu verb that needs a surface | the menu that launched it is GONE by invoke time, so there is no anchor node to place a flyout against | "Move to folder…" opens a `ContentDialog` (W24), exactly as `Menus.OpenPicker` hosts the playlist picker (`RootlistFolderPicker.cs:28-30`) |
| the picker's card width | the picker never passes `DialogWidth`, so the card resolves at ContentDialog's FLOOR — `cardW = clamp(320, [320,548])` — while the panel declares its own `Width = 320` column inside the card's 24-DIP padding (`ContentDialog.cs:283,311-314`, `RootlistFolderPicker.cs:153`) | reproduce what 0.2.9 paints and measure it; do NOT "fix" it into a 460-wide dialog (460 is the sidebar seam's `NavPaneMaxW`, W23 — it has nothing to do with this card) |
| zero-alloc scroll frames vs per-row richness | 13 row kinds, menus, drags, tooltips | rows are pure statics over an `in` struct; ONE Component per SLOT (not per row); `ContentType` = row kind gives one recycle pool per shape; the pane reads playback/route ONCE on behalf of every row |

### Do NOT port (dead in 0.2.9, verified by grep)

* `SidebarSectionHeader.Section` / `.Rule` / `.RevealWrapper` / the `Reveal` transition — zero call sites; the
  virtualized pane plans rows, it never wraps a body in a clip.
* `SidebarLayoutMenu.HeaderButton` — already deleted; the always-visible `Button` is the only entry point (the
  component still takes a `revealed` signal for a future caller — `SidebarLayoutMenu.cs:39-49`).
* `SidebarCover.Monogram` — no sidebar call site (list rows keep the seeded tile).
* `SidebarSkeletons.GridCell` / `Rows(count, …)` / `Row(jitter: true)` — unused variants (only `Row(...)` from the slot
  and `RailStack(...)` from the rail have call sites). The `Rail(index)` indirection is position-independent by design.
* `SidebarRowSpec.Caption` — the row builder supports a THIRD text line (11 px `Tok.TextTertiary`, stack gap 1,
  `CharacterEllipsis` — `Shared/SidebarEntityRow.cs:405-413`) and **nothing in 0.2.9 ever sets it** (`:107` is the only
  assignment, to null). Keep the shape if it is free; do not budget a wireframe for it.
* `SidebarDesignPicker`'s `comingSoon` arm (`:176,226`) — no call site passes `true`; the withheld design is ABSENT,
  never a disabled "Coming soon" card. Do not reintroduce the greyed card.
* `WAVEE_SIDEBAR_DISCLOSURE_TRACE` / `WAVEE_SIDEBAR_BINDER_DIAG` env flags (`Pane/SidebarPane.cs:125`,
  `SidebarProjectionBinder.cs:116`) — CLAUDE.md forbids environment-variable switches. Port the disclosure/binder
  lifecycle lines as **always-on** log events (they already are `WaveeLog` events; only the gate must go).
* `WAVEE_RAIL_BASELINE` (`WaveeShell.cs:140`) — the shell flag that swaps `SidebarPaneAnim` for a `Reflow` /
  `ControlFast` baseline and nulls the content card's FLIP. Same rule: the SHIPPING arm (Size|Position Reveal, 300 ms,
  `SuppressDescendantTransitions`) is the only one to port; the comparison arm goes.

### Where the plan is wrong or too thin for this surface

1. **§2's line budget is the headline risk.** `Sidebar.cs 4,500 + Sidebar.UI.cs 5,000 + Sidebar.Host.cs 800 = 10,300`
   against 29,025 lines of `Features/Sidebar/**` + 2,526 of `Wavee.Core/Sidebar/**`. Even excluding the customizer page
   (4,247, ch. 26) and persistence (1,491, ch. 26), this chapter's surface alone is **16,126** lines of renderer/mode
   code plus **6,311** of pure rules it cannot lose. Honest estimate below.
2. **§4.12's `Track.Row` is not this surface's row.** A sidebar row has a selection gutter + pill, a depth ladder with
   tree connectors, a disclosure chevron in the trailing cluster, a check lane, a pin glyph, a count badge, an overflow
   overlay, a drop plate and an insertion line, a drag source, a drop target, `OnActivate(mods)`, `OnRename`, `OnMove`,
   `OnEscape`. `SidebarRowSpec` has **38 fields** (`Shared/SidebarEntityRow.cs:68-297`). Plan for a second row primitive
   in `Sidebar.UI.cs`, not a reuse of `Track.Row`.
3. **§4.13's page shape does not apply.** The sidebar is not `Children = [Hero, TrackList, …]`; it is ONE bound list
   over a heterogeneous plan with a measured variable-extent layout, a disclosure channel and a displacement channel.
   The engine APIs §4 lists (`ItemsView.CreateBound`) are necessary but not sufficient: `RepeatLayout.Extents`,
   `ListOptions.Disclosure`, `ListOptions.Reorder`, `ItemsViewController.BeginDisclosure/CompleteDisclosure`,
   `Reorderable`, `Drop.Target`, `AnimEngine.SeedValue/KeyframesMotion`, `ScrollController`, `EdgeFadeSpec`,
   `OnScrollGeometryChanged` and `ScrollIntoView.ScrollTo` are all load-bearing and must survive the fold.
4. **§5 Wave 4 gives owner J "Sidebar.cs, Sidebar.UI.cs, Sidebar.Host.cs" and the gate `--fake` shows "a working
   sidebar".** That gate is not falsifiable. Replace it with: Classic + Curated + V3 each render at 180/240/280/320/460,
   the rail renders and drag-peeks, `SidebarPaneInvariant.Inspect` returns `None` on a settled frame in both states, and
   the reducer/planner/geometry test files are green.
5. **§7's risk row ("Sidebar platform is the largest port (31k lines) — Owner J alone for the whole wave")** is the one
   risk that is under-mitigated: one owner cannot port 31k lines of renderer in one wave. Split J into J1 (pane
   renderer + row/rail primitives), J2 (the three modes + V3 chrome), J3 (pipeline + persistence + customizer, ch. 26),
   on disjoint files, with the pure rules ported FIRST and kept green throughout. The five files are already disjoint
   along those lines: J1 takes `Sidebar.UI.cs`'s pane/slot/row/rail half, J2 its three-designs half, J3
   `Sidebar.Doc.cs` + `Sidebar.Host.cs` + `Sidebar.Customizer.UI.cs`, and `Sidebar.cs` (the pure rules) lands first
   and is shared. **`Sidebar.Customizer.UI.cs` is sequenced LAST** (arbitration 2026-09-12, A4 — ch. 26 §9.3.7): the
   wave gate is a working sidebar, not a working customizer, so the customizer may slip without blocking it.
6. **§4.14 `User.cs` covers Liked/SavedAlbums/FollowedArtists/SavedShows/Pins/Rootlist** — good — but the sidebar also
   needs the **pin display cache** and **folder child counts** (see §7 DATA GAPS) before a cold, offline first frame
   can paint.
7. **Missing from the §2 tree entirely:** the sidebar customizer page (ch. 26 — it has no home in `Screens/` or
   `Shell/`), `sidebar-layout.json` persistence + migrations (1,491 lines), and the extension registries
   (`WaveeExtensionRegistry` / `ISidebarDataSource` / `WaveeActionDescriptor`), which live in `Actions/Extensibility/**`
   today and have no destination in the 8-folder tree. **Settled** (arbitration 2026-09-12, A4): the first two are
   `Shell/Sidebar.Customizer.UI.cs` and `Shell/Sidebar.Doc.cs` (the layout document + reducer + wire, persistence
   included), both inside the five-file set in this chapter’s header and ch. 26 §9.3.2. The registries are the one
   piece this chapter does not settle — ch. 26 §9.3.9 argues them to owner I, with `PinRowRule` coming the other way
   into `Sidebar.cs`.

### Line budget

| | lines |
|---|---|
| 0.2.9, this chapter's surface (root 3,855 + Pane 6,470 + Shared 2,137 + Modes 3,664) | **16,126** |
| 0.2.9, the pure rules it renders from (`Data/**`, shared with ch. 26) | 6,311 |
| 0.2.9, ch. 26's half (Curated customizer 4,247 + Persistence 1,491 + Core/Sidebar 2,526) | 8,264 |
| plan §2 target (`Sidebar.cs` 4,500 + `Sidebar.UI.cs` 5,000 + `Sidebar.Host.cs` 800) | **10,300** |
| honest estimate at parity for the WHOLE platform (chs. 25 + 26 together), comments trimmed to ~1/3 of today's density | **23,000–25,000** — one five-file set (arbitration 2026-09-12, A4): `Sidebar.cs` **~5,000** · `Sidebar.Doc.cs` **~4,000** · `Sidebar.UI.cs` **~7,500** · `Sidebar.Customizer.UI.cs` **~4,500** · `Sidebar.Host.cs` **~2,500** = **~23,500** |

**Reading that row.** The total and the per-file numbers are ch. 26 §9.3.2's (its estimate wins where the two
chapters disagreed), with this chapter's `Sidebar.Doc.cs` added as the fifth file. The document + reducer + wire that
ch. 26 folded into a ~9,000-line `Sidebar.cs` are exactly `Persistence/**` 1,491 + `Wavee.Core/Sidebar/**` 2,526 =
4,017, so carving them out as `Sidebar.Doc.cs` (~4,000, this chapter's number) leaves `Sidebar.cs` at **~5,000**;
9,000 = 5,000 + 4,000, and no line is counted twice. This chapter's own share is `Sidebar.UI.cs` 7,500 + the
planner/geometry half of `Sidebar.cs` (~1,500) + the store/binder half of `Sidebar.Host.cs` (~1,600) ≈ **10,600**;
ch. 26 §9.4 carries the other ~12,900 and states the same split from its side. The earlier
"18,000–20,000 / `Sidebar.UI.cs` ~9,000 / `Sidebar.cs` ~6,500" line is withdrawn: `Sidebar.UI.cs` is 7,500 because
the plan/diff/geometry/resolve halves of today's `Pane/**` and `Modes/**` are **not** UI and land in `Sidebar.cs`.

**What is at risk if the 10.3k budget is enforced**, in the order a hurried port drops it: the rail folder flyout (321)
· the customize canvas (356 + 253) · the tree connector guides + depth ladder · the multi-select check lane (167 + the
row wiring) · the drop-slot resolver's depth channel and hysteresis (435) · the insertion-line/plate split (D1's fix) ·
the per-row epoch machinery (the perf work) · the mid-drag freeze · the V3 destination word rail (~150) · the chip
morph grammar (121 + 348) · the search morph (203 + 65) · the miniatures (343 + 564) · **the "Move to folder…"
destination picker (189, `RootlistFolderPicker.cs`)** — the keyboard-accessible counterpart to tree drag, and the first
thing a port that "already has drag" drops. Each one of those is a named, shipped fix for a reported bug; none of them
is decoration.

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`. Unless stated, route =
`home`, docked pane, viewport 1280×800, design set from the pane's own quick layout menu.

1. **Left lane.** Classic @280: a screenshot ruler puts every art tile, glyph and cover at x = 21 ± 0.5 from the pane's
   left edge, and the section-header title at x = 12.
2. **Row heights.** Classic @280: Pinned/Playlists rows measure 44, "Your Library" rows 40, headers 28 with 8 above
   (except the pane's first row and a row after a divider) and 2 below.
3. **Divider.** The explicit rule occupies a 16-DIP band with a 1-DIP hairline spanning x 12 → paneW−16.
4. **Pane padding.** The list's first row starts 8 DIP below the pane top; the last row ends 12 DIP above the bottom.
5. **Selection plate.** Navigate to Liked Songs: the row wears `AccentSubtle`, and hovering it gets STRONGER, not
   weaker (compare two hover captures).
6. **Selection pill.** The 3×16 accent pill sits at x 12..15, vertically centred (top 14 on a 44 row).
7. **Pill flight.** Frame-record a click from a top row to a bottom row: the two pills run a 600 ms stretch-then-settle,
   the outgoing fades after the first third, and exactly ONE pill is lit when it ends.
8. **Pill after recycle.** Scroll the selected row out and back: it returns lit, with no stray lit pill anywhere.
9. **Chevrons rotate.** Frame-record a section toggle: one glyph rotates 180° over ~167 ms — no glyph swap, no pop.
10. **Collapse choreography.** Toggling Playlists glides the rows below it; nothing jumps, and the scroll offset does
    not re-pin.
11. **Now playing.** Play a playlist: its row shows the 12-DIP accent equalizer, animated; pause and it holds low.
    Exactly the rows for that context light.
12. **Counts.** "Your Library" shows quiet 11-px tertiary numbers — never an accent pill; before the stats land, each is
    a 20×12 shimmer plate.
13. **Pin marker.** A pinned playlist that also appears in the tree shows the 12-DIP pin glyph in its trailing cluster.
14. **Overflow.** Hover a row: the 26-DIP "…" fades in at the trailing edge and the TITLE DOES NOT RE-TRIM (compare the
    resting and hovered captures pixel-for-pixel at the title's right edge).
15. **Track row.** In a section containing a hand-placed track, hovering the row darkens its cover with a 55 % scrim and
    a white ▶; clicking plays instead of navigating.
16. **Skeletons.** Launch with the pane visible: exactly 3 shimmer rows per pending section, at the section's own row
    height, with a 32-tile + 140×12/80×10 bars.
17. **Empty pinned.** Unpin everything: the drop zone rests at 56 with a solid hairline; start dragging a playlist and
    it grows to 72 with a dashed accent border and accent-subtle fill.
18. **Drop-to-pin.** Drag a playlist onto the pinned band: the band parts (displacement), the chip reads `Pin "{name}"`,
    and the drop lands at the gap the user saw.
19. **Into plate.** Hover a drag over a folder's centre: an accent@0.18 plate with a 1-DIP accent border appears UNDER
    the row — title and art are not tinted.
20. **Insertion line.** Hover the top edge of a tree row: a 2-DIP accent caret with a 6-DIP terminal dot at the row's
    top, starting at the row's own content x for that depth.
21. **Outdent.** With a drag on the last child of a folder, travel the pointer left one cell: the caret jumps one
    connector cell left and the chip reads `Move out of "{folder}"`; it does not flicker within 4 DIP of the boundary.
22. **Tree end.** Drag past the last playlist: a caret at depth 0 and the chip reads `Move to end`.
23. **Refusals.** Drag a folder onto itself (chip: "can't move into itself"), onto a non-editable playlist with tracks
    ("You can't edit this playlist"), and across an album row (**no cue at all** — transparent).
24. **No scrim for organisation drags.** Dragging a playlist inside the sidebar does NOT dim the app; dragging a track
    from a detail page does.
25. **Spring-load.** Hold a drag over a collapsed folder: it opens after ~500 ms and the children become targets.
26. **Drag peek.** Collapse the sidebar, start dragging a track, dwell on the rail: after ~250 ms the pane slides open
    at full width with labels legible (not a clipped strip of covers).
27. **Rail metrics.** @collapsed: the column is exactly 56, tiles are 40×40 at a 6-DIP gap, the first tile's top is 8
    from the pane top, dividers are 24×1.
28. **Rail selection.** The active route's icon tile wears the accent plate; an art tile wears a 2-DIP accent ring.
29. **Rail tooltips.** Hover each tile: every one has a tooltip; a folder tile's reads `"{name} · N items"`.
30. **Rail folder flyout.** Click a folder tile: a 300-DIP panel opens to the tile's right, top-aligned, listing its
    direct children with covers, subtitles and a "…" per row; a sub-folder drills in with a page slide and a back
    chevron appears.
31. **Rail deposit.** Drag a track onto an editable playlist tile: an accent wash appears inside the ring; drop adds.
32. **Rail create.** Classic's rail ends with "+" then the layout-menu tile, each separated by a 24×1 rule.
33. **Both layers mounted.** Toggle collapse and back: text never reflows through a narrow layout (capture the pane's
    first frame after expanding — labels are already at their final width).
34. **Curated grid strip.** Curated @320 with a Grid-presentation section: 2 columns, cell edge 148, gap 8; widen to
    460 and the cell count/edge follow the planner, never a re-derived count.
35. **Curated empty pane.** Hide every section: the centred empty state with the accent "Customize sidebar…" CTA. Do the
    same in Classic (locked): NO CTA.
36. **Customize canvas.** Route `sidebar-customize` in Curated: every section becomes a 44-DIP card with grip/kind
    tile/title/count/eye/"…"/chevron; the Shortcuts card has no grip, eye or "…".
37. **One card at a time.** Expand a card: its real rows appear under it, the card wears the selected plate, and the
    card drag band disarms (dragging a card does nothing; the "…" Move up/down still work).
38. **Hidden section.** Hide a section from its card: the card stays, dimmed to 0.55, with a "Hidden" pill instead of
    the count.
39. **Options popover.** Click a card's "…": a 320×520 popover opens to the RIGHT of the card, top-aligned, carrying
    the property panel; closing it clears the subject.
40. **V3 chrome stack.** V3 @300: nav band (word rail 30 + Home 40 + rule) → header 44 → toolbar 36 → chip rail 40 →
    rule, then the list. Measure each band.
41. **V3 alignment.** The header's library glyph, the nav rows' glyphs, the closed search box and the first chip all
    start at x = 21.
42. **V3 inline search.** @300 the field is present and transparent with "Search in Your Library"; drag the seam to 290
    and it collapses to a 32-DIP magnifier while the sort pill keeps its label until 280.
43. **V3 search morph.** @240, click the magnifier: the SAME node widens to 171 over ~167 ms (frame-record: a reflow, not
    a cross-fade), the sort pill goes icon-only, and the caret lands in the field.
44. **V3 search escape.** Type a query, press Escape → the query clears and the field stays open; press Escape again →
    it collapses and focus returns to the magnifier. Blur an EMPTY narrow field → it collapses; blur a non-empty one →
    it stays.
45. **V3 chips.** Idle = four unselected pills. Tap Playlists: a round ✕ pops in (scale 0.6→1), the other three slide
    left and fade, and (with mixed-provenance data) three qualifier options spill in from the right.
46. **V3 fused pill.** Tap "By you": the option flies left into the facet, which becomes `[✓ Playlists │ By you ✕]` —
    the SAME node (shared key), not a swap.
47. **V3 chip width.** Capture a facet pill unselected and selected: identical width and padding; only the fill, border
    and weight change.
48. **V3 destination rail.** Narrow the pane to 200: the words SCROLL (with an edge fade on the clipped side) and never
    abbreviate to glyphs; the active word keeps its 2-DIP underline and its count.
49. **V3 pager.** Hover the word rail: chevrons appear only on the side that has more to reach; clicking scrolls ~0.8
    viewport.
50. **V3 folders.** @360 a folder discloses inline (indented children); @280 the same click drills in and the 32-DIP
    breadcrumb appears with a back chevron; widening past 320 drops the drill level.
51. **V3 grid.** Switch to Grid @380: 3 columns of 116-DIP cells with a subtitle; CompactGrid drops the subtitle.
52. **V3 sort flyout.** Open it: five sorts only under the Playlists lens (four otherwise), a "Reversed" state, a
    divider, "View as" and the 4-cell bank — and NO size row.
53. **V3 empty states.** Search for nonsense → "No results for …" + Clear search. Filter Podcasts with none → "No
    Podcasts in Your Library" + Clear filter. Exactly ONE empty message on screen.
54. **V3 rail.** Collapse V3: the five library destinations appear as rail tiles (they are chrome, not a section), then
    the shortcut band's tiles, then pins/library, then "Expand Your Library" + "+".
55. **Multi-select.** Right-click a tree row → Select: the check lane slides in on EVERY tree row; Shift-click ranges;
    the selected rows wear the quiet plate, not the accent one; Escape clears.
56. **Batch move.** With 3 rows selected, right-click one: the positional verbs are replaced by "Move 3 to folder…".
57. **Keyboard reorder.** Focus a pinned row, Space, ↓, Space: the row moves and a screen reader announcement fires at
    grab/move/drop.
58. **Alt+arrows.** Focus a tree row and press Alt+↓: it swaps with its next SIBLING (not the next visible row).
59. **Design switch.** Switch design from the pane's own background context menu: the pane cross-fades over ~150 ms,
    the width re-seeds to the new design's tier, and the previous design's width is restored when you switch back.
60. **Width tiers.** With no committed seam drag, set the window to 1300 / 1500 / 1900: Classic 240 / 280 / 320,
    Curated 280 / 320 / 360, V3 300 / 340 / 380. Drag the seam once, and the ladder stops applying for THAT design only.
61. **Drawer.** Narrow the window under 720: the drawer opens with the EXPANDED pane, no rail, and its own scroll
    position.
62. **Terminal state.** After every collapse/expand settles, the diagnostics probe's `SidebarPaneInvariant.Inspect`
    returns `None` (rendered width 56 ± 0.5 compact, = the preferred width expanded, exactly one hit-testable layer).
63. **Classic's library ORDER.** "Your Library" reads top-to-bottom `Albums · Artists · Liked Songs · Podcasts · Local
    files`. Local files carries no count; the other four do.
64. **Seam resist and fade.** Drag the seam from 460 inward: nothing dims until 240, then the pane's CONTENT fades
    progressively; release at 200 and it stays at 200 un-dimmed-on-release; push to ~175 and it snaps to the 56 rail;
    drag back out and it re-expands at ~190, not at 176 (the ~14-DIP hysteresis band).
65. **Tier hysteresis.** With no committed drag, resize the window 1420 → 1390: the pane STAYS at the mid tier (the
    24-DIP shrink dip holds it down to 1376); 1370 drops it to narrow. Widening always applies immediately.
66. **Drawer width.** Below 720 the drawer opens at `min(max(240, your pinned width), viewport − 32)`; widening back
    past 760 (not 720) returns to the docked pane.
67. **Header inline controls.** Give a Curated `EntityList` `InlineControls`: a 26-DIP chip row appears UNDER its
    header band (wrapping at a narrow pane, never scrolling), the header gains a 24-DIP sort trigger, and the picks
    SURVIVE a restart. Tap the active chip: the filter clears to everything rather than blanking the section.
68. **Rail pending.** Launch collapsed with no binder: the rail shows exactly 4 shimmer tiles at 40×40, gap 6 — not an
    empty strip.
69. **Rail omissions.** An Action shortcut in the shortcut band has NO rail tile; a Concerts section has exactly one
    (calendar glyph, navigates to the hub); any other feed-shaped section's tile EXPANDS the pane.
70. **Rail folder menu.** Right-click a folder TILE: the full folder menu, including **Expand folder** — not a
    truncated one.
71. **V3 pin band on drag.** In V3 with nothing pinned, there is no pin band; start dragging a playlist from anywhere
    and the band + drop zone appear for the gesture, then disappear. Drill into a folder or type a query: it hides.
72. **V3 compact list.** Switch to CompactList: rows measure 32 with 20-DIP art and no subtitle — the whole band, not
    a mix.
73. **V3 failure with nothing.** Force the library into `Failed` with zero rows: the FULL error state with Retry takes
    the pane (not the one-line banner, and not an `EmptyState.Compact`).
74. **V3 long query.** Paste a 200-character query with no matches: the echoed string is cut at 24 characters + "…"
    and the pane does not widen or wrap past 2 lines.
75. **V3 search resets the drill.** Drill into a folder, then type: the breadcrumb disappears and the list flattens.
    Unfollow a drilled-into folder from elsewhere: the level pops rather than showing an empty breadcrumb.
76. **V3 library mark.** The header's leading glyph renders as the library mark, not as a tofu box (it is
    `Segoe MDL2 Assets` U+E71C, and it must name that face explicitly).
77. **10k library.** With a 10 000-playlist rootlist, every row is planned (scroll to the last one) — the 5 000
    authored-list guard must not reach a projected section; only the 20 000 ceiling does.
78. **Header title ellipsis.** Rename a section to 80 characters: the header title ellipsizes and the chevron and
    trailing actions stay inside the pane (#84).
79. **Card play/pause.** Play an entity card's context: its circular button stays visible at opacity 1 and shows
    Pause, not Play.
80. **Picker row form.** In the setup wizard, the same three designs render as 480×100 rows with a 120×84 thumbnail —
    same previews, different shell — and clicking one changes the live pane behind it immediately.
81. **Move to folder… exists and is a DIALOG.** Right-click a nested playlist ▸ `Organize` ▸ `Move to folder…`: a modal
    ContentDialog opens over the smoke scrim, titled **"Move to folder"** (no ellipsis — the ellipsis is the menu row's),
    with the body "Choose where “{name}” should live.", a "Find a folder" field, `Top level` first with a list glyph,
    then the legal folders in TREE ORDER indented 12 DIP per level, and ONE `Cancel` button pushed to the right. Click a
    folder: the dialog closes, the row lands inside it, and the toast reads `Moved to {folder}` with **Undo**. Pick
    `Top level` on a nested row: the toast reads `Moved to Your Library`.
82. **The picker is keyboard-complete, and never empty.** With the dialog up, `Tab` cycles only inside the card and
    reaches every destination row; `Enter` and `Escape` both DISMISS (Enter is the default Close button, not a commit);
    focus starts on `Cancel`, not in the search field. Type a string matching no folder: the list shows `Top level` plus
    "No folders yet" only once `Top level` itself is absent. Then right-click the LAST top-level playlist in a rootlist
    with no folders at all: `Move to folder…` is **not in the menu** (no empty picker ever opens). Finally select ≥2
    rows, right-click one of them: the single-row move verbs are replaced by `Move {n} to folder…`, and the picker offers
    only folders the WHOLE selection may enter (a selected folder's own subtree is not on the list).

---

## 11. Audit log

Adversarial re-read of the 0.2.9 sources against every number, state and element claimed above. Each line is one
correction: **wrong** (the chapter said something the source contradicts), **missing** (a state/element/motion/rule the
source has and the chapter did not), **unverified** (stated without a reachable source, now pinned), **overclaim** (a
rule narrower in the source than the chapter's phrasing).

| # | § | kind | correction |
|---|---|---|---|
| 1 | 2 / W1 | wrong | "@280 (default tier, viewport < 1400)" — at viewport < 1400 Classic's tier is **240**. 280 is the MID tier (1400–1799); the wide tier enters at **1800** (`ShellResponsiveLayout.cs:172-174`). Heading corrected. |
| 2 | 2 / W1 | wrong | "Your Library" was drawn `Liked · Albums · Artists · Podcasts · Local`. The document's order is **`albums · artists · liked · podcasts · local`** (`Pane/SidebarBuiltInDocuments.cs:93-97`). Rows re-ordered; "local carries no count" annotated. |
| 3 | 2 / W21 · 3 | wrong | Picker cards "224×196 (compact 200×168)". `WaveePicker.Pane`/`PaneCompact` are `(224, NaN, 10, 7)` / `(200, NaN, 10, 7)` — the HEIGHT is content-sized, only the width is fixed (`Design/WaveePicker.cs:46-48`). The 196/168 figures were invented. |
| 4 | 2 / W2 | wrong | "gap 10" was attributed to the skeleton's bars; 10 is the ROW's art↔text gap, the two bars stack at **4** (`Shared/SidebarSkeletons.cs:36-44`). The tile radius is the shared `SidebarCover.Radius` ladder, not a literal 6. |
| 5 | 2 / W5 · 3 | missing | The rail's PENDING state: `SidebarSkeletons.RailStack(4)` when nothing is planned and there is no binder yet (`Pane/SidebarPaneRail.cs:28,60`). Added as a wireframe row, a §3 token row and parity item 68. |
| 6 | 2 / W5 | missing | Three rail rules the chapter had no line for: an Action/Track shortcut is **omitted** from the rail (`:190`); a feed-shaped SECTION becomes one tile (Concerts → Calendar/navigate, everything else → Grid/expand — `:201-209`); a folder TILE carries the FULL folder menu incl. "Expand folder" (`:154-171`). Added as a table. |
| 7 | 2 / W5 | missing | The rail scroller's own chrome: `AutoEdgeFade = true`, `SuppressScrollBar = true`, and the layout tile is gated on `Config.RailLayoutMenu` (`SidebarPane.cs:562-567`, `SidebarPaneRail.cs:71`). |
| 8 | 2 · 3 · 6 | missing | **The whole `Display.InlineControls` surface was absent**: the 26-DIP kind-filter chip strip under an editable `EntityList` header, its 24-DIP sort trigger, and the flyout's row list. Added as W1b, four §3 rows, a §6 menu block, and parity item 67. Its analytic extent (`ChipHeight` 26 / `ChipStripHeight` 28 / `ChipStripGap` 4, `Data/SidebarRowGeometry.cs:170-178`) was also missing from §8's geometry line. |
| 9 | 3 | missing | The Curated search head's width floor — `max(120, paneW − 16)` (`Pane/SidebarPaneInlineControls.cs:190`) — and its `EditableText` shape (13 px, `ShowDeleteButton`, `Icons.Search` 14 left affix). |
| 10 | 2 / W23 · 5 | missing | **The seam's resist/fade/snap ladder had no home at all.** `Splitter.Create(..., FadeStart 240, FadeDistance 64, ForcePush 64 ⇒ collapse at 176, ReExpand 190, Min 180, Max 460, CompactWidth 56, ShowIndicator false)` plus the `_sidebarFade` content-opacity cue (`WaveeShell.cs:124,1029,1228-1237`). Added as W23 + two §5 rows + parity item 64. §5 previously said only "snapped 1:1". |
| 11 | 2 / W23 | missing | Every responsive number the wireframe widths sit inside: tier enters 1400 / 1800, **24-DIP shrink hysteresis**, drawer band **720 enter / 760 leave** (the chapter said "≤ 720" only), drawer width `min(max(240, preferred), viewport − 32)`, clamp [180,460]. Added as a table + parity items 65-66. |
| 12 | 2 / W11 | missing | **V3's pin band is conditional**: `(HasPins ‖ DragInFlight) && !Drilled && !Searching` (`LibraryV3Document.cs:297`, fed by the one `UseDragState()` in `LibraryV3Chrome.cs:52-59`). With zero pins it is absent until a resource drag starts. Added + parity item 71. |
| 13 | 2 / W11 | missing | V3 has **four** row shapes, not one: `CompactList` is Compact density (32-DIP rows, 20-DIP art, no subtitle) and `CompactGrid` drops the min cell to **84** vs Grid's 116 (`LibraryV3Document.cs:164-176`, `LibraryV3Metrics.cs:79`). Added as a table + parity item 72. |
| 14 | 2 / W15 | missing | Three degraded-state facts: a failure with NO rows is the full `ErrorState.Build`, not an `EmptyState.Compact` (`LibraryV3Chrome.cs:165-167`); the echoed query is truncated at **24 chars + "…"** (`:171-172`); and the exact gate is `Failed && rows > 0` for the banner vs `rows == 0 && !anyPending && load != Pending` for the state. Parity items 73-74. |
| 15 | 2 / W12 | missing | Two self-correcting V3 states: a query RESETS the drill level (`LibraryV3Chrome.cs:79-82`) and a vanished drill target POPS the stack (`:69-73`). Parity item 75. |
| 16 | 3 | missing | V3 chip hover/pressed fills (`AccentSecondary`/`AccentTertiary` vs `FillControlSecondary`/`Tertiary`), `FocusVisualMargin (2,2,2,2)`, `Role = RadioButton`, and the ✕'s `sidebar.v3.clearFilters` tooltip (`LibraryV3Chips.cs:245-301`). The chapter gave only the two resting fills. |
| 17 | 3 | missing | The V3 header's library mark is `""` in the **`Segoe MDL2 Assets`** face, named explicitly, 16 px in a 32-DIP box with a 6-DIP right margin (`LibraryV3Header.cs:69-79`). Reading the codepoint against the default Segoe Fluent face renders tofu. Parity item 76. |
| 18 | 2 / W22 | missing | The destination-rail count requires `LibraryStore.Stats.State == Ready` and exists for only four of the five words (Local files never) — `LibraryV3NavBand.cs:330-336`. |
| 19 | 3 | missing | The entity card's own DISABLED state (`Opacity 0.55`, `IsEnabled false`, no menu) and the play button's `Icons.Pause` swap + pinned opacity 1 while playing (`Pane/SidebarPaneSlot.cs:892-928`). Parity item 79. |
| 20 | 3 | missing | The section header's long-title treatment — `Shrink 1`, `MinWidth 0`, `MaxLines 1`, `CharacterEllipsis` (`Shared/SidebarSectionHeader.cs:104-108`, issue #84): the one row family that used to push past the pane. Parity item 78. |
| 21 | 4 | missing | What actually separates the pane from content: the CONTENT region's 1-DIP `StrokeCardDefault` on LEFT + TOP only, drawn as one ring on an over-sized clipped box because the engine has no per-side thickness (`WaveeShell.cs:151,161-176`). §4 said "no fill" without saying what supplies the seam. |
| 22 | 5 | missing | The **cross-depth** pill flight is a different animation: `Linear` on both channels, ScaleY `1→0`/`0→1` anchored at the facing edge, and **no opacity leg** — the opacity fade is the same-depth arm only (`NavigationSelectionMotion.cs:42-67`). §5 folded both into one row. |
| 23 | 8 | missing | `SidebarNavPreview` (`Data/SidebarNavPreview.cs`, 56 lines, `SidebarNavPreviewTests`) — an assigned source that appeared nowhere in the chapter. It is the pure rule that makes a sidebar click paint the destination header on frame one. Added as a §8 row (§ NAV). |
| 24 | 8 | missing | The planner's row CAPS: `SectionRowCap` 5 000 (hand-authored only) vs `DynamicSectionRowCap` **20 000** (projected — a 10k library must plan in full), and `SkeletonRows` 3 (`Data/SidebarRowPlanner.cs:181-199`). Parity item 77. |
| 25 | 9 | missing | Section rhythm has **three** suppressions, not two — index 0, after a `Divider`, **and after a bare `HeaderLabel`** (`Pane/SidebarPaneSlot.cs:230-238`). The chapter's §10 item 2 said "except the pane's first row and a row after a divider". |
| 26 | 9 | missing | The empty row has **four** arms; the chapter's tree listed three. `HideBody ⇒ Blank` is the arm that keeps V3's actionable state the only empty message on screen (`Pane/SidebarPaneSlot.cs:1043-1090`). |
| 27 | 9 | missing | `WAVEE_RAIL_BASELINE` (`WaveeShell.cs:140`) — a third env flag, and the one that changes this surface's flagship motion (it swaps the 300 ms Reveal for a `Reflow`/`ControlFast` baseline). Added to "Do NOT port". |
| 28 | 9 | missing | Two more dead shapes verified by grep: `SidebarRowSpec.Caption` (a third text line the builder supports and nothing sets — `Shared/SidebarEntityRow.cs:107,405-413`) and `SidebarDesignPicker`'s `comingSoon` arm (no call site passes true). |
| 29 | 2 / W21 | missing | The picker's two other forms — `Variant.Rows` (`WaveePicker.WideRow` 480×100 with a 120×84 thumbnail, the setup wizard) and `Metrics.Stage` (a 112-wide live preview) — plus the fact that the picker **applies LIVE** (the pane behind the scrim changes on every card click) and the follow-up row carries an explanatory line, not just two buttons (`SidebarDesignPicker.cs:171-223,415-434,490-530`). Parity item 80. |
| 30 | 2 | unverified→pinned | Classic's Tools section: the chapter named the item `api-console`; the spec is `StaticLinks` with `Links with {ShowInRail:false}` carrying the dev-tools route under `IconOverride "Code"`, and the divider is emitted WITH the section, never alone (`Pane/SidebarBuiltInDocuments.cs:110-128`). Also pinned: Pinned is emitted even with zero pins so the layout-menu host always exists. **`api-console` itself is DELETED, plan §9.6 Q7, 2026-09-12 — this row records what 0.2.9's `SidebarBuiltInDocuments.cs` does, struck as a live 0.3 destination.** |
| 31 | 2 / W24 · 3 · 5 · 6 · 9 · 10 | missing | **critic-fix: the "Move to folder…" destination picker had no wireframe anywhere.** §6 listed the menu row that opens it and §8 cited `RootlistFolderPickerTests`, but neither this chapter (W1–W23) nor ch. 26 (W1–W21, whose pickers are the customizer's) DREW the dialog — and it is, by its own header, the third non-mouse route into the rootlist (`RootlistFolderPicker.cs:14-30`), i.e. the keyboard counterpart to tree drag. Added **W24** (the `ContentDialog` card, the pinned `Top level` row, the depth-indented folder list, the filtered-empty arm, and the legality/snapshot/commit table), **seven §3 token rows** (card 320 · panel 320/gap 4 · search 300×32 · row 40/icon 18/pad 6+12·d/gap 10 · list `MaxHeight 360`, gap 2, `EdgeCues.None` · empty line 44), **two §5 rows** (the dialog's open 1.05→1.0 / 250 ms + 83 ms and close 1.0→1.05 / 167 ms + 83 ms, spline `(0,0,0,1)` — `ContentDialog.cs:21-24`), a **§6 block** (the menu rows OPEN the picker; a row is the only affirmative action, `PrimaryText = ""`) plus **three §6 keyboard rows** (Tab cycles in the focus trap · Enter = the default CLOSE button, not a commit · Escape = `None`; initial focus on the button, not the field), **two §9 traps**, the picker's 189 lines in the at-risk list, and **parity items 81-82**. |
| 32 | 2 / W24 | wrong | The finding that prompted row 31 called the dialog "460 default width". It is not: the picker never passes `DialogWidth` and shows ONE button, so `cardW = clamp(320, [320,548])` = ContentDialog's floor 320 (`ContentDialog.cs:110,283`); the panel inside declares its own `Width = 320` with a 300-wide field (`RootlistFolderPicker.cs:153,163`). **460 is the sidebar seam's `NavPaneMaxW`** (W23), a different surface entirely. Recorded as a §9 trap so the rebuild measures rather than inherits the wrong number. |
| 33 | 2 / W24 | overclaim | The same finding asked for an "empty / no-legal-destination arm". There are TWO different things and only one of them is drawn: the dialog **never opens** with no legal destination (`Open` returns at `:48,58`, and the menu row is absent by `RootlistTreeNav.HasDestinations`, `Data/RootlistTreeNav.cs:130-147`), so the only empty state is the FILTERED one — "No folders yet", reachable solely when the never-filtered `Top level` row is itself absent (`TryTopLevelAnchor` false — `:120,175-183`). W24 draws that arm and states the other as a non-state. |

**Verified correct, no change** (spot-checked with file:line, listed so a later reader does not re-audit them): the
21-DIP art lane and its whole decomposition · every row height (32/40/44/48) and art size (20/32/40) · `TreeContentX`
13/25/37/49/61 · the 3×16 pill at radius 1.5 with `TransformOriginY 0` · `MotionTok` 83/150/167/333 and the four
easings (`FluentStandard`, `FluentPopOpen`, `FluentDisclosureCollapse` `(1,1,0,1)`, `FluentDisclosureChevron`
`(.167,.167,0,1)`) · the springs 0.40/0.90, 0.40/0.85, 0.45/1.0 · `NavigationSelectionMotion` 600 ms / 0.333 /
`|Δ|/16 + 1` · the drop bands `clamp(0.30h, 10, 16)` capped at h/2 with 4-DIP depth hysteresis, line 2 / radius 1 /
dot 6, `LineWidth = contentWidth − TreeContentX(d) − 8` · the rail's 40/36/6/8/24×1/A 0.30/accent@0.35 · counts 11 px
tertiary and the 20×12 plate · the pin drop zone 56→72 with dashes 4/2 · the edit card 44/2/24/13/12/10 and its
`Move up · Move down · ─ · Hide/Show · Duplicate · ─ · Remove` menu · the empty pane's 24/14/12/32/13 and its
(16,24,16,24) · V3's 30/44/36/40/32 chrome stack, 13.5/14 words, 28-DIP chips at pad 12, the 300/280/320 thresholds,
`OpenWidth` = 171 at 240, and `Columns` / `ClampColumns [2,4]` · the quick layout menu's exact rows · every loc key §6
lists (all present in `assets/loc/en-US.json`) · every test class §8 names (all present in `Wavee.Tests`).

**arbitration 2026-09-12:** one sidebar file set and one platform total (A4). This chapter and ch. 26 gave different
file names, different per-file numbers and a ~5,000-line gap; the settled answer is **ch. 26's total and file set
plus this chapter's `Sidebar.Doc.cs`** — `Shell/Sidebar.cs` (CORE) · `Shell/Sidebar.Doc.cs` (document + reducer +
wire) · `Shell/Sidebar.UI.cs` · `Shell/Sidebar.Customizer.UI.cs` · `Shell/Sidebar.Host.cs`, **owner J, Wave 4, the
customizer sequenced LAST**. The header, §1.2's file legend, §9.3.5, §9.3.7 and §9's budget table were rewritten to
it. Numbers: the platform is **23,000–25,000** (ch. 26's band), itemised **5,000 / 4,000 / 7,500 / 4,500 / 2,500 =
23,500**, where ch. 26's ~9,000 `Sidebar.cs` is split into `Sidebar.cs` ~5,000 + `Sidebar.Doc.cs` ~4,000 — and the
4,000 is grounded: `Features/Sidebar/Persistence/**` 1,491 + `Wavee.Core/Sidebar/**` 2,526 = 4,017 (both re-counted
against 0.2.9 for this pass, along with `Features/Sidebar/**` 29,025 in 92 files and `Modes/**` 3,664 in 14 files
recursive). This chapter's own share is ~10,600 of the 23,500 and ch. 26 §9.4 states the other ~12,900 the same way,
so no line is counted twice. Withdrawn from this chapter: the 18,000–20,000 estimate and `Sidebar.UI.cs` ~9,000 /
`Sidebar.cs` ~6,500 (the plan/diff/geometry/resolve halves of `Pane/**` and `Modes/**` are not UI and land in
`Sidebar.cs`). Nothing about the renderer, the rows, the rail or the three designs changed.

**answers 2026-09-12: Q7 (plan §9.6) reaches this chapter twice.** Classic's Tools section (§2 W1, `StaticLinks` with `ShowInRail:false`) no longer carries a dev-tools destination — `api-console` is DELETED with the API console and its four `ApiDebug*` helpers — and audit row 30 is annotated to say so; both places are struck with the reason and date, not removed, so the chapter still records what 0.2.9's `SidebarBuiltInDocuments.cs` painted. No wireframe, token or motion row changed.
