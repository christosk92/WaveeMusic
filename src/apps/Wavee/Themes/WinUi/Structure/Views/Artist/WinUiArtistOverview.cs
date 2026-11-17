// ── Themes/WinUi/Structure/Views/Artist/WinUiArtistOverview.cs — WinUI's artist overview (themes plan 6.2; P6 W6a) ────────────────────────────────────
//
// Board W-Artist: under the pivot the OVERVIEW is two columns 36 apart, Popular (the chart, weight 1.2) | the latest release card over the Discography preview (weight 1),
// stacking below a 1100-wide page (the column count is `WinUiArtistTiers.OverviewColumns`, held 24 once two); at Huge (W-Artist-Huge) three: Popular | Discography | the latest
// release (and the upcoming card and the compact pick under it; at two columns and one they follow Discography). The Discography preview is three covers of the chosen type under two combos (type, order) and "Show all N albums"; at Huge it
// is two titled rows (Albums, Singles & EPs) under "Newest" and "Show all". The full facets (the core's grid and album drawer) follow as the Albums, Singles & EPs and Compilations sections.
//
// About and Fans stay sections under the overview (their pivot anchors live there): the Huge board stacks them in the third column; here the third column holds the latest release alone.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

internal static class WinUiArtistOverview
{
    /// <summary>The overview block at the page's measure: the columns by the tier's rule, each keyed so a crossing re-parents nothing it need not.</summary>
    public static Element Block(ArtistPageProps p, WinUiArtistLatch latch, WidthTier tier, float inset, in TierFacts facts)
    {
        Artist a = p.Artist;
        bool popular = false, latest = false, upcoming = false;
        for (int i = 0; i < p.Sections.Count; i++)
        {
            var s = p.Sections[i].Section;
            popular |= s == ArtistSection.Popular;
            latest |= s == ArtistSection.LatestRelease;
            upcoming |= s == ArtistSection.Upcoming;
        }
        var accentSignal = p.AccentSignal;
        var accentFn = p.AccentFn;
        var overlay = p.Overlay;
        var surface = latch.Surface;
        // The latest release's slot stands from the moment the overview names one: its card fields (an answer of their own, often a few frames after
        // the overview's) land in place over its placeholder, so the side column has its final height when the page lands (the owner's recording,
        // 2026-11-15: the column grew under the chart, and the chart grew rows to match).
        bool latestSlot = latest || a.Latest.IsValid;
        long stamp = PreviewStamp(a);
        int tierRows = WinUiArtistTiers.ChartRowsOf(tier);
        int fitMax = Math.Max(tierRows, Math.Min(a.PopularSlots.Length, WinUiArtistRules.PopularFitMaxRows));
        float pitch = WinUiOptions.ArtworkInRows() ? WinUiArtistChartLayout.RowHeight : WinUiArtistChartLayout.OneLineRowHeight;
        Func<int, float, PreviewState, PopularHold, ChartFit, bool, Element> build = (rung, slot, state, hold, fit, landing) =>
        {
            int columns = latch.Columns = WinUiArtistTiers.OverviewColumnsOf(rung, tier);
            // W-Artist: the latest release, then Discography; the upcoming card and the artist's pick (no board draws one) follow under Discography, the pick
            // compact (no photograph band) so it never outweighs the column.
            float target = WinUiDetailRules.TargetOf(tier);
            // Beside the side column (two columns) Popular takes as many rows as fill the side column's height, never fewer than the tier's (owner
            // 2026-11-07: a tall side column left a large empty area under five rows; balanced columns keep five). The chart MOUNTS at the rows the
            // side column's arithmetic predicts (SeedRows) and the measured fit corrects a wrong prediction: measured alone, the chart mounted at the
            // tier's rows and re-fitted a frame or three later, which drew the rows growing into place on every landing.
            bool fits = columns == 2 && popular;
            int seed = fits ? SeedRows(PreviewWidth(slot, columns), target, latestSlot, upcoming, a.HasPick, tierRows, fitMax, pitch) : 0;
            fit.Arm(fits, tierRows, fitMax, pitch, seed);
            Element? chart = popular
                ? new BoxEl
                {
                    Key = "ov:chart", Direction = 1, MinWidth = 0f, OnBoundsChanged = fits ? fit.OnChart : null,
                    Children = [Artist.Chart(a, accentSignal, rows: fits ? fit.Rows : null, seedRows: seed)],
                }
                : null;
            Element? latestCard = latestSlot ? LatestBlock(a, target, known: latest) with { Layout = WinUiArtistMotion.Block } : null;
            var after = new List<Element>(2);
            if (upcoming)
            {
                // The band arm (cover · copy · date · actions in one row) left the copy ~33 DIP at Compact and "Upcoming · Album" ran out of it
                // (audit B8): Compact stacks the column arm; the arms swap on a declared fade across the edge.
                bool column = tier == WidthTier.Compact;
                Element card = WinUiArtistBanners.UpcomingCard(a, accentFn, wide: column) with
                {
                    Key = column ? "upcoming:column" : "upcoming:band",
                    Enter = WinUiScreenMotion.SwapFade, Exit = WinUiScreenMotion.SwapFade, Transition = WinUiScreenMotion.SwapFadeMotion,
                };
                after.Add(WinUiArtistSections.SectionBlock(Loc.Get(Strings.Artist.Upcoming), card) with { Key = "ov:upcoming", Layout = WinUiArtistMotion.Block });
            }
            // The compact pick is one composition in every arrangement, so it moves with the restack like any other block.
            if (a.HasPick)
                after.Add(WinUiArtistPick.Card(a, accentFn, horizontal: false, overlay, surface, target, photo: false) with { Key = "ov:pick", Layout = WinUiArtistMotion.Block });
            // The preview's covers are sized from its column's width, worked out here from the slot and the arrangement (not measured by the preview), so
            // the commit that restacks lays the side column out at its final height and the overview's eased height aims at the right number.
            Element preview = Embed.Comp(new PreviewProps(a, stamp, columns >= 3, target, PreviewWidth(slot, columns), state), static () => new PreviewHost()) with { Key = "ov:preview" };

            var side = new List<Element>(4);
            if (latestCard is not null) side.Add(latestCard.Value);
            if (columns >= 3)
            {
                side.AddRange(after);
                return Grid(0, WinUiArtistRules.HugeColumnGap,
                    Popular(PopularWidth(slot, columns), chart, hold, row: true),
                    Column("ov:col:discography", WinUiArtistRules.PopularGrow, [preview], landing),
                    Column(SideKey(columns), WinUiArtistRules.OtherGrow, side.ToArray(), landing));
            }

            // One column and two are one grid: it turns from a row into a column and Popular keeps its node (it resizes in place). The side column is keyed
            // by the arrangement, so a restack fades the old one out where it stands and the new one in at its slot (WinUiArtistMotion): nothing slides across
            // Popular. Stacked, the columns stop growing and stack at their content's height: Popular, then the side column, one section gap apart.
            side.Add(new BoxEl { Key = "ov:preview:slot", Direction = 1, MinWidth = 0f, Layout = WinUiArtistMotion.Block, Children = [preview] });
            side.AddRange(after);
            bool two = columns == 2;
            float grow = two ? 1f : 0f;
            Element sideColumn = Column(SideKey(columns), grow * WinUiArtistRules.OtherGrow, side.ToArray(), landing) with { OnBoundsChanged = two ? fit.OnSide : null };
            // Stacked without a chart, an empty Popular column would add a section gap above the side column.
            if (!two && chart is null) return Grid(1, WinUiArtistRules.SectionGap, sideColumn);
            return Grid((byte)(two ? 0 : 1), two ? WinUiArtistRules.ColumnGap : WinUiArtistRules.SectionGap,
                Popular(two ? PopularWidth(slot, columns) : float.NaN, chart, hold, two),
                sideColumn);
        };
        // Popular's laid-out width in an arrangement (the slot stacked), NaN without a chart: what a restack's hold compares.
        Func<int, float, float> popularWidth = (rung, slot) => popular ? PopularWidth(slot, WinUiArtistTiers.OverviewColumnsOf(rung, tier)) : float.NaN;
        return Embed.Comp(new GridProps(build, popularWidth, inset, p.Width, WinUiArtistTiers.OverviewRungOf(in facts)),
            static () => new GridHost()) with { Key = "artist:overview" };
    }

