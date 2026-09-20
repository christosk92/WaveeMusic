// ── Shell/Video.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// the docked cap surface, in-window PiP (8 resize zones), the fullscreen surface, the watch stage, the placement
// menu
//
// Role: UI
// Owner: K
// Wave: 4
// Budget: 1450 lines
// Spec: ch 24 §9
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// ONE PLAYER, TWO PRESENTERS. The main window has a single stay-mounted presenter that Docked, the in-window PiP and
// fullscreen are GEOMETRY modes of; the pop-out is a second HWND and therefore a second element. Every surface here
// binds `Playback.Video.Player` (owner H's decode host) and
// `Video.State` (the one placement value) — none builds a player, none holds a second visibility flag, none issues an
// engine-video command of its own: play/pause/commit go through the session transport (`Playback.*`), a scrub PREVIEW
// through `Playback.Video.Seek(accurate: false)` planned by `Playback.Video.SeekPlanner`. The only engine-video surface
// API used is `MediaPlayerElement` (the element) and the bound player's `NaturalSize` read, so the engine's video
// rework lands behind H's host in one place.
//
// THE RULES THIS FILE IS SHAPED BY (ch 24 §0):
//   1. No LayoutTransition, no Opacity, no offscreen-RT effect, no scale Enter/Exit on ANY ancestor of a video hole.
//      The hole is a DestOut erase against the real back buffer. Docked/PiP/FS share ONE stay-mounted presenter;
//      geometry is bound Width/Height/Transform (the PiP pattern). Pop-out is another HWND.
//   2. Main-window hole is one UseVideoSurface. Docked cap is a hollow reservation that publishes AbsoluteRect.
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
// join delay, the fullscreen entry) — this file lays out and forwards.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Signals;

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

    /// <summary>Laid-out window-DIP rect of the hollow docked reservation. The stay-mounted overlay follows this.</summary>
    public static readonly Signal<RectF> DockedSlot = new(default);

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

        var layers = new List<Element>(3)
        {
            // ALWAYS the bottom layer, byte-identical to the card's poster ground: the idle→live hand-over is ONE dissolve.
            PosterGround(posterUrl),
        };
        if (onPlay is not null)
        {
            layers.Add(Flow.Show(() => !Live(), new BoxEl
            {
                Key = "module-stage-cta",
                Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [ToolTip.Wrap(Controls.IconPill(Icons.Play, onPlay, ButtonAppearance.Accent, size: 64f), Loc.Get(Strings.Detail.Play))],
            }));
        }
        // Flow.Show, NOT a C# `if`: a KeepAlive-parked page cannot re-render, but the node-bound predicate still runs.
        layers.Add(Flow.Show(Live, Embed.Comp(() => new DockedSurface(DockedFace.PageStage, ownerStagePlayable)) with { Key = "module-stage-video" }));

        return new BoxEl
        {
            Key = "module-stage",
            // A plain COLUMN carries the aspect (AspectRatio never reaches a ZStack's measure); the ZStack is its child.
            Shrink = 0f, Direction = 1, AspectRatio = 16f / 9f, MaxHeight = Shell.DockedVideoMaxH,
            ClipToBounds = true, Fill = Tok.MediaLetterbox,
            Children = [new BoxEl { Grow = 1f, MinHeight = 0f, ZStack = true, ClipToBounds = true, Children = layers.ToArray() }],
        };
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
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ZStack = true, HitTestPassThrough = true,
            Children =
            [
                Embed.Comp(static () => new PipSurface()),
                Embed.Comp(static () => new PopOut { Settings = Platform.Settings }),
                Embed.Comp(static () => new HostObserver()),
                Embed.Comp(static () => new JoinWatch()),
            ],
        };
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

        MenuFlyoutItem Row(SurfacePlacement p, string labelKey, string icon, string? accelWhenAllowed, string? reasonKey)
        {
            bool allowed = PlacementCore.Allows(state.Available, p);
            string? accel = allowed ? accelWhenAllowed : reasonKey is null ? null : Loc.Get(reasonKey);
            return MenuFlyoutItem.RadioItem(Loc.Get(labelKey), now == p, () => State.OpenAt(p), icon, allowed) with { AcceleratorText = accel };
        }

        var items = new List<MenuFlyoutItem>(7)
        {
            Row(SurfacePlacement.Docked, Strings.Player.DockInRail, Icons.SplitView, null, Strings.Player.VideoNeedsWiderWindow),
            Row(SurfacePlacement.Floating, Strings.Player.VideoMiniPlayer, Icons.BackToWindow, null, null),
            Row(SurfacePlacement.Detached, Strings.Player.VideoInSeparateWindow, Icons.Movie, null, Strings.Player.VideoNoSecondWindow),
        };
        if (includeFullscreen)
            items.Add(Row(SurfacePlacement.Fullscreen, Strings.Player.VideoFullScreen, Icons.FullScreen, "F11", Strings.Player.VideoNoFullscreen));
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
    static readonly Action s_play = static () => Playback.Resume();
    static readonly Action s_pause = static () => Playback.Pause();
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

    static VideoAspectMode ToEngine(AspectPreference p) => p switch
    {
        AspectPreference.Crop => VideoAspectMode.UniformToFill,
        AspectPreference.Stretch => VideoAspectMode.Fill,
        AspectPreference.Native => VideoAspectMode.Native,
        AspectPreference.Custom => VideoAspectMode.Custom,
        _ => VideoAspectMode.Uniform,
    };

    static AspectPreference FromEngine(VideoAspectMode m) => m switch
    {
        VideoAspectMode.UniformToFill => AspectPreference.Crop,
        VideoAspectMode.Fill => AspectPreference.Stretch,
        VideoAspectMode.Native => AspectPreference.Native,
        VideoAspectMode.Custom => AspectPreference.Custom,
        _ => AspectPreference.Fit,
    };

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

    /// <summary>The current track's cover url, or null. Subscribes.</summary>
    static string? CurrentArtUrl() => CurrentTrack() is { } t ? Controls.ArtUrl(t.ImageId) : null;

    /// <summary>A module playable uri is 60+ characters differing only in the tail — a log line keeps the tail.</summary>
    static string Show(string? uri) => string.IsNullOrEmpty(uri) ? "(none)" : uri.Length <= 24 ? uri : "…" + uri[^24..];

    // ── the poster ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The poster's GROUND alone — dimmed artwork. The watch page draws this byte-identical layer under its stage
    /// so the idle→live hand-over is ONE cross-fade. A track with no art is the bare letterbox.</summary>
    /// <summary>The poster ground: the current art, dimmed, FILLING the video box — and imposing no shape of its own.
    /// <para><b>Never <c>Controls.ArtworkFill</c> here.</b> That helper is, by its own doc, "a square cover that fills
    /// the width its layout hands it (aspect-ratio 1)", and a ZStack measures to its largest child — so hosting it as
    /// the element's <c>PosterContent</c> made the video AREA width x width. Every downstream decision then fitted the
    /// picture into a square: Fit centred it in the square (about 87 DIP low in a 16:9 card), Crop and Native fitted
    /// the wrong box entirely, and the bottom-justified on-media transport laid out below the visible edge and was
    /// clipped away — which read as "the controls never show on hover" when the machine was in fact revealing them.
    /// A NaN aspect is the engine's "no derived height: fill the box your layout gives you"
    /// (<c>FluentGpu.Engine/Dsl/Factories.cs</c>).</para></summary>
    internal static Element PosterGround(string? url) => new BoxEl
    {
        Grow = 1f, Opacity = 0.4f, ClipToBounds = true,
        Children = url is { Length: > 0 }
            ? [Ui.Image(url, ImageFit.Cover, aspect: float.NaN, decodePx: 256f, corners: 0f, placeholder: (ColorF?)null)
                   with { Placeholder = Design.WatchedPlaceholder(url) }]
            : [],
    };

    /// <summary>The "no picture yet" composition every surface shares: the live art over the letterbox, and — only for
    /// the two states that have something to say — the notice over it. The ground is the SAME keyed
    /// <see cref="LivePoster"/> in every state, so the notice appearing or the fault replacing it never re-fades the
    /// artwork underneath and never reflows the card.</summary>
    static Element Poster(JoinVisual visual) => new BoxEl
    {
        Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = Tok.MediaLetterbox,
        Children = visual is JoinVisual.Working or JoinVisual.Failed
            ? [LivePoster.Make(), JoinNotice(visual)]
            : [LivePoster.Make()],
    };

    /// <summary>The CURRENT track's art, read inside its OWN render — so a stage whose key survives a source switch never
    /// shows the first track's art forever (<c>PosterContent</c> freezes at the element's mount).</summary>
    sealed class LivePoster : Component
    {
        public override Element Render() => new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = Tok.MediaLetterbox,
            Children = [PosterGround(CurrentArtUrl())],
        };

        internal static Element Make() => Embed.Comp(static () => new LivePoster()) with { Key = "live-poster" };
    }

    /// <summary>What sits over the poster once the host has something to say. Inside the join budget there is no notice
    /// at all — not a hidden one, none — so a switch that lands fast flashes nothing (ch 24 parity 75) and the ring
    /// never animates unseen. Past the budget: the 20-DIP ring and "Loading…". On a failure: a warning glyph and the
    /// fault's own line, and NO ring, because "not coming" must not draw the same picture as "still coming".
    /// <para>The notice fades in rather than popping. An opacity channel is legal in this subtree and ONLY here: a
    /// poster is mounted exactly while no player is bound, so there is no video hole below it for an ancestor opacity
    /// to wash out (§0.1). The `Key` makes Working → Failed a real swap, so the new notice plays its own entrance
    /// instead of the words changing under the user.</para></summary>
    static Element JoinNotice(JoinVisual visual)
    {
        bool failed = visual == JoinVisual.Failed;
        var stack = new BoxEl
        {
            Key = failed ? "join:failed" : "join:working",
            Direction = 1, AlignItems = FlexAlign.Center, Gap = Spacing.S, HitTestVisible = false,
            // Reduced motion is the DEFAULT terminal — a hard cut, read as a VALUE, never a branch at the call site.
            Enter = Design.Reduced ? default : new EnterExit(Opacity: 0f, Active: true),
            Children = failed ? FailedLine() : WorkingLine(),
        };
        return new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HitTestPassThrough = true,
            Children = [stack],
        };
    }

    /// <summary>"Still coming": the 20-DIP indeterminate ring and "Loading…", both in ON-MEDIA ink.</summary>
    static Element[] WorkingLine() =>
    [
        ProgressRing.Indeterminate(size: 20f, foreground: Tok.OnMediaPrimary),
        new TextEl(Loc.Get(Strings.Player.Loading))
        {
            Size = 12f, Weight = 600, Color = Tok.OnMediaSecondary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        },
    ];

    /// <summary>"Not coming": a static warning glyph and the fault's OWN line — no ring, because a ring means wait. The
    /// line is bound reactively: the reducer types the fault, and it can land after this notice mounted.</summary>
    static Element[] FailedLine() =>
    [
        new TextEl(Icons.StatusWarning) { Size = 18f, FontFamily = Theme.IconFont, Color = Tok.OnMediaPrimary },
        new TextEl(Prop.Of(static () => Loc.Get(Shell.PlayerBarRules.FaultTitleKey(Playback.Error.Value))))
        {
            Size = 12f, Weight = 600, Color = Tok.OnMediaPrimary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        },
    ];

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

            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    /// <summary>A hover-revealed chrome glyph on the on-media ladder (24 × 24, 11-DIP icon). Its tooltip is its name, and
    /// names the DESTINATION.</summary>
    static Element ChromeGlyph(string glyph, string tip, Action onClick) => ToolTip.Wrap(new BoxEl
    {
        Width = 24f, Height = 24f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Fill = ColorF.Transparent,
        HoverFill = Tok.OnMediaPrimary with { A = 0.14f },
        PressedFill = Tok.OnMediaPrimary with { A = 0.22f },
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(glyph) { Size = 11f, FontFamily = Theme.IconFont, Color = Tok.OnMediaSecondary, HoverColor = Tok.OnMediaPrimary }],
    }, tip);

    // ── the shared stage (the mini player, the pop-out, the fullscreen surface) ─────────────────────────────────────

    /// <summary>What a HOST hands the shared stage: its transport identity and what its fullscreen affordance does (ENTER
    /// from an inline surface, EXIT from the fullscreen surface, TOGGLE the pop-out's own window mode).</summary>
    readonly record struct StageHost(TransportOwner Identity, Action FullscreenRequested);

    /// <summary>One presenter of the bound player. Props freeze at mount: the host bundle is constant per surface.
    /// Host-fullscreen is a live signal so Docked↔PiP↔FS never remounts the hole. Engine transport is always off.</summary>
    sealed class PlayerStage(StageHost host, IReadSignal<bool> hostFullscreen, Signal<bool> chromeVisible) : Component
    {
        long _loggedGen = -1;

        public override Element Render()
        {
            UseSignalEffect(static () => { _ = Prefs.Epoch.Value; SyncAspect(); });
            var binding = Playback.Video.Player.Value;
            _ = hostFullscreen.Value;
            if (binding.Player is not { } player) return new BoxEl { Grow = 1f, MinHeight = 0f };
            // The pop-out draws its own title band (PopOutContent.TitleBand), so the picture never arms a window move.
            bool drag = StageInput.DragMovesWindow(host.Identity, hostFullscreen.Peek(), hasTitleBand: true);
            var cursor = StageInput.HidesCursorWindowed(host.Identity) ? CursorAutoHidePolicy.Always : CursorAutoHidePolicy.FullscreenOnly;
            if (_loggedGen != binding.Generation)
            {
                _loggedGen = binding.Generation;
                Log.Info(State.LogCategory, "video presenter mount owner=" + host.Identity
                    + " gen=" + binding.Generation + " hostFs=" + hostFullscreen.Peek()
                    + " engineTransport=" + MainWindowHole.EngineTransportEnabled
                    + " key=player:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ZStack = true, ClipToBounds = true, Fill = ColorF.Transparent,
                Children =
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
                        PosterContent = LivePoster.Make(),
                        AreTransportControlsEnabled = MainWindowHole.EngineTransportEnabled,
                        SuppressTransport = true,
                        FullscreenRequested = host.FullscreenRequested,
                        HostFullscreen = hostFullscreen,
                        DragMovesWindow = drag,
                        CursorAutoHide = cursor,
                        ChromeVisibleOut = chromeVisible,
                    }) with
                    {
                        Key = "player:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                    OnMedia.Transport(chromeVisible),
                ],
            };
        }
    }

    /// <summary>The video element's idle-chrome output — ONE per HWND, because there is one presenter per window. The
    /// element writes it (dwell, leave debounce, scrub / menu / keyboard / window-move holds, the accessibility
    /// override); every strip on that window reads it, so the transport, the pop-out's title band and the cursor all
    /// go away on the same edge. It is not hover: a `HoverOpacity` reveal follows the nearest INTERACTIVE ancestor, and
    /// the pop-out's tree has none — which is why its controls never appeared at all.</summary>
    static readonly Signal<bool> s_mainChrome = new(true);
    static readonly Signal<bool> s_popOutChrome = new(true);

    static string GenKey(long generation) => "gen:" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ══ 2. THE DOCKED CARD (Cap + PageStage faces) ═══════════════════════════════════════════════════════════════════

    /// <summary>The docked music-video surface: video that simply LIVES in the app at zero commitment. Two faces, one
    /// card — the rail's Cap (height follows the content's own aspect, splitter-overridable) and the watch page's
    /// PageStage (the page owns the envelope). NO transition, NO shadow, NO corners of its own.</summary>
    sealed class DockedSurface(DockedFace face, string? ownerStagePlayable) : Component
    {
        NodeHandle _slot;
        string? _fittedFor;
        (string Key, float RailW, int Nw, int Nh, float H, bool Pinned, string Src) _loggedFit;
        (VideoAspectMode Mode, double Custom, TransportOwner Owner) _loggedPolicy = ((VideoAspectMode)255, -1d, (TransportOwner)255);
        (SurfacePlacement Resolved, string? Playing, string Active, bool Mounts) _loggedHost = ((SurfacePlacement)255, " ", " ", false);

        public override Element Render()
        {
            UseSignalEffect(static () => { _ = Prefs.Epoch.Value; SyncAspect(); });

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
                if (!string.Equals(sourceKey, _fittedFor, StringComparison.Ordinal))
                {
                    _fittedFor = sourceKey;
                    if (Shell.Ui.DockedVideoHeightPinned.Peek()) Shell.Ui.DockedVideoHeightPinned.Value = false;
                }
                string dimSource = NaturalSeed.Name(NaturalSeed.Pick(decoded.Width, decoded.Height, src?.NaturalWidth ?? 0,
                    src?.NaturalHeight ?? 0, catalogue.W, catalogue.H, out int naturalW, out int naturalH));
                var natural = new SizeI(naturalW, naturalH);
                bool pinned = Shell.Ui.DockedVideoHeightPinned.Peek();
                float height = pinned
                    ? Shell.Ui.DockedVideoHeight.Peek()
                    : Shell.FitDockedVideoHeight(railW, natural.Width, natural.Height, Shell.Ui.RailBodyHeight.Peek());
                if (!pinned) Shell.Ui.DockedVideoHeight.Value = height;
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
                DockedSlot.Value = default;
            }, DepKey.Empty);

            if (!mount) return new BoxEl();

            BoxEl card = new BoxEl
            {
                ZStack = true, ClipToBounds = true,
                // NO Shadow, NO Layout/Enter/Exit: a content-layer rung, and any transition here erases the hole.
                OnPointerExit = static () => { },
                OnKeyDown = static e =>
                {
                    if (e.KeyCode != Keys.Space) return;
                    e.Handled = true;
                    Playback.TogglePlay();
                },
                Focusable = true,
                Children = [VideoArea()],
            };

            return face == DockedFace.PageStage
                ? card with { Grow = 1f, MinHeight = 0f, Fill = Tok.MediaLetterbox }
                : card with { Shrink = 0f, MinWidth = 0f, Height = Shell.Ui.DockedVideoHeight, Fill = Tok.MediaLetterbox };
        }

        Element VideoArea()
        {
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = Tok.MediaLetterbox,
                HitTestVisible = false,
                OnRealized = h => _slot = h,
                OnBoundsChanged = _ => PublishDockedSlot(),
            };
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
            Log.Info(State.LogCategory, $"docked slot face={face} rect={rect.X:0.#},{rect.Y:0.#} {rect.W:0.#}x{rect.H:0.#}");
        }
    }

    static void EnterFullscreen()
    {
        Announcer.Say(Loc.Get(Strings.Player.VideoFullScreen));
        State.OpenAt(SurfacePlacement.Fullscreen);
    }

    /// <summary>The hover-revealed top strip: mini player · full screen · off, right-aligned, no label.</summary>
    static Element DockedOverlayChrome() => new BoxEl
        {
            Grow = 1f, Direction = 1, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Height = 30f, Shrink = 0f, Direction = 0,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Spacing.XXS,
                    Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
                    Gradient = Tok.ScrimTop,
                    Opacity = 0f, HoverOpacity = 1f,
                    HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
                    Children =
                    [
                        ChromeGlyph(Icons.BackToWindow, Loc.Get(Strings.Player.VideoMiniPlayer), static () =>
                        {
                            Announcer.Say(Loc.Get(Strings.Player.VideoMiniPlayer));
                            State.OpenAt(SurfacePlacement.Floating);
                        }),
                        ChromeGlyph(Icons.FullScreen, Loc.Get(Strings.Player.VideoFullScreen), EnterFullscreen),
                        ChromeGlyph(Icons.Cancel, Loc.Get(Strings.Player.TurnOffVideo), static () =>
                        {
                            Announcer.Say(Loc.Get(Strings.Player.TurnOffVideo));
                            // Sticky off through the model's HOST-CLOSE (it carries the stale-close identity guard).
                            State.ReportClosed(SurfacePlacement.Docked);
                        }),
                    ],
                },
            ],
        };

    // ══ 3. THE IN-WINDOW MINI PLAYER ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The stay-mounted main-window presenter: docked overlay, in-window PiP, and fullscreen share one
    /// MediaPlayerElement. Geometry lives in bound <c>Transform</c>/<c>Width</c>/<c>Height</c> thunks.</summary>
    sealed class PipSurface : Component
    {
        const float ScrimH = 30f;

        readonly Signal<float> _x = new(0f), _y = new(0f);
        readonly Signal<float> _w = new(Pip.DefaultW), _h = new(Pip.DefaultH);
        readonly Signal<bool> _placed = new(false);   // false = anchored (tracks the corner, reserves layout)
        bool _sized;                                  // the user sized it (a gesture or a remembered rect)
        float _fitRatio;
        bool _seeded;
        NodeHandle _dragNode;
        readonly NodeHandle[] _bandNodes = new NodeHandle[8];
        IReadSignal<Size2>? _vpSig;
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
                    _w.Value = MathF.Max(Pip.MinW, pw); _h.Value = MathF.Max(Pip.MinH, ph);
                    _placed.Value = true;
                    _sized = true;
                }
            }

            var vp = UseContextSignal(Viewport.Size);
            _vpSig = vp;
            UseSignalEffect(static () => { _ = Prefs.Epoch.Value; SyncAspect(); });

            var resolved = PlacementCore.Resolve(State.Surface.Value);
            bool covered = resolved == SurfacePlacement.Docked && Shell.Ui.ImmersiveLyrics.Value;
            bool owns = MainWindowHole.Owns(resolved) && !covered;
            bool livePip = resolved == SurfacePlacement.Floating;
            UseEffect(() => State.ReportLive(SurfacePlacement.Floating, livePip), DepKey.From(livePip));
            UseSignalEffect(() =>
                FloatingSurfaceReserve.Value = PipGesture.Reserve(
                    PlacementCore.Resolve(State.Surface.Value) == SurfacePlacement.Floating, _placed.Value, _h.Value));
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
                _h.Value = Pip.FitHeight(w, ratio, vp.Value.Height);
            });

            if (!owns) return new BoxEl();

            var surface = new BoxEl
            {
                Direction = 1, ClipToBounds = true, ZStack = true,
                Width = Prop.Of(() => OverlayWidth(PlacementCore.Resolve(State.Surface.Value), vp.Value, _w.Value, DockedSlot.Value)),
                Height = Prop.Of(() => OverlayHeight(PlacementCore.Resolve(State.Surface.Value), vp.Value, _h.Value, DockedSlot.Value)),
                Transform = Prop.Of(() => OverlayTransform(PlacementCore.Resolve(State.Surface.Value), vp.Value, _placed.Value, _x.Value, _y.Value, _w.Value, _h.Value, DockedSlot.Value)),
                Fill = ColorF.Transparent,
                Corners = resolved == SurfacePlacement.Floating ? CornerRadius4.All(Radii.Card) : default,
                BorderWidth = resolved == SurfacePlacement.Floating ? 1f : 0f,
                BorderColor = resolved == SurfacePlacement.Floating ? Prop.Of(static () => Tok.StrokeCardDefault) : ColorF.Transparent,
                Shadow = resolved == SurfacePlacement.Floating ? Elevation.Flyout : default,
                OnPointerExit = static () => { },
                Children = OverlayChildren(resolved),
            };

            return new BoxEl { Grow = 1f, Direction = 1, HitTestPassThrough = true, Children = [surface] };
        }

        Element[] OverlayChildren(SurfacePlacement resolved)
        {
            var kids = new List<Element>(4) { VideoArea() };
            if (resolved == SurfacePlacement.Docked) kids.Add(DockedOverlayChrome());
            if (resolved == SurfacePlacement.Floating) { kids.Add(Chrome()); kids.Add(ResizeBands()); }
            return kids.ToArray();
        }

        static float OverlayWidth(SurfacePlacement resolved, Size2 vp, float pipW, RectF slot) => resolved switch
        {
            SurfacePlacement.Fullscreen => vp.Width,
            SurfacePlacement.Docked => slot.W,
            _ => pipW,
        };

        static float OverlayHeight(SurfacePlacement resolved, Size2 vp, float pipH, RectF slot) => resolved switch
        {
            SurfacePlacement.Fullscreen => vp.Height,
            SurfacePlacement.Docked => slot.H,
            _ => pipH,
        };

        static Affine2D OverlayTransform(SurfacePlacement resolved, Size2 vp, bool placed, float x, float y, float w, float h, RectF slot)
        {
            if (resolved == SurfacePlacement.Fullscreen) return Affine2D.Identity;
            if (resolved == SurfacePlacement.Docked) return Affine2D.Translation(slot.X, slot.Y);
            var (ax, ay) = placed ? (x, y) : Pip.Anchor(w, h, vp.Width, vp.Height);
            return Affine2D.Translation(PipGesture.ClampX(ax, vp.Width, w), PipGesture.ClampY(ay, vp.Height, h));
        }

        static Element VideoArea()
        {
            _ = Playback.Video.Source.Value;
            var binding = Playback.Video.Player.Value;
            var join = JoinNow.Value;
            if (!SurfaceMount.ShouldMountPlayerStage(binding.Player is not null)) return Poster(join);
            var stage = Embed.Comp(static () => new PlayerStage(
                new StageHost(TransportOwner.Docked, s_toggleMainFullscreen), State.MainHostFullscreen, s_mainChrome))
                with { Key = "mainstage:" + GenKey(binding.Generation) };
            if (join != JoinVisual.Video)
                return new BoxEl { Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = ColorF.Transparent, Children = [stage, Poster(join)] };
            return new BoxEl { Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = ColorF.Transparent, Children = [stage] };
        }

        /// <summary>The hover strip: a drag surface and the ✕, inset by the corner width and the edge band so neither sits
        /// under a resize zone.</summary>
        Element Chrome()
        {
            var dragSurface = new BoxEl
            {
                Grow = 1f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                Cursor = CursorId.SizeAll,
                OnRealized = h => _dragNode = h,
                OnPointerDown = OnDragDown,
                OnDrag = OnDragMove,
                OnClick = PersistGeometry,          // an OnDrag node's click IS its release edge
                OnDragCanceled = PersistGeometry,
            };
            var close = ChromeGlyph(Icons.Cancel, Loc.Get(Strings.Player.TurnOffVideo), static () => State.ReportClosed(SurfacePlacement.Floating));
            var strip = new BoxEl
            {
                Height = ScrimH, Shrink = 0f, Direction = 0,
                Padding = new Edges4(Pip.CornerW, Pip.EdgeBand, Pip.CornerW, 0f),
                Gradient = Tok.ScrimTop,
                Corners = new CornerRadius4(Radii.Card, Radii.Card, 0f, 0f),
                Opacity = 0f, HoverOpacity = 1f,
                HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
                Children = [dragSurface, close],
            };
            return new BoxEl { Grow = 1f, Direction = 1, HitTestPassThrough = true, Children = [strip] };
        }

        /// <summary>The eight resize zones: a 3-row skeleton whose every non-band cell is pass-through, topmost so a band
        /// always wins over the chrome. Corner rows are 12 tall, edge bands 6 thin.</summary>
        Element ResizeBands()
        {
            BoxEl Band(int slot, PipEdge edge, CursorId cursor) => new BoxEl
            {
                Cursor = cursor,
                OnRealized = h => _bandNodes[slot] = h,
                OnPointerDown = p => OnResizeDown(slot, p),
                OnDrag = p => OnResizeMove(slot, edge, p),
                OnClick = PersistGeometry,
                OnDragCanceled = PersistGeometry,
            };

            return new BoxEl
            {
                Grow = 1f, Direction = 1, HitTestPassThrough = true,
                Children =
                [
                    new BoxEl
                    {
                        Height = Pip.CornerH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Start, HitTestPassThrough = true,
                        Children =
                        [
                            Band(0, PipEdge.Left | PipEdge.Top, CursorId.SizeNWSE) with { Width = Pip.CornerW, Height = Pip.CornerH },
                            Band(1, PipEdge.Top, CursorId.SizeNS) with { Grow = 1f, Height = Pip.EdgeBand },
                            Band(2, PipEdge.Right | PipEdge.Top, CursorId.SizeNESW) with { Width = Pip.CornerW, Height = Pip.CornerH },
                        ],
                    },
                    new BoxEl
                    {
                        Grow = 1f, MinHeight = 0f, Direction = 0, HitTestPassThrough = true,
                        Children =
                        [
                            Band(3, PipEdge.Left, CursorId.SizeWE) with { Width = Pip.EdgeBand },
                            new BoxEl { Grow = 1f, MinWidth = 0f, HitTestPassThrough = true },
                            Band(4, PipEdge.Right, CursorId.SizeWE) with { Width = Pip.EdgeBand },
                        ],
                    },
                    new BoxEl
                    {
                        Height = Pip.CornerH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.End, HitTestPassThrough = true,
                        Children =
                        [
                            Band(5, PipEdge.Left | PipEdge.Bottom, CursorId.SizeNESW) with { Width = Pip.CornerW, Height = Pip.CornerH },
                            Band(6, PipEdge.Bottom, CursorId.SizeNS) with { Grow = 1f, Height = Pip.EdgeBand },
                            // The SE zone carries the one painted pixel of this layer, revealed with the chrome.
                            Band(7, PipEdge.Right | PipEdge.Bottom, CursorId.SizeNWSE) with
                            {
                                Width = Pip.CornerW, Height = Pip.CornerH,
                                Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.End,
                                Opacity = 0f, HoverOpacity = 1f,
                                HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
                                Children =
                                [
                                    new BoxEl
                                    {
                                        Width = 8f, Height = 8f, Margin = new Edges4(0f, 0f, 2f, 2f), HitTestVisible = false,
                                        Corners = new CornerRadius4(0f, 0f, Radii.Control, 0f),
                                        Fill = Tok.OnMediaTertiary,
                                    },
                                ],
                            },
                        ],
                    },
                ],
            };
        }

        Size2 ViewportPeek() => _vpSig?.Peek() ?? new Size2(1280f, 720f);

        /// <summary>Commit the DRAWN position first (an unplaced card would otherwise snap to the origin on the first sample)
        /// and release the reservation: placing it is the user opting into a free-floating overlay.</summary>
        void AdoptDrawnPosition()
        {
            var vp = ViewportPeek();
            float w = _w.Peek(), h = _h.Peek();
            var (x, y) = _placed.Peek() ? (_x.Peek(), _y.Peek()) : Pip.Anchor(w, h, vp.Width, vp.Height);
            _x.Value = PipGesture.ClampX(x, vp.Width, w);
            _y.Value = PipGesture.ClampY(y, vp.Height, h);
            _placed.Value = true;
            _startX = _x.Peek(); _startY = _y.Peek(); _startW = w; _startH = h;
        }

        void OnDragDown(Point2 local)
        {
            var scene = Context.Scene;
            if (scene is null || _dragNode.IsNull || !scene.IsLive(_dragNode)) return;
            AdoptDrawnPosition();
            var abs = scene.AbsoluteRect(_dragNode);
            _startPx = local.X + abs.X; _startPy = local.Y + abs.Y;
        }

        void OnDragMove(Point2 local)
        {
            var scene = Context.Scene;
            if (scene is null || _dragNode.IsNull || !scene.IsLive(_dragNode)) return;
            var abs = scene.AbsoluteRect(_dragNode);   // the grip moves WITH the surface → reconstruct window space
            var vp = ViewportPeek();
            _x.Value = PipGesture.ClampX(_startX + (local.X + abs.X - _startPx), vp.Width, _w.Peek());
            _y.Value = PipGesture.ClampY(_startY + (local.Y + abs.Y - _startPy), vp.Height, _h.Peek());
        }

        void OnResizeDown(int slot, Point2 local)
        {
            var scene = Context.Scene;
            var band = _bandNodes[slot];
            if (scene is null || band.IsNull || !scene.IsLive(band)) return;
            AdoptDrawnPosition();
            _sized = true;   // the user's size is the size now
            var abs = scene.AbsoluteRect(band);
            _startPx = local.X + abs.X; _startPy = local.Y + abs.Y;
        }

        void OnResizeMove(int slot, PipEdge edge, Point2 local)
        {
            var scene = Context.Scene;
            var band = _bandNodes[slot];
            if (scene is null || band.IsNull || !scene.IsLive(band)) return;
            var abs = scene.AbsoluteRect(band);
            var vp = ViewportPeek();
            var r = PipGesture.Resize(edge, _startX, _startY, _startW, _startH,
                local.X + abs.X - _startPx, local.Y + abs.Y - _startPy, vp.Width, vp.Height);
            _x.Value = r.X; _y.Value = r.Y; _w.Value = r.W; _h.Value = r.H;
        }

        /// <summary>One settings write per gesture, and only for a DELIBERATELY placed card (writing the computed anchor
        /// would freeze it against a later window resize).</summary>
        void PersistGeometry()
        {
            if (!_placed.Peek()) return;
            Platform.Settings.Set(Platform.Keys.VideoPipRect, PlacementPersistence.SaveRect(_x.Peek(), _y.Peek(), _w.Peek(), _h.Peek()));
        }
    }

    // ══ 4. THE POP-OUT WINDOW'S CONTENT ══════════════════════════════════════════════════════════════════════════════

    /// <summary>The detached window's root (installed as <see cref="PopOut.ContentFactory"/>). It wraps the content in its
    /// OWN overlay host — a detached window builds its own AppHost, so the shell's never reaches it and every transport
    /// flyout would be a silent no-op. The child MUST be a component (the overlay host mounts its child once).</summary>
    sealed class PopOutRoot : Component
    {
        public override Element Render() => OverlayHost.Create(Embed.Comp(static () => new PopOutContent()), isPrimaryToastHost: false);
    }

    /// <summary>The pop-out's content: sized to THIS window's viewport, ALWAYS opaque (the hole erases the fill over the
    /// video rect; anything unpainted shows the desktop), the shared stage when a player exists and the POSTER otherwise
    /// (0.2.9 left a bare letterbox rect here — W14b, a deliberate divergence).</summary>
    sealed class PopOutContent : Component
    {
        Point2 _grab;
        float _scale = 1f;
        bool _dragging;
        readonly Action<Point2> _bandDown, _bandDrag;
        readonly Action _bandRelease;

        public PopOutContent()
        {
            // THE WINDOW MOVES WITH US, so the grab point is the invariant: hold the client DIP position the press
            // landed on, and each sample asks for the delta that puts it back under the pointer. Because the window
            // then moves, the NEXT sample's local position returns to the grab point on its own — the error never
            // accumulates and a dropped sample self-corrects. The in-window PiP drag reconstructs its pointer the same
            // way. No OS modal loop: see PopOut.DragBy for why (21 fps measured, and touchpad presses that never took).
            _bandDown = p => { _grab = p; _dragging = true; };
            _bandDrag = p =>
            {
                if (!_dragging) return;
                float dx = (p.X - _grab.X) * _scale, dy = (p.Y - _grab.Y) * _scale;
                if (dx == 0f && dy == 0f) return;
                PopOut.DragBy?.Invoke(dx, dy);
            };
            // Every release edge ends it. An OnDrag node's OnClick IS its release edge (the PiP's resize bands rely on
            // the same fact) and a capture loss arrives as OnDragCanceled, which a PointerUp alone would miss.
            _bandRelease = () => _dragging = false;
        }

        public override Element Render()
        {
            var vp = UseContextSignal(Viewport.Size);
            var hooks = UseContext(InputHooks.Current);
            _scale = UseContext(Viewport.Scale);      // DIP -> physical px for the window move
            var binding = Playback.Video.Player.Value;
            var join = JoinNow.Value;
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

            // NO OS caption region. A pushed TitleBarRegion answers WM_NCHITTEST with HTCAPTION, which means the app
            // never sees the pointer in that band at all: no hover, no cursor, no visual — the window was draggable and
            // said nothing about it, and reaching for the strip made the chrome vanish because the pointer had left the
            // client area. The band below is ordinary client area instead, so it can carry the title, the move cursor
            // and its own drag. The engine's top resize border (SM_CXPADDEDBORDER + SM_CYSIZEFRAME, ~8 px) still sits
            // above it, and the corners stay HTTOPLEFT/HTTOPRIGHT, so resizing is unchanged.
            UseLayoutEffect(() =>
            {
                hooks.SetTitleBarRegions?.Invoke([], 0);
                Log.Info(State.LogCategory, $"pop-out caption band w={vp.Peek().Width:0.#} hostFs={hostFullscreen} mode=client-strip");
                return null;
            }, DepKey.From(hostFullscreen ? 1 : 0, (int)vp.Peek().Width, 0, (int)vp.Peek().Height));

            Element body = SurfaceMount.ShouldMountPlayerStage(binding.Player is not null)
                ? new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, ZStack = true,
                    Children =
                    [
                        Embed.Comp(static () => new PlayerStage(new StageHost(TransportOwner.PopOut, s_toggleDetachedFullscreen), State.DetachedFullscreen, s_popOutChrome))
                            with { Key = "stage:" + GenKey(Playback.Video.Player.Peek().Generation) },
                        join == JoinVisual.Video ? new BoxEl { HitTestVisible = false } : Poster(join),
                        TitleBand(band, bandRef),
                    ],
                }
                : Poster(join);
            return new BoxEl
            {
                Direction = 1,
                Width = Prop.Of(() => vp.Value.Width),
                Height = Prop.Of(() => vp.Value.Height),
                Fill = Tok.MediaLetterbox,
                Children = [body],
            };
        }

        /// <summary>The pop-out's title band: the only identity this chromeless window has, and the visible answer to
        /// "can I drag this?". Opacity is the TERMINAL, FadeChrome seeds the approach; the band is a sibling of the
        /// hole, never an ancestor of it.</summary>
        Element TitleBand(bool show, Ref<NodeHandle> bandRef) => new BoxEl
        {
            Grow = 1f, Direction = 1, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f, Height = 44f, Direction = 0, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(12f + Spacing.S, 0f, 12f, 0f), Gap = Spacing.M,
                    Gradient = Tok.ScrimTop,
                    Opacity = show ? 1f : 0f,
                    HitTestVisible = show,
                    Cursor = show ? CursorId.SizeAll : (CursorId?)null,
                    OnPointerDown = show ? _bandDown : null,
                    OnDrag = show ? _bandDrag : null,
                    OnClick = show ? _bandRelease : null,
                    OnDragCanceled = show ? _bandRelease : null,
                    OnPointerExit = show ? _bandRelease : null,
                    OnRealized = h => bandRef.Value = h,
                    Children =
                    [
                        // Bound REACTIVELY: the window outlives a track change.
                        new TextEl(Prop.Of(static () => CurrentTitle()))
                        {
                            Size = 15f, Weight = 600, Color = Tok.OnMediaPrimary,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            Shrink = 1f, MinWidth = 0f,
                        },
                    ],
                },
            ],
        };
    }

    // ══ 5. THE FULLSCREEN SURFACE ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The last placement state that did NOT resolve to fullscreen — the "before" of the next entry.</summary>
    static PlacementState s_beforeFullscreen = PlacementState.Music;

    /// <summary>The shell-lifetime half: observes every placement change and mounts the surface while fullscreen resolves.</summary>
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
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                    HitTestPassThrough = true,
                    Children = [Embed.Comp(static () => new FullscreenSurface())],
                });
        }
    }

    /// <summary>A REAL fullscreen: the OS window goes borderless-fullscreen on its monitor and restores what it found; the
    /// shell unmounts its chrome for the duration (one transport). Its lifetime IS the mode — mount = enter, unmount =
    /// exit, for every route out.</summary>
    sealed class FullscreenSurface : Component
    {
        NodeHandle _root;

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var priorOsFullscreen = UseRef(false);
            var priorFocus = UseRef<NodeHandle>(default);

            bool live = PlacementCore.Resolve(State.Surface.Value) == SurfacePlacement.Fullscreen;
            UseEffect(() => State.ReportLive(SurfacePlacement.Fullscreen, live), DepKey.From(live));
            UseEffect(() => () => State.ReportLive(SurfacePlacement.Fullscreen, false), DepKey.Empty);

            // REAL OS fullscreen, restoring the REMEMBERED prior state — never an unconditional false.
            UseLayoutEffect(() =>
            {
                priorOsFullscreen.Value = hooks.IsWindowFullscreen?.Invoke() ?? false;
                if (!priorOsFullscreen.Value) hooks.WindowSetFullscreen?.Invoke(true);
                return () => { if (!priorOsFullscreen.Value) hooks.WindowSetFullscreen?.Invoke(false); };
            }, DepKey.Empty);

            // A focus SCOPE at the root (Tab cannot walk into the unmounted chrome); focus parks INSIDE the video — but only
            // when the user asked for fullscreen; on unmount the scope pops and focus returns to whatever invoked it.
            UseLayoutEffect(() =>
            {
                if (Context.HostNode.IsNull) return null;
                var root = Context.HostNode;
                priorFocus.Value = hooks.GetFocus?.Invoke() ?? default;
                hooks.PushFocusScope?.Invoke(root);
                if (FullscreenEntry.UserInitiated(s_beforeFullscreen, State.Surface.Peek()))
                    hooks.FocusNode?.Invoke(root, false);
                return () =>
                {
                    hooks.PopFocusScope?.Invoke(root);
                    var back = priorFocus.Value;
                    priorFocus.Value = default;
                    if (!back.IsNull) hooks.RestoreFocus?.Invoke(back);
                };
            }, DepKey.Empty);

            return new BoxEl
            {
                Grow = 1f, Direction = 1, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                HitTestPassThrough = true,
                Focusable = true,
                OnRealized = h => _root = h,
                // Escape/F exit, handled at the ROOT (no focused control can swallow the way out). A modified key belongs
                // to whatever else claims it — Shift+Esc does nothing, deliberately.
                OnKeyDown = static e =>
                {
                    if (e.Handled || (e.Mods & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift)) != 0) return;
                    if (e.KeyCode != Keys.Escape && e.KeyCode != Keys.F) return;
                    e.Handled = true;
                    State.ExitFullscreen();
                },
                // Never leave focus null while the surface is up.
                OnFocusChanged = got =>
                {
                    if (got || _root.IsNull) return;
                    if ((hooks.GetFocus?.Invoke() ?? default).IsNull) hooks.FocusNode?.Invoke(_root, false);
                },
                Children = [ExitChrome()],
            };
        }

        /// <summary>The hover band: the title at top-left (the only identity on screen) and the way out at top-right. The
        /// opacity is on THIS band only, never on an ancestor of the hole.</summary>
        static Element ExitChrome() => new BoxEl
        {
            Grow = 1f, Direction = 1, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Shrink = 0f,
                    Height = 56f, Padding = new Edges4(12f + Spacing.S, 12f, 12f, 0f), Gap = Spacing.M,
                    Gradient = Tok.ScrimTop,
                    Opacity = 0f, HoverOpacity = 1f,
                    HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
                    Children =
                    [
                        // Bound REACTIVELY: the surface outlives a track change.
                        new TextEl(Prop.Of(static () => CurrentTitle()))
                        {
                            Size = 15f, Weight = 600, Color = Tok.OnMediaPrimary,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            Shrink = 1f, MinWidth = 0f,
                        },
                        ToolTip.Wrap(ExitFab(), Loc.Get(Strings.Player.VideoExitFullScreen)),
                    ],
                },
            ],
        };

        /// <summary>The way out over undimmed video: a 44-DIP ink-made plate with a card shadow — the one separation channel
        /// that survives the stage's inverted light ink ladder. Its glyph never dims on hover.</summary>
        static Element ExitFab() => new BoxEl
        {
            Width = 44f, Height = 44f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(44f),
            Fill = Design.StageInk.GlassPlate, HoverFill = Design.StageInk.GlassPlateHover, PressedFill = Design.StageInk.GlassPlatePressed,
            BorderWidth = 1f, BorderColor = Design.StageInk.Stroke,
            Shadow = Elevation.Card,
            BrushTransitionMs = Design.Motion.Faster,
            HoverScale = Design.Motion.ScaleEmphatic.Hover, PressScale = Design.Motion.ScaleEmphatic.Press,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = static () => State.ExitFullscreen(),
            Children = [new TextEl(Icons.BackToWindow) { Size = 18f, FontFamily = Theme.IconFont, Color = Design.StageInk.Ink, HoverColor = Design.StageInk.Ink }],
        };
    }
}
