using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Media;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.SpotifyLive;

namespace Wavee.Features.Video;

/// <summary>Which edges a resize gesture is dragging (the anchored edges are the ones NOT named here). A flags enum so
/// the four corners are just two edges at once and one handler covers all eight zones.</summary>
[Flags]
enum PipResizeEdge : byte { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

/// <summary>
/// An in-window, draggable + resizable picture-in-picture video surface. It floats over the shell (a top-Z, pass-through
/// overlay layer), anchored bottom-right by default, and hosts the SHARED <see cref="PopOutVideoStage"/>, which PRESENTS the
/// engine <c>MediaPlayer</c> owned by <c>FluentVideoMediaHost</c> (<see cref="PlaybackBridge.VideoPlayer"/>) for the resolved
/// <see cref="PopOutVideoSource"/>. NO surface builds a player — that ownership inversion is what keeps a placement move from
/// restarting the video from 0 and what makes the video's soundtrack the app's ONE current media.
/// Video composites here because the stage's <c>MediaPlayerElement</c> uses the PRIMARY window's AppHost registry (the
/// PiP lives INSIDE the main scene). Mutually exclusive with the detached pop-out window (exactly ONE mounted surface may
/// pump a given player). Visibility is derived: it mounts iff <see cref="PlaybackBridge.VideoPlacementNow"/> resolves
/// to <see cref="SurfacePlacement.Floating"/> — the ONE placement value, never a standalone flag. While it sits at its
/// default anchor it RESERVES layout (<see cref="PlaybackBridge.FloatingSurfaceReserve"/>) so it cannot cover page
/// content; dragging or resizing it releases the reservation, because placing it deliberately is the user opting into
/// a free-floating overlay.
///
/// CHROME (the WinUI compact-overlay read): there is NO permanent title bar — the video fills the whole rounded card, and
/// a slim <see cref="Tok.ScrimTop"/> strip carrying the ✕ HOVER-REVEALS over it. The reveal is declarative, not stateful:
/// the strip is <c>Opacity = 0, HoverOpacity = 1</c> and the engine cascades the CARD's hover onto it (a container reads as
/// hovered while the pointer is anywhere in its subtree, and reveal-affordance descendants follow — SceneRecorder
/// ResolveOpacity / AnimEngine.SetHoverDescendants). That costs no signal, no re-render, and no per-frame work; the card
/// earns its container status with a no-op <c>OnPointerExit</c> (the TrackRow / DetailTracks idiom — the handler exists to
/// register PointerBit, nothing else).
///
/// Drag/resize follow the SidebarResizeGrip idiom: BoxEl.OnDrag uses the engine's eager pointer capture, so the gesture
/// keeps firing as the pointer leaves the thin band; because the bands MOVE with the surface, the true window-space
/// pointer position is reconstructed each move as <c>local + scene.AbsoluteRect(handle)</c>. The surface is positioned
/// by a compositor-only <c>Transform</c> translation (layout stays put; only the paint/hit-test rect moves). Resizing is
/// eight-zone (four thin edge bands + four corners) via ONE handler parameterised by <see cref="PipResizeEdge"/>: a
/// left/top drag moves x/y so the OPPOSITE edge stays anchored. The bands live in a top-Z, hit-test-pass-through layer,
/// so only the bands themselves take input and everything inside (MediaPlayerElement's transport included) is untouched.
/// </summary>
sealed class InWindowVideoPip : Component
{
    // Tuning (DIP) — empirical, safe to tweak live.
    const float DefaultW = 360f, DefaultH = 202f;   // ~16:9 — the card is ALL video now, so this IS the video's size
    const float MinW = 240f, MinH = 135f;           // sensible floor (16:9)
    const float Margin = 16f;                        // gap from the window edges
    const float ScrimH = 30f;                        // the hover-revealed top strip (drag surface + ✕)
    const float CloseSize = 24f;                     // the ✕ hit target inside the strip
    const float EdgeBand = 6f;                       // the four thin edge resize bands
    const float CornerW = 14f, CornerH = 12f;        // the four corner resize zones (also the strip's side inset)
    const float ChromeFadeMs = WaveeMotion.Fast;     // hover-reveal / hide of the chrome

