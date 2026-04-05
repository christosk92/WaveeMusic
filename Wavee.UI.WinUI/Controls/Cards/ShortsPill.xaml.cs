using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class ShortsPill : UserControl
{
    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(ShortsPill),
            new PropertyMetadata(null, OnImageUrlChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ShortsPill),
            new PropertyMetadata(null, OnTitleChanged));

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(HomeSectionItem), typeof(ShortsPill),
            new PropertyMetadata(null));

    public string? ImageUrl
    {
        get => (string?)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public HomeSectionItem? Item
    {
        get => (HomeSectionItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public ShortsPill()
    {
        InitializeComponent();
    }

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (ShortsPill)d;
        // Guard: template may not be applied yet
        if (pill.PillImage == null || pill.PlaceholderIcon == null) return;

        var url = e.NewValue as string;
        if (!string.IsNullOrEmpty(url))
        {
            var httpsUrl = SpotifyImageHelper.ToHttpsUrl(url);
            var cache = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ImageCacheService>();
            pill.PillImage.Source = cache?.GetOrCreate(httpsUrl, 100);
            pill.PlaceholderIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            pill.PillImage.Source = null;
            pill.PlaceholderIcon.Visibility = Visibility.Visible;
        }
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (ShortsPill)d;
        if (pill.TitleText == null) return;
        pill.TitleText.Text = e.NewValue as string ?? "";
    }

    private void PillButton_Click(object sender, RoutedEventArgs e)
    {
        if (Item != null)
            NavigateItem(NavigationHelpers.IsCtrlPressed());
    }

    private void PillButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed && Item != null)
            NavigateItem(openInNewTab: true);
    }

    private void NavigateItem(bool openInNewTab)
    {
        if (Item == null || string.IsNullOrEmpty(Item.Uri)) return;

        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = Item.Uri,
            Title = Item.Title,
            Subtitle = Item.Subtitle,
            ImageUrl = Item.ImageUrl
        };

        var parts = Item.Uri.Split(':');
        if (parts.Length < 3) return;
        var type = parts[1];
        var title = Item.Title ?? type;

        switch (type)
        {
            case "artist":
                NavigationHelpers.OpenArtist(param, title, openInNewTab);
                break;
            case "album":
                NavigationHelpers.OpenAlbum(param, title, openInNewTab);
                break;
            case "playlist":
                NavigationHelpers.OpenPlaylist(param, title, openInNewTab);
                break;
        }
    }

    private void PillButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Item == null) return;

        var menu = new MenuFlyout();
        var openNewTab = new MenuFlyoutItem
        {
            Text = "Open in new tab",
            Icon = new SymbolIcon(Symbol.OpenWith)
        };
        openNewTab.Click += (_, _) => HomeViewModel.NavigateToItem(Item, openInNewTab: true);
        menu.Items.Add(openNewTab);
        menu.ShowAt(PillButton, e.GetPosition(PillButton));
    }
}
