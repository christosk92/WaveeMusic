// ── Themes/Zune/Structure/Views/Home/ZuneHomeQuickplay.cs — Zune's `Home.Quickplay` and `Home.Zone.RecentGrid` (themes plan 6.3; P5 Z5, ZT4) ──
//
// Board Z-Home's first row: three quickplay GROUPS side by side, 32 apart, each a head in the quickplay voice (22/28 light, lowercase) with its
// link word, over a row of tiles 12 apart:
//
//   pins      the user's pins (the band's own read): 132 art (an artist round), the name under it; the name takes the accent while the pin is
//             the playing context; "edit" only where the structure has a place to edit them (none yet: `EditPins` is null)
//   history   the Recents zone's plays (`Home.Zone.RecentGrid`, through its core host): 100 art, the name and its line; the playing one in the
//             accent with "playing" (or "playing on {device}") as its line; "listening history" opens Recents
//   new       the release list's first releases: 100 art, the name and its artist; its link is the section's "see all"
//
// THE TIERS (`ZuneHomeLayout`, ZT4):
//   Huge          six 180 pins, four 140 plays and three 140 new, 16 apart, the groups 48 apart (Z-Frame-Huge)
//   Small         pins and history side by side, three 88 tiles each and the name alone, no new group (the zone below says it; Z-Frame-Small)
//   Compact       the groups in one COLUMN: four 80 pins, the history as three 56 ROWS (44 art, the name and its line, the row's "…" on hover),
//                 no new group (Z-Frame-Compact). The history's two arms (tiles, rows) are two keyed alternatives in an unkeyed box that cross-fade.
// The groups WRAP where the page is narrower than the row of them (a live layout, never a re-key), and a group's tiles wrap where it is narrower
// than they are. The history group is the SAME view wherever a Recents zone stands (the quickplay strip on All, a chapter of its own on the music
// facets). The skeleton is the same tree at neutral live facts: no pins (the page cannot read them), no plays.

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

/// <summary>Zune's quickplay strip and its history group.</summary>
internal static class ZuneHomeQuickplay
{
    static readonly Dictionary<string, long> s_noPlays = new(StringComparer.Ordinal);
    static readonly Func<string?> s_noDevice = static () => null;

    /// <summary>The registered template of <see cref="ViewKeys.HomeQuickplay"/>.</summary>
    public static Element Template(HomeQuickplayProps props, in ViewContext ctx)
    {
        var lay = ZuneHomeTiers.Of(ctx.Tier());
        var pins = props.Pins();   // tracked: a pin, an unpin or a move re-renders the strip
        return Strip(pins.Count > 0 && lay.QuickPins > 0 ? PinsGroup(pins, props.EditPins, lay) : null, props.History,
                     lay.QuickFresh > 0 && props.Fresh is { Items.Count: > 0 } fresh ? FreshGroup(fresh, lay) : null, lay);
    }

    /// <summary>The strip's skeleton: the same tree with no pins and the history at neutral live facts.</summary>
    internal static Element Skeleton(Zone? history, Zone? fresh, ZuneHomeLayout lay)
        => Strip(null, history is null ? null : HistorySkeleton(history, lay),
                 lay.QuickFresh > 0 && fresh is { Items.Count: > 0 } f ? FreshGroup(f, lay) : null, lay);

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

    // ── the strip ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The groups: a row that wraps (Small and up) or a column (Compact), ONE node whose direction is a live prop.</summary>
    static Element Strip(Element? pins, Element? history, Element? fresh, ZuneHomeLayout lay)
    {
        var kids = new List<Element>(3);
        if (pins is not null) kids.Add(pins);
        if (history is not null) kids.Add(history);
        if (fresh is not null) kids.Add(fresh);
        bool stacked = lay.QuickStacked;
        return new BoxEl
        {
            Key = "zune-home-quick-strip", Direction = (byte)(stacked ? 1 : 0), Wrap = !stacked && lay.QuickWrap, ClipToBounds = !stacked && !lay.QuickWrap,
            Gap = lay.QuickGroupGap, MinWidth = 0f,
            AlignItems = stacked ? FlexAlign.Stretch : FlexAlign.Start, Children = kids.ToArray(),
        };
    }

