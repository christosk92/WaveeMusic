// ── Themes/WinUi/Structure/Lists/WinUiArtistChart.cs — WinUI's artist Top-tracks chart (themes plan 6.2; P6 W3) ───────────────────────────
//
// The picture of the artist page's Popular chart (W-Artist, W-Artist-Compact / -Small / -Huge / -Huge4K). `StyleKeys.ArtistChart` carries the templates the core
// chart host calls while it renders; the host keeps the readiness gate, the asks, the items and their keys, the selection model, the drag payload, the menu, the
// play-from-here queue, the per-row memo that is a row's render gate, the tier decision and the pager's controlled-pips state
// (see `Theming\Contracts\ArtistChartStyle.cs`).
//
// THE ROW. Shared by live rows and the skeleton so the two cannot drift. A 60-tall row with a 40 cover (W-Artist: rank, cover, title over "214,018,301 plays", the
// heart, the time), rows touching, the full-width r4 plate on hover only and NO resting fill (now-playing is content: the bars and the accent text). With the
// artwork off (`artworkInRows`) the row is the one-line 48. The # cell, the heart and the explicit mark are `WinUiRowCells`'.
//
// RANK AND PLAYS AT EVERY WIDTH. The tier (`ArtistPopularLayout.Tier`: the art edge, whether the duration shows, whether the sub line stacks) is the core's decision
// per cell width, with hysteresis; this file only draws it: the rank is the # cell (`Index + 1`), the compact play count rides the sub line (never squeezing the
// credit: the exact count is its tooltip) and, in a stacking tier with a credit AND a count, the exact count takes its own third line.
//
// Numbers are `Rules\WinUiArtistChartLayout.cs` (WVTH003 allows no literal look here).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

using ChartCells = global::Wavee.Theming.Contracts.ArtistChartCells;

/// <summary>WinUI's style for the artist chart.</summary>
internal static class WinUiArtistChart
{
    /// <summary>The style WinUI registers under <c>StyleKeys.ArtistChart</c>. Stateless: every host and a test share it.</summary>
    public static readonly ArtistChartStyle Style = new(Metrics, Frame, Shelf, Bar, Header, Row, Cell, Skeleton, Failed, Pager);

    /// <summary>Rows are 60 tall touching each other (the one-line skin 48); the 264 card minimum is a BREAKPOINT (two columns when the chart column is
    /// 540 wide), and the maximum is uncapped on purpose.</summary>
    static ArtistChartMetrics Metrics(bool classic)
        => new(classic ? WinUiArtistChartLayout.OneLineRowHeight : WinUiArtistChartLayout.RowHeight,
               WinUiArtistChartLayout.CellGap, WinUiArtistChartLayout.HeaderGap,
               WinUiArtistChartLayout.MinCardWidth, WinUiArtistChartLayout.EdgeFade);

