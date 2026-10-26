// ── Themes/Classic/Structure/Rules/NavHeadLayout.cs — the numbers of Classic's navigation heads (themes plan 6.1, T5b) ─────────
//
// What `Shell/Sidebar.UI.cs`, `Sidebar.UI.Rows.cs` and `Sidebar.UI.Library.cs` held as literals beside the heads' builders, moved
// verbatim with the pictures: the section header's title, the Playlists filter box, the authored-empty pane, Your Library's page
// title, breadcrumb, retry banner and search box. They are data, not decisions: `Views\Nav\WinUiNavHead.cs`,
// `WinUiNavFilter.cs` and `WinUiNavLibraryHead.cs` read them (WVTH003 allows literals only here, in `Rules\**`). The row
// ladder's own numbers (the 40-px header, the 24-px inline button, the separator) are `WinUiNavGeometry`; the Library head's lane
// rules (inset, heights, tab order) are core's `SidebarLibraryHeadRules`, which the pure tests pin.

namespace Wavee.Themes.WinUi;

/// <summary>The constants of Classic's navigation heads.</summary>
public static class WinUiNavHeadLayout
{
    // ── the section header ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The header title's size (14 SemiBold, brightening to TextPrimary on hover).</summary>
    public const float SectionTitleSize = 14f;

    // ── the Playlists filter box ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The transient filter's field height (design V.11).</summary>
    public const float FilterHeight = 32f;

    /// <summary>The filter field's text size.</summary>
    public const float FilterFontSize = 13f;

    // ── the authored-empty pane ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The empty pane's sentence size (14 SemiBold, two lines at most).</summary>
    public const float EmptyTitleSize = 14f;

    // ── Your Library's head ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The page title's size ("Your Library", and the "· Albums" suffix beside it).</summary>
    public const float LibraryTitleSize = 14f;

    /// <summary>The gap between the title words and the dropdown chevron.</summary>
    public const float LibraryTitleGap = 6f;

    /// <summary>The minimum width of the page, sort and options menus the head opens.</summary>
    public const float MenuMinWidth = 220f;

    /// <summary>The search box's text size.</summary>
    public const float LibrarySearchFontSize = 14f;

    /// <summary>The breadcrumb's back button box.</summary>
    public const float BreadcrumbBack = 24f;

    /// <summary>The breadcrumb's folder name size.</summary>
    public const float BreadcrumbSize = 12f;

    /// <summary>The gap between the back button and the folder name.</summary>
    public const float BreadcrumbGap = 4f;

    /// <summary>The retry banner's gap between its glyph, its text and its button.</summary>
    public const float BannerGap = 8f;

    // ── the retry banner (S10, board `C-Sidebar` state failed): a caution plate under the head's rule, 8 in from the pane
    //    edges (the head's own 4 plus 4), 4 above and 6 below, padding 10,8, r4; a 16 "!" glyph, the 12/16 sentence (it
    //    wraps), and a standard Small "Retry" (24 tall). ──

    /// <summary>The banner's inset beyond the head's own pane edge, and its top and bottom margins.</summary>
    public const float BannerInsetX = 4f, BannerTop = 4f, BannerBottom = 6f;

    /// <summary>The banner's padding.</summary>
    public const float BannerPadX = 10f, BannerPadY = 8f;

    /// <summary>The banner's glyph: Segoe Fluent Icons U+E783 (the outlined "!" in a circle, absent from the engine's glyph
    /// table), 16.</summary>
    public const string BannerGlyph = "";
    public const float BannerGlyphSize = 16f;

    /// <summary>The longest the banner's sentence runs before it trims (it wraps at narrow widths).</summary>
    public const int BannerMaxLines = 3;
}
