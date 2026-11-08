// ── Playback/Playback.Video.Timeline.cs ────────────────────────────────────────────────────────────────────────────
// The video open path on one clock: a `[vtime]` log event at every stage the existing `[video]` lines leave untimed (the
// click's commit, the resolve's lane wait and run, the hop to the UI, the worker's dequeue, the adopt wait, each UI round
// trip of a rebuild or teardown, the first frame as the host observes it, the cut). Every event carries `since` (ms from the
// last Reset: a probe step, a click, a placement commit) and the thread, so `tools/video-probe-report.py` can lay a
// click-to-picture waterfall over the `[video]` budgets and the probe's UI stall meter.
//
// Off the hot path: marks fire per load/switch, never per frame.

using System.Diagnostics;
using System.Globalization;

namespace Wavee;

public static partial class Playback
{
    public static partial class Video
    {
        public static class Timeline
        {
            static long s_resetTs = Stopwatch.GetTimestamp();
            static int s_firstFrame;

            /// <summary>Start a new waterfall (a probe step, a click on a video verb, a placement commit).</summary>
            public static void Reset(string label)
            {
                Volatile.Write(ref s_resetTs, Stopwatch.GetTimestamp());
                Volatile.Write(ref s_firstFrame, 0);
                Mark("reset", label);
            }

            /// <summary>A reset from the app's own edge (a placement commit) unless a probe step reset the clock in the last
            /// <paramref name="withinMs"/>: a probe's click and the commit it causes stay one waterfall, timed from the click.</summary>
            public static void ResetUnlessRecent(string label, long withinMs)
            {
                if (SinceResetMs < withinMs) { Mark("edge", label); return; }
                Reset(label);
            }

            /// <summary>The host observed a first presented frame since the last <see cref="Reset"/>.</summary>
            public static bool FirstFrameSeen => Volatile.Read(ref s_firstFrame) != 0;

            public static long SinceResetMs => (long)((Stopwatch.GetTimestamp() - Volatile.Read(ref s_resetTs)) * 1000.0 / Stopwatch.Frequency);

            internal static void NoteFirstFrame(string detail)
            {
                Volatile.Write(ref s_firstFrame, 1);
                Mark("first.frame", detail);
            }

            /// <summary>One `[vtime]` event: the stage, ms since the last reset, the thread (ui or its managed id), and a detail.</summary>
            public static void Mark(string stage, string detail = "")
            {
                bool ui = Platform.UiThreadId is { } id && id == Environment.CurrentManagedThreadId;
                Log.Event(WaveeLogLevel.Info, "vtime", stage, detail, null, SinceResetMs, null,
                    WaveeLogField.Of("since", SinceResetMs),
                    WaveeLogField.Of("thread", ui ? "ui" : Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)));
            }

            /// <summary>A mark for an elapsed span measured by the caller (a lock wait, a UI hop): <paramref name="ms"/> rides as `ms`.</summary>
            public static void Span(string stage, double ms, string detail = "")
            {
                bool ui = Platform.UiThreadId is { } id && id == Environment.CurrentManagedThreadId;
                Log.Event(WaveeLogLevel.Info, "vtime", stage, detail, null, SinceResetMs, null,
                    WaveeLogField.Of("since", SinceResetMs),
                    WaveeLogField.Of("ms", ms.ToString("0.0", CultureInfo.InvariantCulture)),
                    WaveeLogField.Of("thread", ui ? "ui" : Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)));
            }

            internal static double MsSince(long ts) => (Stopwatch.GetTimestamp() - ts) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
