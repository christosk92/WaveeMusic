// ── Screens/Diagnostics.Drive.cs — the live driver: wavee://diag?cmd=drive replays an inspector script on the REAL window ──────
//
// The feature recorder (ops/release/tools/Record-Feature.ps1) films a VERIFY Wavee on the owner's real account, so nothing
// in the picture is a fixture. What moves in it is a script: the inspector's own pointer grammar (`pointer path=[k@ms, at:x:y@ms,
// leave@ms] steps=N`, `hover key=<k> dwell=ms`, `click`, `key`, `scroll`, `resize`, `frames`, `settle`), read from a file
// and replayed here on the wall clock, one real input event per presented frame through the window's own input queue
// (FluentAppHarness.QueueInput: the queue the WndProc feeds, so hit testing, hover, rest-to-open timers and previews run as
// for a mouse). The OS cursor never moves (no SendInput): the driver's pointer is drawn by the app (DriveCursor, below).
//
// TIME. The inspector runs on a 16 ms frame clock; here a `steps`/`frames` count keeps that duration (N x 16.67 ms) and the
// pointer is placed on every frame the display presents in between, so a 120 Hz panel gets 120 moves a second. A step starts
// on the frame after the previous one ended. Settles are explicit: live verbs do not settle unless `settle=true` (a looping
// canvas or marquee never settles; the wall clock is the script's).
//
// LIVE-ONLY LINES start with `#live ` (the inspector reads them as comments, so one script runs in both):
//   #live ready key=<k> [images=true] [video=false] [quiet=300] [max=20000]   wait until the key is shown, its covers decoded,
//          no skeleton pending under it, (video=true) every video surface under it bound and presenting, and its rect still
//          for `quiet` ms. A miss after `max` fails the drive (a recording never shows a loading state it did not ask for).
//   #live reveal key=<k> [top=96] [vp=<scroll prefix>] [ms=900]   glide the scroller so the key's top sits `top` DIP below
//          the viewport's top, then wait for the glide to end.
//   #live wait ms=<n>                                                rest.
// Inspector-only verbs that observe (mount, perf, film, render, lint, status, find, dump, tiers, trace, said, views, crossing,
// crossings, help) are skipped; any other verb the live driver does not play fails the drive at load, before anything moves.
//
// OUTPUT: logs\evidence\drive\<yyyyMMdd-HHmmss>-<tag>\ steps.tsv (each step's start/end ms, what the pointer hit), frames.tsv
// (every presented frame: ms, gap, the host's frame ms and present counters) and summary.txt; the reply (`drive ok <folder>`
// or `drive failed <line: why>`) lands when the script ends. `cmd=drive&stop=1` ends a running drive.
//
// Role: SHELL (UI thread; the input queue, the hit test and the scene's rects only). Developer-only, like every diag verb.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentGpu;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Diagnostics
{
    public static class Drive
    {
        /// <summary>A drive is running: the cursor overlay is mounted while it holds.</summary>
        public static readonly Signal<bool> Active = new(false);

        /// <summary>The drawn pointer's pose (window DIP translation; a press scales it a little).</summary>
        public static readonly Signal<Affine2D> CursorPose = new(Affine2D.Translation(-64f, -64f));

        /// <summary>The drawn pointer's opacity: 0 before the first move and after a `leave`.</summary>
        public static readonly Signal<float> CursorOpacity = new(0f);

        const double InspectorFrameMs = 1000.0 / 60.0;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        static readonly HashSet<string> s_observers = new(StringComparer.Ordinal)
        {
            "mount", "perf", "film", "render", "lint", "status", "find", "dump", "tiers", "trace", "said", "views", "crossing", "crossings", "help",
        };

        static Run? s_run;
        static bool s_attached;
        static Timer? s_timer;
        static readonly Action<FrameStats> s_onFrame = OnFrame;
        static readonly Action s_tickOnUi = static () => Tick(null);

        /// <summary>UI THREAD. `cmd=drive&amp;file=…&amp;tag=…` / `cmd=drive&amp;stop=1`.</summary>
        public static void Apply(in DiagVerb v)
        {
            if (v.On == 0) { Finish("stopped", "stopped by cmd=drive&stop=1"); return; }
            if (s_run is not null) { Evidence.Reply("drive", "busy", s_run.Folder); return; }
            if (Probe.Host is null) { Evidence.Reply("drive", "no-host", ""); return; }
            string text;
            try { text = File.ReadAllText(v.File, Encoding.UTF8); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Evidence.Reply("drive", "failed", Clean("cannot read " + v.File + ": " + ex.Message));
                return;
            }
            if (!TryLoad(text, out List<Line> lines, out string error)) { Evidence.Reply("drive", "failed", Clean(error)); return; }
            string folder = Path.Combine(Evidence.Root, "drive", EvidenceReport.BundleFolderName(DateTime.Now, v.Tag.Length > 0 ? v.Tag : "drive"));
            s_run = new Run(lines, folder, Stopwatch.GetTimestamp());
            Active.Value = true;
            if (!s_attached) { FluentApp.FrameCompleted += s_onFrame; s_attached = true; }
            // A rest renders nothing, so no frame would come back to end it: a 4 ms timer posts a tick through the UI poster
            // (which wakes the loop). Moves are placed from the frame callback, on the frame the display shows.
            s_timer = new Timer(static _ => StoreShot.ToUi(s_tickOnUi), null, 4, 4);
            Log.Info("drive", "[drive] start " + v.File + " lines=" + lines.Count.ToString(Inv) + " -> " + folder);
            Tick(null);
        }

        // ── the script ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One script line: the verb, its args (the inspector's parse and strict check, or the live grammar for `#live`).</summary>
        sealed record Line(int No, string Text, string Verb, Inspect.Args Args);

        static bool TryLoad(string text, out List<Line> lines, out string error)
        {
            lines = [];
            error = "";
            string[] raw = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < raw.Length; i++)
            {
                string t = raw[i].Trim();
                int no = i + 1;
                if (t.StartsWith("#live ", StringComparison.Ordinal))
                {
                    if (!TryParseLive(t[6..], out string verb, out Inspect.Args args, out error)) { error = "line " + no.ToString(Inv) + ": " + error; return false; }
                    lines.Add(new Line(no, t, verb, args));
                    continue;
                }
                if (!Inspect.TryParse(t, out Inspect.Command cmd, out string parseError)) { error = "line " + no.ToString(Inv) + ": " + parseError; return false; }
                if (cmd.IsNone) continue;   // blank or a comment
                if (!Inspect.Validate(in cmd, out string invalid)) { error = "line " + no.ToString(Inv) + ": " + invalid; return false; }
                string name = cmd.Name.ToLowerInvariant();
                if (s_observers.Contains(name)) continue;
                if (name is not ("pointer" or "hover" or "click" or "key" or "scroll" or "resize" or "frames" or "settle"))
                {
                    error = "line " + no.ToString(Inv) + ": '" + cmd.Name + "' does not play live (pointer, hover, click, key, scroll, resize, frames, settle; #live ready, reveal, wait)";
                    return false;
                }
                lines.Add(new Line(no, t, name, cmd.Args));
            }
            return true;
        }

        /// <summary>`verb k=v k=v` (no quoting; a list value is `[a,b]` without spaces).</summary>
        static bool TryParseLive(string text, out string verb, out Inspect.Args args, out string error)
        {
            args = new Inspect.Args();
            error = "";
            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            verb = words.Length > 0 ? words[0].ToLowerInvariant() : "";
            if (verb is not ("ready" or "reveal" or "wait")) { error = "#live " + verb + " is not a live verb (ready, reveal, wait)"; return false; }
            for (int i = 1; i < words.Length; i++)
            {
                int eq = words[i].IndexOf('=');
                if (eq <= 0) { error = "#live " + verb + ": '" + words[i] + "' is not name=value"; return false; }
                string name = words[i][..eq];
                string[] known = verb switch
                {
                    "ready" => ["key", "images", "video", "quiet", "max", "nth"],
                    "reveal" => ["key", "top", "vp", "ms", "nth"],
                    _ => ["ms"],
                };
                if (Array.IndexOf(known, name) < 0) { error = "#live " + verb + ": unknown arg '" + name + "' (" + string.Join(", ", known) + ")"; return false; }
                args.Set(name, Inspect.Value.FromWord(words[i][(eq + 1)..]));
            }
            if (verb is "ready" or "reveal" && !args.Has("key")) { error = "#live " + verb + " wants key="; return false; }
            return true;
        }

        // ── the run ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One primitive the lines expand into, played in order. <see cref="Target"/> is resolved when the op starts
        /// (a key's visible centre where it stands THEN, as a hand aims).</summary>
        enum OpKind : byte { Glide, Rest, Down, Up, Key, Wheel, Resize, Ready, Reveal, Settle }

        sealed class Op
        {
            public OpKind Kind;
            public Line Line = null!;
            public string Target = "";
            public int Nth;
            public double Ms;
            public bool Ease;
            public int Button, Vk;
            public KeyModifiers Mods;
            public float Dx, Dy, W, H, Top;
            public string Vp = "";
            public bool Images = true, Video;
            public double QuietMs = 300, MaxMs = 20_000;
            // live state
            public long Start;
            public Point2 From, To;
            public float W0, H0;
            public bool Begun, Hidden;
            public long StillSince;
            public RectF LastRect;
            public double LastOffset = double.NaN;
            public string Note = "";
        }

        sealed class Run(List<Line> lines, string folder, long start)
        {
            public readonly string Folder = folder;
            public readonly long Start = start;
            public readonly List<Op> Ops = Expand(lines);
            public int Index;
            public Point2? Pointer;
            public bool Down;
            public readonly StringBuilder Steps = new("line\top\tstart_ms\tend_ms\ttarget\tnote\n");
            public readonly StringBuilder Frames = new("frame\tms\tgap_ms\tframe_ms\tpresents\tdropped\trepeated\tmissed_motion\tdwm_dropped\top\n");
            public int FrameCount;
            public long LastFrame;
            public FrameStats? FirstStats, LastStats;
        }

        static List<Op> Expand(List<Line> lines)
        {
            var ops = new List<Op>();
            foreach (Line l in lines)
            {
                Inspect.Args a = l.Args;
                switch (l.Verb)
                {
                    case "pointer":
                    {
                        int steps = Math.Clamp(a.Int("steps", 6), 1, 600);
                        int dwell = Math.Clamp(a.Int("dwell", 0), 0, 60_000);
                        bool ease = a.Str("ease", "linear") == "inout";
                        foreach (string raw in a.List("path"))
                        {
                            string target = raw;
                            int ms = dwell;
                            int at = raw.LastIndexOf('@');
                            if (at > 0 && int.TryParse(raw.AsSpan(at + 1), NumberStyles.Integer, Inv, out int legMs)) { target = raw[..at]; ms = Math.Clamp(legMs, 0, 60_000); }
                            ops.Add(new Op { Kind = OpKind.Glide, Line = l, Target = target, Ms = steps * InspectorFrameMs, Ease = ease });
                            if (ms > 0) ops.Add(new Op { Kind = OpKind.Rest, Line = l, Ms = ms });
                        }
                        break;
                    }
                    case "hover":
                        ops.Add(Aimed(l, a, OpKind.Glide));
                        if (a.Int("dwell", 0) is > 0 and var dwellMs) ops.Add(new Op { Kind = OpKind.Rest, Line = l, Ms = Math.Min(dwellMs, 60_000) });
                        break;
                    case "click":
                    {
                        ops.Add(Aimed(l, a, OpKind.Glide));
                        int button = a.Str("button", "left") == "right" ? 1 : 0;
                        KeyModifiers mods = KeyModifiers.None;
                        if (a.Has("mods") && !Inspect.TryKey(a.Str("mods") + "+a", out _, out mods)) mods = KeyModifiers.None;
                        ops.Add(new Op { Kind = OpKind.Down, Line = l, Button = button, Mods = mods });
                        ops.Add(new Op { Kind = OpKind.Rest, Line = l, Ms = 70 });
                        ops.Add(new Op { Kind = OpKind.Up, Line = l, Button = button, Mods = mods });
                        break;
                    }
                    case "key":
                    {
                        Inspect.TryKey(a.Str("key"), out int vk, out KeyModifiers mods);
                        int repeat = Math.Clamp(a.Int("repeat", 1), 1, 100);
                        for (int i = 0; i < repeat; i++)
                        {
                            ops.Add(new Op { Kind = OpKind.Key, Line = l, Vk = vk, Mods = mods });
                            ops.Add(new Op { Kind = OpKind.Rest, Line = l, Ms = InspectorFrameMs });
                        }
                        break;
                    }
                    case "scroll":
                        ops.Add(Aimed(l, a, OpKind.Glide));
                        ops.Add(new Op { Kind = OpKind.Wheel, Line = l, Dx = a.Float("dx", 0f), Dy = a.Float("dy", 3f) });
                        break;
                    case "resize":
                        ops.Add(new Op { Kind = OpKind.Resize, Line = l, W = a.Float("w", 0f), H = a.Float("h", 0f), Ms = Math.Max(0, a.Int("ms", 0)), Ease = true });
                        break;
                    case "frames":
                        ops.Add(new Op { Kind = OpKind.Rest, Line = l, Ms = Math.Clamp(a.Int("n", 1), 0, 100_000) * InspectorFrameMs });
                        break;
                    case "settle":
                        ops.Add(new Op { Kind = OpKind.Settle, Line = l, MaxMs = Math.Clamp(a.Int("max", 600), 0, 100_000) * InspectorFrameMs });
                        break;
                    case "wait":
                        ops.Add(new Op { Kind = OpKind.Rest, Line = l, Ms = Math.Clamp(a.Int("ms", 0), 0, 600_000) });
                        break;
                    case "ready":
                        ops.Add(new Op
                        {
                            Kind = OpKind.Ready, Line = l, Target = a.Str("key"), Nth = a.Int("nth", 0), Images = a.Bool("images", true), Video = a.Bool("video", false),
                            QuietMs = Math.Clamp(a.Int("quiet", 300), 0, 10_000), MaxMs = Math.Clamp(a.Int("max", 20_000), 0, 600_000),
                        });
                        break;
                    case "reveal":
                        ops.Add(new Op { Kind = OpKind.Reveal, Line = l, Target = a.Str("key"), Nth = a.Int("nth", 0), Top = a.Float("top", 96f), Vp = a.Str("vp"), Ms = Math.Clamp(a.Int("ms", 900), 0, 10_000), MaxMs = 8_000 });
                        break;
                }
                // `settle=true` on an acting verb: the inspector's settle, on the wall clock (live verbs do not settle by default).
                if (l.Verb is "pointer" or "hover" or "click" or "key" or "scroll" or "resize" && a.Bool("settle", false) && a.Contains("settle"))
                    ops.Add(new Op { Kind = OpKind.Settle, Line = l, MaxMs = Math.Clamp(a.Int("max", 600), 0, 100_000) * InspectorFrameMs });
            }
            return ops;
        }

        /// <summary>The pointer's approach to a verb's target: a key (`nth` in reading order) or x,y. `steps` (live: the glide
        /// lasts steps x 16.67 ms; default 1, a jump, as the inspector's hover/click move once).</summary>
        static Op Aimed(Line l, Inspect.Args a, OpKind kind)
        {
            string target = a.Has("key") ? a.Str("key")
                : "at:" + a.Float("x", 0f).ToString(Inv) + ":" + a.Float("y", 0f).ToString(Inv);
            return new Op { Kind = kind, Line = l, Target = target, Nth = a.Int("nth", 0), Ms = Math.Clamp(a.Int("steps", 1), 1, 600) * InspectorFrameMs, Ease = true };
        }

        // ── the clock ──────────────────────────────────────────────────────────────────────────────────────────────

        static void OnFrame(FrameStats stats) => Tick(stats);

        static double MsSince(long start, long now) => (now - start) * 1000.0 / Stopwatch.Frequency;

        static void Tick(FrameStats? stats)
        {
            if (s_run is not { } run) return;
            long now = Stopwatch.GetTimestamp();
            if (stats is { } s && s.Rendered) LogFrame(run, now, s);
            int guard = 0;
            while (run.Index < run.Ops.Count && guard++ < 64)
            {
                Op op = run.Ops[run.Index];
                string? failure = null;
                bool done;
                try { done = Step(run, op, now, ref failure); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
                {
                    failure = ex.Message;
                    done = true;
                }
                if (failure is not null)
                {
                    EndOp(run, op, now);
                    Finish("failed", "line " + op.Line.No.ToString(Inv) + " (" + op.Line.Text + "): " + failure);
                    return;
                }
                if (!done) return;
                EndOp(run, op, now);
                run.Index++;
                if (op.Kind is OpKind.Glide or OpKind.Resize or OpKind.Rest or OpKind.Reveal or OpKind.Ready or OpKind.Settle) return;   // the next timed op starts on the next tick
            }
            if (run.Index >= run.Ops.Count) Finish("ok", "");
        }

        static void EndOp(Run run, Op op, long now)
        {
            if (!op.Begun) return;
            run.Steps.Append(op.Line.No.ToString(Inv)).Append('\t').Append(op.Kind.ToString().ToLowerInvariant()).Append('\t')
                .Append(MsSince(run.Start, op.Start).ToString("0.0", Inv)).Append('\t').Append(MsSince(run.Start, now).ToString("0.0", Inv)).Append('\t')
                .Append(op.Target).Append('\t').Append(op.Note).Append('\n');
        }

        static void LogFrame(Run run, long now, FrameStats s)
        {
            double ms = MsSince(run.Start, now);
            double gap = run.LastFrame == 0 ? 0 : MsSince(run.LastFrame, now);
            run.LastFrame = now;
            run.FirstStats ??= s;
            run.LastStats = s;
            string op = run.Index < run.Ops.Count ? run.Ops[run.Index].Line.No.ToString(Inv) + ":" + run.Ops[run.Index].Kind.ToString().ToLowerInvariant() : "end";
            run.Frames.Append(run.FrameCount++.ToString(Inv)).Append('\t').Append(ms.ToString("0.00", Inv)).Append('\t').Append(gap.ToString("0.00", Inv)).Append('\t')
                .Append(s.FrameMs.ToString("0.00", Inv)).Append('\t').Append(s.PresentsDisplayed.ToString(Inv)).Append('\t').Append(s.PresentsDropped.ToString(Inv)).Append('\t')
                .Append(s.VblanksRepeated.ToString(Inv)).Append('\t').Append(s.RenderMissedMotionTicks.ToString(Inv)).Append('\t').Append(s.DwmDropped.ToString(Inv)).Append('\t')
                .Append(op).Append('\n');
        }

        /// <summary>Advance one op; true when it is done. <paramref name="failure"/> set = the drive fails here.</summary>
        static bool Step(Run run, Op op, long now, ref string? failure)
        {
            AppHost host = Probe.Host ?? throw new InvalidOperationException("the host went away");
            if (!op.Begun) { op.Begun = true; op.Start = now; if (!Begin(run, op, host, ref failure)) return true; }
            double t = MsSince(op.Start, now);
            switch (op.Kind)
            {
                case OpKind.Glide:
                {
                    double f = op.Ms <= 0 ? 1 : Math.Clamp(t / op.Ms, 0, 1);
                    if (op.Ease) f = f < 0.5 ? 4 * f * f * f : 1 - Math.Pow(-2 * f + 2, 3) / 2;   // ease in-out (cubic): a hand's start and stop
                    var p = new Point2(op.From.X + (op.To.X - op.From.X) * (float)f, op.From.Y + (op.To.Y - op.From.Y) * (float)f);
                    MoveTo(run, p, hidden: op.Hidden && f >= 1);
                    if (f < 1) return false;
                    if (!op.Hidden) op.Note = "hit=" + (HitKey(host, op.To) ?? "-");
                    return true;
                }
                case OpKind.Rest:
                    return t >= op.Ms;
                case OpKind.Resize:
                {
                    double f = op.Ms <= 0 ? 1 : Math.Clamp(t / op.Ms, 0, 1);
                    if (op.Ease) f = f < 0.5 ? 4 * f * f * f : 1 - Math.Pow(-2 * f + 2, 3) / 2;
                    float w = op.W0 + (op.W - op.W0) * (float)f, h = op.H0 + (op.H - op.H0) * (float)f;
                    FluentApp.ResizeWindow(MathF.Round(w), MathF.Round(h));
                    return f >= 1;
                }
                case OpKind.Ready:
                {
                    string? why = NotReady(host, op);
                    if (why is null)
                    {
                        if (op.StillSince == 0) op.StillSince = now;
                        if (MsSince(op.StillSince, now) >= op.QuietMs) { op.Note = "ready after " + t.ToString("0", Inv) + " ms"; return true; }
                    }
                    else { op.StillSince = 0; op.Note = why; }
                    if (t > op.MaxMs) failure = "not ready after " + op.MaxMs.ToString("0", Inv) + " ms: " + op.Note;
                    return false;
                }
                case OpKind.Reveal:
                {
                    // done when the scroller's offset has held for 3 frames' worth of time after the glide's own length
                    double offset = ViewportOffset(host, op.Vp);
                    if (!offset.Equals(op.LastOffset)) { op.LastOffset = offset; op.StillSince = now; }
                    if (t >= op.Ms && MsSince(op.StillSince, now) >= 50) return true;
                    if (t > op.MaxMs) failure = "the scroller did not come to rest";
                    return false;
                }
                case OpKind.Settle:
                    return !host.Animation.HasActive || t >= op.MaxMs;
                default:
                    return true;
            }
        }

        /// <summary>An op's first tick: resolve its target and fire what is instantaneous. False when the op is finished
        /// (instant ops) or failed.</summary>
        static bool Begin(Run run, Op op, AppHost host, ref string? failure)
        {
            RectF win = WindowRect(host);
            switch (op.Kind)
            {
                case OpKind.Glide:
                {
                    op.From = run.Pointer ?? new Point2(win.X + win.W / 2f, win.Y + win.H / 2f);
                    if (op.Target == "leave") { op.To = new Point2(win.X - 40f, win.Y - 40f); op.Hidden = true; return true; }
                    if (op.Target.StartsWith("at:", StringComparison.Ordinal))
                    {
                        string[] xy = op.Target[3..].Split(':');
                        if (xy.Length != 2 || !float.TryParse(xy[0], NumberStyles.Float, Inv, out float x) || !float.TryParse(xy[1], NumberStyles.Float, Inv, out float y))
                        { failure = "bad waypoint '" + op.Target + "' (at:x:y in window DIP)"; return false; }
                        op.To = new Point2(x, y);
                        return true;
                    }
                    if (!TryAim(host, op.Target, op.Nth, out Point2 aim, out string why)) { failure = why; return false; }
                    op.To = aim;
                    return true;
                }
                case OpKind.Down:
                {
                    Point2 p = run.Pointer ?? new Point2(win.W / 2f, win.H / 2f);
                    op.Target = HitKey(host, p) ?? "-";
                    FluentAppHarness.QueueInput(new InputEvent(InputKind.PointerDown, p, op.Button, 0, op.Mods, TimestampMs: NowMs()));
                    run.Down = true;
                    CursorPose.Value = Pose(p, pressed: true);
                    return false;
                }
                case OpKind.Up:
                {
                    Point2 p = run.Pointer ?? new Point2(win.W / 2f, win.H / 2f);
                    FluentAppHarness.QueueInput(new InputEvent(InputKind.PointerUp, p, op.Button, 0, op.Mods, TimestampMs: NowMs()));
                    run.Down = false;
                    CursorPose.Value = Pose(p, pressed: false);
                    return false;
                }
                case OpKind.Key:
                    FluentAppHarness.QueueInput(new InputEvent(InputKind.Key, default, 0, op.Vk, op.Mods, TimestampMs: NowMs()));
                    FluentAppHarness.QueueInput(new InputEvent(InputKind.KeyUp, default, 0, op.Vk, op.Mods, TimestampMs: NowMs()));
                    op.Target = op.Line.Args.Str("key");
                    return false;
                case OpKind.Wheel:
                {
                    Point2 p = run.Pointer ?? new Point2(win.W / 2f, win.H / 2f);
                    var wheel = new ScrollInputEvent(ScrollSource.MouseWheel, ScrollGesture.Notch, 0, p, op.Dx, op.Dy, 0, KeyModifiers.None);
                    FluentAppHarness.QueueInput(InputEvent.ForScroll(in wheel, timestampMs: NowMs()));
                    op.Target = HitKey(host, p) ?? "-";
                    return false;
                }
                case OpKind.Resize:
                    if (!(op.W >= 1f && op.H >= 1f)) { failure = "resize wants w and h (DIP)"; return false; }
                    op.W0 = win.W; op.H0 = win.H;
                    op.Target = op.W.ToString(Inv) + "x" + op.H.ToString(Inv);
                    return true;
                case OpKind.Reveal:
                {
                    if (!TryFind(host, op.Target, op.Nth, out NodeHandle node)) { failure = "no shown node keyed '" + op.Target + "'"; return false; }
                    if (!TryViewportOf(host, node, op.Vp, out ViewportInfo vp, out ScrollHandle? handle)) { failure = "no scroller holds '" + op.Target + "'" + (op.Vp.Length > 0 ? " under vp=" + op.Vp : ""); return false; }
                    RectF r = host.Scene.PresentedAbsoluteRect(node);
                    double to = Math.Max(0.0, vp.Offset + (r.Y - vp.Y) - op.Top);
                    op.Vp = vp.ScrollKey ?? op.Vp;
                    op.Note = "vp=" + op.Vp + " to=" + to.ToString("0", Inv);
                    handle!.ScrollTo(to, ScrollMove.Glide);
                    return true;
                }
                default:
                    return true;
            }
        }

        static uint NowMs() => (uint)Environment.TickCount64;

        static void MoveTo(Run run, Point2 p, bool hidden)
        {
            if (run.Pointer is { } prev && prev.X == p.X && prev.Y == p.Y && !hidden) return;
            run.Pointer = hidden ? null : p;
            FluentAppHarness.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0, TimestampMs: NowMs()));
            CursorPose.Value = Pose(p, run.Down);
            float opacity = hidden ? 0f : 1f;
            if (CursorOpacity.Peek() != opacity) CursorOpacity.Value = opacity;
        }

        /// <summary>The drawn pointer's pose: its tip on <paramref name="p"/>; pressed draws it 8 % smaller about the tip.</summary>
        static Affine2D Pose(Point2 p, bool pressed)
        {
            const float tip = 1f;   // the arrow's tip in its own box (DriveCursor's path starts at 1,1)
            float s = pressed ? 0.92f : 1f;
            return new Affine2D(s, 0, 0, s, p.X - s * tip, p.Y - s * tip);
        }

        // ── the scene ──────────────────────────────────────────────────────────────────────────────────────────────

        static RectF WindowRect(AppHost host)
        {
            NodeHandle root = host.Scene.Root;
            return root.IsNull ? default : host.Scene.Bounds(root) with { X = 0f, Y = 0f };
        }

        /// <summary>The <paramref name="nth"/> SHOWN node keyed exactly <paramref name="key"/> (reading order: top, then left).</summary>
        static bool TryFind(AppHost host, string key, int nth, out NodeHandle node)
        {
            SceneStore scene = host.Scene;
            var keyed = new List<(NodeHandle Node, string Key)>();
            scene.CopyDebugKeys(keyed);
            keyed.RemoveAll(e => !string.Equals(e.Key, key, StringComparison.Ordinal) || !scene.IsShown(e.Node));
            keyed.Sort((a, b) =>
            {
                RectF ra = scene.PresentedAbsoluteRect(a.Node), rb = scene.PresentedAbsoluteRect(b.Node);
                int y = MathF.Round(ra.Y).CompareTo(MathF.Round(rb.Y));
                return y != 0 ? y : ra.X.CompareTo(rb.X);
            });
            node = nth < keyed.Count ? keyed[nth].Node : NodeHandle.Null;
            return !node.IsNull;
        }

        /// <summary>Where a hand aims at a key: the centre of its part inside the window. Refused when it is not shown or has no
        /// visible part. What the pointer lands on is written to steps.tsv (`hit=`), so a covered target shows there.</summary>
        static bool TryAim(AppHost host, string key, int nth, out Point2 p, out string why)
        {
            p = default;
            if (!TryFind(host, key, nth, out NodeHandle node)) { why = "no shown node keyed '" + key + "'" + (nth > 0 ? " (nth=" + nth.ToString(Inv) + ")" : ""); return false; }
            RectF r = host.Scene.PresentedAbsoluteRect(node), win = WindowRect(host);
            float x0 = MathF.Max(r.X, win.X), y0 = MathF.Max(r.Y, win.Y), x1 = MathF.Min(r.X + r.W, win.X + win.W), y1 = MathF.Min(r.Y + r.H, win.Y + win.H);
            if (x1 - x0 < 2f || y1 - y0 < 2f) { why = "'" + key + "' is off the window"; return false; }
            p = new Point2((x0 + x1) / 2f, (y0 + y1) / 2f);
            why = "";
            return true;
        }

        static string? HitKey(AppHost host, Point2 p)
        {
            NodeHandle hit = host.Input.HitTest(p);
            for (NodeHandle n = hit; !n.IsNull && host.Scene.IsLive(n); n = host.Scene.Parent(n))
                if (host.Scene.DebugKeyOf(n) is { Length: > 0 } key) return key;
            return null;
        }

        /// <summary>Null when the key is ready to be filmed, else what it still waits for.</summary>
        static string? NotReady(AppHost host, Op op)
        {
            if (!TryFind(host, op.Target, op.Nth, out NodeHandle root)) return "not shown";
            SceneStore scene = host.Scene;
            RectF r = scene.PresentedAbsoluteRect(root);
            bool moved = r != op.LastRect;
            op.LastRect = r;
            if (moved) return "moving";
            ImageCache? images = FluentApp.EngineImages;
            var stack = new Stack<NodeHandle>();
            stack.Push(root);
            int videos = 0;
            while (stack.Count > 0)
            {
                NodeHandle n = stack.Pop();
                if (n.IsNull || !scene.IsLive(n) || !scene.IsShown(n)) continue;
                if (NodeDescriber.ElementTypeName(scene.ElementTypeId(n)) == "SkeletonRegion" && host.IsSkeletonPending(n)) return "skeleton";
                ref NodePaint paint = ref scene.Paint(n);
                if (op.Images && paint.VisualKind == VisualKind.Image && paint.ImageId > 0 && images is not null)
                {
                    ImageState state = images.StateOf(new ImageHandle(paint.ImageId));
                    if (state is ImageState.None or ImageState.Pending) return "image decoding";
                }
                if (op.Video && paint.VisualKind == VisualKind.Video)
                {
                    videos++;
                    if (!VideoBound(host, (uint)paint.ImageId)) return "video not presenting";
                }
                for (NodeHandle c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
            }
            if (op.Video && videos == 0) return "no video under the key";
            return null;
        }

        /// <summary>A video surface id whose slot holds a bound, committed presenter surface (the registry's per-slot readiness).</summary>
        static bool VideoBound(AppHost host, uint surfaceId)
        {
            if (surfaceId == 0) return false;
            var registry = host.VideoSurfaces;
            for (int token = 1; token <= 16; token++)
                if (registry.Surface(token).Peek().Value == surfaceId) return registry.Bound(token).Peek();
            return false;
        }

        static bool TryViewportOf(AppHost host, NodeHandle node, string prefix, out ViewportInfo found, out ScrollHandle? handle)
        {
            found = default;
            handle = null;
            var vps = new List<ViewportInfo>();
            host.CopyViewports(vps);
            SceneStore scene = host.Scene;
            foreach (ViewportInfo v in vps)
            {
                if (v.Horizontal) continue;
                if (prefix.Length > 0 && !EvidenceReport.ViewportMatches(v.ScrollKey, prefix)) continue;
                NodeHandle vpNode = scene.HandleAt(v.NodeIndex);
                bool holds = false;
                for (NodeHandle n = node; !n.IsNull; n = scene.Parent(n)) if (n == vpNode) { holds = true; break; }
                if (!holds) continue;
                ScrollHandle? h = host.TryGetScrollHandle(vpNode);
                if (h is null) continue;
                found = v;
                handle = h;
                // the innermost vertical scroller that holds the node wins: keep looking for a deeper one
            }
            return handle is not null;
        }

        static double ViewportOffset(AppHost host, string scrollKey)
        {
            var vps = new List<ViewportInfo>();
            host.CopyViewports(vps);
            foreach (ViewportInfo v in vps) if (v.ScrollKey == scrollKey) return v.Offset;
            return double.NaN;
        }

        // ── the end ────────────────────────────────────────────────────────────────────────────────────────────────

        static void Finish(string result, string why)
        {
            if (s_run is not { } run) { if (result == "stopped") Evidence.Reply("drive", "idle", ""); return; }
            s_run = null;
            s_timer?.Dispose();
            s_timer = null;
            if (s_attached) { FluentApp.FrameCompleted -= s_onFrame; s_attached = false; }
            if (run.Down && run.Pointer is { } p) FluentAppHarness.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: NowMs()));
            Active.Value = false;
            CursorOpacity.Value = 0f;
            double total = MsSince(run.Start, Stopwatch.GetTimestamp());
            var summary = new StringBuilder();
            summary.Append("result\t").Append(result).Append('\n');
            if (why.Length > 0) summary.Append("why\t").Append(why).Append('\n');
            summary.Append("ms\t").Append(total.ToString("0", Inv)).Append('\n');
            summary.Append("frames\t").Append(run.FrameCount.ToString(Inv)).Append('\n');
            if (run.FirstStats is { } a && run.LastStats is { } b)
            {
                summary.Append("presentsDisplayed\t").Append((b.PresentsDisplayed - a.PresentsDisplayed).ToString(Inv)).Append('\n');
                summary.Append("presentsDropped\t").Append((b.PresentsDropped - a.PresentsDropped).ToString(Inv)).Append('\n');
                summary.Append("vblanksRepeated\t").Append((b.VblanksRepeated - a.VblanksRepeated).ToString(Inv)).Append('\n');
                summary.Append("renderMissedMotionTicks\t").Append((b.RenderMissedMotionTicks - a.RenderMissedMotionTicks).ToString(Inv)).Append('\n');
            }
            try
            {
                Directory.CreateDirectory(run.Folder);
                File.WriteAllText(Path.Combine(run.Folder, "steps.tsv"), run.Steps.ToString(), Utf8);
                File.WriteAllText(Path.Combine(run.Folder, "frames.tsv"), run.Frames.ToString(), Utf8);
                File.WriteAllText(Path.Combine(run.Folder, "summary.txt"), summary.ToString(), Utf8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("drive", "[drive] write failed", ex);
            }
            Log.Info("drive", "[drive] " + result + " " + why + " -> " + run.Folder);
            Evidence.Reply("drive", result, result == "ok" ? run.Folder : Clean(why) + " | " + run.Folder);
        }

        static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }
}
