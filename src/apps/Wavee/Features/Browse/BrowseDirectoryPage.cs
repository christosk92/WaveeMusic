using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Features.Browse;

/// <summary>The Browse directory as its own keep-alive page. Extracted from SearchPage's empty-query branch so the
/// landing owns a slot (<c>ScrollKey = "browse"</c>) and returning from a category reactivates the live node instead
/// of remounting through ScrollMemory. SearchPage is results-only.</summary>
sealed class BrowseDirectoryPage : Component
{
    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var pageScroll = UseSignal(0f);
        (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Publish) scrollPub =
            (g => (long)(g.OffsetY / 24f), g => pageScroll.Value = g.OffsetY);

        var browseModel = new BrowseDirectory.Model(
            OnOpenCategory: (uri, title) => go(BrowseRoutes.Page(uri), title),
            // A client feature this build has no surface for resolves to null — the tile declines rather than
            // navigating to a key no page renders. See BrowseRoutes.FeatureRoute.
            OnOpenFeature: uri =>
            {
                if (BrowseRoutes.FeatureRoute(uri) is { } route) go(route, null);
                else WaveeLog.Instance.Warn("nav", "browse.feature.unsupported: " + uri);
            });

        return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)pageScroll,
            Ctx.Provide(BrowseDirectory.Props, browseModel,
                ScrollView(new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Gap = Spacing.L,
                    Padding = BrowseMastheadMetrics.FamilyBodyPad(PlayerDock.Reserve + Spacing.XXL),
                    Children = [Embed.Comp(() => new BrowseDirectory())],
                }) with
                {
                    Grow = 1f, MinWidth = 0f, ScrollKey = "browse",
                    OnScrollGeometryChanged = scrollPub,
                }));
    }
}
