using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// Layout-aware zoom wrapper. Sets explicit Width/Height on its content so the XAML
/// layout system reflows naturally, then applies a ScaleTransform to fill the space.
/// No custom MeasureOverride — just standard XAML layout with constrained dimensions.
/// </summary>
public sealed class ZoomContentControl : ContentControl
{
    private readonly ScaleTransform _scaleTransform = new() { ScaleX = 1.0, ScaleY = 1.0 };

    public static readonly DependencyProperty ZoomProperty =
        DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(ZoomContentControl),
            new PropertyMetadata(1.0, OnZoomChanged));

    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public ZoomContentControl()
    {
        HorizontalContentAlignment = HorizontalAlignment.Left;
        VerticalContentAlignment = VerticalAlignment.Top;
        SizeChanged += OnSizeChanged;
    }

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ZoomContentControl)d).ApplyZoomLayout();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyZoomLayout();
    }

    private void ApplyZoomLayout()
    {
        if (Content is not FrameworkElement fe) return;

        var z = Math.Max(Zoom, 0.1);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Constrain content to (width/zoom, height/zoom) → forces layout reflow
        fe.Width = w / z;
        fe.Height = h / z;

        // Scale from top-left to fill the control — no overflow since (w/z * z) == w
        fe.RenderTransformOrigin = new Point(0, 0);
        if (fe.RenderTransform != _scaleTransform)
            fe.RenderTransform = _scaleTransform;

        _scaleTransform.ScaleX = z;
        _scaleTransform.ScaleY = z;
    }
}
