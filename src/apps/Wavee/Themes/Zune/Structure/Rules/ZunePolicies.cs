// ── Themes/Zune/Structure/Rules/ZunePolicies.cs — what the Zune structure decides for the core (themes plan 5.5, 6.3; P5 Z0) ───────
//
// Z0 fixes the frame-wide decisions and leaves every family's to its lane:
//
//   · NAV PANE: none, at every tier. Zune's navigation is the band (`Shell.NavPane` drawn as the band by Z2b), so the core sidebar never
//     docks, never forces a rail and opens no drawer: NarrowMode and TinyMode are `None`, the pane is not resizable, and the Wide band's
//     pane is kept from mounting by the capability (`NavTree = false`, `ZunePolicies.Capabilities`).
//   · CAPABILITIES (technical plan 6.3, owner Q5): no tabs, no nav tree, no Forward button, six band pins; the history menu (right-click
//     Back) and the friends panel stay.
//   · TIERS: `ZuneTiers.Policy` (Tiers.cs).
//
// FAMILY HOOKS. Every other policy starts at the core's Standard value (the shell as it shipped: the numbers Classic declares, which
// `ClassicPoliciesTests` pins equal to the core defaults) and passes through the partial hook of the family that owns it. A later lane
// implements its hook in its own `Rules\ZunePolicies.<Family>.cs` and reads the structure values it depends on from `parameters`
// (panelMode and maxDockedPanes for the panel, collapseBand for the band, seekTimes for the bar, ...). An unimplemented hook compiles away.
//
// `For` memoizes one bundle per `ZuneParameters`, so republishing the facts for the same values is not a change.
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`).

using Wavee.Theming;

namespace Wavee.Themes.Zune;

/// <summary>The policies of the Zune structure.</summary>
public static partial class ZunePolicies
{
    /// <summary>No navigation pane at any tier: the band replaces it. The widths are the core's (a stored width survives a visit to Zune);
    /// nothing docks, nothing is forced as a rail, no drawer opens, and the pane is not resizable.</summary>
    public static NavPanePolicy NavPane { get; } = NavPanePolicy.Default with
    {
        NarrowMode = NavPaneMode.None, TinyMode = NavPaneMode.None, Resizable = false,
    };

    /// <summary>What the shell offers under Zune (technical plan 6.3): no tabs, no nav tree, no Forward button, six band pins, the history
    /// menu and the friends panel.</summary>
    public static ShellCapabilities Capabilities { get; } = new(
        Tabs: false, NavTree: false, FriendsPanel: true, ForwardButton: false, HistoryMenu: true, MaxBandPins: 6, DefersPageMount: true, ContinuousPageSwap: true);

    private static readonly object s_gate = new();
    private static readonly Dictionary<ZuneParameters, ThemePolicies> s_bundles = new();

    /// <summary>The bundle for the structure's values. One instance per value set.</summary>
    public static ThemePolicies For(ZuneParameters parameters)
    {
        lock (s_gate)
        {
            if (s_bundles.TryGetValue(parameters, out var known)) return known;
            var built = Build(parameters);
            s_bundles[parameters] = built;
            return built;
        }
    }

    private static ThemePolicies Build(ZuneParameters parameters)
    {
        NavPanePolicy nav = NavPane;
        PanelPolicy panel = PanelPolicy.Default;
        PlayerBarPolicy bar = PlayerBarPolicy.Default;
        ChromePolicy chrome = ChromePolicy.Default;
        StagePolicy stage = StagePolicy.Default;
        ListPolicy lists = ListPolicy.Default;
        DetailPolicy detail = DetailPolicy.Default;
        ArtworkColorPolicy artwork = ArtworkColorPolicy.Default;
        OverlayPolicy overlays = OverlayPolicy.Default;
        DetailTierPolicy detailTiers = DetailTierPolicy.Default;
        ArtistTierPolicy artistTiers = ArtistTierPolicy.Default;
        LibraryTierPolicy libraryTiers = LibraryTierPolicy.Default;

        BandPolicy(parameters, ref nav);
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
            Tiers = ZuneTiers.Policy,
            Lists = lists,
            Detail = detail,
            Artwork = artwork,
            Overlays = overlays,
            DetailTiers = detailTiers,
            ArtistTiers = artistTiers,
            LibraryTiers = libraryTiers,
        };
    }

    // The family hooks (each later lane implements its own in `Rules\ZunePolicies.<Family>.cs`).

    /// <summary>The band (Z2b): the nav pane policy the band rides (it stays `None` at every tier).</summary>
    static partial void BandPolicy(ZuneParameters parameters, ref NavPanePolicy navPane);

    /// <summary>The frame (Z2a, ZT1): the title bar's budget per window tier.</summary>
    static partial void FramePolicy(ZuneParameters parameters, ref ChromePolicy chrome);

    /// <summary>The panel (Z4a, ZT2): inline from 1008, the panes per rung, `panelMode`, `maxDockedPanes`.</summary>
    static partial void PanelPolicyFor(ZuneParameters parameters, ref PanelPolicy panel);

    /// <summary>The player bar (Z4a, ZT2): its level ladder (0 / 640 / 760 / 1240 / 1920 / 3200, hold 24) and height.</summary>
    static partial void PlayerPolicy(ZuneParameters parameters, ref PlayerBarPolicy playerBar);

    /// <summary>The fullscreen stage (Z4b, Z4c, ZT3).</summary>
    static partial void StagePolicyFor(ZuneParameters parameters, ref StagePolicy stage);

    /// <summary>The track lists (Z3).</summary>
    static partial void ListsPolicy(ZuneParameters parameters, ref ListPolicy lists);

    /// <summary>The detail pages (Z3, Z6a1, Z6b1, Z6b2, ZT5): the page layout and the per-tier detail records.</summary>
    static partial void DetailPolicyFor(ZuneParameters parameters, ref DetailPolicy detail, ref DetailTierPolicy detailTiers);

    /// <summary>The artist and people pages (Z6a2, ZT6).</summary>
    static partial void ArtistPolicy(ZuneParameters parameters, ref ArtistTierPolicy artistTiers);

    /// <summary>The collection (Z7, ZT7): the library navigator per content tier.</summary>
    static partial void CollectionPolicy(ZuneParameters parameters, ref LibraryTierPolicy libraryTiers);

    /// <summary>Colour taken from artwork (Z2b's material, Z9's light pass).</summary>
    static partial void ArtworkPolicy(ZuneParameters parameters, ref ArtworkColorPolicy artwork);

    /// <summary>The overlays the core composes (Z8a, ZT8): toasts at most 3, bottom right above the bar.</summary>
    static partial void OverlaysPolicy(ZuneParameters parameters, ref OverlayPolicy overlays);
}