    // Entrance / exit (the compact-overlay pop). Channels = Opacity ONLY — deliberately NOT a layout transition, so the
    // node is never marked BoundsAnimated and a live drag/resize is never FLIP-chased; the TERMINALS carry the scale.
    // The recorder composites a node's local transform about its centre, and the anim compose seeds Tx/Ty from the
    // node's current LocalTransform, so the scale rides ON TOP of the bound translation instead of replacing it.
    // Reduced motion is handled where it belongs (AnimScheduler.ReducedSnap): the scale snaps, the fade still runs.
    static readonly LayoutTransition SurfaceMotion = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(240f, Easing.SmoothOut),
        Enter: new EnterExit(Sx: 0.94f, Sy: 0.94f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Sx: 0.96f, Sy: 0.96f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(140f, Easing.EaseInOut));

    // Live geometry (signals so the bound Transform / Width / Height re-fire on drag + resize).
    readonly Signal<float> _x = new(0f), _y = new(0f);
    readonly Signal<float> _w = new(DefaultW), _h = new(DefaultH);
    // false = still anchored bottom-right (tracks the viewport) AND reserving layout so it covers nothing; true = the
    // user has dragged it somewhere specific, which is them opting into a free-floating overlay. A signal because the
    // reservation effect must re-run on that edge.
    readonly Signal<bool> _placed = new(false);
    // Has the user DELIBERATELY sized this surface — a resize gesture, or a rect remembered from a previous session?
    // False (the default) means the HEIGHT follows the playing content's own aspect, so the mini player is exactly the
    // shape of what it is showing instead of a fixed ~16:9 box that letterboxes everything else. A plain field, not a
    // signal: only the height effect reads it, and it is written from gesture/seed code that runs outside render.
    bool _sized;
    float _fitRatio;   // last content aspect (h/w) the fit applied or acknowledged; 0 = none yet

    // Captured nodes for window-space pointer reconstruction (the grips move with the surface). The bands are indexed
    // by their build slot (see BuildResizeBands) — allocated ONCE, never per render.
    NodeHandle _dragNode;
    readonly NodeHandle[] _bandNodes = new NodeHandle[8];
    // The ambient viewport signal, captured in Render so the gesture handlers (which run outside Render) can Peek it.
    IReadSignal<Size2>? _vpSig;
    // Drag/resize gesture anchors (window-space pointer at gesture start + the values being dragged).
    float _startX, _startY, _startW, _startH, _startPx, _startPy;

    /// <summary>The settings store (frozen at mount — a stable instance). Remembers where the user dragged/sized the mini
    /// player. Never remembers whether a video was playing.</summary>
    public IAppSettings? Settings { get; init; }
    bool _seeded;

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        if (b is null) return new BoxEl();

        // Restore the remembered geometry once, before the first layout, so it opens where the user left it rather than
        // at the default corner and then jumping. A remembered rect also means "deliberately placed", so it keeps its
        // free-floating behavior (no layout reservation) exactly as it had when the user put it there.
        if (!_seeded)
        {
            _seeded = true;
            if (Settings is { } st &&
                PlacementPersistence.TryLoadRect(st.Get(WaveeSettings.VideoPipRect),
                    out float px, out float py, out float pw, out float ph))
            {
                _x.Value = px; _y.Value = py;
                _w.Value = Math.Max(MinW, pw); _h.Value = Math.Max(MinH, ph);
                _placed.Value = true;
                _sized = true;   // a remembered rect IS a deliberate size — never re-fit it to the content
            }
        }

        var vp = UseContextSignal(Viewport.Size);
        _vpSig = vp;

        // Reality + layout reservation, in one place and OUTSIDE render (an effect, never a write during render):
        // report which placement is actually mounted, and publish how much bottom space the page must keep clear.
        // Anchored ⇒ reserve exactly our height + the gap, so the surface cannot cover content; dragged or gone ⇒ 0.
        // _h IS the surface height (chrome no longer adds a band above the video — it overlays it), so the reserve
        // still matches the drawn card exactly.
        UseSignalEffect(() =>
        {
            bool live = b.VideoPlacementNow() == SurfacePlacement.Floating;
            b.SetVideoSurfaceLive(SurfacePlacement.Floating, live);   // reports only its OWN placement
            b.FloatingSurfaceReserve.Value = live && !_placed.Value ? _h.Value + Margin : 0f;
        });
        // Give the reservation back on unmount (logout / shell swap), or the page would keep a hole for a surface that
        // no longer exists.
        UseEffect(() => () => b.FloatingSurfaceReserve.Value = 0f, DepKey.Empty);

