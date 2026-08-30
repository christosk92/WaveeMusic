# Artist-page hero: detached card vs. full-bleed banner — research note

**Status:** research only, no code changed. **Scope:** `src/apps/Wavee/Features/Detail/Artist*` + the type/color
contracts it leans on. **Date:** 2026-07-29.

Three reported defects, one root question:

- **(a)** the approved HTML prototype showed a **detached inset rounded card** (8px radius, gutters, hairline); the
  implementation kept the pre-existing **full-bleed banner**.
- **(b)** the hero's bottom fade/scrim is **always black** (`Scrim()` is black-only,
  `ArtistPage.Sections.cs:47`); in light mode the darkened lower hero collides with the near-white page.
- **(c)** the artist name (48px / weight 700 / white-on-photo) "looks a bit ugly, especially in light mode".

> The prototype HTML is **not in the tree** (`docs/prototypes/` holds only `account.html`,
> `playlist-signals.html`; `docs/plans/wavee/media-card-concepts.html` is the card study). Its geometry below is
> taken from the brief, not verified against a file.

---

## 0. Testing the central hypothesis

**Hypothesis as stated:** *the always-black fade is a structural consequence, not a color bug — a full-bleed hero MUST
dissolve into the live page composite, and a dissolve needs a dark direction, so it can't be theme-neutral.*

**Verdict: right conclusion, wrong mechanism.** The hero's bottom band is **two** independent layers and only one of
them is dark:

| Layer | Where | What it is | Theme-neutral? |
|---|---|---|---|
| Photo dissolve | `ArtistPage.Hero.cs:149` — `EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, PhotoFadeBandFor(height))` | an **alpha** mask (`EdgeMask`, `Effects.cs:136`, `:169-188`) | **yes** — alpha has no colour and no "direction" |
| Copy legibility veil | `ArtistPage.Hero.cs:184-196` — `GradientDown(0.30/0, 0.60/0.40, 0.85/0.55, 1.0/0)` over `Scrim()` = pure black | a **colour** veil so white type survives an arbitrary photo | **no** — and it can't be, while the copy is white |

