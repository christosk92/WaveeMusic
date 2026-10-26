// ── Themes/WinUi/Structure/WinUiStructure.Frame.cs — the frame family's registrar (themes plan 6.2; P6 W2a) ──────────────────────────────
//
// W2a's keys: the window frame (`Views\Frame`: the nav column's 167 ms width, the r8 content layer, the panels' hosts) and its content states,
// the two-row title bar (`Views\TitleBar`), the search and its suggestions (`Views\Search`), the command palette, the history menu, the file-drop
// cue, the drag chip, the cover scrim (`Views\Palette`, `Views\FrameBits`), and the masthead with its page head (`Views\Masthead`, the
// BreadcrumbBar). Each replaces the stand-in's (Classic's) key by key. The nav pane's keys (`Shell.NavPane`, the narrow drawer) are W2b's and the
// panel's (`Shell.Panel`) W4a's: the frame leaves them their slots. Numbers: `Rules\WinUiFrameRules.cs`, `Rules\Tiers.Frame.cs`.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public sealed partial class WinUiStructure
{
    static partial void RegisterFrame(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.ShellFrame, WinUiFrame.Template, "winui.shell.frame");
        views.Add(ViewKeys.ShellContentState, WinUiContentState.Template, "winui.shell.content-state");
        views.Add(ViewKeys.ShellTitleBar, WinUiTitleBar.Template, "winui.shell.title-bar");
        views.Add(ViewKeys.ShellSearch, WinUiSearch.Template, "winui.shell.search");
        views.Add(ViewKeys.ShellSearchSuggestions, WinUiSearch.SuggestionsTemplate, "winui.shell.search-suggestions");
        views.Add(ViewKeys.ShellMasthead, WinUiMasthead.Template, "winui.shell.masthead");
        views.Add(ViewKeys.PageHead, WinUiMasthead.PageHead, "winui.page.head");
        views.Add(ViewKeys.ShellCommandPalette, WinUiPalette.Template, "winui.shell.command-palette");
        views.Add(ViewKeys.ShellHistoryMenu, WinUiFrameBits.HistoryMenu, "winui.shell.history-menu");
        views.Add(ViewKeys.ShellFileDrop, WinUiFrameBits.FileDrop, "winui.shell.file-drop");
        views.Add(ViewKeys.ShellDragChip, WinUiFrameBits.DragChip, "winui.shell.drag-chip");
        views.Add(ViewKeys.ShellCoverScrim, WinUiFrameBits.CoverScrim, "winui.shell.cover-scrim");
    }
}
