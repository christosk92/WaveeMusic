// ── Screens/Diagnostics.ScrollTrace.cs — the per-frame scroll trace and its verdict ─────────────────────────────────
//
// WHY THIS EXISTS. "Scrolling still sometimes feels bad, I cannot explain it" (2026-09-16). The rollup that existed,
// `scroll.frames`, could not answer that: every CPU frame was under 9 ms, and its one cadence figure, missedVblanks,
// counts every refresh interval between two presents — so the idle gap after a wheel glide LANDS and before the next
// notch arrives reads as "missed" exactly like a real hitch does. The screen-capture probe that diagnosed the original
// stepping (scratchpad ScrollProbe, 2026-09-16) measured what the user sees — the per-frame shift of the list — but it
// needs synthetic input or the user's cooperation and a capture rectangle that actually shows the list.
//
// This file reproduces the probe's trace FROM INSIDE, always on: the engine now surfaces, per frame, whether the kernel
// had live motion (`FrameStats.ScrollLiveMotion`), the largest displacement any body produced (`ScrollDeltaDip`) and the
// wheel notches applied (`WheelNotches`); the frame watch keeps the last `Capacity` frames of a burst and, when the
// burst ends, logs one `scroll.trace` line: the verdict figures below plus the shift sequence itself, so a bad-feeling
// scroll can be read back frame by frame from the log the user already has.
//
// The figures (all over LIVE frames — frames the kernel was moving something):
//   liveMissed  vblanks with no present between two LIVE frames: a frame the user saw held for 2+ refreshes while the
//               list was moving. The gap after a landing (the next frame is idle) is the glide having ended, not a hitch.
//   liveZero    a live frame that moved nothing (|Δ| < ZeroDip) and was not the landing frame: a stall inside motion.
//   dips        a frame whose shift fell under DipFraction of the median of the three before it and recovered on the
//               next — the "eager to jump up and down" feel; a landing tail is excluded because it does not recover.
//   late        a live frame whose completion came later than LateFraction of a refresh after the previous one.
//   jitter      the median relative step change between consecutive live shifts (notch frames excluded): 0 is a
//               perfectly even glide, 0.15 is the eye's threshold on a 120 Hz panel in the probe runs.
//   notches / notchGapMs   the wheel cadence the user actually drove, so a verdict can separate device from engine.
//   slackFrames  frames the engine reports as slept/pre-empted/GC-paused for more than `SlackMs` (`FrameStats.SlackMs`).
//   holdFrames  zero frames inside a drag while the contact was down but produced no delta (`ScrollContactHeld`), in runs
//               of HoldRun or more: a finger resting on the surface, not a stall. A shorter held run that recovers is a stall.
//   pins        frames a body was pinned at a clamp (`ScrollEdgePins`).
//   structural  frames the kernel rebased a body (anchor shift / clamp rebase, `ScrollStructuralDip`) by ZeroDip or more.
//               A rebase is not motion, so the frame is left out of the stall, dip and jitter figures.
//
// The missed-vblank figure is one frame late at the source: the engine adds a frame's missed vblanks in `NotePresented`
// AFTER it raised `FrameCompleted` for that frame, so the counter delta the frame watch reads at frame N+1 is frame N's
// present gap. `ScrollTraceRing.Add` moves it back to frame N and drops the delta that belongs to the burst's first frame
// (the idle gap before the burst — the previous log read that as "held while moving" on the burst's second frame).
//
// Pure: no engine, no clock, no logger. The frame watch feeds it `Sample`s; the tests feed it arrays.

