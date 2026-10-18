// ── Themes/Zune/Structure/Views/Artist/ZuneArtistSections.cs — Zune's artist page sections (themes plan 6.3; P5 Z6a2) ───────────────────
//
// The section BODIES under the hero (the plan, the pivot's membership, the caps and the biography's rules stay core: `ArtistSections`). Every
// section is a 26/32 light heading row over its body, 28 apart; no card, no rule, no accent spine (Zune speaks in ink and the accent as ink).
//
//   Popular          the chart (Z3's style) beside the rail (latest release, artist pick, an upcoming release) at the tiers with room, the rail
//                    under it elsewhere. Upcoming and Latest release ride the rail whenever Popular stands; without Popular, Latest release is
//                    a section of its own.
//   Albums · Singles · Compilations   the heading row ("albums", "N releases", "discography" at its end) over the core's grid and album drawer
//                    (`StyleKeys.Discography`, `ZuneDiscography`). "discography" opens the standalone page.
//   Appears on · Videos · Playlists · Concerts · Merch · Gallery · Related / Fans   a shelf of 154 covers 16 apart under the heading row
//                    (the shelf's chevrons end it); an artist's card is round; the video card is 16:9; the gallery's are plain squares.
//   Tour             the flat announcement row.
//   Biography        "about": the biography (links in the accent), the external links, the cities as 4-dip bars, and the profile facts as light
//                    numerals; the facts stand beside the text at the tiers with room.
//
// Words the theme writes take their casing from the case roles (`K.Heading`, `K.Caption`); names, titles and the biography are data, never re-cased.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using Rules = Wavee.Themes.Zune.ZuneArtistRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's artist page sections.</summary>
internal static class ZuneArtistSections
{
    /// <summary>Whether a section draws a body of its own: the upcoming release and the latest release ride the Popular band's rail when it
    /// stands (<paramref name="railed"/>).</summary>
    public static bool Draws(ArtistSection s, bool railed) => s switch
    {
        ArtistSection.Upcoming => false,
        ArtistSection.LatestRelease => !railed,
        _ => true,
    };

    /// <summary>One section's body, in the plan's order.</summary>
    public static Element Body(ArtistSection s, ArtistPageProps p, ZuneArtistLayout lay)
    {
        Artist a = p.Artist;
        return s switch
        {
            ArtistSection.Popular => PopularBand(p, lay),
            ArtistSection.LatestRelease => K.Section(K.Heading(Loc.Get(Strings.Artist.LatestRelease), null, null), ZuneArtistBanners.Latest(a.Latest, lay.LatestArt)),
            ArtistSection.Albums => Discography(a, DiscoFacet.Albums, p, lay),
            ArtistSection.Singles => Discography(a, DiscoFacet.Singles, p, lay),
            ArtistSection.Compilations => Discography(a, DiscoFacet.Compilations, p, lay),
            ArtistSection.AppearsOn => AppearsOnShelf(a, lay),
            ArtistSection.Tour => ZuneArtistBanners.Tour(a),
            ArtistSection.MusicVideos => VideosShelf(a, lay),
            ArtistSection.Playlists => PlaylistsShelf(a, lay),
            ArtistSection.Concerts => ConcertsShelf(a, lay),
            ArtistSection.Merch => MerchShelf(a, lay),
            ArtistSection.Biography => About(p, lay),
            ArtistSection.Gallery => GalleryShelf(p, lay),
            ArtistSection.Related => ArtistsShelf(a.RelatedSlots, lay),
            _ => ArtistsShelf(p.Fans, lay),
        };
    }

    // ── popular and its rail ─────────────────────────────────────────────────────────────────────────────────────────────

