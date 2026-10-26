// ── Themes/WinUi/Structure/Views/Discovery/Browse/WinUiBrowseDirectory.cs — WinUI's `Pages.Browse` and `Browse.Charts` (themes plan 6.2; P6 W5) ──
//
// Boards W-Browse (Standard), W-Browse-Compact / -Small / -Huge. The title ("Browse", Title Large) is the masthead's (the overlay band, W2a); this page draws
// the line under it ("Genres, moods and charts — everything Spotify is featuring right now.") and the bands. The masthead band paints NOTHING, so the
// directory cuts ITSELF at the band's lower edge: a spacer ABOVE a node clipped with `StickyClip(ClipInset)`, whose 24-DIP top feather arms only on the
// clip's engage edge. `SkelReveal.None`: the bands own the entrance.
//
//   Top (colour tiles) · For you (chips)   a row at Huge (2 : 1, "Explore all categories" the last chip), stacked below
//   Charts                                 the core's Fold deck: stacked at Compact and Small, paged (3 up, 5 at Huge) from Standard
//   Genres · Mood & activity · the long tail (Podcasts & audiobooks)   colour tiles on the tier's columns (2 / 3 / 6 / 10 and more as the region allows)
//
// THE DIRECTORY BODY IS EAGER: it mounts exactly once per page mount with a taxonomy-fixed band count, so each band cascades in 40 ms behind the last. The
// same `DirectoryBody` renders the loaded page and (over `BrowseDirectorySeeds`, `.Skeletonized(true)`) the loading one. The Charts band is its own core host
// (`BrowseChartsBand`, mounting `Browse.Charts`): in the live body the band slot mounts that host, in the skeleton it is the Fold deck over its seed.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's Browse directory.</summary>
internal static class WinUiBrowseDirectory
{
    /// <summary>WinUI's band order: Top, For you, Charts, Genres, Mood &amp; activity, the long tail (W-Browse, W-Browse-Huge).</summary>
    static readonly BrowseGroup[] s_order =
        [BrowseGroup.Top, BrowseGroup.ForYou, BrowseGroup.Charts, BrowseGroup.Genres, BrowseGroup.MoodActivity, BrowseGroup.More];

    /// <summary>The registered template of <see cref="ViewKeys.BrowseDirectory"/>.</summary>
    public static Element Template(BrowseDirectoryProps props, in ViewContext ctx)
    {
        // The loading directory is built once per template instance; the body once per run of this template, which the host re-runs only when the band list,
        // a tile row or the verdict changed.
        var skeleton = ctx.Hooks.UseRef<(WidthTier Tier, Element? Tree)>((WidthTier.Standard, null));
        var facts = ctx.Tier();
        var frame = WinUiTiers.FrameOf(facts);
        var layout = WinUiBrowseTiers.Of(facts);   // the tier's record: tiles, chips, columns, charts, band gap
        float firstW = WinUiRegion.EstimateWidth(facts, ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width);
        Element? body = null;
        float clipInset = WinUiPageHeadRules.TitleBodyTop(facts);

        Element region = new SkelRegionEl(
            Pending: () => props.Load == BrowseLoad.Pending, Failed: () => props.Load == BrowseLoad.Failed,
            Content: () => body ??= Content(props.Groups, layout, firstW),
            ShimmerSource: () => skeleton.Value.Tree is { } tree && skeleton.Value.Tier == layout.Tier ? tree : (skeleton.Value = (layout.Tier, Skeleton(layout, firstW))).Tree!.Value,
            // Retry vacancy: `Entities.Refresh`, never `Demand`'s `Ensure` (a sealed Asked-and-unanswered row is a no-op for `Ensure`).
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.Retry),
            Reveal: SkelReveal.None, Style: SkeletonStyle.Default, Group: null,
            SmoothResize: false);

