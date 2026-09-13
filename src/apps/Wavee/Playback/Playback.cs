// ── Playback/Playback.cs ───────────────────────────────────────────────────────────────────────────────────────────
// State, Input, Effects, Step, the ownership fold; SmtcTimelineCoalescer (ch 14, +67); TimeFormat / LiveRail /
// LiveEdgeState (ch 20, +120)
//
// Role: CORE
// Owner: G
// Wave: 3
// Budget: 1790 lines
// Spec: plan + ch 14 §9 + ch 20 §9
//
// THE REDUCER. One value in, one `State` mutated in place, a fixed set of effect SLOTS filled. Nothing here opens a
// socket, reads a clock, allocates or awaits: `Playback.Host.cs` (SHELL) drains a mailbox on the UI thread, calls
// `Step` once per input inside ONE engine Batch, and executes whatever the slots hold ONCE at the end of the drain.
// That is the whole of C1-C5:
//
//   C1  single writer — `State` and every signal are UI-thread only; every other thread posts a value.
//   C2  inputs are VALUES — a click is `Input.Next()`, a dealer push is `Input.Cluster(frame, remote)`, an audio
//       event is `Input.Audio(...)`. The core is synchronous; every test is "post these inputs, assert State and fx".
//   C3  coalescing by construction — one drain = one Batch = one render, however many inputs. Effects are SLOTS, not
//       a list: ten `Next` clicks are ten `Step`s and exactly ONE `Load`, the last one.
//   C4  epoch on everything that leaves the core — `State.Epoch` bumps on every Step that changes what a shell is
//       doing; a shell result carries the epoch it was started for and `Step` drops anything older.
//   C5  remote truth vs local claim — the ownership fold with the server-timestamp FENCE lives HERE, pure. A cluster
//       push older than the fence cannot revoke; the put-state RESPONSE is the judge.
//
// NO CLOCK IS READ IN THIS FILE. Every input that needs "now" carries it (`Input.NowMs`), stamped by the host off the
// frame clock — the memory rule (`animations-sample-frame-time`): motion and playback sample FRAME time, never
// `Environment.TickCount64`. The same discipline as `Entities.Now`, and the reason a position fold is deterministic
// in a unit test.
//
// WHAT IS DELIBERATELY NOT HERE:
//   • `QueueSlots` / `QueueMovePlan` / `QueueOrder` — ch 21 §8's three ported rule sets live in `Entities/Queue.cs`
//     (owner Q, Wave 5) on top of Wave 1's shape. This file READS `Queue` and never restates one of its rules.
//   • `PlacementCore` / `PlacementState` / `TransportOwner` — A13, owner K's `Shell/Video.cs` (ch 20 §9 is explicit
//     that these do NOT land here).
//   • `PlayerBarTier` / `PlayerBarResponsiveLayout` / `PlayerBarLayout` / `DevicePickerModel` / `PlayableLinks` —
//     owner I's `Shell/Shell.cs` (ch 20 §9).
//   • `VolumeTaper` and the local-endpoint service — owner H's `Playback.Audio.cs` (the UI slider stays LINEAR).
//   • The device ROSTER — `Playback.Host.cs`'s table, painted by `+Shell.PlayerBar.UI.cs`. This file keeps only the
//     ownership VERDICT and the active device's identity hash (the memory rule
//     `connect-ownership-single-authority`: one authority, never routing from raw cluster ids).
//
// Rules: no allocation after warm-up (P8); no LINQ, no closures, no async, no boxing (P9); UI thread only (C1).

using FluentGpu.Foundation;

using RemoteCmd = Wavee.Spotify.Decode.RemoteCmd;
using RemoteCommand = Wavee.Spotify.Decode.RemoteCommand;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;
using ClusterOrigin = Wavee.Spotify.Decode.ClusterOrigin;

namespace Wavee;

public static partial class Playback
{
    // ── 1. the small enums ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the local transport is doing. Buffering is NOT a phase — it is a bit alongside
    /// <see cref="Phase.Playing"/>, because a buffering stream is still the playing one and a bar that drops to
    /// "paused" mid-rebuffer is the flicker 0.2.9 shipped.</summary>
    public enum Phase : byte { Idle, Loading, Playing, Paused, Ended }

    /// <summary>WHO owns playback right now — the one authority (memory rule
    /// <c>connect-ownership-single-authority</c>). Ported from 0.2.9's <c>OwnerKind</c> member-for-member so a
    /// side-by-side reviewer reads the same three names.</summary>
    public enum Owner : byte { Nobody, Us, Foreign }

    /// <summary>Only meaningful while <see cref="Owner.Us"/>. <see cref="Protected"/> = claimed, the server's verdict
    /// pending; <see cref="Adopted"/> = the cluster names us; <see cref="Unadopted"/> = we play but the cluster does
    /// not name us (a module / local-file context the server will not adopt, a lost registration, no publisher).
    /// </summary>
    public enum ClaimPhase : byte { None, Protected, Adopted, Unadopted }

    /// <summary>Why nobody owns playback — it decides what the bar shows (<see cref="FromForeign"/> keeps the departed
    /// device's snapshot) and that a stale "us" cluster is not ownership.</summary>
    public enum NobodyCause : byte { Launch, StaleSelf, FromUs, FromForeign }

    /// <summary>What made us claim. The three that RESTAMP <c>started_playing_at</c> — the stamp the server compares
    /// across devices to pick the newest starter — are the new-playback causes, never a resume or a skip.</summary>
    public enum ClaimCause : byte
    {
        UserPlay, UserResume, NobodyTransferToSelf,
        InboundPlay, InboundTransfer, InboundResume, InboundSkip, InboundQueueStart,
    }

    /// <summary>Why we gave playback up. Pause is not here on purpose: ownership is not audibility.</summary>
    public enum ReleaseCause : byte { TransferAway, EndOfContext, LoadFailed, Logout }

    /// <summary>Why the host is being asked to load. <see cref="Ownership.AllowsLoad"/> reads it: a launch RESTORE may
    /// seed a paused deck without claiming, everything else must own playback first.</summary>
    public enum LoadOrigin : byte { Claim, Advance, MediaKindRefresh, VideoRecovery, Restore }

    /// <summary>What a fold decided the SHELL must do about it. Flags, because one cluster can both stop the host and
    /// oblige an inactive announce. Ported verbatim from 0.2.9's <c>OwnerFx</c>.</summary>
    [Flags]
    public enum OwnerFx : byte
    {
        None = 0,
        /// <summary>The frame is older than one already folded — drop it everywhere (state AND roster).</summary>
        DropFrame = 1,
        /// <summary>The audio/video host must not run: stop it (keep the session), bump the load epoch.</summary>
        StopHost = 2,
        /// <summary>We WERE the owner and lost it — close the listening segment, reset the publisher.</summary>
        EmitBecameInactive = 4,
        /// <summary>Tell the connect-state service we are no longer active.</summary>
        PublishInactive = 8,
        /// <summary>The server answered our claim by keeping another device — log <c>connect.claim.rejected</c>.</summary>
        ClaimRejected = 16,
    }

    /// <summary>Why we are announcing to the connect-state service. The ordinals ARE `connect.proto`'s
    /// <c>PutStateReason</c>, read off the proto itself — the encoder writes this value straight onto the wire.
    ///
    /// <para><b>The host maps to <see cref="Spotify.Connect.PutReason"/> BY NAME, never by a cast.</b> That Wave-2
    /// enum said it carried the proto's ordinals and gave <c>BecameInactive = 6</c> — 6 is <c>PICKER_OPENED</c>, and
    /// the proto's <c>BECAME_INACTIVE</c> is 7. Reported, and owner F has corrected it, so the two now agree
    /// numerically as well; the by-name map stays anyway, because the members of two enums in two files are free to
    /// drift again and announcing "I opened the picker" when we meant "I went inactive" is the kind of thing the
    /// service answers 422 to, silently.</para></summary>
    public enum PublishReason : byte
    {
        Unknown = 0, SpircHello = 1, SpircNotify = 2, NewDevice = 3,
        PlayerStateChanged = 4, VolumeChanged = 5, PickerOpened = 6, BecameInactive = 7,
        AliasChanged = 8, NewConnection = 9, PullPlayback = 10, AudioDriverInfoChanged = 11,
    }

    /// <summary>What the audio/video pump is telling us. Every one of these arrives stamped with the
    /// <see cref="State.LoadEpoch"/> it was started for; anything older is dropped (C4).</summary>
    public enum AudioSignal : byte
    {
        None = 0,
        /// <summary>First frame is out of the sink: the deck is audible. Carries the position it started at.</summary>
        Started,
        /// <summary>A position report from the pump's own 200 ms ticker.</summary>
        Position,
        /// <summary>The stream is refilling. The phase does NOT change — a buffering stream is the playing one.</summary>
        Buffering,
        /// <summary>Refilled.</summary>
        Buffered,
        /// <summary>A committed seek landed.</summary>
        Seeked,
        /// <summary>The source's duration became known (or changed on a variant switch).</summary>
        Duration,
        /// <summary>The load failed for this epoch. Carries a <see cref="Fault"/> in <see cref="Input.LongArg"/>.</summary>
        Failed,
        /// <summary>The sink is paused.</summary>
        Paused,
        /// <summary>The sink resumed.</summary>
        Resumed,
        /// <summary>The pump stopped and holds nothing.</summary>
        Stopped,
    }

    /// <summary>Why the current playable is not playing. <see cref="Fault.None"/> is the ONLY value that lets the
    /// transport arm (ch 20 §7: <c>canTransport = Current != none &amp;&amp; Error == None</c>).</summary>
    public enum Fault : byte { None, Network, Unavailable, DrmRequired, DecodeFailed, RuntimeMissing, Unknown }

    /// <summary>A recoverable interruption affecting the active stream. Deliberately coarse: byte-range and retry
    /// detail stay in diagnostics while playback surfaces only what the chrome can say — ch 20 W10's "Reconnecting"
    /// band and the top-edge sweep read exactly this.</summary>
    public enum RecoveryKind : byte { None, Network }

    /// <summary>Why the host is being told to stop. Carried on the effect rather than inferred, so a log line and a
    /// diagnostics row can say which of the four very different stops this was.</summary>
    public enum StopReason : byte { None, EndOfQueue, LostOwnership, Released, Failed }

    /// <summary>The kind of playable the app's ONE current media is, and therefore which host runs it. Ported from
    /// 0.2.9's <c>MediaSwitchLogic.PlayableKind</c>: a video track is a video wherever it came from, so
    /// <see cref="Video"/> wins over <see cref="LocalFile"/>.</summary>
    public enum PlayableKind : byte { Audio, Video, LocalFile }

    /// <summary>The largest volume the Connect wire carries. Our own <see cref="State.Volume"/> is 0..1; this is the
    /// scale the cluster and the PUT body speak.</summary>
    public const int MaxWireVolume = 65535;

    // ── 2. the live timeline (ch 20 §9) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The engine-authoritative shape of a LIVE broadcast's timeline: whether it is live at all, the seekable
    /// (DVR) window it exposes, where the live edge is, and where the playhead sits inside it. Ported verbatim from
    /// <c>_old/Wavee.Core/Playback/Playback.cs</c>.
    ///
    /// <para><b>Why one value and not four loose numbers.</b> "Live" is not one fact but four that must agree — a
    /// channel with a 4-hour DVR window and a station with no rewind are both live, and the difference decides whether
    /// the rail scrubs or breathes. Publishing them together means the bar can never render a half-updated mix (a
    /// window from the previous variant against this one's edge), which is exactly what a set of independent signals
    /// produces at a variant switch.</para>
    ///
    /// <para><b>Not inferred.</b> Every field is what the media pipeline STATED. A zero duration is not evidence of
    /// live-ness (it is also what "unknown length" looks like), and a finite duration is not evidence against it
    /// (Media Foundation reports a sliding DVR window as a finite number — the defect this type exists to end).</para>
    /// </summary>
    /// <param name="IsLive">Is this a broadcast with a moving live edge?</param>
    /// <param name="SeekableStartMs">The earliest position the source will accept, in ms.</param>
    /// <param name="SeekableEndMs">The latest position the source will accept, in ms (the DVR window's right end).</param>
    /// <param name="LiveEdgeMs">Where "now" is on the broadcast's own clock, in ms.</param>
    /// <param name="PositionMs">Where the playhead sits, in ms, on the same clock.</param>
    /// <param name="IsAtLiveEdge">Is the playhead riding the edge as the SOURCE judges it? NOT the decided state —
    /// that is <see cref="LiveEdgeState"/>, and every surface must read that one instead.</param>
    public readonly record struct LiveWindow(
        bool IsLive,
        long SeekableStartMs,
        long SeekableEndMs,
        long LiveEdgeMs,
        long PositionMs,
        bool IsAtLiveEdge)
    {
        /// <summary>The narrowest DVR window worth offering a rail for. Below this a scrub is a worse affordance than
        /// no scrub at all: the thumb would cover seconds per pixel and the window would slide out from under the
        /// gesture.</summary>
        public const long MinWindowMs = 30_000;

        /// <summary>Does this broadcast expose a REWINDABLE window (≥ <see cref="MinWindowMs"/>)? This — not
        /// <see cref="IsLive"/> — decides whether the seek bar is a DVR rail or a breathing line, and it is the whole
        /// of "can seek" while live (ch 20 §0 item 11: "live is a different rail, not a track with a weird
        /// duration").</summary>
        public bool HasWindow => SeekableEndMs - SeekableStartMs >= MinWindowMs;

        /// <summary>How far behind the live edge the playhead is, in ms (never negative).</summary>
        public long BehindMs => Math.Max(0, LiveEdgeMs - PositionMs);

        /// <summary>The window's span in ms (0 when there is none).</summary>
        public long WindowMs => Math.Max(0, SeekableEndMs - SeekableStartMs);

        /// <summary>"Not a live broadcast" — the honest default for every non-live playable.</summary>
        public static LiveWindow None => default;
    }

