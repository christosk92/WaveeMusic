# Home page (frame, hero, facets, wash, section flow, readiness) — 0.3 visual fidelity contract

> 0.2.9 sources: `src/apps/Wavee/Features/Home/HomePage.cs` (1744) · `HomeLandingProjection.cs` (368) · `HomeFacetChips.cs` (214)
> · `HomeFacetProjection.cs` (183) · `HomeFeedReadiness.cs` (164) · `HomeFoldTile.cs` (143) · `HomeTimelineMerge.cs` (114)
> · `HomeWashSource.cs` (106) · `HomeSectionAppendPreloader.cs` (96) · `HomePreferences.cs` (88) · `BrowseSectionWalk.cs` (82)
> · `HomeFacetStrip.cs` (74) · `HomeHeroLayout.cs` (70) · `HomeFeedDiagnostics.cs` (60) · `HomeModuleCopy.cs` (24)
> · `HomeCardPlayRouting.cs` (14) — **3 544 lines**; plus the frame's direct dependencies `HomeModules.cs` (804, geometry
> table + Fold deck), `HomeCards.cs:266-433` (the hero band), `HomeBrowseCards.cs` (93, the Charts deck),
> `Features/Shell/ContentHost.cs` (322), `Features/Shell/ShellMastheadBand.cs` (131), `App/ShellMaterial.cs` (59),
> `Features/Shell/ShellMaterialLayer.cs` (133), `Features/Shell/ShellWashGeometry.cs` (55), `App/DaylistNotifier.cs` (129).
> | 0.3 target: the settled seven-file Home set (arbitration 2026-09-12, A15) — `Entities/Home.cs` (CORE),
> `Home.UI.cs`, `Home.Cards.UI.cs`, `Home.Artists.UI.cs`, `Home.Page.cs`, `Home.Customizer.cs`, `Home.Host.cs`.
> This chapter writes in the first, second and fifth. | Wave 5 owner **P**
>
> After Wave 0 every 0.2.9 path below lives at `src/apps/_old/Wavee/<same relative path>`.
> Sibling chapters: `00-design-system.md` (tokens, type ramp, cover palette, CTAs, motion tokens), `02-cards-and-controls.md`
> (`PagedShelf`, `MediaCard`, `Surfaces.Artwork`), `11-home-cards-and-modules.md` (every module + card skin),
> `12-home-section-and-customizer.md` (the drill page + `home-layout.json`), `18-shell-frame.md` (page transitions, masthead,
> the material layer that *paints* Home's wash), `19-shell-overlays.md` (toasts), `25-sidebar.md` (the pane whose width
> decides Home's row width).

---

## 0. The non-negotiables

1. **Home reveals ONCE, and everything reveals together.** The skeleton stays up until the feed the session settles on
   AND the two chrome resources (Charts deck, notification timeline) have concluded — or 1 500 ms after the feed settled,
   whichever is first (`HomeFeedReadiness.ChromeSettleMs = 1500`, `HomeFeedReadiness.cs:60`). No row ever pops into an
   already-revealed page; no cached shelf is painted and then replaced 1.5 s later. After the first reveal every later
   publish is a Ready→Ready swap in place (`HomeRevealGate.Offer`, `HomeFeedReadiness.cs:131-146`).
2. **Home never sits on the skeleton forever.** 8 000 ms after mount the page force-publishes the best answer it has seen
   — even a withheld placeholder, else `HomeFeed.Empty` (`HomeFeedReadiness.ForceReleaseMs = 8000`, `HomePage.cs:192`,
   `HomePage.cs:929-934`). Three details the port must keep: it is a **no-op if the loadable already left Pending by any
   path** including Failed (`:931`); it passes `liveCatalogConcluded: true` so `Classify` cannot withhold it again
   (`:933`); and it skips the chrome hold but NOT the epoch and facet gates, so a forced publish can never regress
   behind a real answer that landed at 7 999 ms (`HomeFeedReadiness.ApplyEpoch`, `:80-82`). Armed once, `DepKey.Empty` —
   not re-armed per epoch/auth re-run.
3. **The greeting is the hero's eyebrow, not a banner.** "Good morning, Christos · your daylist" sits *inside* the hero
   band as Caption 12/16/600 tertiary ink (`HomePage.cs:792-800`, `HomeCards.cs:308-312`). The standalone two-line greeting
   block (PageHero 28/36 + "Here's what's been on rotation.") exists ONLY when the page has no hero
   (`HomePage.cs:1175-1184`). The "· your daylist" tail appears only when the hero card's `Meta.Format == "daylist"`.
4. **The window carries the page's colour, not the page.** Home publishes THREE radial washes (hero / weekly / mix) into
   the shell material; the shell paints them over live Mica behind the chrome, clipped above the player dock
   (`ShellMaterialLayer.cs:44-77`). They are window-anchored: they do **not** scroll with the feed.
5. **No invented colour, ever.** A wash leg resolves from the card's payload accent (`extractedColors.colorDark`) first,
   the graded cover second, and NOTHING third — a null leg contributes no layer (`HomeWashSource.cs:59-71`). The loading
   seed therefore resolves to an empty wash and the shell keeps the previous page's material rather than dipping neutral.
6. **The facet strip is a tab strip, never pills.** 14/600 labels going secondary→primary, a 3-DIP accent underline inset
   12 each side, a 1-DIP divider under the whole strip (`HomeFacetChips.cs:124-177`). Selecting a sub-chip *fuses* the
   parent into a segmented pill **under the same node key**, so the label morphs in place instead of popping
   (`HomeFacetStrip.cs:49-73`, `HomeFacetChips.cs:158-167`).
7. **A facet is a different DOCUMENT, not a filter.** It renders the server's own sections in server order
   (`HomeFacetProjection.Rows`), keeps every titled section's own title, coalesces only *consecutive* baseline sections,
   and keeps its own extent table and its own scroll offset (`HomePage.cs:529-589`).
8. **The chip row survives the All↔facet swap.** Both viewports describe it under the byte-identical key
   `"home:row:Chips"` (`HomePage.cs:476-483`, `HomePage.cs:541-547`) so the fused pill's morph is never replayed.
9. **Every row is capped and centred.** Row content stops growing at `WaveeSize.PageMaxW = 1600` and centres; the gutter
   is `Spacing.PageWide = 36`, the same NavigationView measure every other page uses (`HomePage.cs:767-782`).
10. **The estimator and the renderer state the same geometry.** Every row height in `HomeFeedVirtualLayout.Estimate`
    is the renderer's own arithmetic through `HomeModuleLayout` / `HomeHeroLayout` (`HomePage.cs:1316-1403`). A
    disagreement re-pins the scroll anchor mid-scroll, which reads as the feed jumping under the cursor.
11. **Hero = one complete square cover, integrated.** Edge is the surface height (336 / 344 / 384), faded in from the left
    over 96 DIP, never stretched or cropped into a banner (`HomeHeroLayout.cs:47-56`, `HomeCards.cs:408-424`).
12. **Charts is present in EVERY state.** Pending shimmers a Fold-shaped deck, Ready-empty shows "No charts right now",
    Failed shows the retry state — the row never collapses to 0 and never flaps the anchor (`HomePage.cs:738-746`,
    `HomeModuleLayout.FoldStateExtent`, `HomeModules.cs:585`). The two settled arms share ONE estimate (152) but not one
    grammar: Ready-empty is `EmptyState.Compact` (Subtitle 20/28, no action ⇒ ≈ 76 of content), Failed is
    `ErrorState.Build` → the PAGE-scale `EmptyState.Build` (PageHero 28/36 + caption + a 16 spacer + a `[Retry]`
    `Button.Standard` ⇒ ≈ 160). See W12 — the estimate is shared, the measured heights are ~84 DIP apart.
13. **Home is a place you drill OUT of.** Every navigation away carries `NavOrigin("Home","home")`, so the masthead on the
    destination reads `Home › Browse › X` (`HomePage.cs:274-275`).
14. **Scroll frames allocate nothing and recycle nothing incompatible.** One `Virtual.Measured` list, overscan 1, a cheap
    recyclable row shell whose single child is keyed per row (`HomePage.cs:504-515`, `767-782`).

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
ContentHost (Features/Shell/ContentHost.cs:21)                    — KeepAlive boundary, MaxEntries 3 (:106)
├─ PublishesShellMaterial("home") == true (ContentHost.cs:184-186) — so the host does NOT claim neutral; Home owns it
├─ page:home BoxEl (ContentHost.cs:196-198)                       — Grow 1, Direction 1
│  └─ HomePage : Component (HomePage.cs:36)
│     └─ Skel.Region(home, group: HomeSkeleton.Group, reveal: StaggerRows, smoothResize:false)  (HomePage.cs:840-853)
│        ├─ [Pending]  shimmer derived from content(FakeData.HomeSeed)      (HomePage.cs:90, FakeData.cs:587-610)
│        ├─ [Empty]    StateHome(EmptyState.Default())                      (HomePage.cs:814-820, :850)
│        ├─ [Failed]   StateHome(ErrorState.Build(home.Error))              (HomePage.cs:851)
│        └─ [Ready]    feed.Facet == "" ? VirtualHome(feed) : VirtualFacet(feed)   (HomePage.cs:853)
│
├─ VirtualHome  (HomePage.cs:443-516)  — Virtual.Measured(rows.Length, homeLayout, RowAt, KeyAt, overscan:1), ScrollKey "home"
│  ├─ HomeFeedVirtualLayout (HomePage.cs:1196-1444)               — Fenwick ExtentTable, per-HomeRow Estimate
│  ├─ KeyAt(i)   = "home:row:<HomeRow>" [+ ":" + SourceGroupKey(group)]      (HomePage.cs:476-483)
│  ├─ RowAt(i)   = Responsive.Of(w => HomeRowShell(RenderRow(liveFeed, liveLanding, row), key, top, bottom))
│  │                fallback 1100 (HomePage.cs:485-502)
│  └─ OnVisibleRange → WarmGroup(group): preview prime + PrefetchImage at the module's decode px (HomePage.cs:420-441)
│
├─ HomeRowShell (HomePage.cs:767-782)   — Direction 0 / Justify Center
│  └─ BoxEl Grow1 Shrink1 Basis0 MaxWidth 1600, Padding (36, top, 36, bottom)
│     └─ child with { Key = contentKey }
│
├─ RenderRow (HomePage.cs:667-760) — the landing dispatcher, one arm per HomeRow
│  ├─ Chips        → GreetingBlock(name, feed, home, svc, post, LiveHasHero(feed), go)   (HomePage.cs:982-1018)
│  │   ├─ GreetingHero(name, greeting)   — only when !hasHero (HomePage.cs:1175-1184)
│  │   ├─ Ctx.Provide(HomeFacetChips.Props, Model(chips, RefreshForFacet)) → HomeFacetChips  (HomePage.cs:990-993)
│  │   └─ HomeCustomizeAffordance.Button(go)   (HomeCustomizerPage.cs:198-248)
│  ├─ Hero         → HomeModules.SourceModule(group, Responsive.Of(w => HomeCards.HeroBand(card, eyebrow, meta, …, w),
│  │                   fallback 900))                                     (HomePage.cs:676-685, HomeCards.cs:276-427)
│  ├─ Weekly/Quick/Recents/MixBand/ChipCards/Radio/EpisodesAndBooks/Queue/Books/Podcasts/Editorial/Feed
│  │                → HomeModules.* — chapter 11                          (HomePage.cs:686-756)
│  ├─ Artists      → Embed.Comp(() => new HomeArtistRow())                (HomePage.cs:704-705)
│  ├─ Timeline     → Embed.Comp(() => new HomeTimeline())                 (HomePage.cs:733-734)
│  ├─ Charts       → Skel.Region(charts.Loadable, content: FoldDeck(list,"Charts",…), isEmpty: 0,
│  │                   onEmpty: EmptyState.Compact, onFailed: ErrorState.Build(retry), reveal: None) (HomePage.cs:738-746)
│  ├─ Sections     → HomeModules.FoldDeck(landing.Sections, "Sections for you", OpenSection)  (HomePage.cs:747-749)
│  └─ Tail         → BoxEl{Direction 1, Gap 20}[ConcertUi.WideEditorialDestination ×2]    (HomePage.cs:388-415)
│
├─ VirtualFacet (HomePage.cs:529-589) — Virtual.Measured(rows+2, facetLayout, …), ScrollKey "home:<facet>"
│  ├─ HomeFacetVirtualLayout (HomePage.cs:1456-1591)
│  ├─ index 0 = GreetingBlock(hasHero: LiveHasHero(liveFeed)) — standalone greeting + chips WHEN the facet
│  │             carries no `HomeFacetRowKind.Hero` row; a facet that DOES carry one suppresses the greeting block
│  │             exactly like the landing (`LiveHasHero`'s facet branch, HomePage.cs:1165-1172)
│  ├─ index 1..n = RenderFacetRow(feed, HomeFacetProjection row)          (HomePage.cs:595-631)
│  └─ index n+1 = tail
│
├─ page state (instance fields, NOT statics — two tabs each keep their own):
│  ├─ HomeRevealGate<HomeFeed> _gate         (HomePage.cs:860)
│  ├─ Signal<int> _heldVersion               (HomePage.cs:864)   — bumped on every HOLD; re-arms the 1.5 s cap
│  ├─ bool _chartsArmed                      (HomePage.cs:867)   — monotonic per mount
│  ├─ CancellationTokenSource? _facetCts     (HomePage.cs:1102)
│  ├─ object _washOwner                      (HomePage.cs:1086)  — shell-material ownership token
│  └─ memoized projections: _landing/_landingFeed/_landingTitles/_landingLayout/_landingLayoutVersion (HomePage.cs:1113-1136)
│                           _facetRows/_facetFeed/_facetTitles                                        (HomePage.cs:1144-1158)
│
└─ shell material publish (HomePage.cs:212-258)
   ├─ HomeWashSource.Sources(feed) → 3 cards → WatchArtwork(PlaneUrl ×3) → Select(…, Surfaces.ChromeSchemeFor)
   ├─ UseEffect(dep = HashCode(colorWashesDisabled, HomeWashSource.Fingerprint(picks))) → ShellMaterial.Publish
   └─ UseActivation(onActivated) → Publish(isClaim:true) + the epoch compare / head probe
```

Context reads (all in `HomePage.Render`, `HomePage.cs:74-87`): `Services.Slot`, `HistoryStore.NavCtx`,
`HistoryStore.GoWithOrigin`, `HomePreferences.Slot`, `PlaybackBridge.Slot`, `NavPreviewStore.Slot`,
`HomeSectionPreviewStore.Slot`, `ActionServices.Slot`, `Overlay.Service`, `LibraryBridge.Slot`, `ShellMaterial.Slot`,
`NotificationCenterBridge.Slot` (`:135`), `Viewport.Scale` (in `HomeQuickImageProbe`, `:1615`).

### 1.2 The same tree in 0.3 terms

`Home` is a **synthetic subject**: the plan already reserves `Edges.HomeSection` for exactly this
(`wavee-0.3-implementation.md:335`). One `Home` row per (scope, facet); its sections are child slots of that row; each
section's cards are an edge list of entity slots.

| Node | 0.3 file · symbol | Input | How data reaches it |
|---|---|---|---|
| `HomePage` | `Home.Page.cs` · `Home.Page : Component` | `Home h` (handle for the current facet) | `UseSignal(Entities.Current.HomeTable.Changed)` + `UseSignal(Home.Facet)` (a `Signal<StringId>`) |
| reveal gate | `Home.cs` CORE · `Home.RevealGate` | `(epoch, sectionCount, faceted, settled, chromeConcluded, nowMs)` | page-local instance field, ported verbatim (§8) |
| landing projection | `Home.cs` CORE · `Home.Project(in HomeDoc, in Titles, in LayoutDoc)` | slot spans, not records | memoized on `(Home.Version, Titles, LayoutVersion)` — the same three-key memo as `HomePage.Landing` |
| row table | `Home.cs` CORE · `Home.Rows` (`HomeRow[]`) | projection output | value array, re-read per render (cheap) |
| `VirtualHome` | `Home.Page.cs` · static `Landing(Home h)` | `Home` handle | `Virtual.Measured(rows.Length, layout, RowAt, KeyAt, 1)` — unchanged engine call |
| `HomeFeedVirtualLayout` | `Home.Page.cs` · `sealed class LandingLayout : IMeasuredVirtualLayout` | shape fingerprint | hoisted with `UseMemo(DepKey.Empty)`, mirrored per render via `Configure(rows, counts)` |
| row shell | `Home.UI.cs` · `static Element RowShell(Element child, string key, float top, float bottom)` | values | pure |
| greeting block | `Home.UI.cs` · `static Element Greeting(StringId greeting, StringId name, bool hasHero, …)` | **`Signal`/`Func`**, not frozen strings | the name comes from `User.Me` — read inside the row's `Responsive.Of` closure so a late login re-describes it |
| facet strip | `Home.UI.cs` · `sealed class FacetChips : Component` + `Home.Slots(chips, selected)` CORE | `Ctx.Provide(FacetChips.Props, Model(chipRange, onChanged))` | props freeze at mount → the selection is read from the **signal** `Home.Facet` inside `Render` (0.2.9 does exactly this, `HomeFacetChips.cs:51`) |
| hero band | `Home.UI.cs` · `static Element HeroBand(Playlist p, string eyebrow, string meta, float width, …)` | handle + width | re-described per row render; `Key` = `uri + ":" + expiresAt` for the countdown remount (`HomeCards.cs:295`) |
| Fold tile | `Home.UI.cs` · `static Element FoldTile(HomeSectionRef s, float cardW, …)` | slot + width | width is baked into `Key` (`HomeFoldTile.cs:126`) |
| wash | `Home.cs` CORE · `Home.WashPicks(in HomeDoc, Func<StringId, Scheme?>)` | 3 slots | published from `UseEffect` keyed on the fingerprint (unchanged) |
| Charts deck | `Home.Page.cs` · `UseResource` replacement → `Browse.EnsureChartSections()` + `Known` bits | 5 browse-section slots | **see DATA GAPS** |
| timeline | `Home.UI.cs` · `sealed class Timeline : Component` | notification feed (not an entity table) | **see DATA GAPS** |

**Props freeze at mount — where 0.3 must use a Signal / Func / Key:**

* The row's build closure is a `Responsive.Of` **component**; its closure freezes. 0.2.9 keeps rows honest two ways and
  0.3 must keep both (`HomePage.cs:463-475`): (a) the closure re-reads the live feed (`home.Value.Value`) → in 0.3,
  re-read the handle's columns and `UseSignal(table.Changed)`; (b) the row KEY carries the group's content fingerprint
  (`HomeModuleLayout.SourceGroupKey`, an FNV over every card and every card's meta, memoized on the group instance,
  `HomeModules.cs:732-803`) so a changed module REMOUNTS and an unchanged one is left alone. In 0.3 the fingerprint is
  free: it is the section row's `Version` (`Table.Version`, plan §4.1) plus the edge list's `Version` — **use those, do
  not port the FNV**.
* Chrome rows (Chips / Artists / Timeline / Charts / Sections / Tail) carry the row NAME alone as their key. That is what
  lets the chip row keep one identity across the whole page family.
* `HomeFacetChips` must NOT take the selected facet as a prop. It reads `Services.HomeFacet.Value` inside `Render`.
* `FlipCountdown` inside the hero is keyed on `uri + ":" + expiresAtMs` so a daylist rollover remounts the digits.

---

## 2. Wireframes

Scale ≈ 8 DIP per character. `A` = the module row width available inside the gutters:
`A = min(contentPaneWidth, 1600) − 2×36`. A 1 440-DIP window with a 240-DIP sidebar gives a content pane ≈ 1 160 → **A ≈
1 088**. Module gap `g = 40` when the *row* width ≥ 1 080, else 32 (`HomeModules.cs:596`).

### W1 — Cold mount, skeleton (Pending) @ A ≈ 1088

The shimmer is **derived from the real tree rendered against `FakeData.HomeSeed`** — same silhouette, bars instead of
text/art (`HomePage.cs:90`, `FakeData.cs:587-610`; seed titles are a single space so every module wears a header bone).

```
├─36─┬────────────────────────────────────────────────── A = 1088 ──────────────────────────────────────────────────┬─36─┤
     │                                                                                            ▒▒▒  ← "⋯" alone  │  24 top
     ├───────────────────────────────────────────────────────────────────────────────────────────────────────────┤
     │ ▒▒▒▒▒▒▒▒ 40  (hero module header bone: Subtitle 20/28)                                                      │
     │ ┌──────────────────────────────────────────────────────────────────────────────┬──────────────────────────┐│
     │ │ ▒▒▒▒▒▒▒▒▒▒▒  eyebrow bone 16                                                 │                          ││
     │ │ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒ title bone 2×60                            │   cover bone 384×384     ││ 384
     │ │ ▒▒▒▒ ▒▒▒▒ ▒▒▒▒  tags 20                                                      │   (square = height)      ││
     │ │ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒ meta 20                                                    │                          ││
     │ │ ▒▒▒▒▒▒▒▒ 28 pulse                                                            │                          ││
     │ │ ·············· actions 32: a BLANK 32-DIP spacer, no bones ··················│                          ││
     │ └──────────────────────────────────────────────────────────────────────────────┴──────────────────────────┘│
     │                                        g = 40                                                              │
     │ ┌──────────────────────────────┐ 12 ┌──────────────────────────────┐        weekly pair 88 (2 cols > 760)   │
     │ └──────────────────────────────┘    └──────────────────────────────┘                                       │
     │ ▒▒▒▒▒▒▒▒ head 40                                                                                           │
     │ ┌────────┐┌────────┐┌────────┐┌────────┐   quick grid 4 cols (> 1120 → 4; 780-1120 → 3; ≤ 780 → 2)          │
     │ └────────┘└────────┘└────────┘└────────┘   card 56 high, row gap 12, 8 shown                               │
     │  … recents shelf 299 · mix band 142/row · chip cards 96 · radio 48/row · episodes+books · podcasts shelf …  │
```

**The cold first row is NOT a chip strip.** `FakeData.HomeSeed` passes no `Chips` (the `HomeFeed` record defaults it to
null, `HomeFeed.cs:130-137`; the seed's ctor call stops at `Sections:`, `FakeData.cs:587-610`), so `GreetingBlock`'s
`chipRow` is null; the seed DOES carry a `Hero` group, so `hasHero` is true and `GreetingHero` is null too. Both halves
gone, `GreetingBlock` falls to its third arm — a `Direction 0 / Justify End` row holding ONLY the customize "⋯"
(`HomePage.cs:1002-1008`). The cold page therefore opens with a single 28-DIP bone at the right edge above 24 of inset,
while the estimator still reserves `0 + 40 + 24 + g` = 104 for that row. Same shape on a real account whose server sent
no chips (W19).

Shimmer bars: `SkeletonStyle.Default` = `Tok.FillSubtleSecondary`, radius 4, breathe 1.0↔0.5 over 1 000 ms, text bars at
0.72 of the run width, inter-row gap 8 (`SkeletonRegion.cs:34-39`). **Two** rows shimmer a Fold-shaped deck, not one:

* **Charts** — three blank Fold tiles, each holding three blank cards (`HomeBrowseCards.ChartDeckSeed`,
  `HomeBrowseCards.cs:68-80`), because its own `UseResource` seed is Fold-shaped;
* **Sections for you** — the seed carries a 3-entry `Sections` ledger (`FakeData.cs:605-610`) of which the podcasts uri
  is consumed by `PodcastShelf`, leaving TWO tiles in `HomeLanding.Sections` (`HomeLandingProjection.SectionDirectory`).

The tail row is never realized cold. With `overscan: 1` neither Fold row is on screen at a 900-DIP window height — they
matter because they are in the extent table from the first frame, not because the user sees them shimmer.

**Nodes that are deliberately BLANK in the shimmer** (`Skeletonized(false)` ⇒ the deriver emits a same-sized empty
spacer, never a bar — `SkeletonExtensions.cs:7-10`):

* the hero's whole action row (`HomeCards.cs:356`) — 32 DIP of nothing, not four button bones;
* every card's hover-play FAB (`HomeCards.cs:213`) — a hover-only affordance is not skeleton content.

**Nodes with a bespoke shimmer**: the Fold tile substitutes ONE card-silhouette bone (`root.Skel(bone)`,
`HomeFoldTile.cs:134-141`) — `cardW × 176`, `Radii.CardAll`, `SkeletonStyle.Default.BarColor`, falling back to
`FoldCardMin = 440` before the first fit. Derived, it would lose its fill and border and the three covers would
collapse onto the corner (the deriver zeroes Offset/Rotation, and `FoldRest` IS the covers' authored pose).

### W2 — Reveal in progress @ A ≈ 1088

```
   row 0  ████████████████  opacity 1.00  dy 0    blur 0     ← delay 0 ms
   row 1  ███████████████░  opacity 0.82  dy 1.4  blur 0.5   ← delay 40 ms
   row 2  █████████████░░░  opacity 0.55  dy 3.6  blur 1.3   ← delay 80 ms
   row 3  ████████░░░░░░░░  opacity 0.21  dy 6.3  blur 2.3   ← delay 120 ms
   (shimmer orphan fades out UNDER the rising rows over 250 ms — never a blank frame)
```

Per realized row: Opacity 0→1, TranslateY 8→0, BlurSigma 3→0, 500 ms `Easing.SmoothOut`, stagger 40 ms × row index
(`SkeletonRegion.cs:169-187`, `MotionRecipes.cs:55-70`). Rows are the **realized children of the virtual viewport's
content node** (`FindVirtualRows`, `SkeletonRegion.cs:207-219`) — with overscan 1 that is ≈ 4-6 rows, not 17.

### W3 — Loaded landing, top of page @ A ≈ 1088 (Wide hero tier)

```
├─36─┬──────────────────────────────────── A = 1088 ─────────────────────────────────────┬─36─┤
     │ New release from KIMMUSEUM              ← hero module header, ModuleHeader 20/28   │ 24 top inset (Spacing.XXL)
     │ ┌──────────────────────────────────────────────────────────────────────────────┐  │
     │ │·48·                                                        ╭ cover 384 sq ─╮ │  │ radius 8, 1px StrokeCardDefault
     │ │ Good morning, Christos          ← Caption 12/16/600 +30 tracking, tertiary  │ │  │ gradient: card→accent 10% top
     │ │ 44                                                          │ EdgeFade left │ │  │
     │ │ Afternoon focus                 ← ArtistTitle 48/60/700, -20 tracking, 2 ln │ │  │ 96 DIP, veil = horizontal
     │ │ rotation                                                    │               │ │  │ 0.96→0.92→0.35→0 alpha
     │ │ ┌ focus ┐┌ indie ┐┌ chill ┐     ← Tag: Caption 12/16, 8/2 pad, 1px stroke   │ │  │ 384
     │ │ 50 songs · by Spotify           ← Body 14/20 secondary, 2 lines max         │ │  │
     │ │ 04 : 27 : 11                    ← FlipCountdown 28 (daylist only)           │ │  │
     │ │ [ ▶ Play ][ ⇄ Shuffle ][ ♡ ][ ⋯ ]  ← WaveeCta 32 high, gap 8, wrap          │ │  │
     │ │·48·                                                         ╰───────────────╯ │  │
     │ └──────────────────────────────────────────────────────────────────────────────┘  │
     │                                   g = 40                                          │
     │ Discover Weekly & Release Radar                                                    │
     │ ┌───────────────────────────────────┐ 12 ┌───────────────────────────────────┐    │ weekly 88
     │ └───────────────────────────────────┘    └───────────────────────────────────┘    │
     │                                   g = 40                                          │
     │ Jump back in            Your 8 most-opened of 14   ← ModuleHeader(title, meta)     │ head 40
     │ ┌─────────┐ 12 ┌─────────┐ 12 ┌─────────┐ 12 ┌─────────┐                          │ 56
     │ ┌─────────┐    ┌─────────┐    ┌─────────┐    ┌─────────┐   row gap 12             │ 56
```

`HeroEyebrow` = `GreetingPart(feed.Greeting)` + `", " + name` when the display name is not a handle (≥ 20 chars, no
space → suppressed, `HomePage.cs:1037`), + `" · your daylist"` only for `Meta.Format == "daylist"` (`HomePage.cs:792-800`).
Loc keys: `home.greeting` `"{part}, {name}"`, `home.heroEyebrow` `"{greeting}, {name} · {what}"`, `home.yourDaylist`
`"your daylist"`, `home.songsBy` `"{count} · by {owner}"`, `home.mostOpenedOf` `"Your {shown} most-opened of {total}"`.

**The copy column is FULL WIDTH, not a left column.** `copy.Width = max(1, width − 2 × CopyPaddingX)` — the band's whole
inner measure (`HomeCards.cs:300`), and the artwork is a ZStack SIBLING, not a flex peer. So a long title, the tag run
and the meta line all run *underneath* the square cover; the only thing separating copy from art is the veil's falloff
(0.35 alpha at 62 %, 0 at 100 %). Nothing reserves the cover's 384 DIP. That is deliberate — it is what lets a two-line
48/60 title use the full 992 rather than 608 — but it means a hero with a bright cover and a long title WILL read as
text over art at the right edge, and the `EdgeFade(Left, 96)` on the artwork is the second half of that integration.
0.3 must not "fix" this into a two-column flex row.

**Copy block vertical alignment.** Wide/Medium centre the copy column in the band (`Justify = FlexJustify.Center`);
only Narrow bottom-justifies it (`Justify = End`) (`HomeCards.cs:364`). The artwork is `AlignSelf = Center` always and
`JustifySelf = End` (Wide/Medium) / `Center` (Narrow) (`HomeCards.cs:411-413`).

**Which slots collapse.** Each hero slot renders an empty `BoxEl` when its datum is missing, while
`HomeHeroLayout.ContentHeight` reserves ALL of them unconditionally — so a hero missing tags + meta + countdown still
estimates 384 and the measured pass shrinks it to 300:

| slot | present when | collapsed shape | reserved in the estimate |
|---|---|---|---|
| tags | `Meta.Seeds.Count > 0`, max 6 rendered | `new BoxEl()` | 32 (`TagsBlock`) |
| meta line | `CardMeta(c).Length > 0` (track count and/or owner) | `new BoxEl()` | 36 (`MetaBlock`) |
| countdown | `Meta.ExpiresAtMs > 0` | `new BoxEl()` | 40 (`PulseBlock`) |
| "⋯" CTA | `menu is not null` | `new BoxEl()` | inside the 32 action row |

(`HomeCards.cs:289-296, 318-356`; `HomeHeroLayout.cs:36-45`.)

**Long text.** The title is `MaxLines = 2` + `CharacterEllipsis`, and the type itself auto-shrinks before it clips:
`ArtistTitle` has `MinSize = 40`, `ArtistCompactTitle` `MinSize = 28` (`WaveeType.cs:167-188`). The eyebrow is one line
+ ellipsis; the meta line is two lines + ellipsis.

### W3b — Hero, the full-bleed photo arm @ A ≈ 1088

When the payload carries `header_image_url_desktop` the surface is a DIFFERENT object: no square cover, no
`HomeHeroBackdrop` ground — one `ImageFit.Cover` photo filling `A × height`, the veil over it, then the copy
(`HomeCards.cs:382-404`).

```
├─36─┬──────────────────────────────────── A = 1088 ─────────────────────────────────────┬─36─┤
     │ ┌──────────────────────────────────────────────────────────────────────────────┐  │
     │ │▓▓▓▓▓▓▓▓▓▓▓ full-bleed photo, aspect = A / height, decodePx = clamp(A,320,1920)│  │ radius 8, 1px stroke
     │ │ Good morning, Christos            (the whole plate is the click target →Nav)  │  │ EdgeFade(Bottom, 96)
     │ │ Afternoon focus rotation                                                      │  │ veil axis STILL horizontal
     │ │ [ ▶ Play ][ ⇄ Shuffle ][ ♡ ][ ⋯ ]                                             │  │ at Wide/Medium
     │ └──────────────────────────────────────────────────────────────────────────────┘  │ image fade-in 300 ms
```

### W4 — Loaded landing, mid-scroll (chrome rows) @ A ≈ 1088

There is **no sticky header and no compact header** on Home: `ShellMastheadBand` does not claim the `home` route
(`NavOrigin.cs:82-83`), so it holds its last trail at `Opacity 0`, `HitTestVisible false` (`ShellMastheadBand.cs:57,70-71`).
The shell wash does not move.

```
     │ Your top artists                                                        ← HomeArtistRow (ch. 11)            │
     │        ╭──╮  ╭────╮  ╭──╮                                                                                   │ 204 + g
     │        ╰──╯  ╰────╯  ╰──╯   podium: 2×16 pad + 2×8 + 76 avatar + 8 + 2×16 label                             │
     │                                   g                                                                        │
     │ Podcasts for you                                                    ‹ ›  ← PagedShelf chevrons on the head  │ 299 + g
     │ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐                                                │
     │                                   g                                                                        │
     │ New releases        2 unheard of 9                                       ← HomeTimeline (hidden when empty) │ 488 + g
     │      Today │ ● ▒▒▒▒▒▒▒▒▒▒▒▒                                                                                 │
     │            │ ● ▒▒▒▒▒▒▒▒▒▒                                                                                   │
     │                                   g                                                                        │
     │ Charts                                                              ‹ ›                                     │ 232 + g
     │ ┌──────────────────────────── 538 ────────────┐ 12 ┌──────────────── 538 ─────────────┐                     │
     │ │                          ╱▓▓▓▓╲ ╱▓▓▓▓╲╱▓▓▓╲│    │                     ╱▓▓╲╱▓▓╲╱▓▓╲ │  Fold tile 176 high │
     │ │ Featured Charts          ╲▓▓▓▓╱ ╲▓▓▓▓╱╲▓▓▓╱│    │ Weekly Song Charts  ╲▓▓╱╲▓▓╱╲▓▓╱ │  covers 124, rot     │
     │ └────────────────────────────────────────────┘    └──────────────────────────────────┘  -11° / +5° / -2°    │
     │                                   g                                                                        │
     │ Sections for you                                                    ‹ ›                                     │ 232 + g
     │ ┌───────── Fold ─────────────────────────────┐ 12 ┌────────── Fold ──────────────────┐                      │
```

Fold deck fit (`HomeModules.cs:363-374` + `VirtualLayout.cs:484-501`): `cols = clamp(floor((A+12)/452), 1, 2)`,
`cardW = (A − (cols−1)·12)/cols`. A = 1088 → 2 × 538. Two-up appears at A ≥ 892; below that one tile fills the row.

### W5 — The tail @ A ≈ 1088

```
     │ ┌──────────────────────────────────────────────────────────────────────────────┐  │
     │ │ Live music                                                    ╭────────────╮ │  │ ConcertLayout.WideEditorial
     │ │ Concerts near you                                             │  art 0.38  │ │  │ A ≥ 900 → 288 high, pad 28
     │ │ Tour dates, tickets, and shows from artists you love.         │  min 280   │ │  │ subtitle 3 lines
     │ │ [ Explore concerts ]                                          ╰────────────╯ │  │
     │ └──────────────────────────────────────────────────────────────────────────────┘  │
     │                                   20  (Spacing.XL — the tail's internal gap)      │
     │ ┌──────────────────────────────────────────────────────────────────────────────┐  │
     │ │ Browse all / Browse / Genres, moods and charts … / [ Explore all categories ] │  │ 288
     │ └──────────────────────────────────────────────────────────────────────────────┘  │
     │                                   96  (PlayerDock.Reserve 72 + Spacing.XXL 24)    │
     ╞══════════════════════════ player dock 72 ═══════════════════════════════════════╡
```

### W6 — Medium tier @ A ≈ 820 (window ≈ 1 160)

Crossings between W3 and W6: hero Wide→Medium at **A < 980** (`HomeHeroLayout.cs:50`); module gap 40→32 at row width
< 1 080; episodes+books side-by-side→stacked at A < 1 020 (`HomeModuleLayout.SplitEvenMin`); editorial two-column→stacked
at A < 980; quick grid 4→3 cols at A ≤ 1 120; Fold 2→1 tile at A < 892. **No hysteresis on any of these** — they are pure
width switches evaluated inside `Responsive.Of`.

```
├─36─┬───────────────────────── A = 820 ─────────────────────────┬─36─┤
     │ ┌──────────────────────────────────────────────────────┐  │
     │ │ Good morning, Christos              ╭ cover 344 sq ─╮ │  │ 344  (Medium: title ArtistCompactTitle 32/40/700)
     │ │ Afternoon focus rotation            │               │ │  │
     │ │ [ ▶ Play ][ ⇄ Shuffle ][ ♡ ][ ⋯ ]   ╰───────────────╯ │  │
     │ └──────────────────────────────────────────────────────┘  │
     │                        g = 32                             │
     │ ┌───────── Up next (episodes) ─────────────────────────┐  │  split STACKED (A < 1020)
     │ └──────────────────────────────────────────────────────┘  │
     │                        g = 32                             │
     │ ┌───────── Audiobooks for you ─────────────────────────┐  │
     │ └──────────────────────────────────────────────────────┘  │
     │ ┌───────────── ONE Fold tile, cardW = 820 ─────────────┐  │  (A < 892)
```

### W7 — Narrow tier @ A ≈ 640 (window ≈ 940)

Hero Narrow (A < 700): the band **stacks** — copy is bottom-justified over a centred square cover, the veil axis flips to
vertical, and the artwork carries **no** edge fade (`HomeCards.cs:364, 370-377, 414`).

```
├─36─┬────────────── A = 640 ──────────────┬─36─┤
     │ ┌────────────────────────────────┐  │
     │ │        ╭─ cover 336 sq ─╮      │  │ 336, JustifySelf Center
     │ │        ╰────────────────╯      │  │ veil vertical: 0 → .35 @45% → .78 @82% → 0 (dark)
     │ │ Good morning, Christos         │  │ copy Justify = End
     │ │ Afternoon focus rotation       │  │ PageHero 28/36/600, 2 lines
     │ │ [ ▶ Play ][ ⇄ ][ ♡ ][ ⋯ ]      │  │
     │ └────────────────────────────────┘  │
     │ quick grid 2 cols (A ≤ 780)         │
     │ mix band 3 cols (620 < A ≤ 1080)    │
     │ chip cards 1 col (A ≤ 680)          │
     │ radio 1 col (RadioColMin = 252)     │
```

### W8 — Facet page ("Podcasts") @ A ≈ 1088

```
     │ good morning                        ← GreetingHero when this facet has no Hero row (see below)  84
     │ Here's what's been on rotation.                                                              │ 12 gap
     │ All  [Music]  Podcasts  Following   ← the SAME chip row node, key "home:row:Chips"           │ 40
     │ ─────────────────────────────────────────────────────────────────── 1px divider             │
     │                                   g                                                          │
     │ Your shows                                                          ‹ ›   ← server's OWN title│ 299 + g
     │ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐                                  │
     │ Podcasts for you                                                    ‹ ›   ← a SECOND shelf,   │ 299 + g
     │ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐             never merged          │
     │ Because you listened                                                ‹ ›   ← the coalesced run │ 299 + g
     │ ┌───────┐ ┌───────┐ ┌───────┐ …                                          (app copy, no drill) │
     │ [ tail ]                                                                                      │
```

Row 0 estimate on a facet page is unconditional `84 + 40 + 24 + g` (`HomePage.cs:1526`).

**Correction to the estimator's own comment.** `HomePage.cs:1524-1525` claims "a facet page never has a hero band", but
`HomeFacetProjection` DOES emit `HomeFacetRowKind.Hero` for a one-card spotlight section (`HomeFacetProjection.cs:74-78`)
and `LiveHasHero` answers `true` for exactly that case (`HomePage.cs:1165-1172`) — so the greeting block collapses to
the chip strip alone while the estimator still reserves 84. The measured pass corrects it; **do not port the comment**.
In 0.3, make row 0's estimate read the same `HasHero(facet)` predicate the renderer does.

Other facet-page states the landing does not have:

* **A facet with no rows at all** (the server answered nothing, or every section was empty): `count = 0 + 2` — the chip
  row and the tail, and nothing between. `Skel.Region`'s `isEmpty` guard is facet-agnostic (`feed.Facet.Length == 0 &&
  …`, `HomePage.cs:849`), so a faceted empty document renders the ORDERED-DOCUMENT arm, never `StateHome` —
  deliberately (`HomeRevealGate.Offer`'s "a faceted read always passes", `HomeFeedReadiness.cs:138-139`).
* **A section with zero unique cards** never becomes a row at all (`HomeFacetProjection.cs:65-67`), and a row whose
  metric count is 0 estimates exactly `0` (`HomePage.cs:1534`).
* **A run of consecutive baseline sections** coalesces into one `Because you listened` shelf; a titled section between
  two runs closes the first, so two runs stay two rows (`HomeFacetProjection.cs:41-72`).

### W9 — Facet strip states (detail) @ any width

```
 (a) unfiltered                     (b) "Music" selected                  (c) "Music · Following" fused
 ┌──────────────────────────┐       ┌────────────────────────────────┐    ┌─────────────────────────────────┐
 │ All   Music   Podcasts   │       │ All  Music  Following  Podcasts│    │ All  (✓ Music│Following ✕) Pod… │
 │ ▂▂▂▂                     │       │      ▂▂▂▂▂                     │    │      ▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂          │
 └──────────────────────────┘       └────────────────────────────────┘    └─────────────────────────────────┘
   12│label│12  gap 4               sub-chip: Caption 12/16 tertiary,      pill: h 28, pad L3 T3 R10 B3,
   tab pad L12 T8 R12 B8            pad L8 T4 R8 B4, radius 4             segment h 22 pad 9/0, radius 11,
   underline 3 high, inset 12,      spilled INSIDE the parent's group      segment fill AccentDefault +
   corners (2,2,0,0)                node, never appended after the last    Elevation.Card, ink OnAccent,
   selected = Tok.AccentDefault     tab                                    value ink TextPrimary, ✕ 9 DIP
   1px Tok.StrokeDividerDefault under the whole strip (HomeFacetChips.cs:114)
```

Strip details the wireframe cannot show:

* The item row is `Wrap = true`, `Gap = Spacing.XS`, `MinWidth = 0`, and every tab/sub-chip is `Shrink = 0`
  (`HomeFacetChips.cs:111, 126, 187`) — a strip too long for the row **wraps onto a second line**; it never compresses.
* Every label is `MaxLines = 1` + `TextTrim.CharacterEllipsis` (`:146, 195`) — a long server chip ellipsises inside its
  own tab rather than pushing the strip.
* The underline slot is **always present** (`Underline(false)` paints `ColorF.Transparent`, `:171-177`), so selecting a
  tab cannot change the strip's height by a pixel.
* The tab shell carries top-only corners `(Radii.Control, Radii.Control, 0, 0)` = `(4, 4, 0, 0)`; the sub-chip is
  `Radii.ControlAll` = 4 on all four (`:127, 188`).
* The fused pill carries a **1px `Tok.StrokeControlDefault` border** and a `✓` check glyph at 11 DIP before the parent
  name (`ConcertUi.cs:1164-1173`). The outer capsule is `Radii.Full`; the segment's corners are `SegmentHeight/2 = 11`.
* `model.Chips.Count == 0` (or a null `Services`) ⇒ the component returns a bare `BoxEl` — **no strip, no divider**
  (`HomeFacetChips.cs:49`). The greeting row still renders; the estimate's `+40` is then wrong by 40 until the measured
  pass lands.
* `Wavee.Tests` pins the ORDER, not the pixels: `HomeFacetStripTests` (5 facts over `Resolve`/`Slots`).

### W10 — Empty feed (`Groups.Count == 0` AND the live-catalog attempt concluded) @ A ≈ 1088

`StateHome` is a plain `ScrollView`, not the virtual list, and reads the same gutter/first-row inset
(`HomePage.cs:814-820`): `Padding (36, 24, 36, 96)`, `Gap = Spacing.XL (20)`, children `[GreetingBlock(hasHero:false),
state, tail]`, `ScrollKey "home"` (facet-agnostic).

```
     │ good evening                                             ← GreetingHero (feed null ⇒ clock greeting)
     │ Here's what's been on rotation.
     │                            20
     │                     Nothing here yet                     ← EmptyState.Default: PageHero 28/36 centred
     │        When there's something to show, it'll appear here. ← Caption 12/16 secondary
     │                            20
     │ [ Concerts near you ]  [ Browse ]                        ← the tail still closes the page
```

The state block itself is `EmptyState.Centered`: `Direction 1`, `Grow 1`, `AlignItems Center`, `Justify Center`,
`Gap = Spacing.XS (4)`, `Padding = Edges4.All(Spacing.XXL) = 24` (`EmptyState.cs:68-72`). Copy is `common.emptyTitle`
"Nothing here yet" / `common.emptySubtitle` "When there's something to show, it'll appear here." — `EmptyState.Default()`
(`EmptyState.cs:66`). **No glyph and no action**: the empty-state grammar dropped the pictogram outright
(`EmptyState.cs:24-30`), and `Default()` passes no action label. With `feed = null` the greeting block renders its
standalone form with **no chip strip** (chips come off the feed) and `hasHero: false` (`HomePage.cs:819`).

### W11 — Failed initial load @ A ≈ 1088

Identical frame, `ErrorState.Build(home.Error)` in the middle: "Something went wrong." / "Check your connection and try
again." with **no retry button** (the page's own 60 s loop is the retry) — `HomePage.cs:851`, `ErrorState.cs:17-27`.
A failure only reaches this branch under TWO conditions at once (`HomePage.cs:964-970`): the read was the **first of a
refresh-loop generation** (`failIfInitial: true`, passed only at `HomePage.cs:943`), AND the loadable is not already
`Ready`. The loop restarts on every feed-epoch bump and every `AuthState` flip (`HomePage.cs:176-184`), so "initial" is
per generation, not per mount — a page still on its skeleton when a re-armed loop's first read throws WILL paint this
state. Every ordinary 60 s tick failure is swallowed and the page keeps what it has.

### W12 — Charts row, three states @ A ≈ 1088

```
 Pending            Charts                                            ‹ ›
                    ┌── ▒▒▒ Fold bone 176 ──┐ 12 ┌── ▒▒▒ Fold bone ──┐        est 232 + g, measured 232
 Ready-empty        Charts                                            ‹ ›
                    │            No charts right now                  │        est 152 + g, measured ≈ 108
                                 Ui.Subtitle 20/28, no caption, no action      (EmptyState.Compact)
 Failed             Charts                                            ‹ ›
                    │           Something went wrong.                 │        est 152 + g, measured ≈ 192
                    │   Check your connection and try again.          │        (EmptyState.Build — PAGE scale)
                    │              [ Retry ]                          │
```

**The two settled arms are NOT the same block.** `onEmpty` is `EmptyState.Compact` (`HomePage.cs:744`): a lone
`Ui.Subtitle` 20/28 headline inside `Edges4.All(24)` ⇒ 28 + 48 = **76** of content. `onFailed` is
`ErrorState.Build(err, onRetry: charts.Refresh)` (`HomePage.cs:745`), and `ErrorState.Build` calls
`EmptyState.**Build**` — the PAGE-scale grammar (`ErrorState.cs:23-27`): `WaveeType.PageHero` 36 + gap 4 +
`TrackMeta` 16 + gap 4 + a `Spacing.L` 16 spacer + gap 4 + a 32-DIP `Button.Standard` + 48 padding ⇒ ≈ **160**.
`FoldStateExtent = 32 + 96 + 24 = 152` (`HomeModules.cs:585`) is one ESTIMATE covering both, sized between them; the
measured pass then lands the two rows ~84 DIP apart. Do not port "the row height is identical either way" — port
"the row is never 0 and never flaps the anchor", which is the actual invariant the constant buys.

`reveal: SkelReveal.None` and `smoothResize: false` on this region (`HomePage.cs:746`): the Fold deck owns its own
entrance, and the shimmer's exit is floored at 400 ms (`Expressive.Slow`) so it dissolves under it
(`SkeletonRegion.cs:20-24`).

**Which of the three states `--fake` actually produces — Failed, not Ready-empty.** `NullBrowseService.GetSectionAsync`
returns `null` for every uri (`Wavee.Core/Sources/IBrowseService.cs:41-42`), so `pages[0]` is null; `hasLiveCatalog` is
`LiveCatalogAvailable()` = `AuthState == Live` (`HomePage.cs:105`), and `FakeSpotifySession` reports
`AuthStatus.Authenticated` (`Wavee.Core/Fakes/FakeSpotifySession.cs:22`) which `ProjectAuthState` folds straight to
`Live` (`App/PlaybackBridge.cs:288`). So `LoadChartDeckAsync` **throws** (`HomeBrowseCards.cs:46-50`) and the row paints
the Failed arm with a working `[Retry]`. Both arms share the `FoldStateExtent = 152` ESTIMATE, but they render at
different scales and different heights (above) — so a side-by-side must compare the arm 0.2.9 actually produces, not the
other one's copy. The `Ready`-empty arm ("No charts right now", `home.chartsEmpty`) is what an offline or
still-connecting real session produces, where `hasLiveCatalog` is false and the deck degrades instead of failing loud.

**`[Retry]` does not re-skeletonize the page.** `charts.Refresh` flips only THIS resource back to Pending; the region's
`reveal: SkelReveal.None` means the Fold shimmer returns for this one row with no stagger and no page-level reveal
(`HomePage.cs:746`), and `_chartsArmed` is already true so nothing re-arms. The row's estimate flips 152 → 232 the same
frame (`SetChartsState`, `HomePage.cs:453`), which is the one anchor move `[Retry]` is allowed to make.

### W13 — Hover / pressed / focus

```
 Fold tile at rest                      Fold tile hovered                     Fold tile focused
 ┌───────────────────╱▓▓╲╱▓▓╲╱▓▓╲─┐     ┌──────────────╱▓▓╲ ╱▓▓╲  ╱▓▓╲──┐     ┌────────────────────────────┐
 │ Featured Charts   ╲▓▓╱╲▓▓╱╲▓▓╱ │     │ Featured Ch.  ╲▓▓╱ ╲▓▓╱  ╲▓▓╱ │     │ 2px accent focus ring      │
 └────────────────────────────────┘     └───────────────────────────────┘     └────────────────────────────┘
  Fill FillCardDefault                    Fill FillCardSecondary                Role Hyperlink, Focusable
  covers at FoldRest                      covers += (−10,+6,−5°)/(+2,−6,+3°)/   (engine focus visual — ch. 00)
  Shadow Elevation.Card (unchanged)        (+10,0,+3°) over 250 ms FluentStandard
  NO elevation lift, NO scale — the fill swap plus the fan IS the hover state (HomeFoldTile.cs:19-23)

 Facet tab hovered: Interaction.Subtle plate under the label box; ink stays TextSecondary until selected.
 Hero: the band itself has no hover state; its four CTAs carry WaveeCta's own ramps (ch. 00).
 Customize "⋯": 28×28, Interaction.Subtle, tooltip "Customize Home".

 Fold tile with FEWER than three covers: coverCount = min(3, cards.Count) and a missing slot is OMITTED,
 never padded with a repeated cover (HomeFoldTile.cs:59-60). One card ⇒ one cover at rest pose 0.
 Fold tile with a blank section title: falls back to Loc home.sections "Sections for you" (HomeFoldTile.cs:88).
 Fold tile whose first card carries Accent == 0: the radial wash layer is not emitted AT ALL — a flat card
 plate, not a grey wash (HomeFoldTile.cs:32-35).
 Fold tile copy: HitTestVisible = false on the copy column AND on every cover, so the ROOT owns the one
 hyperlink hit-test (HomeFoldTile.cs:80, 103-104).
 Fold covers after a KeepAlive park: SnapAuthoredPose puts Offset/Rotation back at FoldRest, so hover is
 never what leaves the stack fanned (HomeFoldTile.cs:78-79).
```

**Where a click actually lands on the hero.** `OnClick = onNav` sits on the ARTWORK box only (Wide/Medium/Narrow), or
on the full-bleed photo in the header-image arm (`HomeCards.cs:413, 389`). Neither the `surface` ZStack nor the
`foreground` copy column carries a handler — **clicking the copy area of the band does nothing**. Right-click anywhere
on the band hits `surface.WithMenu(menu)` (`HomeCards.cs:426`), which is the whole plate.

### W14 — Now-playing @ A ≈ 1088

Home marks the *sounding context* on cards, never on the hero: `HomeNowPlaying.Mark(uri, 12)` collapses to zero width
unless the card is the playing context (`HomeCards.cs:93-97, 506`). The equalizer bars are chapter 01's.

```
     │ ┌─────────┬────────────────────────── ▮▯▮  ▶ ┐   quick tile: mark BEFORE the hover-play FAB
```

Three states, not two (`HomeCards.cs:1116-1126`): **not the sounding context** ⇒ `BoxEl { Width 0, Height 0 }` (no gap
either); **the context, playing** ⇒ animated bars; **the context, PAUSED** ⇒ the same mark, bars held. The predicate is
`bridge.HasActiveContext` + `NowPlayingMatch.RelatesToPlaying(uri, ContextUri, Track)`, i.e. a live signal, never a
fetch. Sizes: 12 on the quick tile (`HomeCards.cs:506`), 11 on every `Titled(...)` row (`HomeCards.cs:93`).

### W15 — Drag in progress

```
     ┌────────┐
     │  ▒▒▒▒  │ ← the drag chip (ch. 01) built from WaveeResourceDragPayload.ForEntity(kind, uri, title, image)
     └────────┘   Track and Episode cards are deliberately NOT draggable (HomePage.cs:294-298)
     drop targets: sidebar playlist (add), sidebar folder (file), pin band (pin)
```

### W16 — The customize flyout @ A ≈ 1088

```
     │ good morning                                                              [⋯]  ← 28×28 at the row's END
     │ Here's what's been on rotation.                                            │
     │                                                                    ┌───────┴────────────┐
     │ All   Music   Podcasts                                             │ ✎  Customize Home  │ min width 200
     │ ────────────────────────────────────────────────────────────────── └────────────────────┘
     │                                          FlyoutPlacement.BottomEdgeAlignedRight, light dismiss, focus trap
```

With no overlay service the button navigates straight to `home-customize` (`HomeCustomizerPage.cs:216-221`).

### W17 — Card context menu open

Right-click on any card (and the hero's "⋯", which re-enters the context funnel rather than owning a handler —
`HomeCards.cs:350-354`) opens `Menus.CardAttach(acts, overlay, uri, title, image, plainSubtitle, circular: kind==Artist)`
(`HomePage.cs:307-311`) — items, order and icons are chapter 01's `Menus.Card`.

### W18 — The wash geometry (window-relative, NOT page-relative)

```
 ┌─ window ───────────────────────────────────────────────────────────────┐
 │●hero centre (0.06, 0.00)                        weekly centre (0.92,0.10)●│
 │ ░░░░░░░░░░░░                                          ░░░░░░░░░░░░░░░░░ │  hero box  51.9% × 57.0%, top-left
 │ ░░░░░░░░░░                                              ░░░░░░░░░░░░░░  │  weekly    45.1% × 59.9%, top-RIGHT
 │  ░░░░░░                                                    ░░░░░░░░░    │  mix      100.0% × 46.2%, BOTTOM
 │                                                                        │
 │                    ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░                       │  alphas at the origin stop:
 │                 ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░                    │   hero  .10 dark / .055 light
 │                          ●mix centre (0.58, 1.00)                      │   shelf  .085 dark / .05 light
 ╞══════════════ player dock 72 — the wash HOST is inset by this ════════╡  (Mix is translated up and clipped)
 └────────────────────────────────────────────────────────────────────────┘
```

### W19 — The chips row's four arms (two of them degenerate) @ A ≈ 1088

`GreetingBlock` composes from three independently-optional parts — the greeting hero (`!hasHero`), the chip strip
(`feed?.Chips is { Count: > 0 }` **and** a non-null `Services`) and the customize "⋯" (`go is not null`) — and collapses
through four arms (`HomePage.cs:982-1018`). Every one of these is a live state, not a theoretical one: the `--fake` cold
seed hits (c), a server that sends no `homeChips[]` hits (c), and `HomeFacetChips` itself returns a bare `BoxEl` for a
zero-chip model (`HomeFacetChips.cs:49`) so (b) degrades into (c) one level down.

```
 (a) hero + chips              (b) no hero + chips             (c) hero, NO chips            (d) no `go`
 ┌──────────────────────┐      ┌──────────────────────┐        ┌──────────────────────┐      ┌────────────────┐
 │ All  Music  Pod… [⋯] │      │ good morning     [⋯] │        │                  [⋯] │      │  (empty BoxEl) │
 │ ──────────────────── │      │ Here's what's…       │        └──────────────────────┘      └────────────────┘
 └──────────────────────┘      │ 12                   │         Direction 0, Justify End,     body ?? new BoxEl()
  row: Dir 0, AlignItems Start,│ All  Music  Podcasts │         AlignItems Center — the        (HomePage.cs:1002)
  Gap 8; body Grow1 Basis0     │ ──────────────────── │         "⋯" is the ONLY child
  (HomePage.cs:1009-1017)      └──────────────────────┘         (HomePage.cs:1003-1008)
                                stack gap 12 (Spacing.M)
```

In (c) the rendered row is ≈ 28 + 24 inset + g while the estimator still says `0 + 40 + 24 + g` = 104 — the §9 defect #5
over-report, 40 DIP, for exactly one frame. In (b) the estimate `84 + 40 + 24 + g` = 188 is right.

---

## 3. Tokens

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| page row shell | MaxWidth 1600, centred | `Edges4(36, top, 36, bottom)` | — | — | none | none | `HomePage.cs:767-782`, `WaveeTokens.cs:75`, `Spacing.cs:22` |
| first row top inset | 24 | `Spacing.XXL` | — | — | — | — | `HomePage.cs:499` |
| last row bottom | 96 | `PlayerDock.Reserve 72 + Spacing.XXL 24` | — | — | — | — | `HomePage.cs:489` |
| module gap | 40 / 32 | `WaveeSize.SectionGapWide/SectionGap` at ≥1080 | — | — | — | — | `HomeModules.cs:496-497, 596`, `WaveeTokens.cs:70` |
| module head | 28 + 12 = 40 | `HomeModuleLayout.HeadGap = Spacing.M` | — | `WaveeType.ModuleHeader` 20/28/600 display face, −6 tracking | `Tok.TextPrimary`; meta run `Tok.TextTertiary` 12/16 | — | `HomePage.cs:1314`, `HomeModules.cs:499`, `WaveeType.cs:63-99` |
| head chevron | 12 | gap 4 before it | — | — | `Tok.TextTertiary` | — | `HomeModules.cs:70` |
| greeting hero (fallback) | — | `Edges4(0, 8, 0, 0)`, gap 4 | — | `WaveeType.PageHero` 28/36/600 + `TrackMeta` 12/16 | primary / secondary | — | `HomePage.cs:1179-1183` |
| greeting+chips stack | — | gap 12 (`Spacing.M`) | — | — | — | — | `HomePage.cs:996-1000` |
| greeting row ↔ ⋯ | — | gap 8, AlignItems Start | — | — | — | — | `HomePage.cs:1009-1017` |
| customize ⋯ | 28 × 28 | — | 4 | icon 14 | `Tok.TextSecondary` | `Interaction.Subtle` | `HomeCustomizerPage.cs:237-246` |
| facet tab | auto | `Edges4(12, 8, 12, 8)` | top 4/4 | `Ui.BodyStrong` 14/20/600 | `Tok.TextSecondary` → `Tok.TextPrimary` | `Interaction.Subtle` | `HomeFacetChips.cs:124-152` |
| facet underline | height 3 | margin L/R 12 | (2,2,0,0) | — | `Tok.AccentDefault` / transparent | brush transition 150 ms | `HomeFacetChips.cs:171-177` |
| facet strip divider | height 1 | — | — | — | `Tok.StrokeDividerDefault` | — | `HomeFacetChips.cs:114` |
| facet item gap | 4 (`Spacing.XS`), wrap | group gap 4 | — | — | — | — | `HomeFacetChips.cs:65, 111` |
| sub-chip | auto | `Edges4(8, 4, 8, 4)` | 4 | `Ui.Caption` 12/16 | `Tok.TextTertiary` | `Interaction.Subtle` | `HomeFacetChips.cs:184-202` |
| fused pill | h 28, segment 22 | `Edges4(3,3,10,3)` / seg `Edges4(9,0,9,0)`, gaps 6 / 4 | Full (999) / seg 11 | 14/600 | fill `FillControlDefault`→`Secondary`→`Tertiary`, **1px `Tok.StrokeControlDefault`**; seg `AccentDefault` on `Tok.OnAccent`; value `TextPrimary`; ✕ (`Icons.Cancel`) 9 `TextSecondary`; ✓ (`Icons.Check`) 11 | `Elevation.Card` on the segment | `ConcertUi.cs:637-671, 1160-1173` |
| quick-tile hover FAB | 28 circle | — | `Radii.Circle(28)` | glyph `Icons.Play` 13 | `Tok.AccentDefault` on `Tok.TextOnAccentPrimary` | `Opacity 0→HoverOpacity 1`, `HoverScale 1.07`, `Skeletonized(false)` | `HomeCards.cs:200-213` |
| now-playing mark | 12 (quick) / 11 (rows) | — | — | — | ch. 01's equalizer | collapses to 0×0 when not the context | `HomeCards.cs:1101-1126, 506, 93` |
| empty/failed state block | — | `Edges4.All(24)`, gap 4, `Grow 1`, centred both axes | — | `PageHero` 28/36 + `TrackMeta` 12/16 | primary / secondary | no glyph, no action | `EmptyState.cs:66-72` |
| …its action arm (Charts `[Retry]` only) | button 32 | a `Spacing.L` (16) **spacer box** above the button, then the stack's own gap 4 | pill | `Button.Standard`, **never** `Button.Accent` | — | the accent-budget rule, made structural | `EmptyState.cs:58-63` |
| hero copy column | `max(1, A − 2×48)` — the FULL inner measure, not a left column | gap 0; every slot spaces itself with its own bottom `Margin` | — | — | — | a ZStack sibling of the artwork, so copy runs UNDER the cover | `HomeCards.cs:298-300` |
| hero surface | width A × 336/344/384 | copy pad 48 × 44 | 8 (`Radii.Card`) | — | `Gradient = Surfaces.HomeHeroBackdrop(accent)`; border 1px `Tok.StrokeCardDefault` | ZStack, ClipToBounds | `HomeCards.cs:398-425`, `HomeHeroLayout.cs:24-25` |
| hero eyebrow | 16 + 8 margin | — | — | `WaveeType.Eyebrow` 12/16/600, +30 tracking, **sentence case** | `Tok.TextTertiary` | — | `HomeCards.cs:308-312`, `WaveeType.cs:54` |
| hero title | 2 × 60 / 2 × 40 / 2 × 36 + 12 margin | — | — | `ArtistTitle` 48/60/700 −20 / `ArtistCompactTitle` 32/40/700 −12 / `PageHero` 28/36/600 | `Tok.TextPrimary` | — | `HomeCards.cs:281-286, 313-317`, `HomeHeroLayout.cs:32-35` |
| hero tag | 20 high (12/16 + 2×2) + 12 margin | `Edges4(8,2,8,2)`, gap 4, max 6 | 4 | `Ui.Caption` 12/16 | 1px `Tok.StrokeControlDefault`, no fill | — | `HomeCards.cs:171-177, 318-325` |
| hero meta | 20 + 16 margin | — | — | `Ui.Body` 14/20, 2 lines | `Tok.TextSecondary` | — | `HomeCards.cs:326-334` |
| hero pulse | 28 + 12 margin | — | — | `FlipCountdown` digits | accent, contrast-graded to `TextInk` | — | `HomeCards.cs:289-296`, `FlipCountdown.cs:42` |
| hero actions | 32 | gap 8, wrap | pill | — | `WaveeCta.Accent(accent)` + `Pill` + 2 × `Icon` | — | `HomeCards.cs:341-356` |
| hero artwork | height × height | — | 0 (clipped by the surface) | — | `Surfaces.Artwork`, decode 512 | `EdgeFade(Left, 96)`, none when stacked | `HomeCards.cs:408-416`, `HomeHeroLayout.cs:26` |
| Fold tile | cardW × 176 | copy `Edges4(20,20,12,18)`, gap 4, copy MaxWidth 0.70·cardW | 8 | `WaveeType.FoldTitle` 28/36/400 display −12, 3 lines | fill `Tok.FillCardDefault` → hover `Tok.FillCardSecondary`; border 1px `Tok.StrokeCardDefault` | `Elevation.Card`, ClipToBounds | `HomeFoldTile.cs:105-128`, `HomeModules.cs:540-544` |
| Fold covers | 124 × 124 × 3 | rest x = max(0, cardW−210) + {0,44,92}; y = {38,22,8} | 4 | — | `Surfaces.Artwork`, decode 128 | `Elevation.Card`, rot −11°/+5°/−2° | `HomeFoldTile.cs:59-86`, `HomeModules.cs:556-568` |
| Fold wash | cardW × 176 | — | — | — | radial `accent@0.22 → accent@0` at 0.72, centre (1.0, 0.48), radius (0.70, 1.10) | HitTestVisible false | `HomeFoldTile.cs:36-53` |
| Fold deck | rows 1, maxColumns 2 | gap 12, minCardW 440, maxCardW 9999, edgeFade 24 | — | header = `ModuleHeader(title)` | — | lift 12 + shadow 12 clearance | `HomeModules.cs:363-374`, `HomeModuleLayout.cs`→`HomeModules.cs:549-552, 582` |
| Charts empty | ≈ 76 rendered (76 = 28 + 2×24); 96 nominal in the constant | `Edges4.All(24)` | — | `EmptyState.Compact` = `Ui.Subtitle` 20/28, **no caption, no action** | `Tok.TextPrimary` | — | `HomePage.cs:744`, `EmptyState.cs:43-45` |
| Charts failed | ≈ 160 rendered | `Edges4.All(24)`, gap 4, + a 16 spacer above `[Retry]` | — | `EmptyState.Build` = `PageHero` 28/36 + `TrackMeta` 12/16 + `Button.Standard` | primary / secondary | — | `HomePage.cs:745`, `ErrorState.cs:17-27` |
| empty/failed page | — | `Edges4(36, 24, 36, 96)`, gap 20 | — | `EmptyState.Build` = PageHero | — | — | `HomePage.cs:814-820` |
| tail | 2 × 288 (A ≥ 900) | inner gap 20 (`Spacing.XL`) | 8 | ConcertUi editorial | — | — | `HomePage.cs:388-415`, `ConcertLayout.cs:30-38` |
| skeleton bar | — | row gap 8 | 4 | text bars at 0.72 width | `Tok.FillSubtleSecondary` | breathe 1.0↔0.5 / 1 000 ms | `SkeletonRegion.cs:34-39` |

**Row-height estimates (`HomeFeedVirtualLayout.Estimate`, `HomePage.cs:1365-1403`).** `available = max(1, min(cross,1600)
− 72)`; `g = Gap(available)`; `Head = 40`.

| row | formula | typical (A = 1088, g = 40) |
|---|---|---|
| Chips | `(hero ? 0 : 84) + 40 + 24 + g` | 104 with hero / 188 without |
| Hero | `(titled ? 40 : 0) + HomeHeroLayout.HeightFor(A) + g` | 40 + 384 + 40 = **464** |
| Weekly | `(titled?40) + ceil(n/cols)·88 + (rows−1)·12 + g` | 40 + 88 + 40 = 168 |
| Quick | `(titled?40) + ceil(min(n,8)/cols)·56 + (rows−1)·12 + g` | 40 + 124 + 40 = 204 |
| Recents / Podcasts | `ShelfExtent(A) + g` = `32 + (cardW+72) + 24 + g` | 32 + 243 + 24 + 40 = **339** |
| MixBand | `(titled?40) + rows·142 + (rows−1) + 2 + g` | 40 + 142 + 2 + 40 = 224 |
| Artists | `40 + 2·16 + 2·8 + 76 + 8 + 2·16 + g` | **204 + 40 = 244** — but the RENDERER pays `HomeArtistRowLayout.ModuleGap` = **32** (≥1080) / 24, not `g`, and scales the 76 avatar by `RampScaleFor` up to ×1.6 — see §9 |
| ChipCards | `(titled?40) + ceil(min(n,6)/cols)·96 + (rows−1)·12 + g` | 40 + 204 + 40 = 284 |
| Radio | `(titled?40) + ceil(min(n,12)/cols)·48 + g` | 40 + 288 + 40 = 368 |
| EpisodesAndBooks | `A ≥ 1020 ? max(L,R) + g : L + Gap(A) + R + g` | max(358, 434) + 40 = 474 |
| Timeline | `40 + 8·(40 + 16) + g` | **488 + 40 = 528** |
| Charts | `(Failed ‖ (Ready ∧ empty)) ? 152 : 232` `+ g` | 232 + 40 = 272 |
| Sections | `deck == 0 ? 0 : 232 + g` | 272 |
| Editorial | `(titled?40) + (A ≥ 980 ? max(188, 232) : 188+16+232) + g` | 40 + 232 + 40 = 312 |
| Feed | `ShelfExtent(A) + g` | 339 |
| Tail | `2 × WideEditorial(A).Height + 20 + 72 + 24` | 2 × 288 + 116 = **692** |

The extent table is seeded at 240 per row, then every seeded value is overwritten by `Estimate`, then corrected by the
engine's `SetMeasured` as rows realize (`HomePage.cs:1307-1310, 1427-1431`). A reseed (item count, shape version, or a
cross-size change > 0.5 DIP) wipes every correction — traced as `ScrollTrace.Note(110)` (landing) / `111` (facet).

**Who pays the module gap.** `HomeRowShell`'s `bottom` is the row gap for every row EXCEPT the two whose component owns
it internally: `RowHasContent` returns **false** for `Artists` and `Timeline` (`HomePage.cs:655-665`), so their shell
bottom is 0 and each pays its own bottom `Padding` — `HomeArtistRowLayout.ModuleGap(width)` = **32 / 24**
(`HomeModules.Artists.cs:178`) and `HomeModuleLayout.Gap(width)` = 40 / 32 (`HomeModules.Timeline.cs:98`). That is what
lets an empty artist podium or an empty timeline contribute **exactly zero** — no orphan gap — and it is also the
source of the Artists 8-DIP estimator drift in §9. Charts is the opposite case: `RowHasContent` is hard-coded `true`
(`HomePage.cs:660-662`) so the row always pays its gap in every one of its three states.

---

## 4. Colour & material

### 4.1 The three-leg shell wash — Home's signature

| step | input | function | where applied | transition |
|---|---|---|---|---|
| select | `HomeFeed` groups | `HomeWashSource.Sources(feed)` — first card of the first `Hero` / `WeeklyPair` / `MixBand` group, **by kind + ordinal only, never by title** (`HomeWashSource.cs:44-47, 97-105`) | three optional cards | — |
| resolve, tier 1 | `card.Meta.Accent` (`extractedColors.colorDark`, opaque ARGB, 0 = none) | `WaveePalette.Lift(ToColor(accent)) with { A = 1 }` — lift raises the strongest channel to 210/255 at constant hue, never darkens (`WaveePalette.cs:25-33`) | the leg colour | available **before any image byte lands** — a cold feed is already in its own colours |
| resolve, tier 2 | the graded cover | `WaveePalette.ChromeAccent(Surfaces.ChromeSchemeFor(url))` = `Vivid(Lift(Accent(scheme)))`, `Tok.AccentDefault` when S ≤ 0.08 (`WaveePalette.cs:126-131`) | the leg colour | lands later; the page watches exactly the ≤3 pending artworks (`HomePage.cs:224-226, 1094-1097`) |
| resolve, tier 3 | — | **does not exist**: null leg, no layer (`HomeWashSource.cs:70`) | — | — |
| identity | `CoverColorPlane.KeyForUrl(url)`, else the card uri | `HomeWashSource.KeyOf` (`:83-87`) | `WashLayer.ArtworkKey` | the shell keys its layer node on it → a new key remounts (cross-fade), a colour change under the same key **snaps** (D23, deliberate) |
| publish | `HomeWash(Layer(hero), Layer(weekly), Layer(mix))` | `ShellMaterial.Publish(slot, _washOwner, isClaim, definite: colorWashesDisabled, tint: null, wash)` (`HomePage.cs:237`) | `Signal<ShellMaterialState>` at the shell root | ownership resolved by `ShellTintOwnership.Resolve` (`CoverColorPlane.cs:543-555`) |
| paint | the state | `ShellMaterialLayer.Wash(...)` (`ShellMaterialLayer.cs:101-132`) | three clipped radial rects above live Mica, below the chrome | Enter/Exit `Opacity 0↔1`, 300 ms `FluentDecelerate` in / 200 ms `FluentAccelerate` out (`AnimBake.cs:26, 38`) |

Resolved placements (`ShellWashGeometry.Resolve`, `ShellWashGeometry.cs:24-54`; window fractions):

| leg | source centre / radius / fade | clipped box | anchor | node-relative centre / radius | alpha (dark / light) |
|---|---|---|---|---|---|
| Hero | (0.06, 0.00) r (0.74, 0.92) f 0.62 | 0.5188 × 0.5704 | top-left | (0.116, 0.000) r (1.426, 1.613) | 0.10 / 0.055 |
| Weekly | (0.92, 0.10) r (0.58, 0.78) f 0.64 | 0.4512 × 0.5992 | top-**right** | (0.823, 0.167) r (1.286, 1.302) | 0.085 / 0.05 |
| Mix | (0.58, 1.00) r (0.90, 0.70) f 0.66 | 1.0000 × 0.4620 | **bottom** | (0.580, 1.000) r (0.900, 1.515) | 0.085 / 0.05 |

Rules that must survive the rebuild:

* The transparent stop carries the wash's **own RGB at A = 0** — never `ColorF.Transparent` (premultiplied black drags
  the whole falloff toward black) (`ShellMaterialLayer.cs:117-124`).
* The wash host is inset by `PlayerDock.Reserve = 72` at the bottom and clips; only Mix is affected
  (`ShellMaterialLayer.cs:54-77`). The flat *tint* arm stays full-bleed.
* "No colour" is `WaveeColors.ShellGround with { A = 0.03 }`, never transparent (`ShellMaterialLayer.cs:89`).
* Washes off (`WaveeSettings.ColorWashesEnabled` false): Home still CLAIMS the slot as **definitely neutral**
  (`HomePage.cs:229-231`), and `AppearancePrefs.Epoch` is read so the Settings toggle applies live (`HomePage.cs:216`).
* A claim whose colour is not graded yet returns `WriteHeldColor` — the chrome keeps the previous page's colour rather
  than dipping to neutral and back (`CoverColorPlane.cs:552-554`).
* **Drift note:** `Surfaces.cs:120-134` says page-surface washes take `SchemeFor` (the theme's own half) while chrome
  plates take `ChromeSchemeFor` (the opposite half). Home passes `Surfaces.ChromeSchemeFor` (`HomePage.cs:227`).
  The CODE wins: the wash leg is `ChromeAccent` over the opposite-theme grading. Keep it — `HomeWashLocaleTests` and
  `HomeWashSourceTests.TheThemeAxisIsTheAlphaRamp_NotTheResolvedColour` pin this behaviour.

### 4.2 Hero band colour

* `accent = HomeCards.AccentOrChrome(card)` = payload accent → graded `Surfaces.SchemeFor` (`backgroundTintedBase` →
  `backgroundBase`) → **`Tok.AccentDefault`** as the last resort, because this one drives the Play capsule and must paint
  something (`HomeCards.cs:47-88`).
* Ground: `Surfaces.HomeHeroBackdrop(accent)` — vertical `lerp(FillCardDefault, accent, 0.10 dark / 0.06 light)` at stop 0
  → `FillCardDefault` at 0.45 and 1.0, card alpha preserved (`Surfaces.cs:437-445`).
* Veil: `Surfaces.ArtistHeroVeil(accent, axis)` where `veil = lerp(Tok.FillLayerDefault, accent, 0.24 dark / 0.16 light)`.
  Horizontal (wide/medium): alpha 0.96 → 0.92 @30% → 0.35 @62% → 0 @100%. Vertical (narrow): 0 → 0.35 @45% →
  0.78 dark / 0.42 light @82% → 0 @100% (`Surfaces.cs:173-192`).
* The full-bleed `header_image_url_desktop` arm replaces ground + cover with one `ImageFit.Cover` photo at
  `aspect = width/height`, `decodePx = clamp(round(width), 320, 1920)`, `EdgeFade(Bottom, 96)`, image fade-in 300 ms
  (`HomeCards.cs:382-404`).

### 4.3 Fold tile colour

Radial wash from the **first cover's raw payload accent** (not lifted, `WaveePalette.ToColor` only) at 0.22 → 0 by 0.72,
centred right-of-centre; omitted entirely when `Accent == 0` (`HomeFoldTile.cs:32-53`). Hover changes only the card fill
(`FillCardDefault` → `FillCardSecondary`) at the **same elevation** — never a Material lift.

### 4.4 Light / dark differences summary

| surface | dark | light |
|---|---|---|
| hero wash alpha | 0.10 | 0.055 |
| weekly / mix wash alpha | 0.085 | 0.05 |
| hero backdrop accent pull | 0.10 | 0.06 |
| hero veil accent pull | 0.24 | 0.16 |
| vertical veil peak | 0.78 | 0.42 |
| neutral ground | `WaveeColors.ShellGround` `#202020` @ 0.03 | `WaveeColors.ShellGround` `#EDEDED` @ 0.03 |

---

## 5. Motion

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| first reveal (Pending→Ready) | each realized virtual row | Opacity, TranslateY, BlurSigma | 0→1, 8→0, 3→0 | 500 ms | `SmoothOut` (0.22, 1, 0.36, 1) | 40 ms × row index | snaps (recipe returns early) | `SkeletonRegion.cs:169-187`, `MotionRecipes.cs:55-70` |
| same | the shimmer orphan | Opacity | 1→0 | 250 ms (`Expressive.Fast`) | engine dissolve | 0 | ignored (snap) | `SkeletonRegion.cs:34-36` |
| Charts region ready | — | — | — | — | — | — | — | `reveal: None` — the deck owns its entrance; shimmer exit floored at 400 ms (`HomePage.cs:746`, `SkeletonRegion.cs:20-24`) |
| group coordination | Home region + the artist-row disclosure | reveal thunks | — | — | — | fire together when the LAST member is done | — | `HomeSkeleton.Group` (`HomePage.cs:23-26, 842`; `HomeModules.Artists.cs:242-243`), `SkelGroupCoordinator` |
| skeleton bars | every derived bar | Opacity | 1 ↔ 0.5 | 1 000 ms loop | keyframes | — | — | `SkeletonRegion.cs:35` |
| facet tab → fused pill | the shared node `facet-pill:<id>` | Position, Size (width Reflow) | old → new box | 260 ms | `SmoothOut` | 0 | `LayoutTransition` (engine policy) | `HomeFacetChips.cs:135-138`, `ConcertUi.cs:640-643` |
| **any** tab label box moving or resizing | that same node | Position, Size (width Reflow) | old → new box | 260 ms | `SmoothOut` | 0 | `LayoutTransition` | `HomeFacetChips.cs:135-138` — the recipe rides `Tab`, not just the fuse: a wrap onto a second line, a sub-chip spilling in beside it, or a language change animates every tab's box, which is what stops the strip from snapping around one morphing pill |
| Charts `[Retry]` | the Charts region only | — | Ready/Failed → Pending → Ready | — | — | — | — | `reveal: None` + `_chartsArmed` already true ⇒ the Fold shimmer returns for ONE row, the page never re-reveals (`HomePage.cs:745-746`) |
| empty / failed page state | the `StateHome` ScrollView | Opacity, TranslateY, BlurSigma | 0→1, 8→0, 3→0 | 500 ms | `SmoothOut` | 40 ms × child | snaps | the same `SkelReveal.StaggerRows` the feed uses — `StateHome` is a branch of the SAME region (`HomePage.cs:840-851`), so an empty or failed Home rises exactly like a loaded one and never cuts in |
| fuse | the pill's inner segment | Position + Opacity (Enter) | Dx 56, Opacity 0.4 → rest | 300 ms | `SmoothOut` | 0 | — | `ConcertUi.cs:655-658` |
| fuse | the spilled sub-token (Exit) | Position + Opacity | 0 → Dx −56, Opacity 0 | 220 ms | `FluentAccelerate` | 0 | — | `HomeFacetChips.cs:199-201` |
| select a tab | the underline | Fill | transparent ↔ `AccentDefault` | 150 ms (`MotionTok.ControlFast`) | `FluentStandard` | 0 | KeepFade | `HomeFacetChips.cs:176` |
| hover a Fold tile | its ≤3 covers | OffsetX/Y + Rotation (**deltas on the authored rest pose**) | +(−10,+6,−5°) / (+2,−6,+3°) / (+10,0,+3°) | 250 ms | `FluentStandard` | 0 | `MotionTok.ControlNormal` carries KeepFade — **no `if (ReducedMotion)` branch anywhere** | `HomeFoldTile.cs:63-79`, `HomeModules.cs:573-579` |
| hover a Fold tile | the card plate | Fill | `FillCardDefault` → `FillCardSecondary` | engine hover ramp (83 ms) | `FluentStandard` | 0 | KeepFade | `HomeFoldTile.cs:123` |
| hover a quick tile | the play FAB | Opacity + Scale | **0→1, 1.0→1.07** (`ScaleEmphatic.Hover`; the prototype's 0.86 rest is NOT reproduced — the rest scale is 1.0 and only hover moves) | 150 ms (`MotionTok.ControlFast`) | `FluentStandard` | 0 | KeepFade | `HomeCards.cs:200-213`, `WaveeMotion.cs:48` |
| hero artwork lands (photo arm) | the `header_image_url_desktop` image | Opacity (image fade) | 0→1 | 300 ms (`MotionTok.StandardEnter`) | `FluentDecelerate` | 0 | KeepFade | `HomeCards.cs:394-395`, `MotionTok.cs:168` |
| masthead leaves/enters the family | `ShellMastheadBand` root | Opacity | 1→0 (leaving) / 0→1 (entering) | `PageNavMotion.FadeThroughExitMs` | `FluentAccelerate` (hide) / `SmoothOut` (show) | 0 | KeepFade | `ShellMastheadBand.cs:23-26, 69-71` — Home is always the **hide** side |
| wash leg re-graded (new artwork key) | the wash rect | Opacity (mount/orphan) | 0→1 / 1→0 | 300 / 200 ms | `FluentDecelerate` / `FluentAccelerate` | 0 | `WashFade` is null under reduced motion → snap (`ShellMaterialLayer.cs:34`) | `ShellMaterialLayer.cs:129-130`, `AnimBake.cs:26,38` |
| material tint change | the flat tint rect | Fill | previous → new | 250 ms (`WaveeMotion.Standard`) | brush transition | 0 | KeepFade | `ShellMaterialLayer.cs:93-99` |
| page enter/exit | the whole page root | per `PageNavMotion.RecipeFor(direction)` | — | — | — | — | — | `ContentHost.cs:147-153` — chapter 18 |
| daylist countdown | `FlipCountdown` digits | flip | — | — | — | — | — | chapter 05/11; samples the frame clock |
| shelf paging | `PagedShelf` chevrons | scroll | — | — | — | — | — | chapter 02 |

**Frame-time rule.** Every animation above is engine-scheduled (`AnimEngine` tracks advanced from the present clock) —
nothing on Home polls a timer to animate. The one `Environment.TickCount64` on this surface is
`HomePage.NowMs()` (`HomePage.cs:870`), and it is **not** motion: it feeds the reveal gate's 1 500 ms chrome cap, where
only elapsed differences matter and the comment says so. Keep that exception in 0.3; do not route it through
`FrameTime.NowQpc` (a paused/parked page must still age its cap).

---

## 6. Interaction

**Hero band**
* **Cover click only** (the square artwork, or the full-bleed photo in the header-image arm) → `NavCard(card)`. The
  copy column and the surrounding plate carry NO click handler — see W13 — so the band is not one big button
  (`HomeCards.cs:413, 389`). → `HomeCardNav.Open` (`HomePage.cs:279`, `HomeSectionNavigation.cs:42-80`):
  Liked → `liked`; Track/Episode → **play**, never navigate; Artist → `artist:<uri>`; Album → `DetailNav.OpenAlbum` with a
  preview stash; Podcast/Audiobook → `show:<uri>`; default → `DetailNav.OpenPlaylist` with `OwnerName` (not Subtitle) and
  the daylist window carried along.
* `▶ Play` → `PlayCardAsync` — **awaited**, writes one `card.play uri=… kind=… outcome=completed|canceled|failed` log
  line, and a thrown failure shows a toast `home.playFailed` "Couldn't play that. Try again." (`HomePage.cs:43-70`).
  Item-vs-context is `HomeCardPlayRouting.PlaysAsItem(kind)` — Track/Episode play themselves, everything else is a context.
* `⇄ Shuffle` → `SetShuffleAsync(true)` then the same `PlayCard` (`HomePage.cs:806-810`).
* `♡` → `LibraryBridge.ToggleSaved(uri, title)` (`HomePage.cs:681`). **The glyph never changes.** It is
  `WaveeCta.Icon(Icons.Heart, onLike)` unconditionally (`HomeCards.cs:349`) — no filled/outline swap, no pressed latch,
  no toast, no disabled state, and the hero never reads the library back. Pressing it twice looks identical to pressing
  it once. Port the behaviour if you port the surface, or fix it deliberately (it is a genuine 0.2.9 gap, not a
  simplification) — but do NOT ship a half-state where the icon animates without a source of truth behind it.
* `⋯` → carries **no handler**: `requestsContext: true` re-enters the engine's context funnel and finds the band's
  attached menu (`HomeCards.cs:350-354`).
* Right-click anywhere on the band → the same card menu.

**Cards (every module)**
* Left click → `NavOf(card)`; hover-play FAB → `PlayOf(card)`; right-click → `Menus.CardAttach(...)` with
  `circular: kind == Artist`; drag → `WaveeResourceDragPayload.ForEntity(...)` — **Track and Episode cards are not
  draggable at all** (the feed carries only a uri for either) (`HomePage.cs:294-311`).
* Drag targets: a sidebar playlist (add its tracks), a sidebar folder (file it), the pin band (pin it).

**Module headers**
* The chevron is armed only when the group names something the section page can open — a uri, a `TotalCount > 0`, or
  cards in hand (`HomeModules.cs:39-47`). Recents is the exception: armed unconditionally, because its destination is the
  app's own `recents` route backed by a different endpoint (`HomePage.cs:692-700`, `HomeModules.cs:152-158`).
* Charts' module header opens `browse:<ChartPages.Charts>` labelled "Charts"; each Fold tile opens
  `browse-section:<uri>`, or — when the section holds exactly one card — that card directly (`HomePage.cs:741-742`,
  `HomeSectionNavigation.cs:85-99`).
* "Sections for you" tiles open `home-section:<uri>`, or `home-section:wavee:local:<hash>` for a section the server gave
  no uri, with the section stashed in `HomeSectionPreviewStore` first (`HomePage.cs:347-355`).

**Facet strip**
* Click a tab → `Select(svc, model, chipId)`: peek-compare first (re-picking the current chip is a no-op), write
  `Services.HomeFacet`, hand `(previous, next)` back to the page (`HomeFacetChips.cs:207-213`).
* Click "All" → writes `null` (the app's own position; the server never models clearing).
* Click a spilled sub-chip → selects it; the parent fuses.
* Click the fused pill → steps back **one level** to the bare parent, never to unfiltered (`HomeFacetStrip.cs:62`).
* Exactly one facet read is in flight; a second tap cancels the first. A failure is **loud**: the strip is put back to
  `previous` and a toast shows `home.facetFailed` "Couldn't load that view. Showing the previous one." — unless the user
  has since moved on (`HomePage.cs:1048-1075`).
* Roles: tab shell `AutomationRole.Tab`; "All" is a Tab too; sub-chip `AutomationRole.Button`; the fused pill is the
  control (the shell is inert so the step-back cannot double-fire).

**Artist podium (ch. 11's row, but its GESTURE is Home's)**
* A pod is **double-click-to-navigate**, hand-rolled: the engine's `DoubleTap` is reserved but unrouted and `OnClick`
  carries no click count, so the same uri clicked twice inside `HomeArtistRowLayout.DoubleClickWindowMs = 400` counts as
  a double (`HomeArtistRowLayout.cs:84-93`, `IsDoubleClick`). A negative gap never reads as a double. Single-click
  selects the pod (the Mixview disclosure); double-click opens the artist. This is the ONE multi-click gesture on Home,
  and it is pure + testable — `HomeArtistRowLayoutTests`.

**Keyboard / focus**
* Fold tile: `Focusable = true`, `Role = Hyperlink`, `Cursor = Hand` (`HomeFoldTile.cs:125`).
* Module header with a drill target: `Focusable = true`, `Role = Hyperlink` (`HomeModules.cs:69`).
* Customize "⋯": `Focusable = true`, `Role = Button`, tooltip `home.customize` "Customize Home". The click TOGGLES:
  a second press on an open flyout closes it (`HomeCustomizerPage.cs:217`) rather than re-opening a second one.
* The hero's four CTAs, every card and every module header are focusable; the hero BAND itself is not — only its
  artwork carries `Role = Button` (`HomeCards.cs:389, 413`), so tabbing never lands on the plate.
* No Home-specific accelerators; scrolling is the engine's (`ScrollKey "home"` restores the offset on a cold revisit).

**Tooltips**: only the customize button carries one on this surface (`HomeCustomizerPage.cs:246`).

**Inline edit / selection / multi-select**: none on Home.

**Out-of-app**: the daylist ROLLOVER is an OS toast, scheduled ahead of time from the feed's own window
(`App/DaylistNotifier.cs`). Title "Your daylist has refreshed", body "It moved on from {title}" or "A new mix is
waiting." when the ending window had no title (`:68-71`) — both **hard-coded English, not loc keys**; port the strings
into `assets/loc` in 0.3 rather than porting the literals. Group `wavee.daylist`, tag `daylist-roll` (a new window
REPLACES the held entry, never stacks, `:66`), minimum lead 2 minutes (`MinLead`, `:23` — Windows silently drops a
toast due immediately), gated on `NotifyTopic.DaylistRefresh`. Activation deep-links `wavee://open?route=pl&arg=<uri>`,
or `wavee://open?route=home` when the card carried no context uri (`:72-74`). Three more behaviours to port with it:
  * **Silent when the policy says so** — `if (!policy.Sound) toast.Silent()` (`:76`); the dial's sound leg is honoured
    per toast, not globally.
  * **Turning the dial down REVOKES a pending toast.** `RequestReconcile` re-checks the held entry against the current
    dials and can only ever `Unschedule` (`:106-117`) — re-scheduling needs a window end, which arrives with the next
    feed resolve. The disallowed branch of `Note` unschedules and remembers nothing (`:57-61`), so turning the dial back
    up re-arms itself from the next read rather than guessing at a stale expiry.
  * **The diagnostics seam is the REAL path.** `SimulateSchedule` (`:87-104`, Settings ▸ Notifications ▸ Send event)
    goes through `Note`, and nudges the millisecond by 1 when the window it is simulating is already held — otherwise a
    second press is a no-op, because `_scheduledFor == expiresAtUnixMs` is the idempotence gate (`:46`). Parity item 71
    depends on exactly that nudge.
  * Fed by `HomeDaylistHydrator.WindowObserved` (`:35`), i.e. a data path that runs when the feed resolves — **never**
    from a render. In 0.3 it hangs off the `Playlist.ExpiresAt` write, not off `Home.Page`.

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 data source | 0.3 read | readiness predicate (skeleton until true) |
|---|---|---|---|
| greeting word | `HomeFeed.Greeting` ← `home.greeting.transformedLabel` (`SpotifyHomeComposer.cs:216-219`) | `Home.Greeting` (StringId column on the Home subject row) | `h.Knows(HomeFields.Greeting)`; else the local-clock fallback, which is always renderable |
| greeted name | `PlaybackBridge.User.Value.DisplayName` | `User.Me.DisplayName` | `User.Me.Knows(UserFields.Identity)`; a ≥20-char space-less handle is suppressed |
| facet chips | `HomeFeed.Chips` (`HomeChip(Id, Label, SubChips)`) | `Home.Chips`: a `(StringId id, StringId label, int subStart, byte subCount)` array on the Home row — chips are **not** entities | `h.Knows(HomeFields.Chips)`; 0 chips ⇒ no strip (the row still renders the greeting) |
| selected facet | `Services.HomeFacet` (`Signal<string?>`) | `Home.Facet` (`Signal<StringId>`; `default` = unfiltered) | always |
| row table | `HomeLandingProjection.Project(feed, titles, layout).Rows` | `Home.Project(...)` over section slots | needs the section list complete: `Edges.HomeSection.State[homeSlot] == 2` |
| hero card | first card of the first `Hero` group | `Edges.HomeSection.Targets(heroSectionSlot)[0]` → a `Playlist`/`Album` slot | `Knows(Identity | Image)` **and** the format/accent/seeds group — partial ⇒ skeleton |
| hero eyebrow "· your daylist" | `card.Meta.Format == "daylist"` | `Playlist.Format` (byte enum column) | `Knows(PlaylistFields.Format)` |
| hero tags | `card.Meta.Seeds` (daylist `localized_terms`) | `Edges.PlaylistSeeds` payload (`StringId`) **or** a `Seeds` range column | `Knows(Seeds)`; absent ⇒ the tag row collapses (and the estimate over-reports by 32 — accept, the measured pass corrects it) |
| hero meta "50 songs · by Spotify" | `Meta.TrackCount` + `Meta.OwnerName` | `Playlist.TrackCount` + `Playlist.Owner` (user slot) | `Knows(TrackCount)` ∧ `Knows(Owner)` — the line is omitted, never half-written |
| hero countdown | `Meta.ExpiresAtMs` / `CreatedAtMs` | `Playlist.ExpiresAt` / `CreatedAt` (int, app epoch seconds) | `> 0` (the slot collapses otherwise) |
| hero photo masthead | `Meta.HeaderImageUrl` | `Playlist.HeaderImage` (StringId) | `Knows(HeaderImage)` |
| hero / Fold / wash accent | `Meta.Accent` (`extractedColors.colorDark`) | **GAP** — see below | payload accent known, or a graded scheme present |
| module titles | server section titles + `HomeModuleCopy.Titles` (loc) | section `Title` StringId + a `Home.Titles` struct rebuilt per read (Loc is live) | title is a `StringId`; a blank/whitespace title is not a label (`HomeLandingProjection.cs:298-309`) |
| section directory (Fold) | `HomeFeed.Sections` minus consumed uris | the Home row's section edge list minus the slots the projection consumed | list `State == 2` |
| Charts deck | 5 `browseSection` reads via `HomeBrowseCards.LoadChartDeckAsync` | `Browse` subject slots for `ChartSections.All` + their edges | all five settled (or the featured one null ⇒ empty deck) |
| timeline | `NotificationCenterBridge.Items` | **GAP** — notifications are not entities | `WhatsNewState != Loading ∧ SocialState != Loading` |
| artist podium | `IUserTopService.GetTopArtistsAsync/GetTopTracksAsync` | `Edges.UserTopArtists` / `UserTopTracks` on `User.Me` | list `State == 2` |
| "is this card playing" | `HomeNowPlaying.Mark(uri)` | `Playback.Current.Value` + the context uri | live signal, never a fetch |
| wash legs | `HomeWashSource.Select(feed, ChromeSchemeFor)` | `Home.WashPicks(h, Design.ChromeSchemeFor)` | a leg is null until its accent OR grading exists — **never a placeholder colour** |
| layout order / hidden modules | `HomePreferences.Layout` + `LayoutVersion` signal | unchanged (a settings document, not an entity) | always |

**Readiness: the whole-page rule.** 0.2.9's three-state model must be rebuilt over `Known` bits, because the 0.3 store
*warms rows from sqlite at boot* (`Store.Warm`, plan §4.1) — exactly the "cached shelves painted, then replaced 1.5 s
later" shape the gate exists to prevent:

```
Home.State Classify(int sectionCount, bool liveAttemptConcluded)   // port of HomeFeedReadiness.Classify
    !concluded              -> Placeholder   // keep the skeleton; the warm rows are provisional
    sectionCount > 0        -> Ready
    else                    -> Empty
concluded := the Home fetch for (scope, facet) is no longer Inflight
             AND Spotify.Session.Phase != Connecting          // the ProjectAuthState equivalent
```

**And the CHROME predicate, which is a different question.** `ChromeConcluded` (`HomePage.cs:136-140`) is what the
1 500 ms hold waits on, and "concluded" there means *nothing is coming*, not *something arrived*:

```
chromeConcluded := charts.Loadable.State != Pending
                && (nc is null                                  // no notification bridge at all → concluded
                    || (nc.WhatsNewState != Loading              // Idle (never fetched: offline, --fake) → CONCLUDED
                        && nc.SocialState != Loading))           // Loading is the ONE state worth a bounded wait
```

Getting `Idle == concluded` wrong is the difference between a 0 ms hold and a 1 500 ms one on every offline launch.
Every read is `Peek()`, not `.Value` — the predicate is called from background continuations; the *subscription* lives
in the one signal effect above it (`HomePage.cs:202-208`), which reads `.Value` on all three plus `_heldVersion`.

Pages demand their whole model on mount — `Home.Ensure(facet)` plans one fetch for the document and one batched
`Entities.Ensure(cardSlots, Fields.Row)` for **every** card in it, not per visible range (`WarmGroup` stays, but as an
*image decode* budget only, never as a data window).

### DATA GAPS

| element | 0.2.9 source | proposed 0.3 column / edge |
|---|---|---|
| **The Home subject itself** | `HomeFeed` record | `HomeTable : Table` with `Column<StringId> Greeting, Facet`; `Column<uint> Known`; one row per (scope, facet). `Entities.Home(facet)` factory. Nothing in plan §4.1-4.3 creates it. |
| **Section rows** | `HomeSection(Uri, Title, Subtitle, Cards, TotalCount, RawItemCount, Unsupported, Duplicate)` | `SectionTable : Table` (`Uri, Title, Subtitle` StringId; `Total, Raw, Unsupported, Duplicates` int; `Kind` byte = the composer's `__typename` verdict). `Edges.HomeSection` parents the Home row → section slots; `Edges.SectionCards` parents a section → entity slots. The plan has **one** `HomeSection` edge table and no section row store. |
| **Section kind (`__typename`)** | `HomeSpotlightSectionData` / `HomeFeedBaselineSectionData` / `HomeRecentlyPlayedSectionData` / generic — read at decode (`SpotifyHomeComposer.cs:60-110`) | `Section.Kind` byte column, written by `Spotify.Decode.Home`. The facet projection and the landing projection both depend on it; the card kinds alone cannot reproduce it. |
| **Facet chips** | `HomeChip(Id, Label, SubChips)` | `Column<StringId>` pair arrays on the Home row (`ChipIds`, `ChipLabels`, `ChipParent`), or a tiny `ChipTable`. Not entities; do not force them into a slab. |
| **Playlist format** | `HomeCardMeta.Format` (`daylist`, `daily-mix`, `discover-weekly`, `release-radar`, `topic-mix`, `inspiredby-mix`, `editorial`, …) | `Playlist.Format` byte enum — **load-bearing**: it is what routes a card to Hero / MixBand / WeeklyPair / ChipCards / RadioDial / Featured (`SpotifyHomeComposer.cs:230-241`). |
| **Payload accent** | `HomeCardMeta.Accent` = `extractedColors.colorDark` ARGB | `Column<uint> Accent` on Playlist/Album/Show (+ `Known` bit). Without it there is **no cold-start colour**: the wash, the hero backdrop, the Fold wash and every card spine go grey until a cover decodes and grades. |
| **Graded cover palette** | `CoverColorPlane` (5 roles × 2 themes, keyed by artwork url) | A palette side-table keyed by artwork `StringId`: `Column<uint> BgBase, BgTinted, TextBase, TextSubdued, TextBright` × 2 themes + a `Known` bit and a per-key watch signal. The plan's data model has **no colour anywhere**. Shared with ch. 00, ch. 03, ch. 21. |
| **Seeds / tags** | `Meta.Seeds` (mix seed artists, daylist terms) | `Edges.PlaylistSeeds` with a `StringId` payload (the `TrackTags` shape from plan §4.3). |
| **Daylist window** | `Meta.ExpiresAtMs`, `CreatedAtMs` | `Playlist.ExpiresAt`, `Playlist.CreatedAt` (int seconds). Also feeds `DaylistNotifier` (OS toast scheduling, `App/DaylistNotifier.cs`). |
| **Header image** | `Meta.HeaderImageUrl` (`header_image_url_desktop`) | `Playlist.HeaderImage` StringId. |
| **Generic title / needs-hydration** | `Meta.GenericTitle`, `NeedsHydration` (a daylist whose `name` equals its `daylist_pretitle`) | `Playlist.GenericTitle` StringId + a `Known`-group rule: identity is *not* known until a title differing from the generic one has arrived (this is the 0.2.9 `HomeDaylistHydrator` contract, and in 0.3 it becomes a `Known` bit, not a re-fetch loop). |
| **Owner name** | `Meta.OwnerName` (distinct from `Subtitle`) | `Playlist.Owner` → a `User` slot; resolve the name through the user table. Never re-use the description slot. |
| **Card eyebrow** | `HomeCard.Eyebrow` (the baseline section's own title, stamped per card) | derive at render from the **section** row's Title — the 0.2.9 copy-onto-each-card exists only because cards were records. |
| **Audiobook rating / author / signifier** | `Meta.Rating`, `Author`, `Signifier` | `Show.Rating` (ushort ×100), `Show.Author` StringId, `Show.Signifier` byte enum — ch. 09/11. |
| **Episode duration / resume / video** | `Meta.DurationMs`, `ResumeMs`, `HasVideo` | already in plan §4.2 for Track/Episode; confirm Episode carries `ResumeMs`. |
| **Chart sections** | hardcoded `BrowseTaxonomy.ChartSections.All` (5 uris) + `browseSection` reads | `Browse` subject slots + `Edges.BrowseSection`; the five uris stay as constants (ch. 13). |
| **Notification timeline** | `NotificationCenterBridge.Items` (what's-new + Spotify-category feeds) | not an entity domain — keep a small `Notifications` store with a `Signal<uint> Changed`, and port `HomeTimelineMerge` unchanged over it. |
| **Recently played** | `/playlist/v2/list/recents/page` (its own endpoint) | `Edges.Recents` on `User.Me` (ch. 16). |
| **Hover peek previews** | `HomeBaselinePreviews.Prime(uris)` (`HomePage.cs:424-425`) | a preview-track side-table (uri → name/cover/previewUrl); ch. 11. |
| **Home layout document** | `home-layout.json` via `HomePreferences`/`HomeLayoutStore` | unchanged — a settings document read by `Home.Project`; ch. 12. |

---

## 8. Pure rules to port verbatim

| name | 0.2.9 file | decides | tests | 0.3 destination |
|---|---|---|---|---|
| `HomeFeedReadiness` (`Classify`, `ShouldForceRelease`, `MayReveal`, `ApplyEpoch`, `ForceReleaseMs = 8000`, `ChromeSettleMs = 1500`) | `Features/Home/HomeFeedReadiness.cs:31-84` | whether a read settles the page, when the first reveal may fire, and that a withheld read leaves the applied epoch UNCHANGED | `Wavee.Tests/HomeFeedReadinessTests.cs:15-152` (14 facts) | `Entities/Home.cs` CORE |
| `HomeRevealGate<TFeed>` (`Offer`, `Tick`, `ForceRelease`, `AppliedEpoch`, `Revealed`, `IsHolding`) | `HomeFeedReadiness.cs:105-164` | the whole launch sequence as a state machine; one instance per mounted page | `HomeFeedReadinessTests.cs:154-322` (`HomeRevealGateTests`, 13 facts incl. the recorded-launch replay) | `Entities/Home.cs` CORE |
| `HomeLandingProjection` (+ `HomeLanding`, `HomeLandingModule`, `HomeRow`, `DefaultRows`, `ApplyLayout`, `WeeklyPair`, `SectionDirectory`, `Title`) | `Features/Home/HomeLandingProjection.cs:12-368` | the authored rhythm: one module per kind, hide + reorder, the half-pair fallback into the quick grid, the section directory, the chrome anchors (Artists after MixBand; Timeline + Charts + Sections after Podcasts) | `HomeLandingProjectionTests.cs` (146), `HomeLayoutTests.cs:240-373` | `Entities/Home.cs` CORE |
| `HomeFacetStrip` (`Resolve`, `Slots`, `FacetSlot`, `FacetSlotKind`) | `Features/Home/HomeFacetStrip.cs:11-74` | All first, server order, a selected parent spills its subs inline, a selected sub fuses, the fused slot's Select is the PARENT id, and the shared node key | `HomeFacetStripTests.cs` (149, 5 facts) | `Entities/Home.cs` CORE |
| `HomeFacetProjection` (`Rows`, `Classify`, `GroupKind`, `Baseline`, `Hero`, `Unique`, `Key`) | `Features/Home/HomeFacetProjection.cs:11-183` | a facet page's rows: server order, per-section titles, the coalesced consecutive-baseline run, the no-ledger fallback | `HomeFacetProjectionTests.cs` (212) | `Entities/Home.cs` CORE |
| `HomeHeroLayout` (+ `HomeHeroMetrics`, `HomeHeroTier`) | `Features/Home/HomeHeroLayout.cs:5-70` | hero tier thresholds 700/980, heights 336/344/384 as a sum of on-ramp blocks, copy padding 48/44, artwork = height, fade 96 | `HomeHeroLayoutTests.cs` (61, 3 theories) | `Entities/Home.cs` CORE |
| `HomeWashSource` (`Sources`, `Select`, `Pick`, `PlaneUrl`, `KeyOf`, `Fingerprint`) | `Features/Home/HomeWashSource.cs:11-106` | which three cards tint the shell and how a leg resolves (payload accent → graded → null) | `HomeWashSourceTests.cs` (223, 10 facts), `HomeWashLocaleTests.cs` (138) | `Entities/Home.cs` CORE (its palette dependency moves to `Platform/Design.cs`) |
| `HomeModuleLayout` (geometry: `Gap`, `Columns`, `RadioColumns`, `CardHeight`, `ContentExtent`, `ShelfExtent`, `FeaturedExtent`, `Shown`, `Fold*`, `SplitEvenMin`, `EditorialMin`) | `Features/Home/HomeModules.cs:490-716` | every breakpoint and every row height the estimator and the renderer share | none today (**add them** — this is the file whose drift re-pins the scroll anchor) | `Entities/Home.cs` CORE (shared with ch. 11) |
| `HomeTimelineMerge` (`Build`, `Eligible`, `LocalDay`, `MaxRows = 8`) | `Features/Home/HomeTimelineMerge.cs:8-114` | which notifications are timeline material, newest-first with an id tie-break, local-midnight day groups, and the uncapped "N unheard of M" counts | `HomeTimelineMergeTests.cs` (309) | `Entities/Home.cs` CORE |
| `HomeCardPlayRouting.PlaysAsItem` | `Features/Home/HomeCardPlayRouting.cs:13` | item vs context on every ▶ | `HomeCardPlayRoutingTests.cs` (25) | `Entities/Home.cs` CORE |
| `BrowseSectionWalk` (`Begin`, `Fold`) + `ChartTitleMatch` | `Features/Home/BrowseSectionWalk.cs:9-82` | the Charts drill walk's first publish and its three terminators; the title filter span | `BrowseSectionPagingWalkTests.cs` (326) | `Entities/Browse.cs` CORE (ch. 13) |
| `HomeBrowseCards` (`Section`, `LoadChartDeckAsync` shape, `ChartDeckSeed`, `KindOf`) | `Features/Home/HomeBrowseCards.cs:14-93` | browse→home card mapping and the "Featured null throws unless there is no live catalog" rule | `HomeBrowseCardsTests.cs` (226) | `Entities/Browse.cs` + `Home.cs` |
| `ShellWashGeometry` (`Resolve`, the three placements, the four alphas) | `Features/Shell/ShellWashGeometry.cs:20-55` | where each wash box sits and how strong it is | `ShellWashGeometryTests.cs` (136) | `Shell/Shell.cs` CORE — ch. 18; Home is the only producer |
| `ShellTintOwnership.Resolve` | `SpotifyLive/CoverColorPlane.cs:516-556` | claim / refresh / hold / no-write for the material hand-over | `ShellTintOwnershipTests.cs` (135) | `Shell/Shell.cs` CORE — ch. 18 |
| `HomeLayoutDoc` / `HomeLayoutReducer` / `HomeLayoutWire` / `HomeLayoutModules` | `Wavee.Core/Home/HomeLayoutModel.cs`, `Features/Home/Persistence/*` | hide/reorder/reset + the persisted document | `HomeLayoutTests.cs` (373) | ch. 12 |
| `HomeArtistRowLayout` (tier 900, hysteresis 24; `BaseArtSize` 76/60/46, `RampScaleFor`, `MinArtScale 1.0` / `MaxArtScale 1.6`, `ModuleGap` 32/24, `IsDoubleClick` + `DoubleClickWindowMs = 400`) | `Features/Home/HomeArtistRowLayout.cs:46-93` | the podium's Wide/Spine tier, its fill-the-width avatar ramp, its PRIVATE module gap (§9 defect #2) and the hand-rolled double-click | `HomeArtistRowLayoutTests.cs` | ch. 11 |
| `HomeSectionPaging` | `Features/Home/HomeSectionPaging.cs` | the "Show all" cursor arithmetic | `HomeSectionPagingTests.cs` | ch. 12 |

Not pure but must be ported as-is in shape: `HomeFeedVirtualLayout` / `HomeFacetVirtualLayout` (`HomePage.cs:1196-1591`).
Their `Estimate` bodies are pure arithmetic over `HomeModuleLayout`; only `Ensure/Window/ItemRect/SetMeasured` touch the
engine's `ExtentTable`.

Also not pure, also port as-is in shape — `HomeSectionAppendPreloader` (`Features/Home/HomeSectionAppendPreloader.cs`,
96). Home's LANDING never mounts it (the drill page and Browse's flattened grid do — ch. 12 / ch. 13), but it is the
app's one infinite-scroll grammar and its three gates are numbers, not taste: **(C)** near-tail is
`OffsetY + ViewportH >= ContentH − 1.5 × ViewportH`, published by the host through `NearTailWatch`'s 24-px-floored
offset × 48-px-floored content-height projector (`:46-54`) so an append's own growth re-evaluates nearness; **(B)** a
300 ms `ArmDelayMs` debounce, cancel-and-restart on every gate re-evaluation, re-checked on the UI thread after the
delay; **(A)** `MaxAttempts = 3` bounded retry plus the host's single-in-flight `Loading` signal. No shimmer child and
no visual footprint at all — it returns `new BoxEl()` (`:72`), deliberately, because the grid it trails is a
self-scrolling `Virtual.Custom` with nothing to append a loading row to.

---

## 9. Re-author notes

### What must not be simplified

* **The reveal gate is not optional bookkeeping — it is the page's opening shot.** Three things are easy to lose and each
  one was a recorded user complaint: publishing warm rows before the session settles; letting Charts or the timeline pop
  in after the reveal; and stranding the skeleton when no read ever concludes. Port `HomeRevealGate` verbatim and keep
  all four verdicts (`Withheld` / `Held` / `Reveal` / `Swap`).
* **Two extent tables, never one.** A facet and the landing are different documents; one table asked to describe both
  reseeds on every swap and throws away every measured correction belonging to both (`HomePage.cs:146-149, 1446-1455`).
* **Two scroll keys.** `"home"` for the landing (and for the empty/failed states, which are facet-agnostic), `"home:<facet>"`
  per facet — the previous facet's offset belongs to a document that no longer exists (`HomePage.cs:509, 582, 820`).
* **The chip row's single key across the whole page family.** `"home:row:Chips"` in both viewports
  (`HomePage.cs:476-483, 541-547`). A remount a second after the tap replays the fused pill's morph.
* **`WarmGroup`'s per-module decode budget** (64 / 128 / 256 / 512 px by `HomeGroupKind`, `HomePage.cs:428-441`): a cover
  decoded for a 32-px station row must not be fetched at 512. It follows the realized window — the old eager whole-feed
  pass enqueued every cover before the first content frame.
* **The landing/facet projection memos** (`HomePage.cs:1104-1158`). `Project` walks every group and every card; it used to
  run inside `VirtualHome`, which is re-entered on every re-render (a hover fade, a landed grading). Titles compare **by
  value** because `Loc` is live; the feed compares by **reference** because it is an immutable snapshot.
* **`HomeFeedDiagnostics.LogModules`** (`home.feed.modules`, one line per distinct shape, facet included in the dedupe
  key) and `HomeImageDiagnostics` (`home.image.quickgrid.*`, gated on Developer mode, **not** an env var). Both are
  always-on diagnostics under CLAUDE.md's rules; keep them.
* **The greeting's server-first rule.** `home.greeting.transformedLabel` is already localized for the ACCOUNT and bucketed
  against the timezone the request carried; the local-clock fallback (< 5 evening, < 12 morning, < 18 afternoon, else
  evening) is only for sources that publish none (`HomePage.cs:1020-1033`).

### Traps

1. **Props freeze at mount.** Every row is a `Responsive.Of` component. 0.2.9 keeps rows honest with (a) a live read
   inside the closure and (b) a content-fingerprint key. Drop either and every Ready→Ready swap becomes invisible — the
   shelves keep describing the first feed while the chip row, which reads a signal, moves on (`HomePage.cs:463-475`).
2. **`Key` is where width-derived state goes.** `HomeFoldTile` bakes `cardW` into its key (`HomeFoldTile.cs:126`);
   `SectionGrid` bakes the column tier and title lines; the radio grid bakes its column count
   (`HomeModules.cs:245, 456`). A recycled shell must replace, not positionally rebind, an incompatible subtree — that is
   why `HomeRowShell` keys its single child (`HomePage.cs:779`).
3. **`ReuseGuard`** will fire if a row's component type changes under an unchanged key. The row's *role* stays as mounted
   in the facet viewport for exactly this reason (`HomePage.cs:554, 570`).
4. **Zero-allocation scroll frames vs per-row richness.** 0.2.9 reconciles them by (i) hoisting the extent tables outside
   `Render`, (ii) memoizing `SourceGroupKey` / `SectionSetKey` in `ConditionalWeakTable`s because `KeyAt` runs per
   realized row **and** again for the list's own key lookup (`HomeModules.cs:732-757`), and (iii) never subscribing the
   page to anything hot — `CoverColorPlane.Epoch` is explicitly forbidden at page scope; only the ≤3 wash artworks are
   watched (`HomePage.cs:220-226, 1094-1097`). In 0.3 the equivalent hazard is `Table.Changed`: Home must subscribe to
   the **Home table's** publication, not to Playlist/Album/Track table publications, or every scroll of any other page's
   grid re-renders Home.
5. **`KeepAlive` park runs no cleanups.** The 60 s refresh effect stays live on a parked page and replays the fresh feed
   the instant it is activated; `UseActivation(onActivated)` re-claims the shell material and does the epoch compare
   (`HomePage.cs:150-184, 243-258`). A cached page does **not** re-run its mount effect — that is why the claim is
   explicit.
6. **The 60 s loop must be tied to the component's lifetime.** Each cold remount used to leak an orphaned `PeriodicTimer`
   loop that compounded over a session (`HomePage.cs:150-153`).
7. **The refresh loop reads the facet as a request parameter** (`svc.HomeFacet.Peek()`, `HomePage.cs:959`) — re-reading
   unfiltered would have every tick quietly replace the faceted feed with "All".
8. **`_chartsArmed` is monotonic per mount.** A later Offline→Connecting→Live reconnect must not flip the Charts resource
   back to Pending and re-skeletonize one row inside an already-revealed page (`HomePage.cs:114-123`).
9. **`FoldRest` clamps at 0** so a 0-width first frame cannot park covers at a negative X and paint into the band header
   (`HomeModules.cs:558-560`). `Ensure` reuses the last real cross size and falls back to 1 100 for the same reason
   (`HomePage.cs:1295-1300`).
10. **`WhileHover` targets are DELTAS** on the authored rest pose, not replacement values — `FoldFan` carries the
    difference from the prototype's CSS, never the CSS numbers (`HomeModules.cs:570-579`).

### Where the plan is wrong or too thin for this surface

* **§2's budget is off by ~4×.** `Home.cs 400 + Home.UI.cs 800 + Home.Page.cs 800 = 2 000` lines is supposed to cover the
  page frame, every card skin, every module, the Fold deck, the section page and the customizer. 0.2.9's
  `Features/Home/**` is **7 779 lines** excluding `Persistence/`. This chapter's slice alone (the 16 files above) is
  3 544.
* **§4.12/§4.13 model the wrong shape.** `Track.Row` and `Album.Page` are a uniform bound list and a static vertical
  stack. Home is a **heterogeneous measured virtual list** with a per-row estimator, two documents, and a reveal gate.
  `ItemsView.CreateBound` cannot express it; `Virtual.Measured(count, IMeasuredVirtualLayout, RowAt, KeyAt, overscan)` is
  the API this page needs, and the plan never mentions measured virtual layouts at all.
* **§4.13's "demand the whole model" is right but incomplete for a document subject.** `Entities.Ensure(rows, fields)`
  plans by entity slot; Home must first fetch the *document* (per facet, per epoch) and only then batch its cards. Add
  `Fetch.PlanDocument(Home h)` (or an equivalent) with an epoch and a per-facet inflight slot — the facet race
  (`_facetCts`, "exactly one read in flight, answer publishes only if the strip still says this facet",
  `HomePage.cs:1039-1082`) has no home in §4.5's planner.
* **§4.1's `Publication`/`Changed` gives "something changed", never "settled".** Readiness (§7) needs both the auth phase
  and the document's inflight state. Wire `Spotify.Session.Phase` into the Home gate explicitly — the plan's `Fetch`
  has no notion of "the attempt concluded".
* **No colour anywhere in the data model.** See DATA GAPS. Home is the page that breaks first: three wash legs, a hero
  backdrop, a hero CTA fill, a Fold wash and every card spine all read a per-artwork palette.
* **Was missing from the §2 tree, now placed (A15).** All of these were Home's with no named file: the Home customizer
  page + `home-layout.json` persistence (`HomeCustomizerPage.cs` 310 + `Persistence/*` + `HomeLayoutModel.cs`) →
  `Home.Customizer.cs` (page) + `Home.cs` CORE (document, reducer, wire) + `Home.Host.cs` (the json store, chapter 12);
  the Home/browse section drill page (`HomeSectionPage.cs` 628) → `Home.Page.cs` (chapter 12); the append preloader
  (`HomeSectionAppendPreloader.cs` 96) → `Home.cs` CORE; the notification-center timeline → `Home.UI.cs` + `Home.cs`
  CORE; the `userTopContent` artist podium → `Home.Artists.UI.cs` (chapter 11). Still unplaced and still this bullet's
  point: the Charts taxonomy + `browseSection` reads (`Entities/Browse.cs` — and the Browse **page** is now
  `Entities/Browse.Page.cs`, owner P, chapter 13), `DaylistNotifier` (`App/DaylistNotifier.cs` 129 — OS toast
  scheduling fed by the feed; `Platform/Notify.cs` per A9), and `HomeQuickImageProbe`/`HomeImageDiagnostics`.
* **Real defect #1, to fix not port:** the row GAP is computed from the **outer** row width while the estimator computes
  it from `available = min(cross,1600) − 72` (`HomePage.cs:500` vs `:1321-1322`). For a content pane of 1 080-1 151 DIP
  the renderer uses 40 and the estimator 32 — 8 DIP per row, ~136 DIP of accumulated extent error until every row has
  been measured. In 0.3 compute the gap once, from `available`, and pass it down.
* **Real defect #2, the same class:** the Artists row's estimate adds the SHARED gap (`gap`, 40 / 32 —
  `HomePage.cs:1381`) while the podium pays its own `HomeArtistRowLayout.ModuleGap` (**32 / 24** —
  `HomeModules.Artists.cs:178`, `HomeArtistRowLayout.cs:80`). 8 DIP per Home page, every width. Either the estimator
  reads `HomeArtistRowLayout.ModuleGap` too, or the row stops having a private gap; do not port the disagreement.
* **Real defect #3 (soft):** the Artists estimate hard-codes the rank-1 avatar at **76** (`HomePage.cs:1381`) while the
  renderer scales the whole ramp by `RampScaleFor(...)`, clamped to ×1.0…×1.6 (`HomeArtistRowLayout.cs:48-73`) — at
  A ≈ 1 088 with ten artists that is ≈ ×1.3, so the rendered podium is ~25 DIP taller than the first estimate. The
  measured pass fixes it, but the cold frame is short. In 0.3 estimate through the same `RampScaleFor`.
* **Real defect #4:** the facet estimator's row 0 is unconditional `84 + 40 + 24 + g` under a comment asserting "a facet
  page never has a hero band" (`HomePage.cs:1524-1526`), which `HomeFacetProjection`'s `Hero` arm and `LiveHasHero`'s
  facet branch contradict (`HomeFacetProjection.cs:74-78`, `HomePage.cs:1165-1172`). Read the same predicate in 0.3.
* **Real defect #5:** the chips estimate adds a flat `+40` for the strip even when the feed carries **zero chips**, in
  which case `HomeFacetChips` renders a bare `BoxEl` and the row is 40 DIP shorter than estimated
  (`HomePage.cs:1368` vs `HomeFacetChips.cs:49`). Mirror the chip count into the layout the way `_sectionDeckCount` and
  `SetChartsState` already mirror their inputs.
* **One dead rule:** `HomeModules.ChartEyebrow` (`HomeModules.cs:383-394`) and `FoldDeck`'s `eyebrowOf`/`tileEyebrow`
  parameters have **no caller** — Fold tiles render title-only in both Home and Browse. The design doc
  (`2026-08-18-home-hub-strip-charts.md:308, 350`) and the prototype both show an eyebrow and a count numeral on the
  tile; the code dropped them. **Code wins** — if the eyebrow is wanted back, it is a deliberate change, not parity.
* **Other doc-vs-code drift** (code wins in all cases): the plan doc's Fold constants (`FoldCardHeight 168`,
  `FoldCover 112`, `FoldCardMin 340`, `FoldCardMax 460`) are now 176 / 124 / 440 / 9 999 with `maxColumns: 2`
  (`HomeModules.cs:540-552`); the prototype's `.fold .t { font-weight: 300; font-size: 32px }` is deliberately **not**
  copied (off the 400/600 weight policy and off the ramp) — `WaveeType.FoldTitle` is 28/36/400 (`WaveeType.cs:205-212`);
  the prototype's `.hero-eyebrow { text-transform: uppercase }` is deliberately not honoured (it carries a person's
  name) (`HomeCards.cs:303-307`); `DEFECT_REGISTER.md` D3 still quotes the pre-daylist hero heights 345/305/297 — the
  tests pin 384/344/336.

### Line budget

| | lines |
|---|---|
| 0.2.9, this chapter's 16 files | **3 544** |
| plan §2 target for ALL of Home (`Home.cs` + `Home.UI.cs` + `Home.Page.cs`) | 2 000 |
| honest estimate, this chapter's slice in 0.3 | **2 600 – 3 000** (≈ 500 saved on the loadable/epoch/hydration plumbing; the gate, both projections, both estimators, the hero geometry and the wash are ported 1:1) |
| honest estimate, the whole Home family (ch. 10 + 11 + 12) | **≈ 8 230** — the seven files below, each column an owning chapter's own number. The earlier 7 000 – 8 000 (`Home.cs` ≈ 1 800 + `Home.UI.cs` ≈ 3 500 + `Home.Page.cs` ≈ 2 500 = 7 800) counted **no customizer page and no SHELL store** — that is the +700 — against −270 where this chapter's own slice estimate (top of range, 3 000) is tighter than its share of that split |

### The Home file set (settled 2026-09-12, A15)

Chapters 10, 11 and 12 proposed three different file sets for one owner. The decision is **seven files, all
`Entities/`, all owner P**, each chapter keeping its own line estimate for the parts it owns. The per-chapter columns
are those chapters' own numbers (this chapter's slice at the top of its 2 600 – 3 000 range), so the total carries
their imprecision — it is a budget, not a measurement:

| file | ch. 10 | ch. 11 | ch. 12 | lines | what |
|---|--:|--:|--:|--:|---|
| `Entities/Home.cs` (CORE) | 650 | 450 | 700 | **1 800** | readiness + reveal gate, both projections, facet strip, hero + module + artist-row geometry, wash source, timeline merge, play routing, section cursor / walk / chart filter / routes / grid fit, the layout document + reducer + commands + wire |
| `Entities/Home.UI.cs` | 600 | 1 000 | — | **1 600** | row shell, greeting, facet chips, hero band, Fold tile, module shells, the timeline UI |
| `Entities/Home.Cards.UI.cs` | — | 1 250 | — | **1 250** | the card skin vocabulary |
| `Entities/Home.Artists.UI.cs` | — | 650 | — | **650** | the artist podium + Mixview |
| `Entities/Home.Page.cs` | 1 750 | — | 480 | **2 230** | `Home.Page` (the landing, its measured virtual layout and both estimators) **and** `Home.SectionPage` (the shared home-section / browse-section drill) |
| `Entities/Home.Customizer.cs` | — | — | 300 | **300** | the `home-customize` page |
| `Entities/Home.Host.cs` (SHELL) | — | — | 400 | **400** | `home-layout.json`: the store, its faults, the `.bak` recovery |
| **total** | **3 000** | **3 350** | **1 880** | **8 230** | |

Two files chapter 12 floated do **not** exist: `Screens/HomeCustomize.UI.cs` (the customizer is `Home.Customizer.cs`,
in `Entities/` with the rest of Home) and a separate section-page class (`Home.Page.cs` owns both sources, which was
chapter 12's own recommendation). `Entities/Browse.Page.cs` **does** exist and is owner P's too — chapter 13 §9.

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`.
The `--fake` Home document is `assets/spotify/home.json` (31 sections: greeting "Good morning"; chips
`music-chip` (+ Following) / `podcasts-chip` / `audiobooks-chip`; a 1-card spotlight "New release from KIMMUSEUM";
"Made For Christos" 9; "Recents"; "Jump back in" 9/14; "Recommended Stations" 9/20; "Your top mixes" 9/13; "Popular
radio" 10/50; 20 single-card baseline sections). Browse is `NullBrowseService` under `--fake` **and the fake session
reports `Authenticated`**, so the Charts row resolves **Failed** (the fail-loud arm with a Retry), not Ready-empty —
see W12. Unless stated, use a **maximized 1 440 × 900 window with the sidebar at its default width**
(content pane ≈ 1 160 → A ≈ 1 088) and route `home`.

1. **Cold skeleton silhouette.** Launch both, capture the first frame after the window paints: the shimmer shows a hero
   bone, a weekly pair, a 4-wide quick grid, a recents shelf, a mix band, chip cards, a radio dial, episodes+books,
   podcasts, a Fold-shaped Charts bone. Same rows, same order, same heights.
2. **One reveal.** Frame-record launch→+4 s. The page must go skeleton → (hold) → one staggered reveal. No intermediate
   paint of a partial feed, no row appearing after the reveal.
3. **Reveal stagger.** From the same recording: consecutive realized rows rise 40 ms apart, over 500 ms, with an 8-DIP
   travel and a visible blur→sharp.
4. **No second reveal on refresh.** Wait 60 s on Home (the poll ticks). Nothing re-skeletonizes, nothing re-staggers.
5. **Hard fallback.** Not reachable under `--fake` (the attempt concludes instantly). Verify by inspection that the 8 s
   `UseTimeout` is armed once, with `DepKey.Empty`.
6. **Gutter and cap.** Static capture: 36 DIP from the content-pane edge to the first glyph on both sides. Widen the
   window past ~1 750 DIP of content pane — the row stops growing at 1 600 and centres, while the scrollbar stays at the
   window edge.
7. **First-row inset.** 24 DIP above the greeting/chip row (not 12, not 36).
8. **Module gap.** Measure two adjacent module boundaries at A ≈ 1 088 → 40 DIP; resize to a content pane < 1 080 → 32.
9. **Greeting placement.** With a hero present (default `--fake`), there is **no** standalone greeting block — the
   greeting is the hero's eyebrow.
10. **Greeting text.** Eyebrow reads "Good morning, <display name>" (server greeting wins over the clock). The
    `--fake` hero is a new release, so there must be **no** "· your daylist" tail.
11. **Handle suppression.** Not reproducible under `--fake`; verify by inspection (`LooksLikeHandle`: ≥ 20 chars, no space).
12. **Hero tier Wide.** At A ≥ 980: band height 384, square cover 384, title `ArtistTitle` 48/60/700 over two lines max.
13. **Hero tier Medium.** Narrow the window until A ∈ [700, 980): height 344, cover 344, title 32/40/700. The switch is
    instantaneous (no hysteresis) and the artwork stays square.
14. **Hero tier Narrow.** A < 700: height 336, the cover **centres** and the copy bottom-justifies, the veil runs
    vertically, and the left edge fade disappears.
15. **Hero copy padding.** 48 DIP from the band's left edge to the eyebrow; 44 above it.
16. **Hero artwork fade.** Wide/Medium: the cover's left edge dissolves over 96 DIP. Never a hard seam.
17. **Hero actions.** `Play` (accent capsule on the card's own graded colour), `Shuffle` (standard capsule with the
    shuffle glyph), `♡`, `⋯` — in that order, 32 high, 8 apart, wrapping at narrow widths.
18. **Hero "⋯" opens the band's own menu** (identical to right-clicking the band), not a second menu.
19. **Shuffle ≠ Play.** Press `Shuffle`, then open the player: shuffle is armed. (Under `--fake` playback is rejected with
    the standard toast — compare the toast text and the `card.play` log line in `%LOCALAPPDATA%\Wavee\logs`.)
20. **Hero module header.** "New release from KIMMUSEUM" renders above the band in `ModuleHeader` 20/28 display face.
21. **Facet strip grammar.** Static capture: `All  Music  Podcasts  Audiobooks` as text tabs, 12/8/12/8 padding,
    14/600, a 3-DIP accent underline under the selected tab inset 12 each side, and a 1-DIP divider under the whole strip.
22. **Facet selection ink.** Selected tab is `TextPrimary`, the rest `TextSecondary`; the underline slot is always
    reserved (selecting a tab must not shift the strip's height by one pixel).
23. **Sub-chip spill.** Click "Music": "Following" appears **immediately after Music**, inside its group, as a smaller
    tertiary caption — not appended after the last tab.
24. **Fuse morph.** Click "Following": frame-record. The Music label morphs into the segmented pill in place (width
    reflow, ~260 ms), the ✓Music segment slides in from the right, the sub-token flies −56 DIP out. No pop, no remount.
25. **Fuse step-back.** Click the fused pill: it returns to the bare "Music" tab (not to "All").
26. **Chip row survives the swap.** Switch All → Music → All repeatedly while recording: the chip row never replays an
    entrance animation.
27. **Facet document shape.** Select "Podcasts": the page becomes a plain ordered list of the server's sections, each
    wearing its own title, with the greeting block standing alone above the strip (there is no hero band).
28. **Facet scroll isolation.** Scroll a facet 2 000 DIP down, switch to All: All is at its own offset, not 2 000 down.
29. **Facet failure.** Not reachable under `--fake`; verify by inspection that the strip reverts and a toast fires.
30. **Wash presence.** Static capture of the **window chrome** (title bar area and the sidebar) on Home vs on Settings:
    Home carries three coloured washes, Settings is neutral.
31. **Wash anchoring.** Scroll Home to the bottom: the washes do not move.
32. **Wash and the dock.** The player-dock band carries no gradient peak — the Mix wash is cut at the dock line.
33. **Wash off.** Settings ▸ Appearance ▸ colour washes off, return to Home **without relaunching**: the chrome eases to
    neutral, and Home still owns the slot (navigating to Settings and back does not flash another page's colour).
34. **Wash hand-over.** Navigate album → Home: the chrome cross-fades from the album's tint to Home's washes; it must not
    dip to neutral in between.
35. **Greyscale safety.** Any card whose art is greyscale must not produce a random hue; the leg is omitted.
36. **Fold deck two-up.** At A ≈ 1 088 the Sections deck shows two 538-wide tiles; narrow to A < 892 and it becomes one
    full-width tile.
37. **Fold tile anatomy.** 176 high, radius 8, 1-px card stroke, card fill, title in 28/36/400 display face up to 3 lines,
    three 124-px covers hanging off the right at −11° / +5° / −2°, and **no** eyebrow and **no** count numeral.
38. **Fold hover fan.** Hover capture: the three covers fan by (−10,+6,−5°) / (+2,−6,+3°) / (+10,0,+3°) over 250 ms, the
    plate goes `FillCardSecondary`, and the elevation does **not** change.
39. **Fold rest after park.** Home → Browse → Featured Charts → Back: the covers are at their rest pose (not stuck in the
    hover pose).
40. **Charts row present in its settled state.** Under `--fake` the Charts row shows the module header plus the FAILED
    state ("Something went wrong." / "Check your connection and try again." / `[Retry]`) — 152 DIP, never 0. Confirm
    against 0.2.9's own build rather than against the "No charts right now" copy: both arms are the same extent, and
    which one `--fake` produces depends on whether the fake session reports `Authenticated` (it does, today). The
    Ready-empty arm needs an offline real session.
41. **Charts drill.** Click the Charts module header → `browse:` Charts page with the masthead reading `Home › Browse ›
    Charts` (the origin hand-over).
42. **Sections deck.** "Sections for you" holds only sections the landing did **not** consume; clicking a tile opens
    `home-section:` with the tile's content already painted (no empty grid flash).
43. **Timeline row.** If the notification feeds are empty the row occupies **zero** height and contributes no gap.
44. **Row order.** Top to bottom: chips, hero, weekly, quick, recents, mix band, artists, chip cards, radio,
    episodes+books, podcasts, timeline, charts, sections, editorial, feed, tail.
45. **Customizer reorder.** Hide "Jump back in" in the customizer, return to Home: the row is gone with no hole, and every
    row below moves up by exactly its height + one gap.
46. **Tail.** The page ends with two full-width editorial destinations 20 apart (Concerts, then Browse), then 96 DIP of
    clearance above the player dock.
47. **Empty state.** Requires an empty feed — verify by inspection: the empty page uses the same 36 gutter, 24 top inset,
    and still renders the greeting block and the tail.
48. **Scroll restore.** Scroll Home to ~3 000 DIP, navigate to Search, come back: the offset is restored and no row
    re-reveals.
49. **Scroll smoothness.** Record a continuous wheel scroll from top to tail: no row jumps, no anchor re-pin, no shelf
    shears. (This is the estimator/renderer agreement test — watch the 1 080-1 151 content-pane band in particular,
    see §9's gap defect.)
50. **Allocation-free scroll.** With `FG_FPS_LOG` on, scroll top→tail: the managed-allocation counter stays at 0 in
    frame phases 6-13.
51. **Card menu parity.** Right-click a quick tile, a Fold tile and the hero: the item list, order and icons match 0.2.9
    item-for-item.
52. **Drag exclusions.** A track or episode card must not start a drag; a playlist/album/artist/show card must.
53. **Customize affordance.** The "⋯" sits at the END of the greeting/chip row, is 28 × 28, and opens a single-item
    flyout "Customize Home" anchored bottom-right.
54. **No masthead on Home.** The `Browse ›` band must be invisible (opacity 0) on Home and must not consume any height —
    the first row's top inset stays 24.
55. **Diagnostics.** `%LOCALAPPDATA%\Wavee\logs` must contain exactly one `home.feed.modules` line per distinct feed
    shape (facet included), with the same `modules=` shape string in both builds — and the same `hero:` field
    (`uri | title | fmt=… seeds=N`), `groups=`, `chips=`, `greeting=` (`HomeFeedDiagnostics.cs:37-50`).
56. **Skeleton blanks.** In the cold shimmer the hero's action row is a BLANK 32-DIP band (no four button bones) and no
    card shows a play FAB bone. If either shimmers, `Skeletonized(false)` was lost.
57. **Fold tile bone.** The Charts/Sections shimmer tiles are ONE card-shaped bone each (fill + radius 8, no covers, no
    border) — not a derived tile with three covers stacked at the corner.
58. **Fold tile with one cover.** Force a single-card section (a `--fake` Sections deck entry with one card): exactly
    one cover at rest pose 0, no repeat-padding to three.
59. **Fold tile, no accent.** A section whose first card carries no `extractedColors` paints a FLAT card plate — no
    radial wash, and definitely no grey one.
60. **Hero click target.** Click the hero's copy area (the title, the meta line, empty plate between the tags and the
    buttons): **nothing happens**. Click the cover: it navigates. Right-click anywhere on the plate: the card menu.
61. **Hero slot collapse.** Compare a daylist hero (tags + meta + countdown) against the `--fake` spotlight (none of
    the three): the same 384 band height on the first frame, then the spotlight settles ~108 DIP shorter once measured.
    Nothing half-renders — a missing meta line is omitted, never written blank.
62. **Hero long title.** A ≥ 40-character hero title wraps to two lines and then SHRINKS (48 → down to 40 at Wide,
    32 → 28 at Medium) before it ellipsises.
63. **Hero photo arm.** An account whose spotlight carries `header_image_url_desktop` renders the full-bleed photo
    variant: no square cover, no backdrop gradient, a bottom edge fade instead of a left one, and a 300 ms image fade.
64. **Facet strip wrap.** Narrow the window until the chip row cannot fit: the tabs WRAP to a second line (the strip
    grows), they do not compress or scroll, and each label ellipsises at one line.
65. **Facet strip height stability.** Frame-compare the strip with nothing selected vs "Music" selected: identical
    height (the underline slot is always reserved).
66. **Facet with no chips.** A feed carrying zero chips renders the greeting with NO strip and NO divider; the row is
    40 DIP shorter than the cold estimate for exactly one frame.
67. **Facet with a hero.** Pick a facet whose first section is a one-card spotlight: the greeting block collapses to
    the chip strip alone (no standalone greeting) and the hero band opens the page — the same rule the landing follows.
68. **Empty facet document.** A facet that answers nothing shows the chip strip and the tail with nothing between —
    NOT the "Nothing here yet" page state (that arm is unfiltered-only).
69. **Now-playing paused.** Start a Home context, then pause: the mark stays on the card with its bars held; it does
    not disappear and the row does not reflow.
70. **Artists row gap.** Measure the boundary under "Your top artists" vs under any other module at A ≈ 1 088: 32 vs 40
    in 0.2.9. Decide once for 0.3 and record the answer — do not reproduce the split by accident (§9 defect #2).
71. **Daylist toast.** With `NotifyTopic.DaylistRefresh` on and a daylist window > 2 minutes out, Settings ▸
    Notifications ▸ Send event schedules ONE entry (tag `daylist-roll`); learning the same window twice does not stack
    a second banner, and the toast's activation opens the daylist playlist. Press Send event TWICE — the second press
    must also schedule (the `SimulateSchedule` ms-nudge, `DaylistNotifier.cs:101`). Then turn the dial off: the pending
    entry is REVOKED, not left behind (`RequestReconcile`).
72. **Cold first row.** Under `--fake` the first shimmer row carries NO chip bones and no greeting bones — one 28-DIP
    bone at the RIGHT edge (the customize "⋯") over 24 of inset. If a chip strip shimmers, the seed grew chips it
    never had.
73. **Two Fold shimmers.** Scroll the cold skeleton (it survives ~1 s): BOTH the Charts row and "Sections for you"
    shimmer Fold-shaped decks — 3 seeded Charts tiles, 2 Sections tiles.
74. **Charts empty vs failed are different blocks.** Force each (offline real session vs `--fake`): the empty arm is a
    lone 20/28 Subtitle line ≈ 76 tall; the failed arm is a 28/36 PageHero + caption + `[Retry]` ≈ 160. Do not accept a
    build where both render at one scale — and do not accept a build where the ESTIMATE differs between them (152 both).
75. **`[Retry]` re-shimmers one row.** Press it: the Fold bones return in the Charts row only. The page must not
    re-skeletonize, re-stagger, or move any other row except by that row's 152 → 232 estimate step.
76. **Hero copy runs under the cover.** Give the hero a ≥ 60-character title at A ≈ 1 088: the second line extends past
    the cover's left edge and is veiled, never wrapped early against an invisible column edge. The copy column is
    `A − 96` wide, always.
77. **The heart does nothing visible.** Press `♡` twice on the hero: the glyph is identical before, between and after.
    (Parity, not approval — see §6.)
78. **Podium double-click.** Single-click a top-artist pod (it selects / discloses); click the SAME pod twice within
    400 ms (it navigates to the artist). 401 ms apart must NOT navigate.
79. **Empty / failed pages reveal, not cut.** Force an empty feed and a failed initial read: each rises through the same
    500 ms blurred stagger the loaded page uses — they are branches of one `Skel.Region`, so neither may snap in.
80. **Facet strip reflow is animated on every tab.** Narrow the window until the strip wraps while recording: the tabs
    that move do so over ~260 ms `SmoothOut`, they do not jump to the second line.
81. **Chips row with a hero but no chips.** A feed with a hero and zero chips renders ONLY the right-aligned "⋯"
    (W19 (c)) — not an empty divider, not a 40-DIP hole, not a stray greeting.

---

## 11. Audit log

Adversarial re-read of the 19 assigned 0.2.9 sources plus the geometry / token / engine files they depend on. One line
per correction; every line names what was checked and against what.

| # | § | kind | correction |
|---|---|---|---|
| 1 | §0.12 · W12 · §10.40 | **overclaim** | "Both arms are `FoldStateExtent = 152`, so the row HEIGHT is identical either way — only the copy differs" is false. 152 is the shared ESTIMATE. `onEmpty` is `EmptyState.Compact` (Subtitle 20/28, no caption, no action ⇒ ≈ 76 of content, `EmptyState.cs:43-45`); `onFailed` is `ErrorState.Build` → `EmptyState.`**`Build`** — the PAGE grammar, PageHero 28/36 + caption + a 16 spacer + a 32 `Button.Standard` ⇒ ≈ 160 (`ErrorState.cs:23-27`, `EmptyState.cs:35-63`). Measured, the two rows are ~84 DIP apart. Fixed in §0, W12, §3 and item 40; new item 74 tests it. |
| 2 | W1 | **wrong** | The cold shimmer's first row was drawn as three chip bones + a trailing bone. `FakeData.HomeSeed` passes no `Chips` (the `HomeFeed` record defaults it null, `HomeFeed.cs:130-137`; the seed's ctor stops at `Sections:`, `FakeData.cs:587`) AND carries a `Hero` group, so `GreetingBlock` falls to its customize-only arm (`HomePage.cs:1002-1008`): ONE 28-DIP bone, right-justified. Wireframe corrected, the mechanism written out, item 72 added. |
| 3 | W1 | **missing** | The cold page shimmers TWO Fold-shaped decks, not one. The seed carries a 3-entry `Sections` ledger (`FakeData.cs:605-610`); the podcasts uri is consumed by `PodcastShelf`, leaving 2 tiles in `HomeLanding.Sections`, so "Sections for you" shimmers alongside Charts' 3 seeded tiles. Added, item 73. |
| 4 | W3 · §3 | **missing** | The hero copy column is `max(1, A − 2×48)` — the band's FULL inner measure (`HomeCards.cs:300`) — and the artwork is a ZStack sibling, not a flex peer. Long titles / tags / meta run UNDER the square cover, separated only by the veil; nothing reserves the cover's 384. Added as a §2 note, a §3 row and item 76 — this is the single easiest thing to "fix" into a two-column row and lose the look. |
| 5 | §2 (new W19) | **missing** | `GreetingBlock` has FOUR arms, not two (`HomePage.cs:982-1018`): hero+chips, no-hero+chips, **hero + zero chips ⇒ the customize "⋯" alone, `Justify = End`**, and **no `go` ⇒ a bare `BoxEl`**. Two of the four were undocumented and both are reachable (`--fake`, and any server that sends no `homeChips[]`; `HomeFacetChips.cs:49` degrades (b) into (c)). New wireframe W19 + item 81. |
| 6 | W11 | **wrong** | "A failure only reaches this branch when it is the **initial** read." `failIfInitial: true` is passed at the start of EVERY loop generation (`HomePage.cs:943`), and the loop restarts on every epoch bump and every `AuthState` flip (`:176-184`); the failure is additionally gated on the loadable not already being `Ready` (`:965-969`). Corrected to "first read of a loop generation, and only while the page is not Ready". |
| 7 | §0.2 | **missing** | The 8 s force path's three invariants were unstated: no-op if the loadable already left Pending by ANY path incl. Failed (`:931`); it asserts `liveCatalogConcluded: true` (`:933`); it skips the chrome hold but keeps the epoch + facet gates (`HomeFeedReadiness.cs:80-82`). Added. |
| 8 | §7 | **missing** | `ChromeConcluded`'s actual predicate (`HomePage.cs:136-140`) — a null bridge counts as concluded, and `NotificationFeedState.Idle` (never fetched: offline, `--fake`) counts as concluded; only `Loading` is worth the wait. Every read is `Peek()`; the subscription lives in the effect above it. This is the difference between a 0 ms and a 1 500 ms hold on every offline launch. Added with the port's pseudocode. |
| 9 | §5 | **missing** | The 260 ms `LayoutTransition(Position\|Size, Reflow, Width)` rides `Tab`, not only the fused pill (`HomeFacetChips.cs:135-138`) — every tab's box animates on a wrap, a sub-chip spill or a language change. Row added; item 80 tests it. |
| 10 | §5 | **missing** | `[Retry]` on the Charts row: `reveal: None` + an already-true `_chartsArmed` means one row re-shimmers and the page never re-reveals; the row's estimate steps 152 → 232 in the same frame (`SetChartsState`, `HomePage.cs:453`). Row added + W12 note + item 75. |
| 11 | §5 | **missing** | The empty and failed page states are branches of the SAME `Skel.Region` (`HomePage.cs:840-851`), so they enter through `SkelReveal.StaggerRows` — an empty Home rises, it does not cut in. Row added + item 79. |
| 12 | §6 | **missing** | `♡` is `WaveeCta.Icon(Icons.Heart, onLike)` unconditionally (`HomeCards.cs:349`): no filled/outline swap, no latch, no toast, no library read-back. A real 0.2.9 gap a re-author will otherwise "restore" into a half-state. Documented + item 77. |
| 13 | §6 · §8 | **missing** | The artist podium's hand-rolled **double-click to navigate** — `HomeArtistRowLayout.IsDoubleClick` / `DoubleClickWindowMs = 400` (`HomeArtistRowLayout.cs:84-93`), the one multi-click gesture on Home, pure and testable. Added under Interaction, to the §8 row, and as item 78. |
| 14 | §6 | **missing** | Customize "⋯" is a TOGGLE (a second press closes the open flyout, `HomeCustomizerPage.cs:217`), and the hero BAND is not focusable — only its artwork carries `Role = Button`, so tab never lands on the plate (`HomeCards.cs:389, 413`). Added. |
| 15 | §6 | **missing** | `DaylistNotifier`: the per-toast `policy.Sound` → `Silent()` leg (`:76`); `RequestReconcile` / `Unschedule` — turning the dial down REVOKES a pending toast and remembers nothing, so turning it back up re-arms from the next feed resolve (`:57-61, 106-117`); and `SimulateSchedule`'s ms-nudge, without which a second Send event press is a no-op against the `_scheduledFor` idempotence gate (`:46, 101`). Added; item 71 extended. |
| 16 | §8 | **missing** | `HomeSectionAppendPreloader` was an assigned source with no row anywhere. Added as a "port as-is in shape" entry with its three gates as numbers: near-tail at 1.5 viewport heights, `ArmDelayMs = 300`, `MaxAttempts = 3`, the 24/48-px-floored geometry projector, and the deliberate zero visual footprint (`HomeSectionAppendPreloader.cs:46-95`). |
| 17 | §8 | **missing** | The `HomeArtistRowLayout` row named only "tier 900, hysteresis 24". It also owns `BaseArtSize` 76/60/46, `RampScaleFor` (clamped 1.0…1.6), the PRIVATE `ModuleGap` 32/24 that is §9's defect #2, and the double-click window. Row expanded with line refs. |
| 18 | §3 | **missing** | `EmptyState`'s action arm inserts a `Spacing.L` (16) **spacer box** above the button, and the button is `Button.Standard`, never Accent (`EmptyState.cs:58-63`) — the accent-budget rule made structural. It is 16 DIP of the Charts-failed arm's height. Row added. |
| 19 | §10 | **missing** | Ten new parity items (72-81): the cold first row, the two Fold shimmers, the two Charts grammars, `[Retry]`, the full-width copy column, the inert heart, the podium double-click, the empty/failed reveal, tab reflow, and the hero-without-chips row. |
| 20 | header · §9 tree gaps · §9 line budget | **arbitration** | arbitration 2026-09-12: **A15 settles the Home file set at seven files, all `Entities/`, all owner P** — `Home.cs` (CORE) · `Home.UI.cs` · `Home.Cards.UI.cs` · `Home.Artists.UI.cs` · `Home.Page.cs` · `Home.Customizer.cs` · `Home.Host.cs` — against the three different sets chapters 10, 11 and 12 proposed for one owner. The header target, the "missing from the §2 tree" bullet (every item in it now has a named file) and the line budget are rewritten to it, with a per-file table whose columns are each owning chapter's own estimate. Family total **7 800 → ≈8 230**: +300 for the customizer page and +400 for the SHELL json store, neither of which the old three-file split counted, less −270 where this chapter's own slice estimate is tighter than its share. Chapter 12's `Screens/HomeCustomize.UI.cs` is not taken (the customizer is `Entities/Home.Customizer.cs`), and `Home.Page.cs` owns the section page for both sources — chapter 12's own recommendation. `Entities/Browse.Page.cs` is settled separately (owner P, chapter 13). |

**Verified correct, re-checked against source — no change made.** Hero tiers 700/980 and heights 336/344/384 as the sum
of `2×44 + 24 + title + 12 + 32 + 36 + 40 + 32` (`HomeHeroLayout.cs:20-69`); every §3 geometry constant
(`FoldExtent 232 = 32+176+24`, `FoldStateExtent 152 = 32+96+24`, `FoldCover 124`, `FoldCopyMaxFrac 0.70`,
`FoldCardMin 440` / `Max 9999`, `FoldRest` = `max(0, cardW−210) + {0,44,92}` over `y = 6 + {32,16,2}` at
`rot −11/+5/−2`, `FoldFan` `(−10,+6,−5)/(+2,−6,+3)/(+10,0,+3)`, `Gap` 40/32 at 1080, `SplitEvenMin 1020`,
`EditorialMin 980`, `QuickShown 8`, `ChipCardsShown 6`, `RadioShown 12`, `Queue`/`BooksShown 6`,
`EditorialCompanions 3`, the column queries 1120/780, 1020/680, 1080/620 and 760, `RadioColMin`,
`ShelfCardMin`/`Max` 148/188, `ShelfExtent = 32 + (cardW+72) + 24`, `MediaCard.ShelfHeight = cardW + 72` —
`HomeModules.cs:490-716`, `MediaCard.cs:212`); every row-estimate formula and every typical number in the §3 table,
including Weekly 168, Quick 204, Recents/Podcasts 339 (cardW 171.3 at A = 1088), MixBand 224, ChipCards 284, Radio 368,
EpisodesAndBooks max(358, 434), Timeline 528 and Tail 692 (`HomePage.cs:1316-1403`); the facet estimator's three index
cases and its `metric.Count == 0 ⇒ 0` (`:1516-1551`); `DefaultRows`' 17-row order and the chrome anchors
(`HomeLandingProjection.cs:65-70, 135-181`); both viewports' byte-identical `"home:row:Chips"` and the two scroll keys
(`:476-483, 509, 541-547, 582, 820`); the whole §4.1 wash chain — `Sources` by kind+ordinal, the accent→graded→null
tiers, `PlaneUrl`'s watch guard, `KeyOf`, `Fingerprint` (`HomeWashSource.cs:44-105`) — with all three placements
recomputed from `ShellWashGeometry.Resolve` (0.5188×0.5704 top-left, 0.4512×0.5992 top-right, 1.0×0.4620 bottom, and
their node-relative centres/radii to three places), the four alphas, the own-RGB-at-A0 stop, the `PlayerDock.Reserve`
host inset and the `ShellGround @ 0.03` neutral (`ShellWashGeometry.cs:20-55`, `ShellMaterialLayer.cs:34, 50-132`); the
veil ramps 0.96 / 0.92@30 / 0.35@62 / 0 and 0 / 0.35@45 / 0.78·0.42@82 / 0, and `HomeHeroBackdrop`'s 0.10/0.06 pull
(`Surfaces.cs:173-192, 437-445`); the reveal recipe 0→1 / 8→0 / 3→0 over 500 ms `SmoothOut` at 40 ms stagger with the
reduced-motion early return, and the 250/400 ms shimmer exits (`SkeletonRegion.cs:20-39, 160-187`,
`MotionRecipes.cs:55-70`, `Expressive.cs:14, 17, 19-20, 37`); `MotionTok` 83/150/250/300/200 (`MotionTok.cs:164-169`);
the `Strip` `SegmentedPillStyle` (28/22, `(3,3,10,3)` / `(9,0,9,0)`, gaps 6/4, 14/600, ✓ 11, ✕ 9, `AccentDefault` on
`OnAccent`, 1 px `StrokeControlDefault`, `Radii.Full` capsule, `SegmentHeight/2` segment) and both fuse animations
300/220 ms at ±56 DIP (`ConcertUi.cs:637-671, 1164-1174`, `HomeFacetChips.cs:199-201`); the facet tab's `(12,8,12,8)`,
top-only `(4,4,0,0)`, the 3-DIP underline inset 12 with `(2,2,0,0)` corners and its always-reserved transparent slot,
the wrap-never-compress row, the one-line ellipsised labels and the bare-`BoxEl` zero-chip return
(`HomeFacetChips.cs:104-202`); `HomeFacetStrip`'s All-first / spill-inline / fuse-to-parent ordering
(`HomeFacetStrip.cs:49-73`); `HomeFacetProjection`'s Hero arm, zero-unique-card skip and consecutive-baseline
coalescing (`HomeFacetProjection.cs:41-84`); `HomeFeedReadiness` / `HomeRevealGate` in full, including the
faceted-always-passes leg and the unchanged-epoch-on-withhold invariant (`HomeFeedReadiness.cs:39-164`);
`HomeTimelineMerge`'s `MaxRows 8`, id tie-break, local-midnight buckets and uncapped counts
(`HomeTimelineMerge.cs:54-113`); `ContentHost`'s `MaxEntries: 3` and `PublishesShellMaterial` route list
(`ContentHost.cs:106, 184-186`); `ShellMastheadRegistry` NOT claiming `home` (`NavOrigin.cs:77-82`) and the band's
opacity-0 / `HitTestVisible false` hold (`ShellMastheadBand.cs:56-73`); the customize button's 28×28, `Icons.More` 14,
`Interaction.Subtle`, 200-min flyout, `BottomEdgeAlignedRight`, focus trap and light dismiss
(`HomeCustomizerPage.cs:212-248`); every type style cited (`Eyebrow` 12/16/600 +30, `ArtistTitle` 48/60/700 −20
MinSize 40, `ArtistCompactTitle` 32/40/700 −12 MinSize 28, `PageHero` 28/36/600, `ModuleHeader` 20/28/600 display −6,
`FoldTitle` = `PickQuote` 28/36/400 display −12 — `WaveeType.cs:38-212`); every Spacing rung plus `PageMaxW 1600` and
`SectionGap`/`SectionGapWide` 32/40 (`Spacing.cs:11-22`, `WaveeTokens.cs:65-75`); and all ten `home.*` loc keys quoted
in §2/§6 against `assets/loc/en-US.json:509-576`. The §9 defect register (#1 outer-vs-available gap, #2 Artists 32/24
against 40/32, #3 the un-scaled 76 avatar, #4 the facet row-0 comment, #5 the flat +40 for zero chips) was re-derived
from source and all five stand.

**token-reconcile (2026-09-12):** §4.4's "neutral ground" row gave the two washes as raw `#202020` / `#EDEDED` with no token behind them. Both are `WaveeColors.ShellGround`'s arms (`WaveeTokens.cs:163-170`; light `#EDEDED` = `MicaRef.LightDefault`), so the row now cites the token and keeps the literals. Every other value re-verified against `WaveeTokens.cs` / `Dsl/Spacing.cs` and unchanged.
