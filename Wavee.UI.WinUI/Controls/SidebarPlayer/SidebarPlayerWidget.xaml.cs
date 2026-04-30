using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Services.Docking;
using Wavee.UI.WinUI.ViewModels;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Controls.SidebarPlayer;

/// <summary>
/// Sidebar-mounted player widget — alternative location to the bottom-docked
/// PlayerBar. Two visual states (Expanded / Collapsed) driven by
/// <see cref="ShellViewModel.SidebarPlayerCollapsed"/>. Reuses the singleton
/// <see cref="PlayerBarViewModel"/> as the only source of truth.
/// </summary>
public sealed partial class SidebarPlayerWidget : UserControl, IMediaSurfaceConsumer
{
    public PlayerBarViewModel ViewModel { get; }
    private readonly ShellViewModel _shellViewModel;
    private readonly IActiveVideoSurfaceService _videoSurface;
    private readonly MiniVideoPlayerViewModel? _miniVideoViewModel;
    private readonly ITrackLikeService? _likeService;
    private readonly IPlaybackStateService? _playbackStateService;
    private readonly ILogger<SidebarPlayerWidget>? _logger;
    private MediaPlayerElement? _videoElement;
    private FrameworkElement? _videoElementSurface;
    private Stretch _videoStretch = Stretch.Uniform;
    private bool _isLoaded;
    private bool _ownsVideoSurface;

    public SidebarPlayerWidget()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();
        _shellViewModel = Ioc.Default.GetRequiredService<ShellViewModel>();
        _videoSurface = Ioc.Default.GetRequiredService<IActiveVideoSurfaceService>();
        _miniVideoViewModel = Ioc.Default.GetService<MiniVideoPlayerViewModel>();
        _likeService = Ioc.Default.GetService<ITrackLikeService>();
        _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
        _logger = Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger<SidebarPlayerWidget>();

        InitializeComponent();

        // Drive the position-interpolation timer's visibility gate. When the
        // widget is detached the bar may still be rendering — the gate is the
        // bool-OR of every surface, so this is just one input.
        Loaded += OnWidgetLoaded;
        Unloaded += OnWidgetUnloaded;
        SizeChanged += OnWidgetSizeChanged;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _shellViewModel.PropertyChanged += OnShellPropertyChanged;
        _videoSurface.ActiveSurfaceChanged += OnActiveVideoSurfaceChanged;
        if (_miniVideoViewModel is not null)
            _miniVideoViewModel.PropertyChanged += OnMiniVideoViewModelPropertyChanged;

        // Reactive heart updates (both layouts share the wiring)
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;

        ExpandedHeartButton.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(OnHeartClicked);
        CollapsedHeartButton.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(OnHeartClicked);

        // Initial paint of state-driven visuals
        ApplyCollapseState();
        ApplyVideoStretchSelection();
        ApplyTintColor(ViewModel.AlbumArtColor);
        UpdateHeartState();

