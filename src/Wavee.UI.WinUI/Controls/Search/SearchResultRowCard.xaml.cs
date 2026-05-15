using System;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
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
    private static readonly InputCursor HandCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

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

    private IPlaybackStateService? _playbackStateService;
    private string? _trackId;
    private bool _isTrack;
    private bool _isHovered;
    private bool _isPressed;
    private bool _isThisTrackPlaying;
    private bool _isThisTrackPaused;
    private bool _isBuffering;
    private bool _subscribedToPlayback;

    // Album-metadata viewport prefetch (Pattern A in AlbumPrefetcher). Single-
    // shot per (realization × item) — reset on Unloaded AND on ApplyItem (so a
    // recycled row showing a different album fires once for the new URI).
    private bool _albumPrefetchKicked;
    private bool _playlistPrefetchKicked;
    private const double AlbumPrefetchTriggerDistance = 500;
    private const string AlbumUriPrefix = "spotify:album:";
    private const string PlaylistUriPrefix = "spotify:playlist:";

    public event TypedEventHandler<SearchResultRowCard, RightTappedRoutedEventArgs>? CardRightTapped;

    public SearchResultRowCard()
    {
        InitializeComponent();
        ProtectedCursor = HandCursor;
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
        Tapped += OnTapped;
        RightTapped += OnRightTapped;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        var item = Item;
        if (item is null) return;
        var uri = item.Uri;
        if (string.IsNullOrEmpty(uri)) return;
        if (args.BringIntoViewDistanceX > AlbumPrefetchTriggerDistance
            || args.BringIntoViewDistanceY > AlbumPrefetchTriggerDistance) return;

        if (!_albumPrefetchKicked
            && item.Type == SearchResultType.Album
            && uri.StartsWith(AlbumUriPrefix, StringComparison.Ordinal))
        {
            _albumPrefetchKicked = true;
            Ioc.Default.GetService<IAlbumPrefetcher>()?.EnqueueAlbumPrefetch(uri);
        }
        else if (!_playlistPrefetchKicked
            && item.Type == SearchResultType.Playlist
            && uri.StartsWith(PlaylistUriPrefix, StringComparison.Ordinal))
        {
            _playlistPrefetchKicked = true;
            Ioc.Default.GetService<IPlaylistMetadataPrefetcher>()?.EnqueuePlaylistPrefetch(uri);
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        // Refresh code-behind-set brushes so they pick up the new theme.
        ApplyTitleAccent();
        ApplyInteractionState();
    }

    private void ApplyInteractionState()
    {
        var backgroundKey = _isPressed
            ? "CardBackgroundFillColorTertiaryBrush"
            : _isHovered
                ? "CardBackgroundFillColorSecondaryBrush"
                : "CardBackgroundFillColorDefaultBrush";

        RootBorder.Background = (Brush)Application.Current.Resources[backgroundKey];
        RootBorder.BorderBrush = (Brush)Application.Current.Resources[_isHovered || _isPressed
            ? "ControlStrokeColorDefaultBrush"
            : "CardStrokeColorDefaultBrush"];
        ActionIcon.Foreground = (Brush)Application.Current.Resources[_isHovered || _isPressed
            ? "TextFillColorPrimaryBrush"
            : "TextFillColorSecondaryBrush"];
        RootBorder.Opacity = _isPressed ? 0.92 : 1.0;
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SearchResultRowCard)d).ApplyItem(e.NewValue as SearchResultItem);

    private void ApplyItem(SearchResultItem? item)
    {
        // Container recycle: re-arm the prefetch guards so a row reused for a
        // different album / playlist fires once for the new URI.
        _albumPrefetchKicked = false;
        _playlistPrefetchKicked = false;

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

        var source = new BitmapImage(new Uri(httpsUrl))
        {
            DecodePixelWidth = 64,
            DecodePixelType = DecodePixelType.Logical,
        };

        if (isArtist)
        {
            ArtistAvatar.ProfilePicture = source;
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

        // Drop the native decoded surface so the WinUI compositor releases it.
        if (ThumbnailImage != null) ThumbnailImage.Source = null;

        // Re-arm album / playlist prefetch on re-realization.
        _albumPrefetchKicked = false;
        _playlistPrefetchKicked = false;
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
        ApplyInteractionState();
        UpdateOverlayState();
        AnimationBuilder.Create()
            .Scale(to: new Vector2(1.004f, 1.004f), duration: TimeSpan.FromMilliseconds(120))
            .Start(RootBorder);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;
        _isPressed = false;
        ApplyInteractionState();
        UpdateOverlayState();
        AnimationBuilder.Create()
            .Scale(to: Vector2.One, duration: TimeSpan.FromMilliseconds(120))
            .Start(RootBorder);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isPressed = true;
        ApplyInteractionState();
        AnimationBuilder.Create()
            .Scale(to: new Vector2(0.996f, 0.996f), duration: TimeSpan.FromMilliseconds(80))
            .Start(RootBorder);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPressed)
            return;

        _isPressed = false;
        ApplyInteractionState();
        AnimationBuilder.Create()
            .Scale(to: _isHovered ? new Vector2(1.004f, 1.004f) : Vector2.One, duration: TimeSpan.FromMilliseconds(100))
            .Start(RootBorder);
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

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        CardRightTapped?.Invoke(this, e);
        e.Handled = true;
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
