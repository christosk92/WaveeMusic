// ── Themes/Zune/Structure/Views/Home/ZuneHomeZones.cs — Zune's `Home.Zone.<Kind>` bodies and `Home.Charts` (themes plan 6.3; P5 Z5) ────────
//
// Board Z-Home (Standard) and Z-Home-Podcasts. A Zune zone body is the whole chapter's content: its heading row (the 26/32 light heading, the
// subtitle 14 after it in the tertiary 13 voice, "see all" at the end) 10 over the body. The pieces are the shared surfaces every page uses
// (`PagedShelf` + `HomeCards.ShelfCell`, `Controls.Surface` rows, `Episode.RowData`), drawn by Zune's own surface style (`StyleKeys.Surface`, Z3):
//
//   CoverShelf · MixedCovers · RadioShelf · ShowGrid   a shelf of 154 covers 16 apart (an artist's art round), chevrons in the heading row
//   WideTiles · VideoTiles                            a shelf of 16:9 tiles, the tier's columns across (4 at Standard), 16 apart
//   ReleaseList                                       64 rows of 56 art: the name, its kind line, the "when" 80 wide at the end
//   SpineShelf                                        the Spine Shelf (ZuneHomeZones.Shelf over Home/SpineShelf): flat spines, one open card
//   PodcastGroups                                     the tier's columns (2 at Standard, 24 apart) of a 28 title over three 48 rows of 40 art
//   BrowseTiles                                       flat 88 plates (the tier's columns, 12 apart) 2 : 1 beside the charts (`Home.Charts`)
//   EpisodeLead · EpisodeRows · ContinueEpisodes      the shared episode rows (72 floor, 56 art), one column (a pair puts two side by side)
//   LeadGrid · CompactGrid · ArtistCircles ·
//   CappedShelf                                       the Home rhythm templates (ZuneHomeZones.Rhythm.cs)
//   EmptyFacet                                        the app's one vacancy with the facet's own words
//
// Every body is a pure function of its zone (and the tier's record): the template and the page's skeleton proxy build the SAME tree (`Tree`).
// Each grid's column count is a live prop of ONE keyed node, so a crossing re-fits the same grid (never a key per count).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Home zone bodies.</summary>
internal static partial class ZuneHomeZones
{
    /// <summary>The registered template of every `Home.Zone.{Kind}` key.</summary>
    public static Element BodyTemplate(HomeZoneProps props, in ViewContext ctx)
        => Tree(props.Zone, props.Overlay, ZuneHomeTiers.Of(ctx.Tier()), ctx.Hooks);

    /// <summary>The body of <paramref name="zone"/>: what the template and the page's skeleton proxy both build. <paramref name="hooks"/> is the
    /// template's (null for the proxy): a row that shows what it showed last render is handed back as the same element (<see cref="ItemMemo"/>).</summary>
    internal static Element Tree(Zone zone, IOverlayService? overlay, ZuneHomeLayout lay, RenderContext? hooks = null) => zone.Kind switch
    {
        ZoneKind.CoverShelf or ZoneKind.MixedCovers or ZoneKind.RadioShelf or ZoneKind.ShowGrid => CoverShelf(zone, overlay, lay),
        ZoneKind.VideoTiles when IsOneEpisode(zone) => VideoCard(zone, lay),
        ZoneKind.WideTiles or ZoneKind.VideoTiles => WideShelf(zone, overlay, lay),
        ZoneKind.MoodPlates => MoodPlates(zone, lay, hooks),
        ZoneKind.BookShelf => BookShelf(zone, overlay, lay),
        ZoneKind.LeadGrid => LeadGrid(zone, overlay, lay),
        ZoneKind.CompactGrid => CompactGrid(zone, overlay, lay),
        ZoneKind.ArtistCircles => ArtistCircles(zone, overlay, lay),
        ZoneKind.CappedShelf => CappedShelf(zone, overlay, lay),
        ZoneKind.ReleaseList => ReleaseList(zone, lay, hooks),
        ZoneKind.SpineShelf => Shelf(zone, lay),
        ZoneKind.PodcastGroups => Clusters(zone, lay),
        ZoneKind.BrowseTiles => BrowseTiles(zone, lay),
        ZoneKind.EpisodeLead or ZoneKind.EpisodeRows or ZoneKind.ContinueEpisodes => Episodes(zone, lay),
        ZoneKind.EmptyFacet => EmptyFacet(zone),
        _ => new BoxEl(),
    };

