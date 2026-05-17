using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Windows.UI;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels.Home;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Thin composer that owns three child VMs (<see cref="Feed"/>,
/// <see cref="Recommendations"/>, <see cref="Greeting"/>), the page-level
/// hero palette / page-bleed pipeline, and the home-page navigation
/// lifecycle (load / hibernate / resume / dispose).
///
/// <para>The decomposition replaces the previous ~2,540-line "god ViewModel"
/// that owned every home concern. Each child has a single responsibility
/// (feed composition / recommendation enrichment / greeting + user identity);
/// they communicate via the parent — no direct child-to-child references.</para>
/// </summary>
public sealed partial class HomeViewModel : ObservableObject, ITabBarItemContent, IDisposable
{
    private readonly IHomeFeedService? _homeFeedService;
    private readonly HomeFeedCache? _homeFeedCache;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;
    private bool _isDisposed;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isCustomizeFlyoutOpen;

    // ── Children — constructor-initialised, never replaced ──────────────────

    /// <summary>Greeting band state — text, subtitle, current-user identity.
    /// Constructor-initialized.</summary>
    public HomeGreetingViewModel Greeting { get; }

    /// <summary>Home-feed surface — sections collection, chips, local-files
    /// shelf, customization preferences, chip facet selection.
    /// Constructor-initialized.</summary>
    public HomeFeedViewModel Feed { get; }

    /// <summary>Recommendation enrichment — baseline preview tracks /
    /// canvases, recently-played hand-off, featured-item tracking.
    /// Constructor-initialized.</summary>
    public HomeRecommendationsViewModel Recommendations { get; }

    /// <summary>
    /// Adapter feeding the redesigned hero band + side rail + region buckets.
    /// Lives in <c>ViewModels/Home/HomeHeroAdapter.cs</c>; subscribes to
    /// <see cref="Feed"/>'s Sections and <see cref="Recommendations"/>'s
    /// FeaturedItem via the parent (HostPropertyChanged proxy events).
    /// </summary>
    public ViewModels.Home.HomeHeroAdapter HeroAdapter { get; }

    // ── Hero band (greeting + featured "pick up where you left off") ──
    // Mirrors the album/playlist palette pipeline so the hero feels like a
    // sibling of those pages — backdrop wash is derived from the featured
    // item's cover art and theme-aware via ApplyTheme.

    /// <summary>Subtle page-wash brush tinted toward the featured item's color.
    /// Null when no palette is available (cold start, fetch failure).</summary>
    [ObservableProperty]
    private Brush? _heroBackdropBrush;

    /// <summary>Crisp accent bar matching the section-header AccentLineBrush
    /// treatment, tinted from the featured item's color (lifted for legibility).
    /// Renders as a 120x3 colored bar under the greeting/chips.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroAccentLineBrushOrFallback))]
    private Brush? _heroAccentLineBrush;

    /// <summary>Same as <see cref="HeroAccentLineBrush"/>, but falls back to
    /// the system accent brush when no palette is available — keeps the line
    /// visible on featured items without ExtractedColors so the hero band
    /// doesn't read as missing chrome.</summary>
    public Brush HeroAccentLineBrushOrFallback =>
        HeroAccentLineBrush
        ?? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    /// <summary>Top-left page bleed — a large radial wash anchored at the
    /// page's top-left corner, tinted from the first home card's extracted
    /// color. Gives the whole page a per-day visual identity.</summary>
    [ObservableProperty]
    private Brush? _pageBleedBrush;

    private bool _isDarkTheme;

    /// <summary>
    /// Fires <see cref="HomeFeedLoadedMessage"/> at most once per VM
    /// instance — drives the final phase of <c>SpotifyConnectDialog</c>'s
    /// progress bar (and its auto-close).
    /// </summary>
    private bool _homeFeedLoadedFired;

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    public HomeViewModel(
        IHomeFeedService? homeFeedService = null,
        ISettingsService? settingsService = null,
        Services.HomeFeedCache? homeFeedCache = null,
        Services.RecentlyPlayedService? recentlyPlayedService = null,
        Services.HomeResponseParserFactory? parserFactory = null,
        IAuthState? authState = null,
        ILogger<HomeViewModel>? logger = null,
        Wavee.Local.ILocalLibraryService? localLibrary = null)
    {
        _homeFeedService = homeFeedService;
        _homeFeedCache = homeFeedCache;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        var resolvedParserFactory = parserFactory ?? new Services.HomeResponseParserFactory();

        Greeting = new HomeGreetingViewModel(authState);

        Feed = new HomeFeedViewModel(
            homeFeedService,
            settingsService,
            homeFeedCache,
            resolvedParserFactory,
            localLibrary,
            logger,
            isDarkThemeProvider: () => _isDarkTheme,
            greetingSetter: g => Greeting.ApplyGreetingFromSnapshot(g));

        Recommendations = new HomeRecommendationsViewModel(
            homeFeedService,
            homeFeedCache,
            recentlyPlayedService,
            logger,
            sectionsProvider: () => Feed.Sections);

        // ── Cross-child wiring ──────────────────────────────────────────────
        // Feed.SectionsApplied → recommendations fans out baseline enrichment
        // and the recents-service hand-off; parent refreshes the page-level
        // palette wash from the new section colours. Children stay decoupled
        // — the parent is the single fan-out point.
        Feed.SectionsApplied += (_, ordered) =>
        {
            Recommendations.BeginBaselineEnrichment();
            Recommendations.DispatchRecentsToService(ordered);
            // Refresh page-level bleed now that Sections is populated. ApplyTheme
            // reads Sections[0].Items[0].ColorHex to source the glow color.
            ApplyTheme(_isDarkTheme);
        };

        // Feed.FacetRefetchStateChanged → mirror into the parent's top-level
        // IsLoading / HasError flags so the page's loading scrim tracks chip
        // presses without the child holding page-lifecycle flags itself. The
        // begin-tick also cancels any in-flight baseline enrichment so the
        // previous chip's preview-track fetch doesn't keep racing the new
        // chip's load (was an explicit step in the old RefetchWithFacet).
        Feed.FacetRefetchStateChanged += (_, args) =>
        {
            IsLoading = args.IsLoading;
            if (args.IsLoading)
            {
                HasError = false;
                ErrorMessage = null;
                Recommendations.CancelBaselineEnrichment();
            }
        };
        Feed.FacetRefetchFailed += (_, ex) =>
        {
            HasError = true;
            ErrorMessage = ex.Message;
        };

        // Recommendations.FeaturedItemChanged → parent re-derives the hero
        // palette from the new featured cover. (HeroAdapter listens via the
        // adapter's own host-property-changed proxy so we re-raise from here
        // when the feature changes.)
        Recommendations.FeaturedItemChanged += (_, _) =>
        {
            _heroBaseColor = TryParseHex(Recommendations.FeaturedItem?.ColorHex);
            ApplyTheme(_isDarkTheme);
            // HeroAdapter listens for HostPropertyChanged(nameof(FeaturedItem))
            // — re-raise on the parent so the adapter rebuilds its slide row.
            OnPropertyChanged(nameof(FeaturedItem));
            OnPropertyChanged(nameof(HasFeaturedItem));
        };

        // Subscribe Greeting + Recommendations to their long-lived services
        // through a helper so they have a single matching Detach point
        // (called from Dispose). HomePage is Enabled-cached so only one
        // HomeViewModel exists at a time, but routing through the helper
        // keeps the pattern consistent across all VMs.
        AttachLongLivedServices();

        WeakReferenceMessenger.Default.Register<HomeLocalFilesVisibilityChangedMessage>(this, (r, m) =>
        {
            var vm = (HomeViewModel)r;
            if (m.Value)
                _ = vm.Feed.RefreshLocalSectionAsync();
            else
                vm.Feed.RemoveLocalSectionOnDispatcher();
        });

        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Home, null)
        {
            Title = "Home"
        };

        // Hero adapter must subscribe AFTER Feed.Sections is initialised —
        // both children are constructed above, so the adapter can safely
        // observe the bound collection through the parent's proxy properties.
        HeroAdapter = new ViewModels.Home.HomeHeroAdapter(this);

        Diagnostics.LiveInstanceTracker.Register(this);
    }

    // ── Proxy properties — preserved for HomeHeroAdapter, XAML, and external
    //    callers that still observe these as top-level VM properties. The
    //    real state lives on the children; the proxies re-raise when the
    //    children's underlying values mutate. ─────────────────────────────

    /// <summary>Bound collection of home feed sections. Proxies to
    /// <see cref="HomeFeedViewModel.Sections"/> for compatibility with
    /// <see cref="HomeHeroAdapter"/> and the few code-behind paths still
    /// reading <c>ViewModel.Sections</c> directly.</summary>
    public ObservableCollection<HomeSection> Sections => Feed.Sections;

    /// <summary>True while the synthetic "Local files" chip is selected.
    /// Proxies <see cref="HomeFeedViewModel.IsLocalChipActive"/>.</summary>
    public bool IsLocalChipActive => Feed.IsLocalChipActive;

    /// <summary>Most-recently-played item promoted to the hero card slot.
    /// Proxies <see cref="HomeRecommendationsViewModel.FeaturedItem"/>.</summary>
    public HomeSectionItem? FeaturedItem => Recommendations.FeaturedItem;

    /// <summary>True when there's a "Pick up where you left off" item to render.
    /// Drives the FeaturedItem card's `x:Load` so users without a featured item
    /// never instantiate the card subtree.</summary>
    public bool HasFeaturedItem => FeaturedItem != null;

    /// <summary>Active chip facet driving the home feed. Proxies
    /// <see cref="HomeFeedViewModel.CurrentFacet"/>.</summary>
    public string? CurrentFacet => Feed.CurrentFacet;

    // Re-raise child property changes as parent property changes so XAML
    // bindings rooted at the parent observe child state without the user
    // having to know the decomposition.
    private void OnFeedPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HomeFeedViewModel.Sections):
                OnPropertyChanged(nameof(Sections));
                break;
            case nameof(HomeFeedViewModel.IsLocalChipActive):
                OnPropertyChanged(nameof(IsLocalChipActive));
                break;
        }
    }

    // ── Load orchestration ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading) return;
        Recommendations.CancelBaselineEnrichment();
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;

        try
        {
            if (_homeFeedService is null || !_homeFeedService.IsAvailable)
            {
                Greeting.UpdateGreetingFromTimeOfDay();
                return;
            }

            // 1. Serve cached data instantly if available
            if (_homeFeedCache != null && _homeFeedCache.HasData && !_homeFeedCache.IsStale)
            {
                var snapshot = _homeFeedCache.GetCached();
                if (snapshot != null)
                {
                    await Feed.ApplyCacheSnapshotAsync(snapshot);
                    MaybeFireHomeFeedLoaded();
                    return;
                }
            }

            // 2. Fetch fresh data
            if (_homeFeedCache != null)
            {
                var snapshot = await _homeFeedCache.FetchFreshAsync();
                await Feed.ApplyFreshSnapshotAsync(snapshot);

                // Start background refresh
                _homeFeedCache.StartBackgroundRefresh();
            }
            else
            {
                // No cache service — direct fetch through the home-feed service.
                var response = _homeFeedService is null
                    ? null
                    : await _homeFeedService.GetHomeAsync(sectionItemsLimit: 10).ConfigureAwait(false);
                if (response is null) return;
                await Feed.ApplyDirectFetchAsync(response);
            }

            if (string.IsNullOrEmpty(Greeting.Text))
                Greeting.UpdateGreetingFromTimeOfDay();

            // First successful home render — drives the sign-in dialog's
            // auto-close. Fires at most once per VM instance; subsequent
            // tab switches / refreshes are no-ops.
            MaybeFireHomeFeedLoaded();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            _logger?.LogError(ex, "Failed to load home page content");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void MaybeFireHomeFeedLoaded()
    {
        if (_homeFeedLoadedFired || Sections.Count == 0) return;
        _homeFeedLoadedFired = true;
        var totalItems = 0;
        foreach (var s in Sections) totalItems += s.Items?.Count ?? 0;
        WeakReferenceMessenger.Default.Send(
            new Data.Messages.HomeFeedLoadedMessage(Sections.Count, totalItems));
    }

    /// <summary>
    /// Pause the 5-minute background refresh. Call from the page's
    /// OnNavigatedFrom so the timer doesn't keep hammering Pathfinder
    /// while the user is on another page (that work was a big chunk of
    /// hot-spot #3 in the reactive-infrastructure plan).
    /// </summary>
    public void SuspendBackgroundRefresh() => _homeFeedCache?.SuspendRefresh();

    /// <summary>
    /// Resume the 5-minute background refresh. Call from OnNavigatedTo.
    /// No-op if refresh was never started.
    /// </summary>
    public void ResumeBackgroundRefresh() => _homeFeedCache?.ResumeRefresh();

    /// <summary>
    /// Pause refresh AND release the parsed feed tree so the Home page's
    /// footprint drops while the user is on another page. The raw home-feed
    /// response stays cached in <see cref="Services.HomeFeedCache"/> (SQLite
    /// + in-memory), so coming back via <see cref="ResumeAndRehydrate"/>
    /// rebuilds the parsed sections without a network round-trip.
    ///
    /// Without this, 127 section items + baseline enrichment (preview tracks,
    /// poster URLs, canvas JSON) stay pinned in <see cref="Sections"/> for
    /// the navigation-cached page's entire lifetime — a few MB per Home
    /// visit that never come back under GC.
    /// </summary>
    public void HibernateForNavigation()
    {
        SuspendBackgroundRefresh();
        Recommendations.CancelBaselineEnrichment();
        // Pin the local-files shelf across hibernation. It is small (capped
        // by LocalSectionMaxItems) and is sourced separately from the
        // Spotify feed, so the Extract→ApplyDiff→Restore pattern that
        // ResumeAndRehydrate relies on only works if it can find the
        // section in Sections on entry.

        // Phase 7.4 — release hero/featured state so the bound Image controls
        // drop their textures and the cached page's residual footprint shrinks.
        // ResumeAndRehydrate replays ApplyBackgroundRefresh which re-derives
        // FeaturedItem and the hero brushes from the cached snapshot, so this
        // is a pure transient release. HasFeaturedItem flips false → x:Load
        // unloads the FeaturedItem button subtree.
        HeroBackdropBrush = null;
        HeroAccentLineBrush = null;
        PageBleedBrush = null;
    }

    /// <summary>
    /// Pair with <see cref="HibernateForNavigation"/>: rebuild sections from
    /// the cached raw feed and resume the background refresh.
    /// </summary>
    public void ResumeAndRehydrate()
    {
        ResumeBackgroundRefresh();
        if (_homeFeedCache?.GetCached() is { } snapshot)
            ApplyBackgroundRefresh(snapshot);
    }

    public void ResumeFromNavigationCache()
    {
        ResumeBackgroundRefresh();

        if (Sections.Count == 0)
        {
            ResumeAndRehydrate();
            return;
        }

        ApplyTheme(_isDarkTheme);
        Recommendations.BeginBaselineEnrichment();
    }

    public void ApplyBackgroundRefresh(Services.HomeFeedSnapshot snapshot)
    {
        Recommendations.CancelBaselineEnrichment();
        Feed.ApplyBackgroundRefresh(snapshot);
    }

    /// <summary>
    /// Refresh the synthetic "Local files" home shelf. Forwarded straight to
    /// the feed child — kept here as a convenience so HomePage's
    /// OnNavigatedTo doesn't need to know the child topology.
    /// </summary>
    public Task RefreshLocalSectionAsync() => Feed.RefreshLocalSectionAsync();

    [RelayCommand]
    private async Task RetryAsync()
    {
        HasError = false;
        ErrorMessage = null;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Force cache to be stale so LoadAsync fetches fresh data
        var cache = _homeFeedCache;
        cache?.Invalidate();
        await LoadAsync();
    }

    // ── Chip selection (parent shim that routes into the feed child) ────────

    [RelayCommand]
    private Task SelectChipAsync(HomeChipViewModel? chip) => Feed.SelectChipAsync(chip);

    // ── Bound proxies for the chip / section preferences XAML surfaces ──────
    // Forwarded so existing XAML bindings (and the code-behind handlers in
    // HomePage.xaml.cs) keep working through the parent VM root.

    public ObservableCollection<HomeChipViewModel> DisplayedChips => Feed.DisplayedChips;
    public ObservableCollection<HomeSectionPref> SectionPreferences => Feed.SectionPreferences;
    public int NewSectionCount => Feed.NewSectionCount;

    /// <summary>
    /// Sets a section's visibility to a specific value (not a toggle).
    /// Called from the checkbox Checked/Unchecked events to avoid double-toggle bugs.
    /// </summary>
    public void SetSectionVisibility(string sectionUri, bool visible)
        => Feed.SetSectionVisibility(sectionUri, visible);

    [RelayCommand]
    private void ToggleSectionVisibility(string sectionUri) => Feed.ToggleSectionVisibility(sectionUri);

    [RelayCommand]
    private void ToggleSectionPin(string sectionUri) => Feed.ToggleSectionPin(sectionUri);

    [RelayCommand]
    private void MoveSectionUp(string sectionUri) => Feed.MoveSectionUp(sectionUri);

    [RelayCommand]
    private void MoveSectionDown(string sectionUri) => Feed.MoveSectionDown(sectionUri);

    [RelayCommand]
    private async Task ResetSectionPreferencesAsync()
    {
        Feed.ResetSectionPreferences();
        await LoadAsync();
    }

    // ── Section mapping ──
    // Kept on the parent — these are pure static parsers shared by both
    // HomeResponseParserV1 / V2 paths. Moving them off the type would force
    // call sites to know about a HomeViewModelStaticHelpers class for no
    // gain; the cost of leaving them is one more line in the file (they're
    // already static).

    internal static List<HomeSection> MapSectionsFromResponse(HomeResponse response)
    {
        var sections = new List<HomeSection>();
        var apiSections = response.Data?.Home?.SectionContainer?.Sections?.Items;
        if (apiSections == null) return sections;

        var rawSections = Services.HomeRawJsonHelper.GetRawSectionJsonByIndex(response);
        var sectionIndex = -1;

        foreach (var entry in apiSections)
        {
            sectionIndex++;
            var sectionType = entry.Data?.TypeName switch
            {
                "HomeShortsSectionData" => HomeSectionType.Shorts,
                "HomeRecentlyPlayedSectionData" => HomeSectionType.RecentlyPlayed,
                "HomeFeedBaselineSectionData" => HomeSectionType.Baseline,
                _ => HomeSectionType.Generic
            };

            var rawTitle = entry.Data?.Title?.TransformedLabel;
            // Fallback title for sections with no name
            var title = !string.IsNullOrWhiteSpace(rawTitle) ? rawTitle : sectionType switch
            {
                HomeSectionType.Shorts => "Quick access",
                HomeSectionType.RecentlyPlayed => "Recently played",
                HomeSectionType.Baseline => entry.Data?.TypeName ?? "Recommended",
                _ => "Untitled section"
            };

            var section = new HomeSection
            {
                Title = title,
                Subtitle = entry.Data?.Subtitle?.TransformedLabel,
                SectionType = sectionType,
                SectionUri = entry.Uri ?? "",
                RawSpotifyJson = sectionIndex < rawSections.Count ? rawSections[sectionIndex] : null
            };

            if (entry.SectionItems?.Items != null)
            {
                foreach (var itemEntry in entry.SectionItems.Items)
                {
                    var item = MapSectionItem(itemEntry);
                    if (item != null)
                        section.Items.Add(item);
                }
            }

            if (section.Items.Count > 0)
            {
                // Pull a visual-identity accent from the first item that
                // carries an extracted dark color. Brushes are built later
                // by section.ApplyTheme(isDark) on the instance side
                // (PopulateSectionsChunkedAsync / HomeViewModel.ApplyTheme)
                // — this method is static so it can't read _isDarkTheme.
                section.AccentColorHex = section.Items
                    .FirstOrDefault(i => !string.IsNullOrEmpty(i.ColorHex))?.ColorHex;

                sections.Add(section);
            }
        }

        return sections;
    }

    private static HomeSectionItem? MapSectionItem(HomeSectionItemEntry entry)
    {
        var content = entry.Content;
        if (content == null) return null;

        var result = content.TypeName switch
        {
            "ArtistResponseWrapper" => MapArtist(entry.Uri, content),
            "PlaylistResponseWrapper" => MapPlaylist(entry.Uri, content),
            "AlbumResponseWrapper" => MapAlbum(entry.Uri, content),
            "PodcastOrAudiobookResponseWrapper" => MapPodcast(entry.Uri, content),
            _ => (HomeSectionItem?)null
        };

        // If typed deserialization failed or returned incomplete data, try raw JsonElement extraction
        if (result == null || result.Title == null)
        {
            var hasData = content.Data.HasValue;
            var kind = hasData ? content.Data!.Value.ValueKind : (System.Text.Json.JsonValueKind?)null;
            System.Diagnostics.Debug.WriteLine(
                $"[MapSectionItem] Fallback for {entry.Uri}: result={result != null}, title={result?.Title}, hasData={hasData}, kind={kind}");

            if (hasData && content.Data!.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                // Skip items the API marks as "NotFound" — not available for this platform
                if (content.Data!.Value.TryGetProperty("__typename", out var tn)
                    && tn.GetString() == "NotFound")
                {
                    System.Diagnostics.Debug.WriteLine($"[MapSectionItem] Skipping NotFound item: {entry.Uri}");
                    return null;
                }

                result ??= new HomeSectionItem { Uri = entry.Uri, ContentType = InferContentType(entry.Uri) };
                EnrichFromRawJson(result, content.Data!.Value);
            }
        }

        return result ?? MapUnknownType(entry.Uri);
    }

    /// <summary>
    /// Extracts common fields directly from the raw JsonElement when typed deserialization fails.
    /// </summary>
    private static void EnrichFromRawJson(HomeSectionItem item, System.Text.Json.JsonElement raw)
    {
        if (raw.ValueKind != System.Text.Json.JsonValueKind.Object)
            return;

        // Diagnostic: log the actual properties in the JsonElement
        System.Diagnostics.Debug.WriteLine(
            $"[EnrichFromRawJson] uri={item.Uri}, rawText={raw.GetRawText()[..Math.Min(200, raw.GetRawText().Length)]}");

        if (item.Title == null && raw.TryGetProperty("name", out var name))
            item.Title = name.GetString();

        if (item.Uri == null && raw.TryGetProperty("uri", out var uri))
            item.Uri = uri.GetString();

        if (item.ImageUrl == null)
            item.ImageUrl = ExtractImageUrlFromJson(raw);

        if (item.Subtitle == null && raw.TryGetProperty("description", out var desc))
        {
            var descStr = SpotifyHtmlHelper.StripHtml(desc.GetString());
            if (!string.IsNullOrEmpty(descStr))
                item.Subtitle = descStr;
        }

        if (item.ColorHex == null)
            item.ColorHex = ExtractColorFromJson(raw);

        // If top-level extraction found nothing, try nested "data" wrapper (double-wrapped items)
        if (item.Title == null && raw.TryGetProperty("data", out var nested)
            && nested.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            EnrichFromRawJson(item, nested);
        }
    }

    private static string? ExtractImageUrlFromJson(System.Text.Json.JsonElement raw)
    {
        if (raw.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        // Playlist: images.items[0].sources
        if (raw.TryGetProperty("images", out var images)
            && images.TryGetProperty("items", out var items)
            && items.ValueKind == System.Text.Json.JsonValueKind.Array
            && items.GetArrayLength() > 0)
        {
            var url = GetLargestSourceUrl(items[0]);
            if (url != null) return url;
        }

        // Album/Podcast: coverArt.sources
        if (raw.TryGetProperty("coverArt", out var coverArt))
        {
            var url = GetLargestSourceUrl(coverArt);
            if (url != null) return url;
        }

        // Artist: visuals.avatarImage.sources
        if (raw.TryGetProperty("visuals", out var visuals)
            && visuals.TryGetProperty("avatarImage", out var avatar))
        {
            var url = GetLargestSourceUrl(avatar);
            if (url != null) return url;
        }

        return null;
    }

    private static string? GetLargestSourceUrl(System.Text.Json.JsonElement container)
    {
        if (container.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        if (!container.TryGetProperty("sources", out var sources)
            || sources.ValueKind != System.Text.Json.JsonValueKind.Array
            || sources.GetArrayLength() == 0)
            return null;

        string? bestUrl = null;
        int maxWidth = -1;
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            var width = source.TryGetProperty("width", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Number
                ? w.GetInt32() : 0;
            if (width > maxWidth || bestUrl == null)
            {
                maxWidth = width;
                bestUrl = source.TryGetProperty("url", out var url) ? url.GetString() : null;
            }
        }
        return bestUrl;
    }

    private static string? ExtractColorFromJson(System.Text.Json.JsonElement raw)
    {
        if (raw.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        // Try images.items[0].extractedColors.colorDark.hex
        if (raw.TryGetProperty("images", out var images)
            && images.TryGetProperty("items", out var items)
            && items.ValueKind == System.Text.Json.JsonValueKind.Array
            && items.GetArrayLength() > 0
            && items[0].ValueKind == System.Text.Json.JsonValueKind.Object
            && items[0].TryGetProperty("extractedColors", out var ec)
            && ec.ValueKind == System.Text.Json.JsonValueKind.Object
            && ec.TryGetProperty("colorDark", out var cd)
            && cd.ValueKind == System.Text.Json.JsonValueKind.Object
            && cd.TryGetProperty("hex", out var hex))
            return hex.GetString();

        // Try coverArt.extractedColors.colorDark.hex
        if (raw.TryGetProperty("coverArt", out var coverArt)
            && coverArt.ValueKind == System.Text.Json.JsonValueKind.Object
            && coverArt.TryGetProperty("extractedColors", out var ec2)
            && ec2.ValueKind == System.Text.Json.JsonValueKind.Object
            && ec2.TryGetProperty("colorDark", out var cd2)
            && cd2.ValueKind == System.Text.Json.JsonValueKind.Object
            && cd2.TryGetProperty("hex", out var hex2))
            return hex2.GetString();

        return null;
    }

    private static HomeContentType InferContentType(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return HomeContentType.Unknown;
        if (uri.Contains(":playlist:", StringComparison.Ordinal)) return HomeContentType.Playlist;
        if (uri.Contains(":album:", StringComparison.Ordinal)) return HomeContentType.Album;
        if (uri.Contains(":artist:", StringComparison.Ordinal)) return HomeContentType.Artist;
        if (uri.Contains(":show:", StringComparison.Ordinal)) return HomeContentType.Podcast;
        if (uri.Contains(":episode:", StringComparison.Ordinal)) return HomeContentType.Episode;
        return HomeContentType.Unknown;
    }

    private static HomeSectionItem? MapUnknownType(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;

        if (uri.Contains(":collection", StringComparison.OrdinalIgnoreCase))
        {
            return new HomeSectionItem
            {
                Uri = uri,
                Title = "Liked Songs",
                ContentType = HomeContentType.Playlist
            };
        }

        var parts = uri.Split(':');
        if (parts.Length < 2) return null;

        var type = parts[1];
        return new HomeSectionItem
        {
            Uri = uri,
            Title = type switch
            {
                "artist" => "Artist",
                "album" => "Album",
                "playlist" => "Playlist",
                _ => null
            },
            ContentType = type switch
            {
                "artist" => HomeContentType.Artist,
                "album" => HomeContentType.Album,
                "playlist" => HomeContentType.Playlist,
                _ => HomeContentType.Unknown
            }
        };
    }

    private static HomeSectionItem? MapArtist(string? uri, HomeItemContent content)
    {
        var data = content.GetArtistData();
        if (data == null) return null;

        var imageUrl = data.Visuals?.AvatarImage?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        var colorHex = data.Visuals?.AvatarImage?.ExtractedColors?.ColorDark?.Hex;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Profile?.Name,
            Subtitle = "Artist",
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Artist,
            ColorHex = colorHex
        };
    }

    private static HomeSectionItem? MapPlaylist(string? uri, HomeItemContent content)
    {
        var data = content.GetPlaylistData();
        if (data == null) return null;

        var imageUrl = data.Images?.Items?.FirstOrDefault()?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        var colorHex = data.Images?.Items?.FirstOrDefault()?.ExtractedColors?.ColorDark?.Hex;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = SpotifyHtmlHelper.StripHtml(data.Description) is { Length: > 0 } desc
                ? desc
                : data.OwnerV2?.Data?.Name,
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Playlist,
            ColorHex = colorHex
        };
    }

    private static HomeSectionItem? MapAlbum(string? uri, HomeItemContent content)
    {
        var data = content.GetAlbumData();
        if (data == null) return null;

        var imageUrl = data.CoverArt?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        var colorHex = data.CoverArt?.ExtractedColors?.ColorDark?.Hex;
        var artistName = data.Artists?.Items?.FirstOrDefault()?.Profile?.Name;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = artistName ?? "Album",
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Album,
            ColorHex = colorHex
        };
    }

    private static HomeSectionItem? MapPodcast(string? uri, HomeItemContent content)
    {
        var data = content.GetPodcastData();
        if (data == null) return null;

        var imageUrl = data.CoverArt?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = data.Publisher?.Name,
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Podcast
        };
    }

    // ── Navigation helpers (called from code-behind) ──

    public static void NavigateToItem(HomeSectionItem item, bool openInNewTab = false)
    {
        if (string.IsNullOrEmpty(item.Uri)) return;

        var parts = item.Uri.Split(':');
        if (parts.Length < 3) return;

        var type = parts[1]; // artist, playlist, album, show, etc.
        var id = item.Uri;   // full URI as ID

        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = id,
            Title = item.Title,
            Subtitle = item.Subtitle,
            ImageUrl = item.ImageUrl
        };

        switch (type)
        {
            case "collection" when item.Uri.Contains("your-episodes", StringComparison.OrdinalIgnoreCase):
                Helpers.Navigation.NavigationHelpers.OpenYourEpisodes(openInNewTab);
                break;
            case "collection":
                Helpers.Navigation.NavigationHelpers.OpenLikedSongs(openInNewTab);
                break;
            case "artist":
                Helpers.Navigation.NavigationHelpers.OpenArtist(param, item.Title ?? "Artist", openInNewTab);
                break;
            case "album":
                Helpers.Navigation.NavigationHelpers.OpenAlbum(param, item.Title ?? "Album", openInNewTab);
                break;
            case "playlist":
                Helpers.Navigation.NavigationHelpers.OpenPlaylist(param, item.Title ?? "Playlist", openInNewTab);
                break;
            case "user" when item.Uri.Contains(":collection", StringComparison.OrdinalIgnoreCase):
                Helpers.Navigation.NavigationHelpers.OpenLikedSongs(openInNewTab);
                break;
            case "page":
            case "section":
            case "genre":
                Helpers.Navigation.NavigationHelpers.OpenBrowsePage(param, openInNewTab);
                break;
        }
    }

    // ── Hero palette pipeline ──
    // The featured item already carries a `ColorHex` populated by the home
    // feed parser (Spotify ships pre-extracted dark/light/raw colours for
    // every cover via the home GraphQL response). Use it directly — no
    // additional Pathfinder fetch needed. ApplyTheme just rebuilds the
    // backdrop brush against the right alpha per theme.

    private Color? _heroBaseColor;

    /// <summary>
    /// Theme-aware backdrop refresh for the hero band. Called by the page on
    /// init and on ActualThemeChanged. Builds a soft palette wash by mixing
    /// the featured item's dominant colour with a theme-appropriate alpha.
    /// </summary>
    public void ApplyTheme(bool isDark)
    {
        _isDarkTheme = isDark;

        if (_heroBaseColor is Color bg)
        {
            // Lift the source colour first. Spotify's ExtractedColors.colorDark
            // is by spec near-black on most covers (the darkest swatch that
            // keeps contrast with white text), and pushing that through a 22%
            // alpha over a white surface lands at ~#dbdbdb — the wash dissolves
            // and the hero stops reading as a tinted band. Liked Songs only
            // looked right because its hard-coded #4B2A8A is already saturated.
            // Lift to the same target the accent line uses so every featured
            // item sits at comparable visibility.
            var lifted = TintColorHelper.BrightenForTint(bg, targetMax: 210);
            HeroBackdropBrush = new SolidColorBrush(Color.FromArgb(
                (byte)(isDark ? 90 : 56), lifted.R, lifted.G, lifted.B));
            HeroAccentLineBrush = new SolidColorBrush(Color.FromArgb(255, lifted.R, lifted.G, lifted.B));
        }
        else
        {
            HeroBackdropBrush = null;
            HeroAccentLineBrush = null;
        }

        // Propagate to per-section accents so each shelf header re-tints.
        Feed.ApplyThemeToSections(isDark);

        // Page bleed — a soft radial glow at the top-left of the page,
        // tinted from the first card's visual identity (or the first section
        // accent if items haven't reached the bound collection yet).
        var bleedHex = Sections
            .SelectMany(s => s.Items.Select(i => i.ColorHex))
            .FirstOrDefault(c => !string.IsNullOrEmpty(c))
            ?? Sections.FirstOrDefault(s => !string.IsNullOrEmpty(s.AccentColorHex))?.AccentColorHex;

        if (TintColorHelper.TryParseHex(bleedHex, out var bleedRaw))
        {
            var bleedLifted = TintColorHelper.BrightenForTint(bleedRaw, targetMax: 220);
            var radial = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.0, 0.0),
                GradientOrigin = new Windows.Foundation.Point(0.0, 0.0),
                RadiusX = 1.0,
                RadiusY = 1.0,
                MappingMode = Microsoft.UI.Xaml.Media.BrushMappingMode.RelativeToBoundingBox,
            };
            radial.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(isDark ? 130 : 80), bleedLifted.R, bleedLifted.G, bleedLifted.B), Offset = 0.0 });
            radial.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(isDark ? 60  : 40), bleedLifted.R, bleedLifted.G, bleedLifted.B), Offset = 0.5 });
            radial.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, bleedLifted.R, bleedLifted.G, bleedLifted.B), Offset = 1.0 });
            PageBleedBrush = radial;
        }
        else
        {
            PageBleedBrush = null;
        }
    }

    // ── Live carousel accent → page bleed ─────────────────────────────
    // HeroCarousel publishes CurrentAccent every InteractionTracker tick
    // (per-frame RGB lerp between adjacent slides' seed accents). The
    // page-level radial bleed follows that signal so the whole page
    // reads as one cohesive surface as slides transition.
    //
    // HeroBackdropBrush is intentionally NOT touched here — it's tied to
    // FeaturedItem and consumed by the mini-player + other downstream
    // surfaces; live-updating it would smear the wrong colour into them.

    private Color? _lastCarouselBleedAccent;
    private const int CarouselBleedDeltaThreshold = 4;

    /// <summary>
    /// Update <see cref="PageBleedBrush"/> from the live carousel accent.
    /// Throttles to skip updates with RGB delta &lt; 4/256 from the last
    /// applied colour so the 60 fps callback doesn't thrash a UI-thread
    /// brush.
    /// </summary>
    public void UpdatePageBleedFromCarousel(Color accent)
    {
        if (_lastCarouselBleedAccent is { } prev
            && Math.Abs(prev.R - accent.R) < CarouselBleedDeltaThreshold
            && Math.Abs(prev.G - accent.G) < CarouselBleedDeltaThreshold
            && Math.Abs(prev.B - accent.B) < CarouselBleedDeltaThreshold)
        {
            return;
        }

        _lastCarouselBleedAccent = accent;

        // Mirrors the bleed-build branch of ApplyTheme. The carousel-published
        // accent is already a final RGB colour (no hex parse needed) — just
        // lift for tint legibility.
        var lifted = TintColorHelper.BrightenForTint(accent, targetMax: 220);
        var radial = new RadialGradientBrush
        {
            Center = new Windows.Foundation.Point(0.0, 0.0),
            GradientOrigin = new Windows.Foundation.Point(0.0, 0.0),
            RadiusX = 1.0,
            RadiusY = 1.0,
            MappingMode = Microsoft.UI.Xaml.Media.BrushMappingMode.RelativeToBoundingBox,
        };
        radial.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(_isDarkTheme ? 130 : 80), lifted.R, lifted.G, lifted.B), Offset = 0.0 });
        radial.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(_isDarkTheme ? 60 : 40), lifted.R, lifted.G, lifted.B), Offset = 0.5 });
        radial.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, lifted.R, lifted.G, lifted.B), Offset = 1.0 });
        PageBleedBrush = radial;
    }

    /// <summary>Resets the carousel-bleed throttle so a fresh nav cycle
    /// always paints the next accent rather than skipping as below-threshold.</summary>
    public void ResetCarouselBleedThrottle() => _lastCarouselBleedAccent = null;

    private static Color? TryParseHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var trimmed = hex.TrimStart('#');
        if (trimmed.Length != 6) return null;
        try
        {
            var r = Convert.ToByte(trimmed[..2], 16);
            var g = Convert.ToByte(trimmed[2..4], 16);
            var b = Convert.ToByte(trimmed[4..6], 16);
            return Color.FromArgb(255, r, g, b);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        DetachLongLivedServices();
        WeakReferenceMessenger.Default.Unregister<HomeLocalFilesVisibilityChangedMessage>(this);

        Greeting.Dispose();
        Recommendations.Dispose();
        Feed.Dispose();

        HeroAdapter.Dispose();
    }

    /// <summary>
    /// Fetches the top-level Browse All surface (genres / moods / charts /…).
    /// Internal helper for <c>HomeHeroAdapter.LoadBrowseAsync</c> — keeps the
    /// session encapsulated in the VM rather than handing a session reference
    /// to the adapter.
    /// </summary>
    internal async Task<Wavee.Core.Http.Pathfinder.BrowseAllResponse?> FetchBrowseAllAsync(CancellationToken ct)
    {
        if (_homeFeedService is null) return null;
        return await _homeFeedService.GetBrowseAllAsync(ct).ConfigureAwait(false);
    }

    private bool _longLivedAttached;

    private void AttachLongLivedServices()
    {
        if (_longLivedAttached) return;
        _longLivedAttached = true;
        Recommendations.AttachRecentlyPlayedListener();
        Greeting.AttachAuthListener();
        Feed.PropertyChanged += OnFeedPropertyChanged;
    }

    private void DetachLongLivedServices()
    {
        if (!_longLivedAttached) return;
        _longLivedAttached = false;
        Recommendations.DetachRecentlyPlayedListener();
        Greeting.DetachAuthListener();
        Feed.PropertyChanged -= OnFeedPropertyChanged;
    }
}

