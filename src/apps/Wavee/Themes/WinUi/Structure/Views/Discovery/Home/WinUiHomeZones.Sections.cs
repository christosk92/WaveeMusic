// ── Themes/WinUi/Structure/Views/Discovery/Home/WinUiHomeZones.Sections.cs — the richer section bodies of the Home concept (pinned Home, lane H) ──
//
// The concept's WinUI versions (`home-concept.html`, home-gap P4, P6, P8 and the video card), built from the pieces the other bodies use:
//
//   MoodPlates     a grid of flat colour plates (the mix's own colour), the name 20 semibold over its first sentence      tools: See all N
//   BookShelf      PagedShelf of the house surface: author, "★ 4.7 · 20 hr 56 min", the plan as a pill; every card 4 lines tools: See all · pager
//   PodcastGroups  Ui.Card: the reason (avatar + "Because you listen to" + the show as a link), the show (cover, name, description,
//                  "Playlist · Spotify · 50 songs", Play), 4 rows; pages of two rows at most, no empty slot (GridFill)
//   VideoTiles(1)  one episode as a video card: the still (16:9) with "Video" and the length on it, the progress hairline, the
//                  show, the title, "20 Jul · 1 hr 24 min · not started", Play and Save
//   entity header  a section that hangs off something: its cover, the lead words over its name as a link
//
// Deferred (owner calls D3, D4, D6): hover audio previews, the canvas loop on a card, the episode's video preview and WinUI's header tint.
// Every box here has its height from its data the moment it mounts: nothing lands later and moves it.

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

namespace Wavee.Themes.WinUi;

