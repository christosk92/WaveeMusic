// ── Screens/EvidenceAuto.cs — the always-on AUTO evidence bundle on the first coverage clamp of a scroll burst ─────────
//
// 2026-09-25, item G (the Q-top 1500 playlist goes blank on a fast thumb drag / touchpad scroll in the vertical
// detail-row layout). A coverage clamp — the composite viewport reaching past the rows the virtualizer realized
// (`TileCensus.CoverageClamps`, SliceRecorder: "a band no tile can hold") — is the recorded shape of the blank, but it
// lasts a few frames and the probe's trace ring rolls long before anybody exports it. So the FIRST clamped turn of a
// burst arms one evidence bundle (the next composited present, its items / placements / census / scroll.csv / vps.tsv
// with coverage) plus a column of pixel queries down every clamped viewport, and writes one always-on
// `[evidence] auto clamp` line naming the viewport, its shown window and its coverage. Rate-limited: one per burst (a
// burst ends when no viewport is in user-driven motion) and never within `MinIntervalMs` of the last. Evidence only —
// nothing here changes a pose, a plan or a frame.
//
// The decision is `ClampCaptureRules` (pure, tested); the shell reads the census and the viewports on the UI thread in
// the frame relay and allocates nothing on a frame that does not capture.

using System.Globalization;
using FluentGpu.Hosting;

namespace Wavee;

/// <summary>When the auto clamp bundle fires — pure (<c>ClampCaptureRulesTests</c>).</summary>
public static class ClampCaptureRules
{
    /// <summary>Never two auto bundles closer than this (a bundle costs a frame capture and a disk write).</summary>
    public const double MinIntervalMs = 60_000;

    /// <summary>At most this many auto bundles per session: each clamp is a defect worth one capture, not a disk fill.</summary>
    public const int MaxPerSession = 6;

    /// <summary>Never two auto bundles of ANY cause closer than this (2026-10-06: a bundle is a frame capture plus a
    /// 10-file export; 5-6 of them per run at ~14 s spacing were measurable hitches).</summary>
    public const double AnyMinIntervalMs = 30_000;

    /// <summary>One frame's step: capture when ARMED, the turn clamped, and the last capture is old enough; a capture
    /// disarms, and the end of the burst (no user-driven scroll this frame) re-arms.</summary>
    public static bool Step(ref bool armed, int clampsThisTurn, bool scrollActive, double msSinceLastCapture,
        double msSinceAnyAutoCapture = double.MaxValue)
    {
        if (!scrollActive && clampsThisTurn == 0) { armed = true; return false; }
        if (!armed || clampsThisTurn <= 0 || msSinceLastCapture < MinIntervalMs || msSinceAnyAutoCapture < AnyMinIntervalMs) return false;
        armed = false;
        return true;
    }

    /// <summary>Is a virtual viewport's shown window [offset, offset + viewport) past its realized coverage? The
    /// viewport(s) the auto line names. A plain scroller (no items) never is; the window's end is capped at the content
    /// extent (a shelf shorter than its viewport shows its whole content, which is covered).</summary>
    public static bool ShowsPastCoverage(double offset, double viewport, double extent, double coverStart, double coverEnd, int items)
        => items > 0 && viewport > 0
        && (offset < coverStart - 0.5 || Math.Min(offset + viewport, extent) > coverEnd + 0.5);
}

/// <summary>When the auto JUMP bundle fires (2026-09-25, item J — the Library V3 rail jumping on selection) — pure
/// (<c>JumpCaptureRulesTests</c>). The engine's always-on detector counts every unrequested jump of a viewport at rest
/// (<c>ScrollProbe.Jumps</c>, and writes its own <c>[scroll.jump]</c> line); the first new jump captures one bundle, never
/// two within <see cref="MinIntervalMs"/>, at most <see cref="MaxPerSession"/> a session.</summary>
public static class JumpCaptureRules
{
    public const double MinIntervalMs = 120_000;
    public const int MaxPerSession = 4;

