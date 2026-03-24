using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using DynamicData.Binding;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Session;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ArtistViewModel : ObservableObject, ITabBarItemContent, IDisposable
{
    private readonly ILogger? _logger;
    private readonly CompositeDisposable _disposables = new();

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

    // ── Tab management ──
    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    // ── Constructor (reactive pipelines) ──

    public ArtistViewModel(ILogger<ArtistViewModel>? logger = null)
    {
        _logger = logger;

        // Top tracks: bind in insertion order (already ordered by popularity from API)
        _topTracksSource.Connect()
            .Bind(out _topTracks)
            .Subscribe()
            .DisposeWith(_disposables);

        // Albums: filter + sort by date desc
        _releasesSource.Connect()
            .Filter(r => r.Data?.Type == "ALBUM" || (!r.IsLoaded && r.Id.StartsWith("album-ph")))
            .Sort(SortExpressionComparer<LazyReleaseItem>.Descending(r => r.Data?.ReleaseDate ?? DateTimeOffset.MinValue))
            .Bind(out _albums)
            .Subscribe()
            .DisposeWith(_disposables);

        // Singles
        _releasesSource.Connect()
            .Filter(r => r.Data?.Type == "SINGLE" || (!r.IsLoaded && r.Id.StartsWith("single-ph")))
            .Sort(SortExpressionComparer<LazyReleaseItem>.Descending(r => r.Data?.ReleaseDate ?? DateTimeOffset.MinValue))
            .Bind(out _singles)
            .Subscribe()
            .DisposeWith(_disposables);

        // Compilations
        _releasesSource.Connect()
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
            var session = Ioc.Default.GetService<ISession>();
            if (session == null || !session.IsConnected())
            {
                HasError = true;
                ErrorMessage = "Not connected to Spotify";
                return;
            }

            var response = await session.Pathfinder.GetArtistOverviewAsync(ArtistId);
            var artist = response.Data?.ArtistUnion;
            if (artist == null)
            {
                HasError = true;
                ErrorMessage = "Artist not found";
                return;
            }

            // ── Map scalar properties ──
            ArtistName = artist.Profile?.Name ?? ArtistName;
            ArtistImageUrl = artist.Visuals?.AvatarImage?.Sources?.LastOrDefault()?.Url ?? ArtistImageUrl;
            HeaderImageUrl = artist.HeaderImage?.Data?.Sources
                ?.OrderByDescending(s => s.MaxWidth ?? s.Width ?? 0)
                .FirstOrDefault()?.Url;
            MonthlyListeners = artist.Stats?.MonthlyListeners ?? 0;
            Followers = artist.Stats?.Followers ?? 0;
            Biography = artist.Profile?.Biography?.Text;
            IsVerified = artist.Profile?.Verified ?? false;

            // ── Latest release ──
            var latest = artist.Discography?.Latest;
            if (latest != null)
            {
                LatestReleaseName = latest.Name;
                LatestReleaseImageUrl = latest.CoverArt?.Sources?.LastOrDefault()?.Url;
                LatestReleaseUri = latest.Uri;
                LatestReleaseType = latest.Type;
                LatestReleaseTrackCount = latest.Tracks?.TotalCount ?? 0;
                LatestReleaseDate = FormatReleaseDate(latest.Date);
            }

            // ── Top tracks (reactive source update) ──
            _topTracksSource.Edit(cache =>
            {
                cache.Clear();
                int idx = 1;

                // Add loaded tracks from initial API response
                if (artist.Discography?.TopTracks?.Items != null)
                {
                    foreach (var item in artist.Discography.TopTracks.Items)
                    {
                        var track = item.Track;
                        if (track == null) continue;

                        var trackVm = new ArtistTopTrackVm
                        {
                            Id = track.Id ?? item.Uid ?? $"track-{idx}",
                            Index = idx,
                            Title = track.Name,
                            Uri = track.Uri,
                            AlbumName = null,
                            AlbumImageUrl = track.AlbumOfTrack?.CoverArt?.Sources?.FirstOrDefault()?.Url,
                            AlbumUri = track.AlbumOfTrack?.Uri,
                            Duration = TimeSpan.FromMilliseconds(track.Duration?.TotalMilliseconds ?? 0),
                            PlayCountRaw = long.TryParse(track.Playcount, out var pc) ? pc : 0,
                            ArtistNames = string.Join(", ",
                                track.Artists?.Items?.Select(a => a.Profile?.Name ?? "") ?? []),
                            IsExplicit = track.ContentRating?.Label == "EXPLICIT",
                            IsPlayable = track.Playability?.Playable ?? true
                        };

                        cache.AddOrUpdate(LazyTrackItem.Loaded(trackVm.Id, idx, trackVm));
                        idx++;
                    }
                }

                // Pad to fill the current page so placeholders start on the next page
                var loadedCount = idx - 1;
                var pageSize = TracksPerPage > 0 ? TracksPerPage : 12; // fallback
                var remainder = loadedCount % pageSize;
                var padCount = remainder > 0 ? pageSize - remainder : 0;

                // Add placeholders: padding to fill current page + a full page of shimmers
                for (int i = 0; i < padCount + pageSize; i++)
                {
                    cache.AddOrUpdate(LazyTrackItem.Placeholder($"placeholder-{idx}", idx));
                    idx++;
                }
            });

            // ── Releases (all types into one SourceCache with placeholders) ──
            var albumsTotal = artist.Discography?.Albums?.TotalCount ?? 0;
            var singlesTotal = artist.Discography?.Singles?.TotalCount ?? 0;
            var compilationsTotal = artist.Discography?.Compilations?.TotalCount ?? 0;

            _releasesSource.Edit(cache =>
            {
                cache.Clear();
                var albumsLoaded = MapReleaseGroup(cache, artist.Discography?.Albums, "ALBUM");
                var singlesLoaded = MapReleaseGroup(cache, artist.Discography?.Singles, "SINGLE");
                var compilationsLoaded = MapReleaseGroup(cache, artist.Discography?.Compilations, "COMPILATION");

                // Add shimmer placeholders for remaining items
                for (int i = albumsLoaded; i < albumsTotal; i++)
                    cache.AddOrUpdate(LazyReleaseItem.Placeholder($"album-ph-{i}", i));
                for (int i = singlesLoaded; i < singlesTotal; i++)
                    cache.AddOrUpdate(LazyReleaseItem.Placeholder($"single-ph-{i}", i));
                for (int i = compilationsLoaded; i < compilationsTotal; i++)
                    cache.AddOrUpdate(LazyReleaseItem.Placeholder($"comp-ph-{i}", i));
            });

            AlbumsTotalCount = albumsTotal;
            SinglesTotalCount = singlesTotal;
            CompilationsTotalCount = compilationsTotal;

            // ── Related artists ──
            RelatedArtists.Clear();
            if (artist.RelatedContent?.RelatedArtists?.Items != null)
            {
                foreach (var ra in artist.RelatedContent.RelatedArtists.Items)
                {
                    RelatedArtists.Add(new RelatedArtistVm
                    {
                        Id = ra.Id,
                        Uri = ra.Uri,
                        Name = ra.Profile?.Name,
                        ImageUrl = ra.Visuals?.AvatarImage?.Sources?.LastOrDefault()?.Url
                    });
                }
            }

            // ── Pagination ──
            CurrentPage = 0;
            NotifyPaginationChanged();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            _logger?.LogError(ex, "Failed to load artist {ArtistId}", ArtistId);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Mapping helpers ──

    private static int MapReleaseGroup(
        ISourceUpdater<LazyReleaseItem, string> cache,
        ArtistReleaseGroup? group,
        string type)
    {
        if (group?.Items == null) return 0;

        int count = 0;
        foreach (var item in group.Items)
        {
            var release = item.Releases?.Items?.FirstOrDefault();
            if (release?.Id == null) continue;

            var vm = new ArtistReleaseVm
            {
                Id = release.Id,
                Uri = release.Uri,
                Name = release.Name,
                Type = type,
                ImageUrl = release.CoverArt?.Sources?.LastOrDefault()?.Url,
                ReleaseDate = ParseReleaseDate(release.Date),
                TrackCount = release.Tracks?.TotalCount ?? 0,
                Label = release.Label,
                Year = release.Date?.Year ?? 0
            };

            cache.AddOrUpdate(LazyReleaseItem.Loaded(vm.Id, count, vm));
            count++;
        }
        return count;
    }

    private static DateTimeOffset ParseReleaseDate(ArtistReleaseDate? date)
    {
        if (date == null) return DateTimeOffset.MinValue;
        try
        {
            return new DateTimeOffset(date.Year, date.Month ?? 1, date.Day ?? 1, 0, 0, 0, TimeSpan.Zero);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static string FormatReleaseDate(ArtistReleaseDate? date)
    {
        if (date == null) return "";
        var dt = new DateTime(date.Year, date.Month ?? 1, date.Day ?? 1);
        return dt.ToString("MMM d, yyyy").ToUpperInvariant();
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
    public required string Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public required string Type { get; init; } // ALBUM, SINGLE, COMPILATION
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
