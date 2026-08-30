using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Render;

namespace Wavee;

/// <summary>
/// Shared cadence + shape helpers for the nine setup-wizard hero animations (<see cref="HeroView"/>'s seam). Every
/// hero loops on the SAME 3.5s / 210-frame cadence the Windows-11-OOBE Lottie exports use
/// (<c>docs/plans/wavee/onboarding-v2-assets/*.json</c>: <c>fr=60, op=210</c> — 210/60 = 3.5s) and the approved
/// prototype's shared CSS <c>--ease</c> token approximates (<c>docs/plans/wavee/onboarding-mica.html</c>). <see cref="Ease"/>
/// uses the Lottie's own LITERAL spline (every nested precomp's opacity/transform keys use
/// <c>{0.167,0.167}→{0.833,0.833}</c>) rather than the CSS approximation, and rather than inventing a third value —
/// it is also the exact curve already landed for <c>ProgressRing</c>'s indeterminate spinner
/// (<c>ProgressRing.cs</c>'s <c>IndeterminateSpline</c>), so this is a precedented choice, not a new one.
/// </summary>
static class HeroMotion
{
    /// <summary>The shared hero loop length — 3.5s, matching every Lottie's <c>op=210</c> at <c>fr=60</c>.</summary>
    public const float LoopMs = 3500f;

    /// <summary>The shared hero easing (see type doc): the Lottie precomps' own cubic-bezier spline.</summary>
    public static readonly EasingSpec Ease = EasingSpec.CubicBezier(0.167f, 0.167f, 0.833f, 0.833f);

    /// <summary>A CSS-keyframe-percentage stop (0..100, matching the prototype's <c>@keyframes</c> literally) at the
    /// shared <see cref="Ease"/>.</summary>
    public static Keyframe K(float pct, float value) => new(pct / 100f, value, Ease);

    /// <summary>A percentage stop at an explicit easing (linear holds / steps-flicker beats).</summary>
    public static Keyframe K(float pct, float value, EasingSpec ease) => new(pct / 100f, value, ease);

    /// <summary>A percentage stop at named <see cref="Easing.Linear"/> — CSS keyframe segments that are a flat hold or
    /// an explicitly-linear beat (the prototype's <c>ob-arc</c>/<c>ob-pip</c> offset-path travel).</summary>
    public static Keyframe KL(float pct, float value) => new(pct / 100f, value, Easing.Linear);

    /// <summary>Intern an SVG path string against the shared 192x192 canvas every hero authors on (the prototype's
    /// <c>viewBox="0 0 192 192"</c>) — registering the SAME literal string on every mount (a static readonly field,
    /// never a per-render call) keeps <see cref="PathGeometryTable"/>'s cache warm (one epoch per distinct string).</summary>
    public static PathData Geo(string d, FillRule rule = FillRule.NonZero)
    {
        int id = PathGeometryTable.Shared.Register(d, 192f, 192f, rule);
        PathGeometryTable.Shared.TryGet(id, out var data);
        return data;
    }

    /// <summary>The prototype's default line art weight (<c>.oobe *{stroke-width:2.4}</c>), round caps/joins.</summary>
    public static StrokeStyle LineStroke(float width = 2.4f) => new(width, LineCap.Round, LineJoin.Round);

    // ── absolutely-positioned primitives (OffsetX/Y place a node inside a 192x192 ZStack canvas; see HeroView's
    // 192x192 art root — a static Offset coexists correctly with a later Scale/Opacity/Rotation animation channel on
    // the SAME node because AnimEngine's per-tick fold reseeds Tx/Ty from the node's CURRENT composited transform and
    // only overwrites the channels that actually have a live row (AnimScheduler.cs's Accum.FromPaint: "preserves
    // un-animated channels"). A node that will ALSO carry an animated TranslateX/Y must instead fold its rest
    // position into the keyframe VALUES themselves — see HeroConnect's travelling pip / HeroSettings' knobs.) ────

    /// <summary>A stroked (unfilled) circle centered at (cx,cy) with diameter <paramref name="d"/>.</summary>
    public static BoxEl RingBox(float cx, float cy, float d, ColorF stroke, float strokeWidth, Action<NodeHandle>? onRealized = null)
        => new BoxEl
        {
            Width = d, Height = d, OffsetX = cx - d / 2f, OffsetY = cy - d / 2f,
            Corners = CornerRadius4.All(d / 2f), Fill = ColorF.Transparent,
            BorderColor = stroke, BorderWidth = strokeWidth, OnRealized = onRealized,
        };