public enum HomeSectionType { Shorts, Generic, RecentlyPlayed, Baseline }
public enum HomeContentType { Artist, Playlist, Album, Podcast, Episode, Unknown }

/// <summary>
/// Episode listening state from the Home GraphQL <c>playedState.state</c>.
/// Drives the bottom-row layout of the episode card (date+duration vs progress
/// bar vs played check).
/// </summary>
public enum EpisodePlayedState { NotStarted, InProgress, Completed }

public sealed partial class HomeSection : ObservableObject
{
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public HomeSectionType SectionType { get; set; }
    public string SectionUri { get; set; } = "";
    public ObservableCollection<HomeSectionItem> Items { get; set; } = [];
    public string? RawSpotifyJson { get; set; }

    /// <summary>
    /// When non-null, a "View all" button is rendered in the section header
    /// and tapping it navigates to this URI. Used by the Wavee local-files
    /// section to surface a destination listing all indexed local content;
    /// Spotify-side sections leave this null.
    /// </summary>
    public string? ViewAllUri { get; set; }
    public bool HasViewAll => !string.IsNullOrEmpty(ViewAllUri);

#if DEBUG
    public bool IsDebugVisible => true;
#else
    public bool IsDebugVisible => false;
#endif

    /// <summary>
    /// Header entity name (e.g. artist name for "More like X" sections).
    /// </summary>
    public string? HeaderEntityName { get; set; }

