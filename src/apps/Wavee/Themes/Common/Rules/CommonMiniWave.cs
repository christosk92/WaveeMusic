// ── Themes/Common/Rules/CommonMiniWave.cs — the mini player's swell geometry (#197) ─────────────────────────────────────
//
// Classic's wave edge (the prototype's `wavemask`: `M0 0H100V91C86 85 73 99 56 94S24 84 0 93Z`): the cover keeps everything above one slow
// swell and the window's ground fills the rest. The path is that ground region, in the box's own DIP (stretched like the prototype's
// `preserveAspectRatio=none`), rebuilt only when the box's size changes.

using FluentGpu.Foundation;
using FluentGpu.Render;

namespace Wavee.Themes.Common;

internal static class MiniWave
{
    /// <summary>The swell's heights on the right and left edges, as shares of the box (the cover's, and the tall column's band).</summary>
    public const float CoverTop = 0.91f, BandTop = 0.90f;

    /// <summary>The ground under the swell: from the right edge at <paramref name="top"/> across to the left edge, closed along the foot.</summary>
    public static PathData Foot(float w, float h, float top)
    {
        // One cached path per use (the cover's, the band's): a re-render at the same size re-uses it, so the tessellation is not redone.
        int slot = top == CoverTop ? 0 : 1;
        if (s_cache[slot] is { } hit && s_size[slot] == (w, h)) return hit;
        var path = Build(w, h, top);
        s_cache[slot] = path;
        s_size[slot] = (w, h);
        return path;
    }

    static readonly PathData?[] s_cache = new PathData?[2];
    static readonly (float W, float H)[] s_size = new (float, float)[2];

    static PathData Build(float w, float h, float top)
    {
        float d = top - CoverTop;   // the band's swell sits a hair higher than the cover's
        var b = new PathBuilder();
        b.MoveTo(w, h * top);
        b.CubicTo(w * 0.86f, h * (0.85f + d), w * 0.73f, h * (0.99f + d), w * 0.56f, h * (0.94f + d));
        b.CubicTo(w * 0.39f, h * (0.89f + d), w * 0.24f, h * (0.84f + d), 0f, h * (0.93f + d));
        b.LineTo(0f, h + 1f);
        b.LineTo(w, h + 1f);
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }
}
