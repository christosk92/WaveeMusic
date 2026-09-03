# What's-new highlight card + viewer — implementation

Design and rationale: [`whatsnew-highlight-viewer-design.md`](whatsnew-highlight-viewer-design.md). Interactive
prototype: [`whatsnew-highlight-viewer-prototype.html`](whatsnew-highlight-viewer-prototype.html). This file is the
*build* document: the file sets, the real code, and the places where the engine as it actually exists overrides the
spec.

The card drops from ~640 DIP to **272** (band 122 + text block 150); the trimmed body gets a home in a modal viewer.
The after-update dialog lands at **505** DIP against its 620 cap.

---

## 0. Where the engine overrides the spec

Verified against `..\fluent-gpu` before writing this. Where the spec and the engine disagree, the engine wins.

| Spec says | Engine fact | What we build |
|---|---|---|
| Card hit region uses `PressScale` + `Transition` | `.Interactive(Interaction.Card)` writes the fill brushes **unconditionally** (`Interaction.cs:86-93`); there is no opt-out flag. Its motion half is exactly `WhilePressed = MotionTarget { Scale }` + `Transition` (`:114-121`) | Skip `Interactive` entirely; set those two properties directly. Nothing to undo. |
| §B.3 the slide is a `Transition` | `Element.Transition` is `MotionTokenDef?` (on-change interpolation); Enter/Exit specs live on `Animate` (`LayoutTransition?`) | Slide subtrees get `Animate`; the fade's and the chevrons' opacity changes get `Transition = MotionTok.ControlFaster`. |
| §B.3 `Exit (Dx ∓24)`, mirrored per direction | An orphan's exit is seeded from the spec **the node mounted with**, so a node that entered "forward" exits "forward" even on a Back step | Exit is **direction-free**: fade in place. Only Enter carries ±24. Pinned by `HighlightViewerMotionTests`. |
| §B.1 `W = clamp(320, …)` **and** "320-wide window → 224" | The two contradict each other | Window edge wins over the floor: `W = min(max(320, inner), vpW − 96)`. Both stated cases pass. |
| §B.1 light dismiss on a scrim click | The viewer paints its own full-window veil, so no click is ever *outside* the popup | The veil box carries `OnClick = close`. `DismissBehavior.LightDismiss` stays, for Escape. |
| §B.3 one keyed subtree (image + pager + text) | Remounting the `PipsPager` destroys the focused pip, and the chevrons' dim is an opacity transition on a *persistent* node | **Two** keyed subtrees (`:img`, `:txt`) with the same key and recipe; pager and chrome are stable siblings. |
| §A.2 `OnBoundsChanged` gives `r.Height` | `RectF` fields are `.W` / `.H` | `r.H`. |
| §B.6 root has a dialog role | `AutomationRole` has **no** `Dialog` member | `AutomationRole.None`; the documented engine gap stands (app-only change, by decision). |
| §E.7 a 48 DIP glyph makes the image a link | There is no automation-name property, so a bare glyph link has no name | A **labelled** on-media pill (`Icons.Play` + `whatsNew.viewer.watch`) centred in the band is the hyperlink. |
| §B.1 veil `rgba(0,0,0,0.72)` | — | `ColorF.FromRgba(0, 0, 0, 184)`, a local literal with a comment. |

**The user's correction, which overrides the spec's first draft:** both chevrons are **always rendered at a fixed
position**. At an end the button dims in place (opacity 0.3, `MediaScrim @ A = 0.40`, `HitTestVisible = false`) and
never leaves the layout. Chrome that disappears and comes back as you page reads as the chrome jumping, not the
slide moving.

---

## 1. File sets

Four disjoint sets; no file appears twice, so all four can be written in parallel. Only the orchestrator builds,
tests and launches.

**Set 1 — pure models + tests**
- NEW `src/apps/Wavee.Core/ReleaseNotes/HighlightCardMetrics.cs`
- NEW `src/apps/Wavee.Core/ReleaseNotes/HighlightViewerLayout.cs`
- NEW `src/apps/Wavee/Features/ReleaseNotes/HighlightViewerMotion.cs`
- NEW `src/apps/Wavee.Tests/ReleaseNotes/HighlightCardMetricsTests.cs`
- NEW `src/apps/Wavee.Tests/ReleaseNotes/HighlightViewerLayoutTests.cs`
- NEW `src/apps/Wavee.Tests/ReleaseNotes/HighlightViewerMotionTests.cs`
- EDIT `src/apps/Wavee.Tests/Wavee.Tests.csproj` — one `<Compile Include>` beside the `PageNavMotion.cs` one at `:183`

