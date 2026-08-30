using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static Wavee.HeroMotion;

namespace Wavee;

/// <summary>
/// Hero 6 · <see cref="SetupPage.Sound"/> (the prototype's <c>ob-sound</c>) — a speaker silhouette with three
/// off-axis sound-wave arcs pulsing outward (staggered .2/.4s) and a 3-bar EQ meter rippling. The waves are genuine
/// curve geometry authored as <see cref="PathEl"/> arcs (each is an off-center open arc, not a simple concentric
/// sweep, so deriving an equivalent <see cref="ArcSpec"/> would be more complex than the primitive it would replace —
/// <see cref="BoxEl.Arc"/> is used instead for the Done ring, which IS a simple centered sweep).
/// </summary>
sealed class HeroSound : Component
{
    static readonly PathData Speaker = Geo("M72 80 H56 V112 H72 L98 134 V58 Z");
    static readonly PathData Ac1 = Geo("M112 78 A26 26 0 0 1 112 114");
    static readonly PathData Ac2 = Geo("M124 66 A42 42 0 0 1 124 126");
    static readonly PathData Ac3 = Geo("M136 54 A58 58 0 0 1 136 138");

    public override Element Render()
    {
        ColorF accent = Tok.AccentDefault;

        var ac1 = UseRef<NodeHandle>(default);
        var ac2 = UseRef<NodeHandle>(default);
        var ac3 = UseRef<NodeHandle>(default);
        var eq0 = UseRef<NodeHandle>(default);
        var eq1 = UseRef<NodeHandle>(default);
        var eq2 = UseRef<NodeHandle>(default);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;

            // ob-arcp (1.6s sub-loop, staggered .2/.4s): 0%{opacity0,translateX-4} 30%{opacity1,translateX0} 70%,100%{opacity0,translateX4}.
            const float arcMs = 1600f;
            Keyframe[] tx = [K(0, -4f), K(30, 0f), K(70, 4f), K(100, 4f)];
            Keyframe[] op = [K(0, 0f), K(30, 1f), K(70, 0f), K(100, 0f)];
            void Wave(NodeHandle n, float delayMs)
            {
                if (n.IsNull || !scene.IsLive(n)) return;
                anim.KeyframesMotion(n, AnimChannel.TranslateX, tx, arcMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
                anim.KeyframesMotion(n, AnimChannel.Opacity, op, arcMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
            }
            Wave(ac1.Value, 0f);
            Wave(ac2.Value, 200f);
            Wave(ac3.Value, 400f);

            // ob-bar (1s sub-loop, staggered .18/.36s) — same shape as HeroWelcome's meter.
            const float barMs = 1000f;
            Keyframe[] barKeys = [K(0, 0.35f), K(30, 1f), K(60, 0.55f), K(80, 0.9f), K(100, 1f)];
            void Bar(NodeHandle n, float delayMs)
            {
                if (n.IsNull || !scene.IsLive(n)) return;
                anim.KeyframesMotion(n, AnimChannel.ScaleY, barKeys, barMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
            }
            Bar(eq0.Value, 0f);
            Bar(eq1.Value, 180f);
            Bar(eq2.Value, 360f);
        });

        return new BoxEl
        {
            ZStack = true, Width = 192f, Height = 192f,
            Children =
            [
                StrokePath(Speaker, accent, LineStroke()),
                StrokePath(Ac1, accent, LineStroke(2f), n => ac1.Value = n),
                StrokePath(Ac2, accent, LineStroke(2f), n => ac2.Value = n),
                StrokePath(Ac3, accent, LineStroke(2f), n => ac3.Value = n),
                RectBox(52, 130, 4, 14, 2, accent, n => eq0.Value = n),
                RectBox(60, 126, 4, 18, 2, accent, n => eq1.Value = n),
                RectBox(68, 132, 4, 12, 2, accent, n => eq2.Value = n),
            ],
        };
    }
}
