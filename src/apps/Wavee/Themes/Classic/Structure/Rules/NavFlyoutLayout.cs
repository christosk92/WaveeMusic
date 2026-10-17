// ── Themes/Classic/Structure/Rules/NavFlyoutLayout.cs — the numbers of Classic's rail flyouts (themes plan 6.1, T5c) ───────────
//
// What `Shell/Sidebar.UI.Flyout.cs` held as literals beside the section flyout and the folder flyout, moved verbatim with the
// pictures. `Views\Nav\ClassicNavFlyouts.cs` reads them (WVTH003 allows literals only here, in `Rules\**`). The row ladder's own
// numbers (pitch, art, the pane edge) are `SidebarGeometry`.

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's compact-rail flyouts.</summary>
internal static class ClassicNavFlyoutLayout
{
    /// <summary>Both panels are 300 DIP wide.</summary>
    public const float PanelWidth = 300f;

    /// <summary>The tallest the rows' list grows before it scrolls.</summary>
    public const float MaxListHeight = 420f;

    /// <summary>The section flyout's vertical padding around its list.</summary>
    public const float SectionPadY = 2f;

    /// <summary>The section flyout virtualizes its list past this many drawn rows.</summary>
    public const int VirtualizeAfter = 50;

    /// <summary>The folder flyout's header height (name + item count, and the back chevron).</summary>
    public const float FolderHeaderHeight = 40f;

    /// <summary>The folder flyout's gap between rows.</summary>
    public const float FolderRowGap = 1f;

    /// <summary>The back chevron's box and its glyph.</summary>
    public const float BackButton = 28f, BackGlyph = 14f;

    /// <summary>A sub-folder row's drill-in chevron.</summary>
    public const float DrillGlyph = 12f;

    /// <summary>The empty hint's minimum height.</summary>
    public const float EmptyMinHeight = 44f;
}