    /// <summary>
    /// The one fact every live surface asks — <b>am I at the live edge, or am I behind it?</b> — decided ONCE, with
    /// hysteresis, instead of re-derived from a raw threshold at each of the three places that need it. Ported
    /// verbatim from <c>_old/Wavee/Backend/Playback/LiveEdgeState.cs</c>; the memory rule
    /// <c>derived-facts-live-on-the-model</c> is why the UI may never recompute it.
    ///
    /// <para><b>Why a state machine and not a comparison.</b> A live playable does not sit ON the edge; it sits a few
    /// seconds inside it, and that distance BREATHES. The window is republished several times a second, the edge
    /// advances in bursts as segments land, and a healthy HLS playhead rides 5-8 s behind the window's end. A plain
    /// <c>behind &gt; threshold</c> comparison therefore flips on almost every report: the observed defect was the
    /// player bar's right slot FLICKERING between the LIVE mark and "GO LIVE −0:06" a few times a second, dragging the
    /// whole seek row's layout with it (parity item 66).</para>
    ///
    /// <para><b>The three rules</b>, asymmetric on purpose — the cost of the two mistakes is not the same. AT EDGE
    /// while <see cref="EnterBehindMs"/> or less behind; BEHIND only after <see cref="ConfirmReports"/> CONSECUTIVE
    /// reports past that line; back AT EDGE at <see cref="ReturnToEdgeMs"/> or less. Between the two lines the state
    /// HOLDS.</para>
    /// </summary>
    /// <param name="IsBehind">The decided state: true = BEHIND the edge (offer the way back), false = AT the edge.</param>
    /// <param name="PendingReports">How many consecutive reports have already been seen past
    /// <see cref="EnterBehindMs"/> while still AT EDGE. Zero in every settled state.</param>
    public readonly record struct LiveEdgeState(bool IsBehind, int PendingReports)
    {
        /// <summary>Past this much behind the edge, a playable is a CANDIDATE for BEHIND (confirmed by the next
        /// report). Wide enough that a healthy ride — 5-8 s inside the window's end — never crosses it.</summary>
        public const long EnterBehindMs = 15_000L;

        /// <summary>At or under this much behind, a BEHIND playable is back AT the edge. Strictly below
        /// <see cref="EnterBehindMs"/>: the gap between the two IS the hysteresis.</summary>
        public const long ReturnToEdgeMs = 5_000L;

        /// <summary>How many CONSECUTIVE reports past <see cref="EnterBehindMs"/> confirm the fall back. One report
        /// can be a window that jumped forward on a segment boundary; two in a row is a playhead.</summary>
        public const int ConfirmReports = 2;

        /// <summary>The settled AT-EDGE state — the honest start for every playable, and where GO LIVE puts the
        /// machine outright (the user asked to be at the edge; the next window report must not be able to answer
        /// "still behind" from a position the seek has already left — parity item 67).</summary>
        public static LiveEdgeState AtEdge => default;

        /// <summary>The settled BEHIND state.</summary>
        public static LiveEdgeState Behind => new(true, 0);

        /// <summary>Fold one window report into the state.</summary>
        /// <param name="previous">The state the last report left behind.</param>
        /// <param name="behindMs">How far behind the live edge the playhead is now, in ms (never negative).</param>
        /// <param name="hasWindow">Is there a REWINDABLE window to be behind IN? A station with nothing to rewind can
        /// never be behind, so it settles AT EDGE unconditionally, and so does every non-live playable.</param>
        public static LiveEdgeState Next(LiveEdgeState previous, long behindMs, bool hasWindow)
        {
            // Nothing to be behind IN: AT EDGE, and the counter resets, so a variant switch can never carry a
            // half-confirmed fall into the next playable.
            if (!hasWindow) return AtEdge;

            // Already behind: leave only at the LOWER line. Between the two lines the state holds.
            if (previous.IsBehind) return behindMs <= ReturnToEdgeMs ? AtEdge : Behind;

            // At the edge: anything inside the wide line is ordinary breathing, and it CLEARS the counter — the rule
            // is CONSECUTIVE reports, so one report back inside means the fall did not happen.
            if (behindMs <= EnterBehindMs) return AtEdge;

            int pending = previous.PendingReports + 1;
            return pending >= ConfirmReports ? Behind : new LiveEdgeState(false, pending);
        }
    }

    /// <summary>
    /// The DVR rail's arithmetic — the pure map between a live broadcast's SEEKABLE WINDOW and the 0..1 fraction a
    /// seek bar draws and drags. Ported verbatim from <c>_old/Wavee/Backend/Playback/LiveRail.cs</c>.
    ///
    /// <para><b>Why this is not the ordinary <c>position / duration</c>.</b> An ordinary track's rail is anchored at
    /// zero and scaled by a fixed length. A live broadcast has neither: the window's LEFT end is a wall-clock position
    /// that keeps moving forward, and its right end is the live edge, also moving. Scrubbing such a source with the
    /// position/duration formula puts the thumb where nothing means anything and commits seeks the source refuses. The
    /// rail therefore maps the WINDOW, and the window alone.</para>
    /// </summary>
    public static class LiveRail
    {
        /// <summary>Where the playhead sits inside the seekable window, as a 0..1 fraction (0 = the window's oldest
        /// position, 1 = the live edge). Clamped at both ends, and honest about the degenerate case: a window with no
        /// width answers 1 — a station with nothing to rewind IS at the live edge, and answering 0 would draw an empty
        /// rail under audio that is playing. A playhead a slid window has left behind answers 0, never a negative
        /// fraction.</summary>
        public static double Frac(long startMs, long endMs, long positionMs)
        {
            long span = endMs - startMs;
            if (span <= 0) return 1.0;
            double f = (double)(positionMs - startMs) / span;
            return f <= 0 ? 0.0 : f >= 1 ? 1.0 : f;
        }

        /// <summary>The fraction the DVR rail actually DRAWS — <see cref="Frac"/> with the live edge snapped.
        ///
        /// <para><b>Why the draw differs from the measurement.</b> At the edge, <see cref="Frac"/> answers a number
        /// honest about milliseconds and dishonest about MEANING: a healthy playhead rides a few seconds inside a
        /// window whose two ends are both moving, so it answers ~0.88 on a 50-second window and a DIFFERENT ~0.88 on
        /// every one of the four window reports a second. Drawn literally, that is a rail never full under a stream
        /// that IS live, and a fill sliding under a playhead that is not moving. Once <see cref="LiveEdgeState"/> has
        /// decided the playable is AT the edge, the rail says so: full fill, thumb on the live tick, and — because the
        /// answer no longer depends on two moving ends — dead still between reports.</para></summary>
        /// <param name="isBehind">The DECIDED edge state (<see cref="LiveEdgeState.IsBehind"/>), never a raw
        /// comparison.</param>
        public static double DisplayFrac(long startMs, long endMs, long positionMs, bool isBehind)
            => isBehind ? Frac(startMs, endMs, positionMs) : 1.0;

        /// <summary>The position a rail fraction commits to, in ms on the broadcast's own clock — the inverse of
        /// <see cref="Frac"/>. Clamped INTO the window at both ends, because a seek even a millisecond outside it is a
        /// request the source rejects (and, at the right end, one that can wedge a live session against a moving
        /// edge). A zero-width window commits to its own single position, which makes "scrub a station with no DVR" a
        /// no-op rather than an error.</summary>
        public static long Seek(long startMs, long endMs, double frac)
        {
            if (endMs <= startMs) return startMs;
            double f = double.IsNaN(frac) ? 0 : frac <= 0 ? 0 : frac >= 1 ? 1 : frac;
            long span = endMs - startMs;
            return startMs + (long)Math.Round(f * span);
        }

        /// <summary><see cref="Frac"/> against a whole <see cref="LiveWindow"/> (its own start/end/position).</summary>
        public static double Frac(in LiveWindow w) => Frac(w.SeekableStartMs, w.SeekableEndMs, w.PositionMs);

        /// <summary><see cref="Seek"/> against a whole <see cref="LiveWindow"/>.</summary>
        public static long Seek(in LiveWindow w, double frac) => Seek(w.SeekableStartMs, w.SeekableEndMs, frac);

        /// <summary>How far behind the live edge a rail fraction would land, in ms — what the "GO LIVE −m:ss" label
        /// reads while a drag is in flight, before anything is committed.</summary>
        public static long BehindAt(in LiveWindow w, double frac) => Math.Max(0, w.LiveEdgeMs - Seek(in w, frac));
    }

    /// <summary>Clock formatting for the transport's time labels — the ONE place a millisecond count becomes the
    /// <c>m:ss</c> / <c>h:mm:ss</c> string the player bar, the immersive stage, the queue and the deck all read.
    /// Ported verbatim from <c>_old/Wavee/Backend/Playback/TimeFormat.cs</c>.
    ///
    /// <para>The hours rung is the case that needed pinning: a live broadcast's elapsed-since-tune-in crosses 59:59 on
    /// any stream left running, and an <c>m:ss</c>-only formatter answered "73:04" for it. Hours are never wrapped —
    /// a 100-hour stream reads as 100 hours (parity item 64).</para>
    ///
    /// <para>Digits are written by hand rather than through a composite format string: these labels are rebuilt every
    /// second on the position tick, and the transport is the one surface where a per-tick boxed <c>long</c> and a
    /// culture lookup would be paid forever. The separator is the invariant colon.</para></summary>
    public static class TimeFormat
    {
        /// <summary>One hour, in ms — the rung where the label grows an hours field.</summary>
        public const long HourMs = 3_600_000L;

        /// <summary>A duration as a transport clock: <c>m:ss</c> below one hour, <c>h:mm:ss</c> at or above it (the
        /// minutes field takes a leading zero only once hours are present, which is what makes 1:05:07 read as an hour
        /// and not as "one minute five"). Negative input clamps to 0 — a clock never counts backwards.</summary>
        public static string Clock(long ms)
        {
            if (ms < 0L) ms = 0L;
            long totalSeconds = ms / 1000L;
            long s = totalSeconds % 60L;
            long totalMinutes = totalSeconds / 60L;
            if (ms < HourMs)
                return totalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Two(s);
            long m = totalMinutes % 60L;
            long h = totalMinutes / 60L;
            return h.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Two(m) + ":" + Two(s);
        }

        static string Two(long v) => v < 10L
            ? "0" + v.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── 3. the SMTC timeline coalescer (ch 14 §9, +67) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The newest-wins latch behind the OS bridges' position feed: it collapses a BURST of position ticks into exactly
    /// ONE <c>UpdateTimeline</c> push. Ported verbatim from <c>_old/Wavee/App/SmtcTimelineCoalescer.cs</c> (67 lines),
    /// re-homed here because it is a pure playback rule and ch 14 §9 asks for exactly that.
    ///
    /// <para><b>Why.</b> Every <c>UpdateTimeline</c> is a WinRT activation plus property puts plus a CROSS-PROCESS COM
    /// RPC to the shell (~1 ms). One per second is free; one per queued tick is not. The bridges are fed from
    /// UI-thread posts, so any frame that drains a BACKLOG of position ticks (each carrying a different whole second,
    /// so the steady-state per-second dedupe never fires) used to pay that RPC once per queued tick, synchronously, on
    /// the UI thread — the <c>frame.slow</c> signature. This turns N ticks in one drain into N field writes plus one
    /// deferred flush (ch 14 non-negotiable 2).</para>
    ///
    /// <para><b>Two coalescers, not one shared instance</b> (ch 14 §9): SMTC and the taskbar each own a FIELD of this
    /// type. They bail out on different conditions, and one shared latch would let one bridge consume the other's
    /// flush — leaving that surface frozen with no error anywhere.</para>
    ///
    /// <para><b>Cost.</b> A POD struct with four fields — no allocation on the per-tick path, and none on the flush
    /// path either. Dependency-free (<c>System</c> only) so the rule is unit-testable without a media session.</para>
    /// </summary>
    public struct SmtcTimelineCoalescer
    {
        long _pendingMs;   // newest position latched since the last flush
        long _lastSec;     // last whole second actually pushed to the OS (the steady-state dedupe)
        bool _hasLast;     // has _lastSec ever been written? (default(struct) must not dedupe against second 0)
        bool _flushQueued; // a flush is scheduled and not yet consumed

        /// <summary>True while a flush has been scheduled and not yet consumed by <see cref="TryTake"/>.</summary>
        public readonly bool FlushQueued => _flushQueued;

        /// <summary>Record a position tick. NEWEST WINS — an unconsumed pending value is simply overwritten, so a
        /// burst of N ticks costs N field writes and no OS call. Returns true IFF the caller must SCHEDULE a flush (no
        /// flush is outstanding); every subsequent tick of the same burst returns false, which is what makes the burst
        /// cost exactly one <c>UpdateTimeline</c>.</summary>
        public bool Push(long positionMs)
        {
            _pendingMs = positionMs;
            if (_flushQueued) return false;
            _flushQueued = true;
            return true;
        }

        /// <summary>Consume the latch — ALWAYS clears the scheduled-flush bit (ch 14 non-negotiable 3), so a caller
        /// that bails out (a disposed bridge, no session, a zero duration) cannot wedge the latch armed forever.
        /// Returns true, with the clamped position, IFF the OS timeline actually has to be pushed: a non-positive
        /// duration and a whole-second value equal to the last pushed one are both dropped here.</summary>
        public bool TryTake(long durationMs, out long positionMs)
        {
            _flushQueued = false;
            positionMs = 0;
            if (durationMs <= 0) return false;
            long pos = Math.Clamp(_pendingMs, 0, durationMs);
            long sec = pos / 1000;
            if (_hasLast && sec == _lastSec) return false;
            _hasLast = true;
            _lastSec = sec;
            positionMs = pos;
            return true;
        }
    }

    // ── 4. the pure host gates (ported from _old/Wavee/App + SpotifyLive/Audio) ──────────────────────────────────────

    /// <summary>The PURE decision rules for the ONE current media's host swap: which kind a playable is, whether a
    /// change reloads the current host or swaps hosts, whether a crossfade is allowed across the boundary, what
    /// <c>track_player</c> Connect should report, and whether the outgoing host must be stopped first. Ported from
    /// <c>_old/Wavee/App/MediaSwitchLogic.cs</c>.
    ///
    /// <para>0.2.9's <c>HasVideoMetadata</c> / <c>StampVideoAssociation</c> are deliberately NOT ported: both took an
    /// <c>IReadOnlyDictionary&lt;string,string&gt;</c> of wire metadata, and 0.3's cluster decode folds that map to
    /// typed fields on the way in (<c>Spotify.Decode.ClusterTrack</c>) — re-introducing the dictionary here would
    /// re-introduce the per-row allocation the decode exists to remove.</para></summary>
    public static class MediaSwitch
    {
        /// <summary>Classify the current media into the ONE kind that selects its host. A video track is always
        /// <see cref="PlayableKind.Video"/> regardless of origin (video wins over local); otherwise a local file is
        /// <see cref="PlayableKind.LocalFile"/>; everything else is <see cref="PlayableKind.Audio"/>.</summary>
        public static PlayableKind KindOf(bool isVideoTrack, bool isLocalFile)
            => isVideoTrack ? PlayableKind.Video
             : isLocalFile ? PlayableKind.LocalFile
             : PlayableKind.Audio;

        /// <summary>What the current-media owner should do to honour a change from one playable to another.</summary>
        public enum SwitchAction : byte
        {
            /// <summary>Same kind → the host is unchanged; just re-load the new playable onto it.</summary>
            LoadOnCurrent,
            /// <summary>Different kind → stop the outgoing host, swap for the new kind's host, then load.</summary>
            SwapThenLoad,
        }

