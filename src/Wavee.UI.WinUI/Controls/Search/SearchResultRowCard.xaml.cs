using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Controls.Track.Behaviors;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Search;

public sealed partial class SearchResultRowCard : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(
            nameof(Item),
            typeof(SearchResultItem),
            typeof(SearchResultRowCard),
            new PropertyMetadata(null, OnItemChanged));

    public SearchResultItem? Item
    {
        get => (SearchResultItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private const double SquareCornerRadius = 18.0;
    private const double CircleCornerRadius = 26.0;

    private ImageCacheService? _imageCache;
    private IPlaybackStateService? _playbackStateService;
    private string? _trackId;
    private bool _isTrack;
    private bool _isHovered;
    private bool _isThisTrackPlaying;
    private bool _isThisTrackPaused;
    private bool _isBuffering;
    private bool _subscribedToPlayback;

    public SearchResultRowCard()
    {
        InitializeComponent();
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        Tapped += OnTapped;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        // Refresh code-behind-set brushes so they pick up the new theme.
        ApplyTitleAccent();
        ApplyHoverBackground();
    }

    private void ApplyHoverBackground()
    {
        var key = _isHovered
            ? "CardBackgroundFillColorSecondaryBrush"
            : "CardBackgroundFillColorDefaultBrush";
        RootBorder.Background = (Brush)Application.Current.Resources[key];
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SearchResultRowCard)d).ApplyItem(e.NewValue as SearchResultItem);

    private void ApplyItem(SearchResultItem? item)
    {
        if (item == null)
        {
            TitleText.Text = string.Empty;
            SubtitleText.Inlines.Clear();
            TypeTagText.Text = string.Empty;
            ThumbnailPlaceholderIcon.Glyph = string.Empty;
            ActionIcon.Glyph = string.Empty;
            ThumbnailImage.Source = null;
            ArtistAvatar.ProfilePicture = null;
            _trackId = null;
            _isTrack = false;
            ApplyThumbnailShape(isArtist: false);
            RefreshPlaybackState();
            UpdateOverlayState();
            return;
        }

        TitleText.Text = item.Name;
        SearchSubtitleBuilder.Build(SubtitleText, item);
        TypeTagText.Text = item.GetTypeTag();
        ThumbnailPlaceholderIcon.Glyph = item.GetPlaceholderGlyph();
        ActionIcon.Glyph = item.GetActionGlyph();

        var isArtist = item.Type == SearchResultType.Artist;
        _isTrack = item.Type == SearchResultType.Track;
        ApplyThumbnailShape(isArtist);
        ApplyThumbnail(item.ImageUrl, isArtist);

        _trackId = _isTrack ? ExtractId(item.Uri) : null;

        RefreshPlaybackState();
        UpdateOverlayState();
    }

    private void ApplyThumbnailShape(bool isArtist)
    {
        if (isArtist)
        {
            ThumbnailClip.Visibility = Visibility.Collapsed;
            ArtistAvatar.Visibility = Visibility.Visible;
        }
        else
        {
            ArtistAvatar.Visibility = Visibility.Collapsed;
            ThumbnailClip.Visibility = Visibility.Visible;
            ThumbnailClip.CornerRadius = new CornerRadius(SquareCornerRadius);
        }
    }

    private void ApplyThumbnail(string? imageUrl, bool isArtist)
    {
        ThumbnailImage.Source = null;
        ArtistAvatar.ProfilePicture = null;

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(httpsUrl))
        {
            ThumbnailImage.Visibility = Visibility.Collapsed;
            ThumbnailPlaceholderIcon.Visibility = Visibility.Visible;
            return;
        }

        _imageCache ??= Ioc.Default.GetService<ImageCacheService>();
        var source = _imageCache?.GetOrCreate(httpsUrl, 64);

        if (isArtist)
        {
            ArtistAvatar.ProfilePicture = source as BitmapImage;
            ThumbnailImage.Visibility = Visibility.Collapsed;
            ThumbnailPlaceholderIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            ThumbnailImage.Source = source;
            ThumbnailImage.Visibility = Visibility.Visible;
            ThumbnailPlaceholderIcon.Visibility = Visibility.Collapsed;
        }
    }

    private static string? ExtractId(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        var idx = uri.LastIndexOf(':');
        return idx >= 0 && idx < uri.Length - 1 ? uri[(idx + 1)..] : uri;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribedToPlayback)
        {
            TrackStateBehavior.EnsurePlaybackSubscription();
            // Weak-reference messenger replaces the prior static event subscription.
            WeakReferenceMessenger.Default.Register<SearchResultRowCard, TrackStateRefreshMessage>(
                this, static (r, _) => r.OnPlaybackStateChanged());
            _subscribedToPlayback = true;
        }

        RefreshPlaybackState();
        UpdateOverlayState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedToPlayback)
        {
            WeakReferenceMessenger.Default.Unregister<TrackStateRefreshMessage>(this);
            _subscribedToPlayback = false;
        }

        NowPlayingEqualizer.IsActive = false;
        StopPendingBeam();
    }

    private void OnPlaybackStateChanged()
    {
        // Same filter as TrackItem.OnPlaybackStateChanged — skip the dispatch
        // when this card's effective playback state can't have flipped.
        if (_trackId == null) return;

        var isThisTrack = _trackId == TrackStateBehavior.CurrentTrackId;
        var nowPlaying = isThisTrack && TrackStateBehavior.IsCurrentlyPlaying;
        var nowPaused = isThisTrack && !TrackStateBehavior.IsCurrentlyPlaying
                                    && TrackStateBehavior.CurrentTrackId != null;
        var nowBuffering = _trackId == TrackStateBehavior.BufferingTrackId
                           && TrackStateBehavior.IsCurrentlyBuffering;

        if (nowPlaying == _isThisTrackPlaying
            && nowPaused == _isThisTrackPaused
            && nowBuffering == _isBuffering)
            return;

        DispatcherQueue?.TryEnqueue(() =>
        {
            RefreshPlaybackState();
            UpdateOverlayState();
        });
    }

    private void RefreshPlaybackState()
    {
        if (_trackId == null)
        {
            _isThisTrackPlaying = false;
            _isThisTrackPaused = false;
            _isBuffering = false;
            ApplyTitleAccent();
            return;
        }

        var isThisTrack = _trackId == TrackStateBehavior.CurrentTrackId;
        _isThisTrackPlaying = isThisTrack && TrackStateBehavior.IsCurrentlyPlaying;
        _isThisTrackPaused = isThisTrack && !TrackStateBehavior.IsCurrentlyPlaying && TrackStateBehavior.CurrentTrackId != null;
        _isBuffering = _trackId == TrackStateBehavior.BufferingTrackId
                       && TrackStateBehavior.IsCurrentlyBuffering;

        ApplyTitleAccent();
    }

    private void ApplyTitleAccent()
    {
        var highlight = _isThisTrackPlaying || _isThisTrackPaused || _isBuffering;
        TitleText.Foreground = highlight
            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    private void UpdateOverlayState()
    {
        if (_isBuffering)
        {
            HoverPlayButton.Visibility = Visibility.Collapsed;
            HoverPlayButton.Opacity = 0;
            NowPlayingOverlay.Visibility = Visibility.Collapsed;
            NowPlayingEqualizer.IsActive = false;
            BufferingRing.IsActive = true;
            BufferingRing.Visibility = Visibility.Visible;
            StartPendingBeam();
            return;
        }

        StopPendingBeam();
        BufferingRing.IsActive = false;
        BufferingRing.Visibility = Visibility.Collapsed;

        if (_isHovered && _isTrack)
        {
            NowPlayingOverlay.Visibility = Visibility.Collapsed;
            NowPlayingEqualizer.IsActive = false;
            if (HoverPlayContent != null)
                HoverPlayContent.IsPlaying = _isThisTrackPlaying;

            if (HoverPlayButton.Visibility == Visibility.Collapsed)
            {
                HoverPlayButton.Opacity = 0;
                HoverPlayButton.Visibility = Visibility.Visible;
                HoverPlayButton.UpdateLayout();
                AnimationBuilder.Create()
                    .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(150))
                    .Start(HoverPlayButton);
            }
            return;
        }

        // Not hovered, not buffering
        if (HoverPlayButton.Visibility == Visibility.Visible)
        {
            HoverPlayButton.Visibility = Visibility.Collapsed;
            HoverPlayButton.Opacity = 0;
        }

        if (_isThisTrackPlaying)
        {
            NowPlayingOverlay.Visibility = Visibility.Visible;
            NowPlayingOverlay.Opacity = 1.0;
            NowPlayingEqualizer.IsActive = true;
        }
        else if (_isThisTrackPaused)
        {
            NowPlayingOverlay.Visibility = Visibility.Visible;
            NowPlayingOverlay.Opacity = 0.7;
            NowPlayingEqualizer.IsActive = false;
        }
        else
        {
            NowPlayingOverlay.Visibility = Visibility.Collapsed;
            NowPlayingEqualizer.IsActive = false;
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;
        ApplyHoverBackground();
        UpdateOverlayState();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;
        ApplyHoverBackground();
        UpdateOverlayState();
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        // Push an immediate buffering state for tracks so the loading visual appears
        // synchronously on click — same trick TrackItem uses (see TrackItem.xaml.cs:935-950).
        // Skip if this row is already the active/buffering track.
        if (!_isTrack || _trackId == null) return;
        if (_trackId == TrackStateBehavior.CurrentTrackId) return;
        if (_trackId == TrackStateBehavior.BufferingTrackId) return;

        _playbackStateService ??= Ioc.Default.GetService<IPlaybackStateService>();
        _playbackStateService?.NotifyBuffering(_trackId);
    }

    private void StartPendingBeam()
    {
        if (PlaybackPendingBeam == null)
            FindName(nameof(PlaybackPendingBeam));
        PlaybackPendingBeam?.Start();
    }

    private void StopPendingBeam()
    {
        PlaybackPendingBeam?.Stop();
    }
}