The two metric classes are BCL-only and live beside `HighlightVisibility` / `ReleaseNotesRange` in
`Wavee.Core/ReleaseNotes/`, so no csproj edit and the app reads them through its existing reference — which is what
makes the 620-cap test a real gate: the dialog *renders from* the numbers the test asserts. `HighlightViewerMotion`
needs engine `Foundation` types, which `Wavee.Core` must not reference, so it stays in the app and is
source-included into the tests exactly like `Features/Shell/PageNavMotion.cs`.

**Set 2 — the card**
- REWRITE `src/apps/Wavee/Features/ReleaseNotes/HighlightCard.cs`
- EDIT `src/apps/Wavee/Features/ReleaseNotes/HighlightStrip.cs`

**Set 3 — the viewer**
- NEW `src/apps/Wavee/Features/ReleaseNotes/HighlightViewer.cs`

**Set 4 — hosts + localization**
- EDIT `src/apps/Wavee/Features/ReleaseNotes/AfterUpdateDialog.cs`
- EDIT `src/apps/Wavee/Features/Shell/SettingsPage.About.cs` (`:445`, the third `Open` caller)
- EDIT `src/apps/Wavee/Features/ReleaseNotes/ReleaseNotesPage.cs`
- EDIT `src/apps/Wavee/assets/loc/en-US.json`

Serial fallback order: Set 4's JSON → Set 1 → Sets 2 & 3 → Set 4's host edits.

---

## 2. Set 1 — the pure models

### `Wavee.Core/ReleaseNotes/HighlightCardMetrics.cs`

```csharp
using System;

namespace Wavee.Core.ReleaseNotes;

/// <summary>The highlight card's fixed geometry and the after-update dialog's height budget, as PURE arithmetic.
/// The card and the dialog RENDER from these constants and the tests hold the 620 DIP plate cap against them, so a
/// padding someone nudges fails a test instead of a screenshot.</summary>
public static class HighlightCardMetrics
{
    /// <summary>Release images are authored 1200×675; the band derives its height from the card's width rather than
    /// pinning a pixel height a wide card would letterbox and a narrow one would crop.</summary>
    public const float PosterAspect = 16f / 9f;

    /// <summary>The page card's width cap — with one highlight a full-row card reads as a banner, not a card.</summary>
    public const float CardMaxW = 420f;

    /// <summary>The dialog's lone-card cap, REPLACING the old 236 DIP height cap: 356 wide gives a 200 DIP band at
    /// the authored proportion. A height cap would have had to crop.</summary>
    public const float CompactCardMaxW = 356f;

    public const float TitleSize = 13.5f;
    /// <summary>Explicit, so the title block's height is arithmetic (18 or 36), not a font-metric guess.</summary>
    public const float TitleLineHeight = 18f;
    /// <summary>Two: "Report a problem from inside Wavee" wraps at 216 wide and a one-line ellipsis cuts the verb off.
    /// Three is 18 DIP the body needs more.</summary>
    public const int TitleMaxLines = 2;

    public const float BodySize = 12.5f;
    /// <summary>The font-natural box for 12.5 px is ~16.6; an INTEGER line height is what makes the overflow test
    /// exact — natural heights are 17·n, never within half a pixel of the threshold.</summary>
    public const float BodyLineHeight = 17f;
    /// <summary>Four: ~128 characters at 216 wide (one whole sentence of every 0.2.6 body), ~260 at 420. Three cuts
    /// the Logs body inside its first clause; five is 17 DIP the row does not need once the viewer exists.</summary>
    public const int BodyLines = 4;
    public const float BodySlotHeight = BodyLines * BodyLineHeight;      // 68

    /// <summary>One line of fade, not two — two dissolved lines out of four reads as a rendering fault.</summary>
    public const float FadeHeight = 24f;
    /// <summary>Natural heights are 51 / 68 / 85, so "exactly four lines" is decided correctly.</summary>
    public const float OverflowThreshold = BodySlotHeight + 0.5f;        // 68.5

    public const float LabelHeight = 16f;
    public const float StoreButtonHeight = 32f;
    public const float StoreButtonGap = 4f;
    public const float PadL = 12f, PadT = 10f, PadR = 12f, PadB = 12f;
    /// <summary>The column gap between title, slot and label.</summary>
    public const float TitleBodyGap = 4f;
    /// <summary>The store card's hit region stops 4 DIP under the slot; its button is a sibling footer, so a card
    /// never nests a button inside a button.</summary>
    public const float HitRegionStoreBottomPad = 4f;

    public const float PlateWidth = 720f;
    public const float PlatePadX = 26f;
    public const float PlateMaxHeight = 620f;
    public const float CardGap = 10f;
    public const float RowPadTop = 6f, RowPadBottom = 14f;
    public const float HeroPadTop = 22f, HeroPillRow = 22f, HeroGap = 8f,
                       HeroWelcomeLine = 35f, HeroTaglineLine = 19f, HeroPadBottom = 18f;
    /// <summary>14 + 32 buttons + 14 + the 1 px top border.</summary>
    public const float FooterHeight = 62f;

    public static bool Overflows(float naturalHeight) => naturalHeight > OverflowThreshold;

    /// <summary>10 + title + 4 + 68 + tail + 12 → 132 / 150 regular (one- / two-line title), 152 / 170 store.</summary>
    public static float TextBlockHeight(int titleLines, bool store)
    {
        float title = Math.Clamp(titleLines, 1, TitleMaxLines) * TitleLineHeight;
        float tail = store
            ? HitRegionStoreBottomPad + StoreButtonGap + StoreButtonHeight   // 4 + 4 + 32
            : TitleBodyGap + LabelHeight;                                    // 4 + 16
        return PadT + title + TitleBodyGap + BodySlotHeight + tail + PadB;
    }

    /// <summary>668 inner: three-up 216, two-up 329, lone min(668, 356) = 356.</summary>
    public static float DialogCardWidth(int cardCount)
    {
        int n = Math.Clamp(cardCount, 1, 3);
        float inner = PlateWidth - 2f * PlatePadX;
        return MathF.Min((inner - (n - 1) * CardGap) / n, CompactCardMaxW);
    }

    /// <summary>The 16:9 band: 216 → 122, 329 → 185, 356 → 200, 420 → 236.</summary>
    public static float BandHeight(float cardWidth)
        => MathF.Round(cardWidth / PosterAspect, MidpointRounding.AwayFromZero);

    public static float CardHeight(float cardWidth, int titleLines, bool store)
        => BandHeight(cardWidth) + TextBlockHeight(titleLines, store);

    /// <summary>22 + 22 + 8 + 35 + 8 + 19·lines + 18 → 132 for one tagline line, 151 for two.</summary>
    public static float HeroHeight(int taglineLines)
        => HeroPadTop + HeroPillRow + HeroGap + HeroWelcomeLine + HeroGap
           + Math.Max(1, taglineLines) * HeroTaglineLine + HeroPadBottom;

    /// <summary>The plate's height for a row of <paramref name="cardCount"/> cards, worst case (two-line titles).</summary>
    public static float DialogHeight(int cardCount, bool store, int taglineLines)
        => HeroHeight(taglineLines) + RowPadTop
           + CardHeight(DialogCardWidth(cardCount), TitleMaxLines, store)
           + RowPadBottom + FooterHeight;
}
```

