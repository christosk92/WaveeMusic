using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Merch tile for the V4A "Tour &amp; Merch" right column. Square image with
/// price chip overlay + title + subtitle + Buy link. The image lives inside an
/// intrinsically-rounded <see cref="Border"/> with <see cref="MerchImageBrush"/> as
/// its background so the rounded clipping holds under all layers (per the
/// WinUI 3 rounded-clipping rule).
/// </summary>
public sealed partial class MerchCard : UserControl
{
    public event EventHandler<RoutedEventArgs>? CardClick;
    public event EventHandler<RoutedEventArgs>? BuyClick;

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(MerchCard),
            new PropertyMetadata(null, OnImageUrlChanged));

    public static readonly DependencyProperty PriceProperty =
        DependencyProperty.Register(nameof(Price), typeof(string), typeof(MerchCard),
            new PropertyMetadata(string.Empty, OnPriceChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(MerchCard),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(MerchCard),
            new PropertyMetadata(string.Empty, OnSubtitleChanged));

    public string? ImageUrl { get => (string?)GetValue(ImageUrlProperty); set => SetValue(ImageUrlProperty, value); }
    public string Price { get => (string)GetValue(PriceProperty); set => SetValue(PriceProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    public MerchCard()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (MerchImageBrush != null) MerchImageBrush.ImageSource = null;
    }

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MerchCard card) return;
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(e.NewValue as string);
        if (string.IsNullOrEmpty(httpsUrl))
        {
            card.MerchImageBrush.ImageSource = null;
            return;
        }
        card.MerchImageBrush.ImageSource = new BitmapImage(new Uri(httpsUrl))
        {
            DecodePixelWidth = 360,
            DecodePixelType = DecodePixelType.Logical,
        };
    }

    private static void OnPriceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MerchCard card)
        {
            var text = e.NewValue as string ?? string.Empty;
            card.PriceText.Text = text;
            card.PriceChip.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MerchCard card) card.TitleText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MerchCard card)
        {
            var text = e.NewValue as string ?? string.Empty;
            card.SubtitleText.Text = text;
            card.SubtitleText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void CardButton_Click(object sender, RoutedEventArgs e) => CardClick?.Invoke(this, e);
    private void BuyLink_Click(object sender, RoutedEventArgs e) => BuyClick?.Invoke(this, e);
}
