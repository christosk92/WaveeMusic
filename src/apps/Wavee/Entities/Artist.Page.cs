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
//
// ── ONE BASELINE, AND THE ZUNE ROW 2 (A2) ───────────────────────────────────────────────────────────────────────────────
//
// The band is `Detail.BandCluster`: title · divider · tabs · actions share one 20-DIP line (Detail.BandLayout). Under Zune the
// band lives in row 2: the page publishes the same words (`PageHead.PublishBand`, under its route name), its in-page band is
// not composed, its stuck floor is 0 and the hero collapses to that floor itself. The floor is LATCHED
// (Detail.BandLayout.FloorLatch): it flips only while the scroll is under FlipLine, where every scroll channel presents the
// same, so a nav-style switch while scrolled never snaps the hero, the sentinel or the clip. Above the line the page keeps its
// band (row 2 shows the same words for that window) until the scroll next crosses back. `_inRow2` is the ONE input for the
// hero's floor, both clips, the facets' pin, the spy's band height, GoToSection's margin, the sentinel and whether the band
// composes.

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
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
        readonly Signal<int> _layoutEpoch = new(0);
        ArtistHeroTier _tier = ArtistHeroTier.Wide;
        ArtistHeroMetrics _metrics = ArtistHeroLayout.For(ArtistHeroLayout.WideWidth, ArtistHeroTier.Wide);
        float _width = ArtistHeroLayout.WideWidth;
        bool _measured;
        bool _topBandWide = true;
        int _bioMode;
        bool _bioModeInitialized;
        bool _classic, _showArtwork;

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
        // The LATCHED floor placement (Detail.BandLayout.FloorLatch): true while the band lives in the Zune band's row 2.
        readonly Signal<bool> _floorInRow2 = new(Detail.BandLayout.InRow2(Shell.Ui.PresentedNavStyle.Peek()));
        bool _inRow2;                      // the value Compose latched this render; every other reader uses this
        float _flipHeroH = ArtistHeroLayout.WideHeight;
        /// <summary>EXPERIMENTAL (artist bleed): non-null when this render publishes a backdrop; true while the shell slot actually
        /// carries it, so the hero hides its own photo only while the shell draws one (never blank when the publish was dropped).</summary>
        Func<bool>? _bleedDrawn;
        Memo<bool>? _belowLine;            // offset < FlipLine: a threshold, so the latch subscribes to the crossing only
        IReadSignal<bool>? _isActive;
        string[]? _bandLabels;
        int _bandLabelsHash;
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
        readonly Action _play, _shuffle, _radio, _scrollToTop;
        readonly Action _latchFloor, _publishBand;
        readonly Func<bool> _belowLineFn;
        readonly Action<int> _onBandPivot;
        readonly Func<Element> _bandActions;
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
            _scrollToTop = ScrollToTop;
            _latchFloor = LatchFloor;
            _publishBand = PublishBand;
            _belowLineFn = () => _scroll.Offset.Value < Detail.BandLayout.FlipLine(_flipHeroH);
            _onBandPivot = i =>
            {
                if ((uint)i < (uint)_pivotCount) _sectionClicks[(int)_pivot[i]]();
            };
            _bandActions = BandActions;
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
            // Version/Readiness values Render and Compose actually paint (the PUSH — Memo's push-pull equality
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
            UseEffect(_publishTheme, DepKey.From((int)Tok.Theme));
            UseEffect(_resolveSpy);                                       // the scroll spy
            // THE FLOOR LATCH and the Zune row 2 (A2). Every hook runs unconditionally: Compose is not always reached.
            _isActive = UseIsActive();
            _belowLine = UseComputed(_belowLineFn);
            UseSignalEffect(_latchFloor);
            UseActivation(onActivated: _publishBand);

            // ch 08 §6's settings, each subscribing the appearance epoch.
            bool washes = Prefs.Appearance.SurfaceWash() != WashLevel.Off;
            _classic = Prefs.Appearance.TrackRowStyle() == 1;
            _showArtwork = !Prefs.Appearance.TrackArtworkHidden();

            _ = _layoutEpoch.Value;
            _width = MathF.Max(1f, _heroWidth.Value);
            _metrics = ArtistHeroLayout.For(_width, _tier);
            _tier = _metrics.Tier;

            _ready = ArtistReadiness.Overview(a);
            bool overviewPending = a.IsValid
                && ((scope.Artists.Asked[a.Slot] & (uint)ArtistFields.Overview) != 0 || scope.Artists.Inflight[a.Slot] != 0);
            _failed = !a.IsValid || ArtistSections.PageFailed(_ready, overviewPending, e.ArtistPopular.Readiness(a.Slot));

            string routeKey = p.RouteKey;
            var paletteSource = PaletteSourceOf(a);   // the tint and the page accent read this ONE pair
            string? paletteUrl = paletteSource.Url;
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
                dw = Design.ImageDecodeScale.For(baseW, scale);
                dh = Math.Max(1, Design.ImageDecodeScale.For(baseH, scale));
            }
            var heroImage = UseImage(heroBind, dw, dh, ImagePriority.Visible, blurHash: null);
            if (heroBind.Length > 0)
                heroImageReady = heroImage.State is ImageState.Ready or ImageState.Failed;

            // A 0-size leaf: the watch, the tone and the claim live there, never in this render. Gated on the incoming
            // art being USABLE (ch 08 BUG D) — never on Knows(Overview): a claim whose cover is already graded (the
            // search/home card's avatar, warmed by an earlier batch) must land its colour on the first paint, and only
            // a genuinely ungraded cover falls through to TintOwnership's hold.
            bool artUsable = Detail.CoverLatch.IsUsable(paletteUrl);

            // ONE visual swap: overview + measured width + the first hero decode + chart settled. Snap, not FadeOnly:
            // waiting for the bitmap then fading the whole tree from 0 hid the photo again (stillwrong.mp4).
            bool chartSettled = ArtistReadiness.ChartSettled(ArtistReadiness.Chart(a), ArtistReadiness.ChartFailed(a));
            _bodyReady = ArtistReadiness.BodyReady(_ready, _measured, _heroGateOpened, chartSettled);
            if (_bodyReady) _heroGateOpened = true;

            // EXPERIMENTAL (artist bleed; Artist.Bleed.cs): the photo runs from the window top behind the chrome. The ONE read of
            // `ArtistBleed.Enabled`. Only once the body is up, so the skeleton never draws over a photo it does not show. The
            // tint's publish carries the backdrop as data; the shell draws it. `floor` and the collapse distance are the hero's own
            // (A2's latched floor), so the shell's ground and fade track the hero 1:1. For a stacked tier the bleed ends where the
            // PHOTO ends (the identity column below it stays on the solid ground), so the hero height it rides is the photo's.
            bool bleed = ArtistBleed.Applies(ArtistBleed.Enabled, Prefs.Appearance.SurfaceWash(), _bodyReady ? heroUrl : null);
            ShellBackdrop? backdrop = null;
            if (bleed)
            {
                float bleedFloor = Detail.BandLayout.StuckHeight(_floorInRow2.Value);
                float bleedPhotoH = ArtistHeroLayout.PhotoHeightFor(_metrics);
                backdrop = new ShellBackdrop(heroUrl!, bleedPhotoH, bleedPhotoH, bleedFloor, _scroll.Offset,
                    ArtistHeroLayout.CollapseDistance(_metrics.MinHeight, bleedFloor), dw, dh, a.Uri.Text);
            }
            Element tint = Palette.ShellTint(paletteUrl, ready: artUsable, disabled: !washes, apply: true,
                owner: _tintOwner, slot: shellSlot, key: "artist-tint:" + routeKey, fallbackUrl: paletteSource.FallbackUrl,
                backdrop: backdrop);
            string bleedKey = a.Uri.Text;
            _bleedDrawn = bleed ? () => shellSlot?.Value.Backdrop?.Key == bleedKey : null;
            _body = _bodyReady ? Compose(a, paletteUrl, _heroUrl, washes, routeKey) : null;
            UseEffect(_bumpPivot, DepKey.From(_pivotHash, (int)scopeEpoch));   // a new section set → re-resolve the spy
            // Row 2's words: the deps-gated form (PublishBand reads no signal), keyed on everything the publication carries.
            UseEffect(_publishBand, DepKey.From(HashCode.Combine(_bodyReady, _bandLabelsHash, a.IsValid ? a.Name : null, routeKey)));
            UseSignalEffect(_watchScroll);

            // ONE region, and the shimmer is DERIVED — `PageShimmer` is a ShimmerSource (a representative tree at the
            // loaded page's geometry), never a tree to mount. Rendering it directly painted its own placeholder copy
            // as if it were data: "Artist name", "A first sentence of the biography stands in this line.",
            // "10,000,000 Monthly listeners", "Top track title" (evenworsenow.mp4).
            //
            // Reveal.Soft - the page-level reveal the rest of the app uses (Concert.Page's Region): the whole tree
            // blur-rises in as ONE (Opacity + TranslateY + Blur -> rest) over the shimmer dissolving out beneath it.
            // Not None, which means "the content animates its own entrance" - and this content has none, so the swap
            // landed as a hard cut with nothing moving at all (shitttt.mp4).
            //
            // The reveal is safe to animate now in a way it was NOT before, which is the whole point of the gate
            // above: `BodyReady` has already waited for the measured width, the DECODED hero bitmap and a settled
            // chart, so the tree that rises is complete. What broke earlier (stillwrong.mp4) was revealing at
            // overview-readiness and animating a tree whose photo had not landed - the header appeared, then vanished
            // behind its own second fade. Group null, not routeKey: the chart's inner region (Artist.UI.Chart.cs) must
            // NOT join this one, or top tracks plays a second wave after the page has already revealed.
            Element region = new SkelRegionEl(
                Pending: _pendingFn, Failed: _failedFn, Content: _contentFn, ShimmerSource: _shimmerFn,
                OnFailed: _failedPanelFn, Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default,
                Group: null, SmoothResize: false);

            Element scroll = ScrollView(new BoxEl
            {
                Direction = 1,
                // Keyed CHILD (a key on a single content root is inert): a scope switch remounts the sections, whose
                // anchors re-register after the render-time reset above.
                Children = [new BoxEl { Key = "artist-body:" + scopeEpoch.ToString(CultureInfo.InvariantCulture), Direction = 1, Children = [region] }],
            }) with
            {
                Key = "artist-scroll:" + routeKey, Grow = 1f, ScrollKey = UseContext(Shell.PageScrollScope) + routeKey,
                // The spy resolves against, and a pivot click scrolls, THIS viewport — never a nested scroller.
                OnRealized = _captureViewport,
                // The band itself is the occlusion cue; the default colour cue resolved the wrong surface.
                EdgeCues = ScrollEdgeCues.None,
                Handle = _scroll,
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
            bool inRow2 = _inRow2 = _floorInRow2.Value;   // the LATCHED floor placement, read once for the whole compose
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
            // Everything after Compilations collapses into ONE "Details" tab: only the first present member of
            // ArtistSections.IsDetailsGroup becomes a destination (Biography is unconditional, so Details always
            // shows), the rest still render in place but without their own pivot entry.
            var sections = new Element[n];
            int pivots = 0, hash = 17;
            bool detailsAssigned = false;
            for (int i = 0; i < n; i++)
            {
                var s = _plan[i];
                // EVERY section keyed: sections appear mid-stream as data lands, and keyless siblings would pair against
                // their neighbours' old subtrees (ArtistPage.cs:213-221).
                Element body = SectionBody(s, a, accent, fans, related ? a.RelatedSlots.Length : fanCount) with { Key = s_secKeys[(int)s] };
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
                if (!isDest) { sections[i] = body; continue; }
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

            // The floor: 56 with the band in the page, 0 once it lives in row 2. The ONE input below is the LATCHED value.
            float floor = Detail.BandLayout.StuckHeight(inRow2);
            _flipHeroH = m.MinHeight;
            float collapse = ArtistHeroLayout.CollapseDistance(m.MinHeight, floor);
            bool compact = _compact.Value;
            if (_bandLabels is null || _bandLabelsHash != _pivotHash || _bandLabels.Length != pivots)
            {
                _bandLabelsHash = _pivotHash;
                var labels = new string[pivots];
                for (int i = 0; i < pivots; i++) labels[i] = pivotItems[i].Label;
                _bandLabels = labels;
            }
            Element band = BandBar(a, width, m.Gutter, collapse, compact, pivotItems, inRow2);
            Element hero = HeroBanner(HeroText.For(a), uri, heroUrl, paletteUrl, width, in m, accent,
                compact, _play, _shuffle, _radio, band, headerAccent: a.HeaderAccent, floor: floor, bleedDrawn: _bleedDrawn);

            // One edge-only hand-off: the sentinel's sticky(56) ENGAGED edge is the band's input switch (`_compact`). The
            // magazine's feather is NOT switched by it (RCA 2026-09-25 F(ii): a re-render off the engaged edge landed two
            // presents after the render-posed clip — `[scroll.engaged.present] ticksAfterCross=2`); it is the clip's own
            // composite-time parameter below (EdgeFadeSpec.WhileStuck).
            Element sentinel = new BoxEl { Height = 0f, HitTestVisible = false }
                .Sticky(floor, engaged: _compact);

            // The blend wash, from the RESOLVED metrics (ch 08 §9 inconsistency #1); a cover-keyed leaf, so a grading
            // re-renders only the wash. Tinted surfaces Off ⇒ it renders nothing; the veil falls to its neutral rung and
            // the Play colour follows 'Accent from artwork' (Detail.AccentFor).
            Element wash = Palette.ArtistBlendWash(paletteUrl, ArtistHeroLayout.BlendBackdropHeightFor(in m),
                ArtistHeroLayout.BlendBoundaryFor(in m), disabled: !washes, key: "artist-wash:" + uri,
                payloadAccent: a.HeaderAccent);

            // THE CLIP IS THE CONTRACT: the band paints nothing, so nothing may render into its 56 DIP — the magazine and
            // the wash are both cut at the line, and the magazine's cut is feathered exactly while its clip is engaged, on
            // the same render turn that poses the cut (EdgeFadeSpec.WhileStuck; nothing is softened at rest). No gate
            // of its own (ch 08 BUG F, restored to 0.2.10's model): the page-level `region` above already keeps the
            // whole body — this magazine included — unmounted until the overview lands, so a second, nested
            // SkelRegionEl here pending on the SAME predicate would never once see Pending() true and would only add a
            // dead group member.
            Element magazine = new BoxEl
            {
                Key = "artist-under-band", Direction = 1,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Top, Detail.BandLayout.ClipFadeBand) { WhileStuck = true },
                Children =
                [
                    new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false },
                    new BoxEl { Direction = 0, Justify = FlexJustify.Center, Children = [Magazine(sections, m.Gutter)] },
                ],
            }.StickyClip(Detail.BandLayout.ClipInsetFor(inRow2));

            return new BoxEl
            {
                ZStack = true,
                Children =
                [
                    new BoxEl { Key = "artist-wash-clip", Direction = 1, HitTestVisible = false, Children = [wash] }
                        .StickyClip(Detail.BandLayout.ClipInsetFor(inRow2)),
                    new BoxEl { Direction = 1, Children = [hero, sentinel, magazine] },
                ],
            };
        }

        /// <summary>The centred magazine column: Grow toward the row's free width, capped at 1600, the hero's gutter on
        /// both sides, a 32 section gap, the page's bottom reserve (PageGeometry.BottomReserve) under the last section (ArtistPage.cs:271-285).</summary>
        static Element Magazine(Element[] sections, float gutter) => new BoxEl
        {
            Direction = 1, Gap = Design.Size.SectionGap,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, MaxWidth = Design.Size.PageMaxW,
            Padding = new Edges4(gutter, Spacing.M, gutter, PageGeometry.BottomReserve),
            Children = sections,
        };

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

        /// <summary>The stuck height under the LATCHED floor placement: the facets pin their headers at it.</summary>
        float Stuck => Detail.BandLayout.StuckHeight(_inRow2);

        /// <summary>One section's body, in the plan's order (ArtistPage.cs:234-269).</summary>
        Element SectionBody(ArtistSection s, Artist a, ColorF accent, int[] fans, int relatedCount) => s switch
        {
            ArtistSection.Popular => TopBand(a, accent),
            ArtistSection.Upcoming => SectionBlock(Loc.Get(Strings.Artist.Upcoming), UpcomingCard(a, _accentFn, wide: false), accent),
            ArtistSection.LatestRelease => SectionBlock(Loc.Get(Strings.Artist.LatestRelease), LatestBanner(a.Latest, accent), accent),
            ArtistSection.Albums => FacetSection(a, DiscoFacet.Albums, _accentFn, Stuck, Stuck + FacetHeaderRowH),
            ArtistSection.Singles => FacetSection(a, DiscoFacet.Singles, _accentFn, Stuck, Stuck + FacetHeaderRowH),
            ArtistSection.Compilations => FacetSection(a, DiscoFacet.Compilations, _accentFn, Stuck, Stuck + FacetHeaderRowH),
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

        /// <summary>title · divider · tabs · Play + Follow as WORDS on one baseline (<see cref="Detail.BandCluster"/>), one
        /// hairline, no fill, no avatar, no capsules (ch 08 §0 #4). Everything geometric is the shared band's; the gutter is the
        /// hero metrics' and the content is this page's. Reveals over the last 44 DIP of the collapse with a 4-DIP settle; takes
        /// input only past the sentinel's edge. With the band in Zune's row 2 (<paramref name="inRow2"/>) the SAME outer box
        /// stays (the hero's tree shape never changes) with no children and no input.</summary>
        Element BandBar(Artist a, float width, float gutter, float collapse, bool canHit,
                        (string Label, Action OnClick)[] pivotItems, bool inRow2)
        {
            Element[] kids = [];
            if (!inRow2)
            {
                // No "Overview" pivot tab: the title itself is the way back to the top of the page.
                Element title = new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Shrink = 1f, MaxWidth = Detail.BandLayout.TitleCap, AlignItems = FlexAlign.Center,
                    Cursor = CursorId.Hand, OnClick = _scrollToTop,
                    Children = [Detail.BandTitle(a.Name)],
                };
                // The pivot is the ONLY elastic lane; the title and the actions never drop (W28).
                Element row = Detail.Band(MathF.Min(width, Design.Size.PageMaxW), gutter,
                    Detail.BandCluster(title, Detail.Pivot(pivotItems, _active, _accentFn), BandActions(), Detail.BandLayout.Height));
                kids =
                [
                    // The content sits at the page's 1600 measure; the band's extent stays full-bleed.
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
                ];
            }
            return new BoxEl
            {
                Key = "artist-band", Width = width, Height = Detail.BandLayout.Height, ZStack = true,
                HitTestVisible = canHit && !inRow2, HitTestPassThrough = true,
                Children = kids,
            }.Reveal(ArtistHeroLayout.CompactRevealStart(collapse), collapse - ArtistHeroLayout.CompactRevealStart(collapse),
                Design.Reduced ? 0f : Spacing.XS).Skeletonized(false);
        }

        /// <summary>Play + Follow, the band's action cluster: the in-page band and the Zune band's row 2 both build it here.</summary>
        Element BandActions()
        {
            string uri = _artist.Uri.Text;
            string name = _artist.Name;
            return new BoxEl
            {
                Direction = 0, Gap = Detail.BandLayout.ActionGap, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children =
                [
                    Detail.BandAction(Loc.Get(Strings.Artist.Play), _play, primary: true),
                    Embed.Comp(() => new Controls.FollowTextAction
                        { Uri = uri, Name = name, Height = Detail.BandLayout.ItemHeight, PadX = Detail.BandLayout.ActionPadX })
                        with { Key = "artist-band-follow:" + uri, SkeletonProxy = s_emptyShape },
                ],
            };
        }

        /// <summary>The floor latch (Detail.BandLayout.FloorLatch): adopt the WANTED placement only while the scroll is under
        /// the flip line, where the flip is invisible. Subscribes to the presented nav style and the threshold crossing, never
        /// to every scroll frame.</summary>
        void LatchFloor()
        {
            bool wanted = Detail.BandLayout.InRow2(Shell.Ui.PresentedNavStyle.Value);
            _ = _belowLine?.Value;
            _floorInRow2.SetIfChanged(Detail.BandLayout.FloorLatch(_floorInRow2.Peek(), wanted, _scroll.Offset.Peek(), _flipHeroH));
        }

        /// <summary>Hands the band to the Zune band's row 2 under the route name, only while this page is the active one (a parked
        /// artist must not take the slot's words away from the page on screen). Safe any time and any number of times: the store
        /// bumps its version only when the title, the tabs or the active signal change. It publishes regardless of the latch, so
        /// row 2 is filled from its first frame.</summary>
        void PublishBand()
        {
            if (_isActive is { } act && !act.Peek()) return;
            var labels = _bandLabels;
            var p = _latest;
            if (labels is null || p is null || !_artist.IsValid || !_bodyReady) return;
            PageHead.PublishBand(p.RouteKey, _artist.Name, labels, _active, _onBandPivot, _bandActions, _scrollToTop, _accentFn);
        }

        /// <summary>A pivot click parks the section's top exactly under the band, animated unless reduced motion is on,
        /// through the engine's one bring-into-view seam against the page's OWN viewport (ContextBand.cs:347-362).</summary>
        void GoToSection(int section)
        {
            var scene = Context.Scene;
            var node = _anchors[section];
            if (scene is null || node.IsNull || _viewport.IsNull || !scene.IsLive(node) || !scene.IsLive(_viewport)) return;
            scene.BringIntoView(_viewport, node, align: 0f, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide,
                margin: Detail.BandLayout.StuckHeight(_inRow2));
        }

        /// <summary>The band title's click target: no "Overview" pivot tab, so the title itself is the way back to the
        /// hero. A raw offset, not <see cref="GoToSection"/> — the top of the page has no section anchor to bring in.</summary>
        void ScrollToTop() => _scroll.ScrollTo(0.0, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide);

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
            float band = Detail.BandLayout.StuckHeight(_inRow2);
            int at = Detail.BandLayout.ActiveSection(tops[..n], band, viewportHeight, atEnd);
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
                WaveeLogField.Of("line", (double)Detail.BandLayout.SpyLine(Detail.BandLayout.StuckHeight(_inRow2), viewportHeight)),
                WaveeLogField.Of("vh", (double)viewportHeight),
                WaveeLogField.Of("atEnd", atEnd));
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
            return Responsive.Of(w =>
            {
                bool wide = _topBandWide = ArtistSections.TopBandWide(w, _topBandWide);
                Element tracks = Chart(a, accentSignal);
                if (!pick && !upcomingInRail) return new BoxEl { Direction = 1, MinWidth = 0f, Children = [tracks] };
                Element featured = pick
                    ? PickCard(a, accentFn, horizontal: !wide, _overlay) with { Key = wide ? "featured:pick:rail" : "featured:pick:band" }
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
            }, fallback: MathF.Max(1f, _width - 2f * _metrics.Gutter));
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

        /// <summary>THE RENDER-COST FOLD (ch 08 render-cost fix, part 2): everything Render — and the Compose/SectionBody
        /// helpers it calls INLINE, never a separately-gated child (<see cref="FacetSection"/>'s FacetHost and
        /// <see cref="Controls.FollowTextAction"/>/<see cref="PageUpcomingClock"/> subscribe themselves and are
        /// deliberately NOT folded here) — reads from the entity tables, folded into one value the same shape as
        /// <see cref="RowStamp"/>/<see cref="RowFold"/> (Album.Page.cs) and Shell.PlayerBar.UI.cs's <c>ArtistLineStamp</c>:
        /// read the table's <c>Changed</c> for the WAKE (a publication anywhere in that table must recompute this, so a
        /// real answer for THIS row is never missed), then fold only the SLOT-SPECIFIC <c>Version</c>/count values Render
        /// actually paints for the PUSH (<see cref="Memo{T}"/>'s push-pull equality cut-off: recompute is cheap — a
        /// handful of array reads — and only a value that MOVED re-renders the page).
        ///
        /// <para><b>The dependency table</b> (every datum Render/Compose reads, and the term covering it):</para>
        /// <list type="bullet">
        /// <item>this artist's row (identity, header/hero images, header accent, bio, monthly/followers, the pick, the
        /// pre-release, the latest-release POINTER, the tour text) — <c>RowFold.Row(scope.Artists, slot)</c>. The pick
        /// and pre-release side-slab CONTENT (<see cref="ArtistPickTable"/>/<see cref="ArtistPreReleaseTable"/>) is
        /// written and <c>Applied</c> in the same commit as the row's own pointer (Artist.cs's overview decode), so the
        /// row's <c>Version</c> alone covers it — <c>scope.ArtistPicks</c>/<c>ArtistPreReleases</c>' own <c>Changed</c>
        /// is redundant and is no longer read.</item>
        /// <item>the latest release's OWN fields (<see cref="AlbumFields.DiscoCard"/>, read for <c>Facts.LatestRelease</c>
        /// and painted by <see cref="LatestBanner"/>) — a DIFFERENT table (Albums): <c>RowFold.Row(scope.Albums,
        /// a.Latest.Slot)</c>.</item>
        /// <item>the ten section-presence / facet edges whose ONLY page-level use is <c>ShelfPresent</c>/<c>FacetPresent</c>
        /// — Albums, Singles, Compilations, Gallery, Merch, Cities, Links — <c>Version(slot)</c> alone: <c>ShelfPresent</c>'s
        /// boolean cannot move on a Version-less Readiness flip (a <c>MarkFailed</c> without a structural write) because
        /// Unknown and Failed both read as "not Complete", the same branch either way; <c>FacetPresent</c> reads the
        /// edge's raw <c>State</c>, never <c>Readiness</c>, so it cannot see a failure mark at all.</item>
        /// <item>the four shelves whose CARDS are OTHER rows — AppearsOn (Albums), Playlists (Playlists), Videos
        /// (Tracks), Concerts (Concerts) — the edge's own <c>Version(slot)</c> (which/how many) AND every target row's
        /// <c>Version</c> (a title/cover hydrating after the edge landed, so <see cref="Items"/>'s value-record rebuilds
        /// exactly that shelf's cards next time Compose runs).</item>
        /// <item>Related artists / the "fans also like" fallback — <c>e.ArtistRelated.Version(slot)</c> then every
        /// related artist's <c>Version</c>; when the related edge is genuinely empty (mirrors Compose's own
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

            // Mirrors Compose's OWN `relatedShell` branch exactly (ArtistReadiness.ShelfPresent over the same edge):
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
            _pageAccent.SetIfChanged(Detail.PageAccentOf(accent, routeKey));
        }

        /// <summary>The header's chrome grading, else the avatar's, else the header payload colour, else the ladder's
        /// held/default rungs (<see cref="Detail.AccentFor"/>). Both urls are watched HERE, inside the tracked
        /// effect, so either grading landing re-derives the accent.</summary>
        static ColorF AccentFor(Artist a)
        {
            if (!a.IsValid) return AccentHold.Last ?? Tok.AccentDefault;
            var src = PaletteSourceOf(a);
            string? url = src.Url, avatar = src.FallbackUrl;
            if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
            if (avatar is { Length: > 0 } && !string.Equals(avatar, url, StringComparison.Ordinal)) _ = Palette.Watch(avatar).Value;
            return Detail.AccentFor(url, a.HeaderAccent, fallbackUrl: avatar);
        }

        /// <summary>The artist's ONE artwork entry pair: the header (palette) image, else the avatar. The shell tint and
        /// <see cref="AccentFor"/> both read it, so the chrome tint and every accent role grade from the same entry.</summary>
        static Detail.PaletteSource PaletteSourceOf(Artist a)
            => a.IsValid ? Detail.PaletteSource.ForArtist(Controls.ArtUrl(a.PaletteImageId), Controls.ArtUrl(a.ImageId)) : default;

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
        /// unknown, never a hero-then-magazine two-step. The facet's cells are the grid's own card — the shared surface's
        /// SEED face at <c>Shape.Grid</c> (the discography grid's gap), so a cell is the height of the real card it stands in
        /// for (<c>cell width + 50</c>) and nothing moves when the grid lands. The face IS already the bone description, so
        /// each cell hands it to the deriver as-is (<c>Skel</c>): derived, its aspect-only cover declares no extent and
        /// would collapse to nothing.</summary>
        Element MagazineShimmer()
        {
            var cells = new Element[5];
            for (int i = 0; i < cells.Length; i++)
            {
                Element cell = new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children = [SurfaceParts.Seed(Shape.Grid, float.NaN)],
                };
                cells[i] = cell.Skel(cell);
            }
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
                    new BoxEl { Direction = 0, Gap = DiscoGap, Children = cells },
                ],
            };
            return Magazine([ChartSkeleton(0, _classic, _showArtwork), facet], _metrics.Gutter);
        }

        // ── 2.6 the shelves (Shelves.cs, Discography.cs's appears-on; W19) ──────────────────────────────────────────

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

        Element AppearsOnShelf(Artist a, ColorF accent)
            => ShelfOf(Items(a.AppearsOnSlots, EntityKind.Album, ArtistSections.AppearsOnCap), AlbumCard,
                       Loc.Get(Strings.Artist.AppearsOn), accent, onInvoke: static (item, _) => OpenAlbum(item));

        /// <summary>The album card's click, shared with the shelf slot's <c>onInvoke</c>.</summary>
        static void OpenAlbum(ShelfEntity item) => Track.GoToAlbum(new Album(item.Slot));

        /// <summary>Square cover, title, and the year — or the kind when the year is unknown (parity 56).</summary>
        Element AlbumCard(ShelfEntity item, int index, float w)
        {
            var al = new Album(item.Slot);
            string uri = al.Uri.Text;
            string title = al.Title;
            string? cover = Controls.ArtUrl(al.ImageId);
            string sub = al.Year > 0 ? al.Year.ToString(CultureInfo.CurrentCulture) : Detail.Text.KindLabel(al.Kind);
            var data = new Controls.CardData(uri, title, CardSubtitle(sub), cover,
                OnClick: () => OpenAlbum(item),
                OnPlay: () => Playback.PlayOrToggleContext(al.Id),
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
            return ShelfOf(items, VideoCard, Loc.Get(Strings.Artist.MusicVideos), accent, cardHeight: s_videoHeight,
                           onInvoke: static (item, _) => PlayVideo(item));
        }

        /// <summary>The video card's click and ▶, shared with the shelf slot's <c>onInvoke</c>: it plays the track.</summary>
        static void PlayVideo(ShelfEntity item) => Playback.PlayContext(new Track(item.Slot).Id);

        /// <summary>The 16:9 music-video card: the shared surface's <see cref="Shape.Video"/> (the FAB at rest, the real
        /// now-playing overlay, the corner "…", the hover plate, the hand cursor) over the thumb at 16:9 and the duration
        /// as its caption. It stands for its TRACK — the menu is the track's, the drag carries the track, a click plays it.</summary>
        Element VideoCard(ShelfEntity item, int index, float w)
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

        Element PlaylistsShelf(Artist a, ColorF accent)
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
        static void OpenPlaylist(ShelfEntity item)
        {
            var pl = new Playlist(item.Slot);
            Shell.GoTo(Shell.For(pl.Uri, Entities.Strings.Resolve(pl.TitleId)));
        }

        Element PlaylistCard(ShelfEntity item, int index, float w)
        {
            var pl = new Playlist(item.Slot);
            string uri = pl.Uri.Text;
            string title = Entities.Strings.Resolve(pl.TitleId);
            string sub = Entities.Strings.Resolve(item.Sub);
            string? cover = Controls.ArtUrl(pl.ImageId);
            var data = new Controls.CardData(uri, title, CardSubtitle(sub), cover,
                OnClick: () => OpenPlaylist(item),
                OnPlay: () => Playback.PlayOrToggleContext(pl.Id),
                Drag: Drag.Source(() => new DragPayload(DragKind.Playlist, uri, uri, title, new EntityRef(EntityKind.Playlist, pl.Slot), ArtUrl: cover)))
            {
                Menu = () => Menus.Container(ActionTarget.ForPlaylist(pl.Uri, title), cover, sub),
            };
            return Controls.Surface(data, s_shelfShape, w);
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
                    Design.Type.DenseTitle(name) with
                    {
                        Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                    Design.Type.DenseTitle(price) with { Weight = 700, Color = Tok.AccentTextPrimary, MaxLines = 1 },
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
                       Loc.Get(Strings.Detail.FansAlsoLike), accent, onInvoke: static (item, _) => OpenArtist(item));

        /// <summary>The artist card's click, shared with the shelf slot's <c>onInvoke</c>.</summary>
        static void OpenArtist(ShelfEntity item) => Track.GoToArtist(new Artist(item.Slot));

        Element ArtistCard(ShelfEntity item, int index, float w)
        {
            var ar = new Artist(item.Slot);
            string uri = ar.Uri.Text;
            string name = ar.Name;
            string? image = Controls.ArtUrl(ar.ImageId);
            string sub = Loc.Get(Strings.Search.TypeArtist);
            var data = new Controls.CardData(uri, name, CardSubtitle(sub), image,
                OnClick: () => OpenArtist(item),
                OnPlay: () => Playback.PlayOrToggleContext(ar.Id),
                Circular: true,
                Drag: Drag.Source(() => new DragPayload(DragKind.Artist, uri, uri, name, new EntityRef(EntityKind.Artist, ar.Slot), ArtUrl: image)))
            {
                Menu = () => Menus.Container(ActionTarget.ForArtist(ar.Uri, name), image, sub),
            };
            return Controls.Surface(data, s_shelfShape, w);
        }

        static Element CardSubtitle(string text) => Design.Type.TrackMeta(text) with
        {
            MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

        // ── the card menu (W27) ──

        // Every card on the page (album, playlist, artist, video) carries its menu in the surface's data (`CardData.Menu`);
        // the host attaches the right-click funnel and the corner "…". The container cards' menu is Menus.Container.

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
            }, fallback: MathF.Max(1f, _width - 2f * _metrics.Gutter));
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

        /// <summary>The external links (Workstream B: the "social links" row is the grammar's LINK role — a stock
        /// <c>HyperlinkButton</c>, not a bordered pill). 0.2.9's were boxes with NO click at all (parity 92); they
        /// open now.</summary>
        static Element LinkPills(LinkEdge[] links)
        {
            var pills = new Element[links.Length];
            for (int i = 0; i < links.Length; i++)
            {
                string name = Entities.Strings.Resolve(links[i].Name);
                string url = Entities.Strings.Resolve(links[i].Url);
                pills[i] = HyperlinkButton.Create(name, () => OpenLink(url));
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
                                Design.Type.DenseMeta(CountLabel(cities[i].Listeners)) with { Color = Tok.TextSecondary },
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

    /// <summary>The "just dropped" full-width banner: ONE shared media surface (<see cref="Shape.Row"/> at a 72 cover) with
    /// the eyebrow "Single · date · N tracks" over the title. Contract: the whole row opens the release
    /// (<see cref="Track.GoToAlbum"/>), the hover play affordance toggles its context
    /// (<see cref="Playback.PlayOrToggleContext(EntityId, string)"/>), the "…" is the container menu, and the row is a drag
    /// source. There is no separate View button.</summary>
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
        var id = al.Id;
        var data = new Controls.CardData(uri, title, null, cover,
            OnClick: () => Track.GoToAlbum(album),
            OnPlay: () => Playback.PlayOrToggleContext(id),
            Drag: Drag.Source(() => new DragPayload(DragKind.Album, uri, uri, title,
                new EntityRef(EntityKind.Album, album.Slot), ArtUrl: cover)))
        {
            Eyebrow = Design.Type.Eyebrow(eyebrow) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Menu = () => Menus.Container(ActionTarget.ForAlbum(album.Uri, title), cover, eyebrow),
        };
        return Controls.Surface(data, Shape.Row(72f) with { Plate = PlateKind.Tile });
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
                        Design.Type.DenseMeta(Entities.Strings.Resolve(a.TourSublineId)) with
                        {
                            Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
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