    /// <summary>A jump that is the expected result of async content arriving, not an anomaly worth a bundle: an
    /// <c>Extent</c> jump of a viewport sitting at the TOP (shown offset 0 before and after — the offset never moved, the
    /// content just grew, e.g. the lyrics document arriving into the rail). The detector still counts and logs it.</summary>
    public static bool IsBenign(FluentGpu.Scroll.Diag.ScrollJumpCause cause, double from, double to)
        => cause == FluentGpu.Scroll.Diag.ScrollJumpCause.Extent && Math.Abs(from) <= 0.5 && Math.Abs(to) <= 0.5;

    /// <summary>One frame's step over the monotonic jump counter: capture when it moved since the last frame, the
    /// session cap is not reached and the last capture is old enough. <paramref name="seen"/> starts at −1 (the first
    /// frame only takes the baseline — jumps from before the watch started are not this session's evidence).</summary>
    public static bool Step(ref long seen, long jumps, double msSinceLastCapture, int captures,
        double msSinceAnyAutoCapture = double.MaxValue)
    {
        long before = seen;
        seen = jumps;
        if (before < 0 || jumps <= before) return false;
        return captures < MaxPerSession && msSinceLastCapture >= MinIntervalMs && msSinceAnyAutoCapture >= ClampCaptureRules.AnyMinIntervalMs;
    }
}

public static partial class Diagnostics
{
    /// <summary>The shell half of <see cref="ClampCaptureRules"/>: the frame relay's per-frame check. UI THREAD.</summary>
    public static class EvidenceAuto
    {
        static bool s_armed = true;
        static long s_lastCaptureTicks;
        static int s_captures;
        static long s_seenClampedPoses = -1;
        static readonly List<ViewportInfo> s_vps = new(16);

        static long s_lastAnyTicks;
        static double SinceAny() => s_lastAnyTicks == 0 ? double.MaxValue
            : System.Diagnostics.Stopwatch.GetElapsedTime(s_lastAnyTicks).TotalMilliseconds;

        static long s_seenJumps = -1;
        static long s_lastJumpCaptureTicks;
        static int s_jumpCaptures;

        /// <summary>Called from the frame watch (<c>Diagnostics.Host</c>) once per rendered frame.</summary>
        public static void OnFrame(in FrameStats stats)
        {
            var host = Probe.Host;
            if (host is null) return;
            OnFrameJump(host);
            if (s_captures >= ClampCaptureRules.MaxPerSession) return;
            // Two clamp signals: the render poser's clamped POSES (the burst summary's `clamps=`, always-on counter) and
            // the composite's tile-level coverage clamps (a band past the realized rows). Either starts a capture.
            long poses = FluentGpu.Scroll.Diag.ScrollProbe.ClampedPoses;
            int poseClamps = s_seenClampedPoses < 0 ? 0 : (int)Math.Min(int.MaxValue, poses - s_seenClampedPoses);
            s_seenClampedPoses = poses;
            int clamps = host.LastTileCensus.CoverageClamps + poseClamps;
            double since = s_lastCaptureTicks == 0 ? double.MaxValue
                : System.Diagnostics.Stopwatch.GetElapsedTime(s_lastCaptureTicks).TotalMilliseconds;
            if (!ClampCaptureRules.Step(ref s_armed, clamps, stats.ScrollActive, since, SinceAny())) return;
            s_lastCaptureTicks = s_lastAnyTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            s_captures++;
            Capture(host, clamps);
        }

