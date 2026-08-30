using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Xunit;

namespace Wavee.Tests;

// The content card's page-swap policy (ContentHost's PURE half — PageNavMotion). Two regressions live here:
//
//  1. The card CUT TO EMPTY on every navigation. ContentHost used to hand the reconciler `recipe with { Exit = default }`,
//     so KeepAlive took the no-exit branch: the outgoing page was detached in the same frame the incoming one was seeded
//     at opacity 0. The whole 250 ms was therefore "empty card, then the new page fades up". A recipe that keeps its Exit
//     makes the reconciler mark the boundary a ZStack, keep the old root drawing (hit-test invisible) and park it once
//     the tracks settle — the pages overlap, which is the entire point of a page transition.
//
//  2. A motion-only write re-faded the page. The keep-alive token used to be (TabId, Route, Motion) while SlotKey
//     ignored Motion, so writing `_navMotion` alone (tab activation / open / close all write Neutral) changed the token
//     on the ACTIVE key — which the reconciler reads as an activation change and re-seeds the entrance, i.e. a full-page
//     re-fade with no content change whatsoever.
//
//  3. Masthead double-exposure (G2c — history). Navigating between two pages that shared the drill masthead (search's
//     directory, a browse category, a section drill) used to run PageSlideForward — enter and exit both at 250ms in
//     lockstep, plus the incoming title on a 500ms EmphasizedEnter — so the outgoing and incoming 40px display titles
//     double-exposed at nearly the same X/Y, and "Browse ›" was left reading alone for 250ms after the pages had
//     already settled. The root cause was structural, not a timing mismatch: every browse-family page rendered its
//     OWN copy of BrowseMasthead, plus a misclassified search-with-query route and an inverted FluentAccelerate/
//     SmoothOut curve pairing in the family-swap recipe that briefly lived here (PageNavMotion.SharesMasthead /
//     MastheadSwap). The structural fix is ShellMastheadBand: ONE masthead, mounted as an overlay on this whole
//     boundary (ContentHost.Render), that never consumes KeepAlive height — so PageTransition/RecipeFor went back to
//  4. Symmetric page-slide double-exposure (A/B/D recordings). RecipeFor used MotionRecipes.PageSlideForward/Back —
//     250ms opacity mix of two full-bleed pages at mismatched scroll offsets. ContentHost now uses fade-through
//     (exit in place, enter delayed); the shared PageSlide recipes stay on SearchPage's facet swap.
public class ContentHostPageTransitionTests
{
    // ── 2. slot identity ────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void PageSlot_CarriesNoNavDirection()
    {
        var members = typeof(PageSlot).GetProperties().Select(p => p.PropertyType)
            .Concat(typeof(PageSlot).GetFields().Select(f => f.FieldType));
        Assert.DoesNotContain(typeof(NavTransitionKind), members);
    }

    [Fact]
    public void SameTabAndRoute_IsTheSameToken_WhateverDirectionReachedIt()
    {
        // The reconciler compares TOKENS on the active key; equal tokens = no activation change = no re-seeded entrance.
        var a = new PageSlot(3, new Route("album:spotify:album:x", "Kid A"));
        var b = new PageSlot(3, new Route("album:spotify:album:x", "Kid A"));
        Assert.Equal(a, b);
        Assert.Equal(PageNavMotion.SlotKey(a), PageNavMotion.SlotKey(b));
    }

    [Fact]
    public void DifferentTabOrRoute_IsADifferentSlot()
    {
        var home = new PageSlot(1, new Route("home"));
        Assert.NotEqual(home, new PageSlot(2, new Route("home")));                 // per-tab: no shared page state
        Assert.NotEqual(home, new PageSlot(1, new Route("settings")));
        Assert.NotEqual(PageNavMotion.SlotKey(home), PageNavMotion.SlotKey(new PageSlot(2, new Route("home"))));
        Assert.NotEqual(PageNavMotion.SlotKey(home), PageNavMotion.SlotKey(new PageSlot(1, new Route("settings"))));
    }

