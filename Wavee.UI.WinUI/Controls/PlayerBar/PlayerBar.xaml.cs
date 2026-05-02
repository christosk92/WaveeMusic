using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
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
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.Playback;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Services.Docking;
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
    private readonly IMusicVideoMetadataService? _musicVideoMetadata;
    private readonly IPanelDockingService? _dockingService;
    private string? _currentLayoutState;
    private int _heartStateVersion;
    private bool _eventsSubscribed;
    private bool _videoMiniPlayerPromptOpen;
    private bool _suppressVideoPopoutTeachingTipClosedHandling;
    private static bool s_videoPopoutTeachingTipDismissed;

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
        _musicVideoMetadata = Ioc.Default.GetService<IMusicVideoMetadataService>();
        _dockingService = Ioc.Default.GetService<IPanelDockingService>();
        _logger = Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger<PlayerBar>();
        InitializeComponent();

        _logger?.LogDebug("[PlayerBar] Constructed — track={Track}, playing={Playing}", ViewModel.TrackTitle ?? "<none>", ViewModel.IsPlaying);

        SizeChanged += OnPlayerBarSizeChanged;
        Loaded += OnPlayerBarLoaded;
        Unloaded += OnPlayerBarUnloaded;

        PlayerHeartButton.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(OnPlayerHeartClicked);

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
        SubscribeEvents();
        ViewModel.SetSurfaceVisible("bar", true);
        UpdateLayoutState(PlayerBarLayoutRoot.ActualWidth > 0 ? PlayerBarLayoutRoot.ActualWidth : ActualWidth);
        UpdateVideoPopoutTeachingTip();

        AlbumArtHost?.ChangeCursor(HandCursor);
        NarrowAlbumArtHost?.ChangeCursor(HandCursor);
        // Progress bar drag handling now lives on CompositionProgressBar via its
        // SeekStarted / SeekCommitted events — wired in XAML, dispatched below.
    }

    private void OnPlayerBarUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SetSurfaceVisible("bar", false);
        UnsubscribeEvents();
    }

    private void SubscribeEvents()
    {
        if (_eventsSubscribed)
            return;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
        if (_playbackStateService != null)
            _playbackStateService.PropertyChanged += OnPlaybackSaveTargetChanged;
        if (_dockingService != null)
            _dockingService.PropertyChanged += OnDockingPropertyChanged;
        WeakReferenceMessenger.Default.Register<VideoMiniPlayerPromptStateChangedMessage>(this, static (r, m) =>
        {
            if (r is PlayerBar playerBar)
                playerBar.DispatcherQueue?.TryEnqueue(() => playerBar.OnVideoMiniPlayerPromptStateChanged(m.Value));
        });

        _eventsSubscribed = true;
    }

    private void UnsubscribeEvents()
    {
        if (!_eventsSubscribed)
            return;

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_likeService != null)
            _likeService.SaveStateChanged -= OnSaveStateChanged;
        if (_playbackStateService != null)
            _playbackStateService.PropertyChanged -= OnPlaybackSaveTargetChanged;
        if (_dockingService != null)
            _dockingService.PropertyChanged -= OnDockingPropertyChanged;
        WeakReferenceMessenger.Default.Unregister<VideoMiniPlayerPromptStateChangedMessage>(this);

        _eventsSubscribed = false;
    }

    // CompositionProgressBar drag started: pause the GPU animation source on
    // the VM so audio-host position updates do not overwrite the user's drag.
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

    private void OnPlaybackSaveTargetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackId)
            or nameof(IPlaybackStateService.CurrentTrackIsVideo)
            or nameof(IPlaybackStateService.CurrentOriginalTrackId))
        {
            DispatcherQueue?.TryEnqueue(UpdatePlayerHeartState);
        }

        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackId)
            or nameof(IPlaybackStateService.CurrentTrackIsVideo))
        {
            DispatcherQueue?.TryEnqueue(UpdateVideoPopoutTeachingTip);
        }
    }

    private void OnDockingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPanelDockingService.IsPlayerDetached))
            DispatcherQueue?.TryEnqueue(UpdateVideoPopoutTeachingTip);
    }

    private void OnVideoMiniPlayerPromptStateChanged(bool isOpen)
    {
        _videoMiniPlayerPromptOpen = isOpen;
        if (isOpen)
            s_videoPopoutTeachingTipDismissed = true;
        UpdateVideoPopoutTeachingTip();
    }

    private void UpdateVideoPopoutTeachingTip()
    {
        if (FindName("VideoPopoutTeachingTip") is not TeachingTip tip)
            return;

        if (s_videoPopoutTeachingTipDismissed || _videoMiniPlayerPromptOpen)
        {
            CloseVideoPopoutTeachingTipProgrammatically(tip);
            return;
        }

        var show = _dockingService?.IsPlayerDetached == true
                   && _playbackStateService?.CurrentTrackIsVideo == true;
        if (show)
            tip.IsOpen = true;
        else
            CloseVideoPopoutTeachingTipProgrammatically(tip);
    }

    private void CloseVideoPopoutTeachingTipProgrammatically(TeachingTip tip)
    {
        if (!tip.IsOpen)
        {
            _suppressVideoPopoutTeachingTipClosedHandling = false;
            return;
        }

        _suppressVideoPopoutTeachingTipClosedHandling = true;
        tip.IsOpen = false;
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
        return PlaybackSaveTargetResolver.GetTrackId(_playbackStateService);
    }

    private void UpdatePlayerHeartState()
    {
        var version = ++_heartStateVersion;
        var uri = PlaybackSaveTargetResolver.GetTrackUri(_playbackStateService);
        if (!string.IsNullOrEmpty(uri))
        {
            PlayerHeartButton.IsLiked = _likeService?.IsSaved(Data.Contracts.SavedItemType.Track, uri) == true;
            return;
        }

        PlayerHeartButton.IsLiked = false;
        _ = UpdatePlayerHeartStateAsync(version);
    }

    private void OnPlayerHeartClicked()
        => _ = OnPlayerHeartClickedAsync();

    private async Task UpdatePlayerHeartStateAsync(int version)
    {
        var uri = await PlaybackSaveTargetResolver
            .ResolveTrackUriAsync(_playbackStateService, _musicVideoMetadata)
            .ConfigureAwait(true);

        if (version != _heartStateVersion)
            return;

        PlayerHeartButton.IsLiked = !string.IsNullOrEmpty(uri)
            && _likeService?.IsSaved(Data.Contracts.SavedItemType.Track, uri) == true;
    }

    private async Task OnPlayerHeartClickedAsync()
    {
        var uri = await PlaybackSaveTargetResolver
            .ResolveTrackUriAsync(_playbackStateService, _musicVideoMetadata)
            .ConfigureAwait(true);
        if (string.IsNullOrEmpty(uri) || _likeService == null) return;

        var isLiked = _likeService.IsSaved(Data.Contracts.SavedItemType.Track, uri);
        _logger?.LogInformation("[PlayerBar] Heart clicked: uri={Uri}, wasLiked={WasLiked} → {NewLiked}", uri, isLiked, !isLiked);
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
    private void TrackTitle_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        NavigateToAlbum();
        e.Handled = true;
    }

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
        if (albumId.StartsWith("spotify:show:", StringComparison.Ordinal))
        {
            NavigationHelpers.OpenShow(albumId, param.Title);
            return;
        }
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
            case PlaybackContextType.Show:
                NavigationHelpers.OpenShow(param.Uri, param.Title);
                break;
            case PlaybackContextType.Episode:
                if (param.Uri.Contains("your-episodes", StringComparison.OrdinalIgnoreCase))
                    NavigationHelpers.OpenYourEpisodes();
                else
                    NavigationHelpers.OpenShow(param.Uri, param.Title);
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

        var currentTrackId = GetCurrentTrackId()
            ?? (_playbackStateService?.CurrentTrackIsVideo == true ? _playbackStateService.CurrentTrackId : null);
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

    private void PopOutToWindow_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsAlbumArtExpanded = false;
        Ioc.Default.GetService<IShellSessionService>()?.UpdateLayout(s => s.PlayerWindowExpanded = true);
        Ioc.Default.GetService<IPanelDockingService>()?.Detach(DetachablePanel.Player);
        _logger?.LogInformation("[PlayerBar] Player popped out via bottom bar");
    }

    private void ShowMiniVideoPlayer_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsAlbumArtExpanded = false;
        Ioc.Default.GetService<MiniVideoPlayerViewModel>()?.Show();
        _logger?.LogInformation("[PlayerBar] Mini video player restored via bottom bar");
    }

    private void VideoPopoutTeachingTip_ActionButtonClick(TeachingTip sender, object args)
    {
        s_videoPopoutTeachingTipDismissed = true;
        ViewModel.IsAlbumArtExpanded = false;
        CloseVideoPopoutTeachingTipProgrammatically(sender);
        (_dockingService ?? Ioc.Default.GetService<IPanelDockingService>())?.Detach(DetachablePanel.Player);
    }

    private void VideoPopoutTeachingTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
    {
        if (_suppressVideoPopoutTeachingTipClosedHandling)
        {
            _suppressVideoPopoutTeachingTipClosedHandling = false;
            return;
        }

        if (args.Reason is TeachingTipCloseReason.LightDismiss or TeachingTipCloseReason.CloseButton)
            s_videoPopoutTeachingTipDismissed = true;
    }

    // Old Slider PointerPressed/Released/CaptureLost handlers were removed when
    // the WideProgressSlider / CompactProgressSlider Sliders were replaced by
    // CompositionProgressBar. Drag → seek now fires via the bar's typed events.
}
