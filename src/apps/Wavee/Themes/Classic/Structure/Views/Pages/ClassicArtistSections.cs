// ── Themes/Classic/Structure/Views/Pages/ClassicArtistSections.cs — Classic's artist page sections (themes plan 6.1, T10b1) ─────
//
// From `Entities\Artist.Page.cs` §2.3 (the Top-tracks band), §2.6 (the shelves: appears on, music videos, playlists, concerts, merch, gallery, "Fans also like")
// and §2.7 (the biography / profile-facts band): the section BODIES under the hero. The section plan, the pivot's membership, the shelf caps and the
// biography's rules (`ArtistSections`) stay core; what a card opens, plays or puts in its menu is `ArtistVerbs` and the handles' own commands.
//
// A section is an accent-ruled header over its body — never a card (ch 08 §0 #11, Sections.cs:39-42) — except the biography band, the page's one exception.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>The page view's latched layout decisions: plain fields that outlive a render (the view's hook cell), exactly the host fields they replace.</summary>
internal sealed class ClassicArtistLatch
{
    /// <summary>The Top band's wide/stacked decision (760, held down to 736 once wide).</summary>
    public bool TopBandWide = true;

    /// <summary>The biography band's mode (the detail page's mode ladder) and whether it has decided once.</summary>
    public int BioMode;
    public bool BioModeInitialized;

    /// <summary>The structure's media-surface templates (T8c): the pick card's clickable shell is the surface's.</summary>
    public SurfaceStyle? Surface;

    /// <summary>The structure's chart templates (T8c): the page's skeleton draws the chart's own skeleton from them.</summary>
    public ArtistChartStyle? Chart;
}

