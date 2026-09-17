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
// THE FOUR PRESENTERS OF ONE PLAYER. Every surface here binds `Playback.Video.Player` (owner H's decode host) and
// `Video.State` (the one placement value) — none builds a player, none holds a second visibility flag, none issues an
// engine-video command of its own: play/pause/commit go through the session transport (`Playback.*`), a scrub PREVIEW
// through `Playback.Video.Seek(accurate: false)` planned by `Playback.Video.SeekPlanner`. The only engine-video surface
// API used is `MediaPlayerElement` (the element) and the bound player's `NaturalSize` read, so the engine's video
// rework lands behind H's host in one place.
//
// THE RULES THIS FILE IS SHAPED BY (ch 24 §0):
//   1. No LayoutTransition, no Opacity, no offscreen-RT effect on ANY ancestor of a video hole. The hole is a DestOut
//      erase against the real back buffer: an ancestor opacity washes it out, an ancestor RT makes it vanish. Hence the
//      docked card carries no transition and the fullscreen terminals are SCALE-ONLY.
//   2. Exactly one mounted surface, derived from one value (`PlacementCore.Resolve`), plus `DockedHosting.ShouldMount`
//      for the two docked faces. Exactly one transport per window (`State.Transport`).
//   3. The stage key is PLAYER identity only (the binding generation): a video→video skip keeps the element mounted
//      and pumping. Every frozen element prop that can change under a live stage is folded into the key.
//   4. A no-player state is the current track's artwork at 0.4 over the letterbox — never a black rectangle — on ALL
//      FOUR surfaces (the pop-out's bare rect was a 0.2.9 defect, W14b). The spinner waits out the join budget
//      (`Joining`), and uses the on-media ink (the 0.2.9 `TextOnAccentPrimary` was black-on-black in dark, §4.5).
//   5. Hover chrome costs no signal and no re-render: `Opacity 0 / HoverOpacity 1` under a container that earns hover
//      with a no-op `OnPointerExit`.
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
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Video
{
    // ══ 0. MOUNT POINTS ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>How much bottom space the page must keep clear while the mini player sits ANCHORED at its home (its height
    /// plus the gap; 0 once the user places it, or when it is not mounted). The content host insets its bottom by this.</summary>
    public static readonly FloatSignal FloatingSurfaceReserve = new(0f);

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
    /// input), the pop-out window's lifecycle owner (`Video.Host.cs`'s controller leaf) and the host observer
    /// (`Video.Host.Wiring.cs`: the session mirror, the availability fold, the badge-lit prefetch) — each mounted exactly
    /// once here. It also installs the pop-out's content and title factories.</summary>
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
            ],
        };
    }

    // MOUNT POINT (stage B contract)
    /// <summary>The full-bleed fullscreen layer. Its enter/exit terminals are SCALE ONLY — a cross-fade would wash the
    /// hole out. The surface remounts fresh on every entry; the entry observer here is what tells it whether the user
    /// asked (so it may take focus).</summary>
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
    static readonly Func<IReadOnlyList<MenuFlyoutItem>> s_stageMenu = static () => PlacementMenu(includeFullscreen: false);
    static readonly Action s_toggleDetachedFullscreen = static () => State.DetachedFullscreen.Value = !State.DetachedFullscreen.Peek();

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
    internal static Element PosterGround(string? url) => new BoxEl
    {
        Grow = 1f, Opacity = 0.4f, ClipToBounds = true,
        Children = url is { Length: > 0 } ? [Controls.ArtworkFill(url, 0f)] : [],
    };

    /// <summary>The "no player yet" composition every surface shares: the live art over the letterbox, and the join
    /// spinner that waits out the budget.</summary>
    static Element Poster() => new BoxEl
    {
        Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = Tok.MediaLetterbox,
        Children = [LivePoster.Make(), Embed.Comp(static () => new JoinOverlay())],
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

    /// <summary>The loading affordance over a poster: nothing for <see cref="Joining.SpinnerDelayMs"/>, then a 20-DIP ring
    /// and "Loading…" in on-media ink. Mounted only past the delay, so the spinner never animates unseen.</summary>
    sealed class JoinOverlay : Component
    {
        readonly Signal<bool> _shown = new(false);

        public override Element Render()
        {
            UseTimeout(() => _shown.Value = true, Joining.SpinnerDelayMs, DepKey.Empty);
            return new BoxEl
            {
                Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestPassThrough = true,
                Children =
                [
                    Flow.Show(() => _shown.Value, new BoxEl
                    {
                        Direction = 1, AlignItems = FlexAlign.Center, Gap = Spacing.S, HitTestVisible = false,
                        Children =
                        [
                            ProgressRing.Indeterminate(size: 20f, foreground: Tok.OnMediaPrimary),
                            new TextEl(Loc.Get(Strings.Player.Loading))
                            {
                                Size = 12f, Weight = 600, Color = Tok.OnMediaSecondary,
                                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                            },
                        ],
                    }),
                ],
            };
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

    /// <summary>One presenter of the bound player. Props freeze at mount: the host bundle is constant per surface and the
    /// host-fullscreen bit is folded into the element key, as is the transport-ownership bit.</summary>
    sealed class PlayerStage(StageHost host, bool hostFullscreen) : Component
    {
        public override Element Render()
        {
            UseSignalEffect(static () => { _ = Prefs.Epoch.Value; SyncAspect(); });
            var binding = Playback.Video.Player.Value;
            // The player vanished: render nothing — the owning surface unmounts this on the same pass.
            if (binding.Player is not { } player) return new BoxEl { Grow = 1f, MinHeight = 0f };
            bool suppress = State.Transport.Value != host.Identity;
            bool drag = StageInput.DragMovesWindow(host.Identity, hostFullscreen);
            var cursor = StageInput.HidesCursorWindowed(host.Identity) ? CursorAutoHidePolicy.Always : CursorAutoHidePolicy.FullscreenOnly;
            return Embed.Comp(() => new MediaPlayerElement
            {
                Player = player,
                Stretch = MediaStretch.Uniform,
                PlayRequested = s_play,
                PauseRequested = s_pause,
                SeekRequested = s_seek,
                AspectMode = s_aspect,
                CustomAspectRatio = s_customRatio,
                AspectModeChanged = s_aspectChanged,
                MoreMenuItems = s_stageMenu,
                PosterContent = LivePoster.Make(),
                SuppressTransport = suppress,
                FullscreenRequested = host.FullscreenRequested,
                IsHostFullscreen = hostFullscreen,
                DragMovesWindow = drag,
                CursorAutoHide = cursor,
            }) with
            {
                Key = "player:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + (suppress ? ":t0" : ":t1") + (hostFullscreen ? ":f1" : ":f0"),
            };
        }
    }

    static string GenKey(long generation) => "gen:" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ══ 2. THE DOCKED CARD (Cap + PageStage faces) ═══════════════════════════════════════════════════════════════════

    /// <summary>The docked music-video surface: video that simply LIVES in the app at zero commitment. Two faces, one
    /// card — the rail's Cap (height follows the content's own aspect, splitter-overridable) and the watch page's
    /// PageStage (the page owns the envelope). NO transition, NO shadow, NO corners of its own.</summary>
    sealed class DockedSurface(DockedFace face, string? ownerStagePlayable) : Component
    {
        /// <summary>The <c>Activation.IsActive</c> OVERRIDE for this card's element: window visibility AND "immersive lyrics
        /// is not covering the rail". A STABLE instance — a parked, non-decorative element keeps pumping, so the video
        /// picks up mid-song when the stage closes.</summary>
        readonly Signal<bool> _activeGate = new(true);
        string? _fittedFor;
        (string Key, float RailW, int Nw, int Nh, float H, bool Pinned, string Src) _loggedFit;
        (VideoAspectMode Mode, double Custom, TransportOwner Owner) _loggedPolicy = ((VideoAspectMode)255, -1d, (TransportOwner)255);
        (SurfacePlacement Resolved, string? Playing, string Active, bool Mounts) _loggedHost = ((SurfacePlacement)255, " ", " ", false);

        public override Element Render()
        {
            var windowVisible = UseContext(Activation.IsActive);
            UseSignalEffect(() => _activeGate.Value = (windowVisible is null || windowVisible.Value) && !Shell.Ui.ImmersiveLyrics.Value);
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
                float height = pinned ? Shell.Ui.DockedVideoHeight.Peek() : Shell.FitDockedVideoHeight(railW, natural.Width, natural.Height);
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
            UseEffect(() => () => State.ReportLive(SurfacePlacement.Docked, false), DepKey.Empty);

            if (!mount) return new BoxEl();

            BoxEl card = new BoxEl
            {
                ZStack = true, ClipToBounds = true,
                // NO Shadow, NO Layout/Enter/Exit: a content-layer rung, and any transition here erases the hole.
                OnPointerExit = static () => { },
                // Space = play/pause (no modifier check, by design); Escape is NOT mirrored — this face is never fullscreen.
                OnKeyDown = static e =>
                {
                    if (e.KeyCode != Keys.Space) return;
                    e.Handled = true;
                    Playback.TogglePlay();
                },
                Focusable = true,
                Children = [VideoArea(), Chrome()],
            };

            return face == DockedFace.PageStage
                ? card with { Grow = 1f, MinHeight = 0f, Fill = Tok.MediaLetterbox }
                : card with { Shrink = 0f, MinWidth = 0f, Height = Shell.Ui.DockedVideoHeight, Fill = Tok.MediaLetterbox };
        }

        Element VideoArea()
        {
            _ = Playback.Video.Source.Value;                 // subscribe → a source switch re-renders, never remounts
            var binding = Playback.Video.Player.Value;       // subscribe → poster ↔ hole
            if (!SurfaceMount.ShouldMountPlayerStage(binding.Player is not null) || binding.Player is not { } player)
                return Poster();

            bool suppress = State.Transport.Value != TransportOwner.Docked;
            Element element = Embed.Comp(() => new MediaPlayerElement
            {
                Player = player,
                PlayRequested = s_play,
                PauseRequested = s_pause,
                SeekRequested = s_seek,
                Stretch = MediaStretch.Uniform,
                AspectMode = s_aspect,
                CustomAspectRatio = s_customRatio,
                AspectModeChanged = s_aspectChanged,
                CornerRadius = 0f,
                AreTransportControlsEnabled = true,
                SuppressTransport = suppress,
                ShowLetterboxBars = true,
                IsDecorative = false,                         // decorative skips the pump while parked
                PosterContent = LivePoster.Make(),
                MoreMenuItems = s_stageMenu,
                FullscreenRequested = EnterFullscreen,
            }) with { Key = "dockstage:" + GenKey(binding.Generation) + (suppress ? ":t0" : ":t1") };
            // The override lives tight around the element that reads it.
            Element stage = Ctx.Provide<IReadSignal<bool>?>(Activation.IsActive, _activeGate, element);
            return new BoxEl { Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = ColorF.Transparent, Children = [stage] };
        }

        static void EnterFullscreen()
        {
            Announcer.Say(Loc.Get(Strings.Player.VideoFullScreen));
            State.OpenAt(SurfacePlacement.Fullscreen);
        }

        /// <summary>The hover-revealed top strip: mini player · full screen · off, right-aligned, no label.</summary>
        static Element Chrome() => new BoxEl
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
    }

    // ══ 3. THE IN-WINDOW MINI PLAYER ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The draggable, eight-zone-resizable picture-in-picture card, anchored bottom-right until the user places
    /// it. Geometry lives in its own signals and reaches the node through bound <c>Transform</c>/<c>Width</c>/<c>Height</c>
    /// thunks, so a drag never re-renders.</summary>
    sealed class PipSurface : Component
    {
        // The scale rides the TERMINALS; Channels = Opacity only, so a live drag/resize is never FLIP-chased.
        static readonly LayoutTransition SurfaceMotion = new(
            TransitionChannels.Opacity,
            TransitionDynamics.Tween(240f, Easing.SmoothOut),
            Enter: new EnterExit(Sx: 0.94f, Sy: 0.94f, Opacity: 0f, Active: true),
            Exit: new EnterExit(Sx: 0.96f, Sy: 0.96f, Opacity: 0f, Active: true),
            ExitDynamics: TransitionDynamics.Tween(140f, Easing.EaseInOut));

        const float ScrimH = 30f, CloseSize = 24f;

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

            bool live = PlacementCore.Resolve(State.Surface.Value) == SurfacePlacement.Floating;
            UseEffect(() => State.ReportLive(SurfacePlacement.Floating, live), DepKey.From(live));
            // The reservation the page insets by: anchored ⇒ height + gap; placed or gone ⇒ 0.
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

            if (!live) return new BoxEl();

            var surface = new BoxEl
            {
                Direction = 1, ClipToBounds = true, ZStack = true,
                Width = Prop.Of(() => _w.Value),
                Height = Prop.Of(() => _h.Value),
                Transform = Prop.Of(() =>
                {
                    var v = vp.Value;
                    float w = _w.Value, h = _h.Value;
                    var (x, y) = _placed.Value ? (_x.Value, _y.Value) : Pip.Anchor(w, h, v.Width, v.Height);
                    return Affine2D.Translation(PipGesture.ClampX(x, v.Width, w), PipGesture.ClampY(y, v.Height, h));
                }),
                // Transparent: the video composites as a hole; anything opaque behind the video rect paints OVER it.
                Fill = ColorF.Transparent,
                Corners = CornerRadius4.All(Radii.Card),
                BorderWidth = 1f,
                BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
                Shadow = Elevation.Flyout,
                OnPointerExit = static () => { },   // the hover CONTAINER (and it absorbs clicks over the page)
                Layout = SurfaceMotion,
                Children = [VideoArea(), Chrome(), ResizeBands()],
            };

            return new BoxEl { Grow = 1f, Direction = 1, HitTestPassThrough = true, Children = [surface] };
        }

        static Element VideoArea()
        {
            _ = Playback.Video.Source.Value;
            var binding = Playback.Video.Player.Value;
            if (!SurfaceMount.ShouldMountPlayerStage(binding.Player is not null)) return Poster();
            var stage = Embed.Comp(static () => new PlayerStage(
                new StageHost(TransportOwner.Docked, static () => State.OpenAt(SurfacePlacement.Fullscreen)), hostFullscreen: false))
                with { Key = "pipstage:" + GenKey(binding.Generation) };
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
        public override Element Render()
        {
            var vp = UseContextSignal(Viewport.Size);
            var binding = Playback.Video.Player.Value;
            bool live = SurfaceMount.ShouldMountPlayerStage(binding.Player is not null);
            // THIS window's own fullscreen mode (never SurfacePlacement.Fullscreen). Read with .Value: it is in the key.
            bool hostFullscreen = State.DetachedFullscreen.Value;
            Element body = live
                ? new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                    Children =
                    [
                        Embed.Comp(() => new PlayerStage(new StageHost(TransportOwner.PopOut, s_toggleDetachedFullscreen), hostFullscreen))
                            with { Key = "stage:" + GenKey(binding.Generation) + (hostFullscreen ? ":f1" : ":f0") },
                    ],
                }
                : Poster();
            return new BoxEl
            {
                Direction = 1,
                Width = Prop.Of(() => vp.Value.Width),
                Height = Prop.Of(() => vp.Value.Height),
                Fill = Tok.MediaLetterbox,
                Children = [body],
            };
        }
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
            // Reduced motion is the DEFAULT terminal (a hard cut), read as a value.
            bool reduced = Design.Reduced;
            return Flow.Show(
                static () => PlacementCore.Resolve(State.Surface.Value) == SurfacePlacement.Fullscreen,
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                    HitTestPassThrough = true,
                    Enter = reduced ? default : new EnterExit(Sx: 1.03f, Sy: 1.03f, Active: true),
                    Exit = reduced ? default : new EnterExit(Sx: 1.02f, Sy: 1.02f, Active: true),
                    Children = [Embed.Comp(static () => new FullscreenSurface())],
                });
        }
    }

    /// <summary>A REAL fullscreen: the OS window goes borderless-fullscreen on its monitor and restores what it found; the
    /// shell unmounts its chrome for the duration (one transport). Its lifetime IS the mode — mount = enter, unmount =
    /// exit, for every route out.</summary>
    sealed class FullscreenSurface : Component
    {
        NodeHandle _root, _videoArea;

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
                {
                    var target = _videoArea.IsNull ? default : hooks.FirstFocusableIn?.Invoke(_videoArea) ?? default;
                    hooks.FocusNode?.Invoke(target.IsNull ? root : target, false);
                }
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
                Children =
                [
                    new BoxEl
                    {
                        Grow = 1f, Shrink = 1f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
                        Fill = Tok.MediaLetterbox,            // STATIC, never animated
                        OnPointerExit = static () => { },
                        // The shield comes FIRST: the hit walk keeps the LAST matching child.
                        Children = [Shield(hooks), VideoArea(), ExitChrome()],
                    },
                ],
            };
        }

        Element Shield(InputHooks hooks) => new BoxEl
        {
            Key = "fs:shield",
            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
            OnClick = () => { if (!_root.IsNull) hooks.FocusNode?.Invoke(_root, false); },
        };

        Element VideoArea()
        {
            _ = Playback.Video.Source.Value;
            var binding = Playback.Video.Player.Value;
            Element child = SurfaceMount.ShouldMountPlayerStage(binding.Player is not null)
                ? Embed.Comp(static () => new PlayerStage(new StageHost(TransportOwner.Fullscreen, static () => State.ExitFullscreen()), hostFullscreen: true))
                    with { Key = "fsstage:" + GenKey(binding.Generation) }
                : Poster();   // a placement MOVE is close-then-open: cover the gap, never a black screen
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = ColorF.Transparent,
                OnRealized = h => _videoArea = h,
                Children = [child],
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
