using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Concerts;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The artist page (docs/plans/wavee/architecture.md §2 "Album & artist") — WaveeMusic's full magazine surface.
// This partial owns route-reactive loading and composes the hero with the magazine sections.
sealed partial class ArtistPage : Component
{
    readonly Signal<Route> _route;
    // Identity for race-free last-writer-wins on ShellMaterial, and it survives KeepAlive park/reactivate (which is
    // what the owner check in CoverShellTintBinder's clear path needs). NOT a cross-artist token: ContentHost's
    // SlotKey is tab + route.Name + route.Arg, so artist→artist mounts a NEW ArtistPage with a new owner — the
    // outgoing page clears its own tint on unmount and the incoming one publishes its own.
    readonly object _tintOwner = new();
    // The context band's scroll-spy registry: the page's scroll viewport + one node per pivot-visible section. Written
    // from OnRealized, read by ContextPivot's scroll effect — never an ancestor relationship, so the band can live
    // inside the pinned hero while the sections live in the magazine body.
    readonly SectionAnchors _anchors = new();
    // The route the registry above currently holds nodes FOR. Compared (and the registry dropped) inside Render — see
    // the reset note there for why this edge cannot be an effect.
    string? _anchorsRoute;
    // Cover-extracted page CHROME accent for accent-filled controls; null keeps the semantic default live.
    // WASH accent lives in CoverPaletteLeaves (veil / blend wash) — not a page field.
    ColorF? _paletteAccent;
    ColorF _accent => _paletteAccent ?? Tok.AccentDefault;
    ActionServices? _acts;                // shelf-card context menus — resolved per-render, read by the shelf builders
    IOverlayService? _menuOverlay;
    public ArtistPage(Signal<Route> route) { _route = route; }

    /// <summary>The lazy card-menu attach for this page's shelves (albums / playlists / artists / video tracks — the
    /// model is inferred from the uri by Menus.Card). Null when the action system / overlay isn't provided (fake shell).</summary>
    MenuAttach? CardMenu(string uri, string name, Image? image = null, string? subtitle = null, bool circular = false)
        => _menuOverlay is { } ov ? Menus.CardAttach(_acts, ov, uri, name, image, subtitle, circular) : null;

    /// <summary>The drag source for this page's shelf cards. Like <see cref="CardMenu"/> it reads <c>_acts</c> at
    /// PROMOTION (the payload factory is cold), so a page whose services arrive after the first render still drags.
    /// <paramref name="kind"/> is explicit where the shelf knows it and derived from the uri where it does not.</summary>
    DragSource CardDrag(WaveeResourceKind kind, string uri, string name, Image? cover)
        => Drag.Source(WaveeDragKinds.Resource,
            () => WaveeResourceDragPayload.ForEntity(kind, uri, name, cover, _acts));

    internal static string? UriOf(Route r) =>
        r.Name.StartsWith("artist:", StringComparison.Ordinal) ? r.Name["artist:".Length..] : null;

    internal static string? PaletteImageUrl(Artist a)
        => a.HeaderImage?.Url is { Length: > 0 } hero ? hero : a.Image?.Url;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        _ = AppearancePrefs.Epoch.Value;
        bool colorWashesDisabled = svc?.Settings.Get(WaveeSettings.DisableColorWashes) ?? false;
        var go = UseContext(HistoryStore.NavCtx);
        var bridge = UseContext(PlaybackBridge.Slot);
        var store = UseContext(LibraryStore.Slot);
        var shellMaterial = UseContext(ShellMaterial.Slot);
        _acts = UseContext(ActionServices.Slot);          // shelf-card context menus (Menus.CardAttach)
        _menuOverlay = UseContext(Overlay.Service);
        if (svc is null || store is null) return new BoxEl { Grow = 1f };

        var route = _route.Value;                       // subscribe → re-derive when this slot's route object changes
        string routeKey = route.Name;
        string uri = UriOf(route) ?? "";

        // ContentHost keeps one ArtistPage alive for artist→artist hops. The data is cached per artist, while the
        // scroll/skeleton subtree below is keyed by route so pending/ready branches and child components remount cleanly.
        // ONE read at the RICH rung: identity + assembled discography (Open) plus the queryArtistOverview facets the
        // header shows — monthly listeners, followers, world rank, related. Two calls and two services collapsed into
        // one ask because the rung IS the contract (design §1.2); offline the ladder simply cannot reach Rich and the
        // resident V4 artist stands, exactly as the old null-stats fallback did.
        var artist = store.ArtistDetail(uri, ct => svc.Library.GetArtistAsync(uri, HydrationLevel.Rich, ct)!,
            PendingArtist(uri));
        store.EnsureArtists();
        var fansList = store.Artists.Value.Value;