    /// <summary>
    /// Header entity image (e.g. artist avatar for "More like X" sections).
    /// </summary>
    public string? HeaderEntityImageUrl { get; set; }

    /// <summary>
    /// Header entity URI for navigation.
    /// </summary>
    public string? HeaderEntityUri { get; set; }

    // ── Visual identity accent ──────────────────────────────────────────
    // Derived from the section's first item that carries an extracted
    // colorDark (Spotify Pathfinder visualIdentity). Drives the subtle
    // colored underline + soft backdrop wash on the section header so each
    // shelf reads with its own personality (Daily Mixes vs DJ vs Made For
    // X) instead of a uniform gray title bar.
    public string? AccentColorHex { get; set; }

    /// <summary>
    /// True when every item in the section is an Episode or Podcast. Drives
    /// (1) a fixed podcast-purple accent override on <see cref="AccentColorHex"/>
    /// so the shelf wash reads distinctly from album/playlist shelves, and
    /// (2) a small microphone glyph next to the section title in the header.
    /// Set by the parsers after <c>Items</c> is populated, and re-propagated
    /// by <c>HomeFeedCache.UpdateSectionInPlace</c> across diff updates so the
    /// header cannot keep a stale flag when items change to a non-podcast mix.
    /// </summary>
    private bool _isPodcastSection;
    public bool IsPodcastSection
    {
        get => _isPodcastSection;
        set => SetProperty(ref _isPodcastSection, value);
    }

