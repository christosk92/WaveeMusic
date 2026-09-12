# Home cards and modules (every section/module type) — 0.3 visual fidelity contract

> 0.2.9 sources: `src/apps/Wavee/Features/Home/HomeCards.cs` (1 201) · `HomeModules.cs` (804) ·
> `HomeModules.Artists.cs` (665) · `HomeModules.Timeline.cs` (176) · `HomeFoldTile.cs` (143) ·
> `HomeArtistRowLayout.cs` (93) · `HomeBrowseCards.cs` (93) — **3 175 lines**; plus the direct dependencies
> `HomeHeroLayout.cs` (70), `HomeTimelineMerge.cs` (114), `HomeCardPlayRouting.cs` (14), `HomeModuleCopy.cs` (24),
> `Components/MediaCard.cs` (1 570, ch. 02), `Components/FlipCountdown.cs` (173), `Components/Equalizer.cs`,
> `Design/Surfaces.cs`, `Design/WaveePalette.cs`, `Design/WaveeCta.cs`, `Design/WaveeType.cs`,
> `Wavee.Core/Library/HomeFeed.cs` (the card/group model), and the call site `Features/Home/HomePage.cs:601-759`.
> | 0.3 target: `Entities/Home.Cards.UI.cs`, `Entities/Home.UI.cs`, `Entities/Home.Artists.UI.cs` (+ a CORE
> `Entities/Home.cs` section for the geometry rules) — four of the settled seven-file Home set (arbitration
> 2026-09-12, A15; §9.6) | Wave 5 owner **P**
>
> After Wave 0 every 0.2.9 path below lives at `src/apps/_old/Wavee/<same relative path>`.
> Sibling chapters (cross-referenced, never re-specified here): `00-design-system.md` (tokens, type ramp, cover
> palette, CTAs, motion tokens, reduced motion), `01-track-row.md` (`TrackRow`, the equalizer, the heart),
> `02-cards-and-controls.md` (`MediaCard.Shelf` / `GridCard` / `ApplyCardPhysics`, `PagedShelf`, `Surfaces.Artwork`),
> `10-home.md` (the page frame, the virtual row list, the wash, facets, readiness, the greeting, the row order),
> `12-home-section-and-customizer.md` (the `home-section:` drill page that `SectionGrid` serves, and the customizer),
> `13-search-and-browse.md` (Browse's Charts band and category pages, which reuse `FoldDeck` / `SectionGrid`).
>
> **Ownership line with ch. 10:** ch. 10 owns *where* a module sits, *when* it appears, and the page-level wash.
> This chapter owns *what each module and card looks like* — every skin in `HomeCards`, every shell in `HomeModules`,
> the Fold tile, the timeline and the artist podium — including the hero band skin (`HomeCards.HeroBand`), whose
> placement/readiness ch. 10 §0.11/§4.2 owns.

---

## 0. The non-negotiables

1. **One skin per content shape, not one square card twelve times.** Home carries *thirteen* distinct card skins
   (hero band, weekly card, quick tile, mix segment, chip card, radio row, queue row, book row, feature card, crowd
   row, timeline row, ranked avatar, Fold tile) plus three shelf modules that deliberately reuse `MediaCard.Shelf`.
   Collapsing any of them into "the shelf card" destroys the page (`HomeCards.cs:14-21`).
2. **The spine.** A 2-DIP hairline of the item's OWN colour along the card's bottom edge, and the only place a
   per-item colour touches a card. Exactly seven skins carry it — quick tile, weekly, mix segment, chip card,
   feature, crowd row, feed card — and no tabular row does (`HomeCards.cs:24-27`, `Spine` at `:107`). *Six of those
   seven are live; the seventh (`FeedCard`) has no call site (§9.4), so on screen the spine belongs to six skins.*
   Its hover colour reaches the leaf through the engine's INHERITED interaction progress: the leaf is
   `HitTestVisible = false` and owns no reveal, and the recorder opts a descendant into the cross-fade purely because
   it declares an explicit `HoverFill` (`SceneRecorder.cs:3079-3086`). Drop the explicit `HoverFill` and the hairline
   silently stops reacting to hover — it will NOT fall back to an auto-lighten.
3. **No invented colour, ever.** The spine/wash/numeral paint NOTHING until a real colour exists:
   `Meta.Accent` (the payload's `extractedColors.colorDark`) first, the graded cover's `BackgroundTintedBase` second,
   `null` third — and `null` means paint nothing (`HomeCards.cs:35-58`). A hash palette is used *only* to re-hue an
   already-real but near-neutral (S ≤ 0.08) seed, never to invent one (`HomeCards.cs:63-83`).
4. **Every skin survives a null `Meta`.** `FakeData.HomeSeed` renders the whole tree with blank cards to DERIVE the
   loading shimmer; a dereference in a skin is a crash on the loading path (`HomeCards.cs:30-31`).
5. **The daily-mix band is ONE surface divided into cells, not six cards.** One card plate with `ClipToBounds`, a
   grid at gap 0, per-cell leading rules and a top rule on wrapped rows — the numeral carries the identity
   (`HomeModules.cs:184-214`, `HomeCards.cs:515-580`).
6. **Numbers never reflow.** There is no `font-variant-numeric` in the text seam, so every numeric column reserves a
   fixed-width cell (`Count(text, width)` at `HomeCards.cs:190`; the queue's 56-DIP time cell at `:684`; the
   countdown's 13-DIP digit cells at `FlipCountdown.cs:92`).
7. **A tabular row is transparent; a card is a plate.** `Row()` = no contour, no spine, `Interaction.ListRow`
   (`HomeCards.cs:131`); `Card()` = `Interaction.Card` + `MediaCard.ApplyCardPhysics` — the app-wide −4 DIP hover
   lift and the 0.99 press (`HomeCards.cs:116-128`). No Home skin carries a shadow except the Fold tile.
8. **The renderer and the estimator state the same geometry.** Every column count, card height and module gap is a
   `HomeModuleLayout` member read by BOTH `HomeModules` and `HomeFeedVirtualLayout` (`HomeModules.cs:483-489`). A
   disagreement re-pins the scroll anchor mid-scroll, which reads as the feed jumping under the cursor.
9. **Module headers wear exactly one grammar** — `WaveeType.ModuleHeader` (Subtitle 20/28/600 in Segoe UI Variable
   Display, tracking −6), with the subtitle baked into the SAME paragraph so the 12px run sits on the 20px baseline,
   and a 12-DIP `ChevronRight` only when there is somewhere to drill (`HomeModules.cs:39-82`, `WaveeType.cs:63-99`).
10. **Chrome belongs to the entity, not the skin.** Drag-out and the context menu are applied once, by `Keyed`, to
    every card of every module (`HomeModules.cs:90-98`) — so right-click and drag work on a mix segment, a station
    row, a quick tile and a book row identically.
11. **The podium ramp fills the width it is given.** Rank encodes scale (76/60/46 at scale 1) and the whole ramp is
    multiplied by a measured-width scale clamped to [1.0, 1.6], solved against the ramp's own AVERAGE box
    (`HomeArtistRowLayout.cs:41-75`). Every pod reserves the tallest avatar's slot so all labels land on one line
    (`HomeCards.cs:1003-1007`).
12. **The timeline is a chronology, not another shelf.** A fixed 96-DIP day column, a 1-DIP rule down the rows, and a
    7-DIP pip with a 1.5-DIP ring straddling that rule at `margin-left −4` — filled accent when unread, hollow once
    seen (`HomeModules.Timeline.cs:30/58-79`, `HomeCards.cs:934-955`).
13. **The Fold tile's covers hang off the card and fan on hover.** Up to three 124-DIP covers at authored rest poses
    (rot −11°/+5°/−2°), fanned by DELTAS on hover, with the copy column painted LAST over them
    (`HomeFoldTile.cs:59-116`, `HomeModuleLayout.FoldRest/FoldFan` at `HomeModules.cs:556-579`).
14. **The now-playing mark is on every skin that has room**, collapsing to zero width when the card is not the
    sounding context, and it never re-renders the card on an unrelated track skip (`HomeCards.cs:92-97`, `:1091-1134`).
15. **Hover-only affordances are not skeleton content.** `HoverPlay` and the hero action row are
    `.Skeletonized(false)`, so the derived shimmer never draws a play puck or a CTA row (`HomeCards.cs:213`, `:356`).

---

## 1. Anatomy

### 1.1 The 0.2.9 composition

```
Home feed row (ch. 10 owns the row shell: max 1600, gutter 36)
│
├── A · HERO BAND ───────────────────────────────────────────────────────────────────────────────────────────
│   HomeModules.SourceModule(g, Responsive.Of(w => HomeCards.HeroBand(...)))   HomePage.cs:676-685 — module wrapper
│   └ HomeCards.HeroBand                          HomeCards.cs:276   ZStack surface, r-card 8, 1px StrokeCardDefault
│     ├ artwork | media                           HomeCards.cs:408 / :386   square cover (edge = height) OR full-bleed photo
│     │  └ HomeCards.Art → LikedSongsArtwork.For ?? Surfaces.Artwork   HomeCards.cs:143   decodePx 512 / clamp(w,320,1920)
│     ├ veil (Surfaces.ArtistHeroVeil)            HomeCards.cs:374   4-stop copy protection, H or V axis
│     └ foreground → copy column                  HomeCards.cs:298   padding 48 × 44
│        ├ WaveeType.Eyebrow(greeting)            HomeCards.cs:308   Caption 12/16/600, tracking 30, TextTertiary, mb 8
│        ├ title (tier-selected face)             HomeCards.cs:281-286/:313   ArtistTitle 48/60 | Compact 32/40 | PageHero 28/36
│        ├ tag run (Meta.Seeds, max 6)            HomeCards.cs:318 → Tag() :171   bordered Caption chips, wrap, gap 4, mb 12
│        ├ meta line (CardMeta)                   HomeCards.cs:326   Body 14/20 TextSecondary, 2 lines, mb 16
│        ├ pulse: FlipCountdown (daylist only)    HomeCards.cs:289   Key = uri + ":" + ExpiresAtMs; 28-DIP row, mb 12
│        └ actions (Skeletonized false)           HomeCards.cs:341   Accent Play · Shuffle pill · ♥ icon · "…" (requestsContext)
│
├── A2 · WEEKLY PAIR ────────────────────────────────────────────────────────────────────────────────────────
│   HomeModules.WeeklyPair                        HomeModules.cs:129   Responsive.Of → Grid(Columns(WeeklyPair,w), 12, 12)
│   └ HomeCards.WeeklyCard                        HomeCards.cs:440   Card(r-card 8, spine)
│     ├ Art 56 r-ctrl (decode 128)                HomeCards.cs:449
│     ├ Titled(BodyLarge 18/24/600 display, −8)   HomeCards.cs:457   + now-playing mark 12
│     ├ Caption 12/16 TextSecondary, 2 lines      HomeCards.cs:462   FirstSentence(Subtitle) — plain text, first sentence
│     └ Count(TrackCount)                         HomeCards.cs:469   Caption tertiary, no fixed width
│
├── B · JUMP BACK IN (QuickGrid) ────────────────────────────────────────────────────────────────────────────
│   HomeModules.Quick                             HomeModules.cs:139   Take(8) → Grid(Columns(QuickGrid,w), 12, 12)
│   └ HomeCards.QuickTile                         HomeCards.cs:490   Card(r-ctrl 4, spine, height 56)
│     ├ Art 56 flush-left (decode 128)            HomeCards.cs:498
│     ├ BodyStrong 14/20, 2 lines, margin 12/12   HomeCards.cs:499
│     ├ HomeNowPlaying.Mark(12)                   HomeCards.cs:506
│     └ HoverPlay(28, solid accent) pad-right 12  HomeCards.cs:507
│
├── C · RECENTS RAIL ────────────────────────────────────────────────────────────────────────────────────────
│   HomeModules.Recents                           HomeModules.cs:163   PagedShelf.Create (ch. 02)
│     cards MediaCard.Shelf(circular: Kind==Artist)  :171   height ShelfHeight(cardW)=cardW+72, min 148 max 188, gap 12, fade 24
│     header ModuleHeader(title, null, null, openAll)  :175   drills to the app's own "recents" route, armed unconditionally
│
├── D · DAILY-MIX BAND ──────────────────────────────────────────────────────────────────────────────────────
│   HomeModules.MixBand                           HomeModules.cs:187   ONE plate: r-card 8, FillCardDefault, 1px stroke, clip
│   └ Ui.Grid(StarTracks(columns), 0, 0)          HomeModules.cs:211
│     └ HomeCards.MixSegment ×N                   HomeCards.cs:522   ZStack cell, FillSubtleTransparent→FillSubtleSecondary
│       ├ wash leaf (accent @ A 0.09)             HomeCards.cs:553 → HomeAccentLeaf.Wash :1180
│       ├ content (padding 16, gap 2)             HomeCards.cs:524
│       │  ├ numeral leaf Title 28/36 display −40 HomeCards.cs:530 → HomeAccentLeaf.Numeral :1187
│       │  ├ Titled(Eyebrow "DAILY MIX", 10)      HomeCards.cs:531
│       │  └ seeds Caption 12/16, 3 lines, mb 2   HomeCards.cs:535
│       ├ Spine(c)                                HomeCards.cs:559
│       ├ leading rule 1 DIP (i % cols != 0)      HomeCards.cs:561
│       └ top rule 1 DIP (i >= cols)              HomeCards.cs:566
│
├── E · TOP-ARTIST PODIUM + DISCLOSURE ──────────────────────────────────────────────────────────────────────
│   HomeArtistRow : Component                     HomeModules.Artists.cs:30   fixed row, own resources, own module gap
│   ├ Surfaces.SectionHeader(title, sub)          :168   "Your top artists" / "Last 4 weeks · N tracked · select one"
│   └ card (r-card 8, FillCardDefault, 1px, CardResizeHeight)  :139
│     ├ podium (wrap row, gap 8, padding 12)      :131
│     │  └ HomeCards.RankedAvatar ×N              HomeCards.cs:991   slot-reserved column, rank plate, ring when hub
│     └ disclosure (Key = uri, PageSlideForward/Back)  :148-160
│        ├ 1-DIP divider                          :156
│        └ Disclosure(...)                        :209   Skel.Region(StaggerRows, HomeSkeleton.Group) unless warm
│           ├ TopTracks (left, Grow)              :278   head + 5 × TrackRow.Row (ch. 01), cols 36/28/32/★/84/52/160
│           └ MixviewPanel (right, 342 fixed)     :404   ring (tier Wide) or spine (tier Spine)
│
├── F · CHIP CARDS ──────────────────────────────────────────────────────────────────────────────────────────
│   HomeModules.ChipCards                         HomeModules.cs:224   Take(6) → Grid(Columns(ChipCards,w), 12, 12)
│   └ HomeCards.ChipCard                          HomeCards.cs:586   Card(r-ctrl 4, spine), padding 12, gap 12
│     ├ Art 64 r-ctrl (decode 128)                HomeCards.cs:595
│     ├ Titled(BodyStrong 14/20)                  HomeCards.cs:601
│     ├ ChipRun(Seeds, 3) ELSE Desc(2 lines)      HomeCards.cs:605 → Chip() :159   filled FillSubtleSecondary chips
│     └ Count("{n} songs")                        HomeCards.cs:608
│
├── G · RADIO DIAL ──────────────────────────────────────────────────────────────────────────────────────────
│   HomeModules.Radio                             HomeModules.cs:237   Take(12) → Grid(RadioColumns(w), colGap 24, rowGap 0)
│   └ HomeCards.RadioRow                          HomeCards.cs:620   Row(height 48), padding 8/0
│     ├ Art 32 ROUND (decode 64)                  HomeCards.cs:633
│     ├ TwoLine(BodyStrong / Caption secondary)   HomeCards.cs:634   seeds joined ", " else plain-text description
│     ├ HomeNowPlaying.Mark(11)                   HomeCards.cs:635
│     └ HoverPlay(24, ghost)                      HomeCards.cs:636
│
├── H1 · UP NEXT (episodes) ─────────────────────────────────────────────────────────────────────────────────
│   HomeModules.UpNext                            HomeModules.cs:249   Take(6), Gap 0 column
│   └ HomeCards.QueueRow                          HomeCards.cs:647   Row(auto), padding 8, bottom divider except last
│     ├ art 56×32 (16:9, HasVideo) | 32×32        HomeCards.cs:652-663   + ResumeHairline when ResumeMs > 0
│     ├ TwoLine(title / ["Video"] + show)         HomeCards.cs:681
│     └ 56-DIP time cell: "{n} min" / state word  HomeCards.cs:684
│
├── H2 · AUDIOBOOKS ─────────────────────────────────────────────────────────────────────────────────────────
│   HomeModules.Audiobooks                        HomeModules.cs:266   Take(6), Gap 2 column
│   └ HomeCards.BookRow                           HomeCards.cs:729   Row(auto), padding 8
│     ├ Art 48 r-ctrl (decode 128)                HomeCards.cs:754
│     ├ TwoLine(title / Author ?? Subtitle)       HomeCards.cs:755
│     ├ HomeNowPlaying.Mark(11)                   HomeCards.cs:756
│     └ rating cluster: RatingControl(readOnly) + "4.57" + Count(Hours, width 32)   HomeCards.cs:734-746
│   H1+H2 pairing: HomeModules.SplitEven / SplitSingle   HomeModules.cs:280 / :287
│
├── I · WHAT'S-NEW TIMELINE ─────────────────────────────────────────────────────────────────────────────────
│   HomeTimeline : Component                      HomeModules.Timeline.cs:26   reads NotificationCenterBridge.Items
│   ├ Surfaces.SectionHeader + InfoBadge.Count    :89-91
│   └ per day group (row, gap 12, align start)    :51
│     ├ day column 96, align end, pad-top 12      :58   Caption 600 secondary + Caption tertiary "N days ago"
│     └ rows column with the 1-DIP rule           :69-77
│        └ HomeCards.TimelineRow ×N               HomeCards.cs:902   ZStack(Row + pip)
│          ├ Surfaces.Artwork 40 (square | round) HomeCards.cs:913
│          ├ BodyStrong title                     HomeCards.cs:920
│          ├ KindTag + meta Caption secondary     HomeCards.cs:927 → KindTag :959
│          ├ NewPill (unread) | Count("Seen")     HomeCards.cs:931 → NewPill :971
│          └ pip 7 DIP, ring 1.5, margin-left −4  HomeCards.cs:942-953
│
├── J · EDITORS' PICKS ──────────────────────────────────────────────────────────────────────────────────────
│   HomeModules.Editorial                         HomeModules.cs:296   TwoColumn(1.08 : 1, gap 16) above 980
│   ├ HomeCards.FeatureCard (left)                HomeCards.cs:767   Card(r-card 8, spine), padding 20, gap 20
│   │  ├ Art 148 r-card (decode 256)              HomeCards.cs:778
│   │  ├ "Editorial" tag, accent-lerped border    HomeCards.cs:786-800
│   │  ├ Titled(Subtitle 20/28 display, −12)      HomeCards.cs:801
│   │  ├ Desc(3 lines, RichText)                  HomeCards.cs:808
│   │  ├ spacer (Grow) — footer pins to bottom    HomeCards.cs:811
│   │  └ WaveeCta.Play(accent) + Count(meta)      HomeCards.cs:816
│   └ column of 3 × HomeCards.CrowdRow (right)    HomeCards.cs:827   Card(r-ctrl 4, spine, Grow), padding 12, gap 8 stack
│      ├ Art 48 · BodyStrong · Desc(1 line) · HoverPlay(30, ghost)
│
├── K · DISCOVER FEED / PODCASTS / FACET SHELF ─────────────────────────────────────────────────────────────
│   HomeModules.Feed / .Podcasts / .Shelf         HomeModules.cs:398 / :322 / :344   PagedShelf of MediaCard.Shelf
│     Feed subtitle: "N recommendations, each with its own reason"; card subtitle = Subtitle ?? Eyebrow
│   (HomeCards.FeedCard, HomeCards.cs:859, is the AUTHORED but UNREFERENCED card form — see §9)
│
└── L · FOLD DECK (Charts + Sections for you) ──────────────────────────────────────────────────────────────
    HomeModules.FoldDeck                          HomeModules.cs:363   PagedShelf rows 1, maxColumns 2, min 440, max ∞
    └ HomeFoldTile.Create                         HomeFoldTile.cs:26   ZStack card 176 high, r-card 8, Elevation.Card
      ├ radial wash (first card's accent)         HomeFoldTile.cs:40   0.22 → 0 at 72%, centre (1.0, 0.48), radius (0.70,1.10)
      ├ covers ×≤3 at FoldRest, WhileHover FoldFan  HomeFoldTile.cs:65   124 DIP, r-ctrl, Elevation.Card, hit-test off
      ├ copy column (Justify End, gap 4, maxW 70% × cardW)  HomeFoldTile.cs:105-116
      │  pad = Edges4(L 20, T 20, R 12, B 18) — `Edges4` is POSITIONAL (L,T,R,B); the CSS `20 12 18 20`
      │  (T R B L) is the same four numbers RE-ORDERED, not copied through. Mirror it and the title shifts.
      │  ├ Eyebrow (AccentTextPrimary) — NULL at every live call site today
      │  └ WaveeType.FoldTitle = Title 28/36 display 400, −12, 3 lines
      └ .Skel(bone) — one flat r-card plate at the fitted width (FoldCardMin 440 when cardW ≤ 0)   HomeFoldTile.cs:134
```

Shared leaves used by the skins above: `Titled` (`:93`), `Spine` (`:107`), `Card` (`:116`), `Row` (`:131`),
`Art` (`:143`), `Chip` (`:159`), `Tag` (`:171`), `Count` (`:190`), `HoverPlay` (`:200`), `TwoLine` (`:219`),
`Sub` (`:232`), `Desc` (`:241`), `HomeNowPlaying.Mark` (`:1103`), `HomeAccentLeaf` (`:1146`).

### 1.2 The same tree in 0.3 terms

Target files `Entities/Home.Cards.UI.cs` (the skin vocabulary), `Entities/Home.UI.cs` (module shells, the Fold tile,
the timeline UI) and `Entities/Home.Artists.UI.cs` (the podium + Mixview), plus a CORE block in `Entities/Home.cs`
for the pure geometry (§8) — the split this chapter asked for in §9.3 #1, settled 2026-09-12 (A15). Where a row
below says `Home.UI.cs`, read it as "the Home UI partial that owns that thing" per §9.6's table. The 0.2.9
`HomeCard` record becomes a **card handle** — a section-edge target slot plus the edge payload (§7) — so every
function below takes a handle (or a span of handles) and reads columns, never a materialised record.

| 0.2.9 node | 0.3 form | Inputs | How data changes reach it |
|---|---|---|---|
| `HomeCards.*` skins | `static Element Home.Card<Skin>(in HomeCardRef c, …)` — pure static functions over a handle, in `Home.UI.cs` | `HomeCardRef` (section slot + index), callbacks | Parent re-renders on the Home table's `Changed` signal; per-card colour arrives through the accent LEAF (below), not through a parent re-render |
| `HomeModules.*` shells | `static Element Home.Module<Kind>(in HomeSectionRef s, in HomeCallbacks cb, float width)` — pure functions of (section, callbacks, width) | section handle, width from `Responsive.Of` | Same: pure functions, re-invoked by the page |
| `HomeAccentLeaf` (spine/wash/numeral) | `sealed class Home.AccentLeaf : Component` — **must stay a component** | props: `(HomeCardRef, Kind, string? text)` | `UseSignal` on the cover-palette watch for THIS image only. A page-scope epoch re-renders every shelf; keep the narrow watch (`HomeCards.cs:1136-1145`) |
| `HomeNowPlaying.Mark` | `sealed class Home.NowPlayingMark : Component` — **must stay a component** | props: `(StringId uri, float height)` | Reads `Playback.HasActiveContext` first, bails, else bridges `Identity`+`IsPlaying` into ONE coarse `(here, playing)` signal whose setter suppresses on equality (`HomeCards.cs:1116-1125`) |
| `HomeArtistRow` | `sealed class Home.ArtistRow : Component` | none (reads `Entities`/`Playback` contexts) | `UseState` for the selected index + hub; `UseResource` for the top ranking and the hub artist |
| `MixviewPanel` | `sealed class Home.MixviewPanel : Component` | `MixviewProps(hub, related, tier, onHubChanged, go)` — a record, so an unchanged re-render short-circuits | Props are **pushed down** from the row; `onHubChanged` MUST be a `UseRef`-backed stable forwarder or the record's reuse check never hits (`HomeModules.Artists.cs:66-74`) |
| `HomeTimeline` | `sealed class Home.Timeline : Component` | none | Subscribes the notification feed signal; pure `HomeTimelineMerge.Build` over it |
| `HomeFoldTile.Create` | `static Element Home.FoldTile(in HomeSectionRef s, float cardW, string? eyebrow, Action<HomeSectionRef> open)` | section handle + fitted width | A static factory; the shelf rebuilds it per fit. Keep the `Key` carrying `cardW` (`HomeFoldTile.cs:126`) |
| `HomeModuleLayout` | `static class Home.Layout` in `Entities/Home.cs` (CORE, BCL-only, test-included) | — | Constants + pure functions, ported verbatim (§8) |

**Props freeze at mount — the three places it bites on this surface:**

1. `FlipCountdown` is keyed `uri + ":" + ExpiresAtMs` (`HomeCards.cs:295`). A new daylist window must REMOUNT it;
   without the key the strip counts down to a dead deadline.
2. `MixviewPanel` is keyed `"mixview:" + ownerUri` (`HomeModules.Artists.cs:222`) so a new podium pick remounts and
   the node double-click latch resets; but the HUB inside it changes by PROPS, not by key, so recentring cross-fades
   instead of blanking.
3. `PagedShelf` is a Component: its item list and card factory are mount-time configuration. Home keys the shelves
   whose data can change under them on a deep content fingerprint — but only TWO of them actually do today: `Feed`
   carries `Key = SourceGroupKey(group) + ":feed"` (`HomeModules.cs:415`) and `FoldDeck` carries
   `Key = SectionSetKey(sections) + ":fold"` (`:374`). `Recents`, `Podcasts` and `Shelf` carry **no shelf-level `Key`
   at all** — only the per-card `keyOf: SourceCardKey` (`:178`, `:335`, `:357`) — so a refreshed group leaves their
   frozen mount-time item list on screen. Both fingerprints are memoised in a `ConditionalWeakTable`
   (`:732-744`) because `KeyAt` runs on the scroll-hot path. **Port the memoisation with the key — and give the other
   three shelves the same key rather than reproducing the hole.**

---

## 2. Wireframes

Scale: ~8 DIP per character. `A` = the module's own content width (ch. 10: `min(window − chrome, 1600) − 72`).
Typical desktop `A ≈ 1088`. Every threshold below is from `HomeModuleLayout.Columns` (`HomeModules.cs:613-626`) or
the named constant beside it.

### W1 — Hero band, Wide tier @ A = 1088 (A ≥ 980 → `HomeHeroLayout.WideWidth`)

Height 384 (`2×44 + (16+8) + 2×60 + 12 + (20+12) + (20+16) + (28+12) + 32`), artwork edge = height = 384.

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐ ─┐
│ ← 48 →                                                                        ╔══════════════ 384 square cover ══════════════════╗  │  │
│                                                                               ║  EdgeFade(Left, 96)  over  HomeHeroBackdrop      ║  │  │
│  Good morning, Christos · your daylist          Caption 12/16/600 tracking 30  ║  wash: FillCardDefault lerped 10%/6% to accent    ║  │  │
│  ↕ 8                                            Tok.TextTertiary               ║                                                  ║  │  │
│                                                                               ║   [ the COMPLETE square cover — never cropped ]   ║  │ 384
│  daylist                                        ArtistTitle 48/60/700          ║                                                  ║  │  │
│  wistful bedroom pop tuesday afternoon          display face, −20 tracking     ║                                                  ║  │  │
│  ↕ 12                                           wrap, max 2 lines             ║                                                  ║  │  │
│  ┌ bedroom pop ┐ ┌ wistful ┐ ┌ tuesday ┐        Tag(): 1px StrokeControl,      ║   ← the ArtistHeroVeil (H axis) lies over the    ║  │  │
│  └────────────┘ └─────────┘ └─────────┘         r-ctrl, Caption, pad 8/2       ║     whole surface: 0.96 / 0.92 / 0.35 / 0 →      ║  │  │
│  ↕ 12   (wrap, gap 4, max 6 seeds)                                            ║                                                  ║  │  │
│  50 songs · by Spotify                          Body 14/20 TextSecondary       ║                                                  ║  │  │
│  ↕ 16                                                                         ║                                                  ║  │  │
│  0 3 : 2 1 : 4 4   Next update at 18:00         FlipCountdown 28 row,          ║                                                  ║  │  │
│  ↕ 12  (13-DIP digit cells, 8-DIP colons)       display 20/300 TextInk(accent) ║                                                  ║  │  │
│  ( ▶ Play )( ⇄ Shuffle )( ♥ )( … )              36-DIP capsules, gap 8         ║                                                  ║  │  │
│                                                 accent Play on the card accent ╚══════════════════════════════════════════════════╝  │  │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘ ─┘
   r-card 8 · 1px Tok.StrokeCardDefault · ClipToBounds · copy padding 48 × 44 · Justify Center (not stacked)
```

### W2 — Hero band, Medium tier @ A = 820 (700 ≤ A < 980)

Identical anatomy; title becomes `ArtistCompactTitle` 32/40/700 (−12), height 344, artwork 344 square.

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────┐ ─┐
│  Good morning, Christos · your daylist                          ╔═══════════ 344 square cover ═════════════╗  │  │
│  daylist                                     Title 32/40/700     ║  EdgeFade(Left, 96)                     ║  │ 344
│  wistful bedroom pop tuesday                                     ║                                         ║  │  │
│  ┌ bedroom pop ┐ ┌ wistful ┐                                     ║                                         ║  │  │
│  50 songs · by Spotify                                           ║                                         ║  │  │
│  0 3 : 2 1 : 4 4  Next update at 18:00                           ║                                         ║  │  │
│  ( ▶ Play )( ⇄ Shuffle )( ♥ )( … )                               ╚═════════════════════════════════════════╝  │  │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────┘ ─┘
```

### W3 — Hero band, Narrow tier @ A = 640 (A < 700) — STACKED

`metrics.Stacked` flips three things: `Justify = End` (copy sits at the bottom), the veil axis becomes Vertical
(0 / 0.35 @45% / 0.78 dark · 0.42 light @82% / 0), the artwork is centred with NO EdgeFade, and the title drops to
`WaveeType.PageHero` 28/36/600. Height 336, artwork 336.

```
┌──────────────────────────────────────────────────────────────────────────────┐ ─┐
│                   ╔══════════ 336 square cover, centred ══════════╗           │  │
│                   ║                                                ║          │  │
│                   ║   (vertical veil: transparent at the top,      ║          │ 336
│                   ║    0.78 dark / 0.42 light at 82%, 0 at the seam)║         │  │
│  Good morning, Christos · your daylist                                        │  │
│  daylist wistful bedroom pop            PageHero 28/36/600                     │  │
│  ┌ bedroom pop ┐ ┌ wistful ┐                                                   │  │
│  50 songs · by Spotify                                                         │  │
│  0 3 : 2 1 : 4 4  Next update at 18:00                                         │  │
│  ( ▶ Play )( ⇄ Shuffle )( ♥ )( … )                                             │  │
└──────────────────────────────────────────────────────────────────────────────┘ ─┘
```

### W4 — Hero band, authored desktop header photo (`Meta.HeaderImageUrl`)

The square cover and `HomeHeroBackdrop` are BOTH dropped; the surface becomes media → veil → copy, with an
`EdgeFade(Bottom, 96)` on the media and `ImageTransition.Fade(300)` on arrival (`HomeCards.cs:382-405`).

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│▓▓▓▓▓▓▓ full-bleed header photo, ImageFit.Cover, aspect = width/height, decodePx = clamp(width, 320, 1920) ▓▓▓▓▓▓│
│▓▓▓▓ veil (same axis rule as W1/W3) ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓│
│  Good afternoon, Christos                                                                                      │
│  New Music Friday                                                                       ← identical copy column│
│  ( ▶ Play )( ⇄ Shuffle )( ♥ )( … )                                                                             │
│░░░░ EdgeFade(Bottom, 96) ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### W5 — Hero band, non-daylist (no countdown) and skeleton

The pulse slot is ALWAYS in the children list; a card with `ExpiresAtMs ≤ 0` puts an empty `BoxEl` there
(`HomeCards.cs:289-296`) — the reserved 40 DIP (`PulseBlock`) stays in `ContentHeight`, so the surface height never
changes between a daylist and a spotlight hero. Skeleton: the derived shimmer paints the cover square, the eyebrow /
title / tag / meta bars — and NOT the action row (`.Skeletonized(false)`, `HomeCards.cs:356`).

```
┌────────────────────────────────────────────────────────────────────────────┐   SKELETON (derived from the same tree)
│  ▁▁▁▁▁▁▁▁▁▁▁▁                                   ╔═══ cover ═══╗            │   bars: Tok.FillSubtleSecondary, r 4,
│  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁                     ║  breathing  ║            │   1 s breathe 1.0↔0.5 (CoverShimmer)
│  ▁▁▁▁▁▁▁  ▁▁▁▁▁                                 ║  placeholder║            │   text bar width = 0.72 × slot
│  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁                               ║             ║            │
│  (pulse row reserved, empty)                     ╚═════════════╝            │   no Play/Shuffle/♥/… bars
└────────────────────────────────────────────────────────────────────────────┘
```

### W6 — Weekly pair @ A = 1088 (A > 760 → 2 columns), card 538 × 88, one card hovered

```
┌ Discover Weekly ▸  ·  2 playlists ──────────────────────────────────────────────────────────────────────────────┐   header: ModuleHeader
│                                                                                                                 │   20/28/600 display −6
├────────────── 538 ──────────────┐ ←12→ ┌────────────── 538 ──────────────┐                                      │   + Caption tertiary in
│ ┌────┐  Discover Weekly      30 │      │ ┌────┐  Release Radar        30 │                                      │   the SAME paragraph
│ │ 56 │  Your weekly mixtape of  │      │ │ 56 │  Catch all the latest    │  ← BodyLarge 18/24/600 display, −8   │
│ │r-4 │  fresh music. Enjoy new  │      │ │    │  music from artists you  │    Caption 12/16 secondary, 2 lines  │
│ └────┘  discoveries...          │      │ └────┘  follow...               │    Count: Caption tertiary           │
│ ▔▔▔▔▔▔▔▔▔ spine 2 DIP ▔▔▔▔▔▔▔▔▔ │      │ ▔▔▔▔▔▔▔▔▔ spine 2 DIP ▔▔▔▔▔▔▔▔▔ │                                      │
└─────────────────────────────────┘      └─────────────────────────────────┘                                      │
  padding 16 · gap 12 · r-card 8 · height 56 art vs (24 + 2×16) text = level      HOVER (left card):               │
                                                                                  · plate FillCardDefault→Secondary│
                                                                                  · lift OffsetY −4 (250 ms)       │
                                                                                  · spine Hairline→HairlineHover   │
                                                                                  · paints above its neighbours    │
└─────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### W7 — Weekly pair @ A = 700 (A ≤ 760 → 1 column), card 700 × 88, stacked with a 12-DIP row gap.

### W8 — Jump back in @ A = 1128 (A > 1120 → 4 columns), tile 273 × 56, second tile hovered

```
┌ Jump back in ▸  ·  Your 8 most-opened of 42 ────────────────────────────────────────────────────────────────────┐
│┌─────── 273 ───────┐┌─────── 273 ───────┐┌─────── 273 ───────┐┌─────── 273 ───────┐                             │
││[56]  Liked Songs  ││[56]  Chill Mix ▶  ││[56]  Blonde       ││[56]  Radiohead    │  tile height = 56 exactly:   │
││     (2 lines max) ││          ↑        ││                   ││                   │  the cover IS the tile       │
│└▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔┘└▔▔▔▔ hover play ▔▔┘└▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔┘└▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔┘  r-ctrl 4 · spine 2            │
│ ←12→                                                                             art flush to the leading edge  │
│┌───────────────────┐┌───────────────────┐┌───────────────────┐┌───────────────────┐   title margin 12 | 12       │
││[56]  ...          ││[56]  ...          ││[56]  ...          ││[56]  ...          │   HoverPlay 28 solid accent, │
│└▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔┘└▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔┘└▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔┘└▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔┘   right pad 12, opacity 0→1   │
└─────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
   row gap 12 (HomeModuleLayout.RowGap: QuickGrid/WeeklyPair/ChipCards = 12)
```

Thresholds: `> 1120 → 4` · `> 780 → 3` · else `2` (`HomeModules.cs:618`). At A = 1088 → 3 columns of 354.7.
**No hysteresis** on any module column count — the count flips exactly at the threshold (see §9 traps).

### W9 — Recents rail (PagedShelf) @ A = 1088 → 6 cards of 171.3, header hovered

```
┌ Recents ▸                                                                                    ( ‹ )  ( › )  ┐   chevrons: 32 × 32 CIRCLES
│                                                                                              ↑ ALWAYS painted│   (r 16), Tok.FillControl-
│┌─ 171 ─┐ ┌─ 171 ─┐ ┌─ 171 ─┐ ┌─ 171 ─┐ ┌─ 171 ─┐ ┌─ 171 ─┐░░ EdgeFade(Right, 24)              opacity 1 / .35│   Default plate, hover
││ ▢▢▢▢▢ │ │  ◯◯◯  │ │ ▢▢▢▢▢ │ │ ▢▢▢▢▢ │ │  ◯◯◯  │ │ ▢▢▢▢▢ │░░                                  when disabled │   FillControlSecondary,
││ cover │ │ ARTIST│ │ cover │ │ cover │ │ ARTIST│ │ cover │░░                                  header gap 8  │   glyph 13 TextSecondary
│└───────┘ └───────┘ └───────┘ └───────┘ └───────┘ └───────┘░░   card height = cardW + 72 = 243 │   (ch. 02 owns MediaCard)
│  Title     Title     Title     Title     Title     Title                                      │
│  Playlist  Artist    Album     Playlist  Artist    Album      ← KindLabel, not Subtitle        │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Artists are CIRCULAR here and in the facet `Shelf`; nowhere else on Home. No module subtitle (deliberate).

**The pager chevrons are `PagedShelf`'s own, not a Wavee control.** `PagedShelf.Chevron`
(`fluent-gpu/src/FluentGpu.Controls/PagedShelf.cs:1402-1411`): `32 × 32`, `CornerRadius4.All(16)` — a CIRCLE, not
r-ctrl — `Fill = Tok.FillControlDefault` **at rest** (so the pair is visible at all times, not hover-revealed),
`HoverFill = Tok.FillControlSecondary`, `Opacity = 1` enabled / `0.35` disabled, glyph 13 in `Tok.TextSecondary`,
header item gap 8 (`:1399`). There is **no 0.7 rest opacity, no header-scoped reveal and no hover scale** on Home —
that behaviour belongs to `Components/Rail.cs` (112 lines), which nothing on Home — or anywhere in the app —
instantiates (§9.4).
**Live defect:** `PagedShelf.Create`'s `prevGlyph` defaults to `""` (`PagedShelf.cs:130`) and no Wavee call site
passes one, so the ‹ chevron on every Home shelf is a blank 32-DIP puck with no glyph. Pass `Icons.ChevronLeft` in 0.3.

### W10 — Daily-mix band @ A = 1088 (A > 1080 → 6 cells), one plate 1088 × 144

```
┌ Your top mixes ▸  ·  One series, 6 mixes ───────────────────────────────────────────────────────────────────────────────────────────┐
├──────────────────────────────────────────── ONE card plate: r-card 8, FillCardDefault, 1px stroke, clipped ─────────────────────────┤
│  ┌ 181 ─────┐│┌ 181 ─────┐│┌ 181 ─────┐│┌ 181 ─────┐│┌ 181 ─────┐│┌ 181 ─────┐        ← 1-DIP StrokeDividerDefault before every    │
│  │ 1        ││ 2         ││ 3         ││ 4         ││ 5         ││ 6         │          cell whose index % columns != 0            │
│  │ Title 28/36 display, −40 tracking, colour = the mix's own lifted accent (TextPrimary until it lands)                             │
│  │ DAILY MIX││ DAILY MIX ││ DAILY MIX ││ DAILY MIX ││ DAILY MIX ││ DAILY MIX │        ← Eyebrow 12/16/600 tertiary; the STRING is    │
│  │          ││           ││           ││           ││           ││           │          authored caps ("DAILY MIX", home.dailyMix),  │
│  │          ││           ││           ││           ││           ││           │          no ToUpper in code — see §6.6               │
│  │ Beabadoo ││ Clairo ·  ││ Alvvays · ││ Men I Tr. ││ Dazey and ││ Snail Mail│        ← Caption secondary, 3-line clamp, mb 2       │
│  │ bee · Cl ││ Faye Web. ││ Big Thief ││ ust · Hop ││ the Scout ││ · Beach B.│                                                     │
│  │▔▔ spine ▔││▔▔ spine ▔▔││▔▔ spine ▔▔││▔▔ spine ▔▔││▔▔ spine ▔▔││▔▔ spine ▔▔│        wash: the cell's own accent at A = 0.09      │
│  └──────────┘└───────────┘└───────────┘└───────────┘└───────────┘└───────────┘        padding 16 · content gap 2                    │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
   cell height 142 = 2×16 pad + 36 numeral + 4 + 16 eyebrow + 3×16 seeds + 3×2 stack gaps; plate adds 2 for its 1px contour
```

### W11 — Daily-mix band @ A = 1000 (620 < A ≤ 1080 → 3 cells), 6 cards → 2 rows

```
├─────────────────────── ONE plate ────────────────────────┤
│ ┌ 333 ────┐│┌ 333 ────┐│┌ 333 ────┐ │                    │   a 1-DIP top rule runs across every cell of
│ │ 1 …     ││ 2 …      ││ 3 …      │ │                    │   row ≥ 1 (i >= columns), drawn INSIDE the cell
│ └─────────┘└──────────┘└──────────┘ │                    │   (a grid has nowhere to put a separator)
│ ──────────────────────────────────────  ← top rules      │
│ ┌ 333 ────┐│┌ 333 ────┐│┌ 333 ────┐ │                    │   estimator: 2×142 + 1 hairline + 2 contour = 287
│ │ 4 …     ││ 5 …      ││ 6 …      │ │                    │
│ └─────────┘└──────────┘└──────────┘ │                    │
└──────────────────────────────────────┘
```

### W12 — Daily-mix band @ A = 600 (A ≤ 620 → 2 cells), 6 cards → 3 rows of 300-DIP cells, height 430.

### W13 — Top-artist podium, closed @ A = 1088, 10 artists (ramp scale clamped at 1.6)

`FillRowVirtualLayout.Fit(1064, 84, 9999, 8, perPageOverride: 10)` → 99.2 per column;
`RampScaleFor(99.2, 10, 8)` = 99.2 / 59.8 = 1.659 → **clamped to 1.6** (`HomeArtistRowLayout.cs:64-73`).
Art sizes: rank 1 = 121.6, ranks 2-3 = 96, ranks 4-10 = 73.6. Slot height = 121.6 for every pod.

```
┌ Your top artists  ·  Last 4 weeks · 10 tracked · select one ────────────────────────────────────────────────────────────┐
├─── card: r-card 8, FillCardDefault, 1px StrokeCardDefault, ClipToBounds, Animate = CardResizeHeight ────────────────────┤
│  padding 12                                                                                                             │
│    ╭───╮                                                                                                                │
│   ╭│ 1 │╮        ╭─────╮      ╭─────╮     ╭───╮   ╭───╮   ╭───╮   ╭───╮   ╭───╮   ╭───╮   ╭───╮        ← every pod's art │
│  ╭╯╰───╯╰╮      ╭╯  2  ╰╮    ╭╯  3  ╰╮   ╭╯ 4╰╮  ╭╯ 5╰╮  ╭╯ 6╰╮  ╭╯ 7╰╮  ╭╯ 8╰╮  ╭╯ 9╰╮  ╭╯10 ╰╮        is bottom-aligned│
│  │ 121.6 │      │  96   │    │  96   │   │73.6│  │73.6│  │73.6│  │73.6│  │73.6│  │73.6│  │73.6│         in a 121.6 slot  │
│  ╰───────╯      ╰───────╯    ╰───────╯   ╰────╯  ╰────╯  ╰────╯  ╰────╯  ╰────╯  ╰────╯  ╰────╯         → ONE label line │
│   ↕ 8                                                                                                                   │
│   NewJeans        IU           aespa      Bibi   Wonho  Zico   Heize  Dean   Crush  Colde  ← Caption 600 primary, 2 lines│
│                                                                                              centred, MaxWidth = pod w  │
│   pod w = max(art + 8, 60) · pod padding L2 T8 R2 B8 · gap 8 · wrap · Role = Tab · Interaction.Subtle                    │
│   rank plate: 20 × 20 circle (28 wide at rank ≥ 10) on the ART's top-left; FillControlSolid + 1px StrokeCardDefault;     │
│               Caption 600 TextSecondary                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
   module bottom gap = HomeArtistRowLayout.ModuleGap: 32 at ≥1080, else 24 (NOT the shared 40/32)
```

### W14 — Podium with a selection: the hub pod

```
      ╭─────────╮        selected (= the Mixview HUB, not necessarily the last click):
     ╭╯ ╭─────╮ ╰╮       · 3-DIP Tok.AccentDefault ring, and the artwork is inset by 3 BY HAND
     │  │  1  │  │         (BorderWidth has no layout effect in this engine) — art drawn at artSize − 6
     │  │ ▓▓▓ │  │       · rank plate flips to Fill = AccentDefault, no border, ink TextOnAccentPrimary
     ╰╮ ╰─────╯ ╭╯       · clicking the OPEN pod closes the disclosure (index → −1)
      ╰─────────╯
```

### W15 — Podium disclosure, tier Wide (row width ≥ 900, hysteresis 24) @ A = 1088

```
├──── podium (as W13) ──────────────────────────────────────────────────────────────────────────────────────────┤
├─────────────────────────────── 1-DIP StrokeDividerDefault ────────────────────────────────────────────────────┤
│ Top tracks  6.3M monthly listeners · #4 worldwide            ( ▶ Play ) │  Mixview   6 · fans also like        │
│ ─────────────────────────────────────────────────────────────────────── │  ─────────────────────────────────── │
│  #  ♥   ▢   Title                          Plays      3:41   [in top 5]│         ╭────╮                       │
│  1  ♥  [32] Ditto                        1 203 455    3:12      ( … )  │      ╭──│ 42 │──╮   ring: hub r 34,   │
│  2  ♥  [32] Hype Boy                       982 110    2:59      ( … )  │     ╱   ╰────╯   ╲  node r 21,        │
│  3  ♥  [32] OMG                            870 004    3:33      ( … )  │    ◯  ╭────────╮  ◯ ringR = min(cx,cy)│
│  4  ♥  [32] Attention                      712 998    3:00      ( … )  │    │  │ HUB 68 │  │      − 21 − 12    │
│  5  ♥  [32] Super Shy                      655 431    2:34      ( … )  │    ◯  ╰────────╯  ◯ connectors: 1 DIP,│
│                                                                        │     ╲            ╱  TextTertiary @.26 │
│  columns 36 | 28 | 32 | ★ | 84 | 52 | 160   (art column dropped when   │      ╰────◯────╯                      │
│  AppearancePrefs.TrackArtworkHidden)  rows = TrackRow.RowHeight 48     │    node caps: width 104, centred,     │
│  padding 12 · gap 8 · head: BodyStrong + Caption tertiary + Play       │    Caption 12/16 (hub 600 primary)    │
│                                                            ← left Grow │  342 fixed, padding 12, gap 12 →      │
└────────────────────────────────────────────────────────────────────────┴───────────────────────────────────────┘
   pane geometry: ring w = measuredWidth − 24 (fallback 314); h = w × 0.92 + 18; cx = w/2, cy = h × 0.46
   drawn nodes = min(related, 6) — and the header REPORTS the drawn count, never the payload count
```

### W16 — Podium disclosure, tier Spine (row width < 900)

```
├──── podium ───────────────────────────────────────────────┤
├─────────────── 1-DIP divider ─────────────────────────────┤
│ Top tracks  6.3M monthly listeners        ( ▶ Play )      │
│  1 ♥ [32] Ditto …                                         │
│  … 5 rows …                                               │
├─────────────── 1-DIP divider (horizontal now) ────────────┤
│ Mixview  6 · fans also like                               │
│  ╭────╮                                                   │   SpineHub: 40 circle, 3-DIP accent ring,
│  │ 40 │  NewJeans            ← BodyStrong                 │   BodyStrong name
│  ╰────╯                                                   │
│    │ ← 1-DIP rule, inset 20 (SpineHubD × 0.5) so it falls │   rows: 32 circle + Caption secondary,
│    ├─ ◯32  IVE                                            │         padding L8 T4 R4 B4, gap 8
│    ├─ ◯32  LE SSERAFIM                                    │
│    ├─ ◯32  (G)I-DLE                                       │   the rule is ONE full-height 1-DIP box behind
│    ├─ ◯32  aespa                                          │   the whole list, not six stroke elements
│    ├─ ◯32  STAYC                                          │
│    └─ ◯32  ITZY                                           │
└───────────────────────────────────────────────────────────┘
```

### W17 — Podium disclosure, pending (no warm artist in the store)

```
├─────────────── 1-DIP divider ─────────────────────────────┤
│ Top tracks                                ( ▶ Play )      │   5 × a 48-DIP bar of the ROW SHAPE
│  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁       │   (never a spinner, never empty), so the
│  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁       │   pane never jumps when the overview lands
│  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁       │
│  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁       │   Mixview with no related list: ONE 120-DIP
│  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁       │   skeletonized block
└───────────────────────────────────────────────────────────┘
   A WARM artist (store hydration ≥ Rich) skips the skeleton entirely and renders content immediately,
   revalidating underneath (KeepPreviousData) — so re-centring never blanks the pane.
```

### W18 — Chip cards @ A = 1088 (A > 1020 → 3 columns), card 354.7 × 96

```
┌ Made for you ▸  ·  6 mixes built from artists you play ─────────────────────────────────────────────────────────┐
│┌────────── 354.7 ──────────┐ ┌────────── 354.7 ──────────┐ ┌────────── 354.7 ──────────┐                        │
││ ┌──┐ IU Mix               │ │ ┌──┐ Bedroom Pop Mix      │ │ ┌──┐ Hyperpop Mix         │  BodyStrong 14/20      │
││ │64│ ┌ IU ┐┌ Sunwoo ┐┌ B ┐│ │ │64│ ┌ Clairo ┐┌ mxmt ┐   │ │ │64│ ┌ 100 gecs ┐┌ A.G ┐  │  chips: FillSubtle-    │
││ └──┘ └────┘└────────┘└───┘│ │ └──┘ └────────┘└──────┘   │ │ └──┘ └──────────┘└─────┘  │  Secondary, r-ctrl,    │
││      50 songs             │ │      50 songs             │ │      50 songs             │  Caption, pad 8/2      │
│└▔▔▔▔▔▔▔▔ spine ▔▔▔▔▔▔▔▔▔▔┘ └▔▔▔▔▔▔▔▔ spine ▔▔▔▔▔▔▔▔▔▔┘ └▔▔▔▔▔▔▔▔ spine ▔▔▔▔▔▔▔▔▔▔┘  max 3 chips, wrap gap 4 │
│  padding 12 · art gap 12 · text stack gap 8 · r-ctrl 4                                                          │
└─────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
   NO seeds → the description renders instead, as RichText at 12 px, 2 lines (anchors become accent hyperlinks)
   thresholds: > 1020 → 3 · > 680 → 2 · else 1 · row gap 12
```

### W19 — Radio dial @ A = 1088 → 2 columns of 532, 12 stations, NO row gap

```
┌ Radio ▸  ·  20 stations ────────────────────────────────────────────────────────────────────────────────────────┐
│┌──────────────────── 532 ────────────────────┐ ←──── 24 ────→ ┌──────────────────── 532 ────────────────────┐   │
││ ◯32  IU Radio                          ▷    │                │ ◯32  NewJeans Radio                    ▷    │   │  48 high each,
││      IU, Taeyeon, Baekhyun                  │                │      NewJeans, IVE, LE SSERAFIM             │   │  no gap between
│├─────────────────────────────────────────────┤                ├─────────────────────────────────────────────┤   │  rows — twenty
││ ◯32  Clairo Radio                      ▷    │                │ ◯32  aespa Radio                       ▷    │   │  stations read
││      Clairo, Beabadoobee, Faye Webster      │                │      aespa, ITZY, STAYC                     │   │  as ONE dial
│├─────────────────────────────────────────────┤                ├─────────────────────────────────────────────┤   │  folded in half
││ … 4 more …                                  │                │ … 4 more …                                  │   │
│└─────────────────────────────────────────────┘                └─────────────────────────────────────────────┘   │
│   row: padding L8/R8, art ROUND 32 (decode 64), gap 12, BodyStrong over Caption secondary,                       │
│        now-playing mark 11, HoverPlay 24 GHOST (transparent plate, TextSecondary glyph)                          │
│        Interaction.ListRow: transparent → FillSubtleSecondary → FillSubtleTertiary, r-ctrl 4, NO spine           │
└─────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
   columns: floor((A + 24) / (RadioColMin 252 + 24)) clamped to [1,2] → 2 columns from A ≥ 528
   the grid carries Key = "radio-grid:" + columns so a column flip rebuilds rather than rebinding
```

### W20 — Up next (episodes) + Audiobooks, split even @ A = 1088 (≥ `SplitEvenMin` 1020)

```
┌ Up next ▸ · 2 h 14 m queued · 9 suggestions ───────────┐ ←24→ ┌ Audiobooks for you ▸ · 12 included with Premium ┐
│ ┌────────┐ Wine Down Wednesday            8 min        │      │ ┌────┐ Project Hail Mary       ★★★★★ 4.57  16.1 h│
│ │ 56×32  │ Video · The Blindboy Podcast  unplayed      │      │ │ 48 │ Andy Weir                                 │
│ │ 16:9!  │                                             │      │ └────┘                                           │
│ └▔▔▔▔▔▔▔┘ ← resume hairline: 2 DIP AccentDefault,      │      │  ↕ 2                                              │
│ ─────────── 1-DIP divider ────────────────────────────  │      │ ┌────┐ Dune                     ★★★★☆ 4.31  21.3 h│
│ ┌──┐ The Rewatchables                    92 min        │      │ │ 48 │ Frank Herbert                             │
│ │32│ The Ringer                          resume        │      │ └────┘                                           │
│ └──┘                                                   │      │  … 6 rows total, 2-DIP stack gap …               │
│ ─────────── divider ─────────────────────────────────  │      │                                                  │
│ … 6 rows total, Gap 0, divider on all but the last …   │      │  rating: 5 × 16-DIP stars, gap 8 (112 wide),     │
│ row: padding 8, gap 12, 56-DIP right cell (time over   │      │  placeholder brush = Tok.TextPrimary (read-only) │
│ state word, both Caption; time secondary, state        │      │  value Caption 600 primary, 2 dp                 │
│ tertiary)                                              │      │  length: Count(Hours, width 32) — "16.1 h"/"45 m"│
└────────────────────────────────────────────────────────┘      └──────────────────────────────────────────────────┘
   below 1020 the two stack, separated by HomeModuleLayout.Gap(width) (40 ≥1080, else 32)
   one side missing → SplitSingle keeps the survivor in its own half-column above 1020
```

### W21 — Queue row, video vs audio artwork (the shape previews the medium)

```
 VIDEO episode (Meta.HasVideo)                    AUDIO episode
 ┌──────────────┐                                 ┌──────┐
 │   56 × 32    │  16:9, r-ctrl, clipped          │ 32×32│  square, r-ctrl
 │▔▔▔▔▔▔▔▔▔▔▔▔▔ │  ← resume hairline: width =     └──────┘
 └──────────────┘    max(2, artW × ResumeMs/DurationMs), height 2, AccentDefault, bottom-left
   "Video" (tertiary) + show name (secondary)      show name only
```

### W22 — Editors' picks @ A = 1088 (≥ `EditorialMin` 980) — `1.08fr : 1fr`, gap 16

```
┌ Editors' picks ▸  ·  Curated, with something to say ────────────────────────────────────────────────────────────┐
│┌─────────────────────── 556.6 ────────────────────────┐ ←16→ ┌────────────────── 515.4 ────────────────────────┐│
││  ┌─────────┐   ┌ Editorial ┐  ← 1px border lerped 40%│      │┌───────────────────────────────────────────────┐││
││  │         │   └───────────┘    toward the accent;   │      ││ ┌──┐ Lorem Playlist                      ▷    │││
││  │   148   │   ink = WaveeAccent.Decor               │      ││ │48│ One-line description, RichText        ↑    │││
││  │  r-card │                                         │      ││ └──┘                          HoverPlay 30 ghost││
││  │         │   New Music Friday          ← Subtitle  │      │└▔▔▔▔▔▔▔▔▔▔▔ spine ▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔┘││
││  └─────────┘   20/28 display, −12, 2 lines           │      │  ↕ 8                                            ││
││                                                      │      │┌───────────────────────────────────────────────┐││
││   The freshest tracks from every corner of the       │      ││ ┌──┐ Another Playlist                    ▷    │││
││   catalogue, updated every Friday at midnight.       │      ││ │48│ One-line description                      │││
││   Three lines of RichText at 12 px.                  │      │└▔▔▔▔▔▔▔▔▔▔▔ spine ▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔┘││
││                                     ← spacer (Grow)  │      │  ↕ 8                                            ││
││   ( ▶ Play )  100 songs · by Spotify   ← pad-top 12  │      │┌───────────────────────────────────────────────┐││
│└▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔ spine ▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔┘      ││ ┌──┐ Third Playlist                       ▷    │││
│  padding 20, gap 20, r-card 8                         │      ││ │48│ …                                        │││
│  height = 2×20 + 148 = 188                            │      │└▔▔▔▔▔▔▔▔▔▔▔ spine ▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔┘││
│                                                       │      │  column height = 3×(2×12 + 48) + 2×8 = 232      ││
│└──────────────────────────────────────────────────────┘      └─────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
   below 980 (or with zero companions): a single column, feature then the companion stack, gap 16
```

### W23 — What's-new timeline @ A = 1088

```
┌ New releases  ·  3 unheard of 8            (3)  ← InfoBadge.Count ────────────────────────────────────────────┐
│                                                                                                               │
│        Today │ ┊ ┌──┐ Midnights (3am Edition)                              ( New )   ← accent plate, Eyebrow  │
│    ← 96 →    │ ●─│40│ ┌ Releases ┐ Taylor Swift                                        ink TextOnAccentPrimary│
│              │ ┊ └──┘ └──────────┘  ← KindTag: 1px StrokeControlDefault, r-ctrl, pad 4/0, Eyebrow tertiary    │
│              │ ┊ ┌──┐ The Blindboy Podcast                                  ( New )                           │
│              │ ●─│40│ ┌ Podcast ┐ The Blindboy Podcast                                                        │
│              │ ┊ └──┘ └─────────┘                                                                             │
│  ────────────┼─┊──────────────────────────────────────────────────────────────────────────────────────────    │
│    Yesterday │ ┊ ┌──┐ Sharon Van Etten at Paradiso                            Seen   ← Count(), Caption       │
│              │ ○─│40│ ┌ Concert ┐ Sharon Van Etten          ← ROUND art (an act is a person)   tertiary      │
│              │ ┊ └──┘ └─────────┘                                                                             │
│  ────────────┼─┊───────────────────────────────────────────────────────────────────────────────────────────   │
│    Fri 5 Sep │ ┊ ┌──┐ …                                                                                       │
│    4 days ago│ ○─│40│ …                                                                                       │
│              │ ┊                                                                                              │
│   day column:│ └── the 1-DIP StrokeDividerDefault rule the pips straddle (margin-left −4 on a 7-DIP pip)       │
│   Caption 600 secondary (Today / Yesterday / "Fri 5 Sep"), Caption tertiary "N days ago" below (delta > 1),    │
│   align End, padding-top 12, width 96 fixed                                                                   │
│   row: ZStack(Row(padding L16 T8 R8 B8, gap 12) , pip) · art 40 · BodyStrong · KindTag + meta · 56 high        │
│   pip: 7 × 7, r 3.5, 1.5-DIP ring — unread = AccentDefault fill + AccentDefault ring;                          │
│                                      seen  = FillLayerDefault fill + StrokeControlStrongDefault ring          │
│   max 8 rows (HomeTimelineMerge.MaxRows); the counter describes the UNCAPPED eligible set                      │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### W24 — Fold deck (Charts / Sections for you) @ A = 1088 → 2 tiles of 538 × 176

```
┌ Charts ▸ ────────────────────────────────────────────────────────────────────────────── ( ‹ ) ( › ) ─┐
│┌──────────────────────── 538 ─────────────────────────┐ ←12→ ┌──────────────────────── 538 ─────────┐│
││ radial wash: first card's accent, 0.22 → 0 at 72%,   │      │                                       ││
││ centre (1.0, 0.48), radius (0.70, 1.10)              │      │           ┌──────┐                    ││
││                              ┌──────┐                │      │      ┌────│ 124  │                    ││
││                     ┌──────┐ │ 124  │╲               │      │  ┌───│124 │ −2°  │                    ││
││                 ┌───│ 124  │ │ +5°  │ ╲ x = cardW    │      │  │124│+5° └──────┘                    ││
││                 │124│ +5°  │ └──────┘   − 210 = 328  │      │  │−11°                                ││
││                 │−11°      │                         │      │                                       ││
││   Featured Charts          ← FoldTitle: Title 28/36  │      │   Weekly Song Charts                  ││
││   ↑ copy column, maxWidth 70% of cardW,   display    │      │                                       ││
││     padding L20 T20 R12 B18, Justify End  400, −12   │      │                                       ││
││     (Edges4 is L,T,R,B — NOT the CSS order)          │      │                                       ││
│└───────────────────────────────────────────────────────┘      └───────────────────────────────────────┘│
│  card: r-card 8, FillCardDefault → FillCardSecondary on hover, 1px StrokeCardDefault, Elevation.Card,   │
│        ClipToBounds (the third cover is cut by the right edge on purpose), Role = Hyperlink, Focusable  │
│  covers: r-ctrl 4, Elevation.Card, HitTestVisible false                                                 │
└────────────────────────────────────────────────────────────────────────────────────────────────────────┘
   two-up appears at 2 × 440 + 12 = 892 of content; below that ONE tile fills the row (maxColumns 2, max ∞)
   the stack's left is `max(0, cardW − 210)` (`HomeModuleLayout.FoldRest`, `HomeModules.cs:560`) — CLAMPED, so a
   0-width first frame (or a cell narrower than the 250-DIP stack box) cannot park the covers at a negative X and
   paint them up into the band header
```

### W25 — Fold tile, hovered (the fan)

```
  rest                                    hover (WhileHover deltas, MotionTok.ControlNormal 250 ms)
  ┌──────┐ ┌──────┐ ┌──────┐              ┌──────┐   ┌──────┐  ┌──────┐
  │ −11° │ │  +5° │ │  −2° │              │ −16° │   │  +8° │  │  +1° │     Δ = (−10, +6, −5°) · (+2, −6, +3°) · (+10, 0, +3°)
  └──────┘ └──────┘ └──────┘              └──────┘   └──────┘  └──────┘     the prototype's front-cover scale(1.03) is deliberately OUT
   x 328     372      420                  318        374       430         the card fill also swaps Default → Secondary (no lift)
   y 38      22       8                     44         16        8
```

### W26 — Fold deck, the three non-loaded states (the row is NEVER absent)

```
 PENDING (ChartDeckSeed = 3 blank sections, 3 blank cards each)   EMPTY (Ready, 0 sections)        FAILED
┌───────────────────────┐ ┌───────────────────────┐              ┌──────────────────────────┐     ┌──────────────────────────┐
│▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓│ │▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓│              │   No charts right now    │     │  (ErrorState.Build with  │
│▓ one flat r-card bone ▓│ │▓ at the fitted width ▓│              │   EmptyState.Compact     │     │   a Retry that calls     │
│▓ SkeletonStyle.BarColor│ │▓                     ▓│              │   Subtitle 20/28         │     │   charts.Refresh)        │
└───────────────────────┘ └───────────────────────┘              └──────────────────────────┘     └──────────────────────────┘
   extent FoldExtent = 32 + 176 + 24 = 232                        extent FoldStateExtent = 32 + 96 + 24 = 152
   reveal: SkelReveal.None, smoothResize false
```

**The deck is 1..5 tiles, not always 5.** `HomeBrowseCards.LoadChartDeckAsync` fans out over `ChartSections.All`
(five uris) but **omits every later section that came back null or with zero cards** (`HomeBrowseCards.cs:53-60`) —
a market with no Podcast Charts loses that tile rather than blanking Home. Only `pages[0]` (Featured) is the
fail-loud slot, and even that degrades to an EMPTY deck (→ the `onEmpty` arm) when `hasLiveCatalog` is false, i.e.
offline or logged out (`:46-50`). Each surviving section is re-stamped with the taxonomy uri we ASKED for
(`:59`), because a mapper variant on `s.Uri` would miss `ChartSections.Contains` on the drill page.

The bone is EXPLICIT (`root.Skel(bone)`, `HomeFoldTile.cs:134-141`): a derived skeleton would zero the covers'
authored `Offset`/`Rotation` (their rest pose is not a transient transform) and drop the card's fill and border.

### W27 — Discover feed / Podcasts / facet shelf (`MediaCard.Shelf` in a `PagedShelf`)

```
┌ Because you listened ▸  ·  18 recommendations, each with its own reason ───────────── ( ‹ ) ( › ) ┐
│┌─ 171 ─┐ ┌─ 171 ─┐ ┌─ 171 ─┐ ┌─ 171 ─┐ ┌─ 171 ─┐ ┌─ 171 ─┐░░ EdgeFade 24                          │
││ cover │ │ cover │ │ cover │ │ cover │ │ cover │ │ cover │░░                                       │
│└───────┘ └───────┘ └───────┘ └───────┘ └───────┘ └───────┘░░   card height = cardW + 72            │
│  Title     Title     Title     Title     Title     Title                                           │
│  For fans of IU  ← subtitle = Subtitle ?? Eyebrow: the section's REASON survives as the card's      │
│                     second line (the old composer discarded every one of them)                      │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### W28 — Module header, the three forms

```
 drillable            Recents ▸                        title (Subtitle 20/28/600 display, −6) + 12-DIP ChevronRight
                                                       Tok.TextTertiary, gap 4, Role = Hyperlink, Focusable
 drillable + subtitle Radio ▸  ·  20 stations          ONE SpanTextEl: the 12-px run shares the 20-px baseline,
                                                       separated by two literal spaces (a run break carries no margin)
 not drillable        Sections for you                 the SAME label, no chevron, no click wrapper — never a second grammar
```

`HeadGap` between the header and the content is 12 (`HomeModuleLayout.HeadGap`). A group with a blank title renders
NO header at all (`HomeModules.cs:31-34`, `:41`).

### W29 — Hover / pressed / focus, per family

```
 CARD family (weekly, quick, chip, feature, crowd, Fold)        ROW family (radio, queue, book, timeline)
 rest     plate FillCardDefault + 1px StrokeCardDefault          rest     transparent (FillSubtleTransparent)
 hover    fill → FillCardSecondary (83 ms)                       hover    → FillSubtleSecondary (83 ms)
          OffsetY −4 over 250 ms FluentStandard                           NO lift, NO scale
          HoverElevatePaint: paints above later siblings                  HoverPlay fades 0 → 1 + scales .86 → 1
          spine → HairlineHover (3.55:1)                         pressed  → FillSubtleTertiary
          HoverPlay (if any) 0 → 1 opacity + 1.07 scale
 pressed  Scale 0.99 + OffsetY −1 (ApplyCardPhysics wins over    focus    engine focus ring on the Row/Card box
          Interaction.Card's 0.985 spring)
 MIX CELL  Fill FillSubtleTransparent → FillSubtleSecondary with BrushTransitionMs 83 — no lift (it is one cell of
           a shared plate, and lifting a cell would tear the plate)
 POD       Interaction.Subtle (transparent → subtle → tertiary), no scale, Role = Tab
 FOLD TILE fill swap only + the cover fan; the card does NOT lift (WinUI hover: SubtleFill at the same elevation)
```

### W30 — Now-playing, per skin

```
 quick tile   [56] Blonde            ▮▮▮ ( ▷ )     mark 12, BEFORE the hover play
 weekly card  [56] Discover Weekly ▮▮▮        30   mark 12, trailing the title inside Titled()
 mix cell     Daily Mix ▮▮▮                        mark 10, trailing the eyebrow
 chip card    IU Mix ▮▮▮                           mark 11 (default)
 radio row    IU Radio           ▮▮▮   ( ▷ )       mark 11, before the ghost play
 book row     Project Hail Mary  ▮▮▮   ★★★★★       mark 11, before the rating cluster
 feature      New Music Friday ▮▮▮                 mark 13
 Bars: three, 2-DIP gap, bottom-anchored, Tok.AccentTextPrimary, 850 ms loop at ~30 Hz while playing;
 PAUSED = all three bars snap to a FLAT 0.4 (Equalizer.cs:100) — Home passes no `paused` signal, so "the context is
 open but not sounding" and "not playing" are the same state here and the mark stays VISIBLE at rest, flat;
 zero width (a 0 × 0 BoxEl) when this card is not the sounding context at all (HomeCards.cs:1125).
 Reduced motion + playing = a settled NON-UNIFORM snapshot, never a loop and never the flat paused shape.
```

### W31 — Card context menu open (any skin, any module)

```
        ┌ Playlist ─────────────────┐
        │ [cover 40] Discover Weekly│   header: art + name + kind
        │            Playlist       │
        ├───────────────────────────┤
        │ ( ▶ )( ⤓ )( ⤒ )( ♥ )      │   AppBar strip: Play · Play next · Add to queue · Save
        ├───────────────────────────┤   (the queue pair only when the kind has a resolvable track set;
        │ + Add to playlist       ▸ │    Liked Songs drops Save)
        │ ↗ Open                    │
        │ 📌 Pin to sidebar          │
        │ ↗ Share                 ▸ │
        └───────────────────────────┘
   artist cards additionally carry Follow (a row) and "Go to artist radio"; album cards carry "Go to artist";
   a SHOW card is Play · Open · Pin · Share; a TRACK uri card is the thin track menu; an EPISODE uri gets NO menu.
   Podium pods and Mixview nodes get a ONE-ITEM menu: "Go to artist" (HomeCards.cs:1072).
```

### W32 — Drag in progress (any card, any module)

```
   press + move 8 DIP on a card  →  the engine's drag chip (ch. 01 §6) carries cover + title
   sources: EVERY card of EVERY module except Kind == Track / Episode (the feed has only a uri for those)
   targets: sidebar playlist rows, folders, the pin band  (ch. 25)
```

### W33 — Section grid (the drill destination `SectionGrid` builds; ch. 12 owns the page)

```
┌ A 5-column AspectGrid of MediaCard.GridCard, gap 12, cells fit between 148 and 188 ────────────────┐
│ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐    cell reserve = GridCardChromeFor(lines,  │
│ │ square │ │ square │ │ square │ │ square │ │ square │      hasSubtitle):                          │
│ └────────┘ └────────┘ └────────┘ └────────┘ └────────┘      = 16 + lines×20 + (sub ? 18 : 0)       │
│  Title      Title      Title      Title      Title          (1 line + subtitle = 52 = GridCardChrome│
│  subtitle   subtitle   subtitle   subtitle   subtitle        exactly)                              │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
   a CHART section blanks every subtitle and buys a second title line for 2 DIP (HomeModules.cs:524-536)
```

### W34 — states this surface has NO form for (checked, and absent on purpose)

| state | on Home | why / where the form lives instead |
|---|---|---|
| explicit / 18+ badge | **none on any card skin** | the marker is a TRACK-level fact; `TrackRow` carries it (ch. 01) and the artist disclosure inherits it there |
| unavailable / greyed / region-blocked | **none** | a Home card carries no availability field; the drill destination owns the refusal |
| selection (multi-select, checkbox) | **none** | the only "selected" on Home is the podium's hub ring (W14), which is a navigation state, not a selection |
| drop TARGET highlight | **none** | every card is a drag SOURCE only (§6.4); Home accepts no drops. Targets are ch. 25 |
| scrolled / compact / sticky chrome | **none inside a module** | ch. 10 owns the page frame; a module's geometry is a pure function of its row width |
| zoom | no per-skin branch | everything is DIP and the engine's `Viewport.Scale` handles it; the only pixel-aware code is the equalizer's whole-device-pixel quantisation (`Equalizer.cs:126-130`) |
| per-card error | **none** | a card that failed to resolve is simply absent from the group; only the Charts ROW has a failed arm (W26) |
| 0 items in a group | the module is not rendered at all (`Grid` returns an empty box at `count == 0`, `HomeModules.cs:114`; `HomePage` returns `new BoxEl()` when the group is null) | the page never shows an empty module frame — the only "empty" grammar on Home is the Charts row's `EmptyState.Compact` |
| 10 000 items in a group | every module renders a bounded, already-decided slice (§7 "Demand discipline"); the shelves virtualise what is in hand | the drill page (`SectionGrid`, ch. 12) is where an unbounded set lives |

---

## 3. Tokens

Sizes in DIP. `Spacing`: XXS 2 · XS 4 · S 8 · M 12 · L 16 · XL 20 · XXL 24 · XXXL 32 · PageWide 36.
`Radii`: Control 4 · Card 8 · Full 999 (clamped to half the box). Type: Caption 12/16 · Body 14/20 ·
BodyStrong 14/20/600 · BodyLarge 18/24 · Subtitle 20/28/600 · Title 28/36/600 · TitleLarge 40/52/600.

### 3.1 Module shells

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| module (head + content) | — | column gap `HeadGap` 12 | — | — | — | — | `HomeModules.cs:27-34`, `:499` |
| module header title | — | row gap 12 to tools | — | `WaveeType.ModuleHeader` Subtitle 20/28/600, Segoe UI Variable Display, tracking −6 | `Tok.TextPrimary` | — | `WaveeType.cs:63-67` |
| header subtitle run | — | two literal spaces | — | Caption 12/16, same paragraph | `Tok.TextTertiary` | — | `WaveeType.cs:74-99` |
| header chevron | 12 glyph | gap 4 | — | `Icons.ChevronRight` | `Tok.TextTertiary` | — | `HomeModules.cs:70` |
| module gap (between rows) | 40 ≥ 1080, else 32 | — | — | — | — | — | `HomeModules.cs:496-497`, `:596` |
| grid (uniform) | `columns` star tracks | col 12 / row 12 (2 for books, 0 for radio & mix) | — | — | — | — | `HomeModules.cs:112-121`, `:665-670` |
| two-column (editorial) | `1.08fr / 1fr` | gap 16 | — | — | — | — | `HomeModules.cs:312-315` |
| two-column (split even) | `1fr / 1fr` | gap 24 | — | — | — | — | `HomeModules.cs:281` |
| shelf card fit | min 148 · max 188 | gap 12 | — | — | — | edge fade 24 | `HomeModules.cs:504-506` |
| shelf card height | `cardW + 72` | — | — | — | — | — | `MediaCard.cs:212` |

### 3.2 Skins

| element | size | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| **hero surface** | width × 336/344/384 | copy 48 × 44 | Card 8 | — | `Gradient = Surfaces.HomeHeroBackdrop(accent)`; border 1 `Tok.StrokeCardDefault` | ClipToBounds, no shadow | `HomeCards.cs:398-425`, `HomeHeroLayout.cs:24-25` |
| hero artwork | edge = surface height | — | 0 (clipped by the parent) | — | decode 512 | `EdgeFade(Left, 96)`, none when stacked | `HomeCards.cs:408-416` |
| hero veil | full surface | — | — | — | `Surfaces.ArtistHeroVeil(accent, axis)` | 4 stops, hit-test off | `HomeCards.cs:374-377` |
| hero eyebrow | — | mb 8 | — | Eyebrow 12/16/600, tracking 30 | `Tok.TextTertiary` | — | `HomeCards.cs:308-312` |
| hero title | — | mb 12 | — | ArtistTitle 48/60/700 −20 (**MinSize 40**) · ArtistCompactTitle 32/40/700 −12 (**MinSize 28**) · PageHero 28/36/600 (no MinSize) | `Tok.TextPrimary` | wrap, 2 lines, then AUTOFIT down to MinSize before it ellipsizes | `HomeCards.cs:281-286`, `WaveeType.cs:167-187` |
| hero tag | auto | pad 8 × 2, gap 4, mb 12 | Control 4 | Caption 12/16 | 1px `Tok.StrokeControlDefault`, no fill | — | `HomeCards.cs:171-177` |
| hero meta | — | mb 16 | — | Body 14/20, 2 lines | `Tok.TextSecondary` | — | `HomeCards.cs:326-333` |
| hero countdown | row 28, digit cell 13, colon 8 | gap 8, mb 12 | — | 20 px, weight 300, Segoe UI Variable Display | `WaveePalette.TextInk(accent)`; expired → `Tok.TextTertiary` | clipped digit cells; hours CLAMPED to 99 (a > 4-day window clamps rather than reflows); the 1 s interval stops once expired | `FlipCountdown.cs:82-129` |
| countdown trailing copy | — | gap 8 from the digits | — | Caption 12/16, 1 line, ellipsis | `Tok.TextTertiary` | `home.nextUpdateAt` with the local short time ("t") | `FlipCountdown.cs:124-129` |
| hero actions | Play / Shuffle pills 36 (`WaveeCta.PillHeight`, pad 18/6/18/7); ♥ / … icon arms ALSO 36 (`WaveeCta.Icon` defaults `size = PillHeight`, `Radii.Full`) | gap 8, wrap | Full | Button label 14/600 | Play on the card accent; Shuffle Standard; ♥ / … the icon arm of the same ramp | `.Skeletonized(false)`; hover 1.04 / press 0.96 (`ScaleStandard`) | `HomeCards.cs:341-356`, `WaveeCta.cs:61-151` |
| **weekly card** | auto × 88 | pad 16, gap 12 | Card 8 | BodyLarge 18/24/600 display −8 over Caption 12/16 ×2 | `Interaction.Card` + spine | lift −4 | `HomeCards.cs:440-473` |
| weekly art | 56 | — | Control 4 | — | decode 128 | — | `HomeCards.cs:449` |
| **quick tile** | auto × 56 | title margin 12 / 12; play pad-right 12 | Control 4 | BodyStrong 14/20, 2 lines | `Interaction.Card` + spine | lift −4 | `HomeCards.cs:490-513` |
| quick art | 56 flush | — | Control 4 | — | decode 128 | — | `HomeCards.cs:498` |
| quick hover play | 28 circle | — | Full | glyph 13 | `Tok.AccentDefault` plate, `Tok.TextOnAccentPrimary` glyph | opacity 0→1, scale 1.07 | `HomeCards.cs:200-213` |
| **mix cell** | (A/cols) × 142 | pad 16, gap 2 | 0 (the plate is rounded) | Title 28/36 display −40 · Eyebrow · Caption ×3 | numeral = accent ?? `Tok.TextPrimary`; wash = accent @ A 0.09; `FillSubtleTransparent` → `FillSubtleSecondary` | inside ONE plate | `HomeCards.cs:522-580` |
| mix plate | A × (cells + 2) | — | Card 8 | — | `Tok.FillCardDefault`, 1px `Tok.StrokeCardDefault` | ClipToBounds | `HomeModules.cs:205-212` |
| mix dividers | 1 | — | — | — | `Tok.StrokeDividerDefault` | hit-test off | `HomeCards.cs:561-571` |
| **chip card** | auto × 96 | pad 12, art gap 12, stack gap 8 | Control 4 | BodyStrong over chips over Caption | `Interaction.Card` + spine | lift −4 | `HomeCards.cs:586-614` |
| chip | auto | pad 8 × 2, gap 4 | Control 4 | Caption 12/16 | `Tok.FillSubtleSecondary` | — | `HomeCards.cs:159-164` |
| **radio row** | auto × 48 | pad L8/R8, gap 12 | Control 4 | BodyStrong over Caption | `Interaction.ListRow` | no spine, no lift | `HomeCards.cs:620-640` |
| radio art | 32 | — | Full | — | decode 64 | — | `HomeCards.cs:633` |
| radio hover play | 24 circle | — | Full | glyph 11 | transparent plate, `Tok.TextSecondary` glyph | opacity 0→1 | `HomeCards.cs:636` |
| **queue row** | auto × 53 | pad 8, gap 12 | Control 4 | BodyStrong over Caption | `Interaction.ListRow`; divider `Tok.StrokeDividerDefault` | — | `HomeCards.cs:647-707` |
| queue art | 56×32 video / 32×32 audio | — | Control 4 | — | decode 64 | ClipToBounds | `HomeCards.cs:652-663` |
| resume hairline | w × 2 | — | 0 | — | `Tok.AccentDefault` | bottom-left of the art | `HomeCards.cs:709-713` |
| queue time cell | 56 | — | — | Caption ×2 | time `Tok.TextSecondary`, state `Tok.TextTertiary` | fixed width (no tabular figures) | `HomeCards.cs:684-693` |
| **book row** | auto × 64 | pad 8, gap 12, rating gap 8 | Control 4 | BodyStrong over Caption; value Caption 600 | `Interaction.ListRow` | — | `HomeCards.cs:729-761` |
| book art | 48 | — | Control 4 | — | decode 128 | — | `HomeCards.cs:754` |
| rating strip | 5 × 16, gap 8 (112) | — | — | — | read-only placeholder brush `Tok.TextPrimary` | — | `RatingControl.cs:102`, `:168-169` |
| book length cell | 32 | — | — | Caption | `Tok.TextTertiary` | fixed width | `HomeCards.cs:743` |
| **feature card** | auto × 188 | pad 20, gap 20, footer pad-top 12 | Card 8 | Subtitle 20/28 display −12; Desc 12 ×3 | `Interaction.Card` + spine | lift −4 | `HomeCards.cs:767-823` |
| feature art | 148 | — | Card 8 | — | decode 256 | — | `HomeCards.cs:778` |
| "Editorial" tag | auto | pad 8 × 2 | Control 4 | Eyebrow 12/16/600 | ink `WaveeAccent.Decor` (= `Tok.AccentTextPrimary`); border 1 = `Lerp(StrokeControlDefault, accent, 0.40)` | — | `HomeCards.cs:786-800` |
| **crowd row** | auto × 72 | pad 12, gap 12 | Control 4 | BodyStrong over Desc(1) | `Interaction.Card` + spine, `Grow = 1` | lift −4 | `HomeCards.cs:827-852` |
| crowd hover play | 30 circle | — | Full | glyph 13 | transparent plate | opacity 0→1 | `HomeCards.cs:848` |
| **timeline row** | auto × 56 | pad L16 T8 R8 B8, gap 12 | Control 4 | BodyStrong over KindTag + Caption | `Interaction.ListRow` | — | `HomeCards.cs:902-956` |
| timeline art | 40 | — | Control 4 (square) or 20 (round act) | — | decode 64 | — | `HomeCards.cs:913`, `HomeModules.Timeline.cs:122` |
| kind tag | auto | pad 4 × 0 | Control 4 | Eyebrow | 1px `Tok.StrokeControlDefault`, ink `Tok.TextTertiary` | — | `HomeCards.cs:959-968` |
| new pill | auto | pad 4 × 0 | Control 4 | Eyebrow | `Tok.AccentDefault` plate, `Tok.TextOnAccentPrimary` ink | the ONE accent plate behind text on Home | `HomeCards.cs:971-982` |
| pip | 7 × 7, ring 1.5 | margin-left −4 | Full (3.5) | — | unread `Tok.AccentDefault` / seen `Tok.FillLayerDefault` + `Tok.StrokeControlStrongDefault` | hit-test off | `HomeCards.cs:942-953` |
| day column | 96 | pad-top 12 | — | Caption 600 secondary + Caption tertiary | — | align End | `HomeModules.Timeline.cs:30`, `:58-61` |
| **ranked avatar** | art 76/60/46 × scale | pod pad L2 T8 R2 B8, gap 8 | Full | Caption 600, 2 lines, centred | `Interaction.Subtle`; ring 3 `Tok.AccentDefault` when hub | — | `HomeCards.cs:991-1065` |
| rank plate | 20 × 20 (28 wide at ≥ 10) | — | Full | Caption 600 | unselected `Tok.FillControlSolid` + 1px `Tok.StrokeCardDefault`, ink `Tok.TextSecondary`; selected `Tok.AccentDefault` + `Tok.TextOnAccentPrimary` | anchored to the ART's top-left | `HomeCards.cs:1034-1053` |
| podium card | auto | pad 12, gap 8, wrap | Card 8 | — | `Tok.FillCardDefault`, 1px `Tok.StrokeCardDefault` | `Animate = CardResizeHeight` | `HomeModules.Artists.cs:131-145` |
| disclosure left pane | Grow, Basis 0 | pad 12 all round, stack gap 8 | — | head: BodyStrong + Caption tertiary facts | — | — | `HomeModules.Artists.cs:334-339`, `:285-303` |
| disclosure head Play | 32 (`Button.Create` Standard + `Icons.Play`) | — | Control ramp | Button label 14 | `ButtonAppearance.Standard` — a STOCK control, **not** a `WaveeCta` pill (the hero's 36-DIP grammar does not reach inside the disclosure) | — | `HomeModules.Artists.cs:300-301` |
| "In your top N" badge | auto | pad 8 × 4 | Full | Eyebrow 12/16/600 + tracking 30, 1 line, ellipsis | plate `Tok.SystemFillSuccessBackground`, ink `Tok.SystemFillSuccess` — a SEMANTIC colour, deliberately outside the page's accent budget | in the row's 160-DIP actions cell, right-aligned beside `TrackRow.MoreButton(true)` | `HomeModules.Artists.cs:342-361` |
| **mixview pane** | 342 fixed (wide); full width in the Spine tier | pad 12, gap 12 | — | BodyStrong + Caption tertiary | — | — | `HomeModules.Artists.cs:231`, `:437-442` |
| mixview empty graph | height 120 | — | — | — | `.Skeletonized(true)` block — `related.Count == 0` never renders an empty pane | — | `HomeModules.Artists.cs:428` |
| ring node | hub d 68, node d 42 | — | Full | — | hub ring 3 `Tok.AccentDefault` | — | `HomeModules.Artists.cs:511`, `:626-640` |
| ring connector | 1 thick | — | — | — | `Tok.TextTertiary` @ A 0.26 | `PolylineStrokeEl`, SOLID (the prototype's are dashed — the leaf carries no dash pattern) | `HomeModules.Artists.cs:521-526` |
| node cap | width 104 | top = cy + r + 5 | — | Caption; hub 600 primary, node 400 secondary | — | centred by the parent | `HomeModules.Artists.cs:648-664` |
| spine hub / node | 40 / 32 | gap 8; row pad L8 T4 R4 B4 | Full | BodyStrong / Caption secondary | hub ring 3 accent | 1-DIP rule at `Tok.TextTertiary` @ 0.26 | `HomeModules.Artists.cs:586-623` |
| **Fold tile** | cardW × 176 | copy pad **L 20 / T 20 / R 12 / B 18** (`Edges4` is L,T,R,B), gap 4 | Card 8 | FoldTitle 28/36 display 400 −12, 3 lines | `Tok.FillCardDefault` → `Tok.FillCardSecondary`, 1px `Tok.StrokeCardDefault` | `Elevation.Card` (dark: blur 8, y 2, #00000033; light: blur 4, y 2, #0000001A) | `HomeFoldTile.cs:105-128` |
| Fold cover | 124 × 124 | — | Control 4 | — | decode 128 | `Elevation.Card`, hit-test off | `HomeFoldTile.cs:65-85` |
| Fold wash | full card | — | — | — | radial `accent @ 0.22` → `@ 0` at 0.72, centre (1.0, 0.48), radius (0.70, 1.10) | hit-test off | `HomeFoldTile.cs:40-53` |
| Fold eyebrow | — | — | — | Eyebrow | `Tok.AccentTextPrimary` | **null at every live call site** | `HomeFoldTile.cs:91-96` |
| Fold bone | cardW × 176 | — | Card 8 | — | `SkeletonStyle.Default.BarColor` (= `Tok.FillSubtleSecondary`) | — | `HomeFoldTile.cs:134-140` |

---

## 4. Colour & material

### 4.1 The card identity colour — the one derivation

```
HomeCard
  ├ Meta.Accent != 0 ?  → WaveePalette.ToColor(accent)                 (payload extractedColors.colorDark)
  └ else Surfaces.SchemeFor(Image.Url)                                  (the graded cover plane, theme-following)
       └ BackgroundTintedBase != 0 ? that : BackgroundBase
            └ 0 → NULL  → PAINT NOTHING
                                                            HomeCards.cs:47-58
RawAccent → WaveePalette.Lift(seed, targetMax 210)  = Accent(c)         HomeCards.cs:60-61
```

`Lift` scales RGB uniformly so the strongest channel reaches 210/255 — it only ever brightens, so a near-black
`colorDark` still reads on a dark card and cannot bruise a light one (`WaveePalette.cs:25-33`).

**Why `BackgroundTintedBase` and not `textBrightAccent`:** the payload path is a dark, saturated tone; the two
sources must speak the same language or a section with payload colours and one without read as two design systems.
`textBrightAccent` is graded for TEXT on the cover and came out near-white as a 2-DIP hairline (`HomeCards.cs:50-53`).

| consumer | function | input → output | where applied | transition |
|---|---|---|---|---|
| spine | `SpineAccent(c, hovered)` `HomeCards.cs:77-83` | `RawAccent` → (S ≤ 0.08 ? `SpineFallback`) → `Hairline` 3.25:1 / `HairlineHover` 3.55:1, S capped at 0.50, V binary-solved 22 iterations against the flattened card fill | `HomeAccentLeaf.Spine` `Fill`/`HoverFill` (`:1171-1178`) | engine hover fade, 83 ms `FluentPopOpen` |
| mix wash | `Accent(c) with { A = 0.09 }` `HomeCards.cs:1184` | lifted accent at 9% | the cell's full-bleed ZStack layer | repaints on the leaf's own watch |
| mix numeral | `Accent(c) ?? Tok.TextPrimary` `HomeCards.cs:1193` | lifted accent | Title 28/36 glyph | the numeral KEEPS its slot in primary ink until the grading lands — it is content, not decoration, so it must not pop in |
| hero wash | `Surfaces.HomeHeroBackdrop(AccentOrChrome(c))` `Surfaces.cs:437-445` | `Lerp(FillCardDefault, accent, dark 0.10 / light 0.06)` at stop 0, `FillCardDefault` from 45% down; the card's own alpha is preserved so only the HUE shifts | the hero surface gradient | none (the hero re-renders whole) |
| hero veil | `Surfaces.ArtistHeroVeil(accent, axis)` `Surfaces.cs:173-192` | `veil = Lerp(FillLayerDefault, accent, light 0.16 / dark 0.24)`; H: A 0.96 / 0.92 @30% / 0.35 @62% / 0 · V: 0 / 0.35 @45% / (dark 0.78, light 0.42) @82% / 0 | over the artwork, under the copy | none |
| hero CTA + countdown | `AccentOrChrome(c)` = `Accent(c) ?? Tok.AccentDefault` `HomeCards.cs:88` | chrome MUST paint something, so this ONE derivation has a fallback — and that is exactly why the spines do not | the Play capsule fill; the countdown re-grades it with `TextInk` (4.5:1) before using it as ink | — |
| "Editorial" tag border | `Lerp(Tok.StrokeControlDefault, accent, 0.40)` `HomeCards.cs:790` | 40% toward the card accent | 1-DIP border | — |
| Fold wash | `WaveePalette.ToColor(cards[0].Meta.Accent)` `HomeFoldTile.cs:32-53` | the FIRST card's payload accent ONLY (0 → no wash layer at all; the graded plane is NOT consulted here) | radial gradient layer | static (the prototype's hover 0.22 → 0.34 is NOT implemented — see §9) |
| near-neutral rescue | `SpineFallback(c)` `HomeCards.cs:63-75` | `Hash(uri) & 3` → `Tok.AccentDefault` / `SystemFillSuccess` / `SystemFillCaution` / `SystemFillCritical` | only when a REAL seed exists but S ≤ `NeutralS` 0.08 | — |

### 4.2 Light / dark differences

* Every token above is theme-live (`Tok.*` re-reads per access), so a theme switch re-resolves in place.
* `Hairline` searches V from black→white on dark and keeps the LIGHTER solution; on light it keeps the DARKER one
  (`WaveePalette.cs:98-116`). The same seed therefore yields two different spines, both at 3.25:1.
* `HomeHeroBackdrop` / `SectionBand` lerp 0.10 in dark vs 0.06 in light — a faint tint vanishes on a dark plate.
* `ArtistHeroVeil` vertical peak: 0.78 dark vs 0.42 light. Horizontal is theme-invariant (0.96/0.92/0.35/0).
* `Elevation.Card`: dark blur 8 / y 2 / #00000033; light blur 4 / y 2 / #0000001A.
* `Surfaces.ArtworkPlaceholder`: dark #2A2A2A, light #F2F2F2, tinted toward the cover's graded colour when known.

---

## 5. Motion

Every animation below is engine-serviced off the frame clock (`AnimScheduler` / `FrameTime`). **One exception,
which must be carried forward knowingly:** `WaveeEqualizer` ticks its bars off `Environment.TickCount64`
(`Components/Equalizer.cs:105`, `:128`) — it is a 30 Hz quantised loop, not a media-synced animation, and it is the
only `TickCount64` read that DRIVES MOTION on this surface. Three more read it for INPUT timing — the podium's
`PodClick` and the Mixview panel's `NodeClick`/`HubClick` (`HomeModules.Artists.cs:187`, `:468`, `:483`) feed the
reading into the pure `HomeArtistRowLayout.IsDoubleClick` (400 ms window, `HomeArtistRowLayout.cs:85-92`). Input
timing is wall-clock by definition and must NOT be moved onto the frame clock.

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| hover a card skin | card root | OffsetY | 0 → −4 | 250 ms | `FluentStandard` cubic-bezier(0.8, 0, 0.2, 1) | — | `KeepFade`: the token's policy parks the offset, the fill still cross-fades | `MediaCard.cs:68-74` |
| press a card skin | card root | Scale + OffsetY | 1 → 0.99, 0 → −1 | 250 ms | `FluentStandard` | — | parked | `MediaCard.cs:72` |
| hover a card skin | card plate | Fill | `FillCardDefault` → `FillCardSecondary` | 83 ms | `FluentPopOpen` (engine hover fade) | — | fade kept | `Interaction.cs:141-149` |
| hover a card skin | spine leaf | Fill | `Hairline(seed)` → `HairlineHover(seed)` | 83 ms | `FluentPopOpen` | — | fade kept | `HomeCards.cs:1176-1177` |
| hover a card | paint order | `HoverElevatePaint` | below → above later siblings | instant | — | — | unaffected | `MediaCard.cs:70` |
| hover a tile/row with a play | `HoverPlay` | Opacity + Scale | 0 → 1, 0.86 → 1 (`ScaleEmphatic.Hover` 1.07) | 150 ms (`MotionTok.ControlFast`) | `FluentStandard` | — | `ScaleTier.Hover` returns 1 → opacity only | `HomeCards.cs:200-213` |
| hover a mix cell | cell root | Fill | `FillSubtleTransparent` → `FillSubtleSecondary` | 83 ms (`BrushTransitionMs`) | engine brush fade | — | kept | `HomeCards.cs:576-577` |
| hover a row skin | row root | Fill | transparent → `FillSubtleSecondary` (pressed `FillSubtleTertiary`) | 83 ms | `FluentPopOpen` | — | kept | `Interaction.cs:133-139` |
| hover a Fold tile | card fill | Fill | `FillCardDefault` → `FillCardSecondary` | 83 ms | engine hover fade | — | kept | `HomeFoldTile.cs:123` |
| hover a Fold tile | each of ≤3 covers | OffsetX/Y + Rotation (additive DELTAS on the authored rest pose) | (0,0,0) → (−10,+6,−5°) / (+2,−6,+3°) / (+10,0,+3°) | 250 ms (`MotionTok.ControlNormal`) | `FluentStandard` | none (all three move together) | the token carries `KeepFade`; **no `if (ReducedMotion)` branch anywhere in the file** | `HomeFoldTile.cs:63-76` |
| hover a shelf chevron | the chevron itself | Fill | `Tok.FillControlDefault` → `Tok.FillControlSecondary` | engine hover fade | engine | — | fade kept | `PagedShelf.cs:1402-1411` — there is **no reveal and no scale**: the pair is painted at opacity 1 (0.35 when the end is reached) at all times. The 0.7-rest/1.04-scale/header-scoped reveal in `Components/Rail.cs:95-108` belongs to a class nothing instantiates (§9.4) |
| cover decode lands | hero header image | Opacity | 0 → 1 | 300 ms (`StandardEnter`) | `FluentDecelerate` | — | fade kept | `HomeCards.cs:395` |
| cover pending | `CoverShimmer` tile | Opacity | 1 ↔ 0.5 loop | 1 000 ms | linear keyframes | — | the loop is replaced by a flat track once ready | `Surfaces.cs:448-456` |
| skeleton → content (artist disclosure) | each mounted row | Opacity + TranslateY + Blur | 0 → 1, +8 → 0, 3 → 0 | 500 ms (`VerySlow`) | `SmoothOut` | 40 ms per row (`Expressive.Stagger`) | engine `ReducedSnap` parks rise+blur, keeps the fade | `HomeModules.Artists.cs:242`, `SkeletonRegion.cs:169-183` |
| skeleton → content (Charts row) | — | — | `SkelReveal.None`: the content owns its entrance; the shimmer orphan lingers (ExitMs floored at 400 ms) and cross-dissolves | — | — | — | snap | `HomePage.cs:746` |
| podium selection opens | disclosure block | Position + Opacity | Dx +8 → 0, 0 → 1 (forward) / Dx −8 → 0 (back) | 250 ms (`Expressive.Fast`) | `SmoothOut` | — | `KeepFade` | `HomeModules.Artists.cs:152`, `MotionRecipes.cs:193-203` |
| podium selection opens/closes | podium card | Height (real layout, neighbours reflow) | old → new | 300 ms | `SmoothOut` | — | snap | `HomeModules.Artists.cs:142`, `MotionRecipes.cs:246` |
| daylist second ticks | one digit cell | keyed remount: old digit exits up, new rises | Dy ±(0.35 × 28 = 10) + Opacity | 150 ms (`ControlFast`) | `FluentStandard` | — | degrades to a cross-fade (`KeepFade`) | `FlipCountdown.cs:135-155` |
| now playing | equalizer bars | ScaleY per bar | three phase-offset patterns, quantised to whole device pixels; row gap 2, bottom-anchored (`AlignItems = End`) | 850 ms loop at ~30 Hz (`1000/30`) | sampled table | per-bar phase | see the reduced-motion row below | `Equalizer.cs:61-118` |
| paused (context still open) | equalizer bars | ScaleY | **all three snap to a FLAT 0.4** — not "frozen at the last sample". `HomeNowPlaying` passes only `playing` (`WaveeEqualizer.Of(playing, …)`, `HomeCards.cs:1130`) and never the optional `paused` signal, so a pause flips `animate` false and `WriteAll(0.4f)` runs | instant (a batched signal write, no track) | — | — | — | `Equalizer.cs:100`, `:153-160`, `HomeCards.cs:1130` |
| reduced motion **while playing** | equalizer bars | ScaleY | a settled NON-UNIFORM snapshot (`Sample(pattern, 0.3)` per bar) — never a loop, never a flat bar (flat would read as paused) | instant | — | per-bar phase | this IS the reduced-motion form | `Equalizer.cs:101-104`, `:166-174` |
| Fold deck / shelf paging | the strip | scroll offset | page stride | engine `BringItemIntoView` | engine | — | engine policy | ch. 02 |

**Deliberately absent** (do not add them back): no scale on a card footprint (only the −4 lift), no `CardHover`
elevation band on hover (a card is a card, hovered or not — `MediaCard.cs:89-93`), no wash-opacity animation on the
Fold tile, no dashed connectors in the Mixview ring, no per-row entrance on Home's own virtual list (an entrance
replayed mid-scroll reads as flicker — `WaveeMotion.cs:94-100`).

---

## 6. Interaction

### 6.1 Click targets, per module

| surface | left click | hover | right click / Menu key | keyboard |
|---|---|---|---|---|
| hero surface | `onNav` — but only from the ARTWORK box (`HomeCards.cs:389`, `:413`); the copy column is not clickable | veil/wash unchanged | the band's attached `Menus.CardAttach`; the "…" capsule re-enters the engine's context funnel (`ClickRequestsContext`) rather than owning a handler | the four capsules are focusable buttons |
| hero Play / Shuffle / ♥ | `PlayCard` / `ShuffleCard` / `lib.ToggleSaved` | capsule 1.04 hover, 0.96 press | — | Space/Enter on each |
| weekly / chip / feature / crowd / quick / mix cell | `NavCard` (`HomeCardNav.Open`) | card lift + spine + plate | the entity's card menu | `Role = Button`, focus ring |
| quick tile / radio row / crowd row play puck | `PlayCard` (routes through `HomeCardPlayRouting.PlaysAsItem`) | puck 0 → 1 | — | focusable |
| radio / queue / book row | row nav (`Role = Button`) | subtle fill | the entity's card menu | focus ring |
| timeline row — CONCERT | `nc.MarkRead(id)` **then** `NotificationPanel.ClickSocial` (the notification center's own destination, verbatim) | subtle fill | none | focus ring |
| timeline row — RELEASE | navigates only; it does **not** mark itself read (`HomeModules.Timeline.cs:124`), so the pip and the bell badge both stay lit. Album/show → `RichText.RouteForUri`; an EPISODE has no app route, so it opens the **web player** (`SpotifyLink.WebUrl` → `LoginView.OpenUrl`, `:136-152`) | subtle fill | none | focus ring |
| module header | `openSection` / `openAll` | — | — | `Role = Hyperlink`, `Focusable = true` |
| podium pod | select / deselect; **double click within 400 ms → the artist page** | subtle fill | ONE item: "Go to artist" (`Strings.Detail.GoToArtist`, `ActionIcons.Artist`) | `Role = Tab` |
| Mixview node | re-centre the hub (the graph AND the left pane); **double click → the artist page** | — | "Go to artist" | `Role = Button` |
| Mixview hub node | single click is a deliberate no-op (it is already the centre); a second click within the window navigates | — | "Go to artist" | — |
| Fold tile | `openTile` → `HomeCardNav.OpenBrowseSection` (a ONE-card section opens the card itself; otherwise the browse section page) | fill swap + cover fan | — | `Role = Hyperlink`, `Focusable = true` |
| description anchors (chip card, feature, crowd, feed) | `RichText.RouteForUri` → navigate; unroutable hrefs are ignored | accent hyperlink ink | — | — |

### 6.2 Card navigation (`HomeCardNav.Open`, `HomeSectionNavigation.cs:42-83`)

`Liked` → the `liked` route · `Track`/`Episode` → **play**, never navigate (there is no episode page) ·
`Artist` → `artist:<uri>` · `Album` → `DetailNav.OpenAlbum` with a preview record · `Podcast`/`Audiobook` →
`show:<uri>` · default → the playlist route. Every drill out of Home carries `NavOrigin("Home", "home")`.

### 6.3 Play routing

`HomeCardPlayRouting.PlaysAsItem(kind)` — `Track`/`Episode` → `PlayTrackAsync(uri)`; everything else →
`PlayAsync(uri, 0)` as a CONTEXT. A track uri sent as a context is the fire-and-forget that used to do nothing.

### 6.4 Drag and drop

Source: every card of every module **except** `Track`/`Episode` (`HomePage.cs:294-298`). Payload
`WaveeResourceDragPayload.ForEntity(kind, uri, title, image)`. Attached by `Keyed` to the card ROOT, never to a
spacing wrapper. Targets and the chip visual: ch. 25 / ch. 01.

### 6.5 Context menus (`Menus.Card`, `Actions/Menus.cs:544-603`)

Header = cover + name + kind label ("Album" / "Artist" / "Playlist", `Menus.cs:607-613`; a card passes its own
plain-text subtitle when it has one, else the kind label). Strip = Play · Play next · Add to queue · Save (queue
pair only when the kind has a resolvable track set; Liked Songs drops Save). Rows in grammar order: Follow (artists
only) · Add to playlist ▸ · Open · Pin/Unpin · Go to artist (albums) · Share ▸ · Go to artist radio (artists).
Shows: Play · Open · Pin · Share. Track uris: the thin track menu. Episode uris and unknown schemes: **no menu**.

**An ARTIST card is the shortest container menu, and it is short in TWO places, not one.** `ContainerTracks.CanResolve`
is false for an artist, so it loses the queue pair *and* `Add to playlist` (`Menus.cs:570-582`, `:586-603`): its strip
is **Play · Follow(Save)** and its rows are **Follow · Open · Pin · Share ▸ · Go to artist radio**. Every liked
spelling routes too — a Home/recents card can carry `spotify:user:<u>:collection`, whose `EntityKind` is `Collection`,
and `LikedSongsArtwork.IsLikedUri` is what stops that card falling through to `TargetKind.None` and getting NO menu
at all (`Menus.cs:556-563`).

### 6.6 Accessibility names and roles

`Role = Button` on every card and row; `Role = Hyperlink` on module headers and Fold tiles; `Role = Tab` on podium
pods. Visible strings are localised: `home.dailyMix`, `home.video`, `home.resume`, `home.unplayed`, `home.seen`,
`home.newBadge`, `home.editorial`, `home.play`, `home.topTracks`, `home.mixview`, `home.relatedCount`,
`home.inYourTop`, `home.topArtists`, `home.topArtistsSub`, `home.charts`, `home.chartsEmpty`, `home.sections`,
`home.mostOpenedOf`, `home.oneSeries`, `home.mixesFromArtists`, `home.stationCount`, `home.queuedSuggestions`,
`home.includedWithPremium`, `home.unheardOf`, `home.recommendationsWithReason`, `home.editorsPicksSub`,
`home.songsBy`, `home.nextUpdateAt`, `home.newReleases`, `home.greeting`, `home.heroEyebrow`, `home.yourDaylist`,
and the five Fold-tile eyebrow words `home.chartEye` ("charts") / `home.weeklyEye` ("weekly") / `home.dailyEye`
("daily") / `home.videoEye` ("video") / `home.podcastsEye` ("podcasts") — authored, unreachable today (§9.4)
(all in `src/apps/Wavee/assets/loc/en-US.json`), plus `detail.shuffle`, `detail.play`, `detail.songCount`,
`detail.durationHrMin`, `detail.durationMin`, `detail.goToArtist`, `detail.today`, `detail.yesterday`,
`detail.daysAgo`, `detail.factReleases`, `podcast.show`, `concerts.detail.concert`, `concerts.liveMusic`,
`artist.metaMonthly`, `artist.worldRank`.

**No `ToUpper` anywhere.** The eyebrow role gave up caps deliberately: a caps transform on a localised string mangles
Turkish dotted i and expands German ß, and the hero's eyebrow carries the USER'S OWN NAME (`WaveeType.cs:43-47`,
`HomeCards.cs:303-307`).

**One string is nonetheless authored in caps**: `home.dailyMix` = `"DAILY MIX"` in `en-US.json`, so the mix cell's
eyebrow RENDERS as caps even though no code upper-cases it. That is a copy decision a translator may reverse per
locale, which is exactly the point of keeping the transform out of the code. Do not "fix" it in the renderer;
`home.newBadge` = "New", `home.seen` = "Seen", `home.video` = "Video", `home.resume` = "resume" and
`home.unplayed` = "unplayed" are the sentence/lowercase neighbours it sits beside.

### 6.7 Number and duration formatting (culture-aware, ported verbatim)

`Duration(ms)` → `"{h} hr {m} min"` past the hour else `"{m} min"`, minimum 1 (`HomeCards.cs:248-254`).
`Hours(ms)` → `"1.3 h"` past the hour else `"45 m"` (audiobooks only, `:256-264`).
`CompactNumber(n)` → `B` / `M` / `K` to one decimal, else `N0` (`:1081-1088`).
`Mmss` exists at `HomeModules.Artists.cs:375` and is **unused** (dead).

---

## 7. Data & readiness in 0.3 terms

### 7.1 What each visual element reads

| visual element | 0.2.9 source | 0.3 read | readiness predicate |
|---|---|---|---|
| module order + which module | `HomeLanding` projection over `HomeFeed.Groups` (ch. 10) | `Home.Sections` synthetic parents + a `Kind` column per section | the whole landing is Ready (ch. 10 §0.1) — modules never appear one by one |
| module title / subtitle | `HomeGroup.Title` (server, verbatim) / app copy | `HomeSection.TitleId` (StringId) + the module's own loc string | `Knows(HomeFields.Title)`; a blank title renders NO header |
| drill affordance | `group.Uri`/`TotalCount`/`Cards.Count` gate | `section.Uri != 0 \|\| section.Total > 0 \|\| edge length > 0` | same gate; the chevron is absent, never dead |
| card cover | `HomeCard.Image` (+ `MosaicTiles`) | `Image` (StringId) on the card's target entity; mosaic = a 4-slot edge or a packed StringId list | never gated — `Surfaces.Artwork` owns the placeholder |
| card title | `HomeCard.Title` | target entity `Title` | `Knows(Identity)` on the target; until then the derived shimmer |
| card subtitle / description | `HomeCard.Subtitle` (`description ?? ownerName`) | **needs a separate column** — see the gaps | — |
| card kind (shape: round vs square, menu grammar, nav route) | `HomeCard.Kind` | `EntityUri.Kind` of the edge target — already in the plan | free (parsed at the wire) |
| spine / wash / numeral colour | `Meta.Accent` else `CoverColorPlane` | `HomeCardEdge.Accent` (uint) else the palette plane | NEVER gated: null = paint nothing, and the leaf repaints when the grading lands |
| track count ("50 songs", the weekly's trailing count) | `Meta.TrackCount` | `Playlist.TrackCount` column, or the edge payload | `Knows(Counts)`; 0 renders NOTHING (not "0 songs") |
| owner ("· by Spotify") | `Meta.OwnerName` | `Playlist.Owner` (user slot) → `User.Name` | `Knows(Owner)`; absent → the count alone |
| seeds (mix chips, hero tags, station line) | `Meta.Seeds` | **gap** — a per-card string list | absent → the chip card falls back to the description; the mix cell drops the line |
| episode duration / resume / video | `Meta.DurationMs`, `ResumeMs`, `HasVideo` | `Episode.DurationMs`, an `Episode.ResumeMs` column, `TrackFields.Video` flag | resume 0 → no hairline; `HasVideo` decides the 56×32 vs 32×32 art |
| audiobook rating / author / length | `Meta.Rating`, `Author`, `DurationMs` | **gap** — no audiobook columns in the plan | rating 0 → the length cell alone |
| daylist window | `Meta.ExpiresAtMs` / `CreatedAtMs` | **gap** | 0 → the pulse row is reserved but empty |
| authored header photo | `Meta.HeaderImageUrl` | **gap** | absent → the square-cover arm |
| discover-feed reason | `HomeCard.Eyebrow` | **gap** | absent → the card shows its subtitle only |
| now-playing mark | `PlaybackBridge.HasActiveContext` → `Identity` → `IsPlaying` | `Playback.Current` / `Playback.ContextUri` / `Playback.IsPlaying` signals | coarse-gate first (`HasActiveContext`), so an idle page never joins the identity fanout |
| liked-collection artwork | `LikedSongsArtwork.For(uri, …)` (the user's treatment) | same helper over `User.Me` liked edges (ch. 07) | the URI is the whole gate — even a card that arrived WITH stock liked art takes the user's treatment |
| top artists (podium) | `IUserTopService.GetTopArtistsAsync` (30-min transport cache) | **gap** — affinity ranking has no table | `artists.Count == 0` → the whole row renders `new BoxEl()` |
| "in your top N" badge | `IUserTopService.GetTopTracksAsync` | **gap** | absent → no badge, the row geometry is unchanged |
| monthly listeners / world rank | `Artist.MonthlyListeners`, `WorldRank` | `Artist` cold group | each half printed ONLY when > 0 (`#0 worldwide` is a fact that is not one) |
| related artists (Mixview) | `Artist.Extras.Related` at `HydrationLevel.Rich` | `Edges.ArtistRelated` | a warm artist renders immediately and revalidates under; only a cold one shimmers |
| top tracks (disclosure) | `Artist.TopTracks` | `Edges.ArtistPopular` | 5 rows of the ROW SHAPE while pending — never a spinner, never empty |
| timeline rows | `NotificationCenterBridge.Items` → `HomeTimelineMerge.Build` | **gap** — no notification model in the plan | `feed.IsEmpty` → the whole module renders nothing. No `EnsureFresh` on mount: go-live primed the bridge, and re-priming per Home mount would refetch on every back-navigation (`HomeModules.Timeline.cs:17-21`) |
| timeline unread state | `s.IsUnread` / `r.IsUnread` off the SAME bridge; `nc.MarkRead(id)` writes back through it | the `Notification` table's unread bit | reading and writing the same store is what keeps the pip, the header counter and the bell badge in agreement |
| Charts deck | 5 × `browseSection` (`ChartSections.All`) | `Browse.cs` sections + `Edges.HomeSection` | Featured null + a live catalog → **fail loud**; no live catalog → an empty deck; any LATER section that is null or card-less is silently omitted, so the deck is 1..5 tiles |
| Fold tile covers | `section.Cards[0..2].Image` | the first three targets of `Edges.HomeSection` | a missing slot is OMITTED, never padded with a repeated cover |

**Demand discipline.** Every module renders a bounded, already-decided slice — `QuickShown 8`, `ChipCardsShown 6`,
`RadioShown 12`, `QueueShown 6`, `BooksShown 6`, `EditorialCompanions 3`, `HomeTimelineMerge.MaxRows 8`
(`HomeModules.cs:588-594`) — and the page demands its whole model on mount. No module manages a visible window;
`PagedShelf` virtualizes what is already in hand. Keep it that way.

**Derived facts live on the model.** `Shown(kind, count)`, `ContentExtent(kind, width, count)`,
`RowHasContent(landing, row)` and the module subtitles are computed from the model, never probed from the rendered
tree. In 0.3 they belong to the CORE `Home.Layout` section, not to the page.

### DATA GAPS

Everything this surface paints that the plan's data model (§4.1-4.3) does not yet hold. `HomeCardEdge` below is a
proposed payload struct for `Edges.HomeSection` (today `EdgeTable<NoEdge>`, plan §4.3) — the section→card edge is
the natural home for facts that belong to *this card in this section*, not to the entity.

| element | 0.2.9 source | proposal |
|---|---|---|
| identity colour (spine, mix wash + numeral, hero wash/CTA, Fold wash, tag border) | `HomeCardMeta.Accent` ← Pathfinder `extractedColors.colorDark`; decoded in `SpotifyHomeComposer` | `HomeCardEdge.Accent` (uint ARGB, 0 = unknown). The graded-cover fallback stays a palette-plane read, not a column. |
| card description (chip card, feature, crowd, feed) — an HTML fragment with anchors | `HomeCard.Subtitle` | `HomeCardEdge.Subtitle` (StringId, raw HTML kept — `RichText` renders it and routes its anchors) |
| the section's REASON for this card ("For fans of IU") | `HomeCard.Eyebrow` ← the baseline section title | `HomeCardEdge.Eyebrow` (StringId) |
| mix/daylist seeds — the chip run, the hero tags, the station's "who seeded it" | `HomeCardMeta.Seeds` (`IReadOnlyList<string>`) | `EdgeTable<StringId> HomeCardSeeds` keyed by the card edge slot (the same shape as `TrackTags`), ordered, max 6 read |
| playlist format token (`daylist`, `daily-mix`, `inspiredby-mix`, `editorial`) — what the composer ROUTES on | `HomeCardMeta.Format` | `HomeCardEdge.Format` (byte enum; the wire token interned once at decode) |
| playlist owner (the "· by {owner}" meta line) | `HomeCardMeta.OwnerName` | `Playlist.Owner` (user slot) — already implied by the plan's `User` table; make it a real column |
| track count on a home card | `HomeCardMeta.TrackCount` | `Playlist.TrackCount` (int) + a `PlaylistFields.Counts` bit |
| episode resume position | `HomeCardMeta.ResumeMs` | `Episode.ResumeMs` (int, seconds) + `EpisodeFields.Progress` |
| episode has a video track | `HomeCardMeta.HasVideo` | the plan's `TrackFlags`/`VideoCounterpart` already covers tracks; add the same flag to `EpisodeFlags` |
| audiobook rating (2 dp), author, access signifier | `HomeCardMeta.Rating/Author/Signifier` | `Show` cold group: `Rating` (ushort ×100), `Author` (StringId), `Signifier` (StringId), gated by `ShowFields.Audiobook` |
| daylist window (`expires`, `created`) | `HomeCardMeta.ExpiresAtMs/CreatedAtMs` | `HomeCardEdge.ExpiresAt`, `HomeCardEdge.CreatedAt` (int, seconds since app epoch) |
| authored desktop header image | `HomeCardMeta.HeaderImageUrl` | `HomeCardEdge.HeaderImage` (StringId) |
| shallow-identity flags (`daylist_pretitle`, `NeedsHydration`) | `HomeCardMeta.GenericTitle/NeedsHydration` | `HomeCardEdge.Flags` bit + `GenericTitle` (StringId) — ch. 10 owns the policy, this surface only must not synthesise a title |
| cover-less playlist mosaic (up to 4 tiles) | `HomeCard.MosaicTiles` | `EdgeTable<StringId> Mosaic` on the entity (4 slots), read by `Surfaces.Artwork` |
| section totals for the drill gate and "Show all" | `HomeSection.TotalCount/RawItemCount` | `Edges.HomeSection.Total` + `State` — already in the plan's CSR table; expose them on the section handle |
| top-artist affinity ranking (the podium) + top tracks (the badge) | `IUserTopService` (`userTopContent`, 30-min transport cache) | `EdgeTable<NoEdge> UserTopArtists`, `UserTopTracks` with parent = `User.Me`; the order IS the rank |
| notification feed (the timeline) | `NotificationCenterBridge.Items` (what's-new + gander), unread diff vs persisted last-seen keys | a `Notification` table (id, kind, timestamp, unread, title, image, creator, uri) + `MarkRead` — no table exists in the plan at all; the whole module is otherwise unbuildable |
| concert announcements inside the timeline | `SpotifyUpdates.IsConcert(SocialNotification)` | a `NotificationKind` byte on the same table |
| chart section identity (five hardcoded `spotify:section:` uris) | `ChartSections.All` (`BrowseTaxonomy.cs:160-181`) | keep as constants in `Browse.cs`; the deck is 5 section handles |

---

## 8. Pure rules to port verbatim

| name | 0.2.9 file | what it decides | tests | 0.3 destination |
|---|---|---|---|---|
| `HomeModuleLayout` | `Features/Home/HomeModules.cs:490-804` | every module's column count and its thresholds, the module gap ladder, card heights, display caps, the shelf/Fold fit constants, `GridCardChromeFor`, `FoldRest`/`FoldFan` poses, `ContentExtent`/`FeaturedExtent`, the row/group/section key shapes and their FNV fingerprints | none today — it is engine-bound because of `TrackSize`/`Spacing`; only `HomeHeroLayoutTests`/`HomeArtistRowLayoutTests` cover its neighbours | **CORE** section of `Entities/Home.cs` as `Home.Layout` — strip the two `Ui.Grid` helpers out to the UI file and it is BCL-only, so it becomes test-included (the plan's D17 shape). Port the `ConditionalWeakTable` memoisation with the key functions. |
| `HomeHeroLayout` (+ `HomeHeroMetrics`, `HomeHeroTier`) | `Features/Home/HomeHeroLayout.cs` (70) | the hero's three tiers (700 / 980), its height arithmetic per tier, the 48 × 44 copy padding, the 96-DIP artwork fade, and the reserved pulse block | `src/apps/Wavee.Tests/HomeHeroLayoutTests.cs` (61) | **CORE** of `Entities/Home.cs` |
| `HomeArtistRowLayout` | `Features/Home/HomeArtistRowLayout.cs` (93) | the Wide/Spine tier at 900 with 24-DIP asymmetric hysteresis, the 76/60/46 ramp, `RampScaleFor` (solved against the ramp's AVERAGE box, clamped [1.0, 1.6]), this row's own 32/24 module gap, the 400 ms double-click window | `src/apps/Wavee.Tests/HomeArtistRowLayoutTests.cs` (166) | **CORE** of `Entities/Home.cs` |
| `HomeTimelineMerge` (+ `HomeTimelineRow/Group/Feed/Kind`) | `Features/Home/HomeTimelineMerge.cs` (114) | which notifications are timeline material (kind, never the display category), newest-first with an id tie-break, local-midnight day buckets, the 8-row cap, and the UNCAPPED unread/total counter | `src/apps/Wavee.Tests/HomeTimelineMergeTests.cs` (309) | **CORE** of `Entities/Home.cs` |
| `HomeCardPlayRouting` | `Features/Home/HomeCardPlayRouting.cs` (14) | item-vs-context play | `src/apps/Wavee.Tests/HomeCardPlayRoutingTests.cs` (25) | **CORE** of `Entities/Home.cs` |
| `HomeBrowseCards` | `Features/Home/HomeBrowseCards.cs` (93) | browse section → home section mapping, the `max(total, count)` ledger rule, the fail-loud-unless-offline chart deck, the three-blank-tile seed, uri→kind | `src/apps/Wavee.Tests/HomeBrowseCardsTests.cs` (226) | **CORE** of `Entities/Browse.cs` (it is the browse→home boundary), consumed by `Home.UI.cs` |
| `HomeCards.Duration` / `Hours` / `CompactNumber` / `FirstSentence` | `HomeCards.cs:248-264`, `:478-484`, `:1081-1088` | the three number/duration formats and the "first sentence only" blurb rule (split on `". "` with a > 20 length guard, AFTER stripping HTML) | none | **CORE** of `Entities/Home.cs`; add tests |
| `ChartSections` | `Features/Browse/BrowseTaxonomy.cs:160-181` | the five hardcoded chart section uris and their order (Featured first = the fail-loud slot) | covered indirectly by `HomeBrowseCardsTests` | `Entities/Browse.cs` |
| the CARD identity-colour ladder: `RawAccent` → `Accent` (`Lift` 210) → `SpineAccent` (neutral-S rescue + `Hairline` 3.25 / `HairlineHover` 3.55) → `AccentOrChrome` | `HomeCards.cs:47-88` | non-negotiable §0.3 in code: payload accent → graded `BackgroundTintedBase`/`BackgroundBase` → **null**, with the hash palette reachable only to RE-HUE an already-real near-neutral seed | **none.** The shell-wash twin of exactly this ladder IS pinned (`Wavee.Tests/HomeWashSourceTests.cs`, 223 lines: "payload first, graded cover second, nothing third", plus `HomeWashLocaleTests.cs`, 138) — the CARD ladder has no such test, which is how a well-meaning fallback tone gets added back | **CORE** of `Entities/Home.cs` (it is BCL + `ColorF` once `Tok`/`Surfaces` are passed in as arguments); port `HomeWashSourceTests`' shape onto it |

---

## 9. Re-author notes

### 9.1 What must not be simplified

1. **Thirteen skins stay thirteen skins.** The temptation is one `Card(cover, title, subtitle)` with flags. The
   whole page is the argument against it: a station is a round 32 avatar on a 48-DIP row, a mix is a numeral on a
   shared plate, an audiobook is a rating cluster, an episode is a 16:9 crop with a resume hairline. Each of those is
   the content shape saying what it is before the text does.
2. **The spine's null path.** Do not add a fallback colour "so the card isn't bare". The bare card IS the state
   "not graded yet", and it lasts one decode.
3. **The mix band's shared plate.** Six cards with 12-DIP gaps is a different design. One plate, gap 0, per-cell
   rules, a single contour.
4. **The queue's divider list.** `Gap = 0` + a 1-DIP bottom hairline on all but the last row is what makes it read
   as a table. A gap turns it into six cards.
5. **The audiobook stack's `Gap = 2`.** A deliberate divergence from the shared 12 (`HomeModules.cs:271-275`).
6. **Fixed-width numeric cells.** There are no tabular figures in the text seam. Every numeric column that must
   align gets an explicit width (56 for the queue time, 32 for the book length, 13 for a countdown digit).
7. **The podium's reserved slot.** Without it the labels staircase — the flex engine does not reproduce
   bottom-alignment under wrap.
8. **Reporting the DRAWN count.** Mixview's header says `min(related, 6)` because the ring only draws six: at a
   ~318-DIP pane the circumference (2π × ringR ≈ 690) over the 104-DIP caption box is ≈ 6.6 (`:430-435`).
9. **The hero's ONE action grammar.** Play/Shuffle/♥/… are `WaveeCta`, the same capsules as every other page. The
   old private 32/13 cluster was a second grammar on the app's landing page.
10. **`Skeletonized(false)` on hover-only affordances**, and the Fold tile's EXPLICIT bone.
11. **The rating strip is the stock read-only `RatingControl`**, not a repurposed `ProgressBar` — the row next to it
    uses a real progress hairline for real progress (`HomeCards.cs:715-728`).
12. **RichText, not TextEl, for every description.** Spotify blurbs are HTML fragments; a plain `TextEl` prints the
    markup at the user (`HomeCards.cs:237-242`).

### 9.2 Traps

* **Props freeze at mount.** `FlipCountdown` keyed on `ExpiresAtMs`; `MixviewPanel` keyed on the podium's owner uri
  but taking its hub through props; `PagedShelf` keyed on a deep content fingerprint. A width-derived value that
  changes a mounted component's configuration goes in the `Key` (the radio grid does exactly this:
  `Key = "radio-grid:" + columns`).
* **`Responsive.Of` freezes its builder closure.** This is D17: a nested `ResponsiveBox` inside the artist row froze
  `hub`/`svc`/`go` at first mount and a Mixview click did nothing. `HomeArtistRow` and `MixviewPanel` therefore call
  `UseMeasuredWidth(4f)` directly. **Never nest a `Responsive.Of` inside a component that owns interactive state.**
* **A `ComponentEl` carries NO layout props.** No `AlignSelf`, `JustifySelf`, `Height`. That is why `Spine` and the
  mix wash are a plain `BoxEl` wrapping a leaf component — the first time the spine became a component directly,
  every spine on the page silently vanished (`HomeCards.cs:99-106`).
* **A flex row of `Grow = 1` cells measures every child at the FULL row width.** Wrapping text then reports a
  one-line height, the row's cross size is wrong, and an ancestor clip cuts glyphs — permanently, because nothing
  re-measures. Every multi-column module is a real `GridEl` of star tracks for this reason (`HomeModules.cs:100-121`).
  `AlignSelf = Stretch` on the grid is load-bearing: a star grid measured against hug width collapses to content.
* **`BorderWidth` has no layout effect.** The selected pod insets its artwork by hand (`HomeCards.cs:1016-1023`).
* **`Edges4` is positional (L, T, R, B).** `Edges4(0, 0, artSize, 0)` was a RIGHT margin that collapsed the rank
  plate to ~8 DIP.
* **A ZStack stretches auto-sized children.** The rank plate needs an explicit `Width`; `MinWidth` does not stop it.
  Conversely, a ZStack sizes a child to the root only when the child is EXPLICITLY sized — hence the Fold tile's
  wash and copy column both carry `Height = 176` (`HomeFoldTile.cs:40-53`, `:105-116`).
* **Hover scope.** The engine reveals hover affordances through non-interactive wrappers. A hover handler on a shelf
  root hands every card its container's hover and pops all of them; the pager reveal is scoped to the HEADER ROW
  (`Rail.cs:56-70`). Same rule applies to any new hover-revealed chrome on a module.
* **Zero-allocation scroll frames vs per-card richness.** 0.2.9 reconciles them by (a) building each card tree ONCE
  per realization — `Keyed` deliberately avoids the `build(c) is BoxEl b ? … : build(c)` shape that evaluates the
  builder twice (`HomeModules.cs:87-89`); (b) memoising the deep fingerprints in a `ConditionalWeakTable` because
  `KeyAt` runs per realized row AND again for the list's own key lookup (`:726-736`); (c) keeping the per-card
  subscriptions NARROW — `HomeAccentLeaf` watches one image, `HomeNowPlaying` coarse-gates before touching the hot
  identity signal; (d) `WarmGroup`'s per-module decode targets (64 for radio/queue/quick/mix, 128 for
  books/chips/weekly/feed, 256 for the hero, 512 for editorial — `HomePage.cs:427-437`) so a 32-DIP station cover is
  never fetched at 512. All four survive into 0.3 unchanged in spirit.
* **`KeepAlive` park/un-park snaps authored poses.** The Fold covers' `Offset`/`Rotation` is a REST POSE, not a
  transient transform; hover must not be what puts the stack back on the right after Browse → back
  (`HomeFoldTile.cs:77-79`).
* **The hero's estimator under-reserves its action row by 4 DIP.** `HomeHeroLayout.ActionsBlock = Spacing.XXXL` (32,
  and its comment says "the 32-DIP hero button row"), but the row is built from `WaveeCta` whose `PillHeight` — and
  whose `Icon` default `size` — is **36** (`WaveeCta.cs:61-68`, `:121-122`). The copy column is `Justify = Center`
  inside a fixed-height foreground, so nothing clips today; but the CORE arithmetic and the renderer disagree, which
  is exactly the class of drift non-negotiable §0.8 exists to prevent. Fix the constant when porting, and move the
  hero's test (`HomeHeroLayoutTests`) with it.
* **The hero's `Responsive.Of` fallback is 900, not `HomeModuleLayout.FallbackWidth` 1100** (`HomePage.cs:611`,
  `:684`). 900 falls in the MEDIUM tier, so the pre-measure first frame paints a 344-DIP hero with a 32/40 title and
  then re-renders to Wide. Every other module falls back to 1100. Keep the divergence only if it is deliberate —
  otherwise a wide window's first Home frame is a visible one-frame tier flip.
* **`PagedShelf`'s `prevGlyph` defaults to `""`.** No Wavee call site passes one, so the ‹ chevron is a blank puck on
  every Home shelf (`PagedShelf.cs:130`, `:1386`). Pass `Icons.ChevronLeft` in 0.3.
* **A module's own bottom gap is not always the shared one.** `HomeModules.Module` contributes NO bottom gap at all —
  ch. 10's row shell owns the spacing for feed-group modules — but the two COMPONENT modules pad themselves:
  `HomeTimeline` uses the shared `HomeModuleLayout.Gap(width)` 40/32 (`HomeModules.Timeline.cs:95-100`) while
  `HomeArtistRow` uses its own `HomeArtistRowLayout.ModuleGap(width)` 32/24 (`HomeModules.Artists.cs:178`). Two
  service rows, two different answers, both deliberate (#82) — port both, do not unify them by accident.

### 9.3 Where the plan is wrong or too thin for this surface

1. **§2's line budget was off by 3× — settled 2026-09-12 (A15).** `Home.cs 400 / Home.UI.cs 800 / Home.Page.cs 800`
   = 2 000 lines for all of Home. The 0.2.9 Home surface is ~7 800 lines, of which THIS chapter's files are 3 175.
   `Home.UI.cs` at 800 could not hold thirteen card skins, fourteen module shells, the Fold tile, the timeline and the
   artist row. The split this chapter asked for is granted: **`Home.Cards.UI.cs` and `Home.Artists.UI.cs` are real
   files in the tree**, alongside `Home.UI.cs`, `Home.cs` (CORE), `Home.Page.cs`, `Home.Customizer.cs` and
   `Home.Host.cs` — seven in all, one owner (**P**). This chapter's shares are §9.6's numbers, which are slightly
   tighter than the ask above (1 250 / 1 000 / 650 / 450 rather than 1 400 / 1 400 / 700 / 900) because §9.6 counted
   the savings and §9.3 did not; §9.6 is the one carried into the plan.
2. **§4.3's `HomeSection` edge is `EdgeTable<NoEdge>`.** Fourteen of the facts this surface paints live on the
   section→card relationship, not on the entity (accent, eyebrow, seeds, format, daylist window, header image,
   generic title). Give it a real payload struct (§7 DATA GAPS) or the whole card vocabulary degrades to
   cover + title.
3. **No notification model anywhere in the plan.** The what's-new timeline (176 lines of UI + 114 lines of merge +
   309 lines of tests) has no data source in §4. Either add a `Notification` table or the module cannot be rebuilt.
4. **No affinity/user-top model.** The top-artist podium — the single most expensive module on the page (665 lines)
   — needs `userTopContent` artists AND tracks. §4.14 models the library as edges but has no "top" edges.
5. **§4.12's `Track.Row` is the only row shape in the plan.** The artist disclosure needs the canonical cell with a
   specific column set (`36 | 28 | 32 | ★ | 84 | 52 | 160`), a per-row "In your top N" badge in the actions cell, and
   an artwork column that disappears with `AppearancePrefs.TrackArtworkHidden`. Make `RowStyle` carry a column set and
   a trailing-cell factory, or Home's disclosure will fork the row.
6. **§4.13's page shape assumes one hero + one list.** Home is a heterogeneous measured virtual list whose row
   heights are ESTIMATED by the same arithmetic the renderer uses. `Home.Page.cs` must own an extent table
   (`ContentExtent`, `ShelfExtent`, `FoldExtent`, `FeaturedExtent`) — that is not "a page", it is a layout contract,
   and it belongs in CORE where a test can pin it.
7. **Nothing in the plan mentions the cover-palette plane.** Six of this surface's colour derivations read it. It is
   not an entity column and must not become one; ch. 00 owns it.

### 9.4 Dead code found — do not port

| what | where | why |
|---|---|---|
| `HomeCards.FeedCard` (the `.fcard` skin: 64 art, caps accent reason, 2-line blurb, bottom-pinned meta, spine) | `HomeCards.cs:854-892` (39 lines) | **No call site anywhere in the repo.** The discover feed renders `MediaCard.Shelf` instead (`HomeModules.cs:398-416`), carrying the reason as the card's subtitle. Port it ONLY if the owner wants the feed to become a card module again — and then it needs a module shell, which never existed. |
| `HomeModules.ChartEyebrow` + `FoldDeck(tileEyebrow / eyebrowOf)` | `HomeModules.cs:361-394` | No caller passes either, so **every Fold tile ships with no eyebrow today** — the prototype's "charts / weekly / daily" accent label is authored and unreachable. Decide deliberately: wire it, or delete it. |
| `HomeArtistRow.Mmss` | `HomeModules.Artists.cs:375-380` | Unreferenced. |
| `Rail : Component` — the whole file (title + chevron pager + `UseAnimatedValue` glide + `EdgeFade(36)`) | `Components/Rail.cs` (112 lines) | **Never instantiated.** Nothing in `Wavee` or `Wavee.Tests` constructs it; every shelf on Home, Browse and the facet page is `PagedShelf.Create`. Its header-scoped hover reveal (0.7 → 1, `ScaleStandard`, 167 ms) is the origin of a chevron behaviour this chapter previously attributed to Home — see W9. Do not port it; if the quiet pager is wanted, it is a change to `PagedShelf`'s own `Chevron`, in the engine, under ch. 02. |

### 9.5 Drift between the design docs and the code (CODE WINS)

* `home-sections-v1-mica.html:330-348` gives the Blend/Fold card `height:176px` (matched), `.copy` padding
  `20 12 18 20` (CSS order: **T R B L**) — the code writes `Edges4(20, 20, 12, 18)`, which in the engine's order
  (**L, T, R, B**) is left 20 / top 20 / right 12 / bottom 18: the same four numbers, RE-ORDERED, not copied through.
  Reading the C# tuple as if it were CSS mirrors the copy column;
  `.t` at `font-size:32px; font-weight:300` — the code uses `WaveeType.FoldTitle` = Title 28/36 display **400**,
  because 300 is off the house weight policy and 32 has no rung on the ramp (`WaveeType.cs:205-212`).
* `.blend:hover .wash{opacity:.34}` (from 0.22) is **not implemented** — the Fold wash is static.
* `.fold:hover .stack .c:nth-child(3)` adds `scale(1.03)`; the code deliberately drops it (`HomeModules.cs:572-576`).
* The prototype's Mixview connectors are dashed; `PolylineStrokeEl` carries no dash pattern, so they are solid —
  flagged rather than faked (`HomeModules.Artists.cs:494-498`).
* The prototype's `.hero-eyebrow{text-transform:uppercase}` is deliberately NOT honoured (§6.6).
* The prototype's per-row 13/17 + 11/14 type pair was the app's rogue ramp; `TwoLine`/`Sub` now resolve to
  BodyStrong 14/20 over Caption 12/16, "and those two edits fix most of Home in one place" (`HomeCards.cs:216-235`).
* `2026-08-18-home-hub-strip-charts.md` describes the Fold tile as living on a `.fold` crop at height 168; the
  shipped tile is the `.blend` variant at 176 with a card plate. The doc's wire facts (the five section uris) ARE
  current.
* `DEFECT_REGISTER.md` D3 records the hero at "345/305/297 DIP"; the code's current heights are **384/344/336**.

### 9.6 Line budget

| | lines |
|---|---|
| 0.2.9, this chapter's files | 3 175 (`HomeCards` 1 201 + `HomeModules` 804 + `.Artists` 665 + `.Timeline` 176 + `HomeFoldTile` 143 + `HomeArtistRowLayout` 93 + `HomeBrowseCards` 93) |
| plan §2 target for ALL of Home | 2 000 (`Home.cs` 400 + `Home.UI.cs` 800 + `Home.Page.cs` 800) |
| honest estimate for THIS chapter's surface in 0.3 | **~2 900**: `Home.Cards.UI.cs` ~1 250 (skins; −150 from dropping `FeedCard` and from handles replacing `HomeCard`/`Meta` plumbing), `Home.UI.cs` ~1 000 (module shells + Fold tile + timeline UI), `Home.Artists.UI.cs` ~650 (podium + Mixview), plus ~450 CORE in `Home.cs` (`Home.Layout` + hero/artist-row/timeline/play rules). Comments are ~35% of the 0.2.9 files and every one of them records a defect that was paid for — keep them. |

**The settled Home file set (A15).** Chapters 10, 11 and 12 gave three different file sets for one owner; the
decision is these seven, each chapter keeping its own estimate for the parts it owns. This chapter's four columns are
the ≈ 2 900 UI + ≈ 450 CORE above.

| file | ch. 10 | **ch. 11** | ch. 12 | lines |
|---|--:|--:|--:|--:|
| `Entities/Home.cs` (CORE) | 650 | **450** | 700 | 1 800 |
| `Entities/Home.UI.cs` | 600 | **1 000** | — | 1 600 |
| `Entities/Home.Cards.UI.cs` | — | **1 250** | — | 1 250 |
| `Entities/Home.Artists.UI.cs` | — | **650** | — | 650 |
| `Entities/Home.Page.cs` | 1 750 | — | 480 | 2 230 |
| `Entities/Home.Customizer.cs` | — | — | 300 | 300 |
| `Entities/Home.Host.cs` (SHELL) | — | — | 400 | 400 |
| **total** | 3 000 | **3 350** | 1 880 | **8 230** |

Still missing from the §2 tree for this surface, and NOT settled by A15: a `Notification` model for the timeline
(A9 puts the notification stack in `Platform/Notify.cs` + `Notify.Host.cs`, owner I, Wave 4 — this module reads it,
it does not own it), and the affinity / `userTopContent` edges the podium needs (§9.3 #4).

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`.
Unless stated, the route is **Home** (the app's landing route) at a **1 280 × 900** window (content `A ≈ 1088`) in
**dark** theme, 100% zoom. "Static capture" = a screenshot; "hover capture" = a screenshot with the pointer parked;
"frame recording" = a ≥ 2 s capture at ≥ 60 fps. Run every item once in light theme as well where colour is named.

**Hero band**

1. At 1 280 wide the hero surface is 384 DIP tall and its cover is a 384 square whose LEFT edge fades over 96 DIP
   (static capture; measure the cover edge against the surface edge).
2. Narrowing the window to ≈ 1 160 (A ≈ 820) swaps the title to 32/40 and the surface to 344 with no other change
   (two static captures).
3. Narrowing to ≈ 940 (A ≈ 640) stacks the hero: cover centred, no left fade, copy pinned to the bottom, vertical
   veil (static capture).
4. The eyebrow reads "Good morning, {name} · your daylist" in sentence case with the user's own capitalisation.
5. The daylist countdown ticks once per second, each digit sliding up and out while the new one rises, inside a
   clipped cell that never reflows the row (frame recording, 3 s).
6. The countdown's digits are the daylist hue, legible on the wash (`TextInk`, 4.5:1) — never the raw accent.
7. A non-daylist hero (spotlight) keeps the SAME surface height: the pulse row is reserved and empty (compare the
   two states' surface heights across a feed refresh, or force with `--fake`).
8. The action row is Play (accent, on the card's own colour) · Shuffle (standard) · ♥ · … — all 36-DIP capsules,
   gap 8 (static capture, measure).
9. Clicking the cover navigates; clicking the copy column does nothing.

**Weekly pair / jump back in**

10. At A > 760 the weekly pair is two cards of equal width, gap 12, each 88 tall, art 56 at r-4 (static capture).
11. The weekly blurb is ONE sentence, plain text, max two lines — no HTML markup ever visible.
12. Jump back in shows 8 tiles at most; the module subtitle reads "Your 8 most-opened of N".
13. A quick tile is exactly 56 tall with its cover flush to the leading edge — no sliver of card under the art.
14. Hovering a quick tile fades in a 28-DIP solid accent play puck at the trailing edge and scales it 0.86 → 1
    (hover capture + 400 ms frame recording).
15. At A > 1120 the quick grid is 4 columns, at A > 780 it is 3, below that 2 (three static captures at ~1 350,
    ~1 280 and ~1 000 window widths).

**Daily-mix band**

16. Six cells sit inside ONE rounded plate with one contour; the separators are 1-DIP internal rules, not gaps.
17. Each numeral is 28/36 in the display face and takes the mix's OWN colour; before grading it is primary ink,
    never absent (capture during a cold load).
18. Each cell carries a full-bleed wash of its own colour at 9% and a 2-DIP spine of the same hue.
19. Hovering a cell changes only its fill (no lift, no shadow) — the plate does not tear (hover capture).
20. At A ≈ 1000 the band wraps to 3 + 3 with a 1-DIP rule across the second row (static capture at a ~1 200 window).

**Top-artist podium**

21. Ten avatars descend 76 → 60 → 46 (times the fill scale) and every LABEL sits on one baseline (static capture).
22. Widening the window grows the avatars until the ramp scale clamps at 1.6 (two static captures; at 1 280 the
    rank-1 avatar measures ≈ 122).
23. The rank plate rides each avatar's own top-left corner, 20 × 20 (28 wide from rank 10).
24. Clicking a pod opens the disclosure with a slide-in and a height tween on the card; clicking the same pod closes
    it (frame recording).
25. Above 900 the disclosure is top-tracks | 1-DIP divider | a 342-DIP Mixview pane; below it stacks (two captures
    at ~1 280 and ~1 000).
26. The Mixview ring draws the hub plus at most six nodes, every node captioned, with 1-DIP connectors at 26%
    tertiary — and the header count equals the number of nodes DRAWN.
27. Clicking a ring node re-centres the graph AND the left pane (title, facts, Play) — they never disagree.
28. Double-clicking a pod or a node within 400 ms opens the artist page.
29. Right-clicking a pod or node shows exactly one item: "Go to artist".
30. Selecting an artist with no cached overview shows five row-shaped bars, never a spinner, never an empty pane.

**Chip cards / radio / queue / audiobooks**

31. A chip card shows at most three filled chips; a mix with no seeds shows its description instead (static capture
    of both cases).
32. The radio dial is two columns with a 24-DIP column gap and NO row gap — twenty rows read as one folded list.
33. A station's artwork is a 32-DIP circle; its hover play is a 24-DIP GHOST puck (hover capture).
34. An episode with video has 56 × 32 (16:9) artwork; an audio episode has 32 × 32 (static capture of a mixed queue).
35. A partly-played episode carries a 2-DIP accent hairline across the bottom of its art, width proportional to
    resume/duration.
36. The queue's right cell is 56 DIP wide with the duration over "resume"/"unplayed"; the digits never shift the
    column between rows.
37. Queue rows are separated by 1-DIP hairlines with no gap; the LAST row has none.
38. An audiobook row shows a five-star read-only strip, the value to two decimals, and the length in a 32-DIP cell.
39. Audiobook rows stack with a 2-DIP gap (not 12).
40. Above A = 1020 episodes and audiobooks sit side by side at equal width with a 24-DIP gap; below they stack
    (two captures at ~1 280 and ~1 100 windows).
41. With only ONE of the two present, it still occupies its original half-column above 1020.

**Editors' picks**

42. Above A = 980 the feature and the three companions are `1.08 : 1` with a 16-DIP gap, and the feature's footer row
    sits level with the last companion's bottom (static capture; measure both column heights — expect 188 vs 232).
43. The "Editorial" tag's border is 40% toward the card's accent and its ink is the accent TEXT role.
44. The feature's blurb renders hyperlinks as accent text, never raw `<a href=…>` markup; clicking one navigates.
45. A companion row hovers with a 30-DIP ghost play and lifts −4 like every other card.

**Timeline**

46. Day groups have a fixed 96-DIP right-aligned column: "Today"/"Yesterday" alone, older days as "Fri 5 Sep" with
    "N days ago" beneath in tertiary.
47. The pips straddle the 1-DIP rule (7 DIP, 1.5-DIP ring): filled accent when unread, hollow with a strong control
    stroke once seen.
48. An unread row ends in a solid accent "New" pill; a seen row ends in tertiary "Seen".
49. A concert row's artwork is a 40-DIP CIRCLE; a release row's is square at r-4.
50. The header reads "N unheard of M" with an info badge showing N — counts describe the whole feed, not the eight
    visible rows.
51. Clicking a concert row marks it read (the pip hollows and the bell badge drops) and opens the notification's own
    destination.

**Fold deck (Charts / Sections for you)**

52. At A ≈ 1088 the deck shows 2 tiles of 538 × 176; below ~892 of content it shows one tile filling the row
    (two static captures).
53. Each tile hangs up to three 124-DIP covers off its right edge at −11° / +5° / −2°, the third clipped by the
    card's edge.
54. Hovering a tile swaps the card fill and fans the covers to −16° / +8° / +1° over ~250 ms with NO lift and NO
    change in shadow (frame recording).
55. The tile title is 28/36 display-face at weight 400, up to three lines, confined to 70% of the card width.
56. A section with fewer than three cards shows only that many covers — never a repeated cover.
57. While Charts is loading, the row shows Fold-shaped bones at the fitted width (not generic bars) and the row
    height does not change when the real deck arrives (frame recording across a cold start).
58. Offline (or logged out), the Charts row shows "No charts right now" — it never collapses to zero height.
59. Navigating Home → Browse → back leaves the fan at REST, not fanned.

**Shelves and shared chrome**

60. Recents, Podcasts and the discover feed are horizontally paged shelves whose cards fit between 148 and 188 with
    a 12-DIP gap and a 24-DIP edge fade (static capture; at A ≈ 1088 expect 6 cards of ≈ 171).
61. Artist cards in the Recents rail are CIRCULAR; playlists/albums are square.
62. Each Recents card's second line names the entity TYPE ("Artist", "Album", "Playlist"), not its owner.
63. A discover-feed card's second line is the section's reason ("For fans of …") when it has no description.
64. The pager chevrons are TWO 32-DIP CIRCLES on the header's trailing edge, painted at full strength at all times
    (`Tok.FillControlDefault`), dimming to 0.35 when their end is reached — they are not hover-revealed. Hovering one
    card never pops the others (hover capture over a card, then over the header). **Known defect to fix in 0.3, not
    to reproduce:** the ‹ chevron ships with no glyph at all (`prevGlyph` defaults to `""`).
65. Every drillable module header shows a 12-DIP chevron; a non-drillable one shows the identical label with none.
66. A module header with a subtitle renders both on ONE baseline ("Radio · 20 stations"), not as two stacked runs.
67. Module gaps are 40 DIP at A ≥ 1080 and 32 below (measure between two modules at ~1 280 and ~1 150 windows).

**Cross-cutting**

68. Every card and row on the page answers right-click with the entity's menu (spot-check one mix cell, one station
    row, one quick tile, one book row).
69. Every card except a track/episode card can be picked up and dropped on a sidebar playlist.
70. The now-playing equalizer appears on whichever card is the sounding context, in the sizes listed in W30, and on
    pause SETTLES TO THREE FLAT BARS at 0.4 — it stays visible, it does not hold its last non-uniform sample and it
    does not vanish (frame recording, play then pause; compare the last playing frame with the settled one).
71. Skipping to an unrelated track re-renders no card (watch for flicker on a page full of cards during a skip;
    the mark simply appears on the new card).
72. Switching to light theme re-resolves every spine, wash and numeral in place — no reload, no stale dark tint.
73. With Windows "Show animations" OFF, no card lifts, no cover fans, no digit slides — but fills still cross-fade
    and content still reveals (compare captures with the setting toggled).
74. Scrolling the whole feed top to bottom produces no allocation ticks and no row that changes height after it
    appears (engine `FG_FPS_LOG` alloc counter and a slow scroll recording).

**States the previous pass under-specified — check these too**

75. A hero whose title overflows two lines SHRINKS before it ellipsizes: 48 → 40 in the Wide tier, 32 → 28 in
    Medium (`MinSize`). Force it with a long daylist name and measure the cap height (it must stay 384 / 344).
76. A hero with no tags and no meta line keeps the same surface height and its copy stays vertically CENTRED — the
    estimator reserves both blocks unconditionally while the renderer collapses each to an empty box.
77. The daylist countdown shows two hour digits and clamps at `99:59:59` — a multi-day window never reflows the row
    into a third digit cell.
78. The mix cell's eyebrow reads "DAILY MIX" (the resource string is authored in caps) while every other eyebrow on
    the page is sentence case.
79. The disclosure's Play is a STANDARD 32-DIP button, not the hero's 36-DIP accent pill — two grammars, one page,
    deliberately (the disclosure is chrome inside a card, not a page primary).
80. A track in the user's top N carries a green "In your top 5" pill on the SUCCESS background, not on the accent —
    check in light theme too, where the semantic green must still read against the card.
81. A timeline RELEASE row navigates but stays UNREAD (its pip stays filled and the bell badge does not drop); only a
    CONCERT row marks itself read. An episode release opens the browser, not an in-app page.
82. The Charts deck renders however many of the five sections came back with cards — a market missing Podcast Charts
    shows four tiles, never a blank one.
83. The Fold tile's copy sits 20 in from the LEFT and 12 in from the RIGHT (not the reverse); the title's right edge
    is 70 % of the card width and the third cover is cut by the card's own right edge.
84. An ARTIST card's context menu has no "Add to playlist" and no queue verbs — Play · Follow · Open · Pin · Share ·
    Go to artist radio and nothing else.
85. A Recents/Podcasts/facet shelf whose group is refreshed under it re-renders its cards (today only the discover
    feed and the Fold deck carry a content-fingerprint `Key`; the other three do not — fix, then verify).

---

## 11. Audit log

Adversarial re-read of every assigned 0.2.9 source against this chapter, 2026-09-12. Every line below cites the
evidence that settled it. The chapter's ~120 `file:line` citations into `HomeCards.cs`, `HomeModules.cs`,
`HomeModules.Artists.cs`, `HomeModules.Timeline.cs`, `HomeFoldTile.cs`, `HomeHeroLayout.cs`,
`HomeArtistRowLayout.cs` and `HomeBrowseCards.cs` were spot-checked line by line and are ACCURATE; so are every
column threshold, card height, extent formula, ramp number, ring geometry, veil/wash stop and hero height
arithmetic in §2-§5. The corrections are:

| # | kind | what |
|---|---|---|
| 1 | **wrong** | **Fold tile copy padding was mirrored.** §1.1 (L), §3.2 and W24 read `Edges4(20,20,12,18)` as CSS `T R B L` → "T20 R20 B12 L18". `Edges4` is positional **(L, T, R, B)** — the chapter says so itself in §9.2 — so the truth is **left 20 / top 20 / right 12 / bottom 18** (`HomeFoldTile.cs:112`). Fixed in all three wireframe/token sites; §9.5's "the same four numbers in the engine's own edge order" now spells out that they are RE-ORDERED, not copied. |
| 2 | **wrong** | **W9's shelf chevrons described a dead class.** "32 square, r-ctrl, rest opacity 0.7 → 1, revealed on the HEADER's hover" is `Components/Rail.cs:95-117`. Home's shelves are `PagedShelf`, whose `Chevron` (`fluent-gpu/.../PagedShelf.cs:1402-1411`) is a 32-DIP **circle** (`CornerRadius4.All(16)`) on `Tok.FillControlDefault`, **always painted**, opacity 1 / 0.35 disabled, glyph 13, header item gap 8, no reveal and no scale. W9, the §5 motion row and parity #64 rewritten. |
| 3 | **wrong** | **Paused equalizer.** W30 and parity #70 claimed "FROZEN, bars held at their last sample, non-uniform". `HomeNowPlaying` calls `WaveeEqualizer.Of(playing, …)` with **no `paused` signal** (`HomeCards.cs:1130`), so a pause makes `animate` false and `Equalizer.cs:100` runs `WriteAll(0.4f)` — three **flat** bars. Corrected in W30, §5 and parity #70; a separate §5 row now states the reduced-motion-while-playing shape (`Sample(pattern, 0.3)`, `:166-174`), which is the only non-uniform settled form. |
| 4 | **wrong** | **W10 drew the mix eyebrow as "Daily Mix".** `home.dailyMix` is authored `"DAILY MIX"` in `en-US.json`. No code upper-cases it, so §6.6's no-`ToUpper` rule is intact — but the rendered glyphs are caps. W10 redrawn; §6.6 gained the caveat and the sentence-case neighbours it sits beside. |
| 5 | **overclaim** | **"Home keys every shelf on a deep content fingerprint" (§1.2.3).** Only `Feed` (`HomeModules.cs:415`) and `FoldDeck` (`:374`) carry a shelf-level `Key`. `Recents`, `Podcasts` and `Shelf` carry only per-card `keyOf` (`:178`, `:335`, `:357`) and no `Key` at all, so a refreshed group leaves their mount-time item list frozen. Restated as a hole to close in 0.3, with parity item #85. |
| 6 | **missing** | **`Components/Rail.cs` is dead code** — an assigned source for this chapter, never instantiated anywhere in `Wavee` or `Wavee.Tests` (every shelf is `PagedShelf.Create`). Added to §9.4 with the note that its quiet pager, if wanted, is an engine change under ch. 02. |
| 7 | **missing** | **`PagedShelf`'s `prevGlyph` defaults to `""`** (`PagedShelf.cs:130`) and no Wavee caller passes one — the ‹ chevron on every Home shelf is a blank puck. Recorded in W9, §9.2 and parity #64 as a defect to fix, not to reproduce. |
| 8 | **missing** | **The hero's estimator under-reserves its action row by 4 DIP.** `HomeHeroLayout.ActionsBlock = Spacing.XXXL` (32) against a row built from `WaveeCta.PillHeight` = 36, and `WaveeCta.Icon`'s default `size = PillHeight` = 36 too (`WaveeCta.cs:61-68`, `:121-122`) — so the ♥ and "…" are 36-DIP round arms, not 32. §3.2 corrected, §9.2 trap added: this is precisely the renderer-vs-estimator drift non-negotiable §0.8 forbids. |
| 9 | **missing** | **Hero title autofit.** `ArtistTitle` carries `MinSize 40` and `ArtistCompactTitle` `MinSize 28` (`WaveeType.cs:167-187`): a long daylist name SHRINKS before it ellipsizes. Added to §3.2 and parity #75. |
| 10 | **missing** | **The hero's `Responsive.Of` fallback is 900, not 1100** (`HomePage.cs:611`, `:684`) — 900 is the MEDIUM tier, so the pre-measure first frame paints a 344-DIP hero on a wide window. §9.2 trap. |
| 11 | **missing** | **The disclosure's chrome tokens.** §3.2 had no row for the "In your top N" badge (`Tok.SystemFillSuccessBackground` plate, `Tok.SystemFillSuccess` ink, `Radii.Full`, pad 8 × 4, Eyebrow — a semantic colour outside the accent budget, `HomeModules.Artists.cs:342-361`), for the left pane's own padding/gap (`:334-339`), for the head's Play being a stock 32-DIP `Button.Create(Standard)` rather than a `WaveeCta` pill (`:300`), or for the 120-DIP skeletonized block a zero-related hub draws (`:428`). All four added. |
| 12 | **missing** | **Two service modules pad themselves, differently.** `HomeTimeline` adds `HomeModuleLayout.Gap(width)` 40/32 (`HomeModules.Timeline.cs:95-100`); `HomeArtistRow` adds its own 32/24 (`HomeModules.Artists.cs:178`). The chapter named only the artist row's. §9.2. |
| 13 | **missing** | **Timeline row behaviour splits by leg.** Only the CONCERT row calls `nc.MarkRead` before navigating; a RELEASE row navigates and stays unread (`HomeModules.Timeline.cs:108-124`), and an EPISODE release has no app route so it opens the **web player** (`SpotifyLink.WebUrl` → `LoginView.OpenUrl`, `:136-152`). §6.1 rows added, parity #81. |
| 14 | **missing** | **The Charts deck is 1..5 tiles.** `LoadChartDeckAsync` omits every later section that is null or card-less (`HomeBrowseCards.cs:53-60`) and re-stamps each survivor with the taxonomy uri it asked for (`:59`). Only Featured is fail-loud, and even that degrades to an empty deck when `hasLiveCatalog` is false. Added to W26 and §7; parity #82. |
| 15 | **missing** | **`FoldRest`'s left is clamped at 0** (`max(0, cardW − 210)`, `HomeModules.cs:560`), so a 0-width first frame cannot park the covers at a negative X and paint them into the band header. Added under W24. |
| 16 | **missing** | **An ARTIST card's menu is short in two places, not one.** `ContainerTracks.CanResolve` is false for an artist, so it loses the queue pair **and** `Add to playlist` (`Menus.cs:570-603`): Play · Follow, then Follow · Open · Pin · Share · Go to artist radio. Also recorded: `LikedSongsArtwork.IsLikedUri` is what keeps a `spotify:user:<u>:collection` card from getting no menu at all (`:556-563`). §6.5, parity #84. |
| 17 | **missing** | **Five loc keys.** `home.chartEye` / `weeklyEye` / `dailyEye` / `videoEye` / `podcastsEye` — the Fold-tile eyebrow words `HomeModules.ChartEyebrow` resolves, authored and unreachable (§9.4). Added to §6.6. |
| 18 | **missing** | **FlipCountdown clamps hours at 99** (`FlipCountdown.cs:97`) and stops its 1 s interval once expired; the trailing `home.nextUpdateAt` run is a tertiary Caption at gap 8. §3.2 rows; parity #77. |
| 19 | **missing** | **§8 had no row for the card identity-colour ladder** — the chapter's own non-negotiable §0.3, and the one decision on this surface with NO test, while its shell-wash twin is pinned by `HomeWashSourceTests.cs` (223) + `HomeWashLocaleTests.cs` (138). Added with that test as the shape to port. |
| 20 | **missing** | **No statement of which states this surface deliberately has no form for** — explicit, unavailable, selection, drop-target, sticky/compact, zoom, per-card error, 0 items, 10k items. Added as W34 so a re-author cannot mistake "absent" for "forgotten". |
| 21 | **unverified → confirmed** | The spine's hover colour DOES reach a `HitTestVisible = false` leaf: the recorder opts a descendant into the container's inherited hover cross-fade purely because it declares an explicit `HoverFill` (`SceneRecorder.cs:3079-3086`; the cascade in `AnimScheduler.Hover.cs:73-112` would NOT drive it). §0.2 now records this, because dropping the explicit `HoverFill` would silently kill the hover with no auto-lighten fallback. |
| 22 | **unverified → confirmed** | Every card and row IS keyboard-reachable without `Focusable`: the engine's tab-stop rule is `TabStop ?? (Focusable \|\| OnClick != null)` (`InputDispatcher.cs:3823`). §6.1's "focus ring" claims stand. |
| 23 | **unverified → confirmed** | `HomeModuleLayout` still has no test (`Wavee.Tests/HomeLayoutTests.cs` is the customizer's layout DOCUMENT, not this class); `HomeHeroLayoutTests` 61, `HomeArtistRowLayoutTests` 166, `HomeTimelineMergeTests` 309, `HomeCardPlayRoutingTests` 25, `HomeBrowseCardsTests` 226 all exist at the line counts §8 claims. `ChartEyebrow` / `FoldDeck(tileEyebrow, eyebrowOf)` confirmed unreachable: the three live `FoldDeck` call sites (`HomePage.cs:741`, `:749`, `BrowseDirectory.cs:272`, `:358`) pass neither. |
| 24 | **arbitration** | arbitration 2026-09-12: **A15 settles the Home file set at seven files, all `Entities/`, all owner P** — `Home.cs` (CORE) · `Home.UI.cs` · **`Home.Cards.UI.cs`** · **`Home.Artists.UI.cs`** · `Home.Page.cs` · `Home.Customizer.cs` · `Home.Host.cs`. The two named partials this chapter asked for in §9.3 #1 are granted, so the header target, §1.2's lead paragraph and §9.3 #1 are rewritten from an ask into a decision, and §9.6's "files missing" paragraph becomes the seven-file table. This chapter's own estimates are the ones carried (§9.6's 1 250 / 1 000 / 650 / 450, not §9.3's looser 1 400 / 1 400 / 700 / 900); family total ≈8 230. What A15 does **not** settle and this chapter still needs: the timeline's notification model (A9 owns the stack in `Platform/Notify.cs`, owner I) and the `userTopContent` affinity edges. |
