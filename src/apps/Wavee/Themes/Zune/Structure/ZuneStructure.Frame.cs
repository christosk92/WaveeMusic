// ── Themes/Zune/Structure/ZuneStructure.Frame.cs — the frame family's registrar (themes plan 6.3; P5 Z2a) ───────────────────────────────
//
// Z2a's keys: the window frame, the title bar, the search and its popup body, the command palette, the history menu, the file-drop cue, the
// drag chip layer, the cover scrim, the content states, the page head and the panel's frame. Each replaces the stand-in's (Classic's) key by
// key (`ViewRegistryBuilder.AddMissing` copies only what Zune does not draw). `Shell.NavPane` (the band) is Z2b's; the frame leaves it a slot.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterFrame(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.ShellFrame, ZuneFrame.Template, "zune.shell.frame");
        views.Add(ViewKeys.ShellContentState, ZuneContentState.Template, "zune.shell.content-state");
        views.Add(ViewKeys.PageHead, ZunePageHead.Template, "zune.page.head");
        views.Add(ViewKeys.ShellTitleBar, ZuneTitleBar.Template, "zune.shell.title-bar");
        views.Add(ViewKeys.ShellSearch, ZuneSearch.Template, "zune.shell.search");
        views.Add(ViewKeys.ShellSearchSuggestions, ZuneSearch.SuggestionsTemplate, "zune.shell.search-suggestions");
        views.Add(ViewKeys.ShellCommandPalette, ZunePalette.Template, "zune.shell.command-palette");
        views.Add(ViewKeys.ShellHistoryMenu, ZuneFrameBits.HistoryMenu, "zune.shell.history-menu");
        views.Add(ViewKeys.ShellFileDrop, ZuneFrameBits.FileDrop, "zune.shell.file-drop");
        views.Add(ViewKeys.ShellDragChip, ZuneFrameBits.DragChip, "zune.shell.drag-chip");
        views.Add(ViewKeys.ShellCoverScrim, ZuneFrameBits.CoverScrim, "zune.shell.cover-scrim");
        views.Add(ViewKeys.ShellPanel, ZunePanel.Template, "zune.shell.panel");
    }
}