        /// <inheritdoc cref="SwitchAction"/>
        public static SwitchAction Decide(PlayableKind current, PlayableKind next)
            => current == next ? SwitchAction.LoadOnCurrent : SwitchAction.SwapThenLoad;

        /// <summary>Whether a crossfade / prepared-next transition is allowed across this boundary. Crossfade is an
        /// AUDIO-only, same-kind capability; every cross-kind boundary and every video boundary is a HARD CUT.</summary>
        public static bool AllowCrossfade(PlayableKind from, PlayableKind to)
            => from == to && from == PlayableKind.Audio;

        /// <summary>The Connect <c>track_player</c> metadata value: <c>"video"</c> for <see cref="PlayableKind.Video"/>,
        /// else <c>"audio"</c> (a local file plays through the audio host and therefore also reports audio).</summary>
        public static string TrackPlayer(PlayableKind kind) => kind == PlayableKind.Video ? "video" : "audio";

        /// <summary>Whether the outgoing host must be stopped BEFORE the new one starts. True on any kind change so
        /// two decoders never both output audio at once.</summary>
        public static bool ShouldStopOutgoingHost(PlayableKind current, PlayableKind next) => current != next;

        /// <summary>Whether the ONE current-media HOST INSTANCE actually changes. <see cref="PlayableKind.Audio"/> and
        /// <see cref="PlayableKind.LocalFile"/> share the SAME host — only a <see cref="PlayableKind.Video"/> boundary
        /// flips to (or away from) the video host, so an Audio↔LocalFile change stays a same-host reload and keeps the
        /// fast-start / prepared-next path untouched.</summary>
        public static bool HostChanges(PlayableKind current, PlayableKind next)
            => (current == PlayableKind.Video) != (next == PlayableKind.Video);
    }

    /// <summary>What the audio host does with a seek request, decided against the state of the serialized pump at the
    /// moment the seek op RUNS (never earlier — the answer depends on ops that ran before it).</summary>
    public enum SeekAdmission : byte
    {
        /// <summary>Hand the seek to the engine now: a session is open and its byte source can serve the target.</summary>
        ApplyNow,
        /// <summary>Park the target and apply it after the next body attach / session open — the engine's seek would
        /// otherwise block the pump waiting for bytes that only a LATER pump op can attach.</summary>
        Defer,
    }

    /// <summary>The pure decision behind the audio host's seek. It exists because of one deadlock: the controller
    /// enqueues <c>LoadFastStart</c> → <c>Seek(resumePositionMs)</c> → (later) <c>SupplyBody</c> onto ONE serialized
    /// pump, and the engine's seek holds its replacement gate until the decode producer has PCM at the target. A
    /// fast-start session owns only the ~80 KB clear head; a target beyond it makes the decoder block waiting for the
    /// body attach that is the very next op in the pump, BEHIND the seek. Nothing completes; the bar buffers forever.
    /// A launch restore at a saved position and a video→audio swap mid-track both take this path.</summary>
    public static class SeekGate
    {
        /// <param name="hasSession">An engine session is open (a deferred-open load has none until its body attaches).</param>
        /// <param name="sourceCanServeBeyondHead">The active byte source can read past its clear head: a Spotify stream
        /// with its body attached, or a source that never had a head/body split (local file, module stream).</param>
        public static SeekAdmission Decide(bool hasSession, bool sourceCanServeBeyondHead)
            => hasSession && sourceCanServeBeyondHead ? SeekAdmission.ApplyNow : SeekAdmission.Defer;

        /// <summary>The position the host should REPORT while a seek is parked: the parked TARGET. The session's own
        /// clock still reads the pre-seek position (0 for a fresh load), and publishing that would show 0:00 for a
        /// track the user resumed at 3:45. A negative <paramref name="pendingSeekMs"/> means nothing is parked.</summary>
        public static long ReportedPositionMs(long pendingSeekMs, long clockPositionMs)
            => pendingSeekMs >= 0 ? pendingSeekMs : clockPositionMs;
    }

    /// <summary>The buffering-bar-on-a-paused-restored-track fix. A launch-recovery restore loads the current track
    /// PAUSED so the bar can show it at its saved position; the host announced Prebuffering/Buffering anyway while
    /// attaching the head and body — work it does whether or not anyone asked to HEAR it — and with no Play() ever
    /// called the ticker that retires the flag never started. The indeterminate bar latched until the user pressed
    /// play.</summary>
    public static class PlayIntentGate
    {
        /// <summary>A load nobody asked to hear announces nothing: buffering while attaching is expected work, not a
        /// state the UI needs to show. Once there IS play intent every buffering signal is real.</summary>
        public static bool ShouldAnnounceBuffering(bool playIntent) => playIntent;
    }

    /// <summary>Frame-domain arithmetic for the gapless join, in ONE place. A session's sample clock counts frames
    /// consumed since THAT session was built (a seek rebases the position clock, never the sample clock; a
    /// device-format soft reload builds a NEW session at clock 0 and seeks it to the saved playhead). Every writer of
    /// the active track's natural-end frame therefore expresses it as "clock now + frames still to play", never as
    /// "frames from track start" — computing it track-absolute once at open is what scheduled a mid-track reopen's
    /// join hundreds of seconds into the future.</summary>
    public static class GaplessJoinClock
    {
        public static long MsToFrames(long ms, int rate) => ms * rate / 1000L;

        /// <summary>The active track's natural-end frame, on the session clock, given where the playhead is now.</summary>
        public static long JoinFrameFor(long sampleClockNow, long durationMs, long playheadMs, int rate)
            => sampleClockNow + MsToFrames(Math.Max(0L, durationMs - playheadMs), rate);

        /// <summary>Where to start the next voice: never in the past, and never further out than the active track's own
        /// remaining time + 100 ms — a stale estimate degrades to a ≤100 ms butt-join instead of a 164 s stall.</summary>
        public static long ScheduleJoin(long activeJoinFrame, long sampleClockNow, long remainingMs, int rate)
        {
            long join = Math.Max(activeJoinFrame, sampleClockNow);
            long bound = sampleClockNow + MsToFrames(Math.Max(0L, remainingMs), rate) + rate / 10;
            return Math.Min(join, bound);
        }

        /// <summary>A primed voice is only spliceable into a mixer running at the rate it was resampled for.</summary>
        public static bool PrimedSlotMatches(int primedMixRate, int sessionRate) => primedMixRate == sessionRate;

        /// <summary>May the join commit right now? Never into a session a soft reload may replace, and never while the
        /// reported playhead is a stale 0 — with a stale playhead "duration − position" reads as "the whole track
        /// remains" and the join is scheduled off a clock that is not this track's.</summary>
        public static bool CanCommit(bool clockStale, bool softReloading) => !clockStale && !softReloading;
    }

    /// <summary>What the host does with the live session after the engine swapped the output sink underneath it.</summary>
    public enum DeviceRecoveryAction : byte
    {
        /// <summary>Rate changed, body reopenable: build a NEW session/graph at the live rate, restore the playhead.</summary>
        ReopenNewGraph,
        /// <summary>Same rate, reopenable body: the existing graph can still render on the new sink.</summary>
        AdoptIntoExistingGraph,
        /// <summary>Same rate, body not reopenable: keep the session and make sure it is audible (a swap can leave the
        /// transport parked).</summary>
        KeepSession,
        /// <summary>Rate changed, body not reopenable: the session stays SILENT and the host cannot rebuild in place —
        /// surface an honest fault so the reducer records it and the bar offers Retry.</summary>
        ReloadThroughController,
    }

    /// <summary>The pure decision behind a device-format recovery. A rate-changed sink rebuild latches "requires graph
    /// rebuild" one-way — the mixer/decoder graph bound at prepare time cannot render on the new device and the
    /// session stays SILENT until a new graph exists — so "leave the old session playing" is only a benign no-op for a
    /// SAME-rate swap. Every early exit of the soft reload routes through here, so a rate change ends in an audible
    /// session or a Retry, never in silence until the next track.</summary>
    public static class DeviceRecoveryPlan
    {
        public static DeviceRecoveryAction Decide(bool requiresGraphRebuild, bool canReopen)
            => canReopen
                ? (requiresGraphRebuild ? DeviceRecoveryAction.ReopenNewGraph : DeviceRecoveryAction.AdoptIntoExistingGraph)
                : requiresGraphRebuild ? DeviceRecoveryAction.ReloadThroughController : DeviceRecoveryAction.KeepSession;
    }

    /// <summary>How an OS audio endpoint is NAMED in the picker. Pure strings, so the rule is pinned by a test rather
    /// than by whatever hardware the developer happens to have plugged in (ch 20 §9 asks for it here; the enumeration
    /// service itself stays SHELL, in owner H's <c>Playback.Audio.cs</c>).</summary>
    public static class AudioDeviceNaming
    {
        /// <summary>The short label: the endpoint's own description first, else the friendly name with its trailing
        /// " (adapter)" parenthetical stripped, else the raw name. Null / empty in, null out.</summary>
        public static string? Shorten(string? deviceDesc, string? friendlyName)
        {
            if (!string.IsNullOrWhiteSpace(deviceDesc)) return deviceDesc.Trim();
            if (string.IsNullOrWhiteSpace(friendlyName)) return friendlyName;
            string name = friendlyName.Trim();
            int open = name.LastIndexOf(" (", StringComparison.Ordinal);
            return open > 0 && name.EndsWith(')') ? name[..open] : name;
        }
    }

    // ── 5. the ownership fold, with the fence (C5) ──────────────────────────────────────────────────────────────────
    //
    // WHO OWNS PLAYBACK RIGHT NOW: this device (Us), another Connect device (Foreign), or nobody. Ported from
    // `_old/Wavee/Backend/PlaybackOwnership.cs`, which exists because 0.2.9 had TWO authorities that disagreed — a
    // sticky `_ownsActivePlayback` demoted only on an active-id TRANSITION, and a raw cluster `ActiveDeviceId` plus a
    // 5 s wall-clock "pending" window — plus several is_active writers that asked neither. Every Connect incident of
    // 2026-09-11 (the stray-reload loop, transfer-to-self playing here while the bar said "Playing on iPhone", the
    // PLAY glyph over audible playback, the launch slot steal) was the two of them answering differently.
    //
    // ORDERING. Only SERVER timestamps are ever compared with each other. A local claim cannot be ordered against a
    // cluster push by clocks (the claim is ours, the push is the server's, and a push the server built while our PUT
    // was still in flight carries a server time AFTER our claim yet knows nothing of it). So a claim is judged by the
    // server's own answer to it: the put-state RESPONSE is a Cluster, and its server_timestamp_ms becomes the FENCE.
    // Pushes older than the fence cannot revoke; a push newer than the fence naming another device is a real takeover.
    // The 5 s protection window bounds only the case where that response never arrives.
    //
    // WHAT CHANGED FROM 0.2.9: device ids are `ulong` FNV hashes, not strings. The fold only ever compares identity,
    // and a CORE file may not allocate, resolve an interner or walk a string per dealer push (P8/P9). The host hashes
    // once on the way in (`Playback.DeviceHash`), and the memory rule `connect-ownership-single-authority` is
    // preserved exactly: routing reads THIS verdict, never a raw cluster id.

    /// <summary>One cluster as the ownership fold sees it — the pure half of a <c>Spotify.Decode.ClusterDelta</c>,
    /// with its active-device id already reduced to an identity hash by the host.</summary>
    /// <param name="Origin">A dealer PUSH, or the RESPONSE to one of our put-states.</param>
    /// <param name="PutMsgId">The put-state message id this cluster answers (0 for a push).</param>
    /// <param name="ActiveDevice">The hash of the cluster's active device id; 0 = the cluster names nobody.</param>
    /// <param name="ServerTs">`server_timestamp_ms` — the ONLY clock the fold orders frames by.</param>
    /// <param name="UpdateReason">The push's own reason ordinal, carried for diagnostics.</param>
    /// <param name="ActiveStartedPlayingAt">The active device's started-playing stamp (the newest-starter rule).</param>
    public readonly record struct ClusterFrame(
        ClusterOrigin Origin,
        uint PutMsgId,
        ulong ActiveDevice,
        long ServerTs,
        int UpdateReason = 0,
        long ActiveStartedPlayingAt = 0);

    /// <summary>The whole ownership state, folded into <see cref="State.Own"/>. <see cref="Device"/> is the foreign
    /// owner (Foreign) or the device that just left (Nobody/FromForeign). <see cref="ClaimMsgId"/> is the first
    /// is_active put-state sent for the current claim (0 = not sent yet). <see cref="Fence"/> is the server time at
    /// which the server acknowledged our claim.</summary>
    public struct OwnerState
    {
        public Owner Kind;
        public ulong Device;
        public ClaimPhase Claim;
        public NobodyCause Cause;
        public long ClaimId;
        public uint ClaimMsgId;
        public long ClaimStartedAtMs;
        /// <summary>Frame-clock ms at which the protection window expires. 0 = no window.</summary>
        public long ProtectUntilMs;
        /// <summary>The server time at which the server acknowledged our claim — C5's FENCE.</summary>
        public long Fence;
        /// <summary>The newest server time folded so far — the stale guard (F0).</summary>
        public long LastServerTs;
        /// <summary>A foreign device seen DURING protection; decided at the verdict or at expiry (P2).</summary>
        public ulong LastSeenActive;
        public long LastSeenServerTs;

        public static OwnerState Initial => new() { Kind = Owner.Nobody, Cause = NobodyCause.Launch };

        public readonly bool IsUs => Kind == Owner.Us;
    }

    /// <summary>The fold. Every rule is a pure function of (state, frame, our identity) — no clock, no I/O, no
    /// logging, no string. <see cref="Fold"/> is 0.2.9's <c>PlaybackOwnership.OnCluster</c>, row for row.</summary>
    public static class Ownership
    {
        /// <summary>How long a claim waits for the server's verdict (its put-state response) before deciding on what
        /// it has seen. Bounds a LOST response only; the normal verdict arrives in one round trip.</summary>
        public const long ClaimProtectMs = 5000;

