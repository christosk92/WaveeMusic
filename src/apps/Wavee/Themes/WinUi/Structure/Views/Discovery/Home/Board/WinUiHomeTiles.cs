// ── Themes/WinUi/Structure/Views/Discovery/Home/Board/WinUiHomeTiles.cs — WinUI's `Home.Tile.*` (pinned-home-tech.md 1.7; spec 4.2, 8.1) ─────
//
// The Widgets board's card: a Fluent card plate (CardBackground, CardStroke, r8) with a ⋯ in a RESERVED corner in every form, so the menu never sits on
// content. A row tile (S) is the Recently played tile's shape: a 48 cover, the name, one line. A card (M) and a double card (L) stack a head (cover or
// glyph, title, line) over the widget's body. A FULL row is today's chapter (the shelf, the release list, the Recents grid, the daylist card) with the ⋯
// in a lane of its own at the header's end.
//
// EVERY CARD ALWAYS DRAGS (the grid's reorder lifts it: a 6 DIP mouse move; touch: hold, then drag, or release for the menu), and takes focus:
// Alt+Shift+arrows move it (Start's chord), the Menu key or Shift+F10 opens its menu. The menu (spec 8.1): the sizes as radio rows (the pill mark),
// Customize widget, Pause live updates, Move to front / left / right, then the remove row.

using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Animation;
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

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's tile templates and the board's chrome pieces.</summary>
internal static class WinUiHomeTiles
{
    // ══ the board's chrome ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A group head: a quiet 14 semibold label at the foot of its row (spec 4.2).</summary>
    public static Element GroupHead(BoardEntry e, WinUiHomeBoardLayout lay) => new BoxEl
    {
        Direction = 1, MinWidth = 0f, Grow = 1f, Justify = FlexJustify.End, Padding = new Edges4(0f, 0f, 0f, Spacing.S),
        // The label hugs its words (it does not stretch with the row), so a width change moves nothing in it.
        Children = [Ui.BodyStrong(e.Title) with { Key = HomeBoardKeys.Group(e.GroupId) + ":title", MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, AlignSelf = FlexAlign.Start }],
    };

    /// <summary>The empty Pinned group: a dashed row at the group's height with how to pin (spec 8.1 "Empty states").</summary>
    public static Element EmptyPinned(WinUiHomeBoardLayout lay) => EmptyPinned(SurfacePlate.For(PlateKind.Outline));

