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