        // ContentHost's SlotKey carries the ROUTE (tab + name + arg), so artist→artist mounts a fresh ArtistPage and
        // slides it in; KeepAlive parks the previous one so going back restores its scroll and its loaded data. There
        // is no cross-artist instance reuse to defend against here — but the shell tint + wash Watch subscriptions
        // still live in cover-keyed leaves (CoverPaletteLeaves), never in this Render, so a graded batch does not
        // rebuild the magazine tree. HeaderImage is the dominant visual; fall back to the avatar only when there is no
        // hero image.
        bool artistReady = artist.State.Value == (byte)LoadState.Ready;
        var currentArtist = artist.Value.Value;
        string? paletteUrl = PaletteImageUrl(currentArtist);
        // The shell material tint is published from a leaf: the Watch subscription, the derived tone and the
        // owner-gated set/clear effects all live inside CoverPaletteLeaves.ShellTint, so a graded batch repaints one
        // zero-size node instead of rebuilding the magazine tree. Flat arm only (Wash: null) — the three-layer radial
        // wash belongs to Home.
        Element tintBinder = CoverPaletteLeaves.ShellTint(
            paletteUrl, artistReady, colorWashesDisabled, apply: true, _tintOwner, shellMaterial,
            key: "artist-tint:" + routeKey);

        // A new artist is a new set of sections in a REUSED slot, so the band's registry must drop the old nodes. That
        // edge is taken HERE, in Render, and NOT in the effect below — the ordering is the whole point:
        // OnRealized fires during the RECONCILE of this render (Reconciler.Realize), while UseEffect bodies run after
        // PRESENT. A reset in the effect therefore wiped the registrations the same frame's realization had just made,
        // and nothing re-registers afterwards (a node realizes once). The band was left with a null viewport — the spy
        // bailed at its `vp.IsNull` guard and the active index stayed 0 forever, while every pivot click resolved a
        // null node and did nothing. Resetting on the render that changes identity runs BEFORE the new nodes realize,
        // which is the only correct edge for a registry the reconciler fills.
        if (!string.Equals(_anchorsRoute, routeKey, StringComparison.Ordinal))
        {
            _anchors.Reset();
            _anchorsRoute = routeKey;
        }

        var compactInteractive = UseSignal(false);
        var pageScroll = UseSignal(0f);   // live page scroll offset → published so the in-page virtualized discography grids window against it
        var pageViewportHeight = UseSignal(0f);   // live height → the context spy's upper-quarter line
        var pageAtEnd = UseSignal(false);         // real lower limit → the final short section can still become current
        UseEffect(() =>
        {
            compactInteractive.Value = false;
            pageScroll.Value = 0f;
            pageViewportHeight.Value = 0f;
            pageAtEnd.Value = false;
        }, routeKey);
        // One tree: the boundary renders Body with the resource's pending value, derives its loading paint, then fills
        // the same Body with the loaded artist. The page does not author or pass a separate skeleton subtree.
        var scroll = ScrollView(Skel.Region(artist,
            content: a => Body(a, fansList, svc, go, bridge, compactInteractive, pageScroll, pageViewportHeight, pageAtEnd),
            onFailed: () => ErrorState.Build(artist.Error),
            reveal: SkelReveal.FadeOnly,
            group: routeKey)
            with
            {
                Key = "artist-region:" + routeKey,
            })
            with
            {
                // Scroll-position restoration keyed by the artist (route). One ScrollView serves successive artists in place,
                // so without a key artist B would inherit A's scroll; with it, B starts at the top and a revisit to A restores it.
                Key = "artist-scroll:" + routeKey, Grow = 1f, ScrollKey = routeKey,
                // The band's pivot resolves "which section am I in" against THIS viewport, and scrolls THIS viewport
                // on a click — an explicit handle, so a nested scroller deeper in the magazine can never be the thing
                // that moves.
                OnRealized = h => _anchors.Viewport = h,
                // No colour edge cue: the default cue resolved its surface by ANCESTOR walk, which sails past the opaque
                // content pane (a ZStack sibling) to the untinted ShellGround — a neutral one-rung-darker band painted
                // OVER the pinned compact bar. The shy header itself is the occlusion cue on this page.
                EdgeCues = ScrollEdgeCues.None,
                // Publish the live offset (24px write-throttle floor; LazyGrid windowing is per-row inside the control).
                OnScrollGeometryChanged =
                (
                    g => HashCode.Combine(
                        (int)(g.OffsetY / 24f),
                        (int)MathF.Round(g.ViewportH / Spacing.XS),
                        ContextBandLayout.IsAtScrollEnd(g.OffsetY, g.ViewportH, g.ContentH)),
                    g =>
                    {
                        pageScroll.Value = g.OffsetY;
                        pageViewportHeight.SetIfChanged(g.ViewportH);
                        pageAtEnd.SetIfChanged(ContextBandLayout.IsAtScrollEnd(g.OffsetY, g.ViewportH, g.ContentH));
                    }
                ),
            };

