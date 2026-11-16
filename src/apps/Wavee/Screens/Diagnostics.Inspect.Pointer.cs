// ── Screens/Diagnostics.Inspect.Pointer.cs ─────────────────────────────────────────────────────────────────────────────
// `pointer path=[...]`: pointer realism for hover-driven UI. The pointer glides from where it is to each waypoint over `steps` frames
// (one real PointerMove per frame through the window's input queue, the very pipeline a mouse feeds: hover enter and exit, rest timers,
// hover previews), rests there for the waypoint's dwell on the 16 ms clock, and goes on; `leave` moves it off the window (the dispatcher's
// pointer-left-window edge). `hover ... dwell=ms` is the one-target form. Then the verb settles (or traces: trace=N).
//
// Role: SHELL (UI thread; the input queue and the hit test only)
// Spec: docs/guide/agent-inspect.md "pointer"; the skill's verify/debug-home.md is the recipe.

using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Pal;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>Frames a dwell of <paramref name="ms"/> rests on the 16 ms clock (0 for 0).</summary>
        static int DwellFrames(int ms) => ms <= 0 ? 0 : (ms + 15) / 16;

        /// <summary>Glide the pointer from where it is (the window's centre when it has gone nowhere) to <paramref name="to"/>: one move
        /// per frame over <paramref name="steps"/> frames, the last one on the target.</summary>
        void Glide(Point2 to, int steps, bool ease = false)
        {
            Shell.HeadlessShell shell = _shell!;
            RectF win = InspectKit.WindowRect(shell);
            Point2 from = _pointer ?? new Point2(win.X + win.W / 2f, win.Y + win.H / 2f);
            steps = Math.Max(1, steps);
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                if (ease) t = t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;   // cubic in-out, the live driver's pace
                Move(new Point2(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t));
                RunFrames(1);
            }
            _pointer = to;
        }

        /// <summary>`pointer path=[a, b@700, at:120:300@0, leave@200] steps=6`.</summary>
        string Pointer(int id, string name, Inspect.Args a)
        {
            Shell.HeadlessShell shell = _shell!;
            List<string> path = a.List("path");
            if (path.Count == 0) return Inspect.Reply.Fail(id, name, "pointer wants path=[target@dwellMs, ...] (a key, at:x:y or leave)");
            int steps = Math.Clamp(a.Int("steps", 6), 1, 600);
            int defaultDwell = Math.Clamp(a.Int("dwell", 0), 0, 60_000);
            bool ease = a.Str("ease", "linear") == "inout";
            var legs = new List<(string Target, Point2 At, string? Hit, int Frames, string? Note)>();
            foreach (string raw in path)
            {
                string target = raw;
                int dwell = defaultDwell;
                int at = raw.LastIndexOf('@');
                if (at > 0 && int.TryParse(raw.AsSpan(at + 1), out int ms)) { target = raw[..at]; dwell = Math.Clamp(ms, 0, 60_000); }
                Point2 p;
                string? note = null;
                if (target == "leave")
                {
                    RectF win = InspectKit.WindowRect(shell);
                    p = new Point2(win.X - 40f, win.Y - 40f);   // off the window: the pointer-left-window edge
                }
                else if (target.StartsWith("at:", StringComparison.Ordinal))
                {
                    string[] xy = target[3..].Split(':');
                    if (xy.Length != 2 || !float.TryParse(xy[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                        || !float.TryParse(xy[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
                        return Inspect.Reply.Fail(id, name, "bad waypoint '" + raw + "' (at:x:y in window DIP)");
                    p = new Point2(x, y);
                }
                else
                {
                    var leg = new Inspect.Args();
                    leg.Set("key", new Inspect.Value(Inspect.ValueKind.String, target));
                    leg.Set("stableMax", new Inspect.Value(Inspect.ValueKind.Number, Num: 0));   // a glide never waits for a still target
                    if (!TryAim(id, name, leg, wheel: false, out Aim aim, out string refusal)) return refusal;   // names the target and why
                    p = aim.Point;
                    note = aim.Note.Length > 0 ? aim.Note : null;
                }
                Glide(p, steps, ease);
                string? hit = target == "leave" ? null : HitKey(p);
                if (target == "leave") _pointer = null;
                int frames = DwellFrames(dwell);
                RunFrames(frames);
                legs.Add((target, p, hit, steps + frames, note));
            }
            return Settled(id, name, a, w =>
            {
                w.WriteStartArray("legs");
                foreach (var (target, p, hit, frames, note) in legs)
                {
                    w.WriteStartObject();
                    w.WriteString("target", target);
                    Inspect.Reply.Num(w, "x", p.X);
                    Inspect.Reply.Num(w, "y", p.Y);
                    if (hit is null) w.WriteNull("hit"); else w.WriteString("hit", hit);
                    w.WriteNumber("frames", frames);
                    if (note is not null) w.WriteString("note", note);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }
    }
}