        // The card is ALL video, so its default HEIGHT is the content's own — not a fixed 16:9 box that letterboxes
        // anything else (the same defect the docked cap had). Width stays the user's/default one; only the height is
        // derived, so a resize still behaves exactly as before. 16:9 until the player reports a natural size, and never
        // taller than the free window height. A deliberate size (a resize gesture, a remembered rect) opts out for good.
        UseSignalEffect(() =>
        {
            var natural = b.VideoPlayer.Value.Player?.NaturalSize.Value ?? default;   // subscribe → fit when MF reports
            // The decoder's NaturalSize lands seconds after mount; fall back to the manifest's own dims (SEEDED on the
            // resolved source) so the mini player is the right shape AT MOUNT, and the decoder's later report becomes a
            // CONFIRM rather than a second reflow.
            if (natural.Width <= 0 || natural.Height <= 0)
            {
                var src = b.PopOutVideoSource.Value;                                  // subscribe → fit once the manifest resolves
                if (src is { NaturalWidth: > 0, NaturalHeight: > 0 }) natural = new SizeI(src.NaturalWidth, src.NaturalHeight);
            }
            float w = _w.Value;
            float free = vp.Value.Height - WaveeSize.PlayerBarH - (2f * Margin);
            float ratio = natural.Width > 0 && natural.Height > 0 ? (float)natural.Height / natural.Width : DefaultH / DefaultW;
            // A deliberate size (a resize gesture, a remembered rect) wins — until the CONTENT's shape changes.
            // A remembered near-square card letterboxing a 2.35:1 stream is the defect, not a preference: when a
            // source with a different aspect arrives, the height refits from the KEPT width (the user owns the
            // width, the content owns the shape — mini-player behavior everywhere else).
            bool aspectChanged = _fitRatio > 0f && Math.Abs(ratio - _fitRatio) > 0.01f;
            if (_sized && !aspectChanged) { if (_fitRatio <= 0f) _fitRatio = ratio; return; }
            _fitRatio = ratio;
            _h.Value = Math.Max(MinH, Math.Min(w * ratio, Math.Max(MinH, free)));
        });

        // Subscribe → mount/unmount the whole surface as the ONE resolved placement changes.
        if (b.VideoPlacementNow() != SurfacePlacement.Floating) return new BoxEl();

        // The floating surface. Compositor-only Transform positions it; layout keeps it at the layer origin.
        var surface = new BoxEl
        {
            Direction = 1, ClipToBounds = true, ZStack = true,
            Width = Prop.Of(() => _w.Value),
            Height = Prop.Of(() => _h.Value),
            Transform = Prop.Of(() =>
            {
                var v = vp.Value;                       // subscribe → re-clamp on window resize
                float w = _w.Value, h = _h.Value;       // subscribe → re-place on resize
                bool placed = _placed.Value;            // subscribe → re-anchor when the user first drags it
                float x = placed ? _x.Value : DefaultX(v, w);
                float y = placed ? _y.Value : DefaultY(v, h);
                return Affine2D.Translation(ClampX(x, v, w), ClampY(y, v, h));
            }),
            // Transparent: the video composites as a passive z-below hole, so nothing opaque may sit behind the video
            // rect (an opaque surface fill paints OVER it → black video). The hover scrim + MediaPlayerElement's
            // letterbox bars + the border/shadow give the PiP its solid framed look; the video area punches the hole.
            Fill = ColorF.Transparent,
            Corners = CornerRadius4.All(Radii.Card),
            BorderWidth = 1f,
            BorderColor = Prop.Of(() => Tok.StrokeCardDefault),
            Shadow = Elevation.Flyout,
            // The card is the hover CONTAINER for its chrome: a pointer handler is what earns it the engine's
            // HoverWithin flag while the pointer is anywhere in the subtree (video + transport + bands included), and
            // the reveal cascade drives every HoverOpacity descendant from it. The body is deliberately empty — we want
            // the registration, not the callback. Cached static lambda ⇒ no per-render alloc. Side effect (wanted): the
            // card now absorbs clicks instead of letting them fall through the pass-through layer onto the page below.
            OnPointerExit = static () => { },
            Layout = SurfaceMotion,
            Children =
            [
                // Layer 0 — the video, full-bleed. It IS the card.
                BuildVideoArea(b, Settings),
                // Layer 1 — the hover-revealed top chrome (drag surface + ✕), floating over the video.
                BuildChrome(b),
                // Layer 2 — the eight resize zones, topmost so a band always wins over the chrome beneath it.
                BuildResizeBands(),
            ],
        };

