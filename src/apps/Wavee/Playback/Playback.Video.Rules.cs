// ── Playback/Playback.Video.Rules.cs ───────────────────────────────────────────────────────────────────────────────
// the pure video decisions: what to prefetch, where a seek lands, when the song's audio cuts, what the store keeps
//
// Role: CORE
// Owner: V
// Wave: 3
// Budget: 350 lines
// Spec: docs/plans/wavee/wavee-0.3-video-engine-implementation.md §3.1.4, §3.1.5, §3.2.2, §3.2.3, §3.5; ch 24 §7, §9
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT THIS FILE IS. Every decision the video host makes that is not a call into Media Foundation. `Playback.Video.cs`
// (owner H) owns the player, the pump, the watchdog and the threads; this file owns the ARITHMETIC it runs on, so the
// answers are pinned by `Wavee.Tests/VideoRulesTests.cs` instead of by a live DRM stream nobody can replay.
//
// CORE discipline (P8/P9/P10/C4): no allocation after warm-up, no LINQ, no closures, no `async`, no boxing, no
// `Enum.ToString`, no timer. Every timestamp is a value the caller passes in — FRAME TIME from the engine's
// `FrameTime.NowQpc` / a media sample's QPC, NEVER `Environment.TickCount64`. Nothing here touches a `Signal`, an
// engine type or a table; `SwitchPhase` is an enum so H can declare `Signal<SwitchPhase> Phase` beside the host.
//
// THE TEN SEEK BUGS (§1.4.3), and the rule that prevents each:
//   S1 open at 0:00 then jump        → `PrefetchSchedule.Decide` (Full = init + 8 s AT P) + `SeekPlanner.OpenAt`,
//                                      which plans the segment that HOLDS P — segment 0 is never the answer.
//   S2 verbs late by one 80 ms tick  → `SeekVerb.Ride` / `Instant`: the app never asks for a fetch it does not need,
//                                      so nothing waits on a tick. (The apply-now half is native, E1.)
//   S3 two serial GETs for a seek    → ONE `SeekPlan.SegmentIndex`: video and audio ride the same grid
//                                      (`SegmentGrid`), so a far seek is one index fetched on two streams in parallel.
//   S4 back-seek > ~6-10 s refetches → `RetentionWindow`: 30 s behind / 60 s ahead in TIME, byte-capped.
//   S5 "landed" waits 750 ms         → `SeekPlan.KeyframeMs` + `DecodeToTargetMs` are exact; no tolerance constant
//                                      exists in this file, so a keyframe seek can never wait on one.
//   S6 a drag leaves ack waiters     → `Plan` is a pure function returning a value. There is nothing to await.
//   S7 position stair-steps          → `PositionClock.At`: a timestamped sample extrapolated by QPC, not a poll.
//   S8 the preview never reaches here→ `SeekIntent` is an input: `Preview` and `Commit` plan differently.
//   S9 a seek right after open drops → `OpenAt` is the SAME planner: the open carries its own plan, so there is no
//                                      "seek issued before the session is ready" window to lose one in.
//   S10 a paused seek shows the old  → `SeekIndex.Playing`: `Ride` ("let playback reach it") is unrepresentable while
//       frame / Play is re-asserted     paused, so a paused seek always repositions and nothing infers Playing.
// S2's apply-now, S3's parallel GETs, S6's deleted waiters and S7's event are engine work (E1/E2); the rules above are
// what this half contributes to each.

namespace Wavee;

public static partial class Playback
{
    public static partial class Video
    {
        // ── 1. where a switch is (K's discriminator; H writes the signal) ───────────────────────────────────────────

        /// <summary>Where a song→video switch has got to. The poster/spinner discriminator (§3.4): `Presenting` is the
        /// moment the poster cross-fades, and a `Seeking` under <see cref="Budgets.JoiningNoSpinnerMs"/> shows none.</summary>
        public enum SwitchPhase : byte { Idle, Resolving, Licensing, Buffering, Attaching, Presenting, Playing, Failed }

        // ── 2. prefetch — what to have in hand before the toggle is pressed (§3.1.5) ────────────────────────────────

        /// <summary>What to prefetch for a track, and when. Pure. Inputs are the facts the reducer already has.</summary>
        public enum PrefetchLevel : byte { None, Manifest, ManifestAndLicense, Full }

