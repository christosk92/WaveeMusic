using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace Wavee.Controls.Lyrics.Controls;

public sealed partial class ShadowImage : UserControl
{
    public ImageSource? Source
    {
        get { return (ImageSource?)GetValue(SourceProperty); }
        set { SetValue(SourceProperty, value); }
    }

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(ShadowImage), new PropertyMetadata(null));

    public Stretch Stretch
    {
        get { return (Stretch)GetValue(StretchProperty); }
        set { SetValue(StretchProperty, value); }
    }

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(nameof(Stretch), typeof(Stretch), typeof(ShadowImage), new PropertyMetadata(Stretch.Uniform));

    public int CornerRadiusAmount
    {
        get { return (int)GetValue(CornerRadiusAmountProperty); }
        set { SetValue(CornerRadiusAmountProperty, value); }
    }

    public static readonly DependencyProperty CornerRadiusAmountProperty =
        DependencyProperty.Register(nameof(CornerRadiusAmount), typeof(int), typeof(ShadowImage), new PropertyMetadata(0, OnLayoutPropertyChanged));

    public int ShadowAmount
    {
        get { return (int)GetValue(ShadowAmountProperty); }
        set { SetValue(ShadowAmountProperty, value); }
    }

    public static readonly DependencyProperty ShadowAmountProperty =
        DependencyProperty.Register(nameof(ShadowAmount), typeof(int), typeof(ShadowImage), new PropertyMetadata(0, OnLayoutPropertyChanged));

    public ShadowImage()
    {
        InitializeComponent();
    }

    private void ShadowCastGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateShadowLayout();
    }

    private void ShadowRect_Loaded(object sender, RoutedEventArgs e)
    {
        DropShadow.Receivers.Add(ShadowCastGrid);
        UpdateShadowLayout();
    }

    private void ShadowRect_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateShadowLayout();
    }

    private void UpdateShadowLayout()
    {
        if (ShadowCastGrid == null || ShadowRect == null) return;

        var w = ShadowCastGrid.ActualWidth;
        var h = ShadowCastGrid.ActualHeight;
        if (w <= 0 || h <= 0) return;

        ShadowRect.Width = w;
        ShadowRect.Height = h;
        ShadowRect.CornerRadius = new CornerRadius(CornerRadiusAmount);
        ShadowRect.Translation = new Vector3(0, 0, ShadowAmount);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShadowImage si) si.UpdateShadowLayout();
    }
}
