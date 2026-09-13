// ── Shell/Lyrics.cs ────────────────────────────────────────────────────────────────────────────────────────────────
// Fx, BlurPolicy, SyncGate, RowShape, MediaClock, PeekClock, Emphasis, Cascade, Prefs
//
// Role: CORE
// Owner: K
// Wave: 4
// Budget: 3700 lines
// Spec: ch 22 §9
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// EVERY LYRICS DECISION, AND NOTHING THAT PAINTS. Two halves live here:
//
//   A. THE SURFACE'S RULES — the depth-of-field ladders, the blur dial, the sync gate, the row-shape rule, the media
//      clock, the peek clock, the active-line resolve + interlude advance, the emphasis ladder, the karaoke wipe's
//      geometry, the handoff cascade's math, the motion-demand gate, the upgrade authority, and the preferences.
//   B. THE DOCUMENT PIPELINE — the model (`Doc`/`Line`/`Syllable`), the five parsers (LRC incl. enhanced A2, TTML,
//      KRC, YRC, QRC, richsync), the normalizer, the timing plausibility gate, the credit grammar, the cleaner, the
//      query ladder and the reranker. `Lyrics.Host.cs` owns the network, the disk cache and the diagnostics store;
//      NOTHING in this file opens a socket, reads a clock or touches a file.
//
// THREE THINGS THIS FILE EXISTS TO MAKE UNREPRESENTABLE:
//
//   1. TWO COPIES OF `ResolveLine`. The full view and the NPV peek each had one in 0.2.9. There is ONE here (§4), and
//      `PeekClock` calls it.
//   2. A CLOCK THAT IS NOT THE FRAME CLOCK. `MediaClock` is fed (position, the QPC instant that position was true)
//      and queried at the FRAME's present time (`FrameTime.NowQpc` → the engine's `FrameClock.PresentQpc`), never
//      `Environment.TickCount64`, whose 15.6 ms quanta made the karaoke wipe step. The memory rule
//      `animations-sample-frame-time` is this, and the lyrics clock is its canonical case.
//   3. A SURFACE THAT POLLS WHEN NOTHING MOVES. `MotionDemand` (§10) is the 2026-09-12 idle-GPU fix — "lyrics wake
//      only for the words": the per-frame stepper is a frame-clock subscriber, and its subscription IS the request
//      for panel-rate frames, so mounting it on `playing` alone held the UI thread at panel rate for a whole track
//      (measured: 4146 frames in 30 s, 4085 of them record-only, to render 61). Playing is not motion; motion is a
//      lane that is actually moving.
//
// Rules: CORE — pure, engine-free apart from `Signal<T>` in `Prefs` (the two signals are the only non-pure part, ch 22
// §8). The rule half is allocation-free on every per-frame path (P8/P9): the cascade is spans and refs, the wipe and
// the ladders are scalar switches. The PIPELINE half allocates freely and by design — it runs once per track, never
// per frame — which is the one sanctioned exception on this surface.
//
// Ported VERBATIM (ch 22 §8) from `Features/Player/{LyricsView,LyricsBlurPolicy,LyricsMediaClock,LyricsMotionDemand}.cs`,
// `App/{LyricsSyncGate,LyricsRowShape}.cs` and `Backend/Lyrics/{LyricsPeekClock,LyricsText,LyricsTiming,LyricsClean,
// LyricsWordFormats,LyricsCreditRules,LyricsQuery,LyricsReranker,LyricsModel}.cs`. The decisions are not re-derived.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using FluentGpu.Signals;

namespace Wavee;

public static partial class Lyrics
{
    // ── 1. the document model ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How tightly the lyric is timed — drives whether the view can do karaoke (<see cref="Syllable"/>),
    /// line-follow (<see cref="Line"/>), or only static display. Set by the parser / the reranker's repair.</summary>
    public enum SyncKind { None, Unsynced, Line, Syllable }

    /// <summary>One word or character group with its own timing.</summary>
    public sealed record Syllable(long StartMs, long EndMs, string Text);

    /// <summary>One lyric line.
    /// <para><b><see cref="Text"/> is a <c>string</c>, deliberately, not a <c>StringId</c></b> (ch 22 §9 (c)): the
    /// wipe's feather is a FRACTION of <c>GetRangeRects(text, …)</c> measured over that same instance, so an interning
    /// layer between the measured string and the painted one is a silent re-scale of every wipe boundary. `StringId` is
    /// used on this surface only for its STATIC strings (the two messages, the video banner, the resync label, the
    /// pivot links).</para></summary>
    /// <param name="EndMs">The line's end; null ⇒ derived as the next line's start.</param>
    /// <param name="IsWordByWord">Real syllable timing, as opposed to a line-synced row that happens to carry
    /// syllables.</param>
    public sealed record Line(
        long StartMs,
        string Text,
        IReadOnlyList<Syllable> Syllables,
        long? EndMs = null,
        string? Translation = null,
        string? Romanization = null,
        bool IsWordByWord = false);

    /// <summary>One track's lyrics. A managed object graph, so it cannot be an unmanaged column: it lives in the
    /// per-scope side table `Lyrics.Host.cs` owns, keyed by track slot, behind `TrackFields.Lyrics`.</summary>
    /// <param name="Provider">The winning source's id — shown by the inspector, and part of the cache key.</param>
    /// <param name="OffsetMsApplied">What the reranker shifted every timestamp by to line up with the reference.</param>
    public sealed record Doc(
        string TrackId,
        bool IsSynced,
        IReadOnlyList<Line> Lines,
        SyncKind Sync = SyncKind.Line,
        string? Provider = null,
        long OffsetMsApplied = 0)
    {
        /// <summary>A document with no lines — what a miss publishes, and what an instrumental notice collapses to.</summary>
        public static Doc Empty(string trackId, string? provider = null)
            => new(trackId, false, [], SyncKind.Unsynced, provider);

        /// <summary>Computed at COMMIT, never scanned by the UI (CLAUDE.md: derived facts live on the model). Feeds
        /// <see cref="Prefs.Available"/>.</summary>
        public bool HasTranslation
        {
            get { for (int i = 0; i < Lines.Count; i++) if (Lines[i].Translation is { Length: > 0 }) return true; return false; }
        }

        /// <inheritdoc cref="HasTranslation"/>
        public bool HasRomanization
        {
            get { for (int i = 0; i < Lines.Count; i++) if (Lines[i].Romanization is { Length: > 0 }) return true; return false; }
        }

        /// <summary>The two capability bits, as <see cref="Prefs.Available"/> spells them.</summary>
        public int SecondaryAvailable
            => (HasTranslation ? Prefs.HasTranslation : 0) | (HasRomanization ? Prefs.HasRomanization : 0);
    }

    // ── 2. the depth-of-field ladders ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The blur σ a line takes for its RING DISTANCE from the active one. Monotone, 0 on the focus; 0 ⇒ no
    /// blur layer is emitted at all (the recorder drops σ ≤ 0.01), so there is no second "is blur on" branch.
    ///
    /// <para>PER SURFACE, because the two surfaces do different jobs with the same ladder. The RAIL is a narrow panel
    /// beside a page: it shows a handful of lines and the reference treatment is right there. The STAGE is a READING
    /// surface — ~20 lines at 36 DIP — and at the rail's far rungs (4 → 5.5 → 6.5 σ) everything outside the focal band
    /// dissolves into fog, which is what "the lyrics are nearly illegible" was describing. The stage keeps the same
    /// SHAPE — monotone, 0 on the focus, ring 1 barely soft — and flattens the tail so the verse around the line stays
    /// readable as shape rather than resolving to nothing.</para></summary>
    public static class Fx
    {
        public static float DofSigma(int dist, bool large) => large ? StageSigma(dist) : RailSigma(dist);

        /// <summary>The RAIL's ladder: the reference capture, measured (edge energy 27 / 3.7 / ~0 across the first
        /// three rings).</summary>
        public static float RailSigma(int dist) => dist switch
        {
            <= 0 => 0f,
            1 => 1.25f,
            2 => 2.5f,
            3 => 4f,
            4 => 5.5f,
            _ => 6.5f,
        };

        /// <summary>The STAGE's ladder: same shape, flatter tail. Ring 1 is within a rounding step of the rail's (and
        /// of the design reference's 1.2), and the far end settles at 3 instead of 6.5 — recessed, not dissolved.</summary>
        public static float StageSigma(int dist) => dist switch
        {
            <= 0 => 0f,
            1 => 1.2f,
            2 => 2.0f,
            3 => 2.6f,
            _ => 3.0f,
        };
    }

    /// <summary>The blur STRENGTH dial — one 0..100 % number that scales BOTH the depth-of-field ladder
    /// (<see cref="Fx"/>) and the active-line / held-note glow halo σ.
    ///
    /// <para>The setting stores −1 (AUTO) by default: a fresh install lets the device decide rather than shipping one
    /// fixed number that is too heavy for a weak iGPU or too light for a desktop card. <see cref="Resolve"/> is the
    /// ONLY place −1 is interpreted — everywhere else deals in the resolved 0..100 int, so a caller that forgets the
    /// auto case cannot silently treat −1 as "1 % blur".</para></summary>
    public static class BlurPolicy
    {
        public const int Auto = -1;
        public const int WeakGpuDefault = 40;
        public const int StrongGpuDefault = 100;

        /// <summary>The stored setting (−1 = auto, else 0..100) resolved against the device's GPU tier into the
        /// strength this frame actually paints at. Clamps a stored value from an older/newer build's ladder into range
        /// rather than trusting the registry.</summary>
        public static int Resolve(int setting, bool weakGpu)
            => setting == Auto ? (weakGpu ? WeakGpuDefault : StrongGpuDefault) : Math.Clamp(setting, 0, 100);

        /// <summary>The resolved strength (0..100, NOT −1 — call <see cref="Resolve"/> first) as the 0..1 multiplier
        /// the view scales its DoF ladder and halo σ by.</summary>
        public static float Scale(int strength) => Math.Clamp(strength, 0, 100) / 100f;

        /// <summary>Whether the resolved strength paints any blur at all. Strength 0 is a real, deliberate "off" — not
        /// a rounding edge of <see cref="Scale"/> — so the ONE gate on the whole DoF/halo path is this, never a
        /// comparison of <see cref="Scale"/> against 0.</summary>
        public static bool Enabled(int strength) => strength > 0;
    }

    // ── 3. the sync gate and the row-shape rule ─────────────────────────────────────────────────────────────────────

    /// <summary>Whether TIMED lyric sync may run.
    ///
    /// <para>A lyric document's line timings belong to the SONG's audio edit. A music video is a DIFFERENT edit —
    /// spoken intros, alternate arrangements, a longer runtime — so while a video is the current media the position
    /// the app publishes is the VIDEO's clock against the VIDEO's duration, and highlighting "the line at that
    /// position" lands on the wrong line. Sync is suppressed not because it is unavailable but because it would be
    /// actively misleading.</para>
    ///
    /// <para>Suppression is deliberately NARROW: it stops the timed behaviour (the active-line highlight, the
    /// auto-scroll, the lead-time freeze) and nothing else. The lyrics stay on screen and stay freely scrollable. The
    /// surfaces pair this with a one-line note so the missing highlight reads as a deliberate state rather than as
    /// broken sync — and they STOP THE CLOCK rather than branching the render, because a render-time branch over a
    /// still-ticking signal keeps waking the panel to compute a line it must not use.</para></summary>
    public static class SyncGate
    {
        /// <param name="videoActive">Whether a video is the current media — a DERIVED read of the one placement state
        /// (<c>Playback.VideoActive</c>), never a standalone flag.</param>
        public static bool SyncSuppressed(bool videoActive) => videoActive;
    }

    /// <summary>Whether a document swap may KEEP the mounted lyric rows.
    ///
    /// <para>A row freezes its <see cref="Line"/> at mount (props freeze at mount), and the karaoke wipe, the
    /// held-note glow and the secondary line are all built from that frozen line. The aggregator routinely returns a
    /// LINE-synced document first and publishes a WORD-synced upgrade a second later — and because both are scored
    /// against the same reference, the upgrade's per-line TEXT is usually identical. A "same text ⇒ keep the rows"
    /// rule therefore kept rows whose frozen line had NO syllables, so the whole track played with no karaoke wipe at
    /// all, while the next play (disk cache now holding the word-synced document) worked. That is the "syllable lyrics
    /// work sometimes and sometimes not" report.</para>
    ///
    /// <para>So the rows may only survive when every field a row FREEZES is identical: the text, the word-timing shape
    /// (flag, syllable count and every syllable's timing/text) and both secondary layers. Anything else is a genuine
    /// document change and the rows are rebuilt (the caller bumps its row-key epoch), which is the path that was
    /// always correct.</para></summary>
    public static class RowShape
    {
        /// <summary>True when every row of <paramref name="next"/> would render byte-identically to the row already
        /// mounted for <paramref name="current"/>.</summary>
        public static bool SameRows(Doc current, Doc next)
        {
            if (ReferenceEquals(current, next)) return true;
            if (!StringComparer.Ordinal.Equals(current.TrackId, next.TrackId)) return false;
            var a = current.Lines; var b = next.Lines;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!SameRow(a[i], b[i])) return false;
            return true;
        }

        /// <summary>The per-row half of the rule: everything a mounted row reads from its frozen line.</summary>
        public static bool SameRow(Line a, Line b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (!StringComparer.Ordinal.Equals(a.Text, b.Text)) return false;
            if (a.IsWordByWord != b.IsWordByWord) return false;
            if (!StringComparer.Ordinal.Equals(a.Translation ?? "", b.Translation ?? "")) return false;
            if (!StringComparer.Ordinal.Equals(a.Romanization ?? "", b.Romanization ?? "")) return false;
            return SameSyllables(a.Syllables, b.Syllables);
        }