    /// <summary>The overview's shimmer source for the page's skeleton (<c>WinUiArtistPage.Shimmer</c>): the loaded overview's arrangement through the same
    /// grid, columns and widths, Popular as the chart's own skeleton at the rows the side column holds (<see cref="SeedRows"/>), and the side column as
    /// the latest release card and the Discography preview over placeholder words and covers. What the skeleton cannot know yet (an upcoming card, a
    /// pick) is not drawn, so the column stands at the height a page without them lands at.</summary>
    internal static Element Skeleton(Artist a, float pageWidth, float inset, WidthTier tier, in TierFacts facts, ArtistChartStyle? chartStyle)
    {
        float slot = MathF.Max(1f, pageWidth - 2f * inset);
        int columns = WinUiArtistTiers.OverviewColumnsOf(WinUiArtistTiers.OverviewRungOf(in facts), tier);
        float target = WinUiDetailRules.TargetOf(tier);
        int tierRows = WinUiArtistTiers.ChartRowsOf(tier);
        float pitch = WinUiOptions.ArtworkInRows() ? WinUiArtistChartLayout.RowHeight : WinUiArtistChartLayout.OneLineRowHeight;
        int rows = columns == 2 ? SeedRows(PreviewWidth(slot, columns), target, latest: true, upcoming: false, pick: false, tierRows, WinUiArtistRules.PopularFitMaxRows, pitch) : tierRows;
        Element chart = chartStyle is null ? new BoxEl()
            : new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children = [chartStyle.Skeleton(ArtistChartSkeletonArgs.For(0, !WinUiOptions.ArtworkInRows(), WinUiOptions.ArtworkInRows(), WinUiArtistTiers.ChartColumnsOf(tier), rows))],
            };
        float previewW = PreviewWidth(slot, columns);
        Element preview = columns >= 3
            ? PreviewHost.RowsPlaceholder(target, previewW, Loc.Get(Strings.Theme.Winui.Detail.Newest), a)
            : PreviewHost.Preview(target, PreviewHost.Combo(target, Artist.FacetTitle(DiscoFacet.Albums), [], null),
                PreviewHost.Combo(target, Loc.Get(Strings.Theme.Winui.Detail.Newest), [], null), PreviewHost.Placeholders(PreviewCell(previewW)),
                PreviewHost.Link(Loc.Get(Strings.Theme.Winui.Detail.ShowAll), a, DiscoFacet.Albums));
        Element latestCard = LatestCard(a, target, placeholder: true);
        var hold = new PopularHold(float.NaN, false, false);
        // Unkeyed: a placeholder that carried the content's keys handed each key from its shimmer node to the content's in one frame where the
        // reveal cuts (reduced motion), which reads as a remount of the overview's columns.
        if (columns >= 3)
            return Grid(0, WinUiArtistRules.HugeColumnGap,
                Popular(PopularWidth(slot, columns), chart, hold, row: true) with { Key = null },
                Column("ov:col:discography", WinUiArtistRules.PopularGrow, [preview]) with { Key = null },
                Column(SideKey(columns), WinUiArtistRules.OtherGrow, [latestCard]) with { Key = null }) with { Key = null };
        bool two = columns == 2;
        Element side = Column(SideKey(columns), (two ? 1f : 0f) * WinUiArtistRules.OtherGrow, [latestCard, preview]) with { Key = null };
        return Grid((byte)(two ? 0 : 1), two ? WinUiArtistRules.ColumnGap : WinUiArtistRules.SectionGap,
            Popular(two ? PopularWidth(slot, columns) : float.NaN, chart, hold, two) with { Key = null }, side) with { Key = null };
    }

    /// <summary>Popular's width in a <paramref name="slot"/>-wide overview of <paramref name="columns"/> columns: the slot stacked; the slot less the
    /// side column and the gap beside it; the discography column's share at Huge.</summary>
    static float PopularWidth(float slot, int columns) => columns switch
    {
        <= 1 => slot,
        2 => MathF.Max(1f, slot - WinUiArtistRules.ColumnGap - PreviewWidth(slot, columns)),
        _ => PreviewWidth(slot, columns),
    };

    /// <summary>The preview's width in a <paramref name="slot"/>-wide overview of <paramref name="columns"/> columns: the slot stacked; the side column's
    /// share beside Popular; the discography column's share at Huge.</summary>
    static float PreviewWidth(float slot, int columns)
    {
        if (columns <= 1) return slot;
        if (columns == 2)
            return MathF.Max(1f, (slot - WinUiArtistRules.ColumnGap) * WinUiArtistRules.OtherGrow / (WinUiArtistRules.PopularGrow + WinUiArtistRules.OtherGrow));
        float weights = 2f * WinUiArtistRules.PopularGrow + WinUiArtistRules.OtherGrow;
        return MathF.Max(1f, (slot - 2f * WinUiArtistRules.HugeColumnGap) * WinUiArtistRules.PopularGrow / weights);
    }

    /// <summary>The overview's grid: a row of columns (two, three at Huge) or, stacked, a column of them. Its height eases through real layout
    /// (<see cref="WinUiArtistMotion.OverviewHeight"/>) so the sections under it glide when a restack changes it. A reflow clips its box for its
    /// life, so the box reaches past its top and its end by Popular's hit room (padding in, margin out: nothing moves), or that clip cut the room
    /// Popular's own clip keeps for the chart head's chevrons for the length of every restack, and their hit floor with it.</summary>
    static Element Grid(byte direction, float gap, params Element[] columns) => new BoxEl
    {
        Key = "ov:grid", Direction = direction, Gap = gap, MinWidth = 0f, AlignItems = direction == 0 ? FlexAlign.Start : FlexAlign.Stretch,
        Padding = new Edges4(0f, WinUiArtistRules.PopularHitRoom, WinUiArtistRules.PopularHitRoom, 0f),
        Margin = new Edges4(0f, -WinUiArtistRules.PopularHitRoom, -WinUiArtistRules.PopularHitRoom, 0f),
        Layout = WinUiArtistMotion.OverviewHeight, Children = columns, EntranceGroup = true,   // its columns' blocks stagger in (WinUiPageMotion)
    };

    /// <summary>A column: in a row it grows by its weight from a zero basis; stacked (<paramref name="grow"/> 0) it takes its content's height.
    /// <paramref name="landing"/>: the overview's first arrangement (the page landing), whose side column stands with the page instead of fading in.</summary>
    static BoxEl Column(string key, float grow, Element[] kids, bool landing = false) => new BoxEl
    {
        Key = key, Direction = 1, Gap = WinUiArtistRules.SectionGap, Grow = grow, Basis = grow > 0f ? 0f : float.NaN, MinWidth = 0f,
        // Popular's height never follows its width (the chart's rows are fixed), so it re-lays at the eased width once the side column has faded; the side
        // column fades through a restack (it is keyed by the arrangement). On the landing nothing has to make room: the column is there with the page.
        Layout = key == PopularKey ? WinUiArtistMotion.Column : landing ? WinUiArtistMotion.SideLanding : WinUiArtistMotion.Side, Children = kids, EntranceGroup = true,
        // Popular holds its old width while the side column fades out (laid out at it when the restack widens it, presented at it when the restack
        // narrows it): the clip keeps the chart's far edge (the count, the durations) inside the held width instead of over the outgoing column.
        ClipToBounds = key == PopularKey,
        // The clip reaches past the chart head's corner by the chevrons' touch slop, up into the gap above and out past the end (the padding takes it
        // back, so nothing moves): the pager's next chevron sits on the column's top-right corner, and a clip flush with it would cut its hit floor at a
        // Compact window.
        Padding = key == PopularKey ? new Edges4(0f, WinUiArtistRules.PopularHitRoom, WinUiArtistRules.PopularHitRoom, 0f) : default,
        Margin = key == PopularKey ? new Edges4(0f, -WinUiArtistRules.PopularHitRoom, -WinUiArtistRules.PopularHitRoom, 0f) : default,
    };

    /// <summary>The Popular column, <paramref name="width"/> wide beside the other columns (its weight's share of the slot; NaN stacked: the slot).
    /// An explicit width, not a grow from a zero basis: its restack resize re-lays the chart at the eased width it writes into the column's width, and
    /// a row re-solved under it (the page easing through a docked pane's slide) hands a growing column its flex share instead, which presented
    /// Popular at its new width for the whole of the resize's delay, then back at the old one when the resize started.
    /// <para>A restack that WIDENS it (two columns or three to one: a docked pane opening, a window narrowing) lays it out at its old width while the
    /// side column fades out (<paramref name="hold"/>), then releases it into an undelayed resize. Laid out at the new width at once and only PRESENTED
    /// at the old one for the resize's delay, the chart's rows (a virtual strip that fits its cells to the width it last measured) were laid out at
    /// the new width under the held clip: the durations and the pager sat past the clip's edge for the whole delay.</para></summary>
    /// <para>Beside the other columns (<paramref name="row"/>) it may shrink on the main axis (its width); stacked it never does: the main axis is then
    /// its height, which the overview's eased height would otherwise squeeze while it grows.</para>
    /// <para>The explicit width and the restack's resize (<see cref="WinUiArtistMotion.Column"/>) hold only while a restack runs
    /// (<paramref name="hold"/>). Otherwise, beside the side column, it is its weight's flex share (the same width <see cref="PopularWidth"/> works
    /// out) under <see cref="WinUiArtistMotion.ColumnSteady"/>: a slide that re-lays the page every frame (the nav pane collapsing, a docked pane's
    /// slide in one arrangement) splits the live width in one solve, where the width the last commit measured trailed the page by a frame and the
    /// side column took the difference for that frame.</para>
    static Element Popular(float width, Element? chart, PopularHold hold, bool row)
    {
        bool share = row && !hold.Restacking;
        return Column(PopularKey, 0f, chart is null ? [] : [chart.Value]) with
        {
            Width = share ? float.NaN : float.IsNaN(hold.Width) ? width : hold.Width, Shrink = row ? 1f : 0f,
            Grow = share ? WinUiArtistRules.PopularGrow : 0f, Basis = share ? 0f : float.NaN,
            Layout = !hold.Restacking ? WinUiArtistMotion.ColumnSteady : hold.Released ? WinUiArtistMotion.ColumnReleased : WinUiArtistMotion.Column,
        };
    }

    /// <summary>Popular's hold through a widening restack: the width it is laid out at until the side column has faded out (NaN: none), whether
    /// the last restack's hold has been released (its resize then runs without the delay the hold spent), and whether a restack's resize is still
    /// running (<see cref="WinUiArtistMotion.RestackSpanMs"/> from its commit).</summary>
    readonly record struct PopularHold(float Width, bool Released, bool Restacking);

    const string PopularKey = "ov:col:popular";

    /// <summary>The side column's key in an arrangement of <paramref name="columns"/> columns: a restack replaces it, which is the fade-through.</summary>
    static string SideKey(int columns) => columns switch { <= 1 => "ov:col:side:1", 2 => "ov:col:side:2", _ => "ov:col:side:3" };

    /// <summary>The rows Popular takes beside the side column, worked out before any layout from the rules and what the column holds: the latest
    /// release card (<paramref name="latest"/>: its slot, reserved while its fields come), the discography preview at <paramref name="previewW"/> (its
    /// header, the covers at their cell size, the link; reserved while a facet's releases come), the upcoming card and the compact pick, a section gap
    /// apart; then as many of the chart's rows as fit under its head, from the tier's <paramref name="tierRows"/> to <paramref name="fitMax"/>. A text
    /// the arithmetic cannot know (a pick's quote wrapping past <see cref="WinUiArtistRules.PickQuoteSeedLines"/>) leaves the measured fit
    /// (<see cref="ChartFit"/>) a row or two to correct, which it does in place.</summary>
    static int SeedRows(float previewW, float target, bool latest, bool upcoming, bool pick, int tierRows, int fitMax, float pitch)
    {
        float side = 0f;
        int blocks = 0;
        if (latest) Stack(ref side, ref blocks, LatestHeight(target));
        Stack(ref side, ref blocks, PreviewHeight(previewW, target));
        if (upcoming) Stack(ref side, ref blocks, UpcomingHeight());
        if (pick) Stack(ref side, ref blocks, PickHeight());
        float head = WinUiArtistChartLayout.ChevronBox + WinUiArtistChartLayout.HeaderGap;
        int rows = (int)MathF.Floor((side - head) / MathF.Max(1f, pitch));
        return Math.Clamp(rows, tierRows, Math.Max(tierRows, fitMax));
    }

    static void Stack(ref float side, ref int blocks, float h)
    {
        side += (blocks > 0 ? WinUiArtistRules.SectionGap : 0f) + h;
        blocks++;
    }

    /// <summary>The latest release card's height (<see cref="LatestCard"/>): its padding around the tallest of the cover, the copy's three lines and Play.</summary>
    static float LatestHeight(float target)
    {
        float copy = WinUiArtistRules.LatestEyebrowLine + WinUiArtistRules.LatestTitleLine + WinUiArtistRules.LatestSubLine + 2f * WinUiArtistRules.CopyGap;
        return 2f * WinUiArtistRules.LatestPad + MathF.Max(WinUiArtistRules.LatestCoverEdge, MathF.Max(copy, target));
    }

    /// <summary>The two-column preview's height at <paramref name="w"/> (<see cref="PreviewHost"/>): the header (the title beside the combos), the covers
    /// (a shelf card of one caption line at the cell width <see cref="Cards"/> takes) and the Small "Show all" link, a block gap apart.</summary>
    static float PreviewHeight(float w, float target)
    {
        float header = MathF.Max(LineOf(R.Type.RailHeader(" ")), MathF.Max(WinUiArtistRules.ComboHeight, target));
        float card = SurfaceGeometry.ShelfHeight(PreviewCell(w), 1f, captionLines: 1, metaLine: false);
        var link = ControlMetrics.For(ControlSize.Small).Padding;
        return header + WinUiArtistRules.BlockGap + card + WinUiArtistRules.BlockGap + link.Top + link.Bottom + WinUiArtistRules.PreviewLinkLine;
    }

    /// <summary>The upcoming section's height (its header over the band arm: the 88 cover inside the band's padding).</summary>
    static float UpcomingHeight()
        => LineOf(R.Type.RailHeader(" ")) + WinUiArtistRules.BlockGap + WinUiArtistRules.UpcomingCover + 2f * Spacing.L;

    /// <summary>The compact pick's height (<see cref="WinUiArtistPick.Card"/> without its photograph): the head (the avatar beside the name and the
    /// eyebrow), the quote at <see cref="WinUiArtistRules.PickQuoteSeedLines"/> lines, the foot (the cover beside the title).</summary>
    static float PickHeight()
    {
        float head = Spacing.L + MathF.Max(WinUiArtistRules.PickAvatar, LineOf(BodyStrong(" ")) + LineOf(Caption(" ")));
        float quote = Spacing.S + Spacing.L + WinUiArtistRules.PickQuoteSeedLines * LineOf(R.Type.PickQuote(" "));
        float foot = 2f * Spacing.M + WinUiArtistRules.PickCover;
        return head + quote + foot;
    }

    /// <summary>A text role's line height.</summary>
    static float LineOf(TextEl t) => t.LineHeight > 0f ? t.LineHeight : t.Size * WinUiArtistRules.NameNaturalLine;

    /// <summary>A preview cover's cell width in a <paramref name="w"/>-wide preview: three columns, a preview gap apart.</summary>
    static float PreviewCell(float w)
        => MathF.Max(1f, (w - (WinUiArtistRules.PreviewColumns - 1f) * WinUiArtistRules.PreviewGap) / WinUiArtistRules.PreviewColumns);

    /// <summary>Popular's rows beside the side column (two columns): the chart takes as many rows as the side column's measured height holds, from the
    /// tier's five up to <see cref="WinUiArtistRules.PopularFitMaxRows"/> and the tracks there are, and the pager's pages follow. The grid aligns both
    /// columns at the top, so the chart's own box (never the Popular column, whose height eases through a layout transition and reports its eased
    /// bounds) against the side column's height decides it: a side column a row or more taller than the chart adds rows, one shorter takes them back.
    /// It grows on a whole row of room and shrinks on any overhang, so it holds inside a row's height and never oscillates; Popular never grows past
    /// the side column, so the overview's height (the side column's) never moves with it: the added rows fill the empty area under the chart and
    /// nothing below shifts when the side column's content lands late.
    /// <para>A written count waits for the chart's box to answer it (its height changes by the rows written, which the cap keeps true) before the next
    /// decision, so a side measure in between never counts the same room twice. The fit is owned by the grid host so it outlives a restack, and is let
    /// go (the tier's rows) in any other arrangement.</para></summary>
    sealed class ChartFit
    {
        /// <summary>The rows the chart takes (0: the tier's), written from the bounds callbacks, never from a render.</summary>
        public readonly Signal<int> Rows = new(0);
        public readonly Action<RectF> OnChart, OnSide;
        float _chart = float.NaN, _side = float.NaN, _pitch = 1f, _asked = float.NaN;
        int _base = 5, _max = 5, _seed;
        bool _armed, _frozen;

        public ChartFit()
        {
            OnChart = r =>
            {
                // A write is answered once the chart's height has moved off the height it was written at.
                if (!float.IsNaN(_asked) && MathF.Abs(r.H - _asked) < 0.5f) return;
                _asked = float.NaN;
                _chart = r.H;
                Fit();
            };
            OnSide = r => { _side = r.H; Fit(); };
            Thaw = () => { if (!_frozen) return; _frozen = false; Fit(); };
        }

        /// <summary>Holds the count through a restack: the side column fades out, Popular resizes and the new side column fades in at a preview laid out
        /// at the page's live width, so its height passes through values it does not keep (a fit taken from them grew Popular a row and took it back,
        /// re-mounting the pager's pips). The count the last two-column arrangement settled on stands meanwhile; <see cref="Thaw"/> fits again.</summary>
        public void Freeze() => _frozen = true;

        /// <summary>Ends <see cref="Freeze"/> and fits to the settled measures.</summary>
        public readonly Action Thaw;

        /// <summary>Called by the arrangement's render: whether the fit applies (two columns with a chart), the tier's rows, the cap, a row's pitch and the
        /// predicted rows the chart mounts with (<see cref="SeedRows"/>; 0: the tier's). Leaving the fit forgets the measures (the next arrangement
        /// measures afresh); the chart then reads the tier's rows (its rows prop is null).</summary>
        public void Arm(bool on, int tierRows, int max, float pitch, int seed)
        {
            if (!on && _armed) { _chart = float.NaN; _side = float.NaN; _asked = float.NaN; }
            _armed = on;
            _base = tierRows;
            _max = Math.Max(tierRows, max);
            _pitch = MathF.Max(1f, pitch);
            _seed = seed;
        }

        void Fit()
        {
            if (!_armed || _frozen || !float.IsNaN(_asked) || float.IsNaN(_chart) || float.IsNaN(_side)) return;
            int now = Rows.Peek();
            // 0 is the chart's own count: the seed it mounted with, else the tier's.
            int shown = _seed > 0 ? _seed : _base;
            int cur = now > 0 ? now : shown;
            float room = _side - _chart;
            int next = room >= 0f ? cur + (int)MathF.Floor(room / _pitch) : cur - (int)MathF.Ceiling(-room / _pitch);
            next = Math.Clamp(next, _base, _max);
            int want = next == shown ? 0 : next;
            if (want == now) return;
            _asked = _chart;
            Rows.Value = want;
        }
    }

    /// <summary>The preview's choices (the type and the order), owned by the grid host so they outlive the side column a restack replaces.</summary>
    sealed class PreviewState
    {
        public readonly Signal<int> Facet = new(0);
        public readonly Signal<bool> Oldest = new(false);
    }

    // ── the column rung ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The grid host's props: the arrangement for a column rung, the page inset (the page is the slot plus the inset either side), the page
    /// width the host measured (the slot's seed before the first measure) and the committed column rung (<see cref="WinUiArtistTiers.OverviewRung"/>).
    /// The builder is a live closure (a fresh one rebuilds with the page).</summary>
    sealed record GridProps(Func<int, float, PreviewState, PopularHold, ChartFit, bool, Element> Build, Func<int, float, float> PopularWidth, float Inset, float Seed,
        int Rung);

    /// <summary>The overview's arrangement. The column rung is the policy's content-axis mark, which the core resolves from the content width the shell
    /// computes (the width the page lands at) and commits right before FLIP capture: on a docked pane's open or close that is the slide's first frame,
    /// on a window resize the first frame that is not resized.
    /// <para>The overview is laid out at the page's LIVE width. A docked pane's slide eases the page's width through real layout and the restack commits
    /// on its first frame: the parts' transitions then run against the live layout (the engine re-aims a revealed size and keeps a FLIP's offset over the
    /// re-solve while a sibling reflow resizes the page), so the grid narrows with the page instead of jumping to the landing width and waiting there
    /// while the page caught up with it.</para></summary>
    sealed class GridHost : Component
    {
        readonly Signal<float> _slot = new(0f);   // a version: the width a render uses is _slotNow
        readonly PreviewState _state = new();
        readonly ChartFit _fit = new();
        readonly Action<RectF> _onBounds;
        readonly Signal<int> _held = new(0);   // a version: bumped when Popular's hold below is let go
        readonly Action _releaseHold, _endRestack;
        float _inset, _measured, _slotNow;
        float _hold = float.NaN;   // Popular's old width, held through a widening restack's fade-out (NaN: none)
        bool _holdReleased, _following;   // _following: a restack's rung is ahead of the page's width (the overview follows the live width)
        bool _restacking;   // a restack's resize is running: Popular carries the restack's transition (WinUiArtistMotion.RestackSpanMs)
        bool _landed;   // the first arrangement has rendered (the page's landing): every later side column is a restack's
        int _rung = -1, _holds, _restackRenders, _fitHolds;   // _restackRenders: a version, bumped by every render while a restack runs

        public GridHost()
        {
            _releaseHold = () =>
            {
                if (float.IsNaN(_hold)) return;
                _hold = float.NaN;
                _holdReleased = true;
                _held.Value++;
            };
            _endRestack = () =>
            {
                if (!_restacking) return;
                _restacking = false;
                _held.Value++;
            };
            _onBounds = r =>
            {
                if (!(r.W > 0f)) return;
                _measured = r.W;
                // A width across the column edge from the committed rung waits for the restack: re-rendering the preview at it now (an in-frame rebuild with
                // no FLIP capture) would snap the overview's height a frame or two before the restack animates it. After a restack the rung is the
                // one the page LANDS in, ahead of the page's width (a docked pane's slide): the overview follows the live width until the width
                // reaches the rung, so its columns' targets move with the page (the engine re-aims Popular's resize at each).
                bool crossing = _rung >= 0 && WinUiArtistTiers.OverviewLadder.Resolve(r.W + 2f * _inset, _rung) != _rung;
                if (!crossing) _following = false;
                if ((!crossing || _following) && MathF.Abs(r.W - _slotNow) > 0.5f) { _slotNow = r.W; _slot.Value = r.W; }
            };
        }

        public override Element Render()
        {
            var props = UseProps<GridProps>();
            _inset = props.Inset;
            bool restack = _rung >= 0 && props.Rung != _rung;
            int previousRung = _rung;
            float previousSlot = _slotNow;
            _rung = props.Rung;
            _ = _slot.Value;
            _ = _held.Value;
            if (restack && _measured > 0.5f) { _slotNow = _measured; _following = true; }
            else if (_measured > 0.5f && MathF.Abs(_measured - _slotNow) > 0.5f
                && WinUiArtistTiers.OverviewLadder.Resolve(_measured + 2f * props.Inset, _rung) == _rung) _slotNow = _measured;
            float slot = _slotNow;
            if (!(slot > 0.5f)) slot = MathF.Max(1f, props.Seed - 2f * props.Inset);
            if (restack)
            {
                // A restack that widens Popular holds it at its old width while the side column fades out, then releases it (Popular's summary).
                float from = previousSlot > 0.5f ? props.PopularWidth(previousRung, previousSlot) : float.NaN;
                float to = props.PopularWidth(_rung, slot);
                _holdReleased = false;
                _hold = from + 0.5f < to ? from : float.NaN;
                if (!float.IsNaN(_hold)) _holds++;
                _restacking = true;
                _fit.Freeze();
                _fitHolds++;
            }
            // The restack's transitions must outlive every resize it starts: the release of a widening restack's hold (FadeOutMs after the commit)
            // starts one, and a docking pane's slide re-aims Popular's every frame. So the restack ends a whole span after its LAST render.
            if (_restacking) _restackRenders++;
            UseTimeout(_releaseHold, WinUiArtistMotion.FadeOutMs, DepKey.From(_holds));
            UseTimeout(_endRestack, WinUiArtistMotion.RestackSpanMs, DepKey.From(_restackRenders));
            // Popular's fit waits for the restack to finish (the side column's fade-in at its new slot), then fits to what stands.
            UseTimeout(_fit.Thaw, WinUiArtistMotion.FadeInDelayMs + WinUiArtistMotion.FadeInMs, DepKey.From(_fitHolds));
            // The measured box follows the page, and the overview inside it with it.
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, OnBoundsChanged = _onBounds,
                Children = [new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children = [props.Build(_rung, slot, _state, new PopularHold(_hold, _holdReleased, _restacking), _fit, Landing())],
                }],
            };
        }

        /// <summary>True for the overview's first render (the arrangement the page lands in), false ever after.</summary>
        bool Landing()
        {
            bool landing = !_landed;
            _landed = true;
            return landing;
        }
    }

    // ── the latest release ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The latest release's slot: the card, over its placeholder while its fields are still coming (<paramref name="known"/> false). The slot
    /// has the card's geometry from the first frame and the card fades in over its placeholder in place when its fields land.</summary>
    static Element LatestBlock(Artist a, float target, bool known) => new BoxEl
    {
        Key = "ov:latest", Direction = 1, MinWidth = 0f,
        Children =
        [
            new SkelRegionEl(
                Pending: known ? s_known : s_pending, Failed: s_known, Content: () => LatestCard(a, target, placeholder: false),
                ShimmerSource: () => LatestCard(a, target, placeholder: true), OnFailed: null,
                Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false),
        ],
    };

    static readonly Func<bool> s_known = static () => false, s_pending = static () => true;

    /// <summary>The latest release card (W-Artist): r8 on the card fill, a 64 cover, "Latest release" over the title over "Single · 12 Jun 2026", Play at 32; the card opens the release.
    /// <paramref name="placeholder"/>: the shimmer's source (placeholder words where the release's own are still coming).</summary>
    static Element LatestCard(Artist a, float target, bool placeholder)
    {
        Album al = a.Latest;
        string title = placeholder ? Loc.Get(Strings.Artist.LatestRelease) : al.Title;
        string uri = al.IsValid ? al.Uri.Text : "";
        string? cover = placeholder ? null : Controls.ArtUrl(al.ImageId);
        string date = placeholder ? "" : DiscoCardText.ReleaseDateLabel(al);
        string sub = placeholder ? Loc.Get(Strings.Theme.Winui.Detail.Newest) : Detail.Text.KindLabel(al.Kind) + (date.Length > 0 ? WinUiDetailRules.MetaLead + date : "");
        var album = al;
        var id = al.Id;
        return new BoxEl
        {
            Key = placeholder ? null : "ov:latest:" + uri, Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiArtistRules.LatestGap, MinWidth = 0f,
            Padding = Edges4.All(WinUiArtistRules.LatestPad), Corners = CornerRadius4.All(WinUiDetailRules.CardRadius),
            Fill = Prop.Of(static () => Tok.FillCardDefault), HoverFill = Prop.Of(static () => Tok.FillCardSecondary),
            BorderWidth = 1f, BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered, Cursor = CursorId.Hand,
            OnClick = () => Track.GoToAlbum(album),
            Children =
            [
                new BoxEl
                {
                    Width = WinUiArtistRules.LatestCoverEdge, Height = WinUiArtistRules.LatestCoverEdge, Shrink = 0f,
                    Corners = CornerRadius4.All(WinUiArtistRules.LatestCoverRadius), ClipToBounds = true,
                    Children = [Controls.Artwork(cover, WinUiArtistRules.LatestCoverEdge, WinUiArtistRules.LatestCoverEdge, WinUiArtistRules.LatestCoverRadius, decodePx: WinUiArtistRules.LatestDecode)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = WinUiArtistRules.CopyGap,
                    Children =
                    [
                        Line(Loc.Get(Strings.Artist.LatestRelease), WinUiArtistRules.LatestEyebrow, WinUiArtistRules.LatestEyebrowLine, 400, Tok.TextSecondary),
                        Line(title, WinUiArtistRules.LatestTitle, WinUiArtistRules.LatestTitleLine, 600, Tok.TextPrimary),
                        Line(sub, WinUiArtistRules.LatestSub, WinUiArtistRules.LatestSubLine, 400, Tok.TextSecondary),
                    ],
                },
                Button.Create(Loc.Get(Strings.Artist.Play), () => ArtistVerbs.PlayOrToggle(id), ButtonAppearance.Standard) with { Key = placeholder ? null : "ov:latest:play", Shrink = 0f, Height = target },
            ],
        };
    }

    static TextEl Line(string text, float size, float line, ushort weight, ColorF color) => new(text)
    {
        Size = size, LineHeight = line, Weight = weight, Color = color, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    // ── the discography preview ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The preview's gate: a stamp over the three facets' slots and their rows' versions (a landing release re-renders it).</summary>
    static long PreviewStamp(Artist a)
    {
        var h = new HashCode();
        h.Add(a.Slot);
        h.Add(Artist.FacetPresent(a, DiscoFacet.Albums));
        h.Add(Artist.FacetPresent(a, DiscoFacet.Singles));
        h.Add(Artist.FacetPresent(a, DiscoFacet.Compilations));
        foreach (int s in a.AlbumSlots) { h.Add(s); h.Add(new Album(s).Version); }
        foreach (int s in a.SingleSlots) { h.Add(s); h.Add(new Album(s).Version); }
        foreach (int s in a.CompilationSlots) { h.Add(s); h.Add(new Album(s).Version); }
        return h.ToHashCode();
    }

    sealed record PreviewProps(Artist A, long Stamp, bool Rows, float Target, float Width, PreviewState State)
    {
        public bool Equals(PreviewProps? o) => o is not null && o.A == A && o.Stamp == Stamp && o.Rows == Rows && o.Target == Target && o.Width == Width
            && ReferenceEquals(o.State, State);
        public override int GetHashCode() => HashCode.Combine(A.Slot, Stamp, Rows, Target, Width);
    }

    sealed class PreviewHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<PreviewProps>();
            Signal<int> facetSignal = p.State.Facet;
            Signal<bool> oldestSignal = p.State.Oldest;
            Artist a = p.A;
            int[][] pools = [a.AlbumSlots.ToArray(), a.SingleSlots.ToArray(), a.CompilationSlots.ToArray()];
            DiscoFacet[] facets = [DiscoFacet.Albums, DiscoFacet.Singles, DiscoFacet.Compilations];
            bool oldest = oldestSignal.Value;
            Element order = Combo(p.Target, Loc.Get(oldest ? Strings.Theme.Winui.Detail.Oldest : Strings.Theme.Winui.Detail.Newest),
                [Item(Loc.Get(Strings.Theme.Winui.Detail.Newest), () => oldestSignal.Value = false), Item(Loc.Get(Strings.Theme.Winui.Detail.Oldest), () => oldestSignal.Value = true)],
                "ov:order:" + oldest);

            if (p.Rows)
            {
                var rows = new List<Element>(2);
                bool rowsPending = false;
                for (int f = 0; f < WinUiArtistRules.HugePreviewRows; f++)
                {
                    if (pools[f].Length == 0) continue;
                    rows.Add(Group(Artist.FacetTitle(facets[f]), Cards(pools[f], oldest, p.Width), f));
                    rowsPending |= CardsPending(pools[f], oldest);
                }
                Element more = Link(Loc.Get(Strings.Theme.Winui.Detail.ShowAll), a, DiscoFacet.Albums);
                Element[] groups = rows.ToArray();
                float rowsTarget = p.Target, rowsW = p.Width;
                string rowsOrder = Loc.Get(oldest ? Strings.Theme.Winui.Detail.Oldest : Strings.Theme.Winui.Detail.Newest);
                // Like the two-column preview: over its placeholder while the covers' fields (or the facets themselves) are still coming.
                bool rowsComing = groups.Length == 0 && (Artist.FacetPresent(a, DiscoFacet.Albums) || Artist.FacetPresent(a, DiscoFacet.Singles));
                return Reserved(rowsPending || rowsComing, () => RowsPreview(rowsTarget, order, more, groups),
                    () => RowsPlaceholder(rowsTarget, rowsW, rowsOrder, a));
            }

            // The facets that have releases, in the core's order; the chosen one clamps when the data shrinks. While a facet's releases are still
            // coming the preview stands over its placeholder (the side column has its height when the page lands); with none to come it is gone.
            var present = new List<int>(3);
            bool coming = false;
            for (int f = 0; f < 3; f++)
            {
                if (pools[f].Length > 0) present.Add(f);
                else coming |= Artist.FacetPresent(a, facets[f]);
            }
            if (present.Count == 0 && !coming) return new BoxEl { Key = "ov:preview:none" };
            if (present.Count == 0)
            {
                float cell = PreviewCell(p.Width);
                string orderWord = Loc.Get(oldest ? Strings.Theme.Winui.Detail.Oldest : Strings.Theme.Winui.Detail.Newest);
                return Reserved(true, () => new BoxEl(), () => Preview(p.Target, Combo(p.Target, Artist.FacetTitle(facets[0]), [], null),
                    Combo(p.Target, orderWord, [], null), Placeholders(cell), Link(Loc.Get(Strings.Theme.Winui.Detail.ShowAll), a, DiscoFacet.Albums)));
            }
            int chosen = present.Contains(facetSignal.Value) ? facetSignal.Value : present[0];
            var typeItems = new MenuFlyoutItem[present.Count];
            for (int i = 0; i < typeItems.Length; i++)
            {
                int f = present[i];
                typeItems[i] = Item(Artist.FacetTitle(facets[f]), () => facetSignal.Value = f);
            }
            Element type = Combo(p.Target, Artist.FacetTitle(facets[chosen]), typeItems, "ov:type:" + chosen);
            int total = Artist.FacetTotal(a, facets[chosen]);
            Element all = Link(Strings.Theme.Winui.Detail.ShowAllAlbums(count: Math.Max(total, pools[chosen].Length)), a, facets[chosen]);
            int[] shown = pools[chosen];
            float w = p.Width;
            // The covers' own fields (an answer per release, often after the overview's): the preview stands over its placeholder until they land.
            bool pending = CardsPending(shown, oldest);
            float target = p.Target;
            // The shimmer's combos are the same words, unkeyed (the placeholder never shares a key with the content it stands for).
            string typeWord = Artist.FacetTitle(facets[chosen]), orderLabel = Loc.Get(oldest ? Strings.Theme.Winui.Detail.Oldest : Strings.Theme.Winui.Detail.Newest);
            return Reserved(pending, () => Preview(target, type, order, Cards(shown, oldest, w), all),
                () => Preview(target, Combo(target, typeWord, [], null), Combo(target, orderLabel, [], null), Placeholders(PreviewCell(w)), all));
        }

        /// <summary>The Huge preview's tree (W-Artist-Huge): the Discography header with the order combo and "Show all", over the titled rows.</summary>
        static Element RowsPreview(float target, Element order, Element more, Element[] groups) => new BoxEl
        {
            Direction = 1, Gap = WinUiArtistRules.BlockGap, MinWidth = 0f,
            Children =
            [
                // The combo's slot holds its height (the placeholder derives it as a bar of its own size).
                Header(Loc.Get(Strings.Theme.Winui.Detail.Discography),
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MinHeight = MathF.Max(WinUiArtistRules.ComboHeight, target), Children = [order] }, more),
                new BoxEl { Direction = 1, Gap = WinUiArtistRules.BlockGap, MinWidth = 0f, Children = groups },
            ],
        };

        /// <summary>The Huge preview's shimmer source at <paramref name="w"/>: its header and two titled rows over placeholder covers, unkeyed.</summary>
        internal static Element RowsPlaceholder(float target, float w, string orderWord, Artist a)
        {
            float cell = PreviewCell(w);
            DiscoFacet[] facets = [DiscoFacet.Albums, DiscoFacet.Singles];
            var groups = new Element[WinUiArtistRules.HugePreviewRows];
            for (int f = 0; f < groups.Length; f++) groups[f] = Group(Artist.FacetTitle(facets[f]), Placeholders(cell), f) with { Key = null };
            return RowsPreview(target, Combo(target, orderWord, [], null), Link(Loc.Get(Strings.Theme.Winui.Detail.ShowAll), a, DiscoFacet.Albums), groups);
        }

        /// <summary>The preview's tree: the Discography header beside the type and order combos, the covers, the link. The combos' row holds their
        /// height (a placeholder derives a component as one bar of its own size, which stood the row 4 short of the combos).</summary>
        internal static Element Preview(float target, Element type, Element order, Element cards, Element all) => new BoxEl
        {
            Direction = 1, Gap = WinUiArtistRules.BlockGap, MinWidth = 0f,
            Children =
            [
                Header(Loc.Get(Strings.Theme.Winui.Detail.Discography), new BoxEl
                {
                    Direction = 0, Gap = WinUiArtistRules.ActionsGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [type, order],
                    MinHeight = MathF.Max(WinUiArtistRules.ComboHeight, target),
                }, null),
                cards,
                new BoxEl { Direction = 0, Children = [all] },
            ],
        };

        /// <summary>The preview over its placeholder while <paramref name="pending"/>: one region in every state, so the landing fades the covers in over
        /// their placeholders in place.</summary>
        static Element Reserved(bool pending, Func<Element> content, Func<Element> shimmer) => new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children =
            [
                new SkelRegionEl(
                    Pending: pending ? s_pending : s_known, Failed: s_known, Content: content, ShimmerSource: shimmer, OnFailed: null,
                    Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false),
            ],
        };

        /// <summary>Whether any of the first covers the preview shows still lacks its card fields.</summary>
        static bool CardsPending(int[] slots, bool oldest)
        {
            int count = Math.Min(slots.Length, (int)WinUiArtistRules.PreviewColumns);
            for (int i = 0; i < count; i++)
                if (!new Album(slots[oldest ? slots.Length - 1 - i : i]).Knows(AlbumFields.DiscoCard)) return true;
            return false;
        }

        /// <summary>Three placeholder covers at <paramref name="cell"/>, the covers' shimmer source.</summary>
        internal static Element Placeholders(float cell)
        {
            var kids = new Element[(int)WinUiArtistRules.PreviewColumns];
            for (int i = 0; i < kids.Length; i++) kids[i] = WinUiArtistSections.AlbumCardPlaceholder(cell);
            return new BoxEl { Direction = 0, Gap = WinUiArtistRules.PreviewGap, MinWidth = 0f, Children = kids };
        }

        static MenuFlyoutItem Item(string label, Action invoke) => new(label, default, true, invoke);

        /// <summary>A combo: the current word and a chevron on the control fill; the menu is the choices. Keyed on the word (the label freezes at mount).</summary>
        internal static Element Combo(float target, string label, IReadOnlyList<MenuFlyoutItem> items, string? key)
            => Embed.Comp(() => new DropDownButton
            {
                Label = label, Items = items,
                Parts = new TemplateParts { [DropDownButton.PartRoot] = b => target > WinUiDetailRules.Target ? b with { Height = target } : b },
            }) with { Key = key };

        static Element Header(string title, Element lead, Element? trailing) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Children = trailing is null
                ? [R.Type.RailHeader(title) with { Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, lead]
                : [R.Type.RailHeader(title) with { Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, lead, trailing.Value],
        };

        static Element Group(string title, Element cards, int index) => new BoxEl
        {
            Key = "ov:group:" + index.ToString(CultureInfo.InvariantCulture), Direction = 1, Gap = WinUiArtistRules.BlockGap, MinWidth = 0f,
            Children =
            [
                new TextEl(title) { Size = WinUiArtistRules.GroupTitle, LineHeight = WinUiArtistRules.GroupTitleLine, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1 },
                cards,
            ],
        };

        internal static Element Link(string text, Artist a, DiscoFacet facet)
        {
            string key = DiscoRoute.Key(facet, a.Uri.Text);
            return HyperlinkButton.Create(text, () => ArtistVerbs.OpenRoute(key), size: ControlSize.Small);
        }

        /// <summary>Three covers in equal columns of the preview's width <paramref name="w"/>, 16 apart: the shared media surface (cover, title, "2014 · Album").</summary>
        static Element Cards(int[] slots, bool oldest, float w)
        {
            int count = Math.Min(slots.Length, (int)WinUiArtistRules.PreviewColumns);
            float gap = WinUiArtistRules.PreviewGap;
            float cell = PreviewCell(w);
            var kids = new Element[(int)WinUiArtistRules.PreviewColumns];
            for (int i = 0; i < kids.Length; i++)
            {
                int at = oldest ? slots.Length - 1 - i : i;
                kids[i] = i < count ? WinUiArtistSections.AlbumCardAt(slots[at], cell) : new BoxEl { Width = cell, Shrink = 0f, HitTestVisible = false };
            }
            return new BoxEl { Direction = 0, Gap = gap, MinWidth = 0f, Children = kids };
        }
    }
}
