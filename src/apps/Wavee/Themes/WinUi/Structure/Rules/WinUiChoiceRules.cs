// ── Themes/WinUi/Structure/Rules/WinUiChoiceRules.cs — the numbers of WinUI's choice controls (winui menus/podcasts) ──────────────────────
//
// What `Views\Choice\WinUi*.cs` read instead of a literal (WVTH003). Boards: W-Flyouts ("Sort and view menu · Library toolbar": a DropDownButton
// "Recently added ⌄" over a MenuFlyout with caption group headers and radio rows), W-Library (the filter, the sort and the grid / list pair in one 32
// row), W-LibraryList (the scope SelectorBar "In your library | All releases" and the "Newest ⌄" sort), W-Show (the filter SelectorBar "All · 14",
// the find box and "Newest ⌄"). The menu's own metrics (36 rows, 14 text, 4 backplates, the 8 acrylic plate) are the engine MenuFlyout's.

namespace Wavee.Themes.WinUi;

/// <summary>The numbers of WinUI's choice controls.</summary>
public static class WinUiChoiceRules
{
    /// <summary>The stock control height (W-Library's toolbar row: the filter, the sort and the view pair all 32).</summary>
    public const float ControlH = 32f;

    /// <summary>The Compact touch target (W-*-Compact: every toolbar control 40).</summary>
    public const float CompactH = 40f;

    /// <summary>The view pair's cell: 40 wide on the 32 row (W-Library), square at a taller row.</summary>
    public const float ViewCellW = 40f;

    /// <summary>The view pair's glyph (Fluent 16).</summary>
    public const float ViewGlyph = 16f;

    /// <summary>The view pair's plate: the control stroke around both cells, 1 wide, the control radius.</summary>
    public const float ViewStroke = 1f;

    /// <summary>The gap between the view pair and the "..." menu button.</summary>
    public const float ViewGap = 4f;

    /// <summary>A pivot item's height (the stock SelectorBar item: 12,10,12,7 around a 20 line, 40).</summary>
    public const float PivotItemH = 40f;

    /// <summary>A pivot's leading inset: the first item's text on the column edge (its 12 padding hangs left of it).</summary>
    public const float PivotLead = -12f;

    /// <summary>The word that joins a pivot word to its count ("All · 14", W-Show).</summary>
    public const string CountJoin = " · ";
}