### `Wavee.Core/ReleaseNotes/HighlightViewerLayout.cs`

```csharp
using System;

namespace Wavee.Core.ReleaseNotes;

/// <summary>The viewer's navigation verbs, app-neutral: the view maps FluentGpu's <c>Keys.*</c> ints onto these so
/// this file stays engine-free and unit-testable.</summary>
public enum HighlightNavKey : byte { Previous, Next, First, Last }

/// <summary>Which way a slide travels. None = the index did not change (clamped at an end, or a single item).</summary>
public enum HighlightSlideDirection : byte { None, Forward, Back }

public readonly record struct HighlightStep(int Index, HighlightSlideDirection Direction);

/// <summary>The viewer plate's geometry (design §B.1) and its stepping rule (§B.2), pure.</summary>
public static class HighlightViewerLayout
{
    /// <summary>A 1200 px poster shown at 960 is a 0.8× downsample — sharp, never upsampled; wider plates make the
    /// text measure absurd.</summary>
    public const float PlateMaxWidth = 960f;
    /// <summary>Below this the image is unreadable (180 tall); the text column scrolls instead.</summary>
    public const float PlateMinWidth = 320f;
    /// <summary>48 DIP of veil each side, so the plate reads as a plate and not as a page.</summary>
    public const float ScrimInsetX = 96f;
    /// <summary>Pager (36) + text block (≤ 260) + 64 vertical margin the image must leave below itself.</summary>
    public const float ReservedBelowImage = 360f;
    public const float PlateMarginY = 64f;
    /// <summary>A slide with no poster gets a tinted band, not a 540 DIP void. The chrome still fits: 12 + 36 + 12.</summary>
    public const float NoPosterBandHeight = 120f;
    public const float PosterAspect = 16f / 9f;
    public const float ChromeCircle = 36f;
    public const float ChromeInset = 12f;

    /// <summary>W = min(max(320, min(960, vpW − 96, (vpH − 360)·16⁄9)), vpW − 96). The floor never pushes the plate
    /// past the window edge. 1440×900 → 960; 1100×700 → 604; 900×600 → 427; 500×420 → 320; a 320-wide window → 224.</summary>
    public static float PlateWidth(float vpW, float vpH)
    {
        float byWidth = vpW - ScrimInsetX;
        float byHeight = (vpH - ReservedBelowImage) * PosterAspect;
        float w = MathF.Min(PlateMaxWidth, MathF.Min(byWidth, byHeight));
        w = MathF.Max(PlateMinWidth, w);
        w = MathF.Min(w, byWidth);
        return MathF.Round(w);
    }

    public static float ImageHeight(float plateWidth, bool hasPoster)
        => hasPoster ? MathF.Round(plateWidth / PosterAspect) : NoPosterBandHeight;

    public static float PlateMaxHeight(float vpH) => vpH - PlateMarginY;

    /// <summary>Clamped, no wrap (the WinUI FlipView rule): a Right press on the last slide does nothing. A silent
    /// jump back to the first feels like a bug, and the dots already say where you are.</summary>
    public static HighlightStep Step(int current, int count, HighlightNavKey key)
    {
        if (count <= 1) return new(0, HighlightSlideDirection.None);
        int last = count - 1;
        current = Math.Clamp(current, 0, last);
        int target = key switch
        {
            HighlightNavKey.Previous => current - 1,
            HighlightNavKey.Next => current + 1,
            HighlightNavKey.First => 0,
            _ => last,
        };
        return StepTo(current, Math.Clamp(target, 0, last), count);
    }

    /// <summary>A direct jump (a pip click): the direction is the sign of the move.</summary>
    public static HighlightStep StepTo(int current, int target, int count)
    {
        if (count <= 1) return new(0, HighlightSlideDirection.None);
        int last = count - 1;
        current = Math.Clamp(current, 0, last);
        target = Math.Clamp(target, 0, last);
        var dir = target > current ? HighlightSlideDirection.Forward
                : target < current ? HighlightSlideDirection.Back
                : HighlightSlideDirection.None;
        return new(dir == HighlightSlideDirection.None ? current : target, dir);
    }
}
```

