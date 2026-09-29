# 06 — Content facets: All / Music / Podcasts / Audiobooks

Design lead doc for the facet control and the per-facet pages. Sources: `home_new.json` (All, morning),
`home_filtered.json` (Music, afternoon), `podcasts.json` (Podcasts, afternoon, Fri 25 Sep 2026), the app's
`Home.Page.cs` / `Home.Rules.cs` / `Home.UI.cs` / `Spotify.Api.cs`, the canvas (`Main.dc.html` vocabulary),
`05-native-research.md`, and the owner's dashboard refetch pattern (`ops/crash/dashboard/src/scene/PageHeader.tsx`,
`PageBody.tsx`). Every item, title, duration, date and image below is real; nothing is invented.

Facts that shape everything:

- A facet is a **different page from the server**, not a filter. `HomeQuery(facet)` sends `facet:"music-chip"` /
  `"podcasts-chip"` / `"audiobooks-chip"` / `""` (All) and, for the sub-chip, `"music-following-chip"` /
  `"podcasts-following-chip"`. The response is a whole new `home` document with its own greeting and section list,
  stored today under `wavee:home` / `wavee:home:<facet>` (one row per facet in the Homes table, with `Inflight`).
- `homeChips` is identical in all three captures: Music (sub: Following), Podcasts (sub: Following), Audiobooks (no
  sub). "All" is not a server chip; it is the app's own "no facet" position (`FacetSlotKind.All` exists already).
- The All page for this user carries **zero podcast sections** although he follows 8 shows. "All" is Spotify's
  music-heavy landing; podcasts live only behind the Podcasts facet. Don't fabricate a merged page.
- Podcast episodes arrive rich: `duration.totalMilliseconds`, `releaseDate.isoString`, `playedState.state`
  (NOT_STARTED / IN_PROGRESS) + `playPositionMilliseconds`, `mediaTypes` (VIDEO+AUDIO on 26 of 36, AUDIO only on 10),
  `contentRating.label` (EXPLICIT on 2), 64/300/640 square cover art, a 1280×720 `videoPreviewThumbnail` on every
  video episode, and the parent show (`podcastV2`: name, publisher, cover, mediaType). Shows carry name, publisher,
  cover, `mediaType` (MIXED/AUDIO) and an explicit label. There is **no** unplayed count, no "latest episode" on a
  show, and only one IN_PROGRESS episode (8 s into a 44 min episode).

---

## 1. Options considered

Evaluation axes: native feel (Windows 11 inbox apps; Media Player, Store, Xbox, Photos, Snipping Tool; Zune soul),
discoverability, keyboard/a11y, and the fact that a switch is a **full page reload** (so the control must read as
"go to a different page", never as "filter this list").

