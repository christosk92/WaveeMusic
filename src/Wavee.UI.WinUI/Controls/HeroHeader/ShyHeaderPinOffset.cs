using System;
using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.Controls.HeroHeader;

/// <summary>
/// Pin-offset formulas for <see cref="ShyHeaderController"/>. Each factory
/// returns a <c>Func&lt;double&gt;</c> the controller re-evaluates on every
/// scroll tick so a window resize that changes the hero height is reflected
/// without needing to rewire the controller.
/// </summary>
public static class ShyHeaderPinOffset
{
    /// <summary>
    /// Pin once the user has scrolled past <c>hero.ActualHeight - margin</c>.
    /// Default 120 px matches ArtistPage / detail pages; library banners
    /// pass <c>margin: 90</c> because their banner is shorter.
    /// </summary>
    public static Func<double> Below(FrameworkElement hero, double margin = 120)
        => () => Math.Max(0, hero.ActualHeight - margin);

    /// <summary>
    /// ProfilePage style — pin once the user has scrolled past an arbitrary
    /// element (the identity card) plus a small lead so the shy pill
    /// appears just as the card crosses the top of the page.
    /// </summary>
    public static Func<double> BelowElement(FrameworkElement element, double lead = 32)
        => () => Math.Max(0, element.ActualHeight + lead);
}
