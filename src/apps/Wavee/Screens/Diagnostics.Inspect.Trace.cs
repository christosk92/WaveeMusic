// ── Screens/Diagnostics.Inspect.Trace.cs ───────────────────────────────────────────────────────────────────────────────
// the `trace` verb: run N frames on the inspect host and report one row per frame (engine timing, relayout counts, watched
// nodes, one scroller) plus a summary that names the snaps, jumps, relayouts and scroll reversals
//
// Role: SHELL (UI thread; reads the engine's public scene inspection and the per-frame FrameStats only)
// Spec: the "inspect trace" lane (page navigation jank + scroll jitter diagnosis); reference docs/guide/agent-inspect.md.
//
// TWO HALVES. `InspectTrace` (this file, top) is PURE: the argument parse, the wheel schedule, the row/sample types, the
// summary detectors and the JSON writer. No engine, no host, no thread: Wavee.Tests/InspectTraceTests.cs pins the parse and
// every detector on hand-built rows. The `InspectSession` partial (bottom) is the UI-thread half that runs the frames and
// fills the rows from `AppHost.RunFrame()`'s FrameStats, the scene and the animation engine.
//
// THE CLOCK. The inspect host runs on a fixed fake frame clock (one 16 ms step per frame): frames are NOT real time. The
// millisecond fields are the CPU cost of each frame while the motion plays on that clock.

