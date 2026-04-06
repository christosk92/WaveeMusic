using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Controls.Cards;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class HomePage : Page, ITabBarItemContent
{
    private readonly ILogger? _logger;
    private readonly HomeFeedCache? _cache;

    public HomeViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public HomePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<HomeViewModel>();
        _logger = Ioc.Default.GetService<ILogger<HomePage>>();
        _cache = Ioc.Default.GetService<HomeFeedCache>();
        InitializeComponent();

        // Hide content initially via composition visual (not XAML Opacity — they multiply)
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;

        // Set up template selector for the sections repeater
        SectionsRepeater.ItemTemplate = new HomeSectionTemplateSelector
        {
            ShortsTemplate = (DataTemplate)Resources["ShortsSectionTemplate"],
            GenericTemplate = (DataTemplate)Resources["GenericSectionTemplate"],
            BaselineTemplate = (DataTemplate)Resources["BaselineSectionTemplate"]
        };

        // Re-trigger load when auth completes (session may not be ready at page load).
        // Message fires on background thread, so dispatch to UI thread.
        WeakReferenceMessenger.Default.Register<AuthStatusChangedMessage>(this, (r, m) =>
        {
            if (m.Value == AuthStatus.Authenticated)
                DispatcherQueue.TryEnqueue(() => _ = ViewModel.LoadCommand.ExecuteAsync(null));
        });

        // Subscribe to background cache refreshes — apply diffs on UI thread
        if (_cache != null)
            _cache.DataRefreshed += OnCacheDataRefreshed;

        // Crossfade from shimmer to content when loading completes
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
    }

    private bool _showingContent;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsLoading))
        {
            if (ViewModel.IsLoading && _showingContent)
            {
                // Loading started (e.g. refresh) — show shimmer again
                ShowShimmer();
            }
        }

        if (e.PropertyName is nameof(ViewModel.IsLoading) or nameof(ViewModel.Sections))
        {
            if (!ViewModel.IsLoading && ViewModel.Sections.Count > 0 && !_showingContent)
            {
                CrossfadeToContent();
            }
        }
    }

    private void OnCacheDataRefreshed(HomeFeedSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() => ViewModel.ApplyBackgroundRefresh(snapshot));
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= HomePage_Loaded;
        try
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unhandled error in HomePage Loaded handler");
        }
    }

    private void ShowShimmer()
    {
        _showingContent = false;
        ShimmerContainer.Visibility = Visibility.Visible;

        // Fade in shimmer, fade out content
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(200))
            .Start(ShimmerContainer);

        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200))
            .Start(ContentContainer);
    }

    private void CrossfadeToContent()
    {
        _showingContent = true;

        // Fade out shimmer
        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200))
            .Start(ShimmerContainer);

        // Fade in content
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300),
                     delay: TimeSpan.FromMilliseconds(100))
            .Start(ContentContainer);

        // Collapse shimmer after animation
        _ = CollapseShimmerAfterDelay();
    }

    private const int ShimmerCollapseDelayMs = 500;

    private async Task CollapseShimmerAfterDelay()
    {
        await Task.Delay(ShimmerCollapseDelayMs);
        if (_showingContent)
            ShimmerContainer.Visibility = Visibility.Collapsed;
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        WeakReferenceMessenger.Default.Unregister<AuthStatusChangedMessage>(this);
        if (_cache != null)
            _cache.DataRefreshed -= OnCacheDataRefreshed;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
    }

    // ── Card click handlers (used by both ContentCard and baseline buttons) ──

    private void ContentCard_Click(object sender, EventArgs e)
    {
        if (sender is ContentCard { DataContext: HomeSectionItem item })
            HomeViewModel.NavigateToItem(item, NavigationHelpers.IsCtrlPressed());
    }

    private void ContentCard_MiddleClick(object sender, EventArgs e)
    {
        if (sender is ContentCard { DataContext: HomeSectionItem item })
            HomeViewModel.NavigateToItem(item, openInNewTab: true);
    }

    private void ContentCard_RightTapped(ContentCard sender, RightTappedRoutedEventArgs e)
    {
        if (sender.DataContext is not HomeSectionItem item) return;

        var menu = new MenuFlyout();
        var openNewTab = new MenuFlyoutItem
        {
            Text = "Open in new tab",
            Icon = new SymbolIcon(Symbol.OpenWith)
        };
        openNewTab.Click += (_, _) => HomeViewModel.NavigateToItem(item, openInNewTab: true);
        menu.Items.Add(openNewTab);
        menu.ShowAt(sender, e.GetPosition(sender));
    }

    // Baseline section still uses buttons directly
    private void GenericItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HomeSectionItem item })
            HomeViewModel.NavigateToItem(item, NavigationHelpers.IsCtrlPressed());
    }

    private void GenericItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed
            && sender is Button { DataContext: HomeSectionItem item })
            HomeViewModel.NavigateToItem(item, openInNewTab: true);
    }

    // ── Customize flyout handlers ──

    private void SectionTitle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string uri }) return;

        // Close the flyout
        CustomizeFlyout.Hide();

        // Find the section index in the displayed Sections collection
        var sectionIndex = -1;
        for (int i = 0; i < ViewModel.Sections.Count; i++)
        {
            if (ViewModel.Sections[i].SectionUri == uri)
            {
                sectionIndex = i;
                break;
            }
        }

        if (sectionIndex < 0) return;

        // Get the element from the ItemsRepeater and scroll to it
        var element = SectionsRepeater.TryGetElement(sectionIndex);
        if (element is FrameworkElement fe)
        {
            // Scroll into view
            fe.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                VerticalAlignmentRatio = 0.0 // Align to top
            });

            // Brief highlight animation — flash the background
            HighlightSection(fe);
        }
    }

    private const int HighlightBlinkDelayMs = 120;

    private static async void HighlightSection(FrameworkElement element)
    {
        // Store original opacity, flash it
        var original = element.Opacity;
        for (int i = 0; i < 3; i++)
        {
            element.Opacity = 0.5;
            await Task.Delay(HighlightBlinkDelayMs);
            element.Opacity = original;
            await Task.Delay(HighlightBlinkDelayMs);
        }
    }

    private void VisibilityCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string uri)
            ViewModel.SetSectionVisibility(uri, cb.IsChecked == true);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uri })
            ViewModel.MoveSectionUpCommand.Execute(uri);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uri })
            ViewModel.MoveSectionDownCommand.Execute(uri);
    }

    // ── Chip click handler ──

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        var chip = (sender as FrameworkElement)?.Tag as HomeChipViewModel;
        System.Diagnostics.Debug.WriteLine($"[Chip_Click] sender={sender?.GetType().Name}, chip={chip?.Label ?? "null"}");
        if (chip != null)
            _ = ViewModel.SelectChipCommand.ExecuteAsync(chip);
    }
}

/// <summary>
/// Selects the appropriate DataTemplate for each home section type.
/// </summary>
public sealed class HomeSectionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ShortsTemplate { get; set; }
    public DataTemplate? GenericTemplate { get; set; }
    public DataTemplate? BaselineTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        if (item is HomeSection section)
        {
            return section.SectionType switch
            {
                HomeSectionType.Shorts => ShortsTemplate ?? GenericTemplate!,
                HomeSectionType.Baseline => BaselineTemplate ?? GenericTemplate!,
                _ => GenericTemplate!
            };
        }
        return GenericTemplate!;
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}

/// <summary>
/// Selects per-item card template based on content type (artist = circle, everything else = square).
/// </summary>
public sealed class HomeItemTemplateSelector : DataTemplateSelector
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
