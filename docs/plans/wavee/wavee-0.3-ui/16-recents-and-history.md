# Recents and History pages - 0.3 visual fidelity contract

> **0.2.9 sources:** `src/apps/Wavee/Features/Recents/RecentsPage.cs` (2,499 lines) · `src/apps/Wavee/Features/Recents/RecentsView.cs` (687 lines) · `src/apps/Wavee/Features/Shell/HistoryPage.cs` (616 lines, of which :1-146 is `HistoryStore`, the nav log itself) · consulted: `src/apps/Wavee/App/PlayLogStore.cs` (455), `src/apps/Wavee/App/PlayRecency.cs` (74), `src/apps/Wavee/App/ActivityUndo.cs` (144), `src/apps/Wavee.Core/Library/RecentsList.cs` (the wire model), `src/apps/Wavee.Tests/RecentsViewTests.cs` (1,009)
> **0.3 target: Settled in §2.** Recents is **`Entities/Recents.cs` (CORE, 650) + `Entities/Recents.UI.cs` (UI, 550) + `Entities/Recents.Page.cs` (UI, 1,150)** — a synthetic-parent feed exactly like `Home.cs/Home.UI.cs/Home.Page.cs` — owner **P**, **Wave 5**. History is folded into `Shell/Shell.cs` (CORE: the log, kinds, filters, grouping, the play log), `Shell/Shell.Host.cs` (SHELL: `history.json` / `play-log.json` / `play-recency.json`) and the named partial **`Shell/+Shell.History.UI.cs`** (the `history` page, 400 lines) — owner **I**, **Wave 4**. See §9 for why `User.Page.cs` is the wrong home.
> **Recents: Wave 5** (the entity-UI/pages wave), owner **P** (who already owns `Home.*` and `Search.*` — Recents shares Home's wash publication, Home's masthead stagger rung and Home's page gutter). **History: Wave 4, owner I** (Shell) — not Wave 5.

---

## 0. The non-negotiables

Ordered by what a user notices first if it is missing.

1. **The masthead is one oversized light display word over one thin grey line, and the two arrive 45 ms apart.** `WaveeType.SurfaceDisplay` = `Ui.TitleLarge` 40/52 in *Segoe UI Variable Display* at **Weight 400**, tracking **−12/1000 em** (`WaveeType.cs:146-151`), over a `Ui.Caption` 12/16 `Tok.TextTertiary` summary line, container `Stagger = 45` (`RecentsPage.cs:64`, `WaveeMotion.cs:75`). A bold 600 title, or both lines arriving together, is a different page.
2. **The summary line never reflows.** `MinWidth = 220` reserves it (`RecentsPage.cs:59`, `:490`) because this engine has no tabular-figures seam. The count and the day-window resolve *inside* a fixed box; the "Overview" button beside the title does not shuffle.
3. **Four fixed pivot tabs, always all four, on frame one.** All / Music / Podcasts / Artists in `WaveeType.PivotLabel` (19/25, **SemiLight 350**, display face, tracking −6, `WaveeType.cs:219-226`). A tab with zero matching rows is `Tok.TextDisabled` and inert — **never hidden** (`RecentsPage.cs:530-537`, `:546`). Only the selected tab *mounts* a 2-DIP underline, so the underline plays a real `scaleX 0→1` enter over 260 ms (`RecentsPage.cs:552-566`).
4. **The whole 1,708-row grouped list is one measured virtual list with a pinned day band.** Rows are 64 DIP, day headers 48 DIP, and the in-list rows are band-clipped at exactly 48 with a 24-DIP feather so a row scrolls *under* the pinned header instead of showing through it (`RecentsPage.cs:1202-1203`, `DetailVerticalLayout.cs:92`).
5. **The pinned day header is pushed out by the next one.** `push = min(0, offsetOf(nextHeader) − scrollY − 48)`, quantized to 2 DIP (`RecentsPage.cs:1283`, `:1383-1384`). Two day words never cross; the outgoing one slides up under the incoming one.
6. **A day header is `label ——————— N items`** — a 20/28 display-face `ModuleHeader`, a 1-DIP `Tok.StrokeDividerDefault` rule that eats the slack, and a right-aligned tertiary count (`RecentsPage.cs:1423-1436`). Hovering the label eases its ink to the page's live accent over 83 ms; it is also the zoom-out affordance.
7. **The page's accent follows the viewport, per DAY, and glides.** Every accent consumer on the page (pivot underline, day-header hover ink, drawer spine, calendar heat, current-month title, "today" numeral) is a *bound* `Prop`, cross-fading over `BrushTransitionMs = 250` (`RecentsPage.cs:68`). It re-derives once per **section crossing**, never per row or per scroll frame (`RecentsPage.cs:1288-1301`, `:2471-2497`).
8. **The shell's Mica wash is this page's own content.** Recents publishes exactly ONE leg (Hero) of `HomeWash` from the same row the accent grades (`RecentsPage.cs:315-330`, `:2435-2460`). The chrome around the window carries the colour of whatever day you are looking at.
9. **Expanding a group row is an accordion — exactly one drawer open** (`RecentsPage.cs:86-89`), opening on a 333 ms `FluentPopOpen` size reflow with the content fading and dropping *inside* a stationary clip window, children staggered 40 ms each capped at 8 (`RecentsPage.cs:1598-1622`, `:1828`).
10. **The drawer hangs off a 1-DIP accent spine** indented 32 DIP from the row's left edge, its children a further 24 in (`RecentsPage.cs:1844-1866`). This is the one structural-looking element the accent budget lets carry accent (`WaveeTokens.cs:26`).
11. **The right edge of the list is a 44-DIP annotated rail** with abbreviated month labels ("Aug", "Aug 24" on a year change), a 3×3 tick per day, a 30×3 accent thumb, and a hover flag that names the day under the pointer plus "Jump to 12 Sep" when that day has a header (`RecentsPage.cs:789-833`, `:1046-1047`, `AnnotatedScrollBar.cs:190-196`).
12. **Zooming out is a real semantic zoom, not a route change.** Both views stay mounted (`Flow.KeepAlive`, MaxEntries 2); the overview enters from `scale 1.08` while the list recedes to `0.94`, both cross-fading on the standard spring `FromResponse(0.35, 0.85)` (`SemanticZoom.cs:238-241`, `MotionRecipes.cs:214-226`). Escape zooms back in.
13. **The calendar is a heat-map of exact, never-stretchy cells.** 38 × 32 with a 4-DIP gutter, a 290-DIP month card, `weeks` rows — **never a fixed 6** (`RecentsPage.cs:839-858`, `:966-977`). A stretchy cell turns the heatmap into ragged bars that say nothing.
14. **The heat ramp is logarithmic and legended.** `level = clamp(ceil(ln(1+plays)/ln(1+max) × 5), 1, 5)` (`RecentsView.cs:514`), painted as the accent ink at `α = A_subtle + (1 − A_subtle)·level/5`, with a five-swatch "Quieter → Busier" key built from the *same* function (`RecentsPage.cs:1060-1095`).
15. **History is card-grouped, not a flat list**: an eyebrow + rule + count-pill header over a rounded `FillCardSecondary` card with hairline dividers inset past the 36-DIP icon column, rows 56 DIP tall (`HistoryPage.cs:331-365`, `:483-504`). A dead route stays in the log at **opacity 0.6**, inert, with its delete button still live (`HistoryPage.cs:414-418`, `:487-492`).
16. **The now-playing row is inked in `Tok.AccentTextPrimary`, not highlighted.** `TrackRow.StateOf(bridge, lib, track)` drives a title recolour on the single-play and drawer arms (`RecentsPage.cs:2022-2027`), and the group card's cover swaps its hover FAB for the shared now-playing overlay (`MediaCard.cs:982`). This is the reason the page *provides* `WaveeAccentCtx.Slot` at all (`RecentsPage.cs:418-420`): the equalizer and `TrackRow`'s number cell read the page's viewport-following accent through the context instead of knowing they sit in Recents. A now-playing row that looks like every other row is a different page.
17. **Nothing on either page is ever removed to make room** — a disabled pivot, a zero-count day header, a chevron-less group row and an unreachable History entry all keep their full geometry. The chevron-less group row reserves a **24 × 24** box where the chevron would be (`RecentsPage.cs:1669`), the zero-count day header renders an empty caption rather than dropping the node (`:1433`), and the pinned band goes to **`Opacity 0`** rather than un-mounting (`:1459-1461`). "Evidenced or dimmed, never removed" is the page's whole layout contract.

---

## 1. Anatomy

### 1.1 — 0.2.9 composition (Recents)

```
RecentsPage : Component                                      Features/Recents/RecentsPage.cs:44   the page; no ctor deps
│   Ctx.Provide(WaveeAccentCtx.Slot, _accent, page)          :420                                 publishes the live accent
├─ BoxEl page (col, Focusable, OnKeyDown Esc→ZoomInTo(-1))   :405-416                             Esc leaves the overview
│  ├─ Hero()                              BoxEl col Gap 4    :454-501   Stagger 45
│  │   ├─ BoxEl row Gap 12
│  │   │   ├─ WaveeType.SurfaceDisplay(Loc home.recents)     :467-473   Enter Dy10/op0, StandardEnter
│  │   │   └─ Button.Create(recents.overview, Subtle, Small, glyph Calendar)  :474-475
│  │   └─ Caption(RecentsView.Summary(...))                  :485-493   TextTertiary, MinWidth 220
│  ├─ BoxEl pad(36,0,36,8) → PivotTabs                       :358, :378   Key "recents-pivots:"+shapeEpoch
│  │   └─ PivotTabs : Component  (page-local, reads _chip/_shape live)   :510-577
│  │       └─ 4 × Tab(token,label,available,selected)        :542-576
│  │           ├─ WaveeType.PivotLabel(label)                :544-548
│  │           └─ underline BoxEl h2 r-Full Fill=Prop(accent.Fill)  :552-566   (selected only → real mount)
│  ├─ BoxEl grow pad(36,0,36,16)  Layout=ContentResize FLIP  :380-397
│  │   └─ Skel.Region(recents.Loadable, shimmerSource: PendingContentSource, reveal None)  :365-373
│  │       ├─ pending  → PendingContentSource()              :427-449   real builders over _pendingShape
│  │       ├─ empty    → EmptyState.Build(sidebar.section.emptyRecents)  :372
│  │       ├─ failed   → ErrorState.Build(err, onRetry)      :373
│  │       └─ content  → RecentsSemanticSurface              :368-369   Key "recents-semantic:"+token+":"+shapeEpoch
│  │            └─ SemanticZoom.Create(slots, options)       :685-695
│  │                ├─ ZoomedIn  = BoxEl row Gap 12          :662-682
│  │                │   ├─ col grow → RecentsListSurface     :658-659
│  │                │   │   └─ BoxEl ZStack clip             :1214-1219
│  │                │   │       ├─ ItemsView.CreateBound<RecentsFlatItem>  :1172-1210
│  │                │   │       │   RepeatLayout.Measured(GroupedListVirtualLayout)
│  │                │   │       │   Overscan 6 · ContentType per row kind · OnVisibleRange → hydration pump
│  │                │   │       │   Scroll: AutoEdgeFade, SuppressScrollBar, ItemClipTopInset 48, fade band 24
│  │                │   │       │   Entrance: StaggerColdRealize
│  │                │   │       │   └─ RecentsRowSlot : Component (bound slot)      :1479-1503
│  │                │   │       │       ├─ DateHeader kind → DayHeader(dayIndex, overlay:false)  :1396-1439
│  │                │   │       │       └─ Row kind → Embed.Comp(HydratedRecentsRow.Props(...))  :1501
│  │                │   │       │            └─ HydratedRecentsRow : Component      :1505-1539
│  │                │   │       │                Skel.Region(facts, reveal FadeOnly, smoothResize false)
│  │                │   │       │                └─ RowContent(row, facts, idx, expanded, …)     :1624-1716
│  │                │   │       │                    ├─ Single arm → TrackRowContent → TrackRow.Grid  :1988-1997
│  │                │   │       │                    └─ Group arm (col)
│  │                │   │       │                        ├─ primary  = MediaCard.Row(plated:false)   :1681-1707
│  │                │   │       │                        │   leadingArtwork = SavedArtwork (Saved only) :1953-1986
│  │                │   │       │                        │   metaContent   = SavedMeta (Saved only)    :1721-1733
│  │                │   │       │                        │   typeChip = KindLabel(kind)                :2087-2096
│  │                │   │       │                        │   trailing = [when · MoreButton · ExpandChevron] :1670-1679
│  │                │   │       │                        │   menu = Menus.CardAttach · drag = CardDrag
│  │                │   │       │                        │   morphKey = MorphKeys.For(...) on first occurrence only
│  │                │   │       │                        └─ drawer (expanded only) → Drawer(row)       :1809-1882
│  │                │   │       │                            BoxEl Animate=DrawerReveal (Size, clip)
│  │                │   │       │                            └─ BoxEl Animate=DrawerPresence (op+pos)
│  │                │   │       │                                └─ row: [1px accent spine | col children]
│  │                │   │       │                                    └─ n × HydratedChildRow            :1884-1912
│  │                │   │       │                                         → ChildRowContent → TrackRow.Grid :1927-1936
│  │                │   │       └─ StickyDayHeader : Component  (overlay)           :1441-1477
│  │                │   │           BoxEl h48, Transform=translate(0, _stickyPush), OnPointerWheel→ScrollBy
│  │                │   │           └─ DayHeader(day, overlay:true)                 :1464
│  │                │   └─ col stretch → RecentsRail : Component  :660-661, :702-787
│  │                │       └─ AnnotatedScrollBar.Create(_scrollController, {Labels, TickOffsets, Height, DetailLabelAtOffset})
│  │                └─ ZoomedOut = CalendarOverviewSurface    :683-684, :860-931
│  │                    ├─ band row Gap 12 pad(8,8,8,0)       :905-926
│  │                    │   ├─ col Gap 2: Caption("dddd d MMMM", 600, TextPrimary) + Caption(CalendarReadout)  :913-923
│  │                    │   └─ Legend(page)  5 × 12×12 r4 swatch between two tertiary captions  :1075-1095
│  │                    └─ ItemsView.Create(months, GridFit(290, 36, MonthCardHeight(MaxWeeks)))  :871-892
│  │                        └─ CalendarMonthCard : Component   :933-1020   Key "recents-month:y:m"
│  │                            ├─ row: ModuleHeader(month) [+accent ink when current] + Caption(meta)  :996-1015
│  │                            └─ col Gap 4: weekday band (7 × 38×20) + WeekCount × (7 × CalendarCell)  :946-977
│  │                                └─ CalendarCell → ToolTip.Wrap(cell, CalendarTooltip)  :1097-1147
│  └─ RecentsAccentBinder : Component (0×0, always mounted)   :401-402, :2471-2497   owns the grading Watch
```

**Page state (five signals + one atomic reference).** `_epoch` (hydration/adoption landed → the realized slots re-render, `:80`), `_shapeEpoch` (grouping/filter rebuilt → keyed remount, `:83`), `_chip` (`:84`), `_expandedRow` (`:89`), `_stickyHeader`/`_stickyPush` (`:94-95`), `_railMeasuredVersion` (`:104`), `_accentDay` (`:110`), `_accent` (`:116`), `_isZoomedOut` (`:117`), `_calendarDay` (`:118`). Everything else that must agree — wire rows, the chip's cut, morph flags, sections, calendar, **and the stateful measured layout** — lives on ONE immutable `Shape` swapped by a single reference assignment (`:133-152`, published in `BuildShape` `:2210-2266`).

### 1.2 — 0.2.9 composition (History)

```
HistoryPage : Component                                      Features/Shell/HistoryPage.cs:151
├─ PageHeader(...)  BoxEl col Gap 12 pad(36,16,36,12)         :539-602
│   ├─ title row Gap 12                                       :551-576
│   │   ├─ Icon(Icons.Clock \uE823, 22, TextPrimary)          :556
│   │   ├─ WaveeType.PageHero(nav.history.title) Grow 1       :557
│   │   ├─ row Gap 8: StatPill(totalVisits, "visits") · StatPill(uniqueRoutes, "unique")  :558-566, :604-615
│   │   └─ Button.Standard(nav.history.clearAll → SettingsShared.Confirm)  :569-574
│   ├─ AutoSuggestBox.Create(placeholder nav.history.searchPlaceholder, grow 1, minHeight 36, r4)  :578-586
│   └─ row Gap 12 margin-bottom 8                             :590-599
│       ├─ SelectorBar.Create(6 filter labels, _filterIndex)  :595
│       ├─ spacer Grow 1                                      :596
│       └─ ComboBox.Create(2 sort labels, _sortIndex, width 160)  :597
└─ ScrollView(col Gap 16, pad(36,12,36,24))                   :249-254
    └─ body:
        ├─ empty   → EmptyState(search, filter)               :522-536   3 copy variants
        ├─ Recent  → DateGroupedList                          :313-329
        │   └─ per bucket: DateGroup(label, count, rows)      :331-365
        │       ├─ header row Gap 8 margin-bottom 4: Eyebrow + 1px rule (margin 4/0/4/0) + count pill
        │       └─ card col r8 FillCardSecondary border1 StrokeCardDefault clip → n × EntryRow
        └─ MostVisited → MostVisitedList (dedup by route)     :368-405
            └─ header row (Eyebrow nav.history.mostVisited + rule) + the same card
                └─ EntryRow(..., visitCount: n, showDivider: true)  :408-505
                    ├─ icon box 36×36 r4  (accent tint for playlists)
                    ├─ col Gap 2: Body(title) + row Gap 4 [kindLabel · "·" · Arg]
                    ├─ TextEl(ts) 12/16 TextTertiary
                    ├─ visit badge (MostVisited, count > 1)
                    └─ delete BoxEl 28×28 r4 opacity .5 → store.Remove(e)
```

### 1.3 — 0.3 tree (proposed)

| 0.2.9 node | 0.3 home | Kind | Inputs | How data reaches it |
|---|---|---|---|---|
| `RecentsPage` | `Entities/Recents.Page.cs` → `Recents.Page : Component` | UI | none (ctor-free, plan §4.13 shape) | reads `Entities.Current.Recents.Changed` + its own signals in `Render` |
| `RecentsSemanticSurface` / `RecentsListSurface` | same file, nested `sealed class` | UI | `(Recents.Page page, string? token, int shapeEpoch)` ctor args | remounted by `Key = "recents-semantic:"+token+":"+shapeEpoch` — a **Key remount**, deliberately |
| `RecentsRowSlot` | same file | UI | `BoundItemScope<RecentsFlatItem>` | **bound slot**: the scope's `Item`/`Index` signals |
| `HydratedRecentsRow` | fold into `RecentsRowSlot` (no hydration in 0.3) | UI | **props record** `(Page, RecentsRow Row, int RowIndex)` re-pushed every render, **no Key** | `Embed.Comp(props, factory)` — the props contract's "same instance, changed data" arm (`RecentsPage.cs:1495-1501`). **Keep this exact shape**: a factory closure freezes the first row and the slot renders it forever (the "Tue/Wed under a Yesterday header" bug, noted verbatim at `:1497`) |
| `HydratedChildRow` | `Recents.UI.cs` static function `Recents.ChildRow(Track, int, long)` | UI | plain args | mounted fresh per drawer open; no props churn |
| `DayHeader` / `StickyDayHeader` | `Recents.UI.cs` static + `Recents.Page.cs` nested component | UI | `(Shape, int dayIndex, bool overlay)` / `Signal<int> _stickyHeader`, `Signal<float> _stickyPush` | **Signals** — the overlay reads `_stickyHeader.Value` and binds `Transform` to `_stickyPush` as a `Prop` (no re-render on push) |
| `PivotTabs` | nested in `Recents.Page.cs` | UI | the page instance (page-local component; reads `_chip`/`_shape` live) | reads signals in `Render`; remounts only on `shapeEpoch` |
| `RecentsRail` | nested | UI | `Func<AnnotatedScrollBarLabel[]>`, `Func<float[]>`, `Func<float, …>` pre-bound in the ctor | `UseMemo` keyed on `(shapeEpoch, MeasuredVersion)` |
| `CalendarOverviewSurface` / `CalendarMonthCard` | `Recents.UI.cs` (cell/card) + nested surface | UI | `(page, monthIndex)` | `Key = "recents-month:"+year+":"+month`; the *accent* reaches every cell as a bound `Prop`, never a re-render |
| `RecentsAccentBinder` | nested, unchanged shape | UI | props `(Page)` | the ONE node that owns the cover-grading `Watch`; publishes into `_accent` |
| `RecentsView` (all of it) | `Entities/Recents.cs` → `static partial class Recents` CORE section | CORE | pure | ported verbatim; see §8 |
| `RecentsList.Group` (wire fold) | `Spotify/Spotify.Decode.cs` → `Spotify.Decode.RecentsPage(bytes, ref Staging)` | CORE | pure | plan §4.6 |
| `IRecentsSource` / `RecentsFetcher` | `Spotify/Spotify.Api.cs` → `Spotify.Api.RecentsPage(...)`, `Spotify.Api.RecentsDiff(rev, …)` | SHELL | — | plan §4.5 planner drives it |
| `HistoryStore` | `Shell/Shell.cs` CORE (`Shell.History`: entries, kinds, filters, grouping, cap 500) + `Shell/Shell.Host.cs` (the JSON read/write) | CORE+SHELL | — | one `Signal<int> Version` as today |
| `HistoryPage` | `Shell/+Shell.History.UI.cs` → `Shell.HistoryPage : Component` | UI | none | `UseContext(Shell.History.Slot)` + `store.Version.Value` |

**Props-freeze map for this surface** (the three mechanisms, and which node uses which):

- **Signal** — `_epoch`, `_shapeEpoch`, `_chip`, `_expandedRow`, `_stickyHeader`, `_stickyPush`, `_railMeasuredVersion`, `_accentDay`, `_isZoomedOut`, `_calendarDay`, plus `_accent` read as a bound `Prop` by 6 consumers.
- **Re-pushed props record** — `HydratedRecentsRow.Props` and `RecentsAccentBinder.Props` only. Equality-coalesced: an unchanged re-push costs no child render, a changed `RowIndex` pushes through immediately.
- **Key remount** — the list (`"recents-list:"+token+":"+shapeEpoch`), the semantic surface, the detail surface, the rail, the sticky overlay, the pivots, each month card. A pivot switch or a snapshot adoption tears down and rebuilds; a hydration landing does not.
- **Context** — `Services.Slot`, `HistoryStore.NavCtx`, `NavPreviewStore.Slot`, `ShellMaterial.Slot`, `PlaybackBridge.Slot`, `LibraryBridge.Slot`, `ActionServices.Slot`, `Overlay.Service` (`RecentsPage.cs:245-249`, `:1520-1523`), and the page's own `WaveeAccentCtx.Slot` *provision* (`:420`).

---

## 2. Wireframes

Scale: ~8 DIP per monospace column. Content-region width `W` = window − sidebar (default 300, `AppSettings.cs:30`) − (right rail open ? railW + 16 splitter : 0). At the default 1180 × 760 window (`Program.cs:214`) with the rail closed, **W ≈ 880 DIP ≈ 110 cols**. The page has **no width tiers of its own** — `SingleRowTracks` is `static` precisely because "this surface has no width tiers" (`RecentsPage.cs:1999-2000`). The only width-driven reflow is the calendar grid (W12).

### W1 — Recents, cold / skeleton @ 880

`Skel.Region(shimmerSource: PendingContentSource)` renders the REAL day-header and row builders against 8 seed rows stamped 1 minute apart off the same local clock that grouped them, then the engine derives shimmer bars from that tree. The stateful virtual list does **not** mount while pending (`RecentsPage.cs:365-373`, `:427-449`, `RecentsView.cs:282-291`).

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│←36→                                                                                                        │
│                                                                                     ↑24 (Spacing.XXL)      │
│   Recents                                                    [ 📅 Overview ]                               │  SurfaceDisplay 40/52/400
│   ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁  ← shimmer, 220 reserved                                                           │  Caption 12/16 tertiary
│                                                                                     ↓16 (Spacing.L)        │
│   All      Music      Podcasts      Artists                                                                │  PivotLabel 19/25/350, gap 20
│   ▂▂▂▂                                                                                                     │  underline 2 DIP r-Full
│                                                                                     ↓8                     │
│   ▁▁▁▁▁▁▁  ────────────────────────────────────────────────────────────────────────────────  ▁▁▁▁▁▁       │  h48 day header shimmer
│   ▓▓▓▓  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁                                       ▁▁▁▁▁   ▁▁▁   ▁▁            ▓▓ │  h64 row shimmer
│   ▓▓▓▓  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁                                                 ▁▁▁▁▁   ▁▁▁   ▁▁            ▓▓ │   48 art, 12 gap
│   ▓▓▓▓  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁                                          ▁▁▁▁▁   ▁▁▁   ▁▁            ▓▓ │
│   ▓▓▓▓  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁                                               ▁▁▁▁▁   ▁▁▁   ▁▁            ▓▓ │
│   ▓▓▓▓  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁                                     ▁▁▁▁▁   ▁▁▁   ▁▁            ▓▓ │
│   ▓▓▓▓  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁                                                 ▁▁▁▁▁   ▁▁▁   ▁▁            ▓▓ │
│   ▓▓▓▓  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁                                            ▁▁▁▁▁   ▁▁▁   ▁▁            ▓▓ │
│   ▓▓▓▓  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁                                                   ▁▁▁▁▁   ▁▁▁   ▁▁            ▓▓ │
│                                                                                                            │  no rail while pending
│←36→                                                                                                 ←36→   │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Exactly **one** "Today" bucket: the seed rows are stamped `now.AddMinutes(-i)` off `DateTimeOffset.Now` taken in the page constructor (`RecentsPage.cs:226-227`), never `UtcNow` — a UTC instant read back through a local offset splits one skeleton Today into two near local midnight (`RecentsView.cs:278-281`, pinned by `RecentsViewTests.BuildSections_PendingSeedShape_IsASingleTodayHeader_NeverJuly`).

### W2 — Recents, reveal in progress @ 880

`SkelReveal.None` + `EntranceOptions { StaggerColdRealize = true }`: the shimmer's exit is floored at 400 ms (`SkeletonRegion.cs:20-24`, `Expressive.Slow`) and cross-dissolves under real rows that **materialize top-down over frames** — one batch per frame, budgeted at 600 fresh scene nodes / 3.3 ms of walk (`ColdRealizeRamp.cs:39-46`). Not an authored per-row delay: 1,708 authored delays is a bug, not a choreography (`RecentsPage.cs:60-61`).

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│   Today   ──────────────────────────────────────────────────────────────────────────────────   14 items   ││ real
│   ▣▣▣▣  Discover Weekly                                                     Playlist   14:22  ⋯  ›        ││ real
│   ▣▣▣▣  Blonde                                                                 Album   13:58  ⋯  ›        ││ real
│   ▣▣▣▣  Frank Ocean                                                           Artist   13:10  ⋯           ││ real  (fading in)
│   ▓▓▓▓  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁                                                ▁▁▁▁▁  ▁▁▁  ▁▁                ││ shimmer still under
│   ▓▓▓▓  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁                                           ▁▁▁▁▁  ▁▁▁  ▁▁                ││
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Per-row hydration reveal is separate and **fade-only, no resize**: `Skel.Region(facts, reveal: SkelReveal.FadeOnly, smoothResize: false)` (`RecentsPage.cs:1534-1537`). A row's geometry is final from frame one; only its ink arrives.

### W3 — Recents, loaded, at the top @ 880

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                                                                              ↑24           │
│   Recents                                                    [ 📅 Overview ]                               │ title row gap 12
│   1,708 items · 4 Aug – Today · grouped from 9,446 plays                                                   │ Caption 12/16 tertiary
│                                                                                              ↓16           │
│   All        Music        Podcasts        Artists                                                          │ gap 20 (Spacing.XL)
│   ▂▂▂                                                                                                      │ 2 DIP accent underline
│                                                                                              ↓8            │
│ ╔══ PINNED OVERLAY (h48, FillLayerDefault, HitTestPassThrough) ═════════════════════════════════════╗ ┌──┐ │
│ ║ Today  ────────────────────────────────────────────────────────────────────────────   14 items    ║ │Se│ │ ← 44 rail
│ ╚════════════════════════════════════════════════════════════════════════════════════════════════════╝ │p │ │
│  ┌────┐                                                                                              │ │▬▬│ │ thumb 30×3
│  │ ▨▨ │  Discover Weekly                                          [Playlist]    14:22   ⋯      ›     │ │ ·│ │ tick 3×3
│  │ ▨▨ │  Spotify · Played 23 tracks                                                                  │ │ ·│ │
│  └────┘  ← 48 art, r4                                                                        h64     │ │ ·│ │
│  ┌────┐                                                                                              │ │Au│ │
│  │ ▨▨ │  Blonde                                                      [Album]    13:58   ⋯      ›     │ │g │ │
│  │ ▨▨ │  Frank Ocean · Played 11 tracks                                                              │ │ ·│ │
│  └────┘                                                                                              │ │ ·│ │
│  ( ●● )  Frank Ocean                                                [Artist]    13:10   ⋯            │ │  │ │ circular art, no chevron
│  \    /  Played 4 tracks                                                                             │ │  │ │
│  ┌────┐                                                                                              │ │Ju│ │
│  │ ♥  │  Liked Songs                                                              12:40   ⋯      ›   │ │l │ │ no type chip
│  │    │  Played 31 tracks                                                                            │ │  │ │
│  └────┘                                                                                              │ └──┘ │
│   Yesterday ───────────────────────────────────────────────────────────────────────   9 items        │      │ inline h48 header
│ ←36→                                                                                        ←12→ 44 ←36→    │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Row internals (`MediaCard.Row`, `plated: false`, `MediaCard.cs:1039-1062`): `Direction 0, Height 64, AlignItems Center, Gap 12, Padding (8,0,8,0), Corners r4, Fill transparent, HoverFill Tok.FillSubtleSecondary, PressedFill Tok.FillSubtleTertiary, BorderWidth 0`. Children: 48×48 cover stack (r4, or `art/2` when `circular`) · text column (`Gap 2`: `TrackTitle` 14/20/600 + a 12px `RichText` subtitle in `Tok.TextSecondary`) · type chip · trailing cluster.

**The trailing cluster is built conditionally** (`RecentsPage.cs:1670-1679`), and every omission still holds its width:

| child | present when | absent ⇒ |
|---|---|---|
| played-at `Caption` | `RecentsView.PlayedAt(row.PlayedAtMs, …).Length > 0` — i.e. the wire gave an instant (`:1671-1672`) | the caption node is simply not added; the `Gap 12` closes up |
| `TrackRow.MoreButton(true)` | `CardMenu(...)` returned non-null — needs `ActionServices` **and** `Overlay.Service` **and** a non-empty uri (`:1673`, `:2049-2053`) | no "…" at all |
| chevron | `RecentsView.CanExpand(row)` (`:1664`, `:1667-1669`) | a **24 × 24 transparent spacer** (`Spacing.XXL`), never a collapsed lane — so an artist row's "…" sits in the same column as an album row's |

The **type chip** is `KindLabel(kind)` and is omitted entirely for `Collection` (Liked Songs — the title already says it) and `Unknown` (`:2087-2096`); those rows have no capsule and the text column grows into the slack.

### W4 — Recents, scrolled, sticky push @ 880

The next day's inline header has reached the pinned band. `push = min(0, offsetOf(nextHeader, viewportW) − scrollY − 48)`, quantized to 2 DIP; the overlay translates by it, so "Today" rides up and off while "Yesterday" arrives underneath (`RecentsPage.cs:1363-1385`, `:1462`).

```
│ ╔══════════════════════════════════════════════════════════════════════════╗   ← overlay at translate(0, −31)
│ ║ Today  ───────────────────────────────────────────────────  14 items     ║      (17 of 48 still visible)
│  ┌─────────────────────────────────────────────────────────────────────────┐
│   Yesterday ────────────────────────────────────────────────────  9 items      ← the inline h48 header, arriving
│  ┌────┐
│  │ ▨▨ │  Late Night Tales                                [Playlist]   23:41  ⋯  ›
```

Rows scrolling through the band are **clipped, not blended**: `ItemClipTopInset = 48`, `ItemClipTopFadeBand = 24` (`RecentsPage.cs:1202-1203`) — a 24-DIP feather at the top of each recyclable row's clip, so content dissolves into the band edge instead of ghosting through the translucent plate.

### W5 — Recents row, hover @ 880

```
│  ┌────┐                                                                                              │
│  │ ▨▨ │  Blonde                                                      [Album]    13:58   ⋯      ›     │  ← row fill Tok.FillSubtleSecondary
│  │(▶)│  Frank Ocean · Played 11 tracks                                                               │     30-DIP accent play FAB centred on art
│  └────┘                                                                                              │     "…" opacity .45 → 1
```

- Row fill rest → hover: `transparent → Tok.FillSubtleSecondary` (dark `#FFFFFF0F`, light `#0000000F`-class), cross-faded by the engine's `HoverFade` on the brush track.
- The 30-DIP play FAB (`MediaCard.cs:969`, `:982`) reveals off **row** hover, gated by `HoverMotionGate` so a post-navigation stationary re-hover does not light it (`WaveeMotion.cs:141-159`).
- `TrackRow.MoreButton(true)`: 28×28 circle, `Icon(Icons.More \uE712, 16, Tok.TextSecondary)`, rest opacity **0.45**, `HoverOpacity 1`, `HoverScale 1.07 / PressScale 0.92` (`TrackRow.cs:966-985`, `:989`).
- The chevron is `TrackRow.ExpandChevron`: 24×24, r4, `HoverFill Tok.FillControlSecondary`, glyph **swap** `ChevronRight \uE76C → ChevronDown \uE70D` (never a rotation), `Tok.TextSecondary → Tok.AccentTextPrimary` when open, transition `MotionTok.DisclosureChevron` = 167 ms `cubic-bezier(0.167,0.167,0,1)` (`TrackRow.cs:625-644`, `RecentsPage.cs:1668`).

### W6 — Recents group row expanded (drawer open) @ 880

```
│  ┌────┐                                                                                              │
│  │ ▨▨ │  Blonde                                                      [Album]    13:58   ⋯      ⌄     │  parent row, 64
│  │ ▨▨ │  Frank Ocean · Played 11 tracks                                                              │
│  └────┘                                                                                              │
│         ┆                                                                                            │  ← margin left 32, top 2
│    ←32→ ┃ 1  ♡  ▩  Nikes                                                       4:54   14:03   ⋯      │  ┃ = 1-DIP accent spine
│         ┃ 2  ♡  ▩  Ivy                                                         4:09   14:08   ⋯      │  child row h40
│         ┃ 3  ♡  ▩  Pink + White                                                3:04   14:12   ⋯      │  ←24→ inner padding
│         ┃ 4  ♡  ▩  Solo                                                        4:17   14:15   ⋯      │
│         ┆                                                                                    ↓8      │  bottom margin 8
```

Child row grid (`RecentsPage.cs:1914-1935`), tracks `[30 · 28 · 32 · *1 · 52 · 112]`, height **40**:
`#` 30 · ♥ `TrackRow.HeartCol` 28 · art `TrackRow.ThumbSize` 32 · title star · duration 52 · actions 112 = `40 + 12 + ChildWhenCol 60`. With the artwork setting off (`appearance.trackArtwork.hidden`, `AppSettings.cs:71`) the art track drops and the set is `[30 · 28 · *1 · 52 · 112]`.
The actions cell is `[played-at caption · MoreButton]`, right-justified, gap 12 (`:1941-1951`) — the fixed 60-DIP caption lane keeps every drawer row's "…" aligned even when a folded-members fallback entry has no instant.

### W7 — Recents "Saved" row @ 880

A `RecentsReason.Saved` row gets a stacked leading artwork and a green meta line instead of a plain subtitle (`RecentsPage.cs:1655`, `:1692`, `:1721-1733`, `:1953-1986`).

```
│   ┌──┐ ┌──┐                                                                                          │  two 40×40 r4 member covers
│  ┌│▨▨│─│▨▨│─┐                                                                                        │  at x=12 and x=16, y=0
│  ││  │ │  │ │  Liked Songs                                                       11:02   ⋯      ›    │
│  │└──┘ └──┘ │  ✓ 3 songs added · Played 3 tracks                                                     │  Icons.Check \uE73E 12 DIP
│  │  ▨▨▨▨    │                                                                                        │  Tok.SystemFillSuccess, Weight 600
│  └──────────┘  ← 48 context tile at (0, 8);  box = 60 × 56                                           │
```

### W8 — Recents, empty @ 880

`isEmpty: snapshot => snapshot.Rows.Count == 0` → `EmptyState.Build(Loc.Get(Strings.Sidebar.Section.EmptyRecents))` (`RecentsPage.cs:371-372`). **This is the `--fake` state**: `NullRecentsService.FetchAsync` returns `RecentsSnapshot.Empty` (`RecentsList.cs:124`).

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│   Recents                                                    [ 📅 Overview ]                               │  masthead present, summary ABSENT
│                                                                                                            │  (Summary("") when rows.Count==0)
│   All        Music        Podcasts        Artists                                                          │  Music/Podcasts/Artists DISABLED
│   ▂▂▂                                                                                                      │
│                                                                                                            │
│                                                                                                            │
│                              Play something and it'll show up here                                         │  PageHero 28/36/600, centred,
│                                                                                                            │  pad 24, no subtitle, no action
│                                                                                                            │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### W9 — Recents, error / offline @ 880

`onFailed: () => ErrorState.Build(recents.Loadable.Error, onRetry: () => recents.Refresh())` (`RecentsPage.cs:373`; `ErrorState.cs:17-27`).

```
│                              Something went wrong.                       ← common.errorTitle, PageHero
│                              Check your connection and try again.        ← common.errorSubtitle, TrackMeta 12/16
│                                        ↓16 spacer
│                                    [  Retry  ]                           ← Button.Standard, NEVER Accent
```

### W10 — Recents, pivot = Podcasts, Artists unavailable @ 880

```
│   All        Music        Podcasts        Artists                                                          │
│                           ▂▂▂▂▂▂▂▂                                                                         │
│    ↑           ↑              ↑              ↑                                                             │
│  Secondary  Secondary      Primary       DISABLED (Tok.TextDisabled, Focusable=false, Cursor=Arrow,         │
│                            + underline    IsEnabled=false, OnClick=null)                                   │
```

Colour ladder (`RecentsPage.cs:546`): unavailable → `Tok.TextDisabled`; selected → `Tok.TextPrimary`; else `Tok.TextSecondary`. The non-selected tabs still reserve a 2-DIP transparent baseline box so no tab shifts vertically on selection (`:567`).

### W11 — Calendar overview (zoomed out) @ 880 — 2 columns

Columns = `max(1, floor((cross + gap) / (minCell + gap)))` = `max(1, floor((W − 72 + 36) / 326))` (`VirtualLayout.cs:217-220`, `RepeatLayout.GridFit(290, 36, …)` at `RecentsPage.cs:878`). At W = 880 → cross 808 → `floor(844/326)` = **2**.

| content W | cross (W−72) | columns |
|---|---|---|
| ≤ 579 | ≤ 507 | 1 |
| 580 – 905 | 508 – 833 | 2 |
| 906 – 1231 | 834 – 1159 | 3 |
| 1232 – 1557 | 1160 – 1485 | 4 |

No hysteresis: the column count is recomputed from the live cross size each layout.

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│  ←8→                                                                                                 ←8→   │
│  Thursday 11 September                                     Quieter ▪ ▪ ▪ ▪ ▪ Busier                        │  Caption 12/16/600 primary
│  14 plays · top: Blonde · Jump to 11 Sep                   ↑ 5 × 12×12 r4 swatches, gap 4                  │  Caption 12/16 secondary
│  ↑ Grow 1, ellipsised — the readout, never the legend, gives width back                                    │
│                                                                                       ↓16 (Gap Spacing.L)  │
│  ┌─ 290 ───────────────────────────┐  ←36→  ┌─ 290 ───────────────────────────┐                            │
│  │ September            14 plays · so far│  │ August                 318 plays│                            │  ModuleHeader 20/28 + Caption
│  │                                   ↓8  │  │                                 │                            │
│  │  M    T    W    T    F    S    S      │  │  M    T    W    T    F    S    S│                            │  38×20 weekday band
│  │ ┌──┐ ┌──┐ ┌──┐ ┌──┐ ┌──┐ ┌──┐ ┌──┐    │  │ ...                             │                            │  38×32 cells, gutter 4
│  │ │  │ │  │ │  │ │ 1│ │ 2│ │ 3│ │ 4│    │  │                                 │                            │
│  │ └──┘ └──┘ └──┘ └──┘ └──┘ └──┘ └──┘    │  │                                 │                            │
│  │ ┌──┐ ┌──┐ ┌──┐ ┌──┐ ┌──┐ ┌──┐ ┌──┐    │  │                                 │                            │
│  │ │ 5│ │ 6│ │▓7│ │▓8│ │ 9│ │10│ │11│    │  │                                 │                            │  ▓ = density fill
│  │ └──┘ └──┘ └──┘ └──┘ └──┘ └──┘ └──┘    │  │                                 │                            │
│  │  …  WeekCount rows, never 6           │  │                                 │                            │
│  └───────────────────────────────────────┘  └─────────────────────────────────┘                            │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Row-height estimate handed to `GridFit` = `MonthCardHeight(MaxWeeks(calendar))` = `28 + 8 + 20 + 4 + weeks×36 − 4` = **56 + 36·weeks** → 4 weeks 200 · 5 weeks 236 · 6 weeks 272 (`RecentsPage.cs:858`). Seeding from the *tallest* month is load-bearing: a 4-week estimate visibly clipped the bottom week row off every 6-week card until it measured (`:850-856`).

### W12 — Calendar overview @ 1288 content — 3 columns

```
│  ┌─ 290 ──────────┐  ←36→  ┌─ 290 ──────────┐  ←36→  ┌─ 290 ──────────┐                                   │
│  │ September      │        │ August         │        │ July           │      cross = 1216 → floor(1252/326)=3
│  └────────────────┘        └────────────────┘        └────────────────┘                                   │
```

### W13 — Calendar month card, cell detail

```
 ┌─ CalGridW = 7×38 + 6×4 = 290 ─────────────────────────────┐
 │ September                       318 plays · so far        │  h = CalTitleH 28; row AlignItems Center, gap 8
 │                                                           │  title: ModuleHeader, accent Ink when IsCurrentMonth
 │ ↓ Spacing.S = 8                                           │  meta:  Caption tertiary, Grow 1, ellipsis
 │ ┌────┐4┌────┐4┌────┐4┌────┐4┌────┐4┌────┐4┌────┐          │  weekday band: 7 × (38 × 20)
 │ │ M  │ │ T  │ │ W  │ │ T  │ │ F  │ │ S  │ │ S  │          │  Caption 12/16 Tok.TextSecondary, centred
 │ └────┘ └────┘ └────┘ └────┘ └────┘ └────┘ └────┘          │  rotated by culture FirstDayOfWeek
 │ ↓ CalGap = 4                                              │
 │ ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐          │
 │ │    │ │    │ │    │ │  1 │ │  2 │ │  3 │ │  4 │  h = 32  │  lead/trail days = BLANK SPACERS (38×32),
 │ └────┘ └────┘ └────┘ └────┘ └────┘ └────┘ └────┘          │  never dimmed numerals (:1099-1103)
 │ ┌────┐ ┌────┐ ┌▓▓▓▓┐ ┌▓▓▓▓┐ ┌────┐ ┌░░░░┐ ┌▒▒▒▒┐          │  cell: r4 (Radii.ControlAll),
 │ │  5 │ │  6 │ │▓ 7 │ │▓ 8 │ │  9 │ │░10 │ │▒11̲ │          │  Fill = DensityFill(level) — a bound Prop
 │ └────┘ └────┘ └▓▓▓▓┘ └▓▓▓▓┘ └────┘ └░░░░┘ └▒▒▒▒┘          │  11̲ = today: numeral Weight 600 + accent Ink
 └───────────────────────────────────────────────────────────┘  ONE today cue; no dot, no ring, no glow (:1108-1111)
```

The card's meta caption has **three** states, and none of them is "0 plays" (`RecentsPage.cs:982-985`):

| month | meta string | loc |
|---|---|---|
| current month, plays > 0 | `"318 plays · so far"` | `recents.playCount` + `" · "` + `recents.soFar` |
| past month, plays > 0 | `"318 plays"` | `recents.playCount` |
| any month, plays == 0 | `"Nothing played"` | `recents.nothingPlayed` — the same sentence the readout uses |

The title is `WaveeType.ModuleHeader(monthTitle)` as **its own node beside** the caption, deliberately not the `ModuleHeader(title, meta)` span alias: that alias is a `SpanTextEl` whose `Color` is a plain `ColorF` with no `BrushTransitionMs`, so it could not carry the accent **bind** the current month's title needs (`:992-995`).

### W14 — Calendar cell hover / focus

```
   ┌▓▓▓▓┐
   │▓ 8 │ ◀── OnHoverMove → _calendarDay.SetIfChanged(date)  → the band above re-renders
   └▓▓▓▓┘     OnFocusChanged(true) → same; (false) → ResetCalendarDay() → today
      │
      └─ ToolTip.Wrap: "Monday 8 September · 27 plays · top: Blonde · Jump to 8 Sep"
         = date.ToString("dddd d MMMM") + " · " + CalendarReadout(date)   (:1051-1052)

   band re-render:   Monday 8 September
                     27 plays · top: Blonde · Jump to 8 Sep
```

A day with no plays reads **"Nothing played"** — the app's own sentence, never a `0 plays` plural doing arithmetic at the reader (`RecentsPage.cs:1037-1039`, loc `recents.nothingPlayed`). The "Jump to …" clause appears only when `HeaderFlatFor(date) >= 0`, i.e. the flat list actually has a header for that day; otherwise no dead promise (`:1044-1047`). Only a cell with rows is `Focusable`/`TabStop`/`Cursor.Hand`/`AutomationRole.Button` (`:1128-1131`). `OnPointerExit` on the card and on the whole surface resets the readout to today (`:899`, `:989`).

### W15 — Annotated rail detail (right edge, 44 wide)

```
      ┌────44────┐
      │ Sep      │  label: 14 DIP text, absolute at layout.OffsetOf(headerFlat) → rail Y
      │        ▬▬│  thumb 30 × 3, r1.5, Tok.AccentDefault  (NOT the page accent)
      │         ·│  tick  3 × 3, r1.5, Tok.TextTertiary, right-aligned in a 30-wide lane,
      │         ·│         one per DAY header, density-capped to one per (3+1) DIP bucket
      │ Aug      │  label rule: first-seen month only; "MMM yy" when the YEAR changes, else "MMM"
      │         ·│         (full month names ellipsise to "Augu.." at 44 — RecentsPage.cs:800-804)
 ┌────────────┐ ·│
 │ 12 September│  │  hover flag (PartTip): acrylic-fallback plate, MinHeight 40, hangs off the LEADING
 │ 27 plays…   │▬▬│  edge (rail ClipToBounds=false), text = RailDetail(offset) → HeaderLabels[day]
 └────────────┘ ·│  ghost thumb (30×3, Tok.AccentDisabled) sits at the pointer's mapped offset
      │ Jul      │
      └──────────┘   AnnotatedScrollBar.cs:190-196, :560-609, :850-871
```

The list's own scrollbar is suppressed (`SuppressScrollBar = true`, `RecentsPage.cs:1195`) — the rail IS the scrollbar.

### W16 — Recents card menu open (right-click, or "…")

```
│  ┌────┐                                                  ┌──────────────────────────────────┐
│  │ ▨▨ │  Blonde                          [Album]  13:58  │ ▨  Blonde                        │  header: art + name + subtitle
│  │ ▨▨ │  Frank Ocean · Played 11 tracks              ⋯ ──┤    Frank Ocean · Played 11 tracks│
│  └────┘                                                  ├──────────────────────────────────┤
│                                                          │ [▶ Play][⏭ Next][➕ Queue][♡ Save]│  transport strip
│                                                          ├──────────────────────────────────┤
│                                                          │ Add to playlist              ▸   │
│                                                          │ Open                             │
│                                                          │ Pin / Unpin                      │
│                                                          │ Go to artist                     │
│                                                          │ Share                        ▸   │
│                                                          └──────────────────────────────────┘
```

Built by `Menus.CardAttach → Menus.Card` (`Actions/Menus.cs:668-670`, `:544-568`); the grammar (strip + row order, per-kind omissions) is **chapter 01-track-row.md §menus**' subject. Recents contributes only the arguments: `(artUri, facts.Title, facts.Cover, sub, circular: kind == Artist)` (`RecentsPage.cs:1666`). A **single-play** row instead gets `TrackContextMenu.BuildSingle` (`:2045`).

### W17 — History, loaded, Most recent @ 880

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ←36→                                                                                          ↑16          │
│  🕐  History                                              (12 visits) (5 unique)  [ Clear all ]            │  Clock 22 · PageHero 28/36/600
│                                                                                          ↓12 (Gap M)       │  StatPill ×2 · Button.Standard
│  ┌────────────────────────────────────────────────────────────────────────────────────────────────────┐    │
│  │ 🔍 Search history…                                                                                 │    │  AutoSuggestBox, minH 36, r4
│  └────────────────────────────────────────────────────────────────────────────────────────────────────┘    │
│   All   Playlists   Podcasts   Library   Search   Pages                        [ Most recent    ⌄ ]        │  SelectorBar ← → ComboBox w160
│   ▃▃▃                                                                                                      │  pill 16×3 (4×4 scaleX)
│                                                                                          ↓8               │
│ ╌╌╌ scroll region: pad (36, 12, 36, 24), Gap 16 ╌╌╌                                                        │
│  Today ────────────────────────────────────────────────────────────────────────────────  8 visits          │  Eyebrow 12/16/600 +30 tracking
│  ┌────────────────────────────────────────────────────────────────────────────────────────────────────┐    │  card r8 FillCardSecondary
│  │  ┌──┐                                                                                              │    │  border 1 StrokeCardDefault
│  │  │🎵│   Discover Weekly                                                        14:22          ✕    │    │  row h56, pad (12,0,8,0)
│  │  └──┘   Playlist · spotify:playlist:37i9…                                                          │    │  icon box 36×36 r4
│  │         ─────────────────────────────────────────────────────────────────────────────────────      │    │  divider 1, left margin 60
│  │  ┌──┐                                                                                              │    │
│  │  │🔍│   drake                                                                   13:58          ✕    │    │
│  │  └──┘   Search · drake                                                                              │    │
│  │         ─────────────────────────────────────────────────────────────────────────────────────      │    │
│  │  ┌──┐                                                                                              │    │
│  │  │♥ │   Liked Songs                                                             13:10          ✕    │    │
│  │  └──┘   Library                                                                                     │    │
│  └────────────────────────────────────────────────────────────────────────────────────────────────────┘    │
│                                                                                          ↓16               │
│  Yesterday ────────────────────────────────────────────────────────────────────────────  4 visits          │
│  ┌────────────────────────────────────────────────────────────────────────────────────────────────────┐    │
│  │  ┌──┐   Blonde                                                        Yesterday, 23:41         ✕    │    │
│  │  │💿│   Page · album:spotify:album:3mH6…                                                            │    │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Buckets, in order, by `(now.Date − dt.Date).Days` (`HistoryPage.cs:286-297`): `0 → Today`, `1 → Yesterday`, `<7 → This week`, `<30 → This month`, else `Earlier`. Timestamps (`:299-310`): `0 → "HH:mm"`, `1 → "Yesterday, {t}"`, `<7 → "{dddd}, {t}"`, else `"{MMM d yyyy}, {t}"`.

A playlist row's icon box is the one tinted cell: `Fill = Tok.AccentDefault with { A = 0.12f }`, glyph `Tok.AccentDefault`; every other kind is `Tok.FillSubtleSecondary` / `Tok.TextSecondary` (`:429-432`).

**The last row of a Most-recent card carries no divider**: `DateGroupedList` passes `showDivider: k < rows.Length - 1` (`:325`). The group's own column is `Gap 8` between the eyebrow header and the card, and the list of groups is `Gap 16` (`:328`, `:335`). Contrast W18.

### W18 — History, Most visited @ 880

One flat card, deduplicated to the newest entry per route, ordered by descending visit count; every row shows a `{n}×` badge when `n > 1` (`HistoryPage.cs:368-405`, `:461-470`). The dedup runs **after** the sort, over the already-filtered list, keying on `Route.Name` alone (so two different args under one route collapse to one row) and the counts come from a map built over the **full unfiltered** log (`:205-222`, `:371-379`).

Unlike the Most-recent card, **every** row here keeps its divider, including the last — `showDivider: true` unconditionally, with the "just leave it, the card clips" note in place (`:378-380`). Its header also carries **no count pill**, only the eyebrow + rule (`:386-395`). The eyebrow string itself is authored ALL-CAPS in the loc table (`nav.history.mostVisited` = `"MOST VISITED"`), the one place the app's sentence-case eyebrow policy is bypassed by the data rather than by a `ToUpper()`.

```
│  MOST VISITED ──────────────────────────────────────────────────────────────────────────────               │  loc nav.history.mostVisited
│  ┌────────────────────────────────────────────────────────────────────────────────────────────────────┐    │  (no count pill on this header)
│  │  ┌──┐   Home                                                        14:22       ( 7× )        ✕    │    │
│  │  │🏠│   Page                                                                                        │    │
│  │  │  │   ─────────────────────────────────────────────────────────────────────────────────────      │    │
│  │  ┌──┐   Discover Weekly                                             14:22       ( 4× )        ✕    │    │
│  │  │🎵│   Playlist · spotify:playlist:37i9…                                                           │    │
```

### W19 — History, empty (three copies) @ 880

`EmptyState(search, filter)` picks one of three heading/subtitle pairs (`HistoryPage.cs:522-536`):

```
  search non-empty  →  "No results"        /  "No history matches \"drake\""
  filter ≠ All      →  "Nothing here"      /  "Try switching to \"All\" to see your full history."
  otherwise         →  "No history yet"    /  "Start navigating — your history will appear here."
```

Rendered by the same `EmptyState.Build(heading, sub)` grammar: `PageHero` 28/36/600 wrapped, `TrackMeta` 12/16 under it, centred with `Gap 4, Padding 24`, **no glyph, no action** (`EmptyState.cs:33-35`, `:48-66`).

### W20 — History row hover / unreachable @ 880

```
│  │  ┌──┐                                                                                            │
│  │  │🎵│   Discover Weekly                                                     14:22            ✕    │  ← hover: Interaction.Subtle
│  │  └──┘   Playlist · spotify:playlist:37i9…                                                        │    Fill transparent → FillSubtleSecondary
│  │                                                                                                  │    press → FillSubtleTertiary
│  │  ┌──┐                                                                                            │
│  │  │📚│   Your Library                                                        11:02            ✕    │  ← UNREACHABLE: opacity 0.6,
│  │  └──┘   Page · old-route-name                                                                    │    OnClick null, IsEnabled false
                                                                                                          (✕ still live — the row is removable)
```

`reachable = ShellRoutes.IsKnown(e.Route.Name)` (`HistoryPage.cs:418`). `isEnabled` must go **through** `.Interactive(Interaction.Subtle, isEnabled: reachable)` — the recipe writes `IsEnabled` itself and would silently overwrite an initializer value (`:492`, and the comment at `:490-491`).

### W21 — History, Clear-all confirm @ 880

```
                    ┌──────────────────────────────────────────────────┐
                    │  Clear all history?                              │  ContentDialog title
                    │                                                  │
                    │  This removes every entry from your navigation   │  body
                    │  history.                                        │
                    │                                                  │
                    │                        [ Cancel ]  [ Clear all ] │  DefaultButton = Close (Cancel)
                    └──────────────────────────────────────────────────┘
```

`SettingsShared.Confirm(overlay, title, body, primaryText, store.Clear)` (`HistoryPage.cs:569-574`, `SettingsShared.cs:29-41`). Destructive and **unrecoverable** — `HistoryStore.Clear` also deletes `history.json` (`HistoryPage.cs:99-106`).

### W22 — Recents @ 420 content (narrow window)

No tiers: the row grid, the pivot gaps and the 36-DIP page inset are all static. What actually happens is ellipsis + column loss by flex, plus a 1-column calendar.

```
┌──────────────────────────────────────────────────┐
│   Recents                       [ 📅 Overview ]  │  title Grow 1 Basis 0 → ellipsised first
│   1,708 items · 4 Aug – Today · grouped fro…     │  summary MinWidth 220 still reserved
│   All   Music   Podcasts   Artists               │  gap 20 holds; tabs Shrink 0 → may clip
│   ▂▂▂                                            │
│ ╔══════════════════════════════════════════╗ ┌──┐│
│ ║ Today ──────────────────────  14 items   ║ │Se││
│  ┌────┐                                    │ │p ││
│  │ ▨▨ │  Discover Weekly     [Play…] 14:22 │ │▬▬││  type chip Shrink 0; title ellipsises
│  │ ▨▨ │  Spotify · Played 23 tra…          │ │ ·││
│  └────┘                                    │ └──┘│
└──────────────────────────────────────────────────┘
```

### W23 — Recents, now playing @ 880

The page is Wavee's only `WaveeAccentCtx` **provider**, and this is why: the shared now-playing cues read the page's live, viewport-following accent instead of `Tok.AccentDefault`.

```
│  ┌────┐                                                                                              │
│  │ ▨▨ │  Blonde                                                      [Album]    13:58   ⋯      ⌄     │  group card — the COVER carries
│  │(▮▮)│  Frank Ocean · Played 11 tracks                                                              │  the shared now-playing overlay
│  └────┘                                                                                              │  (MediaCard LazyOverlay, :982)
│         ┃ 1  ♡  ▩  Nikes                                                       4:54   14:03   ⋯      │
│         ┃ 2  ♥  ▩  Ivy                                        ◀── now playing  4:09   14:08   ⋯      │  title → Tok.AccentTextPrimary
│         ┃    ▮▮▮        ↑ the # cell becomes the equalizer / pause target (TrackRow)                 │  (:2025); # cell + equalizer
│         ┃ 3  ♡  ▩  Pink + White                                                3:04   14:12   ⋯      │  read WaveeAccentCtx (:418-420)
```

- Title ink: `st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary` — a **recolour, not a row fill** (`RecentsPage.cs:2022-2027`). Both the drawer arm and the single-play arm go through the same `BindTrackRow`.
- `TrackRow.StateOf(bridge, lib, track)` also decides the ♥ state, so a now-playing *and* saved drawer row shows a filled heart; both cues are the track's, neither is Recents'.
- The group card itself has **no** now-playing ink on its title — only its cover overlay changes — because the card stands for a *context*, not for the playing track. Do not add one in 0.3.
- Everything above is `01-track-row.md`'s vocabulary; Recents contributes only the column set and the accent context.

### W24 — Recents, before the first sticky resolve (list at the very top) @ 880

`StickyMetrics` returns `header = -1` when the projection has no header indices, and `StickyDayHeader` then renders a **present but invisible, non-hit-testable** band — it is never un-mounted (`RecentsPage.cs:1368-1376`, `:1453-1461`).

```
│ ╔════════════════════════════════════════════════════════════════════════════════════════════════════╗
│ ║  (Opacity 0, HitTestVisible false — the node stays, 48 DIP, so nothing below it shifts)            ║
│ ╚════════════════════════════════════════════════════════════════════════════════════════════════════╝
│   Today  ──────────────────────────────────────────────────────────────────────────   14 items       │  ← the INLINE header, visible
│  ┌────┐                                                                                              │     (ItemClipTopInset still 48)
```

`BuildShape` re-resolves the band from the live scroll offset immediately after publishing (`:2258-2263`) precisely so a pivot re-cut mid-list never leaves a blank band until the next scroll frame.

### W25 — Degenerate / placeholder frames

Five states that are *shapes*, not messages. Each keeps the exact geometry of the thing it stands in for.

| state | what renders | source |
|---|---|---|
| no `Services` in context | `BoxEl { Grow = 1 }` — a blank page region, no masthead, no pivots | `RecentsPage.cs:258` |
| bound slot outside the live range | `EmptyFlat` sentinel → `RecentsRowSlot` falls through to `BoxEl { Height = 64 }` | `:75`, `:1493` |
| a row whose `ItemId` is empty | `BoxEl { Height = RowHeight }` — 64 DIP of nothing | `:1628` |
| a day bucket whose count is 0 | `Caption("")` — label + rule, **no** count word; the node still occupies its lane | `:1433-1436` |
| a member entry with an empty uri | skipped entirely; the drawer's rendered ordinals then differ from the wire indices on purpose | `RecentsView.cs:381-388`, `RecentsPage.cs:1816-1821` |
| History with no `HistoryStore` | `BoxEl { Grow = 1 }` | `HistoryPage.cs:184` |

---

## 3. Tokens

### 3.1 Recents

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| page gutter | — | `Padding.L/R = Spacing.PageWide 36` | — | — | — | — | `RecentsPage.cs:55`, `:378`, `:388`, `:497` |
| masthead block | — | `pad(36, 24, 36, 16)`, `Gap 4`, `Stagger 45` | — | — | — | — | `:494-500` |
| masthead title | 40 / 52 | `Gap 12` to the button | — | `WaveeType.SurfaceDisplay` (Display face, W400, track −12) | `Tok.TextPrimary` | — | `:467-473`, `WaveeType.cs:146` |
| Overview button | MinH 24 | `pad(7,2,7,3)` | 4 | 12 px label, 14 px glyph `Icons.Calendar \uE787` | `ButtonAppearance.Subtle` | — | `:474-475`, `ControlSize.cs:41` |
| summary line | 12 / 16 | `MinWidth 220` | — | `Ui.Caption` | `Tok.TextTertiary` | — | `:59`, `:485-493` |
| pivot strip | — | `pad(36,0,36,8)`, `Gap 20` | — | — | — | — | `:358`, `:378`, `:528` |
| pivot tab | — | `Gap 4` (label→underline), `FocusVisualMargin 2` | — | `WaveeType.PivotLabel` 19/25/350, track −6 | disabled `Tok.TextDisabled` · selected `Tok.TextPrimary` · else `Tok.TextSecondary` | — | `:542-576`, `WaveeType.cs:219` |
| pivot underline | h 2 | — | `Radii.Full` | — | `Prop(_accent.Fill)`, `BrushTransitionMs 250` | — | `:512`, `:552-566` |
| list body wrapper | grow | `pad(36, 0, 36, 16)` | — | — | — | `LayoutTransition ContentResize` | `:380-397` |
| list ↔ rail gap | — | `Gap 12` | — | — | — | — | `:664` |
| day header (inline) | h 48 | `pad(8,0,8,0)`, `Gap 12` | — | `WaveeType.ModuleHeader` 20/28/600 Display, track −6 | `Tok.TextPrimary`; `HoverColor = accent.Ink`, `BrushTransitionMs 83` | `Fill = transparent` | `:52`, `:1402-1427` |
| day-header rule | h 1, `Grow 1` | — | — | — | `Tok.StrokeDividerDefault` (dark `#FFFFFF15`, light `#0000000F`) | `HitTestVisible false` | `:1428-1432`, `PaletteBuilder.cs:317`, `:428` |
| day-header count | 12 / 16 | — | — | `Ui.Caption` | `Tok.TextTertiary` | — | `:1433-1436` |
| day header (pinned) | h 48, `Grow 1` | same | — | same | same ink | `Fill = Tok.FillLayerDefault` (dark `#3A3A3A4C`, light `#FFFFFF80`) | `:1407`, `:1455-1465`, `PaletteBuilder.cs:306`, `:417` |
| day header (pinned), no day yet | h 48 | — | — | — | — | `Opacity 0`, `HitTestVisible false`, `HitTestPassThrough true` — never un-mounted | `:1459-1461` |
| day-header count, n == 0 | — | — | — | — | `Caption("")` — label + rule only | — | `:1433` |
| sticky clip on rows | inset 48 | feather 24 | — | — | — | `ItemClipTopInset` / `ItemClipTopFadeBand` | `:1202-1203`, `DetailVerticalLayout.cs:92` |
| recents row (group) | h 64 | `pad(8,0,8,0)`, `Gap 12` | 4 | — | rest transparent · hover `Tok.FillSubtleSecondary` · press `Tok.FillSubtleTertiary` · border 0 | `plated: false` | `:48`, `:1689`, `MediaCard.cs:1039-1062` |
| row cover | 48 × 48 | — | 4, or `24` when `circular` | — | `Surfaces.Artwork` + shimmer tile | — | `MediaCard.cs:965-984` |
| row title | 14 / 20 / 600 | text col `Gap 2` | — | `WaveeType.TrackTitle` | `Tok.TextPrimary` | — | `MediaCard.cs:990-992` |
| row subtitle | 12 | — | — | `RichText.OfRow` | `Tok.TextSecondary`, links `Tok.AccentTextPrimary` | — | `MediaCard.cs:995-996` |
| type chip | — | `pad(8,4,8,4)` | `Radii.Full` | `WaveeType.Eyebrow` 12/16/600 +30 track | `Tok.TextTertiary` on `Tok.FillSubtleSecondary` | — | `MediaCard.cs:1065-1071` |
| trailing cluster | — | `Gap 12`, `Shrink 0` | — | when-caption `Ui.Caption` | `Tok.TextTertiary` | — | `:1670-1679` |
| chevron slot, `!CanExpand` | 24 × 24 | — | — | — | transparent spacer (`Spacing.XXL`) | — | `:1669` |
| now-playing row title | 14 / 20 / 600 | — | — | `WaveeType.TrackTitle` | `Tok.AccentTextPrimary` — a recolour only, **no** row fill | — | `:2022-2027` |
| "…" button | 28 × 28 | — | circle | `Icons.More \uE712` @ 16 | `Tok.TextSecondary`, opacity **0.45 → 1** on row hover | `HoverScale 1.07 / Press 0.92` | `TrackRow.cs:966-989` |
| chevron | 24 × 24 | — | 4 | `\uE76C` ↔ `\uE70D` @ 12 | `Tok.TextSecondary` → `Tok.AccentTextPrimary` | `HoverFill Tok.FillControlSecondary`, `FocusVisualMargin 1`, `BlocksDragArm` | `TrackRow.cs:625-641` |
| saved meta line | 12 / 16 / 600 | `Gap 4`, check @ 12 | — | `Ui.Caption` | `Tok.SystemFillSuccess` | — | `:1721-1733` |
| saved artwork stack | 60 × 56 | tiles at x 12 / x 16; context tile at y 8 | 4 | — | two 40×40 member covers + the 48 context tile | `ZStack` | `:1953-1986` |
| drawer outer | — | `Margin(32, 2, 0, 8)` | — | — | — | `ClipToBounds`, `Animate = DrawerReveal` | `:1862-1867` |
| drawer spine | w 1 | — | — | — | `Prop(_accent.Ink)`, `BrushTransitionMs 250` | `HitTestVisible false` | `:1849-1853` |
| drawer inner column | — | `Padding(24, 0, 8, 0)` | — | — | — | — | `:1857` |
| drawer child row | h 40 | tracks `[30·28·32·*1·52·112]` | 4 | see 01-track-row.md | `HoverFill Tok.FillSubtleSecondary` | — | `:53`, `:1914-1935`, `:2032-2043` |
| single-play row | h 64 | tracks `[36·28·32·*1·52·40]` | 4 | see 01-track-row.md | same | — | `:1999-2007` |
| annotated rail | w 44 (`LabelsMinWidth`) | — | — | labels 14 px | — | — | `AnnotatedScrollBar.cs:196` |
| rail thumb | 30 × 3 | — | 1.5 | — | `Tok.AccentDefault` | — | `AnnotatedScrollBar.cs:585-601` |
| rail ghost | 30 × 3 | — | 1.5 | — | `Tok.AccentDisabled` | — | `AnnotatedScrollBar.cs:557-575` |
| rail tick | 3 × 3 | one per day, min gap 4 | 1.5 | — | `Tok.TextTertiary` | — | `AnnotatedScrollBar.cs:851-870`, `:196` |
| rail hover flag | MinH 40, MaxW 360 | — | — | — | `Tok.AcrylicFlyout.Fallback` | — | `AnnotatedScrollBar.cs:642`, `:655-656` |
| calendar band | — | `pad(8,8,8,0)`, `Gap 12`; inner col `Gap 2` | — | `Ui.Caption` ×2 | day `Tok.TextPrimary` W600 · readout `Tok.TextSecondary` | — | `:905-926` |
| legend swatch | 12 × 12 | `Gap 4` | 4 | — | `DensityFill(1..5)`, `BrushTransitionMs 250` | — | `:1079-1083` |
| legend captions | 12 / 16 | — | — | `Ui.Caption` | `Tok.TextTertiary` | — | `:1086`, `:1089` |
| month card | w 290 | `Gap 8`; grid `Gap 4` | — | — | — | — | `:846`, `:986-1017` |
| month title | 20 / 28 / 600 | row `Gap 8` | — | `WaveeType.ModuleHeader` | `Prop(_accent.Ink)` when `IsCurrentMonth`, else `Tok.TextPrimary` | `BrushTransitionMs 250` | `:1001-1008` |
| month meta | 12 / 16 | `Grow 1` | — | `Ui.Caption` | `Tok.TextTertiary` | — | `:1009-1013` |
| weekday cell | 38 × 20 | centred | — | `Ui.Caption` | `Tok.TextSecondary` | — | `:841`, `:951-957` |
| calendar cell | 38 × 32 | centred | 4 | `Ui.Body` 14/20; W600 when today | numeral `Tok.TextPrimary`, or `Prop(_accent.Ink)` when today | `Fill = DensityFill(level)`, `BrushTransitionMs 250` | `:839`, `:1112-1143` |
| month grid | — | `GridFit(290, 36, MonthCardHeight(MaxWeeks))`, `Overscan 1` | — | — | — | `AutoEdgeFade` (40 DIP feather) | `:878-891`, `Reconciler.cs:4231` |

### 3.2 History

| element | size | padding / gap | radius | type style | colour / brush | material | source |
|---|---|---|---|---|---|---|---|
| page header | — | `pad(36,16,36,12)`, `Gap 12` | — | — | no fill (inherits the content ground) | — | `HistoryPage.cs:542-547` |
| title row | — | `Gap 12` | — | `WaveeType.PageHero` 28/36/600 | `Tok.TextPrimary`; glyph `Icons.Clock \uE823` @ 22 | — | `:551-557` |
| stat pill | — | `pad(8,4,8,4)`, `Gap 4` | `Radii.Full` | value 12/16/600, label 12/16 | value `Tok.TextPrimary`, label `Tok.TextSecondary`, plate `Tok.FillSubtleSecondary` | — | `:604-615` |
| search box | MinH 36 | grow 1 | 4 | — | `AutoSuggestBox` defaults | — | `:578-586` |
| filter bar | — | `Gap 12`, row `margin-bottom 8` | — | `SelectorBar` (`pad 12,10,12,7`) | pill 4×3 scaled ×4 → 16 wide | — | `:590-599`, `SelectorBar.cs:29-32`, `:153` |
| sort combo | w 160, MinH 32 | — | 4 | — | `ComboBox` defaults | — | `:597`, `ComboBox.cs:73` |
| scroll region | — | `pad(36,12,36,24)`, `Gap 16` | — | — | — | — | `:249-254` |
| group column | — | `Gap 8` (header → card); groups stacked `Gap 16` | — | — | — | — | `:328`, `:335`, `:383` |
| group header | — | `Gap 8`, `margin-bottom 4`; rule `margin(4,0,4,0)` | — | `WaveeType.Eyebrow` 12/16/600 +30 | `Tok.TextSecondary`; rule `Tok.StrokeDividerDefault` | — | `:339-354` |
| Most-visited header | — | same, **no count pill** | — | same | same | — | `:386-395` |
| count pill | — | `pad(8,2,8,2)` | `Radii.Full` | 12 / 16 | `Tok.TextSecondary` on `Tok.FillSubtleSecondary` | — | `:348-352` |
| group card | — | — | `Radii.Card 8` | — | `Tok.FillCardSecondary`, border 1 `Tok.StrokeCardDefault` | `ClipToBounds` | `:357-362` |
| entry row | h 56 | `pad(12,0,8,0)`, `Gap 12` | (card-clipped) | `Ui.Body` 14/20 title | `Interaction.Subtle` ramp; unreachable `Opacity 0.6` | — | `:483-492` |
| row icon box | 36 × 36 | centred | 4 | glyph @ 16 | playlist: `Tok.AccentDefault` on `AccentDefault α .12`; else `Tok.TextSecondary` on `Tok.FillSubtleSecondary` | — | `:429-442` |
| kind + arg line | 12 / 16 | `Gap 4`; col `Gap 2` | — | `TextEl` | `Tok.TextTertiary` | — | `:444-456`, `:507-519` |
| timestamp | 12 / 16 | — | — | `TextEl` | `Tok.TextTertiary` | — | `:458` |
| visit badge | — | `pad(8,4,8,4)` | `Radii.Full` | 12 / 16 | `Tok.TextSecondary` on `Tok.FillSubtleSecondary` | — | `:464-469` |
| delete button | 28 × 28 | centred | 4 | `Icons.Cancel \uE711` @ 12 | `Tok.TextSecondary`, `Opacity 0.5` | `Interaction.Subtle` | `:473-481` |
| row divider | h 1 | `margin-left 60` (= 12 + 36 + 12) | — | — | `Tok.StrokeDividerDefault` | — | `:502` |
| row divider, last row | — | **absent** in Most recent (`showDivider: k < n-1`); **present** in Most visited | — | — | — | — | `:325` vs `:378-380` |

### 3.3 Loc keys and glyphs (the complete inventory for this surface)

Every string either comes from `assets/loc/en-US.json` or from a `CultureInfo` table. §9.5's rule stands: **do not re-key any of these in 0.3.**

| key | en-US | used by |
|---|---|---|
| `home.recents` | `Recents` | the masthead word (`RecentsPage.cs:467`) |
| `recents.overview` | `Overview` | the masthead button (`:474`) |
| `recents.itemCount` | `{count, plural, one {# item} other {# items}}` | the summary line **and** every day header's count (`:460`, `:1433`) |
| `recents.groupedFrom` | `{count, plural, one {grouped from # play} other {grouped from # plays}}` | the summary's third clause (`:461`) |
| `recents.playedCount` | `{count, plural, one {Played # track} other {Played # tracks}}` | a group row's meta (`:1645`) |
| `recents.savedCount` | `{count, plural, one {# song added} other {# songs added}}` | a `Saved` row's green line (`:1646`) |
| `recents.playCount` | `{count, plural, one {# play} other {# plays}}` | month card meta + calendar readout (`:983`, `:1039`) |
| `recents.soFar` | `so far` | current month only (`:984`) |
| `recents.nothingPlayed` | `Nothing played` | zero-play month **and** zero-play day (`:985`, `:1039`) |
| `recents.mostly` | `top: {title}` | the readout's second clause (`:1042`) |
| `recents.jumpToDay` | `Jump to {date}` | only when `HeaderFlatFor(date) >= 0` (`:1047`) |
| `recents.legend.quieter` / `.busier` | `Quieter` / `Busier` | the heat legend (`:1086`, `:1089`) |
| `detail.filter.all` | `All` | pivot 1 (`:531`) |
| `recents.chip.music` / `.podcasts` | `Music` / `Podcasts` | pivots 2 and 3 (`:532`, `:534`) |
| `recents.pivot.artists` | `Artists` | pivot 4 (`:536`) |
| `detail.today` / `detail.yesterday` | `Today` / `Yesterday` | `DayBucketLabel` (`RecentsView.cs:266-267`) |
| `detail.likedSongs` | `Liked Songs` | the locally-answered Liked row (`:1553`) |
| `sidebar.section.emptyRecents` | `Play something and it'll show up here` | the empty state (`:372`) |
| `common.errorTitle` / `.errorSubtitle` / `.retry` | `Something went wrong.` / `Check your connection and try again.` / `Retry` | the error state (`ErrorState.cs:22-26`) |

**Type-chip strings** (`KindLabel`, `RecentsPage.cs:2087-2096`) — all borrowed, this page mints none:

| entity kind | key | en-US |
|---|---|---|
| Album | `home.album` | `Album` |
| Artist | `home.artist` | `Artist` |
| Show | `podcast.show` | `Podcast` |
| Episode | `podcast.episodes` | `Episodes` *(plural on a single episode — a borrowed key's cost, kept as-is)* |
| Track | `detail.column.song` | `Song` |
| Playlist | `nav.playlist` | `Playlist` |
| Collection / Unknown | — | **null — no chip at all** |

**History** uses `nav.history.*` throughout: `title`, `clearAll`, `clearAllConfirm`, `clearAllConfirmBody`, `searchPlaceholder`, `mostVisited` (authored `MOST VISITED`), `visitCount`, `visitMultiplier` (`{count}×`), `stat.visits`, `stat.unique`, `filter.{all,playlists,shows,library,search,pages}` (note **`filter.shows` = "Podcasts"**), `sort.{mostRecent,mostVisited}`, `group.{today,yesterday,thisWeek,thisMonth,earlier}`, `kind.{playlist,show,library,search,page}`, `ts.{yesterday,weekday,date}`, `empty.{noResults,nothingHere,noHistory,noMatch,tryAll,startNavigating}`, plus `nav.viewAllHistory` on the toolbar flyout. **There is no `kind.browse`** — a browse route filters under *Pages* but labels itself `Page` (`HistoryPage.cs:35`, `:421-428`).

**Glyphs** (all verified against the generated `Icons` table): `Icons.Calendar ` (Overview) · `Icons.Clock ` (History title, and the toolbar's "View all history") · `Icons.Check ` (the Saved meta tick) · `Icons.More ` · `Icons.Cancel ` (History delete) · `Icons.ChevronRight ` ↔ `Icons.ChevronDown ` · `Icons.Headphones` (the command-palette entry) · History's per-row glyph comes from `ShellNav.Dest(route)`, never from this page.

---

## 4. Colour & material

### 4.1 The page accent (`PageAccent { Ink, Fill, Key }`)

```
_stickyHeader (flat index)
   → RecentsView.AccentSourceRow(sections, rows, stickyFlat)          RecentsView.cs:670-686
       first Row-kind flat item at/after stickyFlat INSIDE THE SAME DAY, bounded to an 8-item forward walk
   → FactsFor(rows[row]).Cover?.Url                                    RecentsPage.cs:2488
   → SpotifyLive.CoverColorPlane.Current.Watch(url)                    :2490   ← the ONE subscription, owned by
   → Surfaces.ChromeSchemeFor(url) → CoverColorPlane.Scheme            :2491     RecentsAccentBinder (a 0×0 leaf)
   → Ink  = WaveePalette.ChromeAccent(scheme)                          :2492   = Vivid(Lift(Accent(scheme))), or
   → Fill = WaveePalette.Accent(scheme)                                          Tok.AccentDefault on greyscale art
   → _accent.SetIfChanged(resolved)                                    :2495
```

Fallback when washes are off, no cover has graded, or `AccentSourceRow` returns −1: `new PageAccent(Tok.AccentTextPrimary, Tok.AccentDefault, "")` — byte-identical to what every consumer painted before the page had a dynamic accent (`RecentsPage.cs:2425-2428`).

**Where it lands, and how it transitions** — every site is a bound `Prop`, so the value changes without the consumer re-rendering:

| site | channel | transition | file:line |
|---|---|---|---|
| pivot underline | `Fill` ← `accent.Fill` | `BrushTransitionMs 250` | `:559-562` |
| drawer spine | `Fill` ← `accent.Ink` | 250 | `:1851-1852` |
| calendar cell heat | `Fill` ← `DensityFill(level)` over `accent.Ink` | 250 | `:1126-1127` |
| legend swatches | `Fill` ← same function | 250 | `:1081-1082` |
| today's numeral | `Color` ← `accent.Ink` | 250 | `:1115-1116` |
| current month title | `Color` ← `accent.Ink` | 250 | `:1005-1006` |
| day-header hover ink | `HoverColor` = `accent.Ink` (**plain ColorF, not bindable**) | `BrushTransitionMs 83` (`WaveeMotion.Faster`) | `:1423-1427` |

The day-header case is the one exception and it is documented in place: `TextEl.HoverColor` takes no `Prop`, so the realized date-header slots **do** re-render on a section crossing — bounded to the realized window, and they already re-render for `_stickyHeader`/`_epoch` (`:1416-1422`).

**Quantization.** `UpdateSticky` records the sticky bucket's **DayIndex** into a plain field and posts one coalesced `ResolveAccentDay` that re-reads it *fresh*; a scroll burst crossing five days commits exactly one accent move — the day it settled on (`:1288-1313`). Never the flat header index (a drawer-driven extent correction can shift that within one day) and never a per-frame value.

### 4.2 The shell wash (Mica scrim)

Recents publishes **one leg** of Home's three-leg `HomeWash`: `new HomeWash(new WashLayer(pick.Color, pick.Key), null, null)` (`RecentsPage.cs:317-320`).

```
WashCard()                                                             :2435-2460
  1. the accent's own source row (same AccentSourceRow selector) if its cover has a url
  2. else scan the first min(Display.Length, 32) rows for the first resolved cover   :2450
  3. else null  → no wash (a colour invented before any artwork landed is a colour the page does not own)
HomeWashSource.Pick(card, Surfaces.ChromeSchemeFor)                    HomeWashSource.cs:59-71
  tier 1: card.Meta.Accent (payload) → WaveePalette.Lift(...) with A = 1
  tier 2: graded cover  → WaveePalette.ChromeAccent(scheme) with A = 1
HomeWashSource.PlaneUrl(card) → the ONE artwork the page Watches for the wash       :315-316
ShellMaterial.Publish(slot, _washOwner, isClaim, washesDisabled, tint: null, wash)  :325
```

Painted by `ShellMaterialLayer` as the **Hero** placement: centre `(0.06, 0.00)`, radius `(0.74, 0.92)`, fade offset `0.62`, origin alpha **0.055 light / 0.10 dark**, clipped to the ellipse's own box and top-anchored (`ShellWashGeometry.cs:24-25`, `:33-36`; `ShellMaterialLayer.cs:44`, `:122-127`). It composites over live DWM Mica through every chrome band — title row, sidebar, player dock (`WaveeTokens.cs:86-106`).

**Ownership.** Claim on the first publish and on `UseActivation.onActivated`; refresh while still the owner; **never clear** (`RecentsPage.cs:325-343`). The publishing effect is keyed on `HashCode.Combine(washesDisabled, pick.Key, pick.Color.R/G/B)` so it writes on a real colour change, not once per render (`:330`).

**Settings.** `AppearancePrefs.Epoch.Value` is read in `Render` and in the binder, so `appearance.colorWashes.enabled` (`AppSettings.cs:100`) applies **live**: turning washes off publishes `definite: true` → the neutral ground, eased, and drives the accent to the fallback in the same frame (`RecentsPage.cs:310-311`, `:2478-2479`).

### 4.3 The heat ramp

```
DensityLevel(count, max) = clamp(ceil( ln(1+count) / ln(1+max) × 5 ), 1, 5);  0 when count ≤ 0   RecentsView.cs:511-516
DensityFill(level)       = level ≤ 0 ? transparent
                         : accent.Ink with { A = A_subtle + (accent.Ink.A − A_subtle) × clamp(level,1,5)/5 }   RecentsPage.cs:1060-1070
```

`A_subtle = Tok.AccentSubtle.A` = **0.141** on the stock light/dark palettes (`0x24`/255, `PaletteBuilder.cs:252`, `:451`) or **0.16** when a preset accent ramp is active (`Tokens.cs:444`). With an opaque accent ink (α = 1) the five rungs are therefore:

| level | α (A_subtle = 0.141) | α (A_subtle = 0.16) |
|---|---|---|
| 1 | 0.313 | 0.328 |
| 2 | 0.485 | 0.496 |
| 3 | 0.656 | 0.664 |
| 4 | 0.828 | 0.832 |
| 5 | 1.000 | 1.000 |

Only **played** rows contribute heat: a group contributes `max(1, ChildCount)`, a played single contributes 1, `Saved`/`Unknown` contribute 0 — they still extend the visible date range (`RecentsView.cs:427-429`, `:507-509`).

### 4.4 Light / dark differences

Nothing on either page branches on theme in app code; every colour is a `Tok.*` accessor the palette resolves per theme. The differences a reviewer will actually see:

- The sticky band: light `#FFFFFF80` (50 % white) vs dark `#3A3A3A4C` (30 % of a light grey) — light reads as a frosted sheet, dark as a lifted plate.
- Hover fill: light `#0000000F`-class vs dark `#FFFFFF0F` (`PaletteBuilder.cs:302`, `:428`).
- The wash is **~1.8× stronger in dark** (0.10 vs 0.055) because the same colour reads weaker over the dark ground (`ShellWashGeometry.cs:31-34`).
- `Tok.AccentTextPrimary` (the fallback Ink) is `#004275` light / `#A6D8FF` dark (`PaletteBuilder.cs:248`, `:336`) — so a page with no graded artwork is navy-inked in light and pale-blue-inked in dark.

---

## 5. Motion

All motion samples the engine present clock (`FrameTime.NowQpc`) through `MotionTok`/`LayoutTransition`/`While*`; **no `Environment.TickCount64` anywhere on this surface** (verified: zero hits in `Features/Recents/**` and `HistoryPage.cs`). The one clock read that is *not* frame time is `DateTimeOffset.Now`, used for grouping and the midnight timer — a calendar clock, not an animation clock.

| # | trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | file:line |
|---|---|---|---|---|---|---|---|---|---|
| 1 | page mount | masthead container | children stagger | — | — | — | `Stagger = 45` per child (2 children) | `Motion.ReducedMotion ? 0 : 45` — a **value**, never a branch | `:64`, `:498`, `WaveeMotion.cs:75` |
| 2 | page mount | masthead title & summary | `TranslateY`, `Opacity` | `Dy 10, α 0 → rest` | 300 | `MotionTok.StandardEnter` = `FluentDecelerate` `(0.1,0.9,0.2,1)` | (stagger above) | `ReducedMotionPolicy.KeepFade` — the fade survives, the rise snaps | `:471-472`, `:491-492`, `MotionTok.cs:168` |
| 3 | pivot selection change | the newly selected tab's underline | `ScaleX` | `0 → 1`, origin X = 0 | **260** | `Easing.SmoothOut` `(0.22,1,0.36,1)` | — | engine `ReducedSnap` parks the scale | `:514`, `:563-565` |
| 4 | accent moves (section crossing) | underline / spine / cells / swatches / month title / today numeral | `Fill` / `Color` | old → new graded colour | **250** (`WaveeMotion.Standard`) | engine brush cross-fade | — | `AccentTransitionMs` returns **0** under reduced motion → an instant swap | `:68` |
| 5 | hover a day-header label | `ModuleHeader` ink | `Color` | `TextPrimary → accent.Ink` | **83** (`WaveeMotion.Faster`) | engine `HoverFade` | — | (engine hover ramp, compositor-only) | `:1426` |
| 6 | pivot switch (list identity change) | the body wrapper | `Position`, `Opacity` | enter `Dy 8, α 0`; exit `α 0` | spring `FromResponse(0.40, 0.90)` (`MotionTok.ContentResize`) | spring | — | `Motion.ReducedMotion` collapses the value → null transition | `:392-395`, `MotionTok.cs:177` |
| 7 | list cold realize | rows | materialization | — | ≤ 600 fresh nodes / 3.3 ms per frame | — | one batch **per frame**, visible band is a hard floor while scrolling | unaffected (a budget, not an animation) | `:1207`, `ColdRealizeRamp.cs:39-49` |
| 8 | pending → ready (page) | the skeleton shimmer | `Opacity` | 1 → 0 | floored at **400** (`Expressive.Slow`) for `SkelReveal.None` | cross-dissolve | — | fade retained | `:370`, `SkeletonRegion.cs:20-24` |
| 9 | row facts resolve | one row | `Opacity` | 0 → 1, **no resize** | `SkelReveal.FadeOnly`, `smoothResize: false` | cross-fade | — | fade retained | `:1534-1537` |
| 10 | expand a group row | drawer **outer** (clip box) | `Size` (height) | 0 → natural | **333** | `MotionTok.DisclosureExpand` = `FluentPopOpen` `(0,0,0,1)` | — | engine `ReducedSnap` | `:1598-1606`, `MotionTok.cs:180` |
| 11 | collapse | drawer outer | `Size` | natural → 0 | **167** | `MotionTok.DisclosureCollapse` `(1,1,0,1)` | — | snap | `:1603`, `MotionTok.cs:181` |
| 12 | expand | drawer **inner** (presence) | `Opacity`, `Position` | `Dy −8, α 0 → rest` | 333 / exit 167 (`Dy −4`) | same pair | — | fade retained | `:1608-1613` |
| 13 | drawer open | each child row wrapper | `Opacity` | `Dy −4, α 0 → rest` | 333 | `DisclosureExpand` | `WaveeEntrance.DelayMs(slot)` = `clamp(i,0,8) × 40` → **0…320 ms** | `DelayMs` returns 0 | `:1619-1622`, `:1828`, `WaveeMotion.cs:121-122` |
| 14 | chevron toggle | chevron glyph | glyph **swap** + colour | `\uE76C`/Secondary ↔ `\uE70D`/AccentTextPrimary | **167** | `MotionTok.DisclosureChevron` `(0.167,0.167,0,1)` | — | snap | `:1668`, `TrackRow.cs:640-642` |
| 15 | zoom out (Overview / a day header) | overview view | `Scale`, `Opacity` | enter `1.08 → 1`, α 0 → 1; list exits to `0.94`, α → 0 | spring `FromResponse(0.35, 0.85)` | spring | — | engine policy | `SemanticZoom.cs:238-241`, `MotionRecipes.cs:214-218` |
| 16 | zoom in (cell click / Esc) | list view | `Scale`, `Opacity` | enter `0.94 → 1`; overview exits to `1.08` | same spring | spring | — | engine policy | `MotionRecipes.cs:222-226` |
| 17 | after a zoom swap | incoming viewport | scroll | — | instant (`animate: false`) | — | in the first layout effect, before the animation tick | — | `SemanticZoom.cs:229-235` |
| 18 | scroll | pinned day header | `Transform.TranslateY` | `0 → push` (`push ≤ 0`, `|push| ≤ 48`) | continuous, per frame | — | quantized to 2 DIP | none (it tracks scroll) | `:1283-1285`, `:1462` |
| 19 | wheel over the pinned header | list scroll offset | offset | +delta | engine `WheelAnimating` chase with target accumulation | — | — | — | `:1468-1476` |
| 20 | row hover | row plate | `Fill` | transparent → `FillSubtleSecondary` | 83 (`BrushMs` default) | engine `HoverFade` | — | brush only, always on | `MediaCard.cs:1052` |
| 21 | row hover | play FAB, "…" | `Opacity` (+ scale on "…") | 0 → 1 / 0.45 → 1 | 167 (`WaveeMotion.Fast`) | `FluentDecelerate` | — | `ScaleTier` collapses to 1 | `MediaCard.cs:113`, `TrackRow.cs:968-983` |
| 22 | row press | "…" / FAB | `Scale` | 1 → 0.92 | `ControlFaster` 83 | eased | — | 1 (no scale) | `WaveeMotion.cs:48`, `:182` |
| 23 | list top/bottom edge | list content | `Opacity` mask | 1 → 0 over 40 DIP | — | — | — | — | `:1193`, `Reconciler.cs:4231` |
| 24 | rail hover | ghost thumb + detail flag | `Opacity`, `Transform` | 0 → 1, y = pointer | bound `Prop`, per frame | — | — | — | `AnnotatedScrollBar.cs:492-506` |
| 25 | local midnight | day-header labels | text | yesterday's word → today's | instant (an `_epoch` bump, **not** `_shapeEpoch`) | — | timer re-armed per local day, `+1 s` settle | — | `:266-269`, `:2275-2289` |
| 26 | now-playing identity change | the snapshot | `/page/diff` refresh | — | trailing **2,000 ms** (`PlayLogStore.SaveDebounceMs`) | — | keyed on the uri, so a skip burst collapses to one fetch | — | `:72`, `:274-276` |
| 27 | History: row hover / press | entry row | `Fill` | transparent → `FillSubtleSecondary` → `FillSubtleTertiary` | 83 | `Interaction.Subtle` | — | brush only | `HistoryPage.cs:492`, `Interaction.cs:132-135` |
| 28 | History: filter change | `SelectorBar` pill | `ScaleX` | 0.625 ↔ 1 (→ 16 DIP shown) | 167 | `ControlFast` | — | engine | `SelectorBar.cs:29-32` |
| 29 | hover a calendar cell ≥ the tooltip dwell | `ToolTip` plate | `Opacity`, `Position` | engine tooltip default | engine | engine | engine dwell | engine policy | `:1146` (`ToolTip.Wrap`) |
| 30 | **first** mount of the semantic zoom | neither view | — | — | **none** | — | — | — | `SemanticZoom.cs:240-242` — `Transition` returns `null` on `KeepAliveOptions.FirstActivation`: the view a zoom first mounts into just appears, carried by the page's own entrance. Only a real zoom **gesture** animates |
| 31 | pivot switch, then switch back | the list viewport | scroll offset | restored, not re-animated | instant | — | — | — | `ScrollKey = "recents:" + (token ?? "all")` (`:1192`) — each pivot keeps its **own** remembered offset. The overview's key is `"recents-calendar:" + shapeEpoch` (`:889`), so a shape rebuild deliberately drops the calendar's scroll |
| 32 | a snapshot adoption / pivot re-cut | the calendar readout band | text | whatever day → **today** | instant | — | — | — | `BuildShape` writes `_calendarDay = today` as part of publishing (`:2264`) |

**Motion this surface deliberately has none of.** No hover/press *scale* on the row plate itself (a near-full-width row's press acknowledgement is `PressedFill` alone — `WaveeMotion.cs:35-38`); no rotation on the chevron (a glyph swap, #14); no connected/shared-element fly (below); no per-row authored entrance delay (#7); no animation on the sticky push beyond the scroll it tracks (#18); and no row-level `LayoutTransition` — the only FLIP on the page is the body wrapper's (#6).

**No page transition of its own.** Recents/History enter through the shell's route transition (chapter 18-shell-frame.md). There is **no connected/shared-element animation**: the row's `morphKey` is minted (first occurrence of a uri only, `RecentsView.cs:530-541`) but nothing captures the forward half anywhere in the app — `SharedTransition.Begin` has no callers (the full account is at `RecentsPage.cs:1694-1707`). Keep the key minted and dormant.

---

## 6. Interaction

### 6.1 Recents

| gesture | target | result | source |
|---|---|---|---|
| click | a **group** row that `CanExpand` | toggles its drawer (accordion: closes any other) | `:1238-1247`, `:1684` |
| click | a group row that cannot expand | `Open(row)` → `HomeCardNav.Open` on the shared card dispatcher | `:1684`, `:2117-2127` |
| click | a **single-play** row | `TrackRow.Invoke(bridge, track, …)` → play | `:2036` |
| click | the play FAB on the cover | `Play(uri)`: a track/episode plays itself, anything else starts as a **context** from index 0 | `:1688`, `:2129-2136` |
| click | the chevron | same toggle as the row; `BlocksDragArm = true` | `:1668`, `TrackRow.cs:636` |
| click | a day header (inline or pinned) | `InvokeDay` → `DateHeaderInvoked` → `OpenOverview(date)` → sets `_calendarDay`, `ZoomOutTo(HeaderFlatFor(date))` | `:239`, `:1387-1392`, `:596-600` |
| click | **Overview** in the masthead | `_calendarDay = today`, `ZoomOutTo(-1)` | `:474`, `:590-594` |
| click | a pivot tab | `SelectPivot(token)` — a **client-side re-cut**, never a network call | `:574`, `:582-587`, `RecentsView.cs:148-165` |
| click | a calendar cell with rows | `_calendarDay = date`, `ZoomInTo(monthIndex)` | `:1133-1137` |
| hover | a calendar cell | `_calendarDay.SetIfChanged(date)` → the readout band updates | `:1132` |
| pointer exit | a month card, or the overview surface | `ResetCalendarDay()` → back to today | `:899`, `:989`, `:1054-1055` |
| focus | a calendar cell | in → same as hover; out → reset | `:1138-1141` |
| **Esc** | the page (or the SemanticZoom root) | `ZoomInTo(-1)` when zoomed out; `e.Handled = true` | `:409-414`, `SemanticZoom.cs:277-282` |
| wheel | over the pinned day header | forwarded to the list: `_scrollController.ScrollBy(e.Delta, animate: true)` — never negated, never a hard snap | `:1468-1476` |
| right-click / "…" | a group row | `Menus.CardAttach` container menu (W16) | `:1666`, `:2049-2053` |
| right-click | a single-play / drawer row | `TrackContextMenu.BuildSingle(acts, track)` | `:2044-2046` |
| drag | a group row | `Drag.Source(WaveeDragKinds.Resource, WaveeResourceDragPayload.ForEntity(kind, uri, name, cover, acts))`; Album/Artist/Show/Episode/Track map 1:1, `Collection`+`Playlist` → `Playlist`, anything else → **no drag** | `:1691`, `:2055-2071` |
| drag | a drawer / single row | `WaveeResourceDragPayload.ForTrack(track)` | `:2039-2041` |
| keyboard | the list | `ItemsView` item navigation; `IsItemInvokedEnabled` → Enter/Space runs `InvokeFlat`; `ItemTextTyped` supplies type-ahead text (the day label for a header, the resolved title for a row) | `:1181-1184`, `:1249-1257` |
| tooltip | a calendar cell | `"dddd d MMMM · <readout>"`; the readout itself is the ONE builder shared with the band (`CalendarReadout`) | `:1043-1052` |

**Accessibility.** The pinned day header carries `Role = None, Focusable = false` while the inline one keeps `Role = Button, Focusable = true` — one day, one tab stop, one announcement; the overlay keeps only the click affordance because it is the same target under the pointer (`:1409-1412`). `SelectionMode = None` and `Selector = None` on the list: the rows are cards with their own chrome and a list selector would be a second competing cue (`:1178-1180`). A calendar cell with no rows has `Role = None, Focusable = false, Cursor = Arrow` and no click handler. A disabled pivot tab is `Focusable = false, IsEnabled = false`.

### 6.2 History

| gesture | target | result | source |
|---|---|---|---|
| click | a reachable row | `go(e.Route.Name, e.Route.Arg)`; History itself always opens in its own tab (`GoNav`) | `:488`, `WaveeShell.cs:1937` |
| click | an unreachable row | nothing — `OnClick = null`, `IsEnabled = false`, `Opacity 0.6` | `:487-492` |
| click | the ✕ | `store.Remove(e)` → version bump → save | `:479`, `:92-97` |
| click | **Clear all** | `ContentDialog` confirm (W21) → `store.Clear()` — clears memory **and deletes the file** | `:569-574`, `:99-106` |
| type | the search box | `onChange` and `onQuerySubmitted` both write `_search`; matches **title** (via `ShellNav.Dest`), **Arg**, or **route name**, case-insensitive | `:582-583`, `:270-276` |
| click | a `SelectorBar` chip | `_filterIndex`; `All / Playlists / Shows(→"Podcasts") / Library / Search / Pages`, where Pages = kind `page` **or** `browse` | `:595`, `:260-268` |
| select | the sort combo | `Most recent` (date-grouped) or `Most visited` (deduped flat, counted over the **full unfiltered** log) | `:597`, `:205-222` |

Entry kinds (`:28-36`): `pl:*` → playlist · `show:*` → show · `albums|artists|liked|podcasts|local` → library · `search` → search · `BrowseRoutes.IsHome(name)` → browse · everything else → page. Kind labels come from `nav.history.kind.*`; the row glyph and title come from `ShellNav.Dest(route)` (`ShellNav.cs:14-82`), which is also what makes a retired route label itself plausibly — hence the `ShellRoutes.IsKnown` gate.

**Entry points.** History: the toolbar back/forward button's context flyout, which lists up to the **8** most recent routes (`HistoryMenuMax`, `ShellToolbar.cs:54`) and — **only when the log holds more than 8** (`hasMore`, `:73-74`, `:83-87`) — a separator + "View all history" (`Icons.Clock`) that goes to route `history`; the command palette; a sidebar pin. **On a fresh profile with ≤ 8 navigations there is no "View all history" item at all** — the page is then reached only through the palette or a pin. Recents: the command palette entry `nav.recents` (`WaveeCommands.cs:200`, Ctrl+K), the Home "Recents" module's see-all (`HomePage.cs:617`, `:699`), a sidebar pin (customizer-only, never seeded into `DefaultTopBar` — `SidebarPinId.cs:45-48`), and the deep link `wavee://open?route=recents` (`DeepLinkParse.cs:42-47`).

**What History deliberately does not have.** No context menu, no drag source, no `ItemsView` (it is a plain `ScrollView` over built elements, so no virtualization, no type-ahead, no item-invoke keyboard path beyond Tab + Space on each row's own click target), **no `ScrollKey`** (so leaving and returning starts at the top, unlike Recents), and no skeleton — the whole page is synchronous over an in-memory list, capped at 500. Keep it that way in 0.3: the log is a few hundred rows and the cost of a virtual list here is the scroll-restore and focus bugs that come with it.

**The pivot strip has no arrow-key behaviour.** Unlike a WinUI `Pivot`/`SelectorBar`, these four tabs are four independent `Focusable` boxes: Tab moves between them and Space/Enter selects. A disabled tab is skipped (`Focusable = false`). If 0.3 adopts `SelectorBar` here it gains Left/Right — a change, not a port.

**There is no undo on either surface.** `App/ActivityUndo.cs` (`ActivityUndoExecutor`) is the library-mutation inverse used by the Activity log / notification centre; grep confirms **zero** references from `Features/Recents/**` or `HistoryPage.cs`. Removing a history row is immediate and unrecoverable; the only guard is the Clear-all dialog. Do not invent an undo in 0.3 — but do not lose the dialog either.

---

## 7. Data & readiness in 0.3 terms

### 7.1 Recents

| visual element | 0.2.9 source | 0.3 read | readiness predicate |
|---|---|---|---|
| masthead title | `Loc.Get(Strings.Home.Recents)` | unchanged | always |
| summary line | `RecentsView.Summary(_shape.Rows, …)` gated on `_hasSnapshot` (`:458-460`) | `Recents.Summary(feed.Rows, …)` | `Edges.Recents.State(me) != Unknown` — else render **nothing** (never "0 items") |
| pivot availability | `RecentsView.PivotAvailable(rows, token)` over `ContentType` / `HydrationUri` kind | same, over the recents edge payload | the snapshot's presence; the four tabs render on frame one regardless |
| day header label | `RecentsSections.HeaderLabels[day]` from `PlayedAtMs` + `CultureInfo` | `RecentsEdge.PlayedAtMs` → `Recents.DayBucketLabel` | complete once the edge list exists (no entity data needed) |
| day header count | `RecentsView.CountForDay(sections, day)` | same | same |
| row cover / title / subtitle | `IStore.Get{Track,Album,Artist,Show,Episode,Playlist}(uri)` at render time, `RowFacts` (`:1543-1571`) | `Track/Album/Artist/Show/Episode/Playlist` **handle** → `Title`, `Image`, `Album`/`Artists` edge, `Show.Publisher` | `h.Knows(<Kind>Fields.Identity)` — **skeleton until then** (`Skel.Region`, `FadeOnly`). A row whose title is empty must render the placeholder geometry, never an invented string |
| playlist owner byline | `IStore.GetOwner(canonical)` + `RecentsView.OwnerSubtitle` (`:1578-1583`) | `Playlist.Owner` (user slot) → `User.Name`; keep `Recents.OwnerSubtitle` verbatim so a raw base62 id is never shown | `Playlist.Knows(Owner)` **and** `User.Knows(Name)`; else `null` (render nothing, not an empty line) |
| Liked Songs row | answered locally: `Loc.Get(Strings.Detail.LikedSongs)` + `LikedSongsArtwork.Uri` (`:1551-1553`, `:1636`) | unchanged — costs no request | always ready |
| "Played N tracks" | `RecentsView.MetaFor(row)` over the wire `ChildCount` (**never** `ChildUris.Count` — the server truncates that list) | `RecentsEdge.ChildCount` | with the edge |
| "N songs added" | `MetaFor` → `SavedCount`, `max(1, ChildCount)` | same | with the edge |
| played-at caption | `RecentsView.PlayedAt(row.PlayedAtMs, now, culture)` | `RecentsEdge.PlayedAtMs` | with the edge |
| chevron present? | `RecentsView.CanExpand(row)` — count > 0 **and** members-or-childUris has a uri | same over the edge payload | with the edge |
| drawer rows | `RecentsView.DrawerEntries(row)` → members, else childUris with no instant | member edge list | each child row skeletons until its `Track.Knows(Identity)` |
| type chip | `KindLabel(RecentsList.EntityKindOf(uri))` | `EntityUri.Kind` | immediate (parsed at the wire, plan §4.1) |
| calendar heat | `RecentsView.DayDensity(rows, display, now, culture)` | same over the edge | with the edge — the whole overview is edge-only |
| calendar "top: X" | `day.TopItem.OriginalRowIndex → FactsFor(...).Title` (`:1040-1042`) | handle `Title` | omitted until `Knows(Identity)` — the sentence drops the clause, it does not show a placeholder |
| accent + wash | `FactsFor(row).Cover?.Url` → `CoverColorPlane` | `<Handle>.ImageId` → the same grading plane | fallback accent until a cover grades; **never** an invented colour |
| History row title/glyph | `ShellNav.Dest(route)` | unchanged (`Shell.Dest`) | always — History reads no entity data at all |

**Readiness rule for this surface.** The *structure* (day buckets, counts, dates, heat, chevrons, the whole calendar) comes from the recents edge alone and must appear **complete and at once**. Only per-row *identity* (title/subtitle/cover) can trail, and it trails as a **fade-only** reveal into geometry that never moves. Sections popping in, counts arriving late, or a day header appearing after its rows is a regression.

**Demand on mount.** Plan §4.13's shape, applied here:

```csharp
UseEffect(() =>
{
    Entities.EnsureEdges(User.Me, EdgeKind.Recents);                  // the whole grouped snapshot, one call
    Entities.EnsureRows(_shape.EntitySlots, EntityFields.Row);        // ~1,708 handles, batched 300/POST by the planner
    Entities.EnsureRows(_shape.OwnerSlots,  UserFields.Identity);     // the playlist bylines
});
```

No `OnVisibleRange`, no `_inflight`, no `_batch`, no `Pump`. See §9 — this is the single biggest behavioural change on the page and it needs the planner's priority lane, not a page-side window.

### 7.2 DATA GAPS

| what the surface shows | 0.2.9 source | the plan's model holds | proposed column / edge |
|---|---|---|---|
| the grouped recents list itself | `GET /playlist/v2/list/recents/page` → `RecentsWireMapper` → `RecentsList.Group` → `RecentsRow[]` | **nothing** — no Recents kind, no edge | `EdgeTable<RecentsEdge> Recents` in `Edges.cs`, parent = the `User.Me` slot, targets = entity slots, payload `readonly record struct RecentsEdge(StringId ItemId, long PlayedAtMs, int ChildCount, byte Reason /*0 unknown,1 played,2 saved*/, byte ContentType /*0 none,1 music,2 podcasts*/, byte Kind /*single|group*/, int MembersStart, int MembersLen)` |
| a group's collapsed members (with their own instants) | `RecentsRow.Members` (`RecentsMember(ItemId, Uri, PlayedAtMs)`) | nothing | a second CSR table `EdgeTable<RecentsMemberEdge> RecentsMembers`, parent = the group's **synthetic** slot; `MembersStart/Len` on the parent edge index into it. (Do **not** reuse `ChildUris` — it is server-truncated; the members list is the only complete account, `RecentsList.cs:58-61`) |
| the header's own truncated `child_uri` list | `RecentsRow.ChildUris` | nothing | the fallback arm of the same table, flagged `NoInstant` |
| the snapshot revision (for `/page/diff`) | `RecentsSnapshot.Revision`, lowercase hex | nothing | `Column<StringId> RecentsRevision` on the user table, or the edge table's own `Total`/state word |
| `item_id` as the reconciler key | `RecentsRow.ItemId` — **uris repeat ~1,388× in a real list** | edges are keyed by target slot only | `StringId ItemId` on `RecentsEdge` (above). Without it the accordion's identity check (`:1762`) and the recycle keys break |
| `content_type_*` | `RecentsRow.ContentType` (`"music"`, `"podcasts"`) | nothing | the `ContentType` byte on `RecentsEdge` |
| `recent_type_*` (played vs saved) | `RecentsRow.Reason` | nothing | the `Reason` byte |
| cover palette (accent + wash) | `SpotifyLive.CoverColorPlane` keyed by artwork url, graded async | nothing in §4.1-4.3 | **shared gap with 00-design-system.md / 10-home.md**: a `Column<uint> AccentArgb` + `Column<byte> AccentKnown` per kind (or one global `Dictionary<StringId,uint>` plane), filled by the same grading pass |
| playlist owner display name | `IStore.GetOwner(canonical)` (the `Owner` row) | `User` table exists (§4.14) but no `Playlist.Owner` slot column is listed | `Column<int> Owner` on `PlaylistTable` + `Column<StringId> Name` on `UserTable` |
| the recency index (library "Recents" sort) | `PlayLogStore.Recency` / `PlayRecency`, fed by `RecentsRecency.Stamps` on `Adopt` and `RecentsRecency.TrackStamps` after hydration (`:2185`, `:2404`) | nothing | `Column<int> LastPlayedAt` (seconds since app epoch) on every kind table, max-merged — this removes the sidecar file entirely. Keep `play-log.json` (the 200-entry ring) for the sidebar's "recently played" projection |
| the navigation log (History) | `HistoryStore` + `history.json` (cap 500) | nothing | `Shell.History`: a `List<(Route, DateTime)>` + `Signal<int> Version` in `Shell.cs` CORE; the JSON in `Shell.Host.cs`. It is **not** entity data and must not become an edge |

---

## 8. Pure rules to port verbatim

| name | 0.2.9 file | what it decides | tests | 0.3 destination |
|---|---|---|---|---|
| `RecentsView.ContentTypes` | `RecentsView.cs:109-123` | the distinct `content_type_*` tokens, first-seen order, no duplicates | `RecentsViewTests.cs:54,67,74` | `Entities/Recents.cs` CORE |
| `RecentsView.Matches` / `PivotAvailable` / `Filter` | `:132-165` | the chip predicate (incl. the `kind:artist` pivot, decided from the hydration uri, not `ContentType`) and the display map | `:83,90,101,115,132,151` | same |
| `RecentsView.BuildSections` | `:169-227` | one synthetic header per calendar day over an already-filtered map; **all nine index maps** (`Items, HeaderIndices, HeaderLabels, HeaderDates, FlatToRow, FlatToDay, FlatToMonth, RowToFlat, RowToDay`); content-only, no trailing dock spacer | `:180,202,227,259,282,321,352,389` | same |
| `RecentsView.Relabel` | `:233-243` | rebuild **only** the labels from settled dates (the midnight path) | `:428` | same |
| `RecentsView.CountForDay` | `:248-255` | the count a day header states | `:302` | same |
| `RecentsView.DayBucketLabel` | `:259-275` | Today / Yesterday / weekday (days 2–6) / abbreviated month-day, compared in `now`'s offset across a year edge | `:166,458,477` | same |
| `RecentsView.PendingSeedRows` | `:282-291` | the 8 skeleton rows, stamped off the **caller's** clock | `:389,409` | same |
| `RecentsView.HydrationUri` / `TargetKind` | `:300-316` | ContextUri → first child → own Uri | `:493` | same (renamed `EntitySlotOf`) |
| `RecentsView.CollectRange` | `:328-349` | the misses a realized window owes, exclusive end, dedup, cap | `:512,524,544,559` | **port, then retire** — 0.3 demands the whole model; keep it only if the planner grows a priority lane keyed on the visible range |
| `RecentsView.CanExpand` / `MissingMembers` / `DrawerEntries` / `CollectChildUris` | `:356-414` | chevron eligibility, the once-per-session diagnostic, what the drawer lists, the drawer's prefetch | `:574,587,604,619,627` | `Entities/Recents.cs` CORE |
| `RecentsView.MetaFor` | `:418-425` | which sentence a row states (PlayedAt / PlayedCount / SavedCount, Saved ≥ 1) | `:643` | same |
| `RecentsView.DayDensity` + `DensityLevel` + `PlayContribution` | `:430-516` | the whole calendar: months newest-first, per-day counts, the log ramp, the busiest-day tie rule (ascending walk + `>=` ⇒ newest wins), `FirstDayOffset` from the culture | `:655` | same |
| `RecentsCalendarMonth.WeekCount` / `RecentsView.MaxWeeks` | `:71`, `:499-505` | the grid's real row count (4–6, never a fixed 6) and the `GridFit` estimate seed, floored at 1 | `:708,741,760` | same |
| `RecentsView.FirstOccurrence` | `:530-541` | which rows may claim the morph tag (first occurrence of each uri only) | `:772` | same |
| `RecentsView.PlayedAt` + `ShortMonthDay` + `DateOf` | `:549-579` | today → time, < 7 d → abbreviated weekday, this year → abbreviated month-day, older → short date; 0 → `""` | `:787,805` | same |
| `RecentsView.Summary` | `:609-639` | the count + the day-window + the "grouped from N plays" clause (only when grouping actually hid plays); endpoints formatted as **day words**, not clock times | `:813,827,835,850,862,876,890` | same |
| `RecentsView.ChipLabel` | `:643-644` | the wire token IS the label when the app has no key | `:898` | same |
| `RecentsView.OwnerSubtitle` | `:653-661` | never show a raw base62 id; resolved name wins; store name only when it differs from the id (either spelling) | `:910,917,926,933` | same |
| `RecentsView.AccentSourceRow` | `:670-686` | the row the accent grades from: first Row-kind item at/after the sticky header **inside the same day**, 8-item bound | `:941,962,979,996` | same |
| `RecentsPage.MonthCardHeight` | `RecentsPage.cs:858` | `56 + 36·weeks` — derived from the same consts the card lays out with | *(none — pure but untested)* | `Entities/Recents.cs` CORE; **add a test** |
| `RecentsPage.ProjectSticky` / `StickyMetrics` | `:1269-1278`, `:1363-1385` | the sticky gate's packed key and the `push` formula | *(none)* | `Entities/Recents.cs` CORE; **add a test** |
| `RecentsPage.HeaderFlatFor` / `MonthFor` / `MapInToOut` / `MapOutToIn` | `:602-643` | the semantic-zoom anchor maps — flat↔month, and the "exact day first, else the month's first header" rule `MapOutToIn` applies (`:632-642`) | *(none — pure but untested, and a wrong answer here sends the zoom to the wrong month)* | `Entities/Recents.cs` CORE; **add a test** |
| `RecentsPage.ContentTypeOf` | `:1224-1233` | the recycle-pool id per row kind (`0` = header, `1 + (int)Kind` = row) | *(none)* | `Entities/Recents.cs` CORE |
| `RecentsPage.DensityFill`'s alpha ramp | `:1060-1070` | `A = A_subtle + (ink.A − A_subtle)·clamp(level,1,5)/5`, transparent at level 0 — the half of the heat ramp that lives outside `RecentsView` | *(none — `DensityLevel` is tested, its paint is not)* | `Entities/Recents.cs` CORE; **add a test** |
| `RecentsPage.MonthCardHeight`'s callers' seed | `:878`, `RecentsView.MaxWeeks` | the grid estimate must come from the **tallest** month, floored at 1 | `RecentsViewTests.cs:708,741,760` | same |
| `RecentsList.Group` | `Wavee.Core/Library/RecentsList.cs:139+` | the flat-items → grouped-rows fold (group headers absorb their `group_id_<N>` members) | `Wavee.Tests/RecentsListTests*.cs` | `Spotify/Spotify.Decode.cs` CORE |
| `RecentsRecency.Stamps` / `TrackStamps` | `Wavee.Core/Library/RecentsRecency.cs` | which uris a snapshot stamps as "last played" | `Wavee.Tests` | `Entities/Recents.cs` CORE (writes `LastPlayedAt`) |
| `PlayRecency` | `App/PlayRecency.cs:11-74` | max-merge stamping, cap 4096, trim to 3840 in one pass | `Wavee.Tests/PlayRecency*` | `Entities/Entities.cs` or retire into `LastPlayedAt` columns |
| `HistoryEntry.Kind`, `PassesFilter`, `PassesSearch`, `DateGroupLabel`, `FormatTimestamp`, `CountUniqueRoutes` | `HistoryPage.cs:28-36`, `:260-310` | the entire History decision set | *(none today — **extract and test in 0.3**, per CLAUDE.md's "no source-text tests" rule)* | `Shell/Shell.cs` CORE (`Shell.History`) |
| History's **visible-list** build | `HistoryPage.cs:195-203` | reverse iteration (newest first) → filter → search; one pass, one list | *(none)* | same; **add a test** |
| History's **Most-visited** fold | `:205-222`, `:371-379` | count map over the **full unfiltered** log keyed on `Route.Name`; stable sort by count desc; then dedup keeping the first (= most recent within a count) | *(none — the "stable" claim in the comment is the whole rule and nothing pins it)* | same; **add a test** |
| `HistoryStore` retention | `:45`, `:84-90`, `:108-119` | cap **500**, FIFO evict the oldest on `Add`, and `SaveToDisk` writes only the newest `min(count, 500)` | *(none)* | `Shell/Shell.cs` CORE + `Shell.Host.cs`; **add a test** |
| `HistoryStore` dead-seed purge | `:72-82` | on load, drop every entry whose route starts with `"pl:local:"` (the removed fake-history seed) and **rewrite the file** so the log stops carrying them | *(none)* | `Shell/Shell.Host.cs`; **add a test** — a silently-dropped purge resurrects dead rows for every existing install |
| `HistoryStore` persistence shape | `:19`, `:57-67`, `:118` | `HistoryEntryDto(Name, Arg, TicksUtc)` — UTC ticks, not a `DateTime`, to dodge `DateTimeKind` round-trip; read back as `.ToLocalTime()`; a corrupt file starts empty rather than throwing | *(none)* | `Shell/Shell.Host.cs` |

---

## 9. Re-author notes

### 9.1 What must not be simplified

1. **The `Shape`.** Rows, the chip's cut, morph flags, sections, calendar and the *stateful* `GroupedListVirtualLayout` are one immutable object swapped by a single reference write, and every engine-invoked member captures `var s = _shape;` **once** at its entry (`:121-152`). The old six-loose-fields shape had a real bug class: a reader landing between two of the six saw one generation of sections against another of everything else. Keep the single-swap discipline and keep the publish ORDER: build → reuse-or-build layout → prime the extent table → construct `Shape` → publish `_shape` → replace the VC snapshot → reset interaction state → bump `_shapeEpoch` **last** (`:2204-2266`).
2. **Two epochs, not one.** `_epoch` = "the same list, new facts" (re-render the realized window, keep scroll, keep the drawer). `_shapeEpoch` = "a different list" (remount by Key). Midnight relabelling bumps `_epoch` only — the grouping did not move (`:2268-2289`).
3. **The accordion's stale-identity check.** `ToggleExpanded` resolves the index against the *current* `Shape` and rejects when the ItemId no longer matches (`:1754-1776`). Event callbacks outlive their shape generation by one reconcile turn.
4. **The off-screen drawer extent normalization.** An open drawer that has been recycled offscreen has no live node to report a collapsed height, so its cached measured extent must be snapped back — through `_listController.CorrectMeasuredExtent` (which preserves the visible anchor and rebases in-flight scroll intent), falling back to `Layout.SetMeasured` when not mounted (`:1785-1799`). A realized old row must **not** snap: it follows the `SizeMode.Reflow` exit and eases back to 64.
5. **The two-node drawer transition.** Outer = `Size` only, a **column** so its child is main-axis sized and the host measures the drawer's true height; inner = `Opacity | Position` inside the clip. The single-spec version (Size|Opacity|Position on the clipping row) stalled 333 ms and then snapped, because `SizeMode.Reflow` pins the node's height per tick, a cross-stretching row hands that pinned height to its children, and the natural-target pass then read the sliver as the real extent (~18 DIP) (`:1585-1613`). `SuppressDescendantTransitions: true` on the outer stops late-hydrating rows starting a second geometry wave.
6. **`WaveeEntrance.DelayMs`, not `Element.Stagger`, for drawer children.** `Stagger` is `index × ms` with **no ceiling**; a "played 40" drawer authored that way was still arriving 1.6 s after it opened (`:1615-1622`).
7. **One tagged node per morph key.** Only the first occurrence of a uri may carry `morphKey` — the engine's registry is last-writer-wins and `SetTaggedVisible` hides *every* node with the key, so a second tagged row blanks itself mid-fly (`:1694-1701`).
8. **The calendar's fixed cell.** `Width = 38, Height = 32, Shrink = 0` — never `Grow = 1` (`:837-839`). And `month.WeekCount`, never 6 (`:966-967`). And lead/trail days are **blank spacers**, not dimmed numerals: drawing them put one day on screen twice with two different densities and a tooltip reading the other card's data (`:1099-1103`).
9. **One "today" cue.** The numeral goes Semibold in the accent ink. The 700 weight, the accent dot, the accent ring and the accent glow are all deleted on purpose — four cues for one day, three of them spending the accent on *geometry* (`:1108-1111`).
10. **No dock reserve anywhere.** The shell clips the content region above the player bar, so a reserve — as padding or as a trailing spacer item — parks a dead band at the end of the scroll and makes the rail advertise unreachable range (`:383-388`, `RecentsView.cs:9-13`). `Spacing.L` of tail padding is breathing room, not a reserve.
11. **The rail is memoized on `(shapeEpoch, MeasuredVersion)`**, and `MeasuredVersion` bumps only on a *real* extent delta (`:96-104`, `:730-733`, `VirtualLayout.cs:643-648`). The predecessor — a 128-DIP `ContentH` bucket — could miss a correction that left the total inside the same bucket.
12. **The explicit `shimmerSource`.** The content is a stateful component (semantic zoom + its controllers) that must not mount while pending, so the deriver would hit an unrendered `ComponentEl` and fall back to ONE 160×10 bar. `PendingContentSource` runs the real builders against the seed shape (`:360-364`).
13. **`ContentType` per row kind.** One recycle pool per kind, so a group card's slot never rebinds into the track-grid shape (a cross-shape reuse forces a full rebuild instead of a cheap rebind) (`:1186-1189`, `:1224-1233`).
14. **History: the divider's 60-DIP left inset** (`Spacing.M + 36 + Spacing.M`) is what makes the card read as a list rather than a table. The two lists differ on the **last** divider and both spellings must survive: Most recent trims it (`showDivider: k < rows.Length - 1`, `:325`), Most visited leaves every one in (`showDivider: true`, `:378`, with the "just leave it, the card clips" note at `:380`). Do not "tidy" one into the other — the flat card genuinely reads better closed, and the grouped card genuinely reads better open.
15. **The chevron-less lane, the zero-count caption and the invisible sticky band.** Three places where 0.2.9 keeps a node it could have dropped: a `!CanExpand` row gets a 24 × 24 transparent spacer (`:1669`), a zero-count day header gets `Caption("")` (`:1433`), and a sticky band with no day goes to `Opacity 0` with `HitTestVisible false` rather than un-mounting (`:1459-1461`). Each is load-bearing for column alignment or for avoiding a mount/unmount thrash on the scroll-hot path.
16. **The scroll keys.** The list's `ScrollKey` carries the **pivot token** (`"recents:" + (token ?? "all")`, `:1192`) so each pivot remembers its own offset; the calendar's carries the **shapeEpoch** (`:889`) so a rebuild deliberately forgets. `BuildShape` then resets `_calendarDay` to today (`:2264`) and re-resolves the sticky band from the live offset (`:2258-2263`). Three deliberate, different answers to "what survives a re-cut" — copying one over the others changes behaviour.
17. **The DEBUG sticky-alignment probe.** `ProbeStickyAlignment` / `ProbeFlatDate` (`:1315-1361`) `Debug.Fail` when a realized row's calendar day disagrees with its section header, or when the pinned header's day disagrees with the first visible row. It is `[Conditional("DEBUG")]`, costs the Release build nothing, and it is what turns the "Today over July" class of bug into a stop rather than a screenshot. Port the assert, not just the layout.

### 9.2 Traps

- **Props freeze at mount.** `RecentsRowSlot` renders `Embed.Comp(new HydratedRecentsRow.Props(page, row, rowIndex), () => new HydratedRecentsRow())` with **no `Key`** (`:1495-1501`). A `Key` there is inert anyway — a component's single root child is paired by `ElementTypeId` alone (`ReuseGuard.KeyIgnoredInSingleChildSlot`) — and a *factory closure* freezes the first row, which is exactly the "Tue/Wed under a Yesterday header" bug. Re-pushed props are the mechanism.
- **`UseLoadable` + `DepKey.From(epoch, RowIndex)`.** The effect must include `RowIndex` so a **recycle** re-resolves (including `SetPending`), or a reused slot shows the previous row's facts while the new one is still a pointer (`:1524-1533`).
- **`UseActivation` ordering.** `CollapseExpanded` runs in `onActivated` (while row render effects are live — writing it after parking defers row reconciliation behind the page replay budget); `ResetExpandedExtent` runs in `onDeactivated` (KeepAlive has begun parking). Swapping them replays the old drawer out of the parked scene on the first return frame (`:331-350`).
- **`Motion.ReducedMotion` is a VALUE, never a hook branch.** `AccentTransitionMs`, `HeroStaggerMs` and `WaveeEntrance.DelayMs` all read it at the call site. Gating a hook on it changes the hook COUNT between renders and crashes the reconciler the moment the flag flips mid-session — a resize grip flips it (`:65-68`, `WaveeMotion.cs:86-92`).
- **The grading `Watch` belongs to one leaf.** `RecentsAccentBinder` is a 0×0, always-mounted node that owns the subscription; the page's `Render` must never subscribe to a grading arrival itself, or every scrolling batch re-renders the page (`:398-402`, `:2462-2470`).
- **`Grow` on a `ComponentEl` root is mirrored onto the flex child.** `RecentsRail` must NOT put `Grow = 1`/`AlignSelf = Stretch` on its root — that would steal **width** from the list. The HStack item is a stretch *column* around it (`:672-680`, `:720-726`).
- **The drawer's two indices are not the same number.** `Drawer` skips members with an empty uri, so the child's **rendered ordinal** is `slot = rendered.Count` (what the `#` cell shows) while its **Key** is the wire index `i` (`:1813-1834`). Capturing the loop variable instead read `entries.Count` in every child ("3", "3" for two rows), and keying on the ordinal instead of the wire index makes the keys shuffle when an empty entry appears. Keep both, and keep them distinct.
- **`WaveeType.ModuleHeader(title, meta)` cannot carry a bound accent.** The span alias is a `SpanTextEl` whose `Color` is a plain `ColorF` with no `BrushTransitionMs`, so the month card builds its title and meta as **two nodes on one centred row** instead (`:992-1015`). The same constraint is why `DayHeader`'s `HoverColor` is a read-and-subscribe rather than a `Prop` (`:1416-1427`). A re-author that "simplifies" either back into the alias silently deletes the accent bind.
- **The artwork-hidden setting applies to BOTH track arms.** `appearance.trackArtwork.hidden` drops the thumb track from the drawer row *and* from the single-play row (`:1930-1933`, `:1992-1995`) — two `ColumnSet`/`TrackSize[]` pairs, four static arrays. It does **not** touch the 48-DIP group-card cover, which is the card's identity rather than a track column.
- **`ErrorState.Build` writes a log line every time it is built** (`ErrorState.cs:18-20`), so it must stay on a branch that renders once per failure, not inside a hot path.
- **Zero-allocation scroll frames vs per-row richness.** 0.2.9's answer, which must survive: the row holds **no copied strings** (it resolves its uri against the store at render time), the bound projection carries `(epoch, version, collection)` so exactly the realized slots re-render, the pump's scratch lists (`_batch`, `_ownerBatch`) are reused across pumps, `_resolveAccentDay` is a pre-created delegate so the scroll-hot `UpdateSticky` path allocates no closure, and every accent consumer is a bound `Prop` rather than a re-render. In 0.3 the equivalents are: handles (no strings at all), `Version` compares on bound rows, and `Prop.Of` over the accent signal. **Do not** re-introduce per-frame `Func` allocation in `UpdateSticky`/`ProjectSticky`.

### 9.3 Where the plan is wrong or too thin

1. **§2 had no home for either page when this chapter was first written.** Not in `Entities/`, not in `Shell/`, not in `Screens/`. Left unfixed the re-author would have stuffed Recents into `User.Page.cs` ("library, liked, profile pages"), which is wrong: Recents is a *server feed over a synthetic parent* (like Home and Search), not a library edge read. Settled (ch 16 §9.4) budget lines, now in §2:

   ```
   ├── Recents.cs  Recents.UI.cs  Recents.Page.cs                       650 + 550 + 1,150   (owner P, Wave 5)
   Shell.cs        + Shell.History (core)                                 +180                (owner I, Wave 4)
   +Shell.History.UI.cs   (the history page)                              400                (owner I, Wave 4)
   ```

2. **§4.13's `UseEffect` demand pattern does not survive 1,708 pointers.** `Entities.EnsureRows(_a.TrackSlots, TrackFields.Row)` is fine for a 12-track album. Recents' snapshot is ~1,708 grouped rows over ~9,446 plays, of which **~1,388 uris repeat** (`RecentsList.cs:5-6`); 0.2.9 hydrated only what the user realized (`OnVisibleRange` + a 64-uri cap). CLAUDE.md's rule ("a page demands its whole model; the query layer batches 300/POST") is right and must be kept — but the plan's `Fetch.Plan` needs (a) **dedup by uri before slotting**, which collapses 1,708 asks to ~320, and (b) a **priority lane** so the realized window's rows resolve first. Neither is in §4.5. Flag this to the Wave 1 owner C.
3. **§4.3's `Edges` list has no Recents table** and no member sub-table. The `HomeSection`/`SearchResult` synthetic-parent precedent is there; Recents needs the same plus a rich payload (§7.2).
4. **§4.1's `Authority` ladder has no rung for a pointer list.** A recents row supplies a uri and a timestamp and **nothing readable** — Title/Subtitle/Image are null on the wire *by design* (`RecentsPage.cs:25-27`). It must be able to allocate a slot without writing any identity group and without lowering an existing row's authority.
5. **§4.12's `Track.Row` is one shape for every list.** This page needs **three**: the 64-DIP `MediaCard.Row` container card (group arm), the 40-DIP 6-track child grid, and the 64-DIP 6-track single grid — all three with different column sets. Chapter 01-track-row.md owns the row; §4.12 should say `RowStyle` carries the track array + column set, not just booleans.
6. **The cover palette is missing from the whole data model** (§7.2). Recents is the app's only `WaveeAccentCtx` *provider*, so if grading has no home, this page loses its identity entirely.
7. **§5 originally listed five Wave 5 owners and no Recents/History file**, so nobody owned them; settled (ch 16 §9.4) as Recents = owner P, Wave 5, and History = owner I, Wave 4 (Shell) because `HistoryStore.NavCtx`/`GoWithOrigin`/`BackCtx` are shell contexts every page consumes (`HistoryPage.cs:134-145`) — they must exist before Wave 5 pages compile.
8. **§6 migration table** puts "tests of hydration/store merge/query plumbing" in Delete. `RecentsViewTests.cs` (1,009 lines, 57 facts) is **not** that — it is the pure-rules file for this surface and belongs in the Port row.

### 9.4 Line budget

| | 0.2.9 | plan §2 target | honest 0.3 estimate |
|---|---|---|---|
| Recents page (shell, zoom, rail, sticky, accent, calendar surface) | 2,499 | **`Recents.Page.cs` 1,150 (P, Wave 5)** | ~1,150 (`Recents.Page.cs`) |
| Recents rows / headers / cells / drawer | *(inside the above)* | **`Recents.UI.cs` 550 (P, Wave 5)** | ~550 (`Recents.UI.cs`) |
| Recents pure rules | 687 | **`Recents.cs` 650 (P, Wave 5)** | ~650 (`Recents.cs`) |
| Recents wire fold (`RecentsList.Group`) | ~180 (Wavee.Core) | folded into `Spotify.Decode.cs` | ~150 |
| History log | 146 | **`Shell.cs` + `Shell.Host.cs` (I, Wave 4)** | ~180 (in `Shell.cs` + `Shell.Host.cs`) |
| History page | 470 | **`+Shell.History.UI.cs` 400 (I, Wave 4)** | ~400 (in `+Shell.History.UI.cs`) |
| **total** | **3,982** | **3,080** | **~3,080** |

The ~900-line saving is almost exactly the hydration machinery that disappears: `_inflight`/`_batch`/`_ownerBatch`, `OnVisibleRange`, `Pump`, `CollectUnresolvedOwners`, `HydrateChildren`, `Pending`, `HydrateAsync`, `MarkStoreDirty`, `FactsFor`×2, `OwnerSubtitleFor`, `ResolveTrack` and the `RowFacts` plumbing (`RecentsPage.cs:177-215`, `:289-300`, `:1541-1583`, `:2009-2016`, `:2291-2423`). **Everything else must survive.** A budget under ~2,800 for Recents means something in §9.1 was dropped.

### 9.5 Files now in the §2 tree for this surface

`Recents.cs`, `Recents.UI.cs`, `Recents.Page.cs` (owner P, Wave 5); a `Shell.History` section in `Shell.cs`, `+Shell.History.UI.cs` (owner I, Wave 4); `history.json` + `play-log.json` + `play-recency.json` persistence in `Shell.Host.cs`/`Entities/Store.cs`. Also note `assets/loc/*.json` already carries `recents.*` and `nav.history.*` and moves as-is per §2 — **do not re-key them**.

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe`

**Read this first.** On `--fake` the Recents *list* cannot be shown: `NullRecentsService.FetchAsync` returns `RecentsSnapshot.Empty` (`RecentsList.cs:124`), so items **1-4, 7-11 and 40-48** are the only Recents items verifiable in `--fake`; everything else needs a **live signed-in session** on both builds (run them one at a time — see CLAUDE.md on `%LOCALAPPDATA%\Wavee` and the memory note on never disturbing the user's running instance). History is fully verifiable in `--fake`: navigate 10-12 routes first to seed the log.

Route in with `wavee://open?route=recents` / `wavee://open?route=history`, or Ctrl+K → "Recents". Window `--width 1180 --height 760` unless stated.

| # | check | how |
|---|---|---|
| 1 | Masthead word is **light (400)**, 40/52, display face, not bold | static capture, zoom to the glyph stems |
| 2 | "Overview" button sits on the title's centre line, height 24, subtle (no plate at rest) | static capture |
| 3 | Title and summary arrive **45 ms apart**, both rising 10 DIP | frame recording of the first 500 ms after navigation |
| 4 | Four pivot tabs render on frame one, gap 20, SemiLight | first-frame capture |
| 5 | Summary reads `N items · <from> – <to>` and adds `· grouped from M plays` only when M > N | live; compare strings |
| 6 | Summary box does not reflow as facts resolve (220 reserved) | frame recording; watch the "Overview" button's x |
| 7 | Pivot underline plays `scaleX 0→1` from the LEFT over 260 ms on every switch | frame recording, switch All→Music→Podcasts |
| 8 | A pivot with no rows is dimmed and un-clickable, **never hidden** | `--fake` (all three dim) and live |
| 9 | Empty state is the `PageHero` sentence "Play something and it'll show up here", centred, no glyph, no button | `--fake`, static capture |
| 10 | Offline error shows "Something went wrong." + subtitle + a **Standard** (not accent) Retry | pull the network, navigate, capture |
| 11 | Page gutter is 36 on both sides at every width | capture at 1180 and at 700 |
| 12 | Day header = label · 1px rule · "N items", band height 48 | static capture, measure |
| 13 | Hovering a day header eases its ink to the page accent in ~83 ms | hover capture + frame recording |
| 14 | The pinned header carries `FillLayerDefault`; the inline one is transparent | capture both themes |
| 15 | Rows scroll **under** the pinned band with a 24-DIP feather, never through it | slow-scroll frame recording |
| 16 | The outgoing day header is pushed up by the incoming one, quantized (no jitter) | frame recording at the boundary |
| 17 | Wheel over the pinned header scrolls the list with the same inertia as wheel over the body | frame recording, wheel on both |
| 18 | Group row is 64 tall, 48 cover r4, transparent at rest | static capture |
| 19 | Artist rows have a **circular** cover and **no** chevron | live, find an artist row |
| 20 | Liked Songs row shows the bundled cover, title "Liked Songs", and **no** type chip | live |
| 21 | Type chip is a capsule, `Eyebrow` type, tertiary on subtle fill, right of the text column | static capture |
| 22 | Row hover: fill lifts, play FAB appears centred on the cover, "…" goes 0.45 → 1 | hover capture |
| 23 | The FAB does **not** appear when the pointer has not moved since mount (nav back with the cursor over a row) | navigate away and back without moving the mouse |
| 24 | Chevron **swaps** glyph (▸ → ▾) and goes accent when open — no rotation | frame recording |
| 25 | Exactly one drawer open at a time; opening a second closes the first | click two group rows |
| 26 | Drawer opens over ~333 ms with the window growing and the content fading/dropping **inside** it | frame recording |
| 27 | Drawer collapse is faster (~167 ms) than the expand | frame recording |
| 28 | Drawer children stagger 40 ms each, capped at 8 (item 9+ lands with item 8) | frame recording on a "Played 20+" row |
| 29 | Drawer spine is 1 DIP, accent-coloured, 32 from the row's left edge; children a further 24 | static capture, measure |
| 30 | Drawer rows are 40 tall with `# ♥ art title … duration when ⋯` and every "…" aligned | static capture |
| 31 | A Saved row shows two 40-DIP member covers stepped behind the 48 context tile, and a green ✓ meta line | live, find a Saved row |
| 32 | Scrolling across a day boundary re-tints the pivot underline, the spine and the wash — **once**, gliding over 250 ms | frame recording across two day headers |
| 33 | Fast-flick across five days moves the accent exactly once, to the day it settles on | frame recording of a fling |
| 34 | The shell chrome (title row / sidebar / dock) carries the page's colour, top-left-weighted | full-window capture |
| 35 | Settings ▸ Appearance ▸ colour washes OFF → chrome goes neutral and the accent drops to the system blue **live** | toggle while Recents is open |
| 36 | Rail labels are abbreviated months, with `MMM yy` at a year change | live, scroll to a year boundary |
| 37 | Rail thumb is a 30×3 accent bar; each day is a 3×3 tertiary dot | zoomed static capture |
| 38 | Hovering the rail shows a ghost thumb **and** a flag naming that day | hover capture |
| 39 | The rail's bottom label sits at the bottom of the track (no dead band) | static capture; cross-check the `recents.rail` log line's `lastMinusMax` |
| 40 | "Overview" zooms out: the calendar enters from 1.08 while the list recedes to 0.94, cross-fading | frame recording |
| 41 | Esc zooms back in | keyboard |
| 42 | Clicking a day header zooms out **anchored on that month** | click a "Yesterday" header, capture which month is in view |
| 43 | Month cards are exactly 290 wide with 36 gutters; 2 columns at 1180, 3 at ~1600 | capture at both widths |
| 44 | A 4-week month card draws 4 week rows, a 6-week month 6 — no clipped bottom row, no dead band | capture Feb and a 31-day month starting late |
| 45 | Weekday initials are rotated by the system's first-day-of-week | switch Windows to a Monday-first locale |
| 46 | Lead/trail days are **blank**, not dimmed numerals | static capture of any month edge |
| 47 | Today's numeral is Semibold in the accent — and has **no** dot, ring or glow | static capture |
| 48 | The legend's five swatches match the cells' five levels exactly | static capture, sample pixels |
| 49 | Hovering a cell updates the band to `dddd d MMMM` + `N plays · top: X · Jump to d MMM` | hover capture |
| 50 | A zero-play day reads "Nothing played", never "0 plays" | hover an empty cell |
| 51 | "Jump to …" appears only for days the list actually has a header for | hover a day inside the range with no rows |
| 52 | Leaving the grid resets the band to today | move the pointer off and capture |
| 53 | Clicking a cell with rows zooms in to that month's day | click and capture the resulting scroll position |
| 54 | Right-click a group row → the container menu with header art, transport strip, then the grouped rows | right-click capture |
| 55 | Right-click a drawer row → the single-track menu | right-click capture |
| 56 | Dragging a group row shows the resource drag chip; dragging an unsupported kind starts no drag | drag onto the sidebar |
| 57 | Leaving and returning to Recents restores the scroll position and shows **no** open drawer | open a drawer, navigate away, come back |
| 58 | Crossing local midnight relabels "Today" → "Yesterday" **without** losing scroll or rebuilding the list | set the clock forward with the page open |
| 59 | History header: Clock 22 + `PageHero` + two stat pills + Clear all, on one line | `--fake`, static capture |
| 60 | History rows are 56 tall inside a rounded card with hairlines inset 60 from the left | `--fake`, measure |
| 61 | A playlist row's icon cell is accent-tinted (α 0.12); every other kind is neutral | `--fake` capture |
| 62 | A retired route renders at 0.6 opacity, is unclickable, and still deletes | `--fake`: hand-edit `history.json` to add a bogus route name |
| 63 | Group buckets are Today / Yesterday / This week / This month / Earlier with a count pill on each | `--fake`, navigate across days or edit the log |
| 64 | Timestamps read `14:22`, `Yesterday, 14:22`, `Monday, 14:22`, `Sep 3 2025, 14:22` by age | `--fake` with edited timestamps |
| 65 | Sort → "Most visited" collapses to one row per route, adds `N×` badges, and swaps the header to "MOST VISITED" (no count pill) | `--fake` |
| 66 | Searching filters on title, arg **and** route name, case-insensitively, and the empty state becomes `No results / No history matches "x"` | `--fake`, type a nonsense query |
| 67 | Filtering to a chip with no rows gives `Nothing here / Try switching to "All"…` | `--fake` |
| 68 | A never-navigated profile gives `No history yet / Start navigating…` | `--fake` with `history.json` deleted |
| 69 | Clear all opens a ContentDialog whose **default** button is Cancel, and only then wipes | `--fake` |
| 70 | History always opens in its **own tab** (from the back-button flyout's "View all history") | `--fake`, click Back's context flyout — **navigate 9+ routes first**: the item only appears when the log holds more than 8 (`ShellToolbar.cs:73-74`) |
| 71 | A now-playing drawer/single row's **title** goes `AccentTextPrimary` and its `#` cell becomes the equalizer; the row's fill does **not** change | live, start a track from a drawer and capture |
| 72 | A now-playing group row shows the now-playing overlay on its **cover** and keeps a primary-ink title | live |
| 73 | A row whose kind cannot expand (artist, most Liked rows) still aligns its "…" with an expandable row's — the chevron lane is reserved, not collapsed | static capture, measure the "…" x across a mixed run |
| 74 | A row the wire gave no instant shows no time caption and the cluster closes up — the "…" and chevron stay put | live, or a hand-built snapshot |
| 75 | Liked Songs and any Unknown-kind row have **no** type chip; every other kind has exactly one | live |
| 76 | Before the first sticky resolve (list pinned at the top) the band is invisible but the inline "Today" header is visible — no double header, no gap | navigate in and capture frame one |
| 77 | A month with no plays reads "Nothing played" in its meta line, not "0 plays"; the current month appends "· so far" | Overview, scroll to an empty month |
| 78 | Switching All → Music → All restores **All's** scroll position, not the top | scroll down, switch twice, capture |
| 79 | Opening the Overview after a pivot switch shows **today** in the readout band, not the previously hovered day | switch pivot with the overview open, then reopen |
| 80 | History: the last row of a *Most recent* card has no hairline; the last row of the *Most visited* card does | `--fake`, zoom to the card's bottom edge in both sorts |
| 81 | History: a `browse` route filters under **Pages** but labels itself **Page** (there is no "Browse" kind label) | `--fake`, navigate to a browse hub, then filter |
| 82 | History: leaving and returning starts at the **top** (no scroll restore) — this is the 0.2.9 behaviour, not a regression | `--fake`, scroll, navigate away, return |
| 83 | A legacy `pl:local:*` entry in `history.json` is purged on load and the file rewritten | `--fake`, hand-add one, restart, re-read the file |

---

## 11. Audit log

Adversarial re-read of the chapter against the 0.2.9 sources (2026-09-12). Every number in §2, §3 and §5 was re-derived from source; the ones not listed below were confirmed correct.

**wrong / imprecise — fixed in place**

1. **§9.1 #14 (History dividers)** — claimed "the last row's divider is deliberately left in place because the card clips it" as if it applied to both lists. It applies only to **Most visited** (`HistoryPage.cs:378-380`); **Most recent** trims it (`showDivider: k < rows.Length - 1`, `:325`). Rewritten as §9.1 #14 with both spellings, and stated in W17/W18.
2. **§6.2 Entry points ("View all history")** — claimed the back-button flyout "lists the 8 most recent routes and then a separator + 'View all history'". The separator and the item are gated on `hasMore = _history.Count > HistoryMenuMax` (`ShellToolbar.cs:73-74`, `:83-87`), so with ≤ 8 navigations **the item does not exist**. Corrected, and parity item 70's "how" column now says to seed 9+ routes first.
3. **§3.1 rail width citation** — `AnnotatedScrollBar.cs:193-195` are `ThumbWidth`/`ThumbHeight`; `LabelsMinWidth = 44f` is at `:196`. Citation corrected.
4. **§3.1 chevron row** — cited `TrackRow.cs:625-644`; the factory ends at `:641`. Corrected, and `FocusVisualMargin 1` + `BlocksDragArm` added (both were missing).

**missing — added**

5. **The now-playing state was absent from the whole chapter** — no §0 item, no wireframe, no token row, no parity item, despite `RecentsPage.cs:2022-2027` recolouring the title to `Tok.AccentTextPrimary` and `MediaCard.cs:982` swapping the cover's hover FAB for the now-playing overlay. This is also the *only reason* the page provides `WaveeAccentCtx.Slot` (`:418-420`), which §1.1 quoted without ever drawing the result. Added §0 #16, **W23**, two token rows, parity 71-72.
6. **The conditional trailing cluster** (`:1670-1679`) — the played-at caption appears only when the wire gave an instant, the "…" only when `CardMenu` resolved, and a `!CanExpand` row gets a **24 × 24 transparent spacer** rather than a collapsed lane (`:1669`). None of this was stated; parity item 19 ("no chevron") read as "nothing there". Added as a table under W3, a token row, §0 #17, parity 73-74.
7. **The type chip is omitted for `Collection` and `Unknown`** (`:2087-2096`). W3 showed the Liked row without a chip but never said why, and the chip's actual per-kind strings were nowhere. Added the `KindLabel` table in the new §3.3, parity 75.
8. **The pinned band's hidden state** — `Opacity 0` / `HitTestVisible false` / never un-mounted when `day < 0` (`:1453-1461`), plus `BuildShape`'s immediate re-resolve from the live offset (`:2258-2263`). Added **W24**, a token row, parity 76.
9. **The month card's three meta states** (`:982-985`) — the chapter showed only `"318 plays · so far"` and never mentioned the zero case reusing `recents.nothingPlayed`. Added a table under W13, parity 77.
10. **The zero-count day header** renders `Caption("")` (`:1433`) — label + rule, no count word. Added a token row and §0 #17.
11. **The degenerate frames** — `svc is null` (`:258`), the `EmptyFlat` sentinel (`:75`, `:1493`), an empty `ItemId` (`:1628`), an empty member uri (`RecentsView.cs:381-388`), `store is null` in History (`HistoryPage.cs:184`). Added as **W25**.
12. **Scroll-key semantics** — the list key carries the **pivot token** (`:1192`) so each pivot keeps its own offset; the calendar key carries **shapeEpoch** (`:889`) so a rebuild forgets; `BuildShape` resets `_calendarDay` to today (`:2264`). History has **no** `ScrollKey` at all. Added motion rows 31-32, §9.1 #16, §6.2, parity 78-79, 82.
13. **Motion absent from §5** — the calendar tooltip (`ToolTip.Wrap`, `:1146`); the fact that the **first** mount of the semantic zoom has *no* transition (`SemanticZoom.cs:240-242`, `KeepAliveOptions.FirstActivation`). Added as rows 29-30, plus an explicit "motion this surface deliberately has none of" paragraph.
14. **Loc keys and glyph codes had no inventory** — added **§3.3** covering every `recents.*`, the borrowed type-chip keys, the full `nav.history.*` set, and all eight glyph codes (each verified against the generated `Icons` table). Notes the two data-level oddities a re-author will otherwise "fix": `podcast.episodes` = "Episodes" on a single episode, and `nav.history.mostVisited` authored ALL-CAPS.
15. **History has no `kind.browse` label** (`HistoryPage.cs:35`, `:421-428`) — a browse route filters under *Pages* and labels itself *Page*. Added to §3.3 and parity 81.
16. **Pure rules missing from §8** — the semantic-zoom index maps `HeaderFlatFor`/`MonthFor`/`MapInToOut`/`MapOutToIn` (`:602-643`, untested, and a wrong answer sends the zoom to the wrong month); `ContentTypeOf` (`:1224-1233`); `DensityFill`'s alpha ramp (`:1060-1070` — `DensityLevel` is tested, its paint is not); History's visible-list build (`:195-203`), its Most-visited fold (`:205-222`, `:371-379`), `HistoryStore`'s retention (cap 500 + FIFO, `:45`, `:84-90`), its **dead-seed purge** (`pl:local:`, `:72-82`) and its `TicksUtc` persistence shape (`:19`, `:57-67`). All added with "add a test" markers. Parity 83 covers the purge.
17. **§9.1/§9.2 re-author traps** — the drawer's rendered ordinal (`slot`) vs wire-index Key distinction (`:1813-1834`); `ModuleHeader(title, meta)` being a `SpanTextEl` that cannot carry a bound accent (`:992-1015`, and the same constraint behind `DayHeader`'s `HoverColor` at `:1416-1427`); the artwork-hidden setting applying to **both** track arms (`:1930-1933`, `:1992-1995`); `ErrorState.Build` logging on every build; the DEBUG `ProbeStickyAlignment` assert (`:1315-1361`). Added as §9.1 #15-17 and four §9.2 bullets.
18. **§6.2 — what History deliberately lacks**: no context menu, no drag, no `ItemsView` (hence no virtualization/type-ahead), no scroll key, no skeleton. And the pivot strip has **no arrow-key navigation** (four independent `Focusable` boxes, not a `SelectorBar`). Added so 0.3 does not "upgrade" either by accident.

**verified correct — no change**

All of §3's geometry (`Spacing` XXS 2 / XS 4 / S 8 / M 12 / L 16 / XL 20 / XXL 24 / PageWide 36; `Radii.Control` 4, `Card` 8, `Full` 999), all type ramps (`SurfaceDisplay` 40/52/400/−12 `WaveeType.cs:146-151`, `PivotLabel` 19/25/350/−6 `:219-226`, `ModuleHeader` 20/28/600/−6 `:63-67`, `PageHero` 28/36/600, `Eyebrow` 12/16/600+30, `TrackTitle` 14/20/600), every §5 duration and curve (`StandardEnter` 300/`FluentDecelerate (0.1,0.9,0.2,1)`, `SmoothOut (0.22,1,0.36,1)`, `ContentResize` spring `FromResponse(0.40,0.90)`, `StandardSpring` `FromResponse(0.35,0.85)`, `DisclosureExpand` 333/`FluentPopOpen (0,0,0,1)`, `DisclosureCollapse` 167/`(1,1,0,1)`, `DisclosureChevron` 167/`(0.167,0.167,0,1)`, `Faster` 83 / `Fast` 167 / `Standard` 250, `StaggerMs` 40 × `StaggerCap` 8 = **320 ms** — the engine's own doc comment saying "360 ms" is stale, the chapter's 320 is right — `MastheadStaggerMs` 45, `Expressive.Slow` 400, `ColdRealizeRamp` 600 nodes / 8.3 × 0.4 = 3.32 ms), the semantic-zoom scales 1.08 / 0.94 and `MaxEntries: 2`, the drawer geometry (`Margin(32,2,0,8)`, inner `Padding(24,0,8,0)`, `Dy −8` presence / `Dy −4` exit and child reveal), the child/single track arrays (`[30·28·32·*1·52·112]` with `ChildActionsCol = 40 + 12 + 60`, `[36·28·32·*1·52·40]`), the calendar constants (38 × 32, gutter 4, band 20, title 28, `CalGridW` 290, `MonthCardHeight = 56 + 36·weeks`), the `GridFit` column formula and its 1/2/3/4-column table, `StickyFadeBand` 24, `AnnotatedScrollBar` 30 × 3 thumb / 3 × 3 tick / `MinTickGap` 4 / flag MinH 40 MaxW 360, `ControlSize.Small` (7,2,7,3)/MinH 24/font 12/icon 14, `SelectorBar` pill 4 × 3 × 4 = 16 and padding (12,10,12,7), `ComboBox.MinHeight` 32, `EmptyState.Centered` Gap 4 / Padding 24, every §4 colour (`AccentSubtle` α `0x24` = 0.141, `StrokeDividerDefault` `#FFFFFF15`/`#0000000F`, `FillLayerDefault` `#3A3A3A4C`/`#FFFFFF80`, `FillSubtleSecondary` `#FFFFFF0F`, `AccentTextPrimary` `#004275`/`#A6D8FF`), the wash geometry (Hero centre (0.06, 0.00), radius (0.74, 0.92), fade 0.62, α 0.055 light / 0.10 dark = 1.82×), all eight glyph codes, all §8 `RecentsView` line ranges, `PlayRecency` cap 4096 / trim 3840, `PlayLogStore.SaveDebounceMs` 2000 and `MaxEntries` 200, `RecentsViewTests` at 1,009 lines / 57 facts, the 1180 × 760 default window (`Program.cs:214`) and the 300-DIP sidebar (`AppSettings.cs:30`), and — confirmed by grep — **zero** references to `ActivityUndo` from `Features/Recents/**` or `HistoryPage.cs`, so §6.1's "there is no undo on either surface" stands.

**token-reconcile (2026-09-12):** one token NAME was mis-cased in §2's day-tick wireframe — `Tok.TextTertIARY` → `Tok.TextTertiary`. Corrected in place. Every value this chapter cites re-verified and left alone: `WaveeType.SurfaceDisplay` 40/52/400 −12, `PivotLabel` 19/25/350 −6, `MastheadStaggerMs` 45, `StaggerMs` 40 with `StaggerCap` 8 ⇒ 320 ms, `MotionTok.DisclosureExpand` 333 / `DisclosureCollapse` 167 / `DisclosureChevron` 167, `MotionTok.ControlFast` **150** (not `WaveeMotion.Fast`'s 167 — the two are different rungs, see `00-design-system.md §12`), and `Expressive.Slow` 400. Index: `00-design-system.md §12.1`.

**consistency 2026-09-12:** header said "NOT IN PLAN §2" and put History in `Shell.UI.cs`; settled per plan §2/§9.4 — Recents is `Entities/Recents.cs` + `Recents.UI.cs` + `Recents.Page.cs` (owner P, Wave 5), History is `Shell.cs`/`Shell.Host.cs` + the named partial `+Shell.History.UI.cs` (owner I, Wave 4). Header, §1.3's tree row, §9.3 item 1's file-plan block, §9.4's line-budget table and §9.5's file list all corrected to match.
