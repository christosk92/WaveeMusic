// ── Themes/Zune/Structure/Views/Home/ZuneHomeZones.Sections.cs — the richer section bodies of the Home concept, in Zune's voice (pinned Home, lane H) ──
//
// The concept's Zune versions (`home-concept.html`, home-gap P4, P6, P8 and the video card). No cards: a Zune group stands on the ground.
//
//   MoodPlates     flat plates in the plate rule Browse's tiles use (muted, mid-dark), "Spring music for you." small at the top, the name
//                  40 light lowercase at the foot; a name longer than its plate bleeds off its edge under a fade, it never shrinks
//   BookShelf      the 154 cover shelf: the author, "★ 4.7 · 20 hr 56 min", what the plan covers as a quiet lowercase line
//   PodcastGroups  groups: the show's picture, "for fans of" and the name as a link; the playlist (cover, its name 26 light as written,
//                  the description, "playlist · spotify · 50 songs", the play and save rings); 4 rows; pages of two rows at most, "01/02" by the heading
//   VideoTiles(1)  the episode's still, flat and square-cornered, "video" and the length on it, the progress line; the show, the title in
//                  the 26 light voice as written (it wraps), "20 jul · 1 hr 24 min · not started", the play and save rings
//   entity heading a section that hangs off something: its cover, the lead words lowercase over its name in its own case as a link
//
// Deferred (owner calls D3, D6): hover audio previews, the canvas loop, the episode's video preview. Every box here has its height from its
// data the moment it mounts; display type is never shrunk (Zune large type).

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

