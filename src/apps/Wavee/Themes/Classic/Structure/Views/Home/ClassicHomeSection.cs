// ── Themes/Classic/Structure/Views/Home/ClassicHomeSection.cs — Classic's `Pages.HomeSection` (themes plan 6.1, T10a1) ──────
//
// From `Home\SectionScreen.UI.cs`: the "See all" / charts drill page. The title is the masthead's (the overlay band paints the route's arg, else
// what the host publishes): this page owns no header of its own, only the reserve under the band and a thin "N items" meta row.
//
// Wave 2E (home-redesign-remediation.md F9/§3.6): the grid is `ItemsView.CreateBound` over `RepeatLayout.GridFit` — no measured width, no
// frozen template, no hand-rolled tier table. Each cell is the app's ONE media surface (#157): `Controls.BoundSurface(item, Shape.Grid, …)` over
// `HomeCards.GridCardData` — the plate, hover, play FAB, "…", menu, drag, circle and trim tooltip every other card has, with the slot root owning
// invoke and the roving tab stop. A blank seed is the surface's own seed face.
//
// THE GRID: a BOUND `ItemsView` over `RepeatLayout.GridFit` (F9) — the column count and cell width come from the engine's own measured cross size
// and never from an app-side width read, so there is no tier flip and no frozen-at-first-measure column count. The host's card source is the
// reactive item source (it starts at the seed cards and the host swaps it to the live row's cards once the section's WHOLE list has landed);
// the row template runs ONCE per recycled slot and reads the bound item reactively, so a later answer (a feed refresh re-landing the band) just
// re-skins the slots the source already realized — no remount, no re-render of this view. The grid never asks for anything: the row holds the
// whole section. No content-type pools: a circle is `CardData.Circular`, which the one surface re-renders for, so an artist and a playlist share
// every slot.
//
// THE REGION: Pending until the whole section has landed (the seed cards, blank, are the shimmer: `Content(seed)` IS the shimmer, so there is no
// separate shimmer source), the failure vacancy with the host's Retry, the empty vacancy for a section that landed empty.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's "See all" drill page.</summary>
internal static class ClassicHomeSection
{
    /// <summary>The row-height ESTIMATE (the seed before the first row measures; GridFit corrects to the real measured height itself, this
    /// never pins it). The estimate is the surface's own geometry, never a local sum: an estimate that drifts from the rendered card re-pins
    /// the scroll.</summary>
    static float CardRowEstimate => SurfaceGeometry.GridRowEstimate(ClassicHomeRules.SectionCardMinWidth, Shape.Grid, hasSubtitle: true);

    /// <summary>The registered template of <see cref="ViewKeys.HomeSection"/>.</summary>
    public static Element Template(HomeSectionProps props, in ViewContext ctx)
    {
        // The cell template runs ONCE per recycled slot and is frozen at the grid's mount (the adapter is the host's one stable delegate).
        var cell = ctx.Hooks.UseRef<Func<BoundItemScope<HomeCard>, Element>?>(null);
        Func<BoundItemScope<HomeCard>, Element> template = cell.Value ??= item => Controls.BoundSurface(in item, Shape.Grid, props.Adapt);

        return new BoxEl
        {
            Key = "home-section-body", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Gap = ClassicHomeRules.SectionBodyGap,
            Padding = BrowseMastheadMetrics.FamilyBodyPad(Spacing.XXL),
            Children =
            [
                props.Total > 0
                    ? R.Type.TrackMeta(Strings.Home.SectionItems(props.Total))
                    : new BoxEl(),
                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Children = [Body(props, template)] },
            ],
        };
    }

    // ── the shimmer/content/failed boundary (Skel — Pending until the whole section has landed) ────────────────────

    static Element Body(HomeSectionProps props, Func<BoundItemScope<HomeCard>, Element> cell) => new SkelRegionEl(
        Pending: props.Pending,
        Failed: props.Failed,
        Content: () => props.Empty() ? Controls.Vacancy(Controls.VacancyVoice.Empty) : Grid(props, cell),
        ShimmerSource: null,   // Content(seed) IS the shimmer: while Pending, the card source still holds the seed cards ⇒ blank cards
        OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.Retry),
        Reveal: SkelReveal.StaggerRows,
        Style: SkeletonStyle.Default,
        Group: props.Group,
        SmoothResize: false);

    static Element Grid(HomeSectionProps props, Func<BoundItemScope<HomeCard>, Element> cell) => ItemsView.CreateBound(props.Cards, cell,
        RepeatLayout.GridFit(ClassicHomeRules.SectionCardMinWidth, Spacing.Card, CardRowEstimate),
        new ListOptions<HomeCard>
        {
            Grow = 1f, SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None,
            IsItemInvokedEnabled = true, OnInvokedTyped = props.OnInvoked, IsItemEnabledTyped = props.IsEnabled,
            Scroll = new ScrollOptions { ScrollKey = props.ScrollKey },
        });
}
