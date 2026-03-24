using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
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
    private readonly ILogger? _logger;
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

    // ── Scalar properties ──

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _artistId;
    [ObservableProperty] private string? _artistName;
    [ObservableProperty] private string? _artistImageUrl;
    [ObservableProperty] private string? _headerImageUrl;
    [ObservableProperty] private long _monthlyListeners;
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

    // ── Tab management ──
    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    // ── Constructor (reactive pipelines) ──

    public ArtistViewModel(IArtistService artistService, ILogger<ArtistViewModel>? logger = null)
    {
        _artistService = artistService;
        _logger = logger;

        // Top tracks: bind in insertion order (already ordered by popularity from API)
        _topTracksSource.Connect()
            .Bind(out _topTracks)
            .Subscribe()
            .DisposeWith(_disposables);

        // Albums: filter + sort by date desc
        // AutoRefresh ensures filter/sort re-evaluate when placeholders get populated
        _releasesSource.Connect()
            .AutoRefresh(r => r.IsLoaded)
            .AutoRefresh(r => r.Data)
            .Filter(r => r.Data?.Type == "ALBUM" || (!r.IsLoaded && r.Id.StartsWith("album-ph")))
            .Sort(SortExpressionComparer<LazyReleaseItem>.Descending(r => r.Data?.ReleaseDate ?? DateTimeOffset.MinValue))
            .Bind(out _albums)
            .Subscribe()
            .DisposeWith(_disposables);

        // Singles
        _releasesSource.Connect()
            .AutoRefresh(r => r.IsLoaded)
            .AutoRefresh(r => r.Data)
            .Filter(r => r.Data?.Type == "SINGLE" || (!r.IsLoaded && r.Id.StartsWith("single-ph")))
            .Sort(SortExpressionComparer<LazyReleaseItem>.Descending(r => r.Data?.ReleaseDate ?? DateTimeOffset.MinValue))
            .Bind(out _singles)
            .Subscribe()
            .DisposeWith(_disposables);

        // Compilations
        _releasesSource.Connect()
            .AutoRefresh(r => r.IsLoaded)
            .AutoRefresh(r => r.Data)
            .Filter(r => r.Data?.Type == "COMPILATION" || (!r.IsLoaded && r.Id.StartsWith("comp-ph")))
            .Sort(SortExpressionComparer<LazyReleaseItem>.Descending(r => r.Data?.ReleaseDate ?? DateTimeOffset.MinValue))
            .Bind(out _compilations)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    // ── Initialization ──

    public void Initialize(string artistId)
    {
        ArtistId = artistId;
        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Artist, artistId)
        {
            Title = "Artist"
        };
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

        try
        {
            var overview = await _artistService.GetOverviewAsync(ArtistId);

            // ── Map scalar properties ──
            ArtistName = overview.Name ?? ArtistName;
            ArtistImageUrl = overview.ImageUrl ?? ArtistImageUrl;
            HeaderImageUrl = overview.HeaderImageUrl;
            MonthlyListeners = overview.MonthlyListeners;
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
                        AlbumName = null,
                        AlbumImageUrl = track.AlbumImageUrl,
                        AlbumUri = track.AlbumUri,
                        Duration = track.Duration,
                        PlayCountRaw = track.PlayCount,
                        ArtistNames = track.ArtistNames,
                        IsExplicit = track.IsExplicit,
                        IsPlayable = track.IsPlayable
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

            _ = Task.Run(async () =>
            {
                try
                {
                    var tasks = new List<Task>();

                    if (albumsLoaded < overview.AlbumsTotalCount)
                        tasks.Add(FetchDiscographyGroupAsync(ArtistId!,
                            "ALBUM", "album-ph", albumsLoaded, overview.AlbumsTotalCount, discoToken));

                    if (singlesLoaded < overview.SinglesTotalCount)
                        tasks.Add(FetchDiscographyGroupAsync(ArtistId!,
                            "SINGLE", "single-ph", singlesLoaded, overview.SinglesTotalCount, discoToken));

                    if (compilationsLoaded < overview.CompilationsTotalCount)
                        tasks.Add(FetchDiscographyGroupAsync(ArtistId!,
                            "COMPILATION", "comp-ph", compilationsLoaded, overview.CompilationsTotalCount, discoToken));

                    if (tasks.Count > 0)
                        await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException) { /* navigated away */ }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Background discography fetch failed for {ArtistId}", ArtistId);
                }
            }, discoToken);

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

        // Add shimmer placeholders for remaining items
        for (int i = count; i < totalCount; i++)
            cache.AddOrUpdate(LazyReleaseItem.Placeholder($"{phPrefix}-{i}", i));
    }

    // ── Album expand/collapse ──

    [RelayCommand]
    private void ExpandAlbum(LazyReleaseItem? album)
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

        // Estimate track count if unknown: albums ~12, singles ~2, compilations ~20
        var trackCount = album.Data.TrackCount;
        if (trackCount <= 0)
        {
            trackCount = album.Data.Type switch
            {
                "SINGLE" => 2,
                "COMPILATION" => 20,
                _ => 12 // ALBUM default
            };
        }

        // Add shimmer placeholders for tracks
        for (int i = 0; i < trackCount; i++)
        {
            ExpandedAlbumTracks.Add(LazyTrackItem.Placeholder($"expanded-{i}", i + 1));
        }

        // TODO: Fetch real tracks from Pathfinder/SpClient and call .Populate() on each
        // For now, shimmers will stay until the real endpoint is wired
        IsLoadingExpandedTracks = false;
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
        const int pageSize = 20;
        var offset = alreadyLoaded;

        while (offset < totalCount)
        {
            ct.ThrowIfCancellationRequested();

            var releases = await _artistService.GetDiscographyPageAsync(artistUri, type, offset, pageSize, ct);

            if (releases.Count == 0)
                break;

            _releasesSource.Edit(cache =>
            {
                int idx = offset;
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
            });

            offset += releases.Count;
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
    private void ToggleFollow() => IsFollowing = !IsFollowing;

    [RelayCommand]
    private void PlayTopTracks() { /* TODO: Play via Wavee core */ }

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

    public void Dispose()
    {
        _discoCts?.Cancel();
        _discoCts?.Dispose();
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

    // ── ITrackItem implementation ──
    string Data.Contracts.ITrackItem.Title => Title ?? "";
    string Data.Contracts.ITrackItem.ArtistName => ArtistNames ?? "";
    string Data.Contracts.ITrackItem.ArtistId => "";
    string Data.Contracts.ITrackItem.AlbumName => AlbumName ?? "";
    string Data.Contracts.ITrackItem.AlbumId => AlbumUri ?? "";
    string? Data.Contracts.ITrackItem.ImageUrl => AlbumImageUrl;
    TimeSpan Data.Contracts.ITrackItem.Duration => Duration;
    bool Data.Contracts.ITrackItem.IsExplicit => IsExplicit;
    string Data.Contracts.ITrackItem.DurationFormatted => DurationFormatted;
    int Data.Contracts.ITrackItem.OriginalIndex => Index;

    // ── Public properties ──
    public string? Title { get; init; }
    public string? AlbumName { get; init; }
    public string? ArtistNames { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsExplicit { get; init; }

    public string PlayCountFormatted => PlayCountRaw switch
    {
        >= 1_000_000_000 => $"{PlayCountRaw / 1_000_000_000.0:0.#}B",
        >= 1_000_000 => $"{PlayCountRaw / 1_000_000.0:0.#}M",
        >= 1_000 => $"{PlayCountRaw / 1_000.0:0.#}K",
        _ => PlayCountRaw.ToString("N0")
    };

    public string DurationFormatted =>
        Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");
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
