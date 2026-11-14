// ── Themes/Common/Rules/CommonMiniWave.cs — the mini player's swell geometry (#197) ─────────────────────────────────────
//
// Classic's swell (after the prototype's `wavemask`, `M0 0H100V91C86 85 73 99 56 94S24 84 0 93Z`, raised a hair): the tinted band behind the
// tall column's object faces keeps everything above one slow swell and the window's ground fills the rest. The path is that ground region,
// in the box's own DIP (stretched like the prototype's `preserveAspectRatio=none`), rebuilt only when the box's size changes. A cover never
// gets it (it meets the ground on a straight edge).

using FluentGpu.Foundation;
using FluentGpu.Render;

namespace Wavee.Themes.Common;

internal static class MiniWave
{
    /// <summary>The ground under the swell: from the right edge across to the left edge, closed along the foot.</summary>
    public static PathData Foot(float w, float h)
    {
        // One cached path: a re-render at the same size re-uses it, so the tessellation is not redone.
        if (s_cache is { } hit && s_size == (w, h)) return hit;
        var path = Build(w, h);
        s_cache = path;
        s_size = (w, h);
        return path;
    }

    static PathData? s_cache;
    static (float W, float H) s_size;

    static PathData Build(float w, float h)
    {
        var b = new PathBuilder();
        b.MoveTo(w, h * 0.90f);
        b.CubicTo(w * 0.86f, h * 0.84f, w * 0.73f, h * 0.98f, w * 0.56f, h * 0.93f);
        b.CubicTo(w * 0.39f, h * 0.88f, w * 0.24f, h * 0.83f, 0f, h * 0.92f);
        b.LineTo(0f, h + 1f);
        b.LineTo(w, h + 1f);
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }
}
