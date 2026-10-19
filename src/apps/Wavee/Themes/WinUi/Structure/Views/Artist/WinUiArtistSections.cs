// ── Themes/WinUi/Structure/Views/Artist/WinUiArtistSections.cs — WinUI's artist page sections (themes plan 6.2; P6 W6a) ─────────────────────────────
//
// The section BODIES under the hero (the plan, the pivot's membership, the shelf caps and the biography's rules are core, `ArtistSections`; what a card opens, plays or puts in
// its menu is `ArtistVerbs` and the handles' own commands). A section is a Subtitle (20/28) over its body, 12 apart, 32 from the next (W-Artist); the shelves are the shared
// media surface at the tier's fit; the discography facets are the core's grid and album drawer drawn by WinUI's discography style; About is the shared artist card with the
// links and the cities under it. The overview (Popular, the latest release, the discography preview, About and Fans at Huge) is `WinUiArtistOverview`.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>The page view's latched decisions and handles (the view's hook cell).</summary>
internal sealed class WinUiArtistLatch
{
    /// <summary>The overview's column count last decided (the hysteresis of `WinUiArtistTiers.OverviewColumns`).</summary>
    public int Columns;

    /// <summary>The structure's media-surface templates: the pick card's clickable shell is the surface's.</summary>
    public SurfaceStyle? Surface;

    Artist _menuArtist;
    Func<ContextMenuModel?>? _menu;

    /// <summary>The hero's More menu: the artist's container menu as its own page's hero offers it, built at open for the artist of the latest render.</summary>
    public Func<ContextMenuModel?> ArtistMenu(Artist a)
    {
        _menuArtist = a;
        return _menu ??= () =>
        {
            var artist = _menuArtist;
            return artist.IsValid
                ? Menus.Container(ActionTarget.ForArtist(artist.Uri, artist.Name), Controls.ArtUrl(artist.ImageId), null, new ContainerExtras { OnPage = true })
                : null;
        };
    }
}

