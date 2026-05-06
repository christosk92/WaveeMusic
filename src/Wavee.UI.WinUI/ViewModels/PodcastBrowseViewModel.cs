using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.Controls.HeroCarousel;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class PodcastBrowseViewModel : ObservableObject, IDisposable
{
    public const string RootPodcastsUri = "spotify:page:0JQ5DArNBzkmxXHCqFLx2J";
    public const string AllCategoriesUri = "spotify:page:0JQ5DArNBzkmxXHCqFLx2U";
    public const string PodcastChartsUri = "spotify:page:0JQ5DAB3zgCauRwnvdEQjJ";

    private static readonly IReadOnlyList<EditorialPrefetchSource> EditorialPrefetchSources =
    [
        new("Games", "spotify:page:0JQ5DAqbMKFHAsyQVXtkEA"),
        new("Health", "spotify:page:0JQ5DAqbMKFxGfN2v0xfBG"),
        new("Comedy", "spotify:page:0JQ5DAqbMKFNr6gDrHHVKL"),
        new("True Crime", "spotify:page:0JQ5DAqbMKFJxB6x6hfvv0"),
        new("Business & Technology", "spotify:page:0JQ5DAqbMKFQRiNGmKYj3B"),
        new("Educational", "spotify:page:0JQ5DAqbMKFEKYLBUxreJF")
    ];

    private readonly IPodcastService _podcastService;
    private readonly IColorService? _colorService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private readonly ILogger? _logger;
    private readonly Dictionary<string, PodcastBrowsePageDto> _pageCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PodcastBrowseSectionDto> _sectionCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PodcastBrowseCategoryPath> _categoryPaths = new(StringComparer.Ordinal);
    private CancellationTokenSource? _loadCts;
    private string _currentUri = RootPodcastsUri;
    private bool _disposed;

    public event EventHandler? HeroColorsResolved;

    public ObservableCollection<PodcastBrowseItemViewModel> HeroShows { get; } = [];
    public ObservableCollection<PodcastBrowseItemViewModel> HeroSideShows { get; } = [];
    public ObservableCollection<PodcastBrowseItemViewModel> FeaturedCategories { get; } = [];
    public ObservableCollection<PodcastBrowseSectionViewModel> EditorialShelves { get; } = [];
    public ObservableCollection<PodcastBrowseSectionViewModel> CategoryGroups { get; } = [];
    /// <summary>
    /// The master list of every podcast genre, sourced once from the AllCategories
    /// browse page and reused on every PodcastBrowsePage instance for the Zune-style
    /// left-column genre nav. Independent from <see cref="CategoryGroups"/> (which
    /// holds whatever category sections the *current* page itself returned, and may
    /// be empty on a sub-page).
    /// </summary>
    public ObservableCollection<PodcastBrowseSectionViewModel> AllPodcastCategories { get; } = [];
    public ObservableCollection<PodcastBrowseBreadcrumbItem> BreadcrumbItems { get; } = [];

    public string CurrentUri => _currentUri;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContent))]
    [NotifyPropertyChangedFor(nameof(ShowHeroShows))]
    [NotifyPropertyChangedFor(nameof(ShowHeroCarousel))]
    [NotifyPropertyChangedFor(nameof(ShowInitialLoading))]
    [NotifyPropertyChangedFor(nameof(ShowNoContentMessage))]
    [NotifyPropertyChangedFor(nameof(ShowSidebarShimmer))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContent))]
    [NotifyPropertyChangedFor(nameof(ShowNoContentMessage))]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _title = "Podcasts";

    [ObservableProperty]
    private string _subtitle = "Browse shows, charts, and categories.";

    [ObservableProperty]
    private string _searchPlaceholder = "Search podcasts";

    [ObservableProperty]
    private Brush? _headerColorBrush;

    [ObservableProperty]
    private Brush? _headerTintBrush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedHeroImageUrl))]
    [NotifyPropertyChangedFor(nameof(SelectedHeroIndex))]
    private PodcastBrowseItemViewModel? _selectedHero;

    public string? SelectedHeroImageUrl => SelectedHero?.ImageUrl;
    public int SelectedHeroIndex => SelectedHero is null ? 0 : Math.Max(0, HeroShows.IndexOf(SelectedHero));
    public int HeroCount => HeroShows.Count;
    public bool HasHeroShows => HeroShows.Count > 0;
    public bool HasHeroSideShows => HeroSideShows.Count > 0;
    public PodcastBrowseItemViewModel? HeroSidePrimary => HeroSideShows.Count > 0 ? HeroSideShows[0] : null;
    public PodcastBrowseItemViewModel? HeroSideSecondaryOne => HeroSideShows.Count > 1 ? HeroSideShows[1] : null;
    public PodcastBrowseItemViewModel? HeroSideSecondaryTwo => HeroSideShows.Count > 2 ? HeroSideShows[2] : null;
    public bool HasHeroSidePrimary => HeroSidePrimary is not null;
    public bool HasHeroSideSecondaryOne => HeroSideSecondaryOne is not null;
    public bool HasHeroSideSecondaryTwo => HeroSideSecondaryTwo is not null;
    public bool ShowHeroShows => !IsLoading && HasHeroShows;
    public bool ShowHeroCarousel => IsLoading || HasHeroShows;
    public bool HasFeaturedCategories => FeaturedCategories.Count > 0;
    public bool HasEditorialShelves => EditorialShelves.Count > 0;
    public bool HasCategoryGroups => CategoryGroups.Count > 0;
    public bool HasAllPodcastCategories => AllPodcastCategories.Count > 0;
    public bool HasContent => HasHeroShows || HasEditorialShelves;
    public bool ShowInitialLoading => IsLoading && !HasContent;
    public bool ShowNoContentMessage => !IsLoading && !HasError && !HasContent;
    public bool ShowBreadcrumbs => BreadcrumbItems.Count > 0;
    public bool ShowSidebar => HasAllPodcastCategories;
    public bool ShowSidebarShimmer => IsLoading && !HasAllPodcastCategories;

    public PodcastBrowseViewModel(
        IPodcastService podcastService,
        IColorService? colorService = null,
        ILogger<PodcastBrowseViewModel>? logger = null)
    {
        _podcastService = podcastService ?? throw new ArgumentNullException(nameof(podcastService));
        _colorService = colorService;
        _logger = logger;
        SetHeaderColor("#27856A");
    }

    public async Task LoadAsync(ContentNavigationParameter? parameter)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        var uri = NormalizeBrowseUri(parameter?.Uri);
        if (string.IsNullOrWhiteSpace(uri))
            uri = RootPodcastsUri;

        _currentUri = uri;
        OnPropertyChanged(nameof(CurrentUri));
        UpdateSelectedCategoryStates();
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;
        ClearContent();

        try
        {
            if (string.Equals(uri, RootPodcastsUri, StringComparison.Ordinal))
            {
                await LoadRootAsync(ct);
            }
            else if (uri.StartsWith("spotify:section:", StringComparison.Ordinal))
            {
                await LoadSectionPageAsync(uri, parameter?.Title, ct);
            }
            else
            {
                await LoadBrowsePageAsync(uri, parameter?.Title, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load podcast browse page {Uri}", uri);
            HasError = true;
            ErrorMessage = "Could not load podcast browse.";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    public void SelectNextHero()
    {
        if (HeroShows.Count == 0)
            return;

        var index = SelectedHero is null ? -1 : HeroShows.IndexOf(SelectedHero);
        SelectHero(HeroShows[(index + 1 + HeroShows.Count) % HeroShows.Count]);
    }

    public void SelectPrevHero()
    {
        if (HeroShows.Count == 0)
            return;

        var index = SelectedHero is null ? 0 : HeroShows.IndexOf(SelectedHero);
        SelectHero(HeroShows[(index - 1 + HeroShows.Count) % HeroShows.Count]);
    }

    public void SelectHero(int index)
    {
        if (index < 0 || index >= HeroShows.Count)
            return;
        SelectHero(HeroShows[index]);
    }

    public void SelectHero(PodcastBrowseItemViewModel? item)
    {
        if (item is null || ReferenceEquals(SelectedHero, item))
            return;

        foreach (var hero in HeroShows)
        {
            hero.IsHeroSelected = ReferenceEquals(hero, item);
            if (!hero.IsHeroSelected)
                hero.IsHeroDetailsExpanded = false;
        }

        SelectedHero = item;
    }

    public void OpenItem(PodcastBrowseItemViewModel? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Uri))
            return;

        if (item.Kind == PodcastBrowseItemKind.Show)
        {
            NavigationHelpers.OpenShowPage(new ContentNavigationParameter
            {
                Uri = item.Uri,
                Title = item.Title,
                Subtitle = item.Subtitle,
                ImageUrl = item.ImageUrl
            }, NavigationHelpers.IsCtrlPressed());
            return;
        }

        NavigationHelpers.OpenPodcastBrowse(new ContentNavigationParameter
        {
            Uri = item.Uri,
            Title = item.Title,
            Subtitle = item.Subtitle,
            ImageUrl = item.ImageUrl
        }, NavigationHelpers.IsCtrlPressed());
    }

    public void OpenBreadcrumb(int index)
    {
        if (index < 0 || index >= BreadcrumbItems.Count || index == BreadcrumbItems.Count - 1)
            return;

        var breadcrumb = BreadcrumbItems[index];
        if (string.IsNullOrWhiteSpace(breadcrumb.Uri) ||
            string.Equals(breadcrumb.Uri, CurrentUri, StringComparison.Ordinal))
        {
            return;
        }

        NavigationHelpers.OpenPodcastBrowse(new ContentNavigationParameter
        {
            Uri = breadcrumb.Uri,
            Title = breadcrumb.Title,
            Subtitle = breadcrumb.Subtitle
        }, NavigationHelpers.IsCtrlPressed());
    }

    public async Task ActivateSectionAsync(PodcastBrowseSectionViewModel? section)
    {
        if (section is null)
            return;

        if (section.EnableLoadMore && section.NextOffset is int offset && !section.IsLoadingMore)
        {
            await LoadMoreSectionAsync(section, offset);
            return;
        }

        if (string.IsNullOrWhiteSpace(section.Uri))
            return;

        NavigationHelpers.OpenPodcastBrowse(new ContentNavigationParameter
        {
            Uri = section.Uri,
            Title = section.Title,
            Subtitle = section.Subtitle
        }, NavigationHelpers.IsCtrlPressed());
    }

    private async Task LoadRootAsync(CancellationToken ct)
    {
        Title = "Podcasts";
        Subtitle = "Browse shows, charts, and categories.";
        SearchPlaceholder = "Search podcasts";
        SetHeaderColor("#27856A");

        var rootTask = GetBrowsePageCachedAsync(RootPodcastsUri, pageLimit: 10, sectionLimit: 10, ct);
        var allCategoriesTask = GetBrowsePageCachedAsync(AllCategoriesUri, pageLimit: 20, sectionLimit: 20, ct);
        var chartsTask = LoadChartShowSectionsAsync(ct);
        // Editorial prefetches feed the "Popular in X" shelves and only act as a
        // hero fallback if charts is empty. Fire them in the background so the
        // first paint isn't blocked on 6 sequential HTTP fetches (~5s).
        var editorialTask = LoadEditorialPrefetchesAsync(ct);

        var root = await rootTask;
        var allCategories = await allCategoriesTask;
        var chartSections = await chartsTask;

        IndexCategoryPaths(allCategories);
        ApplyAllPodcastCategories(allCategories);
        UpdateBreadcrumbs();
        ApplyFeaturedCategories(root, allCategories);
        ApplyCategoryGroups(allCategories);

        var topPodcastSection = chartSections.FirstOrDefault(static section => section.Items.Count > 0);

        var initialShelves = new List<PodcastBrowseSectionViewModel>();
        if (topPodcastSection is not null)
        {
            initialShelves.Add(CreateSectionViewModel(topPodcastSection with { Title = "Top podcasts" }, maxItems: 12));
            ApplyHeroShows(topPodcastSection.Items);
        }

        EditorialShelves.ReplaceWith(initialShelves);
        RefreshContentState();

        // Editorial shelves stream in once their fetches finish. If charts had no
        // items, the first editorial section becomes the hero source.
        _ = AppendEditorialShelvesAsync(editorialTask, hasHero: topPodcastSection is not null, ct);
    }

    private async Task AppendEditorialShelvesAsync(
        Task<IReadOnlyList<PodcastBrowseSectionDto>> editorialTask,
        bool hasHero,
        CancellationToken ct)
    {
        IReadOnlyList<PodcastBrowseSectionDto> editorialSections;
        try
        {
            editorialSections = await editorialTask;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Editorial shelf prefetch failed");
            return;
        }

        if (ct.IsCancellationRequested) return;

        if (!hasHero)
        {
            var fallbackHero = editorialSections.FirstOrDefault(static s => s.Items.Count > 0);
            if (fallbackHero is not null)
                ApplyHeroShows(fallbackHero.Items);
        }

        var existingUris = new HashSet<string>(
            EditorialShelves.Select(s => s.Uri),
            StringComparer.Ordinal);

        foreach (var section in editorialSections.Where(static s => s.Items.Count > 0))
        {
            if (ct.IsCancellationRequested) return;
            if (!existingUris.Add(section.Uri)) continue;
            EditorialShelves.Add(CreateSectionViewModel(section, maxItems: 12));
        }

        RefreshContentState();
    }

    private async Task LoadBrowsePageAsync(string uri, string? fallbackTitle, CancellationToken ct)
    {
        var page = await GetBrowsePageCachedAsync(uri, pageLimit: 20, sectionLimit: 20, ct);
        if (page is null)
            throw new InvalidOperationException($"Podcast browse page returned no data: {uri}");

        Title = string.IsNullOrWhiteSpace(page.Title) ? fallbackTitle ?? "Podcasts" : page.Title;
        Subtitle = string.IsNullOrWhiteSpace(page.Subtitle)
            ? $"Explore podcasts in {Title}."
            : page.Subtitle!;
        SearchPlaceholder = $"Search {Title}";
        SetHeaderColor(page.HeaderColorHex);
        await EnsureCategoryIndexAsync(ct);
        UpdateBreadcrumbs();

        var showSections = page.ShowSections.ToList();
        ApplyHeroShows(showSections.FirstOrDefault()?.Items ?? []);
        FeaturedCategories.ReplaceWith(FlattenCategoryItems(page).Take(12).Select(CreateItemViewModel));
        CategoryGroups.ReplaceWith(CreateCategoryGroups(page));
        EditorialShelves.ReplaceWith(showSections.Select(section => CreateSectionViewModel(section, maxItems: 16)));
        RefreshContentState();
    }

    private async Task LoadSectionPageAsync(string uri, string? fallbackTitle, CancellationToken ct)
    {
        var section = await GetBrowseSectionCachedAsync(uri, offset: 0, limit: 30, ct);
        if (section is null)
            throw new InvalidOperationException($"Podcast browse section returned no data: {uri}");

        Title = string.IsNullOrWhiteSpace(section.Title) ? fallbackTitle ?? "Podcasts" : section.Title;
        Subtitle = section.Subtitle ?? "Browse podcasts from this section.";
        SearchPlaceholder = $"Search {Title}";
        SetHeaderColor(section.Items.FirstOrDefault()?.ColorHex);
        await EnsureCategoryIndexAsync(ct);
        UpdateBreadcrumbs();

        ApplyHeroShows(section.Items.Where(static item => item.Kind == PodcastBrowseItemKind.Show).Take(12));
        FeaturedCategories.ReplaceWith(section.Items
            .Where(static item => item.Kind is PodcastBrowseItemKind.Category or PodcastBrowseItemKind.Section)
            .Select(CreateItemViewModel));
        CategoryGroups.Clear();
        EditorialShelves.ReplaceWith([CreateSectionViewModel(section, maxItems: 30, enableLoadMore: true)]);
        RefreshContentState();
    }

    private async Task<IReadOnlyList<PodcastBrowseSectionDto>> LoadChartShowSectionsAsync(CancellationToken ct)
    {
        var charts = await GetBrowsePageCachedAsync(PodcastChartsUri, pageLimit: 10, sectionLimit: 12, ct);
        var showSections = ExtractShowSections(charts).ToList();
        if (showSections.Count > 0)
            return showSections;

        var firstBrowseLink = charts?.Sections
            .SelectMany(static section => section.Items)
            .FirstOrDefault(static item => item.Kind is PodcastBrowseItemKind.Category or PodcastBrowseItemKind.Section);

        if (firstBrowseLink is null)
            return [];

        if (firstBrowseLink.Uri.StartsWith("spotify:section:", StringComparison.Ordinal))
        {
            var section = await GetBrowseSectionCachedAsync(firstBrowseLink.Uri, offset: 0, limit: 12, ct);
            return section is { HasShows: true } ? [section] : [];
        }

        var page = await GetBrowsePageCachedAsync(firstBrowseLink.Uri, pageLimit: 10, sectionLimit: 12, ct);
        return ExtractShowSections(page).ToList();
    }

    private async Task<IReadOnlyList<PodcastBrowseSectionDto>> LoadEditorialPrefetchesAsync(CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(2);
        var tasks = EditorialPrefetchSources.Select(async source =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var page = await GetBrowsePageCachedAsync(source.Uri, pageLimit: 10, sectionLimit: 12, ct);
                var section = ExtractShowSections(page).FirstOrDefault(static section => section.Items.Count > 0);
                return section is null
                    ? null
                    : section with { Title = $"Popular in {source.Title}" };
            }
            finally
            {
                gate.Release();
            }
        });

        var sections = await Task.WhenAll(tasks);
        return sections.Where(static section => section is not null).Cast<PodcastBrowseSectionDto>().ToList();
    }

    private async Task<PodcastBrowsePageDto?> GetBrowsePageCachedAsync(
        string uri,
        int pageLimit,
        int sectionLimit,
        CancellationToken ct)
    {
        uri = NormalizeBrowseUri(uri);
        if (_pageCache.TryGetValue(uri, out var cached))
            return cached;

        var page = await _podcastService.GetPodcastBrowsePageAsync(
            uri,
            pageOffset: 0,
            pageLimit: pageLimit,
            sectionOffset: 0,
            sectionLimit: sectionLimit,
            ct).ConfigureAwait(true);

        if (page is not null)
            _pageCache[uri] = page;

        return page;
    }

    private async Task<PodcastBrowseSectionDto?> GetBrowseSectionCachedAsync(
        string uri,
        int offset,
        int limit,
        CancellationToken ct)
    {
        var cacheKey = $"{uri}|{offset}|{limit}";
        if (_sectionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var section = await _podcastService.GetPodcastBrowseSectionAsync(uri, offset, limit, ct).ConfigureAwait(true);
        if (section is not null)
            _sectionCache[cacheKey] = section;

        return section;
    }

    private async Task LoadMoreSectionAsync(PodcastBrowseSectionViewModel section, int offset)
    {
        if (_loadCts is null)
            return;

        section.IsLoadingMore = true;
        try
        {
            var next = await GetBrowseSectionCachedAsync(section.Uri, offset, 30, _loadCts.Token);
            if (next is null)
                return;

            foreach (var item in next.Items.Select(CreateItemViewModel))
                section.Items.Add(item);

            section.NextOffset = next.NextOffset;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load more podcast browse section {Uri}", section.Uri);
        }
        finally
        {
            section.IsLoadingMore = false;
        }
    }

    private void ApplyFeaturedCategories(PodcastBrowsePageDto? root, PodcastBrowsePageDto? allCategories)
    {
        var quickLinks = new[]
        {
            new PodcastBrowseItemDto
            {
                Uri = AllCategoriesUri,
                Title = "All podcast categories",
                Subtitle = "Browse every category",
                ColorHex = "#8d67ab",
                Kind = PodcastBrowseItemKind.Category,
                SourceLabel = "Browse"
            },
            new PodcastBrowseItemDto
            {
                Uri = PodcastChartsUri,
                Title = "Podcast Charts",
                Subtitle = "Popular podcasts",
                ColorHex = "#0d73ec",
                Kind = PodcastBrowseItemKind.Category,
                SourceLabel = "Charts"
            }
        };

        var items = quickLinks
            .Concat(FlattenCategoryItems(root))
            .Concat(FlattenCategoryItems(allCategories))
            .DistinctBy(static item => item.Uri, StringComparer.Ordinal)
            .Take(12)
            .Select(CreateItemViewModel);

        FeaturedCategories.ReplaceWith(items);
    }

    private void ApplyCategoryGroups(PodcastBrowsePageDto? allCategories)
    {
        IndexCategoryPaths(allCategories);
        CategoryGroups.ReplaceWith(CreateCategoryGroups(allCategories));
    }

    private async Task EnsureCategoryIndexAsync(CancellationToken ct)
    {
        var alreadyIndexed = _categoryPaths.Count > 0;
        var alreadyHasSidebar = AllPodcastCategories.Count > 0;
        if (alreadyIndexed && alreadyHasSidebar)
            return;

        var allCategories = await GetBrowsePageCachedAsync(AllCategoriesUri, pageLimit: 20, sectionLimit: 20, ct);
        if (!alreadyIndexed)
            IndexCategoryPaths(allCategories);
        if (!alreadyHasSidebar)
            ApplyAllPodcastCategories(allCategories);
    }

    private void ApplyAllPodcastCategories(PodcastBrowsePageDto? allCategories)
    {
        if (allCategories is null)
            return;

        AllPodcastCategories.ReplaceWith(CreateCategoryGroups(allCategories));
        UpdateSelectedCategoryStates();
        OnPropertyChanged(nameof(HasAllPodcastCategories));
        OnPropertyChanged(nameof(ShowSidebar));
        OnPropertyChanged(nameof(ShowSidebarShimmer));
    }

    private void IndexCategoryPaths(PodcastBrowsePageDto? allCategories)
    {
        if (allCategories is null)
            return;

        foreach (var section in allCategories.Sections.Where(static section => section.HasCategories))
        {
            foreach (var item in section.Items.Where(static item =>
                         item.Kind is PodcastBrowseItemKind.Category or PodcastBrowseItemKind.Section))
            {
                if (string.IsNullOrWhiteSpace(item.Uri))
                    continue;

                _categoryPaths[NormalizeBrowseUri(item.Uri)] = new PodcastBrowseCategoryPath(
                    section.Title,
                    item.Title);
            }
        }
    }

    private void UpdateBreadcrumbs()
    {
        var items = new List<PodcastBrowseBreadcrumbItem>
        {
            new("Podcasts", RootPodcastsUri, "Browse shows, charts, and categories.")
        };

        if (string.Equals(CurrentUri, RootPodcastsUri, StringComparison.Ordinal))
        {
            BreadcrumbItems.ReplaceWith(items);
            OnPropertyChanged(nameof(ShowBreadcrumbs));
            return;
        }

        if (string.Equals(CurrentUri, AllCategoriesUri, StringComparison.Ordinal))
        {
            items.Add(new("All podcast categories", AllCategoriesUri, "Browse every category"));
        }
        else if (string.Equals(CurrentUri, PodcastChartsUri, StringComparison.Ordinal))
        {
            items.Add(new("Podcast Charts", PodcastChartsUri, "Popular podcasts"));
        }
        else if (_categoryPaths.TryGetValue(CurrentUri, out var path))
        {
            items.Add(new("All podcast categories", AllCategoriesUri, "Browse every category"));
            if (!string.IsNullOrWhiteSpace(path.GroupTitle) &&
                !string.Equals(path.GroupTitle, Title, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new(path.GroupTitle, null, null));
            }
            items.Add(new(Title, CurrentUri, Subtitle));
        }
        else
        {
            items.Add(new(Title, CurrentUri, Subtitle));
        }

        BreadcrumbItems.ReplaceWith(items);
        OnPropertyChanged(nameof(ShowBreadcrumbs));
        UpdateSelectedCategoryStates();
    }

    private void UpdateSelectedCategoryStates()
    {
        var current = NormalizeBrowseUri(CurrentUri);

        foreach (var section in AllPodcastCategories)
        {
            foreach (var item in section.Items)
            {
                item.IsSidebarSelected = !string.Equals(current, RootPodcastsUri, StringComparison.Ordinal) &&
                                         string.Equals(NormalizeBrowseUri(item.Uri), current, StringComparison.Ordinal);
            }
        }
    }

    private void ApplyHeroShows(IEnumerable<PodcastBrowseItemDto> items)
    {
        var heroes = items
            .Where(static item => item.Kind == PodcastBrowseItemKind.Show)
            .DistinctBy(static item => item.Uri, StringComparer.Ordinal)
            .Take(12)
            .Select(CreateItemViewModel)
            .ToList();

        HeroShows.ReplaceWith(heroes);
        HeroSideShows.ReplaceWith(heroes.Skip(1).Take(3));
        SelectHero(HeroShows.FirstOrDefault());
        RefreshContentState();

        _ = ResolveHeroColorsAsync(heroes);
    }

    /// <summary>
    /// Spotify Pathfinder doesn't return extracted colours on browse-show payloads,
    /// so we kick off a single batched lookup against <see cref="IColorService"/>
    /// (hot-cache → SQLite → API) and repaint the hero/side cards as colours arrive.
    /// </summary>
    private async Task ResolveHeroColorsAsync(IReadOnlyList<PodcastBrowseItemViewModel> items)
    {
        if (_colorService is null || items.Count == 0)
            return;

        var byUrl = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl))
            .GroupBy(item => item.ImageUrl!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        if (byUrl.Count == 0)
            return;

        try
        {
            var colors = await _colorService.GetColorsAsync(byUrl.Keys.ToList()).ConfigureAwait(false);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed)
                    return;

                foreach (var (url, color) in colors)
                {
                    if (!byUrl.TryGetValue(url, out var matches))
                        continue;
                    foreach (var item in matches)
                        item.ApplyExtractedColor(color);
                }

                HeroColorsResolved?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to resolve hero extracted colours for {Count} images", byUrl.Count);
        }
    }

    private IEnumerable<PodcastBrowseSectionDto> ExtractShowSections(PodcastBrowsePageDto? page)
        => page?.Sections.Where(static section => section.HasShows) ?? [];

    private static IEnumerable<PodcastBrowseItemDto> FlattenCategoryItems(PodcastBrowsePageDto? page)
        => page?.Sections
               .Where(static section => section.HasCategories)
               .SelectMany(static section => section.Items)
               .Where(static item => item.Kind is PodcastBrowseItemKind.Category or PodcastBrowseItemKind.Section)
           ?? [];

    private IEnumerable<PodcastBrowseSectionViewModel> CreateCategoryGroups(PodcastBrowsePageDto? page)
    {
        if (page is null)
            yield break;

        foreach (var section in page.Sections.Where(static section => section.HasCategories))
        {
            var items = section.Items
                .Where(static item => item.Kind is PodcastBrowseItemKind.Category or PodcastBrowseItemKind.Section)
                .DistinctBy(static item => item.Uri, StringComparer.Ordinal)
                .Select(CreateItemViewModel)
                .ToList();

            if (items.Count == 0)
                continue;

            yield return new PodcastBrowseSectionViewModel(section.Uri, section.Title, section.Subtitle, enableLoadMore: false)
                .WithItems(items);
        }
    }

    private PodcastBrowseSectionViewModel CreateSectionViewModel(
        PodcastBrowseSectionDto section,
        int maxItems,
        bool enableLoadMore = false)
    {
        var vm = new PodcastBrowseSectionViewModel(section.Uri, section.Title, section.Subtitle, enableLoadMore)
        {
            NextOffset = section.NextOffset,
            TotalCount = section.TotalCount
        };
        vm.Items.ReplaceWith(section.Items
            .Where(static item => item.Kind == PodcastBrowseItemKind.Show)
            .DistinctBy(static item => item.Uri, StringComparer.Ordinal)
            .Take(maxItems)
            .Select(CreateItemViewModel));
        return vm;
    }

    private static PodcastBrowseItemViewModel CreateItemViewModel(PodcastBrowseItemDto item)
        => new(item);

    private void SetHeaderColor(string? hex)
    {
        if (!TintColorHelper.TryParseHex(hex, out var color))
            color = Color.FromArgb(255, 39, 133, 106);

        var bright = TintColorHelper.BrightenForTint(color, targetMax: 185);
        HeaderColorBrush = new SolidColorBrush(Color.FromArgb(110, color.R, color.G, color.B));
        HeaderTintBrush = new SolidColorBrush(Color.FromArgb(48, bright.R, bright.G, bright.B));
    }

    private void ClearContent()
    {
        HeroShows.Clear();
        HeroSideShows.Clear();
        FeaturedCategories.Clear();
        EditorialShelves.Clear();
        CategoryGroups.Clear();
        SelectedHero = null;
        RefreshContentState();
    }

    private void RefreshContentState()
    {
        OnPropertyChanged(nameof(HasHeroShows));
        OnPropertyChanged(nameof(HasHeroSideShows));
        OnPropertyChanged(nameof(HeroSidePrimary));
        OnPropertyChanged(nameof(HeroSideSecondaryOne));
        OnPropertyChanged(nameof(HeroSideSecondaryTwo));
        OnPropertyChanged(nameof(HasHeroSidePrimary));
        OnPropertyChanged(nameof(HasHeroSideSecondaryOne));
        OnPropertyChanged(nameof(HasHeroSideSecondaryTwo));
        OnPropertyChanged(nameof(ShowHeroShows));
        OnPropertyChanged(nameof(ShowHeroCarousel));
        OnPropertyChanged(nameof(HasFeaturedCategories));
        OnPropertyChanged(nameof(HasEditorialShelves));
        OnPropertyChanged(nameof(HasCategoryGroups));
        OnPropertyChanged(nameof(HasAllPodcastCategories));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(ShowInitialLoading));
        OnPropertyChanged(nameof(ShowNoContentMessage));
        OnPropertyChanged(nameof(ShowBreadcrumbs));
        OnPropertyChanged(nameof(HeroCount));
        OnPropertyChanged(nameof(SelectedHeroIndex));
    }

    private static string NormalizeBrowseUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return RootPodcastsUri;

        const string genrePrefix = "spotify:genre:";
        uri = uri.Trim();
        return uri.StartsWith(genrePrefix, StringComparison.Ordinal)
            ? $"spotify:page:{uri[genrePrefix.Length..]}"
            : uri;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }

    private sealed record EditorialPrefetchSource(string Title, string Uri);
    private sealed record PodcastBrowseCategoryPath(string GroupTitle, string Title);
}

