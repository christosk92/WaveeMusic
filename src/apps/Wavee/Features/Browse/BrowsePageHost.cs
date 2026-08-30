using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Features.Browse;

/// <summary>Route host for a browse category page: resolves the page uri out of the route, wires navigation/playback
/// from the shell, and provides the model the page reads.
///
/// Mirrors <c>ConcertRoutePage</c> — the route-parsing and service-wiring live here so <see cref="BrowsePage"/> stays a
/// pure view over its model.</summary>
sealed class BrowsePageHost : Component
{
    readonly Route _route;

    public BrowsePageHost(Route route) => _route = route;

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var svc = UseContext(Services.Slot);

        string pageUri = BrowseRoutes.UriOf(_route.Name);
        if (pageUri.Length == 0) return new BoxEl { Grow = 1f };

        var model = new BrowsePage.Model(
            PageUri: pageUri,
            OnOpenCategory: (uri, title) => go(BrowseRoutes.Page(uri), title),
            // A client feature is not a browse page: Live Events carries featureUri "spotify:concerts" and routes into
            // the Concerts hub Wavee already has. Any OTHER feature resolves to null — this client has no surface for
            // it, so the tile declines instead of opening a route no page renders (BrowseRoutes.FeatureRoute).
            OnOpenFeature: uri =>
            {
                if (BrowseRoutes.FeatureRoute(uri) is { } route) go(route, null);
                else WaveeLog.Instance.Warn("nav", "browse.feature.unsupported: " + uri);
            },
            Go: go,
            Play: uri => { if (svc is not null) _ = svc.Player.PlayAsync(uri, 0); },
            OnExploreAll: () => go(BrowseRoutes.Home, null),
            RouteName: _route.Name,
            RouteArg: _route.Arg);

        // Scroll ownership moved INTO BrowsePage (T8): a flattened body's Virtual.Custom grid must own the scroll
        // container itself (the cover-shear trap — a virtual viewport measures 0 natural height inside a page-level
        // ScrollView), so BrowsePage now decides ScrollView-vs-not per BrowsePageLayout.Of's mode. This host only
        // resolves the route and provides the model; the frame padding (BrowseLayout.Frame — the ONE masthead frame
        // the directory and a category page share) now lives on whichever body BrowsePage renders.
        return Ctx.Provide(BrowsePage.Props, model,
            new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [Embed.Comp(() => new BrowsePage()) with { Key = "browse-page:" + pageUri }],
            });
    }
}
