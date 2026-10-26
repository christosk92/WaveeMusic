// ── Themes/WinUi/Structure/Rules/WinUiPolicies.Nav.cs — the nav pane's policy hook (P6 W2b) ───────────────────────────────────────────
//
// W0 sets what each `navigationPane` value forces per window tier (`WinUiPolicies.NavPaneFor`: the 280 pane, not resizable, the 48 rail; Automatic: the rail
// at Small and the overlay at Compact; Expanded: nothing forced; Compact: the rail at Small). W2b adds what that could not say:
//
//   · COMPACT HOLDS THE RAIL AT STANDARD AND HUGE. A band that forces nothing follows the user's own collapse, so `navigationPane = Compact` could not hold the
//     48 rail from 1008 up. `NavPanePolicy.WideMode` (an opt-in core seam; the default keeps today's behaviour) says what the Wide band forces: Compact under the
//     Compact option, so the rail stays whatever the user did and the hamburger opens the 280 overlay (NavigationView LeftCompact). Automatic and Expanded keep
//     the default: docked 280, which the user may collapse to the rail at Standard.
//   · A FIXED 280. The pane is not resizable, so its width is the one number: the bounds are [280, 280] and the content floor is 0 (an Expanded pane at 641
//     docks at 280 and leaves the content what is left, as NavigationView does, instead of the core's 480 floor squeezing it to its minimum).

using Wavee.Theming;

namespace Wavee.Themes.WinUi;

public static partial class WinUiPolicies
{
    static partial void NavPolicy(WinUiParameters parameters, ref NavPanePolicy navPane)
    {
        navPane = navPane with
        {
            ContentFloorW = 0f,
            MinWidth = WinUiTiers.PaneW,
            MaxWidth = WinUiTiers.PaneW,
            DefaultWidth = WinUiTiers.PaneW,
            WideMode = parameters.NavigationPane == WinUiNavigationPane.Compact ? NavPaneMode.Compact : NavPaneMode.Expanded,
        };
    }
}
