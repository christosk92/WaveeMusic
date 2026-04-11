using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

public sealed partial class HomePage : Page, ITabBarItemContent, IDisposable
{
    private readonly ILogger? _logger;
    private readonly HomeFeedCache? _cache;
    private bool _isShimmerContentReleased;
    private bool _isDisposed;

    public HomeViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public HomePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<HomeViewModel>();
        _logger = Ioc.Default.GetService<ILogger<HomePage>>();
        _cache = Ioc.Default.GetService<HomeFeedCache>();
        InitializeComponent();

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

        // Deferred setup — moved from constructor so InitializeComponent returns faster
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;

        SectionsRepeater.ItemTemplate = new HomeSectionTemplateSelector
        {
            ShortsTemplate = (DataTemplate)Resources["ShortsSectionTemplate"],
            GenericTemplate = (DataTemplate)Resources["GenericSectionTemplate"],
            BaselineTemplate = (DataTemplate)Resources["BaselineSectionTemplate"]
        };

        WeakReferenceMessenger.Default.Register<AuthStatusChangedMessage>(this, (r, m) =>
        {
            if (m.Value == AuthStatus.Authenticated)
                DispatcherQueue.TryEnqueue(() => _ = ViewModel.LoadCommand.ExecuteAsync(null));
        });

        if (_cache != null)
            _cache.DataRefreshed += OnCacheDataRefreshed;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

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
        if (_isShimmerContentReleased || ShimmerContainer?.Content == null)
            return;

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
        if (_showingContent && ShimmerContainer != null)
        {
            ShimmerContainer.Visibility = Visibility.Collapsed;
            if (!_isShimmerContentReleased)
            {
                // The first-load skeleton is one of the heaviest retained subtrees on Home.
                // Release it after content has loaded so a cached Home page doesn't keep the
                // entire shimmer visual tree resident for the rest of the session.
                ShimmerContainer.Content = null;
                _isShimmerContentReleased = true;
            }
        }
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        CleanupSubscriptions();
    }

    public void RefreshWithParameter(object? parameter)
    {
        // HomePage has no parameter — a refresh just reloads the feed if stale
        if (_cache is { IsStale: true })
            _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Loaded -= HomePage_Loaded;
        Unloaded -= HomePage_Unloaded;
        CleanupSubscriptions();
        (ViewModel as IDisposable)?.Dispose();
    }

    private void CleanupSubscriptions()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        WeakReferenceMessenger.Default.Unregister<AuthStatusChangedMessage>(this);
        if (_cache != null)
            _cache.DataRefreshed -= OnCacheDataRefreshed;
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

    private async void HomeSectionDebugButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            await ShowHomeDebugTextDialog(
                "Home Section Debug",
                "The debug button did not have a HomeSection attached.");
            return;
        }

        var section = element.Tag as HomeSection ?? element.DataContext as HomeSection;
        if (section == null)
        {
            await ShowHomeDebugTextDialog(
                "Home Section Debug",
                "The debug button did not have a HomeSection attached.");
            return;
        }

        try
        {
            await ShowHomeSectionDebugDialog(section);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeSectionDebug] Failed to show dialog: {ex}");
            await ShowHomeDebugTextDialog("Home Section Debug Error", ex.ToString());
        }
    }

    private static readonly JsonSerializerOptions HomeDebugJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private async Task ShowHomeSectionDebugDialog(HomeSection section)
    {
        var pivot = new Pivot
        {
            MaxWidth = 860
        };

        pivot.Items.Add(new PivotItem
        {
            Header = "Raw Spotify",
            Content = CreateJsonDebugViewer(BuildRawSectionDebugJson(section))
        });

        pivot.Items.Add(new PivotItem
        {
            Header = "ViewModel",
            Content = CreateJsonDebugViewer(BuildViewModelDebugJson(section))
        });

        var dialog = new ContentDialog
        {
            Title = $"Home Section Debug: {section.Title ?? section.SectionUri}",
            Content = pivot,
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
            MaxWidth = 900
        };

        await dialog.ShowAsync();
    }

    private static ScrollViewer CreateJsonDebugViewer(string json)
    {
        return new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = json,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas"),
                FontSize = 11,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.NoWrap
            },
            MaxHeight = 520,
            Padding = new Thickness(12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private async Task ShowHomeDebugTextDialog(string title, string text)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = text,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas"),
                    FontSize = 11,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                },
                MaxHeight = 500
            },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private static string BuildRawSectionDebugJson(HomeSection section)
    {
        if (string.IsNullOrWhiteSpace(section.RawSpotifyJson))
        {
            return JsonSerializer.Serialize(new
            {
                message = "No raw Spotify section JSON is attached to this rendered section.",
                title = section.Title,
                sectionUri = section.SectionUri,
                sectionType = section.SectionType.ToString(),
                itemCount = section.Items.Count
            }, HomeDebugJsonOptions);
        }

        return PrettyPrintJson(section.RawSpotifyJson);
    }

    private static string BuildViewModelDebugJson(HomeSection section)
    {
        var viewModel = new
        {
            title = section.Title,
            subtitle = section.Subtitle,
            sectionType = section.SectionType.ToString(),
            sectionUri = section.SectionUri,
            headerEntityName = section.HeaderEntityName,
            headerEntityImageUrl = section.HeaderEntityImageUrl,
            headerEntityUri = section.HeaderEntityUri,
            itemCount = section.Items.Count,
            items = section.Items.Select((item, index) => new
            {
                index,
                uri = item.Uri,
                title = item.Title,
                subtitle = item.Subtitle,
                imageUrl = item.ImageUrl,
                contentType = item.ContentType.ToString(),
                colorHex = item.ColorHex,
                placeholderGlyph = item.PlaceholderGlyph,
                isBaselineLoading = item.IsBaselineLoading,
                hasBaselinePreview = item.HasBaselinePreview,
                heroImageUrl = item.HeroImageUrl,
                heroColorHex = item.HeroColorHex,
                canvasUrl = item.CanvasUrl,
                canvasThumbnailUrl = item.CanvasThumbnailUrl,
                audioPreviewUrl = item.AudioPreviewUrl,
                baselineGroupTitle = item.BaselineGroupTitle,
                previewTracks = item.PreviewTracks.Select(track => new
                {
                    uri = track.Uri,
                    name = track.Name,
                    coverArtUrl = track.CoverArtUrl,
                    colorHex = track.ColorHex,
                    canvasUrl = track.CanvasUrl,
                    canvasThumbnailUrl = track.CanvasThumbnailUrl,
                    audioPreviewUrl = track.AudioPreviewUrl
                }).ToList()
            }).ToList()
        };

        return JsonSerializer.Serialize(viewModel, HomeDebugJsonOptions);
    }

    private static string PrettyPrintJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, HomeDebugJsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    // ── Customize flyout handlers ──

    private void SectionTitle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string uri }) return;

        // Close the flyout
       // CustomizeFlyout.Hide();

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