    /// <summary>The empty row in the dashed outline plate the Recently played grid's history tile wears (one outline vocabulary).</summary>
    static Element EmptyPinned(SurfacePlate plate) => new BoxEl
    {
        Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.S),
        Corners = Radii.CardAll, BorderColor = plate.Stroke, BorderWidth = plate.StrokeWidth, BorderDashOn = plate.Dash, BorderDashOff = plate.Dash,
        Children =
        [
            Ui.Icon(Icons.Pin, WinUiHomeBoardRules.WidgetGlyph, Tok.TextSecondary) with { Shrink = 0f },
            Ui.Body(Loc.Get(Strings.Theme.Winui.Home.Board.EmptyPinned)).Secondary() with { Shrink = 1f, MinWidth = 0f },
        ],
    };

    /// <summary>The drop outline at the lifted tile's packed place.</summary>
    public static Element Slot(RectF rect) => new BoxEl
    {
        Key = HomeBoardKeys.Slot, Width = rect.W, Height = rect.H, Corners = Radii.CardAll,
        BorderColor = Tok.AccentDefault, BorderWidth = WinUiHomeBoardRules.RingW, BorderDashOn = WinUiHomeBoardRules.DashOn, BorderDashOff = WinUiHomeBoardRules.DashOff, HitTestVisible = false,
    };

    /// <summary>The open tile: the card becomes its own settings (the options body is the core's `Home.Tile.Options`, drawn by
    /// <see cref="WinUiHomeTileOptions"/>), lifted a little (the open card's stroke and shadow).</summary>
    public static Element Open(BoardEntry e, Element options) => new BoxEl
    {
        Direction = 1, Grow = 1f, MinWidth = 0f, Corners = Radii.CardAll,
        Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f, Shadow = Elevation.Flyout,
        Children = [options],
    };

    // ══ the card chrome ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A card around <paramref name="content"/>: the plate, the reserved ⋯ corner, focus, the keys and the context request. A full row has no
    /// plate (it is a chapter on the page, as today): its ⋯ stands in a lane at its header's end.</summary>
    static Element Card(in ViewContext ctx, HomeTileFrame f, Element content, string? key = null, bool moreInContent = false,
                        MoreMenu menu = MoreMenu.Full)
    {
        bool full = f.Form == HomeTileForm.Full;
        // A full row whose chapter has a header carries the menu button at the header's end (passed in by the template): the body keeps the
        // chapter's full width, so it lays out exactly as the page's chapter does (its first-frame estimates hold, nothing re-lays next frame).
        if (full && moreInContent)
            return new BoxEl { Key = key, Direction = 1, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [content] };
        Element more = menu == MoreMenu.None ? new BoxEl() : More(ctx, f, menu);
        string id = f.Entry.Id;
        var verbs = f.Verbs;
        // The card carries no context handler of its own: a touch hold is the grid wrapper's (it gates the lift, so the hold can become a
        // drag), and a right-click or the Menu key reaches the board, which opens the menu of the tile under the pointer or in focus
        // (`WinUiHomeBoard`).
        if (full)
            return new BoxEl
            {
                Key = key, Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Start, Gap = Spacing.XS,
                Children = [new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [content] }, more],
            };
        return new BoxEl
        {
            Key = key, Direction = 1, Grow = 1f, MinWidth = 0f, ZStack = true, Corners = Radii.CardAll, ClipToBounds = true,
            Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
            BrushTransitionMs = Design.Motion.Faster,
            Children =
            [
                content,
                // The ring around a tile just added: an accent outline for a moment (a bound visibility; nothing relayouts).
                new BoxEl
                {
                    Corners = Radii.CardAll, BorderColor = Tok.AccentDefault, BorderWidth = WinUiHomeBoardRules.RingW, HitTestVisible = false,
                    Visible = Prop.Of(() => f.View.RingId.Value == id),
                },
                // The reserved corner: a full-bleed layer whose row hugs the ⋯ at its top end (a Start layer fills the stack; its child hugs).
                new BoxEl
                {
                    Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Start,
                    Padding = new Edges4(0f, WinUiHomeBoardRules.MoreInset, WinUiHomeBoardRules.MoreInset, 0f), Children = [more],
                },
            ],
        };
    }

    /// <summary>Which menu a card's button opens: the full one, only the remove row (a tile whose thing is gone), or none at all (a placeholder of
    /// Spotify's feed: nothing on it can be acted on).</summary>
    internal enum MoreMenu : byte { Full, RemoveOnly, None }

    /// <summary>The card's menu button (its tools key).</summary>
    static Element More(in ViewContext ctx, HomeTileFrame f, MoreMenu menu = MoreMenu.Full)
        => Embed.Comp(new MoreProps(f, WinUiHomeBoardTiers.Of(ctx.Tier()).MoreBox) { Menu = menu }, static () => new TileMore())
            // A resize re-flows the tile's box by animation while its content lays out at the new width at once: the button rides the card's
            // edge by its own FLIP instead of jumping to it.
            with { Key = HomeBoardKeys.Tools(f.Entry.Id), Layout = Design.Reduced ? null : WinUiDiscoveryMotion.Reflow };

    /// <summary>A full row that is today's chapter: the menu button ends its header row (a headerless chapter keeps the lane).</summary>
    static Element FullChapter(in ViewContext ctx, HomeTileFrame f, Zone zone, WinUiHomeLayout home)
        => WinUiHomeZones.HasHeader(zone.Kind)
            ? Card(ctx, f, WinUiHomeZones.ChapterContent(zone, f.Overlay, home, More(ctx, f)), moreInContent: true)
            : Card(ctx, f, WinUiHomeZones.ChapterContent(zone, f.Overlay, home));

    /// <summary>A card's padded column.</summary>
    static BoxEl Pad(params Element[] kids) => new()
    {
        Direction = 1, Grow = 1f, MinWidth = 0f, Gap = Spacing.S,
        // The ⋯ lane is outside the card's content (a margin, not padding): the content's click target never runs under the tools.
        Padding = new Edges4(WinUiHomeBoardRules.TilePad, WinUiHomeBoardRules.TilePad, 0f, WinUiHomeBoardRules.TilePad),
        Margin = new Edges4(0f, 0f, WinUiHomeBoardRules.MoreReserve, 0f),
        Children = kids,
    };

    /// <summary>A row tile's line: the art, the name over one line (spec 4.2 `s`).</summary>
    static BoxEl RowFace(Element art, Func<string> title, Func<string> line, Element? end = null, bool pinned = false)
    {
        Element caption = Ui.Caption("").Secondary() with { Text = Prop.Of(line), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        var text = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
            Children =
            [
                Ui.BodyStrong("") with { Text = Prop.Of(title), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                pinned ? PinnedLine(caption) : caption,
            ],
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(WinUiHomeBoardRules.TilePad, 0f, 0f, 0f),
            Margin = new Edges4(0f, 0f, WinUiHomeBoardRules.MoreReserve, 0f),
            Children = end is null ? [art, text] : [art, text, end],
        };
    }

    /// <summary>A user pin's line, led by the pin mark (state at rest: "yours first, then Spotify's shortcuts", the concept's meta glyph).</summary>
    static Element PinnedLine(Element caption) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
        Children =
        [
            Ui.Icon(Icons.Pin, WinUiHomeBoardRules.PinMarkGlyph, Tok.AccentTextPrimary) with { Shrink = 0f },
            new BoxEl { Direction = 1, Shrink = 1f, MinWidth = 0f, Children = [caption] },
        ],
    };

    /// <summary>A widget head inside a card: a glyph, the title, a line.</summary>
    static Element WidgetHead(string glyph, string title, Func<string>? line) => new BoxEl
    {
        Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = WinUiHomeBoardRules.WidgetHeadH,
        Children =
        [
            Ui.Icon(glyph, WinUiHomeBoardRules.WidgetGlyph, Tok.AccentTextPrimary) with { Shrink = 0f },
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

    /// <summary>A glyph plate standing where a cover would (a widget's row tile).</summary>
    static Element GlyphPlate(string glyph, float edge)
        => Controls.IconPlate(glyph, edge, Tok.AccentSubtle, Tok.AccentTextPrimary);

    static Element Cover(string? url, float edge, bool round)
        => url is { Length: > 0 }
            ? new BoxEl { Shrink = 0f, Width = edge, Height = edge, Children = [Ui.Image(url, edge, edge, round ? edge * WinUiHomeBoardRules.Half : Radii.Control, Tok.FillSubtleSecondary)] }
            : new BoxEl { Width = edge, Height = edge, Shrink = 0f, Corners = round ? Radii.Circle(edge) : Radii.ControlAll, Fill = Tok.FillSubtleSecondary };

    // ══ Home.Tile.Pin ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTilePin"/>.</summary>
    public static Element Pin(HomePinTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        string? cover = p.Cover();
        Element Art(float edge) => p.CoverArt is { } drawn ? new BoxEl { Shrink = 0f, Width = edge, Height = edge, Children = [drawn(edge)] } : Cover(cover, edge, p.Round);
        switch (f.Form)
        {
            case HomeTileForm.S:
                return Card(ctx, f, Clickable(RowFace(Art(WinUiHomeBoardRules.RowArt), p.Name, p.Line, pinned: p.UserPin), p.Open));
            default:
            {
                bool wide = f.Form != HomeTileForm.M;
                float art = wide ? WinUiHomeBoardRules.WideArt : WinUiHomeBoardRules.CardArt;
                var head = new BoxEl
                {
                    Direction = (byte)(wide ? 0 : 1), Gap = Spacing.M, MinWidth = 0f, Grow = 1f,
                    Children =
                    [
                        Art(art),
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = Spacing.XXS,
                            Children =
                            [
                                Ui.BodyStrong("") with { Text = Prop.Of(p.Name), MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                                p.UserPin
                                    ? PinnedLine(Ui.Caption("").Secondary() with { Text = Prop.Of(p.Line), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f })
                                    : Ui.Caption("").Secondary() with { Text = Prop.Of(p.Line), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                                new BoxEl { Grow = 1f },
                                p.Play is { } play
                                    ? Button.Create(Loc.Get(Strings.Theme.Winui.Home.Board.Play), play, ButtonAppearance.Accent, ControlSize.Medium, Icons.Play) with { AlignSelf = FlexAlign.Start }
                                    : new BoxEl(),
                            ],
                        },
                    ],
                };
                return Card(ctx, f, Clickable(Pad(head), p.Open));
            }
        }
    }

    static BoxEl Clickable(BoxEl box, Action open) => box with
    {
        OnClick = open, Cursor = CursorId.Hand, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, Role = AutomationRole.Button,
    };

    // ══ Home.Tile.NowPlaying ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileNowPlaying"/>.</summary>
    public static Element NowPlaying(HomeNowPlayingTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        string title = Loc.Get(Strings.Theme.Winui.Home.Board.NowPlaying);
        Func<string> name = () => p.HasTrack() ? p.Title() : Loc.Get(Strings.Theme.Winui.Home.Board.NothingPlaying);
        var pp = new BoxEl
        {
            Shrink = 0f, Children = [Controls.IconAction(Icons.Play, p.PlayPause) with { Visible = Prop.Of(() => !p.Playing.Value) },
                                     Controls.IconAction(Icons.Pause, p.PlayPause) with { Visible = Prop.Of(() => p.Playing.Value) }],
        };
        if (f.Form == HomeTileForm.S)
            return Card(ctx, f, RowFace(Cover(p.Cover(), WinUiHomeBoardRules.RowArt, false), name, p.Line, pp));
        bool wide = f.Form != HomeTileForm.M;
        float art = wide ? WinUiHomeBoardRules.WideArt : WinUiHomeBoardRules.CardArt;
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
                Pad(new BoxEl { Direction = (byte)(wide ? 0 : 1), Gap = Spacing.M, Grow = 1f, MinWidth = 0f, Children = [Cover(p.Cover(), art, false), text] }),
                new BoxEl { Height = WinUiHomeBoardRules.ProgressH, Shrink = 0f, Direction = 1, Children = [p.ProgressLine()] },
            ],
        };
        return Card(ctx, f, body);
    }

    /// <summary>The lyric reel's line: the active line in the accent (a re-render per line, the reel's own signal).</summary>
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
        var rows = p.Rows();
        string title = Loc.Get(Strings.Theme.Winui.Home.Board.UpNext);
        string first = rows.Length > 0 ? rows[0].Data.Title : Loc.Get(Strings.Theme.Winui.Home.Board.QueueEmpty);
        string count = Strings.Theme.Winui.Home.Board.InQueue(n: rows.Length);
        if (f.Form == HomeTileForm.S)
            return Card(ctx, f, Clickable(RowFace(GlyphPlate(Icons.List, WinUiHomeBoardRules.RowArt), () => title, () => first), p.OpenQueue));
        int max = f.Form == HomeTileForm.M ? WinUiHomeBoardRules.CardRows : WinUiHomeBoardRules.WideRows;
        var kids = new List<Element> { WidgetHead(Icons.List, title, () => count) };
        for (int i = 0; i < rows.Length && i < max; i++)
            kids.Add(Controls.Surface(rows[i].Data, ListShape) with { Key = "upnext:" + rows[i].Key });
        if (rows.Length == 0) kids.Add(Ui.Caption(first).Secondary());
        return Card(ctx, f, Pad(kids.ToArray()));
    }

    static SurfaceShape ListShape => Shape.Row(WinUiHomeBoardRules.ListArt) with { MinHeight = WinUiHomeBoardRules.ListRowH };

    // ══ Home.Tile.Recents ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileRecents"/>.</summary>
    public static Element Recents(HomeRecentsTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var r = p.Recents;
        string title = Loc.Get(Strings.Sidebar.Section.RecentlyPlayed);
        string week = Strings.Home.PlaysThisWeek(r.Week.Total);
        switch (f.Form)
        {
            case HomeTileForm.Full:
            {
                var home = WinUiHomeTiers.Of(ctx.Tier());
                var body = new BoxEl
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Gap = home.HeaderGap,
                    Children =
                    [
                        WinUiHomeZones.ModuleHeader(r.Zone.Title ?? title, home.HeaderSubtitles ? r.Zone.Subtitle ?? Loc.Get(Strings.Home.RecentsSub) : null,
                            HyperlinkButton.Create(Loc.Get(Strings.Home.ListeningHistory), p.OpenHistory), home, More(ctx, f)),
                        ctx.View(ViewKeys.HomeZoneRecentGrid, r),
                    ],
                };
                return Card(ctx, f, body, moreInContent: true);
            }
            case HomeTileForm.S:
                return Card(ctx, f, Clickable(RowFace(GlyphPlate(Icons.History, WinUiHomeBoardRules.RowArt), () => title, () => week), p.OpenHistory));
            default:
            {
                int max = f.Form == HomeTileForm.M ? WinUiHomeBoardRules.CardRows : WinUiHomeBoardRules.WideRows;
                var kids = new List<Element> { WidgetHead(Icons.History, title, () => week) };
                for (int i = 0; i < r.Cells.Count && i < max; i++)
                {
                    var cell = r.Cells[i];
                    var c = cell.Card;
                    var data = new Controls.CardData(c.Uri, cell.Title, WinUiHomeZones.Meta(cell.Detail), c.ImageUrl,
                        () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c), Circular: cell.Round) { Menu = HomeCardNav.MenuOf(in c) };
                    kids.Add(Controls.Surface(data, ListShape) with { Key = "recents:" + (c.IsBlank ? i.ToString(CultureInfo.InvariantCulture) : c.Uri) });
                }
                return Card(ctx, f, Pad(kids.ToArray()));
            }
        }
    }

    // ══ Home.Tile.Releases ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileReleases"/>.</summary>
    public static Element Releases(HomeReleasesTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var zone = p.Zone;
        var home = WinUiHomeTiers.Of(ctx.Tier());
        if (f.Form == HomeTileForm.Full) return FullChapter(ctx, f, zone, home);
        string title = zone.Title ?? Loc.Get(Strings.Home.Zone.FromArtistsYouFollow);
        int max = f.Form == HomeTileForm.M ? WinUiHomeBoardRules.CardRows : WinUiHomeBoardRules.WideRows;
        var kids = new List<Element> { WidgetHead(Icons.RefineSparkle, title, null) };
        for (int i = 0; i < zone.Items.Count && i < max; i++) kids.Add(CardRow(zone.Items[i], i));
        return Card(ctx, f, Pad(kids.ToArray()));
    }

    static Element CardRow(HomeCard card, int i)
    {
        var c = card;
        var data = new Controls.CardData(c.Uri, c.Title, WinUiHomeZones.Meta(c.Subtitle ?? ""), c.ImageUrl,
            () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c)) { Menu = HomeCardNav.MenuOf(in c) };
        return Controls.Surface(data, ListShape) with { Key = "row:" + (c.IsBlank ? i.ToString(CultureInfo.InvariantCulture) : c.Uri) };
    }

    // ══ Home.Tile.Daylist ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileDaylist"/>.</summary>
    public static Element Daylist(HomeDaylistTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        if (f.Form == HomeTileForm.Full) return Card(ctx, f, p.Card);
        var card = p.Zone.Items.Count > 0 ? p.Zone.Items[0] : default;
        bool wide = f.Form != HomeTileForm.M;
        float art = wide ? WinUiHomeBoardRules.WideArt : WinUiHomeBoardRules.CardArt;
        var text = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
            Children =
            [
                Ui.Caption("").Secondary() with { Text = Prop.Of(p.Eyebrow), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                Ui.Subtitle(card.Title ?? "") with { MaxLines = wide ? 3 : 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                new BoxEl { Grow = 1f },
                p.Play is { } play
                    ? Button.Create(Loc.Get(Strings.Theme.Winui.Home.Board.Play), play, ButtonAppearance.Accent, ControlSize.Medium, Icons.Play) with { AlignSelf = FlexAlign.Start }
                    : new BoxEl(),
            ],
        };
        var row = new BoxEl { Direction = (byte)(wide ? 0 : 1), Gap = Spacing.M, Grow = 1f, MinWidth = 0f, Children = [Cover(card.ImageUrl, art, false), text] };
        return Card(ctx, f, Pad(row));
    }

    // ══ Home.Tile.Section ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileSection"/>: the full form is today's chapter; a card is the section's
    /// first covers and names, and opens the section.</summary>
    public static Element Section(HomeSectionTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var zone = p.Zone;
        var home = WinUiHomeTiers.Of(ctx.Tier());
        if (f.Form == HomeTileForm.Full) return FullChapter(ctx, f, zone, home);
        string title = zone.Title ?? f.Entry.Title;
        int max = f.Form == HomeTileForm.M ? WinUiHomeBoardRules.CardRows : WinUiHomeBoardRules.WideRows;
        var kids = new List<Element> { WidgetHead(Icons.Library, title, zone.Subtitle is { Length: > 0 } sub ? () => sub : null) };
        for (int i = 0; i < zone.Items.Count && i < max; i++) kids.Add(CardRow(zone.Items[i], i));
        var box = Pad(kids.ToArray());
        return Card(ctx, f, p.SeeAll is { } open ? Clickable(box, open) : box);
    }

    // ══ Home.Tile.Vacant ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileVacant"/>: a flat placeholder at the box while loading; a quiet line when the
    /// thing is gone, paused, offline or unknown. Never a pulse (owner rule 11-15).</summary>
    public static Element Vacant(HomeTileVacantProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        // Loading: a flat placeholder at the loaded size, derived from the real chapter (a shelf of blank cards under a title bar), never a pulse.
        if (p.Reason == HomeTileVacancy.Loading)
            return LoadingShelf(f, WinUiHomeTiers.Of(ctx.Tier()));
        string line = Loc.Get(p.Reason switch
        {
            HomeTileVacancy.Unavailable => Strings.Theme.Winui.Home.Board.Unavailable,
            HomeTileVacancy.Paused => Strings.Theme.Winui.Home.Board.Paused,
            HomeTileVacancy.Offline => Strings.Theme.Winui.Home.Board.Offline,
            HomeTileVacancy.Failed => Strings.Theme.Winui.Home.Board.Failed,
            _ => Strings.Theme.Winui.Home.Board.Unknown,
        });
        // The face per vacancy: a gone or unknown thing keeps its name and offers only its removal; Spotify's feed that failed or is offline says
        // so in its group's own words and offers nothing (the page's retry and the offline lane are the actions).
        bool feed = p.Reason is HomeTileVacancy.Failed or HomeTileVacancy.Offline;
        string name = p.Name.Length > 0 ? p.Name : Loc.Get(feed ? Strings.Theme.Winui.Home.Board.FeedName : Strings.Theme.Winui.Home.Board.Unavailable);
        string glyph = p.Reason switch
        {
            HomeTileVacancy.Offline => Icons.Globe,
            HomeTileVacancy.Failed => Icons.Warning,
            HomeTileVacancy.Paused => Icons.Pause,
            _ => Icons.Info,
        };
        var menu = feed ? MoreMenu.None : p.Reason == HomeTileVacancy.Paused ? MoreMenu.Full : MoreMenu.RemoveOnly;
        return Card(ctx, f, RowFace(GlyphPlate(glyph, WinUiHomeBoardRules.RowArt), () => name, () => line), menu: menu);
    }

    /// <summary>A placeholder of Spotify's feed while its answer is on its way: the chapter's own skeleton (a shelf of blank cards under a title
    /// bar) at the loaded size, flat.</summary>
    static Element LoadingShelf(HomeTileFrame f, WinUiHomeLayout home)
    {
        var blanks = new HomeCard[WinUiHomeBoardRules.SeedCards];
        for (int i = 0; i < blanks.Length; i++) blanks[i] = HomeCard.Blank(HomeCardKind.Playlist, i);
        var seed = new Zone(ZoneKind.CoverShelf, "seed:" + f.Entry.Id, " ", null, blanks);
        return new SkelRegionEl(
            Pending: static () => true, Failed: static () => false,
            Content: static () => new BoxEl(),
            ShimmerSource: () => WinUiHomeZones.ChapterContent(seed, null, home),
            OnFailed: static () => new BoxEl(),
            Reveal: SkelReveal.None, Style: SkeletonStyle.Default with { BarColor = Tok.FillControlSecondary }, Group: f.Entry, SmoothResize: false);
    }

    // ══ the ⋯ ════════════════════════════════════════════════════════════════════════════════════════════════════════

    internal sealed record MoreProps(HomeTileFrame Frame, float Box)
    {
        /// <summary>Which rows the menu holds.</summary>
        public MoreMenu Menu { get; init; }

        public bool Equals(MoreProps? o) => o is not null && Frame.Entry.Equals(o.Frame.Entry) && Frame.Form == o.Frame.Form && ReferenceEquals(Frame.Verbs, o.Frame.Verbs)
            && Box == o.Box && Menu == o.Menu;
        public override int GetHashCode() => Frame.Entry.GetHashCode();
    }

    /// <summary>The card's ⋯: the menu (spec 8.1) anchored to it.</summary>
    internal sealed class TileMore : Component
    {
        static readonly Dictionary<string, TileMore> s_live = new(StringComparer.Ordinal);
        NodeHandle _anchor;
        OverlayHandle? _menu;
        HomeTileFrame? _f;
        MoreMenu _menuKind;

        /// <summary>Open the menu of <paramref name="f"/>'s card (a context request).</summary>
        public static void OpenFor(HomeTileFrame f) => OpenFor(f.Entry.Id);

        /// <summary>Open the menu of the card of entry <paramref name="id"/> (the Menu key, Shift+F10 on the focused tile).</summary>
        public static bool OpenFor(string id)
        {
            if (!s_live.TryGetValue(id, out var m)) return false;
            m.Toggle();
            return true;
        }

        public override Element Render()
        {
            var p = UseProps<MoreProps>();
            _f = p.Frame;
            _menuKind = p.Menu;
            string id = p.Frame.Entry.Id;
            UseLayoutEffect(() =>
            {
                s_live[id] = this;
                return () => { if (s_live.TryGetValue(id, out var x) && ReferenceEquals(x, this)) s_live.Remove(id); };
            }, DepKey.From(id.GetHashCode()));
            return ToolTip.Wrap(Controls.IconAction(Icons.More, Toggle, requestsContext: false, size: p.Box) with
            {
                OnRealized = h => _anchor = h, Role = AutomationRole.Button,
            }, Loc.Get(Strings.Theme.Winui.Home.Board.More));
        }

        void Toggle()
        {
            if (_menu is { IsOpen: true } open) { open.Close(); return; }
            if (_f is not { } f || f.Overlay is not { } overlay) return;
            ToolTip.CloseOpen();
            var items = _menuKind == MoreMenu.RemoveOnly ? WinUiHomeTileMenu.RemoveOnly(f) : WinUiHomeTileMenu.Items(f);
            _menu = overlay.Open(() => _anchor, () => MenuFlyout.Create(items, () => _menu?.Close()), FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss));
            _menu.ClosedAction = () => _menu = null;
        }
    }
}
