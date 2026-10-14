// ── Shell/Shell.PlayerBar.Host.cs ──────────────────────────────────────────────────────────────────────────────────────
// the CORE half of the player bar (themes plan 6.1, T6a): the hooks and the decisions the bar's picture is drawn from
//
// Role: CORE ([CoreHost])
// Owner: I (stage B, I2)
// Spec: `Theming\Contracts\PlayerBarProps.cs` (the contract), `Shell.PlayerBar.cs` (the pure rules), `NowPlayingFace.cs` (the hold)
//
// Until T6a the bar was two components that also drew it: `PlayerBarRoot` (the viewport -> level machine) under `PlayerBarContent`
// (every fact, every bookkeeping hook, the element tree). The picture moved to the structure (`Player.Bar`); what stays here is
// everything that DECIDES, in the order it always ran:
//
//   1. the level machine         the viewport width -> the six pressure levels, widening at once, narrowing held through the
//                                policy's dip (Classic 24 DIP), so a resize drag re-renders the bar only at a crossing
//   2. the catalog stamp         the ONE subscription the bar takes on the catalog; its equality cut-off keeps a 300-row
//                                playlist landing elsewhere from re-rendering the bar
//   3. the volume mirror         model -> the slider cell both volume rails bind
//   4. the facts                 the fold, the face hold (`NowPlayingFace.Resolve`), the save state, the right cluster's slots
//   5. the bookkeeping           the layout effects that commit the hold AFTER the render that read it, the saved-episodes demand
//                                and the two deadline timers (the face hold and the sweep's grace)
//
// It runs on the TEMPLATE's hook context (`IPlayerBarHost.Use`), not on a component of its own: the bar is one render over all of
// this and splitting it would move the instant the hold's model is read relative to the effects that follow. The hooks are called
// in the old order (the effects in particular: saved demand, the three observers, the two timers), and the template's own effects
// (the like pop, the face cross-fade) follow them, as they followed them in the single component.
//
// WVTH008: this type builds no element and reads no token or role. The seek rail and the time labels it hands the template as
// builders are core hosts too (`[CoreHost]`, `Shell.PlayerBar.UI.cs`: they mount `Player.SeekRail` / `Player.TimeLabel`); the
// credits line is the template's own picture over the facts `BarCredits` states.

using System.Collections.Generic;

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

