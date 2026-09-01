using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.SpotifyLive;

namespace Wavee.Features.Video;

/// <summary>
/// The FULLSCREEN VIDEO surface — the full-bleed home for <see cref="SurfacePlacement.Fullscreen"/>, and a REAL
/// fullscreen: entering drives the OS window borderless-fullscreen on the monitor it sits on
/// (<c>InputHooks.WindowSetFullscreen</c> → <c>IWindow.SetFullscreen</c>), and the shell UNMOUNTS the title bar and the
/// global player bar for the duration (<c>WaveeShell</c>, gated on <see cref="PlaybackBridge.VideoFullscreenActive"/>).
/// Nothing here is merely hidden: extra layers stacked above a full-screen video defeat the composition fast path and
/// cost real GPU, so they leave the tree entirely.
///
/// <para><b>ONE transport.</b> The video's own auto-hiding transport is the only transport while this surface is up —
/// <see cref="PlaybackBridge.TransportOwnerNow"/> resolves to <see cref="TransportOwner.Fullscreen"/>, the player bar
/// unmounts, and no surface renders a second bar. This surface therefore RESERVES NOTHING: it used to carry two
/// pass-through bands (a <c>TitleBar.ExpandedHeight</c> strip at top and a <c>WaveeSize.PlayerBarH</c> strip at bottom,
/// inherited from <see cref="ImmersiveLyricsSurface"/>, whose own chrome legitimately stays reachable) and those bands
/// are exactly what stacked the global 72-DIP bar under the video's own. Full-bleed, no bands.</para>
///
/// <para><b>Prior OS state is REMEMBERED, never assumed.</b> Exit restores whatever <c>IsWindowFullscreen</c> reported
/// at enter — it does NOT call <c>WindowSetFullscreen(false)</c> unconditionally (which is what
/// <c>MediaPlayerElement</c>'s own overlay path does, clobbering a window the user had already put in OS fullscreen).</para>
///
/// <para>Content is the SHARED <see cref="PopOutVideoStage"/> (<c>PopOutVideoWindow.cs</c>) — the same stage
/// <see cref="InWindowVideoPip"/> and <c>PopOutVideoWindow</c> present, bound to <see cref="PlaybackBridge.VideoPlayer"/>
/// and keyed on the binding generation. This surface NEVER builds a <c>MediaPlayer</c>: the engine player is owned by
/// <c>FluentVideoMediaHost</c>, and every placement only BINDS a <see cref="FluentGpu.Controls.Media.MediaPlayerElement"/>
/// to it — which is what lets a placement move re-bind a presenter instead of restarting playback from 0.</para>
///
/// <para><b>NO OPACITY CHANNEL ON ANY ANCESTOR OF THE HOLE.</b> <c>DrawOp.DrawVideo</c> is a DestOut erase against the
/// back buffer; a fade on an ancestor multiplies cumulative opacity into it (a washed-out, translucent video with the
/// page bleeding through), and an <c>OpacityGroup</c>/blur/edge-fade ancestor is worse — it pushes an OFFSCREEN RT,
/// where the erase never reaches the real back buffer and the video vanishes entirely (the docked-video plan's §2
/// ancestor table). So <see cref="EnterTerminal"/>/<see cref="ExitTerminal"/> below carry a SCALE only — no
/// <c>Opacity</c> component — unlike <see cref="ImmersiveLyricsSurface"/>'s own terminals, which fade freely because
/// that stage has no video hole to protect. The scale terminal still carries the ⚠️ the same table names (the hole
/// scales about the node centre, the DirectComposition child does not, so there is a brief visual misalignment for the
/// ~200 ms of the transition) — accepted here because it is only a transient seam, not a total failure like a wash-out
/// or a vanished hole.</para>
/// </summary>
sealed class VideoFullscreenSurface : Component
{
    const SurfacePlacement Owned = SurfacePlacement.Fullscreen;   // the ONE placement this surface is responsible for
    const TransportOwner OwnedTransport = TransportOwner.Fullscreen;   // the ONE transport identity this surface claims

    const float TopBandH = 56f;      // the exit affordance's band — no identity/lyrics content here, just the way out
    const float ExitInset = 12f;

    NodeHandle _root;       // the surface root — the node the shield and the focus re-park park focus on
    NodeHandle _videoArea;  // the stage's own box — where the enter choreography parks focus (keyboard transport)

    /// <inheritdoc cref="ImmersiveLyricsSurface.EnterTerminal"/>
    /// <remarks>Scale-only — see the NO-OPACITY remark on the type doc comment. Reduced motion is the DEFAULT terminal
    /// (<c>Active = false</c>): a hard cut, not a same-value no-op animation.</remarks>
    internal static EnterExit EnterTerminal => Motion.ReducedMotion
        ? default
        : new EnterExit(Sx: 1.03f, Sy: 1.03f, Active: true);

