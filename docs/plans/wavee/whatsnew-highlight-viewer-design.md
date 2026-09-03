# What's-new highlights — the short card and the viewer

Design spec. No code in this document has been built; every number below is argued, and the implementer owns the
build (`HighlightCard.cs`, `HighlightStrip.cs`, `AfterUpdateDialog.cs`, `ReleaseNotesPage.cs`, one new
`HighlightViewer.cs`, one new pure `HighlightViewerLayout` for the tests).

The complaint: a highlight card is as tall as its body paragraph. The 0.2.6 "Logs tab" body is 370 characters, which is
13 wrapped lines at the dialog's card width, so the three-up row runs to ~640 DIP and blows through the dialog's 620
cap. The fix is not a smaller font or a shorter paragraph: the card gets a **fixed text block** that fades out where it
ends, and the paragraph moves to a **viewer** that shows it whole, with the poster large, one highlight at a time.

## The decision

- **Body slot is 68 DIP, always:** four lines of 12.5 px body at an explicit 17 px line height, clipped, no
  `MaxLines` — the overflow is measured, not estimated.
- **Edge fade is 24 DIP** over the bottom of that slot, `Tok.FillSolidTertiary` α 0 → α 1, drawn with opacity bound
  to an "overflows" signal; a body that fits has no fade, a body that overflows has its last line ghosted.
- **Card fill goes opaque** (`Tok.FillSolidTertiary`) and stays flat on hover — a fade can only end in a colour that
  is actually there, and the stock 5 %-white card veil is not a colour. Hover is the stroke and the label, not the fill.
- **Title is 2 lines max** (13.5 px / 18 px line height, character ellipsis); a card's text block is therefore 150 DIP
  (132 with a one-line title), the store card's 170.
- **Poster band stays 16:9 and fluid.** The dialog's lone card narrows to 356 DIP (`CompactCardMaxW`, replacing
  `CompactMediaMaxH`) so its band is 200 DIP with no crop; three-up bands are 122, two-up 185, the page's ≤ 236.
- **Dialog height budget:** 151 hero + 292 card row (6 + 272 + 14) + 62 footer = **505 DIP** for the three-up case;
  the worst case (a lone store card, two-line tagline) is 603. Everything fits under 620 with the row unchanged.
- **The viewer is a centred plate, image above text**, width `W = clamp(320, min(960, vpW − 96, (vpH − 360) × 16⁄9))`,
  image `W × 9⁄16`, text below it in a scrolling column; `PipsPager` dots between image and text; prev/next as 36 DIP
  on-media circles inside the image band; Left/Right/Home/End/Esc.
- **The prev/next circles never move.** Both are always rendered at a fixed position in the band; at an end the
  button dims in place (opacity 0.3, not hit-testable) instead of being removed — chrome that disappears and comes
  back as you page reads as the chrome jumping, not the slide.
- **Slides change with a 24 DIP direction-aware slide + cross-fade (250 ms, `Easing.SmoothOut`)**; the plate opens
  with the stock `PopupChrome.Modal` scale/fade. No shared-element fly — not worth it here (§B.6).
- **One component, two hosts:** `HighlightViewer.Open(overlay, items, index, nav, closeHost)`; the dialog passes its
  own `close` as `closeHost` so "Try it" leaves both plates, the page passes `null`.

---

## A. The shortened card

### A.1 Geometry

```
                          dialog 3-up   dialog 2-up   dialog lone   page (≤ 420)
card width                216           329           356           ≤ 420
poster band (16:9)        122           185           200           ≤ 236
text block (2-line title) 150           150           150           150
text block (store card)   170           170           170           170
card height (regular)     272           335           350           ≤ 386
card height (store)       292           355           370           ≤ 406
```

Card widths in the dialog come from the plate: 720 − 2 × 26 padding = 668; three-up `(668 − 2 × 10) ⁄ 3 = 216`,
two-up `(668 − 10) ⁄ 2 = 329`, lone = `CompactCardMaxW` 356.

**Poster band.** 16:9 survives. The band's own comment is right: release images are authored 1200 × 675, and a fixed
pixel height would letterbox a wide card and crop a narrow one. At 216 wide the 16:9 band is only 122 DIP — the band
was never what made the card tall. The one change is the dialog's lone-card cap: `CompactMediaMaxH = 236` becomes
`CompactCardMaxW = 356`. A width cap makes the band 200 DIP *at its authored proportion*; a height cap would have
had to crop. 356 still reads as a card in a 668-wide row (it is the two-up width plus a margin), and only the lone
card ever reaches it. The page keeps `CardMaxW = 420` and no cap — nothing there is height-constrained.

**Title.** 13.5 px / weight 600 as today, plus `LineHeight = 18`, `MaxLines = 2`, `Trim = CharacterEllipsis`. Two
lines because "Report a problem from inside Wavee" wraps at 216 wide and a one-line ellipsis would cut the verb
off; three because no authored title is that long and a third line is 18 DIP the body needs more. The line height
is explicit so the block's height is arithmetic (18 or 36), not a font-metric guess.

