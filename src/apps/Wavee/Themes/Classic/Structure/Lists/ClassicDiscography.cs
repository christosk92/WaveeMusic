// ── Themes/Classic/Structure/Lists/ClassicDiscography.cs — Classic's discography (themes plan 6.1, T10b2) ───────────────────────────
//
// From `Entities\Artist.Discography.cs` (§6 the facet section's header, expander parts, clipped grid and the pinned label `FacetHeaderLabel`; §7 the
// drawer's frame and caret; §7b the card's data and its disclosure chevron; §8 the drawer panel's chrome, head, empty note, shimmer and column split,
// the selection bar, the track slot's skin, the "Show all" cell and the track cell): the picture of the artist page's inline facets and of the
// standalone discography page's grid. `StyleKeys.Discography` carries the templates the core components call while they render; they keep the
// demands, the verdicts, the memos, the selection and every verb (see `Theming\Contracts\DiscographyStyle.cs`).
//
// MOTION IS UNCHANGED: one click, one motion. The expander's content resize is off (a drawer opening inside the grid must not replay the disclosure
// tween, ch 08 W17, parity 55), the drawer's slot reveals with `Design.Reveal.Drawer`, the grid's cut under the pinned header feathers exactly while
// its clip is engaged (`EdgeFadeSpec.WhileStuck`), and the check lane slides the cells over on the track table's own tween.
//
// Numbers are `Rules\ClassicDiscographyRules.cs` (WVTH003 allows no literal look here).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using DrawerVerdict = Wavee.Album.DrawerVerdict;
using Rules = Wavee.Themes.Classic.ClassicDiscographyRules;

namespace Wavee.Themes.Classic;

/// <summary>Classic's style for the discography.</summary>
internal static class ClassicDiscography
{
    /// <summary>The style Classic registers under <c>StyleKeys.Discography</c>. Stateless but for the expander parts' one-entry cache.</summary>
    public static readonly DiscographyStyle Style = new(Metrics, Facet, FacetFailed, Card, Opened, Seed, Cell, DrawerFrame, DrawerPanel,
                                                       DrawerBar, DrawerSlot, ShowAll, DrawerTrack);

    static DiscographyMetrics Metrics() => new(Rules.MinColumn, Rules.Gap, Rules.CardChrome, Rules.RowGap);

