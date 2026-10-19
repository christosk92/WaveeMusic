// ── Themes/WinUi/Structure/Views/Discovery/Home/WinUiHomeZones.cs — WinUI's Home zone column, chapters and bodies (themes plan 6.2; P6 W5) ───
//
// Boards W-Home, W-Home2, W-Home-Compact / -Small / -Huge / -Huge4K. Nothing here is a card template or a hover recipe of its own: every zone body is composed
// from exactly the pieces the app's native pages compose — `PagedShelf` + `HomeCards.ShelfCell` + `HomeCardNav`, `Controls.Surface` rows (`Shape.Row*`) in a
// `GridEl`, stock `SettingsCard`, `Ui.Card`, `Controls.Vacancy`. The data pipeline (`Zone`, `ZoneCluster`, `RecentsCells`, `ReleaseListRules`, `ShelfLead`,
// `EpisodeCaption`) is core; every COUNT AND SIZE below is the content tier's `WinUiHomeLayout` (`Rules\Tiers.Discovery.cs`): the shelf's card band and column
// cap, the grids' column bands, the zone gap, and the two pairs Huge draws (the daylist 2 : 1 with the release list; Because you like 1 : 1 with Continue
// listening).
//
// ── THE CHAPTER SHAPE (one per zone) ──────────────────────────────────────────────────────────────────────────────────
//
//   BoxEl { Key = "zone:" + key, ScrollScope = key, Layout = Reflow }      — the SAME node at every tier: a width change FLIPs it (250 ms)
//   └─ BoxEl { Animate = Design.Entrance.Row(i) }                           — the entrance (its stagger) on the inner box
//      ├─ HEADER  [ title 20/28 over the subtitle 12/16 (Compact: the title alone) · tools: "See all" · divider · ‹ pips › ]
//      └─ BODY    HeaderGap (12; 8 below Standard) under it
//
// The headers are NOT sticky (W-Home2 pins the facet row alone); the page's pinned band cuts the content under it.
//
// ── THE SHELF PAGER LIVES IN THE HEADER (the engine's E4 external-controller shape) ───────────────────────────────────
//
// A cover shelf's chevrons and pips sit in the header's tools slot: the shelf is built `pager: ShelfPager.None, controller: c` and the header's tools are a
// `ShelfTools` leaf reading the same `ShelfController`'s four published signals. The controller must be reference-stable for the shelf's lifetime (PagedShelf
// freezes it at mount), so a shelf zone renders through the tiny `ShelfChapter` component that OWNS its controller; every other zone is a plain static tree.
//
// ── THE BODIES (the `Home.Zone.<Kind>` keys) ─────────────────────────────────────────────────────────────────────────────
//   Daylist                                   the daylist card host (Home.Daylist)                                         no header
//   RecentGrid                                the Jump back in host (Home.Zone.RecentGrid): tiles of 64 (48 art), the history tile   tools: Listening history
//   CoverShelf/MixedCovers/RadioShelf/ShowGrid  PagedShelf of HomeCards.ShelfCell (+ 2-span lead cell)                       tools: See all · ‹ pips ›
//   WideTiles/VideoTiles                      PagedShelf of 16:9 HomeCards.ShelfCell                                         tools: See all · ‹ pips ›
//   ReleaseList                               GridEl of Surface(Row) 56 high, the "New" chip and the when at the end         tools: See all
//   ClusterCards/PodcastGroups                GridEl of Ui.Card(avatar + name, then three Surface(Row(40)))
//   BrowseTiles                               GridEl of SettingsCard tiles + the Charts sub-block (Home.Charts)
//   EpisodeLead/EpisodeRows                   GridEl of Surface(Episode.RowData, EpisodeRow)
//   ContinueEpisodes                          GridEl of the same row + the resume rung (CardData.Below)
//   EmptyFacet                                Controls.Vacancy(Empty, ... -> Browse)                                         no header
//
// THE SKELETON. The deriver cannot run a component's Render — a bare `ComponentEl` derives to ONE bar — so every slot and host the column places carries a
// `SkeletonProxy` that is the SAME static builder its template runs (`BodyTree`, `WinUiHomeRecents.Skeleton`, `WinUiHomeDaylist.Skeleton`, the charts tree over
// its fallback model): the cold load shimmers as the header + the real cards.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's Home zone column.</summary>
internal static class WinUiHomeZones
{
    /// <summary>The registered template of every `Home.Zone.{Kind}` key whose body needs nothing but its zone.</summary>
    public static Element BodyTemplate(HomeZoneProps props, in ViewContext ctx)
        => BodyTree(props.Zone, props.Overlay, props.Controller, WinUiHomeTiers.Of(ctx.Tier()));

