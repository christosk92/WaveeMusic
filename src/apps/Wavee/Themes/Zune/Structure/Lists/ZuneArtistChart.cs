// ── Themes/Zune/Structure/Lists/ZuneArtistChart.cs — Zune's artist Top-tracks chart (themes plan 6.3; P5 Z3) ──────────────────────
//
// The artist page's chart as Z-Artist draws it: 60-DIP rows that touch (no gap, no plate, no border, square), the rank in the NumeralLarge
// role (28·300, tertiary; the playing row's accent bars in its place), the 48 cover at the Cover radius, the title in the TrackTitle role
// (15/20, the accent on the playing row), the sub line ("4.9B plays", feat. credit) in the TrackMeta role, the heart revealed on hover and
// the time; hover is the 5 % ink wash, pressed the pressed wash. The header is the Heading role's word (lowercased by the style); the pager
// is two square chevrons around the pips; a single page states the count.
//
// WHAT THE CORE KEEPS (`Artist.ChartHost`): the readiness gate, the asks, the items and their keys, the selection model, the drag payload,
// the menu, the play-from-here queue, the per-row memo, the tier decision (the art edge, the duration, the stacking sub line), the skeleton's
// counts and the pager's state. The row is shared by the live rows and the skeleton so the two cannot drift.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

using ChartCells = global::Wavee.Theming.Contracts.ArtistChartCells;

/// <summary>Zune's style for the artist chart.</summary>
internal static class ZuneArtistChart
{
    /// <summary>The style Zune registers under <c>StyleKeys.ArtistChart</c>. Stateless.</summary>
    public static readonly ArtistChartStyle Style = new(Metrics, Frame, Shelf, Bar, Header, Row, Cell, Skeleton, Failed, Pager);

    /// <summary>One row skin: 60 tall, touching, whatever the row style.</summary>
    static ArtistChartMetrics Metrics(bool classic)
        => new(ZuneListRules.ChartRowHeight, ZuneListRules.ChartCellGap, ZuneListRules.ChartHeaderGap, ZuneListRules.ChartMinCardWidth,
               ZuneListRules.ChartEdgeFade);

    /// <summary>The readiness region over the content, the shimmer and the failed panel, with the floating selection bar over it.</summary>
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

    /// <summary>The keyed shelf as a CHILD of a pass-through column (a key on a component's root is inert); a rows-per-column change glides
    /// the column's height (the flow reveal), never jumps.</summary>
    static Element Shelf(Element keyedShelf) => new BoxEl
    {
        Direction = 1, MinWidth = 0f, Layout = ZuneListRules.ChartResize, Children = [keyedShelf],
    };

    static Element Bar(int count, Func<int, Element> lane) => Controls.SelectionBar(count, lane, standalone: true, bottomPadding: Spacing.S);

    /// <summary>The chart's heading: "popular" (Z-Artist's word, not the core's "top tracks"), the Heading role (26/32·300), lowercased by the style.
    /// Zune draws no accent rule (the accent is ink).</summary>
    static Element Header(string title, ColorF accent)
        => R.Type.Heading(Loc.Get(Strings.Artist.Popular)) with { Case = R.Case.For(LabelKind.Heading), Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

    static Element Row(ArtistChartRowArgs a)
        => ChartRow(a.Cells, a.Index, a.State, a.Tier, FeatLine(a.Track, a.PageArtist, a.FeatCount, a.FeatSlot), a.OnPlay, a.OnLike, a.Pop,
                    a.Hovered, a.Slot, a.ChecksVisible, a.ShowArtwork);

    /// <summary>One realized cell: the row with the cell's drag; selection is the 12 % accent wash (no pill), compositor-only through the bound
    /// opacity the core hands for the single-selection cue.</summary>
    static BoxEl Cell(ArtistChartCellArgs a) => new()
    {
        ZStack = true,
        Draggable = a.Draggable,
        Children =
        [
            new BoxEl
            {
                Key = "row-pill", Grow = 1f, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                Fill = Prop.Of(static () => R.Layer.SelectedRest), HitTestVisible = false, Opacity = a.PillOpacity,
            },
            a.Content,
        ],
    };

    static Element Failed(string title, ColorF accent, Action retry) => new BoxEl
    {
        Direction = 1, Gap = ZuneListRules.ChartHeaderGap,
        Children = [Header(title, accent), Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: retry)],
    };