### `Wavee/Features/ReleaseNotes/HighlightViewerMotion.cs`

Engine `Foundation` types only, so it can be source-included into the test project like `PageNavMotion.cs`.

```csharp
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core.ReleaseNotes;

namespace Wavee;

/// <summary>The viewer's slide: a directional ENTER (±24 DIP + fade, 250 ms SmoothOut — Motion.EntranceOffsetPx and
/// Expressive.Fast) over a direction-FREE exit (fade in place).
///
/// <para>The exit is direction-free by necessity, not by taste: the engine seeds an orphan's exit from the spec the
/// node MOUNTED with, so a directional exit would replay the PREVIOUS step's direction on the way out.</para>
///
/// <para>Reduced motion needs no branch here — the scheduler skips Enter/Exit tracks under
/// <c>Motion.ReducedMotion</c> and the swap becomes a cut. Reduced motion is a value, never an author-side if.</para></summary>
static class HighlightViewerMotion
{
    /// <summary>== <c>Motion.EntranceOffsetPx</c>; a slideshow should visibly move (PageSlide's 8 is a page nudge).</summary>
    public const float SlideDistance = 24f;
    /// <summary>The exit's own duration — short, so the outgoing slide is gone before the incoming one lands.</summary>
    public const float ExitMs = 120f;

    public static LayoutTransition SlideForward => new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Dx: SlideDistance, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: 0f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(ExitMs, Easing.FluentAccelerate));

    public static LayoutTransition SlideBack => SlideForward with
        { Enter = new EnterExit(Dx: -SlideDistance, Opacity: 0f, Active: true) };

    /// <summary>The FIRST slide: no entrance (the Modal chrome already scales the plate in) but the same exit, so it
    /// can still fade out when the user steps off it.</summary>
    public static LayoutTransition ExitOnly => SlideForward with { Enter = default };

    public static LayoutTransition For(HighlightSlideDirection d) => d switch
    {
        HighlightSlideDirection.Forward => SlideForward,
        HighlightSlideDirection.Back => SlideBack,
        _ => ExitOnly,
    };
}
```

### The tests

`HighlightCardMetricsTests` — the budget table from design §A.4, and the cap gate:

