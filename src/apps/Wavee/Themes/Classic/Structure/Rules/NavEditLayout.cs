// ── Themes/Classic/Structure/Rules/NavEditLayout.cs — the numbers of Classic's Edit outline (themes plan 6.1, T5d) ─────────────
//
// What `Shell/Sidebar.UI.Edit.cs` held as literals beside the edit bar and the Outline's rows, moved verbatim with the pictures.
// `Views\Nav\ClassicNavEdit.cs` reads them (WVTH003 allows literals only here, in `Rules\**`). The row heights, the pin fold and
// the hint height are core's `SidebarEditRules` (the edit model's extent arithmetic reads them too); the pane edge is
// `SidebarGeometry`.

using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's Edit outline.</summary>
internal static class ClassicNavEditLayout
{
    /// <summary>The edit bar's height.</summary>
    public const float BarHeight = 36f;

    /// <summary>The gap between the edit bar's children and between a row's children.</summary>
    public const float Gap = 4f;

    /// <summary>The edit bar's margin: the pane edge, the top inset, the pane edge, 2 below.</summary>
    public static Edges4 BarMargin => new(SidebarGeometry.PaneEdge, SidebarGeometry.PaneTopInset, SidebarGeometry.PaneEdge, 2f);

    /// <summary>The edit bar's padding.</summary>
    public static Edges4 BarPadding => new(8f, 0f, 4f, 0f);

    /// <summary>The padding of a header or a row (checkbox column to the grip).</summary>
    public static Edges4 RowPadding => new(4f, 0f, 4f, 0f);

    /// <summary>The scroll content's padding.</summary>
    public static Edges4 ContentPadding => new(4f, 3f, 4f, 8f);

    /// <summary>The rule above the Settings row.</summary>
    public static Edges4 SeparatorMargin => new(8f, 6f, 8f, 6f);

    /// <summary>The caption under the Outline.</summary>
    public static Edges4 FootPadding => new(8f, 8f, 8f, 4f);

    /// <summary>A hint row's padding: its caption sits at x 40, under the header text.</summary>
    public static Edges4 HintPadding => new(40f, 0f, 8f, 0f);

    /// <summary>The "Show all N pins" row's padding.</summary>
    public static Edges4 ShowAllPadding => new(40f, 0f, 4f, 0f);

    /// <summary>The checkbox / lock / filter glyph column's box.</summary>
    public const float GlyphBox = 24f;

    /// <summary>A pin's leading art box.</summary>
    public const float PinArtBox = 20f;

    /// <summary>The pin cover's edge.</summary>
    public const float PinCover = 20f;

    /// <summary>A hidden section or item draws at this opacity.</summary>
    public const float HiddenOpacity = 0.5f;

    /// <summary>The title text size of a header or a row.</summary>
    public const float TitleSize = 14f;
}
