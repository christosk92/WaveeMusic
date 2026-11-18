// ── Themes/Classic/Structure/Rules/ClassicPodcastRules.cs — the numbers of Classic's podcast pages v2 (the kit, the show, the episode, the audiobook) ──
//
// Concept `winui-podcast-v2.html` (theme=classic), its CSS `.cpg` / `.c-aside` / `.c-main` / `.t-classic .row` and the boards C-Show, C-Album,
// C-Episode: a 280 cover column (240 below 1008) padded 24 / 12 / 24 / 16 with 12 between its parts, the list padded 24 (16 at Compact), the 28/36
// title (22/28 for an episode), 12/18 lines, a 4-DIP listened bar, rows 10 / 8 / 10 / 12 with the play button (36) at their end, lowercase 12/16
// group labels, a 52 list bar. Tier records: `Tiers.Podcast.cs`.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The podcast pages' geometry (DIP), weights, alphas and motion.</summary>
internal static class ClassicPodcastRules
{
    // ── the page ──
    public const float ColumnW = 280f, ColumnSmallW = 240f;
    public const float ColumnPadTop = 24f, ColumnPadEnd = 12f, ColumnPadBottom = 24f, ColumnPadStart = 16f, ColumnPadStacked = 16f, ColumnGap = 12f;
    public const float MainPad = 24f, MainPadCompact = 16f, MainFoot = 40f;
    public const float CoverStacked = 120f, StillStacked = 200f, CoverRadius = 8f, StillAspect = 16f / 9f;
    public const int CoverDecode = 512;
    /// <summary>The cover-tone ground under the whole page (dark: the cover's hue sunk to the dark background; light: a whisper of it).</summary>
    public const float GroundDark = 0.92f, GroundLight = 0.10f;
    /// <summary>A control: Classic's 32, Compact's 40.</summary>
    public const float Target = 32f, TargetCompact = 40f;

    // ── the column's words ──
    public const float KindGap = 6f, TitleLift = -4f, SubLift = -6f, LineSize = 12f, LineHeight = 18f, TitleSize = 28f, TitleLine = 36f,
        TitleSmallSize = 22f, TitleSmallLine = 28f, SubSize = 14f, SubLine = 20f;
    public const ushort Strong = 600;
    public const float TagH = 18f, TagPadX = 5f, TagGap = 4f, TagGlyph = 12f, TagSize = 11f, TagLine = 14f, TagStrokeAlpha = 0.6f;
    public const float CrumbArt = 16f, CrumbArtRadius = 3f;

    // ── the listened bar (concept `.lbar`) ──
    public const float LedgerH = 4f, LedgerGap = 2f, LedgerRadius = 2f, LedgerMin = 2f;
    public const float LedgerTrackAlpha = 0.12f, LedgerPlayedAlpha = 0.40f;

    // ── the primary (the cover-coloured full-width split) and what it plays ──
    public const float SplitChevron = 12f, SplitDivider = 1f, SplitDividerH = 20f, SplitDividerInk = 0.35f, SplitChevronInk = 0.86f, SplitMenuW = 32f;
    public const float UpGap = 8f, UpBarW = 88f, UpSubGap = 8f;
    public const float CommandGap = 6f;

    // ── the description, facts, trailer ──
    public const float DescSize = 12f;
    public const int DescLines = 8, RowDescLines = 1;
    public const float FactsColGap = 12f, FactsRowGap = 4f, FactLabelW = 64f;
    public const float TrailerButton = 40f, TrailerGap = 12f, PlayGlyph = 14f;

    // ── the list bar (filters, search, sort) and the group labels ──
    public const float ListBarH = 52f, ListBarGap = 8f, SearchW = 208f, SearchWSmall = 160f;
    public const float GroupH = 36f, GroupGap = 6f, GroupLabelSize = 12f, GroupLabelLine = 16f;
    public const float FilterPadX = 10f, FilterH = 32f, FilterGap = 4f, FilterCountGap = 4f, FilterUnderline = 3f, FilterUnderlineW = 16f;