/// <summary>WinUI's artist page sections.</summary>
internal static class WinUiArtistSections
{
    /// <summary>A section: its title in Subtitle (20/28) over its body, 12 apart; <paramref name="trailing"/> stands on the title's line (a link).</summary>
    public static Element SectionBlock(string title, Element body, Element? trailing = null) => new BoxEl
    {
        Direction = 1, Gap = WinUiArtistRules.BlockGap, MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                Children = trailing is null
                    ? [R.Type.RailHeader(title) with { Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
                    : [R.Type.RailHeader(title) with { Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, trailing],
            },
            body,
        ],
    };

    /// <summary>A section whose accent the view does not draw (the banners' shell).</summary>
    public static Element SectionBlock(string title, Element body, ColorF accent) => SectionBlock(title, body, (Element?)null);

    /// <summary>One section's body, in the plan's order (the overview's three and the facets are the page's).</summary>
    public static Element Body(ArtistSection s, ArtistPageProps p, WinUiShelfBand shelf)
    {
        Artist a = p.Artist;
        ColorF accent = p.Accent;
        return s switch
        {
            ArtistSection.Albums => Discography(a, DiscoFacet.Albums, p),
            ArtistSection.Singles => Discography(a, DiscoFacet.Singles, p),
            ArtistSection.Compilations => Discography(a, DiscoFacet.Compilations, p),
            ArtistSection.AppearsOn => AppearsOnShelf(a, accent, shelf),
            ArtistSection.Tour => WinUiArtistBanners.TourBanner(a, accent),
            ArtistSection.MusicVideos => VideosShelf(a, accent, shelf),
            ArtistSection.Playlists => PlaylistsShelf(a, accent, shelf),
            ArtistSection.Concerts => ConcertsShelf(a, accent, shelf),
            ArtistSection.Merch => MerchShelf(a, accent, shelf),
            ArtistSection.Biography => AboutBlock(p),
            ArtistSection.Gallery => GalleryShelf(p, accent, shelf),
            ArtistSection.Related => ArtistsShelf(a.RelatedSlots, accent, shelf),
            _ => ArtistsShelf(p.Fans, accent, shelf),
        };
    }

    /// <summary>One discography facet: the core's grid and album drawer under WinUI's header (the structure's discography style draws the header, the cards and the drawer).</summary>
    static Element Discography(Artist a, DiscoFacet facet, ArtistPageProps p)
        => Artist.FacetSection(a, facet, p.AccentFn, WinUiArtistRules.PivotClearance, ArtistSections.FacetExpandedTopInset);

    // ── the shelves ──────────────────────────────────────────────────────────────────────────────────────────────────

    // ── the shelves (Shelves.cs, Discography.cs's appears-on; W19) ───────────────────────────────────────────────────

    /// <summary>One shelf card's identity + the data it paints that is not on the handle: a slot, its row version, an
    /// optional id (a playlist subtitle, a video thumb, a gallery image) and an int (a video's duration). A record, so the
    /// shelf's gate compares it by VALUE.</summary>
    readonly record struct ShelfEntity(int Slot, uint Version, StringId Sub, int Aux, string Key);

    /// <summary>A measured PagedShelf under an accent header, at the tier's fit (<paramref name="band"/>: cards between 150 and 200, gap 12, stock chevrons;
    /// a 164 strip whose third card peeks at Compact; ten columns at Huge), one row, edge fade 36. The items are VALUE records (slot + version), so a publish that changes nothing
    /// a card paints rebuilds no card; a hydrated name bumps the version and rebuilds exactly that card.</summary>
    static Element ShelfOf(ShelfEntity[] items, Func<ShelfEntity, int, float, Element> cardAt, string title, ColorF accent, WinUiShelfBand band,
                           Func<float, float>? cardHeight = null, Action<ShelfEntity, int>? onInvoke = null)
        => new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            // Artist shelf cards are the shared surface's `s_shelfShape`: a fixed square art and label budget (the
            // videos shelf passes its own 16:9 extent). Supplying the shared height formula keeps the virtual shelf
            // from mounting a probe row of full cards (artwork, tooltips, playback overlays) just to discover a height
            // it already knows. The probe was a major source of late mount/layout bursts during navigation and could
            // make the page appear to flicker.
            // A shelf of SURFACE cards passes `onInvoke` (the slot root is then the one click/focus owner: one Tab stop
            // per card, Enter/Space invoke); a shelf whose cards own their click (concert stub, merch, gallery) omits it.
            Children = [PagedShelf.Create(items, cardAt, onInvoke: onInvoke, cardHeight: cardHeight ?? s_squareHeight,
                header: R.Type.RailHeader(title) with { MaxLines = 1, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis }, measured: false, keyOf: s_shelfKey,
                pager: band.Pager ? ShelfPager.Chevrons : ShelfPager.None, minCardW: band.MinCardW, maxCardW: band.MaxCardW, gap: band.Gap,
                fixedCardW: band.FixedCardW, maxColumns: band.MaxColumns,
                lift: ShelfLift.None) with { Key = ShelfKey }],   // the shared card hovers fill-only: no lift halo to reserve clearance for
        };

    static readonly Func<ShelfEntity, int, string> s_shelfKey = static (item, _) => item.Key;

    /// <summary>The shelf's ONE key: its fit (the strip, the column cap, the plain fit) and its pager are live props of the mounted PagedShelf, so a tier crossing
    /// re-fits the SAME shelf (its cards and its page survive) instead of remounting it.</summary>
    internal const string ShelfKey = "shelf";
    static readonly Func<float, float> s_squareHeight = static w => SurfaceGeometry.ShelfHeight(w);

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

    static Element AppearsOnShelf(Artist a, ColorF accent, WinUiShelfBand band)
        => ShelfOf(Items(a.AppearsOnSlots, EntityKind.Album, ArtistSections.AppearsOnCap), AlbumCard,
                   Loc.Get(Strings.Artist.AppearsOn), accent, band, onInvoke: static (item, _) => OpenAlbum(item));

