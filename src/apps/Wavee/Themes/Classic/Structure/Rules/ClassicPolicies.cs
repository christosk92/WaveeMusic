// ── Themes/Classic/Structure/Rules/ClassicPolicies.cs — Wavee Classic's policies (themes plan 5.5, 6.1) ───────────────
//
// What the Classic structure decides for the core: the navigation pane's widths and what each band forces, the panel's bounds
// and the mini player's, the player bar's six pressure levels, the title-bar row's budget (per window tier, TC1) and the stage's
// layout classes, and the window tiers whose edges are the pane's bands (`Tiers.cs`, TC1: board C-Breakpoints). These are the
// numbers the shell shipped with before themes existed; Classic behaves exactly as it did because the core state machines now
// take them from here (through `ThemeHost.Policies`) instead of from constants.
//
// A tuned-Classic PR (CT, TC) changes a value HERE and nowhere else. `ThemePolicies.Default` in core is the same set, kept so
// the shell has a policy before a composition is installed; `ClassicPoliciesTests` pins the two equal (the tiers aside: core
// never names a theme, so `Default` carries `TierPolicy.Standard` and the bundle below carries `ClassicTiers.Policy`).
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`).

using System.Collections.Immutable;
using Wavee.Theming;

namespace Wavee.Themes.Classic;

/// <summary>The policies of Wavee Classic.</summary>
public static class ClassicPolicies
{
    /// <summary>The pane: the Wide band (Standard and Huge) lets the user collapse; Narrow (Small) forces the 48-DIP rail with an overlay
    /// pane over the whole band; Tiny (Compact) is the drawer (where the bands begin is the window ladder's, 528 and 1008 in
    /// <c>ClassicTiers</c>, board C-Breakpoints). An expanded pane is 180-460
    /// (320 fresh) and leaves 480 of content; a drag 64 below 180 collapses it, 24 back expands it. The seam strip is 16 wide and the collapsed rail 48.</summary>
    public static NavPanePolicy NavPane { get; } = new(
        ContentFloorW: 480f,
        MinWidth: 180f, MaxWidth: 460f, DefaultWidth: 320f,
        CollapseApproach: 64f, CollapseHysteresis: 24f,
        NarrowMode: NavPaneMode.Compact, TinyMode: NavPaneMode.Overlay, Resizable: true, Seam: 16f,
        RailWidth: SidebarGeometry.RailWidth);

    /// <summary>The panel: 200-500 wide, beside the content row, placed per window tier (`ClassicPanelTiers`, TC2: a sheet at Compact,
    /// floating at Small, one docked at Standard, two at Huge). The mini player keeps the 0.2.9 numbers: 240 x 135 at the least, 72 clear
    /// below (the player bar) and 32 above (the chrome), 16 from every edge.</summary>
    public static PanelPolicy Panel { get; } = new(
        MinWidth: 200f, MaxWidth: 500f, FullHeight: false,
        Pip: new Video.PipBounds(MinW: 240f, MinH: 135f, ReserveBottom: 72f, ReserveTop: 32f, Margin: 16f))
    {
        Tiers = ClassicPanelTiers.Rules,
    };

    /// <summary>The player bar: the 72-DIP dock, its six levels (the sub-ladder) and the levels each window tier may use: `ClassicPlayerTiers`
    /// (Rules/Tiers.Player.cs, TC2).</summary>
    public static PlayerBarPolicy PlayerBar { get; } = ClassicPlayerTiers.Policy;

    /// <summary>The title-bar row: bell, friends and pin from 1200, the profile name from 1360, forward above 520 (all
    /// budget-checked), every fixed affordance at the width the row really lays it out at, a 280-420 search field (28 % of the
    /// window) that yields to the 44 magnifier and widens by 80 while focused (12 clear of the tabs and the avatar), and 110-200 tabs.
    /// No theme toggle: light and dark live in Settings › App mode and the profile menu (tuned F13). Every window tier but Compact and
    /// Huge is allocated by this budget.</summary>
    private static ChromeBudget RowBudget { get; } = new(
        NameEnterW: 1360f, ActionsEnterW: 1200f, ForwardEnterW: 520f, PromotionHysteresisW: 40f,
        BarLeadW: 60f, NavButtonW: 44f, AddSlotW: 32f,
        ProfileChipW: 32f, ConnectingChipW: 90f, SignInChipW: 84f, ReconnectChipW: 100f, ProfileNameW: 90f,
        MinDragStripW: 48f, CaptionClusterW: 138f, GutterMinW: 8f,
        WidthQuantumW: 10f, SearchMaxW: 420f, SearchMinW: 280f, SearchWidthRatio: 0.28f, SearchIconW: 44f,
        TabMaxW: 200f, TabMinW: 110f, PinnedTabW: 40f, TabViewportMinW: 32f)
    {
        SearchFocusGrowW = 80f, SearchFocusClearW = 12f,
    };

    /// <summary>The title-bar row per window tier (board C-TitleBar, TC1): Compact folds the tab strip into ONE tab-count button that
    /// opens the open tabs as a menu (≡, Back, the search icon and the count; no "+") and carries the account in the drawer, not the row
    /// (C-Shell-Compact, TC2); Huge seats a search of up to 480 and tabs of up to
    /// 240; Small and Standard keep <see cref="RowBudget"/>. Forward hides below 520 at every tier (the budget's <c>ForwardEnterW</c>).</summary>
    public static ChromePolicy Chrome { get; } = new(RowBudget)
    {
        Compact = RowBudget with { TabsAsCount = true, TrailingInDrawer = true },
        Huge = RowBudget with { SearchMaxW = 480f, TabMaxW = 240f },
    };

    /// <summary>The stage: Compact under 600 wide or from 460 tall (back at 484), Portrait up to 0.80 (back above 0.85),
    /// Ultrawide from 2.00 (back at 1.95).</summary>
    public static StagePolicy Stage { get; } = new(new AspectThresholds(
        CompactBelowW: 600f, PortraitEnter: 0.80f, PortraitLeave: 0.85f, UltrawideEnter: 2.00f, UltrawideLeave: 1.95f,
        CompactEnterH: 460f, CompactLeaveH: 484f));

    /// <summary>The whole bundle, with Classic's window tiers. One instance, so republishing the facts is not a change. Declared
    /// LAST: static properties initialise in textual order and it reads the five above (`ClassicTiers` builds its policy in its
    /// own initializer and never reads this class).</summary>
    public static ThemePolicies Bundle { get; } = new(NavPane, Panel, PlayerBar, Chrome, Stage) { Tiers = ClassicTiers.Policy, Overlays = ClassicOverlayTiers.Policy };

    // The bundle for each combination of the structure options that reach the core (M7a: the sidebar layout x density; M4: the track artwork hidden; M8: the
    // detail page layout x the rails' uniformity; M2: the track row style; S0: the sidebar covers; CT9: the accent from artwork and the now-playing colours):
    // index = (nowPlayingOff * 2 + accentOff) * 128 + coversOff * 64 + ((((rowClassic * 2) + railUniform) * 2 + pageLayout) * 2 + hideArt) * 4 + layout * 2 + density.
    // The default combination is `Bundle` itself.
    private static readonly ThemePolicies[] s_byOptions = BuildByOptions();

    private static ThemePolicies[] BuildByOptions()
    {
        var all = new ThemePolicies[512];
        foreach (bool accentFromArtwork in new[] { true, false })
         foreach (bool nowPlayingColors in new[] { true, false })
         foreach (bool showCovers in new[] { true, false })
         foreach (TrackRowStyle rowStyle in new[] { TrackRowStyle.Modern, TrackRowStyle.Classic })
         foreach (bool railUniform in new[] { false, true })
            foreach (DetailPageLayout pageLayout in new[] { DetailPageLayout.Automatic, DetailPageLayout.Hero })
                foreach (bool hideArt in new[] { false, true })
                    foreach (SidebarLayoutId layout in new[] { SidebarLayoutId.Classic, SidebarLayoutId.Library })
                        foreach (SidebarDensity density in new[] { SidebarDensity.Default, SidebarDensity.Compact })
                        {
                            bool isDefault = !hideArt && layout == SidebarLayoutId.Classic && density == SidebarDensity.Default
                                && pageLayout == DetailPageLayout.Automatic && !railUniform && rowStyle == TrackRowStyle.Modern && showCovers
                                && accentFromArtwork && nowPlayingColors;
                            all[IndexOf(layout, density, hideArt, pageLayout, railUniform, rowStyle, showCovers, accentFromArtwork, nowPlayingColors)] = isDefault
                                ? Bundle
                                : Bundle with
                                {
                                    NavPane = NavPane with { Layout = layout, Density = density, ShowCovers = showCovers },
                                    Lists = new ListPolicy(!hideArt, rowStyle),
                                    Detail = new DetailPolicy(pageLayout, railUniform),
                                    Artwork = accentFromArtwork && nowPlayingColors ? ArtworkColorPolicy.Default : new ArtworkColorPolicy(accentFromArtwork, nowPlayingColors),
                                };
                        }
        return all;
    }

    private static int IndexOf(SidebarLayoutId layout, SidebarDensity density, bool hideArt, DetailPageLayout pageLayout, bool railUniform, TrackRowStyle rowStyle,
        bool showCovers, bool accentFromArtwork, bool nowPlayingColors)
        => ((nowPlayingColors ? 0 : 2) + (accentFromArtwork ? 0 : 1)) * 128 + (showCovers ? 0 : 64)
            + ((((((int)rowStyle * 2 + (railUniform ? 1 : 0)) * 2) + (int)pageLayout) * 2 + (hideArt ? 1 : 0)) * 4) + (int)layout * 2 + (int)density;

    /// <summary>The bundle for the options the structure published: <see cref="Bundle"/> with the pane's <see cref="NavPanePolicy.Layout"/> and
    /// <see cref="NavPanePolicy.Density"/> set (and <see cref="NavPanePolicy.ShowCovers"/>, S0), the track lists' artwork hidden when <paramref name="hideTrackArt"/>, the detail pages' layout and rail
    /// uniformity, the track rows' skin (M2) and whether the page accent and the now-playing colours follow the artwork (CT9). One instance per combination, so
    /// republishing the facts is not a change.</summary>
    public static ThemePolicies For(SidebarLayoutId layout, SidebarDensity density, bool hideTrackArt = false,
        DetailPageLayout pageLayout = DetailPageLayout.Automatic, bool railUniform = false, TrackRowStyle rowStyle = TrackRowStyle.Modern,
        bool showCovers = true, bool accentFromArtwork = true, bool nowPlayingColors = true)
        => s_byOptions[IndexOf(layout, density, hideTrackArt, pageLayout, railUniform, rowStyle, showCovers, accentFromArtwork, nowPlayingColors)];
}
