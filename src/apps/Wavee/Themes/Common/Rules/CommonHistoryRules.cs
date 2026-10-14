// ── Themes/Common/Rules/CommonHistoryRules.cs — the numbers of the history page (themes plan 6.5, T11c) ──────────────────────────
//
// What `Shell.History.UI.cs` held as file-level constants and inline literals, moved verbatim with the picture. Data, not decisions
// (WVTH003 allows literals only here, in `Rules\**`); the DECISIONS are `Shell.History` (core).

namespace Wavee.Themes.Common;

/// <summary>The constants of the history page.</summary>
public static class CommonHistoryRules
{
    /// <summary>One visit's row height.</summary>
    public const float RowHeight = 56f;

    /// <summary>The row's glyph cell.</summary>
    public const float IconBox = 36f;

    /// <summary>The row's delete button cell.</summary>
    public const float DeleteBox = 28f;

    /// <summary>The title glyph beside the page hero.</summary>
    public const float TitleGlyphSize = 22f;

    /// <summary>The row's glyph inside its cell.</summary>
    public const float RowGlyphSize = 16f;

    /// <summary>The delete button's glyph.</summary>
    public const float DeleteGlyphSize = 12f;

    /// <summary>A stat pill's, a count pill's, a meta line's and a badge's text size.</summary>
    public const float SmallTextSize = 12f;

    /// <summary>The line height of that text.</summary>
    public const float SmallTextLine = 16f;

    /// <summary>The search box's minimum height.</summary>
    public const float SearchMinHeight = 36f;

    /// <summary>The sort combo's width.</summary>
    public const float SortComboWidth = 160f;

    /// <summary>The gap between a row's title and its meta line.</summary>
    public const float TitleMetaGap = 2f;

    /// <summary>The delete button at rest.</summary>
    public const float DeleteRestOpacity = 0.5f;

    /// <summary>A row this build has no page for (retired, or developer-gated): dimmed, inert.</summary>
    public const float InertRowOpacity = 0.6f;

    /// <summary>The playlist row's one tinted cell: the accent's alpha.</summary>
    public const float PlaylistTintAlpha = 0.12f;

    /// <summary>The weight of a stat figure.</summary>
    public const int StatFigureWeight = 600;

    /// <summary>The group header's rule inset (either side) and its bottom margin.</summary>
    public const float GroupRuleInset = 4f;
}
