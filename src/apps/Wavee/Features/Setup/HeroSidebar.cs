using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static Wavee.HeroMotion;

namespace Wavee;

/// <summary>
/// Hero 5 · <see cref="SetupPage.Sidebar"/> (the prototype's <c>ob-sidebar</c>) — three sidebar-layout rail sets
/// (Classic / LibraryV3 / Curated) cross-fading in place at 1.166s offsets (3.5s / 3), beside a static content-pane
/// preview. Straight lines/rects only.
/// </summary>
sealed class HeroSidebar : Component
{
    static readonly PathData Divider = Geo("M78 44 V148");
    static readonly PathData SetALines = Geo("M36 62 H68 M36 76 H68 M36 90 H60");
    static readonly PathData SetASoft = Geo("M36 104 H68 M36 118 H62 M36 132 H68");
    static readonly PathData SetBSoft = Geo("M36 80 H68 M36 94 H60 M36 108 H68 M36 122 H56");
    static readonly PathData SetCLine = Geo("M36 62 H62");
    static readonly PathData SetCSoft = Geo("M36 102 H68 M36 116 H58 M36 130 H66");
    static readonly PathData ContentLines = Geo("M92 62 H150 M92 76 H136 M92 130 H150");

    public override Element Render()
    {
        ColorF accent = Tok.AccentDefault;
        ColorF soft = Tok.TextTertiary;

        var setA = UseRef<NodeHandle>(default);
        var setB = UseRef<NodeHandle>(default);
        var setC = UseRef<NodeHandle>(default);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;

            // ob-set: 0%,4%{opacity0,translateY 6} 10%,28%{opacity1,translateY 0} 34%,100%{opacity0,translateY -6} —
            // staggered 0s/1.166s/2.333s (3.5s / 3) across the three rail sets.
            // The prototype's ob-set cycles all three sets out because it LOOPS forever. These heroes play once and
            // hold, so the last set must REMAIN on screen — otherwise the scene ends as an empty window frame. Sets A
            // and B hand off as before; set C rises and stays.
            Keyframe[] op = [K(0, 0f), K(4, 0f), K(10, 1f), K(28, 1f), K(34, 0f), K(100, 0f)];
            Keyframe[] ty = [K(0, 6f), K(4, 6f), K(10, 0f), K(28, 0f), K(34, -6f), K(100, -6f)];
            Keyframe[] opLast = [K(0, 0f), K(4, 0f), K(10, 1f), K(100, 1f)];
            Keyframe[] tyLast = [K(0, 6f), K(4, 6f), K(10, 0f), K(100, 0f)];
            void Set(NodeHandle n, float delayMs, bool last = false)
            {
                if (n.IsNull || !scene.IsLive(n)) return;
                anim.KeyframesMotion(n, AnimChannel.Opacity, last ? opLast : op, LoopMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
                anim.KeyframesMotion(n, AnimChannel.TranslateY, last ? tyLast : ty, LoopMs, ReducedMotionPolicy.KeepFade, delayMs: delayMs);
            }
            Set(setA.Value, 0f);
            Set(setB.Value, 1166f);
            Set(setC.Value, 2333f, last: true);
        });

        return new BoxEl
        {
            ZStack = true, Width = 192f, Height = 192f,
            Children =
            [
                StrokeRectBox(26, 44, 140, 104, 10, accent, 2.4f),
                StrokePath(Divider, soft, LineStroke(2f)),
                PivotGroup(52, 96, n => setA.Value = n,
                    StrokePath(SetALines, accent, LineStroke(2f)),
                    StrokePath(SetASoft, soft, LineStroke(2f))),
                PivotGroup(52, 96, n => setB.Value = n,
                    StrokeRectBox(35, 57, 14, 9, 4.5f, accent, 2f),
                    StrokeRectBox(52, 57, 17, 9, 4.5f, accent, 2f),
                    StrokePath(SetBSoft, soft, LineStroke(2f))),
                PivotGroup(52, 96, n => setC.Value = n,
                    StrokePath(SetCLine, accent, LineStroke(2f)),
                    StrokeRectBox(35, 74, 15, 15, 3f, accent, 2f),
                    StrokeRectBox(54, 74, 15, 15, 3f, accent, 2f),
                    StrokePath(SetCSoft, soft, LineStroke(2f))),
                StrokePath(ContentLines, soft, LineStroke(2f)),
                StrokeRectBox(92, 90, 26, 26, 3f, soft, 2f),
                StrokeRectBox(124, 90, 26, 26, 3f, soft, 2f),
            ],
        };
    }
}
