using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.AlbumDetailPanel;
using Wavee.UI.WinUI.Controls.TabBar;
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

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArtistViewModel.IsLoading))
        {
            if (!ViewModel.IsLoading && !_showingContent)
            {
                // Defer slightly to let reactive bindings populate collections
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    CrossfadeToContent);
            }
        }
    }

    private async void CrossfadeToContent()
    {
        if (_showingContent) return;
        _showingContent = true;

        // Start both simultaneously — content fades in AS shimmer fades out
        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(250),
                     layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(ShimmerContainer);

        ContentContainer.Opacity = 1;
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(250),
                     layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(ContentContainer);

        // Collapse shimmer after animation completes
        await Task.Delay(300);
        if (_showingContent)
            ShimmerContainer.Visibility = Visibility.Collapsed;
    }

    private void ArtistPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        SizeChanged -= OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Hero height = 45% of page height (min 300)
        if (HeroGrid != null)
            HeroGrid.Height = Math.Max(300, e.NewSize.Height * 0.45);
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

    // Prefetched extracted colors: imageUrl -> hex
    private readonly Dictionary<string, string> _colorCache = new();

    private void AlbumCard_Click(object sender, EventArgs e)
    {
        // sender is the ContentCard. Walk up to find the ItemsRepeater,
        // then use GetElementIndex to find the data item.
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
        if (repeater == null) return;

        // Collapse any existing expansion first
        CollapseExpandedAlbum();

        // If clicking the same album that was expanded, just collapse (toggle)
        if (ViewModel.ExpandedAlbum?.Id == item.Id)
        {
            ViewModel.CollapseAlbumCommand.Execute(null);
            return;
        }

        // Find the parent StackPanel and the repeater's index in it
        var parentPanel = repeater.Parent as StackPanel;
        if (parentPanel == null) return;

        var repeaterIndex = parentPanel.Children.IndexOf(repeater);
        if (repeaterIndex < 0) return;

        // Determine row info from the layout
        var layout = repeater.Layout as UniformGridLayout;
        var allItems = repeater.ItemsSource as System.Collections.IList;
        if (allItems == null) return;

        var itemIndex = allItems.IndexOf(item);
        if (itemIndex < 0) return;

        // Calculate columns from layout
        var availableWidth = repeater.ActualWidth;
        var minWidth = layout?.MinItemWidth ?? 160;
        var spacing = layout?.MinColumnSpacing ?? 12;
        var columns = Math.Max(1, (int)Math.Floor((availableWidth + spacing) / (minWidth + spacing)));

        // Find the split point: end of the clicked item's row
        var rowOfItem = itemIndex / columns;
        var splitAfterIndex = Math.Min((rowOfItem + 1) * columns, allItems.Count);

        // Split the items
        var itemsBefore = new System.Collections.Generic.List<object>();
        var itemsAfter = new System.Collections.Generic.List<object>();
        for (int i = 0; i < allItems.Count; i++)
        {
            if (i < splitAfterIndex)
                itemsBefore.Add(allItems[i]!);
            else
                itemsAfter.Add(allItems[i]!);
        }

        // Save original state for restore
        _originalRepeater = repeater;
        _originalItemsSource = repeater.ItemsSource;
        _splitParent = parentPanel;

        // Set the first repeater to show only items before the split
        repeater.ItemsSource = itemsBefore;

        // Create the detail panel
        _activeDetailPanel = new AlbumDetailPanel();
        _activeDetailPanel.Album = item.Data;
        _activeDetailPanel.Tracks = ViewModel.ExpandedAlbumTracks;
        _activeDetailPanel.CloseRequested += (_, _) => CollapseExpandedAlbum();

        // Position triangle notch under the clicked card's center
        var columnIndex = itemIndex % columns;
        var cellWidth = (availableWidth - (columns - 1) * spacing) / columns;
        var notchX = columnIndex * (cellWidth + spacing) + cellWidth / 2;
        _activeDetailPanel.NotchOffsetX = notchX;

        // Fetch extracted color for album art gradient (uses cache if available)
        _ = FetchAlbumColorAsync(item.Data, _activeDetailPanel);

        // Create a second repeater for items after the split
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
            ItemTemplate = repeater.ItemTemplate,
            ItemsSource = itemsAfter
        };

        // Insert detail panel + second repeater after the first repeater
        _splitInsertIndex = repeaterIndex + 1;
        parentPanel.Children.Insert(_splitInsertIndex, _activeDetailPanel);
        if (itemsAfter.Count > 0)
            parentPanel.Children.Insert(_splitInsertIndex + 1, _splitRepeaterAfter);

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

    private async Task FetchAlbumColorAsync(ArtistReleaseVm album, AlbumDetailPanel panel)
    {
        if (string.IsNullOrEmpty(album.ImageUrl)) return;

        var imageUrl = SpotifyImageHelper.ToHttpsUrl(album.ImageUrl);
        if (string.IsNullOrEmpty(imageUrl)) return;

        // Use cached color if available (prefetched on hover)
        if (_colorCache.TryGetValue(imageUrl, out var cached))
        {
            panel.ColorHex = cached;
            return;
        }

        var hex = await FetchExtractedColorHexAsync(imageUrl);
        if (!string.IsNullOrEmpty(hex))
        {
            panel.ColorHex = hex;
        }
    }

    /// <summary>
    /// Prefetch color on card hover so it's instant when the user clicks.
    /// </summary>
    private void AlbumCard_Hover(object sender, EventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        // Walk up to find the item
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
        if (string.IsNullOrEmpty(imageUrl) || _colorCache.ContainsKey(imageUrl)) return;

        // Fire-and-forget prefetch
        _ = PrefetchColorAsync(imageUrl);
    }

    private async Task PrefetchColorAsync(string imageUrl)
    {
        var hex = await FetchExtractedColorHexAsync(imageUrl);
        // Result is cached inside FetchExtractedColorHexAsync
    }

    private async Task<string?> FetchExtractedColorHexAsync(string imageUrl)
    {
        try
        {
            var session = Ioc.Default.GetService<Wavee.Core.Session.ISession>();
            if (session == null || !session.IsConnected()) return null;

            var response = await session.Pathfinder.GetExtractedColorsAsync([imageUrl]);
            var entry = response.Data?.ExtractedColors?.FirstOrDefault();
            if (entry == null) return null;

            var isDark = ActualTheme == ElementTheme.Dark;
            var hex = isDark
                ? entry.ColorDark?.Hex ?? entry.ColorRaw?.Hex
                : entry.ColorLight?.Hex ?? entry.ColorRaw?.Hex;

            if (!string.IsNullOrEmpty(hex))
            {
                _colorCache[imageUrl] = hex;
            }

            return hex;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch extracted color");
            return null;
        }
    }
}
