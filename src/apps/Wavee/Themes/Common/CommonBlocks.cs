// ── Themes/Common/CommonBlocks.cs — the Common blocks' registration (themes plan 6.5, T11c) ──────────────────────────────────────
//
// `Themes\Common\` holds the views every theme shares: utility pages written once from today's code and converted to roles (WVTH003 applies to
// Common like a structure). A structure registers them with ONE call from its own `Register` (WVTH001: a structure may reference its own theme
// and Common), so there is one copy of each view and a theme that wants its own simply registers its own template under the same key instead.
//
//   Pages.History          the navigation log                              CommonHistory
//   Pages.HomeCustomize    the Home customizer                             CommonCustomize
//   Shell.Fps              the frame-rate overlay (developer mode)         CommonFps
//   Pages.Diagnostics.*    the playback, connect and capture pages         CommonDiagnostics
//   Pages.Logs             the log files page                              CommonLogs
//   Pages.Module           a module's page                                 CommonModulePage
//
// TIERS (themes plan 3.2, TC10): every block is tiered once for every theme, from the content tier and the height tier alone (`Rules\Tiers.cs`, `CommonTouch.cs`,
// `CommonTierComponent.cs`); no theme has a board for them (owner Q18), so a block never names an edge of the ladder its theme draws.
//
// The not-found page is a block too (`CommonNotFound`), but the content state draws it: `Shell.ContentState` is a structure's own key (the page box
// and the offline lane are the theme's), and its NotFound arm calls the one block.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

/// <summary>Registers every Common block.</summary>
public static class CommonBlocks
{
    /// <summary>Adds every Common view to <paramref name="views"/>. Called by a structure's <c>Register</c>.</summary>
    public static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.HistoryPage, CommonHistory.Template, "common.history");
        views.Add(ViewKeys.HomeCustomize, CommonCustomize.Template, "common.customize");
        views.Add(ViewKeys.ShellFps, CommonFps.Template, "common.fps");
        CommonDiagnostics.Register(views);
        CommonLogs.Register(views);
        views.Add(ViewKeys.ModulePage, CommonModulePage.Template, "common.module.page");
    }
}
