using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.Browse;

/// <summary>
/// Reusable responsive chip grid built on ItemsRepeater + UniformGridLayout.
/// Callers provide the data and item template; this control owns only the grid
/// layout defaults shared by browse/category surfaces.
/// </summary>
public sealed partial class BrowseGrid : UserControl
{
    public BrowseGrid()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(object),
        typeof(BrowseGrid),
        new PropertyMetadata(null));

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate),
        typeof(DataTemplate),
        typeof(BrowseGrid),
        new PropertyMetadata(null));

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth),
        typeof(double),
        typeof(BrowseGrid),
        new PropertyMetadata(140d));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public static readonly DependencyProperty MinItemHeightProperty = DependencyProperty.Register(
        nameof(MinItemHeight),
        typeof(double),
        typeof(BrowseGrid),
        new PropertyMetadata(28d));

    public double MinItemHeight
    {
        get => (double)GetValue(MinItemHeightProperty);
        set => SetValue(MinItemHeightProperty, value);
    }

    public static readonly DependencyProperty MinRowSpacingProperty = DependencyProperty.Register(
        nameof(MinRowSpacing),
        typeof(double),
        typeof(BrowseGrid),
        new PropertyMetadata(3d));

    public double MinRowSpacing
    {
        get => (double)GetValue(MinRowSpacingProperty);
        set => SetValue(MinRowSpacingProperty, value);
    }

    public static readonly DependencyProperty MinColumnSpacingProperty = DependencyProperty.Register(
        nameof(MinColumnSpacing),
        typeof(double),
        typeof(BrowseGrid),
        new PropertyMetadata(6d));

    public double MinColumnSpacing
    {
        get => (double)GetValue(MinColumnSpacingProperty);
        set => SetValue(MinColumnSpacingProperty, value);
    }

    public static readonly DependencyProperty ItemsStretchProperty = DependencyProperty.Register(
        nameof(ItemsStretch),
        typeof(UniformGridLayoutItemsStretch),
        typeof(BrowseGrid),
        new PropertyMetadata(UniformGridLayoutItemsStretch.Fill));

    public UniformGridLayoutItemsStretch ItemsStretch
    {
        get => (UniformGridLayoutItemsStretch)GetValue(ItemsStretchProperty);
        set => SetValue(ItemsStretchProperty, value);
    }
}
