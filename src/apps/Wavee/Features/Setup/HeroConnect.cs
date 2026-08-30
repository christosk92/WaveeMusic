using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static Wavee.HeroMotion;

namespace Wavee;

/// <summary>
/// Hero 2 · <see cref="SetupPage.SignIn"/> (the prototype's <c>ob-connect</c>) — a monitor and phone pairing: a QR
/// flicker, a pairing arc drawing then retracting, a pip travelling the arc (the prototype used CSS
/// <c>offset-path</c>; approximated here by sampling the same cubic at 5 points and driving TranslateX/Y), and a
/// confirmation badge popping in with its own checkmark draw. The pairing arc is the scene's genuine curve.
/// </summary>
sealed class HeroConnect : Component
{
    static readonly PathData MonitorStand = Geo("M48 114 V124 H74 V114 M42 128 H80");
    static readonly PathData PhoneNotch = Geo("M136 54 H150");
    static readonly PathData Arc = Geo("M104 74 C118 54 132 52 142 60");
    static readonly PathData BadgeTick = Geo("M143.5 122.5 L148.5 127.5 L158 117");

    // 5 points sampled off the SAME cubic as `Arc` at t=0,.25,.5,.75,1 (De Casteljau by hand — see HeroConnect's own
    // derivation comment) — the pip's travel waypoints, since the engine has no offset-path primitive.
    static readonly (float X, float Y)[] PipPath =
    [
        (104f, 74f), (114.44f, 62.25f), (124.5f, 56.5f), (133.81f, 56f), (142f, 60f),
    ];

    public override Element Render()
    {
        ColorF accent = Tok.AccentDefault;
        ColorF soft = Tok.TextTertiary;

        var arc = UseRef<NodeHandle>(default);
        var pip = UseRef<NodeHandle>(default);
        var badge = UseRef<NodeHandle>(default);
        var tick = UseRef<NodeHandle>(default);
        var qr0 = UseRef<NodeHandle>(default);
        var qr1 = UseRef<NodeHandle>(default);
        var qr2 = UseRef<NodeHandle>(default);
        var qr3 = UseRef<NodeHandle>(default);
        var qr4 = UseRef<NodeHandle>(default);
        var qr5 = UseRef<NodeHandle>(default);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;

            // ob-arc: 0%{Trim end 0} 20%,64%{end 1} 84%,100%{start 1} — draws, holds, then retreats from its own start
            // (the closest trim analogue of the CSS dashoffset running past the dasharray length).
            if (arc.Value is { IsNull: false } an && scene.IsLive(an))
            {
                anim.KeyframesMotion(an, AnimChannel.StrokeTrimEnd, [K(0, 0f), K(20, 1f), K(84, 1f), K(100, 1f)], LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(an, AnimChannel.StrokeTrimStart, [K(0, 0f), K(100, 0f)], LoopMs, ReducedMotionPolicy.KeepFade);
            }

            // ob-pip: 0%,6%{t=0,opacity0} 14%{opacity1} 58%{t=1,opacity1} 66%,100%{opacity0} — travel sampled off Arc.
            if (pip.Value is { IsNull: false } pn && scene.IsLive(pn))
            {
                Keyframe[] tx = [K(6, PipPath[0].X - 3.6f), K(19, PipPath[1].X - 3.6f), K(32, PipPath[2].X - 3.6f), K(45, PipPath[3].X - 3.6f), K(58, PipPath[4].X - 3.6f)];
                Keyframe[] ty = [K(6, PipPath[0].Y - 3.6f), K(19, PipPath[1].Y - 3.6f), K(32, PipPath[2].Y - 3.6f), K(45, PipPath[3].Y - 3.6f), K(58, PipPath[4].Y - 3.6f)];
                Keyframe[] op = [K(0, 0f), K(6, 0f), K(14, 1f), K(58, 1f), K(66, 0f), K(100, 0f)];
                anim.KeyframesMotion(pn, AnimChannel.TranslateX, tx, LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(pn, AnimChannel.TranslateY, ty, LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(pn, AnimChannel.Opacity, op, LoopMs, ReducedMotionPolicy.KeepFade);
            }

            // ob-badge: 0%,62%{scale0,opacity0} 74%{scale1.12,opacity1} 82%,94%{scale1,opacity1} 100%{opacity0}.
            if (badge.Value is { IsNull: false } bn && scene.IsLive(bn))
            {
                Keyframe[] sc = [K(0, 0f), K(62, 0f), K(74, 1.12f), K(82, 1f), K(94, 1f), K(100, 1f)];
                Keyframe[] op = [K(0, 0f), K(62, 0f), K(74, 1f), K(100, 1f)];
                anim.KeyframesMotion(bn, AnimChannel.ScaleX, sc, LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(bn, AnimChannel.ScaleY, sc, LoopMs, ReducedMotionPolicy.KeepFade);
                anim.KeyframesMotion(bn, AnimChannel.Opacity, op, LoopMs, ReducedMotionPolicy.KeepFade);
            }
            if (tick.Value is { IsNull: false } tn && scene.IsLive(tn))
                anim.KeyframesMotion(tn, AnimChannel.StrokeTrimEnd, [K(0, 0f), K(66, 0f), K(80, 1f), K(100, 1f)], LoopMs, ReducedMotionPolicy.KeepFade);

            // ob-qrb: a 1.4s flicker independent of the main 3.5s loop, staggered per the prototype's nth-child(2n)/
            // nth-child(3n) delays on the 6 QR cells (steps(2,end) approximated as a smooth two-stop fade).
            const float qrMs = 1400f;
            Keyframe[] flicker = [K(0, 0.25f), K(50, 1f), K(100, 1f)];
            float[] delays = [0f, 350f, 700f, 350f, 0f, 700f];
            var qr = new[] { qr0.Value, qr1.Value, qr2.Value, qr3.Value, qr4.Value, qr5.Value };
            for (int i = 0; i < qr.Length; i++)
            {
                if (qr[i].IsNull || !scene.IsLive(qr[i])) continue;
                anim.KeyframesMotion(qr[i], AnimChannel.Opacity, flicker, qrMs, ReducedMotionPolicy.KeepFade, delayMs: delays[i]);
            }
        });

        return new BoxEl
        {
            ZStack = true, Width = 192f, Height = 192f,
            Children =
            [
                StrokeRectBox(22, 58, 78, 56, 7, accent, 2.4f),
                StrokePath(MonitorStand, soft, LineStroke(2f)),
                StrokeRectBox(120, 48, 46, 78, 9, accent, 2.4f),
                StrokePath(PhoneNotch, soft, LineStroke(2f)),
                RectBox(129, 68, 9, 9, 1.5f, accent, n => qr0.Value = n),
                RectBox(143, 68, 9, 9, 1.5f, accent, n => qr1.Value = n),
                RectBox(129, 82, 9, 9, 1.5f, accent, n => qr2.Value = n),
                RectBox(143, 82, 9, 9, 1.5f, accent, n => qr3.Value = n),
                RectBox(136, 96, 9, 9, 1.5f, accent, n => qr4.Value = n),
                RectBox(150, 96, 9, 9, 1.5f, accent, n => qr5.Value = n),
                StrokePath(Arc, accent, LineStroke(2f), n => arc.Value = n),
                DiscBox(0, 0, 7.2f, accent, n => pip.Value = n),
                PivotGroup(150, 122, n => badge.Value = n,
                    RingBox(150, 122, 30, accent, 2.4f),
                    StrokePath(BadgeTick, accent, LineStroke(2.4f), n => tick.Value = n)),
            ],
        };
    }
}
