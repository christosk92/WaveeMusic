// ── Themes/WinUi/Structure/Rules/WinUiPodcastAudiobookRules.cs — the numbers of WinUI's audiobook page (podcast pages v2) ─────────────────────────────
//
// Source: the concept `C:/WAVEE/scratch/sphome/winui-podcast-v2.html`, page "Audiobook", WinUI, "Chapters as: Line + contents" (its CSS: .hero.book,
// .bline/.segsb/.seg, .contents/.cl, .babout/.facts/.rate, .bgrid, .compact, .wash, .allow). Tiers: Compact < 641 (the concept's 500 window, .w-s),
// Small 641-1007 (900, .w-m), Standard >= 1008 (1400, .w-l). The page gutter is the frame's (`WinUiTiers.InsetOf`).

using Wavee.Theming.Contracts;
using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's audiobook page: keys and fixed geometry (DIP).</summary>
internal static class WinUiPodcastAudiobookRules
{
    // ── keys ──
    public const string PageKey = "ab-page:", ScrollKey = "ab-scroll:", HeroKey = "ab-hero", BarKey = "ab-bar", BodyKey = "ab-body",
        LineKey = "ab-line", ContentsKey = "ab-contents", AboutKey = "ab-about", MoreKey = "ab-more", PremiumKey = "ab-premium",
        RowKey = "ab-row:", SegKey = "ab-seg:", PrimaryKey = "ab-primary", SampleKey = "ab-sample", SpeedKey = "ab-speed", SleepKey = "ab-sleep",
        LibraryKey = "ab-library";

    // ── the wash (.wash: the cover's dark colour, faded out before the list) ──
    public const float WashHeight = 232f, WashTop = 0.5f, WashMid = 0.18f, WashMidAt = 0.55f;

    // ── the hero ──
    public const float CoverRadius = 4f, HeroColGap = 24f, HeroColGapCompact = 16f, HeroRowGap = 12f, HeroRowGapCompact = 16f;
    public const float IdGap = 4f, EyebrowGap = 6f, BylineGap = 20f, BylineTop = 6f, MetaGap = 6f, StarGlyph = 14f, CmdGap = 8f;
    public const float SubtitleSize = 18f, SubtitleLine = 24f, SubtitleSizeCompact = 14f, SubtitleLineCompact = 20f;
    public const float TagHeight = 16f, TagPadX = 5f, TagSize = 10f, TagLine = 14f, TagRadius = 3f;
    public const float UpGap = 16f, UpGapCompact = 10f, UpMinHeight = 40f, PrimaryMinWidth = 132f, LabelGap = 2f, LabelBarGap = 10f;
    public const float BarHeight = 3f, BarTrack = 1f, BarRadius = 1.5f;
    public const ushort Strong = 600;

    // ── the Premium line (.allow) ──
    public const float PremiumGap = 8f, PremiumBottom = 20f, PremiumGlyph = 14f, PriceGap = 12f;

    // ── the columns ──
    public const float AboutWidth = 380f, ColumnGap = 24f, ColumnGapNarrow = 12f, CardGap = 12f;

    // ── the book line (.bline, .segsb, .seg) ──
    public const float LinePadX = 16f, LinePadTop = 12f, LinePadBottom = 14f, LineGap = 10f;
    public const float SegBand = 20f, SegHeight = 8f, SegGap = 3f, SegMin = 6f, SegRadius = 4f, ThumbOuter = 18f, ThumbInner = 10f;
    public const float SegTrackAlpha = 0.14f;

    // ── the contents page (.contents, .cl) ──
    public const float ContentsPadX = 16f, ContentsPadTop = 6f, ContentsPadBottom = 16f, ContentsHeadPad = 8f, ContentsHeadBottom = 10f;
    public const float RowMin = 36f, RowMinCompact = 44f, RowPadX = 8f, RowPadLeft = 12f, RowGap = 10f, NumberWidth = 24f, StartWidth = 52f;
    public const float NameSize = 15f, NameLine = 22f, MatterSize = 14f, Margin = 2f, LeaderHeight = 1f, LeaderMin = 20f, LeaderLift = -4f;
    public const float MarginRest = 0.08f, MarginDone = 0.34f, LeaderAlpha = 0.3f, LeaderDoneAlpha = 0.14f;

    // ── the About card (.babout, .facts, .rate, .trailer) ──
    public const float AboutPad = 16f, AboutGap = 14f, ParagraphGap = 10f, FactsLabel = 88f, FactsRowGap = 6f, FactsColGap = 16f;
    public const float SampleButton = 36f, SampleGap = 12f, RatePad = 10f, RatePadX = 12f, RateGap = 10f, RateBig = 28f, RateBigLine = 36f, RateStar = 16f;

    // ── more by the author (.bgrid, .gc) ──
    public const float SectionTop = 28f, SectionBottom = 8f, GridGap = 12f, CardPad = 12f, CardRadius = 8f, CardArtRadius = 2f, CardArtGap = 8f;
    public const float CompactCardWidth = 136f, PageBottom = 48f;

    // ── the collapsing bar (.compact) ──
    public const float BarBand = 48f, BarCover = 32f, BarGap = 12f, BarReveal = 44f;

    /// <summary>The cover tone (0xAARRGGBB, 0 none) as a colour.</summary>
    public static ColorF ToneOf(uint argb) => ColorF.FromRgba((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, 255);
}

/// <summary>The audiobook page's numbers per width tier.</summary>
internal sealed record WinUiAudiobookLayout(
    bool TwoColumns, bool Stacked, float Cover, float TitleSize, float TitleLine, float SubtitleSize, float SubtitleLine, float BarWidth,
    float ColumnGap, int GridColumns, bool GridScrolls, float RowMin, bool LongLeft, bool ToolWords, float HeroTop, float HeroBottom,
    float ColGap, float RowGap)
{
    static readonly WinUiAudiobookLayout s_standard = new(true, false, 200f, 40f, 52f, 18f, 24f, 220f, 24f, 6, false, 36f, true, true, 32f, 24f, 24f, 12f);
    static readonly WinUiAudiobookLayout s_small = new(false, false, 152f, 28f, 36f, 18f, 24f, 120f, 12f, 4, false, 36f, false, false, 32f, 24f, 24f, 12f);
    static readonly WinUiAudiobookLayout s_compact = new(false, true, 104f, 20f, 28f, 14f, 20f, 120f, 12f, 5, true, 44f, false, false, 20f, 16f, 16f, 16f);

    public static WinUiAudiobookLayout Of(WidthTier content) => content switch
    {
        WidthTier.Compact => s_compact,
        WidthTier.Small => s_small,
        _ => s_standard,
    };
}
