using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Animations;
using CommunityToolkit.WinUI.Media;

namespace Wavee.WinUI.Controls;
/// <summary>
/// A custom button control with shadow color support for interactive media cards
/// </summary>
public sealed partial class ShadowButton : Button
{
    private AttachedCardShadow? _attachedShadow;
    private Grid? _rootGrid;
    private AnimationSet? _hoverEnterAnimation;
    private AnimationSet? _hoverExitAnimation;
    private AnimationSet? _pressedAnimation;
    private AnimationSet? _releasedAnimation;

    public ShadowButton()
    {
        DefaultStyleKey = typeof(ShadowButton);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // Unsubscribe from previous events if reapplying template
        if (_rootGrid != null)
        {
            _rootGrid.PointerEntered -= OnRootGridPointerEntered;
            _rootGrid.PointerExited -= OnRootGridPointerExited;
            _rootGrid.PointerPressed -= OnRootGridPointerPressed;
            _rootGrid.PointerReleased -= OnRootGridPointerReleased;
        }

        // Get the Grid from the template that has the shadow attached
        if (GetTemplateChild("RootGrid") is Grid rootGrid)
        {
            _rootGrid = rootGrid;

            // Get the AttachedCardShadow from the Effects.Shadow attached property
            _attachedShadow = Effects.GetShadow(rootGrid) as AttachedCardShadow;

            // Apply the current shadow color if we have one
            if (_attachedShadow != null)
            {
                _attachedShadow.Color = ShadowColor;
            }

            // Get animation resources from application resources
            // These are defined in CardAnimations.xaml merged into Generic.xaml
            _hoverEnterAnimation = Application.Current.Resources["HoverEnterAnimation"] as AnimationSet;
            _hoverExitAnimation = Application.Current.Resources["HoverExitAnimation"] as AnimationSet;
            _pressedAnimation = Application.Current.Resources["PressedAnimation"] as AnimationSet;
            _releasedAnimation = Application.Current.Resources["ReleasedAnimation"] as AnimationSet;

            // Subscribe to pointer events for trim-safe animation triggering
            _rootGrid.PointerEntered += OnRootGridPointerEntered;
            _rootGrid.PointerExited += OnRootGridPointerExited;
            _rootGrid.PointerPressed += OnRootGridPointerPressed;
            _rootGrid.PointerReleased += OnRootGridPointerReleased;
        }
    }

    private async void OnRootGridPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverEnterAnimation != null && _rootGrid != null)
        {
            await _hoverEnterAnimation.StartAsync(_rootGrid);
        }
    }

    private async void OnRootGridPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverExitAnimation != null && _rootGrid != null)
        {
            await _hoverExitAnimation.StartAsync(_rootGrid);
        }
    }

    private async void OnRootGridPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedAnimation != null && _rootGrid != null)
        {
            await _pressedAnimation.StartAsync(_rootGrid);
        }
    }

    private async void OnRootGridPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_releasedAnimation != null && _rootGrid != null)
        {
            await _releasedAnimation.StartAsync(_rootGrid);
        }
    }

    /// <summary>
    /// Dependency property for the shadow color
    /// </summary>
    public static readonly DependencyProperty ShadowColorProperty =
        DependencyProperty.Register(
            nameof(ShadowColor),
            typeof(Color),
            typeof(ShadowButton),
            new PropertyMetadata(Colors.Black, OnShadowColorChanged)
        );

    private static void OnShadowColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (ShadowButton)d;
        var oldColor = (Color)e.OldValue;
        var newColor = (Color)e.NewValue;
        System.Diagnostics.Debug.WriteLine($"[ShadowButton] OnShadowColorChanged: Old=A={oldColor.A}, R={oldColor.R}, G={oldColor.G}, B={oldColor.B}, New=A={newColor.A}, R={newColor.R}, G={newColor.G}, B={newColor.B}");

        // Update the shadow color directly in C#
        if (button._attachedShadow != null)
        {
            button._attachedShadow.Color = newColor;
            System.Diagnostics.Debug.WriteLine($"[ShadowButton] Shadow color updated to: A={newColor.A}, R={newColor.R}, G={newColor.G}, B={newColor.B}");
        }
    }

    /// <summary>
    /// Gets or sets the color of the shadow effect
    /// </summary>
    public Color ShadowColor
    {
        get => (Color)GetValue(ShadowColorProperty);
        set => SetValue(ShadowColorProperty, value);
    }
}