    /// <inheritdoc cref="EnterTerminal"/>
    internal static EnterExit ExitTerminal => Motion.ReducedMotion
        ? default
        : new EnterExit(Sx: 1.02f, Sy: 1.02f, Active: true);

    /// <summary>Whether THIS mount was reached by an explicit user action (F11 / F / a double-click on the video / the
    /// menu's "Full screen" / the card's own fullscreen glyph) rather than an automatic reappearance — the surface's own
    /// analogue of the docked card's B12 ("never re-opened by a track change"). <c>WaveeShell</c> is the one place that
    /// can tell the two apart: this component fully remounts every time <c>Flow.Show</c> toggles it, so it has no memory
    /// of its OWN history, while the shell never unmounts and can compare <c>PlaybackBridge.VideoSurface.Requested</c>
    /// across the transition (see the shell's own remarks beside where this is written). Peeked ONCE, at mount, never
    /// subscribed — a live value here would fight the focus system on every unrelated placement-state change. Null (not
    /// wired) defaults to TRUE, matching <see cref="ImmersiveLyricsSurface"/>'s unconditional take-focus-at-mount
    /// behaviour.</summary>
    public IReadSignal<bool>? UserInitiated { get; init; }

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var hooks = UseContext(InputHooks.Current);
        // The OS window's fullscreen state as it was BEFORE we entered. Captured once, in the enter effect; restored
        // verbatim on exit. Never a hard `false` — see the type doc.
        var priorOsFullscreen = UseRef(false);
        // The node that had keyboard focus when fullscreen was entered, so exit can hand focus straight back to the
        // control the user invoked it from (the transport glyph, the rail card, the ⋯ row).
        var priorFocus = UseRef<NodeHandle>(default);
        if (b is null) return new BoxEl();

        // Reality report: mirrors VideoPlacementHost's / InWindowVideoPip's own Owned/SetVideoSurfaceLive idiom for
        // the other two owned surfaces (Detached / Floating) — the model's Live field is written ONLY by the owner.
        UseSignalEffect(() => b.SetVideoSurfaceLive(Owned, mounted: b.VideoPlacementNow() == Owned));
        UseEffect(() => () => b.SetVideoSurfaceLive(Owned, mounted: false), DepKey.Empty);

        // ── REAL OS FULLSCREEN + the enter/exit choreography ────────────────────────────────────────────────────────
        // This component's LIFETIME is the fullscreen mode (WaveeShell's Flow.Show mounts it iff the resolved placement
        // is Fullscreen), so mount = enter and unmount = exit for EVERY route out: Esc, F11, F, a double-click, the exit
        // FAB, the ⋯ ladder, a track that lost its video, or the placement model changing its mind. One effect, one
        // disposer — there is no second "am I really fullscreen" flag to get stuck.
        UseLayoutEffect(() =>
        {
            priorOsFullscreen.Value = hooks.IsWindowFullscreen?.Invoke() ?? false;
            if (!priorOsFullscreen.Value) hooks.WindowSetFullscreen?.Invoke(true);
            return () =>
            {
                // RESTORE the remembered state; never an unconditional false (that would drop a window the user had
                // already put in OS fullscreen out of it — MediaPlayerElement's own overlay path's bug).
                if (!priorOsFullscreen.Value) hooks.WindowSetFullscreen?.Invoke(false);
            };
        }, DepKey.Empty);