| cards | store | tagline lines | expected |
|---|---|---|---|
| 3 | no | 2 | 505 |
| 3 | yes | 2 | 525 |
| 2 | no | 2 | 568 |
| 2 | yes | 2 | 588 |
| 1 | no | 2 | 583 |
| 1 | yes | 2 | 603 |
| 3 | no | 1 | 486 |

plus `TextBlockHeight` → 132 / 150 / 152 / 170, `DialogCardWidth` → 216 / 329 / 356, `BandHeight` → 122 / 185 / 200 /
236, `Overflows(51|68|68.5) == false` and `Overflows(85|102) == true`, and a `[Fact]` sweeping every
(cards × store × tagline) shape asserting `<= PlateMaxHeight`.

`HighlightViewerLayoutTests` — `PlateWidth` 1440×900 → 960, 1100×700 → 604, 900×600 → 427, 500×420 → 320,
320×600 → 224; `ImageHeight(960, true) == 540`, `(427, true) == 240`, `(w, false) == 120`; the chrome fits the
no-poster band; and the `Step` / `StepTo` table (clamped, no wrap; count 1 → always `None`; for `Home`/`End` and a
pip jump the direction is the sign of the move).

`HighlightViewerMotionTests` — `SlideDistance == Motion.EntranceOffsetPx`; Forward enters from +24 and Back from
−24 with `Enter.Active`; **every** recipe's exit has `Dx == 0`, `Opacity == 0`, `Active`; `For(None).Enter.Active`
is false.

Shape to copy: `src/apps/Wavee.Tests/ArtistHeroLayoutTests.cs` (`[Theory]/[InlineData]` ladders, `[Fact]` field
assertions).

---

## 3. Set 2 — the card

`HighlightCard.cs` becomes a thin static façade over a `HighlightCardView : Component`. The component exists because
the fade needs an owner for its per-card `overflows` signal.

Key points:

- **File comment must flag** that this is the ONE card in Wavee with an opaque fill (`Tok.FillSolidTertiary`, first
  card use), because the edge fade must end in a colour that is actually there — `Interaction.Card`'s white-@-5 %
  veil is not a colour, and no end-stop matches it over both the dialog plate and Mica.
- **Deleted outright** (no legacy path): `CompactMediaMaxH`, `CardMaxW`, `PosterAspect` (all now in
  `HighlightCardMetrics`), the "Try it" `TextEl`, the `nav` parameter, `Media`'s `maxHeight`, the
  `.Interactive(Interaction.Card)` call.
- `Media` / `KindPill` / `PlayGlyph` / `OpenStoreListing` / `PosterDecodePx` become `internal` — the viewer reuses
  them. Add `internal static bool IsVideo(ReleaseHighlight h)` extracted from `PlayGlyph`'s existing test.

```csharp
public static Element Create(HighlightItem item, Action open)
    => Embed.Comp(() => new HighlightCardView(item, open, compact: false));

public static Element Compact(HighlightItem item, Action open)
    => Embed.Comp(() => new HighlightCardView(item, open, compact: true));
```

The measuring slot and the fade:

```csharp
readonly Signal<bool> _overflows = new(false);

// Theme-live: a get-only property re-reads the token each render. Both stops share the card's RGB, so the ramp is
// a pure alpha ramp with no hue drift mid-fade.
static GradientSpec Fade => GradientDown(
    new GradientStop(0f, Tok.FillSolidTertiary with { A = 0f }),
    new GradientStop(1f, Tok.FillSolidTertiary));

/// <summary>The fixed 68 DIP slot. The paragraph is rendered UNCLAMPED (MaxLines 0) inside a natural-height wrapper
/// whose bounds decide the fade; the slot clips whatever falls below line 4. Clamping with MaxLines instead would
/// make the natural height always ≤ 68 and the overflow unmeasurable — the fade could never be conditional.</summary>
Element BodySlot(ReleaseHighlight h, bool overflows) => new BoxEl
{
    Height = M.BodySlotHeight, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
    ZStack = true, ClipToBounds = true, HitTestVisible = false,
    Children =
    [
        new BoxEl   // measuring wrapper: top-anchored, full width, NATURAL height
        {
            AlignSelf = FlexAlign.Start, Shrink = 0f, MinWidth = 0f,
            OnBoundsChanged = r =>
            {
                bool v = M.Overflows(r.H);                       // RectF.H, not .Height
                if (_overflows.Peek() != v) _overflows.Value = v; // flip only when the ANSWER changes
            },
            Children =
            [
                RichTextBlock.Paragraph(
                    ReleaseNotesText.ToSpans(MarkdownLite.Tokenize(h.Body), static _ => { }),
                    isTextSelectionEnabled: false)
                with { Size = M.BodySize, LineHeight = M.BodyLineHeight, MaxLines = 0,
                       Wrap = TextWrap.Wrap, Color = Tok.TextSecondary },
            ],
        },
        new BoxEl   // the edge fade, bottom-anchored
        {
            Height = M.FadeHeight, AlignSelf = FlexAlign.End,
            HitTestVisible = false, Gradient = Fade,
            Opacity = overflows ? 1f : 0f,
            Transition = MotionTok.ControlFaster,   // 83 ms KeepFade: a resize eases the fade, never pops it
        },
    ],
};
```

