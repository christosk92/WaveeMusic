// ── Themes/Zune/Structure/Views/Home/ZuneHomeSection.cs — Zune's `Pages.HomeSection` and `Pages.BrowseSection` (themes plan 6.3; P5 Z5) ───────
//
// No board draws the "see all" drill page (undrawn: built from Zune's parts, owner Q18). Its head is the band (the section's title rides the
// trail and the band's row 2 has no views here), so the page is the "N items" line in the tertiary voice over the bound card grid on the frame's
// gutter, 8 under the band: the shared grid surface (Zune's surface style draws it), cells at least 154 wide (the Home shelf's card), 16 apart.
// The host owns the demand, the table sync and the card source; the cell template is frozen at the grid's mount (the adapter is the host's one
// stable delegate). Pending until the whole section has landed (the seed cards, blank, ARE the shimmer), the error vacancy with the host's
// Retry, the empty vacancy for a section that landed empty. The grid is ONE key at every tier (the cell floor is a live layout prop).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's section drill page (Home's and Browse's).</summary>
internal static class ZuneHomeSection
{
    /// <summary>The page's root node (the tests find it).</summary>
    internal const string PageKey = "zune-section-page";

    static float CardRowEstimate() => SurfaceGeometry.GridRowEstimate(H.SectionMinCell, Shape.Grid, hasSubtitle: true);

    /// <summary>The registered template of <see cref="ViewKeys.HomeSection"/> and <see cref="ViewKeys.BrowseSection"/>.</summary>
    public static Element Template(HomeSectionProps props, in ViewContext ctx)
    {
        var cell = ctx.Hooks.UseRef<Func<BoundItemScope<HomeCard>, Element>?>(null);
        Func<BoundItemScope<HomeCard>, Element> template = cell.Value ??= item => Controls.BoundSurface(in item, Shape.Grid, props.Adapt);
        var lay = ZuneHomeTiers.Of(ctx.Tier());

        return new BoxEl
        {
            Key = PageKey, Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = H.HeadGap,
            Padding = new Edges4(lay.Gutter, H.PageTop, lay.Gutter, 0f),
            Children =
            [
                props.Total > 0 ? K.Caption(Strings.Home.SectionItems(props.Total)) : new BoxEl(),
                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Children = [Body(props, template)] },
            ],
        };
    }

    static Element Body(HomeSectionProps props, Func<BoundItemScope<HomeCard>, Element> cell) => new SkelRegionEl(
        Pending: props.Pending,
        Failed: props.Failed,
        Content: () => props.Empty() ? Controls.Vacancy(Controls.VacancyVoice.Empty) : Grid(props, cell),
        ShimmerSource: null,   // Content(seed) IS the shimmer: while Pending the card source holds the seed cards, blank
        OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.Retry),
        Reveal: SkelReveal.StaggerRows,
        Style: SkeletonStyle.Default,
        Group: props.Group,
        SmoothResize: false);

    static Element Grid(HomeSectionProps props, Func<BoundItemScope<HomeCard>, Element> cell) => ItemsView.CreateBound(props.Cards, cell,
        RepeatLayout.GridFit(H.SectionMinCell, H.SectionGap, CardRowEstimate()),
        new ListOptions<HomeCard>
        {
            Grow = 1f, SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None,
            IsItemInvokedEnabled = true, OnInvokedTyped = props.OnInvoked, IsItemEnabledTyped = props.IsEnabled,
            Scroll = new ScrollOptions { ScrollKey = props.ScrollKey },
        })
        with { Key = "zune-section-grid" };
}
