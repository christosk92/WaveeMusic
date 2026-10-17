// ── Themes/Classic/Structure/Views/Pages/ClassicSearchPage.cs — Classic's `Pages.Search` (themes plan 6.1, T10a2) ─────────────────────────────
//
// From `Entities\Search.Page.cs` (`PageHost.Render`) and `Entities\Search.UI.cs` §1-§2 and §10: the Search results page's frame — the query echo, the facet
// row and its eleven-pill skeleton, the keyed sliding facet body in its skeleton region, the three loading shapes and the empty / error vacancies. The body's
// contents are `ClassicSearchBody` (the hit rows, the top result, the shelves, the grids and lists, the genre links, the related queries).
//
// THE FACET ROW (W3, §0.3-§0.5): label + count over a 3-DIP underline that GROWS from its left edge when selected (§0.4). Tabs are focus stops (§9 gap 3).
// The tabs arrive WITH the first result, not a beat before it: the row stays skeletal (the eleven placeholder pills at their real widths, each with a
// transparent underline spacer so the row's per-tab height matches a real tab: W2, §9 must-not-simplify 3) until the host says the body has answered once for
// this query. Both rows are keyed so the swap is a mount, not an in-place patch — an unkeyed swap never fires Enter — and the real row rides the body's own 8-DIP rise.
//
// THE SLIDE (§0.5): the facet body is keyed "facet-body:<facet>" and carries `MotionRecipes.PageSlideForward` / `PageSlideBack` only on a REAL chip change (the
// host's `SearchSlide`): mounting never slides. The body region is `smoothResize: false` — a shimmer and a complete result set differ by thousands of DIP (§9
// must-not-simplify 2). A grid facet (Albums, Playlists) owns its own virtual scroll; every other facet scrolls in the page's own ScrollView.
//
// THE THREE LOADING SHAPES (§0.6, W2): the facet body's shimmer, in the shape of the facet it stands in for — never one bar stack. Its cards and rows are the media
// surface's own seed face (`SurfaceParts.Seed`), so a hydrating page shifts nothing. The numbers are `Rules\ClassicSearchRules.cs`.

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

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Search results page.</summary>
internal static class ClassicSearchPage
{
    /// <summary>The registered template of <see cref="ViewKeys.SearchPage"/>.</summary>
    public static Element Template(SearchPageProps props, in ViewContext ctx)
    {
        // The tier's record (plan 3.6, TC4): the page inset, the facet row's form, the hit rows, the columns. The first frame of every width-driven box is seeded
        // from the region estimate — no fixed 900 / 1100. Read before the early return: the hooks run on every render, in the same order.
        var facts = ctx.Tier();
        var layout = ClassicSearchTiers.Of(facts);
        float firstW = ClassicRegion.EstimateWidth(facts, ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width);

        if (props.Vacant) return Controls.Vacancy(Controls.VacancyVoice.Empty);

        // The tabs arrive WITH the first result, not a beat before it: the chip row stays skeletal until the body has
        // answered once for this query. Both rows are keyed so the swap is a mount, not an in-place patch — an unkeyed swap
        // never fires Enter — and the real row rides the body's own 8-DIP rise.
        Element chipRow = props.ChipsSkeletal
            ? FacetRowSkeleton(layout) with { Key = "facets-skel" }
            : FacetRow(props, layout) with
            {
                Key = "facets",
                Enter = new EnterExit(Dy: ClassicSearchRules.FacetRowEnterDy, Opacity: 0f, Active: true),
                Transition = MotionTokenDef.Eased(Design.Motion.Standard, Easing.SmoothOut),
            };

        SearchFacet facet = props.Facet;
        var region = new SkelRegionEl(
            Pending: props.Pending, Failed: props.Failed, Content: () => ClassicSearchBody.Content(props, layout, firstW), ShimmerSource: () => Shimmer(facet, layout),
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
                Padding = new Edges4(layout.BodyInset, Spacing.S, layout.BodyInset, 0f),
                Children = [region],
            }
            : ScrollView(new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                Padding = new Edges4(layout.BodyInset, Spacing.S, layout.BodyInset, R.Space.DockReserve + Spacing.XXL),
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
                    Direction = 1, Shrink = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Gap = Spacing.S,
                    Padding = new Edges4(layout.Gutter, Spacing.M, layout.Gutter, Spacing.S),
                    // Compact's tab strip runs edge to edge (C-Search-Compact): it takes back the gutter, less the 4 its first tab keeps.
                    Children = [QueryEcho(props.Echo), layout.Flush ? new BoxEl { Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Margin = new Edges4(4f - layout.Gutter, 0f, 4f - layout.Gutter, 0f), Children = [chipRow] } : chipRow],
                },
                body,
            ],
        };
    }

    // ══ 1. THE FACET ROW (W3, §0.3-§0.5) ═════════════════════════════════════════════════════════════════════════════

    /// <summary>The query echo: the committed query lowercased in the display face, one line, Shrink never Grow (§0.2).</summary>
    static Element QueryEcho(string lowered)
        => R.Type.SurfaceDisplay(lowered) with
        {
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        };

    /// <summary>The eleven placeholder pills at their real widths, each with a transparent underline spacer so the
    /// row's per-tab height matches a real tab (W2, §9 must-not-simplify 3).</summary>
    static Element FacetRowSkeleton(ClassicSearchLayout layout)
    {
        var widths = ClassicSearchRules.ChipSkeletonWidths;
        var tabs = new Element[widths.Length];
        for (int i = 0; i < widths.Length; i++)
            tabs[i] = new BoxEl
            {
                Direction = 1, Shrink = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center,
                        Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.XS),
                        Children = [new BoxEl { Width = widths[i], Height = ClassicSearchRules.ChipSkeletonBarH, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary }],
                    },
                    new BoxEl { Height = ClassicSearchRules.FacetUnderlineH, Shrink = 0f },
                ],
            };
        // A strip tier keeps ONE row of the strip's height (clipped), so the real strip replaces it without a height change.
        return layout.TabsWrap
            ? new BoxEl { Direction = 0, Wrap = true, AlignItems = FlexAlign.End, MinWidth = 0f, Grow = 1f, Children = tabs }
            : new BoxEl { Direction = 0, AlignItems = FlexAlign.End, MinWidth = 0f, Grow = 1f, Height = layout.TabStripH, ClipToBounds = true, Children = tabs };
    }

    /// <summary>The real facet row over the host's tab list: server rank first, All always first, counts omitted for
    /// All and for 0 (W3; the host computed the counts). <c>Selectors</c> holds one cached click per index.</summary>
    static Element FacetRow(SearchPageProps props, ClassicSearchLayout layout)
    {
        var tabs = new Element[props.Tabs.Count];
        for (int i = 0; i < tabs.Length; i++)
        {
            var tab = props.Tabs[i];
            tabs[i] = FacetTab(Loc.Get(Search.FacetNameKey(tab.Facet)), tab.Count, i == props.Selected, props.Selectors[i], layout.TabH);
        }
        if (layout.TabsWrap)
            return new BoxEl { Direction = 0, Wrap = true, AlignItems = FlexAlign.End, MinWidth = 0f, Grow = 1f, Children = tabs };
        // Compact and Small: ONE strip that scrolls, its trailing edge faded (the tabs never wrap onto a second row on a narrow card).
        return ScrollView(new BoxEl { Direction = 0, AlignItems = FlexAlign.End, MinWidth = 0f, Children = tabs }, horizontal: true) with
        {
            Grow = 0f, Height = layout.TabStripH, AutoEdgeFade = true, SuppressScrollBar = true, ScrollKey = "search:tabs", MinWidth = 0f,
        };
    }

    /// <summary>One tab: label + count over a 3-DIP underline that GROWS from its left edge when selected (§0.4). Tabs
    /// are focus stops (§9 gap 3).</summary>
    static Element FacetTab(string name, int total, bool selected, Action onClick, float tabH)
    {
        Element label = Body(name) with
        {
            Color = selected ? Tok.TextPrimary : Tok.TextSecondary, HoverColor = Tok.TextSecondary,
            Wrap = TextWrap.NoWrap, Shrink = 0f, MaxLines = 1,
        };
        Element[] labelKids = total > 0
            ? [label, Caption(total.ToString(CultureInfo.InvariantCulture)) with { Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap, Shrink = 0f, MaxLines = 1 }]
            : [label];
        return new BoxEl
        {
            Direction = 1, Shrink = 0f, MinHeight = tabH,
            Role = AutomationRole.Tab, Focusable = true, FocusVisualMargin = Design.FocusInsetRow,
            Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                    // A tier with its own tab height (Compact's 44 target) centres the label in what the underline leaves.
                    Grow = tabH > 0f ? 1f : 0f,
                    Padding = tabH > 0f ? new Edges4(Spacing.M, 0f, Spacing.M, 0f) : new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.XS),
                    Children = labelKids,
                },
                selected
                    ? new BoxEl
                    {
                        Key = "underline",
                        Height = ClassicSearchRules.FacetUnderlineH, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
                        Corners = Radii.FullAll, Fill = Tok.AccentDefault,
                        TransformOriginX = 0f,
                        Enter = new EnterExit(Sx: 0f, Active: true),
                        Transition = MotionTokenDef.Eased(ClassicSearchRules.FacetUnderlineMs, Easing.SmoothOut),
                    }
                    : new BoxEl { Height = ClassicSearchRules.FacetUnderlineH, Shrink = 0f },
            ],
        };
    }

    // ══ 2. THE THREE LOADING SHAPES (§0.6, W2) ═══════════════════════════════════════════════════════════════════════

    /// <summary>The facet body's shimmer, in the shape of the facet it stands in for — never one bar stack. Its cards and
    /// rows are the media surface's own seed face (<see cref="SeedFace"/>), so a hydrating page shifts nothing.</summary>
    internal static Element Shimmer(SearchFacet facet, ClassicSearchLayout layout) => Search.ShimmerFor(facet) switch
    {
        Search.ShimmerShape.Hero => ShimmerAll(layout),
        Search.ShimmerShape.CardGrid => ShimmerCardGrid(),
        _ => ShimmerRows(layout),
    };

    /// <summary>One seed card or row: <see cref="SurfaceParts.Seed"/> — the live surface's geometry in bones — handed to the
    /// region's deriver as its OWN override. A seed is already bones, so deriving it again would only lose the title bar's
    /// 150 cap (a row's bar would run the whole text column); a static tree also mounts no <c>SurfaceHost</c> during load
    /// (<c>Skel.Region</c>'s explicit-shimmer rule).</summary>
    static Element SeedFace(SurfaceShape shape, float width = float.NaN)
    {
        Element seed = SurfaceParts.Seed(in shape, width);
        return seed.Skel(seed);
    }

    /// <summary>Ten seed rows at the hit row's own geometry — 48 art in the 64 floor — on the 8-DIP list gap.</summary>
    static Element ShimmerRows(ClassicSearchLayout layout)
    {
        var rows = new Element[ClassicSearchRules.ShimmerRows];
        for (int i = 0; i < rows.Length; i++) rows[i] = SeedFace(layout.RowShape(Shape.Row(SearchHitRules.RowArt)));
        return new BoxEl { Direction = 1, Gap = Spacing.S, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch, Children = rows };
    }

    /// <summary>Twelve seed shelf cards at the playlist rail's card width, wrapping.</summary>
    static Element ShimmerCardGrid()
    {
        var cards = new Element[ClassicSearchRules.ShimmerCards];
        for (int i = 0; i < cards.Length; i++) cards[i] = SeedFace(ClassicSearchRules.RailCard, ClassicSearchRules.ShimmerCardW);
        return new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.M, AlignSelf = FlexAlign.Stretch, Children = cards };
    }

    static Element ShimmerAll(ClassicSearchLayout layout)
    {
        bool beside = !float.IsNaN(layout.HeroBesideW);
        bool avatar = layout.HeroForm == ClassicSearchHeroForm.Avatar;
        var hero = new BoxEl
        {
            Height = layout.HeroH, MinWidth = 0f, Grow = beside ? 0f : 1f, Width = beside ? layout.HeroBesideW : float.NaN, Shrink = beside ? 0f : 1f,
            AlignSelf = FlexAlign.Stretch, Direction = 0,
            ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = avatar ? ShimmerAvatarFace(layout) :
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, MinWidth = 0f, Gap = Spacing.S, Justify = FlexJustify.End,
                    Padding = new Edges4(Spacing.L, Spacing.L, Spacing.XL, Spacing.L),
                    Children =
                    [
                        new BoxEl { Width = ClassicSearchRules.ShimmerEyebrowW, Height = ClassicSearchRules.ShimmerEyebrowH, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
                        new BoxEl { Width = ClassicSearchRules.ShimmerTitleW, Height = ClassicSearchRules.ShimmerTitleH, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
                        new BoxEl { Width = ClassicSearchRules.ShimmerSubtitleW, Height = ClassicSearchRules.ShimmerSubtitleH, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleTertiary },
                        new BoxEl { Width = ClassicSearchRules.ShimmerButtonW, Height = ClassicSearchRules.ShimmerButtonH, Corners = Radii.FullAll, Fill = Tok.FillSubtleSecondary },
                    ],
                },
                new BoxEl { Width = ClassicSearchRules.HeroCoverW, Height = ClassicSearchRules.HeroCoverH, Shrink = 0f, AlignSelf = FlexAlign.Center, Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillSubtleTertiary },
            ],
        };
        // The avatar form's card stands under its own header (C-Search-Huge): the shimmer reserves the header's line and step too.
        Element heroColumn = avatar
            ? new BoxEl
            {
                Direction = 1, Gap = ClassicSearchTiers.AvatarHeaderGap, MinWidth = 0f, Shrink = beside ? 0f : 1f, Width = beside ? layout.HeroBesideW : float.NaN,
                AlignSelf = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl { Width = ClassicSearchRules.ShimmerEyebrowW, Height = ClassicSearchTiers.AvatarHeaderH, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
                    hero,
                ],
            }
            : hero;
        // Where the top result sits beside Best matches (Huge) the shimmer keeps that band: the hero and a column of seed rows.
        Element top = beside
            ? new BoxEl
            {
                Direction = 0, Gap = ClassicSearchTiers.BesideGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                Children = [heroColumn, new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [ShimmerRows(layout)] }],
            }
            : heroColumn;
        return new BoxEl { Direction = 1, Gap = Spacing.L, AlignSelf = FlexAlign.Stretch, Children = [top, ShimmerCardGrid()] };
    }

    /// <summary>The avatar form's bones (Huge): the round art at the top left, the title and the kind line under it, the buttons at the bottom right.</summary>
    static Element[] ShimmerAvatarFace(ClassicSearchLayout layout)
    {
        float pad = ClassicSearchTiers.AvatarPad, art = ClassicSearchTiers.AvatarArt;
        return
        [
            new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, Gap = ClassicSearchTiers.AvatarLineGap, Justify = FlexJustify.Start, AlignItems = FlexAlign.Start,
                Padding = new Edges4(pad, pad, pad, pad),
                Children =
                [
                    new BoxEl { Width = art, Height = art, Corners = Radii.FullAll, Fill = Tok.FillSubtleTertiary, Margin = new Edges4(0f, 0f, 0f, ClassicSearchTiers.AvatarTitleGap) },
                    new BoxEl { Width = ClassicSearchRules.ShimmerTitleW, Height = ClassicSearchRules.ShimmerTitleH, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
                    new BoxEl { Width = ClassicSearchRules.ShimmerSubtitleW, Height = ClassicSearchRules.ShimmerSubtitleH, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleTertiary },
                ],
            },
            new BoxEl
            {
                Direction = 1, Shrink = 0f, Justify = FlexJustify.End, Padding = new Edges4(0f, pad, pad, pad),
                Children = [new BoxEl { Width = ClassicSearchRules.ShimmerButtonW, Height = ClassicSearchRules.ShimmerButtonH, Corners = Radii.FullAll, Fill = Tok.FillSubtleSecondary }],
            },
        ];
    }
}
