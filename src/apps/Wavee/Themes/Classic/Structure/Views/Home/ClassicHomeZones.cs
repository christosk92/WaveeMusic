// ── Themes/Classic/Structure/Views/Home/ClassicHomeZones.cs — Classic's Home zone column, chapters and bodies (T10a1) ─────────
//
// From `Home\Zones.UI.cs` (the home rebuild's fifth + sixth + seventh pass), moved with its comments. Nothing here is a card template or a
// hover recipe of its own: every zone body is composed from exactly the pieces the app's native pages compose — `PagedShelf` +
// `HomeCards.ShelfCell` + `HomeCardNav` (`ClassicBrowseCategory`'s shelf, verbatim), `Controls.Surface` rows (`Shape.Row*`) in a `GridEl`,
// stock `SettingsCard`, `Ui.Card` + `Ui.SectionHeader`, `Controls.Vacancy` — and every header is `HomeModules.ModuleHeader`
// (Entities/Browse.Modules.cs). Type is `R.Type.*`, rhythm is `Spacing.*`/`Design.Size.*`, radii are `Radii.*`. The data pipeline (`Zone`,
// `ZoneCluster`, `RecentsCells`, `ReleaseListRules`, `ShelfLead`, `EpisodeCaption`) is core and untouched; the numbers are
// `Rules\ClassicHomeRules.cs` (the plan's P22). EVERY COUNT AND SIZE BELOW is the content tier's `ClassicHomeLayout` (`Rules\Tiers.Home.cs`, TC4): the shelf's card
// band and column cap, the grids' column bands, the zone gap, and at Huge the daylist hero and the Recently played tiles SHARE one band.
//
// SIXTH pass — the prototype's visuals, each a small general extension of a SHARED control, none a Home-private template: wide shelves
// (WideTiles/VideoTiles) are 16:9 `ShelfCell`s through `CardData.CoverAspect`; a cover shelf whose lead carries a header image gets
// PagedShelf's 2-span lead cell (`ShelfLead.LeadAspect` keeps the lead cover exactly as tall as the squares); Browse tiles wear
// `Controls.TileCardStyle`. SEVENTH pass — a header is title · subtitle as a plain label (`open: null`, no chevron — the tools' "See all" /
// "Listening history" link is the drill), its tools are "See all" · divider · ‹ pips ›; only an artist's cover is round (radio art is
// Spotify's own square design; the 2-span lead and the 16:9 cells always pass `circular: false`); the 2-span lead mounts from three
// columns, so a page with the right panel open keeps its hero.
//
// ── THE CHAPTER SHAPE (one per zone) ──────────────────────────────────────────────────────────────────────────────────
//
//   BoxEl { Key = "zone:" + key, ScrollScope = key, Animate = Design.Entrance.Row(i) }
//   ├─ HEADER  ModuleHeader(title, subtitle, tools, open: null)  .Sticky(StuckBottom, scope: key)  — paints NOTHING
//   └─ BODY    .StickyClip(StuckBottom + HeaderH + HeaderGap)
//              EdgeFade = EdgeFadeSpec(Top, ClipFadeBand) { WhileStuck = true }
//
// The header pins under the (transparent) facet row and the body cuts ITSELF at the header's lower edge, feathered only while the clip is
// engaged — the Browse masthead / Artist band idiom (`ClassicBrowseDirectory`; `ClassicArtistPage`). The zone box is the sticky SCOPE,
// so the pinned header releases where its own content ends and the next zone's header pushes it out (the engine's stock scope clamp). No
// acrylic, no band fill, nowhere.
//
// ── THE SHELF PAGER LIVES IN THE STICKY HEADER (the engine's E4 external-controller shape) ─────────────────────────────
//
// A cover shelf's chevrons and pips sit in the sticky header's TOOLS slot, not in the shelf's own header row: the shelf is built
// `pager: ShelfPager.None, controller: c` and the header's tools are a `ShelfTools` leaf reading the same `ShelfController`'s four published
// signals (‹ `PipsPager.Controlled` › — two stock `IconButton` chevrons around the pips, routed back through `Prev`/`GoTo`/`Next`) —
// exactly the gallery's `ExternalChapterHeader` sample (fluent-gpu CollectionsMenusPages.cs:365-470), with stock IconButtons in place of its
// hand-drawn discs. The controller must be reference-stable for the shelf's lifetime (PagedShelf freezes it at mount), so a shelf zone
// renders through the tiny `ShelfChapter` component that OWNS its controller; every other zone is a plain static tree. A chapter body with no
// controller (a standalone / seed use) falls back to the shelf's own built-in `Chevrons | Pips` header pager — the same shelf, one flag
// different. That controller-less chapter is also `ShelfChapter`'s `SkeletonProxy`.
//
// ── THE BODIES (the `Home.Zone.<Kind>` keys) ─────────────────────────────────────────────────────────────────────────────
//   Daylist                                   the daylist card host (Home.Daylist)                          no header
//   RecentGrid                                the Recents host (Home.Zone.RecentGrid): GridEl{200, ≤4, 64} of Surface(RowTile) × (2·cols − 1)
//                                             + the history tile (RowOutline)                               tools: history link
//   CoverShelf/MixedCovers/RadioShelf/
//   ShowGrid                                  PagedShelf of HomeCards.ShelfCell (+ 2-span lead cell)       tools: See all · ‹ pips ›
//   WideTiles/VideoTiles                      PagedShelf of 16:9 HomeCards.ShelfCell                       tools: See all · ‹ pips ›
//   ReleaseList                               Ui.SectionHeader + GridEl{320, ≤2, 64} of Surface(Row)       (sub-block)
//   ClusterCards/PodcastGroups                GridEl{300, ≤3} of Ui.Card(SectionHeader + ≤3 Surface(Row(40)))
//   BrowseTiles                               GridEl{220, ≤4} of SettingsCard + the Charts sub-block (Home.Charts)
//   EpisodeLead/EpisodeRows                   GridEl{320, ≤2} of Surface(Episode.RowData, EpisodeRow: 56, 2-line title, meta, "…")
//   ContinueEpisodes                          GridEl{320, ≤3} of the same row + the resume rung (CardData.Below)
//   EmptyFacet                                Controls.Vacancy(Empty, … → Browse)                          no header
//
// THE SKELETON. The deriver cannot run a component's Render — a bare `ComponentEl` derives to ONE bar — so every slot and host the column
// places carries a `SkeletonProxy` that is the SAME static builder its template runs (`BodyTree`, `ClassicHomeRecents.Skeleton`,
// `ClassicHomeDaylist.Skeleton`, the charts tree over its fallback model): the cold load shimmers as the header + the real cards.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Home zone column.</summary>
internal static class ClassicHomeZones
{
    /// <summary>The registered template of every `Home.Zone.{Kind}` key whose body needs nothing but its zone.</summary>
    public static Element BodyTemplate(HomeZoneProps props, in ViewContext ctx)
        => BodyTree(props.Zone, props.Overlay, props.Controller, ClassicHomeTiers.Of(ctx.Tier()));