        /// <summary>Why a level was asked for — the `why=` field of `[video] prefetch.plan`.</summary>
        public enum PrefetchReason : byte { None, Badge, Current, Next }

        public readonly record struct PrefetchInput(
            bool HasVideo, bool VideoOn, bool Metered, bool IsCurrent, int MsToBoundary, PrefetchLevel Already, bool ManifestFresh);

        public static class PrefetchSchedule
        {
            public const int NextTrackWindowMs = 20_000;      // "ending soon": the same window Audio.Prepare uses for gapless
            public const int ManifestTtlMs = 10 * 60_000;     // 0.2.9's SingleFlightMemo TTL; signed CDN urls outlive it
            public const int PrefetchAheadMs = 8_000;         // Full = the init segments + this much media AT the carried position

            /// <summary>The level to reach NOW. Never asks for bytes on a metered link; never asks for a license for a
            /// track whose video will not be shown (VideoOn is the placement != None ∨ the user's video preference).</summary>
            public static PrefetchLevel Decide(in PrefetchInput i)
            {
                if (!i.HasVideo) return PrefetchLevel.None;
                PrefetchLevel want;
                if (!i.VideoOn) want = i.IsCurrent ? PrefetchLevel.Manifest : PrefetchLevel.None;   // the badge is lit: know WHAT, cost one GET
                else if (i.IsCurrent) want = i.Metered ? PrefetchLevel.ManifestAndLicense : PrefetchLevel.Full;
                else if (i.MsToBoundary <= NextTrackWindowMs) want = i.Metered ? PrefetchLevel.ManifestAndLicense : PrefetchLevel.Full;
                else want = PrefetchLevel.None;
                if (i.Already >= want && (i.ManifestFresh || want == PrefetchLevel.None)) return PrefetchLevel.None;
                return want;
            }

            /// <summary>The `why=` of the line. Independent of <see cref="Decide"/>'s already-have short-circuit, so the log
            /// says what the track IS, not what is left to do for it.</summary>
            public static PrefetchReason WhyFor(in PrefetchInput i)
            {
                if (!i.HasVideo) return PrefetchReason.None;
                if (!i.VideoOn) return i.IsCurrent ? PrefetchReason.Badge : PrefetchReason.None;
                if (i.IsCurrent) return PrefetchReason.Current;
                return i.MsToBoundary <= NextTrackWindowMs ? PrefetchReason.Next : PrefetchReason.None;
            }

            /// <summary>A manifest younger than the TTL is reused; a negative age is a clock nobody can trust and is stale.</summary>
            public static bool IsFresh(long ageMs) => ageMs >= 0 && ageMs < ManifestTtlMs;

            /// <summary>How many segments `Full` puts in the store at the carried position: <see cref="PrefetchAheadMs"/>
            /// rounded UP, never fewer than one (the segment holding P is the whole point — S1).</summary>
            public static int SegmentsFor(long segmentLengthMs)
                => segmentLengthMs <= 0 ? 1 : (int)((PrefetchAheadMs + segmentLengthMs - 1) / segmentLengthMs);
        }

        // ── 2b. the prefetch/load race, and the warm keeper (§3.1.5, §6.2 G1, D15) ──────────────────────────────────

        /// <summary>A row's TWO identities: the playable uri (known the moment a row is named) and the resolved source
        /// key (known only once its manifest landed). Either half is enough to say two mentions are the same row, which
        /// is what the race below is decided on — the prefetch names a row before its key exists, and the load names a
        /// key whose row the pump was never told.</summary>
        public readonly record struct RowKey(string Uri, string Key)
        {
            public static readonly RowKey None = new("", "");

            public bool IsNone => Uri.Length == 0 && Key.Length == 0;
        }

        /// <summary>What becomes of a prepare that is still in the air when a load starts.</summary>
        public enum PrepareFate : byte
        {
            /// <summary>Nothing is in flight — the load opens on whatever it finds.</summary>
            Idle,
            /// <summary>The prepare is for the row being loaded: the load WAITS for it and opens onto its session.</summary>
            Adopt,
            /// <summary>The prepare is for some other row: its session is dead on arrival and is disposed the moment it
            /// lands, never left running for nobody.</summary>
            Drop,
        }

