// ── Themes/WinUi/Structure/Rules/WinUiPodcastEpisodeRules.cs — the WinUI episode page's numbers and keys (podcast pages v2, concept "Episode") ─────────────
//
// The concept's CSS (winui-podcast-v2.html, "episode page"): the hero (a 16:9 still beside the words), the chapter map card, the "Up next" card, the two
// 456-tall cards (Chapters | About | Comments at 400 beside the Transcript) and the rows under them. The per-tier numbers are `Tiers.PodcastEpisode.cs`.

using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The WinUI episode page's fixed numbers and node keys.</summary>
internal static class WinUiPodcastEpisodeRules
{
    // ── keys ──
    public const string PageKey = "pe-page", ScrollKeyPrefix = "pe-scroll:", HeroKey = "pe-hero", MapKey = "pe-map", NextUpKey = "pe-nextup",
        ColsKey = "pe-cols", SectionsKey = "pe-sections", TranscriptKey = "pe-transcript", MoreKey = "pe-more", SimilarKey = "pe-similar", BarKey = "pe-bar";

    // ── the hero ──
    /// <summary>The still's aspect (16:9) and its corner clock and Video tag insets.</summary>
    public const float StillAspect = 16f / 9f, StillTagInset = 8f, StillTagPadX = 6f, StillTagH = 20f;
    /// <summary>The crumb: the show's tiny art, the gap, the chevron.</summary>
    public const float CrumbArt = 20f, CrumbArtRadius = 3f, CrumbGap = 8f, CrumbChevron = 12f;
    /// <summary>The gap between the words' rows and the action row's top margin.</summary>
    public const float MainGap = 6f, ActionTop = 10f, ActionGap = 8f;
    /// <summary>The in-progress mini bar beside the split: its width and the gap over the bar.</summary>
    public const float MiniBarW = 220f, MiniBarCompactW = 120f, BarGap = 4f;
    /// <summary>The WinUI progress bar: a 3-DIP fill on a 1-DIP track.</summary>
    public const float BarFill = 3f, BarTrack = 1f, BarRadius = 1.5f;

    // ── the chapter map ──
    public const float MapPadX = 16f, MapPadTop = 12f, MapPadBottom = 14f, MapGap = 10f, CardGap = 12f;
    /// <summary>The segment band: its height, the gap between segments, a segment's rest and hover height, its floor and radius.</summary>
    public const float SegBand = 20f, SegGap = 3f, SegRest = 8f, SegHover = 12f, SegMin = 6f, SegRadius = 4f;
    /// <summary>The thumb on the current segment: its outer and inner discs.</summary>
    public const float Thumb = 18f, ThumbDot = 10f;
    /// <summary>The thumb's ring stroke and the segments' rest and hover fills (white on the card, light and dark through alpha).</summary>
    public const float ThumbStroke = 1f;

    // ── the "Up next" card ──
    public const float NextArt = 48f, NextArtRadius = 4f, NextPadY = 12f, NextGap = 16f;

    // ── the two cards ──
    public const float ColsAside = 400f, CardsH = 456f, CardsNarrowH = 440f, SelBarPad = 4f, ListBodyPad = 8f;
    /// <summary>A chapter row: the number column, the check column, the row's gap, padding, floor and the current row's accent tick.</summary>
    public const float ChNum = 28f, ChCheck = 20f, ChGap = 8f, ChPadX = 8f, ChPadY = 6f, ChMinH = 52f, ChCheckGlyph = 12f,
        ChTickW = 3f, ChTickH = 16f, ChTickRadius = 2f, ChBarTop = 3f;
    /// <summary>The transcript header and body.</summary>
    public const float TrHeadMinH = 48f, TrHeadPadL = 16f, TrHeadPad = 8f, FindW = 176f, TrPadX = 16f, TrPadBottom = 16f;
    /// <summary>The sticky chapter heading's padding.</summary>
    public const float TrHeadingTop = 10f, TrHeadingBottom = 8f, TrHeadingGap = 10f;
    /// <summary>A transcript line: the time column, the column gap, the text's padding, its type (15/22) and the time's (12/24), the gap over a line, the
    /// current line's accent stroke, and the measured list's estimate per line.</summary>
    public const float TrTimeW = 64f, TrColGap = 8f, TrTextPadX = 8f, TrTextPadY = 4f, TrTextSize = 15f, TrTextLine = 22f, TrTimeLine = 24f,
        TrLineGap = 4f, TrCurrentStroke = 3f, TrLineEstimate = 34f, TrHeadingEstimate = 74f, TrTextMax = 600f;

    // ── the sections under the cards ──
    public const float SectionTop = 28f, SectionBottom = 8f, RowMinH = 64f, RowGap = 12f, RowArt = 48f, GridGap = 12f, GridCardPad = 12f, GridCardArtGap = 8f,
        GridSmallCard = 136f;

    // ── the 48 bar ──
    public const float BarH = 48f, BarCover = 32f, BarCoverRadius = 4f, BarGapX = 12f, BarReveal = 44f;

    // ── the wash: the cover's dark colour at the page top, fading out before the cards ──
    public const float WashH = 420f, WashTop = 0.30f, WashMid = 0.10f, WashMidAt = 0.55f;

    /// <summary>A show tone (0xAARRGGBB, 0 = none) as a colour; null when there is none.</summary>
    public static ColorF? ToneOf(uint argb)
        => argb == 0u ? null : ColorF.FromRgba((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, byte.MaxValue);

    /// <summary>The find highlight (the concept's rgba(255,214,10,.3)).</summary>
    public static ColorF FindMark => ColorF.FromRgba(255, 214, 10, 77);
}
