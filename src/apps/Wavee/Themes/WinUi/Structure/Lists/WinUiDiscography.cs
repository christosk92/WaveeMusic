// ── Themes/WinUi/Structure/Lists/WinUiDiscography.cs — WinUI's discography (themes plan 6.1, T10b2) ───────────────────────────
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
// Numbers are `Rules\WinUiDiscographyRules.cs` (WVTH003 allows no literal look here).

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
using Rules = Wavee.Themes.WinUi.WinUiDiscographyRules;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's style for the discography.</summary>
internal static class WinUiDiscography
{
    /// <summary>The style WinUI registers under <c>StyleKeys.Discography</c>. Stateless.</summary>
    public static readonly DiscographyStyle Style = new(Metrics, Facet, FacetFailed, Card, Opened, Seed, Cell, DrawerFrame, DrawerPanel,
                                                       DrawerBar, DrawerSlot, ShowAll, DrawerTrack);

    /// <summary>The grid's configuration in the window tier the grid is drawn in (TC6: a two-column floor at Compact) and the album drawer's geometry
    /// (its columns, pitch and head per tier: C-Discography-Compact's one column of 40 rows, -Small's two, -Huge's four): the core's grid host reads its
    /// facts tracked and re-fits the same grid on a crossing, and an open drawer re-flows to the tier's columns.</summary>
    static DiscographyMetrics Metrics(in global::Wavee.Theming.TierFacts tiers)
    {
        var layout = WinUiArtistTiers.DiscoLayoutOf(WinUiArtistTiers.TierOf(in tiers));
        return new(layout.MinColumn, layout.Gap, Rules.CardChrome, Rules.RowGap, layout.Drawer);
    }

    // ══ the facet section ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The whole inline facet section (W-Artist's sections, W-Discography's page): the title in Subtitle 20/28 with its count (an era's label while the grid
    /// shows that era), 12 over the grid; the drawer inside the grid. No spine, no expander, no pin: the page's sticky piece is the pivot.</summary>
    static Element Facet(DiscoFacetArgs a)
    {
        var visible = a.Visible;
        return new BoxEl
        {
            Direction = 1, Gap = Rules.FacetGap, MinWidth = 0f,
            Children =
            [
                Embed.Comp(new FacetLabelProps(a.Title, a.Total, a.Eras), () => new FacetHeaderLabel(visible)) with { Key = "facet-label" },
                new BoxEl { Direction = 1, Children = [a.Grid] },
            ],
        };
    }