        /// <summary>THE cluster fold — rows F0-F6 (not ours), P1-P4 (claim pending), A1-A3 (claim settled). Mutates
        /// <paramref name="s"/> in place and answers what the SHELL must do.</summary>
        /// <param name="s">The ownership state, folded in place.</param>
        /// <param name="f">The cluster.</param>
        /// <param name="us">Our own device-id hash.</param>
        public static OwnerFx Fold(ref OwnerState s, in ClusterFrame f, ulong us)
        {
            bool isVerdict = s.Kind == Owner.Us && s.Claim == ClaimPhase.Protected
                             && f.Origin == ClusterOrigin.PutResponse
                             && s.ClaimMsgId != 0 && f.PutMsgId >= s.ClaimMsgId;

            // F0 — the ONE stale guard. A slow PUT's response can land after a newer push; folding it would regress
            // the active id. Frames without a server time are never dropped (nothing to order them by).
            if (f.ServerTs > 0 && f.ServerTs < s.LastServerTs)
            {
                // …except that the answer to our CLAIM still ends the wait: it was overtaken by a newer push, which
                // P2 recorded, so THAT push is the truth now. Dropping the verdict instead would sit Protected (and
                // audible) until expiry.
                if (!isVerdict) return OwnerFx.DropFrame;
                if (s.LastSeenActive != 0)
                {
                    ToForeign(ref s, s.LastSeenActive);
                    return OwnerFx.DropFrame | OwnerFx.StopHost | OwnerFx.EmitBecameInactive;
                }
                s.Claim = ClaimPhase.Unadopted;
                s.Fence = s.LastServerTs;
                ClearSeen(ref s);
                return OwnerFx.DropFrame;
            }

            if (f.ServerTs > s.LastServerTs) s.LastServerTs = f.ServerTs;
            ulong active = f.ActiveDevice;
            bool namesUs = active != 0 && active == us;
            bool namesForeign = active != 0 && !namesUs;

            switch (s.Kind)
            {
                case Owner.Nobody:
                    if (namesForeign) { ToForeign(ref s, active); return OwnerFx.StopHost; }            // F3
                    if (namesUs) { s.Cause = NobodyCause.StaleSelf; return OwnerFx.None; }              // F2
                    return OwnerFx.None;                                                                // F1

                case Owner.Foreign:
                    if (namesForeign) { ToForeign(ref s, active); return OwnerFx.StopHost; }            // F4, level-triggered
                    if (namesUs) { ToNobody(ref s, NobodyCause.StaleSelf, 0); return OwnerFx.None; }    // F6
                    ToNobody(ref s, NobodyCause.FromForeign, s.Device);                                 // F5
                    return OwnerFx.None;
            }

            // ── Us ──
            if (s.Claim == ClaimPhase.Protected)
            {
                if (namesUs)                                                                            // P1
                {
                    long fence = f.ServerTs > 0 ? f.ServerTs : s.LastServerTs;
                    s.Claim = ClaimPhase.Adopted;
                    s.Fence = fence;
                    // A push that arrived DURING protection and named another device with a server time past the
                    // fence was a real takeover racing our claim: honour it now rather than waiting for its next
                    // heartbeat.
                    if (s.LastSeenActive != 0 && s.LastSeenServerTs > fence)
                    {
                        ToForeign(ref s, s.LastSeenActive);
                        return OwnerFx.StopHost | OwnerFx.EmitBecameInactive;
                    }
                    ClearSeen(ref s);
                    return OwnerFx.None;
                }
                if (isVerdict && namesForeign)                                                          // P3
                {
                    ToForeign(ref s, active);
                    return OwnerFx.StopHost | OwnerFx.EmitBecameInactive | OwnerFx.ClaimRejected;
                }
                if (isVerdict)                                                                          // P4
                {
                    s.Claim = ClaimPhase.Unadopted;
                    s.Fence = Math.Max(f.ServerTs, s.LastServerTs);
                    ClearSeen(ref s);
                    return OwnerFx.None;
                }
                // P2 — a push (or the answer to a put sent BEFORE the claim) naming someone else may predate our
                // claim: remember it, decide at the verdict or at expiry.
                s.LastSeenActive = active;
                s.LastSeenServerTs = f.ServerTs;
                return OwnerFx.None;
            }

            // ── Adopted / Unadopted ──
            if (namesUs)                                                                                // A1
            {
                s.Claim = ClaimPhase.Adopted;
                if (f.ServerTs > s.Fence) s.Fence = f.ServerTs;
                return OwnerFx.None;
            }
            if (namesForeign && (f.ServerTs == 0 || f.ServerTs > s.Fence))                              // A2
            {
                ToForeign(ref s, active);
                return OwnerFx.StopHost | OwnerFx.EmitBecameInactive;
            }
            if (active == 0 && s.Claim == ClaimPhase.Adopted && f.ServerTs >= s.Fence)                  // A3
            {
                s.Claim = ClaimPhase.Unadopted;
                return OwnerFx.None;
            }
            return OwnerFx.None;
        }

        /// <summary>A claim: an explicit local play/resume, a transfer-to-self fallback, or an inbound command
        /// addressed to us.</summary>
        /// <param name="acknowledged">A publisher is attached that will send the claim and fold its response. Without
        /// one (unit tests, <c>--fake</c>) there is no verdict to wait for and the claim is Unadopted at once.</param>
        /// <param name="nowMs">The frame-clock stamp the protection window is measured from (never a clock read).</param>
        public static OwnerFx Claim(ref OwnerState s, ClaimCause c, long id, long startedAtMs, long nowMs, bool acknowledged)
        {
            if (s.Kind == Owner.Us)
            {
                // Already ours: keep the phase. A NEW playback (not a resume/skip) restamps started_at, which is what
                // the server's newest-starter rule compares across devices.
                bool restamp = c is ClaimCause.UserPlay or ClaimCause.InboundPlay or ClaimCause.InboundTransfer;
                if (restamp) { s.ClaimId = id; s.ClaimStartedAtMs = startedAtMs; }
                return OwnerFx.None;
            }
            long fence = acknowledged ? 0 : s.LastServerTs;
            s.Kind = Owner.Us;
            s.Device = 0;
            s.Claim = acknowledged ? ClaimPhase.Protected : ClaimPhase.Unadopted;
            s.Cause = NobodyCause.Launch;
            s.ClaimId = id;
            s.ClaimMsgId = 0;
            s.ClaimStartedAtMs = startedAtMs;
            s.ProtectUntilMs = acknowledged ? nowMs + ClaimProtectMs : 0;
            s.Fence = fence;
            ClearSeen(ref s);
            return OwnerFx.None;
        }

        /// <summary>Binds the claim to the first is_active put-state sent after it — its response is the verdict.</summary>
        public static void PutSent(ref OwnerState s, uint msgId, bool isActive)
        {
            if (s.Kind == Owner.Us && s.Claim == ClaimPhase.Protected && s.ClaimMsgId == 0 && isActive)
                s.ClaimMsgId = msgId;
        }

        /// <summary>The claim's put-state failed: no verdict will come. Keep playing, Unadopted; the next announce
        /// re-asserts is_active.</summary>
        public static OwnerFx PutFailed(ref OwnerState s, uint msgId)
        {
            if (s.Kind != Owner.Us || s.Claim != ClaimPhase.Protected || s.ClaimMsgId == 0 || msgId != s.ClaimMsgId)
                return OwnerFx.None;
            s.Claim = ClaimPhase.Unadopted;
            s.Fence = s.LastServerTs;
            ClearSeen(ref s);
            return OwnerFx.None;
        }

        /// <summary>Protection expiry (the verdict never arrived): decide on what was seen meanwhile.</summary>
        /// <param name="nowMs">The frame-clock stamp carried by the tick.</param>
        public static OwnerFx Tick(ref OwnerState s, long nowMs)
        {
            if (s.Kind != Owner.Us || s.Claim != ClaimPhase.Protected || nowMs < s.ProtectUntilMs) return OwnerFx.None;
            if (s.LastSeenActive != 0)
            {
                ToForeign(ref s, s.LastSeenActive);
                return OwnerFx.StopHost | OwnerFx.EmitBecameInactive | OwnerFx.ClaimRejected;
            }
            s.Claim = ClaimPhase.Unadopted;
            s.Fence = s.LastServerTs;
            return OwnerFx.None;
        }

        /// <summary>We give playback up. PAUSE NEVER RELEASES — ownership is not audibility.</summary>
        public static OwnerFx Release(ref OwnerState s, ReleaseCause c)
        {
            if (s.Kind != Owner.Us) return OwnerFx.None;
            ToNobody(ref s, NobodyCause.FromUs, 0);
            return c is ReleaseCause.TransferAway or ReleaseCause.Logout
                ? OwnerFx.StopHost | OwnerFx.EmitBecameInactive | OwnerFx.PublishInactive
                : OwnerFx.PublishInactive;
        }

        /// <summary>Local unless another device owns playback (then every transport verb forwards to it). THE routing
        /// authority — nothing anywhere may route off a raw cluster id.</summary>
        public static bool RoutesLocal(in OwnerState s) => s.Kind != Owner.Foreign;

        /// <summary>The wire's is_active — its ONE writer.</summary>
        public static bool IsActiveOnWire(in OwnerState s) => s.Kind == Owner.Us;

        /// <summary>May the host load media for <paramref name="origin"/>? Foreign: never. Nobody: only a PAUSED
        /// restore (launch recovery seeds without claiming). Us: always.</summary>
        public static bool AllowsLoad(in OwnerState s, LoadOrigin origin, bool paused) => s.Kind switch
        {
            Owner.Us => true,
            Owner.Nobody => origin == LoadOrigin.Restore && paused,
            _ => false,
        };

        /// <summary>Does now-playing show the LOCAL session (vs the cluster's mirrored row)? A departed foreign
        /// device's snapshot stays on screen (paused) until the user acts — no flip to a stale local session on a
        /// phone flap.</summary>
        public static bool ShowsLocalNowPlaying(in OwnerState s, bool hasLocalSession) => s.Kind switch
        {
            Owner.Us => true,
            Owner.Nobody => s.Cause != NobodyCause.FromForeign && hasLocalSession,
            _ => false,
        };

        static void ToForeign(ref OwnerState s, ulong device)
        {
            s.Kind = Owner.Foreign;
            s.Device = device;
            s.Claim = ClaimPhase.None;
            s.ClaimMsgId = 0;
            s.ClaimStartedAtMs = 0;
            s.ProtectUntilMs = 0;
            s.Fence = 0;
            ClearSeen(ref s);
        }

        static void ToNobody(ref OwnerState s, NobodyCause cause, ulong departed)
        {
            s.Kind = Owner.Nobody;
            s.Device = departed;
            s.Claim = ClaimPhase.None;
            s.Cause = cause;
            s.ClaimMsgId = 0;
            s.ClaimStartedAtMs = 0;
            s.ProtectUntilMs = 0;
            s.Fence = 0;
            ClearSeen(ref s);
        }

        static void ClearSeen(ref OwnerState s) { s.LastSeenActive = 0; s.LastSeenServerTs = 0; }
    }

    // ── 6. the state ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the remote's cluster said is playing THERE — the mirror the bar paints while another device owns
    /// playback, decoded to typed fields by the host so the core never touches a <see cref="TextRef"/>.</summary>
    /// <param name="HasTrack">Did the cluster name a current track at all?</param>
    /// <param name="Track">Its identity (<c>default</c> when the uri is not one we can pack).</param>
    /// <param name="IsPlaying">The remote's own is_playing.</param>
    /// <param name="IsPaused">The remote's own is_paused.</param>
    /// <param name="IsBuffering">The remote's own is_buffering.</param>
    /// <param name="PositionAsOfMs">Its position at <paramref name="TimestampMs"/>.</param>
    /// <param name="TimestampMs">The server clock that position was true at.</param>
    /// <param name="DurationMs">The remote's duration for that row.</param>
    /// <param name="Shuffling">Its shuffle flag.</param>
    /// <param name="Repeat">Its repeat mode.</param>
    /// <param name="Volume">The ACTIVE device's volume, 0..65535 (-1 = the cluster stated none).</param>
    /// <param name="NoPrev">Restriction: previous is disallowed (a non-empty <c>disallow_skipping_prev</c>).</param>
    /// <param name="NoNext">Restriction: next is disallowed.</param>
    /// <param name="NoSeek">Restriction: seeking is disallowed.</param>
    public readonly record struct RemoteState(
        bool HasTrack,
        EntityId Track,
        bool IsPlaying,
        bool IsPaused,
        bool IsBuffering,
        long PositionAsOfMs,
        long TimestampMs,
        long DurationMs,
        bool Shuffling,
        RepeatMode Repeat,
        int Volume,
        bool NoPrev,
        bool NoNext,
        bool NoSeek);

    /// <summary>THE playback state. One value, written on the UI thread only (C1), copied freely, and the single
    /// source every playback surface binds through the host's signals.
    ///
    /// <para><b>Why <see cref="Current"/> is an <see cref="EntityRef"/> and not the sketch's <c>Track</c></b> — a
    /// deliberate divergence from plan §4.7, recorded here rather than left to a reader: Wave 1 landed the queue
    /// CROSS-KIND, and <c>Queue.TryAdvance</c> hands back an <see cref="EntityRef"/> because track slot 5 and episode
    /// slot 5 are the same <c>int</c> (<c>Entities/Queue.cs</c>'s header box). A <c>Track</c>-typed current row would
    /// re-introduce exactly the aliasing that decision removed. <see cref="CurrentId"/> rides beside it so the bar,
    /// the SMTC card and the PUT body read an identity without a table lookup — and so a foreign device's row, which
    /// has no slot in any table yet, still paints.</para></summary>
    public struct State
    {
        // ── what is playing ──
        /// <summary>The current playable, cross-kind (track OR episode). <see cref="EntityRef.IsNone"/> when idle.</summary>
        public EntityRef Current;
        /// <summary>Its identity. Survives a row the catalog has not fetched yet (a foreign device's track).</summary>
        public EntityId CurrentId;
        /// <summary>Which host runs it (<see cref="MediaSwitch.KindOf"/>).</summary>
        public PlayableKind Kind;
        /// <summary>The context it is playing FROM — a playlist, an album, a station. The art tile's route.</summary>
        public EntityId Context;
        /// <summary>Where in <c>Edges.Queue</c> the session is (bucket, index). A VALUE, re-validated, never a row
        /// pointer: the same recording may legitimately sit in the queue twice.</summary>
        public QueueCursor Cursor;

