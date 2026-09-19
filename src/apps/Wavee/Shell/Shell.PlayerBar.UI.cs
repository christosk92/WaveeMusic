// ── Shell/Shell.PlayerBar.UI.cs ────────────────────────────────────────────────────────────────────────────────────
// the player bar, seek bar, responsive tiers, the device picker menu and the device roster it reads (ch 20 asks for
// a home; the roster is a read of Spotify.Connect's cluster, not a new store), the four toggles — which write
// Shell.Ui's rail state
//
// Role: UI
// Owner: I (stage B, I2)
// Wave: 4
// Budget: 2000 lines
// Spec: ch 20 §0-§6, §9 (1,900-2,100); the pure rules it renders from are `Shell.cs` §7 and `Shell.PlayerBar.cs`
//
// THE COST MODEL, ported as one design (ch 20 §9 "Traps"): the BAR is rich (marquees, links, menus, drag, drop) but
// re-renders only on LOW-frequency facts; everything HOT is either a compositor bind (the seek fill + thumb, the volume
// rail) or an isolated bound text channel (the two time labels); and the dock root is a LAYOUT FIREWALL
// (`IsolateLayout`), so a title change never re-solves the window.
//
// THE CLOCK. The playhead is extrapolated from the model's TIMESTAMPED sample — `State.Position(now)`, i.e.
// `PosMs + (now − PosQpc)` on `Playback.FrameNowMs`, the same frame clock the sample was stamped with — never from an
// `Environment.TickCount64` anchor of its own (0.2.9's `_tickWallMs`, deleted). The pixel-due interval that repaints it
// is armed only while the playhead ADVANCES (`SeekRail.Advances`); a paused, loading or refilling bar wakes no frames.
//
// PROPS FREEZE AT MOUNT — the five places this surface meets it: the tier arrives as a SIGNAL; the marquee's text and
// ink arrive as `Prop.Of` thunks that read everything they depend on; `BarSeekRail`'s enablement is derived in Render;
// `BarVolumeButton` is remounted by a Key that encodes its form; `BarMoreMenu` is remounted by a Key over its command set
// (its rows are built at OPEN time from live reads, so checked states can never be stale).

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Shell
{
    // ══ 0. THE MOUNT POINT AND THE SHARED VERBS ══════════════════════════════════════════════════════════════════════

    // MOUNT POINT (stage B contract)
    /// <summary>The 72-DIP player dock. Named <c>PlayerBarDock</c>, not <c>PlayerBar</c>: `Shell.cs` §7 already declares
    /// the nested class <c>Shell.PlayerBar</c> (the tier constants), and a method of the same name is CS0102.
    /// The frame mounts it full-width at the bottom of the window column and UNMOUNTS it while video is fullscreen.</summary>
    public static Element PlayerBarDock() => Embed.Comp(static () => new PlayerBarRoot());

    /// <summary>THE play/pause intent — the bar's primary, the Space key, the palette and the deck faces all land here,
    /// so there is one play button's worth of behaviour in the app.</summary>
    public static void TogglePlayPause() => Playback.TogglePlay();

    public static void ToggleShuffle() => Playback.SetShuffle(!Playback.Shuffle.Peek());

    /// <summary>Off → Context → Track → Off.</summary>
    public static void CycleRepeat() => Playback.SetRepeat(PlayerBarRules.NextRepeat(Playback.Repeat.Peek()));

    /// <summary>Mute the Windows session when there is a local output to mute; otherwise the software 0 ⇄ 0.7 toggle
    /// (the fake path, a Connect viewer).</summary>
    public static void ToggleMute()
    {
        if (Playback.Audio.Supported.Peek()) { Playback.Audio.SetMuted(!Playback.Audio.Muted.Peek()); return; }
        Playback.SetVolume(PlayerBarRules.SoftwareMuteTarget(Playback.Volume.Peek()));
    }

    /// <summary>The errored primary: re-issue the current playable from where it stopped. 0.3 has no retry token (the
    /// 0.2.9 bridge's <c>InvokePlaybackErrorAction</c>); a fresh <c>PlayNow</c> of the same row, context and cursor is
    /// the same intent through the reducer's one door.</summary>
    public static void RetryPlayback()
    {
        var s = Playback.Snap();
        if (s.Current.IsNone) return;
        Playback.PlayNow(s.Current, s.Context, s.Cursor, s.Kind, s.PosMs);
    }

    /// <summary>The transport's seek rail as a reusable surface — the immersive stage's identity block mounts the same
    /// one (ch 21).</summary>
    public static Element SeekBar() => Embed.Comp(static () => new BarSeekRail());

    /// <summary>A transport time label. <paramref name="remaining"/> picks the right slot (−remaining ⇄ duration, or
    /// the live mark while live); <paramref name="ink"/> is the stage's theme-invariant on-media override (null = the
    /// bar's own caption ink). Both are STRUCTURAL and fixed for the label's life.</summary>
    public static Element TimeText(bool remaining, ColorF? ink = null) => Embed.Comp(() => new BarTimeText(remaining, ink));

    // ── the motion specs (ch 20 §5) ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A breakpoint crossing: every structural node FLIPs to its new rect over 167 ms.</summary>
    static readonly LayoutTransition BarMoveMotion = new(
        TransitionChannels.Bounds, TransitionDynamics.Tween(Design.Motion.Fast, Easing.FluentPopOpen), SizeMode.Reveal);

    /// <summary>A line entering/leaving a column: fade + size 0 → natural, so neighbours SLIDE over (Reflow is not
    /// resize-gated, so it animates during a pointer resize too). 250 in, 167 out. ONLY the remote-device line and the
    /// two time labels carry it: every command slot is fixed by the tier and lights its face instead
    /// (<see cref="Slot(string, float, float, bool, Element)"/>), so nothing in the row enters or leaves with playback
    /// STATE. The one exception is the video split, which exists iff the current track has a video and rides the
    /// right box's <see cref="BarMoveMotion"/> when it comes or goes (user decision 2026-09-16).</summary>
    static readonly LayoutTransition BarItemMotion = new(
        TransitionChannels.Bounds | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut),
        SizeMode.Reflow,
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Design.Motion.Fast, Easing.SmoothOut));

    /// <summary>The now-playing marquee's cadence: one slow pass every 14 s, a 3 s rest at each end and a 2 s hold on
    /// the start before the first pass — slower than the 10 s / 2.5 s hover cadence it replaces, because this one runs
    /// unattended.</summary>
    const float BarMarqueeCycleMs = 14_000f, BarMarqueeEndPauseMs = 3_000f, BarMarqueeStartDelayMs = 2_000f, BarMarqueeSpeed = 18f;
    const float SplitChevronGlyph = 10f;

    /// <summary>The app-local critical ink for an errored title (≈ #ED6B73, ch 00 §12.2 magic number).</summary>
    static readonly ColorF BarCriticalInk = new(0.93f, 0.42f, 0.45f, 1f);

    /// <summary>The ONE float the volume rails bind (inline + popup). A slider needs a FloatSignal and the model
    /// publishes a Signal&lt;float&gt;; <see cref="PlayerBarContent"/> mirrors model → rail with an equality gate, and the
    /// rail's own writes go to the model through <see cref="Playback.SetVolume"/>.</summary>
    static readonly FloatSignal s_barVolume = new(1f);

    static readonly Slider.SliderOptions s_volumeOptions = new()
    {
        ThumbToolTipValueConverter = static v => PlayerBarRules.VolumePercent(v),
    };

    // ══ 1. THE TIER OWNER ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Folds the viewport width into the coarse layout SIGNAL (with the 24-DIP narrowing hysteresis), so a
    /// resize drag re-renders the bar only at a tier crossing — never per pixel.</summary>
    sealed class PlayerBarRoot : Component
    {
        public override Element Render()
        {
            var viewport = UseContextSignal(Viewport.Size);
            float initialWidth = viewport.Peek().Width;
            var layout = UseSignal(PlayerBarLayout.Initial(initialWidth));
            var initialized = UseRef(initialWidth > 0f);

            UseSignalEffect(() =>
            {
                var previous = layout.Peek();
                float width = viewport.Value.Width;
                var next = PlayerBarLayout.Resolve(width, in previous, initialized.Value);
                if (width > 0f) initialized.Value = true;
                if (next.Equals(previous)) return;
                // Always-on and cheap: a band change is a handful of events per session (0.2.9 gated this on an env var).
                if (next.Tier != previous.Tier) Log.Debug("playerbar", "layout band " + previous.Tier + " -> " + next.Tier);
                layout.Value = next;
            });

            return Embed.Comp(() => new PlayerBarContent(layout));
        }
    }

    // ══ 2. THE BAR ═══════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class PlayerBarContent(IReadSignal<PlayerBarLayout> layout) : Component
    {
        readonly IReadSignal<PlayerBarLayout> _layout = layout;

        public override Element Render()
        {
            // ── hooks first, stable order ──
            var titleLinkHover = UseSignal(false);      // the title's link ink (the marquees scroll unattended)
            var likeNode = UseRef<NodeHandle>(default);
            var likePrevious = UseRef((0, false));
            var videoAnchor = UseRef<NodeHandle>(default);
            var videoMenu = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var hooks = UseContext(InputHooks.Current);
            var barNode = UseRef<NodeHandle>(default);
            float artScale = UseContext(Viewport.Scale);
            var rowStamp = UseComputed<(EntityKind Kind, int Slot, uint Version)>(BarRowStamp);
            UseSignalEffect(static () => s_barVolume.SetIfChanged(Playback.Volume.Value));

            bool isEpisode = Playback.CurrentId.Value.Kind == EntityKind.Episode;
            var L = isEpisode ? PodcastBarLayout(_layout.Value) : _layout.Value;
            bool marquee = Prefs.Appearance.Marquee();

            // ── the low-frequency facts ──
            _ = rowStamp.Value;
            var current = Playback.Current.Value;
            bool hasCurrent = !Playback.CurrentId.Value.IsEmpty;
            var phase = Playback.PhaseSignal.Value;
            bool buffering = Playback.Buffering.Value;
            var recovery = Playback.Recovery.Value;
            var facts = PlayerBarRules.Fold(hasCurrent, Playback.Error.Value, phase, buffering, recovery,
                Playback.PrevAllowedByContext.Value, Playback.NextAllowedByContext.Value);
            bool active = facts.Active;
            bool playing = Playback.IsPlaying.Value;
            bool shuffle = Playback.Shuffle.Value;
            var repeat = Playback.Repeat.Value;
            bool ownsTransport = PlayerBarRules.OwnsTransport(Video.State.Transport.Value);
            bool videoLive = Video.PlacementCore.IsActive(Video.State.Surface.Value);
            bool railOpen = Ui.RailOpen.Value;
            var railMode = Ui.Mode.Value;
            bool isTrack = !current.IsNone && current.Kind == EntityKind.Track;
            var track = isTrack ? new Track(current.Slot) : default;
            bool hasVideo = isTrack && track.IsValid && track.HasVideo;
            string? playingUri = BarPlayingUri(current);
            var episode = !current.IsNone && current.Kind == EntityKind.Episode ? new Episode(current.Slot) : default;
            bool liked = isEpisode ? Spotify.Podcasts.IsSaved(episode)
                : playingUri is not null && Controls.Library is { } library && library.IsSaved(playingUri);
            bool saveReady = !isEpisode || episode.IsValid && Spotify.Podcasts.SavedReady && !Spotify.Podcasts.SavedBusy;
            uint savedEpoch = Entities.ScopeEpoch.Value;
            UseLayoutEffect(() =>
            {
                if (isEpisode && !Spotify.Podcasts.SavedReady && !Spotify.Podcasts.SavedBusy)
                    _ = Spotify.Podcasts.ReadSavedAsync(System.Threading.CancellationToken.None);
            }, DepKey.From((int)savedEpoch, isEpisode ? 1 : 0));
            bool likeLit = PlayerBarRules.LikeFaceVisible(L, facts.State);

            // The heart pops on the SAME playable's save edge only — played on the captured node, because a keyed
            // remount of a focusable button would reset its hover/focus mid-toggle.
            int playingSlot = current.IsNone ? 0 : current.Slot;
            UseLayoutEffect(() =>
            {
                var (previousSlot, previouslyLiked) = likePrevious.Value;
                likePrevious.Value = (playingSlot, liked);
                if (PlayerBarRules.LikePops(previousSlot, previouslyLiked, playingSlot, liked) && likeLit
                    && !likeNode.Value.IsNull && Context.Anim is { } anim)
                    anim.IconSwapIn(likeNode.Value);   // the kit recipe honours reduced motion itself
            }, DepKey.From(playingSlot, liked ? 1 : 0));

            float box = L.ButtonBox, glyph = L.ButtonGlyph;

            // ── LEFT — the now-playing identity ──────────────────────────────────────────────────────────────────────
            int remoteSlot = active && L.ShowRemoteDeviceLine
                ? DeviceRoster.RemoteSlot(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, Playback.Devices.Rows)
                : -1;
            if (remoteSlot >= 0) _ = Playback.Devices.Changed.Value;

            Prop<ColorF> titleInk = Prop.Of(() => titleLinkHover.Value && !BarTitleRoute().IsNone
                ? Tok.AccentTextPrimary
                : BarTitleInk(PlayerBarRules.InkOf(BarStateNow())));
            bool titleNav = !BarTitleRoute().IsNone;
            // Both lines are KEYED ("np-title"/"np-artists"): the remote-device line is a keyed insert that precedes
            // them, and an unkeyed title would reuse the artists' mounted marquee host (#139).
            BoxEl titleEl = marquee
                ? (BoxEl)Marquee.Of(Prop.Of<string>(BarTitleText), new Marquee.Style
                {
                    FontSize = 14f, Weight = 700, Foreground = titleInk,
                    Speed = BarMarqueeSpeed, CycleMs = BarMarqueeCycleMs, EndPauseMs = BarMarqueeEndPauseMs,
                    StartDelayMs = BarMarqueeStartDelayMs,
                    // ALWAYS, at the slower cadence: a title exists to be read without aiming a pointer at it, and the
                    // hover gate left a long title permanently cut off for anyone who never hovered. The 0.2.9 worry
                    // (a 27-second idle scroll parked mid-word, S2 #12) is gone — the engine now glides the text home
                    // on deactivation instead of freezing it wherever it was. `titleLinkHover` still drives the link ink.
                    Mode = Marquee.ScrollMode.PingPong, Trigger = Marquee.TriggerMode.Always,
                })
                : new BoxEl
                {
                    ClipToBounds = true, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(Prop.Of<string>(BarTitleText))
                        {
                            Size = 14f, Weight = 700, Color = titleInk, Wrap = TextWrap.NoWrap, MaxLines = 1,
                            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                };
            titleEl = titleEl with { Key = "np-title" };
            if (titleNav)
                titleEl = titleEl with
                {
                    Cursor = CursorId.Hand, OnClick = static () => BarGo(BarTitleRoute()),
                    Role = AutomationRole.Hyperlink, Focusable = true,
                    OnHoverMove = _ => { if (!titleLinkHover.Peek()) titleLinkHover.Value = true; },
                    OnPointerExit = () => { if (titleLinkHover.Peek()) titleLinkHover.Value = false; },
                };

            var metaKids = new List<Element>(3);
            if (remoteSlot >= 0)
                metaKids.Add(new BoxEl
                {
                    Key = "remote-device-line", Animate = BarItemMotion,
                    Children = [Embed.Comp(static () => new BarRemoteDeviceLine())],
                });
            metaKids.Add(titleEl);
            if (PlayerBarRules.ShowsArtistLine(facts.State, L.ShowSubtitle))
                metaKids.Add(marquee
                    ? Marquee.Content(static () => new BarArtistsLine(compact: false), new Marquee.Style
                    {
                        Speed = BarMarqueeSpeed, CycleMs = BarMarqueeCycleMs, EndPauseMs = BarMarqueeEndPauseMs,
                        StartDelayMs = BarMarqueeStartDelayMs,
                        Mode = Marquee.ScrollMode.PingPong, Trigger = Marquee.TriggerMode.Always,
                    }) with { Key = "np-artists" }
                    : new BoxEl
                    {
                        Key = "np-artists", ClipToBounds = true, MinWidth = 0f,
                        Children = [Embed.Comp(static () => new BarArtistsLine(compact: true))],
                    });

            var metaCol = new BoxEl
            {
                Key = "meta", Animate = BarMoveMotion,
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 1f,
                Gap = 2f, Justify = FlexJustify.Center, ClipToBounds = true,
                Children = metaKids.ToArray(),
            };

            bool artNav = !BarArtRoute().IsNone;
            var art = new BoxEl
            {
                Key = "art", Width = L.ArtSize, Height = L.ArtSize, Shrink = 0f,
                Cursor = artNav ? CursorId.Hand : null,
                OnClick = artNav ? static () => BarGo(BarArtRoute()) : null,
                Role = artNav ? AutomationRole.Hyperlink : AutomationRole.None,
                Focusable = artNav,
                Children = [Controls.Artwork(BarArtUrl(current), L.ArtSize, L.ArtSize, 6f, scale: artScale)],
            };
            // The heart's SLOT is the tier's; an idle bar shows no face in it (and the face cannot be hit or focused).
            var saveIcon = isEpisode ? ActionIcons.Resolve(ActionIcons.Save, liked) : ActionIcons.Resolve(ActionIcons.Heart, liked);
            var saveButton = BarButton(saveIcon.Glyph ?? Icons.Add, static () => BarToggleLike(), likeLit && saveReady, liked,
                box, glyph, onRealized: h => likeNode.Value = h) with { BlocksDragArm = true };
            Element saveFace = isEpisode ? ToolTip.Wrap(saveButton,
                Loc.Get(liked ? Strings.Podcast.Reader.RemoveSaved : Strings.Podcast.Reader.Save)) : saveButton;
            var likeSlot = Slot("like", box, box, likeLit, saveFace);

            var left = new BoxEl
            {
                Key = "left", Width = L.LeftW, Shrink = 0f, MinWidth = 0f, Animate = BarMoveMotion,
                Direction = 0, AlignItems = FlexAlign.Center, Gap = L.LeftGap, ClipToBounds = true,
                // The factory PEEKS at promotion time: the mounted cluster outlives every track change. An idle bar is
                // not a drag handle at all.
                Draggable = isTrack ? Drag.Source(static () => BarDragPayload()) : null,
                Children = isEpisode && L.Tier == PlayerBarTier.Minimal ? [art] : L.ShowLikeSlot ? [art, metaCol, likeSlot] : [art, metaCol],
            };
            // Right-click / Menu key / long-press: the track menu over the now-playing target, with its header. ONE seam
            // feeds this cluster AND the immersive stage (`Stage.NowPlayingMenu`, installed by the track menu's owner);
            // unset, the factory answers null and nothing opens.
            left = left.WithContextMenu(overlay, static () => BarTrackPeek().IsValid ? Stage.NowPlayingMenu?.Invoke() : null);

            // ── CENTRE — transport + seek (empty while a full-bleed surface owns the transport) ─────────────────────
            var transportKids = new List<Element>(3);
            if (ownsTransport && L.ShowPrevNext)
                transportKids.Add(BarButton(Icons.Previous, static () => Playback.Previous(), facts.PrevEnabled, false, box, glyph)
                    with { Key = "prev" });
            if (ownsTransport && isEpisode)
                transportKids.Add(BarSeekStepButton(-10_000, box) with { Key = "seek-back-10" });
            if (ownsTransport)
                transportKids.Add(BarPrimaryButton(
                        facts.State == PlayerState.Error ? Icons.Play : playing ? Icons.Pause : Icons.Play,
                        facts.Primary, L.PrimaryBox, L.PrimaryGlyph)
                    with { Key = "primary", Animate = BarMoveMotion });
            if (ownsTransport && L.ShowPrevNext)
                transportKids.Add(BarButton(Icons.Next, static () => Playback.Next(), facts.NextEnabled, false, box, glyph)
                    with { Key = "next" });

            if (ownsTransport && isEpisode)
            {
                transportKids.Add(BarSeekStepButton(10_000, box) with { Key = "seek-forward-10" });
                transportKids.Add(Embed.Comp(static () => new BarEpisodeSpeed()) with { Key = "episode-speed" });
            }

            // Stable keys keep BarSeekRail's identity (scrub state, cached width) across the 760-DIP label breakpoint.
            var seekKids = new List<Element>(3);
            if (ownsTransport && L.ShowTimesElapsed)
                seekKids.Add(new BoxEl { Key = "elapsed", Animate = BarItemMotion, Children = [TimeText(remaining: false)] });
            if (ownsTransport)
                seekKids.Add(new BoxEl
                {
                    Key = "seek", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, Animate = BarMoveMotion,
                    Children = [SeekBar()],
                });
            if (ownsTransport && L.ShowTimesRemaining)
                seekKids.Add(new BoxEl { Key = "remaining", Animate = BarItemMotion, Children = [TimeText(remaining: true)] });

            var centre = new BoxEl
            {
                Key = "centre", Grow = 1f, Shrink = 1f, MinWidth = 0f, Animate = BarMoveMotion,
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Gap = L.ClusterGap,
                // A drop on the transport is PLAY NEXT — a front-insert, never an immediate playback change.
                DropTarget = Drop.Target<DragPayload>(Drag.Resource,
                    accepts: static p => p.CanCopyTracks,
                    onDrop: static (p, session) => BarPlayNextDrop(p),
                    caption: static _ => Loc.Get(Strings.Drag.PlayNext),
                    refusalCaption: static p => Loc.Get(p.Kind == DragKind.Artist ? Strings.Drag.CantAddArtist : Strings.Drag.NothingToAdd),
                    visualPolicy: DropTargetVisualPolicy.Spotlight),
                Children =
                [
                    new BoxEl
                    {
                        Key = "transport", Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Animate = BarMoveMotion, Children = transportKids.ToArray(),
                    },
                    new BoxEl
                    {
                        Key = "seek-row", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center,
                        Gap = L.SeekGap, Animate = BarMoveMotion, Children = seekKids.ToArray(),
                    },
                ],
            };

            // ── RIGHT — shuffle/repeat · volume · lyrics · video · queue · devices · expand · ⋯ ──────────────────────
            // Every slot the TIER has is in the row at its fixed width (PlayerBarRules.RightSlots, in bit order) EXCEPT the
            // video split, which exists iff the current track has a video — the cluster reclaims its 52 DIP otherwise
            // rather than carrying an unlit hole (user decision 2026-09-16). Playback state only lights a face
            // (PlayerBarRules.SlotFaceVisible). The cluster's width is therefore one of exactly TWO values per tier
            // (with / without the split; the wider is L.RightWMax), a track starting WITHOUT a video moves nothing in the
            // centre, and a video track landing eases the row once via BarMoveMotion on `right`.
            void OpenVideoMenu()
            {
                if (videoMenu.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = Video.PlacementMenu(includeFullscreen: true);
                videoMenu.Value = overlay.Open(
                    () => videoAnchor.Value,
                    () => MenuFlyout.Create(items, () => videoMenu.Value?.Close()),
                    FlyoutPlacement.TopEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                videoMenu.Value.ClosedAction = () => videoMenu.Value = null;
            }

            // Materialised here (a Span cannot cross into the local function below); the "⋯" face is keyed over it.
            Span<OverflowCommand> overflow = stackalloc OverflowCommand[PlayerBarRules.MaxOverflow];
            int overflowCount = PlayerBarRules.Overflow(L, ownsTransport, active, hasVideo, overflow);
            OverflowCommand[] overflowCommands = overflow[..overflowCount].ToArray();

            Element RightSlotOf(RightSlot slot)
            {
                float w = PlayerBarRules.SlotWidth(slot, in L);
                bool lit = PlayerBarRules.SlotFaceVisible(slot, facts.State, hasVideo);
                switch (slot)
                {
                    case RightSlot.Shuffle:
                        return Slot("shuffle", w, box, lit,
                            BarButton(Icons.Shuffle, static () => ToggleShuffle(), facts.CanTransport, shuffle, box, glyph));
                    case RightSlot.Repeat:
                        return Slot("repeat", w, box, lit,
                            BarButton(PlayerBarRules.RepeatGlyph(repeat), static () => CycleRepeat(), facts.CanTransport,
                                repeat != RepeatMode.Off, box, glyph));
                    case RightSlot.Volume:
                    {
                        bool popup = !L.ShowVolumeSlider;
                        return Slot("volume", w, box, lit,
                            Embed.Comp(() => new BarVolumeButton(popup, box, glyph))
                                with { Key = (popup ? "volume-popup-" : "volume-inline-") + box + "-" + glyph });
                    }
                    case RightSlot.VolumeSlider:
                        // The STOCK slider style — a 22-DIP ring, not 0.2.9's old 12-DIP grab; thickness clears the ring.
                        return Slot("volume-slider", w, box, lit,
                            Slider.Create(s_barVolume, static v => Playback.SetVolume(v), s_volumeOptions,
                                length: PlayerBarLayout.VolumeSliderW, thickness: Slider.DefaultStyle.ThumbRingDiameter));
                    case RightSlot.Lyrics:
                        return Slot("lyrics", w, box, lit,
                            ToolTip.Wrap(BarButton(WaveeIcons.Lyrics, static () => Ui.Toggle(RailMode.Lyrics), lit,
                                railOpen && railMode == RailMode.Lyrics, box, glyph, font: WaveeIcons.Font),
                                Loc.Get(isEpisode ? "podcast.reader.transcript" : Strings.Player.Lyrics)));
                    case RightSlot.Video:
                        // Only built while hasVideo holds (the slot is in `slots` iff the track has a video); the face
                        // additionally needs an Active playable, so it stays dark through Loading/Reconnecting/Error.
                        return Slot("video", w, box, lit, new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center,
                            OnRealized = h => videoAnchor.Value = h,
                            OnContextRequested = _ => OpenVideoMenu(),
                            Children =
                            [
                                ToolTip.Wrap(
                                    BarButton(Icons.Movie, static () => Video.State.TogglePrimary(BarHasVideoPeek()), lit, videoLive, box, glyph),
                                    Loc.Get(videoLive ? Strings.Player.SwitchToAudio : Strings.Player.SwitchToVideo)),
                                // A disclosure never lights. Narrow but FULL-HEIGHT, so the two glyphs share one centre line.
                                ToolTip.Wrap(
                                    BarButton(Icons.ChevronDownSmall, OpenVideoMenu, lit, false, PlayerBarLayout.SplitChevronW, SplitChevronGlyph, boxHeight: box),
                                    Loc.Get(Strings.Player.VideoOptions)),
                            ],
                        });
                    case RightSlot.Queue:
                        return Slot("queue", w, box, lit,
                            BarButton(Icons.Queue, static () => Ui.Toggle(RailMode.Queue), true,
                                railOpen && railMode == RailMode.Queue, box, glyph));
                    case RightSlot.Devices:
                        return Slot("devices", w, box, lit, Embed.Comp(() => new BarDevicesButton(box, glyph)));
                    case RightSlot.Expand:
                        return Slot("expand", w, box, lit,
                            BarButton(Icons.ChevronUp, static () => Ui.Toggle(RailMode.NowPlaying), true,
                                railOpen && railMode == RailMode.NowPlaying, box, glyph));
                    default:
                        // Reserved only where the idle menu is already non-empty, so the command set is never empty here.
                        return Slot("more", w, box, lit, BarMoreButton(overflowCommands, box, glyph));
                }
            }

            var slots = PlayerBarRules.RightSlots(in L, hasVideo);
            var rightKids = new List<Element>(10);
            for (int bit = 1; bit <= (int)RightSlot.More; bit <<= 1)
            {
                var slot = (RightSlot)bit;
                if ((slots & slot) != 0) rightKids.Add(RightSlotOf(slot));
            }

            var right = new BoxEl
            {
                Key = "right", Width = PlayerBarRules.RightWidth(in L, hasVideo), Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                Gap = L.RightGap, Animate = BarMoveMotion, Children = rightKids.ToArray(),
            };

            // ── assemble: the top activity edge + the single centred row ────────────────────────────────────────────
            Element topEdge = PlayerBarRules.TopEdgeSweeps(phase, buffering, recovery)
                ? ProgressBar.Indeterminate(L.TopEdgeWidth)
                : new BoxEl
                {
                    // The ONE seam on this edge. Light takes a black ALPHA literal, not StrokeCardDefault, which every
                    // seeded light preset resolves OPAQUE.
                    Height = 1f,
                    Fill = Prop.Of(static () => FluentGpu.Dsl.Theme.Dark ? Tok.StrokeDividerDefault : ColorF.FromRgba(0, 0, 0, 0x0F)),
                };

            return new BoxEl
            {
                // NO fill, NO shadow, NO Elevation.DockTop: the dock is a paint-site omission over live Mica.
                Direction = 1, Height = Design.Size.PlayerBarH, ClipToBounds = true,
                IsolateLayout = true,   // the layout firewall: a title change re-solves THIS subtree only
                Focusable = true, FocusVisualMargin = Edges4.All(Spacing.XXS),
                OnRealized = node => barNode.Value = node,
                OnClick = () => hooks.FocusNode?.Invoke(barNode.Value, true),
                OnKeyDown = e =>
                {
                    var intent = PlayerKey(e.KeyCode, hooks.GetFocus?.Invoke() == barNode.Value,
                        e.Handled, e.Ctrl || e.Alt || e.Shift);
                    if (intent == PlayerKeyIntent.None) return;
                    e.Handled = true;
                    switch (intent)
                    {
                        case PlayerKeyIntent.SeekBack: BarSeekBy(-10_000); break;
                        case PlayerKeyIntent.SeekForward: BarSeekBy(10_000); break;
                        case PlayerKeyIntent.VolumeDown: Playback.SetVolume(Math.Clamp(Playback.Volume.Peek() - .05f, 0f, 1f)); break;
                        case PlayerKeyIntent.VolumeUp: Playback.SetVolume(Math.Clamp(Playback.Volume.Peek() + .05f, 0f, 1f)); break;
                        case PlayerKeyIntent.Toggle: if (!e.IsRepeat) TogglePlayPause(); break;
                    }
                },
                Children =
                [
                    topEdge,
                    new BoxEl
                    {
                        Key = "player-row", Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = L.RowGap,
                        Padding = new Edges4(L.RowPad, 0f, L.RowPad, 0f), Animate = BarMoveMotion,
                        // ALWAYS three clusters: the row's shape is the tier's, never the state's.
                        Children = [left, centre, right],
                    },
                ],
            };
        }
    }

    // ── the now-playing reads (every one peeks or reads the model LIVE — never a render-time capture) ────────────────

    /// <summary>(kind, slot, row version): the ONE subscription the bar takes on the catalog. The memo's equality
    /// cut-off means a 300-row playlist landing elsewhere in the scope re-renders nothing here.</summary>
    static (EntityKind Kind, int Slot, uint Version) BarRowStamp()
    {
        var r = Playback.Current.Value;
        if (r.IsNone) return (EntityKind.Unknown, 0, 0u);
        var table = Entities.TableFor(r.Kind);
        if (table is null) return (r.Kind, r.Slot, 0u);
        _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
        _ = table.Changed.Value;
        return (r.Kind, r.Slot, (uint)r.Slot < (uint)table.Count ? table.Version[r.Slot] : 0u);
    }

    static PlayerState BarStateNow()
        => PlayerBarRules.StateOf(!Playback.CurrentId.Value.IsEmpty, Playback.Error.Value, Playback.PhaseSignal.Value,
            Playback.Recovery.Value);

    static ColorF BarTitleInk(NowPlayingInk ink) => ink switch
    {
        NowPlayingInk.Secondary => Tok.TextSecondary,
        NowPlayingInk.Critical => BarCriticalInk,
        _ => Tok.TextPrimary,
    };

    /// <summary>The title line's text, read LIVE inside a bound channel (the marquee host freezes its ctor args).</summary>
    static string BarTitleText()
    {
        var r = Playback.Current.Value;
        string title = BarPlayableTitle(r);
        return PlayerBarRules.TextOf(BarStateNow(), title.Length > 0) switch
        {
            NowPlayingText.Title => title,
            NowPlayingText.NothingPlaying => Loc.Get(Strings.Player.NothingPlaying),
            NowPlayingText.Reconnecting => Loc.Get(Strings.Player.Reconnecting),
            // G-212: the reason on the model, not one fixed sentence for every fault.
            NowPlayingText.CannotPlay => Loc.Get(PlayerBarRules.FaultTitleKey(Playback.Error.Value)),
            _ => Loc.Get(Strings.Player.Loading),
        };
    }

    static string BarPlayableTitle(EntityRef r)
    {
        if (r.IsNone) return "";
        switch (r.Kind)
        {
            case EntityKind.Track:
            {
                _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
                _ = Entities.Current.Tracks.Changed.Value;
                var t = new Track(r.Slot);
                return t.IsValid && t.Knows(TrackFields.Title) ? t.Title : "";
            }
            case EntityKind.Episode:
            {
                _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
                _ = Entities.Current.Episodes.Changed.Value;
                var e = new Episode(r.Slot);
                return e.IsValid && e.Knows(EpisodeFields.Title) ? e.Title : "";
            }
            default:
                return "";
        }
    }

    static string? BarPlayingUri(EntityRef r)
    {
        if (r.IsNone) return null;
        return r.Kind switch
        {
            EntityKind.Track when new Track(r.Slot) is { IsValid: true } t => t.Uri.Text,
            EntityKind.Episode when new Episode(r.Slot) is { IsValid: true } e => e.Uri.Text,
            _ => null,
        };
    }

    static string? BarArtUrl(EntityRef r)
    {
        if (r.IsNone) return null;
        return r.Kind switch
        {
            EntityKind.Track when new Track(r.Slot) is { IsValid: true } t && t.Knows(TrackFields.Image) => Controls.ArtUrl(t.ImageId),
            EntityKind.Episode when new Episode(r.Slot) is { IsValid: true } e && e.Knows(EpisodeFields.Image) => Controls.ArtUrl(e.ImageId),
            _ => null,
        };
    }

    static Track BarTrackPeek()
    {
        var r = Playback.Current.Peek();
        return !r.IsNone && r.Kind == EntityKind.Track ? new Track(r.Slot) : default;
    }

    static bool BarHasVideoPeek() => BarTrackPeek() is { IsValid: true } t && t.HasVideo;

    /// <summary>Title → the album (a track) or the show (an episode). Resolved at INVOKE time.</summary>
    static Route BarTitleRoute()
    {
        var r = Playback.Current.Peek();
        if (r.IsNone) return Route.None;
        if (r.Kind == EntityKind.Episode)
        {
            var e = new Episode(r.Slot);
            return e.IsValid && e.Show.IsValid ? For(e.Show.Uri, e.Show.Title) : Route.None;
        }
        var t = new Track(r.Slot);
        return t.IsValid ? LinkFor(t, LinkSlot.Title) : Route.None;
    }

    /// <summary>Art → the playback CONTEXT (a playlist, Liked Songs, an album) — not derivable from the track, so the
    /// module slot answers first and the context uri is the fallback.</summary>
    static Route BarArtRoute()
    {
        var t = BarTrackPeek();
        if (t.IsValid && LinkFor(t, LinkSlot.Art) is { IsNone: false } module) return module;
        var context = Playback.ContextUri.Peek();
        return context.IsEmpty ? Route.None : For(new EntityUri(context));
    }

    static void BarGo(in Route route)
    {
        if (!route.IsNone) GoTo(route);
    }

    static void BarToggleLike()
    {
        var r = Playback.Current.Peek();
        if (!r.IsNone && r.Kind == EntityKind.Episode)
        {
            if (Spotify.Podcasts.SavedReady && !Spotify.Podcasts.SavedBusy) Spotify.Podcasts.ToggleSaved(new Episode(r.Slot));
            return;
        }
        if (BarPlayingUri(r) is not { } uri || Controls.Library is not { } library) return;
        library.ToggleSaved(uri, BarPlayableTitle(r));
    }

    static object? BarDragPayload()
    {
        var t = BarTrackPeek();
        if (!t.IsValid) return null;
        string uri = t.Uri.Text;
        return new DragPayload(DragKind.Track, uri, uri, t.Title, new EntityRef(EntityKind.Track, t.Slot),
            Tracks: [t], ArtUrl: Controls.ArtUrl(t.ImageId));
    }

    static void BarPlayNextDrop(DragPayload payload) => _ = BarPlayNextDropAsync(payload);

    /// <summary>The bar's one deposit: resolve the payload's tracks (cold — after the drop, never during the drag) and
    /// FRONT-insert them, last first, so the dropped order is the play order. Said out loud with a success toast.</summary>
    static async Task BarPlayNextDropAsync(DragPayload payload)
    {
        Track[] tracks;
        try { tracks = await payload.ResolveTracksAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("playerbar", "play-next drop could not resolve its tracks", ex); return; }
        if (tracks.Length == 0) return;
        Playback.ToUi(() =>
        {
            // G-211: nothing must answer with silence — a drop with no queue seam behind it still tells the user why,
            // instead of the drag simply evaporating.
            if (Actions.Services.PlayNext is not { } playNext)
            {
                Notify.Say(Loc.Get(Strings.Detail.QueueUnavailable), InfoBarSeverity.Warning);
                return;
            }
            // The batch cap: take the FIRST MaxPlayNextDrop tracks of the drop (in drop order), still front-inserted
            // last-first so the kept prefix plays in the order it was dropped.
            int cap = PlayerBarRules.DropInsertCount(tracks.Length);
            int n = 0;
            for (int i = cap - 1; i >= 0; i--)
            {
                if (!tracks[i].IsValid) continue;
                playNext(tracks[i].Uri);
                n++;
            }
            if (n == 0) return;
            bool truncated = PlayerBarRules.DropWasTruncated(tracks.Length);
            Notify.Say(truncated
                    ? Strings.Detail.AddedFirstToQueue(Strings.Detail.SongCount(n))
                    : Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)),
                InfoBarSeverity.Success);
        });
    }

    // ══ 3. THE ARTISTS LINE ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Per-name links (a track's credits) or ONE link (an episode's show). Compact (marquee off): the first
    /// name, ellipsised, plus the "+N" chip when there is more than one.</summary>
    sealed class BarArtistsLine(bool compact) : Component
    {
        readonly bool _compact = compact;

        public override Element Render()
        {
            _ = UseComputed<long>(ArtistLineStamp).Value;
            var r = Playback.Current.Value;
            if (r.IsNone) return new BoxEl { Direction = 0 };

            if (r.Kind == EntityKind.Episode)
            {
                var e = new Episode(r.Slot);
                if (!e.IsValid || !e.Show.IsValid) return new BoxEl { Direction = 0 };
                return new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children = [MetaLink(e.Show.Title, For(e.Show.Uri, e.Show.Title), _compact, "show:" + e.Show.Slot)],
                };
            }

            var t = new Track(r.Slot);
            // A PARTIAL credit list must not render: the line waits for the whole group, and a playable with no
            // credits renders an EMPTY row that still spends the column's 2-DIP gap (W20).
            if (!t.IsValid || !t.Knows(TrackFields.Artists)) return new BoxEl { Direction = 0 };
            var slots = t.ArtistSlots.ToArray();
            if (slots.Length == 0) return new BoxEl { Direction = 0 };

            if (_compact && slots.Length > 1)
            {
                var first = new Artist(slots[0]);
                return new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Gap = Spacing.XS,
                    Children =
                    [
                        MetaLink(first.Name, first.Uri.IsValid ? For(first.Uri, first.Name) : Route.None, true, "artist:" + slots[0]),
                        Embed.Comp(() => new BarArtistOverflowChip(slots)) with { Key = "npmore:" + slots[0] + ":" + slots.Length },
                    ],
                };
            }

            var kids = new List<Element>(slots.Length * 2);
            for (int i = 0; i < slots.Length; i++)
            {
                var a = new Artist(slots[i]);
                string name = a.Name;
                if (name.Length == 0) continue;
                if (kids.Count > 0) kids.Add(new TextEl(", ") { Size = 12f, Color = Tok.TextSecondary });
                // A name with no route is INERT, not styled-and-dead.
                kids.Add(MetaLink(name, a.Uri.IsValid ? For(a.Uri, name) : Route.None, false, "artist:" + slots[i]));
            }
            return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids.ToArray() };
        }

        static Element MetaLink(string text, Route route, bool trim, string key)
            => Embed.Comp(() => new BarMetaLink(text, route, trim)) with { Key = key + ":" + text };

        /// <summary>The track row's version folded with its credited artists' versions — re-render only when THESE
        /// rows change.</summary>
        static long ArtistLineStamp()
        {
            var r = Playback.Current.Value;
            if (r.IsNone) return 0L;
            if (r.Kind == EntityKind.Episode)
            {
                _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
                _ = Entities.Current.Episodes.Changed.Value;
                _ = Entities.Current.Shows.Changed.Value;
                var e = new Episode(r.Slot);
                return e.IsValid ? ((long)r.Slot << 32) ^ e.Version ^ (e.Show.IsValid ? (long)e.Show.Version << 16 : 0L) : 0L;
            }
            _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
            _ = Entities.Current.Tracks.Changed.Value;
            _ = Entities.Current.Artists.Changed.Value;
            var t = new Track(r.Slot);
            if (!t.IsValid) return 0L;
            long h = ((long)r.Slot << 32) ^ t.Version;
            var slots = t.ArtistSlots;
            for (int i = 0; i < slots.Length; i++) h = h * 31 + new Artist(slots[i]).Version + slots[i];
            return h;
        }
    }

    /// <summary>A clickable now-playing meta link. It drives its OWN foreground: <c>TextEl.HoverColor</c> follows the
    /// ancestor hover path, which would light every link while the pointer is anywhere on the meta column.</summary>
    sealed class BarMetaLink(string text, Route route, bool trim) : Component
    {
        readonly string _text = text;
        readonly Route _route = route;
        readonly bool _trim = trim;

        public override Element Render()
        {
            var hover = UseSignal(false);
            bool enabled = !_route.IsNone;
            var route = _route;
            return new BoxEl
            {
                Cursor = enabled ? CursorId.Hand : null,
                OnClick = enabled ? () => GoTo(route) : null,
                OnHoverMove = enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null,
                OnPointerExit = enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
                ClipToBounds = true, Shrink = 1f, MinWidth = _trim ? 0f : float.NaN,
                Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text,
                Children =
                [
                    new TextEl(_text)
                    {
                        Size = 12f, Color = hover.Value ? Tok.TextPrimary : Tok.TextSecondary,
                        MaxLines = _trim ? 1 : 0, Trim = _trim ? TextTrim.CharacterEllipsis : TextTrim.None,
                        MinWidth = _trim ? 0f : float.NaN,
                    },
                ],
            };
        }
    }

    /// <summary>The "+N" credits chip. Opens EVERY credited artist UPWARD from the dock (0.2.9 asked for
    /// BottomEdgeAlignedLeft from a bottom dock and relied on the overlay re-fit; ch 20 W20 asks 0.3 to anchor it up).
    /// Keyed by its artist set at the call site, so the frozen slot array is the right one.</summary>
    sealed class BarArtistOverflowChip(int[] slots) : Component
    {
        readonly int[] _slots = slots;

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var slots = _slots;

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = new MenuFlyoutItem[slots.Length];
                for (int i = 0; i < slots.Length; i++)
                {
                    var a = new Artist(slots[i]);
                    var route = a.Uri.IsValid ? For(a.Uri, a.Name) : Route.None;
                    items[i] = new MenuFlyoutItem(a.Name, default, !route.IsNone, () => BarGo(route));
                }
                handle.Value = overlay.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                    FlyoutPlacement.TopEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Shrink = 0f, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f),
                OnRealized = h => anchor.Value = h,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = Toggle, BlocksDragArm = true,
                Children = [FluentGpu.Dsl.Ui.Caption("+" + (slots.Length - 1)) with { Weight = 600, Color = Tok.TextTertiary }],
            };
        }
    }

    // ══ 4. THE BUTTONS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A transport TOGGLE / command glyph. The on-state is WinUI's AppBarToggleButton checked visual — an
    /// accent glyph over <c>FillSubtleSecondary</c>, cross-faded over 83 ms — and the resting off-state is completely
    /// unpainted (a row of resting plates would re-plate the dock). Flat 32/16 at every tier.</summary>
    static BoxEl BarButton(string glyphText, Action onClick, bool enabled, bool latched, float box, float glyphSize,
        Action<NodeHandle>? onRealized = null, string? font = null, float boxHeight = float.NaN)
    {
        ColorF ink = !enabled ? Tok.TextDisabled : latched ? Tok.AccentDefault : Tok.TextSecondary;
        ColorF inkHover = !enabled ? Tok.TextDisabled : latched ? Tok.AccentDefault : Tok.TextPrimary;
        ColorF plate = latched && enabled ? Tok.FillSubtleSecondary : ColorF.Transparent;
        ColorF plateHover = latched && enabled ? Tok.FillSubtleTertiary : ColorF.Transparent;
        return new BoxEl
        {
            Width = box, Height = float.IsNaN(boxHeight) ? box : boxHeight,
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            Fill = plate, HoverFill = plateHover, PressedFill = plate,
            BrushTransitionMs = Design.Motion.Faster,
            HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
            // Clicking never moves focus, so the shell's Space shortcut keeps working after a click.
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            OnRealized = onRealized,
            IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : null,
            Children = [new TextEl(glyphText) { Size = glyphSize, FontFamily = font ?? FluentGpu.Dsl.Theme.IconFont, Color = ink, HoverColor = inkHover }],
        };
    }

    /// <summary>A RESERVED slot in the bar's row: a fixed <paramref name="w"/>×<paramref name="h"/> box the tier owns,
    /// whose <paramref name="face"/> is either lit (opaque, hit-testable) or dark (transparent, inert) — cross-faded over
    /// the control token, never entered/exited, so a face lighting up moves NOTHING around it. The face's own
    /// <c>IsEnabled</c> should follow <paramref name="lit"/> too, so a dark face is not a focus stop.</summary>
    static BoxEl Slot(string key, float w, float h, bool lit, Element face) => new()
    {
        Key = key, Width = w, Height = h, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Opacity = lit ? 1f : 0f, HitTestVisible = lit,
        // Opacity-only layout transition: a face lights or dims over the control-normal beat with NO bounds channel,
        // so the slot's rect (and every neighbour's) never moves. Element.Transition alone would snap here — the
        // reconciler routes it only through Enter/Exit synthesis, not a live static Opacity change.
        Animate = BarFaceMotion,
        Children = [face],
    };

    /// <summary>The face fade of a reserved slot (<see cref="Slot(string, float, float, bool, Element)"/>): opacity only,
    /// never Bounds/Reflow, so lighting a button cannot reflow the row.</summary>
    static readonly LayoutTransition BarFaceMotion = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut), SizeMode.Auto);

    /// <summary>The primary play/pause: NO plate (the XML doc of 0.2.9 said "a filled accent circle"; the code painted
    /// none, and code wins) — primary ink, pressed secondary, scale-only hover/press.</summary>
    static BoxEl BarPrimaryButton(string glyphText, PrimaryVerb verb, float box, float glyphSize)
    {
        bool enabled = verb != PrimaryVerb.None;
        return new BoxEl
        {
            Width = box, Height = box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
            OnClick = verb == PrimaryVerb.Retry ? static () => RetryPlayback() : static () => TogglePlayPause(),
            Children =
            [
                new TextEl(glyphText)
                {
                    Size = glyphSize, FontFamily = FluentGpu.Dsl.Theme.IconFont,
                    Color = enabled ? Tok.TextPrimary : Tok.TextDisabled, HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary,
                },
            ],
        };
    }

    // ── the "⋯" overflow ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The overflow button's FACE, remounted by a Key over its COMMAND SET (the slot around it is the tier's —
    /// <see cref="Slot(string, float, float, bool, Element)"/> — so a command set changing with playback swaps the face in
    /// place). Rows are built at open
    /// time from live reads, so a latched toggle's check mark is fresh on every open without the key having to encode it.</summary>
    static Element BarMoreButton(OverflowCommand[] commands, float box, float glyphSize)
    {
        int key = commands.Length;
        for (int i = 0; i < commands.Length; i++) key = key * 11 + (int)commands[i] + 1;
        return Embed.Comp(() => new BarMoreMenu(commands, box, glyphSize)) with { Key = "more#" + key };
    }

    sealed class BarMoreMenu(OverflowCommand[] commands, float box, float glyphSize) : Component
    {
        readonly OverflowCommand[] _commands = commands;
        readonly float _box = box, _glyph = glyphSize;

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var commands = _commands;

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                if (commands.Length == 0) return;
                var items = new List<MenuFlyoutItem>(commands.Length);
                for (int i = 0; i < commands.Length; i++) items.Add(OverflowItem(commands[i]));
                // A PLAIN MenuFlyout upward — never CommandBarFlyout, whose second clip fought the reveal.
                handle.Value = overlay.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                    FlyoutPlacement.TopEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Width = _box, Height = _box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HoverScale = Design.Motion.ScaleEmphatic.Hover, PressScale = Design.Motion.ScaleEmphatic.Press,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                OnClick = Toggle, Cursor = CursorId.Hand, OnRealized = h => anchor.Value = h,
                Children = [new TextEl(Icons.More) { Size = _glyph, FontFamily = FluentGpu.Dsl.Theme.IconFont, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
            };
        }

        /// <summary>One overflow row, from LIVE peeks at open time.</summary>
        static MenuFlyoutItem OverflowItem(OverflowCommand command)
        {
            var facts = PlayerBarRules.Fold(!Playback.CurrentId.Peek().IsEmpty, Playback.Error.Peek(), Playback.PhaseSignal.Peek(),
                Playback.Buffering.Peek(), Playback.Recovery.Peek(), Playback.PrevAllowedByContext.Peek(), Playback.NextAllowedByContext.Peek());
            bool railOpen = Ui.RailOpen.Peek();
            var mode = Ui.Mode.Peek();
            switch (command)
            {
                case OverflowCommand.Previous:
                    return new MenuFlyoutItem(Loc.Get(Strings.Player.Previous), Icons.Previous, facts.PrevEnabled, static () => Playback.Previous());
                case OverflowCommand.Next:
                    return new MenuFlyoutItem(Loc.Get(Strings.Player.Next), Icons.Next, facts.NextEnabled, static () => Playback.Next());
                case OverflowCommand.Shuffle:
                    return MenuFlyoutItem.Toggle(Loc.Get(Strings.Player.Shuffle), Playback.Shuffle.Peek(), static () => ToggleShuffle(),
                        Icons.Shuffle, facts.CanTransport);
                case OverflowCommand.Repeat:
                {
                    var repeat = Playback.Repeat.Peek();
                    return MenuFlyoutItem.Toggle(Loc.Get(Strings.Player.Repeat), repeat != RepeatMode.Off, static () => CycleRepeat(),
                        PlayerBarRules.RepeatGlyph(repeat), facts.CanTransport);
                }
                case OverflowCommand.Lyrics:
                    return MenuFlyoutItem.Toggle(Loc.Get(Playback.CurrentId.Peek().Kind == EntityKind.Episode ? "podcast.reader.transcript" : Strings.Player.Lyrics), railOpen && mode == RailMode.Lyrics,
                        static () => Ui.Toggle(RailMode.Lyrics), new IconRef { Glyph = WaveeIcons.Lyrics, Font = WaveeIcons.Font });
                case OverflowCommand.Queue:
                    return new MenuFlyoutItem(Loc.Get(Strings.Player.Queue), Icons.Queue, true, static () => Ui.Toggle(RailMode.Queue));
                case OverflowCommand.NowPlaying:
                    return new MenuFlyoutItem(Loc.Get(Strings.Player.NowPlaying), Icons.ChevronUp, true, static () => Ui.Toggle(RailMode.NowPlaying));
                case OverflowCommand.Video:
                {
                    bool live = Video.PlacementCore.IsActive(Video.State.Surface.Peek());
                    // A cascading sub-menu over the SAME placement ladder the inline chevron opens.
                    return MenuFlyoutItem.SubMenu(Loc.Get(live ? Strings.Player.SwitchToAudio : Strings.Player.SwitchToVideo),
                        Video.PlacementMenu(includeFullscreen: true), Icons.Movie);
                }
                default:
                {
                    bool muted = PlayerBarRules.ShowsMuteGlyph(Playback.Audio.Muted.Peek(), Playback.Volume.Peek());
                    return new MenuFlyoutItem(Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute),
                        muted ? Icons.Volume : Icons.Mute, true, static () => ToggleMute());
                }
            }
        }
    }

    // ══ 5. VOLUME ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The volume glyph. Inline form: a click MUTES. Popup form (Medium/Comfortable): a click opens the vertical
    /// rail above it. Remounted by its Key when the form changes. It re-renders only when the MUTE GLYPH flips — a
    /// volume drag moves the compositor rail, not this.</summary>
    sealed class BarVolumeButton(bool popup, float box, float glyphSize) : Component
    {
        readonly bool _popup = popup;
        readonly float _box = box, _glyph = glyphSize;

        public override Element Render()
        {
            bool muted = UseComputed(static () => PlayerBarRules.ShowsMuteGlyph(Playback.Audio.Muted.Value, Playback.Volume.Value)).Value;
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            bool popupForm = _popup;

            void Click()
            {
                if (!popupForm) { ToggleMute(); return; }
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                handle.Value = overlay.Open(
                    () => anchor.Value,
                    static () => new BoxEl
                    {
                        Width = 52f, Height = 168f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Padding = new Edges4(10f, 14f, 10f, 14f),
                        Children =
                        [
                            Slider.Create(s_barVolume, static v => Playback.SetVolume(v),
                                new Slider.SliderOptions { Vertical = true, IsThumbToolTipEnabled = false },
                                length: 124f, thickness: 32f),
                        ],
                    },
                    FlyoutPlacement.TopCenter,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                        { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return BarButton(muted ? Icons.Mute : Icons.Volume, Click, true, false, _box, _glyph, h => anchor.Value = h);
        }
    }

    // ══ 6. DEVICES ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The Devices button. LATCHED only while playback is on ANOTHER device (the same owner-gated verdict the
    /// "Playing on" line reads, so the two cannot disagree). It also opens ITSELF when the model bumps
    /// <see cref="Playback.DevicePickerRequest"/> (a toast's "Choose device") — only for a request that post-dates its
    /// mount, and the bar mounts exactly one of these, so the toast opens exactly one picker.</summary>
    sealed class BarDevicesButton(float box, float glyphSize) : Component
    {
        readonly float _box = box, _glyph = glyphSize;

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            _ = Playback.Devices.Changed.Value;
            bool latched = DeviceRoster.RemoteSlot(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, Playback.Devices.Rows) >= 0;
            int request = Playback.DevicePickerRequest.Value;
            var lastRequest = UseRef(request);

            void Toggle() => BarToggleDevicePicker(overlay, anchor, handle);

            UseEffect(() =>
            {
                if (request == lastRequest.Value) return;
                lastRequest.Value = request;
                if (handle.Value is not { IsOpen: true }) Toggle();
            }, request);

            return BarButton(Icons.Devices, Toggle, true, latched, _box, _glyph, h => anchor.Value = h);
        }
    }

    /// <summary>One picker, two anchors (the Devices button, the "Playing on" line): upward, right-aligned to the
    /// anchor, light-dismiss, focus trap. The menu caps its own scroll height at <see cref="DeviceRoster.PickerMaxHeight"/>
    /// (window height − 96 DIP, G-210) rather than trusting the engine's own fixed 468-DIP <c>MenuFlyout</c> cap, which
    /// closes 0.2.9's uncapped-roster trap (ch 20 §9, parity 81) even on a short/undocked window.</summary>
    static void BarToggleDevicePicker(IOverlayService overlay, Ref<NodeHandle> anchor, Ref<OverlayHandle?> handle)
    {
        if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
        handle.Value = overlay.Open(
            () => anchor.Value,
            () => Embed.Comp(() => new BarDevicePickerMenu(() => handle.Value?.Close())),
            FlyoutPlacement.TopEdgeAlignedRight,
            // The roster already caps itself to the window (PickerMaxHeight = height - 96), so it never needs to
            // escape the root bounds -- and NOT escaping is what keeps it on the engine's in-window acrylic path
            // instead of leasing a popup window, whose flat plate the owner rejected.
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true });
        handle.Value.ClosedAction = () => handle.Value = null;
    }

    /// <summary>The picker body. It re-renders WHILE OPEN — a transfer landing swaps the checks in place (parity 79) —
    /// and re-keys its menu presenter on every change, because the presenter freezes its rows at mount.</summary>
    sealed class BarDevicePickerMenu(Action close) : Component
    {
        readonly Action _close = close;

        public override Element Render()
        {
            uint roster = Playback.Devices.Changed.Value;
            var owner = Playback.OwnerSignal.Value;
            int activeSlot = Playback.ActiveDeviceSlot.Value;
            var local = Playback.Audio.Devices.Value;
            string? selected = Playback.Audio.SelectedOutputId.Value;
            bool supported = Playback.Audio.Supported.Value;
            float windowH = UseContextSignal(Viewport.Size).Value.Height;

            var connect = Playback.Devices.Rows;
            int remote = DeviceRoster.RemoteSlot(owner, activeSlot, connect);
            string? activeId = (uint)activeSlot < (uint)connect.Length ? connect[activeSlot].Id : null;
            // The verification row: the player's own echo, never a loopback of the setting (user's exact ask).
            var observed = Playback.Audio.PlayingOpened;
            var asked = (Spotify.Audio.Quality)Math.Clamp(Platform.Settings.Get(Platform.Keys.PlaybackQuality),
                Platform.Network.QualityMin, Platform.Network.QualityMax);
            var rows = DevicePickerRows(local, selected, supported, weAreActiveOutput: remote < 0, connect, activeId,
                observed, asked);

            var items = new MenuFlyoutItem[rows.Count];
            for (int i = 0; i < rows.Count; i++) items[i] = MapDeviceRow(rows[i]);

            // G-210: a dozen Connect devices must scroll well before the window edge, not just the engine's own fixed
            // 468-DIP cap — a short/undocked window is smaller than that.
            float maxH = DeviceRoster.PickerMaxHeight(windowH);
            var parts = new TemplateParts();
            parts.Set<ScrollEl>(MenuFlyout.PartScrollViewer, s => s with { MaxHeight = maxH });

            int version = HashCode.Combine(roster, (byte)owner, activeSlot, local.Length, selected, supported, maxH,
                HashCode.Combine(observed.Label, (byte)observed.Format, (byte)asked));
            return new BoxEl
            {
                Direction = 1,
                Children = [MenuFlyout.Create(items, _close, parts: parts) with { Key = "devices:" + version }],
            };
        }

        static MenuFlyoutItem MapDeviceRow(DevicePickerRow row)
        {
            string id = row.DeviceId;
            return row.Kind switch
            {
                DevicePickerRowKind.Separator => MenuFlyoutItem.Separator,
                // A disabled command row stands in as the section header (MenuFlyout has no header kind; neither does WinUI).
                // Quality rides the same disabled-row shape: it is informational, never a radio.
                DevicePickerRowKind.Header or DevicePickerRowKind.Empty or DevicePickerRowKind.Quality
                    => new MenuFlyoutItem(row.Label, default, false),
                DevicePickerRowKind.LocalDefault => MenuFlyoutItem.RadioItem(row.Label, row.IsChecked,
                        row.Enabled ? static () => BarSelectLocalOutput(null) : null, Icons.ThisPc, row.Enabled)
                    with { AcceleratorText = row.Accelerator },
                DevicePickerRowKind.LocalDevice => MenuFlyoutItem.RadioItem(row.Label, row.IsChecked,
                        row.Enabled ? () => BarSelectLocalOutput(id) : null, DeviceRoster.LocalGlyph(row.LocalKind), row.Enabled)
                    with { AcceleratorText = row.Accelerator },
                DevicePickerRowKind.ConnectDevice => MenuFlyoutItem.RadioItem(row.Label, row.IsChecked,
                    () => BarTransferToDevice(id), DeviceRoster.ConnectGlyph(row.ConnectKind)),
                _ => new MenuFlyoutItem(row.Label, default, false),
            };
        }
    }

    /// <summary>Route first, THEN pull playback home — so the first local audio lands on the just-chosen endpoint.</summary>
    static void BarSelectLocalOutput(string? deviceId)
    {
        Playback.Audio.Select(deviceId);
        var rows = Playback.Devices.Rows;
        if (DeviceRoster.RemoteSlot(Playback.OwnerSignal.Peek(), Playback.ActiveDeviceSlot.Peek(), rows) < 0) return;
        int home = DeviceRoster.ThisDeviceSlot(rows);
        if (home >= 0) Playback.TransferTo(home);
    }

    static void BarTransferToDevice(string deviceId)
    {
        int slot = DeviceRoster.SlotOfId(Playback.Devices.Rows, deviceId);
        if (slot >= 0) Playback.TransferTo(slot);
    }

    /// <summary>"🖳 Playing on &lt;device&gt;" — 13 DIP, accent @ 88 % → full accent on hover, above the title. A second
    /// anchor for the SAME picker.</summary>
    sealed class BarRemoteDeviceLine : Component
    {
        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            _ = Playback.Devices.Changed.Value;
            var rows = Playback.Devices.Rows;
            int slot = DeviceRoster.RemoteSlot(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, rows);
            if (slot < 0) return new BoxEl { Height = 0f, HitTestVisible = false };
            string name = rows[slot].Name ?? "";

            ColorF accent = Tok.AccentDefault;
            ColorF rest = accent with { A = 0.88f };
            return new BoxEl
            {
                Height = 13f, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                MinWidth = 0f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                OnClick = () => BarToggleDevicePicker(overlay, anchor, handle), OnRealized = h => anchor.Value = h,
                Children =
                [
                    new TextEl(Icons.Devices) { Size = 12f, FontFamily = FluentGpu.Dsl.Theme.IconFont, Color = rest, HoverColor = accent },
                    new BoxEl
                    {
                        Shrink = 1f, MinWidth = 0f, ClipToBounds = true,
                        Children =
                        [
                            new TextEl(Strings.Player.PlayingOn(name))
                            {
                                Size = 12f, LineHeight = 16f, Weight = 600, Color = rest, HoverColor = accent,
                                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                            },
                        ],
                    },
                ],
            };
        }
    }

    // ══ 7. THE SEEK BAR ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The bespoke media rail (not a Slider): the SCRUB GATE, the smooth extrapolated playhead and
    /// click-anywhere scrub committing ONE seek per gesture. The fill and thumb are compositor binds on
    /// <see cref="_displayFrac"/>; the component re-renders only on enablement / mode / advancing edges.</summary>
    sealed class BarSeekRail : Component
    {
        const float HitHeight = 32f;         // WinUI SliderHorizontalHeight
        const float RingDiameter = 22f;      // Slider.DefaultStyle.ThumbRingDiameter

        readonly Signal<bool> _scrubbing = new(false);
        readonly FloatSignal _scrubFrac = new(0f);
        readonly FloatSignal _displayFrac = new(0f);
        readonly FloatSignal _railPx = new(0f);
        readonly FloatSignal _chapterHover = new(0f);
        readonly Signal<bool> _chapterHovering = new(false);
        long _committedAtMs;

        // Wired ONCE: a bind thunk and the handlers are fields, so a re-render allocates none of them.
        readonly Prop<Affine2D> _fillBind;
        readonly Prop<Affine2D> _thumbBind;
        readonly Action _recompute, _onReport, _onCommit, _onCancel;
        readonly Action<Point2> _onDown, _onDrag, _onHover;
        readonly Action _onExit;
        readonly Action<RectF> _onBounds;

        public BarSeekRail()
        {
            _fillBind = Prop.Of(() => Affine2D.Scale(MathF.Max(Math.Clamp(_displayFrac.Value, 0f, 1f), 1e-4f), 1f));
            _thumbBind = Prop.Of(() => Affine2D.Translation(SeekRail.ThumbX(_railPx.Value, _displayFrac.Value, RingDiameter), 0f));
            _recompute = Recompute;
            _onReport = OnReport;
            _onCommit = OnCommit;
            _onCancel = OnCancel;
            _onDown = OnDown;
            _onDrag = OnDragMove;
            _onHover = local => { _chapterHovering.Value = true; _chapterHover.SetIfChanged(SeekRail.FractionAt(local.X, _railPx.Peek())); };
            _onExit = () => _chapterHovering.Value = false;
            _onBounds = b => { if (b.W > 0f && MathF.Abs(b.W - _railPx.Peek()) > 0.5f) { _railPx.Value = b.W; Recompute(); } };
        }

        public override Element Render()
        {
            bool hasCurrent = !Playback.CurrentId.Value.IsEmpty;
            var error = Playback.Error.Value;
            var phase = Playback.PhaseSignal.Value;
            bool enabled = SeekRail.Enabled(hasCurrent, error, phase, Playback.CanSeek.Value);
            bool advances = SeekRail.Advances(hasCurrent, error, phase, Playback.Buffering.Value);
            // The three-way SHAPE through a memo: a live window republishing several times a second re-renders nothing.
            var mode = UseComputed(static () => SeekRail.ModeOf(Playback.Live.Value)).Value;
            float dwell = UseComputed(() => SeekRail.DwellMs(
                SeekRail.SpanMs(Playback.Live.Value, Playback.DurationMs.Value), _railPx.Value)).Value;

            // A report (position, duration, window, edge, phase) re-derives the RESTING fraction — an effect, not Render.
            UseSignalEffect(_onReport);
            // The pixel-due stepper: armed only while the playhead advances on its own (paused ⇒ disarmed ⇒ no frames).
            // UseInterval also pauses under a parked / minimised window.
            UseInterval(_recompute, dwell, enabled: advances && mode != SeekRailMode.Line);

            // NOTHING TO REWIND: the rail stops pretending to be one. After every hook, so the hook order never varies.
            if (mode == SeekRailMode.Line) return Embed.Comp(static () => new BarLiveLine());

            var chapterId = Playback.CurrentId.Value;
            uint chapterEpoch = Entities.ScopeEpoch.Value;
            bool chapterRail = chapterId.Kind == EntityKind.Episode && mode == SeekRailMode.Track;
            var chapterIdentity = new ChapterResources.Identity(chapterEpoch, Entities.Current.Key.Account, chapterId);
            Element chapters = chapterRail
                ? Embed.Comp(() => new BarChapterTimeline(chapterIdentity, _railPx, _chapterHover, _scrubFrac, _chapterHovering, _scrubbing))
                    with { Key = "player-chapters:" + chapterEpoch + ":" + chapterId.Text }
                : new BoxEl { HitTestVisible = false };
            var s = Slider.DefaultStyle;
            var fill = new BoxEl
            {
                Grow = 1f, Height = s.TrackHeight, AlignSelf = FlexAlign.Center,
                Fill = enabled ? s.ValueFill : s.ValueFillDisabled,
                HoverFill = enabled ? s.ValueFillPointerOver : s.ValueFillDisabled,
                PressedFill = enabled ? s.ValueFillPressed : s.ValueFillDisabled,
                // Square: a scaled rounded rect shows cap slivers at the value edge.
                Corners = CornerRadius4.All(0f), HitTestVisible = false,
                TransformOriginX = 0f, Transform = _fillBind,
            };
            // The DVR rail's right end IS the live edge, so it is marked.
            Element[] railKids = mode == SeekRailMode.Dvr
                ? new Element[]
                {
                    fill,
                    new BoxEl
                    {
                        Grow = 1f, Height = s.TrackHeight, Direction = 0, Justify = FlexJustify.End, HitTestVisible = false,
                        Children = [new BoxEl { Width = 2f, Height = s.TrackHeight, Shrink = 0f, Fill = Tok.AccentDefault, HitTestVisible = false }],
                    },
                }
                : new Element[] { fill };

            float rest = enabled ? s.InnerRestScale : s.InnerDisabledScale;
            var stack = new BoxEl
            {
                // Shrink + MinWidth 0 on every layer: the engine's flex items do not shrink by default, and a ZStack can
                // measure its available width as its own size, so without them the rail keeps that width and paints under the
                // right cluster to the window edge.
                ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = HitHeight, AlignItems = FlexAlign.Center, HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        Height = s.TrackHeight, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignSelf = FlexAlign.Center,
                        Fill = enabled ? s.RailFill : s.RailFillDisabled,
                        Corners = CornerRadius4.All(s.TrackCornerRadius), ClipToBounds = true, ZStack = true,
                        HitTestVisible = false, Children = railKids,
                    },
                    chapters,
                    new BoxEl
                    {
                        Width = RingDiameter, Height = RingDiameter,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = CornerRadius4.All(s.ThumbCornerRadius),
                        Fill = s.ThumbRing, HoverFill = s.ThumbRing, PressedFill = s.ThumbRing,
                        BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
                        Opacity = 0f, HoverOpacity = enabled ? 1f : 0f, PressedOpacity = enabled ? 1f : 0f,
                        HitTestVisible = false, Transform = _thumbBind,
                        Children =
                        [
                            new BoxEl
                            {
                                Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter,
                                Corners = CornerRadius4.All(s.InnerThumbDiameter * 0.5f),
                                Fill = enabled ? s.ThumbFill : s.ThumbFillDisabled,
                                HoverFill = enabled ? s.ThumbFillPointerOver : s.ThumbFillDisabled,
                                PressedFill = enabled ? s.ThumbFillPressed : s.ThumbFillDisabled,
                                ScaleX = rest, ScaleY = rest,
                                HoverScale = enabled ? s.InnerHoverScale / s.InnerRestScale : 1f,
                                PressScale = enabled ? s.InnerPressScale / s.InnerRestScale : 1f,
                                HoverDurationMs = Design.Motion.Standard, PressDurationMs = Design.Motion.Standard,
                                HitTestVisible = false,
                            },
                        ],
                    },
                ],
            };

            // Click-anywhere + drag scrub; OnClick is the drag-END commit edge. Chapter decoration never owns input.
            return new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = HitHeight, Direction = 0, AlignItems = FlexAlign.Center,
                Role = AutomationRole.Slider, IsEnabled = enabled,
                Cursor = enabled ? CursorId.Hand : null,
                OnBoundsChanged = _onBounds,
                OnPointerDown = enabled ? _onDown : null,
                OnDrag = enabled ? _onDrag : null,
                OnClick = enabled ? _onCommit : null,
                OnDragCanceled = enabled ? _onCancel : null,
                OnHoverMove = chapterRail && enabled ? _onHover : null,
                OnPointerExit = chapterRail ? _onExit : null,
                Children = [stack],
            };
        }

        void OnReport()
        {
            _ = Playback.PositionMs.Value;
            _ = Playback.DurationMs.Value;
            _ = Playback.Live.Value;
            _ = Playback.IsBehindLive.Value;
            _ = Playback.PhaseSignal.Value;
            if (_committedAtMs > 0L && !_scrubbing.Peek()) _committedAtMs = 0L;   // the landing arrived
            Recompute();
        }

        /// <summary>Re-derive the ONE value the binds read. Peeks only — it runs from the ticker and from effects.</summary>
        void Recompute()
        {
            var live = Playback.Live.Peek();
            var mode = SeekRail.ModeOf(live);
            long now = Playback.FrameNowMs();
            bool holding = SeekRail.HoldsDrop(_committedAtMs, now);
            float model = 0f;
            if (!_scrubbing.Peek() && !holding)
            {
                bool advancing = SeekRail.Advances(!Playback.CurrentId.Peek().IsEmpty, Playback.Error.Peek(),
                    Playback.PhaseSignal.Peek(), Playback.Buffering.Peek());
                // THE CLOCK: the model's timestamped sample, extrapolated on the clock it was stamped with.
                long position = advancing ? Playback.Snap().Position(now)
                    : mode == SeekRailMode.Dvr ? live.PositionMs : Playback.PositionMs.Peek();
                model = SeekRail.ModelFraction(mode, position, Playback.DurationMs.Peek(), live, Playback.IsBehindLive.Peek());
            }
            float shown = SeekRail.Displayed(_scrubbing.Peek() || holding, _scrubFrac.Peek(), model);
            _displayFrac.SetIfChanged(SeekRail.Quantize(shown, _railPx.Peek()));   // an unmoved pixel writes nothing
        }

        static bool EnabledNow() => SeekRail.Enabled(!Playback.CurrentId.Peek().IsEmpty, Playback.Error.Peek(),
            Playback.PhaseSignal.Peek(), Playback.CanSeek.Peek());

        void OnDown(Point2 local)
        {
            if (!EnabledNow()) return;
            _scrubbing.Value = true;
            _scrubFrac.Value = SeekRail.FractionAt(local.X, _railPx.Peek());   // jump to the press point
            Recompute();
        }

        void OnDragMove(Point2 local)
        {
            if (!EnabledNow()) return;
            _scrubFrac.Value = SeekRail.FractionAt(local.X, _railPx.Peek());
            Recompute();
        }

        void OnCommit()
        {
            // Always release the gate — bailing with it set would freeze the fill at the abandoned finger position.
            if (!EnabledNow()) { OnCancel(); return; }
            var live = Playback.Live.Peek();
            long duration = Playback.DurationMs.Peek();
            if (!SeekRail.CanCommit(live, duration)) { OnCancel(); return; }
            long target = SeekRail.CommitTargetMs(_scrubFrac.Peek(), duration, live);
            s_barSeek.Reset();
            Playback.SeekTo((int)Math.Clamp(target, 0L, int.MaxValue));
            _committedAtMs = Math.Max(1L, Playback.FrameNowMs());
            _scrubbing.Value = false;
            Recompute();
        }

        void OnCancel()
        {
            _scrubbing.Value = false;
            _committedAtMs = 0L;
            Recompute();
        }
    }

    /// <summary>The rail for a broadcast with NOTHING TO REWIND: a 2-DIP accent line that breathes 0.55 ↔ 1 over 3 s —
    /// only while playing, the window is active and motion is not reduced; otherwise the loop is REPLACED IN PLACE by a
    /// finite flat track so the frame loop quiesces. Not a slider: no hit test, no role.</summary>
    sealed class BarLiveLine : Component
    {
        static readonly Keyframe[] Breathe = [new(0f, 0.55f), new(0.5f, 1f), new(1f, 0.55f)];
        static readonly Keyframe[] Flat = [new(0f, 1f), new(1f, 1f)];

        public override Element Render()
        {
            bool playing = Playback.IsPlaying.Value;
            bool windowActive = UseIsActive().Value;
            bool breathe = playing && windowActive && !Design.Reduced;   // reduced motion is a VALUE, never a hook branch
            UseKeyframes(AnimChannel.Opacity, breathe ? Breathe : Flat, breathe ? 3000f : 1f, breathe, DepKey.From(breathe));

            // Shrink on both, like BarSeekRail (G-254): a grown line keeps its last arranged width across a narrowing
            // resize unless it may shrink, and pushes the right cluster off the bar.
            return new BoxEl
            {
                Grow = 1f, Shrink = 1f, Height = 32f, Direction = 0, AlignItems = FlexAlign.Center,
                HitTestVisible = false, Role = AutomationRole.None,
                Children =
                [
                    new BoxEl
                    {
                        Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = 2f, Corners = CornerRadius4.All(1f),
                        Fill = Tok.AccentDefault, HitTestVisible = false,
                    },
                ],
            };
        }
    }

    // ══ 8. THE TIME LABELS ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Formatted once per whole second and cached: the labels tick at 1 Hz forever.</summary>
    static readonly FormatCache<long> s_clockText = new();
    static readonly FormatCache<long> s_minusClockText = new();
    static readonly FormatCache<long> s_goLiveText = new();

    /// <summary>One time label. The digits are a BOUND text channel — reading the position in Render would rebuild
    /// the whole label at tick rate (0.2.9's steady-churn census finding). The structural reads (live or not) go
    /// through a memo; the show-remaining preference is read INSIDE the bind.</summary>
    sealed class BarTimeText : Component
    {
        readonly bool _remaining;
        readonly ColorF? _ink;
        readonly Prop<string> _label;

        public BarTimeText(bool remaining, ColorF? ink)
        {
            _remaining = remaining;
            _ink = ink;
            _label = Prop.Of<string>(Label);
        }

        string Label()
        {
            long position = Playback.PositionMs.Value;
            long duration = Playback.DurationMs.Value;
            bool isLive = Playback.Live.Value.IsLive;
            long tunedIn = Playback.TunedInAtMs.Value;
            var (ms, minus) = TimeLabel.Of(_remaining, Prefs.PlayerBar.ShowRemaining(), position, duration, isLive, tunedIn,
                isLive ? Playback.UnixNowMs() : 0L);
            long seconds = ms / 1000L;
            return minus
                ? s_minusClockText.Get(seconds, static sec => "-" + Playback.TimeFormat.Clock(sec * 1000L))
                : s_clockText.Get(seconds, static sec => Playback.TimeFormat.Clock(sec * 1000L));
        }

        public override Element Render()
        {
            bool isLive = UseComputed(static () => Playback.Live.Value.IsLive).Value;
            if (TimeLabel.RightSlotIsLive(_remaining, isLive))
            {
                var ink = _ink;
                return Embed.Comp(() => new BarLiveSlot(ink));
            }

            bool right = _remaining;
            var caption = FluentGpu.Dsl.Ui.Caption("");
            return new BoxEl
            {
                // The slot is a reservation at rest (0:09 → 0:10 moves nothing) and a FLOOR while live (h:mm:ss).
                Width = isLive ? float.NaN : TimeLabel.SlotW, MinWidth = TimeLabel.SlotW, Shrink = 0f,
                Direction = 0, AlignItems = FlexAlign.Center,
                Justify = right ? FlexJustify.Start : FlexJustify.End,   // the digits hug the rail
                HoverFill = right ? (_ink is null ? Tok.FillSubtleSecondary : Design.OnMedia.GlassHover) : ColorF.Transparent,
                PressedFill = right ? (_ink is null ? Tok.FillSubtleTertiary : Design.OnMedia.GlassPressed) : ColorF.Transparent,
                Corners = Radii.ControlAll,
                OnClick = right ? static () => Prefs.PlayerBar.ToggleRemaining() : null,
                Cursor = right ? CursorId.Hand : null,
                Children =
                [
                    _ink is { } inkColor
                        ? caption with { Text = _label, Color = inkColor, Wrap = TextWrap.NoWrap }
                        : caption with { Text = _label, Wrap = TextWrap.NoWrap },
                ],
            };
        }
    }

    /// <summary>The right slot while live: ONE 104-DIP reservation, right-aligned, holding the LIVE word-mark or —
    /// behind a rewindable window — the GO LIVE action. The swap moves nothing (parity 65); which of the two is the
    /// model's DECIDED edge state, never a threshold here.</summary>
    sealed class BarLiveSlot(ColorF? ink) : Component
    {
        readonly ColorF? _ink = ink;
        readonly Prop<string> _goLive = Prop.Of(static () => s_goLiveText.Get(Playback.Live.Value.BehindMs / 1000L,
            static sec => Loc.Get(Strings.Play.GoLive) + " " + Strings.Play.Behind(Playback.TimeFormat.Clock(sec * 1000L))));

        public override Element Render()
        {
            bool offer = UseComputed(static () => TimeLabel.OffersGoLive(Playback.Live.Value, Playback.IsBehindLive.Value)).Value;
            ColorF fg = _ink ?? Design.Accent.Decor;
            Element face = offer
                ? new BoxEl
                {
                    // A TEXT button, not a plate — a filled accent here would outrank the transport itself.
                    MinWidth = 44f, Shrink = 0f, Height = 20f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f), Corners = Radii.ControlAll,
                    HoverFill = _ink is null ? Tok.FillSubtleSecondary : Design.OnMedia.GlassHover,
                    PressedFill = _ink is null ? Tok.FillSubtleTertiary : Design.OnMedia.GlassPressed,
                    HoverScale = Design.Motion.ScaleSubtle.Hover, PressScale = Design.Motion.ScaleSubtle.Press,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    OnClick = static () => Playback.GoLive(),
                    Children = [new TextEl(_goLive) { Size = 9f, LineHeight = 12f, Weight = 600, Color = fg, Wrap = TextWrap.NoWrap }],
                }
                : new BoxEl
                {
                    Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Spacing.XXS,
                    Children =
                    [
                        // STATIC red: a blinking dot would keep the frame loop awake for its whole life.
                        new BoxEl { Width = 6f, Height = 6f, Shrink = 0f, Corners = CornerRadius4.All(3f), Fill = Tok.SystemFillCritical },
                        new BoxEl
                        {
                            Height = 14f, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f), Shrink = 0f,
                            Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = fg,
                            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Children = [new TextEl(Loc.Get(Strings.Play.Live)) { Size = 9f, LineHeight = 12f, Weight = 600, Color = fg, Wrap = TextWrap.NoWrap }],
                        },
                    ],
                };

            return new BoxEl
            {
                Width = TimeLabel.LiveSlotW, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                Children = [face],
            };
        }
    }
}
