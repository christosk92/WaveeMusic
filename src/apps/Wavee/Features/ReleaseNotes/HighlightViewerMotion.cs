using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core.ReleaseNotes;

namespace Wavee;

/// <summary>The viewer's slide: a directional ENTER (±24 DIP + fade, 250 ms SmoothOut — Motion.EntranceOffsetPx and
/// Expressive.Fast) over a direction-FREE exit (fade in place).
///
/// <para>The exit is direction-free by necessity, not by taste: the engine seeds an orphan's exit from the spec the
/// node MOUNTED with, so a directional exit would replay the PREVIOUS step's direction on the way out.</para>
///
/// <para>Reduced motion needs no branch here — the scheduler skips Enter/Exit tracks under
/// <c>Motion.ReducedMotion</c> and the swap becomes a cut. Reduced motion is a value, never an author-side if.</para></summary>
static class HighlightViewerMotion
{
    /// <summary>== <c>Motion.EntranceOffsetPx</c>; a slideshow should visibly move (PageSlide's 8 is a page nudge).</summary>
    public const float SlideDistance = 24f;
    /// <summary>The exit's own duration — short, so the outgoing slide is gone before the incoming one lands.</summary>
    public const float ExitMs = 120f;

    public static LayoutTransition SlideForward => new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Dx: SlideDistance, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: 0f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(ExitMs, Easing.FluentAccelerate));

    public static LayoutTransition SlideBack => SlideForward with
        { Enter = new EnterExit(Dx: -SlideDistance, Opacity: 0f, Active: true) };

    /// <summary>The FIRST slide: no entrance (the Modal chrome already scales the plate in) but the same exit, so it
    /// can still fade out when the user steps off it.</summary>
    public static LayoutTransition ExitOnly => SlideForward with { Enter = default };

    public static LayoutTransition For(HighlightSlideDirection d) => d switch
    {
        HighlightSlideDirection.Forward => SlideForward,
        HighlightSlideDirection.Back => SlideBack,
        _ => ExitOnly,
    };
}
