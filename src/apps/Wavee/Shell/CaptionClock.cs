// ── Shell/CaptionClock.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the stage caption's CLOCK (themes plan C4, X11): the pure view rules (`Stage.Caption`: which line the caption hangs on, the
// break's dots, the next edge, the one-shot edge wake) and `CaptionClock`, the playhead the caption extrapolates and the wake
// it arms, as one engine-free object
//
// Role: CORE
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §4.4
//
// `Stage.Caption` MOVED VERBATIM from `Stage.cs` (same name, so the caption suites pin it unchanged). `CaptionClock` is what
// `CaptionHost` kept in five fields: the position interpolator, whether it has been anchored, whether the transport advances,
// the last report and the edge wake. It takes the frame clock as an argument (`FrameTime.NowMs` in production), so
// `CaptionClockTests` drives it with a fake one.

namespace Wavee;

public static partial class Stage
{
    // ── 6. the caption's clock view (fed the lyrics view's OWN resolves — Lyrics.ResolveLine / AdvancePastInterlude) ──

    public static class Caption
    {
        /// <summary>What the caption hangs on at <paramref name="nowMs"/>. <c>Anchor</c> is the line the view centres on
        /// (−1 = before the first line); <c>Dots</c> says the break's breathing dots stand in for the active line.
        /// <paramref name="leadLine"/> is the lyrics view's lead-resolved line (<c>ResolveLine(now + LeadMs)</c>);
        /// <paramref name="gapStart"/>/<paramref name="gapEnd"/> the real break <c>AdvancePastInterlude</c> reported for it
        /// (equal ⇒ none). The dots hold the slot from the sung-out point until the NEXT line resolves on the same lead, so
        /// the hand-off is dots → next line with nothing between (never the finished line flashing back, never a line-synced
        /// line lit before it is sung). A long intro (≥ <paramref name="minGapMs"/>) breathes the same dots toward line 0.</summary>
        public static (int Anchor, bool Dots) View(int leadLine, long gapStart, long gapEnd, long firstStartMs, long nowMs, long leadMs, long minGapMs)
        {
            if (leadLine < 0) return (-1, firstStartMs >= minGapMs && nowMs + leadMs < firstStartMs);
            return (leadLine, gapEnd > gapStart && nowMs >= gapStart && nowMs + leadMs < gapEnd);
        }

        /// <summary>The packed view (<see cref="Pack"/>) of <paramref name="d"/> at <paramref name="nowMs"/> from the lyrics
        /// view's OWN resolves (the lead-shifted line, the break <c>AdvancePastInterlude</c> reports), decided by
        /// <see cref="View"/>. The intro's "break" is [0, first line).</summary>
        public static int ViewAt(Lyrics.Doc d, long nowMs, out long gapStart, out long gapEnd)
        {
            var lines = d.Lines;
            int lead = Lyrics.ResolveLine(lines, nowMs + Lyrics.LeadMs);
            _ = Lyrics.AdvancePastInterlude(d, lead, nowMs, out gapStart, out gapEnd);
            long first = lines.Count > 0 ? lines[0].StartMs : long.MaxValue;
            if (lead < 0) { gapStart = 0L; gapEnd = first; }
            var (anchor, dots) = View(lead, gapStart, gapEnd, first, nowMs, Lyrics.LeadMs, Lyrics.InterludeGapMs);
            return Pack(anchor, dots);
        }

        /// <summary>The first media instant after <paramref name="nowMs"/> at which <see cref="ViewAt"/> changes
        /// (<see cref="long.MaxValue"/> = never): the next line's start coming within the lead (which also ends a break and an
        /// intro), or the lead line's sung-out point when a real break follows it (where the break's dots take the slot). The
        /// view is constant before it, so a playing caption whose wipe the render thread poses needs ONE wake there, not a
        /// tick per frame.</summary>
        public static long NextEdgeMs(Lyrics.Doc d, long nowMs)
        {
            var lines = d.Lines;
            if (lines.Count == 0) return long.MaxValue;
            int lead = Lyrics.ResolveLine(lines, nowMs + Lyrics.LeadMs);
            long next = lead + 1 < lines.Count ? lines[lead + 1].StartMs - Lyrics.LeadMs : long.MaxValue;
            // the sung-out point matters only where a real break follows (AdvancePastInterlude's own rule): the dots take the slot
            if (lead >= 0 && lead + 1 < lines.Count)
            {
                long sungOut = Lyrics.SungOutMs(d, lead);
                if (sungOut > nowMs && sungOut < next && lines[lead + 1].StartMs - sungOut >= Lyrics.InterludeGapMs) next = sungOut;
            }
            return next;
        }

        /// <summary>A re-arm whose due instant moved by no more than this (ms) keeps the armed wake: a position report that
        /// re-anchors the clock by a frame's jitter must not restart the timer once a second.</summary>
        public const long EdgeRearmSlackMs = 2L;

        /// <summary>The caption's ONE one-shot wake at its next view edge, as a pure arming rule. <see cref="Arm"/> takes the
        /// media instant (<see cref="NextEdgeMs"/>, or <see cref="long.MaxValue"/> for none), the media position now and the
        /// caller's clock now (ONE clock: the due instant is computed and compared in it), and says whether a new one-shot
        /// must be published (<c>delayMs</c> &lt; 0 = cancel). It re-arms whenever the edge moved, the due instant moved
        /// (a seek within the line), or the armed wake has FIRED (<see cref="Fire"/>): a timer that fires on another clock a
        /// little before the edge finds the view unchanged and arms again instead of losing the edge.</summary>
        public struct EdgeWake
        {
            bool _armed, _fired;
            long _atMs, _dueMs;

