using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Wavee.Core;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.Services.Infra;
using Wavee.UI.Services.Search;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Views;

namespace Wavee.UI.WinUI.ViewModels.Shell;

/// <summary>
/// Owns the omnibar search experience: search-text debounce, the three-section
/// suggestion list (Settings / Your Library / Spotify), the recent searches
/// fallback, link-paste handling (delegated to <see cref="LinkPreviewCoordinator"/>),
/// and the suggestion dispatchers (<see cref="OnSuggestionChosen"/> /
/// <see cref="OnSuggestionActionClicked"/>).
///
/// <para>Extracted from <c>ShellViewModel</c> as part of the shell decomposition.
/// The omnibar VM doesn't own tab state — it asks the parent for the active
/// page via the <c>activeFrameContentProvider</c> accessor when it needs to
/// re-search in place on <see cref="SearchPage"/>.</para>
/// </summary>
public sealed partial class OmnibarViewModel : ObservableObject
{
    private readonly ISearchService _searchService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly LinkPreviewCoordinator _linkPreview;
    private readonly OmnibarSuggestionCache _cache;
    private readonly OmnibarSuggestionRanker _ranker;
    private readonly IBackgroundWorkRunner _backgroundWork;
    private readonly Wavee.Local.ILocalLibraryService? _localLibrary;
    private readonly ILogger? _logger;
    private readonly Func<object?> _activeFrameContentProvider;
    private readonly IDispatcherService? _dispatcher;

    private readonly Debouncer _searchDebouncer = new(TimeSpan.FromMilliseconds(300));
    private string _activeSearchText = string.Empty;

    public OmnibarViewModel(
        ISearchService searchService,
        IPlaybackStateService playbackStateService,
        LinkPreviewCoordinator linkPreview,
        OmnibarSuggestionCache cache,
        OmnibarSuggestionRanker ranker,
        IBackgroundWorkRunner backgroundWork,
        Wavee.Local.ILocalLibraryService? localLibrary,
        Func<object?> activeFrameContentProvider,
        IDispatcherService? dispatcher = null,
        ILogger? logger = null)
    {
        _searchService = searchService;
        _playbackStateService = playbackStateService;
        _linkPreview = linkPreview;
        _cache = cache;
        _ranker = ranker;
        _backgroundWork = backgroundWork;
        _localLibrary = Wavee.UI.WinUI.Services.AppFeatureFlags.LocalFilesEnabled ? localLibrary : null;
        _activeFrameContentProvider = activeFrameContentProvider;
        _dispatcher = dispatcher;
        _logger = logger;

        _linkPreview.PreviewReady += OnLinkPreviewReady;
    }

    // ── Bindable state (XAML binds via Vm.Omnibar.X) ────────────────────────

    [ObservableProperty]
    private List<SearchSuggestionItem>? _searchSuggestions;

    /// <summary>
    /// Grouped suggestions for the three-section omnibar mode (Settings / Your library
    /// / Spotify). When this contains any items the Omnibar prefers grouped rendering
    /// over the flat <see cref="SearchSuggestions"/> list. Null/empty falls back to
    /// the legacy flat path used by recent searches and no-match fallback.
    /// </summary>
    [ObservableProperty]
    private List<SearchSuggestionGroup>? _suggestionGroups;

    [ObservableProperty]
    private bool _isSearchSuggestionsLoading;

    [ObservableProperty]
    private string? _searchSuggestionErrorMessage;

    // ── Public commands invoked from ShellPage code-behind ──────────────────

    /// <summary>
    /// Submit the current omnibar text. Recognizable Spotify links navigate
    /// directly to the entity; everything else opens <see cref="SearchPage"/>.
    /// </summary>
    public void Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        // URL / URI paste — navigate straight to the entity instead of searching for
        // the literal URL on the SearchPage. Uses whatever placeholder data we have;
        // destination pages prefill their hero from the URI.
        if (SpotifyLink.TryParse(query.Trim(), out var link))
        {
            _linkPreview.Cancel();
            ClearSearchSuggestionState();
            NavigateToLink(link, title: null, imageUrl: null);
            return;
        }

        _cache.InvalidateRecentSearches();