    /// <summary>The chart's frame. The standard selection bar floats over the chart's own rows the moment the selection earns it
    /// (SelectionBarRules.Visible); it is its own component, so a select/clear re-renders the bar and the bound row chrome, never the host or
    /// the shelf. The page waits for the chart (`ArtistReadiness.BodyReady`), so this inner region joins no page-level skeleton group (that
    /// coordination faded the hero from 0 after the photo was already decoded).</summary>
    static Element Frame(ArtistChartFrameArgs a) => new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Children =
        [
            ZStack(
                new SkelRegionEl(
                    Pending: a.Pending, Failed: a.Failed, Content: a.Content, ShimmerSource: a.Shimmer,
                    OnFailed: a.OnFailed, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default,
                    Group: null, SmoothResize: false),
                a.Bar),
        ],
    };

    /// <summary>A key on a component's ROOT is inert (ReconcileSingleChild pairs by type alone), so the keyed shelf is a CHILD of a pass-through
    /// column (ch 08 §9 trap 2). The column is the chart's one height: a crossing that changes the rows per column (Huge 4K's four against five)
    /// re-fits the same shelf, and the column glides to the new height with everything below riding it (<see cref="WinUiArtistChartLayout.Resize"/>).</summary>
    static Element Shelf(Element keyedShelf) => new BoxEl { Direction = 1, MinWidth = 0f, Layout = WinUiArtistChartLayout.Resize, Children = [keyedShelf] };

    /// <summary>The standard selection bar, standalone, 8 up from the chart's bottom.</summary>
    static Element Bar(int count, Func<int, Element> lane) => Controls.SelectionBar(count, lane, standalone: true, bottomPadding: Spacing.S);

    /// <summary>The chart's title (W-Artist): "Popular" in Subtitle 20/28, no accent rule (the pager rides the shelf's header row beside it). The core's own title ("Top tracks") is
    /// the Classic word; WinUI says what the board says.</summary>
    static Element Header(string title, ColorF accent) => R.Type.RailHeader(Loc.Get(Strings.Artist.Popular)) with { MaxLines = 1, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis };

    static Element Row(ArtistChartRowArgs a)
    {
        Element? feat = FeatLine(a.Track, a.PageArtist, a.FeatCount, a.FeatSlot);
        return ChartRowView(a.Cells, a.Index, a.State, a.Tier, feat, a.OnPlay, a.OnLike, a.Pop, a.Hovered, a.Slot,
                            a.ChecksVisible, a.ShowArtwork, a.Classic);
    }

    /// <summary>One realized cell: the row content over the single-selection pill (accent, 3 x 16, the check lane's checkbox says it instead
    /// while the lane is down) with the cell's drag. The pill is an always-mounted BOUND opacity (selecting is compositor-only).</summary>
    static BoxEl Cell(ArtistChartCellArgs a) => new()
    {
        ZStack = true,
        // A vertical lift wins the drag; a horizontal sweep yields to the shelf's pan (the engine's arena).
        Draggable = a.Draggable,
        Children =
        [
            a.Content,
            new BoxEl
            {
                Key = "row-pill", Width = WinUiArtistChartLayout.PillWidth, Height = WinUiArtistChartLayout.PillHeight,
                Corners = CornerRadius4.All(WinUiArtistChartLayout.PillRadius),
                AlignSelf = FlexAlign.Center, Fill = a.PillFill, HitTestVisible = false,
                // The accent pill is the single-selection cue; with the check lane up the checkbox says it instead.
                Opacity = a.PillOpacity,
            },
        ],
    };

    /// <summary>The failed chart: the header over the Error vacancy and its Retry (the rows that were there keep rendering until the answers
    /// replace them).</summary>
    static Element Failed(string title, ColorF accent, Action retry) => new BoxEl
    {
        Direction = 1, Gap = WinUiArtistChartLayout.HeaderGap,
        Children =
        [
            Controls.AccentHeader(title, accent),
            Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: retry),
        ],
    };

    /// <summary>The chart's derived-skeleton source: the SAME row builder over a representative shape, at the seed's column count (up to ten
    /// charted is 2 x 5; an empty edge assumes the overview's ten, W5).</summary>
    static Element Skeleton(ArtistChartSkeletonArgs s)
    {
        bool classic = s.Classic;
        int cols = s.Columns;
        var tier = classic ? ArtistPopularLayout.Tier.Classic(true) : new ArtistPopularLayout.Tier(WinUiArtistChartLayout.SkeletonArt, true, false);
        var cells = new ChartCells("Top track title", null, "3:30", "1.2M", "1,234,567", false, false);
        var state = default(Track.RowState);
        var columns = new Element[cols];
        for (int c = 0; c < cols; c++)
        {
            int rows = Math.Min(s.PerColumn, Math.Max(0, s.Total - c * s.PerColumn));
            var kids = new Element[rows];
            for (int r = 0; r < rows; r++)
                kids[r] = ChartRowView(cells, c * s.PerColumn + r, in state, tier, null, null, null, false, null, null, null,
                                       s.ShowArtwork, classic);
            columns[c] = new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = WinUiArtistChartLayout.CellGap, Children = kids,
            };
        }
        return new BoxEl
        {
            Direction = 1, Gap = WinUiArtistChartLayout.HeaderGap,
            Children =
            [
                // The header at its NATURAL width, so the derived bar is title-wide in both trees.
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [Controls.AccentHeader(Loc.Get(Strings.Artist.TopTracks), Tok.AccentDefault)],
                },
                new BoxEl { Direction = 0, Gap = WinUiArtistChartLayout.CellGap, Children = columns },
            ],
        };
    }

    /// <summary>The pager: previous, the pips and next at two or more pages; the bare track count at one (W11). The pips are a CONTROLLED pager
    /// mirrored from the shelf's page through the core's effect; onChange AND onReselect both call GoTo: after a partial pan the strip rests
    /// between pages and the re-click is the request to be put back on the boundary.</summary>
    static Element Pager(ArtistChartPagerArgs p)
    {
        if (p.PageCount <= 1)
            return Ui.Caption(p.Total.ToString(System.Globalization.CultureInfo.CurrentCulture))
                with { Weight = 600, Color = Tok.TextTertiary };
        return new BoxEl
        {
            Direction = 0, Gap = Wavee.Theming.TouchTargetPolicy.PagerGap(p.Window, Spacing.XS), AlignItems = FlexAlign.Center,
            Children =
            [
                ChartChevron(Icons.ChevronLeft, p.CanPrev, p.Prev),
                PipsPager.Create(p.PageCount, p.Selected, onChange: p.GoTo, onReselect: p.GoTo),
                ChartChevron(Icons.ChevronRight, p.CanNext, p.Next),
            ],
        };
    }

    /// <summary>The prototype row, shared by live rows and the skeleton so the two cannot drift. 60 tall (the one-line skin 48), the full-width r4 plate
    /// on hover only, NO resting fill: now-playing is content (the bars and the accent title).</summary>
    static Element ChartRowView(in ChartCells c, int index, in Track.RowState st, ArtistPopularLayout.Tier tier,
                                Element? feat, Action? onPlay, Action? onLike, bool pop, Signal<bool>? hovered,
                                RowScope? slot, Func<bool>? checksVisible, bool showArtwork, bool classic)
    {
        ColorF? nowInk = st.IsNow ? Tok.AccentTextPrimary : null;
        bool hasPlays = c.PlaysCompact.Length > 0;
        // The third line needs ALL THREE: a stacking tier, a feat credit and a play count (ch 08 audit #17).
        bool stacked = tier.StackSub && feat is not null && hasPlays;
        bool playsInSub = hasPlays && !stacked;

        int parts = (c.Explicit ? 1 : 0) + (c.Video ? 1 : 0) + (feat is not null ? 1 : 0) + (playsInSub ? 1 : 0);
        var sub = parts == 0 ? Array.Empty<Element>() : new Element[parts * 2 - 1];
        int n = 0;
        if (c.Explicit) sub[n++] = WinUiRowCells.ExplicitMark();
        if (c.Video)
        {
            if (n > 0) sub[n++] = ChartDot(nowInk);
            sub[n++] = Icon(Icons.Movie, 13f, nowInk ?? Tok.TextTertiary);
        }
        if (feat is not null)
        {
            if (n > 0) sub[n++] = ChartDot(nowInk);
            sub[n++] = feat;
        }
        if (playsInSub)
        {
            if (n > 0) sub[n++] = ChartDot(nowInk);
            // ONE format: compact in the row, the exact count in the tooltip — never squeezing the feat credit (§9 #10).
            sub[n++] = ToolTip.Wrap(Ui.Caption(c.PlaysCompact) with
            {
                Color = nowInk ?? Tok.TextSecondary, MaxLines = 1, Shrink = 0f,
            }, c.PlaysExact);
        }

        var title = R.Type.TrackTitle(c.Title) with
        {
            Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        Element subLine = sub.Length > 0
            ? new BoxEl { Direction = 0, Gap = WinUiArtistChartLayout.SubGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = sub }
            : new BoxEl();
        Element[] mid = stacked
            // The stacked line is the same ONE format as the sub line's: compact, the exact count in its tooltip (the exact count trimmed at a narrow
            // row; Zune's B2z fix, audit B6b).
            ? [title, subLine, ToolTip.Wrap(Ui.Caption(c.PlaysCompact) with { Color = Tok.TextSecondary, MaxLines = 1, Shrink = 0f }, c.PlaysExact)]
            : [title, subLine];

        var trail = new Element[tier.ShowDuration ? 2 : 1];
        // The one row heart (tuned Classic L2): a liked track's filled heart at rest, the outline on row hover or while the
        // chart's slot holds the focus. The row re-renders on a like (the pop edge), so the saved value is current here.
        bool saved = st.Saved;
        var focus = slot?.IsFocused;
        trail[0] = WinUiRowCells.HeartReveal(WinUiRowCells.Heart(saved, onLike, pop, st.IsNow),
            () => saved || (focus?.Value ?? false));
        if (tier.ShowDuration)
            trail[1] = R.Type.DenseTitle(c.Duration) with { Color = nowInk ?? Tok.TextSecondary };

        var rowChildren = new Element[showArtwork ? 4 : 3];
        int child = 0;
        rowChildren[child++] = new BoxEl
        {
            Width = WinUiArtistChartLayout.RankBox, Height = WinUiArtistChartLayout.RankBox, Shrink = 0f,
            Children = [WinUiRowCells.NumberCell(index, in st, onPlay, hovered, chartStatus: 0)],
        };
        // TRACK ARTWORK HIDDEN: the cell is not built at all (rowChildren is 3 long), the mid column takes the width.
        if (showArtwork)
            rowChildren[child++] = new BoxEl
            {
                Width = tier.Art, Height = tier.Art, Shrink = 0f,
                Children = [Controls.Artwork(c.ArtUrl, tier.Art, tier.Art, R.Shape.Control, decodePx: 64)],
            };
        rowChildren[child++] = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Gap = WinUiArtistChartLayout.CopyGap, Justify = FlexJustify.Center,
            Children = mid,
        };
        rowChildren[child] = new BoxEl { Direction = 0, Gap = WinUiArtistChartLayout.TrailGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = trail };

        float rowHeight = classic ? WinUiArtistChartLayout.OneLineRowHeight : WinUiArtistChartLayout.RowHeight;
        var inset = new Edges4(WinUiArtistChartLayout.RowInset, 0f, WinUiArtistChartLayout.RowInset, 0f);
        // Slot-hosted rows carry the multi-select check lane in front of the cells; it slides in with the selection.
        BoxEl body = slot is { } scope && checksVisible is not null
            ? new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, MinHeight = rowHeight, AlignItems = FlexAlign.Center, Padding = inset,
                Children =
                [
                    SelectorVisualsBound.BoundCheckLane(checksVisible, scope.IsSelected, scope.OnInteraction, leftMargin: 0f),
                    new BoxEl
                    {
                        Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                        Animate = WinUiArtistChartLayout.CheckShift, Children = rowChildren,
                    },
                ],
            }
            : new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, MinHeight = rowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Padding = inset, Children = rowChildren,
            };

        return new BoxEl
        {
            ZStack = true, MinHeight = rowHeight, MinWidth = 0f, ClipToBounds = true,
            Corners = CornerRadius4.All(WinUiListRules.RowRadius),
            Fill = ColorF.Transparent,
            HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            Role = AutomationRole.Button,
            // Slot-hosted (the live chart): the shelf's slot root owns click, focus and keys, so this content is click-less.
            // The skeleton (no slot) is inert.
            OnClick = null,
            // Real hover edges: the PointerBit every HoverOpacity descendant inherits AND the equalizer's pause gate.
            OnHoverMove = hovered is null ? null : _ => { if (!hovered.Peek()) hovered.Value = true; },
            OnPointerExit = hovered is null ? null : () => { if (hovered.Peek()) hovered.Value = false; },
            Children = [body],
        };
    }

    static Element ChartDot(ColorF? ink) => Ui.Caption("·") with { Color = ink ?? Tok.TextTertiary, Shrink = 0f };

    /// <summary>"feat. X +N": the first featured name is a link; "+N" opens the credit flyout of the rest. The featured
    /// list is built only on the +N branch (§9 trap b).</summary>
    static Element? FeatLine(Track t, int pageArtist, int featCount, int firstSlot)
    {
        if (featCount == 0 || firstSlot <= 0) return null;
        var first = new Artist(firstSlot);
        var kids = new Element[featCount > 1 ? 3 : 2];
        kids[0] = Ui.Caption(Loc.Get(Strings.Artist.Feat)) with { Color = Tok.TextTertiary, Shrink = 0f };
        kids[1] = new SpanTextEl([new TextSpan(first.Name, OnClick: () => Track.GoToArtist(first))])
        {
            Size = Ui.Caption("").Size, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
        };
        if (featCount > 1)
        {
            var credits = t.ArtistSlots;
            Span<int> featured = featCount <= 32 ? stackalloc int[featCount] : new int[featCount];
            int k = 0;
            for (int i = 0; i < credits.Length && k < featured.Length; i++)
                if (credits[i] != pageArtist) featured[k++] = credits[i];
            kids[2] = Track.ArtistMoreButton(featured[..k], 1) with { Key = "featmore:" + firstSlot };
        }
        return new BoxEl { Direction = 0, Gap = WinUiArtistChartLayout.FeatGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f, Children = kids };
    }

    /// <summary>The chart's own 28-DIP transparent chevron (not the stock 32 filled circle); a disabled one has no hover
    /// fill and a tertiary glyph.</summary>
    /// <summary>A pager chevron; its touch slop reaches left (<see cref="TouchTargetPolicy.OneSide"/>): outward for the previous one, into the gap for the next
    /// one, whose right side is the page's edge (a scroll bar's lane takes the side past it).</summary>
    static Element ChartChevron(string glyph, bool enabled, Action onClick) => new BoxEl
    {
        HitSlop = Wavee.Theming.TouchTargetPolicy.OneSide(WinUiArtistChartLayout.ChevronBox, left: true),
        Width = WinUiArtistChartLayout.ChevronBox, Height = WinUiArtistChartLayout.ChevronBox, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(WinUiListRules.RowRadius), HoverFill = enabled ? Tok.FillSubtleSecondary : ColorF.Transparent,
        HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), OnClick = enabled ? onClick : null,
        Role = AutomationRole.Button, Cursor = enabled ? CursorId.Hand : null,
        Children = [Icon(glyph, 12f, enabled ? Tok.TextSecondary : Tok.TextTertiary)],
    };
}
