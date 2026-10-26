// ── Themes/Classic/Structure/Views/Pages/ClassicBrowseDirectory.cs — Classic's `Pages.Browse` and `Browse.Charts` (themes plan 6.1, T10a2) ────
//
// From `Entities\Browse.Page.cs` (`DirectoryPage.Render`, `ChartsBand.Render`) and `Entities\Browse.UI.cs` §2: the directory's picture. The masthead band
// paints NOTHING (live Mica), so the directory cuts ITSELF at the band's lower edge: a 100-DIP spacer ABOVE a node clipped with `StickyClip(84)`, whose
// 24-DIP top feather arms only on the clip's engage edge — content dissolves into the band, is never guillotined by it, and never softens at rest.
// `SkelReveal.None`: the bands own the entrance.
//
// ── THE DIRECTORY BODY IS EAGER ──────────────────────────────────────────────────────────────────────────────────────
//
// It mounts exactly once per page mount with a taxonomy-fixed band count — the two conditions `Design.Entrance` needs — so each band cascades in 40 ms
// behind the last, starting at index 1. The same `DirectoryBody` renders the loaded page and (over `BrowseDirectorySeeds`, `.Skeletonized(true)`) the
// loading one, which is what keeps the band and wrap counts identical across the swap (§0 W16, parity 53). The Charts band is its own core host
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
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Browse directory.</summary>
internal static class ClassicBrowseDirectory
{
    /// <summary>The registered template of <see cref="ViewKeys.BrowseDirectory"/>.</summary>
    public static Element Template(BrowseDirectoryProps props, in ViewContext ctx)
    {
        // The loading directory is built once per template instance (the old page's `_skeleton ??=`); the body once per run of this template,
        // which the host re-runs only when the band list, a tile row or the verdict changed.
        var skeleton = ctx.Hooks.UseRef<(WidthTier Tier, Element? Tree)>((WidthTier.Standard, null));
        var facts = ctx.Tier();
        var page = ClassicTiers.PageOf(facts);   // the one page gutter (F3)
        var layout = ClassicBrowseTiers.Of(facts);   // the tier's record: chips, columns, charts, band gap
        float firstW = ClassicRegion.EstimateWidth(facts, ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width);
        Element? body = null;

        // smoothResize false: a page-level region whose branches differ by hundreds of DIP lands its height at once.
        Element region = new SkelRegionEl(
            Pending: () => props.Load == BrowseLoad.Pending, Failed: () => props.Load == BrowseLoad.Failed,
            Content: () => body ??= Content(props.Groups, layout, firstW),
            ShimmerSource: () => skeleton.Value.Tree is { } tree && skeleton.Value.Tier == layout.Tier ? tree : (skeleton.Value = (layout.Tier, Skeleton(layout, firstW))).Tree!.Value,
            // Retry vacancy: `Entities.Refresh`, never `Demand`'s `Ensure` — a row the planner has already sealed
            // Asked-and-unanswered is exactly a no-op for `Ensure` (P4's dedupe: `wanted & ~known & ~asked`), so a
            // failed directory's Retry button did nothing until it un-asks first (root cause of the failure card).
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.Retry),
            Reveal: SkelReveal.None, Style: SkeletonStyle.Default, Group: null,
            SmoothResize: false);