        // Navigate to search page with query
        NavigationHelpers.OpenSearch(query);
    }

    public async Task OnSearchTextChangedAsync(string text)
    {
        var normalizedText = text?.Trim() ?? string.Empty;
        _activeSearchText = normalizedText;

        try
        {
            // Fast path: Spotify URL / URI paste. Replaces the normal three-section
            // search with a single "Open link" suggestion that previews the entity.
            // Skip-ahead works regardless of current page (including SearchPage) — we
            // don't want to re-search for the literal URL.
            if (!string.IsNullOrWhiteSpace(normalizedText)
                && SpotifyLink.TryParse(normalizedText, out var link))
            {
                ApplyLinkPasteSuggestion(link, normalizedText);
                return;
            }

            // Text is not a link: drop any in-flight preview so a late-arriving result
            // can't overwrite the now-valid search suggestions.
            _linkPreview.Cancel();

            // If already on SearchPage, re-search directly instead of showing suggestions
            if (_activeFrameContentProvider() is SearchPage searchPage
                && !string.IsNullOrWhiteSpace(normalizedText))
            {
                ClearSearchSuggestionState();
                await _searchDebouncer.DebounceAsync(async _ =>
                {
                    await searchPage.ViewModel.LoadAsync(normalizedText);
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                // Empty → hide sectioned groups, show recent searches via flat list.
                _searchDebouncer.Cancel();
                SearchSuggestionErrorMessage = null;
                SuggestionGroups = null;

                if (_cache.TryGetRecentSearches(out var cachedRecents, out var recentCacheIsFresh))
                {
                    SearchSuggestions = cachedRecents;
                    IsSearchSuggestionsLoading = false;
                    if (recentCacheIsFresh)
                        return;

                    _backgroundWork.Run(_ => RefreshRecentSearchesSafeAsync(normalizedText), "OmnibarViewModel.RefreshRecentSearches");
                    return;
                }

                SearchSuggestions = null;
                IsSearchSuggestionsLoading = true;
                await RefreshRecentSearchesAsync(normalizedText);
            }
            else
            {
                SearchSuggestionErrorMessage = null;
                SearchSuggestions = null; // flat list is off when sectioned mode is active
                IsSearchSuggestionsLoading = true;

                // 1) Synchronous Settings filter — always ≤ 3 items, in-memory.
                var settingsItems = BuildSettingsSuggestions(normalizedText);

                // 2) Zero-network library quicksearch — broadened to AllCached so anything
                //    the user has seen/played is findable, not just explicitly-saved items.
                var libraryItems = await BuildLibrarySuggestionsAsync(normalizedText, CancellationToken.None);

                if (!string.Equals(_activeSearchText, normalizedText, StringComparison.Ordinal))
                    return;

                // 3) Try cached Spotify suggestions; show partial groups immediately when missing.
                List<SearchSuggestionItem>? spotifyItems = null;
                var queryCacheIsFresh = false;
                if (_cache.TryGetQuerySuggestions(normalizedText, out var cachedSpotify, out queryCacheIsFresh))
                {
                    spotifyItems = cachedSpotify;
                }

                SuggestionGroups = _ranker.BuildGroups(settingsItems, libraryItems, spotifyItems);
                IsSearchSuggestionsLoading = spotifyItems is null;

                if (spotifyItems is not null && queryCacheIsFresh)
                    return;

                // 4) Debounce 300ms then refresh the Spotify group.
                await _searchDebouncer.DebounceAsync(async ct =>
                {
                    await RefreshQuerySuggestionsAsync(normalizedText, ct);
                });
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("[Omnibar] Search suggestion query cancelled for \"{Query}\"", normalizedText);
        }
        catch (Exception ex)
        {
            ApplySearchSuggestionFailure(normalizedText, ex);
        }
    }

    public void RetrySearchSuggestions()
    {
        // Fire-and-forget — user-initiated retry. Exception logged inside the
        // task so the unobserved-task crash handler stops being the only net.
        _ = RunSearchTextChangedAsync(_activeSearchText);
    }

    public void OnSuggestionChosen(object? item)
    {
        if (item is not SearchSuggestionItem suggestion) return;
        if (suggestion.Type == SearchSuggestionType.SectionHeader) return; // defense-in-depth
        if (suggestion.Type == SearchSuggestionType.Shimmer) return;       // placeholder is non-interactive

        Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.RecordClickIntent("Omnibar." + suggestion.Type);

        _cache.InvalidateRecentSearches();

        switch (suggestion.Type)
        {
            case SearchSuggestionType.Artist:
                NavigationHelpers.OpenArtist(suggestion.Uri, suggestion.Title);
                break;
            case SearchSuggestionType.Album:
                NavigationHelpers.OpenAlbum(suggestion.Uri, suggestion.Title);
                break;
            case SearchSuggestionType.Playlist:
                NavigationHelpers.OpenPlaylist(suggestion.Uri, suggestion.Title);
                break;
            case SearchSuggestionType.Track:
                var trackId = suggestion.Uri.Replace("spotify:track:", "");
                _playbackStateService.PlayTrack(trackId);
                break;
            case SearchSuggestionType.TextQuery:
                var query = suggestion.Uri.Replace("spotify:search:", "").Replace("+", " ");
                NavigationHelpers.OpenSearch(query);
                break;

            // Omnibar link-paste destinations (entity types not produced by free-text search).
            case SearchSuggestionType.Podcast:
                NavigationHelpers.OpenShowPage(suggestion.Uri, suggestion.Title, subtitle: null, imageUrl: suggestion.ImageUrl);
                break;
            case SearchSuggestionType.Episode:
                NavigationHelpers.OpenEpisodePage(suggestion.Uri, suggestion.Title, suggestion.ImageUrl);
                break;
            case SearchSuggestionType.User:
                NavigationHelpers.OpenProfile(new Wavee.UI.WinUI.Data.Parameters.ContentNavigationParameter
                {
                    Uri = suggestion.Uri,
                    Title = suggestion.Title,
                    ImageUrl = suggestion.ImageUrl,
                }, suggestion.Title);
                break;
            case SearchSuggestionType.Genre:
                NavigationHelpers.OpenBrowsePage(new Wavee.UI.WinUI.Data.Parameters.ContentNavigationParameter
                {
                    Uri = suggestion.Uri,
                    Title = string.IsNullOrWhiteSpace(suggestion.Title) ? "Browse" : suggestion.Title,
                    ImageUrl = suggestion.ImageUrl,
                });
                break;
            case SearchSuggestionType.LinkAction:
                if (suggestion.Uri == "spotify:collection")
                    NavigationHelpers.OpenLikedSongs();
                else if (suggestion.Uri == "spotify:collection:your-episodes")
                    NavigationHelpers.OpenYourEpisodes();
                break;

            // Omnibar Settings deep-link — reuse the in-page filter via existing
            // NavigateToSearchEntry path on SettingsPage.OnNavigatedTo.
            case SearchSuggestionType.Setting:
                if (!string.IsNullOrEmpty(suggestion.ContextTag) && !string.IsNullOrEmpty(suggestion.GroupKey))
                {
                    NavigationHelpers.OpenSettings(new Wavee.UI.WinUI.Data.Parameters.SettingsNavigationParameter(
                        suggestion.ContextTag, suggestion.GroupKey, suggestion.Title));
                }
                else
                {
                    NavigationHelpers.OpenSettings();
                }
                break;

            // Your-library quicksearch results. URIs are either wavee:local:... (filesystem)
            // or spotify:... (cached Spotify saved items). The existing NavigationHelpers
            // already handle local URIs via the SearchPage merge path, so the same helpers
            // work for both.
            case SearchSuggestionType.LocalTrack:
                _playbackStateService.PlayTrack(suggestion.Uri);
                break;
            case SearchSuggestionType.LocalAlbum:
                NavigationHelpers.OpenAlbum(suggestion.Uri, suggestion.Title);
                break;
            case SearchSuggestionType.LocalArtist:
                NavigationHelpers.OpenArtist(suggestion.Uri, suggestion.Title);
                break;
            case SearchSuggestionType.LocalPlaylist:
                NavigationHelpers.OpenPlaylist(suggestion.Uri, suggestion.Title);
                break;

            default:
                NavigationHelpers.OpenSearch(suggestion.Title);
                break;
        }
    }

    public void OnSuggestionActionClicked(SearchSuggestionItem item)
    {
        switch (item.Type)
        {
            case SearchSuggestionType.Track:
                var trackId = item.Uri.Replace("spotify:track:", "");
                _playbackStateService.AddToQueue(trackId);
                break;
        }
    }

    // ── Suggestion fetch + cache plumbing ───────────────────────────────────

    private async Task RunSearchTextChangedAsync(string text)
    {
        try { await OnSearchTextChangedAsync(text).ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogError(ex, "OnSearchTextChangedAsync failed"); }
    }

    /// <summary>Maps the static SettingsPage entries through the omnibar query, capped at 3.</summary>
    private static List<SearchSuggestionItem> BuildSettingsSuggestions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<SearchSuggestionItem>();

        var results = new List<SearchSuggestionItem>();
        var count = 0;
        foreach (var entry in SettingsPage.SettingsSearchEntries)
        {
            if (!entry.Matches(query)) continue;
            results.Add(new SearchSuggestionItem
            {
                Title = entry.Title,
                Subtitle = entry.Section,
                Uri = $"wavee:setting:{entry.Tag}:{entry.GroupKey}",
                Type = SearchSuggestionType.Setting,
                ContextTag = entry.Tag,
                GroupKey = entry.GroupKey,
                QueryText = query,
            });
            count++;
            if (count >= 3) break;
        }
        return results;
    }

    /// <summary>
    /// Calls <see cref="Wavee.Local.ILocalLibraryService.SearchAsync"/> across all
    /// cached entities (local files + cached Spotify tracks/albums/artists/playlists). Maps
    /// each result to a Local* suggestion type so the dispatcher knows whether to play (Track),
    /// open Album/Artist/Playlist pages, etc.
    /// </summary>
    private async Task<List<SearchSuggestionItem>> BuildLibrarySuggestionsAsync(string query, CancellationToken ct)
    {
        if (_localLibrary is null || string.IsNullOrWhiteSpace(query))
            return new List<SearchSuggestionItem>();

        IReadOnlyList<Wavee.Local.LocalSearchResult> results;
        try
        {
            results = await _localLibrary.SearchAsync(
                query,
                limit: 8,
                Wavee.Local.LocalSearchScope.AllCached,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Local library quicksearch failed for \"{Query}\"", query);
            return new List<SearchSuggestionItem>();
        }

        var items = new List<SearchSuggestionItem>(results.Count);
        foreach (var r in results)
        {
            var subtitle = string.IsNullOrWhiteSpace(r.Subtitle)
                ? "Your library"
                : "Your library · " + r.Subtitle;

            items.Add(new SearchSuggestionItem
            {
                Title = r.Name,
                Subtitle = subtitle,
                ImageUrl = r.ArtworkUri,
                Uri = r.Uri,
                Type = r.Type switch
                {
                    Wavee.Local.LocalSearchEntityType.Track    => SearchSuggestionType.LocalTrack,
                    Wavee.Local.LocalSearchEntityType.Album    => SearchSuggestionType.LocalAlbum,
                    Wavee.Local.LocalSearchEntityType.Artist   => SearchSuggestionType.LocalArtist,
                    Wavee.Local.LocalSearchEntityType.Playlist => SearchSuggestionType.LocalPlaylist,
                    _ => SearchSuggestionType.LocalTrack,
                },
                QueryText = query,
            });
        }
        return items;
    }

    private async Task RefreshRecentSearchesAsync(string querySnapshot, CancellationToken ct = default)
    {
        var recents = await _searchService.GetRecentSearchesAsync(ct);
        _cache.StoreRecentSearches(recents);

        if (string.Equals(_activeSearchText, querySnapshot, StringComparison.Ordinal))
        {
            SearchSuggestionErrorMessage = null;
            IsSearchSuggestionsLoading = false;
            SearchSuggestions = _ranker.Clone(recents);
        }
    }

    private async Task RefreshQuerySuggestionsAsync(string querySnapshot, CancellationToken ct)
    {
        // Network leg: cache Spotify suggestions keyed by query (existing pattern).
        var spotifyItems = await _searchService.GetSuggestionsAsync(querySnapshot, ct);
        _cache.StoreQuerySuggestions(querySnapshot, spotifyItems);

        if (!string.Equals(_activeSearchText, querySnapshot, StringComparison.Ordinal))
            return;

        // Recompute the other two sections so they stay in sync with the current query.
        var settingsItems = BuildSettingsSuggestions(querySnapshot);
        var libraryItems = await BuildLibrarySuggestionsAsync(querySnapshot, ct);

        if (!string.Equals(_activeSearchText, querySnapshot, StringComparison.Ordinal))
            return;

        SearchSuggestionErrorMessage = null;
        IsSearchSuggestionsLoading = false;
        SuggestionGroups = _ranker.BuildGroups(settingsItems, libraryItems, _ranker.Clone(spotifyItems));
    }

    private async Task RefreshRecentSearchesSafeAsync(string querySnapshot)
    {
        try
        {
            await RefreshRecentSearchesAsync(querySnapshot);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ApplySearchSuggestionFailure(querySnapshot, ex);
        }
    }

    private void ClearSearchSuggestionState()
    {
        _searchDebouncer.Cancel();
        SearchSuggestionErrorMessage = null;
        IsSearchSuggestionsLoading = false;
        SearchSuggestions = null;
        SuggestionGroups = null;
    }

    // ── Spotify URL / URI paste handling (omnibar fast path) ────────────────

    /// <summary>
    /// Replaces the omnibar suggestions with a single "Open link" card for the parsed
    /// Spotify URL / URI, and kicks off an async metadata fetch to fill in the real
    /// title and cover art (when a link-preview service is available).
    /// </summary>
    private void ApplyLinkPasteSuggestion(SpotifyLink link, string rawText)
    {
        _searchDebouncer.Cancel();

        SearchSuggestionErrorMessage = null;
        SearchSuggestions = null;
        IsSearchSuggestionsLoading = false;

        SuggestionGroups = new List<SearchSuggestionGroup>
        {
            new(OmnibarSuggestionRanker.OpenLinkHeader, new List<SearchSuggestionItem>
            {
                BuildLinkSuggestion(link, rawText, preview: null),
            }),
        };

        _linkPreview.StartPreview(link, rawText);
    }

    private void OnLinkPreviewReady(LinkPreviewResult result)
    {
        // Marshal to UI thread — the coordinator fires PreviewReady from
        // whatever thread the work runner used; XAML-bound properties must
        // be updated on the dispatcher.
        if (_dispatcher is not null)
            _dispatcher.TryEnqueue(() => ApplyLinkPreview(result));
        else
            ApplyLinkPreview(result);
    }

    private void ApplyLinkPreview(LinkPreviewResult result)
    {
        // Bail out if the user typed past this URL while the fetch was in flight.
        if (!string.Equals(_activeSearchText, result.RawText, StringComparison.Ordinal)) return;

        SuggestionGroups = new List<SearchSuggestionGroup>
        {
            new(OmnibarSuggestionRanker.OpenLinkHeader, new List<SearchSuggestionItem>
            {
                BuildLinkSuggestion(result.Link, result.RawText, result.Preview),
            }),
        };
    }

    private static SearchSuggestionItem BuildLinkSuggestion(SpotifyLink link, string rawText, LinkPreview? preview)
    {
        var (placeholderTitle, placeholderSubtitle) = GetLinkPlaceholder(link.Kind);

        return new SearchSuggestionItem
        {
            Title = preview?.Title ?? placeholderTitle,
            Subtitle = preview?.Subtitle ?? placeholderSubtitle ?? TrimLinkForDisplay(rawText),
            ImageUrl = preview?.ImageUrl,
            Uri = link.CanonicalUri,
            Type = MapLinkKindToSuggestionType(link.Kind),
            QueryText = rawText,
        };
    }

    private static (string Title, string? Subtitle) GetLinkPlaceholder(SpotifyLinkKind kind) => kind switch
    {
        SpotifyLinkKind.Track        => ("Open track", "Track"),
        SpotifyLinkKind.Album        => ("Open album", "Album"),
        SpotifyLinkKind.Artist       => ("Open artist", "Artist"),
        SpotifyLinkKind.Playlist     => ("Open playlist", "Playlist"),
        SpotifyLinkKind.Show         => ("Open podcast", "Podcast"),
        SpotifyLinkKind.Episode      => ("Open episode", "Episode"),
        SpotifyLinkKind.User         => ("Open profile", "Profile"),
        SpotifyLinkKind.LikedSongs   => ("Liked Songs", "Playlist"),
        SpotifyLinkKind.YourEpisodes => ("Your Episodes", "Podcasts"),
        SpotifyLinkKind.Genre        => ("Open browse page", null),
        _                            => ("Open link", null),
    };

    private static SearchSuggestionType MapLinkKindToSuggestionType(SpotifyLinkKind kind) => kind switch
    {
        SpotifyLinkKind.Track        => SearchSuggestionType.Track,
        SpotifyLinkKind.Album        => SearchSuggestionType.Album,
        SpotifyLinkKind.Artist       => SearchSuggestionType.Artist,
        SpotifyLinkKind.Playlist     => SearchSuggestionType.Playlist,
        SpotifyLinkKind.Show         => SearchSuggestionType.Podcast,
        SpotifyLinkKind.Episode      => SearchSuggestionType.Episode,
        SpotifyLinkKind.User         => SearchSuggestionType.User,
        SpotifyLinkKind.Genre        => SearchSuggestionType.Genre,
        SpotifyLinkKind.LikedSongs   => SearchSuggestionType.LinkAction,
        SpotifyLinkKind.YourEpisodes => SearchSuggestionType.LinkAction,
        _                            => SearchSuggestionType.TextQuery,
    };

    private static string TrimLinkForDisplay(string raw)
    {
        const int max = 64;
        return raw.Length <= max ? raw : string.Concat(raw.AsSpan(0, max - 1), "…");
    }

    /// <summary>
    /// Direct-navigation path used when the user presses Enter without a suggestion
    /// selected. Mirrors the dispatch in <see cref="OnSuggestionChosen"/> but takes
    /// raw link data instead of a built suggestion.
    /// </summary>
    private void NavigateToLink(SpotifyLink link, string? title, string? imageUrl)
    {
        Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.RecordClickIntent("Omnibar.Link." + link.Kind);
        switch (link.Kind)
        {
            case SpotifyLinkKind.Track:
                _playbackStateService.PlayTrack(link.EntityId ?? string.Empty);
                break;
            case SpotifyLinkKind.Album:
                NavigationHelpers.OpenAlbum(link.CanonicalUri, title ?? "Album");
                break;
            case SpotifyLinkKind.Artist:
                NavigationHelpers.OpenArtist(link.CanonicalUri, title ?? "Artist");
                break;
            case SpotifyLinkKind.Playlist:
                NavigationHelpers.OpenPlaylist(link.CanonicalUri, title ?? "Playlist");
                break;
            case SpotifyLinkKind.Show:
                NavigationHelpers.OpenShowPage(link.CanonicalUri, title, subtitle: null, imageUrl: imageUrl);
                break;
            case SpotifyLinkKind.Episode:
                NavigationHelpers.OpenEpisodePage(link.CanonicalUri, title, imageUrl);
                break;
            case SpotifyLinkKind.User:
                NavigationHelpers.OpenProfile(new Wavee.UI.WinUI.Data.Parameters.ContentNavigationParameter
                {
                    Uri = link.CanonicalUri,
                    Title = title,
                    ImageUrl = imageUrl,
                }, title);
                break;
            case SpotifyLinkKind.LikedSongs:
                NavigationHelpers.OpenLikedSongs();
                break;
            case SpotifyLinkKind.YourEpisodes:
                NavigationHelpers.OpenYourEpisodes();
                break;
            case SpotifyLinkKind.Genre:
                NavigationHelpers.OpenBrowsePage(new Wavee.UI.WinUI.Data.Parameters.ContentNavigationParameter
                {
                    Uri = link.CanonicalUri,
                    Title = title ?? "Browse",
                    ImageUrl = imageUrl,
                });
                break;
        }
    }

    private void ApplySearchSuggestionFailure(string querySnapshot, Exception ex)
    {
        if (!string.Equals(_activeSearchText, querySnapshot, StringComparison.Ordinal))
            return;

        _logger?.LogWarning(ex, "Failed to fetch search suggestions");
        IsSearchSuggestionsLoading = false;

        // Spotify leg failed. Strip its shimmer placeholders so the section doesn't keep
        // pulsing forever. Keep partial Settings + Library groups visible — the user still
        // gets local results.
        var trimmed = _ranker.TrimShimmerGroups(SuggestionGroups);
        if (trimmed is not null)
        {
            SuggestionGroups = trimmed;
            return;
        }

        if (SearchSuggestions is { Count: > 0 } currentFlat
            && _ranker.DoSuggestionsMatchQuery(currentFlat, querySnapshot))
        {
            return;
        }

        SearchSuggestions = null;
        SuggestionGroups = null;
        SearchSuggestionErrorMessage = ErrorMapper.ToUserMessage(ex);
    }

    public void Dispose()
    {
        _searchDebouncer.Dispose();
        _linkPreview.PreviewReady -= OnLinkPreviewReady;
        _linkPreview.Dispose();
    }
}
