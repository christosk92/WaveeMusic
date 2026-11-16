// ── Themes/Classic/Structure/Views/Home/ClassicHomeZones.Sections.cs — the richer section bodies of the Home concept (pinned Home, lane H) ─────
//
// The concept's Classic versions (`home-concept.html`, home-gap P4, P6, P8 and the video card), from the pieces Classic's other bodies use:
//
//   MoodPlates     a grid of plates, each the mix's colour graded toward the page the way the daylist hero is, the name in the section
//                  voice on the calm side over its first sentence                                                   tools: See all N
//   BookShelf      Classic's shelf of the house surface: author, "★ 4.7 · 20 hr 56 min", the plan as a pill; every card 4 lines
//   ClusterCards   Ui.Card: the artist's picture with the name (a link) over "For fans of", the playlist (cover, name, description,
//                  "Playlist · Spotify · 50 songs", Play), 4 rows; pages of two rows at most, no empty slot (GridFill)
//   VideoTiles(1)  one episode as a video card: the still with "Video" and the length on it, the progress hairline, the show, the
//                  title, its facts, Play and Save
//   entity header  a section that hangs off something: its cover, the lead words over its name as a link (a taller sticky header)
//   release header the release list's small heading with "See all N"
//
// Deferred (owner calls D3, D6): hover audio previews, the canvas loop on a card, the episode's video preview. Every box here has its height
// from its data the moment it mounts.

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

namespace Wavee.Themes.Classic;