    // ── the episode row (concept `.t-classic .row`) ──
    public const float RowPadTop = 10f, RowPadEnd = 8f, RowPadBottom = 10f, RowPadStart = 12f, RowPadCompact = 8f, RowColGap = 12f;
    public const float PlayButton = 36f, PlayButtonCompact = 40f, RowAction = 32f, RowActionCompact = 40f;
    public const float RingTrackAlpha = 0.16f, NewDot = 6f, MetaGap = 6f, MetaGlyph = 12f;
    public const float ExpandTop = 10f, ExpandGap = 10f, ExpandMs = 167f;

    // ── sections, cards ──
    public const float SectionTop = 28f, SectionBottom = 12f, SectionGap = 8f;
    public const float CardCover = 160f, CardGap = 16f, RowCardCover = 48f;
    public const float EmptyPad = 32f;
    /// <summary>The list's seeded extents (the measured ones replace them): a row (with its line of description), a group label's foot, the stacked
    /// column, the foot with More like this; the recycle pools of the page's own parts start here.</summary>
    public const float RowEstimate = 60f, RowEstimateTall = 78f, FootEstimate = 64f, ColumnEstimate = 720f, SimilarEstimate = 320f;
    public const int PartType = 16;
    public const float JumpCellH = 56f, JumpGap = 4f, JumpPad = 12f, JumpW = 340f;
    public const int JumpCols = 3;

    // ── the episode page ──
    public const float StillTagH = 20f, StillTagPadX = 6f, StillTagInset = 8f, StillTagGap = 4f;
    public const float MapPadX = 16f, MapPadTop = 14f, MapPadBottom = 16f, MapGap = 12f, SegGap = 2f, SegRest = 6f, SegHover = 10f, SegBand = 16f,
        SegRadius = 3f, SegMin = 4f, Thumb = 16f, ThumbHalf = 8f, ThumbDot = 6f, ThumbDotHalf = 3f, ThumbStroke = 1f;
    public const float CardsH = 452f, CardsHCompact = 420f, ChaptersCardW = 320f, CardRadius = 8f;
    public const float ChNum = 28f, ChGap = 10f, ChPadX = 8f, ChPadY = 8f, ChMinH = 52f, ChBarTop = 6f, ChBarH = 3f, ChCheck = 16f, ChCheckGlyph = 12f, ChTickW = 3f, ChTickH = 16f;
    public const float CardHeadPadX = 16f, CardHeadPadTop = 10f, BodyPad = 8f;
    public const float TrPadX = 16f, TrTimeW = 52f, TrColGap = 12f, TrLineGap = 6f, TrTextSize = 15f, TrTextLine = 22f, TrTextPadX = 6f, TrTextPadY = 2f,
        TrLineEstimate = 34f, TrHeadingEstimate = 64f, TrCurrentStroke = 3f, FindW = 200f, TrHeadingTop = 10f, TrHeadingBottom = 6f;
    public const float MiniBarH = 3f, MiniBarTrack = 1f, MiniBarRadius = 1.5f;
    public const string Dot = " · ";
    public static readonly ColorF FindMark = ColorF.FromRgba(0xFF, 0xD3, 0x3D, 0x66);

    // ── the audiobook ──
    public const float NoticePad = 12f, NoticeGap = 8f, NoticeGlyph = 16f;
    public const float BookSegH = 6f, BookSegBand = 16f, BookSegGap = 2f, BookSegMin = 4f, BookSegTrackAlpha = 0.14f, BookThumbOuter = 16f, BookThumbInner = 6f;
    public const float TocNum = 28f, TocLen = 72f, TocRowH = 36f, TocRowHCompact = 44f, TocGap = 10f, TocHeadPadBottom = 6f, TocMargin = 3f;
    public const float AboutW = 300f, AboutGap = 24f;
    public const int AboutLines = 6;

    /// <summary>A tab's count ("12"; "" while it is on its way or none).</summary>
    public static string CountLabel(int n, bool pending) => pending || n <= 0 ? "" : FormatCache.Int(n);

    /// <summary>The row expand: 167 ms, a FlowReveal so the rows after it ride it.</summary>
    public static readonly LayoutTransition Expand = new(TransitionChannels.Size, TransitionDynamics.Tween(ExpandMs, Easing.SmoothOut),
        Size: SizeMode.FlowReveal, Anchor: SizeAnchor.Parallax);
}