    /// <summary>One album card for a slot at a cell width (the overview's discography preview).</summary>
    internal static Element AlbumCardAt(int slot, float w)
        => AlbumCard(new ShelfEntity(slot, new Album(slot).Version, default, 0, slot.ToString(CultureInfo.InvariantCulture)), 0, w);

    /// <summary>The album card's click, shared with the shelf slot's <c>onInvoke</c>.</summary>
    static void OpenAlbum(ShelfEntity item) => Track.GoToAlbum(new Album(item.Slot));

    /// <summary>Square cover, title, and the year — or the kind when the year is unknown (parity 56).</summary>
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

    /// <summary>The artist shelves' card: a square cover, a title and a subtitle capped at two lines
    /// (<see cref="CardSubtitle"/>) — exactly the extent <see cref="ShelfOf"/> hands the shelf
    /// (<c>SurfaceGeometry.ShelfHeight(w)</c> = <c>w + 66</c>).</summary>
    static readonly SurfaceShape s_shelfShape = Shape.Shelf(captionLines: 2);

    static Element VideosShelf(Artist a, ColorF accent, WinUiShelfBand band)
    {
        var slots = a.VideoSlots;
        var payload = a.VideoPayload;
        int n = Math.Min(slots.Length, ArtistSections.VideoCap);
        var items = new ShelfEntity[n];
        for (int i = 0; i < n; i++)
        {
            var v = i < payload.Length ? payload[i] : default;
            items[i] = new ShelfEntity(slots[i], new Track(slots[i]).Version, v.Thumb, v.DurationMs,
                slots[i].ToString(CultureInfo.InvariantCulture));
        }
        return ShelfOf(items, VideoCard, Loc.Get(Strings.Artist.MusicVideos), accent, band, cardHeight: s_videoHeight,
                       onInvoke: static (item, _) => PlayVideo(item));
    }

    /// <summary>The video card's click and ▶, shared with the shelf slot's <c>onInvoke</c>: it plays the track.</summary>
    static void PlayVideo(ShelfEntity item) => ArtistVerbs.Play(new Track(item.Slot).Id);

    /// <summary>The 16:9 music-video card: the shared surface's <see cref="Shape.Video"/> (the FAB at rest, the real
    /// now-playing overlay, the corner "…", the hover plate, the hand cursor) over the thumb at 16:9 and the duration
    /// as its caption. It stands for its TRACK — the menu is the track's, the drag carries the track, a click plays it.</summary>
    static Element VideoCard(ShelfEntity item, int index, float w)
    {
        var t = new Track(item.Slot);
        string uri = t.Uri.Text;
        string title = t.Title;
        string? thumb = Controls.ArtUrl(item.Sub);
        Action play = () => PlayVideo(item);
        var data = new Controls.CardData(uri, title, null, thumb,
            OnClick: play,
            OnPlay: play,
            Drag: Drag.Source(() => new DragPayload(DragKind.Track, uri, uri, title,
                new EntityRef(EntityKind.Track, t.Slot), Tracks: [t], ArtUrl: thumb)))
        {
            CoverAspect = VideoAspect,
            Caption = item.Aux > 0 ? Track.Format.TrackTime(item.Aux) : null,
            Menu = () => Track.Menu([t], in s_trackMenu),
        };
        return Controls.Surface(data, Shape.Video, w);
    }

    /// <summary>A video thumb's width ÷ height, and the shelf's extent for it: <c>ShelfHeight</c> at that aspect with
    /// the video shape's one caption line (the duration) — the renderer and this estimator both read
    /// <see cref="SurfaceGeometry"/>, so the virtual shelf never mounts a probe row.</summary>
    const float VideoAspect = 16f / 9f;
    static readonly Func<float, float> s_videoHeight = static w => SurfaceGeometry.StackExtent(Shape.Video, w, VideoAspect);

    static readonly Track.MenuOptions s_trackMenu = new(ShowGoToAlbum: true);

