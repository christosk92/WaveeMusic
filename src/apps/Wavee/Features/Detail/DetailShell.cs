using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The delegates the rail / tracks / trailing builders need. DetailShell hands out ONE mount-stable instance
/// whose delegates are trampolines into a per-render box (DetailShell.LiveHandlersRef), so a copy frozen into a
/// component's ctor still calls this render's closures; only <see cref="Accent"/>, the one non-delegate field, re-mints
/// it. <see cref="Play"/>/<see cref="PlayAll"/>/<see cref="Shuffle"/> act on THIS page's context;
/// <see cref="PlayContext"/> plays an arbitrary uri (the More-by cards). <see cref="Accent"/> is the art-derived (or
/// default) accent colour.</summary>
readonly record struct DetailHandlers(
    Action<int> Play, Action PlayAll, Action Shuffle, Action<string> PlayContext, Action<string, string?> Go, ColorF Accent,
    IReadSignal<DetailTrackSort> Sort, Action<DetailTrackSort> SetSort,
    // List-view controls surfaced by the chrome / header toolbar (search query, advanced local filters, row density).
    Signal<string> Query, IReadSignal<TrackFilterState> Filters, Action<TrackFilterState> SetFilters,
    IReadSignal<int> Density, Action<int> SetDensity,
    // BPM·Key column opt-in (app-wide, persisted). Surfaces that CAN show the column gate on this; the row expander
    // shows tempo/key regardless, so turning it off hides a column rather than hiding the data.
    IReadSignal<bool> TempoColumn, Action<bool> SetTempoColumn,
    // Playlist/queue mutations for THIS context (insert next / append to the queue / add to a user playlist).
    Action PlayNext, Action AddToQueue, Action AddToPlaylist,
    // Open a related album / "Featured on" playlist card. Unlike Go (a bare route flip), these stash the card's partial
    // model first so the destination takes DetailPage's in-place fast path instead of a full skeleton remount. See DetailNav.
    Action<Album> OpenAlbum, Action<PlaylistSummary> OpenPlaylist,
    // A 1-element cell the TrackList fills with "play the VISIBLE (sorted/filtered) order from the top"; the rail's big
    // Play late-binds through it (null until the list mounts → falls back to Play(0)). Optional so other constructions
    // (LibraryPage) compile unchanged.
    Action?[]? PlayAllOverride = null,
    IReadSignal<bool>? MultiSelect = null, Action<bool>? SetMultiSelect = null,
    // Plays (stream-count) column opt-in — the same app-wide/persisted shape as TempoColumn, but only offered by
    // surfaces whose profile does NOT already carry a Plays lane (playlist / Liked; see DetailConfig.PlaysColumnOptIn).
    // Purely visibility: kind 185 hydrates for every list surface regardless (TraitPolicy), so the counts are already
    // there. Optional so the embedded library list — which has no overflow menu to toggle it — constructs unchanged
    // and simply never shows the column.
    IReadSignal<bool>? PlaysColumn = null, Action<bool>? SetPlaysColumn = null);

// ── DIAGNOSTIC ONLY: the detail cover trace ───────────────────────────────────────────────────────────────────────
//
// "The hero cover flashes out then back in, same picture, right after opening — only in the narrow/Hero arm." Pixels
// can't tell us WHY, so every app-side decision point that can change what the hero asks the ImageCache for logs its
// verdict here, and the engine logs the pipeline's side (`[img]` / `[morph]` events via Diag.Sink → WaveeLog).
// SETTLED (2026-08-16, from a live trace): it was the SOURCE url — the live refresh re-mapped the same art under its
// 640px hash (`…0000b273…`) after the page had painted the 300px one (`…00001e02…`), and the vertical hero read the raw
// model. The latch now lives in DetailPage (ImageSource.PreferVisible on every publish, keyed on ImageSource.SameArt), so
// `hero-build`/`editable-cover` lines for one route must show ONE cover id from first paint on. The trace stays: it is
// how the next cover regression gets settled in minutes instead of by bisecting.
//
// The three candidate causes and what to look for:
//   • the SOURCE url changed → two `cover` lines whose `id=` share the last 24 chars but differ in the first 16 (a
//     Spotify image id is 40 hex: 16 size/kind marker + 24 art identity) ⇒ same art, new cache key ⇒ new decode.
//   • the requested DECODE size changed → `decodePx=` differs between two lines for the same route (also a new key).
//   • the node remounted → an engine `[img] unmount` then `[img] pin ... state=None`.
//
// Cost: every call site is guarded by `DetailCoverTrace.On` (WaveeLog's own level gate), so at the default Info level
// nothing is formatted and nothing is allocated. Turn it on via Settings › Logs › Verbose (+ FG_DIAG=1 for the engine half).
static class DetailCoverTrace
{
    public static bool On => WaveeLog.Instance.IsEnabled(WaveeLogLevel.Debug);

    /// <summary>The FULL Spotify image id of a cover url (the segment after <c>/image/</c>) — deliberately not just the
    /// identity tail, because "same art, different size hash" is precisely the hypothesis this trace has to settle.
    /// "-" when there is no url at all.</summary>
    public static string Id(string? url)
        => string.IsNullOrEmpty(url) ? "-" : SpotifyLive.CoverColorPlane.IdSpan(url).ToString();

    public static string Id(Image? image, bool preferLargest = false)
        => image is null ? "null" : Id(ImageSource.UrlFor(image, preferLargest));
}