        /// <summary>The prefetch/load race, decided in ONE place (§6.2 G1). Before these two rules a lit badge's click
        /// opened two protected sessions in the same millisecond — the prefetch's (paused, never taken) and the load's —
        /// and only the second was ever destroyed, on a machine with 128 MB of shared VRAM.</summary>
        public static class PrefetchRace
        {
            /// <summary>The same row? Either identity matching is enough; two unknowns never match, because an empty
            /// side is an ABSENCE and not an identity.</summary>
            public static bool Same(in RowKey a, in RowKey b)
                => (a.Uri.Length > 0 && b.Uri.Length > 0 && string.Equals(a.Uri, b.Uri, StringComparison.Ordinal))
                || (a.Key.Length > 0 && b.Key.Length > 0 && string.Equals(a.Key, b.Key, StringComparison.Ordinal));

            /// <summary>RULE 1 — a row the pump has already CLAIMED (a Load queued for it, opening it, or live on it) is
            /// never prefetched: its load IS the fetch, and a prepare racing that load is the second session the switch
            /// never takes.</summary>
            public static bool ShouldPrefetch(in RowKey claimed, in RowKey row) => !row.IsNone && !Same(in claimed, in row);

            /// <summary>RULE 2 — a load ADOPTS the prepare aimed at its own row instead of racing it, and DROPS one aimed
            /// anywhere else rather than letting it land unowned. The two arms are TOTAL over "a prepare is in flight",
            /// which is what turns "every prepared session is adopted or disposed" from a hope into a property.</summary>
            public static PrepareFate FateOf(bool preparing, in RowKey preparingRow, in RowKey loading)
                => !preparing ? PrepareFate.Idle
                 : Same(in preparingRow, in loading) ? PrepareFate.Adopt
                 : PrepareFate.Drop;
        }

        /// <summary>D15's keeper. The licence is ~80 % of a cold switch (measured: 2 010 ms of a 2 477 ms first frame),
        /// and the native runtime's create is most of the rest (521 ms) — so while a video surface is wanted the app
        /// keeps the content keys of the playing row and the next queued one in hand, and lets go
        /// <see cref="ShedMs"/> after the surface closes. Pure: the host supplies the facts and owns the timer.</summary>
        public static class WarmPolicy
        {
            /// <summary>The beat. Deliberately well under <see cref="ShedMs"/>: the engine destroys its native runtime
            /// that long after the last session detaches, and a beat landing inside the window brings it back before the
            /// user's next switch has to pay <see cref="Budgets.FirstVideoOfProcessMs"/> all over again.</summary>
            public const int HeartbeatMs = 10_000;

            /// <summary>D15's "30 s after off" — the same window as the engine's own
            /// <c>ProtectedVideoRuntime.WarmIdleDisposeMs</c>, so the app never outlives the runtime it warmed and never
            /// extends its life either.</summary>
            public const int ShedMs = 30_000;

            /// <summary>How long a load waits for the prepare it is adopting before giving up and opening cold. It waits
            /// only for the backend to have REGISTERED the prepare, not for its download, and the fallback is the cold
            /// open it would otherwise have done — so a stalled prepare costs nothing that was not already lost.</summary>
            public const int AdoptBudgetMs = 1_500;

            /// <summary>Is the keeper beating? Only while a video surface is wanted AND the user left pre-acquisition on
            /// — stopping the licence POSTs for videos nobody may watch is the setting's whole job.</summary>
            public static bool Beats(bool videoOn, bool prepareAhead) => videoOn && prepareAhead;

            /// <summary>Does the keeper let go? At once when the setting is off, and <see cref="ShedMs"/> after the last
            /// surface closed — never while one is showing, however long it has been open.</summary>
            public static bool Sheds(bool videoOn, bool prepareAhead, long msSinceOff)
                => !prepareAhead || (!videoOn && msSinceOff >= ShedMs);

            /// <summary>Does this beat ask for a key? Audio ALWAYS wins (§3.1.4): a licence POST never shares the api
            /// pool with a track the user has just started. A key already usable — or one whose challenge is already in
            /// flight — is never asked for twice, because a second challenge for one KID is the one the CDM rejects.</summary>
            public static bool Acquires(bool videoOn, bool prepareAhead, bool audioBusy, bool keyInHand)
                => Beats(videoOn, prepareAhead) && !audioBusy && !keyInHand;
        }

        // ── 3. the seek planner (§3.2.2) ────────────────────────────────────────────────────────────────────────────

        /// <summary>The seek modes the UI has (Media3's SeekParameters, reduced to what a scrubber needs).</summary>
        public enum SeekIntent : byte { Commit, Preview }