    /// <summary>A group: its head (the quickplay voice) and link word over its <paramref name="body"/>.</summary>
    static Element Group(string id, string head, Element? link, Element body) => new BoxEl
    {
        Key = H.QuickGroupPrefix + id, Direction = 1, Gap = H.QuickHeadGap, Shrink = 0f, MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = H.HeadInlineGap, MinWidth = 0f,
                Children = link is null ? [K.GroupText(head)] : [K.GroupText(head), link],
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
        Children = sub is null ? [art, name] : [art, name, sub],
    };

    // ── pins ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element PinsGroup(IReadOnlyList<BandPin> pins, Action? edit, ZuneHomeLayout lay)
    {
        int n = Math.Min(pins.Count, lay.QuickPins);
        var tiles = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var pin = pins[i];
            float corners = pin.Circular ? R.Shape.For(ShapeRole.Avatar) : R.Shape.For(ShapeRole.Cover);
            var name = K.Name(pin.Label) with
            {
                Color = Prop.Of(() => pin.Playing() ? Tok.AccentTextPrimary : Tok.TextPrimary),   // bound: a track change re-renders nothing
            };
            tiles[i] = Controls.Named(Tile(H.PinTilePrefix + pin.Id, lay.PinTileW,
                Controls.Artwork(pin.Image, lay.PinTileW, lay.PinTileW, corners), name, null, pin.Go), pin.Label);
        }
        Element? link = edit is null ? null : K.Link("zune-home-pins-edit", Loc.Get(Strings.Theme.Zune.Discovery.Edit), edit, lay.LinkTarget);
        return Group(H.PinsGroup, Loc.Get(Strings.Theme.Zune.Discovery.Pins), link, Tiles(tiles, lay));
    }

    // ── history ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element History(IReadOnlyList<RecentsCell> cells, Func<string?> remoteDevice, ZuneHomeLayout lay)
    {
        // The two arms are keyed alternatives in an unkeyed box (so they sit in a children list): a crossing cross-fades one into the other.
        Element arm = lay.HistoryRows ? HistoryRows(cells, remoteDevice, lay) : HistoryTiles(cells, remoteDevice, lay);
        return Group(H.HistoryGroup, Loc.Get(Strings.Theme.Zune.Discovery.History),
            K.Link("zune-home-history-link", Loc.Get(Strings.Home.ListeningHistory), HomeVerbs.OpenRecents, lay.LinkTarget),
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
            tiles[i] = Tile(c.IsBlank ? "zune-history-blank:" + i.ToString(CultureInfo.InvariantCulture) : "zune-history:" + uri, edge, art, name, sub,
                () => HomeCardNav.Open(in c));
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
                Direction = 1, MinWidth = 0f, Children = [Controls.Surface(data, shape)],
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

    // ── new ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element FreshGroup(Zone zone, ZuneHomeLayout lay)
    {
        int n = Math.Min(zone.Items.Count, lay.QuickFresh);
        float edge = lay.QuickTileW;
        var tiles = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var c = zone.Items[i];
            string sub = HomeCards.PlainText(c.Subtitle);
            tiles[i] = Tile(c.IsBlank ? "zune-new-blank:" + i.ToString(CultureInfo.InvariantCulture) : "zune-new:" + c.Uri, edge,
                Controls.Artwork(c.ImageUrl, edge, edge, R.Shape.For(ShapeRole.Cover)), K.Name(c.Title), lay.QuickSubs && sub.Length > 0 ? K.Sub(sub) : null,
                () => HomeCardNav.Open(in c));
        }
        var seeAll = HomeVerbs.SeeAll(zone);
        Element? link = seeAll is null ? null
            : K.Link("zune-home-new-link", zone.Title ?? Loc.Get(Strings.Home.Zone.FromArtistsYouFollow), seeAll, lay.LinkTarget);
        return Group(H.FreshGroup, Loc.Get(Strings.Theme.Zune.Discovery.New), link, Tiles(tiles, lay));
    }
}