    // ══ the facet section ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The whole inline facet section: the accent-spined header (an <c>Expander</c> whose header pins at the band's height), the grid
    /// clipped under it with the band's feather, and the drawer inside the grid.</summary>
    static Element Facet(DiscoFacetArgs a)
    {
        float inset = a.PinTop + Rules.FacetHeaderRowH;
        var visible = a.Visible;
        Element header = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Width = Rules.SpineW, MinHeight = Rules.SpineMinH, AlignSelf = FlexAlign.Stretch,
                    Corners = CornerRadius4.All(Radii.Pill), Fill = a.Accent, HitTestVisible = false,
                },
                Embed.Comp(new FacetLabelProps(a.Title, a.Total, a.Eras), () => new FacetHeaderLabel(visible))
                    with { Key = "facet-label" },
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f },
            ],
        };
        Element grid = new BoxEl
        {
            Direction = 1,
            // Feathered exactly while the grid's own clip is engaged, on the render turn that poses it (WhileStuck —
            // RCA 2026-09-25 F(ii): a re-render off an engaged signal lagged the cut by two presents).
            EdgeFade = new EdgeFadeSpec(EdgeMask.Top, Detail.BandLayout.ClipFadeBand) { WhileStuck = true },
            // Bug D: no facet-identity demotion — the grid's own next-page paging stays scroll-paced (the core's DemandNextPage
            // via the grid's realized window); only page ONE's priority changed, and it defaults to Visible.
            Children = [a.Grid],
        }.StickyClip(inset);

        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                // AnimateContentResize off: a drawer opening inside the grid must not replay the disclosure tween —
                // one click, one motion (ch 08 W17, parity 55).
                Embed.Comp(new Expander.ExpanderSlots(header, grid, Parts(a.PinTop)),
                    static () => new Expander { InitiallyExpanded = true, Options = new ExpanderOptions { AnimateContentResize = false } }),
                new BoxEl { Height = Spacing.XXL, HitTestVisible = false },
            ],
        };
    }

    /// <summary>A failed facet must leave the shimmer: the compact Error vacancy and its Retry.</summary>
    static Element FacetFailed(Action retry)
        => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: retry);

    // The expander's parts, rebuilt only when the pin moves (every inline facet pins under the same band), so an equal pin keeps the expander's slots
    // equal across renders.
    static TemplateParts? s_parts;
    static float s_partsPin = float.NaN;

    static TemplateParts Parts(float pinTop)
    {
        if (s_parts is not null && s_partsPin.Equals(pinTop)) return s_parts;
        s_partsPin = pinTop;
        return s_parts = new TemplateParts
        {
            [Expander.PartHeader] = element => element with
            {
                MinHeight = Rules.FacetHeaderRowH,
                Padding = new Edges4(0f, Spacing.XS, Spacing.S, Spacing.XS),
                Fill = ColorF.Transparent,
                HoverFill = ColorF.Transparent,
                PressedFill = ColorF.Transparent,
                BorderWidth = 0f,
                Corners = CornerRadius4.All(0f),
                BrushTransitionMs = 0f,
                ScrollEffects = [new ScrollEffectSpec(ScrollEffect.Sticky(pinTop))],
            },
            [Expander.PartChevron] = element => element with
            {
                Width = Rules.ChevronBox, Height = Rules.ChevronBox,
                Margin = new Edges4(Spacing.S, 0f, 0f, 0f),
            },
            [Expander.PartContent] = element => element with
            {
                Padding = Edges4.All(0f), MinHeight = 0f, Margin = Edges4.All(0f),
                Fill = ColorF.Transparent, BorderWidth = 0f,
                Corners = CornerRadius4.All(0f),
            },
        };
    }

    sealed record FacetLabelProps(string Title, int Total, DiscographyEraBand[]? Eras);

    /// <summary>The only reactive leaf in the pinned facet heading: a visible-window change replaces one text run; it
    /// never re-renders the Expander, reflows the grid or writes the page scroll (0.2.9 DiscographyFacetHeaderLabel).</summary>
    sealed class FacetHeaderLabel(IReadSignal<LazyGridVisibleRange> visible) : Component
    {
        public override Element Render()
        {
            var props = UsePropsOrDefault<FacetLabelProps>() ?? new FacetLabelProps("", 0, null);
            var range = visible.Value;
            string meta = props.Total > 0 ? Strings.Artist.ReleaseCount(props.Total) : "";
            if (props.Eras is { Length: > 0 } eras)
            {
                int index = range.LastIndexExclusive > range.FirstIndex ? range.FirstIndex : 0;
                if (DiscographyEraBands.AtIndex(eras, index) is { } era && era.Label.Length > 0)
                    meta = era.Label + " · " + Strings.Artist.ReleaseCount(era.Count);
            }
            return meta.Length > 0
                ? R.Type.RailHeader(props.Title, meta)
                : (Element)(R.Type.RailHeader(props.Title) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        }
    }

    // ══ the cards ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The card's data off its row. The PINNED height is what keeps the card OUT of the grid's folded row gap (its shell would otherwise grow
    /// into it), and the menu rides along so the surface's host attaches it to the shell.</summary>
    static Controls.CardData Card(DiscoCardArgs a)
        => new(a.Uri, a.Title, SubtitleOf(a.Meta, Icons.ChevronDown), a.Cover, a.Click, OnPlay: a.Play, Drag: a.Drag)
        {
            Height = a.Height, Menu = a.Menu,
        };

    /// <summary>The opened card wears the accent border + the brighter fill; with the caret it answers "where did it open" (ch 08 §9
    /// must-not-simplify 4). The accent is read inside the surface's render, so a palette landing repaints the one opened card.</summary>
    static Controls.CardData Opened(Controls.CardData plain, string meta, Func<ColorF> accent)
        => plain with { Selected = true, SelectedAccent = accent, Subtitle = SubtitleOf(meta, Icons.ChevronUp) };

    /// <summary>The seed (no row yet) at the card's pinned height: the grid's folded row gap must not stretch it.</summary>
    static Controls.CardData Seed(float height) => Controls.CardData.Seed with { Height = height };

    /// <summary>One cell. A constant key, shared by the seed and the card: the grid's own per-position wrapper already carries the identity, so a seed
    /// hydrating is a props re-push, never a remount.</summary>
    static Element Cell(Controls.CardData data) => Controls.Surface(data, Shape.Grid) with { Key = "album" };

    /// <summary>The meta line with a small tertiary disclosure chevron after it: pointing down while the card can open
    /// its drawer, up while it is the open one (<see cref="Controls.CardData.Selected"/> carries the accent skin; the
    /// glyph is the quiet "this expands" cue the plain card never had).</summary>
    static Element SubtitleOf(string meta, string glyph) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.SubtitleGap, MinWidth = 0f,
        Children =
        [
            R.Type.TrackMeta(meta) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
            Icon(glyph, Rules.SubtitleGlyph, Tok.TextTertiary),
        ],
    };

    // ══ the drawer ═══════════════════════════════════════════════════════════════════════════════════════════════════

    static readonly PathData s_caret = BuildCaret();

    static PathData BuildCaret()
    {
        var b = new PathBuilder();
        b.MoveTo(0f, Rules.CaretH);
        b.LineTo(Rules.CaretW * 0.5f, 0f);
        b.LineTo(Rules.CaretW, Rules.CaretH);
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    /// <summary>The drawer's slot under the opened card's row: the reserved height (the verdict's), clipped and revealed, the panel under its top gap
    /// and the caret apexed on the clicked card's column.</summary>
    static Element DrawerFrame(DiscoDrawerFrameArgs a)
    {
        float panelWidth = a.Columns * a.CellWidth + MathF.Max(0f, a.Columns - 1) * a.Gap;
        float apexX = Math.Clamp(a.Left + a.CellWidth / 2f, Radii.Card, MathF.Max(Radii.Card, panelWidth - Radii.Card));
        float caretX = apexX - Rules.CaretW / 2f;
        float caretY = DrawerVerdict.TopGap - Rules.CaretH + Rules.CaretOverlap;   // sinks 1 DIP to hide the panel's top stroke
        return new BoxEl
        {
            Key = "disco-drawer", Direction = 1, Height = a.SlotHeight, ClipToBounds = true, Animate = Design.Reveal.Drawer,
            Children =
            [
                new BoxEl
                {
                    ZStack = true,
                    Children =
                    [
                        new BoxEl { Direction = 1, Children = [new BoxEl { Height = DrawerVerdict.TopGap, HitTestVisible = false }, a.Panel] },
                        new PathEl
                        {
                            OffsetX = caretX, OffsetY = caretY, Width = Rules.CaretW, Height = Rules.CaretH,
                            Geometry = s_caret, Fill = Tok.FillCardSecondary, Rule = FillRule.NonZero,
                        },
                        new PolylineStrokeEl
                        {
                            OffsetX = caretX, OffsetY = caretY, Width = Rules.CaretW, Height = Rules.CaretH,
                            P0 = new Point2(0f, Rules.CaretH), P1 = new Point2(Rules.CaretW * 0.5f, 0f), P2 = new Point2(Rules.CaretW, Rules.CaretH),
                            PointCount = 3, Color = Tok.StrokeCardDefault, Thickness = Rules.CaretStroke, RoundCaps = false,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The per-album panel: the head over the body (the shimmer while loading, the 2-row Retry note when ready but empty, else the rows) with the
    /// floating selection bar over the rows.</summary>
    static Element DrawerPanel(DiscoDrawerPanelArgs a)
    {
        var v = a.Verdict;
        Element body = v.ReadyEmpty ? EmptyNote(a.Retry)
                     : v.Loading ? BuildColumns(v.Shown, v.Columns, s_shimmer)
                     : RowsBox(a);
        return new BoxEl
        {
            Direction = 1, ClipToBounds = true,
            Padding = new Edges4(Rules.PanelPadX, Rules.PanelPadY, Rules.PanelPadX, Rules.PanelPadY),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
            BorderWidth = Rules.PanelBorder, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                Head(a),
                // The standard selection bar floats over the drawer's own rows, 8 up from the bottom, once the
                // selection earns it (SelectionBarRules.Visible: two or more rows). It is a component of its own, so a
                // select/clear re-renders only the bar and the bound row chrome - never the panel or the list.
                ZStack(body, a.Bar),
            ],
        };
    }

    static Element Head(DiscoDrawerPanelArgs a)
    {
        var al = a.Album;
        var titleType = R.Type.DenseTitle("");
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = Rules.HeadH,
            Children =
            [
                Controls.Named(Controls.PlayFab(a.PlayAlbum, Icons.Play, Rules.HeadPlay), Loc.Get(Strings.Detail.Play)),
                Controls.Artwork(Controls.ArtUrl(al.ImageId), Rules.HeadCover, Rules.HeadCover, Radii.Control, decodePx: Rules.HeadCoverDecode),
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, OnClick = a.GoAlbum, Cursor = CursorId.Hand,
                    Children =
                    [
                        new SpanTextEl(
                        [
                            new TextSpan(al.Title),
                            new TextSpan(" · " + DiscoCardText.AlbumMeta(al), Weight: 400, Color: Tok.TextSecondary, Size: Ui.Caption("").Size),
                        ])
                        {
                            Size = titleType.Size, LineHeight = titleType.LineHeight, Weight = titleType.ResolvedWeight,
                            Color = Tok.TextPrimary,
                            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f,
                        },
                    ],
                },
                Controls.Named(new BoxEl
                {
                    Width = Rules.HeadOpen, Height = Rules.HeadOpen, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(Rules.HeadOpenRadius), BorderWidth = Rules.HeadBorder, BorderColor = Tok.StrokeControlDefault,
                    OnClick = a.GoAlbum, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                    HoverFill = Tok.FillSubtleSecondary,
                    Children = [Icon(Icons.OpenInNewWindow, Rules.HeadOpenGlyph, Tok.TextSecondary)],
                }, Loc.Get(Strings.Detail.GoToAlbum)),
            ],
        };
    }

    /// <summary>The track rows (and the "Show all" cell) as ONE bound list (the core's, mounted once per geometry). The list is exactly
    /// <c>rows x RowPitch</c> tall, so nothing scrolls and the reserved slot height holds.</summary>
    static Element RowsBox(DiscoDrawerPanelArgs a)
        => a.List is null ? new BoxEl()
            : new BoxEl { Direction = 1, MinWidth = 0f, Height = a.Rows * DrawerVerdict.RowPitch, Children = [a.List] };

    /// <summary>READY BUT EMPTY — offline, a failed read, a trackless album: a fixed 2-row note with a Retry that
    /// re-asks the tracklist while the drawer stays open (W16).</summary>
    static Element EmptyNote(Action retry) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.M),
        Children =
        [
            R.Type.DenseMeta(Loc.Get(Strings.Detail.Empty.NoTracks))
                with { Grow = 1f, Basis = 0f, MinWidth = 0f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Button.Standard(Loc.Get(Strings.Common.Retry), retry),
        ],
    };

    static readonly Func<int, Element> s_shimmer = ShimmerRow;

    /// <summary>The placeholder row at the SAME pitch and column split as the real rows — nothing reflows on land.</summary>
    static Element ShimmerRow(int i) => new BoxEl
    {
        Key = "shimmer:" + i.ToString(CultureInfo.InvariantCulture),
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = DrawerVerdict.RowPitch,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        Children =
        [
            new BoxEl { Width = Rules.ShimmerRank, Height = Rules.ShimmerBarH, Corners = CornerRadius4.All(Rules.ShimmerRadius), Fill = Tok.FillSubtleSecondary },
            new BoxEl
            {
                Grow = 1f, Basis = 0f, Height = Rules.ShimmerBarH, MaxWidth = Rules.ShimmerTitleMax, Corners = CornerRadius4.All(Rules.ShimmerRadius),
                Fill = Tok.FillSubtleSecondary,
            },
            new BoxEl { Width = Rules.ShimmerTime, Height = Rules.ShimmerBarH, Corners = CornerRadius4.All(Rules.ShimmerRadius), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Width = Rules.ShimmerTrail },
        ],
    };

    static Element BuildColumns(int cellCount, int columns, Func<int, Element> cell)
    {
        if (cellCount <= 0) return new BoxEl();
        var kids = new Element[cellCount];
        for (int i = 0; i < cellCount; i++) kids[i] = cell(i);
        return Split(kids, columns);
    }

    /// <summary>One stack, or two parallel column-major stacks (the FIRST ⌈n/2⌉ cells go left), as the verdict's ⌈cells / columns⌉ rows assume.</summary>
    static Element Split(Element[] cells, int columns)
    {
        if (cells.Length == 0) return new BoxEl();
        if (columns <= 1) return new BoxEl { Direction = 1, Children = cells };
        int perColumn = (cells.Length + columns - 1) / columns;
        var cols = new Element[columns];
        for (int c = 0; c < columns; c++)
        {
            int start = c * perColumn, end = Math.Min(cells.Length, start + perColumn);
            cols[c] = new BoxEl
            {
                Key = "col:" + c.ToString(CultureInfo.InvariantCulture), Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = end > start ? cells[start..end] : [],
            };
        }
        return new BoxEl { Direction = 0, Gap = Spacing.XL, Children = cols };
    }

    /// <summary>The drawer's floating selection bar, standalone, 8 up from the panel's bottom.</summary>
    static Element DrawerBar(int count, Func<int, Element> lane) => Controls.SelectionBar(count, lane, standalone: true, bottomPadding: Spacing.S);

    /// <summary>One bound slot of the drawer's list: the check lane, the album's track cells, the accent pill. The slot ROOT owns the gesture (press /
    /// Enter / Space through the row scope's interaction funnel) and focus; the cells inside are click-less content.</summary>
    static BoxEl DrawerSlot(DiscoDrawerSlotArgs a)
    {
        var v = a.Verdict;
        int index = a.Index;
        // 2-column gutter: the layout butts the columns together, the slot insets its inner edge.
        Edges4 margin = v.Columns <= 1 ? default
            : index / Math.Max(1, v.Rows) == 0 ? new Edges4(0f, 0f, Spacing.M, 0f) : new Edges4(Spacing.M, 0f, 0f, 0f);
        var scope = a.Scope;
        return new BoxEl
        {
            // Height, not MinHeight: the slot IS the 32-DIP pitch, so a column totals EXACTLY rows x RowPitch.
            ZStack = true, Height = DrawerVerdict.RowPitch, ClipToBounds = true, Corners = Radii.ControlAll, Margin = margin,
            Fill = ColorF.Transparent, HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            Role = AutomationRole.Button, Cursor = CursorId.Hand,
            Focusable = false,                                // the ItemsView roving effect owns the one tab stop
            FocusVisualMargin = Design.FocusInsetRow,
            Draggable = a.Drag,
            OnPointerReleased = a.Release,
            OnKeyDown = a.Key,
            OnFocusChanged = scope.OnFocusChanged,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Animate = Rules.CheckShift,
                    Children =
                    [
                        SelectorVisualsBound.BoundCheckLane(a.ChecksVisible, scope.IsSelected, scope.OnInteraction, leftMargin: Rules.CheckLead),
                        new BoxEl
                        {
                            Grow = 1f, Basis = 0f, MinWidth = 0f,
                            Children = [a.Content],
                        },
                    ],
                },
                new BoxEl
                {
                    Key = "pill", Width = Rules.PillW, Height = Rules.PillH, Margin = new Edges4(Rules.PillLead, 0f, 0f, 0f),
                    Corners = CornerRadius4.All(Rules.PillRadius), AlignSelf = FlexAlign.Center, Fill = a.PillFill, HitTestVisible = false,
                    Opacity = a.PillOpacity,
                },
            ],
        };
    }

    /// <summary>The "Show all N tracks" cell: not selectable (the list's IsItemSelectable), opens the album on a click, Enter or Space.</summary>
    static Element ShowAll(DiscoShowAllArgs a) => new BoxEl
    {
        Key = "row:show-all", Height = DrawerVerdict.RowPitch, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Cursor = CursorId.Hand, Role = AutomationRole.Button,
        Corners = Radii.ControlAll, HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
        Focusable = false, FocusVisualMargin = Design.FocusInsetRow,
        OnPointerReleased = a.Release,
        OnKeyDown = a.Key,
        OnFocusChanged = a.FocusChanged,
        Children = [Ui.Caption(Strings.Detail.Discography.ShowAllTracks(a.Total)) with { Weight = 600, Color = Tok.AccentTextPrimary }],
    };

    /// <summary># 26 · ♥ 28 · title ★ · time 44 · … 32 at the 32-DIP <c>DrawerVerdict.RowPitch</c> (content 28).</summary>
    static readonly Track.ColumnSet s_drawerCols = new(Album: false, By: false, Date: false, Video: false, Plays: false,
                                                        Heart: true, Thumb: false);
    static readonly TrackSize[] s_drawerColumns =
        [TrackSize.Px(Rules.RankCol), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Star(), TrackSize.Px(Rules.TimeCol), TrackSize.Px(Rules.MoreCol)];

    /// <summary>One drawer track cell: the SAME eager-row grid `Track.EagerRow` draws (Classic's `StyleKeys.EagerTrackRow`), the title in the accent
    /// while it plays.</summary>
    static Element DrawerTrack(DiscoDrawerTrackArgs a)
    {
        var t = a.Track;
        var st = a.State;
        Element title = R.Type.DenseTitle(t.Title)
            with
            {
                Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            };
        return ClassicEagerRow.Style.Grid(t, a.Index, in st, in s_drawerCols, s_drawerColumns, Rules.RowContentH, title,
                                          new Track.GridOptions(OnPlay: a.Play, OnLike: a.Like, ActionsCell: Controls.MoreButton(null)));
    }
}
