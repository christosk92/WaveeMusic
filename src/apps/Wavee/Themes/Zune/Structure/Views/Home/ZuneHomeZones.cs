// ── Themes/Zune/Structure/Views/Home/ZuneHomeZones.cs — Zune's `Home.Zone.<Kind>` bodies and `Home.Charts` (themes plan 6.3; P5 Z5) ────────
//
// Board Z-Home (Standard) and Z-Home-Podcasts. A Zune zone body is the whole chapter's content: its heading row (the 26/32 light heading, the
// subtitle 14 after it in the tertiary 13 voice, "see all" at the end) 10 over the body. The pieces are the shared surfaces every page uses
// (`PagedShelf` + `HomeCards.ShelfCell`, `Controls.Surface` rows, `Episode.RowData`), drawn by Zune's own surface style (`StyleKeys.Surface`, Z3):
//
//   CoverShelf · MixedCovers · RadioShelf · ShowGrid   a shelf of 154 covers 16 apart (an artist's art round), chevrons in the heading row
//   WideTiles · VideoTiles                            a shelf of 16:9 tiles, the tier's columns across (4 at Standard), 16 apart
//   ReleaseList                                       64 rows of 56 art: the name, its kind line, the "when" 80 wide at the end
//   ClusterCards · PodcastGroups                      the tier's columns (2 at Standard, 24 apart) of a 28 title over three 48 rows of 40 art
//   BrowseTiles                                       flat 88 plates (the tier's columns, 12 apart) 2 : 1 beside the charts (`Home.Charts`)
//   EpisodeLead · EpisodeRows · ContinueEpisodes      the shared episode rows (72 floor, 56 art), one column (a pair puts two side by side)
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
internal static class ZuneHomeZones
{
    /// <summary>The registered template of every `Home.Zone.{Kind}` key.</summary>
    public static Element BodyTemplate(HomeZoneProps props, in ViewContext ctx) => Tree(props.Zone, props.Overlay, ZuneHomeTiers.Of(ctx.Tier()));

    /// <summary>The body of <paramref name="zone"/>: what the template and the page's skeleton proxy both build.</summary>
    internal static Element Tree(Zone zone, IOverlayService? overlay, ZuneHomeLayout lay) => zone.Kind switch
    {
        ZoneKind.CoverShelf or ZoneKind.MixedCovers or ZoneKind.RadioShelf or ZoneKind.ShowGrid => CoverShelf(zone, overlay, lay),
        ZoneKind.WideTiles or ZoneKind.VideoTiles => WideShelf(zone, overlay, lay),
        ZoneKind.ReleaseList => ReleaseList(zone, lay),
        ZoneKind.ClusterCards or ZoneKind.PodcastGroups => Clusters(zone, lay),
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
        Element? tools = seeAll is null ? null : K.Link("see-all:" + zone.Key, Loc.Get(Strings.Home.SeeAll), seeAll, lay.LinkTarget);
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
        return PagedShelf.Create<HomeCards.ShelfItem>(items,
            (item, i, cardW) => Cell(in item, cardW, 1f, host),
            onInvoke: static (item, _) => HomeCardNav.Open(item.Card),
            cardHeight: s_coverHeight,
            header: Heading(zone, lay) with { Grow = H.HeadFillGrow },
            pager: ShelfPager.Chevrons | ShelfPager.WhenPaged,
            // Standard: the fixed 154 card. Elsewhere the shelf FITS: up to the tier's columns share the width, none narrower than the tier's floor
            // (one node at every tier: these are live props of the shelf).
            minCardW: lay.ShelfMinW, maxCardW: lay.ShelfCols > 0 ? H.WideMaxW : lay.ShelfCardW, fixedCardW: lay.ShelfCols > 0 ? 0f : lay.ShelfCardW,
            maxColumns: lay.ShelfCols, gap: H.ShelfGap, headerGap: H.HeadGap,
            edgeFade: HomeModuleLayout.ShelfEdgeFade, snap: ShelfSnap.Page,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight, parts: K.ChevronTargets,
            lift: ShelfLift.None,
            keyOf: static (item, i) => item.Card.IsBlank
                ? "zune-shelf-blank:" + i.ToString(CultureInfo.InvariantCulture)
                : "zune-shelf-card:" + item.Card.Uri)
            with { Key = "zune-home-shelf:" + zone.Key };
    }