    [ObservableProperty]
    private Brush? _accentLineBrush;

    [ObservableProperty]
    private Brush? _accentBackdropBrush;

    /// <summary>
    /// Slim fading streak — full-alpha accent on the left, transparent on
    /// the right. Renders as a 2px tall trailing line under the section
    /// title, giving the soft tinted backdrop a directional accent without
    /// the hard right edge a solid bar would have.
    /// </summary>
    [ObservableProperty]
    private Brush? _accentFadeBarBrush;

    /// <summary>
    /// Theme-aware refresh of the accent brushes. Mirrors the alpha cadence
    /// used by HomeViewModel.ApplyTheme for the hero so the section accent
    /// reads as "the same family" of palette wash as the page top.
    /// </summary>
    public void ApplyTheme(bool isDark)
    {
        if (!TintColorHelper.TryParseHex(AccentColorHex, out var raw))
        {
            AccentLineBrush = null;
            AccentBackdropBrush = null;
            AccentFadeBarBrush = null;
            return;
        }

        // Dark spotify "colorDark" values can collapse to near-black at
        // partial alpha. Lift them so the accent line stays legible.
        var lifted = TintColorHelper.BrightenForTint(raw, targetMax: 210);

        // Solid line: full alpha — reads as a clear "tag" mark rather than a
        // ghost. Width/height are set by the consuming XAML.
        AccentLineBrush = new SolidColorBrush(Color.FromArgb(
            255, lifted.R, lifted.G, lifted.B));

        // Backdrop: vertical fade from a stronger top tint to a near-zero
        // bottom tint. Pairs with the horizontal-fading streak below to
        // form a 2-axis gradient family (vertical here + horizontal there).
        // Vertical orientation keeps both ends bounded by the rounded corners
        // — no left/right edge cutoff issues like the earlier horizontal
        // gradient attempts.
        var backdrop = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint   = new Windows.Foundation.Point(0, 1),
        };
        backdrop.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(isDark ? 50 : 32), lifted.R, lifted.G, lifted.B), Offset = 0.0 });
        backdrop.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(isDark ? 12 :  6), lifted.R, lifted.G, lifted.B), Offset = 1.0 });
        AccentBackdropBrush = backdrop;

        // Fading streak: thin horizontal bar that goes solid → transparent
        // across the section width. Visual identity that doesn't have a hard
        // right edge to cut off. Lives just below the title row.
        var fade = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
        };
        fade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, lifted.R, lifted.G, lifted.B), Offset = 0.00 });
        fade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(180, lifted.R, lifted.G, lifted.B), Offset = 0.30 });
        fade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0,   lifted.R, lifted.G, lifted.B), Offset = 0.85 });
        AccentFadeBarBrush = fade;
    }
}

