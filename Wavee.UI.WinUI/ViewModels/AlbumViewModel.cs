using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ReactiveUI;
using Wavee.Core.Data;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Data.Stores;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Sort column options for album tracks.
/// </summary>
public enum AlbumSortColumn { Title, Artist, TrackNumber }

/// <summary>
/// ViewModel for the Album detail page.
/// Album tracks are static after load — no reactive pipeline needed.
/// </summary>
public sealed partial class AlbumViewModel : ReactiveObject, ITrackListViewModel, ITabBarItemContent, IDisposable
{
    private readonly IAlbumService _albumService;
    private readonly AlbumStore _albumStore;
    private CompositeDisposable? _subscriptions;
    private string? _appliedDetailFor;
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ITrackLikeService? _likeService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;

    /// <summary>All loaded tracks (unfiltered). Null until loaded.</summary>
    private List<LazyTrackItem> _allTracks = [];

    private string _albumId = "";
    private string _albumName = "";
    private string? _albumImageUrl;
    private string? _colorHex;
    private string _artistId = "";
    private string _artistName = "";
    private int _year;
    private string? _albumType;
    private bool _isSaved;
    private bool _isLoading;
    private bool _isLoadingTracks;
    private bool _hasError;
    private string? _errorMessage;
    private string? _label;
    private string? _releaseDateFormatted;
    private string? _copyrightsText;

    private string _searchQuery = "";
    private AlbumSortColumn _currentSortColumn = AlbumSortColumn.TrackNumber;
    private bool _isSortDescending = false;
    private int _totalTracks;
    private string _totalDuration = "";
    private IReadOnlyList<object> _selectedItems = Array.Empty<object>();
    private IReadOnlyList<PlaylistSummaryDto> _playlists = Array.Empty<PlaylistSummaryDto>();
    private ObservableCollection<AlbumRelatedResult> _moreByArtist = [];
    private ObservableCollection<AlbumMerchItemResult> _merchItems = [];

    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    /// <summary>
    /// The album ID.
    /// </summary>
    public string AlbumId
    {
        get => _albumId;
        private set => this.RaiseAndSetIfChanged(ref _albumId, value);
    }

    /// <summary>
    /// The album name.
    /// </summary>
    public string AlbumName
    {
        get => _albumName;
        private set
        {
            this.RaiseAndSetIfChanged(ref _albumName, value);
            UpdateTabTitle();
        }
    }

    /// <summary>
    /// The album cover image URL.
    /// </summary>
    public string? AlbumImageUrl
    {
        get => _albumImageUrl;
        private set => this.RaiseAndSetIfChanged(ref _albumImageUrl, value);
    }

    /// <summary>
    /// Extracted dark color from the album cover art, as a hex string.
    /// Used as a tint for track placeholder backgrounds while album art loads.
    /// </summary>
    public string? ColorHex
    {
        get => _colorHex;
        private set => this.RaiseAndSetIfChanged(ref _colorHex, value);
    }

    /// <summary>
    /// The primary artist ID.
    /// </summary>
    public string ArtistId
    {
        get => _artistId;
        private set => this.RaiseAndSetIfChanged(ref _artistId, value);
    }

    /// <summary>
    /// The primary artist name.
    /// </summary>
    public string ArtistName
    {
        get => _artistName;
        private set => this.RaiseAndSetIfChanged(ref _artistName, value);
    }

    /// <summary>
    /// Release year.
    /// </summary>
    public int Year
    {
        get => _year;
        private set => this.RaiseAndSetIfChanged(ref _year, value);
    }

    /// <summary>
    /// Album type (Album, Single, EP, etc.).
    /// </summary>
    public string? AlbumType
    {
        get => _albumType;
        private set => this.RaiseAndSetIfChanged(ref _albumType, value);
    }

    public string? Label
    {
        get => _label;
        private set => this.RaiseAndSetIfChanged(ref _label, value);
    }

    public string? ReleaseDateFormatted
    {
        get => _releaseDateFormatted;
        private set => this.RaiseAndSetIfChanged(ref _releaseDateFormatted, value);
    }

