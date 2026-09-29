# Wavee Home — UI Pattern Library (Agent 2: UI Patterns)

## 0. Current visual language (as built today)

Skimmed `src/apps/Wavee/Entities/Home.UI.cs`, `Home.Cards.UI.cs`, `Platform/Design.cs`, `Platform/Controls.cs`.

- **Shell is stock Windows 11 Mica.** Design.cs is explicit: "the authenticated shell is the STOCK Windows 11
  Mica stack" — the shell root paints nothing, every chrome band (title row, sidebar, player bar) is Mica
  passthrough. Content pages sit on a `Veil` scrim over Mica (opacity dial ~0.20 for detail pages, i.e.
  "mostly Mica" is the standing direction, not a colored slab). Home specifically uses **three clipped radial
  washes** as its background tint, not a flat fill.
- **Tokens (`Tok.*`)** mirror WinUI/Fluent 2 exactly: `AccentDefault`, `AccentSubtle` (14% light / 16% dark),
  `FillSubtleSecondary`/`FillSubtleTertiary` (hover/press ramps), `StrokeControlDefault`, `StrokeDividerDefault`,
  `TextFillColor`-style ladder (primary/secondary/tertiary), `FillLayerDefault`. Selection uses
  `AccentSubtle` background with `ColorContrast.Over` for hover/press — textbook Fluent 2 selection ramp.
  `RowZebra` exists only for light theme.
- **Corner radii**: `Radii.Card`, `Radii.Control` — a `CornerRadius4` type distinguishes card vs control
  radius (the WinUI 8/4 split). Content pane corners default to `(Radii.Card, 0, 0, 0)` — top-left only, i.e.
  panes tuck under the title bar the way a WinUI NavigationView content frame does.
- **Spacing is a 4-px grid**: `Spacing.XXS/XS/S/M/L/XL` used throughout (chip padding `S, XXS, S, XXS`, gaps
  `Spacing.XS`/`Spacing.M`). Comment literally says "the 4-px grid is the native [ladder]."
  Typography face: `"Segoe UI Variable Display"` for headers (`DisplayFace` const), `Caption()` helper for
  12-px tertiary text — matches the Fluent type ramp.
- **Cards are per-content-shape skins**, not one generic tile: a station is a round 32dp avatar on a 48dp row,
  a mix is a numeral on a shared plate, an audiobook has a rating cluster, an episode is 16:9 with a resume
  hairline. Explicit comment: *"Home is not one square card twelve times... each shape says what the content
  IS before the text does."*
