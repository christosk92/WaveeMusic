// ── Themes/WinUi/Structure/WinUiOptions.cs — WinUI's structure option values and their reads (themes plan 6.2; P6 W0) ─────────────────
//
// The enum the `navigationPane` option stores (its values are what is stored: an appended member never re-means a stored value), the bundle
// of every structure value the policies depend on (`WinUiParameters`, what `WinUiPolicies.For` memoizes on), and the tracked reads a WinUI
// view uses where it draws with an option (WVTH005: only this structure reads its options). The options themselves are declared in
// `WinUiStructure.cs`.
//
// The six options of W-ThemeOptions (technical plan 6.2) are split by layer: the STRUCTURE's three are here (`navigationPane`, `tabs`,
// `artworkInRows`); the STYLE's three (`backdrop`, `density`, `useWindowsAccent`) are `WinUiStyle`'s (W1). WinUI has no hidden structure
// parameter (no opt-in or composition-only member), so it adds no line to `analyzer-input\WVTH011.matrix.txt`.

using Wavee.Theming;

namespace Wavee.Themes.WinUi;

/// <summary>How the navigation pane presents (`structure.navigationPane`). Persisted: never renumber, append.</summary>
public enum WinUiNavigationPane
{
    /// <summary>The 280 pane docked wherever it fits (Small and up); the minimal overlay at Compact.</summary>
    Expanded = 0,
    /// <summary>The 48 rail of icons; the hamburger opens the 280 pane over the page; the minimal overlay at Compact.</summary>
    Compact = 1,
    /// <summary>NavigationView's thresholds (W-Tiers): minimal below 641, the 48 rail from 641 to 1007, the docked 280 from 1008.</summary>
    Automatic = 2,
}

/// <summary>Every structure value the WinUI policies depend on: the three user options.</summary>
public readonly record struct WinUiParameters(WinUiNavigationPane NavigationPane, bool Tabs, bool ArtworkInRows)
{
    /// <summary>The declared defaults: Automatic, tabs on, artwork in rows.</summary>
    public static WinUiParameters Default => From(OptionValues.Empty);

    /// <summary>The values the composition built for the structure.</summary>
    public static WinUiParameters From(OptionValues structure) => new(
        structure.Get(WinUiStructure.NavigationPane.Key),
        structure.Get(WinUiStructure.Tabs.Key),
        structure.Get(WinUiStructure.ArtworkInRows.Key));
}

/// <summary>Tracked reads of WinUI's structure options, for a view that draws with one. Call them inside a render or a binding.</summary>
public static class WinUiOptions
{
    /// <summary>How the navigation pane presents (the `navigationPane` option; the window tier decides too, `WinUiPolicies`).</summary>
    public static WinUiNavigationPane NavigationPane() => ThemeHost.Options.Get(WinUiStructure.NavigationPane.Key);

    /// <summary>The title bar shows its 40 tab strip (the `tabs` option). Off: one 48 bar, the other tabs kept in the background.</summary>
    public static bool Tabs() => ThemeHost.Options.Get(WinUiStructure.Tabs.Key);

    /// <summary>Playlist, Liked and search rows show a 40 cover and two lines (the `artworkInRows` option). Album rows never do.</summary>
    public static bool ArtworkInRows() => ThemeHost.Options.Get(WinUiStructure.ArtworkInRows.Key);
}