using System.Diagnostics;
using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>The pure half of `trace`: options, wheel schedule, sample types, summary detectors, JSON.</summary>
    public static class InspectTrace
    {
        public const int MaxFrames = 5000;
        public const int MaxWatch = 500;
        public const double Epsilon = 0.01;

        // ── the options ─────────────────────────────────────────────────────────────────────────────────────────────

        public sealed class Options
        {
            public int Frames = 60;
            /// <summary>Emit every Nth row (the summary always covers every frame).</summary>
            public int Every = 1;
            public List<string> Keys = [];
            public List<string> Prefixes = [];
            /// <summary>"" (none), "main" (the shown vertical scroller with the most room) or a node key.</summary>
            public string Scroll = "";
            public int MaxNodes = 20;
            /// <summary>A presented move over this many DIP in one frame while a track is active is a jump.</summary>
            public float JumpDip = 2f;
            /// <summary>A change over this many DIP in one frame with no track is a snap.</summary>
            public float SnapDip = 0.5f;
            /// <summary>A tracked move is INSTANT when one frame carries at least this many DIP and most of its run (<see cref="InstantShare"/>):
            /// the change landed in a frame while something above it animated (a page reflowing at once beside a sliding pane).</summary>
            public float InstantDip = 4f;
            public bool Rows = true;
            /// <summary>Turn the host's render census on: a slow frame's row then names the components that made it slow (`census`). Off by default (it costs frame time).</summary>
            public bool Census;
            /// <summary>Wheel notches (dy), cycled per event: "3" or "3,3,-6". Empty = no input.</summary>
            public double[] Wheel = [];
            public int WheelEvery = 1;
            public int WheelCount;
            public string WheelKey = "";
            public float WheelX = float.NaN, WheelY = float.NaN;
            /// <summary>A touchpad/touch pan (a contact stream, the only input with rubber band and fling): DIP per frame while the
            /// contact is down, cycled; empty = none. The contact opens at <see cref="PanStart"/>, lasts <see cref="PanFrames"/>
            /// frames, then lifts (the release authors the fling or the edge spring).</summary>
            public double[] Pan = [];
            public int PanFrames = 8, PanStart, PanEvery, PanCount = 1;
            public bool PanTouch;
            /// <summary>A mouse drag (a splitter, a slider, a drag source): DIP per frame along x while the button is down, cycled; empty = none.
            /// The button goes down at <see cref="DragStart"/> on <see cref="DragKey"/>'s centre (or <see cref="DragX"/>, <see cref="DragY"/>), the
            /// pointer moves for <see cref="DragFrames"/> frames (<see cref="DragDy"/> DIP per frame down), then the button comes up.</summary>
            public double[] Drag = [];
            public int DragFrames = 8, DragStart;
            public float DragDy, DragX = float.NaN, DragY = float.NaN;
            public string DragKey = "";
            public bool WatchesNodes => Keys.Count > 0 || Prefixes.Count > 0;
        }

        /// <summary>The trace options an args bag spells: <c>frames</c> (or <paramref name="frames"/> from an action's
        /// <c>trace=N</c>), <c>keys</c>, <c>prefix</c>, <c>scroll</c>, <c>every</c>, <c>max</c> (watched nodes), <c>jump</c>, <c>snap</c>,
        /// <c>rows</c>, and the wheel schedule <c>wheel</c>, <c>wheelEvery</c>, <c>wheelCount</c>, <c>wheelKey</c>, <c>wheelX</c>, <c>wheelY</c>.</summary>
        public static bool TryParse(Inspect.Args a, int frames, string defaultScroll, out Options options, out string error)
        {
            error = "";
            options = new Options();
            var o = new Options();
            if (frames < 1 || frames > MaxFrames) { error = "trace wants 1 <= frames <= " + MaxFrames; return false; }
            o.Frames = frames;
            o.Every = Math.Max(1, a.Int("every", 1));
            o.Keys = a.List("keys");
            o.Prefixes = a.List("prefix");
            string scroll = a.Str("scroll", defaultScroll);
            o.Scroll = scroll is "none" or "off" or "false" ? "" : scroll;
            o.MaxNodes = Math.Clamp(a.Int("max", 20), 1, MaxWatch);
            o.JumpDip = a.Float("jump", 2f);
            o.SnapDip = a.Float("snap", 0.5f);
            o.InstantDip = a.Float("instant", 4f);
            o.Rows = a.Bool("rows", true);
            o.Census = a.Bool("census", false);
            if (a.Has("wheel"))
            {
                if (!TryWheelSequence(string.Join(",", a.List("wheel")), out o.Wheel)) { error = "wheel is a number or a comma list of numbers (notches, + toward the end)"; return false; }
                o.WheelEvery = Math.Max(1, a.Int("wheelEvery", 1));
                o.WheelCount = Math.Clamp(a.Int("wheelCount", 1), 0, 10_000);
                o.WheelKey = a.Str("wheelKey");
                o.WheelX = a.Float("wheelX", float.NaN);
                o.WheelY = a.Float("wheelY", float.NaN);
            }
            if (a.Has("pan"))
            {
                if (!TryWheelSequence(string.Join(",", a.List("pan")), out o.Pan)) { error = "pan is a number or a comma list of numbers (DIP per frame, + toward the end)"; return false; }
                o.PanFrames = Math.Clamp(a.Int("panFrames", 8), 1, 1000);
                o.PanStart = Math.Max(0, a.Int("panStart", 0));
                o.PanEvery = Math.Max(0, a.Int("panEvery", 0));
                o.PanCount = Math.Clamp(a.Int("panCount", 1), 1, 1000);
                o.PanTouch = a.Str("panKind", "touchpad") == "touch";
                o.WheelKey = a.Str("wheelKey");
                o.WheelX = a.Float("wheelX", float.NaN);
                o.WheelY = a.Float("wheelY", float.NaN);
            }
            if (a.Has("drag"))
            {
                if (!TryWheelSequence(string.Join(",", a.List("drag")), out o.Drag)) { error = "drag is a number or a comma list of numbers (DIP per frame along x)"; return false; }
                o.DragFrames = Math.Clamp(a.Int("dragFrames", 8), 1, 5000);
                o.DragStart = Math.Max(0, a.Int("dragStart", 0));
                o.DragDy = a.Float("dragDy", 0f);
                o.DragKey = a.Str("dragKey");
                o.DragX = a.Float("dragX", float.NaN);
                o.DragY = a.Float("dragY", float.NaN);
                if (o.DragKey.Length == 0 && !(float.IsFinite(o.DragX) && float.IsFinite(o.DragY))) { error = "drag wants dragKey or dragX and dragY"; return false; }
            }
            options = o;
            return true;
        }

        /// <summary>The pan contact on traced frame <paramref name="frame"/>: the DIP to feed (<paramref name="delta"/>, valid when
        /// <paramref name="down"/>) and whether the lift lands this frame. Gesture k runs [start + k*every, + PanFrames).</summary>
        public static void PanAt(Options o, int frame, out bool down, out double delta, out bool lift)
        {
            down = false; delta = 0; lift = false;
            if (o.Pan.Length == 0) return;
            for (int k = 0; k < o.PanCount; k++)
            {
                int start = o.PanStart + k * o.PanEvery;
                if (frame >= start && frame < start + o.PanFrames) { down = true; delta = o.Pan[(frame - start) % o.Pan.Length]; return; }
                if (frame == start + o.PanFrames) { lift = true; return; }
                if (o.PanEvery == 0) break;
            }
        }

        /// <summary>"3" or "3,3,-6" into notches; false on an empty list or a non-number.</summary>
        public static bool TryWheelSequence(string text, out double[] sequence)
        {
            sequence = [];
            var list = new List<double>();
            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d)) return false;
                list.Add(d);
            }
            if (list.Count == 0) return false;
            sequence = list.ToArray();
            return true;
        }

        /// <summary>True when the wheel event with ordinal <paramref name="sent"/> is due on traced frame <paramref name="frame"/>.</summary>
        public static bool WheelDue(int frame, int every, int count, int sent) => sent < count && every > 0 && frame % every == 0;

        /// <summary>The notches of event <paramref name="index"/> (the sequence cycles).</summary>
        public static double WheelNotches(double[] sequence, int index) => sequence.Length == 0 ? 0d : sequence[index % sequence.Length];

        // ── the samples ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One watched node on one frame. <see cref="W"/> indexes the trace's watch list.</summary>
        public readonly record struct NodeSample(int W, bool Shown, RectF Laid, RectF Pres, bool Track, float Opacity);

        /// <summary>The traced scroller on one frame. <see cref="Label"/> names the scroller (a different label = a different scroller).</summary>
        public readonly record struct ScrollSample(string Label, double Offset, double Velocity, double Max, double Content, double Viewport,
                                                  double LastShown, string Motion, bool User);

        public sealed class Row
        {
            public int Frame;
            public long Abs;
            public bool Rendered, Presented, Animating, WheelSent;
            public double Wall, FrameMs, Flush, ReactiveFlush, VirtualRealize, Layout, Solve, Effects, Connected, ReflowSeed, Anim, Record, ImagePump, RealizeCatchup, Submit, LongestUnit;
            public int Measure, Arrange, TextShapeMisses, TextShapes, NodesVisited, DrawNodes, ScopedMarks, Escapes, LocalResolves,
                       Components, ReactiveUnits, BindingFires, BindingWrites, ImageApplied, Gc0, Gc1, Gc2;
            public List<NodeSample> Nodes = [];
            public ScrollSample? Scroll;
            /// <summary>The render census of a slow frame (which components made the flush slow), when the host has it on.</summary>
            public string? Census;

            /// <summary>Escapes that were not absorbed locally: each one is a full-window solve.</summary>
            public int RootSolves => Math.Max(0, Escapes - LocalResolves);
            /// <summary>No dirty marks but real measures: the full-layout path (the scoped path always has marks). INFERRED.</summary>
            public bool FullLayoutInferred => ScopedMarks == 0 && Measure > 0;
            /// <summary>flush + layout + anim + record (the UI thread's own work; submit is the GPU/present side).</summary>
            public double Cpu => Flush + Layout + Anim + Record;
        }

        // ── the summary ─────────────────────────────────────────────────────────────────────────────────────────────

        public readonly record struct Move(string Key, int Frame, float Dx, float Dy, float Dw, float Dh, float Mag, float PrevMag, RectF From, RectF To, bool Track);

        public readonly record struct ScrollEvent(int Frame, string Kind, string Scroller, double Offset, double Prev, double Delta, double Max);

        /// <summary>A run of consecutive frames with the offset outside [0, max] on one scroller (a rubber band or an overshoot).</summary>
        public readonly record struct OvershootRun(string Scroller, string Kind, int StartFrame, int EndFrame, int Frames, double MaxOver, int MaxOverFrame);

        public readonly record struct DeltaStats(int Count, double Mean, double Std, double Min, double Max);

        public sealed class Summary
        {
            public int Frames;
            public int SettledAt = -1;             // first frame after which nothing animates for the rest of the trace; -1 = still animating at the end
            public int LastAnimatingFrame = -1;
            public List<int> RootSolveFrames = [];
            public List<int> FullLayoutFrames = [];
            public List<int> EscapeFrames = [];
            public int MeasureTotal, ArrangeTotal, FramesWithMeasure, ComponentsTotal, FramesWithComponents;
            public int MaxLayoutFrame = -1, MaxCpuFrame = -1, MaxWallFrame = -1, MaxRecordFrame = -1, MaxFlushFrame = -1, MaxSubmitFrame = -1, MaxMeasureFrame = -1;
            public double MaxLayoutMs, MaxCpuMs, MaxWallMs, MaxRecordMs, MaxFlushMs, MaxSubmitMs;
            public int MaxMeasure;
            public int CpuOver8, CpuOver16;
            public List<Move> Snaps = [], Jumps = [];
            /// <summary>The jumps that are no glide: one frame carries the run's move (<see cref="DetectInstants"/>).</summary>
            public List<Move> Instants = [];
            public List<OvershootRun> Overshoots = [];
            public List<ScrollEvent> Reversals = [], Stalls = [], Uneven = [], LayoutDuringScroll = [];
            public DeltaStats Deltas;
            public List<(string Key, int First, int Last)> NodeLife = [];
        }

        /// <summary>Everything a trace measured.</summary>
        /// <summary>A watched key that is a different node from one frame to the next (shown on both, one node of that key each time): its
        /// subtree and state were thrown away. <see cref="Family"/> names the swap cross-fade it sits under (a node at or above it fading in
        /// under the theme's opacity-only swap), null when none does: a rebuild.</summary>
        public readonly record struct Remount(string Key, int Frame, string? Family);

        public sealed class Result
        {
            public List<string> Watch = [];
            public List<Remount> Remounts = [];
            public List<Row> Rows = [];
            public Options Options = new();
            public Summary Summary = new();
        }

        public static Summary Summarize(IReadOnlyList<Row> rows, IReadOnlyList<string> watch, Options o)
        {
            var s = new Summary { Frames = rows.Count };
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (r.RootSolves > 0) s.RootSolveFrames.Add(r.Frame);
                if (r.Escapes > 0) s.EscapeFrames.Add(r.Frame);
                if (r.FullLayoutInferred) s.FullLayoutFrames.Add(r.Frame);
                s.MeasureTotal += r.Measure;
                s.ArrangeTotal += r.Arrange;
                if (r.Measure > 0) s.FramesWithMeasure++;
                s.ComponentsTotal += r.Components;
                if (r.Components > 0) s.FramesWithComponents++;
                if (r.Animating) s.LastAnimatingFrame = r.Frame;
                if (r.Layout > s.MaxLayoutMs) { s.MaxLayoutMs = r.Layout; s.MaxLayoutFrame = r.Frame; }
                if (r.Cpu > s.MaxCpuMs) { s.MaxCpuMs = r.Cpu; s.MaxCpuFrame = r.Frame; }
                if (r.Wall > s.MaxWallMs) { s.MaxWallMs = r.Wall; s.MaxWallFrame = r.Frame; }
                if (r.Record > s.MaxRecordMs) { s.MaxRecordMs = r.Record; s.MaxRecordFrame = r.Frame; }
                if (r.Flush > s.MaxFlushMs) { s.MaxFlushMs = r.Flush; s.MaxFlushFrame = r.Frame; }
                if (r.Submit > s.MaxSubmitMs) { s.MaxSubmitMs = r.Submit; s.MaxSubmitFrame = r.Frame; }
                if (r.Measure > s.MaxMeasure) { s.MaxMeasure = r.Measure; s.MaxMeasureFrame = r.Frame; }
                if (r.Cpu > 8.34) s.CpuOver8++;
                if (r.Cpu > 16.67) s.CpuOver16++;
            }
            if (rows.Count > 0) s.SettledAt = s.LastAnimatingFrame < 0 ? rows[0].Frame : (s.LastAnimatingFrame == rows[^1].Frame ? -1 : s.LastAnimatingFrame + 1);
            DetectMoves(rows, watch, o.JumpDip, o.SnapDip, s);
            DetectInstants(s, o.InstantDip);
            DetectScroll(rows, s);
            return s;
        }

        /// <summary>Snaps (laid-out AND presented rect changed with no track on the node or an ancestor, in either frame) and jumps
        /// (presented rect moved more than <paramref name="jumpDip"/> in one frame while a track was active). A node that is not
        /// shown or not present resets its history. A move of the presented rect alone (scroll content translate) is neither.</summary>
        public static void DetectMoves(IReadOnlyList<Row> rows, IReadOnlyList<string> watch, float jumpDip, float snapDip, Summary into)
        {
            var prev = new NodeSample?[watch.Count];
            var prevMag = new float[watch.Count];
            var first = new int[watch.Count];
            var last = new int[watch.Count];
            Array.Fill(first, -1);
            Array.Fill(last, -1);
            var seen = new bool[watch.Count];
            foreach (Row r in rows)
            {
                Array.Clear(seen);
                foreach (NodeSample cur in r.Nodes)
                {
                    int w = cur.W;
                    if ((uint)w >= (uint)watch.Count) continue;
                    seen[w] = true;
                    if (first[w] < 0) first[w] = r.Frame;
                    last[w] = r.Frame;
                    if (!cur.Shown) { prev[w] = null; prevMag[w] = 0; continue; }
                    if (prev[w] is { } p)
                    {
                        float dx = cur.Pres.X - p.Pres.X, dy = cur.Pres.Y - p.Pres.Y, dw = cur.Pres.W - p.Pres.W, dh = cur.Pres.H - p.Pres.H;
                        float mag = MathF.Max(MathF.Max(MathF.Abs(dx), MathF.Abs(dy)), MathF.Max(MathF.Abs(dw), MathF.Abs(dh)));
                        float lay = MathF.Max(MathF.Max(MathF.Abs(cur.Laid.X - p.Laid.X), MathF.Abs(cur.Laid.Y - p.Laid.Y)),
                                              MathF.Max(MathF.Abs(cur.Laid.W - p.Laid.W), MathF.Abs(cur.Laid.H - p.Laid.H)));
                        bool track = cur.Track || p.Track;
                        var move = new Move(watch[w], r.Frame, dx, dy, dw, dh, mag, prevMag[w], p.Pres, cur.Pres, track);
                        if (track && mag > jumpDip) into.Jumps.Add(move);
                        else if (!track && mag > snapDip && lay > snapDip) into.Snaps.Add(move);
                        prevMag[w] = mag;
                    }
                    prev[w] = cur;
                }
                for (int w = 0; w < watch.Count; w++)
                    if (!seen[w]) { prev[w] = null; prevMag[w] = 0; }
            }
            for (int w = 0; w < watch.Count; w++)
                if (first[w] >= 0) into.NodeLife.Add((watch[w], first[w], last[w]));
        }

        /// <summary>The share of a run's whole move one frame must carry for the run to be instant: an eased glide spreads its move over
        /// many frames (an ease-out's first frame carries about a fifth of a 280 ms tween); a reflow that lands at once carries all of it.</summary>
        public const float InstantShare = 0.6f;

        /// <summary>Instant moves: per watched key, the jumps (tracked moves) on consecutive frames make a run; a run whose largest frame
        /// moves at least <paramref name="instantDip"/> and carries <see cref="InstantShare"/> of the run's whole move landed at once, though
        /// a track ran on the node or above it (the trace's `jumps` list every eased frame too: they are not a verdict, these are).</summary>
        public static void DetectInstants(Summary into, float instantDip)
        {
            foreach (var group in into.Jumps.GroupBy(static m => m.Key))
            {
                List<Move> moves = group.OrderBy(static m => m.Frame).ToList();
                for (int i = 0; i < moves.Count;)
                {
                    int j = i;
                    while (j + 1 < moves.Count && moves[j + 1].Frame == moves[j].Frame + 1) j++;
                    float total = 0f;
                    Move top = moves[i];
                    for (int k = i; k <= j; k++)
                    {
                        total += moves[k].Mag;
                        if (moves[k].Mag > top.Mag) top = moves[k];
                    }
                    if (top.Mag >= instantDip && top.Mag >= InstantShare * total) into.Instants.Add(top);
                    i = j + 1;
                }
            }
            into.Instants.Sort(static (a, b) => a.Frame != b.Frame ? a.Frame.CompareTo(b.Frame) : string.CompareOrdinal(a.Key, b.Key));
        }

        /// <summary>Scroll offset analysis per scroller run: direction reversals, offsets outside [0, max] (rubber band), stalls (a zero
        /// delta between two moving frames), uneven deltas (a delta that is a local extreme and off its neighbours' average by over a third of their size) and
        /// relayout work on frames where the offset moved.</summary>
        public static void DetectScroll(IReadOnlyList<Row> rows, Summary into)
        {
            double prevDelta = 0;
            double lastNonZero = 0;
            var deltas = new List<double>();
            var window = new List<(int Frame, double Delta, string Scroller, double Offset, double Max)>();
            ScrollSample? prev = null;
            foreach (Row r in rows)
            {
                if (r.Scroll is not { } cur) { prev = null; window.Clear(); lastNonZero = 0; prevDelta = 0; continue; }
                if (cur.Offset > cur.Max + 0.5 || cur.Offset < -0.5)
                {
                    string kind = cur.Offset < 0 ? "before-start" : "past-end";
                    double over = cur.Offset < 0 ? -cur.Offset : cur.Offset - cur.Max;
                    if (into.Overshoots.Count > 0 && into.Overshoots[^1] is { } run && run.Scroller == cur.Label && run.Kind == kind && run.EndFrame == r.Frame - 1)
                        into.Overshoots[^1] = run with { EndFrame = r.Frame, Frames = run.Frames + 1, MaxOver = Math.Max(run.MaxOver, over), MaxOverFrame = over > run.MaxOver ? r.Frame : run.MaxOverFrame };
                    else into.Overshoots.Add(new OvershootRun(cur.Label, kind, r.Frame, r.Frame, 1, over, r.Frame));
                }
                if (prev is { } p && p.Label == cur.Label)
                {
                    double d = cur.Offset - p.Offset;
                    if (Math.Abs(d) > Epsilon)
                    {
                        deltas.Add(d);
                        if (lastNonZero != 0 && Math.Sign(d) != Math.Sign(lastNonZero))
                            into.Reversals.Add(new ScrollEvent(r.Frame, "reversal", cur.Label, cur.Offset, p.Offset, d, cur.Max));
                        lastNonZero = d;
                        if (r.Escapes > 0 || r.ScopedMarks > 0 || r.Measure > 0)
                            into.LayoutDuringScroll.Add(new ScrollEvent(r.Frame, "layout-during-scroll", cur.Label, cur.Offset, p.Offset, d, cur.Max));
                    }
                    window.Add((r.Frame, d, cur.Label, cur.Offset, cur.Max));
                    prevDelta = d;
                }
                else { window.Clear(); lastNonZero = 0; prevDelta = 0; }
                prev = cur;
            }
            _ = prevDelta;
            // stalls and uneven deltas over consecutive deltas of one scroller
            for (int i = 1; i + 1 < window.Count; i++)
            {
                if (window[i - 1].Scroller != window[i].Scroller || window[i + 1].Scroller != window[i].Scroller) continue;
                double d0 = window[i - 1].Delta, d1 = window[i].Delta, d2 = window[i + 1].Delta;
                if (Math.Abs(d0) >= 1 && Math.Abs(d2) >= 1 && Math.Sign(d0) == Math.Sign(d2))
                {
                    if (Math.Abs(d1) <= Epsilon)
                        into.Stalls.Add(new ScrollEvent(window[i].Frame, "stall", window[i].Scroller, window[i].Offset, window[i].Offset, d1, window[i].Max));
                    else if (Math.Sign(d1) == Math.Sign(d0) && (d1 - d0) * (d1 - d2) > 0 && Math.Abs(d1 - (d0 + d2) / 2) > Math.Max(1.0, 0.35 * Math.Max(Math.Abs(d0), Math.Abs(d2))))
                        into.Uneven.Add(new ScrollEvent(window[i].Frame, "uneven", window[i].Scroller, window[i].Offset, window[i].Offset - d1, d1, window[i].Max));
                }
            }
            if (deltas.Count > 0)
            {
                double mean = deltas.Average(), var = 0;
                foreach (double d in deltas) var += (d - mean) * (d - mean);
                into.Deltas = new DeltaStats(deltas.Count, mean, Math.Sqrt(var / deltas.Count), deltas.Min(), deltas.Max());
            }
        }

        // ── the reply ───────────────────────────────────────────────────────────────────────────────────────────────

        static void Ms(Utf8JsonWriter w, string name, double v) => w.WriteNumber(name, double.IsFinite(v) ? Math.Round(v, 3) : 0d);

        static void Arr(Utf8JsonWriter w, string name, in RectF r)
        {
            w.WritePropertyName(name);
            w.WriteStartArray();
            foreach (float v in (ReadOnlySpan<float>)[r.X, r.Y, r.W, r.H]) w.WriteNumberValue(float.IsFinite(v) ? Math.Round(v, 2) : 0d);
            w.WriteEndArray();
        }

        /// <summary>Writes the trace into the current JSON object: <c>note</c>, <c>watch</c>, <c>rows</c> (unless <c>rows=false</c>) and <c>summary</c>.</summary>
        public static void Write(Utf8JsonWriter w, Result result)
        {
            Options o = result.Options;
            w.WriteString("note", "ms are the CPU cost of each frame while the motion plays on the inspect host's fixed 16 ms fake frame clock; frames are not real time");
            w.WriteNumber("traced", result.Rows.Count);
            if (result.Watch.Count == 0)   // the move detectors read the watched nodes only: say why snaps and jumps are 0
                w.WriteString("warning", o.WatchesNodes
                    ? "no node matched keys/prefix: nothing was watched, so snaps and jumps read 0"
                    : "no keys or prefix given: nothing was watched, so snaps and jumps read 0 (add prefix= or keys=)");
            w.WriteStartArray("watch");
            foreach (string k in result.Watch) w.WriteStringValue(k);
            w.WriteEndArray();
            if (o.Rows)
            {
                w.WriteStartArray("rows");
                foreach (Row r in result.Rows)
                    if (r.Frame % o.Every == 0 || r.WheelSent) WriteRow(w, r);
                w.WriteEndArray();
            }
            WriteSummary(w, result.Summary);
            // the gate's counts: snapped watched keys, and remounts in and outside a swap family
            var snappedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (Move m in result.Summary.Snaps) snappedKeys.Add(m.Key);
            var movedKeys = new HashSet<string>(snappedKeys, StringComparer.Ordinal);
            foreach (Move m in result.Summary.Instants) movedKeys.Add(m.Key);
            int swap = 0;
            foreach (Remount r in result.Remounts) if (r.Family is not null) swap++;
            w.WriteStartObject("counts");
            w.WriteNumber("watched", result.Watch.Count);
            w.WriteNumber("snaps", result.Summary.Snaps.Count);
            w.WriteNumber("snappedKeys", snappedKeys.Count);
            w.WriteNumber("instants", result.Summary.Instants.Count);
            w.WriteNumber("movedAtOnce", movedKeys.Count);   // snapped or instant keys: what `inspect.py --gate` counts
            w.WriteNumber("remounted", result.Remounts.Count);
            w.WriteNumber("remountedSwap", swap);
            w.WriteNumber("remountedOutside", result.Remounts.Count - swap);
            w.WriteEndObject();
            w.WriteStartArray("remounted");
            foreach (Remount r in result.Remounts)
            {
                w.WriteStartObject();
                w.WriteString("key", r.Key);
                w.WriteNumber("frame", r.Frame);
                if (r.Family is null) w.WriteNull("family");
                else w.WriteString("family", r.Family);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        static void WriteRow(Utf8JsonWriter w, Row r)
        {
            w.WriteStartObject();
            w.WriteNumber("f", r.Frame);
            if (!string.IsNullOrEmpty(r.Census)) w.WriteString("census", r.Census);
            w.WriteNumber("abs", r.Abs);
            w.WriteBoolean("rendered", r.Rendered);
            w.WriteBoolean("animating", r.Animating);
            if (r.WheelSent) w.WriteBoolean("wheel", true);
            w.WriteStartObject("ms");
            Ms(w, "wall", r.Wall); Ms(w, "cpu", r.Cpu); Ms(w, "flush", r.Flush); Ms(w, "reactiveFlush", r.ReactiveFlush); Ms(w, "virtualRealize", r.VirtualRealize);
            Ms(w, "layout", r.Layout); Ms(w, "solve", r.Solve); Ms(w, "layoutEffects", r.Effects); Ms(w, "connected", r.Connected); Ms(w, "reflowSeed", r.ReflowSeed);
            Ms(w, "anim", r.Anim); Ms(w, "record", r.Record); Ms(w, "imagePump", r.ImagePump); Ms(w, "realizeCatchup", r.RealizeCatchup);
            Ms(w, "submit", r.Submit); Ms(w, "longestUnit", r.LongestUnit);
            w.WriteEndObject();
            w.WriteStartObject("n");
            w.WriteNumber("measure", r.Measure); w.WriteNumber("arrange", r.Arrange); w.WriteNumber("scopedMarks", r.ScopedMarks);
            w.WriteNumber("escapes", r.Escapes); w.WriteNumber("localResolves", r.LocalResolves); w.WriteNumber("rootSolves", r.RootSolves);
            w.WriteBoolean("fullLayoutInferred", r.FullLayoutInferred);
            w.WriteNumber("components", r.Components); w.WriteNumber("reactiveUnits", r.ReactiveUnits);
            w.WriteNumber("bindingFires", r.BindingFires); w.WriteNumber("bindingWrites", r.BindingWrites);
            w.WriteNumber("textShapes", r.TextShapes); w.WriteNumber("textShapeMisses", r.TextShapeMisses);
            w.WriteNumber("nodesVisited", r.NodesVisited); w.WriteNumber("drawNodes", r.DrawNodes); w.WriteNumber("imageApplied", r.ImageApplied);
            w.WriteNumber("gc0", r.Gc0); w.WriteNumber("gc1", r.Gc1); w.WriteNumber("gc2", r.Gc2);
            w.WriteEndObject();
            if (r.Nodes.Count > 0)
            {
                w.WriteStartArray("nodes");
                foreach (NodeSample n in r.Nodes)
                {
                    w.WriteStartObject();
                    w.WriteNumber("w", n.W);
                    Arr(w, "lay", n.Laid);
                    Arr(w, "pres", n.Pres);
                    w.WriteBoolean("track", n.Track);
                    w.WriteBoolean("shown", n.Shown);
                    Ms(w, "op", n.Opacity);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (r.Scroll is { } s)
            {
                w.WriteStartObject("scroll");
                w.WriteString("on", s.Label);
                Ms(w, "offset", s.Offset); Ms(w, "lastShown", s.LastShown); Ms(w, "velocity", s.Velocity); Ms(w, "max", s.Max);
                Ms(w, "content", s.Content); Ms(w, "viewport", s.Viewport);
                w.WriteString("motion", s.Motion);
                w.WriteBoolean("user", s.User);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }

        static void Ints(Utf8JsonWriter w, string name, List<int> v)
        {
            w.WriteStartArray(name);
            foreach (int i in v) w.WriteNumberValue(i);
            w.WriteEndArray();
        }

        static void Moves(Utf8JsonWriter w, string name, List<Move> moves)
        {
            w.WriteStartArray(name);
            foreach (Move m in moves)
            {
                w.WriteStartObject();
                w.WriteString("key", m.Key);
                w.WriteNumber("frame", m.Frame);
                w.WriteNumber("dx", Math.Round(m.Dx, 2)); w.WriteNumber("dy", Math.Round(m.Dy, 2));
                w.WriteNumber("dw", Math.Round(m.Dw, 2)); w.WriteNumber("dh", Math.Round(m.Dh, 2));
                w.WriteNumber("prevMag", Math.Round(m.PrevMag, 2));
                w.WriteBoolean("track", m.Track);
                Arr(w, "from", m.From);
                Arr(w, "to", m.To);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        static void Events(Utf8JsonWriter w, string name, List<ScrollEvent> events)
        {
            w.WriteStartArray(name);
            foreach (ScrollEvent e in events)
            {
                w.WriteStartObject();
                w.WriteNumber("frame", e.Frame);
                w.WriteString("kind", e.Kind);
                w.WriteString("on", e.Scroller);
                Ms(w, "offset", e.Offset); Ms(w, "prev", e.Prev); Ms(w, "delta", e.Delta); Ms(w, "max", e.Max);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        public static void WriteSummary(Utf8JsonWriter w, Summary s)
        {
            w.WriteStartObject("summary");
            w.WriteNumber("frames", s.Frames);
            w.WriteNumber("settledAt", s.SettledAt);
            w.WriteNumber("lastAnimatingFrame", s.LastAnimatingFrame);
            Ints(w, "rootSolveFrames", s.RootSolveFrames);
            Ints(w, "escapeFrames", s.EscapeFrames);
            Ints(w, "fullLayoutFramesInferred", s.FullLayoutFrames);
            w.WriteNumber("measureTotal", s.MeasureTotal); w.WriteNumber("arrangeTotal", s.ArrangeTotal);
            w.WriteNumber("framesWithMeasure", s.FramesWithMeasure);
            w.WriteNumber("maxMeasure", s.MaxMeasure); w.WriteNumber("maxMeasureFrame", s.MaxMeasureFrame);
            w.WriteNumber("componentsTotal", s.ComponentsTotal); w.WriteNumber("framesWithComponents", s.FramesWithComponents);
            w.WriteStartObject("max");
            void Max(string n, double ms, int frame) { w.WriteStartObject(n); Ms(w, "ms", ms); w.WriteNumber("frame", frame); w.WriteEndObject(); }
            Max("layout", s.MaxLayoutMs, s.MaxLayoutFrame); Max("cpu", s.MaxCpuMs, s.MaxCpuFrame); Max("wall", s.MaxWallMs, s.MaxWallFrame);
            Max("record", s.MaxRecordMs, s.MaxRecordFrame); Max("flush", s.MaxFlushMs, s.MaxFlushFrame); Max("submit", s.MaxSubmitMs, s.MaxSubmitFrame);
            w.WriteEndObject();
            w.WriteNumber("cpuOver8ms", s.CpuOver8); w.WriteNumber("cpuOver16ms", s.CpuOver16);
            Moves(w, "snaps", s.Snaps);
            Moves(w, "jumps", s.Jumps);
            Moves(w, "instants", s.Instants);
            w.WriteStartObject("scroll");
            Events(w, "reversals", s.Reversals);
            w.WriteStartArray("overshoots");
            foreach (OvershootRun o in s.Overshoots)
            {
                w.WriteStartObject();
                w.WriteString("on", o.Scroller); w.WriteString("kind", o.Kind);
                w.WriteNumber("startFrame", o.StartFrame); w.WriteNumber("endFrame", o.EndFrame); w.WriteNumber("frames", o.Frames);
                Ms(w, "maxOver", o.MaxOver); w.WriteNumber("maxOverFrame", o.MaxOverFrame);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            Events(w, "stalls", s.Stalls);
            Events(w, "uneven", s.Uneven);
            Events(w, "layoutDuringScroll", s.LayoutDuringScroll);
            w.WriteStartObject("deltas");
            w.WriteNumber("count", s.Deltas.Count); Ms(w, "mean", s.Deltas.Mean); Ms(w, "std", s.Deltas.Std); Ms(w, "min", s.Deltas.Min); Ms(w, "max", s.Deltas.Max);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartArray("nodeLife");
            foreach (var (key, first, last) in s.NodeLife)
            {
                w.WriteStartObject();
                w.WriteString("key", key); w.WriteNumber("first", first); w.WriteNumber("last", last);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
    }

    public sealed partial class InspectSession
    {
        static bool Tracing(Inspect.Args a) => a.Int("trace", 0) > 0;

        /// <summary>`trace {frames, keys?, prefix?, scroll?, every?, ...}`: run N frames and report them (see InspectTrace).</summary>
        string Trace(int id, string name, Inspect.Args a, Shell.HeadlessShell shell)
        {
            if (!InspectTrace.TryParse(a, a.Int("frames", 60), "", out InspectTrace.Options o, out string error)) return Inspect.Reply.Fail(id, name, error);
            InspectTrace.Result result = RunTrace(shell, o);
            return Inspect.Reply.Ok(id, name, w => InspectTrace.Write(w, result));
        }

        /// <summary>The nodes of one shown vertical scroller: "main" = the shown scroller with the most room, else the first scroller under the node keyed so.</summary>
        NodeHandle TraceScroller(Shell.HeadlessShell shell, string spec, List<(NodeHandle Node, string Key)> keyed)
        {
            if (spec.Length == 0) return NodeHandle.Null;
            SceneStore scene = shell.Host.Scene;
            if (spec != "main")
            {
                foreach (var (node, key) in keyed)
                    if (string.Equals(key, spec, StringComparison.Ordinal) && ScrollUnder(scene, node) is { IsNull: false } vp) return vp;
                return NodeHandle.Null;
            }
            NodeHandle best = NodeHandle.Null;
            float room = -1f;
            foreach (var n in InspectKit.Subtree(scene, scene.Root))
            {
                if (!scene.HasScroll(n) || !scene.TryGetScroll(n, out ScrollState s) || s.Orientation != 0 || !scene.IsShown(n)) continue;
                if (s.MaxOffset > room) { room = s.MaxOffset; best = n; }
            }
            return best;
        }

        static string ScrollLabel(SceneStore scene, NodeHandle vp)
        {
            for (var n = vp; !n.IsNull; n = scene.Parent(n))
                if (scene.DebugKeyOf(n) is { Length: > 0 } k) return k + "@" + vp.Raw.Index;
            return "scroller@" + vp.Raw.Index;
        }

        /// <summary>Run <paramref name="o"/>.Frames frames from where the host stands and sample each one.</summary>
        InspectTrace.Result RunTrace(Shell.HeadlessShell shell, InspectTrace.Options o)
        {
            SceneStore scene = shell.Host.Scene;
            var anim = shell.Host.Animation;
            var result = new InspectTrace.Result { Options = o };
            var watchIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var seen = new HashSet<uint>();
            var dup = new Dictionary<string, int>(StringComparer.Ordinal);
            var lastNode = new Dictionary<string, NodeHandle>(StringComparer.Ordinal);   // the node of each lone watched key on the frame before
            var nowNode = new Dictionary<string, NodeHandle>(StringComparer.Ordinal);
            int sent = 0;
            bool moved = false;
            Point2 dragAt = default;
            shell.Host.RenderCensus = o.Census;   // a slow frame names its components (row.census)
            for (int f = 0; f < o.Frames; f++)
            {
                var keyed = shell.FindPrefix("");   // one key copy per frame serves the nodes, the wheel target and the scroller
                bool wheel = o.Wheel.Length > 0 && InspectTrace.WheelDue(f, o.WheelEvery, o.WheelCount, sent);
                if (wheel)
                {
                    Point2 p = WheelPoint(shell, o, keyed);
                    if (!moved) { Move(p); moved = true; }
                    var evt = new ScrollInputEvent(ScrollSource.MouseWheel, ScrollGesture.Notch, 0, p, 0f, (float)InspectTrace.WheelNotches(o.Wheel, sent), 0, KeyModifiers.None);
                    Queue(InputEvent.ForScroll(in evt, timestampMs: _ms));
                    sent++;
                }
                if (o.Pan.Length > 0)
                {
                    InspectTrace.PanAt(o, f, out bool down, out double dy, out bool lift);
                    if (down || lift)
                    {
                        Point2 p = WheelPoint(shell, o, keyed);
                        if (down) shell.Window.QueueScrollDelta(0f, (float)dy, o.PanTouch ? PointerKind.Touch : PointerKind.Touchpad, p);
                        if (lift) shell.Window.QueueScrollLift();
                    }
                    wheel |= down;
                }
                if (o.Drag.Length > 0) DragStep(shell, o, f, keyed, ref dragAt);
                long t0 = Stopwatch.GetTimestamp();
                FrameStats s = shell.Host.RunFrame();
                double wall = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                _ms += 16;
                _frames++;
                var row = new InspectTrace.Row
                {
                    Frame = f, Abs = _frames, Rendered = s.Rendered, Presented = s.Presented, Animating = anim.HasActive, WheelSent = wheel,
                    Wall = wall, FrameMs = s.FrameMs, Flush = s.FlushMs, ReactiveFlush = s.ReactiveFlushMs, VirtualRealize = s.VirtualRealizeMs,
                    Layout = s.LayoutMs, Solve = s.LayoutSolveMs, Effects = s.LayoutEffectsMs, Connected = s.ConnectedTickMs, ReflowSeed = s.ReflowSeedMs,
                    Anim = s.AnimMs, Record = s.RecordMs, ImagePump = s.ImagePumpMs, RealizeCatchup = s.RealizeCatchupMs, Submit = s.SubmitMs,
                    LongestUnit = s.ReactiveLongestUnitMs,
                    Measure = s.MeasureCount, Arrange = s.ArrangeCount, TextShapeMisses = s.TextShapeMisses, TextShapes = s.TextShapes,
                    NodesVisited = s.NodesVisited, DrawNodes = s.DrawNodeCount, ScopedMarks = s.ScopedRelayoutMarks, Escapes = s.RootRelayoutEscapes,
                    LocalResolves = s.LocalRelayoutResolves, Components = s.ComponentsRendered, ReactiveUnits = s.ReactiveUnits,
                    BindingFires = s.BindingFires, BindingWrites = s.BindingWrites, ImageApplied = s.ImageApplyCount,
                    Gc0 = s.Gc0Delta, Gc1 = s.Gc1Delta, Gc2 = s.Gc2Delta, Census = s.Census,
                };
                if (o.WatchesNodes)
                {
                    keyed = shell.FindPrefix("");   // after the frame: the keys of the nodes this frame left standing
                    seen.Clear();
                    dup.Clear();
                    nowNode.Clear();
                    foreach (var (node, key) in keyed)
                    {
                        if (!Matches(o, key) || !seen.Add(node.Raw.Index)) continue;
                        int n = dup.TryGetValue(key, out int c) ? c : 0;
                        dup[key] = n + 1;
                        string label = n == 0 ? key : key + "#" + n;
                        if (!watchIndex.TryGetValue(label, out int w))
                        {
                            if (watchIndex.Count >= o.MaxNodes) continue;
                            w = watchIndex.Count;
                            watchIndex[label] = w;
                            result.Watch.Add(label);
                        }
                        float opacity = 1f;
                        bool track = false;
                        for (var up = node; !up.IsNull; up = scene.Parent(up))
                        {
                            opacity *= scene.Paint(up).Opacity;
                            if (!track && anim.HasTracks(up)) track = true;
                        }
                        row.Nodes.Add(new InspectTrace.NodeSample(w, scene.IsShown(node), scene.AbsoluteLayoutRect(node), scene.PresentedAbsoluteRect(node), track, opacity));
                        if (scene.IsShown(node)) nowNode[key] = n == 0 ? node : NodeHandle.Null;   // a second node of the key: not a lone key
                    }
                    foreach (var (key, node) in nowNode)
                        if (!node.IsNull && lastNode.TryGetValue(key, out NodeHandle was) && !was.IsNull && was != node)
                            result.Remounts.Add(new InspectTrace.Remount(key, f, FadingSwapFamily(shell, node)));
                    lastNode.Clear();
                    foreach (var (key, node) in nowNode) lastNode[key] = node;
                }
                if (o.Scroll.Length > 0)
                {
                    NodeHandle vp = TraceScroller(shell, o.Scroll, keyed);
                    if (!vp.IsNull && scene.TryGetScroll(vp, out ScrollState ss))
                    {
                        ScrollHandle? h = shell.Host.TryGetScrollHandle(vp);
                        row.Scroll = new InspectTrace.ScrollSample(ScrollLabel(scene, vp), ss.Offset, ss.Velocity, ss.MaxOffset, ss.ContentH, ss.ViewportH,
                            h?.LastShown ?? ss.Offset, ss.Motion.Kind.ToString(), ss.Motion.UserDriven);
                    }
                }
                result.Rows.Add(row);
            }
            result.Summary = InspectTrace.Summarize(result.Rows, result.Watch, o);
            return result;
        }

        /// <summary>The swap family a node remounted mid-trace belongs to: the key of the nearest node at or above it that swaps under the theme's
        /// opacity-only cross-fade (<see cref="SwapsOut"/>) and is fading right now (live tracks); null when none is (a rebuild).</summary>
        static string? FadingSwapFamily(Shell.HeadlessShell shell, NodeHandle node)
        {
            SceneStore scene = shell.Host.Scene;
            for (NodeHandle n = node; !n.IsNull && scene.IsLive(n); n = scene.Parent(n))
                if (SwapsOut(shell, n) && shell.Host.Animation.HasTracks(n)) return InspectKit.KeyOf(scene, n);
            return null;
        }

        static bool Matches(InspectTrace.Options o, string key)
        {
            foreach (string k in o.Keys)
                if (string.Equals(k, key, StringComparison.Ordinal)) return true;
            foreach (string p in o.Prefixes)
                if (key.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Where a trace's wheel lands: `wheelKey`, else `wheelX`/`wheelY`, else the centre of the traced scroller, else the window centre.</summary>
        /// <summary>The drag on traced frame <paramref name="f"/>: the press on its start point, a move per frame while it is down, the release.</summary>
        void DragStep(Shell.HeadlessShell shell, InspectTrace.Options o, int f, List<(NodeHandle Node, string Key)> keyed, ref Point2 at)
        {
            int end = o.DragStart + o.DragFrames;
            if (f < o.DragStart || f > end) return;
            if (f == o.DragStart)
            {
                at = float.IsFinite(o.DragX) && float.IsFinite(o.DragY) ? new Point2(o.DragX, o.DragY) : default;
                if (o.DragKey.Length > 0)
                    foreach (var (node, key) in keyed)
                        if (string.Equals(key, o.DragKey, StringComparison.Ordinal)) { at = InspectKit.CentreOf(shell.Host.Scene, node); break; }
                Move(at);
                Queue(new InputEvent(InputKind.PointerDown, at, 0, 0, TimestampMs: _ms));
                return;
            }
            if (f == end) { Queue(new InputEvent(InputKind.PointerUp, at, 0, 0, TimestampMs: _ms)); return; }
            at = new Point2(at.X + (float)o.Drag[(f - o.DragStart - 1) % o.Drag.Length], at.Y + o.DragDy);
            Move(at);
        }

        Point2 WheelPoint(Shell.HeadlessShell shell, InspectTrace.Options o, List<(NodeHandle Node, string Key)> keyed)
        {
            SceneStore scene = shell.Host.Scene;
            if (o.WheelKey.Length > 0)
                foreach (var (node, key) in keyed)
                    if (string.Equals(key, o.WheelKey, StringComparison.Ordinal)) return InspectKit.CentreOf(scene, node);
            if (float.IsFinite(o.WheelX) && float.IsFinite(o.WheelY)) return new Point2(o.WheelX, o.WheelY);
            NodeHandle vp = TraceScroller(shell, o.Scroll.Length > 0 ? o.Scroll : "main", keyed);
            if (!vp.IsNull) return InspectKit.CentreOf(scene, vp);
            float scale = Math.Max(0.01f, shell.Window.Scale);
            return new Point2(shell.Window.ClientSizePx.Width / scale / 2f, shell.Window.ClientSizePx.Height / scale / 2f);
        }
    }
}
