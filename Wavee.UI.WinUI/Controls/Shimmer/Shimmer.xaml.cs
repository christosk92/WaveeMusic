using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// A shimmer/skeleton loading placeholder control.
/// </summary>
public sealed partial class Shimmer : UserControl
{
    private Compositor? _compositor;
    private Visual? _shimmerVisual;

    public Shimmer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartAnimation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAnimation();
    }

    private void StartAnimation()
    {
        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _shimmerVisual = ElementCompositionPreview.GetElementVisual(ShimmerOverlay);

        if (_compositor == null || _shimmerVisual == null) return;

        // Set initial state
        _shimmerVisual.Opacity = 1;

        // Create translation animation that moves the gradient across
        var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.InsertKeyFrame(0f, new Vector3(-200, 0, 0));
        offsetAnimation.InsertKeyFrame(1f, new Vector3((float)ActualWidth + 200, 0, 0));
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(1500);
        offsetAnimation.IterationBehavior = AnimationIterationBehavior.Forever;

        _shimmerVisual.StartAnimation("Offset", offsetAnimation);
    }

    private void StopAnimation()
    {
        _shimmerVisual?.StopAnimation("Offset");
    }
}
