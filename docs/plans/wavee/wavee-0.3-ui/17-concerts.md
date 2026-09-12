# Concerts (hub, artist schedule, concert detail) — 0.3 visual fidelity contract

> 0.2.9 sources: `src/apps/Wavee/Features/Concerts/*.cs` (16 files, 3,547 lines) + `src/apps/Wavee/Components/ConcertUi.cs`
> (1,188 lines) = **4,735 lines**. Per file: `ConcertHubPage.cs` 536, `ConcertDetailPage.cs` 387, `ConcertScheduleModel.cs` 361,
> `ConcertDateFlyout.cs` 354, `ArtistSchedulePage.cs` 321, `MonthBoard.cs` 289, `ConcertFilterBar.cs` 256, `ConcertLocationController.cs` 207,
> `ShyMonthPill.cs` 186, `ConcertHubModel.cs` 163, `ConcertLayout.cs` 121, `ConcertAppendPreloader.cs` 100, `ConcertDetailModel.cs` 97,
> `CountTicker.cs` 72, `ConcertRoutes.cs` 65, `ConcertRoutePage.cs` 32.
> 0.3 target: `Entities/Concert.cs` (CORE: columns, handle, every pure rule), `Entities/Concert.UI.cs` (tiles, date blocks,
> pills, hero, flyout panels), `Entities/Concert.Page.cs` (hub / schedule / detail pages, filter bar, month board, shy pill,
> ticker, append preloader) | Wave 5 owner N.
>
> After Wave 0 every 0.2.9 path below lives at `src/apps/_old/Wavee/<same relative path>`.
>
> Cross-references (do not re-specify): tokens / type ramp / accent budget / materials / motion curves → `00-design-system.md`;
> `PagedShelf` + card grammar → `02-cards-and-controls.md`; masthead / drill trail / page transitions / KeepAlive →
> `18-shell-frame.md`; the Home "Concerts near you" editorial card (which is built by `ConcertUi.WideEditorialDestination` +
> `EditorialArt`, physically inside `ConcertUi.cs` but owned by Home) → `11-home-cards-and-modules.md`; the fused
> `SegmentedPill` at the Home-strip and sidebar registers → `12-home-section-and-customizer.md` / `25-sidebar.md`; the
> artist page's own Tour banner + "Upcoming concerts" shelf (`ArtistPage.Shelves.cs:93-120`) → `08-artist-and-discography.md`;
> the sidebar's concert feed rows (`SidebarFeedSources.cs:109,215`) → `26-sidebar-customizer-and-pipeline.md`.

---

## 0. The non-negotiables

1. **Accent is the DATE, never the plate.** On every concert surface the accent (`WaveeAccent.Decor` = `Tok.AccentTextPrimary`,
   `Design/WaveeTokens.cs:50`) appears only as an eyebrow caption ("Fri, Aug 21 · 19:00", "MAR", "NEXT SHOW · IN 5 DAYS",
   section captions), as the selected filter token's fill, and as the 3×32 near-you rail. There is no large saturated
   accent block anywhere (`ConcertUi.cs:23-24`, `ConcertUi.cs:120`).
2. **The hub filter bar pins under the masthead, not at the viewport top.** `.Sticky(BrowseMastheadMetrics.Reserve = 84)`
   (`ConcertFilterBar.cs:107`) — and the header card above it and the feed below it are clipped **separately** at the same
   line (`ConcertHubPage.cs:114-144`), so the pinned bar's own top is never feathered. One clip around all three is a
   regression you can see instantly.
3. **The when-pill fuses; it never re-lays-out.** Rest `[📅 Dates ▾] [This weekend]` → active `[⟨✓ This weekend⟩ Jul 17 – 19 ▾]`
   is ONE reused node (`Key = "when-pill"`, `ConcertUi.cs:595,638`) whose width reflows over 260 ms while the chip exits left
   (Dx −56) and the docked segment enters right (Dx +56) — the two legs overlap and read as a dock
   (`ConcertFilterBar.cs:126-138`, `ConcertUi.cs:651-667`).
4. **Filter tokens never shift their own label.** A selected token is the same width as an unselected one — no check glyph is
   inserted; the filled accent plate and on-accent ink carry "selected" (`ConcertUi.cs:541-566`). The strip must not walk
   under the cursor.
5. **The count counts.** The events figure eases numerically (380 ms cubic-out) inside a FIXED 64-DIP right-aligned box so the
   caption row never reflows while digits spin (`CountTicker.cs:35-56`), and the per-frame wake exists only while a tween runs.
6. **Every card/tile/row is uniform height and single-line clamped.** `EventTile` height = cardW + 70 exactly
   (`ConcertHubPage.cs:236-240`); every text run is `MaxLines = 1` + `TextTrim.CharacterEllipsis` + `MinWidth = 0`
   (`ConcertUi.cs:113-154`). A grid of tiles is a grid of identical rectangles.
7. **Near-you is a minority marker, not a theme.** The accent rail + pin appear only when
   `0 < near ≤ max(3, ⌈shows/3⌉)` (`ConcertScheduleModel.cs:282-303`); a near-everything response paints nothing.
8. **Copy never sits on a photo.** The schedule/detail hero is a grounded split card: copy on `FillCardDefault`, artwork in an
   adjacent clipped pane with a short gradient blend at the seam only (`ConcertUi.cs:256-326`). No scrim, no white-ink-on-
   arbitrary-photo.
9. **The shy month pill is passive and shy.** 32-DIP acrylic capsule, top-centre, 12 DIP down; appears only once a board header
   has crossed the viewport top, fades 1 s after the last scroll step, and is exempt from hit-testing so it can never steal
   a wheel event (`ShyMonthPill.cs:99-130,136-146`).
10. **Run collapse and the 6-tile cap.** Consecutive nights at the same venue+city are ONE tile ("3 nights" chip, day range
    "14–16") that expands in place; a board over 6 tiles collapses behind "Show all N"
    (`ConcertScheduleModel.cs:86,134-148`, `MonthBoard.cs:50-80,178-236`).
11. **Skeletons are derived from the real subtree.** Every pending state is `Skel.Region` over a seeded model of the SAME
    composition (`ConcertHubPage.cs:519-535`, `ConcertDetailPage.cs:367-386`, `ArtistSchedulePage.cs:302-320`), never a hand
    authored bar layout, and reveal is `SkelReveal.Soft`.
12. **No playback anywhere.** No concert element wires a play callback; the playlist promo card deliberately drops the
    `MediaCard` play FAB (`ConcertHubPage.cs:303-306`). The only draggable concert-surface object is that promo card
    (it stands for a real playlist resource) — concerts themselves are not drag sources (`ConcertHubPage.cs:318-322`).
13. **Ticket buttons are external links only.** `Button.Accent("Buy tickets")` only when `ConcertOffers.ValidTicketUrl`
    returns an absolute http/https URL; otherwise quiet "Tickets not available online" text — never a dead button
    (`ConcertDetailPage.cs:234-242`).
14. **Provider local time is preserved.** Every date/time renders the `DateTimeOffset`'s stored clock, never converted to the
    machine zone (`ConcertDetailPage.cs:154-158`, `ConcertDetailModel.cs:46-59`).
15. **Appends are gated, not chained.** The tail preloader fires only when near-tail AND after a 300 ms arm AND not already
    loading, retries at most 3× then collapses; the page drops `NearTail` after every append so only a fresh scroll
    continues the chain (`ConcertAppendPreloader.cs:20-84`, `ConcertHubPage.cs:404-406`).

---

## 1. Anatomy

### 1.1 Hub — 0.2.9 composition

```
ConcertRoutePage                                  Features/Concerts/ConcertRoutePage.cs:10   route switch (Key per route)
└─ ConcertHubPage (Key "concert-hub")             ConcertHubPage.cs:24                       the `concerts` destination
   └─ Ctx.Provide(LazyScroll.Slot, _pageScroll)   ConcertHubPage.cs:169                      page scroll → the LazyGrid
      └─ ScrollEl  ScrollKey "concert-hub"        ConcertHubPage.cs:163-167                  ONE vertical viewport
         │  OnScrollGeometryChanged = HomeSectionAppendPreloader.NearTailWatch(_nearTail,_pageScroll)  HomeSectionAppendPreloader.cs:46-54
         └─ BoxEl col                             ConcertHubPage.cs:146-162
            ├─ BoxEl spacer H=84+24=108           ConcertHubPage.cs:153                       masthead reserve, HitTestVisible=false
            └─ BoxEl col Gap=16 Pad=(36,0,36,108) ConcertHubPage.cs:155-159                  FamilyUnderBandPad(72+36)
               ├─ Header() .ClipBelow(84)         ConcertHubPage.cs:116-121,173-195          identity card
               │   ├─ Eyebrow "Live music"        ConcertHubPage.cs:184-187                  accent, 1 line
               │   ├─ PageHero "Concerts"         ConcertHubPage.cs:188                      28/36/600
               │   └─ Body subtitle (secondary)   ConcertHubPage.cs:189-192
               ├─ ConcertFilterBar                ConcertFilterBar.cs:25                     Key "concert-filter-bar"
               │   │ .Sticky(84)                  ConcertFilterBar.cs:107
               │   ├─ caption row                 ConcertFilterBar.cs:64-80
               │   │   ├─ Eyebrow "Filter by"     ConcertFilterBar.cs:69-72                  accent
               │   │   ├─ BoxEl Grow=1 (spacer)   ConcertFilterBar.cs:73
               │   │   └─ CountTicker             CountTicker.cs:18   Key "concert-count"    "9,191 events"
               │   │       └─ TickerClock         CountTicker.cs:62                          mounted only mid-tween
               │   └─ ScrollEl horizontal H=44    ConcertFilterBar.cs:92-95                  AutoEdgeFade, no scrollbar
               │       └─ row Gap=12              ConcertFilterBar.cs:85-89
               │           ├─ WherePill           ConcertUi.cs:573      OnRealized → WhereAnchor (flyout anchor)
               │           ├─ Divider 1×20        ConcertFilterBar.cs:255
               │           ├─ WhenArea            ConcertFilterBar.cs:120-139
               │           │   ├─ RestDatePill    ConcertUi.cs:593      Key "when-pill"
               │           │   └─ FilterToken chip ConcertUi.cs:548     Key "when-chip", Exit Dx −56
               │           │   └─ (or) SegmentedDatePill ConcertUi.cs:619  Key "when-pill" + ":seg"
               │           ├─ Divider 1×20
               │           └─ Genres              ConcertFilterBar.cs:150-172
               │               ├─ FilterToken "All"            ConcertFilterBar.cs:154
               │               ├─ FilterToken × TopConcepts(3) ConcertFilterBar.cs:156-163
               │               └─ MoreToken "+N genres"/"Show less"  ConcertUi.cs:679
               └─ BoxEl body .ClipBelow(84)       ConcertHubPage.cs:136-143
                   └─ Skel.Region(_feed, reveal:Soft, group:"concert-hub")  ConcertHubPage.cs:96-108
                      ├─ pending  → shimmer derived from FeedSeed()          ConcertHubPage.cs:519-535
                      ├─ failed   → ErrorState.Build(retry = RequeryFeed)    ConcertHubPage.cs:100
                      ├─ empty    → EmptyState.Build(title, place-dependent subtitle, "Set location")  ConcertHubPage.cs:102-107
                      └─ content  → FeedBody  Gap=20                         ConcertHubPage.cs:198-219
                         ├─ ConcertShelf (Nearby | everything-else)          ConcertHubPage.cs:222-233
                         │   caption = "Near you" only for Kind == Nearby; ANY other non-AllEvents kind falls through
                         │   to "Recommended for you" — there is no third caption (:229-231)
                         │   └─ PagedShelf measured, headerGap 8, maxItems 16, keyOf=Uri
                         │       └─ ConcertUi.VerticalCard → EventTile × n   ConcertUi.cs:157 → :113
                         ├─ PromoShelf "Playlists for the scene"             ConcertHubPage.cs:291-301
                         │   └─ PlaylistPromoCard (draggable, no play FAB)   ConcertHubPage.cs:305-334
                         ├─ EventsGrid "All events"                          ConcertHubPage.cs:242-281
                         │   ├─ seed page  → AutoGrid(240, 12, NaN)          ConcertHubPage.cs:246-261
                         │   └─ real page  → LazyGrid(count→_allEvents.Count, minCol 240, gap 12,
                         │                            rowExtra 86, overscanRows 3)  ConcertHubPage.cs:273-278
                         │       └─ EventCell → ConcertUi.EventTile          ConcertHubPage.cs:283-289
                         │       └─ EventCell renders BoxEl() for an index past the live list (append reconcile skew) :286
                         └─ ConcertAppendPreloader  Key "concert-append:{paginationKey}"  ConcertHubPage.cs:210-217
                             (absent entirely when page.PaginationKey is null — the feed is complete)   :210
                             ├─ far from tail → BoxEl H=16 (quiet spacer)    ConcertAppendPreloader.cs:56
                             ├─ 3 failures    → BoxEl() (tail collapses)     ConcertAppendPreloader.cs:54
                             └─ near tail     → Skel.Region(ShimmerGrid: 4 EventTiles in AutoGrid(240,12))  :58-61,88-99

Anchored overlays (Overlay.Service, FlyoutPlacement.BottomEdgeAlignedLeft, FocusTrap + LightDismiss + PopupChrome.Popup):
├─ ConcertWhereFlyout      ConcertDateFlyout.cs:274   264 wide — Search cities / Use my location / 25·50·100 km radios
├─ ConcertDateFlyout       ConcertDateFlyout.cs:23    320 wide — root ⇄ month leaf (page-slide), calendar range picker
└─ ConcertLocationPickerPanel  ConcertUi.cs:1050      360 wide — EditableText + Use my location + result rows
    driven by ConcertLocationController   ConcertLocationController.cs:22 (debounce 220 ms, geolocation, save)
```

### 1.2 Artist schedule — 0.2.9 composition

```
ArtistSchedulePage(artistUri, artistName)     ArtistSchedulePage.cs:20     Key "artist-schedule:{route}"
└─ BoxEl ZStack Grow=1                        ArtistSchedulePage.cs:119-127
   ├─ ScrollEl ScrollKey "artist-schedule:{uri}"  ArtistSchedulePage.cs:112-117
   │  │  OnScrollGeometryChanged → pageScroll (throttle key = OffsetY/8)
   │  │  OnRealized → _tracker.Viewport
   │  └─ BoxEl col                            ArtistSchedulePage.cs:95-111
   │     ├─ spacer H = 84+40 = 124            ArtistSchedulePage.cs:100
   │     └─ BoxEl Pad=(32,0,32,72+40=112) .ClipBelow(84)   ArtistSchedulePage.cs:101-109
   │        └─ Skel.Region(schedule, Soft, group "artist-schedule:{uri}")   ArtistSchedulePage.cs:76-90
   │           ├─ empty → EmptyState "No upcoming concerts" + subtitle      :87-89
   │           ├─ failed → ErrorState(retry = reload++)                     :86
   │           └─ content → Responsive.Of(width → BuildBody(wide))          :78-84
   │              └─ BoxEl col Gap=20                                       :179
   │                 ├─ TourHero                                            :182-253
   │                 │   └─ ConcertUi.SplitEditorialHero(HeaderImage, spotlight.AccentColor, copy, wide)  ConcertUi.cs:256
   │                 │      ├─ copy column (Grow .56 wide / stacked narrow, Pad 28/20, Gap 12)
   │                 │      │   ├─ Eyebrow "On tour" (accent)               :187-190
   │                 │      │   ├─ PageHero artistName (wrap ≤2)            :191-192
   │                 │      │   ├─ Body StatsLine "18 shows · 12 cities · Mar – Aug 2026"  :193-194
   │                 │      │   ├─ divider 1px (only with a spotlight)      :201
   │                 │      │   ├─ next-show row: DateBlock 56×60 + Eyebrow caption + TrackTitle + TrackMeta  :202-224
   │                 │      │   └─ actions: Button.Accent "View event" + ConcertUi.LocationButton(220 wide)   :227-249
   │                 │      └─ media pane (Grow .44 / H 180 narrow) + seam gradient  ConcertUi.cs:286-326
   │                 └─ TourDates card  (or the bounded "No other dates near you" card)  :255-300 / :163-176
   │                    ├─ head Pad=(16,12,16,8) Gap=8                      :284-296
   │                    │   ├─ TextEl "Tour dates" 28/36/600                :290-293
   │                    │   └─ ScrollEl horizontal H=48 AutoEdgeFade → SelectorBar month tabs  :259-268
   │                    └─ MonthBoard  Key "dashboard-month:{group}:{runs}:{shows}:{firstUri}:{wide}"  :271-274
   │                       ├─ Header MinH 58 (month 20/28/600 + "N shows")  MonthBoard.cs:85-107  OnRealized → _tracker.PanelHeader
   │                       ├─ Divider 1px                                   MonthBoard.cs:281
   │                       ├─ body: 1 column, or 2 balanced columns + 1px vertical divider (wide && visible>1)  :54-73
   │                       │   ├─ NightTile MinH 68  (rail, DayColumn 44, primary/secondary, ChevronRight)  :139-176
   │                       │   └─ RunTile  MinH 68  (rail, RunDayColumn 60, "N nights" chip, ChevronDown)   :178-223
   │                       └─ Divider + ShowAllFooter MinH 44 (accent label + ChevronDown)  :225-236
   └─ ShyMonthPill(_tracker, pageScroll)      ShyMonthPill.cs:36   Key "shy-month:{uri}"
      ├─ overlay root HitTestVisible=false + HitTestPassThrough, Pad top 12  :101-111
      ├─ Flow.Show(active) → Surface: H 32 capsule, acrylic, Eyebrow accent  :99,114-130
      └─ ShyPillLingerClock  Key "shy-linger:{arm}"  (1 s duration track on the ANIM clock)  :153-186
```

### 1.3 Concert detail — 0.2.9 composition