    static Element PopularBand(ArtistPageProps p, ZuneArtistLayout lay)
    {
        Artist a = p.Artist;
        Element chart = new BoxEl
        {
            Key = "zune-artist-chart", Direction = 1, MinWidth = 0f, Grow = lay.RailBeside ? 1f : 0f, Basis = lay.RailBeside ? 0f : float.NaN,
            // The record's cap (Huge: 1200), so a 2400 page never spreads a track's title and its time across the screen; the rail follows it.
            MaxWidth = lay.ChartMaxW > 0f ? lay.ChartMaxW : float.NaN,
            Children = [Artist.Chart(a, p.AccentSignal)],
        };

        var rail = new List<Element>(3);
        if (a.HasPreRelease && a.HasUpcoming)
            rail.Add(RailItem("upcoming", Loc.Get(Strings.Artist.Upcoming), ZuneArtistBanners.Upcoming(a, p.AccentFn, lay.LatestArt)));
        var latest = a.Latest;
        if (latest.IsValid && latest.Knows(AlbumFields.DiscoCard))
            rail.Add(RailItem("latest", Loc.Get(Strings.Artist.LatestRelease), ZuneArtistBanners.Latest(latest, lay.LatestArt, lay.LatestStacked)));
        if (a.HasPick)
            rail.Add(RailItem("pick", Loc.Get(Strings.Artist.ArtistPick), ZuneArtistBanners.Pick(a, p.AccentFn, lay.PickArt)));
        if (rail.Count == 0) return new BoxEl { Key = Rules.PopularKey, Direction = 1, MinWidth = 0f, Children = [chart] };

        Element railBox = new BoxEl
        {
            Key = Rules.RailKey, Direction = 1, Gap = lay.RailStackGap, MinWidth = 0f, Shrink = 0f, Width = lay.RailBeside ? lay.RailW : float.NaN,
            Layout = ZuneMotion.ColumnCollapse, Children = rail.ToArray(),
        };
        return new BoxEl
        {
            Key = Rules.PopularKey, Direction = (byte)(lay.RailBeside ? 0 : 1), Gap = lay.RailBeside ? lay.RailGap : lay.ZoneGap, MinWidth = 0f,
            AlignItems = lay.RailBeside ? FlexAlign.Start : FlexAlign.Stretch,
            Layout = ZuneMotion.ColumnCollapse, Children = [chart, railBox],
        };
    }