using System;
using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>One frame of a scroll burst as the frame watch recorded it.</summary>
    /// <param name="DtMs">Milliseconds since the previous completed frame (any frame, live or not).</param>
    /// <param name="DeltaDip">The largest displacement a scroll body produced this frame, DIP.</param>
    /// <param name="Missed">Vblanks with no present between the previous frame's present and this frame's present. The
    /// frame watch hands the ring the counter delta it read this frame; the ring moves it to the frame it belongs to.</param>
    /// <param name="Notches">Wheel notches the kernel applied this frame.</param>
    /// <param name="Live">The kernel reported continuous motion this frame.</param>
    /// <param name="SlackMs">Time this frame the engine reports as not its own (GC pause, a wake that slept, pre-emption).</param>
    /// <param name="ContactHeld">A drag contact is down but produced no delta this frame (a finger resting).</param>
    /// <param name="EdgePins">Bodies pinned at a clamp this frame.</param>
    /// <param name="StructuralDip">The largest anchor-shift / clamp rebase applied this frame, DIP. Not motion.</param>
    public readonly record struct ScrollTraceSample(float DtMs, float DeltaDip, int Missed, int Notches, bool Live, float SlackMs = 0f,
                                                    bool ContactHeld = false, int EdgePins = 0, float StructuralDip = 0f,
                                                    byte ZeroReason = 0, bool DtRepaired = false);

    /// <summary>The verdict figures over one burst (see the file header).</summary>
    public readonly record struct ScrollTraceVerdict(int Frames, int LiveFrames, int LiveMissed, int LiveZero, int Dips, int Late,
                                                     float Jitter, int Notches, float NotchGapMs, float DtP95Ms, float DtMaxMs,
                                                     int SlackFrames = 0, int HoldFrames = 0, int Pins = 0, int Structural = 0,
                                                     int ZeroDtZero = 0, int ZeroNotAdvanced = 0, int ZeroPinned = 0, int ZeroOther = 0, int DtRepairs = 0)
    {
        /// <summary>A one-word reading for the log line: the dominant defect, or "smooth".</summary>
        public string Grade
            => LiveFrames < ScrollTraceRules.MinLiveFrames ? "short"
             : LiveMissed > 0 ? "present-hitch"
             : LiveZero > 0 ? "stalled-frames"
             : Dips > 0 ? "velocity-dips"
             : Late > 0 ? "late-frames"
             : Jitter > ScrollTraceRules.JitterSmooth ? "uneven"
             : "smooth";
    }

    public static class ScrollTraceRules
    {
        /// <summary>Frames kept per burst: 400 at 120 Hz is the last 3.3 s, which is the part the user is reacting to.</summary>
        public const int Capacity = 400;
        /// <summary>A live frame that shifted less than this is "moved nothing" (the probe's own threshold was ⅓ px).</summary>
        public const float ZeroDip = 0.25f;
        /// <summary>A shift under this fraction of the trailing median that recovers next frame is a dip (the wheel
        /// plan's acceptance was "never below 0.55× the steady median").</summary>
        public const float DipFraction = 0.55f;
        /// <summary>A frame completing later than this many refresh intervals after the previous one is late.</summary>
        public const float LateFraction = 1.5f;
        /// <summary>Median relative step change under which a glide reads as even.</summary>
        public const float JitterSmooth = 0.15f;
        /// <summary>Fewer live frames than this is a nudge, not a scroll — no verdict.</summary>
        public const int MinLiveFrames = 8;
        /// <summary>A frame whose engine-reported slack exceeds this is a slack frame (and gets its own log line while scrolling).</summary>
        public const float SlackMs = 12f;
        /// <summary>Consecutive held zero frames from which a run reads as a resting finger rather than a stall.</summary>
        public const int HoldRun = 3;

        public static ScrollTraceVerdict Analyse(ReadOnlySpan<ScrollTraceSample> s, float refreshMs)
        {
            int live = 0, liveMissed = 0, liveZero = 0, dips = 0, late = 0, notches = 0, slackFrames = 0;
            int holdFrames = 0, pins = 0, structural = 0, heldRun = 0;
            int zDt = 0, zNot = 0, zPin = 0, zOther = 0, dtRepairs = 0;
            Span<float> jitterScratch = s.Length <= 512 ? stackalloc float[s.Length] : new float[s.Length];
            Span<float> dtScratch = s.Length <= 512 ? stackalloc float[s.Length] : new float[s.Length];
            Span<float> gapScratch = s.Length <= 512 ? stackalloc float[s.Length] : new float[s.Length];
            int jitterN = 0, dtN = 0, gapN = 0;
            float sinceNotch = 0f; bool sawNotch = false;
            float lateAfter = refreshMs > 0f ? refreshMs * LateFraction : float.MaxValue;

            for (int i = 0; i < s.Length; i++)
            {
                var f = s[i];
                bool prevLive = i > 0 && s[i - 1].Live;
                bool isStructural = IsStructural(in f);
                if (f.SlackMs > SlackMs) slackFrames++;
                if (f.EdgePins > 0) pins++;
                if (isStructural) structural++;
                if (f.DtRepaired) dtRepairs++;
                switch (f.ZeroReason)   // FluentGpu.Scroll.ScrollZeroReason: 1 DtZero, 2 NotAdvanced, 3 Pinned, 4 Parked, 5 Other
                {
                    case 1: zDt++; break;
                    case 2: zNot++; break;
                    case 3: zPin++; break;
                    case 4: case 5: zOther++; break;
                }
                if (f.Notches > 0)
                {
                    notches += f.Notches;
                    // The gap closes on the notch frame itself, so its own dt belongs to the gap it ends.
                    if (sawNotch) { gapScratch[gapN++] = sinceNotch + f.DtMs; }
                    sawNotch = true; sinceNotch = 0f;
                }
                else if (sawNotch) sinceNotch += f.DtMs;

                bool nextLive = i + 1 < s.Length && s[i + 1].Live;
                float a = MathF.Abs(f.DeltaDip);
                // A zero frame inside motion. A held one (contact down, no delta) joins a run; the run is judged when it
                // ends: HoldRun or more frames is a finger resting (holdFrames), fewer is a stall that happened to
                // coincide with the contact. A structural frame moved the body by rebase, not motion: not a stall.
                bool zero = f.Live && a < ZeroDip && nextLive && f.Notches == 0 && !isStructural;
                if (zero && f.ContactHeld) heldRun++;
                else
                {
                    if (heldRun >= HoldRun) holdFrames += heldRun; else liveZero += heldRun;
                    heldRun = 0;
                    if (zero) liveZero++;
                }

                if (!f.Live) continue;
                // Held refreshes BETWEEN two live frames. Nothing is expected to present under a resting finger or
                // across a rebase, so those frames do not charge a hitch.
                if (prevLive && f.Missed > 0 && !f.ContactHeld && !isStructural) liveMissed += f.Missed;
                live++;
                dtScratch[dtN++] = f.DtMs;
                if (prevLive && f.DtMs > lateAfter) late++;

                if (prevLive && f.Notches == 0 && i > 0 && !isStructural && !IsStructural(in s[i - 1]))
                {
                    float b = MathF.Abs(s[i - 1].DeltaDip);
                    float hi = MathF.Max(a, b);
                    if (a >= 0.5f && b >= 0.5f) jitterScratch[jitterN++] = MathF.Abs(a - b) / hi;
                }
                // A dip: under DipFraction of the trailing three-frame median, recovering on the next frame (a landing
                // tail keeps falling and is not counted). Notch frames and the frame after one are excluded — a
                // reversal legitimately brakes through zero there. Structural frames and their neighbours are excluded
                // too: a rebase is a jump the median must not see.
                if (i >= 3 && nextLive && f.Notches == 0 && s[i - 1].Notches == 0 && s[i - 1].Live && s[i - 2].Live && s[i - 3].Live
                    && !isStructural && !IsStructural(in s[i - 1]) && !IsStructural(in s[i + 1]))
                {
                    float m = Median3(MathF.Abs(s[i - 1].DeltaDip), MathF.Abs(s[i - 2].DeltaDip), MathF.Abs(s[i - 3].DeltaDip));
                    if (m >= 1f && a < DipFraction * m && MathF.Abs(s[i + 1].DeltaDip) > a * 1.2f) dips++;
                }
            }

            if (heldRun >= HoldRun) holdFrames += heldRun; else liveZero += heldRun;

            float jitter = jitterN > 0 ? Median(jitterScratch[..jitterN]) : 0f;
            float p95 = dtN > 0 ? Percentile(dtScratch[..dtN], 0.95f) : 0f;
            float max = 0f;
            for (int i = 0; i < dtN; i++) if (dtScratch[i] > max) max = dtScratch[i];
            float gap = gapN > 0 ? Median(gapScratch[..gapN]) : 0f;
            return new ScrollTraceVerdict(s.Length, live, liveMissed, liveZero, dips, late, jitter, notches, gap, p95, max, slackFrames,
                                          holdFrames, pins, structural, zDt, zNot, zPin, zOther, dtRepairs);
        }

        static bool IsStructural(in ScrollTraceSample f) => MathF.Abs(f.StructuralDip) >= ZeroDip;

        /// <summary>The shift sequence, one entry per frame: the signed shift with one decimal, then the markers
        /// <c>*</c> (a notch applied this frame), <c>!</c> (a vblank passed with no present before it) and <c>~</c>
        /// (completed later than <see cref="LateFraction"/> refreshes after the previous frame), then <c>p</c> (a body pinned
        /// at a clamp) and <c>s</c> (a structural rebase of <see cref="ZeroDip"/> or more). A frame the kernel did not
        /// move is written as <c>.</c> so an idle gap between two glides stays visible without a number.</summary>
        public static string Format(ReadOnlySpan<ScrollTraceSample> s, float refreshMs)
        {
            var sb = new StringBuilder(s.Length * 7);
            float lateAfter = refreshMs > 0f ? refreshMs * LateFraction : float.MaxValue;
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                var f = s[i];
                if (!f.Live && MathF.Abs(f.DeltaDip) < ZeroDip) sb.Append('.');
                else sb.Append(f.DeltaDip.ToString("0.0", CultureInfo.InvariantCulture));
                if (f.Notches > 0) sb.Append('*');
                if (f.Missed > 0) sb.Append('!');
                if (i > 0 && f.DtMs > lateAfter) sb.Append('~');
                if (f.EdgePins > 0) sb.Append('p');
                if (IsStructural(in f)) sb.Append('s');
                if (f.ZeroReason != 0) sb.Append('z');
                if (f.DtRepaired) sb.Append('r');
            }
            return sb.ToString();
        }

        /// <summary>The log line's key=value half.</summary>
        public static string Describe(in ScrollTraceVerdict v)
            => "verdict=" + v.Grade + " frames=" + v.Frames + " live=" + v.LiveFrames + " liveMissed=" + v.LiveMissed
             + " liveZero=" + v.LiveZero + " dips=" + v.Dips + " late=" + v.Late
             + " jitter=" + v.Jitter.ToString("0.00", CultureInfo.InvariantCulture)
             + " notches=" + v.Notches + " notchGapMs=" + v.NotchGapMs.ToString("0", CultureInfo.InvariantCulture)
             + " dtP95=" + v.DtP95Ms.ToString("0.0", CultureInfo.InvariantCulture)
             + " dtMax=" + v.DtMaxMs.ToString("0.0", CultureInfo.InvariantCulture)
             + " slackFrames=" + v.SlackFrames
             + " holdFrames=" + v.HoldFrames + " pins=" + v.Pins + " structural=" + v.Structural
             + " zero=dt:" + v.ZeroDtZero + "/skip:" + v.ZeroNotAdvanced + "/pin:" + v.ZeroPinned + "/other:" + v.ZeroOther
             + " dtRepairs=" + v.DtRepairs;

        static float Median3(float a, float b, float c)
            => MathF.Max(MathF.Min(a, b), MathF.Min(MathF.Max(a, b), c));

        static float Median(Span<float> v)
        {
            v.Sort();
            int n = v.Length;
            return n == 0 ? 0f : (n & 1) == 1 ? v[n / 2] : 0.5f * (v[n / 2 - 1] + v[n / 2]);
        }

        static float Percentile(Span<float> v, float p)
        {
            v.Sort();
            if (v.Length == 0) return 0f;
            int idx = Math.Clamp((int)MathF.Ceiling(p * v.Length) - 1, 0, v.Length - 1);
            return v[idx];
        }
    }

    /// <summary>The burst ring the frame watch fills: the last <see cref="ScrollTraceRules.Capacity"/> frames, oldest
    /// first when read back. Zero allocation per frame; one array for the life of the process.</summary>
    public sealed class ScrollTraceRing
    {
        readonly ScrollTraceSample[] _ring = new ScrollTraceSample[ScrollTraceRules.Capacity];
        readonly ScrollTraceSample[] _linear = new ScrollTraceSample[ScrollTraceRules.Capacity];
        int _head, _count;
        long _added;

        public int Count => _count;
        public void Clear() { _head = 0; _count = 0; _added = 0; }

        /// <summary>Add a frame. <paramref name="s"/>.Missed is the present counter's delta AS READ this frame; the engine
        /// adds a frame's missed vblanks after it raises the frame event, so that delta is the PREVIOUS frame's present gap
        /// and is written there. The frame's own figure stays 0 until the next Add (the burst's last frame keeps 0). The
        /// delta read on the burst's second frame is the first frame's present against the last idle present — the
        /// pre-burst idle — and is dropped.</summary>
        public void Add(in ScrollTraceSample s)
        {
            if (_added > 1)
            {
                int prev = (_head - 1 + _ring.Length) % _ring.Length;
                _ring[prev] = _ring[prev] with { Missed = s.Missed };
            }
            _ring[_head] = s with { Missed = 0 };
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
            _added++;
        }

        /// <summary>The samples oldest → newest, in a reused buffer (valid until the next call).</summary>
        public ReadOnlySpan<ScrollTraceSample> Samples()
        {
            int start = _count < _ring.Length ? 0 : _head;
            for (int i = 0; i < _count; i++) _linear[i] = _ring[(start + i) % _ring.Length];
            return _linear.AsSpan(0, _count);
        }
    }
}