    static Element PlaylistsShelf(Artist a, ColorF accent, WinUiShelfBand band)
    {
        var slots = a.PlaylistSlots;
        var subtitles = a.PlaylistSubtitleIds;
        int n = Math.Min(slots.Length, ArtistSections.PlaylistCap);
        var items = new ShelfEntity[n];
        for (int i = 0; i < n; i++)
            items[i] = new ShelfEntity(slots[i], new Playlist(slots[i]).Version, i < subtitles.Length ? subtitles[i] : default, 0,
                slots[i].ToString(CultureInfo.InvariantCulture));
        return ShelfOf(items, PlaylistCard, Loc.Get(Strings.Artist.PlaylistsDiscovery), accent, band,
                       onInvoke: static (item, _) => OpenPlaylist(item));
    }

    /// <summary>The playlist card's click, shared with the shelf slot's <c>onInvoke</c>.</summary>
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

    static Element ConcertsShelf(Artist a, ColorF accent, WinUiShelfBand band)
        => ShelfOf(Items(a.ConcertSlots, EntityKind.Concert, ArtistSections.ConcertCap), s_concertCard,
                   Loc.Get(Strings.Artist.UpcomingConcerts), accent, band);

    /// <summary>The concert stub is the concert cells' (`WinUiArtistBanners.Stub`, T10b3; ch 08 §9: 0.2.9's ArtistPage.ConcertStub). No menu.</summary>
    static readonly Func<ShelfEntity, int, float, Element> s_concertCard = static (item, _, w) =>
    {
        var c = new Concert(item.Slot);
        return WinUiArtistBanners.Stub(c, () => ArtistVerbs.OpenConcert(c), w);
    };

    static Element MerchShelf(Artist a, ColorF accent, WinUiShelfBand band)
        => ShelfOf(Items(a.MerchSlots, EntityKind.Unknown, ArtistSections.MerchCap), s_merchCard, Loc.Get(Strings.Artist.Merch), accent, band);

