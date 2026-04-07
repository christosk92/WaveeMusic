using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class MediaCard : UserControl
{
    private static ImageCacheService? _imageCache;

    public event EventHandler<RoutedEventArgs>? CardClick;
    public event EventHandler<RightTappedRoutedEventArgs>? CardRightTapped;

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(MediaCard),
            new PropertyMetadata(null, OnImageUrlChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(MediaCard),
            new PropertyMetadata("", OnTitleChanged));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(MediaCard),
            new PropertyMetadata("", OnSubtitleChanged));

    public static readonly DependencyProperty CardSizeProperty =
        DependencyProperty.Register(nameof(CardSize), typeof(double), typeof(MediaCard),
            new PropertyMetadata(160.0, OnCardSizeChanged));

    public static readonly DependencyProperty PlaceholderGlyphProperty =
        DependencyProperty.Register(nameof(PlaceholderGlyph), typeof(string), typeof(MediaCard),
            new PropertyMetadata("\uE8D6"));

    public string? ImageUrl { get => (string?)GetValue(ImageUrlProperty); set => SetValue(ImageUrlProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public double CardSize { get => (double)GetValue(CardSizeProperty); set => SetValue(CardSizeProperty, value); }
    public string PlaceholderGlyph { get => (string)GetValue(PlaceholderGlyphProperty); set => SetValue(PlaceholderGlyphProperty, value); }

    public MediaCard()
    {
        InitializeComponent();
        UpdateSize(160);
    }

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MediaCard card && e.NewValue is string url)
        {
            var httpsUrl = SpotifyImageHelper.ToHttpsUrl(url);
            if (!string.IsNullOrEmpty(httpsUrl))
            {
                _imageCache ??= Ioc.Default.GetService<ImageCacheService>();
                card.CardImage.Source = _imageCache?.GetOrCreate(httpsUrl, 200);
                card.PlaceholderIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                card.CardImage.Source = null;
                card.PlaceholderIcon.Visibility = Visibility.Visible;
            }
        }
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MediaCard card) card.TitleText.Text = e.NewValue as string ?? "";
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MediaCard card)
        {
            var text = e.NewValue as string ?? "";
            card.SubtitleText.Text = text;
            card.SubtitleText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static void OnCardSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MediaCard card && e.NewValue is double size)
            card.UpdateSize(size);
    }

    private void UpdateSize(double size)
    {
        RootPanel.Width = size;
        ImageContainer.Width = size;
        ImageContainer.Height = size;
    }

    private void CardButton_Click(object sender, RoutedEventArgs e)
        => CardClick?.Invoke(this, e);

    private void CardButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        CardRightTapped?.Invoke(this, e);
        e.Handled = true;
    }
}
