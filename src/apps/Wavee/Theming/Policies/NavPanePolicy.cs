// ── Theming/Policies/NavPanePolicy.cs — what a structure decides about the navigation pane (themes plan 5.5) ──────────
//
// The pane's STATE MACHINE is core (`SidebarPaneModeRules`, `SidebarResizeRules`, `FrameModel`): which band the window is
// in, what each band forces, how wide the docked pane presents, when a seam drag collapses it. The NUMBERS and the
// per-band answers are the structure's, and arrive here as a value. `Default` is the shell as it shipped before themes (the
// values Wavee Classic declares): used until a composition is installed and as the fallback of an invalid policy.
//
// WHERE the bands begin is not the pane's: the bands are the window's width tiers (T3b, `TierPolicy.Window`; Classic's
// edges are `Themes\Classic\Structure\Rules\Tiers.cs`) and `SidebarPaneModeRules.BandOf(WidthTier)` reads them. The policy
// decides what each band forces (`ModeFor(WidthTier)`), the width bounds and the seam rules.

using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>What the pane does in a window band. <see cref="Expanded"/> forces nothing: the user's collapse decides.</summary>
public enum NavPaneMode : byte
{
    /// <summary>Docked and expanded (or the user's collapsed rail): the band forces nothing.</summary>
    Expanded = 0,
    /// <summary>Forced rail: docked at the rail width, the toggle opens the overlay pane.</summary>
    Compact = 1,
    /// <summary>No docked pane and no rail: the toggle opens the overlay pane (the drawer).</summary>
    Overlay = 2,
    /// <summary>No pane at all in this band, and no overlay either.</summary>
    None = 3,
}

/// <summary>The ids of the structure options the core sidebar asks for BY NAME (a verb of the pane's menu, the migration, the undo
/// ring): a structure that offers the choice declares an option with that id and publishes its value through <see cref="NavPanePolicy.Layout"/> /
/// <see cref="NavPanePolicy.Density"/>; a structure that does not simply ignores the request.</summary>
public static class NavPaneOptionIds
{
    /// <summary>The sidebar layout choice (a <see cref="SidebarLayoutId"/>).</summary>
    public const string Layout = "sidebar";

    /// <summary>The entity-row density choice (a <see cref="SidebarDensity"/>).</summary>
    public const string Density = "libraryDensity";
}

