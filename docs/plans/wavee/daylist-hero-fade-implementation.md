# Daylist hero — artist-style fade (variant A chosen)

Status 2026-09-30: **the owner picked A (keep the card).** B stays below as the rejected alternative. Prototype:
`docs/plans/wavee/daylist-hero-fade-mica.html` (published privately at https://claude.ai/artifact/HjPqADDYYo1BbhbrTY7PoN).
Already approved and in flight separately (not part of this choice): the two-line countdown, the "next" caption named
from the next timeline segment, and the engine fix that makes finished dayparts fill their bars (the reconciler now
re-wires a bound `Func` that changes on re-render).

## Why

Today the card is a text column plus a separate 540-DIP art column (`Daylist.UI.cs` `Row`): the header art shows as a
cropped rectangle, and the text column is squeezed to ~392 DIP, which truncated the countdown line. The owner wants the
header art rendered in full behind the card the way the artist hero renders its photo: art full-bleed, a veil fading it
under the text.

## The artist hero, as built (what we reuse)

`Artist.UI.cs` `HeroBanner` (horizontal tiers): a ZStack of
1. `media` — the photo (`HeroArt`), `ClipToBounds`, `EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, PhotoFadeBandFor(h))`
   (28 % of the height, 120–180 DIP);
2. `veil` — `Palette.ArtistHeroVeil(url, vertical: false, w, h, key)` → `Controls.ArtistHeroVeil(accent, vertical)`:
   `veil = Lerp(FillLayerDefault, accent, 0.16 light / 0.24 dark)`, `GradientRight` stops `0 → .96`, `.30 → .92`,
   `.62 → .35`, `1 → 0`, graded from the image (a late grading swaps the gradient without rebuilding);
3. `copy` — the identity column, left-aligned, over the opaque side of the veil.

## Variant A — keep the card

```
┌ Ui.Card (border, 8 corners, padding 32/12/12/12, MinHeight 320) ─────────────┐
│ CoverFill(art) across the WHOLE card, clipped to the card corners   layer 1  │
│ Palette.ArtistHeroVeil(art, vertical:false, w, h)                   layer 2  │
│ ┌ copy: width clamp(340, 50 % of card, 560) ┐                        layer 3  │
│ │ eyebrow · title (≤2) · tags · meta         │                                │
│ │ [Play] [Shuffle] ♥ …                       │                                │
│ │ ◌ Next daylist in hh:mm:ss                 │                                │
│ │   {weekday} {next daypart} arrives at HH:mm│                                │
│ │ ████ ████ ██── ──── ────                   │                                │
│ └────────────────────────────────────────────┘                                │
└───────────────────────────────────────────────────────────────────────────────┘
```

```csharp
// Daylist.UI.cs — DaylistCard.Row(p, w) for variant A (replaces the text + art row)
static Element Row(Parts p, float w)
{
    float copyW = DaylistForm.CopyWidth(w);                 // pure: clamp(TextMin, 0.5 · w, CopyMax), unit-tested
    var copy = new BoxEl
    {
        Direction = 1, Width = copyW, MinWidth = 0f, Shrink = 0f,
        Justify = FlexJustify.SpaceBetween, Padding = Daylist.TextPadding, Gap = Spacing.XL,
        Children = [Top(p, w), p.Clock],
    };
    return new BoxEl
    {
        ZStack = true, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Children =
        [
            // Art and veil stretch to the card's content box; the card's own ClipToBounds + Corners clip them.
            Controls.CoverFill(p.ArtUrl, 0f, Daylist.ArtDecodePx, focusY: Daylist.ArtFocusY) with { AlignSelf = FlexAlign.Stretch },
            p.ArtUrl is null ? new BoxEl() : Palette.ArtistHeroVeil(p.ArtUrl, vertical: false, w, h: Daylist.CardMinHeight,
                                                                     key: "daylist-veil:" + p.ArtUrl),
            copy,
        ],
    };
}
// Shape(parts): the card gains ClipToBounds = true; its padding moves inside `copy` (the art must reach the card
// edges), i.e. Card padding 0 and copy Padding = CardPadding + TextPadding.
```

Open points for A: `ArtistHeroVeil` takes a fixed height — the card's height is content-driven (≥ 320), so either
measure it (`UseMeasuredHeight` in a tiny wrapper) or let the veil stretch (give `CoverKeyedVeil` a stretch arm). The
art decode width grows from 512 to the card width (`ArtDecodePx` → ~1024). → Resolved: see *As implemented (A)* at the
end.

## Variant B — borderless, bleeds into the page

```
  (Home content width, no card frame, gutter-aligned copy)
  art full width, EdgeFade Bottom 28 % into the page ground            layer 1
  ArtistHeroVeil horizontal, same stops                                layer 2
  copy at the page gutter, same width rule as A                        layer 3
  ─ the art fades out ~100 DIP above "Recently played" ─
```

```csharp
// Daylist.UI.cs — DaylistCard.Shape for variant B (no Ui.Card)
static Element Shape(Parts parts)
    => Responsive.Of(w => new BoxEl
    {
        ZStack = true, Width = w, MinHeight = Daylist.BleedMinHeight,           // 360
        Children =
        [
            new BoxEl
            {
                ZStack = true, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, ArtistHeroLayout.PhotoFadeBandFor(Daylist.BleedMinHeight)),
                Children =
                [
                    Controls.CoverFill(parts.ArtUrl, 0f, Daylist.ArtDecodePx, focusY: Daylist.ArtFocusY),
                    Palette.ArtistHeroVeil(parts.ArtUrl, vertical: false, w, Daylist.BleedMinHeight, key: "daylist-veil"),
                ],
            },
            Copy(parts, w) with { Padding = new Edges4(HomeModuleLayout.Gutter, Spacing.M, HomeModuleLayout.Gutter, Spacing.XXL) },
        ],
    }, fallback: HomeModuleLayout.FallbackWidth, grow: 1f);
```

B differs from A in: no card frame or corners, the art spans the Home content width (the module's own gutter
inset is dropped for this zone only), and the bottom fades into the page like the artist hero. B also needs the
card-as-button affordance re-thought (today the whole card is a Tile-interactive click target; a borderless band as one
big button reads oddly) — proposal: the title becomes the link, the band itself is not a button.

## Both variants

- The skeleton (`DaylistCard.Skeleton`) keeps using the same builders: art slot = the placeholder fill, no veil.
- Narrow (`!DaylistForm.ShowArt(w)` today): the copy takes the full width and the veil flattens to ~92 %→70 % so the art
  still tints the card; no art column exists any more, so `DaylistForm.ShowArt`/`ArtBasis`/`ArtMin` are deleted.
- Tests: `DaylistForm.CopyWidth` (pure) replaces `TextMinFor`/`ShowArt` tests; no source-text tests.
- Dark theme: the veil's pull is 0.24 (`Controls.ArtistHeroVeil`), already theme-aware.

## As implemented (A) — 2026-09-30

- **Veil height: the stretch arm.** `CoverKeyedVeil` (Design.cs) takes `float.NaN` width + height as its STRETCH arm:
  the component anchor mirrors the NaN extent, so the card's ZStack hands it the whole slot, and the root gets `Grow 1`
  (only when the height is NaN) to fill the anchor's column. The sized arm (the artist hero) is byte-for-byte the old
  box (`Grow 0`). Still cover-keyed: a late grading re-keys the tone child, never the card. No measuring wrapper.
- **Card** (`DaylistCard.Shape`): `Ui.Card` with `Padding = default`, `ClipToBounds = true` (8-DIP rounded clip; the
  engine's tier-2 rounded clip covers images and gradients, and a box border paints after its children, so the border
  stays on top of the art), `MinHeight 320`. `Row` is a ZStack `[art, veil, copy]`; an absent layer is an empty
  hit-test-free box and the copy is keyed (`daylist-copy`), so late art never remounts the copy and its clock.
- **Copy**: `Daylist.CopyPadding = (32, 32, 12, 24)` — exactly the old card padding + text padding. Width =
  `DaylistForm.CopyWidth(inner) + 44`, `inner = cardWidth − 44`; `CopyWidth = inner < SplitMin (532) ? inner :
  clamp(0.5 · inner, TextMin 340, CopyMax 560)`. The title rung reads the copy width (`UseHeroTitle(CopyWidth)`).
- **Art**: `Controls.CoverFill(url, 0, ArtDecodePx 1024, focusY 0.4)`. `CoverFill` has no X focus, so the crop stays
  centred horizontally (`ImageEl.FocusX` exists if we want the prototype's 62 % later — one optional parameter).
- **Veil**: `Palette.ArtistHeroVeil(url, vertical: false, NaN, NaN, key: "daylist-veil:" + uri, payloadAccent:
  card.Accent)` — keyed on the card (the surface), not the url; the card's accent is the payload rung until grading.
- **Narrow**: the horizontal veil as is (no flatter arm exists; none invented), so on a narrow card the text's right
  side sits on the veil's thin end.
- **No url**: no art, no veil — the plain card. **Skeleton**: `ArtLayer(null)` (the placeholder art slot) and no veil.
- **Deleted**: `DaylistForm.ShowArt/TextMinFor/TextWidth/Gap/ArtMin/ArtBasis`, `Daylist.TextMinWidth/ArtBasis/
  CardPadding/TextPadding`. Tests: `DaylistFormTests` `CopyWidth_*` (floor, half, max, narrow, split boundary) and
  `Title_*` replace the `ShowArt`/`TextMinFor`/`TextWidth` facts.
- **Open**: the `Interaction.Tile` hover/press ramp is the card's own fill, which the full-bleed art now covers, so the
  card shows no hover/press feedback wherever the art is (the border ramp is flat for Tile).
