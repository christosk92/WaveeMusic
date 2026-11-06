// ── Themes/WinUi/Structure/Rules/WinUiPolicies.cs — what the WinUI structure decides for the core (themes plan 5.5, 6.2; P6 W0) ─────
//
// W0 fixes the frame-wide decisions and leaves every family's to its lane:
//
//   · NAV PANE (W-Tiers, W-ThemeOptions): NavigationView's modes by window tier. `navigationPane = Automatic`: minimal (the 280 overlay from
//     the hamburger) at Compact, the forced 48 rail at Small, the docked 280 from Standard. `Expanded`: docked wherever it fits, so Small
//     forces nothing. `Compact`: the rail at Small; from Standard the core's pane forces nothing (a band that forces nothing follows the
//     user's collapse), so holding the rail at Standard and Huge is W2b's (with a core seam if the policy cannot say it). The pane is 280,
//     not resizable; the rail is 48. Compact is always minimal: a 280 pane does not dock in a < 641 window.
//   · LISTS: `artworkInRows` IS `ListPolicy.TrackArtwork` (W3 refines the rest of the list policy in its hook).
//   · CAPABILITIES (technical plan 6.2): tabs as the `tabs` option says, the nav tree, Forward, the history menu and the friends panel, no
//     band pins. Tabs off keeps the workspace (owner Q1: the board; core seam WK (a)).
//   · TIERS: `WinUiTiers.Policy` (Tiers.cs).
//   · OWNER ANSWERS the family lanes implement in their hooks: no page cap at Huge or 4K (Q2: grids and tables fill, only reading text caps
//     at 760); the nav pane stays docked beside a docked side pane at Standard (Q4: 280 + 340 + 480 fits from 1100).
//
// FAMILY HOOKS. Every other policy starts at the core's Standard value and passes through the partial hook of the family that owns it. A
// later lane implements its hook in its own `Rules\WinUiPolicies.<Family>.cs` and reads the structure values it depends on from
// `parameters`. An unimplemented hook compiles away.
//
// `For` memoizes one bundle per `WinUiParameters`, so republishing the facts for the same values is not a change.
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`).

using Wavee.Theming;

namespace Wavee.Themes.WinUi;

/// <summary>The policies of the WinUI structure.</summary>
public static partial class WinUiPolicies
{
    /// <summary>The nav pane under <paramref name="mode"/>: the 280 pane, not resizable, the 48 rail; what Small and Compact force by mode.</summary>
    public static NavPanePolicy NavPaneFor(WinUiNavigationPane mode) => mode switch
    {
        WinUiNavigationPane.Expanded => s_navExpanded,
        WinUiNavigationPane.Compact => s_navCompact,
        _ => s_navAutomatic,
    };

    private static readonly NavPanePolicy s_navAutomatic = NavPanePolicy.Default with
    {
        DefaultWidth = WinUiTiers.PaneW, RailWidth = WinUiTiers.RailW, Resizable = false,
        NarrowMode = NavPaneMode.Compact, TinyMode = NavPaneMode.Overlay,
    };

    private static readonly NavPanePolicy s_navExpanded = s_navAutomatic with { NarrowMode = NavPaneMode.Expanded };

    private static readonly NavPanePolicy s_navCompact = s_navAutomatic with { NarrowMode = NavPaneMode.Compact };

    private static readonly ShellCapabilities s_tabsOn = new(
        Tabs: true, NavTree: true, FriendsPanel: true, ForwardButton: true, HistoryMenu: true, MaxBandPins: 0, ContinuousPageSwap: true) { CyclesTabs = true };

    // Tabs off keeps the workspace (owner Q1, the board): the strip is hidden, not the tabs (W2a, core seam WK (a)).
    private static readonly ShellCapabilities s_tabsOff = s_tabsOn with { HiddenTabStrip = true };

    /// <summary>What the shell offers under WinUI (technical plan 6.2): tabs always, the strip hidden when the `tabs` option is off (the workspace
    /// kept, Ctrl+T opening in the background, Ctrl+Tab cycling in both), the nav tree, the friends panel, Forward, the history menu, no band
    /// pins. One instance per value.</summary>
    public static ShellCapabilities CapabilitiesFor(WinUiParameters parameters) => parameters.Tabs ? s_tabsOn : s_tabsOff;

    private static readonly object s_gate = new();
    private static readonly Dictionary<WinUiParameters, ThemePolicies> s_bundles = new();

    /// <summary>The bundle for the structure's values. One instance per value set.</summary>
    public static ThemePolicies For(WinUiParameters parameters)
    {
        lock (s_gate)
        {
            if (s_bundles.TryGetValue(parameters, out var known)) return known;
            var built = Build(parameters);
            s_bundles[parameters] = built;
            return built;
        }
    }

    private static ThemePolicies Build(WinUiParameters parameters)
    {
        NavPanePolicy nav = NavPaneFor(parameters.NavigationPane);
        PanelPolicy panel = PanelPolicy.Default;
        PlayerBarPolicy bar = PlayerBarPolicy.Default;
        ChromePolicy chrome = ChromePolicy.Default;
        StagePolicy stage = StagePolicy.Default;
        ListPolicy lists = ListPolicy.Default with { TrackArtwork = parameters.ArtworkInRows };
        DetailPolicy detail = DetailPolicy.Default;
        ArtworkColorPolicy artwork = ArtworkColorPolicy.Default;
        OverlayPolicy overlays = OverlayPolicy.Default;
        DetailTierPolicy detailTiers = DetailTierPolicy.Default;
        ArtistTierPolicy artistTiers = ArtistTierPolicy.Default;
        LibraryTierPolicy libraryTiers = LibraryTierPolicy.Default;

        NavPolicy(parameters, ref nav);
        FramePolicy(parameters, ref chrome);
        PanelPolicyFor(parameters, ref panel);
        PlayerPolicy(parameters, ref bar);
        StagePolicyFor(parameters, ref stage);
        ListsPolicy(parameters, ref lists);
        DetailPolicyFor(parameters, ref detail, ref detailTiers);
        ArtistPolicy(parameters, ref artistTiers);
        CollectionPolicy(parameters, ref libraryTiers);
        ArtworkPolicy(parameters, ref artwork);
        OverlaysPolicy(parameters, ref overlays);

        return new ThemePolicies(nav, panel, bar, chrome, stage)
        {
            Tiers = WinUiTiers.Policy,
            Lists = lists,
            Detail = detail,
            Artwork = artwork,
            Overlays = overlays,
            DetailTiers = detailTiers,
            ArtistTiers = artistTiers,
            LibraryTiers = libraryTiers,
            Settings = SettingsGrouping.Seven,   // W8b: the board's seven tabs over the twelve areas
        };
    }

    // The family hooks (each later lane implements its own in `Rules\WinUiPolicies.<Family>.cs`).

    /// <summary>The nav pane (W2b): the modes per tier from `navigationPane`, the width animation.</summary>
    static partial void NavPolicy(WinUiParameters parameters, ref NavPanePolicy navPane);

    /// <summary>The frame (W2a): the title row's budget per window tier (tab widths, the 520 / 640 search).</summary>
    static partial void FramePolicy(WinUiParameters parameters, ref ChromePolicy chrome);

    /// <summary>The panel (W4a): 340, docked when nav + 340 + 480 fits (868 at Small, 1100 at Standard), two panes at Huge, sheets at Compact.</summary>
    static partial void PanelPolicyFor(WinUiParameters parameters, ref PanelPolicy panel);

    /// <summary>The player bar (W4a): Mini &lt; 641, Compact 641-859, Medium 860-1099, Full 1100-1919, Huge from 1920; 64 / 72.</summary>
    static partial void PlayerPolicy(WinUiParameters parameters, ref PlayerBarPolicy playerBar);

    /// <summary>The fullscreen stage (W4b, W4c): the layouts each tier allows.</summary>
    static partial void StagePolicyFor(WinUiParameters parameters, ref StagePolicy stage);

    /// <summary>The track lists (W3).</summary>
    static partial void ListsPolicy(WinUiParameters parameters, ref ListPolicy lists);

    /// <summary>The detail pages (W6a, W6b): the horizontal hero, the Huge side column, the per-tier detail records.</summary>
    static partial void DetailPolicyFor(WinUiParameters parameters, ref DetailPolicy detail, ref DetailTierPolicy detailTiers);

    /// <summary>The artist and people pages (W6a).</summary>
    static partial void ArtistPolicy(WinUiParameters parameters, ref ArtistTierPolicy artistTiers);

    /// <summary>The collection (W7): master-detail from 1008.</summary>
    static partial void CollectionPolicy(WinUiParameters parameters, ref LibraryTierPolicy libraryTiers);

    /// <summary>Colour taken from artwork (W1's accent rule, W9's light pass).</summary>
    static partial void ArtworkPolicy(WinUiParameters parameters, ref ArtworkColorPolicy artwork);

    /// <summary>The overlays the core composes (W8a): toasts above the player, dialogs full screen below 641.</summary>
    static partial void OverlaysPolicy(WinUiParameters parameters, ref OverlayPolicy overlays);
}