public sealed record PodcastBrowseBreadcrumbItem(string Title, string? Uri, string? Subtitle)
{
    public override string ToString() => Title;
}

public sealed partial class PodcastBrowseSectionViewModel : ObservableObject
{
    public string Uri { get; }
    public string Title { get; }
    public string? Subtitle { get; }
    public bool EnableLoadMore { get; }
    public ObservableCollection<PodcastBrowseItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAction))]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private int? _nextOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAction))]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private bool _isLoadingMore;

    [ObservableProperty]
    private int _totalCount;

    public bool HasAction => EnableLoadMore ? NextOffset.HasValue || IsLoadingMore : !string.IsNullOrWhiteSpace(Uri);
    public string ActionText => EnableLoadMore ? IsLoadingMore ? "Loading" : "Load more" : "See all";

    public PodcastBrowseSectionViewModel(string uri, string title, string? subtitle, bool enableLoadMore)
    {
        Uri = uri;
        Title = string.IsNullOrWhiteSpace(title) ? "Podcasts" : title;
        Subtitle = subtitle;
        EnableLoadMore = enableLoadMore;
    }

    public PodcastBrowseSectionViewModel WithItems(IEnumerable<PodcastBrowseItemViewModel> items)
    {
        Items.ReplaceWith(items);
        return this;
    }
}

