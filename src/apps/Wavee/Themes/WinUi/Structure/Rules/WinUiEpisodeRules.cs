// ── Themes/WinUi/Structure/Rules/WinUiEpisodeRules.cs — the numbers of WinUI's episode page (W-Episode, -Compact, -Small, -Huge; winui show pages) ──────
//
// The episode page's pictures (`Views\Episode\WinUiEpisode*.cs`) own these sizes. W-Episode: the pivot (a SelectorBar: About · Chapters · Transcript ·
// Comments) over the sections; at Standard and Huge two columns 36 apart (48 at Huge), About and Chapters (and at Huge "More from") on the left, the
// Transcript on the right; at Small About beside Chapters (W-Episode-Small); at Compact one column (W-Episode-Compact). The headings are WinUI's
// Subtitle (20/28 semibold, the Display face), the prose Body 14/20, a chapter row 40 tall with its 52-wide time.

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's episode page geometry (DIP).</summary>
internal static class WinUiEpisodeRules
{
    /// <summary>The pivot's row (a SelectorBar 40 tall, a hairline under it) and the air between it and the first section.</summary>
    public const float PivotHeight = 40f, PivotGap = 16f;

    /// <summary>The columns' gap (Standard, Small), and Huge's; the left column's weight against the right's (W-Episode: 594 | 457).</summary>
    public const float ColumnGap = 36f, HugeColumnGap = 48f, LeftGrow = 1.3f, RightGrow = 1f;

    /// <summary>The gap between the sections of one column, between a heading and its body.</summary>
    public const float SectionGap = 28f, HeadGap = 12f;

    /// <summary>A section heading: Subtitle 20/28.</summary>
    public const float HeadSize = 20f, HeadLine = 28f;

    /// <summary>The prose: Body 14, clamped to five lines before "Read more" (W-Episode: four lines and a link).</summary>
    public const float AboutSize = 14f;
    public const int AboutLines = 5;

    /// <summary>A chapter row: 40 tall, the time's column 52, the row's padding across 12, the equalizer's edge.</summary>
    public const float ChapterRowH = 40f, ChapterTimeW = 56f, ChapterPadX = 12f, ChapterEqualizer = 12f;

    /// <summary>The transcript's box (it scrolls inside the page's scroll): the right column's height at two columns, the stacked one's.</summary>
    public const float TranscriptHeight = 480f, TranscriptStackedHeight = 420f;

    /// <summary>The reader's bottom air above the dock reserve and its edge fade.</summary>
    public const float ReaderBottom = 24f, EdgeFadeBand = 24f;

    /// <summary>The rating popover's width and its stars.</summary>
    public const float RatingWidth = 280f, RatingStar = 24f;
}
