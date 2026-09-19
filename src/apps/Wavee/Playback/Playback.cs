// ── Playback/Playback.cs ───────────────────────────────────────────────────────────────────────────────────────────
// State, Input, Effects, Step, the ownership fold; SmtcTimelineCoalescer (ch 14, +67); TimeFormat / LiveRail /
// LiveEdgeState (ch 20, +120)
//
// Role: CORE
// Owner: G
// Wave: 3
// Budget: 1790 lines
// Spec: plan + ch 14 §9 + ch 20 §9
// Named partial: `Playback.Transitions.cs` (gap batch B3) — the hand-off, next-row, device-reload, video-switch,
// restore, remote-volume, autoplay and play-report arms, their pure planners, and the pure host gates (§4 moved there)
// Named partial: `Playback.Wire.cs` (gap batch R4-1) — the PutState snapshot and its parity half (§10 moved there)
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
    public enum LoadOrigin : byte { Claim, Advance, MediaKindRefresh, VideoRecovery, Restore, DeviceReload }

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
        /// <summary>The pump JOINED the prepared row — a gapless butt-join or a committed crossfade (D4). <c>Epoch</c> is
        /// the OUTGOING load's, <see cref="Input.Id"/> the prepared identity, <see cref="Input.LongArg"/> its position.
        /// The reducer advances WITHOUT a Load and hands the new epoch back through <see cref="Effects.Adopt"/>.</summary>
        HandedOff,
        /// <summary>This load's endgame window opened (the pump's <c>EndingSoonMs</c>: fade + 8 s) — prepare the next
        /// row now. Once per load.</summary>
        EndingSoon,
        /// <summary>The output device changed format under a graph that cannot render on it: reload the row at the
        /// current position, play intent kept (G-108).</summary>
        DeviceReload,
        /// <summary>The video host found no source for the row (the manifest resolve answered nothing): demote to audio.</summary>
        VideoUnavailable,
    }

    /// <summary>Why the current playable is not playing. <see cref="Fault.None"/> is the ONLY value that lets the
    /// transport arm (ch 20 §7: <c>canTransport = Current != none &amp;&amp; Error == None</c>).</summary>
    public enum Fault : byte { None, Network, Unavailable, DrmRequired, DecodeFailed, RuntimeMissing, Unknown }

    /// <summary>THE auto-skip decision, pure: does a failed load step the deck onto the next playable row instead of
    /// parking it? Three things must all hold. The fault must be TERMINAL FOR THE ROW — the catalog has no playable
    /// file, refused the key for this file, or the bytes will not decode (<see cref="IsTerminal"/>) — and not a
    /// session-level one: <see cref="Fault.Network"/> means the next row would fail too, <see cref="Fault.RuntimeMissing"/>
    /// has its own door (the setup toast), and <see cref="Fault.Unknown"/> is nobody's verdict. The load must have been
    /// the deck's OWN move (<see cref="LoadOrigin.Advance"/> — a natural end, a hand-off gone wrong, a Next) and never a
    /// row the listener CHOSE (<see cref="LoadOrigin.Claim"/>: they clicked it, so it parks with Retry and says what is
    /// wrong). And the guard must hold: at most <see cref="MaxConsecutive"/> skips in a row before the deck parks
    /// anyway, so a context of dead rows cannot spin. Repeat-one never skips — the only next row is the dead one.</summary>
    public static class AutoSkip
    {
        /// <summary>Consecutive dead rows the deck steps past before it parks with the fault. The counter resets on the
        /// first audio that actually plays.</summary>
        public const int MaxConsecutive = 3;

        /// <summary>Is this fault about THE ROW, so the next row is worth trying?</summary>
        public static bool IsTerminal(Fault fault) => fault is Fault.Unavailable or Fault.DrmRequired or Fault.DecodeFailed;

        /// <inheritdoc cref="AutoSkip"/>
        public static bool Skips(Fault fault, LoadOrigin origin, bool repeatTrack, int consecutive)
            => IsTerminal(fault) && origin == LoadOrigin.Advance && !repeatTrack && consecutive < MaxConsecutive;
    }

    /// <summary>Where a RESUME (never a fresh <c>Play</c> — that one always starts where it was asked to) should
    /// actually start, given the position and duration already on the deck. PURE: no queue, no clock. The verdict
    /// names its own intent and stops there — <see cref="VerdictKind.StartNext"/> hands the CALLER the decision to
    /// go find that row (<c>Playback.Transitions.cs</c>'s <c>NaturalNext</c>), because a Wave-3 reducer file does not
    /// reach into Wave 1's queue for a lookup it can just as well be handed back.
    ///
    /// <para>Exists because the OLD rule was "resume at <c>PosMs</c>, whatever it is": right for a row the local pump
    /// tracked continuously, wrong for a MIRRORED row whose <c>PosMs</c> is a single cluster snapshot that can sit
    /// unfolded for hours (A2 stops <c>DoTick</c> from ratcheting it to the duration in the meantime, but a snapshot
    /// legitimately taken a second before the remote's own track ended is STILL "the end" once it is resumed) —
    /// reloading exactly there is the inaudible instant this whole plan exists to stop shipping
    /// (<c>docs/plans/wavee/explain-why-palyback-is-indexed-cat.md</c>, "what happened" #1-4).</para></summary>
    public readonly record struct ResumeStart(ResumeStart.VerdictKind Kind, int Ms)
    {
        /// <summary>The three shapes a resume can take.</summary>
        public enum VerdictKind : byte
        {
            /// <summary>Resume exactly at <see cref="ResumeStart.Ms"/>.</summary>
            StartAt,
            /// <summary>Close enough to the end that this row is effectively over: start the row
            /// <c>NaturalNext</c> names, or fall back to 0 on the same row when it names none.</summary>
            StartNext,
            /// <summary>Nothing to resume from (a non-positive position): start over, at 0.</summary>
            StartAtZero,
        }

        /// <summary>Within this much of a KNOWN duration, a resume means "start the next row" rather than reload an
        /// instant nobody will hear. A tighter number than <c>Playback.Transitions.ResumeTailMs</c> (an EPISODE'S
        /// saved resume point, found and applied automatically): a resume PRESS is the listener acting now, on
        /// whatever the deck shows now, not a stale point picked up later.</summary>
        public const int ResumeEndEpsilonMs = 1_500;

        static readonly ResumeStart Next = new(VerdictKind.StartNext, 0);
        static readonly ResumeStart Zero = new(VerdictKind.StartAtZero, 0);

        /// <summary>The verdict for a position/duration pair. <paramref name="durationMs"/> ≤ 0 (unknown) can never
        /// be judged "near the end", so it always resumes exactly at <paramref name="posMs"/> (clamped to 0).</summary>
        public static ResumeStart For(int posMs, int durationMs)
        {
            if (posMs <= 0) return Zero;
            if (durationMs > 0 && posMs >= durationMs - ResumeEndEpsilonMs) return Next;
            return new ResumeStart(VerdictKind.StartAt, posMs);
        }
    }

    /// <summary>Where a committed SEEK actually lands — the one clamp <see cref="DoSeek"/> and a controller's
    /// <c>SeekTo</c> (folded through the very same <see cref="DoSeek"/>) both run through, so a drag and a remote
    /// command agree (<c>PlaybackStepTests.A_controllers_seek_goes_through_the_same_clamp_as_a_local_one</c>). PURE:
    /// two numbers in, one out.</summary>
    public static class SeekTarget
    {
        /// <summary>How far before the very end a seek is still allowed to land. The same reasoning as
        /// <c>Video.HostRules.StartClampGuardMs</c> from the other host, sized for audio's much shorter open: a seek
        /// that lands EXACTLY at the duration hands the pump the identical inaudible instant a stale mirror used to
        /// (A2) — except this time it is the LOCAL listener asking for it. The guard lands it a moment earlier
        /// instead, where the natural end can still fire on its own once playback actually gets there.</summary>
        public const int TailGuardMs = 750;

        /// <summary>Clamp a requested position into <c>[0, durationMs - TailGuardMs]</c> (never past 0 either way).
        /// An unknown duration (≤ 0) clamps only the lower bound — there is no upper one to reserve a tail against.</summary>
        public static int Clamp(int requestedMs, int durationMs)
        {
            int ms = requestedMs < 0 ? 0 : requestedMs;
            if (durationMs <= 0) return ms;
            int cap = durationMs > TailGuardMs ? durationMs - TailGuardMs : 0;
            return ms > cap ? cap : ms;
        }
    }

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

    // ── 4. the pure host gates live in Playback.Transitions.cs §11 ──────────────────────────────────────────────────────

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
    /// <param name="Context">The remote's own context uri, <c>default</c> when the cluster carried none. Never
    /// painted directly (<see cref="MirrorRemote"/> only CACHES it, into <see cref="State.MirrorContext"/>) — a
    /// takeover (A4) is what adopts it into <see cref="State.Context"/>, atomically with the queue reseed, so the
    /// header never names a session the rows do not yet match.</param>
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
        bool NoSeek,
        EntityId Context = default);

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
        /// <summary>The last MIRRORED remote's own context uri (<see cref="RemoteState.Context"/>), cached by
        /// <see cref="MirrorRemote"/> while <see cref="Owner.Foreign"/> owns playback. Never painted directly — only
        /// a TAKEOVER (A4: <c>DoResume</c>'s parked branch, <c>DoPlay</c> over the same row) adopts it into
        /// <see cref="Context"/>, atomically with the queue reseed the host runs for <see cref="Effects.TakeoverSeed"/>,
        /// so the header never names a session the rows do not yet match.</summary>
        public EntityId MirrorContext;

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
        /// <summary>THIS device's volume, 0..1, LINEAR — the sink's and the PUT body's. The cubic taper is the audio
        /// host's (<c>Playback.Audio.VolumeTaper</c>), and the wire scale is the snapshot's. Never a foreign owner's:
        /// that one is <see cref="MirrorVolume"/>, and the slider reads <see cref="SliderVolume"/>.</summary>
        public float Volume;
        /// <summary>The foreign owner's volume as the cluster stated it, 0..1; -1 = none stated.</summary>
        public float MirrorVolume;
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
        /// <summary>The controller command the next PUT is attributed to (a <c>connect/volume</c> or a player command's
        /// message id), so the sender's own control does not fight an unattributed echo (G-073). 0 = none.</summary>
        public uint LastCommandMessageId;
        /// <summary>The frame-clock stamp that command arrived at: a PUT more than <see cref="CommandAttribution.WindowMs"/>
        /// later is not its answer (G-246).</summary>
        public long LastCommandAtMs;
        /// <summary>The sender's device-id hash (<c>last_command_sent_by_device_id</c>, resolved through the roster); 0 = none.</summary>
        public ulong LastCommandSender;

        // ── the next row, the hosts, the registration (gap batch B3, Playback.Transitions.cs) ──
        /// <summary>The row after the cursor the pump was last told about — prefetched, or prepared when
        /// <see cref="NextArmed"/> (G-112). Empty = nothing armed.</summary>
        public EntityId NextId;
        /// <summary><see cref="NextId"/> was PREPARED (a full open), not merely prefetched.</summary>
        public bool NextArmed;
        /// <summary>The pump said this load's endgame window opened; the next row is prepared from here on.</summary>
        public bool EndingSoon;
        /// <summary>The deck shows a row NO host holds — a launch restore, a stop, a lost ownership, the end of the queue.
        /// The next Resume loads it at <see cref="PosMs"/> instead of resuming a session that does not exist.</summary>
        public bool Parked;
        /// <summary>The placement wants video: a row that has one loads on the video host (G-141).</summary>
        public bool VideoWanted;
        /// <summary>Re-resolves spent on the current video load (G-142).</summary>
        public byte VideoRetries;
        /// <summary>Where autoplay stands for <see cref="Context"/> (G-080) — and, while <see cref="MorePages"/>, where the
        /// next context page stands: one "more rows were asked for" phase for both answers.</summary>
        public AutoplayPhase Autoplay;
        /// <summary>The host holds a next page for <see cref="Context"/> (G-242): its run-out pages before autoplay is asked,
        /// and a station — which autoplay refuses — keeps paging.</summary>
        public bool MorePages;
        /// <summary>The host has said whether <see cref="Context"/> pages (<see cref="InputKind.ContextPages"/> landed for
        /// it). Until then no refill is decided: the answer may still name a page.</summary>
        public bool PagesKnown;
        /// <summary>The host holds a next page of the autoplay answer: a consumed autoplay run pages before it re-asks.</summary>
        public bool AutoplayPages;
        /// <summary>The row after <see cref="NextId"/> the pump was told to warm. Empty = none.</summary>
        public EntityId Next2Id;
        /// <summary>A play registration is open for the row on the deck (G-076): exactly one per row, however many
        /// reloads, host switches and pauses it lives through.</summary>
        public bool ReportOpen;
        /// <summary>Why the current load started — the registration's <c>reason_start</c> when its audio begins.</summary>
        public PlayReason StartReason;
        /// <summary>WHO asked for the current load — the deck's own move (<see cref="LoadOrigin.Advance"/>) or a row the
        /// listener chose (<see cref="LoadOrigin.Claim"/>). A failed load reads it to decide between stepping past a dead
        /// row and parking on it with Retry (<see cref="AutoSkip"/>).</summary>
        public LoadOrigin LoadWhy;
        /// <summary>Dead rows the deck has stepped past in a row without any audio playing between them — the
        /// <see cref="AutoSkip"/> guard. Reset by the first <see cref="AudioSignal.Started"/> and by a Play.</summary>
        public byte AutoSkips;

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
        /// <summary>Does the CONTEXT allow Next / Previous, error or not? The player bar reads these for its own fold:
        /// a failed load parks the deck row with <see cref="Error"/> set and <see cref="CanTransport"/> false, and the
        /// one way out that does not re-play the dead row is to skip past it (<c>Advance</c> heals the error through
        /// <c>PutOnDeck</c>). <see cref="CanSkipNext"/> / <see cref="CanSkipPrev"/> stay the reducer's folded verdict.</summary>
        public readonly bool NextAllowedByContext => HasCurrent && !NoNext;
        /// <inheritdoc cref="NextAllowedByContext"/>
        public readonly bool PrevAllowedByContext => HasCurrent && !NoPrev;
        /// <summary>What the volume slider shows: the foreign owner's volume while one owns playback (dragging it is a
        /// volume PUT to that device), ours otherwise.</summary>
        public readonly float SliderVolume => Own.Kind == Owner.Foreign && MirrorVolume >= 0f ? MirrorVolume : Volume;

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
            MirrorVolume = -1f,
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
        /// <summary>A controller set THIS device's volume (<c>IntArg</c> = 0..<see cref="MaxWireVolume"/>, <c>LongArg</c> =
        /// the sender's message id). Never forwarded.</summary>
        RemoteVolume,
        /// <summary>The queue changed under the deck: re-arm the next row.</summary>
        QueueChanged,
        /// <summary>The video placement turned on or off (<c>IntArg</c> != 0 = on).</summary>
        VideoPlacement,
        /// <summary>The row <see cref="Input.Id"/> gained video facts the connect-state service has not been told.</summary>
        VideoOffer,
        /// <summary>Put the last session back on the deck at launch (<c>IntArg</c> = position, <c>LongArg</c> = duration).</summary>
        Restore,
        /// <summary>An episode's saved resume point for load <c>Epoch</c> (<c>IntArg</c> = ms).</summary>
        ResumeAt,
        /// <summary>The autoplay answer for <see cref="Input.Context"/> (<c>IntArg</c> = rows appended; <c>LongArg</c> bit 0 =
        /// a page follows, bit 1 = the ask should be retried once the session is online).</summary>
        Autoplayed,
        /// <summary>An autoplay page answered for <see cref="Input.Context"/> (<c>IntArg</c> = rows appended, <c>LongArg</c> != 0 =
        /// another page follows it).</summary>
        AutoplayPaged,
        /// <summary>The session came online: a deferred autoplay ask and a dropped prefetch are re-issued.</summary>
        SessionOnline,
        /// <summary>The host holds (<c>IntArg</c> != 0) or no longer holds a next page for <see cref="Input.Context"/> (G-242).</summary>
        ContextPages,
        /// <summary>A context page answered for <see cref="Input.Context"/> (<c>IntArg</c> = rows appended, <c>LongArg</c> != 0 =
        /// another page follows it).</summary>
        Paged,
        /// <summary>The user queued rows the host staged (<c>IntArg</c> = how many, <c>LongArg</c> != 0 = play them next):
        /// forwarded to a foreign owner as <c>add_to_queue</c> / <c>set_queue</c> (G-248).</summary>
        QueueToOwner,
        /// <summary>The deck's context becomes <see cref="Input.Context"/> WITHOUT a load — a radio parked behind the
        /// current row (G-251). <see cref="Input.Cursor"/> is the deck row's cursor in the rewritten queue.</summary>
        SwitchContext,
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

        /// <summary>A play with its claim cause and its start state stated — what an inbound Connect load and a restored
        /// context post. <c>LongArg</c> packs the cause (low byte) and <see cref="PausedBit"/>; a plain
        /// <see cref="Play"/> is <see cref="ClaimCause.UserPlay"/>, playing.</summary>
        public static Input PlayFrom(EntityRef row, EntityId id, EntityId context, QueueCursor cursor, PlayableKind kind,
            int fromMs, long nowMs, ClaimCause cause, bool paused)
            => new(InputKind.Play, row, id, context, cursor, fromMs, (long)cause | (paused ? PausedBit : 0L),
                nowMs: nowMs, playKind: kind);

        /// <summary>The bit of a Play's <c>LongArg</c> that asks for a paused start.</summary>
        public const long PausedBit = 1L << 8;

        /// <summary>The bit of a SetShuffle's <c>IntArg</c> that says the queue is ALREADY in the asked order (a load that
        /// built it shuffled), so no reorder effect follows.</summary>
        public const int ShuffleOrderedBit = 2;

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
        public static Input ShuffleOrdered(bool on) => new(InputKind.SetShuffle, intArg: (on ? 1 : 0) | ShuffleOrderedBit);
        public static Input HandedOff(uint epoch, EntityId preparedId, int positionMs, long nowMs = 0)
            => new(InputKind.AudioSignal, id: preparedId, intArg: (int)AudioSignal.HandedOff, longArg: positionMs,
                epoch: epoch, nowMs: nowMs);
        public static Input RemoteVolume(int wire, uint messageId, long nowMs = 0)
            => new(InputKind.RemoteVolume, intArg: wire, longArg: messageId, nowMs: nowMs);
        public static Input QueueChanged(long nowMs = 0) => new(InputKind.QueueChanged, nowMs: nowMs);
        public static Input VideoPlacement(bool active, long nowMs = 0)
            => new(InputKind.VideoPlacement, intArg: active ? 1 : 0, nowMs: nowMs);
        public static Input VideoOffer(EntityId track) => new(InputKind.VideoOffer, id: track);
        public static Input Restore(EntityRef row, EntityId id, EntityId context, QueueCursor cursor, int positionMs,
            int durationMs, long nowMs = 0)
            => new(InputKind.Restore, row, id, context, cursor, positionMs, durationMs, nowMs: nowMs);
        public static Input ResumeAt(uint epoch, int positionMs, long nowMs = 0)
            => new(InputKind.ResumeAt, intArg: positionMs, epoch: epoch, nowMs: nowMs);
        public static Input Autoplayed(EntityId context, int appended, bool morePages = false, bool retry = false, long nowMs = 0)
            => new(InputKind.Autoplayed, context: context, intArg: appended,
                longArg: (morePages ? AutoplayMorePagesBit : 0L) | (retry ? AutoplayRetryBit : 0L), nowMs: nowMs);
        /// <summary>The bits of an Autoplayed's <c>LongArg</c>.</summary>
        public const long AutoplayMorePagesBit = 1L, AutoplayRetryBit = 2L;
        public static Input AutoplayPaged(EntityId context, int appended, bool morePages, long nowMs = 0)
            => new(InputKind.AutoplayPaged, context: context, intArg: appended, longArg: morePages ? 1 : 0, nowMs: nowMs);
        public static Input SessionOnline(long nowMs = 0) => new(InputKind.SessionOnline, nowMs: nowMs);
        public static Input ContextPages(EntityId context, bool morePages, long nowMs = 0)
            => new(InputKind.ContextPages, context: context, intArg: morePages ? 1 : 0, nowMs: nowMs);
        public static Input Paged(EntityId context, int appended, bool morePages, long nowMs = 0)
            => new(InputKind.Paged, context: context, intArg: appended, longArg: morePages ? 1 : 0, nowMs: nowMs);
        /// <summary><c>LongArg</c> packs the staged run's offset (above bit 0) and "play next" (bit 0).</summary>
        public static Input QueueToOwner(int count, bool next, int offset = 0, long nowMs = 0)
            => new(InputKind.QueueToOwner, intArg: count, longArg: ((long)Math.Max(0, offset) << 1) | (next ? 1L : 0L), nowMs: nowMs);
        /// <summary>The context under the deck row changes with no load (G-251): the host has already rewritten the queue
        /// around the row at <paramref name="cursor"/>.</summary>
        public static Input SwitchContext(EntityId context, QueueCursor cursor, long nowMs = 0)
            => new(InputKind.SwitchContext, context: context, cursor: cursor, nowMs: nowMs);
        public static Input Suspend(long nowMs = 0) => new(InputKind.Suspend, nowMs: nowMs);
        public static Input Wake(long nowMs = 0) => new(InputKind.Resume_, nowMs: nowMs);

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
        /// <summary>Open the load PAUSED at <see cref="LoadFromMs"/> — a paused transfer, a paused device reload or host
        /// switch. The host follows the load with the host's pause in the same execute pass.</summary>
        public bool LoadPaused;

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

        // ── gapless (G-100, G-112) ──
        /// <summary>Fully open the next row for the hand-off (the endgame). A pump holding another id replaces it.</summary>
        public bool PrepareNext;
        public EntityRef NextRow;
        public EntityId NextId;
        /// <summary>WARM the next row only — head, mirrors, key; no ring, no decoder. What a load asks for.</summary>
        public bool Prefetch;
        public EntityRef PrefetchRow;
        public EntityId PrefetchId;
        /// <summary>Warm the row after the next one too; the host runs it before <see cref="Prefetch"/>.</summary>
        public bool Prefetch2;
        public EntityRef Prefetch2Row;
        public EntityId Prefetch2Id;
        /// <summary>The prepared row stopped being next: dispose the slot (and a join not yet live).</summary>
        public bool CancelPrepared;
        /// <summary>A hand-off advanced the deck without a Load: the pump adopts <see cref="AdoptTo"/> in place of
        /// <see cref="AdoptFrom"/> for the voice already playing.</summary>
        public bool Adopt;
        public uint AdoptFrom, AdoptTo;

        // ── queue writes the host applies (G-074, G-080) ──
        /// <summary>A controller's <c>add_to_queue</c>: enqueue <see cref="QueueAddId"/>.</summary>
        public bool QueueAdd;
        public EntityId QueueAddId;
        /// <summary>Reorder the rows ahead of the cursor: shuffle them (<see cref="ReorderShuffle"/>) or restore the
        /// context's own order.</summary>
        public bool Reorder;
        public bool ReorderShuffle;
        /// <summary>The context is running out: ask autoplay for <see cref="AutoplayContext"/> (the host declines when
        /// the setting is off) and answer with <see cref="Input.Autoplayed"/>.</summary>
        public bool Autoplay;
        public EntityId AutoplayContext;
        /// <summary>The context is running out and the host holds its next page: fetch it for <see cref="PageContext"/> and
        /// answer with <see cref="Input.Paged"/> (G-242).</summary>
        public bool Page;
        public EntityId PageContext;
        /// <summary>The autoplay run is nearly consumed and the host holds its next page: fetch it for
        /// <see cref="AutoplayPageContext"/> and answer with <see cref="Input.AutoplayPaged"/>.</summary>
        public bool AutoplayPage;
        public EntityId AutoplayPageContext;

        // ── video (G-142) ──
        /// <summary>The video placement was demoted to audio after its retry: the shell turns the surface off and says
        /// why.</summary>
        public bool VideoDemoted;
        public Fault VideoDemotedWhy;

        // ── the auto-skip (a dead row the deck stepped past on its own) ──
        /// <summary>The deck advanced onto a row whose load failed for good and moved on to the next playable one in
        /// this same drain (<see cref="AutoSkip"/>): the shell says so — a toast naming <see cref="SkippedId"/> — because
        /// a track that silently vanishes from a listen is a bug report. Coalesces like every slot: three dead rows in
        /// one drain surface the LAST one.</summary>
        public bool SkippedUnavailable;
        public EntityId SkippedId;

        // ── the play report (G-076, G-079) — a SEQUENCE, drained by the host after every Step ──
        public PlayReport Play;

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
        /// <summary><see cref="RemoteCmd.PlayContext"/>: what to play on the owner — the context and the row to start at.</summary>
        public EntityId RemoteContext, RemoteTrack;

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

        // ── takeover (A4) ──
        /// <summary>The user just claimed a MIRRORED row (<c>DoResume</c>'s parked branch, <c>DoPlay</c> over the
        /// same row): the host must re-seed the local queue from the cluster data it still holds, UNCONDITIONALLY —
        /// bypassing <c>Queue.DecideSeed</c>'s normal "never touch a queue that left <c>EdgeState.Unknown</c>" rule
        /// (its own <c>takeover</c> parameter is exactly this override). The 3-10 line hook
        /// <c>Playback.Host.cs</c>'s <c>Execute()</c> needs is <c>if (s_fx.TakeoverSeed) SeedQueueFromCluster(takeover: true);</c>
        /// — see <c>Playback.Host.Remote.cs</c>.</summary>
        public bool TakeoverSeed;

        /// <summary>Back to empty. ONE assignment, so a new slot can never be forgotten here.</summary>
        public void Clear() => this = default;

        /// <summary>Is anything at all pending? The host skips its whole execute pass when nothing is.</summary>
        public readonly bool Any => Load || Start || Stop || PauseHost || ResumeHost || Seek || Volume
            || PrepareNext || PublishState || SendRemote || Transfer || Fetch || Smtc || SmtcTimeline || Snapshot
            || Prefetch || Prefetch2 || CancelPrepared || Adopt || QueueAdd || Reorder || Autoplay || Page || AutoplayPage
            || VideoDemoted || SkippedUnavailable || TakeoverSeed;
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
            case InputKind.Next: Advance(ref s, in i, ref fx, forward: true, PlayReason.ForwardButton); break;
            case InputKind.Prev: Advance(ref s, in i, ref fx, forward: false, PlayReason.BackButton); break;
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
            case InputKind.Resume_: DoWake(ref s, in i, ref fx); break;
            case InputKind.Tick: DoTick(ref s, in i, ref fx); break;
            case InputKind.LiveReport: DoLiveReport(ref s, in i, ref fx); break;
            case InputKind.GoLive: DoGoLive(ref s, in i, ref fx); break;
            case InputKind.RemoteVolume: DoRemoteVolume(ref s, in i, ref fx); break;
            case InputKind.QueueChanged: DoQueueChanged(ref s, ref fx); break;
            case InputKind.VideoPlacement: DoVideoPlacement(ref s, in i, ref fx); break;
            case InputKind.VideoOffer: DoVideoOffer(ref s, in i, ref fx); break;
            case InputKind.Restore: DoRestore(ref s, in i, ref fx); break;
            case InputKind.ResumeAt: DoResumeAt(ref s, in i, ref fx); break;
            case InputKind.Autoplayed: DoAutoplayed(ref s, in i, ref fx); break;
            case InputKind.AutoplayPaged: DoAutoplayPaged(ref s, in i, ref fx); break;
            case InputKind.SessionOnline: DoSessionOnline(ref s, ref fx); break;
            case InputKind.ContextPages: DoContextPages(ref s, in i, ref fx); break;
            case InputKind.Paged: DoPaged(ref s, in i, ref fx); break;
            case InputKind.QueueToOwner: DoQueueToOwner(ref s, in i, ref fx); break;
            case InputKind.SwitchContext: DoSwitchContext(ref s, in i, ref fx); break;
            default: break;
        }
    }

    // ── 9.1 transport ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Does <paramref name="row"/> name <paramref name="id"/>? False for no row, a slot past the table, or a
    /// slot from a retired scope that now reads another identity — every case the host must re-resolve.
    /// <see cref="EntityRef.Id"/> already guards a slot past its table's end (returns <c>default</c>, never throws),
    /// so nothing here needs to re-check the table count.</summary>
    public static bool RowNamesId(EntityRef row, EntityId id) => !row.IsNone && !id.IsEmpty && row.Id.Equals(id);

    static void DoPlay(ref State s, in Input i, ref Effects fx)
    {
        var cause = (ClaimCause)(byte)(i.LongArg & 0xFF);
        bool paused = (i.LongArg & Input.PausedBit) != 0;
        bool inbound = cause >= ClaimCause.InboundPlay;

        // Forwarding: while another device owns playback, a local Play is a PLAY COMMAND to that device, not a load
        // here. This is the routing rule, and it reads the ownership verdict — never a raw cluster id. An INBOUND load
        // that raced a takeover is simply stale: it is nobody's command to forward.
        if (!s.RoutesLocal)
        {
            // Not a bare `play` (a RESUME on the owner): the desktop `play` envelope naming the context and the row, so
            // the phone starts what was clicked here (0.2.9 parity; user report 2026-09-16 "it does not start playing").
            if (!inbound) ForwardPlay(ref s, in i, ref fx);
            return;
        }

        EntityRef row = i.Row;
        EntityId id = i.Id;
        QueueCursor cursor = i.Cursor;
        int fromMs = i.IntArg;
        // A clicked row the catalog has RULED dead starts the context at the next playable row instead: the greyed row
        // stays where it was clicked, and the listener hears the list rather than a parked "0:00". Only when the cursor
        // names that row in the live queue (a queue the reducer has not been handed yet is not walked), never for the
        // row already on the deck (a Retry re-issues exactly that row and must keep meaning "try it again"), and never
        // past the end: with nothing playable after it the dead row loads, fails and parks with Retry, as a lone one does.
        if (!cursor.IsNone && Entities.Current is not null && Queue.RefAt(in cursor) == row
            && !(row == s.Current && cursor == s.Cursor) && !RowPlayable(row))
        {
            int at = Queue.NextPlayable(Queue.Rows, default(LiveRows), cursor.Index, forward: true, wrap: false);
            if (at >= 0) { cursor = Queue.CursorOf(at); row = Queue.RefAt(at); id = row.Id; fromMs = 0; }
        }

        // A4: claiming the row currently MIRRORED from a foreign device — it has no cursor of its own (nothing local
        // ever queued it) — is a TAKEOVER, not an ordinary click: the context comes from the cluster we cached, never
        // from the click's own (possibly stale) `i.Context`, and the host re-seeds prev/next + cursor atomically
        // (fx.TakeoverSeed) once this Step lands.
        bool takeover = s.Cursor.IsNone && !id.IsEmpty && id.Equals(s.CurrentId);
        EntityId context = takeover && !s.MirrorContext.IsEmpty ? s.MirrorContext : i.Context;

        Ownership.Claim(ref s.Own, cause, ++s.PublishSeq, i.NowMs, i.NowMs, acknowledged: true);
        ReportEnd(ref s, ref fx, inbound ? PlayReason.Remote : PlayReason.ClickRow, s.Position(i.NowMs));
        if (!context.Equals(s.Context)) ResetRefill(ref s);
        s.Context = context;
        s.StartedPlayingAtMs = i.NowMs;
        s.HasBeenPlayingForMs = 0;
        s.AutoSkips = 0;                                  // a chosen row is a fresh intent: the guard starts over
        PlayableKind kind = i.PlayKind == PlayableKind.Audio ? KindOfRow(row, s.VideoWanted) : i.PlayKind;
        PutOnDeck(ref s, in i, row, id, cursor, kind, fromMs, paused);
        s.StartReason = inbound ? PlayReason.Remote : PlayReason.ClickRow;
        EmitLoad(ref s, ref fx, LoadOrigin.Claim, paused);
        if (!id.IsEmpty && !RowNamesId(row, id)) { fx.Fetch = true; fx.FetchId = id; fx.FetchEpoch = s.Epoch; }
        if (takeover) fx.TakeoverSeed = true;
        fx.Snapshot = true;
    }

    /// <summary>Next / Previous, and the natural end (<paramref name="why"/> = <see cref="PlayReason.TrackDone"/>).
    /// TEN CLICKS ARE TEN STEPS AND ONE LOAD (C3): each one moves the cursor synchronously — which is what answers the
    /// click inside the frame — and rewrites the same <see cref="Effects.Load"/> slot, so the shell opens exactly one
    /// stream, for the LAST epoch.</summary>
    static void Advance(ref State s, in Input i, ref Effects fx, bool forward, PlayReason why)
    {
        if (!s.RoutesLocal) { Forward(ref s, forward ? RemoteCmd.SkipNext : RemoteCmd.SkipPrev, ref fx, 0, false); return; }
        bool natural = why == PlayReason.TrackDone;
        if (!natural && (forward ? s.NoNext : s.NoPrev)) return;   // the context disallows the SKIP (the cluster said so)

        // Repeat-one re-plays the same row rather than moving: the one place the cursor stands still under a transport
        // verb. A natural end reloads the row (its session has ended); a click restarts the live one. An explicit
        // Previous past the first three seconds also restarts the row instead of stepping back.
        if (forward && s.Repeat == RepeatMode.Track && s.HasCurrent)
        {
            if (!natural) { Restart(ref s, in i, ref fx, why); return; }
            ReportEnd(ref s, ref fx, why, s.DurationMs > 0 ? s.DurationMs : s.Position(i.NowMs));
            PutOnDeck(ref s, in i, s.Current, s.CurrentId, s.Cursor, s.Kind, 0, paused: false);
            s.StartReason = why;
            EmitLoad(ref s, ref fx, LoadOrigin.Advance, paused: false);
            return;
        }
        if (!forward && s.Phase != Phase.Idle && s.Position(i.NowMs) > RestartWindowMs) { Restart(ref s, in i, ref fx, why); return; }

        // The walk steps past every row the catalog has ruled dead (`RowPlayable`): the deck never lands on a row whose
        // load can only fail, in either direction, and a context whose remaining rows are all dead has run out exactly
        // like one with none left. Under repeat-context (and no page still to fetch — a context with a page pages
        // first, or the rows past its first page would never play, G-242) the walk wraps to the first row the CONTEXT
        // provided (`Queue.WrapIndex`): a consumed queued or autoplay row must not replay.
        bool wrap = forward && s.Repeat == RepeatMode.Context && !s.MorePages;
        int at = Queue.NextPlayable(Queue.Rows, default(LiveRows), s.Cursor.Index, forward, wrap);
        if (at < 0)
        {
            if (!forward) return;                        // the head of history (or only dead rows behind us): stay
            if (!wrap) { EndOfContext(ref s, in i, ref fx, why); return; }
            EndOfQueue(ref s, in i, ref fx, why);        // repeat-context with nothing playable anywhere in it
            return;
        }
        QueueCursor cursor = Queue.CursorOf(at);
        EntityRef row = Queue.RefAt(at);

        ReportEnd(ref s, ref fx, why, natural && s.DurationMs > 0 ? s.DurationMs : s.Position(i.NowMs));
        PutOnDeck(ref s, in i, row, row.Id, cursor, KindOfRow(row, s.VideoWanted), 0, paused: false);
        s.StartReason = why;
        EmitLoad(ref s, ref fx, LoadOrigin.Advance, paused: false);
        fx.Snapshot = true;
    }

    /// <summary>How far into a row Previous still means "previous" rather than "start this one again".</summary>
    public const int RestartWindowMs = 3_000;

    static void Restart(ref State s, in Input i, ref Effects fx, PlayReason why)
    {
        if (s.Parked) { s.PosMs = 0; s.PosQpc = i.NowMs; fx.SmtcTimeline = true; return; }   // nothing live to seek
        bool registered = s.ReportOpen;
        ReportEnd(ref s, ref fx, why, s.Position(i.NowMs));
        s.PosMs = 0;
        s.PosQpc = i.NowMs;
        Bump(ref s);
        fx.Seek = true;
        fx.SeekMs = 0;
        fx.SeekEpoch = s.Epoch;
        if (registered) ReportStart(ref s, ref fx, why, 0);   // the same row, played again, is a new registration
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.SmtcTimeline = true;
    }

    /// <summary>Nothing more to play: the phase ends, the host stops, and the deck parks at the row's start so a play
    /// press replays it rather than resuming a session that ended.</summary>
    static void EndOfQueue(ref State s, in Input i, ref Effects fx, PlayReason why)
    {
        ReportEnd(ref s, ref fx, why, why == PlayReason.TrackDone && s.DurationMs > 0 ? s.DurationMs : s.Position(i.NowMs));
        s.Phase = Phase.Ended;
        s.Buffering = false;
        s.PosMs = 0;
        s.PosQpc = i.NowMs;
        s.Parked = true;
        s.EndingSoon = false;
        s.NextId = default;
        s.NextArmed = false;
        Bump(ref s);
        s.LoadEpoch = s.Epoch;
        fx.Load = false;                                 // a load earlier in this drain was skipped past
        fx.PrepareNext = false;
        fx.Prefetch = false;
        fx.Stop = true;
        fx.StopWhy = StopReason.EndOfQueue;
        fx.TransportEpoch = s.Epoch;
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
        fx.Snapshot = true;
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
        ReportPaused(ref s, ref fx, s.PosMs);
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
        fx.Snapshot = true;
    }

    static void DoResume(ref State s, in Input i, ref Effects fx)
    {
        if (!s.RoutesLocal) { Forward(ref s, RemoteCmd.Resume, ref fx, 0, false); return; }
        if (!s.HasCurrent || s.Error != Fault.None) return;
        if (s.Parked || s.Phase == Phase.Ended)
        {
            // No host holds this row (a restore, a stop, a lost ownership, the end): resuming it is LOADING it — a
            // new playback as far as the newest-starter rule goes, so it restamps.
            //
            // A4: a MIRRORED row this session never queued (no cursor of its own) resumes as a TAKEOVER — the
            // context is adopted from the cluster we cached and the host re-seeds prev/next + cursor atomically
            // (fx.TakeoverSeed) once this Step lands.
            bool takeover = s.Cursor.IsNone;
            if (takeover && !s.MirrorContext.IsEmpty) s.Context = s.MirrorContext;

            Ownership.Claim(ref s.Own, ClaimCause.UserPlay, ++s.PublishSeq, i.NowMs, i.NowMs, acknowledged: true);
            s.StartedPlayingAtMs = i.NowMs;
            s.HasBeenPlayingForMs = 0;

            // A3: a row parked within ResumeStart.ResumeEndEpsilonMs of its own end resumes into the NEXT row
            // instead of an inaudible instant at the tail (A2 stops DoTick from ratcheting a mirrored row's PosMs to
            // the duration, but a position that legitimately IS near the end must still not reload right at it).
            EntityRef row = s.Current;
            EntityId id = s.CurrentId;
            QueueCursor cursor = s.Cursor;
            int fromMs;
            ResumeStart verdict = ResumeStart.For(s.PosMs, s.DurationMs);
            if (verdict.Kind == ResumeStart.VerdictKind.StartNext)
            {
                EntityRef next = NaturalNext(in s, out QueueCursor nextCursor);
                if (!next.IsNone) { row = next; id = next.Id; cursor = nextCursor; }
                fromMs = 0;
            }
            else fromMs = verdict.Ms;

            PutOnDeck(ref s, in i, row, id, cursor, KindOfRow(row, s.VideoWanted), fromMs, paused: false);
            s.StartReason = PlayReason.PlayButton;
            EmitLoad(ref s, ref fx, LoadOrigin.Claim, paused: false);
            if (!id.IsEmpty && !RowNamesId(row, id)) { fx.Fetch = true; fx.FetchId = id; fx.FetchEpoch = s.Epoch; }
            if (takeover) fx.TakeoverSeed = true;
            return;
        }
        Ownership.Claim(ref s.Own, ClaimCause.UserResume, ++s.PublishSeq, s.StartedPlayingAtMs, i.NowMs, acknowledged: true);
        s.PosQpc = i.NowMs;
        s.Phase = Phase.Playing;
        Bump(ref s);
        fx.ResumeHost = true;
        fx.TransportEpoch = s.Epoch;
        ReportResumed(ref s, ref fx, s.PosMs);
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    static void DoSeek(ref State s, in Input i, ref Effects fx)
    {
        if (s.NoSeek) return;
        if (!s.RoutesLocal) { Forward(ref s, RemoteCmd.SeekTo, ref fx, i.IntArg, false); return; }
        int ms = SeekTarget.Clamp(i.IntArg, s.DurationMs);
        if (s.Parked)
        {
            // Nothing live to seek: the parked deck just moves where its eventual load will start.
            s.PosMs = ms;
            s.PosQpc = i.NowMs;
            fx.SmtcTimeline = true;
            fx.Snapshot = true;
            return;
        }
        ReportSeeked(ref s, ref fx, s.Position(i.NowMs), ms);
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
            // phone (its own route, not a player command) and our own sink is untouched. The slider moves at once; the
            // phone's next cluster confirms it.
            Forward(ref s, RemoteCmd.Unknown, ref fx, wire, false);
            if (fx.SendRemote) { fx.RemoteIsVolume = true; s.MirrorVolume = wire / (float)MaxWireVolume; }
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

    /// <summary>Shuffle on/off. Locally it is not a flag but an ORDER (G-080): the rows ahead of the cursor are shuffled,
    /// or put back in the context's own order, by the host (<see cref="Effects.Reorder"/>) — unless the input says the
    /// queue was already built in that order (<see cref="Input.ShuffleOrdered"/>).</summary>
    static void DoShuffle(ref State s, in Input i, ref Effects fx)
    {
        bool on = (i.IntArg & 1) != 0;
        if (!s.RoutesLocal) { Forward(ref s, RemoteCmd.SetShufflingContext, ref fx, 0, on); return; }
        if (s.Shuffle == on) return;
        s.Shuffle = on;
        Bump(ref s);
        if ((i.IntArg & Input.ShuffleOrderedBit) == 0) { fx.Reorder = true; fx.ReorderShuffle = on; }
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
        ArmNext(ref s, ref fx);                          // repeat changes what a natural end continues into
        CheckRefill(ref s, ref fx);
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
                s.AutoSkips = 0;                          // audio is out: the dead-row run, if any, is over
                s.PosMs = (int)i.LongArg;
                s.PosQpc = i.NowMs;
                Bump(ref s);
                ReportStart(ref s, ref fx, s.StartReason, s.PosMs);   // once per row: a reload's Started finds it open
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
                if (s.Phase == Phase.Playing)
                {
                    s.PosMs = s.Position(i.NowMs);
                    s.PosQpc = i.NowMs;
                    s.Phase = Phase.Paused;
                    ReportPaused(ref s, ref fx, s.PosMs);          // the host paused on its own (a device, a focus loss)
                }
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
            {
                var fault = (Fault)Math.Clamp(i.LongArg, 0, (long)Fault.Unknown);
                // A video load gets its retry and then falls back to audio at the carried position (G-142).
                if (s.Kind == PlayableKind.Video && s.RoutesLocal) { DoVideoFault(ref s, in i, ref fx, retry: true, fault); break; }
                // A row the deck ADVANCED onto on its own — a natural end, a Next — whose fault is terminal for the row
                // is stepped past like a dead disc in a changer: the next playable row loads in this same drain (the
                // same Advance a natural end runs, so it too skips ruled-dead rows, pages, autoplays or ends), the
                // skipped identity is surfaced for a toast, and the bounded guard keeps a broken context from spinning.
                // A row the listener chose, a session-level fault, repeat-one and a tripped guard park below, with Retry.
                if (s.RoutesLocal && AutoSkip.Skips(fault, s.LoadWhy, s.Repeat == RepeatMode.Track, s.AutoSkips))
                {
                    s.AutoSkips++;
                    fx.SkippedUnavailable = true;
                    fx.SkippedId = s.CurrentId;
                    ReportEnd(ref s, ref fx, PlayReason.TrackError, s.Position(i.NowMs));
                    Advance(ref s, in i, ref fx, forward: true, PlayReason.TrackDone);
                    break;
                }
                // The load is dead. Keep the row on the deck so the bar can say WHAT failed, drop to Paused, and
                // release nothing: a failed load is not a transfer.
                ReportEnd(ref s, ref fx, PlayReason.TrackError, s.Position(i.NowMs));
                s.Phase = Phase.Paused;
                s.Buffering = false;
                s.Error = fault == Fault.None ? Fault.Unknown : fault;
                s.Parked = true;
                s.EndingSoon = false;
                s.NextId = default;
                s.NextArmed = false;
                Bump(ref s);
                fx.Stop = true;
                fx.StopWhy = StopReason.Failed;
                fx.TransportEpoch = s.Epoch;
                Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
                fx.Smtc = true;
                fx.SmtcEpoch = s.Epoch;
                break;
            }

            case AudioSignal.Stopped:
                if (s.Phase is Phase.Playing or Phase.Loading) { s.Phase = Phase.Paused; Bump(ref s); }
                break;

            case AudioSignal.HandedOff: DoHandedOff(ref s, in i, ref fx); break;
            case AudioSignal.EndingSoon: DoEndingSoon(ref s, ref fx); break;
            case AudioSignal.DeviceReload: DoDeviceReload(ref s, in i, ref fx); break;

            case AudioSignal.VideoUnavailable:
                if (s.Kind == PlayableKind.Video && s.RoutesLocal) DoVideoFault(ref s, in i, ref fx, retry: false, Fault.Unavailable);
                break;
        }
    }

    /// <summary>The stream ran out with NOTHING prepared — the hard-cut fallback of D4 (a prepared row arrives as
    /// <see cref="AudioSignal.HandedOff"/> instead). Advancing INLINE (rather than posting a second input) still matters:
    /// the next Load lands in the SAME drain, so the pump is asked for it before the frame ends.</summary>
    static void DoEnded(ref State s, in Input i, ref Effects fx)
    {
        if (i.Epoch != s.LoadEpoch) return;
        s.HasBeenPlayingForMs += Math.Max(0, i.NowMs - s.PosQpc);
        Advance(ref s, in i, ref fx, forward: true, PlayReason.TrackDone);
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

        if (stop) LoseHost(ref s, in i, ref fx, StopReason.LostOwnership, PlayReason.Remote);
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

    /// <summary>The local host stops holding the row, WITHOUT a bump of its own (the caller has bumped once for the
    /// whole transition, C4): the registration closes, the in-flight load is superseded, the deck parks and the next-row
    /// arm resets. What a lost ownership, a transfer away and a release share.</summary>
    static void LoseHost(ref State s, in Input i, ref Effects fx, StopReason why, PlayReason reason)
    {
        ReportEnd(ref s, ref fx, reason, s.Position(i.NowMs));
        s.Phase = Phase.Idle;
        s.Buffering = false;
        s.Parked = true;
        s.EndingSoon = false;
        s.NextId = default;
        s.NextArmed = false;
        s.LoadEpoch = s.Epoch;                            // every in-flight load for the old epoch is now superseded
        fx.Load = false;
        fx.PrepareNext = false;
        fx.Prefetch = false;
        fx.Stop = true;
        fx.StopWhy = why;
        fx.TransportEpoch = s.Epoch;
    }

    /// <summary>Where a MIRRORED row's position comes from, A2: extrapolate ONCE, from the CLUSTER's own clock, and
    /// say when the report itself is too old to trust as "still playing".
    ///
    /// <para>The bug this replaces: <see cref="MirrorRemote"/> used to stamp the mirrored position with OUR frame
    /// clock (<c>s.PosQpc = i.NowMs</c>) and leave it to <see cref="DoTick"/>, which folded <see cref="State.Position"/>
    /// back into <c>PosMs</c> every second with NO owner test. A row a local pump never drives has nothing else
    /// moving it forward, so a "playing" mirror that stopped hearing from its owner simply ratcheted, one second at a
    /// time, straight into <see cref="State.Position"/>'s own duration clamp and sat there — the next Resume then
    /// loaded at exactly the end (<c>docs/plans/wavee/explain-why-palyback-is-indexed-cat.md</c>, "what happened"
    /// #1-4). <see cref="DoTick"/> now folds only for <see cref="Owner.Us"/>; a mirrored row's <c>PosMs</c> is
    /// written exactly once per cluster, here, and <see cref="State.Position"/>'s existing extrapolation off
    /// <c>PosQpc</c> keeps the bar moving between clusters with nothing re-folding it.</para></summary>
    public static class MirrorSnapshot
    {
        /// <summary>One projected mirror fact: the position to paint, whether the remote is still audibly playing,
        /// and whether the report was already too old to trust as "playing" at all.</summary>
        public readonly record struct Projected(int PosMs, bool Playing, bool Stale);

        /// <summary>Extrapolate a remote's reported position ONCE, from its OWN clock — never <c>nowMs</c>, a local
        /// receipt time a slow dealer round trip (or the seconds since the last heartbeat) can already have skewed
        /// well past what the report itself says. A playing report whose age (the server-clock gap between
        /// <paramref name="wireTimestampMs"/> and <paramref name="serverNowMs"/>) already exceeds what was left of
        /// the row is STALE — the track ended on the owner, whether that was noticed then or only now — and mirrors
        /// as PAUSED at the row's end, never ratcheted past it.</summary>
        /// <param name="positionAsOfMs">The remote's own position, true at <paramref name="wireTimestampMs"/>.</param>
        /// <param name="wireTimestampMs">The server clock <paramref name="positionAsOfMs"/> was true at
        /// (<see cref="RemoteState.TimestampMs"/>).</param>
        /// <param name="serverNowMs">The server clock this very push carries (<see cref="ClusterFrame.ServerTs"/>) —
        /// the best "now" available without reading one. 0 (not carried) means no extrapolation happens, which is
        /// honest: a push with no server time cannot be aged against anything.</param>
        /// <param name="playing">The remote's own intent, already folded with is_paused by the caller.</param>
        /// <param name="durationMs">The row's duration; ≤ 0 (unknown) can never be judged stale either.</param>
        public static Projected Project(long positionAsOfMs, long wireTimestampMs, long serverNowMs, bool playing, int durationMs)
        {
            long pos = positionAsOfMs < 0 ? 0 : positionAsOfMs;
            if (durationMs > 0 && pos > durationMs) pos = durationMs;
            if (!playing || durationMs <= 0) return new Projected((int)pos, playing, false);

            long elapsed = serverNowMs > wireTimestampMs ? serverNowMs - wireTimestampMs : 0;
            long remaining = durationMs - pos;
            if (elapsed >= remaining) return new Projected(durationMs, false, true);
            return new Projected((int)(pos + elapsed), true, false);
        }
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
        int durMs = (int)Math.Clamp(r.DurationMs, 0, int.MaxValue);
        MirrorSnapshot.Projected proj = MirrorSnapshot.Project(r.PositionAsOfMs, r.TimestampMs, i.Frame.ServerTs,
            r.IsPlaying && !r.IsPaused, durMs);
        s.Phase = !r.HasTrack ? Phase.Idle : proj.Playing ? Phase.Playing : Phase.Paused;
        s.Buffering = r.IsBuffering;
        s.Error = Fault.None;
        s.PosMs = proj.PosMs;
        s.PosQpc = i.NowMs;
        s.DurationMs = durMs;
        if (!r.Context.IsEmpty) s.MirrorContext = r.Context;   // cached for a later takeover (A4); never painted here
        s.Shuffle = r.Shuffling;
        s.Repeat = r.Repeat;
        s.NoNext = r.NoNext;
        s.NoPrev = r.NoPrev;
        s.NoSeek = r.NoSeek;
        s.StreamFormat = StringId.Empty;
        // The slider follows the OWNER; our own volume (the sink's, the PUT body's) is untouched.
        if (r.Volume >= 0) s.MirrorVolume = Math.Clamp(r.Volume / (float)MaxWireVolume, 0f, 1f);
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
        // The PUT this command causes is attributed to it, so the controller sees its own id come back (G-073) — for
        // CommandAttribution.WindowMs, and no longer (G-246): stamped here, aged when the snapshot is captured.
        if (c.MessageId != 0)
        {
            s.LastCommandMessageId = (uint)c.MessageId;
            s.LastCommandAtMs = i.NowMs;
            s.LastCommandSender = c.SenderHash;
        }

        switch (c.Kind)
        {
            case RemoteCmd.Play:
            case RemoteCmd.Transfer:
                // An inbound play/transfer IS a claim: the controller addressed US, so we own playback from this
                // Step, before any cluster confirms it. The host resolves what it asks for (`RemoteLoadArrived`) and
                // folds the real Play with the inbound cause.
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
                Advance(ref s, in i, ref fx, forward: true, PlayReason.Remote);
                break;

            case RemoteCmd.SkipPrev:
                Ownership.Claim(ref s.Own, ClaimCause.InboundSkip, ++s.PublishSeq, s.StartedPlayingAtMs, i.NowMs, true);
                Advance(ref s, in i, ref fx, forward: false, PlayReason.Remote);
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

            // A controller that queues onto us has addressed us: the claim is ours, and the write is the host's
            // (`Queue.Enqueue`, G-074) — whose version bump re-arms the next row on the following drain.
            case RemoteCmd.AddToQueue:
                Ownership.Claim(ref s.Own, ClaimCause.InboundQueueStart, ++s.PublishSeq, s.StartedPlayingAtMs, i.NowMs, true);
                if (!c.Track.IsEmpty) { fx.QueueAdd = true; fx.QueueAddId = c.Track; }
                Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
                break;

            // set_queue / update_context: the claim and the PUT attributed to the sender are folded here; their ROWS are
            // decoded by `Decode.ConnectQueue` and spliced behind the deck by the host's intake (`RunQueue`,
            // Playback.Host.Remote.cs) in the same drain — before this announce is executed, so the PUT carries the new
            // queue. set_options never reaches here: the glue folds it to the shuffle / repeat verbs above.
            case RemoteCmd.SetQueue:
            case RemoteCmd.UpdateContext:
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
        s.StreamFormat = StringId.Empty;
        Bump(ref s);
        LoseHost(ref s, in i, ref fx, StopReason.Released, PlayReason.Remote);
        s.TransferEpoch = s.Epoch;
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
        var cause = (ReleaseCause)i.IntArg;
        bool wasUs = s.Own.Kind == Owner.Us;
        OwnerFx owner = Ownership.Release(ref s.Own, cause);
        if (cause == ReleaseCause.Logout) { SignOut(ref s, in i, ref fx, wasUs); return; }
        if (owner == OwnerFx.None) return;
        if ((owner & OwnerFx.StopHost) != 0)
        {
            s.StreamFormat = StringId.Empty;
            Bump(ref s);
            LoseHost(ref s, in i, ref fx, StopReason.Released, PlayReason.EndPlay);
        }
        if ((owner & OwnerFx.PublishInactive) != 0) Announce(ref s, ref fx, PublishReason.BecameInactive);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    /// <summary>The account signed out (G-036): whatever owned playback, the deck is the account's and it goes — the
    /// host stops, the registration closes, the mirrored foreign row is dropped, and the epochs move on so every result
    /// still in flight for the old session is stale. This device's identity, its volume and the placement wish stay.</summary>
    static void SignOut(ref State s, in Input i, ref Effects fx, bool wasUs)
    {
        ReportEnd(ref s, ref fx, PlayReason.Logout, s.Position(i.NowMs));
        ulong us = s.Us;
        float volume = s.Volume;
        bool video = s.VideoWanted;
        uint epoch = s.Epoch, seq = s.PublishSeq;
        s = State.Initial;
        s.Us = us;
        s.Volume = volume;
        s.VideoWanted = video;
        s.Epoch = epoch;
        s.PublishSeq = seq;
        s.Own.Cause = wasUs ? NobodyCause.FromUs : NobodyCause.Launch;
        Bump(ref s);
        s.LoadEpoch = s.Epoch;
        s.TransferEpoch = s.Epoch;
        fx.Load = false;
        fx.PrepareNext = false;
        fx.Prefetch = false;
        fx.Stop = true;
        fx.StopWhy = StopReason.Released;
        fx.TransportEpoch = s.Epoch;
        if (wasUs) Announce(ref s, ref fx, PublishReason.BecameInactive);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
        fx.Snapshot = true;
    }

    static void DoStop(ref State s, StopReason why, ref Effects fx)
    {
        if (s.Phase == Phase.Idle) return;
        s.Phase = Phase.Idle;
        s.Buffering = false;
        s.Parked = true;
        s.EndingSoon = false;
        s.NextId = default;
        s.NextArmed = false;
        s.ReportOpen = false;                            // a stop is not a play event the service is told about
        Bump(ref s);
        s.LoadEpoch = s.Epoch;
        fx.Stop = true;
        fx.StopWhy = why;
        fx.TransportEpoch = s.Epoch;
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    /// <summary>The machine is going to sleep (D13, G-081): pause LOCAL playback and nothing else. A foreign owner is
    /// never forwarded a pause — a sleeping laptop must not pause somebody's speaker — and a deck that is not playing is
    /// left exactly as it is.</summary>
    static void DoSuspend(ref State s, in Input i, ref Effects fx)
    {
        if (!s.RoutesLocal || s.Phase is not (Phase.Playing or Phase.Loading)) return;
        s.PosMs = s.Position(i.NowMs);
        s.PosQpc = i.NowMs;
        s.Phase = Phase.Paused;
        Bump(ref s);
        fx.PauseHost = true;
        fx.TransportEpoch = s.Epoch;
        ReportPaused(ref s, ref fx, s.PosMs);
        fx.Snapshot = true;
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    /// <summary>…and waking (D13, G-082). A suspended machine loses its server-side device registration even though
    /// the socket still looks alive, so the device RE-ANNOUNCES as a new connection (0.2.9's
    /// <c>AnnounceNewConnection</c> after <c>PBT_APMRESUMEAUTOMATIC</c>); the tick folds the lost time and expires a
    /// protection window that ran out while asleep. Playback does not resume by itself.</summary>
    static void DoWake(ref State s, in Input i, ref Effects fx)
    {
        DoTick(ref s, in i, ref fx);
        Announce(ref s, ref fx, PublishReason.NewConnection);
    }

    // ── 9.4 the ticker and the live window ──────────────────────────────────────────────────────────────────────────

    /// <summary>The 1 s ticker (P10, named). It folds the extrapolation into <see cref="State.PosMs"/> so every reader
    /// sees ONE number, arms the OS timeline latch, and expires the ownership protection window. It must produce NO
    /// other effect: a tick that announced would be a PUT per second.</summary>
    static void DoTick(ref State s, in Input i, ref Effects fx)
    {
        // A2: fold only OUR OWN pump's position. A mirrored (Foreign) or departed (Nobody) row has nothing here
        // driving it forward — folding it anyway is the exact ratchet-to-the-duration bug this gate exists to stop;
        // `State.Position` still extrapolates it for display, off the single snapshot `MirrorRemote` wrote.
        if (s.Phase == Phase.Playing && s.Own.Kind == Owner.Us)
        {
            int now = s.Position(i.NowMs);
            s.HasBeenPlayingForMs += Math.Max(0, now - s.PosMs);
            s.PosMs = now;
            s.PosQpc = i.NowMs;
            fx.SmtcTimeline = true;
        }
        OwnerFx owner = Ownership.Tick(ref s.Own, i.NowMs);
        if ((owner & OwnerFx.StopHost) == 0) return;
        Bump(ref s);
        LoseHost(ref s, in i, ref fx, StopReason.LostOwnership, PlayReason.Remote);
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

    /// <summary>A local Play while another device owns playback: forward "play this context from this row" to it
    /// (<see cref="RemoteCmd.PlayContext"/>). The context is the play's own; a row played on its own (no context) names
    /// the row as the context, which is how the desktop plays a single track. Our shuffle setting rides along as the
    /// player option override, as 0.2.9's envelope did.</summary>
    static void ForwardPlay(ref State s, in Input i, ref Effects fx)
    {
        if (i.Id.IsEmpty) return;
        Forward(ref s, RemoteCmd.PlayContext, ref fx, 0, s.Shuffle);
        fx.RemoteContext = i.Context.IsEmpty ? i.Id : i.Context;
        fx.RemoteTrack = i.Id;
    }

    // ── 10. the snapshot the PUT body encodes, and its parity half, live in Playback.Wire.cs ─────────────────────────

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
