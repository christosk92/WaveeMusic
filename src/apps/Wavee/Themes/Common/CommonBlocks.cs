// ── Themes/Common/CommonBlocks.cs — the Common blocks' registration (themes plan 6.5, T11c) ──────────────────────────────────────
//
// `Themes\Common\` holds the views every theme shares: utility pages written once from today's code and converted to roles (WVTH003 applies to
// Common like a structure). A structure registers them with ONE call from its own `Register` (WVTH001: a structure may reference its own theme
// and Common), so there is one copy of each view and a theme that wants its own simply registers its own template under the same key instead.
//
//   Pages.History          the navigation log                              CommonHistory
//   Shell.Fps              the frame-rate overlay (developer mode)         CommonFps
//   Pages.Diagnostics.*    the playback, connect and capture pages         CommonDiagnostics
//   Pages.Logs             the log files page                              CommonLogs
//   Pages.Module           a module's page                                 CommonModulePage
//   Hint.Line              a hint on its surface (the neutral fallback)    CommonHint
//   Hint.Explainer         a dial's explanation badge (stand-in, phase 3)  CommonHint
//   Settings.Dials         a feature panel's dials (stand-in, phase 3)     CommonHint
//   Shell.RateNudge        the rate & star card over the page              RateNudgeCard
//   LocalFiles.*           Local Files' empty state, first-scan line,      CommonLocalFiles (#240 UX S4-S6; Classic uses the WinUI layout,
//                          "what changed" pill and missing-songs review     a structure may pass its own empty state)
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
    /// <summary>Adds every Common view to <paramref name="views"/>. Called by a structure's <c>Register</c>. A structure that draws the module page in its own page
    /// column passes its template (and a template id) as <paramref name="module"/>: the view registry refuses a second template for a key, so the choice is made here.
    /// So does a structure that draws History (<paramref name="history"/>), the explanation badge (<paramref name="explainer"/>, settings rework
    /// phase 4) or the hints (<paramref name="hint"/>, phase 6) in its own voice. A hint template hands a surface it does not draw to the neutral
    /// <c>CommonHint.Hint</c>, which is registered itself only for a structure that draws no hint at all.</summary>
    public static void Register(ViewRegistryBuilder views, ViewTemplate<ModulePageProps>? module = null, string moduleTemplateId = "common.module.page",
        ViewTemplate<HistoryPageProps>? history = null, string historyTemplateId = "common.history",
        ViewTemplate<ExplainerProps>? explainer = null, string explainerTemplateId = "common.hint.explainer",
        ViewTemplate<HintProps>? hint = null, string hintTemplateId = "common.hint",
        ViewTemplate<LocalFilesEmptyProps>? localFilesEmpty = null, string localFilesEmptyTemplateId = "common.local-files.empty")
    {
        views.Add(ViewKeys.HistoryPage, history ?? CommonHistory.Template, historyTemplateId);
        views.Add(ViewKeys.ShellFps, CommonFps.Template, "common.fps");
        CommonDiagnostics.Register(views);
        CommonLogs.Register(views);
        views.Add(ViewKeys.ModulePage, module ?? CommonModulePage.Template, moduleTemplateId);
        // The settings rework's three keys (contract 1.115): plain stand-ins until each theme draws its own (phases 4-6).
        views.Add(ViewKeys.Hint, hint ?? CommonHint.Hint, hintTemplateId);
        views.Add(ViewKeys.Explainer, explainer ?? CommonHint.Explainer, explainerTemplateId);
        views.Add(ViewKeys.SettingsDials, CommonHint.SettingsDials, "common.settings.dials");
        // The rate & star card (rate-nudge-implementation.md §5.3): one picture for Classic, WinUI and Zune, each of which reaches it through
        // its own call to this method (so no structure file names the key).
        views.Add(ViewKeys.ShellRateNudge, RateNudgeCard.Template, "common.shell.rate-nudge");
        // Local Files' own views (#240): one picture for Classic and WinUI (Classic uses the WinUI layout); Zune passes its own empty state and shares
        // the rest, which reads the style's roles and case.
        views.Add(ViewKeys.LocalFilesEmpty, localFilesEmpty ?? CommonLocalFiles.Empty, localFilesEmptyTemplateId);
        views.Add(ViewKeys.LocalFilesScan, CommonLocalFiles.Scan, "common.local-files.scan");
        views.Add(ViewKeys.LocalFilesPill, CommonLocalFiles.Pill, "common.local-files.pill");
        views.Add(ViewKeys.LocalFilesReview, CommonLocalFiles.Review, "common.local-files.review");
        // "Your files and Spotify" (#240 phase 1): the opt-in dialog's body, one picture in every theme's dialog. The offer and the review are each
        // structure's own (WinUI and Classic: CommonLocalLink; Zune: ZuneLocalLink).
        views.Add(ViewKeys.LocalLinkOptIn, CommonLocalLink.OptIn, "common.local-link.opt-in");
        views.Add(ViewKeys.LocalRename, CommonLocalLink.Rename, "common.local-link.rename");   // phase 2: the rename dialog's body, every theme
    }
}