        /// <summary>Item J: one bundle on a new unrequested scroll jump (the engine's detector, <c>ScrollProbe.Jumps</c>).
        /// Allocates nothing on a frame that does not capture.</summary>
        static void OnFrameJump(AppHost host)
        {
            double since = s_lastJumpCaptureTicks == 0 ? double.MaxValue
                : System.Diagnostics.Stopwatch.GetElapsedTime(s_lastJumpCaptureTicks).TotalMilliseconds;
            // The detector's last jump is read BEFORE the step: a benign one (async content growing a viewport at the top)
            // is logged below but never takes a capture slot.
            var j = FluentGpu.Scroll.Diag.ScrollProbe.LastJump;
            long jumps = FluentGpu.Scroll.Diag.ScrollProbe.Jumps;
            bool benign = JumpCaptureRules.IsBenign(j.Cause, j.From, j.To);
            if (benign)
            {
                if (s_seenJumps >= 0 && jumps > s_seenJumps)
                    Log.Info("evidence", "[evidence] auto jump skipped (benign: extent growth at top) node=" + j.Vp.ToString(CultureInfo.InvariantCulture));
                s_seenJumps = jumps;
                return;
            }
            if (!JumpCaptureRules.Step(ref s_seenJumps, jumps, since, s_jumpCaptures, SinceAny())) return;
            s_lastJumpCaptureTicks = s_lastAnyTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            s_jumpCaptures++;
            var inv = CultureInfo.InvariantCulture;
            s_vps.Clear();
            host.CopyViewports(s_vps);
            string key = "-";
            foreach (var v in s_vps) if (v.NodeIndex == j.Vp) { key = v.ScrollKey ?? "-"; break; }
            Log.Info("evidence", "[evidence] auto jump vp=" + key + " node=" + j.Vp.ToString(inv)
                + " from=" + j.From.ToString("0.#", inv) + " to=" + j.To.ToString("0.#", inv)
                + " cause=" + FluentGpu.Scroll.Diag.ScrollJumpRules.CauseName(j.Cause)
                + " atMs=" + (j.Qpc * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0", inv));
            Evidence.RequestBundle("jump-auto");
        }

        static void Capture(AppHost host, int clamps)
        {
            s_vps.Clear();
            host.CopyViewports(s_vps);
            var inv = CultureInfo.InvariantCulture;
            var named = new System.Text.StringBuilder(128);
            foreach (var v in s_vps)
            {
                if (!ClampCaptureRules.ShowsPastCoverage(v.Offset, v.Viewport, v.Extent, v.CoverStart, v.CoverEnd, v.ItemCount)) continue;
                named.Append(" vp=").Append(v.ScrollKey ?? "-").Append(" offset=").Append(v.Offset.ToString("0.#", inv))
                     .Append(" window=").Append(v.Viewport.ToString("0.#", inv))
                     .Append(" cover=[").Append(v.CoverStart.ToString("0.#", inv)).Append(',').Append(v.CoverEnd.ToString("0.#", inv))
                     .Append("] realized=[").Append(v.FirstRealized.ToString(inv)).Append(',').Append(v.LastRealized.ToString(inv))
                     .Append(") items=").Append(v.ItemCount.ToString(inv)).Append(" prefix=").Append(v.PersistentPrefix.ToString(inv))
                     .Append(" origin=").Append(v.WindowOrigin.ToString("0.#", inv));
                // A column of pixel queries down the viewport's centre: what composited where the rows should be.
                if (v.Horizontal || v.W <= 0f || v.H <= 0f) continue;
                int x = (int)(v.X + v.W * 0.5f);
                for (int k = 1; k <= 8; k++)
                    Evidence.QueryPixel(x, (int)(v.Y + v.H * k / 9f), dip: true, writeNow: false, out _);
            }
            var last = FluentGpu.Scroll.Diag.ScrollProbe.LastClamp;
            Log.Info("evidence", "[evidence] auto clamp clamps=" + clamps.ToString(inv)
                + " lastPose(vp=" + last.Vp.ToString(inv) + " shown=" + last.Shown.ToString("0.#", inv) + " plan=" + last.Plan.ToString("0.#", inv)
                + " atMs=" + (last.PresentQpc * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0", inv) + ")"
                + (named.Length > 0 ? named.ToString() : " vp=(none past coverage now)"));
            Evidence.RequestBundle("clamp-auto");
        }
    }
}
