# Artist page v2 — decisive design spec

## 1. Diagnosis: why v1 failed

- **The study varied the seam and held the defect constant.** All four variants are one hero — same photo, same scrim, same overlaid name, same floating card — differing only inside the ~40–96px boundary (`ContentBlendTail`). The user's objection is to the *composition*, so no variant could have answered it [C-D7, D-§2.1].
- **Text on an arbitrary photograph is unfixable, not untuned.** The stats row measures ~3:1 against the mid-blue field, under the 4.5:1 floor; `shelf-narrow.png` puts a 42px white name directly on a white sailor jacket. Four hand-tuned per-photo scrim stacks generalise to exactly one artist [A-V4, C-D8/D9]. And light theme can never reach the hero while the scrim exists — the stated north star is structurally unmeetable [D-§2.3].
- **There is no source asset for a full-bleed band.** Spotify's Web API caps artist images at 640×640 square; rendering that into 1440×420 is a 2.25× upscale that also discards ~70% of the frame. Only Spotify's *internal* 2660×1140 header supports the idiom [D-§1]. The prototype then reused that same photo ~9 times in one frame — avatar, 5 track covers, 2 release tiles — as a 78px zoom of the pixels directly behind it [C-D4].
- **The page is a poster, not a content page.** 438px hero in a 623px viewport = 70.3%; 2.2 of 10 track rows above the fold, Releases sliced mid-artwork by the player bar. On the real page the first track row lands at y≈690 of 900 [C-D1, D-§2.4].
- **It is not on the design system at all.** ~14 type sizes between 7.5px and 17px then a 3.4× chasm to 58px, none on the Fluent ramp, body text below the 12px floor; 8 radii; 4 non-standard weights; 5 nested rounded boxes; 7 accent objects of which one is a button; fake window chrome costing the 172px that caused the type collapse; and cards wrapped around *sections*, which is the exact inverse of the shipping rule "cards are for OBJECTS, never for SECTIONS" [C-D2/D12/D14/D15/D16, B-§D2].

**Conflicts resolved up front (with the report overruled):**

| Question | Ruling | Overruled |
|---|---|---|
| Page margin | **32px** (`Spacing.XXXL`, shipping `ArtistHeroLayout.PageGutter`) | **A** — its 56epx is the Photos/Settings silhouette; the 280px sidebar already buys that cohesion, and 56+56 would cost the ledger 48px of title measure [B-§B] |
| Card around Top tracks / Releases | **No wrapper.** Sections are dividers + headers; only objects get chrome | **A** — its "Card pattern" contradicts the shipping rule [B-§D2] |
| Card radius | **8** (`Radii.Card`) | **A**'s `ControlCornerRadius` 4 for cards; the app's declared ramp is 4/8/16 [B-§C] |
| Photo as a shorter bleed band | **No band. The photo is an object with edges.** | **C**'s must-change #2 — the 640² source cannot fill a band [D-§1] |
| Identity token size | **208×208**, not 88 | **D**'s Direction B — D itself names "reads as underdesigned" as the risk; 208 at 2× DPI = 416 device px, still zero upscale |
| Type sizes 11/13/15/16/22/18-line variants | **Six ramp steps only:** 40/52, 20/28, 18/24, 14/20, 12/16 | **D**'s 13/18, 15/22, 11/16 — off-ramp [A-§1a] |
| "▾ Show 5 more" on tracks 6–10 | **Show all 10.** Flat ledger | **D** — a collapse is the scroll barrier YT Music just removed, by D's own evidence |
| Section accent rule | **Keep** the 3×20 accent bar (shipping `AccentHeader`), kill the icon chip | **A-V6** on the rule; A/B/C all win on the chip |
| Floating shy pill vs sticky bar | **Full-width 48px sticky bar** | **B**'s shipping `ArtistShyPill` — a floating pill is the Spotify/YT idiom; a bar is the CommandBar reading |
| Sticky bar material | **Opaque** `FillSolidBase` + hairline, no acrylic | **B**'s acrylic pill — A-V5 is a hard no on non-transient acrylic |
| Palette | **Warm shipping preset** (`#FAF9F6` / `#E9E7E2` / `#005FB8`), not the prototype's cool blue-grey | the prototype's own `.wavee-app` light theme [B-§C] |

---

## 2. The recommendation — **MARQUEE PLATE**

**Thesis:** the artist photograph becomes a single sharp framed object rendered 1:1 from its square source, the identity text moves onto the content layer beside it, and the extracted palette colour survives only as a 6% wash behind the band and the one accent-filled Play button — so nothing is ever set over photography, light theme works by construction, and the catalogue starts 320px higher up the page.

### 2.1 Wireframe — 1440 × 900, light theme, inside the real shell

