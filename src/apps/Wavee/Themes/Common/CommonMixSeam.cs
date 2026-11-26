// ── Themes/Common/CommonMixSeam.cs — the timeline through a mixed playlist's list, shared by every theme (#48 Mix, owner 2026-11-26) ──────────
//
// With Mix on, a mixed playlist's rows and their transition chips read as ONE timeline: a thin vertical line from each row's artwork down to the
// next row's, broken only where a pair goes straight into the next song ("No transition"), each chip sitting on it between its two songs. The
// line meets the chip's edges (two segments: from the artwork above to the chip's top, from the chip's foot to the artwork below), so nothing
// shows through a translucent plate and nothing overlaps.
//
// When Mix turns on, each lane draws its stretch in, staggered down the screen (the core's `MixSeamProps.Reveal`): the upper segment grows
// down from the artwork, the theme's chip arrives, the lower segment grows on down to the next artwork. A theme picks the ink, the stroke and
// its chip's own entrance; under reduced motion the engine lands every track at once.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;
using TL = Wavee.Themes.Common.CommonMixTimelineRules;

namespace Wavee.Themes.Common;

public static class MixSeamTimeline
{
    /// <summary>The timeline's two segments in a lane whose chip is <paramref name="chipH"/> tall (centred in the lane), in <paramref name="ink"/>
    /// at <paramref name="stroke"/> DIP; none for "No transition" (the timeline breaks there).</summary>
    public static Element[] Segments(MixSeamProps p, float chipH, ColorF ink, float stroke)
    {
        if (p.Kind == MixSeamKind.None) return [];
        float chipTop = MathF.Max(0f, (p.LaneHeight - chipH) * 0.5f);
        float upper = p.Reach + chipTop;
        float lower = MathF.Max(0f, p.LaneHeight + p.Reach - chipTop - chipH);
        float x = p.Indent - stroke * 0.5f;
        return
        [
            Segment("mix:seam:line:up", x, -p.Reach, upper, ink, stroke, Draw(p.Reveal, 0f)),
            Segment("mix:seam:line:down", x, chipTop + chipH, lower, ink, stroke, Draw(p.Reveal, TL.SeamDrawMs + TL.SeamChipMs * 0.5f)),
        ];
    }

    /// <summary>The chip's entrance in the lane's sequence (after the upper segment), from <paramref name="gesture"/> (the theme's gesture: a fade and
    /// a small scale, a slide); null when the lane was born at rest.</summary>
    public static LayoutTransition? ChipEnter(MixSeamProps p, EnterExit gesture)
        => p.Reveal < 0f ? null
        : new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(TL.SeamChipMs, Easing.FluentDecelerate),
            Enter: gesture with { Active = true, DelayMs = p.Reveal + TL.SeamDrawMs * 0.5f });

    static LayoutTransition? Draw(float reveal, float after)
        => reveal < 0f ? null
        : new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(TL.SeamDrawMs, Easing.SmoothOut),
            Enter: new EnterExit(Sy: 0f, Active: true, DelayMs: reveal + after));

    static BoxEl Segment(string key, float x, float y, float h, ColorF ink, float stroke, LayoutTransition? draw) => new()
    {
        // It grows down from its top edge (the artwork above, or the chip's foot): a line drawing itself along the list.
        Key = key, JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = stroke, Height = MathF.Max(0f, h), OffsetX = x, OffsetY = y,
        Fill = ink, HitTestVisible = false, TransformOriginY = 0f, Animate = draw,
    };
}