    public string? CopyrightsText
    {
        get => _copyrightsText;
        private set => this.RaiseAndSetIfChanged(ref _copyrightsText, value);
    }

    /// <summary>
    /// Whether the album is saved to the user's library.
    /// </summary>
    public bool IsSaved
    {
        get => _isSaved;
        set => this.RaiseAndSetIfChanged(ref _isSaved, value);
    }

    /// <summary>
    /// Loading state for initial page load.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    /// <summary>
    /// Loading state specifically for tracks.
    /// </summary>
    public bool IsLoadingTracks
    {
        get => _isLoadingTracks;
        set => this.RaiseAndSetIfChanged(ref _isLoadingTracks, value);
    }

    /// <summary>
    /// Whether an error occurred during loading.
    /// </summary>
    public bool HasError
    {
        get => _hasError;
        set => this.RaiseAndSetIfChanged(ref _hasError, value);
    }

    /// <summary>
    /// Error message to display.
    /// </summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    /// <summary>
    /// Search query for filtering tracks.
    /// </summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            var old = _searchQuery;
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            if (old != value && _allTracks.Count > 0)
                ApplyFilterAndSort();
        }
    }

    /// <summary>
    /// Current sort column.
    /// </summary>
    public AlbumSortColumn CurrentSortColumn
    {
        get => _currentSortColumn;
        set
        {
            var old = _currentSortColumn;
            this.RaiseAndSetIfChanged(ref _currentSortColumn, value);
            if (old != value)
            {
                this.RaisePropertyChanged(nameof(IsSortingByTitle));
                this.RaisePropertyChanged(nameof(IsSortingByArtist));
            }
        }
    }

    /// <summary>
    /// Whether sort is descending.
    /// </summary>
    public bool IsSortDescending
    {
        get => _isSortDescending;
        set
        {
            var old = _isSortDescending;
            this.RaiseAndSetIfChanged(ref _isSortDescending, value);
            if (old != value)
            {
                this.RaisePropertyChanged(nameof(SortChevronGlyph));
            }
        }
    }

    /// <summary>
    /// Total number of tracks.
    /// </summary>
    public int TotalTracks
    {
        get => _totalTracks;
        private set => this.RaiseAndSetIfChanged(ref _totalTracks, value);
    }

    /// <summary>
    /// Total duration formatted.
    /// </summary>
    public string TotalDuration
    {
        get => _totalDuration;
        private set => this.RaiseAndSetIfChanged(ref _totalDuration, value);
    }

    /// <summary>
    /// Currently selected items in the ListView.
    /// </summary>
    public IReadOnlyList<object> SelectedItems
    {
        get => _selectedItems;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedItems, value);
            this.RaisePropertyChanged(nameof(SelectedCount));
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(SelectionHeaderText));
            PlaySelectedCommand.NotifyCanExecuteChanged();
            PlayAfterCommand.NotifyCanExecuteChanged();
            AddSelectedToQueueCommand.NotifyCanExecuteChanged();
            AddToPlaylistCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Number of selected tracks.
    /// </summary>
    public int SelectedCount => SelectedItems.Count;

    /// <summary>
    /// Whether any tracks are selected.
    /// </summary>
    public bool HasSelection => SelectedItems.Count > 0;

    /// <summary>
    /// Selection header text for the command bar.
    /// </summary>
    public string SelectionHeaderText => SelectedCount == 1
        ? "1 track selected"
        : $"{SelectedCount} tracks selected";

    /// <summary>
    /// User's playlists for "Add to playlist" menu.
    /// </summary>
    public IReadOnlyList<PlaylistSummaryDto> Playlists
    {
        get => _playlists;
        private set => this.RaiseAndSetIfChanged(ref _playlists, value);
    }

    /// <summary>
    /// More albums by the same artist.
    /// </summary>
    public ObservableCollection<AlbumRelatedResult> MoreByArtist
    {
        get => _moreByArtist;
        private set => this.RaiseAndSetIfChanged(ref _moreByArtist, value);
    }

    private bool _hasMoreByArtist;
    public bool HasMoreByArtist
    {
        get => _hasMoreByArtist;
        private set => this.RaiseAndSetIfChanged(ref _hasMoreByArtist, value);
    }

    private bool _hasNoRelatedAlbums;
    public bool HasNoRelatedAlbums
    {
        get => _hasNoRelatedAlbums;
        private set => this.RaiseAndSetIfChanged(ref _hasNoRelatedAlbums, value);
    }

    /// <summary>
    /// Merchandise items for this album.
    /// </summary>
    public ObservableCollection<AlbumMerchItemResult> MerchItems
    {
        get => _merchItems;
        private set => this.RaiseAndSetIfChanged(ref _merchItems, value);
    }

    public bool HasMerch => MerchItems.Count > 0;

    /// <summary>
    /// Filtered and sorted tracks for UI binding. Replaced wholesale on load/filter/sort.
    /// </summary>
    private IReadOnlyList<LazyTrackItem> _filteredTracks = Array.Empty<LazyTrackItem>();
    public IReadOnlyList<LazyTrackItem> FilteredTracks
    {
        get => _filteredTracks;
        private set => this.RaiseAndSetIfChanged(ref _filteredTracks, value);
    }

    // Sort indicator properties for column headers
    public bool IsSortingByTitle => CurrentSortColumn == AlbumSortColumn.Title;
    public bool IsSortingByArtist => CurrentSortColumn == AlbumSortColumn.Artist;
    public bool IsSortingByAlbum => false;
    public bool IsSortingByAddedAt => false;

    /// <summary>
    /// Sort chevron glyph: up for ascending, down for descending.
    /// </summary>
    public string SortChevronGlyph => IsSortDescending ? "\uE70D" : "\uE70E";

    public AlbumViewModel(
        IAlbumService albumService,
        AlbumStore albumStore,
        ILibraryDataService libraryDataService,
        IPlaybackStateService playbackStateService,
        ITrackLikeService? likeService = null,
        ILogger<AlbumViewModel>? logger = null)
    {
        _albumService = albumService;
        _albumStore = albumStore;
        _libraryDataService = libraryDataService;
        _playbackStateService = playbackStateService;
        _likeService = likeService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _logger = logger;

        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
    }

    public void Initialize(string albumId)
    {
        if (AlbumId != albumId)
            _appliedDetailFor = null;

        AlbumId = albumId;
        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Album, albumId)
        {
            Title = "Album"
        };

        RefreshSaveState();
    }

    /// <summary>
    /// Start observing the album detail through AlbumStore. Disposing the
    /// subscription on navigation-away cancels any inflight Pathfinder query.
    /// </summary>
    public void Activate(string albumId)
    {
        Initialize(albumId);

        _subscriptions?.Dispose();
        _subscriptions = new CompositeDisposable();

        var sub = _albumStore.Observe(albumId)
            .Subscribe(
                state => _dispatcherQueue.TryEnqueue(() => ApplyDetailState(state, albumId)),
                ex => _logger?.LogError(ex, "AlbumStore stream faulted for {AlbumId}", albumId));
        _subscriptions.Add(sub);
    }

    public void Deactivate()
    {
        _subscriptions?.Dispose();
        _subscriptions = null;
    }

    private void ApplyDetailState(EntityState<AlbumDetailResult> state, string expectedAlbumId)
    {
        if (AlbumId != expectedAlbumId)
            return;

        switch (state)
        {
            case EntityState<AlbumDetailResult>.Initial:
                IsLoading = string.IsNullOrEmpty(AlbumImageUrl);
                IsLoadingTracks = true;
                break;
            case EntityState<AlbumDetailResult>.Loading loading:
                IsLoading = loading.Previous is null && string.IsNullOrEmpty(AlbumImageUrl);
                break;
            case EntityState<AlbumDetailResult>.Ready ready:
                if (_appliedDetailFor != expectedAlbumId || ready.Freshness == Freshness.Fresh)
                    _ = ApplyDetailAsync(ready.Value, expectedAlbumId);
                break;
            case EntityState<AlbumDetailResult>.Error error:
                HasError = true;
                ErrorMessage = error.Exception.Message;
                IsLoading = false;
                IsLoadingTracks = false;
                _logger?.LogError(error.Exception, "AlbumStore reported error for {AlbumId}", expectedAlbumId);
                break;
        }
    }

    private void UpdateTabTitle()
    {
        if (TabItemParameter != null && !string.IsNullOrEmpty(AlbumName))
        {
            TabItemParameter.Title = AlbumName;
            ContentChanged?.Invoke(this, TabItemParameter);
        }
    }

    private void ApplyFilterAndSort()
    {
        using var _p = Services.UiOperationProfiler.Instance?.Profile("AlbumFilterSort");
        IEnumerable<LazyTrackItem> result = _allTracks;

        // Filter
        var query = SearchQuery?.Trim();
        if (!string.IsNullOrEmpty(query))
        {
            result = result.Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.ArtistName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        // Sort
        result = (CurrentSortColumn, IsSortDescending) switch
        {
            (AlbumSortColumn.Title, false) => result.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase),
            (AlbumSortColumn.Title, true) => result.OrderByDescending(t => t.Title, StringComparer.OrdinalIgnoreCase),
            (AlbumSortColumn.Artist, false) => result.OrderBy(t => t.ArtistName, StringComparer.OrdinalIgnoreCase),
            (AlbumSortColumn.Artist, true) => result.OrderByDescending(t => t.ArtistName, StringComparer.OrdinalIgnoreCase),
            (AlbumSortColumn.TrackNumber, true) => result.OrderByDescending(t => t.OriginalIndex),
            _ => result // already in track order
        };

        FilteredTracks = result.ToList();
    }

    private static string FormatDuration(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} hr {ts.Minutes} min";
        return $"{ts.Minutes} min";
    }

    public void PrefillFrom(Data.Parameters.ContentNavigationParameter nav)
    {
        if (!string.IsNullOrEmpty(nav.Title)) AlbumName = nav.Title;
        if (!string.IsNullOrEmpty(nav.ImageUrl)) AlbumImageUrl = nav.ImageUrl;
        if (!string.IsNullOrEmpty(nav.Subtitle)) ArtistName = nav.Subtitle;
    }

    /// <summary>
    /// Apply a pre-fetched AlbumDetailResult from the AlbumStore. Called by
    /// ApplyDetailState once the store emits Ready; drives tracklist build,
    /// related-albums, and the non-blocking merch fetch.
    /// </summary>
    private async Task ApplyDetailAsync(AlbumDetailResult detail, string albumId)
    {
        if (AlbumId != albumId) return;
        _appliedDetailFor = albumId;
        HasError = false;
        ErrorMessage = null;

        try
        {
            // Show shimmer placeholders immediately if we don't have any tracks yet.
            if (_allTracks.Count == 0)
            {
                FilteredTracks = Enumerable.Range(0, 10)
                    .Select(i => LazyTrackItem.Placeholder($"ph-{i}", i + 1))
                    .ToList();
            }

            // Rootlist for "Add to playlist" — fire-and-forget so it doesn't block detail render.
            _ = LoadRootlistAsync();

            // Map metadata (respect prefilled values from navigation)
            if (!string.IsNullOrEmpty(detail.Name))
                AlbumName = detail.Name;
            if (!string.IsNullOrEmpty(detail.CoverArtUrl) && string.IsNullOrEmpty(AlbumImageUrl))
                AlbumImageUrl = detail.CoverArtUrl;
            ColorHex = detail.ColorDarkHex;
            var firstArtist = detail.Artists.FirstOrDefault();
            if (firstArtist != null)
            {
                if (!string.IsNullOrEmpty(firstArtist.Uri)) ArtistId = firstArtist.Uri;
                if (!string.IsNullOrEmpty(firstArtist.Name)) ArtistName = firstArtist.Name;
            }
            if (detail.ReleaseDate.Year > 0)
                Year = detail.ReleaseDate.Year;
            if (!string.IsNullOrEmpty(detail.Type))
                AlbumType = detail.Type;
            IsSaved = detail.IsSaved;
            Label = detail.Label;
            if (detail.ReleaseDate != default)
                ReleaseDateFormatted = detail.ReleaseDate.ToString("MMMM d, yyyy");
            if (detail.Copyrights?.Count > 0)
                CopyrightsText = string.Join("\n", detail.Copyrights.Select(c =>
                {
                    var prefix = c.Type == "P" ? "\u2117" : "\u00A9";
                    var text = c.Text?.TrimStart() ?? "";
                    if (text.StartsWith("\u00A9") || text.StartsWith("\u2117"))
                        return text;
                    return $"{prefix} {text}";
                }));

            RefreshSaveState();

            IsLoading = false;

            // Build real track list
            _allTracks = detail.Tracks
                .Select((t, i) => LazyTrackItem.Loaded(t.Id, i + 1, t))
                .ToList();

            TotalTracks = _allTracks.Count;
            TotalDuration = FormatDuration(_allTracks.Sum(t => t.Duration.TotalSeconds));
            IsLoadingTracks = false;

            // Swap directly from shimmer placeholders to real tracks — one realization
            // burst instead of two, and no visible empty-state flash. The previous
            // "clear -> delay -> repopulate" pattern forced the ListView to tear down
            // every container, flash empty for 50 ms, then rebuild every container,
            // which read as "buggy and laggy" in the transition.
            ApplyFilterAndSort();

            // Related albums
            MoreByArtist.Clear();
            foreach (var r in detail.MoreByArtist)
                MoreByArtist.Add(r);
            HasMoreByArtist = MoreByArtist.Count > 0;
            HasNoRelatedAlbums = !HasMoreByArtist;

            // Merch (non-blocking, loaded after main content)
            _ = LoadMerchAsync(albumId);
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            _logger?.LogError(ex, "Failed to load album {AlbumId}", albumId);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Retry()
    {
        HasError = false;
        ErrorMessage = null;
        if (!string.IsNullOrEmpty(AlbumId))
        {
            _appliedDetailFor = null;
            _albumStore.Invalidate(AlbumId);
        }
    }

    private async Task LoadRootlistAsync()
    {
        try
        {
            var list = await _libraryDataService.GetUserPlaylistsAsync().ConfigureAwait(false);
            _dispatcherQueue.TryEnqueue(() => Playlists = list);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadRootlistAsync failed (album)");
        }
    }

    [RelayCommand]
    private void SortBy(string columnName)
    {
        if (!Enum.TryParse<AlbumSortColumn>(columnName, out var column))
            return;

        if (CurrentSortColumn == column)
        {
            IsSortDescending = !IsSortDescending;
        }
        else
        {
            CurrentSortColumn = column;
            IsSortDescending = false;
        }

        ApplyFilterAndSort();
    }

    [RelayCommand]
    private void PlayAlbum()
    {
        BuildQueueAndPlay(0, shuffle: false);
    }

    [RelayCommand]
    private void ShuffleAlbum()
    {
        _playbackStateService.SetShuffle(true);
        BuildQueueAndPlay(0, shuffle: true);
    }

    [RelayCommand]
    private void ToggleSave()
    {
        if (_likeService == null || string.IsNullOrEmpty(AlbumId))
            return;

        var albumUri = NormalizeAlbumUri(AlbumId);
        var wasSaved = _likeService.IsSaved(SavedItemType.Album, albumUri);

        IsSaved = !wasSaved;
        _likeService.ToggleSave(SavedItemType.Album, albumUri, wasSaved);
    }

    private void RefreshSaveState()
    {
        if (_likeService == null || string.IsNullOrEmpty(AlbumId))
            return;

        IsSaved = _likeService.IsSaved(SavedItemType.Album, NormalizeAlbumUri(AlbumId));
    }

    private void OnSaveStateChanged()
    {
        _dispatcherQueue.TryEnqueue(RefreshSaveState);
    }

    private static string NormalizeAlbumUri(string albumIdOrUri)
    {
        const string prefix = "spotify:album:";
        if (albumIdOrUri.StartsWith(prefix, StringComparison.Ordinal))
            return albumIdOrUri;

        return $"{prefix}{albumIdOrUri}";
    }

    [RelayCommand]
    private void PlayTrack(object? track)
    {
        if (track is not ITrackItem trackItem) return;
        var index = FilteredTracks.ToList().FindIndex(t => t.Id == trackItem.Id);
        BuildQueueAndPlay(index >= 0 ? index : 0, shuffle: false);
    }

    private void BuildQueueAndPlay(int startIndex, bool shuffle)
    {
        if (FilteredTracks.Count == 0) return;

        var queueItems = FilteredTracks.Select(t => new QueueItem
        {
            TrackId = t.Id,
            Title = t.Title,
            ArtistName = t.ArtistName,
            AlbumArt = t.ImageUrl ?? AlbumImageUrl,
            DurationMs = t.Duration.TotalMilliseconds,
            IsUserQueued = false
        }).ToList();

        if (shuffle)
        {
            queueItems.Shuffle();
            startIndex = 0;
        }

        var context = new PlaybackContextInfo
        {
            ContextUri = AlbumId,
            Type = PlaybackContextType.Album,
            Name = AlbumName,
            ImageUrl = AlbumImageUrl
        };

        _playbackStateService.LoadQueue(queueItems, context, startIndex);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlaySelected()
    {
        if (!HasSelection) return;
        // TODO: Implement play selected tracks
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlayAfter()
    {
        if (!HasSelection) return;
        // TODO: Implement play after current track
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddSelectedToQueue()
    {
        if (!HasSelection) return;
        // TODO: Implement add selected to queue
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        // Not applicable for albums - tracks can't be removed
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddToPlaylist(PlaylistSummaryDto? playlist)
    {
        if (playlist == null || !HasSelection) return;
        // TODO: Implement add selected tracks to playlist
    }

    [RelayCommand]
    private async Task OpenMerchItemAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to open merch URL: {Url}", url);
        }
    }

    private async Task LoadMerchAsync(string albumUri)
    {
        try
        {
            var items = await Task.Run(async () => await _albumService.GetMerchAsync(albumUri));
            MerchItems.Clear();
            foreach (var item in items)
                MerchItems.Add(item);
            this.RaisePropertyChanged(nameof(HasMerch));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load merch for {AlbumId}", albumUri);
        }
    }

    [RelayCommand]
    private void OpenArtist()
    {
        if (!string.IsNullOrEmpty(ArtistId))
        {
            Helpers.Navigation.NavigationHelpers.OpenArtist(ArtistId, ArtistName ?? "Artist");
        }
    }

    [RelayCommand]
    private void OpenLikedSongs()
    {
        Helpers.Navigation.NavigationHelpers.OpenLikedSongs();
    }

    [RelayCommand]
    private void OpenRelatedAlbum(string albumId)
    {
        var album = MoreByArtist.FirstOrDefault(a => a.Id == albumId);
        Helpers.Navigation.NavigationHelpers.OpenAlbum(albumId, album?.Name ?? "Album");
    }

    #region Explicit ITrackListViewModel ICommand Implementation

    ICommand ITrackListViewModel.SortByCommand => SortByCommand;
    ICommand ITrackListViewModel.PlayTrackCommand => PlayTrackCommand;
    ICommand ITrackListViewModel.PlaySelectedCommand => PlaySelectedCommand;
    ICommand ITrackListViewModel.PlayAfterCommand => PlayAfterCommand;
    ICommand ITrackListViewModel.AddSelectedToQueueCommand => AddSelectedToQueueCommand;
    ICommand ITrackListViewModel.RemoveSelectedCommand => RemoveSelectedCommand;
    ICommand ITrackListViewModel.AddToPlaylistCommand => AddToPlaylistCommand;

    #endregion

    public void Dispose()
    {
        if (_likeService != null)
            _likeService.SaveStateChanged -= OnSaveStateChanged;

        _allTracks.Clear();
    }
}
