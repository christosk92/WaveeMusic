using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using CommunityToolkit.WinUI.Animations;
using Wavee.WinUI.ViewModels;

namespace Wavee.WinUI.Controls;

/// <summary>
/// A reusable content card control with automatic hover animations (blur + shadow).
/// Displays media content (playlists, albums, artists, tracks) with interactive buttons.
/// </summary>
public sealed partial class ContentCard : UserControl
{
    private AnimationSet? _blurInAnimation;
    private AnimationSet? _blurOutAnimation;
    private AnimationSet? _overlayFadeInAnimation;
    private AnimationSet? _overlayFadeOutAnimation;

    public ContentCard()
    {
        this.InitializeComponent();

        // Get blur animation resources from local resources (defined in ContentCard.xaml)
        _blurInAnimation = this.Resources["BlurInAnimation"] as AnimationSet;
        _blurOutAnimation = this.Resources["BlurOutAnimation"] as AnimationSet;

        // Get overlay animation resources from application resources (defined in CardAnimations.xaml)
        _overlayFadeInAnimation = Application.Current.Resources["ButtonOverlayFadeInAnimation"] as AnimationSet;
        _overlayFadeOutAnimation = Application.Current.Resources["ButtonOverlayFadeOutAnimation"] as AnimationSet;
    }

    /// <summary>
    /// The content item to display in this card
    /// </summary>
    public ContentItemViewModel Content
    {
        get => (ContentItemViewModel)GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public static readonly DependencyProperty ContentProperty =
        DependencyProperty.Register(
            nameof(Content),
            typeof(ContentItemViewModel),
            typeof(ContentCard),
            new PropertyMetadata(null));

    /// <summary>
    /// Handles pointer entering the card - starts blur and overlay fade-in animations
    /// </summary>
    private async void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Start blur animation on the image
        if (_blurInAnimation != null && CardImage != null)
        {
            await _blurInAnimation.StartAsync(CardImage);
        }

        // Fade in the hover overlay with buttons
        if (_overlayFadeInAnimation != null && HoverOverlay != null)
        {
            await _overlayFadeInAnimation.StartAsync(HoverOverlay);
        }
    }

    /// <summary>
    /// Handles pointer exiting the card - starts blur and overlay fade-out animations
    /// </summary>
    private async void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Remove blur from the image
        if (_blurOutAnimation != null && CardImage != null)
        {
            await _blurOutAnimation.StartAsync(CardImage);
        }

        // Fade out the hover overlay
        if (_overlayFadeOutAnimation != null && HoverOverlay != null)
        {
            await _overlayFadeOutAnimation.StartAsync(HoverOverlay);
        }
    }

    /// <summary>
    /// Handles right-click on the card to show the context menu
    /// </summary>
    private void OnCardRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Show the context flyout at the tapped position
        if (MoreButton?.Flyout is MenuFlyout flyout && sender is FrameworkElement element)
        {
            flyout.ShowAt(element, new FlyoutShowOptions
            {
                Position = e.GetPosition(element),
                ShowMode = FlyoutShowMode.Standard
            });
            e.Handled = true;
        }
    }

    /// <summary>
    /// Handles long-press/hold on the card to show the context menu (touch-friendly)
    /// </summary>
    private void OnCardHolding(object sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState == Microsoft.UI.Input.HoldingState.Started)
        {
            // Show the context flyout at the held position
            if (MoreButton?.Flyout is MenuFlyout flyout && sender is FrameworkElement element)
            {
                flyout.ShowAt(element, new FlyoutShowOptions
                {
                    Position = e.GetPosition(element),
                    ShowMode = FlyoutShowMode.Standard
                });
                e.Handled = true;
            }
        }
    }
}
