// ── Shell/Video.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// the core half of the video surfaces: the docked card's host, the stay-mounted presenter and the PiP gestures, the pop-out
// window's content host, the fullscreen mode, the watch stage, the join fold and the placement rows
//
// Role: UI
// Owner: K
// Wave: 4
// Budget: 1450 lines
// Spec: ch 24 §9
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// ONE PLAYER, TWO PRESENTERS. The main window has a single stay-mounted presenter that Docked, the in-window PiP and
// fullscreen are GEOMETRY modes of; the pop-out is a second HWND and therefore a second element. "Stay-mounted" is
// exactly `MainWindowHole.Mounted`: it holds across those three placements and the immersive cover (which only HIDES it),
// and it ends only when nothing resolves to the main window (video off) or the pop-out has presented its first frame.
// None of that is what keeps the session's state moving: the `MediaPlayer` pumps its own state, position, duration and
// errors with no element mounted (F132); a presenter only binds and places the surface. Every surface here
// binds `Playback.Video.Player` (owner H's decode host) and
// `Video.State` (the one placement value) — none builds a player, none holds a second visibility flag, none issues an
// engine-video command of its own: play/pause/commit go through the session transport (`Playback.*`), a scrub PREVIEW
// through `Playback.Video.Seek(accurate: false)` planned by `Playback.Video.SeekPlanner`. The only engine-video surface
// API used is `MediaPlayerElement` (the element) and the bound player's `NaturalSize` read, so the engine's video
// rework lands behind H's host in one place.
//
// THE RULES THIS FILE IS SHAPED BY (ch 24 §0):
//   1. No LayoutTransition, no Opacity, no offscreen-RT effect, no scale Enter/Exit on ANY ancestor of a video hole.
//      The hole is a DestOut erase against the real back buffer. Docked/PiP/FS share ONE stay-mounted presenter
//      (pinned by `VideoMainWindowHoleTests`); geometry is bound Width/Height/Transform (the PiP pattern). Pop-out is
//      another HWND.
//   2. Main-window hole is one UseVideoSurface. Docked cap is a hollow reservation the stay-mounted presenter FOLLOWS
//      through the engine's `FollowRect` (resolved after layout and the animation tick, so the overlay lands on the
//      reservation's painted rect in the same frame, rail slide included); `DockedSlot` is only the fallback and the log.
//      Exactly one transport per window (`State.Transport`); engine MediaPlayerElement chrome is off.
//   3. The stage key is PLAYER identity only (the binding generation). Host-fullscreen is a live signal, not a key bit.
//   4. A no-player state is the current track's artwork at 0.4 over the letterbox — never a black rectangle — on ALL
//      FOUR surfaces (the pop-out's bare rect was a 0.2.9 defect, W14b). What sits OVER that poster is decided by
//      `Joining.Decide` from the host's EVENTS (`Playback.Video.Phase` / `.FirstFrame` / `.Player`, folded once by
//      `JoinWatch` into `Video.JoinNow`) — never by a timer guess. The ring waits out the join budget and only the
//      budget, a failure draws a different picture from a slow licence, and the ink is the on-media ladder (the 0.2.9
//      `TextOnAccentPrimary` was black-on-black in dark, §4.5).
//   5. Hover chrome costs no signal and no re-render: `Opacity 0 / HoverOpacity 1` under a container that earns hover
//      with a no-op `OnPointerExit`. The on-media transport is `OnMedia.Transport(chromeVisible)`, not the engine kit,
//      and it is driven by the element's idle machine rather than hover — see the header of `OnMedia.UI.cs` for why.
//
// Every decision is `Video.cs`'s (placement, hosting, stage input, persistence, the PiP maths, the scrub preview, the
// join delay, the fullscreen entry) — this file hosts and forwards.
//
// T7c MOVED THE PICTURES TO THE STRUCTURE (`Themes\Classic\Structure\Views\Video\*.cs`). The six keys, all mounted from here with a props
// record (`Theming\Contracts\VideoProps.cs`): `Video.Docked` (the docked card, and the watch page's stage frame), `Video.Pip` (the
// stay-mounted presenter's box, the docked hover strip, the mini player's drag strip, close and eight resize zones), `Video.PopOut` (the
// pop-out window's content and title band), `Video.Fullscreen` (the exit chrome), `Video.JoinPoster` (the poster and the join notice) and
// `Video.PlacementMenu` (the chevron's flyout, mounted by the player bar). WHAT STAYED HERE is everything that is the media pipeline or a
// decision: the engine `MediaPlayerElement` and its keys and handles (`PlayerStage`, `MainVideoArea`), the hollow reservation the presenter
// follows (the realized node, the slot signal, the fit and the log lines), the join fold (`JoinWatch`), the geometry binds, the PiP gestures
// (they run on `PipGesture` and the panel policy's bounds), the pop-out's caption regions and move-loop hold, the fullscreen mode, focus
// scope and Escape / F keys, and the placement rows. A theme draws around and over the hole and never between it and its ancestors.
//
// EVERY HOST HERE IS A [CoreHost] (P3): the boxes on the hole's ancestor chain (the video layer, the presenter's column, the area's stack, the
// stage box), the fullscreen focus-scope root and the empty / zero-size placeholders are built in ONE place, `HoleBoxes` — core on purpose, so
// the hole rule cannot be broken by a theme, and carrying no look (transparent, 0 / Grow, no motion).

