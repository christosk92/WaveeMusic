// ── Themes/Zune/Structure/ZuneOptions.cs — Zune's structure option values and their reads (themes plan 6.3; P5 Z0) ──────────────────
//
// The enums the Zune structure's options and parameters store (their values are what is stored: an appended member never re-means a
// stored value), the bundle of every value the policies depend on (`ZuneParameters`, what `ZunePolicies.For` memoizes on), and the
// tracked reads a Zune view uses where it draws with an option (WVTH005: only this structure reads its options). The options themselves
// are declared in `ZuneStructure.cs`.

using Wavee.Theming;

namespace Wavee.Themes.Zune;

/// <summary>What sits behind page titles (`structure.background`).</summary>
public enum ZuneBackground
{
    /// <summary>The top of albums, artists and playlists is tinted from their cover.</summary>
    ArtworkWash = 0,
    /// <summary>Artist photos drift behind Home and now playing.</summary>
    Mix = 1,
    /// <summary>The flat ground.</summary>
    None = 2,
}

/// <summary>How a panorama title meets the window edge (`structure.panoramaFit`).</summary>
public enum PanoramaFit
{
    /// <summary>The title bleeds off the edge.</summary>
    Bleed = 0,
    /// <summary>The title fits the window (one line, ellipsis).</summary>
    Fit = 1,
}

/// <summary>What the band's row 1 does when its pivots do not fit (structure parameter `pivotOverflow`).</summary>
public enum PivotOverflow
{
    /// <summary>The pivots scroll under an edge fade.</summary>
    EdgeFadeScroll = 0,
    /// <summary>A "more" item opens the rest in a menu.</summary>
    MoreMenu = 1,
}

/// <summary>Where the play bar's elapsed and remaining times sit (structure parameter `seekTimes`).</summary>
public enum SeekTimes
{
    /// <summary>In the face column, under the title.</summary>
    InFace = 0,
    /// <summary>At both ends of the seek row.</summary>
    AtSliderEnds = 1,
}

/// <summary>How the right panel is placed (structure parameter `panelMode`). Named apart from the contract's <c>PanelMode</c> (which panel
/// body is shown).</summary>
public enum ZunePanelMode
{
    /// <summary>Inline beside the page from the Standard edge, an overlay below it.</summary>
    InlineWhenRoom = 0,
    /// <summary>Always an overlay.</summary>
    AlwaysOverlay = 1,
}

/// <summary>Every structure value the Zune policies depend on: the three user options and the five structure parameters.</summary>
public readonly record struct ZuneParameters(
    ZuneBackground Background, PanoramaFit PanoramaFit, bool BandPins,
    bool CollapseBand, PivotOverflow PivotOverflow, SeekTimes SeekTimes, ZunePanelMode PanelMode, int MaxDockedPanes)
{
    /// <summary>The declared defaults.</summary>
    public static ZuneParameters Default => From(OptionValues.Empty);

    /// <summary>The values the composition built for the structure (user options, then the parameters' defaults and overrides).</summary>
    public static ZuneParameters From(OptionValues structure) => new(
        structure.Get(ZuneStructure.Background.Key),
        structure.Get(ZuneStructure.PanoramaFitOption.Key),
        structure.Get(ZuneStructure.BandPins.Key),
        structure.Get(ZuneStructure.CollapseBand.Key),
        structure.Get(ZuneStructure.PivotOverflowParameter.Key),
        structure.Get(ZuneStructure.SeekTimesParameter.Key),
        structure.Get(ZuneStructure.PanelModeParameter.Key),
        (int)MathF.Round(structure.Get(ZuneStructure.MaxDockedPanes.Key)));
}

/// <summary>Tracked reads of Zune's structure options, for a view that draws with one. Call them inside a render or a binding.</summary>
public static class ZuneOptions
{
    /// <summary>What sits behind page titles (the `background` option).</summary>
    public static ZuneBackground Background() => ThemeHost.Options.Get(ZuneStructure.Background.Key);

    /// <summary>How panorama titles meet the window edge (the `panoramaFit` option).</summary>
    public static PanoramaFit Fit() => ThemeHost.Options.Get(ZuneStructure.PanoramaFitOption.Key);

    /// <summary>The band shows its pins (the `bandPins` option; the window tier decides too: Standard and Huge show them, Small and Compact do not).</summary>
    public static bool BandPins() => ThemeHost.Options.Get(ZuneStructure.BandPins.Key);

    /// <summary>The band collapses to row 1 on scroll (the `collapseBand` parameter).</summary>
    public static bool CollapseBand() => ThemeHost.Options.Get(ZuneStructure.CollapseBand.Key);

    /// <summary>What the band's row 1 does when its pivots do not fit (the `pivotOverflow` parameter).</summary>
    public static PivotOverflow Overflow() => ThemeHost.Options.Get(ZuneStructure.PivotOverflowParameter.Key);

    /// <summary>Where the play bar's times sit (the `seekTimes` parameter).</summary>
    public static SeekTimes Times() => ThemeHost.Options.Get(ZuneStructure.SeekTimesParameter.Key);

    /// <summary>How the right panel is placed (the `panelMode` parameter).</summary>
    public static ZunePanelMode Panel() => ThemeHost.Options.Get(ZuneStructure.PanelModeParameter.Key);

    /// <summary>How many panes the right panel may dock at once (the `maxDockedPanes` parameter, 1-3).</summary>
    public static int MaxDockedPanes() => (int)MathF.Round(ThemeHost.Options.Get(ZuneStructure.MaxDockedPanes.Key));
}
