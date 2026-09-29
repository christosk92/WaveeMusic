# Wavee Home — Design Brief (Agent 3 of 4: Art Direction)

Sources: `01-user-value.md`, `02-ui-patterns.md`, `home_digest.txt` (31 raw sections), `items.json`,
`recents.json` (extracted this pass from `home_new.json` §3 `Recents`), light skim of `Home.UI.cs` /
`Home.Cards.UI.cs` for existing token/component names (`Tok.*`, `Radii.*`, `Spacing.*`, `HomeModules`,
`CardPhysics`). All content below is real — pulled by exact name from the captured response, nothing invented.

---

## 1. Design thesis

Wavee's Home is the five-second decision that gets you from "I opened the app" to "music is playing" — and for
a desktop app with a persistent sidebar and now-playing bar already covering "my library" and "what's on,"
Home's only remaining job is to *do the deciding for you*. The page opens with your literal history (Recents)
and one unmissable "just press play" pick, because most sessions are resume sessions, not discovery sessions;
everything below that is a graceful decay from certainty (mixes made for you) to serendipity (Explore), never
the reverse. The feeling: **a record shop clerk who already knows you, not a search engine that makes you
choose from 18 identical shelves.** Visually this is Zune's editorial nerve — huge lowercase type, one accent,
content-first, no card-clutter — poured into WinUI 3 / Fluent 2's actual material (true Mica, the 4px grid, the
existing per-shape card skins Wavee already ships). We are not redecorating Spotify's flat-grey web app, and
we are not building a Zune cosplay; we are pushing Wavee's *already-Fluent* shell toward more typographic
confidence and a much steeper information hierarchy than it has today (31 flat shelves → 7 zones of
decreasing certainty).

---

## 2. The page, top to bottom (1440×900 window; title bar 48, sidebar 280 already exists, now-playing bar 88
already exists — both out of scope, drawn only as context)

Content column width: 1440 − 280 (sidebar) = 1160px. Page horizontal margin: `Spacing.PageWide` = 24px each
side per existing token → content area 1112px wide.

### Zone 0 — Greeting + facet strip (sticky-feeling header, not literally pinned)

- **Job:** orient (time, who), and let Music/Podcasts/Audiobooks filter the whole page — unchanged concept
  from today, per agent 1 §"Facet strip...stays as global page filter."