```
1440 × 900 · light · sidebar 280 (Classic mid tier, ≥1400) · pane 1160 · content 1096
┌────────────────────────────────────────────────────────────────────────────────────────┐
│  ‹  ›     [ ⌕  Search music                        Ctrl K ]                     (CK)   │ 48  titlebar / Mica
├──────────────┬─────────────────────────────────────────────────────────────────────────┤
│ SIDEBAR 280  │▓▓ WASH  palette-derived, α.06, pane-wide 1160, FLAT — no gradient tail ▓▓│
│              │◀ 32 ▶                                                           ◀ 32 ▶  │
│ ⌂  Home      │                                                                         │ 24
│ ⌕  Search    │   ┌───────────────┐   ✓ Verified artist                        12/16     │ 20
│ ☰  Library   │   │               │                                                      │  8
│              │   │   PORTRAIT    │   Conan Gray                               40/52 sb  │ 52
│ ──────────── │   │   208 × 208   │                                                      │  8
│ ♪ Superbloom │   │   r8 · 1px    │   Nobody has tapped into the thoughts and feelings of │ 40
│ ♪ Late night │   │   shadow8     │   this era quite like Conan Gray.          14/20      │
│ ♪ Deep cuts  │   │   NO SCRIM    │                                        measure ≤640  │ 12
│ ♪ On repeat  │   │   50% 22%     │   20,577,457 monthly listeners · 13,357,890 followers │ 20
│ ♪ Liked      │   │   no zoom     │                                            14/20 sec │ 12
│ ♪ Discovery  │   │               │   ┌───────────┐ ┌────────┐ ┌──┐ ┌──┐ ┌──┐            │ 36
│              │   └───────────────┘   │  ▶  Play  │ │ Follow │ │⤨ │ │((·│ │⋯ │            │
│              │                       └───────────┘ └────────┘ └──┘ └──┘ └──┘            │ 24
│              │ ─────────────────────────────────────────────────────────────── 1px ─────│ ◀ hard edge
│              │                                                                         │ 24
│              │   ┌──────┐  New release · 12 Jun 2026                     12/16 sec      │
│              │   │  96  │  Wishbone Deluxe                        20/28 sb   [Play][Open]│128
│              │   └──────┘  Album · 17 tracks · 52 min               14/20 sec           │
│              │                                                                         │ 24
│              │   ▮ Top tracks  10                                        Show all  ›    │ 44
│              │   1   [40]  Heather                     2,381,125,897   ♥   3:18    ⋯    │ 56
│              │       ───────────────────────────────────────────────────────────────    │ 1px @ x=92
│              │   2   [40]  The Cut That Always Bleeds     662,053,974   ♡   3:51    ⋯    │ 56
│              │   3   [40]  Memories                      758,810,137   ♡   4:08    ⋯    │ 56
│              │   4   [40]  Vodka Cranberry               125,421,696   ♡   4:05    ⋯    │ 56
│              │   5   [40]  Maniac                      1,026,908,442   ♡   3:05    ⋯    │ 56
├──────────────┴─────────────────────────────────────────────────────────────────────────┤ fold y=828
│  ♡   ‹  ⏸  ›    0:22 ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━  -3:42      │ 72
└────────────────────────────────────────────────────────────────────────────────────────┘
```

Vertical ledger, absolute y: titlebar 48 → band 256 → hairline 1 → gap 24 → masthead 128 (ends 457) → gap 24 → section header 44 (ends 525) → rows 56 each → **row 5 ends at 805, fold at 828.** **5 full track rows + the release masthead above the fold, vs 2.2 rows and no masthead in v1** [C-D1]. With no release inside 90 days the masthead is absent and you get 8 rows.

### 2.2 Geometry

