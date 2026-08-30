using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static Wavee.HeroMotion;

namespace Wavee;

/// <summary>
/// Hero 7 · <see cref="SetupPage.Notifications"/> (the prototype's <c>ob-bell</c>) — two soft ripples breathing
/// outward, a bell silhouette swinging (a real curved silhouette — genuine <see cref="PathEl"/> geometry), and a
/// toast sliding in and settling.
/// </summary>
sealed class HeroBell : Component
{
    static readonly PathData Bell = Geo("M96 48 A22 22 0 0 1 118 70 V90 L127 103 H65 L74 90 V70 A22 22 0 0 1 96 48 Z");
    static readonly PathData Clapper = Geo("M86 110 A10 10 0 0 0 106 110");
    static readonly PathData ToastLines = Geo("M108 132 H142 M108 142 H130");

    public override Element Render()
    {
        ColorF accent = Tok.AccentDefault;
        ColorF soft = Tok.TextTertiary;

        var wv1 = UseRef<NodeHandle>(default);
        var wv2 = UseRef<NodeHandle>(default);
        var bell = UseRef<NodeHandle>(default);
        var toast = UseRef<NodeHandle>(default);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;

            // ob-wave (1.8s sub-loop, wv2 delayed .6s): 0%{scale .8,opacity 0} 30%{opacity .7} 100%{scale 1.35,opacity 0}.
            const float waveMs = 1800f;
            Keyframe[] sc = [K(0, 0.8f), K(100, 1.35f)];
            Keyframe[] op = [K(0, 0f), K(30, 0.7f), K(100, 0f)];
            void Wave(NodeHandle n, float delayMs)
            {
                if (n.IsNull || !scene.IsLive(n)) return;
                anim.KeyframesMotion(n, AnimChannel.ScaleX, sc, waveMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
                anim.KeyframesMotion(n, AnimChannel.ScaleY, sc, waveMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
                anim.KeyframesMotion(n, AnimChannel.Opacity, op, waveMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
            }
            Wave(wv1.Value, 0f);
            Wave(wv2.Value, 600f);

            // ob-swing: 0%,64%,100%{rotate 0} 70%{rotate 9} 76%{rotate -7} 82%{rotate 4} 88%{rotate -2} — pivoted at (96,56).
            if (bell.Value is { IsNull: false } bn && scene.IsLive(bn))
            {
                Keyframe[] rot = [K(0, 0f), K(64, 0f), K(70, 9f), K(76, -7f), K(82, 4f), K(88, -2f), K(100, 0f)];
                anim.KeyframesMotion(bn, AnimChannel.Rotation, rot, LoopMs, ReducedMotionPolicy.KeepFade);
            }

            // ob-toast: 0%,20%{translateX 26,opacity 0} 34%,74%{translateX 0,opacity 1} 88%,100%{translateX 26,opacity 0}.
            if (toast.Value is { IsNull: false } tn && scene.IsLive(tn))
            {
                Keyframe[] tx = [K(0, 26f), K(20, 26f), K(34, 0f), K(100, 0f)];
                Keyframe[] op2 = [K(0, 0f), K(20, 0f), K(34, 1f), K(100, 1f)];
                anim.KeyframesMotion(tn, AnimChannel.TranslateX, tx, LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(tn, AnimChannel.Opacity, op2, LoopMs, ReducedMotionPolicy.KeepFade);
            }
        });

        return new BoxEl
        {
            ZStack = true, Width = 192f, Height = 192f,
            Children =
            [
                RingBox(96, 96, 104, soft, 2f, n => wv1.Value = n),
                RingBox(96, 96, 104, soft, 2f, n => wv2.Value = n),
                PivotGroup(96, 56, n => bell.Value = n,
                    StrokePath(Bell, accent, LineStroke()),
                    StrokePath(Clapper, accent, LineStroke())),
                PivotGroup(129, 137, n => toast.Value = n,
                    StrokeRectBox(98, 122, 62, 30, 7, accent, 2.4f),
                    StrokePath(ToastLines, accent, LineStroke(2f))),
            ],
        };
    }
}
