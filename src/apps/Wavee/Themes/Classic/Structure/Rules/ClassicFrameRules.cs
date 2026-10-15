// ── Themes/Classic/Structure/Rules/ClassicFrameRules.cs — the numbers of Classic's frame (themes plan 6.1, T4a) ────────────
//
// What `Shell.UI.cs` held as file-level constants beside the frame root, moved verbatim with the frame's picture: the sidebar
// pane's and the content card's shared transition, the content row's morph id, the stroke overhang of the content region, the
// rail band's corner, and the offline lane's geometry. They are data, not decisions: `Views\Frame\ClassicFrame.cs` and
// `ClassicContentState.cs` read them (WVTH003 allows literals only here, in `Rules\**`).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's frame and content states.</summary>
internal static class ClassicFrameRules
{
    // The sidebar collapse AND the content card's FLIP share ONE transition, so the pane's animating edge and the card's
    // left edge ease on identical dynamics. Reveal lays out at the FINAL size and eases a clip + translate only; a grip
    // drag snaps both 1:1 through the suppression arbiter. WinUI SplitView's pane spline at 300 ms (200 ms read as a snap
    // behind the heavier media surface).
    private static readonly EasingSpec PaneEase = EasingSpec.CubicBezier(0f, 0.35f, 0.15f, 1f);
    private const float PaneMs = 300f;

    /// <summary>The content row's shared-layout group: the content card FLIPs relative to the row, which never moves on a toggle.</summary>
    public const string ContentRowMorphId = "shell.content-row";

    /// <summary>The pane opens in 200 ms and closes in 100 ms on WinUI's SplitView spline (MotionTok.PaneOpen/PaneClose).</summary>
    public static readonly LayoutTransition SidebarPaneAnim = new(TransitionChannels.Size | TransitionChannels.Position,
        TransitionDynamics.Tween(MotionTok.PaneOpen.DurationMs, Easing.FluentPane), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(MotionTok.PaneClose.DurationMs, Easing.FluentPane), SuppressDescendantTransitions: true);

    /// <summary>The content card's transition: position and size ease on the pane's dynamics.</summary>
    public static readonly LayoutTransition ContentCardAnim = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(PaneMs, PaneEase), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(PaneMs, PaneEase), SuppressDescendantTransitions: true);

    /// <summary>A LEFT+TOP-only stroke: the engine's border is one uniform SDF ring, so the stroked box is one DIP larger on
    /// its right and bottom and parked in a clip of the real geometry: the right/bottom strokes land in the clipped DIP
    /// and the rounded top-left arc survives whole.</summary>
    public static readonly Edges4 StrokeOverhang = new(0f, 0f, -1f, -1f);

    /// <summary>The rail band's corner: the card radius on the top left only.</summary>
    public static CornerRadius4 RailBandCorners => new(Radii.Card, 0f, 0f, 0f);

    /// <summary>The offline lane's inset in the content card (tuned Classic X1, board C-Offline): the page gutter either side, 16 from the card's
    /// top, nothing below (the page's own head opens under it).</summary>
    public static Edges4 OfflineLanePadding => new(Spacing.PageWide, 16f, Spacing.PageWide, 0f);
}
