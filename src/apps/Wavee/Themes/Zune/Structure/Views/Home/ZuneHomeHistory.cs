// ── Themes/Zune/Structure/Views/Home/ZuneHomeHistory.cs — Zune's `Home.Zone.RecentGrid`: the history group (themes plan 6.3; P5 Z5, ZT4) ─────────
//
// The Recents zone as Zune draws it wherever it stands (the pinned board's recently-played band, a chapter of its own on the music facets): a head in
// the group voice (22/28 light, lowercase) with "see all", over the plays: the art, the name and its line, the playing one in the accent with
// "playing" (or "playing on {device}") as its line. Compact draws the plays as ROWS of the shared media surface (44 art); the two arms are keyed
// alternatives that cross-fade on a crossing. The skeleton is the same tree at neutral live facts (no plays, no device).
//
// It was the quickplay strip's middle group; the strip itself is gone (the pinned board's Pinned group and its bands replaced it, lane Z).

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
using Wavee.Theming.Roles;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's history group.</summary>
internal static class ZuneHomeHistory
{
    static readonly Dictionary<string, long> s_noPlays = new(StringComparer.Ordinal);
    static readonly Func<string?> s_noDevice = static () => null;

    /// <summary>The registered template of <see cref="ViewKeys.HomeZoneRecentGrid"/>: the history group.</summary>
    public static Element HistoryTemplate(HomeRecentsProps props, in ViewContext ctx)
        => History(props.Cells, props.RemoteDeviceName, ZuneHomeTiers.Of(ctx.Tier()));

