using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Wavee.UI.WinUI.Controls.HeroHeader;

/// <summary>
/// One-line fade-strategy factories for <see cref="ShyHeaderController"/>'s
/// <c>applyHeroFade</c> parameter. Each factory returns an
/// <c>Action&lt;double&gt;</c> that takes the controller's
/// <c>VerticalOffset / hero.ActualHeight</c> progress (0..1) and applies the
/// corresponding visual fade.
/// </summary>
public static class ShyHeaderFade
{
    /// <summary>
    /// Detail-page case — let <see cref="HeroHeader"/> handle the dead-zone
    /// and opacity math via its <see cref="HeroHeader.ScrollFadeProgress"/>
    /// dependency property.
    /// </summary>
    public static Action<double> ForHeroHeader(HeroHeader header)
        => p => header.ScrollFadeProgress = p;

    /// <summary>
    /// Library-page case — drive composition opacity directly with a 15 %
    /// dead zone so a tiny scroll doesn't soften the hero before the user
    /// commits to scrolling. Mirrors the curve <see cref="HeroHeader"/> uses
    /// internally, just applied to an arbitrary element.
    /// </summary>
    public static Action<double> ForCompositionOpacity(UIElement element, double deadZone = 0.15)
        => p =>
        {
            var faded = p <= deadZone ? 0.0 : (p - deadZone) / (1.0 - deadZone);
            ElementCompositionPreview.GetElementVisual(element).Opacity = (float)(1.0 - faded);
        };

    /// <summary>
    /// ConcertPage style — drive a specific element's <see cref="UIElement.Opacity"/>
    /// continuously (no dead zone). Used when the thing being faded isn't the
    /// hero itself but a sibling (e.g. the feature tile that sits on top).
    /// </summary>
    public static Action<double> ForElementOpacity(FrameworkElement element)
        => p => element.Opacity = 1.0 - p;
}
