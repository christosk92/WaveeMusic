// ── Themes/Zune/Structure/Views/Artist/ZuneDiscography.cs — Zune's discography style (themes plan 6.3; P5 Z6a2; no board: built from Zune's parts, Q18) ──
//
// `StyleKeys.Discography`: the artist page's inline facets and the standalone page's grid, with the album drawer under an opened card. The core's
// components keep the demands, the verdicts, the memos, the selection and every verb; this draws what they hand it, from Zune's parts:
//
//   · THE FACET: the grid alone. The heading row ("albums", "N releases", "discography" at its end) is the page's, because it carries the link that
//     opens the standalone page and the style is not told which artist it draws. No pinned header, no accent spine, no era label.
//   · THE CARD: Zune's media surface (square, the cover at the cover radius, a wash on hover, the play ring on the art); its meta line wears a small
//     disclosure cue (down while it can open its drawer, up while it is the open one: the surface's selected skin is the 2-dip accent border over
//     the accent wash). 154 cells 16 apart, the surface's own chrome (50) under the square.
//   · THE DRAWER: a flat panel (the floating-pane fill, square, no stroke, no shadow) revealed under the card's row with a filled caret on the
//     clicked card; its head is a play ring, the cover, the title and meta (it opens the album) and an open ring; the rows are Zune's own eager row
//     (# · heart · title · time · "..."), the selection a 12 % accent wash, "show all N tracks" an accent word.
//
// Motion is the core's drawer reveal (`Design.Reveal.Drawer`). Numbers are `Rules\ZuneArtistRules.cs` and the tier records (`ZuneArtistTiers`).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using DrawerVerdict = Wavee.Album.DrawerVerdict;
using Rules = Wavee.Themes.Zune.ZuneArtistRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's style for the discography.</summary>
internal static class ZuneDiscography
{
    /// <summary>The style Zune registers under <c>StyleKeys.Discography</c>. Stateless.</summary>
    public static readonly DiscographyStyle Style = new(Metrics, Facet, FacetFailed, Card, Opened, Seed, Cell, DrawerFrame, DrawerPanel,
                                                       DrawerBar, DrawerSlot, ShowAll, DrawerTrack);

    /// <summary>The grid's configuration in the tier the grid is drawn in, and the album drawer's geometry (columns, pitch and head).</summary>
    static DiscographyMetrics Metrics(in TierFacts tiers)
    {
        var lay = ZuneArtistTiers.Of(in tiers);
        return new(lay.DiscoMinColumn, lay.DiscoGap, Rules.CardChrome, Rules.RowGap, lay.Drawer);
    }

    // ══ the facet section ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The grid alone (the page draws the heading row above it).</summary>
    static Element Facet(DiscoFacetArgs a) => new BoxEl { Direction = 1, MinWidth = 0f, Children = [a.Grid] };

