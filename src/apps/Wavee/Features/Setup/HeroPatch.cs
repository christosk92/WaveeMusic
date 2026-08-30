using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static Wavee.HeroMotion;

namespace Wavee;

/// <summary>
/// Hero 3 · <see cref="SetupPage.LocalPlayback"/> (the prototype's <c>ob-patch</c>) — a shield silhouette drawing
/// itself on, three "bytes" rising off it, a package group dropping into place, and a verify checkmark drawing in
/// last. The shield silhouette is the scene's genuine curve/complex-outline geometry.
/// </summary>
sealed class HeroPatch : Component
{
    static readonly PathData Shield = Geo("M96 62 L136 78 V110 C136 136 112 150 96 156 C80 150 56 136 56 110 V78 Z");
    static readonly PathData PkgCross = Geo("M78 29 H114 M96 18 V48");
    static readonly PathData Verify = Geo("M83 108 L94 119 L113 97");

    public override Element Render()
    {
        ColorF accent = Tok.AccentDefault;

        var shield = UseRef<NodeHandle>(default);
        var byte0 = UseRef<NodeHandle>(default);
        var byte1 = UseRef<NodeHandle>(default);
        var byte2 = UseRef<NodeHandle>(default);
        var pkg = UseRef<NodeHandle>(default);
        var verify = UseRef<NodeHandle>(default);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;

            // ob-shield: 0%{Trim 0} 24%,100%{Trim 1} — draws once, holds.
            if (shield.Value is { IsNull: false } sh && scene.IsLive(sh))
                anim.KeyframesMotion(sh, AnimChannel.StrokeTrimEnd, [K(0, 0f), K(24, 1f), K(100, 1f)], LoopMs, ReducedMotionPolicy.KeepFade);

            // ob-byte (1.15s linear sub-loop, staggered .38/.76s): 0%{translateY 0,opacity 0} 20%{opacity .9} 100%{translateY -34,opacity 0}.
            const float byteMs = 1150f;
            Keyframe[] ty = [KL(0, 0f), KL(100, -34f)];
            Keyframe[] op = [KL(0, 0f), KL(20, 0.9f), KL(100, 0f)];
            void Byte(NodeHandle n, float delayMs)
            {
                if (n.IsNull || !scene.IsLive(n)) return;
                anim.KeyframesMotion(n, AnimChannel.TranslateY, ty, byteMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
                anim.KeyframesMotion(n, AnimChannel.Opacity, op, byteMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
            }
            Byte(byte0.Value, 0f);
            Byte(byte1.Value, 380f);
            Byte(byte2.Value, 760f);

            // ob-pkg: 0%,4%{translateY -26,opacity 0} 16%{opacity 1} 42%{translateY 30,opacity 1} 52%,100%{translateY 30,opacity 0}.
            if (pkg.Value is { IsNull: false } pn && scene.IsLive(pn))
            {
                Keyframe[] pty = [K(0, -26f), K(4, -26f), K(42, 30f), K(100, 30f)];
                Keyframe[] pop = [K(0, 0f), K(4, 0f), K(16, 1f), K(42, 1f), K(52, 0f), K(100, 0f)];
                anim.KeyframesMotion(pn, AnimChannel.TranslateY, pty, LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(pn, AnimChannel.Opacity, pop, LoopMs, ReducedMotionPolicy.KeepFade);
            }

            // ob-verify: 0%,52%{Trim 0,opacity 0} 62%{opacity 1} 72%,92%{Trim 1,opacity 1} 100%{opacity 0,Trim 0}.
            if (verify.Value is { IsNull: false } vn && scene.IsLive(vn))
            {
                Keyframe[] vt = [K(0, 0f), K(52, 0f), K(72, 1f), K(100, 1f)];
                Keyframe[] vop = [K(0, 0f), K(52, 0f), K(62, 1f), K(100, 1f)];
                anim.KeyframesMotion(vn, AnimChannel.StrokeTrimEnd, vt, LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(vn, AnimChannel.Opacity, vop, LoopMs, ReducedMotionPolicy.KeepFade);
            }
        });

        return new BoxEl
        {
            ZStack = true, Width = 192f, Height = 192f,
            Children =
            [
                StrokePath(Shield, accent, LineStroke(), n => shield.Value = n),
                DiscBox(78, 126, 5.2f, accent, n => byte0.Value = n),
                DiscBox(96, 132, 5.2f, accent, n => byte1.Value = n),
                DiscBox(114, 126, 5.2f, accent, n => byte2.Value = n),
                PivotGroup(96, 33, n => pkg.Value = n,
                    StrokeRectBox(78, 18, 36, 30, 5, accent, 2.4f),
                    StrokePath(PkgCross, accent, LineStroke(2f))),
                StrokePath(Verify, accent, LineStroke(), n => verify.Value = n),
            ],
        };
    }
}
