using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.AccentIcon;

/// <summary>
/// A minimal two-layer icon. A <see cref="BasePathData"/> renders with the theme's icon
/// base brush and an <see cref="AccentPathData"/> renders with the app accent brush.
/// Use keyed styles (see AccentIcon.xaml) to declare individual icons — consumers apply
/// the style like <c>Style="{StaticResource App.AccentIcons.Media.Play}"</c>.
///
/// Inspired by Files' ThemedIcon (MIT) but stripped down to exactly the roles we need.
/// </summary>
public sealed class AccentIcon : Control
{
    public AccentIcon()
    {
        DefaultStyleKey = typeof(AccentIcon);
        IsTabStop = false;
    }

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize),
            typeof(double),
            typeof(AccentIcon),
            new PropertyMetadata(20.0));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public static readonly DependencyProperty BasePathDataProperty =
        DependencyProperty.Register(
            nameof(BasePathData),
            typeof(Geometry),
            typeof(AccentIcon),
            new PropertyMetadata(null));

    public Geometry? BasePathData
    {
        get => (Geometry?)GetValue(BasePathDataProperty);
        set => SetValue(BasePathDataProperty, value);
    }

    public static readonly DependencyProperty AccentPathDataProperty =
        DependencyProperty.Register(
            nameof(AccentPathData),
            typeof(Geometry),
            typeof(AccentIcon),
            new PropertyMetadata(null));

    public Geometry? AccentPathData
    {
        get => (Geometry?)GetValue(AccentPathDataProperty);
        set => SetValue(AccentPathDataProperty, value);
    }
}
