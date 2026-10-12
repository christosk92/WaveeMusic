// ── Themes/Classic/Structure/Rules/StageGeometry.Caption.cs — the numbers of Classic's stage caption (themes plan 6.1, T7a1) ──
//
// What `Stage.CaptionHost` held as literals beside the picture, moved verbatim with it: the lines' weight and line budgets, the
// secondary line's gap, the veil's mid stop, the dots' radius. The allocator's geometry (the block's width, left edge and bottom
// margin, the type sizes, the veil's overhang `Stage.Layout.CaptionVeilPadX/Y`, the line gap) and the tone constants (the context
// alphas, the bloom, the rise) are the core's: the allocator budgets the face's safe rect from them, so they stay in `Stage.Layout`
// and `Stage.Tone` and the view reads them by name.

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's stage caption.</summary>
internal static class ClassicStageCaptionGeometry
{
    /// <summary>The weight of every caption line (context and active).</summary>
    public const int LineWeight = 600;

    /// <summary>A context line (previous / next) is one line.</summary>
    public const int ContextMaxLines = 1;

    /// <summary>The translation / romanization under the active line holds two lines.</summary>
    public const int SecondaryMaxLines = 2;

    /// <summary>The gap between the active line and its secondary line.</summary>
    public const float SecondaryGap = 4f;

    /// <summary>The veil's radial gradient: where the middle stop sits and the share of the centre alpha it carries.</summary>
    public const float VeilMidStop = 0.55f, VeilMidAlpha = 0.55f;

    /// <summary>A breathing dot is a circle: its corner radius is this fraction of its size.</summary>
    public const float DotRadiusFraction = 0.5f;
}
