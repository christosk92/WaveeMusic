// ── Themes/Zune/Structure/Views/Album/ZuneAlbumTrailing.cs — Zune's album trailing band and aside (themes plan 6.3; P5 Z6a1) ───────────────────
//
// Boards Z-Album (the 260 aside: "more by" as rows with their covers), Z-Album-Huge / -Huge4K (the 400 / 440 aside: "about" with the artist's
// band, "more by" as a two-column grid of covers, "fans also like" as four circles a row), Z-Album-Small / -Compact (no aside: the same sections
// under the rows). Every section opens on Zune's heading (26/32 light, lowercased; a heading that carries a NAME, "More by Daft Punk", keeps the
// name's casing) and its link word at the end ("show all 12", one-way: the stack lengthens in place).
//
//   · the REGION (`Album.Trailing`): one skeleton boundary shimmering from the first frame and revealing once, keyed on the shape the host decided;
//   · the SECTIONS (`Album.Sections`, under the rows): the music videos (one: the wide card; two or more: the paged shelf), about the artist,
//     fans also like, then the list stacks (more by, the similar albums, featured on, merch), in that fixed order;
//   · the ASIDE (`Album.Aside`, the docked column): about (the artist's band), "more by", fans also like. The release facts stay in the identity
//     column (Z-Album draws its ℗ line there), so the aside never repeats them;
//   · the STACK (`Album.TrailStack`): rows of the shared media surface (Zune's surface style), or, in a Huge aside, the grid of covers.
//
// The hosts keep every gate, stamp and verb (`Entities\Album.Page.cs`, `Album.UI.cs`); the rows reach these views as plain records with closures.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneDetailKit;
using PageRules = Wavee.Album.PageRules;
using Z = Wavee.Themes.Zune.ZuneDetailRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's album trailing views.</summary>
internal static class ZuneAlbumTrailing
{
    static readonly Func<bool> s_never = static () => false;
    static readonly SurfaceShape s_fanShape = Shape.Shelf(captionLines: 1);
    static readonly SurfaceShape s_gridShape = Shape.Shelf(captionLines: 2);