using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Video
{
    // ══ 0. MOUNT POINTS ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>How much bottom space the page must keep clear while the mini player sits ANCHORED at its home (its height
    /// plus the gap; 0 once the user places it, or when it is not mounted). The content host insets its bottom by this.</summary>
    public static readonly FloatSignal FloatingSurfaceReserve = new(0f);

    /// <summary>What every surface's video area shows — ONE value, folded from the host's switch events by
    /// <see cref="JoinWatch"/> (mounted once, by <see cref="PipLayer"/>). Read it with <c>.Value</c> inside a render:
    /// it changes long after any surface mounted, so it can only ever reach a child as a signal.</summary>
    public static readonly Signal<JoinVisual> JoinNow = new(JoinVisual.Poster);

    /// <summary>The persistence key of the main window's video presenter (<c>Flow.Persist</c>): a theme switch's new root adopts the
    /// playing picture's subtree under this key instead of re-creating its surface (plan A4.2).</summary>
    public const string PresenterPersistKey = "video.presenter";

    /// <summary>Last laid-out window-DIP rect of the hollow docked reservation: the overlay's FALLBACK while it has no
    /// live reservation to follow (before the first one is realized, across a remount) and the source of the slot log. The
    /// docked overlay itself follows the reservation NODE (<see cref="s_dockedReservation"/>) in the same frame; this
    /// signal is sampled on a layout-bounds edge, so it is a frame late and blind to paint-only motion. An unmount resets it
    /// only when NO successor reservation has registered (docked with nothing mounted collapses, as before); a remount that
    /// has already registered keeps the last rect, so a re-dock no longer drops a 0x0 hole (and a 2x1 stream resize) in.</summary>
    public static readonly Signal<RectF> DockedSlot = new(default);

    /// <summary>The live docked reservation node (UI thread; written at its realize, cleared at its unmount). The overlay's
    /// engine <c>FollowRect</c> answers it while the placement is docked. A freed node reads as "nothing to follow", so a
    /// stale value across a remount is harmless: the overlay then keeps its last placed geometry. ROOT-LIFETIME state
    /// (<see cref="RootScope"/>): the node belongs to the mounted root's tree, so a remounted root starts with none.</summary>
    static readonly RootState<NodeHandle> s_dockedReservationState = new("Video.DockedReservation");
    static NodeHandle s_dockedReservation { get => s_dockedReservationState.Value; set => s_dockedReservationState.Value = value; }

    /// <summary>The overlay's <c>FollowRect</c>: ONE cached delegate (a fresh closure per render would re-diff the box). It
    /// reads the placement with <c>Peek</c> because the engine runs it outside any reactive scope.</summary>
    static readonly Func<NodeHandle> s_followDockedReservation =
        static () => MainWindowHole.FollowsReservation(PlacementCore.Resolve(State.Surface.Peek())) ? s_dockedReservation : default;

    // MOUNT POINT (stage B contract)
    /// <summary>The right rail's ONE docked card (the Cap face), full-bleed at the rail's width, pinned above the header
    /// in every rail body. The rail frame owns the wrapper and its splitter; this is the card, and its own gate makes it a
    /// zero-size box the moment the video is anywhere else.</summary>
    public static Element DockedCap() => Embed.Comp(static () => new DockedSurface(DockedFace.Cap, null));

    // MOUNT POINT (stage B contract) — takes inputs: the page's identity and poster are per-route, the play verb the page's.
    /// <summary>The module watch page's in-page 16:9 stage (the PageStage face). <paramref name="ownerStagePlayable"/> is
    /// the PLAYABLE uri this page would stage (the parked-page discriminator — never the page's own entity uri);
    /// <paramref name="posterUrl"/> the idle poster; <paramref name="onPlay"/> the idle play disc's verb (null ⇒ no disc).
    /// Mounted OUTSIDE the page's section wrapper, skeleton region and scroll view (rule 1).</summary>
    public static Element WatchStage(string ownerStagePlayable, string? posterUrl, Action? onPlay)
    {
        bool Live() => DockedHosting.ShouldMount(DockedFace.PageStage, PlacementCore.Resolve(State.Surface.Value),
            ownerStagePlayable, Shell.Ui.ActiveStagePlayable.Value, PlayingUri());

        return ViewSlots.View(ViewKeys.VideoDocked, new VideoDockedProps
        {
            PageFrame = new VideoPageFrame(posterUrl, onPlay, Live,
                () => Embed.Comp(() => new DockedSurface(DockedFace.PageStage, ownerStagePlayable)) with { Key = "module-stage-video" },
                Shell.DockedVideoMaxH),
        });
    }

    // MOUNT POINT (stage B contract)
    /// <summary>The shell's top-Z video layer: the in-window mini player (a pass-through layer where only the card takes
    /// input), the pop-out window's lifecycle owner (`Video.Host.cs`'s controller leaf), the host observer
    /// (`Video.Host.Wiring.cs`: the session mirror, the availability fold, the badge-lit prefetch) and the join watcher
    /// (the switch events → <see cref="JoinNow"/>) — each mounted exactly once here. It also installs the pop-out's
    /// content and title factories.</summary>
    public static Element PipLayer()
    {
        PopOut.ContentFactory ??= static () => new PopOutRoot();
        PopOut.TitleFactory = static () => CurrentTitle() is { Length: > 0 } title ? title : Loc.Get(Strings.Player.NowPlaying);
        return HoleBoxes.Layer(
            [
                Embed.Comp(static () => new PipSurface()),
                Embed.Comp(static () => new PopOut { Settings = Platform.Settings }),
                Embed.Comp(static () => new HostObserver()),
                Embed.Comp(static () => new JoinWatch()),
            ]);
    }

    // MOUNT POINT (stage B contract)
    /// <summary>The full-bleed fullscreen layer. OS borderless + exit chrome only — the picture lives in
    /// <see cref="PipLayer"/>'s stay-mounted presenter.</summary>
    public static Element FullscreenLayer() => Embed.Comp(static () => new FullscreenHost());

    // MOUNT POINT (stage B contract)
    /// <summary>The placement ladder as the player bar's chevron opens it (Full screen row included, accelerator F11).</summary>
    public static IReadOnlyList<MenuFlyoutItem> PlacementMenu() => PlacementMenu(includeFullscreen: true);

    /// <summary>The ONE builder of the placement rows — the player bar's chevron, its narrow-layout overflow and every
    /// surface's ⋯ share it. Radio-checked against the RESOLVED placement; an unavailable rung is DISABLED with its
    /// reason in the accelerator column, never hidden. "Turn off video" is a deliberate menu choice, so it turns off
    /// directly rather than reporting a host close.</summary>
    public static IReadOnlyList<MenuFlyoutItem> PlacementMenu(bool includeFullscreen)
    {
        var state = State.Surface.Peek();
        var now = PlacementCore.Resolve(state);

        // The TRUTH behind each row, read live at open time rather than off the stale `Available` snapshot: the same
        // source the availability fold reads (`HostObserver.CurrentHasVideo`, not subscribed — a menu that is open
        // does not need to re-render on a kind-99 land) and the live host fact. `Available`/`Resolve` above still
        // drive the radio CHECK (the resolved placement does not change just because the menu opened), but disabled +
        // reason now answer "why", not just "not now".
        bool hasVideo = HostObserver.CurrentHasVideo(subscribe: false);
        var hostCapable = State.HostCapability.Peek();

        MenuFlyoutItem Row(SurfacePlacement p, string labelKey, string icon, string? accelWhenAllowed)
        {
            string? reasonKey = PlacementMenuRules.ReasonKey(hasVideo, hostCapable, p);
            bool allowed = reasonKey is null;
            string? accel = reasonKey is null ? accelWhenAllowed : Loc.Get(reasonKey);
            // FOLD, THEN OPEN (the rail's ShowVideoAt shape). The row was enabled from the LIVE has-video answer above,
            // but the state's stamped Available is None for a session restored mid-track (it is stamped only at a track
            // boundary or a primary click) — committing OpenAt against that stale stamp resolved to None: the video
            // loaded and played with no surface to show it (2026-09-22: audio, an endless join spinner, no window).
            return MenuFlyoutItem.RadioItem(Loc.Get(labelKey), now == p,
                () => PlayerVerbs.Default.SetVideoPlacement(p, hasVideo), icon, allowed) with { AcceleratorText = accel };
        }

        var items = new List<MenuFlyoutItem>(7)
        {
            Row(SurfacePlacement.Docked, Strings.Player.DockInRail, Icons.SplitView, null),
            Row(SurfacePlacement.Floating, Strings.Player.VideoMiniPlayer, Icons.BackToWindow, null),
            Row(SurfacePlacement.Detached, Strings.Player.VideoInSeparateWindow, Icons.Movie, null),
        };
        if (includeFullscreen)
            items.Add(Row(SurfacePlacement.Fullscreen, Strings.Player.VideoFullScreen, Icons.FullScreen, "F11"));
        // Always-on-top is a property of the SEPARATE WINDOW: offered only while that is where the video lives.
        if (now == SurfacePlacement.Detached)
        {
            bool onTop = Prefs.AlwaysOnTop(Platform.Settings);
            items.Add(MenuFlyoutItem.Separator);
            items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Player.VideoAlwaysOnTop), onTop,
                () => Prefs.SetAlwaysOnTop(Platform.Settings, !onTop)));
        }
        if (PlacementCore.IsActive(state))
        {
            items.Add(MenuFlyoutItem.Separator);
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Player.TurnOffVideo), Icons.Cancel, true, State.TurnOff));
        }
        return items;
    }

    // ══ 1. SHARED PIECES ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the stages' transport verbs do. Play/pause/commit are the SESSION transport's; a scrub preview goes to
    /// the host's <c>Playback.Video.Seek(accurate: false)</c>, which plans it against the live session's keyframe table
    /// and buffered ranges — the closest buffered keyframe, and no fetch while the pointer is down (G-153).</summary>
    static readonly Action s_play = static () => Playback.Resume("video.transport");
    static readonly Action s_pause = static () => Playback.Pause("video.transport");
    static readonly Action<TimeSpan, SeekMode> s_seek = static (target, mode) => SeekFromTransport(target, mode);
    static readonly Action s_toggleDetachedFullscreen = static () => State.DetachedFullscreen.Value = !State.DetachedFullscreen.Peek();

    /// <summary>The main window's fullscreen affordance. Constant (props freeze at mount on a stay-mounted presenter),
    /// but it reads the LIVE placement, so F11 / double-click / the element's ⛶ exit fullscreen instead of re-asking
    /// for it — see <see cref="MainWindowHole.FullscreenAffordanceExits"/>.</summary>
    static readonly Action s_toggleMainFullscreen = static () =>
    {
        if (MainWindowHole.FullscreenAffordanceExits(PlacementCore.Resolve(State.Surface.Peek()))) State.ExitFullscreen();
        else State.OpenAt(SurfacePlacement.Fullscreen);
    };

    static void SeekFromTransport(TimeSpan target, SeekMode mode)
    {
        long ms = Math.Max(0L, (long)target.TotalMilliseconds);
        if (mode == SeekMode.Accurate)
        {
            Playback.SeekTo((int)Math.Min(ms, int.MaxValue));
            return;
        }
        Playback.Video.Seek(ms, accurate: Scrub.PreviewAccurate);
    }

    // ── the aspect policy, as the element's controlled signals (seeded from and written back to `Video.Prefs`) ─────────
    static readonly Signal<VideoAspectMode> s_aspect = new(VideoAspectMode.Uniform);
    static readonly Signal<double> s_customRatio = new(AspectPersistence.DefaultCustomRatio);
    static readonly Action<VideoAspectMode, double> s_aspectChanged = static (mode, ratio) => Prefs.SetAspect(Platform.Settings, FromEngine(mode), ratio);

    /// <summary>Re-read the persisted aspect under the video-prefs epoch (a Settings write, or the element's own menu).</summary>
    static void SyncAspect()
    {
        s_aspect.Value = ToEngine(Prefs.Aspect(Platform.Settings));
        s_customRatio.Value = Prefs.CustomRatio(Platform.Settings);
    }

    static VideoAspectMode ToEngine(AspectPreference p) => AspectMapping.ToEngine(p);

    static AspectPreference FromEngine(VideoAspectMode m) => AspectMapping.FromEngine(m);

    // ── what is playing ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The playing PLAYABLE uri (the id space `ActiveStagePlayable` speaks), or null. Subscribes.</summary>
    static string? PlayingUri()
    {
        var id = Playback.CurrentId.Value;
        return id.IsEmpty ? null : id.Text;
    }

    static Track? CurrentTrack()
    {
        var cur = Playback.Current.Value;
        return cur.Kind == EntityKind.Track && !cur.IsNone ? new Track(cur.Slot) : null;
    }

    /// <summary>The current track's title, "" when unknown. Subscribes — to the row AND to the tracks table's
    /// publication, so a title that lands after the pop-out opened or fullscreen mounted re-titles it (G-153).</summary>
    static string CurrentTitle()
    {
        if (CurrentTrack() is not { } t || Entities.Current is not { } scope) return "";
        _ = scope.Tracks.Changed.Value;
        return t.IsValid && t.Knows(TrackFields.Title) ? t.Title : "";
    }

    /// <summary>The current track's artists as one line, "" when unknown. Subscribes like <see cref="CurrentTitle"/>.</summary>
    static string CurrentArtists()
    {
        if (CurrentTrack() is not { } t || Entities.Current is not { } scope) return "";
        _ = scope.Tracks.Changed.Value;
        return t.IsValid && t.Knows(TrackFields.Row) ? Entities.Strings.Resolve(t.ArtistLineId) : "";
    }

    /// <summary>The current track's cover url, or null. Subscribes.</summary>
    static string? CurrentArtUrl() => CurrentTrack() is { } t ? Controls.ArtUrl(t.ImageId) : null;

    /// <summary>A module playable uri is 60+ characters differing only in the tail — a log line keeps the tail.</summary>
    static string Show(string? uri) => string.IsNullOrEmpty(uri) ? "(none)" : uri.Length <= 24 ? uri : "…" + uri[^24..];

    // ── the poster ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The poster's two reads, cached so an equal props record skips the template: the CURRENT track's art (read inside the
    /// poster's OWN render, so a stage whose key survives a source switch never shows the first track's art forever) and the fault's own
    /// line (bound reactively: the reducer types the fault and it can land after the notice mounted).</summary>
    static readonly Func<string?> s_artUrl = static () => CurrentArtUrl();
    static readonly Func<string> s_faultTitle = static () => Loc.Get(Shell.PlayerBarRules.FaultTitleKey(Playback.Error.Value));

    /// <summary>The "no picture yet" composition every surface shares (<c>Video.JoinPoster</c>): the live art over the letterbox and, only
    /// for the two states that have something to say, the notice over it. WHAT it says is <see cref="JoinNow"/>'s (decided by
    /// <c>Joining.Decide</c> from the host's events, never a timer guess); the picture is the theme's. <paramref name="shown"/> collapses
    /// the layer (still mounted) once the live picture is the one thing on screen.</summary>
    static Element Poster(JoinVisual visual, bool shown = true, string? callerKey = null)
        => ViewSlots.View(ViewKeys.VideoJoinPoster, new VideoJoinPosterProps
        {
            Visual = visual, Shown = shown, ArtUrl = s_artUrl, FaultTitle = s_faultTitle,
        }, callerKey);

    /// <summary>The ONE consumer of the host's switch events, and the only clock anywhere in the loading path. It folds
    /// <c>Playback.Video.Phase</c> / <c>.FirstFrame</c> / <c>.Player</c> and the placement into <see cref="JoinNow"/>,
    /// which all four surfaces read — so no surface decides anything about a load and four of them can never disagree.
    /// Mounted once by <see cref="PipLayer"/>; renders nothing.
    /// <para>WHY THE CLOCK IS SHAPED LIKE THIS. It starts on the JOIN's leading edge and runs for the whole join, not
    /// per phase: re-arming at every step would take the ring away again at `Resolving → Licensing`, a flicker inside
    /// the exact two seconds the user called ugly. And it is the join's edge, never the MOUNT: the 0.2.9 overlay armed
    /// a `DepKey.Empty` timeout of its own, so the first join earned the 400 ms and every later one found it already
    /// spent and showed the ring instantly. The clock is the host timer's own (`TimerHandle.NowMs`) so the budget and
    /// the wake that samples it can never disagree — and the headless harness's virtual clock drives both.</para></summary>
    [CoreHost]
    sealed class JoinWatch : Component
    {
        /// <summary>The grace timer's wake. Read by the fold, written by nothing else: the budget is SPENT, not polled.</summary>
        readonly Signal<int> _budget = new(0);

        TimerHandle _grace;
        double _startedMs;               // the timer clock's stamp this join began at — the SAME clock the wake rides
        long _frameEpochWas;             // the last `FirstFrame` value seen, so a BUMP is an edge and not a level
        string _framedKey = "";          // …and the SOURCE that bump belonged to: that source has a picture
        bool _joiningWas;
        (JoinVisual Visual, Playback.Video.SwitchPhase Phase) _logged = ((JoinVisual)255, (Playback.Video.SwitchPhase)255);

        public override Element Render()
        {
            // ONE wake per join — the only reason the fold ever runs when no signal moved. The hook declares the budget;
            // the JOIN re-arms it imperatively below, so nothing about the loading picture waits on a re-render.
            _grace = UseTimeout(() => _budget.Value = _budget.Peek() + 1, Joining.SpinnerDelayMs, DepKey.Empty);

            UseSignalEffect(() =>
            {
                var phase = Playback.Video.Phase.Value;
                var binding = Playback.Video.Player.Value;
                long frame = Playback.Video.FirstFrame.Value;
                int buffered = Playback.Video.Buffered.Value;   // the join's progress heartbeat — it rides the log line
                string key = Playback.Video.Source.Value?.Key ?? "";
                bool wanted = PlacementCore.IsActive(State.Surface.Value);
                _ = _budget.Value;                              // the budget's wake re-runs this fold and nothing else

                // `FirstFrame` bumps once per source; remember WHICH source it bought a picture for. Keying the answer
                // on the source (not on the join) is what keeps a teardown → null-source → new-source sequence ONE join
                // instead of three, each restarting the budget and pushing the ring out by another 400 ms.
                if (frame != _frameEpochWas) { _frameEpochWas = frame; _framedKey = key; }
                bool framed = key.Length > 0 && string.Equals(_framedKey, key, StringComparison.Ordinal);

                var shape = new JoinState(phase, binding.Player is not null, wanted, framed);
                bool joining = Joining.IsJoining(in shape);
                // The budget is earned ONCE per join, on its leading edge: a step from `Resolving` to `Licensing` is the
                // same join and must not take the ring away again.
                if (joining && !_joiningWas)
                {
                    _startedMs = _grace.NowMs;
                    _grace.Restart();
                }
                _joiningWas = joining;

                long elapsed = joining ? (long)Math.Max(0d, _grace.NowMs - _startedMs) : 0L;
                var visual = Joining.Decide(in shape, elapsed);
                JoinNow.Value = visual;                          // equal writes coalesce: a buffer bump wakes nobody

                // ALWAYS-ON, deduplicated on the pair that matters: what is drawn, and why. "Still loading" and "dead"
                // were indistinguishable in the log for the same reason they were indistinguishable on screen.
                if ((visual, phase) == _logged) return;
                _logged = (visual, phase);
                Log.Info(State.LogCategory, $"video join visual={visual} phase={phase} player={shape.PlayerPresent} " +
                    $"wanted={wanted} framed={framed} elapsedMs={elapsed} buffered={buffered} " +
                    $"gen={binding.Generation} key={Show(key)}");
            });

            return HoleBoxes.Machinery();
        }
    }

    // ── the shared stage (the mini player, the pop-out, the fullscreen surface) ─────────────────────────────────────

    /// <summary>What a HOST hands the shared stage: its transport identity and what its fullscreen affordance does (ENTER
    /// from an inline surface, EXIT from the fullscreen surface, TOGGLE the pop-out's own window mode) and, for the
    /// pop-out, the report that its own surface presented (the main presenter's make-before-break release).</summary>
    readonly record struct StageHost(TransportOwner Identity, Action FullscreenRequested, Action? SurfacePresented = null);

    /// <summary>One presenter of the bound player. Props freeze at mount: the host bundle is constant per surface.
    /// Host-fullscreen is a live signal so Docked↔PiP↔FS never remounts the hole. Engine transport is always off.</summary>
    [CoreHost]
    sealed class PlayerStage(StageHost host, IReadSignal<bool> hostFullscreen, Signal<bool> chromeVisible, PlayerChromeFeed feed) : Component
    {
        long _loggedGen = -1;
        NodeHandle _box;
        float _scale = 1f;
        long _mountLoggedFrame;

        /// <summary>F257/F259: ONE line per source this presenter shows a first frame for, carrying every term the ghost A/B
        /// reads: the tier (override = a local file, clear, playready), the natural size, the content size the stream is
        /// worth at this rect and the device rect. dev is the FITTED picture rect (the uniform fit of the natural size
        /// inside the stage box, centred), the rect `content` is computed from, so content == dev reads scale 1.0 exactly as
        /// in the engine's `[video.surface] place` line (an estimate under a non-Uniform aspect mode); box is the raw
        /// stage box. Written when both the first frame and a laid-out rect exist, so a hidden presenter says nothing.</summary>
        void TryLogMount()
        {
            long frame = Playback.Video.FirstFrame.Peek();
            if (frame <= 0 || frame == _mountLoggedFrame) return;
            var scene = Context.Scene;
            if (scene is null || _box.IsNull || !scene.IsLive(_box)) return;
            var r = scene.AbsoluteRect(_box);
            if (r.W < 1f || r.H < 1f) return;
            _mountLoggedFrame = frame;
            var source = Playback.Video.Source.Peek();
            SizeI natural = Playback.Video.Player.Peek().Player?.NaturalSize.Peek() ?? default;
            float s = _scale > 0f ? _scale : 1f;
            var fitted = r;
            if (!natural.IsEmpty)
            {
                float k = MathF.Min(r.W / natural.Width, r.H / natural.Height);
                float fw = natural.Width * k, fh = natural.Height * k;
                fitted = new RectF(r.X + (r.W - fw) * 0.5f, r.Y + (r.H - fh) * 0.5f, fw, fh);
            }
            SizeI content = VideoStreamSizing.BucketedSizeFor(natural, fitted, s, default);   // the bucket the session requests for this rect (F071)
            Log.Info(State.LogCategory, $"[video.mount] owner={host.Identity} placement={PlacementCore.Resolve(State.Surface.Peek())} " +
                $"tier={Playback.Video.MountTier(source)} natural={natural.Width}x{natural.Height} content={content.Width}x{content.Height} " +
                $"dev=({fitted.X * s:0},{fitted.Y * s:0},{fitted.W * s:0},{fitted.H * s:0}) box=({r.X * s:0},{r.Y * s:0},{r.W * s:0},{r.H * s:0}) " +
                $"scale={s:0.##} key={Show(source?.Key)}");
        }

        /// <summary>The strip's reveal: the idle machine's, while this presenter OWNS the transport (`State.Transport`, the placement's owner). In
        /// fullscreen the main window's presenter is Docked by identity and the fullscreen surface owns it (its chrome draws the strip), so this
        /// one stands down.</summary>
        IReadSignal<bool> OwnedChrome() => UseComputed(() => chromeVisible.Value && State.Transport.Value == host.Identity);

        public override Element Render()
        {
            _scale = UseContext(Viewport.Scale);
            UseSignalEffect(() =>
            {
                _ = Playback.Video.FirstFrame.Value;
                TryLogMount();
            });
            // No `hostFullscreen.Value` read here: the element reads HostFullscreen itself (MediaPlayerElement), so a
            // fullscreen edge re-renders only the element that cares, not this stage and its transport strip too.
            var binding = Playback.Video.Player.Value;
            if (binding.Player is not { } player) return HoleBoxes.Vacancy();
            var cursor = StageInput.HidesCursorWindowed(host.Identity) ? CursorAutoHidePolicy.Always : CursorAutoHidePolicy.FullscreenOnly;
            if (_loggedGen != binding.Generation)
            {
                _loggedGen = binding.Generation;
                Log.Info(State.LogCategory, "video presenter mount owner=" + host.Identity
                    + " gen=" + binding.Generation + " hostFs=" + hostFullscreen.Peek()
                    + " engineTransport=" + MainWindowHole.EngineTransportEnabled
                    + " key=player:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return HoleBoxes.Stage(h => _box = h, _ => TryLogMount(),
                [
                    Embed.Comp(() => new MediaPlayerElement
                    {
                        Player = player,
                        Stretch = MediaStretch.Uniform,
                        PlayRequested = s_play,
                        PauseRequested = s_pause,
                        SeekRequested = s_seek,
                        AspectMode = s_aspect,
                        CustomAspectRatio = s_customRatio,
                        AspectModeChanged = s_aspectChanged,
                        PosterContent = Poster(JoinVisual.Poster, callerKey: "live-poster"),
                        // The join poster (Poster(join)) stacks ABOVE this element and the host owns every loading and failure
                        // visual through JoinNow, so the engine's own status overlay would only animate under it (and flash a
                        // "Seeking" pill over the picture on a seek). Off: no ring, timer or tween runs for covered pixels.
                        ShowStatusOverlay = false,
                        AreTransportControlsEnabled = MainWindowHole.EngineTransportEnabled,
                        SuppressTransport = true,
                        FullscreenRequested = host.FullscreenRequested,
                        SurfacePresented = host.SurfacePresented,
                        HostFullscreen = hostFullscreen,
                        CursorAutoHide = cursor,
                        AutoHideTransportControls = !Wavee.Theming.InspectSwitches.HoverChromeAtRest,   // `mount chrome=shown`: the chrome at rest, no auto-hide
                        ChromeVisibleOut = chromeVisible,
                        ChromeFeed = feed,
                    }) with
                    {
                        Key = "player:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                    OnMedia.Transport(OwnedChrome(), feed),
                ]);
        }
    }

    /// <summary>The video element's idle-chrome output — ONE per HWND, because there is one presenter per window. The
    /// element writes it (dwell, leave debounce, scrub / menu / keyboard / window-move holds, the accessibility
    /// override); every strip on that window reads it, so the transport, the pop-out's title band and the cursor all
    /// go away on the same edge. It is not hover: a `HoverOpacity` reveal follows the nearest INTERACTIVE ancestor, and
    /// the pop-out's tree has none — which is why its controls never appeared at all.</summary>
    static readonly Signal<bool> s_mainChrome = new(true);
    static readonly Signal<bool> s_popOutChrome = new(true);

    /// <summary>The engine's activity/pointer/pressed/scrubbing feed for each window's transport — one per HWND, same
    /// split as <see cref="s_mainChrome"/>/<see cref="s_popOutChrome"/> and for the same reason: `ChromeFeed` is an
    /// `init` prop on `MediaPlayerElement`, so it must be a STABLE instance handed in at mount, never a per-render one.</summary>
    static readonly PlayerChromeFeed s_mainFeed = new();
    static readonly PlayerChromeFeed s_popOutFeed = new();

    /// <summary>The track's title, bound by the pop-out's band and the fullscreen band (one cached delegate: an equal props record skips the template).</summary>
    static readonly Func<string> s_currentTitle = static () => CurrentTitle();

    static string GenKey(long generation) => "gen:" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>THE HOLE'S STRUCTURAL BOXES, the one place this file builds an element (P3), so every host below is a <c>[CoreHost]</c> that WVTH008
    /// guards. They stay core on purpose: they are the picture's ANCESTORS (the shell's video layer, the presenter's column, the video area's
    /// stack, the stage box that holds the <c>MediaPlayerElement</c>) or the fullscreen mode's focus-scope root, and the hole rule (no layout
    /// transition, opacity, offscreen effect or scale Enter/Exit on any ancestor of the hole) binds every theme: keeping the chain core means
    /// no theme can put an effect between the picture and the window. They carry no look: no colour but transparent (an erasable, hit-testable
    /// plate), no size but 0 and Grow, no motion. Everything a theme draws around and over the hole is a key (`Video.Pip`, `Video.Docked`,
    /// `Video.PopOut`, `Video.Fullscreen`, `Video.JoinPoster`) placed INSIDE these boxes.</summary>
    static class HoleBoxes
    {
        /// <summary>The shell's top-Z video layer (<see cref="PipLayer"/>): a pass-through ZStack where only the card takes input.</summary>
        public static Element Layer(Element[] children) => new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ZStack = true, HitTestPassThrough = true, Children = children,
        };

        /// <summary>A surface whose mount decision is false (the docked card yielded, the presenter with nothing to present).</summary>
        public static Element Unmounted() => new BoxEl();

        /// <summary>Zero-size machinery (the join fold).</summary>
        public static Element Machinery() => new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

        /// <summary>The player stage's place while no player is bound (the poster stacked over it shows).</summary>
        public static Element Vacancy() => new BoxEl { Grow = 1f, MinHeight = 0f };

        /// <summary>The player stage: the <c>MediaPlayerElement</c> and the on-media transport, clipped, over a transparent plate (the hole erases
        /// it). <paramref name="realized"/> and <paramref name="bounds"/> feed the mount log.</summary>
        public static Element Stage(Action<NodeHandle> realized, Action<RectF> bounds, Element[] children) => new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ZStack = true, ClipToBounds = true, Fill = ColorF.Transparent,
            OnRealized = realized,
            OnBoundsChanged = bounds,
            Children = children,
        };

        /// <summary>The main window's video area: the stage over the poster, always the same ZStack shape (see <see cref="MainVideoArea"/>).</summary>
        public static Element Area(Element[] children) => new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = ColorF.Transparent, Children = children,
        };

        /// <summary>The stay-mounted presenter's pass-through column around the theme's <c>Video.Pip</c> box (which carries the geometry binds).</summary>
        public static Element Presenter(Element pip) => new BoxEl
        {
            Grow = 1f, Direction = 1, HitTestPassThrough = true, Children = [pip],
        };

        /// <summary>The fullscreen layer's pass-through column while fullscreen resolves.</summary>
        public static Element FullscreenLayer(Element surface) => new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            HitTestPassThrough = true,
            Children = [surface],
        };

        /// <summary>The fullscreen mode's root: the focus scope's node, with the Escape / F exit and the "never leave focus null" rule wired on it.</summary>
        public static Element FullscreenRoot(Action<NodeHandle> realized, Action<KeyEventArgs> keyDown, Action<bool> focusChanged, Element chrome) => new BoxEl
        {
            Grow = 1f, Direction = 1, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            HitTestPassThrough = true,
            Focusable = true,
            OnRealized = realized,
            OnKeyDown = keyDown,
            OnFocusChanged = focusChanged,
            Children = [chrome],
        };
    }

    /// <summary>F243: a minimum spacing for a log line that rides a GEOMETRY signal (the docked slot). A window or splitter
    /// resize writes it per frame; the line is for the settled value, not the sweep. A line that passes carries how many
    /// were held back, and <see cref="TryFlush"/> (a trailing timer) writes the held value once the sweep stops, so the
    /// LAST line is always the settled one.</summary>
    public struct GeometryLogGate
    {
        long _lastMs;
        int _held;

        /// <summary>True when a line may be written now; <paramref name="held"/> is how many were dropped since the last one.</summary>
        public bool TryPass(long nowMs, int minGapMs, out int held)
        {
            if (_lastMs != 0 && nowMs - _lastMs < minGapMs)
            {
                _held++;
                held = 0;
                return false;
            }
            _lastMs = nowMs;
            held = _held;
            _held = 0;
            return true;
        }

        /// <summary>The trailing edge: true when lines were held back since the last write, so the caller writes the
        /// settled value now. Nothing pending (the timer's fire at mount, or after a passed line) writes nothing.</summary>
        public bool TryFlush(long nowMs, out int held)
        {
            held = _held;
            if (_held == 0) return false;
            _held = 0;
            _lastMs = nowMs;
            return true;
        }
    }

    // ══ 2. THE DOCKED CARD (Cap + PageStage faces) ═══════════════════════════════════════════════════════════════════

    /// <summary>The docked music-video surface: video that simply LIVES in the app at zero commitment. Two faces, one
    /// card — the rail's Cap (height follows the content's own aspect, splitter-overridable) and the watch page's
    /// PageStage (the page owns the envelope). NO transition, NO shadow, NO corners of its own.</summary>
    [CoreHost]
    sealed class DockedSurface(DockedFace face, string? ownerStagePlayable) : Component
    {
        /// <summary>F243: the docked slot lines are at most one per this many ms, the settled rect always written last (see
        /// <see cref="GeometryLogGate"/>). The fit line is not gated: it follows discrete events, not a per-frame signal.</summary>
        const int GeometryLogGapMs = 250;

        NodeHandle _slot;
        Action<NodeHandle>? _onRealized;   // the reservation's reports, built once (an equal props record skips the template)
        Action<RectF>? _onBounds;
        string? _fittedFor;
        GeometryLogGate _slotGate;
        TimerHandle _slotFlush;
        (string Key, float RailW, int Nw, int Nh, float H, bool Pinned, string Src) _loggedFit;
        (VideoAspectMode Mode, double Custom, TransportOwner Owner) _loggedPolicy = ((VideoAspectMode)255, -1d, (TransportOwner)255);
        (SurfacePlacement Resolved, string? Playing, string Active, bool Mounts) _loggedHost = ((SurfacePlacement)255, " ", " ", false);

        public override Element Render()
        {
            // The Cap follows the CONTENT's aspect at the rail's width: the catalogue's kind-99 size seeds it before any
            // resolve (G-058), the manifest's dims replace that at resolve, and the decoder's report is a CONFIRM
            // (`NaturalSeed`; 16:9 when nothing is known). A splitter drag pins it — for THIS source only.
            UseSignalEffect(() =>
            {
                var src = Playback.Video.Source.Value;
                string sourceKey = src?.Key ?? "";
                var decoded = Playback.Video.Player.Value.Player?.NaturalSize.Value ?? default;
                var catalogue = CurrentCatalogueSize();
                float railW = Shell.Ui.RailWidth.Value;
                if (face != DockedFace.Cap) return;
                // The fit itself — the source change that unpins, the natural size's source, the pinned-or-fitted height —
                // is the pure `DockedFit`; this effect applies it.
                var fit = DockedFit.Decide(new DockedFit.Input(sourceKey, _fittedFor, Shell.Ui.DockedVideoHeightPinned.Peek(),
                    Shell.Ui.DockedVideoHeight.Peek(), railW, decoded.Width, decoded.Height, src?.NaturalWidth ?? 0,
                    src?.NaturalHeight ?? 0, catalogue.W, catalogue.H, Shell.Ui.RailBodyHeight.Peek()));
                if (fit.SourceChanged)
                {
                    _fittedFor = sourceKey;
                    if (Shell.Ui.DockedVideoHeightPinned.Peek()) Shell.Ui.DockedVideoHeightPinned.Value = false;
                }
                string dimSource = fit.DimSource;
                var natural = new SizeI(fit.NaturalW, fit.NaturalH);
                bool pinned = fit.Pinned;
                float height = fit.Height;
                if (fit.WritesHeight) Shell.Ui.DockedVideoHeight.Value = height;
                var now = (sourceKey, railW, natural.Width, natural.Height, height, pinned, dimSource);
                if (now == _loggedFit) return;
                _loggedFit = now;
                Log.Info(State.LogCategory, $"docked cap fit face={face} rail={railW:0.#} natural={natural.Width}x{natural.Height} " +
                    $"height={height:0.##} pinned={pinned} source={dimSource} key={(sourceKey.Length == 0 ? "(none)" : Show(sourceKey))}");
            });

            // ALWAYS-ON policy line: the aspect the element is driven with, and who owns the transport.
            UseSignalEffect(() =>
            {
                var now = (s_aspect.Value, s_customRatio.Value, State.Transport.Value);
                if (now == _loggedPolicy) return;
                _loggedPolicy = now;
                Log.Info(State.LogCategory, $"docked policy face={face} aspect={now.Item1} custom={now.Item2:0.###} " +
                    $"transportOwner={now.Item3} transportSuppressed={now.Item3 != TransportOwner.Docked}");
            });

            // ALWAYS-ON host line: every TERM of the mount decision, not just its outcome ("the rail kept it" looks the
            // same whether the page never claimed it, claimed it with the wrong id, or was correctly outranked).
            var resolved = PlacementCore.Resolve(State.Surface.Value);
            string? playingUri = PlayingUri();
            string activeStage = Shell.Ui.ActiveStagePlayable.Value;
            bool mount = DockedHosting.ShouldMount(face, resolved, ownerStagePlayable, activeStage, playingUri);
            // Deduplicated on the whole tuple, so it writes only when a term moves (it writes no signal).
            var hostTerms = (resolved, playingUri, activeStage, mount);
            if (hostTerms != _loggedHost)
            {
                _loggedHost = hostTerms;
                Log.Info(State.LogCategory, $"docked host face={face} mounts={mount} placement={resolved} " +
                    $"stageHosts={DockedHosting.PageStageHosts(activeStage, playingUri)} owner={Show(ownerStagePlayable)} " +
                    $"active={Show(activeStage)} playing={Show(playingUri)}");
            }

            // Reality: the DERIVED mount, never the placement alone (a yielded rail face is not mounted).
            UseEffect(() => State.ReportLive(SurfacePlacement.Docked, mount), DepKey.From(mount));
            UseEffect(() => () =>
            {
                State.ReportLive(SurfacePlacement.Docked, false);
                // Release OUR reservation only if a newer face has not already registered its own. Then (and only then)
                // is nothing left to cover, so the fallback collapses; a successor keeps the last rect (see DockedSlot).
                // (A cleanup that runs after its root's scope ended sees the next scope's empty slot: that is "nothing registered".)
                if (!_slot.IsNull && (s_dockedReservation == _slot || s_dockedReservation.IsNull))
                {
                    s_dockedReservation = default;
                    DockedSlot.Value = default;
                }
            }, DepKey.Empty);

            // The slot log's trailing edge: re-armed by every held line, so it fires GeometryLogGapMs after the sweep stops.
            _slotFlush = UseTimeout(FlushSlotLog, GeometryLogGapMs);

            if (!mount) return HoleBoxes.Unmounted();

            // The card is the theme's (`Video.Docked`): a hollow reservation the stay-mounted presenter follows, with the Space key.
            _onRealized ??= h => { _slot = h; s_dockedReservation = h; };
            _onBounds ??= _ => PublishDockedSlot();
            return ViewSlots.View(ViewKeys.VideoDocked, new VideoDockedProps
            {
                Card = new VideoDockedCard(face, Shell.Ui.DockedVideoHeight, _onRealized, _onBounds, s_toggleSpace),
            });
        }

        void PublishDockedSlot()
        {
            var scene = Context.Scene;
            if (scene is null || _slot.IsNull || !scene.IsLive(_slot)) return;
            var rect = scene.AbsoluteRect(_slot);
            if (MathF.Abs(DockedSlot.Peek().X - rect.X) < 0.5f
                && MathF.Abs(DockedSlot.Peek().Y - rect.Y) < 0.5f
                && MathF.Abs(DockedSlot.Peek().W - rect.W) < 0.5f
                && MathF.Abs(DockedSlot.Peek().H - rect.H) < 0.5f)
                return;
            DockedSlot.Value = rect;
            if (!_slotGate.TryPass(Environment.TickCount64, GeometryLogGapMs, out int held))
            {
                _slotFlush.Restart();
                return;
            }
            LogSlot(rect, held);
        }

        /// <summary>The timer's fire: the sweep stopped, so write the SETTLED rect if lines were held back.</summary>
        void FlushSlotLog()
        {
            if (!_slotGate.TryFlush(Environment.TickCount64, out int held)) return;
            LogSlot(DockedSlot.Peek(), held);
        }

        void LogSlot(RectF rect, int held) =>
            Log.Info(State.LogCategory, $"docked slot face={face} rect={rect.X:0.#},{rect.Y:0.#} {rect.W:0.#}x{rect.H:0.#}" +
                (held > 0 ? $" held={held}" : ""));
    }

    /// <summary>Space on the focused docked card.</summary>
    static readonly Action s_toggleSpace = static () => Playback.TogglePlay("video.space");

    /// <summary>The docked hover strip's three verbs (the strip itself is the theme's, in `Video.Pip`): each announces its destination.</summary>
    static readonly Action s_toMiniPlayer = static () =>
    {
        Announcer.Say(Loc.Get(Strings.Player.VideoMiniPlayer));
        State.OpenAt(SurfacePlacement.Floating);
    };

    static readonly Action s_toFullscreen = static () =>
    {
        Announcer.Say(Loc.Get(Strings.Player.VideoFullScreen));
        State.OpenAt(SurfacePlacement.Fullscreen);
    };

    static readonly Action s_turnOffDocked = static () =>
    {
        Announcer.Say(Loc.Get(Strings.Player.TurnOffVideo));
        // Sticky off through the model's HOST-CLOSE (it carries the stale-close identity guard).
        State.ReportClosed(SurfacePlacement.Docked);
    };

    /// <summary>The mini player's close button.</summary>
    static readonly Action s_closePip = static () => State.ReportClosed(SurfacePlacement.Floating);

    /// <summary>The mini player's own placement buttons (W-Video): each announces its destination, as the docked strip's do.</summary>
    static readonly Action s_toDocked = static () =>
    {
        Announcer.Say(Loc.Get(Strings.Player.DockInRail));
        State.OpenAt(SurfacePlacement.Docked);
    };

    static readonly Action s_toSeparateWindow = static () =>
    {
        Announcer.Say(Loc.Get(Strings.Player.VideoInSeparateWindow));
        State.OpenAt(SurfacePlacement.Detached);
    };

    // ══ 3. THE IN-WINDOW MINI PLAYER ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the main-window presenter shows: the bound player's stage over the poster. Its OWN component so the
    /// switch's edges (Source, Player, JoinNow — three to five per switch) re-render this small subtree and not the whole
    /// <see cref="PipSurface"/> (its chrome, its eight resize bands, its tooltips and its geometry binds).
    /// <para>The wrapper's SHAPE never changes: always a ZStack of [stage?, poster], the poster KEYED and merely collapsed
    /// (<c>Visible</c> false, which also takes it out of hit-test and paint) once the first frame is up. Swapping
    /// between a ZStack and a bare box at first frame re-laid the stage out on the very frame the hole went live.</para></summary>
    [CoreHost]
    sealed class MainVideoArea : Component
    {
        public override Element Render()
        {
            _ = Playback.Video.Source.Value;
            var binding = Playback.Video.Player.Value;
            var join = JoinNow.Value;
            bool stage = SurfaceMount.ShouldMountPlayerStage(binding.Player is not null);
            var poster = Poster(join, SurfaceMount.PosterShown(stage, join), "poster");
            Element[] kids = stage
                ? new Element[]
                {
                    Embed.Comp(static () => new PlayerStage(
                        new StageHost(TransportOwner.Docked, s_toggleMainFullscreen), State.MainHostFullscreen, s_mainChrome, s_mainFeed))
                        with { Key = "mainstage:" + GenKey(binding.Generation) },
                    poster,
                }
                : new Element[] { poster };
            return HoleBoxes.Area(kids);
        }
    }

    /// <summary>The stay-mounted main-window presenter: docked overlay, in-window PiP, and fullscreen share one
    /// MediaPlayerElement. Geometry lives in bound <c>Transform</c>/<c>Width</c>/<c>Height</c> thunks. The box those binds are put on
    /// and the chrome over the picture are the theme's (<c>Video.Pip</c>); this component keeps the binds, the picture's mount and the
    /// mini player's gestures (<see cref="IVideoPipGestures"/>).</summary>
    [CoreHost]
    sealed class PipSurface : Component, IVideoPipGestures
    {
        readonly Signal<float> _x = new(0f), _y = new(0f);
        readonly Signal<float> _w = new(Pip.DefaultW), _h = new(Pip.DefaultH);

        /// <summary>The mini player's bounds: the active panel policy's (Classic: the 0.2.9 numbers), read at the moment of a gesture.</summary>
        static PipBounds PipBoundsNow => ShellPolicy.Now.Panel.Pip;
        readonly Signal<bool> _placed = new(false);   // false = anchored (tracks the corner, reserves layout)
        bool _sized;                                  // the user sized it (a gesture or a remembered rect)
        float _fitRatio;
        bool _seeded;
        NodeHandle _dragNode;
        readonly NodeHandle[] _bandNodes = new NodeHandle[8];
        IReadSignal<Size2>? _vpSig;
        // Wired ONCE (first render): the geometry binds read everything they depend on, so a re-render of this component
        // (a placement edge) hands the reconciler the SAME thunks and rebinds nothing, as `BarSeekRail` does.
        Prop<float> _widthBind, _heightBind;
        Prop<Affine2D> _transformBind;
        float _startX, _startY, _startW, _startH, _startPx, _startPy;

        public override Element Render()
        {
            // Restore the remembered rect once, before the first layout — a remembered rect is also "deliberately placed".
            if (!_seeded)
            {
                _seeded = true;
                if (PlacementPersistence.TryLoadRect(Platform.Settings.Get(Platform.Keys.VideoPipRect), out float px, out float py, out float pw, out float ph))
                {
                    _x.Value = px; _y.Value = py;
                    _w.Value = MathF.Max(PipBoundsNow.MinW, pw); _h.Value = MathF.Max(PipBoundsNow.MinH, ph);
                    _placed.Value = true;
                    _sized = true;
                }
            }

            var vp = UseContextSignal(Viewport.Size);
            _vpSig = vp;
            // The ONE aspect sync for every presenter (the docked card, the mini player, the pop-out and fullscreen all read
            // `s_aspect` / `s_customRatio`): PipSurface is mounted once for the process by `PipLayer`, so a single effect
            // re-reads the persisted aspect under the video-prefs epoch instead of one per presenter mount.
            UseSignalEffect(static () => { _ = Prefs.Epoch.Value; SyncAspect(); });

            var resolved = PlacementCore.Resolve(State.Surface.Value);
            // The presenter is mounted for EVERY reason the main window may need to pump the session: the immersive stage
            // covering a docked video HIDES it (the bound Visible below) rather than unmounting it, and a pop-out
            // hand-off keeps it mounted (hidden) until the pop-out presents. The Detached-only read keeps every other
            // placement from subscribing to the hand-off signal.
            bool mounted = MainWindowHole.Mounted(resolved, resolved == SurfacePlacement.Detached && State.PopOutPresented.Value);
            bool livePip = resolved == SurfacePlacement.Floating;
            UseEffect(() => State.ReportLive(SurfacePlacement.Floating, livePip), DepKey.From(livePip));
            UseSignalEffect(() =>
                FloatingSurfaceReserve.Value = PipGesture.Reserve(
                    PlacementCore.Resolve(State.Surface.Value) == SurfacePlacement.Floating, _placed.Value, _h.Value,
                    ShellPolicy.Tracked.Panel.Pip));
            UseEffect(() => () =>
            {
                FloatingSurfaceReserve.Value = 0f;
                State.ReportLive(SurfacePlacement.Floating, false);
            }, DepKey.Empty);

            // The HEIGHT follows the content's own aspect (the catalogue's kind-99 size, then the manifest's dims, then the
            // decoder's — `NaturalSeed`, G-058), capped by the free window height; a deliberate size opts out until the
            // content's shape changes.
            UseSignalEffect(() =>
            {
                var decoded = Playback.Video.Player.Value.Player?.NaturalSize.Value ?? default;
                var src = Playback.Video.Source.Value;
                var catalogue = CurrentCatalogueSize();
                NaturalSeed.Pick(decoded.Width, decoded.Height, src?.NaturalWidth ?? 0, src?.NaturalHeight ?? 0,
                    catalogue.W, catalogue.H, out int naturalW, out int naturalH);
                float w = _w.Value;
                float ratio = naturalW > 0 && naturalH > 0 ? (float)naturalH / naturalW : Pip.FallbackRatio;
                if (!PipGesture.ShouldRefit(_sized, _fitRatio, ratio)) { if (_fitRatio <= 0f) _fitRatio = ratio; return; }
                _fitRatio = ratio;
                _h.Value = Pip.FitHeight(w, ratio, vp.Value.Height, ShellPolicy.Tracked.Panel.Pip);
            });

            if (!mounted) return HoleBoxes.Unmounted();

            // What the window can do with the video besides float it: the mini player offers "Dock in rail" where the rail fits, else the
            // separate window (W-Video, -Small). Subscribed: the rail's fit flips it, rarely.
            var capable = State.HostCapability.Value;

            if (!_widthBind.IsBound)
            {
                _widthBind = Prop.Of(() => OverlayWidth(PlacementCore.Resolve(State.Surface.Value), vp.Value, _w.Value, DockedSlot.Value));
                _heightBind = Prop.Of(() => OverlayHeight(PlacementCore.Resolve(State.Surface.Value), vp.Value, _h.Value, DockedSlot.Value));
                _transformBind = Prop.Of(() => OverlayTransform(PlacementCore.Resolve(State.Surface.Value), vp.Value, _placed.Value, _x.Value, _y.Value, _w.Value, _h.Value, DockedSlot.Value));
            }

            // The box, its frame and the chrome over the picture are the theme's (`Video.Pip`); the binds are the engine's geometry.
            return HoleBoxes.Presenter(
                    ViewSlots.View(ViewKeys.VideoPip, new VideoPipProps
                    {
                        Placement = resolved,
                        Visible = s_surfaceVisible,
                        FollowRect = s_followDockedReservation,
                        Width = _widthBind,
                        Height = _heightBind,
                        Transform = _transformBind,
                        Area = s_videoArea,
                        Gestures = this,
                        ToMiniPlayer = s_toMiniPlayer,
                        ToFullscreen = s_toFullscreen,
                        TurnOffDocked = s_turnOffDocked,
                        ClosePip = s_closePip,
                        ToDocked = (capable & PlacementSet.Docked) != 0 ? s_toDocked : null,
                        ToSeparateWindow = (capable & PlacementSet.Detached) != 0 ? s_toSeparateWindow : null,
                    }));
        }

        /// <summary>A BOUND Visible, so covering or releasing the picture re-renders and remounts nothing: the subtree goes inactive, the
        /// element hides its DComp visual and pumps with an inert binding (nothing is placed or shown from this slot; the player's own
        /// state pump never depended on this element, F132), and uncovering re-places the same visual with no new slot.</summary>
        static readonly Prop<bool> s_surfaceVisible = Prop.Of(static () => !MainWindowHole.Hidden(PlacementCore.Resolve(State.Surface.Value), Shell.Ui.ImmersiveLyrics.Value));

        /// <summary>The picture: the keyed video area (the player stage over the poster), one component of its own, PERSISTENT across a
        /// theme switch (<c>Flow.Persist("video.presenter")</c>, plan A4.2): the switch remounts the root, and the new theme's presenter
        /// adopts this very subtree (the MediaPlayerElement, its DComp surface and its binding), re-rendered once against the new
        /// providers, so a playing video never loses its surface or shows a black frame. Without an adopter (the new theme draws no
        /// presenter) it stays parked, then is released (<c>Flow.PersistParkMs</c>).</summary>
        static readonly Func<Element> s_videoArea = static () => Flow.Persist(PresenterPersistKey, static () => Embed.Comp(static () => new MainVideoArea())) with { Key = "video-area" };

        // The overlay's size and offset per resolved placement are the pure `PipOverlay` (PipGesture.cs); these only adapt the
        // engine's value types to it.
        static float OverlayWidth(SurfacePlacement resolved, Size2 vp, float pipW, RectF slot)
            => PipOverlay.Width(resolved, vp.Width, pipW, slot.W);

        static float OverlayHeight(SurfacePlacement resolved, Size2 vp, float pipH, RectF slot)
            => PipOverlay.Height(resolved, vp.Height, pipH, slot.H);

        static Affine2D OverlayTransform(SurfacePlacement resolved, Size2 vp, bool placed, float x, float y, float w, float h, RectF slot)
        {
            var o = PipOverlay.Offset(resolved, vp.Width, vp.Height, placed, x, y, w, h, slot.X, slot.Y, ShellPolicy.Tracked.Panel.Pip);
            return o.Identity ? Affine2D.Identity : Affine2D.Translation(o.X, o.Y);
        }

        Size2 ViewportPeek() => _vpSig?.Peek() ?? new Size2(1280f, 720f);

        /// <summary>Commit the DRAWN position first (an unplaced card would otherwise snap to the origin on the first sample)
        /// and release the reservation: placing it is the user opting into a free-floating overlay.</summary>
        void AdoptDrawnPosition()
        {
            var vp = ViewportPeek();
            float w = _w.Peek(), h = _h.Peek();
            var bounds = PipBoundsNow;
            var (x, y) = _placed.Peek() ? (_x.Peek(), _y.Peek()) : Pip.Anchor(w, h, vp.Width, vp.Height, bounds);
            _x.Value = PipGesture.ClampX(x, vp.Width, w, bounds);
            _y.Value = PipGesture.ClampY(y, vp.Height, h, bounds);
            _placed.Value = true;
            _startX = _x.Peek(); _startY = _y.Peek(); _startW = w; _startH = h;
        }

        public void DragRealized(NodeHandle node) => _dragNode = node;

        public void ZoneRealized(int slot, NodeHandle node) => _bandNodes[slot] = node;

        public void DragDown(Point2 local)
        {
            var scene = Context.Scene;
            if (scene is null || _dragNode.IsNull || !scene.IsLive(_dragNode)) return;
            AdoptDrawnPosition();
            var abs = scene.AbsoluteRect(_dragNode);
            _startPx = local.X + abs.X; _startPy = local.Y + abs.Y;
        }

        public void DragMove(Point2 local)
        {
            var scene = Context.Scene;
            if (scene is null || _dragNode.IsNull || !scene.IsLive(_dragNode)) return;
            var abs = scene.AbsoluteRect(_dragNode);   // the grip moves WITH the surface → reconstruct window space
            var vp = ViewportPeek();
            var bounds = PipBoundsNow;
            _x.Value = PipGesture.ClampX(_startX + (local.X + abs.X - _startPx), vp.Width, _w.Peek(), bounds);
            _y.Value = PipGesture.ClampY(_startY + (local.Y + abs.Y - _startPy), vp.Height, _h.Peek(), bounds);
        }

        public void ZoneDown(int slot, Point2 local)
        {
            var scene = Context.Scene;
            var band = _bandNodes[slot];
            if (scene is null || band.IsNull || !scene.IsLive(band)) return;
            AdoptDrawnPosition();
            _sized = true;   // the user's size is the size now
            var abs = scene.AbsoluteRect(band);
            _startPx = local.X + abs.X; _startPy = local.Y + abs.Y;
        }

        public void ZoneMove(int slot, PipEdge edge, Point2 local)
        {
            var scene = Context.Scene;
            var band = _bandNodes[slot];
            if (scene is null || band.IsNull || !scene.IsLive(band)) return;
            var abs = scene.AbsoluteRect(band);
            var vp = ViewportPeek();
            var r = PipGesture.Resize(edge, _startX, _startY, _startW, _startH,
                local.X + abs.X - _startPx, local.Y + abs.Y - _startPy, vp.Width, vp.Height, PipBoundsNow);
            _x.Value = r.X; _y.Value = r.Y; _w.Value = r.W; _h.Value = r.H;
        }

        /// <summary>One settings write per gesture, and only for a DELIBERATELY placed card (writing the computed anchor
        /// would freeze it against a later window resize).</summary>
        public void Commit()
        {
            if (!_placed.Peek()) return;
            Platform.Settings.Set(Platform.Keys.VideoPipRect, PlacementPersistence.SaveRect(_x.Peek(), _y.Peek(), _w.Peek(), _h.Peek()));
        }
    }

    // ══ 4. THE POP-OUT WINDOW'S CONTENT ══════════════════════════════════════════════════════════════════════════════

    /// <summary>The detached window's root (installed as <see cref="PopOut.ContentFactory"/>). It wraps the content in its
    /// OWN overlay host — a detached window builds its own AppHost, so the shell's never reaches it and every transport
    /// flyout would be a silent no-op — and in the theme providers (`ThemeRoot.Wrap`: the view registry, the option cells and the
    /// facts): the same reason, so without them `Video.PopOut`, `Video.JoinPoster` and the on-media strip would resolve to nothing in
    /// this window. A theme switch re-renders this root through the epoch `Wrap` reads, like the main window's. The child MUST be a
    /// component (the overlay host mounts its child once).</summary>
    [CoreHost]
    sealed class PopOutRoot : Component
    {
        public override Element Render() => Theming.ThemeRoot.Wrap(static () =>
            OverlayHost.Create(Embed.Comp(static () => new PopOutContent()), isPrimaryToastHost: false));
    }

    /// <summary>The pop-out's content: sized to THIS window's viewport, ALWAYS opaque (the hole erases the fill over the
    /// video rect; anything unpainted shows the desktop), the shared stage when a player exists and the POSTER otherwise
    /// (0.2.9 left a bare letterbox rect here — W14b, a deliberate divergence).</summary>
    [CoreHost]
    sealed class PopOutContent : Component
    {
        // ── the title band is an OS caption region ──────────────────────────────────────────────────────────────────
        //
        // The band answers WM_NCHITTEST with HTCAPTION (pushed via InputHooks.SetTitleBarRegions below), so
        // DefWindowProc owns the press from down to up: the OS move loop, Aero Snap, monitor hops, touch and pen, and
        // double-click-to-maximize all just work, and the loop ends within ~2 ms of the release. A hand-rolled drag
        // (InputHooks.WindowBeginMove, PopOut.DragBy) cannot replace this — a press that starts as CLIENT pointer
        // input can never be handed to the OS move loop under mouse-in-pointer (measured 2026-09-22: the loop entered
        // ~500 ms after release, never moved the window, and did not end until the next click). The engine still
        // synthesizes the pointer's real position over the region to this tree on a reserved pointer id, so the
        // band's hover keeps arriving through OnPointerMoveWithin/OnPointerExit exactly as over client area; the app
        // only ever learns that a move/size loop began (InputHooks.WindowMoveSizeBeganObserved) and ended
        // (WindowMoveSizeEndedObserved), to hold the chrome open for the duration.
        readonly Action<Point2> _onPointerMoveWithin;
        readonly Action _onPointerExit;
        readonly Action _onMoveBegan = static () => s_popOutFeed.WindowMoveStarted();

        public PopOutContent()
        {
            _onPointerMoveWithin = static _ => s_popOutFeed.SetPointerOverControls(true);
            _onPointerExit = static () => s_popOutFeed.SetPointerOverControls(false);
        }

        public override Element Render()
        {
            var vp = UseContextSignal(Viewport.Size);
            var hooks = UseContext(InputHooks.Current);
            // The OS owns the band's press end to end (it is a caption region, see below); the only thing the app
            // learns is that the loop began — hold the chrome for its duration (the element releases the hold itself
            // on WindowMoveSizeEndedObserved). Edge resizes raise it too, which is right: resizing shows the chrome.
            UseEffect(() =>
            {
                hooks.WindowMoveSizeBeganObserved += _onMoveBegan;
                return () => hooks.WindowMoveSizeBeganObserved -= _onMoveBegan;
            }, DepKey.Empty);
            var binding = Playback.Video.Player.Value;
            var join = JoinNow.Value;
            long generation = binding.Generation;
            bool hostFullscreen = State.DetachedFullscreen.Value;
            // The band is the window's only chrome, so it rides the same idle machine as the transport: move the
            // pointer and both appear, idle and both go with the cursor.
            bool band = s_popOutChrome.Value && !hostFullscreen;
            var bandRef = UseRef<NodeHandle>(default);
            var fadeArmed = UseRef(false);
            UseLayoutEffect(() =>
            {
                if (!fadeArmed.Value) { fadeArmed.Value = true; return; }
                OnMedia.FadeChrome(Context, bandRef.Value, band);
            }, band ? 1 : 0);

            // THE BAND IS A CAPTION REGION (WM_NCHITTEST → HTCAPTION). It is the only mechanism that works: DefWindowProc
            // owns the press from down to up (the OS move loop, Aero Snap, monitor hops, touch and pen, double-click
            // maximize), and the loop ends the instant the button comes up. A press that starts as client pointer input
            // can never be handed to that loop under mouse-in-pointer (measured 2026-09-22: the loop entered ~500 ms
            // after the release, never moved the window during the drag and did not end until the next click — the
            // "still dragging while I am doing something else" bug). The engine still synthesizes the pointer's real
            // position over the region to this tree, so the band's hover (OnPointerMoveWithin/Exit) and the chrome's
            // auto-show keep working exactly as over client area. The region starts BELOW the OS resize strip so the top
            // ~8 DIP keep answering HTTOP, and the corners stay HTTOPLEFT/HTTOPRIGHT. No region while fullscreen.
            var regions = UseRef<TitleBarRegion[]>(null!);
            regions.Value ??= new TitleBarRegion[1];
            UseLayoutEffect(() =>
            {
                int count = 0;
                float w = vp.Peek().Width;
                if (!hostFullscreen && w > 0f)
                {
                    regions.Value[0] = new TitleBarRegion(
                        new RectF(0f, PopOut.ResizeStripDip, w, PopOutBand.HeightDip - PopOut.ResizeStripDip), TitleBarHit.Caption);
                    count = 1;
                }
                hooks.SetTitleBarRegions?.Invoke(regions.Value, count);
                Log.Info(State.LogCategory, $"pop-out caption band w={w:0.#} hostFs={hostFullscreen} regions={count} mode=caption-region");
                return null;
            }, DepKey.From(hostFullscreen ? 1 : 0, (int)vp.Peek().Width, 0, (int)vp.Peek().Height));

            // The picture (the stage over the poster, the title band) is the theme's (`Video.PopOut`). `Generation` is the stage's
            // identity: the stage key follows it, so a new binding generation must re-run the template even when nothing else moved.
            return ViewSlots.View(ViewKeys.VideoPopOut, new VideoPopOutProps
            {
                Size = vp,
                HasPlayer = SurfaceMount.ShouldMountPlayerStage(binding.Player is not null),
                Join = join,
                BandShown = band,
                Generation = generation,
                Stage = s_popOutStage,
                Poster = static visual => Poster(visual),
                Title = s_currentTitle,
                BandRealized = h => bandRef.Value = h,
                BandPointerMoved = _onPointerMoveWithin,
                BandPointerLeft = _onPointerExit,
            });
        }

        /// <summary>The pop-out's stage: the shared player stage keyed by the binding generation (read when the template builds it).</summary>
        static readonly Func<Element> s_popOutStage = static () =>
            Embed.Comp(static () => new PlayerStage(new StageHost(TransportOwner.PopOut, s_toggleDetachedFullscreen, State.ReportPopOutPresented), State.DetachedFullscreen, s_popOutChrome, s_popOutFeed))
                with { Key = "stage:" + GenKey(Playback.Video.Player.Peek().Generation) };

    }

    /// <summary>The pop-out's title band: the number the caption region (`InputHooks.SetTitleBarRegions`) and the theme's band must agree on.</summary>
    public static class PopOutBand
    {
        /// <summary>44 DIP: the caption region's height, pushed to <see cref="InputHooks.SetTitleBarRegions"/> and the band's own layout
        /// height (they must match, or the region and the visible band drift apart).</summary>
        public const float HeightDip = 44f;
    }

    // ══ 5. THE FULLSCREEN SURFACE ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The last placement state that did NOT resolve to fullscreen — the "before" of the next entry.</summary>
    static PlacementState s_beforeFullscreen = PlacementState.Music;

    /// <summary>The shell-lifetime half: observes every placement change and mounts the surface while fullscreen resolves.</summary>
    [CoreHost]
    sealed class FullscreenHost : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () =>
            {
                var s = State.Surface.Value;
                if (PlacementCore.Resolve(s) != SurfacePlacement.Fullscreen) s_beforeFullscreen = s;
            });
            return Flow.Show(
                static () => PlacementCore.Resolve(State.Surface.Value) == SurfacePlacement.Fullscreen,
                HoleBoxes.FullscreenLayer(Embed.Comp(static () => new FullscreenSurface())));
        }
    }

    /// <summary>A REAL fullscreen: the OS window goes borderless-fullscreen on its monitor and restores what it found; the
    /// shell collapses its chrome for the duration (one transport). Its lifetime IS the mode — mount = enter, unmount =
    /// exit, for every route out.</summary>
    [CoreHost]
    sealed class FullscreenSurface : Component
    {
        NodeHandle _root;
        IImmersiveHost? _immersiveHost;

        /// <summary>Fullscreen is still wanted (the host mounts this surface while it resolves): a teardown that finds it true is a REMOUNT.</summary>
        static bool FullscreenWanted() => PlacementCore.Resolve(State.Surface.Peek()) == SurfacePlacement.Fullscreen;

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var immersive = ImmersiveService.Shared;
            var immersiveHost = _immersiveHost ??= new HooksImmersiveHost(hooks);

            bool live = PlacementCore.Resolve(State.Surface.Value) == SurfacePlacement.Fullscreen;
            UseEffect(() => State.ReportLive(SurfacePlacement.Fullscreen, live), DepKey.From(live));
            UseEffect(() => () => State.ReportLive(SurfacePlacement.Fullscreen, false), DepKey.Empty);

            // REAL OS fullscreen, restoring the REMEMBERED prior state — never an unconditional false (ImmersiveService: keyed by the
            // mode, so a remount of this surface while fullscreen is still wanted does not leave it).
            UseLayoutEffect(() =>
            {
                immersive.EnterWindow(ImmersiveKind.VideoFullscreen, immersiveHost);
                return () => { immersive.ReleaseWindow(ImmersiveKind.VideoFullscreen, immersiveHost, FullscreenWanted); };
            }, DepKey.Empty);

            // A focus SCOPE at the root (Tab cannot walk into the collapsed chrome); focus parks INSIDE the video — but only
            // when the user asked for fullscreen; on unmount the scope pops and focus returns to whatever invoked it.
            UseLayoutEffect(() =>
            {
                if (Context.HostNode.IsNull) return null;
                immersive.PushFocus(ImmersiveKind.VideoFullscreen, immersiveHost, Context.HostNode,
                    focusRoot: FullscreenEntry.UserInitiated(s_beforeFullscreen, State.Surface.Peek()));
                return () => { immersive.ReleaseFocus(ImmersiveKind.VideoFullscreen, immersiveHost, FullscreenWanted); };
            }, DepKey.Empty);

            return HoleBoxes.FullscreenRoot(
                h => _root = h,
                // Escape/F exit, handled at the ROOT (no focused control can swallow the way out). A modified key belongs
                // to whatever else claims it — Shift+Esc does nothing, deliberately.
                static e =>
                {
                    if (e.Handled || (e.Mods & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift)) != 0) return;
                    if (e.KeyCode != Keys.Escape && e.KeyCode != Keys.F) return;
                    e.Handled = true;
                    State.ExitFullscreen();
                },
                // Never leave focus null while the surface is up.
                got =>
                {
                    if (got || _root.IsNull) return;
                    if ((hooks.GetFocus?.Invoke() ?? default).IsNull) hooks.FocusNode?.Invoke(_root, false);
                },
                ViewSlots.View(ViewKeys.VideoFullscreen, s_chromeProps));
        }

        /// <summary>The chrome's props, built once: the title (bound) and the way out. Its band is the theme's (`Video.Fullscreen`).</summary>
        static readonly VideoFullscreenProps s_chromeProps = new()
        {
            Title = s_currentTitle, Exit = static () => State.ExitFullscreen(),
            Artists = static () => CurrentArtists(), SwitchToAudio = static () => State.TurnOff(),
            // The fullscreen owner's transport (TransportOwnerFor(Fullscreen) = Fullscreen): the main window's presenter keeps running the idle
            // machine and the feed; the theme's chrome draws the strip over them, the presenter's own strip stands down (PlayerStage).
            Transport = RailWiring.OnMediaProps(s_mainChrome, s_mainFeed),
        };
    }
}