        // The full-bleed, pass-through overlay layer: only the surface itself takes input; everything else falls through
        // to the shell below (the runtime-banner / rail-overlay pattern).
        return new BoxEl
        {
            Grow = 1f, Direction = 1, HitTestPassThrough = true,
            Children = [ surface ],
        };
    }

    // ── chrome (the hover-revealed top strip: drag surface + close) ──────────────────────────────────
    // The strip is inset by CornerW left/right and EdgeBand top so its two interactive children never sit under a
    // resize band — the bands are topmost, and a ✕ whose corner is stolen by the NE zone is the classic overlay bug.
    Element BuildChrome(PlaybackBridge b)
    {
        var dragSurface = new BoxEl
        {
            Grow = 1f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center,
            Cursor = CursorId.SizeAll,
            OnRealized = h => _dragNode = h,
            OnPointerDown = OnDragDown,
            OnDrag = OnDragMove,
            OnClick = PersistGeometry,        // an OnDrag node's click is its release/commit edge (drag-end)
            OnDragCanceled = PersistGeometry,
        };

        var close = new BoxEl
        {
            Width = CloseSize, Height = CloseSize, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control),
            // On-media ink, not theme ink: this sits on a dark scrim over video in BOTH themes, so a light-theme
            // TextSecondary would be invisible here.
            Fill = ColorF.Transparent,
            HoverFill = Tok.OnMediaPrimary with { A = 0.14f },
            PressedFill = Tok.OnMediaPrimary with { A = 0.22f },
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            Cursor = CursorId.Hand,
            // ✕ turns video OFF (audio keeps playing, the surface goes away) — globally and stickily: no later track
            // re-opens it, only an explicit "watch video" (the player-bar primary / the placement picker) does. It is
            // reported as a HOST CLOSE for this surface's own placement so it shares the model's stale-close identity
            // guard; the model, not this view, decides that an in-app close means off (PlacementCore.HostClosed).
            OnClick = () => b.NotifyVideoSurfaceClosed(SurfacePlacement.Floating),
            Children =
            [
                new TextEl(Icons.Cancel)
                {
                    Size = 11f, FontFamily = Theme.IconFont,
                    Color = Tok.OnMediaSecondary, HoverColor = Tok.OnMediaPrimary,
                },
            ],
        };

        // The scrim strip itself: transparent at rest, faded in by the CARD's hover (HoverOpacity + the engine's
        // reveal cascade — no signal, no re-render). Tok.ScrimTop is the canonical top-down media scrim, the mirror of
        // the transport's Tok.ScrimBottom; its top corners match the card so it can't square off the rounded border.
        var strip = new BoxEl
        {
            Height = ScrimH, Shrink = 0f, Direction = 0,
            Padding = new Edges4(CornerW, EdgeBand, CornerW, 0f),
            Gradient = Tok.ScrimTop,
            Corners = new CornerRadius4(Radii.Card, Radii.Card, 0f, 0f),
            Opacity = 0f, HoverOpacity = 1f,
            HoverDurationMs = ChromeFadeMs, HoverEasing = Easing.FluentDecelerate,
            Children = [ dragSurface, close ],
        };

