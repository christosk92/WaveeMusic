// ── Home/Zones.UI.cs — the Home zones over STOCK controls (home rebuild, fifth + sixth + seventh pass) ────────────────
//
// Role: UI
// Spec: docs/plans/wavee/home-rebuild-implementation.md "Fifth pass — stock controls" (the zone → control table, the
//        no-background sticky idiom) + "Sixth pass — prototype visuals on the shared controls" (the Zones.UI.cs bullet)
//        + "Seventh pass" (the Zones.UI.cs bullet).
//
// FIFTH ground-up pass over this file (the four hand-built ones live in git history under this path). Nothing here is a
// card template or a hover recipe of its own: every zone body is composed from exactly the pieces the app's native pages
// compose — `PagedShelf` + `HomeCards.ShelfCell` + `HomeCardNav` (Entities/Browse.Page.cs's shelf, verbatim),
// `Controls.MediaRow` in a `GridEl`, stock `SettingsCard`, `Ui.Card` + `Ui.SectionHeader`, `Controls.Vacancy` — and
// every header is `HomeModules.ModuleHeader` (Entities/Browse.Modules.cs). Type is `Design.Type.*`, rhythm is
// `Spacing.*`/`Design.Size.*`, radii are `Radii.*`. The data pipeline (`Zone`, `ZoneCluster`, `RecentsCells`,
// `ReleaseListRules`, `ShelfLead`, `EpisodeCaption`) is untouched.
//
// SIXTH pass — the prototype's visuals, each a small general extension of a SHARED control, none a Home-private
// template: the Recents grid is `MediaRow(skin: RowSkin.Tile)` cells sized by the engine's own auto-fill count
// (`GridEl.AutoFillColumnCount` → `RecentsPlan.Cells`, always 2·cols − 1 played + the history tile = two full rows),
// each cell's subtitle carrying a live equalizer + "playing on {device}" swap through `Controls.RelatesNow`; the history
// tile is the same row in `RowSkin.Outline` with an `IconPlate` cover and a stock `SparkBars` 7-day strip; wide shelves
// (WideTiles/VideoTiles) are 16:9 `ShelfCell`s through `CardData.CoverAspect`; a cover shelf whose lead carries a header
// image gets PagedShelf's 2-span lead cell (`ShelfLead.LeadAspect` keeps the lead cover exactly as tall as the squares);
// Browse tiles wear `Controls.TileCardStyle`.
//
// SEVENTH pass — the prototype's header and the cold load: a header is title · subtitle as a plain label (`open: null`,
// no chevron — the tools' "See all" / "Listening history" link is the drill), its tools are "See all" · divider · ‹ pips ›;
// only an artist's cover is round (radio art is Spotify's own square design; the 2-span lead and the 16:9 cells always
// pass `circular: false`); the 2-span lead mounts from three columns, so a page with the right panel open keeps its hero;
// and the two component boundaries (`ShelfChapter`, `RecentGrid`) carry a
// `SkeletonProxy` that is their OWN static tree builder, so the seed shimmers as header + cards / tile grid instead of the
// deriver's one default bar per component.
//
// ── THE CHAPTER SHAPE (one per zone) ──────────────────────────────────────────────────────────────────────────────────
//
//   BoxEl { Key = "zone:" + key, ScrollScope = key, Animate = Design.Entrance.Row(i) }
//   ├─ HEADER  ModuleHeader(title, subtitle, tools, open: null)  .Sticky(Facet.StuckBottom, scope: key)  — paints NOTHING
//   └─ BODY    .StickyClip(Facet.StuckBottom + HeaderH + HeaderGap)
//              EdgeFade = EdgeFadeSpec(Top, ClipFadeBand) { WhileStuck = true }
//
// The header pins under the (transparent) facet row and the body cuts ITSELF at the header's lower edge, feathered only
// while the clip is engaged — the Browse masthead / Artist band idiom (Browse.Page.cs:24-29,165-173; Artist.Page.cs:
// 525-545). The zone box is the sticky SCOPE, so the pinned header releases where its own content ends and the next
// zone's header pushes it out (the engine's stock scope clamp). No acrylic, no band fill, nowhere.
//
// ── THE SHELF PAGER LIVES IN THE STICKY HEADER (the engine's E4 external-controller shape) ─────────────────────────────
//
// A cover shelf's chevrons and pips sit in the sticky header's TOOLS slot, not in the shelf's own header row: the shelf
// is built `pager: ShelfPager.None, controller: c` and the header's tools are a `ShelfTools` leaf reading the same
// `ShelfController`'s four published signals (‹ `PipsPager.Controlled` › — two stock `IconButton` chevrons around the
// pips, routed back through `Prev`/`GoTo`/`Next`) — exactly the gallery's `ExternalChapterHeader` sample (fluent-gpu
// CollectionsMenusPages.cs:365-470), with stock IconButtons in place of its hand-drawn discs. The controller must be
// reference-stable for the shelf's lifetime (PagedShelf freezes it at mount), so a shelf zone renders through the tiny
// `ShelfChapter` component that OWNS its controller; every other zone is a plain static tree. `Body(zone, overlay)`
// with no controller (a standalone / seed use) falls back to the shelf's own built-in `Chevrons | Pips` header pager —
// the same shelf, one flag different. That controller-less chapter is also `ShelfChapter`'s `SkeletonProxy`.
//
// ── DISPATCH ──────────────────────────────────────────────────────────────────────────────────────────────────────────
//   Daylist                                   Daylist.Card (Home/Daylist.UI.cs)                           no header
//   RecentGrid                                RecentGrid component (+ proxy) → Responsive → GridEl{200, ≤4,  tools: history link
//                                             64} of MediaRow(Tile) × (2·cols − 1) + the history tile (Outline)
//   CoverShelf/MixedCovers/RadioShelf/
//   ShowGrid                                  PagedShelf of HomeCards.ShelfCell (+ 2-span lead cell)       tools: See all · ‹ pips ›
//   WideTiles/VideoTiles                      PagedShelf of 16:9 HomeCards.ShelfCell                       tools: See all · ‹ pips ›
//   ReleaseList                               Ui.SectionHeader + GridEl{320, ≤2, 64} of MediaRow           (sub-block)
//   ClusterCards/PodcastGroups                GridEl{300, ≤3} of Ui.Card(SectionHeader + ≤3 MediaRow 40)
//   BrowseTiles                               GridEl{220, ≤4} of SettingsCard + the Charts sub-block (ChartsBlockView:
//                                             Charts page · Top 50 / Viral 50 playlists picked from Featured Charts)
//   EpisodeLead/EpisodeRows                   GridEl{320, ≤2} of MediaRow(56, 2-line title, meta)
//   ContinueEpisodes                          GridEl{320, ≤3} of the same row + ProgressBar
//   EmptyFacet                                Controls.Vacancy(Empty, … → Browse)                          no header