        // Escape routes to the FOCUSED node and bubbles up its ancestors, so the surface takes focus once at mount —
        // but ONLY on a user-initiated open (see the UserInitiated doc comment). The root stays focusable (and does
        // NOT set AllowFocusOnInteraction=false) so a click on the surface's own background lands focus back here
        // rather than clearing it — Escape keeps working after any interaction.
        //
        // Focus is parked INSIDE the video (the first focusable of the stage — the player frame, which owns Space /
        // ←→ / F11 / F) rather than on the bare root, so the keyboard drives playback the moment fullscreen opens; the
        // surface root is the fallback. A focus SCOPE rooted here is WinUI's TabFocusNavigation=Cycle: Tab can no longer
        // walk out of the fullscreen presentation into the (now unmounted, but Tab-order-adjacent) shell chrome.
        UseLayoutEffect(() =>
        {
            if (Context.HostNode.IsNull) return null;
            var root = Context.HostNode;
            priorFocus.Value = hooks.GetFocus?.Invoke() ?? default;
            hooks.PushFocusScope?.Invoke(root);
            if (UserInitiated?.Peek() ?? true)
            {
                var target = _videoArea.IsNull ? default : hooks.FirstFocusableIn?.Invoke(_videoArea) ?? default;
                hooks.FocusNode?.Invoke(target.IsNull ? root : target, false);
            }
            return () =>
            {
                hooks.PopFocusScope?.Invoke(root);
                // Hand focus back to whatever invoked fullscreen. A stale handle is harmless — the dispatcher ignores a
                // node that is no longer live — but never restore to the surface we are tearing down.
                var back = priorFocus.Value;
                priorFocus.Value = default;
                if (!back.IsNull) hooks.RestoreFocus?.Invoke(back);
            };
        }, DepKey.Empty);

        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            // FULL-BLEED, and therefore NOT HitTestPassThrough any more. The two pass-through bands this root used to
            // reserve (TitleBar.ExpandedHeight at top, WaveeSize.PlayerBarH at bottom) existed so the shell chrome
            // underneath stayed reachable — but under fullscreen there IS no chrome underneath (the shell unmounts it),
            // and those bands are exactly what let the global player bar stack under the video's own transport.
            Focusable = true,
            OnRealized = h => _root = h,
            OnKeyDown = e =>
            {
                if (e.Handled || (e.Mods & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift)) != 0) return;
                // Escape ALWAYS exits, and it is handled HERE — at the fullscreen root, the common ancestor of every
                // focusable thing in the presentation — so no focused control inside the chrome can swallow the one
                // guaranteed way out. It never ENTERS fullscreen and never quits the app.
                // F is the media-player convention for the same toggle (W3 binds it, plus double-click, on the element
                // itself; this arm covers focus sitting on the surface chrome instead of inside the player).
                if (e.KeyCode != Keys.Escape && e.KeyCode != Keys.F) return;
                e.Handled = true;
                b.ExitVideoFullscreen();
            },
            // NEVER LEAVE FOCUS NULL WHILE THE SURFACE IS UP — see ImmersiveLyricsSurface's identical remark. Runs
            // regardless of UserInitiated: once mounted, an automatic reappearance still owns keyboard Escape and
            // must not strand focus at null the first time something else steals it.
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
                    // Opaque floor: the SAME token PopOutVideoContent/InWindowVideoPip's no-player fallback uses. A
                    // STATIC fill, never an animated/transitioning one — see the NO-OPACITY-ANCESTOR remark on the
                    // type doc.
                    Fill = Tok.MediaLetterbox,
                    // HOVER-CONTAINER registration (the TrackRow / DockedVideoSurface idiom): one pointer registration,
                    // the engine's hover cascade fades the chrome band below in and out. No signal, no re-render.
                    OnPointerExit = static () => { },
                    Children =
                    [
                        VideoArea(b, h => _videoArea = h),
                        Shield(hooks),
                        ExitChrome(b),
                    ],
                },
            ],
        };
    }

    /// <summary>The surface's HIT SHIELD — childless, full-bleed, input-only. Same contract as
    /// <see cref="ImmersiveLyricsSurface.Shield"/>: the video area and the exit chrome are the only real hit targets in
    /// this ZStack, so anywhere else (the letterbox bars, empty space around the video) must still take the click
    /// itself rather than let it fall through to whatever page the surface covers.</summary>
    Element Shield(InputHooks hooks) => new BoxEl
    {
        Key = "fs:shield",
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
        OnClick = () => { if (!_root.IsNull) hooks.FocusNode?.Invoke(_root, false); },
    };

    /// <summary>The video area — hosts the SHARED <see cref="PopOutVideoStage"/>, keyed on the PLAYER identity (the
    /// binding generation) so a source change alone (a video→video skip) never remounts it; only a rebuilt player does.
    /// Poster + spinner cover the brief no-player window a placement MOVE leaves (close-then-open — B22), and overlay
    /// (rather than replace) a still-live stage while its resolved source's first frame has not landed yet — see the
    /// `switching` derivation below — exactly like <see cref="InWindowVideoPip.BuildVideoArea"/>.</summary>
    static Element VideoArea(PlaybackBridge b, Action<NodeHandle> realized)
    {
        var src = b.PopOutVideoSource.Value;      // subscribe → re-render (switching overlay/poster), not a remount — see stageKey below
        var binding = b.VideoPlayer.Value;        // subscribe → poster ↔ hole
        bool mount = VideoSurfaceMount.ShouldMountPlayerStage(binding.Player is not null);
        if (mount)
        {
            // PLAYER identity only — a source change (a video→video skip) must never remount the stage, only a
            // rebuilt player (a new Generation) may. See DockedVideoSurface's identical stage-key remark.
            string stageKey = "gen:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Bridge puts the placement ladder on the ⋯ menu so fullscreen is not a dead end — you can leave it FOR a
            // placement, not only back to wherever ReturnTo happens to point. Settings is deliberately null: the only
            // row that reads it is "Always on top", which is Detached-only and therefore unreachable from fullscreen.
            //
            // Fullscreen is the ONE surface that OWNS the transport, so the stage renders it (SuppressTransport stays
            // false) — and FullscreenRequested is wired so the transport's fullscreen glyph, its ⋯ Fullscreen row, F11
            // and F all EXIT through the app's placement model instead of reaching MediaPlayerElement's own overlay
            // fullscreen (a modal light-dismiss popup ON TOP of this surface, which Alt-Tab would then close).
            var stage = Embed.Comp(() => new PopOutVideoStage
            {
                Source = src, Player = b.VideoPlayer, Bridge = b,
                Host = new VideoStageHost(OwnedTransport, b.TransportOwnerNow, b.ExitVideoFullscreen),
                // This surface IS the fullscreen presentation — constant true, never keyed (it cannot change while this
                // surface is mounted; the surface unmounts instead). Without it the element's PresentingFullscreen stays
                // false here, so the transport drew the ENTER-fullscreen glyph while already fullscreen, the ⋯ row said
                // "Full screen" instead of "Exit full screen", and Escape missed its
                // `case Keys.Escape when PresentingFullscreen` arm — i.e. Escape did not leave.
                IsHostFullscreen = true,
            }) with { Key = "fsstage:" + stageKey };
            // While a stage is mounted the ENGINE element is the one loading affordance (poster + spinner +
            // "Starting playback…") — stacking the app's own LoadingOverlay on top produced two spinners at once.
            // The stage stays mounted and pumping across a source switch; the engine holds the previous frame and
            // crossfades to the new source's first frame on its own.
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = ColorF.Transparent,
                OnRealized = realized,
                Children = [stage],
            };
        }

        var track = b.CurrentTrack.Value;
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = Tok.MediaLetterbox,
            OnRealized = realized,
            Children =
            [
                new BoxEl { Grow = 1f, Opacity = 0.4f, ClipToBounds = true, Children = [Surfaces.ArtworkFill(track?.Image, 0f)] },
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

    /// <summary>The fullscreen chrome band — the track title at top-LEFT (the only identity in the presentation; the
    /// shell's title bar is unmounted, so without it nothing on screen names what is playing) and the way out at
    /// top-RIGHT: a <see cref="StageChrome.ExitFab"/> (the "way out" shape: an ink-made ground with a card shadow, the
    /// one separation channel that survives an inverted ink ladder — see that shape's own remarks for why it, not
    /// <see cref="StageChrome.ScrimFab"/>, is the correct plate over undimmed video). The tooltip names the DESTINATION,
    /// not the current state — the label discipline the placement spec §3.2 sets out ("every control is named for its
    /// destination, never a bare verb"). Reusing the menu's "Full screen" label here would name the state the user is
    /// already in, which reads as a no-op control.
    ///
    /// <para>The band rides the SAME hover gate as the video's own auto-hiding transport (<c>Opacity = 0,
    /// HoverOpacity = 1</c> — the docked card's idiom, one pointer registration and no signal): the title fades with the
    /// chrome, exactly as the contract asks. <b>The opacity is on THIS band only, never on an ancestor of the video
    /// hole</b> — see the type doc.</para></summary>
    static Element ExitChrome(PlaybackBridge b) => new BoxEl
    {
        Grow = 1f, Direction = 1, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Shrink = 0f,
                Height = TopBandH, Padding = new Edges4(ExitInset + Spacing.S, ExitInset, ExitInset, 0f),
                Gap = Spacing.M,
                Gradient = Tok.ScrimTop,
                Opacity = 0f, HoverOpacity = 1f,
                HoverDurationMs = WaveeMotion.Fast, HoverEasing = Easing.FluentDecelerate,
                Children =
                [
                    // Bound REACTIVELY (a Prop thunk over the bridge's signal), not a frozen render-time string: the
                    // surface outlives a track change, and a captured title would name the previous song forever.
                    new TextEl(Prop.Of(() => b.CurrentTrack.Value?.Title ?? ""))
                    {
                        Size = 15f, Weight = 600, Color = Tok.OnMediaPrimary,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        Shrink = 1f, MinWidth = 0f,
                    },
                    ToolTip.Wrap(
                        StageChrome.ExitFab(Icons.BackToWindow, () => b.ExitVideoFullscreen()),
                        Loc.Get(Strings.Player.VideoExitFullScreen)),
                ],
            },
        ],
    };
}
