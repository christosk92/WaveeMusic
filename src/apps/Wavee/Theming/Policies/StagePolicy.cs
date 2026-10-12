// ── Theming/Policies/StagePolicy.cs — what a structure decides about the fullscreen stage (themes plan 5.5) ───────────
//
// The stage's allocator (`Stage.Layout.Resolve`) classifies its (W, H) into one of four layout classes with a hysteresis
// on every edge; the classification is core, the edges are the structure's. There is no default FACE here: the face is
// Bloom in every theme until the user picks one (owner NQ3).

namespace Wavee.Theming;

/// <summary>The edges between the stage's layout classes. Demotion is immediate, promotion needs the hold: a class entered at
/// one value is left only past the other, so a drag across an edge flips once per crossing.</summary>
/// <param name="CompactBelowW">Under this width the stage is Compact whatever the height.</param>
/// <param name="PortraitEnter">Width/height ratio at or below which the stage is Portrait.</param>
/// <param name="PortraitLeave">Ratio a Portrait stage must exceed to leave Portrait (greater than <see cref="PortraitEnter"/>).</param>
/// <param name="UltrawideEnter">Ratio at or above which the stage is Ultrawide.</param>
/// <param name="UltrawideLeave">Ratio an Ultrawide stage must fall to (or below) to leave Ultrawide (less than <see cref="UltrawideEnter"/>).</param>
/// <param name="CompactEnterH">Height at or below which a landscape stage is Compact.</param>
/// <param name="CompactLeaveH">Height a Compact landscape stage must reach to leave Compact (greater than <see cref="CompactEnterH"/>).</param>
public sealed record AspectThresholds(
    float CompactBelowW, float PortraitEnter, float PortraitLeave, float UltrawideEnter, float UltrawideLeave,
    float CompactEnterH, float CompactLeaveH)
{
    /// <summary>The shell before themes: Compact under 600 wide or from 460 tall (leaves at 484), Portrait up to 0.80
    /// (leaves above 0.85), Ultrawide from 2.00 (leaves at 1.95).</summary>
    public static AspectThresholds Default { get; } = new(
        Stage.Layout.WideEnterW, Stage.Layout.PortraitEnter, Stage.Layout.PortraitLeave,
        Stage.Layout.UltrawideEnter, Stage.Layout.UltrawideLeave, Stage.Layout.CompactEnterH, Stage.Layout.CompactLeaveH);

    /// <summary>Everything wrong with the thresholds, empty when they can classify a stage.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        void Finite(string name, float v)
        {
            if (!float.IsFinite(v) || v < 0f) problems.Add($"Stage.Aspect.{name} must be zero or more and finite (was {v})");
        }
        Finite(nameof(CompactBelowW), CompactBelowW);
        Finite(nameof(PortraitEnter), PortraitEnter);
        Finite(nameof(PortraitLeave), PortraitLeave);
        Finite(nameof(UltrawideEnter), UltrawideEnter);
        Finite(nameof(UltrawideLeave), UltrawideLeave);
        Finite(nameof(CompactEnterH), CompactEnterH);
        Finite(nameof(CompactLeaveH), CompactLeaveH);
        if (problems.Count > 0) return problems;
        if (PortraitLeave < PortraitEnter) problems.Add("Stage.Aspect.PortraitLeave must not be below PortraitEnter");
        if (UltrawideLeave > UltrawideEnter) problems.Add("Stage.Aspect.UltrawideLeave must not exceed UltrawideEnter");
        if (CompactLeaveH < CompactEnterH) problems.Add("Stage.Aspect.CompactLeaveH must not be below CompactEnterH");
        if (PortraitLeave >= UltrawideLeave) problems.Add("Stage.Aspect: the Portrait and Ultrawide ratio bands must not overlap");
        return problems;
    }
}

/// <summary>The stage's policy.</summary>
/// <param name="Aspect">The layout-class edges.</param>
public sealed record StagePolicy(AspectThresholds Aspect)
{
    /// <summary>The shell before themes.</summary>
    public static StagePolicy Default { get; } = new(AspectThresholds.Default);

    /// <summary>Everything wrong with the policy.</summary>
    public IReadOnlyList<string> Validate() => Aspect is null ? new[] { "Stage.Aspect is missing" } : Aspect.Validate();
}
