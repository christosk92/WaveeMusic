// ── Themes/Zune/Structure/Views/Album/ZuneAlbumTrailing.cs — Zune's album trailing band and aside (themes plan 6.3; P5 Z6a1) ───────────────────
//
// Boards Z-Album (the 260 aside: "more by" as rows of a 56 cover, the title and "type · year", its "discography" link, then "credits"), Z-Album-Huge /
// -Huge4K (the 400 / 440 aside: "about <artist>" with the photo band, "more by" as a two-column grid of covers, "fans also like" as four circles a row),
// Z-Album-Small / -Compact (no aside: the same sections under the rows). Every section opens on Zune's heading (26/32 light, lowercased; a heading that carries a NAME, "More by Daft Punk", keeps
// the name's casing) and its link word at the end ("discography" on an artist's releases, else "show all 12", one-way: the stack lengthens in place).
// "about" is the Huge aside's only (owner 2026-10-25, audit FU2b: Z-Album-Huge draws it, Z-Album and the narrower boards do not): ONE keyed block in
// every aside, folded to nothing below Huge, so the 1920 crossing eases it in and out and keeps its parts (no tier in a key). "credits" is drawn only
// when the core has a paragraph to state (`AlbumAsideProps.Credits`), which it has not yet: the section draws nothing, not even its heading.
//
//   · the REGION (`Album.Trailing`): one skeleton boundary shimmering from the first frame and revealing once, keyed on the shape the host decided;
//   · the SECTIONS (`Album.Sections`, under the rows): the music videos (one: the wide card; two or more: the paged shelf), fans also like, then
//     the list stacks (more by, the similar albums, featured on, merch), in that fixed order;
//   · the ASIDE (`Album.Aside`, the docked column): "more by", fans also like. The release facts stay in the identity column (Z-Album draws its ℗
//     line there), so the aside never repeats them;
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
    /// <summary>Z-Album's "more by" row: the 56 cover on the list-row plate (no tile), the title over "type · year".</summary>
    static readonly SurfaceShape s_releaseRow = Shape.Row(Z.MoreByArt);

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
        // Fans also like and "more by" move between this band and the docked aside on the 1008 crossing (a different parent: they cross-fade, the
        // swap's family); the sections that stay here keep their nodes (each list's wrapper is keyed by its list, never by its position).
        var enter = ZuneMotion.SwapEnter(ZuneMotion.Rendered(ctx.Hooks));
        if (!p.Any) return new BoxEl();
        var sections = new List<Element>(7);
        if (p.Videos.Count > 0) sections.Add(Section("section:videos", null, Videos(p.Videos, p.CustomVideo, p.Overlay)));
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
        Children = heading is null ? [body] : [K.HeadingRow(key + ":head", heading.Value, null, Z.AsideHeadingH), body],
    };

    /// <summary>The shared surface's card for a trailing row. <paramref name="release"/>: a row of an artist's own releases, captioned by its kind and
    /// year ("album · 2001", Z-Album's "more by"), the artist being the stack's own.</summary>
    static Controls.CardData CardOf(TrailCardData c, bool release = false)
        => new(c.Uri, c.Title, Caption(c, release), c.CoverUrl, c.OnClick, c.OnPlay, c.Circular, Drag: c.Drag is { } drag ? Drag.Source(drag) : null)
        { Menu = c.Menu };

    static TextEl? Caption(TrailCardData c, bool release)
        => release && c.TypeYear is { Length: > 0 } typeYear ? K.Sub(typeYear) with { Case = R.Case.For(LabelKind.Label) }
         : c.Subtitle is { } subtitle ? K.Sub(subtitle)
         : null;

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

    /// <summary>The registered template of <see cref="ViewKeys.AlbumAside"/>: "more by" and fans also like, at the aside's measure (Z-Album). The release
    /// facts stay in the identity column.</summary>
    public static Element AsideTemplate(AlbumAsideProps p, in ViewContext ctx)
    {
        // Each section returns to the band under the rows when the aside folds (and back): the swap's family, cross-faded (see SectionsTemplate).
        var enter = ZuneMotion.SwapEnter(ZuneMotion.Rendered(ctx.Hooks));
        var lay = ZuneDetailTiers.Of(ctx.Tier());
        float w = lay.AsideW > 0f ? lay.AsideW : ZuneDetailRules.Standard.AsideW;
        var kids = new List<Element>(3);
        if (p.MoreBy is { } moreBy)
            kids.Add(new BoxEl
            {
                Key = "aside:" + moreBy.Key, Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Enter = enter, Exit = ZuneMotion.SwapFade,
                Children = [moreBy],
            });
        // Credits only when there is a paragraph to state (contract 1.82); today the core has none (Album.PageRules.CreditsOf), so nothing draws.
        if (p.Credits is { Runs.Count: > 0 } credits) kids.Add(Credits(credits) with { Enter = enter, Exit = ZuneMotion.SwapFade });
        if (p.Fans.Count > 0) kids.Add(AsideFans(p.Fans, w) with { Enter = enter, Exit = ZuneMotion.SwapFade });
        Element sections = new BoxEl { Direction = 1, Gap = Z.AsideSectionGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
        // "about" stands above them in its own fold: its gap to the next section is its own (inside the fold), so a folded block leaves no air behind.
        return new BoxEl
        {
            Key = Z.AlbumAsideKey, Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = p.About is { } artist ? [About(artist, lay), sections] : [sections],
        };
    }

    /// <summary>Z-Album-Huge's "about <artist>" (the board's own form: no eyebrow, no follow pill): the head ("about" and the artist's name, its link), the
    /// photo band with the name over its foot (40/48 light, wrapping, never shrinking), the bio (three lines and "more") and the listeners line. ONE keyed
    /// block at every aside tier, folded to nothing where the tier draws none (<see cref="ZuneDetailLayout.AboutBand"/> 0): its height eases through real
    /// layout, so a crossing into or out of Huge slides the sections under it instead of mounting a part.</summary>
    static Element About(Artist artist, ZuneDetailLayout lay)
    {
        bool shown = lay.AboutBand > 0f;
        string name = artist.Name;
        string? image = Controls.ArtUrl(artist.HeroImageId) is { Length: > 0 } hero ? hero : Controls.ArtUrl(artist.ImageId);
        string bio = ArtistVerbs.Text(artist.BioLeadId.IsEmpty ? artist.BioId : artist.BioLeadId);
        Action open = () => Track.GoToArtist(artist);
        Element head = new BoxEl
        {
            Key = Z.AboutKey + ":head", Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.SectionGap, Height = Z.AsideHeadingH, Shrink = 0f, MinWidth = 0f,
            Children =
            [
                K.Heading(Loc.Get(Strings.Theme.Zune.Artist.About)),
                new BoxEl
                {
                    Key = Z.AboutKey + ":name-link", Direction = 0, AlignItems = FlexAlign.Center, MinHeight = Z.AsideHeadingH, Shrink = 1f, MinWidth = 0f,
                    Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = open,
                    Children =
                    [
                        R.Type.DenseMeta(name) with
                        {
                            Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, Case = TextCase.AsWritten, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            Shrink = 1f, MinWidth = 0f,
                        },
                    ],
                },
            ],
        };
        Element band = new BoxEl
        {
            Key = Z.AboutKey + ":band", Height = MathF.Max(lay.AboutBand, ZuneDetailRules.Huge.AboutBand), ZStack = true, ClipToBounds = true,
            Fill = R.Layer.RowHover, Shrink = 0f,
            Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = open,
            Children =
            [
                image is { Length: > 0 } url ? Ui.Image(url, ImageFit.Cover, decodePx: Z.AboutImageDecode) : new BoxEl(),
                new BoxEl
                {
                    HitTestVisible = false, MinWidth = 0f,
                    Gradient = Ui.GradientDown(new GradientStop(0f, Tok.FillSolidBase with { A = 0f }), new GradientStop(1f, Tok.FillSolidBase with { A = Z.AboutScrimTail })),
                },
                new BoxEl
                {
                    AlignSelf = FlexAlign.End, MinWidth = 0f, Padding = new Edges4(Z.AboutNamePadX, 0f, Z.AboutNamePadX, Z.AboutNamePadBottom), HitTestVisible = false,
                    Children =
                    [
                        R.Type.Heading(name) with
                        {
                            Size = Z.AboutNameSize, LineHeight = Z.AboutNameLine, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading),
                            Wrap = TextWrap.WrapWholeWords, MaxLines = Z.AboutNameLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
            ],
        };
        var words = new List<Element>(2);
        if (!string.IsNullOrWhiteSpace(bio))
        {
            TextSpan more = new(Loc.Get(Strings.Common.More).ToLowerInvariant(), Color: Tok.TextPrimary, OnClick: open, IsLink: true);
            words.Add(new SpanTextEl([new TextSpan(bio + " "), more])
            {
                Size = Z.AboutBodySize, LineHeight = Z.AboutBodyLine, Color = Tok.TextSecondary, Wrap = TextWrap.WrapWholeWords, MaxLines = Z.AboutBioLines,
                Trim = TextTrim.CharacterEllipsis, OverflowSuffix = [new TextSpan(" "), more], MinWidth = 0f,
            });
        }
        if (artist.MonthlyListeners > 0 || artist.Followers > 0)
        {
            var meta = new List<TextSpan>(3);
            if (artist.MonthlyListeners > 0)
                meta.Add(new TextSpan((Detail.Text.CompactCount(artist.MonthlyListeners) + " " + Loc.Get(Strings.Artist.MetaMonthly)).ToLowerInvariant(), Color: R.Ink.AccentDecor));
            if (artist.Followers > 0)
                meta.Add(new TextSpan((meta.Count > 0 ? " · " : "") + Strings.Person.Card.Followers((int)Math.Min(artist.Followers, int.MaxValue), Detail.Text.CompactCount(artist.Followers)).ToLowerInvariant()));
            words.Add(new SpanTextEl(meta.ToArray()) { Size = Z.AboutMetaSize, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        }
        Element content = new BoxEl
        {
            Direction = 1, Gap = Z.AsideRowGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Shrink = 0f,
            Padding = new Edges4(0f, 0f, 0f, Z.AsideSectionGap),   // the gap to the next section, folded with the block
            Children = [head, band, .. words],
        };
        return new BoxEl
        {
            Key = Z.AboutKey, Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true, Shrink = 0f,
            Height = shown ? float.NaN : 0f, HitTestVisible = shown, Layout = Design.Reduced ? null : ZuneMotion.HeightResize,
            Children = [content],
        }.Skeletonized(shown);
    }

    /// <summary>Z-Album's "credits": the heading (22/28, as the aside's named headings) over the core's paragraph, its names in the primary ink as links and
    /// "all credits" at the end (contract 1.82).</summary>
    static Element Credits(AlbumCredits credits)
    {
        var spans = new List<TextSpan>(credits.Runs.Count + 2);
        foreach (var run in credits.Runs)
            spans.Add(run.Name ? new TextSpan(run.Text, Color: Tok.TextPrimary, OnClick: run.Open, IsLink: run.Open is not null) : new TextSpan(run.Text));
        if (credits.AllCredits is { } all)
            spans.Add(new TextSpan(" · " + Loc.Get(Strings.Detail.AllCredits).ToLowerInvariant(), OnClick: all, IsLink: true));
        return new BoxEl
        {
            Key = Z.CreditsKey, Direction = 1, Gap = Z.AsideRowGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                new BoxEl
                {
                    Key = Z.CreditsKey + ":head", Direction = 0, AlignItems = FlexAlign.Center, MinHeight = Z.AsideHeadingH, Shrink = 0f, MinWidth = 0f,
                    Children = [K.Heading(Loc.Get(Strings.Player.Credits)) with { Size = Z.AsideNamedSize, LineHeight = Z.AsideNamedLine }],
                },
                new SpanTextEl(spans.ToArray())
                {
                    Size = Z.CreditsSize, LineHeight = Z.CreditsLine, Color = Tok.TextSecondary, Wrap = TextWrap.WrapWholeWords, MinWidth = 0f,
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
        // An artist's own releases ("more by") link the artist's discography (Z-Album, Z-Album-Huge) and caption each release by its kind and year; any
        // other stack keeps its one-way "show all N".
        bool releases = p.Discography is not null;
        Element? link = p.Discography is { } discography ? K.Link("stack:discography", Loc.Get(Strings.Nav.Discography), discography, Z.AsideHeadingH)
            : p.Count > p.Shown ? K.Link("stack:all", Strings.Home.ShowAllCount(p.Count), p.Expand, Z.AsideHeadingH)
            : null;
        Element body;
        // The Huge aside's grid of covers and the rows are two arrangements (the column count changes): one keyed tree each, cross-faded inside the
        // body, whose height eases as before (ZuneMotion: "grids reflow by fading").
        bool grid = aside && lay.MoreByGrid;
        // Z-Album's aside: an artist's releases as plateless rows of a 56 cover under "more by" and its "discography" (the name is the pivot's).
        bool releaseRows = aside && releases && !grid;
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
                        ? Controls.Surface(CardOf(row.Card!, releases), s_gridShape, cell) with { Key = row.Card!.Key }
                        : TrailRow(row, releases);
                }
                rows.Add(new BoxEl { Direction = 0, Gap = Z.MoreByGridGap, MinWidth = 0f, Children = cells });
            }
            body = new BoxEl { Key = Z.StackKey + ":grid", Direction = 1, Gap = Z.MoreByGridGap, MinWidth = 0f, Children = rows.ToArray() };
        }
        else
        {
            // The cover stands on the heading's x and 8 under its row (Z-Album): the row's plate bleeds its own pad.
            var rows = new Element[p.Rows.Count];
            for (int i = 0; i < rows.Length; i++) rows[i] = releaseRows ? ReleaseRow(p.Rows[i]) : TrailRow(p.Rows[i], releases);
            body = new BoxEl
            {
                Key = Z.StackKey + ":rows", Direction = 1, Gap = releaseRows ? 0f : Z.SkelRowGap, MinWidth = 0f,
                Margin = releaseRows ? new Edges4(-SurfaceGeometry.RowPad, -SurfaceGeometry.RowPad, -SurfaceGeometry.RowPad, 0f) : default, Children = rows,
            };
        }
        body = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Animate = Z.SlotReveal,
            Children = [body with { Enter = enter, Exit = ZuneMotion.SwapFade }],
        };
        // In the aside the heading carries a name in a narrow column: the 22/28 step over at most two lines, the row a floor of the heading height (audit B).
        // Z-Album's "more by" is the heading's own voice without the name, its "discography" right after it.
        Element heading = releaseRows
            ? new BoxEl
            {
                Key = Z.StackKey + ":head", Direction = 0, AlignItems = FlexAlign.Center, Height = Z.AsideHeadingH, Shrink = 0f, MinWidth = 0f,
                Gap = Z.SectionGap,
                Children = [K.Heading(Loc.Get(Strings.Detail.MoreByHeading)), link!.Value],
            }
            : aside
            ? new BoxEl
            {
                Key = Z.StackKey + ":head", Direction = 0, AlignItems = FlexAlign.Center, MinHeight = Z.AsideHeadingH, Shrink = 0f, MinWidth = 0f,
                Gap = Z.SectionGap,
                Children = link is null ? [AsideNamedHeading(StackTitle(p))] : [AsideNamedHeading(StackTitle(p)), link.Value],
            }
            : K.HeadingRow(Z.StackKey + ":head", K.NamedHeading(StackTitle(p)), link, Z.AsideHeadingH);
        return new BoxEl
        {
            Key = Z.StackKey, Direction = 1, Gap = Z.AsideRowGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [heading, body],
        };
    }

    /// <summary>The aside's named heading ("More by Mia Solace"): the heading voice as written at 22/28, wrapping by words to two lines.</summary>
    /// <summary>A stack's title in Zune's voice: its words lowercase ("featured on", "more by"), the name it carries as written ("more by Mia Solace").</summary>
    static string StackTitle(AlbumTrailStackProps p)
    {
        string title = p.Title;
        int at = p.Name is { Length: > 0 } name ? title.IndexOf(name, StringComparison.Ordinal) : -1;
        if (at < 0) return ZuneText.Word(title, LabelKind.Heading);
        int end = at + p.Name!.Length;
        return ZuneText.Word(title[..at], LabelKind.Heading) + p.Name + ZuneText.Word(title[end..], LabelKind.Heading);
    }

    static TextEl AsideNamedHeading(string title) => K.NamedHeading(title) with
    {
        Size = Z.AsideNamedSize, LineHeight = Z.AsideNamedLine, MaxLines = Z.AsideNamedLines, Wrap = TextWrap.WrapWholeWords, Grow = 1f, Basis = 0f,
    };

    static Element TrailRow(TrailRowData row, bool release = false) => row.Kind switch
    {
        TrailRowKind.Card => Controls.Surface(CardOf(row.Card!, release), Shape.RowTileStill) with { Key = row.Card!.Key },
        TrailRowKind.Merch => MerchRow(row.Merch!),
        _ => new BoxEl { Height = Z.PlaceholderRowH },
    };

    /// <summary>One of the artist's releases in Z-Album's aside: the 56 cover, the title over "type · year" (the shared surface: its hover play and "…").</summary>
    static Element ReleaseRow(TrailRowData row) => row.Kind == TrailRowKind.Card
        ? Controls.Surface(CardOf(row.Card!, release: true), s_releaseRow) with { Key = row.Card!.Key }
        : TrailRow(row);

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
        // No About block: Zune draws no about card on the album (the shape's About is the core's reserve for a theme that does).
        var sections = new List<Element>(2 + shape.RowBlocks);
        if (shape.Video) sections.Add(SkelSection(K.Bar(Z.VideoThumbW, Z.SkelVideoH), heading: shape.VideoShelf));
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