**Body slot.** A `BoxEl` of fixed `Height = 68` (4 × 17), `ClipToBounds`, `ZStack`. Inside it the body paragraph
is rendered **unclamped** (`MaxLines = 0`, `LineHeight = 17`, 12.5 px, `Tok.TextSecondary`) inside a measuring
wrapper; the slot clips whatever falls below line 4. Four lines because at 216 wide that is ~128 characters — one
whole sentence of every 0.2.6 body — and at 420 wide ~260 characters, two sentences; three lines cut the Logs body
inside its first clause, and five is 17 DIP the row does not need once the viewer exists. 17 px line height: the
font-natural box for 12.5 px Segoe UI Variable is ~16.6 and an explicit integer is what makes the overflow test in
§A.2 exact.

**"Read more" row.** Below the slot: `Margin top 4`, a 12 px / 600 label (`whatsNew.readMore`, new key) and
`Icons.ChevronRight` at 10 px, `Gap 4`, height 16. `Tok.TextSecondary` at rest, `Tok.AccentTextPrimary` under the
card's hover/focus (a `TextEl.HoverColor` eases with the nearest interactive ancestor's hover progress — no extra
state). The label exists for the cards whose bodies *fit*: without it a short card gives no cue that a click opens
anything, and a keyboard user needs a name on the target. It replaces the page card's "Try it →" line, which moves
into the viewer (§B.5) so the card has exactly one meaning: open.

**Text block arithmetic** (padding 12 / 10 / 12 / 12 kept):

```
regular:  10 + 36 (title) + 4 + 68 (slot) + 4 + 16 (label) + 12 = 150   (132 with a one-line title)
store:    10 + 36         + 4 + 68        + 4 (pad)  + 4 + 32 (button) + 12 = 170
```

The row's `AlignItems = Stretch` (already there) equalises card heights when titles differ by a line; the slack goes
under the label. A row's height is therefore the tallest card's, which is bounded by the table above.

### A.2 The edge fade

A `BoxEl` in the slot's ZStack: `Height = 24`, `AlignSelf = End` (anchored to the slot's bottom edge — the lightbox's
filmstrip idiom), width fluid, `HitTestVisible = false`,

```
Gradient = GradientDown(new GradientStop(0f, Tok.FillSolidTertiary with { A = 0f }),
                        new GradientStop(1f, Tok.FillSolidTertiary))
```

Both stops share the card's RGB, so the ramp is a pure alpha ramp with no hue drift mid-fade. Geometry, with the slot
at y = 0..68 and lines at 0/17/34/51: the fade spans y = 44..68. Line 3's baseline (~y 47) sits at α ≈ 0.12 — untouched
to the eye; line 4 runs from α 0.29 at its top to α 0.83 at its baseline — legibly ghosted, unmistakably "there is
more". 24 rather than 34 (two lines) because the fade should take one line, not two: two dissolved lines out of four
reads as a rendering fault.

**Why the card fill goes opaque.** `Interaction.Card` paints `Tok.FillCardDefault` (white @ 5 %) over whatever is
beneath — the dialog's `FillSolidBase` plate, or the page's Mica-over-wallpaper — and cross-fades to `FillCardSecondary`
(white @ 3 %) on hover. No gradient end-stop can equal a translucent veil on two different backdrops, and a stop that
matched the rest state would show a 2 % seam on hover. So the highlight card is the one card in Wavee with an opaque
fill: `Tok.FillSolidTertiary` — in dark theme the canvas lightened 6 %, i.e. visually what the 5 % veil produced over
the plate; in light theme `#F9F9F9`. It is an existing token, used by no card today; flag it in the file comment. The
`Interaction.Card` preset is therefore not applied; the card carries its own flat recipe (§A.3).

**No fade on a body that fits.** The fade's `Opacity` is bound to a per-card `Signal<bool> overflows`, with
`Transition = MotionTok.ControlFaster` (83 ms, KeepFade). The signal is set from the measuring wrapper's
`OnBoundsChanged`: `overflows.Value = r.H > 68.5f`. The wrapper is a `BoxEl { AlignSelf = Start, Shrink = 0 }` around
the paragraph so it takes its natural laid-out height instead of stretching to the slot (the ZStack anchoring rule the
lightbox relies on). Because the line height is an explicit 17, natural heights are 17·n — 51, 68, 85 — never within
half a pixel of the threshold, so "exactly four lines" is decided correctly (§E.8). A window resize on the page reflows
the paragraph, `OnBoundsChanged` fires again, and the fade eases in or out over 83 ms instead of popping.

This is why the card becomes a small `Component` (`HighlightCardView`, `Embed.Comp` keyed by highlight id) rather than
a static builder: the signal needs an owner. Props freeze at mount, which is fine — a `HighlightItem` is immutable and
the open callback captures the index.

### A.3 The click target, hover, focus

The card frame is a column of two siblings — **hit region** and, on the store card only, **footer** — so a card never
nests a button inside a button (the constraint `HighlightCard.cs` already states).