```
ConcertDetailPage(concertUri, title)        ConcertDetailPage.cs:22     Key "concert-detail:{route}"
└─ ScrollEl ScrollKey "concert-detail:{uri}"  :89
   └─ BoxEl col  :72-88
      ├─ spacer H = 124                        :77
      └─ BoxEl Pad=(32,0,32,112) .ClipBelow(84)  :78-86
         └─ Skel.Region(details, Soft, group "concert-detail:{uri}")  :53-67
            ├─ empty  → EmptyState "Concert not available" + subtitle       :65-66
            ├─ failed → ErrorState(retry)                                   :63
            └─ content → Responsive.Of(width → BuildBody(wide))             :55-61
               ├─ WIDE (≥920, leaves <860): row Gap=20 AlignItems=Start     :114-122
               │   ├─ mainColumn Grow=1 Basis=0
               │   └─ ticket rail Width=320 Shrink=0
               └─ NARROW: one column, tickets inline after the hero         :106,111
                  mainColumn (Gap=20)                                       :110
                  ├─ ConcertUi.SplitEditorialHero(artwork, accent, Identity(d), wide)   :105
                  │   └─ Identity: Eyebrow "Concert" / PageHero title(≤2) / StatusPill? / fact rows  :136-176
                  │       └─ FactRow(glyph 16 + Body wrap ≤2): Calendar date·time, Clock doors, MapPin venue·city,region,country, Info ages  :178-190
                  ├─ TicketSection (narrow position)                        :201-206
                  │   ├─ SectionCaption "Tickets" (accent eyebrow)
                  │   └─ OfferCard × n  Key "offer:{i}:{provider}"          :208-254
                  │       provider / availability / price / sale window / Button.Accent "Buy tickets" | quiet text
                  ├─ Lineup card (omitted with no artists)                  :257-292
                  │   ├─ head row: caption "Lineup" + "N artists" eyebrow (secondary)
                  │   ├─ Divider
                  │   └─ LineupRow MinH 60: 44 circle avatar + TrackTitle + ChevronRight(only if spotify:artist)  :294-329
                  └─ RelatedShelf (omitted with no related)                 :333-350
                      └─ PagedShelf measured, cell 0 = ConcertUi.BrowseAllCard → hub, then EventTile × ≤15
```

### 1.4 The same trees in 0.3 terms

Component props freeze at mount (`..\fluent-gpu\docs\design\subsystems\component-props-contract.md`). Column below says how
live data reaches each node: **Sig** = `Signal`/`IReadSignal` prop read in `Render`; **Ctx** = `UseContext`; **Key** = keyed
remount; **Frozen** = genuinely immutable for the node's life.

| 0.3 node | File / shape | Inputs | Live-data path |
|---|---|---|---|
| `Concert.Routes` (Hub/ArtistSchedule/Detail parse + build) | `Concert.cs` CORE | `ReadOnlySpan<char>` | Frozen (pure) |
| `Concert.Hub` | `Concert.Page.cs`, `sealed partial class HubPage : Component` | none (reads `Entities.Current`) | table `Changed` Sig + own filter signals |
| `Concert.Hub.FilterBar` | `Concert.Page.cs`, `sealed class FilterBar : Component` | `Signal<PlaceHandle?> Place`, `Signal<int> RadiusKm`, `Signal<ConcertWhen> When`, `Signal<IReadOnlyList<StringId>> Concepts`, `Signal<IReadOnlyList<Concept>> AllConcepts`, `Signal<int?> MatchCount`, `Ref<NodeHandle> WhereAnchor`, 3 `Action` callbacks | **Sig** for all data — identical to `ConcertFilterBar.cs:28-40`. Never pass values. |
| `Concert.Hub.CountTicker` | `Concert.Page.cs` | `IReadSignal<int?> Target`, `string Suffix` | **Sig**; the clock child remounts by `Key = "count-tick:{target}"` |
| `Concert.Hub.AppendTail` | `Concert.Page.cs` | `string PaginationKey` (Frozen), `Signal<bool> Loading`, `IReadSignal<bool> NearTail`, `Action Start` | **Key** remount per pagination key + **Sig** for gates |
| `Concert.Tile` (was `EventTile`) | `Concert.UI.cs` static | `Concert` handle + `Action onClick` | Handle read at build; the row rebuilds when the concert table publishes |
| `Concert.DateBlock` / `BigDateBlock` | `Concert.UI.cs` static | `DateTimeOffset`, `bool compact` | Frozen per build |
| `Concert.SplitHero` | `Concert.UI.cs` static | `image, accent, Element copy, bool wide` | Frozen per build; `wide` comes from `Responsive.Of` |
| `Concert.Schedule` page | `Concert.Page.cs`, `SchedulePage : Component` | `Artist` handle (Frozen at mount, Key = route) | Artist/Concert table `Changed` Sig |
| `Concert.MonthBoard` | `Concert.Page.cs`, `MonthBoard : Component` | group (Frozen), nearby set (Frozen), `Signal<int> revision`, `HashSet` expansion state (shared, page-owned) | **Key** `"dashboard-month:{sig}"` for a new group + **Sig** `revision` for expand/collapse |
| `Concert.ShyMonthPill` | `Concert.Page.cs` | `ShyMonthTracker` (mutable shared object, Frozen reference), `Signal<float> scroll` | **Sig** scroll; tracker fields are read imperatively via `SceneStore.AbsoluteRect` |
| `Concert.Detail` page | `Concert.Page.cs`, `DetailPage : Component` | `Concert` handle (Frozen, Key = route) | Concert table `Changed` Sig |
| `Concert.WhenFlyout` / `WhereFlyout` / `PlacePicker` | `Concert.UI.cs` (presentational) | `Signal`s + `Action`s only | **Sig**; each mounts fresh per open |

**Traps carried over verbatim:** the filter bar takes `Signal`s, not values (`ConcertFilterBar.cs:23`); the where pill
publishes its node through `OnRealized` into a `Ref<NodeHandle>` so the flyout anchors at it (`ConcertFilterBar.cs:82-83`);
`MonthBoard`'s expansion sets live on the PAGE (`ArtistSchedulePage.cs:29-30`) and survive its `Key` remount.

---

## 2. Wireframes

Scale is stated per wireframe. "content W" = the width `Responsive.Of` measures inside the content host (window minus
sidebar, rail and the shell's own padding — the mapping itself is chapter 18/25's).

**Arithmetic rule (do not skip):** every shelf/grid figure below is derived from the **inner** width, i.e. content W
minus the page's own gutters (hub `Spacing.PageWide` 36 + 36 = 72; schedule/detail 32 + 32 = 64). Feeding the raw
content W into `PagedShelf.Fit` / the `LazyGrid` column math over-counts by a whole gutter pair and yields cards ~12
DIP too wide.

### W1 — Hub, fully loaded @ content 1100 (1 char ≈ 14 DIP)

```
╔══════════════════════════════════════════════════════════════════════════════╗ ← shell masthead overlay (paints nothing)
║ (84 DIP reserve spacer, then 24 DIP)                                          ║
║ ┌──────────────────────────────────────────────────────────────────────────┐ ║ ← gutters 36 | 36
║ │ LIVE MUSIC                                          Eyebrow 12/16/600 acc │ ║   card: r8, FillCardDefault, 1px
║ │ Concerts                                            PageHero 28/36/600    │ ║   StrokeCardDefault, pad (20,16,20,16)
║ │ Live shows, festival dates, and artists playing near you.   Body 14/20 sec│ ║   gap 4
║ └──────────────────────────────────────────────────────────────────────────┘ ║
║                                       gap 16                                  ║
║ ┌──────────────────────────────────────────────────────────────────────────┐ ║   bar: r8, FillLayerDefault, 1px
║ │ FILTER BY                                              9,191  events      │ ║   StrokeSurfaceDefault, pad (12,8,12,8)
║ │ ┌────────────────────┐ │ ┌────────────┐ ┌──────────────┐ │ ┌───┐┌──────┐ │ ║   card column gap 4; caption ROW
║ │ │📍 New York City  ▾ │ │ │📅 Dates   ▾│ │ This weekend │ │ │All││ Rock │ │ ║   gap 8 (eyebrow · Grow spacer ·
║ │ └────────────────────┘ │ └────────────┘ └──────────────┘ │ └───┘└──────┘ │ ║   ticker); scroller H 44, outer row
║ └──────────────────────────────────────────────────────────────────────────┘ ║   gap 12, group inner gaps 8,
║                                       gap 16                                  ║   dividers 1×20
║ NEAR YOU                                             ← accent eyebrow, gap 8  ║   PagedShelf measured, maxItems 16
║ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐   ◀ ▶      ║   inner W = 1100 − 72 = 1028
║ │ [cover]│ │        │ │        │ │        │ │        │ │        │            ║   cardW = Fit(1028,150,200,12):
║ │ square │ │        │ │        │ │        │ │        │ │        │            ║   cols ⌊1040/162⌋ = 6 → 161.3
║ │ THU AUG│ │        │ │        │ │        │ │        │ │        │            ║   tile H = cardW + 70 = 231
║ │ Title  │ │        │ │        │ │        │ │        │ │        │            ║
║ │ Venue ·│ │        │ │        │ │        │ │        │ │        │            ║
║ └────────┘ └────────┘ └────────┘ └────────┘ └────────┘ └────────┘            ║
║                                       gap 20                                  ║
║ RECOMMENDED FOR YOU                                                           ║
║ ┌────────┐ ┌────────┐ …                                                       ║
║                                       gap 20                                  ║
║ PLAYLISTS FOR THE SCENE                                                       ║
║ ┌────────┐ ┌────────┐ …   (playlist promo: cover + 2-line title + source)     ║
║                                       gap 20                                  ║
║ ALL EVENTS                                                                    ║
║ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐      ║   LazyGrid minCol 240 gap 12
║ │               │ │               │ │               │ │               │      ║   cols = ⌊(1028+12)/252⌋ = 4
║ │               │ │               │ │               │ │               │      ║   cellW = (1028−36)/4 = 248
║ │ FRI SEP 5 · 20│ │               │ │               │ │               │      ║   rowH  = 248 + 86 = 334
║ │ Event title   │ │               │ │               │ │               │      ║   (tile 248+70 = 318 + a 16 gutter)
║ │ Venue · City  │ │               │ │               │ │               │      ║
║ └───────────────┘ └───────────────┘ └───────────────┘ └───────────────┘      ║
║ … virtualized: only the scroll window ±3 rows is realized                     ║
║ (bottom padding 72 player reserve + 36)                                       ║
╚══════════════════════════════════════════════════════════════════════════════╝
```

### W2 — Hub, pending/skeleton @ content 1100 (same scale)

```
 header card                (real card chrome; text repainted as shimmer bars)
 filter bar                 (real: pills render with live labels — the bar is OUTSIDE the Skel.Region)
 ▒▒▒▒▒▒▒▒                  ← "NEAR YOU" caption bar
 ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐        4 seed concerts (FeedSeed, ConcertHubPage.cs:522-526)
 │▒▒▒▒▒▒▒▒│ │▒▒▒▒▒▒▒▒│ │…       │ │…       │        shimmer derived from the SkeletonProxy of PagedShelf
 │▒▒▒▒    │ │        │ │        │ │        │        (bars: BarColor token, 1 s breathe, r4)
 └────────┘ └────────┘ └────────┘ └────────┘
 ▒▒▒▒▒▒▒▒                  ← "ALL EVENTS"
 ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐               6 seed concerts in a PLAIN AutoGrid(240,12,NaN)
 │▒▒▒▒▒▒│ │      │ │      │ │      │               (never LazyGrid: a stateful scroll-windowed component
 └──────┘ └──────┘ └──────┘ └──────┘                must not mount inside a shimmer derivation)
 Reveal on ready: SkelReveal.Soft — whole region opacity+TranslateY+Blur → rest; shimmer cross-dissolves (ExitMs 250).
```

### W3 — Hub, empty with NO location @ content 1100

```
 header card
 filter bar   [📍 Set location ▾] │ [📅 Dates ▾][This weekend] │ [All]      ← only "All" (no geohash ⇒ no concepts)
                                                    0  events               ← CountTicker at 0
 ┌──────────────────────────────────────────────────────────────────────┐
 │                                                                      │   EmptyState.Build (pad 24, gap 4, centred)
 │                     No concerts found                                │   PageHero 28/36/600, wrapped
 │        Set your location to discover live music near you.            │   TrackMeta (Caption 12/16 secondary)
 │                                                                      │   16 DIP spacer
 │                        [ Set location ]                              │   Button.Standard — NEVER Accent
 └──────────────────────────────────────────────────────────────────────┘
 Tapping it opens the location picker anchored at the WHERE PILL node (_anchor).  ConcertHubPage.cs:107
 With a place set, the subtitle instead reads: "Nothing is on sale for this location right now. Try another place
 or category." and there is NO action button.   ConcertHubPage.cs:103-107
```

### W4 — Hub, failed @ content 1100

```
 header card + filter bar unchanged (both live outside the Skel.Region)
 ┌──────────────────────────────────────────────────────────────────────┐
 │                     Something went wrong                             │   ErrorState.Build → EmptyState grammar
 │        (Strings.Common.ErrorSubtitle)                                │   no glyph, no critical colour
 │                          [ Retry ]                                   │   Retry = RequeryFeed(svc): new generation,
 └──────────────────────────────────────────────────────────────────────┘   feed re-pends, count re-previews
```

### W5 — Hub, scrolled: sticky bar + the two clips @ content 1100

```
  ▓▓▓▓▓▓▓▓▓▓▓▓▓ shell masthead band (unpainted overlay; crumb "Browse › Concerts") ▓▓▓▓▓▓▓▓▓▓▓
  ── y = 84 ─────────────────────────────────────────────────────────────────────────────────
  │ FILTER BY                                        6,657  events   │   ← the bar PINNED at 84
  │ [📍 New York City · 50 km ▾] │ [⟨✓ This weekend⟩ Jul 17 – 19 ▾] │ [All][Rock][+4 genres]│
  └───────────────────────────────────────────────────────────────────┘
   ░░░ 24-DIP top feather on the FEED body (EdgeFade mounted only while the clip is engaged) ░░░
  ┌───────────────┐ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐
  │ event tile    │ │               │ │               │ │               │
  The HEADER card scrolled above is clipped at the same y=84 line with its OWN 24-DIP feather.
  Two ClipBelow nodes, one on each side of the pinned bar — never one clip around all three.
  ConcertHubPage.cs:114-144, BrowseMastheadMetrics.cs:27-31 (ClipInset 84, ClipFadeBand 24)
```

### W6 — Filter bar, states of the when-area and genre strip (1 char ≈ 8 DIP)

```
REST (When.Kind == Any):
  ┌─────────────────┐   ┌──────────────┐
  │ 📅  Dates    ▾  │   │ This weekend │        RestDatePill H32 pad(12,5,12,5) gap 6 r-full,
  └─────────────────┘   └──────────────┘        FillControlDefault + 1px StrokeControlDefault
   Key "when-pill"       Key "when-chip"        FilterToken H32 pad(14,5,14,5) gap 6 r-full
                         ↑ 8 DIP gap (Spacing.S)

ACTIVE (a date chosen) — the SAME "when-pill" node, width reflowed over 260 ms:
  ┌──────────────────────────────────────────┐
  │ ⟨ ✓ This weekend ⟩  Jul 17 – 19       ▾  │   outer: H32 pad(3,3,12,3) gap 8 r-full, AccentDefault
  └──────────────────────────────────────────┘   segment: H26 pad(11,0,11,0) gap 5 r13,
    segment: FillControlSolid + Elevation.Flyout  ink AccentTextPrimary, check 12
    value ink TextOnAccentPrimary 14/600, chevron 10 TextOnAccentPrimary

GENRES, rest (top 3) :  [ All ][ Rock ][ Hip Hop ][ Electronic ][ +4 genres ]      ← strip gap 8 (Spacing.S), NOT the
GENRES, expanded     :  [ All ][ Rock ][ Hip Hop ][ Electronic ][ Jazz ][ Metal ][ Pop ][ Indie ][ Show less ]  outer 12
  selected token      :  filled AccentDefault, ink TextOnAccentPrimary — SAME WIDTH as unselected
  "+N genres"         :  dashed 1px StrokeControlDefault (on 5 / off 4), accent LABEL, Chevron*Down* 10 secondary
  "Show less"         :  same shell, Chevron*Up* 10 secondary (`expanded: true` flips the glyph, ConcertUi.cs:694)
  "All" token         :  selected exactly when the selection set is EMPTY; tapping it while empty is a no-op (no requery)
  offered only when   :  "+N" while `all.Count > shown.Count`; "Show less" while `expanded && all.Count > 3`
  every token's Key   :  "filter-token:{label}" — the label IS the identity, so a re-ordered concept list reuses nodes
  ConcertUi.cs:548-566 (token), 679-696 (more), ConcertFilterBar.cs:150-172
```

Genre-strip states the wireframe above does not draw, each of which really happens:
`all.Count == 0` (no geohash ⇒ no concepts call, `ConcertHubPage.cs:454-459`) → the strip is the lone `[All]` token, the
two dividers still render, and the scroller has nothing to scroll. `all.Count ≤ 3` → no "+N" token at all. A concept
selected before a location change that the new place no longer offers is dropped by `PublishConcepts` and the feed
requeries — the token disappears from under the cursor (`ConcertHubPage.cs:476-492`).

### W7 — Date flyout, ROOT view (anchored bottom-left of the when-pill; 1 char ≈ 8 DIP)

```
┌────────────────────────────────────────┐  width 320, pad 8, ClipToBounds
│ Anytime                                │  RowButton MinH 40, pad (12,4,12,4), r4, Interaction.Subtle
│ Today                                  │  Body 14/20 primary, Grow/Basis0/MinWidth0 + ellipsis
│ This weekend                           │  rows gap 2
│ Next weekend                           │
│ ──────────────────────────────────────  │  Divider: 1px StrokeSurfaceDefault, 4 DIP pad above/below
│ September 2026                      ›  │  ChevronRight 14 secondary — drills to the month leaf
│ October 2026                        ›  │  months = current + 0..3, from Now (no tables)
│ November 2026                       ›  │
│ December 2026                       ›  │
└────────────────────────────────────────┘
Popup: FocusTrap, LightDismiss, PopupChrome.Popup, ConstrainToRootBounds.  ConcertFilterBar.cs:192-205
```

### W8 — Date flyout, MONTH LEAF (drilled in; page-slides forward)

```
┌────────────────────────────────────────┐  same 320 box; body ScrollEl ContentSized MaxHeight 400, gap 4
│ ‹  September 2026                      │  back 28×28 r4 (ChevronLeft 14) + BodyStrong; row MinH 36, gap 8
│ All of September                       │  RowButton
│ Weekend · Sep 4 – 6                    │  every Fri–Sun in the month ("Weekend · " + WhenLabel)
│ Weekend · Sep 11 – 13                  │
│ Weekend · Sep 18 – 20                  │
│ Weekend · Sep 25 – 27                  │
│  Su  Mo  Tu  We  Th  Fr  Sa            │  header cells 38×20, Caption 12/16 secondary, 2-letter abbrev
│      1   2   3   4   5   6             │  day cells 38×32, r4, gap 4 both axes
│  7   8   9  10  11  12  13             │  past days: TextDisabled, HitTestVisible=false
│ 14  15  16  17  18  19  20             │  tap 1 = start (AccentDefault plate, TextOnAccentPrimary)
│ 21 [22] ▒23▒ ▒24▒ [25] 26  27          │  in-range: AccentSubtle; tap 2 = end
│ 28  29  30                             │
│ [ Clear ]                [Show events] │  footer MinH 44 pad (4,4,4,0): Standard + spacer + Accent
└────────────────────────────────────────┘  "Show events" disabled until a start day exists
ConcertDateFlyout.cs:84-220
```

