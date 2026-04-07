using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls;

public sealed partial class RecentlyPlayedSection : UserControl
{
    private RecentlyPlayedService? _service;

    public RecentlyPlayedSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _service = Ioc.Default.GetService<RecentlyPlayedService>();
        if (_service == null) return;

        // Set up the item template selector (same logic as Home page)
        ItemsHost.ItemTemplate = new RecentlyPlayedItemTemplateSelector
        {
            ArtistTemplate = (DataTemplate)Resources["ArtistCardTemplate"],
            DefaultTemplate = (DataTemplate)Resources["DefaultCardTemplate"]
        };

        // If already loaded, show immediately
        if (_service.Items.Count > 0)
        {
            ShowItems();
        }

        // Subscribe to updates
        _service.ItemsChanged += OnItemsChanged;

        // Trigger load if empty
        if (_service.Items.Count == 0)
        {
            try
            {
                await _service.LoadAsync();
            }
            catch
            {
                // Non-critical — silently fail
            }
        }
    }

    private void OnItemsChanged()
    {
        DispatcherQueue.TryEnqueue(ShowItems);
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

        protected override DataTemplate SelectTemplateCore(object item)
        {
            if (item is HomeSectionItem { ContentType: HomeContentType.Artist })
                return ArtistTemplate ?? DefaultTemplate!;
            return DefaultTemplate!;
        }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
            => SelectTemplateCore(item);
    }
}
