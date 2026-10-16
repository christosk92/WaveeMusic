// ── Themes/Classic/Structure/Rules/ClassicPageHeadRules.cs — the numbers of Classic's page head (breadcrumb B, Classic tuning F5) ──────
//
// Breadcrumb B (`Views\Masthead\ClassicPageHead.cs`): one page title under a small trail of only its ancestors. The trail's slot, its links' look,
// the depth below which it draws nothing, and the reserve a page under the overlay masthead keeps for each `PageHeadForm`, so a head keeps one
// height on its route whatever its trail turns out to be. Public so `Wavee.Tests` pins the numbers (the assembly has no InternalsVisibleTo).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The numbers of Classic's page head.</summary>
public static class ClassicPageHeadRules
{
    /// <summary>The trail's slot: 28 tall, its links sitting on the slot's bottom edge.</summary>
    public const float TrailSlotHeight = 28f;

    /// <summary>The gap between two trail links and the <c>›</c> between them.</summary>
    public const float TrailLinkGap = 6f;

    /// <summary>The trail links' weight (12/16/600 on the caption rung).</summary>
    public const ushort TrailWeight = 600;

    /// <summary>The shortest trail (the current page included) that draws its ancestors: a page two levels deep has none, because the
    /// sidebar's selected parent already says where it lives.</summary>
    public const int TrailMinDepth = 3;

    /// <summary>The gap between the trail and the title (4).</summary>
    public static float TrailTitleGap => Spacing.XS;

    /// <summary>The gap between a hero page's trail and its hero (12).</summary>
    public static float HeroGap => Spacing.M;

    /// <summary>The height a route's head takes under the band's top inset: the 52-DIP title line alone (Title); the trail's slot and its gap over
    /// it (Drill); the trail's slot alone on a page whose hero carries its title (Hero). Fixed per form, so a trail that appears or not never
    /// moves the page.</summary>
    public static float HeadHeight(PageHeadForm form) => form switch
    {
        PageHeadForm.Drill => TrailSlotHeight + TrailTitleGap + BrowseMastheadMetrics.TitleLine,
        PageHeadForm.Hero => TrailSlotHeight,
        _ => BrowseMastheadMetrics.TitleLine,
    };

    /// <summary>The overlay band's reserve for a route's head: the band's top inset plus <see cref="HeadHeight"/>. A page under the band spaces
    /// its body by this and cuts its scrolling content at it. <see cref="PageHeadForm.Title"/>'s is <see cref="BrowseMastheadMetrics.Reserve"/>.</summary>
    public static float HeadReserve(PageHeadForm form) => ClassicFrameBitsRules.MastheadFrameTop + HeadHeight(form);

    /// <summary>A drill-in page's body padding under the band: the page <paramref name="gutter"/> either side (the content tier's,
    /// <see cref="ClassicTiers.PageOf"/>), the drill reserve plus the 16-DIP gap under the head, and the page's own bottom.</summary>
    public static Edges4 DrillBodyPad(float gutter, float bottom) => new(gutter, HeadReserve(PageHeadForm.Drill) + Spacing.L, gutter, bottom);
}