    /// <summary>A failed facet must leave the shimmer: the compact Error vacancy and its Retry.</summary>
    static Element FacetFailed(Action retry)
        => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: retry);

    // ══ the cards ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The card's data off its row. The PINNED height keeps the card OUT of the grid's folded row gap.</summary>
    static Controls.CardData Card(DiscoCardArgs a)
        => new(a.Uri, a.Title, SubtitleOf(a.Meta, Icons.ChevronDown), a.Cover, a.Click, OnPlay: a.Play, Drag: a.Drag)
        {
            Height = a.Height, Menu = a.Menu,
        };

    /// <summary>The opened card wears the selected skin (the accent border over the accent wash) and the up cue.</summary>
    static Controls.CardData Opened(Controls.CardData plain, string meta, Func<ColorF> accent)
        => plain with { Selected = true, SelectedAccent = accent, Subtitle = SubtitleOf(meta, Icons.ChevronUp) };

    /// <summary>The seed (no row yet) at the card's pinned height.</summary>
    static Controls.CardData Seed(float height) => Controls.CardData.Seed with { Height = height };

    /// <summary>One cell. A constant key, shared by the seed and the card: a seed hydrating is a props re-push, never a remount.</summary>
    static Element Cell(Controls.CardData data) => Controls.Surface(data, Shape.Grid) with { Key = "album" };

    /// <summary>The meta line (data and labels: kept as written) with a small tertiary disclosure cue after it.</summary>
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
        b.LineTo(Rules.CaretW * Rules.Half, 0f);
        b.LineTo(Rules.CaretW, Rules.CaretH);
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    /// <summary>The drawer's slot under the opened card's row: the reserved height (the verdict's), clipped and revealed, the panel under its top gap
    /// and the filled caret apexed on the clicked card's column (it sinks 1 DIP to join the panel).</summary>
    static Element DrawerFrame(DiscoDrawerFrameArgs a)
    {
        float panelWidth = a.Columns * a.CellWidth + MathF.Max(0f, a.Columns - 1) * a.Gap;
        float apexX = Math.Clamp(a.Left + a.CellWidth * Rules.Half, Rules.CaretW, MathF.Max(Rules.CaretW, panelWidth - Rules.CaretW));
        float caretX = apexX - Rules.CaretW * Rules.Half;
        float caretY = DrawerVerdict.TopGap - Rules.CaretH + Rules.CaretOverlap;
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
                            Geometry = s_caret, Fill = R.Layer.FloatingPane, Rule = FillRule.NonZero,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The per-album panel: the head over the body (the shimmer while loading, the Retry note when ready but empty, else the rows) with the
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
            Corners = R.Shape.All(ShapeRole.Tile), Fill = R.Layer.FloatingPane,
            Children = [Head(a), ZStack(body, a.Bar)],
        };
    }

    /// <summary>The 32 ring beside the cover (a filled accent disc for play, a 2-dip outline otherwise): a button named for assistive tech.</summary>
    static Element HeadRing(string glyph, bool primary, Action onClick, string name)
        => Controls.Named(new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shrink = 0f, MinWidth = Rules.HeadPlay, MinHeight = Rules.HeadPlay,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, BlocksDragArm = true,
            OnClick = onClick, Children = [K.RingDisc(glyph, primary)],
        }, name);

    /// <summary>The head: Play, the cover, the title and meta (it opens the album) and the open ring.</summary>
    static Element Head(DiscoDrawerPanelArgs a)
    {
        var al = a.Album;
        var titleType = R.Type.TrackTitle("");
        // The head row is what the verdict's allowance leaves after the rows and the panel's padding, so the panel is exactly the slot's height.
        var v = a.Verdict;
        float head = v.PanelHeight - v.Rows * v.Pitch - Rules.PanelPadSum;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = head,
            Children =
            [
                HeadRing(Icons.Play, primary: true, a.PlayAlbum, Loc.Get(Strings.Detail.Play)),
                Controls.Artwork(Controls.ArtUrl(al.ImageId), Rules.HeadCover, Rules.HeadCover, R.Shape.Cover, decodePx: Rules.HeadCoverDecode),
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, OnClick = a.GoAlbum, Cursor = CursorId.Hand,
                    Children =
                    [
                        new SpanTextEl(
                        [
                            new TextSpan(al.Title),
                            new TextSpan(" · " + DiscoCardText.AlbumMeta(al), Weight: 400, Color: Tok.TextSecondary, Size: R.Type.DenseMeta("").Size),
                        ])
                        {
                            Size = titleType.Size, LineHeight = titleType.LineHeight, Weight = titleType.ResolvedWeight,
                            Color = Tok.TextPrimary,
                            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f,
                        },
                    ],
                },
                HeadRing(Icons.OpenInNewWindow, primary: false, a.GoAlbum, Loc.Get(Strings.Detail.GoToAlbum)),
            ],
        };
    }

    /// <summary>The track rows (and the "show all" cell) as ONE bound list (the core's, mounted once per geometry): exactly rows x pitch tall.</summary>
    static Element RowsBox(DiscoDrawerPanelArgs a)
        => a.List is null ? new BoxEl()
            : new BoxEl { Direction = 1, MinWidth = 0f, Height = a.Rows * a.Verdict.Pitch, Children = [a.List.Value] };

    /// <summary>READY BUT EMPTY (offline, a failed read, a trackless album): a fixed 2-row note with a Retry that re-asks the tracklist.</summary>
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

    /// <summary>The placeholder row at the SAME pitch and column split as the real rows: nothing reflows on land.</summary>
    static Element ShimmerRow(int i, float pitch) => new BoxEl
    {
        Key = "shimmer:" + i.ToString(CultureInfo.InvariantCulture),
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = pitch,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        Children =
        [
            new BoxEl { Width = Rules.ShimmerRank, Height = Rules.ShimmerBarH, Fill = Tok.FillSubtleSecondary },
            new BoxEl { Grow = 1f, Basis = 0f, Height = Rules.ShimmerBarH, MaxWidth = Rules.ShimmerTitleMax, Fill = Tok.FillSubtleSecondary },
            new BoxEl { Width = Rules.ShimmerTime, Height = Rules.ShimmerBarH, Fill = Tok.FillSubtleSecondary },
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

    /// <summary>One stack, or parallel column-major stacks (the FIRST ⌈n/columns⌉ cells go left), as the verdict's rows assume.</summary>
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

    /// <summary>The drawer's floating selection bar, standalone, near the panel's bottom.</summary>
    static Element DrawerBar(int count, Func<int, Element> lane) => Controls.SelectionBar(count, lane, standalone: true, bottomPadding: Spacing.S);

    /// <summary>One bound slot of the drawer's list: the check lane, the album's track cells, the selection wash. The slot ROOT owns the gesture (press /
    /// Enter / Space through the row scope's interaction funnel) and focus; the cells inside are click-less content.</summary>
    static BoxEl DrawerSlot(DiscoDrawerSlotArgs a)
    {
        var v = a.Verdict;
        int index = a.Index;
        int column = index / Math.Max(1, v.Rows);
        Edges4 margin = v.Columns <= 1 ? default
            : new Edges4(column > 0 ? Spacing.M : 0f, 0f, column < v.Columns - 1 ? Spacing.M : 0f, 0f);
        var scope = a.Scope;
        return new BoxEl
        {
            ZStack = true, Height = v.Pitch, ClipToBounds = true, Margin = margin,
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
                // The selection is the 12 % accent wash (no pill), compositor-only through the bound opacity the core hands.
                new BoxEl
                {
                    Key = "row-pill", Grow = 1f, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    Fill = Prop.Of(static () => R.Layer.SelectedRest), HitTestVisible = false, Opacity = a.PillOpacity,
                },
                new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Animate = Rules.CheckShift,
                    Children =
                    [
                        SelectorVisualsBound.BoundCheckLane(a.ChecksVisible, scope.IsSelected, scope.OnInteraction, leftMargin: Rules.CheckLead),
                        new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [a.Content] },
                    ],
                },
            ],
        };
    }

    /// <summary>The "show all N tracks" cell: not selectable, opens the album on a click, Enter or Space. An accent word.</summary>
    static Element ShowAll(DiscoShowAllArgs a) => new BoxEl
    {
        Key = "row:show-all", Height = a.Pitch, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Cursor = CursorId.Hand, Role = AutomationRole.Button,
        HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
        Focusable = false, FocusVisualMargin = Design.FocusInsetRow,
        OnPointerReleased = a.Release,
        OnKeyDown = a.Key,
        OnFocusChanged = a.FocusChanged,
        Children =
        [
            R.Type.DenseMeta(Strings.Detail.Discography.ShowAllTracks(a.Total)) with
            {
                Weight = Rules.ShowAllWeight, Color = Prop.Of(static () => R.Ink.AccentDecor), MaxLines = 1,
            },
        ],
    };

    /// <summary># · heart · title ★ · time · "...": Zune's eager row (`ZuneRowCells.Grid`), the title in the accent while it plays.</summary>
    static readonly Track.ColumnSet s_drawerCols = new(Album: false, By: false, Date: false, Plays: false, Heart: true, Thumb: false);
    static readonly TrackSize[] s_drawerColumns =
        [TrackSize.Px(Rules.RankCol), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Star(), TrackSize.Px(Rules.TimeCol), TrackSize.Px(Rules.MoreCol)];

    /// <summary>One drawer track cell.</summary>
    static Element DrawerTrack(DiscoDrawerTrackArgs a)
    {
        var t = a.Track;
        var st = a.State;
        Element title = ZuneRowCells.TitleCell(t, st.IsNow);
        return ZuneRowCells.Grid(t, a.Index, in st, in s_drawerCols, s_drawerColumns, a.Pitch - Rules.RowAir, title,
                                 new Track.GridOptions(OnPlay: a.Play, OnLike: a.Like, ActionsCell: Controls.MoreButton(null)));
    }
}
