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

    /// <summary>The title line's ink rung.</summary>
    public enum NowPlayingInk : byte { Primary, Secondary, Critical }

    /// <summary>What the primary transport button does when clicked.</summary>
    public enum PrimaryVerb : byte { None, TogglePlay, Retry }

    /// <summary>One row of the "⋯" overflow, in BUILD order — which IS the menu order (ch 20 W17).</summary>
    public enum OverflowCommand : byte { Previous, Next, Shuffle, Repeat, Lyrics, Queue, NowPlaying, Video, Mute }

    /// <summary>What the centre rail IS right now — decided by the source's own timeline, never by whether a duration
    /// happened to be reported (a sliding DVR window reported as a 3-minute "track" is the defect this ends).</summary>
    public enum SeekRailMode : byte { Track, Dvr, Line }

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

        /// <summary>The software mute's restore level when there is no output device to mute (the fake path).</summary>
        public const float SoftwareUnmuteLevel = 0.7f;

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
        /// 0.3's <c>CanSkipNext</c>/<c>CanSkipPrev</c>, which 0.2.9 did not have). The primary is a RETRY while
        /// errored and dead only for NoTrack/Loading — merely buffering keeps it live.</summary>
        public static PlayerBarFacts Fold(bool hasCurrent, Playback.Fault error, Playback.Phase phase, bool buffering,
            Playback.RecoveryKind recovery, bool canSkipPrev, bool canSkipNext)
        {
            var state = StateOf(hasCurrent, error, phase, recovery);
            bool canTransport = state == PlayerState.Active || buffering || state == PlayerState.Reconnecting;
            var primary = state switch
            {
                PlayerState.Error => PrimaryVerb.Retry,
                PlayerState.NoTrack or PlayerState.Loading => PrimaryVerb.None,
                _ => PrimaryVerb.TogglePlay,
            };
            return new PlayerBarFacts(state, canTransport, canTransport && canSkipPrev, canTransport && canSkipNext, primary);
        }

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

        /// <summary>The global activity cue: the top edge sweeps while opening, refilling or reconnecting.</summary>
        public static bool TopEdgeSweeps(Playback.Phase phase, bool buffering, Playback.RecoveryKind recovery)
            => phase == Playback.Phase.Loading || buffering || recovery == Playback.RecoveryKind.Network;

        /// <summary>ONE transport per WINDOW: the bar keeps it for every placement except full-bleed fullscreen, where
        /// the shell unmounts the bar outright (ch 20 §0 item 15).</summary>
        public static bool OwnsTransport(Video.TransportOwner owner)
            => owner is Video.TransportOwner.GlobalBar or Video.TransportOwner.PopOut or Video.TransportOwner.Docked;

        /// <summary>The video split button's slot is RESERVED whenever a video could exist, so an async <c>hasVideo</c>
        /// never reflows the row (§0 item 12).</summary>
        public static bool VideoSlotReserved(in PlayerBarLayout layout, bool active) => active && layout.ShowQueue;

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
            // Unlike the inline slot this row is NOT reserved: no video, no row.
            if (!VideoSlotReserved(layout, active) && active && hasVideo) dest[n++] = OverflowCommand.Video;
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
    }

    // ══ 3. THE SEEK RAIL ════════════════════════════════════════════════════════════════════════════════════════════

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