        static bool SameSyllables(IReadOnlyList<Syllable> a, IReadOnlyList<Syllable> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                var x = a[i]; var y = b[i];
                if (x.StartMs != y.StartMs || x.EndMs != y.EndMs || !StringComparer.Ordinal.Equals(x.Text, y.Text)) return false;
            }
            return true;
        }
    }

    // ── 4. the line resolve — ONE copy (plan §5 Wave 4) ─────────────────────────────────────────────────────────────

    /// <summary>The lead the surface applies before resolving: the emphasis and the follow scroll move this far AHEAD
    /// of the strictly-played position so the line is rising as its first syllable lands. It is a TIME-domain
    /// anticipation and is deliberately separate from the wipe's own <see cref="Wipe.LeadFrac"/>.</summary>
    public const long LeadMs = 140;

    /// <summary>An instrumental gap must be at least this long before the surface treats it as a real interlude.</summary>
    public const long InterludeGapMs = 5000L;

    /// <summary>The last line whose <see cref="Line.StartMs"/> is ≤ <paramref name="nowMs"/>, or −1 before the first
    /// line (and on an empty list). THE one copy: the full view, the immersive surface and the NPV peek all call this
    /// — 0.2.9 carried two byte-identical binary searches, which is exactly the drift the plan names.</summary>
    public static int ResolveLine(IReadOnlyList<Line> lines, long nowMs)
    {
        if (lines.Count == 0 || nowMs < lines[0].StartMs) return -1;
        int lo = 0, hi = lines.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (lines[mid].StartMs <= nowMs) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>Is this document timed at all? A line-synced document is a perfectly good one; only Unsynced/None are
    /// static.</summary>
    public static bool IsTimed(Doc doc) => doc.Sync is SyncKind.Line or SyncKind.Syllable;

    /// <summary>The point a line stops being SUNG: the last syllable's end for a word-by-word line, else the authored
    /// end — and, absent that, the next line's start, which by construction leaves no gap. ONE definition, shared by
    /// the frame lane's voice clear and the interlude test below.</summary>
    public static long SungOutMs(Doc doc, int index)
    {
        var l = doc.Lines[index];
        if (l.IsWordByWord && l.Syllables.Count > 0) return l.Syllables[^1].EndMs;
        return l.EndMs ?? (index + 1 < doc.Lines.Count ? doc.Lines[index + 1].StartMs : long.MaxValue);
    }

    /// <summary>The interlude advance: an already-resolved (lead-shifted) <paramref name="active"/> steps ON to the
    /// next line for the whole of a real instrumental break, and <paramref name="gapStart"/>/<paramref name="gapEnd"/>
    /// come back as that break's bounds (equal ⇒ not in one).
    ///
    /// <para>The ADVANCE is word-by-word ONLY, for a sharp reason: a line-synced document has no syllable timing, so
    /// retiring a line early would light the NEXT line fully white (a line-synced row has no wipe with which to look
    /// "unsung") seconds before it is sung. The last line is likewise never advanced past: there is nothing to move
    /// focus to, so it simply stays lit.</para>
    ///
    /// <para>The GAP is reported either way. Those used to be the same decision because a line-synced doc could never
    /// show a gap at all — end derivation sets <c>EndMs</c> to the next line's start, so <c>sungOut == nextStart</c>
    /// by construction. That is no longer true: <see cref="Clean"/> folds a removed ♪/blank row's timestamp into the
    /// line above it, which gives a line-synced document a genuine early sung-out point for the first time. Reporting
    /// the gap lights the dots through the break (the instrumental cue those ♪ rows used to carry) while the focus
    /// stays put, so neither half borrows the other's hazard.</para></summary>
    public static int AdvancePastInterlude(Doc doc, int active, long nowMs, out long gapStart, out long gapEnd)
    {
        gapStart = 0L; gapEnd = 0L;
        if (active < 0 || active + 1 >= doc.Lines.Count) return active;
        var l = doc.Lines[active];
        bool wordSynced = l.IsWordByWord && l.Syllables.Count > 0;
        // Absent syllables, only an AUTHORED end earlier than the next line is a real sung-out point; without one
        // SungOutMs falls back to the next line's start and no gap can exist anyway.
        long nextStart = doc.Lines[active + 1].StartMs;
        if (!wordSynced && !(l.EndMs is { } e && e < nextStart)) return active;
        long sungOut = SungOutMs(doc, active);
        if (nowMs < sungOut || nextStart - sungOut < InterludeGapMs) return active;
        gapStart = sungOut; gapEnd = nextStart;
        return wordSynced ? active + 1 : active;
    }

    // ── 5. the NPV peek's clock ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The NPV lyrics peek: which line is active and which one peeks, from line starts only. It plans NO
    /// fetch of its own — it reads the ONE document the view already has — and it is the whole gate on the peek:
    /// <see cref="ShouldShow"/> false ⇒ the peek renders NOTHING AT ALL, not an empty box.</summary>
    public static class PeekClock
    {
        /// <summary>The same lead the full view applies, so the same line is rising as the first syllable lands.</summary>
        public const long LeadMs = Lyrics.LeadMs;

        public static bool ShouldShow(Doc? doc)
            => doc is { Lines.Count: > 0, Sync: SyncKind.Line or SyncKind.Syllable };

        /// <summary>Active + peek indices for the NPV reel. <paramref name="nowMs"/> is the raw playback clock; the
        /// lead is applied here. Before the first line: active −1, peek 0 (a faded first line). After the last: hold
        /// the last, peek −1. A hidden document returns (−1, −1).</summary>
        public static (int Active, int Peek) ActiveAndPeek(Doc? doc, long nowMs)
        {
            if (!ShouldShow(doc)) return (-1, -1);
            var lines = doc!.Lines;
            int active = ResolveLine(lines, nowMs + LeadMs);
            if (active < 0) return (-1, 0);
            int peek = active + 1 < lines.Count ? active + 1 : -1;
            return (active, peek);
        }
    }

    // ── 6. the media clock ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One reporting interval's counters — the always-on <c>lyrics.clock</c> log line's payload. That line is
    /// written once every 30 s of wall time while a timed document is playing and MUST NEVER become a switch of any
    /// kind: it is the only evidence that the karaoke-stepping bug has not returned.</summary>
    public readonly record struct ClockDiagnostics(long Frames, long ZeroAdvanceFrames, long MaxStepMs, double SlewMs, long Snaps);

    /// <summary>Maps QPC time onto a continuous media-position estimate (ms) — the ONE clock the karaoke wipe, the
    /// follow scroll and the depth-of-field ramp read for "now".
    ///
    /// <para><b>Why it exists.</b> The authoritative signal is itself a coarse ~1 Hz snapshot. Extrapolating it against
    /// <c>Environment.TickCount64</c> was the bug: TickCount64 only advances in ~15.6 ms SYSTEM-TIMER quanta, so at the
    /// 120+ Hz a modern panel produces frames at, its per-frame delta alternates 0 / 15.6 ms. Every snapshot
    /// disagreement was then measured against that coarse "now" and folded in as a STEP (up to 125 ms in one frame),
    /// and a backward step froze the wipe under the monotonic guard — "the karaoke wipe steps even with blur at 0".
    /// This clock is fed a (position, the QPC instant that position was true) pair and queried at the FRAME's present
    /// time, so both sides of every comparison share one clock.</para>
    ///
    /// <para><b>The model.</b> A straight line in (QPC, media-ms) space: <c>anchorMs</c> at <c>anchorQpc</c>, advancing
    /// at <c>rate</c> (nominally 1.0). <see cref="OnSample"/> is the only writer of that line.</para></summary>
    public sealed class MediaClock
    {
        /// <summary>A sample this far (ms) from the mapping's own prediction is not jitter or an ordinary drift
        /// correction — it is a seek, a Connect device transfer, or a track change. Snap instead of slewing.</summary>
        public const long SnapThresholdMs = 250;

        /// <summary>The largest speed-up/slow-down an ordinary correction may apply (5 %) — small enough that closing
        /// even the largest un-snapped disagreement is never perceptible as the lyrics visibly "running fast".</summary>
        public const double MaxSlew = 0.05;

        /// <summary>The time constant an ordinary disagreement closes over, so a fresh 200 ms-apart sample sees ~20 %
        /// of the remaining error gone — a 40 ms bias is under 3 ms a little past 3 s, and keeps shrinking.</summary>
        public const double ConvergeMs = 1000.0;

        readonly double _qpcFrequency;

        bool _hasSample;
        bool _playing;
        double _anchorMs;
        long _anchorQpc;
        double _rate = 1.0;

        // The cheap monotonic guard (see At): the rate is always >= 1-MaxSlew (0.95) > 0 between snaps, so this only
        // ever matters right after one — Snap() resets it so a legitimate backward seek is never clamped away.
        long _lastReturnedMs = long.MinValue;
        long _lastTargetQpc;   // the latest frame target At() was evaluated at since the last (re)anchor — the pivot

        long _frames;
        long _zeroAdvanceFrames;
        long _maxStepMs;
        double _cumulativeSlewMs;
        long _snaps;
        long _lastPlayingMs = long.MinValue;   // previous frame's returned ms WHILE PLAYING; breaks across a pause

        /// <param name="qpcFrequency">QPC ticks per second. Defaults to <see cref="Stopwatch.Frequency"/> (0 or
        /// negative also resolves to it) — a test injects a convenient value (1000, so QPC reads as milliseconds).</param>
        public MediaClock(long qpcFrequency = 0)
            => _qpcFrequency = qpcFrequency > 0 ? qpcFrequency : Stopwatch.Frequency;

        /// <summary>The play state the mapping was last fed — a caller whose play state flips WITHOUT a fresh sample
        /// feeds the edge itself, so a resume does not sit pinned until the next ~200 ms tick.</summary>
        public bool Playing => _playing;

        /// <summary>Feed an authoritative (position, the QPC instant it was true) sample. Returns <c>true</c> when the
        /// position JUMPED (the first sample ever, or a &gt;<see cref="SnapThresholdMs"/> disagreement — a seek, a
        /// transfer, a track change) rather than converging smoothly: callers that care about a discontinuity (the
        /// follow scroll, the handoff cascade) key their "treat this like a fresh landing" recovery off it. A
        /// paused→playing resume always rebases the mapping but reports <c>false</c> unless the position also
        /// jumped.</summary>
        public bool OnSample(long positionMs, long sampleQpc, bool playing)
        {
            bool resuming = playing && !_playing;
            _playing = playing;

            if (!playing)
            {
                // Paused: PIN via rate 0 — the SAME formula in At() then returns anchorMs regardless of how far
                // targetQpc drifts, so a scrub (a fresh sample at a different position, still paused) is reflected
                // immediately and idling paused accumulates no error to correct on resume.
                _anchorMs = positionMs;
                _anchorQpc = sampleQpc;
                _rate = 0.0;
                _lastReturnedMs = long.MinValue;
                _lastTargetQpc = 0;
                bool firstSample = !_hasSample;
                _hasSample = true;
                if (firstSample) _snaps++;
                return firstSample;
            }

            if (!_hasSample)
            {
                Snap(positionMs, sampleQpc);
                return true;
            }

            // While paused the mapping is pinned (rate 0), so on a resume `predicted` is the paused position and the
            // error is exactly "did the position move while paused" (a paused scrub already re-pinned it, so ~0).
            double predicted = AtInternal(sampleQpc);
            double error = positionMs - predicted;
            bool jumped = Math.Abs(error) > SnapThresholdMs;
            if (jumped || resuming)
            {
                // A resume always REBASES (the pinned rate-0 line cannot be slewed back to rate 1), but it only reports
                // a snap when the position genuinely jumped — a plain pause/resume is not a seek, and the caller's seek
                // recovery (instant re-latch, zeroing an in-flight handoff cascade) must not fire for it.
                Snap(positionMs, sampleQpc);
                return jumped;
            }

            // Continuity: the ERROR is measured at the sample's own instant (above), but the line is pivoted where the
            // viewer last SAW it — the latest frame target — not at the sample instant. A sample is always older than
            // the frame on screen (host tick → UI post → a frame produced ~2 vblanks ahead), and re-anchoring at that
            // older instant with a new rate would retroactively shift the value already displayed: a small step at
            // every sample. Pivoting at max(last frame, sample) keeps what was shown exactly where it was and bends
            // only the slope ahead.
            long pivot = Math.Max(_lastTargetQpc, sampleQpc);
            _anchorMs = AtInternal(pivot);
            _anchorQpc = pivot;
            _rate = 1.0 + Math.Clamp(error / ConvergeMs, -MaxSlew, MaxSlew);
            _cumulativeSlewMs += Math.Abs(error);
            return false;
        }

        void Snap(long positionMs, long qpc)
        {
            _anchorMs = positionMs;
            _anchorQpc = qpc;
            _rate = 1.0;
            _hasSample = true;
            _lastReturnedMs = long.MinValue;
            _lastTargetQpc = 0;
            _snaps++;
        }

        /// <summary>Hard-seed the mapping to an exact position, bypassing the snap/slew decision entirely — for sites
        /// that already know they are re-anchoring (a fresh document load, a deliberate lyric click, the advance
        /// probe). Equivalent to feeding a "first sample" regardless of history.
        /// <para>A document CLEAR must call this too (with 0): a port that keeps the clock across a track change
        /// re-treats the next document's first sample as a &gt;250 ms disagreement.</para></summary>
        public void Reset(long positionMs, long qpc, bool playing)
        {
            _hasSample = false;
            OnSample(positionMs, qpc, playing);
        }

        double AtInternal(long targetQpc) => _anchorMs + (targetQpc - _anchorQpc) * 1000.0 / _qpcFrequency * _rate;

        /// <summary>The media position (ms) at <paramref name="targetQpc"/> — call once per frame with the FRAME's
        /// present QPC, never a wall clock. Zero-allocation.</summary>
        public long At(long targetQpc)
        {
            _frames++;
            if (targetQpc > _lastTargetQpc) _lastTargetQpc = targetQpc;
            long ms = (long)AtInternal(targetQpc);

            if (_playing)
            {
                // Never goes backward while playing between snaps — rate >= 0.95 > 0 already guarantees that
                // mathematically; this is the cheap belt-and-braces, engaging only if integer truncation ever lands a
                // hair below the previous frame's rounded value.
                if (_lastReturnedMs != long.MinValue && ms < _lastReturnedMs) ms = _lastReturnedMs;
                _lastReturnedMs = ms;

                if (_lastPlayingMs != long.MinValue)
                {
                    long step = ms - _lastPlayingMs;
                    if (step == 0) _zeroAdvanceFrames++;
                    long absStep = step < 0 ? -step : step;
                    if (absStep > _maxStepMs) _maxStepMs = absStep;
                }
                _lastPlayingMs = ms;
            }
            else
            {
                _lastPlayingMs = long.MinValue;   // break the step-size chain across a pause
            }

            return ms;
        }

        /// <summary>Read the accumulated diagnostics since the last read and zero them — a periodic log line's source,
        /// never a per-frame one.</summary>
        public ClockDiagnostics ReadAndResetDiagnostics()
        {
            var snapshot = new ClockDiagnostics(_frames, _zeroAdvanceFrames, _maxStepMs, _cumulativeSlewMs, _snaps);
            _frames = 0;
            _zeroAdvanceFrames = 0;
            _maxStepMs = 0;
            _cumulativeSlewMs = 0;
            _snaps = 0;
            return snapshot;
        }
    }

    // ── 7. the emphasis ladder ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The packed per-line emphasis word, and the two opacity ladders it selects.
    ///
    /// <para>Bits 0-2 are the ring DISTANCE, clamped at 6. Clamp 6 is exact for the look — the DoF ladder saturates at
    /// ring 5 and the glyph-mount "near" threshold is dist ≤ 2, so any line ≥ 6 away is visually identical; clamping
    /// lets far lines share bucket 6 and skip the re-render as active sweeps.</para>
    ///
    /// <para>Bit 4 (past) is the reference's past/future ASYMMETRY: a line the song has already passed settles dimmer
    /// than an upcoming line the same distance away (measured 118 vs 133 luma), so the two directions ride two
    /// different ladders. It is deliberately NOT set at the saturated bucket 6: both ladders bottom out at the same
    /// 0.10 there, so tagging far lines would be visually identical while breaking the far-line no-op — every line
    /// above the active one would re-render on a seek instead of sitting silent at bucket 6.</para>
    ///
    /// <para>Bit 3 (reserve) is the interlude anchor's extra TOP PAD — the band the breathing dots occupy. It rides
    /// the emphasis signal rather than a signal of its own because that signal is already per-line and value-gated:
    /// arming the reserve re-renders exactly the anchor row, and releasing it exactly the row that had it. It carries
    /// no ladder meaning — <see cref="OpacityOf"/> ignores it — so a line's brightness, scale and σ are identical
    /// whether or not it is holding the band.</para></summary>
    public static class Emphasis
    {
        /// <summary>The saturated ring bucket. A line this far away is visually identical to one further.</summary>
        public const int MaxBucket = 6;
        /// <summary>Bit 3 — this row holds the interlude dots' reserved band.</summary>
        public const int ReserveBit = 8;
        /// <summary>Bit 4 — already sung, so the dimmer of the two ladders.</summary>
        public const int PastBit = 16;

        /// <summary>Pack one line's emphasis word.</summary>
        public static int Pack(int index, int active, int reserveLine)
        {
            int bucket = active < 0 ? MaxBucket : Math.Min(Math.Abs(index - active), MaxBucket);
            int e = bucket;
            if (index == reserveLine) e |= ReserveBit;                           // holds the dots' reserved band
            if (active >= 0 && index < active && bucket < MaxBucket) e |= PastBit;   // already sung ⇒ dimmer ladder
            return e;
        }

        /// <summary>The ring distance a packed word carries.</summary>
        public static int DistOf(int packed) => packed & 7;

        /// <summary>Does this packed word hold the interlude reserve band?</summary>
        public static bool HasReserve(int packed) => (packed & ReserveBit) != 0;

        /// <summary>The row-group opacity for a PACKED emphasis value — the ONE place the ladder lives, so the
        /// direction test can ask it the same question about the previous packed value. A switch, not a table: no
        /// allocation, no indirection.
        ///
        /// <para>Measured off the reference capture: peak luma per ring is 252 (active) / 133 / 113 / 101 / 89 / 72,
        /// and a line the song has ALREADY passed settles DIMMER than an upcoming line the same distance away (118 vs
        /// 133). Past rows sit far lower in ALPHA than that luma gap suggests because their TEXT is the sung white
        /// (full alpha behind a settled wipe) where an upcoming row's is the unsung gray: the same row alpha would
        /// read markedly brighter on a past line.</para></summary>
        public static float OpacityOf(int packed)
        {
            int dist = packed & 7;
            if (dist == 0) return 1f;                         // active line: full focus
            return (packed & PastBit) != 0
                ? dist switch { 1 => 0.19f, 2 => 0.13f, _ => 0.10f }                              // past (sung, white-base)
                : dist switch { 1 => 0.45f, 2 => 0.24f, 3 => 0.14f, 4 => 0.11f, _ => 0.10f };     // future (unsung, gray)
        }

        /// <summary>The emphasis word a row is handed while the per-line array is transiently SHORT — one shared value
        /// pinned at the saturated bucket. It is a RESIZE-GAP GUARD, not a state: a row must never be left holding
        /// it.</summary>
        public const int Fallback = MaxBucket;
    }

    // ── 8. the karaoke wipe ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The wipe's geometry and its held-note bloom. Every constant is measured off the frame-by-frame
    /// reference capture.</summary>
    public static class Wipe
    {
        /// <summary>A small POSITIVE lead: nudge the bright boundary a hair ahead of the strictly-played fraction so
        /// the edge reads as anticipating the voice. 2 %, HALVED from the 0.04 that shipped alongside the old
        /// softness-0.14 wash — a 4-9× too-wide feather HID a 4 % lead inside its own gradient, and against the narrow
        /// band the reference actually shows the same 4 % put the bright edge visibly ahead of the syllable being
        /// sung. The TIME-domain anticipation is a separate knob (<see cref="Lyrics.LeadMs"/>).</summary>
        public const float LeadFrac = 0.02f;

        /// <summary>Unsung glyph alpha. Reference: crossed text holds 250-255 luma while unsung sits at 183-192 over a
        /// ~90 background ⇒ α ≈ 0.59 of the sung white.</summary>
        public const float UnsungAlpha = 0.58f;

        /// <summary>Feather width of the sung/unsung boundary, IN DIP. Reference: a narrow 5-12 source-px band ≈ 2-3 %
        /// of the run width. DIP is the authored unit because the engine's softness is a FRACTION of the run's
        /// READING-ORDER length, so the same on-screen band is a different fraction on every line;
        /// <see cref="SoftnessOfLine"/> does the per-line division.</summary>
        public const float SoftnessDip = 5f;
        /// <inheritdoc cref="SoftnessDip"/>
        public const float SoftnessDipLarge = 7f;
        public static float SoftnessDipFor(bool large) => large ? SoftnessDipLarge : SoftnessDip;

        /// <summary>Clamps on the converted fraction: below 0.01 the band collapses to a hard per-pixel cut on a long
        /// line; above 0.10 a very short line is back in the refuted wash.</summary>
        public const float SoftnessMin = 0.01f;
        /// <inheritdoc cref="SoftnessMin"/>
        public const float SoftnessMax = 0.10f;

        /// <summary>DIP feather → the per-line reading-order fraction, clamped. <paramref name="runLengthDip"/> is the
        /// MEASURED reading-order length of that line's text — the measurement itself stays in the UI (it needs the
        /// text seam and a `TextStyle` that mirrors the row's EXACTLY; the two are changed together or the feather
        /// re-scales).</summary>
        public static float SoftnessOfLine(float runLengthDip, bool large)
            => Math.Clamp(SoftnessDipFor(large) / runLengthDip, SoftnessMin, SoftnessMax);

        /// <summary>Per-word vertical rise, in DIP. Reference: unsung words sit ~2 source px low at cap-height 30 px.
        /// Reduced motion zeroes it — as a VALUE at each consumption point, never an early return.</summary>
        public const float LiftDip = 1.25f;
        /// <inheritdoc cref="LiftDip"/>
        public const float LiftDipLarge = 1.75f;
        /// <inheritdoc cref="LiftDip"/>
        public static float LiftFor(bool large, bool reducedMotion)
            => reducedMotion ? 0f : large ? LiftDipLarge : LiftDip;

        /// <summary>The CHAR-WEIGHTED sung fraction of a line at <paramref name="now"/>: each syllable contributes its
        /// text length, the one being sung contributes its own linear share, and the scan stops there. Returns an
        /// EXACT 1 once the last syllable has ended, and 0 for a line with no syllables.</summary>
        public static float ComputeSplit(Line line, long now)
        {
            var syl = line.Syllables;
            int total = 0;
            for (int i = 0; i < syl.Count; i++) total += Math.Max(1, syl[i].Text.Length);
            if (total == 0) return 0f;
            float played = 0f;
            for (int i = 0; i < syl.Count; i++)
            {
                int len = Math.Max(1, syl[i].Text.Length);
                long s = syl[i].StartMs, e = syl[i].EndMs;
                if (now >= e) { played += len; continue; }
                if (now >= s) played += len * Math.Clamp((float)(now - s) / Math.Max(1L, e - s), 0f, 1f);
                break;
            }
            return Math.Clamp(played / total, 0f, 1f);
        }

        /// <summary>A syllable shorter than this never glows: the bloom is for HELD notes.</summary>
        public const float HeldGlowMinMs = 700f;
        /// <summary>The longest swell-in; a shorter note compresses to half its own duration.</summary>
        public const float HeldGlowRampMaxMs = 500f;
        /// <summary>The melt-out window, measured back from the note's end.</summary>
        public const float GlowOutMs = 240f;
        /// <summary>The strict-parity amplitude trim, applied ONCE to the finished monotone envelope.</summary>
        public const float HeldGlowPeakScale = 0.75f;

        /// <summary>The held-note bloom: 0 unless <paramref name="nowMs"/> sits inside a syllable of at least
        /// <see cref="HeldGlowMinMs"/>; inside one, swell in over min(<see cref="HeldGlowRampMaxMs"/>, half the note)
        /// and melt over <see cref="GlowOutMs"/> into the note's end — min(up, down) is a monotone triangle, so a
        /// short-held note compresses smoothly instead of snapping.</summary>
        public static float HeldSyllableGlow(Line line, long nowMs)
        {
            var syls = line.Syllables;
            for (int i = 0; i < syls.Count; i++)
            {
                var s = syls[i];
                if (nowMs < s.StartMs) break;        // syllables are time-ordered — nothing later can contain nowMs
                if (nowMs >= s.EndMs) continue;
                long dur = s.EndMs - s.StartMs;
                if (dur < HeldGlowMinMs) return 0f;  // short syllable: never glows
                float rampIn = MathF.Min(HeldGlowRampMaxMs, dur * 0.5f);
                float up = EaseOutSine(Math.Clamp((nowMs - s.StartMs) / rampIn, 0f, 1f));
                float down = EaseOutSine(Math.Clamp((s.EndMs - nowMs) / GlowOutMs, 0f, 1f));
                return MathF.Min(up, down);
            }
            return 0f;
        }

        public static float EaseOutSine(float t) => MathF.Sin(t * MathF.PI * 0.5f);

        /// <summary>THE GLOW RULE, as a value: a halo σ may NEVER nest inside a depth-of-field σ — a nested blur layer
        /// is exactly what the compositor's pin key refuses, so the voice row went pin-INELIGIBLE through the whole
        /// lead window and the out-fade after it, i.e. at every line handoff. Fixed by RULE, not by trimming
        /// amplitude: while the row's OWN DoF σ is above the recorder's threshold this halo simply does not paint —
        /// the row is either the active line (DoF settles to 0, so the halo returns exactly there) or a
        /// near-but-not-active row mid-handoff (DoF &gt; 0, so the halo stays off until it either becomes active or
        /// falls out of the near band).</summary>
        public static float GlowSigma(float rowDofSigma, float glowAlpha, bool large, float blurScale)
            => rowDofSigma > 0.01f ? 0f : (large ? 4.5f : 3f) * blurScale * glowAlpha;

        /// <summary>At most TWO glow layers are ever animating: a third in-flight out-fade is finished instantly. A
        /// general fade pool would put a blurred layer on every recently-sung row.</summary>
        public const int MaxGlowFades = 2;

        /// <summary>The write gates. A tick that moves nothing writes nothing, the draw list stays byte-identical, and
        /// the host's skip-submit elides the frame entirely — which is what makes a 30 Hz surface free.</summary>
        public const float SplitEps = 0.0008f, SoftnessEps = 0.002f, DotAlphaEps = 0.004f;

        /// <summary>A split at either endpoint that is ALREADY at that endpoint is frozen: re-writing a line that is
        /// fully sung misses the blur pin on exactly the frames the outgoing line is being blurred away, and keeps the
        /// run in the expensive gradient batch instead of the cheap plain-glyph one.</summary>
        public static bool SplitSettled(float split, float current)
            => (split >= 1f && current >= 1f) || (split <= 0f && current <= 0f);
    }

    // ── 9. the staggered handoff cascade ────────────────────────────────────────────────────────────────────────────

    /// <summary>The per-line compensating translate that turns a viewport LATCH into a staggered settle.
    ///
    /// <para><b>Why not one viewport spring.</b> That is what 0.2.9 replaced: it re-chases from standstill in dense
    /// sections and never reaches the band. Here the viewport jumps INSTANTLY to its new offset and every line carries
    /// a compensating translate that decays to zero — so the document is always where the follow says it is, and the
    /// motion the eye sees is the compensation retiring.</para>
    ///
    /// <para><b>Three properties that are load-bearing.</b> (1) The ADD on <c>comp</c> — never an assign — is what
    /// makes a mid-flight re-target velocity-continuous. (2) The j1 SIGN CLAMP is what guarantees zero overshoot:
    /// <c>x(t) = e^(−y·t)·(c + j1·t)</c> with <c>j1 = v + c·y</c> crosses zero iff j1 has the OPPOSITE sign to c; a
    /// same-direction re-arm provably cannot, a REVERSING one can, so j1 is clamped to 0 there and those lines degrade
    /// to a pure exponential decay — still continuous, still monotone, never an overshoot. (3) The WRITE BAND retires
    /// out-of-band lines rather than skipping their writes, because a line that leaves the band mid-flight would
    /// otherwise keep a stale transform forever.</para>
    ///
    /// <para>Every write is a TRANSFORM change and must be marked transform-dirty ONLY. Adding a paint mark refuses
    /// both of the recorder's span-reuse paths and re-records every in-flight row's glyph runs from scratch for the
    /// whole 0.48 s settle.</para></summary>
    public static class Cascade
    {
        /// <summary>Onset lag per successive line BELOW the outgoing one (measured ~50-70 ms). Rank 0 is the outgoing
        /// line and everything above it — those move IMMEDIATELY.</summary>
        public const float StaggerMs = 60f;
        /// <summary>The rank cap keeps the tail bounded: past ~4 ranks a line is already blurred to nothing, and a
        /// longer tail would still be settling when the NEXT line lands.</summary>
        public const int MaxRank = 4;
        /// <summary>Every rank lands at the SAME wall time (measured 0.42-0.56 s, converging), so a rank that starts
        /// late simply gets a faster rate and catches up exactly at the synchronized settle.</summary>
        public const float TotalS = 0.48f;
        /// <summary>A critically damped (ζ=1) settle is essentially complete once y·t ≈ 7: the closed form's envelope
        /// (1+u)·e^(−u) is 0.7 % of the travel at u = 7 — well inside the landing gate for a line-sized step.</summary>
        public const float SettleY = 7f;
        /// <summary>Land EXACTLY (comp = 0, vel = 0, identity write) inside this residual…</summary>
        public const float LandDip = 0.5f;
        /// <summary>…but only while it is genuinely settling — never mid-flight through zero.</summary>
        public const float LandVel = 20f;
        /// <summary>The transform write gate. The LANDING write is exempt: it must be exact.</summary>
        public const float WriteEps = 0.1f;
        /// <summary>Ticker-gap clamp: a minimized/parked window resumes without a teleport, and a restored window does
        /// not spend a ten-second gap as one integration step.</summary>
        public const float DtMaxMs = 100f;
        /// <summary>±rows around the new active line that take part. 24 single-line rows is ~1130 DIP in the rail and
        /// ~1540 on the stage, and the active line sits at the focal band, so the band covers the whole viewport for
        /// any lyrics surface up to ~1800 DIP tall. Past that the bottom-most rows SNAP with the latch instead of
        /// easing — a bound on the effect, not a correctness cliff.</summary>
        public const int WriteBand = 24;

        /// <summary>What the caller must do with one line after an <see cref="Arm"/> or a <see cref="Step"/>.</summary>
        public const byte WriteNone = 0;
        /// <summary>Write <c>comp[i]</c> as an in-flight translate (subject to <see cref="WriteEps"/>).</summary>
        public const byte WriteMoving = 1;
        /// <summary>Write <c>comp[i]</c> EXACTLY — the landing / retirement write, exempt from the eps gate.</summary>
        public const byte WriteLanded = 2;

        /// <summary>The rate a line whose stagger is <paramref name="delayMs"/> must run at to land with every other
        /// rank at <see cref="TotalS"/>.</summary>
        public static float RateFor(float delayMs) => SettleY / MathF.Max(0.05f, TotalS - delayMs * 0.001f);

        /// <summary>The band this handoff owns.</summary>
        public static void Band(int newActive, int count, out int lo, out int hi)
        {
            lo = Math.Max(0, newActive - WriteBand);
            hi = Math.Min(count - 1, newActive + WriteBand);
        }

        /// <summary>Arm one handoff. <paramref name="delta"/> is the viewport jump that just happened
        /// (newOffset − oldOffset); <paramref name="newActive"/> is the line that just took focus.
        /// <para>Fills <paramref name="write"/> with what the caller must push to the scene: every in-band line is
        /// compensated on the LATCH FRAME ITSELF (not one tick later), and every out-of-band line that still carried
        /// compensation is retired to its true position with one exact identity write.</para>
        /// <para>Reduced motion is read as a VALUE and hoisted: the cascade still RUNS — the lines have to end up
        /// where the latched viewport put them — but with every delay at 0 the whole document performs ONE rigid,
        /// critically-damped, zero-overshoot translate over the same 0.48 s instead of a top-first waterfall.</para>
        /// <returns>Whether anything is now in flight (the caller's "cascade pending" flag).</returns></summary>
        public static bool Arm(Span<float> comp, Span<float> vel, Span<float> delayLeftMs, Span<float> rate,
                               Span<byte> write, float delta, int newActive, bool reducedMotion)
        {
            write.Clear();
            if (comp.Length == 0 || delta == 0f) return false;
            int outgoing = newActive - 1;   // rank 0 — the line that just lost focus, and everything above it
            Band(newActive, comp.Length, out int lo, out int hi);
            for (int i = 0; i < comp.Length; i++)
            {
                if (i < lo || i > hi)
                {
                    // Out of band. Drop any compensation it still carries and put it back on its true scroll position.
                    if (comp[i] != 0f || vel[i] != 0f || delayLeftMs[i] != 0f)
                    {
                        comp[i] = 0f; vel[i] = 0f; delayLeftMs[i] = 0f;
                        write[i] = WriteLanded;
                    }
                    continue;
                }
                float c = comp[i] + delta;   // ADD, never assign: a mid-cascade re-target folds into the in-flight comp
                // Re-arm the stagger for EVERY line on every handoff: a re-target restarts the wave from the NEW active
                // line, so a line that was already flying can be asked to hold again — that is the new wave passing it.
                float delayMs = reducedMotion ? 0f : StaggerMs * Math.Clamp(i - outgoing, 0, MaxRank);
                float y = RateFor(delayMs);
                float v = vel[i];
                if ((v + c * y) * c < 0f) v = -c * y;   // the j1 sign clamp — see the type doc
                comp[i] = c; vel[i] = v; delayLeftMs[i] = delayMs; rate[i] = y;
                write[i] = WriteMoving;
            }
            return true;
        }

        /// <summary>Step every in-flight line by <paramref name="dtMs"/> (already clamped to
        /// <see cref="DtMaxMs"/> by <see cref="ClampDt"/>). A settled line — zero compensation and no delay left —
        /// costs one comparison and no write, which is what lets the out-of-band retirement above make the per-frame
        /// cost a function of the band rather than of the document length.
        /// <returns>Whether anything is still moving.</returns></summary>
        public static bool Step(Span<float> comp, Span<float> vel, Span<float> delayLeftMs, ReadOnlySpan<float> rate,
                                Span<byte> write, float dtMs)
        {
            write.Clear();
            if (comp.Length == 0) return false;
            float dtS = dtMs * 0.001f;
            bool moving = false;
            for (int i = 0; i < comp.Length; i++)
            {
                float c = comp[i], d = delayLeftMs[i];
                if (c == 0f && d <= 0f) continue;   // settled — no state to integrate, no write to make
                if (d > 0f)
                {
                    delayLeftMs[i] = d - dtMs;      // HOLD at the old screen position — this IS the stagger
                    moving = true;
                    continue;                       // already written at arm time; nothing has moved
                }
                // The exact ζ=1 closed-form step (position AND velocity). It is dt-deterministic, so the cascade lands
                // at the same WALL time whatever the frame rate, and a mid-flight re-arm stays velocity-continuous.
                float y = rate[i] > 0f ? rate[i] : SettleY / TotalS;
                float v = vel[i];
                float j1 = v + c * y;
                float e = MathF.Exp(-y * dtS);
                c = e * (c + j1 * dtS);
                v = e * (v - j1 * y * dtS);
                bool landed = MathF.Abs(c) < LandDip && MathF.Abs(v) < LandVel;
                if (landed) { c = 0f; v = 0f; }
                else moving = true;
                comp[i] = c; vel[i] = v;
                write[i] = landed ? WriteLanded : WriteMoving;
            }
            return moving;
        }

        /// <summary>The ticker-gap clamp. The wall stamp is refreshed on EVERY call — even when nothing is pending —
        /// so a long gap can never be spent as one giant integration step.</summary>
        public static float ClampDt(float rawDtMs, float fallbackMs)
            => rawDtMs <= 0f ? fallbackMs : Math.Clamp(rawDtMs, 0f, DtMaxMs);

        /// <summary>Should this line's translate actually reach the scene? The landing write is exempt from the gate
        /// because it must be exact.</summary>
        public static bool ShouldWrite(byte flag, float value, float currentValue)
            => flag == WriteLanded || (flag == WriteMoving && MathF.Abs(value - currentValue) > WriteEps);
    }

    // ── 10. the motion-demand gate (the 2026-09-12 idle-GPU fix) ────────────────────────────────────────────────────

    /// <summary>One frame's worth of lyrics MOTION state, as the surface's own lanes report it at the end of a step.
    /// Pure data — no scene, no signals, no engine types — so the demand decision is unit-testable.</summary>
    /// <param name="Playing">Whether the MEDIA clock is advancing. Every media-clock lane (the wipe, the glow
    /// envelope, the interlude dots' fill/breath) is frozen while paused, so a paused surface demands nothing from
    /// them.</param>
    /// <param name="VoiceActive">A line is being sung right now: its wipe split and glow envelope both move every
    /// frame.</param>
    /// <param name="DotsActive">The interlude dots are up — fill and breath are both media-clock driven.</param>
    /// <param name="GlowFadeActive">An OUTGOING halo cross-fade is still in flight (wall-clock, ~240 ms).</param>
    /// <param name="DofRampPending">The σ ramp has not reached its target (the pass self-quiesces).</param>
    /// <param name="CascadePending">At least one line's compensating translate is still in flight or staggered.</param>
    /// <param name="FollowUnsettled">The follow has not landed.</param>
    /// <param name="Following">The follow mode is Following. Detached/resyncing is a wall-clock countdown plus a
    /// programmatic settle, so it demands frames whatever the transport is doing.</param>
    /// <param name="NowMs">The media clock, in ms.</param>
    /// <param name="NextEventMs">The next media instant at which this surface's own resolution changes.</param>
    public readonly record struct MotionLanes(
        bool Playing,
        bool VoiceActive,
        bool DotsActive,
        bool GlowFadeActive,
        bool DofRampPending,
        bool CascadePending,
        bool FollowUnsettled,
        bool Following,
        long NowMs,
        long NextEventMs);

    /// <summary>Whether the per-frame stepper must be mounted, and — when it must not — the media instant the surface
    /// has to be woken at instead.</summary>
    public readonly record struct MotionDecision(bool NeedsTicks, long WakeAtMs);

    /// <summary>The re-arm the view publishes to its ticker: a one-shot timeout <paramref name="DelayMs"/> from now,
    /// tagged with a <paramref name="Seq"/> so arming the SAME delay twice still restarts the timer. A negative delay
    /// means "nothing to wake for".</summary>
    public readonly record struct MotionWake(int Seq, float DelayMs);

    /// <summary>Does the lyrics surface have MOTION IN FLIGHT — i.e. is there anything for the next step to advance?
    ///
    /// <para><b>Why it exists (the 2026-09-12 fix, "lyrics wake only for the words").</b> The per-frame stepper is a
    /// frame-clock subscriber, and its subscription is also the REQUEST for panel-rate frames. Mounting it on
    /// <c>playing</c> alone therefore held the whole UI thread at panel rate for the entire length of a track —
    /// measured on ARM64 with the panel open through an instrumental intro: 4146 frames in 30 s, 4085 of them
    /// record-only, to render 61. "Playing" is not motion; motion is a lane that is actually moving.</para>
    ///
    /// <para><b>The contract.</b> Ticks are demanded while any lane is live, and the MEDIA-clock lanes only count
    /// while the clock is advancing. When no lane is live the surface goes quiescent and re-arms from a one-shot
    /// timeout at <see cref="ArmLeadMs"/> before the next event on the MEDIA clock. Nothing is lost across the gap:
    /// the wipe split, the glow envelope and the dots' fill are pure FUNCTIONS of the media clock (never accumulated),
    /// so the first frame after a re-mount lands on the same value it would have had. The two INTEGRATORS (the σ ramp
    /// and the handoff cascade) are the exception, which is why they are both in the lane set: neither can go
    /// quiescent mid-flight, and the caller re-seeds their dt stamps on a re-arm rather than differencing across the
    /// gap.</para></summary>
    public static class MotionDemand
    {
        /// <summary>"No future instant can start motion." Also the caller's "not computed" sentinel.</summary>
        public const long None = long.MaxValue;

        /// <summary>How far AHEAD of the next event the surface wakes — and how close an event has to be before the
        /// decision simply stays live rather than arming a timeout for it. Two 60 Hz frames: enough that the stepper is
        /// already mounted and stepping when the syllable lands, small enough that an instrumental gap is still spent
        /// asleep. It also makes the re-arm self-terminating — a timeout that fires a hair early finds the event inside
        /// the lead and goes live instead of arming again.</summary>
        public const long ArmLeadMs = 32L;

        /// <summary>The re-check period for the states that have NO media-clock deadline at all: no document yet, an
        /// untimed one, or sync suppressed by a video. The condition that ends them is not a clock instant, so the
        /// ticker polls it on a slow TIMER (which wakes nothing that produces frames) instead of subscribing the frame
        /// clock.</summary>
        public const float UnresolvedRecheckMs = 250f;

        /// <summary>The lane test on its own: is something moving RIGHT NOW (deadline not consulted)?</summary>
        public static bool LanesMoving(in MotionLanes l)
            => l.CascadePending
            || !l.Following
            || (l.Playing && (l.VoiceActive || l.DotsActive || l.GlowFadeActive || l.DofRampPending || l.FollowUnsettled));

        /// <summary>The whole decision: ticks now, or the instant to be woken at.</summary>
        public static MotionDecision Evaluate(in MotionLanes l)
        {
            if (LanesMoving(in l)) return new MotionDecision(true, None);
            // Paused: the media clock is not advancing, so no future media instant arrives on its own. The surface is
            // woken by the transport instead.
            if (!l.Playing || l.NextEventMs >= None) return new MotionDecision(false, None);
            // Already inside the lead window ⇒ stay live rather than arm a timeout that would fire immediately.
            if (l.NextEventMs - l.NowMs <= ArmLeadMs) return new MotionDecision(true, None);
            return new MotionDecision(false, l.NextEventMs - ArmLeadMs);
        }

        /// <summary>The next media instant at which the surface's own resolution changes, searched from the line the
        /// view has already resolved to. Two candidates per line, checked in time order: the lead-shifted HANDOFF
        /// (<c>start − leadMs</c>), at which the active index moves and the emphasis/latch/cascade fire, and the line's
        /// START, at which it becomes the voice line and its wipe and glow begin. A quiescent surface is by
        /// construction past the current line's sung-out point, so no earlier line can carry an event still in the
        /// future — which is what makes starting the scan at <paramref name="fromLine"/> exact.</summary>
        public static long NextEventMs(IReadOnlyList<Line>? lines, int fromLine, long nowMs, long leadMs)
        {
            if (lines is null) return None;
            for (int i = Math.Max(0, fromLine); i < lines.Count; i++)
            {
                long start = lines[i].StartMs;
                if (start - leadMs > nowMs) return start - leadMs;
                if (start > nowMs) return start;
            }
            return None;
        }
    }

    // ── 11. the upgrade authority ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether a document that arrived LATER is worth taking. The aggregator publishes a fast line-synced
    /// answer first and a richer word-synced one a second later; this is the gate on that swap.
    ///
    /// <para>And the swap must still land on a HANDOFF frame, not mid-line: a richer document that lands mid-line is
    /// HELD and applied on the next active-line change, inside that handoff. Applying it the moment it arrives re-keys
    /// every row under the reader's eyes mid-sentence. Paused, no document, or before the first line ⇒ apply at
    /// once.</para></summary>
    public static class Authority
    {
        /// <summary>Any word-by-word line ⇒ 3, regardless of the declared <see cref="SyncKind"/>; else the kind's own
        /// rank (Syllable 3 / Line 2 / Unsynced 1 / None 0).</summary>
        public static int Richness(Doc doc)
        {
            for (int i = 0; i < doc.Lines.Count; i++)
            {
                var l = doc.Lines[i];
                if (l.IsWordByWord && l.Syllables.Count > 0) return 3;
            }
            return doc.Sync switch
            {
                SyncKind.Syllable => 3,
                SyncKind.Line => 2,
                SyncKind.Unsynced => 1,
                _ => 0,
            };
        }

        public static int SyllableCount(Doc doc)
        {
            int n = 0;
            for (int i = 0; i < doc.Lines.Count; i++) n += doc.Lines[i].Syllables.Count;
            return n;
        }

        /// <summary>A higher rank wins outright; an EQUAL rank is broken by total syllable COUNT; a LOWER rank is
        /// refused.</summary>
        public static bool IsRicher(Doc next, Doc current)
        {
            int nr = Richness(next), cr = Richness(current);
            if (nr != cr) return nr > cr;
            if (nr < 3) return false;
            return SyllableCount(next) > SyllableCount(current);
        }

        /// <summary>May the upgrade be applied RIGHT NOW, or must it be held until the next handoff? Held only while a
        /// timed document is playing and the reader is mid-line.</summary>
        public static bool ApplyImmediately(bool playing, Doc? current, int activeLine)
            => !playing || current is null || activeLine < 0;
    }

    // ── 12. the preferences — an ADAPTER over `Prefs.Lyrics` (owner L), never a second epoch ────────────────────────

    /// <summary>The lyrics preferences as the lyrics surfaces speak them.
    ///
    /// <para><b>No state lives here.</b> The epoch, <see cref="Available"/> and the secondary-line state machine are
    /// owner L's `Platform/Prefs.cs` (`Prefs.Lyrics`, ch 30) — the one cross-surface epoch the Settings picker and both
    /// header toggles bump. This class carried its own `Signal`s until that file landed in the same wave; a second epoch
    /// is a second authority (a Settings write that bumps one and not the other is "the toggle shows the old layer"), so
    /// every member FORWARDS to the same instances. What it adds is only what `Prefs.cs` does not know: the blur dial
    /// RESOLVED against the GPU tier (<see cref="BlurPolicy"/>), which is this file's rule.</para>
    ///
    /// <para><see cref="Available"/> is published once per document (computed at COMMIT on <see cref="Doc"/>, never
    /// scanned by the UI) and read by the rail header and the immersive top bar: the toggle is NOT COMPOSED AT ALL for a
    /// document with neither layer — never composed-and-hidden — and it cycles only through the layers that document
    /// actually has.</para></summary>
    public static class Prefs
    {
        /// <summary>The ONE lyrics epoch — `Prefs.Lyrics.Epoch`, the same instance.</summary>
        public static Signal<int> Epoch => global::Wavee.Prefs.Lyrics.Epoch;
        public static void Bump() => global::Wavee.Prefs.Lyrics.Bump();

        /// <summary>The three states of the secondary-line setting.</summary>
        public const int None = global::Wavee.Prefs.Lyrics.None;
        public const int Translation = global::Wavee.Prefs.Lyrics.Translation;
        public const int Romanization = global::Wavee.Prefs.Lyrics.Romanization;

        /// <summary>…and the two capability bits (deliberately <c>1 &lt;&lt; (mode − 1)</c>).</summary>
        public const int HasTranslation = global::Wavee.Prefs.Lyrics.HasTranslation;
        public const int HasRomanization = global::Wavee.Prefs.Lyrics.HasRomanization;

        /// <summary>Which secondary layers the document on screen carries — `Prefs.Lyrics.Available`, the same instance.
        /// 0 ⇒ no toggle is offered.</summary>
        public static Signal<int> Available => global::Wavee.Prefs.Lyrics.Available;

        /// <summary>Coerce a persisted/hand-edited value into a real mode — a stray 7 shows the original, not nothing.</summary>
        public static int Clamp(int mode) => global::Wavee.Prefs.Lyrics.ClampMode(mode);

        public static int BitFor(int mode) => global::Wavee.Prefs.Lyrics.BitFor(mode);

        /// <summary>none → translation → romanization → none, SKIPPING any layer the document lacks. "None" is always
        /// reachable, so the cycle can never trap the user.</summary>
        public static int Next(int mode, int available) => global::Wavee.Prefs.Lyrics.Next(mode, available);

        /// <summary>The toggle's tooltip — it names the state the view is in NOW, never the next one.</summary>
        public static string Tooltip(int mode) => global::Wavee.Prefs.Lyrics.Tooltip(mode);

        /// <summary>Reactive read of the persisted secondary-line mode, clamped.</summary>
        public static int SecondaryLine() => global::Wavee.Prefs.Lyrics.SecondaryLine();

        /// <summary>Reactive read of the blur strength RESOLVED (0..100) — the stored −1 means AUTO and is interpreted
        /// here, once, through <see cref="BlurPolicy.Resolve"/>.</summary>
        public static int BlurStrength(bool weakGpu) => BlurPolicy.Resolve(global::Wavee.Prefs.Lyrics.BlurStrength(), weakGpu);

        /// <summary>Reactive read of the animated-backdrop switch. Reduced motion holds the backdrop still regardless.</summary>
        public static bool AnimatedBackdrop() => global::Wavee.Prefs.Lyrics.AnimatedBackdrop();

        /// <summary>The ONE writer the Settings picker and both header toggles go through: persist, then bump.</summary>
        public static void SetSecondaryLine(int mode) => global::Wavee.Prefs.Lyrics.SetSecondaryLine(mode);

        /// <summary>Persist a blur strength (−1 = auto, else 0..100) and bump.</summary>
        public static void SetBlurStrength(int strength) => global::Wavee.Prefs.Lyrics.SetBlurStrength(strength);
    }

    // ── 13. the aggregator's working values ─────────────────────────────────────────────────────────────────────────

    /// <summary>Everything a candidate source needs to look a track up: the Spotify identity for the identity-matched
    /// sources and the human metadata for the search-matched ones. <see cref="HasSpotifyLyrics"/> gates ONLY the
    /// Spotify-native source — every other source is still queried when it is false or unknown.</summary>
    public sealed record Request(
        string TrackId,
        string Uri,
        string Title,
        IReadOnlyList<string> Artists,
        string Album,
        long DurationMs,
        string? Isrc = null,
        string Market = "from_token",
        bool? HasSpotifyLyrics = null)
    {
        public string ArtistsJoined => string.Join(", ", Artists);
        public string PrimaryArtist => Artists.Count > 0 ? Artists[0] : "";
    }

    /// <summary>How a candidate was matched to the request — feeds the reranker's confidence (an identity/ISRC match
    /// is trusted more than a fuzzy metadata search).</summary>
    public enum MatchBasis { Identity, Isrc, MetadataSearch, LocalFile, Consensus }

    /// <summary>One source's answer, pre-rank. <see cref="Document"/> is the normalized display lyrics; the reranker
    /// derives comparison text from it on demand. <see cref="Prior"/> is the provider's tiebreak weight — correctness
    /// and timing dominate the score, the prior only breaks ties.</summary>
    public sealed record Candidate(string ProviderId, double Prior, MatchBasis Basis, Doc Document)
    {
        public SyncKind Sync => Document.Sync;
        public int LineCount => Document.Lines.Count;
    }

    /// <summary>Per-candidate decision record — kept so a wrong pick is EXPLAINABLE (which provider, why it won or
    /// lost, what offset was applied). The inspector's Providers tab is a rendering of these.</summary>
    /// <param name="SyncScore">The sync tier AFTER any gate demotion — the one score component that is not simply a
    /// function of the document, and the one a "why did the word-synced candidate lose?" question always hinges
    /// on.</param>
    /// <param name="Verified">Stage one's VERIFIED verdict, exposed so a caller outside the reranker (the background
    /// upgrade pass) can tell "corroborated as the right song" apart from "merely scored highest".</param>
    public sealed record Decision(
        string ProviderId, SyncKind Sync, double Score,
        double TextAgreement, double Coverage, double TimingScore, long AppliedOffsetMs, string Reason,
        double SyncScore = 0d,
        bool Verified = false);

    /// <summary>The reranker's whole answer.</summary>
    public sealed record Ranked(Doc? Winner, Decision? Best, IReadOnlyList<Decision> All);

    // ── 14. the query ladder ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ordered, deduped search-query variants a metadata-matched source tries in turn (most specific
    /// first): full "title artist", then a feat-stripped "title artist", then feat-stripped "title". The single
    /// biggest lever on match rate, since one keyword per source finds far fewer songs. Pure and stateless.
    /// <para><see cref="Normalize"/> here is the SEARCH-text cleanup (full-width → ASCII, curly quotes, " - "
    /// separators) — a DIFFERENT function from the reranker's token-level <see cref="Text.Normalize"/>.</para></summary>
    public static class Query
    {
        // Featured-artist clause in brackets: "(feat. X)", "[ft X]", "(featuring X)". \b after the keyword so
        // "feature" is NOT matched (no boundary before 'u'); the keyword's "featuring" branch covers that word.
        static readonly Regex FeatBracket = new(@"[([{]\s*(?:feat\.?|featuring|ft\.?)\b[^)\]}]*[)\]}]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        // Trailing dash-introduced feat: " - feat. X", " – ft X".
        static readonly Regex FeatDashTail = new(@"\s*[-–—]\s*(?:feat\.?|featuring|ft\.?)\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        static readonly Regex MultiSpace = new(@"\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>Joined "title artist" keyword variants, most specific first, normalized and deduped (a no-feat
        /// title collapses levels 0/1 → 2 variants). Never empty when the title is non-empty.</summary>
        public static IReadOnlyList<string> Variants(Request req)
        {
            string title = req.Title ?? "";
            string artist = req.PrimaryArtist;
            string stripped = StripFeat(title);
            return Dedup([
                Join(title, artist),       // V0: full "title artist"
                Join(stripped, artist),    // V1: feat-stripped "title artist"
                Normalize(stripped),       // V2: feat-stripped title only
            ]);
        }

        /// <summary>The same three levels as (title, artist) PAIRS for split-field sources. Each component normalized;
        /// deduped on the pair.</summary>
        public static IReadOnlyList<(string Title, string Artist)> TitleArtistVariants(Request req)
        {
            string title = Normalize(req.Title ?? "");
            string artist = Normalize(req.PrimaryArtist);
            string stripped = Normalize(StripFeat(req.Title ?? ""));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var outv = new List<(string, string)>(3);
            ReadOnlySpan<(string T, string A)> ladder = [(title, artist), (stripped, artist), (stripped, "")];
            foreach (var (t, a) in ladder)
                if (t.Length > 0 && seen.Add(t + "" + a)) outv.Add((t, a));
            return outv;
        }

        /// <summary>Remove featured-artist clauses (bracketed and trailing " - feat. …"); whitespace-collapsed.</summary>
        public static string StripFeat(string title)
        {
            if (string.IsNullOrEmpty(title)) return title ?? "";
            string s = FeatBracket.Replace(title, "");
            s = FeatDashTail.Replace(s, "");
            return MultiSpace.Replace(s, " ").Trim();
        }

        /// <summary>Search-safe normalization: full-width forms → ASCII, ideographic space → space, curly
        /// quotes/dashes → straight, " - " separator → " ", collapse whitespace, trim. Case is PRESERVED (this is a
        /// query, not a comparison). Traditional→Simplified is deliberately deferred: it needs a large mapping table
        /// and is not the Western/Korean bottleneck.</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                char n = c;
                if (c >= '！' && c <= '～') n = (char)(c - 0xFEE0);   // full-width ASCII block → ASCII
                else if (c == '　') n = ' ';                          // ideographic space
                else switch (c)
                {
                    case '‘' or '’': n = '\''; break;                 // curly single quotes
                    case '“' or '”': n = '"'; break;                  // curly double quotes
                    case '–' or '—': n = '-'; break;                  // en/em dash
                }
                sb.Append(n);
            }
            string r = sb.ToString().Replace(" - ", " ");
            return MultiSpace.Replace(r, " ").Trim();
        }

        static string Join(string title, string artist)
        {
            string t = Normalize(title);
            string a = Normalize(artist);
            return a.Length > 0 ? (t + " " + a).Trim() : t;
        }

        static IReadOnlyList<string> Dedup(ReadOnlySpan<string> items)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var outv = new List<string>();
            foreach (var s in items)
                if (s.Length > 0 && seen.Add(s)) outv.Add(s);
            return outv;
        }
    }

    // ── 15. the format parsers ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Format parsers plus the comparison-text normalizer. Pure (no HTTP, no state), so text →
    /// <see cref="Doc"/> is fully unit-testable. Credit/metadata judgement lives in <see cref="CreditRules"/>.</summary>
    public static partial class Text
    {
        // ── LRC (incl. enhanced/A2 word timing) ─────────────────────────────────────────────────────────────────────
        //
        // `[mm:ss.xx]text` · multiple stamps per line `[t1][t2]text` · metadata `[ti:][ar:][al:][by:][offset:]`
        // · enhanced word timing `[line] <mm:ss.xx>wo<mm:ss.xx>rd …` → per-syllable timing.

        [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
        private static partial Regex LineStampRx();

        [GeneratedRegex(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>", RegexOptions.Compiled)]
        private static partial Regex WordStampRx();

        [GeneratedRegex(@"^\[([a-zA-Z]+):(.*)\]\s*$", RegexOptions.Compiled)]
        private static partial Regex MetaRx();

        [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
        private static partial Regex WsRx();

        [GeneratedRegex(@"[\p{P}\p{S}]", RegexOptions.Compiled)]
        private static partial Regex PunctRx();

        /// <summary>Parse an LRC payload. Word-synced if any line carries <c>&lt;ts&gt;</c> word markers, else
        /// line-synced. <c>[offset:N]</c> is consumed and applied to every timestamp (positive = lyrics earlier).</summary>
        public static Doc ParseLrc(string lrc, string trackId, string? provider = "lrclib")
        {
            long offset = 0;
            var rawLines = new List<Line>();
            bool anyWord = false;

            foreach (var line in SplitLines(lrc))
            {
                // Tier 1 of the credit rules: a [ti:]/[ar:]/[al:]/[by:]/[length:]/[offset:] tag is format metadata and
                // never becomes a line; [offset:] is the one whose value we keep.
                if (CreditRules.IsStructuralMetadata(line))
                {
                    var meta = MetaRx().Match(line);
                    if (meta.Success && meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase)
                        && long.TryParse(meta.Groups[2].Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var off))
                        offset = off;
                    continue;
                }

                var stamps = LineStampRx().Matches(line);
                if (stamps.Count == 0) continue;   // no timing → not a synced line (free leading text / blank)

                // Text is everything after the LAST leading stamp.
                int textStart = stamps[^1].Index + stamps[^1].Length;
                string rest = line[textStart..];

                var (text, syllables) = ParseEnhancedWords(rest);
                if (syllables is { Count: > 0 }) anyWord = true;

                foreach (Match s in stamps)
                {
                    long baseMs = StampMs(s.Groups[1].Value, s.Groups[2].Value, s.Groups[3].Value);
                    // Word stamps are ABSOLUTE in enhanced LRC, so they already include the line base — only the line
                    // start shifts.
                    rawLines.Add(new Line(baseMs, text, (IReadOnlyList<Syllable>?)syllables ?? [],
                        IsWordByWord: syllables is { Count: > 0 }));
                }
            }

            rawLines.Sort(static (a, b) => a.StartMs.CompareTo(b.StartMs));
            if (offset != 0)
                for (int i = 0; i < rawLines.Count; i++) rawLines[i] = ShiftLine(rawLines[i], offset);
            var withEnds = DeriveEnds(rawLines);
            bool synced = withEnds.Count > 0;
            var sync = anyWord ? SyncKind.Syllable : synced ? SyncKind.Line : SyncKind.Unsynced;
            return new Doc(trackId, synced, withEnds, sync, provider);
        }

        // `<00:01.50>Hel<00:01.90>lo` → text "Hello" + two syllables. Plain text (no <ts>) → no syllables.
        static (string Text, List<Syllable>? Syllables) ParseEnhancedWords(string rest)
        {
            var stamps = WordStampRx().Matches(rest);
            if (stamps.Count == 0) return (rest.Trim(), null);

            var syl = new List<Syllable>();
            var sb = new StringBuilder();
            for (int i = 0; i < stamps.Count; i++)
            {
                long start = StampMs(stamps[i].Groups[1].Value, stamps[i].Groups[2].Value, stamps[i].Groups[3].Value);
                int from = stamps[i].Index + stamps[i].Length;
                int to = i + 1 < stamps.Count ? stamps[i + 1].Index : rest.Length;
                string word = rest[from..to];
                long end = i + 1 < stamps.Count
                    ? StampMs(stamps[i + 1].Groups[1].Value, stamps[i + 1].Groups[2].Value, stamps[i + 1].Groups[3].Value)
                    : start + Math.Max(120, word.Trim().Length * 90);
                if (word.Length == 0) continue;
                syl.Add(new Syllable(start, end, word));
                sb.Append(word);
            }
            return (sb.ToString().Trim(), syl);
        }

        static long StampMs(string mm, string ss, string frac)
        {
            long m = long.Parse(mm, CultureInfo.InvariantCulture);
            long s = long.Parse(ss, CultureInfo.InvariantCulture);
            long f = 0;
            if (!string.IsNullOrEmpty(frac))
            {
                // 2-digit = centiseconds, 3-digit = ms (pad/truncate to ms).
                string g = frac.Length switch { 1 => frac + "00", 2 => frac + "0", _ => frac[..3] };
                f = long.Parse(g, CultureInfo.InvariantCulture);
            }
            return (m * 60 + s) * 1000 + f;
        }

        static Line ShiftLine(Line l, long offset)
        {
            static long Shift(long t, long o) => Math.Max(0, t - o);
            IReadOnlyList<Syllable> syl = l.Syllables;
            if (l.Syllables.Count > 0)
            {
                var copy = new List<Syllable>(l.Syllables.Count);
                for (int i = 0; i < l.Syllables.Count; i++)
                {
                    var s = l.Syllables[i];
                    copy.Add(new Syllable(Shift(s.StartMs, offset), Shift(s.EndMs, offset), s.Text));
                }
                syl = copy;
            }
            return l with { StartMs = Shift(l.StartMs, offset), Syllables = syl };
        }

        // ── TTML (the Apple-Music-like word-synced format) ──────────────────────────────────────────────────────────
        //
        // `<p begin="0:01.20" end="0:04.50"> <span begin=… end=…>word</span> … </p>`, namespace-agnostic. A `<p>` with
        // child timed `<span>`s is word-synced; a `<p>` with only text is line-synced. `ttm:role="x-translation"` /
        // `"x-roman"` spans become the line's translation / romanization.

        public static Doc ParseTtml(string ttml, string trackId, string? provider = "amll")
        {
            XDocument doc;
            try { doc = XDocument.Parse(ttml, LoadOptions.PreserveWhitespace); }
            catch { return new Doc(trackId, false, [], SyncKind.Unsynced, provider); }

            var lines = new List<Line>();
            bool anyWord = false;
            foreach (var p in doc.Descendants())
            {
                if (p.Name.LocalName != "p") continue;
                long? begin = ParseTtmlTime(Attr(p, "begin"));
                long? end = ParseTtmlTime(Attr(p, "end"));
                if (begin is null) continue;

                var syl = new List<Syllable>();
                string? translation = null, romanization = null;
                var main = new StringBuilder();

                foreach (var node in p.Nodes())
                    AppendTtmlContent(node, main, syl, ref translation, ref romanization, ref anyWord);

                string text = CollapseWs(main.ToString());
                if (text.Length == 0 && syl.Count == 0) continue;
                lines.Add(new Line(begin.Value, text, syl, end, translation, romanization, IsWordByWord: syl.Count > 0));
            }

            lines.Sort(static (a, b) => a.StartMs.CompareTo(b.StartMs));
            var withEnds = DeriveEnds(lines);
            var sync = anyWord ? SyncKind.Syllable : withEnds.Count > 0 ? SyncKind.Line : SyncKind.Unsynced;
            return new Doc(trackId, withEnds.Count > 0, withEnds, sync, provider);
        }

        static string? Attr(XElement e, string localName)
        {
            foreach (var a in e.Attributes()) if (a.Name.LocalName == localName) return a.Value;
            return null;
        }

        static XElement? FirstWithRuby(XElement span, string kind)
        {
            foreach (var e in span.Descendants())
                if (string.Equals(Attr(e, "ruby"), kind, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        static XElement? LastWithRuby(XElement span, string kind)
        {
            XElement? last = null;
            foreach (var e in span.Descendants())
                if (string.Equals(Attr(e, "ruby"), kind, StringComparison.OrdinalIgnoreCase)) last = e;
            return last;
        }

        static bool HasChildSpan(XElement span)
        {
            foreach (var e in span.Elements()) if (e.Name.LocalName == "span") return true;
            return false;
        }

        static void AppendTtmlContent(
            XNode node,
            StringBuilder main,
            List<Syllable> syl,
            ref string? translation,
            ref string? romanization,
            ref bool anyWord)
        {
            if (node is XText t) { main.Append(t.Value); return; }
            if (node is not XElement span || span.Name.LocalName != "span") return;

            string role = Attr(span, "role") ?? "";   // ttm:role
            if (role.Contains("translation", StringComparison.OrdinalIgnoreCase)) { translation = span.Value.Trim(); return; }
            if (role.Contains("roman", StringComparison.OrdinalIgnoreCase)) { romanization = span.Value.Trim(); return; }
            if (role.Contains("bg", StringComparison.OrdinalIgnoreCase)) return;

            if (string.Equals(Attr(span, "ruby"), "container", StringComparison.OrdinalIgnoreCase))
            {
                var baseSpan = FirstWithRuby(span, "base");
                var firstText = FirstWithRuby(span, "text");
                var lastText = LastWithRuby(span, "text");
                string rubyText = baseSpan?.Value ?? span.Value;
                long? rb = ParseTtmlTime(Attr(firstText ?? span, "begin")) ?? ParseTtmlTime(Attr(span, "begin"));
                long? re = ParseTtmlTime(Attr(lastText ?? span, "end")) ?? ParseTtmlTime(Attr(span, "end"));
                if (rb is not null && rubyText.Length > 0)
                {
                    syl.Add(new Syllable(rb.Value, re ?? rb.Value + 200, rubyText));
                    anyWord = true;
                }
                main.Append(rubyText);
                return;
            }

            long? sb = ParseTtmlTime(Attr(span, "begin"));
            long? se = ParseTtmlTime(Attr(span, "end"));
            if (sb is null && HasChildSpan(span))
            {
                foreach (var child in span.Nodes())
                    AppendTtmlContent(child, main, syl, ref translation, ref romanization, ref anyWord);
                return;
            }

            string spanText = span.Value;
            if (sb is not null && spanText.Length > 0)
            {
                syl.Add(new Syllable(sb.Value, se ?? sb.Value + 200, spanText));
                anyWord = true;
            }
            main.Append(spanText);
        }

        /// <summary>TTML time: <c>hh:mm:ss.mmm</c>, <c>mm:ss.mmm</c>, <c>ss.mmm</c>, or the offset form <c>12.5s</c> /
        /// <c>900ms</c>.</summary>
        public static long? ParseTtmlTime(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim();
            if (v.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) return (long)ms;
            if (v.EndsWith('s')
                && double.TryParse(v[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return (long)(s * 1000);
            var parts = v.Split(':');
            try
            {
                double total = 0;
                foreach (var part in parts) total = total * 60 + double.Parse(part, CultureInfo.InvariantCulture);
                return (long)(total * 1000);
            }
            catch { return null; }
        }

        // ── shared shaping ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Fill each line's end (last syllable end → else the next line's start → else a conservative +4 s)
        /// so the wipe has a bound. Deliberately NOT a judge of what is a lyric: this used to drop credit-looking rows
        /// by word list from ANYWHERE in the document, which is the mid-song false positive <see cref="Clean"/> exists
        /// to make impossible. The parsers hand every timed row over; the cleaner decides, positionally.</summary>
        internal static List<Line> DeriveEnds(List<Line> lines)
        {
            var outLines = new List<Line>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                long sylEnd = l.Syllables.Count > 0 ? l.Syllables[^1].EndMs : 0L;
                long end = l.EndMs ?? (sylEnd > l.StartMs ? sylEnd
                    : i + 1 < lines.Count ? lines[i + 1].StartMs
                    : l.StartMs + 4000);
                outLines.Add(l with { EndMs = end });
            }
            return outLines;
        }

        internal static string[] SplitLines(string s)
            => s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        static string CollapseWs(string s) => WsRx().Replace(s, " ").Trim();

        /// <summary>Comparison-safe text: lowercase, punctuation stripped, whitespace collapsed. The reranker aligns
        /// candidates on THIS (never the display text), so romanization, punctuation and case differences do not look
        /// like mismatches. Distinct from <see cref="Query.Normalize"/>, which is the SEARCH cleanup.</summary>
        public static string Normalize(string? text)
            => CollapseWs(PunctRx().Replace(text ?? "", " ").ToLowerInvariant());
    }

    /// <summary>Parsers for the CJK word-synced lyric body formats (KRC / YRC / QRC) and Musixmatch's richsync, all
    /// produced after decrypt. All carry per-syllable timing. Pure; reranked and cleaned like any other
    /// candidate.</summary>
    public static partial class WordFormats
    {
        // Line header: [lineStartMs,lineDurationMs]
        [GeneratedRegex(@"^\[(\d+),(\d+)\]", RegexOptions.Compiled)]
        private static partial Regex LineHeadRx();

        // KRC word: <offsetMs,durationMs,0>word — offset RELATIVE to the line start.
        [GeneratedRegex(@"<(\d+),(\d+),-?\d+>([^<]*)", RegexOptions.Compiled)]
        private static partial Regex KrcWordRx();

        // NetEase YRC word: (startMs,durationMs,0)word — ABSOLUTE times.
        [GeneratedRegex(@"\((\d+),(\d+),-?\d+\)([^(]*)", RegexOptions.Compiled)]
        private static partial Regex YrcWordRx();

        // QQ QRC word: word(startMs,durationMs) — timing AFTER the word, ABSOLUTE.
        [GeneratedRegex(@"([^()]*?)\((\d+),(\d+)\)", RegexOptions.Compiled)]
        private static partial Regex QrcWordRx();

        /// <summary>Kugou KRC body → word-synced document. Word times are relative to the line start.</summary>
        public static Doc ParseKrc(string krc, string trackId, string provider = "kugou")
            => BuildDoc(krc, trackId, provider, ParseKrcLine);

        static (long Start, string Text, List<Syllable> Syl)? ParseKrcLine(string line)
        {
            var head = LineHeadRx().Match(line);
            if (!head.Success) return null;
            long lineStart = long.Parse(head.Groups[1].Value, CultureInfo.InvariantCulture);
            string body = line[head.Length..];
            var syl = new List<Syllable>();
            foreach (Match m in KrcWordRx().Matches(body))
            {
                long off = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                long dur = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                string w = m.Groups[3].Value;
                if (w.Length == 0) continue;
                syl.Add(new Syllable(lineStart + off, lineStart + off + dur, w));
            }
            if (syl.Count == 0) return null;
            return (lineStart, Concat(syl), syl);
        }

        /// <summary>NetEase YRC body → word-synced document. Word times are ABSOLUTE. (The credit rows are JSON objects
        /// between the timed rows — <see cref="CreditRules.IsStructuralMetadata"/> — and <see cref="BuildDoc"/> never
        /// turns them into lines.)</summary>
        public static Doc ParseYrc(string yrc, string trackId, string provider = "netease")
            => BuildDoc(yrc, trackId, provider, ParseYrcLine);

        static (long Start, string Text, List<Syllable> Syl)? ParseYrcLine(string line)
        {
            var head = LineHeadRx().Match(line);
            if (!head.Success) return null;
            long lineStart = long.Parse(head.Groups[1].Value, CultureInfo.InvariantCulture);
            var syl = new List<Syllable>();
            foreach (Match m in YrcWordRx().Matches(line[head.Length..]))
            {
                long s = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                long d = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                string w = m.Groups[3].Value;
                if (w.Length == 0) continue;
                syl.Add(new Syllable(s, s + d, w));
            }
            if (syl.Count == 0) return null;
            return (lineStart, Concat(syl), syl);
        }

        /// <summary>QQ QRC body → word-synced document. The decrypted QRC is usually XML-wrapped; the attribute is
        /// extracted first, then each line is parsed with ABSOLUTE word times.</summary>
        public static Doc ParseQrc(string qrc, string trackId, string provider = "qq")
            => BuildDoc(ExtractQrcContent(qrc), trackId, provider, ParseQrcLine);

        static string ExtractQrcContent(string qrc)
        {
            int i = qrc.IndexOf("LyricContent=\"", StringComparison.Ordinal);
            if (i < 0) return qrc;
            i += "LyricContent=\"".Length;
            int j = qrc.IndexOf('"', i);
            return j > i ? System.Net.WebUtility.HtmlDecode(qrc[i..j]) : qrc;
        }

        static (long Start, string Text, List<Syllable> Syl)? ParseQrcLine(string line)
        {
            var head = LineHeadRx().Match(line);
            if (!head.Success) return null;
            long lineStart = long.Parse(head.Groups[1].Value, CultureInfo.InvariantCulture);
            var syl = new List<Syllable>();
            foreach (Match m in QrcWordRx().Matches(line[head.Length..]))
            {
                string w = m.Groups[1].Value;
                long s = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                long d = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (w.Length == 0) continue;
                syl.Add(new Syllable(s, s + d, w));
            }
            if (syl.Count == 0) return null;
            return (lineStart, Concat(syl), syl);
        }

        /// <summary>Musixmatch richsync body (a JSON array <c>[{ts,te,l:[{c,o}],x}]</c>): line start/end and the
        /// per-CHARACTER offsets are in SECONDS (the offset is from the line start). Produces character-level
        /// syllables, with whitespace-only characters folded onto the chunk before them.</summary>
        public static Doc ParseRichsync(string richsyncJson, string trackId, string provider = "musixmatch")
        {
            var lines = new List<Line>();
            try
            {
                using var d = JsonDocument.Parse(richsyncJson);
                if (d.RootElement.ValueKind != JsonValueKind.Array) return Doc.Empty(trackId, provider);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    double ts = e.TryGetProperty("ts", out var tsv) ? tsv.GetDouble() : 0;
                    double te = e.TryGetProperty("te", out var tev) ? tev.GetDouble() : ts;
                    string text = e.TryGetProperty("x", out var xv) && xv.ValueKind == JsonValueKind.String ? xv.GetString()! : "";
                    var syl = new List<Syllable>();
                    if (e.TryGetProperty("l", out var l) && l.ValueKind == JsonValueKind.Array)
                    {
                        var arr = new List<(string Text, double Offset)>();
                        foreach (var x in l.EnumerateArray())
                            arr.Add((
                                x.TryGetProperty("c", out var cv) && cv.ValueKind == JsonValueKind.String ? cv.GetString()! : "",
                                x.TryGetProperty("o", out var ov) ? ov.GetDouble() : 0d));
                        for (int i = 0; i < arr.Count; i++)
                        {
                            var (c, o) = arr[i];
                            if (c.Length == 0 || string.IsNullOrWhiteSpace(c)) continue;

                            string chunk = c;
                            int next = i + 1;
                            while (next < arr.Count && string.IsNullOrWhiteSpace(arr[next].Text))
                            {
                                chunk += arr[next].Text;
                                next++;
                            }

                            double oNext = next < arr.Count ? arr[next].Offset : te - ts;
                            long startMs = (long)((ts + o) * 1000);
                            long endMs = Math.Max(startMs + 1, (long)((ts + Math.Max(o, oNext)) * 1000));
                            syl.Add(new Syllable(startMs, endMs, chunk));
                        }
                    }
                    lines.Add(new Line((long)(ts * 1000), text.Trim(), syl, (long)(te * 1000), IsWordByWord: syl.Count > 0));
                }
            }
            catch { return Doc.Empty(trackId, provider); }
            lines.Sort(static (a, b) => a.StartMs.CompareTo(b.StartMs));
            bool synced = lines.Count > 0;
            bool word = false;
            for (int i = 0; i < lines.Count; i++) if (lines[i].IsWordByWord) { word = true; break; }
            return new Doc(trackId, synced, lines,
                word ? SyncKind.Syllable : synced ? SyncKind.Line : SyncKind.Unsynced, provider);
        }

        static string Concat(List<Syllable> syl)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < syl.Count; i++) sb.Append(syl[i].Text);
            return sb.ToString().Trim();
        }

        internal static Doc BuildDoc(string text, string trackId, string provider,
            Func<string, (long Start, string Text, List<Syllable> Syl)?> parseLine)
        {
            var lines = new List<Line>();
            foreach (var raw in Text.SplitLines(text))
            {
                var line = raw.Trim();
                // Tier 1 of the credit rules: what the format itself marks as metadata ([ti:]/[language:] tags, YRC's
                // JSON credit objects, the QRC XML wrapper) never becomes a line.
                if (line.Length == 0 || CreditRules.IsStructuralMetadata(line) || line[0] != '[') continue;
                var parsed = parseLine(line);
                if (parsed is not { } p) continue;
                long end = p.Syl.Count > 0 ? p.Syl[^1].EndMs : p.Start;
                lines.Add(new Line(p.Start, p.Text, p.Syl, end, IsWordByWord: p.Syl.Count > 0));
            }
            lines.Sort(static (a, b) => a.StartMs.CompareTo(b.StartMs));
            bool synced = lines.Count > 0;
            return new Doc(trackId, synced, lines, synced ? SyncKind.Syllable : SyncKind.Unsynced, provider);
        }
    }

    // ── 16. the timing plausibility gate ────────────────────────────────────────────────────────────────────────────

    /// <summary>The ONE owner of "are these word timings physically possible?".
    ///
    /// <para>A word-synced body can be internally consistent — monotonic, every syllable inside its line, no negative
    /// durations — and still be garbage: the words all fire in the first fraction of a second and the line then sits
    /// dead until the next one. Nothing else in the pipeline notices. The reranker only aligns LINE STARTS against the
    /// reference, so such a candidate scores as a perfect match and wins the sync tier on word timing it does not
    /// deliver; the view then wipes the whole line in 200 ms and shows interlude dots for eight seconds.</para>
    ///
    /// <para>The test is deliberately PHYSICAL rather than comparative: WORDS PER SECOND. Ordinary singing is 1-3 wps
    /// and the fastest recorded rap is around 9, so a line over <see cref="ImpossibleWordsPerSecond"/> did not get its
    /// real duration — no reference document, no per-provider knowledge and no threshold tuning required. A "the line
    /// ends early relative to the gap" test was considered and rejected: an instrumental break makes every legitimate
    /// line look squashed.</para></summary>
    public static class Timing
    {
        /// <summary>Above this, a sung line is not physically possible.</summary>
        public const double ImpossibleWordsPerSecond = 8.0;

        /// <summary>Short lines ("Ooh, ah") are excluded — a two-word line legitimately lands inside a few hundred
        /// milliseconds, so its rate says nothing.</summary>
        const int MinWordsToJudge = 3;

        /// <summary>A document needs this many judgeable lines before the verdict means anything — a three-line
        /// fragment must not be able to condemn a provider.</summary>
        const int MinJudgeableLines = 5;

        public static int WordCount(string text)
        {
            int n = 0;
            bool inWord = false;
            foreach (char c in text)
            {
                bool space = char.IsWhiteSpace(c);
                if (!space && !inWord) n++;
                inWord = !space;
            }
            return n;
        }

        /// <summary>How long the WORD timing claims the line takes. Measured across the syllables when there are any
        /// (that is the timing under test), falling back to the line's own end.</summary>
        public static long WordSpanMs(Line line)
        {
            if (line.Syllables.Count > 0)
            {
                long first = line.Syllables[0].StartMs, last = line.Syllables[^1].EndMs;
                if (last > first) return last - first;
            }
            return line.EndMs is { } e && e > line.StartMs ? e - line.StartMs : 0L;
        }

        public static bool IsImpossiblyFast(Line line)
        {
            long span = WordSpanMs(line);
            if (span <= 0) return false;
            int words = WordCount(line.Text);
            return words >= MinWordsToJudge && words * 1000d / span > ImpossibleWordsPerSecond;
        }

        /// <summary>True when a word-synced document's timings are not singable: at least HALF of its judgeable lines
        /// are impossibly fast. Half (not all) because a handful of legitimately clipped lines is normal, while a body
        /// whose offsets are systematically compressed fails on nearly every line.</summary>
        public static bool HasImplausibleWordTiming(Doc doc, out int impossible, out int judged)
        {
            impossible = 0;
            judged = 0;
            if (doc.Sync != SyncKind.Syllable) return false;

            for (int i = 0; i < doc.Lines.Count; i++)
            {
                var l = doc.Lines[i];
                if (WordSpanMs(l) <= 0 || WordCount(l.Text) < MinWordsToJudge) continue;
                judged++;
                if (IsImpossiblyFast(l)) impossible++;
            }
            return judged >= MinJudgeableLines && impossible * 2 >= judged;
        }

        const long SquashDenominator = 4;
        const long SquashMinGapMs = 1500;

        /// <summary>A line is "squashed" when it ENDS inside a quarter of the time until the next line begins.
        /// Reported, but deliberately NOT part of <see cref="HasImplausibleWordTiming"/>: an instrumental break makes
        /// every legitimate line look squashed, so it is a symptom worth showing a human, not a rule worth gating
        /// on.</summary>
        public static bool IsSquashed(Doc doc, int i)
        {
            if (i + 1 >= doc.Lines.Count) return false;
            var l = doc.Lines[i];
            long dur = (l.EndMs ?? l.StartMs) - l.StartMs;
            if (dur <= 0) return false;
            long gap = doc.Lines[i + 1].StartMs - l.StartMs;
            return gap >= SquashMinGapMs && dur * SquashDenominator < gap;
        }

        /// <summary>A document that runs well past the track it is supposed to caption did not get real timing for
        /// this recording — the tell behind an anti-scraping decoy (206 lines running to 13:56 on a 3:30 track).
        /// Generous on purpose (15 % + 5 s) so a trailing outro line on a correctly-timed lyric never trips it. An
        /// unknown track length always returns false.</summary>
        public static bool ExceedsTrackDuration(Doc doc, long durationMs)
        {
            if (durationMs <= 0 || doc.Lines.Count == 0) return false;
            var last = doc.Lines[^1];
            long end = last.EndMs ?? last.StartMs;
            return end > (long)(durationMs * 1.15) + 5000;
        }

        /// <summary>True when a document's lines march forward at an EXACTLY uniform step (and, where ends exist, an
        /// exactly uniform duration) — the other half of the same decoy tell: 206 lines every 4000 ms apart is not a
        /// human transcription, it is a generator. Needs at least 20 lines so a short, legitimately regular verse
        /// cannot trip it; "identical within 1 ms" tolerates integer rounding without accepting real variation.</summary>
        public static bool HasUniformLineDurations(Doc doc)
        {
            if (doc.Lines.Count < 20) return false;
            long? step = null, lineDur = null;
            for (int i = 0; i < doc.Lines.Count; i++)
            {
                var l = doc.Lines[i];
                if (i > 0)
                {
                    long d = l.StartMs - doc.Lines[i - 1].StartMs;
                    if (step is null) step = d;
                    else if (Math.Abs(d - step.Value) > 1) return false;
                }
                if (l.EndMs is { } e)
                {
                    long dur = e - l.StartMs;
                    if (lineDur is null) lineDur = dur;
                    else if (Math.Abs(dur - lineDur.Value) > 1) return false;
                }
            }
            return true;
        }

        static long UniformStepMs(Doc doc)
            => doc.Lines.Count >= 2 ? doc.Lines[1].StartMs - doc.Lines[0].StartMs : 0L;

        static string Fmt(long ms)
        {
            if (ms < 0) ms = 0;
            long m = ms / 60000, s = (ms % 60000) / 1000;
            return m.ToString(CultureInfo.InvariantCulture) + ":" + s.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>ONE human-readable verdict on a document's timings — the string the inspector shows and every saved
        /// bundle records. Two families: STRUCTURAL (timestamps that go backwards, never got set, or end before they
        /// start; syllables outside their own line) and PLAUSIBILITY (timings that are internally consistent but far
        /// too compressed to be sung). The second family is what catches a scale mistake in a body parser.
        /// <paramref name="durationMs"/> unlocks the two decoy tells, which the per-line loop cannot see.</summary>
        public static string Describe(Doc doc, long durationMs = 0)
        {
            if (doc.Sync == SyncKind.Unsynced)
                return "unsynced document — it carries no timings at all, so the UI cannot follow it";

            int outOfOrder = 0, noStamp = 0, badEnd = 0, syllOutside = 0, syllOutOfOrder = 0, squashed = 0, tooFast = 0;
            long prev = long.MinValue;

            for (int i = 0; i < doc.Lines.Count; i++)
            {
                var l = doc.Lines[i];
                if (prev != long.MinValue && l.StartMs < prev) outOfOrder++;
                prev = l.StartMs;
                if (i > 0 && l.StartMs == 0) noStamp++;
                if (l.EndMs is { } e && e < l.StartMs) badEnd++;

                long ps = long.MinValue;
                foreach (var s in l.Syllables)
                {
                    if (ps != long.MinValue && s.StartMs < ps) syllOutOfOrder++;
                    ps = s.StartMs;
                    if (s.EndMs < s.StartMs || s.StartMs < l.StartMs || (l.EndMs is { } le && s.EndMs > le)) syllOutside++;
                }

                if (IsSquashed(doc, i)) squashed++;
                if (IsImpossiblyFast(l)) tooFast++;
            }

            var parts = new List<string>(8);
            if (outOfOrder > 0) parts.Add($"{outOfOrder} line(s) start BEFORE the line above them");
            if (noStamp > 0) parts.Add($"{noStamp} line(s) after the first have a 0ms timestamp");
            if (badEnd > 0) parts.Add($"{badEnd} line(s) end before they start");
            if (squashed > 0)
                parts.Add($"{squashed} line(s) END inside a quarter of their slot — the words burst at the line start and "
                    + "the line then sits dead until the next one (compressed line-end / syllable offsets)");
            if (tooFast > 0)
                parts.Add($"{tooFast} line(s) would have to be sung faster than {ImpossibleWordsPerSecond:0} words/second");
            if (syllOutOfOrder > 0) parts.Add($"{syllOutOfOrder} syllable(s) run backwards");
            if (syllOutside > 0) parts.Add($"{syllOutside} syllable(s) fall outside their own line");
            if (HasImplausibleWordTiming(doc, out _, out _))
                parts.Add("VERDICT: the word-timing gate rejects this — the reranker demotes it to the line tier and the "
                    + "winner's syllables are stripped, so the view falls back to line-level highlighting on the (correct) starts");
            if (ExceedsTrackDuration(doc, durationMs))
            {
                var last = doc.Lines[^1];
                long end = last.EndMs ?? last.StartMs;
                parts.Add($"runs to {Fmt(end)} on a {Fmt(durationMs)} track");
            }
            if (HasUniformLineDurations(doc))
                parts.Add($"{doc.Lines.Count} lines all exactly {UniformStepMs(doc)} ms — uniform decoy timing");

            return parts.Count == 0
                ? "clean — monotonic lines, plausible durations, every timestamp inside its line"
                : string.Join(" · ", parts);
        }

        /// <summary>Keep what the payload got RIGHT and drop what it got wrong. The line STARTS of such a document are
        /// typically perfect (they align 1:1 with the reference); it is only the word offsets and line ends that are
        /// compressed. Stripping the syllables and clearing the ends leaves a document shaped exactly like a natively
        /// line-synced one — which the view already renders correctly — instead of a karaoke wipe over invented
        /// timings.</summary>
        public static Doc StripWordTiming(Doc doc)
        {
            var lines = new List<Line>(doc.Lines.Count);
            for (int i = 0; i < doc.Lines.Count; i++)
                lines.Add(doc.Lines[i] with { Syllables = [], IsWordByWord = false, EndMs = null });
            return doc with { Lines = lines, Sync = SyncKind.Line };
        }
    }

    // ── 17. the credit grammar ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The decisions behind "is this row a credit or provider padding rather than a sung line?", in the order
    /// they are TRUSTED. A hard-coded word list is deliberately the LAST resort, not the rule:
    /// <list type="number">
    /// <item><b>Structural</b> — <see cref="IsStructuralMetadata"/>. The provider's own format marks the row as
    /// metadata. Language-free and never wrong, so the parsers apply it and nothing that matches ever becomes a
    /// <see cref="Line"/>.</item>
    /// <item><b>Reference alignment</b> — <see cref="TrimUnalignedEdges"/>. With the reference in hand, a candidate's
    /// leading/trailing lines that align to NOTHING in it and sit outside the span the reference sings are padding,
    /// whatever language they are in. Needs the reference, so it runs at the RANK site, and only ever touches the two
    /// edges.</item>
    /// <item><b>Grammar</b> — <see cref="LooksLikeCreditLine"/>. The SHAPE <c>key: value</c>: a short key, then a
    /// value that reads as a list of names rather than a clause. The known credit keys are a confidence BOOST on top:
    /// a known key is a credit whatever follows it, and with a full-width colon even mid-document.</item>
    /// </list></summary>
    public static class CreditRules
    {
        // ── 1. structural ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>True for a RAW payload row the format itself marks as metadata, never a lyric: an LRC-family
        /// <c>[key:value]</c> tag with an ALPHABETIC key (a timed row's key is digits), a JSON credit object row, or an
        /// XML wrapper row.</summary>
        public static bool IsStructuralMetadata(string? rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) return false;
            string s = rawLine.Trim();
            if (s.Length < 3) return false;
            if (s[0] == '[' && s[^1] == ']')
            {
                int colon = s.IndexOf(':');
                // …and nothing after the closing bracket: "[ti:X][00:01.00]sung" is a timed row with a tag glued on.
                return colon > 1 && IsAsciiAlpha(s.AsSpan(1, colon - 1)) && s.IndexOf(']') == s.Length - 1;
            }
            // A YRC body interleaves {"t":0,"c":[{"tx":"作词: "}]} credit objects with its timed rows.
            if (s[0] == '{' && s[^1] == '}') return true;
            // A decrypted QRC is XML-wrapped; the wrapper rows are markup.
            if (s[0] == '<' && s[^1] == '>') return true;
            return false;
        }

        static bool IsAsciiAlpha(ReadOnlySpan<char> s)
        {
            foreach (char c in s) if (!char.IsAsciiLetter(c)) return false;
            return s.Length > 0;
        }

        // ── 2. reference alignment ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>A reference shorter than this is too thin to say what a candidate's edges "should" align to.</summary>
        public const int MinReferenceLines = 8;
        /// <summary>An unaligned edge run longer than this is a different cut of the song (or a different song), not a
        /// credit block; leave it for the reranker to judge.</summary>
        public const int MaxEdgeRun = 6;
        /// <summary>Between the edges, at least this share of lines must align — otherwise the candidate is not
        /// demonstrably the same text and "aligns to nothing" means nothing.</summary>
        public const double MinInteriorAgreement = 0.5;
        /// <summary>Timing corroboration: an edge line only counts as padding when it also sits OUTSIDE the span the
        /// reference sings by more than this. A differently-worded first or last lyric sits AT the reference's edge and
        /// must survive.</summary>
        public const long EdgeSlackMs = 500;

        /// <summary>Drop the leading/trailing lines of <paramref name="cand"/> that align to no line of
        /// <paramref name="reference"/> and lie outside the reference's sung span. Returns the same instance when
        /// nothing qualifies. Kept lines keep their timestamps; a dropped line's syllables go with it.</summary>
        public static Doc TrimUnalignedEdges(Doc cand, Doc? reference, out int leading, out int trailing)
        {
            leading = trailing = 0;
            if (reference is null || reference.Lines.Count < MinReferenceLines || cand.Lines.Count < 2) return cand;

            int n = cand.Lines.Count;
            var candTokens = new List<string[]>(n);
            for (int i = 0; i < n; i++) candTokens.Add(Reranker.Tokens(cand.Lines[i].Text));
            var refTokens = new List<string[]>(reference.Lines.Count);
            for (int i = 0; i < reference.Lines.Count; i++) refTokens.Add(Reranker.Tokens(reference.Lines[i].Text));

            var matched = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (candTokens[i].Length == 0) continue;   // an empty row matches nothing
                for (int r = 0; r < refTokens.Count; r++)
                    if (Reranker.LineMatch(candTokens[i], refTokens[r])) { matched[i] = true; break; }
            }

            // The constant offset between the two documents, from the in-order alignment.
            var (_, pairs) = Reranker.LcsAlign(candTokens, refTokens);
            if (pairs.Count < 3) return cand;   // nothing to anchor on
            var deltas = new long[pairs.Count];
            for (int k = 0; k < pairs.Count; k++)
                deltas[k] = cand.Lines[pairs[k].C].StartMs - reference.Lines[pairs[k].R].StartMs;
            Array.Sort(deltas);
            long offset = deltas.Length % 2 == 1
                ? deltas[deltas.Length / 2]
                : (deltas[deltas.Length / 2 - 1] + deltas[deltas.Length / 2]) / 2;
            long refFirst = reference.Lines[0].StartMs, refLast = reference.Lines[^1].StartMs;

            int lead = 0;
            while (lead < n && !matched[lead] && cand.Lines[lead].StartMs - offset < refFirst - EdgeSlackMs) lead++;
            int trail = 0;
            while (trail < n - lead && !matched[n - 1 - trail] && cand.Lines[n - 1 - trail].StartMs - offset > refLast + EdgeSlackMs) trail++;
            if (lead > MaxEdgeRun) lead = 0;
            if (trail > MaxEdgeRun) trail = 0;
            if (lead + trail == 0 || lead + trail >= n) return cand;

            int interior = n - lead - trail, interiorMatched = 0;
            for (int i = lead; i < n - trail; i++) if (matched[i]) interiorMatched++;
            if (interiorMatched / (double)interior < MinInteriorAgreement) return cand;

            leading = lead; trailing = trail;
            var lines = new List<Line>(interior);
            for (int i = lead; i < n - trail; i++) lines.Add(cand.Lines[i]);
            return cand with { Lines = lines };
        }

        // ── 3. grammar ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The key of a credit is short: "Words and Music by" is the longest sane one. A clause that happens
        /// to end in a colon is longer, or has more words, or carries digits/punctuation.</summary>
        public const int MaxKeyChars = 20;
        const int MaxKeyWords = 4;
        /// <summary>A value with no list separator still reads as names when it is this short and every cased word is
        /// capitalised.</summary>
        public const int MaxNameListWords = 8;

        /// <summary>True when <paramref name="text"/> has the shape of a credit: <c>key: value</c> with a short key and
        /// a name-list value, a known key with any value, a ℗/© notice, or a known no-colon English form.
        /// <paramref name="knownKey"/> reports the boost; <paramref name="fullWidthColon"/> reports the CJK <c>：</c>,
        /// which together with a known key is the ONLY combination trusted mid-document ("作曲：X" is not a lyric in any
        /// language, whereas "Girl: I told you" is).</summary>
        public static bool LooksLikeCreditLine(string? text, out bool knownKey, out bool fullWidthColon)
        {
            knownKey = false; fullWidthColon = false;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = StripEnclosure(text.Trim());
            if (s.Length == 0) return false;

            if (s[0] == '℗' || s[0] == '©') { knownKey = true; return true; }

            int colon = IndexOfColon(s, out fullWidthColon);
            if (colon < 0)
            {
                // No colon ⇒ no key/value shape to test; only a form we recognise outright counts.
                knownKey = StartsWithKnownForm(s);
                return knownKey;
            }

            string key = s[..colon].Trim();
            if (key.Length == 0 || key.Length > MaxKeyChars || !KeyShapeOk(key)) return false;
            knownKey = AllPartsKnown(key);
            if (knownKey) return true;
            string value = s[(colon + 1)..].Trim();
            return value.Length > 0 && LooksLikeNameList(value);
        }

        static string StripEnclosure(string s)
        {
            if (s.Length < 2) return s;
            char a = s[0], z = s[^1];
            bool wrapped = (a == '[' && z == ']') || (a == '(' && z == ')') || (a == '（' && z == '）') || (a == '【' && z == '】');
            return wrapped ? s[1..^1].Trim() : s;
        }

        static int IndexOfColon(string s, out bool fullWidth)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == ':') { fullWidth = false; return i; }
                if (s[i] == '：') { fullWidth = true; return i; }
            }
            fullWidth = false;
            return -1;
        }

        static readonly char[] KeySeparators = ['/', '／', '、', '&', '・', '·', '+', ',', '，'];

        static bool KeyShapeOk(string key)
        {
            int words = 1;
            foreach (char c in key)
            {
                if (char.IsWhiteSpace(c)) { words++; continue; }
                if (char.IsDigit(c)) return false;
                if (!char.IsLetter(c) && Array.IndexOf(KeySeparators, c) < 0) return false;
            }
            return words <= MaxKeyWords;
        }

        static bool AllPartsKnown(string key)
        {
            int found = 0;
            foreach (string raw in key.Split(KeySeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                if (part.EndsWith(" and", StringComparison.OrdinalIgnoreCase)) part = part[..^4].TrimEnd();
                if (!IsKnownKey(part)) return false;
                found++;
            }
            return found > 0;
        }

        static bool IsKnownKey(string part)
        {
            if (KnownKeys.Contains(part)) return true;
            // 作词人 / 作曲者 — the role plus a "person" suffix.
            return part.Length > 1 && (part[^1] == '人' || part[^1] == '者') && KnownKeys.Contains(part[..^1]);
        }

        static bool StartsWithKnownForm(string s)
        {
            foreach (string form in NoColonForms)
            {
                if (s.Length < form.Length || !s.StartsWith(form, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.Length == form.Length || char.IsWhiteSpace(s[form.Length])) return true;
            }
            return false;
        }

        static readonly char[] NameSeparators = ['/', '／', '、', '&', '(', ')', '（', '）', ',', '，', '・', '·', ';', '；'];
        static readonly char[] WordBreaks = [' ', '\t', '　', '/', '／', '、', '&', '(', ')', '（', '）', ',', '，', '・', '·', ';', '；'];
        static readonly char[] TrimPunct = ['.', '\'', '"', '“', '”', '-', '–', '—', '［', '］', '[', ']'];
        // Lower-case words a name list legitimately contains ("Simon and Garfunkel", "Vincent van Gogh").
        static readonly HashSet<string> Connectors = new(StringComparer.OrdinalIgnoreCase)
        { "and", "feat", "ft", "of", "the", "de", "da", "di", "la", "le", "van", "von", "der", "y", "e", "et" };

        static bool LooksLikeNameList(string value)
        {
            if (value[^1] is '?' or '!') return false;   // a sentence, not a list
            bool separated = value.IndexOfAny(NameSeparators) >= 0;
            int content = 0;
            foreach (string w in value.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = w.Trim(TrimPunct);
                if (t.Length == 0) continue;
                content++;
                // Names are capitalised; scripts without case (CJK, Hangul) pass by construction.
                if (char.IsLower(t[0]) && !Connectors.Contains(t)) return false;
            }
            return content > 0 && (separated || content <= MaxNameListWords);
        }

        // The vocabulary boost. Matched as a WHOLE key part, case-insensitively, after the shape test above — never as
        // a substring of a lyric.
        static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            // Chinese (simplified + traditional)
            "词", "曲", "词曲", "作词", "作曲", "编曲", "制作", "监制", "混音", "母带", "录音", "和声", "配唱", "吉他", "贝斯", "鼓",
            "键盘", "弦乐", "出品", "发行", "演唱", "歌手", "原唱", "翻唱", "版权", "统筹", "企划", "封面", "歌词", "演奏", "op", "sp",
            "詞", "詞曲", "作詞", "編曲", "製作", "監製", "母帶", "錄音", "和聲", "貝斯", "鍵盤", "弦樂", "發行", "版權", "統籌", "企劃", "歌詞",
            // Japanese (作詞 / 作曲 / 編曲 are shared with the traditional-Chinese forms above)
            "歌", "唄", "作詞作曲",
            // Korean
            "작사", "작곡", "편곡", "노래", "프로듀서", "가수", "보컬",
            // English
            "lyrics", "lyric", "lyricist", "lyricists", "lyrics by", "words", "words by", "words and music", "words and music by",
            "words & music", "words & music by", "music", "music by", "music and lyrics", "music and lyrics by",
            "composed", "composed by", "composer", "composers", "composition",
            "arranged", "arranged by", "arranger", "arrangers", "arrangement",
            "produced", "produced by", "producer", "producers", "written", "written by", "writer", "writers",
            "mixed", "mixed by", "mixing", "mix", "mastered", "mastered by", "mastering", "recorded", "recorded by", "engineer",
            "performed", "performed by", "performer", "performers", "vocals", "vocal", "vocals by",
            "publisher", "publishing", "copyright", "label", "feat", "feat.", "featuring",
            "guitar", "bass", "drums", "keys", "keyboards", "strings", "artist", "title", "album",
        };

        // The English forms that stand without a colon ("Lyrics by Someone", "Copyright 2020 Label").
        static readonly string[] NoColonForms =
        [
            "lyrics by", "words by", "words and music by", "words & music by", "music and lyrics by", "music by", "composed by",
            "arranged by", "produced by", "written by", "mixed by", "mastered by", "recorded by", "performed by", "vocals by",
            "copyright", "feat.", "featuring", "ft.",
        ];

        // ── provider boilerplate ────────────────────────────────────────────────────────────────────────────────────

        // Literal sentences the providers print INSTEAD of lyrics. Matched as substrings, lower-cased (CJK unaffected).
        static readonly string[] InstrumentalNotices =
        [
            "纯音乐，请欣赏", "纯音乐,请欣赏", "纯音乐 请欣赏", "此歌曲为没有填词的纯音乐", "純音樂，請欣賞",
            "this song is instrumental", "this song is an instrumental", "this track is instrumental",
        ];
        static readonly string[] BoilerplateFragments =
        [
            "本歌曲来自", "未经许可不得翻唱或使用", "未经授权", "未經許可", "qq音乐", "酷狗", "网易云", "kugou", "netease",
        ];

        /// <summary>True for the provider's "this is an instrumental" sentence — the document has no lyrics at all,
        /// whatever else it carries (some providers pair it with the writer credits, which would otherwise be displayed
        /// as the lyrics of an instrumental).</summary>
        public static bool IsInstrumentalNotice(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string lower = text.Trim().ToLowerInvariant();
            foreach (string s in InstrumentalNotices) if (lower.Contains(s, StringComparison.Ordinal)) return true;
            return Text.Normalize(text) == "instrumental";
        }

        /// <summary>True for provider boilerplate — an instrumental notice, a licensing line, or branding.</summary>
        public static bool IsProviderBoilerplate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (IsInstrumentalNotice(text)) return true;
            string lower = text.Trim().ToLowerInvariant();
            foreach (string s in BoilerplateFragments) if (lower.Contains(s, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    // ── 18. the cleaner ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ONE owner of "is this row actually a lyric?".
    ///
    /// <para>Every provider ships non-lyric rows inside its lyric document, and they do two kinds of damage. On screen
    /// they render as blank gaps, bare ♪ glyphs, writer credits and a fake first line carrying the song's own title. In
    /// the reranker they inflate the line COUNT that coverage is computed from, which punishes a provider for what its
    /// rival padded with: measured, one reference sends 62 lines of which 23 are ♪ or blank, so a candidate matching 34
    /// of its 35 lines scored coverage 0.565. Cleaned, the same pair scores text 1.000 / coverage 0.872.</para>
    ///
    /// <para>Applied at a SINGLE chokepoint (the aggregator's per-source fetch), so every provider — INCLUDING the
    /// document used as the comparison reference — is cleaned by the same rule and both sides of every comparison stay
    /// consistent.</para></summary>
    public static class Clean
    {
        /// <summary>Token overlap with the track's own title+artist above which a LEADING line is the provider's title
        /// header rather than a lyric.</summary>
        const double HeaderOverlap = 0.8;
        /// <summary>…and how far the real lyrics must start after it, when the line carries no " - " separator. A
        /// header is a pre-roll; a chorus line that happens to be the song's title is not.</summary>
        const long HeaderGapMs = 3000;

        /// <summary>True for a row that carries no readable text at all — "", "♪", "...", "—", "***", "//".
        /// <see cref="Text.Normalize"/> already strips every punctuation and symbol codepoint, so an empty
        /// normalization IS the test; there is deliberately no second hand-written character list to drift from
        /// it.</summary>
        public static bool IsSymbolOnly(string? text) => Text.Normalize(text ?? "").Length == 0;

        /// <summary>Strip the non-lyric rows. <paramref name="title"/>/<paramref name="artists"/> are the track's own
        /// metadata and enable the header rule; omit them (the disk-cache path, which by design never resolves the
        /// track) and the other families still apply.</summary>
        public static Doc Apply(Doc doc, string? title = null, string? artists = null)
            => Apply(doc, title, artists, out _);

        /// <summary>As <see cref="Apply(Doc, string?, string?)"/>, also reporting how many of the dropped rows were
        /// credit headers (for the probe note).</summary>
        public static Doc Apply(Doc doc, string? title, string? artists, out int credits)
        {
            credits = 0;
            int n = doc.Lines.Count;
            if (n == 0) return doc;

            var drop = new bool[n];

            // ── 1. rows with no lyric in them, anywhere in the document ─────────────────────────────────────────────
            // Symbol-only/empty filler, a format tag that leaked into a timed row, and the provider's own boilerplate.
            // An INSTRUMENTAL notice is the provider saying the song has no lyrics at all: the whole document goes (the
            // caller turns an empty result into a miss).
            for (int i = 0; i < n; i++)
            {
                string text = doc.Lines[i].Text;
                if (CreditRules.IsInstrumentalNotice(text)) return doc with { Lines = [] };
                if (IsSymbolOnly(text) || CreditRules.IsStructuralMetadata(text) || CreditRules.IsProviderBoilerplate(text))
                    drop[i] = true;
            }

            // ── 2. credits by grammar, positionally (tier 3) ────────────────────────────────────────────────────────
            // Credits sit at the top and bottom of a document. In the LEADING and TRAILING runs any credit-shaped line
            // goes; in the MIDDLE only a KNOWN key with a full-width colon does.
            var credit = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (drop[i]) continue;                                                     // filler does not end the run
                if (!CreditRules.LooksLikeCreditLine(doc.Lines[i].Text, out _, out _)) break;   // first real lyric
                credit[i] = true;
            }
            for (int i = n - 1; i >= 0; i--)
            {
                if (drop[i] || credit[i]) continue;
                if (!CreditRules.LooksLikeCreditLine(doc.Lines[i].Text, out _, out _)) break;
                credit[i] = true;
            }
            for (int i = 0; i < n; i++)
            {
                if (drop[i] || credit[i]) continue;
                if (CreditRules.LooksLikeCreditLine(doc.Lines[i].Text, out bool known, out bool fullWidth) && known && fullWidth)
                    credit[i] = true;
            }
            // Credits alone never empty a document: if every surviving line is credit-shaped, the shape is the
            // document's idiom rather than a header, and the grammar is the thing that is wrong. Keep them all.
            int survivors = 0;
            for (int i = 0; i < n; i++) if (!drop[i] && !credit[i]) survivors++;
            if (survivors > 0)
                for (int i = 0; i < n; i++) if (credit[i]) { drop[i] = true; credits++; }

            // ── 3. the provider's title header ──────────────────────────────────────────────────────────────────────
            // Both edges: a search-matched document can carry the same "Title - Artist" row at the top (the common
            // case) or, less often, repeated at the bottom after the last sung line.
            int first = FirstKept(drop);
            if (first >= 0 && IsTitleHeader(doc, first, drop, title, artists, leading: true)) drop[first] = true;
            int last = LastKept(drop);
            if (last >= 0 && IsTitleHeader(doc, last, drop, title, artists, leading: false)) drop[last] = true;

            int kept = 0;
            foreach (bool d in drop) if (!d) kept++;
            if (kept == n) return doc;

            // ── rebuild, FOLDING each dropped row's timestamp into the line above it ────────────────────────────────
            // A ♪ or blank row is where the previous line stops being sung. Dropping it without carrying that over
            // would stretch the preceding line across the whole instrumental and would erase the very gap the interlude
            // dots are detected from.
            var lines = new List<Line>(kept);
            for (int i = 0; i < n; i++)
            {
                if (!drop[i]) { lines.Add(doc.Lines[i]); continue; }
                if (lines.Count == 0) continue;                          // nothing above it to carry the end to
                int lastIdx = lines.Count - 1;
                if (lines[lastIdx].EndMs is null) lines[lastIdx] = lines[lastIdx] with { EndMs = doc.Lines[i].StartMs };
            }
            return doc with { Lines = lines };
        }

        static int FirstKept(bool[] drop)
        {
            for (int i = 0; i < drop.Length; i++) if (!drop[i]) return i;
            return -1;
        }

        static int LastKept(bool[] drop)
        {
            for (int i = drop.Length - 1; i >= 0; i--) if (!drop[i]) return i;
            return -1;
        }

        /// <summary>True when the KEPT row at <paramref name="index"/> is a "Title (annotation) - Artist" header rather
        /// than a lyric. A CJK provider's own bracketed remark — a franchise/TV-tie-in note — sits alongside the title
        /// on the very row this trims, and its words are never part of the track's own metadata, so counting them
        /// toward the row's token total used to drag the overlap ratio below <see cref="HeaderOverlap"/> and let the
        /// header through. <see cref="StripBracketedAnnotations"/> removes bracketed content before the ratio is
        /// judged, exactly as a reader would ignore a parenthetical aside.</summary>
        static bool IsTitleHeader(Doc doc, int index, bool[] drop, string? title, string? artists, bool leading)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;

            string rawText = doc.Lines[index].Text;
            string[] lineTokens = Tokens(StripBracketedAnnotations(rawText));
            if (lineTokens.Length == 0) return false;
            var meta = new HashSet<string>(Tokens(title + " " + (artists ?? "")), StringComparer.Ordinal);
            if (meta.Count == 0) return false;

            int hits = 0;
            foreach (string t in lineTokens) if (meta.Contains(t)) hits++;
            if (hits / (double)lineTokens.Length < HeaderOverlap) return false;

            // Corroboration, so a chorus/outro line that IS the song's title survives. A header always carries the
            // "Title - Artist" separator when it repeats at the BOTTOM (providers duplicate the literal header string,
            // never a bare title) — the only signal trusted there. At the TOP a header can also be a bare pre-roll with
            // no separator, corroborated instead by sitting well before the singing starts; that heuristic does NOT
            // carry over to the trailing edge, where an isolated last line after an instrumental outro is a completely
            // ordinary way for a song to end on its own hook.
            if (rawText.Contains(" - ", StringComparison.Ordinal)) return true;
            if (!leading) return false;
            for (int j = index + 1; j < doc.Lines.Count; j++)
            {
                if (drop[j]) continue;
                return doc.Lines[j].StartMs - doc.Lines[index].StartMs >= HeaderGapMs;
            }
            return false;   // it is the only kept line — keep it rather than empty the document
        }

        static string[] Tokens(string text)
        {
            string n = Text.Normalize(text);
            return n.Length == 0 ? [] : n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        // Brackets of every family the sources actually ship: half-width/full-width parens, 《book title marks》,
        // 【lenticular brackets】 and square brackets. Removed WHOLE (open through its matching close) so the
        // annotation's words never dilute the header-overlap ratio; UNBALANCED brackets are left untouched rather than
        // risk eating real lyric text past an unmatched opener.
        static readonly (char Open, char Close)[] BracketPairs =
        [
            ('(', ')'), ('（', '）'), ('《', '》'), ('【', '】'), ('[', ']'),
        ];

        static bool IsBracketOpen(char c, out char close)
        {
            foreach (var (open, cl) in BracketPairs) if (c == open) { close = cl; return true; }
            close = '\0';
            return false;
        }

        /// <summary>Remove every bracketed run WHOLE (open through its matching close). Public because it is the
        /// header rule's most surprising term and the one a test has to be able to drive on its own.</summary>
        public static string StripBracketedAnnotations(string text)
        {
            bool anyBracket = false;
            foreach (char c in text) if (IsBracketOpen(c, out _)) { anyBracket = true; break; }
            if (!anyBracket) return text;

            var sb = new StringBuilder(text.Length);
            var closers = new Stack<char>();
            foreach (char c in text)
            {
                if (IsBracketOpen(c, out char close)) { closers.Push(close); continue; }
                if (closers.Count > 0)
                {
                    if (c == closers.Peek()) closers.Pop();
                    continue;   // still inside (or just closed) a bracketed run — its text never reaches the output
                }
                sb.Append(c);
            }
            return closers.Count == 0 ? sb.ToString() : text;
        }
    }

    // ── 19. the reranker ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Score every candidate together against the reference (text agreement + timing coherence + sync tier +
    /// coverage + provider prior), correct a constant timing offset on the winner, and gate word-synced candidates so
    /// a WRONG word-synced lyric cannot beat a correct line-synced one. Pure and deterministic.</summary>
    public static class Reranker
    {
        const double WText = 0.40, WSync = 0.25, WTiming = 0.20, WCoverage = 0.10, WPrior = 0.05;
        const double LineMatchThreshold = 0.5;   // token overlap to call two lines "the same line"
        const double DriftToleranceMs = 600;     // MAD above this ⇒ timing is locally wrong
        const long TimingFallbackBucketMs = 100;
        const long TimingFallbackPairToleranceMs = 600;
        // Sync-gate thresholds. The text bar is a low FLOOR; the timing/coverage guardrails do the real work of
        // rejecting a wrong song.
        const double SyncGateTextFloor = 0.15, SyncGateTiming = 0.5, SyncGateCoverage = 0.6;
        // TIER-PROMOTION bar — deliberately much stricter than the sync gate, because the CONSEQUENCE is stronger.
        // Clearing the sync gate merely preserves a candidate's sync weight (+0.10 in the blended score); clearing this
        // one lets a candidate WIN OUTRIGHT over a better-scoring line lyric. A candidate that cannot clear this bar is
        // not rejected; it falls back to the blended score, where a genuinely strong word-sync can still win.
        const double TierTextFloor = 0.50, TierTiming = 0.70, TierCoverage = 0.60;

        public static Ranked Rank(IReadOnlyList<Candidate> candidates, Doc? reference)
        {
            if (candidates.Count == 0) return new Ranked(null, null, []);

            List<string[]>? refTokens = null;
            if (reference is not null)
            {
                refTokens = new List<string[]>(reference.Lines.Count);
                for (int i = 0; i < reference.Lines.Count; i++) refTokens.Add(Tokens(reference.Lines[i].Text));
            }

            var decisions = new List<Decision>(candidates.Count);
            // Stage-one bookkeeping for the tier pass: is this candidate corroborated as the right recording, and what
            // sync tier does it ACTUALLY deliver (a gated word-sync delivers line, because that is what it is stripped
            // to).
            var verified = new bool[candidates.Count];
            var tiers = new int[candidates.Count];
            int bestIdx = -1; double bestScore = double.NegativeInfinity;

            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                bool trusted = c.Basis is MatchBasis.Identity or MatchBasis.Isrc;
                var candTokens = new List<string[]>(c.Document.Lines.Count);
                for (int k = 0; k < c.Document.Lines.Count; k++) candTokens.Add(Tokens(c.Document.Lines[k].Text));

                double text, coverage, timing; long applied = 0; string reason;
                if (refTokens is { Count: > 0 } && candTokens.Count > 0)
                {
                    var (lcs, pairs) = LcsAlign(candTokens, refTokens);
                    bool timingFallback = false;
                    if (pairs.Count < 3 && trusted && TryTimingFallbackPairs(c.Document, reference!, out var fallbackPairs))
                    {
                        pairs = fallbackPairs;
                        timingFallback = true;
                    }
                    int overlap = Math.Min(candTokens.Count, refTokens.Count);
                    text = lcs / (double)Math.Max(1, overlap);
                    coverage = overlap / (double)Math.Max(1, Math.Max(candTokens.Count, refTokens.Count));
                    (timing, applied) = TimingScoreVsRef(c.Document, reference!, pairs, text);
                    reason = $"ref-align lcs={lcs}/{overlap} off={applied}ms";
                    if (timingFallback) reason += " [timing-fallback]";
                    // Explain the neutral-0.6 / rejected-0.0 timing verdicts — otherwise "timing 0.60" on an unsynced
                    // reference reads identically to a real timing match.
                    if (reference!.Sync == SyncKind.Unsynced)
                        reason += " [ref-unsynced:timing-neutral-for-all]";
                    else if (pairs.Count < 3)
                        reason += text >= SyncGateTextFloor
                            ? " [timing:too-few-pairs→neutral]"
                            : " [timing:too-few-pairs+no-text→rejected]";
                }
                else
                {
                    // No reference: cannot verify content. Credit coverage + intrinsic timing sanity; stay text-neutral
                    // so a word-synced, internally coherent candidate can still win on sync/timing.
                    text = candTokens.Count > 0 ? 0.6 : 0.0;
                    coverage = Math.Min(1.0, candTokens.Count / 10.0);
                    timing = IntrinsicTimingSanity(c.Document);
                    reason = "no-reference";
                }

                double sync = c.Sync switch { SyncKind.Syllable => 1.0, SyncKind.Line => 0.6, SyncKind.Unsynced => 0.2, _ => 0.2 };
                // Sync gate: a word-synced candidate keeps its sync advantage unless it looks like a DIFFERENT song. An
                // identity/ISRC-matched source IS the exact recording, so it is never demoted on fuzzy text
                // disagreement with a reference's own (often sparse/romanized) line lyric. Otherwise apply a low text
                // FLOOR plus the timing/coverage guardrails: correct-but-divergent karaoke (romanization, CJK, ad-libs)
                // scores ~0.15-0.5 text and must survive, while a truly wrong song has incoherent timing and/or
                // mismatched line counts and is still demoted below a clean line candidate.
                if (c.Sync == SyncKind.Syllable && refTokens is { Count: > 0 } && !trusted
                    && !(text >= SyncGateTextFloor && timing >= SyncGateTiming && coverage >= SyncGateCoverage))
                {
                    sync = 0.45;
                    reason += " [sync-gate:demoted]";
                }
                // WORD-TIMING gate — a different question from the sync gate, and deliberately NOT exempted by trust.
                // An identity/ISRC match proves the candidate is the right RECORDING; it says nothing about whether its
                // word offsets are physically singable. A body whose lines fire twelve words in 167 ms is word-synced
                // in name only, and the reranker cannot see it any other way: the timing score aligns LINE STARTS,
                // which such a document gets perfectly right. Demote to the LINE tier (0.6, not the sync gate's 0.45):
                // it IS the right song, delivered at line resolution, which is what the winner is repaired down to.
                int impossible = 0, judged = 0;   // the && short-circuits for a non-syllable candidate, so seed them
                bool wordTimingRejected = c.Sync == SyncKind.Syllable
                    && Timing.HasImplausibleWordTiming(c.Document, out impossible, out judged);
                if (wordTimingRejected)
                {
                    sync = Math.Min(sync, 0.6);
                    reason += $" [word-timing-gate: {impossible}/{judged} lines impossibly fast]";
                }

                double score = WText * text + WSync * sync + WTiming * timing + WCoverage * coverage + WPrior * Math.Clamp(c.Prior, 0, 1);

                // VERIFIED = corroborated well enough to override a better-scoring candidate on tier alone.
                // Identity/ISRC is verified by construction — BUT ONLY WHEN THERE IS A REFERENCE TO CHECK IT AGAINST: a
                // decoy is trusted by ISRC and has ZERO text agreement with the real lyric, and "verified by
                // construction, no text floor" is exactly how it used to win stage two outright regardless of its 0.32
                // score. With a reference in hand, trust alone is no longer enough — text must be > 0 (any real overlap
                // at all, not the strict tier bar) before construction-trust is honoured; without a reference there is
                // nothing to check text against, so trust alone still verifies.
                verified[i] = refTokens is { Count: > 0 }
                    ? text > 0 && (trusted || (text >= TierTextFloor && timing >= TierTiming && coverage >= TierCoverage))
                    : trusted;

                decisions.Add(new Decision(c.ProviderId, c.Sync, score, text, coverage, timing, applied, reason, sync, verified[i]));
                if (score > bestScore) { bestScore = score; bestIdx = i; }
                // The tier it actually DELIVERS — a rejected word-sync delivers line, because that is what the winner
                // repair strips it to.
                tiers[i] = wordTimingRejected ? 2 : c.Sync switch
                {
                    SyncKind.Syllable => 3,
                    SyncKind.Line => 2,
                    SyncKind.Unsynced => 1,
                    _ => 0,
                };
            }

            // ── stage two: among the VERIFIED candidates, richness decides ───────────────────────────────────────────
            // The weighted sum answers two questions at once and therefore answers neither well: it mixes "is this the
            // right song?" with "is this the better lyric?". Those are different kinds of judgement — the first is a
            // THRESHOLD, the second an ORDERING. Conflating them is what made a genuine word-synced candidate lose:
            // sync is weighted 0.25, so syllable over line is worth only +0.10, and a provider with a perfect text
            // match beats that on the text term alone — the karaoke lost to the paragraph.
            //
            // So: once a candidate is verified, prefer the richest tier it actually delivers, and use the score only to
            // break ties WITHIN a tier. Only with a reference to verify against — promoting an unchecked word-sync on
            // tier alone is precisely the wrong-song failure the sync gate exists to prevent.
            int chosenIdx = bestIdx;
            if (refTokens is { Count: > 0 })
            {
                int bestTier = -1; double bestTierScore = double.NegativeInfinity;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (!verified[i]) continue;
                    if (tiers[i] > bestTier || (tiers[i] == bestTier && decisions[i].Score > bestTierScore))
                    { bestTier = tiers[i]; bestTierScore = decisions[i].Score; chosenIdx = i; }
                }
            }

            var winnerCand = candidates[chosenIdx];
            long bestOffset = decisions[chosenIdx].AppliedOffsetMs;
            // Repair before publishing: if the winner only won BECAUSE nothing better existed, it must still not reach
            // the view claiming word timing it does not have. Its line starts are kept (they are the part that is
            // right) and the unsingable syllables/ends are dropped.
            var winnerDoc = winnerCand.Document;
            if (winnerDoc.Sync == SyncKind.Syllable && Timing.HasImplausibleWordTiming(winnerDoc, out _, out _))
                winnerDoc = Timing.StripWordTiming(winnerDoc);
            var winner = ApplyOffset(winnerDoc, bestOffset);
            return new Ranked(winner, decisions[chosenIdx], decisions);
        }

        // ── text agreement: fuzzy line-sequence LCS ──────────────────────────────────────────────────────────────────
        // Internal, not private: TrimUnalignedEdges asks "does this line align to the reference?" and must use the SAME
        // notion of alignment the score is computed with.

        internal static string[] Tokens(string text)
        {
            var n = Text.Normalize(text);
            return n.Length == 0 ? [] : n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        internal static bool LineMatch(string[] a, string[] b)
        {
            if (a.Length == 0 && b.Length == 0) return true;
            if (a.Length == 0 || b.Length == 0) return false;
            var set = new HashSet<string>(a);
            int inter = 0;
            foreach (var t in b) if (set.Contains(t)) inter++;
            double overlap = inter / (double)Math.Min(a.Length, b.Length);   // overlap coefficient (length-robust)
            return overlap >= LineMatchThreshold;
        }

        /// <summary>LCS over the two line sequences with fuzzy line equality; returns the LCS length and the matched
        /// index pairs (candIndex, refIndex) in order — the pairs drive the timing-offset estimate.</summary>
        internal static (int Lcs, List<(int C, int R)> Pairs) LcsAlign(List<string[]> cand, List<string[]> reff)
        {
            int n = cand.Count, m = reff.Count;
            var dp = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    dp[i, j] = LineMatch(cand[i], reff[j]) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

            var pairs = new List<(int, int)>();
            for (int i = 0, j = 0; i < n && j < m;)
            {
                if (LineMatch(cand[i], reff[j])) { pairs.Add((i, j)); i++; j++; }
                else if (dp[i + 1, j] >= dp[i, j + 1]) i++;
                else j++;
            }
            return (dp[0, 0], pairs);
        }

        // ── timing ──────────────────────────────────────────────────────────────────────────────────────────────────

        static bool TryTimingFallbackPairs(Doc cand, Doc reff, out List<(int C, int R)> pairs)
        {
            pairs = [];
            if (cand.Lines.Count < 3 || reff.Lines.Count < 3) return false;

            var buckets = new Dictionary<long, int>();
            for (int c = 0; c < cand.Lines.Count; c++)
            {
                long cs = cand.Lines[c].StartMs;
                for (int r = 0; r < reff.Lines.Count; r++)
                {
                    long bucket = DeltaBucket(cs - reff.Lines[r].StartMs);
                    buckets.TryGetValue(bucket, out int n);
                    buckets[bucket] = n + 1;
                }
            }

            long bestBucket = 0;
            int bestCount = 0;
            bool tied = false;
            foreach (var kv in buckets)
            {
                if (kv.Value > bestCount)
                {
                    bestBucket = kv.Key;
                    bestCount = kv.Value;
                    tied = false;
                }
                else if (kv.Value == bestCount)
                {
                    tied = true;
                }
            }
            if (bestCount < 3 || tied) return false;

            int nextRef = 0;
            for (int c = 0; c < cand.Lines.Count && nextRef < reff.Lines.Count; c++)
            {
                int bestRef = -1;
                long bestErr = long.MaxValue;
                long cs = cand.Lines[c].StartMs;
                for (int r = nextRef; r < reff.Lines.Count; r++)
                {
                    long err = Math.Abs((cs - reff.Lines[r].StartMs) - bestBucket);
                    if (err < bestErr)
                    {
                        bestErr = err;
                        bestRef = r;
                    }
                    if (reff.Lines[r].StartMs > cs - bestBucket + TimingFallbackPairToleranceMs && bestErr <= TimingFallbackPairToleranceMs)
                        break;
                }
                if (bestRef < 0 || bestErr > TimingFallbackPairToleranceMs) continue;
                pairs.Add((c, bestRef));
                nextRef = bestRef + 1;
            }

            return pairs.Count >= 3;
        }

        static long DeltaBucket(long delta)
            => (long)Math.Round(delta / (double)TimingFallbackBucketMs, MidpointRounding.AwayFromZero) * TimingFallbackBucketMs;

        /// <summary>The neutral 0.6 used to fire whenever <paramref name="pairs"/> was too small to align on —
        /// including against an Unsynced reference, which by definition NEVER has 3 real timing pairs (every start is
        /// 0). That made every candidate's timing term identical and useless for telling a real lyric from a decoy, so
        /// an Unsynced reference now returns the same neutral for every candidate unconditionally, while an
        /// otherwise-synced reference with too few pairs only stays neutral when the candidate ALSO has some text
        /// agreement — a candidate that aligns on neither text NOR timing gets 0, not a free pass.</summary>
        static (double Score, long AppliedOffsetMs) TimingScoreVsRef(Doc cand, Doc reff, List<(int C, int R)> pairs, double text)
        {
            if (reff.Sync == SyncKind.Unsynced) return (0.6, 0);
            if (pairs.Count < 3) return (text >= SyncGateTextFloor ? 0.6 : 0.0, 0);   // too few matches → neutral
            var deltas = new long[pairs.Count];
            for (int k = 0; k < pairs.Count; k++)
                deltas[k] = cand.Lines[pairs[k].C].StartMs - reff.Lines[pairs[k].R].StartMs;
            long median = Median(deltas);
            var absDev = new long[deltas.Length];
            for (int k = 0; k < deltas.Length; k++) absDev[k] = Math.Abs(deltas[k] - median);
            long mad = Median(absDev);
            double score = Math.Clamp(1.0 - mad / DriftToleranceMs, 0.0, 1.0);
            // Correct the constant offset only when internally coherent (low drift); otherwise the times are locally
            // wrong and shifting would not help — leave it and let the low score demote it.
            long applied = mad <= DriftToleranceMs ? -median : 0;
            return (score, applied);
        }

        static double IntrinsicTimingSanity(Doc doc)
        {
            if (doc.Lines.Count < 2) return 0.4;
            int monotonic = 0;
            for (int i = 1; i < doc.Lines.Count; i++)
                if (doc.Lines[i].StartMs >= doc.Lines[i - 1].StartMs) monotonic++;
            return 0.3 + 0.6 * (monotonic / (double)(doc.Lines.Count - 1));
        }

        static long Median(long[] v)
        {
            if (v.Length == 0) return 0;
            var s = (long[])v.Clone();
            Array.Sort(s);
            return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
        }

        /// <summary>Shift every line + syllable timestamp by <paramref name="offsetMs"/> and record it on the
        /// document.</summary>
        public static Doc ApplyOffset(Doc doc, long offsetMs)
        {
            if (offsetMs == 0) return doc;
            static long Shift(long t, long o) => Math.Max(0, t + o);
            var lines = new List<Line>(doc.Lines.Count);
            for (int i = 0; i < doc.Lines.Count; i++)
            {
                var l = doc.Lines[i];
                IReadOnlyList<Syllable> syl = l.Syllables;
                if (l.Syllables.Count > 0)
                {
                    var copy = new List<Syllable>(l.Syllables.Count);
                    for (int k = 0; k < l.Syllables.Count; k++)
                    {
                        var s = l.Syllables[k];
                        copy.Add(new Syllable(Shift(s.StartMs, offsetMs), Shift(s.EndMs, offsetMs), s.Text));
                    }
                    syl = copy;
                }
                lines.Add(l with
                {
                    StartMs = Shift(l.StartMs, offsetMs),
                    EndMs = l.EndMs is { } e ? Shift(e, offsetMs) : (long?)null,
                    Syllables = syl,
                });
            }
            return doc with { Lines = lines, OffsetMsApplied = offsetMs };
        }
    }
}