The card's own link handler is a no-op (`static _ => { }`): the card is one button, so a live link run inside it
would be a nested click target. Links are live in the viewer, where the body is not a button.

The flat interaction recipe on the hit region — `Interaction.Card`'s press without its fill ramp:

```csharp
Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
WhilePressed = new MotionTarget { Scale = 0.985f },
Transition = MotionTok.StandardSpring,
```

and on the frame:

```csharp
Fill = Tok.FillSolidTertiary,                                   // opaque — see the file comment
BorderWidth = 1f,
BorderColor      = store ? Tok.AccentDefault : Tok.StrokeCardDefault,
HoverBorderColor = store ? Tok.AccentDefault : Tok.StrokeSurfaceDefault,
BrushTransitionMs = WaveeMotion.Faster,
MaxWidth = compact ? M.CompactCardMaxW : M.CardMaxW,
```

No hover scale: three scaling cards in a 10 DIP-gap row shimmer against each other.

**Regular card** — frame and hit region are ONE node (hover stroke and click share the box):
`Media → column(title, slot, "Read more ›")`.
**Store card** — frame holds two siblings: the hit region (band + title + slot, bottom padding 4, no label) and a
footer with `Button.Accent(storeCta)`. Exactly one click target each.

"Read more ›" is a `TextEl` at 12 / 600 with `HoverColor = FocusedColor = Tok.AccentTextPrimary` and
`BrushTransitionMs = WaveeMotion.Faster` (it eases with the nearest interactive ancestor, which is the hit region),
plus `Icons.ChevronRight` at 10. It exists even on cards whose body fits: without it a short card gives no cue that
a click opens anything.

Arithmetic check against the column `Gap = 4`: regular `10 + 36 + 4 + 68 + 4 + 16 + 12 = 150`; store hit region
`10 + 36 + 4 + 68 + 4 = 122`, footer `4 + 32 + 12 = 48`, total 170. Both are what `TextBlockHeight` returns.

`HighlightStrip.Create(highlights, Action<int> open)` — capture `int idx = i` per card and pass
`() => open(idx)`; keying is unchanged.

---

## 4. Set 3 — the viewer

```csharp
public static OverlayHandle Open(IOverlayService overlay, IReadOnlyList<HighlightItem> items, int initial,
                                 Action<string, string?>? nav, Action? closeHost)
{
    OverlayHandle? handle = null;                     // the ArtistPage.OpenGallery self-reference idiom
    handle = overlay.Open(
        static () => NodeHandle.Null,
        () => Embed.Comp(() => new HighlightViewerView(items, initial, nav, closeHost, () => handle)),
        FlyoutPlacement.BottomCenter,
        // Modal chrome = the ContentDialog scale/fade for free; FocusTrap keeps Tab inside; LightDismiss keeps
        // Escape. ScrimVisual = false: the view paints ONE authored veil instead of stacking on the chrome's smoke.
        new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Modal)
            { ScrimVisual = false });
    return handle;
}
```

Tree:

```
BoxEl root   Grow 1 · ZStack · Focusable · OnKeyDown
├─ BoxEl veil   Stretch · Fill rgba(0,0,0,.72) · OnClick close
└─ BoxEl plate  Center · Width W · MaxHeight vpH−64 · FillSolidBase · r8 · border StrokeSurfaceDefault · Clip
   ├─ BoxEl band   H = W×9/16 (120 with no poster) · ZStack · Clip
   │  ├─ BoxEl  Key "hv:{id}:img" · Animate slide     [ImageEl poster · r8 top · Fade(140)]
   │  └─ BoxEl  chrome — STABLE, outside the slide
   │     ├─ top row:    KindPill ································· (×) close      ← 12 inset
   │     └─ centre row: (‹) prev ··· [▶ Watch the video on GitHub] ··· (›) next
   ├─ BoxEl pager row (count ≥ 2)  H 24 · margin-top 12 · PipsPager(_pip)
   └─ BoxEl  Key "hv:{id}:txt" · Animate slide · Grow/Shrink/MinHeight 0
      └─ ScrollView  pad 24 / 8|16 / 24 / 24 · MaxWidth 720
         ├─ TextEl title 20 / 600 · LineHeight 28
         ├─ SpanTextEl body 14 · LineHeight 20 · selection on · links LIVE
         └─ actions row  (Try it → | Get it from the Microsoft Store)
```

