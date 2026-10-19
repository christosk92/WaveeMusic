// ── Themes/WinUi/Structure/Views/Discovery/Search/WinUiSearchPage.cs — WinUI's `Pages.Search` (themes plan 6.2; P6 W5) ────────────────────
//
// Boards W-Search (-Compact / -Small / -Huge), W-NoResults (-Compact). The page's frame: the title ("Results for “stereo love”", Title Large 40/52, Title 28/36
// below Standard, keyed `winui.page.title`), the facet row (WinUI's SelectorBar: 14/20 words padded 12 in 40-high items with the 3-px pill, one strip that scrolls
// with its trailing edge faded, a hairline under it from inset to inset), the keyed sliding facet body in its skeleton region, the three loading shapes and the
// empty / error vacancies. The body's contents are `WinUiSearchBody` (the top result, the Songs, the rails, the grids and lists, the genre tiles, the related
// queries).
//
// The tabs arrive WITH the first result, not a beat before it: the row stays skeletal (the eleven placeholder pills at their real widths) until the host says the
// body has answered once for this query. Both rows are keyed so the swap is a mount, not an in-place patch, and the real row rides the body's own 8-DIP rise.
//
// THE SLIDE: the facet body is keyed "facet-body:<facet>" and carries `MotionRecipes.PageSlideForward` / `PageSlideBack` only on a REAL chip change (the host's
// `SearchSlide`): mounting never slides. The body region is `smoothResize: false`. A grid facet (Albums, Playlists) owns its own virtual scroll; every other
// facet scrolls in the page's own ScrollView. THE THREE LOADING SHAPES: the facet body's shimmer, in the shape of the facet it stands in for; its cards and rows
// are the media surface's own seed face (`SurfaceParts.Seed`), so a hydrating page shifts nothing. The numbers are `Rules\WinUiSearchRules.cs`.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's Search results page.</summary>
internal static class WinUiSearchPage
{
    /// <summary>The registered template of <see cref="ViewKeys.SearchPage"/>.</summary>
    public static Element Template(SearchPageProps props, in ViewContext ctx)
    {
        // The tier's record: the page inset, the facet row's form, the hit rows, the columns. The first frame of every width-driven box is seeded from the
        // region estimate. Read before the early return: the hooks run on every render, in the same order.
        var facts = ctx.Tier();
        var layout = WinUiSearchTiers.Of(facts);
        var frame = WinUiTiers.FrameOf(facts);
        float firstW = WinUiRegion.EstimateWidth(facts, ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width);

        // The SelectorBar owns a selection cell; the host owns the selection (`Selected`), so the cell follows it each render.
        var selection = ctx.Hooks.UseRef(new FluentGpu.Signals.Signal<int>(props.Selected)).Value;
        if (selection.Peek() != props.Selected) selection.Value = props.Selected;

        if (props.Vacant) return Controls.Vacancy(Controls.VacancyVoice.Empty);

        Element chipRow = props.ChipsSkeletal
            ? FacetRowSkeleton(layout) with { Key = "facets-skel" }
            : FacetRow(props, layout, selection) with
            {
                Key = "facets",
                Enter = new EnterExit(Dy: WinUiSearchRules.FacetRowEnterDy, Opacity: 0f, Active: true),
                Transition = MotionTokenDef.Eased(Design.Motion.Standard, Easing.SmoothOut),
            };

        SearchFacet facet = props.Facet;
        var region = new SkelRegionEl(
            Pending: props.Pending, Failed: props.Failed, Content: () => WinUiSearchBody.Content(props, layout, firstW), ShimmerSource: () => Shimmer(facet, layout),
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.Retry),
            Reveal: facet == SearchFacet.Tracks ? SkelReveal.None : SkelReveal.StaggerRows,
            Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
        LayoutTransition? motion = props.Slide switch
        {
            SearchSlide.Forward => MotionRecipes.PageSlideForward,
            SearchSlide.Back => MotionRecipes.PageSlideBack,
            _ => null,
        };
        string facetId = ((int)facet).ToString(CultureInfo.InvariantCulture);

        Element body = Search.IsGridFacet(facet)
            ? new BoxEl
            {
                Key = "facet-body:" + facetId, Animate = motion,
                Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
                Padding = new Edges4(layout.Inset, 0f, layout.Inset, 0f),
                Children = [region],
            }
            : ScrollView(new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                Padding = new Edges4(layout.Inset, 0f, layout.Inset, R.Space.DockReserve + Spacing.XXL),
                Children =
                [
                    new BoxEl { Key = "facet-body:" + facetId, Animate = motion, Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = [region] },
                ],
            }) with
            {
                Grow = 1f, MinWidth = 0f, MinHeight = 0f, ScrollKey = props.ScrollKey,
            };

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Shrink = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Gap = WinUiSearchRules.TitleGap,
                    Padding = new Edges4(layout.Inset, frame.InsetTop, layout.Inset, WinUiSearchRules.StripGap),
                    Children = [Title(props.Echo, layout), chipRow, new BoxEl { Height = WinUiSearchRules.TabLineH, Shrink = 0f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false, Margin = new Edges4(0f, -Spacing.S, 0f, 0f) }],
                },
                body,
            ],
        };
    }

    // ══ 1. THE TITLE AND THE FACET ROW ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>The page title: "Results for “{query}”" in the display face (Title Large 40/52; Title 28/36 below Standard), one line, keyed for the inset lint.</summary>
    static Element Title(string lowered, WinUiSearchLayout layout)
        => (layout.TitleLarge ? R.Type.PageHero(Strings.Theme.Winui.Discovery.ResultsFor(lowered)) : R.Type.Pivot(Strings.Theme.Winui.Discovery.ResultsFor(lowered))) with
        {
            Key = WinUiFrameLayout.PageTitleKey, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        };

    /// <summary>The eleven placeholder pills at their real widths, in a strip of the row's own height, so the real strip replaces it without a height change.</summary>
    static Element FacetRowSkeleton(WinUiSearchLayout layout)
    {
        var widths = WinUiSearchRules.ChipSkeletonWidths;
        var tabs = new Element[widths.Length];
        for (int i = 0; i < widths.Length; i++)
            tabs[i] = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Height = layout.TabStripH,
                Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                Children = [new BoxEl { Width = widths[i], Height = WinUiSearchRules.ChipSkeletonBarH, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary }],
            };
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f, Height = layout.TabStripH, ClipToBounds = true, Children = tabs };
    }

    /// <summary>The real facet row over the host's tab list: server rank first, All always first. WinUI's SelectorBar (the style's `FacetCompact` voice with
    /// the pill), one strip that scrolls on a narrow card with its trailing edge faded. <c>Selectors</c> holds one cached click per index.</summary>
    static Element FacetRow(SearchPageProps props, WinUiSearchLayout layout, FluentGpu.Signals.Signal<int> selection)
    {
        var labels = new string[props.Tabs.Count];
        for (int i = 0; i < labels.Length; i++) labels[i] = Loc.Get(Search.FacetNameKey(props.Tabs[i].Facet));
        var selectors = props.Selectors;
        var bar = SelectorBar.Create(labels, selection, onChange: i => { if ((uint)i < (uint)selectors.Count) selectors[i](); },
                                     style: R.Type.FacetCompactStyle with { ShowPill = true });
        return ScrollView(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [bar] }, horizontal: true) with
        {
            Grow = 0f, Height = layout.TabStripH, AutoEdgeFade = true, SuppressScrollBar = true, ScrollKey = "search:tabs", MinWidth = 0f,
        };
    }

    // ══ 2. THE THREE LOADING SHAPES ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The facet body's shimmer, in the shape of the facet it stands in for — never one bar stack. Its cards and rows are the media surface's own seed
    /// face (<see cref="SeedFace"/>), so a hydrating page shifts nothing.</summary>
    internal static Element Shimmer(SearchFacet facet, WinUiSearchLayout layout) => Search.ShimmerFor(facet) switch
    {
        Search.ShimmerShape.Hero => ShimmerAll(layout),
        Search.ShimmerShape.CardGrid => ShimmerCardGrid(),
        _ => ShimmerRows(layout),
    };

    /// <summary>One seed card or row: <see cref="SurfaceParts.Seed"/> — the live surface's geometry in bones — handed to the region's deriver as its OWN
    /// override. A static tree also mounts no <c>SurfaceHost</c> during load (<c>Skel.Region</c>'s explicit-shimmer rule).</summary>
    static Element SeedFace(SurfaceShape shape, float width = float.NaN)
    {
        Element seed = SurfaceParts.Seed(in shape, width);
        return seed.Skel(seed);
    }

    /// <summary>Ten seed rows at the hit row's own geometry on the 8-DIP list gap.</summary>
    static Element ShimmerRows(WinUiSearchLayout layout)
    {
        var rows = new Element[WinUiSearchRules.ShimmerRows];
        for (int i = 0; i < rows.Length; i++) rows[i] = SeedFace(layout.RowShape(Shape.Row(SearchHitRules.RowArt)));
        return new BoxEl { Direction = 1, Gap = Spacing.S, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch, Children = rows };
    }

    /// <summary>Twelve seed shelf cards at the playlist rail's card width, wrapping.</summary>
    static Element ShimmerCardGrid()
    {
        var cards = new Element[WinUiSearchRules.ShimmerCards];
        for (int i = 0; i < cards.Length; i++) cards[i] = SeedFace(WinUiSearchRules.RailCard, WinUiSearchRules.ShimmerCardW);
        return new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.M, AlignSelf = FlexAlign.Stretch, Children = cards };
    }

    /// <summary>The All tab's shimmer: the top result's card and the rows beside it (stacked below 900), then the rail's cards.</summary>
    static Element ShimmerAll(WinUiSearchLayout layout)
    {
        var hero = new BoxEl
        {
            Height = WinUiSearchRules.HeroHeight, MinWidth = 0f, Grow = layout.Beside ? 0f : 1f, Width = layout.Beside ? layout.HeroW : float.NaN,
            Shrink = layout.Beside ? 0f : 1f, AlignSelf = FlexAlign.Stretch, Direction = 1, Gap = Spacing.S, Padding = Edges4.All(WinUiSearchRules.HeroPad),
            ClipToBounds = true, Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl { Width = layout.HeroCover, Height = layout.HeroCover, Shrink = 0f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleTertiary },
                new BoxEl { Width = WinUiSearchRules.ShimmerTitleW, Height = WinUiSearchRules.ShimmerTitleH, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
                new BoxEl { Width = WinUiSearchRules.ShimmerSubtitleW, Height = WinUiSearchRules.ShimmerSubtitleH, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleTertiary },
                new BoxEl { Width = WinUiSearchRules.ShimmerButtonW, Height = WinUiSearchRules.ShimmerButtonH, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
            ],
        };
        Element top = layout.Beside
            ? new BoxEl
            {
                Direction = 0, Gap = WinUiSearchTiers.BesideGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                Children = [hero, new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [ShimmerRows(layout)] }],
            }
            : hero;
        return new BoxEl { Direction = 1, Gap = WinUiSearchTiers.SectionGap, AlignSelf = FlexAlign.Stretch, Children = [top, ShimmerCardGrid()] };
    }
}
