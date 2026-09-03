using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Backend.Playback;
using Wavee.Core;
using Wavee.Features.Video;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Row 3 — the adaptive player bar (72px dock), the MUSA single-row layout: now-playing · transport+seek · controls, all
// on ONE centered row that progressively collapses with the viewport width. A full-bleed TOP EDGE is a 1px hairline at
// rest and an animated indeterminate sweep while Loading/Buffering (a browser-style global-activity cue, kept SEPARATE
// from the centre seek bar's actual position).
//
// Cost discipline (signals-first): the bar's Render reads only the LOW-frequency signals (track/play-state/shuffle/repeat/
// buffering/error/viewport) so it re-renders only on those. The HOT values never re-render the bar — the SEEK is a
// bespoke compositor-bound SeekBar sub-component (smooth interpolated playhead + scrub-gate), the VOLUME is Slider.Create (signal-bound)
// (compositor-only), and the time labels + volume glyph are isolated sub-components that re-render at ~1 Hz.
enum PlayerState : byte { NoTrack, Loading, Reconnecting, Error, Active }

sealed class PlayerBar : Component
{
    static readonly bool DiagEnabled = Diag.EnvFlag("WAVEE_PLAYERBAR_DIAG");

    public override Element Render()
    {
        var viewport = UseContextSignal(Viewport.Size);
        float initialWidth = viewport.Peek().Width;
        var layout = UseSignal(PlayerBarLayout.Initial(initialWidth));
        var initialized = UseRef(initialWidth > 0f);

        UseSignalEffect(() =>
        {
            var prev = layout.Peek();
            float width = viewport.Value.Width;
            var next = PlayerBarLayout.Resolve(width, in prev, initialized.Value);
            if (width > 0f) initialized.Value = true;
            if (next.Equals(prev)) return;
            if (DiagEnabled)
                WaveeLog.Instance.Event(WaveeLogLevel.Debug, "ui", "playerbar.layout_band", "Player bar layout band changed",
                    fields:
                    [
                        WaveeLogField.Of("from", (int)prev.Tier),
                        WaveeLogField.Of("to", (int)next.Tier),
                        WaveeLogField.Of("viewportW", viewport.Peek().Width),
                    ]);
            layout.Value = next;
        });

        return Embed.Comp(() => new PlayerBarContent(layout));
    }
}

sealed class PlayerBarContent : Component
{
    readonly IReadSignal<PlayerBarLayout> _layout;
    static readonly bool DiagEnabled = Diag.EnvFlag("WAVEE_PLAYERBAR_DIAG");
    static int s_renderCount;

    public PlayerBarContent(IReadSignal<PlayerBarLayout> layout) => _layout = layout;

    static readonly LayoutTransition MoveMotion = new(
        TransitionChannels.Bounds,
        TransitionDynamics.Tween(Motion.ControlFast, Easing.FluentPopOpen),
        SizeMode.Reveal);