### W9 — Where flyout (anchored at the where pill)

```
┌──────────────────────────────┐   width 264, pad 8, gap 2
│ 📍 Search cities          ›  │   ActionRow MinH 40, glyph 16 secondary, Body primary, ChevronRight 14
│ 📍 Use my location           │   closes the flyout and hands off to the location controller
│ ─────────────────────────── │   Divider
│ Within 25 km                 │   RadioRow MinH 40, ToggleButton role
│ Within 50 km              ✓  │   active: Body weight 600 + AccentTextPrimary, Check 14 accent
│ Within 100 km                │   inactive rows reserve a 14×14 box so labels never shift
└──────────────────────────────┘   radius selection KEEPS the flyout open and re-labels the pill
ConcertDateFlyout.cs:274-347, ConcertFilterBar.cs:236-241
```

### W10 — Location picker panel

```
┌────────────────────────────────────────────┐   width 360, pad 12, gap 8
│ ┌────────────────────────────────────────┐ │   EditableText 340×32, placeholder "Search cities"
│ │ new yor                                │ │   220 ms debounce → SearchLocationsAsync
│ └────────────────────────────────────────┘ │
│ [ Use my location ]                        │   Button.Standard; label becomes "Locating…" + disabled while loading
│ Location access is off. Turn it on in …    │   error: Body SystemFillCritical, wrap ≤3 lines (only when set)
│ ┌────────────────────────────────────────┐ │   results ScrollEl ContentSized MaxHeight 248, rows gap 2
│ │ 📍 New York City                       │ │   row MinH 48, pad (8,4,8,4), r4, Interaction.Subtle
│ │    New York - United States            │ │   TrackTitle + TrackMeta; MapPin 18 secondary
│ │ 📍 York                                │ │
│ └────────────────────────────────────────┘ │
└────────────────────────────────────────────┘   no results → a 48-high row: "Searching…" | "No locations found"
ConcertUi.cs:1050-1128; controller ConcertLocationController.cs:22-207
```

"Use my location" is a TWO-STEP affordance, not a one-tap save (`ConcertLocationController.cs:130-172`): the OS consent
prompt runs, the coordinates are reverse-geocoded, and the matches land in THIS SAME results list — the user still taps a
row to save. While it runs the button's label becomes "Locating…" and it is disabled (`ConcertUi.cs:1092-1093`); the same
`Loading` signal also drives the empty-body row's "Searching…" copy, so the panel never shows a spinner of its own.

The inline error line (`Body`, `SystemFillCritical`, wrap ≤ 3) has SEVEN distinct sources, not the four §6 used to list —
each is a different sentence and all must be ported:
| when | string (en-US.json) |
|---|---|
| city search threw | `concerts.location.searchFailed` :465 |
| OS consent refused | `concerts.location.permissionDenied` :466 |
| provider has no fix | `concerts.location.unavailable` :467 |
| fix timed out | `concerts.location.timedOut` :468 |
| any other geolocation failure | `concerts.location.failed` :469 |
| reverse lookup returned 0 places | `concerts.location.noMatches` :470 |
| reverse lookup threw | `concerts.location.lookupFailed` :471 |
| a picked place with a blank id | `concerts.location.invalid` :472 |
| `SaveLocationAsync` returned false / threw | `concerts.location.saveFailed` :473 |

Opening the picker always resets Query / Results / Loading / Error, so a prior session's error never greets the next open
(`ConcertLocationController.cs:76-79`).

### W11 — Hub append tail (three states, bottom of the feed)

```
far from tail      :  (nothing but a 16-DIP spacer — no shimmer subtree, no standing pulse)
near tail / loading:  ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐   4 shimmering EventTiles in AutoGrid(240,12)
                      │▒▒▒▒▒▒│ │▒▒▒▒▒▒│ │▒▒▒▒▒▒│ │▒▒▒▒▒▒│   same card family + metrics as All events
                      └──────┘ └──────┘ └──────┘ └──────┘
after 3 failures   :  (empty BoxEl — the tail collapses rather than spinning forever)
ConcertAppendPreloader.cs:54-61,88-99
```

### W12 — Hub @ content 600 (narrow; 1 char ≈ 10 DIP)

```
┌──────────────────────────────────────────────────────────┐
│ LIVE MUSIC                                               │  header card unchanged (no breakpoint here)
│ Concerts                                                 │  PageHero wraps? No — MaxLines 1 + ellipsis
│ Live shows, festival dates, and artists playing near yo…  │  subtitle 1 line + ellipsis
└──────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────┐
│ FILTER BY                             1,204  events      │
│ [📍 New York City ▾] │ [📅 Dates ▾] [This week…        →  │  the row does NOT wrap: it scrolls
└──────────────────────────────────────────────────────────┘  horizontally with a 36-DIP auto edge fade
 NEAR YOU
 ┌──────────┐ ┌──────────┐ ┌──────────┐     inner W = 600 − 72 = 528
                                            shelf: Fit(528,150,200,12) → cols ⌊540/162⌋ = 3 × 168
 ALL EVENTS
 ┌────────────────────────┐ ┌────────────────────────┐     grid: cols ⌊540/252⌋ = 2, cellW 258, rowH 344
 The hub has NO width breakpoint of its own: everything reflows through PagedShelf.Fit / LazyGrid column math.
 Below ~372 inner DIP the grid drops to ONE column and the shelf to one card — still no branch, just the same math.
```

### W13 — Artist schedule, WIDE (content ≥ 760, leaves < 720) @ content 1100 (1 char ≈ 14 DIP)

```
║ (84 + 40 spacer)                                                              ║
║ ┌──────────────────────────────────────────────────────────────────────────┐ ║  gutters 32 | 32
║ │ copy pane (Grow .56, pad 28, gap 12)         │ media pane (Grow .44)      │ ║  hero H = 320 (fixed)
║ │ ON TOUR                              accent  │ ▓▓▓▓▓ cover-cropped ▓▓▓▓▓▓ │ ║  r8, FillCardDefault, 1px stroke
║ │ Arctic Monkeys                    PageHero   │ ▓ FocusY 0.30, decode 1024 │ ║  seam: horizontal gradient,
║ │ 18 shows · 12 cities · Mar – Aug 2026   sec  │ ▓ fade 0 → 22% from left   │ ║  FillCardDefault → transparent
║ │ ──────────────────────────────────────────   │ ▓                          │ ║  divider 1px StrokeDividerDefault
║ │ ┌────┐  NEXT SHOW · IN 5 DAYS       accent   │ ▓                          │ ║  DateBlock 56×60 (r4,
║ │ │MAR │  O2 Academy Brixton      TrackTitle   │ ▓                          │ ║  FillCardSecondary + 1px)
║ │ │ 14 │  London · 19:30           TrackMeta   │ ▓                          │ ║
║ │ └────┘                                       │ ▓                          │ ║
║ │ [ View event ]  [ 📍 London            ▾ ]   │ ▓                          │ ║  Accent CTA + 220-wide
║ └──────────────────────────────────────────────┴────────────────────────────┘ ║  LocationButton (H32, r4)
║                                     gap 20                                    ║
║ ┌──────────────────────────────────────────────────────────────────────────┐ ║  TourDates card r8 + 1px
║ │ Tour dates                                        TextEl 28/36/600        │ ║  head pad (16,12,16,8) gap 8
║ │ [Mar ’26] [Apr] [May] [Jun] [Jul] [Aug]                        →          │ ║  SelectorBar in a 48-high
║ ├──────────────────────────────────────────────────────────────────────────┤ ║  horizontal AutoEdgeFade scroller
║ │ March 2026                                         6 shows                │ ║  header MinH 58, 20/28/600 +
║ ├──────────────────────────────────────────────────────────────────────────┤ ║  Body secondary; divider 1px
║ │▌ ┌──┐ O2 Academy Brixton            │ ┌──┐ Rock City                  ›  │ ║  two BALANCED columns, a 1px
║ │▌ │14│ 📍 London · 19:30             │ │22│ Nottingham · 19:00           │ ║  vertical divider between,
║ │  │Sa│                               │ │Su│                              │ ║  AlignItems = Start
║ ├──────────────────────────────────── ┼ ─────────────────────────────────┤ ║  tiles MinH 68, pad (12,8,12,8)
║ │  ┌────┐ Alexandra Palace            │ ┌──┐ …                            │ ║  near rail 3×32 r2 AccentDefault
║ │  │17–19│ 3 nights  London        ▾  │ │27│                              │ ║  run tile: RunDayColumn 60,
║ │  │Tu–Th│                            │ │Fr│                              │ ║  "3 nights" chip AccentSubtle
║ ├──────────────────────────────────────────────────────────────────────────┤ ║
║ │                    Show all 11              ▾                             │ ║  footer MinH 44, accent label
║ └──────────────────────────────────────────────────────────────────────────┘ ║  + ChevronDown 14 accent
```

**W13b — the hero with NO spotlight** (every date in the past ⇒ `Spotlight()` returns null, `ConcertScheduleModel.cs:90-96`).
Three things vanish together and the hero must still read as composed:

```
║ ┌──────────────────────────────────────────────┬────────────────────────────┐ ║
║ │ ON TOUR                                      │ ▓ media pane unchanged  ▓  │ ║  NO 1px divider (:201)
║ │ Arctic Monkeys                               │ ▓                       ▓  │ ║  NO next-show row (:202-224)
║ │ 12 shows · 8 cities · Mar 2024 – Aug 2024    │ ▓                       ▓  │ ║  NO accent "View event"
║ │ [ 📍 London                              ▾ ] │ ▓                       ▓  │ ║  the LocationButton alone, and
║ └──────────────────────────────────────────────┴────────────────────────────┘ ║  NOT boxed at 220 — it keeps its
                                                                                   own Grow 1/Basis 0 and stretches
ArtistSchedulePage.cs:197,229-249.  The media pane also loses its accent tint (accent = spotlight?.AccentColor, :252),
so a spotlight-less hero falls back to the flat theme base — a real, designed state, not a bug.
```

**W13c — the hero with NO header image** (the seeded/pending hero, and any artist whose provider ships no banner).
`SplitHeroMedia` swaps the `ImageEl` for a flat `fill` pane carrying ONE centred **52-DIP** `Icons.Calendar` at
`TextSecondary` α 0.72 (`ConcertUi.cs:301-305`) — note 52, not the event tile's 38. The seam gradient still paints over
it, so the fallback dissolves into the copy card exactly as a photo would.

### W14 — Artist schedule, NARROW (content < 720) @ content 640 (1 char ≈ 10 DIP)

```
┌──────────────────────────────────────────────────────────┐
│ ▓▓▓▓▓▓▓▓ media pane, Height 180, cover-cropped ▓▓▓▓▓▓▓▓▓ │  hero Direction = column,
│ ▓▓ bottom gradient: transparent @68% → FillCardDefault ▓ │  media FIRST, Height auto
├──────────────────────────────────────────────────────────┤
│ ON TOUR                                                  │  copy pad 20 (not 28), gap 12
│ Arctic Monkeys                                           │
│ 18 shows · 12 cities · Mar – Aug 2026                    │
│ ────────────────────────────────────────────────────────  │
│ ┌────┐ NEXT SHOW · IN 5 DAYS                             │
│ │MAR │ O2 Academy Brixton                                │
│ │ 14 │ London · 19:30                                    │
│ └────┘                                                   │
│ [ View event ]                                           │  actions STACK (column, gap 8)
│ [ 📍 London                                         ▾ ]  │  location button full width
└──────────────────────────────────────────────────────────┘
 Tour dates card: single column of tiles, no vertical divider (MonthBoard.cs:54,68-73)
 Hysteresis: wide enters at 760, leaves below 720 — between 720 and 760 the layout HOLDS its last state.
 ConcertLayout.cs:7-16
```

### W15 — Month board, run expanded + month expanded

```
collapsed run            expanded run (tap the run tile)        capped month (>6 tiles)
┌────────────────────┐   ┌────────────────────┐                ┌────────────────────┐
│ ┌────┐ Ally Pally  │   │ ┌──┐ Ally Pally  › │                │ …6 tiles…          │
│ │17–19│ 3 nights ▾ │ → │ │17│ London·19:30 │                ├────────────────────┤
│ │Tu–Th│            │   │ ├──┴──────────────┤ 1px divider    │  Show all 11    ▾  │ ← accent, MinH 44
│ └────┘             │   │ │18│ …          › │                └────────────────────┘
└────────────────────┘   │ ├─────────────────┤                 tapping adds the month key to
 ChevronDown trailing    │ │19│ …          › │                 _expandedMonths, revision++,
 (this tile EXPANDS)     └─┴─────────────────┘                 the footer disappears
 Expansion is permanent for the page's life (a HashSet on the page); there is no collapse affordance.
 MonthBoard.cs:120-137,225-236; ArtistSchedulePage.cs:29-30

 RUN-TILE SECONDARY ROW, in built order (MonthBoard.cs:182-195) — the chip is NOT always first:
   venue-primary run  :  [📍12] [City …………] [⟨3 nights⟩]     city INSERTED before the chip (index near?1:0)
   city-primary run   :  [📍12] [⟨3 nights⟩] [with Band of Silver …]   support acts trail the chip
   the pin is a 12-DIP Icons.MapPin at AccentTextPrimary, only when the run is in the guarded near set
   the growing/shrinking text run is Grow 1 + Shrink 1 + MinWidth 0 (never Basis 0 — see §9.7)
 NIGHT-TILE secondary row is the same shape minus the chip; both rows gap 4 (Spacing.XS), tile text column gap 2.
 A capped month prints "Show all {ShowCount}" — the SHOW count, while the CAP counts TILES (runs): a month of
 7 runs where two are 3-night residencies shows "Show all 11" over 7 tiles (MonthBoard.cs:50-51,233).
```

### W16 — Shy month pill (visible only while scrolling the boards region)

```
        ┌────────────────────────────┐   ← top-centre, 12 DIP below the content top
        │  SEPTEMBER 2026 · 6 SHOWS  │      H 32, pad (12,0,12,0), r-full,
        └────────────────────────────┘      Acrylic AcrylicFlyout + FillLayerDefault,
                                            Elevation.Card, 1px StrokeSurfaceDefault,
                                            Eyebrow 12/16/600 + 30/1000em tracking, accent ink
 Label = "{MMMM yyyy} · {N shows}", written by the page each render (ArtistSchedulePage.cs:154)
 Enter: opacity 0→1 with Dy −8, 200 ms SmoothOut.  Exit: opacity→0 with Dy −6, 260 ms SmoothOut.
 TransformOrigin (0.5, 0) so the Dy lift reads as hanging from the top edge (ShyMonthPill.cs:117).
 Lives 1 s past the last scroll step; never exists above the first board header.
 ShyMonthPill.cs:47-52,114-130,136-146

 IT DOES NOT "TRACK THE MONTH AT THE VIEWPORT TOP" — there is exactly ONE board mounted (the selected month), and the
 tracker holds exactly one `PanelHeader`. The predicate is simply: the board header's AbsoluteRect.Y ≤ the viewport's
 AbsoluteRect.Y + 4 DIP ⇒ show `_tracker.Label`, else deactivate IMMEDIATELY (no linger) — ShyMonthPill.cs:136-146.
 A scroll step smaller than 0.5 DIP is ignored entirely (no re-arm, no resolve) — ShyMonthPill.cs:74.
 The first scroll after mount only establishes the baseline offset and shows nothing (:73).
 With no month group selected the page blanks `Label` and nulls `PanelHeader`, so the pill can never appear over the
 "No other dates near you" card (ArtistSchedulePage.cs:159-160).
```

### W17 / W18 — Artist schedule, empty states

```
W17  no concerts at all (isEmpty)              W18  hero rendered but no month groups
 ┌──────────────────────────────────┐           ┌────────────────────────────────────────┐
 │      No upcoming concerts        │           │ (hero card as W13)                     │
 │  There are no dates on sale …    │           └────────────────────────────────────────┘
 └──────────────────────────────────┘           ┌────────────────────────────────────────┐
 EmptyState.Build, no action                    │        No other dates near you         │  bounded card:
 ArtistSchedulePage.cs:87-89                    │  That's every show on sale for this …  │  r8, FillCardDefault,
                                                │           [ Set location ]             │  1px stroke,
                                                └────────────────────────────────────────┘  pad (20,24,20,24)
                                                ArtistSchedulePage.cs:163-176
```

### W19 — Concert detail, WIDE (content ≥ 920, leaves < 860) @ content 1100 (1 char ≈ 14 DIP)

```
║ (84 + 40 spacer)                                                              ║  gutters 32 | 32
║ ┌────────────────────────────────────────────────┐  ┌──────────────────────┐  ║  row gap 20, AlignItems Start
║ │ copy (Grow .56, pad 28)      │ media (Grow .44)│  │ TICKETS       accent │  ║  ticket rail Width 320 Shrink 0
║ │ CONCERT                      │ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ │  │ ┌──────────────────┐ │  ║  hero H 320
║ │ Arctic Monkeys               │ ▓             ▓ │  │ │ Ticketmaster     │ │  ║  offer card r8, pad 12,
║ │ ⟨ Cancelled ⟩  ← status pill │ ▓             ▓ │  │ │ Available        │ │  ║  gap 4, FillCardDefault + 1px
║ │ 📅 Saturday, March 14, 2026  │ ▓             ▓ │  │ │ 45 - 75 EUR      │ │  ║  caption + cards gap 8
║ │    · 19:30                   │ ▓             ▓ │  │ │ On sale 1 Jan …  │ │  ║
║ │ 🕐 Doors open 18:30          │ ▓             ▓ │  │ │ [ Buy tickets ]  │ │  ║  Button.Accent
║ │ 📍 O2 Academy Brixton ·      │ ▓             ▓ │  │ └──────────────────┘ │  ║
║ │    London, England, UK       │ ▓             ▓ │  │ ┌──────────────────┐ │  ║
║ │ ℹ Ages 14+                   │ ▓             ▓ │  │ │ Resale partner   │ │  ║
║ └──────────────────────────────┴─────────────────┘  │ │ Tickets not      │ │  ║  no URL ⇒ quiet TrackMeta,
║                                                     │ │ available online │ │  ║  never a dead button
║ ┌────────────────────────────────────────────────┐  │ └──────────────────┘ │  ║
║ │ LINEUP                            3 artists    │  └──────────────────────┘  ║  head pad (16,12,16,12)
║ ├────────────────────────────────────────────────┤                            ║  divider 1px
║ │ (●) Arctic Monkeys                          ›  │                            ║  row MinH 60, avatar 44 r22,
║ │ (●) The Hives                               ›  │                            ║  rows box pad (8,4,8,8)
║ │ (●) Billing-text-only support               │  │                            ║  ← no chevron, not clickable
║ └────────────────────────────────────────────────┘                            ║
║ RELATED CONCERTS                                                              ║  accent eyebrow, headerGap 8
║ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ …                                 ║  cell 0 = BrowseAllCard
║ │ 🗓  📍  │ │ cover  │ │        │ │        │                                   ║  (calendar + pin glyphs,
║ │ LIVE M.│ │ FRI …  │ │        │ │        │                                   ║   30/44/22 DIP, 30% secondary)
║ │Browse a│ │ Title  │ │        │ │        │                                   ║
║ └────────┘ └────────┘ └────────┘ └────────┘                                   ║
```

