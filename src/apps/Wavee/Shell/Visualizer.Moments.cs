// ── Shell/Visualizer.Moments.cs ────────────────────────────────────────────────────────────────────────────────────
// Visualizer.Moments — "change with the music": every 8 bars, on the downbeat, the palette rotates (A, B, C) → (B, C, A)
// and every face re-tints over 900 ms (1.4 s calm); a face may force one (Verse on a chorus entry)
//
// Role: CORE
// Owner: K
// Wave: 7
// Budget: 120 lines
// Spec: viz-app-plan §3.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// BEAT-LOCKED, NEVER A TIMER. A moment lands on a downbeat the model CROSSED while playing (`Frame.DownbeatEdge` — a seek
// moves the bar, it is not an edge) whose bar is a multiple of 8. The bars come from the beat grid's downbeat marks, else
// every 4th beat of the grid or the tempo; without either there are no bars and so no moments (honest). A forced moment
// lands at once; the scheduled one after it is skipped when it would follow within 8 bars, so the palette never turns
// twice in a phrase. Only a forced moment holds one back: a scheduled bar re-crossed after a backward seek turns again.
//
// The CLOCK owns the fade (Visualizer.Palette.Fade): it captures the slab's current colours, lerps A/B/C/Deep toward the
// rotated palette, drives `Slab.MomentMix` 0 → 1, and on landing bumps `Slab.PaletteEpoch` and republishes
// `StageCtx.Palette` rotated (so gradient faces, keyed by the epoch, remount on the new colours). This file decides WHEN.
//
// Rules: pure (the one static is the force request's sequence, written on the UI thread only); no allocation.

namespace Wavee;

public static partial class Visualizer
{
    public static class Moments
    {
        /// <summary>A moment every this many bars.</summary>
        public const int Bars = 8;

        /// <summary>The fade's length: <see cref="Stage.Tone.MomentFadeMs"/>, or the calm one.</summary>
        public static float FadeMsFor(bool calm) => calm ? Stage.Tone.MomentCalmFadeMs : Stage.Tone.MomentFadeMs;

        static long s_forced;

        /// <summary>The force requests so far (the clock compares it with the last one it saw).</summary>
        public static long ForcedSequence => s_forced;

        /// <summary>Ask for a moment on the next clock tick (Verse on a chorus entry). UI thread.</summary>
        public static void Force() => s_forced++;

        /// <summary>Is this tick a scheduled moment? A downbeat crossed while playing, on a bar that is a multiple of 8.</summary>
        public static bool Scheduled(int bar, bool downbeatEdge) => downbeatEdge && bar > 0 && bar % Bars == 0;

        /// <summary>The per-stage schedule (a value the clock owns; the tests drive it directly).</summary>
        public struct Schedule
        {
            long _forcedSeen;
            int _forcedBar;
            bool _primed, _forcedLive;

            /// <summary>One tick: true when a moment fires now. <paramref name="forcedSequence"/> is
            /// <see cref="ForcedSequence"/>; a request made before the first step (a stale one) is ignored. Disabled
            /// (<c>StageMoments</c> off) nothing fires and a pending request is dropped. Only a FORCED moment holds the next
            /// scheduled one back: a scheduled moment re-crossed after a backward seek turns again, and a seek back before
            /// the forced bar forgets it.</summary>
            public bool Step(int bar, bool downbeatEdge, bool enabled, long forcedSequence)
            {
                if (!_primed) { _primed = true; _forcedSeen = forcedSequence; }
                bool forced = forcedSequence != _forcedSeen;
                _forcedSeen = forcedSequence;
                if (_forcedLive && bar < _forcedBar) _forcedLive = false;        // rewound past the forced moment
                if (!enabled) return false;
                if (forced) { _forcedBar = bar; _forcedLive = true; return true; }
                if (!Scheduled(bar, downbeatEdge)) return false;
                if (_forcedLive && bar - _forcedBar < Bars) return false;        // a forced moment just turned it
                _forcedLive = false;
                return true;
            }

            /// <summary>A new track: the next scheduled moment is not held back by the last track's.</summary>
            public void Reset() => _forcedLive = false;
        }
    }
}