    /// <summary>The derived-skeleton source: the SAME row over a representative shape at the core's counts.</summary>
    static Element Skeleton(ArtistChartSkeletonArgs s)
    {
        int cols = s.Columns;
        var tier = new ArtistPopularLayout.Tier(ZuneListRules.ChartArt, true, false);
        var cells = new ChartCells("Top track title", null, "3:30", "1.2M", "1,234,567", false, false);
        var state = default(Track.RowState);
        var columns = new Element[cols];
        for (int c = 0; c < cols; c++)
        {
            int rows = Math.Min(s.PerColumn, Math.Max(0, s.Total - c * s.PerColumn));
            var kids = new Element[rows];
            for (int r = 0; r < rows; r++)
                kids[r] = ChartRow(cells, c * s.PerColumn + r, in state, tier, null, null, null, false, null, null, null, s.ShowArtwork);
            columns[c] = new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ZuneListRules.ChartCellGap, Children = kids };
        }
        return new BoxEl
        {
            Direction = 1, Gap = ZuneListRules.ChartHeaderGap,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = [Header(Loc.Get(Strings.Artist.TopTracks), Tok.AccentDefault)] },
                new BoxEl { Direction = 0, Gap = ZuneListRules.ChartTrailGap, Children = columns },
            ],
        };
    }

    /// <summary>Previous, the pips and next at two or more pages; the bare count at one.</summary>
    static Element Pager(ArtistChartPagerArgs p)
    {
        if (p.PageCount <= 1)
            return R.Type.TrackMeta(Strings.Home.SeeAllN(p.Total)) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Command), MaxLines = 1 };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
            Children =
            [
                Chevron(Icons.ChevronLeft, p.CanPrev, p.Prev),
                PipsPager.Create(p.PageCount, p.Selected, onChange: p.GoTo, onReselect: p.GoTo),
                Chevron(Icons.ChevronRight, p.CanNext, p.Next),
            ],
        };
    }

    /// <summary>One chart row (Z-Artist), shared by live rows and the skeleton.</summary>
    static Element ChartRow(in ChartCells c, int index, in Track.RowState st, ArtistPopularLayout.Tier tier, Element? feat, Action? onPlay,
                            Action? onLike, bool pop, Signal<bool>? hovered, RowScope? slot, Func<bool>? checksVisible, bool showArtwork)
    {
        bool isNow = st.IsNow;
        // The board's cover is 48 where the core's tier says the 44 step (the 40 floor of a narrow column stays).
        if (tier.Art >= 44f) tier = tier with { Art = ZuneListRules.ChartArt };
        ColorF accent = R.Ink.AccentAction;
        ColorF secondary = ZuneListRules.RowInk(isNow, accent, Tok.TextSecondary);
        ColorF tertiary = ZuneListRules.RowInk(isNow, accent, Tok.TextTertiary);
        bool hasPlays = c.PlaysCompact.Length > 0;
        bool stacked = tier.StackSub && feat is not null && hasPlays;
        bool playsInSub = hasPlays && !stacked;

        var sub = new List<Element>(7);
        void Dot() => sub.Add(R.Type.TrackMeta("·") with { Color = tertiary, Shrink = 0f });
        if (c.Explicit) sub.Add(ZuneRowCells.ExplicitMark());
        if (c.Video)
        {
            if (sub.Count > 0) Dot();
            sub.Add(Icon(Icons.Movie, ZuneListRules.FilmGlyph, tertiary));
        }
        if (feat is not null)
        {
            if (sub.Count > 0) Dot();
            sub.Add(feat);
        }
        if (playsInSub)
        {
            if (sub.Count > 0) Dot();
            sub.Add(ToolTip.Wrap(R.Type.TrackMeta(c.PlaysCompact) with { Color = tertiary, MaxLines = 1, Shrink = 0f }, c.PlaysExact));
        }

        var title = R.Type.TrackTitle(c.Title) with
        {
            Color = ZuneListRules.RowInk(isNow, accent, Tok.TextPrimary), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        Element subLine = sub.Count > 0
            ? new BoxEl { Direction = 0, Gap = ZuneListRules.ChartSubGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = sub.ToArray() }
            : new BoxEl();
        Element[] mid = stacked
            ? [title, subLine, R.Type.TrackMeta(c.PlaysExact) with { Color = tertiary, MaxLines = 1, Shrink = 0f }]
            : [title, subLine];

        bool saved = st.Saved;
        var focus = slot?.IsFocused;
        var trail = new List<Element>(2)
        {
            ZuneRowCells.HeartReveal(ZuneRowCells.Heart(saved, onLike, pop), () => saved || (focus?.Value ?? false)),
        };
        if (tier.ShowDuration) trail.Add(R.Type.DenseMeta(c.Duration) with { Color = secondary });

        var rowChildren = new List<Element>(4)
        {
            new BoxEl
            {
                Width = ZuneListRules.ChartRankBox - Spacing.M, Height = ZuneListRules.ChartRowHeight, Shrink = 0f,
                Children = [ZuneRowCells.NumberCell(index, in st, onPlay, hovered, chartStatus: 0, large: true)],
            },
        };
        if (showArtwork)
            rowChildren.Add(new BoxEl
            {
                Width = tier.Art, Height = tier.Art, Shrink = 0f,
                Children = [Controls.Artwork(c.ArtUrl, tier.Art, tier.Art, R.Shape.Cover, decodePx: ZuneListRules.ChartArtDecodePx)],
            });
        rowChildren.Add(new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Children = mid,
        });
        rowChildren.Add(new BoxEl { Direction = 0, Gap = ZuneListRules.ChartTrailGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = trail.ToArray() });

        float rowHeight = ZuneListRules.ChartRowHeight;
        var inset = new Edges4(0f, 0f, Spacing.S, 0f);
        BoxEl body = slot is { } scope && checksVisible is not null
            ? new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, MinHeight = rowHeight, AlignItems = FlexAlign.Center, Padding = inset,
                Children =
                [
                    SelectorVisualsBound.BoundCheckLane(checksVisible, scope.IsSelected, scope.OnInteraction, leftMargin: 0f),
                    new BoxEl
                    {
                        Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                        Animate = ZuneListRules.CheckShift, Children = rowChildren.ToArray(),
                    },
                ],
            }
            : new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, MinHeight = rowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = inset, Children = rowChildren.ToArray(),
            };

        return new BoxEl
        {
            ZStack = true, MinHeight = rowHeight, MinWidth = 0f, ClipToBounds = true,
            Fill = ColorF.Transparent, HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            PressScale = R.Motion.ScaleSubtle.Press,
            Role = AutomationRole.Button,
            OnClick = null,
            OnHoverMove = hovered is null ? null : _ => { if (!hovered.Peek()) hovered.Value = true; },
            OnPointerExit = hovered is null ? null : () => { if (hovered.Peek()) hovered.Value = false; },
            Children = [body],
        };
    }

    /// <summary>"feat. X +N": the first featured name is a link; "+N" opens the credit flyout of the rest.</summary>
    static Element? FeatLine(Track t, int pageArtist, int featCount, int firstSlot)
    {
        if (featCount == 0 || firstSlot <= 0) return null;
        var first = new Artist(firstSlot);
        var meta = R.Type.TrackMeta("");
        var kids = new Element[featCount > 1 ? 3 : 2];
        kids[0] = R.Type.TrackMeta(Loc.Get(Strings.Artist.Feat)) with { Color = Tok.TextTertiary, Shrink = 0f, Case = R.Case.For(LabelKind.Label) };
        kids[1] = new SpanTextEl([new TextSpan(first.Name, OnClick: () => Track.GoToArtist(first))])
        {
            Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
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
        return new BoxEl { Direction = 0, Gap = ZuneListRules.ChartFeatGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f, Children = kids };
    }

    /// <summary>The pager's square chevron; a disabled one has no hover wash and a tertiary glyph.</summary>
    static Element Chevron(string glyph, bool enabled, Action onClick) => new BoxEl
    {
        Width = ZuneListRules.ChartPagerChevron, Height = ZuneListRules.ChartPagerChevron, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        HoverFill = enabled ? R.Layer.RowHover : ColorF.Transparent, OnClick = enabled ? onClick : null,
        Role = AutomationRole.Button, Cursor = enabled ? CursorId.Hand : null,
        Children = [Icon(glyph, ZuneListRules.ChevronGlyph, enabled ? Tok.TextSecondary : Tok.TextTertiary)],
    };
}