internal static partial class WinUiHomeZones
{
    // ── the entity header ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A section that hangs off something ("Popular with listeners of" The Pog State): the thing's cover, the lead words in the caption
    /// voice over its name in the module header voice, the name a link to it; the tools at the end.</summary>
    static Element EntityHeader(HeaderEntity e, Element? tools, WinUiHomeLayout layout, Element? tail = null)
    {
        string uri = e.Uri, name = e.Name;
        var link = new BoxEl
        {
            Direction = 0, MinWidth = 0f, Shrink = 1f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
            OnClick = () => HomeVerbs.OpenEntity(uri, name),
            Children = [R.Type.ModuleHeader(name) with
            {
                HoverColor = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            }],
        };
        return new BoxEl
        {
            Direction = 0, Gap = WinUiHomeRules.HeaderGap, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = layout.HeaderToolsH,
            Children =
            [
                Controls.Artwork(e.Image, WinUiHomeRules.EntityCover, WinUiHomeRules.EntityCover, Radii.Control),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        Ui.Caption(e.Lead).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        link,
                    ],
                },
                tools ?? new BoxEl(),
                tail ?? new BoxEl(),
            ],
        };
    }

    // ── extents (the pinned board packs a full row at its chapter's height: `ChapterExtent`) ─────────────────────────────

    /// <summary>The entity header's height: the cover beside the lead's caption line over the name's title line, never under the tools' floor.</summary>
    static float EntityHeadExtent(WinUiHomeLayout layout)
        => MathF.Max(layout.HeaderToolsH, MathF.Max(WinUiHomeRules.EntityCover, WinUiHomeRules.SubtitleLine + WinUiHomeRules.TitleLine));

    /// <summary>The mood plates' grid at <paramref name="width"/>: its rows of plates and the gaps between them.</summary>
    static float MoodPlatesExtent(Zone zone, WinUiHomeLayout layout, float width)
    {
        int n = Math.Min(zone.Items.Count, layout.PlatesMax);
        if (n == 0) return 0f;
        int rows = GridFill.Rows(n, GridFill.Columns(n, layout.Plates.Columns(width, WinUiHomeRules.CardGap)));
        return rows * WinUiHomeRules.MoodPlateH + (rows - 1) * WinUiHomeRules.CardGap;
    }

    /// <summary>The video card in its row form: the card's padding and stroke around the still (the copy beside it is shorter); 0 in the stacked form,
    /// whose title wraps to its width (measured).</summary>
    static float VideoCardExtent(WinUiHomeLayout layout)
    {
        if (layout.Tier < WidthTier.Standard) return 0f;
        var pad = Spacing.CardPad;
        return pad.Top + pad.Bottom + WinUiHomeRules.VideoStillW / R.Space.WideTileAspect;
    }

    static readonly Func<ZoneCluster, float> s_clusterExtent = ClusterExtent, s_wideClusterExtent = WideClusterExtent;

    /// <summary>A preview card's height: the reason, the offer, its rows, inside the card's padding.</summary>
    static float ClusterExtent(ZoneCluster c)
    {
        var pad = Spacing.CardPad;
        float h = pad.Top + pad.Bottom + WinUiHomeRules.ReasonAvatar;
        if (c.Header is not null) h += Spacing.M + WinUiHomeRules.PreviewCover;
        return h + Spacing.M + Math.Min(c.Rows.Count, WinUiHomeRules.ClusterRows) * Shape.Row(WinUiHomeRules.ClusterArt).MinHeight;
    }

    /// <summary>A lone last preview card's height in its wide form: the reason over the offer beside its rows in two columns.</summary>
    static float WideClusterExtent(ZoneCluster c)
    {
        var pad = Spacing.CardPad;
        float left = WinUiHomeRules.ReasonAvatar + (c.Header is not null ? Spacing.M + WinUiHomeRules.PreviewCover : 0f);
        float right = GridFill.Rows(Math.Min(c.Rows.Count, WinUiHomeRules.ClusterRows), WinUiHomeRules.WideClusterRowCols) * Shape.Row(WinUiHomeRules.ClusterArt).MinHeight;
        return pad.Top + pad.Bottom + MathF.Max(left, right);
    }

    /// <summary>A responsive grid's first-frame width (before its box is measured): the band's widest fit.</summary>
    static float FirstWidth(WinUiGridBand band, float gap) => band.Floor * band.MaxCols + gap * (band.MaxCols - 1);

    // ── mood plates (P6) ────────────────────────────────────────────────────────────────────────────────────────────────

    readonly record struct PlateKey(HomeCard Card, string Uri, string Title, string Line, uint Accent);

    static readonly Func<PlateKey, Element> s_plate = static k => Plate(in k);

    /// <summary>"Soundtrack your Monday afternoon": up to the tier's plate count of flat plates in its plate columns, one height.</summary>
    static Element MoodPlates(Zone zone, WinUiHomeLayout layout, RenderContext? hooks)
    {
        int n = Math.Min(zone.Items.Count, layout.PlatesMax);
        if (n == 0) return new BoxEl();
        var plates = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var c = zone.Items[i];
            plates[i] = ItemMemo.Of(hooks, new PlateKey(c, c.Uri, c.Title, SectionCopy.PlateLine(in c), c.Accent), s_plate);
        }
        var band = layout.Plates;
        return GridFill.Responsive("mood-plates:" + zone.Key, n, w => band.Columns(w, WinUiHomeRules.CardGap),
            (cols, w) => GridFill.Build(plates, cols, w, WinUiHomeRules.CardGap, WinUiHomeRules.CardGap, WinUiHomeRules.MoodPlateH),
            FirstWidth(band, WinUiHomeRules.CardGap));
    }

    /// <summary>One plate: the mix's own dark colour (the card fill where it has none), the name 20 semibold and its line in the on-media inks.</summary>
    static Element Plate(in PlateKey k)
    {
        var c = k.Card;
        bool coloured = k.Accent != 0u;
        ColorF fill = coloured ? ColorScience.ToColor(k.Accent) : default;
        var kids = new List<Element>(2)
        {
            R.Type.ModuleHeader(k.Title) with
            {
                Color = coloured ? Prop.Of(static () => R.OnMedia.Ink) : Tok.TextPrimary,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        };
        if (k.Line.Length > 0)
            kids.Add(Ui.Caption(k.Line) with
            {
                Color = coloured ? Prop.Of(static () => R.OnMedia.InkSecondary) : Tok.TextSecondary,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        var plate = new BoxEl
        {
            Key = "mood-plate:" + k.Uri, Direction = 1, Justify = FlexJustify.End, MinWidth = 0f, ClipToBounds = true,
            Corners = Radii.CardAll, Padding = Edges4.All(WinUiHomeRules.MoodPlatePad),
            Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = () => HomeCardNav.Open(in c),
            Children = kids.ToArray(),
        }.Interactive(Interaction.Tile);
        // The tile ramp paints the card fill; a coloured plate keeps its own colour at rest and a step toward the ink on hover and press.
        return coloured
            ? plate with
            {
                Fill = fill, HoverFill = ColorF.Lerp(fill, R.OnMedia.Ink, WinUiHomeRules.PlateHoverMix),
                PressedFill = ColorF.Lerp(fill, R.OnMedia.Ink, WinUiHomeRules.PlatePressMix), BorderWidth = 0f,
            }
            : plate;
    }

    // ── the book shelf (P8) ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What a book card paints, as a value (PagedShelf compares items by value).</summary>
    readonly record struct BookItem(HomeCard Card, string Title, string Author, string Facts, string Plan, string? Art);

    static readonly Func<float, float> s_bookHeight =
        static w => SurfaceGeometry.ShelfHeight(w, 1f, WinUiHomeRules.BookCaptionLines, metaLine: false, flush: true);

    /// <summary>"More like The Manager's Path": the cover shelf's band and pager, each card its author, rating and length, and the plan pill.</summary>
    static Element BookShelf(Zone zone, IOverlayService? overlay, ShelfController? controller, WinUiHomeLayout layout)
    {
        var band = layout.Shelf;
        var culture = CultureInfo.CurrentCulture;
        var items = new BookItem[zone.Items.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var c = zone.Items[i];
            items[i] = new BookItem(c, c.Title, c.Author ?? HomeCards.PlainText(c.Subtitle), SectionCopy.BookFacts(in c, culture), c.Signifier ?? "", c.ImageUrl);
        }
        var host = HostOf(overlay);
        return PagedShelf.Create<BookItem>(items,
            (item, i, cardW) => BookCell(in item, cardW, host),
            onInvoke: static (item, _) => HomeCardNav.Open(item.Card),
            cardWidthBound: true,
            cardHeight: s_bookHeight,
            pager: controller is null ? (band.Pager ? ShelfPager.Chevrons | ShelfPager.Pips : ShelfPager.None) : ShelfPager.None,
            controller: controller,
            minCardW: band.MinCardW, maxCardW: band.MaxCardW, maxColumns: band.MaxColumns, fixedCardW: band.FixedCardW,
            gap: band.Gap, edgeFade: HomeModuleLayout.ShelfEdgeFade, snap: ShelfSnap.Page,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight, lift: ShelfLift.None,
            keyOf: static (item, i) => item.Card.IsBlank ? "home-book-blank:" + i.ToString(CultureInfo.InvariantCulture) : "home-book:" + item.Card.Uri)
            with { Key = "home-books:" + zone.Key };
    }

    static Element BookCell(in BookItem item, float cardW, IOverlayService? host)
    {
        var c = item.Card;
        var lines = new List<Element>(3);
        if (item.Author.Length > 0) lines.Add(Meta(item.Author));
        if (item.Facts.Length > 0) lines.Add(Meta(item.Facts) with { Color = Tok.TextTertiary });
        if (item.Plan.Length > 0) lines.Add(new BoxEl { Direction = 0, MinWidth = 0f, Children = [Controls.RowChip(item.Plan)] });
        var menu = HomeCardNav.MenuOf(in c);
        bool hasMenu = menu is not null && host is not null;
        var data = new Controls.CardData(c.Uri, item.Title,
            new BoxEl { Direction = 1, MinWidth = 0f, Gap = WinUiHomeRules.SectionBodyGap, AlignItems = FlexAlign.Stretch, Children = lines.ToArray() },
            item.Art, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c), Drag: HomeCardNav.DragOf(in c), ShowMenu: hasMenu)
            { CaptionLines = WinUiHomeRules.BookCaptionLines, Menu = hasMenu ? menu : null };
        return Controls.Surface(data, Shape.Shelf(WinUiHomeRules.BookCaptionLines, metaLine: false) with { Flush = true }, cardW);
    }

    // ── preview cards (P4) ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Because you like": the cards on pages of two rows at most (GridFill: no empty slot, the last row's cards grow, a lone last card in
    /// its wide form), one page at a time, sliding sideways; the previous and next buttons in the header, the pips centred under the grid, the
    /// edge chevrons on hover. The section keeps the tallest page's height.</summary>
    static Element ClusterGrid(Zone zone, WinUiHomeLayout layout, ShelfController? controller)
    {
        var clusters = zone.Clusters;
        if (clusters is not { Count: > 0 }) return new BoxEl();
        var band = layout.Clusters;
        string key = zone.Key;
        return FluentGpu.Controls.Responsive.Of("clusters:" + key, w => band.Columns(w, WinUiHomeRules.CardGap),
            fit => ClusterPages(key, clusters, fit, controller), fallback: FirstWidth(band, WinUiHomeRules.CardGap));
    }

    static Element ClusterPages(string key, IReadOnlyList<ZoneCluster> clusters, int fit, ShelfController? controller)
    {
        var pages = GridFill.Pages(clusters.Count, fit, clusters);
        float height = GridFill.PagesHeight(clusters, pages, WinUiHomeRules.CardGap, s_clusterExtent, s_wideClusterExtent);
        var shelf = GridFill.Paged(key, clusters, pages, WinUiHomeRules.CardGap, WinUiHomeRules.CardGap, height,
            static c => ClusterCard(c), static c => ClusterCard(c, wide: true), controller, ShelfPager.HoverEdge, Icons.ChevronLeft, Icons.ChevronRight);
        if (pages.Length <= 1) return shelf;
        var pips = new BoxEl
        {
            Direction = 0, Height = WinUiHomeRules.PipsRowH, MinWidth = 0f, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Children = controller is null ? [] : [Embed.Comp(() => new ClusterPips(controller)) with { Key = "cluster-pips" }],
        };
        return new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Gap = WinUiHomeRules.CardGap, Children = [shelf, pips] };
    }

    /// <summary>The pips under the preview cards: the stock pips over the chapter's controller (the selected one larger), each a page.</summary>
    sealed class ClusterPips(ShelfController controller) : Component
    {
        public override Element Render()
        {
            int count = controller.PageCount.Value;
            int page = controller.Page.Value;
            return count <= 1 ? new BoxEl() : PipsPager.Controlled(count, page, controller.GoTo);
        }
    }

    /// <summary>One preview card: the reason, the playlist it offers, its first four rows. <paramref name="wide"/> (a lone card on the last row):
    /// the reason over the offer on the left, the rows in two columns beside them.</summary>
    static Element ClusterCard(ZoneCluster cluster, bool wide = false)
    {
        var kids = new List<Element>(3) { ReasonLine(cluster) };
        if (cluster.Header is { } h) kids.Add(Offer(h));
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
        if (wide)
        {
            var side = new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.M, Children = kids.ToArray() };
            var grid = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                Children = [new GridEl { Columns = RhythmCards.Stars(WinUiHomeRules.WideClusterRowCols), ColGap = WinUiHomeRules.CardGap, Children = rows }],
            };
            return Ui.Card(new BoxEl { Direction = 0, MinWidth = 0f, Gap = WinUiHomeRules.CardGap, AlignItems = FlexAlign.Start, Children = [side, grid] })
                with { MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Layout = WinUiDiscoveryMotion.Shove };
        }
        kids.Add(new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = rows });
        return Ui.Card(kids.ToArray()) with { MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Gap = Spacing.M, Layout = WinUiDiscoveryMotion.Shove };
    }

    /// <summary>Why the card is here: the artist's picture and "For fans of" + the artist as a link, or the reason alone ("Based on your recent
    /// listening"). One caption line either way, so every card's offer starts at the same height.</summary>
    static Element ReasonLine(ZoneCluster cluster)
    {
        var kids = new List<Element>(3);
        if (cluster.Reason is { } r)
        {
            string uri = r.Uri, name = r.Name;
            kids.Add(Controls.Artwork(r.Image, WinUiHomeRules.ReasonAvatar, WinUiHomeRules.ReasonAvatar, WinUiHomeRules.ReasonAvatar / 2f));
            kids.Add(Ui.Caption(r.Lead).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f, Margin = new Edges4(0f, 0f, -Spacing.XS, 0f) });
            kids.Add(new BoxEl
            {
                Direction = 0, MinWidth = 0f, Shrink = 1f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                OnClick = () => HomeVerbs.OpenEntity(uri, name),
                Children = [Ui.Caption(name) with
                {
                    Color = Tok.AccentTextPrimary, HoverColor = Tok.AccentTextSecondary,
                    MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                }],
            });
        }
        else
            kids.Add(Ui.Caption(cluster.Over).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f });
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = WinUiHomeRules.ReasonAvatar,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The playlist the card offers: its cover, its name (opens it), its description, "Playlist · Spotify · 50 songs" and Play.</summary>
    static Element Offer(HomeCard h)
    {
        var hc = h;
        string description = HomeCards.PlainText(h.Subtitle);
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f, Cursor = CursorId.Hand, OnClick = () => HomeCardNav.Open(in hc),
                    Children = [Controls.Artwork(h.ImageUrl, WinUiHomeRules.PreviewCover, WinUiHomeRules.PreviewCover, Radii.Control)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Gap = WinUiHomeRules.SectionBodyGap,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                            OnClick = () => HomeCardNav.Open(in hc),
                            Children = [Ui.BodyStrong(h.Title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
                        },
                        // Two fixed lines under the name (an empty description keeps its line): the Play button sits at one height on every card.
                        Ui.Caption(description.Length > 0 ? description : " ").Secondary() with
                            { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        Meta(SectionCopy.PreviewFacts(in h)) with { Color = Tok.TextTertiary },
                        new BoxEl
                        {
                            Direction = 0, MinWidth = 0f,
                            Children = [Button.Create(Loc.Get(Strings.Home.Play), () => HomeCardNav.Play(in hc), ButtonAppearance.Accent, ControlSize.Small, Icons.Play)
                                with { Shrink = 0f }],
                        },
                    ],
                },
            ],
        };
    }

    // ── the video card (an episode on All) ──────────────────────────────────────────────────────────────────────────────

    static bool IsOneEpisode(Zone zone) => zone.Items.Count == 1 && zone.Items[0].Kind == HomeCardKind.Episode;

    /// <summary>"Videos you might like": the episode's own still (16:9) with "Video" and the length on it and the progress hairline under it,
    /// then the show, the title (two lines), its facts and Play and Save. A row from Standard up, a stack below it.</summary>
    static Element VideoCard(HomeCard e, WinUiHomeLayout layout)
    {
        var ec = e;
        bool row = layout.Tier >= WidthTier.Standard;
        float progress = SectionCopy.EpisodeProgress(in e);
        var stillLayers = new List<Element>(4)
        {
            Controls.CoverFill(e.VideoThumbUrl ?? e.ImageUrl, Radii.Control, WinUiHomeRules.VideoStillW * 2f),
            new BoxEl
            {
                Direction = 0, AlignSelf = FlexAlign.End, Justify = FlexJustify.SpaceBetween, MinWidth = 0f, HitTestVisible = false,
                Padding = Edges4.All(WinUiHomeRules.VideoBadgeInset),
                Children = [Badge(Loc.Get(Strings.Home.Video)), Badge(EpisodeCaption.Duration(e.DurationMs))],
            },
        };
        if (progress > 0f)
            stillLayers.Add(new BoxEl
            {
                AlignSelf = FlexAlign.End, Height = WinUiHomeRules.VideoProgressH, MinWidth = 0f, HitTestVisible = false, Direction = 0,
                Children =
                [
                    new BoxEl { Grow = progress, Basis = 0f, Fill = Tok.AccentDefault },
                    new BoxEl { Grow = 1f - progress, Basis = 0f, Fill = Prop.Of(static () => R.OnMedia.ScrimRest) },
                ],
            });
        Element still = new BoxEl
        {
            ZStack = true, MinWidth = 0f, Shrink = 0f, AspectRatio = R.Space.WideTileAspect, ClipToBounds = true, Corners = Radii.ControlAll,
            Width = row ? WinUiHomeRules.VideoStillW : float.NaN,
            Cursor = CursorId.Hand, OnClick = () => HomeCardNav.Open(in ec), Children = stillLayers.ToArray(),
        };

        string show = e.ShowName is { Length: > 0 } s ? s : HomeCards.PlainText(e.Subtitle);
        string facts = SectionCopy.EpisodeFacts(in e, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        var copy = new BoxEl
        {
            Direction = 1, Grow = row ? 1f : 0f, Shrink = 1f, Basis = row ? 0f : float.NaN, MinWidth = 0f, Gap = WinUiHomeRules.SectionBodyGap,
            Children =
            [
                Ui.Caption(show).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                R.Type.ModuleHeader(e.Title) with { MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                Meta(facts) with { Color = Tok.TextTertiary },
                new BoxEl { Grow = 1f, MinHeight = Spacing.M }.Skeletonized(false),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        Button.Create(Loc.Get(Strings.Home.Play), () => HomeCardNav.Play(in ec), ButtonAppearance.Accent, ControlSize.Medium, Icons.Play) with { Shrink = 0f },
                        Controls.Named(Embed.Comp(() => new Controls.SaveButton { Uri = ec.Uri, Name = ec.Title }) with { Key = "save:" + e.Uri },
                                       Loc.Get(Strings.Home.Save)),
                    ],
                },
            ],
        };
        return Ui.Card(new BoxEl
        {
            Key = "video-card:" + e.Uri, Direction = (byte)(row ? 0 : 1), Gap = Spacing.L, MinWidth = 0f,
            AlignItems = FlexAlign.Stretch, Children = [still, copy],
        }) with { MinWidth = 0f };
    }

    /// <summary>A small badge on the still: the on-media scrim and ink, the caption voice.</summary>
    static Element Badge(string text) => text.Length == 0 ? new BoxEl() : new BoxEl
    {
        Direction = 0, Shrink = 0f, Corners = Radii.ControlAll, Padding = new Edges4(Spacing.XS + Spacing.XXS, Spacing.XXS, Spacing.XS + Spacing.XXS, Spacing.XXS),
        Fill = Prop.Of(static () => R.OnMedia.ScrimHover),
        Children = [Ui.Caption(text) with { Color = Prop.Of(static () => R.OnMedia.Ink), MaxLines = 1, Wrap = TextWrap.NoWrap }],
    };
}