- **Hit region** = poster band + title + slot (+ label on regular cards). `Role = AutomationRole.Button`,
  `Focusable = true`, `Cursor = Hand`, `OnClick = open`, `PressScale = 0.985`, `Transition = MotionTok.StandardSpring`
  (the `Interaction.Card` press value and spring — the geometric press is the part of that preset worth keeping).
- **Frame**: `Fill = Tok.FillSolidTertiary`, `BorderWidth 1`, `BorderColor = Tok.StrokeCardDefault`
  (store: `Tok.AccentDefault`), `HoverBorderColor = Tok.StrokeSurfaceDefault` (store: unchanged),
  `BrushTransitionMs = WaveeMotion.Faster`. Hover = the stroke lifts and the label turns accent. No hover scale: three
  scaling cards in a 10 DIP-gap row shimmer against each other, and `Interaction.Card` has none either.
- **Keyboard focus**: the engine's standard ring (`Tok.FocusOuter` / `Tok.FocusInner`, `Tok.FocusThickness`) on the
  hit region; Enter/Space opens. The label reads accent while focused (`FocusedColor`).
- **Store card**: hit region has *no* label row (its bottom padding drops to 4); the footer holds
  `Button.Accent(whatsNew.storeCta)` at padding 12 / 4 / 12 / 12 — a real button, a sibling of the hit region, exactly
  one click target each. The hit region still opens the viewer (fade, hover stroke, focus ring all apply); the
  automation name of a store hit region is its title. The store card keeps its accent stroke and accent pill, so it
  still reads as the strip's headline.

### A.4 Dialog height budget

```
hero:     22 pad + 22 pills + 8 + 35 (26 px welcome) + 8 + 19 (14 px tagline, 1 line) + 18 pad = 132   (151 if the tagline wraps)
row:      6 pad + card + 14 pad
footer:   14 + 32 buttons + 14 + 1 + 1 border = 62

3-up regular    151 + (6 + 272 + 14) + 62 = 505
3-up w/ store   151 + (6 + 292 + 14) + 62 = 525
2-up            151 + (6 + 335 + 14) + 62 = 568   (588 with a store card)
lone regular    151 + (6 + 350 + 14) + 62 = 583
lone store      151 + (6 + 370 + 14) + 62 = 603   ← worst case, ≤ 620
```

The plate's `MaxHeight = 620` stays as the backstop; nothing reaches it.

---

## B. The viewer

### B.1 Layout

**Image above text, centred plate.** Not image-left/text-right: a 16:9 poster beside a text column needs ≥ 1100 DIP
of window before the image is 640 wide, and a 370-character body makes the text column taller than the image, leaving
the poster floating beside a wall of words. Stacked, the poster takes the plate's full width — the thing the user
asked to see "big" — and the paragraph reads as its caption. Every authored asset is 16:9, so the band is exactly
`W × 9⁄16` with `Fit = Cover` (which equals Contain here).

```
W = clamp(320, min(960, vpW − 96, (vpH − 360) × 16⁄9))
```

- 960: a 1200-wide poster shown at 960 is a 0.8× downsample — sharp, never upsampled; wider plates make the text
  measure absurd.
- `vpW − 96`: 48 DIP of scrim each side so the plate reads as a plate, not a page.
- `(vpH − 360) × 16⁄9`: the image must leave 360 DIP for pager (36) + text block (≤ 260 for a six-line body with a CTA)
  + 64 vertical margin. At 900 tall this yields exactly 960; at 700 → 604; at 600 → 427.
- 320 floor: below it the image is unreadable (180 tall); the text column scrolls instead of the image shrinking.