W19 details the wireframe cannot show:
- **Which photo the hero gets**, in strict order: the first lineup artist with a `HeaderImage`, else `Summary.Image`,
  else the first lineup artist with an `Image`, else the 52-DIP fallback pane. The accent is the **other** way round —
  `Summary.AccentColor` first, then that header artist's (`ConcertDetailPage.cs:99-103`). Getting either order wrong
  changes the page's colour on real data.
- **Wide but no offers** ⇒ there is no rail and no row: `BuildBody` returns the bare `mainColumn`, full width
  (`ConcertDetailPage.cs:111`). The two-column shape is a function of OFFERS, not of width alone.
- **Lineup avatar with no image** ⇒ `Surfaces.Artwork` renders its seeded procedural fallback at 44×44 r22; the seed is a
  stable hash of the artist URI (or the name, for billing-only entries), so the same artist keeps the same fallback
  across renders and pages (`ConcertDetailPage.cs:306,358-363`).
- **Offer with no `ProviderName`** ⇒ the title line falls back to `concerts.detail.ticketProvider` ("Ticket provider");
  availability `Unknown` omits its line entirely; no price ⇒ no price line; no sale dates ⇒ no sale line. A degenerate
  offer is therefore a card of exactly two lines: "Ticket provider" + the quiet unavailable text.
- The Buy button sits in its own box with `Padding (0,4,0,0)` — the 4-DIP lift off the last text line
  (`ConcertDetailPage.cs:237`).

### W20 — Concert detail, NARROW (content < 860) @ content 700 (1 char ≈ 10 DIP)

```
┌──────────────────────────────────────────────────────────┐
│ ▓▓▓ media pane Height 180 (gradient bottom 68%→100%) ▓▓▓ │
├──────────────────────────────────────────────────────────┤
│ CONCERT                                                  │  copy pad 20
│ Arctic Monkeys                                           │
│ 📅 Saturday, March 14, 2026 · 19:30                      │
│ 📍 O2 Academy Brixton · London, England, UK              │
└──────────────────────────────────────────────────────────┘
 TICKETS                                                      ← tickets move INLINE, directly after the hero
 ┌────────────────────────────────────────────────────────┐    (ConcertDetailPage.cs:106)
 │ Ticketmaster / Available / 45 - 75 EUR / [Buy tickets] │
 └────────────────────────────────────────────────────────┘
 LINEUP card → RELATED CONCERTS shelf (identical composition, narrower cards)
```

### W21 / W22 — Concert detail, skeleton and unavailable

```
W21 pending: the seeded shape is facts + TWO offer cards + THREE lineup rows (ConcertDetailPage.cs:367-386),
    so the shimmer shows a hero block, four fact bars, two offer cards and three avatar rows — the exact
    silhouette of the loaded page. Reveal SkelReveal.Soft.
W22 unavailable (details == null):
    ┌───────────────────────────────────────────┐
    │           Concert not available           │   EmptyState.Build, no action
    │  This event may have been removed or is   │
    │        no longer listed.                  │
    └───────────────────────────────────────────┘
```

### W23 — Interactive states (hover / press / focus), 1 char ≈ 8 DIP

```
EventTile          rest: transparent   hover: FillSubtleSecondary   press: FillSubtleTertiary   (Interaction.Subtle)
                   focus: 2-DIP focus-visual margin ring around the whole card
ScheduleRow/MonthBoard tile: identical Subtle ramp; the card-like rows in ConcertUi.ScheduleRow instead use
                   FillCardDefault → FillControlSecondary → FillControlTertiary with a 1px card stroke
PlaylistPromoCard  FillCardDefault → FillControlSecondary → FillControlTertiary, focus margin 2
WherePill/RestPill FillControlDefault → FillControlSecondary → FillControlTertiary
FilterToken (sel)  AccentDefault → AccentSecondary → AccentTertiary, 167 ms brush cross-fade (BrushTransitionMs)
FilterToken (unsel)FillControlDefault → FillControlSecondary → FillControlTertiary, same 167 ms
Calendar day       unselected: FillSubtleSecondary/Tertiary · selected: AccentSecondary/Tertiary · past: inert
LineupRow          only when a canonical spotify:artist URI exists — otherwise Cursor.Arrow, Role None, no fills
ShowAllFooter      Interaction.Subtle over a transparent rest — the accent LABEL is the affordance, no plate

FOCUS is NOT uniform, and the 0.3 port must not "tidy" it into one rule. A 2-DIP `FocusVisualMargin` is carried ONLY by
the card/tile/row family — `EventTile`/`VerticalCard` (ConcertUi.cs:142), `BrowseAllCard` (:172), `PlaylistPromoCard`
(ConcertHubPage.cs:316), `ScheduleRow` (:94), `SpotlightCard` (:469), `NightTile`/`RunTile` (MonthBoard.cs:156,202).
Everything else is `Focusable = true` with the engine's DEFAULT focus visual and no margin: `WherePill`, `RestDatePill`,
`SegmentedPill`, `FilterToken`, `MoreToken`, `LocationButton`, every flyout `RowButton`/`ActionRow`/`RadioRow`, the
calendar day cells, the back button and the show-all footer. `Interaction.Subtle` is the rest→hover→press fill ramp
(`FillSubtleTransparent → FillSubtleSecondary → FillSubtleTertiary`, `Interaction.cs:132-135`) — rest is a transparent
token, not an absent brush, so a brush cross-fade has something to tween from.
```

---