internal static partial class ZuneHomeZones
{
    // ── the entity heading ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"popular with listeners of" over "The Pog State": the thing's cover, the lead words in the label case, the name in the heading voice
    /// as written (a link to it), "see all" at the end.</summary>
    static BoxEl EntityHeading(HeaderEntity e, Element? tools, ZuneHomeLayout lay)
    {
        string uri = e.Uri, name = e.Name;
        var words = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
            Children =
            [
                K.Caption(e.Lead),
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Shrink = 1f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                    OnClick = () => HomeVerbs.OpenEntity(uri, name),
                    Children = [K.HeadingText(name) with { Case = TextCase.AsWritten, HoverColor = Tok.TextSecondary }],
                },
            ],
        };
        var kids = new List<Element>(3)
        {
            new BoxEl
            {
                Width = H.EntityCover, Height = H.EntityCover, Shrink = 0f,
                Children = [Controls.Artwork(e.Image, H.EntityCover, H.EntityCover, 0f)],
            },
            words,
        };
        if (tools is not null)
            kids.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Margin = new Edges4(H.HeadToolsGap, 0f, 0f, 0f), Children = [tools.Value] });
        return new BoxEl { Direction = 0, Gap = H.HeadInlineGap, AlignItems = FlexAlign.End, MinWidth = 0f, Children = kids.ToArray() };
    }

    // ── mood plates (P6) ────────────────────────────────────────────────────────────────────────────────────────────────

    readonly record struct PlateKey(HomeCard Card, string Uri, string Title, string Line, uint Accent);

    static readonly Func<PlateKey, Element> s_moodPlate = static k => MoodPlate(in k);

    /// <summary>"soundtrack your monday afternoon": up to eight plates in the tier's tile columns.</summary>
    static Element MoodPlates(Zone zone, ZuneHomeLayout lay, RenderContext? hooks)
    {
        int n = Math.Min(zone.Items.Count, H.MoodPlatesMax);
        var plates = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var c = zone.Items[i];
            plates[i] = ItemMemo.Of(hooks, new PlateKey(c, c.Uri, c.Title, SectionCopy.PlateLine(in c), c.Accent), s_moodPlate);
        }
        var band = new ZuneCappedBand(H.TileMinCol, Math.Min(lay.TileCols, H.MoodPlatesMax / 2), 0);
        Element grid = GridFill.Responsive("zune-mood-plates:" + zone.Key, n, w => band.Columns(w, H.TileGap),
            (cols, w) => GridFill.Build(plates, cols, w, H.TileGap, H.TileGap), band.MinCol * band.MaxCols + H.TileGap * (band.MaxCols - 1));
        return K.Section(Heading(zone, lay), grid);
    }

    /// <summary>One plate: the mix's colour through the plate rule (the accent where it has none), its line at the top, its name 40 light at the foot.
    /// The name is never shrunk and never cut mid-glyph: it wraps at word breaks and the plate grows to hold it (every plate of a row takes the
    /// row's height, the tallest name's); only a single word wider than the plate still runs off its edge, under a fade.</summary>
    static Element MoodPlate(in PlateKey k)
    {
        var c = k.Card;
        bool coloured = k.Accent != 0u;
        ColorF plate = coloured ? H.MutedPlate(ColorScience.ToColor(k.Accent)) : default;
        bool dark = coloured && H.PlateWantsDarkInk(plate);
        Prop<ColorF> ink = dark ? ColorContrast.NearBlackInk : Prop.Of(static () => R.OnMedia.Ink);
        // The small line takes the full ink too: the secondary on-media ink misses 4.5 : 1 on the paler plates.
        Prop<ColorF> inkSoft = ink;
        return new BoxEl
        {
            Key = "zune-mood-plate:" + k.Uri, MinHeight = H.MoodPlateH, Grow = 1f, Direction = 1, Justify = FlexJustify.SpaceBetween, MinWidth = 0f,
            ClipToBounds = true, Corners = R.Shape.All(ShapeRole.Tile), Padding = new Edges4(H.TilePadX, H.TilePadY, 0f, H.TilePadY),
            Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = () => HomeCardNav.Open(in c),
            Children =
            [
                // The line wraps to a second line rather than cutting (the plate has the room above the 40 light name; nothing is shrunk).
                R.Type.DenseMeta(k.Line.Length > 0 ? k.Line : " ") with
                {
                    Color = inkSoft, Case = R.Case.For(LabelKind.Label), MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    MinWidth = 0f, Margin = new Edges4(0f, 0f, H.TilePadX, 0f),
                },
                // The word is the art: never shrunk, never cut to an ellipsis. The name wraps at its word breaks inside the plate's inset (the
                // plate grows with it); a single word wider than the plate is the one thing that still runs off its right edge, through a fade.
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, ClipToBounds = true, EdgeFade = new EdgeFadeSpec(EdgeMask.Right, H.TilePadX * 2f),
                    Padding = new Edges4(0f, 0f, H.TilePadX, 0f),
                    Children = [R.Type.HeroTitle(k.Title) with
                    {
                        Color = ink, Case = R.Case.For(LabelKind.Navigation), MaxLines = 0, Wrap = TextWrap.Wrap, Trim = TextTrim.None,
                        Shrink = 1f, MinWidth = 0f,
                    }],
                },
            ],
        }.Interactive(Interaction.Tile) with
        {
            Fill = coloured ? plate : Prop.Of(static () => Tok.AccentDefault),
            HoverFill = coloured ? H.PlateHover(plate) : Prop.Of(static () => H.PlateHover(Tok.AccentDefault)),
            PressedFill = coloured ? H.PlatePressed(plate) : Prop.Of(static () => H.PlatePressed(Tok.AccentDefault)),
            BorderWidth = 0f,
        };
    }

    // ── the book shelf (P8) ─────────────────────────────────────────────────────────────────────────────────────────────

    readonly record struct BookItem(HomeCard Card, string Title, string Author, string Facts, string Plan, string? Art);

    static readonly Func<float, float> s_bookHeight = static w => SurfaceGeometry.ShelfHeight(w, 1f, H.BookCaptionLines, metaLine: false);

    /// <summary>"more like The Manager's Path": the cover shelf, each book's author, its rating and length and what the plan covers under it.</summary>
    static Element BookShelf(Zone zone, IOverlayService? overlay, ZuneHomeLayout lay)
    {
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
            header: Heading(zone, lay) with { Grow = H.HeadFillGrow },
            pager: ShelfPager.Chevrons | ShelfPager.WhenPaged,
            minCardW: lay.ShelfMinW, maxCardW: lay.ShelfCols > 0 ? H.WideMaxW : lay.ShelfCardW, fixedCardW: lay.ShelfCols > 0 ? 0f : lay.ShelfCardW,
            maxColumns: lay.ShelfCols, gap: H.ShelfGap, headerGap: H.HeadGap,
            edgeFade: HomeModuleLayout.ShelfEdgeFade, snap: ShelfSnap.Page,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight, parts: K.ChevronTargets,
            lift: ShelfLift.None,
            keyOf: static (item, i) => item.Card.IsBlank ? "zune-book-blank:" + i.ToString(CultureInfo.InvariantCulture) : "zune-book:" + item.Card.Uri)
            with { Key = "zune-home-books:" + zone.Key };
    }

    static Element BookCell(in BookItem item, float cardW, IOverlayService? host)
    {
        var c = item.Card;
        var lines = new List<Element>(3);
        if (item.Author.Length > 0) lines.Add(K.Sub(item.Author));
        if (item.Facts.Length > 0) lines.Add(K.Sub(item.Facts) with { Color = Tok.TextTertiary });
        if (item.Plan.Length > 0) lines.Add(K.Caption(item.Plan));
        var menu = HomeCardNav.MenuOf(in c);
        bool hasMenu = menu is not null && host is not null;
        var data = new Controls.CardData(c.Uri, item.Title,
            new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = lines.ToArray() },
            item.Art, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c), Drag: HomeCardNav.DragOf(in c), ShowMenu: hasMenu)
            { CaptionLines = H.BookCaptionLines, Menu = hasMenu ? menu : null };
        return Controls.Surface(data, Shape.Shelf(H.BookCaptionLines, metaLine: false), cardW);
    }

    // ── the preview groups (P4) ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"because you like": the groups on pages of two rows at most (GridFill: no empty slot, the last row's groups grow, a lone last group
    /// in its wide form), one page at a time, sliding sideways by swipe, wheel, keys and the edge chevrons; a light "01/02" beside the heading
    /// (display type, never shrunk). Every page is laid out, so the section keeps the tallest page's height. The section owns its pager.</summary>
    static Element Clusters(Zone zone, ZuneHomeLayout lay)
        => Embed.Comp(new ClustersProps(zone, lay), static () => new ClustersView()) with { SkeletonProxy = () => ClustersTree(zone, lay, null) };

    sealed record ClustersProps(Zone Zone, ZuneHomeLayout Layout);

    /// <summary>Owns the section's ONE pager controller (stable for its life, as the shelf requires) and registers it for the inspector.</summary>
    sealed class ClustersView : Component
    {
        readonly ShelfController _controller = new();

        public override Element Render()
        {
            var p = UseProps<ClustersProps>();
            string key = p.Zone.Key;
            UseEffect(() => GridFill.Register(key, _controller));
            return ClustersTree(p.Zone, p.Layout, _controller);
        }
    }

    static Element ClustersTree(Zone zone, ZuneHomeLayout lay, ShelfController? controller)
    {
        var clusters = zone.Clusters;
        if (clusters is not { Count: > 0 }) return K.Section(K.Heading(zone.Title ?? "", lay.Subtitles ? zone.Subtitle : null, null), new BoxEl());
        var rowShape = Shape.Row(lay.ClusterArt) with { MinHeight = lay.ClusterRowH };
        var pages = GridFill.Pages(clusters.Count, lay.ClusterCols, clusters);
        Element body = GridFill.Paged(zone.Key, clusters, pages, H.ClusterGap, H.ClusterGap, float.NaN,
            c => Cluster(c, IndexOf(clusters, c), lay, rowShape), c => Cluster(c, IndexOf(clusters, c), lay, rowShape, wide: true),
            controller, ShelfPager.HoverEdge, Icons.ChevronLeft, Icons.ChevronRight, K.ChevronTargets);
        return K.Section(ClustersHeading(zone, lay, pages.Length > 1 ? controller : null), body);
    }

    static int IndexOf(IReadOnlyList<ZoneCluster> list, ZoneCluster c)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], c)) return i;
        return -1;
    }

    /// <summary>The heading, the page counter beside it while there are pages ("01/02" in the heading's light voice), the subtitle after.</summary>
    static Element ClustersHeading(Zone zone, ZuneHomeLayout lay, ShelfController? controller)
    {
        if (controller is null) return K.Heading(zone.Title ?? "", lay.Subtitles ? zone.Subtitle : null, null);
        var words = new List<Element>(3)
        {
            K.HeadingText(zone.Title ?? ""),
            Embed.Comp(() => new PageCounter(controller)) with { Key = "zune-cluster-pages" },
        };
        if (lay.Subtitles && zone.Subtitle is { Length: > 0 } sub) words.Add(K.Caption(sub) with { Margin = new Edges4(H.HeadInlineGap, 0f, 0f, 0f) });
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = words.ToArray() };
    }

    /// <summary>"01/02": the page and the count in the heading's light voice, tertiary, never shrunk.</summary>
    sealed class PageCounter(ShelfController controller) : Component
    {
        public override Element Render()
        {
            int count = controller.PageCount.Value;
            int page = controller.Page.Value;
            string text = count <= 1 ? "" : (page + 1).ToString("00", CultureInfo.InvariantCulture) + "/" + count.ToString("00", CultureInfo.InvariantCulture);
            return R.Type.Heading(text) with
            {
                Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f, Margin = new Edges4(H.HeadInlineGap, 0f, 0f, 0f),
            };
        }
    }

    static Element Cluster(ZoneCluster cluster, int index, ZuneHomeLayout lay, SurfaceShape rowShape, bool wide = false)
    {
        var kids = new List<Element>(4) { ReasonRow(cluster, lay) };
        if (cluster.Header is { } h) kids.Add(Offer(h, index, lay));
        int head = kids.Count;
        int n = Math.Min(cluster.Rows.Count, H.ClusterRows);
        for (int i = 0; i < n; i++)
        {
            var c = cluster.Rows[i];
            string sub = HomeCards.PlainText(c.Subtitle);
            var data = new Controls.CardData(c.Uri, c.Title, sub.Length > 0 ? K.Caption(sub, cased: false) with { Color = Tok.TextSecondary } : null,
                c.ImageUrl, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
                Circular: c.Kind == HomeCardKind.Artist, Drag: HomeCardNav.DragOf(in c))
            { Menu = HomeCardNav.MenuOf(in c) };
            kids.Add(new BoxEl
            {
                Key = c.IsBlank ? "zune-cluster-blank:" + i.ToString(CultureInfo.InvariantCulture) : "zune-cluster-row:" + c.Uri,
                Direction = 1, MinWidth = 0f, Margin = ZuneListRules.OnColumn(in rowShape), Children = [Controls.Surface(data, rowShape)],
            });
        }
        string key = "zune-cluster:" + index.ToString(CultureInfo.InvariantCulture) + ":" + cluster.Name;
        if (wide)
        {
            var side = new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Gap = H.ClusterRowGap, AlignItems = FlexAlign.Stretch, Children = kids.GetRange(0, head).ToArray() };
            var rows = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                Children = [new GridEl { Columns = RhythmCards.Stars(H.WideClusterRowCols), ColGap = H.ClusterGap, RowGap = H.ClusterRowGap, Children = kids.GetRange(head, kids.Count - head).ToArray() }],
            };
            return new BoxEl { Key = key, Direction = 0, Gap = H.ClusterGap, MinWidth = 0f, AlignItems = FlexAlign.Start, Children = [side, rows] };
        }
        return new BoxEl
        {
            Key = key, Direction = 1, Gap = H.ClusterRowGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = kids.ToArray(),
        };
    }

    /// <summary>Why the group is here: the artist's picture, "for fans of" in the label case and the artist's name as written, a link; or the
    /// reason alone ("based on your recent listening"). One row of the cluster title's height either way.</summary>
    static Element ReasonRow(ZoneCluster cluster, ZuneHomeLayout lay)
    {
        var kids = new List<Element>(3);
        if (cluster.Reason is { } r)
        {
            string uri = r.Uri, name = r.Name;
            kids.Add(new BoxEl
            {
                Width = H.ReasonAvatar, Height = H.ReasonAvatar, Shrink = 0f,
                Children = [Controls.Artwork(r.Image, H.ReasonAvatar, H.ReasonAvatar, H.ReasonAvatar / 2f)],
            });
            kids.Add(R.Type.DenseMeta(r.Lead) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f });
            kids.Add(new BoxEl
            {
                Direction = 0, MinWidth = 0f, Shrink = 1f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                OnClick = () => HomeVerbs.OpenEntity(uri, name),
                Children = [R.Type.DenseMeta(name) with
                {
                    Color = Tok.TextPrimary, HoverColor = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                    MinWidth = 0f, Shrink = 1f,
                }],
            });
        }
        else
            kids.Add(R.Type.DenseMeta(cluster.Over) with
            {
                Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        return new BoxEl
        {
            Height = lay.ClusterTitleH, Direction = 0, Gap = H.ClusterRowGap * 2f, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray(),
        };
    }

    /// <summary>The playlist the group offers: its cover (flat), its name 26 light as written (opens it), the description, "playlist · spotify · 50 songs",
    /// then the play and save rings.</summary>
    static Element Offer(HomeCard h, int index, ZuneHomeLayout lay)
    {
        var hc = h;
        string description = HomeCards.PlainText(h.Subtitle);
        string key = index.ToString(CultureInfo.InvariantCulture);
        return new BoxEl
        {
            Direction = 1, Gap = H.HeadGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = H.OfferGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = H.PreviewCover, Height = H.PreviewCover, Shrink = 0f, Cursor = CursorId.Hand, OnClick = () => HomeCardNav.Open(in hc),
                            Children = [Controls.Artwork(h.ImageUrl, H.PreviewCover, H.PreviewCover, 0f)],
                        },
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                            Children =
                            [
                                new BoxEl
                                {
                                    Direction = 0, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                                    OnClick = () => HomeCardNav.Open(in hc),
                                    Children = [K.HeadingText(h.Title) with { Case = TextCase.AsWritten, HoverColor = Tok.TextSecondary }],
                                },
                                K.Sub(description.Length > 0 ? description : " "),
                                K.Caption(SectionCopy.PreviewFacts(in h)),
                            ],
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = lay.RingsGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        K.Ring("zune-cluster-play:" + key, Icons.Play, Loc.Get(Strings.Home.Play), showLabel: true, primary: true,
                               () => HomeCardNav.Play(in hc), lay.RingSize, lay.RingTarget),
                        ZuneHomeDaylist.SaveRing(h.Uri, h.Title, lay with { RingLabels = true }, "zune-cluster-save:" + key),
                    ],
                },
            ],
        };
    }

    // ── the video card (an episode on All) ──────────────────────────────────────────────────────────────────────────────

    static bool IsOneEpisode(Zone zone) => zone.Items.Count == 1 && zone.Items[0].Kind == HomeCardKind.Episode;

    /// <summary>"videos you might like": the episode's own still, flat and square-cornered, "video" and its length on it, the progress line; beside it
    /// (under it below Standard) the show, the title in the 26 light voice as written (it wraps, never shrunk), its facts, the play and save rings.</summary>
    static Element VideoCard(Zone zone, ZuneHomeLayout lay)
    {
        var e = zone.Items[0];
        var ec = e;
        bool row = lay.Tier >= WidthTier.Standard;
        float progress = SectionCopy.EpisodeProgress(in e);
        var layers = new List<Element>(3)
        {
            Controls.CoverFill(e.VideoThumbUrl ?? e.ImageUrl, 0f, H.VideoStillW * 2f),
            new BoxEl
            {
                Direction = 0, AlignSelf = FlexAlign.End, Justify = FlexJustify.SpaceBetween, MinWidth = 0f, HitTestVisible = false,
                Padding = Edges4.All(H.VideoBadgeInset),
                Children = [Badge(Loc.Get(Strings.Home.Video)), Badge(EpisodeCaption.Duration(e.DurationMs))],
            },
        };
        if (progress > 0f)
            layers.Add(new BoxEl
            {
                AlignSelf = FlexAlign.End, Height = H.VideoProgressH, MinWidth = 0f, HitTestVisible = false, Direction = 0,
                Children =
                [
                    new BoxEl { Grow = progress, Basis = 0f, Fill = Prop.Of(static () => R.Ink.AccentAction) },
                    new BoxEl { Grow = 1f - progress, Basis = 0f, Fill = Prop.Of(static () => R.OnMedia.ScrimRest) },
                ],
            });
        Element still = new BoxEl
        {
            ZStack = true, MinWidth = 0f, Shrink = 0f, AspectRatio = R.Space.WideTileAspect, ClipToBounds = true,
            Width = row ? H.VideoStillW : float.NaN,
            Cursor = CursorId.Hand, OnClick = () => HomeCardNav.Open(in ec), Children = layers.ToArray(),
        };
        string show = e.ShowName is { Length: > 0 } s ? s : HomeCards.PlainText(e.Subtitle);
        string facts = SectionCopy.EpisodeFacts(in e, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        var copy = new BoxEl
        {
            Direction = 1, Grow = row ? 1f : 0f, Shrink = 1f, Basis = row ? 0f : float.NaN, MinWidth = 0f, Gap = H.ClusterRowGap,
            Children =
            [
                K.Caption(show, cased: false),
                R.Type.Heading(e.Title) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 0, MinWidth = 0f },
                K.Caption(facts),
                new BoxEl
                {
                    Direction = 0, Gap = lay.RingsGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Margin = new Edges4(0f, H.HeadGap, 0f, 0f),
                    Children =
                    [
                        K.Ring("zune-video-play:" + e.Uri, Icons.Play, Loc.Get(Strings.Home.Play), showLabel: true, primary: true,
                               () => HomeCardNav.Play(in ec), lay.RingSize, lay.RingTarget),
                        ZuneHomeDaylist.SaveRing(e.Uri, e.Title, lay with { RingLabels = true }, "zune-video-save:" + e.Uri),
                    ],
                },
            ],
        };
        Element card = new BoxEl
        {
            Key = "zune-video-card:" + e.Uri, Direction = (byte)(row ? 0 : 1), Gap = H.ClusterGap, MinWidth = 0f,
            AlignItems = row ? FlexAlign.Start : FlexAlign.Stretch, Children = [still, copy],
        };
        return K.Section(Heading(zone, lay), card);
    }

    /// <summary>A word on the still: the on-media scrim and ink, lowercase.</summary>
    static Element Badge(string text) => text.Length == 0 ? new BoxEl() : new BoxEl
    {
        Direction = 0, Shrink = 0f, Padding = new Edges4(H.TilePadY / 2f, H.ClusterRowGap / 2f, H.TilePadY / 2f, H.ClusterRowGap / 2f),
        Fill = Prop.Of(static () => R.OnMedia.ScrimHover),
        Children = [R.Type.MicroMeta(text) with { Color = Prop.Of(static () => R.OnMedia.Ink), Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap }],
    };
}