    /// <summary>A filled disc centered at (cx,cy) with diameter <paramref name="d"/>.</summary>
    public static BoxEl DiscBox(float cx, float cy, float d, ColorF fill, Action<NodeHandle>? onRealized = null)
        => new BoxEl
        {
            Width = d, Height = d, OffsetX = cx - d / 2f, OffsetY = cy - d / 2f,
            Corners = CornerRadius4.All(d / 2f), Fill = fill, OnRealized = onRealized,
        };

    /// <summary>A filled, optionally-rounded rect at top-left (x,y).</summary>
    public static BoxEl RectBox(float x, float y, float w, float h, float radius, ColorF fill, Action<NodeHandle>? onRealized = null)
        => new BoxEl
        {
            Width = w, Height = h, OffsetX = x, OffsetY = y,
            Corners = CornerRadius4.All(radius), Fill = fill, OnRealized = onRealized,
        };

    /// <summary>A stroked (unfilled), optionally-rounded rect at top-left (x,y).</summary>
    public static BoxEl StrokeRectBox(float x, float y, float w, float h, float radius, ColorF stroke, float strokeWidth, Action<NodeHandle>? onRealized = null)
        => new BoxEl
        {
            Width = w, Height = h, OffsetX = x, OffsetY = y,
            Corners = CornerRadius4.All(radius), Fill = ColorF.Transparent,
            BorderColor = stroke, BorderWidth = strokeWidth, OnRealized = onRealized,
        };

    /// <summary>A full-canvas (192x192, fit-scale 1:1) fill-only path — the geometry's own coordinates are already
    /// absolute in the 192 canvas, so this node needs no static Offset.</summary>
    public static PathEl FillPath(PathData geo, ColorF fill, FillRule rule = FillRule.NonZero, Action<NodeHandle>? onRealized = null)
        => new PathEl
        {
            Width = 192f, Height = 192f, ViewBoxW = 192f, ViewBoxH = 192f,
            Geometry = geo, Fill = fill, Rule = rule, OnRealized = onRealized,
        };

    /// <summary>A full-canvas (192x192, fit-scale 1:1) stroke-only path, authored TrimEnd=0 by default so a caller
    /// that forgets to wire a draw-on loop still renders nothing rather than a jarring fully-drawn flash pre-effect —
    /// callers driving the trim via <see cref="AnimEngine.Keyframes"/> override this every tick regardless.</summary>
    public static PathEl StrokePath(PathData geo, ColorF stroke, StrokeStyle style, Action<NodeHandle>? onRealized = null)
        => new PathEl
        {
            Width = 192f, Height = 192f, ViewBoxW = 192f, ViewBoxH = 192f,
            Geometry = geo, StrokeColor = stroke, Stroke = style, OnRealized = onRealized,
        };

    /// <summary>A same-size-as-canvas (192x192) grouping wrapper whose <see cref="Element.OnRealized"/>-free
    /// TransformOrigin is set to the fraction of (<paramref name="pivotX"/>,<paramref name="pivotY"/>) within the
    /// 192 canvas: a Scale/Rotation/Opacity animation on THIS node then pivots exactly around that absolute-canvas
    /// point, while every child inside keeps authoring its OWN absolute-canvas Offset unchanged (the wrapper sits at
    /// the same origin/size as the canvas it groups, so nothing needs re-deriving into a local coordinate space).
    /// The engine idiom this stands in for: CSS <c>transform-origin: &lt;px&gt; &lt;px&gt;</c> on a grouping
    /// <c>&lt;g&gt;</c> (the prototype's <c>.seal</c>/<c>.badge</c>/<c>.bell</c>/<c>.toast</c> groups).</summary>
    public static BoxEl PivotGroup(float pivotX, float pivotY, Action<NodeHandle>? onRealized, params Element[] children)
        => new BoxEl
        {
            ZStack = true, Width = 192f, Height = 192f,
            TransformOriginX = pivotX / 192f, TransformOriginY = pivotY / 192f,
            Children = children, OnRealized = onRealized,
        };
}
