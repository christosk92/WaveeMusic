// ── Entities/Artist.Page.cs ────────────────────────────────────────────────────────────────────────────────────────
// The artist PAGE's CORE HOST (0.2.9 Features/Detail/ArtistPage.cs, ArtistCompactBar.cs): owner N's one install method, the route's mount point, and the
// host component — demand on mount, readiness, the Retry state, the watched chrome accent, the section plan and the pivot's membership, the page-owned
// section-anchor registry and its scroll spy, the shell tint, and the Play / Shuffle / Radio decisions. The PICTURE is the structure's since T10b1: the host
// builds an `ArtistPageProps` and mounts `ViewKeys.ArtistPage`; Classic draws the scroll column and its skeleton region, the hero, the context band, the
// Top-tracks band, the shelves, the biography band, the banners and the pick card (`Themes\Classic\Structure\Views\Pages\ClassicArtist*.cs`).
//
// Role: CORE HOST
// Owner: N (stream N-A)
// Wave: 5
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
// The band is `ClassicDetailBand` (`Band` / `BandTitle` / `BandHairline` / `Pivot`) + `Detail.BandLayout` — written once, by M, for both
// consumers; the artist ARM (title · pivot · Play + Follow words) is Classic's. What this file owns is the SECTION
// ANCHORS: a fixed array of node handles filled from `OnRealized` (the host hands each pivot destination's callback in
// its `ArtistSectionSlot`) and RESET FROM RENDER (never an effect: `OnRealized` fires during the reconcile of the view's
// render, an effect after present — ch 08 §9 #6). The spy is an auto-tracked effect over the page's scroll signals that
// resolves `Detail.BandLayout.ActiveSection` into the pivot's `active` signal, so a scroll step re-renders the pivot only
// when the answer changes.
//
// The host reads no token and builds no element (WVTH008): the few token reads its decisions need (the accent ladder's
// default, the theme epoch, the decode scale, reduced motion) are the static helpers at the end of this file.

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Scroll.Effects;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
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

    [CoreHost]
    sealed partial class PageHost : Component, IPropsHost
    {
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
        readonly Signal<int> _layoutEpoch = new(0);
        ArtistHeroTier _tier = ArtistHeroTier.Wide;
        ArtistHeroMetrics _metrics = ArtistHeroLayout.For(ArtistHeroLayout.WideWidth, ArtistHeroTier.Wide);
        float _width = ArtistHeroLayout.WideWidth;
        bool _measured;

        // ── scroll, band, spy ──
        readonly Signal<float> _scrollY = new(0f);
        readonly Signal<float> _viewportH = new(0f);
        readonly Signal<bool> _atEnd = new(false);
        // The sentinel's sticky ENGAGED edge (`Sticky(56, engaged: _compact)` — written by the engine on the flip, before
        // the frame publishes): the band's input switch and the magazine feather's gate.
        readonly Signal<bool> _compact = new(false) { DebugName = "artist.compact" };
        /// <summary>The page viewport's scroll handle: the spy's coarse geometry is projected off it, and the band title
        /// scrolls it home.</summary>
        readonly ScrollHandle _scroll = new();
        long _scrollKey = long.MinValue;   // the last coarse geometry key the watch let through
        readonly Signal<int> _active = new(Detail.BandLayout.NoSection);   // nothing lit until the spy answers (RCA E)
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
        readonly Signal<ColorF> _accent = new(HeldAccent());
        readonly Signal<Design.PageAccent> _pageAccent = new(InitialPageAccent());
        readonly Func<ColorF> _accentFn;
        int _accentEpoch = -1;   // the Tok.Epoch the accent was last published at (themes plan X44)

        // ── readiness ──
        bool _ready, _failed, _bodyReady, _heroGateOpened;
        int _heroDecodeW, _heroDecodeH;
        IOverlayService? _overlay;

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
        readonly Func<ColorF> _accentPeekFn;
        readonly Func<Signal<bool>> _compactEdgeFn;
        readonly Action<string[], int> _openGallery;
        readonly Action _demand, _demandRows, _publishAccent, _republishAccent, _resolveSpy, _bumpPivot, _retry;
        readonly Action _play, _shuffle, _radio, _scrollToTop;
        readonly Action<NodeHandle> _captureViewport;
        readonly Action<RectF> _measure;
        readonly Action _watchScroll;
        readonly Func<long> _stampFn;

        public PageHost()
        {
            for (int i = 0; i < ArtistSections.Count; i++)
            {
                int index = i;
                _anchorRealized[i] = h => _anchors[index] = h;
                _sectionClicks[i] = () => GoToSection(index);
            }
            _accentFn = () => _accent.Value;
            _accentPeekFn = () => _accent.Peek();
            _compactEdgeFn = () => _compact;
            _openGallery = (urls, index) => GalleryOpen(_overlay, urls, index);
            _pendingFn = () => !_bodyReady && !_failed;
            _failedFn = () => _failed;
            _demand = Demand;
            _demandRows = DemandRows;
            _publishAccent = PublishAccent;
            _republishAccent = RepublishAccent;
            _resolveSpy = ResolveSpy;
            _bumpPivot = () => _pivotEpoch.Value = _pivotEpoch.Peek() + 1;
            _retry = Demand;
            _play = Play;
            _shuffle = Shuffle;
            _radio = Radio;
            _scrollToTop = ScrollToTop;
            _captureViewport = h => _viewport = h;
            _measure = r =>
            {
                // The 0.5-DIP write floor: a smaller one writes every frame of a resize (ch 08 §9 traps).
                if (r.W <= 0f) return;
                if (!_measured)
                {
                    _measured = true;
                    _heroWidth.Value = r.W;
                    _layoutEpoch.Value++;
                    return;
                }
                if (MathF.Abs(r.W - _heroWidth.Peek()) > 0.5f) _heroWidth.Value = r.W;
            };
            // A COARSE key off the page's scroll handle: the 24-DIP write floor, the viewport height in 4-DIP steps and
            // the at-end edge. The watch re-runs on every moved frame and writes only when the key changes.
            _watchScroll = () =>
            {
                float y = (float)_scroll.Offset.Value, vh = (float)_scroll.ViewportSignal.Value, ch = (float)_scroll.ExtentSignal.Value;
                bool atEnd = Detail.BandLayout.IsAtScrollEnd(y, vh, ch);
                long key = HashCode.Combine((int)(y / 24f), (int)MathF.Round(vh / Spacing.XS), atEnd);
                if (key == _scrollKey) return;
                _scrollKey = key;
                _scrollY.Value = y;                         // → the inline facet grids window against it (LazyScroll)
                _viewportH.SetIfChanged(vh);
                _atEnd.SetIfChanged(atEnd);
            };
            _stampFn = Stamp;
        }
        public void ApplyProps(object props)
        {
            _latest = (PageProps)props;
            if (!_accentSeeded)
            {
                // The first paint carries a cached grading's accent instead of one frame of the default.
                _accentSeeded = true;
                _accentEpoch = ThemeEpoch();
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
            float scale = UseContext(Viewport.Scale);
            var a = _artist;
            // THE RENDER-COST FOLD (ch 08 render-cost fix): one UseComputed<long> replacing the sixteen table-wide
            // Changed reads this Render used to make directly. Stamp() still reads every one of those Changed signals
            // (the WAKE — a publication anywhere in that table recomputes the fold), but folds only the slot-specific
            // Version/Readiness values Render, PlanSections and the view's Compose actually paint (the PUSH — Memo's push-pull equality
            // cut-off), so an unrelated artist's shelf landing elsewhere recomputes this cheaply without re-rendering
            // the whole page. See Stamp()'s doc for the exact dependency table.
            _ = UseComputed(_stampFn).Value;
            // THE ONE EXCEPTION (ch 08 §9): ArtistReadiness.Chart/ChartFailed additionally read every popular
            // track's Known/Asked/Inflight bits (up to ArtistPopularTracks.ExtendedCap rows), which change per
            // in-flight row — folding them would cost as much as just re-rendering on them. Kept as a literal
            // table-wide subscription, same as before.
            _ = e.ArtistPopular.Changed.Value;
            UseEffect(_demand, DepKey.From(a.Slot, (int)scopeEpoch));   // once per artist per scope
            UseEffect(_demandRows);                                       // re-runs as the lists land
            UseEffect(_publishAccent);                                    // the watched chrome accent
            UseEffect(_republishAccent, DepKey.From(ThemeEpoch()));          // a theme flip or a definition swap re-derives the accent (X44)
            UseEffect(_resolveSpy);                                       // the scroll spy

            // ch 08 §6's settings, each subscribing the appearance epoch. The structure's view draws with them (M2 moves the decisions behind them).
            bool rowsClassic = Prefs.Appearance.TrackRowStyle() == 1;

            _ = _layoutEpoch.Value;
            _width = MathF.Max(1f, _heroWidth.Value);
            _metrics = ArtistHeroLayout.For(_width, _tier);
            _tier = _metrics.Tier;

            _ready = ArtistReadiness.Overview(a);
            bool overviewPending = a.IsValid
                && ((scope.Artists.Asked[a.Slot] & (uint)ArtistFields.Overview) != 0 || scope.Artists.Inflight[a.Slot] != 0);
            _failed = !a.IsValid || ArtistSections.PageFailed(_ready, overviewPending, e.ArtistPopular.Readiness(a.Slot));

            string routeKey = p.RouteKey;
            string? paletteUrl = a.IsValid ? Controls.ArtUrl(a.PaletteImageId) : null;
            // Latch the hero only once the overview is known so a launching card's avatar cannot paint, then swap to
            // the header. Do not clear a latched url after the first reveal — that unmounted HeroArt and flashed the
            // flat placeholder over already-visible copy.
            if (_ready)
            {
                string? heroCandidate = a.IsValid ? Controls.ArtUrl(a.HeroImageId) : null;
                _heroUrl = Detail.CoverLatch.PreferVisible(heroCandidate, _heroUrl);
            }
            else if (!_heroGateOpened)
            {
                _heroUrl = null;
            }

            // Decode at the MEASURED width, and only after the overview has chosen the final art. Decoding against
            // the default WideWidth during the skeleton made the page wait on a 1440-wide cache entry, then HeroArt
            // mounted at the real width and decoded again — a flat hero in between. UseContext(Viewport.Scale) is
            // unconditional: a hero url appearing used to add that hook mid-life and remount the whole page.
            string? heroUrl = _heroUrl;
            // Always call UseImage so the hook count never changes when the url/measure lands (the same remount
            // class as a conditional UseContext(Viewport.Scale)). Empty src is a no-op binding.
            int dw = 8, dh = 8;
            bool heroImageReady = true;
            string heroBind = heroUrl ?? "";
            if (_measured && heroBind.Length > 0)
            {
                float photoH = ArtistHeroLayout.PhotoHeightFor(_metrics);
                if (_heroDecodeW <= 0)
                {
                    _heroDecodeW = Math.Clamp((int)MathF.Round(_width), 320, 1920);
                    _heroDecodeH = Math.Max(1, (int)MathF.Round(_heroDecodeW * (photoH / _width)));
                }
                int baseW = _heroDecodeW > 0 ? _heroDecodeW : Math.Clamp((int)MathF.Round(_width), 320, 1920);
                int baseH = _heroDecodeH > 0 ? _heroDecodeH : Math.Max(1, (int)MathF.Round(baseW * (photoH / _width)));
                dw = DecodePx(baseW, scale);
                dh = Math.Max(1, DecodePx(baseH, scale));
            }
            var heroImage = UseImage(heroBind, dw, dh, ImagePriority.Visible, blurHash: null);
            if (heroBind.Length > 0)
                heroImageReady = heroImage.State is ImageState.Ready or ImageState.Failed;

            // A 0-size leaf: the watch, the tone and the claim live there, never in this render. Gated on the incoming
            // art being USABLE (ch 08 BUG D) — never on Knows(Overview): a claim whose cover is already graded (the
            // search/home card's avatar, warmed by an earlier batch) must land its colour on the first paint, and only
            // a genuinely ungraded cover falls through to TintOwnership's hold.
            bool artUsable = Detail.CoverLatch.IsUsable(paletteUrl);
            Element tint = Palette.ShellTint(paletteUrl, ready: artUsable, apply: true,
                owner: _tintOwner, slot: shellSlot, key: "artist-tint:" + routeKey);

            // ONE visual swap: overview + measured width + the first hero decode + chart settled. Snap, not FadeOnly:
            // waiting for the bitmap then fading the whole tree from 0 hid the photo again (stillwrong.mp4).
            bool chartSettled = ArtistReadiness.ChartSettled(ArtistReadiness.Chart(a), ArtistReadiness.ChartFailed(a));
            _bodyReady = ArtistReadiness.BodyReady(_ready, _measured, _heroGateOpened, chartSettled);
            if (_bodyReady) _heroGateOpened = true;

            // THE PLAN: which sections the page renders, in order, and which of them are pivot destinations (one pass, so the pivot and the sections can never
            // disagree about what the page renders). Only a ready page has a body; a pending one keeps the last pivot.
            ArtistSectionSlot[] sections = [];
            ArtistPivotItem[] pivot = [];
            int[] fans = [];
            int relatedCount = 0;
            if (_bodyReady) PlanSections(a, out sections, out pivot, out fans, out relatedCount);
            UseEffect(_bumpPivot, DepKey.From(_pivotHash, (int)scopeEpoch));   // a new section set → re-resolve the spy
            UseSignalEffect(_watchScroll);

            // The accent and the band's engaged edge are read here, as the page always read them (only for a body that is composed): this render wakes on
            // the grading and on the sentinel's flip. The derived skeleton peeks at the accent instead (`AccentPeek`).
            ColorF accent = _bodyReady ? _accent.Value : _accent.Peek();
            bool compact = _bodyReady && _compact.Value;

            return global::FluentGpu.Theming.Slots.View(ViewKeys.ArtistPage, new ArtistPageProps
            {
                RouteKey = routeKey,
                ScopeKey = scopeEpoch.ToString(CultureInfo.InvariantCulture),
                Artist = a,
                ScrollKey = UseContext(Shell.PageScrollScope) + routeKey,
                Pending = _pendingFn,
                Failed = _failedFn,
                Retry = a.IsValid ? _retry : null,
                BodyReady = _bodyReady,
                Width = _width,
                Metrics = _metrics,
                Measure = _measure,
                Accent = accent,
                AccentFn = _accentFn,
                AccentPeek = _accentPeekFn,
                AccentSignal = _accent,
                PageAccent = _pageAccent,
                PaletteUrl = paletteUrl,
                HeroUrl = _heroUrl,
                Tint = tint,
                Compact = compact,
                CompactEdge = _compactEdgeFn,
                Pivot = pivot,
                Active = _active,
                Scroll = _scroll,
                CaptureViewport = _captureViewport,
                ScrollY = _scrollY,
                Sections = sections,
                Fans = fans,
                RelatedCount = relatedCount,
                Play = _play,
                Shuffle = _shuffle,
                Radio = _radio,
                ScrollToTop = _scrollToTop,
                OpenGallery = _openGallery,
                Overlay = _overlay,
                RowsClassic = rowsClassic,
            });
        }

        // ── 2.1 the section plan ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The magazine's plan (ArtistPage.cs:188-352): what is present (ch 08 §7's readiness column), the sections in their one fixed order, each
        /// pivot destination's anchor callback and the pivot's tabs, in ONE pass. Everything after Compilations collapses into ONE "Details" tab: only the first
        /// present member of <see cref="ArtistSections.IsDetailsGroup"/> becomes a destination (Biography is unconditional, so Details always shows), the rest
        /// still render in place but without their own pivot entry.</summary>
        void PlanSections(Artist a, out ArtistSectionSlot[] sections, out ArtistPivotItem[] pivot, out int[] fans, out int relatedCount)
        {
            var scope = Entities.Current;
            var e = scope.Edges;
            int slot = a.Slot;
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
            fans = fanCount > 0 ? fanSlots[..fanCount].ToArray() : [];
            relatedCount = related ? a.RelatedSlots.Length : fanCount;

            sections = new ArtistSectionSlot[n];
            int pivots = 0, hash = 17;
            bool detailsAssigned = false;
            for (int i = 0; i < n; i++)
            {
                var s = _plan[i];
                bool isDest;
                if (ArtistSections.IsDetailsGroup(s))
                {
                    isDest = !detailsAssigned;
                    detailsAssigned = true;
                }
                else
                {
                    isDest = ArtistSections.IsDestination(s);
                }
                sections[i] = new ArtistSectionSlot(s, isDest ? _anchorRealized[(int)s] : null);
                if (!isDest) continue;
                _pivot[pivots++] = s;
                hash = unchecked(hash * 31 + (int)s + 1);
            }
            _pivotCount = pivots;
            _pivotHash = hash;
            pivot = new ArtistPivotItem[pivots];
            for (int i = 0; i < pivots; i++) pivot[i] = new ArtistPivotItem(PivotLabel(_pivot[i]), _sectionClicks[(int)_pivot[i]]);
        }

        static string PivotLabel(ArtistSection s) => s switch
        {
            ArtistSection.Albums => Loc.Get(Strings.Artist.Albums),
            ArtistSection.Singles => Loc.Get(Strings.Artist.SinglesEps),
            ArtistSection.Compilations => Loc.Get(Strings.Artist.Compilations),
            // Everything past Compilations shares one tab (ArtistSections.IsDetailsGroup): whichever member is
            // first present becomes the single "Details" destination, so the label is the same regardless of which.
            ArtistSection.AppearsOn or ArtistSection.MusicVideos or ArtistSection.Playlists or ArtistSection.Concerts
                or ArtistSection.Merch or ArtistSection.Biography or ArtistSection.Gallery or ArtistSection.Related
                or ArtistSection.Fans => Loc.Get(Strings.Artist.Details),
            ArtistSection.Upcoming => Loc.Get(Strings.Artist.Upcoming),
            ArtistSection.LatestRelease => Loc.Get(Strings.Artist.LatestRelease),
            _ => "",
        };

        /// <summary>A pivot click parks the section's top exactly under the band, animated unless reduced motion is on,
        /// through the engine's one bring-into-view seam against the page's OWN viewport (ContextBand.cs:347-362).</summary>
        void GoToSection(int section)
        {
            var scene = Context.Scene;
            var node = _anchors[section];
            if (scene is null || node.IsNull || _viewport.IsNull || !scene.IsLive(node) || !scene.IsLive(_viewport)) return;
            scene.BringIntoView(_viewport, node, align: 0f, GlideOrJump(),
                margin: Detail.BandLayout.Height);
        }

        /// <summary>The band title's click target: no "Overview" pivot tab, so the title itself is the way back to the
        /// hero. A raw offset, not <see cref="GoToSection"/> — the top of the page has no section anchor to bring in.</summary>
        void ScrollToTop() => _scroll.ScrollTo(0.0, GlideOrJump());

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
            if (at != _spyLogged) LogSpy(at, tops[..n], viewportHeight, atEnd);
            if (at != -1) _active.SetIfChanged(at);   // −1 = no answer: hold what we had (D40); NoSection lights nothing
        }

        // Evidence (2026-09-25): `ui.spy page=artist at= label= tops= line= vh= atEnd=` — one line each time the spy's
        // ANSWER changes (never per scroll step), so "the pivot lit Singles & EPs while Top tracks was on screen" is a
        // recorded fact with the section tops and the activation line that produced it. Allocates only on a change.
        int _spyLogged = int.MinValue;

        void LogSpy(int at, ReadOnlySpan<float> tops, float viewportHeight, bool atEnd)
        {
            _spyLogged = at;
            if (!Log.IsEnabled(WaveeLogLevel.Info)) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(tops.Length * 8);
            for (int i = 0; i < tops.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(float.IsNaN(tops[i]) ? "NaN" : tops[i].ToString("0", inv));
            }
            string label = at >= 0 && at < _pivotCount ? PivotLabel(_pivot[at]) : at == -1 ? "(hold)" : "(none)";
            Log.Event(WaveeLogLevel.Info, "ui", "ui.spy", "", null, -1, null,
                WaveeLogField.Of("page", "artist"),
                WaveeLogField.Of("at", at),
                WaveeLogField.Of("label", label),
                WaveeLogField.Of("tops", sb.ToString()),
                WaveeLogField.Of("line", (double)Detail.BandLayout.SpyLine(Detail.BandLayout.Height, viewportHeight)),
                WaveeLogField.Of("vh", (double)viewportHeight),
                WaveeLogField.Of("atEnd", atEnd));
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

        /// <summary>THE RENDER-COST FOLD (ch 08 render-cost fix, part 2): everything Render and PlanSections — and the view's Compose and section bodies, which
        /// run INLINE off this render's props, never a separately-gated child (<see cref="FacetSection"/>'s FacetHost and
        /// <see cref="Controls.FollowTextAction"/>/the upcoming card's clock subscribe themselves and are
        /// deliberately NOT folded here) — read from the entity tables, folded into one value the same shape as
        /// <see cref="RowStamp"/>/<see cref="RowFold"/> (Album.Page.cs) and Shell.PlayerBar.UI.cs's <c>ArtistLineStamp</c>:
        /// read the table's <c>Changed</c> for the WAKE (a publication anywhere in that table must recompute this, so a
        /// real answer for THIS row is never missed), then fold only the SLOT-SPECIFIC <c>Version</c>/count values Render
        /// actually paints for the PUSH (<see cref="Memo{T}"/>'s push-pull equality cut-off: recompute is cheap — a
        /// handful of array reads — and only a value that MOVED re-renders the page).
        ///
        /// <para><b>The dependency table</b> (every datum Render, PlanSections and the view's Compose read, and the term covering it):</para>
        /// <list type="bullet">
        /// <item>this artist's row (identity, header/hero images, header accent, bio, monthly/followers, the pick, the
        /// pre-release, the latest-release POINTER, the tour text) — <c>RowFold.Row(scope.Artists, slot)</c>. The pick
        /// and pre-release side-slab CONTENT (<see cref="ArtistPickTable"/>/<see cref="ArtistPreReleaseTable"/>) is
        /// written and <c>Applied</c> in the same commit as the row's own pointer (Artist.cs's overview decode), so the
        /// row's <c>Version</c> alone covers it — <c>scope.ArtistPicks</c>/<c>ArtistPreReleases</c>' own <c>Changed</c>
        /// is redundant and is no longer read.</item>
        /// <item>the latest release's OWN fields (<see cref="AlbumFields.DiscoCard"/>, read for <c>Facts.LatestRelease</c>
        /// and painted by Classic's latest-release banner) — a DIFFERENT table (Albums): <c>RowFold.Row(scope.Albums,
        /// a.Latest.Slot)</c>.</item>
        /// <item>the ten section-presence / facet edges whose ONLY page-level use is <c>ShelfPresent</c>/<c>FacetPresent</c>
        /// — Albums, Singles, Compilations, Gallery, Merch, Cities, Links — <c>Version(slot)</c> alone: <c>ShelfPresent</c>'s
        /// boolean cannot move on a Version-less Readiness flip (a <c>MarkFailed</c> without a structural write) because
        /// Unknown and Failed both read as "not Complete", the same branch either way; <c>FacetPresent</c> reads the
        /// edge's raw <c>State</c>, never <c>Readiness</c>, so it cannot see a failure mark at all.</item>
        /// <item>the four shelves whose CARDS are OTHER rows — AppearsOn (Albums), Playlists (Playlists), Videos
        /// (Tracks), Concerts (Concerts) — the edge's own <c>Version(slot)</c> (which/how many) AND every target row's
        /// <c>Version</c> (a title/cover hydrating after the edge landed, so <see cref="Items"/>'s value-record rebuilds
        /// exactly that shelf's cards next time the view's Compose runs).</item>
        /// <item>Related artists / the "fans also like" fallback — <c>e.ArtistRelated.Version(slot)</c> then every
        /// related artist's <c>Version</c>; when the related edge is genuinely empty (mirrors PlanSections' own
        /// <c>relatedShell</c> branch exactly), the me-row's <see cref="Edges.FollowedArtists"/> edge at
        /// <c>scope.MeSlot</c> — A DIFFERENT PARENT, so it is its own term, never conflated with this artist's slot —
        /// then every artist <see cref="ArtistSections.Fans"/> would actually pick.</item>
        /// <item>Gallery/Merch payload TEXT (pills, tiles) carries no separate target row — it lands whole with the
        /// edge's own <c>Replace</c>, so the edge's <c>Version(slot)</c> above is already the whole story.</item>
        /// </list>
        ///
        /// <para><b>Kept OUT of this fold</b> (too dynamic to fold cheaply, per the render-cost fix's own escape
        /// hatch): <see cref="ArtistReadiness.Chart"/>/<see cref="ArtistReadiness.ChartFailed(Artist)"/> additionally
        /// read every popular track's <c>Known</c>/<c>Asked</c>/<c>Inflight</c> bits (up to
        /// <see cref="ArtistPopularTracks.ExtendedCap"/> rows, each landing independently) to decide the chart gate —
        /// folding that is as expensive as just re-rendering on it. Render keeps a literal
        /// <c>e.ArtistPopular.Changed</c> subscription instead, exactly as before.</para></summary>
        long Stamp()
        {
            _ = Entities.ScopeEpoch.Value;                      // FIRST (ch 08 §9 / G-179): re-point every table below
            var a = _artist;
            if (!a.IsValid) return 0L;
            var scope = Entities.Current;
            var e = scope.Edges;
            int slot = a.Slot;
            var artists = scope.Artists;
            var albums = scope.Albums;
            var playlists = scope.Playlists;
            var tracks = scope.Tracks;
            var concerts = scope.Concerts;

            // ── the wakes ──
            _ = artists.Changed.Value;
            _ = albums.Changed.Value;
            _ = playlists.Changed.Value;
            _ = tracks.Changed.Value;
            _ = concerts.Changed.Value;
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

            // ── the fold ──
            ulong h = RowFold.Row(RowFold.Seed, artists, slot);
            h = RowFold.Row(h, albums, a.Latest.Slot);

            h = RowFold.Add(h, (int)e.ArtistAlbums.Version(slot));
            h = RowFold.Add(h, (int)e.ArtistSingles.Version(slot));
            h = RowFold.Add(h, (int)e.ArtistCompilations.Version(slot));
            h = RowFold.Add(h, (int)e.ArtistGallery.Version(slot));
            h = RowFold.Add(h, (int)e.ArtistMerch.Version(slot));
            h = RowFold.Add(h, (int)e.ArtistCities.Version(slot));
            h = RowFold.Add(h, (int)e.ArtistLinks.Version(slot));

            h = RowFold.Add(h, (int)e.ArtistAppearsOn.Version(slot));
            var appears = e.ArtistAppearsOn.Targets(slot);
            for (int i = 0; i < appears.Length; i++) h = RowFold.Row(h, albums, appears[i]);

            h = RowFold.Add(h, (int)e.ArtistPlaylists.Version(slot));
            var pls = e.ArtistPlaylists.Targets(slot);
            for (int i = 0; i < pls.Length; i++) h = RowFold.Row(h, playlists, pls[i]);

            h = RowFold.Add(h, (int)e.ArtistVideos.Version(slot));
            var videos = e.ArtistVideos.Targets(slot);
            for (int i = 0; i < videos.Length; i++) h = RowFold.Row(h, tracks, videos[i]);

            h = RowFold.Add(h, (int)e.ArtistConcerts.Version(slot));
            var shows = e.ArtistConcerts.Targets(slot);
            for (int i = 0; i < shows.Length; i++) h = RowFold.Row(h, concerts, shows[i]);

            h = RowFold.Add(h, (int)e.ArtistRelated.Version(slot));
            var related = e.ArtistRelated.Targets(slot);
            for (int i = 0; i < related.Length; i++) h = RowFold.Row(h, artists, related[i]);

            // Mirrors PlanSections' OWN `relatedShell` branch exactly (ArtistReadiness.ShelfPresent over the same edge):
            // the followed-artists fallback is only ever consulted — and only ever rendered — while it is true.
            bool relatedShell = ArtistReadiness.ShelfPresent(e.ArtistRelated.Readiness(slot), related.Length);
            if (!relatedShell)
            {
                h = RowFold.Add(h, (int)e.FollowedArtists.Version(scope.MeSlot));
                Span<int> fanSlots = stackalloc int[ArtistSections.FansCap];
                int fanCount = ArtistSections.Fans(e.FollowedArtists.Targets(scope.MeSlot), slot, fanSlots);
                for (int i = 0; i < fanCount; i++) h = RowFold.Row(h, artists, fanSlots[i]);
            }

            return unchecked((long)h);
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

        /// <summary>Auto-tracked: the header's grading and the artist row. A grading landing re-derives
        /// the accent — the page re-renders once, not on "the next unrelated render" (ch 08 §9 inconsistency #2).</summary>
        void PublishAccent()
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Artists.Changed.Value;
            PublishAccent(_artist, _latest?.RouteKey ?? "");
        }

        /// <summary>Themes plan X44: the accent follows <see cref="Tok.Epoch"/> (the light/dark kind AND the definition) instead of a
        /// <c>Signal&lt;ThemeKind&gt;</c> the page republished on every flip. The first publish (<see cref="ApplyProps"/>) already ran
        /// at the current epoch, so the effect's mount run does nothing.</summary>
        void RepublishAccent()
        {
            if (_accentEpoch == ThemeEpoch()) return;
            _accentEpoch = ThemeEpoch();
            PublishAccent(_artist, _latest?.RouteKey ?? "");
        }

        /// <summary>The accent and the page-scoped context pair are written together.</summary>
        void PublishAccent(Artist a, string routeKey)
        {
            var accent = AccentFor(a);
            _accent.SetIfChanged(accent);
            _pageAccent.SetIfChanged(PageAccentOf(accent, routeKey));
        }

        void Play()
        {
            if (_artist.IsValid) Playback.PlayOrToggleContext(_artist.Id);
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
    }

    // ── 2.3 the token reads the host's decisions need ────────────────────────────────────────────────────────────────
    //
    // The host builds no presentation and reads no token (WVTH008); these are the few reads its DECISIONS make — the accent ladder's held / default rungs,
    // the theme epoch the accent follows, the decode scale of the hero photo's warm-up and reduced motion's choice of a scroll move — kept out of the host type.

    /// <summary>The last remembered page accent, else the default: the first paint never shows the default over a held colour.</summary>
    static ColorF HeldAccent() => AccentHold.Last ?? Tok.AccentDefault;

    /// <summary>The page accent before the first publish (the held colour for both inks).</summary>
    static Design.PageAccent InitialPageAccent()
        => new(AccentHold.Last ?? Tok.AccentTextPrimary, AccentHold.Last ?? Tok.AccentDefault, "");

    /// <summary>The page accent the page provides to its subtree, for a route.</summary>
    static Design.PageAccent PageAccentOf(ColorF accent, string routeKey) => new(accent, accent, routeKey);

    /// <summary>The epoch of the theme definition and its light/dark kind (themes plan X44): the accent re-derives when it moves.</summary>
    static int ThemeEpoch() => Tok.Epoch;

    /// <summary>The decode size for a hero photo's base size at the viewport's zoom scale.</summary>
    static int DecodePx(int basePx, float scale) => Design.ImageDecodeScale.For(basePx, scale);

    /// <summary>A glide, or an immediate jump under reduced motion.</summary>
    static ScrollMove GlideOrJump() => Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide;

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
}