    /// <summary>The history group at neutral live facts (no plays, no device): the Recents host's skeleton proxy.</summary>
    internal static Element HistorySkeleton(Zone zone, ZuneHomeLayout lay)
    {
        var cells = RecentsCells.Of(zone.Items, s_noPlays, likedSongsCount: 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                    TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        return History(cells, s_noDevice, lay);
    }

    /// <summary>A group: its head (the quickplay voice) and link word over its <paramref name="body"/>.</summary>
    static BoxEl Group(string id, string head, Element? link, Element body) => new()
    {
        Key = H.QuickGroupPrefix + id, Direction = 1, Gap = H.QuickHeadGap, Shrink = 0f, MinWidth = 0f,
        // A group that wraps to the next line or back (the page narrowing beside a docking panel, a crossing) slides there; its box follows its tiles.
        Layout = Design.Reduced ? null : ZuneMotion.GroupResize,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = H.HeadInlineGap, MinWidth = 0f,
                Children = link is null ? [K.GroupText(head)] : [K.GroupText(head), link.Value],
            },
            body,
        ],
    };

    /// <summary>A group's tiles: ONE row where the groups stand side by side (a wrapping box takes the whole line it is measured on, so every group would
    /// fill the page and the strip would stack them: the owner's Home), a wrapping row only in the Compact column, <see cref="ZuneHomeLayout.QuickTileGap"/> apart.</summary>
    static BoxEl Tiles(Element[] tiles, ZuneHomeLayout lay) => new()
    {
        Direction = 0, Wrap = lay.QuickStacked, Gap = lay.QuickTileGap, MinWidth = 0f, Children = tiles,
    };

    /// <summary>A quickplay tile: the art, then up to two lines under it, the whole tile one click target.</summary>
    static BoxEl Tile(string key, float edge, Element art, Element name, Element? sub, Action open) => new()
    {
        Key = key, Direction = 1, Gap = H.TileCaptionGap, Width = edge, Shrink = 0f, MinWidth = 0f,
        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = open,
        Children = sub is null ? [art, name] : [art, name, sub.Value],
    };

    /// <summary>A tile: a crossing that changes its edge scales it there and slides it to its new place (<see cref="ZuneMotion.TileResize"/>).</summary>
    static Element Resizes(Element tile) => Design.Reduced ? tile : tile with { Layout = ZuneMotion.TileResize };

    // ── history ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element History(IReadOnlyList<RecentsCell> cells, Func<string?> remoteDevice, ZuneHomeLayout lay)
    {
        // The two arms are keyed alternatives in an unkeyed box (so they sit in a children list): a crossing cross-fades one into the other.
        Element arm = lay.HistoryRows ? HistoryRows(cells, remoteDevice, lay) : HistoryTiles(cells, remoteDevice, lay);
        return Group(H.HistoryGroup, Loc.Get(Strings.Theme.Zune.Discovery.History),
            K.Link("zune-home-history-link", Loc.Get(Strings.Home.SeeAll), HomeVerbs.OpenRecents, lay.LinkTarget),   // "see all" (Z-Frame-Home: history, see all)
            new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [arm] });
    }

    static Element HistoryTiles(IReadOnlyList<RecentsCell> cells, Func<string?> remoteDevice, ZuneHomeLayout lay)
    {
        int n = Math.Min(cells.Count, lay.QuickTiles);
        float edge = lay.QuickTileW;
        var tiles = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var cell = cells[i];
            var c = cell.Card;
            string uri = c.Uri;
            string line = cell.When.Length > 0 ? cell.When : cell.Detail;
            Element art = c.Kind == HomeCardKind.Liked
                ? Sidebar.Cover.Liked(edge)
                : Controls.Artwork(c.ImageUrl, edge, edge, cell.Round ? R.Shape.For(ShapeRole.Avatar) : R.Shape.For(ShapeRole.Cover));
            // The playing one speaks in the accent: two bound props (the name's ink, the line's words and ink), so a track change re-renders nothing.
            var name = K.Name(cell.Title) with { Color = Prop.Of(() => Controls.RelatesNow(uri) ? Tok.AccentTextPrimary : Tok.TextPrimary) };
            Element? sub = lay.QuickSubs ? PlayingLine(line, uri, remoteDevice) : null;
            tiles[i] = Resizes(Tile(c.IsBlank ? "zune-history-blank:" + i.ToString(CultureInfo.InvariantCulture) : "zune-history:" + uri, edge, art, name, sub,
                () => HomeCardNav.Open(in c)));
        }
        return Tiles(tiles, lay) with { Key = "zune-history-tiles", Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade };
    }

    /// <summary>Compact: the plays as rows of the shared media surface (44 art, the name, the line, the row's own "…"), the playing one in the accent.</summary>
    static Element HistoryRows(IReadOnlyList<RecentsCell> cells, Func<string?> remoteDevice, ZuneHomeLayout lay)
    {
        int n = Math.Min(cells.Count, lay.QuickTiles);
        var shape = Shape.Row(lay.ReleaseArt) with { MinHeight = lay.ReleaseRowH };
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var cell = cells[i];
            var c = cell.Card;
            string uri = c.Uri;
            string line = cell.When.Length > 0 ? cell.When : cell.Detail;
            var data = new Controls.CardData(uri, cell.Title, PlayingLine(line, uri, remoteDevice), c.ImageUrl,
                () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c), Circular: cell.Round, Drag: HomeCardNav.DragOf(in c),
                CoverOverride: c.Kind == HomeCardKind.Liked ? Sidebar.Cover.Liked(lay.ReleaseArt) : null)
            { Menu = HomeCardNav.MenuOf(in c) };
            rows[i] = new BoxEl
            {
                Key = c.IsBlank ? "zune-history-blank:" + i.ToString(CultureInfo.InvariantCulture) : "zune-history:" + uri,
                Direction = 1, MinWidth = 0f, Margin = ZuneListRules.OnColumn(in shape), Children = [Controls.Surface(data, shape)],
            };
        }
        return new BoxEl
        {
            Key = "zune-history-rows", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade, Children = rows,
        };
    }

    /// <summary>The history line under a name: the playing one says "playing" (or "playing on {device}") in the accent, the rest their line.</summary>
    static TextEl PlayingLine(string line, string uri, Func<string?> remoteDevice) => K.Sub(line) with
    {
        Text = Prop.Of(() => Controls.RelatesNow(uri) ? RecentsCells.NowCaption(remoteDevice()) : line),
        Color = Prop.Of(() => Controls.RelatesNow(uri) ? Tok.AccentTextPrimary : Tok.TextSecondary),
    };

}
