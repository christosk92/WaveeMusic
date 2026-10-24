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
        // About, fans also like and "more by" move between this band and the docked aside on the 1008 crossing (a different parent: they cross-fade, the
        // swap's family); the sections that stay here keep their nodes (each list's wrapper is keyed by its list, never by its position).
        var enter = ZuneMotion.SwapEnter(ZuneMotion.Rendered(ctx.Hooks));
        if (!p.Any) return new BoxEl();
        var sections = new List<Element>(7);
        if (p.Videos.Count > 0) sections.Add(Section("section:videos", null, Videos(p.Videos, p.CustomVideo, p.Overlay)));
        if (p.About is { } lead)
            sections.Add(Section("section:about", K.Heading(Loc.Get(Strings.Detail.AboutTheArtist)),
                Controls.ArtistAboutCard(lead, AboutLayout.Page) with { Key = "about:" + lead.Uri.Text }) with { Enter = enter, Exit = ZuneMotion.SwapFade });
        if (p.Fans.Count > 0)
            sections.Add(Section(Z.FansKey, K.Heading(Loc.Get(Strings.Detail.FansAlsoLike)), FansRow(p.Fans)) with { Enter = enter, Exit = ZuneMotion.SwapFade });
        for (int i = 0; i < p.Lists.Count; i++)
            sections.Add(new BoxEl
            {
                Key = "section:" + p.Lists[i].Key, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Padding = new Edges4(Z.SectionInset, Z.SectionTop, Z.SectionInset, 0f),
                Enter = enter, Exit = ZuneMotion.SwapFade,
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
        // Each section returns to the band under the rows when the aside folds (and back): the swap's family, cross-faded (see SectionsTemplate).
        var enter = ZuneMotion.SwapEnter(ZuneMotion.Rendered(ctx.Hooks));
        var lay = ZuneDetailTiers.Of(ctx.Tier());
        float w = lay.AsideW > 0f ? lay.AsideW : ZuneDetailRules.Standard.AsideW;
        var kids = new List<Element>(3);
        if (p.About is { } artist)
            kids.Add(new BoxEl
            {
                Key = Z.AboutKey, Direction = 1, Gap = Z.AsideRowGap, MinWidth = 0f, Enter = enter, Exit = ZuneMotion.SwapFade,
                Children =
                [
                    K.HeadingRow(Z.AboutKey + ":head", K.Heading(Loc.Get(Strings.Detail.AboutTheArtist)), null, Z.AsideHeadingH),
                    AboutCard(artist, lay) with { Key = "aside:about:" + artist.Uri.Text },
                ],
            });
        if (p.MoreBy is { } moreBy)
            kids.Add(new BoxEl
            {
                Key = "aside:" + moreBy.Key, Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Enter = enter, Exit = ZuneMotion.SwapFade,
                Children = [moreBy],
            });
        if (p.Fans.Count > 0) kids.Add(AsideFans(p.Fans, w) with { Enter = enter, Exit = ZuneMotion.SwapFade });
        return new BoxEl { Key = Z.AlbumAsideKey, Direction = 1, Gap = Z.AsideSectionGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
    }

    /// <summary>Zune's aside artist card, flat: the artist's image as the record's band, then the name (20/28, two lines, as written) with the follow pill
    /// STACKED under it (audit B, Album at 1440: beside the name the pill ellipsised "Mia S..." in the 260 aside), the monthly listeners in the accent and
    /// the bio (four lines). The band and the name are the card's one link (to the artist); the pill is its sibling, never a button inside a button. The
    /// band's height is a live prop of the same node (a crossing eases it, nothing remounts).</summary>
    static Element AboutCard(Artist artist, ZuneDetailLayout lay)
    {
        string name = artist.Name;
        string uri = artist.Uri.Text;
        string? image = Controls.ArtUrl(artist.HeroImageId) is { Length: > 0 } hero ? hero : Controls.ArtUrl(artist.ImageId);
        string bio = ArtistVerbs.Text(artist.BioLeadId.IsEmpty ? artist.BioId : artist.BioLeadId);
        Action open = () => Track.GoToArtist(artist);
        Element band = new BoxEl
        {
            Key = "about:band", Height = lay.ArtistBand, ZStack = true, ClipToBounds = true, Fill = R.Layer.RowHover, Animate = Z.SlotReveal,
            Children = image is { Length: > 0 } url ? [Ui.Image(url, ImageFit.Cover, decodePx: Z.AboutImageDecode)] : [],
        };
        Element nameRow = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Start, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                R.Type.TrackTitle(name) with
                {
                    Size = Z.AboutNameSize, LineHeight = Z.AboutNameLine, Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary,
                    Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.WrapWholeWords, MaxLines = Z.AboutNameLines, Trim = TextTrim.CharacterEllipsis,
                },
                artist.IsVerified
                    ? Ui.Icon(Icons.Check, Z.AboutCheck, Tok.AccentTextPrimary) with { Shrink = 0f, Margin = new Edges4(0f, Z.AboutCheckTop, 0f, 0f) }
                    : new BoxEl { Width = 0f, Shrink = 0f },
            ],
        };
        Element link = new BoxEl
        {
            Key = "about:link", Direction = 1, Gap = Z.AboutWordsGap, MinWidth = 0f,
            HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = open,
            Children =
            [
                band,
                new BoxEl
                {
                    Direction = 1, Gap = Z.AboutWordsGap, MinWidth = 0f, Padding = new Edges4(Z.AboutPadX, Z.AboutPadTop, Z.AboutPadX, 0f),
                    Children =
                    [
                        R.Type.Eyebrow(Loc.Get(Strings.Detail.AboutTheArtist)) with
                        {
                            Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        nameRow,
                    ],
                },
            ],
        };
        var words = new List<Element>(2);
        if (artist.MonthlyListeners > 0)
            words.Add(R.Type.DenseMeta(Detail.Text.CompactCount(artist.MonthlyListeners) + " " + Loc.Get(Strings.Artist.MetaMonthly)) with
            {
                Size = Z.AboutBodySize, LineHeight = Z.AboutBodyLine, Color = Tok.AccentTextPrimary, Case = R.Case.For(LabelKind.Label),
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        if (!string.IsNullOrWhiteSpace(bio))
            words.Add(R.Type.DenseMeta(bio) with
            {
                Size = Z.AboutBodySize, LineHeight = Z.AboutBodyLine, Color = Tok.TextSecondary, Wrap = TextWrap.WrapWholeWords,
                MaxLines = Z.AboutBioLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                link,
                new BoxEl
                {
                    Key = "about:follow", Direction = 0, MinWidth = 0f, Padding = new Edges4(Z.AboutPadX, Z.AboutFollowTop, Z.AboutPadX, 0f),
                    Children = [Embed.Comp(() => new ZuneArtistHero.FollowPill { Uri = uri, Name = name, Height = lay.Target }) with { Key = "about:follow:" + uri }],
                },
                new BoxEl
                {
                    Key = "about:words", Direction = 1, Gap = Z.AboutWordsGap, MinWidth = 0f,
                    Padding = new Edges4(Z.AboutPadX, Z.AboutWordsGap, Z.AboutPadX, Z.AboutPadBottom), Children = words.ToArray(),
                },
            ],
        };
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
        var enter = ZuneMotion.SwapEnter(ZuneMotion.Rendered(ctx.Hooks));
        var lay = ZuneDetailTiers.Of(ctx.Tier());
        Element? link = p.Count > p.Shown ? K.Link("stack:all", Strings.Home.ShowAllCount(p.Count), p.Expand, Z.AsideHeadingH) : null;
        Element body;
        // The Huge aside's grid of covers and the rows are two arrangements (the column count changes): one keyed tree each, cross-faded inside the
        // body, whose height eases as before (ZuneMotion: "grids reflow by fading").
        bool grid = aside && lay.MoreByGrid;
        if (grid)
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
            body = new BoxEl { Key = Z.StackKey + ":grid", Direction = 1, Gap = Z.MoreByGridGap, MinWidth = 0f, Children = rows.ToArray() };
        }
        else
        {
            var rows = new Element[p.Rows.Count];
            for (int i = 0; i < rows.Length; i++) rows[i] = TrailRow(p.Rows[i]);
            body = new BoxEl { Key = Z.StackKey + ":rows", Direction = 1, Gap = Z.SkelRowGap, MinWidth = 0f, Children = rows };
        }
        body = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Animate = Z.SlotReveal,
            Children = [body with { Enter = enter, Exit = ZuneMotion.GridCrossFade }],
        };
        // In the aside the heading carries a name in a narrow column: the 22/28 step over at most two lines, the row a floor of the heading height (audit B).
        Element heading = aside
            ? new BoxEl
            {
                Key = Z.StackKey + ":head", Direction = 0, AlignItems = FlexAlign.Center, MinHeight = Z.AsideHeadingH, Shrink = 0f, MinWidth = 0f,
                Gap = Z.SectionGap,
                Children = link is null ? [AsideNamedHeading(p.Title)] : [AsideNamedHeading(p.Title), link],
            }
            : K.HeadingRow(Z.StackKey + ":head", K.NamedHeading(p.Title), link, Z.AsideHeadingH);
        return new BoxEl
        {
            Key = Z.StackKey, Direction = 1, Gap = Z.AsideRowGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [heading, body],
        };
    }

    /// <summary>The aside's named heading ("More by Mia Solace"): the heading voice as written at 22/28, wrapping by words to two lines.</summary>
    static TextEl AsideNamedHeading(string title) => K.NamedHeading(title) with
    {
        Size = Z.AsideNamedSize, LineHeight = Z.AsideNamedLine, MaxLines = Z.AsideNamedLines, Wrap = TextWrap.WrapWholeWords, Grow = 1f, Basis = 0f,
    };

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
