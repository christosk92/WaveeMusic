using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Wavee.UI.WinUI.Data.Messages;
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
            new PropertyMetadata(null, OnItemChanged));

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(ShortsPill),
            new PropertyMetadata(false, OnIsPlayingChanged));

    public static readonly DependencyProperty IsContextPausedProperty =
        DependencyProperty.Register(nameof(IsContextPaused), typeof(bool), typeof(ShortsPill),
            new PropertyMetadata(false, OnIsContextPausedChanged));

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

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public bool IsContextPaused
    {
        get => (bool)GetValue(IsContextPausedProperty);
        set => SetValue(IsContextPausedProperty, value);
    }

    public ShortsPill()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!WeakReferenceMessenger.Default.IsRegistered<NowPlayingChangedMessage>(this))
            WeakReferenceMessenger.Default.Register<NowPlayingChangedMessage>(this, OnNowPlayingChanged);
        SyncInitialPlaybackState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private void OnNowPlayingChanged(object recipient, NowPlayingChangedMessage msg)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var (contextUri, playing) = msg.Value;
            var uri = Item?.Uri;
            var isMatch = !string.IsNullOrEmpty(contextUri)
                && !string.IsNullOrEmpty(uri)
                && string.Equals(uri, contextUri, StringComparison.OrdinalIgnoreCase);
            IsPlaying = isMatch && playing;
            IsContextPaused = isMatch && !playing;
        });
    }

    private void SyncInitialPlaybackState()
    {
        var ps = Ioc.Default.GetService<Data.Contracts.IPlaybackStateService>();
        if (ps == null) return;
        var contextUri = ps.CurrentContext?.ContextUri;
        var playing = ps.IsPlaying;
        var uri = Item?.Uri;
        var isMatch = !string.IsNullOrEmpty(contextUri)
            && !string.IsNullOrEmpty(uri)
            && string.Equals(uri, contextUri, StringComparison.OrdinalIgnoreCase);
        IsPlaying = isMatch && playing;
        IsContextPaused = isMatch && !playing;
    }

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ShortsPill)d).UpdateImage();
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Item is set after ImageUrl in the template. Re-apply image logic
        // so the Liked Songs heart icon can detect the :collection URI.
        var pill = (ShortsPill)d;
        if (string.IsNullOrEmpty(pill.ImageUrl))
            pill.UpdateImage();
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (ShortsPill)d;
        pill.UpdatePlayingState();
    }

    private static void OnIsContextPausedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (ShortsPill)d;
        pill.UpdatePlayingState();
    }

    private void UpdatePlayingState()
    {
        if (PlayingIndicator == null || TitleText == null) return;

        var isActiveContext = IsPlaying || IsContextPaused;

        PlayingIndicator.Visibility = IsPlaying ? Visibility.Visible : Visibility.Collapsed;

        // When paused, show permanent play (resume) button
        if (IsContextPaused && PlayButton != null)
        {
            PlayButtonIcon.Glyph = "\uE768"; // Play
            PlayButton.Visibility = Visibility.Visible;
            PlayButton.Opacity = 1;
        }
        else if (!IsPlaying && PlayButton != null)
        {
            PlayButton.Visibility = Visibility.Collapsed;
        }

        if (isActiveContext)
            TitleText.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        else
            TitleText.ClearValue(TextBlock.ForegroundProperty);
    }

    // ── Hover play button ──

    private void PillButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (PlayButton == null) return;

        PlayButtonIcon.Glyph = IsPlaying ? "\uE769" : "\uE768"; // Pause or Play
        PlayButton.Visibility = Visibility.Visible;
        PlayButton.Opacity = 1;

        // Hide playing indicator while hovering
        if (IsPlaying && PlayingIndicator != null)
            PlayingIndicator.Visibility = Visibility.Collapsed;
    }

    private void PillButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (PlayButton == null) return;

        // Hide play button unless context is paused (permanent resume button)
        if (!IsContextPaused)
            PlayButton.Visibility = Visibility.Collapsed;

        // Restore playing indicator
        if (IsPlaying && PlayingIndicator != null)
            PlayingIndicator.Visibility = Visibility.Visible;
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        var playback = Ioc.Default.GetService<Data.Contracts.IPlaybackService>();
        if (playback == null) return;

        try
        {
            if (IsPlaying)
                await playback.PauseAsync();
            else if (IsContextPaused)
                await playback.ResumeAsync();
            else if (Item != null && !string.IsNullOrEmpty(Item.Uri))
                await playback.PlayContextAsync(Item.Uri);
        }
        catch
        {
            // Playback errors surface via IPlaybackService.Errors observable
        }
    }

    private void UpdateImage()
    {
        // Guard: template may not be applied yet
        if (PillImage == null || PlaceholderIcon == null) return;

        if (!string.IsNullOrEmpty(ImageUrl))
        {
            var httpsUrl = SpotifyImageHelper.ToHttpsUrl(ImageUrl);
            var cache = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ImageCacheService>();
            PillImage.Source = cache?.GetOrCreate(httpsUrl, 100);
            PlaceholderIcon.Visibility = Visibility.Collapsed;
            ImageContainer.Background = null;
        }
        else
        {
            PillImage.Source = null;

            // Liked Songs: heart icon on purple gradient
            var isCollection = Item?.Uri?.Contains(":collection", StringComparison.OrdinalIgnoreCase) == true;
            if (isCollection)
            {
                PlaceholderIcon.Glyph = "\uEB51"; // Heart
                PlaceholderIcon.Foreground = new SolidColorBrush(Colors.White);
                ImageContainer.Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Windows.UI.Color.FromArgb(255, 69, 0, 214), Offset = 0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(255, 142, 112, 219), Offset = 1 }
                    }
                };
            }
            else
            {
                PlaceholderIcon.Glyph = "\uE8D6"; // Music note
                PlaceholderIcon.Foreground = null;
                ImageContainer.Background = null;
            }

            PlaceholderIcon.Visibility = Visibility.Visible;
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
        if (IsPlayButtonSource(e.OriginalSource))
            return;

        if (Item != null)
            NavigateItem(NavigationHelpers.IsCtrlPressed());
    }

    private bool IsPlayButtonSource(object? source)
    {
        var current = source as DependencyObject;
        while (current != null)
        {
            if (ReferenceEquals(current, PlayButton))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
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
            case "user" when Item.Uri.Contains(":collection", StringComparison.OrdinalIgnoreCase):
                NavigationHelpers.OpenLikedSongs(openInNewTab);
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
