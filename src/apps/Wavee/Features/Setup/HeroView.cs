using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>The setup wizard's hero-art SEAM. Each page now returns its real animated vector hero — one
/// self-contained <see cref="Component"/> per page (<c>Hero{Page}.cs</c>), authored on the engine's path lane
/// (<c>PathEl</c>/<see cref="BoxEl.Arc"/> + <c>AnimEngine.Keyframes</c>) against the approved prototype
/// (<c>docs/plans/wavee/onboarding-mica.html</c>'s <c>ob-*</c> scenes) — see <see cref="HeroMotion"/> for the shared
/// cadence/shape helpers. <see cref="SetupPageHost"/> calls only <see cref="Exists"/>/<see cref="SetupStage.Rail"/> —
/// do not add per-page hero logic anywhere else.</summary>
static class HeroView
{
    /// <summary>False ⇒ the page drops the hero column, independently of the plate-width breakpoint in
    /// <see cref="SetupPageHost"/> (which drops it for every page below 700-DIP plate width regardless of this).
    /// Every page has art today, so this is unconditionally true — kept as a real seam rather than an always-true
    /// stub, because a future text-only page (a legal/consent page, say) is then a one-line change here.</summary>
    public static bool Exists(SetupPage page) => true;

    /// <summary>The stage/decision rework's box (<see cref="SetupStage.Rail"/>) IS this box — <see cref="For"/> is kept
    /// only because it's a smaller diff than chasing down every remaining call site in one pass; new code should call
    /// <see cref="SetupStage.Rail"/> directly.</summary>
    public static Element For(SetupPage page) => SetupStage.Rail(page);

    /// <summary>The per-page animated art — <see cref="SetupStage.Rail"/>'s one child. Public so
    /// <see cref="SetupStage"/> (a sibling, not a subtype) can build the rail box itself instead of routing through
    /// <see cref="For"/>.</summary>
    public static Element Art(SetupPage page) => page switch
    {
        SetupPage.Welcome => Embed.Comp(() => new HeroWelcome()),
        SetupPage.Terms => Embed.Comp(() => new HeroEula()),
        SetupPage.SignIn => Embed.Comp(() => new HeroConnect()),
        SetupPage.LocalPlayback => Embed.Comp(() => new HeroPatch()),
        SetupPage.Appearance => Embed.Comp(() => new HeroSettings()),
        SetupPage.Sidebar => Embed.Comp(() => new HeroSidebar()),
        SetupPage.Sound => Embed.Comp(() => new HeroSound()),
        SetupPage.Notifications => Embed.Comp(() => new HeroBell()),
        _ => Embed.Comp(() => new HeroDone()),
    };
}