The chevron — the same node on every slide, dimming in place:

```csharp
/// <summary>A 36 DIP on-media circle. The SAME node on every slide (stable Key): at an end it dims IN PLACE and
/// never leaves the layout. Removing it was the first draft and it read as the chrome jumping around, not as the
/// slide moving — a control that is in the same place on every slide is the whole point of chrome.</summary>
static BoxEl Circle(string glyph, float glyphSize, bool enabled, Action onClick, string key) => new BoxEl
{
    Key = key,
    Width = L.ChromeCircle, Height = L.ChromeCircle, Shrink = 0f,
    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
    Corners = CornerRadius4.All(L.ChromeCircle / 2f),
    Fill = enabled ? Tok.MediaScrim : ChromeDimmed,          // MediaScrim @ A = 0.40 when dimmed
    HoverFill = enabled ? ChromeHover : default,             // MediaScrim @ A = 0.70
    BrushTransitionMs = WaveeMotion.Faster,
    Opacity = enabled ? 1f : 0.3f,
    Transition = MotionTok.ControlFaster,                    // the dim is an 83 ms ease, not a pop
    HitTestVisible = enabled, Focusable = enabled, TabStop = enabled ? null : false,
    Role = AutomationRole.Button, Cursor = enabled ? CursorId.Hand : default,
    OnClick = enabled ? onClick : null,
    Children = [ Icon(glyph, glyphSize, Tok.OnMediaPrimary) ],
};
```

Navigation and keys:

```csharp
readonly Signal<int> _index;
readonly Signal<int> _pip = new(0);                 // the pager's CONTROLLED value, mirrored from _index by an effect
HighlightSlideDirection _dir = HighlightSlideDirection.None;   // a FIELD, not a signal: a motion-only value must
                                                               // never trigger a render of its own

void Go(HighlightStep step)
{
    if (step.Direction == HighlightSlideDirection.None) return;
    _dir = step.Direction;          // read by the render the next line triggers
    _index.Value = step.Index;
}

/// <summary>Root keys. Left/Right step (clamped), Home/End jump, Escape is the overlay's. Every recognised key is
/// marked handled even when it is a no-op, so a single-highlight viewer never leaks arrows to the page under the
/// veil. A key that arrives already handled came from a focused pip — PipsPager roves pip FOCUS on Left/Right and
/// marks it handled, which is the WinUI contract we defer to.</summary>
void OnKeys(KeyEventArgs e)
{
    if (e.Handled) return;
    HighlightNavKey key;
    switch (e.KeyCode)                  // int; Keys.* are const ints, so these are constant cases
    {
        case Keys.Left:  key = HighlightNavKey.Previous; break;
        case Keys.Right: key = HighlightNavKey.Next; break;
        case Keys.Home:  key = HighlightNavKey.First; break;
        case Keys.End:   key = HighlightNavKey.Last; break;
        default: return;
    }
    e.Handled = true;
    Go(L.Step(_index.Peek(), _items.Count, key));
}
```

The pager is **controlled**: `_pip` is mirrored from `_index` in a `UseEffect` (never written during render — the
`ArtistPopular.ChartPager` rule) and the pips write back only through `onChange`, so `_dir` is always set before the
index moves.

Actions row (design §B.5): the store slide gets `Button.Accent(storeCta)` and the viewer **stays open** (the Store
app takes focus anyway); a deep-link slide gets `Button.Accent(tryIt)` whose handler runs **in this order** —
`nav(route, arg)`, then `closeHost?.Invoke()`, then `Close()` — so the shell has navigated before either plate
starts its exit.

A video highlight's band carries the labelled watch pill linking to `doc.Links.Release` (falling back to
`ReleaseNotesText.ReleaseTagUrl(doc.Version)`); the viewer stays open. A play glyph that does nothing on click is a
lie, and the release page is where the mp4 lives.

---

## 5. Set 4 — hosts and localization

`AfterUpdateDialog`: widen `nav` from `Action<string>` to `Action<string, string?>` at `:36` (`Open`), `:53`
(`Plate`), `:124` (`nav("whatsnew", null)`) and `:195` (`AfterUpdateChrome`, which already holds the two-arg
delegate at `:181` and needlessly narrows it). Pass the `IOverlayService` into `Plate` so the cards can open the
viewer. The card loop becomes:

