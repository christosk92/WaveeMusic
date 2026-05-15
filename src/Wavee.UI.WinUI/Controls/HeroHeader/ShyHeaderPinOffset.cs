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
    ///
    /// Returns <see cref="double.PositiveInfinity"/> when the hero hasn't
    /// been measured yet (<c>ActualHeight &lt;= 0</c>) so the controller can
    /// never decide <c>shouldPin = true</c> against an unmeasured hero —
    /// otherwise <c>Max(0, 0 - 120) = 0</c> would mean every scroll offset
    /// (including the post-nav offset=0 case) qualifies as pinned, and the
    /// controller would morph the shy pill in over the still-loading hero.
    /// </summary>
    public static Func<double> Below(FrameworkElement hero, double margin = 120)
        => () =>
        {
            var h = hero.ActualHeight;
            if (h <= 0) return double.PositiveInfinity;
            return Math.Max(0, h - margin);
        };

    /// <summary>
    /// ProfilePage style — pin once the user has scrolled past an arbitrary
    /// element (the identity card) plus a small lead so the shy pill
    /// appears just as the card crosses the top of the page. Same
    /// unmeasured-element guard as <see cref="Below"/>.
    /// </summary>
    public static Func<double> BelowElement(FrameworkElement element, double lead = 32)
        => () =>
        {
            var h = element.ActualHeight;
            if (h <= 0) return double.PositiveInfinity;
            return Math.Max(0, h + lead);
        };
}
