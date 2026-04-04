using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using DynamicData.Binding;
using Microsoft.Extensions.Logging;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ArtistViewModel : ObservableObject, ITabBarItemContent, IDisposable
{
    private readonly IArtistService _artistService;
    private readonly IAlbumService _albumService;
    private readonly ILocationService _locationService;
    private readonly IPlaybackService _playbackService;
    private readonly ITrackLikeService? _likeService;
    private readonly ILogger? _logger;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private readonly CompositeDisposable _disposables = new();
    private CancellationTokenSource? _discoCts;

    // ── Reactive data sources ──
    private readonly SourceCache<LazyTrackItem, string> _topTracksSource = new(t => t.Id);
    private readonly SourceCache<LazyReleaseItem, string> _releasesSource = new(r => r.Id);

    // ── Reactive output collections (bound to UI) ──
    private readonly ReadOnlyObservableCollection<LazyTrackItem> _topTracks;
    private readonly ReadOnlyObservableCollection<LazyReleaseItem> _albums;
    private readonly ReadOnlyObservableCollection<LazyReleaseItem> _singles;
    private readonly ReadOnlyObservableCollection<LazyReleaseItem> _compilations;

    public ReadOnlyObservableCollection<LazyTrackItem> TopTracks => _topTracks;
    public ReadOnlyObservableCollection<LazyReleaseItem> Albums => _albums;
    public ReadOnlyObservableCollection<LazyReleaseItem> Singles => _singles;
    public ReadOnlyObservableCollection<LazyReleaseItem> Compilations => _compilations;

    // ── Non-reactive collections (simple lists) ──
    [ObservableProperty]
    private ObservableCollection<RelatedArtistVm> _relatedArtists = [];

    [ObservableProperty]
    private ObservableCollection<ConcertVm> _concerts = [];

    [ObservableProperty]
    private ObservableCollection<LocationSearchResultVm> _locationSuggestions = [];

    [ObservableProperty]
    private string? _userLocationName;

    // ── Scalar properties ──

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _artistId;
    [ObservableProperty] private string? _artistName;
    [ObservableProperty] private string? _artistImageUrl;
    [ObservableProperty] private string? _headerImageUrl;
    [ObservableProperty] private string? _monthlyListeners;
    [ObservableProperty] private long _followers;
    [ObservableProperty] private string? _biography;
    [ObservableProperty] private bool _isVerified;
    [ObservableProperty] private bool _isFollowing;

    // Latest release
    [ObservableProperty] private string? _latestReleaseName;
    [ObservableProperty] private string? _latestReleaseImageUrl;
    [ObservableProperty] private string? _latestReleaseUri;
    [ObservableProperty] private string? _latestReleaseDate;
    [ObservableProperty] private int _latestReleaseTrackCount;
    [ObservableProperty] private string? _latestReleaseType;

    // Per-group total counts
    [ObservableProperty] private int _albumsTotalCount;
    [ObservableProperty] private int _singlesTotalCount;
    [ObservableProperty] private int _compilationsTotalCount;

    // Per-group view mode (Grid vs List)
    [ObservableProperty] private bool _albumsGridView = true;
    [ObservableProperty] private bool _singlesGridView = true;
    [ObservableProperty] private bool _compilationsGridView = true;

    // Per-group error state (background pagination failures)
    [ObservableProperty] private bool _hasAlbumsError;
    [ObservableProperty] private bool _hasSinglesError;
    [ObservableProperty] private bool _hasCompilationsError;

    // Top tracks pagination (Apple Music style)
    private const int RowsPerPage = 3;
    [ObservableProperty] private int _columnCount = 4;
    [ObservableProperty] private int _currentPage;

    private int TracksPerPage => RowsPerPage * ColumnCount;
    public int TotalPages => TopTracks.Count == 0 ? 0 : (int)Math.Ceiling((double)TopTracks.Count / TracksPerPage);

    public IEnumerable<LazyTrackItem> PagedTopTracks =>
        TopTracks.Skip(CurrentPage * TracksPerPage).Take(TracksPerPage);

    // ── Expanded album detail ──
    [ObservableProperty] private LazyReleaseItem? _expandedAlbum;
    [ObservableProperty] private ObservableCollection<LazyTrackItem> _expandedAlbumTracks = [];
    [ObservableProperty] private bool _isLoadingExpandedTracks;

    // Pinned item + Watch feed
    [ObservableProperty] private ArtistPinnedItemResult? _pinnedItem;
    [ObservableProperty] private ArtistWatchFeedResult? _watchFeed;
    public bool HasPinnedItem => PinnedItem != null;
    public bool HasWatchFeed => WatchFeed != null;
    public bool HasConcerts => Concerts.Count > 0;

    // ── Location operations (delegated to ILocationService) ──

    public async Task<List<LocationSearchResult>> SearchLocationsAsync(string query, CancellationToken ct = default)
        => await _locationService.SearchAsync(query, ct);

    public async Task SaveLocationAsync(string geonameId, string? cityName)
    {
        await _locationService.SaveByGeonameIdAsync(geonameId, cityName);
        UserLocationName = cityName ?? _locationService.CurrentCity;
        RefreshNearUserFlags();
    }

    /// <summary>
    /// Resolves device GPS to a city name but does NOT save yet.
    /// The UI should confirm with the user before calling SaveLocationAsync.
    /// </summary>
    public async Task<LocationSearchResult?> ResolveCurrentLocationAsync()
    {
        try
        {
            var geolocator = new Windows.Devices.Geolocation.Geolocator();
            var position = await geolocator.GetGeopositionAsync();
            var lat = position.Coordinate.Point.Position.Latitude;
            var lon = position.Coordinate.Point.Position.Longitude;

            var results = await _locationService.SearchByCoordinatesAsync(lat, lon);
            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve current location");
            return null;
        }
    }

    public void RefreshNearUserFlags()
    {
        foreach (var c in Concerts)
            c.IsNearUser = _locationService.IsNearUser(c.City);
    }

    // ── Tab management ──
    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    // ── Constructor (reactive pipelines) ──

    public ArtistViewModel(IArtistService artistService, IAlbumService albumService, ILocationService locationService, IPlaybackService playbackService, ITrackLikeService? likeService = null, ILogger<ArtistViewModel>? logger = null)
    {
        _artistService = artistService;
        _albumService = albumService;
        _locationService = locationService;
        _playbackService = playbackService;
        _likeService = likeService;
        _logger = logger;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // React to follow state changes from other sources (Dealer WebSocket, other pages)
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;

        // Top tracks: bind in insertion order (already ordered by popularity from API)
        _topTracksSource.Connect()
            .Bind(out _topTracks)
            .Subscribe()
            .DisposeWith(_disposables);

        // Albums: filter + sort by date desc
        // Single AutoRefresh on Data (captures both filter and sort changes on Populate)
        _releasesSource.Connect()
            .AutoRefresh(r => r.Data)
            .Filter(r => r.Data?.Type == "ALBUM" || (!r.IsLoaded && r.Id.StartsWith("album-ph")))
            .SortAndBind(out _albums, SortExpressionComparer<LazyReleaseItem>.Descending(r => r.Data?.ReleaseDate ?? DateTimeOffset.MinValue))
            .Subscribe()
            .DisposeWith(_disposables);

        // Singles
        _releasesSource.Connect()
            .AutoRefresh(r => r.Data)
            .Filter(r => r.Data?.Type == "SINGLE" || (!r.IsLoaded && r.Id.StartsWith("single-ph")))
            .SortAndBind(out _singles, SortExpressionComparer<LazyReleaseItem>.Descending(r => r.Data?.ReleaseDate ?? DateTimeOffset.MinValue))
            .Subscribe()
            .DisposeWith(_disposables);

        // Compilations
        _releasesSource.Connect()
            .AutoRefresh(r => r.Data)
            .Filter(r => r.Data?.Type == "COMPILATION" || (!r.IsLoaded && r.Id.StartsWith("comp-ph")))
            .SortAndBind(out _compilations, SortExpressionComparer<LazyReleaseItem>.Descending(r => r.Data?.ReleaseDate ?? DateTimeOffset.MinValue))
            .Subscribe()
            .DisposeWith(_disposables);
    }

    // ── Initialization ──

    public void Initialize(string artistId)
    {
        // Clear stale data from a previous artist on this cached page
        if (ArtistId != null && ArtistId != artistId)
            ResetForNewArtist();

        ArtistId = artistId;
        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Artist, artistId)
        {
            Title = "Artist"
        };
        RefreshFollowState();
    }

    private void ResetForNewArtist()
    {
        // Clear scalar properties so bindings don't flash old data
        ArtistName = null;
        ArtistImageUrl = null;
        HeaderImageUrl = null;
        MonthlyListeners = null;
        Followers = 0;
        Biography = null;
        IsVerified = false;
        IsFollowing = false;
        LatestReleaseName = null;
        LatestReleaseImageUrl = null;
        LatestReleaseUri = null;
        LatestReleaseDate = null;
        LatestReleaseTrackCount = 0;
        LatestReleaseType = null;
        PinnedItem = null;
        WatchFeed = null;
        HasData = false;
        CurrentPage = 0;
        ExpandedAlbum = null;
        ExpandedAlbumTracks.Clear();

        // Clear reactive collections
        _topTracksSource.Clear();
        _releasesSource.Clear();
        RelatedArtists.Clear();
        Concerts.Clear();
    }

    public void PrefillFrom(ContentNavigationParameter nav)
    {
        if (!string.IsNullOrEmpty(nav.Title)) ArtistName = nav.Title;
        if (!string.IsNullOrEmpty(nav.ImageUrl)) ArtistImageUrl = nav.ImageUrl;
    }

    // ── Load data from real Pathfinder API ──

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading || string.IsNullOrEmpty(ArtistId)) return;
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;
        HasAlbumsError = false;
        HasSinglesError = false;
        HasCompilationsError = false;

        try
        {
            var overview = await _artistService.GetOverviewAsync(ArtistId);

            // ── Map scalar properties ──
            ArtistName = overview.Name ?? ArtistName;
            // Only update image if not already prefilled (avoids flash during connected animation)
            if (string.IsNullOrEmpty(ArtistImageUrl))
                ArtistImageUrl = overview.ImageUrl ?? ArtistImageUrl;
            HeaderImageUrl = overview.HeaderImageUrl;
            MonthlyListeners = overview.MonthlyListeners > 0
                ? overview.MonthlyListeners.ToString("N0")
                : null;
            Followers = overview.Followers;
            Biography = overview.Biography;
            IsVerified = overview.IsVerified;

            // ── Latest release ──
            if (overview.LatestRelease != null)
            {
                LatestReleaseName = overview.LatestRelease.Name;
                LatestReleaseImageUrl = overview.LatestRelease.ImageUrl;
                LatestReleaseUri = overview.LatestRelease.Uri;
                LatestReleaseType = overview.LatestRelease.Type;
                LatestReleaseTrackCount = overview.LatestRelease.TrackCount;
                LatestReleaseDate = overview.LatestRelease.FormattedDate;
            }

            // ── Top tracks ──
            _topTracksSource.Edit(cache =>
            {
                cache.Clear();
                int idx = 1;

                foreach (var track in overview.TopTracks)
                {
                    var trackVm = new ArtistTopTrackVm
                    {
                        Id = track.Id,
                        Index = idx,
                        Title = track.Title,
                        Uri = track.Uri,
                        AlbumName = track.AlbumName,
                        AlbumImageUrl = track.AlbumImageUrl,
                        AlbumUri = track.AlbumUri,
                        Duration = track.Duration,
                        PlayCountRaw = track.PlayCount,
                        ArtistNames = track.ArtistNames,
                        IsExplicit = track.IsExplicit,
                        IsPlayable = track.IsPlayable,
                        HasVideo = track.HasVideo
                    };

                    cache.AddOrUpdate(LazyTrackItem.Loaded(trackVm.Id, idx, trackVm));
                    idx++;
                }

                // Pad + shimmer placeholders
                var loadedCount = idx - 1;
                var pageSize = TracksPerPage > 0 ? TracksPerPage : 12;
                var remainder = loadedCount % pageSize;
                var padCount = remainder > 0 ? pageSize - remainder : 0;
                for (int i = 0; i < padCount + pageSize; i++)
                {
                    cache.AddOrUpdate(LazyTrackItem.Placeholder($"placeholder-{idx}", idx));
                    idx++;
                }
            });

            // ── Extended top tracks (background, parallel) ──
            _ = LoadExtendedTopTracksAsync(ArtistId!);

            // ── Releases ──
            var albumsLoaded = overview.Albums.Count;
            var singlesLoaded = overview.Singles.Count;
            var compilationsLoaded = overview.Compilations.Count;

            _releasesSource.Edit(cache =>
            {
                cache.Clear();
                AddReleasesToCache(cache, overview.Albums, "ALBUM", "album-ph", overview.AlbumsTotalCount);
                AddReleasesToCache(cache, overview.Singles, "SINGLE", "single-ph", overview.SinglesTotalCount);
                AddReleasesToCache(cache, overview.Compilations, "COMPILATION", "comp-ph", overview.CompilationsTotalCount);
            });

            AlbumsTotalCount = overview.AlbumsTotalCount;
            SinglesTotalCount = overview.SinglesTotalCount;
            CompilationsTotalCount = overview.CompilationsTotalCount;

            // ── Background discography pagination ──
            _discoCts?.Cancel();
            _discoCts = new CancellationTokenSource();
            var discoToken = _discoCts.Token;

            // Run API fetches on thread pool, dispatch Populate() to UI thread
            _ = Task.Run(() => FetchRemainingDiscographyAsync(
                albumsLoaded, overview.AlbumsTotalCount,
                singlesLoaded, overview.SinglesTotalCount,
                compilationsLoaded, overview.CompilationsTotalCount,
                discoToken), discoToken);

            // ── Related artists ──
            RelatedArtists.Clear();
            foreach (var ra in overview.RelatedArtists)
            {
                RelatedArtists.Add(new RelatedArtistVm
                {
                    Id = ra.Id,
                    Uri = ra.Uri,
                    Name = ra.Name,
                    ImageUrl = ra.ImageUrl
                });
            }

            // ── Concerts ──
            Concerts.Clear();
            foreach (var c in overview.Concerts)
            {
                Concerts.Add(new ConcertVm
                {
                    Title = c.Title,
                    Venue = c.Venue,
                    City = c.City,
                    DateFormatted = c.Date != default
                        ? c.Date.ToString("MMM d").ToUpperInvariant()
                        : "",
                    DayOfWeek = c.Date != default
                        ? c.Date.ToString("ddd").ToUpperInvariant()
                        : "",
                    Year = c.Date != default ? c.Date.Year.ToString() : "",
                    IsFestival = c.IsFestival,
                    IsNearUser = c.IsNearUser,
                    Uri = c.Uri
                });
            }
            OnPropertyChanged(nameof(HasConcerts));

            // ── User location name (for concerts header) ──
            UserLocationName = _locationService.CurrentCity;

            // ── Pinned item + Watch feed ──
            PinnedItem = overview.PinnedItem;
            WatchFeed = overview.WatchFeed;
            OnPropertyChanged(nameof(HasPinnedItem));
            OnPropertyChanged(nameof(HasWatchFeed));

            // ── Pagination ──
            CurrentPage = 0;
            NotifyPaginationChanged();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ErrorMapper.ToUserMessage(ex);
            _logger?.LogError(ex, "Failed to load artist {ArtistId}", ArtistId);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Mapping helpers ──

    private static void AddReleasesToCache(
        ISourceUpdater<LazyReleaseItem, string> cache,
        List<ArtistReleaseResult> releases,
        string type,
        string phPrefix,
        int totalCount)
    {
        int count = 0;
        foreach (var r in releases)
        {
            var vm = new ArtistReleaseVm
            {
                Id = r.Id,
                Uri = r.Uri,
                Name = r.Name,
                Type = type,
                ImageUrl = r.ImageUrl,
                ReleaseDate = r.ReleaseDate,
                TrackCount = r.TrackCount,
                Label = r.Label,
                Year = r.Year
            };
            cache.AddOrUpdate(LazyReleaseItem.Loaded(vm.Id, count, vm));
            count++;
        }

        // Add shimmer placeholders for remaining items (capped at 20 to avoid AutoRefresh overhead)
        var maxPlaceholders = Math.Min(totalCount - count, 20);
        for (int i = count; i < count + maxPlaceholders; i++)
            cache.AddOrUpdate(LazyReleaseItem.Placeholder($"{phPrefix}-{i}", i));
    }

    // ── Background discography pagination ──

    private async Task FetchRemainingDiscographyAsync(
        int albumsLoaded, int albumsTotal,
        int singlesLoaded, int singlesTotal,
        int compilationsLoaded, int compilationsTotal,
        CancellationToken ct)
    {
        // Each group handles its own errors (removes placeholders + sets error flag)
        var tasks = new List<Task>();

        if (albumsLoaded < albumsTotal)
            tasks.Add(FetchDiscographyGroupAsync(ArtistId!,
                "ALBUM", "album-ph", albumsLoaded, albumsTotal, ct));

        if (singlesLoaded < singlesTotal)
            tasks.Add(FetchDiscographyGroupAsync(ArtistId!,
                "SINGLE", "single-ph", singlesLoaded, singlesTotal, ct));

        if (compilationsLoaded < compilationsTotal)
            tasks.Add(FetchDiscographyGroupAsync(ArtistId!,
                "COMPILATION", "comp-ph", compilationsLoaded, compilationsTotal, ct));

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    // ── Album expand/collapse ──

    [RelayCommand]
    private async Task ExpandAlbum(LazyReleaseItem? album)
    {
        if (album == null || !album.IsLoaded || album.Data == null) return;

        // Toggle: clicking same album collapses
        if (ExpandedAlbum?.Id == album.Id)
        {
            CollapseAlbum();
            return;
        }

        ExpandedAlbum = album;
        IsLoadingExpandedTracks = true;
        ExpandedAlbumTracks.Clear();

        // Add shimmer placeholders immediately
        var trackCount = album.Data.TrackCount;
        if (trackCount <= 0)
        {
            trackCount = album.Data.Type switch
            {
                "SINGLE" => 2,
                "COMPILATION" => 20,
                _ => 12
            };
        }

        for (int i = 0; i < trackCount; i++)
            ExpandedAlbumTracks.Add(LazyTrackItem.Placeholder($"expanded-{i}", i + 1));

        // Fetch real tracks and populate placeholders
        try
        {
            var albumUri = album.Data.Uri ?? $"spotify:album:{album.Data.Id}";
            var tracks = await _albumService.GetTracksAsync(albumUri);

            for (int i = 0; i < Math.Min(tracks.Count, ExpandedAlbumTracks.Count); i++)
            {
                // Replace placeholder with properly-keyed loaded item so track ID matches
                // for playback state highlighting (LazyTrackItem.Id is init-only)
                ExpandedAlbumTracks[i] = LazyTrackItem.Loaded(tracks[i].Id, i + 1, tracks[i]);
            }

            // Remove excess placeholders
            while (ExpandedAlbumTracks.Count > tracks.Count)
                ExpandedAlbumTracks.RemoveAt(ExpandedAlbumTracks.Count - 1);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load album tracks for {AlbumUri}", album.Data.Uri);
        }
        finally
        {
            IsLoadingExpandedTracks = false;
        }
    }

    [RelayCommand]
    private void CollapseAlbum()
    {
        ExpandedAlbum = null;
        IsLoadingExpandedTracks = false;
    }

    // ── Background discography pagination ──

    private async Task FetchDiscographyGroupAsync(
        string artistUri,
        string type,
        string phPrefix,
        int alreadyLoaded,
        int totalCount,
        CancellationToken ct)
    {
        try
        {
            const int pageSize = 20;
            var offset = alreadyLoaded;

            // Collect all pages on background thread first — no UI thread blocking
            var allReleases = new List<(int Offset, List<ArtistReleaseResult> Items)>();
            while (offset < totalCount)
            {
                ct.ThrowIfCancellationRequested();
                var releases = await _artistService.GetDiscographyPageAsync(artistUri, type, offset, pageSize, ct);
                if (releases.Count == 0) break;
                allReleases.Add((offset, releases));
                offset += releases.Count;
            }

            if (allReleases.Count == 0) return;

            // Single UI thread update with all pages batched
            var tcs = new TaskCompletionSource();
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _releasesSource.Edit(cache =>
                    {
                        foreach (var (pageOffset, releases) in allReleases)
                        {
                            int idx = pageOffset;
                            foreach (var r in releases)
                            {
                                var vm = new ArtistReleaseVm
                                {
                                    Id = r.Id,
                                    Uri = r.Uri,
                                    Name = r.Name,
                                    Type = type,
                                    ImageUrl = r.ImageUrl,
                                    ReleaseDate = r.ReleaseDate,
                                    TrackCount = r.TrackCount,
                                    Label = r.Label,
                                    Year = r.Year
                                };

                                var phKey = $"{phPrefix}-{idx}";
                                var existing = cache.Lookup(phKey);
                                if (existing.HasValue)
                                    existing.Value.Populate(vm);
                                else
                                    cache.AddOrUpdate(LazyReleaseItem.Loaded(r.Id, idx, vm));
                                idx++;
                            }
                        }
                    });
                    tcs.SetResult();
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            await tcs.Task;
        }
        catch (OperationCanceledException) { /* navigated away */ }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Discography {Type} fetch failed for {ArtistId}", type, artistUri);

            // Remove shimmer placeholders for this group so they don't stay forever
            var tcsCleanup = new TaskCompletionSource();
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _releasesSource.Edit(cache =>
                    {
                        var placeholders = cache.Items
                            .Where(i => !i.IsLoaded && i.Id.StartsWith(phPrefix))
                            .Select(i => i.Id).ToList();
                        cache.RemoveKeys(placeholders);
                    });

                    // Set per-group error flag
                    switch (type)
                    {
                        case "ALBUM": HasAlbumsError = true; break;
                        case "SINGLE": HasSinglesError = true; break;
                        case "COMPILATION": HasCompilationsError = true; break;
                    }

                    tcsCleanup.SetResult();
                }
                catch (Exception cleanupEx) { tcsCleanup.SetException(cleanupEx); }
            });

            try { await tcsCleanup.Task; }
            catch (Exception cleanupEx2) { _logger?.LogDebug(cleanupEx2, "Discography cleanup failed (non-critical)"); }
        }
    }

    // ── Commands ──

    [RelayCommand]
    private async Task RetryAsync()
    {
        HasError = false;
        ErrorMessage = null;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RetryDiscographyAsync()
    {
        var albumsLoaded = Albums.Count(a => a.IsLoaded);
        var singlesLoaded = Singles.Count(s => s.IsLoaded);
        var compilationsLoaded = Compilations.Count(c => c.IsLoaded);

        HasAlbumsError = false;
        HasSinglesError = false;
        HasCompilationsError = false;

        _discoCts?.Cancel();
        _discoCts = new CancellationTokenSource();
        var ct = _discoCts.Token;

        await Task.Run(() => FetchRemainingDiscographyAsync(
            albumsLoaded, AlbumsTotalCount,
            singlesLoaded, SinglesTotalCount,
            compilationsLoaded, CompilationsTotalCount,
            ct), ct);
    }

    [RelayCommand]
    private void ToggleFollow()
    {
        if (string.IsNullOrEmpty(ArtistId) || _likeService == null) return;
        var wasSaved = IsFollowing;
        IsFollowing = !wasSaved; // Optimistic UI update
        _likeService.ToggleSave(SavedItemType.Artist, ArtistId, wasSaved);
    }

    private void RefreshFollowState()
    {
        if (!string.IsNullOrEmpty(ArtistId) && _likeService != null)
            IsFollowing = _likeService.IsSaved(SavedItemType.Artist, ArtistId);
    }

    private void OnSaveStateChanged()
    {
        _dispatcherQueue?.TryEnqueue(RefreshFollowState);
    }

    [RelayCommand]
    private async Task PlayTopTracksAsync()
    {
        if (string.IsNullOrEmpty(ArtistId)) return;
        var result = await _playbackService.PlayContextAsync(
            ArtistId,
            new Data.Models.PlayContextOptions { PlayOriginFeature = "artist_page" });
        if (!result.IsSuccess)
            _logger?.LogWarning("PlayTopTracks failed: {Error}", result.ErrorMessage);
    }

    [RelayCommand]
    private async Task PlayTrackAsync(ITrackItem? track)
    {
        if (track == null || string.IsNullOrEmpty(ArtistId)) return;

        // Build full track list so extended tracks beyond the Spotify artist context play correctly
        var allTrackUris = TopTracks
            .Where(t => t.IsLoaded && t.Data != null)
            .Select(t => t.Data!.Uri)
            .Where(uri => !string.IsNullOrEmpty(uri))
            .ToList();

        var startIndex = allTrackUris.IndexOf(track.Uri);
        if (startIndex >= 0)
        {
            await _playbackService.PlayTracksAsync(allTrackUris, startIndex);
        }
        else
        {
            // Track not in loaded list — play within artist context as fallback
            await _playbackService.PlayTrackInContextAsync(track.Uri, ArtistId,
                new Data.Models.PlayContextOptions { PlayOriginFeature = "artist_page" });
        }
    }

    [RelayCommand]
    private void ToggleAlbumsView() => AlbumsGridView = !AlbumsGridView;

    [RelayCommand]
    private void ToggleSinglesView() => SinglesGridView = !SinglesGridView;

    [RelayCommand]
    private void ToggleCompilationsView() => CompilationsGridView = !CompilationsGridView;

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < TotalPages - 1)
            CurrentPage++;
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPage > 0)
            CurrentPage--;
    }

    // ── Pagination notifications ──

    private void NotifyPaginationChanged()
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PagedTopTracks));
    }

    partial void OnCurrentPageChanged(int value) => NotifyPaginationChanged();

    partial void OnColumnCountChanged(int value)
    {
        CurrentPage = 0;
        NotifyPaginationChanged();
    }

    partial void OnArtistNameChanged(string? value)
    {
        if (TabItemParameter != null && !string.IsNullOrEmpty(value))
        {
            TabItemParameter.Title = value;
            ContentChanged?.Invoke(this, TabItemParameter);
        }
    }

    // ── Cleanup ──

    /// <summary>
    /// Background-loads extended top tracks and replaces shimmer placeholders.
    /// </summary>
    private async Task LoadExtendedTopTracksAsync(string artistUri)
    {
        try
        {
            var extendedTracks = await _artistService.GetExtendedTopTracksAsync(artistUri);
            if (extendedTracks.Count == 0) return;

            // Get existing track URIs to avoid duplicates
            var existingUris = new HashSet<string>(
                _topTracksSource.Items
                    .Where(i => i.IsLoaded && i.Data != null)
                    .Select(i => ((ArtistTopTrackVm)i.Data!).Uri ?? ""));

            var startIdx = _topTracksSource.Items.Count(i => i.IsLoaded) + 1;

            _dispatcherQueue?.TryEnqueue(() =>
            {
                _topTracksSource.Edit(cache =>
                {
                    // Remove all placeholder items
                    var placeholders = cache.Items
                        .Where(i => !i.IsLoaded)
                        .Select(i => i.Id)
                        .ToList();
                    cache.RemoveKeys(placeholders);

                    int idx = startIdx;
                    foreach (var track in extendedTracks)
                    {
                        if (existingUris.Contains(track.Uri ?? "")) continue;

                        var trackVm = new ArtistTopTrackVm
                        {
                            Id = track.Id,
                            Index = idx,
                            Title = track.Title,
                            Uri = track.Uri,
                            AlbumName = track.AlbumName,
                            AlbumImageUrl = track.AlbumImageUrl,
                            AlbumUri = track.AlbumUri,
                            Duration = track.Duration,
                            PlayCountRaw = track.PlayCount,
                            ArtistNames = track.ArtistNames,
                            IsExplicit = track.IsExplicit,
                            IsPlayable = track.IsPlayable,
                            HasVideo = track.HasVideo
                        };

                        cache.AddOrUpdate(LazyTrackItem.Loaded(trackVm.Id, idx, trackVm));
                        idx++;
                    }
                });

                // Refresh pagination
                OnPropertyChanged(nameof(TotalPages));
                OnPropertyChanged(nameof(PagedTopTracks));
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load extended top tracks for {Artist}", artistUri);
        }
    }

    public void Dispose()
    {
        if (_likeService != null)
            _likeService.SaveStateChanged -= OnSaveStateChanged;

        if (_discoCts is not null)
        {
            _discoCts?.Cancel();
            _discoCts?.Dispose();
            _discoCts = null;
        }

        _disposables.Dispose();
        _topTracksSource.Dispose();
        _releasesSource.Dispose();
    }
}

// ── View Models (UI-layer records) ──

public sealed class ArtistTopTrackVm : Data.Contracts.ITrackItem
{
    public required string Id { get; init; }
    public int Index { get; set; }
    public string? Uri { get; init; }
    public string? AlbumImageUrl { get; init; }
    public string? AlbumUri { get; init; }
    public long PlayCountRaw { get; init; }
    public bool IsPlayable { get; init; }
    public bool HasVideo { get; init; }

    // ── ITrackItem implementation ──
    string Data.Contracts.ITrackItem.Uri => Uri ?? $"spotify:track:{Id}";
    string Data.Contracts.ITrackItem.Title => Title ?? "";
    string Data.Contracts.ITrackItem.ArtistName =>
        PlayCountRaw > 0 ? PlayCountFormatted : (ArtistNames ?? "");
    string Data.Contracts.ITrackItem.ArtistId => "";
    string Data.Contracts.ITrackItem.AlbumName => AlbumName ?? "";
    string Data.Contracts.ITrackItem.AlbumId => AlbumUri ?? "";
    string? Data.Contracts.ITrackItem.ImageUrl => AlbumImageUrl;
    TimeSpan Data.Contracts.ITrackItem.Duration => Duration;
    bool Data.Contracts.ITrackItem.IsExplicit => IsExplicit;
    string Data.Contracts.ITrackItem.DurationFormatted => DurationFormatted;
    int Data.Contracts.ITrackItem.OriginalIndex => Index;
    bool Data.Contracts.ITrackItem.IsLoaded => true;
    bool Data.Contracts.ITrackItem.HasVideo => HasVideo;

    // ── Public properties ──
    public string? Title { get; init; }
    public string? AlbumName { get; init; }
    public string? ArtistNames { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsExplicit { get; init; }

    public string PlayCountFormatted => PlayCountRaw.ToString("N0");

    public string DurationFormatted =>
        Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");

    private bool _isLiked;
    public bool IsLiked
    {
        get => _isLiked;
        set => SetField(ref _isLiked, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public sealed class ArtistReleaseVm
{
    public string Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string Type { get; init; } // ALBUM, SINGLE, COMPILATION
    public string? ImageUrl { get; init; }
    public DateTimeOffset ReleaseDate { get; init; }
    public int TrackCount { get; init; }
    public string? Label { get; init; }
    public int Year { get; init; }
}

public sealed class RelatedArtistVm
{
    public string? Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
}

public sealed class ConcertVm : System.ComponentModel.INotifyPropertyChanged
{
    public string? Title { get; init; }
    public string? Venue { get; init; }
    public string? City { get; init; }
    public string? DateFormatted { get; init; } // "MAR 26"
    public string? DayOfWeek { get; init; }     // "WED"
    public string? Year { get; init; }
    public bool IsFestival { get; init; }
    public string? Uri { get; init; }

    private bool _isNearUser;
    public bool IsNearUser
    {
        get => _isNearUser;
        set
        {
            if (_isNearUser == value) return;
            _isNearUser = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsNearUser)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LocationSearchResultVm
{
    public string? Name { get; init; }
    public string? FullName { get; init; }
    public string? GeonameId { get; init; }
    public string? Country { get; init; }

    public override string ToString() => FullName ?? Name ?? "";
}