- **Color discipline**: per-item accent color touches exactly four places — the spine (2dp hairline on a
  card's bottom edge), the mix wash, the mix numeral, and the hero backdrop/veil/CTA. Resolved through one
  ladder (`HomeCardAccent`): extracted cover color first, graded cover second, nothing third. Comment:
  *"Accent is never STRUCTURE. A border, a divider, a chevron... is chrome... Accent on structure was the
  single largest source of [visual noise]."* This is a strong, quotable rule for the mockup.
- **Cards use real physics**: `Controls.CardPhysics` = a −4dp lift + 0.99 scale press spring, applied last so
  it always wins over a recipe's own spring. Rows (non-card, tabular) get no lift, just a transparent→subtle
  fill ramp on hover — cards lift, rows just tint.
- **Shelves** are horizontal `PagedShelf` with a `‹`/`›` chevron pager (WinUI-style, not infinite-scroll dots).
- **Chips/tags**: two variants — `Chip` (filled, `FillSubtleSecondary`) for seed-artist chips, `Tag` (1px
  bordered, `StrokeControlDefault`) for the hero's daylist terms — filled=data label, bordered=chrome-on-wash.

**Takeaway for the mockup**: this is already a serious, real Fluent 2 app — Mica-true, 4px-grid, per-shape
cards, disciplined single-accent-per-surface. The redesign should push Zune's *typographic and rhythmic*
soul into this frame, not replace the frame.

## 1. WinUI 3 / Fluent 2 reference spec

**Mica**: a wallpaper-tinted, heavily blurred, low-luminosity-noise backdrop composited by DWM behind the
whole top-level window (not per-panel). Dark theme ≈ base tint `#202020` blended toward the desktop wallpaper's
dominant hue at very low saturation; light theme ≈ `#F3F3F3`. Mica Alt is a *darker*, more opaque variant used
for tool-window-like surfaces (title bar / side panes) so nested content panes can sit as slightly lighter Mica
against it. CSS approximation: a fixed, full-viewport `background` layer with `backdrop-filter: blur(60px)
saturate(120%)` over a soft multi-stop radial gradient standing in for "wallpaper," at ~4% opacity of a fractal
noise `<svg><feTurbulence>` overlay to fake the sensor-noise micro-grain Mica has at 100% zoom.

**Layering**: `Layer` (elevated content on top of Mica, e.g. a card) uses `CardBackgroundFillColorDefault`;
`LayerOnMica`/`LayerOnAcrylic` variants add ~2% more opacity since they sit on already-translucent material.
Dark theme card fill ≈ `rgba(255,255,255,0.0512)` (Layer) with `rgba(255,255,255,0.093)` stroke top-half /
`rgba(255,255,255,0.0698)` bottom (the classic WinUI "top-lit hairline" card border — two different stroke
alphas top vs bottom to fake a light source). Light theme card fill ≈ `rgba(255,255,255,0.7)` with
`rgba(0,0,0,0.0578)` stroke.

**Color tokens (approx, dark / light)**:
- Text primary: `rgba(255,255,255,1.0)` (actually `#FFFFFF` at 100%) / `rgba(0,0,0,0.8956)`
- Text secondary: `rgba(255,255,255,0.786)` / `rgba(0,0,0,0.6063)`
- Text tertiary: `rgba(255,255,255,0.5442)` / `rgba(0,0,0,0.4458)`
- Text disabled: `rgba(255,255,255,0.3628)` / `rgba(0,0,0,0.3614)`
- CardBackgroundFillColorDefault: `#FFFFFF` @5.12% / `#FFFFFF` @70%
- SubtleFillColorTransparent → Secondary → Tertiary: `0%` → `6.05%` white → `4.19%` white (dark); analog
  black alphas in light. This is the hover/press ramp for chromeless rows and buttons.
- ControlStrokeColorDefault: `#FFFFFF` @8.37% (dark) / `#000000` @5.78% (light)
- CardStrokeColorDefault: `#FFFFFF` @5.87% (dark)
- AccentFillColorDefault/Secondary/Tertiary: the system accent at 100/90/80% with automatic light/dark
  luminosity flip (Fluent computes a "light on dark accent, dark accent stays legible on light" pass — never
  hardcode one accent hex, always derive a tint ladder).
- Solid background (window canvas beneath Mica fallback): `#202020` dark / `#F3F3F3` light.

**Corner radii**: 8px = overlay surfaces, cards, flyouts, dialogs. 4px = buttons, text inputs, small controls,
chips. 2px = tiny in-control accents (e.g. progress bar caps). Never mix — a card is 8, a chip inside it is 4.

**Elevation/shadow**: WinUI uses very soft, low-spread shadows keyed to a "reveal" of layered material, not a
drop shadow in the classic sense: `0 2px 4px rgba(0,0,0,0.12), 0 0.5px 1.5px rgba(0,0,0,0.10)` for a resting
flyout; hover/dialog goes up to `0 8px 16px rgba(0,0,0,0.14)`. Cards at rest on Home have effectively *no*
shadow — separation comes from the fill/stroke pair, not elevation; shadow appears only on lift (press/hover
of a genuinely floating element like a TeachingTip).

**Spacing grid**: 4px atomic unit. Standard control padding 8/12/16; page margins 24 (compact) to 48+ (wide);
gutter between shelf cards 12–16; section-to-section vertical rhythm 32–40.

**Type ramp (Segoe UI Variable, size/line-height, weight)**:
- Caption 12/16, Regular — metadata, counts, timestamps
- Body 14/20, Regular — body text, descriptions
- Body Strong 14/20, Semibold — emphasized inline text, button labels
- Subtitle 20/28, Semibold — card group headers, dialog titles
- Title 28/36, Semibold — page-level section headers
- Title Large 40/52, Semibold — page hero title (e.g. a playlist name)
- Display 68/92, Semibold — reserved for the most singular moment on a page (used sparingly; this is the
  register Zune's "quickplay" header lives in — see §2)

**Controls relevant to Home**:
- **SelectorBar** (successor to Pivot): underline-indicator horizontal tab strip, no card chrome, indicator is
  a 2px accent bar that slides between labels with a spring, ~200ms. This is the natural home for the
  Music/Podcasts/Audiobooks chip row already in the digest data.
- **TitleBar**: 32px tall (48 with search box docked), caption buttons live top-right, draggable region is
  Mica itself so it reads as part of the material, not a bar on top of it.
- **InfoBar**: used for system messages ("You're offline"), never for editorial content — flag if the mockup
  is tempted to use it as a promo banner.
- **ItemsView/GridView**: virtualized card grid, standard item spacing 4–8px, selection uses the
  `AccentSubtle` wash, not a border.
- **FlipView**: could motivate a hero carousel but is rarely used on Windows 11 surfaces now; SelectorBar +
  manual paging chevrons is more current.
- **TeachingTip**: 8px-radius flyout with a pointer tail, used for one-time callouts (e.g. "long-press to
  reorder") — reserve for onboarding a redesigned Home feature.
- **SplitButton**: primary action + chevron-revealed menu — good fit for a hero's "Play ▾ (Shuffle/Play)".
- Hover state: fill ramp only (`SubtleFillColorSecondary`), no scale change on plain rows. Pressed: darker
  ramp step + optional −1px scale on cards specifically (Wavee's own `CardPhysics` already does −4dp lift +
  0.99 press, which is *more* generous than stock WinUI's near-flat press — keep that, it's a Wavee signature).
- Focus visuals: a 2px accent-colored rounded rectangle offset ~2px outside the control bounds (never inset,
  never a color change alone) — required for keyboard/gamepad users, must render in the mockup as a visible
  (if muted) affordance class even if shown only in one state callout.

**Motion**: Fluent uses a small vocabulary of eased curves — "Fluent standard" cubic-bezier(0.8,0,0.2,1) for
entrance (~250–300ms), "Fluent decelerate" cubic-bezier(0.1,0.9,0.2,1) for content reveal, "Fluent accelerate"
for exits (~150ms). Connected animation morphs a tapped tile's bounds/art into the destination hero over
~350ms. List entrance uses a staggered fade+8px-rise per row, ~20–30ms stagger between items, capped around
8-10 visible items so a long list doesn't crawl in for a full second.

## 2. Zune reference spec (what made it loved)

- **Typography-as-UI**: section headers set in a huge, light-weight, *lowercase* face bleeding past the right
  edge of the viewport — e.g. "quickplay" rendered at ~64–96px, weight 200–300, tight or slightly negative
  letter-spacing (-0.01em), full-width lowercase, often clipped by the window edge on purpose so it reads as
  a typographic *event* rather than a label. Sub-labels beneath in small caps or 11px, wide letter-spacing
  (+0.08em), 60–70% opacity — the "eyebrow" pattern before it had a name.
- **Panorama / parallax**: content extends horizontally beyond the viewport; background art (artist image,
  gradient) scrolls at ~40–60% of the foreground's rate, so headers stay legible while imagery drifts — cheap
  to fake in CSS with two scroll-linked layers or a fixed background-attachment trick, or in the FluentGpu
  engine with a parallax factor on a `BoxEl`'s transform bound to shelf scroll offset.
- **Quickplay** (the Zune 4.x start screen): a grid of *variable-size* tiles — big tiles for "pinned"/most
  recent, small tiles for history — no card chrome at all, just image + a thin text label overlay at the
  bottom-left in the same light typography. This is functionally what "Jump back in" / "Recents" already are
  in the digest — Zune just gave them one unified oversized-tile canvas instead of a uniform shelf.
- **Mixview**: a radial/orbital "related artists" explorer — an artist tile in the center with satellite tiles
  around it. Too heavy for Home; worth a one-line mention as a *future* pattern, not spec'd here.
- **Smart DJ**: an endless auto-generated station seeded from one artist/track — conceptually identical to
  Wavee's Radio stations already in the data (`"X Radio"` items) — Zune's UI move was a single glowing "start
  a smart dj" pill under the seed's hero art, not a shelf entry.
- **Social tab**: irrelevant to Home; skip.
- **Artist background imagery with grain**: full-bleed artist/album photography behind translucent panels,
  with a fine film-grain noise overlay (~3–5% opacity monochrome noise) to keep flat gradients from banding
  and to give the whole thing a tactile, non-plastic finish — this pairs *very* naturally with Mica's own
  noise, they're the same idea from two eras.
- **Pivot header**: the now-standard WinUI Pivot literally descends from Zune's top tab strip — large
  lowercase labels, generous horizontal spacing, active tab in full brightness, inactive tabs at ~50% opacity,
  no underline in the original (WinUI added the underline later for a11y).
- **Restrained color, one accent**: Zune's whole chrome was near-black/near-white with a single accent used
  sparingly — orange-to-magenta gradient on Zune HD, solid brand accent on desktop — never a rainbow of card
  colors. Reinforces Wavee's existing "accent only on spine/wash/numeral/hero" rule almost exactly.
- **Content-first chromeless design**: no borders, no drop shadows, no button skeuomorphism — separation comes
  from whitespace, scale contrast, and the panorama's motion, not from boxes. The biggest risk in a fusion
  design is re-introducing card borders everywhere out of habit; Zune's UI had almost none.

## 3. Fusion patterns (8–12), spec-ready

Colors given as dark / light rgba pairs, built from the token spec in §1. `--radius-card: 8px; --radius-ctl: 4px;
--space: 4px base grid.`

**1. Quickplay Header** — top of Home, above the first shelf. Oversized lowercase greeting ("good morning")
at 56–72px / weight 200 / line-height 0.95 / letter-spacing -0.01em, allowed to run edge-to-edge and clip on
resize; a Subtitle-weight (20/28, 60% opacity) line beneath for context ("Friday, September 25"). Sits directly
on Mica, no card. Motion: fades+rises 12px over 280ms Fluent-decelerate on page load, no repeat.
```
good morning
Friday · ready when you are
```

**2. Resume Hero Tile** — single oversized tile (full shelf width on mobile-narrow, ~40% width desktop) for
"continue where you left off" (an album/playlist with resume position). Art fills a 16:9 or 4:3 crop with a
gradient scrim `linear-gradient(180deg, transparent 40%, rgba(0,0,0,.55) 100%)` (dark) / lighter scrim
(light), radius 8px, title in Title (28/36) over the scrim, a resume progress hairline (2px, accent, at the
image bottom edge — reuses Wavee's existing `Spine` component verbatim), SplitButton "Play ▾" bottom-right,
lift+shadow on hover `translateY(-4px)` (matches existing `CardPhysics`), press scale 0.99.

**3. History Strip** — a Zune quickplay-style dense row of *small square* tiles (56–72px), no title text on
the tile itself, just art + a 1px accent-if-playing ring; a caption line below the strip reads "recently
played" in Caption/Tertiary. Horizontal scroll, no card chrome, 4px gaps only — the anti-shelf, deliberately
lighter than the card shelves around it, for rhythm contrast.

**4. WinUI Chevron Shelf** — the standard horizontal card shelf, unchanged from Wavee today: header row is
Subtitle (20/28 semibold) + optional Caption subtitle to the right + `‹ ›` chevron pair (32px circular hit
target, `SubtleFillColorSecondary` on hover, glyph opacity 70%→100% on hover), card gap 12px, card corner 8px,
card fill = CardBackgroundFillColorDefault, no border in dark (rely on the fill's own contrast against Mica),
1px `CardStrokeColorDefault` border in light (Mica-light is too close in luminosity to a translucent white
fill without it).
```
Your top mixes                                    ‹ ›
[img][img][img][img][img][img]...
```

**5. "Why" Caption Pattern** — every algorithmic/personalized shelf gets one small explanatory line under its
header, Caption 12/16, Tertiary color, max 1 line with ellipsis, e.g. "Because you listened to Troye Sivan."
Never a tooltip-only explanation — Zune's ethos was to *say* why something's here in the UI itself, not hide
it behind an affordance.

**6. Radio Station Card — Stacked Artist Faces** — for "Recommended Stations"/"Popular radio": a round
station art (the mix-radio cover) with 2–3 small circular artist avatars overlapping bottom-right (16px each,
2px Mica-colored ring so they read as "cut out" of the base card, offset -6px each), title below, "With X, Y,
Z and more" as Caption/Secondary. Row height 48dp variant available for a denser list mode (mirrors the
existing per-shape-skin discipline — this is a new skin, not a repaint of the generic card).
```
  ⬤●  Savage Garden Radio
 ●●    With All-4-One, *NSYNC...
```

**7. Artist Circles** — for artist entities in mixed shelves (digest shows bare `ArtistResponseWrapper` items):
perfectly round 96px (shelf) / 40px (dense row) avatar, no card frame at all, name centered below in Body
Strong, no subtitle unless verified-artist checkmark is needed (small 12px badge, accent-filled circle,
white check, bottom-right of the avatar, only for the "Officiële Spotify" style entities).

**8. Dense Shortcut Grid** — a 2–3 column × N-row grid of small horizontal "row" cards (not `Card`, Wavee's
existing tabular `Row` primitive: 56–64dp tall, small square art + title + no chevron, `FillSubtleSecondary`
hover only, no lift) for "Jump back in" style mixed-type shelves where 6-9 items should read as one glanceable
block rather than a scrolling shelf — matches the digest's `items=9 total=14` "Jump back in" section exactly.

**9. Time-of-Day Tint** — the greeting's Mica wash (Wavee already has "three clipped radial washes" on Home)
gets a slow, low-saturation hue drift keyed to local time: cool blue-grey wash pre-noon, warm amber-grey
dusk/evening, neutral midday — amplitude small enough that it reads as ambient mood, not a colored page.
Implement as a CSS custom property `--daypart-hue` interpolated by JS `Date` in the mockup; in-engine this
would be a signal already available since Wavee computes an "accent from cover" — same math, different input.

**10. SelectorBar (Music / Podcasts / Audiobooks)** — underline-indicator tab strip directly under the
Quickplay header, Body Strong 14/20 labels, 2px accent underline sliding between tabs (~200ms
Fluent-standard ease), inactive label at Text/Secondary, no background fill on tabs ever (chromeless, per
Zune's pivot). This maps 1:1 onto the digest's `homeChips` field.

**11. Hover-to-Play Affordance** — every card/tile in every pattern above reveals a circular accent Play button
(40px, centered on the art, `rgba(0,0,0,.35)` scrim behind it for contrast on light art) on hover only, fades
in over 120ms, no layout shift; press triggers the existing `CardPhysics` 0.99 scale. On touch/no-hover
surfaces the button is always visible at 70% opacity in the art's bottom-right corner instead.

**12. Context-Derived Color Wash** — reuses Wavee's existing accent ladder verbatim as a *pattern name* for the
mockup author: a section or hero may take a very low-opacity (8–12%) radial wash of the lead item's extracted
cover color behind its header text only — never behind body shelves, never full-bleed. This is the one place
"one accent per surface, applied to decor never structure" (Design.cs's own rule) should visibbly show up in
the mockup so the fourth agent doesn't invent a different color rule.

## 4. Anti-patterns

- **Cloning Spotify's web/desktop green home** — dark-flat-gray cards with a hard green accent slapped on
  every CTA. Wavee is Mica-native; a flat `#121212` background instead of translucent Mica reads as a skin,
  not a Windows app.
- **Rainbow cards** — a different saturated color per card/shelf "for personality." Directly violates the
  existing accent discipline (`Accent is never STRUCTURE`) and Zune's restrained-color ethos; the eye has
  nowhere to rest and nothing reads as *the* accent anymore.
- **Too much chrome** — borders on every card, drop shadows at rest, boxed section headers, background fills
  behind every label. Fluent 2 and Zune both get separation from typography scale and whitespace, not frames.
- **Fake Mica** — a flat solid dark-gray `#202020` rectangle with no blur/noise/translucency standing in for
  the backdrop. If the mockup can't render true backdrop-filter blur reliably, a gradient+noise fallback
  (§5) is required — a flat fill reads instantly as "not a Windows app."
- **Uniform square cards for every content type** — ignoring the shape vocabulary (round station avatars, mix
  numerals, artist circles, episode 16:9 crops) collapses Home back into "one card twelve times," which the
  current codebase's own comments explicitly warn against.
- **Overwrought motion** — parallax/stagger/connected-animation on *every* element simultaneously. Zune's
  panorama worked because only the header and background moved differently; if everything animates, nothing
  reads as intentional.
- **Low-contrast light theme neglect** — designing only for dark Mica (common failure mode) and leaving light
  theme as an inverted afterthought; light Mica needs the 1px stroke on cards that dark doesn't, and text
  alpha values are not simple inversions of the dark set (see §1 token list — they're tuned independently).

## 5. HTML mockup rendering guidance

- **Mica approximation**: full-viewport fixed `<div class="mica">` behind all content:
  `background: radial-gradient(1200px 800px at 20% -10%, #2b3040 0%, transparent 60%),
  radial-gradient(1000px 700px at 90% 10%, #3a2a30 0%, transparent 55%), #202020;`
  plus `backdrop-filter: blur(40px) saturate(130%)` on the content-pane layer sitting above it (blur only
  matters if content-pane itself is semi-transparent over something with detail, e.g. a screenshot-behind
  effect — for a static mockup, apply the noise+gradient combo directly and skip backdrop-filter unless there's
  a real layered scrollable background to blur). Grain: one shared inline SVG `feTurbulence` (`baseFrequency
  0.9`, `numOctaves 2`) as a repeating background at ~3% opacity, mix-blend-mode `overlay`, applied once to
  the mica div, not per-card (perf + Zune-grain fidelity).
- **Light theme Mica**: same technique, base `#F3F3F3`, gradient hues desaturated further (~8% chroma vs
  ~14% dark), noise opacity down to ~1.5% (light Mica is visually much quieter than dark).
- **Font stack**: `font-family: "Segoe UI Variable Display", "Segoe UI Variable Text", "Segoe UI", system-ui,
  sans-serif;` — declare both Variable Display (headers) and Variable Text (body) as separate CSS custom
  properties (`--font-display`, `--font-text`) since real Segoe UI Variable ships as two optical sizes; on the
  web both fall through to the same installed "Segoe UI Variable" or plain "Segoe UI" on Windows viewers, and
  to system-ui elsewhere — acceptable since this mockup targets a Windows-using reviewer.
- **Icons**: Segoe Fluent Icons is a local font, unavailable on the web — do not reference its Unicode
  codepoints expecting glyphs to render. Use small inline SVG paths (16/20/24px, `currentColor` stroke ~1.5px
  or filled per Fluent's outline-vs-filled duality) for: play (triangle), pause (two bars), chevron
  left/right, shuffle, more (three dots), verified-check, pin. Keep every icon a single `<svg>` with no
  external font/icon-webfont dependency so the mockup renders identically offline.
- **Window frame for context**: wrap the mockup in a chromeless-look window frame — 32px title bar (Mica,
  draggable-looking, three caption glyphs top-right as inline SVG, app icon + "Wavee" wordmark top-left at
  Caption size), a 48–72px icon-rail or ~280px full left nav (mirror Wavee's actual sidebar informally — icons
  + labels, selected item gets `AccentSubtle` wash not a border), and a 72–80px bottom now-playing bar (art
  thumbnail 48px, title/artist two-line stack, transport controls centered, volume/queue icons right) — all
  sitting on the same Mica layer as the content so the seam between chrome and content is invisible, which is
  the whole point of Mica. Render both a dark and a light variant of the full frame (two artboards or a
  toggle) since token values genuinely differ, not just invert.
- **Scroll affordance**: shelves should visually clip at the viewport edge with a soft ~24px fade-to-transparent
  mask (`mask-image: linear-gradient(90deg, black calc(100% - 24px), transparent)`) rather than a hard cut, to
  hint continued content without needing working scroll in a static mockup.
