// ── Theming/Contracts/ViewKeys.Module.cs — the module page's published view key (themes plan 6.0, T10b3) ──────────────────────
//
// T10b3 publishes the PAGE a playback module describes (`Pages.Module`): the masthead reserve, the watch stage slot, the scroller with its
// skeleton region (loading, ready, failed) and the two readings of the document (the entity reading and the watch reading). It is a Common block
// (`Themes\Common\CommonModulePage.cs`): written once, registered by every structure, so a theme gets the page without drawing it.
//
// What stays core (`Modules.PageHost`): the route parse, the attach state, the page's load (`UseResource`, the cached document as the seed), the
// stage-playable claim (the attached page is the one writer of `Shell.Ui.ActiveStagePlayable`), the watch projection (`WatchPageModel`), the route
// grammar, the shared item budget and every verb (`Modules.PlayOrToggle`, `OpenUrl`, `Navigate`, `InvokeFor`).

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The module page of one `module:` route. Mounted by `Modules.PageHost`. Props: <see cref="ModulePageProps"/>.</summary>
    public static readonly ViewKey<ModulePageProps> ModulePage = new("Pages.Module");
}
