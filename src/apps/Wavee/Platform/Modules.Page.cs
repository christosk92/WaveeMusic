// ── Platform/Modules.Page.cs ───────────────────────────────────────────────────────────────────────────────────────
// the module page's core host
//
// Role: Host
// Owner: T
// Wave: 6
// Spec: ch 09 §9.5 (the module page), ch 24 (the watch page's stage), gap register G-021 · G-151 · G-212; themes plan T10b3
//
// THE PAGE'S DECISIONS. A module is out of process, so it never renders anything: it answers `module/page` with a small declarative `ModulePageDoc`
// and `Themes\Common\CommonModulePage.cs` (the registered `Pages.Module` view) draws it. This host keeps what is not a picture: the route parse, the
// attach state, the load (the cached document is the seed, so a revisit shows the real page at once), the claim and the page's identity.
//
// THE CLAIM. Only the page holds the document, and only the document names the playable behind an entity (a module's entity ids and playable ids are
// different namespaces), so the ATTACHED page is the one writer of `Shell.Ui.ActiveStagePlayable` — from a signal effect gated on `UseIsActive`,
// reading every input inside the closure.

using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Sdk;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Modules
{
    /// <summary>The page a <c>module:</c> route renders. Keyed by the page uri, so a different entity is a different
    /// keep-alive slot and a different mount — the frozen ctor values are then exactly right.</summary>
    public static Element PageFor(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(in route);
        string? arg = Shell.ArgOf(in route);
        return Embed.Comp(() => new PageHost(routeKey, arg)) with { Key = "module-page:" + routeKey };
    }

    [global::Wavee.Theming.Contracts.CoreHost]
    sealed class PageHost(string routeKey, string? routeArg) : Component
    {
        public override Element Render()
        {
            // Hooks first, unconditionally — the route parse only decides what is DRAWN.
            string pageUri = Pages.UriOf(routeKey) ?? "";
            bool parsed = Pages.TryParseRoute(routeKey, out string moduleId, out _);
            var attached = UseIsActive();
            var page = UseResource(ct => LoadPage(pageUri, ct), Pages.Get(pageUri) ?? Seed);
            Loadable<ModulePageDoc> loadable = page.Loadable;
            string scrollScope = UseContext(Shell.PageScrollScope);

            UseSignalEffect(() =>
            {
                if (!attached.Value) return;   // subscribe: re-asserts the claim when this page is un-parked
                string staged = parsed && loadable.State.Value == (byte)LoadState.Ready
                    ? Pages.StagePlayableUri(moduleId, loadable.Value.Value) : "";
                if (!string.Equals(Shell.Ui.ActiveStagePlayable.Peek(), staged, StringComparison.Ordinal))
                    Shell.Ui.ActiveStagePlayable.Value = staged;
            });

            Resource<ModulePageDoc> resource = page;
            return Slots.View(ViewKeys.ModulePage, new ModulePageProps
            {
                PageUri = pageUri,
                ModuleId = moduleId,
                Parsed = parsed,
                RouteArg = routeArg,
                ScrollKey = scrollScope + "module-page:" + pageUri,
                Loadable = loadable,
                Retry = resource.Refresh,
            });
        }
    }
}