| # | Option | Native feel | Discoverability | Keyboard / a11y | Honest about the reload? | Verdict |
|---|---|---|---|---|---|---|
| A | **Zune typographic pivot** at Fluent sizes: the words are the page title, selected bright, others dim, no underline, no boxes | High. Windows Phone/Zune Pivot lineage; sentence case and the Fluent ramp keep it Windows 11 (the owner's lowercase + Light-weight version fails `05` §1.3: no Light weights, no forced lowercase — a web-ism) | High: it is the largest type on the page after the hero title | tablist + arrows + Enter; selection is colour **and** weight, so not colour-only | Yes: the page's title changes, so the page changed | **Chosen** |
| B | **WinUI Segmented / SelectorBar** at rest (compact boxed group or underline strip) | High for the control itself, but the underline strip under the title bar's underlined Home tab is what the owner rejected: two tab grammars stacked; a boxed Segmented at rest reads as a *view-mode* switch (Snipping Tool modes), not a page identity | Medium | Good (native control) | Weak: a segmented control says "same data, different view" | Used **only as the collapsed, scrolled form** (§2.4) |
| C | **Title-bar integration**: the Home tab becomes a split/drop-down ("Home ▾ → All / Music / Podcasts / Audiobooks") | Low. Title-bar tabs are documents; Windows apps never put content facets in the caption area; the shell is fixed | Low: hidden behind a chevron, two clicks per switch | Menu is keyboardable but slow | Yes, but at the wrong level | Rejected |
| D | **The rail** (facets as rail entries) | Store/Xbox put content types in the left nav, but Wavee's rail is global (Liked, Library, Artists, Radio, Podcasts, Home) and the facets exist only on Home; the rail's "Podcasts" is the library, not Home-filtered | Medium | Good | Yes | Rejected (shell is fixed; overloads the rail) |
| E | **Page-header BreadcrumbBar / DropDownButton** ("Home › Podcasts ▾") | Medium (BreadcrumbBar is native, but Settings-flavoured) | Low-medium: siblings hidden until opened | Fine | Reads as location, OK | Rejected: 2 clicks, no glance-able siblings, no room for Following |
| F | **Per-facet pages** (separate routes, separate rail/tab entries) | High (Store's Apps/Gaming/Movies) | High | Good | Perfect | Rejected for now: shell is fixed, Audiobooks is empty for this user (a dead rail entry), and Spotify models them as Home sub-pages sharing greeting + chips. Note as a future direction if Home ever splits |

**Decision: A at rest, B when scrolled.** The rest state is a Zune-souled *page-title pivot*: four words in
Title 28/36 (Segoe UI Variable Display), the selected one Semibold + TextPrimary, the others Regular + TextSecondary,
no underline, no fill, no divider, generous 40 px visual gaps. It is the page's heading, so the title bar's "Home"
tab and the page's "Podcasts" title don't compete: one is the document, the other is what the document is showing.
Once the row scrolls out, a compact **Segmented** control in a 44 px sticky band takes over, which is what the
owner asked for and what Snipping Tool / Photos do for a persistent mode switch. The Following sub-chip is a
Windows **toggle button** at the row's trailing edge, only when the selected chip has `subChips`.

What I changed from the starting recommendation and why:
- **No lowercase, no Light weight.** `05` is explicit that both are web-isms on Windows 11; the Fluent ramp has
  Regular and Semibold only. Zune's soul survives as *type as navigation, bright vs dim, no chrome*.
- **No middle dots between words.** Dots are a web breadcrumb idiom; the gap does the separating.
- **Following is right-aligned, not inline after the selected word.** Inline insertion (which the current app does:
  `HomeFacetStrip.Slots` spills sub-chips after the parent) would push the remaining words sideways on every
  switch. A trailing control is the Windows header grammar (title left, commands right; Photos, Media Player).
- **No skeleton on switch** (owner's rule): keep the old page, dim it, pin a ProgressBar under the control.

---

## 2. The chosen design in full

### 2.1 Anatomy — rest state (page-title pivot)

Geometry inherits the canvas: content surface starts at y = 48 (title bar), content column padding 24 / 32,
inner content width **1316** (`1440 − 56 rail − 2×32 − 4`).

```
y=48   ┌── content surface top (8 px top-left corner) ──────────────────────────────────────┐
       │  24 px                                                                           │
y=72   │  ┌──────┐ ┌──────┐ ┌──────────┐ ┌────────────┐              ┌─────────────────┐  │
       │  │ All  │ │Music │ │ Podcasts │ │ Audiobooks │              │ ◯ Following      │  │  row: 40 px
y=112  │  └──────┘ └──────┘ └──────────┘ └────────────┘              └─────────────────┘  │
       │  20 px                                                                           │
y=132  │  first zone begins                                                               │
```

- **Row**: height 40, x from 56+32 = 88 to 88+1316. Sits 24 below the surface top, 20 above the first zone.
  (In the canvas: the row is the first child of `.contentcol`; give it `margin-bottom:-20px` so the 40 px column
  gap nets to 20.)
- **Word box**: `height:40px; padding:0 8px; margin-left:-8px (first only); border-radius:4px;` text vertically
  centred. Boxes are laid out in a row with **24 px gap** (visual glyph-to-glyph gap ≈ 40 px). The first word's
  glyphs align with the content edge (x = 88).
- **Type**: `font-family: var(--font-display)`; **28 px / 36 px line**; selected `font-weight:600`, unselected
  `font-weight:400`. Sentence case exactly as the server labels them (`Music`, `Podcasts`, `Audiobooks`) and the
  app's own `All`. Letter-spacing 0. Never wraps; at widths where four words + toggle don't fit (< ~720 px content)
  the row switches to the compact band form permanently (below the app's `HomeHeroLayout.MediumWidth = 700`).
- **Order**: All, then server chip order (Music, Podcasts, Audiobooks). All is always present.
- **Following toggle** (only when the selected chip has `subChips`, i.e. Music and Podcasts): right-aligned in the
  same row, vertically centred on the row. Spec in §2.3.
- **Nothing else in the row.** No divider under it, no greeting, no "See all". The greeting stays in the hero
  eyebrow on All/Music; on Podcasts/Audiobooks the greeting is not shown (the page opens with content).

Type ramp on the page, for hierarchy: hero title 40/52 (All/Music only) › **facet pivot 28/36** › chapter
headers 20/28 › item titles 14/20 › captions 12/16.

### 2.2 States — pivot word

| State | Text | Backplate | Notes |
|---|---|---|---|
| Rest, unselected | 28/400, TextSecondary (dark `rgba(255,255,255,.786)`, light `rgba(0,0,0,.6063)`) | none | |
| Hover, unselected | TextPrimary | `SubtleFillColorSecondary` (dark `rgba(255,255,255,.0605)`, light `rgba(0,0,0,.0373)`), radius 4, the 40-px box | colour 83 ms; arrow cursor (never the hand) |
| Pressed | TextSecondary | `SubtleFillColorTertiary` (dark `rgba(255,255,255,.0419)`, light `rgba(0,0,0,.0241)`) | no scale, no lift |
| Selected | 28/600, TextPrimary | none at rest; hover/press backplates as above | weight change reflows width: 260 ms `SmoothOut` (the app's existing `s_reflow` on tabs) — neighbours slide, they don't jump |
| Focus (keyboard) | as state | two-tone focus rect around the 40-px box: 2 px outer `#fff` (dark) / `#000` (light), 1 px inner opposite, offset 1 px | only `:focus-visible` |
| Disabled | TextDisabled (dark `rgba(255,255,255,.3628)`, light `rgba(0,0,0,.3614)`) | none, no hover | offline **and** that facet has no cached document. All is never disabled once Home has loaded once. Tooltip: "Not available offline" |
| Loading (in flight) | selected styling on the *target* word immediately (optimistic, as today) | — | the row grows the **ProgressBar** below it (§2.6); the old content dims |

The **whole row** is `role="tablist" aria-label="Content type"`; each word `role="tab" aria-selected`. It is one Tab
stop (roving tabindex).

### 2.3 The Following sub-chip

Semantics: `subChips[0].id` = `music-following-chip` / `podcasts-following-chip`. Selecting it loads *another* page
(the facet variable becomes the sub-chip id). It is a narrower facet, so the control is a **toggle** ("only from
artists/shows you follow") whose on-state means "the sub-chip is the selected facet".

- **Control**: Windows AppBarToggleButton look (icon + label, subtle when off, subtle-filled with an accent glyph
  when on — the Paint / Snipping Tool toolbar toggle). `height:32px; padding:0 12px 0 10px; gap:8px; border-radius:4px;`
  glyph 16 px inline SVG, label Body 14/20 "Following" (verbatim `subChips[0].label`).
- Off: transparent, text TextSecondary, glyph outline (person-with-check). Hover: `SubtleFillColorSecondary`, text
  TextPrimary. Pressed: `SubtleFillColorTertiary`.
- On: fill `SubtleFillColorSecondary`, text TextPrimary, glyph **filled and accent-coloured** (dark
  `AccentTextFillColorPrimary` `#99ebff`, light the system accent). Hover on: `SubtleFillColorTertiary`+`.02`.
- Focus: the two-tone rect. `role="button" aria-pressed`, `aria-description="Only from shows you follow"` (Podcasts)
  / "Only from artists you follow" (Music).
- **Appears/disappears with the facet**: fades in 167 ms when the selected chip has sub-chips, fades out 83 ms when
  it doesn't (All, Audiobooks). No layout shift elsewhere (it lives at the trailing edge).
- **State is per facet and not sticky across facets**: switching Music→Podcasts lands on plain Podcasts (the
  server has no "following" concept across chips and the ids differ). Going back to Music restores Music's last
  toggle state for the tab's lifetime.
- Switching the toggle is a **facet switch** with the same loading behaviour as §2.6 (bar + dim, old page kept).
- Compact band: the same control at 28 px height (padding 0 10 0 8, glyph 14).

### 2.4 Scroll behaviour — the compact band

Nothing about the rest row is sticky. When the row's **bottom edge** (page y = 112, i.e. scrollTop > 64) passes
the surface top, a **44 px sticky band** appears at the top of the content surface and the rest row simply scrolls
away underneath it. Discrete swap, not scroll-linked scaling (Fluent transitions are discrete; scroll-linked
morphs are an iOS idiom and expensive in the engine).

```
y=48  ┌────────────────────────────────────────────────────────────────────────────────┐
      │  ┌──────┬───────┬──────────┬────────────┐                    ┌───────────────┐ │  band 44 px
      │  │ All  │ Music │▐Podcasts▌│ Audiobooks │                    │ ◯ Following   │ │  segmented 32 px
      │  └──────┴───────┴──────────┴────────────┘                    └───────────────┘ │
y=92  ├──────────────────────────────── 1 px divider (or the 3 px bar while busy) ─────┤
      │  (chapter headers stick under this band at top:44, as they stick at 56 today)  │
```

- **Band**: `position:sticky; top:0; height:44px; padding:0 32px; z-index:45;` background = surface fill at 92 %
  + `backdrop-filter: blur(16px) saturate(130%)` (the canvas's current `.pivot-row` recipe), bottom edge 1 px
  `StrokeDividerDefault` (`rgba(255,255,255,.0837)` dark / `rgba(0,0,0,.0803)` light).
- **Segmented control** (the Snipping-Tool mode switch): track `height:32px; border-radius:4px;`
  fill `ControlAltFillColorSecondary` (dark `rgba(0,0,0,.10)`, light `rgba(0,0,0,.0241)`), 1 px stroke
  `ControlStrokeDefault`. Items `padding:0 12px; height:30px; font:14/20` TextSecondary; hover item
  `SubtleFillColorSecondary`; **selected** item is the raised Standard-button look: fill `ControlFillColorDefault`
  (dark `rgba(255,255,255,.0605)`, light `rgba(255,255,255,.70)`), 1 px stroke (top `rgba(255,255,255,.093)`,
  bottom `rgba(0,0,0,.18)` dark / `rgba(0,0,0,.0578)` light), radius 4, text TextPrimary **Semibold**. Focus: the
  two-tone rect around the item. Same `tablist`/`tab` semantics; only one of the two controls is in the a11y tree at
  a time (the other is `aria-hidden` / unmounted).
- **Transition**: band fades+drops in over 167 ms decelerate (`cubic-bezier(0,0,0,1)`, from −8 px) when
  scrollTop crosses 64 downward; fades out 83 ms crossing upward. Hysteresis 8 px so it never flickers at the edge.
- **Chapter headers** (`.chap`) change their sticky `top` from 56 to 44.
- **Switching while scrolled**: the target facet's own remembered scroll offset is restored (the app already keys
  scroll by `_scrollScope + "home:" + facet`). A never-visited facet opens at the top, so the band dissolves and the
  big row shows the new selection: the "new page" moment.

### 2.5 Keyboard and screen reader

- Tab order: title-bar controls → rail → **facet tablist** (one stop; the selected word holds tabindex 0) →
  Following toggle → page content in reading order. The compact band, when shown, takes the same slot.
- In the tablist: **Left/Right** move focus between words (wraps), **Home/End** first/last, **Enter/Space** select.
  Selection does **not** follow focus — a switch is a network round-trip and a page swap; arrowing through must not
  fire four requests. (WinUI TabView semantics, not SelectorBar's selection-follows-focus.)
- **No Ctrl+digit accelerators**: Ctrl+1…9 are the browser-style shell-tab convention (the shell already owns
  Ctrl+T for a new tab, Ctrl+K for the palette). Instead the command palette gets four commands: "Home: All",
  "Home: Music", "Home: Podcasts", "Home: Audiobooks", plus "Home: Following on/off" when applicable.
- Screen reader: tablist "Content type"; tab names are the labels; `aria-selected`. On a switch the content region
  gets `aria-busy=true` while in flight; on publish a polite live region announces "{Label} home" (e.g. "Podcasts
  home"); on failure the InfoBar text is announced (assertive). The ProgressBar's accessible name is
  "Loading {Label}". The toggle: "Following, toggle button, not pressed".
- Focus never moves on a switch; it stays on the word (so arrow+Enter can continue). The page's first zone gets
  no auto-focus.

### 2.6 Motion on switch — keep the page, pin a ProgressBar (owner's rule)

Mirrors the owner's dashboard (`PageHeader.tsx` / `PageBody.tsx`): Skeleton **only** on the very first load of Home
when nothing is cached (`Skel.Region` on first mount — already how the app behaves via `InitialHome()` /
`Feeds.HasRevealed`); every later fetch **keeps the previous data**, dims the body and pins an indeterminate
ProgressBar under the header.

Timeline for a click on a word (or the Following toggle) whose page is **not cached**:

| t | Pivot | Content | Bar |
|---|---|---|---|
| 0 | target word becomes selected (weight+colour, 83 ms; width reflow 260 ms); old word dims | old page stays in place; `opacity → 0.6` over 167 ms (`durationNormal`); `aria-busy=true`; **no hit-testing** (hover/press/clicks ignored, wheel scrolling still works) | appears at 0: a **3 px indeterminate accent ProgressBar**, full content width (1316), pinned to the bottom edge of the facet control (the rest row when it is in view, the sticky band when scrolled). The engine's `ProgressBar.Indeterminate(width)` (WinUI two-indicator sweep, 2 s loop). Its 1 px track replaces the band's divider while busy |
| data lands | — | old page fades out **83 ms** (opacity 0.6 → 0, no movement); new page mounts at the facet's remembered scroll (top if never seen); zones enter with the canvas `enter` motion: 250 ms `cubic-bezier(0,0,0,1)` fade + 8 px rise, **30 ms stagger per zone**, capped at the first 6 zones; items inside a zone don't stagger separately | fades out 83 ms at the same instant |
| +~350 | settled | `aria-busy=false`, announce "{Label} home" | gone |

- **Cached** facet (revealed earlier this session, ≤ 10 min old): no bar, no dim; old out 83 ms, new in as above.
  Total ≈ 330 ms. If the cached document is > 10 min old it is shown instantly **and** refreshed in the background:
  the bar shows (no dim), and the refreshed document replaces the shown one only if the user hasn't scrolled or
  clicked in the meantime (scrollTop still 0 and < 2 s since the switch); otherwise the fresh copy is kept for the
  next visit (the existing `HomeRevealGate` Reveal/Swap/Held verdicts model this).
- **Failure** (the flight concludes with no sections while online — the existing `home.facet.failed` path): bar
  hides, content restores to 1.0 over 167 ms and becomes interactive, the pivot **reverts** to the previously
  published facet (`SelectedFacet = _publishedFacet`, 83 ms), and an **InfoBar** (Error severity, "Couldn't load
  Podcasts. Check your connection and try again." + Retry button) slides in **under the bar's slot**, above the
  content, pushing content down 48 px + 8 px (WinUI InfoBar: 8 px radius, 1 px stroke, icon 16, closable). The old
  content stays.
- **Offline**: words for uncached facets are disabled (§2.2); a cached facet swaps instantly with no bar.
- **Following toggle**: identical behaviour (it *is* a facet switch); the toggle flips immediately and reverts on
  failure.
- Never: a shimmer, a skeleton, a spinner in the middle of the page, or a blank surface.

### 2.7 URL, deep link and persistence

- Subjects already exist: `wavee:home` (All) and `wavee:home:<chip-id>` (facet), sub-chips included
  (`wavee:home:podcasts-following-chip`).
- Deep link: `wavee://open?route=home&arg=<chip-id>` — `arg` is currently unused for `home`; define it as the facet
  id (`podcasts-chip`, `music-following-chip`). Missing/unknown arg → All.
- **History**: a facet switch pushes a navigation entry in the tab (Back returns to the previous facet at its
  remembered scroll). The content changes wholesale, so Back must undo it — Store's category pages behave this way.
- **Per tab, for the tab's lifetime**: the selected facet and each facet's Following state live on the Home tab
  instance (`HomeLandingView`), not on the static `Home.SelectedFacet` — two Home tabs can show two facets.
  Reopening Home from the rail restores the tab's facet.
- **Not persisted across launches**: Home opens on All every launch (it is the only page with Recents and the
  daylist, and it is where Spotify's own clients land). Revisit if telemetry ever shows podcast-first sessions.
- Scroll position: per facet (existing `ScrollKey`), for the tab's lifetime.

### 2.8 Loading and error per facet — the cache model

- One document per facet id in the Homes table (as today). Cached = revealed this session; freshness 10 min.
- **Prefetch on intent**: hovering a word for 150 ms, or moving keyboard focus onto it, prefetches that facet if it
  has never been fetched this session (once per facet per 10 min; never for the Following sub-facet, never when
  offline or on a metered connection). Responses are ~0.7–0.9 MB, so no speculative fetch of all facets at launch.
- The All document is fetched at launch (warm start from the previous session's snapshot, as today).
- First-ever Home load with nothing cached: the skeleton (existing `Skel.Region`), for All only. A facet clicked
  while All is still skeletal: the skeleton stays and the bar shows under the row (the skeleton is the "old
  content").
- Errors are per facet (§2.6). An offline launch shows the last All snapshot with the words for uncached facets
  disabled.

### 2.9 Wireframes

**(a) Top of page at rest — Podcasts selected (dark, 1440 wide; rail and title bar as context)**

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ≡  ←  →   ⌂ Home̲̲̲̲          [ Search songs, artists, albums…        🔍 ]            ◯  ☼  – ▢ ✕ │ 48
├──┬─────────────────────────────────────────────────────────────────────────────────────────────────┤
│♡ │╭────────────────────────────────────────────────────────────────────────────────────────────╮  │
│◎ ││                                                                                            │  │ 24
│☺ ││  All   Music   Podcasts   Audiobooks                                       ◯ Following      │  │ 40  ← Title 28: "Podcasts" 600/primary, others 400/secondary
│◠ ││                                                                                            │  │ 20
│🎙││  New episodes                                                                 See all  ‹ › │  │ 44  chapter (Subtitle 20)
│  ││  ┌────────────────────────┐  ┌────────────────────────────────────────────────────────────┐ │  │
│⌂ ││  │                        │  │ ▣  Hot Take: A Great Movie Trilogy Must Have ONE FLOP…     │ │  │ 76
│  ││  │   video thumb 428×241  │  │    Hey Tablo · Tuesday · 48 min                            │ │  │
│▪ ││  │                        │  ├────────────────────────────────────────────────────────────┤ │  │
│▪ ││  │                        │  │ ▣  The Doomsday Cult Inside OpenAI                         │ │  │ 76
│▪ ││  └────────────────────────┘  │    Patrick Boyle On Finance · Sunday · ▶ 31 min            │ │  │
│▪ ││  The Fund Structure That     ├────────────────────────────────────────────────────────────┤ │  │
│  ││  Could Replace the ETF       │ ▣  Essentials: How to Assess & Improve All Aspects of…     │ │  │ 76
│  ││  The Brave Technologist ·    │    Huberman Lab · 17 Sep · ▶ 35 min                         │ │  │
│  ││  Wednesday · ▶ 27 min        ├────────────────────────────────────────────────────────────┤ │  │
│  ││                              │ ▣  John Park is like Tukutz but way SMARTER | Hey Tablo…   │ │  │ 76
│  ││                              │    Hey Tablo · 15 Sep · 1 h 1 min                          │ │  │
│  ││                              └────────────────────────────────────────────────────────────┘ │  │
│  ││                                                                                            │  │ 40
│  ││  Continue listening                                                                        │  │ 44
│  ││  ┌──────────────────────────┐ ┌──────────────────────────┐ ┌──────────────────────────┐   │  │
│  ││  │▣ Scott Bessent Is at War…│ │▣ The Ancient Medicine of…│ │▣ Hey, Whatever Happened… │   │  │ 76
│  ││  │  Patrick Boyle · 44 min l│ │  Doctor Mike · 2 Sep · 3 h│ │  ColdFusion · 20 Apr · 17│   │  │
│  ││  │  ▂▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁ │ │                          │ │                          │   │  │
│  ││  └──────────────────────────┘ └──────────────────────────┘ └──────────────────────────┘   │  │
├──┴─────────────────────────────────────────────────────────────────────────────────────────────────┤
│ [art]  She Looks So Perfect  ♡     ◁ ⏸ ▷    0:19 ━━━━━━━━━━━━━━━━━━━━━ 3:22     ⇄ ↻ 🔊 ▤ ⋯      │ 80
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

**(b) Scrolled — sticky band with the Segmented control (Podcasts), chapter header stuck under it**

```
├──┬─────────────────────────────────────────────────────────────────────────────────────────────────┤ 48
│  ││ ┌──────┬───────┬──────────┬────────────┐                              ┌───────────────┐       │ 44 band (blurred surface)
│  ││ │ All  │ Music │▐Podcasts▌│ Audiobooks │                              │ ◯ Following   │       │    32 segmented; selected = raised
│  ││ └──────┴───────┴──────────┴────────────┘                              └───────────────┘       │
│  ││─────────────────────────────────── 1 px divider ───────────────────────────────────────────────│
│  ││  Episodes you might like                                                            ‹ ›       │ 52 chapter, sticky top:44
│  ││  ┌───────────────────────────────────────────┐ ┌───────────────────────────────────────────┐  │
│  ││  │▣ E How to Overcome Anxiety, Solved        │ │▣ How To Organize Your Life Like The Top 1%│  │ 76
│  ││  │  SOLVED with Mark Manson · 6 May · ▶ 2 h… │ │  Chamath Palihapitiya · 10 Sep · ▶ 8 min  │  │
│  ││  └───────────────────────────────────────────┘ └───────────────────────────────────────────┘  │
```

**(c) Mid-switch — user clicked "Podcasts" while on Music (not cached): old page kept and dimmed, bar pinned**

```
├──┬─────────────────────────────────────────────────────────────────────────────────────────────────┤ 48
│  ││                                                                                               │ 24
│  ││  All   Music   Podcasts   Audiobooks                                       ◯ Following        │ 40  "Podcasts" already 600/primary; "Music" back to 400/secondary
│  ││  ▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂ │ 3   indeterminate accent ProgressBar, 1316 wide, sweeping; 1 px track
│  ││                                                                                               │ 17
│  ││ ░Made for you░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ See all ‹ ›░│    ← the MUSIC page, still laid out, opacity .6,
│  ││ ░┌──────────────────┐┌──────┐┌──────┐┌──────┐┌──────┐░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│      aria-busy, no hover/press; wheel still scrolls
│  ││ ░│ Discover Weekly  ││ DJ   ││DM 1  ││DM 2  ││DM 3  │░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│
│  ││ ░└──────────────────┘└──────┘└──────┘└──────┘└──────┘░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│
│  ││ ░Soundtrack your Friday afternoon░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│
```

Failure variant of (c): the bar disappears, "Music" is selected again, the content is back to 1.0, and a WinUI
InfoBar (Error) sits where the 17 px gap was: `⊗ Couldn't load Podcasts. Check your connection and try again.
[Retry]  ✕`, 48 px tall, 8 px radius, content pushed down by 56 px with a 167 ms reflow.

---

## 3. Per-facet page layouts (real content)

All widths on the 1316 px content column. Grids: 8-up squares 150 (gap 16), 6-up 206, 4-up 317 (16:9 → 178),
3-up 428 (16:9 → 241), 2-up 650. Templates come from the existing canvas vocabulary: `.chap` chapter header,
`.gitem` square/wide grid item, `.lrow` list row, `.ritem` horizontal row item, `.gcard` grouped list card,
`.tile` navigation tile.

### 3.1 All — the launch page (home_new.json, "Good morning")

What it is: Spotify's default landing. **Mixed only in principle**: this capture has Recents (20 mixed-type items,
including Liked Songs), the daylist, Made For Chris, "It's New Music Friday!", Your top mixes, Soundtrack your Friday
morning, Recommended Stations, Officiële Spotify playlists, Jump back in, More like Troye Sivan, Popular radio and 20
baseline singles — and **no podcast or audiobook section at all**. So All is the page the canvas `Main.dc.html`
already draws (daylist hero → Recently played → Made for you → New Music Friday → Because you like… → Jump back in →
Radio & mixes → Browse → Charts). Only change: the facet row above the hero, "All" selected, **no Following toggle**.

How All differs from Music (same user, morning vs afternoon captures — differences that are structural, not
time-of-day, are marked ★):
- ★ **Recents and "Jump back in" exist only on All.** The Music facet response has neither (it has a Shorts rail
  of 20, All's has 64). Resume lives on All.
- "It's New Music Friday!" and "Officiële Spotify playlists ✅" are on All only in these captures (may be
  time-gated; the Music capture is the afternoon).
- Music adds "Best of artists" (10 This Is…) and four full "More like {artist}" / "For fans of" shelves that All
  only has as one-item baselines.
- Both share Made For Chris, Your top mixes, Recommended Stations, Soundtrack your Friday {daypart}, Popular radio.

### 3.2 Music (home_filtered.json, "Good afternoon", 29 sections)

Zone order (M0 row, then chapters). Every title is the server's verbatim `transformedLabel` unless noted.

| # | Zone (chapter title) | Template | Items (server order; page-wide URI dedupe applied top-down) |
|---|---|---|---|
| M0 | Facet row | §2.1, **Music** selected, Following toggle off | — |
| M1 | **Made For Chris** (why-caption: "Your daily mixes, refreshed today.") | 6-col grid, Discover Weekly as the 2-cell lead (`05` §5), squares 206 | Discover Weekly (lead) · DJ · Daily Mix 1 · Daily Mix 2 · Daily Mix 3 · Daily Mix 4 · Daily Mix 5 · Daily Mix 6 (2 pages: 2+4, then 3; the pager shows pips) |
| M2 | **Soundtrack your Friday afternoon** | 6-col squares 206, pager (2 pages) | scream teen pop friday morning (the daylist) · Fun Road Trip Mix · Hopeless Romantic Soft Mix · Workout Pop Mix · Chill Moody Mix · Breakup Mix ‖ Soft Mix · Hopeless Romantic Love Mix · Wedding Mix · Happy Walking Mix |
| M3 | **More like Avril Lavigne** | 6-col mixed: playlists/albums square, artists round | Avril Lavigne Radio · 00s Pop Rock · Songs We Rocked Out To · All Out 2000s · Breakaway (Kelly Clarkson) · The Kids In The Crowd (Simple Plan) ‖ No Pads, No Helmets…Just Balls · Kelly Clarkson ◯ · P!nk ◯ · Simple Plan ◯ (the baseline single "More like Avril Lavigne → The Kids In The Crowd" is the same album: deduped) |
| M4 | **More like Jung Seung Hwan** | same | Jung Seung Hwan Radio · Lo9ve3r4s (JOONIL JUNG) · v o K a l · Best of Korean OSTs · Paul Kim ◯ · My Love From the Star Pt 7 ‖ 바니와 오빠들 (Crushology 101) · JOONIL JUNG ◯ · BEN ◯ · Me After You (Paul Kim) (two baseline singles fold in: Jung Seung Hwan Radio and Crushology 101 are already here → deduped) |
| M5 | **Best of artists** (server subtitle: "Bringing together the top songs from an artist.") | 6-col squares | This Is Lauv · This Is Troye Sivan · This Is Henry Moodie · This Is ROSÉ · This Is Justin Bieber · This Is Shawn Mendes ‖ This Is Maroon 5 · This Is Imagine Dragons · This Is JVKE · This Is League of Legends |
| M6 | **More like Shawn Mendes** | 6-col mixed | Shawn Mendes Radio · Nine Track Mind (Charlie Puth) · All Out 2010s · V (Maroon 5) · 10s Love Songs · Hit Rewind ‖ Camila Cabello ◯ · Know-It-All (Alessia Cara) · Alessia Cara ◯ · Charlie Puth ◯ (baseline "Hit Rewind" deduped) |
| M7 | **For fans of Henry Moodie** | 6-col squares (7 → 6 + 1) | This Is Henry Moodie (dedupe vs M5 → drop; next) Henry Moodie Radio · Warm Fuzzy Feeling · Alone Again · sad hour · Easy · Chilled Pop Hits (baseline "Chilled Pop Hits" deduped) |
| M8 | **Because you like…** (why-caption "Picked from what you play most.") | 3-col `.gcard` grouped list cards, rows 56 | **90's Nederlandstalig** (over: "More like"): 00's Nederlandstalig · 70's Nederlandstalig ‖ **Sleep Music for Deep Sleeping** (over: "More like"): Sleep Frequency · 528 Hz ‖ **Based on your recent listening**: Ambient Relaxation · Breathe · Stress Relief. Second row of cards, 2-col: **Made for you**: Roy Kim Radio · EYES CLOSED (with ZAYN) Radio · I Don't Love You Radio · This Is IU · This Is Keenan Te ‖ **For fans of Acda en de Munnik** is a singleton (90's Allerbeste) → folds into a **More for you** card together with nothing else → rule: singletons join "Made for you" (renamed **More for you**, 6 rows) |
| M9 | **Radio & mixes for you** (caption "Based on artists and songs you play.") | 8-col: 2-cell lead (Physical Radio, first station) + station circles 150 + mix squares | Physical Radio (lead) · One Day (feat. Helena) Radio ◯ · vaultboy Radio ◯ · Rex Orange County Radio ◯ · LE SSERAFIM Radio ◯ · Damiano David Radio ◯ ‖ Vluchtstrook Radio · loml Radio · Ze Komt Uit Amsterdam Radio · Dire Straits Radio · Sade Radio · Dua Lipa Radio · Fleetwood Mac Radio · Coldplay Radio ‖ Phil Collins Mix · Pop Mix · K-Pop Mix · 2020s Mix · Soft Pop Mix · 2010s Mix · Love Mix · Sad Mix (Teddy Swims/Frenna/Norah Jones/Roxy Dekker/Suzan & Freek Radio, Soundtrack Mix, vaultboy Mix on later pages) |
| M10 | **Browse** | `.tile` navigation tiles, 3-col | from the Shorts rail (UnknownType dropped): Q-top 1500 \| editie 2025 · Heather Radio · WLUWD (Tristam) · Kris Kross Amsterdam · I loved you too much to just laugh it off (doyouka) · sombr (items already placed above — the daylist, 90's Nederlandstalig, Daily Mix 2 — are skipped by dedupe) |

Music rules that differ from All: no Recents/Jump back in/Charts zones (not in the response; never synthesise
them from All's data — that would show a Music page lying about its source). The daylist hero is **not** repeated
on Music (it is in M2 as a plain square); Music opens straight on M1 so it doesn't look like All with a filter.

### 3.3 Podcasts (podcasts.json, 31 sections → 8 zones)

Regroup rules are in §4.3. Dates relative to the capture day (Fri 25 Sep 2026): Today / Yesterday / weekday
name within 6 days / "23 Sep" within the year / "6 May 2025" otherwise. Duration: "27 min", "1 h 1 min", "2 h 10
min". "▶" before the duration marks a VIDEO episode (12 px glyph, TextTertiary). "E" = explicit badge (14×14,
2 px radius, 1 px TextSecondary stroke, 10 px "E", before the show name — Media Player's badge).

**P0 — Facet row**: Podcasts selected (600/primary), Following toggle off.

**P1 — New episodes** (from "New episode from {show}" singles; sorted newest first; caption under the chapter
title: "From shows you follow"). Template: **lead + list**: left a 428-wide lead card (video thumb 428×241, 4 px
radius; title Body Strong 2 lines; caption), right a 872-wide list of four 76 px rows (gap 4), total height 316.

| slot | episode | show · date · duration |
|---|---|---|
| lead | The Fund Structure That Could Replace the ETF | The Brave Technologist · Wednesday · ▶ 27 min |
| row 1 | Hot Take: A Great Movie Trilogy Must Have ONE FLOP \| Hey Tablo Ep.39 | Hey Tablo · Tuesday · 48 min |
| row 2 | The Doomsday Cult Inside OpenAI | Patrick Boyle On Finance · Sunday · ▶ 31 min |
| row 3 | Essentials: How to Assess & Improve All Aspects of Your Fitness \| Dr. Andy Galpin | Huberman Lab · 17 Sep · ▶ 35 min |
| row 4 | John Park is like Tukutz but way SMARTER \| Hey Tablo Ep. 38 | Hey Tablo · 15 Sep · 1 h 1 min |

**P2 — Continue listening** (every IN_PROGRESS episode first, then "Catch up on your shows"). Template: 3-col
`.ritem` horizontal row items 428×76 (art 56 square, 4 px), each with a **2 px progress hairline** across the text
column's bottom (track `ControlStrokeDefault`, fill accent; the fraction is real — 8 334 ms of 44 min ≈ 0.3 %,
rendered with a 2 px floor so the bar is visible as "just started"; caption says the truth).

| item | caption |
|---|---|
| Scott Bessent Is at War With Prices — and Prices Are Winning! | Patrick Boyle On Finance · ▶ 44 min left |
| The Ancient Medicine of Uncontacted Amazon Tribes \| Paul Rosolie | The Checkup with Doctor Mike · 2 Sep · ▶ 3 h 9 min |
| Hey, Whatever Happened to NFTs? | ColdFusion · 20 Apr · ▶ 17 min |

Hover on any episode row/tile: 4 px backplate + a 32 px round accent Play button over the art (Media Player idiom).
Click on the row opens the episode; Play plays it.

**P3 — Your shows** (verbatim). Template: 8-up squares 150 (image 150×150 radius 4, title 14/20 600 one line,
caption 12/16 publisher). Order as served: Patrick Boyle On Finance (Patrick Boyle) · The Brave Technologist (Brave)
· SOLVED with Mark Manson (Mark Manson) · The Pog State (Riot Games Korea) · Huberman Lab (Scicomm Media) ·
ColdFusion (ColdFusion) · Hey Tablo (Team Epikase) · The Checkup with Doctor Mike (DM Operations Inc.). No
"3 new" badges: the response has no per-show counts. Chapter has "See all" → library shows.

**P4 — Videos you might like** (the two "Videos you might like" singles minus the in-progress one, topped up with
VIDEO episodes from "Episodes you might like" to 4). Template: 4-up 16:9 tiles 317×178 (`.wide`), title 2 lines,
caption.

| tile | show · date · duration |
|---|---|
| How Long Can The Stock Market Ignore Reality? | How Money Works · 30 Jul · ▶ 15 min |
| Intelligence Scoop: Is Russia Preparing to Attack Europe? | The Rest Is Classified · Wednesday · ▶ 53 min |
| The Bipartisan Who Split America | Destiny · Wednesday · ▶ 53 min |
| What Chinese rule will actually look like \| Dr. Dave Kang | Money & Macro Talks · 10 Sep · ▶ 47 min |

**P5 — Episodes you might like** ("Similar to your interests" ×4 + "Episodes you might like" not used in P4 +
groups with < 3 episodes, i.e. "More episodes featuring Arthur Brooks"). Template: 2-col list rows 650×76, gap 4.

| row | episode | caption |
|---|---|---|
| 1 | E How to Overcome Anxiety, Solved | SOLVED with Mark Manson · 6 May · ▶ 2 h 10 min |
| 2 | How To Organize Your Life Like The Top 1% | Chamath Palihapitiya · 10 Sep · ▶ 8 min |
| 3 | E How to Change Your Life, Solved | SOLVED with Mark Manson · 3 Jun · ▶ 4 h 58 min |
| 4 | How to Improve Motivation & Overcome Procrastination \| Dr. Masud Husain | Huberman Lab · 24 Aug · ▶ 2 h 20 min |
| 5 | The Shocking Lessons He Teaches His Harvard Students \| Arthur Brooks | The Checkup with Doctor Mike · 25 Jan · ▶ 1 h 59 min |
| 6 | Young people say their lives feel fake. Here's why \| Arthur Brooks | The Big Think Interview · 7 Aug · ▶ 1 h 53 min |

**P6 — Because you listen to…** (grouped list cards, the Podcasts twin of Music's "Because you like…"). Template:
3-col `.gcard` 428 wide: header = eyebrow 12 tertiary + name 14/600; rows 56 (art 40); optional **footer strip**
(6 × 28 px show thumbs, gap 4, "See all" link) when the same key also has a shows shelf.

| card | eyebrow / name | rows | footer |
|---|---|---|---|
| 1 | Popular with listeners of / **Patrick Boyle On Finance** | The OpenAI IPO … It's Worse Than You Think — MonkeyExplains · ▶ 15 min ‖ Stock Picking in Difficult Times with George Noble — The Real Eisman Playbook · 54 min ‖ Why China Built the World's Largest Air Battery — Undecided with Matt Ferrell · ▶ 17 min ‖ The Worlds Most Complicated Video Game Has A Wealth Inequality Problem — Micro · ▶ 24 min | thumbs: Unhedged · The Economics Show · How Money Works · The Real Eisman Playbook · The Plain Bagel · Money & Macro, then "See all 10" |
| 2 | Popular with listeners of / **ColdFusion** | Why The Nepal Flood Was Way Scarier Than You Think — RealLifeLore · ▶ 27 min ‖ The most expensive 33 hours in WordPress history… — Fireship · ▶ 5 min ‖ Dornier Do X The Largest, Heaviest, and Worst Flying Boat — Megaprojects · ▶ 19 min ‖ Why California's Most Famous Ghost Town Still Exists? — Places · ▶ 28 min | none |
| 3 | More like / **Why Germany Stopped Working** | Imperial Collapse Expert: The Real Reason Empires Fall… — Decoding Geopolitics · ▶ 39 min ‖ Houthi blitz threatens Suez Canal… — Iran: The Latest · 33 min ‖ Should markets discount the AI apocalypse? — Unhedged · 21 min ‖ Putin 'knocking on doors of NATO'… — Ukraine: The Latest · 50 min | "See all 9" (the two "Centre punch" copies from two Economist feeds collapse to one) |

**P7 — Shows you might like** (verbatim; total 50). Template: 8-up squares 150 + pager; shows already in Your
shows or in a P6 footer are skipped. Page 1: Fin vs History (Fin Taylor & Horatio Gould) · The Rest Is Classified
(Goalhanger) · Mentour Pilot (Mentour Pilot) · The Rest Is Politics: US (Goalhanger) · From First Principles ·
Destiny VoD Archive (Destiny) · The Philip DeFranco Show (philip defranco) · Modern Wisdom (Chris Williamson).
(How Money Works and Money & Macro Talks were placed in P6's footer → skipped.)

**P8 — Popular with listeners of SOLVED with Mark Manson** (verbatim; a shows-only key, so it stays a shelf).
8-up squares: Therapy in a Nutshell · The Psychology of Money with Morgan Housel · Philosophies for Life · Help Me Be
Me · How to Be a Better Human (TED) · The Psychology Podcast · A Bit of Optimism · Ideas To Thrive (Modern Wisdom
already in P7 → skipped; Dating Intentionally on page 2).

Bottom: 48 px before the now-playing bar. No Charts/Browse tail on Podcasts (no podcast categories in the
response; don't invent them).

Above the fold at 1440×900: P0, all of P1, P2 and the P3 chapter title — "what's new", "where you left off" and
"your shows" answered without scrolling.

**Following (podcasts-following-chip)**: no capture. Same zone rules; expect P1/P2/P3 only. If the response is
empty, the page shows the §3.4 empty pattern with "Nothing new from the shows you follow" and the toggle stays on.

### 3.4 Audiobooks — the starter/empty state

No capture exists and the user has no audiobook activity; Audiobooks has no sub-chip. The page keeps the facet row
(Audiobooks selected 600/primary, no toggle) and shows one empty-state block, **not** a blank surface:

- Block: 480 px wide, centred horizontally, top edge 160 px below the facet row (upper third, like Store/Photos
  empty states; not vertically centred).
- 40 px book glyph (inline SVG, TextTertiary) · Subtitle 20/28 "No audiobooks yet" · Body 14/20 TextSecondary
  "Audiobooks you save or start show up here, along with picks based on them." · one **Standard** button "Browse
  audiobooks" (opens Search with the audiobooks filter) — Standard, not Accent: there is no primary action on an
  empty page.
- If a future response does carry sections, they render with the generic shelf rules (squares 150 with title ·
  author caption); design that when data exists.

The existing States panel 3 ("Podcasts, nothing followed yet") is wrong for this user (he follows 8 shows); it
becomes the Audiobooks starter. Keep its copy pattern as the generic "empty facet" template with per-facet strings:
Podcasts → "Follow shows to see them here" (existing), Audiobooks → the strings above.

---

## 4. Data flow for the real app

### 4.1 Query and cache per facet

- `Spotify.Api.HomeQuery(facet, timeZone)` per facet id; All = `""`. The document lands in the Homes table under
  `wavee:home` / `wavee:home:<facet>` with `Inflight[slot]`, `Known & HomeFields.Sections`, chips and greeting
  (`Spotify.Decode.Home.Feed`). This is already right; keep one row per facet, including sub-chips.
- Freshness: 10 min per document; a stale document is shown and refreshed in the background (bar, no dim).
- `ChipsOf(feed)` already falls back to the unfiltered feed's chips when a facet document carries none — keep,
  the row must always be able to step back out.
- Prefetch on intent (§2.8) calls the same `EnsureFeed(Entities.HomeFeed(facet))` path; it must not change
  `SelectedFacet`.

### 4.2 The switch, in the app's terms (`Home.Page.cs` FeedTick, ~248–318)

What exists: the strip writes `Home.SelectedFacet` optimistically; the previous document stays on screen while
`!h.Knows(Sections)`; `_facetFlightSlot` tracks the flight; failure reverts to `_publishedFacet` and raises
`Notify.Say(Strings.Home.FacetFailed, Error)`; `HomeRevealGate` decides Reveal/Swap/Held.
What is missing, and what this design adds:
1. **Busy signal**: `bool switching = selected != _publishedFacet && scope.Homes.Inflight[h.Slot] != 0` → the facet
   row / band renders `ProgressBar.Indeterminate(contentWidth)` (3 px, `ProgressBarState.Normal`) at its bottom
   edge, and the content host gets `Opacity 0.6` (167 ms tween) + `HitTest = false` + `aria-busy`.
2. **Background refresh** (stale cached facet): same bar, no dim, no hit-test change; the gate's Swap verdict is
   only honoured while scrollTop == 0 and < 2 s since the switch.
3. **Publish** (`Publish(feed)`): old content fades 83 ms, new content mounts with the zone stagger, bar hides,
   `MarkRevealed`, live-region announce.
4. **Failure**: existing revert + `Notify.Say` becomes an InfoBar under the bar slot (the app's `InfoBarSeverity.Error`
   is already the type); add a Retry action that re-runs `EnsureFeed`.
5. Sub-chip toggling goes through exactly the same path (its facet id is the sub-chip id).
6. Move `SelectedFacet` + per-facet Following memory onto the Home tab instance; push a history entry on switch.

### 4.3 Hydration: what extra lookups fill what

- **Home response alone** already carries everything the Podcasts page draws: episode name/description, duration,
  release date, played state + position, media types, explicit rating, 3 cover sizes, the 1280×720 video preview,
  the parent show (name, publisher, cover, mediaType), and show cards (name, publisher, cover, mediaType, explicit).
- **Extended metadata / playlist lookups** (existing, music): header images (`header_image_url_desktop`), track
  counts, descriptions — Music/All only.
- **Episode lookups** (existing "podcast episode lookups"): use them to **refresh `playedState`** for every
  episode placed in P1/P2 (the home payload's played state can be stale by minutes; resume position must be
  current before a Play click) and to fetch the full description for the row's tooltip/context. Not needed for
  layout — never block the reveal on them.
- **Baseline section lookups**: podcast baseline sections come back `items=1 total=0` — `totalCount` is not
  populated, so "See all N" on a baseline-derived group counts the merged items client-side, and the section
  `uri` (`spotify:section:…`) is what "See all" navigates to for the server-backed shelves ([10], [12], [17],
  [22]) that report real totals (20, 20, 50, 10).
- **Not available, don't fake**: per-show unplayed counts, a show's latest-episode date, listening history
  timestamps, podcast categories. A show lookup could later supply "New episode · Wednesday" captions in P3.

### 4.4 Podcasts regroup rules (deterministic, testable as a pure class like `HomeFacetProjection`)

Input: the facet document's sections in server order. Output: the zone list P1…P8. Keys are derived from the
section title with a fixed phrase set (the server uses exactly these):

1. `New episode from {show}` → **New episodes**; sort by `releaseDate` desc; lead = newest with a video thumb
   (else the newest). Cap 5 (1 lead + 4 rows); overflow → "See all".
2. Any episode with `playedState.state == IN_PROGRESS` (from **any** section) → **Continue listening**, first,
   in server order; then `Catch up on your shows` items. An in-progress episode is removed from every other zone.
   Caption for in-progress: "{remaining} left" from `duration − playPosition`; no date.
3. `Videos you might like` → **Videos**; if fewer than 4 remain after rule 2, top up (move, not copy) with
   `mediaTypes ∋ VIDEO` items from the pool of rule 4, newest first. Cap 4 (one row of 16:9 tiles).
4. `Similar to your interests` + `Episodes you might like` → one pool **Episodes you might like**; also receives
   the leftovers of rules 3, 5 and 6. Dedupe by uri **and** by (name, releaseDate) — the two Economist feeds
   republish the same episode under different uris. Cap 6 rows (3 × 2-col); overflow → "See all".
5. `Popular with listeners of {X}` → group **X**; episode singles become the card's rows, a shows shelf with the
   same title becomes the card's footer strip (6 thumbs + "See all N" to the shelf's section uri). A key with
   episodes but < 3 of them → its episodes fold into rule 4. A key with shows only → stays a shows shelf (P8).
6. `More episodes featuring {person}` → group **{person}** ("Featuring" eyebrow) if ≥ 3 episodes, else rule 4.
7. `More like {episode title}` (a HomeGeneric shelf of episodes) → group card "More like / {title}", 4 rows +
   "See all N".
8. Group cards render 3 per row (428); the row count of a card is 4 max; cards in the same row share height.
9. `Your shows` → P3 verbatim (never deduped away; it is identity). `Shows you might like` → P7.
10. **Page-wide URI dedupe** in zone order P1→P8. Shows appearing in Your shows are removed from every
    recommendation shelf. Footer thumbs are navigation, not placements: they don't consume a uri.
11. Drop items with `playability.playable == false` or `restrictions.paywallContent == true`; drop `UnknownType`.
12. A zone with 0 items is not rendered; if every zone is empty → the empty state (§3.4 pattern, podcast strings).

Music/All keep the rules from `03-design-brief.md` §3 (entity fold, page-wide dedupe, radio merge).

---

## 5. Implementation notes for the mockup agent

Canvas: `home-canvas-pub/project/` (format in `canvas-format.md`). Class vocabulary to reuse: `.chap`, `.gitem
w150|w206|w317|w428`, `.wide`, `.lrow`, `.ritem`, `.gcard/.gcard-hd/.over/.who`, `.tile`, `.btn`, `.ibtn`,
`.linkbtn`, `.badge`. New classes to add to the helmet of every board that shows the row: `.facet-row`,
`.facet-word[.sel]`, `.facet-toggle[.on]`, `.facet-band`, `.seg/.seg-item[.sel]`, `.facet-bar`, `.ep-row`,
`.ep-lead`, `.progress-hair`, `.xbadge`, `.vid-glyph`.

### 5.1 Boards to change

1. **Main.dc.html** (All, 1440×3200, keep): delete `.pivot-row` (the 56 px sticky underline tabs) and its 56 px
   offset; insert the **rest facet row** as the first child of `.contentcol` (24 px above, `margin-bottom:-20px`),
   "All" selected, no toggle. `.chap{top:44px}` (chapters now stick under the compact band, which on this static
   board is not shown). Board height shrinks by 12 px or keep 3200.
2. **Light.dc.html** (1440×900): same row in light tokens (selected `rgba(0,0,0,.8956)` 600, others
   `rgba(0,0,0,.6063)` 400; hover backplate `rgba(0,0,0,.0373)`).
3. **States.dc.html**: panel 3 becomes **"Audiobooks — nothing yet"**: the rest row with Audiobooks selected and
   no toggle, then the §3.4 block (book glyph, "No audiobooks yet", body, Standard "Browse audiobooks"). Panel
   title: "Audiobooks, nothing yet"; note: "An empty facet keeps its title and gets a starter, never a blank page".
4. **Components.dc.html** (1440×1780 → grow to ~2400): add redline entries — (a) facet word: rest / hover /
   pressed / focus / selected / disabled, with the 40-px box, 8-px padding, 24-px gap, 28/36 type; (b) Following
   toggle: off / hover / on / focus, 32 px and the 28 px band variant; (c) Segmented (band form): track, hover
   item, selected raised item, focus; (d) the 3 px ProgressBar in both hosts (under the row, under the band);
   (e) episode list row 76 px: audio, video (▶ glyph), explicit (E badge), in-progress (hairline + "44 min left"),
   hover (backplate + round play); (f) episode lead card 428×(241+…); (g) 16:9 video tile 317×178; (h) show
   square 150; (i) group card with footer strip.

### 5.2 Boards to add (positions for `canvas.json`)

| file | title | size | x,y | content |
|---|---|---|---|---|
| `Podcasts.dc.html` | Home — Podcasts, full page | 1440×2700 | 3040, 0 | §3.3 P0–P8 in full, dark, rest row (Podcasts selected, toggle off), shell drawn as context like Main |
| `Music.dc.html` | Home — Music, top | 1440×1520 | 4560, 0 | M0 (Music selected, toggle off) + M1 + M2 + M3; the chapter titles verbatim; enough to show that Music is its own page (no hero, no recents) |
| `FacetSwitch.dc.html` | Facet switch — motion & states | 1440×1900 | 1520, 5220 | six 1440×260 frames stacked with 40 px label rows: ① rest, hover on "Podcasts" (backplate, primary text) · ② t=0 click: Podcasts 600/primary, Music 400/secondary, Music content dimmed .6, 3 px bar sweeping under the row · ③ data lands: old content at opacity .2 (mid-fade), bar gone · ④ new page entering: P1 at full, P2 at 60 % with 5 px rise, P3 at 20 % (the 30 ms stagger caught mid-way) · ⑤ settled Podcasts · ⑥ scrolled: the 44 px band with the Segmented control + toggle, chapter "Episodes you might like" stuck at 44, and a variant strip at the right showing the failure InfoBar under the row |
| sticky note `facetflow` | — | w 420, blue | −480, 3320 | "FACET DATA FLOW — one home document per facet id (`""`, music-chip, podcasts-chip, audiobooks-chip, *-following-chip); switch = keep old page, dim .6, 3 px ProgressBar under the control, swap on arrival (83 ms out / 250 ms in, 30 ms zone stagger), InfoBar + revert on failure; cached (≤10 min) = instant swap; stale = instant + silent refresh (bar, no dim); skeleton only on the first-ever load; prefetch on 150 ms hover / focus; Following = the sub-chip id as the facet; per-tab state, history entry per switch, scroll per facet" |

Notes on the boards:
- Keep every artboard's root fixed-size and the `data-dc-script` block; the facet row can be **interactive** on
  `Podcasts.dc.html` only if it truly works (a `pick` handler that swaps the `.sel` class and toggles the Following
  button's visibility is enough; do not fake page loads).
- Chapter header vocabulary stays: Subtitle 20/28 600 title, optional 14/20 secondary caption ("From shows you
  follow"), right side `See all` link + `‹ ›` pager with pips.
- Episode row: `height:76px; padding:0 8px; gap:12px; border-radius:4px;` art `56×56, radius 4`; title
  `14/20/600`, 2-line clamp; caption `12/16` TextSecondary with ` · ` separators, the ▶ 12 px inline SVG before the
  duration, the `E` badge before the show name; hover backplate `SubtleFillColorSecondary`; a 32 px round accent
  play button centred on the art at hover (opacity 0→1, 83 ms).
- Progress hairline (in-progress rows only): `height:2px; border-radius:1px;` track `rgba(255,255,255,.0837)`,
  fill accent, width = text column, 6 px below the caption; fill width = max(2 px, fraction).
- Video tile: `.wide` 317×178 with the video preview image (object-fit cover), no overlays on the image; title
  2 lines + caption below. Lead card: same at 428×241.
- Show square: `.gitem.w150`, cover 150 radius 4, title 1 line 600, publisher caption 1 line.
- Group card: `.gcard` 428 wide; rows `height:56px` (art 40); footer strip: `display:flex; gap:4px;` six 28 px
  thumbs radius 4, then a `.linkbtn` "See all 10".

### 5.3 Assets the orchestrator uploads (all from podcasts.json; covers are the 300 px `i.scdn.co` sources, video
thumbs the 1280×720 previews; use them verbatim as `/_blob/<id>` once uploaded)

Shows (150 squares / footer thumbs):

| id | show | publisher | cover |
|---|---|---|---|
| S01 | Patrick Boyle On Finance | Patrick Boyle | https://i.scdn.co/image/ab67656300005f1f29e23fd252d2a44ce26ee2e8 |
| S02 | The Brave Technologist | Brave | https://i.scdn.co/image/ab67656300005f1f3894f3993d88d77b0ab2ab72 |
| S03 | SOLVED with Mark Manson | Mark Manson | https://i.scdn.co/image/ab67656300005f1f9704ef03ddbb7afd063aadd4 |
| S04 | The Pog State | Riot Games Korea | https://i.scdn.co/image/ab67656300005f1f864fd84095ff6748d3255888 |
| S05 | Huberman Lab | Scicomm Media | https://i.scdn.co/image/ab67656300005f1f66aed32f8066a72781b3b12a |
| S06 | ColdFusion | ColdFusion | https://i.scdn.co/image/ab67656300005f1f0d29583a640a59b2feebcce0 |
| S07 | Hey Tablo | Team Epikase | https://i.scdn.co/image/ab67656300005f1f708c00022d442acd50618477 |
| S08 | The Checkup with Doctor Mike | DM Operations Inc. | https://i.scdn.co/image/ab67656300005f1f7e62b8e8c1b116815ba8b3c2 |
| S09 | Unhedged | Financial Times | https://i.scdn.co/image/ab67656300005f1feae35ff147f4fb50b6a21361 |
| S10 | The Economics Show | Financial Times | https://i.scdn.co/image/ab67656300005f1fddefc5f5030fb19bb0428460 |
| S11 | How Money Works | How Money Works | https://i.scdn.co/image/ab67656300005f1f503ed4894e3003692b3dcb54 |
| S12 | The Real Eisman Playbook | Steve Eisman | https://i.scdn.co/image/ab67656300005f1f6f94fe8c28fab3d888643801 |
| S13 | The Plain Bagel | The Plain Bagel | https://i.scdn.co/image/ab67656300005f1f6cf60b701b75655078de7a6d |
| S14 | Money & Macro | Money & Macro Media | https://i.scdn.co/image/ab67656300005f1f0d9c82759529f1fd3f4dd038 |
| S15 | Fin vs History | Fin Taylor & Horatio Gould | https://i.scdn.co/image/ab67656300005f1f4834f80901f51ce263235153 |
| S16 | The Rest Is Classified | Goalhanger | https://i.scdn.co/image/ab67656300005f1f8f5db8220819e63b2bc8660c |
| S17 | Mentour Pilot | Mentour Pilot | https://i.scdn.co/image/ab67656300005f1fa670c28cd67ad837cf81fdf7 |
| S18 | The Rest Is Politics: US | Goalhanger | https://i.scdn.co/image/ab67656300005f1ff6a2d7c602aa252b2970ba40 |
| S19 | From First Principles | Krishna Choudhary and Lester Nare | https://i.scdn.co/image/ab67656300005f1fc719208329cb29221d2e0a36 |
| S20 | Destiny VoD Archive | Destiny | https://i.scdn.co/image/ab67656300005f1f3ec737813088c1b1bc94956e |
| S21 | The Philip DeFranco Show | philip defranco | https://i.scdn.co/image/ab67656300005f1f17f4a186b5dc0746f82faa3d |
| S22 | Modern Wisdom | Chris Williamson | https://i.scdn.co/image/ab67656300005f1fc27a8d722b0ba5ca0f788f35 |
| S23 | Therapy in a Nutshell | Therapy in a Nutshell - Emma McAdam | https://i.scdn.co/image/ab67656300005f1fe2a729d29b76cc8aa002b34e |
| S24 | The Psychology of Money with Morgan Housel | Morgan Housel | https://i.scdn.co/image/ab67656300005f1f54b46478969078f1a3d72e72 |
| S25 | Philosophies for Life | Philosophies for Life | https://i.scdn.co/image/ab67656300005f1f8b486c01d3eb444ad36a183d |
| S26 | Help Me Be Me | Cloud10 | https://i.scdn.co/image/ab67656300005f1ffe2cbef1d7209752263d41df |
| S27 | How to Be a Better Human | TED | https://i.scdn.co/image/ab67656300005f1f0cb9f8c550bbadcdf5617bd2 |
| S28 | The Psychology Podcast | Scott Barry Kaufman | https://i.scdn.co/image/ab67656300005f1f2c960864eb3f90024074b93b |
| S29 | A Bit of Optimism | The Optimism Company from Simon Sinek | https://i.scdn.co/image/ab67656300005f1f886f282ed75375faff7e9cb4 |
| S30 | Ideas To Thrive | Ideas To Thrive | https://i.scdn.co/image/ab67656300005f1f3b7d5aae03895b50e8746d09 |

Episodes (row art = `cover`; lead/tiles = `video`):

| id | zone | episode | show | duration | date (ISO) | cover | video (16:9) |
|---|---|---|---|---|---|---|---|
| E01 | P1 lead | The Fund Structure That Could Replace the ETF | The Brave Technologist | 27 min | 2026-09-23 | https://i.scdn.co/image/ab67656300005f1f3894f3993d88d77b0ab2ab72 | https://image-cdn-ak.spotifycdn.com/image/ab6772ab000030aecbca9c085c9f3514bc8ffbd4 |
| E02 | P1 | Hot Take: A Great Movie Trilogy Must Have ONE FLOP \| Hey Tablo Ep.39 | Hey Tablo | 48 min | 2026-09-22 | https://i.scdn.co/image/ab67656300005f1f708c00022d442acd50618477 | — (audio) |
| E03 | P1 | The Doomsday Cult Inside OpenAI | Patrick Boyle On Finance | 31 min | 2026-09-20 | https://i.scdn.co/image/ab67656300005f1f29e23fd252d2a44ce26ee2e8 | https://image-cdn-fa.spotifycdn.com/image/ab6772ab000030ae623a6b8a88481252dd1d3a75 |
| E04 | P1 | Essentials: How to Assess & Improve All Aspects of Your Fitness \| Dr. Andy Galpin | Huberman Lab | 35 min | 2026-09-17 | https://i.scdn.co/image/ab67656300005f1fb94a3c63aa492f7ce7316bb3 | https://image-cdn-fa.spotifycdn.com/image/ab6772ab000030ae886036b9200d0406b3aea09d |
| E05 | P1 | John Park is like Tukutz but way SMARTER \| Hey Tablo Ep. 38 | Hey Tablo | 1 h 1 min | 2026-09-15 | https://i.scdn.co/image/ab67656300005f1f708c00022d442acd50618477 | — (audio) |
| E06 | P2 (in progress 8 334 ms) | Scott Bessent Is at War With Prices — and Prices Are Winning! | Patrick Boyle On Finance | 44 min | 2026-08-30 | https://i.scdn.co/image/ab67656300005f1f29e23fd252d2a44ce26ee2e8 | https://image-cdn-fa.spotifycdn.com/image/ab6772ab000030ae7ef0ff0549454b8d71e41850 |
| E07 | P2 | The Ancient Medicine of Uncontacted Amazon Tribes \| Paul Rosolie | The Checkup with Doctor Mike | 3 h 9 min | 2026-09-02 | https://i.scdn.co/image/ab67656300005f1fd3a908e247cfafe758bbd904 | https://image-cdn-fa.spotifycdn.com/image/ab6772ab000030ae206c687288db805467cd7276 |
| E08 | P2 | Hey, Whatever Happened to NFTs? | ColdFusion | 17 min | 2026-04-20 | https://i.scdn.co/image/ab67656300005f1f0d29583a640a59b2feebcce0 | https://image-cdn-ak.spotifycdn.com/image/ab6772ab000030ae866b759d2ed0b0ca0d9cbecd |
| E09 | P4 | How Long Can The Stock Market Ignore Reality? | How Money Works | 15 min | 2026-07-30 | https://i.scdn.co/image/ab67656300005f1f503ed4894e3003692b3dcb54 | https://image-cdn-ak.spotifycdn.com/image/ab6772ab000030aedc0de2651ae660f74d16926f |
| E10 | P4 | Intelligence Scoop: Is Russia Preparing to Attack Europe? | The Rest Is Classified | 53 min | 2026-09-23 | https://i.scdn.co/image/ab67656300005f1f8c4dbe89bf79eebd6995ea8a | https://image-cdn-ak.spotifycdn.com/image/ab6772ab000030aeb011428a7ae962e407aee2f8 |
| E11 | P4 | The Bipartisan Who Split America | Destiny | 53 min | 2026-09-23 | https://i.scdn.co/image/ab67656300005f1f4db5c6372aeb7ec209a3b8f7 | https://image-cdn-fa.spotifycdn.com/image/ab6772ab000030ae9362c9df78cafac08a9bff2e |
| E12 | P4 | What Chinese rule will actually look like \| Dr. Dave Kang | Money & Macro Talks | 47 min | 2026-09-10 | https://i.scdn.co/image/ab67656300005f1fb0c7e74cd9bdfd71e77d894e | https://image-cdn-fa.spotifycdn.com/image/ab6772ab000030aef791a753b188b8f57cbb86c5 |
| E13 | P5 (E) | How to Overcome Anxiety, Solved | SOLVED with Mark Manson | 2 h 10 min | 2026-05-06 | https://i.scdn.co/image/ab67656300005f1fc999edb8c6098bdcbbfce13f | https://image-cdn-fa.spotifycdn.com/image/ab6772ab000030ae27404ed6f3872f268a50f718 |
| E14 | P5 | How To Organize Your Life Like The Top 1% | Chamath Palihapitiya | 8 min | 2026-09-10 | https://i.scdn.co/image/ab67656300005f1fbe4d4f695beba55700d4f270 | https://image-cdn-fa.spotifycdn.com/image/ab6772ab000030ae6cd7fc6d0a6fe8419de6e22f |
| E15 | P5 (E) | How to Change Your Life, Solved | SOLVED with Mark Manson | 4 h 58 min | 2026-06-03 | https://i.scdn.co/image/ab67656300005f1f6084f8d2cca3a0601f2c956d | https://image-cdn-ak.spotifycdn.com/image/ab6772ab000030ae74a54b1777becfe15b99572a |
| E16 | P5 | How to Improve Motivation & Overcome Procrastination \| Dr. Masud Husain | Huberman Lab | 2 h 20 min | 2026-08-24 | https://i.scdn.co/image/ab67656300005f1f66aed32f8066a72781b3b12a | https://image-cdn-fa.spotifycdn.com/image/ab6772ab000030ae760346c92faa9cbc0dfa0bf7 |
| E17 | P5 | The Shocking Lessons He Teaches His Harvard Students \| Arthur Brooks | The Checkup with Doctor Mike | 1 h 59 min | 2026-01-25 | https://i.scdn.co/image/ab67656300005f1f376cd43e9cfebcd418092e5d | https://image-cdn-fa.spotifycdn.com/image/ab6772ab000030aebb1bcc7e49d90bd6e1626766 |
| E18 | P5 | Young people say their lives feel fake. Here's why \| Arthur Brooks | The Big Think Interview | 1 h 53 min | 2026-08-07 | https://i.scdn.co/image/ab67656300005f1f4cfcac9599161e188d3c50d2 | https://image-cdn-ak.spotifycdn.com/image/ab6772ab000030aefb34f5f119e37b9ae2ec883c |
| E19 | P6 card 1 | The OpenAI IPO … It's Worse Than You Think | MonkeyExplains | 15 min | 2026-09-05 | https://i.scdn.co/image/ab67656300005f1f64b403e98e3543365c3f0770 | (not needed) |
| E20 | P6 card 1 | Stock Picking in Difficult Times with George Noble \| The Real Eisman Playbook Ep 76 | The Real Eisman Playbook | 54 min | 2026-09-21 | https://i.scdn.co/image/ab67656300005f1f6f94fe8c28fab3d888643801 | — |
| E21 | P6 card 1 | Why China Built the World's Largest Air Battery | Undecided with Matt Ferrell | 17 min | 2026-09-15 | https://i.scdn.co/image/ab67656300005f1f33df960031d6d172ba866e09 | (not needed) |
| E22 | P6 card 1 | The Worlds Most Complicated Video Game Has A Wealth Inequality Problem | Micro | 24 min | 2026-09-15 | https://i.scdn.co/image/ab67656300005f1fb99ea39cf28950e55b7ccbec | (not needed) |
| E23 | P6 card 2 | Why The Nepal Flood Was Way Scarier Than You Think | RealLifeLore | 27 min | 2026-09-23 | https://i.scdn.co/image/ab67656300005f1f47f63fab00a2f5b97d28a89d | (not needed) |
| E24 | P6 card 2 | The most expensive 33 hours in WordPress history... | Fireship | 5 min | 2026-09-24 | https://i.scdn.co/image/ab67656300005f1f453284a53565b38d460e6b55 | (not needed) |
| E25 | P6 card 2 | Dornier Do X The Largest, Heaviest, and Worst Flying Boat | Megaprojects | 19 min | 2026-09-23 | https://i.scdn.co/image/ab67656300005f1fb24d77977350388e2fd49df6 | (not needed) |
| E26 | P6 card 2 | Why California's Most Famous Ghost Town Still Exists? | Places | 28 min | 2026-09-23 | https://i.scdn.co/image/ab67656300005f1f1f736b4356a403e416849b29 | (not needed) |
| E27 | P6 card 3 | Imperial Collapse Expert: The Real Reason Empires Fall - And Why America Is Next | Decoding Geopolitics Podcast with Dominik Presl | 39 min | 2026-09-19 | https://i.scdn.co/image/ab67656300005f1ff12749883917026520881a5f | (not needed) |
| E28 | P6 card 3 | Houthi blitz threatens Suez Canal as Saudi Arabia turns to UK for help | Iran: The Latest | 33 min | 2026-09-15 | https://i.scdn.co/image/ab67656300005f1f15081d01e00953f41a1392ca | — |
| E29 | P6 card 3 | Should markets discount the AI apocalypse? | Unhedged | 21 min | 2026-09-15 | https://i.scdn.co/image/ab67656300005f1feae35ff147f4fb50b6a21361 | — |
| E30 | P6 card 3 | Putin 'knocking on doors of NATO' with near-miss strike on train of European officials | Ukraine: The Latest | 50 min | 2026-09-14 | https://i.scdn.co/image/ab67656300005f1fc244178a8bb675edf669d227 | — |

Total: 30 show covers + 30 episode covers + 16 video thumbs (E01, E03, E04, E06–E18; E02/E05 are audio-only). Explicit (E badge): E13,
E15 (episodes); S03, S05, S07, S08, S22, S26–S29 (shows: `contentRatingV2.labels = [EXPLICIT]`), shown on show
squares as the same 14 px badge before the publisher caption.

### 5.4 Music board items

All Music items already exist in `items.json` / `blobmap.txt` from the All capture except: Avril Lavigne Radio,
00s Pop Rock, Songs We Rocked Out To, All Out 2000s, Breakaway, The Kids In The Crowd, No Pads No Helmets, Kelly
Clarkson, P!nk, Simple Plan, and the Soundtrack-afternoon mixes (Fun Road Trip, Hopeless Romantic Soft, Workout
Pop, Chill Moody, Breakup, Soft, Hopeless Romantic Love, Wedding, Happy Walking). Pull their `coverArt.sources`
(300 px) from `home_filtered.json` sections [4] and [6] with the existing `imgs.py`.

---

## Summary

1. **Control**: a Zune-souled *page-title pivot* — "All  Music  Podcasts  Audiobooks" in Title 28/36, selected
   Semibold/primary, others Regular/secondary, no underline, no boxes, 40 px visual gaps, 24 px under the surface
   top. Sentence case and Fluent weights (no lowercase/Light: web-isms per `05`).
2. It is the page heading, so it never competes with the title bar's underlined "Home" tab (document vs. content).
3. **Following** = a subtle AppBarToggleButton-style toggle at the row's trailing edge, only for chips with
   `subChips` (Music, Podcasts); on-state = subtle fill + accent glyph; per-facet memory; toggling is a facet switch.
4. **Scrolled**: past 64 px the row scrolls away and a 44 px sticky band with a WinUI **Segmented** control (raised
   selected item) + the compact toggle fades in; chapters stick under it at 44.
5. **Switch motion (owner's rule)**: keep the old page, dim to .6 over 167 ms + no hit-testing, pin a **3 px
   indeterminate accent ProgressBar** (engine `ProgressBar.Indeterminate`) under the row/band; on arrival 83 ms
   out, 250 ms in with 30 ms zone stagger; cached ≤10 min = instant; stale = instant + silent refresh (bar only);
   failure = revert + InfoBar with Retry; skeleton only on the first-ever load.
6. **Keyboard/SR**: one tablist stop, arrows move focus, Enter selects (selection never follows focus), palette
   commands instead of Ctrl+digits, `aria-busy` while in flight, "{Facet} home" announced on publish.
7. **State**: per Home tab for its lifetime; history entry per switch; scroll per facet; launch always on All;
   deep link `wavee://open?route=home&arg=<chip-id>`.
8. **Podcasts page** (real data): New episodes (lead 428×241 + 4 rows) → Continue listening (3 row items with a
   progress hairline) → Your shows (8 squares) → Videos (4 × 16:9) → Episodes you might like (6 rows) → three
   "Because you listen to…" group cards (Patrick Boyle with a shows footer, ColdFusion, Why Germany Stopped
   Working) → Shows you might like → Popular with listeners of SOLVED with Mark Manson.
9. **Regroup rules**: 21 one-item baseline sections fold by title phrase (New episode from / Catch up / Popular
   with listeners of X / Similar / Videos / Featuring); in-progress episodes always go to Continue; page-wide URI
   dedupe; groups < 3 fold into "Episodes you might like"; nothing invented (no unplayed counts, no categories).
10. **Mockup**: replace `.pivot-row` on Main/Light, add `Podcasts.dc.html` (1440×2700), `Music.dc.html`
    (1440×1520), `FacetSwitch.dc.html` (6 frames incl. the dimmed-old-page + bar frame and the sticky band), turn
    States panel 3 into the Audiobooks starter, extend the component sheet; 74 assets listed with URLs.