        public enum SeekVerb : byte
        {
            /// <summary>A keyframe ≤ target is buffered: reposition now, decode to target (Commit) or show the keyframe (Preview).</summary>
            Instant,
            /// <summary>Fetch ONE segment pair (video ∥ audio) at <see cref="SeekPlan.SegmentIndex"/>, then Instant.</summary>
            Fetch,
            /// <summary>Preview only: the closest KNOWN keyframe that is buffered — never a fetch while the pointer is down.</summary>
            Coarse,
            /// <summary>The target is the current segment and within one GOP ahead: let playback reach it (no seek at all).</summary>
            Ride,
        }

        /// <summary>The plan for one seek: what to do, where the decoder starts, how many ms it must decode past the keyframe.</summary>
        public readonly record struct SeekPlan(SeekVerb Verb, long KeyframeMs, int SegmentIndex, long DecodeToTargetMs);

        /// <summary>Everything the planner reads. Spans over the host's fixed buffers (P8): no allocation per plan.</summary>
        public readonly ref struct SeekIndex
        {
            public readonly ReadOnlySpan<long> Keyframes;    // ascending presentation ms of every sync sample the demuxer has seen
            public readonly ReadOnlySpan<long> Buffered;     // ascending (start, end) ms pairs
            public readonly long SegmentLengthMs;            // Spotify: segment_length × 1000 (4 000 by default)
            public readonly long DurationMs;
            public readonly long PositionMs;                 // the current playhead
            public readonly bool Playing;                    // S10: nothing rides a stopped decoder

            public SeekIndex(ReadOnlySpan<long> keyframes, ReadOnlySpan<long> buffered, long segmentLengthMs,
                             long durationMs, long positionMs, bool playing)
            {
                Keyframes = keyframes; Buffered = buffered; SegmentLengthMs = segmentLengthMs;
                DurationMs = durationMs; PositionMs = positionMs; Playing = playing;
            }
        }

        public static class SeekPlanner
        {
            /// <summary>Ride instead of seek when the target is ahead of the playhead by less than this (one segment): the
            /// decoder is already producing those frames (Media3 lets a forward seek inside the buffer ride when it maps to
            /// the same sync; Chromium's SourceBufferRange fudge room is the same idea).</summary>
            public const long RideAheadMs = 250;

            public static SeekPlan Plan(in SeekIndex ix, long targetMs, SeekIntent intent)
            {
                long t = Clamp(in ix, targetMs);
                if (intent == SeekIntent.Commit && ix.Playing && t > ix.PositionMs && t - ix.PositionMs <= RideAheadMs && IsBuffered(ix.Buffered, t))
                    return new SeekPlan(SeekVerb.Ride, ix.PositionMs, SegmentOf(in ix, t), 0);

                long kf = PreviousKeyframe(ix.Keyframes, t);              // -1 when nothing ≤ t is known
                if (kf >= 0 && IsBuffered(ix.Buffered, kf) && IsBuffered(ix.Buffered, t))
                    return new SeekPlan(SeekVerb.Instant, kf, SegmentOf(in ix, kf), t - kf);

                int seg = SegmentOf(in ix, t);
                long segStart = seg * ix.SegmentLengthMs;                 // a segment start is ALWAYS a keyframe (DASH); it may not be in the table yet
                if (intent == SeekIntent.Preview)
                {
                    long near = ClosestBufferedKeyframe(ix.Keyframes, ix.Buffered, t);
                    return near >= 0 ? new SeekPlan(SeekVerb.Coarse, near, SegmentOf(in ix, near), 0)
                                     : new SeekPlan(SeekVerb.Fetch, segStart, seg, 0);   // nothing buffered near it: one fetch, keyframe shown
                }
                return new SeekPlan(SeekVerb.Fetch, segStart, seg, t - segStart);
            }

            /// <summary>The carried position of a song→video switch, planned BEFORE the open (S1, S9). The same function:
            /// an open is a seek whose playhead does not exist yet, so `Ride` cannot fire and `Fetch` names the segment
            /// that HOLDS P — the reason the first frame is at P and never at 0:00.</summary>
            public static SeekPlan OpenAt(in SeekIndex ix, long startMs) => Plan(in ix, startMs, SeekIntent.Commit);