    /// <summary>The wide tiles (Z-Home "new music"): 16:9 covers, the tier's columns across the page (one key at every count: a live prop).</summary>
    static Element WideShelf(Zone zone, IOverlayService? overlay, ZuneHomeLayout lay)
    {
        var cards = ShelfLead.Merge(zone.Items, zone.Lead);
        var items = new HomeCards.ShelfItem[cards.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var c = cards[i];
            items[i] = HomeCards.ShelfItemOf(in c, HomeCards.PlainText(c.Subtitle), circular: false, wideArt: c.HeaderImageUrl ?? c.ImageUrl);
        }
        var host = HostOf(overlay);
        return PagedShelf.Create<HomeCards.ShelfItem>(items,
            (item, i, cardW) => Cell(in item, cardW, R.Space.WideTileAspect, host),
            onInvoke: static (item, _) => HomeCardNav.Open(item.Card),
            cardHeight: s_wideHeight,
            header: Heading(zone, lay) with { Grow = H.HeadFillGrow },
            pager: ShelfPager.Chevrons | ShelfPager.WhenPaged,
            minCardW: lay.WideMinW, maxCardW: H.WideMaxW, maxColumns: lay.WideCols, gap: H.WideGap, headerGap: H.HeadGap,
            edgeFade: HomeModuleLayout.ShelfEdgeFade, snap: ShelfSnap.Page,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight, parts: K.ChevronTargets,
            lift: ShelfLift.None,
            keyOf: static (item, i) => item.Card.IsBlank
                ? "zune-wide-blank:" + i.ToString(CultureInfo.InvariantCulture)
                : "zune-wide-card:" + item.Card.Uri)
            with { Key = "zune-home-wide:" + zone.Key };
    }

    static Element Cell(in HomeCards.ShelfItem item, float cardW, float aspect, IOverlayService? host)
    {
        var c = item.Card;
        return HomeCards.ShelfCell(in item, cardW, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
                                   HomeCardNav.DragOf(in c), host, HomeCardNav.MenuOf(in c), aspect);
    }