    // ══ 1. THE COLUMN ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every zone of <paramref name="m"/> as a chapter (`Design.Entrance.Row(i)` per zone), then the "Customize Home" link on the All facet
    /// (<paramref name="facet"/> empty). At Huge the daylist shares a row with the release list (2 : 1, W-Home-Huge) and "Because you like" with "Continue
    /// listening" (1 : 1): each pair stands at its first member's place and the second leaves its own.</summary>
    public static Element Column(ScreenModel m, string facet, IOverlayService? overlay, WinUiHomeLayout layout)
    {
        var zones = m.Zones;
        bool all = facet.Length == 0;
        var kids = new List<Element>(zones.Count + 1);

        int release = layout.DaylistBeside ? IndexOf(zones, ZoneKind.ReleaseList) : -1;
        int cluster = layout.ClustersBeside ? IndexOfCluster(zones) : -1;
        int cont = layout.ClustersBeside ? IndexOf(zones, ZoneKind.ContinueEpisodes) : -1;
        bool pairClusters = cluster >= 0 && cont >= 0;
        bool pairHero = release >= 0 && IndexOf(zones, ZoneKind.Daylist) >= 0;

        for (int i = 0; i < zones.Count; i++)
        {
            var zone = zones[i];
            if (pairHero && zone.Kind == ZoneKind.Daylist)
            {
                kids.Add(Beside(zone, zones[release], i, overlay, layout, share: WinUiHomeTiers.HeroShare, otherShare: 1f));
                continue;
            }
            if (pairHero && i == release) continue;
            if (pairClusters && i == Math.Min(cluster, cont))
            {
                kids.Add(Beside(zones[cluster], zones[cont], i, overlay, layout, share: 1f, otherShare: 1f));
                continue;
            }
            if (pairClusters && (i == cluster || i == cont)) continue;
            kids.Add(Chapter(zone, i, overlay, layout));
        }
        if (all)
            kids.Add(new BoxEl
            {
                Key = "customize", Direction = 0, MinWidth = 0f, Animate = Design.Entrance.Row(zones.Count),
                Children = [HyperlinkButton.Create(Loc.Get(Strings.Home.Customizer.Title), HomeVerbs.OpenCustomize)],
            });
        return new BoxEl
        {
            Direction = 1, Gap = layout.ZoneGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = kids.ToArray(),
        };
    }

    static int IndexOf(IReadOnlyList<Zone> zones, ZoneKind kind)
    {
        for (int i = 0; i < zones.Count; i++) if (zones[i].Kind == kind) return i;
        return -1;
    }

    static int IndexOfCluster(IReadOnlyList<Zone> zones)
    {
        for (int i = 0; i < zones.Count; i++) if (zones[i].Kind is ZoneKind.ClusterCards or ZoneKind.PodcastGroups) return i;
        return -1;
    }

    /// <summary>Two chapters as ONE row (a Huge pair): the first takes <paramref name="share"/> parts, the second <paramref name="otherShare"/>, 24 apart.</summary>
    static Element Beside(Zone a, Zone b, int index, IOverlayService? overlay, WinUiHomeLayout layout, float share, float otherShare) => new BoxEl
    {
        Key = "zone:" + a.Key + "+" + b.Key, Direction = 0, Gap = WinUiHomeTiers.HeroGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
        Layout = WinUiDiscoveryMotion.Reflow,
        Children =
        [
            Chapter(a, index, overlay, layout) with { Grow = share, Shrink = 1f, Basis = 0f },
            Chapter(b, index + 1, overlay, layout) with { Grow = otherShare, Shrink = 1f, Basis = 0f },
        ],
    };