    /// <summary>The loading page's popular band (Z-State-Loading): the chart's skeleton beside the rail, whose "latest release" heading stands in real words over a skeleton
    /// card (cover and two lines), at the loaded band's geometry. The chart skeleton's own heading is real too.</summary>
    public static Element PopularSkeleton(Element chartShape, ZuneArtistLayout lay)
    {
        Element chart = new BoxEl
        {
            Key = "zune-artist-chart", Direction = 1, MinWidth = 0f, Grow = lay.RailBeside ? 1f : 0f, Basis = lay.RailBeside ? 0f : float.NaN,
            MaxWidth = lay.ChartMaxW > 0f ? lay.ChartMaxW : float.NaN,
            Children = [chartShape],
        };
        var meta = R.Type.TrackMeta("Single · 2024");
        Element card = new BoxEl
        {
            Direction = 0, Gap = Rules.RailItemGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                new BoxEl { Width = lay.LatestArt, Height = lay.LatestArt, Shrink = 0f, Fill = Prop.Of(static () => R.Layer.RowHover) },
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Grow = 1f,
                    Children = [R.Type.TrackTitle("Latest release title") with { MaxLines = 1 }, meta with { MaxLines = 1 }],
                },
            ],
        };
        BoxEl head = K.Heading(Loc.Get(Strings.Artist.LatestRelease), null, null);
        Element railBox = new BoxEl
        {
            Key = Rules.RailKey, Direction = 1, Gap = lay.RailStackGap, MinWidth = 0f, Shrink = 0f, Width = lay.RailBeside ? lay.RailW : float.NaN,
            Children = [K.Section(head.Skel(head), card)],
        };
        return new BoxEl
        {
            Key = Rules.PopularKey, Direction = (byte)(lay.RailBeside ? 0 : 1), Gap = lay.RailBeside ? lay.RailGap : lay.ZoneGap, MinWidth = 0f,
            AlignItems = lay.RailBeside ? FlexAlign.Start : FlexAlign.Stretch, Children = [chart, railBox],
        };
    }

    static Element RailItem(string key, string title, Element body)
        => K.Section(K.Heading(title, null, null), body) with { Key = "zune-artist-rail:" + key };

    // ── the discography facets ───────────────────────────────────────────────────────────────────────────────────────────

    static Element Discography(Artist a, DiscoFacet facet, ArtistPageProps p, ZuneArtistLayout lay)
    {
        int total = Artist.FacetTotal(a, facet);
        string artistUri = a.Uri.Text;
        Element link = K.Link(Rules.DiscographyLinkPrefix + ((int)facet).ToString(CultureInfo.InvariantCulture), Loc.Get(Strings.Theme.Zune.Artist.Discography),
            () => ArtistVerbs.OpenRoute(DiscoRoute.Key(facet, artistUri)), lay.LinkTarget);
        Element heading = InlineHeading(Artist.FacetTitle(facet), link);
        // The grid is the core's; the facet's own heading is the page's (it holds the link and the count), so the style's facet draws the grid alone.
        return new BoxEl
        {
            Direction = 1, Gap = Rules.HeadGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children =
            [
                heading with { Key = Rules.FacetHeadPrefix + ((int)facet).ToString(CultureInfo.InvariantCulture) },
                Artist.FacetSection(a, facet, p.AccentFn, Rules.PinTop, Rules.GridRevealInset),
            ],
        };
    }

    /// <summary>A heading row as Z-Artist draws it: the word, then its link 20 after it on the heading's baseline ("albums  discography"), never at the row's end.</summary>
    static Element InlineHeading(string title, Element link)
        => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                K.HeadingText(title),
                new BoxEl { Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Margin = new Edges4(ZuneListRules.ChartHeadGap, ZuneListRules.ChartBaseline, 0f, 0f), Children = [link] },
            ],
        };

    // ── the shelves ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One shelf card's identity + the data it paints that is not on the handle: a slot, its row version, an optional id (a playlist
    /// subtitle, a video thumb, a gallery image) and an int (a video's duration). A record, so the shelf's gate compares it by VALUE.</summary>
    readonly record struct ShelfEntity(int Slot, uint Version, StringId Sub, int Aux, string Key);

    static readonly Func<ShelfEntity, int, string> s_shelfKey = static (item, _) => item.Key;
    static readonly Func<float, float> s_squareHeight = static w => SurfaceGeometry.ShelfHeight(w);
    static readonly SurfaceShape s_shelfShape = Shape.Shelf(captionLines: 2);
    static readonly Track.MenuOptions s_trackMenu = new(ShowGoToAlbum: true);

    /// <summary>A shelf of fixed cards under the heading row, the chevrons at its end; one key at every width (the fit is a live prop).</summary>
    static Element ShelfOf(ShelfEntity[] items, Func<ShelfEntity, int, float, Element> cardAt, string title, float cardW,
                           Func<float, float>? cardHeight = null, Action<ShelfEntity, int>? onInvoke = null)
        => new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children =
            [
                PagedShelf.Create(items, cardAt, onInvoke: onInvoke, cardHeight: cardHeight ?? s_squareHeight,
                    header: K.Heading(title, null, null), measured: false, keyOf: s_shelfKey,
                    pager: ShelfPager.Chevrons, minCardW: cardW, maxCardW: cardW, fixedCardW: cardW, gap: Rules.ShelfGap, headerGap: Rules.HeadGap,
                    edgeFade: Rules.ShelfEdgeFade, prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
                    lift: ShelfLift.None) with { Key = "zune-artist-shelf" },
            ],
        };

    static ShelfEntity[] Items(ReadOnlySpan<int> slots, EntityKind kind, int cap)
    {
        int n = Math.Min(slots.Length, cap);
        var items = new ShelfEntity[n];
        for (int i = 0; i < n; i++)
        {
            int s = slots[i];
            uint version = kind switch
            {
                EntityKind.Album => new Album(s).Version,
                EntityKind.Artist => new Artist(s).Version,
                EntityKind.Playlist => new Playlist(s).Version,
                EntityKind.Track => new Track(s).Version,
                EntityKind.Concert => new Concert(s).Version,
                _ => 0u,
            };
            items[i] = new ShelfEntity(s, version, default, 0, s.ToString(CultureInfo.InvariantCulture));
        }
        return items;
    }

    static Element CardSubtitle(string text) => K.Sub(text) with { MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    static Element AppearsOnShelf(Artist a, ZuneArtistLayout lay)
        => ShelfOf(Items(a.AppearsOnSlots, EntityKind.Album, ArtistSections.AppearsOnCap), AlbumCard, Loc.Get(Strings.Artist.AppearsOn), lay.ShelfCardW,
                   onInvoke: static (item, _) => OpenAlbum(item));

    static void OpenAlbum(ShelfEntity item) => Track.GoToAlbum(new Album(item.Slot));

    /// <summary>Square cover, title, and the year (the kind when the year is unknown).</summary>
    static Element AlbumCard(ShelfEntity item, int index, float w)
    {
        var al = new Album(item.Slot);
        string uri = al.Uri.Text;
        string title = al.Title;
        string? cover = Controls.ArtUrl(al.ImageId);
        string sub = al.Year > 0 ? al.Year.ToString(CultureInfo.CurrentCulture) : Detail.Text.KindLabel(al.Kind);
        var data = new Controls.CardData(uri, title, CardSubtitle(sub), cover,
            OnClick: () => OpenAlbum(item),
            OnPlay: () => ArtistVerbs.PlayOrToggle(al.Id),
            Drag: Drag.Source(() => new DragPayload(DragKind.Album, uri, uri, title, new EntityRef(EntityKind.Album, al.Slot), ArtUrl: cover)))
        {
            Menu = () => Menus.Container(ActionTarget.ForAlbum(al.Uri, title), cover, sub),
        };
        return Controls.Surface(data, s_shelfShape, w);
    }

    static Element VideosShelf(Artist a, ZuneArtistLayout lay)
    {
        var slots = a.VideoSlots;
        var payload = a.VideoPayload;
        int n = Math.Min(slots.Length, ArtistSections.VideoCap);
        var items = new ShelfEntity[n];
        for (int i = 0; i < n; i++)
        {
            var v = i < payload.Length ? payload[i] : default;
            items[i] = new ShelfEntity(slots[i], new Track(slots[i]).Version, v.Thumb, v.DurationMs, slots[i].ToString(CultureInfo.InvariantCulture));
        }
        return ShelfOf(items, VideoCard, Loc.Get(Strings.Artist.MusicVideos), lay.VideoCardW, s_videoHeight, static (item, _) => PlayVideo(item));
    }

    static void PlayVideo(ShelfEntity item) => ArtistVerbs.Play(new Track(item.Slot).Id);

    const float VideoAspect = 16f / 9f;
    static readonly Func<float, float> s_videoHeight = static w => SurfaceGeometry.StackExtent(Shape.Video, w, VideoAspect);

    /// <summary>The 16:9 music-video card: the shared video shape over the thumb, the duration as its caption; it stands for its track.</summary>
    static Element VideoCard(ShelfEntity item, int index, float w)
    {
        var t = new Track(item.Slot);
        string uri = t.Uri.Text;
        string title = t.Title;
        string? thumb = Controls.ArtUrl(item.Sub);
        Action play = () => PlayVideo(item);
        var data = new Controls.CardData(uri, title, null, thumb, OnClick: play, OnPlay: play,
            Drag: Drag.Source(() => new DragPayload(DragKind.Track, uri, uri, title, new EntityRef(EntityKind.Track, t.Slot), Tracks: [t], ArtUrl: thumb)))
        {
            CoverAspect = VideoAspect,
            Caption = item.Aux > 0 ? Track.Format.TrackTime(item.Aux) : null,
            Menu = () => Track.Menu([t], in s_trackMenu),
        };
        return Controls.Surface(data, Shape.Video, w);
    }

    static Element PlaylistsShelf(Artist a, ZuneArtistLayout lay)
    {
        var slots = a.PlaylistSlots;
        var subtitles = a.PlaylistSubtitleIds;
        int n = Math.Min(slots.Length, ArtistSections.PlaylistCap);
        var items = new ShelfEntity[n];
        for (int i = 0; i < n; i++)
            items[i] = new ShelfEntity(slots[i], new Playlist(slots[i]).Version, i < subtitles.Length ? subtitles[i] : default, 0,
                slots[i].ToString(CultureInfo.InvariantCulture));
        return ShelfOf(items, PlaylistCard, Loc.Get(Strings.Artist.PlaylistsDiscovery), lay.ShelfCardW, onInvoke: static (item, _) => OpenPlaylist(item));
    }

    static void OpenPlaylist(ShelfEntity item) => ArtistVerbs.OpenPlaylist(new Playlist(item.Slot));

    static Element PlaylistCard(ShelfEntity item, int index, float w)
    {
        var pl = new Playlist(item.Slot);
        string uri = pl.Uri.Text;
        string title = ArtistVerbs.Text(pl.TitleId);
        string sub = ArtistVerbs.Text(item.Sub);
        string? cover = Controls.ArtUrl(pl.ImageId);
        var data = new Controls.CardData(uri, title, CardSubtitle(sub), cover,
            OnClick: () => OpenPlaylist(item),
            OnPlay: () => ArtistVerbs.PlayOrToggle(pl.Id),
            Drag: Drag.Source(() => new DragPayload(DragKind.Playlist, uri, uri, title, new EntityRef(EntityKind.Playlist, pl.Slot), ArtUrl: cover)))
        {
            Menu = () => Menus.Container(ActionTarget.ForPlaylist(pl.Uri, title), cover, sub),
        };
        return Controls.Surface(data, s_shelfShape, w);
    }

    // ── concerts and merch ───────────────────────────────────────────────────────────────────────────────────────────────

    static Element ConcertsShelf(Artist a, ZuneArtistLayout lay)
        => ShelfOf(Items(a.ConcertSlots, EntityKind.Concert, ArtistSections.ConcertCap), s_concertCard, Loc.Get(Strings.Artist.UpcomingConcerts),
                   lay.ShelfCardW, cardHeight: s_concertHeight);

    static readonly Func<float, float> s_concertHeight = static _ => Rules.StubH;

    /// <summary>The concert stub: the month over the day in the numeral voice, the venue and the city; flat, a wash on hover. A click opens it.</summary>
    static readonly Func<ShelfEntity, int, float, Element> s_concertCard = static (item, _, w) =>
    {
        var c = new Concert(item.Slot);
        var date = c.LocalDate;
        var (venue, city) = Concert.StubFacts(c);
        return new BoxEl
        {
            Key = "zune-concert-stub:" + c.Slot.ToString(CultureInfo.InvariantCulture),
            Direction = 0, Width = w, Height = Rules.StubH, Shrink = 0f, Gap = Rules.RailItemGap, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Rules.RailItemGap, 0f, Rules.RailItemGap, 0f),
            Corners = R.Shape.All(ShapeRole.Tile), HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetRow, Cursor = CursorId.Hand,
            OnClick = () => ArtistVerbs.OpenConcert(c),
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Width = Rules.StubDateW, Shrink = 0f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        K.Caption(date.ToString("MMM", CultureInfo.CurrentCulture)) with { Color = Prop.Of(static () => R.Ink.AccentAction) },
                        R.Type.Numeral(date.Day.ToString(CultureInfo.CurrentCulture)) with { Color = Tok.TextPrimary, MaxLines = 1 },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children = [K.Name(venue), K.Sub(city)],
                },
            ],
        };
    };

    static Element MerchShelf(Artist a, ZuneArtistLayout lay)
        => ShelfOf(Items(a.MerchSlots, EntityKind.Unknown, ArtistSections.MerchCap), s_merchCard, Loc.Get(Strings.Artist.Merch), lay.ShelfCardW);

    /// <summary>Square art, the name, the price in accent ink; it opens the shop link when the row carries one.</summary>
    static readonly Func<ShelfEntity, int, float, Element> s_merchCard = static (item, _, w) =>
    {
        ref readonly Merch merch = ref Album.MerchAt(item.Slot);
        string name = ArtistVerbs.Text(merch.Name);
        string price = ArtistVerbs.Text(merch.Price);
        string? image = Controls.ArtUrl(merch.ImageId);
        string shop = ArtistVerbs.Text(merch.ShopUrl);
        bool link = shop.Length > 0;
        return new BoxEl
        {
            Direction = 1, Gap = Rules.MerchGap, Width = w, Shrink = 0f, ClipToBounds = true, Padding = Edges4.All(Rules.MerchPad),
            Corners = R.Shape.All(ShapeRole.Tile), HoverFill = link ? R.Layer.RowHover : ColorF.Transparent, PressedFill = R.Layer.RowPressed,
            Role = link ? AutomationRole.Button : AutomationRole.None, Focusable = link, FocusVisualMargin = Design.FocusInsetRow,
            Cursor = link ? CursorId.Hand : null,
            OnClick = link ? () => ArtistVerbs.OpenLink(shop) : null,
            Children =
            [
                new BoxEl { ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(R.Shape.Cover), Children = [Controls.ArtworkFill(image, R.Shape.Cover)] },
                K.Name(name) with { Wrap = TextWrap.Wrap, MaxLines = 2 },
                R.Type.DenseMeta(price) with { Color = Prop.Of(static () => R.Ink.AccentAction), MaxLines = 1 },
            ],
        };
    };

    // ── the gallery and the related artists ──────────────────────────────────────────────────────────────────────────────

    static Element GalleryShelf(ArtistPageProps p, ZuneArtistLayout lay)
    {
        var ids = p.Artist.GallerySlots;
        int n = Math.Min(ids.Length, ArtistSections.GalleryCap);
        var items = new ShelfEntity[n];
        var urls = new string[n];
        for (int i = 0; i < n; i++)
        {
            items[i] = new ShelfEntity(i, 0, ids[i], 0, "g" + i.ToString(CultureInfo.InvariantCulture));
            urls[i] = Controls.ArtUrl(ids[i]) ?? "";
        }
        Action<string[], int> open = p.OpenGallery;
        return ShelfOf(items, (item, index, w) => new BoxEl
        {
            Width = w, Height = w, Corners = CornerRadius4.All(R.Shape.Cover), ClipToBounds = true,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered, Cursor = CursorId.Hand,
            HoverScale = R.Motion.ScaleStandard.Hover, PressScale = R.Motion.ScaleStandard.Press,
            OnClick = () => open(urls, index),
            Children = [Controls.Artwork(urls[index], w, w, R.Shape.Cover, decodePx: Rules.GalleryDecode)],
        }, Loc.Get(Strings.Artist.Gallery), lay.ShelfCardW);
    }

    /// <summary>"Fans also like": round cards with the subtitle "artist" — the related artists, or the fallback pool of followed artists.</summary>
    static Element ArtistsShelf(ReadOnlySpan<int> slots, ZuneArtistLayout lay)
        => ShelfOf(Items(slots, EntityKind.Artist, ArtistSections.FansCap), ArtistCard, Loc.Get(Strings.Detail.FansAlsoLike), lay.ShelfCardW,
                   onInvoke: static (item, _) => OpenArtist(item));

    static void OpenArtist(ShelfEntity item) => Track.GoToArtist(new Artist(item.Slot));

    static Element ArtistCard(ShelfEntity item, int index, float w)
    {
        var ar = new Artist(item.Slot);
        string uri = ar.Uri.Text;
        string name = ar.Name;
        string? image = Controls.ArtUrl(ar.ImageId);
        string sub = Loc.Get(Strings.Search.TypeArtist);
        var data = new Controls.CardData(uri, name, CardSubtitle(sub), image,
            OnClick: () => OpenArtist(item),
            OnPlay: () => ArtistVerbs.PlayOrToggle(ar.Id),
            Circular: true,
            Drag: Drag.Source(() => new DragPayload(DragKind.Artist, uri, uri, name, new EntityRef(EntityKind.Artist, ar.Slot), ArtUrl: image)))
        {
            Menu = () => Menus.Container(ActionTarget.ForArtist(ar.Uri, name), image, sub),
        };
        return Controls.Surface(data, s_shelfShape, w);
    }

    // ── about ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"about": the biography with the external links and the cities, and the profile facts as light numerals beside it (stacked under
    /// it where the tier has no room). A zero stat drops its whole tile; no facts ⇒ the biography takes the full width.</summary>
    static Element About(ArtistPageProps p, ZuneArtistLayout lay)
    {
        Artist a = p.Artist;
        string bio = ArtistVerbs.Text(a.BioId);
        LinkEdge[] links = a.Links.ToArray();
        CityEdge[] cities = a.TopCities.ToArray();
        long monthly = a.MonthlyListeners, followers = a.Followers;
        long albums = Artist.FacetTotal(a, DiscoFacet.Albums), singles = Artist.FacetTotal(a, DiscoFacet.Singles);
        long concerts = a.ConcertSlots.Length;

        var left = new List<Element>(3);
        if (bio.Length > 0)
            left.Add(Controls.RichText(bio, Rules.BioSize, Tok.TextSecondary, Tok.AccentTextPrimary, lay.AboutTextWidth(p.Width), Rules.BioLines, ArtistVerbs.OpenRoute));
        if (links.Length > 0) left.Add(LinkPills(links, lay.LinkTarget));
        if (cities.Length > 0) left.Add(CityBars(cities));
        Element text = new BoxEl
        {
            Key = "zune-artist-about:text", Direction = 1, Gap = Rules.AboutInnerGap, MinWidth = 0f,
            Grow = lay.AboutBeside ? Rules.BioShare : 0f, Basis = lay.AboutBeside ? 0f : float.NaN, Children = left.ToArray(),
        };

        Span<ArtistFact> facts = stackalloc ArtistFact[6];
        int n = ArtistSections.FactTiles(monthly, followers, albums, singles, concerts, p.RelatedCount, facts);
        var kids = new List<Element>(2) { text };
        if (n > 0)
        {
            var tiles = new Element[n];
            for (int i = 0; i < n; i++) tiles[i] = FactTile(Detail.Text.CompactCount(facts[i].Value), FactLabel(facts[i].Kind), facts[i].Kind);
            kids.Add(new BoxEl
            {
                Key = "zune-artist-about:facts", Direction = 0, Wrap = true, Gap = Rules.FactsGap, MinWidth = 0f,
                Grow = lay.AboutBeside ? 1f - Rules.BioShare : 0f, Basis = lay.AboutBeside ? 0f : float.NaN, Children = tiles,
            });
        }
        Element band = new BoxEl
        {
            Key = Rules.AboutKey, Direction = (byte)(lay.AboutBeside ? 0 : 1), Gap = lay.AboutBeside ? Rules.AboutGap : Rules.AboutInnerGap,
            AlignItems = lay.AboutBeside ? FlexAlign.Start : FlexAlign.Stretch, MinWidth = 0f, Children = kids.ToArray(),
        };
        return K.Section(K.Heading(Loc.Get(Strings.Theme.Zune.Artist.About), null, null), band);
    }

    static string FactLabel(ArtistFactKind kind) => kind switch
    {
        ArtistFactKind.Monthly => Loc.Get(Strings.Artist.Stat.Monthly),
        ArtistFactKind.Followers => Loc.Get(Strings.Artist.Stat.Followers),
        ArtistFactKind.Albums => Loc.Get(Strings.Artist.Stat.Albums),
        ArtistFactKind.Singles => Loc.Get(Strings.Artist.Stat.Singles),
        ArtistFactKind.Concerts => Loc.Get(Strings.Artist.Stat.Concerts),
        _ => Loc.Get(Strings.Artist.Stat.Related),
    };

    /// <summary>A profile fact: the figure in the large numeral voice over its lowercase label.</summary>
    static Element FactTile(string value, string label, ArtistFactKind kind) => new BoxEl
    {
        Key = "zune-artist-fact:" + ((int)kind).ToString(CultureInfo.InvariantCulture),
        Direction = 1, Gap = Rules.FactGap, Basis = Rules.FactBasis, Grow = 1f, MinWidth = 0f,
        Children = [R.Type.NumeralLarge(value) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, K.Caption(label)],
    };

    /// <summary>The external links: link words in the secondary ink (the primary on hover), each opening its page.</summary>
    static Element LinkPills(LinkEdge[] links, float target)
    {
        var words = new Element[links.Length];
        for (int i = 0; i < links.Length; i++)
        {
            string name = ArtistVerbs.Text(links[i].Name);
            string url = ArtistVerbs.Text(links[i].Url);
            words[i] = K.Link("zune-artist-link:" + i.ToString(CultureInfo.InvariantCulture), name, () => ArtistVerbs.OpenLink(url), target);
        }
        return new BoxEl { Direction = 0, Gap = Rules.HeadInlineGap, Wrap = true, MinWidth = 0f, Children = words };
    }

    /// <summary>"Listened to most in": each city a name and its listeners over a 4-dip accent bar proportional to listeners / max.</summary>
    static Element CityBars(CityEdge[] cities)
    {
        long max = 1;
        for (int i = 0; i < cities.Length; i++) if (cities[i].Listeners > max) max = cities[i].Listeners;
        var rows = new Element[cities.Length + 1];
        rows[0] = K.Caption(Loc.Get(Strings.Artist.ListenedMostIn));
        for (int i = 0; i < cities.Length; i++)
        {
            float frac = (float)((double)cities[i].Listeners / max);
            rows[i + 1] = new BoxEl
            {
                Direction = 1, Gap = Rules.CityGap, Height = Rules.CityRow,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            K.Name(ArtistVerbs.Text(cities[i].City)) with { Grow = 1f, Basis = 0f },
                            R.Type.DenseMeta(Detail.Text.CompactCount(cities[i].Listeners)) with { Color = Tok.TextSecondary },
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 0, Height = Rules.CityBar,
                        Children =
                        [
                            new BoxEl { Grow = MathF.Max(Rules.MinGrow, frac), Height = Rules.CityBar, Fill = Prop.Of(static () => R.Ink.AccentAction) },
                            new BoxEl { Grow = MathF.Max(Rules.MinGrow, 1f - frac), Height = Rules.CityBar },
                        ],
                    },
                ],
            };
        }
        return new BoxEl { Direction = 1, Gap = Rules.CityGap, MinWidth = 0f, Children = rows };
    }
}