## 3. Tokens

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| Hub page frame | — | spacer 84+24; pad (36,0,36,108); gap 16 | — | — | — | — | `ConcertHubPage.cs:153-159` |
| Hub header card | auto | pad (20,16,20,16); gap 4 | 8 (`Radii.Card`) | Eyebrow 12/16/600 +30/1000em · PageHero 28/36/600 · Body 14/20 | `WaveeAccent.Decor` / `Tok.TextPrimary` / `Tok.TextSecondary`; fill `Tok.FillCardDefault` | 1px `Tok.StrokeCardDefault`, no shadow | `ConcertHubPage.cs:175-194` |
| Filter bar card | auto | column gap 4; pad (12,8,12,8) | 8 | Eyebrow (caption) | `Tok.FillLayerDefault` | 1px `Tok.StrokeSurfaceDefault`; `.Sticky(84)` | `ConcertFilterBar.cs:97-107` |
| Filter bar caption row | auto | gap 8 (eyebrow · `Grow 1` spacer · ticker) | — | Eyebrow accent | — | — | `ConcertFilterBar.cs:64-80` |
| Filter group inner rows (when-area, genres) | auto | gap 8 | — | — | — | the OUTER row that separates the three groups + dividers is gap 12 | `ConcertFilterBar.cs:87,133,171` |
| Filter scroller | H 44 | row gap 12 | — | — | — | `AutoEdgeFade` (36 DIP band), no scrollbar | `ConcertFilterBar.cs:92-95` |
| Group divider | 1×20 | — | 0 | — | `Tok.StrokeSurfaceDefault` | — | `ConcertFilterBar.cs:255` |
| Where pill | H 32 | pad (12,5,12,5); gap 6 | full | BodyStrong 14/20/600 | `Tok.FillControlDefault` / hover `FillControlSecondary` / press `FillControlTertiary`; 1px `StrokeControlDefault`; glyphs 14/10 `TextSecondary` | — | `ConcertUi.cs:573-587` |
| Rest date pill | H 32 | pad (12,5,12,5); gap 6 | full | Body 14/20 | same control ramp | width-reflow `Animate` 260 ms | `ConcertUi.cs:593-612` |
| Fused date pill (outer) | H 32 | pad (3,3,12,3); gap 8 | full | TextEl 14/600 | `Tok.AccentDefault` / `AccentSecondary` / `AccentTertiary`; value + chevron `TextOnAccentPrimary` | none (flat) | `ConcertUi.cs:637-671`, `1151-1158` |
| Fused pill segment | H 26 | pad (11,0,11,0); gap 5 | 13 | TextEl 14/600 | `Tok.FillControlSolid` (opaque: #FFFFFF light / #454545 dark); ink `Tok.AccentTextPrimary`; check 12 | `Elevation.Flyout` (blur 16, y 8, #00000042 dark / #00000024 light) | `ConcertUi.cs:652-667`, `1151-1158` |
| Filter token | H 32 | pad (14,5,14,5); gap 6 | full | Body 14/20 | sel `AccentDefault`+`TextOnAccentPrimary`; unsel `FillControlDefault`+`TextPrimary`, 1px `StrokeControlDefault` | brush cross-fade 167 ms | `ConcertUi.cs:548-566` |
| More/less token | H 32 | pad (14,5,14,5); gap 6 | full | Body 14/20 | label `Tok.AccentTextPrimary`; border 1px `StrokeControlDefault` dashed 5/4; chevron 10 `TextSecondary` | `Interaction.Subtle` fills | `ConcertUi.cs:679-696` |
| Count ticker | box W 64 (fixed), right-aligned | gap 2 | — | TrackTitle (BodyStrong 14/20/600) + Body suffix | number `TextPrimary`, suffix `TextSecondary` | — | `CountTicker.cs:35-56` |
| Event tile | W = shelf/grid cell; H = W + 70 | pad (8,8,8,12); gap 8; text gap 3 | 8 (card + inner media) | Eyebrow (date) · TrackTitle · TrackMeta (Caption 12/16 secondary) | date `WaveeAccent.Decor`; art fallback pane `Lerp(base, accent, 0.32)` | `Interaction.Subtle`; `ClipToBounds` | `ConcertUi.cs:113-154,223-251` |
| Event tile fallback glyph | 38 | centred | — | — | `Tok.TextSecondary` @ A 0.72 | — | `ConcertUi.cs:247` |
| Browse-all card | same as tile | same; glyph layers inset 12 | 8 | Eyebrow + TrackTitle | glyphs at `TextSecondary` A 0.30, layered in a ZStack: Calendar **30** top-LEFT (pad 12), MapPin **44** centred, Calendar **22** bottom-RIGHT (pad 12); base `#1C1C1E` dark / `FillCardSecondary` light | — | `ConcertUi.cs:162-219` |
| Playlist promo card | W = shelf cell; art = W−16 | pad (8,8,8,12); gap 8 | 8 card / 4 art | TrackTitle (wrap ≤2) + TrackMeta | `FillCardDefault`/`FillControlSecondary`/`FillControlTertiary`, 1px `StrokeCardDefault` | drag source (`WaveeDragKinds.Resource`) | `ConcertHubPage.cs:305-334` |
| All-events grid | minCol 240, gap 12, rowExtra 86, overscan 3 | — | — | — | — | LazyGrid over the page scroll signal | `ConcertHubPage.cs:238-278` |
| Hub shelves | `PagedShelf` measured, headerGap 8, maxItems 16, minCard 150 / maxCard 200 / gap 12 / edgeFade 36 | — | — | header = accent Eyebrow | — | — | `ConcertHubPage.cs:222-233`; `PagedShelf.cs:114-145` |
| Date block (standard) | 56×60 | gap 1 | 4 (`Radii.Control`) | Eyebrow month + BodyStrong day | `Tok.FillCardSecondary`, 1px `StrokeCardDefault`; month accent, day `TextPrimary` | — | `ConcertUi.cs:25-45` |
| Date block (compact) | 48×52 | gap 1 | 4 | same | same | — | `ConcertUi.cs:28-29` |
| Big date block | 84×92 | gap 2 | 4 | Eyebrow month · TextEl 34/700 day · Eyebrow weekday | month accent, day `TextPrimary`, weekday `TextSecondary` | — | `ConcertUi.cs:485-501` |
| Split hero (wide) | H 320; copy Grow .56; media Grow .44 | pad 28 | 8 | — | `FillCardDefault`, 1px `StrokeCardDefault`; media base `#1B1B1D` dark / `FillCardSecondary` light lerped to accent (0.30 dark / 0.18 light) | seam gradient 0°: `FillCardDefault` → α0 @ 22% | `ConcertUi.cs:256-284`, `ConcertLayout.cs:26-28` |
| Split hero (narrow) | media H 180, stacked | pad 20 | 8 | — | same | bottom gradient: α0 @68% → `FillCardDefault` @100% | `ConcertUi.cs:308-318` |
| Hero photo | `ImageFit.Cover`, `FocusY 0.30`, decode 1024 | — | — | — | placeholder = the tinted media fill | `ImageTransition.Fade(220 ms)` | `ConcertUi.cs:291-297` |
| Hero fallback glyph (no photo) | 52 (`Icons.Calendar`, or the caller's `fallbackGlyph`) | centred in the media pane | — | — | `Tok.TextSecondary` @ A 0.72 over the tinted fill | seam gradient still paints over it | `ConcertUi.cs:301-305` |
| Location button | MinH 32 (`WaveeSize.ControlH`), `Grow 1 / Basis 0 / AlignSelf Stretch`; boxed at 220 by the hero at wide | pad (12,4,12,4); gap 8; glyph boxes 20×20 and 16×20 | 4 (`Radii.Control`) | BodyStrong (`Grow 1 / Basis 0 / MinWidth 0` + ellipsis) | `FillControlDefault` / `FillControlSecondary` / `FillControlTertiary`; MapPin 16 + ChevronDown 10 at `TextSecondary` | 1px `Tok.ControlElevationBorder` (a **BorderBrush**, not `StrokeControlDefault` — the only concert control that uses the elevation border) | `ConcertUi.cs:511-538`, `ArtistSchedulePage.cs:241` |
| Tour-dates card | — | head pad (16,12,16,8), gap 8 | 8 | TextEl 28/36/600 | `FillCardDefault`, 1px `StrokeCardDefault` | `ClipToBounds` — the board's own dividers run edge-to-edge inside the radius | `ArtistSchedulePage.cs:277-299` |
| "No other dates" bounded card | — | pad (20,24,20,24), `AlignItems Center` | 8 | `EmptyState.Build` grammar | `FillCardDefault`, 1px `StrokeCardDefault` | — | `ArtistSchedulePage.cs:163-176` |
| Empty / error state (shared) | fills its region (`Grow 1`, centred both axes) | pad 24; gap 4; a 16-DIP spacer before the action | — | PageHero (wrap) + TrackMeta (wrap) | `Button.Standard`, never Accent; no glyph, no critical colour | — | `EmptyState.cs:68-75`, `ErrorState.cs` |
| Month tab strip | H 48 | — | — | `SelectorBar` | — | `AutoEdgeFade`, no scrollbar | `ArtistSchedulePage.cs:259-268` |
| Month board header | MinH 58 | pad (16,12,16,12); gap 12 | 0 | TextEl 20/28/600 + Body | `TextPrimary` / `TextSecondary` | — | `MonthBoard.cs:85-107` |
| Night tile | MinH 68 | pad (12,8,12,8); gap 12; text column gap 2; secondary row gap 4 | 0 | BodyStrong + Body secondary | `Interaction.Subtle`; trailing `ChevronRight` **16** `TextSecondary` | focus margin 2 | `MonthBoard.cs:139-176` |
| Run tile | MinH 68 | same | 0 | same + Caption chip | chip `Tok.AccentSubtle` + `AccentTextPrimary` (pad (8,1,8,1), r-full); trailing `ChevronDown` **16** `TextSecondary` | focus margin 2 | `MonthBoard.cs:178-223,274-279` |
| Near rail | 3×32 | — | 2 | — | `Tok.AccentDefault` (or **transparent** — the rail box is always laid out, so a near and a not-near tile are pixel-identical in width) | — | `MonthBoard.cs:238-242` |
| Near pin (tile secondary) | 12 (`Icons.MapPin`) | in the secondary row, gap 4 | — | — | `Tok.AccentTextPrimary` | — | `MonthBoard.cs:143,183` |
| Day column / run day column | W 44 / W 60 | gap 1 | — | BodyStrong day + Eyebrow weekday | `TextPrimary` / `TextSecondary` | — | `MonthBoard.cs:244-272` |
| Board dividers | 1px | — | — | — | `Tok.StrokeDividerDefault` | — | `MonthBoard.cs:281` |
| Show-all footer | MinH 44 | gap 4, centred | 0 | BodyStrong + chevron 14 | `Tok.AccentTextPrimary` | `Interaction.Subtle` | `MonthBoard.cs:225-236` |
| Shy month pill | H 32 | pad (12,0,12,0); overlay top pad 12 | full | Eyebrow, accent | `Tok.FillLayerDefault` + 1px `StrokeSurfaceDefault` | `Tok.AcrylicFlyout` + `Elevation.Card` | `ShyMonthPill.cs:114-130` |
| Detail identity block | — | headline column gap 4; facts column gap 8; the two blocks gap 12 | — | — | — | this is the hero's copy pane — it inherits the hero's pad 28 / 20 | `ConcertDetailPage.cs:166-176` |
| Detail fact row | icon 16 | gap 8 | — | Body wrap ≤2 (`Grow 1 / Basis 0 / MinWidth 0`) | icon `TextSecondary`, text `TextPrimary` | — | `ConcertDetailPage.cs:178-190` |
| Detail status pill | auto | pad (8,2,8,2) | full | Caption 12/16/600 | `Tok.FillSubtleSecondary` + `Tok.SystemFillCritical` ink | — | `ConcertDetailPage.cs:193-198` |
| "Near you" pill (schedule row) — **unmounted at 0.2.9 HEAD**: its only caller is the dead `ScheduleRow` (§9) | auto | pad (8,2,8,2); `AlignSelf Start`, `Shrink 0` | full | Caption | `Tok.AccentSubtle` + `AccentTextPrimary` | — | `ConcertUi.cs:804-809` |
| Offer card | — | pad 12 all; gap 4; Buy button box pad (0,4,0,0) | 8 | TrackTitle / TrackMeta / Body / TrackMeta | `FillCardDefault`, 1px `StrokeCardDefault` | — | `ConcertDetailPage.cs:237,244-253` |
| Ticket rail | W 320 | — | — | — | — | rides the page scroll (never its own viewport) | `ConcertDetailPage.cs:113-121` |
| Lineup card | — | head pad (16,12,16,12); rows pad (8,4,8,8) | 8 | caption + "N artists" Eyebrow secondary | `FillCardDefault`, 1px stroke; divider `Tok.StrokeSurfaceDefault` via `Divider()` | — | `ConcertDetailPage.cs:257-292` |
| Lineup row | MinH 60; avatar 44 (r22) | pad (8,4,8,4); gap 12 | 4 | TrackTitle | hover `FillSubtleSecondary`, press `FillSubtleTertiary` (only when navigable) | — | `ConcertDetailPage.cs:294-329` |
| Date flyout | W 320 | pad 8 | popup | rows Body 14/20 | popup chrome (`PopupChrome.Popup`) | flyout shadow from chrome | `ConcertDateFlyout.cs:48-53` |
| Flyout row button | MinH 40 | pad (12,4,12,4); gap 8 | 4 | Body | `Interaction.Subtle` | — | `ConcertDateFlyout.cs:223-241` |
| Calendar cells | header 38×20, day 38×32, gap 4 | — | 4 | Caption / Body | sel `AccentDefault`+`TextOnAccentPrimary`; range `AccentSubtle`; past `TextDisabled` | — | `ConcertDateFlyout.cs:195-220` |
| Where flyout | W 264 | pad 8; gap 2 | popup | Body | rows `Interaction.Subtle`; check 14 accent | — | `ConcertDateFlyout.cs:297-347` |
| Location picker | W 360; input 340×32; results MaxH 248 | pad 12; gap 8 | popup / 4 rows | Body, TrackTitle, TrackMeta | error `Tok.SystemFillCritical` | — | `ConcertUi.cs:1050-1128` |

---

## 4. Colour & material

All of the following are **per-theme** and read live from `Tok.*` (never cached into a static).

| # | input → function (file:line) | where applied | transition |
|---|---|---|---|
| 1 | `Concert.AccentColor` (uint ARGB from the provider's extracted dark tone, `Models.cs:216-231`) → `WaveePalette.ToColor(argb)` → `ColorF.Lerp(baseFill, accent, 0.32)` (`ConcertUi.cs:233-236`) | the artwork-less event tile's square pane (base `#1C1C1E` dark / `Tok.FillCardSecondary` light) | none — static per build; the tile re-renders when the concert row changes |
| 2 | accent → `Lerp(base, accent, dark ? 0.30 : 0.18)` (`ConcertUi.cs:261-264`) | `SplitEditorialHero` media pane when there is no photo, base `#1B1B1D` dark / `FillCardSecondary` light | none |
| 3 | seam gradient `LinearGradient(0°, FillCardDefault @0 → FillCardDefault α0 @22%)` (`ConcertUi.cs:311-314`) | wide hero: the left edge of the media pane dissolving into the copy card | static |
| 4 | seam gradient `GradientDown(α0 @68% → FillCardDefault @100%)` (`ConcertUi.cs:315-317`) | narrow hero: bottom of the 180-DIP media pane | static |
| 5 | accent → `Lerp(FillCardDefault, accent, 0.11)` (`ConcertUi.cs:446-448`) | `SpotlightCard` fill (**currently unused in 0.2.9** — see §9) | static |
| 6 | accent → hero wash `Lerp(base, accent, dark ? 0.5 : 0.18)` + bottom tint `GradientDown(α0 @45% → tint α (0.24 dark / 0.14 light) @100%)` inside a box with `EdgeFade(Bottom, 112)` (`ConcertUi.cs:353-388`) | `ConcertUi.Hero`'s 192-DIP atmosphere band (**unused in 0.2.9**) | photo `ImageTransition.Fade(220)` |
| 7 | `Tok.AccentSubtle` (= accent ramp shade at α 0.16, `Tokens.cs:444`) | "N nights" chip (`MonthBoard.cs:277`), calendar in-range days (`ConcertDateFlyout.cs:205`), and the "Near you" pill — the last **unmounted today** (§9) | `BrushTransitionMs` where declared |
| 8 | `Tok.AcrylicFlyout` over `Tok.FillLayerDefault` (`ShyMonthPill.cs:124`) | the shy month pill capsule — the ONLY acrylic on any concert surface | material is static; the pill's own opacity animates |
| 9 | `Tok.FillControlSolid` (opaque #FFFFFF light / #454545 dark) (`ConcertUi.cs:1146-1155`) | the fused pill's raised segment — deliberately NOT `FillCardDefault`, which in dark is 5% white and produced accent-on-accent ink | — |
| 10 | masthead clip feather `EdgeFadeSpec(EdgeMask.Top, 24)` mounted only while the clip is engaged (`ConcertHubPage.cs:117-121,136-143`; `ArtistSchedulePage.cs:105-107`; `ConcertDetailPage.cs:82-84`) | the top edge of the header card and of the scrolling body | the spec appears/disappears on the `ClipBelow` engage/release edge — nothing is softened at rest |
| 11 | `WaveeAccent.Decor` = `Tok.AccentTextPrimary` (`WaveeTokens.cs:50`) | every eyebrow: "Live music", "Filter by", section captions, tile dates, "On tour", "Next show · …", "Concert", "Tickets", "Lineup", "Related concerts", shy-pill label | follows the live accent (dynamic accent changes repaint) |
| 12 | `Tok.SystemFillCritical` | detail status pill ink, location-picker error text | — |
| 13 | procedural `EditorialArt` grounds: warm `#FFFF8A3D` radial (α 0.34 dark / 0.20 light at centre 0.5/0.66, radius 0.85) + two stroke-trim arcs; cool `#FF6C8CFF` 135° linear + 3 tiles (`ConcertUi.cs:911-1001`) | the Home "Concerts near you" / "Browse" editorial cards (chapter 11 owns them) | ambient loops, §5 |

Light/dark differences that are NOT simple token swaps and must be preserved:
`#1C1C1E` vs `FillCardSecondary` (tile/browse-all base), `#1B1B1D` vs `FillCardSecondary` (hero media base),
`#141416` vs `FillCardSecondary` (unused Hero band base), lerp factors 0.30/0.18 (hero) and 0.5/0.18 (band),
tint alphas 0.24/0.14, arc alphas 0.55/0.40 and 0.40/0.28, tile alphas 0.22/0.14 and border 0.34/0.24.

---

## 5. Motion

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| date chosen (rest → fused) | `Key "when-pill"` node | Position + Size (`SizeMode.Reflow`, `Axes.Width`) | rest width → fused width | 260 ms | `Easing.SmoothOut` (cubic-bezier .22,1,.36,1) | none | engine folds transitions to instant | `ConcertUi.cs:595-599,640-643` |
| same | the docked segment `":seg"` | Position + Opacity (Enter) | Dx +56, α 0.4 → 0,1 | 300 ms | SmoothOut | starts with the outer reflow (legs overlap) | instant | `ConcertUi.cs:654-658` |
| same | `Key "when-chip"` (exiting) | Position + Opacity (Exit) | 0,1 → Dx −56, α 0 | 220 ms | `Easing.FluentAccelerate` (.9,.1,1,.2) | none | instant | `ConcertFilterBar.cs:128-132` |
| token select / deselect | any `FilterToken` | Position + Size width reflow | old → new strip geometry | 220 ms | SmoothOut | none | instant | `ConcertUi.cs:551-554` |
| token select | token fills + border | brush cross-fade | control ramp ↔ accent ramp | 167 ms (`WaveeMotion.Fast`) | engine brush tween | none | unaffected (colour only) | `ConcertUi.cs:563` |
| more/less toggle | `Key "genre-more"` | Position + Size width | old → new | 220 ms | SmoothOut | none | instant | `ConcertUi.cs:682-685` |
| debounced count arrives | `CountTicker` shown value | numeric interpolation of the displayed integer | previous → target | 380 ms | cubic ease-out `1−(1−p)³` | 250 ms debounce upstream (`ConcertHubPage.cs:439`) | **NOT gated** — the tween always runs (see §9) | `CountTicker.cs:51-55` |
| count retarget mid-flight | `TickerClock` | remount (`Key "count-tick:{target}"`) | — | — | — | — | — | `CountTicker.cs:46-55` |
| scroll step on the schedule page | shy pill | Opacity (+Dy) Enter | α 0, Dy −8 → α 1, Dy 0 | 200 ms | SmoothOut | none | instant (declarative transition surface) | `ShyMonthPill.cs:47-52` |
| 1 s after the last scroll step | shy pill | Opacity (+Dy) Exit | α 1 → α 0, Dy −6 | 260 ms | SmoothOut | 1000 ms linger on the **anim clock** (a re-armed invisible duration track) | instant | `ShyMonthPill.cs:47-52,150-182` |
| drill into a month (date flyout) | flyout body (`Key "when-view:month:{i}"`) | Position + Opacity | Dx +8 → 0, α 0 → 1; outgoing Dx 0 → −8, α → 0 | 250 ms (`Expressive.Fast`) | SmoothOut | none | instant | `ConcertDateFlyout.cs:41-47`, `MotionRecipes.cs:193-203` |
| back out of a month | same node | mirrored (Dx −8 enter / +8 exit) | | 250 ms | SmoothOut | none | instant | `ConcertDateFlyout.cs:44,248` |
| hero photo decode | `ImageEl` in the split hero media pane | opacity | placeholder → image | 220 ms | engine image fade | none | unaffected | `ConcertUi.cs:296` |
| pending → ready (any concert surface) | the whole `Skel.Region` content | Opacity + TranslateY + Blur (`SkelReveal.Soft`) | shimmer → content | engine default; shimmer `ExitMs` 250 cross-dissolve | engine | none | snaps | `ConcertHubPage.cs:99`, `ArtistSchedulePage.cs:85`, `ConcertDetailPage.cs:62` |
| skeleton at rest | derived shimmer bars | opacity breathe | 1 → 0.5 → 1 | 1000 ms loop | engine | per-bar | still | `SkeletonRegion.cs:34-36` |
| page enter / exit (route swap) | the whole concert page | fade-through + Dx 8 slide | — | see chapter 18 | — | — | — | `ContentHost.cs:96-107` |
| card / row hover + press | tiles, rows, pills | fill only (`Interaction.Subtle`/control ramp) | — | engine brush | — | — | unaffected | `Interaction.cs:132-137` |
| month tab selected | `SelectorBar` indicator | the control's own indicator slide | — | engine control (chapter 02) | — | — | engine | `ArtistSchedulePage.cs:263` |
| scroll step below 0.5 DIP | shy pill | **nothing** — no resolve, no re-arm | — | — | — | — | — | `ShyMonthPill.cs:74` |
| board header leaves the viewport top | shy pill | Opacity Exit (no linger) | α 1 → 0 | 260 ms | SmoothOut | **0** — `ResolveMonth() == null` deactivates immediately | instant | `ShyMonthPill.cs:76-81,136-146` |
| run tile tapped | the run's tile subtree | none — the `Key "run:{uri}"` node is REPLACED by a divider-separated night stack | — | — | — | — | — | `MonthBoard.cs:125-137` |
| "Show all N" tapped | the board | none — the tiles beyond the cap simply appear and the footer unmounts | — | — | — | — | — | `MonthBoard.cs:225-236` |
| ambient editorial art (Home cards) | ground layer | TranslateY | −10 → +10 → −10 | 19,000 ms loop | keyframes | — | amplitude 0 (keyframe ARRAY swap, never a branch) | `ConcertUi.cs:1010,1024,1035` |
| same | outer arc | `StrokeTrimEnd` | 0 → 1 → 0 | 9,000 ms loop | keyframes | — | still | `ConcertUi.cs:1011,1036` |
| same | inner arc (counter-sweep) | `StrokeTrimEnd` | 0 → 1 → 0 | 7,000 ms loop | keyframes | — | still | `ConcertUi.cs:1012,1037` |
| same (Browse style) | 3 tiles | TranslateY (`CompositeOp.Add`) | 0 → +6 → 0 | 11/13/17 s loops | keyframes | co-prime | still | `ConcertUi.cs:1013-1015,1041-1043` |

**Frame-time rule.** `ShyPillLingerClock` correctly rides the engine animation clock (`anim.Animate(..., 1000 ms)` and
`anim.HasTracks`, `ShyMonthPill.cs:174-181`) and every `LayoutTransition` above is engine-driven.
**The one exception to port differently:** `TickerClock.OnFrame(DateTime.UtcNow.Ticks)` (`CountTicker.cs:69`) samples the
**wall clock** inside a `FrameClock.Tick` effect. It re-renders per frame but timestamps with `DateTime.UtcNow`. In 0.3 this
must read `FrameTime.NowQpc` (engine `FrameClock.PresentQpc`) — same 380 ms curve, same fixed 64-DIP box, correct clock.

---

## 6. Interaction

**Hub**
- Where pill — click toggles the where flyout anchored at its own realized node (`ConcertFilterBar.cs:82-83,215-234`).
  Label: `place.Name`, or `"Set location"` (`concerts.location.set`) when unresolved; ` · {N} km` appended whenever the radius
  is not the default 100 (`ConcertFilterBar.cs:111-117`).
  **The pill does NOT mark an inferred place.** `ConcertHub.LocationLabel` knows how to say `"Near Berlin (approximate)"`
  (`ConcertHubModel.cs:132-136`, and the string exists at `concerts.nearApproximate` :503) but the bar builds its label with
  its own private `WhereLabel` instead, and `Inferred` — a `required` prop the page dutifully passes
  (`ConcertHubPage.cs:125`) — is **never read in `Render`**. So in 0.2.9 an IP-guessed location is presented as if the user
  had chosen it. Decide this deliberately in 0.3: either wire `LocationLabel` in (the intended design) or delete the prop.
- Where flyout items, in order: **Search cities** (MapPin, ChevronRight) → closes and opens the location picker;
  **Use my location** (MapPin) → closes and starts the OS consent flow; divider; **Within 25 km / 50 km / 100 km**
  (ToggleButton role, check on the active one) → keeps the flyout open, re-labels the pill, requeries
  (`ConcertDateFlyout.cs:283-302`, `ConcertFilterBar.cs:236-253`).
- When area — clicking either shape toggles the date flyout. The "This weekend" chip is a one-tap shortcut that applies the
  preset directly (`ConcertFilterBar.cs:141-147`).
- Date flyout root, in order: **Anytime**, **Today**, **This weekend**, **Next weekend**, divider, then four month rows
  (current month + 3) with a trailing ChevronRight. Month leaf: back button, **All of {Month}**, each **Weekend · {range}**,
  the calendar, then **Clear** / **Show events**. First day tap = start; a second tap ≥ start closes the range; a tap before
  the start restarts it; a third tap starts fresh (`ConcertDateFlyout.cs:186-193`).
- Genre strip — `All` clears (no-op when already empty); each token toggles multi-select and requeries;
  `+N genres` expands; `Show less` collapses (only offered when `all.Count > 3`) (`ConcertFilterBar.cs:150-185`).
- Event tile / grid cell — click navigates to `concert:{uri}` with the concert title (or venue) as the route arg;
  Button role, focusable, 2-DIP focus-visual margin, hand cursor (`ConcertUi.cs:136-153`).
- Playlist promo card — click navigates to `pl:{uri}`; **drag** source (`WaveeDragKinds.Resource`,
  `WaveeResourceDragPayload.ForEntity(Playlist, …)`) so it can be dropped on the sidebar to pin/file or onto another
  playlist to copy tracks (`ConcertHubPage.cs:318-322`). Concerts, merch and gallery photos are deliberately NOT drag
  sources. There is **no context menu anywhere** on the concert surfaces.
- Empty-state action "Set location" opens the picker anchored at the where pill (`ConcertHubPage.cs:107`).
- Keyboard: the filter row and every card are focusable in DOM order (where → when → chip → All → tokens → more → shelves →
  grid). Flyouts are focus-trapped with light dismiss (`ConcertFilterBar.cs:201`).

**Location picker** (`ConcertUi.cs:1050-1128`, `ConcertLocationController.cs`)
- Typing debounces 220 ms then searches; an empty query clears results with no network call; a search that throws shows
  `concerts.location.searchFailed`.
- "Use my location" runs the OS consent prompt on the UI thread, then **reverse-geocodes and fills the SAME results list** —
  it never saves by itself, the user still taps a row (`ConcertLocationController.cs:130-172`). Its button reads
  "Locating…" and is disabled for the whole round trip. Failures map to distinct messages
  (`concerts.location.permissionDenied | unavailable | timedOut | failed`, `ConcertScheduleModel.cs:35-43`), an empty
  reverse result to `noMatches`, a throwing reverse lookup to `lookupFailed`.
- Selecting a row saves the place; a row with a blank id is refused inline with `concerts.location.invalid`; on success
  the picker closes and the page bumps its location epoch (hub) or reload counter (schedule); on failure
  `concerts.location.saveFailed` is shown inline.
- Every open resets query/results/loading/error before the overlay is created (`ConcertLocationController.cs:76-79`);
  the picker is anchored `BottomEdgeAlignedLeft` with `FocusTrap` + `LightDismiss` + `PopupChrome.Popup` +
  `ConstrainToRootBounds`, exactly like the two filter flyouts (`:92-96`).

**Artist schedule**
- Month tabs (`SelectorBar`) select a month; the selection is remembered by `"yyyy-MM"` key across reloads
  (`ConcertScheduleModel.cs:233-244`). With no remembered key the SPOTLIGHT's month wins, then the first month.
- The hero's location button is the flyout anchor for BOTH the hero control and the "No other dates near you" card's
  "Set location" action (`ArtistSchedulePage.cs:174,227-228`) — there is no where-pill on this page.
- Its label is `savedPlace.Name`, else `GetArtistPageLocationAsync()`'s place name, else `concerts.location.set`
  (`ArtistSchedulePage.cs:70-75`) — a second, independent location read from the hub's.
- Night tile → detail route. Run tile → expands in place (no navigation); once expanded its nights are individual tiles that
  navigate (`MonthBoard.cs:120-137,204`).
- "Show all N" → un-caps the month permanently for this page instance (`MonthBoard.cs:230`).
- "View event" (accent) and the location button sit in the hero; both are keyboard reachable.
- The shy pill is **not** interactive by construction (`HitTestVisible = false` + `HitTestPassThrough`).

**Concert detail**
- "Buy tickets" opens the validated external URL through `InputHooks.Current.Default.OpenUri` (`ConcertDetailPage.cs:238-239`).
- Lineup rows navigate only when the artist URI parses as `spotify:artist:…`; otherwise the row is inert (Role `None`,
  arrow cursor, no hover fill, no chevron) — no dead affordance (`ConcertDetailPage.cs:294-329`).
- Related shelf cell 0 ("Browse all concerts") navigates to the hub.

**Accessibility names / roles** — `AutomationRole.Button` on tiles, rows, pills, flyout rows, calendar days and the
show-all footer; `AutomationRole.ToggleButton` on filter tokens and radius rows (that is what announces selected state,
since the check glyph was deliberately removed from tokens); `AutomationRole.None` + `Focusable = false` on a
billing-only lineup row. **No tooltips and no context menus exist on any concert surface in 0.2.9** (verified: no
`ContextMenu` / `MenuFlyout` / `ToolTip` / `OnKeyDown` anywhere under `Features/Concerts` or in `ConcertUi.cs`) — there
are also **no keyboard shortcuts**; Tab/Enter/Esc and the wheel are the whole input vocabulary. The focus visual itself
is not uniform — see the FOCUS note at the end of W23 before "unifying" it.

**Localised strings** (`src/apps/Wavee/assets/loc/en-US.json`): `concerts.liveMusic` :425, `.title` :426, `.subtitle` :427,
`.browseAll` :431, `.allGenres` :432, `.genreFallback` :433, `.nearYou` :434, `.recommendedForYou` :435, `.allEvents` :436,
`.playlistsForScene` :437, `.emptyTitle` :438, `.emptyWithoutLocation` :439, `.emptyForLocation` :440;
`concerts.filter.*` :442-456 (`filterBy`, `dates`, `thisWeekend`, `nextWeekend`, `anytime`, `today`, `withinKm`,
`moreGenres`, `showLess`, `events`, `allOf`, `weekend`, `custom`, `showEvents`, `clear`);
`concerts.location.*` :459-473; `concerts.schedule.*` :476-485 (`onTour`, `noUpcoming(+Subtitle)`, `noMoreDates(+Subtitle)`,
`nextShow`, `viewEvent`, `tourDates`, `showCount` plural, `showAll`); `concerts.detail.*` :488-500.
**Not localised in 0.2.9 (hard-coded English, must be fixed or knowingly carried):** `"Near you"` pill text
(`ConcertUi.cs:77`), `"Tickets"` button on the spotlight card (`ConcertUi.cs:479`), `"N nights"` chip (`MonthBoard.cs:184`),
`"Concert"` fallback (`ConcertUi.cs:54`, `ConcertScheduleModel.cs:333`), `ConcertScheduleShaping.StatsLine`'s
`" show/shows"`, `" cities"` (`ConcertScheduleModel.cs:187-189`), `RelativeTime`'s `"today"/"tomorrow"/"in N days|weeks|
months"` (`:206-219`), `SupportActs`' `"with "` / `"+n more"` (`:339-360`), `ConcertOffers.AvailabilityLabel` /
`SaleWindowLabel` / `ConcertHub.LocationLabel`'s `"Set location"` + `"Near … (approximate)"`,
`ConcertHub.ConceptLabel`'s `"Genre"` default.

**Six of those strings ALREADY EXIST in `en-US.json` and are simply never read** — the fix is wiring, not authoring:
`concerts.setLocation` :502, `concerts.nearApproximate` :503, `concerts.genre` :504, `concerts.available` :505,
`concerts.soldOut` :506, `concerts.fallbackTitle` :507. (The Home card's own keys `concerts.homeTitle` :428 /
`homeSubtitle` :429 / `explore` :430 belong to chapter 11.) Everything still genuinely missing is a NUMBER-bearing
sentence — the stats line, the relative time, the nights chip, the support-act list — and each needs an ICU plural form
in 0.3, not a concatenation, because `showCount` :484 and `artistCount` :498 already prove the house style.

---

## 7. Data & readiness in 0.3 terms

The plan gives `EntityKind.Concert` (§4.1:151), a `ConcertTable` in `Scope` (§4.1:199) and a `Concert.cs` budget of 200
lines — and **nothing else**: no `ConcertFields`, no concert columns, no concert edges in `Edges` (§4.3:325-336), no
concert decode entry in `Spotify.Decode` (§4.6), no place/geo storage, no feed/pagination concept at all.

| visual element | 0.2.9 data source | 0.3 read (proposed) | readiness predicate (skeleton until true) |
|---|---|---|---|
| Hub header card | static loc strings | — | always ready |
| Where pill label | `IConcertService.GetUserLocationAsync` + `IsUserLocationInferredAsync` (`ConcertHubPage.cs:364-367`) | `Concert.Place Entities.Current.Places.Saved` + flag `PlaceFlags.Inferred` | `Knows(PlaceFields.Identity)`; until then the pill shows `concerts.location.set` (never a spinner) |
| Radius suffix | page signal `_radius` (default 100) | UI state, persisted with the saved place | n/a |
| When pill / fused pill | page signal `_when` (`ConcertWhen`) | UI state (pure `ConcertWhen` in `Concert.cs`) | n/a |
| Genre tokens | `GetConceptsAsync(geoHash, bias)` (`ConcertHubPage.cs:451-471`) | `Edges.PlaceConcepts.Targets(placeSlot)` + `ConceptTable` | `Edges.PlaceConcepts.State == complete`; before that render only the "All" token (today's behaviour) |
| Count ticker | `GetFeedCountAsync` debounced 250 ms (`ConcertHubPage.cs:434-449`) | `Concert.Feed.Count` — a derived fact published on the feed subject row, not probed by the UI | shows nothing (`null` → 0) until the first count lands; it is *previewed*, so it may lag the feed by design |
| Nearby / Recommended shelves | `ConcertFeedPage.Sections[kind].Concerts` | `Edges.FeedSection.Targets(sectionSlot)` with `FeedSectionEdge(Kind)` | section edge `state == complete` **and** every tile's `Concert.Knows(ConcertFields.Tile)`; a section with partial rows must not paint |
| Playlist promos | `section.PlaylistPromotions` (`PlaylistRef`) | `Edges.FeedSectionPlaylists.Targets(sectionSlot)` → `Playlist` handles | playlist `Knows(PlaylistFields.Identity)` |
| All-events grid | merged `AllEvents` list (`ConcertHubPage.cs:508-515`) | `Edges.FeedSection` for the AllEvents section; count = `Length` | grid renders rows for realized cells only; a cell whose concert is not `Knows(Tile)` renders the tile skeleton, never partial text |
| Append tail | `ConcertFeedPage.PaginationKey` (opaque) | `FeedSubject.PaginationKey` (StringId) + `FeedSubject.Complete` flag | tail preloader mounts only while a key exists |
| Event tile date caption | `Concert.Date` | `Concert.Date` (int64 unix + `OffsetMinutes` int16 — the offset MUST be stored, §8) | `ConcertFields.When` |
| Event tile art | `Concert.Image` (+ `AccentColor`) | `Concert.Image` (StringId) + `Concert.Accent` (uint) | `ConcertFields.Art`; no art ⇒ the tinted fallback pane (a designed state, not a missing one) |
| Event tile title / place | `Title`, `Venue`, `City` | `Concert.Title/Venue/City` (StringId ×3) | `ConcertFields.Identity` |
| Schedule hero | `ArtistConcertSchedule.Artist`, `.HeaderImage` | `Artist` handle + `Artist.HeaderImage` | `Artist.Knows(Identity \| Header)` |
| Stats line | `ConcertScheduleShaping.Stats` over the whole schedule | derived on commit from `Edges.ArtistConcerts` | edge `state == complete` — a partial schedule would print a wrong show/city count |
| Month boards | `GroupByMonth(BoardConcerts(Chronological(...)))` | same pure functions over `Edges.ArtistConcerts.Targets(artistSlot)` | as above; boards are all-or-nothing |
| Near-you set | `schedule.Nearby` + `Concert.IsNearUser` | `ConcertFlags.NearUser` + `Edges.ArtistConcertsNearby` | `ConcertFields.Identity` on every board concert (the guard counts them) |
| Detail facts | `ConcertDetails.*` | `Concert` cold group: `DoorsOpenAt`, `AgeRestriction`, `Status`, `Region`, `Country` | `ConcertFields.Detail` |
| Offers | `ConcertDetails.Offers` | `Edges.ConcertOffers` with an `OfferEdge` payload | `Edges.ConcertOffers.State != unknown`; **omit the whole section** when empty (today's rule) |
| Lineup | `ConcertDetails.Artists` (`ConcertArtist` may have no URI) | `Edges.ConcertLineup` → `Artist` slots + a `LineupEdge(StringId BillingName, byte Flags)` for URI-less billing text | `Edges.ConcertLineup.State == complete` |
| Related | `ConcertDetails.Related` | `Edges.ConcertRelated` | complete |
| Drill trail / titles | `ConcertRoutes` + route arg | `Shell.Route(RouteKind.Concert, subject, arg)` | — |

### DATA GAPS — what the 0.3 model does not hold yet

| element | 0.2.9 source | proposed 0.3 column / edge |
|---|---|---|
| The concert row itself | `Concert` record, `Models.cs:216-231` (12 fields) | `ConcertTable`: `Title, Venue, City, Region, Country, Image` (StringId×6), `Date` (int64 unix) + `OffsetMinutes` (short, **required** — the UI prints the provider's local clock), `Accent` (uint), `Flags` (Festival, NearUser, HasArt), `DoorsOpenAt` (int64+offset), `AgeRestriction` (StringId), `Status` (byte enum + raw StringId), `Venue`/`MetroArea` ids (StringId), `Lat`/`Lon` (float×2) |
| `ConcertFields` bitmask | implicit (whole record or nothing) | `Identity = Title\|Venue\|City\|Date`, `Tile = Identity\|Art\|When`, `Detail = Tile\|Doors\|Ages\|Status\|Region\|Country\|Coords`, `Offers`, `Lineup`, `Related` |
| Extracted accent colour | `Concert.AccentColor` / `ConcertArtist.AccentColor` — decoded from the concert/lineup branch, **not** from a cover palette (`Models.cs:225-230`) | `Concert.Accent` (uint) + `Artist.HeaderAccent` (uint). Must NOT be routed through the cover-palette pipeline; the provider ships it |
| Places / geo | `ConcertPlace` (`ConcertModels.cs:19-25`), saved server-side; `GeoCoordinates` | a small `PlaceTable` (`Id`, `Name`, `Region`, `Country`, `GeoHash` StringIds + `Lat/Lon`) with a `Saved` slot and `PlaceFlags.Inferred`; it is account state, not catalog state |
| Concepts (genres) | `ConcertConcept(Uri, Name, Weight)` per geohash (`ConcertModels.cs:65`) | `ConceptTable(Uri, Name, Weight)` + `Edges.PlaceConcepts` (ordered by the provider's weight — the ORDER is the UI's top-3 rule) |
| Feed sections + pagination | `ConcertFeedPage` / `ConcertFeedSection` with `Append` dedupe (`ConcertModels.cs:80-150`) | a synthetic **feed subject** slot per filter tuple (the plan's `HomeSection`/`SearchResult` precedent, §4.3:335): `Edges.FeedSection` (parent = feed subject, payload `FeedSectionEdge(Kind, StringId Key)`), `PaginationKey` (StringId) and `Complete` flag on the subject. `ReplacePage` (§4.3:310) gives the append semantics; the dedupe currently in `ConcertFeedPage.Append` becomes edge-insert dedupe |
| The filter tuple → subject identity | `ConcertFeedQuery` (`ConcertServices.cs:10-16`) | hash of (placeId, radius, from, to, concept uris) → feed subject slot; changing any of them is a different subject, which is what makes "reset pagination on filter change" structural instead of manual |
| The live count preview | `GetFeedCountAsync` (never cached, by contract) | `FeedSubject.Count` (int) + `CountVersion`; explicitly a *preview* column that may be newer than the edge list |
| Offers | `ConcertOffer` (12 fields, `ConcertModels.cs:32-44`) | `Edges.ConcertOffers` payload `OfferEdge(StringId Provider, StringId Url, byte Availability, int MinPriceCents, int MaxPriceCents, StringId Currency, int SaleStart, int SaleEnd, byte Flags)` |
| Lineup with no catalog artist | `ConcertArtist.Name` with `Uri == null` (`ConcertModels.cs:7-12`) | `LineupEdge(StringId BillingName, StringId Image, StringId HeaderImage, uint Accent, byte Flags)` — the edge must carry a name for an artist slot that does not exist |
| Artist schedule | `ArtistConcertSchedule` (`ConcertModels.cs:69-74`) | `Edges.ArtistConcerts` (+ `Edges.ArtistConcertsNearby`, or a `NearUser` flag per edge) and `Artist.HeaderImage` |
| "Is my location inferred" | `IsUserLocationInferredAsync` | `PlaceFlags.Inferred` on the saved place row |
| Geolocation outcome → message | `LocationErrors.ForStatus` (`ConcertScheduleModel.cs:31-44`) | pure, ports as-is into `Concert.cs` |

**Readiness rule for this surface (the owner's regression bar):** a shelf, a board or the grid appears only when its edge
list is `complete` and every row it will paint knows `ConcertFields.Tile`. Sections popping in one at a time, or a tile
rendering with a venue but no date, is a regression. Pages demand their whole model at mount
(`Entities.EnsureEdges(feedSubject, FeedSection)` + `Entities.EnsureRows(allConcertSlots, ConcertFields.Tile)`); the grid's
`ensureRange` stays a **no-op** exactly as in 0.2.9 (`ConcertHubPage.cs:276`) — paging is the tail preloader's job, never
the visible window's.

---

## 8. Pure rules to port verbatim

| class | 0.2.9 file | decides | tests | 0.3 destination |
|---|---|---|---|---|
| `ConcertLayout` | `Features/Concerts/ConcertLayout.cs:5-38` | `ScheduleWide` 760/720, `DetailWide` 920/860, `EditorialHeroWide` 760/720 hysteresis; `EditorialHero` metrics (320/320/.44/28 wide, 0/180/1/20 narrow); `WideEditorial` metrics at ≥900 / ≥600 / else | `Wavee.Tests/ConcertLayoutTests.cs:9-42`, `ConcertDetailModelTests.cs:121-128`, `ConcertSchedulePageTests.cs:92-101` | `Concert.cs` CORE |
| `EditorialArtGeometry` + `ArcSweep` + `BrowseTile` | `ConcertLayout.cs:64-121` | the procedural card art's arcs/tiles (BCL-only, invariant path data) | `ConcertLayoutTests.cs:44-118` | `Concert.cs` CORE (art itself is Home's — chapter 11) |
| `ConcertHub` | `Features/Concerts/ConcertHubModel.cs:24-163` | single + multi `ToggleConcept`, `PresetRange` (Fri–Sun weekend math), `TopConcepts` (head 3 + selected stragglers), `WhenLabel` ("Jul 17 – 19" / "Jul 31 – Aug 2" / "Jul 14", EN DASH), `ReconcileConcept`, `LocationLabel`, `ConceptLabel` (EDM/R&B/Hip Hop casing), `IsFeedEmpty` | `Wavee.Tests/ConcertHubModelTests.cs` (188 lines, 18 facts) | `Concert.cs` CORE |
| `ConcertWhen` / `ConcertWhenKind` / `ConcertHubFilters` | `ConcertHubModel.cs:9-20` | the filter tuple the bar edits | — | `Concert.cs` CORE |
| `ConcertSchedules` | `Features/Concerts/ConcertScheduleModel.cs:14-29` | chronological order + URI dedupe (stable sort) | `ConcertSchedulePageTests.cs:27-65` | `Concert.cs` CORE |
| `LocationErrors` | `ConcertScheduleModel.cs:32-44` | geolocation status → message (distinct per failure) | `ConcertSchedulePageTests.cs:67-90` | `Concert.cs` CORE |
| `ConcertRun` / `ConcertMonthGroup` / `ConcertRunColumns` / `ConcertTourStats` | `ConcertScheduleModel.cs:48-78` | the board's value types (`ShowCount`, `OverflowsCap`, `Uri`) | via the shaping tests | `Concert.cs` CORE |
| `ConcertScheduleShaping` | `ConcertScheduleModel.cs:83-361` | `TileCap = 6`, `Spotlight`, `BoardConcerts`, `GroupByMonth`, `DetectRuns` (same venue+city AND next calendar day), `MonthKey`, `Stats`, `StatsLine` (the cities segment appears ONLY while `2 ≤ cities < shows`; a single-month span collapses to "MMM yyyy"), `RelativeTime`, `MonthBoardColumns`, `ResolveMonthIndex`, `MonthTabLabels` ("MMM ’yy" at year boundaries), `BalancedColumns`, `NearIsInformative`, `GuardedNearSet`, `TileText`, `SupportActs` | `ConcertSchedulePageTests.cs:103-430` — **19** facts, not 23 (the file's other 7 are `Chronological` ×3, `LocationErrors` ×2, `ScheduleWide`, and one service guard) | `Concert.cs` CORE |
| `ConcertOffers` | `Features/Concerts/ConcertDetailModel.cs:10-60` | `ValidTicketUrl` (absolute http/https only), `PriceLabel` ("45 - 75 EUR", invariant digits + provider currency CODE), `AvailabilityLabel`, `SaleWindowLabel` (preserved offset) | `Wavee.Tests/ConcertDetailModelTests.cs:20-93` | `Concert.cs` CORE |
| `ConcertDetailInfo` | `ConcertDetailModel.cs:63-97` | `DisplayStatus` (hides UNKNOWN/CONFIRMED/SCHEDULED, humanises the rest), `LocationLine` (case-insensitive dedupe) | `ConcertDetailModelTests.cs:95-119` | `Concert.cs` CORE |
| `ConcertRoutes` / `ConcertRoute` / `ConcertRouteKind` | `Features/Concerts/ConcertRoutes.cs:7-65` | `concerts`, `artist-concerts:{id}`, `concert:{id}`; opaque ids | `Wavee.Tests/ConcertRouteTests.cs` (54 lines) | `Shell.cs` route table + `Concert.cs` builders |
| `ConcertFeedPage.Append` dedupe | `Wavee.Core/Domain/ConcertModels.cs:95-149` | section merge by (Kind, Key), concert/promo dedupe by URI, pagination key replacement | — (no direct test today) | becomes `Edges.ReplacePage` + insert-dedupe in `Concert.cs`; keep the merge order rule |
| `SegmentedPillStyle` | `Components/ConcertUi.cs:1138-1187` | the three pill registers (Accent 32/26, Strip 28/22, Sidebar 28/22) — every preset is a PROPERTY, never a static field, because `Tok.*` is a live theme read | — | `Concert.UI.cs` (shared; Home + sidebar consume it) |
| `ShyMonthTracker` | `Features/Concerts/ShyMonthPill.cs:17-22` | the registry the pill resolves the current month from without being an ancestor of the scroller | — | `Concert.Page.cs` |

**Four of the rules above are TEST-ONLY at 0.2.9 HEAD** — tested, documented, and called by nothing in
`src/apps/Wavee` (verified by grep). Port them as *decisions*, not by reflex:

| rule | tested by | what the production code does instead |
|---|---|---|
| `ConcertHub.LocationLabel` | `ConcertHubModelTests.cs:61-76` (4 facts, incl. the "approximate" marker) | `ConcertFilterBar.WhereLabel` (`:111-117`) — plain name + radius, no inferred marker |
| `ConcertHub.ReconcileConcept` | `ConcertHubModelTests.cs:33-49` | `ConcertHubPage.PublishConcepts` (`:476-492`) re-implements it inline for the MULTI-select list and additionally requeries when the kept set shrank |
| `ConcertHub.ToggleConcept(string?, string)` (the single-select overload) | `ConcertHubModelTests.cs` | only the `IReadOnlyList<string>` overload ships (`ConcertFilterBar.cs:183`) |
| `ConcertScheduleShaping.MonthBoardColumns` | `ConcertSchedulePageTests.cs` | `MonthBoard` renders 1 or 2 columns from `_wide && visibleCount > 1` + `BalancedColumns` (`MonthBoard.cs:54-73`) — the fit math is never consulted |

If 0.3 keeps the multi-select strip and the private where-label, three of the four are dead weight; if it wires
`LocationLabel` in (see §6), that one becomes live and the "approximate" state needs a wireframe.

---

## 9. Re-author notes

**Must not be simplified**

1. **The two-clip sandwich** around the sticky filter bar (`ConcertHubPage.cs:114-144`). One clip is wrong and looks wrong.
2. **Generation-guarded queries.** Every completion re-checks its captured generation before publishing, a filter change
   cancels and resets pagination, and a late page is dropped (`ConcertHubPage.cs:344-351,391-429`). In 0.3 the feed subject
   identity plus `Scope.Epoch` replaces the counter, but the *behaviour* — a late answer never paints — is the contract.
3. **Concept reconciliation.** A selected concept survives a location change only while still offered; the feed requeries
   only when the kept set actually shrank (`ConcertHubPage.cs:476-492`).
4. **The seed shapes.** `FeedSeed` (4 shelf + 6 grid concerts), `SeedDetails` (facts + 2 offers + 3 lineup rows),
   `SeedSchedule` (8 concerts with a 2-night run at offsets 33/34) exist so the shimmer has the right silhouette. Port the
   seeds, not just the skeleton call.
5. **`EventChrome = 70` is derived, not a magic number.** Tile height =
   pad-top 8 + art (cellW − 16, the card's two 8-DIP side insets, square) + art/text gap 8 +
   text column (eyebrow 16 + 3 + title 20 + 3 + meta 16 = 58) + pad-bottom 12
   = cellW + (8 − 16 + 8 + 58 + 12) = **cellW + 70**. The grid's `rowExtra` is then 70 + `GridRowGap` 16 = 86, which is
   what leaves a real gutter between rows. If the type ramp or the paddings move, recompute both
   (`ConcertHubPage.cs:236-240`, `ConcertUi.cs:136-152`).
6. **`SkeletonProxy` rule:** the seed "All events" section must use a plain `AutoGrid`, never the `LazyGrid` — a stateful
   scroll-windowed component must not mount inside a shimmer derivation (`ConcertHubPage.cs:244-261`).
7. **Basis/Shrink asymmetry in the schedule row's secondary line:** the city grows AND shrinks with a measured basis
   (`Basis = 0` would give it zero shrink weight and the "Near you" pill would paint over it) —
   `ConcertUi.cs:69-77`. Same idiom in `MonthBoard`'s secondary rows.
8. **Zero-allocation scroll vs per-row richness:** the hub reconciles them by realizing only the scroll window through
   `LazyGrid` while keeping the tile itself rich; the appended pages grow the COUNT read through a delegate, never the
   realized tree (`ConcertHubPage.cs:266-289`). The schedule/detail pages are bounded (one month, one lineup) and are
   plainly composed on purpose. Do not "virtualize" the month board — a board is ≤ 6 tiles by construction.
9. **Props freeze traps:** `ConcertFilterBar` takes `Signal`s for every live value (`ConcertFilterBar.cs:28-40`);
   `MonthBoard` is re-keyed on a composed signature string (`ArtistSchedulePage.cs:271-274`) *and* takes a revision signal
   for expansions; `EditorialArt` bakes its geometry into its Key so a resize remounts (`ConcertUi.cs:739-743`);
   `ConcertAppendPreloader` is re-keyed per pagination key (`ConcertHubPage.cs:217`); `TickerClock` and
   `ShyPillLingerClock` are keyed so a re-arm is a remount (`CountTicker.cs:55`, `ShyMonthPill.cs:96`).
10. **The pill morph is a shared KEY, not a fly animation.** `RestDatePill` and `SegmentedDatePill` MUST keep the same
    `"when-pill"` key or the morph silently degrades to a swap (`ConcertUi.cs:589-620`).

**Dead code in 0.2.9 — decide deliberately, do not port by reflex.** `ConcertUi.Hero` (+`BuildHero`, the 192-DIP atmosphere
band, `ConcertUi.cs:346-419`), `HeroStatsLine` (:424), `SpotlightCard` (:435-482), `BigDateBlock` (:485-501),
`ScheduleRow` (:51-107) and `TimeMeta` (:504-508) have **zero production call sites at 0.2.9 HEAD** (verified by grep over
`src/apps/Wavee` and `src/apps/Wavee.Tests`): R3.1 replaced them with `SplitEditorialHero` plus the copy block built inside
`ArtistSchedulePage.TourHero`. That is ~220 lines. Port them only if the owner wants the band/spotlight back; otherwise the
0.3 chapter of record is the `SplitEditorialHero` composition in W13/W19.
Dead with them, because `ScheduleRow` was their only caller: `ConcertUi.StatusPill` (`:804-809`) and `CityLine` (`:819-827`) —
so the "Near you" pill in §3 and §4 row 7 is, today, an unmounted shape. And four PURE rules are test-only in the same
sense (§8's table) — `LocationLabel`, `ReconcileConcept`, the single-select `ToggleConcept`, `MonthBoardColumns`.

**Where the plan is wrong or too thin for this surface**

- **§2 line budget (`Concert.cs` 200 + `Concert.UI.cs` 400 + `Concert.Page.cs` 600 = 1,200).** The pure rules alone are 807
  lines today (`ConcertHubModel` 163 + `ConcertScheduleModel` 361 + `ConcertDetailModel` 97 + `ConcertLayout` 121 +
  `ConcertRoutes` 65) and they are ported verbatim, before a single column is declared. 200 lines for `Concert.cs` is off by
  ~5×.
- **§2 tree has no place for the concert *hub*'s non-entity state.** The feed is not a concert; it is a filtered subject with
  sections and an opaque pagination key. Either `Concert.cs` grows a feed-subject table (the `HomeSection`/`SearchResult`
  synthetic-parent precedent of §4.3:335 applies) or the plan needs a named partial, e.g. `Concert.Feed.cs`.
- **§4.3 `Edges` declares no concert edges at all.** This surface needs at least six: `ArtistConcerts`,
  `ArtistConcertsNearby`, `FeedSection`, `FeedSectionPlaylists`, `ConcertLineup`, `ConcertOffers`, `ConcertRelated`,
  `PlaceConcepts` — three of them with non-trivial payload structs (§7).
- **§4.6 `Spotify.Decode` lists no concert decoder.** 0.2.9 has a whole Pathfinder adapter
  (`SpotifyConcertService` + `ConcertPathfinderRequests`) behind `IConcertService`'s 12 operations, including the
  `concertCount` preview that is "never cached" by contract. Wave 2 owner E's file list must name it or Wave 5 owner N has
  no data to render.
- **§4.12/§4.13 (`Track.UI.cs` row, `Album.Page.cs`) are the templates the plan offers.** Neither covers: a page that owns
  *query state* rather than an entity (the hub), an anchored multi-level flyout, a floating scroll-driven overlay, or a
  per-frame leaf clock. The three clock/overlay idioms (`TickerClock`, `ShyPillLingerClock`, the `Overlay.Service` anchor
  pattern) have no equivalent in the plan and must be carried over as-is.
- **§5 Wave 5 owner N is also the whole Artist page and discography.** Concerts alone is ~4,100 live lines across three
  pages, two flyouts, a picker panel, a board, a pill, a ticker and a preloader. This is the largest single-owner load in
  Wave 5 after the sidebar; consider splitting Concert off to its own owner.
- **Missing from the §2 tree entirely:** the location/place state (account-scoped, not catalog-scoped — it has no home in
  `Entities`, `Platform` or `User` as written), and the geolocation PAL call
  (`svc.Geolocation.RequestAsync`, `ConcertLocationController.cs:140`) which must stay a UI-thread call for the OS consent
  prompt.

**Design-doc drift (the CODE above wins; each line is one disagreement)**

- `concert-ui-component-plan.md:44` says the date block is "64-DIP or compact 52-DIP" — the code is 56×60 / 48×52
  (`ConcertUi.cs:28-29`).
- `concert-ui-component-plan.md:30,97` says All Events is `Ui.AutoGrid(300, gap, 112)` with "no `LazyGrid`" — the code
  virtualizes with `LazyGrid(minCol 240, gap 12, rowExtra 86, overscan 3)` and keeps AutoGrid only for the seed
  (`ConcertHubPage.cs:242-281`).
- `concert-ui-component-plan.md:31,93` says concepts are a `SelectorBar` — the code is a multi-select `FilterToken` strip
  (a SelectorBar has exactly one selection; this strip routinely has none or five, `ConcertUi.cs:541-547`).
- `concert-ui-component-plan.md:75-81` describes the artist schedule as "chronological `Flow.For` rows + an optional
  nearby shelf" — superseded by R3/R3.1: split hero + month boards + shy pill, no nearby shelf.
- `concerts-feed-filters-v2-plan.md:237,246` gives the fused segment `Key "when-seg"` and `Fill = Tok.FillCardDefault`
  with an outer `Shadow = Elevation.Card` — the code keys it `"{key}:seg"`, fills it `Tok.FillControlSolid` (the
  translucent-card mistake is documented at `ConcertUi.cs:1146-1150`) and the outer capsule carries **no** shadow.
- `concerts-feed-filters-v2-plan.md:272` gives the chip's exit easing as `SmoothIn` — the code uses
  `Easing.FluentAccelerate` (`ConcertFilterBar.cs:130`).
- `concert-navigation-hub-geolocation-plan.md:349` says the spotlight "tucks up into the hero via a shallow negative top
  margin" — R3.1 removed both the negative margin and the spotlight card; the next-show block now lives inside the hero's
  copy column (`ArtistSchedulePage.cs:197-225`).

**Line budget**

| | lines |
|---|---|
| 0.2.9 total (`Features/Concerts/*` + `ConcertUi.cs`) | 4,735 |
| minus code owned by other chapters (`WideEditorialDestination`+`BuildWideEditorial` 100, `EditorialArt`+`EditorialArtMotion` 217, the shared `SegmentedPill` grammar + 3 styles ~95) | −412 |
| minus dead code (Hero/HeroStats/Spotlight/BigDateBlock/ScheduleRow/TimeMeta) | −220 |
| **0.2.9 live, concert-owned** | **≈ 4,100** |
| plan §2 target (`Concert.cs` 200 + `.UI.cs` 400 + `.Page.cs` 600) | 1,200 |
| **honest estimate** — `Concert.cs` ≈ 1,050 (807 ported rules + ~250 columns/handle/fields), `Concert.UI.cs` ≈ 900 (tiles, date blocks, pills, split hero, flyout panels, picker), `Concert.Page.cs` ≈ 1,900 (hub 600, filter bar 260, schedule 330, month board 290, detail 390, shy pill + ticker + preloader 330 — the query orchestration shrinks by ~150 because the entity layer owns generations) | **≈ 3,850** |

---

## 10. Parity checklist

Reference build: `C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe`.

**Read this first.** In `--fake` the concert service is `NullConcertService` (`App/Services.cs:360`), so the hub, schedule
and detail pages reach their **empty** states: every *loaded* item below (marked **[live]**) must be verified with both
builds signed into the same account with the same saved location, side by side. `--fake` still verifies (marked **[fake]**)
all chrome, layout, empty/error states, the filter bar, every flyout, the picker, motion of the pills and the ticker at 0,
and the artist page's own concert entry points (`FakeData.SynthConcerts`, `Wavee.Core/Fakes/FakeData.cs:205-213`, gives
fake artists a Tour banner + "Upcoming concerts" shelf whose tiles route to `concert:wavee:concert:…`).
Routes: hub = sidebar rail "Concerts" or the Home "Concerts near you" card; schedule = artist page Tour banner;
detail = any concert tile.

1. **[fake]** Route `concerts` @ window 1440. Static capture: header card = accent "LIVE MUSIC" eyebrow, 28/36 "Concerts",
   one-line secondary subtitle; card radius 8, 1px stroke, no shadow.
2. **[fake]** Same capture: the gap between header card and filter bar is 16, gutters 36, first content pixel 108 below the
   page top.
3. **[fake]** Filter bar: caption row shows "FILTER BY" left, the count right-aligned inside a 64-wide box; the number and
   the word "events" never move when the count changes.
4. **[fake]** Where pill reads "Set location" with a MapPin 14 and chevron 10; height exactly 32; fully rounded.
5. **[fake]** When area at rest shows two separate pills: `[📅 Dates ▾]` then `[This weekend]`, 8 DIP apart.
6. **[fake]** Tap "This weekend". Frame recording: the chip slides LEFT ~56 DIP while fading, the pill's width grows over
   ~260 ms, and a raised `Tok.FillControlSolid` segment (`#FFFFFF` light / `#454545` dark) `⟨✓ This weekend⟩` arrives from the RIGHT. No flicker, no relayout jump of
   the genre tokens beyond the pill's own growth.
7. **[fake]** Fused pill: outer is accent, segment is opaque with a flyout-grade shadow, value text and chevron are
   on-accent ink. Compare the segment's fill in dark theme — it must not be translucent.
8. **[fake]** Tap the fused pill's chevron → the date flyout opens bottom-left-aligned under it. Root list order:
   Anytime / Today / This weekend / Next weekend / divider / four "MMMM yyyy" rows with chevrons.
9. **[fake]** Flyout width 320, rows 40 high, divider hairline; drill into a month: frame recording shows a forward page
   slide (Dx 8, 250 ms), back shows the mirror.
10. **[fake]** Month leaf: calendar grid is Sunday-first, 7 columns of 38×32 cells with 4 DIP gaps, two-letter weekday
    headers, past days greyed and inert.
11. **[fake]** Tap day 12 then day 15: 12 and 15 get the accent plate, 13–14 get `AccentSubtle`, and "Show events" becomes
    enabled. "Clear" resets both.
12. **[fake]** Where flyout: width 264, two action rows then a divider then 25/50/100 km radios with a single accent check;
    picking 50 km keeps the flyout OPEN and re-labels the pill to "… · 50 km".
13. **[fake]** Location picker: 360 wide, 340×32 input with placeholder "Search cities", "Use my location" is a Standard
    (not Accent) button, results scroll inside a 248-high box.
14. **[fake]** Hub empty state with no location: "No concerts found" at PageHero scale, subtitle "Set your location to
    discover live music near you.", a single quiet "Set location" button; tapping it anchors the picker at the WHERE PILL.
15. **[live]** Hub empty state WITH a location set but no results: subtitle changes to "Nothing is on sale for this
    location right now…" and the button is GONE.
16. **[fake]** Force an error (disconnect the network on the live build, or compare the shared `ErrorState`): headline
    "Something went wrong", one caption, one "Retry" — no glyph, no red.
17. **[fake]** Scroll the hub until the header card passes the masthead: hover-free static capture at the transition — the
    header's top edge feathers over 24 DIP, the filter bar pins crisply at y = 84 with NO feather on its own top, and the
    feed below feathers independently.
18. **[fake]** Scroll back to the top: both feathers disappear (no permanent softening at rest).
19. **[live]** Hub loaded @1440: "NEAR YOU" and "RECOMMENDED FOR YOU" shelves, then "PLAYLISTS FOR THE SCENE", then
    "ALL EVENTS" — in that order, 20 DIP apart, each caption an accent eyebrow.
20. **[live]** Event tile: date caption is accent ("Fri, Aug 21 · 19:00" in current culture, the format's own casing — NOT
    uppercased); title and place each exactly one line with ellipsis; the square cover fills the rounded top.
21. **[live]** A concert with no artwork shows the tinted pane with a centred 38-DIP calendar glyph, tinted toward the
    event's accent colour — not a grey box.
22. **[live]** Grid column count at content ≈1100 is 4 with 12-DIP gutters; widen to ≈1400 and it becomes 5 with the cards
    growing, never a ragged last row.
23. **[live]** Scroll to the bottom of "All events": a 4-card shimmer row appears, a page appends, and the shimmer does NOT
    immediately reappear until you scroll again.
24. **[live]** Park at the bottom without scrolling: nothing keeps loading (no chain-load), and there is no standing
    skeleton pulse.
25. **[fake]** Playlist promo card (live build; compare composition only): cover + 2-line title + source subtitle,
    **no play FAB**, drag it toward the sidebar and a drag chip appears (concert tiles must NOT produce one).
26. **[live]** Change a filter (radius, genre, date): the count re-previews ~250 ms after the edit and the number EASES to
    the new value over ~380 ms (frame recording); the caption row does not reflow.
27. **[live]** Rapidly toggle three genres: only one feed request's result paints (no flashing of intermediate feeds),
    and the tokens never shift horizontally on selection.
28. **[fake]** Select a genre token: it becomes a filled accent pill of exactly the same width as before; no check glyph
    appears.
29. **[fake]** "+N genres" is dashed-bordered with an accent LABEL and a secondary chevron; expanding shows the full list
    with "Show less" at the end.
30. **[live]** Navigate hub → a detail → back: the hub returns with the SAME scroll offset and the SAME accumulated pages
    (KeepAlive slot preserved), not a re-fetch from page one.
31. **[fake]** Route `artist-concerts:<uri>` @1440 (live build shows real data; fake shows the empty state) — empty state
    reads "No upcoming concerts" with the "no dates on sale right now" subtitle and NO action.
32. **[live]** Schedule hero @ content ≥760: one card, copy left (~56%), photo right (~44%), height 320, photo cropped
    with a high focal bias (faces not decapitated), a soft vertical blend at the seam only, no scrim, no text on the photo.
33. **[live]** Hero copy order: "ON TOUR" accent eyebrow, artist name at 28/36 (≤2 lines), stats line
    "N shows · M cities · MMM – MMM yyyy", hairline divider, then the next-show row with a 56×60 date block and
    "NEXT SHOW · IN 5 DAYS".
34. **[live]** Hero actions: accent "View event" then a 220-wide location button on the same row at wide; resize below 720
    and they STACK, the media pane moves above the copy and becomes 180 high.
35. **[live]** Resize slowly from 800 → 740 → 730 → 715: the layout must NOT flip until 720 (hysteresis), then must not
    flip back until 760.
36. **[live]** Tour dates card: "Tour dates" at 28/36/600, a month tab strip 48 high with edge fades, then a month header
    at 20/28/600 with "N shows" right-aligned.
37. **[live]** Wide month board: two columns split BALANCED and chronological (every left entry precedes every right
    entry), separated by a 1px full-height divider, top-aligned (a short column does not stretch).
38. **[live]** A multi-night residency renders as ONE tile with a "14–16 / Tu–Th" day column and an accent-subtle
    "3 nights" chip and a ChevronDown; tapping it expands into individual night tiles separated by hairlines.
39. **[live]** A month with more than 6 tiles shows "Show all N" in accent with a chevron at the bottom; tapping it reveals
    the rest and removes the footer.
40. **[live]** Near-you marking: at most ⌈shows/3⌉ (min 3) tiles carry the 3×32 accent rail and a 12-DIP map pin. If the
    response marks everything near, NOTHING is tinted.
41. **[live]** A venue-less event shows the CITY as its primary line (never the artist's own name repeated) with
    "19:30 · with Band of Silver" underneath.
42. **[live]** Frame recording while scrolling the boards: the month pill fades in top-centre (Dy −8, 200 ms), tracks the
    month at the viewport top, and fades out ~1 s after you stop. It must not appear while the hero is at the top.
43. **[live]** With the pill visible, scroll with the wheel over it: the page scrolls normally (the pill never eats the
    wheel).
44. **[live]** Route `concert:<uri>` @1440: two-column layout, 320-wide ticket rail on the right, main column left; the
    ticket rail scrolls with the page (it has no scrollbar of its own).
45. **[live]** Resize to 900: the rail collapses and the "TICKETS" section appears inline immediately after the hero
    (before Lineup) — check the order, not just the presence.
46. **[live]** Detail facts, in order: date "dddd, MMMM d, yyyy · HH:mm", doors ("Doors open 18:30"), venue · city, region,
    country, ages. Each with its own 16-DIP glyph; each omitted entirely when absent.
47. **[live]** A cancelled/postponed event shows a neutral-fill pill with critical-coloured 12/16/600 text directly under
    the title; a normal event shows NO status pill.
48. **[live]** An offer with no valid URL renders "Tickets not available online" as quiet metadata — never a greyed button.
49. **[live]** Lineup: 44-DIP circular avatars, "N artists" eyebrow on the right of the header, and a billing-text-only
    artist has no chevron, no hover fill and no hand cursor (hover capture).
50. **[live]** Related shelf cell 0 is the "Browse all concerts" tile with layered calendar/pin glyphs at 30/44/22 DIP and
    a "LIVE MUSIC" eyebrow; it navigates to the hub.
51. **[fake]** Detail unavailable state: "Concert not available" + "This event may have been removed or is no longer
    listed.", no action.
52. **[fake]** Dark/light: flip the theme on both builds on the hub and on a schedule page; compare the hero media base
    (`#1B1B1D` vs the light card-secondary), the fused segment (opaque both ways) and the shy pill's acrylic.
53. **[fake]** Reduced motion ON: the pill fuse, the shy pill, the flyout drill and the skeleton reveal all snap; the count
    ticker still counts (known 0.2.9 behaviour — decide deliberately if 0.3 changes it).
54. **[fake]** Keyboard only: Tab from the filter bar through where → when → chip → All → tokens → more; every stop shows a
    2-DIP focus ring; Enter opens the flyouts and the flyouts trap focus; Esc light-dismisses.
55. **[fake]** Drill trail on all three routes: "Browse › Concerts" on the hub, "Browse › Concerts › {route arg}" on
    schedule and detail — the last crumb is the ROUTE ARG (the concert title-or-venue, the artist name), and with a null
    arg it falls back to repeating "Concerts". A `NavOrigin` (arriving from Search, from Home) can prepend or replace the
    Browse root — check that too, not just the IA case (`DrillTrail.cs:53-66` + `Compose`).
56. **[fake]** Grid/shelf ARITHMETIC at two widths. At window 1440 (content ≈1100, inner ≈1028) count 6 shelf cards and
    4 grid columns; measure one grid cell ≈248 wide and the row pitch ≈334. If you measure 266/352 the port fed the
    un-guttered width into `Fit`/the column math (§2 arithmetic rule).
57. **[live]** Hub feed section that carries playlist promos but ZERO concerts: the promo shelf still renders on its own
    (the two `if`s are independent, `ConcertHubPage.cs:203-208`) — no empty concert shelf above it.
58. **[fake]** Hub with no location: the genre strip is the lone `[All]` token and BOTH dividers still render (no geohash
    ⇒ no concepts call). Set a location and it repopulates without the strip jumping vertically.
59. **[live]** Rapidly toggle a genre, then immediately change the location: a concept the new place no longer offers
    disappears from the strip AND the feed requeries once — not twice, and never with the stale concept on the wire.
60. **[live]** Artist schedule for an artist whose dates are ALL in the past: no hairline divider, no next-show row, no
    accent "View event" — the location button alone, stretched full width, and the media pane untinted (W13b).
61. **[live]** Artist with no header image: the hero's media pane is a flat tinted plate with ONE centred 52-DIP calendar
    glyph (not the tile's 38), with the seam gradient still crossing it (W13c).
62. **[live]** Wide month board where a run tile is venue-primary: the secondary row reads `[pin] City [3 nights]` —
    the city BEFORE the chip. A city-primary run reads `[pin] [3 nights] with …` (W15).
63. **[live]** A month of 7 tiles where two are 3-night runs: the footer says "Show all 11" (shows), not "Show all 7".
64. **[live]** Detail page at ≥920 for an event with NO offers: ONE full-width column, no 320 rail, no empty rail gutter.
65. **[live]** Detail hero photo provenance: for an event whose summary has no image but whose lineup does, the hero
    shows the first lineup artist's HEADER image (not their avatar, not a grey pane) — and the tint still comes from the
    summary's accent when it has one.
66. **[live]** A lineup artist with no picture: a 44-DIP circular SEEDED fallback, identical on every visit and on the
    artist page — never an empty circle.
67. **[fake]** Location picker: tap "Use my location", approve. The button reads "Locating…" and is disabled, then the
    RESULTS LIST fills — the picker must NOT close and must NOT save on its own. Tap a row to save.
68. **[fake]** Location picker errors: deny OS permission (permissionDenied copy), then disconnect and type a city
    (searchFailed copy). Two different sentences, both `SystemFillCritical`, both wrapping at ≤3 lines.
69. **[fake]** Focus audit: Tab through the filter bar and then into the grid. The CARDS show a 2-DIP inset ring; the
    pills, tokens and flyout rows show the engine's default focus visual with NO 2-DIP margin. Do not "fix" this in 0.3
    without the owner saying so — it is what makes a pill's ring hug its capsule (W23 FOCUS note).
70. **[live]** Shy pill negative test: scroll so the board header is 5 DIP BELOW the viewport top and stop. The pill must
    be gone immediately (no 1-second linger) — the linger only applies to the "stopped scrolling" case.
71. **[fake]** An inferred (IP-derived) location: confirm what the where pill says. 0.2.9 shows the bare city name; if
    0.3 wires `LocationLabel` in it will read "Near {city} (approximate)". Either is defensible — a silent divergence
    between the two builds is not (§6).

---

## 11. Audit log

Adversarial re-read of all 16 `Features/Concerts/*.cs` + `Components/ConcertUi.cs` against the chapter, plus the engine
constants each number resolves through (`Spacing`, `Radii`, `Expressive`, `MotionRecipes`, `Interaction`,
`SkeletonRegion`, `PagedShelf`), `BrowseMastheadMetrics`, `WaveeTokens`, `EmptyState`/`ErrorState`, `DrillTrail`,
`en-US.json`, and the five test files. One line per correction.

**Verified correct (spot list, so a later reader does not redo it):** `Reserve` = `Spacing.XXXL` 32 + `TitleLine` 52 =
**84**; `ClipFadeBand` = `DetailVerticalLayout.StickyFadeBand` = **24**; `PlayerDock.Reserve` = **72** ⇒ hub bottom pad
108, schedule/detail 112; `Radii.Card` 8 / `Control` 4 / `Full` 999; `Spacing` XS 4 / S 8 / M 12 / L 16 / XL 20 / XXL 24
/ PageWide 36 — so every padding, gap and `Height = Spacing.L` spacer in §3 resolves as printed. `WaveeMotion.Fast`
**167**; `Expressive.Fast` **250** and `DistBase` **8** ⇒ the flyout page-slide row in §5 is right; `SkeletonStyle`
defaults PulseMs 1000 / PulseMin 0.5 / BarRadius 4 / **ExitMs 250** (the `Expressive.Slow` floor applies only to
`SkelReveal.None`, and concerts use `Soft`). `PagedShelf.Create` defaults minCard **150** / maxCard **200** / gap **12** /
edgeFade **36** — §3's shelf row is right, and `headerGap` is overridden to 8 at all three call sites.
`WaveeAccent.Decor => Tok.AccentTextPrimary` at `WaveeTokens.cs:50`. `Interaction.Subtle` =
FillSubtleTransparent/Secondary/Tertiary. `Concert` is 12 fields at `Models.cs:216-231`. Loc line numbers :425-:500 all
check out. The §9 dead-code claim (Hero/BuildHero/HeroStatsLine/SpotlightCard/BigDateBlock/ScheduleRow/TimeMeta) is
confirmed by grep. "No context menu / no tooltip" is confirmed; I add "no keyboard shortcut" to it.

| # | section | kind | correction |
|---|---|---|---|
| 1 | §2 W1 | **wrong** | Shelf and grid arithmetic used the raw content width 1100 instead of the inner width (content − the page's own 36+36 gutters = 1028). Shelf was "6 × 173.3, tile H 243" → **6 × 161.3, tile H 231**; grid was "cellW 266, rowH 352" → **cellW 248, rowH 334**. Added a standing "arithmetic rule" note above the wireframes so the same slip cannot recur. |
| 2 | §2 W12 | **wrong** | Same slip at 600: shelf "3 × 192" → **3 × 168** (inner 528); grid "cellW 294, rowH 380" → **cellW 258, rowH 344**. Added the one-column floor (~372 inner DIP). |
| 3 | §2 W1 | wrong | "caption gap 4" conflated two gaps. The filter card's COLUMN gap is 4 (`Spacing.XS`); the caption ROW's gap is 8 (`Spacing.S`, `ConcertFilterBar.cs:66`). Both now stated, and a §3 row added for each. |
| 4 | §2 W6 / §3 | missing | The genre strip's internal gap is 8, not the outer row's 12 (`ConcertFilterBar.cs:171`). Added to W6 and as a §3 row. |
| 5 | §2 W6 | missing | "Show less" carries **ChevronUp**, "+N genres" ChevronDown — the `expanded` flag flips the glyph (`ConcertUi.cs:694`). Chapter had only "chevron 10 secondary". |
| 6 | §2 W6 | missing | Three unwritten strip states: `all.Count == 0` (no geohash ⇒ lone `[All]`, both dividers still drawn), `all.Count ≤ 3` (no "+N" at all), and a selected concept vanishing under a location change. Also the `"filter-token:{label}"` key and the "All is a no-op when already empty" rule. |
| 7 | §2 W10 / §6 | **wrong** | "Use my location" was described as if it saved. It does not: it reverse-geocodes and fills the SAME results list; the user still taps a row (`ConcertLocationController.cs:130-172`). Button becomes "Locating…" + disabled meanwhile. |
| 8 | §2 W10 / §6 | missing | The picker's error line has **nine** sources, not four. Added `searchFailed` :465, `noMatches` :470, `lookupFailed` :471, `invalid` :472 to the existing four geolocation strings + `saveFailed`, as a table. Also the reset-on-open rule and the popup options. |
| 9 | §2 | missing | **W13b** — the no-spotlight hero: no divider, no next-show row, no "View event", the location button unboxed and stretched, and the media pane untinted (accent comes from the spotlight). A whole real state was absent. |
| 10 | §2 | missing | **W13c** — the no-header-image hero: a 52-DIP centred calendar glyph at α 0.72, not the tile's 38 (`ConcertUi.cs:301-305`). Added as a §3 token row too. This is also the seeded/pending hero's look. |
| 11 | §2 W15 / §3 | missing | The run tile's secondary row ORDER is conditional: a venue-primary run inserts the city BEFORE the "N nights" chip (index `near ? 1 : 0`), a city-primary run trails it with the support acts (`MonthBoard.cs:182-195`). The chapter drew only one arrangement. |
| 12 | §2 W15 | missing | "Show all N" prints the SHOW count while the cap counts TILES (runs) — 7 tiles can read "Show all 11" (`MonthBoard.cs:50-51,233`). Added as checklist 63. |
| 13 | §2 W16 | **overclaim** | "tracks the month at the viewport top" and checklist 42's same phrasing. There is exactly ONE board mounted and one tracked `PanelHeader`; the rule is `header.Y ≤ viewport.Y + 4` ⇒ show the page-written label, else deactivate **immediately, with no linger** (`ShyMonthPill.cs:136-146`). Added the 0.5-DIP scroll deadband, the silent first step, and the blanked-tracker case. |
| 14 | §2 W19 | missing | The hero's photo-resolution chain (header-image artist → summary image → artist avatar) and the INVERTED accent order (summary first) — `ConcertDetailPage.cs:99-103`. Getting these backwards changes the page's colour on real data. |
| 15 | §2 W19 | missing | Wide + **no offers** ⇒ one full-width column, no rail (`:111`). The two-column shape is a function of offers, not width. |
| 16 | §2 W19 | missing | Lineup avatar with no image = the seeded procedural fallback keyed on a stable URI/name hash (`:306,358-363`); and the degenerate offer card (no provider name / unknown availability / no price / no sale) collapsing to two lines. |
| 17 | §2 W23 | **overclaim** | "focus: 2-DIP focus-visual margin" was generalised, and checklist 54 said "every stop shows a 2-DIP focus ring". Only the card/tile/row family sets `FocusVisualMargin`; pills, tokens, flyout rows, calendar days, the back button and the show-all footer use the engine default. Enumerated both lists. |
| 18 | §3 | missing | No row existed for `ConcertUi.LocationButton` — a shipped, hero-mounted control with its own metrics and the only concert control using `Tok.ControlElevationBorder` (a BorderBrush, not `StrokeControlDefault`). Added. |
| 19 | §3 | missing | Browse-all card row said "glyphs 30/44/22" without placement. They are a ZStack: Calendar 30 top-left (pad 12), MapPin 44 centred, Calendar 22 bottom-right (pad 12). |
| 20 | §3 | missing | Rows added for the detail identity block's three gaps (4 / 8 / 12), the offer card's Buy-button `(0,4,0,0)` lift, the night/run tiles' secondary-row gap 4 and their 16-DIP trailing chevrons, the 12-DIP near pin, the tour-dates card's `ClipToBounds`, the bounded "No other dates" card, and the shared empty/error metrics. |
| 21 | §3 / §4 / §9 | missing | The "Near you" pill (`ConcertUi.cs:804-809`) is presented as live in both tables, but its ONLY caller is the dead `ScheduleRow`. Flagged in all three places; `CityLine` is dead with it. |
| 22 | §3 | missing | The near rail is always LAID OUT (transparent when not near), so a near tile and a plain tile are pixel-identical in width — stated, because a 0.3 port that omits the box shifts the whole tile. |
| 23 | §5 | missing | Four motion rows: the `SelectorBar` month-tab indicator; the sub-0.5-DIP scroll deadband; the shy pill's **immediate** (no-linger) exit when the header leaves the top; and the explicit "no transition" on run-expand and show-all (both are plain remounts — do not add one in 0.3 by accident). |
| 24 | §6 | **wrong/missing** | The where pill never marks an inferred place: `ConcertHub.LocationLabel`'s "Near … (approximate)" is unreachable and the `required Inferred` prop the page passes is **never read in `Render`**. Called out as a deliberate 0.3 decision with checklist 71. |
| 25 | §6 | missing | The schedule page's location button is its own anchor for both the hero control and the empty card's action, and its label comes from a SECOND location read (`GetArtistPageLocationAsync`) the chapter never mentioned. Also the month-tab fallback order (remembered key → spotlight's month → first). |
| 26 | §6 | missing | Six of the "not localised" strings **already exist unused** in `en-US.json` at :502-507 (`setLocation`, `nearApproximate`, `genre`, `available`, `soldOut`, `fallbackTitle`) — the fix is wiring, not authoring. The genuinely-missing ones are all number-bearing and need ICU plurals. |
| 27 | §6 | missing | Added "no keyboard shortcuts" and `AutomationRole.None` on billing-only lineup rows to the a11y paragraph. |
| 28 | §8 | **wrong** | `ConcertSchedulePageTests.cs:103-430` has **19** facts, not 23 (the file's 26 total include 3 `Chronological`, 2 `LocationErrors`, 1 `ScheduleWide` and 1 service guard). |
| 29 | §8 / §9 | **unverified→resolved** | Four pure rules listed for verbatim porting have **no production call site**: `LocationLabel`, `ReconcileConcept`, the single-select `ToggleConcept`, `MonthBoardColumns`. Added a table naming what ships instead, so Wave 5 ports them as decisions rather than by reflex. |
| 30 | §8 | missing | `StatsLine`'s conditional: the cities segment renders only while `2 ≤ cities < shows`, and a single-month span collapses to "MMM yyyy" (`ConcertScheduleModel.cs:183-202`). W13's example line is the both-true case. |
| 31 | §1.1 | wrong | The tree named `ConcertUi.EventTile` as the call target; every page actually calls `ConcertUi.VerticalCard` (`:157`), a one-line forwarder. Also: the shelf caption is "Near you" only for `Kind == Nearby` — every other non-AllEvents kind falls through to "Recommended for you", there is no third caption. |
| 32 | §1.1 | missing | Two guard branches: `EventCell` returns an empty `BoxEl` for an index past the live list during an append reconcile (`:286`), and the whole append preloader is absent when `PaginationKey` is null (`:210`). |
| 33 | §10 | wrong | `App/Services.cs:359` → **:360**. `DrillTrail.cs:53-65` → `:53-66`, and the last crumb is the ROUTE ARG with a "Concerts" fallback, plus a `NavOrigin` can prepend/replace the Browse root. |
| 34 | §10 | missing | 16 new parity items (56-71) covering the arithmetic regression, promo-only sections, the empty genre strip, the concept-reconcile requery, W13b/W13c, run-tile ordering, the tile-vs-show count, offerless wide detail, hero provenance, avatar fallbacks, the two-step geolocation, two error strings, the focus audit, the shy-pill negative test and the inferred-location divergence. |

**token-reconcile (2026-09-12):** §11 item 29's segmented-control prose said "a raised white/`#454545` segment". That pair IS a token — `Tok.FillControlSolid`, `#FFFFFF` light / `#454545` dark (`BuildWinUILight():410` / `BuildDark():299`) — so the sentence now names it and keeps the literals. Separately, `Tok.ControlElevationBorder` (cited here at `:801`) is now carried in `00-design-system.md §12.1`.