public sealed class HomeSectionItem : ObservableObject
{
    private string? _uri;
    private string? _title;
    private string? _subtitle;
    private string? _imageUrl;
    private string? _imageSmallUrl;
    private string? _imageMediumUrl;
    private string? _imageLargeUrl;
    private HomeContentType _contentType;
    private string? _colorHex;
    private string? _placeholderGlyph;
    private bool _isBaselineLoading;
    private bool _hasBaselinePreview;
    private string? _heroImageUrl;
    private string? _heroColorHex;
    private string? _canvasUrl;
    private string? _canvasThumbnailUrl;
    private string? _audioPreviewUrl;
    private string? _baselineGroupTitle;
    private List<HomeBaselinePreviewTrack> _previewTracks = [];

    public string? Uri
    {
        get => _uri;
        set => SetProperty(ref _uri, value);
    }

    public string? Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string? Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public string? ImageUrl
    {
        get => _imageUrl;
        set
        {
            if (SetProperty(ref _imageUrl, value))
                RaiseBestUrlPropertyChanged();
        }
    }

    /// <summary>
    /// CDN image variant ≲150 px wide. Distinct image-id from Medium/Large
    /// (different bytes, not a decode hint). Use via
    /// <see cref="SpotifyImageHelper.PickByDecodeSize"/> when the
    /// consumer knows its slot size.
    /// </summary>
    public string? ImageSmallUrl
    {
        get => _imageSmallUrl;
        set
        {
            if (SetProperty(ref _imageSmallUrl, value))
                RaiseBestUrlPropertyChanged();
        }
    }