        // ── the transport ──
        public Phase Phase;
        /// <summary>Refilling. NOT a phase: a buffering stream is the playing one.</summary>
        public bool Buffering;
        /// <summary>Why the current playable is not playing. <see cref="Fault.None"/> arms the transport.</summary>
        public Fault Error;
        /// <summary>A recoverable interruption in progress — ch 20 W10's "Reconnecting" band reads it.</summary>
        public RecoveryKind Recovery;
        /// <summary>The last AUTHORITATIVE position, in ms.</summary>
        public int PosMs;
        /// <summary>The frame-clock stamp, in ms, at which <see cref="PosMs"/> was true — so the bar can extrapolate
        /// between reports (<see cref="Position"/>).
        /// <para><b>Unit note.</b> Plan §4.7 spells this <c>PosQpc</c>; the NAME is kept, the UNIT is milliseconds of
        /// the host's frame clock. The core cannot divide raw QPC ticks by a frequency it is forbidden to read, and
        /// the host already converts once per drain. The memory rule stands either way: this is FRAME time, never
        /// <c>Environment.TickCount64</c>.</para></summary>
        public long PosQpc;
        /// <summary>The current playable's duration in ms as the source stated it. 0 = unknown, and the seek bar is
        /// DISABLED rather than drawn as a full grey rail (ch 20 §7).</summary>
        public int DurationMs;
        /// <summary>0..1, LINEAR — the slider's own scale. The cubic taper is the audio host's
        /// (<c>Playback.Audio.VolumeTaper</c>), and the wire scale is the snapshot's.</summary>
        public float Volume;
        public bool Shuffle;
        public RepeatMode Repeat;
        /// <summary>Restrictions as the cluster stated them. The bar greys its buttons off
        /// <see cref="CanSkipNext"/> / <see cref="CanSkipPrev"/> / <see cref="CanSeek"/>, never off these directly.</summary>
        public bool NoNext, NoPrev, NoSeek;
        /// <summary>The stream's format badge ("OGG 320", "FLAC 44.1/16"), published by the audio host. CLEARED the
        /// moment a remote device owns playback — we cannot know a phone's format, and 0.2.9 left ours on screen
        /// (ch 21 G8).</summary>
        public StringId StreamFormat;

        // ── live (ch 20 §9) ──
        public LiveWindow Live;
        /// <summary>The DECIDED edge state. Every surface reads <c>Edge.IsBehind</c>; none may re-derive it.</summary>
        public LiveEdgeState Edge;
        /// <summary>UNIX ms at which this device tuned in to the current broadcast. <c>&lt;= 0</c> means "not known",
        /// and the label falls back to the reported position — never to 0 (ch 20 §9's trap).</summary>
        public long TunedInAtMs;

        // ── ownership (C5) ──
        public OwnerState Own;
        /// <summary>Our own device-id hash, seeded once by the host. The fold's <c>us</c>.</summary>
        public ulong Us;
        /// <summary>The active Connect device's row in the host's roster, or -1. Owner-DERIVED (there is no
        /// <c>is_active</c> fallback anywhere): the host resolves the hash below to a row.</summary>
        public int ActiveDeviceSlot;

        // ── epochs (C4) ──
        /// <summary>Bumps on every Step that changes what a shell is doing. Everything that leaves the core carries
        /// it, and a result older than the epoch it names is dropped.</summary>
        public uint Epoch;
        /// <summary>The epoch the current load was started for. An <see cref="AudioSignal"/> for any other is stale.</summary>
        public uint LoadEpoch;
        /// <summary>The epoch of the transfer in flight.</summary>
        public uint TransferEpoch;
        /// <summary>How many claims/announces this session has minted. Diagnostics; the wire's message id is the
        /// host's, because the glue owns the debounce that decides how many PUTs actually go out.</summary>
        public uint PublishSeq;

        // ── the connect bookkeeping the PUT body carries ──
        public long StartedPlayingAtMs;
        public long HasBeenPlayingForMs;

        // ── reads ──
        /// <summary>The ownership verdict, Us / Foreign / Nobody.</summary>
        public readonly Owner Owner => Own.Kind;
        /// <summary>The server-timestamp FENCE (C5): a push older than this cannot revoke.</summary>
        public readonly long Fence => Own.Fence;
        /// <summary>The device that owns playback: us, the foreign owner, or the one that just left. 0 = none.</summary>
        public readonly ulong ActiveDevice => Own.Kind == Owner.Us ? Us : Own.Device;
        /// <summary>Audible right now.</summary>
        public readonly bool IsPlaying => Phase == Phase.Playing;
        /// <summary>Anything at all on the deck.</summary>
        public readonly bool HasCurrent => !CurrentId.IsEmpty;
        /// <summary>Do transport verbs run locally, or forward to the owner?</summary>
        public readonly bool RoutesLocal => Ownership.RoutesLocal(in Own);
        /// <summary>Is the transport armed at all? Ch 20 §7's <c>canTransport</c>.</summary>
        public readonly bool CanTransport => HasCurrent && Error == Fault.None;
        /// <summary>May the seek bar be dragged? While LIVE this is exactly <see cref="LiveWindow.HasWindow"/> — a
        /// seekable span under 30 s is not a window, and the rail becomes the breathing line.</summary>
        public readonly bool CanSeek => CanTransport && !NoSeek && Phase != Phase.Loading
            && (Live.IsLive ? Live.HasWindow : DurationMs > 0);
        /// <summary>May Next fire? The FOLDED answer; the raw restriction bits are not the UI's business.</summary>
        public readonly bool CanSkipNext => CanTransport && !NoNext;
        /// <summary>May Previous fire?</summary>
        public readonly bool CanSkipPrev => CanTransport && !NoPrev;

        /// <summary>The position to PAINT at <paramref name="nowMs"/> (frame clock): the last authoritative report
        /// extrapolated while playing, and the report itself otherwise. Clamped into the duration when one is known —
        /// a bar that runs past the end of its own rail is the tick-overrun defect 0.2.9 shipped.</summary>
        public readonly int Position(long nowMs)
        {
            if (Phase != Phase.Playing) return PosMs;
            long p = PosMs + (nowMs - PosQpc);
            if (p < 0) p = 0;
            if (DurationMs > 0 && p > DurationMs) p = DurationMs;
            return (int)p;
        }

        public static State Initial => new()
        {
            Volume = 1f,
            Own = OwnerState.Initial,
            Cursor = QueueCursor.None,
            ActiveDeviceSlot = -1,
        };
    }

    // ── 7. the inputs (C2) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything that can move playback. One flat enum, because the core is one switch and a hierarchy of
    /// command objects is the allocation this design exists to delete.</summary>
    public enum InputKind : byte
    {
        None = 0,
        /// <summary>Start a specific playable (a row click, a deep link, a restore).</summary>
        Play,
        Pause,
        Resume,
        /// <summary>A COMMITTED seek (<c>IntArg</c> = ms). A scrub preview never reaches the core.</summary>
        Seek,
        Next,
        Prev,
        /// <summary><c>IntArg</c> = 0..<see cref="MaxWireVolume"/>.</summary>
        SetVolume,
        SetShuffle,
        SetRepeat,
        /// <summary>Move playback to another device (<c>LongArg</c> = its hash, <c>IntArg</c> = its roster slot).</summary>
        Transfer,
        /// <summary>A cluster — a dealer push or a put-state response. Carries <see cref="Input.Frame"/> +
        /// <see cref="Input.Remote"/>.</summary>
        Cluster,
        /// <summary>A controller's verb addressed to us.</summary>
        RemoteCommand,
        /// <summary>The pump reporting. <c>IntArg</c> = <see cref="AudioSignal"/>, <c>Epoch</c> = its load epoch.</summary>
        AudioSignal,
        /// <summary>The stream ran out (<c>Epoch</c> = its load epoch).</summary>
        Ended,
        /// <summary>A transfer we sent has completed (<c>Epoch</c> = its transfer epoch, <c>IntArg</c> != 0 = ok).</summary>
        TransferDone,
        /// <summary>A put-state was SENT (<c>IntArg</c> = message id, <c>LongArg</c> != 0 = it claimed is_active).</summary>
        PutStateSent,
        /// <summary>The put-state round trip ENDED: <c>IntArg</c> = message id, <c>LongArg</c> != 0 = accepted. The
        /// accepted case's cluster body arrives separately as a <see cref="Cluster"/> of origin <c>PutResponse</c>,
        /// because the fence lives on that cluster's server timestamp and nowhere else (C5); THIS input is the
        /// failure half, so a lost response still ends the wait.</summary>
        PutStateVerdict,
        /// <summary>The active device disappeared from the roster.</summary>
        DeviceLost,
        /// <summary>We are giving playback up (<c>IntArg</c> = <see cref="ReleaseCause"/>).</summary>
        Release,
        /// <summary>Stop the deck without releasing ownership (<c>IntArg</c> = <see cref="StopReason"/>).</summary>
        Stop,
        /// <summary>The machine is going to sleep.</summary>
        Suspend,
        /// <summary>…and waking again. (Plan §4.7 spells it <c>Resume_</c> because <see cref="Resume"/> is the
        /// transport verb; the spelling is kept so the two can never be confused at a call site.)</summary>
        Resume_,
        /// <summary>The 1 s position ticker (P10). <c>NowMs</c> is the frame-clock stamp.</summary>
        Tick,
        /// <summary>A live-window report from the media pipeline (<see cref="Input.Live"/>, <c>LongArg</c> = the unix
        /// ms to stamp <see cref="State.TunedInAtMs"/> with on the first live report).</summary>
        LiveReport,
        /// <summary>GO LIVE: a committed seek to the edge that ALSO resets the hysteresis machine outright.</summary>
        GoLive,
        /// <summary>The duration became known or changed (<c>IntArg</c> = ms, <c>Epoch</c> = its load epoch).</summary>
        Duration,
        /// <summary>The audio host published a format badge (<c>LongArg</c> = the <see cref="StringId"/> value).</summary>
        Format,
        /// <summary>A recoverable interruption started or ended (<c>IntArg</c> = <see cref="RecoveryKind"/>).</summary>
        Recovery,
    }

    /// <summary>One input — a VALUE (C2). A plain <c>readonly struct</c> and NOT a <c>record struct</c> on purpose:
    /// a record's synthesized <c>Equals</c> would reach <c>ValueType.Equals</c> for the embedded protocol structs and
    /// box them, which is precisely the hidden allocation P9 forbids. Nothing compares two inputs anyway.</summary>
    public readonly struct Input
    {
        public readonly InputKind Kind;
        /// <summary>The row to play (<see cref="InputKind.Play"/>).</summary>
        public readonly EntityRef Row;
        /// <summary>Its identity.</summary>
        public readonly EntityId Id;
        /// <summary>The context to play from.</summary>
        public readonly EntityId Context;
        public readonly QueueCursor Cursor;
        public readonly int IntArg;
        public readonly long LongArg;
        /// <summary>The epoch this result was started for (C4). 0 on a user input.</summary>
        public readonly uint Epoch;
        /// <summary>The frame-clock stamp, in ms. The core reads NO clock; every input that needs one carries it.</summary>
        public readonly long NowMs;
        public readonly ClusterFrame Frame;
        public readonly RemoteState Remote;
        public readonly RemoteCommand Command;
        public readonly LiveWindow Live;
        public readonly PlayableKind PlayKind;

        public Input(InputKind kind, EntityRef row = default, EntityId id = default, EntityId context = default,
            QueueCursor cursor = default, int intArg = 0, long longArg = 0, uint epoch = 0, long nowMs = 0,
            ClusterFrame frame = default, RemoteState remote = default, RemoteCommand command = default,
            LiveWindow live = default, PlayableKind playKind = PlayableKind.Audio)
        {
            Kind = kind; Row = row; Id = id; Context = context; Cursor = cursor;
            IntArg = intArg; LongArg = longArg; Epoch = epoch; NowMs = nowMs;
            Frame = frame; Remote = remote; Command = command; Live = live; PlayKind = playKind;
        }

        // ── the named constructors every call site should use ───────────────────────────────────────────────────────

        public static Input Play(EntityRef row, EntityId id, EntityId context, QueueCursor cursor,
            PlayableKind kind = PlayableKind.Audio, int fromMs = 0, long nowMs = 0)
            => new(InputKind.Play, row, id, context, cursor, fromMs, nowMs: nowMs, playKind: kind);

        public static Input Next(long nowMs = 0) => new(InputKind.Next, nowMs: nowMs);
        public static Input Prev(long nowMs = 0) => new(InputKind.Prev, nowMs: nowMs);
        public static Input Pause(long nowMs = 0) => new(InputKind.Pause, nowMs: nowMs);
        public static Input Resume(long nowMs = 0) => new(InputKind.Resume, nowMs: nowMs);
        public static Input Seek(int ms, long nowMs = 0) => new(InputKind.Seek, intArg: ms, nowMs: nowMs);
        public static Input Volume(float value) => new(InputKind.SetVolume, intArg: WireVolume(value));
        public static Input Shuffle(bool on) => new(InputKind.SetShuffle, intArg: on ? 1 : 0);
        public static Input Repeat(RepeatMode mode) => new(InputKind.SetRepeat, intArg: (int)mode);
        public static Input Transfer(ulong device, int rosterSlot = -1, long nowMs = 0)
            => new(InputKind.Transfer, intArg: rosterSlot, longArg: (long)device, nowMs: nowMs);
        public static Input Cluster(in ClusterFrame frame, in RemoteState remote, long nowMs = 0)
            => new(InputKind.Cluster, frame: frame, remote: remote, nowMs: nowMs);
        /// <summary>A controller's verb. (Named <c>Controller</c> and not <c>Remote</c> because
        /// <see cref="Input.Remote"/> is already the mirrored remote STATE field on this same value.)</summary>
        public static Input Controller(in RemoteCommand command, long nowMs = 0)
            => new(InputKind.RemoteCommand, command: command, nowMs: nowMs);
        public static Input Audio(AudioSignal signal, uint epoch, long nowMs = 0, long arg = 0)
            => new(InputKind.AudioSignal, intArg: (int)signal, epoch: epoch, nowMs: nowMs, longArg: arg);
        public static Input Ended(uint epoch, long nowMs = 0) => new(InputKind.Ended, epoch: epoch, nowMs: nowMs);
        public static Input TransferDone(uint epoch, bool ok) => new(InputKind.TransferDone, intArg: ok ? 1 : 0, epoch: epoch);
        public static Input PutSent(uint messageId, bool isActive)
            => new(InputKind.PutStateSent, intArg: (int)messageId, longArg: isActive ? 1 : 0);
        public static Input PutVerdict(uint messageId, bool accepted)
            => new(InputKind.PutStateVerdict, intArg: (int)messageId, longArg: accepted ? 1 : 0);
        public static Input Tick(long nowMs) => new(InputKind.Tick, nowMs: nowMs);
        public static Input LiveReport(in LiveWindow window, long unixMs = 0)
            => new(InputKind.LiveReport, live: window, longArg: unixMs);
        public static Input GoLive(long nowMs = 0) => new(InputKind.GoLive, nowMs: nowMs);
        public static Input Duration(int ms, uint epoch) => new(InputKind.Duration, intArg: ms, epoch: epoch);
        public static Input Format(StringId badge, uint epoch) => new(InputKind.Format, longArg: badge.Value, epoch: epoch);
        public static Input Recovering(RecoveryKind kind, uint epoch) => new(InputKind.Recovery, intArg: (int)kind, epoch: epoch);
        public static Input Release(ReleaseCause cause) => new(InputKind.Release, intArg: (int)cause);
        public static Input Stop(StopReason why) => new(InputKind.Stop, intArg: (int)why);
        public static Input DeviceLost() => new(InputKind.DeviceLost);

        /// <summary>0..1 → the Connect wire's 0..65535, rounded the way the cluster rounds it back.</summary>
        public static int WireVolume(float value) => (int)Math.Round(Math.Clamp(value, 0f, 1f) * MaxWireVolume);
    }

