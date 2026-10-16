// ── Themes/Classic/Structure/Views/Detail/ClassicAlbumTrailing.cs — Classic's album trailing band (themes plan 6.1, T9b) ────────────────────────────
//
// From `Entities\Album.Page.cs` §3 and `Entities\Album.UI.cs` §3: the band under the rows. `Album.TrailingHost` keeps the demand, the pending gate with its
// one-swap latch and the deadline; `Album.SectionsHost` keeps the stamp, the demand of the video counterparts and which sections are present; `Album.StackHost`
// keeps the expand state and its stamp. Every row's data (the related albums, the playlists, the merch listings, the fans, the videos) reaches the views as
// the plain records of `Theming\Contracts\DetailAlbumProps.cs`, with the verbs (open, play, drag, menu, watch) as closures the core built.
//
//   · the REGION (`TrailingTemplate`): ONE `SkelRegionEl`, shimmering from the first frame and revealing once into whatever it resolves to — the sections, or a
//     collapsed empty box. The shimmer is shaped by what the row already knows (`PageRules.SkeletonShape`) and the region is keyed on that shape, so a single
//     never reserves an album's rows block and the reveal does not collapse the page under the cursor;
//   · the SECTIONS (`SectionsTemplate`): the music videos (ONE video keeps the hero card's geometry; TWO OR MORE get a horizontal shelf through the shared
//     `PagedShelf`), About the artist, Fans also like, and the list sections, in that fixed order;
//   · the STACK (`StackTemplate`): a capped vertical stack under a section header; "Show all N" lengthens it IN PLACE and is one-way;
//   · the HUGE ASIDE (`AsideTemplate`, contract 1.21, C-Album-Huge / -Huge4K): About this release, the artist card with its image band, Fans also like as
//     a four-column grid and "More by", 24 apart in the frame's docked aside column. While it is docked the band leaves those sections out and the rail its
//     release row (`Detail.AsideDocked`);
//   · the SKELETON: the section skeletons `PageRules.SkeletonShape` names, in the loaded band's own order. The sizes are `PageRules.Skel*` (derived from the
//     real builders, pinned by facts) so `PageRules.SkeletonHeight` equals what is drawn and the reveal is a dissolve, not a snap. It deliberately UNDER-states
//     (no "show all", merch or two-video shelf placeholder).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using PageRules = Wavee.Album.PageRules;

namespace Wavee.Themes.Classic;

/// <summary>Classic's album trailing views.</summary>
internal static class ClassicAlbumTrailing
{
    static readonly Func<bool> s_never = static () => false;

