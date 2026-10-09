// ── Screens/Diagnostics.BenchHooks.cs ──────────────────────────────────────────────────────────────────────────────
// the frame bench's handles on live UI that no route reaches: a sidebar section's disclosure and a track row's drawer
//
// Role: SHELL
// Owner: S
//
// The mounted owner installs its handle on mount and clears it on unmount (only if it is still its own), so a handle is
// either the live one or null. Only `--frame-bench` calls them (`sidebar-disclosure`, `drawer-toggle`); nothing in a normal
// run reads them.

namespace Wavee;

public static partial class Diagnostics
{
    internal static class BenchHooks
    {
        /// <summary>(section id, collapsed): the same path a header click takes, reversal mid-flight included.</summary>
        internal static Action<string, bool>? ToggleSidebarSection;

        /// <summary>(display index): open that row's drawer, or close it when it is the open one — the chevron's path.</summary>
        internal static Action<int>? ToggleTrackDrawer;

        /// <summary>The open drawer's clip box (null when none is realized): the bench reads its shown height, layout height
        /// plus the reveal's FlowDelta, to time a toggle's first moving frame.</summary>
        internal static Func<FluentGpu.Foundation.NodeHandle>? TrackDrawerNode;
    }
}
