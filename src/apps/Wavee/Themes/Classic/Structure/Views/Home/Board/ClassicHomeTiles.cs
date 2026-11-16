// ── Themes/Classic/Structure/Views/Home/Board/ClassicHomeTiles.cs — Classic's `Home.Tile.*` (pinned-home-tech.md 1.7; spec 4.2, 8.3) ─────────
//
// Classic's speed dial and shelves. A speed-dial cell (S) is a cover flush left, the name, one line, on the opaque card tile (the Recently played tile's
// plate). A card (M) and a wide card (L) stack a head (cover or glyph, title, line) over the widget's body. A SHELF (Full) is today's chapter, verbatim
// (`ClassicHomeZones.TileChapter`: its sticky header pins under the facet band inside its own tile, its body clips under it).
//
// EDIT HOME, on every tile (`Frame`): a dashed outline; the chips in a strip of their own ABOVE the tile (the pack's `ToolStrip`, 40 DIP, which the rows
// grow by through the re-flow), or at a speed-dial cell's right, where its name lane gives up `CellChipsW`; the tile's content stops taking the pointer
// (the wrapper lifts, the chips act). At rest a tile's menu (right-click, the Menu key, a card's ⋯) holds Edit Home and the same verbs.

using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's tile templates and the board's chrome pieces.</summary>
internal static class ClassicHomeTiles
{
    // ══ the board's chrome ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A group title: the chapter header's title at the foot of its row. Pinned's title is also where a cover dragged onto Home pins.</summary>
    public static Element GroupHead(HomeBoardProps board, BoardEntry e)
    {
        var title = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Grow = 1f, Justify = FlexJustify.End, Padding = new Edges4(0f, 0f, 0f, Spacing.S),
            Children = [HomeModules.ModuleHeader(e.Title, null, null, open: null) with { Key = HomeBoardKeys.Group(e.GroupId) + ":title" }],
        };
        return e.GroupId == HomeDocs.Pinned && !board.ReadOnly ? title with { DropTarget = PinDrop(board) } : title;
    }

    /// <summary>The empty Pinned group: a dashed row at the group's height, "Drop here to pin. Or use the pin action on any library item." It takes a
    /// cover dragged onto Home.</summary>
    public static Element EmptyPinned(HomeBoardProps board, ClassicHomeBoardLayout lay) => new BoxEl
    {
        Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.S),
        Corners = Radii.CardAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
        BorderDashOn = ClassicHomeBoardRules.DashOn, BorderDashOff = ClassicHomeBoardRules.DashOff,
        DropTarget = board.ReadOnly ? null : PinDrop(board),
        Children =
        [
            Ui.Icon(Icons.Pin, ClassicHomeBoardRules.WidgetGlyph, Tok.TextSecondary) with { Shrink = 0f },
            Ui.Body(Loc.Get(Strings.Theme.Classic.Home.Board.EmptyPinned)).Secondary() with { Shrink = 1f, MinWidth = 0f },
        ],
    };

    /// <summary>Where a cover dragged onto Home pins: the drag chip says "Drop here to pin"; a drop is one Pin (Pinned, after the user's pins).</summary>
    static DropTargetSpec PinDrop(HomeBoardProps board) => new([Drag.Resource],
        OnEnter: s => { if (Pinnable(s.Payload) is not null) s.Caption = Loc.Get("sidebar.pin.dropHere"); },
        OnDrop: s =>
        {
            if (Pinnable(s.Payload) is not { } d || HomePinId.Canonical(d.Uri) is not { } pinId) return;
            board.Verbs.Pin(pinId, d.Name, d.ArtUrl ?? "", HomeDocs.Pinned);
        })
    {
        CanAccept = s => Pinnable(s.Payload) is not null,
    };

    static DragPayload? Pinnable(object? payload) => Drag.Unwrap(payload) is { } d && Drag.Pinnable(d.Kind) && d.Uri.Length > 0 ? d : null;

    /// <summary>"Customize Home" at the board's end: it enters Edit Home (the entry Home had at its end stays).</summary>
    public static Element EndLink(HomeBoardProps board) => new BoxEl
    {
        Key = "home:end:link", Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center,
        Children = [HyperlinkButton.Create(Loc.Get(Strings.Theme.Classic.Home.Board.CustomizeHome), () => ClassicHomeBoard.Live?.SetEditing(true))],
    };

    /// <summary>The drop outline at the lifted tile's packed place.</summary>
    public static Element Slot(RectF rect) => new BoxEl
    {
        Key = HomeBoardKeys.Slot, Width = rect.W, Height = rect.H, Corners = Radii.CardAll,
        BorderColor = Tok.AccentDefault, BorderWidth = ClassicHomeBoardRules.RingW,
        BorderDashOn = ClassicHomeBoardRules.DashOn, BorderDashOff = ClassicHomeBoardRules.DashOff, HitTestVisible = false,
    };

    /// <summary>The open tile: its options in its own box (the core's `Home.Tile.Options`, drawn by <see cref="ClassicHomeTileOptions"/>), on the card
    /// plate with the accent outline; in Edit Home its chips keep their strip above it.</summary>
    public static Element Open(HomeBoardProps board, BoardEntry e, PackCell cell, ClassicHomeBoardLayout lay, Element options)
    {
        var plate = new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderColor = Tok.AccentDefault, BorderWidth = ClassicHomeBoardRules.RingW,
            Children = [options],
        };
        var f = new HomeTileFrame(e, cell, HomeTileForm.M, board.Verbs, board.Overlay, board.View);
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = [Strip(f, lay), plate],
        };
    }

    // ══ the tile chrome ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The chip strip above a block of two units or more in Edit Home: its height is the pack's (bound: it grows and folds with the rows,
    /// by the re-flow's own easing), the chips at its end.</summary>
    static Element Strip(HomeTileFrame f, ClassicHomeBoardLayout lay)
    {
        var cell = f.Cell;
        if (cell is null) return new BoxEl();
        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, ClipToBounds = true,
            Height = Prop.Of(() => cell.Box.Value.ToolStrip),
            Layout = ClassicHomeBoardRules.Reveal,
            Children = [ClassicHomeChips.Create(f, lay, cell: false)],
        };
    }

    /// <summary>A tile around <paramref name="content"/>: the strip (Edit Home), the plate (a card) or none (a shelf), the dashed edit outline, the ring of
    /// a tile just added, the menu on a context request. In Edit Home the content stops taking the pointer: the wrapper lifts, the chips act.</summary>
    static Element Frame(in ViewContext ctx, HomeTileFrame f, Element content)
    {
        var lay = ClassicHomeBoardTiers.Of(ctx.Tier());
        var view = f.View;
        string id = f.Entry.Id;
        bool full = f.Form == HomeTileForm.Full;
        bool cellForm = f.Form == HomeTileForm.S;
        var body = new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
            HitTestVisible = Prop.Of(() => !view.Editing.Value),
            Children = [content],
        };
        Element face = cellForm
            ? new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    body with { Shrink = 1f, Basis = 0f },
                    // The speed-dial cell's chips stand at its right: its name lane gives up their room, by an eased width.
                    new BoxEl
                    {
                        Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, ClipToBounds = true,
                        Width = Prop.Of(() => view.Editing.Value ? ClassicHomeBoardRules.CellChipsW : 0f),
                        Layout = ClassicHomeBoardRules.ChipLane,
                        Children = [ClassicHomeChips.Create(f, lay, cell: true)],
                    },
                ],
            }
            : body;
        var plate = new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, ZStack = true, Corners = Radii.CardAll, ClipToBounds = !full,
            Fill = full ? ColorF.Transparent : Tok.FillCardDefault,
            BorderColor = full ? ColorF.Transparent : Tok.StrokeCardDefault, BorderWidth = full ? 0f : 1f,
            Children =
            [
                face,
                // Edit Home's dashed outline and the ring of a tile just added: bound visibility, nothing relayouts.
                new BoxEl
                {
                    Corners = Radii.CardAll, BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f, HitTestVisible = false,
                    BorderDashOn = ClassicHomeBoardRules.DashOn, BorderDashOff = ClassicHomeBoardRules.DashOff,
                    Visible = Prop.Of(() => view.Editing.Value),
                },
                new BoxEl
                {
                    Corners = Radii.CardAll, BorderColor = Tok.AccentDefault, BorderWidth = ClassicHomeBoardRules.RingW, HitTestVisible = false,
                    Visible = Prop.Of(() => view.RingId.Value == id),
                },
            ],
        };
        var tile = new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = cellForm ? [plate] : [Strip(f, lay), plate],
        };
        return f.Overlay is { } overlay && f.Cell is not null
            ? tile.WithContextMenu(overlay, () => new ContextMenuModel([], ClassicHomeChips.MenuRows(f)))
            : tile;
    }

    /// <summary>A card's padded column.</summary>
    static BoxEl Pad(params Element[] kids) => new()
    {
        Direction = 1, Grow = 1f, MinWidth = 0f, Gap = Spacing.S,
        Padding = new Edges4(ClassicHomeBoardRules.CardPad, ClassicHomeBoardRules.TilePad, ClassicHomeBoardRules.CardPad, ClassicHomeBoardRules.CardPad),
        Children = kids,
    };

    /// <summary>A speed-dial cell's line: the cover flush left at the cell's height, the name over one line (spec 4.2 `s`).</summary>
    static BoxEl CellFace(Element art, Func<string> title, Func<string> line, Element? end = null)
    {
        var text = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
            Children =
            [
                Ui.BodyStrong("") with { Text = Prop.Of(title), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                Ui.Caption("").Secondary() with { Text = Prop.Of(line), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            ],
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(0f, 0f, ClassicHomeBoardRules.TilePad, 0f),
            Children = end is null ? [art, text] : [art, text, end],
        };
    }

    /// <summary>The cell's cover: flush with the plate's left edge, the cell's full height, square (an artist's round, inset).</summary>
    static Element CellArt(string? url, ClassicHomeBoardLayout lay, bool round)
    {
        float edge = lay.Unit;
        if (round) return new BoxEl { Shrink = 0f, Width = edge, Height = edge, Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.S), Children = [Cover(url, edge - 2f * Spacing.S, true)] };
        return url is { Length: > 0 }
            ? new BoxEl { Shrink = 0f, Width = edge, Height = edge, Children = [Ui.Image(url, edge, edge, 0f, Tok.FillSubtleSecondary)] }
            : new BoxEl { Shrink = 0f, Width = edge, Height = edge, Fill = Tok.FillSubtleSecondary };
    }

    /// <summary>A glyph standing where a cell's cover would (a widget's speed-dial cell).</summary>
    static Element CellGlyph(string glyph, ClassicHomeBoardLayout lay)
        => new BoxEl { Shrink = 0f, Width = lay.Unit, Height = lay.Unit, Children = [Controls.IconPlate(glyph, lay.Unit, Tok.FillSubtleSecondary, Tok.TextSecondary)] };

    /// <summary>A widget head inside a card: a glyph, the title, a line.</summary>
    static Element WidgetHead(string glyph, string title, Func<string>? line) => new BoxEl
    {
        Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = ClassicHomeBoardRules.WidgetHeadH,
        Children =
        [
            Ui.Icon(glyph, ClassicHomeBoardRules.WidgetGlyph, Tok.AccentTextPrimary) with { Shrink = 0f },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                Children = line is null
                    ? [Ui.BodyStrong(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }]
                    : [Ui.BodyStrong(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                       Ui.Caption("").Secondary() with { Text = Prop.Of(line), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
            },
        ],
    };

    static Element Cover(string? url, float edge, bool round)
        => url is { Length: > 0 }
            ? new BoxEl { Shrink = 0f, Width = edge, Height = edge, Children = [Ui.Image(url, edge, edge, round ? edge * ClassicHomeBoardRules.Half : Radii.Control, Tok.FillSubtleSecondary)] }
            : new BoxEl { Width = edge, Height = edge, Shrink = 0f, Corners = round ? Radii.Circle(edge) : Radii.ControlAll, Fill = Tok.FillSubtleSecondary };

    static BoxEl Clickable(BoxEl box, Action open) => box with
    {
        OnClick = open, Cursor = CursorId.Hand, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, Role = AutomationRole.Button,
    };

    static Element PlayButton(Action play) => Button.Create(Loc.Get(Strings.Theme.Classic.Home.Board.Play), play, ButtonAppearance.Accent, ControlSize.Medium, Icons.Play)
        with { AlignSelf = FlexAlign.Start };

    // ══ Home.Tile.Pin ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTilePin"/>.</summary>
    public static Element Pin(HomePinTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var lay = ClassicHomeBoardTiers.Of(ctx.Tier());
        string? cover = p.Cover();
        // A pin with no cover of its own (Liked Songs, a route) stands on its glyph, never on an empty plate.
        Element cellArt = cover is { Length: > 0 } ? CellArt(cover, lay, p.Round)
            : CellGlyph(f.Entry.Raw is PinSource { PinId: "liked" } ? Icons.HeartFill : Icons.Pin, lay);
        if (f.Form is HomeTileForm.S or HomeTileForm.Full)
            return Frame(ctx, f, Clickable(CellFace(cellArt, p.Name, p.Line), p.Open));
        bool wide = f.Form == HomeTileForm.L;
        float art = wide ? ClassicHomeBoardRules.WideArt : ClassicHomeBoardRules.CardArt;
        var head = new BoxEl
        {
            Direction = (byte)(wide ? 0 : 1), Gap = Spacing.M, MinWidth = 0f, Grow = 1f,
            Children =
            [
                Cover(cover, art, p.Round),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = Spacing.XXS,
                    Children =
                    [
                        Ui.BodyStrong("") with { Text = Prop.Of(p.Name), MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        Ui.Caption("").Secondary() with { Text = Prop.Of(p.Line), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        new BoxEl { Grow = 1f },
                        p.Play is { } play ? PlayButton(play) : new BoxEl(),
                    ],
                },
            ],
        };
        return Frame(ctx, f, Clickable(Pad(head), p.Open));
    }

    // ══ Home.Tile.NowPlaying ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileNowPlaying"/>.</summary>
    public static Element NowPlaying(HomeNowPlayingTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var lay = ClassicHomeBoardTiers.Of(ctx.Tier());
        string title = Loc.Get(Strings.Theme.Classic.Home.Board.NowPlaying);
        Func<string> name = () => p.HasTrack() ? p.Title() : Loc.Get(Strings.Theme.Classic.Home.Board.NothingPlaying);
        var pp = new BoxEl
        {
            Shrink = 0f, Children = [Controls.IconAction(Icons.Play, p.PlayPause) with { Visible = Prop.Of(() => !p.Playing.Value) },
                                     Controls.IconAction(Icons.Pause, p.PlayPause) with { Visible = Prop.Of(() => p.Playing.Value) }],
        };
        if (f.Form is HomeTileForm.S or HomeTileForm.Full)
            return Frame(ctx, f, CellFace(CellArt(p.Cover(), lay, false), name, p.Line, pp));
        bool wide = f.Form == HomeTileForm.L;
        float art = wide ? ClassicHomeBoardRules.WideArt : ClassicHomeBoardRules.CardArt;
        var transport = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            Children = [Controls.IconAction(Icons.Previous, p.Previous), pp, Controls.IconAction(Icons.Next, p.Next)],
        };
        var text = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
            Children =
            [
                R.Type.Eyebrow(title),
                Ui.BodyStrong("") with { Text = Prop.Of(name), MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                Ui.Caption("").Secondary() with { Text = Prop.Of(p.Line), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                p.Lyric is { } lyric && wide ? lyric(LyricLine) : new BoxEl(),
                new BoxEl { Grow = 1f },
                transport,
            ],
        };
        var body = new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f,
            Children =
            [
                Pad(new BoxEl { Direction = (byte)(wide ? 0 : 1), Gap = Spacing.M, Grow = 1f, MinWidth = 0f, Children = [Clickable(new BoxEl { Shrink = 0f, Children = [Cover(p.Cover(), art, false)] }, p.Open), text] }),
                new BoxEl { Height = ClassicHomeBoardRules.ProgressH, Shrink = 0f, Direction = 1, Children = [p.ProgressLine()] },
            ],
        };
        return Frame(ctx, f, body);
    }

    static Element LyricLine(MiniLyricsFrame frame)
    {
        var lines = frame.Lines;
        var active = frame.Active;
        return Ui.Caption("") with
        {
            Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Text = Prop.Of(() => lines.Count == 0 ? "" : lines[Math.Clamp(active.Value, 0, lines.Count - 1)]),
        };
    }

    // ══ Home.Tile.UpNext ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileUpNext"/>.</summary>
    public static Element UpNext(HomeUpNextTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var lay = ClassicHomeBoardTiers.Of(ctx.Tier());
        var rows = p.Rows();
        string title = Loc.Get(Strings.Theme.Classic.Home.Board.UpNext);
        string first = rows.Length > 0 ? rows[0].Data.Title : Loc.Get(Strings.Theme.Classic.Home.Board.QueueEmpty);
        string count = Strings.Theme.Classic.Home.Board.InQueue(n: rows.Length);
        if (f.Form is HomeTileForm.S or HomeTileForm.Full)
            return Frame(ctx, f, Clickable(CellFace(CellGlyph(Icons.Queue, lay), () => title, () => first), p.OpenQueue));
        int max = f.Form == HomeTileForm.M ? ClassicHomeBoardRules.CardRows : ClassicHomeBoardRules.WideRows;
        var kids = new List<Element> { WidgetHead(Icons.Queue, title, () => count) };
        for (int i = 0; i < rows.Length && i < max; i++)
            kids.Add(Controls.Surface(rows[i].Data, ListShape) with { Key = "upnext:" + rows[i].Key });
        if (rows.Length == 0) kids.Add(Ui.Caption(first).Secondary());
        return Frame(ctx, f, Pad(kids.ToArray()));
    }

    static SurfaceShape ListShape => Shape.Row(ClassicHomeBoardRules.ListArt) with { MinHeight = ClassicHomeBoardRules.ListRowH };

    // ══ Home.Tile.Recents ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileRecents"/>.</summary>
    public static Element Recents(HomeRecentsTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var lay = ClassicHomeBoardTiers.Of(ctx.Tier());
        var r = p.Recents;
        string title = Loc.Get(Strings.Sidebar.Section.RecentlyPlayed);
        string week = Strings.Home.PlaysThisWeek(r.Week.Total);
        switch (f.Form)
        {
            case HomeTileForm.Full:
                return Frame(ctx, f, ClassicHomeZones.TileChapter(r.Zone, f.Overlay, ClassicHomeTiers.Of(ctx.Tier())));
            case HomeTileForm.S:
                return Frame(ctx, f, Clickable(CellFace(CellGlyph(Icons.History, lay), () => title, () => week), p.OpenHistory));
            default:
            {
                int max = f.Form == HomeTileForm.M ? ClassicHomeBoardRules.CardRows : ClassicHomeBoardRules.WideRows;
                var kids = new List<Element> { WidgetHead(Icons.History, title, () => week) };
                for (int i = 0; i < r.Cells.Count && i < max; i++)
                {
                    var cell = r.Cells[i];
                    var c = cell.Card;
                    var data = new Controls.CardData(c.Uri, cell.Title, ClassicHomeZones.Meta(cell.Detail), c.ImageUrl,
                        () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c), Circular: cell.Round) { Menu = HomeCardNav.MenuOf(in c) };
                    kids.Add(Controls.Surface(data, ListShape) with { Key = "recents:" + (c.IsBlank ? i.ToString(CultureInfo.InvariantCulture) : c.Uri) });
                }
                return Frame(ctx, f, Pad(kids.ToArray()));
            }
        }
    }

    // ══ Home.Tile.Releases ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileReleases"/>.</summary>
    public static Element Releases(HomeReleasesTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var zone = p.Zone;
        var lay = ClassicHomeBoardTiers.Of(ctx.Tier());
        if (f.Form == HomeTileForm.Full) return Frame(ctx, f, ClassicHomeZones.TileChapter(zone, f.Overlay, ClassicHomeTiers.Of(ctx.Tier())));
        string title = zone.Title ?? Loc.Get(Strings.Home.Zone.FromArtistsYouFollow);
        if (f.Form == HomeTileForm.S)
        {
            string first = zone.Items.Count > 0 ? zone.Items[0].Title ?? "" : "";
            return Frame(ctx, f, p.SeeAll is { } all
                ? Clickable(CellFace(CellGlyph(Icons.RefineSparkle, lay), () => title, () => first), all)
                : CellFace(CellGlyph(Icons.RefineSparkle, lay), () => title, () => first));
        }
        int max = f.Form == HomeTileForm.M ? ClassicHomeBoardRules.CardRows : ClassicHomeBoardRules.WideRows;
        var kids = new List<Element> { WidgetHead(Icons.RefineSparkle, title, null) };
        for (int i = 0; i < zone.Items.Count && i < max; i++) kids.Add(CardRow(zone.Items[i], i));
        return Frame(ctx, f, Pad(kids.ToArray()));
    }

    static Element CardRow(HomeCard card, int i)
    {
        var c = card;
        var data = new Controls.CardData(c.Uri, c.Title, ClassicHomeZones.Meta(c.Subtitle ?? ""), c.ImageUrl,
            () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c)) { Menu = HomeCardNav.MenuOf(in c) };
        return Controls.Surface(data, ListShape) with { Key = "row:" + (c.IsBlank ? i.ToString(CultureInfo.InvariantCulture) : c.Uri) };
    }

    // ══ Home.Tile.Daylist ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileDaylist"/>: the shelf is today's daylist chapter, verbatim.</summary>
    public static Element Daylist(HomeDaylistTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        if (f.Form == HomeTileForm.Full) return Frame(ctx, f, ClassicHomeZones.TileChapter(p.Zone, f.Overlay, ClassicHomeTiers.Of(ctx.Tier())));
        var card = p.Zone.Items.Count > 0 ? p.Zone.Items[0] : default;
        var lay = ClassicHomeBoardTiers.Of(ctx.Tier());
        if (f.Form == HomeTileForm.S)
        {
            string t = card.Title ?? "";
            return Frame(ctx, f, CellFace(CellArt(card.ImageUrl, lay, false), () => t, p.Eyebrow));
        }
        bool wide = f.Form == HomeTileForm.L;
        float art = wide ? ClassicHomeBoardRules.WideArt : ClassicHomeBoardRules.CardArt;
        var text = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
            Children =
            [
                Ui.Caption("").Secondary() with { Text = Prop.Of(p.Eyebrow), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                Ui.Subtitle(card.Title ?? "") with { MaxLines = wide ? 3 : 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                new BoxEl { Grow = 1f },
                p.Play is { } play ? PlayButton(play) : new BoxEl(),
            ],
        };
        var row = new BoxEl { Direction = (byte)(wide ? 0 : 1), Gap = Spacing.M, Grow = 1f, MinWidth = 0f, Children = [Cover(card.ImageUrl, art, false), text] };
        return Frame(ctx, f, Pad(row));
    }

    // ══ Home.Tile.Section ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileSection"/>: the shelf is today's chapter; a card is the section's first covers
    /// and names, and opens the section.</summary>
    public static Element Section(HomeSectionTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var zone = p.Zone;
        var lay = ClassicHomeBoardTiers.Of(ctx.Tier());
        if (f.Form == HomeTileForm.Full) return Frame(ctx, f, ClassicHomeZones.TileChapter(zone, f.Overlay, ClassicHomeTiers.Of(ctx.Tier())));
        string title = zone.Title ?? f.Entry.Title;
        if (f.Form == HomeTileForm.S)
        {
            var lead = zone.Items.Count > 0 ? zone.Items[0] : default;
            string sub = zone.Subtitle ?? "";
            var face = CellFace(CellArt(lead.ImageUrl, lay, false), () => title, () => sub);
            return Frame(ctx, f, p.SeeAll is { } all ? Clickable(face, all) : face);
        }
        int max = f.Form == HomeTileForm.M ? ClassicHomeBoardRules.CardRows : ClassicHomeBoardRules.WideRows;
        var kids = new List<Element> { WidgetHead(Icons.Library, title, zone.Subtitle is { Length: > 0 } s ? () => s : null) };
        for (int i = 0; i < zone.Items.Count && i < max; i++) kids.Add(CardRow(zone.Items[i], i));
        var box = Pad(kids.ToArray());
        return Frame(ctx, f, p.SeeAll is { } open ? Clickable(box, open) : box);
    }

    // ══ Home.Tile.Vacant ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileVacant"/>: a flat placeholder at the box while loading (never a pulse, owner rule
    /// 11-15); a quiet line when the thing is gone, paused, offline or unknown.</summary>
    public static Element Vacant(HomeTileVacantProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var lay = ClassicHomeBoardTiers.Of(ctx.Tier());
        if (p.Reason == HomeTileVacancy.Loading)
            return new BoxEl { Grow = 1f, Corners = Radii.CardAll, Fill = Tok.FillSubtleSecondary };
        string line = Loc.Get(p.Reason switch
        {
            HomeTileVacancy.Unavailable => Strings.Theme.Classic.Home.Board.Unavailable,
            HomeTileVacancy.Paused => Strings.Theme.Classic.Home.Board.Paused,
            HomeTileVacancy.Offline => Strings.Theme.Classic.Home.Board.Offline,
            _ => Strings.Theme.Classic.Home.Board.Unknown,
        });
        string name = p.Name;
        return Frame(ctx, f, CellFace(CellGlyph(Icons.Info, lay), () => name, () => line));
    }
}
