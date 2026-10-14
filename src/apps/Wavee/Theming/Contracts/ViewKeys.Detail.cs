// ── Theming/Contracts/ViewKeys.Detail.cs — the Detail frame family of published view keys (themes plan 6.0, T9a) ────────────────
//
// T9a publishes the SHARED detail frame (`Detail.Frame`): the scaffold of every album / playlist / Liked / show / episode page, the rail
// and its collapsed strip, the vertical show header, the vertical arm's hero (and its loading band, a style) and the insights sheet.
// The per-kind pages (T9b: album, playlist; T9c: Liked, show, episode) hand the frame a `FrameSpec` whose slots they build themselves;
// their sub-contract keys arrive with them, and the frame places whatever the slots return.
//
// What stays core: the page mode and its fail-safes, the Hero page-layout override, the rail cells and the rail prefs
// (`DetailRailPrefs`, `RailPolicy`), the accent, the shell-tint and page-tone claims, the data gate, the insights toggle and its facts
// latch, the table mount, the drop target, and `Detail.Config` / `Identity` / `FrameSlots` themselves.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>One detail page's scaffold: the tint and tone leaves, the notice strip, the content column and, per arm, the rail | grip |
    /// content row or the vertical page (the show header, the insights sheet). Mounted by `Detail.FrameHost`. Props: <see cref="DetailFrameProps"/>.</summary>
    public static readonly ViewKey<DetailFrameProps> DetailFrame = new("Detail.Frame");

    /// <summary>The expanded rail: the layer fill and its scroller around the identity column, behind one skeleton boundary. Mounted by the
    /// frame's two-column arm. Props: <see cref="DetailRailProps"/>.</summary>
    public static readonly ViewKey<DetailRailProps> DetailRail = new("Detail.Rail");

    /// <summary>The collapsed rail: the 96-DIP identity strip (cover, two-line title, expand chevron). Mounted by the frame's two-column arm.
    /// Props: <see cref="DetailCompactRailProps"/>.</summary>
    public static readonly ViewKey<DetailCompactRailProps> DetailCompactRail = new("Detail.CompactRail");

    /// <summary>The vertical show / episode header (cover 140, info column, CTA cluster). Mounted by the frame's vertical arm for a non-track
    /// page. Props: <see cref="DetailShowHeaderProps"/>.</summary>
    public static readonly ViewKey<DetailShowHeaderProps> DetailShowHeader = new("Detail.ShowHeader");

    /// <summary>The vertical arm's item 0: the expanded hero and the pinned context band, scroll-bound. Mounted by `Detail.Hero` (the table's
    /// item). Props: <see cref="DetailHeroProps"/>.</summary>
    public static readonly ViewKey<DetailHeroProps> DetailHero = new("Detail.Hero");

    /// <summary>The insights sheet: the facts bento as a right-anchored sheet over the vertical arm. Mounted by the frame. Props:
    /// <see cref="DetailInsightsProps"/>.</summary>
    public static readonly ViewKey<DetailInsightsProps> DetailInsights = new("Detail.Insights");

    /// <summary>One entry point to the insights sheet: the hero toolbar's button or the pinned band's word. Mounted by the hero's toolbar row and
    /// the table's band actions. Props: <see cref="DetailInsightsToggleProps"/>.</summary>
    public static readonly ViewKey<DetailInsightsToggleProps> DetailInsightsToggle = new("Detail.InsightsToggle");
}

public static partial class StyleKeys
{
    /// <summary>The hero's loading band (<see cref="DetailHeroStyle"/>): a pure builder the table reads through the ambient view registry,
    /// because the skeleton deriver cannot run a view slot.</summary>
    public static readonly StyleKey<DetailHeroStyle> DetailHero = new("Detail.HeroSkeleton");
}