            public static int SegmentOf(in SeekIndex ix, long ms) => ix.SegmentLengthMs > 0 ? (int)(ms / ix.SegmentLengthMs) : 0;

            /// <summary>Binary search: the last keyframe ≤ ms, or -1.</summary>
            public static long PreviousKeyframe(ReadOnlySpan<long> keyframes, long ms)
            {
                int lo = 0, hi = keyframes.Length - 1, ans = -1;
                while (lo <= hi) { int mid = (lo + hi) >> 1; if (keyframes[mid] <= ms) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
                return ans < 0 ? -1 : keyframes[ans];
            }

            /// <summary>Start inclusive, end exclusive — the MSE `buffered` convention a web scrub bar reads.</summary>
            public static bool IsBuffered(ReadOnlySpan<long> pairs, long ms)
            {
                for (int i = 0; i + 1 < pairs.Length; i += 2) if (ms >= pairs[i] && ms < pairs[i + 1]) return true;
                return false;
            }

            static long Clamp(in SeekIndex ix, long ms) => Math.Clamp(ms, 0, ix.DurationMs > 0 ? ix.DurationMs : long.MaxValue);

            /// <summary>Ties break BACKWARDS (the table is ascending and the compare is strict), which is `fastSeek`'s rule
            /// for a backward seek: the adjusted position must also be before the current one.</summary>
            static long ClosestBufferedKeyframe(ReadOnlySpan<long> keyframes, ReadOnlySpan<long> buffered, long ms)
            {
                long best = -1, bestDist = long.MaxValue;
                for (int i = 0; i < keyframes.Length; i++)
                {
                    long k = keyframes[i];
                    if (!IsBuffered(buffered, k)) continue;
                    long d = Math.Abs(k - ms);
                    if (d < bestDist) { best = k; bestDist = d; }
                    if (k > ms && d > bestDist) break;
                }
                return best;
            }
        }

        /// <summary>Spotify names segments by ABSOLUTE TIME on a fixed stride (`segment_length` seconds), so the index is
        /// arithmetic and no `sidx` is ever fetched (§1.5.3). Video and audio ride the SAME grid — which is why one seek
        /// is one index on two streams (S3).</summary>
        public readonly record struct SegmentGrid(long SegmentLengthMs)
        {
            public int IndexOf(long ms) => SegmentLengthMs > 0 && ms > 0 ? (int)(ms / SegmentLengthMs) : 0;

            public long StartOf(int index) => index > 0 && SegmentLengthMs > 0 ? index * SegmentLengthMs : 0;

            /// <summary>How many segments cover a presentation. The tail is a PARTIAL segment and still counts.</summary>
            public int Count(long durationMs)
                => SegmentLengthMs <= 0 || durationMs <= 0 ? 0 : (int)((durationMs + SegmentLengthMs - 1) / SegmentLengthMs);
        }

        // ── 4. the audio hand-off across the switch (§3.1.4) ────────────────────────────────────────────────────────

        /// <summary>The song is cut, not faded out and waited on: the graph's master ramp goes to zero over
        /// <see cref="FadeMs"/> and the video's soundtrack starts at the audio clock's own value plus that fade. Pure
        /// timing — the caller supplies the positions; nothing here reads a clock.</summary>
        public static class AudioHandoff
        {
            public const int FadeMs = 80;               // the TransportRamp de-click envelope the graph already owns
            public const int CutToleranceMs = 100;      // §3.5: the soundtrack's first sample within this of the fade end is a CUT
            public const int ParkTtlMs = 30_000;        // a parked audio session older than this reloads instead of resuming

            /// <summary>Where the video's soundtrack must start: the audio clock NOW plus the fade, snapped DOWN to a whole
            /// sample so both sides cut on the same frame boundary. Never earlier than now (a negative fade cannot rewind).</summary>
            public static long CutAtMs(long audioPositionNowMs, int sampleRate, int fadeMs = FadeMs)
                => SnapToSample(audioPositionNowMs + (fadeMs > 0 ? fadeMs : 0), sampleRate);

            /// <summary>The fade in whole sample frames. A requested fade always costs at least one frame, so the envelope
            /// can never be a zero-length step — that step is the click this exists to prevent.</summary>
            public static int FadeFrames(int fadeMs, int sampleRate)
            {
                if (fadeMs <= 0 || sampleRate <= 0) return 0;
                long frames = ((long)fadeMs * sampleRate + 500) / 1000;
                return frames < 1 ? 1 : (int)frames;
            }

