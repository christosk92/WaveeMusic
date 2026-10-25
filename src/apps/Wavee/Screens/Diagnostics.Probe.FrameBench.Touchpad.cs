// ── Screens/Diagnostics.Probe.FrameBench.Touchpad.cs ────────────────────────────────────────────────────────────────
// the `touchpad-scroll` / `touchpad-list` frame-bench scenarios (opt-in): the synthetic precision touchpad
//
// Run them by name (opt-in):
//   Wavee.exe --fake --profile <scratch dir> --frame-bench=touchpad-scroll,touchpad-list --bench-size 1664x1109 --probe-out <dir>
//             [--bench-tp-lift-frames 2] [--bench-tp-speed 1]
//
// A DirectManipulation-shaped contact script (SyntheticTouchpad) is pumped at DM's own call site (Win32Window.PumpScroll, once per
// produced frame, after the display-phase gate) with that frame's clock: it hints itself by DM's rule (the finger evaluated at
// now + clamp(PresentQpc − now, 0, one refresh), whole ms) and stamps every event by DM's (ContactStamp.ForFrame: the next tick's
// present), Begin on the first pump of a contact, one Sample per pump in which the finger moved, End `--bench-tp-lift-frames` pumps
// after the finger stopped producing motion (DM raises its status edge 1-5 frames after the lift) with DM's verdict (a flick lifts
// MOVING, a drag lifts STOPPED). Its events enter the window's own input queue (the one DM's producer feeds): no OS input, no real
// cursor, no SendInput — only the bench window scrolls.
// The program (seconds; it loops): a reading drag down (ramp 0.12 s to 900 DIP/s, hold 0.5 s, ramp down 0.25 s, lift at rest),
// a flick down (0.18 s to 6000 DIP/s, lift moving), a second flick down while the first coasts (0.16 s to 4500 DIP/s), then the same
// three upwards; velocities scaled down when the page is too short for the excursion (`tpSpeed` in the summary).
// Besides the usual ledgers it writes `<scenario>-touchpad.csv` (one row per pump: the finger at now and at DM's target time, what
// was emitted, the stamp) and `<scenario>-probe.csv` (the scroll probe at Trace for the scrolled viewport: inputs, plans, poses with
// their present times, render turns and their costs, the DXGI present ledger), from which tools/tpscroll.py derives the latency,
// the displayed offset's lag behind the finger, presented and repeated frames.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        /// <summary>One scripted contact: a velocity profile on [0, Dur] (DIP/s, positive = toward the content end), then the lift.</summary>
        sealed class TouchpadGesture
        {
            public readonly double StartS, DurS, Sign;
            public readonly bool Flick;
            readonly double[] _pos;          // finger position (DIP) at 1 ms steps over [0, Dur]
            readonly double _vEnd;
            const double StepS = 0.001;

            TouchpadGesture(double startS, double sign, bool flick, Func<double, double> v, double durS)
            {
                StartS = startS; DurS = durS; Sign = sign; Flick = flick;
                int n = (int)Math.Ceiling(durS / StepS) + 1;
                _pos = new double[n];
                double p = 0, vPrev = v(0);
                for (int i = 1; i < n; i++)
                {
                    double vi = v(Math.Min(durS, i * StepS));
                    p += 0.5 * (vPrev + vi) * StepS;
                    _pos[i] = p;
                    vPrev = vi;
                }
                _vEnd = v(durS);
            }

            static double Smooth(double x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x);

            /// <summary>A reading drag: up to <paramref name="v"/> over <paramref name="ramp"/>, held, down to rest; lifts at rest.</summary>
            public static TouchpadGesture Drag(double startS, double sign, double v, double ramp, double hold, double rampDown)
                => new(startS, sign, false, t => t < ramp ? v * Smooth(t / ramp) : t < ramp + hold ? v : v * Smooth(1 - (t - ramp - hold) / rampDown),
                       ramp + hold + rampDown);

            /// <summary>A flick: an ease-in to <paramref name="v"/> over <paramref name="dur"/>; the finger lifts at that speed.</summary>
            public static TouchpadGesture Flicked(double startS, double sign, double v, double dur)
                => new(startS, sign, true, t => { double s = Math.Sin(0.5 * Math.PI * Math.Clamp(t / dur, 0, 1)); return v * s * s; }, dur);

            /// <summary>The finger's displacement (DIP, unsigned) at <paramref name="t"/> s after the contact began; past the lift it
            /// follows the lift velocity (what a predicting producer reports for a target time beyond the last report).</summary>
            public double Pos(double t)
            {
                if (t <= 0) return 0;
                if (t >= DurS) return _pos[^1] + _vEnd * (t - DurS);
                double x = t / StepS;
                int i = (int)x;
                if (i >= _pos.Length - 1) return _pos[^1];
                return _pos[i] + (_pos[i + 1] - _pos[i]) * (x - i);
            }

            public double Travel => _pos[^1];
        }

        /// <summary>One pump of the synthetic producer (the touchpad CSV's row).</summary>
        readonly record struct TouchpadPump(long NowQpc, double LeadMs, long StampQpc, int Gesture, double FingerNow, double FingerTarget,
            char Emitted, double Delta, long PresentQpc, long FrameQpc);

        /// <summary>The scripted DirectManipulation stand-in (see the file header). UI thread only.</summary>
        sealed class SyntheticTouchpad : ISyntheticScrollProducer
        {
            readonly TouchpadGesture[] _program;
            readonly double _cycleS;
            readonly int _liftFrames;
            readonly Point2 _pointer;
            readonly long _freq = Stopwatch.Frequency;
            long _t0;                      // program origin (QPC)
            int _cycle, _next;             // the next gesture to begin: _program[_next] of cycle _cycle
            int _active = -1;              // the contact in progress (program index), -1 = none
            long _activeStartQpc;
            double _activeEmitted;         // what this contact has emitted so far (unsigned DIP)
            bool _lifted;                  // the finger is up: no more motion, End after _liftFrames pumps
            int _liftPumps;
            uint _contactId = 0x7C00;
            double _fingerBase;            // signed finger position at the active contact's begin (the program's cumulative DM position)
            public readonly List<TouchpadPump> Pumps = new(16384);
            public long Begins, Samples, Ends;

            public SyntheticTouchpad(TouchpadGesture[] program, double cycleS, int liftFrames, Point2 pointer)
            {
                _program = program; _cycleS = cycleS; _liftFrames = liftFrames; _pointer = pointer;
            }

            public void Start(long nowQpc) { _t0 = nowQpc; _cycle = 0; _next = 0; _active = -1; }

            bool _restart;
            long _restartAt;

            /// <summary>Restart the program at <paramref name="startQpc"/> (the measured window's start, so every run measures the
            /// same phase of it). A contact in progress is lifted at rest on the next pump.</summary>
            public void Restart(long startQpc) { _restart = true; _restartAt = startQpc; }

            long StartQpcOf(int cycle, int index) => _t0 + (long)((cycle * _cycleS + _program[index].StartS) * _freq);

            /// <summary>Seconds (driver clock) until the next contact begins — the bench driver's next wake.</summary>
            public double NextStartQpc => StartQpcOf(_cycle, _next);

            public bool Live => _t0 != 0 && (_active >= 0 || Stopwatch.GetTimestamp() >= StartQpcOf(_cycle, _next) - _freq / 60);

            public void Pump(in FrameClock clock, long nowQpc, Win32Window window)
            {
                if (_t0 == 0) return;
                double refreshMs = clock.RefreshQpc > 0 ? clock.RefreshQpc * 1000.0 / _freq : 0.0;
                double leadMs = (clock.Flags & FrameClockFlags.Unpaced) != 0 ? 0.0 : Math.Clamp((clock.PresentQpc - nowQpc) * 1000.0 / _freq, 0.0, refreshMs);
                leadMs = Math.Round(leadMs);   // DM is told whole milliseconds (Win32DirectManipulation.PumpOnce)
                long target = nowQpc + (long)(leadMs * _freq / 1000.0);
                long stamp = ContactStamp.ForFrame(in clock, nowQpc);
                char emitted = '-';
                double delta = 0;
                if (_restart)
                {
                    _restart = false;
                    if (_active >= 0)
                    {
                        window.EnqueueSyntheticScroll(new ScrollInputEvent(ScrollSource.Touchpad, ScrollGesture.End, stamp, _pointer, 0f, 0f, _contactId, KeyModifiers.None)
                            { PresentTimed = true, ArrivalQpc = nowQpc, Release = ContactRelease.Stopped });
                        Ends++;
                        emitted = 'E';
                        _fingerBase += _program[_active].Sign * _activeEmitted;
                        _active = -1;
                    }
                    Start(_restartAt);
                }
                if (_active < 0 && emitted == '-' && nowQpc >= StartQpcOf(_cycle, _next))
                {
                    _active = _next;
                    _activeStartQpc = StartQpcOf(_cycle, _next);
                    _activeEmitted = 0; _lifted = false; _liftPumps = 0; _contactId++;
                    if (++_next == _program.Length) { _next = 0; _cycle++; }
                    window.EnqueueSyntheticScroll(new ScrollInputEvent(ScrollSource.Touchpad, ScrollGesture.Begin, stamp, _pointer, 0f, 0f, _contactId, KeyModifiers.None)
                        { PresentTimed = true, ArrivalQpc = nowQpc });
                    Begins++;
                    emitted = 'B';
                }
                double fingerNow = _fingerBase, fingerTarget = _fingerBase;
                int g = _active;
                if (g >= 0)
                {
                    var ge = _program[g];
                    double tNow = (nowQpc - _activeStartQpc) / (double)_freq;
                    fingerNow = _fingerBase + ge.Sign * ge.Pos(Math.Min(tNow, ge.DurS));
                    if (!_lifted && tNow >= ge.DurS)
                    {
                        // The finger is up: DM produces no more content (its last, predicted report stands — it never reports a
                        // step back), and raises its status edge _liftFrames pumps later.
                        _lifted = true;
                    }
                    else if (!_lifted)
                    {
                        // DM reports the finger it predicts for the composition target (extrapolated past the lift while the
                        // finger is still down at `now`).
                        double tTarget = (target - _activeStartQpc) / (double)_freq;
                        fingerTarget = _fingerBase + ge.Sign * ge.Pos(tTarget);
                        double pos = ge.Pos(tTarget);
                        double d = pos - _activeEmitted;
                        if (Math.Abs(d) > 1e-6)
                        {
                            float dy = (float)(ge.Sign * d);
                            window.EnqueueSyntheticScroll(new ScrollInputEvent(ScrollSource.Touchpad, ScrollGesture.Sample, stamp, _pointer, 0f, dy, _contactId, KeyModifiers.None)
                                { PresentTimed = true, ArrivalQpc = nowQpc });
                            _activeEmitted = pos;
                            delta = dy;
                            Samples++;
                            emitted = emitted == 'B' ? 'b' : 'S';
                        }
                    }
                    else if (++_liftPumps >= _liftFrames)
                    {
                        window.EnqueueSyntheticScroll(new ScrollInputEvent(ScrollSource.Touchpad, ScrollGesture.End, stamp, _pointer, 0f, 0f, _contactId, KeyModifiers.None)
                            { PresentTimed = true, ArrivalQpc = nowQpc, Release = ge.Flick ? ContactRelease.Moving : ContactRelease.Stopped });
                        Ends++;
                        emitted = 'E';
                        _fingerBase += ge.Sign * _activeEmitted;
                        fingerNow = fingerTarget = _fingerBase;
                        _active = -1;
                    }
                }
                Pumps.Add(new TouchpadPump(nowQpc, leadMs, stamp, g, fingerNow, fingerTarget, emitted, delta, clock.PresentQpc, clock.FrameQpc));
            }
        }

        /// <summary>The scroll probe's rows for one viewport (and every render turn / present), drained every loop iteration so its
        /// 8192-row rings never wrap under a window's worth of poses.</summary>
        sealed class ProbeTap(int vp)
        {
            readonly ProbeRow[] _buf = new ProbeRow[4096];
            long _ui = ScrollProbe.UiWriteCount, _render = ScrollProbe.RenderWriteCount;
            public readonly List<ProbeRow> Rows = new(65536);
            public long Lost;

            public void Drain()
            {
                while (true)
                {
                    long from = _ui;
                    int n = ScrollProbe.ReadUi(from, _buf, out long next);
                    if (next - from > n) Lost += next - from - n;
                    _ui = next;
                    for (int i = 0; i < n; i++) Keep(in _buf[i]);
                    if (n < _buf.Length) break;
                }
                while (true)
                {
                    long from = _render;
                    int n = ScrollProbe.ReadRender(from, _buf, out long next);
                    if (next - from > n) Lost += next - from - n;
                    _render = next;
                    for (int i = 0; i < n; i++) Keep(in _buf[i]);
                    if (n < _buf.Length) break;
                }
            }

            void Keep(in ProbeRow r)
            {
                switch (r.Kind)
                {
                    case ProbeRowKind.Pose:
                    case ProbeRowKind.Plan:
                    case ProbeRowKind.Coverage:
                    case ProbeRowKind.Extent:
                        if (r.Vp == vp) Rows.Add(r);
                        break;
                    case ProbeRowKind.Input:
                    case ProbeRowKind.Turn:
                    case ProbeRowKind.TurnCost:
                    case ProbeRowKind.Present:
                    case ProbeRowKind.Mark:
                        Rows.Add(r);
                        break;
                }
            }
        }

        static ProbeTap? s_probeTap;

        static Pending TouchpadWindow(string name, AppHost host, Win32Window w, FrameBenchOptions o, ScrollState s)
        {
            var vi = s.Info;
            double freq = Stopwatch.Frequency;
            int liftFrames = ArgInt("--bench-tp-lift-frames", 2, 0, 10);
            double speed = 1.0;
            string[] av = Platform.Args.Argv;
            int sAt = Array.IndexOf(av, "--bench-tp-speed");
            if (sAt >= 0 && sAt + 1 < av.Length && double.TryParse(av[sAt + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sp) && sp > 0) speed = sp;
            // The program's one-way excursion (drag + the first flick's travel and coast until the second + the second's full coast,
            // k = FlingDecayPerS): keep it inside 85 % of the page so the edges stay out of the measurement.
            double k = ScrollTunables.Current.FlingDecayPerS;
            TouchpadGesture[] Program(double x) =>
            [
                TouchpadGesture.Drag(0.00, +1, 900 * x, 0.12, 0.50, 0.25),
                TouchpadGesture.Flicked(1.10, +1, 6000 * x, 0.18),
                TouchpadGesture.Flicked(2.00, +1, 4500 * x, 0.16),
                TouchpadGesture.Drag(3.40, -1, 900 * x, 0.12, 0.50, 0.25),
                TouchpadGesture.Flicked(4.50, -1, 6000 * x, 0.18),
                TouchpadGesture.Flicked(5.40, -1, 4500 * x, 0.16),
            ];
            double Excursion(double x)
            {
                var p = Program(x);
                double coast1 = 6000 * x / k * (1 - Math.Exp(-k * (2.00 - 1.10 - 0.18)));
                return p[0].Travel + p[1].Travel + coast1 + p[2].Travel + 4500 * x / k;
            }
            double room = Math.Max(0, s.MaxOffset) * 0.85;
            if (Excursion(speed) > room && room > 0) speed *= room / Excursion(speed);
            var tp = new SyntheticTouchpad(Program(speed), 6.8, liftFrames, new Point2(vi.X + vi.W * 0.5f, vi.Y + vi.H * 0.5f));
            var tap = new ProbeTap(vi.NodeIndex);
            ProbeLevel level0 = ScrollProbe.Level;
            ScrollProbe.Level = ProbeLevel.Trace;
            s_probeTap = tap;
            w.SyntheticScrollProducer = tp;
            long windowQpc = 0;
            tp.Start(Stopwatch.GetTimestamp());
            // The driver only wakes the loop at each contact's start (the producer's Live keeps frames coming while a contact runs);
            // once a start is due the producer begins it on the next produced frame, so an overdue start is re-checked 10 ms later.
            var driver = new Driver(now => now + Math.Max(0.010, (tp.NextStartQpc - Stopwatch.GetTimestamp()) / freq));
            Pending r;
            try
            {
                r = MeasureWindow(name, host, w, o, driver, "viewport=" + s.Key, s, null, () =>
                {
                    tap.Drain();
                    windowQpc = Stopwatch.GetTimestamp();
                    tp.Restart(windowQpc + (long)(0.05 * freq));   // every run measures the program from its start
                    driver.Next = 0;                                 // re-read the restarted schedule now
                });
            }
            finally
            {
                w.SyntheticScrollProducer = null;
                tap.Drain();
                s_probeTap = null;
                ScrollProbe.Level = level0;
            }
            Pump(host, w, 2.5, null);   // let the last coast settle before the next scenario
            WriteTouchpadCsv(name, tp, windowQpc, vi, speed, liftFrames, s.MaxOffset);
            WriteProbeCsv(name, tap, windowQpc, vi);
            var extra = r.Extra ?? [];
            extra.Add(new("tpSpeed", speed));
            extra.Add(new("tpLiftFrames", liftFrames));
            extra.Add(new("tpContacts", tp.Ends));
            extra.Add(new("tpSamples", tp.Samples));
            extra.Add(new("tpProbeLost", tap.Lost));
            return r with { Note = r.Note + " tpSpeed=" + speed.ToString("0.###", CultureInfo.InvariantCulture) + " contacts=" + tp.Ends, Extra = extra };
        }

        static void WriteTouchpadCsv(string name, SyntheticTouchpad tp, long windowQpc, ViewportInfo vi, double speed, int liftFrames, double maxOffset)
        {
            double f = Stopwatch.Frequency / 1000.0;
            var sb = new StringBuilder(96 * tp.Pumps.Count + 512);
            sb.Append("# qpc_hz=").Append(Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture))
              .Append(" window_ms=").Append((windowQpc / f).ToString("0.000", CultureInfo.InvariantCulture))
              .Append(" vp=").Append(vi.NodeIndex.ToString(CultureInfo.InvariantCulture))
              .Append(" key=").Append((vi.ScrollKey ?? "-").Replace(' ', '_'))
              .Append(" speed=").Append(speed.ToString("0.###", CultureInfo.InvariantCulture))
              .Append(" lift_frames=").Append(liftFrames.ToString(CultureInfo.InvariantCulture))
              .Append(" max_offset=").Append(maxOffset.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("now_ms,lead_ms,stamp_ms,present_ms,frame_ms,gesture,finger_now,finger_target,emitted,delta\n");
            foreach (var p in tp.Pumps)
                sb.Append((p.NowQpc / f).ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.LeadMs.ToString("0", CultureInfo.InvariantCulture)).Append(',')
                  .Append((p.StampQpc / f).ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                  .Append((p.PresentQpc / f).ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                  .Append((p.FrameQpc / f).ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.Gesture.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.FingerNow.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.FingerTarget.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.Emitted).Append(',')
                  .Append(p.Delta.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
            WriteArtifact(OutDir(), name + "-touchpad.csv", sb.ToString());
        }

        static void WriteProbeCsv(string name, ProbeTap tap, long windowQpc, ViewportInfo vi)
        {
            double f = Stopwatch.Frequency / 1000.0;
            var sb = new StringBuilder(120 * tap.Rows.Count + 256);
            sb.Append("# qpc_hz=").Append(Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture))
              .Append(" window_ms=").Append((windowQpc / f).ToString("0.000", CultureInfo.InvariantCulture))
              .Append(" vp=").Append(vi.NodeIndex.ToString(CultureInfo.InvariantCulture))
              .Append(" lost=").Append(tap.Lost.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("kind,qpc_ms,vp,b0,b1,i0,i1,d0,d1,d2,f0,f1,seq\n");
            foreach (var r in tap.Rows)
                sb.Append(r.Kind.ToString()).Append(',').Append((r.Qpc / f).ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.Vp.ToString(CultureInfo.InvariantCulture)).Append(',').Append(r.B0.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.B1.ToString(CultureInfo.InvariantCulture)).Append(',').Append(r.I0.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.I1.ToString(CultureInfo.InvariantCulture)).Append(',').Append(r.D0.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.D1.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(r.D2.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.F0.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(r.F1.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.Seq.ToString(CultureInfo.InvariantCulture)).Append('\n');
            WriteArtifact(OutDir(), name + "-probe.csv", sb.ToString());
        }
    }
}
