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
    public const double MinIntervalMs = 5000;

    /// <summary>At most this many auto bundles per session: each clamp is a defect worth one capture, not a disk fill.</summary>
    public const int MaxPerSession = 12;

    /// <summary>One frame's step: capture when ARMED, the turn clamped, and the last capture is old enough; a capture
    /// disarms, and the end of the burst (no user-driven scroll this frame) re-arms.</summary>
    public static bool Step(ref bool armed, int clampsThisTurn, bool scrollActive, double msSinceLastCapture)
    {
        if (!scrollActive && clampsThisTurn == 0) { armed = true; return false; }
        if (!armed || clampsThisTurn <= 0 || msSinceLastCapture < MinIntervalMs) return false;
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

        /// <summary>Called from the frame watch (<c>Diagnostics.Host</c>) once per rendered frame.</summary>
        public static void OnFrame(in FrameStats stats)
        {
            var host = Probe.Host;
            if (host is null || s_captures >= ClampCaptureRules.MaxPerSession) return;
            // Two clamp signals: the render poser's clamped POSES (the burst summary's `clamps=`, always-on counter) and
            // the composite's tile-level coverage clamps (a band past the realized rows). Either starts a capture.
            long poses = FluentGpu.Scroll.Diag.ScrollProbe.ClampedPoses;
            int poseClamps = s_seenClampedPoses < 0 ? 0 : (int)Math.Min(int.MaxValue, poses - s_seenClampedPoses);
            s_seenClampedPoses = poses;
            int clamps = host.LastTileCensus.CoverageClamps + poseClamps;
            double since = s_lastCaptureTicks == 0 ? double.MaxValue
                : System.Diagnostics.Stopwatch.GetElapsedTime(s_lastCaptureTicks).TotalMilliseconds;
            if (!ClampCaptureRules.Step(ref s_armed, clamps, stats.ScrollActive, since)) return;
            s_lastCaptureTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            s_captures++;
            Capture(host, clamps);
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