    // ── the release list ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"From artists you follow": 64 rows of 56 art, the release's kind and artist under its name, the "when" at the end.</summary>
    static Element ReleaseList(Zone zone, ZuneHomeLayout lay)
    {
        var items = zone.Items;
        int n = Math.Min(items.Count, H.ReleaseRowsMax);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tz = TimeZoneInfo.Local;
        var culture = CultureInfo.CurrentCulture;
        var rows = new Element[n];
        var shape = Shape.Row(lay.ReleaseArt) with { MinHeight = lay.ReleaseRowH };
        for (int i = 0; i < n; i++) rows[i] = ReleaseRow(items[i], i, nowMs, tz, culture, shape, lay.ReleaseWhenW);
        return K.Section(Heading(zone, lay, Loc.Get(Strings.Home.Zone.FromArtistsYouFollow)),
            new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = rows });
    }

    static Element ReleaseRow(HomeCard c, int index, long nowMs, TimeZoneInfo tz, CultureInfo culture, SurfaceShape shape, float whenW)
    {
        string type = ReleaseListRules.TypeLabel(c.Kind);
        string sub = HomeCards.PlainText(c.Subtitle);
        string line = type.Length > 0 && sub.Length > 0 ? type + " · " + sub : type + sub;
        string when = ReleaseListRules.DateLabel(c.ReleasedAtMs, nowMs, tz, culture);
        var data = new Controls.CardData(c.Uri, c.Title, line.Length > 0 ? K.Caption(line, cased: false) with { Color = Tok.TextSecondary } : null,
            c.ImageUrl, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c), Drag: HomeCardNav.DragOf(in c))
        {
            // Compact has no "when" lane (the board gives the row's end to its "…").
            Trailing = when.Length > 0 && whenW > 0f
                ? new BoxEl { Width = whenW, Shrink = 0f, Direction = 0, Justify = FlexJustify.End, Children = [K.Caption(when)] }
                : null,
            Menu = HomeCardNav.MenuOf(in c),
        };
        return new BoxEl
        {
            Key = c.IsBlank ? "zune-release-blank:" + index.ToString(CultureInfo.InvariantCulture) : "zune-release:" + c.Uri,
            Direction = 1, MinWidth = 0f, Margin = ZuneListRules.OnColumn(in shape), Children = [Controls.Surface(data, shape)],
        };
    }

    // ── the clusters ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Because you like": the tier's columns of clusters, each a 28 title over three 48 rows of 40 art (an artist round).</summary>
    static Element Clusters(Zone zone, ZuneHomeLayout lay)
    {
        var clusters = zone.Clusters;
        int n = clusters is null ? 0 : Math.Min(clusters.Count, H.ClusterMax);
        var cells = new Element[n];
        var rowShape = Shape.Row(lay.ClusterArt) with { MinHeight = lay.ClusterRowH };
        for (int i = 0; i < n; i++) cells[i] = Cluster(clusters![i], i, lay, rowShape);
        Element grid = new GridEl
        {
            Key = "zune-home-clusters:" + zone.Key, MinColWidth = H.ClusterMinCol, MaxColumns = lay.ClusterCols,
            ColGap = H.ClusterGap, RowGap = H.ClusterGap, Children = cells,
        };
        return K.Section(K.Heading(zone.Title ?? "", lay.Subtitles ? zone.Subtitle : null, null), grid);
    }

    static Element Cluster(ZoneCluster cluster, int index, ZuneHomeLayout lay, SurfaceShape rowShape)
    {
        string title = cluster.Over is { Length: > 0 } over ? over + " " + cluster.Name : cluster.Name;
        BoxEl head = new BoxEl
        {
            Height = lay.ClusterTitleH, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = [R.Type.DenseTitle(title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
        };
        if (cluster.Header is { } h)
        {
            var hc = h;
            head = head with { Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = () => HomeCardNav.Open(in hc) };
        }
        int n = Math.Min(cluster.Rows.Count, H.ClusterRows);
        var kids = new Element[n + 1];
        kids[0] = head;
        for (int i = 0; i < n; i++)
        {
            var c = cluster.Rows[i];
            string sub = HomeCards.PlainText(c.Subtitle);
            var data = new Controls.CardData(c.Uri, c.Title, sub.Length > 0 ? K.Caption(sub, cased: false) with { Color = Tok.TextSecondary } : null,
                c.ImageUrl, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
                Circular: c.Kind == HomeCardKind.Artist, Drag: HomeCardNav.DragOf(in c))
            { Menu = HomeCardNav.MenuOf(in c) };
            kids[i + 1] = new BoxEl
            {
                Key = c.IsBlank ? "zune-cluster-blank:" + i.ToString(CultureInfo.InvariantCulture) : "zune-cluster-row:" + c.Uri,
                Direction = 1, MinWidth = 0f, Margin = ZuneListRules.OnColumn(in rowShape), Children = [Controls.Surface(data, rowShape)],
            };
        }
        return new BoxEl
        {
            Key = "zune-cluster:" + index.ToString(CultureInfo.InvariantCulture) + ":" + cluster.Name,
            Direction = 1, Gap = H.ClusterRowGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = kids,
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
        Element grid = new GridEl
        {
            Key = "zune-home-tiles:" + zone.Key, MinColWidth = H.TileMinCol, MaxColumns = lay.TileCols, ColGap = H.TileGap, RowGap = H.TileGap,
            Children = tiles,
        };
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
