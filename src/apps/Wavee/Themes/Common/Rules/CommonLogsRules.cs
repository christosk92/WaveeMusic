// ── Themes/Common/Rules/CommonLogsRules.cs — the numbers of the Common log viewer page (themes plan 6.5, T11c) ────────────────────
//
// From `Screens\LogsPage.UI.cs`: the page gutter, the fixed columns (ONE copy, read by the rows and the column header so they can never
// drift apart), the heights and the type sizes. Every literal look of `CommonLogs*.cs` is here (WVTH003 applies to Common like a structure).

namespace Wavee.Themes.Common;

/// <summary>The log viewer's geometry.</summary>
internal static class CommonLogsRules
{
    /// <summary>The toolbar's and the list card's side gutter.</summary>
    public const float GutterX = 36f;

    /// <summary>The list card's column header.</summary>
    public const float ColumnHeaderHeight = 32f;

    // The row's fixed columns: chevron 12 · dot 6 · time 92 · pill 58 × 22 · category 96 · message · ×N.
    public const float ChevronSlot = 12f, DotSlot = 6f, TimeColumn = 92f, LevelColumn = 58f, CategoryColumn = 96f;

    /// <summary>The level pill's height and the label's size and weight.</summary>
    public const float PillHeight = 22f, PillTextSize = 10f;

    /// <summary>One row's minimum height, and the list's estimate before a row is measured.</summary>
    public const float RowMinHeight = 36f, EstimatedRowExtent = 36f;

    /// <summary>The live tail's poll period (milliseconds).</summary>
    public const float PollMs = 750f;

    /// <summary>The picker caption's widest line.</summary>
    public const float CaptionMaxWidth = 200f;

    /// <summary>The filter box's minimum height and widest fill.</summary>
    public const float FilterMinHeight = 34f, FilterMaxFill = 360f;

    /// <summary>The session and category combos' widths.</summary>
    public const float SessionComboWidth = 320f, CategoryComboWidth = 180f;

    /// <summary>The sortable Time header's gap and arrow size.</summary>
    public const float SortGap = 4f, SortArrowSize = 10f;

    /// <summary>A twelve-point cell (time, category, footer, loading line, code block).</summary>
    public const float CellSize = 12f;

    /// <summary>The empty-filter state's glyph.</summary>
    public const float EmptyGlyphSize = 36f;

    /// <summary>The details' inner gap.</summary>
    public const float DetailGap = 4f;
}
