using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static Wavee.HeroMotion;

namespace Wavee;

/// <summary>
/// Hero 1 · <see cref="SetupPage.Terms"/> (the prototype's <c>ob-eula</c>) — a page card outline drawing itself on,
/// four writing lines drawing in with a 0.10/0.22/0.34/0.46s stagger, then a seal popping in with its checkmark
/// drawing last. The headline draw-on beats <c>PathEl</c>'s <see cref="AnimChannel.StrokeTrimEnd"/> exists for.
/// </summary>
sealed class HeroEula : Component
{
    // Registered once (static readonly): a rounded rect x=50,y=30,w=88,h=112,rx=8 (the prototype's `.pg`), traced as a
    // real multi-segment path (4 lines + 4 arcs) — exactly the ">3-segment trimmed stroke" PathEl exists for.
    static readonly PathData PagePath = Geo(
        "M58 30 H130 A8 8 0 0 1 138 38 V134 A8 8 0 0 1 130 142 H58 A8 8 0 0 1 50 134 V38 A8 8 0 0 1 58 30 Z");

    static readonly PathData Line1 = Geo("M66 56 H122");
    static readonly PathData Line2 = Geo("M66 70 H110");
    static readonly PathData Line3 = Geo("M66 84 H118");
    static readonly PathData Line4 = Geo("M66 98 H100");
    static readonly PathData Tick = Geo("M123 132.5 L129.5 139 L142 126");

    public override Element Render()
    {
        ColorF accent = Tok.AccentDefault;
        ColorF soft = Tok.TextTertiary;

        var page = UseRef<NodeHandle>(default);
        var ln1 = UseRef<NodeHandle>(default);
        var ln2 = UseRef<NodeHandle>(default);
        var ln3 = UseRef<NodeHandle>(default);
        var ln4 = UseRef<NodeHandle>(default);
        var seal = UseRef<NodeHandle>(default);
        var tick = UseRef<NodeHandle>(default);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;

            // ob-draw: 0%{TrimEnd 0} 18%,100%{TrimEnd 1} — the page card draws itself once per loop then holds.
            if (page.Value is { IsNull: false } pn && scene.IsLive(pn))
                anim.KeyframesMotion(pn, AnimChannel.StrokeTrimEnd, [K(0, 0f), K(18, 1f), K(100, 1f)], LoopMs, ReducedMotionPolicy.KeepFade);

            // ob-line: 0%,8%{Trim 0,opacity 0} 22%{opacity 1} 30%,88%{Trim 1,opacity 1} 98%,100%{opacity 0,Trim 1} —
            // staggered 0.10/0.22/0.34/0.46s per the prototype's nth-of-type delays.
            Keyframe[] trim = [K(0, 0f), K(8, 0f), K(30, 1f), K(88, 1f), K(100, 1f)];
            Keyframe[] fade = [K(0, 0f), K(8, 0f), K(22, 1f), K(100, 1f)];
            void Line(NodeHandle n, float delayMs)
            {
                if (n.IsNull || !scene.IsLive(n)) return;
                anim.KeyframesMotion(n, AnimChannel.StrokeTrimEnd, trim, LoopMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
                anim.KeyframesMotion(n, AnimChannel.Opacity, fade, LoopMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
            }
            Line(ln1.Value, 100f);
            Line(ln2.Value, 220f);
            Line(ln3.Value, 340f);
            Line(ln4.Value, 460f);

            // ob-seal: 0%,52%{scale .4,rotate -24deg,opacity 0} 66%{scale 1.08,rotate 0,opacity 1} 74%,92%{scale 1,
            // opacity 1} 100%{opacity 0} — pivoted at the seal's own center (132,132).
            if (seal.Value is { IsNull: false } sn && scene.IsLive(sn))
            {
                Keyframe[] sc = [K(0, 0.4f), K(52, 0.4f), K(66, 1.08f), K(74, 1f), K(92, 1f), K(100, 1f)];
                Keyframe[] rot = [K(0, -24f), K(52, -24f), K(66, 0f), K(100, 0f)];
                Keyframe[] op = [K(0, 0f), K(52, 0f), K(66, 1f), K(100, 1f)];
                anim.KeyframesMotion(sn, AnimChannel.ScaleX, sc, LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(sn, AnimChannel.ScaleY, sc, LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(sn, AnimChannel.Rotation, rot, LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(sn, AnimChannel.Opacity, op, LoopMs, ReducedMotionPolicy.KeepFade);
            }

            // ob-tick: 0%,66%{Trim 0} 80%,100%{Trim 1} — the seal's own checkmark drawing in after the pop.
            if (tick.Value is { IsNull: false } tn && scene.IsLive(tn))
                anim.KeyframesMotion(tn, AnimChannel.StrokeTrimEnd, [K(0, 0f), K(66, 0f), K(80, 1f), K(100, 1f)], LoopMs, ReducedMotionPolicy.KeepFade);
        });

        return new BoxEl
        {
            ZStack = true, Width = 192f, Height = 192f,
            Children =
            [
                StrokePath(PagePath, soft, LineStroke(), n => page.Value = n),
                StrokePath(Line1, accent, LineStroke(2f), n => ln1.Value = n),
                StrokePath(Line2, accent, LineStroke(2f), n => ln2.Value = n),
                StrokePath(Line3, accent, LineStroke(2f), n => ln3.Value = n),
                StrokePath(Line4, accent, LineStroke(2f), n => ln4.Value = n),
                PivotGroup(132, 132, n => seal.Value = n,
                    RingBox(132, 132, 42, accent, 2.4f),
                    StrokePath(Tick, accent, LineStroke(2.4f), n => tick.Value = n)),
            ],
        };
    }
}