        // A pass-through positioner docks the strip to the top: the rest of this layer must not eat the video's input.
        return new BoxEl
        {
            Grow = 1f, Direction = 1, HitTestPassThrough = true,
            Children = [ strip ],
        };
    }

    // ── the video area — hosts the SHARED PopOutVideoStage, keyed on the PLAYER identity (the binding generation) so a
    // source change alone (a video→video skip) never remounts it, only a rebuilt player does; the switching overlay
    // below covers the gap by staying stacked on a still-live stage instead of replacing it ──────────────────────────
    // The stage PRESENTS the player owned by FluentVideoMediaHost (via PlaybackBridge.VideoPlayer); neither this surface nor
    // the stage builds one, which is why moving between the PiP and the pop-out no longer restarts the video from 0.
    //
    // There is exactly ONE state in which this box is transparent — a player is actually presenting, and the video needs
    // the transparent hole to composite through (it sits z-BELOW the UI swapchain, so anything opaque here paints OVER
    // it). In every OTHER state it paints a POSTER: the track's own artwork, dimmed, with a spinner. Resolving a manifest
    // and acquiring a DRM licence takes real time on every track change, and a surface that is a black rectangle for
    // those seconds reads as broken rather than as loading.
    static Element BuildVideoArea(PlaybackBridge b, IAppSettings? settings)
    {
        var src = b.PopOutVideoSource.Value;                          // subscribe → re-render (switching overlay/poster), not a remount — see stageKey below
        var binding = b.VideoPlayer.Value;                            // subscribe → poster ↔ hole
        // Mount whenever a player exists — a brief source null (override re-resolve) must NOT tear down the only MF pump.
        bool mount = VideoSurfaceMount.ShouldMountPlayerStage(binding.Player is not null);
        if (mount)
        {
            // PLAYER identity only — a source change (a video→video skip) must never remount the stage, only a
            // rebuilt player (a new Generation) may. See DockedVideoSurface's identical stage-key remark.
            string stageKey = "gen:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Bridge/Settings put the placement ladder on the video's OWN ⋯ menu, so the mini player can be moved from
            // the surface the user is actually looking at instead of only from the player bar across the window.
            // The mini player shares the window with the global 72-DIP player bar, so the BAR owns the transport
            // (TransportOwnerFor(Floating) == GlobalBar) and this stage suppresses its own — one transport, never two
            // stacked. Its fullscreen affordance ENTERS the app's fullscreen surface rather than the element's own
            // overlay fullscreen.
            var stage = Embed.Comp(() => new PopOutVideoStage
            {
                Source = src, Player = b.VideoPlayer, Bridge = b, Settings = settings,
                Host = new VideoStageHost(TransportOwner.Docked, b.TransportOwnerNow,
                    () => b.ShowVideoAt(SurfacePlacement.Fullscreen)),
            }) with { Key = "pipstage:" + stageKey };
            // While a stage is mounted the ENGINE element is the one loading affordance (poster + spinner +
            // "Starting playback…") — stacking the app's own LoadingOverlay on top produced two spinners at once.
            // The stage stays mounted and pumping across a source switch; the engine holds the previous frame and
            // crossfades to the new source's first frame on its own.
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = ColorF.Transparent,
                Children = [ stage ],
            };
        }

        var track = b.CurrentTrack.Value;
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = Tok.MediaLetterbox,
            Children =
            [
                // The artwork fills the frame behind the spinner, dimmed enough to read as a placeholder rather than as
                // content. A track with no art degrades to the letterbox fill — still never an empty rect.
                new BoxEl { Grow = 1f, Opacity = 0.4f, ClipToBounds = true, Children = [ Surfaces.ArtworkFill(track?.Image, 0f) ] },
                LoadingOverlay(track),
            ],
        };
    }

    static Element LoadingOverlay(Track? _) => new BoxEl
    {
        Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.S,
        HitTestPassThrough = true,
        Children =
        [
            ProgressRing.Indeterminate(size: 20f, foreground: Tok.TextOnAccentPrimary),
            new TextEl(Loc.Get(Strings.Player.Loading))
            {
                Size = 12f, Weight = 600, Color = Tok.TextOnAccentPrimary,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };

    // ── the eight resize zones ───────────────────────────────────────────────────────────────────────
    // A 3-row skeleton whose every non-band cell is HitTestPassThrough, so the layer is INVISIBLE to input except on the
    // bands themselves — the transport keeps its clicks, the wheel keeps falling through (pass-through is consulted by
    // HitAny too), and nothing here paints except the corner nub. Corner rows are CornerH tall while the edge bands stay
    // EdgeBand thin (FlexAlign.Start/End pins them to the outer edge), so the bottom band grazes only the transport's
    // 8px bottom padding instead of covering its controls.
    Element BuildResizeBands()
    {
        BoxEl Band(int slot, PipResizeEdge edge, CursorId cursor) => new BoxEl
        {
            Cursor = cursor,
            OnRealized = h => _bandNodes[slot] = h,
            OnPointerDown = p => OnResizeDown(slot, edge, p),
            OnDrag = p => OnResizeMove(slot, edge, p),
            OnClick = PersistGeometry,        // an OnDrag node's click is its release/commit edge (drag-end)
            OnDragCanceled = PersistGeometry,
        };

        return new BoxEl
        {
            Grow = 1f, Direction = 1, HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    Height = CornerH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Start, HitTestPassThrough = true,
                    Children =
                    [
                        Band(0, PipResizeEdge.Left | PipResizeEdge.Top, CursorId.SizeNWSE) with { Width = CornerW, Height = CornerH },
                        Band(1, PipResizeEdge.Top, CursorId.SizeNS) with { Grow = 1f, Height = EdgeBand },
                        Band(2, PipResizeEdge.Right | PipResizeEdge.Top, CursorId.SizeNESW) with { Width = CornerW, Height = CornerH },
                    ],
                },
                new BoxEl
                {
                    Grow = 1f, MinHeight = 0f, Direction = 0, HitTestPassThrough = true,
                    Children =
                    [
                        Band(3, PipResizeEdge.Left, CursorId.SizeWE) with { Width = EdgeBand },
                        new BoxEl { Grow = 1f, MinWidth = 0f, HitTestPassThrough = true },
                        Band(4, PipResizeEdge.Right, CursorId.SizeWE) with { Width = EdgeBand },
                    ],
                },
                new BoxEl
                {
                    Height = CornerH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.End, HitTestPassThrough = true,
                    Children =
                    [
                        Band(5, PipResizeEdge.Left | PipResizeEdge.Bottom, CursorId.SizeNESW) with { Width = CornerW, Height = CornerH },
                        Band(6, PipResizeEdge.Bottom, CursorId.SizeNS) with { Grow = 1f, Height = EdgeBand },
                        // The SE zone keeps the visual nub — the ONE painted pixel of this layer — and reveals it with
                        // the rest of the chrome (same HoverOpacity cascade), so a resting PiP is pure video.
                        Band(7, PipResizeEdge.Right | PipResizeEdge.Bottom, CursorId.SizeNWSE) with
                        {
                            Width = CornerW, Height = CornerH,
                            Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.End,
                            Opacity = 0f, HoverOpacity = 1f,
                            HoverDurationMs = ChromeFadeMs, HoverEasing = Easing.FluentDecelerate,
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

    // ── drag (move) ──────────────────────────────────────────────────────────────────────────────────
    void OnDragDown(Point2 local)
    {
        var scene = Context.Scene;
        if (scene is null || _dragNode.IsNull || !scene.IsLive(_dragNode)) return;
        var vp = ViewportPeek();
        // Commit the current effective position so the drag continues from where it is drawn (not the unplaced default).
        float w = _w.Peek(), h = _h.Peek();
        float x = _placed.Peek() ? _x.Peek() : DefaultX(vp, w);
        float y = _placed.Peek() ? _y.Peek() : DefaultY(vp, h);
        _x.Value = ClampX(x, vp, w); _y.Value = ClampY(y, vp, h);
        _placed.Value = true;   // → the layout reservation is released (the user placed it deliberately)
        _startX = _x.Peek(); _startY = _y.Peek();
        var abs = scene.AbsoluteRect(_dragNode);
        _startPx = local.X + abs.X; _startPy = local.Y + abs.Y;
    }

    void OnDragMove(Point2 local)
    {
        var scene = Context.Scene;
        if (scene is null || _dragNode.IsNull || !scene.IsLive(_dragNode)) return;
        var abs = scene.AbsoluteRect(_dragNode);       // the drag surface moves WITH the surface → reconstruct window-X/Y
        float px = local.X + abs.X, py = local.Y + abs.Y;
        var vp = ViewportPeek();
        _x.Value = ClampX(_startX + (px - _startPx), vp, _w.Peek());
        _y.Value = ClampY(_startY + (py - _startPy), vp, _h.Peek());
    }

    // ── resize (all eight zones, one handler) ────────────────────────────────────────────────────────
    void OnResizeDown(int slot, PipResizeEdge edge, Point2 local)
    {
        var scene = Context.Scene;
        var band = _bandNodes[slot];
        if (scene is null || band.IsNull || !scene.IsLive(band)) return;
        var vp = ViewportPeek();
        // A left/top drag MOVES x/y, so — exactly like a move gesture — the surface must first adopt the position it is
        // actually DRAWN at, or the first sample would snap an unplaced surface to the origin.
        float w = _w.Peek(), h = _h.Peek();
        float x = _placed.Peek() ? _x.Peek() : DefaultX(vp, w);
        float y = _placed.Peek() ? _y.Peek() : DefaultY(vp, h);
        _x.Value = ClampX(x, vp, w); _y.Value = ClampY(y, vp, h);
        _placed.Value = true;   // resizing is also deliberate placement → release the layout reservation
        _sized = true;          // …and it ends the content-aspect fit: the user's size is the size now
        _startX = _x.Peek(); _startY = _y.Peek(); _startW = w; _startH = h;
        var abs = scene.AbsoluteRect(band);
        _startPx = local.X + abs.X; _startPy = local.Y + abs.Y;
    }

    void OnResizeMove(int slot, PipResizeEdge edge, Point2 local)
    {
        var scene = Context.Scene;
        var band = _bandNodes[slot];
        if (scene is null || band.IsNull || !scene.IsLive(band)) return;
        var abs = scene.AbsoluteRect(band);            // the band moves WITH the surface → reconstruct window-X/Y
        float px = local.X + abs.X, py = local.Y + abs.Y;
        float dx = px - _startPx, dy = py - _startPy;
        var vp = ViewportPeek();

        // The ANCHORED edge is the one not being dragged, so it is what the moving edge is measured against: growing
        // right/bottom is bounded by the viewport, growing left/top is bounded by the frozen far edge.
        float right = _startX + _startW, bottom = _startY + _startH;
        if ((edge & PipResizeEdge.Left) != 0)
        {
            float w = Math.Clamp(_startW - dx, MinW, Math.Max(MinW, right - Margin));
            _w.Value = w; _x.Value = right - w;
        }
        else if ((edge & PipResizeEdge.Right) != 0)
        {
            _w.Value = Math.Clamp(_startW + dx, MinW, Math.Max(MinW, vp.Width - Margin - _startX));
        }
        if ((edge & PipResizeEdge.Top) != 0)
        {
            float h = Math.Clamp(_startH - dy, MinH, Math.Max(MinH, bottom - Margin));
            _h.Value = h; _y.Value = bottom - h;
        }
        else if ((edge & PipResizeEdge.Bottom) != 0)
        {
            _h.Value = Math.Clamp(_startH + dy, MinH, Math.Max(MinH, vp.Height - Margin - WaveeSize.PlayerBarH - _startY));
        }
    }

    // Written at the END of a move/resize gesture (an OnDrag node's click IS its release edge), so one write per gesture
    // rather than one per pointer sample. Only a DELIBERATELY placed surface is persisted: while it still sits at its
    // anchored home there is nothing to remember, and writing the computed anchor would freeze it against a later window
    // resize (it would stop tracking the corner).
    void PersistGeometry()
    {
        if (Settings is not { } settings || !_placed.Peek()) return;
        settings.Set(WaveeSettings.VideoPipRect,
            PlacementPersistence.SaveRect(_x.Peek(), _y.Peek(), _w.Peek(), _h.Peek()));
    }

    // ── placement helpers ────────────────────────────────────────────────────────────────────────────
    static float DefaultX(Size2 vp, float w) => vp.Width - w - Margin;
    static float DefaultY(Size2 vp, float h) => vp.Height - h - WaveeSize.PlayerBarH - Margin;

    static float ClampX(float x, Size2 vp, float w)
        => Math.Clamp(x, Margin, Math.Max(Margin, vp.Width - w - Margin));
    static float ClampY(float y, Size2 vp, float h)
        => Math.Clamp(y, Margin, Math.Max(Margin, vp.Height - h - WaveeSize.PlayerBarH - Margin));

    // Viewport size without a subscription — used inside the gesture handlers (they run outside Render).
    Size2 ViewportPeek() => _vpSig?.Peek() ?? new Size2(1280f, 720f);
}