/// <summary>The navigation pane's policy.</summary>
/// <param name="ContentFloorW">The content width the docked pane leaves: the expanded pane never presents wider than the
/// window minus this.</param>
/// <param name="MinWidth">The narrowest an expanded pane may be.</param>
/// <param name="MaxWidth">The widest an expanded pane may be.</param>
/// <param name="DefaultWidth">The width a fresh pane opens at, and the width a non-resizable pane always presents.</param>
/// <param name="CollapseApproach">How far below <see cref="MinWidth"/> a seam drag must go to collapse the pane to the rail.</param>
/// <param name="CollapseHysteresis">The drag-back distance that expands a collapsed pane again.</param>
/// <param name="NarrowMode">What the Narrow band forces.</param>
/// <param name="TinyMode">What the Tiny band forces.</param>
/// <param name="Resizable">The user can drag, step and set the expanded width. False: the pane presents
/// <see cref="DefaultWidth"/> and the seam does not exist.</param>
/// <param name="Seam">The seam strip's width where the pane is docked and resizable.</param>
/// <param name="RailWidth">The collapsed rail's width: what a compact pane presents, what an overlay leaves visible and the
/// floor of the seam drag (T5f: the row ladder that used to answer it from core is the structure's).</param>
/// <param name="Layout">Which of the two sidebar layouts the structure shows (M7a: the structure's choice, `structure.sidebar` in
/// Classic). The core sidebar state machine (the layout document, the undo ring, the menus) follows it: <c>Sidebar.FollowDesign</c>.</param>
/// <param name="Density">The entity-row density the structure shows (M7a: `structure.libraryDensity` in Classic).</param>
/// <param name="ShowCovers">The Classic layout's entity rows show their covers (tuned item S0: `structure.showCovers` in Classic). False
/// resolves those sections as text rows (<see cref="SidebarRowShape.EntityText"/>); the Library layout always shows covers.
/// Appended (contract 1.3).</param>
public sealed record NavPanePolicy(
    float ContentFloorW,
    float MinWidth, float MaxWidth, float DefaultWidth,
    float CollapseApproach, float CollapseHysteresis,
    NavPaneMode NarrowMode, NavPaneMode TinyMode, bool Resizable, float Seam, float RailWidth,
    SidebarLayoutId Layout = SidebarLayoutId.Classic, SidebarDensity Density = SidebarDensity.Default, bool ShowCovers = true)
{
    /// <summary>The shell before themes: a 480 content floor, an expanded
    /// pane of 180-460 (320 fresh), a drag collapse 64 below the minimum with a 24 hold, a forced rail in Narrow and the
    /// drawer in Tiny, a resizable pane with a 16-DIP seam.</summary>
    public static NavPanePolicy Default { get; } = new(
        ContentFloorW: SidebarPaneModeRules.ContentFloorW,
        MinWidth: SidebarPaneBounds.NavPaneMinW, MaxWidth: SidebarPaneBounds.NavPaneMaxW, DefaultWidth: SidebarPaneBounds.DefaultWidth,
        CollapseApproach: SidebarResizeRules.RegimePush, CollapseHysteresis: SidebarResizeRules.RegimeHysteresis,
        NarrowMode: NavPaneMode.Compact, TinyMode: NavPaneMode.Overlay, Resizable: true, Seam: Shell.FrameRules.SeamStripW,
        RailWidth: SidebarPaneBounds.RailWidth);

    /// <summary>The width a collapsed pane's drag falls to the rail at: <see cref="MinWidth"/> − <see cref="CollapseApproach"/>.</summary>
    public float RailEnterW => MinWidth - CollapseApproach;

    /// <summary>The width a collapsed pane's drag expands again at: <see cref="RailEnterW"/> + <see cref="CollapseHysteresis"/>.</summary>
    public float ExpandedEnterW => RailEnterW + CollapseHysteresis;

    /// <summary>The one clamp of an expanded-width PREFERENCE: inside [<see cref="MinWidth"/>, <see cref="MaxWidth"/>]; a
    /// non-finite width reads as <see cref="DefaultWidth"/>. A fixed (non-resizable) pane clamps the same way, so the user's
    /// saved width survives a visit to a theme that does not use it.</summary>
    public float ClampWidth(float width)
    {
        float w = float.IsFinite(width) ? width : DefaultWidth;
        return w < MinWidth ? MinWidth : w > MaxWidth ? MaxWidth : w;
    }

    /// <summary>The expanded width the pane PRESENTS for a preference: the clamped preference, or always
    /// <see cref="DefaultWidth"/> (clamped) when the pane is not resizable.</summary>
    public float ExpandedWidthFor(float preferred) => ClampWidth(Resizable ? preferred : DefaultWidth);

    /// <summary>What a band forces: Wide forces nothing (the user decides), Narrow and Tiny answer the policy.</summary>
    public NavPaneMode ForcedModeOf(SidebarWindowBand band) => band switch
    {
        SidebarWindowBand.Narrow => NarrowMode,
        SidebarWindowBand.Tiny => TinyMode,
        _ => NavPaneMode.Expanded,
    };

    /// <summary>What a window width tier forces: the tier's band (<see cref="SidebarPaneModeRules.BandOf(WidthTier)"/>) through
    /// <see cref="ForcedModeOf"/>.</summary>
    public NavPaneMode ModeFor(WidthTier tier) => ForcedModeOf(SidebarPaneModeRules.BandOf(tier));

    /// <summary>Everything wrong with the policy, empty when it can drive the pane.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        void Finite(string name, float v, bool allowZero)
        {
            if (!float.IsFinite(v) || v < 0f || (!allowZero && v == 0f)) problems.Add($"NavPane.{name} must be {(allowZero ? "zero or more" : "positive")} and finite (was {v})");
        }
        Finite(nameof(ContentFloorW), ContentFloorW, true);
        Finite(nameof(MinWidth), MinWidth, false);
        Finite(nameof(MaxWidth), MaxWidth, false);
        Finite(nameof(DefaultWidth), DefaultWidth, false);
        Finite(nameof(CollapseApproach), CollapseApproach, true);
        Finite(nameof(CollapseHysteresis), CollapseHysteresis, true);
        Finite(nameof(Seam), Seam, true);
        Finite(nameof(RailWidth), RailWidth, false);
        if (problems.Count > 0) return problems;
        if (MinWidth > MaxWidth) problems.Add("NavPane.MinWidth must not exceed MaxWidth");
        if (DefaultWidth < MinWidth || DefaultWidth > MaxWidth) problems.Add("NavPane.DefaultWidth must lie inside [MinWidth, MaxWidth]");
        if (!Enum.IsDefined(NarrowMode)) problems.Add("NavPane.NarrowMode is not a NavPaneMode");
        if (!Enum.IsDefined(TinyMode)) problems.Add("NavPane.TinyMode is not a NavPaneMode");
        if (!Enum.IsDefined(Layout)) problems.Add("NavPane.Layout is not a SidebarLayoutId");
        if (!Enum.IsDefined(Density)) problems.Add("NavPane.Density is not a SidebarDensity");
        return problems;
    }
}