    /// <summary>
    /// CDN image variant ~300 px wide.
    /// </summary>
    public string? ImageMediumUrl
    {
        get => _imageMediumUrl;
        set
        {
            if (SetProperty(ref _imageMediumUrl, value))
                RaiseBestUrlPropertyChanged();
        }
    }

    /// <summary>
    /// CDN image variant ≥500 px wide (typically 640).
    /// </summary>
    public string? ImageLargeUrl
    {
        get => _imageLargeUrl;
        set
        {
            if (SetProperty(ref _imageLargeUrl, value))
                RaiseBestUrlPropertyChanged();
        }
    }

    /// <summary>
    /// Best URL for a small slot (≤128 px decode — track row, avatar, pill).
    /// Falls back across flavors so card bindings always resolve to a usable
    /// URL even when Spotify only returned 1-2 sizes. Mirrors
    /// <see cref="SpotifyImageHelper.PickByDecodeSize"/> at decode size 64.
    /// </summary>
    public string? BestSmallImageUrl
        => _imageSmallUrl ?? _imageMediumUrl ?? _imageLargeUrl ?? _imageUrl;

    /// <summary>Best URL for a medium slot (~200-256 px — card / shelf tile).</summary>
    public string? BestMediumImageUrl
        => _imageMediumUrl ?? _imageSmallUrl ?? _imageLargeUrl ?? _imageUrl;

