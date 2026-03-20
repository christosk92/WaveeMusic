using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class ArtistCircleCard : UserControl
{
    public event EventHandler<RoutedEventArgs>? CardClick;
    public event EventHandler<RightTappedRoutedEventArgs>? CardRightTapped;

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(ArtistCircleCard),
            new PropertyMetadata(null, OnImageUrlChanged));

    public static readonly DependencyProperty DisplayNameProperty =
        DependencyProperty.Register(nameof(DisplayName), typeof(string), typeof(ArtistCircleCard),
            new PropertyMetadata("", OnDisplayNameChanged));

    public static readonly DependencyProperty MetadataProperty =
        DependencyProperty.Register(nameof(Metadata), typeof(string), typeof(ArtistCircleCard),
            new PropertyMetadata(null, OnMetadataChanged));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(ArtistCircleCard),
            new PropertyMetadata(80.0, OnSizeChanged));

    public string? ImageUrl { get => (string?)GetValue(ImageUrlProperty); set => SetValue(ImageUrlProperty, value); }
    public string DisplayName { get => (string)GetValue(DisplayNameProperty); set => SetValue(DisplayNameProperty, value); }
    public string? Metadata { get => (string?)GetValue(MetadataProperty); set => SetValue(MetadataProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public ArtistCircleCard()
    {
        InitializeComponent();
        UpdateSize(80);
    }

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArtistCircleCard card && e.NewValue is string url)
        {
            var httpsUrl = SpotifyImageHelper.ToHttpsUrl(url);
            if (!string.IsNullOrEmpty(httpsUrl))
            {
                card.CardImage.Source = new BitmapImage(new Uri(httpsUrl));
                card.PlaceholderIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                card.CardImage.Source = null;
                card.PlaceholderIcon.Visibility = Visibility.Visible;
            }
        }
    }

    private static void OnDisplayNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArtistCircleCard card) card.NameText.Text = e.NewValue as string ?? "";
    }

    private static void OnMetadataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArtistCircleCard card)
        {
            var text = e.NewValue as string;
            card.MetadataText.Text = text ?? "";
            card.MetadataText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArtistCircleCard card && e.NewValue is double size)
            card.UpdateSize(size);
    }

    private void UpdateSize(double size)
    {
        RootPanel.Width = size + 20; // padding for text
        ImageContainer.Width = size;
        ImageContainer.Height = size;
        ImageContainer.CornerRadius = new CornerRadius(size / 2);
    }

    private void CardButton_Click(object sender, RoutedEventArgs e)
        => CardClick?.Invoke(this, e);

    private void CardButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        CardRightTapped?.Invoke(this, e);
        e.Handled = true;
    }
}
