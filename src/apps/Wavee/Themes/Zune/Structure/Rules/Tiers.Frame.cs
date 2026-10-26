// ── Themes/Zune/Structure/Rules/Tiers.Frame.cs — the frame's finer tier rules (themes plan 3.2, 3.3, 6.3; P5 ZT1) ───────────────────
//
// What a window-tier crossing does to the frame (Z-Sizes-Breakpoints "how it moves"; the records are `ZuneFrameLayout`, `ZuneTitleBarLayout`
// and `ZuneBandLayout`):
//
//   THE TITLE BAR'S HEIGHT  48 ↔ 56 at the 3200 edge eases over the pivot slide (a reflow on the height axis: the band and the page under it
//                           follow frame by frame, never a jump of 8).
//   THE CHROME BUDGETS      the core allocates the title row (`Shell.Chrome.Resolve`) and re-issues the search focus when the allocation's search
//                           FORM flips (`FrameRules.ReissueSearchFocus`). Zune draws its form by window tier (the magnifier at Compact and Small,
//                           the pill from Standard up, `ZuneTitleBarLayout.Pill`), so its budgets make the allocator answer the same form in the
//                           same tier: the icon budget can never seat a field (its field floor is wider than any window); the field budget seats
//                           one at every width the tier can be laid out at (its floor is the Standard pill, 220, against a row of ~410 fixed DIP
//                           at the 984 narrowing edge). The allocator then flips on the frame the crossing commits, so a focused search keeps
//                           its focus across the pill ↔ magnifier swap. `ChromePolicy.Small` (contract addition) lets Small and Standard differ.
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`). Public, so `Wavee.Tests` pins them.

using FluentGpu.Animation;
using FluentGpu.Foundation;
using Wavee.Theming;

namespace Wavee.Themes.Zune;

/// <summary>The frame's finer tier rules (ZT1).</summary>
public static class ZuneFrameTiers
{
    /// <summary>The title bar's height across a crossing (48 ↔ 56): the pivot slide's 250 ms on the smooth-out curve, a reflow on the height
    /// axis from the top edge. Null under reduced motion at the call site.</summary>
    public static readonly LayoutTransition TitleBarHeightMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    /// <summary>The title bar's trailing island across a crossing (the search form, the bell and the gap change its width): its width eases
    /// over the pivot slide, a reflow anchored on its trailing edge (the island stands at the bar's right end), so the identity and the bell
    /// ride the re-solve instead of a position slide the engine's reflow-shove rule could snap. Null under reduced motion at the call site.</summary>
    public static readonly LayoutTransition TrailingIslandMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Trailing, Axes: SizeAxes.Width);

    /// <summary>The spacer after the pill beside a docked panel (`ZuneFrameRules.PillBesidePanel`): its width eases on the island's own dynamics,
    /// so the island's width ease and the spacer's cancel and the identity and the bell hold still while the pill moves (on the dock's 300 ms
    /// they drifted 7 DIP and back). Null under reduced motion at the call site.</summary>
    public static readonly LayoutTransition PillShiftMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    /// <summary>The identity's host when its chip changes size across a crossing (32 <-> 40 at 640): a reflow of its own box on both axes.</summary>
    public static readonly LayoutTransition AccountSizeMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Both);

    /// <summary>The wordmark across the 640 crossing (drawn from Small up, A9): its host's WIDTH eases over the pivot slide (a reflow from its
    /// leading edge, the word clipped) while it fades, so Back and the trail ride the re-solve instead of jumping by the word's width. Null
    /// under reduced motion at the call site.</summary>
    public static readonly LayoutTransition WordmarkMotion = new(TransitionChannels.Size | TransitionChannels.Opacity,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    /// <summary>A field floor no window reaches: the icon budget's search can never be laid out as a field.</summary>
    public const float NeverAField = 100000f;

    /// <summary>The magnifier tiers' budget (Compact, Small): the core's widths, a search that is always the icon.</summary>
    public static ChromeBudget IconBudget { get; } = ChromeBudget.Default with { SearchMinW = NeverAField, SearchMaxW = NeverAField };

    /// <summary>The pill tiers' budget (Standard, Huge, Huge 4K): the core's widths, a search that is always the field, between the Standard
    /// pill (220) and the Huge 4K pill (400), growing by the pill's 120 while focused.</summary>
    public static ChromeBudget FieldBudget { get; } = ChromeBudget.Default with
    {
        SearchMinW = 220f, SearchMaxW = 400f, SearchFocusGrowW = 120f,
        // The account's name is charged at the chip's cap on the pill tiers (160 + the chip's 14, A9), so the row the allocator solves is
        // the row Zune draws.
        ProfileNameW = ZuneFrameRules.AccountNameMaxW + ZuneFrameRules.AccountNameChrome,
    };

    /// <summary>Zune's title-row policy: the icon budget at Compact and Small, the field budget from Standard up.</summary>
    public static ChromePolicy Chrome { get; } = new(FieldBudget) { Compact = IconBudget, Small = IconBudget, Huge = FieldBudget };
}