    // WinUI AddDeleteThemeTransition: the entrant FADES in (opacity, OS decelerate curve) AND its layout width eases
    // 0→natural so the NEIGHBOURS reflow — they slide over to make room instead of snapping (the WinUI "make room"
    // feel). SizeMode.Reflow routes through RunReflowLayout, which is NOT resize-gated, so this animates even while the
    // window is dragged across a breakpoint (where the FLIP/projection path stays suppressed by design). ~250ms in.
    static readonly LayoutTransition ItemMotion = new(
        TransitionChannels.Bounds | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Motion.ControlNormal, Easing.SmoothOut),
        SizeMode.Reflow,
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Motion.ControlFast, Easing.SmoothOut));

    // A soft critical red for the Error state's title (app-local; the chrome stays WinUI-faithful elsewhere).
    static readonly ColorF Critical = new(0.93f, 0.42f, 0.45f, 1f);
    // Shared marquee cadence for the now-playing title: a capped one-way travel time keeps long sibling lines aligned,
    // while Marquee.Speed prevents a barely-overflowing title from creeping invisibly. PingPong holds at the tail.
    const float MarqueeCycleMs = 10000f;
    const float MarqueeEndPauseMs = 2500f;
    // The video SPLIT button's disclosure half — the one documented exception to the 32-DIP transport floor, and the
    // same exception WinUI's own SplitButton makes (SecondaryButtonSize is narrower than ControlHeight; only the HEIGHT
    // stays on the ladder, which is why `boxHeight: buttonBox` is passed with it). It was `buttonBox * 0.55f` /
    // `buttonGlyph * 0.62f` — 17.6 × 9.9 once the box floor landed, which is a chevron you cannot hit.
    const float SplitChevronW = 20f, SplitChevronGlyph = 10f;
    // The volume rail is the STOCK slider: Slider.DefaultStyle already IS a 4px/2r track, and the only thing this bar
    // used to override was the THUMB — 12/8/6 against the stock 22/12/10, i.e. a 12-DIP grab target on the control the
    // user drags most. SeekBar derives every metric from Slider.DefaultStyle for exactly this reason; the volume rail
    // now does the same by simply not passing a style. `thickness` must clear the 22-DIP ring or the slider's own
    // over-thumb hit test (|cross − thickness/2| ≤ ring/2) is clipped by a 16-DIP box.
    static float VolumeThickness => Slider.DefaultStyle.ThumbRingDiameter;
    static readonly Slider.SliderOptions VolumeSliderOptions = new()
    {
        ThumbToolTipValueConverter = static value => $"{Math.Clamp((int)MathF.Round(value * 100f), 0, 100)}%",
    };

    public override Element Render()
    {
        // Hooks FIRST (stable call order — rule #7), before any early return.
        var b = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);    // Mutations: the now-playing like reflects + toggles the saved-set
        var go = UseContext(HistoryStore.NavCtx);    // navigate to the playback context / album / artist on click
        var ui = UseContext(ShellUi.Slot);           // right-rail (lyrics) toggle state
        var svc = UseContext(Services.Slot);
        var acts = UseContext(ActionServices.Slot);  // now-playing cluster context menu (Menus.NowPlaying)
        var menuOverlay = UseContext(Overlay.Service);
        var videoAnchor = UseRef<NodeHandle>(default);           // the split video button container → the surface-picker MenuFlyout anchor
        var videoMenu = UseRef<OverlayHandle?>(null);            // the open video surface-picker flyout (null = closed)
        var titleHover = UseSignal(false);           // hover the now-playing text → BOTH lines scroll together (synced); idle = static + edge fade
        // §C0 (large-display-scaling.md): the identity artwork's decode budget, routed through the ambient device
        // scale so the player bar's cover doesn't visibly soften the moment the auto-zoom (or a manual pick) takes
        // the effective scale past 1x — the Equalizer.cs precedent for reading Viewport.Scale.
        float artScale = UseContext(Viewport.Scale);
        var L = _layout.Value;                       // coarse breakpoint signal; does NOT change for every resize pixel
        _ = AppearancePrefs.Epoch.Value;
        bool marqueeDisabled = !(svc?.Settings.Get(WaveeSettings.MarqueeEnabled) ?? true);
        if (DiagEnabled)
            WaveeLog.Instance.Event(WaveeLogLevel.Debug, "ui", "playerbar.render", "Player bar rendered",
                fields:
                [
                    WaveeLogField.Of("render", ++s_renderCount),
                    WaveeLogField.Of("band", (int)L.Tier),
                ]);

        if (b is null)
            // NO fill — the dock is a paint-site omission over the window's base layer, like every other chrome band.
            return new BoxEl { Height = WaveeSize.PlayerBarH };

        // ── state derivation (low-frequency signals only) ──────────────────────────────────────────
        var track = b.CurrentTrack.Value;
        bool liked = track is not null && (lib?.IsSaved(track.Uri) ?? false);   // subscribe → the heart re-skins on a like toggle / track change
        string? err = b.Error.Value;
        bool loading = b.IsLoading.Value;
        bool buffering = b.IsBuffering.Value;
        bool reconnecting = b.RecoveryKind.Value == PlaybackRecoveryKind.Network;
        bool playing = b.IsPlaying.Value;
        bool shuffle = b.IsShuffle.Value;
        var repeat = b.Repeat.Value;
        bool hasVideo = b.CurrentTrackHasVideo.Value;   // async-detected music video (VideoService) for the now-playing track
        var accent = Tok.AccentDefault;
        // ONE TRANSPORT PER **WINDOW** — not per session. The stacked-transport defect is two live scrub rows for the
        // same session IN ONE WINDOW, which is what the fullscreen surface produced (it reserved a band so this bar
        // stayed visible under the video's own transport). So the bar is the DEFAULT owner and the owner for every
        // in-window video placement (docked card, mini player) — those surfaces suppress their own — and it yields
        // only to a full-bleed surface in THIS window, where WaveeShell unmounts this component outright anyway.
        //
        // Detached (PopOut) deliberately does NOT take the transport away from this bar: the pop-out is a separate OS
        // window that carries its own transport, the two were never stacked, and stripping the main window's scrub row
        // and play/pause because a video is playing in another window would be a regression, not a fix. Read through
        // the ONE derived signal (PlaybackBridge.TransportOwnerNow); a local visibility bool here is exactly what let
        // two owners both render.
        bool ownsTransport = b.TransportOwnerNow.Value is TransportOwner.GlobalBar or TransportOwner.PopOut or TransportOwner.Docked;

        PlayerState st =
            err is not null ? PlayerState.Error :
            track is null ? PlayerState.NoTrack :
            loading ? PlayerState.Loading :
            reconnecting ? PlayerState.Reconnecting :
                            PlayerState.Active;
        bool active = st == PlayerState.Active;
        bool canTransport = active || buffering || reconnecting;
        var remoteDevice = active && L.ShowRemoteDeviceLine ? RemoteDevice(b) : null;
        // Primary is live for Active/Buffering and for Error (a retry); dead only for NoTrack/Loading.
        bool primaryEnabled = st != PlayerState.NoTrack && st != PlayerState.Loading;
        // (SeekBar derives its own enabled state reactively from the bridge signals — see SeekBar.Render.)

        // ── responsive breakpoints (coarse layout signal; no raw viewport subscription in this component) ──
        // Identity-first pressure ladder: art + title + artist + heart, Play, SeekBar and Devices survive every tier.
        // Secondary commands fall into More; Play + SeekBar never leave the row.
        bool showExpand = L.ShowExpand;
        bool showDevices = L.ShowDevices;
        bool showQueue = L.ShowQueue;
        bool showVolumeSlider = L.ShowVolumeSlider;
        bool showShuffleRepeat = L.ShowShuffleRepeat;
        bool showLike = L.ShowLikeSlot && active;
        bool showVolumeButton = L.ShowVolumeButton;
        bool showLyrics = L.ShowLyrics;

        // Heart pop (transitions.dev icon swap) on the SAME-track unsaved→saved edge only: a track change flips
        // `liked` but also the uri (no pop); unlike stays a plain swap. Imperative seed on the captured button node —
        // a keyed remount of the focusable Transport button would reset its hover/focus state mid-toggle. (Hooks here
        // sit after the `b is null` early return, matching this component's existing practice — the bridge is
        // provided at the shell root and never goes null in a live session.)
        var likeNode = UseRef<NodeHandle>(default);
        var likePrev = UseRef(((string?)null, false));
        UseLayoutEffect(() =>
        {
            var (pUri, pLiked) = likePrev.Value;
            bool edge = liked && !pLiked && pUri == track?.Uri;
            likePrev.Value = (track?.Uri, liked);
            if (edge && showLike && !likeNode.Value.IsNull && Context.Anim is { } a)
                a.IconSwapIn(likeNode.Value);   // kit recipe — honors Motion.ReducedMotion internally
        }, (track?.Uri ?? "") + (liked ? "|1" : "|0"));
        bool showTimesRemaining = L.ShowTimesRemaining;
        bool showTimesElapsed = L.ShowTimesElapsed;
        bool showPrevNext = L.ShowPrevNext;
        bool showLeft = true;
        bool showArtwork = true;                   // album art never disappears
        bool showSubtitle = L.ShowSubtitle;
        float buttonBox = L.ButtonBox;
        float buttonGlyph = L.ButtonGlyph;
        float primaryBox = L.PrimaryBox;
        float primaryGlyph = L.PrimaryGlyph;
        float leftW = L.LeftW;
        float artSize = L.ArtSize;
        float rowGap = L.RowGap;
        float rowPad = L.RowPad;
        float clusterGap = L.ClusterGap;

        // ── LEFT — now playing ─────────────────────────────────────────────────────────────────────
        // Title/subtitle/colour are bound REACTIVELY off the bridge (Prop thunks), not passed as frozen strings: the
        // Marquee is an autonomous component, so a parent re-render does not push new constructor args into it — the
        // thunk (captured once, reading the stable bridge's signals) is what keeps the inner TextEl re-measuring and
        // repainting as the track changes. Passing the strings directly would freeze them at the first-mount value.
        // Both lines auto-scroll when the title overflows (PauseOnHover) and share ONE hover gate (titleHover) on the
        // meta column — hovering pauses so the user can read / click; CycleMs keeps any sibling lines phase-locked.
        // The edge fade is unaffected: a right-edge "there's more" cue at rest, both edges while scrolling.
        // Now-playing is clickable: art → the playback context; title → the album; artists are per-name links inside a
        // scrolling row. The context route matches QueuePanel's "Playing from" breadcrumb instead of assuming that the
        // track's album is also its playback source (it may be a playlist or Liked Songs).
        // Where each span goes is PlayableLinks' answer, not this file's: a MODULE playable carries artist names with
        // no uri and an empty album, so the old `Uri.Length > 0` gates made every module title/subtitle/art tile inert
        // even after the module had named exactly which page it wanted. The art tile keeps the CONTEXT fallback for a
        // catalogue track (the playback source is not derivable from the track) and takes the module's own page when
        // there is one.
        bool contextNav = ArtRoute(b, track) is { Length: > 0 };
        void NavContext()
        {
            // Resolve at invoke time: the mounted click target survives context changes.
            if (ArtRoute(b, b.CurrentTrack.Peek()) is { } route)
                go?.Invoke(route, PlayableLinks.LabelFor(b.CurrentTrack.Peek(), LinkSlot.Art));
        }

        bool albumNav = PlayableLinks.RouteFor(track, LinkSlot.Title) is { Length: > 0 };
        void NavAlbum()
        {
            // Resolve at invoke time: the mounted click target survives track/metadata changes, so never navigate with
            // the render-time album capture (which can be the pre-hydration empty AlbumRef).
            var now = b.CurrentTrack.Peek();
            if (PlayableLinks.RouteFor(now, LinkSlot.Title) is { } route)
                go?.Invoke(route, PlayableLinks.LabelFor(now, LinkSlot.Title));
        }

        var titleLinkHover = UseSignal(false);
        // titleHot is read INSIDE the Prop.Of thunks below (albumNav && titleLinkHover.Value) so the hover recolor stays
        // live — a render-time bool snapshot would freeze at mount (replacement thunks are ignored after reconcile).
        Element titleEl = marqueeDisabled
            ? new BoxEl
            {
                ClipToBounds = true,
                Children = [new TextEl(Prop.Of(() => NowPlaying(b).Title))
                {
                    Size = 14f, Weight = 700,
                    Color = Prop.Of(() => albumNav && titleLinkHover.Value ? Tok.AccentTextPrimary : NowPlaying(b).Color),
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                }],
            }
            : Marquee.Of(Prop.Of(() => NowPlaying(b).Title),
                new Marquee.Style
                {
                    FontSize = 14f, Weight = 700,
                    Foreground = Prop.Of(() => albumNav && titleLinkHover.Value ? Tok.AccentTextPrimary : NowPlaying(b).Color),
                    Speed = 18f, CycleMs = MarqueeCycleMs, EndPauseMs = MarqueeEndPauseMs,
                    Mode = Marquee.ScrollMode.PingPong, Trigger = Marquee.TriggerMode.PauseOnHover,
                },
                scrollWhen: titleHover);
        if (albumNav)   // make the marquee VIEWPORT itself the stable click target; no wrapper/input-boundary ambiguity
            titleEl = ((BoxEl)titleEl) with
            {
                Cursor = CursorId.Hand, OnClick = NavAlbum,
                OnHoverMove = _ =>
                {
                    if (!titleLinkHover.Peek()) titleLinkHover.Value = true;
                    if (!titleHover.Peek()) titleHover.Value = true;
                },
                OnPointerExit = () => { if (titleLinkHover.Peek()) titleLinkHover.Value = false; },
                Role = AutomationRole.Hyperlink, Focusable = true,
            };

        var metaKids = new List<Element>(remoteDevice is null ? 2 : 3);
        if (remoteDevice is not null)
            metaKids.Add(new BoxEl
            {
                Key = "remote-device-line", Animate = ItemMotion,
                Children = [Embed.Comp(() => new RemoteDeviceLine(b))],
            });
        metaKids.Add(titleEl);
        if (showSubtitle && track is not null && err is null)
        {
            metaKids.Add(marqueeDisabled
                ? new BoxEl { ClipToBounds = true, MinWidth = 0f, Children = [Embed.Comp(() => new NowPlayingArtistLinks(compact: true)) with { Key = "npartists:c" }] }
                : Marquee.Content(() => new NowPlayingArtistLinks(),
                    new Marquee.Style
                    {
                        Speed = 18f, CycleMs = MarqueeCycleMs, EndPauseMs = MarqueeEndPauseMs,
                        Mode = Marquee.ScrollMode.PingPong, Trigger = Marquee.TriggerMode.PauseOnHover,
                    },
                    scrollWhen: titleHover));
        }

        var metaCol = new BoxEl
        {
            Key = "meta", Animate = MoveMotion,
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 1f,
            Gap = 2f, Justify = FlexJustify.Center, ClipToBounds = true,
            // The shared hover target: the title marquee reads titleHover, so hovering anywhere on the meta column pauses it.
            OnHoverMove = _ => { if (!titleHover.Peek()) titleHover.Value = true; },
            OnPointerExit = () => { if (titleHover.Peek()) titleHover.Value = false; },
            Children = metaKids.ToArray(),
        };

        var leftKids = new List<Element>(3);
        if (showArtwork)
            leftKids.Add(new BoxEl
            {
                Key = "art", Width = artSize, Height = artSize, Shrink = 0f, Animate = ItemMotion,
                Cursor = contextNav ? CursorId.Hand : (CursorId?)null,
                OnClick = contextNav ? NavContext : null,   // album art → the playback context
                Role = contextNav ? AutomationRole.Hyperlink : AutomationRole.None,
                Focusable = contextNav,
                Children = [Surfaces.Artwork(track?.Image, SeedOf(track), artSize, artSize, 6f, scale: artScale)]
            });
        leftKids.Add(metaCol);
        if (showLike)
            // The heart is a transport-grade target like every other glyph on this row — it used to be capped at 30/15,
            // which put the ONE affordance that survives to the 300-DIP floor below the WinUI icon-button minimum.
            leftKids.Add(Transport(liked ? Icons.HeartFill : Icons.Heart, () => { if (track is { } lt) lib?.ToggleSaved(lt.Uri, lt.Title); }, true, liked, accent, buttonBox, buttonGlyph,
                    onRealized: h => likeNode.Value = h)
                // The cluster is a drag handle (below); the heart is its own affordance, so a press on it must not
                // arm the drag — the first few px of a like-press would otherwise lift the track.
                with { Key = "like", Shrink = 0f, Animate = ItemMotion, BlocksDragArm = true });

        var left = new BoxEl
        {
            Key = "left", Width = leftW, Shrink = 0f, MinWidth = 0f, Animate = MoveMotion,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = L.LeftGap, ClipToBounds = true,
            // The now-playing cluster drags the CURRENT track (drop it on a playlist, the sidebar, the queue). The
            // payload factory Peeks at promotion time — the mounted cluster outlives every track change, so a
            // render-time capture would drag whatever was playing when the bar was built.
            Draggable = track is null
                ? null
                : Drag.Source(WaveeDragKinds.Resource,
                    () => b.CurrentTrack.Peek() is { } dragged ? WaveeResourceDragPayload.ForTrack(dragged) : null),
            Children = leftKids.ToArray(),
        };
        // Right-click / Menu key / long-press on the now-playing cluster: the track menu (Host = null → no Remove
        // rows). The factory Peeks the CURRENT track at open — never the render-time capture.
        if (acts is { } nowActs && menuOverlay is { } menuSvc)
            left = left.WithContextMenu(menuSvc, () =>
                b.CurrentTrack.Peek() is { } nowTrack ? Menus.NowPlaying(nowActs, nowTrack) : (ContextMenuModel?)null);

        // ── CENTRE — transport group + seek (the SeekBar Grows to fill the remaining center width) ───
        // Empty while a bar-less video surface owns the transport — see `ownsTransport` above.
        var transport = new List<Element>(3);
        if (ownsTransport && showPrevNext)
            transport.Add(Transport(Icons.Previous, () => { _ = b.Player.PreviousAsync(); }, canTransport, false, accent, buttonBox, buttonGlyph)
                with { Key = "prev", Animate = ItemMotion });
        if (ownsTransport)
            transport.Add(Primary(
                st == PlayerState.Error ? Icons.Play : playing ? Icons.Pause : Icons.Play,
                () => PrimaryClick(b, st), primaryEnabled, accent, primaryBox, primaryGlyph)
                with { Key = "primary", Animate = MoveMotion });
        if (ownsTransport && showPrevNext)
            transport.Add(Transport(Icons.Next, () => { _ = b.Player.NextAsync(); }, canTransport, false, accent, buttonBox, buttonGlyph)
                with { Key = "next", Animate = ItemMotion });

        var transportGroup = new BoxEl
        {
            Key = "transport", Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Gap = 0f, Animate = MoveMotion, Children = transport.ToArray(),
        };

        // Stable Keys so keyed matching preserves SeekBar identity across the 760-DIP time-label breakpoint (all three children are
        // ComponentEl/ElementTypeId 3 — without keys, dropping the elapsed label shifts SeekBar to index 0 where it is
        // matched against the old TimeText node, a ComponentType mismatch that REMOUNTS SeekBar, losing its scrub state /
        // interpolation anchor / cached width on resize-during-playback).
        var seekKids = new List<Element>(3);
        if (ownsTransport && showTimesElapsed)
            seekKids.Add(new BoxEl { Key = "elapsed", Animate = ItemMotion, Children = [Embed.Comp(() => new TimeText(b, remaining: false))] });
        if (ownsTransport)
            seekKids.Add(new BoxEl { Key = "seek", Grow = 1f, Shrink = 1f, MinWidth = 0f, Animate = MoveMotion, Children = [Embed.Comp(() => new SeekBar(b))] });
        if (ownsTransport && showTimesRemaining)
            seekKids.Add(new BoxEl { Key = "remaining", Animate = ItemMotion, Children = [Embed.Comp(() => new TimeText(b, remaining: true))] });

        var seekRow = new BoxEl
        {
            Key = "seek-row", Direction = 0, Grow = 1f, Shrink = 1f, AlignItems = FlexAlign.Center,
            Gap = L.SeekGap, Animate = MoveMotion, Children = seekKids.ToArray(),
        };

        // Drop on the transport = PLAY NEXT (locked decision): a front-insert into the user queue, never an immediate
        // playback change — a drag must not take the current track away mid-listen. The caption says so explicitly,
        // because "dropped it on the player" could equally read as "play this now".
        var centre = new BoxEl
        {
            Key = "centre", Grow = 1f, Shrink = 1f, MinWidth = 0f, Animate = MoveMotion,
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Gap = clusterGap,
            DropTarget = Drop.Target<WaveeResourceDragPayload>(WaveeDragKinds.Resource,
                accepts: static p => p.CanCopyTracks,
                onDrop: (p, _) => PlayNextDrop(b, acts, p),
                caption: static _ => Loc.Get(Strings.Drag.PlayNext),
                refusalCaption: static p => Loc.Get(p.Kind == WaveeResourceKind.Artist
                    // Locked decision: an artist has no single obvious track set — refuse rather than guess.
                    ? Strings.Drag.CantAddArtist
                    : Strings.Drag.NothingToAdd),
                visualPolicy: DropTargetVisualPolicy.Spotlight),
            Children = [transportGroup, seekRow],
        };

        // ── RIGHT — shuffle/repeat · volume · queue/devices/expand ───────────────────────────────────
        var overflowCommands = new List<AppBarCommand>(8);
        if (ownsTransport && !showPrevNext)
        {
            overflowCommands.Add(new AppBarCommand(Icons.Previous, Loc.Get(Strings.Player.Previous), () => { _ = b.Player.PreviousAsync(); }, Enabled: canTransport));
            overflowCommands.Add(new AppBarCommand(Icons.Next, Loc.Get(Strings.Player.Next), () => { _ = b.Player.NextAsync(); }, Enabled: canTransport));
        }
        if (!showShuffleRepeat)
        {
            overflowCommands.Add(new AppBarCommand(Icons.Shuffle, Loc.Get(Strings.Player.Shuffle), () => ToggleShuffle(b), AppBarCommandKind.ToggleButton, shuffle, canTransport));
            overflowCommands.Add(new AppBarCommand(repeat == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll, Loc.Get(Strings.Player.Repeat), () => CycleRepeat(b), AppBarCommandKind.ToggleButton, repeat != RepeatMode.Off, canTransport));
        }
        if (!showLike && active)
            overflowCommands.Add(new AppBarCommand(liked ? Icons.HeartFill : Icons.Heart, Loc.Get(Strings.Player.Like), () => { if (track is { } lt) lib?.ToggleSaved(lt.Uri, lt.Title); }, AppBarCommandKind.ToggleButton, liked, true));
        bool volumeInOverflow = !showVolumeButton && active;
        if (!showLyrics && ui is not null && active)
            overflowCommands.Add(new AppBarCommand(
                new IconRef { Glyph = WaveeIcons.Lyrics, Font = WaveeIcons.Font },
                Loc.Get(Strings.Player.Lyrics),
                () => ui.Toggle(RailMode.Lyrics),
                AppBarCommandKind.ToggleButton,
                ui.RailOpen.Value && ui.Mode.Value == RailMode.Lyrics,
                true));
        // In the small-window overflow, Queue / Now Playing open their right-rail panels (the rail floats over the
        // content when it doesn't fit inline).
        if (!showQueue)
            overflowCommands.Add(new AppBarCommand(Icons.Queue, Loc.Get(Strings.Player.Queue), () => ui?.Toggle(RailMode.Queue), Enabled: ui is not null));
        // (Devices button is always visible now — no overflow fallback.)
        if (!showExpand)
            overflowCommands.Add(new AppBarCommand(Icons.ChevronUp, Loc.Get(Strings.Player.NowPlaying), () => ui?.Toggle(RailMode.Details), Enabled: ui is not null));

        var rightKids = new List<Element>(8);
        if (showShuffleRepeat)
        {
            rightKids.Add(Transport(Icons.Shuffle, () => ToggleShuffle(b), canTransport, shuffle, accent, buttonBox, buttonGlyph)
                with { Key = "shuffle", Animate = ItemMotion });
            rightKids.Add(Transport(repeat == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll,
                () => CycleRepeat(b), canTransport, repeat != RepeatMode.Off, accent, buttonBox, buttonGlyph)
                with { Key = "repeat", Animate = ItemMotion });
        }
        // Volume progressively degrades slider → popup glyph → live overflow item. The open-time overflow item reads
        // the bridge with Peek, so hot volume changes never subscribe this parent bar.
        if (showVolumeButton)
            rightKids.Add(new BoxEl
            {
                Key = "volume", Animate = ItemMotion,
                Children =
                [
                    Embed.Comp(() => new VolumeButton(b, !showVolumeSlider, buttonBox, buttonGlyph))
                        with { Key = (showVolumeSlider ? "volume-inline-" : "volume-popup-") + buttonBox + "-" + buttonGlyph }
                ]
            });
        if (showVolumeSlider)
            rightKids.Add(new BoxEl
            {
                Key = "volume-slider", Animate = ItemMotion,   // Slider.Create returns a component element (no Animate lane); wrap it like the other Embed.Comp items
                Children = [Slider.Create(b.Volume, v => { _ = b.Player.SetVolumeAsync(v); }, options: VolumeSliderOptions,
                    length: 96f, thickness: VolumeThickness)],
            });
        if (showLyrics && ui is not null && active)
            rightKids.Add(Transport(WaveeIcons.Lyrics,
                () => ui.Toggle(RailMode.Lyrics),
                true,
                ui.RailOpen.Value && ui.Mode.Value == RailMode.Lyrics, accent, buttonBox, buttonGlyph,
                font: WaveeIcons.Font)
                with { Key = "lyrics", Animate = ItemMotion });
        // Video — a SPLIT button, shown only when the now-playing track has a music video (async-detected).
        //   PRIMARY (movie glyph) — a SYMMETRIC toggle: off → show the video at the user's preferred placement (which
        //     starts as the in-window mini player, the lowest-commitment surface: no new OS window, dismissible, stays
        //     with the app); on → off, from ANY placement. It lights whenever video is live, wherever it is.
        //   CHEVRON — opens the placement picker. It NEVER lights: it is a disclosure, not a state, and lighting it made
        //     the pair read as two independent toggles that could disagree.
        //   RIGHT-CLICK / Menu key on either part opens the same picker (WinUI ContextRequested).
        // Everything here is a pure INTENT on the bridge; the placement model decides the rest, and the surfaces render
        // from its one resolved answer. The split button needs ~1.5 slots, so it rides the same crowding threshold as
        // Queue and degrades into the overflow menu below it (see the else-branch) instead of vanishing.
        //
        // The full four-rung ladder, COMMITMENT ORDER, radio-checked against the one resolved placement, disabled-
        // with-a-reason rather than hidden — a disabled MenuFlyoutItem cannot carry a tooltip (IsEnabled = false
        // removes hit-testing), so the reason goes in the accelerator column, the same idiom as the device-picker
        // rows below (:740-747) and WaveeActionDescriptor.ToMenuItem. The ROWS themselves are the single shared
        // VideoPlacementMenu.Items definition (Features/Video/VideoPlacementMenu.cs) — this split button's own
        // chevron menu below, the narrow-layout overflow's cascading Flyout (PlayerMoreMenu), and the video's own
        // transport More menu all call it, so the three pickers cannot drift apart.

        if (active && hasVideo && showQueue)
        {
            // DERIVED state — the ONE resolved placement. The button faces reflect what is actually mounted, so they
            // cannot get stuck or disagree with each other.
            bool videoActive = b.VideoActive();
            var placement = b.VideoPlacementNow();

            // The chevron opens the placement picker UPWARD out of the split button (bottom bar) via the overlay service —
            // the same path DropDownButton/DevicesButton use (light-dismiss + focus-trap + the engine clip-reveal). The
            // items are RADIO-checked against the resolved placement, so the menu shows where the video IS, and choosing
            // the checked one again is a harmless no-op rather than a hidden toggle.
            void OpenVideoMenu()
            {
                if (menuOverlay is not { } videoSvc) return;
                if (videoMenu.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = VideoPlacementMenu.Items(b, svc?.Settings, includeFullscreen: true);
                videoMenu.Value = videoSvc.Open(
                    () => videoAnchor.Value,
                    () => MenuFlyout.Create(items, () => videoMenu.Value?.Close()),
                    FlyoutPlacement.TopEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                videoMenu.Value.ClosedAction = () => videoMenu.Value = null;
            }

            // The two parts share ONE tight anchor container (no gap); the chevron portion is narrower than the primary.
            // Right-click / Menu key anywhere on the pair raises the same picker as the chevron.
            rightKids.Add(new BoxEl
            {
                Key = "video", Direction = 0, AlignItems = FlexAlign.Center, Animate = ItemMotion,
                OnRealized = h => videoAnchor.Value = h,
                OnContextRequested = _ => OpenVideoMenu(),
                Children =
                [
                    ToolTip.Wrap(
                        // Lit whenever video is live, in ANY placement — the primary reflects "am I watching?", not
                        // "am I watching in one specific surface?" (which is what made it read as stuck).
                        Transport(Icons.Movie, b.ToggleVideo, true, videoActive, accent, buttonBox, buttonGlyph),
                        Loc.Get(videoActive ? Strings.Player.SwitchToAudio : Strings.Player.SwitchToVideo)),
                    ToolTip.Wrap(
                        // active: false — a disclosure never lights. NARROW but FULL-HEIGHT (WinUI SplitButton's
                        // secondary half is Width=SecondaryButtonSize, Height=ControlHeight): a shorter box would top-
                        // align against the primary, because ToolTip.Wrap's wrapper sets AlignSelf=Start and a child's
                        // AlignSelf beats this row's AlignItems=Center — which is what put the chevron ~8px high.
                        Transport(Icons.ChevronDownSmall, OpenVideoMenu, true, false, accent,
                            SplitChevronW, SplitChevronGlyph, boxHeight: buttonBox),
                        Loc.Get(Strings.Player.VideoOptions)),
                ],
            });
        }
        // Narrow windows drop the split button entirely; without this the whole feature silently disappears instead of
        // degrading into the overflow menu like every other transport command.
        else if (active && hasVideo)
            overflowCommands.Add(new AppBarCommand(Icons.Movie,
                Loc.Get(b.VideoActive() ? Strings.Player.SwitchToAudio : Strings.Player.SwitchToVideo),
                b.ToggleVideo, AppBarCommandKind.ToggleButton, b.VideoActive(), true)
                { Flyout = VideoPlacementMenu.Items(b, svc?.Settings, includeFullscreen: true) });
        if (showQueue)
            rightKids.Add(Transport(Icons.Queue, () => ui?.Toggle(RailMode.Queue), ui is not null,
                ui?.RailOpen.Value == true && ui.Mode.Value == RailMode.Queue, accent, buttonBox, buttonGlyph)
                with { Key = "queue", Animate = ItemMotion });
        if (showDevices)
            rightKids.Add(Embed.Comp(() => new DevicesButton(b, buttonBox, buttonGlyph, DevicePickerScope.Bar)) with { Key = "devices" });
        if (showExpand)
            rightKids.Add(Transport(Icons.ChevronUp, () => ui?.Toggle(RailMode.Details), ui is not null,
                ui?.RailOpen.Value == true && ui.Mode.Value == RailMode.Details, accent, buttonBox, buttonGlyph)
                with { Key = "expand", Animate = ItemMotion });
        if (overflowCommands.Count > 0 || volumeInOverflow)
            rightKids.Add(MoreButton(overflowCommands, buttonBox, buttonGlyph, b, volumeInOverflow));

        var right = new BoxEl
        {
            Key = "right", Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            Gap = L.RightGap, Animate = MoveMotion,
            Children = rightKids.ToArray(),
        };

        // ── assemble: top activity edge + the single centered row ───────────────────────────────────
        // The dock's top seam, and the ONLY treatment on this edge. The dock body is a paint-site OMISSION over the
        // window's base layer (live Mica; no plate), so the seam is the only thing drawn here — the content region above
        // deliberately carries no bottom stroke, and the dock itself no longer casts a shadow up into it (stock is one
        // hairline per seam, not a hairline plus a ring plus an elevation). Dark keeps the stock divider (#15FFFFFF):
        // white-alpha, so the base still reads through it, where a black card stroke would be a dark scar. Light uses the black@6% ALPHA literal
        // rather than StrokeCardDefault: that token is #0F000000 only in the stock palette — every seeded light preset
        // derives it as an OPAQUE gray (warm #DCDAD4; slate/accent Darken(page, 0.08)), and an opaque gray line across
        // an otherwise unpainted band is its own small disjoint slab. Same reasoning as
        // TabStrip's baseline hairline and separators.
        Element topEdge = (loading || buffering || reconnecting)
            ? ProgressBar.Indeterminate(L.TopEdgeWidth)
            : new BoxEl
            {
                Height = 1f,
                Fill = Prop.Of(() => Theme.Dark ? Tok.StrokeDividerDefault : ColorF.FromRgba(0, 0, 0, 0x0F)),
            };

        var rowKids = new List<Element>(3);
        if (showLeft) rowKids.Add(left);
        rowKids.Add(centre);
        if (rightKids.Count > 0) rowKids.Add(right);

        var row = new BoxEl
        {
            Key = "player-row", Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = rowGap,
            Padding = new Edges4(rowPad, 0f, rowPad, 0f), Animate = MoveMotion,
            Children = rowKids.ToArray(),
        };

        return new BoxEl
        {
            // NO fill: the dock is a paint-site OMISSION over the window's base layer (the merged chrome row, the nav
            // pane and this dock all share it), so the chrome reads as one continuous material.
            // NO SHADOW either. Elevation.DockTop cast a third treatment onto a seam that already had the content
            // region's ring above it and this dock's own 1px topEdge — stock Win11 puts exactly ONE line there.
            Direction = 1, Height = WaveeSize.PlayerBarH, ClipToBounds = true,
            // LAYOUT FIREWALL. The dock is a fixed-height slot whose width cross-stretches from the shell column, so it
            // can never be content-sized by a descendant — the boundary contract. Without it every re-render in here
            // (the track title, the position text, a tooltip) marks a layout-dirty node that escapes to a full-tree
            // relayout from the scene root. Placed on the box that ALREADY clips, so Boundary() contributes only
            // IsolateLayout and cannot change a pixel.
            IsolateLayout = true,
            Children = [topEdge, row],
        };
    }

    /// <summary>The bar's one deposit: resolve the payload's tracks (cold — never during the drag) and FRONT-insert
    /// them into the user queue. Capped by the shared queue batch limit, and a truncation is said out loud rather than
    /// silently applied.</summary>
    static void PlayNextDrop(PlaybackBridge b, ActionServices? acts, WaveeResourceDragPayload payload)
    {
        _ = Run();

        async Task Run()
        {
            IReadOnlyList<Track> tracks;
            try { tracks = await payload.ResolveTracksAsync().ConfigureAwait(false); }
            catch { return; }
            int n = DetailQueueActions.PlayNext(b.Player, tracks);
            if (n <= 0) return;
            int total = tracks.Count;
            acts?.Post?.Invoke(() => Toast.Show(
                n < total
                    ? Strings.Detail.AddedFirstToQueue(Strings.Detail.SongCount(n))
                    : Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)),
                new ToastOptions { Severity = InfoBarSeverity.Success }));
        }
    }

    // ── intents (optimistic: write the signal first so the UI is instant, then the bridge reconciles) ──
    internal static void ToggleShuffle(PlaybackBridge b)
    {
        bool s = b.IsShuffle.Peek(); b.IsShuffle.Value = !s; _ = b.Player.SetShuffleAsync(!s);
    }

    internal static void CycleRepeat(PlaybackBridge b)
    {
        var r = b.Repeat.Peek();
        var next = r == RepeatMode.Off ? RepeatMode.Context : r == RepeatMode.Context ? RepeatMode.Track : RepeatMode.Off;
        b.Repeat.Value = next; _ = b.Player.SetRepeatAsync(next);
    }

    static void PrimaryClick(PlaybackBridge b, PlayerState st)
    {
        if (st == PlayerState.Error) { b.InvokePlaybackErrorAction(); return; }
        if (st is PlayerState.NoTrack or PlayerState.Loading) return;
        TogglePlayPause(b);
    }

    /// <summary>THE play/pause intent — optimistic (write the signal first so the UI is instant, then let the bridge
    /// reconcile), exactly like <see cref="ToggleShuffle"/> and <see cref="CycleRepeat"/>. Shared with the immersive
    /// stage's filled primary so the two surfaces cannot drift into two different play buttons.</summary>
    internal static void TogglePlayPause(PlaybackBridge b)
    {
        bool p = b.IsPlaying.Peek();
        b.IsPlaying.Value = !p;
        if (p) _ = b.Player.PauseAsync(); else _ = b.Player.ResumeAsync();
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────
    // The now-playing title + its colour, derived LIVE from the bridge (same state machine as PlayerState). Called from
    // a Prop thunk so the Marquee's bound TextEl re-measures/repaints when the track or play-state changes — see metaKids.
    static (string Title, ColorF Color) NowPlaying(PlaybackBridge b)
    {
        var track = b.CurrentTrack.Value;
        string? err = b.Error.Value;
        bool loading = b.IsLoading.Value;
        bool reconnecting = b.RecoveryKind.Value == PlaybackRecoveryKind.Network;
        PlayerState st = err is not null ? PlayerState.Error
                       : track is null ? PlayerState.NoTrack
                       : loading ? PlayerState.Loading
                       : reconnecting ? PlayerState.Reconnecting
                       : PlayerState.Active;
        // A placeholder track seeds Title=Uri before metadata resolves; never surface the raw URI to the user.
        string title = track is { } t && !string.IsNullOrEmpty(t.Title) && t.Title != t.Uri ? t.Title : "";
        return st switch
        {
            PlayerState.NoTrack => (Loc.Get(Strings.Player.NothingPlaying), Tok.TextSecondary),
            PlayerState.Loading => (title.Length > 0 ? title : Loc.Get(Strings.Player.Loading), Tok.TextPrimary),
            PlayerState.Reconnecting => (Loc.Get(Strings.Player.Reconnecting), Tok.TextSecondary),
            PlayerState.Error   => (err ?? Loc.Get(Strings.Player.CannotPlay), Critical),
            _                   => (title, Tok.TextPrimary),
        };
    }

    static int SeedOf(Track? t) => t is null ? 11 : Math.Abs((t.Uri ?? t.Id).Length * 7 + t.Title.Length);

    internal static PlaybackDevice? RemoteDevice(PlaybackBridge b)
    {
        var devices = b.Devices.Value;
        string? activeId = b.ActiveDeviceId.Value;
        PlaybackDevice? active = null;

        if (!string.IsNullOrEmpty(activeId))
            active = devices.FirstOrDefault(d => d.Id == activeId);
        active ??= devices.FirstOrDefault(d => d.IsActive);

        return active is { Kind: not DeviceKind.ThisDevice } ? active : null;
    }

    // Two-section device picker (plan §C2): "This computer" (System default + local endpoints) + "Spotify Connect" (the
    // roster, ThisDevice filtered — this PC is section 1). The pure composition lives in DevicePickerModel (unit-tested);
    // this maps its rows to MenuFlyoutItems and wires the intents. Disabled Command rows stand in as section headers
    // (MenuFlyout has no header kind — risk 9.1; WinUI itself has no MenuFlyoutHeader).
    internal static List<MenuFlyoutItem> DevicePickerItems(PlaybackBridge b)
    {
        var connect = b.Devices.Value;
        string? activeId = b.ActiveDeviceId.Value;
        var lo = b.LocalOutputs;
        IReadOnlyList<LocalAudioDevice> local = lo?.Devices.Value ?? Array.Empty<LocalAudioDevice>();
        string? selectedLocal = lo?.SelectedOutputId.Value;
        bool supported = b.LocalPlaybackSupported.Value;
        bool weAreActiveOutput = RemoteDevice(b) is null;   // no remote Connect device active ⇒ we are the output

        var rows = DevicePickerModel.Build(local, selectedLocal, supported, weAreActiveOutput, connect, activeId,
            Loc.Get(Strings.Player.ThisComputer), Loc.Get(Strings.Player.SystemDefault), Loc.Get(Strings.Player.SpotifyConnect),
            Loc.Get(Strings.Player.Unavailable), Loc.Get(Strings.Player.NoDevices), Loc.Get(Strings.Player.NoDevicesHint));

        var items = new List<MenuFlyoutItem>(rows.Count);
        foreach (var r in rows) items.Add(MapRow(b, r));
        return items;
    }

    static MenuFlyoutItem MapRow(PlaybackBridge b, DevicePickerRow r) => r.Kind switch
    {
        DevicePickerRowKind.Separator => MenuFlyoutItem.Separator,
        DevicePickerRowKind.Header => new MenuFlyoutItem(r.Label, default, false, () => { }),
        DevicePickerRowKind.Empty => new MenuFlyoutItem(r.Label, default, false, () => { }),
        DevicePickerRowKind.LocalDefault => MenuFlyoutItem.RadioItem(r.Label, r.IsChecked,
            r.Enabled ? () => { _ = b.LocalOutputs?.SelectAsync(null); } : null, Icons.ThisPc, enabled: r.Enabled)
            with { AcceleratorText = r.Accelerator },
        DevicePickerRowKind.LocalDevice => MenuFlyoutItem.RadioItem(r.Label, r.IsChecked,
            r.Enabled ? () => { var id = r.DeviceId; _ = b.LocalOutputs?.SelectAsync(id); } : null, LocalGlyph(r.LocalKind), enabled: r.Enabled)
            with { AcceleratorText = r.Accelerator },
        DevicePickerRowKind.ConnectDevice => MenuFlyoutItem.RadioItem(r.Label, r.IsChecked,
            () => { var id = r.DeviceId; if (id is not null) _ = b.DeviceControl.TransferAsync(id); }, DeviceGlyph(r.ConnectKind)),
        _ => new MenuFlyoutItem(r.Label, default, false, () => { }),
    };

    // Segoe Fluent glyph for a local (this-computer) output form factor.
    static string LocalGlyph(LocalAudioDeviceKind k) => k switch
    {
        LocalAudioDeviceKind.Speakers => Icons.Speakers,
        LocalAudioDeviceKind.Headphones or LocalAudioDeviceKind.Headset => Icons.Headphones,
        LocalAudioDeviceKind.Hdmi => Icons.TvMonitor,
        _ => Icons.ThisPc,
    };

    // Segoe Fluent glyph per Connect device kind (app-local Mdl set; the engine Icons.* set doesn't carry these).
    static string DeviceGlyph(DeviceKind k) => k switch
    {
        DeviceKind.Phone => Icons.CellPhone,
        DeviceKind.Speaker => Icons.Speakers,
        DeviceKind.Tv => Icons.TvMonitor,
        _ => Icons.ThisPc,   // ThisDevice / Computer
    };

    /// <summary>The transport clock. The digits themselves live in the engine-free <see cref="TimeFormat.Clock"/> under
    /// <c>Backend/</c> so they are source-included by <c>Wavee.Tests</c> and can be pinned — this file is engine-bound
    /// and never is. Formatting hours matters here because a live broadcast's elapsed-since-tune-in has no duration to
    /// bound it and will sit past 59:59 on any stream left running.</summary>
    internal static string Fmt(long ms) => TimeFormat.Clock(ms);

    /// <summary>Where the ART TILE goes. A module playable opens its OWN page (the module named it); everything else
    /// opens the playback CONTEXT, which the track cannot know — QueuePanel's "Playing from" breadcrumb, not the
    /// track's album, because the source may be a playlist or Liked Songs.</summary>
    static string? ArtRoute(PlaybackBridge b, Track? track)
        => PlayableLinks.RouteFor(track, LinkSlot.Art) ?? RichText.RouteForUri(b.CurrentContext.Peek());

    /// <summary>The subtitle's per-name links. A CATALOGUE track links each credited artist to their page; a MODULE
    /// playable has one publisher (a channel, a station) rather than a credit list, so the whole line is ONE link to
    /// the entity the module named — and stays inert, not styled-but-dead, when it named none.</summary>
    static void AddArtistLinks(List<Element> into, IReadOnlyList<ArtistRef> artists, Action<string, string?>? go,
                               Track? track = null)
    {
        string? moduleRoute = PlayableLinks.RouteFor(track, LinkSlot.Artist);
        bool isModule = PlayableLinks.IsModule(track);
        for (int i = 0; i < artists.Count; i++)
        {
            var a = artists[i];
            if (a.Name.Length == 0) continue;
            if (into.Count > 0) into.Add(new TextEl(", ") { Size = 12f, Color = Tok.TextSecondary });
            string? route = isModule ? moduleRoute : a.Uri.Length > 0 ? "artist:" + a.Uri : null;
            bool enabled = route is { Length: > 0 };
            into.Add(NavSpan(a.Name, () => { if (route is { Length: > 0 } r) go?.Invoke(r, a.Name); }, enabled)
                with { Key = "artist:" + (a.Uri.Length > 0 ? a.Uri : a.Id + ":" + a.Name) });
        }
    }

    // Reactive artist row for the player-bar marquee — reads bridge/nav from context so track changes re-skin without
    // remount. Compact mode (marquee disabled): a hard-clipped full credit list is bad UX (names vanish mid-word), so
    // show the FIRST artist (ellipsizing) + the shared "+N" overflow chip that opens a flyout of ALL credited artists.
    sealed class NowPlayingArtistLinks : Component
    {
        readonly bool _compact;
        public NowPlayingArtistLinks(bool compact = false) { _compact = compact; }

        public override Element Render()
        {
            var b = UseContext(PlaybackBridge.Slot);
            var go = UseContext(HistoryStore.NavCtx);
            var track = b?.CurrentTrack.Value;
            var artists = track?.Artists;
            if (artists is not { Count: > 0 })
                return new BoxEl { Direction = 0 };

            if (_compact && artists.Count > 1)
            {
                var a = artists[0];
                // Same rule as AddArtistLinks: a module playable's subtitle is ONE destination, the publisher.
                string? route = PlayableLinks.RouteFor(track, LinkSlot.Artist)
                    ?? (PlayableLinks.IsModule(track) ? null : a.Uri.Length > 0 ? "artist:" + a.Uri : null);
                bool enabled = route is { Length: > 0 };
                var all = artists;
                return new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Gap = 4f,
                    Children =
                    [
                        NavSpan(a.Name, () => { if (route is { Length: > 0 } r) go?.Invoke(r, a.Name); }, enabled, trim: true)
                            with { Key = "artist:" + (a.Uri.Length > 0 ? a.Uri : a.Id + ":" + a.Name) },
                        Embed.Comp(() => new ArtistMoreButton(all, (u, n) => go?.Invoke(u, n)))
                            with { Key = "npmore:" + (a.Uri.Length > 0 ? a.Uri : a.Name) + ":" + all.Count },
                    ],
                };
            }

            var kids = new List<Element>(artists.Count * 2);
            AddArtistLinks(kids, artists, go, track);
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children = kids.ToArray(),
            };
        }
    }

    // A clickable now-playing meta link (artist / album). It drives its own foreground because TextEl.HoverColor follows
    // the engine's ancestor hover path too, which makes the album look hovered when the pointer is over the title line.
    static Element NavSpan(string text, Action onClick, bool enabled, bool trim = false)
        => Embed.Comp(() => new NowPlayingMetaLink(text, onClick, enabled, trim));

    sealed class NowPlayingMetaLink : Component
    {
        readonly string _text;
        readonly Action _onClick;
        readonly bool _enabled;
        readonly bool _trim;   // ellipsize under width pressure (the compact artists line) instead of hard-clipping

        public NowPlayingMetaLink(string text, Action onClick, bool enabled, bool trim = false)
        {
            _text = text;
            _onClick = onClick;
            _enabled = enabled;
            _trim = trim;
        }

        public override Element Render()
        {
            var hover = UseSignal(false);
            return new BoxEl
            {
                Cursor = _enabled ? CursorId.Hand : (CursorId?)null,
                OnClick = _enabled ? _onClick : null,
                OnHoverMove = _enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null,
                OnPointerExit = _enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
                ClipToBounds = true,
                MinWidth = _trim ? 0f : float.NaN,
                Shrink = _trim ? 1f : 1f,
                Role = _enabled ? AutomationRole.Hyperlink : AutomationRole.Text,
                Children =
                [
                    _trim
                        ? new TextEl(_text) { Size = 12f, Color = hover.Value ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                        : new TextEl(_text) { Size = 12f, Color = hover.Value ? Tok.TextPrimary : Tok.TextSecondary },
                ],
            };
        }
    }

    /// <summary>A transport TOGGLE/command glyph button.
    ///
    /// <para>THE ON-STATE IS THE STOCK TOGGLE GRAMMAR: an accent glyph over a <see cref="Tok.FillSubtleSecondary"/>
    /// plate — WinUI's <c>AppBarToggleButton</c> checked visual (a checked AppBar toggle fills; it does not sprout an
    /// ornament). This replaced a 3 × 3 accent DOT parked under the glyph, which was the whole "is shuffle on?" answer
    /// in ~9 device-independent pixels: at 100% scaling it is smaller than the period at the end of this sentence, it
    /// sat on the button's bottom edge where the glyph's own descenders live, and it carried no hit-target, focus or
    /// high-contrast meaning. The file previously argued that a filled background is "reserved for the hover/press
    /// interaction axis" — but that reservation is what forced the ornament, and WinUI itself does not honour it: a
    /// ToggleButton's CHECKED fill and its POINTER-OVER fill are different rungs of the same subtle ramp, which is
    /// exactly how a user tells a latched control from a hovered one. So the checked rung is claimed here, and the
    /// hover/press axis keeps the two rungs above it (Tertiary while checked) plus the glyph ramp and the scale cue.</para>
    ///
    /// <para>Rest (unchecked) is still a completely unpainted box: the dock is a paint-site omission over the window's
    /// base layer, and a row of resting plates would re-plate it.</para></summary>
    /// <param name="boxHeight">Override the button's HEIGHT (default: square, <paramref name="box"/>). Only the split
    /// video button uses it: a narrow disclosure half must still be as TALL as the primary it sits beside, exactly like
    /// <c>SplitButton</c>'s secondary half (Width = SecondaryButtonSize, Height = ControlHeight). Equal heights are what
    /// keeps the two glyphs on one visual centre line — the row's <c>AlignItems=Center</c> cannot do it, because
    /// <c>ToolTip.Wrap</c>'s wrapper carries <c>AlignSelf=Start</c> and a per-child AlignSelf beats the row's AlignItems.</param>
    internal static BoxEl Transport(string glyph, Action onClick, bool enabled, bool active, ColorF accent, float box = 36f, float glyphSize = 16f,
        Action<NodeHandle>? onRealized = null, string? font = null, float? boxHeight = null)
    {
        float height = boxHeight ?? box;
        ColorF fg = !enabled ? Tok.TextDisabled : active ? accent : Tok.TextSecondary;
        ColorF hover = !enabled ? Tok.TextDisabled : active ? accent : Tok.TextPrimary;
        // The checked plate rides the SUBTLE ramp one rung below the interaction rungs above it, so a latched button and
        // a hovered button never render the same fill. Unchecked keeps every rung transparent (see the doc).
        ColorF rest = active && enabled ? Tok.FillSubtleSecondary : ColorF.Transparent;
        ColorF restHover = active && enabled ? Tok.FillSubtleTertiary : ColorF.Transparent;
        return new BoxEl
        {
            Width = box, Height = height, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control),
            Fill = rest, HoverFill = restHover, PressedFill = rest,
            BrushTransitionMs = WaveeMotion.Faster,   // the plate cross-fades on the toggle edge (WinUI 83ms), never snaps
            HoverScale = WaveeMotion.ScaleEmphatic.HoverIf(enabled), PressScale = WaveeMotion.ScaleEmphatic.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            OnRealized = onRealized,
            IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
            Children = [new TextEl(glyph) { Size = glyphSize, FontFamily = font ?? Theme.IconFont, Color = fg, HoverColor = hover }],
        };
    }

    internal static Element MoreButton(
        IReadOnlyList<AppBarCommand> commands,
        float box,
        float glyphSize,
        PlaybackBridge bridge,
        bool includeVolume)
    {
        // Open the overflow as a PLAIN MenuFlyout through the overlay service (the same path the toolbar "⋯" uses) so it
        // gets the engine's clean MenuPopupThemeTransition clip-reveal. We do NOT use CommandBarFlyout here: it layers
        // its own overflow-expand clip on top of the OverlayHost reveal, which made the chrome pop empty then fill in
        // (two out-of-sync clips → the "ugly open"). Toggle commands become E73E-checked ToggleMenuFlyoutItems.
        int vh = commands.Count * 31 + (includeVolume ? 1 : 0);
        for (int i = 0; i < commands.Count; i++)
        {
            var c = commands[i];
            // Cheap, alloc-light version: the command SET only changes at breakpoint crossings / toggle flips (not per
            // resize pixel), so fold a hash so the menu component re-mounts with fresh rows when the set actually changes.
            vh = vh * 31 + c.Icon.GetHashCode();
            vh = vh * 31 + (c.Label?.GetHashCode() ?? 0);
            if (c.IsChecked) vh ^= 0x55555555;
            if (c.Enabled) vh ^= 0x0F0F0F0F;
            // A command's own Flyout (the video placement ladder) can change shape — which rung is checked, which is
            // disabled, and why — without the outer command's IsChecked/Enabled moving at all (e.g. the rail-fit bit
            // flipping while playback state is untouched). Fold it too, or PlayerMoreMenu's frozen `_commands`
            // (component props freeze at mount) would show a stale cascading menu until an unrelated bar change
            // happens to bump this hash.
            if (c.Flyout is { Count: > 0 } sub)
                for (int j = 0; j < sub.Count; j++)
                {
                    var si = sub[j];
                    vh = vh * 31 + (si.Label?.GetHashCode() ?? 0);
                    vh = vh * 31 + (si.AcceleratorText?.GetHashCode() ?? 0);
                    if (si.IsChecked) vh ^= 0x33333333;
                    if (si.Enabled) vh ^= 0x0C0C0C0C;
                }
        }
        string version = "more#" + vh;
        return new BoxEl
        {
            Key = "more", Width = box, Height = box, Animate = ItemMotion,
            Children = [Embed.Comp(() => new PlayerMoreMenu(commands, bridge, includeVolume, box, glyphSize)) with { Key = version }],
        };
    }

    // The player-bar "⋯" overflow: opens a plain MenuFlyout UPWARD out of the button (TopEdgeAlignedRight) via the
    // overlay service, so it gets the engine's MenuPopupThemeTransition clip-reveal (grows up from the anchor edge) —
    // the same clean open as the toolbar overflow. Replaces the old CommandBarFlyout whose extra overflow-expand clip
    // fought the reveal (empty-then-fill flash) and read darker/heavier than a WinUI MenuFlyout.
    sealed class PlayerMoreMenu : Component
    {
        readonly IReadOnlyList<AppBarCommand> _commands;
        readonly PlaybackBridge _bridge;
        readonly bool _includeVolume;
        readonly float _box, _glyph;
        public PlayerMoreMenu(
            IReadOnlyList<AppBarCommand> commands,
            PlaybackBridge bridge,
            bool includeVolume,
            float box,
            float glyph)
        {
            _commands = commands;
            _bridge = bridge;
            _includeVolume = includeVolume;
            _box = box;
            _glyph = glyph;
        }

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var svc = UseContext(Overlay.Service);

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                if (_commands.Count == 0 && !_includeVolume) return;

                // Build at open time so the software/session mute label reflects the live device state without making
                // the whole player bar subscribe to the hot volume signal.
                var items = new List<MenuFlyoutItem>(_commands.Count + (_includeVolume ? 1 : 0));
                for (int i = 0; i < _commands.Count; i++)
                {
                    var c = _commands[i];
                    // A command carrying its own Flyout (the video placement ladder below ~1100 DIP) becomes a
                    // cascading sub-menu row — MenuFlyout natively supports MenuItemKind.SubMenu (hover/Right-arrow
                    // opens the nested popup; Actions/Menus.cs and DetailTracks.cs already rely on this outside
                    // CommandBarFlyoutBody), so no new engine machinery is needed here.
                    items.Add(c.Flyout is { Count: > 0 } sub
                        ? MenuFlyoutItem.SubMenu(c.Label, sub, c.Icon, c.Enabled)
                        : c.Kind == AppBarCommandKind.ToggleButton
                            ? MenuFlyoutItem.Toggle(c.Label, c.IsChecked, c.Invoke, c.Icon, c.Enabled)
                            : new MenuFlyoutItem(c.Label, c.Icon, c.Enabled, c.Invoke));
                }
                if (_includeVolume)
                {
                    bool muted = _bridge.OutputMuted.Peek() || _bridge.Volume.Peek() <= 0.001f;
                    items.Add(new MenuFlyoutItem(
                        Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute),
                        muted ? Icons.Volume : Icons.Mute,
                        true,
                        () => VolumeButton.ToggleMute(_bridge)));
                }

                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                    FlyoutPlacement.TopEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Width = _box, Height = _box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
                HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                OnClick = Toggle, Cursor = CursorId.Hand, OnRealized = h => anchor.Value = h,
                Children = [new TextEl(Icons.More) { Size = _glyph, FontFamily = Theme.IconFont, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
            };
        }
    }

    /// <summary>The primary play/pause action — a filled accent circle (the bar's focal point), glyph on-accent. Hover
    /// brightens to the accent-secondary/tertiary ramp; disabled drops to the neutral control fill.</summary>
    internal static BoxEl Primary(string glyph, Action onClick, bool enabled, ColorF accent, float box = 40f, float glyphSize = 23f)
    {
        var inner = new BoxEl
        {
            Width = box, Height = box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HitTestVisible = false,
            Children = [new TextEl(glyph) { Size = glyphSize, FontFamily = Theme.IconFont, Color = enabled ? Tok.TextPrimary : Tok.TextDisabled, HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary }],
        };
        return new BoxEl
        {
            Width = box, Height = box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            HoverScale = WaveeMotion.ScaleEmphatic.HoverIf(enabled), PressScale = WaveeMotion.ScaleEmphatic.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
            Children = [inner],
        };
    }
}