// The two-column detail scaffold (mounted only once data is Ready, so its lifecycle = the loaded page's lifecycle).
// Owns: the art-derived backdrop wash + accent, the page-scoped shell material tint (set/cleared through the activation
// lifecycle), and the now-playing re-skin epoch. Delegates the rail / track list / trailing to the static builders.
sealed class DetailShell : Component
{
    // The model is a Loadable: the HEADER (cover/title/artist) renders immediately from its current value (the partial
    // preview the Home card had, or the loaded model on a deep link) and updates in place when the full model arrives;
    // the TRACK LIST streams in via the engine's Skel.Region inside TrackList. Connected cover animation is intentionally
    // disabled here so it cannot compete with the route-level page transition.
    readonly Signal<Route> _route;        // read reactively → ONE shell serves successive detail routes (kind/cfg/morphKey re-derived)
    readonly Loadable<DetailModel> _model;
    readonly Signal<DetailHandlers?> _liveHandlers = new(null);   // reactive parent→TrackList props; never freeze accent/actions
    string? _ctxUri;                      // the loaded context uri — the per-context sort key; refreshed each render
    DetailConfig _cfg = DetailConfig.Album;   // derived from route kind + loaded ReleaseKind each render (a re-derive per route, see below)
    readonly object _tintOwner = new();   // identity for race-free last-writer-wins on ShellMaterial (see ShellMaterialState)
    readonly Signal<int> _mode = new(0);  // adaptive layout mode (0 widest), written by OnBoundsChanged
    // The vertical hero's MEASURED height, published back up by TrackList (which owns the measurement). The page-tone
    // plane needs it: it is the band the blurred background extension occupies and where hero-only mode fades out.
    readonly Signal<float> _verticalHeroHeight = new(0f);
    // (There is no page-tone signal any more. The sticky band used to consume one so it could flatten its opaque
    // material over this page's art-derived ground instead of over the neutral Mica reference; the band paints
    // NOTHING now — content is clipped at its lower edge and the tone plane simply shows through — so the tone never
    // has to leave the leaf that resolves it. The hero-only fade needs the plane's own geometry, not its colour.)
    float _measuredW;                     // last measured page width (0 until the first positive bounds)
    float _measuredH;                     // last measured page height — the tone plane's hero-only fade is a fraction of it
    bool _modeInitialized;                // first measurement uses the nominal breakpoints; later vertical crosses hysteresis
    readonly Signal<DetailTrackSort> _sort = new(DetailTrackSort.Default);   // track-list sort, persisted per context (loaded per route)
    readonly Signal<string> _query = new("");                    // filter search query (transient — clears on navigation)
    readonly Signal<TrackFilterState> _filters = new(TrackFilterState.Default);   // local advanced filters (transient)
    readonly Signal<int> _density = new(1);                      // row density 0..3 (app-wide, persisted)
    readonly Signal<bool> _tempoColumn = new(false);             // BPM·Key column opt-in (app-wide, persisted)
    readonly Signal<bool> _playsColumn = new(false);             // Plays column opt-in on playlist/Liked (app-wide, persisted)
    readonly Signal<bool> _multiSelect = new(false);             // ephemeral multi-select mode (clears on navigation)
    readonly IAppSettings? _settings;
    // The mode-0 resizable rail's persisted state, ONE record per DetailRailPolicy.RailScope: the live width + the
    // DRAG-TO-COLLAPSE flag (WP-η) and the two setting keys the drag-end commit writes them to. Per scope, because the
    // four surfaces are independent preferences — the old "playlist, else album" pair made Liked and a podcast show
    // silently share the album's width and collapse flag (and, before that, get no grip at all). `_railFade` is shared:
    // it is a transient in-gesture cue (the resist-zone content fade), never persisted, and only one rail is ever on
    // screen. Seeded 1f = fully opaque.
    readonly RailPrefs _albumRail, _playlistRail, _likedRail, _showRail;
    readonly Signal<float> _railFade = new(1f);
    ActionServices? _actsRef;             // resolved per render; read by the hero cover's drag payload inside RowChildren

    readonly record struct RailPrefs(RailScope Scope, Signal<float> Width, Signal<bool> Collapsed,
                                     SettingKey<float> WidthKey, SettingKey<bool> CollapsedKey);

    public DetailShell(Signal<Route> route, Loadable<DetailModel> model, IAppSettings? settings = null)
    {
        _route = route; _model = model; _settings = settings;
        _albumRail = SeedRail(settings, RailScope.Album, WaveeSettings.DetailAlbumRailWidth, WaveeSettings.DetailAlbumRailCollapsed);
        _playlistRail = SeedRail(settings, RailScope.Playlist, WaveeSettings.DetailPlaylistRailWidth, WaveeSettings.DetailPlaylistRailCollapsed);
        _likedRail = SeedRail(settings, RailScope.Liked, WaveeSettings.DetailLikedRailWidth, WaveeSettings.DetailLikedRailCollapsed);
        _showRail = SeedRail(settings, RailScope.Show, WaveeSettings.DetailShowRailWidth, WaveeSettings.DetailShowRailCollapsed);
    }

    // CLAMP the loaded width to the live grip bounds (the sidebar's seed rule, WaveeShell): the floor moved 220→180 with
    // the collapse detent, and a value written by another build — or a hand-edited store — must never seed raw.
    static RailPrefs SeedRail(IAppSettings? settings, RailScope scope, SettingKey<float> widthKey, SettingKey<bool> collapsedKey)
        => new(scope,
            new(DetailRailPolicy.ClampStored(settings?.Get(widthKey) ?? widthKey.Default, scope)),
            new(settings?.Get(collapsedKey) ?? false),
            widthKey, collapsedKey);

    RailPrefs RailFor(RailScope scope) => scope switch
    {
        RailScope.Playlist => _playlistRail,
        RailScope.Liked => _likedRail,
        RailScope.Show => _showRail,
        _ => _albumRail,
    };

    // Per-context persisted-sort keys (each album/playlist remembers its own sort). Keyed by the context uri so two
    // different lists never share a sort; falls back to the kind when a context has no uri. The column default is a −1
    // "never chosen" sentinel so the fallback can be PER-KIND: playlists/albums open in custom (context) order, Liked
    // Songs opens added-date-newest-first (the Spotify collection default). An explicit user choice persists ≥ 0.
    SettingKey<int> SortColKey() => new("detail.sort.col:" + (_ctxUri ?? _cfg.RailWidth.ToString()), -1);
    SettingKey<bool> SortDescKey() => new("detail.sort.desc:" + (_ctxUri ?? _cfg.RailWidth.ToString()), false);
    DetailTrackSort _defaultSort = DetailTrackSort.Default;   // per-kind fallback, derived each render (DateAdded desc for Liked)

    // Adaptive layout by the page's own width: 0 Wide (full rail) · 1 Mid (rail 224) · 2 Narrow (rail 188, still
    // two-column) · 3 Vertical (rail collapses to a top header, list below). Sized so the right track area keeps a
    // usable width before the vertical switch.
    const int Vertical = DetailLayoutBreakpoints.VerticalMode;
    /// <summary>The two-column arm's "hero region" for the page tone: it has no ONE measured hero (the rail is a
    /// full-height column), so the band the artwork backdrop occupies is the same top fraction the accent wash this
    /// plane replaced already faded out over.</summary>
    const float TwoColumnHeroBandFraction = 0.55f;
    static int ModeFor(float w, int currentMode, bool initialized)
        => DetailLayoutBreakpoints.ModeFor(w, currentMode, initialized);
    static float RailW(int mode, DetailConfig cfg) => mode switch { 0 => cfg.RailWidth, 1 => 224f, _ => 188f };

