using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Layouts;

/// <summary>
/// Simple vertical stack for nested, already-scoped repeaters. Use this when an
/// outer repeater owns virtualization and the inner item count is small; it
/// avoids nested realization-rect races that can leave a region shell visible
/// while its section contents are not realized during fast scroll.
/// </summary>
public sealed partial class NonVirtualizingStackLayout : NonVirtualizingLayout
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(NonVirtualizingStackLayout),
            new PropertyMetadata(0.0, OnPropertyChanged));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NonVirtualizingStackLayout layout)
            layout.InvalidateMeasure();
    }

    private Size _lastSize;

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        try
        {
            var children = context.Children;
            var count = children.Count;
            if (count == 0)
            {
                _lastSize = new Size(0, 0);
                return _lastSize;
            }

            var width = ResolveWidth(availableSize.Width);
            var measureSize = new Size(width, double.PositiveInfinity);
            var spacing = Math.Max(0, Spacing);
            var totalHeight = 0d;
            var maxWidth = 0d;

            for (var i = 0; i < count; i++)
            {
                var child = children[i];
                child.Measure(measureSize);
                totalHeight += child.DesiredSize.Height;
                maxWidth = Math.Max(maxWidth, child.DesiredSize.Width);
                if (i < count - 1)
                    totalHeight += spacing;
            }

            _lastSize = new Size(width > 0 ? width : maxWidth, totalHeight);
            return _lastSize;
        }
        catch (ArgumentException)
        {
            return _lastSize;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // ItemsRepeater can briefly hand us a Children IVectorView whose
            // backing children-collection has been mutated mid-iteration —
            // "ItemsRepeater's child not found in its Children collection."
            // Under CsWinRT AOT the WinRT HRESULT comes through as
            // COMException, not ArgumentException. Either way the layout
            // race recovers on the next measure pass; return the previous
            // size so the host frame doesn't collapse.
            return _lastSize;
        }
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        try
        {
            var children = context.Children;
            var spacing = Math.Max(0, Spacing);
            var y = 0d;

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                var height = Math.Max(0, child.DesiredSize.Height);
                child.Arrange(new Rect(0, y, finalSize.Width, height));
                y += height + (i < children.Count - 1 ? spacing : 0);
            }
        }
        catch (ArgumentException)
        {
            // Collection mutated during layout; next pass will recover.
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Same race as MeasureOverride — recovers on the next arrange
            // pass. Under CsWinRT AOT the HRESULT surfaces as COMException
            // rather than ArgumentException.
        }

        return finalSize;
    }

    private static double ResolveWidth(double width)
        => double.IsNaN(width) || double.IsInfinity(width) || width <= 0 ? 1000 : width;
}
