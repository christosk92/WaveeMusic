// ── Themes/WinUi/Structure/Rules/WinUiPageMotion.cs — WinUI's navigation transitions, per kind of page swap (winui page motion) ─────────
//
// WinUI's own NavigationTransitionInfos, from the WinUI 3 source (microsoft-ui-xaml, dxaml/phone/lib/ThemeTransitions.cpp and .h; the
// Microsoft Learn pages "Page transitions" and "Motion in practice" describe the same):
//
//   kind (PageSwap)        WinUI                                          outgoing page                         incoming page
//   Refresh                EntranceNavigationTransitionInfo (3180-3221)   fade 150 ms, (0.7,0,1,0.5)           from 140 below, 300 ms, (0.1,0.9,0.2,1), after 150
//   RefreshBack            the same, BackNavigating*                      down 140 + fade, 150 ms              fade in 300 ms, (0.1,0.9,0.2,1), after 150
//   DrillIn                DrillInNavigationTransitionInfo (2860-2940)   scale 1.04 + fade, 100 ms            scale 0.94 -> 1, 783 ms, (0.1,0.9,0.2,1); fade 333 ms
//   DrillBack              the same, BackNavigating* (2945-3021)         scale 0.96 + fade, 100 ms            scale 1.06 -> 1 + fade, 333 ms, (0.12,0,0,1)
//   SlideFromRight / Left  SlideNavigationTransitionInfo (1545-1592)     150 aside, 150 ms, (0.7,0,1,0.5)     from 200 the other side, 300 ms, (0.1,0.9,0.2,1), after 150
//   (durations: ThemeTransitions.cpp:1551-1552, 3185-3186; drill: ThemeTransitions.h:326-333)
//
// THE STAGGER. WinUI staggers a page's content with CommonNavigationTransitionInfo.IsStaggeringEnabled and a panel's children with
// EntranceThemeTransition.IsStaggeringEnabled; the delays come from the OS animation table (ThemeGenerator.cpp:868-889: a delay, a
// cap, a reduce factor), not from the source. Here: the incoming page's EntranceGroup units (`Element.EntranceGroup`) carry the
// slide and fade, 30 ms apart, each gap 0.85 of the one before, the last starting at most 180 ms after the first; the page root
// itself only fades in over ControlFaster (83 ms) as the units begin, so the parts nothing groups appear with the page. A drill keeps
// WinUI's zoom on the root and gives the units a short rise (40) on top. Back fades its units in, as WinUI's back has no slide in.
//
// Reduced motion ("Animation effects" off): a quick cross-fade (ControlFaster each way), no slide, no scale, no stagger.
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`).

using FluentGpu.Foundation;
using Wavee.Theming;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's page motion per kind of swap.</summary>
public static class WinUiPageMotion
{
    // ── WinUI's KeySplines (ThemeTransitions.cpp) ────────────────────────────────────────────────────────────────────
    /// <summary>The incoming leg's spline (inControlPoint1/2 = (0.1, 0.9), (0.2, 1.0)): the page is most of the way in on its first frames.</summary>
    public static EasingSpec In => EasingSpec.CubicBezier(0.1f, 0.9f, 0.2f, 1f);
    /// <summary>The outgoing leg's spline (outControlPoint1/2 = (0.7, 0), (1.0, 0.5)): it holds, then goes.</summary>
    public static EasingSpec Out => EasingSpec.CubicBezier(0.7f, 0f, 1f, 0.5f);
    /// <summary>The drill's opacity spline ((0.17, 0.17), (0, 1)).</summary>
    public static EasingSpec DrillFade => EasingSpec.CubicBezier(0.17f, 0.17f, 0f, 1f);
    /// <summary>The drill's outgoing leg. WinUI runs the 1.04 zoom on its fast spline over 100 ms; in six frames that moves the page's edges
    /// half their way on the first one, which reads as a jump, so the zoom and the fade run evenly here (same length, same end).</summary>
    public static EasingSpec DrillAway => Easing.Linear;
    /// <summary>The drill back's incoming scale spline ((0.12, 0), (0, 1)).</summary>
    public static EasingSpec DrillBackIn => EasingSpec.CubicBezier(0.12f, 0f, 0f, 1f);

    // ── WinUI's numbers ─────────────────────────────────────────────────────────────────────────────────────────────
    public const float OutMs = 150f, InMs = 300f;
    public const float RiseDy = 140f;                                   // EntranceNavigationTransitionInfo translationOffset
    public const float SlideOutDx = 150f, SlideInDx = 200f;             // SlideNavigationTransitionInfo exit / entrance offsets
    public const float DrillAwayMs = 100f, DrillBackMs = 333f;
    /// <summary>The drill's zoom in. WinUI runs it 783 ms (ThemeTransitions.h:328) on a spline that is within 1 % of the end by 350 ms; a zoom
    /// cannot be a composite parameter of the page layer (a translate is), so every frame of it re-records the page, and the last 400 ms of
    /// WinUI's length move the page edges by under a pixel. The same spline over 500 ms: the zoom reads the same, the page pays 30 frames
    /// instead of 47. (The owner's call: 783 restores WinUI's length.)</summary>
    public const float DrillInScaleMs = 500f;
    public const float DrillInScale = 0.94f, DrillAwayScale = 1.04f, DrillBackInScale = 1.06f, DrillBackAwayScale = 0.96f;

    // ── the stagger (the owner's taste: see the header) ───────────────────────────────────────────────────────────
    /// <summary>The gap between the first two units, ms.</summary>
    public const float StaggerStepMs = 30f;
    /// <summary>Each later gap is the previous one × this.</summary>
    public const float StaggerDecay = 0.85f;
    /// <summary>The last unit starts at most this long after the first, ms.</summary>
    public const float StaggerCapMs = 180f;
    /// <summary>The incoming page starts this long before the outgoing one's 150 ms is over: the outgoing spline holds the old page up to its
    /// last frames and drops it on the 150th ms, so starting exactly there leaves one or two frames with neither page on them. Two frames of
    /// overlap hand over without a blank frame (the new page draws over the old one's last half).</summary>
    public const float HandOverMs = 33f;
    /// <summary>When the incoming page and its units begin.</summary>
    public const float InDelayMs = OutMs - HandOverMs;
    /// <summary>The page root's own fade as its units begin (ControlFaster).</summary>
    public const float RootFadeMs = 83f;
    /// <summary>A drilled page's units rise this far on top of the zoom.</summary>
    public const float DrillUnitDy = 40f;
    /// <summary>A drilled page's units start this long after the zoom does (the old page's 100 ms fade is most of the way out).</summary>
    public const float DrillUnitDelayMs = 60f;

    static TransitionDynamics Tween(float ms, EasingSpec e) => TransitionDynamics.Tween(ms, e);

    const TransitionChannels Pose = TransitionChannels.Position | TransitionChannels.Opacity;

    /// <summary>A page that waits out the 150 ms exit, then fades in under its units. <paramref name="exit"/> is the outgoing leg.</summary>
    static LayoutTransition AfterExit(EnterExit exit) => new(Pose, Tween(RootFadeMs, In),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: exit, ExitDynamics: Tween(OutMs, Out), DelayMs: InDelayMs, ExitDelayMs: 0f);

    static readonly LayoutTransition s_refresh = AfterExit(new EnterExit(Opacity: 0f, Active: true));
    static readonly LayoutTransition s_refreshBack = AfterExit(new EnterExit(Dy: RiseDy, Opacity: 0f, Active: true));
    static readonly LayoutTransition s_slideFromRight = AfterExit(new EnterExit(Dx: -SlideOutDx, Opacity: 0f, Active: true));
    static readonly LayoutTransition s_slideFromLeft = AfterExit(new EnterExit(Dx: SlideOutDx, Opacity: 0f, Active: true));

    // The drill overlaps its legs (the source starts the incoming storyboard at 0): the old page zooms past and fades in 100 ms while the
    // new one zooms in from 0.94. One tween per leg: the incoming zoom's 783 ms spline brings the fade to ~0.95 by 333 ms, WinUI's fade.
    static readonly LayoutTransition s_drillIn = new(Pose, Tween(DrillInScaleMs, In),
        Enter: new EnterExit(Sx: DrillInScale, Sy: DrillInScale, Opacity: 0f, Active: true),
        Exit: new EnterExit(Sx: DrillAwayScale, Sy: DrillAwayScale, Opacity: 0f, Active: true),
        ExitDynamics: Tween(DrillAwayMs, DrillAway), DelayMs: 0f, ExitDelayMs: 0f);

    static readonly LayoutTransition s_drillBack = new(Pose, Tween(DrillBackMs, DrillBackIn),
        Enter: new EnterExit(Sx: DrillBackInScale, Sy: DrillBackInScale, Opacity: 0f, Active: true),
        Exit: new EnterExit(Sx: DrillBackAwayScale, Sy: DrillBackAwayScale, Opacity: 0f, Active: true),
        ExitDynamics: Tween(DrillAwayMs, DrillAway), DelayMs: 0f, ExitDelayMs: 0f);

    /// <summary>Reduced motion: the old page fades out, then the new one in, ControlFaster each.</summary>
    static readonly LayoutTransition s_reduced = new(TransitionChannels.Opacity, Tween(RootFadeMs, In),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: Tween(RootFadeMs, Out), DelayMs: RootFadeMs, ExitDelayMs: 0f);

    static EntranceCascade Units(EnterExit terminal, float delayMs) => new(terminal with { Active = true }, Tween(InMs, In), delayMs,
        StaggerStepMs, StaggerDecay, StaggerCapMs);

    static readonly EntranceCascade s_riseUnits = Units(new EnterExit(Dy: RiseDy, Opacity: 0f), InDelayMs);
    static readonly EntranceCascade s_fadeUnits = Units(new EnterExit(Opacity: 0f), InDelayMs);
    static readonly EntranceCascade s_fromRightUnits = Units(new EnterExit(Dx: SlideInDx, Opacity: 0f), InDelayMs);
    static readonly EntranceCascade s_fromLeftUnits = Units(new EnterExit(Dx: -SlideInDx, Opacity: 0f), InDelayMs);
    static readonly EntranceCascade s_drillUnits = Units(new EnterExit(Dy: DrillUnitDy, Opacity: 0f), DrillUnitDelayMs);
    static readonly EntranceCascade s_drillBackUnits = Units(new EnterExit(Opacity: 0f), DrillUnitDelayMs);

    /// <summary>The page root's recipe for a kind; null (Neutral) keeps the core's fade.</summary>
    public static LayoutTransition? RecipeFor(PageSwap kind) => kind switch
    {
        PageSwap.Refresh => s_refresh,
        PageSwap.RefreshBack => s_refreshBack,
        PageSwap.DrillIn => s_drillIn,
        PageSwap.DrillBack => s_drillBack,
        PageSwap.SlideFromRight => s_slideFromRight,
        PageSwap.SlideFromLeft => s_slideFromLeft,
        _ => null,
    };

    /// <summary>The incoming page's staggered entrance for a kind; null (Neutral) enters as one block.</summary>
    public static EntranceCascade? CascadeFor(PageSwap kind) => kind switch
    {
        PageSwap.Refresh => s_riseUnits,
        PageSwap.RefreshBack => s_fadeUnits,
        PageSwap.DrillIn => s_drillUnits,
        PageSwap.DrillBack => s_drillBackUnits,
        PageSwap.SlideFromRight => s_fromRightUnits,
        PageSwap.SlideFromLeft => s_fromLeftUnits,
        _ => null,
    };

    /// <summary>A pivot inside a page bringing a sibling section in (the artist page's tabs): the visible units slide in from the side the
    /// tab order points to, as <see cref="PageSwap.SlideFromRight"/> / <see cref="PageSwap.SlideFromLeft"/> do for pages, with no exit
    /// leg (the page stays; the jump to the section is the hand-over).</summary>
    public static EntranceCascade PivotCascade(bool fromRight) => fromRight ? s_pivotRight : s_pivotLeft;

    // No fade: the jump to the section is the hand-over, so a unit that faded in from 0 left the frame after it blank. WinUI's slide does not
    // fade its incoming page either (its opacity steps to 1 as the slide starts, ThemeTransitions.cpp:1571-1574).
    static readonly EntranceCascade s_pivotRight = Units(new EnterExit(Dx: SlideInDx), 0f);
    static readonly EntranceCascade s_pivotLeft = Units(new EnterExit(Dx: -SlideInDx), 0f);

    /// <summary>The policy WinUI publishes (<c>ThemePolicies.PageMotion</c>).</summary>
    public static PageMotionPolicy Policy { get; } = new(RecipeFor, CascadeFor) { ReducedFade = s_reduced, Pivot = static fromRight => FluentGpu.Dsl.Motion.ReducedMotion ? null : PivotCascade(fromRight) };
}
