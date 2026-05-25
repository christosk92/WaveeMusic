using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Converters;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.Controls.ActivityBell;

public sealed partial class ActivityBell : UserControl
{
    private enum ActivityFilter
    {
        All,
        System,
        Actions,
        Spotify
    }

    private readonly IActivityService? _service;
    private readonly ObservableCollection<IActivityItem> _visibleItems = new();
    private readonly INotifyPropertyChanged? _serviceNotifyPropertyChanged;
    private readonly INotifyCollectionChanged? _itemsNotifyCollectionChanged;
    private ActivityFilter _activeFilter = ActivityFilter.All;
    private bool _showBadge = true;
    private bool _subscriptionsAttached;

    public ActivityBell()
    {
        InitializeComponent();
        ActivityList.ItemsSource = _visibleItems;
        UpdateFilterButtons();

        _service = Ioc.Default.GetService<IActivityService>();
        if (_service == null) return;

        _serviceNotifyPropertyChanged = _service as INotifyPropertyChanged;
        _itemsNotifyCollectionChanged = _service.Items as INotifyCollectionChanged;
        RebuildVisibleItems();

        Loaded += ActivityBell_Loaded;
        Unloaded += ActivityBell_Unloaded;
    }

    private void UpdateBadge()
    {
        if (_service == null) return;
        var count = _service.UnreadCount;
        UnreadBadge.Value = count;
        UnreadBadge.Visibility = _showBadge && count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEmptyState()
    {
        var hasItems = _visibleItems.Count > 0;
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        ActivityList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ActivityBell_Loaded(object sender, RoutedEventArgs e)
    {
        AttachSubscriptions();
        UpdateBadge();
        UpdateEmptyState();
    }

    private void ActivityBell_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachSubscriptions();
    }

    private void AttachSubscriptions()
    {
        if (_subscriptionsAttached)
            return;

        if (_serviceNotifyPropertyChanged != null)
            _serviceNotifyPropertyChanged.PropertyChanged += OnServicePropertyChanged;

        if (_itemsNotifyCollectionChanged != null)
            _itemsNotifyCollectionChanged.CollectionChanged += OnItemsCollectionChanged;

        _subscriptionsAttached = true;
    }

    private void DetachSubscriptions()
    {
        if (!_subscriptionsAttached)
            return;

        if (_serviceNotifyPropertyChanged != null)
            _serviceNotifyPropertyChanged.PropertyChanged -= OnServicePropertyChanged;

        if (_itemsNotifyCollectionChanged != null)
            _itemsNotifyCollectionChanged.CollectionChanged -= OnItemsCollectionChanged;

        _subscriptionsAttached = false;
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IActivityService.UnreadCount))
            UpdateBadge();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildVisibleItems();
        UpdateEmptyState();
    }

    private void MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        _service?.MarkAllRead();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _service?.ClearAll();
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string tag)
            return;

        _activeFilter = tag switch
        {
            "System" => ActivityFilter.System,
            "Actions" => ActivityFilter.Actions,
            "Spotify" => ActivityFilter.Spotify,
            _ => ActivityFilter.All
        };

        UpdateFilterButtons();
        RebuildVisibleItems();
        UpdateEmptyState();
    }

    private void BadgeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _showBadge = BadgeToggleButton.IsChecked == true;
        UpdateBadge();
    }

    private void UpdateFilterButtons()
    {
        if (FilterAllButton == null)
            return;

        FilterAllButton.IsChecked = _activeFilter == ActivityFilter.All;
        FilterSystemButton.IsChecked = _activeFilter == ActivityFilter.System;
        FilterActionsButton.IsChecked = _activeFilter == ActivityFilter.Actions;
        FilterSpotifyButton.IsChecked = _activeFilter == ActivityFilter.Spotify;
    }

    private void RebuildVisibleItems()
    {
        _visibleItems.Clear();

        if (_service == null)
            return;

        foreach (var item in _service.Items.Where(MatchesActiveFilter))
            _visibleItems.Add(item);
    }

    private bool MatchesActiveFilter(IActivityItem item) => _activeFilter switch
    {
        ActivityFilter.System => item.ActivityType == ActivityNotificationType.System,
        ActivityFilter.Actions => item.ActivityType == ActivityNotificationType.UserAction,
        ActivityFilter.Spotify => item.ActivityType == ActivityNotificationType.Spotify,
        _ => true
    };

    private void ActivityItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsInsideInteractiveElement(e.OriginalSource))
            return;

        var item = (sender as FrameworkElement)?.Tag
                   ?? (sender as FrameworkElement)?.DataContext;

        if (TryActivateActivityItem(item))
        {
            BellButton.Flyout?.Hide();
            e.Handled = true;
            return;
        }

        if (item is NotificationActivityItem notification && notification.HasDetailContent)
        {
            e.Handled = true;
            _ = ShowActivityDetailsAsync(notification);
        }
    }

    private void ActivityList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (TryActivateActivityItem(e.ClickedItem))
        {
            BellButton.Flyout?.Hide();
            return;
        }

        if (e.ClickedItem is NotificationActivityItem item && item.HasDetailContent)
            _ = ShowActivityDetailsAsync(item);
    }

    private async void ActivityDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        var item = button.CommandParameter as NotificationActivityItem
                   ?? button.Tag as NotificationActivityItem
                   ?? button.DataContext as NotificationActivityItem;

        if (item is not null && item.HasDetailContent)
            await ShowActivityDetailsAsync(item);
    }

    private async Task ShowActivityDetailsAsync(NotificationActivityItem item)
    {
        var content = BuildDetailsContent(item);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = item.DetailTitle ?? item.Title,
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private static bool TryActivateActivityItem(object? clickedItem)
    {
        return clickedItem switch
        {
            NotificationActivityItem item => TryActivateNavigationUri(
                item.NavigationUri,
                ResolveNavigationTitle(item),
                item.DetailSubtitle ?? item.Message,
                item.ImageUrl),
            SpotifyActivityItem item => TryActivateNavigationUri(
                item.NavigationUri,
                item.Title,
                item.Message,
                item.ImageUrl),
            _ => false
        };
    }

    private static bool TryActivateNavigationUri(string? uri, string? title, string? subtitle, string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return false;

        try
        {
            var openInNewTab = NavigationHelpers.IsCtrlPressed();
            if (IsTrackUri(uri))
            {
                var playback = Ioc.Default.GetService<IPlaybackStateService>();
                if (playback is null)
                    return false;

                playback.PlayTrack(uri);
                return true;
            }

            if (uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
            {
                NavigationHelpers.PlayEpisode(uri);
                return true;
            }

            if (IsAlbumUri(uri))
            {
                NavigationHelpers.OpenAlbum(uri, NonEmpty(title, "Album"), openInNewTab);
                return true;
            }

            if (IsArtistUri(uri))
            {
                NavigationHelpers.OpenArtist(uri, NonEmpty(title, "Artist"), openInNewTab);
                return true;
            }

            if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
            {
                NavigationHelpers.OpenPlaylist(uri, NonEmpty(title, "Playlist"), openInNewTab);
                return true;
            }

            if (uri.StartsWith("spotify:show:", StringComparison.Ordinal))
            {
                NavigationHelpers.OpenShowPage(uri, title, subtitle, imageUrl, openInNewTab);
                return true;
            }

            if (uri.StartsWith("spotify:page:", StringComparison.Ordinal))
            {
                NavigationHelpers.OpenBrowsePage(
                    new ContentNavigationParameter
                    {
                        Uri = uri,
                        Title = title,
                        Subtitle = subtitle,
                        ImageUrl = imageUrl
                    },
                    openInNewTab);
                return true;
            }

            if (uri.StartsWith("spotify:user:", StringComparison.Ordinal))
            {
                NavigationHelpers.OpenProfile(
                    new ContentNavigationParameter
                    {
                        Uri = uri,
                        Title = title,
                        Subtitle = subtitle,
                        ImageUrl = imageUrl
                    },
                    title,
                    openInNewTab);
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Activity activation failed: {ex.Message}");
            Ioc.Default.GetService<INotificationService>()?
                .Show("Couldn't open this activity.", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }

        return false;
    }

    private static bool IsTrackUri(string uri) =>
        uri.StartsWith("spotify:track:", StringComparison.Ordinal)
        || uri.StartsWith("wavee:local:track:", StringComparison.Ordinal);

    private static bool IsAlbumUri(string uri) =>
        uri.StartsWith("spotify:album:", StringComparison.Ordinal)
        || uri.StartsWith("wavee:local:album:", StringComparison.Ordinal);

    private static bool IsArtistUri(string uri) =>
        uri.StartsWith("spotify:artist:", StringComparison.Ordinal)
        || uri.StartsWith("wavee:local:artist:", StringComparison.Ordinal);

    private static string ResolveNavigationTitle(NotificationActivityItem item) =>
        NonEmpty(
            FindDetailValue(item, "Item")
            ?? FindDetailValue(item, "Playlist")
            ?? FindDetailValue(item, "Album")
            ?? FindDetailValue(item, "Artist")
            ?? item.DetailTitle
            ?? item.Title,
            "Item");

    private static string? FindDetailValue(NotificationActivityItem item, string label) =>
        item.DetailRows?.FirstOrDefault(row => string.Equals(row.Label, label, StringComparison.OrdinalIgnoreCase)).Value;

    private static string NonEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static ScrollViewer BuildDetailsContent(NotificationActivityItem item)
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 460
        };

        var header = new Grid
        {
            ColumnSpacing = 12
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var art = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(6),
            Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"]
        };

        var imageSource = new SpotifyImageConverter()
            .Convert(item.ImageUrl ?? string.Empty, typeof(ImageSource), "128", string.Empty) as ImageSource;

        if (imageSource is not null)
        {
            art.Child = new Image
            {
                Source = imageSource,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill
            };
        }
        else
        {
            art.Child = new FontIcon
            {
                Glyph = item.IconGlyph ?? "\uE8D6",
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        header.Children.Add(art);

        var headerText = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(headerText, 1);
        headerText.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(item.DetailSubtitle ?? item.Message))
        {
            headerText.Children.Add(new TextBlock
            {
                Text = item.DetailSubtitle ?? item.Message,
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });
        }
        header.Children.Add(headerText);
        panel.Children.Add(header);

        if (!string.IsNullOrWhiteSpace(item.DetailBody))
        {
            panel.Children.Add(new TextBlock
            {
                Text = item.DetailBody,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (item.DetailRows is { Count: > 0 })
        {
            foreach (var row in item.DetailRows)
            {
                var grid = new Grid { ColumnSpacing = 12 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                grid.Children.Add(new TextBlock
                {
                    Text = row.Label,
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"]
                });

                var value = new TextBlock
                {
                    Text = row.Value,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(value, 1);
                grid.Children.Add(value);

                panel.Children.Add(grid);
            }
        }

        return new ScrollViewer
        {
            Content = panel,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private async void ActivityAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn)
            return;

        var action = btn.CommandParameter as ActivityAction
                     ?? btn.Tag as ActivityAction
                     ?? btn.DataContext as ActivityAction;
        if (action is null)
        {
            System.Diagnostics.Debug.WriteLine("Activity action click had no ActivityAction payload.");
            Ioc.Default.GetService<INotificationService>()?
                .Show("Couldn't run this activity action.", NotificationSeverity.Warning, TimeSpan.FromSeconds(3));
            return;
        }

        try
        {
            btn.IsEnabled = false;
            await action.Callback();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Activity action failed: {ex.Message}");
            Ioc.Default.GetService<INotificationService>()?
                .Show(ex.Message, NotificationSeverity.Error, TimeSpan.FromSeconds(4));
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private static bool IsInsideInteractiveElement(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is ButtonBase or HyperlinkButton)
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
