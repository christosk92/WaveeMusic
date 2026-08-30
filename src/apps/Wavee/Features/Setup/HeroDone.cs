using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static Wavee.HeroMotion;

namespace Wavee;

/// <summary>
/// Hero 8 · <see cref="SetupPage.Done"/> (the prototype's <c>ob-done</c>, also the fallback for any unmapped page —
/// see <see cref="HeroView.For"/>) — a completion ring drawing itself on (a real <see cref="BoxEl.Arc"/> sweep, its
/// draw-on riding the SAME <see cref="AnimChannel.StrokeTrimEnd"/> channel <c>ProgressRing</c>'s indeterminate spinner
/// breathes with — the exact precedent the task calls out), a checkmark drawing in after, and an 8-point spark burst.
/// </summary>
sealed class HeroDone : Component
{
    static readonly PathData Spark = Geo(
        "M96 34 V22 M96 158 V170 M34 96 H22 M158 96 H170 " +
        "M52 52 L44 44 M140 140 L148 148 M52 140 L44 148 M140 52 L148 44");
    static readonly PathData Tick = Geo("M77 98 L91.5 112.5 L118 82");

    public override Element Render()
    {
        ColorF accent = Tok.AccentDefault;
        ColorF soft = Tok.TextTertiary;

        var spark = UseRef<NodeHandle>(default);
        var ring = UseRef<NodeHandle>(default);
        var tick = UseRef<NodeHandle>(default);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;

            // ob-spark: 0%,44%{scale .6,opacity 0} 58%{opacity .9} 100%{scale 1.35,opacity 0}.
            if (spark.Value is { IsNull: false } sn && scene.IsLive(sn))
            {
                Keyframe[] sc = [K(0, 0.6f), K(44, 0.6f), K(100, 1.35f)];
                Keyframe[] op = [K(0, 0f), K(44, 0f), K(58, 0.9f), K(100, 0f)];
                anim.KeyframesMotion(sn, AnimChannel.ScaleX, sc, LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(sn, AnimChannel.ScaleY, sc, LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(sn, AnimChannel.Opacity, op, LoopMs, ReducedMotionPolicy.KeepFade);
            }

            // ob-done-ring: 0%{Trim 0} 30%,100%{Trim 1} — the completion ring drawing itself on, once, then holding.
            if (ring.Value is { IsNull: false } rn && scene.IsLive(rn))
                anim.KeyframesMotion(rn, AnimChannel.StrokeTrimEnd, [K(0, 0f), K(30, 1f), K(100, 1f)], LoopMs, ReducedMotionPolicy.KeepFade);

            // ob-done-tick: 0%,26%{Trim 0} 48%,100%{Trim 1} — the checkmark draws in right after the ring completes.
            if (tick.Value is { IsNull: false } tn && scene.IsLive(tn))
                anim.KeyframesMotion(tn, AnimChannel.StrokeTrimEnd, [K(0, 0f), K(26, 0f), K(48, 1f), K(100, 1f)], LoopMs, ReducedMotionPolicy.KeepFade);
        });

        return new BoxEl
        {
            ZStack = true, Width = 192f, Height = 192f,
            Children =
            [
                PivotGroup(96, 96, n => spark.Value = n, StrokePath(Spark, soft, LineStroke(2f))),
                new BoxEl
                {
                    Width = 86f, Height = 86f, OffsetX = 96f - 43f, OffsetY = 96f - 43f,
                    Arc = new ArcSpec(accent, 2.4f, 0f, 360f, RoundCaps: false),
                    OnRealized = n => ring.Value = n,
                },
                StrokePath(Tick, accent, LineStroke(2.6f), n => tick.Value = n),
            ],
        };
    }
}
