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

        /// <summary>`pointer path=[...] film=<png>`: the same walk, filmed. The glide runs inside film's trace (one move at the start of each traced
        /// frame), so the sheet and the keys' curves show the hover's motion as the pointer makes it, a fast sweep included. A key waypoint is
        /// aimed when its leg starts (a tile that widened under an earlier leg is aimed where it now stands).</summary>
        string PointerFilm(int id, string name, Inspect.Args a, List<string> path, int steps, int defaultDwell, bool ease, string film)
        {
            Shell.HeadlessShell shell = _shell!;
            var targets = new List<(string Target, int Dwell)>(path.Count);
            int total = 0;
            foreach (string raw in path)
            {
                string target = raw;
                int dwell = defaultDwell;
                int at = raw.LastIndexOf('@');
                if (at > 0 && int.TryParse(raw.AsSpan(at + 1), out int ms)) { target = raw[..at]; dwell = Math.Clamp(ms, 0, 60_000); }
                int frames = DwellFrames(dwell);
                targets.Add((target, frames));
                total += steps + frames;
            }
            total += Math.Clamp(a.Int("tail", 30), 0, 600);
            int leg = 0, inLeg = 0;
            Point2 from = default, to = default;
            string? refused = null;
            _traceFrameHook = f =>
            {
                if (leg >= targets.Count) return;
                var (target, dwell) = targets[leg];
                if (inLeg == 0)
                {
                    RectF win = InspectKit.WindowRect(shell);
                    from = _pointer ?? new Point2(win.X + win.W / 2f, win.Y + win.H / 2f);
                    if (target == "leave") to = new Point2(win.X - 40f, win.Y - 40f);
                    else if (target.StartsWith("at:", StringComparison.Ordinal))
                    {
                        string[] xy = target[3..].Split(':');
                        if (xy.Length == 2 && float.TryParse(xy[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                            && float.TryParse(xy[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)) to = new Point2(x, y);
                        else { refused ??= "bad waypoint '" + target + "'"; to = from; }
                    }
                    else
                    {
                        var aimArgs = new Inspect.Args();
                        aimArgs.Set("key", new Inspect.Value(Inspect.ValueKind.String, target));
                        aimArgs.Set("stableMax", new Inspect.Value(Inspect.ValueKind.Number, Num: 0));
                        aimArgs.Set("force", new Inspect.Value(Inspect.ValueKind.Bool, Bool: true));   // a moving target mid-film: aim, never refuse
                        if (TryAim(id, name, aimArgs, wheel: false, out Aim aim, out string refusal)) to = aim.Point;
                        else { refused ??= target + ": " + refusal; to = from; }
                    }
                }
                if (inLeg < steps)
                {
                    float t = (float)(inLeg + 1) / steps;
                    if (ease) t = t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;
                    Move(new Point2(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t));
                    if (inLeg == steps - 1) _pointer = target == "leave" ? null : to;
                }
                if (++inLeg >= steps + dwell) { leg++; inLeg = 0; }
            };
            a.Set("path", new Inspect.Value(Inspect.ValueKind.String, film));
            a.Set("frames", new Inspect.Value(Inspect.ValueKind.Number, Num: total));
            if (!a.Has("every")) a.Set("every", new Inspect.Value(Inspect.ValueKind.Number, Num: 2));
            if (!a.Has("cols")) a.Set("cols", new Inspect.Value(Inspect.ValueKind.Number, Num: 6));
            try
            {
                string reply = Film(id, name, a, shell);
                return refused is null ? reply : Inspect.Reply.Fail(id, name, "a waypoint was refused: " + refused);
            }
            finally { _traceFrameHook = null; }
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
            if (a.Str("film") is { Length: > 0 } film) return PointerFilm(id, name, a, path, steps, defaultDwell, ease, film);
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
