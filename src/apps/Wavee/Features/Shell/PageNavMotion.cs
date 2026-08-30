using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>Which direction the NEXT page swap travels. The shell's navigation verbs (Go / Back / Forward / tab
/// activation) write this signal BEFORE the route signal in the same flush, so the reconciler can Peek it (an untracked
/// read — a motion-only write must never re-run the keep-alive boundary) and get the direction that belongs to the
/// route it is about to activate.</summary>
enum NavTransitionKind : byte { Forward, Back, Neutral }

/// <summary>The IDENTITY of a keep-alive page slot: which browser tab, which route. The nav DIRECTION is deliberately
/// NOT part of it — direction decides how a swap animates, not which page is cached. Folding it in made a motion-only
/// write on the already-active key look like an activation change, which re-seeded the entrance and re-faded the whole
/// page with no content change at all.</summary>
readonly record struct PageSlot(int TabId, Route Route);

/// <summary>The page-swap policy of the content card: slot identity (<see cref="SlotKey"/>) and the motion recipe a
/// direction maps to (<see cref="RecipeFor"/>). Split out of <c>ContentHost</c> because it is pure — no pages, no
/// controls, no GPU — so it can be pinned by tests.</summary>
static class PageNavMotion
{
    /// <summary>Every destination page gets its own slot inside the active tab, so ALL forward/back navigation uses the
    /// same page-slide language (Fluent Frame SlideNavigationTransitionInfo / Zune panorama: the page moves, the content
    /// does not then cascade). Each committed search query is its own slot; Back walks query history.</summary>
    public static string SlotKey(PageSlot s)
        => s.TabId + "\u001F" + s.Route.Name + "\u001F" + (s.Route.Arg ?? "");

    /// <summary>The recipe for a page swap, WITH its Exit half. Both halves are load-bearing: the reconciler's
    /// <c>BeginKeepAliveExit</c> only overlaps the outgoing page (ZStack on the boundary, hit-test invisible, parked
    /// once its tracks settle) when <c>Exit.Active</c> is true — with a stripped Exit the outgoing page is detached in
    /// the same frame and the card flashes EMPTY before the incoming page arrives.
    /// Fade-through (not a symmetric slide): exit fades in place (~120ms accelerate) so two full-bleed pages never
    /// mix at readable opacity; enter follows after 90ms with a short directional slide. SearchPage facet swaps keep
    /// the shared <see cref="MotionRecipes.PageSlideForward"/> / Back recipes.</summary>
    public static LayoutTransition RecipeFor(NavTransitionKind motion) => motion switch
    {
        NavTransitionKind.Back => PageFadeThroughBack,
        NavTransitionKind.Neutral => MotionRecipes.PageFade,
        _ => PageFadeThroughForward,
    };

    // Outgoing is mostly gone by ~70ms; incoming starts at 90ms — the readable double-exposure window shrinks from
    // 250ms full-mix to a short low-alpha crossover, and the card is never empty (Exit.Active stays true).
    internal const float FadeThroughExitMs = 120f;
    const float FadeThroughEnterDelayMs = 90f;

    public static LayoutTransition PageFadeThroughForward => new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Dx: Expressive.DistLarge, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: 0f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(FadeThroughExitMs, Easing.FluentAccelerate),
        DelayMs: FadeThroughEnterDelayMs,
        ExitDelayMs: 0f);

    public static LayoutTransition PageFadeThroughBack => new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Dx: -Expressive.DistLarge, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: 0f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(FadeThroughExitMs, Easing.FluentAccelerate),
        DelayMs: FadeThroughEnterDelayMs,
        ExitDelayMs: 0f);

    // ── the VIDEO-SAFE pair ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The recipe for a page swap where either side can be hosting a live composited video — today, a module
    /// watch page. <b>Position only.</b>
    ///
    /// <para>A composited video is a DestOut hole punched into the real back buffer by a descendant. An ancestor
    /// <c>TransitionChannels.Opacity</c> multiplies straight into the video command's own opacity (a washed-out,
    /// see-through video), and an opacity GROUP pushes an offscreen render target the punch can never reach the back
    /// buffer from — so the hole vanishes entirely and silently. Since the reconciler keeps the OUTGOING page's root
    /// attached and drawing for the whole exit, the page being navigated AWAY from is just as exposed as the one being
    /// navigated to, which is why the caller classifies BOTH sides.</para>
    ///
    /// <para>A TRANSLATE is the one ancestor motion a hole rides correctly: it composes on the <c>AbsoluteRect</c> the
    /// punch already reads from, so nobody has to animate the hole for the hole to move. Hence a symmetric slide —
    /// enter from the travel direction, exit the opposite way — with the same dynamics on both halves and
    /// <c>Exit.Active</c> still TRUE (a stripped Exit detaches the outgoing page in the same frame and the card
    /// flashes empty).</para>
    ///
    /// <para><b>The honest degradation:</b> a module-page swap SLIDES instead of cross-fading. Two full-bleed pages
    /// therefore share the card at full opacity for the length of the travel, which is the double-exposure
    /// fade-through was introduced to shrink — and it is still the better trade, because the alternative is a video
    /// that disappears mid-navigation.</para></summary>
    /// <param name="motion">The direction the swap travels.</param>
    /// <returns>The slide for Forward/Back; <b>null</b> for <see cref="NavTransitionKind.Neutral"/> — an honest CUT.
    /// Neutral's only recipe is <see cref="MotionRecipes.PageFade"/>, which is opacity and nothing else, so there is
    /// no video-safe form of it to hand back: a hard cut is what "no motion this page can survive" looks like.</returns>
    public static LayoutTransition? RecipeForVideoSafe(NavTransitionKind motion) => motion switch
    {
        NavTransitionKind.Back => PageSlideSafeBack,
        NavTransitionKind.Neutral => null,
        _ => PageSlideSafeForward,
    };

    public static LayoutTransition PageSlideSafeForward => new(
        TransitionChannels.Position,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Dx: Expressive.DistLarge, Active: true),
        Exit: new EnterExit(Dx: -Expressive.DistLarge, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        DelayMs: 0f,
        ExitDelayMs: 0f);

    public static LayoutTransition PageSlideSafeBack => new(
        TransitionChannels.Position,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Dx: -Expressive.DistLarge, Active: true),
        Exit: new EnterExit(Dx: Expressive.DistLarge, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        DelayMs: 0f,
        ExitDelayMs: 0f);
}