    // ── 8. the effects (C3) ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fixed effect SLOTS — one per effect KIND, never a list. The last write inside a drain wins and the
    /// host's <c>Execute</c> reads them once, which is the whole of C3: ten <see cref="InputKind.Next"/> clicks
    /// produce ten <see cref="Step"/> calls and exactly ONE <see cref="Load"/>, carrying the LAST epoch. Every slot
    /// that leaves the process carries its epoch so the shell can cancel what it has superseded (C4).</summary>
    public struct Effects
    {
        // ── load ──
        public bool Load;
        public EntityRef LoadRow;
        public EntityId LoadId;
        public PlayableKind LoadKind;
        public uint LoadEpoch;
        public int LoadFromMs;
        public LoadOrigin LoadWhy;

        // ── transport ──
        public bool Start, Stop, PauseHost, ResumeHost;
        public StopReason StopWhy;
        public uint TransportEpoch;

        // ── seek ──
        public bool Seek;
        public int SeekMs;
        public uint SeekEpoch;

        // ── volume ──
        public bool Volume;
        public float VolumeValue;
        public uint VolumeEpoch;

        // ── gapless ──
        public bool PrepareNext;
        public EntityRef NextRow;
        public EntityId NextId;

        // ── connect: announce ──
        public bool PublishState;
        public PublishReason PublishWhy;
        public bool PublishActive;
        public uint PublishEpoch;

        // ── connect: a verb for another device ──
        public bool SendRemote;
        public ulong RemoteDevice;
        public int RemoteDeviceSlot;
        public RemoteCmd RemoteCmd;
        /// <summary>True when the verb is the VOLUME route (its own PUT with a protobuf body), not a player command.</summary>
        public bool RemoteIsVolume;
        public long RemoteArg;
        public bool RemoteFlag;
        public uint RemoteEpoch;

        // ── connect: a transfer ──
        public bool Transfer;
        public ulong TransferTo;
        public int TransferSlot;
        public uint TransferEpoch;

        // ── catalog: the row we are about to play has no slot yet ──
        public bool Fetch;
        public EntityId FetchId;
        public uint FetchEpoch;

        // ── os: the SMTC / taskbar surfaces ──
        /// <summary>The whole card changed (track, phase, buttons) — a full push.</summary>
        public bool Smtc;
        /// <summary>Only the timeline moved — latch it into the two <see cref="SmtcTimelineCoalescer"/>s.</summary>
        public bool SmtcTimeline;
        public uint SmtcEpoch;

        // ── persistence ──
        public bool Snapshot;

        /// <summary>Back to empty. ONE assignment, so a new slot can never be forgotten here.</summary>
        public void Clear() => this = default;

        /// <summary>Is anything at all pending? The host skips its whole execute pass when nothing is.</summary>
        public readonly bool Any => Load || Start || Stop || PauseHost || ResumeHost || Seek || Volume
            || PrepareNext || PublishState || SendRemote || Transfer || Fetch || Smtc || SmtcTimeline || Snapshot;
    }

    // ── 9. Step — the reducer ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fold one input. Synchronous, allocation-free, clock-free; the ONLY writer of
    /// <see cref="State"/>.</summary>
    /// <param name="s">The state, mutated in place.</param>
    /// <param name="i">The input, by reference (it is large and never modified).</param>
    /// <param name="fx">The effect slots for THIS drain; the last write to a slot wins.</param>
    public static void Step(ref State s, in Input i, ref Effects fx)
    {
        switch (i.Kind)
        {
            case InputKind.Play: DoPlay(ref s, in i, ref fx); break;
            case InputKind.Next: Advance(ref s, in i, ref fx, forward: true); break;
            case InputKind.Prev: Advance(ref s, in i, ref fx, forward: false); break;
            case InputKind.Pause: DoPause(ref s, in i, ref fx); break;
            case InputKind.Resume: DoResume(ref s, in i, ref fx); break;
            case InputKind.Seek: DoSeek(ref s, in i, ref fx); break;
            case InputKind.SetVolume: DoVolume(ref s, in i, ref fx); break;
            case InputKind.SetShuffle: DoShuffle(ref s, in i, ref fx); break;
            case InputKind.SetRepeat: DoRepeat(ref s, in i, ref fx); break;
            case InputKind.Transfer: DoTransfer(ref s, in i, ref fx); break;
            case InputKind.Cluster: DoCluster(ref s, in i, ref fx); break;
            case InputKind.RemoteCommand: DoRemote(ref s, in i, ref fx); break;
            case InputKind.AudioSignal: DoAudio(ref s, in i, ref fx); break;
            case InputKind.Ended: DoEnded(ref s, in i, ref fx); break;
            case InputKind.Duration: DoDuration(ref s, in i, ref fx); break;
            case InputKind.Format: DoFormat(ref s, in i); break;
            case InputKind.Recovery: DoRecovery(ref s, in i); break;
            case InputKind.TransferDone: DoTransferDone(ref s, in i, ref fx); break;
            case InputKind.PutStateSent: Ownership.PutSent(ref s.Own, (uint)i.IntArg, i.LongArg != 0); break;
            case InputKind.PutStateVerdict: DoVerdict(ref s, in i); break;
            case InputKind.DeviceLost: DoDeviceLost(ref s, ref fx); break;
            case InputKind.Release: DoRelease(ref s, in i, ref fx); break;
            case InputKind.Stop: DoStop(ref s, (StopReason)i.IntArg, ref fx); break;
            case InputKind.Suspend: DoSuspend(ref s, in i, ref fx); break;
            case InputKind.Resume_: break;              // waking changes nothing by itself; the pump re-reports
            case InputKind.Tick: DoTick(ref s, in i, ref fx); break;
            case InputKind.LiveReport: DoLiveReport(ref s, in i, ref fx); break;
            case InputKind.GoLive: DoGoLive(ref s, in i, ref fx); break;
            default: break;
        }
    }

    // ── 9.1 transport ───────────────────────────────────────────────────────────────────────────────────────────────

    static void DoPlay(ref State s, in Input i, ref Effects fx)
    {
        // Forwarding: while another device owns playback, a local Play is a PLAY COMMAND to that device, not a load
        // here. This is the routing rule, and it reads the ownership verdict — never a raw cluster id.
        if (!s.RoutesLocal) { Forward(ref s, RemoteCmd.Play, ref fx, 0, false); return; }

        Ownership.Claim(ref s.Own, ClaimCause.UserPlay, ++s.PublishSeq, i.NowMs, i.NowMs, acknowledged: true);
        s.Current = i.Row;
        s.CurrentId = i.Id;
        s.Kind = i.PlayKind;
        s.Context = i.Context;
        s.Cursor = i.Cursor;
        s.Phase = Phase.Loading;
        s.Buffering = false;
        s.Error = Fault.None;
        s.Recovery = RecoveryKind.None;
        s.PosMs = i.IntArg;
        s.PosQpc = i.NowMs;
        s.DurationMs = 0;
        s.StreamFormat = StringId.Empty;
        s.Live = LiveWindow.None;
        s.Edge = LiveEdgeState.AtEdge;
        s.TunedInAtMs = 0;
        s.StartedPlayingAtMs = i.NowMs;
        s.HasBeenPlayingForMs = 0;
        Bump(ref s);
        s.LoadEpoch = s.Epoch;

        fx.Load = true;
        fx.LoadRow = i.Row;
        fx.LoadId = i.Id;
        fx.LoadKind = i.PlayKind;
        fx.LoadEpoch = s.Epoch;
        fx.LoadFromMs = i.IntArg;
        fx.LoadWhy = LoadOrigin.Claim;
        if (i.Row.IsNone && !i.Id.IsEmpty) { fx.Fetch = true; fx.FetchId = i.Id; fx.FetchEpoch = s.Epoch; }
        Prepare(ref s, ref fx);
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
        fx.Snapshot = true;
    }

    /// <summary>Next / Previous. TEN CLICKS ARE TEN STEPS AND ONE LOAD (C3): each one moves the cursor synchronously —
    /// which is what answers the click inside the frame — and rewrites the same <see cref="Effects.Load"/> slot, so
    /// the shell opens exactly one stream, for the LAST epoch.</summary>
    static void Advance(ref State s, in Input i, ref Effects fx, bool forward)
    {
        if (!s.RoutesLocal) { Forward(ref s, forward ? RemoteCmd.SkipNext : RemoteCmd.SkipPrev, ref fx, 0, false); return; }
        if (forward ? s.NoNext : s.NoPrev) return;       // the context disallows it (the cluster said so)

        // Repeat-one re-plays the same row rather than moving: the one place the cursor stands still under a transport
        // verb. An explicit Previous past the first three seconds also restarts the row instead of stepping back.
        if (forward && s.Repeat == RepeatMode.Track && s.HasCurrent) { Restart(ref s, in i, ref fx); return; }
        if (!forward && s.Phase != Phase.Idle && s.Position(i.NowMs) > RestartWindowMs) { Restart(ref s, in i, ref fx); return; }

        var cursor = s.Cursor;
        bool moved = forward ? Queue.TryAdvance(ref cursor, out EntityRef row) : Queue.TryRetreat(ref cursor, out row);
        if (!moved)
        {
            if (!forward) return;                        // the head of history: stay where we are
            if (s.Repeat != RepeatMode.Context) { EndOfQueue(ref s, ref fx); return; }
            // Wrap: the first non-history row is the head of the context.
            int head = Queue.NextIndex(Queue.Rows, -1);
            if (head < 0) { EndOfQueue(ref s, ref fx); return; }
            cursor = Queue.CursorOf(head);
            row = Queue.RefAt(head);
        }

        s.Cursor = cursor;
        s.Current = row;
        s.CurrentId = row.Id;
        s.Phase = Phase.Loading;
        s.Buffering = false;
        s.Error = Fault.None;
        s.Recovery = RecoveryKind.None;
        s.PosMs = 0;
        s.PosQpc = i.NowMs;
        s.DurationMs = 0;
        s.StreamFormat = StringId.Empty;
        s.Live = LiveWindow.None;
        s.Edge = LiveEdgeState.AtEdge;
        s.TunedInAtMs = 0;
        Bump(ref s);
        s.LoadEpoch = s.Epoch;

        fx.Load = true;
        fx.LoadRow = row;
        fx.LoadId = s.CurrentId;
        fx.LoadKind = s.Kind;
        fx.LoadEpoch = s.Epoch;
        fx.LoadFromMs = 0;
        fx.LoadWhy = LoadOrigin.Advance;
        Prepare(ref s, ref fx);
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
        fx.Snapshot = true;
    }

    /// <summary>How far into a row Previous still means "previous" rather than "start this one again".</summary>
    public const int RestartWindowMs = 3_000;

    static void Restart(ref State s, in Input i, ref Effects fx)
    {
        s.PosMs = 0;
        s.PosQpc = i.NowMs;
        Bump(ref s);
        fx.Seek = true;
        fx.SeekMs = 0;
        fx.SeekEpoch = s.Epoch;
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.SmtcTimeline = true;
    }

