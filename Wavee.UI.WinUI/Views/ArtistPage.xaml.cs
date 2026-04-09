using System;
using System.Collections;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Wavee.Core.Http;
using Wavee.UI.WinUI.Controls.AlbumDetailPanel;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ArtistPage : Page, ITabBarItemContent
{
    private const int ShimmerCollapseDelayMs = 160;
    private const int ResizeDebounceDelayMs = 150;
    private const int PinnedItemFlyoutDelayMs = 900;
    private const int PinnedItemFlyoutCloseDelayMs = 220;

    // Avatar collapse — when the artist has a header image but no watch-feed
    // video, the 120px circular avatar is redundant with the hero and collapses
    // to reclaim horizontal space for the name/stats block.
    private const double AvatarExpandedWidth = 136; // 120 avatar + 16 gap
    private const double AvatarCollapseDurationMs = 320;
    private bool _avatarCollapsed;
    private int _avatarAnimGen;

    private readonly ILogger? _logger;
    private bool _showingContent;
    private bool _isNavigatingAway;
    private DispatcherQueueTimer? _pinnedItemHoverTimer;
    private DispatcherQueueTimer? _pinnedItemCloseTimer;
    private bool _isPointerOverPinnedItem;
    private bool _isPointerOverPinnedPreview;
    private bool _isPinnedItemPreviewOpen;
    private bool _isPinnedItemPressed;

    public ArtistViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ArtistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ArtistViewModel>();
        _logger = Ioc.Default.GetService<ILogger<ArtistPage>>();
        InitializeComponent();

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        Unloaded += ArtistPage_Unloaded;
        Loaded += ArtistPage_Loaded;
    }

    private void ArtistPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ArtistPage_Loaded;

        // Deferred setup — moved from constructor so InitializeComponent returns faster
        ContentContainer.Opacity = 0;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        SizeChanged += OnSizeChanged;
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private Windows.Media.Playback.MediaPlayer? _watchFeedMediaPlayer;
    private Microsoft.UI.Xaml.Controls.MediaPlayerElement? _watchFeedElement;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArtistViewModel.IsLoading))
        {
            if (!ViewModel.IsLoading && !_showingContent)
            {
                DispatcherQueue.TryEnqueue(CrossfadeToContent);
            }
        }
        else if (e.PropertyName is nameof(ArtistViewModel.WatchFeed)
                                 or nameof(ArtistViewModel.HeaderImageUrl))
        {
            // Re-evaluate avatar collapse state whenever either input changes
            // after the initial crossfade. SetupWatchFeedVideo is called from
            // CrossfadeToContent — no need here.
            if (_showingContent)
                UpdateAvatarLayout(animate: true);
        }
    }

    /// <summary>
    /// Collapses or expands the circular avatar depending on whether the
    /// artist has a header image (redundant with the hero) and a watch-feed
    /// video (keep the avatar so the video can play inside it). Animated
    /// with composition-backed Scale+Opacity plus a per-frame Width
    /// interpolation so the name/stats block re-layouts smoothly.
    /// </summary>
    private void UpdateAvatarLayout(bool animate)
    {
        if (AvatarWrapper == null || ArtistImageContainer == null) return;

        bool shouldCollapse = !string.IsNullOrEmpty(ViewModel.HeaderImageUrl)
                              && string.IsNullOrEmpty(ViewModel.WatchFeed?.VideoUrl);

        if (shouldCollapse == _avatarCollapsed) return;
        _avatarCollapsed = shouldCollapse;

        double targetWidth = shouldCollapse ? 0 : AvatarExpandedWidth;
        double targetOpacity = shouldCollapse ? 0 : 1;
        float targetScale = shouldCollapse ? 0.6f : 1f;

        // Anchor composition transforms around the avatar center
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ArtistImageContainer);
        visual.CenterPoint = new System.Numerics.Vector3(60, 60, 0);

        if (!animate)
        {
            AvatarWrapper.Width = targetWidth;
            ArtistImageContainer.Opacity = targetOpacity;
            visual.Scale = new System.Numerics.Vector3(targetScale, targetScale, 1f);
            _avatarAnimGen++; // cancel any in-flight width interpolation
            return;
        }

        // Composition-driven fade + scale (runs off the UI thread for buttery smoothness)
        CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Opacity(to: targetOpacity,
                     duration: TimeSpan.FromMilliseconds(220),
                     easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
            .Scale(to: new System.Numerics.Vector3(targetScale, targetScale, 1f),
                   duration: TimeSpan.FromMilliseconds(AvatarCollapseDurationMs),
                   easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
            .Start(ArtistImageContainer);

        // Layout-affecting Width interpolation via CompositionTarget.Rendering
        // (DoubleAnimation on Width is a dependent animation and looks janky;
        //  per-frame manual interpolation gives a proper 60fps layout re-flow).
        double startWidth = double.IsNaN(AvatarWrapper.Width) ? AvatarExpandedWidth : AvatarWrapper.Width;
        var startTime = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(AvatarCollapseDurationMs);
        var myGen = ++_avatarAnimGen;

        EventHandler<object>? tick = null;
        tick = (_, _) =>
        {
            if (myGen != _avatarAnimGen || _isNavigatingAway)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= tick;
                return;
            }

            var elapsed = DateTime.UtcNow - startTime;
            double t = Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            // Cubic ease-out — snappy start, soft landing
            double eased = 1 - Math.Pow(1 - t, 3);
            AvatarWrapper.Width = startWidth + (targetWidth - startWidth) * eased;

            if (t >= 1)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= tick;
            }
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += tick;
    }

    private void SetupWatchFeedVideo()
    {
        if (_isNavigatingAway || !IsLoaded)
            return;

        if (ViewModel.WatchFeed?.VideoUrl == null) return;

        // Clean up previous
        TeardownWatchFeed();

        // Create MediaPlayer
        _watchFeedMediaPlayer = new Windows.Media.Playback.MediaPlayer
        {
            IsLoopingEnabled = true,
            IsMuted = true,
            AutoPlay = true
        };
        _watchFeedMediaPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(
            new Uri(ViewModel.WatchFeed.VideoUrl));

        // Create MediaPlayerElement programmatically (never in XAML — WinUI teardown bug)
        _watchFeedElement = new Microsoft.UI.Xaml.Controls.MediaPlayerElement
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            AreTransportControlsEnabled = false,
            AutoPlay = true
        };
        _watchFeedElement.SetMediaPlayer(_watchFeedMediaPlayer);

        WatchFeedGrid.Children.Insert(0, _watchFeedElement);

        // Constrain the MediaPlayerElement so the swap chain doesn't overflow
        _watchFeedElement.Width = 120;
        _watchFeedElement.Height = 120;

        // Apply Composition clip directly on the WatchFeedGrid visual
        // (must be on the immediate container — swap chains ignore parent clips)
        var gridVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(WatchFeedGrid);
        var compositor = gridVisual.Compositor;
        var ellipse = compositor.CreateEllipseGeometry();
        ellipse.Center = new System.Numerics.Vector2(60, 60);
        ellipse.Radius = new System.Numerics.Vector2(60, 60);
        gridVisual.Clip = compositor.CreateGeometricClip(ellipse);

        // Crossfade: wait for video to start rendering, then fade in over the static image
        _watchFeedMediaPlayer.VideoFrameAvailable += OnFirstVideoFrame;
        _watchFeedMediaPlayer.IsVideoFrameServerEnabled = true;
    }

    private void OnFirstVideoFrame(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        // Only need the first frame — unsubscribe immediately
        sender.VideoFrameAvailable -= OnFirstVideoFrame;
        sender.IsVideoFrameServerEnabled = false;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isNavigatingAway || _watchFeedMediaPlayer != sender || _watchFeedElement == null || WatchFeedGrid.XamlRoot == null)
                return;

            // Fade video in over the static image
            try
            {
                AnimationBuilder.Create()
                    .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(600),
                             easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
                    .Start(WatchFeedGrid);

                // Show hover overlay
                WatchFeedHoverOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Watch feed first-frame transition skipped during navigation teardown");
            }
        });
    }

    private void TeardownWatchFeed()
    {
        if (_watchFeedElement != null)
        {
            _watchFeedElement.SetMediaPlayer(null);
            WatchFeedGrid.Children.Remove(_watchFeedElement);
            _watchFeedElement = null;
        }
        if (_watchFeedMediaPlayer != null)
        {
            _watchFeedMediaPlayer.VideoFrameAvailable -= OnFirstVideoFrame;
            try { _watchFeedMediaPlayer.Pause(); } catch { }
            try { _watchFeedMediaPlayer.Dispose(); } catch { }
            _watchFeedMediaPlayer = null;
        }
    }

    private async void CrossfadeToContent()
    {
        if (_showingContent) return;
        _showingContent = true;

        // Start both simultaneously — content fades in AS shimmer fades out
        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(150),
                     layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(ShimmerContainer);

        ContentContainer.Opacity = 1;
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(150),
                     layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(ContentContainer);

        // Collapse shimmer after animation completes
        await Task.Delay(ShimmerCollapseDelayMs);
        if (_showingContent)
            ShimmerContainer.Visibility = Visibility.Collapsed;

        // Set up watch feed video now that the content is visible
        SetupWatchFeedVideo();

        // Decide whether to collapse the redundant circular avatar now that
        // we know the final HeaderImageUrl + WatchFeed state.
        UpdateAvatarLayout(animate: true);
    }

    private void ArtistPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // Only tear down ephemeral resources. ViewModel subscriptions stay alive
        // because the page may be re-attached from navigation cache.
        _isNavigatingAway = true;
        CancelPinnedItemPreview();
        CancelResizeDebounce();
        CollapseExpandedAlbum();
        TeardownWatchFeed();
    }

    private void CancelResizeDebounce()
    {
        if (_resizeDebounceCts == null)
            return;

        try { _resizeDebounceCts.Cancel(); } catch (ObjectDisposedException) { }
        _resizeDebounceCts.Dispose();
        _resizeDebounceCts = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Hero height = 45% of page height (min 300)
        if (HeroGrid != null)
            HeroGrid.Height = Math.Max(300, e.NewSize.Height * 0.45);

        // Debounced recompute of expanded panel position
        if (_activeDetailPanel != null && _expandedItem != null)
        {
            CancelResizeDebounce();
            _resizeDebounceCts = new CancellationTokenSource();
            var token = _resizeDebounceCts.Token;
            _ = RecomputeExpandedPanelAsync(token);
        }
    }

    private async Task RecomputeExpandedPanelAsync(CancellationToken ct)
    {
        try { await Task.Delay(ResizeDebounceDelayMs, ct); }
        catch (OperationCanceledException) { return; }

        if (_activeDetailPanel == null || _expandedItem == null ||
            _originalRepeater == null || _splitParent == null || _originalItemsSource == null)
            return;

        ApplySplitLayout();
    }

    /// <summary>
    /// Shared logic: computes columns from available width, splits items before/after
    /// the expanded item's row, updates both repeaters, and positions the notch.
    /// Called on initial expand and on debounced resize.
    /// </summary>
    private void ApplySplitLayout()
    {
        if (_originalRepeater == null || _splitParent == null || _originalItemsSource == null ||
            _activeDetailPanel == null)
            return;

        var layout = _originalRepeater.Layout as UniformGridLayout;
        var allItems = _originalItemsSource as System.Collections.IList;
        if (allItems == null) return;

        var availableWidth = _splitParent.ActualWidth;
        var minWidth = layout?.MinItemWidth ?? 160;
        var spacing = layout?.MinColumnSpacing ?? 12;
        var columns = Math.Max(1, (int)Math.Floor((availableWidth + spacing) / (minWidth + spacing)));

        // Split point: end of the expanded item's row
        var rowOfItem = _expandedItemIndex / columns;
        var splitAfterIndex = Math.Min((rowOfItem + 1) * columns, allItems.Count);

        var itemsBefore = new System.Collections.Generic.List<object>();
        var itemsAfter = new System.Collections.Generic.List<object>();
        for (int i = 0; i < allItems.Count; i++)
        {
            if (i < splitAfterIndex)
                itemsBefore.Add(allItems[i]!);
            else
                itemsAfter.Add(allItems[i]!);
        }

        // Update the first repeater
        _originalRepeater.ItemsSource = itemsBefore;

        // Update or create/remove the second repeater
        if (_splitRepeaterAfter != null)
        {
            if (itemsAfter.Count > 0)
                _splitRepeaterAfter.ItemsSource = itemsAfter;
            else
            {
                _splitParent.Children.Remove(_splitRepeaterAfter);
                _splitRepeaterAfter = null;
            }
        }
        else if (itemsAfter.Count > 0)
        {
            _splitRepeaterAfter = new ItemsRepeater
            {
                Layout = new UniformGridLayout
                {
                    MinItemWidth = minWidth,
                    MinItemHeight = layout?.MinItemHeight ?? 240,
                    MinRowSpacing = layout?.MinRowSpacing ?? 12,
                    MinColumnSpacing = spacing,
                    ItemsStretch = Microsoft.UI.Xaml.Controls.UniformGridLayoutItemsStretch.Uniform
                },
                ItemTemplate = _originalRepeater.ItemTemplate,
                ItemsSource = itemsAfter
            };
            var panelIndex = _splitParent.Children.IndexOf(_activeDetailPanel);
            if (panelIndex >= 0)
                _splitParent.Children.Insert(panelIndex + 1, _splitRepeaterAfter);
        }

        // Position notch at the expanded item's center
        var columnIndex = _expandedItemIndex % columns;
        var cellWidth = (availableWidth - (columns - 1) * spacing) / columns;
        _activeDetailPanel.NotchOffsetX = columnIndex * (cellWidth + spacing) + cellWidth / 2;
    }

    private void TopTracksLayout_ColumnCountChanged(object? sender, int columns)
    {
        ViewModel.ColumnCount = columns;
    }

    private void TopTracksRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is Controls.Track.TrackItem trackItem)
        {
            trackItem.PlayCommand = ViewModel.PlayTrackCommand;
        }
    }

    public void RefreshWithParameter(object? parameter)
    {
        _isNavigatingAway = false;
        LoadNewContent(parameter);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isNavigatingAway = false;

        // CONNECTED-ANIM (disabled): re-enable to restore source→destination morph
        // ConnectedAnimationHelper.TryStartAnimation(ConnectedAnimationHelper.ArtistImage, ArtistImageContainer);

        // Extract the incoming artist URI
        var incomingUri = e.Parameter is ContentNavigationParameter nav ? nav.Uri
                        : e.Parameter as string;

        // Back navigation or re-entering the same artist: skip re-fetch, restore watch feed
        if (e.NavigationMode == Microsoft.UI.Xaml.Navigation.NavigationMode.Back
            || (incomingUri != null && incomingUri == ViewModel.ArtistId))
        {
            SetupWatchFeedVideo();
            return;
        }

        LoadNewContent(e.Parameter);
    }

    private void LoadNewContent(object? parameter)
    {
        // Reset visual state for fresh load
        PageScrollView.ScrollTo(0, 0);
        _showingContent = false;
        ContentContainer.Opacity = 0;
        ShimmerContainer.Visibility = Visibility.Visible;
        ShimmerContainer.Opacity = 1;

        if (parameter is ContentNavigationParameter navParam)
        {
            ViewModel.Initialize(navParam.Uri);
            ViewModel.PrefillFrom(navParam);
        }
        else if (parameter is string artistId)
        {
            ViewModel.Initialize(artistId);
        }

        // Fire-and-forget: page renders shimmer instantly, data populates as it arrives.
        // SetupWatchFeedVideo is called from CrossfadeToContent after data loads.
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void Release_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ArtistReleaseVm release)
        {
            var param = new ContentNavigationParameter
            {
                Uri = release.Uri ?? release.Id,
                Title = release.Name,
                ImageUrl = release.ImageUrl
            };
            NavigationHelpers.OpenAlbum(param, release.Name ?? "Album", NavigationHelpers.IsCtrlPressed());
        }
    }

    // ── Inline album expand (DOM-style visual tree manipulation) ──

    private AlbumDetailPanel? _activeDetailPanel;
    private EventHandler? _closeRequestedHandler;
    private ItemsRepeater? _splitRepeaterAfter;
    private StackPanel? _splitParent;
    private int _splitInsertIndex;
    private ItemsRepeater? _originalRepeater;
    private object? _originalItemsSource;

    // State needed to recompute split on resize
    private LazyReleaseItem? _expandedItem;
    private int _expandedItemIndex;
    private CancellationTokenSource? _resizeDebounceCts;

    private readonly IColorService _colorService = Ioc.Default.GetRequiredService<IColorService>();

    private void AlbumCard_Click(object sender, EventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        var repeater = FindParent<ItemsRepeater>(fe);
        if (repeater == null) return;

        // Walk up to find the direct child of the repeater (the template root)
        DependencyObject? current = fe;
        DependencyObject? parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        while (parent != null && parent != repeater)
        {
            current = parent;
            parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        if (current is not UIElement templateRoot) return;

        var index = repeater.GetElementIndex(templateRoot);
        if (index < 0) return;

        var items = repeater.ItemsSource as System.Collections.IList;
        if (items == null || index >= items.Count) return;

        var item = items[index] as LazyReleaseItem;
        if (item == null || !item.IsLoaded || item.Data == null) return;

        // If clicking the same album that was expanded, just collapse (toggle)
        if (ViewModel.ExpandedAlbum?.Id == item.Id)
        {
            CollapseExpandedAlbum();
            return;
        }

        // Capture the true original repeater/items before collapsing
        // (clicking in _splitRepeaterAfter means the real repeater is _originalRepeater)
        var trueRepeater = _originalRepeater ?? repeater;
        var trueItemsSource = (_originalItemsSource ?? repeater.ItemsSource) as System.Collections.IList;

        // Collapse any existing expansion first (restores original state)
        CollapseExpandedAlbum();

        // Now trueRepeater has its full ItemsSource restored
        if (trueItemsSource == null) return;

        var itemIndex = trueItemsSource.IndexOf(item);
        if (itemIndex < 0) return;

        // Find the parent StackPanel and the repeater's index in it
        var parentPanel = trueRepeater.Parent as StackPanel;
        if (parentPanel == null) return;

        var repeaterIndex = parentPanel.Children.IndexOf(trueRepeater);
        if (repeaterIndex < 0) return;

        // Save original state for restore + resize recompute
        _originalRepeater = trueRepeater;
        _originalItemsSource = trueItemsSource;
        _splitParent = parentPanel;
        _expandedItem = item;
        _expandedItemIndex = itemIndex;

        // Create the detail panel
        _activeDetailPanel = new AlbumDetailPanel();
        _activeDetailPanel.Album = item.Data;
        _activeDetailPanel.Tracks = ViewModel.ExpandedAlbumTracks;
        _closeRequestedHandler = (_, _) => CollapseExpandedAlbum();
        _activeDetailPanel.CloseRequested += _closeRequestedHandler;

        // Fetch extracted color for album art gradient (uses cache if available)
        _ = FetchAlbumColorAsync(item.Data, _activeDetailPanel);

        // Insert detail panel after the repeater
        _splitInsertIndex = repeaterIndex + 1;
        parentPanel.Children.Insert(_splitInsertIndex, _activeDetailPanel);

        // Compute split, notch, and second repeater
        ApplySplitLayout();

        // Auto-scroll so the clicked album card row is visible at the top,
        // with the detail panel below it
        _activeDetailPanel.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            VerticalAlignmentRatio = 0.5, // center the panel, which puts the card row above it
            VerticalOffset = -200          // nudge up so the card row is also visible
        });

        // Update ViewModel state
        ViewModel.ExpandAlbumCommand.Execute(item);
    }

    private void CollapseExpandedAlbum()
    {
        if (_splitParent == null || _originalRepeater == null) return;

        // Unsubscribe event handler to prevent memory leak
        if (_activeDetailPanel != null && _closeRequestedHandler != null)
            _activeDetailPanel.CloseRequested -= _closeRequestedHandler;
        _closeRequestedHandler = null;

        // Detach tracks BEFORE removing from visual tree to prevent COMException
        // (ItemsRepeater can't process CollectionChanged while disconnected)
        if (_activeDetailPanel != null)
        {
            _activeDetailPanel.Tracks = null;
            _splitParent.Children.Remove(_activeDetailPanel);
        }
        if (_splitRepeaterAfter != null)
            _splitParent.Children.Remove(_splitRepeaterAfter);

        // Restore original items source
        _originalRepeater.ItemsSource = _originalItemsSource;

        // Clean up
        _activeDetailPanel = null;
        _splitRepeaterAfter = null;
        _splitParent = null;
        _originalRepeater = null;
        _originalItemsSource = null;
        _expandedItem = null;
        _expandedItemIndex = -1;

        ViewModel.CollapseAlbumCommand.Execute(null);
    }

    private static T? FindParent<T>(DependencyObject child, DependencyObject? stopAt = null) where T : DependencyObject
    {
        var current = child;
        var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        while (parent != null && parent != stopAt)
        {
            if (parent is T found) return found;
            current = parent;
            parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        // If we stopped at stopAt, return the last child before it
        if (stopAt != null && parent == stopAt)
            return current as T;
        return null;
    }

    private void RelatedArtist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is RelatedArtistVm artist)
        {
            var param = new ContentNavigationParameter
            {
                Uri = artist.Uri ?? artist.Id ?? "",
                Title = artist.Name,
                ImageUrl = artist.ImageUrl
            };
            NavigationHelpers.OpenArtist(param, artist.Name ?? "Artist", NavigationHelpers.IsCtrlPressed());
        }
    }

    /// <summary>
    /// Detach the MediaPlayer from the visual tree BEFORE the page is removed.
    /// This prevents COM E_ABORT when WinUI tears down the MediaPlayerElement.
    /// </summary>
    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        _isNavigatingAway = true;
        CancelPinnedItemPreview();
        CancelResizeDebounce();
        CollapseExpandedAlbum();
        TeardownWatchFeed();
    }


    private void WatchFeedOverlay_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        AnimationBuilder.Create()
            .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(150))
            .Start(WatchFeedHoverOverlay);
    }

    private void WatchFeedOverlay_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        AnimationBuilder.Create()
            .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(150))
            .Start(WatchFeedHoverOverlay);
    }

    private void WatchFeedOverlay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // Toggle mute on tap
        if (_watchFeedMediaPlayer != null)
            _watchFeedMediaPlayer.IsMuted = !_watchFeedMediaPlayer.IsMuted;
    }

    private void PinnedItem_Click(object sender, RoutedEventArgs e)
    {
        CancelPinnedItemPreview();

        if (ViewModel.PinnedItem?.Uri == null) return;

        var type = ViewModel.PinnedItem.Type?.ToUpperInvariant();

        // TRACK type: play the track in the artist context
        if (type == "TRACK")
        {
            var playback = Ioc.Default.GetService<IPlaybackService>();
            if (playback != null && !string.IsNullOrEmpty(ViewModel.ArtistId))
                _ = playback.PlayTrackInContextAsync(ViewModel.PinnedItem.Uri, ViewModel.ArtistId);
            return;
        }

        // CONNECTED-ANIM (disabled): nothing to cancel once nothing is being prepared
        // ConnectedAnimationHelper.CancelPending();

        var param = new ContentNavigationParameter
        {
            Uri = ViewModel.PinnedItem.Uri,
            Title = ViewModel.PinnedItem.Title,
            ImageUrl = ViewModel.PinnedItem.ImageUrl
        };

        if (type is "ALBUM" or "SINGLE" or "EP")
            NavigationHelpers.OpenAlbum(param, ViewModel.PinnedItem.Title ?? "Album", NavigationHelpers.IsCtrlPressed());
        else
            NavigationHelpers.OpenAlbum(param, ViewModel.PinnedItem.Title ?? "Release", NavigationHelpers.IsCtrlPressed());
    }

    private void PinnedItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPinnedItem = true;
        _isPointerOverPinnedPreview = false;
        _isPinnedItemPressed = false;
        _pinnedItemCloseTimer?.Stop();
        SetPinnedItemVisual(PinnedItemVisualState.Hover);
        StartPinnedItemPreviewDelay();
    }

    private void PinnedItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPinnedItem = false;
        _isPinnedItemPressed = false;
        _pinnedItemHoverTimer?.Stop();
        if (!_isPinnedItemPreviewOpen)
            StartPinnedItemPreviewCloseDelay();
        SetPinnedItemVisual(PinnedItemVisualState.Normal);
    }

    private void PinnedItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isPinnedItemPressed = true;
        CancelPinnedItemPreview();
        SetPinnedItemVisual(PinnedItemVisualState.Pressed);
    }

    private void PinnedItem_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPinnedItemPressed = false;
        SetPinnedItemVisual(_isPointerOverPinnedItem ? PinnedItemVisualState.Hover : PinnedItemVisualState.Normal);
    }

    private void PinnedItem_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPinnedItem = false;
        _isPointerOverPinnedPreview = false;
        _isPinnedItemPressed = false;
        CancelPinnedItemPreview();
        SetPinnedItemVisual(PinnedItemVisualState.Normal);
    }

    private void PinnedItemPreview_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPinnedPreview = true;
        _pinnedItemCloseTimer?.Stop();
        SetPinnedItemVisual(PinnedItemVisualState.Hover);
    }

    private void PinnedItemPreview_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPinnedPreview = false;
        StartPinnedItemPreviewCloseDelay();
    }

    private void PinnedItemPreviewFlyout_Opened(object? sender, object e)
    {
        _isPinnedItemPreviewOpen = true;
        _pinnedItemCloseTimer?.Stop();
    }

    private void PinnedItemPreviewFlyout_Closed(object? sender, object e)
    {
        _isPinnedItemPreviewOpen = false;
        _isPointerOverPinnedPreview = false;
        if (!_isPointerOverPinnedItem)
            SetPinnedItemVisual(PinnedItemVisualState.Normal);
    }

    private void StartPinnedItemPreviewDelay()
    {
        if (ViewModel.PinnedItem == null)
            return;

        _pinnedItemHoverTimer ??= CreatePinnedItemHoverTimer();
        _pinnedItemHoverTimer.Stop();
        _pinnedItemHoverTimer.Start();
    }

    private void StartPinnedItemPreviewCloseDelay()
    {
        _pinnedItemCloseTimer ??= CreatePinnedItemCloseTimer();
        _pinnedItemCloseTimer.Stop();
        _pinnedItemCloseTimer.Start();
    }

    private DispatcherQueueTimer CreatePinnedItemHoverTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(PinnedItemFlyoutDelayMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (!_isPointerOverPinnedItem || _isPinnedItemPressed || _isNavigatingAway || ViewModel.PinnedItem == null)
                return;

            _pinnedItemCloseTimer?.Stop();
            FlyoutBase.ShowAttachedFlyout(PinnedItemButton);
        };
        return timer;
    }

    private DispatcherQueueTimer CreatePinnedItemCloseTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(PinnedItemFlyoutCloseDelayMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (_isPointerOverPinnedItem || _isPointerOverPinnedPreview)
                return;

            CancelPinnedItemPreview();
            SetPinnedItemVisual(PinnedItemVisualState.Normal);
        };
        return timer;
    }

    private void CancelPinnedItemPreview()
    {
        _pinnedItemHoverTimer?.Stop();
        _pinnedItemCloseTimer?.Stop();

        if (PinnedItemButton != null)
            FlyoutBase.GetAttachedFlyout(PinnedItemButton)?.Hide();

        _isPinnedItemPreviewOpen = false;
    }

    private void SetPinnedItemVisual(PinnedItemVisualState state)
    {
        if (PinnedItemPill == null)
            return;

        PinnedItemPill.Background = GetPinnedItemBrush(state switch
        {
            PinnedItemVisualState.Hover => "PinnedItemHoverBackgroundBrush",
            PinnedItemVisualState.Pressed => "PinnedItemPressedBackgroundBrush",
            _ => "PinnedItemNormalBackgroundBrush"
        });
        PinnedItemPill.BorderBrush = GetPinnedItemBrush(state switch
        {
            PinnedItemVisualState.Hover => "PinnedItemHoverBorderBrush",
            PinnedItemVisualState.Pressed => "PinnedItemPressedBorderBrush",
            _ => "PinnedItemNormalBorderBrush"
        });

        var scale = state switch
        {
            PinnedItemVisualState.Hover => 1.025f,
            PinnedItemVisualState.Pressed => 0.965f,
            _ => 1f
        };

        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(PinnedItemPill);
        visual.CenterPoint = new System.Numerics.Vector3(
            (float)(PinnedItemPill.ActualWidth / 2),
            (float)(PinnedItemPill.ActualHeight / 2),
            0);

        AnimationBuilder.Create()
            .Scale(to: new System.Numerics.Vector3(scale, scale, 1),
                   duration: TimeSpan.FromMilliseconds(state == PinnedItemVisualState.Pressed ? 80 : 140),
                   easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
            .Start(PinnedItemPill);
    }

    private Brush GetPinnedItemBrush(string resourceKey)
    {
        if (Resources.TryGetValue(resourceKey, out var resource) && resource is Brush brush)
            return brush;

        return PinnedItemPill.Background;
    }

    private enum PinnedItemVisualState
    {
        Normal,
        Hover,
        Pressed
    }

    private async Task FetchAlbumColorAsync(ArtistReleaseVm album, AlbumDetailPanel panel)
    {
        if (string.IsNullOrEmpty(album.ImageUrl)) return;

        var imageUrl = SpotifyImageHelper.ToHttpsUrl(album.ImageUrl);
        if (string.IsNullOrEmpty(imageUrl)) return;

        try
        {
            var color = await _colorService.GetColorAsync(imageUrl);
            if (color == null) return;

            // Use Spotify's pre-computed theme-appropriate color
            var isDark = ActualTheme == ElementTheme.Dark;
            var hex = isDark
                ? color.DarkHex ?? color.RawHex
                : color.LightHex ?? color.RawHex;

            if (!string.IsNullOrEmpty(hex))
                panel.ColorHex = hex;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch album color");
        }
    }

    private void AlbumCard_Hover(object sender, EventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        var repeater = FindParent<ItemsRepeater>(fe);
        if (repeater == null) return;

        DependencyObject? current = fe;
        DependencyObject? parentObj = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        while (parentObj != null && parentObj != repeater)
        {
            current = parentObj;
            parentObj = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        if (current is not UIElement templateRoot) return;

        var index = repeater.GetElementIndex(templateRoot);
        if (index < 0) return;

        var items = repeater.ItemsSource as System.Collections.IList;
        if (items == null || index >= items.Count) return;

        var item = items[index] as LazyReleaseItem;
        if (item?.Data?.ImageUrl == null) return;

        var imageUrl = SpotifyImageHelper.ToHttpsUrl(item.Data.ImageUrl);
        if (string.IsNullOrEmpty(imageUrl)) return;

        // Fire-and-forget prefetch via service (hot + SQLite + API)
        _ = _colorService.GetColorAsync(imageUrl);
    }

    private void LocationButton_LocationChanged(object? sender, string city)
    {
        ViewModel.UserLocationName = city;
        ViewModel.RefreshNearUserFlags();
    }

    private void ConcertCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string uri && !string.IsNullOrEmpty(uri))
        {
            var title = (btn.DataContext as ConcertVm)?.Title;
            var param = new ContentNavigationParameter
            {
                Uri = uri,
                Title = title
            };
            NavigationHelpers.OpenConcert(param, title ?? "Concert", NavigationHelpers.IsCtrlPressed());
        }
    }

}
