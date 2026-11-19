// ── Screens/Diagnostics.Inspect.Drag.cs ────────────────────────────────────────────────────────────────────────────────
// the `drag` verb: a mouse drag as one command (press on a node or a point, move over N frames, release), then settle
//
// Role: SHELL (UI thread)
// Spec: handoff/audit-2026-10-23/FIX-PLAN.md lane A2 item 10 (motion.md §6 tooling 14: a drag existed only inside `trace`).
//
// THE GESTURE, as a user's hand makes it: the pointer moves onto the press point, the button goes down (one frame), the pointer
// travels `dx`,`dy` in `steps` equal moves, one per frame (the button held), and the button comes up at the end point. The press
// point is `key`'s visible centre (refused unless actionable and still, as `click` aims) or `x`,`y`. `trace=N` traces N frames from
// the release (a drop's settle animation); to trace the frames of the drag itself, `trace` takes `drag=` (see agent-inspect.md).

using FluentGpu.Foundation;
using FluentGpu.Pal;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`drag {key | x,y, dx?, dy?, steps?, button?, mods?, nth?, force?, stableMax?, + the settle and trace args}`.</summary>
        string Drag(int id, string name, Inspect.Args a)
        {
            if (!TryMods(a, out KeyModifiers mods)) return Inspect.Reply.Fail(id, name, "bad value '" + a.Str("mods") + "' for mods (ctrl, shift, alt or win, joined by +: ctrl+shift)");
            float dx = a.Float("dx", 0f), dy = a.Float("dy", 0f);
            string to = a.Str("to");
            if (dx == 0f && dy == 0f && to.Length == 0) return Inspect.Reply.Fail(id, name, "drag wants dx or dy (DIP the pointer travels) or to=<key>");
            int steps = Math.Clamp(a.Int("steps", 8), 1, 600);
            bool touch = a.Str("pointer", "mouse") == "touch";
            var kind = touch ? PointerKind.Touch : PointerKind.Mouse;
            uint pid = touch ? 1u : 0u;
            if (!TryAim(id, name, a, wheel: false, out Aim aim, out string refusal)) return refusal;
            int button = a.Str("button", "left") == "right" ? 1 : 0;
            Point2 from = aim.Point;
            if (to.Length > 0)
            {
                // `to=<key>`: the end point is that node's presented centre (the first match in reading order).
                var targets = InspectDump.Exact(_shell!, to);
                if (targets.Count == 0) return Inspect.Reply.Fail(id, name, NoNodeKeyed(to));
                InspectDump.InReadingOrder(_shell!.Host.Scene, targets);
                Point2 c = InspectKit.CentreOf(_shell.Host.Scene.PresentedAbsoluteRect(targets[0].Node));
                dx = c.X - from.X;
                dy = c.Y - from.Y;
            }
            if (!touch) { Move(from); RunFrames(1); }
            _hit = HitKey(from);   // what the press lands on
            Queue(new InputEvent(InputKind.PointerDown, from, button, 0, mods, kind, TimestampMs: _ms, PointerId: pid));
            RunFrames(1);
            int holdMs = a.Int("holdMs", 0), pauseMs = a.Int("pauseMs", 0);
            if (holdMs > 0) RunFrames((holdMs + 15) / 16);   // still, button down: a touch lift's hold
            Point2 at = from;
            for (int i = 1; i <= steps; i++)
            {
                at = new Point2(from.X + dx * i / steps, from.Y + dy * i / steps);
                Queue(new InputEvent(InputKind.PointerMove, at, 0, 0, mods, kind, TimestampMs: _ms, PointerId: pid));   // the button stays down
                RunFrames(1);
            }
            if (pauseMs > 0) RunFrames((pauseMs + 15) / 16);   // the dwell at the end, before the release
            Queue(new InputEvent(InputKind.PointerUp, at, button, 0, mods, kind, TimestampMs: _ms, PointerId: pid));
            if (!Tracing(a)) RunFrames(1);   // `trace=N` starts at the frame the release lands on
            if (!touch) _pointer = at;
            Point2 end = at;
            return Settled(id, name, a, w =>
            {
                WriteAim(w, in aim);
                w.WriteStartObject("to");
                Inspect.Reply.Num(w, "x", end.X);
                Inspect.Reply.Num(w, "y", end.Y);
                w.WriteEndObject();
                w.WriteNumber("steps", steps);
            });
        }
    }
}
