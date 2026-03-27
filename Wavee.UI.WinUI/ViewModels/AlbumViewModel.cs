using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using DynamicData.Binding;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Sort column options for album tracks.
/// </summary>
public enum AlbumSortColumn { Title, Artist, TrackNumber }

/// <summary>
/// ViewModel for the Album detail page with reactive filtering and sorting using DynamicData.
/// </summary>
public sealed partial class AlbumViewModel : ReactiveObject, ITrackListViewModel, ITabBarItemContent, IDisposable
{
    private readonly IAlbumService _albumService;
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ILogger? _logger;
    private readonly SourceCache<LazyTrackItem, string> _tracksSource = new(t => t.Id);
    private readonly ReadOnlyObservableCollection<LazyTrackItem> _filteredTracks;
    private readonly CompositeDisposable _disposables = new();

    private string _albumId = "";
    private string _albumName = "";
    private string? _albumImageUrl;
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
        set => this.RaiseAndSetIfChanged(ref _searchQuery, value);
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

    public bool HasMoreByArtist => MoreByArtist.Count > 0;

    /// <summary>
    /// Filtered and sorted tracks collection for UI binding.
    /// </summary>
    public ReadOnlyObservableCollection<LazyTrackItem> FilteredTracks => _filteredTracks;

    // Sort indicator properties for column headers
    // Albums typically sort by track number, so Title and Artist are the only sortable columns
    public bool IsSortingByTitle => CurrentSortColumn == AlbumSortColumn.Title;
    public bool IsSortingByArtist => CurrentSortColumn == AlbumSortColumn.Artist;
    public bool IsSortingByAlbum => false; // Not applicable for album view
    public bool IsSortingByAddedAt => false; // Not applicable for album view

    /// <summary>
    /// Sort chevron glyph: up for ascending, down for descending.
    /// </summary>
    public string SortChevronGlyph => IsSortDescending ? "\uE70D" : "\uE70E";