/// <summary>Wavee Classic's artist page sections.</summary>
internal static class ClassicArtistSections
{
    /// <summary>A section is an accent-ruled header over its body — never a card (ch 08 §0 #11, Sections.cs:39-42). The profile page's sections draw the same shell.</summary>
    public static Element SectionBlock(string title, Element body, ColorF accent) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M, MinWidth = 0f,
        Children = [Controls.AccentHeader(title, accent), body],
    };

    /// <summary>One section's body, in the plan's order (ArtistPage.cs:234-269).</summary>
    public static Element Body(ArtistSection s, ArtistPageProps p, ClassicArtistLatch latch, SurfaceStyle? surface)
    {
        Artist a = p.Artist;
        ColorF accent = p.Accent;
        return s switch
        {
            ArtistSection.Popular => TopBand(p, latch, surface),
            ArtistSection.Upcoming => SectionBlock(Loc.Get(Strings.Artist.Upcoming), ClassicArtistBanners.UpcomingCard(a, p.AccentFn, wide: false), accent),
            ArtistSection.LatestRelease => SectionBlock(Loc.Get(Strings.Artist.LatestRelease), ClassicArtistBanners.LatestBanner(a.Latest, accent), accent),
            ArtistSection.Albums => Artist.FacetSection(a, DiscoFacet.Albums, p.AccentFn, Detail.BandLayout.Height, ArtistSections.FacetExpandedTopInset),
            ArtistSection.Singles => Artist.FacetSection(a, DiscoFacet.Singles, p.AccentFn, Detail.BandLayout.Height, ArtistSections.FacetExpandedTopInset),
            ArtistSection.Compilations => Artist.FacetSection(a, DiscoFacet.Compilations, p.AccentFn, Detail.BandLayout.Height, ArtistSections.FacetExpandedTopInset),
            ArtistSection.AppearsOn => AppearsOnShelf(a, accent),
            ArtistSection.Tour => ClassicArtistBanners.TourBanner(a, accent),
            ArtistSection.MusicVideos => VideosShelf(a, accent),
            ArtistSection.Playlists => PlaylistsShelf(a, accent),
            ArtistSection.Concerts => ConcertsShelf(a, accent),
            ArtistSection.Merch => MerchShelf(a, accent),
            ArtistSection.Biography => BiographyBand(p, latch, accent),
            ArtistSection.Gallery => GalleryShelf(p, accent),
            ArtistSection.Related => ArtistsShelf(a.RelatedSlots, accent),
            _ => ArtistsShelf(p.Fans, accent),
        };
    }

    // ── the Top-tracks band (TopTracks.cs:17-104) ────────────────────────────────────────────────────────────────────

    /// <summary>The chart beside ONE featured object — the pick, or (no pick) the upcoming card — at ≥ 760 (736 once
    /// wide), stacked below it otherwise. Decided at the 900-DIP pre-measure width on the first composed frame
    /// (parity 90); the featured child's key folds the arm (props freeze at mount).</summary>
    static Element TopBand(ArtistPageProps p, ClassicArtistLatch latch, SurfaceStyle? surface)
    {
        Artist a = p.Artist;
        bool pick = a.HasPick;
        bool upcomingInRail = ArtistSections.UpcomingInRail(pick, a.HasPreRelease && a.HasUpcoming);
        IReadSignal<ColorF> accentSignal = p.AccentSignal;
        Func<ColorF> accentFn = p.AccentFn;
        ColorF accent = p.Accent;
        IOverlayService? overlay = p.Overlay;
        return Responsive.Of(w =>
        {
            bool wide = latch.TopBandWide = ArtistSections.TopBandWide(w, latch.TopBandWide);
            Element tracks = Artist.Chart(a, accentSignal);
            if (!pick && !upcomingInRail) return new BoxEl { Direction = 1, MinWidth = 0f, Children = [tracks] };
            Element featured = pick
                ? ClassicArtistPick.Card(a, accentFn, horizontal: !wide, overlay, surface) with { Key = wide ? "featured:pick:rail" : "featured:pick:band" }
                : SectionBlock(Loc.Get(Strings.Artist.Upcoming), ClassicArtistBanners.UpcomingCard(a, accentFn, wide), accent)
                    with { Key = wide ? "featured:upcoming:rail" : "featured:upcoming:band" };
            return new BoxEl
            {
                Direction = (byte)(wide ? 0 : 1), Gap = Spacing.XL, MinWidth = 0f,
                AlignItems = wide ? FlexAlign.Start : FlexAlign.Stretch,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = wide ? 2f : 0f, Basis = wide ? 0f : float.NaN, MinWidth = 0f, Children = [tracks] },
                    new BoxEl { Direction = 1, Grow = wide ? 1f : 0f, Basis = wide ? 0f : float.NaN, MinWidth = 0f, Children = [featured] },
                ],
            };
        }, fallback: MathF.Max(1f, p.Width - 2f * p.Metrics.Gutter));
    }

    // ── the shelves (Shelves.cs, Discography.cs's appears-on; W19) ───────────────────────────────────────────────────

    /// <summary>One shelf card's identity + the data it paints that is not on the handle: a slot, its row version, an
    /// optional id (a playlist subtitle, a video thumb, a gallery image) and an int (a video's duration). A record, so the
    /// shelf's gate compares it by VALUE.</summary>
    readonly record struct ShelfEntity(int Slot, uint Version, StringId Sub, int Aux, string Key);

    /// <summary>A measured PagedShelf under an accent header: auto-fit cards between 150 and 200, gap 12, one row,
    /// edge fade 36, stock chevrons. The items are VALUE records (slot + version), so a publish that changes nothing
    /// a card paints rebuilds no card; a hydrated name bumps the version and rebuilds exactly that card.</summary>
    static Element ShelfOf(ShelfEntity[] items, Func<ShelfEntity, int, float, Element> cardAt, string title, ColorF accent,
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
                header: Controls.AccentHeader(title, accent), measured: false, keyOf: s_shelfKey,
                lift: ShelfLift.None)],   // the shared card hovers fill-only: no lift halo to reserve clearance for
        };

    static readonly Func<ShelfEntity, int, string> s_shelfKey = static (item, _) => item.Key;
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

    static Element AppearsOnShelf(Artist a, ColorF accent)
        => ShelfOf(Items(a.AppearsOnSlots, EntityKind.Album, ArtistSections.AppearsOnCap), AlbumCard,
                   Loc.Get(Strings.Artist.AppearsOn), accent, onInvoke: static (item, _) => OpenAlbum(item));

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

    static Element VideosShelf(Artist a, ColorF accent)
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
        return ShelfOf(items, VideoCard, Loc.Get(Strings.Artist.MusicVideos), accent, cardHeight: s_videoHeight,
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

    static Element PlaylistsShelf(Artist a, ColorF accent)
    {
        var slots = a.PlaylistSlots;
        var subtitles = a.PlaylistSubtitleIds;
        int n = Math.Min(slots.Length, ArtistSections.PlaylistCap);
        var items = new ShelfEntity[n];
        for (int i = 0; i < n; i++)
            items[i] = new ShelfEntity(slots[i], new Playlist(slots[i]).Version, i < subtitles.Length ? subtitles[i] : default, 0,
                slots[i].ToString(CultureInfo.InvariantCulture));
        return ShelfOf(items, PlaylistCard, Loc.Get(Strings.Artist.PlaylistsDiscovery), accent,
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

    static Element ConcertsShelf(Artist a, ColorF accent)
        => ShelfOf(Items(a.ConcertSlots, EntityKind.Concert, ArtistSections.ConcertCap), s_concertCard,
                   Loc.Get(Strings.Artist.UpcomingConcerts), accent);

    /// <summary>The concert stub is N-C's (ch 08 §9: Concert.UI.cs absorbs 0.2.9's ArtistPage.ConcertStub). No menu.</summary>
    static readonly Func<ShelfEntity, int, float, Element> s_concertCard = static (item, _, w) =>
    {
        var c = new Concert(item.Slot);
        return Concert.Stub(c, () => ArtistVerbs.OpenConcert(c), w);
    };

    static Element MerchShelf(Artist a, ColorF accent)
        => ShelfOf(Items(a.MerchSlots, EntityKind.Unknown, ArtistSections.MerchCap), s_merchCard, Loc.Get(Strings.Artist.Merch), accent);

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

    static Element GalleryShelf(ArtistPageProps p, ColorF accent)
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
        }, Loc.Get(Strings.Artist.Gallery), accent);
    }

    /// <summary>"Fans also like": circular cards with the subtitle "Artist" — the related artists, or the fallback
    /// pool of followed artists (the section keys differ, so the two never share a subtree).</summary>
    static Element ArtistsShelf(ReadOnlySpan<int> slots, ColorF accent)
        => ShelfOf(Items(slots, EntityKind.Artist, ArtistSections.FansCap), ArtistCard,
                   Loc.Get(Strings.Detail.FansAlsoLike), accent, onInvoke: static (item, _) => OpenArtist(item));

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

    // ── the biography band (Biography.cs, W18) ───────────────────────────────────────────────────────────────────────

    /// <summary>The biography card (Grow 2) beside the profile facts (Grow 1) at mode 0 of the detail page's mode
    /// ladder (≥ 820, re-entered at 844), stacked below. Two genuine object panels — the page's one exception to
    /// "sections are not cards". A zero stat drops its tile; no tiles ⇒ the biography takes the full width.</summary>
    static Element BiographyBand(ArtistPageProps p, ClassicArtistLatch latch, ColorF accent)
    {
        Artist a = p.Artist;
        string bio = ArtistVerbs.Text(a.BioId);
        LinkEdge[] links = a.Links.ToArray();
        CityEdge[] cities = a.TopCities.ToArray();
        long monthly = a.MonthlyListeners, followers = a.Followers;
        long albums = Artist.FacetTotal(a, DiscoFacet.Albums), singles = Artist.FacetTotal(a, DiscoFacet.Singles);
        long concerts = a.ConcertSlots.Length;
        int relatedCount = p.RelatedCount;
        return Responsive.Of(w =>
        {
            latch.BioMode = Detail.Breakpoints.ModeFor(w, latch.BioMode, latch.BioModeInitialized);
            latch.BioModeInitialized = true;
            bool wide = latch.BioMode == 0;

            int leftCount = 1 + (bio.Length > 0 ? 1 : 0) + (links.Length > 0 ? 1 : 0) + (cities.Length > 0 ? 1 : 0);
            var leftKids = new Element[leftCount];
            int k = 0;
            leftKids[k++] = Controls.AccentHeader(Loc.Get(Strings.Artist.Biography), accent);
            if (bio.Length > 0)
                leftKids[k++] = Controls.RichText(bio, ClassicArtistRules.BioFontSize, Tok.TextSecondary, Tok.AccentTextPrimary,
                    w * (wide ? ClassicArtistRules.BioWideShare : 1f) - ClassicArtistRules.BioTextInset, 14, ArtistVerbs.OpenRoute);
            if (links.Length > 0) leftKids[k++] = LinkPills(links);
            if (cities.Length > 0) leftKids[k] = CityBars(cities, accent);
            Element left = new BoxEl
            {
                Direction = 1, Gap = Spacing.L, Grow = wide ? 2f : 0f, Basis = wide ? 0f : float.NaN,
                MinWidth = 0f, ClipToBounds = true,
                Padding = new Edges4(Spacing.XL, Spacing.L, Spacing.XL, Spacing.L),
                Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children = leftKids,
            };

            Span<ArtistFact> facts = stackalloc ArtistFact[6];
            int n = ArtistSections.FactTiles(monthly, followers, albums, singles, concerts, relatedCount, facts);
            Element band;
            if (n == 0)
                band = new BoxEl { Key = "artist-biography:stacked", Direction = 1, Children = [left] };
            else
            {
                var tiles = new Element[n];
                for (int i = 0; i < n; i++) tiles[i] = FactTile(ClassicArtistHero.CountLabel(facts[i].Value), FactLabel(facts[i].Kind));
                Element right = new BoxEl
                {
                    Direction = 1, Gap = Spacing.M, Grow = wide ? 1f : 0f, Basis = wide ? 0f : float.NaN, MinWidth = 0f,
                    Children =
                    [
                        Controls.AccentHeader(Loc.Get(Strings.Artist.ProfileFacts), accent),
                        new BoxEl { Direction = 0, Gap = Spacing.M, Wrap = true, Children = tiles },
                    ],
                };
                band = new BoxEl
                {
                    // The arm is in the key (props freeze at mount); a KEYED CHILD, since a key on the box's own root
                    // output would be inert.
                    Key = wide ? "artist-biography:wide" : "artist-biography:stacked",
                    Direction = (byte)(wide ? 0 : 1), Gap = Spacing.XL,
                    AlignItems = wide ? FlexAlign.Start : FlexAlign.Stretch,
                    Children = [left, right],
                };
            }
            return new BoxEl { Direction = 1, MinWidth = 0f, Children = [band] };
        }, fallback: MathF.Max(1f, p.Width - 2f * p.Metrics.Gutter));
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

    /// <summary>The biography band's profile-facts tile (Biography.cs:125-134): Basis 140, pad 16, r8, the 28/36/600
    /// title rung over a caption. NOT <c>Controls.StatTile</c> — that is ch 02 W23's 18/800 album-facts tile, a
    /// different object; ch 08 W18 / parity 67 pin this one.</summary>
    static Element FactTile(string value, string label) => new BoxEl
    {
        Direction = 1, Gap = Spacing.XS, Grow = 1f, Basis = ClassicArtistRules.FactBasis, MinWidth = 0f,
        Padding = Edges4.All(Spacing.L),
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children = [Title(value) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, Caption(label)],
    };

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
                Direction = 1, Gap = ClassicArtistRules.CityGap, Height = ClassicArtistRules.CityRow,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl(ArtistVerbs.Text(cities[i].City))
                            {
                                Size = ClassicArtistRules.CityNameSize, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f,
                                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            },
                            R.Type.DenseMeta(ClassicArtistHero.CountLabel(cities[i].Listeners)) with { Color = Tok.TextSecondary },
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 0, Height = ClassicArtistRules.CityBar,
                        Children =
                        [
                            new BoxEl { Grow = MathF.Max(0.001f, frac), Height = ClassicArtistRules.CityBar, Corners = CornerRadius4.All(ClassicArtistRules.CityBar / 2f), Fill = accent },
                            new BoxEl { Grow = MathF.Max(0.001f, 1f - frac), Height = ClassicArtistRules.CityBar },
                        ],
                    },
                ],
            };
        }
        return new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows };
    }
}