using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using Wavee;

namespace Wavee.HomeUi;

public static class Zones
{
    // ── the chapter geometry (the sticky idiom's three numbers) ─────────────────────────────────────────────────────

    /// <summary>The pinned header row's height: the 20/28 module title with a 32-tall stock tools control beside it.</summary>
    public const float HeaderH = Design.Size.ControlH;
    /// <summary>The breath between a header and its body — INSIDE the header's sticky box, so the body's clip line sits
    /// exactly under it.</summary>
    public const float HeaderGap = Spacing.M;
    /// <summary>The feather the body dissolves through while its clip is engaged (the shelf fade rung, as Browse).</summary>
    public const float ClipFadeBand = Design.Size.FadeShelf;
    /// <summary>The viewport line a zone body is cut at while its header is pinned.</summary>
    public static float BodyClipInset => Facet.StuckBottom + HeaderH + HeaderGap;

    // ── the grid geometry per body (stock auto-fill: MinColWidth × MaxColumns; the width picks the count) ───────────

    /// <summary>The Recents grid's minimum column: 200, so the owner's 632-DIP page fits three (3·200 + 2·12 = 624 ≤ 632
    /// → 5 played tiles + the history tile) and four arrive at 836. Public for the fact that pins it.</summary>
    public const float RecentsMinCol = 200f;
    const float ListMinCol = 320f, ClusterMinCol = 300f, TilesMinCol = 220f;
    const int RecentsMaxCols = 4, ListMaxCols = 2, ContinueMaxCols = 3, ClusterMaxCols = 3, TilesMaxCols = 4, ChartsMaxCols = 3;
    const float RowH = 64f, ClusterArt = 40f, EpisodeArt = 56f, TileArt = Design.Size.Thumb48, ProgressW = 120f;
    const int ReleaseRowsMax = 6, ClusterMax = 6, ClusterRows = 3;
    /// <summary>The Recents cell's equalizer height, the history tile's 7-day strip box, the header tools' divider.</summary>
    const float EqualizerH = 12f, HistoryStripW = 56f, HistoryStripH = 14f, ToolsDividerH = 16f;
    /// <summary>The 2-span lead cell of a cover shelf (E21/E22): item 0 spans two columns once the shelf has three —
    /// the lead plus one square — so the hero survives a narrow page (the right panel open); at two columns it would
    /// fill the whole row, so it mounts as a plain square there.</summary>
    const int LeadSpan = 2, LeadMinColumns = 3;

    // ══ 1. THE COLUMN ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every zone of <paramref name="m"/> as a chapter (`Design.Entrance.Row(i)` per zone), then the
    /// "Customize Home" link on the All facet (<paramref name="facet"/> empty).</summary>
    public static Element Column(ScreenModel m, string facet, IOverlayService? overlay)
    {
        var zones = m.Zones;
        bool all = facet.Length == 0;
        var kids = new Element[zones.Count + (all ? 1 : 0)];
        for (int i = 0; i < zones.Count; i++) kids[i] = Chapter(zones[i], i, overlay);
        if (all)
            kids[^1] = new BoxEl
            {
                Key = "customize", Direction = 0, MinWidth = 0f, Animate = Design.Entrance.Row(zones.Count),
                Children = [CustomizeScreen.CustomizeLink()],
            };
        return new BoxEl
        {
            Direction = 1, Gap = Design.Size.SectionGapWide, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = kids,
        };
    }