        Element directory = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = layout.BandGap,
            // The reserve is a SPACER above the clipped node (so the cut engages when content reaches the band, not at rest), leaving the
            // gutters and the bottom on the node.
            Padding = page.Pad(0f, R.Space.DockReserve + Spacing.XXL),
            // Feathered exactly while the clip is engaged, on the render turn that poses it (RCA 2026-09-25 F(ii)).
            EdgeFade = new EdgeFadeSpec(EdgeMask.Top, BrowseMastheadMetrics.ClipFadeBand) { WhileStuck = true },
            Children = [region],
        }.StickyClip(BrowseMastheadMetrics.ClipInset);

        return ScrollView(new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            // The reserve is a SPACER above the clipped node, not padding inside it: the cut engages exactly when the
            // content reaches the band.
            Children = [new BoxEl { Height = BrowseMastheadMetrics.BodyTop, HitTestVisible = false }, directory],
        }) with { Grow = 1f, MinWidth = 0f, ScrollKey = props.ScrollKey };
    }

    /// <summary>The Ready content: the bands, or the "unavailable" vacancy for a directory that answered with no categories.</summary>
    static Element Content(IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)>? groups, ClassicBrowseLayout layout, float firstW)
        => groups is null || groups.Count == 0
            ? Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Browse.Unavailable), subtitle: "")
            : DirectoryBody(groups, live: true, s_liveChartsBand, layout, firstW);

    // ── the Charts band ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.BrowseCharts"/>: the SAME Fold deck Home's Charts row renders, over the SAME five chart
    /// section rows, with the deck's own header (no eyebrow above it — parity 64) and no per-tile eyebrow (parity 65). Pending shimmers the three
    /// blank folds; Ready-and-empty is the compact "No charts right now"; Failed is a compact vacancy WITH its own Retry, refetching only the deck
    /// (parity 66, 97).</summary>
    public static Element ChartsTemplate(BrowseChartsProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var layout = ClassicBrowseTiers.Of(facts);
        float firstW = ClassicRegion.EstimateWidth(facts, ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width);
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

    /// <summary>The Charts deck in the tier's form: a stack of full-width tiles at Compact and Small (136 / 140 high), the paged deck at Standard (two up) and
    /// Huge (four up), each at the tier's tile height and cover (<c>C-Browse-*</c>).
    /// <para>A crossing never snaps the band (plan 3.3). Within a form the part survives: the deck is ONE paged shelf whose column cap and tile height are
    /// live props (1920 re-fits the same deck), the stack ONE column at the band's width (528 keeps it). The paged deck and the stack are genuinely different
    /// trees (a horizontal virtual shelf with its pager; a plain column), so the two forms are keyed apart and cross-fade on the crossing that swaps them (the
    /// content edge between Small and Standard): the shared tier cross-fade the release notes' highlight cards use. The band around them
    /// (<see cref="BandBox"/>) reflows its height, so the bands below glide to their new place instead of jumping.</para></summary>
    static Element Deck(IReadOnlyList<HomeSectionView> sections, Action<HomeSectionView> openTile, Action? openHeader, ClassicBrowseLayout layout, float firstW)
    {
        var band = layout.Charts;
        string title = Loc.Get(Strings.Browse.Charts);
        Element form = band.Stacked
            ? Responsive.Of("browse:charts-stack", w => HomeModules.FoldStack(sections, title, openTile, w, openHeader, band.TileH, band.Cover), fallback: firstW)
            : HomeModules.FoldDeck(sections, title, openTile, openHeader, band.MaxColumns, band.TileH, band.Cover);
        Element keyed = new BoxEl
        {
            Key = band.Stacked ? ClassicBrowseTiers.ChartsStackKey : ClassicBrowseTiers.ChartsDeckKey,
            Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Transition = ClassicTierMotion.SwapFadeMotion,
            Children = [form],
        };
        // The keyed form sits in a CHILDREN LIST, never as the region's (or a component's) single child: a single-child slot reconciles a same-type
        // child in place and ignores its key, so the deck box would be patched into the stack box (no remount, no cross-fade, the old key kept on the
        // node). A children list matches by key, so the old form exits (fading) and the new one enters.
        return new BoxEl { Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = [keyed] };
    }

    static readonly Action<HomeSectionView> s_noopSection = static _ => { };

    /// <summary>The live Charts slot: the core host (the band box around it carries the key, the reflow and the entrance).</summary>
    static readonly Func<ClassicBrowseLayout, float, Element> s_liveChartsBand = static (_, _) => Embed.Comp(static () => new BrowseChartsBand());

    /// <summary>The loading Charts slot: the Fold deck over its seed, in the same band box.</summary>
    static readonly Func<ClassicBrowseLayout, float, Element> s_seedChartsBand = static (layout, firstW) => Deck(HomeBrowseCards.ChartDeckSeed, s_noopSection, null, layout, firstW);

    // ── the body and its bands ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The directory body (0.2.9 <c>BrowseDirectory.Body</c>): the bands in <see cref="BrowseTaxonomy.BandOrder"/>
    /// — Top, Charts, For you, Genres, Mood &amp; activity, More — each keyed by its band, Gap 16, gutterless (the page
    /// owns the frame). Charts is chrome, not a tile band: its bucket is SWALLOWED (the two chart categories never render
    /// as tiles) and <paramref name="chartsBandAt"/> is injected in its slot, always. <paramref name="live"/> false is the
    /// loading directory: every cell still hovers and does nothing.</summary>
    static Element DirectoryBody(IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> groups,
                                 bool live, Func<ClassicBrowseLayout, float, Element> chartsBandAt, ClassicBrowseLayout layout, float firstW)
    {
        var children = new List<Element>(groups.Count + 2);
        int band = 1;   // the first band's OWN entrance delay (0.2.9: the masthead used to hold rung 0)
        for (int b = 0; b < BrowseTaxonomy.BandOrder.Count; b++)
        {
            var g = BrowseTaxonomy.BandOrder[b];
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

    /// <summary>The state a band's responsive grid is gated on: the memoized item list BY REFERENCE plus the live bit, so a
    /// page re-render that changed nothing (the under-band clip edge) rebuilds no grid.</summary>
    sealed record BandState(IReadOnlyList<BrowseCategory> Items, bool Live, ClassicBrowseLayout Layout);

    static readonly string[] s_bandKeys =
        ["browse-band:top", "browse-band:foryou", "browse-band:genres", "browse-band:mood", "browse-band:charts", "browse-band:more"];

    /// <summary>One band: an eyebrow label over the density its destinations earn, cascading in at
    /// <paramref name="index"/> (the only thing the index is for).</summary>
    static Element BandOf(BrowseGroup group, IReadOnlyList<BrowseCategory> items, bool live, int index, ClassicBrowseLayout layout, float firstW)
    {
        var state = new BandState(items, live, layout);
        Element body = group switch
        {
            BrowseGroup.Top => WrapRow(items, live, word: true, layout),
            BrowseGroup.ForYou => WrapRow(items, live, word: false, layout),
            // The Genres columns are the tier's fixed count: no width, no Responsive box, nothing to seed.
            BrowseGroup.Genres => LinkGridOf(state),
            // Mood and More fit INSIDE the tier at their measured width; the first frame is the region's estimate.
            BrowseGroup.MoodActivity => Responsive.Of("browse:moods", state, static (st, w) => BarGrid(st, w), fallback: firstW),
            _ => Responsive.Of("browse:more", state, static (st, w) => MoreGrid(st, w), fallback: firstW),
        };
        return BandBox(s_bandKeys[(int)group], index, Spacing.S, [BandLabel(GroupLabel(group)), body]);
    }

    /// <summary>A band's box (Home's chapter shape): the keyed node is the SAME at every tier and reflows (position and size, its size revealed) when a crossing
    /// moves or resizes it, so the Charts band's deck-to-stack swap and a grid's re-fit push the bands below along a glide, never a jump (plan 3.3); the
    /// entrance (its stagger at <paramref name="index"/>) rides the inner box.</summary>
    static BoxEl BandBox(string key, int index, float gap, Element[] children) => new()
    {
        Key = key, Direction = 1, MinWidth = 0f,
        Layout = ClassicTierMotion.Reflow,
        Children =
        [
            new BoxEl { Direction = 1, Gap = gap, MinWidth = 0f, Animate = Design.Entrance.Row(index), Children = children },
        ],
    };

    /// <summary>Top / For you: a wrapping row of pills at their own natural width — no grid, no column count. At Compact For you is one scrolling strip with a
    /// faded edge (<c>C-Browse-Compact</c>), its chips 40 high.</summary>
    static Element WrapRow(IReadOnlyList<BrowseCategory> items, bool live, bool word, ClassicBrowseLayout layout)
    {
        var cells = new Element[items.Count];
        for (int i = 0; i < cells.Length; i++)
        {
            var m = BrowseTiles.ModelOf(items[i], live);
            cells[i] = word ? ClassicBrowseCells.Word(m, layout) : ClassicBrowseCells.Name(m, layout);
        }
        if (!word && layout.ForYouStrip) return Controls.ChipRail(cells, "browse:foryou");
        return new BoxEl { Direction = 0, Gap = ClassicBrowseRules.ChipGap, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = cells };
    }

    static Element LinkGridOf(BandState st)
    {
        var tiles = new BrowseTileModel[st.Items.Count];
        for (int i = 0; i < tiles.Length; i++) tiles[i] = BrowseTiles.ModelOf(st.Items[i], st.Live);
        return ClassicBrowseCells.LinkGrid(tiles, st.Layout);
    }

    static Element BarGrid(BandState st, float width)
    {
        int cols = st.Layout.MoodColumns(width);
        var cells = new Element[st.Items.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = ClassicBrowseCells.Bar(BrowseTiles.ModelOf(st.Items[i], st.Live));
        // One key at every column count: a re-fit moves the same cells into their new tracks.
        return ClassicBrowseCells.StarGrid(cols, Spacing.XS, Spacing.XS, cells) with { Key = "browse-mood-grid" };
    }

    /// <summary>The More grid; the cell width handed to Peek is <c>width / cols</c> with the gaps NOT subtracted
    /// (0.2.9 <c>BrowseDirectory.cs:312</c>, kept — it only bounds the copy column).</summary>
    static Element MoreGrid(BandState st, float width)
    {
        int cols = st.Layout.MoreColumns(width);
        float cellW = width / cols;
        var cells = new Element[st.Items.Count];
        for (int i = 0; i < cells.Length; i++) cells[i] = ClassicBrowseCells.Peek(BrowseTiles.ModelOf(st.Items[i], st.Live), cellW);
        // One key at every column count: a re-fit moves the same cells into their new tracks.
        return ClassicBrowseCells.StarGrid(cols, Spacing.M, Spacing.M, cells) with { Key = "browse-more-grid" };
    }

    /// <summary>A band heading NAMES the row; it is not a peer of its destinations — the eyebrow rung (Caption 600 +
    /// tracking, secondary), sentence case, never caps-transformed (a localized string).</summary>
    static Element BandLabel(string label)
        => R.Type.Eyebrow(label) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>The localized band heading (membership is uri-keyed and culture-independent; only the label translates).
    /// Never asked for Charts — that band carries the Fold deck's own header.</summary>
    static string GroupLabel(BrowseGroup g) => g switch
    {
        BrowseGroup.Top => Loc.Get(Strings.Browse.Top),
        BrowseGroup.ForYou => Loc.Get(Strings.Browse.ForYou),
        BrowseGroup.Genres => Loc.Get(Strings.Browse.Genres),
        BrowseGroup.MoodActivity => Loc.Get(Strings.Browse.MoodActivity),
        _ => Loc.Get(Strings.Browse.More),
    };

    /// <summary>The loading directory (0.2.9 <c>BrowseDirectory.Skeleton</c>): the SAME body over the seeds — 4 / 10 / 25 /
    /// 14 / 3 — with the Charts band shimmering off <see cref="HomeBrowseCards.ChartDeckSeed"/>, the whole tree
    /// <c>.Skeletonized(true)</c>. Built once per template instance and handed to its region.</summary>
    static Element Skeleton(ClassicBrowseLayout layout, float firstW)
        => DirectoryBody(BrowseTaxonomy.Grouped(BrowseDirectorySeeds.Categories), live: false, s_seedChartsBand, layout, firstW).Skeletonized(true);
}