            /// <summary>Truncate a millisecond position to the sample frame that contains it.</summary>
            public static long SnapToSample(long ms, int sampleRate)
                => sampleRate <= 0 || ms <= 0 ? (ms < 0 ? 0 : ms) : ms * sampleRate / 1000 * 1000 / sampleRate;

            /// <summary>The `gapMs=` field: silence between the end of the song's fade and the first audible video sample.
            /// Zero or negative is an overlap-free cut; positive is a gap the gate must see shrink.</summary>
            public static long GapMs(long cutAtMs, long firstAudibleVideoMs) => firstAudibleVideoMs - cutAtMs;

            /// <summary>What "cut, not gap" means as a predicate (§3.5).</summary>
            public static bool IsCut(long gapMs) => gapMs <= CutToleranceMs;

            /// <summary>Video→song: the parked session resumes in place while the video's volume ramps to zero. A park
            /// older than the TTL has lost its fast-start head and must reload instead.</summary>
            public static bool CanResumeParked(long parkedAgeMs) => parkedAgeMs >= 0 && parkedAgeMs <= ParkTtlMs;
        }

        // ── 5. the retention window (§3.2.1 item 3, §3.5) ───────────────────────────────────────────────────────────

        /// <summary>What the segment store keeps, in TIME and capped in BYTES — the fix for `kRetainBehind = 300` SAMPLES
        /// (≈ 6.4 s of AAC), which is why a ten-second backward seek refetched (S4).</summary>
        public static class RetentionWindow
        {
            public const long RetainBehindMs = 30_000;
            public const long BufferAheadMs = 60_000;
            public const long StoreBudgetBytes = 32L * 1024 * 1024;   // per live session; ≤ 2 sessions, both in the census

            public static long WindowStart(long positionMs) => positionMs > RetainBehindMs ? positionMs - RetainBehindMs : 0;

            public static long WindowEnd(long positionMs, long durationMs)
            {
                long end = positionMs + BufferAheadMs;
                return durationMs > 0 && end > durationMs ? durationMs : end;
            }

            /// <summary>Half-open overlap: a segment is kept while any of it lies inside the window.</summary>
            public static bool Keep(long segmentStartMs, long segmentEndMs, long positionMs, long durationMs)
                => segmentEndMs > WindowStart(positionMs) && segmentStartMs < WindowEnd(positionMs, durationMs);

            public static long BytesFor(long ms, int bytesPerSecond)
                => ms <= 0 || bytesPerSecond <= 0 ? 0 : ms * bytesPerSecond / 1000;

            /// <summary>The behind window is honoured FIRST — it is the cheap half and it is what makes a backward seek
            /// free. Whatever the budget has left pays for the ahead window.</summary>
            public static long BehindAffordableMs(int bytesPerSecond)
            {
                if (bytesPerSecond <= 0) return RetainBehindMs;
                long afford = StoreBudgetBytes * 1000 / bytesPerSecond;
                return afford < RetainBehindMs ? afford : RetainBehindMs;
            }

            public static long AheadAffordableMs(int bytesPerSecond)
            {
                if (bytesPerSecond <= 0) return BufferAheadMs;
                long left = StoreBudgetBytes - BytesFor(BehindAffordableMs(bytesPerSecond), bytesPerSecond);
                if (left <= 0) return 0;
                long afford = left * 1000 / bytesPerSecond;
                return afford < BufferAheadMs ? afford : BufferAheadMs;
            }

            /// <summary>True when the whole 30 s / 60 s window fits the byte budget at this bitrate — the 480p rung does.</summary>
            public static bool FitsBudget(int bytesPerSecond)
                => BehindAffordableMs(bytesPerSecond) == RetainBehindMs && AheadAffordableMs(bytesPerSecond) == BufferAheadMs;

            public static int SegmentsBehind(long segmentLengthMs)
                => segmentLengthMs <= 0 ? 0 : (int)((RetainBehindMs + segmentLengthMs - 1) / segmentLengthMs);

            public static int SegmentsAhead(long segmentLengthMs)
                => segmentLengthMs <= 0 ? 0 : (int)((BufferAheadMs + segmentLengthMs - 1) / segmentLengthMs);
        }

        // ── 6. position between events (S7) ─────────────────────────────────────────────────────────────────────────

