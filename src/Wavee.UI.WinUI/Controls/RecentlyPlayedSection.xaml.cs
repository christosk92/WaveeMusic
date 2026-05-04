using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls;

public sealed partial class RecentlyPlayedSection : UserControl
{
    private RecentlyPlayedService? _service;
    private bool _isSubscribed;

    public RecentlyPlayedSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _service = Ioc.Default.GetService<RecentlyPlayedService>();
        if (_service == null) return;

        // Set up the item template selector (same logic as Home page)
        ItemsHost.ItemTemplate = new RecentlyPlayedItemTemplateSelector
        {
            ArtistTemplate = (DataTemplate)Resources["ArtistCardTemplate"],
            DefaultTemplate = (DataTemplate)Resources["DefaultCardTemplate"],
            LikedSongsRecentTemplate = (DataTemplate)Resources["LikedSongsRecentTemplate"],
            EpisodeTemplate = (DataTemplate)Resources["EpisodeCardTemplate"]
        };

        // If already loaded, show immediately
        if (_service.Items.Count > 0)
        {
            ShowItems();
        }

        // Subscribe to updates. Items now arrive from the Home GraphQL parse
        // pipeline (HomeViewModel → ApplyHomeRecents) — no separate fetch
        // kicked off here. The carousel stays collapsed until the first Home
        // load lands; then ItemsChanged fires and ShowItems takes over.
        if (!_isSubscribed)
        {
            _service.ItemsChanged += OnItemsChanged;
            _isSubscribed = true;
        }
    }

    private void OnItemsChanged()
    {
        DispatcherQueue.TryEnqueue(ShowItems);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_service != null && _isSubscribed)
        {
            _service.ItemsChanged -= OnItemsChanged;
            _isSubscribed = false;
        }
    }

    private void ShowItems()
    {
        if (_service == null || _service.Items.Count == 0)
        {
            RootPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ItemsHost.ItemsSource = _service.Items;
        RootPanel.Visibility = Visibility.Visible;
    }

    private sealed class RecentlyPlayedItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? ArtistTemplate { get; set; }
        public DataTemplate? DefaultTemplate { get; set; }
        public DataTemplate? LikedSongsRecentTemplate { get; set; }
        public DataTemplate? EpisodeTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            if (item is HomeSectionItem hsi)
            {
                // Liked Songs (Recents-saved variant) gets the stack-of-3 + "N
                // songs added" render; falls through to DefaultTemplate for the
                // legacy Liked-Songs-as-played case (no group_metadata).
                if (hsi.IsRecentlySaved
                    && hsi.Uri != null
                    && hsi.Uri.Contains(":collection", System.StringComparison.OrdinalIgnoreCase)
                    && LikedSongsRecentTemplate != null)
                    return LikedSongsRecentTemplate;
                if (hsi.ContentType == HomeContentType.Episode && EpisodeTemplate != null)
                    return EpisodeTemplate;
                if (hsi.ContentType == HomeContentType.Artist)
                    return ArtistTemplate ?? DefaultTemplate!;
            }
            return DefaultTemplate!;
        }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
            => SelectTemplateCore(item);
    }
}