- **Pattern:** Zune Quickplay Header (agent 2 §3.1) fused with WinUI SelectorBar (agent 2 §3.10).
- **Spec:** Greeting set in `--font-display` ("Segoe UI Variable Display") at 56px / line-height 0.95 / weight
  200 / letter-spacing −0.01em / lowercase literal text as returned (`"Good morning"` — keep source casing,
  don't force-lowercase since the server owns capitalization and forcing it would look like a bug, not a
  choice — deviate from pure Zune here deliberately). Directly below, Caption line 14px/Body, `Tok.TextTertiary`,
  60% opacity: the day/date, computed client-side (`"Friday · ready when you are"` — reuse agent 2's exact
  copy, it's good and matches "It's New Music Friday!" being live today). 16px gap, then the SelectorBar: three
  labels **Music / Podcasts / Audiobooks** (verbatim `homeChips` labels), Body Strong 14/20, 2px accent
  underline sliding ~200ms, no fill ever. Total zone height ~132px including 24px top/bottom breathing room.
- **Exact copy:** `"Good morning"` (from `greeting.transformedLabel`, verbatim) / `"Friday · ready when you
  are"` (derived, day-of-week aware per agent 1's Friday/Monday edge state).
- **States:** SelectorBar tab hover = `Tok.TextTertiary → TextSecondary` opacity step, no background. Active
  tab = `Tok.TextPrimary` + underline. Focus = 2px accent outline offset 2px around the label, never the whole
  bar.

```
┌────────────────────────────────────────────────────────────────────────┐
│  Good morning                                                          │
│  Friday · ready when you are                                           │
│                                                                          │
│  Music        Podcasts        Audiobooks                               │
│  ▔▔▔▔▔                                                                  │
└────────────────────────────────────────────────────────────────────────┘
```

### Zone 1 — Continue listening (Resume job — answers "resume" instantly, above the fold)

- **Job:** the #1 job per agent 1 (highest frequency, near-zero decision cost). Must be visible with zero
  scroll.
- **Pattern:** History Strip (agent 2 §3.3) — the "anti-shelf" — NOT a card shelf, deliberately lighter than
  everything below it for rhythmic contrast, per agent 2's own rule ("for rhythm contrast").
- **Exact items (8, in Recents order — order is the only recency signal available, per agent 1; no
  `playedAt` field exists in the payload):**
  1. Kris Kross Amsterdam (Artist)
  2. Liked Songs (Playlist, `spotify:collection:tracks`)
  3. Rich Brian (Artist)
  4. WLUWD — Tristam (Album)
  5. scream teen pop friday morning (Playlist)
  6. Arash (Artist)
  7. Arcane League of Legends: Season 2 (Soundtrack) (Album)
  8. 90's Nederlandstalig (Playlist)
  (12 more exist in `recents.json`; cap the strip at 8 so it reads as one glance, not a second shelf — desktop
  width fits 8 comfortably at the tile size below with room for the edge fade.)
- **Spec:** 64px square tiles, `Radii.Card` (8px) art crop — **except** Artist entries, which render circular
  (Artist Circles pattern, agent 2 §3.7, at the 40px dense-row size scaled to 64px here for strip parity) so an
  artist reads as a person, not a mistaken album. No title text on the tile. 1px accent ring only on the item
  currently playing (none in this snapshot). 8px gaps (tighter than the 12px shelf gap below — the visual
  signal that this is "the anti-shelf"). One caption line above the strip: Caption 12/16, `Tok.TextTertiary`,
  reads **"Recently played"**. Edge-fade mask at 24px per agent 2 §5. Zone height: 24 (caption+gap) + 64
  (tiles) + 24 (bottom margin) = 112px.
- **Interaction:** hover = tile scales to 1.04 with `CardPhysics` lift −4dp (reuse verbatim — the strip is
  small so the lift must stay subtle or it reads jumpy at this size; clamp lift to −2dp here, note as a
  redline for agent 4), a centered 28px play glyph fades in per agent 2 §3.11 (Hover-to-Play). Click = resumes
  playback for Playlist/Album, opens artist page for Artist entries (no direct "resume" verb for a person).
- **Why-copy:** none — recency itself is the reason, per agent 1.

```
Recently played
┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐
│○ ││▢ ││○ ││▢ ││▢ ││○ ││▢ ││▢ │  ○=circular artist ▢=square art
└──┘└──┘└──┘└──┘└──┘└──┘└──┘└──┘···(edge fade)
```

### Zone 2 — Resume Hero + Made for you right now (the "press play" answer — still above the fold at 1440×900)

This is the single most important design decision in the brief: **merge the Resume Hero Tile concept with the
"best bet right now" pick**, because the raw response gives ~18 "just play" candidates (6 Daily Mixes + DJ +
Discover Weekly + 10 top mixes) with *no ranking signal* (agent 1, confirmed: no play-count, no
last-mix-played field). Rather than inventing a fake ranking, the hero promotes the **first item of `Made For
Chris`** (`DJ`, since it's positioned first server-side and is Spotify's own "no decision needed" surface — an
AI DJ literally exists to remove the choice), and the row beside/below it holds the rest of `Made For Chris` +
`Soundtrack your Friday morning` merged into one row, per agent 1's "job 2 & 4 merge" recommendation.

- **Layout:** two-column, left 40% hero / right 60% a 2-row-tall dense card row (mirrors agent 2's Resume Hero
  Tile sizing guidance almost exactly, repurposed for "best bet" instead of literal resume-position).
- **Left — Hero card ("DJ"):**
  - Pattern: Resume Hero Tile (agent 2 §3.2), art = DJ's cover, 4:3 crop, gradient scrim
    `linear-gradient(180deg, transparent 40%, rgba(0,0,0,.55) 100%)` dark / lighter in light theme, `Radii.Card`
    8px.
  - Title: Title Large 40/52 semibold, over the scrim, bottom-left: **"DJ"**.
  - Sub-line: Body 14/20, `Tok.TextSecondary` (on scrim: near-white at reduced opacity): **"All kinds of
    music, picked by your own AI DJ."** (verbatim playlist description).
  - CTA: SplitButton bottom-right, **"Play"** primary / chevron reveals **"Shuffle"** — reuses agent 2's
    SplitButton recommendation exactly.
  - Hover: `CardPhysics` −4dp lift + shadow per §1 spec (`0 8px 16px rgba(0,0,0,.14)`), press 0.99 scale.
- **Right — "Made for you right now" row (2×3 grid, 6 cards):**
  - Header: Subtitle 20/28 semibold: **"Made for you right now"**. Why-caption (Caption/Tertiary, pattern §3.5
    from agent 2): **"Your daily mixes, refreshed today."**
  - Items (6, in server order, DJ excluded since it's the hero): Daily Mix 1, Daily Mix 3, Daily Mix 4,
    Discover Weekly, Chill Sad Mix, Feel Good Happy Mix — i.e. 4 from `Made For Chris` + 2 highest-signal picks
    from `Soundtrack your Friday morning` (Chill Sad and Feel Good Happy, chosen as the two clearest opposite-
    mood anchors so the row doesn't read as "9 near-identical mood mixes," a curation call this brief is making
    explicitly so agent 4 doesn't have to invent one).
  - Card pattern: WinUI Chevron Shelf card skin (agent 2 §3.4) at grid density (no chevrons needed since it's a
    fixed 2×3, not a scrolling shelf) — mix numeral badge per Wavee's existing "mix has a numeral on a shared
    plate" skin (per agent 2 §0) for Daily Mix N specifically; plain card for Discover Weekly / the two mood
    mixes.
  - Why-copy per card (Caption 12/16, `Tok.TextTertiary`, 1 line, ellipsis) — pulled verbatim from each
    playlist's own `description` HTML, stripped to plain text, per agent 1's explicit reuse recommendation:
    - Daily Mix 1: *"IVE, Red Velvet, IU and more"*
    - Daily Mix 3: *"Dynamicduo, LEE MU JIN, Jung Seung Hwan and more"*
    - Daily Mix 4: *"Henry Moodie, vaultboy, elijah woods and more"*
    - Discover Weekly: *"Your shortcut to hidden gems, deep cuts, and future faves."*
    - Chill Sad Mix: *"Chill Sad music for you."*
    - Feel Good Happy Mix: *"Feel Good Happy music for you."*
- **Zone height:** hero ~420px tall (Title Large needs headroom), right grid matches at 420px (3 rows × ~130px
  incl. gaps). This is the tallest zone on the page by design — it's the page's center of gravity.

```
┌───────────────────────────────┐  ┌────────┐┌────────┐┌────────┐
│                                 │  │ Daily 1││ Daily 3││ Daily 4│
│         DJ                     │  │ IVE... ││ Dynam..││ Henry..│
│  All kinds of music, picked... │  └────────┘└────────┘└────────┘
│                        [Play▾] │  ┌────────┐┌────────┐┌────────┐
└───────────────────────────────┘  │Discover││Chill Sad││Feel Good│
   Made for you right now →        │ Weekly ││   Mix   ││  Happy  │
                                    └────────┘└────────┘└────────┘
```

*(Above-the-fold boundary sits roughly here at 1440×900 — zones 0–2 total ≈ 132+112+420+headers ≈ 700–750px,
leaving Zone 3's header peeking in. Both "resume" and "play something now" are answered without scrolling.)*

### Zone 3 — New for you this week (Catch-up job)

- **Job:** weekly ritual, Friday-dated in this snapshot per agent 1.
- **Pattern:** WinUI Chevron Shelf (agent 2 §3.4), header takes the *server's own* Friday-aware title.
- **Header:** **"It's New Music Friday!"** (verbatim `transformedLabel`, reused as the zone title exactly
  because agent 1 flagged the server already computes day-part copy — don't re-author it). Subtitle none.
- **Items (6, deduped):** Release Radar, Fresh Pop, Fresh Hits, All New Pop, This Is Frequency, the new alt.
  (Dedupe rule: `New Music Friday NL` dropped from this shelf because it's promoted standalone as item 1 below
  — see §3 cut rules; `All New K-Pop`, `New Dance Pop`, `Indie Update` cut for length — 6 cards fits one
  screen-width row without paging on a 1112px content area at the card width below.)
- **A leading "pinned" card, visually distinct (slightly wider, 1.3×):** **New Music Friday NL** — Playlist
  card, why-copy: *"Nieuw: Taylor Swift, Yade Lauren, Mula & Lijpe, Racoon, Benny Sings, Sam Feldt and more"*
  (verbatim, truncated to the description's own "and more"). This single card absorbs the duplicate that
  appeared twice in the raw response (§1 item 5 + §7 item 8) — see cut rules.
- **Card spec:** standard 8px-radius card, 176×176 art + 2 title lines + why-caption, `Spacing.M` (16px) gaps,
  `‹ ›` chevron pager top-right of the header row.
- **States:** chevron 32px circular hit target, `Tok.FillControlSecondary` hover fill, glyph 70%→100% opacity
  on hover; disabled state (start-of-row) = 30% opacity, no hover fill.

```
It's New Music Friday!                                        ‹ ›
┌─────────┐  ┌───┐┌───┐┌───┐┌───┐┌───┐┌───┐
│  NMF NL │  │RR ││FP ││FH ││ANP││TIF││TNA│
│(pinned) │  └───┘└───┘└───┘└───┘└───┘└───┘
└─────────┘
```

### Zone 4 — Because you like… (Rediscover job, regrouped baseline — the biggest structural fix)

- **Job:** turn the raw response's 20 dead one-item rows into one living, browsable zone. This directly
  implements agent 1 §3's clustering proposal.
- **Pattern:** a single Subtitle-headed zone containing **6 labeled sub-rows** (Dense Shortcut Grid skin per
  card, agent 2 §3.8, since each sub-row is short), each sub-row a horizontal mini-shelf of its own with a
  small Body-Strong 14/20 sub-label + `iconName`-driven glyph (agent 1 confirmed `iconName` present on all 20;
  render as a 16px icon to the left of the sub-label — reuse the icon vocabulary from §5).
- **Zone header:** **"Because you like…"** (Subtitle 20/28 semibold), why-caption: **"Picked from what you
  play most."**
- **Sub-rows, clustered by named entity exactly as agent 1 derived (7→6, folding two singleton "Made for you"
  entries together since they carry no distinguishing entity name):**
  1. **Troye Sivan** (3 items): "More like Troye Sivan" [dup collapsed to 1], "Pop 터지는 팝콘 한 입"
     [dedupe: appears twice in raw data at §9 item 8 and §25 — render once], *eternal sunshine* (Ariana
     Grande, from the "More like Troye Sivan" shelf, kept as the 3rd since baseline section [29] independently
     surfaced Sabrina Carpenter's *emails i can't send fwd:* — use that instead for variety): **Troye Sivan
     Radio, Pop 터지는 팝콘 한 입, emails i can't send fwd:**
  2. **ROSÉ** (2 items): ROSÉ Radio, romanticizing life
  3. **Henry Moodie** (2 items): sad hour, This Is Henry Moodie
  4. **Savage Garden** (2 items): Guilty Pleasures, This Is Savage Garden
  5. **Acda en de Munnik** (2 items): 90's Allerbeste, Hollandse Meezingers
  6. **Jung Seung Hwan** (2 items): This Is Jung Seung Hwan, Best of Korean OSTs
  (Folded out, per cut rules below: "90's Nederlandstalig" cluster (2 items, both "10's Nederlandstalig" +
  "This Is Suzan & Freek") and the 5 unlabeled "Made for you" singletons + "Based on your recent listening" —
  these 8 items merge into a 7th sub-row: **"More recommendations"**, since none carries a distinguishing
  entity name worth its own sub-header. Cap that sub-row at 5 cards: Charlie Puth Mix, Roy Kim Mix, 80s Mix,
  Giorgos Sabanis Mix, Ambient Relaxation.)
- **Card spec:** 56dp tall row cards (Wavee's tabular `Row` primitive per agent 2 §3.8), square 48px art, title
  1 line, no chevron. `Tok.FillSubtleSecondary` hover only, no lift (rows don't lift, cards do — per agent 2
  §0's own hover-vs-lift split).
- **Zone height:** 7 sub-rows × ~64px (label + one row of cards) + 16px inter-row gaps ≈ 560px — this is a tall
  zone but each sub-row scans in under a second; it replaces 20 full-width single-card rows (which would have
  been >2000px) with one ~560px zone. That ratio is the entire point of this redesign.

```
Because you like…                              Picked from what you play most
  ♪ Troye Sivan        [Radio][Pop 터지는][emails i can't send]
  ♪ ROSÉ                [ROSÉ Radio][romanticizing life]
  ♪ Henry Moodie         [sad hour][This Is Henry Moodie]
  ♪ Savage Garden        [Guilty Pleasures][This Is Savage Garden]
  ♪ Acda en de Munnik    [90's Allerbeste][Hollandse Meezingers]
  ♪ Jung Seung Hwan       [This Is Jung Seung Hwan][Best of Korean OSTs]
  ♪ More recommendations   [Charlie Puth Mix][Roy Kim Mix][80s Mix][Giorgos Sabanis][Ambient Relaxation]
```

### Zone 5 — Jump back in (Rediscover, curated — kept as-is per agent 1)

- **Pattern:** WinUI Chevron Shelf, standard card skin, mixed content types (per-shape skins: Album = square
  art, Artist = circle, Playlist = square art).
- **Header:** **"Jump back in"** (verbatim), no why-caption — title says it (per agent 1).
- **Items (8, server order, dedupe applied — see §3):** Arcane League of Legends: Season 2 (Soundtrack), Chill
  Mix, rosie (ROSÉ), Millennium K-Pop, Phil Collins (Artist), Imagine Dragons (Artist), Arash (Artist), Rich
  Brian (Artist). (`Sleep Music for Deep Sleeping` cut here — already shown in Zone 1's Recents strip and in
  the raw Shorts rail; keeping it a third time anywhere is exactly the redundancy agent 1 flagged.)

### Zone 6 — Radio & mixes for you (merged: Recommended Stations + Popular radio + leftover top mixes)

- **Job:** lean-back, lowest-decision-cost, demoted below mood/new/resume per agent 1's explicit instruction.
- **Pattern:** Radio Station Card — Stacked Artist Faces (agent 2 §3.6), the one shelf that gets the "3
  overlapping avatar" skin since every item here already carries a "With X, Y, Z and more" description.
- **Header:** **"Radio & mixes for you"**, why-caption: **"Based on artists and songs you play."**
- **Items (8, deduped/capped from 3 raw shelves totalling 90 available — genuinely the fold-and-demote agent 1
  called for):** Savage Garden Radio, Troye Sivan Radio *(cross-ref note: if Troye Sivan Radio was already used
  in Zone 4's "Because you like → Troye Sivan" sub-row, drop it here and promote vaultboy Radio instead — dedupe
  by URI across the whole page, not just within a zone)*, Dua Lipa Radio, Coldplay Radio, Phil Collins Mix
  *(top mix)*, Pop Mix *(top mix)*, K-Pop Mix *(top mix)*, Rex Orange County Radio.
- **Card spec:** round 64px station art, 2–3 overlapping 16px artist-face avatars bottom-right with a
  2px Mica-tinted ring, title Body Strong, "With X, Y, Z and more" Caption/Secondary. 8-wide row, `‹ ›` pager
  for the remainder (this is the one zone that legitimately has more content than fits, so paging matters more
  here than a hard cap — an 8-card first page plus pager, not a silent truncation).

### Zone 7 — Explore (lowest priority, still present — closes the page)

- **Pattern:** horizontally scrollable pill/chip rail, not a card grid, per agent 1's "glance-and-skip zone"
  framing and agent 2's Dense Shortcut Grid alternative use.
- **Header:** **"Explore"**, no why-caption (this zone is explicitly *not* personalized — honesty in copy
  matters here).
- **Items (9 — the Shorts rail with the broken entry dropped, per cut rule):** 90's Nederlandstalig, Daily Mix
  2, scream teen pop friday morning, WLUWD, Kris Kross Amsterdam, I loved you too much to just laugh it off,
  Q-top 1500 | editie 2025, Sleep Music for Deep Sleeping *(3rd appearance — cut here too per global dedupe;
  replace with "Helemaal NL" from the Officiële Spotify leftovers)*, Rich Brian.
- **Card spec:** small pill/chip, 40px art thumbnail + 1-line label, `Radii.Control` (4px, not 8 — this is
  chrome-tier per the radius rule, not card-tier), horizontal scroll, no chevron pager (Zune-style continuous
  drag/scroll instead, matching agent 2's "glance-and-skip" framing).
- **Footer margin:** 48px bottom padding before the now-playing bar's 88px band starts, so the last row never
  feels clipped.

---

## 3. What we cut / merge from the raw response (rules the real app could implement)

1. **Drop `UnknownType` items silently.** Any item where `content.__typename == "UnknownType"` (confirmed
   sample: `uri: "spotify:user:@:collection"`, a malformed Liked-Songs self-reference) is filtered out before
   layout — never rendered as a blank tile. Do not attempt to "fix" it into Liked Songs by URI-pattern guessing
   (agent 1 floated this as an option; reject it — Liked Songs is already correctly present via Recents item 2
   as `spotify:collection:tracks`, so silent-drop is strictly safer than a guessed substitution).
2. **Global URI dedupe, page-wide, not per-zone.** Build a `Set<Uri>` of every item placed in a zone, top to
   bottom in the order above; any later zone that would re-place the same URI skips it and promotes its next
   candidate instead. This mechanically resolves: New Music Friday NL (zones 3, dropped from its second
   appearance in "Officiële Spotify playlists"), Pop 터지는 팝콘 한 입 (zone 4, collapsed from its two section
   objects), Sleep Music for Deep Sleeping (kept once in Zone 1 Recents, dropped from Jump back in and Explore),
   Troye Sivan Radio (kept once, in whichever of Zone 4 / Zone 6 renders first).
3. **Fold `HomeFeedBaselineSectionData` sections by named entity in their own title.** Rule: strip the section
   title down to its entity token via the fixed phrase set the server actually uses — `"For fans of {X}"`,
   `"More like {X}"` → group key = `{X}`; titles with no entity token (`"Made for you"`, `"Based on your recent
   listening"`) all collapse into one catch-all group, label **"More recommendations."** Any group with ≥2
   items gets its own sub-row with `{X}` as the sub-label; groups of exactly 1 (there are none in this
   snapshot after clustering, but the rule must handle it) fold into "More recommendations" too, so no sub-row
   ever renders with fewer than 2 cards.
4. **Merge "Recommended Stations" + "Popular radio" into one "Radio & mixes for you" zone**, capped at 8 visible
   cards with a pager for the rest (110 combined available) — implements agent 1's explicit fold-and-demote
   instruction. Top mixes (`"Your top mixes"`) contribute their remaining, not-yet-shown items here too rather
   than getting a fourth standalone shelf, since a "mix" and a "radio" are the same *kind* of low-effort
   listening object to a user even if they're different playlist subtypes server-side.
5. **Consecutive-baseline-fold stays, generalized.** The existing engine behavior (agent 1 §"current code")
   that collapses a *run* of adjacent untitled baseline sections into one discover feed is subsumed by rule 3
   above — rule 3 replaces "consecutive only" with "same entity, anywhere in the response," which is strictly
   better and should replace the old rule rather than run alongside it.
6. **Facet emptiness is a first-class state, not an error.** When `Podcasts` or `Audiobooks` chip is active and
   its backing sections are empty (true today per agent 1's full-text-search confirmation), render the
   "Podcasts chip starter state" from Artboard C (§4) instead of an empty Zone 1–7 stack.

---

## 4. Artboards for agent 4

**(a) Home — Dark, full page, long scroll.** 1440px wide × ~2650px tall (all 8 zones stacked per §2, sidebar
and title bar drawn as static context chrome at their real widths/heights but visually de-emphasized — this
artboard is about Home's content, not the shell). This is the primary deliverable; every zone, every exact
item and copy string above must appear verbatim.

**(b) Home — Light, above-the-fold only.** 1440×900, zones 0–2 only (Greeting, Continue listening, Resume
Hero + Made for you right now). Proves the light-Mica token set and the light-mode card stroke rule (agent 2:
"light Mica needs the 1px stroke on cards that dark doesn't") on the two zones that most need to read
correctly at a glance — the ones answering "resume" and "play now."

**(c) States board.** 1440px wide, three stacked panels, each ~500px tall, dark theme only:
  1. **Loading skeleton** — Zone 1 (History Strip) + Zone 2 (Hero + grid) in skeleton form: reserved geometry
     (64px circles/squares for the strip, full hero box, 6 grid placeholders), shimmer or flat
     `Tok.FillSubtleTertiary` fill, no spinner (per agent 1's "no infinite loading state" edge-state rule).
  2. **First-run/empty state** — no Recents, no "Because you like" zone; page opens straight to Zone 3 (New
     for you) retitled generically ("New this week"), Zone 6 (Radio, genre-generic onboarding copy: "Popular
     right now"), Zone 7 (Explore) — Zones 1, 2, 4, 5 simply absent, not skeletoned, not empty-boxed.
  3. **Podcasts chip starter state** — Zone 0 with Podcasts tab active/underlined, page body replaced by a
     single centered light state: an icon, **"Follow shows to see them here"**, and a genre-pill starter shelf
     (reuse the Explore pill pattern) — never a blank white/Mica rectangle.

**(d) Component sheet.** 1440×1100, dark background, zoomed-in redline callouts (dimensions, corner radii,
color token names as labels, not just swatches) for exactly 5 components, each in resting + hover states side
by side: History Strip tile (square + circular variants), Resume Hero Tile (with SplitButton states: resting/
hover/pressed), Daily-Mix numeral card, Because-you-like row card, Radio stacked-avatar card. This is the sheet
agent 4 should treat as ground truth if artboards (a)/(b) and this sheet ever conflict on a pixel value.

---

## 5. Visual tokens

**Color — Dark:**
- Canvas fallback: `#202020`
- Mica wash: `radial-gradient(1200px 800px at 20% -10%, #2b3040 0%, transparent 60%), radial-gradient(1000px
  700px at 90% 10%, #3a2a30 0%, transparent 55%), #202020` + `feTurbulence` grain at 3% opacity, `overlay` blend
- Text primary / secondary / tertiary / disabled: `rgba(255,255,255,1.0)` / `rgba(255,255,255,0.786)` /
  `rgba(255,255,255,0.5442)` / `rgba(255,255,255,0.3628)`
- Card fill: `rgba(255,255,255,0.0512)`; card stroke: none at rest in dark (rely on fill contrast, per agent 2
  §1 — "no border in dark")
- Hover/press ramp (chromeless rows/buttons): `0%` → `6.05%` white → `4.19%` white
- Control stroke: `rgba(255,255,255,0.0837)`
- Accent: system accent, 100/90/80% tint ladder, never hardcoded — resolve via Fluent's own light-on-dark pass

**Color — Light:**
- Canvas fallback: `#F3F3F3`
- Mica wash: same technique, base `#F3F3F3`, gradient chroma ~8% (vs ~14% dark), grain opacity ~1.5%
- Text primary / secondary / tertiary / disabled: `rgba(0,0,0,0.8956)` / `rgba(0,0,0,0.6063)` /
  `rgba(0,0,0,0.4458)` / `rgba(0,0,0,0.3614)`
- Card fill: `rgba(255,255,255,0.70)`; card stroke **required** in light: `rgba(0,0,0,0.0578)` (per agent 2 §1
  and the anti-goals list — light Mica is too close in luminosity to a translucent white fill without it)
- Control stroke: `rgba(0,0,0,0.0578)`

**Mica CSS recipe (both themes):** fixed full-viewport `<div class="mica">` behind all content, gradients as
above, `backdrop-filter: blur(40px) saturate(130%)` only where content genuinely layers over detail (skip it
for a flat static mockup and apply the gradient+noise combo directly, per agent 2 §5).

**Type ramp:** `--font-display: "Segoe UI Variable Display", "Segoe UI Variable Text", "Segoe UI", system-ui,
sans-serif;` for headers/greeting; `--font-text` same stack for body. Sizes: Caption 12/16 Regular · Body 14/20
Regular · Body Strong 14/20 Semibold · Subtitle 20/28 Semibold (sub-row/section headers) · Title 28/36 Semibold
(reserved, unused this page — zone headers use Subtitle) · Title Large 40/52 Semibold (hero title only) ·
Display 68/92 Semibold reserved but **not used on Home** — the Quickplay greeting uses a custom 56/0.95/200
weight treatment per §2 Zone 0, intentionally lighter and smaller than the reserved Display size so it reads
as a greeting, not a page title.

**Spacing:** 4px base grid. Page margin `Spacing.PageWide` = 24px each side. Zone-to-zone vertical rhythm 40px.
Shelf card gap `Spacing.M` = 16px (History Strip uses a tighter 8px deliberately, see Zone 1). Sub-row gap
within Zone 4: 16px.

**Radii:** 8px cards/hero/flyouts (`Radii.Card`). 4px chips/pills/small controls (`Radii.Control`). Full/round
for artist avatars and station art. Never mix within one component.

**Shadows:** none at rest anywhere on Home (separation from fill/stroke only, per agent 2 §1). On hover/press
lift only: `0 8px 16px rgba(0,0,0,0.14)` dark, proportionally lighter alpha in light theme.

**Accent — where it may appear (justify):** Wavee's own rule (`Design.cs`, quoted by agent 2): *"Accent is
never STRUCTURE."* On this redesign, accent (system accent color, tinted per the ladder) appears in exactly
five places and nowhere else: (1) the SelectorBar's 2px underline, (2) the Resume Hero's SplitButton fill,
(3) a played-item's 1px ring in the History Strip, (4) focus outlines (2px, all interactive elements), (5) the
optional Zone 2/hero low-opacity (8–12%) radial wash behind header text only, from the lead item's extracted
cover color, per agent 2's "Context-Derived Color Wash" pattern. It never appears as a card border, a divider,
a chevron fill, or a background fill behind a whole shelf.

**Icon list (inline stroke SVG, 16/20/24px, `currentColor`, ~1.5px stroke unless noted filled):** play
(filled triangle, used only inside the 40px hover-to-play circle and the SplitButton), pause (two bars),
chevron-left / chevron-right (shelf pagers), shuffle, more (three dots, card overflow menu), verified-check
(filled, 12px badge for "Officiële Spotify" entities), music-note (the `iconName` stand-in glyph for Zone 4
sub-row labels — a generic note glyph since the digest didn't expose the actual `iconName` values), radio-tower
(Zone 6 header accent, optional), pin (unused this page, kept in the set per agent 2's inventory for
consistency with other pages).

---

## 6. Anti-goals checklist

- [ ] No flat `#121212`/pure-black background standing in for Mica — must be the gradient+noise recipe, both
      themes, both artboards where the shell is visible.
- [ ] No rainbow cards — a different saturated accent per card "for personality." One accent, five places only
      (§5).
- [ ] No card borders in dark theme at rest. No missing card borders in light theme (the inverse mistake is
      just as wrong).
- [ ] No drop shadows at rest — only on hover/press lift.
- [ ] No uniform square card for every content type — Artists render circular everywhere on this page
      (History Strip, Jump back in, Explore), radio stations get the stacked-avatar skin, mixes get the
      numeral-plate skin. "One card twelve times" is the exact failure this page exists to avoid.
- [ ] No InfoBar-style banner used for editorial/promo content — InfoBar is reserved for system messages only.
- [ ] No literal 20-row wall of single-card sections — Zone 4 must show the regrouped 6+1 sub-rows, never the
      raw 20.
- [ ] No blank tile for the `UnknownType` Shorts item — it must not appear at all.
- [ ] No fabricated "last played 2h ago" timestamp anywhere — no such field exists in the payload; the History
      Strip carries no relative-time label, only implicit order + the "Recently played" caption.
  - [ ] No fabricated release-date badges on "New for you this week" cards — `releaseDate` does not exist in
      the payload either; the zone's freshness signal is the section title and the day-of-week framing only.
- [ ] No animating-everything-at-once page load — only the greeting (fade+rise, once) and a capped, ~8-item
      staggered list entrance for the first visible zone; shelves below the fold should not all stagger-animate
      simultaneously on scroll-into-view.
- [ ] No hiding the "why" behind a tooltip-only affordance — every algorithmic zone/card that has a real reason
      shows it as visible Caption text, per the "Why" Caption Pattern.
- [ ] Empty Podcasts/Audiobooks facets must render Artboard (c) panel 3's starter state, never a blank page.
