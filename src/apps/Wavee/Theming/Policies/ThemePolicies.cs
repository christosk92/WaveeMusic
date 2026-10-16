// ── Theming/Policies/ThemePolicies.cs — what a structure decides for the core (themes plan 5.5) ───────────────────────
//
//   · `ThemePolicies`      the policy bundle a structure returns: nav pane, panel, player bar, chrome, stage, and the window tiers
//                          (`Tiers`, T3b, `Lists`, M4, `Detail`, M8, and `Artwork`, CT9, init properties beside the five). Each member is
//                          its own file; the core state machines (`SidebarPaneModeRules`, `Shell.Chrome`, `PlayerBarLayout`,
//                          `Stage.Layout`, `PanelHost`, the mini player) take the member instead of Classic constants.
//   · `ShellCapabilities`  what the shell offers. `Unrestricted` is "every feature on", which is what Classic is today.
//   · `StyleMetrics`       the style's row heights and gutters (and the track rows at its density, `TrackRowMetrics`). A style publishes them; `Unset` (all zero) means "none".
//   · `TierFacts`          the window-tier fact (T3b): `Theming/Policies/TierFacts.cs`, with `TierPolicy` beside it.
//   · `ThemeFacts`         the bundle `ThemeHost.Facts` publishes and `ThemeFactsContext` mirrors for templates.

using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Theming;

/// <summary>The policy bundle of the active structure. Equality is by value, so an unchanged bundle republished is not a
/// change. The window tiers (<see cref="Tiers"/>) ride beside the positional policies.</summary>
/// <param name="NavPane">The navigation pane: window bands, widths, the seam.</param>
/// <param name="Panel">The right panel and the mini player.</param>
/// <param name="PlayerBar">The player bar's pressure levels.</param>
/// <param name="Chrome">The title-bar row's budget.</param>
/// <param name="Stage">The fullscreen stage's layout classes.</param>
public sealed record ThemePolicies(NavPanePolicy NavPane, PanelPolicy Panel, PlayerBarPolicy PlayerBar, ChromePolicy Chrome, StagePolicy Stage)
{
    /// <summary>The shell as it shipped before themes (what Wavee Classic declares). Published until a composition is
    /// installed, and in place of a bundle that fails <see cref="Validate"/>, so a core state machine always has a policy it
    /// can run on.</summary>
    public static ThemePolicies Default { get; } = new(
        NavPanePolicy.Default, PanelPolicy.Default, PlayerBarPolicy.Default, ChromePolicy.Default, StagePolicy.Default);

    /// <summary>The structure's window tiers (T3b). A structure that declares none is Standard at every size; the core never
    /// names a theme, so <see cref="Default"/> carries <see cref="TierPolicy.Standard"/> and Classic's bundle replaces it.</summary>
    public TierPolicy Tiers { get; init; } = TierPolicy.Standard;

    /// <summary>What the structure decides for the track lists (M4: whether track artwork is drawn). A structure that declares nothing shows it.</summary>
    public ListPolicy Lists { get; init; } = ListPolicy.Default;

    /// <summary>What the structure decides for the detail pages (M8: the page layout and whether the rails share one size). A structure that declares nothing is the shell as it shipped.</summary>
    public DetailPolicy Detail { get; init; } = DetailPolicy.Default;

    /// <summary>What the structure decides about colour taken from artwork (CT9: the page accent and the now-playing colours). A structure that declares nothing follows the artwork.</summary>
    public ArtworkColorPolicy Artwork { get; init; } = ArtworkColorPolicy.Default;

    /// <summary>What the structure decides about the overlays the core composes, per window band (TC9: the dialog card's fit, the toast lane's dock, the vacancy, the
    /// standalone selection bar, context menus as a sheet). A structure that declares nothing is the shell as it shipped.</summary>
    public OverlayPolicy Overlays { get; init; } = OverlayPolicy.Default;

    /// <summary>Everything wrong with the bundle, empty when every core state machine can run on it.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (NavPane is null) problems.Add("NavPane is missing"); else problems.AddRange(NavPane.Validate());
        if (Panel is null) problems.Add("Panel is missing"); else problems.AddRange(Panel.Validate());
        if (PlayerBar is null) problems.Add("PlayerBar is missing"); else problems.AddRange(PlayerBar.Validate());
        if (Chrome is null) problems.Add("Chrome is missing"); else problems.AddRange(Chrome.Validate());
        if (Stage is null) problems.Add("Stage is missing"); else problems.AddRange(Stage.Validate());
        if (Tiers is null) problems.Add("Tiers is missing"); else problems.AddRange(Tiers.Validate());
        if (Lists is null) problems.Add("Lists is missing");
        if (Detail is null) problems.Add("Detail is missing");
        if (Artwork is null) problems.Add("Artwork is missing");
        if (Overlays is null) problems.Add("Overlays is missing"); else problems.AddRange(Overlays.Validate());
        return problems;
    }
}