        /// <summary>The clock the seek bar reads. A position EVENT carries the sample's own QPC; between events the value
        /// is extrapolated by elapsed·rate — never polled, never `Environment.TickCount64` (P10, and the 0.3 motion rule).</summary>
        public static class PositionClock
        {
            public static long At(long sampleMs, long sampleQpc, long nowQpc, long qpcFrequency, double rate, long durationMs)
            {
                long pos = sampleMs;
                if (qpcFrequency > 0 && rate > 0 && nowQpc > sampleQpc)
                    pos += (long)((nowQpc - sampleQpc) * 1000.0 / qpcFrequency * rate);
                if (pos < 0) pos = 0;
                return durationMs > 0 && pos > durationMs ? durationMs : pos;
            }
        }

        // ── 7. the budgets the manual gate reads (§3.5) ─────────────────────────────────────────────────────────────

        /// <summary>Named so a test can fail when a number drifts, and so `§4.3`'s gate quotes a constant and not a memory.</summary>
        public static class Budgets
        {
            public const int WarmSwitchMs = 300;              // badge lit ≥ 2 s, VideoOn, not metered → first frame at P
            public const int WarmSwitchAfterIdleMs = 450;     // …the first switch after the runtime idled out
            public const int ColdSwitchMs = 1_000;            // manifest never fetched, runtime alive
            public const int FirstVideoOfProcessMs = 1_500;   // + runtime create + the PMP
            public const int VideoToSongMs = 120;             // the fade ∥ the parked session's resume
            public const int NearSeekMs = 150;                // SeekVerb.Instant, to the landed frame
            public const int FarSeekMs = 500;                 // SeekVerb.Fetch on a ≥ 5 Mbps link
            public const int ScrubStepMs = 100;               // SeekVerb.Coarse, and zero network while the pointer is down
            public const int PausedSeekFirstFrameMs = 120;    // FrameStep + the FirstFrame event
            public const int JoiningNoSpinnerMs = 400;        // §3.4: a Seeking shorter than this shows the previous frame
        }

        // ── 8. the always-on `[video]` lines (§3.5) ─────────────────────────────────────────────────────────────────

        /// <summary>The timeline the gate reads, formatted by a pure function over a struct so the host cannot log one
        /// shape while the gate greps another. Every `Format` writes into the caller's buffer and returns the length
        /// (0 if it would not fit) — no string, no interpolation, no allocation.</summary>
        public static class VideoLog
        {
            /// <summary>256 chars holds the longest line with a 64-char key.</summary>
            public const int MaxLineChars = 256;

            public readonly record struct SwitchBegin(string Key, long FromMs, SwitchAction Plan, bool Warm, uint Epoch);
            public readonly record struct FirstFrame(string Key, uint Epoch, long SinceSwitchMs, long SinceAttachMs, long PosMs, int Width, int Height);
            public readonly record struct AudioCut(int FadeMs, long SongPosMs, long VideoPosMs, long GapMs);
            public readonly record struct SeekPlanned(long TargetMs, SeekIntent Intent, SeekVerb Verb, long KeyframeMs, int SegmentIndex, long DecodeMs);
            public readonly record struct SeekDone(long TargetMs, long LandedMs, long ElapsedMs, bool Fetched);
            public readonly record struct PrefetchPlanned(string Key, PrefetchLevel Level, PrefetchReason Why, bool Metered);

            public static int Format(in SwitchBegin l, Span<char> d)
            {
                var b = new Writer(d);
                b.Text("[video] switch.begin key="); b.Text(l.Key); b.Text(" from="); b.Num(l.FromMs);
                b.Text("ms plan="); b.Text(Name(l.Plan)); b.Text(" warm="); b.Flag(l.Warm); b.Text(" epoch="); b.Num(l.Epoch);
                return b.Done;
            }

            public static int Format(in FirstFrame l, Span<char> d)
            {
                var b = new Writer(d);
                b.Text("[video] first.frame key="); b.Text(l.Key); b.Text(" epoch="); b.Num(l.Epoch);
                b.Text(" sinceSwitchMs="); b.Num(l.SinceSwitchMs); b.Text(" sinceAttachMs="); b.Num(l.SinceAttachMs);
                b.Text(" pos="); b.Num(l.PosMs); b.Text("ms natural="); b.Num(l.Width); b.Text("x"); b.Num(l.Height);
                return b.Done;
            }