    // ══ 1. THE REGION ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.AlbumTrailing"/>.</summary>
    public static Element TrailingTemplate(AlbumTrailingProps p, in ViewContext ctx)
    {
        var shape = p.Shape;
        return new BoxEl
        {
            Direction = 1, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                new SkelRegionEl(
                    Pending: p.Pending, Failed: s_never, Content: p.Content, ShimmerSource: () => TrailingSkeleton(in shape), OnFailed: null,
                    Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: true)
                { Key = p.SkeletonKey },
            ],
        };
    }

    // ══ 1b. THE HUGE ASIDE (C-Album-Huge, C-Album-Huge4K) ═════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.AlbumAside"/>: the sections present, in the board's order, at the tier record's aside measure.</summary>
    public static Element AsideTemplate(AlbumAsideProps p, in ViewContext ctx)
    {
        var g = ClassicDetailTiers.Of(ctx.Tier()).Aside ?? ClassicDetailAside.Huge;
        float content = g.ContentWidthFor(DetailKind.Album, body: true);
        var kids = new List<Element>(4);
        if (p.Release is { } release) kids.Add(release);
        // The card freezes at mount: its band height is in the key, so a Huge | Huge 4K crossing re-mounts it at the new band.
        if (p.About is { } artist)
            kids.Add(Controls.ArtistAboutCard(artist, AboutLayout.Aside, g.ArtistBand)
                with { Key = "aside:about:" + artist.Uri.Text + ":" + g.ArtistBand.ToString(CultureInfo.InvariantCulture) });
        if (p.Fans.Count > 0) kids.Add(AsideFans(p.Fans, g, content));
        if (p.MoreBy is { } moreBy) kids.Add(moreBy);
        return new BoxEl { Direction = 1, Gap = g.AlbumGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
    }

    /// <summary>Fans also like in the aside: the 14/20 section title over rows of four circular cards (C-Album-Huge 96 circles 12 apart in rows, 8 across;
    /// 120 and 16 at 4K), the same card as the band's.</summary>
    static Element AsideFans(IReadOnlyList<TrailCardData> fans, ClassicDetailAside g, float content)
    {
        float cell = g.FanCellFor(content);
        int columns = Math.Max(1, g.FanColumns);
        var rows = new List<Element>((fans.Count + columns - 1) / columns);
        for (int start = 0; start < fans.Count; start += columns)
        {
            int n = Math.Min(columns, fans.Count - start);
            var cards = new Element[n];
            for (int i = 0; i < n; i++)
                cards[i] = Controls.Surface(CardOf(fans[start + i]), s_fanShape, cell) with { Key = fans[start + i].Key };
            rows.Add(new BoxEl { Direction = 0, Gap = g.FanColumnGap, MinWidth = 0f, Children = cards });
        }
        return new BoxEl
        {
            Key = "aside:fans", Direction = 1, Gap = Spacing.M, MinWidth = 0f,
            Children =
            [
                Ui.BodyStrong(Loc.Get(Strings.Detail.FansAlsoLike)) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new BoxEl { Direction = 1, Gap = g.FanRowGap, MinWidth = 0f, Children = rows.ToArray() },
            ],
        };
    }

    // ══ 2. THE SECTIONS ═════════════════════════════════════════════════════════════════════════════════════════════════

    static Edges4 SectionPad => new(Spacing.L, Spacing.XL, Spacing.L, Spacing.L);

    /// <summary>The registered template of <see cref="ViewKeys.AlbumSections"/>: the sections the host decided are present, each in its place.</summary>
    public static Element SectionsTemplate(AlbumSectionsProps p, in ViewContext ctx)
    {
        if (!p.Any) return new BoxEl();   // nothing to show: the region eases to zero

        var sections = new List<Element>(7);
        if (p.Videos.Count > 0) sections.Add(VideosSection(p.Videos, p.CustomVideo, p.Overlay));
        if (p.About is { } lead) sections.Add(AboutSection(lead));
        if (p.Fans.Count > 0) sections.Add(Section(Loc.Get(Strings.Detail.FansAlsoLike), FansRow(p.Fans)));
        for (int i = 0; i < p.Lists.Count; i++) sections.Add(ListSection(p.Lists[i]));
        return new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, Children = sections.ToArray() };
    }

    /// <summary>The list section wrapper: the page's trailing padding around a capped, expandable stack.</summary>
    static Element ListSection(Element stack) => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, Padding = SectionPad,
        Children = [stack],
    };

    /// <summary>A plain section (no cap, no link): "Fans also like".</summary>
    static Element Section(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M, AlignSelf = FlexAlign.Stretch, Padding = SectionPad,
        Children = [R.Type.RailHeader(title), body],
    };

    static Element SubtitleLine(string text)
        => R.Type.TrackMeta(text) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>What a media surface shows for one related album, playlist or artist: the subtitle is a plain string the view paints.</summary>
    static Controls.CardData CardOf(TrailCardData c)
        => new(c.Uri, c.Title, c.Subtitle is { } subtitle ? SubtitleLine(subtitle) : null, c.CoverUrl, c.OnClick, c.OnPlay, c.Circular,
            Drag: c.Drag is { } drag ? Drag.Source(drag) : null)
        { Menu = c.Menu };

    // ── the fans ──

    static readonly SurfaceShape s_fanShape = Shape.Shelf(captionLines: 1);

    /// <summary>"Fans also like": at most eight circular artist cards in ONE clipped row, the same card as the artist page (click opens, hover play toggles
    /// the artist context, container menu, drag source).</summary>
    static Element FansRow(IReadOnlyList<TrailCardData> fans)
    {
        var chips = new Element[fans.Count];
        for (int i = 0; i < chips.Length; i++)
            chips[i] = Controls.Surface(CardOf(fans[i]), s_fanShape, PageRules.FanCardW) with { Key = fans[i].Key };
        return new BoxEl { Direction = 0, Gap = Spacing.M, ClipToBounds = true, Children = chips };
    }

    /// <summary>"About the artist": the shared <see cref="Controls.ArtistAboutCard"/> in its page row layout (the link is the focusable click owner; Follow is
    /// its sibling, never nested inside it). The card is at least the skeleton's reserve (<see cref="PageRules.SkelAboutH"/>): a bio-less artist's card would
    /// otherwise land shorter than the skeleton it replaces and pull the sections under it up.</summary>
    static Element AboutSection(Artist artist)
    {
        var card = Controls.ArtistAboutCard(artist, AboutLayout.Page);
        if (card is BoxEl box) card = box with { MinHeight = PageRules.SkelAboutH };
        return new BoxEl
        {
            Direction = 1, AlignSelf = FlexAlign.Stretch, Padding = SectionPad,
            Children = [card with { Key = "about:" + artist.Uri.Text }],
        };
    }

    // ── the music-video section (ch 05 W12; the 2026-09-17 rewrite) ──
    //
    // ONE thumbnail rule for both arms: EVERY still is the video's own (PageRules.SelectVideos → Track.VideoImageId, the counterpart's art, the song's art — in
    // that order). ONE click rule for both arms: the card plays the VIDEO — that is, the SONG that owns it, because kind 99 keys a video on its song.

    /// <summary>The section's two arms (<see cref="PageRules.ArmFor"/>). ONE video keeps the hero card's geometry (200×116 thumb under a 44 FAB) and its "WATCH
    /// THE OFFICIAL VIDEO" eyebrow. TWO OR MORE get a HORIZONTAL SHELF through the shared <c>PagedShelf</c>, so the edge fades, the pips and the page snap are
    /// the engine's and not hand-rolled. Its section box has no bottom padding: About-the-artist supplies that step. <paramref name="overlay"/> is the hero's
    /// context-menu host (a shelf card's surface finds its own).</summary>
    static Element VideosSection(IReadOnlyList<AlbumVideoCard> videos, bool customVideo, IOverlayService? overlay)
    {
        Element body = PageRules.ArmFor(videos.Count) switch
        {
            PageRules.VideoArm.Hero => VideoHero(videos[0], customVideo, overlay),
            PageRules.VideoArm.Shelf => VideoShelf(videos),
            _ => new BoxEl(),
        };
        return new BoxEl
        {
            Direction = 1, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(Spacing.L, Spacing.XL, Spacing.L, 0f),
            Children = [body],
        };
    }

    /// <summary>The HERO arm: one video as a wide ROW — a 200×116 thumb beside its text — which the Video stack (a cover over its labels) cannot express, so it
    /// keeps its own layout and takes the surface's RULES instead of its tree: the ownership rule (Button role, focus stop and hand cursor exactly when the card
    /// is invokable), the real now-playing overlay at rest over the thumb (the FAB, the equalizer pill and its tooltip), and — a music video is a track — the
    /// track's drag and context menu, the menu through a hot-revealed "…" as on every other media surface. Without an overlay host the menu and its "…" are
    /// simply absent.</summary>
    static Element VideoHero(AlbumVideoCard v, bool customVideo, IOverlayService? overlay)
    {
        var mode = SurfaceRules.Ownership(inSlot: false, hasClick: true);
        bool menu = !Controls.IsNullOverlay(overlay) && v.Uri.Length > 0;
        Element thumbnail = new BoxEl
        {
            Width = ClassicAlbumLayout.HeroThumbW, Height = ClassicAlbumLayout.HeroThumbH, Shrink = 0f, ZStack = true,
            Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
            Children =
            [
                Controls.Artwork(v.Thumb, ClassicAlbumLayout.HeroThumbW, ClassicAlbumLayout.HeroThumbH, Radii.Control),
                Controls.NowPlayingOverlay(v.Uri, v.Watch, ClassicAlbumLayout.HeroFab, centred: true, atRest: true),
            ],
        };
        Element text = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS,
            Children =
            [
                R.Type.Eyebrow(Loc.Get(customVideo ? Strings.VideoOverride.CustomLabel : Strings.Detail.WatchOfficialVideo))
                    with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Controls.TrimmedTitle(R.Type.RailHeader(v.Title)
                    with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }, v.Title),
                new TextEl(v.Meta)
                {
                    Size = ClassicAlbumLayout.VideoMetaSize, LineHeight = ClassicAlbumLayout.VideoMetaLine, Color = Tok.TextSecondary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
        var hero = new BoxEl
        {
            Key = v.Key,
            Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.M, Spacing.M, Spacing.L, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
            HoverFill = Tok.FillCardDefault,
            Role = mode.Role, Focusable = mode.OwnsFocus, Cursor = SurfaceRules.Cursor(in mode),
            FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = v.Watch,
            Draggable = Drag.Source(v.Drag),
            Children = menu ? [thumbnail, text, SurfaceParts.Action(Controls.MoreButton(null, requestsContext: true, restOpacity: 0f))] : [thumbnail, text],
        };
        return menu ? ContextSheets.Attach(hero, overlay!, v.Menu) : hero;
    }

    /// <summary>The SHELF arm: one horizontal, page-snapping strip of video cards under a "Music videos" header — the artist page's own video shelf, on the
    /// same shared <see cref="PagedShelf"/>. Nothing here is hand-rolled: the edge feather is the control's <c>edgeFade</c>, the dots are its
    /// <see cref="ShelfPager.Pips"/> (a stock <c>PipsPager</c>), and <see cref="ShelfSnap.Page"/> is what makes a fling, a chevron and a pip all rest on a
    /// page boundary.
    /// <para><c>measured: true</c> — an album has a handful of videos, so the strip lays them all out and sizes itself to the tallest card instead of
    /// estimating a height. That is also what keeps the strip from jumping: nothing is guessed and then corrected.</para>
    /// <para>The shelf builds its OWN header row — <c>[header, spacer, pips, chevrons]</c> — so the header passed here is just the title and the count, and the
    /// pager lands at the trailing edge.</para></summary>
    static Element VideoShelf(IReadOnlyList<AlbumVideoCard> videos)
    {
        var items = new AlbumVideoCard[videos.Count];
        for (int i = 0; i < items.Length; i++) items[i] = videos[i];
        return PagedShelf.Create(items, s_videoCard,
            header: VideoShelfHeader(items.Length),
            pager: ShelfPager.Chevrons | ShelfPager.Pips,
            minCardW: ClassicAlbumLayout.VideoCardMinW, maxCardW: ClassicAlbumLayout.VideoCardMaxW, gap: Spacing.M, headerGap: Spacing.M,
            snap: ShelfSnap.Page, edgeFade: R.Space.FadeRail,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            lift: ShelfLift.None,   // the shared card hovers fill-only: no lift halo to reserve clearance for
            onInvoke: s_videoInvoke,   // the slot owns the click: the card's own Watch
            measured: true, keyOf: s_videoKey, maxItems: PageRules.VideoCap);
    }

    // Reference-stable: a shelf re-render must not rebuild its card/key delegates (PagedShelf re-pushes them as props).
    static readonly Func<AlbumVideoCard, int, float, Element> s_videoCard = static (v, _, w) => VideoShelfCard(v, w);
    static readonly Func<AlbumVideoCard, int, string> s_videoKey = static (v, _) => v.Key;
    static readonly Action<AlbumVideoCard, int> s_videoInvoke = static (v, _) => v.Watch();

    /// <summary>The shelf's title and count. The shelf's own row supplies the spacer and the pager after it.</summary>
    static Element VideoShelfHeader(int count) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
        Children =
        [
            R.Type.RailHeader(Loc.Get(Strings.Artist.MusicVideos))
                with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            R.Type.DenseTitle(count.ToString(CultureInfo.CurrentCulture)) with
            {
                Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f,
            },
        ],
    };

    /// <summary>One shelf cell: the shared <see cref="Shape.Video"/> surface — the video's own still fitted 16:9 to the card width the shelf hands it, the
    /// real now-playing overlay (FAB at rest), its title and its duration as the caption, the track's menu behind the corner "…" and the right-click, the
    /// track's drag, the hand cursor and the focus stop. (No explicit key: the shelf keys each card through <c>keyOf</c>.)</summary>
    static Element VideoShelfCard(AlbumVideoCard v, float width)
        => Controls.Surface(new Controls.CardData(v.Uri, v.Title, null, v.Thumb, v.Watch, v.Watch, Drag: Drag.Source(v.Drag))
        {
            CoverAspect = ClassicAlbumLayout.VideoAspect,
            Caption = v.Duration,
            Menu = v.Menu,
        }, Shape.Video, width);

    // ══ 3. THE STACK ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.AlbumTrailStack"/>: a capped vertical stack under a section header. "Show all N" lengthens it
    /// IN PLACE (the new height reveals) and is one-way.</summary>
    public static Element StackTemplate(AlbumTrailStackProps p, in ViewContext ctx)
    {
        var rows = new Element[p.Rows.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = TrailRow(p.Rows[i]);

        Element title = R.Type.RailHeader(p.Title) with
        {
            Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
        Element header = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Children = p.Count > p.Shown
                ? [title, HyperlinkButton.Create(Strings.Home.ShowAllCount(p.Count), p.Expand, size: ControlSize.Small)]
                : [title],
        };
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Grow = 1f, AlignSelf = FlexAlign.Stretch,
            // "Show all" lengthens the box in place — the new height reveals.
            Children = [header, new BoxEl { Direction = 1, Gap = Spacing.XS, Animate = Design.Reveal.Resize, Children = rows }],
        };
    }

    /// <summary>One row of a list section.</summary>
    static Element TrailRow(TrailRowData row) => row.Kind switch
    {
        TrailRowKind.Card => AlbumOrPlaylistRow(row.Card!),
        TrailRowKind.Merch => MerchRow(row.Merch!),
        _ => new BoxEl { Height = ClassicAlbumLayout.PlaceholderRowH },
    };

    /// <summary>A related album or a playlist this album appears on: the shared media surface (<see cref="Shape.RowTileStill"/> — 48 cover, hover play FAB,
    /// the hot-revealed "…", drag source) on the opaque card tile under its hairline. Its menu is the home card grammar, built at open from the card handle —
    /// the row has no tracklist of its own to compose a track menu from.</summary>
    static Element AlbumOrPlaylistRow(TrailCardData c)
        => Controls.Surface(CardOf(c), Shape.RowTileStill) with { Key = c.Key };

    /// <summary>A merch listing: not a media row — nothing to play, and its call to action is the PRICE, trailing in accent ink ("Buy" when the wire gave
    /// none). With no shop url it is a listing, not a dead button: no scale, no role, no focus stop, no cursor, no click.</summary>
    static Element MerchRow(MerchRowData m)
    {
        bool live = m.Open is not null;
        return new BoxEl
        {
            Key = m.Key,
            Direction = 0, Height = ClassicAlbumLayout.MerchRowH, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Corners = Radii.CardAll,
            Fill = Tok.FillCardSecondary, HoverFill = Tok.FillCardDefault, PressedFill = Tok.FillSubtleTertiary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            HoverScale = R.Motion.ScaleSubtle.HoverIf(live), PressScale = R.Motion.ScaleSubtle.PressIf(live),
            Role = live ? AutomationRole.Button : AutomationRole.None,
            Focusable = live, Cursor = live ? CursorId.Hand : (CursorId?)null,
            OnClick = m.Open,
            Children =
            [
                Controls.Artwork(m.ImageUrl, ClassicAlbumLayout.MerchArt, ClassicAlbumLayout.MerchArt, Radii.Control),
                R.Type.TrackTitle(m.Name) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new TextEl(m.Price.Length > 0 ? m.Price : Loc.Get(Strings.Artist.Buy))
                {
                    Size = ClassicAlbumLayout.MerchPriceSize, LineHeight = ClassicAlbumLayout.MerchPriceLine, Weight = 600, Shrink = 0f,
                    Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    // ══ 4. THE SKELETON ═════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // Video (when known), About (no header), Fans also like (header + a clipped row of circular cards), then the list-section blocks.

    static Element TrailingSkeleton(in PageRules.TrailingShape shape)
    {
        var sections = new List<Element>(3 + shape.RowBlocks);
        if (shape.Video) sections.Add(shape.VideoShelf ? VideoShelfSkeleton() : VideoSkeleton());
        if (shape.About) sections.Add(AboutSkeleton());
        if (shape.Fans) sections.Add(SectionSkeleton(FansSkeleton()));
        for (int i = 0; i < shape.RowBlocks; i++) sections.Add(SectionSkeleton(RowsSkeleton()));
        return new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, Children = sections.ToArray() };
    }

    static BoxEl Bone(float w, float h, float radius = ClassicAlbumLayout.BoneRadius)
        => new() { Width = w, Height = h, Shrink = 0f, Corners = CornerRadius4.All(radius), Fill = Tok.FillSubtleSecondary };

    /// <summary>A text-column bone: up to <paramref name="maxW"/> wide, narrower when the column is (a narrow list column must not push the bar past the
    /// card).</summary>
    static BoxEl TextBone(float maxW, float h)
        => new() { MaxWidth = maxW, Height = h, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(ClassicAlbumLayout.BoneRadius), Fill = Tok.FillSubtleSecondary };

    /// <summary>The single-video hero's face: a 200×116 thumb beside three bars, under the section's own pad (no bottom).</summary>
    static Element VideoSkeleton() => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, Padding = new Edges4(Spacing.L, Spacing.XL, Spacing.L, 0f),
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.M, Spacing.M, Spacing.L, Spacing.M),
                Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault, ClipToBounds = true,
                Children =
                [
                    Bone(PageRules.SkelVideoThumbW, PageRules.SkelVideoThumbH, Radii.Control),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.S,
                        Children =
                        [
                            TextBone(ClassicAlbumLayout.SkelEyebrowW, ClassicAlbumLayout.SkelEyebrowH),
                            TextBone(ClassicAlbumLayout.SkelTitleW, ClassicAlbumLayout.SkelTitleH),
                            TextBone(ClassicAlbumLayout.SkelMetaW, ClassicAlbumLayout.SkelEyebrowH),
                        ],
                    },
                ],
            },
        ],
    };

    /// <summary>Two or more videos load as the shelf (<see cref="VideoShelf"/>), not the single hero: the shelf's header row (title bar left, the pager's 32 DIP
    /// right), then one clipped row of 16:9 cards at the shelf's narrowest card width (<see cref="ClassicAlbumLayout.VideoCardMinW"/>), under the section's
    /// own pad (no bottom).</summary>
    static Element VideoShelfSkeleton()
    {
        var cards = new Element[PageRules.SkelVideoShelfCards];
        var seed = Controls.CardData.Seed with { CoverAspect = ClassicAlbumLayout.VideoAspect };
        for (int i = 0; i < cards.Length; i++) cards[i] = Controls.Surface(seed, Shape.Video, ClassicAlbumLayout.VideoCardMinW);
        return new BoxEl
        {
            Direction = 1, AlignSelf = FlexAlign.Stretch, Gap = PageRules.SkelVideoShelfGap,
            Padding = new Edges4(Spacing.L, Spacing.XL, Spacing.L, 0f),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Height = PageRules.SkelVideoShelfHeaderH,
                    Children = [Bone(ClassicAlbumLayout.SkelHeaderBarW, PageRules.SkelHeaderBarH)],
                },
                new BoxEl { Direction = 0, Gap = Spacing.M, ClipToBounds = true, Children = cards },
            ],
        };
    }

    /// <summary>"About the artist": the card has no header — an 84 circle, two bars and the Follow cell, in the page row layout (<c>Controls.PageAbout</c>: the
    /// link cell carries no right padding, the Follow cell its own L on both sides).</summary>
    static Element AboutSkeleton() => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, Padding = SectionPad,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Height = PageRules.SkelAboutH,
                Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault, ClipToBounds = true,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Padding = new Edges4(Spacing.L, Spacing.M, 0f, Spacing.M),
                        Children =
                        [
                            Bone(PageRules.SkelAboutPortrait, PageRules.SkelAboutPortrait, PageRules.SkelAboutPortrait / 2f),
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.S,
                                Children = [TextBone(ClassicAlbumLayout.SkelAboutNameW, ClassicAlbumLayout.SkelAboutNameH),
                                            TextBone(ClassicAlbumLayout.SkelAboutBioW, ClassicAlbumLayout.SkelEyebrowH)],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Shrink = 0f, Padding = new Edges4(Spacing.L, 0f, Spacing.L, 0f),
                        Children = [Bone(ClassicAlbumLayout.SkelFollowW, ClassicAlbumLayout.SkelFollowH, ClassicAlbumLayout.SkelFollowRadius)],
                    },
                ],
            },
        ],
    };

    /// <summary>"Fans also like": the real row's seed face — circular 148-wide shelf cards in one clipped row.</summary>
    static Element FansSkeleton()
    {
        var cards = new Element[PageRules.FansCap];
        var seed = Controls.CardData.Seed with { Circular = true };
        for (int i = 0; i < cards.Length; i++) cards[i] = Controls.Surface(seed, s_fanShape, PageRules.FanCardW);
        return new BoxEl { Direction = 0, Gap = Spacing.M, ClipToBounds = true, Children = cards };
    }

    /// <summary>The rows block: the SEED face of the real rows' own shape (<see cref="Shape.RowTileStill"/>), which is as tall as the live row (its 64 floor,
    /// pinned by a fact) because every line keeps its box.</summary>
    static Element RowsSkeleton()
    {
        var rows = new Element[PageRules.SkelRows];
        for (int i = 0; i < rows.Length; i++) rows[i] = Controls.Surface(Controls.CardData.Seed, Shape.RowTileStill);
        return new BoxEl { Direction = 1, Gap = PageRules.SkelRowGap, Children = rows };
    }

    // SectionPad's vertical sum is PageRules.SkelSectionPadV; the gap is PageRules.SkelSectionGap; the header line box is PageRules.SkelHeaderH with its
    // 160×18 bone centred in it.
    static Element SectionSkeleton(Element body) => new BoxEl
    {
        Direction = 1, Gap = PageRules.SkelSectionGap, AlignSelf = FlexAlign.Stretch, Padding = SectionPad,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Height = PageRules.SkelHeaderH,
                Children = [Bone(ClassicAlbumLayout.SkelHeaderBarW, PageRules.SkelHeaderBarH)],
            },
            body,
        ],
    };
}