    [Fact]
    public void EverySearchQueryOwnsItsOwnSlot()
    {
        Assert.NotEqual(PageNavMotion.SlotKey(new PageSlot(1, new Route("search", "radiohead"))),
                        PageNavMotion.SlotKey(new PageSlot(1, new Route("search", "aphex twin"))));
        Assert.Equal(PageNavMotion.SlotKey(new PageSlot(1, new Route("search", "radiohead"))),
                     PageNavMotion.SlotKey(new PageSlot(1, new Route("search", "radiohead"))));
        Assert.NotEqual(PageNavMotion.SlotKey(new PageSlot(1, new Route("search", "radiohead"))),
                        PageNavMotion.SlotKey(new PageSlot(2, new Route("search", "radiohead"))));
    }

    [Fact]
    public void BrowseHomeOwnsItsOwnSlot()
    {
        var home = new PageSlot(1, new Route("browse"));
        Assert.Equal(PageNavMotion.SlotKey(home), PageNavMotion.SlotKey(new PageSlot(1, new Route("browse"))));
        Assert.NotEqual(PageNavMotion.SlotKey(home), PageNavMotion.SlotKey(new PageSlot(1, new Route("search", "x"))));
        Assert.NotEqual(PageNavMotion.SlotKey(home), PageNavMotion.SlotKey(new PageSlot(2, new Route("browse"))));
    }

    [Fact]
    public void EveryOtherRouteArgOwnsItsOwnSlot()
    {
        Assert.NotEqual(PageNavMotion.SlotKey(new PageSlot(1, new Route("pl:a", "A"))),
                        PageNavMotion.SlotKey(new PageSlot(1, new Route("pl:b", "B"))));
    }

    // ── 1. the recipes keep BOTH halves ─────────────────────────────────────────────────────────────────────────────
    // NavTransitionKind is internal, so this cannot be an [InlineData] Theory (a public test method may not take a
    // less-accessible parameter) — the loop is the same coverage.
    [Fact]
    public void EveryDirection_HasAnActiveEnterAndAnActiveExit()
    {
        foreach (var motion in new[] { NavTransitionKind.Forward, NavTransitionKind.Back, NavTransitionKind.Neutral })
        {
            var recipe = PageNavMotion.RecipeFor(motion);
            Assert.True(recipe.Enter.Active, $"{motion}: the incoming page must animate in");
            Assert.True(recipe.Exit.Active, $"{motion}: a stripped Exit detaches the outgoing page in the same frame — the card flashes empty");
            Assert.NotEqual(default, recipe.Exit);
            Assert.Equal(0f, recipe.Exit.Opacity);          // it fades out rather than vanishing
            Assert.True(recipe.Dynamics.DurationMs > 0f || recipe.Dynamics.Kind == DynamicsKind.Spring, $"{motion}: no dynamics");
        }
    }

    [Fact]
    public void DirectionsMapToFadeThrough_ExitInPlaceEnterFollows()
    {
        Assert.Equal(PageNavMotion.PageFadeThroughForward, PageNavMotion.RecipeFor(NavTransitionKind.Forward));
        Assert.Equal(PageNavMotion.PageFadeThroughBack, PageNavMotion.RecipeFor(NavTransitionKind.Back));
        Assert.Equal(MotionRecipes.PageFade, PageNavMotion.RecipeFor(NavTransitionKind.Neutral));

        var fwd = PageNavMotion.RecipeFor(NavTransitionKind.Forward);
        var back = PageNavMotion.RecipeFor(NavTransitionKind.Back);
        Assert.Equal(0f, fwd.Exit.Dx);
        Assert.Equal(0f, back.Exit.Dx);
        Assert.True(fwd.Enter.Dx > 0f);
        Assert.True(back.Enter.Dx < 0f);
        Assert.Equal(fwd.Enter.Dx, -back.Enter.Dx);
        var neutral = PageNavMotion.RecipeFor(NavTransitionKind.Neutral);
        Assert.Equal(0f, neutral.Enter.Dx);
        Assert.Equal(0f, neutral.Exit.Dx);
    }

    [Fact]
    public void PageSwapIsFadeThrough_ExitLeadsEnterFollows()
    {
        foreach (var motion in new[] { NavTransitionKind.Forward, NavTransitionKind.Back })
        {
            var recipe = PageNavMotion.RecipeFor(motion);
            Assert.Equal(0f, recipe.ExitDelayMs);
            Assert.True(recipe.DelayMs > 0f, $"{motion}: enter must wait so the outgoing fade leads");
            Assert.NotNull(recipe.ExitDynamics);
            Assert.True(recipe.ExitDynamics!.Value.DurationMs < recipe.Dynamics.DurationMs,
                $"{motion}: exit shorter than enter");
            Assert.True(recipe.DelayMs < recipe.ExitDynamics.Value.DurationMs,
                $"{motion}: enter delay inside the exit window — no empty-card gap");
        }
    }