    /// <summary>Square art, a 2-line name, the price in accent ink (Shelves.cs:139-165). 0.2.9's card was the one inert
    /// card on the page (parity 91): 0.3 opens the shop link when the row carries one.</summary>
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
            Direction = 1, Gap = Spacing.S, Width = w, Shrink = 0f, ClipToBounds = true,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault,
            HoverScale = R.Motion.ScaleStandard.Hover,
            Role = link ? AutomationRole.Button : AutomationRole.None, Focusable = link,
            FocusVisualMargin = Design.FocusInsetBordered,
            Cursor = link ? CursorId.Hand : null,
            OnClick = link ? () => ArtistVerbs.OpenLink(shop) : null,
            Children =
            [
                new BoxEl
                {
                    ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Control),
                    Children = [Controls.ArtworkFill(image, Radii.Control)],
                },
                R.Type.DenseTitle(name) with
                {
                    Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2,
                    Trim = TextTrim.CharacterEllipsis,
                },
                R.Type.DenseTitle(price) with { Weight = 700, Color = Tok.AccentTextPrimary, MaxLines = 1 },
            ],
        };
    };

    static Element GalleryShelf(ArtistPageProps p, ColorF accent, WinUiShelfBand band)
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
            Width = w, Height = w, Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered,
            Cursor = CursorId.Hand,
            HoverScale = R.Motion.ScaleStandard.Hover, PressScale = R.Motion.ScaleStandard.Press,
            OnClick = () => open(urls, index),
            Children = [Controls.Artwork(urls[index], w, w, Radii.Card, decodePx: 480)],
        }, Loc.Get(Strings.Artist.Gallery), accent, band);
    }

    /// <summary>"Fans also like": circular cards with the subtitle "Artist" — the related artists, or the fallback
    /// pool of followed artists (the section keys differ, so the two never share a subtree).</summary>
    static Element ArtistsShelf(ReadOnlySpan<int> slots, ColorF accent, WinUiShelfBand band)
        => ShelfOf(Items(slots, EntityKind.Artist, ArtistSections.FansCap), ArtistCard,
                   Loc.Get(Strings.Detail.FansAlsoLike), accent, band, onInvoke: static (item, _) => OpenArtist(item));

    /// <summary>The artist card's click, shared with the shelf slot's <c>onInvoke</c>.</summary>
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

    static Element CardSubtitle(string text) => R.Type.TrackMeta(text) with
    {
        MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    // ── the card menu (W27) ──
    // Every card on the page (album, playlist, artist, video) carries its menu in the surface's data (`CardData.Menu`);
    // the host attaches the right-click funnel and the corner "…". The container cards' menu is Menus.Container.


    // ── about ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>About: the shared artist card (the image band, the name beside Follow, the monthly listeners, a four-line bio), then the external links and the cities.</summary>
    static Element AboutBlock(ArtistPageProps p)
    {
        Artist a = p.Artist;
        LinkEdge[] links = a.Links.ToArray();
        CityEdge[] cities = a.TopCities.ToArray();
        var kids = new List<Element>(3)
        {
            Controls.ArtistAboutCard(a, AboutLayout.Aside, WinUiArtistRules.AboutBand) with { Key = "about:" + a.Uri.Text },
        };
        if (links.Length > 0) kids.Add(LinkPills(links));
        if (cities.Length > 0) kids.Add(CityBars(cities, p.Accent));
        return SectionBlock(Loc.Get(Strings.Theme.Winui.Detail.About), new BoxEl
        {
            Direction = 1, Gap = Spacing.L, MinWidth = 0f, MaxWidth = WinUiArtistRules.AboutMax, Children = kids.ToArray(),
        });
    }

    /// <summary>The external links (Workstream B: the "social links" row is the grammar's LINK role — a stock
    /// <c>HyperlinkButton</c>, not a bordered pill). 0.2.9's were boxes with NO click at all (parity 92); they
    /// open now.</summary>
    static Element LinkPills(LinkEdge[] links)
    {
        var pills = new Element[links.Length];
        for (int i = 0; i < links.Length; i++)
        {
            string name = ArtistVerbs.Text(links[i].Name);
            string url = ArtistVerbs.Text(links[i].Url);
            pills[i] = HyperlinkButton.Create(name, () => ArtistVerbs.OpenLink(url));
        }
        return new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, Children = pills };
    }

    /// <summary>"Listened to most in": each city 34 tall, a 4-DIP accent bar proportional to listeners / max.</summary>
    static Element CityBars(CityEdge[] cities, ColorF accent)
    {
        long max = 1;
        for (int i = 0; i < cities.Length; i++) if (cities[i].Listeners > max) max = cities[i].Listeners;
        var rows = new Element[cities.Length + 1];
        rows[0] = R.Type.Eyebrow(Loc.Get(Strings.Artist.ListenedMostIn)) with { Color = Tok.TextTertiary };
        for (int i = 0; i < cities.Length; i++)
        {
            float frac = (float)((double)cities[i].Listeners / max);
            rows[i + 1] = new BoxEl
            {
                Direction = 1, Gap = WinUiArtistRules.CityGap, Height = WinUiArtistRules.CityRow,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl(ArtistVerbs.Text(cities[i].City))
                            {
                                Size = WinUiArtistRules.CityNameSize, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f,
                                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            },
                            R.Type.DenseMeta(CountLabel(cities[i].Listeners)) with { Color = Tok.TextSecondary },
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 0, Height = WinUiArtistRules.CityBar,
                        Children =
                        [
                            new BoxEl { Grow = MathF.Max(0.001f, frac), Height = WinUiArtistRules.CityBar, Corners = CornerRadius4.All(WinUiArtistRules.CityBar / 2f), Fill = accent },
                            new BoxEl { Grow = MathF.Max(0.001f, 1f - frac), Height = WinUiArtistRules.CityBar },
                        ],
                    },
                ],
            };
        }
        return new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows };
    }

    /// <summary>A count in the compact form ("20.6M").</summary>
    internal static string CountLabel(long n) => n >= 1_000_000L ? (n / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + "M"
        : n >= 1_000L ? (n / 1_000d).ToString("0.#", CultureInfo.CurrentCulture) + "K" : n.ToString(CultureInfo.CurrentCulture);
}