public sealed partial class PodcastBrowseItemViewModel : ObservableObject
{
    public HeroCarouselSlide ToHeroSlide() => new()
    {
        ImageUri = System.Uri.TryCreate(ImageUrl, UriKind.Absolute, out var u) ? u : new Uri("about:blank"),
        Title = Title,
        Subtitle = Subtitle ?? string.Empty,
        CtaText = "Listen now",
        Tag = null,
        Eyebrow = SourceLabel,
        AccentColor = HeroPrimaryColor,
        GlowColor = HeroPrimaryColor,
        CozyTintColor = HeroPrimaryColor,
        UseScrim = true,
        CtaUsesGlass = false,
        CtaStyle = HeroCarouselCtaStyle.GhostPill,
    };

    private readonly PodcastBrowseItemDto _item;

    private const double DefaultHeroCardWidth = 260;
    private const double DefaultHeroCardFooterHeight = 0;
    private const double DefaultHeroCardArtHeight = 260;

    [ObservableProperty]
    private bool _isSidebarSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroDetailsVisibility))]
    private bool _isHeroDetailsExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroScale))]
    [NotifyPropertyChangedFor(nameof(HeroOpacity))]
    [NotifyPropertyChangedFor(nameof(HeroContentOpacity))]
    [NotifyPropertyChangedFor(nameof(HeroZIndex))]
    [NotifyPropertyChangedFor(nameof(HeroAdvanceProgressVisibility))]
    private bool _isHeroSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroContentMaxWidth))]
    [NotifyPropertyChangedFor(nameof(HeroTitleMaxWidth))]
    [NotifyPropertyChangedFor(nameof(HeroTitleFontSize))]
    [NotifyPropertyChangedFor(nameof(HeroTitleLineHeight))]
    private double _heroCardWidth = DefaultHeroCardWidth;

    [ObservableProperty]
    private double _heroCardHeight = DefaultHeroCardArtHeight + DefaultHeroCardFooterHeight;

    [ObservableProperty]
    private double _heroCardArtHeight = DefaultHeroCardArtHeight;

    [ObservableProperty]
    private double _heroCardFooterHeight = DefaultHeroCardFooterHeight;

    public string Uri => _item.Uri;
    public string Title => _item.Title;
    public string? Subtitle => _item.Subtitle;
    public string? ImageUrl => _item.ImageUrl;
    public string? ColorHex => _item.ColorHex;
    public PodcastBrowseItemKind Kind => _item.Kind;
    public string? MediaType => _item.MediaType;
    public string SourceLabel => _item.SourceLabel ?? Kind switch
    {
        PodcastBrowseItemKind.Show => "Podcast",
        PodcastBrowseItemKind.Section => "Section",
        PodcastBrowseItemKind.Category => "Category",
        _ => ""
    };

    public double HeroScale => IsHeroSelected ? 1.0 : 0.88;
    public double HeroOpacity => IsHeroSelected ? 1.0 : 0.72;
    public double HeroContentOpacity => IsHeroSelected ? 1.0 : 0.52;
    public int HeroZIndex => IsHeroSelected ? 10 : 0;
    public Visibility HeroAdvanceProgressVisibility => IsHeroSelected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HeroDetailsVisibility => IsHeroDetailsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public double HeroContentMaxWidth => Math.Clamp(HeroCardWidth * 0.58, 380, 560);
    public double HeroTitleMaxWidth => Math.Max(220, HeroContentMaxWidth - 112);
    public double HeroTitleFontSize
    {
        get
        {
            var length = Title.Length;
            var longestWord = GetLongestWordLength(Title);

            var fontSize = (length, longestWord) switch
            {
                (> 58, _) or (_, > 22) => 38d,
                (> 44, _) or (_, > 18) => 41d,
                (> 32, _) or (_, > 14) => 44d,
                _ => 48d
            };

            return HeroTitleMaxWidth < 300 ? Math.Min(fontSize, 40d) : fontSize;
        }
    }
    public double HeroTitleLineHeight => Math.Round(HeroTitleFontSize * 1.08);

    [ObservableProperty]
    private Brush _tileBrush = new SolidColorBrush();

    [ObservableProperty]
    private Brush _tileTintBrush = new SolidColorBrush();

    [ObservableProperty]
    private Brush _tileBorderBrush = new SolidColorBrush();

    /// <summary>
    /// Primary colour fed to the hero carousel shader.
    /// Resolved from the DTO's <see cref="ColorHex"/>, then updated by live
    /// extracted-colour results when available.
    /// </summary>
    [ObservableProperty]
    private Color _heroPrimaryColor;

    /// <summary>
    /// Accent colour fed alongside <see cref="HeroPrimaryColor"/> to the shader so the
    /// background mesh has two-tone variation.
    /// </summary>
    [ObservableProperty]
    private Color _heroAccentColor;

    public PodcastBrowseItemViewModel(PodcastBrowseItemDto item)
    {
        _item = item;

        // Use the DTO ColorHex if Spotify supplied one. If not, leave the brushes
        // at their default empty state — async IColorService extraction will fill
        // them in from the cover art when available.
        if (TintColorHelper.TryParseHex(item.ColorHex, out var color))
            ApplyPaletteFromBaseColor(color);
    }

    /// <summary>
    /// Apply a palette derived from the cover's extracted colour. Picks the dark variant
    /// first (best for the hero scrim/shader contrast), then raw, then light. Falls back
    /// silently if the hex can't be parsed.
    /// </summary>
    public void ApplyExtractedColor(ExtractedColor? extracted)
    {
        if (extracted is null)
            return;

        var hex = extracted.DarkHex ?? extracted.RawHex ?? extracted.LightHex;
        if (!TintColorHelper.TryParseHex(hex, out var color))
            return;

        ApplyPaletteFromBaseColor(color);
    }

    private void ApplyPaletteFromBaseColor(Color color)
    {
        var bright = TintColorHelper.BrightenForTint(color, targetMax: 190);
        TileBrush = new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B));
        TileTintBrush = new SolidColorBrush(Color.FromArgb(72, bright.R, bright.G, bright.B));
        TileBorderBrush = new SolidColorBrush(Color.FromArgb(180, bright.R, bright.G, bright.B));

        var heroPrimary = MakeHeroColorVibrant(TintColorHelper.BrightenForTint(color, targetMax: 235));
        HeroPrimaryColor = Color.FromArgb(255, heroPrimary.R, heroPrimary.G, heroPrimary.B);
        HeroAccentColor = RotateHeroHue(heroPrimary, 0.16);
    }

    private static int GetLongestWordLength(string value)
    {
        var longest = 0;
        var current = 0;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch))
            {
                longest = Math.Max(longest, current);
                current = 0;
                continue;
            }

            current++;
        }

        return Math.Max(longest, current);
    }

    private static Color MakeHeroColorVibrant(Color color)
    {
        RgbToHsv(color, out var hue, out var saturation, out var value);
        saturation = Math.Clamp(Math.Max(saturation * 1.28, 0.58), 0, 0.96);
        value = Math.Clamp(Math.Max(value, 0.82) * 1.08, 0, 1);
        return FromHsv(color.A, hue, saturation, value);
    }

    private static Color RotateHeroHue(Color color, double amount)
    {
        RgbToHsv(color, out var hue, out var saturation, out var value);
        hue = (hue + amount) % 1;
        saturation = Math.Clamp(Math.Max(saturation * 1.18, 0.68), 0, 1);
        value = Math.Clamp(Math.Max(value, 0.88), 0, 1);
        return FromHsv(color.A, hue, saturation, value);
    }

    private static void RgbToHsv(Color color, out double hue, out double saturation, out double value)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        value = max;
        saturation = max <= 0 ? 0 : delta / max;

        if (delta <= 0)
        {
            hue = 0;
            return;
        }

        if (max == r)
            hue = ((g - b) / delta) % 6;
        else if (max == g)
            hue = ((b - r) / delta) + 2;
        else
            hue = ((r - g) / delta) + 4;

        hue /= 6;
        if (hue < 0)
            hue += 1;
    }

    private static Color FromHsv(byte alpha, double hue, double saturation, double value)
    {
        var h = hue * 6;
        var c = value * saturation;
        var x = c * (1 - Math.Abs((h % 2) - 1));
        var m = value - c;

        var (r, g, b) = h switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x)
        };

        return Color.FromArgb(
            alpha,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