    /// <summary>Best URL for a large slot (≥512 px — hero / backdrop).</summary>
    public string? BestLargeImageUrl
        => _imageLargeUrl ?? _imageMediumUrl ?? _imageSmallUrl ?? _imageUrl;

    private void RaiseBestUrlPropertyChanged()
    {
        OnPropertyChanged(nameof(BestSmallImageUrl));
        OnPropertyChanged(nameof(BestMediumImageUrl));
        OnPropertyChanged(nameof(BestLargeImageUrl));
    }

    public HomeContentType ContentType
    {
        get => _contentType;
        set => SetProperty(ref _contentType, value);
    }

    public string? ColorHex
    {
        get => _colorHex;
        set => SetProperty(ref _colorHex, value);
    }

    public string? PlaceholderGlyph
    {
        get => _placeholderGlyph;
        set => SetProperty(ref _placeholderGlyph, value);
    }

    public bool IsBaselineLoading
    {
        get => _isBaselineLoading;
        set => SetProperty(ref _isBaselineLoading, value);
    }

    public bool HasBaselinePreview
    {
        get => _hasBaselinePreview;
        set => SetProperty(ref _hasBaselinePreview, value);
    }

    public string? HeroImageUrl
    {
        get => _heroImageUrl;
        set => SetProperty(ref _heroImageUrl, value);
    }