    // ══ 1. THE COLUMN ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every zone of <paramref name="m"/> as a chapter (`Design.Entrance.Row(i)` per zone), then the
    /// "Customize Home" link on the All facet (<paramref name="facet"/> empty). At a tier whose daylist sits BESIDE the Recently played tiles
    /// (Huge: <c>C-Home-Huge</c>) those two zones share one band, 1.35 : 1.</summary>
    public static Element Column(ScreenModel m, string facet, IOverlayService? overlay, ClassicHomeLayout layout)
    {
        var zones = m.Zones;
        bool all = facet.Length == 0;
        var kids = new List<Element>(zones.Count + 1);
        for (int i = 0; i < zones.Count; i++)
        {
            if (layout.DaylistBeside && zones[i].Kind == ZoneKind.Daylist && i + 1 < zones.Count && zones[i + 1].Kind == ZoneKind.RecentGrid)
            {
                kids.Add(Beside(zones[i], zones[i + 1], i, overlay, layout));
                i++;
                continue;
            }
            kids.Add(Chapter(zones[i], i, overlay, layout));
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

    /// <summary>The Huge band: the daylist hero and the Recently played tiles side by side (<c>C-Home-Huge</c>), the hero 1.35 shares of the row to the tiles' 1.
    /// Each is its own chapter (its own sticky scope and entrance); the row only places them.</summary>
    static Element Beside(Zone hero, Zone recents, int index, IOverlayService? overlay, ClassicHomeLayout layout) => new BoxEl
    {
        Key = "zone:" + hero.Key + "+" + recents.Key,
        Direction = 0, Gap = ClassicHomeTiers.HeroGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
        Children =
        [
            Chapter(hero, index, overlay, layout) with { Grow = ClassicHomeTiers.HeroShare, Shrink = 1f, Basis = 0f },
            Chapter(recents, index + 1, overlay, layout) with { Grow = 1f, Shrink = 1f, Basis = 0f },
        ],
    };

    /// <summary>One zone's chapter box: the keyed sticky scope carrying the entrance. A shelf zone mounts
    /// <see cref="ClassicHomeShelfChapter"/> (it owns the pager controller); everything else is a plain tree.
    /// <para>The skeleton deriver cannot run a component's Render — a bare <see cref="ComponentEl"/> derives to ONE
    /// 160×10 bar — so the shelf chapter hands it a <see cref="ComponentEl.SkeletonProxy"/>: the SAME static
    /// <c>Chapter(zone, overlay, controller)</c> its Render calls, with no controller (the shelf
    /// keeps its built-in pager, and the proxy stays a plain element factory — no hooks). The deriver then reaches
    /// the header and PagedShelf's own card proxy, so a cold load shimmers as the header + the real cards.</para></summary>
    static BoxEl Chapter(Zone zone, int index, IOverlayService? overlay, ClassicHomeLayout layout)
    {
        Element content = IsShelf(zone.Kind)
            ? Embed.Comp(new ShelfChapterProps(zone, overlay, layout), static () => new ClassicHomeShelfChapter())
                with { SkeletonProxy = () => Chapter(zone, overlay, controller: null, layout) }
            : Chapter(zone, overlay, controller: null, layout);
        return new BoxEl
        {
            Key = "zone:" + zone.Key, Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            ScrollScope = zone.Key, Animate = Design.Entrance.Row(index),
            Children = [content],
        };
    }

    static bool IsShelf(ZoneKind kind) => kind is ZoneKind.CoverShelf or ZoneKind.MixedCovers or ZoneKind.RadioShelf
        or ZoneKind.ShowGrid or ZoneKind.WideTiles or ZoneKind.VideoTiles;

    /// <summary>Header + clipped body inside the zone scope, or the bare body for a headerless zone.</summary>
    internal static Element Chapter(Zone zone, IOverlayService? overlay, ShelfController? controller, ClassicHomeLayout layout)
    {
        Element body = Body(zone, overlay, controller, layout);
        Element? header = Header(zone, controller);
        if (header is null) return body;
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children =
            [
                // The header paints NOTHING: a fixed-height box pinned under the facet row, releasing at the zone's end.
                new BoxEl
                {
                    Direction = 1, Justify = FlexJustify.Center, MinWidth = 0f,
                    Height = ClassicHomeRules.HeaderH + ClassicHomeRules.HeaderGap,
                    Padding = new Edges4(0f, 0f, 0f, ClassicHomeRules.HeaderGap),
                    Children = [header],
                }.Sticky(ClassicHomeRules.StuckBottom, scope: zone.Key),
                // The body cuts itself at the header's lower edge, feathered exactly while the cut is engaged.
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                    EdgeFade = new EdgeFadeSpec(EdgeMask.Top, ClassicHomeRules.ClipFadeBand) { WhileStuck = true },
                    Children = [body],
                }.StickyClip(ClassicHomeRules.BodyClipInset),
            ],
        };
    }

