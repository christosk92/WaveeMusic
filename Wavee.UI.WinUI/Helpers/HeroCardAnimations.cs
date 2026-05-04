using System;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Composition-driven hero-card selection animations for the PodcastBrowsePage carousel.
/// Mirrors the AI Dev Gallery <c>HeaderTile.OnIsSelectedChanged</c> behaviour from a
/// DataTemplate context: bind <see cref="IsSelectedProperty"/> on each card root and the
/// scale/shadow animation runs on the GPU off the UI thread.
/// </summary>
public static class HeroCardAnimations
{
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.RegisterAttached(
            "IsSelected",
            typeof(bool),
            typeof(HeroCardAnimations),
            new PropertyMetadata(false, OnIsSelectedChanged));

    public static void SetIsSelected(DependencyObject element, bool value)
        => element.SetValue(IsSelectedProperty, value);

    public static bool GetIsSelected(DependencyObject element)
        => (bool)element.GetValue(IsSelectedProperty);

    private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        // Scale animation removed — in the layered-stage model the incoming card
        // would visibly slide in at 0.965 while the active card is at 1.0,
        // creating a size-mismatch step (Image #16). Cards stay at scale 1.0 and
        // we read active vs inactive via the colored shadow grow/shrink only.
        var selected = (bool)e.NewValue;
        var animation = selected
            ? new AnimationSet
              {
                  new OpacityDropShadowAnimation { To = 0.55, Duration = TimeSpan.FromMilliseconds(600) },
                  new BlurRadiusDropShadowAnimation { To = 28, Duration = TimeSpan.FromMilliseconds(600) },
              }
            : new AnimationSet
              {
                  new OpacityDropShadowAnimation { To = 0.30, Duration = TimeSpan.FromMilliseconds(350) },
                  new BlurRadiusDropShadowAnimation { To = 12, Duration = TimeSpan.FromMilliseconds(350) },
              };

        animation.Start(element);
    }
}
