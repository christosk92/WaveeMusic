// ── Themes/Classic/Structure/Lists/ClassicArtistChart.cs — Classic's artist Top-tracks chart (themes plan 6.1, T8c) ─────────────
//
// From `Entities/Artist.UI.Chart.cs` (the prototype row `ChartRowView`, the credit line, the pager's chevron, the skeleton source, the
// header and the failed panel, and the cell's selection pill; T10b2 added the host's frame, the shelf's holder and the selection bar): the
// picture of the artist page's Top-tracks chart. `StyleKeys.ArtistChart`
// carries the templates the core chart host calls while it renders; the host keeps the readiness gate, the asks, the items and their
// keys, the selection model, the drag payload, the menu, the play-from-here queue, the per-row memo that is a row's render gate, the
// tier decision and the pager's controlled-pips state (see `Theming\Contracts\ArtistChartStyle.cs`).
//
// THE ROW. Shared by live rows and the skeleton so the two cannot drift (ArtistPopular.cs:365-522). Modern: 56 tall, r6, a 1-DIP
// transparent border that paints on hover, NO resting fill (now-playing is content: the equalizer and the accent title). Classic: 48
// tall, square, a hairline per row, and the now-playing accent spreads to the explicit mark, the dots, the video glyph, the plays and
// the duration (W10, parity 83). The # cell, the heart and the Classic explicit word-mark are `ClassicRowCells`'.
//
// RANK AND PLAYS AT EVERY WIDTH. The tier (`ArtistPopularLayout.Tier`: the art edge, whether the duration shows, whether the sub line
// stacks) is the core's decision per cell width, with hysteresis; this file only draws it: the rank is the # cell (`Index + 1`), the
// compact play count rides the sub line (never squeezing the credit: the exact count is its tooltip) and, in a stacking tier with a
// credit AND a count, the exact count takes its own third line.
//
// Numbers are `Rules\ClassicArtistChartLayout.cs` (WVTH003 allows no literal look here).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

using ChartCells = global::Wavee.Theming.Contracts.ArtistChartCells;

/// <summary>Classic's style for the artist chart.</summary>
internal static class ClassicArtistChart
{
    /// <summary>The style Classic registers under <c>StyleKeys.ArtistChart</c>. Stateless: every host and a test share it.</summary>
    public static readonly ArtistChartStyle Style = new(Metrics, Frame, Shelf, Bar, Header, Row, Cell, Skeleton, Failed, Pager);

    /// <summary>Modern rows are 56 tall on a 12 gap, Classic's 48 on 8; the 264 card minimum is a BREAKPOINT (two columns when the chart column is
    /// 540 wide), and the maximum is uncapped on purpose.</summary>
    static ArtistChartMetrics Metrics(bool classic)
        => new(classic ? ClassicArtistChartLayout.ClassicRowHeight : ClassicArtistChartLayout.RowHeight,
               classic ? Spacing.S : ClassicArtistChartLayout.CellGap,
               classic ? Spacing.S : ClassicArtistChartLayout.HeaderGap,
               ClassicArtistChartLayout.MinCardWidth, ClassicArtistChartLayout.EdgeFade);

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
    /// re-fits the same shelf, and the column glides to the new height with everything below riding it (<see cref="ClassicTierMotion.Resize"/>).</summary>
    static Element Shelf(Element keyedShelf) => new BoxEl { Direction = 1, MinWidth = 0f, Layout = ClassicTierMotion.Resize, Children = [keyedShelf] };

    /// <summary>The standard selection bar, standalone, 8 up from the chart's bottom.</summary>
    static Element Bar(int count, Func<int, Element> lane) => Controls.SelectionBar(count, lane, standalone: true, bottomPadding: Spacing.S);