    /// <summary>One zone's chapter box: the keyed sticky scope carrying the entrance. A shelf zone mounts
    /// <see cref="ShelfChapter"/> (it owns the pager controller); everything else is a plain tree.
    /// <para>The skeleton deriver cannot run a component's Render — a bare <see cref="ComponentEl"/> derives to ONE
    /// 160×10 bar — so the shelf chapter hands it a <see cref="ComponentEl.SkeletonProxy"/>: the SAME static
    /// <c>Chapter(zone, overlay, controller)</c> its Render calls, with no controller (the shelf
    /// keeps its built-in pager, and the proxy stays a plain element factory — no hooks). The deriver then reaches
    /// the header and PagedShelf's own card proxy, so a cold load shimmers as the header + the real cards.</para></summary>
    static Element Chapter(Zone zone, int index, IOverlayService? overlay)
    {
        Element content = IsShelf(zone.Kind)
            ? Embed.Comp(new ShelfChapterProps(zone, overlay), static () => new ShelfChapter())
                with { SkeletonProxy = () => Chapter(zone, overlay, controller: null) }
            : Chapter(zone, overlay, controller: null);
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
    static Element Chapter(Zone zone, IOverlayService? overlay, ShelfController? controller)
    {
        Element body = Body(zone, overlay, controller);
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
                    Height = HeaderH + HeaderGap, Padding = new Edges4(0f, 0f, 0f, HeaderGap),
                    Children = [header],
                }.Sticky(Facet.StuckBottom, scope: zone.Key),
                // The body cuts itself at the header's lower edge, feathered exactly while the cut is engaged.
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                    EdgeFade = new EdgeFadeSpec(EdgeMask.Top, ClipFadeBand) { WhileStuck = true },
                    Children = [body],
                }.StickyClip(BodyClipInset),
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
                    HyperlinkButton.Create(Loc.Get(Strings.Home.ListeningHistory), GoRecents), open: null);
            default:
                return HomeModules.ModuleHeader(zone.Title ?? "", zone.Subtitle,
                                                ShelfHeaderTools(SeeAll(zone), controller), open: null);
        }
    }

    /// <summary>A shelf header's tools: the "See all" link (when the zone has a backing section), then — while the
    /// shelf pages — a 16-px vertical divider and the pager (‹ pips ›). Null when the header has neither.</summary>
    static Element? ShelfHeaderTools(Action? seeAll, ShelfController? controller)
    {
        Element? link = seeAll is null ? null : HyperlinkButton.Create(Loc.Get(Strings.Home.SeeAll), seeAll) with { Shrink = 0f };
        bool leadingDivider = link is not null;
        Element? pager = controller is null ? null
            : Embed.Comp(() => new ShelfTools(controller, leadingDivider)) with { Key = leadingDivider ? "tools+link" : "tools" };
        if (link is null) return pager;
        if (pager is null) return link;
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [link, pager],
        };
    }

    // ══ 2. THE BODIES ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A zone's body alone (no header, no clip). <paramref name="controller"/> is the shelf pager's external
    /// controller when the header owns the pips/chevrons; null lets a shelf keep its own built-in pager.</summary>
    public static Element Body(Zone zone, IOverlayService? overlay, ShelfController? controller = null) => zone.Kind switch
    {
        ZoneKind.Daylist => Daylist.Card(zone, overlay),
        // The proxy is the component's own static tree at neutral live facts (see RecentGrid.Skeleton): a cold load
        // shimmers as the tile grid, not as one default bar.
        ZoneKind.RecentGrid => Embed.Comp(new RecentGridProps(zone, overlay), static () => new RecentGrid())
            with { SkeletonProxy = () => RecentGrid.Skeleton(zone, overlay) },
        ZoneKind.CoverShelf or ZoneKind.MixedCovers or ZoneKind.RadioShelf or ZoneKind.ShowGrid
            or ZoneKind.WideTiles or ZoneKind.VideoTiles => Shelf(zone, overlay, controller),
        ZoneKind.ReleaseList => ReleaseList(zone, overlay),
        ZoneKind.ClusterCards or ZoneKind.PodcastGroups => ClusterGrid(zone, overlay),
        ZoneKind.BrowseTiles => BrowseTiles(zone),
        ZoneKind.EpisodeLead or ZoneKind.EpisodeRows => EpisodeGrid(zone, ListMaxCols),
        ZoneKind.ContinueEpisodes => EpisodeGrid(zone, ContinueMaxCols),
        ZoneKind.EmptyFacet => EmptyFacet(zone),
        _ => new BoxEl(),
    };

    static IOverlayService? HostOf(IOverlayService? overlay) => Controls.IsNullOverlay(overlay) ? null : overlay;

    // ── 2.1 cover shelves: PagedShelf of the house shelf cell, verbatim Browse.Page.cs's Shelf ────────────────────────

    /// <summary>Three shapes of ONE shelf. A WIDE shelf (WideTiles/VideoTiles) is 16:9 cells — the card's header image
    /// (or its cover) through <c>CardData.CoverAspect</c>, an owner · count meta line, the wide min/max card widths. A
    /// COVER shelf whose lead carries a header image gets PagedShelf's 2-span lead cell (E21/E22): the lead is
    /// <c>ShelfLead.LeadAspect</c> wide so its cover is exactly as tall as the squares beside it, with a two-line caption
    /// whose tail is the inline "{n} songs" meta (no separate meta line); the template follows the cell, so a page too
    /// narrow for the span mounts the lead as a plain square (the engine's rule, never ours). Everything else is the square cell, verbatim.</summary>
    static Element Shelf(Zone zone, IOverlayService? overlay, ShelfController? controller)
    {
        bool wide = zone.Kind is ZoneKind.WideTiles or ZoneKind.VideoTiles;
        var cards = ShelfLead.Merge(zone.Items, zone.Lead);   // lead at index 0, exactly once
        bool leadWide = !wide && zone.Lead is { } lead && cards.Count > 0 && lead.HeaderImageUrl is { Length: > 0 };
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

        float squareAspect = wide ? Design.Size.WideTileAspect : 1f;
        // The row's cross extent, EXACT for what its cards show (`Controls.ShelfHeight`, the rule the card renders): a
        // wide cell is its 16:9 cover + title + ONE caption line + the separate meta line; a cover shelf reserves the
        // most caption lines any of its cards shows — 2 when it carries the lead (its meta inline on those lines), else
        // 1 — and no meta line. A square beside the lead is merely 16 shorter than its row (its plate stretches, labels
        // top-aligned). Like every PagedShelf prop but the items, the extent is fixed at mount.
        Func<float, float> cardHeight = wide ? s_wideCardHeight : CoverCardHeight(HomeCards.CaptionLinesOf(items));

        return PagedShelf.Create<HomeCards.ShelfItem>(items,
            (item, i, cardW) => Cell(in item, cardW, squareAspect),
            cardHeight: cardHeight,
            pager: controller is null ? ShelfPager.Chevrons | ShelfPager.Pips : ShelfPager.None,
            controller: controller,
            minCardW: wide ? Design.Size.WideTileMin : HomeModuleLayout.ShelfCardMin,
            maxCardW: wide ? Design.Size.WideTileMax : HomeModuleLayout.ShelfCardMax,
            gap: HomeModuleLayout.ShelfGap, edgeFade: HomeModuleLayout.ShelfEdgeFade, snap: ShelfSnap.Page,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,   // the shared card hovers fill-only: no lift halo to reserve clearance for
            keyOf: static (item, i) => item.Card.IsBlank
                ? "home-shelf-blank:" + i.ToString(CultureInfo.InvariantCulture)
                : "home-shelf-card:" + item.Card.Uri,
            // The lead span is a LIVE prop (it follows whether THIS lead has a header image); the minimum column count
            // is frozen mount configuration, so it is set for every cover shelf and simply inert while the span is 1.
            leadSpan: leadWide ? LeadSpan : 1,
            leadCardAt: leadWide
                ? (item, i, leadW) => Cell(in item, leadW, ShelfLead.LeadAspect(leadW, HomeModuleLayout.ShelfGap, LeadSpan, Spacing.S))
                : null,
            leadMinColumns: wide ? 0 : LeadMinColumns)
           with { Key = "home-shelf:" + zone.Key };
    }

    // The shelf extents as cached delegates (one per shape, never a per-render closure).
    static readonly Func<float, float> s_wideCardHeight =
        static w => Controls.ShelfHeight(w, Design.Size.WideTileAspect, captionLines: 1, metaLine: true);
    static readonly Func<float, float> s_coverCardHeight1 = static w => Controls.ShelfHeight(w, 1f, captionLines: 1, metaLine: false);
    static readonly Func<float, float> s_coverCardHeight2 = static w => Controls.ShelfHeight(w, 1f, captionLines: 2, metaLine: false);

    static Func<float, float> CoverCardHeight(int captionLines) => captionLines switch
    {
        <= 1 => s_coverCardHeight1,
        2 => s_coverCardHeight2,
        _ => w => Controls.ShelfHeight(w, 1f, captionLines, metaLine: false),
    };

    /// <summary>The shelf pager in the sticky header's tools slot: ‹ pips › — the stock pips between two stock chevrons
    /// (the prototype's order), every action routed back through the controller (the gallery's
    /// <c>ExternalChapterHeader</c>). Reads the controller's four value
    /// signals, so only this leaf re-renders on a page change. <paramref name="leadingDivider"/> puts the 16-px
    /// vertical divider between the header's "See all" link and the pager — it comes and goes WITH the pager, so a
    /// one-page shelf shows the bare link.</summary>
    sealed class ShelfTools(ShelfController controller, bool leadingDivider) : Component
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
        => new BoxEl { Width = 1f, Height = ToolsDividerH, Shrink = 0f, Fill = Tok.StrokeDividerDefault };

    /// <summary>Props for <see cref="ShelfChapter"/>: the zone by value (a re-plan with equal content is a no-op at the
    /// embed boundary); the overlay service by reference.</summary>
    public sealed record ShelfChapterProps(Zone Zone, IOverlayService? Overlay)
    {
        public bool Equals(ShelfChapterProps? o) => o is not null && Zone.Equals(o.Zone) && ReferenceEquals(Overlay, o.Overlay);
        public override int GetHashCode() => Zone.GetHashCode();
    }

    /// <summary>A shelf zone's chapter: owns the ONE <see cref="ShelfController"/> its sticky header's pager and its
    /// shelf share (a plain field — reference-stable for the shelf's lifetime, per PagedShelf's contract). Its embed's
    /// skeleton proxy is the same static chapter builder without the controller (see the chapter box above).</summary>
    public sealed class ShelfChapter : Component
    {
        readonly ShelfController _controller = new();

        public override Element Render()
        {
            var p = UseProps<ShelfChapterProps>();
            return Chapter(p.Zone, p.Overlay, _controller);
        }
    }

    // ── 2.2 recently played: a MediaRow grid, the one component that subscribes to Recents ────────────────────────────

    public sealed record RecentGridProps(Zone Zone, IOverlayService? Overlay)
    {
        public bool Equals(RecentGridProps? o) => o is not null && Zone.Equals(o.Zone) && ReferenceEquals(Overlay, o.Overlay);
        public override int GetHashCode() => Zone.GetHashCode();
    }

    /// <summary>Owns the Recents subscription: a Recents write re-renders THIS grid only. Cell text comes from
    /// <see cref="RecentsCells.Of"/>; `Zone.Items` is the planner's capped candidate list; the week's per-day play
    /// counts (<see cref="WeekSummary"/>) are folded once per Recents version alongside the when-map.
    /// <para>The body is a <see cref="Responsive.Of(Func{float, Element}, float, float)"/> box: the measured width
    /// picks the auto-fill column count through the ENGINE's own formula (<see cref="GridEl.AutoFillColumnCount"/>, the
    /// one the grid lays out with), and <see cref="RecentsPlan.Cells"/> takes 2·cols − 1 played cells so the history
    /// tile always closes the second row flush — never a ragged third row.</para></summary>
    public sealed class RecentGrid : Component
    {
        sealed record Cache(Dictionary<string, long> When, RecentsWeek Week, uint Version);
        Cache? _cache;

        public override Element Render()
        {
            var p = UseProps<RecentGridProps>();
            var items = p.Zone.Items;

            var handle = Recents.Me;
            UseEffect(() => { if (handle.Slot > 0) Entities.EnsureEdge(FetchEdge.Recents, handle.Slot); });

            _ = Recents.Changed.Value;   // subscribe
            var cache = BuildCache(handle);

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var cells = RecentsCells.Of(items, cache.When, User.Me.LikedTrackSlots.Length, nowMs, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
            return Tree(cells, cache.Week, HostOf(p.Overlay));
        }

        /// <summary>The embed's <see cref="ComponentEl.SkeletonProxy"/>: the SAME <see cref="Tree"/> Render builds, at
        /// neutral live facts — no plays (blank "when" captions, an empty week), no liked-songs count — so it reads no
        /// signal and creates no hook (a proxy is a plain element factory). The seed's blank cards derive to the tile
        /// shape (48 art + two text lines) and the history tile closes the grid, exactly as the loaded page lays out.
        /// The "playing now" swap is a per-row BOUND prop in both paths, never a Render read.</summary>
        public static Element Skeleton(Zone zone, IOverlayService? overlay)
        {
            var cells = RecentsCells.Of(zone.Items, NoPlays, likedSongsCount: 0,
                                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TimeZoneInfo.Local, CultureInfo.CurrentCulture);
            return Tree(cells, default, HostOf(overlay));
        }

        static readonly Dictionary<string, long> NoPlays = new(StringComparer.Ordinal);

        /// <summary>The whole grid: a <see cref="Responsive"/> box that rebuilds <see cref="Grid"/> at its measured
        /// width. The ONE builder both Render and <see cref="Skeleton"/> call.</summary>
        static Element Tree(IReadOnlyList<RecentsCell> cells, RecentsWeek week, IOverlayService? host)
            => Responsive.Of(w => Grid(w, cells, week, host), fallback: HomeModuleLayout.FallbackWidth);

        /// <summary>The grid at one measured width: 2·cols − 1 played tiles + the history tile, two flush rows.</summary>
        static Element Grid(float w, IReadOnlyList<RecentsCell> cells, RecentsWeek week, IOverlayService? host)
        {
            int cols = GridEl.AutoFillColumnCount(w, RecentsMinCol, Spacing.M, RecentsMaxCols);
            int n = RecentsPlan.Cells(cols, cells.Count);
            var rows = new Element[n + 1];
            for (int i = 0; i < n; i++) rows[i] = Row(cells[i], i, host);
            rows[n] = HistoryTile(week);
            return new GridEl
            {
                MinColWidth = RecentsMinCol, MaxColumns = RecentsMaxCols,
                ColGap = Spacing.M, RowGap = Spacing.S, RowHeight = RowH, Children = rows,
            };
        }

        /// <summary>One played tile: <c>MediaRow(skin: Tile)</c> whose subtitle is [equalizer · detail · when], the
        /// equalizer PRESENT and the tertiary tail swapped to "playing on {device}" / "Playing now" exactly while the
        /// card relates to what is playing (<see cref="Controls.RelatesNow"/> — a bound prop each, so a track change
        /// touches two node props and re-renders nothing). The equalizer box opts out of skeleton derivation: a
        /// shimmer never shows a "playing" pill. A blank seed card has no uri, so it is keyed by its
        /// <paramref name="index"/> (the shelf's <c>home-shelf-blank:{i}</c> rule) — seven blanks never share a key.</summary>
        static Element Row(RecentsCell cell, int index, IOverlayService? host)
        {
            var c = cell.Card;
            string uri = c.Uri;
            var menu = HomeCardNav.MenuOf(in c);
            bool hasMenu = menu is not null && host is not null;
            string whenTail = cell.When.Length > 0 ? " · " + cell.When : "";

            var subtitle = new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Shrink = 0f, Visible = Prop.Of(() => Controls.RelatesNow(uri)),
                        Children = [Controls.Equalizer(Playback.IsPlaying, Tok.AccentDefault, EqualizerH)],
                    }.Skeletonized(false),
                    Meta(cell.Detail) with { Shrink = 1f },
                    Meta("") with
                    {
                        Color = Tok.TextTertiary, Shrink = 0f,
                        Text = Prop.Of(() => Controls.RelatesNow(uri)
                            ? " · " + RecentsCells.NowCaption(RemoteDeviceName())
                            : whenTail),
                    },
                ],
            };

            var data = new Controls.CardData(uri, cell.Title, subtitle, c.ImageUrl,
                () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
                Circular: cell.Round, Drag: HomeCardNav.DragOf(in c), ShowMenu: hasMenu,
                CoverOverride: c.Kind == HomeCardKind.Liked ? Sidebar.Cover.Liked(Design.Size.Thumb48) : null);
            var row = new BoxEl
            {
                Key = c.IsBlank ? "recent-blank:" + index.ToString(CultureInfo.InvariantCulture) : "recent:" + uri,
                Direction = 1, MinWidth = 0f,
                Children = [Controls.MediaRow(data, skin: Controls.RowSkin.Tile)],
            };
            return hasMenu ? row.WithContextMenu(host!, menu!) : row;
        }

        /// <summary>The grid's closing tile — the same row in the dashed <c>RowSkin.Outline</c>: a headphones
        /// <see cref="Controls.IconPlate"/> for a cover, the stock <see cref="SparkBars"/> 7-day strip (left = six days
        /// ago, right = today in the full accent) beside "{n} plays this week", a trailing chevron, no play FAB. Opens
        /// the listening history.</summary>
        static Element HistoryTile(RecentsWeek week)
        {
            var culture = CultureInfo.CurrentCulture;
            var today = DateTime.Today;
            var bars = new SparkBar[7];
            for (int i = 0; i < bars.Length; i++)
            {
                int daysAgo = 6 - i;
                int n = week[daysAgo];
                string day = culture.DateTimeFormat.GetAbbreviatedDayName(today.AddDays(-daysAgo).DayOfWeek);
                bars[i] = new SparkBar(n, day + " · " + Strings.Home.Plays(n), Accent: daysAgo == 0);
            }
            var strip = new BoxEl
            {
                Direction = 0, Width = HistoryStripW, Height = HistoryStripH, Shrink = 0f,
                Children =
                [
                    SparkBars.Create(new SparkBarsModel(bars),
                                     SparkBars.DefaultStyle with { Height = HistoryStripH, Gap = 2f, MinBar = 2f }),
                ],
            };
            var subtitle = new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = [strip, Meta(Strings.Home.PlaysThisWeek(week.Total)) with { Shrink = 1f }],
            };
            var data = new Controls.CardData("home:history", Loc.Get(Strings.Home.ListeningHistory), subtitle, null,
                GoRecents, OnPlay: null, ShowMenu: false,
                CoverOverride: Controls.IconPlate(Icons.Headphones, Design.Size.Thumb48, Tok.AccentSubtle, Tok.AccentTextPrimary));
            return new BoxEl
            {
                Key = "recent:history", Direction = 1, MinWidth = 0f,
                Children =
                [
                    Controls.MediaRow(data, trailing: Ui.Icon(Icons.ChevronRight, 14f, Tok.TextTertiary) with { Shrink = 0f },
                                      skin: Controls.RowSkin.Outline),
                ],
            };
        }

        /// <summary>The Connect device the bar itself says it is "Playing on", or null while playback is local (or
        /// nobody's). The PlayerBar idiom (Shell/Shell.PlayerBar.cs `DeviceRoster.RemoteSlot`); every read subscribes,
        /// so the bound caption follows a transfer.</summary>
        static string? RemoteDeviceName()
        {
            _ = Playback.Devices.Changed.Value;
            var rows = Playback.Devices.Rows;
            int slot = Shell.DeviceRoster.RemoteSlot(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, rows);
            return slot >= 0 ? rows[slot].Name : null;
        }

        Cache BuildCache(Recents handle)
        {
            uint version = handle.Version;
            if (_cache is { } c && c.Version == version) return c;

            var snapshot = RecentsSnapshot.Of(in handle);
            var when = new Dictionary<string, long>(snapshot.Count, StringComparer.Ordinal);
            for (int i = 0; i < snapshot.Rows.Length; i++)
            {
                var target = snapshot.Targets[i];
                string uri = Entities.TableFor(target.Kind) is { } table && (uint)target.Slot < (uint)table.Count
                    ? table.Id[target.Slot].ToString() : "";
                if (uri.Length > 0) when[uri] = snapshot.Rows[i].PlayedAtMs;
            }
            var week = WeekSummary.Of(snapshot.Rows, DateOnly.FromDateTime(DateTime.Now), TimeZoneInfo.Local);
            return _cache = new Cache(when, week, version);
        }
    }

    // ── 2.3 release list: "From artists you follow" — a SectionHeader sub-block over a two-column MediaRow grid ──────

    static Element ReleaseList(Zone zone, IOverlayService? overlay)
    {
        var items = zone.Items;
        int n = Math.Min(items.Count, ReleaseRowsMax);
        if (n == 0) return new BoxEl();

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tz = TimeZoneInfo.Local;
        var culture = CultureInfo.CurrentCulture;
        var host = HostOf(overlay);

        var rows = new Element[n];
        for (int i = 0; i < n; i++) rows[i] = ReleaseRow(items[i], nowMs, tz, culture, host);
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Stretch,
            Children =
            [
                Ui.SectionHeader(zone.Title ?? Loc.Get(Strings.Home.Zone.FromArtistsYouFollow),
                                 zone.Subtitle ?? Loc.Get(Strings.Home.ReleasedThisWeek)) with { Margin = default },
                new GridEl
                {
                    MinColWidth = ListMinCol, MaxColumns = ListMaxCols,
                    ColGap = Spacing.M, RowGap = Spacing.XS, RowHeight = RowH, Children = rows,
                },
            ],
        };
    }

    static Element ReleaseRow(HomeCard c, long nowMs, TimeZoneInfo tz, CultureInfo culture, IOverlayService? host)
    {
        string type = ReleaseListRules.TypeLabel(c.Kind);
        string when = ReleaseListRules.DateLabel(c.ReleasedAtMs, nowMs, tz, culture);
        var trailingKids = new List<Element>(2);
        if (type.Length > 0) trailingKids.Add(Controls.RowChip(type));
        if (when.Length > 0) trailingKids.Add(Design.Type.TrackMeta(when) with { Color = Tok.TextTertiary, MaxLines = 1 });
        Element? trailing = trailingKids.Count == 0 ? null : new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f, Children = trailingKids.ToArray(),
        };

        var menu = HomeCardNav.MenuOf(in c);
        bool hasMenu = menu is not null && host is not null;
        string sub = HomeCards.PlainText(c.Subtitle);
        var data = new Controls.CardData(c.Uri, c.Title, sub.Length > 0 ? Meta(sub) : null, c.ImageUrl,
            () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
            Drag: HomeCardNav.DragOf(in c), ShowMenu: hasMenu);
        var row = new BoxEl { Key = "release:" + c.Uri, Direction = 1, MinWidth = 0f, Children = [Controls.MediaRow(data, trailing: trailing)] };
        return hasMenu ? row.WithContextMenu(host!, menu!) : row;
    }

    // ── 2.4 clusters: "Because you like" / "Because you listen to" — Ui.Card(SectionHeader + ≤3 MediaRow) ────────────

    static Element ClusterGrid(Zone zone, IOverlayService? overlay)
    {
        var clusters = zone.Clusters;
        if (clusters is not { Count: > 0 }) return new BoxEl();
        var host = HostOf(overlay);
        int n = Math.Min(clusters.Count, ClusterMax);
        var cards = new Element[n];
        for (int i = 0; i < n; i++) cards[i] = ClusterCard(clusters[i], host);
        return new GridEl
        {
            MinColWidth = ClusterMinCol, MaxColumns = ClusterMaxCols,
            ColGap = Spacing.M, RowGap = Spacing.M, Children = cards,
        };
    }

    static Element ClusterCard(ZoneCluster cluster, IOverlayService? host)
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
        int n = Math.Min(rowsSrc.Count, ClusterRows);
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var c = rowsSrc[i];
            var menu = HomeCardNav.MenuOf(in c);
            bool hasMenu = menu is not null && host is not null;
            string sub = HomeCards.PlainText(c.Subtitle);
            var data = new Controls.CardData(c.Uri, c.Title, sub.Length > 0 ? Meta(sub) : null, c.ImageUrl,
                () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
                Circular: c.Kind == HomeCardKind.Artist, Drag: HomeCardNav.DragOf(in c), ShowMenu: hasMenu);
            var row = new BoxEl { Key = "cluster-row:" + c.Uri, Direction = 1, MinWidth = 0f, Children = [Controls.MediaRow(data, artEdge: ClusterArt)] };
            rows[i] = hasMenu ? row.WithContextMenu(host!, menu!) : row;
        }

        return Ui.Card(header, new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = rows })
            with { MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Gap = Spacing.S };
    }

    // ── 2.5 browse tiles: stock SettingsCards in the tile skin (48 art plate, one-line text), then the Charts sub-block ──

    static Element BrowseTiles(Zone zone)
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
                HeaderIconElement = Controls.Artwork(c.ImageUrl, TileArt, TileArt, Radii.Control),
                IsClickEnabled = true,
                OnClick = () => HomeCardNav.Open(in c),
                Style = Controls.TileCardStyle, Parts = Controls.TileCardParts,
            }) with { Key = "browse-tile:" + c.Uri };
        }
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = Design.Size.SectionGap, AlignItems = FlexAlign.Stretch,
            Children =
            [
                new GridEl { MinColWidth = TilesMinCol, MaxColumns = TilesMaxCols, ColGap = Spacing.M, RowGap = Spacing.M, Children = tiles },
                ChartsBlock(),
            ],
        };
    }

    /// <summary>The Browse facet's "Charts" sub-block (RCA 2026-09-30): "Charts" opens the Charts CATEGORY page
    /// (<see cref="ChartPages.Charts"/>, the Browse directory's own route for it); "Top 50" / "Viral 50" open the actual
    /// playlists, picked data-driven from the Featured Charts section's items (<see cref="ChartTilePick"/>), each on the
    /// category page until that section lands or when it carries no match. Every route carries its title as the
    /// frame-one arg. The component owns the section's demand and the derived tile model; its skeleton proxy is the same
    /// static tree over <see cref="ChartTilesModel.Fallback"/>. The two chart titles are the same literals the previous
    /// pass carried — the loc table has captions for these tiles (<c>home.chartsCaption.*</c>) but no title keys yet.</summary>
    public static Element ChartsBlock()
        => Embed.Comp(static () => new ChartsBlockView()) with { Key = "home:charts", SkeletonProxy = s_chartsProxy };

    static readonly Func<Element> s_chartsProxy = static () => ChartsTree(ChartTilesModel.Fallback);

    /// <summary>The Charts block: demands the Featured Charts section once per scope through the query layer
    /// (<see cref="Home.EnsureSection"/> as a browse section — the demand Browse's Charts band and the section drill use)
    /// and renders from the derived <see cref="ChartTilesModel"/>, a memo that re-renders only when a tile's target
    /// actually changes.</summary>
    public sealed class ChartsBlockView : Component
    {
        static readonly Action s_demand = static () => Home.EnsureSection(Entities.BrowseSection(ChartSections.Featured), browse: true);
        static readonly Func<ChartTilesModel> s_read = ReadChartTiles;

        public override Element Render()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            UseEffect(s_demand, DepKey.From((int)epoch));
            var model = UseComputed(s_read);
            return ChartsTree(model.Value);
        }
    }

    /// <summary>The tile model from the Featured Charts section's items, in section order, through the Home model reader
    /// (<see cref="SectionReader.Of"/>). Subscribes to the section rows, their card edge and the playlist rows (a card's
    /// title lands on its playlist row); before the section lands the list is empty and both tiles read the fallback.</summary>
    static ChartTilesModel ReadChartTiles()
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        _ = scope.Sections.Changed.Value;
        _ = scope.Edges.SectionCards.Changed.Value;
        _ = scope.Playlists.Changed.Value;
        var featured = Entities.BrowseSection(ChartSections.Featured);
        if (!featured.IsValid) return ChartTilesModel.Fallback;
        var cards = SectionReader.Of(featured).Cards;
        if (cards.Count == 0) return ChartTilesModel.Fallback;
        var items = new FeaturedChartItem[cards.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var c = cards[i];
            items[i] = c.IsBlank ? new FeaturedChartItem("", null) : new FeaturedChartItem(c.Uri, c.Title);
        }
        return ChartTilePick.Of(items);
    }

    /// <summary>The block's one static tree (Render and the skeleton proxy both build it).</summary>
    static Element ChartsTree(ChartTilesModel m)
    {
        Element[] tiles =
        [
            ChartTile("charts", Loc.Get(Strings.Home.Charts), Loc.Get(Strings.Home.UpdatedDaily), Icons.Equalizer, ChartTileTarget.Fallback),
            ChartTile("top50", "Top 50", Loc.Get(Strings.Home.ChartsCaption.Top50Global), Icons.Globe, m.Top50),
            ChartTile("viral50", "Viral 50", Loc.Get(Strings.Home.ChartsCaption.Viral50Country), Icons.Star, m.Viral50),
        ];
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Stretch,
            Children =
            [
                Ui.SectionHeader(Loc.Get(Strings.Home.Charts), Loc.Get(Strings.Home.UpdatedDaily)) with { Margin = default },
                new GridEl { MinColWidth = TilesMinCol, MaxColumns = ChartsMaxCols, ColGap = Spacing.M, RowGap = Spacing.M, Children = tiles },
            ],
        };
    }

    /// <summary>A chart tile is the browse tile with a glyph PLATE where the category art would be — the same 48 edge, so
    /// the two grids line up. Keyed by the tile's role, so a target landing re-skins the same card.</summary>
    static Element ChartTile(string key, string title, string description, string glyph, ChartTileTarget target)
        => SettingsCard.Create(new SettingsCard.Options
        {
            Header = title, Description = description, IsClickEnabled = true,
            HeaderIconElement = Controls.IconPlate(glyph, TileArt, Tok.FillControlDefault, Tok.TextSecondary),
            OnClick = () => Shell.GoTo(ChartTileRoute(target)),
            Style = Controls.TileCardStyle, Parts = Controls.TileCardParts,
        }) with { Key = "chart-tile:" + key };

    /// <summary>A picked item opens as itself (<see cref="Shell.For"/>: the playlist page, its title the arg); the
    /// fallback — and a uri no page renders — is the Charts category page with its title (<c>BrowseTiles.PageRoute</c>,
    /// the Browse directory's own route for it). UI thread (both parse/intern).</summary>
    static Shell.Route ChartTileRoute(ChartTileTarget target)
    {
        if (!target.IsFallback)
        {
            var route = Shell.For(EntityUri.Parse(target.Uri.AsSpan()), target.Title);
            if (route.Kind != Shell.RouteKind.NotFound) return route;
        }
        return global::Wavee.BrowseTiles.PageRoute(ChartPages.Charts, Loc.Get(Strings.Home.Charts));
    }

    // ── 2.6 episodes: MediaRow(56, two-line title) with the podcast caption, a ProgressBar while in progress ─────────

    static Element EpisodeGrid(Zone zone, int maxColumns)
    {
        var cards = ShelfLead.Merge(zone.Items, zone.Lead);   // the lead episode is row 1, once
        if (cards.Count == 0) return new BoxEl();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tz = TimeZoneInfo.Local;
        var culture = CultureInfo.CurrentCulture;
        var rows = new Element[cards.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = EpisodeRow(cards[i], nowMs, tz, culture);
        return new GridEl
        {
            MinColWidth = ListMinCol, MaxColumns = maxColumns,
            ColGap = Spacing.M, RowGap = Spacing.XS, Children = rows,   // RowHeight auto: two-line titles + a progress rung
        };
    }

    static Element EpisodeRow(HomeCard e, long nowMs, TimeZoneInfo tz, CultureInfo culture)
    {
        string show = e.ShowName is { Length: > 0 } s ? s : HomeCards.PlainText(e.Subtitle);
        string date = EpisodeCaption.Date(e.ReleasedAtMs, nowMs, tz, culture);
        string length = e.ResumeMs > 0 ? EpisodeCaption.Remaining(e.DurationMs, e.ResumeMs) : EpisodeCaption.Duration(e.DurationMs);
        string caption = date.Length > 0 && length.Length > 0 ? date + " · " + length : date.Length > 0 ? date : length;

        var metaKids = new List<Element>(3);
        if (e.IsExplicit) metaKids.Add(Controls.ExplicitBadge(14f));
        if (e.HasVideo) metaKids.Add(Ui.Icon(Icons.Video, 12f, Tok.TextTertiary));
        if (caption.Length > 0) metaKids.Add(Meta(caption) with { Shrink = 1f });
        Element metaRow = new BoxEl { Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = metaKids.ToArray() };
        Element meta = e.ResumeMs > 0
            ? new BoxEl
            {
                Direction = 1, Gap = Spacing.XS, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Children = [metaRow, ProgressBar.Determinate((float)EpisodeCaption.ProgressFraction(e.ResumeMs, e.DurationMs), ProgressW)],
            }
            : metaRow;

        var data = new Controls.CardData(e.Uri, e.Title, show.Length > 0 ? Meta(show) : null, e.ImageUrl,
            () => HomeCardNav.Open(in e), () => HomeCardNav.Play(in e), TitleLines: 2, ShowMenu: false);
        return new BoxEl { Key = "episode:" + e.Uri, Direction = 1, MinWidth = 0f, Children = [Controls.MediaRow(data, artEdge: EpisodeArt, meta: meta)] };
    }

    // ── 2.7 the empty facet: the app's one vacancy, with the facet's own words ───────────────────────────────────────

    /// <summary>Which empty-facet words a zone gets, from the planner's own zone key (<c>home:audiobooks:empty</c>,
    /// <c>podcasts:empty</c>, <c>home:empty:{facet}</c>). Pure, so the mapping is testable without an element.</summary>
    public static (string Title, string Body, string Action) EmptyFacetKeys(string zoneKey)
    {
        if (zoneKey.StartsWith("home:audiobooks", StringComparison.Ordinal))
            return (Strings.Home.Empty.AudiobooksTitle, Strings.Home.Empty.AudiobooksBody, Strings.Home.Empty.AudiobooksAction);
        if (zoneKey.StartsWith("podcasts", StringComparison.Ordinal))
            return (Strings.Home.Empty.PodcastsTitle, Strings.Home.Empty.PodcastsBody, Strings.Home.Empty.PodcastsAction);
        return (Strings.Home.Empty.GenericTitle, Strings.Home.Empty.GenericBody, Strings.Home.Empty.GenericAction);
    }

    static Element EmptyFacet(Zone zone)
    {
        var (title, body, action) = EmptyFacetKeys(zone.Key);
        return Controls.Vacancy(Controls.VacancyVoice.Empty,
            title: Loc.Get(title), subtitle: Loc.Get(body), actionLabel: Loc.Get(action),
            onAction: static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Browse)));
    }

    // ══ 3. SHARED ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A one-line secondary caption, ellipsised.</summary>
    static TextEl Meta(string s)
        => Design.Type.TrackMeta(s) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>The "Listening history" drill (the Recents header's tools link).</summary>
    public static void GoRecents() => Shell.GoTo(new Shell.Route(Shell.RouteKind.Recents));

    /// <summary>A zone's own "See all", from its backing section slot; null when the zone carries none (an aggregate
    /// zone with no single backing section) — the header's tools then carry no "See all" link.</summary>
    public static Action? SeeAll(Zone zone)
    {
        if (zone.SectionSlot < 0) return null;
        int slot = zone.SectionSlot;
        return () =>
        {
            // The section's own drill route (title arg + Home origin), the one composer every "See all" shares.
            var route = HomeCardNav.SectionRoute(HomeSectionView.Of(new Section(slot)), browse: false);
            if (route.IsNone) return;
            Shell.GoTo(route, HomeCardNav.HomeOrigin);
        };
    }
}