    // ── the region ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.AlbumTrailing"/>.</summary>
    public static Element TrailingTemplate(AlbumTrailingProps p, in ViewContext ctx)
    {
        var shape = p.Shape;
        return new BoxEl
        {
            Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                new SkelRegionEl(
                    Pending: p.Pending, Failed: s_never, Content: p.Content, ShimmerSource: () => Skeleton(in shape), OnFailed: null,
                    Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: true)
                { Key = p.SkeletonKey },
            ],
        };
    }

    // ── the sections (under the rows) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.AlbumSections"/>: the sections the host decided are present, each in its place.</summary>
    public static Element SectionsTemplate(AlbumSectionsProps p, in ViewContext ctx)
    {
        if (!p.Any) return new BoxEl();
        var sections = new List<Element>(7);
        if (p.Videos.Count > 0) sections.Add(Section("section:videos", null, Videos(p.Videos, p.CustomVideo, p.Overlay)));
        if (p.About is { } lead)
            sections.Add(Section("section:about", K.Heading(Loc.Get(Strings.Detail.AboutTheArtist)),
                Controls.ArtistAboutCard(lead, AboutLayout.Page) with { Key = "about:" + lead.Uri.Text }));
        if (p.Fans.Count > 0) sections.Add(Section(Z.FansKey, K.Heading(Loc.Get(Strings.Detail.FansAlsoLike)), FansRow(p.Fans)));
        for (int i = 0; i < p.Lists.Count; i++)
            sections.Add(new BoxEl
            {
                Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Padding = new Edges4(Z.SectionInset, Z.SectionTop, Z.SectionInset, 0f),
                Children = [p.Lists[i]],
            });
        return new BoxEl
        {
            Key = Z.SectionsKey, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Z.SectionBottom),
            Children = sections.ToArray(),
        };
    }

    /// <summary>A section under the rows: its heading (if any) over its body, on the rows' inset.</summary>
    static Element Section(string key, TextEl? heading, Element body) => new BoxEl
    {
        Key = key, Direction = 1, Gap = Z.SectionGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Padding = new Edges4(Z.SectionInset, Z.SectionTop, Z.SectionInset, 0f),
        Children = heading is null ? [body] : [K.HeadingRow(key + ":head", heading, null, Z.AsideHeadingH), body],
    };

    static Controls.CardData CardOf(TrailCardData c)
        => new(c.Uri, c.Title, c.Subtitle is { } subtitle ? K.Sub(subtitle) : null, c.CoverUrl, c.OnClick, c.OnPlay, c.Circular,
            Drag: c.Drag is { } drag ? Drag.Source(drag) : null)
        { Menu = c.Menu };

    /// <summary>Fans also like under the rows: the circular artist cards in one clipped row.</summary>
    static Element FansRow(IReadOnlyList<TrailCardData> fans)
    {
        var cards = new Element[fans.Count];
        for (int i = 0; i < cards.Length; i++) cards[i] = Controls.Surface(CardOf(fans[i]), s_fanShape, Z.FanCardW) with { Key = fans[i].Key };
        return new BoxEl { Direction = 0, Gap = Z.FanGap, ClipToBounds = true, MinWidth = 0f, Children = cards };
    }

    // ── the videos ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE video: the wide card (the 200 x 116 still under its play badge, the eyebrow, the title and the meta), the whole card watching it;
    /// TWO OR MORE: the paged shelf under its "music videos" heading.</summary>
    static Element Videos(IReadOnlyList<AlbumVideoCard> videos, bool customVideo, IOverlayService? overlay)
        => PageRules.ArmFor(videos.Count) switch
        {
            PageRules.VideoArm.Hero => VideoHero(videos[0], customVideo, overlay),
            PageRules.VideoArm.Shelf => VideoShelf(videos),
            _ => new BoxEl(),
        };

    static Element VideoHero(AlbumVideoCard v, bool customVideo, IOverlayService? overlay)
    {
        bool menu = !Controls.IsNullOverlay(overlay) && v.Uri.Length > 0;
        BoxEl card = new()
        {
            Key = v.Key, Direction = 0, Gap = Z.VideoGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = v.Watch, Draggable = Drag.Source(v.Drag),
            Children =
            [
                new BoxEl
                {
                    Width = Z.VideoThumbW, Height = Z.VideoThumbH, Shrink = 0f, ZStack = true, Corners = R.Shape.All(ShapeRole.Cover), ClipToBounds = true,
                    Children =
                    [
                        Controls.Artwork(v.Thumb, Z.VideoThumbW, Z.VideoThumbH, R.Shape.For(ShapeRole.Cover)),
                        Controls.NowPlayingOverlay(v.Uri, v.Watch, Z.VideoFab, centred: true, atRest: true),
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Z.HeroIdentityGap,
                    Children =
                    [
                        R.Type.Eyebrow(Loc.Get(customVideo ? Strings.VideoOverride.CustomLabel : Strings.Detail.WatchOfficialVideo)) with
                        {
                            Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        K.NamedHeading(v.Title),
                        K.Sub(v.Meta),
                    ],
                },
            ],
        };
        return menu ? ContextSheets.Attach(card, overlay!, v.Menu) : card;
    }

    static Element VideoShelf(IReadOnlyList<AlbumVideoCard> videos)
    {
        var items = new AlbumVideoCard[videos.Count];
        for (int i = 0; i < items.Length; i++) items[i] = videos[i];
        return PagedShelf.Create(items, s_videoCard,
            header: K.Heading(Loc.Get(Strings.Artist.MusicVideos)),
            pager: ShelfPager.Chevrons | ShelfPager.Pips,
            minCardW: Z.VideoCardMinW, maxCardW: Z.VideoCardMaxW, gap: Z.FanGap, headerGap: Z.SectionGap,
            snap: ShelfSnap.Page, edgeFade: Z.ShelfFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None, onInvoke: s_videoInvoke, measured: true, keyOf: s_videoKey, maxItems: PageRules.VideoCap);
    }

    static readonly Func<AlbumVideoCard, int, float, Element> s_videoCard = static (v, _, w)
        => Controls.Surface(new Controls.CardData(v.Uri, v.Title, null, v.Thumb, v.Watch, v.Watch, Drag: Drag.Source(v.Drag))
        {
            CoverAspect = Z.VideoAspect, Caption = v.Duration, Menu = v.Menu,
        }, Shape.Video, w);

    static readonly Func<AlbumVideoCard, int, string> s_videoKey = static (v, _) => v.Key;
    static readonly Action<AlbumVideoCard, int> s_videoInvoke = static (v, _) => v.Watch();

    // ── the aside ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.AlbumAside"/>: about (the artist's band at the record's height), "more by" and fans
    /// also like, at the aside's measure. The release facts stay in the identity column.</summary>
    public static Element AsideTemplate(AlbumAsideProps p, in ViewContext ctx)
    {
        var lay = ZuneDetailTiers.Of(ctx.Tier());
        float w = lay.AsideW > 0f ? lay.AsideW : ZuneDetailRules.Standard.AsideW;
        var kids = new List<Element>(3);
        if (p.About is { } artist)
            kids.Add(new BoxEl
            {
                Key = Z.AboutKey, Direction = 1, Gap = Z.AsideRowGap, MinWidth = 0f,
                Children =
                [
                    K.HeadingRow(Z.AboutKey + ":head", K.Heading(Loc.Get(Strings.Detail.AboutTheArtist)), null, Z.AsideHeadingH),
                    // The card freezes at mount: its band height is in the key, so a crossing re-mounts it at the new band.
                    Controls.ArtistAboutCard(artist, AboutLayout.Aside, lay.ArtistBand)
                        with { Key = "aside:about:" + artist.Uri.Text + ":" + lay.ArtistBand.ToString(CultureInfo.InvariantCulture) },
                ],
            });
        if (p.MoreBy is { } moreBy) kids.Add(moreBy);
        if (p.Fans.Count > 0) kids.Add(AsideFans(p.Fans, w));
        return new BoxEl { Key = Z.AlbumAsideKey, Direction = 1, Gap = Z.AsideSectionGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
    }

    /// <summary>Fans also like in the aside: four circles a row under the heading.</summary>
    static Element AsideFans(IReadOnlyList<TrailCardData> fans, float width)
    {
        float cell = MathF.Max(0f, (width - (Z.FanColumns - 1) * Z.FanGap) / Z.FanColumns);
        var rows = new List<Element>((fans.Count + Z.FanColumns - 1) / Z.FanColumns);
        for (int start = 0; start < fans.Count; start += Z.FanColumns)
        {
            int n = Math.Min(Z.FanColumns, fans.Count - start);
            var cards = new Element[n];
            for (int i = 0; i < n; i++) cards[i] = Controls.Surface(CardOf(fans[start + i]), s_fanShape, cell) with { Key = fans[start + i].Key };
            rows.Add(new BoxEl { Direction = 0, Gap = Z.FanGap, MinWidth = 0f, Children = cards });
        }
        return new BoxEl
        {
            Key = Z.FansKey, Direction = 1, Gap = Z.AsideRowGap, MinWidth = 0f,
            Children =
            [
                K.HeadingRow(Z.FansKey + ":head", K.Heading(Loc.Get(Strings.Detail.FansAlsoLike)), null, Z.AsideHeadingH),
                new BoxEl { Direction = 1, Gap = Z.FanGap, MinWidth = 0f, Children = rows.ToArray() },
            ],
        };
    }

    // ── the stack ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.AlbumTrailStack"/>: the heading (a name kept as written) and "show all N" (one-way:
    /// the stack lengthens in place), then the rows, or, in a Huge aside, the two-column grid of covers.</summary>
    public static Element StackTemplate(AlbumTrailStackProps p, in ViewContext ctx)
    {
        bool aside = ctx.Hooks.UseContext(ZuneDetailFrame.InAside);
        var lay = ZuneDetailTiers.Of(ctx.Tier());
        Element? link = p.Count > p.Shown ? K.Link("stack:all", Strings.Home.ShowAllCount(p.Count), p.Expand) : null;
        Element body;
        if (aside && lay.MoreByGrid)
        {
            float cell = MathF.Max(0f, (lay.AsideW - (Z.MoreByColumns - 1) * Z.MoreByGridGap) / Z.MoreByColumns);
            var rows = new List<Element>((p.Rows.Count + 1) / Z.MoreByColumns + 1);
            for (int start = 0; start < p.Rows.Count; start += Z.MoreByColumns)
            {
                int n = Math.Min(Z.MoreByColumns, p.Rows.Count - start);
                var cells = new Element[n];
                for (int i = 0; i < n; i++)
                {
                    var row = p.Rows[start + i];
                    cells[i] = row.Kind == TrailRowKind.Card
                        ? Controls.Surface(CardOf(row.Card!), s_gridShape, cell) with { Key = row.Card!.Key }
                        : TrailRow(row);
                }
                rows.Add(new BoxEl { Direction = 0, Gap = Z.MoreByGridGap, MinWidth = 0f, Children = cells });
            }
            body = new BoxEl { Direction = 1, Gap = Z.MoreByGridGap, MinWidth = 0f, Animate = Z.SlotReveal, Children = rows.ToArray() };
        }
        else
        {
            var rows = new Element[p.Rows.Count];
            for (int i = 0; i < rows.Length; i++) rows[i] = TrailRow(p.Rows[i]);
            body = new BoxEl { Direction = 1, Gap = Z.SkelRowGap, MinWidth = 0f, Animate = Z.SlotReveal, Children = rows };
        }
        return new BoxEl
        {
            Key = Z.StackKey, Direction = 1, Gap = Z.AsideRowGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [K.HeadingRow(Z.StackKey + ":head", K.NamedHeading(p.Title), link, Z.AsideHeadingH), body],
        };
    }

    static Element TrailRow(TrailRowData row) => row.Kind switch
    {
        TrailRowKind.Card => Controls.Surface(CardOf(row.Card!), Shape.RowTileStill) with { Key = row.Card!.Key },
        TrailRowKind.Merch => MerchRow(row.Merch!),
        _ => new BoxEl { Height = Z.PlaceholderRowH },
    };

    /// <summary>A merch listing: its art, its name and its PRICE in the accent ink ("buy" where the wire gave none); a listing with no shop url is inert.</summary>
    static Element MerchRow(MerchRowData m)
    {
        bool live = m.Open is not null;
        return new BoxEl
        {
            Key = m.Key, Direction = 0, Height = Z.MerchRowH, AlignItems = FlexAlign.Center, Gap = Z.MoreByArtGap, MinWidth = 0f,
            HoverFill = live ? R.Layer.RowHover : ColorF.Transparent, PressedFill = live ? R.Layer.RowPressed : ColorF.Transparent,
            Role = live ? AutomationRole.Button : AutomationRole.None, Focusable = live, Cursor = live ? CursorId.Hand : (CursorId?)null,
            OnClick = m.Open,
            Children =
            [
                Controls.Artwork(m.ImageUrl, Z.MerchArt, Z.MerchArt, R.Shape.For(ShapeRole.Cover)),
                R.Type.TrackTitle(m.Name) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                R.Type.DenseMeta(m.Price.Length > 0 ? m.Price : Loc.Get(Strings.Artist.Buy)) with
                {
                    Color = Tok.AccentTextPrimary, Shrink = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    Case = m.Price.Length > 0 ? TextCase.AsWritten : R.Case.For(LabelKind.Command),
                },
            ],
        };
    }

    // ── the skeleton ─────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Skeleton(in PageRules.TrailingShape shape)
    {
        var sections = new List<Element>(3 + shape.RowBlocks);
        if (shape.Video) sections.Add(SkelSection(K.Bar(Z.VideoThumbW, Z.SkelVideoH), heading: shape.VideoShelf));
        if (shape.About) sections.Add(SkelSection(new BoxEl { Height = Z.SkelAboutH, AlignSelf = FlexAlign.Stretch, Corners = R.Shape.All(ShapeRole.Card) }, heading: true));
        if (shape.Fans)
        {
            var circles = new Element[PageRules.FansCap];
            for (int i = 0; i < circles.Length; i++) circles[i] = K.Bar(Z.FanCardW, Z.FanCardW) with { Corners = CornerRadius4.All(Z.FanCardW / 2f) };
            sections.Add(SkelSection(new BoxEl { Direction = 0, Gap = Z.FanGap, ClipToBounds = true, MinWidth = 0f, Children = circles }, heading: true));
        }
        for (int b = 0; b < shape.RowBlocks; b++)
        {
            var rows = new Element[PageRules.SkelRows];
            for (int i = 0; i < rows.Length; i++) rows[i] = new BoxEl { Height = Z.SkelRowH, AlignSelf = FlexAlign.Stretch, Corners = R.Shape.All(ShapeRole.Tile) };
            sections.Add(SkelSection(new BoxEl { Direction = 1, Gap = Z.SkelRowGap, MinWidth = 0f, Children = rows }, heading: true));
        }
        return new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Z.SectionBottom), Children = sections.ToArray() };
    }

    static Element SkelSection(Element body, bool heading) => new BoxEl
    {
        Direction = 1, Gap = Z.SectionGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Padding = new Edges4(Z.SectionInset, Z.SectionTop, Z.SectionInset, 0f),
        Children = heading
            ? [new BoxEl { Height = Z.AsideHeadingH, Direction = 0, AlignItems = FlexAlign.Center, Children = [K.Bar(Z.SkelHeadingW, Z.SkelHeadingH)] }, body]
            : [body],
    };
}
