using System;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.PlayerBar;

public sealed partial class PlayerBar : UserControl
{
    private const double WideBreakpoint = 1500;
    private const double CompactBreakpoint = 860;
    private const double NarrowBreakpoint = 520;

    public PlayerBarViewModel ViewModel { get; }

    private readonly Data.Contracts.ITrackLikeService? _likeService;
    private readonly IPlaybackStateService? _playbackStateService;
    private string? _currentLayoutState;

    private readonly ILogger<PlayerBar>? _logger;

    // Hand cursor applied to both album-art hosts (wide + narrow) so the image
    // advertises that it's clickable. Set once on Loaded via reflection-based
    // ChangeCursor — ProtectedCursor isn't a public DP in WinUI 3.
    private static readonly Microsoft.UI.Input.InputCursor HandCursor =
        Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);

    public PlayerBar()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();
        _likeService = Ioc.Default.GetService<Data.Contracts.ITrackLikeService>();
        _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
        _logger = Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger<PlayerBar>();
        InitializeComponent();

        _logger?.LogDebug("[PlayerBar] Constructed — track={Track}, playing={Playing}", ViewModel.TrackTitle ?? "<none>", ViewModel.IsPlaying);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SizeChanged += OnPlayerBarSizeChanged;
        Loaded += OnPlayerBarLoaded;

        PlayerHeartButton.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(OnPlayerHeartClicked);

        // Subscribe to save state changes for reactive heart updates
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
        Unloaded += (_, _) =>
        {
            if (_likeService != null) _likeService.SaveStateChanged -= OnSaveStateChanged;
        };

        // Apply initial color if available
        ApplyTintColor(ViewModel.AlbumArtColor);
        UpdatePlayerHeartState();
    }

    private void OnPlayerBarLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutState(PlayerBarLayoutRoot.ActualWidth > 0 ? PlayerBarLayoutRoot.ActualWidth : ActualWidth);

        AlbumArtHost?.ChangeCursor(HandCursor);
        NarrowAlbumArtHost?.ChangeCursor(HandCursor);

        // The Slider's inner Thumb captures pointer events and marks them handled,
        // so the Slider's own PointerPressed/Released routed events never bubble up
        // to the XAML attribute handlers when the user grabs the thumb (the most
        // common drag path). Without handledEventsToo, IsSeeking stays false during
        // the drag and the interpolation timer keeps overwriting Position.
        var pressed = new PointerEventHandler(ProgressSlider_PointerPressed);
        var released = new PointerEventHandler(ProgressSlider_PointerReleased);
        var captureLost = new PointerEventHandler(ProgressSlider_PointerCaptureLost);
        foreach (var slider in new[] { WideProgressSlider, CompactProgressSlider })
        {
            if (slider == null) continue;
            slider.AddHandler(UIElement.PointerPressedEvent, pressed, handledEventsToo: true);
            slider.AddHandler(UIElement.PointerReleasedEvent, released, handledEventsToo: true);
            slider.AddHandler(UIElement.PointerCaptureLostEvent, captureLost, handledEventsToo: true);
        }
    }

    private void OnPlayerBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutState(PlayerBarLayoutRoot.ActualWidth > 0 ? PlayerBarLayoutRoot.ActualWidth : e.NewSize.Width);
    }

    private void UpdateLayoutState(double width)
    {
        var nextState =
            width >= WideBreakpoint ? "Wide" :
            width >= CompactBreakpoint ? "Compact" :
            width >= NarrowBreakpoint ? "Narrow" :
            "VeryNarrow";

        if (_currentLayoutState == nextState)
        {
            return;
        }

        _logger?.LogDebug("[PlayerBar] Layout state: {From} → {To} (width={Width:F0})", _currentLayoutState ?? "<init>", nextState, width);
        _currentLayoutState = nextState;
        _ = VisualStateManager.GoToState(this, nextState, true);
        ApplyLayoutState(nextState);
    }

    private void ApplyLayoutState(string state)
    {
        var showWide = state is "Wide" or "Compact" or "Narrow";
        var showInlinePanels = state is "Wide" or "Compact";
        var showOverflow = state != "Wide";
        var showNarrowAlbumArt = !ViewModel.IsAlbumArtExpanded;
        var showNarrowRemote = state != "VeryNarrow" && ViewModel.IsPlayingRemotely;
        var showInlineModes = state is "Wide" or "Compact" or "Narrow";
        var showInlineVolume = state == "Wide";
        var showCollapsedVolume = state is "Compact" or "Narrow";
        var showNarrowModes = false;
        var showNarrowVolume = true;

        WideLayoutRoot.Visibility = showWide ? Visibility.Visible : Visibility.Collapsed;
        NarrowLayoutRoot.Visibility = showWide ? Visibility.Collapsed : Visibility.Visible;

        InlinePanelGroup.Visibility = showInlinePanels ? Visibility.Visible : Visibility.Collapsed;
        InlineModeGroup.Visibility = showInlineModes ? Visibility.Visible : Visibility.Collapsed;
        InlineVolumeGroup.Visibility = showInlineVolume ? Visibility.Visible : Visibility.Collapsed;
        CollapsedVolumeButton.Visibility = showCollapsedVolume ? Visibility.Visible : Visibility.Collapsed;
        OverflowButton.Visibility = showOverflow && showWide ? Visibility.Visible : Visibility.Collapsed;

        WideLayoutRoot.Height = showWide ? 56 : double.NaN;
        PlayerBarLayoutRoot.MinHeight = state switch
        {
            "Wide" or "Compact" => 56,
            "Narrow" => 84,
            _ => 76
        };

        NarrowAlbumArtHost.Visibility = showNarrowAlbumArt ? Visibility.Visible : Visibility.Collapsed;
        NarrowRemoteDeviceHost.Visibility = showNarrowRemote ? Visibility.Visible : Visibility.Collapsed;
        NarrowInlineModeGroup.Visibility = showNarrowModes ? Visibility.Visible : Visibility.Collapsed;
        NarrowVolumeButton.Visibility = showNarrowVolume ? Visibility.Visible : Visibility.Collapsed;
        NarrowTrackMetadataHost.Margin = new Thickness(8, 0, 0, 0);
    }

    private void OnSaveStateChanged()
    {
        DispatcherQueue?.TryEnqueue(UpdatePlayerHeartState);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.AlbumArtColor))
        {
            ApplyTintColor(ViewModel.AlbumArtColor);
        }
        else if (e.PropertyName is nameof(PlayerBarViewModel.HasTrack) or nameof(PlayerBarViewModel.TrackTitle))
        {
            UpdatePlayerHeartState();
        }

        if (e.PropertyName is nameof(PlayerBarViewModel.IsAlbumArtExpanded)
            or nameof(PlayerBarViewModel.IsPlayingRemotely))
        {
            ApplyLayoutState(_currentLayoutState ?? "Wide");
        }
    }

    private string? GetCurrentTrackId()
    {
        return _playbackStateService?.CurrentTrackId;
    }

    private void UpdatePlayerHeartState()
    {
        var trackId = GetCurrentTrackId();
        PlayerHeartButton.IsLiked = !string.IsNullOrEmpty(trackId)
            && _likeService?.IsSaved(Data.Contracts.SavedItemType.Track, trackId) == true;
    }

    private void OnPlayerHeartClicked()
    {
        var trackId = GetCurrentTrackId();
        if (string.IsNullOrEmpty(trackId) || _likeService == null) return;

        var uri = $"spotify:track:{trackId}";
        var isLiked = PlayerHeartButton.IsLiked;
        _logger?.LogInformation("[PlayerBar] Heart clicked: trackId={TrackId}, wasLiked={WasLiked} → {NewLiked}", trackId, isLiked, !isLiked);
        _likeService.ToggleSave(Data.Contracts.SavedItemType.Track, uri, isLiked);
        PlayerHeartButton.IsLiked = !isLiked;
    }

    private void ApplyTintColor(string? hexColor)
    {
        if (string.IsNullOrEmpty(hexColor))
        {
            // Reset album art host to default theme background
            var defaultBrush = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            if (AlbumArtHost != null) AlbumArtHost.Background = defaultBrush;
            if (NarrowAlbumArtHost != null) NarrowAlbumArtHost.Background = defaultBrush;
            return;
        }

        if (PlayerBarTintBrush == null) return;

        try
        {
            var hex = hexColor.TrimStart('#');
            if (hex.Length == 6)
            {
                var r = Convert.ToByte(hex[..2], 16);
                var g = Convert.ToByte(hex[2..4], 16);
                var b = Convert.ToByte(hex[4..6], 16);
                var color = Windows.UI.Color.FromArgb(255, r, g, b);
                PlayerBarTintBrush.TintColor = color;

                // Color the album art thumbnail placeholder with the album's dominant color
                // at reduced opacity so it looks like a tinted background (not a solid block).
                var placeholderColor = Windows.UI.Color.FromArgb(100, r, g, b);
                var placeholderBrush = new SolidColorBrush(placeholderColor);
                if (AlbumArtHost != null) AlbumArtHost.Background = placeholderBrush;
                if (NarrowAlbumArtHost != null) NarrowAlbumArtHost.Background = placeholderBrush;
            }
        }
        catch
        {
            // Keep current tint on parse failure
        }
    }

    private void AlbumArt_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // Tapping the image used to toggle the big-art expander; now it opens the
        // active playback context (Playlist / Album / Artist / Liked Songs). The
        // expander is still reachable via ShellPage's dedicated trigger.
        NavigateToActiveContext();
        e.Handled = true;
    }

    private void TrackTitle_Click(object sender, RoutedEventArgs e) => NavigateToAlbum();
    private void TrackTitle_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => NavigateToAlbum();

    private void EndOfContextDismiss_Click(object sender, RoutedEventArgs e)
    {
        _playbackStateService?.DismissEndOfContext();
    }

    private void NavigateToAlbum()
    {
        var albumId = ViewModel.CurrentAlbumId;
        if (string.IsNullOrEmpty(albumId)) return;
        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = albumId,
            Title = ViewModel.TrackTitle ?? "Album",
            ImageUrl = ViewModel.AlbumArt
        };
        NavigationHelpers.OpenAlbum(param, param.Title);
    }

    private void NavigateToActiveContext()
    {
        var ctx = _playbackStateService?.CurrentContext;
        if (ctx is null || string.IsNullOrEmpty(ctx.ContextUri))
        {
            // No known context (e.g. queue-only playback). Fall back to the track's
            // album so the click does *something* reasonable.
            NavigateToAlbum();
            return;
        }

        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = ctx.ContextUri,
            Title = ctx.Name ?? ViewModel.TrackTitle ?? string.Empty,
            ImageUrl = ctx.ImageUrl ?? ViewModel.AlbumArt,
        };

        switch (ctx.Type)
        {
            case PlaybackContextType.Playlist:
                NavigationHelpers.OpenPlaylist(param, param.Title);
                break;
            case PlaybackContextType.Album:
                NavigationHelpers.OpenAlbum(param, param.Title);
                break;
            case PlaybackContextType.Artist:
                NavigationHelpers.OpenArtist(param, param.Title);
                break;
            case PlaybackContextType.LikedSongs:
                NavigationHelpers.OpenLikedSongs();
                break;
            default:
                // Queue / Search / Unknown — no canonical destination; fall back
                // to the album of the currently-playing track.
                NavigateToAlbum();
                break;
        }
    }

    private void OverflowFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout)
        {
            return;
        }

        var currentTrackId = GetCurrentTrackId();
        var likeItem = flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault(static item => Equals(item.Tag, "like"));
        if (likeItem != null)
        {
            likeItem.Text = PlayerHeartButton.IsLiked ? "Unlike track" : "Like track";
            likeItem.IsEnabled = !string.IsNullOrEmpty(currentTrackId);
        }

    }

    private void LikeOverflowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OnPlayerHeartClicked();
    }

    private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _logger?.LogDebug("[PlayerBar] Seek slider pressed: pos={Pos}ms", (long)ViewModel.Position);
        ViewModel.StartSeeking();
    }

    private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _logger?.LogInformation("[PlayerBar] Seek slider released: committing pos={Pos}ms", (long)ViewModel.Position);
        ViewModel.EndSeeking();
    }

    private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsSeeking)
        {
            _logger?.LogDebug("[PlayerBar] Seek slider capture lost while seeking: committing pos={Pos}ms", (long)ViewModel.Position);
            ViewModel.EndSeeking();
        }
    }
}