    static IOverlayService? HostOf(IOverlayService? overlay) => Controls.IsNullOverlay(overlay) ? null : overlay;

    /// <summary>A zone's heading row: its title and subtitle, "see all" when the zone has a backing section.</summary>
    static BoxEl Heading(Zone zone, ZuneHomeLayout lay, string? fallbackTitle = null)
    {
        var seeAll = HomeVerbs.SeeAll(zone);
        Element? tools = seeAll is null ? null : K.Link("see-all:" + zone.Key, HomeVerbs.SeeAllLabel(zone), seeAll, lay.LinkTarget);
        if (zone.Header is { } entity && zone.Kind is not (ZoneKind.ReleaseList or ZoneKind.SpineShelf)) return EntityHeading(entity, tools, lay);
        return K.Heading(zone.Title ?? fallbackTitle ?? "", lay.Subtitles ? zone.Subtitle : null, tools);
    }

    // ── shelves ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly Func<float, float> s_coverHeight = static w => SurfaceGeometry.ShelfHeight(w, 1f, captionLines: 1, metaLine: false);
    static readonly Func<float, float> s_wideHeight = static w => SurfaceGeometry.ShelfHeight(w, R.Space.WideTileAspect, captionLines: 1, metaLine: false);

    /// <summary>A shelf of 154 covers 16 apart (Z-Home "made for you"): the heading row is the shelf's own header, its chevrons at the end.</summary>
    static Element CoverShelf(Zone zone, IOverlayService? overlay, ZuneHomeLayout lay)
    {
        var cards = ShelfLead.Merge(zone.Items, zone.Lead);
        var items = new HomeCards.ShelfItem[cards.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var c = cards[i];
            items[i] = HomeCards.ShelfItemOf(in c, HomeCards.PlainText(c.Subtitle), c.Kind == HomeCardKind.Artist);
        }
        var host = HostOf(overlay);
        return GridFill.Balanced(zone.Key, items.Length, lay.ShelfMinW, lay.ShelfCols > 0 ? H.WideMaxW : lay.ShelfCardW, lay.ShelfCols, lay.ShelfCols > 0 ? 0f : lay.ShelfCardW, H.ShelfGap,
            band2 => PagedShelf.Create<HomeCards.ShelfItem>(items,
            (item, i, cardW) => Cell(in item, cardW, 1f, host),
            onInvoke: static (item, _) => HomeCardNav.Open(item.Card),
            cardWidthBound: true,   // the surface binds the slot's live width: a slide re-lays the cards out, no slot re-renders
            cardHeight: s_coverHeight,
            header: Heading(zone, lay) with { Grow = H.HeadFillGrow },
            pager: ShelfPager.Chevrons | ShelfPager.WhenPaged,
            // Standard: the fixed 154 card. Elsewhere the shelf FITS: up to the tier's columns share the width, none narrower than the tier's floor
            // (one node at every tier: these are live props of the shelf).
            minCardW: lay.ShelfMinW, maxCardW: band2.MaxCardW, fixedCardW: lay.ShelfCols > 0 ? 0f : lay.ShelfCardW,
            maxColumns: band2.MaxColumns, gap: H.ShelfGap, headerGap: H.HeadGap,
            edgeFade: HomeModuleLayout.ShelfEdgeFade, snap: ShelfSnap.Page, offPageDim: HomeModuleLayout.OffPageDim,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight, parts: K.ChevronTargets,
            lift: ShelfLift.None,
            keyOf: static (item, i) => item.Card.IsBlank
                ? "zune-shelf-blank:" + i.ToString(CultureInfo.InvariantCulture)
                : "zune-shelf-card:" + item.Card.Uri)
            with { Key = "zune-home-shelf:" + zone.Key });
    }

