// ── Themes/FluentV8/Structure/Rules/FluentV8ListRules.cs — the numbers of FluentV8's DetailsList header (SPIKE) ───────────────────────────
//
// The v8 DetailsHeader (DetailsColumn.styles.ts / DetailsHeader.styles.ts): the column names in fonts.medium (14) semibold neutralPrimary, the
// sort arrow (SortUp / SortDown, 12) after the sorted column's name with 4 between, a neutralLighter hover fill with square corners, a 1-px
// bodyDivider under the band. The row inset and the check lane's shift are the BASE rows' (WinUI's 6 and the core's 28): the header sits over
// rows the base structure draws, so its grid must match theirs exactly.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.FluentV8;

/// <summary>The DetailsList header numbers.</summary>
public static class FluentV8ListRules
{
    /// <summary>The column name: fonts.medium, semibold.</summary>
    public const float HeaderText = 14f, HeaderLine = 20f;
    public const ushort HeaderWeight = 600;

    /// <summary>The sort arrow: 12, 4 after the name.</summary>
    public const float SortGlyph = 12f, SortGap = 4f;

    /// <summary>SortUp / SortDown (Fabric MDL2; the same code points in Segoe Fluent Icons).</summary>
    public const string SortUpGlyph = "", SortDownGlyph = "";

    /// <summary>The base rows' inset (WinUI's RowPad) and the core check lane the header shifts over by.</summary>
    public const float RowPad = 6f, CheckLaneShift = 28f;

    /// <summary>The 1-px divider under the band.</summary>
    public const float Divider = 1f;

    /// <summary>A lane arriving or leaving: it glides to its new place.</summary>
    public static readonly LayoutTransition CellArrange = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut), SizeMode.Reveal,
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>The band easing to nothing in the Compact row form and back.</summary>
    public static readonly LayoutTransition HeightReflow = new(TransitionChannels.Size, TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut));

    /// <summary>The check lane arriving: the header slides over.</summary>
    public static readonly LayoutTransition HeaderShift = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Design.Motion.Slow, Easing.FluentDecelerate));
}