    /// <summary>One zone's chapter box: the keyed scope carrying the entrance. A shelf zone mounts <see cref="WinUiHomeShelfChapter"/> (it owns the pager
    /// controller); everything else is a plain tree. The skeleton deriver cannot run a component's Render, so the shelf chapter hands it a
    /// <see cref="ComponentEl.SkeletonProxy"/>: the SAME static <c>Chapter(zone, overlay, controller)</c> its Render calls, with no controller.</summary>
    static BoxEl Chapter(Zone zone, int index, IOverlayService? overlay, WinUiHomeLayout layout)
    {
        Element content = IsShelf(zone.Kind)
            ? Embed.Comp(new ShelfChapterProps(zone, overlay, layout), static () => new WinUiHomeShelfChapter())
                with { SkeletonProxy = () => Chapter(zone, overlay, controller: null, layout) }
            : Chapter(zone, overlay, controller: null, layout);
        return new BoxEl
        {
            Key = "zone:" + zone.Key, Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            ScrollScope = zone.Key,
            // A width change moves and resizes the SAME zone: it FLIPs into its new box. The entrance (its stagger) rides the inner box.
            Layout = WinUiDiscoveryMotion.Reflow,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Animate = Design.Entrance.Row(index),
                    Children = [content],
                },
            ],
        };
    }

    static bool IsShelf(ZoneKind kind) => kind is ZoneKind.CoverShelf or ZoneKind.MixedCovers or ZoneKind.RadioShelf
        or ZoneKind.ShowGrid or ZoneKind.WideTiles or ZoneKind.VideoTiles;

    /// <summary>Header + body, or the bare body for a headerless zone.</summary>
    internal static Element Chapter(Zone zone, IOverlayService? overlay, ShelfController? controller, WinUiHomeLayout layout)
    {
        Element body = Body(zone, overlay, controller, layout);
        Element? header = Header(zone, controller is not null && ShelfBandOf(zone.Kind, layout).Pager ? controller : null, layout);
        if (header is null) return body;
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Gap = layout.HeaderGap,
            Children = [header, body],
        };
    }

    /// <summary>The chapter header: the zone's title (20/28 display) over its subtitle (12/16 secondary; none at Compact) and the tools at the end ("See all" /
    /// "Listening history", then the shelf pager). Null for the headerless kinds (Daylist, EmptyFacet).</summary>
    static Element? Header(Zone zone, ShelfController? controller, WinUiHomeLayout layout)
    {
        bool sub = layout.HeaderSubtitles;
        switch (zone.Kind)
        {
            case ZoneKind.Daylist or ZoneKind.EmptyFacet:
                return null;
            case ZoneKind.RecentGrid:
                return ModuleHeader(
                    zone.Title ?? Loc.Get(Strings.Sidebar.Section.RecentlyPlayed),
                    sub ? zone.Subtitle ?? Loc.Get(Strings.Home.RecentsSub) : null,
                    HyperlinkButton.Create(Loc.Get(Strings.Home.ListeningHistory), HomeVerbs.OpenRecents), layout);
            case ZoneKind.ReleaseList:
                return ModuleHeader(zone.Title ?? Loc.Get(Strings.Home.Zone.FromArtistsYouFollow),
                                    sub ? zone.Subtitle ?? Loc.Get(Strings.Home.ReleasedThisWeek) : null,
                                    ShelfHeaderTools(HomeVerbs.SeeAll(zone), null), layout);
            default:
                return ModuleHeader(zone.Title ?? "", sub ? zone.Subtitle : null, ShelfHeaderTools(HomeVerbs.SeeAll(zone), controller), layout);
        }
    }

    /// <summary>A chapter header (W-Home): the title's line over the subtitle's, the tools at the end. A non-drillable label: no chevron, no click.</summary>
    internal static Element ModuleHeader(string title, string? subtitle, Element? tools, WinUiHomeLayout layout)
    {
        var kids = new List<Element>(2)
        {
            R.Type.ModuleHeader(title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        };
        if (subtitle is { Length: > 0 })
            kids.Add(Ui.Caption(subtitle).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        return new BoxEl
        {
            Direction = 0, Gap = WinUiHomeRules.HeaderGap, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = layout.HeaderToolsH,
            Children =
            [
                new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = kids.ToArray() },
                tools ?? new BoxEl(),
            ],
        };
    }

    /// <summary>A shelf header's tools: the "See all" link (when the zone has a backing section), then — while the shelf pages — a 16-px vertical divider and the
    /// pager (‹ pips ›). Null when the header has neither.</summary>
    static Element? ShelfHeaderTools(Action? seeAll, ShelfController? controller)
    {
        Element? link = seeAll is null ? null : HyperlinkButton.Create(Loc.Get(Strings.Home.SeeAll), seeAll) with { Shrink = 0f };
        bool leadingDivider = link is not null;
        Element? pager = controller is null ? null
            : Embed.Comp(() => new WinUiHomeShelfTools(controller, leadingDivider)) with { Key = leadingDivider ? "tools+link" : "tools" };
        if (link is null) return pager;
        if (pager is null) return link;
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [link, pager],
        };
    }

    // ══ 2. THE BODIES ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A zone's body alone (no header). <paramref name="controller"/> is the shelf pager's external controller when the header owns the pips/chevrons;
    /// null lets a shelf keep its own built-in pager. The daylist and the Recents grid mount their core hosts (they read core state); every other kind is its
    /// `Home.Zone.{Kind}` slot.</summary>
    static Element Body(Zone zone, IOverlayService? overlay, ShelfController? controller, WinUiHomeLayout layout) => zone.Kind switch
    {
        ZoneKind.Daylist => Embed.Comp(new DaylistProps(zone, overlay), static () => new DaylistCard())
            with { SkeletonProxy = () => WinUiHomeDaylist.Skeleton(layout) },
        // The proxy is the view's own static tree at neutral live facts: a cold load shimmers as the tile grid, not as one default bar.
        ZoneKind.RecentGrid => Embed.Comp(new RecentGridProps(zone), static () => new RecentGrid())
            with { SkeletonProxy = () => WinUiHomeRecents.Skeleton(zone, layout) },
        _ => ZoneSlot(zone, overlay, controller, layout),
    };

    /// <summary>The kind's `Home.Zone.{Kind}` slot over the zone, its proxy the same static tree the template builds.</summary>
    static Element ZoneSlot(Zone zone, IOverlayService? overlay, ShelfController? controller, WinUiHomeLayout layout)
    {
        if (!HomeZoneKeys.TryKeyOf(zone.Kind, out var key)) return new BoxEl();
        Element slot = Slots.View(key, new HomeZoneProps(zone, overlay, controller));
        return slot is ComponentEl component ? component with { SkeletonProxy = () => BodyTree(zone, overlay, controller: null, layout) } : slot;
    }

    /// <summary>The picture of every kind that is a pure function of its zone: what the `Home.Zone.{Kind}` template and its skeleton proxy both build.</summary>
    internal static Element BodyTree(Zone zone, IOverlayService? overlay, ShelfController? controller, WinUiHomeLayout layout) => zone.Kind switch
    {
        ZoneKind.CoverShelf or ZoneKind.MixedCovers or ZoneKind.RadioShelf or ZoneKind.ShowGrid
            or ZoneKind.WideTiles or ZoneKind.VideoTiles => Shelf(zone, overlay, controller, layout),
        ZoneKind.ReleaseList => ReleaseList(zone, layout),
        ZoneKind.ClusterCards or ZoneKind.PodcastGroups => ClusterGrid(zone, layout),
        ZoneKind.BrowseTiles => BrowseTiles(zone, layout),
        ZoneKind.EpisodeLead or ZoneKind.EpisodeRows => EpisodeGrid(zone, layout.Episodes),
        ZoneKind.ContinueEpisodes => EpisodeGrid(zone, layout.Continue),
        ZoneKind.EmptyFacet => EmptyFacet(zone),
        _ => new BoxEl(),
    };

    static IOverlayService? HostOf(IOverlayService? overlay) => Controls.IsNullOverlay(overlay) ? null : overlay;

    // ── 2.1 cover shelves: PagedShelf of the house shelf cell ────────────────────────────────────────────────────────────────

    /// <summary>Three shapes of ONE shelf. A WIDE shelf (WideTiles/VideoTiles) is 16:9 cells — the card's header image (or its cover) through
    /// <c>CardData.CoverAspect</c>, an owner · count meta line, the wide min/max card widths. A COVER shelf whose lead carries a header image gets PagedShelf's
    /// 2-span lead cell: the lead is <c>ShelfLead.LeadAspect</c> wide so its cover is exactly as tall as the squares beside it; the template follows the cell,
    /// so a page too narrow for the span mounts the lead as a plain square (the engine's rule, never ours). Everything else is the square cell, verbatim.</summary>
    static Element Shelf(Zone zone, IOverlayService? overlay, ShelfController? controller, WinUiHomeLayout layout)
    {
        bool wide = zone.Kind is ZoneKind.WideTiles or ZoneKind.VideoTiles;
        var band = ShelfBandOf(zone.Kind, layout);
        var cards = ShelfLead.Merge(zone.Items, zone.Lead);   // lead at index 0, exactly once
        bool leadWide = layout.ShelfLead && !wide && zone.Lead is { } lead && cards.Count > 0 && lead.HeaderImageUrl is { Length: > 0 };
        var items = new HomeCards.ShelfItem[cards.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var c = cards[i];
            string second = HomeCards.PlainText(c.Subtitle);
            // Only an artist is a disc. A radio station's art is Spotify's own square design (its circles are drawn IN the image).
            bool circular = c.Kind == HomeCardKind.Artist;
            items[i] = wide
                ? HomeCards.ShelfItemOf(in c, second, circular: false, wideArt: c.HeaderImageUrl ?? c.ImageUrl,
                                        meta: CardMeta.Of(c.OwnerName, c.TrackCount))
                : leadWide && i == 0
                    ? HomeCards.ShelfItemOf(in c, second, circular: false, wideArt: c.HeaderImageUrl, captionLines: 2,
                                            meta: CardMeta.Of(null, c.TrackCount), metaInline: true)
                    : HomeCards.ShelfItemOf(in c, second, circular);
        }
        var host = HostOf(overlay);

        Element Cell(in HomeCards.ShelfItem item, float cellW, float coverAspect)
        {
            var c = item.Card;
            return HomeCards.ShelfCell(in item, cellW, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
                                       HomeCardNav.DragOf(in c), host, HomeCardNav.MenuOf(in c), coverAspect);
        }

        float squareAspect = wide ? R.Space.WideTileAspect : 1f;
        Func<float, float> cardHeight = wide ? s_wideCardHeight : CoverCardHeight(HomeCards.CaptionLinesOf(items));

        return PagedShelf.Create<HomeCards.ShelfItem>(items,
            (item, i, cardW) => Cell(in item, cardW, squareAspect),
            onInvoke: static (item, _) => HomeCardNav.Open(item.Card),   // the slot owns the click: the card's own open, the lead cell's too
            cardHeight: cardHeight,
            // The header owns the pager (the controller) where the tier pages the shelf; a tier that does not shows none.
            pager: controller is null ? (band.Pager ? ShelfPager.Chevrons | ShelfPager.Pips : ShelfPager.None) : ShelfPager.None,
            controller: controller,
            minCardW: band.MinCardW, maxCardW: band.MaxCardW, maxColumns: band.MaxColumns, fixedCardW: band.FixedCardW,
            gap: band.Gap, edgeFade: HomeModuleLayout.ShelfEdgeFade, snap: ShelfSnap.Page,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,   // the shared card hovers fill-only: no lift halo to reserve clearance for
            keyOf: static (item, i) => item.Card.IsBlank
                ? "home-shelf-blank:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "home-shelf-card:" + item.Card.Uri,
            // The lead span is a LIVE prop (it follows whether THIS lead has a header image); the minimum column count is frozen mount configuration.
            leadSpan: leadWide ? WinUiHomeRules.LeadSpan : 1,
            leadCardAt: leadWide
                ? (item, i, leadW) => Cell(in item, leadW, ShelfLead.LeadAspect(leadW, band.Gap, WinUiHomeRules.LeadSpan, Spacing.S))
                : null,
            leadMinColumns: wide ? 0 : WinUiHomeRules.LeadMinColumns)
           // ONE key at every tier: the card band, the column cap, the pager and the card height are live props, so a width change re-fits the SAME shelf
           // (its cards, its page and the header's controller survive) instead of remounting it.
           with { Key = "home-shelf:" + zone.Key };
    }

    /// <summary>The shelf band of a zone kind in <paramref name="layout"/>: the wide tiles' or the covers'.</summary>
    internal static WinUiShelfBand ShelfBandOf(ZoneKind kind, WinUiHomeLayout layout)
        => kind is ZoneKind.WideTiles or ZoneKind.VideoTiles ? layout.WideShelf : layout.Shelf;

    // The shelf extents as cached delegates (one per shape, never a per-render closure).
    static readonly Func<float, float> s_wideCardHeight =
        static w => SurfaceGeometry.ShelfHeight(w, R.Space.WideTileAspect, captionLines: 1, metaLine: true);
    static readonly Func<float, float> s_coverCardHeight1 = static w => SurfaceGeometry.ShelfHeight(w, 1f, captionLines: 1, metaLine: false);
    static readonly Func<float, float> s_coverCardHeight2 = static w => SurfaceGeometry.ShelfHeight(w, 1f, captionLines: 2, metaLine: false);

    static readonly Dictionary<int, Func<float, float>> s_coverCardHeightN = new();

    static Func<float, float> CoverCardHeight(int captionLines)
    {
        if (captionLines <= 1) return s_coverCardHeight1;
        if (captionLines == 2) return s_coverCardHeight2;
        if (!s_coverCardHeightN.TryGetValue(captionLines, out var height))
            s_coverCardHeightN[captionLines] = height = w => SurfaceGeometry.ShelfHeight(w, 1f, captionLines, metaLine: false);
        return height;
    }

    /// <summary>The shelf pager in the header's tools slot: ‹ pips › — the stock pips between two stock chevrons, every action routed back through the controller.
    /// Reads the controller's four value signals, so only this leaf re-renders on a page change. <paramref name="leadingDivider"/> puts the 16-px vertical
    /// divider between the header's "See all" link and the pager — it comes and goes WITH the pager, so a one-page shelf shows the bare link.</summary>
    sealed class WinUiHomeShelfTools(ShelfController controller, bool leadingDivider) : Component
    {
        public override Element Render()
        {
            int count = controller.PageCount.Value;
            int page = controller.Page.Value;
            bool canPrev = controller.CanPrev.Value;
            bool canNext = controller.CanNext.Value;
            if (count <= 1) return new BoxEl();
            var kids = new List<Element>(4);
            if (leadingDivider) kids.Add(ToolsDivider());
            kids.Add(IconButton.Create(Icons.ChevronLeft, controller.Prev, isEnabled: canPrev));
            kids.Add(PipsPager.Controlled(count, page, controller.GoTo));
            kids.Add(IconButton.Create(Icons.ChevronRight, controller.Next, isEnabled: canNext));
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids.ToArray(),
            };
        }
    }

    /// <summary>The 16-px vertical hairline between a header's link and its pager.</summary>
    static Element ToolsDivider()
        => new BoxEl { Width = 1f, Height = WinUiHomeRules.ToolsDividerH, Shrink = 0f, Fill = Tok.StrokeDividerDefault };

    /// <summary>Props for <see cref="WinUiHomeShelfChapter"/>: the zone by value (a re-plan with equal content is a no-op at the embed boundary); the overlay
    /// service by reference.</summary>
    internal sealed record ShelfChapterProps(Zone Zone, IOverlayService? Overlay, WinUiHomeLayout Layout)
    {
        public bool Equals(ShelfChapterProps? o) => o is not null && Zone.Equals(o.Zone) && ReferenceEquals(Overlay, o.Overlay) && Layout.Equals(o.Layout);
        public override int GetHashCode() => Zone.GetHashCode();
    }

    /// <summary>A shelf zone's chapter: owns the ONE <see cref="ShelfController"/> its header's pager and its shelf share (a plain field — reference-stable for
    /// the shelf's lifetime, per PagedShelf's contract). Its embed's skeleton proxy is the same static chapter builder without the controller.</summary>
    internal sealed class WinUiHomeShelfChapter : Component
    {
        readonly ShelfController _controller = new();

        public override Element Render()
        {
            var p = UseProps<ShelfChapterProps>();
            return Chapter(p.Zone, p.Overlay, _controller, p.Layout);
        }
    }

    // ── 2.3 release list: "From artists you follow" — rows of 56, two columns (one beside the daylist at Huge) ───────────────────

    static Element ReleaseList(Zone zone, WinUiHomeLayout layout)
    {
        var items = zone.Items;
        int n = Math.Min(items.Count, WinUiHomeRules.ReleaseRowsMax);
        if (n == 0) return new BoxEl();

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tz = TimeZoneInfo.Local;
        var culture = System.Globalization.CultureInfo.CurrentCulture;

        var rows = new Element[n];
        for (int i = 0; i < n; i++) rows[i] = ReleaseRow(items[i], nowMs, tz, culture);
        return new GridEl
        {
            MinColWidth = layout.Releases.Floor, MaxColumns = layout.Releases.MaxCols,
            ColGap = WinUiHomeRules.ReleaseColGap, RowGap = WinUiHomeRules.ReleaseRowGap, RowHeight = WinUiHomeRules.ReleaseRowH, Children = rows,
        };
    }

    static Element ReleaseRow(HomeCard c, long nowMs, TimeZoneInfo tz, System.Globalization.CultureInfo culture)
    {
        string type = ReleaseListRules.TypeLabel(c.Kind);
        string when = ReleaseListRules.DateLabel(c.ReleasedAtMs, nowMs, tz, culture);
        var trailingKids = new List<Element>(2);
        if (type.Length > 0) trailingKids.Add(Controls.RowChip(type));
        if (when.Length > 0) trailingKids.Add(R.Type.TrackMeta(when) with { Color = Tok.TextTertiary, MaxLines = 1 });
        Element? trailing = trailingKids.Count == 0 ? null : new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f, Children = trailingKids.ToArray(),
        };

        string sub = HomeCards.PlainText(c.Subtitle);
        var data = new Controls.CardData(c.Uri, c.Title, sub.Length > 0 ? Meta(sub) : null, c.ImageUrl,
            () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c), Drag: HomeCardNav.DragOf(in c))
            { Trailing = trailing, Menu = HomeCardNav.MenuOf(in c) };
        return new BoxEl
        {
            Key = "release:" + c.Uri, Direction = 1, MinWidth = 0f, Layout = WinUiDiscoveryMotion.Shove,
            Children = [Controls.Surface(data, Shape.Row(WinUiHomeRules.ReleaseArt))],
        };
    }

    // ── 2.4 clusters: "Because you like" — Ui.Card(avatar + name, then <= 3 Surface(Row(40))) ──────────────────────────────────

    static Element ClusterGrid(Zone zone, WinUiHomeLayout layout)
    {
        var clusters = zone.Clusters;
        if (clusters is not { Count: > 0 }) return new BoxEl();
        int n = Math.Min(clusters.Count, WinUiHomeRules.ClusterMax);
        var cards = new Element[n];
        for (int i = 0; i < n; i++) cards[i] = ClusterCard(clusters[i]);
        return new GridEl
        {
            MinColWidth = layout.Clusters.Floor, MaxColumns = layout.Clusters.MaxCols,
            ColGap = WinUiHomeRules.CardGap, RowGap = WinUiHomeRules.CardGap, Children = cards,
        };
    }

    static Element ClusterCard(ZoneCluster cluster)
    {
        // The header (W-Home): a 24 avatar and the name, 14/20 semibold; the cluster opens to its own baseline card when it has one.
        var kids = new List<Element>(2);
        if (cluster.Header is { } hc0 && hc0.ImageUrl is { Length: > 0 } avatarUrl)
            kids.Add(new BoxEl
            {
                Width = WinUiHomeRules.ClusterAvatar, Height = WinUiHomeRules.ClusterAvatar, Shrink = 0f,
                Children = [Controls.Artwork(avatarUrl, WinUiHomeRules.ClusterAvatar, WinUiHomeRules.ClusterAvatar, WinUiHomeRules.ClusterAvatar / 2f)],
            });
        kids.Add(Ui.BodyStrong(cluster.Name) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f });
        Element header = new BoxEl
        {
            Direction = 0, Gap = Spacing.S + Spacing.XXS, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, Spacing.S), Children = kids.ToArray(),
        };
        if (cluster.Header is { } h)
        {
            var hc = h;
            header = new BoxEl
            {
                Direction = 1, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                OnClick = () => HomeCardNav.Open(in hc), Children = [header],
            };
        }

        var rowsSrc = cluster.Rows;
        int n = Math.Min(rowsSrc.Count, WinUiHomeRules.ClusterRows);
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var c = rowsSrc[i];
            string sub = HomeCards.PlainText(c.Subtitle);
            var data = new Controls.CardData(c.Uri, c.Title, sub.Length > 0 ? Meta(sub) : null, c.ImageUrl,
                () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
                Circular: c.Kind == HomeCardKind.Artist, Drag: HomeCardNav.DragOf(in c))
                { Menu = HomeCardNav.MenuOf(in c) };
            rows[i] = new BoxEl { Key = "cluster-row:" + c.Uri, Direction = 1, MinWidth = 0f, Children = [Controls.Surface(data, Shape.Row(WinUiHomeRules.ClusterArt))] };
        }

        return Ui.Card(header, new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = rows })
            with { MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Gap = Spacing.XS, Layout = WinUiDiscoveryMotion.Shove };
    }

    // ── 2.5 browse tiles: stock SettingsCards in the tile skin, then the Charts sub-block ─────────────────────────────────────────

    static Element BrowseTiles(Zone zone, WinUiHomeLayout layout)
    {
        var items = zone.Items;
        if (items.Count == 0) return new BoxEl();
        var tiles = new Element[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var c = items[i];
            string sub = HomeCards.PlainText(c.Subtitle);
            tiles[i] = SettingsCard.Create(new SettingsCard.Options
            {
                Header = c.Title,
                Description = sub.Length > 0 ? sub : null,
                HeaderIconElement = Controls.Artwork(c.ImageUrl, WinUiHomeRules.TileArt, WinUiHomeRules.TileArt, Radii.Control),
                IsClickEnabled = true,
                OnClick = () => HomeCardNav.Open(in c),
                Style = Controls.TileCardStyle, Parts = Controls.TileCardParts,
            }) with { Key = "browse-tile:" + c.Uri, Layout = WinUiDiscoveryMotion.Shove };
        }
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.Stretch,
            Children =
            [
                new GridEl
                {
                    MinColWidth = layout.Tiles.Floor, MaxColumns = layout.Tiles.MaxCols,
                    ColGap = WinUiHomeRules.CardGap, RowGap = WinUiHomeRules.CardGap, Children = tiles,
                },
                ChartsBlock(),
            ],
        };
    }

    /// <summary>The Browse facet's "Charts" sub-block: the core host owns the section's demand and the derived tile model and mounts `Home.Charts`; its skeleton
    /// proxy is the same static tree over <see cref="ChartTilesModel.Fallback"/>.</summary>
    static Element ChartsBlock()
        => Embed.Comp(static () => new ChartsBlockView()) with { Key = "home:charts", SkeletonProxy = s_chartsProxy };

    static readonly Func<Element> s_chartsProxy = static () => WinUiHomeCharts.Tree(ChartTilesModel.Fallback);

    // ── 2.6 episodes: Shape.EpisodeRow fed by Episode.RowData — the podcast caption, the resume rung, the episode menu ────────────

    static Element EpisodeGrid(Zone zone, WinUiGridBand band)
    {
        var cards = ShelfLead.Merge(zone.Items, zone.Lead);   // the lead episode is row 1, once
        if (cards.Count == 0) return new BoxEl();
        var clock = Episode.RowClock.Now();                    // one clock per grid: every row agrees on "Today"
        var rows = new Element[cards.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = EpisodeRow(cards[i], clock);
        return new GridEl
        {
            MinColWidth = band.Floor, MaxColumns = band.MaxCols,
            ColGap = WinUiHomeRules.CardGap, RowGap = Spacing.XS, Children = rows,   // RowHeight auto: two-line titles + a progress rung
        };
    }

    /// <summary>One Home episode row: the card's own facts (its section facts win over the columns — a resume point or an explicit mark the feed stated) through
    /// the episode adapter, so the row has the menu every episode surface has. Click opens the episode, the FAB plays it.</summary>
    static Element EpisodeRow(HomeCard e, Episode.RowClock clock)
    {
        string uri = e.Uri;
        var episode = e.Target.Kind == EntityKind.Episode ? new Episode(e.Target.Slot) : default;
        string show = e.ShowName is { Length: > 0 } s ? s : HomeCards.PlainText(e.Subtitle);
        var facts = new Episode.RowFacts(uri, e.Title, show, e.ImageUrl, e.IsExplicit, e.HasVideo,
                                         e.ReleasedAtMs, e.DurationMs, e.ResumeMs);
        var options = new Episode.RowOptions(OnClick: () => HomeCardNav.Open(in e), OnPlay: () => HomeCardNav.Play(in e), Clock: clock);
        return new BoxEl
        {
            Key = "episode:" + uri, Direction = 1, MinWidth = 0f, Layout = WinUiDiscoveryMotion.Shove,
            Children = [Controls.Surface(Episode.RowData(episode, in facts, in options), Shape.EpisodeRow)],
        };
    }

    // ── 2.7 the empty facet: the app's one vacancy, with the facet's own words ───────────────────────────────────────────────────

    static Element EmptyFacet(Zone zone)
    {
        var (title, body, action) = EmptyFacetCopy.Keys(zone.Key);
        return Controls.Vacancy(Controls.VacancyVoice.Empty,
            title: Loc.Get(title), subtitle: Loc.Get(body), actionLabel: Loc.Get(action),
            onAction: HomeVerbs.OpenBrowse);
    }

    // ══ 3. SHARED ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A one-line secondary caption, ellipsised.</summary>
    internal static TextEl Meta(string s)
        => R.Type.TrackMeta(s) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
}
