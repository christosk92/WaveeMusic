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
        // Flipped by the directory's ClipBelow on its engage/release edge — true only once the directory has scrolled
        // up to the masthead, so the feather never softens the top of a directory at rest.
        var underBand = UseSignal(false);
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

        // The offset model (ContextBand / BrowseMastheadMetrics.ClipInset): the shell masthead over this page paints
        // nothing, so the directory clips itself at the band's lower edge as it scrolls under it. The reserve is a
        // spacer ABOVE the clipped node rather than padding inside it, so the node's top starts where the content
        // does and the cut engages exactly when the content reaches the band.
        Element directory = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = Spacing.L,
            Padding = BrowseMastheadMetrics.FamilyUnderBandPad(PlayerDock.Reserve + Spacing.XXL),
            EdgeFade = underBand.Value
                ? new EdgeFadeSpec(EdgeMask.Top, BrowseMastheadMetrics.ClipFadeBand)
                : null,
            Children = [Embed.Comp(() => new BrowseDirectory())],
        }.ClipBelow(BrowseMastheadMetrics.ClipInset, v => underBand.Value = v);

        return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)pageScroll,
            Ctx.Provide(BrowseDirectory.Props, browseModel,
                ScrollView(new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Height = BrowseMastheadMetrics.BodyTop, HitTestVisible = false },
                        directory,
                    ],
                }) with
                {
                    Grow = 1f, MinWidth = 0f, ScrollKey = "browse",
                    OnScrollGeometryChanged = scrollPub,
                }));
    }
}