Plate: `Fill = Tok.FillSolidBase` (opaque, the dialog plate's material), `Corners = Radii.Overlay`, `BorderWidth 1`,
`BorderColor = Tok.StrokeSurfaceDefault`, `MaxHeight = vpH − 64`, `ClipToBounds`. Vertically centred in the window.
Root behind it: `Fill = rgba(0,0,0,0.72)` with `PopupOptions.ScrimVisual = false` — one authored veil instead of the
Modal chrome's 30 % smoke stacked on ours, and the same darkness over both hosts. (A local literal, not a token —
`Tok.MediaScrim` is 0.55 and reads too thin over the dialog's own smoke.)

Inside, top to bottom:

1. **Image band** `W × 9⁄16`: `ImageEl { Source = poster, Fit = Cover, AspectRatio = 16⁄9, DecodePx = 1200,
   Corners = (8, 8, 0, 0), Placeholder = Tok.FillSubtleSecondary, RevealTransition = ImageTransition.Fade(140) }`.
   No poster → a 120 DIP tinted plate (`Tok.FillSubtleSecondary`, store `Tok.AccentSubtle`) instead of a 16:9 void.
   The kind pill sits at the band's top-left (8 DIP inset, as on the card); the chrome buttons sit over the band (§B.2).
2. **Pager row** (count ≥ 2 only): `PipsPager.Create(count, selected, onChange)`, centred, `Margin top 12`, 24 tall.
3. **Text column**: `ScrollView` with `Grow 1, Shrink 1, MinHeight 0`; padding 24 / 16 / 24 / 24 (top 8 when the pager
   is present); content `MaxWidth 720`, left-aligned:
   - title 20 px / 600 / `LineHeight 28` / `Tok.TextPrimary`, unlimited wrap;
   - body `RichTextBlock.Paragraph(markdown-lite)` 14 px / `LineHeight 20` / `Tok.TextSecondary`, selection on,
     `Margin top 8` — the same spans the card uses (bold, code, links), so a link in a body is clickable here too;
   - actions row `Margin top 16`, `Gap 8`, only when there is one (§B.5).

Text scrolls only when the plate would exceed `vpH − 64`; with the `W` rule above, that happens for bodies longer than
six lines at narrow widths — never for the image.

### B.2 Moving between highlights

- **Prev / next**: 36 DIP circles inside the image band, vertically centred, 12 DIP from its left/right edges.
  `Fill = Tok.MediaScrim`, hover fill `Tok.MediaScrim with { A = 0.70 }`, `Icons.ChevronLeft` / `ChevronRight` at 16 px
  `Tok.OnMediaPrimary`, `HoverScale/PressScale = WaveeMotion.ScaleEmphatic` (the on-artwork circle tier),
  `Role = Button`, `Cursor = Hand`. This is the play glyph's own language, so the band's chrome is one family.
  **Clamped, no wrap** (the WinUI FlipView rule): a Right press on the last slide does nothing — a silent jump back
  to the first feels like a bug, and the dots already say where you are.

  **Both circles are always rendered, in a fixed position.** At an end the button goes *disabled*, not away:
  `Opacity 0.3`, `Fill = MediaScrim with { A = 0.40 }`, `HitTestVisible = false`, `Focusable = false`,
  `HoverScale/PressScale = 1`, with the opacity easing over `MotionTok.ControlFaster` (83 ms) so the state change is a
  dim, not a pop. Removing the end button was the first draft and it is wrong: paging the viewer made a 36 DIP circle
  disappear and come back under the pointer, which reads as the chrome moving rather than as the slide moving. A
  control that is in the same place on every slide is the whole point of chrome.
- **Close**: a 36 DIP circle of the same style with `Icons.ChromeClose` at 12 px, top-right of the band at 12 / 12.
- **Click zones**: the scrim outside the plate closes (light dismiss). Clicking the image does nothing except on a
  video highlight (§E.7). No image-thirds paging — with ≤ 3 slides and visible arrows it buys nothing and steals the
  video link's click.
- **Keyboard** (root `Focusable`, `OnKeyDown` on the root — the lightbox pattern): `Keys.Left` / `Keys.Right` step,
  clamped; `Keys.Home` / `Keys.End` jump; `Keys.Escape` is the overlay's (Modal chrome closes on it). Tab order:
  root → close → prev → next → pager → CTA → (video image link); `FocusTrap = true` keeps it inside. A dimmed end
  button drops out of the tab order (`Focusable = false`) but keeps its slot in the layout, so the order is otherwise
  identical on every slide.
- **Position**: dots, via the stock `PipsPager` — 1–3 items is what dots are for, the control is keyboard-roving
  and carries `AutomationRole.Pager`. No "2 of 3" caption: with three dots a counter is a second copy of the same fact.
  The "2 of 3" text belongs in the automation name (§B.7) only.
- **Wheel / touchpad**: not paged. A vertical wheel over the text column scrolls it; a horizontal swipe is out of scope
  (no gesture arbitration to build for three slides).

### B.3 The slide transition

The slide (image band + pager + text column) is one keyed subtree, `Key = "hv:" + highlight id`. Changing the index
remounts it, and the outgoing/incoming subtrees animate with a `LayoutTransition` in the `MotionRecipes.PageSlide`
shape but at the WinUI entrance distance:

```
SlideForward: Channels Position | Opacity, Tween(Expressive.Fast = 250 ms, Easing.SmoothOut),
              Enter (Dx +24, Opacity 0), Exit (Dx −24, Opacity 0)
SlideBack:    the mirror (Enter −24, Exit +24)
```

Direction is `next > current`. 24 DIP is `Motion.EntranceOffsetPx` — the 8 DIP of `PageSlide` is a page-level
nudge, and a slideshow should visibly *move*. No blur on the slide root (a blur group over a 960 × 540 image is the
GPU cost the `PageSlide` comment warns about).

**Reduced motion**: the reconciler skips Enter/Exit tracks entirely under `Motion.ReducedMotion` (`Reconciler.cs`
gates both on `!Motion.ReducedMotion`), so the swap is instant. That is acceptable: the plate, the pager and the
chrome do not move; only the content changes and the selected dot steps. The `ImageEl` reveal fade (140 ms) is an
image-load transition and still runs — it is orientation, not motion.

### B.4 Open and close

`PopupChrome.Modal` supplies the ContentDialog scale/fade on open and close for free, and the card's 0.985 press is
the "it came from here" cue. That is the whole open transition.

**Shared-element fly — not worth it here.** The engine has the facility (`Element.MorphId` → the ConnectedAnimation
registry, `MotionTok.ConnectedFly`), but it is documented for route changes, not for an overlay mounting over its
source; the source band is 216 × 122 and the target 960 × 540 — a 4.4× scale across a scrim that appears at the same
time reads as a smear, not a hero; and the card is *under* the scrim while it flies. The cheap version is the good
version. If someone spikes `MorphId` across an overlay open and it takes under an hour, the poster (`MorphId = "hl:" +
id` on both `ImageEl`s) is the only participant worth tagging; the text never flies.

### B.5 Kind pill, "Try it", the store CTA

- **Kind pill**: same `KindPill` at the image band's top-left, 8 DIP inset — the card and the viewer share the band
  vocabulary, and it is the only place the pill can go without becoming a title eyebrow.
- **"Try it →"** (`whatsNew.tryIt`, existing key): `Button.Accent` in the actions row when the highlight has an
  `Open` deep link. On click: `nav(route, arg)`, then `closeHost?.Invoke()`, then close the viewer — in that order so
  the shell has navigated before either plate starts its exit. The card's "Try it" text line is removed (§A.1).
- **Store CTA**: `Button.Accent(whatsNew.storeCta)` in the actions row of a store slide; opens the listing
  (`OpenStoreListing`, unchanged) and leaves the viewer open — the Store app takes focus anyway, and the user may want
  to keep reading. `HighlightVisibility` already hides the store highlight on Store installs, so the viewer never
  sees it there.

### B.6 Focus, roles, announcements

- Initial focus: the plate root (`Focusable`, first in tree order, so the overlay's first-tab-stop rule lands on it),
  not the close button — a modal that greets a screen reader with "Close, button" has said nothing.
- Roles: root `AutomationRole.None` (there is no `Dialog` role yet — engine gap, see below), chrome circles `Button`,
  pager `Pager` (from the control), CTA `Button`, the video image `Hyperlink`, the card's hit region `Button`.
- **Engine gap, stated plainly:** `AutomationRole` is the only accessibility property an `Element` carries today —
  no automation name, no live region. The names the future UIA layer should expose are specified so the loc keys
  exist now: card hit region = `"{title}. " + readMore`; viewer root = `title + ", " + viewer.position(index, count)`;
  prev/next = `viewer.previous` / `viewer.next`; close = `viewer.close`. A slide change should announce the root's
  name again. Until the layer exists, the visible title and dots are the announcement.

### B.7 One component, two hosts

```csharp
static class HighlightViewer
{
    public static OverlayHandle Open(IOverlayService overlay, IReadOnlyList<HighlightItem> items, int initial,
                                     Action<string, string?>? nav, Action? closeHost);
}
```

Opened the way `ArtistPage.OpenGallery` opens the lightbox: `overlay.Open(static () => NodeHandle.Null,
() => Embed.Comp(() => new HighlightViewerView(...)), FlyoutPlacement.BottomCenter,
new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Modal)
{ ScrimVisual = false })`. Light dismiss (not Modal) because clicking the scrim of a viewer is how people close
viewers; Escape closes either way.

- **Dialog host** (`AfterUpdateDialog.Plate`): `UseContext(Overlay.Service)` resolves inside the plate (it is itself
  overlay content); `open = i => HighlightViewer.Open(overlay, cards, i, nav2, close)`. `nav` becomes
  `Action<string, string?>` end to end (`AfterUpdateChrome` currently narrows it to one argument — widen it) so a deep
  link's arg survives. The viewer stacks over the dialog; closing it returns focus to the card that opened it (the
  overlay host's focus restore).
- **Page host** (`ReleaseNotesPage`): `open = i => HighlightViewer.Open(overlay, view.MergedHighlights, i, go, null)`;
  `HighlightStrip.Create(highlights, open)` replaces its `nav` parameter — the strip no longer navigates, it opens.

`HighlightCard.Create(item, open)` / `Compact(item, open)` both take `Action open`; the only difference left between
them is `CompactCardMaxW`.

---

## C. Motion and tokens

| What | Value | Why |
|---|---|---|
| Card press | `PressScale 0.985`, `MotionTok.StandardSpring` | `Interaction.Card`'s own values — the physical-card press, kept |
| Card stroke / label hover | `BrushTransitionMs = WaveeMotion.Faster` (83) | the WinUI brush cross-fade rung |
| Fade in/out on overflow change | `Opacity` bound, `Transition = MotionTok.ControlFaster` | 83 ms KeepFade; a resize must not pop the fade |
| Viewer open/close | `PopupChrome.Modal` | ContentDialog scale/fade, engine-owned |
| Slide change | Tween 250 ms `Easing.SmoothOut`, Dx ±24, opacity 0→1 | `Expressive.Fast` + `Motion.EntranceOffsetPx`; reduced motion = instant swap |
| Poster reveal | `ImageTransition.Fade(140)` | the lightbox's value |
| Chrome circles | `WaveeMotion.ScaleEmphatic` hover/press, fill 83 ms | the on-artwork circle tier; collapses to 1 under reduced motion by construction |
| Pager | stock `PipsPager` | its own WinUI motion |

Tokens used (all existing): `Tok.TextPrimary`, `Tok.TextSecondary`, `Tok.TextOnAccentPrimary`, `Tok.AccentTextPrimary`,
`Tok.AccentDefault`, `Tok.AccentSubtle`, `Tok.FillSolidBase`, **`Tok.FillSolidTertiary`** (first card use — flag in the
file comment), `Tok.FillSubtleSecondary`, `Tok.FillSolidBase`, `Tok.StrokeCardDefault`, `Tok.StrokeSurfaceDefault`,
`Tok.MediaScrim`, `Tok.OnMediaPrimary`, `Tok.FocusOuter` / `Tok.FocusInner` / `Tok.FocusThickness` (engine ring),
`Radii.Card`, `Radii.Overlay`, `Radii.Pill`, `Spacing.S` / `M` / `L` / `XXL`, `Motion.EntranceOffsetPx`,
`Expressive.Fast`, `WaveeMotion.Faster`, `WaveeMotion.ScaleEmphatic`, `MotionTok.ControlFaster`,
`MotionTok.StandardSpring`.

Literals that are **not** tokens and should stay local constants with a comment: the viewer veil `rgba(0,0,0,0.72)`,
the chrome hover fill `MediaScrim with { A = 0.70 }`, the fade height 24, the slot height 68, line heights 17 / 18 /
20 / 28, plate max 960, margins 96 / 360 / 64.

New loc keys (`assets/loc/en-US.json`, under `whatsNew`): `readMore` "Read more"; `viewer.close` "Close";
`viewer.previous` "Previous highlight"; `viewer.next` "Next highlight"; `viewer.position` "{index} of {count}";
`viewer.watch` "Watch the video on GitHub". Reused: `tryIt`, `storeCta`, `kind.*`.

---

## D. Wireframes

Scale: 1 column ≈ 6 DIP, 1 row ≈ 17 DIP unless stated.

### D.1 The card — dialog three-up width (216) and page width (420)

```
216 wide                                  420 wide
┌────────────────────────────────────┐    ┌──────────────────────────────────────────────────────────────────────┐
│ [New]                              │    │ [New]                                                         (▶)     │
│                                    │    │                                                                      │
│          POSTER 216 × 122          │ 122│                        POSTER 420 × 236                              │ 236
│          16:9, Cover, r8 top       │    │                        16:9, Cover, r8 top                           │
│                                    │    │                                                                      │
│                                    │    │                                                                      │
├────────────────────────────────────┤    │                                                                      │
│ A real Logs tab                    │ 18 │                                                                      │
│                                    │ 18 │                                                                      │
│ Settings › Logs is a full-height   │ 17 ├──────────────────────────────────────────────────────────────────────┤
│ log viewer: a command bar to       │ 17 │ A real Logs tab                                                      │ 18
│ refresh, copy, export and open the │ 17 │ Settings › Logs is a full-height log viewer: a command bar to        │ 17
│ ░░░ folder, a search box with ░░░░ │ 17 │ refresh, copy, export and open the log folder, a search box with     │ 17
│ Read more ›                        │ 16 │ level and category filters, rows that expand on a click to show      │ 17
└────────────────────────────────────┘    │ ░░░ fields and exception, a session picker that lists your ░░░░░░░░ │ 17
  ▲ 24 DIP fade over line 4 (░)           │ Read more ›                                                          │ 16
    padding 12/10/12/12                   └──────────────────────────────────────────────────────────────────────┘
  card = 122 + 150 = 272                    card = 236 + 132 (one-line title) = 368; row stretches to the tallest
```

Store card (two-up width, 329): accent stroke and pill, no label row, the button as a sibling footer.

```
┌──────────────────────────────────────────────────────┐  ← Tok.AccentDefault stroke
│ [Microsoft Store]                                    │
│               tinted plate 329 × 185 (AccentSubtle)  │ 185
│                                                      │
├──────────────────────────────────────────────────────┤
│ Wavee is on the Microsoft Store                      │ 18   ┐ hit region
│ Install it from the Store and updates arrive the     │ 17   │ (Role Button)
│ way every Store app's do — quietly, in the           │ 17   │
│ background, with no installer to run. Your library,  │ 17   │
│ ░░░ settings and sign-in carry over unchanged ░░░░░░ │ 17   ┘
│ ┌──────────────────────────────────┐                 │ 32   ← footer, a sibling: Button.Accent(storeCta)
│ │ Get it from the Microsoft Store  │                 │
│ └──────────────────────────────────┘                 │
└──────────────────────────────────────────────────────┘      card = 185 + 170 = 355
```

### D.2 The after-update dialog, three shortened cards (720 × 505)

```
720 × 505 · Tok.FillSolidBase · r8
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                                                                                                      │ 22
│    [Updated] [0.2.5.3 → 0.2.6.7]                                                                                     │ 22
│                                                                                                                      │  8
│    Welcome to Wavee 0.2.6 “Breaker”                                                                     26/35 sb     │ 35
│                                                                                                                      │  8
│    A three-step setup, a real Logs tab, and problem reports from inside the app.                        14/19 sec    │ 19
│                                                                                                                      │ 18
│                                                                                                                      │  6
│    ┌────────────────────────────────┐  ┌────────────────────────────────┐  ┌────────────────────────────────┐        │
│    │ [Rebuilt]                      │  │ [New]                          │  │ [New]                          │        │
│    │        POSTER 216 × 122        │  │        POSTER 216 × 122        │  │        POSTER 216 × 122        │        │ 122
│    │                                │  │                                │  │                                │        │
│    │                                │  │                                │  │                                │        │
│    ├────────────────────────────────┤  ├────────────────────────────────┤  ├────────────────────────────────┤        │
│    │ Setup is three screens         │  │ A real Logs tab                │  │ Report a problem from inside   │        │ 18
│    │                                │  │                                │  │ Wavee                          │        │ 18
│    │ Terms, sign in, local playback │  │ Settings › Logs is a full-     │  │ Settings › About has "Report a │        │ 17
│    │ — that is the whole wizard     │  │ height log viewer: a command   │  │ problem…" and "Suggest a       │        │ 17
│    │ now, in a plain WinUI dialog   │  │ bar to refresh, copy, export   │  │ feature…", and after a crash   │        │ 17
│    │ ░░░ an animated hero beside ░░ │  │ ░░░ open the log folder, a ░░░ │  │ ░░░ next launch offers to ░░░░ │        │ 17
│    │ Read more ›                    │  │ Read more ›                    │  │ Read more ›                    │        │ 16
│    └────────────────────────────────┘  └────────────────────────────────┘  └────────────────────────────────┘        │
│    ◀ 26 ▶                      ◀10▶                                                                                  │ 14
├──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ 1
│    ☐ Don't show this after updates                                        [ Full release notes ]  [  Got it  ]       │ 60
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘ 1
     hero 132 (151 with a wrapped tagline) · row 6 + 272 + 14 · footer 62 · total 486 (505) — cap 620 untouched
```

### D.3 The viewer — wide window (1440 × 900 → W = 960)

Scale: 1 column ≈ 12 DIP, 1 row ≈ 30 DIP. Veil rgba(0,0,0,.72) over the whole window.

```
1440 × 900                                              plate 960 × 792, centred
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   54
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒┌────────────────────────────────────────────────────────────────────────────────┐▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ [New]                                                                     (×) │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ (‹)                      POSTER 960 × 540 · 1200 px decode                (›) │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  540
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                                                                │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒├────────────────────────────────────────────────────────────────────────────────┤▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                  ○ ● ○                                         │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   36
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  A real Logs tab                                                    20/28 sb   │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   28
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  Settings › Logs is a full-height log viewer: a command bar to refresh, copy,   │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  export and open the log folder, a search box with level and category filters,  │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   60
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  rows that expand … for the running session.                        14/20 sec  │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  ┌───────────┐                                                                 │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   32
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  │ Try it →  │                                                                 │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒└────────────────────────────────────────────────────────────────────────────────┘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   24 pad
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   54
   (‹) dimmed-in-place on slide 1, (›) on slide 3 — same box, never removed · 36 DIP MediaScrim circles, 12 DIP inset
   plate 540 + 36 + 216 = 792 ≤ 836
```

### D.4 The viewer — narrow window (900 × 600 → W = 427)

Scale: 1 column ≈ 10 DIP, 1 row ≈ 20 DIP. `W = min(960, 804, (600 − 360) × 16⁄9 = 427)`; image 240; text scrolls.

```
900 × 600                                   plate 427 × 536 (MaxHeight = vpH − 64)
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   32
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒┌──────────────────────────────────────────┐▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ [New]                                (×) │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                          │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                          │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ (‹)      POSTER 427 × 240            (›) │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  240
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                          │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                          │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                          │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                                          │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒├──────────────────────────────────────────┤▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│                 ○ ● ○                    │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   36
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  A real Logs tab                        ▲│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   28
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  Settings › Logs is a full-height log   ║│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  viewer: a command bar to refresh,      ║│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  copy, export and open the log folder,  ║│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  120
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  a search box with level and category   ║│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   (scrolls)
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  filters, rows that expand on a click   ║│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  to show their fields and exception, a  ▼│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒└──────────────────────────────────────────┘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒
▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒   32
   image never shrinks below the W rule; the text column takes the remainder and scrolls
```

---

## E. Edge cases

1. **One highlight.** No pager, no arrows; Left/Right/Home/End are no-ops (handled, so they do not leak to the page);
   the card still opens the viewer — the whole body and the large poster are the value even with nothing to page.
2. **Two highlights.** Two dots; the arrows clamp (only "next" on slide 1, only "prev" on slide 2); 2-up cards are
   329 wide in the dialog, bands 185.
3. **No poster.** Card: unchanged — the 16:9 tinted band with the pill, because a card that loses its top third
   next to two cards that kept theirs reads as broken. Viewer: a 120 DIP tinted band (the 16:9 void would be 540 DIP
   of nothing); the chrome circles fit (12 + 36 + 12 = 60 ≤ 120).
4. **Store card.** Accent stroke and pill; hit region without the label row; `Button.Accent` as a sibling footer;
   text block 170; viewer slide with the tinted `AccentSubtle` band and the same button in the actions row. Hidden on
   Store installs by `HighlightVisibility`, in both hosts, as today.
5. **Very short body** (one line). The slot stays 68 — the row's labels and bottoms align across cards, which is the
   point of a fixed slot; `overflows` is false so the fade is at opacity 0 (and would paint fill-on-fill even at 1);
   the viewer shows the line plainly under the poster.
6. **Very long title.** Card: two lines then "…"; the full title is in the viewer at 20/28 with unlimited wrap. The
   card's automation name carries the full title.
7. **Video poster + play glyph.** Card: unchanged (poster, 32 DIP glyph top-right). Viewer: the glyph at 48 DIP,
   centred on the image, and the image band is a link — `Role = Hyperlink`, `Cursor = Hand`, opens
   `doc.Links.Release` (or `ReleaseNotesText.ReleaseTagUrl(version)`) in the browser via `ShellOpen.OpenUrl`; the
   viewer stays open. A play glyph that does nothing on click is a lie; the release page is where the mp4 lives.
8. **Body exactly at the trim boundary** (four lines). Natural height 68 → `68 > 68.5` is false → no fade, all four
   lines crisp, "Read more" still there (the viewer still shows the poster large). Line heights are integral, so a
   four-line body measures 68.0, never 68.4. Five lines → 85 → fade on, line 4 ghosted, line 5 clipped by the slot.
9. **Window resize on the page.** Card width changes → paragraph reflows → `OnBoundsChanged` → `overflows` flips →
   the fade eases over 83 ms. The dialog is fixed-width; nothing reflows there.
10. **Viewer over the dialog, "Try it".** `nav` first, then `closeHost()`, then the viewer's own close — the shell
    navigates under two exiting plates and focus lands on the destination page.

---

## Implementation notes

**Files.** `HighlightCard.cs` (becomes `HighlightCardView : Component` + the static `Create`/`Compact` wrappers;
`CompactMediaMaxH` deleted, `CompactCardMaxW = 356` added; the fade, the slot, the label, the flat recipe);
`HighlightStrip.cs` (`nav` → `open`); `AfterUpdateDialog.cs` (`nav` widened to two args, `open` closure, the viewer
handle); `ReleaseNotesPage.cs` (overlay service, `open` closure); new `HighlightViewer.cs` (opener + `HighlightViewerView`);
new `Wavee.Core/ReleaseNotes/HighlightViewerLayout.cs` (pure); `en-US.json` (six keys).

**Pure classes to unit-test** (no source-text tests, no engine):

- `HighlightViewerLayout.PlateWidth(vpW, vpH)` — the `W` rule; `ImageHeight(W)`; `ImageHeight` for a no-poster slide
  (120). Cases: 1440×900 → 960; 1100×700 → 604; 900×600 → 427; 500×420 → 320 (floor); 320-wide window → 224.
- `HighlightViewerLayout.Step(current, count, key)` — Left/Right clamp, Home/End, and the direction the slide takes
  (`Forward`, `Back`, `None`); count 1 → always `None`.
- `HighlightCardMetrics.TextBlockHeight(titleLines, store)` → 132 / 150 / 152 / 170, and
  `HighlightCardMetrics.DialogHeight(cardCount, store, taglineLines)` against the §A.4 table — the test that keeps the
  620 cap honest when someone touches a padding.
- `HighlightCardMetrics.Overflows(naturalHeight)` → `> 68.5`.

**Verify against the engine before building** (each is a five-minute check, and the design does not change if one
fails — the fallback is named):

- A ZStack child with `AlignSelf = Start` and no explicit height takes its natural height rather than stretching
  (the lightbox's anchored chrome suggests yes). Fallback: measure with a second, invisible copy of the paragraph
  outside the slot (`Opacity 0`, `HitTestVisible false`) and read *its* bounds.
- `OnBoundsChanged` exists on `BoxEl` (Element.cs:358) — it is not on `SpanTextEl`, hence the wrapper.
- `TextEl.HoverColor` eases with the *nearest interactive ancestor* — the hit region must be that ancestor (it is:
  `OnClick` on the region, nothing interactive between it and the label).
- `PopupOptions.ScrimVisual = false` with `PopupChrome.Modal` keeps the blocking and the trap and drops the smoke
  (the field's own comment says so).