    public AlbumViewModel(IAlbumService albumService, ILibraryDataService libraryDataService, IPlaybackStateService playbackStateService, ILogger<AlbumViewModel>? logger = null)
    {
        _albumService = albumService;
        _libraryDataService = libraryDataService;
        _playbackStateService = playbackStateService;
        _logger = logger;

        // Create observable filter predicate from SearchQuery (throttled for performance)
        var filterPredicate = this.WhenAnyValue(x => x.SearchQuery)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Select(CreateFilterPredicate);

        // Create observable sort comparer from sort properties
        var sortComparer = this.WhenAnyValue(
                x => x.CurrentSortColumn,
                x => x.IsSortDescending)
            .Select(tuple => CreateSortComparer(tuple.Item1, tuple.Item2));

        // Build reactive pipeline: Filter -> SortAndBind
        _tracksSource.Connect()
            .Filter(filterPredicate)
            .SortAndBind(out _filteredTracks, sortComparer)
            .DisposeMany()
            .Subscribe()
            .DisposeWith(_disposables);

        // Compute total tracks count from source
        _tracksSource.CountChanged
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(count => TotalTracks = count)
            .DisposeWith(_disposables);

        // Stop loading when tracks are added to the source
        _tracksSource.Connect()
            .WhereReasonsAre(ChangeReason.Add, ChangeReason.Refresh)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (IsLoadingTracks)
                {
                    IsLoadingTracks = false;
                }
            })
            .DisposeWith(_disposables);

        // Compute total duration from source
        _tracksSource.Connect()
            .ToCollection()
            .Select(tracks => FormatDuration(tracks.Sum(t => t.Duration.TotalSeconds)))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(duration => TotalDuration = duration)
            .DisposeWith(_disposables);
    }

    public void Initialize(string albumId)
    {
        AlbumId = albumId;
        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Album, albumId)
        {
            Title = "Album"
        };
    }

    private void UpdateTabTitle()
    {
        if (TabItemParameter != null && !string.IsNullOrEmpty(AlbumName))
        {
            TabItemParameter.Title = AlbumName;
            ContentChanged?.Invoke(this, TabItemParameter);
        }
    }

    private static Func<LazyTrackItem, bool> CreateFilterPredicate(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _ => true;

        var q = query.Trim();
        return t =>
            t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            t.ArtistName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private static IComparer<LazyTrackItem> CreateSortComparer(AlbumSortColumn column, bool descending)
    {
        return (column, descending) switch
        {
            (AlbumSortColumn.Title, false) => SortExpressionComparer<LazyTrackItem>.Ascending(t => t.Title),
            (AlbumSortColumn.Title, true) => SortExpressionComparer<LazyTrackItem>.Descending(t => t.Title),
            (AlbumSortColumn.Artist, false) => SortExpressionComparer<LazyTrackItem>.Ascending(t => t.ArtistName),
            (AlbumSortColumn.Artist, true) => SortExpressionComparer<LazyTrackItem>.Descending(t => t.ArtistName),
            // Default: sort by disc number, then track number
            (AlbumSortColumn.TrackNumber, false) => SortExpressionComparer<LazyTrackItem>
                .Ascending(t => t.OriginalIndex),
            (AlbumSortColumn.TrackNumber, true) => SortExpressionComparer<LazyTrackItem>
                .Descending(t => t.OriginalIndex),
            _ => SortExpressionComparer<LazyTrackItem>
                .Ascending(t => t.OriginalIndex)
        };
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

    [RelayCommand]
    private async Task LoadAsync(string? albumId)
    {
        if (string.IsNullOrEmpty(albumId) || IsLoading) return;
        IsLoading = true;
        IsLoadingTracks = true;
        HasError = false;
        ErrorMessage = null;
        Initialize(albumId);

        try
        {
            // Add shimmer placeholders for tracks (estimated count)
            _tracksSource.Edit(cache =>
            {
                cache.Clear();
                for (int i = 0; i < 10; i++)
                    cache.AddOrUpdate(LazyTrackItem.Placeholder($"ph-{i}", i + 1));
            });

            // Load album details and user playlists in parallel
            var detailTask = _albumService.GetDetailAsync(albumId);
            var playlistsTask = _libraryDataService.GetUserPlaylistsAsync();

            await Task.WhenAll(detailTask, playlistsTask);

            var detail = await detailTask;

            // Map metadata (respect prefilled values from navigation)
            if (!string.IsNullOrEmpty(detail.Name))
                AlbumName = detail.Name;
            if (!string.IsNullOrEmpty(detail.CoverArtUrl))
                AlbumImageUrl = detail.CoverArtUrl;
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
                    // Don't double-prefix if text already starts with © or ℗
                    if (text.StartsWith("\u00A9") || text.StartsWith("\u2117"))
                        return text;
                    return $"{prefix} {text}";
                }));

            Playlists = await playlistsTask;

            // Header is ready
            IsLoading = false;

            // Clear shimmer placeholders first — let the ListView process removals
            _tracksSource.Clear();
            await Task.Yield();

            // Add real tracks in a fresh batch — ListView sees these as new entries → staggered entrance animation
            _tracksSource.Edit(cache =>
            {
                int idx = 1;
                foreach (var t in detail.Tracks)
                {
                    cache.AddOrUpdate(LazyTrackItem.Loaded(t.Id, idx, t));
                    idx++;
                }
            });

            if (detail.Tracks.Count == 0)
                IsLoadingTracks = false;

            // Related albums
            MoreByArtist.Clear();
            foreach (var r in detail.MoreByArtist)
                MoreByArtist.Add(r);
            this.RaisePropertyChanged(nameof(HasMoreByArtist));
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
    private async Task RetryAsync()
    {
        HasError = false;
        ErrorMessage = null;
        await LoadAsync(AlbumId);
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
        IsSaved = !IsSaved;
        // TODO: Update saved state via Wavee core
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
        _playbackStateService.PlayPause();
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
    private void OpenArtist()
    {
        if (!string.IsNullOrEmpty(ArtistId))
        {
            Helpers.Navigation.NavigationHelpers.OpenArtist(ArtistId, ArtistName ?? "Artist");
        }
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
        _disposables.Dispose();
        _tracksSource.Dispose();
    }
}
