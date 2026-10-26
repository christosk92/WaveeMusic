// ── Theming/Contracts/ViewKeys.TitleBar.cs — the title bar's published view keys (themes plan 6.0, T4b) ───────────────────
//
// T4b publishes the next three keys of the Shell family (T4a: `Shell.Frame`, `Shell.ContentState`): the merged chrome row, the
// search (the field in the row, or the magnifier and its flyout) and the search suggestions popup body.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The merged title bar row: the pane toggle, the history buttons and the tab strip, the window-centred search, the
    /// identity cluster and the caption buttons. Mounted by the frame's template. Props: <see cref="ShellTitleBarProps"/>.</summary>
    public static readonly ViewKey<ShellTitleBarProps> ShellTitleBar = new("Shell.TitleBar");

    /// <summary>The search: the omnibar field in the row's centre column (<see cref="SearchForm.Field"/>) or the magnifier that
    /// opens the omnibar in a flyout (<see cref="SearchForm.Icon"/>). Mounted by the title bar's template. Props:
    /// <see cref="ShellSearchProps"/>.</summary>
    public static readonly ViewKey<ShellSearchProps> ShellSearch = new("Shell.Search");

    /// <summary>The search suggestions popup body: the query rows, the rich rows, the pending skeleton, the notices. Mounted by the
    /// search view's presenter. Props: <see cref="SearchSuggestionsProps"/>.</summary>
    public static readonly ViewKey<SearchSuggestionsProps> ShellSearchSuggestions = new("Shell.SearchSuggestions");
}
