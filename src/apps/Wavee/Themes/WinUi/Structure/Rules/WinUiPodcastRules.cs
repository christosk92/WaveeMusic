// ── Themes/WinUi/Structure/Rules/WinUiPodcastRules.cs — the numbers of WinUI's podcast pages v2 (the shared kit and the show page) ────────────────
//
// Concept `winui-podcast-v2.html` (theme=winui): the 4 px grid, Title Large 40/52 for the show (Title 28/36 below 1008), Subtitle 20/28 sections, Body
// 14/20, Caption 12/16; 8 between buttons, 12 between cards, 16 inside surfaces; the page gutter is WinUI's (`WinUiTiers.InsetOf`, 36 / 24 / 16).
// `WinUiPodcastKitRules` holds the kit's numbers (the episode and audiobook pages reuse them); `WinUiPodcastShowTiers` the show page's tier record.
// The tiers are the WINDOW's (the concept's table: 1008 and up = list + a 360 pane; 641-1007 one column; 640 and under stacked).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>The podcast kit's geometry (DIP) and motion.</summary>
internal static class WinUiPodcastKitRules
{
    // ── the page scaffold ──
    /// <summary>The collapsed header bar (concept `.compact`): 48 tall, a 32 cover, 12 between its parts; it fades and drops 8 in over 167 ms.</summary>
    public const float BarH = 48f, BarCover = 32f, BarGap = 12f, BarDrop = -8f, BarCoverRadius = 4f;
    public const int BarCoverDecode = 64;
    public const float BarFadeMs = 167f;
    /// <summary>The cover-tone wash at the top of the page: 232 tall, fading to nothing before the list.</summary>
    public const float WashH = 232f;
    /// <summary>The wash's strength at its top and its middle (the concept's 64 % radial over a 22 % linear).</summary>
    public const float WashTop = 0.42f, WashMid = 0.16f, WashMidStop = 0.45f;
    /// <summary>The page's foot (the dock reserve is added by the caller).</summary>
    public const float PageFoot = 48f;

    // ── the smart SplitButton ──
    public const float SplitChevron = 12f, SplitDividerInk = 0.35f;

    // ── the episode row (concept `.row`) ──
    /// <summary>The play button (36; 40 at Compact), its glyph and its ring's stroke.</summary>
    public const float PlayButton = 36f, PlayButtonCompact = 40f, PlayGlyph = 14f, RingStroke = 2.5f, RingTrackAlpha = 0.16f;
    /// <summary>The row's padding (10 / 8; 8 at Compact), its gap between columns (12) and between rows (2), its corner (4).</summary>
    public const float RowPadY = 10f, RowPadX = 8f, RowPadCompact = 8f, RowColGap = 12f, RowGap = 2f;
    /// <summary>The selection pill on the row's leading edge (3 x 16).</summary>
    public const float SelectPillW = 3f, SelectPillH = 16f;
    /// <summary>The meta line: the New dot (6), the gap between its parts (6), a meta glyph (12).</summary>
    public const float NewDot = 6f, MetaGap = 6f, MetaGlyph = 12f;
    /// <summary>A header line's word links (topics, genres): the hover plate's side padding (pulled back by the margin, so the word stands where
    /// its text would) and the gap between a separator and its word.</summary>
    public const float WordLinkPadX = 4f, WordLinkGap = 6f;
    /// <summary>The row's action buttons (32; 40 at Compact) and their glyph.</summary>
    public const float RowAction = 32f, RowActionCompact = 40f;
    // The row's forms (the state sets the weight): the lead is a card with a headline, a row at rest is the title over its line of description
    // with the date and the length at its end, a row in progress carries its bar, a played row is one quiet line. A numbered show keeps its
    // numbers in a lane before the titles.
    /// <summary>The numeral lane (the row-number voice, 20/28) and the lead's (28/36).</summary>
    public const float NumeralW = 36f, NumeralLeadW = 48f;
    /// <summary>The lead: a card (16 inside), the 40 accent play, a section-head title over two lines of what the episode is about.</summary>
    public const float LeadPad = 16f, LeadPlay = 40f, LeadGap = 4f, LeadBlurbW = 640f;
    public const int LeadTitleLines = 2, LeadBlurbLines = 2;
    /// <summary>A played row's vertical padding (one line, the play button's height).</summary>
    public const float RowPadPlayed = 2f;
    /// <summary>A row in progress: its 3-DIP bar under the title.</summary>
    public const float RowBarH = 3f, RowBarW = 240f, RowBarTop = 6f;