    static Element Header(string title, ColorF accent) => Controls.AccentHeader(title, accent);

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
                Key = "row-pill", Width = ClassicArtistChartLayout.PillWidth, Height = ClassicArtistChartLayout.PillHeight,
                Margin = new Edges4(ClassicArtistChartLayout.PillLead, 0f, 0f, 0f),
                Corners = a.Classic ? CornerRadius4.All(Radii.None) : CornerRadius4.All(ClassicArtistChartLayout.PillRadius),
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
        Direction = 1, Gap = ClassicArtistChartLayout.HeaderGap,
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
        var tier = classic ? ArtistPopularLayout.Tier.Classic(true) : new ArtistPopularLayout.Tier(ClassicArtistChartLayout.SkeletonArt, true, false);
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
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = classic ? Spacing.S : ClassicArtistChartLayout.CellGap, Children = kids,
            };
        }
        return new BoxEl
        {
            Direction = 1, Gap = classic ? Spacing.S : ClassicArtistChartLayout.HeaderGap,
            Children =
            [
                // The header at its NATURAL width, so the derived bar is title-wide in both trees.
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [Controls.AccentHeader(Loc.Get(Strings.Artist.TopTracks), Tok.AccentDefault)],
                },
                new BoxEl { Direction = 0, Gap = classic ? Spacing.S : ClassicArtistChartLayout.CellGap, Children = columns },
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

    /// <summary>The prototype row (ArtistPopular.cs:365-522), shared by live rows and the skeleton so the two cannot
    /// drift. Modern: 56 tall, r6, a 1-DIP transparent border that paints on hover, NO resting fill — now-playing is
    /// content (the equalizer + the accent title). Classic: 48 tall, square, a hairline per row, and the now-playing
    /// accent spreads to the explicit mark, the dots, the video glyph, the plays and the duration (W10, parity 83).</summary>
    static Element ChartRowView(in ChartCells c, int index, in Track.RowState st, ArtistPopularLayout.Tier tier,
                                Element? feat, Action? onPlay, Action? onLike, bool pop, Signal<bool>? hovered,
                                RowScope? slot, Func<bool>? checksVisible, bool showArtwork, bool classic)
    {
        bool classicNow = classic && st.IsNow;
        ColorF? nowInk = classicNow ? Tok.AccentTextPrimary : null;
        bool hasPlays = c.PlaysCompact.Length > 0;
        // The third line needs ALL THREE: a stacking tier, a feat credit and a play count (ch 08 audit #17).
        bool stacked = tier.StackSub && feat is not null && hasPlays;
        bool playsInSub = hasPlays && !stacked;

        int parts = (c.Explicit ? 1 : 0) + (c.Video ? 1 : 0) + (feat is not null ? 1 : 0) + (playsInSub ? 1 : 0);
        var sub = parts == 0 ? Array.Empty<Element>() : new Element[parts * 2 - 1];
        int n = 0;
        if (c.Explicit) sub[n++] = classic ? ClassicRowCells.ClassicExplicitBadge(nowInk) : Controls.ExplicitBadge(14f);
        if (c.Video)
        {
            if (n > 0) sub[n++] = ChartDot(nowInk);
            sub[n++] = Icon(Icons.Movie, 13f, nowInk ?? Tok.TextTertiary);
        }
        if (feat is not null)
        {
            if (n > 0) sub[n++] = ChartDot(nowInk);
            sub[n++] = feat.Value;
        }
        if (playsInSub)
        {
            if (n > 0) sub[n++] = ChartDot(nowInk);
            // ONE format: compact in the row, the exact count in the tooltip — never squeezing the feat credit (§9 #10).
            sub[n++] = ToolTip.Wrap(Ui.Caption(c.PlaysCompact) with
            {
                Color = nowInk ?? Tok.TextTertiary, MaxLines = 1, Shrink = 0f,
            }, c.PlaysExact);
        }

        var title = new TextEl(c.Title)
        {
            Size = ClassicArtistChartLayout.TitleSize, Weight = 600, Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        Element subLine = sub.Length > 0
            ? new BoxEl { Direction = 0, Gap = ClassicArtistChartLayout.SubGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = sub }
            : new BoxEl();
        Element[] mid = stacked
            // The stacked line is the same ONE format as the sub line's: compact, the exact count in its tooltip (the exact count trimmed at a narrow
            // row; Zune's B2z fix, audit B6b).
            ? [title, subLine, ToolTip.Wrap(Ui.Caption(c.PlaysCompact) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f }, c.PlaysExact)]
            : [title, subLine];

        var trail = new Element[tier.ShowDuration ? 2 : 1];
        // The one row heart, painted at rest (filled when liked, the outline when not). The row re-renders on a like (the pop
        // edge), so the saved value is current here.
        trail[0] = ClassicRowCells.HeartLane(ClassicRowCells.Heart(st.Saved, onLike, pop, classic));
        if (tier.ShowDuration)
            trail[1] = R.Type.DenseMeta(c.Duration) with { Color = nowInk ?? Tok.TextSecondary };

        var rowChildren = new Element[showArtwork ? 4 : 3];
        int child = 0;
        rowChildren[child++] = new BoxEl
        {
            Width = ClassicArtistChartLayout.RankBox, Height = ClassicArtistChartLayout.RankBox, Shrink = 0f,
            Children = [ClassicRowCells.NumberCell(index, in st, onPlay, hovered, chartStatus: 0, classic: classic)],
        };
        // TRACK ARTWORK HIDDEN: the cell is not built at all (rowChildren is 3 long), the mid column takes the width.
        if (showArtwork)
            rowChildren[child++] = new BoxEl
            {
                Width = tier.Art, Height = tier.Art, Shrink = 0f,
                Children = [Controls.Artwork(c.ArtUrl, tier.Art, tier.Art, Radii.Control, decodePx: 64)],
            };
        rowChildren[child++] = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Gap = classic ? Spacing.XS : 1f, Justify = FlexJustify.Center,
            Children = mid,
        };
        rowChildren[child] = new BoxEl { Direction = 0, Gap = ClassicArtistChartLayout.TrailGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = trail };

        float rowHeight = classic ? ClassicArtistChartLayout.ClassicRowHeight : ClassicArtistChartLayout.RowHeight;
        var inset = classic ? new Edges4(Spacing.XS, 0f, Spacing.XS, 0f) : new Edges4(Spacing.S, 0f, Spacing.S, 0f);
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
                        Animate = ClassicArtistChartLayout.CheckShift, Children = rowChildren,
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
            ZStack = true, MinHeight = rowHeight, MinWidth = 0f, ClipToBounds = classic,
            Corners = classic ? CornerRadius4.All(Radii.None) : CornerRadius4.All(6f),
            Fill = ColorF.Transparent,
            HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            PressScale = R.Motion.ScaleSubtle.Press,
            BorderWidth = classic ? 0f : 1f, BorderColor = ColorF.Transparent,
            HoverBorderColor = classic ? ColorF.Transparent : Tok.StrokeCardDefault,
            Role = AutomationRole.Button,
            // Slot-hosted (the live chart): the shelf's slot root owns click, focus and keys, so this content is click-less.
            // The skeleton (no slot) is inert.
            OnClick = null,
            // Real hover edges: the PointerBit every HoverOpacity descendant inherits AND the equalizer's pause gate.
            OnHoverMove = hovered is null ? null : _ => { if (!hovered.Peek()) hovered.Value = true; },
            OnPointerExit = hovered is null ? null : () => { if (hovered.Peek()) hovered.Value = false; },
            Children = classic
                ? [body, new BoxEl
                  {
                      Key = "classic-hairline", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch,
                      Height = 1f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
                  }]
                : [body],
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
        return new BoxEl { Direction = 0, Gap = ClassicArtistChartLayout.FeatGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f, Children = kids };
    }

    /// <summary>The chart's own 28-DIP transparent chevron (not the stock 32 filled circle); a disabled one has no hover
    /// fill and a tertiary glyph.</summary>
    /// <summary>A pager chevron; its touch slop reaches left (<see cref="TouchTargetPolicy.OneSide"/>): outward for the previous one, into the gap for the next
    /// one, whose right side is the page's edge (a scroll bar's lane takes the side past it).</summary>
    static Element ChartChevron(string glyph, bool enabled, Action onClick) => new BoxEl
    {
        HitSlop = Wavee.Theming.TouchTargetPolicy.OneSide(ClassicArtistChartLayout.ChevronBox, left: true),
        Width = ClassicArtistChartLayout.ChevronBox, Height = ClassicArtistChartLayout.ChevronBox, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(14f), HoverFill = enabled ? Tok.FillSubtleSecondary : ColorF.Transparent,
        HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), OnClick = enabled ? onClick : null,
        Role = AutomationRole.Button, Cursor = enabled ? CursorId.Hand : null,
        Children = [Icon(glyph, 12f, enabled ? Tok.TextSecondary : Tok.TextTertiary)],
    };
}