    public string? HeroColorHex
    {
        get => _heroColorHex;
        set => SetProperty(ref _heroColorHex, value);
    }

    public string? CanvasUrl
    {
        get => _canvasUrl;
        set => SetProperty(ref _canvasUrl, value);
    }

    public string? CanvasThumbnailUrl
    {
        get => _canvasThumbnailUrl;
        set => SetProperty(ref _canvasThumbnailUrl, value);
    }

    public string? AudioPreviewUrl
    {
        get => _audioPreviewUrl;
        set => SetProperty(ref _audioPreviewUrl, value);
    }

    public string? BaselineGroupTitle
    {
        get => _baselineGroupTitle;
        set => SetProperty(ref _baselineGroupTitle, value);
    }

    public List<HomeBaselinePreviewTrack> PreviewTracks
    {
        get => _previewTracks;
        set => SetProperty(ref _previewTracks, value);
    }

    // ── Liked Songs "X songs added" stack (Home Recents only) ──
    // Spotify renders the Liked Songs Recents tile as a fanned stack of the
    // three most-recently-added track covers behind the heart tile, with a
    // "{N} songs added" subtitle and a green check glyph. The data comes from
    // the home item's formatListAttributes.group_metadata (base64 protobuf):
    //   field 1 varint = added_count
    //   field 2 string repeat = up to 3 track URIs
    // See HomeResponseParserV2 for the decode.

    private int? _recentlyAddedCount;
    private string _recentlyAddedItemNoun = "song";
    private bool _isRecentlySaved;
    private IReadOnlyList<string> _recentlyAddedThumbnailUris = [];
    private string? _recentlyAddedThumbnail1Url;
    private string? _recentlyAddedThumbnail2Url;
    private string? _recentlyAddedThumbnail3Url;

    /// <summary>
    /// Number of items recently added to the entity (Liked Songs only today).
    /// Drives the "{N} songs added" subtitle.
    /// </summary>
    public int? RecentlyAddedCount
    {
        get => _recentlyAddedCount;
        set => SetProperty(ref _recentlyAddedCount, value);
    }

    public string RecentlyAddedItemNoun
    {
        get => _recentlyAddedItemNoun;
        set => SetProperty(ref _recentlyAddedItemNoun, string.IsNullOrWhiteSpace(value) ? "song" : value);
    }

    /// <summary>
    /// True when this Recents entry came from a "saved" event (a track was
    /// added to the collection) rather than a "played" event. Drives the
    /// green-check glyph + "added" wording vs the default play-history look.
    /// </summary>
    public bool IsRecentlySaved
    {
        get => _isRecentlySaved;
        set => SetProperty(ref _isRecentlySaved, value);
    }

    /// <summary>
    /// Up to 3 track URIs Spotify wants drawn as thumbnails behind the
    /// foreground tile. Resolution to actual cover image URLs happens
    /// asynchronously via the metadata cache; the resolved URLs land in the
    /// three Thumbnail*Url properties.
    /// </summary>
    public IReadOnlyList<string> RecentlyAddedThumbnailUris
    {
        get => _recentlyAddedThumbnailUris;
        set => SetProperty(ref _recentlyAddedThumbnailUris, value);
    }

    public string? RecentlyAddedThumbnail1Url
    {
        get => _recentlyAddedThumbnail1Url;
        set => SetProperty(ref _recentlyAddedThumbnail1Url, value);
    }

    public string? RecentlyAddedThumbnail2Url
    {
        get => _recentlyAddedThumbnail2Url;
        set => SetProperty(ref _recentlyAddedThumbnail2Url, value);
    }

    public string? RecentlyAddedThumbnail3Url
    {
        get => _recentlyAddedThumbnail3Url;
        set => SetProperty(ref _recentlyAddedThumbnail3Url, value);
    }

    // ── Episode / podcast metadata (Home only — not enriched live) ──
    // Populated by the Home parsers when an item carries an
    // EpisodeOrChapterResponseWrapper payload. The episode card binds these
    // OneWay; values do not refresh while playback advances — they refresh
    // on the next Home parse only (deliberate: keeps the card cheap).

    private long? _durationMs;
    private long? _playedPositionMs;
    private EpisodePlayedState? _playedState;
    private string? _publisherName;
    private bool _isVideoPodcast;
    private string? _releaseDateIso;

    /// <summary>Total episode duration in milliseconds. Null for non-episodes.</summary>
    public long? DurationMs
    {
        get => _durationMs;
        set => SetProperty(ref _durationMs, value);
    }

    /// <summary>Current play position in milliseconds (0 when NotStarted).</summary>
    public long? PlayedPositionMs
    {
        get => _playedPositionMs;
        set => SetProperty(ref _playedPositionMs, value);
    }

    /// <summary>Mapped from Home <c>playedState.state</c>.</summary>
    public EpisodePlayedState? PlayedState
    {
        get => _playedState;
        set => SetProperty(ref _playedState, value);
    }

    /// <summary>
    /// Publisher / show name for an episode. Used as the card's secondary line.
    /// For a standalone show, this carries the publisher name (Spotify hosts).
    /// </summary>
    public string? PublisherName
    {
        get => _publisherName;
        set => SetProperty(ref _publisherName, value);
    }

    /// <summary>True when the episode's mediaTypes include "VIDEO".</summary>
    public bool IsVideoPodcast
    {
        get => _isVideoPodcast;
        set => SetProperty(ref _isVideoPodcast, value);
    }

    /// <summary>Raw ISO-8601 release date — formatted at render time.</summary>
    public string? ReleaseDateIso
    {
        get => _releaseDateIso;
        set => SetProperty(ref _releaseDateIso, value);
    }
}

internal sealed record HomeBaselineEnrichment(
    string Uri,
    List<HomeBaselinePreviewTrack> PreviewTracks,
    string? HeroImageUrl,
    string? HeroColorHex,
    string? CanvasUrl,
    string? CanvasThumbnailUrl,
    string? AudioPreviewUrl);

public sealed class HomeBaselinePreviewTrack
{
    // `set;` (not `init;`) because the XAML type info generator scans every
    // public type in any namespace referenced by `xmlns:vm="using:...ViewModels"`
    // and emits setter shims for each property — init-only properties trip
    // CS8852 in XamlTypeInfo.g.cs. This type isn't bound from XAML, so the
    // looser accessor is harmless.
    public string? Uri { get; set; }
    public string? Name { get; set; }
    public string? CoverArtUrl { get; set; }
    public string? ColorHex { get; set; }
    public string? CanvasUrl { get; set; }
    public string? CanvasThumbnailUrl { get; set; }
    public string? AudioPreviewUrl { get; set; }
}

public sealed partial class HomeChipViewModel : ObservableObject
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public List<HomeChipViewModel> SubChips { get; set; } = [];

    /// <summary>True for the "✕ Parent" chip that reverts to main chips.</summary>
    public bool IsBackChip { get; set; }

    [ObservableProperty]
    private bool _isSelected;
}