using Wavee.Theming.Contracts;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Shell
{
    /// <summary>The bar's core half: see the file header. UI thread only (it runs inside a render).</summary>
    [CoreHost]
    internal sealed class PlayerBarHost : IPlayerBarHost
    {
        /// <summary>The one instance: props stability (a cached object, never a fresh one per render).</summary>
        public static readonly PlayerBarHost Instance = new();

        /// <summary>The ONE wake a load costs. Nothing else re-renders the bar while a playable resolves: the face hold and the top
        /// edge's grace are both DEADLINES, and a deadline that nothing is watching would leave the bar showing the held face (or
        /// no sweep) forever. Two <c>UseTimeout</c>s below bump it; the render reads it through <see cref="BarFaceNow"/>.</summary>
        static readonly Action s_wake = static () => NowPlayingFace.Shared.Bump();

        /// <summary>The props the frame mounts <c>Player.Bar</c> with: the host, the verbs, the live reads and the actions, built once.</summary>
        public static readonly PlayerBarProps Props = new()
        {
            Host = Instance,
            Verbs = PlayerVerbs.Default,
            TitleText = static () => BarTitleText(),
            TitleInk = static () => PlayerBarRules.InkOf(BarStateNow()),
            TitleHasRoute = static () => !BarTitleRoute().IsNone,
            DragPayload = static () => BarDragPayload(),
            DeviceIsRemote = static () =>
            {
                _ = Playback.Devices.Changed.Value;
                return DeviceRoster.RemoteSlot(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, Playback.Devices.Rows) >= 0;
            },
            RemoteDeviceName = static () =>
            {
                _ = Playback.Devices.Changed.Value;
                var rows = Playback.Devices.Rows;
                int slot = DeviceRoster.RemoteSlot(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, rows);
                return slot < 0 ? null : rows[slot].Name ?? "";
            },
            DevicePickerRequest = Playback.DevicePickerRequest,
            Muted = Playback.Muted,
            EpisodeSpeed = Playback.EpisodeSpeed,
            Volume = s_barVolume,
            NavigateTitle = static () => PlayerVerbs.Default.Navigate(BarTitleRoute()),
            NavigateArt = static () => PlayerVerbs.Default.Navigate(BarArtRoute()),
            OpenLyrics = static () => PlayerVerbs.Default.OpenPanel(RailMode.Lyrics),
            OpenQueue = static () => PlayerVerbs.Default.OpenPanel(RailMode.Queue),
            ToggleMute = static () => Playback.ToggleMute(),
            SetVolume = static v => Playback.SetVolume(v),
            OnKey = static (e, focusedContainer) => OnPlayerKey(e, focusedContainer),
            ToggleDevicePicker = BarToggleDevicePicker,
            LikePops = PlayerBarRules.LikePops,
            ArtMorphKey = Stage.Entry.MorphKey,
            VolumeText = PlayerBarRules.VolumePercent,
            RateText = EpisodeRateText,
            SeekBar = static () => SeekBar(),
            TimeText = static remaining => TimeText(remaining),
            CreditsStamp = static () => BarCreditsStamp(),
            Credits = static () => BarCredits(),
            OverflowRow = OverflowItem,
            VideoPlacementMenu = static () => Video.PlacementMenu(includeFullscreen: true),
        };

        /// <inheritdoc />
        public PlayerBarView Use(RenderContext hooks)
        {
            // ── 1. the level machine (was PlayerBarRoot) ──
            var viewport = hooks.UseContextSignal(Viewport.Size);
            float initialWidth = viewport.Peek().Width;
            var levelSignal = hooks.UseSignal(PlayerBarLayout.Initial(initialWidth));
            var initialized = hooks.UseRef(initialWidth > 0f);

            hooks.UseSignalEffect(() =>
            {
                var previous = levelSignal.Peek();
                float width = viewport.Value.Width;
                // The policy is read tracked: a theme's own levels, slots and spacing re-resolve the bar in place.
                var next = PlayerBarLayout.Resolve(width, in previous, initialized.Value, ShellPolicy.Tracked.PlayerBar);
                if (width > 0f) initialized.Value = true;
                if (next.Equals(previous)) return;
                // Always-on and cheap: a band change is a handful of events per session (0.2.9 gated this on an env var).
                if (next.Tier != previous.Tier) Log.Debug("playerbar", "layout band " + previous.Tier + " -> " + next.Tier);
                levelSignal.Value = next;
            });

            // ── 2/3. the catalog stamp and the volume mirror ──
            var rowStamp = hooks.UseComputed<(EntityKind Kind, int Slot, uint Version)>(BarRowStamp);
            hooks.UseSignalEffect(static () => s_barVolume.SetIfChanged(Playback.Volume.Value));

            // ── 4. the facts ──
            var level = levelSignal.Value;
            var policy = ShellPolicy.Tracked.PlayerBar;
            bool isEpisode = Playback.CurrentId.Value.Kind == EntityKind.Episode;
            var L = isEpisode ? PodcastBarLayout(level, policy) : level;
            bool marquee = Prefs.Appearance.Marquee();

            _ = rowStamp.Value;
            var current = Playback.Current.Value;
            bool hasCurrent = !Playback.CurrentId.Value.IsEmpty;
            var phase = Playback.PhaseSignal.Value;
            bool buffering = Playback.Buffering.Value;
            var recovery = Playback.Recovery.Value;
            var facts = SkipRule.Facts(hasCurrent, Playback.Error.Value, phase, buffering, recovery,
                Playback.PrevAllowedByContext.Value, Playback.NextAllowedByContext.Value);
            bool active = facts.Active;
            bool playing = Playback.IsPlaying.Value;
            // ── WHICH IDENTITY THE FACE SHOWS ───────────────────────────────────────────────────────────────────
            // One arm for the whole block (art, title, artists, heart, both routes), so its parts cannot disagree
            // while a skip lands. `BarFaceNow` is the same read the bound channels make, so the marquee's text
            // and this render's art can never be one frame apart.
            var (faceArm, faceRef) = BarFaceNow();
            bool identityShowing = PlayerBarRules.IdentityShowing(faceArm);
            bool holding = faceArm == FaceArm.Hold;
            bool titleKnown = NowPlayingFace.TitleKnown(current);
            bool loadingNow = phase == Playback.Phase.Loading || buffering;
            bool shuffle = Playback.Shuffle.Value;
            var repeat = Playback.Repeat.Value;
            bool ownsTransport = PlayerBarRules.OwnsTransport(Video.State.Transport.Value);
            bool videoLive = Video.PlacementCore.IsActive(Video.State.Surface.Value);
            bool railOpen = Ui.RailOpen.Value;
            var railMode = Ui.Mode.Value;
            bool isTrack = !current.IsNone && current.Kind == EntityKind.Track;
            var track = isTrack ? new Track(current.Slot) : default;
            bool hasVideo = isTrack && track.IsValid && track.HasVideo;
            // The SAVE target is the face's row, not the deck's: while the face is held the heart states the held
            // track's savedness (and is inert — see `likeEnabled`), so it cannot flicker off and back on a skip.
            bool faceIsEpisode = faceRef.Kind == EntityKind.Episode;
            string? playingUri = NowPlayingFace.UriOf(faceRef);
            var episode = faceIsEpisode ? new Episode(faceRef.Slot) : default;
            bool liked = faceIsEpisode ? Spotify.Podcasts.IsSaved(episode)
                : playingUri is not null && Controls.Library is { } library && library.IsSaved(playingUri);
            bool saveReady = !faceIsEpisode || episode.IsValid && Spotify.Podcasts.SavedReady && !Spotify.Podcasts.SavedBusy;
            uint savedEpoch = Entities.ScopeEpoch.Value;
            hooks.UseLayoutEffect(() =>
            {
                if (isEpisode) PlayerVerbs.Default.DemandSaved();   // the saved-episodes read, once, if nobody has asked
            }, DepKey.From((int)savedEpoch, isEpisode ? 1 : 0));
            bool likeLit = PlayerBarRules.LikeFaceVisible(L, facts.State, identityShowing);
            // Lit but INERT while held: the held row is no longer the deck row, and `IPlayerVerbs.ToggleLike` saves the DECK.
            // The slot is reserved either way, so this costs no geometry.
            bool likeEnabled = likeLit && saveReady && !holding;

            // ── 5. the hold's bookkeeping, committed AFTER the render that read it ──────────────────────────────
            // `NowPlayingFace.Shared` (the core model): the shell mounts exactly one dock, this host owns the writes,
            // and the only thing a reader must REACT to — a deadline passing — is `Wake`, which the timers bump.
            var nowPlayingFace = NowPlayingFace.Shared;
            hooks.UseLayoutEffect(() => nowPlayingFace.Observe(current, titleKnown, Playback.ContextUri.Peek(), Playback.FrameNowMs()),
                DepKey.From((int)current.Kind, current.Slot, titleKnown ? 1 : 0, 0));
            hooks.UseLayoutEffect(() => nowPlayingFace.ObserveLoading(loadingNow, Playback.FrameNowMs()),
                DepKey.From(loadingNow ? 1 : 0));
            // The primary's face is the INTENT: a load keeps whatever the answer settled on last.
            hooks.UseLayoutEffect(() => nowPlayingFace.ObserveSettled(facts.State, playing), DepKey.From((int)facts.State, playing ? 1 : 0));
            bool pauseGlyph = PlayerBarRules.ShowsPauseGlyph(facts.State, playing, nowPlayingFace.PlayingBeforeLoad);
            hooks.UseTimeout(s_wake, PlayerBarRules.FaceHoldMs, DepKey.From((int)current.Kind, current.Slot));
            hooks.UseTimeout(s_wake, PlayerBarRules.SweepDelayMs, DepKey.From(loadingNow ? 1 : 0));

            int playingSlot = faceRef.IsNone ? 0 : faceRef.Slot;

            // ── the left block and the right cluster ──
            int remoteSlot = active && L.ShowRemoteDeviceLine
                ? DeviceRoster.RemoteSlot(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, Playback.Devices.Rows)
                : -1;
            if (remoteSlot >= 0) _ = Playback.Devices.Changed.Value;

            bool titleNav = !BarTitleRoute().IsNone;
            bool artNav = !BarArtRoute().IsNone;
            string? artUrl = BarArtUrl(faceRef);

            // Materialised here (a Span cannot cross into a closure); the "⋯" face is keyed over it.
            Span<OverflowCommand> overflow = stackalloc OverflowCommand[PlayerBarRules.MaxOverflow];
            int overflowCount = PlayerBarRules.Overflow(L, ownsTransport, active, hasVideo, overflow);
            OverflowCommand[] overflowCommands = overflow[..overflowCount].ToArray();

            var slots = PlayerBarRules.RightSlots(in L, hasVideo);
            var lit = RightSlot.None;
            for (int bit = 1; bit <= (int)RightSlot.More; bit <<= 1)
            {
                var slot = (RightSlot)bit;
                if ((slots & slot) != 0 && PlayerBarRules.SlotFaceVisible(slot, facts.State, hasVideo)) lit |= slot;
            }
            var widths = L;   // a copy the width function closes over (an `in` parameter cannot be captured)

            // A load that has only just begun shows NO sweep; `NowPlayingFace.ObserveLoading` stamps the window in the layout
            // effect above, so the render that OPENS the window reads 0 here and the wake timer brings the sweep in
            // at SweepDelayMs if the load is still running.
            float msInPhase = nowPlayingFace.MsInLoading(loadingNow, Playback.FrameNowMs());
            bool sweeps = PlayerBarRules.TopEdgeSweeps(phase, buffering, recovery, msInPhase);

            // The podcast step buttons' enablement is read only for an episode that owns the transport (the read subscribes).
            bool seekStepEnabled = isEpisode && ownsTransport && PlaybackScrubSource.Instance.Gate(track: true).Enabled;

            return new PlayerBarView
            {
                Metrics = new PlayerBarMetrics
                {
                    ButtonBox = L.ButtonBox, ButtonGlyph = L.ButtonGlyph, PrimaryBox = L.PrimaryBox, PrimaryGlyph = L.PrimaryGlyph,
                    LeftW = L.LeftW, ArtSize = L.ArtSize, RowGap = L.RowGap, RowPad = L.RowPad, ClusterGap = L.ClusterGap,
                    LeftGap = L.LeftGap, SeekGap = L.SeekGap, RightGap = L.RightGap, TopEdgeWidth = L.TopEdgeWidth,
                    Height = policy.Height,
                    VolumeSliderW = PlayerBarLayout.VolumeSliderW, SplitChevronW = PlayerBarLayout.SplitChevronW,
                    EpisodeSpeedButtonW = EpisodeSpeedButtonWidth, MinButtonBox = PlayerBarLayout.MinButtonBox,
                },
                Episode = isEpisode,
                Marquee = marquee,
                Errored = facts.State == PlayerState.Error,
                CanTransport = facts.CanTransport,
                PrevEnabled = facts.PrevEnabled,
                NextEnabled = facts.NextEnabled,
                Primary = facts.Primary,
                PauseGlyph = pauseGlyph,
                Shuffle = shuffle,
                RepeatOn = repeat != RepeatMode.Off,
                RepeatGlyph = PlayerBarRules.RepeatGlyph(repeat),
                OwnsTransport = ownsTransport,
                SeekStepEnabled = seekStepEnabled,
                TopEdgeSweeps = sweeps,
                ShowTitleBlock = L.ShowTitleBlock,
                ShowLikeSlot = L.ShowLikeSlot,
                ShowArtistLine = PlayerBarRules.ShowsArtistLine(facts.State, L.ShowSubtitle),
                ShowPrevNext = L.ShowPrevNext,
                ShowTimesElapsed = L.ShowTimesElapsed,
                ShowTimesRemaining = L.ShowTimesRemaining,
                VolumePopup = !L.ShowVolumeSlider,
                Face = faceRef,
                ArtUrl = artUrl,
                ArtNav = artNav,
                TitleNav = titleNav,
                Draggable = isTrack,
                FaceIsEpisode = faceIsEpisode,
                Liked = liked,
                LikeLit = likeLit,
                LikeEnabled = likeEnabled,
                RemoteLine = remoteSlot >= 0,
                Slots = slots,
                Lit = lit,
                RightWidth = PlayerBarRules.RightWidth(in L, hasVideo),
                SlotWidth = slot => PlayerBarRules.SlotWidth(slot, in widths),
                Overflow = overflowCommands,
                HasVideo = hasVideo,
                VideoLive = videoLive,
                LyricsOpen = railOpen && railMode == RailMode.Lyrics,
                QueueOpen = railOpen && railMode == RailMode.Queue,
            };
        }

        /// <summary>The bar's key map: Left / Right step the scrub ladder, Up / Down the volume, Space toggles play.
        /// <paramref name="focusedContainer"/> is whether the bar itself holds the focus (the keys are the container's, never a
        /// child's).</summary>
        static void OnPlayerKey(KeyEventArgs e, bool focusedContainer)
        {
            var intent = PlayerKey(e.KeyCode, focusedContainer, e.Handled, e.Ctrl || e.Alt, e.Shift);
            if (intent == PlayerKeyIntent.None) return;
            e.Handled = true;
            switch (intent)
            {
                // The keyboard scrub ladder (D4): 5 s, 15 s, 30 s while held, Shift = 1 s; one seek commits when the key goes quiet.
                case PlayerKeyIntent.SeekBack: ScrubController.KeyStep(-1, fine: false); break;
                case PlayerKeyIntent.SeekForward: ScrubController.KeyStep(1, fine: false); break;
                case PlayerKeyIntent.SeekBackFine: ScrubController.KeyStep(-1, fine: true); break;
                case PlayerKeyIntent.SeekForwardFine: ScrubController.KeyStep(1, fine: true); break;
                case PlayerKeyIntent.VolumeDown: Playback.SetVolume(Math.Clamp(Playback.Volume.Peek() - .05f, 0f, 1f)); break;
                case PlayerKeyIntent.VolumeUp: Playback.SetVolume(Math.Clamp(Playback.Volume.Peek() + .05f, 0f, 1f)); break;
                case PlayerKeyIntent.Toggle: if (!e.IsRepeat) PlayerVerbs.Default.TogglePlay("playerbar.space"); break;
            }
        }
    }
}