            public static int Format(in AudioCut l, Span<char> d)
            {
                var b = new Writer(d);
                b.Text("[video] audio.cut fadeMs="); b.Num(l.FadeMs); b.Text(" songPos="); b.Num(l.SongPosMs);
                b.Text("ms videoPos="); b.Num(l.VideoPosMs); b.Text("ms gapMs="); b.Num(l.GapMs);
                return b.Done;
            }

            public static int Format(in SeekPlanned l, Span<char> d)
            {
                var b = new Writer(d);
                b.Text("[video] seek.plan target="); b.Num(l.TargetMs); b.Text(" intent="); b.Text(Name(l.Intent));
                b.Text(" verb="); b.Text(Name(l.Verb)); b.Text(" kf="); b.Num(l.KeyframeMs); b.Text(" seg="); b.Num(l.SegmentIndex);
                b.Text(" decodeMs="); b.Num(l.DecodeMs);
                return b.Done;
            }

            public static int Format(in SeekDone l, Span<char> d)
            {
                var b = new Writer(d);
                b.Text("[video] seek.done target="); b.Num(l.TargetMs); b.Text(" landed="); b.Num(l.LandedMs);
                b.Text(" ms="); b.Num(l.ElapsedMs); b.Text(" fetched="); b.Num(l.Fetched ? 1 : 0);
                return b.Done;
            }

            public static int Format(in PrefetchPlanned l, Span<char> d)
            {
                var b = new Writer(d);
                b.Text("[video] prefetch.plan track="); b.Text(l.Key); b.Text(" level="); b.Text(Name(l.Level));
                b.Text(" why="); b.Text(Name(l.Why)); b.Text(" metered="); b.Flag(l.Metered);
                return b.Done;
            }

            // String literals, never Enum.ToString: an enum name must not box and must not change when a value is added.
            public static string Name(SwitchAction v) => v switch
            {
                SwitchAction.None => "None", SwitchAction.SeekOnly => "SeekOnly",
                SwitchAction.Switch => "Switch", SwitchAction.Rebuild => "Rebuild", _ => "?",
            };

            public static string Name(SeekIntent v) => v == SeekIntent.Preview ? "Preview" : "Commit";

            public static string Name(SeekVerb v) => v switch
            {
                SeekVerb.Instant => "Instant", SeekVerb.Fetch => "Fetch",
                SeekVerb.Coarse => "Coarse", SeekVerb.Ride => "Ride", _ => "?",
            };

            public static string Name(PrefetchLevel v) => v switch
            {
                PrefetchLevel.None => "None", PrefetchLevel.Manifest => "Manifest",
                PrefetchLevel.ManifestAndLicense => "ManifestAndLicense", PrefetchLevel.Full => "Full", _ => "?",
            };

            public static string Name(PrefetchReason v) => v switch
            {
                PrefetchReason.Badge => "badge", PrefetchReason.Current => "current",
                PrefetchReason.Next => "next", _ => "none",
            };

            public static string Name(SwitchPhase v) => v switch
            {
                SwitchPhase.Idle => "Idle", SwitchPhase.Resolving => "Resolving", SwitchPhase.Licensing => "Licensing",
                SwitchPhase.Buffering => "Buffering", SwitchPhase.Attaching => "Attaching", SwitchPhase.Presenting => "Presenting",
                SwitchPhase.Playing => "Playing", SwitchPhase.Failed => "Failed", _ => "?",
            };

            /// <summary>An overflow-safe appender. One short buffer per call site, reused; a line that does not fit is
            /// reported as length 0 rather than truncated into something the gate would mis-parse.</summary>
            ref struct Writer
            {
                readonly Span<char> _d;
                int _n;
                bool _ok;

                public Writer(Span<char> d) { _d = d; _n = 0; _ok = true; }

                public readonly int Done => _ok ? _n : 0;

                public void Text(ReadOnlySpan<char> s)
                {
                    if (!_ok) return;
                    if (s.Length > _d.Length - _n) { _ok = false; return; }
                    s.CopyTo(_d[_n..]);
                    _n += s.Length;
                }

                public void Num(long v)
                {
                    if (!_ok) return;
                    if (!v.TryFormat(_d[_n..], out int w, default, System.Globalization.CultureInfo.InvariantCulture)) { _ok = false; return; }
                    _n += w;
                }

                public void Flag(bool v) => Text(v ? "true" : "false");
            }
        }
    }
}