/// <summary>Cross-surface player-bar preference epoch: bumped when <see cref="WaveeSettings.PlayerBarShowRemaining"/>
/// changes from either the Settings toggle or the player-bar time label, so both surfaces stay in sync.</summary>
static class PlayerBarPrefs
{
    public static readonly Signal<int> Epoch = new(0);
    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;
}

/// <summary>A single time label (elapsed, or "-remaining"). Its OWN component so it re-renders at ~1 Hz on the position
/// tick WITHOUT re-rendering the whole bar (the seek bar beside it is compositor-only).</summary>
sealed class TimeText : Component
{
    readonly PlaybackBridge _b; readonly bool _remaining;
    // The label's ink. NOT a seam and NOT an optional dependency — a presentation override, so the ONE time-label
    // component can serve both the theme-token player bar and the immersive stage, where every glyph is theme-INVARIANT
    // on-media white over a dark veil. Null = the bar's own Caption/Secondary rung (unchanged).
    readonly ColorF? _ink;
    public TimeText(PlaybackBridge b, bool remaining, ColorF? ink = null) { _b = b; _remaining = remaining; _ink = ink; }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var (showRemaining, setShowRemaining) = UseState(svc?.Settings.Get(WaveeSettings.PlayerBarShowRemaining) ?? true);
        // Either surface bumps PlayerBarPrefs after writing the setting → re-seed the mounted label live.
        int prefsEpoch = PlayerBarPrefs.Epoch.Value;
        UseEffect(() => setShowRemaining(svc?.Settings.Get(WaveeSettings.PlayerBarShowRemaining) ?? true), prefsEpoch);
        long pos = _b.PositionMs.Value;          // subscribe → 1 Hz tick
        long dur = _b.DurationMs.Value;
        bool rightDuration = _remaining;
        // LIVE. Both halves of the row change, and both changes are the same idea: state what IS true instead of
        // dressing a broadcast up as a track.
        //   • The DVR window (`Live`) is the engine-authoritative fact — read as a signal so the row arrives and leaves
        //     WITH the playable. `IsLive` alone still counts (radio publishes liveness with no window at all).
        //   • RIGHT: a broadcast has no remaining time to count down (duration is 0 and stays 0), so the slot carries
        //     the live mark — or, once the playhead has fallen behind a rewindable window, the way back to the edge.
        //     The toggle-to-duration gesture goes with the label, because there is no duration to toggle to.
        //   • LEFT: position is a coordinate on the broadcast's clock, not "how long you have been here", so it counts
        //     elapsed since TUNE-IN. That is the only elapsed a listener can act on, and it needs the hours rung.
        var live = _b.Live.Value;
        bool isLive = live.IsLive || _b.IsLive.Value;
        // Read unconditionally (never inside the live arm) so the subscription set is the same in every render — and
        // read it from the BRIDGE, which folds the hysteresis once, rather than comparing BehindMs to a threshold here.
        bool behindLive = _b.IsBehindLive.Value;
        if (rightDuration && isLive) return LiveSlot(_b, live, behindLive, _ink);
        bool remainingMode = rightDuration && showRemaining;
        long ms = rightDuration ? (remainingMode ? Math.Max(0, dur - pos) : dur) : pos;
        if (!rightDuration && isLive) ms = ElapsedSinceTuneIn(_b, pos);
        // ms == 0 in remaining mode means "there is nothing left" (paused exactly at/after the end) — "-0:00" reads as
        // a negative countdown that never resolves; the sign belongs only to an ACTUAL remainder.
        string s = (remainingMode && ms > 0 ? "-" : "") + PlayerBarContent.Fmt(ms);
        void ToggleDuration()
        {
            if (!rightDuration) return;
            bool next = !showRemaining;
            setShowRemaining(next);
            svc?.Settings.Set(WaveeSettings.PlayerBarShowRemaining, next);
            PlayerBarPrefs.Bump();
        }
        return new BoxEl
        {
            // 44 DIPs is the slot at rest, so a track starting does not reflow the seek row around it. While live the
            // elapsed label grows an hours field (1:04:22 does not fit 44) — the slot becomes a FLOOR, not a cage.
            Width = isLive ? float.NaN : 44f, MinWidth = 44f, Shrink = 0f,
            Direction = 0, AlignItems = FlexAlign.Center,
            Justify = _remaining ? FlexJustify.Start : FlexJustify.End,
            Fill = ColorF.Transparent,
            HoverFill = rightDuration ? (_ink is null ? Tok.FillSubtleSecondary : WaveeOnMedia.GlassHover) : ColorF.Transparent,
            PressedFill = rightDuration ? (_ink is null ? Tok.FillSubtleTertiary : WaveeOnMedia.GlassPressed) : ColorF.Transparent,
            Corners = CornerRadius4.All(Radii.Control),
            OnClick = rightDuration ? ToggleDuration : null,
            Cursor = rightDuration ? CursorId.Hand : (CursorId?)null,
            Children =
            [
                _ink is { } ink
                    ? Caption(s) with { Color = ink, Wrap = TextWrap.NoWrap }
                    : Caption(s).Secondary() with { Wrap = TextWrap.NoWrap },
            ],
        };
    }

    /// <summary>How long this listener has been tuned in, in ms — what "elapsed" means for a broadcast.
    /// <para><see cref="PlaybackBridge.TunedInAtMs"/> is Unix ms stamped when live-ness began. Falls back to the
    /// reported position when nothing has been stamped yet (the first tick of a stream whose live-ness has not landed),
    /// so the label never blinks to 0:00 on the way in.</para></summary>
    static long ElapsedSinceTuneIn(PlaybackBridge b, long positionMs)
    {
        long tunedIn = b.TunedInAtMs.Value;   // subscribe → re-anchor when the broadcast changes
        if (tunedIn <= 0L) return positionMs;
        long since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - tunedIn;
        return since > 0L ? since : 0L;
    }

    /// <summary>The right-hand slot while live. Two states, one question — "am I hearing what is happening now?".
    /// <para>AT THE EDGE (or with nothing to rewind) it is a statement: the <see cref="LivePill"/>. BEHIND a rewindable
    /// window it becomes an ACTION, because there is now something to do about it.</para>
    /// <para><b>Which of the two is not decided here.</b> "Behind" is a hysteresis STATE, not a comparison —
    /// <c>Wavee.Backend.Playback.LiveEdgeState</c> owns it and <see cref="PlaybackBridge.IsBehindLive"/> publishes it.
    /// A threshold applied at this call site is the defect: a healthy live playhead rides 5–8 s inside a window that
    /// republishes several times a second, so the slot flickered between the mark and "GO LIVE −0:06" a few times a
    /// second and dragged the seek row's layout with it.</para></summary>
    static Element LiveSlot(PlaybackBridge b, LiveWindow live, bool behindLive, ColorF? ink)
        => new BoxEl
        {
            // ONE width for the slot's whole live life — see LiveSlotWidth. Both states are right-ALIGNED inside it,
            // so the two of them share a trailing edge and the swap moves nothing in either direction.
            Width = LiveSlotWidth, Shrink = 0f,
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            Fill = ColorF.Transparent,
            Children =
            [
                live.HasWindow && behindLive
                    ? GoLiveButton(b, live.BehindMs, ink)
                    : LivePill(ink),
            ],
        };

    /// <summary>The width the right-hand slot RESERVES for its whole live life, mark or action.
    /// <para>The rail beside it is a compositor-bound fill inside a <c>Grow</c> slot carrying a bounds
    /// <c>LayoutTransition</c>, so a slot that changes width does not merely reflow — it FLIPS, easing the entire rail
    /// sideways. Sizing the slot once for the widest thing it can ever hold ("GO LIVE −99:59" at the row's 9/12
    /// semibold caption face, with the button's own padding) means the mark↔action swap moves nothing at all.</para>
    /// <para>Live only. A track keeps the 44-DIP time slot — the widest thing THAT can hold is "-59:59".</para></summary>
    internal const float LiveSlotWidth = 104f;

    /// <summary>"GO LIVE −1:20" — the way back to the edge, offered only when there is a window to be behind IN.
    /// <para>It is a TEXT button, not a plate: the slot sits between the elapsed label and the scrub rail, and a filled
    /// accent button there would read as the row's primary action over the transport itself. So it takes accent as
    /// CONTENT (<see cref="WaveeAccent.Decor"/> ink — this one IS an action, unlike the pill's decorative mark) with
    /// the app's subtle interaction tier, and the immersive stage's on-media ink still wins.</para>
    /// <para>It is content-sized inside the reserved <see cref="LiveSlotWidth"/> slot, so its interaction plate hugs
    /// the words instead of spanning a mostly-empty 104 DIPs, and the count is never clipped to "GO LIV…".
    /// <c>MinWidth</c> keeps the row's rhythm when the count is short.</para></summary>
    static Element GoLiveButton(PlaybackBridge b, long behindMs, ColorF? ink)
    {
        ColorF fg = ink ?? WaveeAccent.Decor;
        string label = Loc.Get(Strings.Play.GoLive) + " " + Strings.Play.Behind(TimeFormat.Clock(behindMs));
        return new BoxEl
        {
            MinWidth = 44f, Shrink = 0f,
            Height = 20f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
            Corners = CornerRadius4.All(Radii.Control),
            Fill = ColorF.Transparent,
            HoverFill = ink is null ? Tok.FillSubtleSecondary : WaveeOnMedia.GlassHover,
            PressedFill = ink is null ? Tok.FillSubtleTertiary : WaveeOnMedia.GlassPressed,
            HoverScale = WaveeMotion.ScaleSubtle.Hover,
            PressScale = WaveeMotion.ScaleSubtle.Press,
            Role = AutomationRole.Button,
            Focusable = true,
            Cursor = CursorId.Hand,
            OnClick = b.GoLive,
            Children =
            [
                new TextEl(label)
                {
                    Size = 9f, LineHeight = 12f, Weight = 600, Color = fg, Wrap = TextWrap.NoWrap,
                },
            ],
        };
    }

    /// <summary>The LIVE mark that replaces the remaining-time label. It is the app's ONE badge shape — the classic
    /// track table's content-rating word-mark (<c>TrackRow.ClassicExplicitBadge</c>: a 14px box, 2px corner, 1px
    /// stroke, 9/12 semibold caps-as-authored) — so the bar does not invent a second badge language for the same job.
    ///
    /// <para>It paints ACCENT INK (<see cref="WaveeAccent.Decor"/>): accent as CONTENT, which is the role that owns
    /// text, never accent as structure. On the immersive stage the caller's theme-invariant on-media ink wins, exactly
    /// as it does for the time labels. It is content-sized inside the reserved <see cref="LiveSlotWidth"/> slot, which
    /// is what keeps the seek row from reflowing when the mark becomes the GO LIVE action and back.</para></summary>
    static Element LivePill(ColorF? ink)
    {
        ColorF fg = ink ?? WaveeAccent.Decor;
        return new BoxEl
        {
            Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            Gap = Spacing.XXS,
            Fill = ColorF.Transparent,
            Children =
            [
                // The broadcast dot — the one piece of the mark that is NOT accent. Every recording indicator ever
                // built is red, so red is the fastest possible read of "this is happening now", and the critical fill
                // is the app's ONE red. It is STATIC: a blinking dot in a bar that is always on screen is a permanent
                // motion source in the corner of the eye, and it would keep the frame loop awake for its whole life.
                new BoxEl
                {
                    Width = 6f, Height = 6f, Shrink = 0f,
                    Corners = CornerRadius4.All(3f),
                    Fill = Tok.SystemFillCritical,
                },
                new BoxEl
                {
                    Height = 14f, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f), Shrink = 0f,
                    Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = fg,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Play.Live))
                        {
                            Size = 9f, LineHeight = 12f, Weight = 600, Color = fg, Wrap = TextWrap.NoWrap,
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>The volume / mute glyph. Its OWN component so the glyph swap (volume crossing 0) doesn't re-render the bar
/// during a volume drag (the slider beside it is compositor-only).</summary>
// The live two-section device-picker flyout body. Reads the bridge signals in Render so the roster updates WHILE the
// flyout is open (roster/selection/active-device/supported all fall out of signals), unlike a frozen open-time snapshot.
sealed class DevicePickerMenu : Component
{
    readonly PlaybackBridge _b; readonly Action _close;
    public DevicePickerMenu(PlaybackBridge b, Action close) { _b = b; _close = close; }
    public override Element Render() => MenuFlyout.Create(PlayerBarContent.DevicePickerItems(_b), _close);
}

sealed class VolumeButton : Component
{
    readonly PlaybackBridge _b; readonly bool _popup; readonly float _box; readonly float _glyphSize;
    public VolumeButton(PlaybackBridge b, bool popup = false, float box = 32f, float glyphSize = 16f)
    {
        _b = b; _popup = popup; _box = box; _glyphSize = glyphSize;
    }

    public override Element Render()
    {
        float v = _b.Volume.Value;               // subscribe → swap Mute/Volume glyph at the 0 boundary
        bool muted = _b.OutputMuted.Value;       // subscribe → external/session mute drives the glyph too (Phase B4)
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        string g = (muted || v <= 0.001f) ? Icons.Mute : Icons.Volume;
        void TogglePopup()
        {
            if (!_popup) { ToggleMute(_b); return; }
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new VolumePopup(_b)),
                FlyoutPlacement.TopCenter,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
        }
        return PlayerBarContent.Transport(g, TogglePopup, true, false, Tok.AccentDefault, _box, _glyphSize, h => anchor.Value = h);
    }

    internal static void ToggleMute(PlaybackBridge b)
    {
        // With a real output-device control (local audio wired) mute the Windows session directly (Phase B4); our own
        // session set is filtered by the engine's context guard, so update the optimistic UI here. Otherwise (fake
        // backend) keep today's software 0 ⇄ 0.7 toggle.
        if (b.LocalOutputs is { } lo)
        {
            bool nowMuted = !b.OutputMuted.Peek();
            b.OutputMuted.Value = nowMuted;
            lo.SetMuted(nowMuted);
            return;
        }
        float v = b.Volume.Peek();
        float nv = v > 0.001f ? 0f : 0.7f;       // mute ⇄ restore (a fuller mute-with-memory is the device-panel pass)
        b.Volume.Value = nv; _ = b.Player.SetVolumeAsync(nv);
    }
}

sealed class RemoteDeviceLine : Component
{
    readonly PlaybackBridge _b;

    public RemoteDeviceLine(PlaybackBridge b) => _b = b;

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        var remote = PlayerBarContent.RemoteDevice(_b);   // reads Devices + ActiveDeviceId → subscribes for re-render
        if (remote is null) return new BoxEl { Height = 0f, HitTestVisible = false };

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new DevicePickerMenu(_b, () => handle.Value?.Close())),
                FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        ColorF accent = Tok.AccentDefault;
        return new BoxEl
        {
            Height = 13f, Direction = 0, AlignItems = FlexAlign.Center, Gap = 4f,
            MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = Toggle, OnRealized = h => anchor.Value = h, ClipToBounds = true,
            Children =
            [
                new TextEl(Icons.Devices) { Size = 12f, FontFamily = Theme.IconFont, Color = accent with { A = 0.88f }, HoverColor = accent },
                new BoxEl
                {
                    Shrink = 1f, MinWidth = 0f, ClipToBounds = true,
                    Children =
                    [
                        new TextEl(Strings.Player.PlayingOn(remote.Name))
                        {
                            Size = 12f, LineHeight = 16f, Weight = 600, Color = accent with { A = 0.88f }, HoverColor = accent,
                            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
            ],
        };
    }
}

// Which mounted DevicesButton instance responds to a bridge DevicePickerRequest (the "Choose device" toast action):
// Bar = the player-bar button; None = never auto-open (embedded pickers).
enum DevicePickerScope : byte { None, Bar }

// The Connect device picker: opens a MenuFlyout of the live device roster (from the bridge) UPWARD out of the button.
// Each row toggles active + transfers playback to that device on click. Re-renders when the roster / active device changes.
// Also opens itself when the bridge's DevicePickerRequest is bumped (the critical "playback unsupported" toast's action) —
// only the Bar-scoped instance responds, so the toast opens exactly one picker.
sealed class DevicesButton : Component
{
    // No accent ctor arg: ctor args freeze at mount (Embed.Comp preserves the instance across parent re-renders),
    // so the accent is read in Render() where it stays live — RethemeAll re-renders this component on any Tok.Epoch
    // bump, and the now-playing scope's TrackPalette read subscribes to art changes.
    readonly PlaybackBridge _b; readonly float _box, _glyph; readonly DevicePickerScope _scope;
    public DevicesButton(PlaybackBridge b, float box = 36f, float glyph = 16f, DevicePickerScope scope = DevicePickerScope.None)
    {
        _b = b; _box = box; _glyph = glyph; _scope = scope;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        _ = _b.Devices.Value;                         // subscribe → re-render on roster change (flyout content reads its own)
        // The ON state is "playback is on ANOTHER device", not "some device id is set" — which is true in essentially
        // every live session, this PC included. That distinction did not show while the on-state was a 3px dot nobody
        // could see; now that it is a real plate (see Transport), a button lit permanently would be pure noise. This is
        // also the same condition RemoteDeviceLine uses to decide whether to say "Playing on <device>" at all, so the
        // glyph and the line can no longer disagree. RemoteDevice reads Devices + ActiveDeviceId → still subscribed.
        bool active = PlayerBarContent.RemoteDevice(_b) is not null;
        int req = _b.DevicePickerRequest.Value;       // subscribe → re-render (and re-run the effect) on a toast "Choose device" click
        var lastReq = UseRef(req);                     // seeded at mount → a request that predates this mount is ignored

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new DevicePickerMenu(_b, () => handle.Value?.Close())),
                FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        // Open the picker in response to the toast action — in an effect (post-render), never during render. Both mounted
        // instances consume the request (each has its own lastReq), but only the scope matching Expanded actually opens it.
        UseEffect(() =>
        {
            if (_scope != DevicePickerScope.Bar || req == lastReq.Value) return;
            lastReq.Value = req;
            if (handle.Value is not { IsOpen: true }) Toggle();
        }, req);

        return PlayerBarContent.Transport(Icons.Devices, Toggle, true, active, Tok.AccentDefault, _box, _glyph, h => anchor.Value = h);
    }
}

sealed class VolumePopup : Component
{
    readonly PlaybackBridge _b;
    public VolumePopup(PlaybackBridge b) { _b = b; }

    public override Element Render()
    {
        // Signal-bound: the vertical volume thumb rides _b.Volume on the compositor bind (a scrub writes the signal),
        // so onChange only pushes the async device write; no per-move re-render.
        return new BoxEl
        {
            Width = 52f, Height = 168f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(10f, 14f, 10f, 14f),
            Children =
            [
                Slider.Create(_b.Volume, next => { _ = _b.Player.SetVolumeAsync(next); },
                    new Slider.SliderOptions { Vertical = true, IsThumbToolTipEnabled = false },
                    length: 124f, thickness: 32f, style: Slider.DefaultStyle)
            ],
        };
    }
}
