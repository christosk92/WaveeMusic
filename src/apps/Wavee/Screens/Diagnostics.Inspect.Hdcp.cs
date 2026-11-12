// ── Screens/Diagnostics.Inspect.Hdcp.cs ─────────────────────────────────────────────────────────────────────────────────
// `state kind=hdcp hdcp=blocked|clear`: the video's screen judged by the HDCP guard, without a real screen or a real PlayReady session
//
// Role: SHELL (UI thread; drives Video.Hdcp)
//
// THE PATH. The headless server has no monitor behind a dock and no protected picture, so the guard (`Video.Hdcp`) never sees a failure.
// This hands it the verdict a real screen would give, through its ONE transition: `blocked` is the safety net's drop (the block, the
// reducer's post that moves a protected row to its song, `JoinVisual.ScreenBlocked` on every video surface), `clear` is the move to a
// screen that carries HDCP (the block lifts and the row goes back to its video). Mount with `video=docked|fullscreen|floating` first so a
// surface is up to show it. Nothing is persisted; the next `mount` (or `reset`) puts the boot's guard back.

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`state kind=hdcp hdcp=blocked|clear`.</summary>
        string HdcpState(int id, string name, Inspect.Args a)
        {
            string v = a.Has("hdcp") ? a.Str("hdcp").ToLowerInvariant() : "blocked";
            if (v is not ("blocked" or "clear")) return Inspect.Reply.Fail(id, name, "state kind=hdcp wants hdcp=blocked|clear, not '" + v + "'");
            Video.Hdcp.InspectVerdict(v == "blocked");
            if (!a.Has("trace") && a.Bool("settle", true)) RunFrames(30);
            return Settled(id, name, a, w =>
            {
                w.WriteString("kind", "hdcp");
                w.WriteString("hdcp", v);
                w.WriteBoolean("blocked", Video.Hdcp.Blocked.Peek());
                w.WriteString("join", Video.JoinNow.Peek().ToString());
            });
        }
    }
}
