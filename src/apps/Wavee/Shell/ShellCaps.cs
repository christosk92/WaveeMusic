// ── Shell/ShellCaps.cs — the active structure's shell capabilities, as the core reads them (themes plan 6.3, P5 ZK) ──────────
//
// Role: CORE
//
// `ShellCapabilities` (Theming\Policies\ThemePolicies.cs) says what the shell offers under the active structure. Classic declares
// `Unrestricted`, so every branch below is a no-op for it. The core honours each member where the feature lives:
//   · Tabs = false        no tab strip and no tab count (`Chrome.Resolve(..., tabs: false)`), the new-tab verbs and the Ctrl+T chord
//                         refuse, "open in a new tab" navigates in place, and the workspace collapses to the active tab while the
//                         others stay parked and persisted (`TabWorkspace.Collapse` / `Expand`, owner Q4; `Shell.FollowTabsCapability`);
//   · ForwardButton = false  the chrome budget has no Forward (`Budget`); Alt+Right and the mouse's forward button still go forward;
//   · NavTree = false     the sidebar pane does not mount (`FrameModel.Gate`, `Sidebar.DockedPaneProps.Mount`, the drawer), the
//                         pane toggle verbs refuse and the toggle is disabled, and the palette hides the nav-tree rows;
//   · MaxBandPins         the band's pins are capped (`NavModel.BandPins`);
//   · HistoryMenu, FriendsPanel: kept (no structure turns them off yet).
// Two reads, as `ShellPolicy`: `Now` (a plain read for verbs) and `Tracked` (subscribes the running effect or render; it re-runs on
// any fact change, a tier crossing included, so a reader keeps its work idempotent).

using Wavee.Theming;

namespace Wavee;

/// <summary>The active shell capabilities.</summary>
public static class ShellCaps
{
    /// <summary>The active capabilities, read without subscribing.</summary>
    public static ShellCapabilities Now => ThemeHost.Facts.Peek().Capabilities;

    /// <summary>The active capabilities, read inside an effect or a render so it subscribes to a change.</summary>
    public static ShellCapabilities Tracked => ThemeHost.Facts.Value.Capabilities;

    /// <summary>The chrome budget the row is allocated by under <paramref name="caps"/>: without a forward button the budget never admits
    /// Forward (its entry width is unreachable), so the row neither draws it nor pays for it. Every other member is the structure's.</summary>
    public static ChromeBudget Budget(ChromeBudget budget, ShellCapabilities caps)
        => caps.ForwardButton ? budget : budget with { ForwardEnterW = float.PositiveInfinity };

    /// <summary>How many pins a band shows under <paramref name="caps"/> (never negative).</summary>
    public static int PinCap(ShellCapabilities caps) => Math.Max(0, caps.MaxBandPins);
}