So the dissolve is *already* theme-neutral. The black is not the dissolve; it is the **contrast veil for
bottom-anchored white copy** — exactly what the code says it is (`ArtistPage.Hero.cs:166-169`: *"Contrast belongs ABOVE
the edge-faded media… guarantees the white hero type/buttons remain readable over both pale faces and dark hair"*).

**The real structural coupling is:** *white copy* × *anchored to the hero's bottom edge* × *that edge is also the
page seam*. The veil peaks (0.55) at 85% height because that is where the name/meta/buttons are — i.e. **at the seam**.
Change any one of the three factors and light mode stops colliding. That gives three levers, not two, and it is why
Option A works: it doesn't remove the black, it removes the *seam* the black lands on.

### The numbers (why dark mode looks fine and light mode doesn't)

Composites, `h = 420`, `PhotoFadeBandFor(420) = 120` (`ArtistHeroLayout.cs:48`) ⇒ the photo's feather starts at
`0.714·h`, so at `0.85·h` the photo is ~56% opaque:

| | light | dark |
|---|---|---|
| page under the hero | `FillLayerDefault` `#80FFFFFF` (`PaletteBuilder.cs:78`) over `MicaRef.LightDefault #EDEDED` (`ColorContrast.cs:65`) ⇒ **≈246** | `#3A3A3A4C` (`PaletteBuilder.cs:244`) over `MicaRef.DarkDefault #202020` (`:68`) ⇒ **≈40** |
| veil peak at `0.85·h`, mid-grey photo (128) | `0.45 · (0.56·128 + 0.44·~240)` ⇒ **≈80** | `0.45 · (0.56·128 + 0.44·45)` ⇒ **≈41** |
| step across the last ~60px | 80 → 246 = **≈7:1** (dark press photo: 63 → 246 ≈ **9:1**) | 41 → 40 = **≈1.02:1** |

**The black veil's terminal value is accidentally matched to the dark page and mismatched to the light page by ~5.5×.**
That is the whole of complaint (b), quantified. It is not a bug in `Scrim()`; it is a veil tuned (by eye, in dark) to
land on a value the light theme never produces.

**Falsifiable prediction:** `WaveeSettings.DisableColorWashes` (`Platform/AppSettings.cs:46`) blanks `washLayer`
entirely (`ArtistPage.cs:320-321`). With washes **off**, in **light**, the photo feathers straight onto ~246 with no
accent buffer — the collision must get measurably harder. If the reported screenshot was taken with washes off, that
is the worst-case configuration and the numbers above understate it.

---

## 1. Option A — the detached hero card

### 1.1 Geometry

The content column is already `Grow=1, MaxWidth=1600` inside a `Justify=Center` row, padded by
`ArtistHeroLayout.PageGutter = 48` (`ArtistPage.cs:287-299`, `:343`; `ArtistHeroLayout.cs:24-27`). Mirror it exactly:

```
outer  = the collapse owner — UNCHANGED: full-bleed, Height = h, ClipToBounds,
         OnBoundsChanged = MeasureHero, ScrollBinds = [ PinTop 0, PresentedHTrailing Px(0,h) ]
  row  = BoxEl { Direction = 0, Justify = FlexJustify.Center }
    blk = BoxEl { Grow=1, Shrink=1, MinWidth=0, Basis=0, MaxWidth=1600,
                  Padding = Edges4(Gutter, 0, Gutter, 0) }        // Gutter == the content column's
      CARD = BoxEl { Width = cardW, Height = h, ZStack = true, ClipToBounds = true,
                     Corners = CornerRadius4.All(Radii.Card),      // 8 — Radii.cs:12
                     BorderWidth = 1f, BorderColor = <per-theme hairline, §1.4>,
                     Shadow = Elevation.Card,                      // Elevation.cs:18-21
                     Children = [ heroParallax, copyContrast, overlay ] }
```

**The owner must stay full-bleed and stay a direct child of the tall scroll content** — the sticky pin's
containing-block clamp is the *parent* height and a tight wrapper clamps the pin to 0
(`ArtistPage.Hero.cs:210-215`, the documented empty-band bug). Inset the **card**, never the owner.

`ArtistHeroLayout` additions (pure, unit-testable, same idiom as the existing helpers):

```csharp
public static float GutterFor(float viewportW) => viewportW < 640f ? 20f : PageGutter;   // 48
public static float CardWidthFor(float viewportW)
    => MathF.Max(1f, MathF.Min(viewportW, 1600f) - 2f * GutterFor(viewportW));
```

`GutterFor` must be adopted by `inner`'s padding too (`ArtistPage.cs:297`) or the two stop aligning below 640.

**Height:** keep `HeroHeightFor` byte-identical (all six tests in `ArtistHeroLayoutTests.cs` test the *function*, so
they stay green) but **feed it the card width**: `HeroHeightFor(CardWidthFor(vw))`. That fixes a real defect for free —
today the height keeps growing to `MaxHeight = 560` on a 2560px monitor while the content column froze at 1600, so the
hero gets *taller* while everything else stopped. With `cardW` capped at 1504 the height freezes at
`0.32·1504 = 481`. Resulting card aspects:

| viewport | cardW | h | aspect |
|---|---|---|---|
| 420 | 380 (gutter 20) | 640 | 0.59 (portrait — intended narrow banner) |
| 640 | 544 | 583 | 0.93 |
| 900 | 804 | 464 | 1.73 |
| 1200 | 1104 | 420 | 2.63 |
| ≥1600 | 1504 | 481 | 3.13 (frozen) |

`MaxHeight = 560` becomes unreachable ⇒ lower the clamp ceiling to **500** and update the three assertions in
`HeroHeightFor_UltraWide_GrowsWithTheWindowUntilTheCap` (`ArtistHeroLayoutTests.cs:36-47`).

**Card padding.** The card's *edge* now sits on the content column's edge, so the copy inside is necessarily inset
further: `Padding = Edges4(32, 32, 32, 28)` (`Spacing.XXXL`/`Spacing.XXL`, `Spacing.cs:18`). The comment at
`ArtistPage.Hero.cs:109-111` ("the hero name and the first section band start on ONE vertical") must be **rewritten** —
and the new claim is *stronger and actually true*: today the hero copy insets 48 from the **viewport** while the
content column insets 48 from the **1600-capped centred block**, so on a 1920 window the name starts at x=48 and the
first section header at x=208 — **160px out of alignment**. The card fixes that by construction: card edge ≡ column edge.

### 1.2 What DIES

1. **`media.EdgeFade`** (`ArtistPage.Hero.cs:149`). Feathering the photo to transparent *inside* a bounded card would
   reveal the card's own fill through the photo's bottom — worse than either state. Delete.
   - ⇒ `ArtistHeroLayout.PhotoFadeBandFor` (`:44-48`) becomes dead; its two tests
     (`ArtistHeroLayoutTests.cs:63-86`) delete. **This is the only test deletion in Option A.**
   - **Verified safe:** the fade was *not* load-bearing for coverage. At scroll `s` the owner's `ChildShiftY = −s`
     (`ScrollBindEval.cs:366-377`) and the parallax adds `+0.18·s` (`ArtistPage.Hero.cs:161`), so the photo box spans
     `[−0.82s, h−0.82s]` while the presented band is `[0, h−s]`; coverage holds for all `s ≥ 0`. The fade only
     *softened* the clip line. The card's rounded bottom edge is now that line — deliberately hard.
2. **The terminal black region.** The veil's last stop is already 0 (`:191`); what collides is the 0.55 peak sitting on
   the seam. Inside a card there is no seam: 8px of radius + 1px hairline + a 4px shadow ramp separate card pixels from
   page pixels.
3. **The wash-to-zero seam machinery's *seam role***. `ArtistPage.cs:303-309` states the contract: *"no pixel row
   exists where background responsibility changes hands… never paint an OPAQUE approximation."* A detached card has no
   shared row — the card owns its pixels, the page owns the rest, and the boundary is an **edge**, not a seam. The
   contract is satisfied *vacuously*, which is the strongest way to satisfy it.
   `ContentBlendTail` / `BlendBackdropHeightFor` / `BlendBoundaryFor` (`ArtistHeroLayout.cs:29-56`) lose their reason
   to exist in their current shape (see §1.3 for the wash's new job).

### 1.3 What must be REWORKED (with effort)

| # | Item | Change | Effort | Risk |
|---|---|---|---|---|
| 1 | **`_heroWidth` plumbing** (`ArtistPage.Hero.cs:22,34,135,208`; `ArtistPage.cs:310`; `HeroArt._width`) | today one signal is viewport width *and* media width *and* decode width *and* wash geometry. Split: `_heroWidth` stays the measured viewport (feeds the height ladder + wash); add a **derived** `_cardWidth = CardWidthFor(_heroWidth)` — a computed read-signal, **no second `OnBoundsChanged`**, so no extra bounds pass and no re-render loop. `HeroArt` takes `IReadSignal<float>` already (`:507`), so it drops in. | **S** ~30min | low. `HeroArt` latches its decode target on first real measure (`:519-525`); a derived width resolves on the same frame, so the latch is unchanged. `Key = "heroart:" + hu` (`:135`) omits width ⇒ no remount churn. One-time re-decode of warm images at the new width. |
| 2 | **The wash's new role** (`ArtistPage.cs:310-329`) | today the wash is **invisible behind the opaque photo** and only shows through the feather. With gutters it becomes a **visible tinted L-frame around the card** — a guaranteed regression at the current alphas (light 0.20 top). Re-role it as a *bleed of the card's own colour onto the page*: height = `h + 120`, two visible stops, **light 0.20 → 0.12 / dark 0.30 → 0.20**, boundary stop fixed at 0.72 instead of `BlendBoundaryFor`. Keep the `DisableColorWashes` gate (`:319-321`). | **S** ~30min | **medium — this is the most likely visual regression if skipped.** Keep or retire `BlendBackdrop*` tests (`ArtistHeroLayoutTests.cs:95-112`) with the helpers. |
| 3 | **Collapse: the card's rounded bottom edge** | `SceneRecorder.cs:677-681`: *"the node's own fill + its child clip are drawn at PresentedW/H"* — so a node with `PresentedH` keeps its **rounded corners at the presented rect**. That is a gift: give the **card** a plain `PresentedH` bind so it visibly shrinks in place with its radius and hairline intact: `new() { From = Offset, To = BindSink.PresentedH, Range = Px(0, h), OutStart = cardH, OutEnd = -6 }`. (`WriteScalarSink` does `Max(0, v)`, so the −6 target makes the card's presented height lag the owner's clip by 0→6px, keeping the AA row off the clip line.) Owner keeps `PinTop` + `PresentedHTrailing` **unchanged**. | **M** ~2h incl. a gate | **Do NOT nest two `PresentedHTrailing` sinks** — that double-counts `ChildShiftY`, the exact bug documented at `ArtistPage.Hero.cs:210-215`. Plain `PresentedH` (sink 8) writes no shift. `PinTop` + a presented sink already coexist on one node today (`:220-224`), and the transform-owner guard (`Reconciler.cs:2965-2971`) only fires against a *static Transform matrix*. Warrants a VerticalSlice gate beside 23u2 (`AnimSuite.cs:1827,1907`). |
| 3b | **A2 fallback for (3)** | do nothing: accept a flat-cut bottom during the collapse. It is transient (≤300ms of scroll) and the card is on its way out. | **0** | none |
| 4 | **`StretchFromTop`** (`ArtistPage.Hero.cs:142`) | compiles to `ScaleUniform` about (0.5, 0) (`Reconciler.cs:3022-3026`) — on the *card* it would scale the card past its gutters **and scale the 1px hairline**. It is already on the inner `media` node, which becomes a descendant of the card's `ClipToBounds`. **Zero change needed.** | **0** | none — but verify `media` stays *inside* the card, not beside it |
| 5 | **`PinnedCard` overlay** (`:379-451`) | (i) the `wide` gate `w >= 960f` (`:43`) reads the **viewport**; must read `cardW` (`cardW >= 900f`, ≈ viewport 996) or a 960–1050px window crushes the copy column inside an 864px card. (ii) its `Corners = Radii.Card` (8, `:404`) now nests inside an 8-radius card ⇒ use `Radii.Control` (4) per the outer-8/inner-4 Fluent tell (`Radii.cs:12`). | **XS** 2 lines | low. Its `Fill = Scrim(0.55)` + white ink stay correct: dark-on-photo *inside* a photo card is legitimate in both themes. |
| 6 | **Shimmer parity** (`ArtistPage.cs:147-177`) | the shimmer hero must gain the same centring wrapper + gutter + `Corners = Radii.CardAll` on its `ImageEl`, or the shimmer→content swap steps ~96px of width and `SmoothResize` animates the jump. | **S** ~30min | medium if skipped — a visible pop on every artist load |
| 7 | **`HeroBioLine`** (`:244`) | `Width = Min(w - 2*PageGutter, 860)` must become the card's inner measure or the bio overflows. | **XS** | low but it *is* an overflow bug if missed |
| 8 | **`copyContrast` box** (`:184-186`) | `Width = w, Height = height` → card box. | **XS** | none |

### 1.4 The scrim, INSIDE the bounded photo — exact stops

The current 4 stops exist to (i) protect white copy **and** (ii) release to 0 at the seam. Inside a card, (ii) is
**no longer required** — and (ii) was consuming the scarcest resource in the whole layer: `GradientSpec.MaxStops` is 4
and extras are **silently dropped** (`ArtistPage.Hero.cs:170-174` documents the exact bug this caused). Spending the
release stop on protection instead:

```csharp
Gradient = GradientDown(
    new GradientStop(0.34f, Scrim(0f)),      // face zone untouched; the shader clamps to stop 0 before its offset
    new GradientStop(0.62f, Scrim(0.32f)),
    new GradientStop(0.82f, Scrim(0.56f)),
    new GradientStop(1.00f, Scrim(0.66f)));  // HOLD through the card's own edge — no release
```

**Identical in both themes.** That is the point: the veil now terminates on the card's edge instead of on the page, so
it is **theme-neutral by construction** — the same reason a dark album cover needs no theme variant. A photo card with
a weighted lower third reads as an intentional duotone/vignette, which is the poster/cover idiom, not a page failure.

Contrast delivered to white 44px type at the copy band (0.82–1.0): over a mid photo (128) ⇒ backing ≈ 46 ⇒ **13:1**;
over a blown-out white press photo (240) ⇒ backing ≈ 86 ⇒ **7.9:1**. Both clear AAA. Note the *peak can now be higher
than 0.55* precisely because it no longer has to reach 0.

`GradientDown`, never `LinearGradient(180f)` — 180° is the **horizontal** axis and produced the documented sideways
dark band (`ArtistPage.Hero.cs:176-178`).

### 1.5 Composited mockup values

**Light** — page ≈ **246** (`#F6F6F6`):

| element | value |
|---|---|
| card interior, bottom | `Scrim(0.66)` over a mid photo ⇒ **44** (`#2C2C2C`) |
| hairline | **black @ 8%** (`#00000014`) over the photo edge — a *recede*. (`Tok.StrokeCardDefault` light is `#0000000F`, `PaletteBuilder.cs:363`; over 246 it reads ≈226) |
| shadow | `Elevation.CardLight` — blur 4, y 2, black@10% (`Elevation.cs:21`) ⇒ deepest ≈ **222** under the bottom edge |
| step across the card's bottom edge | 44 → ~222 (shadow) → 246, over 1px + 8px radius + 4px shadow ramp |

**Honest framing:** the luminance step is **not smaller** than today — it is ≈12.5:1 vs today's ≈7–9:1. Option A does
not reduce the step; it **licenses** it. The eye reads a bounded object's edge as an object boundary (this is the same
12:1 step every album thumbnail already makes against a light page); it reads an *unbounded* region changing luminance
as the page being broken. That distinction, not the ratio, is the fix.

**Dark** — page ≈ **40** (`#282828`):

| element | value |
|---|---|
| card interior, bottom | ⇒ **44** — i.e. essentially equal to the page |
| hairline | **white @ 10%** (`#FFFFFF1A`) — a *rim light*. Required: `Tok.StrokeCardDefault` in dark is **black**-alpha (`#00000019`, `PaletteBuilder.cs:254`) and is nearly invisible over a photo on a dark page. |
| shadow | `Elevation.CardDark` — blur 8, y 2, black@20% (`Elevation.cs:19`) |

So in dark the card reads by **radius + rim + shadow** alone, which is exactly how every dark-mode Fluent card reads.
The per-theme hairline (**black@8% light / white@10% dark**) is the one deliberately theme-keyed value in Option A, and
it is theme-keyed for the right reason: light cards recede from a bright page, dark cards catch light. `ArtistShyPill`
already wears `Tok.StrokeSurfaceDefault` (`#75757566`, theme-neutral grey, `ArtistShyPill.cs:75`) — usable if you want
the two hero states to share one literal token, at the cost of a weaker dark rim.

---

## 2. Option B — keep full-bleed, make the dissolve theme-aware

Four variants, steelmanned:

**B1 — invert the veil in light (white scrim + dark copy).** Terminates on ~246 ≈ the page ⇒ collision gone, and light
mode gets a light hero.
*Cost:* the hero's entire ink axis becomes theme-keyed. Call sites: `WhiteText` (`ArtistPage.Sections.cs:48`) at
`:63` title, `:243` bio, `:266-267` meta, `:366-369` `GlassPill` fill+ink, `:463` `Fab` glyph, `:95`
`FollowButton(…, WhiteText)`, `:474-477` `ArtistRadioPill`'s whole white ramp, `:382,392,397,444` `PinnedCard`.
~10 sites, ≈half a day.
*Why it's worse, not just costlier:* a 0.55 **white** veil over the lower half of a photo is a milk plate, and on the
common case — a bright/blown press photo — it delivers **no separation at all**. The black veil's failure mode
(too dark) is graceful; the white veil's (no contrast) is not. **Reject.**

**B2 — theme-aware terminal stops (black peak → light release in light mode).** *Blocked by the engine:*
`GradientSpec.MaxStops = 4` and all four are consumed (`ArtistPage.Hero.cs:187-191`); a fifth is silently dropped —
the documented failure that turned this veil into a hard-cut plate once already (`:170-174`). It would need a second
node, and black→white cannot cross without a grey mid-band that reads as fog. **Reject.**

**B3 — make the page below the hero dark in light mode (Spotify's actual answer).** Spotify never goes white, so its
hero never has to reconcile. Wavee cannot: an opaque dark plate under the content is the *explicitly forbidden* move —
*"the real background is a live Mica composite no constant colour can match, so any opaque bridge/flatten necessarily
draws a line where it ends"* (`ArtistPage.cs:306-309`). **Reject on canon.**

**B4 — move the copy off the seam (the real "Option B done right").** Keep the full-bleed banner; re-anchor the copy
to ~55–80% of the hero height (`Justify = FlexJustify.End` → a bottom padding of ≈0.20·h, or `Center`), re-stop the
veil to peak at 0.68 and fully release by 0.86, so the hero's last ~14% is **pure photo feathering to nothing**. Light
mode then transitions *photo → page*, never *black → page*.

| | B4 | A |
|---|---|---|
| files | `ArtistPage.Hero.cs` only | 3 + tests |
| effort | **~1h**, ~15 lines | ~1 day |
| palette churn | none | none |
| fixes (b)? | **yes** | yes |
| fixes (a) — the prototype? | **no** | yes |
| fixes the 160px hero/column misalignment? | no | yes |
| fixes the hero growing past 1600? | no | yes |
| does white copy still need protection? | **yes, and it still gets it** | yes, and more of it |

**B4 is the honest cheap answer** and it does resolve complaint (b). It does not resolve (a), and it leaves the veil's
peak coupled to the copy's position forever — every future copy change re-opens the seam question. It is the right
**fallback**, not the right answer.

**Direct answer to "can a light dissolve coexist with white copy?"** No. White copy over an arbitrary photo requires a
dark backing at the copy's location, full stop. The only ways out are: move the copy off the seam (B4), bound the
region so "dark here" is legal (A), or stop using white copy (A2 / §3.4).

---

## 3. Typography — why 48/700 white-on-photo reads poorly

Six independent, verifiable causes. Five are code-level; one is perceptual and labelled as such.

### 3.1 Wrong optical size — the largest single cause

`WaveeType.PageHero(a.Name)` → `Ui.Title` (`WaveeType.cs:19`, `Typography.cs:44`) sets **no `FontFamily`**, so the run
falls to the engine default **`"Segoe UI"`** (`Theme.cs:33` `BodyFont`; `TextLayoutEngine.cs:67` /
`GlyphRenderer.cs:142` `DefaultFamily`). That is the *legacy static UI-text* family, drawn for 9–14pt, being asked to
carry a 48px display headline: large x-height, generous sidebearings, blunt joins.

**The codebase already knows the answer** — the album/playlist immersive hero uses
`FontFamily = "Segoe UI Variable Display"` (`DetailVerticalHero.cs:157`, `:167`; also
`PlaylistInlineEdit.cs:408,469`). The Display cut is Segoe UI Variable's ≥24px optical size: tighter sidebearings,
finer joins, lower relative stem weight. The artist hero is the one large title in the app that does not use it.

### 3.2 Weight 700 is off the ramp — and lands on Bold

The Fluent ramp's ceiling is **600 SemiBold**, inherited by everything Subtitle-up
(`Typography.cs:9-13`, `:43-46` — `Subtitle`/`Title`/`TitleLarge`/`Display` are all 600). The hero overrides to **700**
(`ArtistPage.Hero.cs:63`). On the system path `GetFirstMatchingFont((DWRITE_FONT_WEIGHT)700, …)`
(`TextLayoutEngine.cs:947-950`) resolves real Segoe UI **Bold**; on the custom-file path any weight ≥600 gets
`DWRITE_FONT_SIMULATIONS_BOLD` (`:938`) — synthetic emboldening, which smears stems. Either way: maximal optical weight
at display size.

### 3.3 There is no gamma compensation, and its absence is asymmetric

Canon says text is the deliberate exception to linear blending — *"naive linear coverage blend makes thin stems too
thin"* (`gpu-renderer.md:761-776`; `text.md:589-591`; `SPEC-INDEX.md:56`). **The shipping renderer does not apply it,
on purpose:**

```
GlyphRenderer.cs:245
float a = gAtlas.Sample(gSamp, i.uv).r;   // grayscale coverage, used directly (no gamma boost — that thickened all text)
```

Pipeline: DWrite `CLEARTYPE_3x1` coverage averaged to grayscale (`:456-460`) → premultiplied `ONE / INV_SRC_ALPHA`
blend (`:1123-1130`) → a `BGRA8_UNORM_SRGB` RTV, i.e. **coverage composites in LINEAR space** (`gpu-renderer.md:764-766`).

That blend is **directionally asymmetric**: light-on-dark *gains* apparent weight, dark-on-light *loses* it. The boost
was removed because it "thickened all text" — i.e. it was tuned against the dominant case, dark ink on light surfaces.
Which means **white-on-photo is the un-compensated direction and is systematically over-inked**. At weight 700 the
headline renders as an effective ~750–800. This is the mechanism, and it is a *rendering* fact, not taste.

### 3.4 Why it's specifically worse in light mode (perceptual — labelled)

Nothing about the glyph changes with the theme. What changes is the **adaptation luminance of the surround**: in light
mode the eye is adapted to an L*≈97 field, so irradiation/halation on white glyphs against the dark hero band is at
maximum, and simultaneous contrast makes the band read as a heavy foreign plate. Combined with §3.3's over-inking, the
same headline reads *fat and glary* in light and merely *bold* in dark. **This part is an argument, not a measurement**
— the falsifiable findings are §3.1, §3.2, §3.3, §3.5, §3.6.

### 3.5 The line height is dead, so the leading is font-natural

`Ui.Title` pins `LineHeight = 36f` (`Typography.cs:44`). The hero overrides `Size` to 48 but **not** `LineHeight`
(`ArtistPage.Hero.cs:61-65`). Default `LineStacking.MaxHeight` makes the advance `max(natural, 36)`
(`Element.cs:515-519`; `TextLayout.cs:20-28`), and natural at 48px (~64) wins ⇒ a 2-line artist name is set at ≈1.33
leading. Slack for a display headline; the codebase's display recipe uses `LineHeight = size * 1.08`
(`DetailVerticalHero.cs:159`, `:169`).

**The trap:** setting `LineHeight = 48 * 1.08` alone does **nothing** — `MaxHeight` ignores any value below natural.
Tightening requires `LineStacking = LineStacking.BlockLineHeight` (`TextLayout.cs:27`).
**Second finding:** `DetailVerticalHero.cs:159,169` set 1.08 under the default `MaxHeight` and are therefore *also*
being ignored. Worth a separate one-line fix.

### 3.6 Zero tracking at 48px

`CharSpacing` (WinUI `CharacterSpacing`, 1/1000 em, `Element.cs:509-511`, `DrawList.cs:169`) is unset on the hero name,
while the page's 11px pills correctly use **+20** (`ArtistPage.Hero.cs:360`, `:369`). The small half of the optical
tracking law is applied; the large half is not. The display recipe uses **−28 at ≥34px, −16 below**
(`DetailVerticalHero.cs:169`).

### 3.7 The size ladder is character-count quantized

`HeroSize` (`:232-233`) steps 48/44/38/32 on name length, so 18 vs 19 characters costs 6px. The engine has continuous
auto-fit — `MinSize` (`Element.cs:577-583`), used at `DetailVerticalHero.cs:177`.

### 3.8 Recommendation — name ON the photo (pairs with Option A)

```csharp
Element title = new TextEl(a.Name)
{
    FontFamily  = "Segoe UI Variable Display",   // §3.1 — the app's own display recipe
    Size        = 44f, MinSize = 30f,            // §3.7 — replaces the 4-step HeroSize ladder
    LineHeight  = float.NaN,                     // §3.5 — MUST clear Ui.Title's 36; MinSize requires natural leading
    Weight      = 600,                           // §3.2 + §3.3 — the ramp ceiling; 700 compounded with the
                                                 //   un-compensated linear blend
    CharSpacing = -24f,                          // §3.6 — ≈ -0.024 em (the app's ≥34px value is -28)
    Color       = Tok.OnMediaPrimary,            // Tokens.cs:345 — use the token, retire the local WhiteText const here
    Wrap        = TextWrap.WrapWholeWords,        // currently TextWrap.Wrap ⇒ mid-word breaks on long names
    MaxLines    = 2, Trim = TextTrim.CharacterEllipsis,
};
```

`HeroSize` (`:232-233`) is deleted. **`MinSize` and an explicit `LineHeight` are mutually exclusive** —
`Element.cs:581-582`: *"Use a font-natural line height (leave LineHeight unset) so the chosen size's spacing scales
with it."* If the 2-line case still reads slack after this, the follow-up (not both at once) is a fixed
`Size = 44, LineHeight = 47, LineStacking = LineStacking.BlockLineHeight` and no `MinSize`.

`TextEl` has **no shadow property** (checked: `Element.cs:446-640`), so protection must come from the veil — which is
exactly why §1.4's stronger 0.66 peak pairs with dropping the weight to 600. *Contrast from the backing, not from
ink weight.*

Secondary ink, aligned to the same tokens: bio `Tok.OnMediaSecondary` (white@0.80, `Tokens.cs:347`) instead of the
hand-rolled `WhiteText with { A = 0.8f }` (`:243`); meta labels `Tok.OnMediaTertiary` (white@0.60, `:349`) instead of
`:267`.

### 3.9 Name ON the photo vs BELOW the card

**ON (recommended now, "A1").** Zero palette churn — every white call site in §2/B1 stays valid because the card's
interior is a photo. Keeps the immersive editorial voice. Needs the veil, which §1.4 makes legitimate.

**BELOW (Spotify-2024 / Apple Music, "A2" — the natural follow-up).** The photo becomes a clean **unveiled** image —
the scrim disappears entirely, which is the *fully* theme-neutral end state — and the name/meta/actions sit on the page
in `Tok.TextPrimary`, correct in both themes by construction. §3.4's halation problem largely evaporates because
dark-on-light is the direction the renderer was tuned for.
*Cost:* the whole copy column's colour axis swaps to theme tokens (~10 sites, though `FollowButton(uri, name)` already
defaults to theme ink — `SaveButton.cs:124,132`, and `HeroCta.Pill` already resolves via
`ColorContrast.PickContrast(_accent)` — `ArtistPage.Hero.cs:455`); the `overlay`'s bottom-anchored `Justify = End`
becomes a normal stacked block; the height ladder must shrink (the photo no longer houses ~260px of copy) and becomes a
pure aspect rule; the copy's scroll-dissolve bind (`:113-116`) moves out of the overlay; and the shy-pill arm point
shifts, because the copy now leaves the viewport *after* the photo — `sentinel`'s `PinTop = 40f`
(`ArtistPage.cs:302`) would fire too early. **~1 day**, and it changes the page's character.

**Tie to the A/B decision:** ship **A1** (card + name on photo). It fixes (a), (b) and (c) at the cost of one day and
zero palette churn. Name **A2** as the follow-up once the card's geometry has settled in the real app — it is the only
variant that deletes the scrim, but it re-opens every ink decision in the hero, which is a separate argument.

---

## 4. Recommendation

**Ship Option A1: the detached hero card (name on the photo), with the §3.8 typography.**

**Rationale.**
1. It is what was approved.
2. It resolves (b) **structurally** rather than by tuning: the veil terminates on the card's own edge, so it is
   theme-neutral by construction and no future copy or palette change can re-open the seam. Every theme-aware-dissolve
   variant is either rejected by the engine (4-stop cap, B2), by canon (opaque plate, B3), or by robustness (white veil
   on bright photos, B1).
3. It satisfies `ArtistPage.cs:303-309`'s seam contract **vacuously** — the strongest form.
4. It fixes two latent defects for free: the **160px hero↔column misalignment** above 1696px viewport, and the hero
   **growing past the 1600 content cap** on wide monitors.
5. It makes the hero independent of `DisableColorWashes` (`AppSettings.cs:46`) — today the two configurations produce
   materially different seams in light mode.
6. It speaks the Fluent object-card language the rest of the page already speaks (`Radii.Card` 8, hairline,
   `Elevation.Card`, the same edge vocabulary as `ArtistShyPill.cs:74-75`).

**Exact values, consolidated.**

| knob | value |
|---|---|
| gutter | `GutterFor(vw) = vw < 640 ? 20 : 48` — adopted by **both** the card and `inner` |
| card width | `min(vw, 1600) − 2·GutterFor(vw)` |
| card height | `HeroHeightFor(cardW)`, clamp ceiling **560 → 500** |
| radius | `Radii.Card` = 8 (`Radii.cs:12`) |
| hairline | light **`#00000014`** (black@8%) / dark **`#FFFFFF1A`** (white@10%), 1px |
| shadow | `Elevation.Card` (`Elevation.cs:18-21`) |
| card padding | `Edges4(32, 32, 32, 28)` |
| scrim | `GradientDown(0.34/0.00, 0.62/0.32, 0.82/0.56, 1.00/0.66)` — identical in both themes |
| photo `EdgeFade` | **deleted** |
| wash | height `h + 120`; light 0.20→0.12, dark 0.30→0.20; boundary stop 0.72 |
| title | Segoe UI Variable Display / 44 (MinSize 30) / 600 / `LineHeight = NaN` / `CharSpacing = -24` / `Tok.OnMediaPrimary` / `WrapWholeWords` / 2 lines |
| collapse | owner unchanged (`PinTop 0` + `PresentedHTrailing Px(0,h)`); card adds `PresentedH Px(0,h)` `OutStart = cardH, OutEnd = -6` |

**Implementation cost map.**

| file | change | effort | risk |
|---|---|---|---|
| `src/apps/Wavee/Features/Detail/ArtistHeroLayout.cs` | `+GutterFor`, `+CardWidthFor`; clamp ceiling 560→500; retire `PhotoFadeBandFor`; decide `ContentBlendTail`/`Blend*` fate | S | low |
| `src/apps/Wavee/Features/Detail/ArtistPage.Hero.cs` | the card wrapper; delete `EdgeFade`; re-stop `copyContrast`; typography; `HeroBioLine` width; `wide` gate + nested radius; rewrite the `:109-111` alignment comment | **M — the bulk** | medium (§1.3 #3 is the only novel behaviour) |
| `src/apps/Wavee/Features/Detail/ArtistPage.cs` | derived `_cardWidth`; wash re-role + alpha reduction; rewrite `:303-309`; `inner` gutter → `GutterFor`; `ArtistShimmer` hero parity | M | **medium — wash and shimmer are the two guaranteed regressions if skipped** |
| `src/apps/Wavee.Tests/ArtistHeroLayoutTests.cs` | −2 (`PhotoFadeBandFor`), update 3 (`UltraWide`), +2 (`CardWidthFor`, gutter parity) | S | low |
| `src/FluentGpu.VerticalSlice/Suites/AnimSuite.cs` | *optional* gate beside 23u2 (`:1827`): a rounded card driven by `PresentedH` keeps its corners and hairline through the collapse | M | low |

**Explicitly untouched.** `ArtistShyPill.cs` — the hand-off is sentinel-driven (`ArtistPage.cs:302`, `PinTop = 40f`)
and width-agnostic; **zero cost**. `HeroArt`'s internals (only its width *source* changes). `Design/Surfaces.cs` — the
retired `SectionBand` (`:220-243`) is **not** the model here: it is a *material* surface with an accent glow for
grouped content; the hero card is an **object card** whose content is a photograph. `HeroCta.cs`, `WaveeCta`,
`SaveButton.cs`/`FollowButton`, `PaletteBuilder.cs`, every engine file, and the whole palette.

**Both themes.** Only one value is theme-keyed (the hairline, §1.5), and for a stated reason. The scrim, the radius,
the shadow token, the geometry and the type are theme-invariant.

**Reduced motion.** The hero's motion is entirely **scroll-driven** `ScrollBinds` — parallax (`:161`), overscroll
stretch (`:142`), the copy/media dissolves (`:115`, `:143`, `:194`) and the presented-height collapse (`:223`). These
carry **no** reduced-motion gate (verified: no `ReducedMotion` reference in `ScrollBind.cs` / `ScrollBindEval.cs` /
`ScrollBindDsl.cs`), which is correct — they are direct manipulation, not animation. The two time-driven pieces are
`HeroArt`'s reveal (`ImageTransition.Fade(320)` + the `UseKeyframes` 1.0→1.05 settle, `:491-500`, `:545-548`) and
`ArtistShyPillCore`'s `LayoutTransition` (`ArtistShyPill.cs:35-40`), both already token-governed. **Option A changes
neither.** One new consideration: `StretchFromTop` now rubber-bands a **bounded object** rather than a full-bleed field,
which is more conspicuous — recommend gating it behind the existing appearance-prefs pattern
(`AppearancePrefs.cs`, `AppSettings.cs:46`) as a follow-up, since it is decorative by definition.

**Fallback if the card is rejected.** Take **B4** (§2): re-anchor the copy off the seam and re-stop the veil to release
by 0.86. One file, ~15 lines, ~1h, no palette churn — it resolves (b) and (c) but not (a), and it leaves the veil
permanently coupled to the copy's position.
