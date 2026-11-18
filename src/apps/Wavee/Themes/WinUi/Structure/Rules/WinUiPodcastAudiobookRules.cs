// ── Themes/WinUi/Structure/Rules/WinUiPodcastAudiobookRules.cs — the numbers of WinUI's audiobook page (podcast pages v2) ─────────────────────────────
//
// Source: the concept `C:/WAVEE/scratch/sphome/winui-podcast-v2.html`, page "Audiobook", WinUI, "Chapters as: Line + contents" (its CSS: .hero.book,
// .bline/.segsb/.seg, .contents/.cl, .babout/.facts/.rate, .bgrid, .compact, .wash, .allow). Tiers: Compact < 641 (the concept's 500 window, .w-s),
// Small 641-1007 (900, .w-m), Standard >= 1008 (1400, .w-l). The page gutter is the frame's (`WinUiTiers.InsetOf`).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's audiobook page: keys and fixed geometry (DIP).</summary>
internal static class WinUiPodcastAudiobookRules
{
    // ── keys ──
    public const string PageKey = "ab-page:", ScrollKey = "ab-scroll:", HeroKey = "ab-hero", BarKey = "ab-bar", BodyKey = "ab-body",
        LineKey = "ab-line", ContentsKey = "ab-contents", AboutKey = "ab-about", MoreKey = "ab-more", PremiumKey = "ab-premium",
        RowKey = "ab-row:", SegKey = "ab-seg:", PrimaryKey = "ab-primary", SampleKey = "ab-sample", SpeedKey = "ab-speed", SleepKey = "ab-sleep",
        LibraryKey = "ab-library", KindKey = "ab-kind", CmdsKey = "ab-cmds", UpKey = "ab-up", ColumnsKey = "ab-columns", ChaptersKey = "ab-chapters";

    // ── motion across a tier crossing ──

    /// <summary>The hero's two arrangements (cover beside the words, Small and up; the Compact stack): a DECLARED swap family, cross-faded.</summary>
    public static readonly EnterExit HeroSwap = SwapFamilies.Declare("WinUi.Audiobook.HeroSwap", new EnterExit(Opacity: 0f, Active: true));

    /// <summary>The cross-fade's timing.</summary>
    public static MotionTokenDef HeroSwapMotion => MotionTok.ControlFast;

    /// <summary>A block of the page (the Premium line, the columns, a card, the cards' column): it moves and resizes with a crossing, laid out at once and
    /// revealed, so nothing under it jumps.</summary>
    public static readonly LayoutTransition Block = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(WinUiDiscoveryMotion.ReflowMs, Easing.SmoothOut), SizeMode.Reveal);

    // ── the hero ──
    public const float CoverRadius = 4f, HeroColGap = 24f, HeroColGapCompact = 16f, HeroRowGap = 12f, HeroRowGapCompact = 16f;
    public const float IdGap = 4f, EyebrowGap = 6f, BylineGap = 20f, BylineTop = 6f, MetaGap = 6f, StarGlyph = 14f, CmdGap = 8f;
    public const float SubtitleSize = 18f, SubtitleLine = 24f, SubtitleSizeCompact = 14f, SubtitleLineCompact = 20f;
    public const float TagHeight = 16f, TagPadX = 5f, TagSize = 11f, TagLine = 14f, TagRadius = 3f;
    public const float UpGap = 16f, UpGapCompact = 10f, UpMinHeight = 40f, PrimaryMinWidth = 132f, LabelGap = 2f, LabelBarGap = 10f;
    public const float BarHeight = 3f, BarTrack = 1f, BarRadius = 1.5f, Control = 32f, ControlCompact = 40f;
    public const int CoverDecode = 400;
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
    /// <summary>The contents page windows its rows on the page scroll from this many chapters, in chunks of <see cref="WindowChunk"/> rows.</summary>
    public const int WindowFrom = 24, WindowChunk = 8;
    public const float RowMin = 36f, RowMinCompact = 44f, RowPadX = 8f, RowPadLeft = 12f, RowGap = 10f, NumberWidth = 24f, StartWidth = 52f;
    public const float NameSize = 15f, NameLine = 22f, MatterSize = 14f, Margin = 2f, LeaderHeight = 1f, LeaderMin = 20f, LeaderDot = 1f, LeaderGap = 3f, LeaderLift = -4f;
    public const float MarginRest = 0.08f, MarginDone = 0.34f, LeaderAlpha = 0.3f, LeaderDoneAlpha = 0.14f;

    // ── the About card (.babout, .facts, .rate, .trailer) ──
    // ── more by the author (the kit's section header and cards) ──
    public const float SectionTop = 28f, SectionBottom = 8f;

    /// <summary>About this book's lines before "Read more".</summary>
    public const int AboutLines = 8;
    public const float AboutPad = 16f, AboutGap = 14f, ParagraphGap = 10f, FactsLabel = 88f, FactsRowGap = 6f, FactsColGap = 16f;
    public const float SampleButton = 36f, SampleGap = 12f, RatePad = 10f, RatePadX = 12f, RateGap = 10f, RateBig = 28f, RateBigLine = 36f, RateStar = 16f;

}

/// <summary>The audiobook page's numbers per width tier.</summary>
internal sealed record WinUiAudiobookLayout(
    bool TwoColumns, bool Stacked, float Cover, float TitleSize, float TitleLine, float SubtitleSize, float SubtitleLine, float BarWidth,
    float ColumnGap, float RowMin, bool LongLeft, bool ToolWords, float HeroTop, float HeroBottom, float ColGap, float RowGap, float Control)
{
    static readonly WinUiAudiobookLayout s_standard = new(true, false, 200f, 40f, 52f, 18f, 24f, 220f, 24f, 36f, true, true, 32f, 24f, 24f, 12f, 32f);
    static readonly WinUiAudiobookLayout s_small = new(false, false, 152f, 28f, 36f, 18f, 24f, 120f, 12f, 36f, false, false, 32f, 24f, 24f, 12f, 32f);
    static readonly WinUiAudiobookLayout s_compact = new(false, true, 104f, 20f, 28f, 14f, 20f, 120f, 12f, 44f, false, false, 20f, 16f, 16f, 16f, 40f);

    public static WinUiAudiobookLayout Of(WidthTier content) => content switch
    {
        WidthTier.Compact => s_compact,
        WidthTier.Small => s_small,
        _ => s_standard,
    };
}