        // Re-tint when the theme switches — the blend is theme-dependent.
        ActualThemeChanged += (_, _) => ApplyTintColor(ViewModel.AlbumArtColor);
    }

    private void OnWidgetLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        ViewModel.SetSurfaceVisible("widget", true);
        // Default to "not hovered" — buttons faded out until pointer enters the widget.
        ApplyHoverReveal(hovered: false, animate: false);
        UpdateVideoSurfaceOwnership();
    }

    private void OnWidgetUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        ViewModel.SetSurfaceVisible("widget", false);
        ReleaseVideoSurfaceOwnership();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _shellViewModel.PropertyChanged -= OnShellPropertyChanged;
        _videoSurface.ActiveSurfaceChanged -= OnActiveVideoSurfaceChanged;
        if (_miniVideoViewModel is not null)
            _miniVideoViewModel.PropertyChanged -= OnMiniVideoViewModelPropertyChanged;
        if (_likeService != null) _likeService.SaveStateChanged -= OnSaveStateChanged;
    }

    private void OnWidgetSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Reserved for future responsive logic.
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.AlbumArtColor))
        {
            ApplyTintColor(ViewModel.AlbumArtColor);
        }
        else if (e.PropertyName is nameof(PlayerBarViewModel.HasTrack) or nameof(PlayerBarViewModel.TrackTitle))
        {
            UpdateHeartState();
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SidebarPlayerCollapsed))
        {
            ApplyCollapseState();
            UpdateVideoSurfaceOwnership();
        }
        else if (e.PropertyName is nameof(ShellViewModel.PlayerLocation)
                 or nameof(ShellViewModel.IsSidebarPlayerVisibleInShell))
        {
            UpdateVideoSurfaceOwnership();
        }
    }

    private void ApplyCollapseState()
    {
        var collapsed = _shellViewModel.SidebarPlayerCollapsed;
        ExpandedRoot.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedRoot.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        // Glyph: ChevronUp (E70E) when expanded — clicking will collapse.
        // ChevronDown (E70D) when collapsed — clicking will expand.
        ChevronGlyph.Glyph = collapsed ? "" : "";
        ChevronTooltip.Text = collapsed ? "Expand" : "Collapse";
    }

    private void OnActiveVideoSurfaceChanged(object? sender, MediaPlayer? surface)
        => DispatcherQueue?.TryEnqueue(UpdateVideoSurfaceOwnership);

    private void OnMiniVideoViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MiniVideoPlayerViewModel.IsOnVideoPage))
            UpdateVideoSurfaceOwnership();
    }

    private bool ShouldHostVideoSurface =>
        _isLoaded
        && _videoSurface.HasActiveSurface
        && _shellViewModel.IsSidebarPlayerVisibleInShell
        && !_shellViewModel.SidebarPlayerCollapsed
        && _miniVideoViewModel?.IsOnVideoPage != true;

    private void UpdateVideoSurfaceOwnership()
    {
        if (ShouldHostVideoSurface)
        {
            _miniVideoViewModel?.SetSuppressedBySidebarPlayer(true);
            _videoSurface.AcquireSurface(this);
            _ownsVideoSurface = true;
            return;
        }

        ReleaseVideoSurfaceOwnership();
    }

    private void ReleaseVideoSurfaceOwnership()
    {
        if (_ownsVideoSurface)
        {
            _videoSurface.ReleaseSurface(this);
            _ownsVideoSurface = false;
        }

        _miniVideoViewModel?.SetSuppressedBySidebarPlayer(false);
        ApplyVideoSurfaceVisibility();
    }

    private void ChevronButton_Click(object sender, RoutedEventArgs e)
    {
        _shellViewModel.SidebarPlayerCollapsed = !_shellViewModel.SidebarPlayerCollapsed;
    }

    private void MoveToBottomBar_Click(object sender, RoutedEventArgs e)
    {
        // Clear any leftover expanded-album-art state — the sidebar footer's
        // big-art view is owned by the bar and only the bar can toggle it off.
        ViewModel.IsAlbumArtExpanded = false;
        _shellViewModel.PlayerLocation = PlayerLocation.Bottom;
    }

    private void PopOutToWindow_Click(object sender, RoutedEventArgs e)
    {
        Ioc.Default.GetService<IPanelDockingService>()?.Detach(DetachablePanel.Player);
    }

    private void EndOfContextDismiss_Click(object sender, RoutedEventArgs e)
    {
        _playbackStateService?.DismissEndOfContext();
    }

    // CompositionProgressBar drag started — flag the VM as seeking so incoming
    // authoritative position updates don't overwrite the user's drag.
    private void ProgressBar_SeekStarted(object sender, System.EventArgs e)
        => ViewModel.StartSeeking();

    // CompositionProgressBar drag released — commit the new position via the VM.
    private void ProgressBar_SeekCommitted(object sender, double positionMs)
        => ViewModel.CommitSeekFromBar(positionMs);

    // Device picker logic lives in the SidebarPlayerWidget.DeviceFlyout.cs partial.

    // ── Hover-to-reveal ─────────────────────────────────────────────────
    //
    // Modern compact-player pattern: chrome is quiet at rest, blooms on hover.
    // Buttons fade out (Opacity 0) when pointer leaves the widget and fade back
    // in on entry. Album art / title / progress stay visible always.
    //
    // We use Composition implicit animations for a smooth crossfade without a
    // Storyboard. IsHitTestVisible is also toggled so faded buttons don't catch
    // accidental clicks (Opacity 0 is still hit-testable in WinUI by default).

    private const double HoverFadeDurationMs = 160;
    private bool _hoverAnimationsAttached;

    private void WidgetRoot_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ApplyHoverReveal(hovered: true, animate: true);
    }

    private void WidgetRoot_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ApplyHoverReveal(hovered: false, animate: true);
    }

    private void ApplyHoverReveal(bool hovered, bool animate)
    {
        if (animate) EnsureHoverAnimationsAttached();

        var op = hovered ? 1.0 : 0.0;
        foreach (var fe in EnumerateHoverElements())
        {
            fe.Opacity = op;
            fe.IsHitTestVisible = hovered;
        }
    }

    private System.Collections.Generic.IEnumerable<FrameworkElement> EnumerateHoverElements()
    {
        // Only the chevron is hover-revealed — transport / heart / volume stay
        // visible at rest so the player remains usable without first hovering.
        yield return ChevronButton;
    }

    private void EnsureHoverAnimationsAttached()
    {
        if (_hoverAnimationsAttached) return;
        _hoverAnimationsAttached = true;

        var compositor = Microsoft.UI.Xaml.Window.Current?.Compositor
            ?? Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();

        foreach (var fe in EnumerateHoverElements())
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(fe);
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            animation.Target = nameof(Microsoft.UI.Composition.Visual.Opacity);
            animation.Duration = TimeSpan.FromMilliseconds(HoverFadeDurationMs);
            var collection = compositor.CreateImplicitAnimationCollection();
            collection[nameof(Microsoft.UI.Composition.Visual.Opacity)] = animation;
            visual.ImplicitAnimations = collection;
        }
    }

    private void AlbumArt_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        NavigateToActiveContext();
        e.Handled = true;
    }

    private void VideoHost_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        try { NavigationHelpers.OpenVideoPlayer(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Open video player from sidebar failed"); }
        e.Handled = true;
    }

    public void AttachSurface(MediaPlayer player)
    {
        DetachElementSurface();
        if (_videoElement is null)
        {
            _videoElement = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = _videoStretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            ExpandedVideoHost.Children.Add(_videoElement);
        }

        _videoElement.SetMediaPlayer(player);
        ApplyVideoSurfaceVisibility();
    }

    public void AttachElementSurface(FrameworkElement element)
    {
        DetachMediaPlayerSurface();
        if (_videoElementSurface is not null && ReferenceEquals(_videoElementSurface, element))
            return;

        _videoElementSurface = element;
        element.HorizontalAlignment = HorizontalAlignment.Stretch;
        element.VerticalAlignment = VerticalAlignment.Stretch;
        element.IsHitTestVisible = false;
        ExpandedVideoHost.Children.Add(element);
        ApplyVideoSurfaceVisibility();
    }

    public void DetachSurface()
    {
        DetachMediaPlayerSurface();
        DetachElementSurface();
        ApplyVideoSurfaceVisibility();
    }

    private void DetachMediaPlayerSurface()
    {
        if (_videoElement is null) return;
        _videoElement.SetMediaPlayer(null);
        ExpandedVideoHost.Children.Remove(_videoElement);
        _videoElement = null;
    }

    private void DetachElementSurface()
    {
        if (_videoElementSurface is null) return;
        ExpandedVideoHost.Children.Remove(_videoElementSurface);
        _videoElementSurface.IsHitTestVisible = true;
        _videoElementSurface = null;
    }

    private void ApplyVideoSurfaceVisibility()
    {
        var hasVideo = _videoElement is not null || _videoElementSurface is not null;
        ExpandedVideoHost.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
        ExpandedAlbumArtImage.Visibility = hasVideo ? Visibility.Collapsed : Visibility.Visible;
    }

    private void VideoStretchNone_Click(object sender, RoutedEventArgs e)
        => SetVideoStretch(Stretch.None);

    private void VideoStretchUniform_Click(object sender, RoutedEventArgs e)
        => SetVideoStretch(Stretch.Uniform);

    private void VideoStretchUniformToFill_Click(object sender, RoutedEventArgs e)
        => SetVideoStretch(Stretch.UniformToFill);

    private void VideoStretchFill_Click(object sender, RoutedEventArgs e)
        => SetVideoStretch(Stretch.Fill);

    private void SetVideoStretch(Stretch stretch)
    {
        _videoStretch = stretch;
        if (_videoElement is not null)
            _videoElement.Stretch = stretch;
        ApplyVideoStretchSelection();
    }

    private void ApplyVideoStretchSelection()
    {
        VideoStretchNoneItem.IsChecked = _videoStretch == Stretch.None;
        VideoStretchUniformItem.IsChecked = _videoStretch == Stretch.Uniform;
        VideoStretchUniformToFillItem.IsChecked = _videoStretch == Stretch.UniformToFill;
        VideoStretchFillItem.IsChecked = _videoStretch == Stretch.Fill;
    }

    // Locks the Expanded album-art host to a square aspect ratio. Without this,
    // the row collapses to 0×0 the moment a track's image source is null,
    // because the host has HorizontalAlignment=Stretch but no explicit Height
    // — the Image inside drove the height via its intrinsic size. CrossFadeImage
    // already keeps an old layer painted during swaps, but this guarantees the
    // slot stays the right size even before the very first image arrives.
    private void ExpandedAlbumArtHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement fe && e.NewSize.Width > 0 && fe.Height != e.NewSize.Width)
            fe.Height = e.NewSize.Width;
    }

    private void TrackTitle_Click(object sender, RoutedEventArgs e)
    {
        NavigateToAlbum();
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
                NavigateToAlbum();
                break;
        }
    }

    // ── Heart wiring (mirrors PlayerBar pattern) ────────────────────────

    private string? GetCurrentTrackId() => _playbackStateService?.CurrentTrackId;

    private void OnSaveStateChanged()
    {
        DispatcherQueue?.TryEnqueue(UpdateHeartState);
    }

    private void UpdateHeartState()
    {
        var trackId = GetCurrentTrackId();
        var isLiked = !string.IsNullOrEmpty(trackId)
            && _likeService?.IsSaved(SavedItemType.Track, trackId) == true;
        ExpandedHeartButton.IsLiked = isLiked;
        CollapsedHeartButton.IsLiked = isLiked;
    }

    private void OnHeartClicked()
    {
        var trackId = GetCurrentTrackId();
        if (string.IsNullOrEmpty(trackId) || _likeService == null) return;

        // Only prefix bare base62 ids; full URIs pass through as-is.
        // See PlayerBar.OnPlayerHeartClicked for the background.
        var uri = trackId.Contains(':') ? trackId : $"spotify:track:{trackId}";
        var wasLiked = ExpandedHeartButton.IsLiked;
        _logger?.LogInformation("[SidebarPlayer] Heart clicked: trackId={TrackId}, wasLiked={WasLiked}", trackId, wasLiked);
        _likeService.ToggleSave(SavedItemType.Track, uri, wasLiked);
        var newLiked = !wasLiked;
        ExpandedHeartButton.IsLiked = newLiked;
        CollapsedHeartButton.IsLiked = newLiked;
    }

    // ── Palette tint (mirrors PlayerBar.ApplyTintColor) ─────────────────

    private void ApplyTintColor(string? hexColor)
    {
        if (string.IsNullOrEmpty(hexColor))
        {
            // No track palette → tint stops fall back to the sidebar surface (transparent
            // at top so nothing visually bleeds when there's nothing to bleed).
            var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            WidgetGradientTop.Color = transparent;
            WidgetGradientMid.Color = transparent;
            WidgetGradientBottom.Color = transparent;

            var defaultBrush = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            ExpandedAlbumArtHost.Background = defaultBrush;
            CollapsedAlbumArtHost.Background = defaultBrush;
            return;
        }

        try
        {
            var hex = hexColor.TrimStart('#');
            if (hex.Length != 6) return;
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);

            // Light mode: pre-blend toward white for legibility — same logic the
            // bottom bar uses. Dark mode keeps the saturated palette.
            byte tr = r, tg = g, tb = b;
            byte topAlpha = 96;  // dark mode: subtle saturated tint
            if (ActualTheme != ElementTheme.Dark)
            {
                const float blend = 0.72f;
                tr = (byte)(r * (1 - blend) + 255 * blend);
                tg = (byte)(g * (1 - blend) + 255 * blend);
                tb = (byte)(b * (1 - blend) + 255 * blend);
                topAlpha = 220; // light mode: strong pastel that reads as a tint pad
            }

            // Top stop and mid-hold stop both at full chosen alpha — the color holds
            // for the upper ~55% of the widget. Bottom stop matches RGB with alpha=0
            // so the gradient interpolates smoothly to transparent (a Transparent
            // black would muddy the gradient with gray).
            var fullColor = Windows.UI.Color.FromArgb(topAlpha, tr, tg, tb);
            var fadeColor = Windows.UI.Color.FromArgb(0, tr, tg, tb);
            WidgetGradientTop.Color = fullColor;
            WidgetGradientMid.Color = fullColor;
            WidgetGradientBottom.Color = fadeColor;

            var placeholderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(100, r, g, b));
            ExpandedAlbumArtHost.Background = placeholderBrush;
            CollapsedAlbumArtHost.Background = placeholderBrush;
        }
        catch
        {
            // Keep current tint on parse failure
        }
    }
}