        // Provide the page scroll to the discography LazyGrids deeper in the body (the SwiftUI LazyVGrid-in-ScrollView wiring).
        return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)pageScroll, new BoxEl
        {
            Key = "artist-page:" + routeKey, Grow = 1f, Direction = 1,
            Children = [tintBinder, scroll],
        });
    }

    static Artist PendingArtist(string uri)
    {
        // Async resources need a value while Pending because Body is data-driven (sections are conditional). Reuse a
        // route-stable catalog shape so the one real Body has representative hero/tracks/releases/biography geometry.
        // The engine replaces all text/media paint, so none of this sample content is shown to the user.
        var shape = FakeData.Artist(FakeData.IndexFromUri(uri));
        return shape with { Id = "", Uri = uri, Name = "" };
    }

    Element Body(Artist a, IReadOnlyList<Artist> fansAll, Services svc, Action<string, string?> go,
                 PlaybackBridge? bridge, Signal<bool> compactInteractive, IReadSignal<float> pageScroll,
                 IReadSignal<float> pageViewportHeight, IReadSignal<bool> pageAtEnd)
    {
        string uri = a.Uri;
        // Cover-extracted chrome accent (null ⇒ semantic default). Wash/veil accents are owned by CoverPaletteLeaves.
        string? paletteUrl = PaletteImageUrl(a);
        var chromePal = Surfaces.ChromeSchemeFor(paletteUrl);
        _paletteAccent = chromePal is { } pal ? WaveePalette.ChromeAccent(pal) : null;
        Func<ColorF> accent = () => _accent;
        var extras = a.Extras;
        var popular = a.TopTracks is { Count: > 0 } tt ? tt : FakeData.TopTracksOf(a);
        var albumsAll = a.TopAlbums ?? Array.Empty<Album>();
        var albums = albumsAll.Where(al => al.Kind == AlbumKind.Album).ToArray();
        var singles = albumsAll.Where(al => al.Kind is AlbumKind.Single or AlbumKind.EP).ToArray();
        var compilations = albumsAll.Where(al => al.Kind == AlbumKind.Compilation).ToArray();
        var fans = fansAll.Where(f => f.Uri != uri).Take(12).ToArray();

        void Play() => _ = svc.Player.PlayAsync(uri, 0);
        void Shuffle() { _ = svc.Player.SetShuffleAsync(true); _ = svc.Player.PlayAsync(uri, 0); }
        void PlayContext(string u) => _ = svc.Player.PlayAsync(u, 0);
        // The hero "Artist Radio" pill: seed a real radio off the artist (Apple-Music-style, never interrupting) + toast
        // — NOT a plain replay of the artist context (the previous bug passed Play as the radio callback).
        void Radio() => RadioLaunch.Start(svc.Player, uri, a.Name, go);

        // EVERY section carries a stable Key. This list is built CONDITIONALLY off a loadable: extras/top-albums arrive
        // after the first Ready render, so sections appear mid-stream and shift every later sibling's index. Keyless
        // children pair by raw list index + ElementTypeId, and all but the two DiscographySection entries are BoxEl —
        // one identical type — so an inserted section made each following one pair against its NEIGHBOUR's old subtree:
        // silent cross-wiring of shelves plus a remount cascade of their cards (the LazyNowPlayingOverlay/CoverShimmer
        // mount mass on artist loads). Keyed children pair by key regardless of position, so an insert now costs exactly
        // the inserted section. Keys must stay UNIQUE (a duplicate silently mounts a second node) and must stay stable
        // across renders — note "related" and "fans" are deliberately DISTINCT keys: they are alternatives holding
        // different data, and reusing one key would reuse the shelf subtree across the swap.
        var sections = new List<Element>(15);
        // The pivot is BUILT FROM the section list, in the same pass, so the two can never disagree about what the
        // page renders. A section joins the pivot only when it is a DESTINATION — a place a visitor would aim at.
        // The two banners (latest release, tour) are announcements sitting between destinations and are deliberately
        // excluded: naming them in the pivot would advertise a jump to a 90-DIP card.
        var pivot = new List<ContextPivotItem>(14);
        void Sec(string key, string? pivotLabel, Element body)
        {
            if (pivotLabel is null) { sections.Add(body with { Key = "sec:" + key }); return; }
            pivot.Add(new ContextPivotItem(key, pivotLabel));
            sections.Add(ContextBand.Anchor(_anchors, key, body with { Key = "sec:" + key }));
        }
        if (popular.Count > 0)
            Sec("popular", Loc.Get(Strings.Artist.TopTracks),
                TopBand(popular, uri, bridge, svc, a.Pinned, a.Image, a.HeaderImage, a.Name, extras?.PreRelease, go, PlayContext, accent));
        // The upcoming announcement has exactly ONE home. When there is no pick it is the featured column beside
        // Top tracks (FeaturedColumn). When the pick owns that column, it moves HERE — a quiet full-width band above
        // Latest release (the same slot grammar), never a second card stacked under the pick: that stack made the
        // rail out-run Top tracks and left a dead band beside them.
        if (a.Pinned is not null && popular.Count > 0 && extras?.PreRelease is { IsUpcoming: true } upNext)
            Sec("upcoming", null,
                Section(Loc.Get(Strings.Artist.Upcoming), UpcomingCard(upNext, a.Name, wide: false, go, accent)));
        // The "just dropped" banner earns full-band prominence directly above Albums — not a narrow rail card
        // sharing a column with Artist Pick/Upcoming (see ArtistPage.TopTracks.LatestReleaseBanner).
        if (a.LatestRelease is { Name.Length: > 0, Uri.Length: > 0 } latestRelease)
            Sec("latest-release", null,
                Section(Loc.Get(Strings.Artist.LatestRelease), LatestReleaseBanner(latestRelease, go, PlayContext, accent)));
        // Owned discography stays inline as full virtualized facets; dedicated pages remain deep-link compatible only.
        if (albums.Length > 0 || a.AlbumsTotal > 0) Sec("albums", Loc.Get(Strings.Artist.Albums), Embed.Comp(
            new DiscographySection.Props(albums),
            () => new DiscographySection(DiscographyKind.Albums, Loc.Get(Strings.Artist.Albums), svc, go, PlayContext, accent)));
        if (singles.Length > 0 || a.SinglesTotal > 0) Sec("singles", Loc.Get(Strings.Artist.SinglesEps), Embed.Comp(
            new DiscographySection.Props(singles),
            () => new DiscographySection(DiscographyKind.Singles, Loc.Get(Strings.Artist.SinglesEps), svc, go, PlayContext, accent)));
        if (compilations.Length > 0 || a.CompilationsTotal > 0) Sec("compilations", Loc.Get(Strings.Artist.Compilations), Embed.Comp(
            new DiscographySection.Props(compilations),
            () => new DiscographySection(DiscographyKind.Compilations, Loc.Get(Strings.Artist.Compilations), svc, go, PlayContext, accent)));
        if (a.AppearsOn is { Count: > 0 } appears) Sec("appears-on", Loc.Get(Strings.Artist.AppearsOn), AppearsOnShelf(appears, go, PlayContext));
        if (extras?.Tour is { } tour) Sec("tour", null, TourBannerCard(tour,
            () => go(ConcertRoutes.ArtistSchedule(uri), a.Name)));
        if (extras?.MusicVideos is { Count: > 0 } videos) Sec("music-videos", Loc.Get(Strings.Artist.MusicVideos), MusicVideosShelf(videos, PlayContext));
        if (extras?.Playlists is { Count: > 0 } playlists) Sec("playlists", Loc.Get(Strings.Artist.PlaylistsDiscovery), PlaylistsShelf(playlists, go, PlayContext));
        if (extras?.Concerts is { Count: > 0 } concerts) Sec("concerts", Loc.Get(Strings.Artist.UpcomingConcerts), ConcertsRow(concerts, go));
        if (extras?.Merch is { Count: > 0 } merch) Sec("merch", Loc.Get(Strings.Artist.Merch), MerchRow(merch));
        Sec("biography", Loc.Get(Strings.Artist.Biography), BiographyBand(a, albums.Length, singles.Length, extras, fans.Length, go));
        if (extras?.Gallery is { Count: > 0 } gallery) Sec("gallery", Loc.Get(Strings.Artist.Gallery), GalleryStrip(gallery));
        if (extras?.Related is { Count: > 0 } related) Sec("related", Loc.Get(Strings.Detail.FansAlsoLike), RelatedShelf(related, go, PlayContext));
        else if (fans.Length > 0) Sec("fans", Loc.Get(Strings.Detail.FansAlsoLike), FansShelf(fans, go, PlayContext));

        var inner = new BoxEl
        {
            // W1a-alias: WaveeSize.SectionGap — the ONE page-section gap (32). Was Spacing.XL (20), which made this page's
            // sections sit closer together than every other page's for no stated reason.
            Direction = 1, Gap = WaveeSize.SectionGap,
            // DetailShell's clamp idiom ("detail:two-column"): Grow toward the wrapper row's free width, capped at 1600,
            // with the Justify=Center wrapper below centring the capped block. MaxWidth+AlignSelf.Center is NOT enough —
            // a non-Stretch child arranges at its MEASURED width, so the fluid Grow/Basis=0 sections would under-fill
            // and a wider-than-window fixed shelf would overflow both gutters.
            // The gutter is ArtistHeroLayout.PageGutter — the SAME inset the hero copy uses, so the two line up.
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, MaxWidth = 1600f,
            // Top pad kept tight so Top tracks sits close under the hero Play/Follow row (was 40).
            Padding = new Edges4(ArtistHeroLayout.PageGutterFor(_heroWidth.Value), Spacing.M, ArtistHeroLayout.PageGutterFor(_heroWidth.Value), PlayerDock.Reserve + 40f),
            Children = sections.ToArray(),
        };
        // One edge-only handoff: as the full hero reaches its 56-DIP floor, the compact presentation takes input.
        // It stays inside the already-pinned hero; this sentinel paints nothing and never becomes a second chrome layer.
        var sentinel = new BoxEl
        {
            Height = 0f, HitTestVisible = false,
        }.Sticky(ArtistHeroLayout.CompactIdentityHeight, v => compactInteractive.Value = v);
        float heroWidth = _heroWidth.Value;
        bool colorWashesDisabled = svc.Settings.Get(WaveeSettings.DisableColorWashes);
        // Cover-keyed leaf: a late grading re-renders only the wash box, not this Body / magazine sections.
        Element washLayer = CoverPaletteLeaves.ArtistBlendWash(
            paletteUrl, heroWidth, colorWashesDisabled, key: "artist-wash:" + uri);

        // ── THE BAND'S CLIP (the offset model — see ContextBand) ──────────────────────────────────────────────────
        // The pinned band paints nothing, so nothing may render into its 56 DIP. Everything that scrolls UNDER it is
        // cut at that line by the engine's sticky clip, and the page's real ground (the shell's live Mica smoke, which
        // no constant could ever have matched) shows through.
        //
        // TWO nodes carry it, because two independent things scroll under the band: the magazine column, and the blend
        // WASH — which lives in this ZStack behind everything and would otherwise slide its strongest (top) alpha
        // straight through the band. Both binds are self-gating: ClipTopAtViewport releases to RectF.Infinite while
        // the line is at or above the node's top, so at scroll 0 neither clips anything, and engaging is a paint write
        // rather than a re-render. The magazine's leg also feathers its cut so content dissolves into the band instead
        // of being guillotined by it; the wash is a soft gradient and needs no feather.
        //
        // The feather's gate is `compactInteractive` — NOT a second signal. That flag is the sentinel's PinTop(56)
        // edge, and the sentinel sits at exactly the magazine's top, so it flips on precisely the frame the clip
        // engages. Reusing it means the feather costs no additional page re-render: this Render already reads it.
        Element magazine = new BoxEl
        {
            Key = "artist-under-band",
            Direction = 1,
            // The feather is measured off the VISIBLE boundary (the sticky ClipRect), not the element box — see
            // SceneRecorder's EdgeFade note — so it rides exactly on the cut. Mounted only while engaged: an
            // always-on top fade would feather the divider under the hero at rest.
            EdgeFade = compactInteractive.Value
                ? new EdgeFadeSpec(EdgeMask.Top, ContextBand.ClipFadeBand)
                : null,
            Children =
            [
                new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false },
                new BoxEl { Direction = 0, Justify = FlexJustify.Center, Children = [inner] },
            ],
        }.ClipBelow(ContextBand.ClipInset);
        return new BoxEl
        {
            ZStack = true,
            Children =
            [
                new BoxEl
                {
                    Key = "artist-wash-clip", Direction = 1, HitTestVisible = false,
                    Children = [washLayer],
                }.ClipBelow(ContextBand.ClipInset),
                new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        Banner(a, uri, Play, Shuffle, Radio, compactInteractive.Value, pivot.ToArray(), pageScroll,
                            pageViewportHeight, pageAtEnd),
                        sentinel,
                        magazine,
                    ],
                },
            ],
        };
    }
}
