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
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
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

        // Drive the position-interpolation timer's visibility gate (Phase 6.2).
        // While the bar is detached from the visual tree (right panel covering,
        // tab in transition, etc.) the slider isn't being rendered so spending
        // CPU updating its bound Position is wasted.
        Loaded += (_, _) => ViewModel.SetSurfaceVisible("bar", true);
        Unloaded += (_, _) => ViewModel.SetSurfaceVisible("bar", false);

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

        // Re-tint on theme switch — otherwise the bar stays stuck with
        // whichever theme's blend was active when the track loaded
        // (light-mode pastel under dark mode, or vice-versa).
        ActualThemeChanged += (_, _) => ApplyTintColor(ViewModel.AlbumArtColor);
    }

    private void OnPlayerBarLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutState(PlayerBarLayoutRoot.ActualWidth > 0 ? PlayerBarLayoutRoot.ActualWidth : ActualWidth);

        AlbumArtHost?.ChangeCursor(HandCursor);
        NarrowAlbumArtHost?.ChangeCursor(HandCursor);
        // Progress bar drag handling now lives on CompositionProgressBar via its
        // SeekStarted / SeekCommitted events — wired in XAML, dispatched below.
    }

    // CompositionProgressBar drag started — pause the GPU animation source on
    // the VM so position/anchor updates from the audio host don't overwrite
    // what the user is dragging.
    private void ProgressBar_SeekStarted(object sender, System.EventArgs e)
        => ViewModel.StartSeeking();

    // CompositionProgressBar drag released — commit the new position to the
    // playback service via the VM, which also re-anchors the bar so the
    // animation restarts from the new position immediately (no wait for the
    // AudioHost echo).
    private void ProgressBar_SeekCommitted(object sender, double positionMs)
        => ViewModel.CommitSeekFromBar(positionMs);

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
            if (hex.Length != 6) return;
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);

            // Light mode: pre-blend the album dominant colour toward white so
            // the player bar reads as a gentle pastel pad instead of a fully
            // saturated band that fights the page surface and tanks text
            // contrast (the title / subtitle text is dark, so a saturated
            // mid-tone tint kills legibility). Dark mode keeps the saturated
            // tint — it pops against a dark background.
            byte tr = r, tg = g, tb = b;
            if (ActualTheme != ElementTheme.Dark)
            {
                const float blend = 0.62f; // 62% white, 38% album colour
                tr = (byte)(r * (1 - blend) + 255 * blend);
                tg = (byte)(g * (1 - blend) + 255 * blend);
                tb = (byte)(b * (1 - blend) + 255 * blend);
            }
            PlayerBarTintBrush.TintColor = Windows.UI.Color.FromArgb(255, tr, tg, tb);

            // Album art thumbnail placeholder uses the RAW dominant colour at
            // low alpha — a small, square tint chip looks fine even with the
            // fully saturated colour, and matches what's behind the actual
            // cover image once it loads.
            var placeholderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(100, r, g, b));
            if (AlbumArtHost != null) AlbumArtHost.Background = placeholderBrush;
            if (NarrowAlbumArtHost != null) NarrowAlbumArtHost.Background = placeholderBrush;
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

    private void NowPlaying_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (_playbackStateService is null) return;
        if (string.IsNullOrEmpty(_playbackStateService.CurrentTrackId)) return;

        var adapter = new NowPlayingTrackAdapter(_playbackStateService);
        var items = TrackContextMenuBuilder.Build(adapter);
        ContextMenuHost.Show(fe, items, e.GetPosition(fe));
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

    private void ShowInSidebar_Click(object sender, RoutedEventArgs e)
    {
        var shell = Ioc.Default.GetService<ViewModels.ShellViewModel>();
        if (shell == null) return;
        // Don't carry the expanded-album-art into sidebar mode — the widget
        // already has a big art surface, and the sidebar footer expansion
        // becomes orphaned (no bar tap to dismiss).
        ViewModel.IsAlbumArtExpanded = false;
        shell.PlayerLocation = Data.Enums.PlayerLocation.Sidebar;
        _logger?.LogInformation("[PlayerBar] Player moved to sidebar via overflow menu");
    }

    // Old Slider PointerPressed/Released/CaptureLost handlers were removed when
    // the WideProgressSlider / CompactProgressSlider Sliders were replaced by
    // CompositionProgressBar. Drag → seek now fires via the bar's typed events.
}
