// ── Themes/WinUi/Structure/Rules/WinUiReleaseNotesRules.cs — the numbers of WinUI's release-notes overlays (themes plan 6.1, T11c) ─────────
//
// What `Screens\ReleaseNotes.UI.cs` and `Screens\ReleaseNotes.cs` (`HighlightCardMetrics`, the geometry half of `HighlightViewerLayout`, plan P14) held beside the
// pictures they size (the What's-new PAGE is Classic's since 0.3.6): the row and poster cards of the after-update plate, the highlight card's whole geometry and
// the after-update plate's height budget, the viewer plate's geometry, its fixed colours and its pager. They are data, not decisions (WVTH003 allows literals only in `Rules\**`). The stepping rule (`HighlightPaging`), the highlight cap
// (`HighlightMax`) and every decision the pages take stay core.

using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's highlight viewer and after-update plate.</summary>
public static class WinUiReleaseNotesRules
{
    /// <summary>The 12 DIP meta size (the row card's "Read more").</summary>
    public const float MetaSize = 12f;

    // ── the row card, the card chrome, the kind pill ──────────────────────────────────────────────────────────────────

    /// <summary>The ROW card (the after-update plate's cards at Compact): the poster's size and decode width, the card's padding, the gap between the poster and
    /// the copy and between the copy's lines.</summary>
    public const float RowPosterW = 120f, RowPosterH = 68f, RowPosterDecodePx = 240f, RowCardPad = 8f, RowCardGap = 12f, RowCopyGap = 4f;

    /// <summary>The ROW card's title size and line, and the gap between the row cards in the plate's column.</summary>
    public const float RowTitleSize = 14f, RowTitleLine = 20f, RowListGap = 8f;

    /// <summary>The kind pill's height, the play glyph's disc and the "Read more" row's gap and size.</summary>
    public const float KindPillHeight = 20f, PlayDisc = 32f, ReadMoreGap = 4f;

    /// <summary>The decode width of a poster (the authored width: without it the poster is a smear).</summary>
    public const float PosterDecodePx = 1200f;

    /// <summary>The code span's size in the viewer's sentences.</summary>
    public const float CodeSpanSize = 12f;

    // ── the kind pill's shadow (issue #89 L3: 0/1/4 #00000066) ────────────────────────────────────────────────────────────

    /// <summary>The kind pill's shadow.</summary>
    public static ShadowSpec KindPillShadow => new(Blur: 4f, OffsetY: 1f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x66));

    // ── the viewer plate ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Deliberate literals (ch 28 §4.6): rgba(0,0,0,.72) — the media scrim (.55) reads thin stacked over the plate's own smoke.</summary>
    public static readonly ColorF ViewerVeil = ColorF.FromRgba(0, 0, 0, 184);

    /// <summary>The unselected pips and their hover: the theme's strong control fill and secondary ink, so they read on the plate in
    /// light as well as dark (the white 82 / 140 they were vanished on the light plate). Properties: tokens follow a theme switch.</summary>
    public static ColorF PipOff => Tok.FillControlStrong;
    public static ColorF PipOffHover => Tok.TextSecondary;

    /// <summary>The chrome's dimmed and hover media-scrim alphas (.40 / .70).</summary>
    public const float ChromeDimmedAlpha = 0.40f, ChromeHoverAlpha = 0.70f;

    /// <summary>The plate's shadow.</summary>
    public static ShadowSpec ViewerPlateShadow => new(Blur: 90f, OffsetY: 40f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x99));

    /// <summary>The pager's dot, selected capsule, gap and width tween; the row's height and top margin (dot centres 17 DIP from the band and from the title).</summary>
    public const float PipDot = 6f, PipSelected = 14f, PipGap = 8f, PipMs = 120f, PagerRowHeight = 24f, PagerRowTop = 8f;

    /// <summary>The chrome circle's glyph sizes (chevrons / close), a dimmed end button's opacity.</summary>
    public const float ChevronGlyph = 16f, CloseGlyph = 12f, DimmedOpacity = 0.3f;

    /// <summary>The viewer's text column: the title's size and line, the body's size and line, the column's measure and the action row's gap.</summary>
    public const float ViewerTitleSize = 20f, ViewerTitleLine = 28f, ViewerBodySize = 14f, ViewerBodyLine = 20f, ViewerTextMaxWidth = 720f, ViewerActionGap = 8f;

    /// <summary>The text half's padding (a pager above it takes the smaller top).</summary>
    public static Edges4 ViewerTextPadding(bool hasPager) => new(24f, hasPager ? 8f : 16f, 24f, 24f);

    /// <summary>The watch pill's gap.</summary>
    public const float WatchGap = 6f;

    // ── the after-update plate ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The plate's welcome headline size, its tagline's size and measure and the footer's vertical padding.</summary>
    public const float PlateWelcomeSize = 26f, PlateTaglineSize = 14f, PlateTaglineMaxWidth = 560f, PlateFooterPadY = 14f;

    /// <summary>The highlight card's fixed geometry and the after-update plate's height budget, as PURE arithmetic (was <c>ReleaseNotes.HighlightCardMetrics</c>).
    /// The card and the plate RENDER from these constants and the tests hold the 620 DIP plate cap against them, so a padding someone nudges fails a test
    /// instead of a screenshot.</summary>
    public static class Card
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

    /// <summary>The viewer plate's geometry (design §B.1), pure (was the geometry half of <c>ReleaseNotes.HighlightViewerLayout</c>; the stepping rule is
    /// core's <c>HighlightPaging</c>).</summary>
    public static class Viewer
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

        /// <summary>The 16:9 band height, from the plate's OWN width — always, poster or not. L4 (issue #89): a
        /// no-poster slide used to get a flat 120 DIP band regardless of plate width; the prototype keeps the same
        /// <c>w·9/16</c> rule either way (a tinted band, not a smaller one).</summary>
        public static float ImageHeight(float plateWidth) => MathF.Round(plateWidth / PosterAspect);

        public static float PlateMaxHeight(float vpH) => vpH - PlateMarginY;
    }
}