    /// <summary>The chapter header: `HomeModules.ModuleHeader` with the zone's title/subtitle as a plain label (no
    /// chevron, no click — `open: null`), the drill carried by the tools' link ("See all" / "Listening history") beside
    /// the shelf pager. Null for the headerless kinds (Daylist, EmptyFacet) and for the ReleaseList sub-block, whose
    /// `Ui.SectionHeader` is part of its body.</summary>
    static Element? Header(Zone zone, ShelfController? controller)
    {
        switch (zone.Kind)
        {
            case ZoneKind.Daylist or ZoneKind.EmptyFacet or ZoneKind.ReleaseList:
                return null;
            case ZoneKind.RecentGrid:
                return HomeModules.ModuleHeader(
                    zone.Title ?? Loc.Get(Strings.Sidebar.Section.RecentlyPlayed),
                    zone.Subtitle ?? Loc.Get(Strings.Home.RecentsSub),
                    HyperlinkButton.Create(Loc.Get(Strings.Home.ListeningHistory), HomeVerbs.OpenRecents), open: null);
            default:
                return HomeModules.ModuleHeader(zone.Title ?? "", zone.Subtitle,
                                                ShelfHeaderTools(HomeVerbs.SeeAll(zone), controller), open: null);
        }
    }

    /// <summary>A shelf header's tools: the "See all" link (when the zone has a backing section), then — while the
    /// shelf pages — a 16-px vertical divider and the pager (‹ pips ›). Null when the header has neither.</summary>
    static Element? ShelfHeaderTools(Action? seeAll, ShelfController? controller)
    {
        Element? link = seeAll is null ? null : HyperlinkButton.Create(Loc.Get(Strings.Home.SeeAll), seeAll) with { Shrink = 0f };
        bool leadingDivider = link is not null;
        Element? pager = controller is null ? null
            : Embed.Comp(() => new ClassicHomeShelfTools(controller, leadingDivider)) with { Key = leadingDivider ? "tools+link" : "tools" };
        if (link is null) return pager;
        if (pager is null) return link;
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [link, pager],
        };
    }

    // ══ 2. THE BODIES ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A zone's body alone (no header, no clip). <paramref name="controller"/> is the shelf pager's external
    /// controller when the header owns the pips/chevrons; null lets a shelf keep its own built-in pager. The daylist and
    /// the Recents grid mount their core hosts (they read core state); every other kind is its `Home.Zone.{Kind}` slot.</summary>
    static Element Body(Zone zone, IOverlayService? overlay, ShelfController? controller, ClassicHomeLayout layout) => zone.Kind switch
    {
        ZoneKind.Daylist => Embed.Comp(new DaylistProps(zone, overlay), static () => new DaylistCard())
            with { SkeletonProxy = () => ClassicHomeDaylist.Skeleton(layout) },
        // The proxy is the view's own static tree at neutral live facts (see ClassicHomeRecents.Skeleton): a cold load
        // shimmers as the tile grid, not as one default bar.
        ZoneKind.RecentGrid => Embed.Comp(new RecentGridProps(zone), static () => new RecentGrid())
            with { SkeletonProxy = () => ClassicHomeRecents.Skeleton(zone, layout) },
        _ => ZoneSlot(zone, overlay, controller, layout),
    };

    /// <summary>The kind's `Home.Zone.{Kind}` slot over the zone, its proxy the same static tree the template builds.</summary>
    static Element ZoneSlot(Zone zone, IOverlayService? overlay, ShelfController? controller, ClassicHomeLayout layout)
    {
        if (!HomeZoneKeys.TryKeyOf(zone.Kind, out var key)) return new BoxEl();
        Element slot = Slots.View(key, new HomeZoneProps(zone, overlay, controller));
        return slot is ComponentEl component ? component with { SkeletonProxy = () => BodyTree(zone, overlay, controller: null, layout) } : slot;
    }

    /// <summary>The picture of every kind that is a pure function of its zone: what the `Home.Zone.{Kind}` template and its skeleton
    /// proxy both build.</summary>
    internal static Element BodyTree(Zone zone, IOverlayService? overlay, ShelfController? controller, ClassicHomeLayout layout) => zone.Kind switch
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

    // ── 2.1 cover shelves: PagedShelf of the house shelf cell, verbatim `ClassicBrowseCategory`'s Shelf ────────────────────────

    /// <summary>Three shapes of ONE shelf. A WIDE shelf (WideTiles/VideoTiles) is 16:9 cells — the card's header image
    /// (or its cover) through <c>CardData.CoverAspect</c>, an owner · count meta line, the wide min/max card widths. A
    /// COVER shelf whose lead carries a header image gets PagedShelf's 2-span lead cell (E21/E22): the lead is
    /// <c>ShelfLead.LeadAspect</c> wide so its cover is exactly as tall as the squares beside it, with a two-line caption
    /// whose tail is the inline "{n} songs" meta (no separate meta line); the template follows the cell, so a page too
    /// narrow for the span mounts the lead as a plain square (the engine's rule, never ours). Everything else is the square cell, verbatim.</summary>
    static Element Shelf(Zone zone, IOverlayService? overlay, ShelfController? controller, ClassicHomeLayout layout)
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
            // Only a SQUARE cover may be round (an artist, a radio): the 16:9 wide cells and the 2-span lead are
            // rectangles whatever the card is — the Radio lead is the radio header art, the radios beside it are discs.
            // Only an artist is a disc. A radio station's art is Spotify's own square design (its circles and the
            // "Radio" band are drawn IN the image), so a round crop cut it apart.
            bool circular = c.Kind == HomeCardKind.Artist;
            items[i] = wide
                ? HomeCards.ShelfItemOf(in c, second, circular: false, wideArt: c.HeaderImageUrl ?? c.ImageUrl,
                                        meta: CardMeta.Of(c.OwnerName, c.TrackCount))
                : leadWide && i == 0
                    // The lead's "{n} songs" rides INLINE at the tail of its two-line caption (the prototype's
                    // `.lead-meta` span inside `.g-cap`): a cover shelf never grows a separate meta line.
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
        // The row's cross extent, EXACT for what its cards show (`SurfaceGeometry.ShelfHeight`, the rule the card renders): a
        // wide cell is its 16:9 cover + title + ONE caption line + the separate meta line; a cover shelf reserves the
        // most caption lines any of its cards shows — 2 when it carries the lead (its meta inline on those lines), else
        // 1 — and no meta line. A square beside the lead is merely 16 shorter than its row (its plate stretches, labels
        // top-aligned). Like every PagedShelf prop but the items, the extent is fixed at mount.
        Func<float, float> cardHeight = wide ? s_wideCardHeight : CoverCardHeight(HomeCards.CaptionLinesOf(items));

        return PagedShelf.Create<HomeCards.ShelfItem>(items,
            (item, i, cardW) => Cell(in item, cardW, squareAspect),
            onInvoke: static (item, _) => HomeCardNav.Open(item.Card),   // the slot owns the click: the card's own open, the lead cell's too
            cardHeight: cardHeight,
            // The header owns the pager (the controller) where the tier pages the shelf; a tier that does not (a Compact strip) shows none.
            pager: controller is null ? (band.Pager ? ShelfPager.Chevrons | ShelfPager.Pips : ShelfPager.None) : ShelfPager.None,
            controller: controller,
            minCardW: band.MinCardW, maxCardW: band.MaxCardW, maxColumns: band.MaxColumns, fixedCardW: band.FixedCardW,
            gap: band.Gap, edgeFade: HomeModuleLayout.ShelfEdgeFade, snap: ShelfSnap.Page,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,   // the shared card hovers fill-only: no lift halo to reserve clearance for
            keyOf: static (item, i) => item.Card.IsBlank
                ? "home-shelf-blank:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "home-shelf-card:" + item.Card.Uri,
            // The lead span is a LIVE prop (it follows whether THIS lead has a header image); the minimum column count
            // is frozen mount configuration, so it is set for every cover shelf and simply inert while the span is 1.
            leadSpan: leadWide ? ClassicHomeRules.LeadSpan : 1,
            leadCardAt: leadWide
                ? (item, i, leadW) => Cell(in item, leadW, ShelfLead.LeadAspect(leadW, HomeModuleLayout.ShelfGap, ClassicHomeRules.LeadSpan, Spacing.S))
                : null,
            leadMinColumns: wide ? 0 : ClassicHomeRules.LeadMinColumns)
           // Keyed by the tier too: the shelf's card band, column cap and pager are frozen at mount, so a tier crossing REMOUNTS it (through the page's
           // cross-fade) instead of keeping a stale fit.
           with { Key = "home-shelf:" + zone.Key + ":" + ((int)layout.Tier).ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }

    /// <summary>The shelf band of a zone kind in <paramref name="layout"/>: the wide tiles' or the covers'.</summary>
    internal static ClassicShelfBand ShelfBandOf(ZoneKind kind, ClassicHomeLayout layout)
        => kind is ZoneKind.WideTiles or ZoneKind.VideoTiles ? layout.WideShelf : layout.Shelf;

    // The shelf extents as cached delegates (one per shape, never a per-render closure).
    static readonly Func<float, float> s_wideCardHeight =
        static w => SurfaceGeometry.ShelfHeight(w, R.Space.WideTileAspect, captionLines: 1, metaLine: true);
    static readonly Func<float, float> s_coverCardHeight1 = static w => SurfaceGeometry.ShelfHeight(w, 1f, captionLines: 1, metaLine: false);
    static readonly Func<float, float> s_coverCardHeight2 = static w => SurfaceGeometry.ShelfHeight(w, 1f, captionLines: 2, metaLine: false);

    static Func<float, float> CoverCardHeight(int captionLines) => captionLines switch
    {
        <= 1 => s_coverCardHeight1,
        2 => s_coverCardHeight2,
        _ => w => SurfaceGeometry.ShelfHeight(w, 1f, captionLines, metaLine: false),
    };

    /// <summary>The shelf pager in the sticky header's tools slot: ‹ pips › — the stock pips between two stock chevrons
    /// (the prototype's order), every action routed back through the controller (the gallery's
    /// <c>ExternalChapterHeader</c>). Reads the controller's four value
    /// signals, so only this leaf re-renders on a page change. <paramref name="leadingDivider"/> puts the 16-px
    /// vertical divider between the header's "See all" link and the pager — it comes and goes WITH the pager, so a
    /// one-page shelf shows the bare link.</summary>
    sealed class ClassicHomeShelfTools(ShelfController controller, bool leadingDivider) : Component
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
        => new BoxEl { Width = 1f, Height = ClassicHomeRules.ToolsDividerH, Shrink = 0f, Fill = Tok.StrokeDividerDefault };

    /// <summary>Props for <see cref="ClassicHomeShelfChapter"/>: the zone by value (a re-plan with equal content is a no-op at the
    /// embed boundary); the overlay service by reference.</summary>
    internal sealed record ShelfChapterProps(Zone Zone, IOverlayService? Overlay, ClassicHomeLayout Layout)
    {
        public bool Equals(ShelfChapterProps? o) => o is not null && Zone.Equals(o.Zone) && ReferenceEquals(Overlay, o.Overlay) && Layout.Equals(o.Layout);
        public override int GetHashCode() => Zone.GetHashCode();
    }

    /// <summary>A shelf zone's chapter: owns the ONE <see cref="ShelfController"/> its sticky header's pager and its
    /// shelf share (a plain field — reference-stable for the shelf's lifetime, per PagedShelf's contract). Its embed's
    /// skeleton proxy is the same static chapter builder without the controller (see the chapter box above).</summary>
    internal sealed class ClassicHomeShelfChapter : Component
    {
        readonly ShelfController _controller = new();

        public override Element Render()
        {
            var p = UseProps<ShelfChapterProps>();
            // The pager rides the sticky header only where the tier pages this shelf (its band says so); a strip has none.
            return Chapter(p.Zone, p.Overlay, ShelfBandOf(p.Zone.Kind, p.Layout).Pager ? _controller : null, p.Layout);
        }
    }

    // ── 2.3 release list: "From artists you follow" — a SectionHeader sub-block over a two-column Surface(Row) grid ──

    static Element ReleaseList(Zone zone, ClassicHomeLayout layout)
    {
        var items = zone.Items;
        int n = Math.Min(items.Count, ClassicHomeRules.ReleaseRowsMax);
        if (n == 0) return new BoxEl();

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tz = TimeZoneInfo.Local;
        var culture = System.Globalization.CultureInfo.CurrentCulture;

        var rows = new Element[n];
        for (int i = 0; i < n; i++) rows[i] = ReleaseRow(items[i], nowMs, tz, culture);
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Stretch,
            Children =
            [
                Ui.SectionHeader(zone.Title ?? Loc.Get(Strings.Home.Zone.FromArtistsYouFollow),
                                 zone.Subtitle ?? Loc.Get(Strings.Home.ReleasedThisWeek)) with { Margin = default },
                new GridEl
                {
                    MinColWidth = layout.Releases.MinCol, MaxColumns = layout.Releases.MaxCols,
                    ColGap = Spacing.M, RowGap = Spacing.XS, RowHeight = ClassicHomeRules.RowH, Children = rows,
                },
            ],
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
        return new BoxEl { Key = "release:" + c.Uri, Direction = 1, MinWidth = 0f, Children = [Controls.Surface(data, Shape.Row())] };
    }

    // ── 2.4 clusters: "Because you like" / "Because you listen to" — Ui.Card(SectionHeader + ≤3 Surface(Row(40))) ───

    static Element ClusterGrid(Zone zone, ClassicHomeLayout layout)
    {
        var clusters = zone.Clusters;
        if (clusters is not { Count: > 0 }) return new BoxEl();
        int n = Math.Min(clusters.Count, ClassicHomeRules.ClusterMax);
        var cards = new Element[n];
        for (int i = 0; i < n; i++) cards[i] = ClusterCard(clusters[i]);
        return new GridEl
        {
            MinColWidth = layout.Clusters.MinCol, MaxColumns = layout.Clusters.MaxCols,
            ColGap = Spacing.M, RowGap = Spacing.M, Children = cards,
        };
    }

    static Element ClusterCard(ZoneCluster cluster)
    {
        Element header = Ui.SectionHeader(cluster.Name, cluster.Over is { Length: > 0 } ? cluster.Over : null) with { Margin = default, MinWidth = 0f };
        if (cluster.Header is { } h)
        {
            // The cluster opens to its own baseline card (a playlist / an album): the header is the drill.
            var hc = h;
            header = new BoxEl
            {
                Direction = 1, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                OnClick = () => HomeCardNav.Open(in hc), Children = [header],
            };
        }

        var rowsSrc = cluster.Rows;
        int n = Math.Min(rowsSrc.Count, ClassicHomeRules.ClusterRows);
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var c = rowsSrc[i];
            string sub = HomeCards.PlainText(c.Subtitle);
            var data = new Controls.CardData(c.Uri, c.Title, sub.Length > 0 ? Meta(sub) : null, c.ImageUrl,
                () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
                Circular: c.Kind == HomeCardKind.Artist, Drag: HomeCardNav.DragOf(in c))
                { Menu = HomeCardNav.MenuOf(in c) };
            rows[i] = new BoxEl { Key = "cluster-row:" + c.Uri, Direction = 1, MinWidth = 0f, Children = [Controls.Surface(data, Shape.Row(ClassicHomeRules.ClusterArt))] };
        }

        return Ui.Card(header, new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = rows })
            with { MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Gap = Spacing.S };
    }

    // ── 2.5 browse tiles: stock SettingsCards in the tile skin (48 art plate, one-line text), then the Charts sub-block ──

    // Every tile here is clickable (the Browse tiles and the three chart tiles), so they take `Controls.TileCardParts`:
    // the one-line text and the hand cursor the shared rule gives an invokable surface.

    static Element BrowseTiles(Zone zone, ClassicHomeLayout layout)
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
                HeaderIconElement = Controls.Artwork(c.ImageUrl, ClassicHomeRules.TileArt, ClassicHomeRules.TileArt, Radii.Control),
                IsClickEnabled = true,
                OnClick = () => HomeCardNav.Open(in c),
                Style = Controls.TileCardStyle, Parts = Controls.TileCardParts,
            }) with { Key = "browse-tile:" + c.Uri };
        }
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = R.Space.SectionGap, AlignItems = FlexAlign.Stretch,
            Children =
            [
                new GridEl
                {
                    MinColWidth = layout.Tiles.MinCol, MaxColumns = layout.Tiles.MaxCols,
                    ColGap = Spacing.M, RowGap = Spacing.M, Children = tiles,
                },
                ChartsBlock(),
            ],
        };
    }

    /// <summary>The Browse facet's "Charts" sub-block (RCA 2026-09-30): the core host owns the section's demand and the derived tile model
    /// and mounts `Home.Charts`; its skeleton proxy is the same static tree over <see cref="ChartTilesModel.Fallback"/>.</summary>
    static Element ChartsBlock()
        => Embed.Comp(static () => new ChartsBlockView()) with { Key = "home:charts", SkeletonProxy = s_chartsProxy };

    static readonly Func<Element> s_chartsProxy = static () => ClassicHomeCharts.Tree(ChartTilesModel.Fallback);

    // ── 2.6 episodes: Shape.EpisodeRow (56, two-line title) fed by Episode.RowData — the podcast caption, the resume rung,
    //    the episode menu behind the "…" and the right-click ────────────────────────────────────────────────────────────

    static Element EpisodeGrid(Zone zone, ClassicGridBand band)
    {
        var cards = ShelfLead.Merge(zone.Items, zone.Lead);   // the lead episode is row 1, once
        if (cards.Count == 0) return new BoxEl();
        var clock = Episode.RowClock.Now();                    // one clock per grid: every row agrees on "Today"
        var rows = new Element[cards.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = EpisodeRow(cards[i], clock);
        return new GridEl
        {
            MinColWidth = band.MinCol, MaxColumns = band.MaxCols,
            ColGap = Spacing.M, RowGap = Spacing.XS, Children = rows,   // RowHeight auto: two-line titles + a progress rung
        };
    }

    /// <summary>One Home episode row: the card's own facts (its section facts win over the columns — a resume point or an
    /// explicit mark the feed stated) through the episode adapter, so the row has the menu every episode surface has.
    /// Click opens the episode (the card's own route), the FAB plays it (the card's own play verb).</summary>
    static Element EpisodeRow(HomeCard e, Episode.RowClock clock)
    {
        string uri = e.Uri;
        string show = e.ShowName is { Length: > 0 } s ? s : HomeCards.PlainText(e.Subtitle);
        var episode = e.Target.Kind == EntityKind.Episode ? new Episode(e.Target.Slot) : default;
        var facts = new Episode.RowFacts(uri, e.Title, show, e.ImageUrl, e.IsExplicit, e.HasVideo,
                                         e.ReleasedAtMs, e.DurationMs, e.ResumeMs);
        var options = new Episode.RowOptions(OnClick: () => HomeCardNav.Open(in e), OnPlay: () => HomeCardNav.Play(in e), Clock: clock);
        return new BoxEl
        {
            Key = "episode:" + uri, Direction = 1, MinWidth = 0f,
            Children = [Controls.Surface(Episode.RowData(episode, in facts, in options), Shape.EpisodeRow)],
        };
    }

    // ── 2.7 the empty facet: the app's one vacancy, with the facet's own words ───────────────────────────────────────

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