```csharp
int idx = i;
cards.Add(HighlightCard.Compact(highlights[idx],
        () => HighlightViewer.Open(overlay, highlights, idx, nav, close))
    with { Key = "dlg-hl:" + (highlights[idx].Highlight.Id is { Length: > 0 } id ? id : idx.ToString()) });
```

Plate and row numbers read `HighlightCardMetrics.PlateWidth / PlatePadX / CardGap / RowPadTop / RowPadBottom /
PlateMaxHeight` instead of literals — that is what makes the budget test a gate.

`SettingsPage.About.cs:445` is the **third** `AfterUpdateDialog.Open` caller ("Show the update summary again"); it
narrows the same way and gets the same widening.

`ReleaseNotesPage`: `var overlay = UseContext(Overlay.Service);` after `:53` (this resolves inside a page —
`ArtistPage.cs:71` does exactly this), and `:95` becomes:

```csharp
HighlightStrip.Create(view.MergedHighlights,
    i => HighlightViewer.Open(overlay, view.MergedHighlights, i, go, null)),
```

`en-US.json`, inside the `whatsNew` object — a leaf after `storeCta`:

```json
"readMore": "Read more",
```

and a nested object beside `dialog`:

```json
"viewer": {
  "$comment": "The highlight viewer a card opens: chrome tooltips, the pager's position, the video slide's link.",
  "close": "Close",
  "previous": "Previous highlight",
  "next": "Next highlight",
  "position": "{index} of {count}",
  "watch": "Watch the video on GitHub"
}
```

Generated: `Strings.WhatsNew.ReadMore`, `Strings.WhatsNew.Viewer.Close/.Previous/.Next/.Watch` (consts, read via
`Loc.Get`), and `Strings.WhatsNew.Viewer.Position(index, count)` — a method, called directly, **not** through
`Loc.Get`. No `nl.json` / `ko-KR.json` change: neither carries a `whatsNew` block and no loc gate covers it.

---

## 6. Verification

```powershell
dotnet build Wavee.slnx
dotnet build Wavee.slnx -c Release
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj
```

**The page:** `dotnet run --project src/apps/Wavee -- --fake` → Settings › About → What's new.

**The dialog without a real update** — both routes are gated on `!IsDev`, and a plain `dotnet run` is always dev
(`Quad.Length == 0`), so stamp the build:
`dotnet run --project src/apps/Wavee -p:WaveeChannel=stable -p:WaveePackageVersion=0.2.6.7 -- --fake`, then
Settings › About › "Show the update summary again". The automatic path is `AppLaunchVersion.Arm`: launch once at
`0.2.5.3`, quit, launch at `0.2.6.7`. Delete `%LOCALAPPDATA%\Wavee` afterwards. Packaged truth stays
`ops/release/tests/local-update-e2e.ps1 -Scenario inapp` (elevated).

**Manual checklist** — design §E's ten edge cases, plus: Escape and a veil click close; the Tab order stays trapped;
**the chevrons never move or vanish while paging**; reduced motion gives an instant swap with the chrome still
dimming.

---

## 7. Risks, each with its fallback

| Risk | Fallback |
|---|---|
| A ZStack child with `AlignSelf = Start` stretches to the slot instead of taking its natural height → `r.H` is always 68 → no fade ever | Measure a second, invisible copy of the paragraph outside the slot (`Opacity 0`, `HitTestVisible false`) and read *its* bounds |
| `Embed.Comp` is not layout-transparent, so the card's `Grow/Shrink/Basis/MaxWidth` never reach the row | Keep the flex props on an outer `BoxEl` around the `Embed.Comp` in `Create`/`Compact` |
| Exit-on-orphan does not fire for a plain keyed child | `Flow.KeepAlive` with `TransitionFor: (_, _) => HighlightViewerMotion.For(_dir)` inside a `Grow/Shrink/MinHeight 0/Clip` wrapper, accepting a fixed plate height |
| `TextEl.HoverColor` does not ease with the hit region | The hit region *is* the nearest `OnClick` ancestor; else drive the label colour from a per-card hover signal |
| `ScrimVisual = false` also drops blocking, not just the smoke | `ScrimVisual = true` and drop our veil to ~0.45 so the stack still sums to ~0.72 |
| Focus parked on a chevron that becomes `Focusable = false` at an end | Keep it focusable but `HitTestVisible = false` / `OnClick = null` — a harmless tab stop |
| `Motion.EntranceOffsetPx` is not 24 in the checked-out engine | The pin test fails loudly; update `SlideDistance` |