    [Fact]
    public void NoRecipeBlursThePageRoot()
    {
        // a page-sized blur group is a canvas offscreen RT + a 2-pass Gaussian per frame — outside the frame budget
        foreach (var motion in new[] { NavTransitionKind.Forward, NavTransitionKind.Back, NavTransitionKind.Neutral })
        {
            var recipe = PageNavMotion.RecipeFor(motion);
            Assert.Equal(0f, recipe.Enter.Blur);
            Assert.Equal(0f, recipe.Exit.Blur);
        }
    }

    [Fact]
    public void MastheadFadeSharesThePageExitWindow()
        => Assert.Equal(120f, PageNavMotion.FadeThroughExitMs);

    // ── 5. the VIDEO-SAFE pair ──────────────────────────────────────────────────────────────────────────────────────
    // A module watch page hosts a live composited video, which is a DestOut hole punched into the real back buffer. An
    // ancestor opacity channel multiplies straight into the video command's own opacity (a see-through video), and an
    // opacity GROUP pushes an offscreen RT the punch can never reach the back buffer from — the hole then vanishes
    // entirely and silently. Every recipe above animates opacity, so ContentHost swaps in this pair whenever EITHER
    // side of the swap is a module route (the outgoing page's root is still attached and drawing for its whole exit).

    [Fact]
    public void NoVideoSafeRecipeTouchesOpacity()
    {
        foreach (var motion in new[] { NavTransitionKind.Forward, NavTransitionKind.Back })
        {
            var recipe = PageNavMotion.RecipeForVideoSafe(motion)!.Value;
            Assert.False(recipe.Channels.HasFlag(TransitionChannels.Opacity),
                $"{motion}: an opacity channel washes out (or erases) a composited video hole");
            Assert.Equal(1f, recipe.Enter.Opacity);
            Assert.Equal(1f, recipe.Exit.Opacity);
            Assert.Equal(0f, recipe.Enter.Blur);   // a blur is the same offscreen RT by another name
            Assert.Equal(0f, recipe.Exit.Blur);
        }
    }

    [Fact]
    public void TheVideoSafeRecipesAreASymmetricTranslate_WithAnActiveExit()
    {
        var fwd = PageNavMotion.RecipeForVideoSafe(NavTransitionKind.Forward)!.Value;
        var back = PageNavMotion.RecipeForVideoSafe(NavTransitionKind.Back)!.Value;

        foreach (var recipe in new[] { fwd, back })
        {
            Assert.True(recipe.Channels.HasFlag(TransitionChannels.Position),
                "a translate is the ONE ancestor motion a video hole rides correctly");
            Assert.True(recipe.Enter.Active);
            Assert.True(recipe.Exit.Active,
                "a stripped Exit detaches the outgoing page in the same frame — the card flashes empty");
            Assert.True(recipe.Dynamics.DurationMs > 0f || recipe.Dynamics.Kind == DynamicsKind.Spring);
        }

        // Symmetric: the pages travel together, the outgoing one leaving the way the incoming one arrives from.
        Assert.True(fwd.Enter.Dx > 0f);
        Assert.True(back.Enter.Dx < 0f);
        Assert.Equal(fwd.Enter.Dx, -back.Enter.Dx);
        Assert.Equal(fwd.Enter.Dx, -fwd.Exit.Dx);
        Assert.Equal(back.Enter.Dx, -back.Exit.Dx);
        Assert.Equal(PageNavMotion.PageSlideSafeForward, fwd);
        Assert.Equal(PageNavMotion.PageSlideSafeBack, back);
    }

    /// <summary>Neutral's only recipe is opacity and nothing else, so there is no video-safe form of it to hand back.
    /// Null is an honest CUT — the alternative is inventing a motion the design never asked for.</summary>
    [Fact]
    public void NeutralHasNoVideoSafeRecipe()
        => Assert.Null(PageNavMotion.RecipeForVideoSafe(NavTransitionKind.Neutral));
}
