using System;
using System.Collections;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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
    private readonly ILogger? _logger;
    private bool _showingContent;

    public ArtistViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ArtistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ArtistViewModel>();
        _logger = Ioc.Default.GetService<ILogger<ArtistPage>>();
        InitializeComponent();

        // Hide content initially — shimmer is visible, content is collapsed
        ContentContainer.Opacity = 0;

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        SizeChanged += OnSizeChanged;
        Unloaded += ArtistPage_Unloaded;
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
        else if (e.PropertyName is nameof(ArtistViewModel.WatchFeed))
        {
            SetupWatchFeedVideo();
        }
    }

    private void SetupWatchFeedVideo()
    {
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
            // Fade video in over the static image
            AnimationBuilder.Create()
                .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(600),
                         easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
                .Start(WatchFeedGrid);

            // Show hover overlay
            WatchFeedHoverOverlay.Visibility = Visibility.Visible;
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
            _watchFeedMediaPlayer.Pause();
            _watchFeedMediaPlayer.Dispose();
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
        await Task.Delay(160);
        if (_showingContent)
            ShimmerContainer.Visibility = Visibility.Collapsed;

        // Set up watch feed video now that the content is visible
        SetupWatchFeedVideo();
    }

    private void ArtistPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        SizeChanged -= OnSizeChanged;
        TeardownWatchFeed();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Hero height = 45% of page height (min 300)
        if (HeroGrid != null)
            HeroGrid.Height = Math.Max(300, e.NewSize.Height * 0.45);

        // Debounced recompute of expanded panel position
        if (_activeDetailPanel != null && _expandedItem != null)
        {
            _resizeDebounceCts?.Cancel();
            _resizeDebounceCts = new CancellationTokenSource();
            var token = _resizeDebounceCts.Token;
            _ = RecomputeExpandedPanelAsync(token);
        }
    }

    private async Task RecomputeExpandedPanelAsync(CancellationToken ct)
    {
        try { await Task.Delay(150, ct); }
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

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            ConnectedAnimationHelper.TryStartAnimation(ConnectedAnimationHelper.ArtistImage, ArtistImageContainer);

            if (e.Parameter is ContentNavigationParameter nav)
            {
                ViewModel.PrefillFrom(nav);
                ViewModel.Initialize(nav.Uri);
                await ViewModel.LoadCommand.ExecuteAsync(null);
            }
            else if (e.Parameter is string artistId)
            {
                ViewModel.Initialize(artistId);
                await ViewModel.LoadCommand.ExecuteAsync(null);
            }
            // Set up watch feed after data is loaded
            SetupWatchFeedVideo();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unhandled error in ArtistPage OnNavigatedTo");
        }
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
        _activeDetailPanel.CloseRequested += (_, _) => CollapseExpandedAlbum();

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
        if (ViewModel.PinnedItem?.Uri == null) return;

        ConnectedAnimationHelper.CancelPending();

        var param = new ContentNavigationParameter
        {
            Uri = ViewModel.PinnedItem.Uri,
            Title = ViewModel.PinnedItem.Title,
            ImageUrl = ViewModel.PinnedItem.ImageUrl
        };

        if (ViewModel.PinnedItem.Type == "ALBUM" || ViewModel.PinnedItem.Type == "SINGLE" || ViewModel.PinnedItem.Type == "EP")
            NavigationHelpers.OpenAlbum(param, ViewModel.PinnedItem.Title ?? "Album", NavigationHelpers.IsCtrlPressed());
        else
            NavigationHelpers.OpenAlbum(param, ViewModel.PinnedItem.Title ?? "Release", NavigationHelpers.IsCtrlPressed());
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

    private async void OpenLocationDialog_Click(object sender, RoutedEventArgs e)
    {
        var searchBox = new AutoSuggestBox
        {
            PlaceholderText = "Search city...",
            QueryIcon = new SymbolIcon(Symbol.Find),
            Width = 300,
            DisplayMemberPath = "FullName"
        };

        var useCurrentBtn = new HyperlinkButton { Content = "Use current location", Padding = new Thickness(0) };

        var panel = new StackPanel { Spacing = 16 };

        if (!string.IsNullOrEmpty(ViewModel.UserLocationName))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Current: {ViewModel.UserLocationName}",
                Opacity = 0.6,
                FontSize = 13
            });
        }

        panel.Children.Add(searchBox);
        panel.Children.Add(useCurrentBtn);

        var dialog = new ContentDialog
        {
            Title = "Concert location",
            Content = panel,
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };

        // Search via ViewModel → ILocationService
        CancellationTokenSource? searchCts = null;
        searchBox.TextChanged += async (s, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = s.Text?.Trim();
            if (string.IsNullOrEmpty(query) || query.Length < 2) return;

            searchCts?.Cancel();
            searchCts = new CancellationTokenSource();
            var ct = searchCts.Token;

            try
            {
                await Task.Delay(300, ct);
                var results = await ViewModel.SearchLocationsAsync(query, ct);
                if (!ct.IsCancellationRequested)
                    s.ItemsSource = results;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger?.LogWarning(ex, "Location search failed"); }
        };

        // Save selected location via ViewModel
        searchBox.SuggestionChosen += async (s, args) =>
        {
            if (args.SelectedItem is not LocationSearchResult loc || string.IsNullOrEmpty(loc.GeonameId)) return;
            try
            {
                await ViewModel.SaveLocationAsync(loc.GeonameId, loc.Name);
                dialog.Hide();
            }
            catch (Exception ex) { _logger?.LogWarning(ex, "Failed to save location"); }
        };

        // Resolve current location, then confirm before saving
        useCurrentBtn.Click += async (s, args) =>
        {
            try
            {
                useCurrentBtn.IsEnabled = false;
                useCurrentBtn.Content = "Detecting location...";

                var resolved = await ViewModel.ResolveCurrentLocationAsync();
                if (resolved == null)
                {
                    useCurrentBtn.Content = "Could not detect location";
                    useCurrentBtn.IsEnabled = true;
                    return;
                }

                // Pre-fill the search box with the resolved city for confirmation
                searchBox.Text = resolved.FullName ?? resolved.Name ?? "";
                searchBox.ItemsSource = new[] { resolved };

                useCurrentBtn.Content = $"Detected: {resolved.Name} — select above to confirm";
                useCurrentBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get current location");
                useCurrentBtn.Content = "Failed to detect location";
                useCurrentBtn.IsEnabled = true;
            }
        };

        await dialog.ShowAsync();
    }

    private void ConcertCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ConcertVm concert && !string.IsNullOrEmpty(concert.Uri))
        {
            // TODO: Navigate to ConcertPage once it exists
            // NavigationHelpers.OpenConcert(concert.Uri, concert.Title ?? "Concert");
        }
    }

    private void ConcertCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
            border.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
    }

    private void ConcertCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
            border.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
    }
}
