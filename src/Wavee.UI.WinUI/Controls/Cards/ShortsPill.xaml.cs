using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class ShortsPill : UserControl
{
    private static ImageCacheService? _imageCache;
    private bool _isPointerOver;
    private bool _isPlaybackPending;
    private int _playbackPendingVersion;
    private string? _currentImageUrl;

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
        ResetInteractionState();
        StopPendingBeam();
        WeakReferenceMessenger.Default.UnregisterAll(this);

        // Drop the native decoded surface so the WinUI compositor releases it.
        if (PillImage != null) PillImage.Source = null;
    }

    private void OnNowPlayingChanged(object recipient, NowPlayingChangedMessage msg)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var (contextUri, albumUri, playing) = msg.Value;
            ApplyPlaybackSnapshot(contextUri, albumUri, playing);
        });
    }

    private void SyncInitialPlaybackState()
    {
        var ps = Ioc.Default.GetService<IPlaybackStateService>();
        if (ps == null) return;
        ApplyPlaybackSnapshot(ps.CurrentContext?.ContextUri, ps.CurrentAlbumId, ps.IsPlaying);
    }

    private void ApplyPlaybackSnapshot(string? contextUri, string? albumUri, bool playing)
    {
        var uri = Item?.Uri;
        // Pill represents an item — match on its own URI against either the
        // playback context or the currently-playing track's album URI.
        var isMatch = !string.IsNullOrEmpty(uri)
            && ((!string.IsNullOrEmpty(contextUri)
                 && string.Equals(uri, contextUri, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(albumUri)
                    && string.Equals(uri, albumUri, StringComparison.OrdinalIgnoreCase)));
        if (isMatch && playing)
            _isPointerOver = false;

        IsPlaying = isMatch && playing;
        IsContextPaused = isMatch && !playing;
        if (_isPlaybackPending && (!isMatch || playing))
            SetPlaybackPending(false);
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
        pill.ResetPlaybackVisualStateForNewItem();
        if (string.IsNullOrEmpty(pill.ImageUrl))
            pill.UpdateImage();
        pill.SyncInitialPlaybackState();
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (ShortsPill)d;
        pill.UpdatePlayingState();
    }

    private static void OnIsContextPausedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (ShortsPill)d;
        if ((bool)e.NewValue && pill.PlayButton == null)
            pill.FindName("PlayButton");
        pill.UpdatePlayingState();
    }

    private void UpdatePlayingState()
    {
        if (TitleText == null) return;

        var isActiveContext = IsPlaying || IsContextPaused;
        var isPending = _isPlaybackPending;

        var showPlayingIndicator = (IsPlaying || IsContextPaused) && !isPending;
        var showPlayButton = _isPointerOver || IsContextPaused || isPending;

        if (showPlayingIndicator && PlayingIndicator == null)
            FindName("PlayingIndicator");
        if (showPlayButton && PlayButton == null)
            FindName("PlayButton");

        if (PlayingIndicator != null)
        {
            PlayingIndicator.Visibility = showPlayingIndicator
                ? Visibility.Visible
                : Visibility.Collapsed;
            PlayingIndicator.IsActive = showPlayingIndicator && IsPlaying;
        }

        if (PlayButton != null && PlayButtonContent != null)
        {
            PlayButtonContent.IsPlaying = IsPlaying;
            PlayButtonContent.IsPending = isPending;
            PlayButton.Visibility = showPlayButton ? Visibility.Visible : Visibility.Collapsed;
            if (PlayButton.Visibility == Visibility.Visible)
                PlayButton.Opacity = 1;
        }

        if (isActiveContext)
            TitleText.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        else
            TitleText.ClearValue(TextBlock.ForegroundProperty);
    }

    // ── Hover play button ──

    private void PillButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdatePlayingState();
    }

    private void PillButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        UpdatePlayingState();
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback == null) return;
        var playbackState = Ioc.Default.GetService<IPlaybackStateService>();

        try
        {
            if (IsPlaying)
            {
                await Task.Run(async () => await playback.PauseAsync());
            }
            else if (IsContextPaused)
            {
                SetPlaybackPending(true);
                playbackState?.NotifyBuffering(null);
                var result = await Task.Run(async () => await playback.ResumeAsync());
                if (!result.IsSuccess)
                {
                    SetPlaybackPending(false);
                    playbackState?.ClearBuffering();
                }
            }
            else if (Item != null && !string.IsNullOrEmpty(Item.Uri))
            {
                SetPlaybackPending(true);
                playbackState?.NotifyBuffering(null);
                var uri = Item.Uri;
                var result = await Task.Run(async () => await playback.PlayContextAsync(uri));
                if (!result.IsSuccess)
                {
                    SetPlaybackPending(false);
                    playbackState?.ClearBuffering();
                }
            }
        }
        catch
        {
            SetPlaybackPending(false);
            playbackState?.ClearBuffering();
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
            if (string.IsNullOrEmpty(httpsUrl))
            {
                _currentImageUrl = null;
                PillImage.Source = null;
                PlaceholderIcon.Visibility = Visibility.Visible;
                return;
            }

            if (string.Equals(_currentImageUrl, httpsUrl, StringComparison.Ordinal)
                && PillImage.Source != null)
            {
                PlaceholderIcon.Visibility = Visibility.Collapsed;
                ImageContainer.Background = null;
                return;
            }

            _imageCache ??= CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ImageCacheService>();
            _currentImageUrl = httpsUrl;
            PillImage.Source = _imageCache?.GetOrCreate(httpsUrl, 100);
            PlaceholderIcon.Visibility = Visibility.Collapsed;
            ImageContainer.Background = null;
        }
        else
        {
            _currentImageUrl = null;
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

        ResetInteractionState();
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
        {
            ResetInteractionState();
            NavigateItem(openInNewTab: true);
        }
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

        var items = Controls.ContextMenu.Builders.CardContextMenuBuilder.Build(new Controls.ContextMenu.Builders.CardMenuContext
        {
            Uri = Item.Uri ?? string.Empty,
            Title = Item.Title ?? string.Empty,
            Subtitle = Item.Subtitle,
            ImageUrl = Item.ImageUrl,
            OpenAction = openInNewTab =>
            {
                ResetInteractionState();
                HomeViewModel.NavigateToItem(Item, openInNewTab);
            }
        });
        Controls.ContextMenu.ContextMenuHost.Show(PillButton, items, e.GetPosition(PillButton));
    }

    private void SetPlaybackPending(bool pending)
    {
        if (_isPlaybackPending == pending) return;

        _isPlaybackPending = pending;
        _playbackPendingVersion++;
        if (pending)
        {
            StartPendingBeam();
            _ = ClearPlaybackPendingAfterTimeoutAsync(_playbackPendingVersion);
        }
        else
        {
            StopPendingBeam();
        }

        UpdatePlayingState();
    }

    private void ResetPlaybackVisualStateForNewItem()
    {
        _isPointerOver = false;
        _isPlaybackPending = false;
        _playbackPendingVersion++;
        StopPendingBeam();
        if (PlayButton != null)
            PlayButton.Visibility = Visibility.Collapsed;
        IsPlaying = false;
        IsContextPaused = false;
    }

    private void ResetInteractionState()
    {
        _isPointerOver = false;
        if (!_isPlaybackPending)
            StopPendingBeam();
        UpdatePlayingState();
    }

    private void StartPendingBeam()
    {
        if (PendingBeam == null)
            FindName("PendingBeam");
        PendingBeam?.Start();
    }

    private void StopPendingBeam()
    {
        PendingBeam?.Stop();
    }

    private async Task ClearPlaybackPendingAfterTimeoutAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isPlaybackPending && _playbackPendingVersion == version)
            {
                SetPlaybackPending(false);
                Ioc.Default.GetService<IPlaybackStateService>()?.ClearBuffering();
            }
        });
    }
}