    /// <summary>The wide tiles (Z-Home "new music"): 16:9 covers, the tier's columns across the page (one key at every count: a live prop).</summary>
    static Element WideShelf(Zone zone, IOverlayService? overlay, ZuneHomeLayout lay)
    {
        var cards = ShelfLead.Merge(zone.Items, zone.Lead);
        var items = new HomeCards.ShelfItem[cards.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var c = cards[i];
            items[i] = HomeCards.ShelfItemOf(in c, HomeCards.PlainText(c.Subtitle), circular: false, wideArt: c.VideoThumbUrl ?? c.HeaderImageUrl ?? c.ImageUrl);
        }
        var host = HostOf(overlay);
        return GridFill.Balanced("wide:" + zone.Key, items.Length, lay.WideMinW, H.WideMaxW, lay.WideCols, 0f, H.WideGap,
            band2 => PagedShelf.Create<HomeCards.ShelfItem>(items,
            (item, i, cardW) => Cell(in item, cardW, R.Space.WideTileAspect, host),
            onInvoke: static (item, _) => HomeCardNav.Open(item.Card),
            cardWidthBound: true,   // the surface binds the slot's live width: a slide re-lays the cards out, no slot re-renders
            cardHeight: s_wideHeight,
            header: Heading(zone, lay) with { Grow = H.HeadFillGrow },
            pager: ShelfPager.Chevrons | ShelfPager.WhenPaged,
            minCardW: lay.WideMinW, maxCardW: band2.MaxCardW, maxColumns: band2.MaxColumns, gap: H.WideGap, headerGap: H.HeadGap,
            edgeFade: HomeModuleLayout.ShelfEdgeFade, snap: ShelfSnap.Page, offPageDim: HomeModuleLayout.OffPageDim,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight, parts: K.ChevronTargets,
            lift: ShelfLift.None,
            keyOf: static (item, i) => item.Card.IsBlank
                ? "zune-wide-blank:" + i.ToString(CultureInfo.InvariantCulture)
                : "zune-wide-card:" + item.Card.Uri)
            with { Key = "zune-home-wide:" + zone.Key });
    }

    static Element Cell(in HomeCards.ShelfItem item, float cardW, float aspect, IOverlayService? host)
    {
        var c = item.Card;
        return HomeCards.ShelfCell(in item, cardW, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
                                   HomeCardNav.DragOf(in c), host, HomeCardNav.MenuOf(in c), aspect);
    }