internal static partial class ClassicHomeZones
{
    // ── headers ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    static bool HasEntityHeader(Zone zone) => zone.Header is not null && zone.Kind is not (ZoneKind.Daylist or ZoneKind.EmptyFacet
        or ZoneKind.ReleaseList or ZoneKind.RecentGrid);

    /// <summary>A section that hangs off something: its cover, "More like" in the caption voice over the name in the module header voice (a link).</summary>
    static Element EntityHeader(HeaderEntity e, Element? tools)
    {
        string uri = e.Uri, name = e.Name;
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Height = ClassicHomeRules.EntityHeaderH,
            Children =
            [
                Controls.Artwork(e.Image, ClassicHomeRules.EntityCover, ClassicHomeRules.EntityCover, Radii.Control),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        Ui.Caption(e.Lead).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        new BoxEl
                        {
                            Direction = 0, MinWidth = 0f, Shrink = 1f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                            OnClick = () => HomeVerbs.OpenEntity(uri, name),
                            Children = [R.Type.ModuleHeader(name) with
                            {
                                HoverColor = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                            }],
                        },
                    ],
                },
                tools ?? new BoxEl(),
            ],
        };
    }

    /// <summary>The release list's small heading (its own sub-block) with "See all N" at the end.</summary>
    static Element ReleaseHeader(Zone zone, ClassicHomeLayout layout)
    {
        var heading = Ui.SectionHeader(zone.Title ?? Loc.Get(Strings.Home.Zone.FromArtistsYouFollow),
                                       layout.HeaderSubtitles ? zone.Subtitle ?? Loc.Get(Strings.Home.ReleasedThisWeek) : null)
            with { Margin = default, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f };
        var seeAll = HomeVerbs.SeeAll(zone);
        if (seeAll is null) return heading;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = [heading, HyperlinkButton.Create(HomeVerbs.SeeAllLabel(zone), seeAll) with { Shrink = 0f }],
        };
    }

    // ── mood plates (P6) ────────────────────────────────────────────────────────────────────────────────────────────────

    readonly record struct PlateKey(HomeCard Card, string Uri, string Title, string Line, uint Accent);

    static readonly Func<PlateKey, Element> s_plate = static k => Plate(in k);

    /// <summary>"Soundtrack your Monday afternoon": up to eight plates in the tier's tile columns, one height.</summary>
    static Element MoodPlates(Zone zone, ClassicHomeLayout layout, RenderContext? hooks)
    {
        int n = Math.Min(zone.Items.Count, ClassicHomeRules.MoodPlatesMax);
        if (n == 0) return new BoxEl();
        var plates = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var c = zone.Items[i];
            plates[i] = ItemMemo.Of(hooks, new PlateKey(c, c.Uri, c.Title, SectionCopy.PlateLine(in c), c.Accent), s_plate);
        }
        var band = layout.Tiles with { MaxCols = Math.Min(layout.Tiles.MaxCols, ClassicHomeRules.MoodPlatesMax / 2) };
        return GridFill.Responsive("mood-plates:" + zone.Key, n, w => band.Columns(w, Spacing.M),
            cols => GridFill.Build(plates, cols, Spacing.M, Spacing.M, ClassicHomeRules.MoodPlateH), FirstWidth(band, Spacing.M));
    }

    /// <summary>One plate: the mix's colour graded toward the card fill (the hero's way), the name 20 semibold and its line in the on-media inks
    /// on the colour's side.</summary>
    static Element Plate(in PlateKey k)
    {
        var c = k.Card;
        bool coloured = k.Accent != 0u;
        ColorF colour = coloured ? ColorScience.ToColor(k.Accent) : default;
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
            Corners = Radii.CardAll, Padding = Edges4.All(ClassicHomeRules.MoodPlatePad),
            Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = () => HomeCardNav.Open(in c),
            Children = kids.ToArray(),
        }.Interactive(Interaction.Tile);
        return coloured
            ? plate with
            {
                Gradient = Ui.GradientRight(new GradientStop(0f, colour),
                                            new GradientStop(1f, ColorF.Lerp(colour, Tok.FillCardDefault, ClassicHomeRules.MoodPlateFadeTo))),
                BorderWidth = 0f,
            }
            : plate;
    }

    // ── the book shelf (P8) ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What a book card paints, as a value (PagedShelf compares items by value).</summary>
    readonly record struct BookItem(HomeCard Card, string Title, string Author, string Facts, string Plan, string? Art);

    static readonly Func<float, float> s_bookHeight =
        static w => SurfaceGeometry.ShelfHeight(w, 1f, ClassicHomeRules.BookCaptionLines, metaLine: false);

    /// <summary>"More like The Manager's Path": Classic's cover shelf band and pager, each card its author, rating and length, and the plan pill.</summary>
    static Element BookShelf(Zone zone, IOverlayService? overlay, ShelfController? controller, ClassicHomeLayout layout)
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
        if (item.Facts.Length > 0) lines.Add(Meta(item.Facts));
        if (item.Plan.Length > 0) lines.Add(new BoxEl { Direction = 0, MinWidth = 0f, Children = [Controls.RowChip(item.Plan)] });
        var menu = HomeCardNav.MenuOf(in c);
        bool hasMenu = menu is not null && host is not null;
        var data = new Controls.CardData(c.Uri, item.Title,
            new BoxEl { Direction = 1, MinWidth = 0f, Gap = Spacing.XXS, AlignItems = FlexAlign.Stretch, Children = lines.ToArray() },
            item.Art, () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c), Drag: HomeCardNav.DragOf(in c), ShowMenu: hasMenu)
            { CaptionLines = ClassicHomeRules.BookCaptionLines, Menu = hasMenu ? menu : null };
        return Controls.Surface(data, Shape.Shelf(ClassicHomeRules.BookCaptionLines, metaLine: false), cardW);
    }

    // ── preview cards (P4) ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Because you like": the cards on pages of two rows at most (GridFill: no empty slot, the last row's cards grow, a lone last card
    /// in its wide form), one page at a time, sliding sideways; "1 of 2" and the previous and next buttons in the header. Every page is laid
    /// out, so the section keeps the tallest page's height.</summary>
    static Element ClusterGrid(Zone zone, ClassicHomeLayout layout, ShelfController? controller)
    {
        var clusters = zone.Clusters;
        if (clusters is not { Count: > 0 }) return new BoxEl();
        var band = layout.Clusters;
        string key = zone.Key;
        return FluentGpu.Controls.Responsive.Of("clusters:" + key, w => band.Columns(w, Spacing.M), fit =>
            GridFill.Paged(key, clusters, GridFill.Pages(clusters.Count, fit, clusters), Spacing.M, Spacing.M, float.NaN,
                static c => ClusterCard(c), static c => ClusterCard(c, wide: true), controller, ShelfPager.None, Icons.ChevronLeft, Icons.ChevronRight),
            fallback: FirstWidth(band, Spacing.M));
    }

    /// <summary>A responsive grid's first-frame width (before its box is measured): the band's widest fit.</summary>
    static float FirstWidth(ClassicGridBand band, float gap) => MathF.Max(band.MinCol, 1f) * band.MaxCols + gap * (band.MaxCols - 1);

    /// <summary>One preview card: who it is for, the playlist it offers, its first four rows. <paramref name="wide"/> (a lone card on the last
    /// row): who and the offer on the left, the rows in two columns beside them.</summary>
    static Element ClusterCard(ZoneCluster cluster, bool wide = false)
    {
        var kids = new List<Element>(3) { Reason(cluster) };
        if (cluster.Header is { } h) kids.Add(Offer(h));
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
        if (wide)
        {
            var side = new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.M, Children = kids.ToArray() };
            var grid = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                Children = [new GridEl { Columns = RhythmCards.Stars(ClassicHomeRules.WideClusterRowCols), ColGap = Spacing.M, Children = rows }],
            };
            return Ui.Card(new BoxEl { Direction = 0, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.Start, Children = [side, grid] })
                with { MinWidth = 0f, AlignSelf = FlexAlign.Stretch };
        }
        kids.Add(new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = rows });
        return Ui.Card(kids.ToArray()) with { MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Gap = Spacing.M };
    }

    /// <summary>Classic heads each card with who it is for: the artist's picture, the name (a link) over "For fans of"; a reason with no entity is
    /// its own bold line over nothing. Two lines either way, so every card's offer starts at the same height.</summary>
    static Element Reason(ZoneCluster cluster)
    {
        Element lead, name, picture;
        if (cluster.Reason is { } r)
        {
            string uri = r.Uri, entity = r.Name;
            picture = Controls.Artwork(r.Image, ClassicHomeRules.ReasonAvatar, ClassicHomeRules.ReasonAvatar, ClassicHomeRules.ReasonAvatar / 2f);
            name = new BoxEl
            {
                Direction = 0, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                OnClick = () => HomeVerbs.OpenEntity(uri, entity),
                Children = [Ui.BodyStrong(entity) with
                    { HoverColor = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
            };
            lead = Ui.Caption(r.Lead).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        }
        else
        {
            picture = new BoxEl
            {
                Width = ClassicHomeRules.ReasonAvatar, Height = ClassicHomeRules.ReasonAvatar, Shrink = 0f,
                Corners = CornerRadius4.All(ClassicHomeRules.ReasonAvatar / 2f), Fill = Tok.FillSubtleSecondary,
            };
            name = Ui.BodyStrong(cluster.Over) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
            lead = Ui.Caption(" ").Secondary() with { MaxLines = 1 };
        }
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = [picture, new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [name, lead] }],
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
                    Children = [Controls.Artwork(h.ImageUrl, ClassicHomeRules.PreviewCover, ClassicHomeRules.PreviewCover, Radii.Control)],
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
                            Children = [Ui.BodyStrong(h.Title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
                        },
                        Ui.Caption(description.Length > 0 ? description : " ").Secondary() with
                            { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        Meta(SectionCopy.PreviewFacts(in h)) with { Color = Tok.TextSecondary },
                        new BoxEl
                        {
                            Direction = 0, MinWidth = 0f, Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
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

    /// <summary>"Videos you might like": the still (16:9) with "Video" and the length on it and the progress hairline, then the show, the title
    /// (two lines), its facts, Play and Save. A row from Standard up, a stack below it.</summary>
    static Element VideoCard(HomeCard e, ClassicHomeLayout layout)
    {
        var ec = e;
        bool row = layout.Tier >= WidthTier.Standard;
        float progress = SectionCopy.EpisodeProgress(in e);
        var stillLayers = new List<Element>(3)
        {
            Controls.CoverFill(e.VideoThumbUrl ?? e.ImageUrl, Radii.Control, ClassicHomeRules.VideoStillW * 2f),
            new BoxEl
            {
                Direction = 0, AlignSelf = FlexAlign.End, Justify = FlexJustify.SpaceBetween, MinWidth = 0f, HitTestVisible = false,
                Padding = Edges4.All(ClassicHomeRules.VideoBadgeInset),
                Children = [Badge(Loc.Get(Strings.Home.Video)), Badge(EpisodeCaption.Duration(e.DurationMs))],
            },
        };
        if (progress > 0f)
            stillLayers.Add(new BoxEl
            {
                AlignSelf = FlexAlign.End, Height = ClassicHomeRules.VideoProgressH, MinWidth = 0f, HitTestVisible = false, Direction = 0,
                Children =
                [
                    new BoxEl { Grow = progress, Basis = 0f, Fill = Tok.AccentDefault },
                    new BoxEl { Grow = 1f - progress, Basis = 0f, Fill = Prop.Of(static () => R.OnMedia.ScrimRest) },
                ],
            });
        Element still = new BoxEl
        {
            ZStack = true, MinWidth = 0f, Shrink = 0f, AspectRatio = R.Space.WideTileAspect, ClipToBounds = true, Corners = Radii.ControlAll,
            Width = row ? ClassicHomeRules.VideoStillW : float.NaN,
            Cursor = CursorId.Hand, OnClick = () => HomeCardNav.Open(in ec), Children = stillLayers.ToArray(),
        };
        string show = e.ShowName is { Length: > 0 } s ? s : HomeCards.PlainText(e.Subtitle);
        string facts = SectionCopy.EpisodeFacts(in e, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        var copy = new BoxEl
        {
            Direction = 1, Grow = row ? 1f : 0f, Shrink = 1f, Basis = row ? 0f : float.NaN, MinWidth = 0f, Gap = Spacing.XS,
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