    // ── a section header and the cards ──
    public const float SectionGap = 8f, SectionTop = 24f, SectionBottom = 12f;
    /// <summary>A description's Body size (14/20).</summary>
    public const float BodySize = 14f;
    /// <summary>"More like this": a card's cover, its corner, the gap between cards, a row card's cover (the pane's rows).</summary>
    public const float CardCover = 160f, CardRadius = 8f, CardGap = 12f, RowCardCover = 40f, RowCardRadius = 4f, RowCardGap = 12f;
    public const int CardDecode = 320, RowCardDecode = 96;


    /// <summary>The bar's entrance and exit: a fade with an 8 drop (concept `.compact` 167 ms).</summary>
    public static readonly EnterExit BarFade = new(Dy: BarDrop, Opacity: 0f, Active: true);

    /// <summary>The wash: the cover's tone from the top, fading out before the list. <paramref name="tone"/> 0xAARRGGBB, 0 = none.</summary>
    public static GradientSpec? Wash(uint tone)
    {
        if (tone == 0u) return null;
        var c = ColorF.FromRgba((byte)(tone >> 16), (byte)(tone >> 8), (byte)tone, 255);
        return GradientDown(new GradientStop(0f, c with { A = WashTop }), new GradientStop(WashMidStop, c with { A = WashMid }),
            new GradientStop(1f, c with { A = 0f }));
    }
}

/// <summary>The show page's fixed numbers.</summary>
internal static class WinUiPodcastShowRules
{
    /// <summary>The details pane (360), the gap between the list and it (24), its sticky top (the bar + 16) and its padding.</summary>
    public const float PaneW = 360f, ColGap = 24f, PaneTop = 64f, PanePad = 16f, PanePadTop = 4f, PaneFoot = 24f;
    /// <summary>The toolbar (52 tall, sticky under the bar) and a group header (36, sticky under the toolbar).</summary>
    public const float ToolbarH = 52f, GroupButtonPad = 8f;
    /// <summary>The hairline at a pinned group header's foot.</summary>
    public const float PinnedRule = 1f;
    /// <summary>The jump grid: three columns of 56-tall cells, 4 apart, 12 inside, at most 340 wide.</summary>
    public const float JumpCellH = 56f, JumpGap = 4f, JumpPad = 12f, JumpW = 340f;
    public const int JumpCols = 3;
    /// <summary>The up row (min 40 tall), its gap; the commands' gap.</summary>
    public const float UpMinH = 40f, UpGap = 16f, UpLabelGap = 2f, UpSubGap = 10f, CommandGap = 8f, IdGap = 4f, EyebrowGap = 6f;
    /// <summary>The pane's description clamp (4 lines), the rating card's number (Title 28/36).</summary>
    public const int PaneClamp = 4, TitleLines = 3;
    /// <summary>A small hyperlink's own side padding (the eyebrow's kind word stands on the column edge), the pane's action glyph.</summary>
    public const float LinkPadX = 4f, PaneGlyph = 16f;
    public const float RatingCardPad = 12f, RatingStar = 16f, FactsColGap = 16f, FactsRowGap = 6f, FactLabelW = 96f;
    /// <summary>The empty list's air.</summary>
    public const float EmptyPad = 32f;
    /// <summary>A tag in the eyebrow (Video, Serial): 20 tall, 6 inside, r4.</summary>
    public const float TagH = 20f, TagPadX = 6f, TagGap = 4f, TagGlyph = 12f;
    /// <summary>The meta line's star, the gap after the commands' column, the compact filter ComboBox's least width.</summary>
    public const float MetaStar = 14f, ComboW = 200f, SortW = 32f, HintPad = 24f, ChapterTimeW = 56f;
    /// <summary>The rating card's five stars (a rating x100 rounds to whole stars); the semibold weight.</summary>
    public const int Stars = 5, StarRound = 50, StarScale = 100;
    public const ushort Strong = 600;
    /// <summary>The virtualised list's seeded extents (a realized item reports its measured one): a row (one line of meta; with a line of description),
    /// a foot, the hero and About before they measure; and the content types of the three prefix parts (above every list kind).</summary>
    public const float RowEstimate = 58f, RowEstimateTall = 78f, FootEstimate = 64f, HeroEstimate = 276f, AboutEstimate = 60f;
    public const int PrefixType = 16;
    /// <summary>A row at rest, measured (10 + 36 + 10; with a line of description under its title 60; a played row 2 + 36 + 2; the lead card with
    /// a one-line title and two lines of description): the list seeds them exactly, so a jump lands on its year.</summary>
    public const float RowHeight = 56f, RowHeightDescribed = 60f, RowHeightPlayed = 40f, RowLeadEstimate = 126f;
    public const string PageKey = "podcast-show";
}