            /// <summary>A one-shot is armed and has not fired.</summary>
            public readonly bool Pending => _armed && !_fired;
            /// <summary>The media instant armed (meaningful while <see cref="Pending"/>).</summary>
            public readonly long AtMs => _atMs;

            public bool Arm(long atMs, long mediaNowMs, long clockNowMs, out float delayMs)
            {
                if (atMs == long.MaxValue)
                {
                    delayMs = -1f;
                    if (!_armed) return false;
                    _armed = _fired = false;
                    return true;
                }
                long delay = Math.Max(1L, atMs - mediaNowMs);
                long due = clockNowMs + delay;
                if (_armed && !_fired && atMs == _atMs && Math.Abs(due - _dueMs) <= EdgeRearmSlackMs) { delayMs = 0f; return false; }
                _armed = true; _fired = false; _atMs = atMs; _dueMs = due;
                delayMs = delay;
                return true;
            }

            /// <summary>The armed one-shot fired (its callback runs next): the next <see cref="Arm"/> arms afresh.</summary>
            public void Fire() => _fired = true;
        }

        /// <summary>One int for the view (the host's ONE signal — a re-render only when the line or the break edge moves).</summary>
        public static int Pack(int anchor, bool dots) => ((anchor + 1) << 1) | (dots ? 1 : 0);
        public static int AnchorOf(int packed) => (packed >> 1) - 1;
        public static bool DotsOf(int packed) => (packed & 1) != 0;

        /// <summary>The three slots' line indices (−1 = empty): the previous line above, the active line (none while the dots
        /// stand in), the next line below. In a break the previous slot is the line just sung. Without context (Compact, a
        /// short window) only the centre shows.</summary>
        public static (int Prev, int Centre, int Next) Slots(int anchor, bool dots, bool context, int count)
        {
            int centre = dots ? -1 : Valid(anchor, count);
            int prev = context ? Valid(dots ? anchor : anchor - 1, count) : -1;
            int next = context ? Valid(anchor + 1, count) : -1;
            return (prev, centre, next);

            static int Valid(int i, int n) => (uint)i < (uint)n ? i : -1;
        }
    }

    /// <summary>The caption's playhead and its one-shot wake. UI thread only; one per mounted caption.</summary>
    public sealed class CaptionClock
    {
        /// <summary>How far a report may land behind the clock and still be jitter (ms): the report's own latency, a position timer that
        /// fired late. Within it the caption never steps back (audit B3, V2: a report a frame behind the extrapolated playhead flipped the
        /// view back over a line edge and forward again, replaying the hand-off, so the outgoing and the incoming line drew together); a
        /// seek back further than this moves it.</summary>
        public const long BackJitterMs = 500L;

        Deck.PositionInterpolator _pos;
        bool _anchored, _advancing;
        int _lastReport = int.MinValue;
        long _floor = long.MinValue;   // the latest instant Now() answered while advancing: a jitter-sized correction holds it, never undoes it
        Caption.EdgeWake _edge;

        /// <summary>A host report has anchored the clock at least once: before that, <see cref="Now"/> would extrapolate machine
        /// uptime, so nothing may read it.</summary>
        public bool Anchored => _anchored;

        /// <summary>The transport advances on its own (playing and not buffering): the clock extrapolates only then.</summary>
        public bool Advancing => _advancing;

        /// <summary>A position report (<paramref name="positionMs"/>, <paramref name="advancing"/>) at frame clock
        /// <paramref name="frameNowMs"/>. Re-anchors on every CHANGED report AND on a play/pause edge (the deck's
        /// <see cref="Deck.PositionInterpolator"/> discipline: without the edge a resume would extrapolate across the whole paused
        /// gap for a tick). True when it re-anchored.</summary>
        public bool Report(int positionMs, bool advancing, long frameNowMs)
        {
            bool reanchor = positionMs != _lastReport || advancing != _advancing || !_anchored;
            if (reanchor)
            {
                // the floor survives only a report a little behind it while the transport keeps advancing; a pause, a resume and a real
                // seek back start the clock afresh
                if (!advancing || !_advancing || _floor - positionMs > BackJitterMs) _floor = long.MinValue;
                _pos.Anchor(frameNowMs, positionMs);
                _anchored = true;
                _lastReport = positionMs;
            }
            _advancing = advancing;
            return reanchor;
        }

        /// <summary>The media position now, extrapolated from the last report (clamped to the duration). While the transport advances it
        /// never runs backwards by less than <see cref="BackJitterMs"/>: after a report a little behind it, it holds until the media catches up.</summary>
        public long Now(long frameNowMs, long durationMs)
        {
            long now = _pos.Estimate(frameNowMs, _advancing, null, null, durationMs);
            if (!_advancing) return now;
            if (now < _floor) return _floor;
            _floor = now;
            return now;
        }

        /// <summary>Arm (or clear) the one-shot wake at media instant <paramref name="atMs"/> (<see cref="long.MaxValue"/> =
        /// none), in the frame clock's one domain. True = publish a new one-shot (<paramref name="delayMs"/> &lt; 0 = cancel).</summary>
        public bool ArmEdge(long atMs, long mediaNowMs, long frameNowMs, out float delayMs)
            => _edge.Arm(atMs, mediaNowMs, frameNowMs, out delayMs);

        /// <summary>The armed one-shot fired: the next <see cref="ArmEdge"/> arms afresh.</summary>
        public void FireEdge() => _edge.Fire();
    }
}