**Identity band — height 256, fixed, never clamp/vw-derived.**
`24` top inset + `208` portrait + `24` bottom inset. The portrait sets the height; the copy column is engineered to exactly 208 so both edges align top and bottom (v1's "nothing aligns" [C-D5b] was a shared-baseline failure).

- Portrait `208 × 208`, `Radii.Card` (8), 1px `--wv-stroke-card`, `shadow8`. **The only shadow on the page.**
- Gutter portrait→copy: `32`.
- Copy column = `1096 − 208 − 32 = 856`. Bio measure hard-clamped to **640** regardless of column width (the one durable Qobuz finding [D-§1]).
- Copy stack, summing to 208: eyebrow `20` + `8` + name `52` + `8` + bio `40` (2 lines) + `12` + meta `20` + `12` + actions `36`.

**Photo treatment + focal point.** `object-fit: cover; object-position: 50% 22%` — head-room bias for press-shot framing. **No zoom, no `scale()`, no parallax, no crop-drift, no scrim, no mask, no fade.** The 640² source renders at 208 CSS px = 416 device px at 2× — zero upscale [D-§1]. While loading, the tile fills with the extracted dominant colour at α 1.0. An inset 1px `rgba(0,0,0,.06)` / `rgba(255,255,255,.08)` hairline sits *inside* the radius so a white-background press shot still has an edge. **The avatar is deleted** — there is no second copy of this photo anywhere above the fold [C-D4].

**Wash.** Palette-derived tint at **α .06 light / .12 dark**, filling the band **pane-wide (1160, bleeding through the 32px gutters)**, flat — no gradient, no ramp, no tail. Terminated by a **1px `--wv-divider` hairline**. This is Mica's model (sample once, tint the base gently) and it degrades to `--wv-page-bg` cleanly [A-§4].

**Type ramp — six steps, weights 400/600 only, sentence case everywhere, no uppercase tracking.**

| Element | Style | Token |
|---|---|---|
| Artist name | **40 / 52, 600**, tracking −0.01em, 2 lines max, **no `nowrap`, no ellipsis** | `--wv-text-primary` |
| Eyebrow "Verified artist" | 12 / 16, 400 · 16px inline glyph, **not a pill** | `--wv-text-secondary` |
| Bio | 14 / 20, 400, 2-line clamp, measure 640 | `--wv-text-secondary` |
| Meta line | 14 / 20, 400, `font-variant-numeric: tabular-nums`, `·` separated, **one uniform weight** | `--wv-text-secondary` |
| Section title | 20 / 28, 600 | `--wv-text-primary` |
| Section count / action | 12 / 16 · 14 / 20, 400 | `--wv-text-tertiary` / `--wv-text-secondary` |
| Track title | 14 / 20, **600** | `--wv-text-primary` |
| Track artist / play count / album subline | 12 / 16, 400, tabular | `--wv-text-secondary` |
| Duration | 14 / 20, 400, tabular | `--wv-text-secondary` |
| Masthead eyebrow | 12 / 16, 400 | `--wv-text-secondary` |
| Sticky-bar name | 18 / 24, 600 | `--wv-text-primary` |

The 3.4× chasm and the 7.5px eyebrows are gone; `#419 in the world` moves to About/stats; `6 albums · 20 singles` moves to Releases [C-D19, D-§4].

**Surfaces & elevation — one step per semantic level, exactly one shadow.**

| Layer | Surface | Stroke | Shadow | Radius |
|---|---|---|---|---|
| Base (window) | Mica → `--wv-page-bg` | — | — | — |
| Content layer (the page) | `--wv-layer` | — | — | 0 |
| Identity band | wash α.06 over the layer | 1px bottom `--wv-divider` | **none** | 0 |
| Portrait | image | 1px `--wv-stroke-card` + inset hairline | **shadow8** | 8 |
| Masthead / cover cards | `--wv-fill-card-secondary` / image | 1px `--wv-stroke-card` | none | 8 |
| Controls | `--wv-fill-control` | 1px `--wv-stroke-control` | none | 4 |
| Primary Play | `--wv-accent` fill | none | none | 4 |
| Sticky identity bar | **opaque** `--wv-solid-base` | 1px bottom `--wv-divider` | shadow2 | 0 |
| Row hover | `--wv-subtle-hover` | — | — | 4 |

Radii in play: **4 / 8 / 16** only (16 for SelectorBar segments alone). Nesting depth from viewport to a Play button: **page → band → button = 2**, versus v1's five [C-D15].

**Accent budget — one filled object per view.** `Play` in the identity band is the only accent *fill* on the page. Accent additionally appears as: the 3×20 section bar, the liked-heart *state*, the focus ring, and the hover ▶ on cover cards. **Not** on: the verified badge (glyph, `TextSecondary`), the world-rank badge (deleted from the hero), the masthead Play/Open (both neutral 32px standard buttons), section actions (`TextSecondary` text + chevron), or the transport bar's decorative fill. v1's seven accent objects become one [A-V6].

**Action row — 36 tall, uniform treatment, two widths.** `Play` 112×36 accent-filled r4 · `8` · `Follow` 88×36 neutral (`--wv-fill-control` + 1px stroke) r4 · `8` · `Shuffle` 36×36 · `8` · `Artist radio` 36×36 · `8` · `⋯` 36×36 — the last three identical to Follow's treatment, icon-only. Row = 340. **Play is a rectangle at r4, not a pill — the pill is the Spotify tell** [D-§3A]. 36 rather than 32 is the one declared divergence from `TextControlThemeMinHeight`: it is the deliberate single-step-up for the page's primary action.

**Scrim spec.** **There is none, and there is no code path that can produce one.** No text, badge, count, or control ever composites over photography, at any breakpoint, in any theme. This deletes the four hand-tuned gradient stacks, the cosmetic `text-shadow …0.22`, the per-photo tuning problem, and the 4.5:1 impossibility in one move [A-V4/V5, C-D8/D9]. Every text/background pair in the design is token-on-token and clears 4.5:1 by construction; the α.06 wash moves layer luminance by <2%.

**Hero → content transition.** Wash + **1px hairline** + **24px gap**. Nothing else. No gradient (an alpha ramp corresponds to no Fluent elevation value [A-V2]), no dark band, no blur, no negative margin, no `border-radius: 8px 8px 0 0` shelf lip, no top shadow. On scroll the band translates 1:1 with the page — **no parallax, no counter-translate, no `RestScale 1.05`/`FrameScale 1.08`**; a counter-translating framed object reads as broken.

**Collapse.** At `scrollY > 208` a **48px full-width sticky bar** cross-fades in over 120ms (`Bind.Sticky(0)`): `[32 portrait r4][12][name 18/24][flex][Play 88×32 accent][8][♡ 32][8][⋯ 32]`, opaque `--wv-solid-base` + 1px bottom stroke + shadow2. Two components, one crossfade — no lerping of eight properties, no collapse-height math, no floating pill.

---

## 3. Two alternates

**Which v1 variant survives:** *as a design, none.* `shelf`, `tonal`, and `stage` are deleted. **`current` survives only as the honest control** — and must be **re-implemented** before it can serve as one: today it masks the image 64→100% and reveals `.study-hero { background: #203b4d }`, producing a hard dark slate band the annotation never describes, so the user was asked to reject a variant that was never built [C-D6]. Rebuild it against the real shipping numbers (`WideHeight 420`, `clamp(0.32w, 420, 560)`, `PhotoFadeBandFor = clamp(0.28h,120,180)`, `ContentBlendTail 96` [B-§B, D-§2]) so "today vs Marquee Plate" is a true A/B.

### Alt 1 — **PORTRAIT PLATE 384** (D's Direction A)
Same grammar as the primary, scaled up: a 320×320 portrait in a 384-tall band, copy bottom-aligned to the photo baseline, name still 40/52. **Testing:** does 128 extra vertical pixels — dropping the fold from 5 track rows to ~3 — buy enough photographic presence to be worth it? It is the primary's fallback if the user's reaction to Marquee Plate is "too restrained," and it is the natural home for a `WaveeSettings` row *"Artist header: Portrait / Compact"* (settings row, never an env var). Honest cost: a 320 framed square on a wide light page carries an iTunes-11 memory and needs the wash plus the masthead to read as 2026 [D-§3A].

```
├──────────────┬──────────────────────────────────────────────────────────┤
│ SIDEBAR 280  │▓ wash α.06 · 384 tall · 1px hairline at bottom ▓▓▓▓▓▓▓▓▓▓│
│              │  ┌──────────────────┐   ✓ Verified artist                │ 32
│              │  │                  │                                    │
│              │  │    PORTRAIT      │                                    │
│              │  │    320 × 320     │   Conan Gray               40/52   │
│              │  │    r8 · 1px      │   Nobody has tapped into…  14/20   │
│              │  │    shadow8       │   20,577,457 monthly · 13.4M …     │
│              │  └──────────────────┘   [▶ Play][Follow][⤨][((·][⋯]      │ 32
│              │──────────────────────────────────────────────── 1px ─────│
│              │  ┌────┐ New release · 12 Jun 2026        [Play][Open]     │ 128
│              │  │ 96 │ Wishbone Deluxe                                  │
│              │  ▮ Top tracks  10                          Show all ›    │ 44
│              │  1 [40] Heather            2,381,125,897  ♥  3:18   ⋯    │ ← fold at ~2.7 rows
```

### Alt 2 — **STICKY RAIL** (D's Direction C)
A persistent 344-wide sticky left rail carrying a 344×344 portrait + identity + actions + bio + genre chips, with the catalogue in a 712-wide scrolling column beside it. **Testing:** can the photograph be *larger than anything in today's hero* at **zero vertical cost** — first track visible at y≈180 — by putting identity beside the catalogue rather than above it? **Build one prototype at 1440 only, and only after answering the sticky question.** Three real risks: 280 nav + 344 rail = **43% of window width on persistent chrome**; a ~784-tall rail overflows a 900px window, so it needs bottom-anchored *sticky-until-end*, which `ScrollBind` may only support as top-pin [D-§5]; and sticky-inside-sticky ships nowhere in WinUI, so it will read as a Web/macOS import. Below content 1000 it unsticks and reflows into the primary's marquee row verbatim — which is the argument for building the primary first regardless.

```
├──────────────┬───────────────────────┬────────────────────────────────────┤
│ SIDEBAR 280  │ RAIL 344 · sticky(24) │ LIST 712 (scrolls)                 │
│              │  ┌─────────────────┐  │ ┌────┐ New release · 12 Jun 2026   │
│ ⌂  Home      │  │                 │  │ │ 96 │ Wishbone Deluxe [Play][Open]│
│ ⌕  Search    │  │   PORTRAIT      │  │ └────┘                            │
│ ☰  Library   │  │   344 × 344     │  │ ▮ Top tracks            Show all ›│
│              │  └─────────────────┘  │ 1 [40] Heather      2,381,…  3:18 │ ← first track y≈180
│ ♪ Playlists  │  Conan Gray    30/38  │ 2 [40] The Cut That Always…  3:51 │
│              │  ✓ 20,577,457 monthly │ 3 [40] Memories              4:08 │
│              │  [ ▶  Play        ]   │ 4 [40] Vodka Cranberry       4:05 │
│              │  [ ⤨ | ♡ | ((· | ⋯ ]  │ 5 [40] Maniac                3:05 │
│              │  Nobody has tapped…   │ ▮ Releases                        │
│              │  ( pop )( bedroom )   │ [163][163][163][163]              │
```

---

## 4. Content band redesign — Masthead → Ledger → Segmented grid

**Kill the 2/3 + 1/3 side-by-side band.** At content 1096 the third column spends ~345×230 on a single 96px release card while taxing the ledger ~380px of title measure. Stacked full-width bands return that measure *and* upgrade Releases from 1 visible item to 6 [D-§4]. `TopBandWideW = 760f` and `TopBandHysteresis = 24f` are deleted, not re-tuned.

```
◀─────────────────────────── content 1096 ──────────────────────────────────▶
┌───────────────────────────────────────────────────────────────────────────┐
│ ┌────┐  New release · 12 Jun 2026                12/16                    │
│ │ 96 │  Wishbone Deluxe                          20/28 sb   [Play][Open]  │ 128
│ └────┘  Album · 17 tracks · 52 min               14/20                    │  FillCardSecondary
└───────────────────────────────────────────────────────────────────────────┘  1px · r8
                                    24
 ▮ Top tracks  10                                             Show all  ›     44   ← no card, no chip
 ┌──32──┬─8─┬─40─┬─12─┬──── title 716 ────┬─24─┬ 104 ┬─24─┬40┬8┬48┬8┬32┐
   1        [art]     Heather                   2,381,125,897   ♥  3:18  ⋯    56
        ────────────────────────────────────────────────────────────────────   1px @ x=92
   2        [art]     The Cut That Always Bleeds   662,053,974  ♡  3:51  ⋯    56
   ⋮  … all 10 rows, one column, ranks 1–10 top to bottom …
  10        [art]     This Song                    102,442,308  ♡  3:21  ⋯    56
                                    24
 ▮ Releases  26                                              Show all  ›      44
 ( Albums 6 )( Singles & EPs 20 )( Compilations 2 )( Appears on 14 )          32   SelectorBar r16
 ┌─166─┐ ┌─166─┐ ┌─166─┐ ┌─166─┐ ┌─166─┐ ┌─166─┐        gap 20
 │cover│ │cover│ │cover│ │cover│ │cover│ │cover│                              166
 └─────┘ └─────┘ └─────┘ └─────┘ └─────┘ └─────┘
  Wishbone Deluxe          14/20 sb, 2-line clamp                             40
  2026 · Album · 17 tracks 12/16                                              16
                        ▾ Show all 26
                                    24
 ▮ Gallery  18                                               Show all  ›      44   ← photography as
 ┌─203─┐ ┌─203─┐ ┌─203─┐ ┌─203─┐ ┌─203─┐               gap 20                       CONTENT, not chrome
```

**Section header — 44 tall, one shape everywhere.** `[3×20 accent bar]` + `12` + title `20/28 600` + `8` + count `12/16` tertiary + `flex` + action `14/20` `TextSecondary` + ` ›`. **No icon chip** (invented — the shipping `AccentHeader` has never had one [B-§D3]); **no 7.5px eyebrow** ("Popular now", "Fresh from Conan" — the latter also breaks on the next artist [C-D12]); **no card wrapper** [B-§D2]. The accent bar sits on the 4px grid at x = gutter, height 20, vertically centred against the 28px cap height.

**Band 1 — Latest-release masthead, height 128.** `[16][96 cover r8 +1px][16][ eyebrow 12/16 → 4 → title 20/28 600 → 2 → "Album · 17 tracks · 52 min" 12/16 ][flex][ Play 88×32 neutral r4 ][8][ Open 72×32 neutral r4 ][16]`. Surface `--wv-fill-card-secondary`, 1px stroke, r8. **The floating "Pinned" card is deleted** — one home for the news, first under identity because it is the only element on the page that expires [D-§1 Apple, D-§4]. This single deletion also removes the duplicate release, the `display:none`-at-959px feature loss, the toast-in-the-photo reading, and — decisively — the `right: min(380px, 31%)` reservation that was throttling the name's point size and forcing `nowrap` on the bio [C-D5]. Absent when no release falls inside 90 days; Top tracks becomes band 1.

**Band 2 — Top tracks as one ledger.** Single column, all 10 rows, ranks reading 1→10 vertically. The `1fr 1fr` / `slice(0,5)/slice(5)` split is deleted: a ranked list read as a Z is not a ranked list, "6" at the top of a right column reads as the head of a second list, and it duplicates the like+duration clusters four times per row-pair [C-D13, D-§4].
- Row height **56** (≥ content 720), **48** below. Art **40** at r4.
- Columns: `[32 index][8][40 art][12][title+artist flex][24][plays 104 →right][24][♡ 40][8][duration 48 →right][8][⋯ 32]` = **380 fixed** → title measure **716** at 1096.
- Play counts are **right-aligned tabular figures at 12/16 secondary in their own column** — never a second line under the title, which doubles row height and makes the title compete with its own metadata [D-§4].
- **No per-row border, no chips.** One 1px `--wv-divider` per row, **inset to x = 92** (the text start) so ten rows read as one object. Last row has no divider.
- **Hover:** `--wv-subtle-hover` fill spanning the full content width at r4; the index glyph swaps **in place** inside its own 32px cell (`1` → `▶`) — no `display:none` toggle, no appearing button, no reflow, and the rank is never destroyed [C-D20]. `♡` and `⋯` occupy their columns at all times and go α 0→1, so revealing them also cannot reflow.
- **Focus/keyboard:** row is one focusable; `focus-visible` = 2px `--wv-accent` outline, offset 2, and it triggers the identical index→▶ swap so the affordance is not mouse-only. `↑/↓` move rows, `Enter`/`Space` play, `L` toggles like, `Shift+F10`/`Menu` opens the `⋯` flyout. `♡` and `⋯` are real nested tab stops in DOM order, revealed by `:focus-within`.
- Header action `Show all ›` routes to the full popular page. The shipping `PagedShelf` chevron+pips pager and its five width-pressure tiers are **retired** on this page [B-§A].

**Band 3 — Releases as a segmented grid, not tabs and not a shelf.** Header 44, then a Fluent **SelectorBar** 32 tall at `Radii.Pill`: `Albums (6) · Singles & EPs (20) · Compilations (2) · Appears on (14)`, counts inline — filter in place, never a tab that hides the catalogue (the Roon Overview/Discography complaint [D-§1]). Grid `repeat(auto-fill, minmax(160px, 1fr))`, gap 20 → **6 × 166 at 1096**. First row only, then `▾ Show all 26` expands in place.
- Card = the cover *is* the card: cover `W×W` r8 + 1px stroke α.06 → `8` → title 14/20 600, 2-line clamp → `2` → `2026 · Album · 17 tracks` 12/16 secondary. No plate behind it.
- Hover: stroke α .06→.12 + `shadow4`; a 40px circular accent `▶` fades in bottom-right inset 8 over 120ms; `⋯` top-right. **The cover does not scale** — `HoverScale` in a tight grid reads as neighbour reflow [D-§4].
- **Grid, not a horizontal shelf:** at 1440 you see 6 either way; the shelf adds a gesture, a clipped 7th, no keyboard story, and hides the count. Rails earn their place for heterogeneous endless content (Home); a finite discography is a GridView. This is the "improve, don't port" call — YT Music's carousel-ification of Top songs is the change *not* to copy [D-§1/§4].

**Band 4 — Gallery.** 5 × 203 r8, gap 20 = 1096, opening the existing lightbox. This is where photography becomes information the user chose to look at rather than decoration they learn to ignore [D-§1 NN/g, D-§3B].

**Metadata law.** Monthly listeners and followers appear **once**, in the identity meta line, at one uniform weight (v1's 15px `<strong>` inside an 11px/α.63 parent read as "**20,577,457 13,357,890 6 20**" [C-D19]). `6 albums · 20 singles` lives in the Releases header count. `#419 in the world` lives in About. Verified is a 16px inline glyph before the name, not a pill — two pills above a 40px name is two badges fighting one name [D-§2.8]. Bio: 2-line clamp at measure 640 above the fold, full text in About. **Real album artwork throughout** — the CSS-gradient `--heart` (reads as a red hazard stripe) / `--wishbone` (reads as Saturn) placeholders are deleted; a 40px saturated mark beside a 14px title inverts the hierarchy in every screenshot [C-D18].

---

## 5. Token table

Scope these on the study root and mark them for adoption by the whole prototype. Values are the **shipping Warm preset** (`PaletteBuilder.BuildWarmLight/Dark` [B-§C]) with WinUI stock accent — **not** the prototype's current cool blue-grey, and **not** a second `--study-*`/`--page-*` namespace [B-§E].

```css
[data-page="artist"] {
  /* ── surfaces ─────────────────────────────────────────── */
  --wv-page-bg:              #E9E7E2;   /* WindowBackground / Mica fallback */
  --wv-solid-base:           #EFEEEB;   /* FillSolidBase — sticky bar       */
  --wv-layer:                #FAF9F6;   /* FillLayerDefault — the page      */
  --wv-fill-card:            #FCFBF9;   /* FillCardDefault                  */
  --wv-fill-card-secondary:  #F5F3EF;   /* masthead plate                   */
  --wv-fill-control:         #FCFBF9;   /* FillControlDefault               */
  --wv-subtle-hover:  rgba(31,30,27,.055);
  --wv-subtle-press:  rgba(31,30,27,.030);

  /* ── strokes ──────────────────────────────────────────── */
  --wv-stroke-card:          #DCDAD4;   /* StrokeCardDefault                */
  --wv-stroke-control:       #DEDDD9;
  --wv-divider:       rgba(31,30,27,.09);
  --wv-art-inset:     rgba(0,0,0,.06);  /* inside the portrait radius       */

  /* ── text ─────────────────────────────────────────────── */
  --wv-text-primary:         #1F1E1B;
  --wv-text-secondary:       #5C5B57;
  --wv-text-tertiary:        #656460;
  --wv-text-on-accent:       #FFFFFF;

  /* ── accent (one filled object per view) ──────────────── */
  --wv-accent:               #005FB8;   /* SystemAccentColor Dark1          */
  --wv-accent-hover:         #0A6CC6;
  --wv-accent-press:         #1A76CC;
  --wv-accent-text:          #004275;

  /* ── palette-derived wash (extracted from cover art) ──── */
  --wv-wash-rgb:             0 95 184;  /* runtime: extracted dominant      */
  --wv-wash-alpha:           .06;
  --wv-wash: rgba(var(--wv-wash-rgb) / var(--wv-wash-alpha));

  /* ── elevation: exactly two shadow tokens exist ───────── */
  --wv-shadow-2: 0 0 2px rgba(0,0,0,.12), 0 1px 2px rgba(0,0,0,.14);
  --wv-shadow-8: 0 0 2px rgba(0,0,0,.12), 0 4px 8px rgba(0,0,0,.14);

  /* ── radii: 3 values, no others ───────────────────────── */
  --wv-r-control: 4px;  --wv-r-card: 8px;  --wv-r-pill: 16px;

  /* ── spacing: 4px grid, shipping Spacing.* ────────────── */
  --wv-xxs:2px; --wv-xs:4px; --wv-s:8px;  --wv-m:12px; --wv-l:16px;
  --wv-xl:20px; --wv-xxl:24px; --wv-xxxl:32px;
  --wv-gutter: var(--wv-xxxl);          /* ArtistHeroLayout.PageGutter = 32 */

  /* ── type: 6 steps, weights 400/600 only ──────────────── */
  --wv-t-caption:   400 12px/16px "Segoe UI Variable Text", "Segoe UI", sans-serif;
  --wv-t-body:      400 14px/20px "Segoe UI Variable Text", "Segoe UI", sans-serif;
  --wv-t-body-str:  600 14px/20px "Segoe UI Variable Text", "Segoe UI", sans-serif;
  --wv-t-body-lg:   600 18px/24px "Segoe UI Variable Display", "Segoe UI", sans-serif;
  --wv-t-subtitle:  600 20px/28px "Segoe UI Variable Display", "Segoe UI", sans-serif;
  --wv-t-title-lg:  600 40px/52px "Segoe UI Variable Display", "Segoe UI", sans-serif;

  /* ── page metrics ─────────────────────────────────────── */
  --wv-titlebar-h: 48px;
  --wv-player-h:   72px;   /* WaveeSize.PlayerBarH — NOT the prototype's 82 */
  --wv-band-h:    256px;
  --wv-portrait:  208px;
  --wv-row-h:      56px;
  --wv-bio-measure: 640px;
}

[data-page="artist"][data-theme="dark"] {
  --wv-page-bg:              #1F1E1C;
  --wv-solid-base:           #252422;
  --wv-layer:                #1C1B19;
  --wv-fill-card:            #2A2927;
  --wv-fill-card-secondary:  #242321;
  --wv-fill-control:  rgba(255,255,255,.06);
  --wv-subtle-hover:  rgba(255,255,255,.065);
  --wv-subtle-press:  rgba(255,255,255,.035);

  --wv-stroke-card:   rgba(255,255,255,.09);
  --wv-stroke-control:rgba(255,255,255,.09);
  --wv-divider:       rgba(255,255,255,.08);
  --wv-art-inset:     rgba(255,255,255,.08);

  --wv-text-primary:  #FFFFFF;
  --wv-text-secondary:rgba(255,255,255,.78);
  --wv-text-tertiary: rgba(255,255,255,.55);
  --wv-text-on-accent:#000000;

  --wv-accent:        #60CDFF;
  --wv-accent-hover:  #74D5FF;
  --wv-accent-press:  #4FBCEE;
  --wv-accent-text:   #60CDFF;

  --wv-wash-alpha:    .12;

  --wv-shadow-2: 0 0 2px rgba(0,0,0,.24), 0 1px 2px rgba(0,0,0,.28);
  --wv-shadow-8: 0 0 2px rgba(0,0,0,.24), 0 4px 8px rgba(0,0,0,.28);
}
```

Two dark-theme rules v1 broke and this fixes: `--wv-solid-base` sits **≥8% luminance above** `--wv-layer` so a raised surface is visible without relying on a shadow, and **no black shadow is ever the sole depth cue on a near-black background** [C-D17a/b]. The identity band hard-codes nothing — every colour in it is a token, so theme-testing it is meaningful [C-D17d].

---

## 6. Responsive plan

Sidebar widths from `ShellResponsiveLayout` Classic tiers (240 / 280 / 320 at breakpoints 1400 / 1800; compact rail `NavCompactW` 56) [B-§B].

| | **1440** | **1024** | **720** | **420** |
|---|---|---|---|---|
| Sidebar | 280 | 240 | 56 rail | overlay (0) |
| Gutter | 32 | 32 | 24 | 16 |
| **Content width** | **1096** | **720** | **616** | **388** |
| Band height | 256 | 224 | 168 | 200 |
| Portrait | 208 | 176 | 120 | 88, inline left of name |
| Name | 40/52 | 28/36 | 20/28 | 20/28 |
| Bio | 2 lines, measure 640 | 2 lines, measure 600 | 1 line | hidden → "About" link |
| Meta | listeners · followers | listeners · followers | listeners only | listeners only, 12/16 |
| Actions | inline, one row 36 | inline, one row 36 | own row (+44 to band) | `Play` full-width 388×36, then 4 icons on row 2 |
| Masthead | 128 / 96 cover | 128 / 96 cover | 112 / 80 cover | stacked, 96 cover, actions below |
| Ledger row | 56 | 56 | 48 | 48 |
| Ledger columns | full (380 fixed → title **716**) | full (title **340**) | drop `plays` + `⋯` (212 fixed → title **404**) | `[40 art][8][title][8][48 dur]` (104 → title **284**) |
| Releases grid | 6 × 166 | 4 × 165 | 3 × 192 | 2 × 184 |
| SelectorBar | 4 segments inline | 4 inline | horizontal scroll | horizontal scroll |
| Gallery | 5 × 203 | 4 × 171 | 3 × 192 | 2 × 184 |
| Sticky bar | 48 full | 48 full | 48, drops `⋯` | 48, name + Play only |

**Nothing in this table has `nowrap` + `text-overflow: ellipsis` on the artist name at any width** — v1 encoded a guaranteed clipped artist name as a rule, across nine truncation sites [C-D3]. Truncation survives only on track title (1 line), album title (2-line clamp), and bio (2-line clamp) — all of which have a legitimate measure ceiling. There is **no `display:none` on content-bearing elements** at any breakpoint; secondary content degrades by demotion (bio → About link, plays column → dropped metadata), never deletion of a feature [C-D5e].

---

## 7. Implementation plan — web prototype

Path root: `C:/wavee/fluent-gpu/app/src/components/artist-study/`.

**Delete outright.** The fake window (`.study-window` border/r10/`0 32px 90px` shadow, `.study-window__titlebar`, `.study-window__search`, `.study-window__profile`, `.study-player`), the `height: min(790px, calc(100vh - 172px))` cage, the sticky variant toolbar and the annotation block above the canvas (`.artist-study__canvas` padding, `.artist-study__annotation`), the `--study-*`/`--page-*` variable namespace, `.study-avatar`, `.study-pinned`, `.study-hero__scrim`, `.study-hero__badges`, `.study-album-art--heart/--jump/--wishbone/--portrait`, `.study-track-columns`, `.study-content-card`, `.study-content-surface`, `.study-section-heading i`, `.study-latest-release`, `.study-next-section`, and the `shelf`/`tonal`/`stage` variant CSS blocks. This alone dissolves C-D2/D3/D4/D5/D12/D14/D15/D16/D18.

**Restructure.** Split the 12.3 KB single component into: `ArtistStudyPage.tsx` (route + variant/theme URL state, the only thing `main.tsx` imports), `IdentityBand.tsx`, `StickyIdentityBar.tsx`, `ReleaseMasthead.tsx`, `TrackLedger.tsx` + `TrackRow.tsx`, `ReleaseGrid.tsx` + `ReleaseCard.tsx`, `SelectorBar.tsx`, `SectionHeader.tsx`, `GalleryStrip.tsx`, `artistTokens.css` (§5 verbatim), `artistStudy.css` (layout only). Keep `artistHeroStudy.css`'s filename only if you also rewrite it end-to-end; a rename to `artistStudy.css` is cleaner.

**Host it in the real shell.** Render inside the existing `.wavee-app` grid (`grid-template-rows: var(--title-h) 1fr var(--player-h)`) with the **real sidebar column present** and `--sidebar-width: 280px`, reusing `.titlebar` and the shared player from `AppChrome.tsx`; set `--player-h: 72px` in the artist scope to match `WaveeSize.PlayerBarH` (the shared shell's 82 is a prototype-only value and should follow). Declare `container: artist-page / inline-size` **on the content pane, not the window**, so `cqw` measures 1160 and not 1440 — every proportion in v1 was untransferable because the container was declared with no sidebar [C-D16, B-§D1].

**Reuse, don't reinvent** [B-§E]: route every cover through the existing `Artwork` primitive in `Primitives.tsx` (add a real-image path + extracted-colour placeholder); model `SectionHeader` on the established `content-section-title` shape (bar + title + trailing action) rather than a fourth pattern; drop the bespoke `.study-release-grid` in favour of the existing `.shelf-card` hover vocabulary re-radiused to 8.

**Assets.** `public/assets/conan-gray-hero.webp` stays as the portrait source but must be **downscaled to a 640×640 square crop** so the prototype cannot accidentally demonstrate fidelity the real API does not deliver [D-§1]. Add 6–8 real square cover JPEGs for the ledger and grid; no CSS-gradient art.

**Variant enum** — three *structures*, not four seam treatments:
```
type ArtistVariant = "marquee-plate" | "portrait-plate" | "sticky-rail" | "current";
```
`marquee-plate` is the default (replaces `shelf` as the URL default and in the Playwright default assertion). `current` is the **control**, re-implemented against `WideHeight 420` / `clamp(0.32w,420,560)` / `PhotoFadeBandFor clamp(0.28h,120,180)` / `ContentBlendTail 96` so the baseline is what it claims to be [C-D6]. `sticky-rail` renders only at content ≥ 1000 and falls back to `marquee-plate` below. The four v1 ids must **not** remain aliases — a stale `variant=shelf` URL should redirect to `marquee-plate`.

**Playwright coverage** (extend `C:/wavee/fluent-gpu/app/tests/artist-hero-study.spec.ts`; its `pinned-release-card` and `variant-shelf` assertions must be replaced, and the `variant-{current,shelf,tonal,stage}` loop rewritten):
1. **Fold budget** — at 1440×900, assert the 5th `[data-testid=track-row]`'s `boundingBox().bottom < 900 − 72`, and that the masthead is fully visible. This is the number that killed v1; make it a test.
2. **No text over photography** — assert zero elements matching `text` selectors are descendants of `[data-testid=artist-portrait]`, and that the portrait has no `::after`/scrim child and no `mask-image`.
3. **Contrast** — sample computed fg/bg for name, bio, meta, track title, play count, duration, section title; assert ≥ 4.5:1 in **both** themes.
4. **No truncated name** — for a long-name fixture ("Florence + The Machine", a CJK name, a 40-char name), assert `h1.scrollWidth <= h1.clientWidth` and `getComputedStyle(h1).whiteSpace !== "nowrap"`.
5. **Token discipline** — enumerate every computed `font-size` under the page and assert the set ⊆ {12,14,18,20,40}; every `font-weight` ⊆ {400,600}; every `border-radius` ⊆ {0,4,8,16,999}; count elements with a non-`none` `box-shadow` and assert ≤ 2.
6. **Hover causes no reflow** — capture the row's child bounding boxes, hover, assert the index cell's box is unchanged and the row height is unchanged (guards the `display:none` rank↔play swap [C-D20]).
7. **Keyboard** — Tab reaches the ledger, `↓` moves rows, `focus-visible` produces the same ▶ swap as hover, `♡`/`⋯` are reachable.
8. **Single ranked column** — assert the DOM order of rank labels is `1..10` and that all rows share one `offsetLeft`.
9. **Responsive matrix** — for 1440/1024/720/420 assert content width, portrait size, band height, ledger column count, and `scrollWidth === clientWidth` on the page (no horizontal overflow at any width).
10. **Dark theme is real** — assert `--wv-solid-base` luminance ≥ `--wv-layer` + 8%, and that no element's only depth cue is a black shadow on a near-black parent.
11. **Screenshots** — regenerate `artifacts/artist-hero-study/` as `{marquee-plate,portrait-plate,sticky-rail,current}×{light,dark}×{1440,1024,720,420}`, each **scrolled to top and again to `scrollY 400`** so the sticky bar handoff is captured. Delete the stale six.

---

## 8. FluentGpu mapping notes

| Design surface | Real engine / app concept |
|---|---|
| Identity band, height 256 fixed | `ArtistPage.Hero.cs` `Banner()`. **Delete** from `ArtistHeroLayout.cs`: `HeroHeightFor`, `PhotoFadeBandFor`, `ContentBlendTail`, `BlendBoundaryFor` — the entire height-ladder/fade model is what this replaces. Keep `PageGutter = Spacing.XXXL`. |
| Portrait 208² r8 + shadow8 | A `Card`-shaped node with `Radii.Card`, `StrokeCardDefault`, `Elevation.Card`; image via the existing artist-image fetch. **Delete** `RevealFadeMs 320`, `RevealScaleMs 800`, `RestScale 1.05`, `FrameScale 1.08`, `FrameLiftFrac 0.04`, and the 0.18× collapse parallax counter-translate — five constants, all five gone. |
| Wash α.06 + 1px hairline | The existing cover-extracted accent wash layer, **re-scoped from page-wide-with-a-ramp to band-only-flat**, terminated by `DividerStrokeColorDefault`. Colours from `PaletteBuilder` / the Warm preset in `Design/WaveeTokens.cs`; radii from `Dsl/Radii.cs`, spacing from `Dsl/Spacing.cs`. |
| Type ramp | The Fluent text styles; the name is the shipping stepped ladder capped at 40 (not 48) with steps 40 → 28 → 20. Bio = first-sentence extraction, already implemented. |
| Action row | One accent-filled `Button` + `Follow` + three icon `Button`s at `Radii.Control`; heights 36. |
| Sticky identity bar | `Bind.Sticky(0)` in `FluentGpu.Engine/Dsl/ScrollBindDsl.cs` + `NodeFlags.StickyPinned (1<<19)`; **replaces `ArtistShyPill.cs`** — same trigger (the 0-height sentinel at ~208 instead of ~380), different form factor, and it drops the acrylic + `Scrim(0.55f)`. Crossfade via the `AnimValue` slab `Transition` (120ms), scroll-bound opacity. |
| Release masthead | Reuse the existing masthead atom in `ArtistPage.TopTracks.cs` (96px cover is already the shipping size); **delete the hero's pinned promo card** (`Width = 320f`, `wide && Pinned is not null` gate) and fold pre-release/countdown dressing into this one masthead. |
| Track ledger | `ArtistPopular.cs` — retire `PagedShelf`, the chevron+pips pager, the 2-column split, and the five width-pressure tiers; ship a flat 10-row list at 56/48. In `ArtistPage.TopTracks.cs` delete `TopBandWideW = 760f`, `TopBandHysteresis = 24f`, and `columnW = (w - Spacing.XL)/3f` — the tier/hysteresis latch goes entirely. |
| Section header | `ArtistPage.Sections.cs` `AccentHeader` — kept as-is (3px bar + text + count + trailing action), with **no icon chip** ever added. |
| Releases grid + SelectorBar | `ArtistPage.Discography.cs` / `ArtistPage.Shelves.cs` — the shelf→grid change; SelectorBar from `FluentGpu.Controls`. Section order stays as `Body()` builds it (`ArtistPage.cs:311-332`), with the masthead promoted to first. |
| Gallery | Existing `GalleryStrip` + `ArtistGalleryLightbox`, re-radiused to 8, 5-up at 203. |
| Header size toggle (Alt 1) | A `WaveeSettings` field + a `SettingsPage` row — *"Artist header: Compact / Portrait"*. **Never a new env var.** |

**Two places the engine may not express this today.**
1. **Multi-line clamp with ellipsis at N lines.** The design depends on a *2-line* clamp for the bio, the album title, and the name's overflow behaviour, with ellipsis on the last line only. Verify the Text seam (`Seams/Text/`, DirectWrite path) supports max-line-count + trailing ellipsis; if it only supports single-line trimming, the bio must be pre-truncated at measure by the app, which is a correctness/measure coupling worth knowing before layout is written.
2. **Tabular figures.** The play-count and duration columns are right-aligned numeric columns whose alignment depends on `tnum` (OpenType tabular figures) on the DirectWrite run. Confirm the text run can carry a font-feature setting; without it, proportional digits will make the ledger's right edge ragged — the exact "3–4px off" defect v1 shipped [C-D14].
3. *(Alt 2 only)* **Bottom-anchored sticky.** Sticky Rail needs sticky-until-end (scroll with the page, pin when the rail's bottom reaches the viewport bottom) because a 784-tall rail overflows a 900px window. `ScrollBind` may only offer top-pin. **Answer this before prototyping Alt 2**; if unsupported, the rail's bio clamps to 3 lines and the genre chips are cut so the rail fits 640 [D-§3C].

---

## 9. Open questions

1. **Does the internal `hm://artist` payload actually carry a wide `HeaderImage`, and at what pixel size?** `ArtistOverview.cs` names one. If it delivers a genuine ≥2000px-wide banner, a 384-tall Portrait Plate band gains a *second* legitimate photographic surface and Alt 1 becomes the stronger primary — but if it is another 640² square, the full-bleed idiom is dead for good and Marquee Plate is the only honest answer. Everything about photo prominence hinges on this one measurement.
2. **Does the release masthead lead the page, or do Top tracks?** Leading with the expiring release is Apple's move and it kills the duplicate pinned card, but it costs 156px of the fold — 5 track rows instead of 8. The alternative is Top tracks first with the masthead demoted below them, which reads as a catalogue tool rather than a place you visit for news.
3. **Is `Play` allowed to be 36px tall when every other control on the page is 32?** It is the one deliberate divergence from `TextControlThemeMinHeight` in this spec, and it is the difference between the identity band having a focal point and not. Say no and I drop the whole action row to 32.