    // ── the release list ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"From artists you follow": 64 rows of 56 art, the release's kind and artist under its name, the "when" at the end.</summary>
    static Element ReleaseList(Zone zone, ZuneHomeLayout lay, RenderContext? hooks)
    {
        var items = zone.Items;
        int n = Math.Min(items.Count, H.ReleaseRowsMax);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tz = TimeZoneInfo.Local;
        var culture = CultureInfo.CurrentCulture;
        var rows = new Element[n];
        var shape = Shape.Row(lay.ReleaseArt) with { MinHeight = lay.ReleaseRowH };
        for (int i = 0; i < n; i++)
        {
            var c = items[i];
            // Zune has no chips: the kind is the line's first word (Single, EP, Album), and a pre-save says "upcoming · 5 dec" in the accent where
            // the date stands.
            string type = ReleaseListRules.TypeLabel(c, upcomingFirst: false);
            string sub = HomeCards.PlainText(c.Subtitle);
            string date = ReleaseListRules.DateLabel(c, nowMs, tz, culture);
            bool upcoming = c.IsPreRelease;
            string when = upcoming ? (date.Length > 0 ? Loc.Get(Strings.Home.Release.Upcoming) + " · " + date : Loc.Get(Strings.Home.Release.Upcoming)) : date;
            var key = new ReleaseRowKey(c, i, c.Uri, c.Title, type.Length > 0 && sub.Length > 0 ? type + " · " + sub : type + sub, c.ImageUrl,
                                        when, shape, lay.ReleaseWhenW, upcoming);
            rows[i] = ItemMemo.Of(hooks, key, s_releaseRow);
        }
        return K.Section(Heading(zone, lay, Loc.Get(Strings.Home.Zone.FromArtistsYouFollow)),
            new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = rows });
    }

    /// <summary>Everything a release row shows: the card and its place (a blank's key), its strings and art, the tier's shape and "when" lane (the
    /// row's <see cref="ItemMemo"/> key).</summary>
    readonly record struct ReleaseRowKey(HomeCard Card, int Index, string Uri, string Title, string Line, string? Image, string When,
                                         SurfaceShape Shape, float WhenW, bool Upcoming = false);

    static readonly Func<ReleaseRowKey, Element> s_releaseRow = static k => ReleaseRow(in k);

    static Element ReleaseRow(in ReleaseRowKey k)
    {
        var c = k.Card;
        int index = k.Index;
        var shape = k.Shape;
        float whenW = k.WhenW;
        string line = k.Line, when = k.When;
        var data = new Controls.CardData(k.Uri, k.Title, line.Length > 0 ? K.Caption(line, cased: false) with { Color = Tok.TextSecondary } : null,
            k.Image, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c), Drag: HomeCardNav.DragOf(in c))
        {
            // Compact has no "when" lane (the board gives the row's end to its "…").
            Trailing = when.Length > 0 && whenW > 0f
                ? new BoxEl { Width = whenW, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
                              Children = [k.Upcoming ? K.Caption(when) with { Color = Tok.AccentTextPrimary } : K.Caption(when)] }
                : null,
            Menu = HomeCardNav.MenuOf(in c),
        };
        return new BoxEl
        {
            Key = c.IsBlank ? "zune-release-blank:" + index.ToString(CultureInfo.InvariantCulture) : "zune-release:" + k.Uri,
            Direction = 1, MinWidth = 0f, Margin = ZuneListRules.OnColumn(in shape), Children = [Controls.Surface(data, shape)],
        };
    }

    // ── the Browse tiles and the charts ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Browse tiles (flat plates in the category's colour) 2 : 1 beside the charts where the tier has room, the charts under them
    /// where it has not (the same two nodes; only the row's direction is a live prop).</summary>
    static Element BrowseTiles(Zone zone, ZuneHomeLayout lay)
    {
        var items = zone.Items;
        var tiles = new Element[items.Count];
        for (int i = 0; i < tiles.Length; i++)
        {
            var c = items[i];
            tiles[i] = K.Plate(c.IsBlank ? "zune-tile-blank:" + i.ToString(CultureInfo.InvariantCulture) : "zune-tile:" + c.Uri,
                c.Title, c.Accent, c.ImageUrl, lay.TileH, () => HomeCardNav.Open(in c), lay.TileBig);
        }
        var band = new ZuneCappedBand(H.TileMinCol, lay.TileCols, 0);
        Element grid = GridFill.Responsive("zune-home-tiles:" + zone.Key, tiles.Length, w => band.Columns(w, H.TileGap),
            (cols, w) => GridFill.Build(tiles, cols, w, H.TileGap, H.TileGap), band.MinCol * band.MaxCols + H.TileGap * (band.MaxCols - 1));
        bool beside = lay.ChartsBeside;
        return new BoxEl
        {
            Direction = (byte)(beside ? 0 : 1), Gap = beside ? lay.PairGap : lay.ZoneGap, MinWidth = 0f,
            AlignItems = beside ? FlexAlign.Start : FlexAlign.Stretch,
            Children =
            [
                K.Section(Heading(zone, lay), grid) with
                {
                    Key = "zune-home-tiles-section", Grow = beside ? H.TilesShare : 0f, Shrink = 1f, Basis = beside ? 0f : float.NaN,
                },
                new BoxEl
                {
                    Key = H.ChartsKey, Direction = 1, MinWidth = 0f, Grow = beside ? H.ChartsShare : 0f, Shrink = 1f, Basis = beside ? 0f : float.NaN,
                    Children = [Embed.Comp(static () => new ChartsBlockView()) with { Key = "zune-home-charts-host", SkeletonProxy = () => ChartsTree(ChartTilesModel.Fallback, lay) }],
                },
            ],
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.HomeCharts"/>: the numbered list (Z-Home "charts": 40 rows, the 22 light rank).</summary>
    public static Element ChartsTemplate(HomeChartsProps props, in ViewContext ctx) => ChartsTree(props.Model, ZuneHomeTiers.Of(ctx.Tier()));

    internal static Element ChartsTree(ChartTilesModel m, ZuneHomeLayout lay)
    {
        Element[] rows =
        [
            ChartRow("charts", 1, Loc.Get(Strings.Home.Charts), Loc.Get(Strings.Home.UpdatedDaily), ChartTileTarget.Fallback, lay),
            ChartRow("top50", 2, m.Top50.IsFallback ? ChartTilePick.Top50Prefix : m.Top50.Title, Loc.Get(Strings.Home.ChartsCaption.Top50Global), m.Top50, lay),
            ChartRow("viral50", 3, m.Viral50.IsFallback ? ChartTilePick.Viral50Prefix : m.Viral50.Title, Loc.Get(Strings.Home.ChartsCaption.Viral50Country), m.Viral50, lay),
        ];
        return K.Section(K.Heading(Loc.Get(Strings.Home.Charts), lay.Subtitles ? Loc.Get(Strings.Home.UpdatedDaily) : null, null),
            new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = rows });
    }

    static Element ChartRow(string key, int rank, string title, string caption, ChartTileTarget target, ZuneHomeLayout lay) => new BoxEl
    {
        Key = "zune-chart:" + key, Height = lay.ChartRowH, Direction = 0, AlignItems = FlexAlign.Center, Gap = H.ChartGap, MinWidth = 0f,
        Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = () => HomeVerbs.OpenChart(target),
        Children =
        [
            new BoxEl
            {
                Width = lay.ChartRankW, Shrink = 0f, Direction = 0,
                Children = [R.Type.Numeral(rank.ToString(CultureInfo.InvariantCulture)) with { Color = Tok.TextTertiary, MaxLines = 1 }],
            },
            R.Type.TrackTitle(title) with { Color = Tok.TextPrimary, HoverColor = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f },
            K.Caption(caption),
        ],
    };

    // ── the episodes ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>New episodes / continue listening (Z-Home-Podcasts): one column of the shared episode rows (its menu, its resume rung).</summary>
    static Element Episodes(Zone zone, ZuneHomeLayout lay)
    {
        var cards = ShelfLead.Merge(zone.Items, zone.Lead);
        var clock = Episode.RowClock.Now();
        var rows = new Element[cards.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = EpisodeRow(cards[i], i, clock);
        return K.Section(Heading(zone, lay), new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = rows });
    }

    static Element EpisodeRow(HomeCard e, int index, Episode.RowClock clock)
    {
        string uri = e.Uri;
        string show = e.ShowName is { Length: > 0 } s ? s : HomeCards.PlainText(e.Subtitle);
        var episode = e.Target.Kind == EntityKind.Episode ? new Episode(e.Target.Slot) : default;
        var facts = new Episode.RowFacts(uri, e.Title, show, e.ImageUrl, e.IsExplicit, e.HasVideo, e.ReleasedAtMs, e.DurationMs, e.ResumeMs);
        var options = new Episode.RowOptions(OnClick: () => HomeCardNav.Open(in e), OnPlay: () => HomeCardNav.Play(in e), Clock: clock);
        return new BoxEl
        {
            Key = e.IsBlank ? "zune-episode-blank:" + index.ToString(CultureInfo.InvariantCulture) : "zune-episode:" + uri,
            Direction = 1, MinWidth = 0f, Margin = ZuneListRules.OnColumn(in Shape.EpisodeRow),
            Children = [Controls.Surface(Episode.RowData(episode, in facts, in options), Shape.EpisodeRow)],
        };
    }

    // ── the empty facet ──────────────────────────────────────────────────────────────────────────────────────────────────

    static Element EmptyFacet(Zone zone)
    {
        var (title, body, action) = EmptyFacetCopy.Keys(zone.Key);
        return Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(title), subtitle: Loc.Get(body), actionLabel: Loc.Get(action),
            onAction: HomeVerbs.OpenBrowse);
    }
}