    static void EndOfQueue(ref State s, ref Effects fx)
    {
        s.Phase = Phase.Ended;
        s.Buffering = false;
        Bump(ref s);
        fx.Stop = true;
        fx.StopWhy = StopReason.EndOfQueue;
        fx.TransportEpoch = s.Epoch;
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    static void DoPause(ref State s, in Input i, ref Effects fx)
    {
        if (!s.RoutesLocal) { Forward(ref s, RemoteCmd.Pause, ref fx, 0, false); return; }
        if (s.Phase is not (Phase.Playing or Phase.Loading)) return;
        s.PosMs = s.Position(i.NowMs);                   // freeze the extrapolation BEFORE the phase changes
        s.PosQpc = i.NowMs;
        s.Phase = Phase.Paused;
        Bump(ref s);
        // PAUSE NEVER RELEASES OWNERSHIP — ownership is not audibility (the 2026-09-11 rule).
        fx.PauseHost = true;
        fx.TransportEpoch = s.Epoch;
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
        fx.Snapshot = true;
    }

    static void DoResume(ref State s, in Input i, ref Effects fx)
    {
        if (!s.RoutesLocal) { Forward(ref s, RemoteCmd.Resume, ref fx, 0, false); return; }
        if (!s.HasCurrent || s.Error != Fault.None) return;
        Ownership.Claim(ref s.Own, ClaimCause.UserResume, ++s.PublishSeq, s.StartedPlayingAtMs, i.NowMs, acknowledged: true);
        s.PosQpc = i.NowMs;
        s.Phase = Phase.Playing;
        Bump(ref s);
        fx.ResumeHost = true;
        fx.TransportEpoch = s.Epoch;
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    static void DoSeek(ref State s, in Input i, ref Effects fx)
    {
        if (s.NoSeek) return;
        if (!s.RoutesLocal) { Forward(ref s, RemoteCmd.SeekTo, ref fx, i.IntArg, false); return; }
        int ms = i.IntArg < 0 ? 0 : s.DurationMs > 0 && i.IntArg > s.DurationMs ? s.DurationMs : i.IntArg;
        s.PosMs = ms;
        s.PosQpc = i.NowMs;
        Bump(ref s);
        fx.Seek = true;
        fx.SeekMs = ms;
        fx.SeekEpoch = s.Epoch;
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.SmtcTimeline = true;
    }

    static void DoVolume(ref State s, in Input i, ref Effects fx)
    {
        int wire = Math.Clamp(i.IntArg, 0, MaxWireVolume);
        if (!s.RoutesLocal)
        {
            // The slider follows the ACTIVE device: while a phone owns playback, dragging it is a volume PUT to the
            // phone (its own route, not a player command) and our own sink is untouched.
            Forward(ref s, RemoteCmd.Unknown, ref fx, wire, false);
            if (fx.SendRemote) fx.RemoteIsVolume = true;
            return;
        }
        float v = wire / (float)MaxWireVolume;
        if (Math.Abs(v - s.Volume) * MaxWireVolume < 1f) return;
        s.Volume = v;
        Bump(ref s);
        fx.Volume = true;
        fx.VolumeValue = v;
        fx.VolumeEpoch = s.Epoch;
        Announce(ref s, ref fx, PublishReason.VolumeChanged);
        fx.Snapshot = true;
    }

    static void DoShuffle(ref State s, in Input i, ref Effects fx)
    {
        bool on = i.IntArg != 0;
        if (!s.RoutesLocal) { Forward(ref s, RemoteCmd.SetShufflingContext, ref fx, 0, on); return; }
        if (s.Shuffle == on) return;
        s.Shuffle = on;
        Bump(ref s);
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Snapshot = true;
    }

    static void DoRepeat(ref State s, in Input i, ref Effects fx)
    {
        var mode = (RepeatMode)i.IntArg;
        if (!s.RoutesLocal)
        {
            // The wire carries TWO flags, not one mode: repeat-track and repeat-context.
            Forward(ref s, mode == RepeatMode.Track ? RemoteCmd.SetRepeatingTrack : RemoteCmd.SetRepeatingContext,
                ref fx, 0, mode != RepeatMode.Off);
            return;
        }
        if (s.Repeat == mode) return;
        s.Repeat = mode;
        Bump(ref s);
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Snapshot = true;
    }

    // ── 9.2 the pump's reports (C4: everything older than the load epoch is dropped) ─────────────────────────────────

    static void DoAudio(ref State s, in Input i, ref Effects fx)
    {
        if (i.Epoch != s.LoadEpoch) return;              // stale — the click that started this was superseded
        switch ((AudioSignal)i.IntArg)
        {
            case AudioSignal.Started:
                s.Phase = Phase.Playing;
                s.Buffering = false;
                s.Error = Fault.None;
                s.PosMs = (int)i.LongArg;
                s.PosQpc = i.NowMs;
                Bump(ref s);
                Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
                fx.Smtc = true;
                fx.SmtcEpoch = s.Epoch;
                break;

            case AudioSignal.Position:
                // NOT an epoch bump: a position report changes nothing a shell is doing (C4), and bumping here would
                // cancel the very load that is reporting. This is the ~5 Hz path — it must stay effect-light.
                s.PosMs = (int)i.LongArg;
                s.PosQpc = i.NowMs;
                fx.SmtcTimeline = true;
                break;

            case AudioSignal.Buffering:
                if (s.Buffering) break;
                s.Buffering = true;
                break;

            case AudioSignal.Buffered:
                if (!s.Buffering) break;
                s.Buffering = false;
                s.Recovery = RecoveryKind.None;
                s.PosQpc = i.NowMs;
                break;

            case AudioSignal.Seeked:
                s.PosMs = (int)i.LongArg;
                s.PosQpc = i.NowMs;
                fx.SmtcTimeline = true;
                break;

            case AudioSignal.Paused:
                if (s.Phase == Phase.Playing) { s.PosMs = s.Position(i.NowMs); s.PosQpc = i.NowMs; s.Phase = Phase.Paused; }
                fx.Smtc = true;
                fx.SmtcEpoch = s.Epoch;
                break;

            case AudioSignal.Resumed:
                if (s.Phase == Phase.Paused) { s.PosQpc = i.NowMs; s.Phase = Phase.Playing; }
                fx.Smtc = true;
                fx.SmtcEpoch = s.Epoch;
                break;

            case AudioSignal.Duration:
                s.DurationMs = (int)i.LongArg;
                fx.Smtc = true;
                fx.SmtcEpoch = s.Epoch;
                break;

            case AudioSignal.Failed:
                // The load is dead. Keep the row on the deck so the bar can say WHAT failed, drop to Paused, and
                // release nothing: a failed load is not a transfer.
                s.Phase = Phase.Paused;
                s.Buffering = false;
                s.Error = (Fault)Math.Clamp(i.LongArg, 0, (long)Fault.Unknown);
                if (s.Error == Fault.None) s.Error = Fault.Unknown;
                Bump(ref s);
                fx.Stop = true;
                fx.StopWhy = StopReason.Failed;
                fx.TransportEpoch = s.Epoch;
                Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
                fx.Smtc = true;
                fx.SmtcEpoch = s.Epoch;
                break;

            case AudioSignal.Stopped:
                if (s.Phase is Phase.Playing or Phase.Loading) { s.Phase = Phase.Paused; Bump(ref s); }
                break;
        }
    }

    /// <summary>The stream ran out. Advancing INLINE (rather than posting a second input) is what makes gapless
    /// gapless: the next Load lands in the SAME drain, so the pump is asked for it before the frame ends.</summary>
    static void DoEnded(ref State s, in Input i, ref Effects fx)
    {
        if (i.Epoch != s.LoadEpoch) return;
        s.HasBeenPlayingForMs += Math.Max(0, i.NowMs - s.PosQpc);
        Advance(ref s, in i, ref fx, forward: true);
    }

    static void DoDuration(ref State s, in Input i, ref Effects fx)
    {
        if (i.Epoch != s.LoadEpoch || s.DurationMs == i.IntArg) return;
        s.DurationMs = i.IntArg;
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    static void DoFormat(ref State s, in Input i)
    {
        if (i.Epoch != s.LoadEpoch) return;
        s.StreamFormat = new StringId((int)i.LongArg);
    }

    static void DoRecovery(ref State s, in Input i)
    {
        if (i.Epoch != s.LoadEpoch) return;
        s.Recovery = (RecoveryKind)i.IntArg;
    }

    // ── 9.3 the connect fold (C5) ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>One cluster — a dealer push, or the RESPONSE to one of our put-states. The ownership fold decides
    /// first and its verdict gates everything after it: a frame it DROPS changes nothing at all (the fence rule), and
    /// a frame that takes playback away stops the host in the same Step.
    ///
    /// <para><b>ONE CLUSTER IS ONE TRANSITION, SO IT IS ONE EPOCH BUMP</b> (C4). This used to bump twice — once for
    /// the stop and once again for the owner change — which left <see cref="State.LoadEpoch"/>, and every effect
    /// slot stamped before the second bump, an epoch BEHIND the state that produced them. A shell then held work
    /// tagged with an epoch the core had already moved past: the stop carried epoch n while the state said n+1, and
    /// the drop test every pump report goes through compared against a number that no longer matched anything. The
    /// order below is therefore fixed: decide, bump ONCE, then stamp every slot with that one epoch.</para></summary>
    static void DoCluster(ref State s, in Input i, ref Effects fx)
    {
        Owner before = s.Own.Kind;
        OwnerFx owner = Ownership.Fold(ref s.Own, in i.Frame, s.Us);
        bool dropped = (owner & OwnerFx.DropFrame) != 0;
        bool stop = (owner & OwnerFx.StopHost) != 0;
        bool changed = s.Own.Kind != before;

        if (dropped && !changed && !stop) return;         // older than the fence: it revokes nothing and mirrors nothing

        if (changed || stop) Bump(ref s);                 // the ONE bump — see the note above

        if (stop)
        {
            s.Phase = Phase.Idle;
            s.Buffering = false;
            s.LoadEpoch = s.Epoch;                        // every in-flight load for the old epoch is now superseded
            fx.Stop = true;
            fx.StopWhy = StopReason.LostOwnership;
            fx.TransportEpoch = s.Epoch;
        }
        // We cannot know a phone's format, and 0.2.9 left ours on screen (ch 21 G8).
        if (changed && s.Own.Kind != Owner.Us) s.StreamFormat = StringId.Empty;
        if ((owner & OwnerFx.PublishInactive) != 0) Announce(ref s, ref fx, PublishReason.BecameInactive);

        // Mirror the remote's row so the bar paints what the OWNER is doing. Only while we do NOT own playback: a
        // cluster that names us is an echo of our own state, and folding it back would fight the local session (the
        // "PLAY glyph over audible playback" incident of 2026-09-11). A DROPPED frame is never mirrored either — its
        // payload is exactly as stale as its timestamp.
        //
        // Nobody/FromForeign deliberately KEEPS the departed device's snapshot on screen (paused) until the user acts
        // — `Ownership.ShowsLocalNowPlaying` is the same rule from the other side.
        if (!dropped && s.Own.Kind == Owner.Foreign) MirrorRemote(ref s, in i, ref fx);

        if (!changed) return;
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
        fx.Snapshot = true;
    }

    static void MirrorRemote(ref State s, in Input i, ref Effects fx)
    {
        ref readonly RemoteState r = ref i.Remote;
        if (r.HasTrack && !r.Track.IsEmpty && !r.Track.Equals(s.CurrentId))
        {
            s.CurrentId = r.Track;
            s.Current = default;                         // the remote's row may have no slot here yet
            fx.Fetch = true;
            fx.FetchId = r.Track;
            fx.FetchEpoch = s.Epoch;
        }
        s.Phase = r.IsPlaying && !r.IsPaused ? Phase.Playing : r.HasTrack ? Phase.Paused : Phase.Idle;
        s.Buffering = r.IsBuffering;
        s.Error = Fault.None;
        s.PosMs = (int)Math.Clamp(r.PositionAsOfMs, 0, int.MaxValue);
        s.PosQpc = i.NowMs;
        s.DurationMs = (int)Math.Clamp(r.DurationMs, 0, int.MaxValue);
        s.Shuffle = r.Shuffling;
        s.Repeat = r.Repeat;
        s.NoNext = r.NoNext;
        s.NoPrev = r.NoPrev;
        s.NoSeek = r.NoSeek;
        s.StreamFormat = StringId.Empty;
        if (r.Volume >= 0) s.Volume = Math.Clamp(r.Volume / (float)MaxWireVolume, 0f, 1f);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    /// <summary>A controller's verb addressed to us. <see cref="RemoteCommand.Ok"/> and
    /// <see cref="RemoteCommand.Kind"/> answer two different questions: a garbled BODY on a KNOWN endpoint is still a
    /// command we RECEIVED (the ack is the glue's, already sent), but it must not be ACTED on.</summary>
    static void DoRemote(ref State s, in Input i, ref Effects fx)
    {
        ref readonly RemoteCommand c = ref i.Command;
        if (!c.Ok || c.Kind == RemoteCmd.Unknown) return;

        switch (c.Kind)
        {
            case RemoteCmd.Play:
            case RemoteCmd.Transfer:
                // An inbound play/transfer IS a claim: the controller addressed US, so we own playback from this
                // Step, before any cluster confirms it. The shell resolves the context and posts the real Play.
                Ownership.Claim(ref s.Own,
                    c.Kind == RemoteCmd.Transfer ? ClaimCause.InboundTransfer : ClaimCause.InboundPlay,
                    ++s.PublishSeq, i.NowMs, i.NowMs, acknowledged: true);
                Bump(ref s);
                Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
                break;

            case RemoteCmd.Resume:
                Ownership.Claim(ref s.Own, ClaimCause.InboundResume, ++s.PublishSeq, s.StartedPlayingAtMs, i.NowMs, true);
                DoResume(ref s, in i, ref fx);
                break;

            case RemoteCmd.Pause:
                DoPause(ref s, in i, ref fx);
                break;

            case RemoteCmd.SkipNext:
                Ownership.Claim(ref s.Own, ClaimCause.InboundSkip, ++s.PublishSeq, s.StartedPlayingAtMs, i.NowMs, true);
                Advance(ref s, in i, ref fx, forward: true);
                break;

            case RemoteCmd.SkipPrev:
                Ownership.Claim(ref s.Own, ClaimCause.InboundSkip, ++s.PublishSeq, s.StartedPlayingAtMs, i.NowMs, true);
                Advance(ref s, in i, ref fx, forward: false);
                break;

            case RemoteCmd.SeekTo:
                {
                    var seek = new Input(InputKind.Seek, intArg: (int)Math.Clamp(c.SeekToMs, 0, int.MaxValue), nowMs: i.NowMs);
                    DoSeek(ref s, in seek, ref fx);
                    break;
                }

            case RemoteCmd.SetShufflingContext:
                {
                    var set = new Input(InputKind.SetShuffle, intArg: c.BoolArg ? 1 : 0, nowMs: i.NowMs);
                    DoShuffle(ref s, in set, ref fx);
                    break;
                }

            case RemoteCmd.SetRepeatingTrack:
                {
                    var set = new Input(InputKind.SetRepeat,
                        intArg: (int)(c.BoolArg ? RepeatMode.Track : RepeatMode.Off), nowMs: i.NowMs);
                    DoRepeat(ref s, in set, ref fx);
                    break;
                }

            case RemoteCmd.SetRepeatingContext:
                {
                    var set = new Input(InputKind.SetRepeat,
                        intArg: (int)(c.BoolArg ? RepeatMode.Context : RepeatMode.Off), nowMs: i.NowMs);
                    DoRepeat(ref s, in set, ref fx);
                    break;
                }

            // AddToQueue / SetQueue / UpdateContext / SetOptions are QUEUE and CONTEXT writes: they land in
            // `Entities/Queue.cs` through the host, not in the transport reducer. The claim below is the part that IS
            // ours — a controller that queues onto us has addressed us.
            case RemoteCmd.AddToQueue:
            case RemoteCmd.SetQueue:
                Ownership.Claim(ref s.Own, ClaimCause.InboundQueueStart, ++s.PublishSeq, s.StartedPlayingAtMs, i.NowMs, true);
                Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
                break;

            default: break;
        }
    }

    /// <summary>Move playback to another device. The local deck stops IMMEDIATELY — waiting for the cluster to
    /// confirm is what made 0.2.9 play here while the bar said "Playing on iPhone".</summary>
    static void DoTransfer(ref State s, in Input i, ref Effects fx)
    {
        ulong target = (ulong)i.LongArg;
        if (target == 0 || target == s.Us) return;       // self-to-self is a 400; the caller must not ask
        Ownership.Release(ref s.Own, ReleaseCause.TransferAway);
        s.Phase = Phase.Idle;
        s.Buffering = false;
        s.StreamFormat = StringId.Empty;
        Bump(ref s);
        s.LoadEpoch = s.Epoch;
        s.TransferEpoch = s.Epoch;
        fx.Stop = true;
        fx.StopWhy = StopReason.Released;
        fx.TransportEpoch = s.Epoch;
        fx.Transfer = true;
        fx.TransferTo = target;
        fx.TransferSlot = i.IntArg;
        fx.TransferEpoch = s.Epoch;
        Announce(ref s, ref fx, PublishReason.BecameInactive);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    static void DoTransferDone(ref State s, in Input i, ref Effects fx)
    {
        if (i.Epoch != s.TransferEpoch) return;          // a transfer we already superseded
        if (i.IntArg != 0) return;                       // accepted: the cluster will name the new owner
        // Rejected: nothing moved, and we released. Say so rather than sitting in a half state.
        Bump(ref s);
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
    }

    /// <summary>The put-state round trip's FAILURE half and its send bookkeeping. The SUCCESS half is a cluster of
    /// origin <c>PutResponse</c> and goes through <see cref="DoCluster"/>, because the fence lives on that cluster's
    /// server timestamp and nowhere else (C5).</summary>
    static void DoVerdict(ref State s, in Input i)
    {
        if (i.LongArg != 0) return;                      // accepted: the response cluster is the judge
        if (Ownership.PutFailed(ref s.Own, (uint)i.IntArg) != OwnerFx.None) Bump(ref s);
    }

    static void DoDeviceLost(ref State s, ref Effects fx)
    {
        if (s.Own.Kind != Owner.Foreign) return;
        var frame = new ClusterFrame(ClusterOrigin.Push, 0, 0, s.Own.LastServerTs);
        Ownership.Fold(ref s.Own, in frame, s.Us);       // F5: Foreign → Nobody(FromForeign), snapshot kept
        s.ActiveDeviceSlot = -1;
        Bump(ref s);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    static void DoRelease(ref State s, in Input i, ref Effects fx)
    {
        OwnerFx owner = Ownership.Release(ref s.Own, (ReleaseCause)i.IntArg);
        if (owner == OwnerFx.None) return;
        if ((owner & OwnerFx.StopHost) != 0)
        {
            s.Phase = Phase.Idle;
            s.Buffering = false;
            s.StreamFormat = StringId.Empty;
            Bump(ref s);
            s.LoadEpoch = s.Epoch;
            fx.Stop = true;
            fx.StopWhy = StopReason.Released;
            fx.TransportEpoch = s.Epoch;
        }
        if ((owner & OwnerFx.PublishInactive) != 0) Announce(ref s, ref fx, PublishReason.BecameInactive);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    static void DoStop(ref State s, StopReason why, ref Effects fx)
    {
        if (s.Phase == Phase.Idle) return;
        s.Phase = Phase.Idle;
        s.Buffering = false;
        Bump(ref s);
        s.LoadEpoch = s.Epoch;
        fx.Stop = true;
        fx.StopWhy = why;
        fx.TransportEpoch = s.Epoch;
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    static void DoSuspend(ref State s, in Input i, ref Effects fx)
    {
        if (s.Phase != Phase.Playing) return;
        s.PosMs = s.Position(i.NowMs);
        s.PosQpc = i.NowMs;
        s.Phase = Phase.Paused;
        Bump(ref s);
        fx.PauseHost = true;
        fx.TransportEpoch = s.Epoch;
        fx.Snapshot = true;
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
    }

    // ── 9.4 the ticker and the live window ──────────────────────────────────────────────────────────────────────────

    /// <summary>The 1 s ticker (P10, named). It folds the extrapolation into <see cref="State.PosMs"/> so every reader
    /// sees ONE number, arms the OS timeline latch, and expires the ownership protection window. It must produce NO
    /// other effect: a tick that announced would be a PUT per second.</summary>
    static void DoTick(ref State s, in Input i, ref Effects fx)
    {
        if (s.Phase == Phase.Playing)
        {
            int now = s.Position(i.NowMs);
            s.HasBeenPlayingForMs += Math.Max(0, now - s.PosMs);
            s.PosMs = now;
            s.PosQpc = i.NowMs;
            fx.SmtcTimeline = true;
        }
        OwnerFx owner = Ownership.Tick(ref s.Own, i.NowMs);
        if ((owner & OwnerFx.StopHost) == 0) return;
        s.Phase = Phase.Idle;
        s.Buffering = false;
        Bump(ref s);
        s.LoadEpoch = s.Epoch;
        fx.Stop = true;
        fx.StopWhy = StopReason.LostOwnership;
        fx.TransportEpoch = s.Epoch;
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    static void DoLiveReport(ref State s, in Input i, ref Effects fx)
    {
        s.Live = i.Live;
        s.Edge = LiveEdgeState.Next(s.Edge, i.Live.BehindMs, i.Live.HasWindow);
        // TunedInAtMs is UNIX ms (the label subtracts two dates), stamped ONCE when live-ness begins. `<= 0` is the
        // honest "not known", and the label then falls back to the reported position rather than showing 0:00.
        if (!i.Live.IsLive) s.TunedInAtMs = 0;
        else if (s.TunedInAtMs <= 0 && i.LongArg > 0) s.TunedInAtMs = i.LongArg;
        fx.SmtcTimeline = true;
    }

    /// <summary>GO LIVE: a committed seek to the edge that ALSO resets the hysteresis machine outright — the user
    /// asked to be at the edge, and the next window report must not be able to answer "still behind" from a position
    /// the seek has already left (ch 20 parity item 67: the button disappears IMMEDIATELY, not when the seek lands).
    /// </summary>
    static void DoGoLive(ref State s, in Input i, ref Effects fx)
    {
        if (!s.Live.IsLive) return;
        s.Edge = LiveEdgeState.AtEdge;
        var seek = new Input(InputKind.Seek,
            intArg: (int)Math.Clamp(s.Live.LiveEdgeMs, 0, int.MaxValue), nowMs: i.NowMs);
        DoSeek(ref s, in seek, ref fx);
    }

    // ── 9.5 the shared tails ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every Step that changes what a SHELL is doing bumps the epoch (C4). Nothing else does — a position
    /// report that bumped would cancel the load reporting it.</summary>
    static void Bump(ref State s) => s.Epoch++;

    /// <summary>Fill the announce slot. Coalescing is the slot itself: ten state changes in one drain are ONE PUT
    /// carrying the LAST reason (C3), and the glue debounces 50 ms on top of that.</summary>
    static void Announce(ref State s, ref Effects fx, PublishReason why)
    {
        fx.PublishState = true;
        fx.PublishWhy = why;
        fx.PublishActive = Ownership.IsActiveOnWire(in s.Own);
        fx.PublishEpoch = s.Epoch;
    }

    /// <summary>Arm gapless: what plays after the cursor, WITHOUT moving it. Only where a prepared stream can actually
    /// be used — every video and cross-kind boundary is a hard cut (<see cref="MediaSwitch.AllowCrossfade"/>).</summary>
    static void Prepare(ref State s, ref Effects fx)
    {
        if (s.Kind == PlayableKind.Video) return;
        if (!Queue.TryPeek(in s.Cursor, out EntityRef next) || next.IsNone) return;
        fx.PrepareNext = true;
        fx.NextRow = next;
        fx.NextId = next.Id;
    }

    /// <summary>Forward a transport verb to the device that owns playback. THE routing rule: it reads the ownership
    /// verdict, never a raw cluster id (memory rule <c>connect-ownership-single-authority</c>).</summary>
    static void Forward(ref State s, RemoteCmd cmd, ref Effects fx, long arg, bool flag)
    {
        if (s.Own.Kind != Owner.Foreign || s.Own.Device == 0) return;
        Bump(ref s);
        fx.SendRemote = true;
        fx.RemoteDevice = s.Own.Device;
        fx.RemoteDeviceSlot = s.ActiveDeviceSlot;
        fx.RemoteCmd = cmd;
        fx.RemoteIsVolume = false;
        fx.RemoteArg = arg;
        fx.RemoteFlag = flag;
        fx.RemoteEpoch = s.Epoch;
    }

    // ── 10. the snapshot the PUT body encodes ───────────────────────────────────────────────────────────────────────

    /// <summary>This device's Connect identity — the constants a PUT body carries about US, filled once at boot and
    /// never derived from state. Kept out of <see cref="State"/> because none of it ever changes and a reducer that
    /// copies six strings per Step is a reducer nobody will keep pure.</summary>
    /// <param name="DeviceId">Our persisted, launch-stable device id.</param>
    /// <param name="DeviceName">What the picker shows for us.</param>
    /// <param name="ClientId">The keymaster client id we authenticated with.</param>
    /// <param name="Platform">`PrivateDeviceInfo.platform`.</param>
    /// <param name="SoftwareVersion">`DeviceInfo.device_software_version`.</param>
    /// <param name="SpircVersion">`DeviceInfo.spirc_version`.</param>
    public readonly record struct DeviceIdentity(
        string DeviceId,
        string DeviceName,
        string ClientId,
        string Platform,
        string SoftwareVersion,
        string SpircVersion);

    /// <summary>THE value <c>Spotify.Decode.PutState</c> encodes — our player state as one flat, self-contained
    /// struct.
    ///
    /// <para><b>Why a snapshot and not <see cref="State"/> itself.</b> The PUT runs on an api thread and
    /// <see cref="State"/> is the UI thread's (C1); and half of what the body needs (the device identity, the wire
    /// volume scale, the message id) is not playback state at all. The host captures this ON the UI thread, inside
    /// the drain, and hands the value across — so the encoder can never read a table, a signal or the reducer.</para>
    ///
    /// <para><b>The identities travel packed.</b> <see cref="Track"/> and <see cref="Context"/> are
    /// <see cref="EntityId"/>s, written to the wire through <c>EntityId.Format(Span&lt;byte&gt;)</c> — the same call
    /// the staged uri uses — so the encoder allocates nothing.</para></summary>
    public readonly struct Snapshot
    {
        // ── us ──
        public readonly DeviceIdentity Device;
        /// <summary>The wire's is_active. Its ONE writer is <see cref="Ownership.IsActiveOnWire"/>.</summary>
        public readonly bool IsActive;
        public readonly PublishReason Reason;
        public readonly uint MessageId;
        /// <summary>OUR device's volume on the wire scale, 0..<see cref="MaxWireVolume"/>. Always ours, even while a
        /// foreign device owns playback — a non-active Wavee still reports its real volume.</summary>
        public readonly int Volume;
        public readonly long ClientTimestampMs;
        public readonly long StartedPlayingAtMs;
        public readonly long HasBeenPlayingForMs;

        // ── the player state ──
        public readonly bool HasTrack;
        public readonly EntityId Track;
        public readonly EntityId Context;
        /// <summary>The queue item id for the current row ("" when we minted the session ourselves).</summary>
        public readonly string Uid;
        public readonly long PositionAsOfMs;
        public readonly long TimestampMs;
        public readonly long DurationMs;
        public readonly bool IsPlaying;
        public readonly bool IsPaused;
        public readonly bool IsBuffering;
        public readonly bool Shuffling;
        public readonly RepeatMode Repeat;
        /// <summary>Decides `track_player` — <see cref="MediaSwitch.TrackPlayer"/>.</summary>
        public readonly PlayableKind Kind;

        public Snapshot(in DeviceIdentity device, bool isActive, PublishReason reason, uint messageId, int volume,
            long clientTimestampMs, long startedPlayingAtMs, long hasBeenPlayingForMs,
            bool hasTrack, EntityId track, EntityId context, string uid,
            long positionAsOfMs, long timestampMs, long durationMs,
            bool isPlaying, bool isPaused, bool isBuffering, bool shuffling, RepeatMode repeat, PlayableKind kind)
        {
            Device = device; IsActive = isActive; Reason = reason; MessageId = messageId; Volume = volume;
            ClientTimestampMs = clientTimestampMs; StartedPlayingAtMs = startedPlayingAtMs;
            HasBeenPlayingForMs = hasBeenPlayingForMs;
            HasTrack = hasTrack; Track = track; Context = context; Uid = uid;
            PositionAsOfMs = positionAsOfMs; TimestampMs = timestampMs; DurationMs = durationMs;
            IsPlaying = isPlaying; IsPaused = isPaused; IsBuffering = isBuffering;
            Shuffling = shuffling; Repeat = repeat; Kind = kind;
        }

        /// <summary>The same snapshot with the message id the glue actually SENT it under. The id is minted inside
        /// the debounce (ten captures in one window become one PUT), so the capture cannot know it — and the ownership
        /// fold needs the number that went on the wire, because the response quoting it is the verdict (C5).</summary>
        public Snapshot WithMessageId(uint messageId)
            => new(in Device, IsActive, Reason, messageId, Volume, ClientTimestampMs, StartedPlayingAtMs,
                HasBeenPlayingForMs, HasTrack, Track, Context, Uid, PositionAsOfMs, TimestampMs, DurationMs,
                IsPlaying, IsPaused, IsBuffering, Shuffling, Repeat, Kind);

        /// <summary>Capture the current state for an announce. PURE — the caller supplies the clock (a unix-ms stamp)
        /// and the message id, so a unit test pins the exact body a given state produces.
        ///
        /// <para>While we are NOT the active device the PLAYER half is empty but the DEVICE half is not: librespot
        /// publishes an idle player_state plus our own volume rather than mirroring the foreign device's row, and
        /// mirroring it is how 0.2.9 announced a phone's track as ours.</para></summary>
        public static Snapshot Of(in State s, in DeviceIdentity device, PublishReason reason, uint messageId,
            long unixMs, long frameNowMs, string uid = "")
        {
            bool active = Ownership.IsActiveOnWire(in s.Own);
            int volume = (int)Math.Round(Math.Clamp(s.Volume, 0f, 1f) * MaxWireVolume);
            if (!active)
                return new Snapshot(in device, false, reason, messageId, volume, unixMs, 0, 0,
                    false, default, default, "", 0, unixMs, 0, false, false, false, false, RepeatMode.Off,
                    PlayableKind.Audio);

            return new Snapshot(in device, true, reason, messageId, volume, unixMs,
                s.StartedPlayingAtMs, s.HasBeenPlayingForMs,
                s.HasCurrent, s.CurrentId, s.Context, uid,
                s.Position(frameNowMs), unixMs, s.DurationMs,
                s.Phase == Phase.Playing, s.Phase == Phase.Paused, s.Buffering,
                s.Shuffle, s.Repeat, s.Kind);
        }
    }

    // ── 11. identity hashing (the fold's device keys) ───────────────────────────────────────────────────────────────

    /// <summary>A device id as the ownership fold sees it: one 64-bit FNV-1a hash, never a string. 0 is "no device",
    /// which is what an empty id must answer — 0.2.9's <c>active.Length == 0</c> test, without the string.
    ///
    /// <para>Public and CORE so the host, the roster and the tests all fold the SAME bytes the same way; a second
    /// hasher anywhere would be a second authority on identity, which is exactly the defect the ownership fold exists
    /// to end.</para></summary>
    public static ulong DeviceHash(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return 0;
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < utf8.Length; i++) { h ^= utf8[i]; h *= 1099511628211UL; }
        return h == 0 ? 1UL : h;                          // 0 is reserved for "none"
    }

    /// <inheritdoc cref="DeviceHash(ReadOnlySpan{byte})"/>
    public static ulong DeviceHash(string id)
    {
        if (id.Length == 0) return 0;
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < id.Length; i++)
        {
            // Device ids are ASCII (hex / base62 from the service), so byte and char agree and this matches the span
            // overload exactly; a non-ASCII char still folds deterministically, which is all identity needs.
            char c = id[i];
            h ^= (byte)c; h *= 1099511628211UL;
            if (c > 0xFF) { h ^= (byte)(c >> 8); h *= 1099511628211UL; }
        }
        return h == 0 ? 1UL : h;
    }
}
