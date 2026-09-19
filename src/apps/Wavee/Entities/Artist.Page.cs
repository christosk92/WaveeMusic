// ── Entities/Artist.Page.cs ────────────────────────────────────────────────────────────────────────────────────────
// The artist PAGE (0.2.9 Features/Detail/ArtistPage.cs, .Shelves.cs, .Biography.cs, .Discography.cs's appears-on shelf,
// ArtistCompactBar.cs): owner N's one install method, the route's mount point, and the page component — demand on mount,
// readiness, the derived skeleton and the Retry error state, the watched chrome accent, the section plan, the context band
// (Detail.Band + Detail.Pivot, the page-owned section-anchor registry and its scroll spy), the blend wash and the shell
// tint, and the section bodies: the Top-tracks band, the shelves (appears on, music videos, playlists, concerts, merch,
// gallery, fans also like), the biography / profile-facts band, and the three banners (the upcoming card with its
// countdown clock, the latest-release banner, the tour banner).
//
// Role: UI
// Owner: N (stream N-A)
// Wave: 5
// Budget: 2200 lines
// Spec: ch 08 §1-§7, §9, §10
//
// ── HOW DATA REACHES THE PAGE (ch 08 §1.2, §7) ───────────────────────────────────────────────────────────────────────
//
// The shell mounts ONE PageHost per keep-alive slot, keyed by route, so there is no route signal inside the page. Render
// subscribes to the artist table, the two sparse side tables and every artist edge; a publish re-renders the page, and
// every child gates itself on DATA (the shelves compare their item records by value, the chart and the facets are
// components with their own gates), so a re-render rebuilds records, not cards. The page demands its whole model from
// effects: `Entities.Ensure(a, ArtistFields.All)`, the chart and concert edges while Unknown, the discography facets
// (N-B), and — re-run as each list lands — the rows those lists point at.
//
// ── THE TWO 0.2.9 INCONSISTENCIES (ch 08 §9) ─────────────────────────────────────────────────────────────────────────
//
//  1. ONE ArtistHeroMetrics per render feeds the hero, the band gutter, the magazine gutter and the wash.
//  2. The chrome accent is a WATCHED value: an auto-tracked effect reads `Palette.Watch(url)` and writes `_accent`; the
//     chart, the pivot, the pre-save buttons and the page read that signal, so a grading landing after the page settles
//     re-tints the Play capsule and the section rules at once instead of on the next unrelated render.
//
// ── THE BAND (ch 08 §9 #11) ──────────────────────────────────────────────────────────────────────────────────────────
//
// The band is `Detail.Band` / `BandTitle` / `BandHairline` / `Pivot` + `Detail.BandLayout` — written once, by M, for both
// consumers. What this file owns is the artist ARM's content (title · pivot · Play + Follow words), its gutter (the hero
// metrics'), and the SECTION ANCHORS: a fixed array of node handles filled from `OnRealized` and RESET FROM RENDER (never
// an effect: `OnRealized` fires during the reconcile of this very render, an effect after present — ch 08 §9 #6). The
// spy is an auto-tracked effect over the page's scroll signals that resolves `Detail.BandLayout.ActiveSection` into the
// pivot's `active` signal, so a scroll step re-renders the pivot only when the answer changes.

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Artist
{
    // ══ 1. THE INSTALL METHOD AND THE MOUNT POINT ═════════════════════════════════════════════════════════════════════

    /// <summary>Owner N's ONE install method (WP-5.N contract §7): the artist page, the discography page (N-B) and the
    /// three concert kinds (N-C). Called once by the composition root.</summary>
    public static void InstallPages()
    {
        Shell.SetPage(Shell.RouteKind.Artist, Page);
        Shell.SetPage(Shell.RouteKind.Discography, DiscographyPage);
        Concert.InstallPages();
    }

    /// <summary>The artist route's page, keyed by the route: artist → artist is a new slot and a fresh page.</summary>
    // MOUNT POINT (stage B contract)
    public static Element Page(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new PageProps(route.Subject, routeKey), static () => new PageHost()) with { Key = "artist:" + routeKey };
    }

    sealed record PageProps(EntityUri Subject, string RouteKey);

    // ══ 2. THE PAGE ═══════════════════════════════════════════════════════════════════════════════════════════════════

    sealed partial class PageHost : Component, IPropsHost
    {
        static readonly string[] s_secKeys = SectionKeys("sec:");
        static readonly string[] s_anchorKeys = SectionKeys("anchor:");
        static readonly Func<Element> s_emptyShape = static () => new BoxEl();
        static readonly Action<string> s_navRoute = static key => Shell.GoTo(Shell.Parse(key));

        static string[] SectionKeys(string prefix)
        {
            var keys = new string[ArtistSections.Count];
            for (int i = 0; i < keys.Length; i++) keys[i] = prefix + ArtistSections.Key((ArtistSection)i);
            return keys;
        }

        // ── identity ──
        PageProps? _latest;
        readonly Signal<PageProps?> _props = new(null);
        bool _accentSeeded;
        Scope? _scope;
        EntityUri _subject;
        Artist _artist;
        /// <summary>This page's identity as the shell material's owner; survives keep-alive park/reactivate.</summary>
        readonly object _tintOwner = new();

        // ── geometry: plain latched FIELDS (idempotent functions of the width — ch 08 §9 traps) ──
        readonly Signal<float> _heroWidth = new(ArtistHeroLayout.WideWidth);
        ArtistHeroTier _tier = ArtistHeroTier.Wide;
        ArtistHeroMetrics _metrics = ArtistHeroLayout.For(ArtistHeroLayout.WideWidth, ArtistHeroTier.Wide);
        float _width = ArtistHeroLayout.WideWidth;
        bool _topBandWide = true;
        int _bioMode;
        bool _bioModeInitialized;
        bool _classic, _showArtwork;

        // ── scroll, band, spy ──
        readonly Signal<float> _scrollY = new(0f);
        readonly Signal<float> _viewportH = new(0f);
        readonly Signal<bool> _atEnd = new(false);
        readonly Signal<bool> _compact = new(false);
        readonly Signal<int> _active = new(0);
        readonly Signal<int> _pivotEpoch = new(0);
        NodeHandle _viewport;
        readonly NodeHandle[] _anchors = new NodeHandle[ArtistSections.Count];
        readonly Action<NodeHandle>[] _anchorRealized = new Action<NodeHandle>[ArtistSections.Count];
        readonly Action[] _sectionClicks = new Action[ArtistSections.Count];
        readonly ArtistSection[] _plan = new ArtistSection[ArtistSections.Count];
        readonly ArtistSection[] _pivot = new ArtistSection[ArtistSections.Count];
        int _pivotCount, _pivotHash;

        // ── colour ──
        // Seeded from the last remembered page accent, so the first frame never paints the default over a held colour.
        readonly Signal<ColorF> _accent = new(AccentHold.Last ?? Tok.AccentDefault);
        readonly Signal<Design.PageAccent> _pageAccent = new(new Design.PageAccent(
            AccentHold.Last ?? Tok.AccentTextPrimary, AccentHold.Last ?? Tok.AccentDefault, ""));
        readonly Signal<ThemeKind> _theme = new(ThemeKind.Dark);
        readonly Func<ColorF> _accentFn;

        // ── readiness ──
        bool _ready, _failed, _bodyReady, _heroGateOpened;
        int _heroDecodeW, _heroDecodeH;
        Element? _body;
        IOverlayService? _overlay;

        // ch 08 BUG F, restored to 0.2.10's model: the WHOLE page is one reveal unit (the page-level `region` below),
        // so the only extra plumbing left is the group TOKEN the chart's own inner region (Artist.UI.Chart.cs) joins —
        // `routeKey` itself, set once per Compose() call (below) so `TopBand` can hand it to the chart.
        string _topGroup = "";

        // DemandRows' cached stamps (ch 08 render-cost fix — RowStamp is declared in Album.Page.cs, same namespace):
        // one per relation whose Ensure call walks a growing slot span, so an unrelated table drain that merely wakes
        // this effect (six of its seven subscriptions are table-wide) does not also re-walk every shelf's rows. This
        // PageHost can survive keep-alive across a DIFFERENT artist (route-keyed, not artist-keyed — ch 08 §1), so
        // RowStamp's own Slot field is what keeps a reactivated page from aliasing the previous artist's cache.
        RowStamp _popularStamp, _popularImageStamp, _appearsStamp, _playlistsStamp, _videosStamp, _fansStamp, _latestStamp;

        /// <summary>The hero photo's LATCHED art identity (ch 08 BUG E, <see cref="Detail.CoverLatch"/>): the incoming
        /// candidate (the avatar the card already carried, later the header once the overview lands) only replaces what
        /// is showing when it is genuinely different art — never on a same-artwork CDN-size swap. Reset to null on a
        /// subject change (below) so a new artist never inherits the previous one's photo.</summary>
        string? _heroUrl;

        // ── cached delegates (a node handler never captures a render's closure) ──
        readonly Func<bool> _pendingFn, _failedFn;
        readonly Func<Element> _contentFn, _shimmerFn, _failedPanelFn;
        readonly Action _demand, _demandRows, _publishAccent, _publishTheme, _resolveSpy, _bumpPivot, _retry;
        readonly Action _play, _shuffle, _radio;
        readonly Action<NodeHandle> _captureViewport;
        readonly Action<RectF> _measure;
        readonly Action<bool> _onStuck;
        readonly Func<ScrollGeometry, long> _projectScroll;
        readonly Action<ScrollGeometry> _onScroll;

        public PageHost()
        {
            for (int i = 0; i < ArtistSections.Count; i++)
            {
                int index = i;
                _anchorRealized[i] = h => _anchors[index] = h;
                _sectionClicks[i] = () => GoToSection(index);
            }
            _accentFn = () => _accent.Value;
            _pendingFn = () => !_bodyReady && !_failed;
            _failedFn = () => _failed;
            _contentFn = () => _body ?? new BoxEl();
            _shimmerFn = PageShimmer;
            _failedPanelFn = () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: _artist.IsValid ? _retry : null);
            _demand = Demand;
            _demandRows = DemandRows;
            _publishAccent = PublishAccent;
            _publishTheme = () => _theme.Value = Tok.Theme;
            _resolveSpy = ResolveSpy;
            _bumpPivot = () => _pivotEpoch.Value = _pivotEpoch.Peek() + 1;
            _retry = Demand;
            _play = Play;
            _shuffle = Shuffle;
            _radio = Radio;
            _captureViewport = h => _viewport = h;
            _measure = r =>
            {
                // The 0.5-DIP write floor: a smaller one writes every frame of a resize (ch 08 §9 traps).
                if (r.W > 0f && MathF.Abs(r.W - _heroWidth.Peek()) > 0.5f) _heroWidth.Value = r.W;
            };
            _onStuck = v => _compact.SetIfChanged(v);
            // A COARSE key: the 24-DIP write floor, the viewport height in 4-DIP steps and the at-end edge.
            _projectScroll = static g => HashCode.Combine(
                (int)(g.OffsetY / 24f),
                (int)MathF.Round(g.ViewportH / Spacing.XS),
                Detail.BandLayout.IsAtScrollEnd(g.OffsetY, g.ViewportH, g.ContentH));
            _onScroll = g =>
            {
                _scrollY.Value = g.OffsetY;                 // → the inline facet grids window against it (LazyScroll)
                _viewportH.SetIfChanged(g.ViewportH);
                _atEnd.SetIfChanged(Detail.BandLayout.IsAtScrollEnd(g.OffsetY, g.ViewportH, g.ContentH));
            };
        }

        public void ApplyProps(object props)
        {
            _latest = (PageProps)props;
            if (!_accentSeeded)
            {
                // The first paint carries a cached grading's accent instead of one frame of the default.
                _accentSeeded = true;
                _theme.Value = Tok.Theme;
                if (_latest.Subject.IsValid) PublishAccent(Entities.Artist(_latest.Subject), _latest.RouteKey);
            }
            _props.Value = _latest;
        }

        public override Element Render()
        {
            var p = _props.Value ?? _latest!;
            uint scopeEpoch = Entities.ScopeEpoch.Value;          // FIRST: a scope switch re-points every table below
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = scope.Artists.Changed.Value;
            _ = scope.ArtistPicks.Changed.Value;
            _ = scope.ArtistPreReleases.Changed.Value;
            _ = e.ArtistPopular.Changed.Value;
            _ = e.ArtistAlbums.Changed.Value;
            _ = e.ArtistSingles.Changed.Value;
            _ = e.ArtistCompilations.Changed.Value;
            _ = e.ArtistAppearsOn.Changed.Value;
            _ = e.ArtistRelated.Changed.Value;
            _ = e.ArtistGallery.Changed.Value;
            _ = e.ArtistPlaylists.Changed.Value;
            _ = e.ArtistVideos.Changed.Value;
            _ = e.ArtistMerch.Changed.Value;
            _ = e.ArtistCities.Changed.Value;
            _ = e.ArtistLinks.Changed.Value;
            _ = e.ArtistConcerts.Changed.Value;
            _ = e.FollowedArtists.Changed.Value;

            if (!ReferenceEquals(_scope, scope) || !_subject.Equals(p.Subject))
            {
                _scope = scope;
                _subject = p.Subject;
                // The factory allocates an empty row for an unseen uri: the page binds it at once and Ensure fills it.
                _artist = p.Subject.IsValid ? Entities.Artist(p.Subject) : default;
                // THE ANCHOR RESET, taken HERE: the body below is keyed by the scope epoch, so its sections realize again
                // in this reconcile and re-register AFTER the clear (ch 08 §9 #6).
                Array.Clear(_anchors);
                // The hero-photo latch resets too (ch 08 BUG E): a new artist must never inherit the previous one's
                // photo just because its own art has not resolved yet.
                _heroUrl = null;
                _heroGateOpened = false;
                _heroDecodeW = _heroDecodeH = 0;
                // DemandRows' gates (ch 08 render-cost fix): forced back to default so the first pass for the NEW
                // artist always walks every relation once, even in the (astronomically unlikely) case a recycled slot
                // number and generation happened to match the previous artist's cached stamp.
                _popularStamp = default; _popularImageStamp = default; _appearsStamp = default; _playlistsStamp = default;
                _videosStamp = default; _fansStamp = default; _latestStamp = default;
            }

            _overlay = UseContext(Overlay.Service);
            var shellSlot = UseContext(ShellMaterial.Slot);
            var a = _artist;
            UseEffect(_demand, DepKey.From(a.Slot, (int)scopeEpoch));   // once per artist per scope
            UseEffect(_demandRows);                                       // re-runs as the lists land
            UseEffect(_publishAccent);                                    // the watched chrome accent
            UseEffect(_publishTheme, DepKey.From((int)Tok.Theme));
            UseEffect(_resolveSpy);                                       // the scroll spy

            // ch 08 §6's settings, each subscribing the appearance epoch.
            bool washes = Prefs.Appearance.ColorWashes();
            _classic = Prefs.Appearance.TrackRowStyle() == 1;
            _showArtwork = !Prefs.Appearance.TrackArtworkHidden();

            _width = MathF.Max(1f, _heroWidth.Value);
            _metrics = ArtistHeroLayout.For(_width, _tier);
            _tier = _metrics.Tier;

            _ready = ArtistReadiness.Overview(a);
            bool overviewPending = a.IsValid
                && ((scope.Artists.Asked[a.Slot] & (uint)ArtistFields.Overview) != 0 || scope.Artists.Inflight[a.Slot] != 0);
            _failed = !a.IsValid || ArtistSections.PageFailed(_ready, overviewPending, e.ArtistPopular.Readiness(a.Slot));

            string routeKey = p.RouteKey;
            string? paletteUrl = a.IsValid ? Controls.ArtUrl(a.PaletteImageId) : null;
            // The hero photo's incoming candidate — the avatar the launching card already carried, later the header
            // once the overview lands — latched so the two only swap when the art identity genuinely differs (ch 08
            // BUG E; Detail.cs's CoverLatch is the same contract album/playlist covers already use).
            // Do not select the launching card's avatar while the page is still a skeleton. The overview can replace
            // that avatar with the artist header a few frames later; choosing only at the reveal boundary makes the
            // hero paint once, with the final candidate, instead of flashing avatar -> header.
            if (_ready)
            {
                string? heroCandidate = a.IsValid ? Controls.ArtUrl(a.HeroImageId) : null;
                _heroUrl = Detail.CoverLatch.PreferVisible(heroCandidate, _heroUrl);
            }
            else
            {
                _heroUrl = null;
            }

            // Start the final hero decode during the skeleton and make the reveal wait for that exact cache entry. The
            // old order revealed the page at Overview readiness, painted a flat placeholder, then swapped the photo in
            // later. That was the visible flash and made the hero look as if it resized. The page still withholds the
            // photo until the overview is complete, so an avatar cannot flash before the final header is known.
            string? heroUrl = a.IsValid ? Controls.ArtUrl(a.HeroImageId) : null;
            bool heroImageReady = true;
            if (heroUrl is { Length: > 0 } hero)
            {
                float photoH = ArtistHeroLayout.PhotoHeightFor(_metrics);
                if (_heroDecodeW <= 0 && _width > 100f)
                {
                    _heroDecodeW = Math.Clamp((int)MathF.Round(_width), 320, 1920);
                    _heroDecodeH = Math.Max(1, (int)MathF.Round(_heroDecodeW * (photoH / _width)));
                }
                int baseW = _heroDecodeW > 0 ? _heroDecodeW : Math.Clamp((int)MathF.Round(_width), 320, 1920);
                int baseH = _heroDecodeH > 0 ? _heroDecodeH : Math.Max(1, (int)MathF.Round(baseW * (photoH / _width)));
                float scale = UseContext(Viewport.Scale);
                int dw = Design.ImageDecodeScale.For(baseW, scale);
                int dh = Math.Max(1, Design.ImageDecodeScale.For(baseH, scale));
                var heroImage = UseImage(hero, dw, dh, ImagePriority.Visible, blurHash: null,
                    transition: ImageTransition.None);
                heroImageReady = heroImage.State is ImageState.Ready or ImageState.Failed;
            }

            // A 0-size leaf: the watch, the tone and the claim live there, never in this render. Gated on the incoming
            // art being USABLE (ch 08 BUG D) — never on Knows(Overview): a claim whose cover is already graded (the
            // search/home card's avatar, warmed by an earlier batch) must land its colour on the first paint, and only
            // a genuinely ungraded cover falls through to TintOwnership's hold.
            bool artUsable = Detail.CoverLatch.IsUsable(paletteUrl);
            Element tint = Palette.ShellTint(paletteUrl, ready: artUsable, disabled: !washes, apply: true,
                owner: _tintOwner, slot: shellSlot, key: "artist-tint:" + routeKey);

            // ch 08 BUG F, restored to 0.2.10's model (git show 90b56e82:…/ArtistPage.cs ~:93-136): the WHOLE page —
            // hero photo, hero text, magazine, top tracks — is ONE reveal unit, gated on the overview alone. A same-day
            // fix (ch 08 BUG E #1) once OR'd in `Detail.CoverLatch.IsUsable(_heroUrl)` so a card's avatar painted before
            // the whole eight-field-group overview landed; that broke the "renders as a unit" contract (verified/bio/
            // stats popping in above and below the title after the rest had already settled) and is reverted here. The
            // The photo→header swap is prevented by selecting `_heroUrl` only after this gate opens; the latch still
            // protects later same-art CDN-size changes.
            _bodyReady = _ready && (_heroGateOpened || heroImageReady);
            if (_bodyReady) _heroGateOpened = true;
            _body = _bodyReady ? Compose(a, paletteUrl, _heroUrl, washes, routeKey) : null;
            UseEffect(_bumpPivot, DepKey.From(_pivotHash, (int)scopeEpoch));   // a new section set → re-resolve the spy

            // ONE boundary: the derived shimmer while the overview is unknown, the magazine once it is, the shared error
            // state WITH Retry when it failed (W23 + 0.3's Retry). FadeOnly: no translate, no stagger (parity 75).
            Element region = new SkelRegionEl(
                Pending: _pendingFn, Failed: _failedFn, Content: _contentFn, ShimmerSource: _shimmerFn,
                OnFailed: _failedPanelFn, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default,
                Group: routeKey, SmoothResize: false);

            Element scroll = ScrollView(new BoxEl
            {
                Direction = 1,
                // Keyed CHILD (a key on a single content root is inert): a scope switch remounts the sections, whose
                // anchors re-register after the render-time reset above.
                Children = [new BoxEl { Key = "artist-body:" + scopeEpoch.ToString(CultureInfo.InvariantCulture), Direction = 1, Children = [region] }],
            }) with
            {
                Key = "artist-scroll:" + routeKey, Grow = 1f, ScrollKey = routeKey,
                // The spy resolves against, and a pivot click scrolls, THIS viewport — never a nested scroller.
                OnRealized = _captureViewport,
                // The band itself is the occlusion cue; the default colour cue resolved the wrong surface.
                EdgeCues = ScrollEdgeCues.None,
                OnScrollGeometryChanged = (_projectScroll, _onScroll),
            };

            // The page PROVIDES its accent (Design.AccentCtx): the heart and every other ambient consumer read it.
            return Ctx.Provide(Design.AccentCtx.Slot, (IReadSignal<Design.PageAccent>?)_pageAccent,
                Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>?)_scrollY, new BoxEl
                {
                    Key = "artist-page:" + routeKey, Grow = 1f, Direction = 1, OnBoundsChanged = _measure,
                    Children = [tint, scroll],
                }));
        }

        // ── 2.1 the loaded page ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The magazine (ArtistPage.cs:188-352): [wash clipped at the band] under [hero · sentinel · magazine
        /// clipped at the band]. Builds the section plan, the anchors and the pivot in ONE pass, so the pivot and the
        /// sections can never disagree about what the page renders. <paramref name="heroUrl"/> is the page's LATCHED
        /// hero-art identity (ch 08 BUG E) — never re-read from <c>a.HeroImageId</c> here, so the same latch that keeps
        /// the derived shimmer's caller from unmounting the photo also decides what it paints.</summary>
        Element Compose(Artist a, string? paletteUrl, string? heroUrl, bool washes, string routeKey)
        {
            var scope = Entities.Current;
            var e = scope.Edges;
            int slot = a.Slot;
            string uri = a.Uri.Text;
            ColorF accent = _accent.Value;
            var m = _metrics;
            float width = _width;
            // ch 08 BUG F, restored to 0.2.10's model: the chart's OWN inner region (Artist.UI.Chart.cs) joins the
            // SAME group the page-level region above already uses — `routeKey` itself, not a separate token — so top
            // tracks settles in the SAME one settle window as the hero and the magazine (SkelGroupCoordinator: every
            // registered member must be Done before ANY of them plays its reveal). There is no separate magazine
            // region any more: the whole page is one gate, one group.
            _topGroup = routeKey;

            // ── what is present (ch 08 §7's readiness column) ──
            // Keep edge-backed section slots in the page plan while their first answer is pending. Without this,
            // every late edge answer inserts a new sibling into the magazine and shifts all following anchors/cards.
            bool related = a.RelatedSlots.Length > 0;
            bool relatedShell = ArtistReadiness.ShelfPresent(e.ArtistRelated.Readiness(slot), a.RelatedSlots.Length);
            Span<int> fanSlots = stackalloc int[ArtistSections.FansCap];
            int fanCount = relatedShell ? 0 : ArtistSections.Fans(e.FollowedArtists.Targets(scope.MeSlot), slot, fanSlots);
            var latest = a.Latest;
            var facts = new ArtistPageFacts(
                Popular: ArtistReadiness.ShelfPresent(e.ArtistPopular.Readiness(slot), a.PopularSlots.Length),
                Pick: a.HasPick,
                Upcoming: a.HasPreRelease && a.HasUpcoming,
                // DiscoCard, not Identity: the latest-release card paints title/cover/date/kind on the artist's OWN page and never
                // the billed-artist line, and the overview answer that stages it (`ArtistStageRelease`) carries no artists —
                // gating on the whole Identity group (which now includes `Artists`) would hold the section back for an
                // AlbumV4 round trip it does not need and make it pop in late (bug F's patchiness, by another door).
                LatestRelease: latest.IsValid && latest.Knows(AlbumFields.DiscoCard),
                Albums: FacetPresent(a, DiscoFacet.Albums),
                Singles: FacetPresent(a, DiscoFacet.Singles),
                Compilations: FacetPresent(a, DiscoFacet.Compilations),
                AppearsOn: ArtistReadiness.ShelfPresent(e.ArtistAppearsOn.Readiness(slot), a.AppearsOnSlots.Length),
                Tour: !a.TourHeadlineId.IsEmpty,
                MusicVideos: ArtistReadiness.ShelfPresent(e.ArtistVideos.Readiness(slot), a.VideoSlots.Length),
                Playlists: ArtistReadiness.ShelfPresent(e.ArtistPlaylists.Readiness(slot), a.PlaylistSlots.Length),
                Concerts: ArtistReadiness.ShelfPresent(e.ArtistConcerts.Readiness(slot), a.ConcertSlots.Length),
                Merch: ArtistReadiness.ShelfPresent(e.ArtistMerch.Readiness(slot), a.MerchSlots.Length),
                Gallery: ArtistReadiness.ShelfPresent(e.ArtistGallery.Readiness(slot), a.GallerySlots.Length),
                Related: relatedShell,
                Fans: fanCount > 0);
            int n = ArtistSections.Plan(in facts, _plan);
            int[] fans = fanCount > 0 ? fanSlots[..fanCount].ToArray() : [];

            // ── the sections, their anchors and the pivot, in one pass ──
            var sections = new Element[n];
            int pivots = 0, hash = 17;
            for (int i = 0; i < n; i++)
            {
                var s = _plan[i];
                // EVERY section keyed: sections appear mid-stream as data lands, and keyless siblings would pair against
                // their neighbours' old subtrees (ArtistPage.cs:213-221).
                Element body = SectionBody(s, a, accent, fans, related ? a.RelatedSlots.Length : fanCount) with { Key = s_secKeys[(int)s] };
                if (!ArtistSections.IsDestination(s)) { sections[i] = body; continue; }
                _pivot[pivots++] = s;
                hash = unchecked(hash * 31 + (int)s + 1);
                // A layout-neutral anchor: no size, no padding, no flex of its own.
                sections[i] = new BoxEl
                {
                    Key = s_anchorKeys[(int)s], Direction = 1, MinWidth = 0f,
                    OnRealized = _anchorRealized[(int)s],
                    Children = [body],
                };
            }
            _pivotCount = pivots;
            _pivotHash = hash;
            var pivotItems = new (string Label, Action OnClick)[pivots];
            for (int i = 0; i < pivots; i++) pivotItems[i] = (PivotLabel(_pivot[i]), _sectionClicks[(int)_pivot[i]]);

            float collapse = ArtistHeroLayout.CollapseDistance(m.MinHeight);
            bool compact = _compact.Value;
            Element band = BandBar(a, width, m.Gutter, collapse, compact, pivotItems);
            Element hero = HeroBanner(HeroText.For(a), uri, heroUrl, paletteUrl, width, in m, accent,
                compact, _play, _shuffle, _radio, band, headerAccent: a.HeaderAccent);

            // One edge-only hand-off: the sentinel's PinTop(56) edge IS the band's input switch and the feather's gate.
            Element sentinel = new BoxEl { Height = 0f, HitTestVisible = false }
                .Sticky(ArtistHeroLayout.CompactIdentityHeight, _onStuck);

            // The blend wash, from the RESOLVED metrics (ch 08 §9 inconsistency #1); a cover-keyed leaf, so a grading
            // re-renders only the wash. Colour washes off ⇒ it renders nothing (the veil and the Play colour stay, §4).
            Element wash = Palette.ArtistBlendWash(paletteUrl, ArtistHeroLayout.BlendBackdropHeightFor(in m),
                ArtistHeroLayout.BlendBoundaryFor(in m), disabled: !washes, key: "artist-wash:" + uri,
                payloadAccent: a.HeaderAccent);

            // THE CLIP IS THE CONTRACT: the band paints nothing, so nothing may render into its 56 DIP — the magazine and
            // the wash are both cut at the line, and the magazine's cut is feathered while the band is engaged. No gate
            // of its own (ch 08 BUG F, restored to 0.2.10's model): the page-level `region` above already keeps the
            // whole body — this magazine included — unmounted until the overview lands, so a second, nested
            // SkelRegionEl here pending on the SAME predicate would never once see Pending() true and would only add a
            // dead group member.
            Element magazine = new BoxEl
            {
                Key = "artist-under-band", Direction = 1,
                EdgeFade = compact ? new EdgeFadeSpec(EdgeMask.Top, Detail.BandLayout.ClipFadeBand) : null,
                Children =
                [
                    new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false },
                    new BoxEl { Direction = 0, Justify = FlexJustify.Center, Children = [Magazine(sections, m.Gutter)] },
                ],
            }.ClipBelow(Detail.BandLayout.ClipInset);

            return new BoxEl
            {
                ZStack = true,
                Children =
                [
                    new BoxEl { Key = "artist-wash-clip", Direction = 1, HitTestVisible = false, Children = [wash] }
                        .ClipBelow(Detail.BandLayout.ClipInset),
                    new BoxEl { Direction = 1, Children = [hero, sentinel, magazine] },
                ],
            };
        }

        /// <summary>The centred magazine column: Grow toward the row's free width, capped at 1600, the hero's gutter on
        /// both sides, a 32 section gap, the player dock's reserve under the last section (ArtistPage.cs:271-285).</summary>
        static Element Magazine(Element[] sections, float gutter) => new BoxEl
        {
            Direction = 1, Gap = Design.Size.SectionGap,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, MaxWidth = Design.Size.PageMaxW,
            Padding = new Edges4(gutter, Spacing.M, gutter, Design.Dock.Reserve + 40f),
            Children = sections,
        };

        static string PivotLabel(ArtistSection s) => s switch
        {
            ArtistSection.Popular => Loc.Get(Strings.Artist.TopTracks),
            ArtistSection.Albums => Loc.Get(Strings.Artist.Albums),
            ArtistSection.Singles => Loc.Get(Strings.Artist.SinglesEps),
            ArtistSection.Compilations => Loc.Get(Strings.Artist.Compilations),
            ArtistSection.AppearsOn => Loc.Get(Strings.Artist.AppearsOn),
            ArtistSection.MusicVideos => Loc.Get(Strings.Artist.MusicVideos),
            ArtistSection.Playlists => Loc.Get(Strings.Artist.PlaylistsDiscovery),
            ArtistSection.Concerts => Loc.Get(Strings.Artist.UpcomingConcerts),
            ArtistSection.Merch => Loc.Get(Strings.Artist.Merch),
            ArtistSection.Biography => Loc.Get(Strings.Artist.Biography),
            ArtistSection.Gallery => Loc.Get(Strings.Artist.Gallery),
            ArtistSection.Related or ArtistSection.Fans => Loc.Get(Strings.Detail.FansAlsoLike),
            ArtistSection.Upcoming => Loc.Get(Strings.Artist.Upcoming),
            ArtistSection.LatestRelease => Loc.Get(Strings.Artist.LatestRelease),
            _ => "",
        };

        /// <summary>One section's body, in the plan's order (ArtistPage.cs:234-269).</summary>
        Element SectionBody(ArtistSection s, Artist a, ColorF accent, int[] fans, int relatedCount) => s switch
        {
            ArtistSection.Popular => TopBand(a, accent),
            ArtistSection.Upcoming => SectionBlock(Loc.Get(Strings.Artist.Upcoming), UpcomingCard(a, _accentFn, wide: false), accent),
            ArtistSection.LatestRelease => SectionBlock(Loc.Get(Strings.Artist.LatestRelease), LatestBanner(a.Latest, accent), accent),
            ArtistSection.Albums => FacetSection(a, DiscoFacet.Albums, _accentFn, Detail.BandLayout.Height, ArtistSections.FacetExpandedTopInset),
            ArtistSection.Singles => FacetSection(a, DiscoFacet.Singles, _accentFn, Detail.BandLayout.Height, ArtistSections.FacetExpandedTopInset),
            ArtistSection.Compilations => FacetSection(a, DiscoFacet.Compilations, _accentFn, Detail.BandLayout.Height, ArtistSections.FacetExpandedTopInset),
            ArtistSection.AppearsOn => AppearsOnShelf(a, accent),
            ArtistSection.Tour => TourBanner(a, accent),
            ArtistSection.MusicVideos => VideosShelf(a, accent),
            ArtistSection.Playlists => PlaylistsShelf(a, accent),
            ArtistSection.Concerts => ConcertsShelf(a, accent),
            ArtistSection.Merch => MerchShelf(a, accent),
            ArtistSection.Biography => BiographyBand(a, accent, relatedCount),
            ArtistSection.Gallery => GalleryShelf(a, accent),
            ArtistSection.Related => ArtistsShelf(a.RelatedSlots, accent),
            _ => ArtistsShelf(fans, accent),
        };

        // ── 2.2 the context band's artist arm (ArtistCompactBar.cs) ────────────────────────────────────────────────

        /// <summary>title · pivot · Play + Follow as WORDS, one hairline, no fill, no avatar, no capsules (ch 08 §0 #4).
        /// Everything geometric is the shared band's; the gutter is the hero metrics' and the content is this page's.
        /// Reveals over the last 44 DIP of the collapse with a 4-DIP settle; takes input only past the sentinel's edge.</summary>
        Element BandBar(Artist a, float width, float gutter, float collapse, bool canHit,
                        (string Label, Action OnClick)[] pivotItems)
        {
            string uri = a.Uri.Text;
            string name = a.Name;
            float actionH = Detail.BandLayout.Height - 2f * Spacing.M;
            Element title = new BoxEl
            {
                Direction = 1, MinWidth = 0f, Shrink = 1f, MaxWidth = Detail.BandLayout.TitleCap,
                Children = [Detail.BandTitle(name)],
            };
            // The pivot is the ONLY elastic lane; the title and the actions never drop (W28).
            Element pivot = new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = Detail.BandLayout.Height, AlignItems = FlexAlign.Center,
                Children = [Detail.Pivot(pivotItems, _active, _accentFn)],
            };
            Element actions = new BoxEl
            {
                Direction = 0, Gap = Detail.BandLayout.ActionGap, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children =
                [
                    Controls.TextAction(Loc.Get(Strings.Artist.Play), _play, primary: true,
                        height: actionH, padX: Detail.BandLayout.ActionPadX),
                    Embed.Comp(() => new Controls.FollowTextAction { Uri = uri, Name = name, Height = actionH, PadX = Detail.BandLayout.ActionPadX })
                        with { Key = "artist-band-follow:" + uri, SkeletonProxy = s_emptyShape },
                ],
            };
            // The content sits at the page's 1600 measure; the band's extent stays full-bleed.
            Element row = Detail.Band(MathF.Min(width, Design.Size.PageMaxW), gutter, [title, pivot, actions]);
            return new BoxEl
            {
                Width = width, Height = Detail.BandLayout.Height, ZStack = true,
                HitTestVisible = canHit, HitTestPassThrough = true,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Width = width, Height = Detail.BandLayout.Height, Justify = FlexJustify.Center,
                        Children = [row],
                    },
                    // The ONE hairline, overlaid INSIDE the 56 so the collapse arithmetic stays exact.
                    new BoxEl
                    {
                        Width = width, Height = Detail.BandLayout.Height, Direction = 1, Justify = FlexJustify.End,
                        HitTestVisible = false, Children = [Detail.BandHairline()],
                    },
                ],
            }.Reveal(ArtistHeroLayout.CompactRevealStart(collapse), collapse, Spacing.XS).Skeletonized(false);
        }

        /// <summary>A pivot click parks the section's top exactly under the band, animated unless reduced motion is on,
        /// through the engine's one bring-into-view seam against the page's OWN viewport (ContextBand.cs:347-362).</summary>
        void GoToSection(int section)
        {
            var scene = Context.Scene;
            var node = _anchors[section];
            if (scene is null || node.IsNull || _viewport.IsNull || !scene.IsLive(node) || !scene.IsLive(_viewport)) return;
            FluentGpu.Scroll.ScrollIntoView.BringInto(Context, _viewport, node, margin: Detail.BandLayout.Height,
                alignmentRatio: 0f, animate: !Design.Reduced);
        }

        /// <summary>THE SPY: one AbsoluteRect per pivot section, only on a scroll step the 24-DIP projector let through,
        /// stopping at the first unrealized section; the pivot re-renders only when the answer changes.</summary>
        void ResolveSpy()
        {
            _ = _scrollY.Value;
            _ = _viewportH.Value;
            bool atEnd = _atEnd.Value;
            _ = _pivotEpoch.Value;
            var scene = Context.Scene;
            if (scene is null || _viewport.IsNull || !scene.IsLive(_viewport)) return;
            int n = _pivotCount;
            if (n == 0) return;
            RectF vp = scene.AbsoluteRect(_viewport);
            Span<float> tops = stackalloc float[ArtistSections.Count];
            for (int i = 0; i < n; i++)
            {
                var node = _anchors[(int)_pivot[i]];
                tops[i] = node.IsNull || !scene.IsLive(node) ? float.NaN : scene.AbsoluteRect(node).Y - vp.Y;
            }
            float viewportHeight = _viewportH.Peek();
            if (viewportHeight <= 0f) viewportHeight = vp.H;
            int at = Detail.BandLayout.ActiveSection(tops[..n], Detail.BandLayout.Height, viewportHeight, atEnd);
            if (at >= 0) _active.SetIfChanged(at);   // −1 = no answer: hold what we had (D40)
        }

        // ── 2.3 the Top-tracks band (TopTracks.cs:17-104) ──────────────────────────────────────────────────────────

        /// <summary>The chart beside ONE featured object — the pick, or (no pick) the upcoming card — at ≥ 760 (736 once
        /// wide), stacked below it otherwise. Decided at the 900-DIP pre-measure width on the first composed frame
        /// (parity 90); the featured child's key folds the arm (props freeze at mount).</summary>
        Element TopBand(Artist a, ColorF accent)
        {
            bool pick = a.HasPick;
            bool upcomingInRail = ArtistSections.UpcomingInRail(pick, a.HasPreRelease && a.HasUpcoming);
            IReadSignal<ColorF> accentSignal = _accent;
            Func<ColorF> accentFn = _accentFn;
            // `_topGroup` == `routeKey`, the SAME token the page-level region uses (a LOCAL copy, like
            // `accentSignal`/`accentFn` above — the closure below may run on a later measure pass): the chart's own
            // inner SkelRegionEl (Artist.UI.Chart.cs) joins it, so top tracks settles in the SAME window as the hero
            // and the magazine instead of trailing behind as its own wave (ch 08 BUG F).
            string topGroup = _topGroup;
            return Responsive.Of(w =>
            {
                bool wide = _topBandWide = ArtistSections.TopBandWide(w, _topBandWide);
                Element tracks = Chart(a, accentSignal, topGroup);
                if (!pick && !upcomingInRail) return new BoxEl { Direction = 1, MinWidth = 0f, Children = [tracks] };
                Element featured = pick
                    ? PickCard(a, accentFn, horizontal: !wide) with { Key = wide ? "featured:pick:rail" : "featured:pick:band" }
                    : SectionBlock(Loc.Get(Strings.Artist.Upcoming), UpcomingCard(a, accentFn, wide), accent)
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
            }, fallback: ArtistSections.PreMeasureWidth);
        }

        // ── 2.4 demand, colour, verbs ───────────────────────────────────────────────────────────────────────────────

        /// <summary>The whole model on mount (ch 08 §7), through what exists: the overview + the chart bit, the chart and
        /// concert edges ONLY while nobody has answered, the three facets. The seed marks everything complete, so an
        /// offline page reaches no network. Also the Retry.</summary>
        void Demand()
        {
            var a = _artist;
            if (!a.IsValid) return;
            var e = Entities.Current.Edges;
            Entities.Ensure(a, ArtistFields.All);
            if (e.ArtistPopular.State(a.Slot) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.ArtistPopular, a.Slot);
            DemandDiscography(a);
            if (e.ArtistConcerts.State(a.Slot) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.ArtistConcerts, a.Slot);
        }

        /// <summary>Auto-tracked: as each list lands, ask for the rows it points at — the chart rows at their full row
        /// shape (Identity + PlayCount + Availability) plus the video bit (the film dot folds into the chart gate), the
        /// shelf cards at their card shapes. One batch per list, never per visible range. Six of the seven subscriptions
        /// below are table-wide (any artist's shelf landing anywhere wakes every artist page that reads it), so each
        /// Ensure call is gated behind a <see cref="RowStamp"/> for the ONE relation it walks (ch 08 render-cost fix) —
        /// re-running this whole effect stays cheap, re-walking six shelves' worth of slots on someone else's drain
        /// does not.</summary>
        void DemandRows()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            uint artistsPublished = scope.Artists.Changed.Value;
            uint tracksPublished = scope.Tracks.Changed.Value;
            uint popularPublished = e.ArtistPopular.Changed.Value;
            uint appearsPublished = e.ArtistAppearsOn.Changed.Value;
            uint relatedPublished = e.ArtistRelated.Changed.Value;
            uint playlistsPublished = e.ArtistPlaylists.Changed.Value;
            uint videosPublished = e.ArtistVideos.Changed.Value;
            uint followedPublished = e.FollowedArtists.Changed.Value;
            var a = _artist;
            if (!a.IsValid) return;
            int slot = a.Slot;

            var popularStamp = new RowStamp(slot, popularPublished, e.ArtistPopular.Version(slot));
            if (popularStamp.Moved(_popularStamp))
            {
                _popularStamp = popularStamp;
                var popular = a.PopularSlots;
                if (popular.Length > 0)
                {
                    Entities.Ensure(MemoryMarshal.Cast<int, Track>(popular), TrackFields.Row | TrackFields.Video);

                }
            }

            // The edge and its track rows can publish in different drains.  Repeat the bounded image prefetch when
            // either source changes, otherwise an edge-first answer would still make the chart wait for its reveal
            // before discovering the newly landed covers.
            var popularImageStamp = new RowStamp(slot, tracksPublished, e.ArtistPopular.Version(slot), Source: 5);
            if (popularImageStamp.Moved(_popularImageStamp))
            {
                _popularImageStamp = popularImageStamp;
                var popular = a.PopularSlots;
                int prefetchCount = Math.Min(popular.Length, 10);
                for (int i = 0; i < prefetchCount; i++)
                {
                    var track = new Track(popular[i]);
                    StringId art = track.ImageId.IsEmpty ? track.Album.ImageId : track.ImageId;
                    string? url = Controls.ArtUrl(art);
                    // The chart artwork is a 24-DIP cell. 96 px was a four-times oversize decode for this
                    // surface and made the first chart burst compete with the page's larger images. 64 px covers
                    // a 2x display with room for filtering while keeping the request/upload small.
                    if (!string.IsNullOrEmpty(url)) PrefetchImage(url, 64);
                }
            }

            var appearsStamp = new RowStamp(slot, appearsPublished, e.ArtistAppearsOn.Version(slot));
            if (appearsStamp.Moved(_appearsStamp))
            {
                _appearsStamp = appearsStamp;
                var appears = a.AppearsOnSlots;
                int appearsCount = Math.Min(appears.Length, ArtistSections.AppearsOnCap);
                if (appearsCount > 0) Entities.Ensure(MemoryMarshal.Cast<int, Album>(appears[..appearsCount]), AlbumFields.Card);
            }

            var playlistsStamp = new RowStamp(slot, playlistsPublished, e.ArtistPlaylists.Version(slot));
            if (playlistsStamp.Moved(_playlistsStamp))
            {
                _playlistsStamp = playlistsStamp;
                var playlists = a.PlaylistSlots;
                if (playlists.Length > 0) Entities.Ensure(MemoryMarshal.Cast<int, Playlist>(playlists), PlaylistFields.Identity);
            }

            var videosStamp = new RowStamp(slot, videosPublished, e.ArtistVideos.Version(slot));
            if (videosStamp.Moved(_videosStamp))
            {
                _videosStamp = videosStamp;
                var videos = a.VideoSlots;
                if (videos.Length > 0) Entities.Ensure(MemoryMarshal.Cast<int, Track>(videos), TrackFields.Identity);
            }

            // The fans source flips between this artist's OWN related edge and the me-row's followed-artists edge. The
            // SOURCE TAG is what makes that safe: those two parents live in DIFFERENT tables (an artist slot and the
            // account's user slot), and a slot is a per-table index — so without the tag a flip could match the other
            // source's cached generation/version by coincidence, read as "not moved", and skip the fetch.
            var related = a.RelatedSlots;
            var fansStamp = related.Length > 0
                ? new RowStamp(slot, relatedPublished, e.ArtistRelated.Version(slot), Source: 3)
                : new RowStamp(scope.MeSlot, followedPublished, e.FollowedArtists.Version(scope.MeSlot), Source: 4);
            if (fansStamp.Moved(_fansStamp))
            {
                _fansStamp = fansStamp;
                if (related.Length > 0)
                {
                    int relatedCount = Math.Min(related.Length, ArtistSections.FansCap);
                    Entities.Ensure(MemoryMarshal.Cast<int, Artist>(related[..relatedCount]), ArtistFields.Identity);
                }
                else
                {
                    Span<int> fans = stackalloc int[ArtistSections.FansCap];
                    int n = ArtistSections.Fans(e.FollowedArtists.Targets(scope.MeSlot), a.Slot, fans);
                    if (n > 0) Entities.Ensure(MemoryMarshal.Cast<int, Artist>(fans[..n]), ArtistFields.Identity);
                }
            }

            var latestStamp = new RowStamp(slot, artistsPublished, a.Version);
            if (latestStamp.Moved(_latestStamp))
            {
                _latestStamp = latestStamp;
                var latest = a.Latest;
                if (latest.IsValid)
                {
                    Span<Album> one = stackalloc Album[1];
                    one[0] = latest;
                    Entities.Ensure(one, AlbumFields.Card);
                }
            }
        }

        /// <summary>Auto-tracked: the header's grading, the theme tick and the artist row. A grading landing re-derives
        /// the accent — the page re-renders once, not on "the next unrelated render" (ch 08 §9 inconsistency #2).</summary>
        void PublishAccent()
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Artists.Changed.Value;
            _ = _theme.Value;
            PublishAccent(_artist, _latest?.RouteKey ?? "");
        }

        /// <summary>The accent and the page-scoped context pair are written together.</summary>
        void PublishAccent(Artist a, string routeKey)
        {
            var accent = AccentFor(a);
            _accent.SetIfChanged(accent);
            _pageAccent.SetIfChanged(new Design.PageAccent(accent, accent, routeKey));
        }

        /// <summary>The header's chrome grading, else the avatar's, else the header payload colour, else the ladder's
        /// held/default rungs (<see cref="Detail.AccentFor"/>). Both urls are watched HERE, inside the tracked
        /// effect, so either grading landing re-derives the accent.</summary>
        static ColorF AccentFor(Artist a)
        {
            if (!a.IsValid) return AccentHold.Last ?? Tok.AccentDefault;
            string? url = Controls.ArtUrl(a.PaletteImageId);
            string? avatar = Controls.ArtUrl(a.ImageId);
            if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
            if (avatar is { Length: > 0 } && !string.Equals(avatar, url, StringComparison.Ordinal)) _ = Palette.Watch(avatar).Value;
            return Detail.AccentFor(url, a.HeaderAccent, fallbackUrl: avatar);
        }

        void Play()
        {
            if (_artist.IsValid) Playback.PlayContext(_artist.Id);
        }

        /// <summary>Shuffle ON, then the artist context (Hero Shuffle, ch 08 §6).</summary>
        void Shuffle()
        {
            if (!_artist.IsValid) return;
            Playback.SetShuffle(true);
            Playback.PlayContext(_artist.Id);
        }

        /// <summary>A REAL radio off the artist (never a replay of the artist context — G-251): the seed resolves to its
        /// radio playlist, which plays now or parks behind the current track, and the OUTCOME raises the toast
        /// (<see cref="Queue.RadioToast"/>), never this click. 0.3 replaces 0.2.9's raw exception string with the
        /// friendly unavailable line (ch 08 §6).</summary>
        void Radio()
        {
            if (!_artist.IsValid) return;
            string name = _artist.Name;
            Playback.StartRadio(new EntityUri(_artist.Id), outcome => Queue.RadioToast(outcome with { Name = name }));
        }

        // ── 2.5 the skeleton ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The derived shimmer's SOURCE (W5): the same hero composition over a representative shape at the
        /// tier's real heights (no action row, no band — both are never skeleton content), the divider, a title-width
        /// header bar over the chart's two 5-row columns at the live pitch, a facet header bar and a row of square grid
        /// placeholders. Every geometry is the loaded page's, so nothing shifts when the content lands (parity 74).</summary>
        Element PageShimmer()
        {
            var m = _metrics;
            Element hero = HeroBanner(HeroText.Placeholder, "", null, null, _width, in m, _accent.Peek(), false,
                null, null, null, null);
            return new BoxEl
            {
                Direction = 1,
                Children =
                [
                    hero,
                    new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
                    new BoxEl
                    {
                        Direction = 0, Justify = FlexJustify.Center,
                        Children = [MagazineShimmer()],
                    },
                ],
            };
        }

        /// <summary>The magazine's own derived-shimmer shape (chart skeleton + one facet skeleton) — <see cref="PageShimmer"/>'s
        /// bottom half, factored out so the doc above stays readable. There is no second consumer any more (ch 08 BUG F,
        /// restored to 0.2.10's model): the whole page — hero included — is one derived shimmer while the overview is
        /// unknown, never a hero-then-magazine two-step.</summary>
        Element MagazineShimmer()
        {
            var cells = new Element[5];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.S,
                    Children =
                    [
                        Controls.ArtworkFill(null, Radii.Card),
                        Design.Type.CardTitle("Album title") with { MaxLines = 1 },
                        Design.Type.TrackMeta("2026 · 12") with { MaxLines = 1 },
                    ],
                };
            Element facet = new BoxEl
            {
                Direction = 1, Gap = Spacing.M,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center,
                        Children = [Controls.AccentHeader(Loc.Get(Strings.Artist.Albums), _accent.Peek())],
                    },
                    new BoxEl { Direction = 0, Gap = 16f, Children = cells },
                ],
            };
            return Magazine([ChartSkeleton(0, _classic, _showArtwork), facet], _metrics.Gutter);
        }

        // ── 2.6 the shelves (Shelves.cs, Discography.cs's appears-on; W19) ──────────────────────────────────────────

        /// <summary>A measured PagedShelf under an accent header: auto-fit cards between 150 and 200, gap 12, one row,
        /// edge fade 36, stock chevrons. The items are VALUE records (slot + version), so a publish that changes nothing
        /// a card paints rebuilds no card; a hydrated name bumps the version and rebuilds exactly that card.</summary>
        static Element ShelfOf(ShelfEntity[] items, Func<ShelfEntity, int, float, Element> cardAt, string title, ColorF accent)
            => new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                // Artist shelf cards use Controls.ShelfCard's fixed square art and label budget. Supplying the
                // shared height formula keeps the virtual shelf from mounting a probe row of full cards (artwork,
                // tooltips, playback overlays) just to discover a height it already knows. The probe was a major
                // source of late mount/layout bursts during navigation and could make the page appear to flicker.
                Children = [PagedShelf.Create(items, cardAt, cardHeight: Controls.ShelfHeight,
                    header: Controls.AccentHeader(title, accent), measured: false, keyOf: s_shelfKey)],
            };

        static readonly Func<ShelfEntity, int, string> s_shelfKey = static (item, _) => item.Key;

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

        Element AppearsOnShelf(Artist a, ColorF accent)
            => ShelfOf(Items(a.AppearsOnSlots, EntityKind.Album, ArtistSections.AppearsOnCap), AlbumCard,
                       Loc.Get(Strings.Artist.AppearsOn), accent);

        /// <summary>Square cover, title, and the year — or the kind when the year is unknown (parity 56).</summary>
        Element AlbumCard(ShelfEntity item, int index, float w)
        {
            var al = new Album(item.Slot);
            string uri = al.Uri.Text;
            string title = al.Title;
            string? cover = Controls.ArtUrl(al.ImageId);
            string sub = al.Year > 0 ? al.Year.ToString(CultureInfo.CurrentCulture) : Detail.Text.KindLabel(al.Kind);
            var data = new Controls.CardData(uri, title, CardSubtitle(sub), cover,
                OnClick: () => Track.GoToAlbum(al),
                OnPlay: () => Playback.PlayContext(al.Id),
                Drag: Drag.Source(() => new DragPayload(DragKind.Album, uri, uri, title, new EntityRef(EntityKind.Album, al.Slot), ArtUrl: cover)));
            return WithCardMenu(Controls.ShelfCard(data, w),
                () => CardMenu(ActionTarget.ForAlbum(al.Uri, title), cover, title, sub, artist: false));
        }

        Element VideosShelf(Artist a, ColorF accent)
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
            return ShelfOf(items, VideoCard, Loc.Get(Strings.Artist.MusicVideos), accent);
        }

        /// <summary>The 16:9 music-video card: the thumb at card width − 16, the title, the duration (Shelves.cs:58-74,
        /// MediaCard.VideoCard). It stands for its TRACK — the menu is the track's, the drag carries the track.</summary>
        Element VideoCard(ShelfEntity item, int index, float w)
        {
            var t = new Track(item.Slot);
            string uri = t.Uri.Text;
            string title = t.Title;
            string? thumb = Controls.ArtUrl(item.Sub);
            string duration = item.Aux > 0 ? Track.Format.TrackTime(item.Aux) : "";
            float inner = MathF.Max(64f, w - 2f * Spacing.S);
            float thumbH = MathF.Round(inner * 9f / 16f);
            Action play = () => Playback.PlayContext(t.Id);
            Element content = new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Grow = 1f,
                Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, Width = inner, Height = thumbH, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Control),
                        Children =
                        [
                            Controls.Artwork(thumb, inner, thumbH, Radii.Control, decodePx: 480),
                            Controls.NowPlayingOverlay(uri, play, 44f, centred: true),
                        ],
                    },
                    Design.Type.TrackTitle(title) with { Width = inner, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    duration.Length == 0
                        ? new BoxEl()
                        : Design.Type.TrackMeta(duration) with { Width = inner, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            };
            Element card = new BoxEl
            {
                Width = w, Shrink = 0f, Padding = new Edges4(0f, Spacing.XS, 0f, 2f),
                Children =
                [
                    Controls.CardShell(content, play, Drag.Source(() => new DragPayload(DragKind.Track, uri, uri, title,
                        new EntityRef(EntityKind.Track, t.Slot), Tracks: [t], ArtUrl: thumb))),
                ],
            };
            return WithCardMenu(card, () => Track.Menu([t], in s_trackMenu));
        }

        static readonly Track.MenuOptions s_trackMenu = new(ShowGoToAlbum: true);

        Element PlaylistsShelf(Artist a, ColorF accent)
        {
            var slots = a.PlaylistSlots;
            var subtitles = a.PlaylistSubtitleIds;
            int n = Math.Min(slots.Length, ArtistSections.PlaylistCap);
            var items = new ShelfEntity[n];
            for (int i = 0; i < n; i++)
                items[i] = new ShelfEntity(slots[i], new Playlist(slots[i]).Version, i < subtitles.Length ? subtitles[i] : default, 0,
                    slots[i].ToString(CultureInfo.InvariantCulture));
            return ShelfOf(items, PlaylistCard, Loc.Get(Strings.Artist.PlaylistsDiscovery), accent);
        }

        Element PlaylistCard(ShelfEntity item, int index, float w)
        {
            var pl = new Playlist(item.Slot);
            string uri = pl.Uri.Text;
            string title = Entities.Strings.Resolve(pl.TitleId);
            string sub = Entities.Strings.Resolve(item.Sub);
            string? cover = Controls.ArtUrl(pl.ImageId);
            var data = new Controls.CardData(uri, title, CardSubtitle(sub), cover,
                OnClick: () => Shell.GoTo(Shell.For(pl.Uri, title)),
                OnPlay: () => Playback.PlayContext(pl.Id),
                Drag: Drag.Source(() => new DragPayload(DragKind.Playlist, uri, uri, title, new EntityRef(EntityKind.Playlist, pl.Slot), ArtUrl: cover)));
            return WithCardMenu(Controls.ShelfCard(data, w),
                () => CardMenu(ActionTarget.ForPlaylist(pl.Uri, title), cover, title, sub, artist: false));
        }

        Element ConcertsShelf(Artist a, ColorF accent)
            => ShelfOf(Items(a.ConcertSlots, EntityKind.Concert, ArtistSections.ConcertCap), s_concertCard,
                       Loc.Get(Strings.Artist.UpcomingConcerts), accent);

        /// <summary>The concert stub is N-C's (ch 08 §9: Concert.UI.cs absorbs 0.2.9's ArtistPage.ConcertStub). No menu.</summary>
        static readonly Func<ShelfEntity, int, float, Element> s_concertCard = static (item, _, w) =>
        {
            var c = new Concert(item.Slot);
            return Concert.Stub(c, () => Shell.GoTo(Concert.DetailRoute(c)), w);
        };

        Element MerchShelf(Artist a, ColorF accent)
            => ShelfOf(Items(a.MerchSlots, EntityKind.Unknown, ArtistSections.MerchCap), s_merchCard, Loc.Get(Strings.Artist.Merch), accent);

        /// <summary>Square art, a 2-line name, the price in accent ink (Shelves.cs:139-165). 0.2.9's card was the one inert
        /// card on the page (parity 91): 0.3 opens the shop link when the row carries one.</summary>
        static readonly Func<ShelfEntity, int, float, Element> s_merchCard = static (item, _, w) =>
        {
            ref readonly Merch merch = ref Album.MerchAt(item.Slot);
            string name = Entities.Strings.Resolve(merch.Name);
            string price = Entities.Strings.Resolve(merch.Price);
            string? image = Controls.ArtUrl(merch.ImageId);
            string shop = Entities.Strings.Resolve(merch.ShopUrl);
            bool link = shop.Length > 0;
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Width = w, Shrink = 0f, ClipToBounds = true,
                Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
                Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault,
                HoverScale = Design.Motion.ScaleStandard.Hover,
                Role = link ? AutomationRole.Button : AutomationRole.None, Focusable = link,
                FocusVisualMargin = Design.FocusInsetBordered,
                Cursor = link ? CursorId.Hand : null,
                OnClick = link ? () => OpenLink(shop) : null,
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Control),
                        Children = [Controls.ArtworkFill(image, Radii.Control)],
                    },
                    new TextEl(name)
                    {
                        Size = 13f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                    new TextEl(price) { Size = 13f, Weight = 700, Color = Tok.AccentTextPrimary, MaxLines = 1 },
                ],
            };
        };

        Element GalleryShelf(Artist a, ColorF accent)
        {
            var ids = a.GallerySlots;
            int n = Math.Min(ids.Length, ArtistSections.GalleryCap);
            var items = new ShelfEntity[n];
            var urls = new string[n];
            for (int i = 0; i < n; i++)
            {
                items[i] = new ShelfEntity(i, 0, ids[i], 0, "g" + i.ToString(CultureInfo.InvariantCulture));
                urls[i] = Controls.ArtUrl(ids[i]) ?? "";
            }
            return ShelfOf(items, (item, index, w) => new BoxEl
            {
                Width = w, Height = w, Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
                Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered,
                Cursor = CursorId.Hand,
                HoverScale = Design.Motion.ScaleStandard.Hover, PressScale = Design.Motion.ScaleStandard.Press,
                OnClick = () => GalleryOpen(_overlay, urls, index),
                Children = [Controls.Artwork(urls[index], w, w, Radii.Card, decodePx: 480)],
            }, Loc.Get(Strings.Artist.Gallery), accent);
        }

        /// <summary>"Fans also like": circular cards with the subtitle "Artist" — the related artists, or the fallback
        /// pool of followed artists (the section keys differ, so the two never share a subtree).</summary>
        Element ArtistsShelf(ReadOnlySpan<int> slots, ColorF accent)
            => ShelfOf(Items(slots, EntityKind.Artist, ArtistSections.FansCap), ArtistCard,
                       Loc.Get(Strings.Detail.FansAlsoLike), accent);

        Element ArtistCard(ShelfEntity item, int index, float w)
        {
            var ar = new Artist(item.Slot);
            string uri = ar.Uri.Text;
            string name = ar.Name;
            string? image = Controls.ArtUrl(ar.ImageId);
            string sub = Loc.Get(Strings.Search.TypeArtist);
            var data = new Controls.CardData(uri, name, CardSubtitle(sub), image,
                OnClick: () => Track.GoToArtist(ar),
                OnPlay: () => Playback.PlayContext(ar.Id),
                Circular: true,
                Drag: Drag.Source(() => new DragPayload(DragKind.Artist, uri, uri, name, new EntityRef(EntityKind.Artist, ar.Slot), ArtUrl: image)));
            return WithCardMenu(Controls.ShelfCard(data, w),
                () => CardMenu(ActionTarget.ForArtist(ar.Uri, name), image, name, sub, artist: true));
        }

        static Element CardSubtitle(string text) => Design.Type.TrackMeta(text) with
        {
            MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

        // ── the card menu (W27) ──

        Element WithCardMenu(Element card, Func<ContextMenuModel?> menu)
        {
            var overlay = _overlay;
            if (Controls.IsNullOverlay(overlay)) return card;
            return ContextMenu.Attach(new BoxEl { Direction = 1, MinWidth = 0f, Children = [card] }, overlay, menu);
        }

        // Album / playlist card: strip [Play · Play next · Add to queue · Save], rows [Add to playlist · Open · Pin/Unpin ·
        // Go to artist · Share]. Artist card: strip [Play · Follow], rows [Open · Pin/Unpin · Share · Go to artist radio].
        static readonly ActionId[] s_containerStrip = [ActionId.PlayContext, ActionId.PlayContextNext, ActionId.AddContextToQueue, ActionId.SaveContext];
        static readonly ActionId[] s_containerRows = [ActionId.AddContextToPlaylist, ActionId.OpenItem];
        static readonly ActionId[] s_containerTail = [ActionId.GoToAlbumArtist];
        static readonly ActionId[] s_artistStrip = [ActionId.PlayContext, ActionId.SaveContext];
        static readonly ActionId[] s_artistRows = [ActionId.OpenItem];
        static readonly ActionId[] s_artistTail = [ActionId.GoToArtistRadio];

        /// <summary>The card grammar composed from the registered verbs; a verb nobody registered is simply absent.
        /// Pin and Unpin are an absolute-state pair: only the one that can run is offered.</summary>
        static ContextMenuModel? CardMenu(ActionTarget target, string? art, string title, string? subtitle, bool artist)
        {
            var ctx = new ActionContext(target, Actions.Services);
            var strip = Actions.Menu.Strip(in ctx, artist ? s_artistStrip : s_containerStrip);
            var rows = new List<MenuFlyoutItem>(8);
            Actions.Menu.AddRows(rows, in ctx, artist ? s_artistRows : s_containerRows);
            if (AppActions.Find(ActionId.PinToSidebar) is { } pin && pin.EnabledFor(in ctx)) rows.Add(pin.ToMenuItem(in ctx));
            else if (AppActions.Find(ActionId.UnpinFromSidebar) is { } unpin && unpin.EnabledFor(in ctx)) rows.Add(unpin.ToMenuItem(in ctx));
            Actions.Menu.AddRows(rows, in ctx, artist ? s_artistTail : s_containerTail);
            if (Actions.Menu.Share(in ctx) is { } share) { Actions.Menu.OpenGroup(rows); rows.Add(share); }
            if (strip.Length == 0 && rows.Count == 0) return null;
            return new ContextMenuModel(strip, rows, Actions.Menu.Header(art, title, subtitle, circular: artist));
        }

        // ── 2.7 the biography band (Biography.cs, W18) ──────────────────────────────────────────────────────────────

        /// <summary>The biography card (Grow 2) beside the profile facts (Grow 1) at mode 0 of the detail page's mode
        /// ladder (≥ 820, re-entered at 844), stacked below. Two genuine object panels — the page's one exception to
        /// "sections are not cards". A zero stat drops its tile; no tiles ⇒ the biography takes the full width.</summary>
        Element BiographyBand(Artist a, ColorF accent, int relatedCount)
        {
            string bio = Entities.Strings.Resolve(a.BioId);
            LinkEdge[] links = a.Links.ToArray();
            CityEdge[] cities = a.TopCities.ToArray();
            long monthly = a.MonthlyListeners, followers = a.Followers;
            long albums = FacetTotal(a, DiscoFacet.Albums), singles = FacetTotal(a, DiscoFacet.Singles);
            long concerts = a.ConcertSlots.Length;
            return Responsive.Of(w =>
            {
                _bioMode = Detail.Breakpoints.ModeFor(w, _bioMode, _bioModeInitialized);
                _bioModeInitialized = true;
                bool wide = _bioMode == 0;

                int leftCount = 1 + (bio.Length > 0 ? 1 : 0) + (links.Length > 0 ? 1 : 0) + (cities.Length > 0 ? 1 : 0);
                var leftKids = new Element[leftCount];
                int k = 0;
                leftKids[k++] = Controls.AccentHeader(Loc.Get(Strings.Artist.Biography), accent);
                if (bio.Length > 0)
                    leftKids[k++] = Controls.RichText(bio, 14f, Tok.TextSecondary, Tok.AccentTextPrimary,
                        w * (wide ? 0.62f : 1f) - 60f, 14, s_navRoute);
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
                    for (int i = 0; i < n; i++) tiles[i] = BioFactTile(CountLabel(facts[i].Value), FactLabel(facts[i].Kind));
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
            }, fallback: ArtistSections.PreMeasureWidth);
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
        static Element BioFactTile(string value, string label) => new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Grow = 1f, Basis = 140f, MinWidth = 0f,
            Padding = Edges4.All(Spacing.L),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = [Title(value) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, Caption(label)],
        };

        /// <summary>The external-link pills — 0.2.9's were boxes with NO click at all (parity 92). They open now.</summary>
        static Element LinkPills(LinkEdge[] links)
        {
            var pills = new Element[links.Length];
            for (int i = 0; i < links.Length; i++)
            {
                string name = Entities.Strings.Resolve(links[i].Name);
                string url = Entities.Strings.Resolve(links[i].Url);
                pills[i] = new BoxEl
                {
                    Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(12f, 7f, 14f, 7f), Corners = Radii.FullAll,
                    BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillSubtleSecondary,
                    Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered,
                    Cursor = CursorId.Hand,
                    OnClick = () => OpenLink(url),
                    Children =
                    [
                        Icon(Icons.Link, 13f, Tok.TextSecondary),
                        new TextEl(name) { Size = 13f, Weight = 600, Color = Tok.TextPrimary },
                    ],
                };
            }
            return new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, Children = pills };
        }

        /// <summary>"Listened to most in": each city 34 tall, a 4-DIP accent bar proportional to listeners / max.</summary>
        static Element CityBars(CityEdge[] cities, ColorF accent)
        {
            long max = 1;
            for (int i = 0; i < cities.Length; i++) if (cities[i].Listeners > max) max = cities[i].Listeners;
            var rows = new Element[cities.Length + 1];
            rows[0] = Design.Type.Eyebrow(Loc.Get(Strings.Artist.ListenedMostIn)) with { Color = Tok.TextTertiary };
            for (int i = 0; i < cities.Length; i++)
            {
                float frac = (float)((double)cities[i].Listeners / max);
                rows[i + 1] = new BoxEl
                {
                    Direction = 1, Gap = 4f, Height = 34f,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center,
                            Children =
                            [
                                new TextEl(Entities.Strings.Resolve(cities[i].City))
                                {
                                    Size = 14f, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f,
                                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                },
                                new TextEl(CountLabel(cities[i].Listeners)) { Size = 13f, Color = Tok.TextSecondary },
                            ],
                        },
                        new BoxEl
                        {
                            Direction = 0, Height = 4f,
                            Children =
                            [
                                new BoxEl { Grow = MathF.Max(0.001f, frac), Height = 4f, Corners = CornerRadius4.All(2f), Fill = accent },
                                new BoxEl { Grow = MathF.Max(0.001f, 1f - frac), Height = 4f },
                            ],
                        },
                    ],
                };
            }
            return new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows };
        }
    }

    // ══ 3. THE BANNERS (TopTracks.cs, Shelves.cs — section bodies, moved here from Artist.UI.cs for its budget) ══════

    // ── 3.1 the section shell ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A section is an accent-ruled header over its body — never a card (ch 08 §0 #11, Sections.cs:39-42).</summary>
    internal static Element SectionBlock(string title, Element body, ColorF accent) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M, MinWidth = 0f,
        Children = [Controls.AccentHeader(title, accent), body],
    };

    // ── 3.2 the upcoming release (TopTracks.cs:106-253) ─────────────────────────────────────────────────────────────

    /// <summary>The date-led upcoming card, two arms: the rail COLUMN (<paramref name="wide"/>, no pick) and the full-width
    /// BAND. No Play, ever — a prerelease has nothing to stream; Pre-save and View only (W12).</summary>
    internal static Element UpcomingCard(Artist a, Func<ColorF> accent, bool wide)
    {
        ColorF tint = accent();
        ref readonly ArtistPreRelease pre = ref a.PreRelease;
        string uri = Entities.Strings.Resolve(pre.Uri);
        string name = Entities.Strings.Resolve(pre.Name);
        string type = Entities.Strings.Resolve(pre.Type);
        string? cover = Controls.ArtUrl(pre.Cover);
        int releaseAt = pre.ReleaseAt;
        string artistName = a.Name;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Sentence case: the type word is localized upstream and never caps-transformed.
        string eyebrowText = type.Length > 0 ? Loc.Get(Strings.Artist.Upcoming) + " · " + type : Loc.Get(Strings.Artist.Upcoming);
        TextEl eyebrow = Design.Type.Eyebrow(eyebrowText) with { Color = tint, MaxLines = 1 };
        TextEl title = Design.Type.PickQuote(name) with
        {
            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        string? dateText = releaseAt > 0 ? Track.Format.ShortDate(releaseAt, now) : null;
        Element artistLine = Caption(artistName) with
        {
            Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        // The "releases …" caption joins the meta only on the BAND arm; in the column the big date IS the headline.
        Element meta = new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children = !wide && dateText is not null
                ? [artistLine, Caption(Strings.Detail.ReleasesOn(dateText)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }]
                : [artistLine],
        };
        TextEl? bigDate = dateText is null ? null : Design.Type.SurfaceDisplay(dateText) with { MaxLines = 1 };

        Element actions = new BoxEl
        {
            Direction = 0, Gap = Spacing.S,
            Children =
            [
                Embed.Comp(() => new Controls.PreSaveButton { Uri = uri, Name = name, Accent = accent }) with { Key = "presave:" + uri },
                Button.Create(Loc.Get(Strings.Artist.View), () => Shell.GoTo(Shell.For(EntityUri.Parse(uri), name)),
                    ButtonAppearance.Outline, ControlSize.Small),
            ],
        }.Skeletonized(false);

        // The live clock only inside two weeks; keyed on uri + instant because the instant freezes at mount.
        Element? countdown = ArtistSections.ShowsCountdown(releaseAt, now)
            ? Embed.Comp(new PageUpcomingClockProps(releaseAt), static () => new PageUpcomingClock())
                with { Key = "artist-upcoming:" + uri + ":" + releaseAt.ToString(CultureInfo.InvariantCulture) }
            : null;

        Element content;
        if (wide)
        {
            int n = 4 + (bigDate is null ? 0 : 1) + (countdown is null ? 0 : 1);
            var col = new Element[n];
            int k = 0;
            col[k++] = eyebrow;
            if (bigDate is not null) col[k++] = bigDate;
            col[k++] = title;
            col[k++] = meta;
            if (countdown is not null) col[k++] = countdown;
            col[k] = actions;
            content = new BoxEl { Direction = 1, Padding = Edges4.All(Spacing.L), Gap = Spacing.S, Children = col };
        }
        else
        {
            Element coverBox = new BoxEl { Shrink = 0f, Children = [Controls.Artwork(cover, 88f, 88f, Radii.Control, decodePx: 192)] };
            Element copy = new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS,
                Children = [eyebrow, title with { MaxLines = 1 }, meta],
            };
            Element actionCol = new BoxEl
            {
                Direction = 1, Gap = Spacing.S, AlignItems = FlexAlign.End, Shrink = 0f,
                Children = countdown is null ? [actions] : [actions, countdown],
            };
            content = new BoxEl
            {
                Direction = 0, Gap = Spacing.XL, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Padding = new Edges4(Spacing.XL, Spacing.L, Spacing.XL, Spacing.L),
                Children = bigDate is null ? [coverBox, copy, actionCol] : [coverBox, copy, bigDate with { Shrink = 0f }, actionCol],
            };
        }

        return new BoxEl
        {
            ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    HitTestVisible = false,
                    Gradient = GradientDown(
                        new GradientStop(0f, tint with { A = 0.16f }),
                        new GradientStop(0.55f, tint with { A = 0.05f }),
                        new GradientStop(0.85f, tint with { A = 0f })),
                },
                content,
            ],
        };
    }

    sealed record PageUpcomingClockProps(int ReleaseAt);

    /// <summary>The pre-release countdown's LIVE host: <c>Controls.PreReleaseCountdown</c> is a static composition, so
    /// this one-second interval (auto-paused while parked or minimized) is what re-renders it.</summary>
    sealed class PageUpcomingClock : Component
    {
        public override Element Render()
        {
            var p = UseProps<PageUpcomingClockProps>();
            var tick = UseSignal(0);
            _ = tick.Value;
            UseInterval(() => tick.Value = tick.Peek() + 1, 1000f);
            long left = Math.Max(0L, p.ReleaseAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return Controls.PreReleaseCountdown(TimeSpan.FromSeconds(left));
        }
    }

    // ── 3.3 the latest-release banner and the tour banner ───────────────────────────────────────────────────────────

    /// <summary>The "just dropped" full-width banner (TopTracks.cs:255-311): cover 72 · "Album · date · N tracks" ·
    /// the 20/28 title · [▶ Play][View]. Leaf buttons only — the card body does NOT navigate (parity 42); the whole card
    /// is a drag source.</summary>
    internal static Element LatestBanner(Album al, ColorF accent)
    {
        string title = al.Title;
        string uri = al.Uri.Text;
        string? cover = Controls.ArtUrl(al.ImageId);
        string date = DiscoCardText.ReleaseDateLabel(al);
        int tracks = al.TrackCount;
        string eyebrow = Detail.Text.KindLabel(al.Kind)
                         + (date.Length > 0 ? " · " + date : "")
                         + (tracks > 0 ? " · " + Strings.Artist.TrackCount(tracks) : "");
        var album = al;
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = Edges4.All(Spacing.M), Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Draggable = Drag.Source(() => new DragPayload(DragKind.Album, uri, uri, title,
                new EntityRef(EntityKind.Album, album.Slot), ArtUrl: cover)),
            Children =
            [
                new BoxEl { Shrink = 0f, Children = [Controls.Artwork(cover, 72f, 72f, Radii.Control, decodePx: 144)] },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS,
                    Children =
                    [
                        Design.Type.Eyebrow(eyebrow) with { Color = Tok.TextTertiary, MaxLines = 1 },
                        Subtitle(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children =
                    [
                        Controls.Play(accent, () => Playback.PlayContext(album.Id), Loc.Get(Strings.Artist.Play)),
                        Button.Create(Loc.Get(Strings.Artist.View), () => Track.GoToAlbum(album),
                            ButtonAppearance.Outline, ControlSize.Small),
                    ],
                }.Skeletonized(false),
            ],
        };
    }

    /// <summary>The tour announcement (W20, Shelves.cs:27-51): a 44 accent circle, the commit-derived eyebrow /
    /// headline / subline, a chevron; the card opens the artist's concert schedule. Not a pivot destination.</summary>
    internal static Element TourBanner(Artist a, ColorF accent)
    {
        var artist = a;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.L, Padding = Edges4.All(Spacing.L),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault,
            OnClick = () => Shell.GoTo(Concert.ScheduleRoute(artist)),
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered,
            Cursor = CursorId.Hand,
            Children =
            [
                new BoxEl
                {
                    Width = 44f, Height = 44f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(22f), Fill = accent,
                    Children = [Icon(a.IsTourLive ? Icons.RadioTower : Icons.Calendar, 18f, Tok.TextOnAccentPrimary)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f,
                    Children =
                    [
                        Design.Type.Eyebrow(Entities.Strings.Resolve(a.TourEyebrowId)) with { Color = Design.Accent.Decor },
                        new TextEl(Entities.Strings.Resolve(a.TourHeadlineId))
                            { Size = 16f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(Entities.Strings.Resolve(a.TourSublineId))
                            { Size = 13f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
                Icon(Icons.ChevronRight, 16f, Tok.TextSecondary),
            ],
        };
    }

    /// <summary>One shelf card's identity + the data it paints that is not on the handle: a slot, its row version, an
    /// optional id (a playlist subtitle, a video thumb, a gallery image) and an int (a video's duration). A record, so the
    /// shelf's gate compares it by VALUE.</summary>
    readonly record struct ShelfEntity(int Slot, uint Version, StringId Sub, int Aux, string Key);

    /// <summary>Open an http(s) link through the actions seam, else the shell. Anything else is refused: a decoded url
    /// must never launch a program.</summary>
    static void OpenLink(string url)
    {
        if (!System.Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != System.Uri.UriSchemeHttps && parsed.Scheme != System.Uri.UriSchemeHttp)) return;
        if (Actions.Services.OpenExternal is { } open) { open(url); return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("artist", "could not open an external link", ex);
        }
    }
}
