// ── Shell/Shell.PlayerBar.cs ───────────────────────────────────────────────────────────────────────────────────────
// the player bar's SURFACE rules that `Shell.cs` §7 does not carry: the five-way state fold and what it arms, the
// title/ink ladder, the "⋯" overflow composition, the seek rail's arithmetic (mode, scrub-gated display fraction,
// pixel quantisation, dwell, commit target), the time labels, the remote-device verdict and the roster lookups, the
// glyph maps, the volume and repeat rules and the like-pop edge
//
// Role: CORE
// Owner: I (stage B, I2)
// Wave: 4
// Budget: 400 lines (a named partial of Shell.cs's ch 20 +230 share; named on day one of stage B, §8 G4)
// Spec: ch 20 §0, §1.1, §6, §8, §9
//
// Every rule here was an inline expression in 0.2.9's 1,548-line `PlayerBar.cs`, where nothing could pin it. They are
// lifted verbatim, with three deliberate tightenings, each recorded where it lives: the remote-device verdict gates on
// the OWNER (0.3's active slot also names a device that just left), the remaining label never prints "−0:00" for a
// sub-second remainder, and the transport enablement ANDs the model's folded skip restrictions.

using System;
using System.Globalization;

using FluentGpu.Controls;

using DeviceKind = Wavee.Spotify.Decode.DeviceKind;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Shell
{
    // ══ 1. THE BAR'S STATE MACHINE ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the bar is showing. A five-way FOLD, never five <c>if</c>s at the call sites (ch 29: the one
    /// surface that already renders all four readiness arms).</summary>
    public enum PlayerState : byte { NoTrack, Loading, Reconnecting, Error, Active }

    /// <summary>Which sentence the now-playing title line carries.</summary>
    public enum NowPlayingText : byte { Title, NothingPlaying, Loading, Reconnecting, CannotPlay }

    /// <summary>WHICH identity the now-playing face is showing — the one decision the whole face (art, title, artists,
    /// heart, routes) reads, so its six parts can never disagree mid-skip.
    /// <list type="bullet">
    /// <item><see cref="Live"/> — the deck's own row, resolved (or a state that authors its own copy).</item>
    /// <item><see cref="Hold"/> — the OUTGOING row, kept on screen while the incoming one resolves
    /// (<see cref="PlayerBarRules.FaceHoldMs"/>). A 150 ms skip between two rows the client already has therefore
    /// changes NOTHING but the content itself.</item>
    /// <item><see cref="Placeholder"/> — nothing to show: the shimmer tile and "Loading…", exactly as before.</item>
    /// </list></summary>
    public enum FaceArm : byte { Live, Hold, Placeholder }

    /// <summary>The title line's ink rung.</summary>
    public enum NowPlayingInk : byte { Primary, Secondary, Critical }

    /// <summary>What the primary transport button does when clicked.</summary>
    public enum PrimaryVerb : byte { None, TogglePlay, Retry }

    /// <summary>What a single click on a LIST row does (<c>Track.Invoke</c>): start that row, or toggle the deck.</summary>
    public enum RowAction : byte { Start, Toggle }

    /// <summary>One row of the "⋯" overflow, in BUILD order — which IS the menu order (ch 20 W17).</summary>
    public enum OverflowCommand : byte { Previous, Next, Shuffle, Repeat, Lyrics, Queue, NowPlaying, Video, Mute }

    /// <summary>What the centre rail IS right now — decided by the source's own timeline, never by whether a duration
    /// happened to be reported (a sliding DVR window reported as a 3-minute "track" is the defect this ends).</summary>
    public enum SeekRailMode : byte { Track, Dvr, Line }

    /// <summary>The right cluster's SLOTS, in bit order — which IS the row order. A slot's PRESENCE is a function of the
    /// tier (<see cref="PlayerBarRules.RightSlots"/>) with ONE exception — the video split, which exists iff the
    /// current track has a video (user decision 2026-09-16: a 52-DIP hole beside lyrics was worse than the row easing
    /// once when a video track lands). Playback STATE only lights a slot's FACE (<see cref="PlayerBarRules.SlotFaceVisible"/>).
    /// That split is still the fix for the bar reflowing on track start: the heart, lyrics and overflow used to ARRIVE
    /// with <c>active</c>, each arrival stealing width from the centre while every cluster animated its bounds. A track
    /// starting WITHOUT a video therefore still moves nothing; the centre's seek bar has exactly two widths per tier.</summary>
    [Flags]
    public enum RightSlot : ushort
    {
        None = 0,
        Shuffle = 1 << 0,
        Repeat = 1 << 1,
        Volume = 1 << 2,
        VolumeSlider = 1 << 3,
        Lyrics = 1 << 4,
        Video = 1 << 5,
        Queue = 1 << 6,
        Devices = 1 << 7,
        Expand = 1 << 8,
        More = 1 << 9,
    }

    /// <summary>The folded answer the bar renders from.</summary>
    public readonly record struct PlayerBarFacts(PlayerState State, bool CanTransport, bool PrevEnabled, bool NextEnabled,
        PrimaryVerb Primary)
    {
        public bool Active => State == PlayerState.Active;
        public bool PrimaryEnabled => Primary != PrimaryVerb.None;
    }

    public static class PlayerBarRules
    {
        /// <summary>The overflow can hold every <see cref="OverflowCommand"/> at once.</summary>
        public const int MaxOverflow = 9;

        /// <summary>At or under this linear volume the glyph reads as muted.</summary>
        public const float MuteThreshold = 0.001f;

        /// <summary>The play-next drop's batch cap (G-211, ch 19 #87): a dropped playlist/album inserts at most this
        /// many tracks before the front of the queue; the rest are silently NOT the drop's problem — the toast says
        /// "Added the first N" rather than pretending the whole thing landed.</summary>
        public const int MaxPlayNextDrop = 100;

        /// <summary>How many of <paramref name="totalTracks"/> a play-next drop actually inserts.</summary>
        public static int DropInsertCount(int totalTracks) => Math.Min(totalTracks, MaxPlayNextDrop);

        /// <summary>Did the cap actually cut anything, i.e. does the toast say "the first N" rather than just "N"?</summary>
        public static bool DropWasTruncated(int totalTracks) => totalTracks > MaxPlayNextDrop;

        /// <summary>The software mute's restore level when there is no output device to mute (the fake path).</summary>
        public const float SoftwareUnmuteLevel = 0.7f;

        /// <summary>How long the bar keeps showing the OUTGOING identity while the incoming one resolves. A skip
        /// between two rows the client already holds resolves in 100-250 ms, so the whole gap fits inside this and the
        /// face never degrades; a load that outlives it was never a skip and falls back to the placeholder.</summary>
        public const float FaceHoldMs = 450f;

        /// <summary>How long a <c>Loading</c>/buffering phase must LAST before the top edge sweeps. The sweep is for a
        /// wait, not for a transition: a normal skip is over long before this and shows no sweep at all. A network
        /// recovery is exempt — it is a real, user-meaningful condition and sweeps immediately.</summary>
        public const float SweepDelayMs = 450f;

        /// <summary>WHICH identity the face shows. <c>NoTrack</c> and <c>Error</c> answer <see cref="FaceArm.Live"/>
        /// unconditionally: both author their own copy ("Nothing playing", the fault sentence) and must never be masked
        /// by a held track. Otherwise a resolved title is <see cref="FaceArm.Live"/>, an unresolved one HOLDS the
        /// outgoing face while it is fresh (&lt; <see cref="FaceHoldMs"/>), and everything else is the placeholder.
        /// <paramref name="hasHeldFace"/> is the caller's answer to "is there an outgoing identity still worth
        /// showing" — a held row that has itself been evicted is not.</summary>
        public static FaceArm FaceOf(PlayerState state, bool titleKnown, bool hasHeldFace, float msSinceIdentityChange)
            => state is PlayerState.NoTrack or PlayerState.Error ? FaceArm.Live
             : titleKnown ? FaceArm.Live
             : hasHeldFace && msSinceIdentityChange < FaceHoldMs ? FaceArm.Hold
             : FaceArm.Placeholder;

        /// <summary>Is an IDENTITY on screen at all (as opposed to the placeholder)? The one question the parts of the
        /// face that are not text ask of <see cref="FaceOf"/> — the heart above, and the art/route reads at the call
        /// site.</summary>
        public static bool IdentityShowing(FaceArm arm) => arm is FaceArm.Live or FaceArm.Hold;

        /// <summary>The ORDER is 0.2.9's: an error outranks everything, a missing playable outranks loading, loading
        /// outranks a network recovery.</summary>
        public static PlayerState StateOf(bool hasCurrent, Playback.Fault error, Playback.Phase phase,
            Playback.RecoveryKind recovery)
            => error != Playback.Fault.None ? PlayerState.Error
             : !hasCurrent ? PlayerState.NoTrack
             : phase == Playback.Phase.Loading ? PlayerState.Loading
             : recovery == Playback.RecoveryKind.Network ? PlayerState.Reconnecting
             : PlayerState.Active;

        /// <summary>The whole fold. <c>canTransport = active || buffering || reconnecting</c> verbatim; Previous/Next
        /// additionally AND the model's folded restriction bits (a context the cluster says cannot skip greys them —
        /// 0.3's <c>CanSkipNext</c>/<c>CanSkipPrev</c>, which 0.2.9 did not have).
        ///
        /// <para>THE PRIMARY IS THE USER'S INTENT, NOT THE PIPELINE'S STATE. It is a RETRY while errored and dead only
        /// for <see cref="PlayerState.NoTrack"/> — there is nothing to toggle with no playable. <c>Loading</c> keeps
        /// <see cref="PrimaryVerb.TogglePlay"/>: a skip while playing spends 100-250 ms in that state, and the old
        /// <c>None</c> made the glyph flip pause → play → pause and grey out in between, three discrete changes for a
        /// gap the user experiences as one continuous "still playing". The intent through that gap is PLAYING, so the
        /// button keeps saying so — and <c>Playback.IsPlaying</c>, not this fold, still picks WHICH glyph is drawn.</para></summary>
        public static PlayerBarFacts Fold(bool hasCurrent, Playback.Fault error, Playback.Phase phase, bool buffering,
            Playback.RecoveryKind recovery, bool canSkipPrev, bool canSkipNext)
        {
            var state = StateOf(hasCurrent, error, phase, recovery);
            bool canTransport = state == PlayerState.Active || buffering || state == PlayerState.Reconnecting;
            // Previous/Next stay ARMED under a fault: the reducer's `Advance` has no error guard and heals the fault
            // through `PutOnDeck`, so skipping off a dead row is the user's way OUT of it — otherwise the only verb on
            // offer is Retry against the same row. (The reducer's own `State.CanSkipNext` folds `CanTransport`, which is
            // false while errored: that is the transport VERBS' contract, not the bar's, and it deliberately stays so.)
            bool canSkip = canTransport || state == PlayerState.Error;
            var primary = state switch
            {
                PlayerState.Error => PrimaryVerb.Retry,
                PlayerState.NoTrack => PrimaryVerb.None,
                _ => PrimaryVerb.TogglePlay,
            };
            return new PlayerBarFacts(state, canTransport, canSkip && canSkipPrev, canSkip && canSkipNext, primary);
        }

        /// <summary>What a click on a list row does: the deck row toggles pause/resume, any other row STARTS. The one
        /// exception is the deck row under a fault — the reducer's Resume returns while <c>Error != None</c>, so a toggle
        /// there is a click that does nothing; the row is started afresh instead, which runs through <c>PutOnDeck</c> and
        /// clears the fault (the same heal <see cref="PrimaryVerb.Retry"/> performs).</summary>
        public static RowAction RowVerb(bool isDeckRow, Playback.Fault error)
            => isDeckRow && error == Playback.Fault.None ? RowAction.Toggle : RowAction.Start;

        /// <summary>The title sentence. A playable whose title has not landed reads "Loading…" — the bar NEVER renders
        /// a uri (ch 20 §7).</summary>
        public static NowPlayingText TextOf(PlayerState state, bool titleKnown) => state switch
        {
            PlayerState.NoTrack => NowPlayingText.NothingPlaying,
            PlayerState.Reconnecting => NowPlayingText.Reconnecting,
            PlayerState.Error => NowPlayingText.CannotPlay,
            _ => titleKnown ? NowPlayingText.Title : NowPlayingText.Loading,
        };

        public static NowPlayingInk InkOf(PlayerState state) => state switch
        {
            PlayerState.NoTrack or PlayerState.Reconnecting => NowPlayingInk.Secondary,
            PlayerState.Error => NowPlayingInk.Critical,
            _ => NowPlayingInk.Primary,
        };

        /// <summary>The artists line is ABSENT for NoTrack and Error (not merely empty — W7/W9), present otherwise.</summary>
        public static bool ShowsArtistLine(PlayerState state, bool showSubtitle)
            => showSubtitle && state != PlayerState.NoTrack && state != PlayerState.Error;

        /// <summary>WHICH FACE the primary wears — pause (true) or play. <paramref name="isPlaying"/> is the model's
        /// <c>Phase == Playing</c>, which a skip drops for the 100-250 ms it spends loading: reading it raw makes the
        /// glyph flip pause → play → pause on every track change, which <see cref="Fold"/>'s verb alone does not fix.
        /// A LOAD therefore keeps the last SETTLED answer (<paramref name="playingBeforeLoad"/>) — the user's intent
        /// through the gap — so skipping while playing shows one uninterrupted pause glyph, and skipping while paused
        /// shows play until the new row actually starts.</summary>
        public static bool ShowsPauseGlyph(PlayerState state, bool isPlaying, bool playingBeforeLoad)
            => state == PlayerState.Loading ? playingBeforeLoad : isPlaying;

        /// <summary>The global activity cue: the top edge sweeps while opening, refilling or reconnecting — but a
        /// LOAD only once it has lasted <see cref="SweepDelayMs"/> (<paramref name="msInPhase"/> is how long the
        /// current loading/buffering window has been open). A track skip is a transition, not a wait: running a
        /// high-contrast indeterminate sweep across the whole bar for 150 ms is the single loudest thing the old bar
        /// did on every skip. A network recovery keeps sweeping IMMEDIATELY — that one is a real condition the user is
        /// owed, not a frame of latency.</summary>
        public static bool TopEdgeSweeps(Playback.Phase phase, bool buffering, Playback.RecoveryKind recovery,
            float msInPhase)
            => recovery == Playback.RecoveryKind.Network
            || ((phase == Playback.Phase.Loading || buffering) && msInPhase >= SweepDelayMs);

        /// <summary>ONE transport per WINDOW: the bar keeps it for every placement except full-bleed fullscreen, where
        /// the shell unmounts the bar outright (ch 20 §0 item 15).</summary>
        public static bool OwnsTransport(Video.TransportOwner owner)
            => owner is Video.TransportOwner.GlobalBar or Video.TransportOwner.PopOut or Video.TransportOwner.Docked;

        /// <summary>The video split button's slot exists on the queue tier AND only while the current track has a video:
        /// with no video the cluster RECLAIMS the 52 DIP rather than carrying an unlit hole beside lyrics (user decision
        /// 2026-09-16, narrowing the earlier tier-only rule). <paramref name="hasVideo"/> is the ONE state input to the
        /// right cluster's width; <c>active</c> is still not one (§0 item 12) — a track starting without a video widens
        /// nothing. Whether the face is lit is separate: <see cref="SlotFaceVisible"/>.</summary>
        public static bool VideoSlotReserved(in PlayerBarLayout layout, bool hasVideo) => layout.ShowQueue && hasVideo;

        /// <summary>The "⋯" slot is reserved wherever the IDLE bar already has rows in its menu — the tier-only rows
        /// (shuffle/repeat, queue, now-playing). Every state-dependent row (lyrics, video, mute) only ever ADDS to a menu
        /// that is already non-empty at that tier, so playback can never make the slot appear or vanish.</summary>
        public static bool OverflowSlotReserved(in PlayerBarLayout layout)
        {
            Span<OverflowCommand> scratch = stackalloc OverflowCommand[MaxOverflow];
            return Overflow(layout, ownsTransport: false, active: false, hasVideo: false, scratch) > 0;
        }

        /// <summary>Which slots the right cluster HAS, in row order. The tier decides every slot but the video split,
        /// whose presence <paramref name="hasVideo"/> decides (<see cref="VideoSlotReserved"/>); <see cref="PlayerState"/>
        /// is deliberately NOT an input.</summary>
        public static RightSlot RightSlots(in PlayerBarLayout layout, bool hasVideo)
        {
            var slots = RightSlot.None;
            if (layout.ShowShuffleRepeat) slots |= RightSlot.Shuffle | RightSlot.Repeat;
            if (layout.ShowVolumeButton) slots |= RightSlot.Volume;
            if (layout.ShowVolumeSlider) slots |= RightSlot.VolumeSlider;
            if (layout.ShowLyrics) slots |= RightSlot.Lyrics;
            if (VideoSlotReserved(layout, hasVideo)) slots |= RightSlot.Video;
            if (layout.ShowQueue) slots |= RightSlot.Queue;
            if (layout.ShowDevices) slots |= RightSlot.Devices;
            if (layout.ShowExpand) slots |= RightSlot.Expand;
            if (OverflowSlotReserved(layout)) slots |= RightSlot.More;
            return slots;
        }

        /// <summary>One slot's fixed width: the volume rail, the split video button (glyph + chevron), else the button
        /// box. Exactly ONE bit must be set.</summary>
        public static float SlotWidth(RightSlot slot, in PlayerBarLayout layout) => slot switch
        {
            RightSlot.VolumeSlider => PlayerBarLayout.VolumeSliderW,
            RightSlot.Video => layout.ButtonBox + PlayerBarLayout.SplitChevronW,
            _ => layout.ButtonBox,
        };

        /// <summary>The right cluster's width — the slot sum plus the gaps between them. A function of the tier and
        /// <paramref name="hasVideo"/> ONLY, so the centre's seek bar has exactly TWO widths per tier (with / without
        /// the video split) and the wider one is <see cref="PlayerBarLayout.RightWMax"/>.</summary>
        public static float RightWidth(in PlayerBarLayout layout, bool hasVideo)
        {
            var slots = RightSlots(layout, hasVideo);
            float w = 0f;
            int n = 0;
            for (int bit = 1; bit <= (int)RightSlot.More; bit <<= 1)
            {
                var slot = (RightSlot)bit;
                if ((slots & slot) == 0) continue;
                w += SlotWidth(slot, layout);
                n++;
            }
            return n == 0 ? 0f : w + layout.RightGap * (n - 1);
        }

        /// <summary>Does a present slot show its face? Lyrics need a playable; the video split — which only EXISTS with
        /// a video (<see cref="VideoSlotReserved"/>) — additionally needs an Active playable. Every other slot is always
        /// lit.</summary>
        public static bool SlotFaceVisible(RightSlot slot, PlayerState state, bool hasVideo) => slot switch
        {
            RightSlot.Lyrics => state == PlayerState.Active,
            RightSlot.Video => state == PlayerState.Active && hasVideo,
            _ => true,
        };

        /// <summary>The heart's slot is the tier's (<see cref="PlayerBarLayout.ShowLikeSlot"/>); its face needs an
        /// IDENTITY on screen to like — <paramref name="identityShowing"/>, i.e. <see cref="IdentityShowing"/> of the
        /// face's arm — never <c>state == Active</c>. Keying on Active made the heart fade out and back on every skip,
        /// because the face it belongs to is still right there being held. It stays dark for NoTrack and Error, which
        /// have no playable to save. (Whether the heart can be CLICKED is a separate question the bar answers at the
        /// call site: a HELD face is not the deck row, so its heart is lit but inert. The slot is reserved either way,
        /// so neither answer moves anything.)</summary>
        public static bool LikeFaceVisible(in PlayerBarLayout layout, PlayerState state, bool identityShowing)
            => layout.ShowLikeSlot && identityShowing && state is not (PlayerState.NoTrack or PlayerState.Error);

        /// <summary>Below Medium an ACTIVE bar carries Mute/Unmute in the overflow; an idle one has no volume
        /// affordance at all.</summary>
        public static bool VolumeInOverflow(in PlayerBarLayout layout, bool active) => !layout.ShowVolumeButton && active;

        /// <summary>Everything that left the row, in the fixed order. Returns the count written into
        /// <paramref name="dest"/> (length ≥ <see cref="MaxOverflow"/>). 0.2.9's dead like-branch
        /// (<c>!showLike &amp;&amp; active</c>, unreachable) is not ported.</summary>
        public static int Overflow(in PlayerBarLayout layout, bool ownsTransport, bool active, bool hasVideo,
            Span<OverflowCommand> dest)
        {
            int n = 0;
            if (ownsTransport && !layout.ShowPrevNext) { dest[n++] = OverflowCommand.Previous; dest[n++] = OverflowCommand.Next; }
            if (!layout.ShowShuffleRepeat) { dest[n++] = OverflowCommand.Shuffle; dest[n++] = OverflowCommand.Repeat; }
            if (!layout.ShowLyrics && active) dest[n++] = OverflowCommand.Lyrics;
            if (!layout.ShowQueue) dest[n++] = OverflowCommand.Queue;
            if (!layout.ShowExpand) dest[n++] = OverflowCommand.NowPlaying;
            // A tier WITHOUT the inline split carries the verb here instead — and like the slot: no video, no row.
            if (!layout.ShowQueue && active && hasVideo) dest[n++] = OverflowCommand.Video;
            if (VolumeInOverflow(layout, active)) dest[n++] = OverflowCommand.Mute;
            return n;
        }

        /// <summary>Off → Context → Track → Off.</summary>
        public static RepeatMode NextRepeat(RepeatMode mode) => mode switch
        {
            RepeatMode.Off => RepeatMode.Context,
            RepeatMode.Context => RepeatMode.Track,
            _ => RepeatMode.Off,
        };

        public static string RepeatGlyph(RepeatMode mode) => mode == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll;

        public static bool ShowsMuteGlyph(bool muted, float volume) => muted || volume <= MuteThreshold;

        /// <summary>The software 0 ⇄ 0.7 toggle, for a build with no output device to mute.</summary>
        public static float SoftwareMuteTarget(float volume) => volume > MuteThreshold ? 0f : SoftwareUnmuteLevel;

        /// <summary>The inline volume rail's thumb bubble: <c>"72%"</c>, no space, clamped 0..100.</summary>
        public static string VolumePercent(float volume)
            => Math.Clamp((int)MathF.Round(volume * 100f), 0, 100).ToString(CultureInfo.InvariantCulture) + "%";

        /// <summary>The heart pops ONLY on the same playable's unsaved → saved edge: a track change also flips the
        /// saved bit but is not a like, and an unlike is a plain swap.</summary>
        public static bool LikePops(int previousSlot, bool previouslyLiked, int slot, bool liked)
            => liked && !previouslyLiked && slot > 0 && previousSlot == slot;

        /// <summary>The bar's error TITLE, one loc key per <see cref="Playback.Fault"/> reason (G-212): 0.2.9 printed
        /// the same "Can't play this track" whatever went wrong, though the model always knew which. <c>None</c> never
        /// reaches the bar (the title only renders in <see cref="PlayerState.Error"/>) and folds to the generic
        /// sentence rather than throwing on a call made out of sequence.</summary>
        public static string FaultTitleKey(Playback.Fault fault) => fault switch
        {
            Playback.Fault.Network => Strings.Player.Fault.Network,
            Playback.Fault.Unavailable => Strings.Player.Fault.Unavailable,
            Playback.Fault.DrmRequired => Strings.Player.Fault.DrmRequired,
            Playback.Fault.DecodeFailed => Strings.Player.Fault.DecodeFailed,
            Playback.Fault.RuntimeMissing => Strings.Player.Fault.RuntimeMissing,
            _ => Strings.Player.Fault.Unknown,
        };
    }

    // ══ 2. THE DEVICE ROSTER, READ ══════════════════════════════════════════════════════════════════════════════════

    public static class DeviceRoster
    {
        /// <summary>The Connect row the bar says it is "Playing on", or -1. The OWNER gates it: 0.3's active slot also
        /// names the device that just LEFT (<c>NobodyCause.FromForeign</c>), and the line must disappear then even
        /// though that device is still in the roster (ch 20 §0 item 10, parity 80).</summary>
        public static int RemoteSlot(Playback.Owner owner, int activeSlot, ReadOnlySpan<Playback.Devices.Row> rows)
            => owner == Playback.Owner.Foreign && (uint)activeSlot < (uint)rows.Length
               && rows[activeSlot].Kind != DeviceKind.ThisDevice ? activeSlot : -1;

        /// <summary>The roster slot carrying <paramref name="deviceId"/> (ordinal-ignore-case), or -1.</summary>
        public static int SlotOfId(ReadOnlySpan<Playback.Devices.Row> rows, string? deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return -1;
            for (int i = 0; i < rows.Length; i++)
                if (string.Equals(rows[i].Id, deviceId, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>This PC's own row — where "pull playback home" transfers to — or -1.</summary>
        public static int ThisDeviceSlot(ReadOnlySpan<Playback.Devices.Row> rows)
        {
            for (int i = 0; i < rows.Length; i++) if (rows[i].Kind == DeviceKind.ThisDevice) return i;
            return -1;
        }

        public static string ConnectGlyph(DeviceKind kind) => kind switch
        {
            DeviceKind.Phone => Icons.CellPhone,
            DeviceKind.Speaker => Icons.Speakers,
            DeviceKind.Tv => Icons.TvMonitor,
            _ => Icons.ThisPc,
        };

        /// <summary>A local endpoint's form factor, in 0.2.9's <c>LocalAudioDeviceKind</c> order (Speakers,
        /// Headphones, Headset, Hdmi, …). The Hdmi rung is unreachable from the picker — display audio is filtered
        /// before the roster — but the map keeps it for other callers.</summary>
        public static string LocalGlyph(byte kind) => kind switch
        {
            0 => Icons.Speakers,
            1 or 2 => Icons.Headphones,
            3 => Icons.TvMonitor,
            _ => Icons.ThisPc,
        };

        /// <summary>The picker's inset from the window edges before it scrolls (G-210, ch 20 §9 trap): the engine's
        /// own <c>MenuFlyout</c> cap (468 DIP) is a FIXED number that still overruns a short window. The picker's cap
        /// tracks the LIVE window instead.</summary>
        public const float PickerWindowInset = 96f;

        /// <summary>The picker's scroll viewport cap for a window of <paramref name="windowHeightDip"/>: the window
        /// height less <see cref="PickerWindowInset"/>, floored so a tiny/undocked window never collapses the picker
        /// to nothing.</summary>
        public const float PickerMinHeight = 120f;

        public static float PickerMaxHeight(float windowHeightDip)
            => MathF.Max(PickerMinHeight, windowHeightDip - PickerWindowInset);
    }

    // ══ 3. THE SEEK RAIL ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Accumulation belongs to the intent, not the last delayed backend position sample.</summary>
    public sealed class PlayerSeekAccumulator
    {
        EntityId _id;
        uint _scope;
        Playback.Owner _owner;
        int _device;
        long _at, _target;
        bool _pending;
        public void Reset() => _pending = false;
        public int Step(EntityId id, uint scope, Playback.Owner owner, int device,
            long reportedMs, long durationMs, long nowMs, int deltaMs, long minimumMs = 0)
        {
            bool same = _pending && _id == id && _scope == scope && _owner == owner && _device == device;
            bool awaiting = same && nowMs >= _at && nowMs - _at < 2000
                && Math.Abs(reportedMs - _target) > 1000;
            long basis = awaiting ? _target : reportedMs;
            long maximum = Math.Min(Math.Max(0, durationMs), int.MaxValue);
            _target = Math.Clamp(basis + deltaMs, Math.Clamp(minimumMs, 0, maximum), maximum);
            _id = id; _scope = scope; _owner = owner; _device = device; _at = nowMs; _pending = true;
            return (int)_target;
        }
    }

    public enum PlayerKeyIntent : byte { None, SeekBack, SeekForward, VolumeDown, VolumeUp, Toggle }

    public static PlayerKeyIntent PlayerKey(int key, bool focusedContainer, bool handled, bool modified)
        => !focusedContainer || handled || modified ? PlayerKeyIntent.None : key switch
        {
            FluentGpu.Foundation.Keys.Left => PlayerKeyIntent.SeekBack,
            FluentGpu.Foundation.Keys.Right => PlayerKeyIntent.SeekForward,
            FluentGpu.Foundation.Keys.Down => PlayerKeyIntent.VolumeDown,
            FluentGpu.Foundation.Keys.Up => PlayerKeyIntent.VolumeUp,
            FluentGpu.Foundation.Keys.Space => PlayerKeyIntent.Toggle,
            _ => PlayerKeyIntent.None,
        };

    /// <summary>The podcast pressure map prioritizes its time-step and speed controls over secondary commands.</summary>
    public static PlayerBarLayout PodcastBarLayout(in PlayerBarLayout layout)
    {
        var next = layout with
        {
            ShowShuffleRepeat = false,
            ShowVolumeSlider = false,
            ShowPrevNext = layout.Tier >= PlayerBarTier.Wide,
            ShowTimesElapsed = layout.Tier >= PlayerBarTier.Comfortable,
            ShowTimesRemaining = layout.Tier >= PlayerBarTier.Comfortable,
            ShowLikeSlot = layout.Tier >= PlayerBarTier.Medium,
            LeftW = layout.Tier switch { PlayerBarTier.Minimal => layout.ArtSize, PlayerBarTier.Compact => 120f, _ => layout.LeftW },
        };
        return next with { RightWMax = PlayerBarRules.RightWidth(in next, hasVideo: true) };
    }

    public static class SeekRail
    {
        /// <summary>The pixel-dwell clamp: short tracks stay smooth, long tracks do not oversample.</summary>
        public const float MinDwellMs = 33f, MaxDwellMs = 250f;

        /// <summary>The dwell while the span or the rail width is not known yet.</summary>
        public const float UnknownDwellMs = 100f;

        public static SeekRailMode ModeOf(in Playback.LiveWindow live)
            => !live.IsLive ? SeekRailMode.Track : live.HasWindow ? SeekRailMode.Dvr : SeekRailMode.Line;

        /// <summary>May the rail be dragged? Derived every render from the model, never a ctor flag.</summary>
        public static bool Enabled(bool hasCurrent, Playback.Fault error, Playback.Phase phase, bool canSeek)
            => hasCurrent && error == Playback.Fault.None && phase != Playback.Phase.Loading && canSeek;

        /// <summary>Does the playhead MOVE on its own? The ticker is mounted only while this holds, so a paused or
        /// refilling bar wakes no frames.</summary>
        public static bool Advances(bool hasCurrent, Playback.Fault error, Playback.Phase phase, bool buffering)
            => hasCurrent && error == Playback.Fault.None && phase == Playback.Phase.Playing && !buffering;

        /// <summary>The rail's span: a track's duration, or a DVR window's width.</summary>
        public static long SpanMs(in Playback.LiveWindow live, long durationMs)
            => live.IsLive && live.HasWindow ? live.WindowMs : durationMs;

        /// <summary>How long the playhead dwells on one pixel: <c>clamp(span / railPx, 33, 250)</c> ms.</summary>
        public static float DwellMs(long spanMs, float railPx)
            => spanMs <= 0L || railPx <= 1f ? UnknownDwellMs : Math.Clamp(spanMs / railPx, MinDwellMs, MaxDwellMs);

        /// <summary>The model's fraction for a position. A DVR rail maps the WINDOW and snaps full at the live edge
        /// (<see cref="Playback.LiveRail.DisplayFrac"/>, off the DECIDED edge state); a track maps 0 → duration, and an
        /// unknown duration is an EMPTY rail, never a full grey one.</summary>
        public static float ModelFraction(SeekRailMode mode, long positionMs, long durationMs,
            in Playback.LiveWindow live, bool isBehind)
        {
            if (mode == SeekRailMode.Dvr)
                return (float)Playback.LiveRail.DisplayFrac(live.SeekableStartMs, live.SeekableEndMs, positionMs, isBehind);
            if (durationMs <= 0L) return 0f;
            return Math.Clamp(positionMs / (float)durationMs, 0f, 1f);
        }

        /// <summary>THE SCRUB GATE: while the finger is down the drawn fraction IGNORES the model, so a position report
        /// can never yank the thumb back under it.</summary>
        public static float Displayed(bool scrubbing, float scrubFraction, float modelFraction)
            => scrubbing ? scrubFraction : modelFraction;

        /// <summary>Snap to the rail's whole-pixel grid, so most ticker frames land on the SAME pixel, write nothing and
        /// let the host elide the present. Raw while the width is unknown.</summary>
        public static float Quantize(float fraction, float railPx)
            => railPx > 1f ? MathF.Round(fraction * railPx) / railPx : fraction;

        /// <summary>The fraction under a pointer at local <paramref name="x"/> (the engine delivers it unclamped).</summary>
        public static float FractionAt(float x, float railPx) => Math.Clamp(x / (railPx > 0f ? railPx : 1f), 0f, 1f);

        /// <summary>Can a gesture commit at all? A track needs a duration; a live source needs a window.</summary>
        public static bool CanCommit(in Playback.LiveWindow live, long durationMs)
            => (live.IsLive && live.HasWindow) || durationMs > 0L;

        /// <summary>The position a released gesture commits to: clamped INTO the window for a DVR, into the duration
        /// for a track.</summary>
        public static long CommitTargetMs(float fraction, long durationMs, in Playback.LiveWindow live)
            => ModeOf(live) == SeekRailMode.Dvr
                ? Playback.LiveRail.Seek(in live, fraction)
                : Math.Clamp((long)(fraction * durationMs), 0L, Math.Max(0L, durationMs));

        /// <summary>How long a released drag keeps showing the DROP point while the seek travels through the model. The
        /// commit is a posted input that lands on the next drain; without the hold, a ticker frame between the release
        /// and that drain repaints the OLD position for one step (parity 33). Bounded, so a refused seek cannot pin
        /// the thumb at a position the audio never reached.</summary>
        public const long CommitHoldMs = 750L;

        /// <summary>Is the drop point still held? Released by the first position report after the commit, or by
        /// <see cref="CommitHoldMs"/> — whichever comes first. <paramref name="committedAtMs"/> ≤ 0 means no commit is
        /// pending.</summary>
        public static bool HoldsDrop(long committedAtMs, long nowMs)
            => committedAtMs > 0L && nowMs - committedAtMs < CommitHoldMs;

        /// <summary>The thumb ring's left edge: centred on the fraction, never past either end of the rail.</summary>
        public static float ThumbX(float railPx, float fraction, float ringDiameter)
            => Math.Clamp(Math.Clamp(fraction, 0f, 1f) * railPx - ringDiameter * 0.5f, 0f, MathF.Max(0f, railPx - ringDiameter));
    }

    // ══ 4. THE TIME LABELS ═════════════════════════════════════════════════════════════════════════════════════════

    public static class TimeLabel
    {
        /// <summary>The track slot: wide enough for "-59:59", so 0:09 → 0:10 moves nothing.</summary>
        public const float SlotW = 44f;

        /// <summary>The live slot, sized once for "GO LIVE −99:59", so the mark ↔ action swap moves nothing.</summary>
        public const float LiveSlotW = 104f;

        /// <summary>Elapsed since TUNE-IN — what "elapsed" means for a broadcast. Before the stamp lands it falls back
        /// to the reported POSITION, never to 0, so the label cannot blink to 0:00 on the way in.</summary>
        public static long ElapsedSinceTuneIn(long tunedInAtUnixMs, long positionMs, long nowUnixMs)
            => tunedInAtUnixMs <= 0L ? positionMs : Math.Max(0L, nowUnixMs - tunedInAtUnixMs);

        /// <summary>The right slot is the live mark / GO LIVE action while live — there is no duration to count.</summary>
        public static bool RightSlotIsLive(bool rightSlot, bool isLive) => rightSlot && isLive;

        /// <summary>BEHIND a rewindable window the slot becomes the way back; otherwise it states LIVE.</summary>
        public static bool OffersGoLive(in Playback.LiveWindow live, bool isBehind) => live.HasWindow && isBehind;

        /// <summary>The milliseconds a label prints and whether it carries the minus sign. The minus belongs only to an
        /// actual WHOLE-SECOND remainder: a 400 ms remainder prints "0:00", never "−0:00" (0.2.9 tested
        /// <c>ms &gt; 0</c>, which could).</summary>
        public static (long Ms, bool Minus) Of(bool rightSlot, bool showRemaining, long positionMs, long durationMs,
            bool isLive, long tunedInAtUnixMs, long nowUnixMs)
        {
            if (!rightSlot) return (isLive ? ElapsedSinceTuneIn(tunedInAtUnixMs, positionMs, nowUnixMs) : positionMs, false);
            if (!showRemaining) return (Math.Max(0L, durationMs), false);
            long remaining = Math.Max(0L, durationMs - positionMs);
            return (remaining, remaining >= 1000L);
        }
    }
}