/// <summary>What the shell offers under the active structure.</summary>
/// <param name="Tabs">The tab strip.</param>
/// <param name="NavTree">The nested navigation tree in the nav pane.</param>
/// <param name="FriendsPanel">The friends panel.</param>
/// <param name="ForwardButton">A forward button next to back.</param>
/// <param name="HistoryMenu">The back/forward history menu.</param>
/// <param name="MaxBandPins">How many pins the nav band holds (<see cref="int.MaxValue"/> = unlimited).</param>
public sealed record ShellCapabilities(bool Tabs, bool NavTree, bool FriendsPanel, bool ForwardButton, bool HistoryMenu, int MaxBandPins)
{
    /// <summary>Every capability on and no pin limit: what the shell is today (Classic).</summary>
    public static ShellCapabilities Unrestricted { get; } = new(true, true, true, true, true, int.MaxValue);
}

/// <summary>The style's metrics: the row heights, the gutters and the control height a structure lays out with. A value
/// type, so a style hands them out without allocating.</summary>
public readonly record struct StyleMetrics(
    float RowSingle, float RowDouble, float RowWithArt, float ControlHeight,
    float PivotRow, float SubPivotRow, float SeekRow, float TransportRow, float SearchWidth,
    float Gutter, float Section, float MinPageColumn, float PanoramaCompactBelow,
    TrackRowMetrics TrackRows = default)
{
    /// <summary>No metric published (every member 0): what a style that publishes none answers. Wavee Classic publishes its own
    /// (<c>ClassicStyle.Metrics</c>).</summary>
    public static StyleMetrics Unset => default;
}

/// <summary>The track rows' geometry at the style's current density (M3): what the core's track table sizes its rows, art and episode rows with. A style
/// whose density is an option publishes the rung the user chose; `default` (every member 0) is "none published", which the core reads as
/// <see cref="Standard"/>. The style publishes both skins' ladders (<c>Modern*</c> and <c>Classic*</c>) because the row style is a STRUCTURE option the style layer cannot read (WVTH005); the core picks by <see cref="ListPolicy.RowStyle"/>.</summary>
/// <param name="Rung">The density rung, 0 the tightest: part of the list's re-key and the episode row's "narrow" decision.</param>
/// <param name="ModernRow">The Modern skin's row height.</param>
/// <param name="ModernArt">The Modern skin's art edge.</param>
/// <param name="ClassicRow">The Classic skin's row height.</param>
/// <param name="ClassicArt">The Classic skin's art edge.</param>
/// <param name="EpisodeRow">The episode row's height (two title lines, a clamped description and the meta line).</param>
/// <param name="NarrowEpisode">The episode row drops to its narrow arm at this rung.</param>
public readonly record struct TrackRowMetrics(int Rung, float ModernRow, float ModernArt, float ClassicRow, float ClassicArt, float EpisodeRow, bool NarrowEpisode)
{
    /// <summary>Nothing published (every member 0).</summary>
    public bool IsUnset => ModernRow <= 0f;

    /// <summary>The shell's rows before themes, at the Default rung (48 / 32 Modern, 40 / 32 Classic, 96 episode): what the core uses while no composition
    /// publishes any. `TrackFormatTests` pins Classic's Default rung equal to it.</summary>
    public static TrackRowMetrics Standard { get; } = new(Rung: 1, ModernRow: 48f, ModernArt: 32f, ClassicRow: 40f, ClassicArt: 32f, EpisodeRow: 96f, NarrowEpisode: true);
}

/// <summary>What the active composition publishes for the core: policies, capabilities, metrics and tiers. Core models
/// read <see cref="ThemeHost.Facts"/> (a signal); templates read <see cref="ThemeFactsContext"/>.</summary>
public sealed record ThemeFacts(ThemePolicies Policies, ShellCapabilities Capabilities, StyleMetrics Metrics, TierFacts Tiers)
{
    /// <summary>The facts before any composition is installed.</summary>
    public static ThemeFacts Default { get; } = new(ThemePolicies.Default, ShellCapabilities.Unrestricted, StyleMetrics.Unset, TierFacts.Unknown);
}

/// <summary>The context that carries <see cref="ThemeHost.Facts"/> to templates. The value is the SIGNAL, not a snapshot: the
/// provider sits at the root, which renders once per theme switch, so a snapshot would go stale on every live change.
/// A template reads <c>ctx.Hooks.UseContext(ThemeFactsContext.Signal).Value</c> (the read subscribes it).</summary>
public static class ThemeFactsContext
{
    /// <summary>The provided signal. Null only where no root provides it (a bare unit test of a view).</summary>
    public static readonly Context<IReadSignal<ThemeFacts>?> Signal = new(null);
}