    /// <summary>A failed facet must leave the shimmer: the compact Error vacancy and its Retry.</summary>
    static Element FacetFailed(Action retry)
        => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: retry);

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
        float pitch = v.Pitch;
        Element body = v.ReadyEmpty ? EmptyNote(a.Retry)
                     : v.Loading ? BuildColumns(v.Shown, v.Columns, i => ShimmerRow(i, pitch))
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

    /// <summary>The head (W-Discography): the cover (80, r8), the title in Subtitle 20/28 over "Album · Jun 12, 2014 · 12 tracks" (it opens the album), Play as an accent
    /// button and "Go to album" as a standard one, at the tier's head row (Compact: the 40 targets).</summary>
    static Element Head(DiscoDrawerPanelArgs a)
    {
        var al = a.Album;
        bool large = a.Verdict.Pitch >= Rules.DrawerPitchCompact;
        float target = large ? Rules.CompactTarget : Rules.Target;
        string meta = Detail.Text.KindLabel(al.Kind) + Rules.MetaLead + DiscoCardText.AlbumMeta(al);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = large ? Rules.HeadRowCompact : Rules.HeadRow,
            Children =
            [
                new BoxEl
                {
                    Width = Rules.HeadCover, Height = Rules.HeadCover, Shrink = 0f, Corners = CornerRadius4.All(Rules.HeadCoverRadius), ClipToBounds = true,
                    Children = [Controls.Artwork(Controls.ArtUrl(al.ImageId), Rules.HeadCover, Rules.HeadCover, Rules.HeadCoverRadius, decodePx: Rules.HeadCoverDecode)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, OnClick = a.GoAlbum, Cursor = CursorId.Hand,
                    Children =
                    [
                        R.Type.RailHeader(al.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        Ui.Caption(meta) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
                Controls.PlayButton(Tok.AccentDefault, a.PlayAlbum, large ? ControlSize.Large : ControlSize.Medium, Loc.Get(Strings.Detail.Play)) with { Shrink = 0f },
                Button.Create(Loc.Get(Strings.Detail.GoToAlbum), a.GoAlbum, ButtonAppearance.Standard, large ? ControlSize.Large : ControlSize.Medium) with { Shrink = 0f, Height = target },
            ],
        };
    }

    /// <summary>The track rows (and the "Show all" cell) as ONE bound list (the core's, mounted once per geometry). The list is exactly
    /// <c>rows x Pitch</c> tall (the verdict's pitch), so nothing scrolls and the reserved slot height holds.</summary>
    static Element RowsBox(DiscoDrawerPanelArgs a)
        => a.List is null ? new BoxEl()
            : new BoxEl { Direction = 1, MinWidth = 0f, Height = a.Rows * a.Verdict.Pitch, Children = [a.List] };

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

    /// <summary>The placeholder row at the SAME pitch (the verdict's) and column split as the real rows — nothing reflows on land.</summary>
    static Element ShimmerRow(int i, float pitch) => new BoxEl
    {
        Key = "shimmer:" + i.ToString(CultureInfo.InvariantCulture),
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = pitch,
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

    /// <summary>One stack, or parallel column-major stacks (the FIRST ⌈n/columns⌉ cells go left), as the verdict's ⌈cells / columns⌉ rows assume.</summary>
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
        // The columns' gutter: the layout butts the columns together and each slot insets its inner edges (a middle column of Huge's four both).
        int column = index / Math.Max(1, v.Rows);
        Edges4 margin = v.Columns <= 1 ? default
            : new Edges4(column > 0 ? Spacing.M : 0f, 0f, column < v.Columns - 1 ? Spacing.M : 0f, 0f);
        var scope = a.Scope;
        return new BoxEl
        {
            // Height, not MinHeight: the slot IS the verdict's pitch, so a column totals EXACTLY rows x Pitch.
            ZStack = true, Height = v.Pitch, ClipToBounds = true, Corners = Radii.ControlAll, Margin = margin,
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
        Key = "row:show-all", Height = a.Pitch, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Cursor = CursorId.Hand, Role = AutomationRole.Button,
        Corners = Radii.ControlAll, HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
        Focusable = false, FocusVisualMargin = Design.FocusInsetRow,
        OnPointerReleased = a.Release,
        OnKeyDown = a.Key,
        OnFocusChanged = a.FocusChanged,
        Children = [Ui.Caption(Strings.Detail.Discography.ShowAllTracks(a.Total)) with { Weight = 600, Color = Tok.AccentTextPrimary }],
    };

    /// <summary># 26 · ♥ 28 · title ★ · time 44 · … 32 at the tuned 32 pitch (content 28); C-Discography-Compact's # 24 · ♥ · title · time 40 · … 40 at its
    /// 40 pitch, so the row's More is a 40 target.</summary>
    static readonly Track.ColumnSet s_drawerCols = new(Album: false, By: false, Date: false, Plays: false,
                                                        Heart: true, Thumb: false);
    static readonly TrackSize[] s_drawerColumns =
        [TrackSize.Px(Rules.RankCol), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Star(), TrackSize.Px(Rules.TimeCol), TrackSize.Px(Rules.MoreCol)];
    static readonly TrackSize[] s_drawerColumnsCompact =
        [TrackSize.Px(Rules.RankColCompact), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Star(), TrackSize.Px(Rules.TimeColCompact),
         TrackSize.Px(Rules.MoreColCompact)];

    /// <summary>One drawer track cell: the SAME eager-row grid `Track.EagerRow` draws (WinUI's `StyleKeys.EagerTrackRow`), the title in the accent
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
        // The row's content is the pitch less the tuned 4 of air (28 in 32, 36 in Compact's 40).
        float pitch = a.Pitch;
        bool compact = pitch >= Rules.DrawerPitchCompact;
        return WinUiEagerRow.Style.Grid(t, a.Index, in st, in s_drawerCols, compact ? s_drawerColumnsCompact : s_drawerColumns,
                                          pitch - Rules.RowAir, title,
                                          new Track.GridOptions(OnPlay: a.Play, OnLike: a.Like, ActionsCell: Controls.MoreButton(null)));
    }
}