        // The subtitle under the masthead's title, then the bands; the page's insets are the frame's.
        Element directory = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = layout.BandGap,
            Padding = new Edges4(frame.InsetX, 0f, frame.InsetX, R.Space.DockReserve + Spacing.XXL),
            // Feathered exactly while the clip is engaged, on the render turn that poses it.
            EdgeFade = new EdgeFadeSpec(EdgeMask.Top, BrowseMastheadMetrics.ClipFadeBand) { WhileStuck = true },
            Children = [Subtitle(), region],
        }.StickyClip(clipInset);

        return ScrollView(new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            // The reserve is a SPACER above the clipped node, not padding inside it: the cut engages exactly when the content reaches the band.
            Children = [new BoxEl { Height = clipInset + WinUiBrowseRules.SubtitleGap, HitTestVisible = false }, directory],
        }) with { Grow = 1f, MinWidth = 0f, ScrollKey = props.ScrollKey };
    }

    /// <summary>The line under the title: body 14/20 in the secondary ink (W-Browse), wrapping to two lines at Compact.</summary>
    static Element Subtitle()
        => Body(Loc.Get(Strings.Browse.HomeSubtitle)).Secondary() with { MinWidth = 0f, Wrap = TextWrap.Wrap };

    /// <summary>The Ready content: the bands, or the "unavailable" vacancy for a directory that answered with no categories.</summary>
    static Element Content(IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)>? groups, WinUiBrowseLayout layout, float firstW)
        => groups is null || groups.Count == 0
            ? Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Browse.Unavailable), subtitle: "")
            : DirectoryBody(groups, live: true, s_liveChartsBand, layout, firstW);

    // ── the Charts band ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.BrowseCharts"/>: the SAME Fold deck Home's Charts row renders, over the SAME five chart section
    /// rows, with the deck's own header. Pending shimmers the three blank folds; Ready-and-empty is the compact "No charts right now"; Failed is a compact
    /// vacancy WITH its own Retry, refetching only the deck.</summary>
    public static Element ChartsTemplate(BrowseChartsProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var layout = WinUiBrowseTiers.Of(facts);
        float firstW = WinUiRegion.EstimateWidth(facts, ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width);
        return new SkelRegionEl(
            Pending: () => props.State == HomeLoad.Pending, Failed: () => props.State == HomeLoad.Failed,
            Content: () => props.Deck.Count == 0
                ? Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Home.ChartsEmpty), subtitle: "")
                : Deck(props.Deck, props.OpenTile, props.OpenHeader, layout, firstW),
            ShimmerSource: () => Deck(HomeBrowseCards.ChartDeckSeed, s_noopSection, null, layout, firstW),
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: props.Retry),
            Reveal: SkelReveal.None, Style: SkeletonStyle.Default, Group: null,
            SmoothResize: false);
    }

    /// <summary>The Charts deck in the tier's form: a stack of full-width tiles at Compact and Small, the paged deck from Standard (3 up, 5 at Huge), each at the
    /// tier's tile height and cover (W-Browse-Huge: 72 high, 56 covers).
    /// <para>A crossing never snaps the band. Within a form the part survives: the deck is ONE paged shelf whose column cap and tile height are live props, the
    /// stack ONE column at the band's width. The paged deck and the stack are genuinely different trees, so the two forms are keyed apart and cross-fade on
    /// the crossing that swaps them. The band around them (<see cref="BandBox"/>) reflows its height, so the bands below glide to their new place.</para></summary>
    static Element Deck(IReadOnlyList<HomeSectionView> sections, Action<HomeSectionView> openTile, Action? openHeader, WinUiBrowseLayout layout, float firstW)
    {
        var band = layout.Charts;
        string title = Loc.Get(Strings.Browse.Charts);
        Element form = band.Stacked
            ? Responsive.Of("browse:charts-stack", w => HomeModules.FoldStack(sections, title, openTile, w, openHeader, band.TileH, band.Cover), fallback: firstW)
            : HomeModules.FoldDeck(sections, title, openTile, openHeader, band.MaxColumns, band.TileH, band.Cover);
        Element keyed = new BoxEl
        {
            Key = band.Stacked ? WinUiBrowseTiers.ChartsStackKey : WinUiBrowseTiers.ChartsDeckKey,
            Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Enter = WinUiDiscoveryMotion.SwapFade, Exit = WinUiDiscoveryMotion.SwapFade, Transition = WinUiDiscoveryMotion.SwapFadeMotion,
            Children = [form],
        };
        // The keyed form sits in a CHILDREN LIST, never as the region's (or a component's) single child: a single-child slot reconciles a same-type child in place
        // and ignores its key. A children list matches by key, so the old form exits (fading) and the new one enters.
        return new BoxEl { Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = [keyed] };
    }

    static readonly Action<HomeSectionView> s_noopSection = static _ => { };

    /// <summary>The live Charts slot: the core host (the band box around it carries the key, the reflow and the entrance).</summary>
    static readonly Func<WinUiBrowseLayout, float, Element> s_liveChartsBand = static (_, _) => Embed.Comp(static () => new BrowseChartsBand());

    /// <summary>The loading Charts slot: the Fold deck over its seed, in the same band box.</summary>
    static readonly Func<WinUiBrowseLayout, float, Element> s_seedChartsBand = static (layout, firstW) => Deck(HomeBrowseCards.ChartDeckSeed, s_noopSection, null, layout, firstW);

    // ── the body and its bands ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The directory body: the bands in WinUI's order, each keyed by its band. Charts is chrome, not a tile band: its bucket is SWALLOWED (the two chart
    /// categories never render as tiles) and <paramref name="chartsBandAt"/> is injected in its slot, always. At Huge Top and For you share one row.
    /// <paramref name="live"/> false is the loading directory: every cell still hovers and does nothing.</summary>
    static Element DirectoryBody(IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> groups,
                                 bool live, Func<WinUiBrowseLayout, float, Element> chartsBandAt, WinUiBrowseLayout layout, float firstW)
    {
        var children = new List<Element>(groups.Count + 2);
        int band = 1;   // the first band's OWN entrance delay
        var top = ItemsOf(groups, BrowseGroup.Top);
        var foryou = ItemsOf(groups, BrowseGroup.ForYou);
        if (layout.TopBeside && top is { Count: > 0 } && foryou is { Count: > 0 })
        {
            children.Add(TopRow(top, foryou, live, band, layout));
            band += 2;
        }
        else
        {
            if (top is { Count: > 0 }) children.Add(BandOf(BrowseGroup.Top, top, live, band++, layout, firstW));
            if (foryou is { Count: > 0 }) children.Add(BandOf(BrowseGroup.ForYou, foryou, live, band++, layout, firstW));
        }
        foreach (var g in s_order)
        {
            if (g is BrowseGroup.Top or BrowseGroup.ForYou) continue;
            if (g == BrowseGroup.Charts) { children.Add(BandBox(s_bandKeys[(int)BrowseGroup.Charts], band++, 0f, [chartsBandAt(layout, firstW)])); continue; }
            if (ItemsOf(groups, g) is { Count: > 0 } items) children.Add(BandOf(g, items, live, band++, layout, firstW));
        }
        return new BoxEl { Direction = 1, Gap = layout.BandGap, MinWidth = 0f, Children = children.ToArray() };
    }

    static IReadOnlyList<BrowseCategory>? ItemsOf(IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> groups, BrowseGroup g)
    {
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Group == g) return groups[i].Items;
        return null;
    }

    static readonly string[] s_bandKeys =
        ["browse-band:top", "browse-band:foryou", "browse-band:genres", "browse-band:mood", "browse-band:charts", "browse-band:more"];

    /// <summary>One band: the heading over its body, cascading in at <paramref name="index"/> (the only thing the index is for). Genres carries the
    /// "Explore all categories" link on its heading's end where the tier has no cell for it.</summary>
    static Element BandOf(BrowseGroup group, IReadOnlyList<BrowseCategory> items, bool live, int index, WinUiBrowseLayout layout, float firstW)
        => BandBox(s_bandKeys[(int)group], index, layout.HeaderGap, [Heading(group, layout), BodyOf(group, items, live, layout)]);

    static Element BodyOf(BrowseGroup group, IReadOnlyList<BrowseCategory> items, bool live, WinUiBrowseLayout layout)
    {
        var cells = new Element[items.Count];
        switch (group)
        {
            case BrowseGroup.Top:
                for (int i = 0; i < cells.Length; i++) cells[i] = WinUiBrowseCells.Top(BrowseTiles.ModelOf(items[i], live), layout);
                return WinUiBrowseCells.Cells(layout.Top, layout.TopGap, layout.TopGap, layout.TopH, cells) with { Key = "browse-top-grid" };
            case BrowseGroup.ForYou:
                for (int i = 0; i < cells.Length; i++) cells[i] = WinUiBrowseCells.Chip(BrowseTiles.ModelOf(items[i], live), layout);
                if (layout.ForYouStrip) return Controls.ChipRail(cells, "browse:foryou");
                return WinUiBrowseCells.Cells(layout.ForYou, layout.ForYouGap, layout.ForYouGap, layout.ForYouH, cells) with { Key = "browse-foryou-grid" };
            default:
            {
                var tiles = new BrowseTileModel[items.Count];
                for (int i = 0; i < tiles.Length; i++) tiles[i] = BrowseTiles.ModelOf(items[i], live);
                return WinUiBrowseCells.TileGrid(tiles, layout, "browse-tiles:" + (int)group);
            }
        }
    }

    /// <summary>Huge's Top and For you as ONE row, 2 : 1 (W-Browse-Huge): For you's chips two across, the "Explore all categories" cell the last of them.</summary>
    static Element TopRow(IReadOnlyList<BrowseCategory> top, IReadOnlyList<BrowseCategory> foryou, bool live, int index, WinUiBrowseLayout layout)
    {
        var chips = new List<Element>(foryou.Count + 1);
        for (int i = 0; i < foryou.Count; i++) chips.Add(WinUiBrowseCells.Chip(BrowseTiles.ModelOf(foryou[i], live), layout));
        if (layout.ExploreCell) chips.Add(WinUiBrowseCells.ExploreCell(live ? s_exploreAll : null));
        return BandBox("browse-band:top", index, 0f,
        [
            new BoxEl
            {
                Direction = 0, Gap = WinUiBrowseTiers.TopRowGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = layout.HeaderGap, MinWidth = 0f, Grow = WinUiBrowseTiers.TopShare, Shrink = 1f, Basis = 0f,
                        Children = [Heading(BrowseGroup.Top, layout), BodyOf(BrowseGroup.Top, top, live, layout)],
                    },
                    new BoxEl
                    {
                        Direction = 1, Gap = layout.HeaderGap, MinWidth = 0f, Grow = 1f, Shrink = 1f, Basis = 0f,
                        Children =
                        [
                            Heading(BrowseGroup.ForYou, layout),
                            WinUiBrowseCells.Cells(layout.ForYou, layout.ForYouGap, layout.ForYouGap, layout.ForYouH, chips.ToArray()) with { Key = "browse-foryou-grid" },
                        ],
                    },
                ],
            },
        ]);
    }

    /// <summary>The "Explore all categories" verb of the directory's own cell: the directory is where it already is, so the cell is the Huge board's way of
    /// reaching the full list (the same Genres band's tiles); it reads as a link and re-opens the directory.</summary>
    static readonly Action s_exploreAll = static () => HomeVerbs.OpenBrowse();

    /// <summary>A band's box (Home's chapter shape): the keyed node is the SAME at every tier and reflows (position and size, its size revealed) when a crossing
    /// moves or resizes it, so the Charts band's deck-to-stack swap and a grid's re-fit push the bands below along a glide, never a jump; the entrance (its
    /// stagger at <paramref name="index"/>) rides the inner box.</summary>
    static BoxEl BandBox(string key, int index, float gap, Element[] children) => new()
    {
        Key = key, Direction = 1, MinWidth = 0f,
        Layout = WinUiDiscoveryMotion.Reflow,
        Children =
        [
            new BoxEl { Direction = 1, Gap = gap, MinWidth = 0f, Animate = Design.Entrance.Row(index), Children = children },
        ],
    };

    /// <summary>A band heading: the Subtitle rung (W-Browse: `h2` 20/28 semibold) — on Genres, with the "Explore all categories" link at its end.</summary>
    static Element Heading(BrowseGroup g, WinUiBrowseLayout layout)
    {
        Element title = WinUiBrowseCells.BandHeading(GroupLabel(g)) with { Grow = 1f, Shrink = 1f };
        if (g != BrowseGroup.Genres || layout.ExploreCell) return title;
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = layout.HeaderToolsH,
            Children = [title, HyperlinkButton.Create(Loc.Get(Strings.Browse.ExploreAll), s_exploreAll) with { Shrink = 0f }],
        };
    }

    /// <summary>The localized band heading (membership is uri-keyed and culture-independent; only the label translates). Never asked for Charts — that band
    /// carries the Fold deck's own header.</summary>
    static string GroupLabel(BrowseGroup g) => g switch
    {
        BrowseGroup.Top => Loc.Get(Strings.Browse.Top),
        BrowseGroup.ForYou => Loc.Get(Strings.Browse.ForYou),
        BrowseGroup.Genres => Loc.Get(Strings.Browse.Genres),
        BrowseGroup.MoodActivity => Loc.Get(Strings.Browse.MoodActivity),
        _ => Loc.Get(Strings.Theme.Winui.Discovery.More),
    };

    /// <summary>The loading directory: the SAME body over the seeds with the Charts band shimmering off <see cref="HomeBrowseCards.ChartDeckSeed"/>, the whole
    /// tree <c>.Skeletonized(true)</c>. Built once per template instance and handed to its region.</summary>
    static Element Skeleton(WinUiBrowseLayout layout, float firstW)
        => DirectoryBody(BrowseTaxonomy.Grouped(BrowseDirectorySeeds.Categories), live: false, s_seedChartsBand, layout, firstW).Skeletonized(true);
}