    // ── the mode-0 resizable rail: drag bounds + the collapse detent (WP-η) ──
    // The drag floor/ceiling per scope live on DetailRailPolicy (pure, tested). SnapThreshold == that floor: at/above
    // it the rail tracks the cursor 1:1; below it the grip RESISTS and the rail's content fades, and only pushing
    // ForcePush further (raw ≈ 136) collapses into the compact identity strip (WP-κ — never to oblivion). Re-opening
    // needs a pull past ReExpand (220) — comfortably above the 136 collapse point, so the rail cannot flicker shut/open
    // at the seam. Feel constants live on SplitterOptions (kit defaults).
    const float RailForcePush = 44f, RailReExpand = 220f;
    // Compact strip width while collapsed (sidebar analog): wide enough for a readable cover + 2-line title, narrow
    // enough that the track list keeps most of the card. Cover = strip − 2× Spacing.S.
    const float RailCompactW = 96f;
    // The grip's hit strip: Splitter.StripW, invisible at rest with a reveal-on-hover indicator. Collapsed keeps a
    // wider 20 because the seam is then also a re-open gesture.
    const float GripStripCollapsedW = 20f;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var settings = _settings ?? svc?.Settings;
        _ = AppearancePrefs.Epoch.Value;
        bool colorWashesDisabled = !(settings?.Get(WaveeSettings.ColorWashesEnabled) ?? true);
        var bridge = UseContext(PlaybackBridge.Slot);
        var acts = UseContext(ActionServices.Slot);   // the page-BODY drop destination (see PageDropTarget)
        _actsRef = acts;                             // …and the hero cover's drag payload, built inside RowChildren
        var libBridge = UseContext(LibraryBridge.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var shellMaterial = UseContext(ShellMaterial.Slot);
        var navPreview = UseContext(NavPreviewStore.Slot);   // in-app card nav stashes a preview → destination reconciles in place

        // ContentHost's SlotKey is tab + route.Name + route.Arg, so every destination gets its OWN DetailShell
        // instance and album→album slides a new page in (KeepAlive parks the old one for Back). This subscription is
        // therefore about the route OBJECT changing under one slot — a nav-preview stash resolving into the real
        // model, not a different album arriving in this instance.
        var route = _route.Value;
        var (kind, id) = DetailPage.ParseDetail(route);
        var raw = _model.Value.Value;                  // subscribe → re-render preview→full (header updates in place)
        _cfg = DetailPage.ResolveConfig(kind, raw);    // release-kind-dependent (album→single) → re-derived as the model loads
        _ctxUri = raw.ContextUri;                      // the per-context sort key, refreshed as the model loads
        _defaultSort = kind == DetailKind.Liked ? new DetailTrackSort(SortColumn.DateAdded, Descending: true) : DetailTrackSort.Default;
        // The cover is latched where the model is PUBLISHED (DetailPage: PreferVisible on the initial merge and on every
        // live refresh — same art at another CDN size keeps the rendition already on screen), so raw.Cover here is
        // already the stable one and every arm (rail, vertical hero, editable cover, tone plane) reads the same url.
        // MorphKey stays NULL, and it is not vestigial — investigated 2026-08-12, left deliberately:
        //
        //   1. It was introduced by 3b80bbcf8 ("…ship engine and shell polish", 2026-07-10), the SAME commit that
        //      removed `UseContext(SharedTransition.Begin)` from this shell, dropped `this.UseSoftReveal(...)`, and cut
        //      the `morph` argument off DetailNav.OpenAlbum/OpenPlaylist. Its stated reason is the class comment above:
        //      ContentHost exclusively owns route-level entrance motion, and a cover fly must not compete with it.
        //   2. Setting it back would change NOTHING, because the forward capture is gone repo-wide. ConnectedAnimation
        //      only fills its pending-snapshot slot from Begin(); CaptureOnLeave is an explicit no-op ("reverse-fly
        //      capture is Phase 6"). SharedTransition.Begin/BeginConfigured have ZERO callers outside AppHost's own
        //      ambient registration — even WaveeShell.ProbeCardNav ignores its `doMorph` argument. No capture ⇒ no
        //      pending snapshot ⇒ Tick65 never seeds a flight. Publishing a key here would only re-register this cover
        //      as a tagged participant and make the pairing LOOK wired while still never flying.
        //   3. The reason from (1) is still live. ContentHost's PageTransition is MotionRecipes.PageSlideForward/Back
        //      (Position|Opacity, Dx = Expressive.DistBase = 8, Enter Opacity 0), and SceneStore.AbsoluteRect INCLUDES
        //      ancestor LocalTransform.Dx — so the dest rect moves every frame of the slide. That drives
        //      ConnectedAnimation.Settle's per-frame RetargetFlight, and the overlay would land on a page still fading
        //      up from 0 (DestReady gates on image decode, not on page opacity).
        //
        // Re-enabling therefore means restoring the whole capture seam (a `morph` action back on DetailNav.OpenAlbum /
        // OpenPlaylist, threaded from HomeCardNav and RecentsPage, plus UseContext(SharedTransition.Begin) here) AND
        // deciding how the fly composes with the page slide — not flipping this null. The source half is already
        // minted through MorphKeys.For (see RecentsPage), so the convention survives for whoever does that work.
        var m = raw with { MorphKey = null };

        // ContentHost exclusively owns route-level entrance motion. Keep this shell free of a second full-page reveal so
        // cold mounts and cached KeepAlive revisits use the same duration and easing.

        // "Is THIS context the one currently playing?" — a cheap O(1) test against a mount-time id set, so the wash /
        // tint use the live-track palette only when the playing track belongs to this page (else neutral / no tint).
        var trackIds = UseMemo(() =>
        {
            var s = new HashSet<string>(m.Tracks.Count);
            for (int i = 0; i < m.Tracks.Count; i++) s.Add(m.Tracks[i].Id);
            return s;
        }, DepKey.FromRef(raw));

        Track? cur = bridge?.CurrentTrack.Value;     // subscribe → re-derive wash/tint on track change (rare)
        // Watch THIS page's stable palette source (not the global epoch): the wash appears the moment this one image is
        // graded, and a scrolling grid's batches never re-render the page. A provider image is gradeable directly.
        // Playlist mosaics/custom covers are not: when no visual-identity scheme already seeded that cover, use the
        // first real track cover instead of waiting forever on an endpoint request that cannot succeed.
        string? coverUrl = m.Cover?.Url;
        var coverArt = Surfaces.SchemeFor(coverUrl);
        // LIKED SONGS: the page ground follows the COVER above it. When a dynamic treatment is composing, the anchor is
        // the treatment's own lead tile — the newest liked cover, the record the artwork actually leads with — instead
        // of whichever track happened to be the first gradeable one in the list. The two are usually the same record;
        // "usually" is how a page ends up toned by a sleeve that appears nowhere on the cover it sits under.
        // Stock composes nothing, so it keeps the generic answer byte for byte. (The style read is safe at page scope:
        // this Render already takes AppearancePrefs.Epoch above, and nothing here is behind an equality-gated memo that
        // would have to carry the value in a snapshot.)
        string? paletteUrl = LikedToneAnchor(m, settings)
            ?? (coverArt is not null || SpotifyLive.CoverColorPlane.CanGrade(coverUrl)
                ? coverUrl
                : FirstGradeableTrackCover(m.Tracks) ?? coverUrl);
        if (!string.Equals(paletteUrl, coverUrl, StringComparison.Ordinal))
            coverArt = Surfaces.SchemeFor(paletteUrl);
        var coverChrome = Surfaces.ChromeSchemeFor(paletteUrl);
        bool thisPlaying = cur is not null && trackIds.Contains(cur.Id);
        // Watch lives in cover-keyed leaves (wash + shell tint) — never in this Render — so a graded batch does not
        // rebuild the rail/track-list tree. While this page is playing, a page with no grading of its own (Liked, a
        // show) falls back to the now-playing track's cover for SchemeFor / leaf Watch.
        string? liveUrl = thisPlaying ? cur?.Image?.Url : null;
        var liveChrome = liveUrl is not null ? Surfaces.ChromeSchemeFor(liveUrl) : null;

        // Accent for Play / row chrome: SchemeFor at this render (cached hits land immediately). Late gradings update
        // the tone/tint leaves paint-only; accent-filled controls refresh on the next natural shell re-render
        // (track change, model ready, theme). The page's GROUND colour itself is owned by CoverPageTonePlane
        // (WaveePalette.PageTone), which is a different axis from this accent — chroma for chrome, a clamped tone for
        // the page.
        var chrome = coverChrome ?? liveChrome;
        // Plane first (the same chrome every album/playlist uses once the cover is graded). Payload second: the Home
        // daylist card already has extractedColors.colorDark, and a daylist cover often never grades — without this
        // the hero Play/countdown/heart stay Tok.AccentDefault blue beside an orange Home card.
        ColorF accent = chrome is { } cp ? WaveePalette.ChromeAccent(cp)
            : m.Accent != 0 ? WaveePalette.ChromeFromPayload(m.Accent)
            : Tok.AccentDefault;

        // Page-scoped shell material tint: a cover-keyed binder leaf (UseEffect publication + UseActivation set/clear +
        // a mount-once unmount clear — the "wash sticks" fix), not a page-scope Watch, so a graded batch repaints one
        // zero-size node instead of rebuilding the rail/track-list tree. It publishes the FLAT arm only (Wash: null) —
        // the three-layer radial wash belongs to Home.
        // Ready: true — match pre-leaf SchemeFor timing: apply as soon as a cover URL is known (preview / cached
        // grading). Gating on modelReady left Home→detail with a cached palette on the bare ground until Ready.
        Element tintBinder = CoverPaletteLeaves.ShellTint(
            paletteUrl, ready: true, colorWashesDisabled, apply: _cfg.TwoColumn, _tintOwner, shellMaterial,
            key: "detail-tint:" + route.Name, fallbackUrl: liveUrl);

        // ── handlers (close over live svc/model; not frozen ctor args) ──
        void Play(int index) { if (m.ContextUri is { } uri && svc is not null) _ = svc.Player.PlayAsync(uri, Math.Max(0, index)); }
        void PlayContext(string uri) { if (svc is not null) _ = svc.Player.PlayAsync(uri, 0); }
        void Shuffle()
        {
            if (m.ContextUri is not { } uri || svc is null) return;
            _ = svc.Player.SetShuffleAsync(true);
            _ = svc.Player.PlayAsync(uri, 0);
        }
        // Add-to-queue / add-to-playlist act on THIS context's tracks (capped batch); each confirms with a toast.
        // No device gate here any more: queueing routes LOCAL when no remote device is active (an idle session starts
        // playing the queued track), and the one case that cannot proceed — no local audio stack — is refused by the
        // controller, which raises the "choose a remote device" toast itself. The old page-level guard predated local
        // playback and turned every idle enqueue into a prompt. Same rule in LibraryPage / the track context menus.
        void AddToQueue()
        {
            int n = DetailQueueActions.AddToEnd(svc?.Player, m.Tracks);
            if (n > 0) Toast.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), new ToastOptions { Severity = InfoBarSeverity.Success });
        }
        void PlayNext()
        {
            int n = DetailQueueActions.PlayNext(svc?.Player, m.Tracks);
            if (n > 0) Toast.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), new ToastOptions { Severity = InfoBarSeverity.Success });
        }
        void AddToPlaylist()
        {
            if (libBridge is null || m.Tracks.Count == 0) return;
            var (plUri, plName) = libBridge.AddToDefaultPlaylist(m.Tracks);
            Toast.Show(Strings.Detail.AddedToPlaylist(plName), new ToastOptions
            {
                Severity = InfoBarSeverity.Success,
                ActionLabel = Loc.Get(Strings.Detail.GoToPlaylist), OnAction = () => go("pl:" + plUri, plName),
            });
        }
        // ── persisted per-context sort: load once at mount, save on every change (must be assigned BEFORE handlers
        // captures SetSort, which closes over `settings`) ──
        UseEffect(() =>
        {
            // Re-keyed per context: load THIS page's persisted sort + density and clear transient search/filters. The
            // key is the CONTEXT URI, not the mount, because the uri arrives with the model — a page mounted from a
            // nav preview settles onto its real context one render later.
            _query.Value = "";
            _filters.Value = TrackFilterState.Default;
            _multiSelect.Value = false;
            if (settings is null) { _sort.Value = _defaultSort; return; }
            int col = settings.Get(SortColKey());   // −1 sentinel = never chosen → the per-kind default (Liked: DateAdded desc)
            _sort.Value = col < 0 ? _defaultSort : new DetailTrackSort((SortColumn)col, settings.Get(SortDescKey()));
            _density.Value = settings.Get(WaveeSettings.RowDensity);
            _tempoColumn.Value = settings.Get(WaveeSettings.TempoColumn);
            _playsColumn.Value = settings.Get(WaveeSettings.PlaysColumn);
        }, _ctxUri ?? "");
        void SetSort(DetailTrackSort s)
        {
            _sort.Value = s;
            settings?.Set(SortColKey(), (int)s.Column);
            settings?.Set(SortDescKey(), s.Descending);
        }
        void SetDensity(int d) { _density.Value = d; settings?.Set(WaveeSettings.RowDensity, d); }   // app-wide
        void SetTempoColumn(bool on) { _tempoColumn.Value = on; settings?.Set(WaveeSettings.TempoColumn, on); }   // app-wide
        // Purely a VISIBILITY switch. Kind 185 rides every list surface's trait bundle unconditionally (TraitPolicy),
        // so an open page's counts are already asked for — there is nothing left to backfill. The toggle-time
        // hydration that used to live here existed only because the bundle was gated on this very setting, and it was
        // a poor patch for it: page-local and fire-and-forget, it could not reach the other lists that gate starved.
        void SetPlaysColumn(bool on)
        {
            _playsColumn.Value = on;
            settings?.Set(WaveeSettings.PlaysColumn, on);   // app-wide
        }

        // SetSort / SetDensity are hoisted local functions; the rail + chrome toolbars read all list-view controls off here.
        // The record below is the LIVE one — its closures see THIS render's svc / model / settings — but nothing
        // downstream holds it. It is parked in a mount-stable box, and the record everyone actually receives
        // (`handlers`) carries trampolines that call through that box. The indirection buys two things:
        //   · IDENTITY. `_liveHandlers` feeds TrackList's rowsSnapshot memo. A freshly-built record every render (fresh
        //     closures ⇒ never equal, and DepKey.FromRef over it is a new identity every render — see DepKey.cs) wrote
        //     that signal on every render, invalidated the memo, and re-rendered EVERY realized track row. That was the
        //     whole-list fanout behind a slow artist→album hop. The record below changes identity only with `accent`.
        //   · CORRECTNESS AT THE FROZEN EDGE. Component ctor args freeze at mount (component-props-contract), so every
        //     builder taking DetailHandlers BY VALUE (the rail's More menu, the vertical hero, AlbumTrailing) used to
        //     freeze one render's closures. Trampolines make even those frozen copies late-bind to the live record.
        // Accent is the one non-delegate field, so it cannot ride the box: the stable record is re-minted (with fresh
        // trampolines — cheap, and rare) exactly when the art-derived accent actually moves, and only then.
        var live = UseRef(new LiveHandlersRef()).Value;
        // TrackList fills [0] with the visible-order play; the rail's Play late-binds through it. Mount-stable (UseRef):
        // a fresh array per render would hand the rail an empty cell every time TrackList had already filled one.
        var playAllOverride = UseRef(new Action?[1]).Value;
        live.Current = new DetailHandlers(Play, () => { var ov = playAllOverride[0]; if (ov is not null) ov(); else Play(0); },
            Shuffle, PlayContext, go, accent, _sort, SetSort,
            _query, _filters, f => _filters.Value = f, _density, SetDensity, _tempoColumn, SetTempoColumn,
            PlayNext, AddToQueue, AddToPlaylist,
            a => DetailNav.OpenAlbum(navPreview, go, a),
            p => DetailNav.OpenPlaylist(navPreview, go, p),
            playAllOverride,
            MultiSelect: _multiSelect, SetMultiSelect: v => _multiSelect.Value = v,
            PlaysColumn: _playsColumn, SetPlaysColumn: SetPlaysColumn);
        // Fields that are ALREADY mount-stable (the view-control signals, the override cell, and the two setters that
        // only poke this shell's own signals) pass straight through; everything that closes over per-render state goes
        // through the box.
        var accentKey = DepKey.From(accent.R, accent.G, accent.B, accent.A);
        var handlers = UseMemo(() => new DetailHandlers(
            i => live.Current.Play(i),
            () => live.Current.PlayAll(),
            () => live.Current.Shuffle(),
            uri => live.Current.PlayContext(uri),
            (uri, name) => live.Current.Go(uri, name),
            accent,
            _sort, s => live.Current.SetSort(s),
            _query, _filters, f => _filters.Value = f,
            _density, d => live.Current.SetDensity(d),
            _tempoColumn, on => live.Current.SetTempoColumn(on),
            () => live.Current.PlayNext(),
            () => live.Current.AddToQueue(),
            () => live.Current.AddToPlaylist(),
            a => live.Current.OpenAlbum(a),
            p => live.Current.OpenPlaylist(p),
            playAllOverride,
            MultiSelect: _multiSelect, SetMultiSelect: v => _multiSelect.Value = v,
            // Trampolined like its siblings: SetPlaysColumn closes over THIS render's `settings`.
            PlaysColumn: _playsColumn, SetPlaysColumn: on => live.Current.SetPlaysColumn?.Invoke(on)), accentKey);
        // TrackList is retained across preview→palette hydration and route reuse. Publish AFTER render (never write a
        // signal from Render) so its accent and context-closing actions arrive through the supported signal path
        // instead of frozen constructor arguments. Keyed on the accent — the only thing that can change the published
        // record — so the write, and the row re-render it costs, happens on a real palette change, not once per render.
        UseEffect(() => _liveHandlers.Value = handlers, accentKey);

        // Viewport-size context signal — resolved UNCONDITIONALLY here (rules of hooks): the positional-hook cursor must
        // see the SAME hook sequence on every render, but the branches below differ (single-column / vertical / two-column),
        // and only the two-column path needs the window height. Reading `.Value` (the subscription) is deferred to that
        // branch so single-column / vertical pages don't take a needless re-render on every resize.
        var viewportSig = UseContextSignal(Viewport.Size);

        // Single-column fallback: just the track table, full width, no rail / no wash.
        if (!_cfg.TwoColumn)
            return Embed.Comp(() => new TrackList(_route, _model, bridge, handlers, liveHandlers: _liveHandlers))
                with { Key = "tracks:single:" + route.Name, DeriveRenderedOutput = true };

        // Adaptive two-column / vertical: measure the page width → mode. Value-gated → re-render only on a breakpoint cross.
        void Measure(RectF r)
        {
            if (r.W <= 0f) return;
            _measuredW = r.W;
            if (r.H > 0f) _measuredH = r.H;
            int md = ModeFor(r.W, _mode.Peek(), _modeInitialized);
            _modeInitialized = true;
            if (md != _mode.Peek()) _mode.Value = md;
        }
        // The window VIEWPORT overstates this page's own width by roughly the shell's nav pane — seed from an ESTIMATE
        // of the page's own content width instead (see DetailLayoutBreakpoints.EstimatePageWidthFromViewport): a
        // window a little above one of ModeFor's breakpoints otherwise composes the WIDE arm (rail / two-column) for
        // its first frame while the real, narrower page can only hold the vertical one, and the very next Measure then
        // flips it — the hero-remount flicker this estimate exists to prevent. Reused below for the vertical hero's
        // own pre-measure width (verticalHeroWSeed) so the two seeds can never disagree.
        float pageWidthEstimate = DetailLayoutBreakpoints.EstimatePageWidthFromViewport(viewportSig.Peek().Width);
        int mode = _mode.Value;   // subscribe → re-render on mode change
        if (!_modeInitialized && _measuredW <= 0f)
            mode = Math.Max(mode, DetailLayoutBreakpoints.InitialModeForViewport(pageWidthEstimate));
        // Self-heal (fail-safe #2, mirroring TrackList's tier clamp): never RENDER a mode wider than the last measured
        // width supports — a stale mode signal would keep the two-column layout at a width where its rail + tracks
        // cannot coexist. Narrower-than-needed is fine; the next Measure widens it.
        if (_measuredW > 0f) { int fit = ModeFor(_measuredW, mode, _modeInitialized); if (fit > mode) mode = fit; }
        // Page-layout preference: "Hero" forces the vertical hero SYSTEM at every width for track pages — the
        // metadata rail is never composed; Automatic keeps the responsive rail↔hero behavior. The override is applied
        // at render time only (the _mode signal keeps tracking the real width, so flipping the setting back reverts
        // instantly). Epoch-subscribed → the Settings toggle re-renders any mounted (incl. KeepAlive-parked) page live.
        _ = DetailHeroPrefs.Epoch.Value;
        if (_cfg.Content == DetailContent.Tracks
            && (settings?.Get(WaveeSettings.DetailPageLayout) ?? DetailVerticalLayout.PageAuto) == DetailVerticalLayout.PageHero)
            mode = Vertical;
        bool verticalTracks = mode == Vertical && _cfg.Content == DetailContent.Tracks;

        // The track list (drops columns by breakpoint, owns the now-playing re-skin + an external SelectionModel). Its
        // view controls (filter / sort / row size) ride a responsive Fluent command bar in the list's OWN chrome (always
        // on for tracks; the rail owns the context actions); the podcast episode toolbar stays in the episode column on
        // wide layouts.
        bool showToolbar = _cfg.Content == DetailContent.Tracks || mode != Vertical;
        float rightMinWidth = DetailLayoutBreakpoints.ContentMinWidthForMode(mode);
        // Right column = the track table OR the episode list (podcast shows). Distinct Keys so an album↔show swap in the
        // reused detail slot remounts the column cleanly instead of reconciling TrackList against EpisodeList.
        // Two-column/transient modes retain the 300-DIP guard because their active fixed-column sets can otherwise
        // overlap during a mid-spring/stale-breakpoint frame. Vertical deliberately uses 0: its tier-6 table is the real
        // ultra-narrow layout and must receive the full available width below 300 DIP instead of forcing an edge clip.
        Element right = _cfg.Content == DetailContent.Episodes
            ? new BoxEl
            {
                Key = "right:eps", Grow = 1f, Shrink = 1f, MinWidth = rightMinWidth, Direction = 1,
                Children = [Embed.Comp(() => new EpisodeList(_model, handlers, showToolbar))
                    with { Key = "episodes:" + route.Name, DeriveRenderedOutput = true }],
            }
            : new BoxEl
            {
                Key = "right:tracks", Grow = 1f, Shrink = 1f, MinWidth = rightMinWidth, MinHeight = 0f, Direction = 1,
                Children =
                [
                    Embed.Comp(() => new TrackList(_route, _model, bridge, handlers, showToolbar,
                        verticalHeader: verticalTracks,
                        verticalHeroHeight: _verticalHeroHeight,
                        verticalHeroWSeed: pageWidthEstimate,
                        liveHandlers: _liveHandlers)) with
                    {
                        // A route is a new scroll/hero identity. Remounting prevents an album→album swap from painting
                        // the previous page's collapsed-header signals for one async frame (the blank/clipped hero bug).
                        Key = (verticalTracks ? "tracks:vertical:" : "tracks:standard:") + route.Name,
                        DeriveRenderedOutput = true,
                    },
                ],
            };

        // ── THE PAGE TONE PLANE ─────────────────────────────────────────────────────────────────────────────────
        // ONE flat art-derived ground for BOTH arms, mounted at the shell root behind everything. It replaces the
        // per-arm alpha washes (the deleted detail-wash leaf): those tinted the TOP of a neutral page, which is a
        // different thing from the page HAVING a tone, and the vertical arm needed a second "immersive" wash recipe
        // stacked on top of it to survive the full-bleed cover that is also gone.
        //
        // MOSTLY MICA, not a painted surface — CoverPageTonePlane owns the alphas (0.20 dark / 0.30 light) and the
        // ratchet that got there (opaque → 0.72 → 0.45/0.90 → here, every step a user report, never reversed). Nothing
        // is sampled from the cover at page scale either: the blurred-artwork band this plane used to carry is deleted,
        // because its loudness tracked the SLEEVE's brightness and made the same page read two ways. See that tombstone.
        //
        // "Limit page color to the hero" is gone (Workstream B): the tone always covers the full page now, never just
        // a band that fades back to neutral. heroBand/pageH stay computed below only because CoverPaletteLeaves
        // .PageTonePlane's Props still carry them (Design/CoverPaletteLeaves.cs, out of this workstream's scope).
        const bool heroOnly = false;
        // The window viewport is the pre-measure stand-in: it is larger than the page (it includes the chrome rows),
        // so the first frame's fade lands slightly low and the first real Measure corrects it.
        float pageH = _measuredH > 1f ? _measuredH : viewportSig.Peek().Height;
        float heroBand = mode == Vertical && verticalTracks
            ? _verticalHeroHeight.Value                          // subscribe → the band settles with the hero's measure
            : pageH * TwoColumnHeroBandFraction;
        // Mounted BEHIND the page in the root ZStack (index 1, before the page at index 2), and it is NOT inside any
        // scroller — so it is exactly what the sticky band's clip exposes: the band's unpainted region shows this
        // plane's tone.
        Element tonePlane = CoverPaletteLeaves.PageTonePlane(
            paletteUrl, liveUrl, colorWashesDisabled, heroBand, pageH, heroOnly,
            key: "detail-tone:" + route.Name + ":" + Tok.Theme);

        // HERO SYSTEM: item 0 owns the expanded identity and the custom retained shy-header morph; the chrome pins below
        // it inside TrackList's overlay. The list remounts only on the two-column↔Hero-system cross.
        if (mode == Vertical)
        {
            Element verticalContent = new BoxEl
            {
                Direction = 1, Grow = 1f, ClipToBounds = true,
                DropTarget = PageDropTarget(m, acts, kind),
                Children = verticalTracks ? [right] : [DetailRail.BuildHeader(m, _cfg, handlers, _model, acts: acts), right],
            };
            // The pinned chrome bar now lives INSIDE TrackList's ZStack overlay so it floats over the list AND the album
            // trailing scroller and never remounts when a query/filter remounts the list.
            Element verticalBody = verticalContent;
            var verticalPage = new BoxEl
            {
                Key = "detail:vertical",
                Direction = 1, Grow = 1f, ClipToBounds = true,
                // The notice strip sits between the header and the list — it is about the PAGE, so it must be read
                // before the content it is qualifying, and it must not scroll away with the rows.
                Children = [DetailNoticeBar.For(_model), verticalBody],
            };
            return new BoxEl
            {
                ZStack = true, Grow = 1f, OnBoundsChanged = Measure, ClipToBounds = true,
                Children =
                [
                    tintBinder,
                    DeferWatcher(),
                    tonePlane,
                    verticalPage,
                ],
            };
        }

        // TWO-COLUMN: a centered, max-width row [rail | right] over a top-anchored art wash (behind both columns). The
        // rail width shrinks with the mode; the track list stays mounted across modes 0/1/2 (same row position).
        // Best-effort fit: the cover stays STRETCHED to the full width (a big hero); the TEXT gives — the title font
        // scales CONTINUOUSLY with the window height, and the description's line cap drops on a short window. Keyed on the
        // WINDOW height (known at mount + identical across navigation), NOT a post-layout measurement — so the title never
        // jumps/flickers on a nav and resizes smoothly. The rail's scrollbar stays the last resort.
        // The config decides (RailResizable / RailScope) and the pure policy gates the mode — no kind test here, so
        // Liked and a podcast show get the same grip and their OWN persisted pair.
        var rail = RailFor(_cfg.RailScope);
        bool resizableRail = DetailRailPolicy.ResizableFor(_cfg.RailResizable, mode);
        // Read the collapse preference UNCONDITIONALLY (a stable subscription) but honour it only where the grip that can
        // undo it exists — mode 0. The responsive mid/narrow modes keep composing their breakpoint rail (224/188), exactly
        // as they already ignore the persisted widths, and the collapsed preference returns when the page is wide again.
        bool railCollapsed = rail.Collapsed.Value && resizableRail;
        float railW = resizableRail ? rail.Width.Value : RailW(mode, _cfg);
        float winH = viewportSig.Value.Height;   // subscribe (only here) → re-fit smoothly on resize (stable per page → no nav jump)
        // TWO RUNGS OF THE RAMP, not a fluid interpolation. The old Clamp(24 + (winH-620)*0.05, 24, 38) produced a
        // different off-ramp size at every window height (24.05, 31.4, 37.2 …), so the page hero was never the same
        // typographic step twice and never matched any other title in the app. A tall window gets TitleLarge (40/52),
        // anything shorter gets Title (28/36) — the same two rungs Ui.Title / Ui.TitleLarge publish. 900px is the
        // breakpoint the old ramp's own comment already treated as "the big end".
        float titleSize = winH >= 900f ? 40f : 28f;
        float titleLineHeight = DetailVerticalLayout.TitleLineHeightFor(titleSize);
        int descLines = winH < 760f ? 3 : 6;
        var row = new BoxEl
        {
            // flex:1 1 0 — the same contract every other growing content container in the chain already declares
            // (WaveeShell's content-side, the ContentHost page wrappers, LibraryPage's columns): Basis=0 makes THIS row's
            // width the AVAILABLE content region rather than its children's intrinsic (rail + full track-table) width, and
            // Shrink=1 + MinWidth=0 let it yield when the rail opens and the region narrows. This is the ONE node on the
            // [rail | right] chain that previously declared neither, so it kept FlexShrink's Yoga-default 0 and its
            // intrinsic width — overflowing the narrowed content card, whose ClipToBounds then hard-cut the right columns
            // mid-glyph ("Plays"→"Pl") instead of the table reflowing to a tighter tier. `right` already shrinks (below);
            // the fix is to let its PARENT shrink so the reduced width actually reaches it.
            Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f, MaxWidth = 1600f,
            AlignItems = FlexAlign.Stretch,
            // The hero/rail column is a SIBLING of the track list, so a drag released over the cover, the title or the
            // actions used to reach no destination at all — the dead zone this closes.
            DropTarget = PageDropTarget(m, acts, kind),
            Children = RowChildren(m, handlers, railW, titleSize, titleLineHeight, descLines, right,
                resizableRail, railCollapsed, rail, settings),
        };
        var twoColumnPage = new BoxEl
        {
            Key = "detail:two-column",
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
            ClipToBounds = true,
            // Column, not row: the notice strip spans the full page ABOVE the [rail | right] pair, because it qualifies
            // both halves (the rail's edit affordances are gone too). The centering the row used to get from this node
            // moved onto the row itself.
            Children =
            [
                DetailNoticeBar.For(_model),
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.Center,
                    Children = [row],
                },
            ],
        };
        return new BoxEl
        {
            ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f,
            OnBoundsChanged = Measure, ClipToBounds = true,
            Children =
            [
                tintBinder,
                DeferWatcher(),
                tonePlane,
                twoColumnPage,
            ],
        };
    }

    /// <summary>The zero-size drag watcher that publishes a model deferred by a live same-list reorder
    /// (<see cref="PlaylistReorderDefer"/>). Mounted next to <c>tintBinder</c> — the shell's existing home for
    /// render-nothing subscribers — so nothing in the page's layout chain can see it.</summary>
    Element DeferWatcher()
        => Embed.Comp(() => new PlaylistReorderDeferWatcher(_model)) with { Key = "detail:reorder-defer" };

    /// <summary>The page-BODY drop destination: "anywhere on this playlist page" → APPEND.
    /// <para>The track list owns insertion at a precise slot, and the engine always picks the NEAREST accepting target,
    /// so this never competes with it — it only catches the area the list does not cover. In the two-column layout that
    /// is the whole hero/rail column (cover, title, description, actions), which was previously a dead zone where a
    /// drag released and simply did nothing.</para>
    /// <para>It is mounted for every TRACKS page with a context, not only editable ones, so a read-only playlist can
    /// explain itself there too instead of staying silent.</para>
    /// <para>It is also TRANSPARENT (<see cref="DropTargetSpec.Transparent"/>) for the two gestures it has no business
    /// judging — see <c>Transparent</c> below.</para></summary>
    DropTargetSpec? PageDropTarget(DetailModel m, ActionServices? acts, DetailKind kind)
    {
        if (_cfg.Content != DetailContent.Tracks || acts is null) return null;
        if (m.ContextUri is not { Length: > 0 } uri) return null;
        string name = m.Title;
        bool editable = PlaylistInlineEdit.Editable(m) && acts.Library is not null;
        // An ALBUM or SHOW page is not a playlist and never will be. Its body target has nothing to offer a resource
        // drag, and "Can't edit this playlist" is not an explanation there — it is an accusation about a thing the
        // user is not even looking at. Stay out of that gesture entirely (B2's latent cousin).
        bool foreignSurface = kind is DetailKind.Album or DetailKind.Show && !editable;

        // A page-body drop can only ever APPEND, so a same-playlist row drag has nothing to do here — and treating it
        // as a copy would duplicate the user's own rows into their own playlist. It is not a REFUSAL though: the
        // destination the user wants is the track list a few DIP away, and the hero/cover/actions column is scenery
        // the drag merely crosses on the way there. A hard not-allowed glyph over that transit (with the scrim
        // already suppressed for a same-list reorder) is an accusation with no direction — so this target sits the
        // gesture out and lets the list own it (B2).
        // The same-list case is kept OUT of the shared decision table: that table answers "may this list take this
        // payload at a slot", and a body drop has no slot to speak of.
        bool SameList(WaveeResourceDragPayload p)
            => p.SourceRows is { Count: > 0 } && string.Equals(p.SourcePlaylistUri, uri, StringComparison.Ordinal);
        PlaylistDropRefusal Verdict(WaveeResourceDragPayload p)
            => PlaylistDropRefusalRules.Evaluate(editable, loading: false, p.CanCopyTracks,
                                                 sameList: false, naturalOrder: true, filtered: false, rowsKeyed: true);

        return Drop.Target<WaveeResourceDragPayload>(WaveeDragKinds.Resource,
            accepts: p => !SameList(p) && Verdict(p) == PlaylistDropRefusal.None,
            transparent: p => SameList(p) || foreignSurface,
            onDrop: (p, _) => WaveeResourceDrop.DepositTracks(acts, uri, name, p, insertionIndex: null),
            caption: _ => Strings.Drag.AddTo(name),
            // The same-list arm is gone with the transparency above: this target never sees that gesture now, and a
            // dead branch here would look like a second, disagreeing opinion about it.
            refusalCaption: p => Verdict(p) switch
            {
                PlaylistDropRefusal.NotEditable => Loc.Get(Strings.Drag.CantEditPlaylist),
                // Locked decision: an artist has no single obvious track set — refuse rather than guess. Future
                // work is to let the USER pick what to deposit (top tracks / a release), not to choose for them.
                PlaylistDropRefusal.NoTracks => p.Kind == WaveeResourceKind.Artist
                    ? Loc.Get(Strings.Drag.CantAddArtist)
                    : Loc.Get(Strings.Drag.NothingToAdd),
                _ => null,
            });
    }

    // The two-column row's children. Collapsed keeps a compact identity strip — `[compact, grip, right]` — so the rail
    // never vanishes (WP-κ). `right` keeps its Key so the track list reconciles in place across the collapse rather than
    // remounting (a remount would reset the scroll offset and the hero morph on every collapse).
    Element[] RowChildren(DetailModel m, DetailHandlers handlers, float railW, float titleSize, float titleLineHeight,
        int descLines, Element right, bool resizableRail, bool railCollapsed, RailPrefs rail, IAppSettings? settings)
    {
        if (railCollapsed)
        {
            void ExpandRail()
            {
                rail.Collapsed.Value = false;
                _railFade.Value = 1f;
                settings?.Set(rail.CollapsedKey, false);
            }
            return
            [
                DetailRail.BuildCompact(m, RailCompactW, ExpandRail),
                DetailRailGrip(rail, settings, collapsedNow: true),
                right,
            ];
        }

        // The fade wrapper is present in EVERY non-collapsed mode (0/1/2), not just the resizable one, so crossing a
        // breakpoint never changes the row's child SHAPE at index 0 — the rail subtree reconciles in place instead of
        // being rebuilt against a wrapper.
        // Height is this wrapper's CROSS axis (it is a row child of [rail | right]). AlignSelf=Stretch takes the
        // row's definite height; AlignItems=Stretch hands that height to DetailRail.Build, whose inner ScrollView
        // grows into it. A Direction=1 wrapper would make height the MAIN axis, and Build's root has no Grow, so
        // the scroller collapsed to 0 and ClipToBounds painted an empty column.
        Element railFaded = new BoxEl
        {
            Key = "detail-rail-fade",
            Direction = 0, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch,
            MinHeight = 0f, Shrink = 0f,
            Width = railW,
            // PAINT-BOUND (WaveeShell's sidebar-fade pattern): the resist-zone cue rides the compositor's opacity
            // channel, so a drag toward the detent never re-renders the rail subtree.
            Opacity = Prop.Of(() => _railFade.Value),
            Children = [DetailRail.Build(m, _cfg, handlers, railW, titleSize, titleLineHeight, descLines, _model, _actsRef)],
        };
        return resizableRail
            ? [railFaded, DetailRailGrip(rail, settings, collapsedNow: false), right]
            : [railFaded, right];
    }

    // Same Splitter as the library columns, with the collapse detent ARMED (WP-η): width writes are direct during
    // drag and committed to settings only on release — to THIS scope's own pair.
    Element DetailRailGrip(RailPrefs rail, IAppSettings? settings, bool collapsedNow) => new BoxEl
    {
        Key = "detail-rail-grip-strip",
        Width = collapsedNow ? GripStripCollapsedW : Splitter.StripW,
        Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Stretch,
        Children =
        [
            Splitter.Create(rail.Width, () =>
                {
                    settings?.Set(rail.WidthKey, rail.Width.Peek());
                    settings?.Set(rail.CollapsedKey, rail.Collapsed.Peek());
                },
                new()
                {
                    Min = DetailRailPolicy.MinWidthFor(rail.Scope), Max = DetailRailPolicy.MaxWidth,
                    ForcePush = RailForcePush, ReExpand = RailReExpand,
                },
                collapsed: rail.Collapsed, fade: _railFade)
                with { Key = "detail-rail-grip:" + rail.Scope },
        ],
    };

    /// <summary>The mutable box the mount-stable <see cref="DetailHandlers"/> trampolines call through. Re-assigned
    /// (with a freshly-closed-over record) every render, so the delegates children froze at mount always run against
    /// the CURRENT svc / model / settings while the record's identity — which gates TrackList's rowsSnapshot memo —
    /// stays put. Plain field, written during Render: it is not a signal, so nothing re-renders off it.</summary>
    sealed class LiveHandlersRef { public DetailHandlers Current; }

    static string? FirstGradeableTrackCover(IReadOnlyList<Track> tracks)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            string? url = tracks[i].Image?.Url;
            if (SpotifyLive.CoverColorPlane.CanGrade(url)) return url;
        }
        return null;
    }

    /// <summary>The page-tone anchor for the Liked collection when a DYNAMIC cover treatment is composing, or null —
    /// "null" meaning "this page has no opinion, use the generic answer".
    ///
    /// <para>Null covers three cases, and all three are the same statement: this is not the liked page; or the liked
    /// page is painting the stock PNG (no style chosen, or the library is still below the style's minimum art — E1/E3),
    /// in which case today's tone is exactly right; or the lead tile is a url the colour service cannot grade, in
    /// which case naming it would leave the page permanently neutral where the generic scan still finds a colour.</para>
    ///
    /// <para>The tile list is the SAME <c>LikedCoverRules.Tiles</c> the cover composes from, so the ground and the
    /// artwork above it can never disagree about which record they are keyed to. It caps at sixteen and stops early,
    /// so a 10k liked list costs a short prefix scan on a page render (never a frame).</para></summary>
    static string? LikedToneAnchor(DetailModel m, IAppSettings? settings)
    {
        if (!DetailRail.IsDynamicLikedCover(m)) return null;
        var requested = AppearancePrefs.LikedCover(settings);
        // Cheap reject first: a style that composes no art at ALL (Stock) never reaches Tiles.
        if (LikedCoverRules.Effective(requested, int.MaxValue) == LikedCoverStyle.Stock) return null;

        var tiles = LikedCoverRules.Tiles(m.Tracks);
        if (LikedCoverRules.Effective(requested, tiles.Count) == LikedCoverStyle.Stock) return null;
        var anchor = LikedCoverRules.ToneAnchorUrl(tiles);
        return SpotifyLive.CoverColorPlane.CanGrade(anchor) ? anchor : null;
    }
}
