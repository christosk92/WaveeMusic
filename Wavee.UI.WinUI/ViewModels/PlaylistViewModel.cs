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
using ReactiveUI;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Sort column options for playlist tracks.
/// </summary>
public enum PlaylistSortColumn { Title, Artist, Album, AddedAt }

/// <summary>
/// ViewModel for the Playlist detail page with reactive filtering and sorting using DynamicData.
/// </summary>
public sealed partial class PlaylistViewModel : ReactiveObject, ITrackListViewModel, IDisposable
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly SourceCache<PlaylistTrackDto, string> _tracksSource = new(t => t.Id);
    private readonly ReadOnlyObservableCollection<PlaylistTrackDto> _filteredTracks;
    private readonly CompositeDisposable _disposables = new();

    private string _playlistId = "";
    private string _playlistName = "";
    private string? _playlistDescription;
    private string? _playlistImageUrl;
    private string _ownerName = "";
    private bool _isOwner;
    private bool _isPublic;
    private int _followerCount;

    private string _searchQuery = "";
    private PlaylistSortColumn _currentSortColumn = PlaylistSortColumn.AddedAt;
    private bool _isSortDescending = true;
    private bool _isLoading;
    private bool _isLoadingTracks;
    private int _totalTracks;
    private string _totalDuration = "";
    private IReadOnlyList<object> _selectedItems = Array.Empty<object>();
    private IReadOnlyList<PlaylistSummaryDto> _playlists = Array.Empty<PlaylistSummaryDto>();

    /// <summary>
    /// The playlist ID.
    /// </summary>
    public string PlaylistId
    {
        get => _playlistId;
        private set => this.RaiseAndSetIfChanged(ref _playlistId, value);
    }

    /// <summary>
    /// The playlist name.
    /// </summary>
    public string PlaylistName
    {
        get => _playlistName;
        private set => this.RaiseAndSetIfChanged(ref _playlistName, value);
    }

    /// <summary>
    /// The playlist description.
    /// </summary>
    public string? PlaylistDescription
    {
        get => _playlistDescription;
        private set => this.RaiseAndSetIfChanged(ref _playlistDescription, value);
    }

    /// <summary>
    /// The playlist cover image URL.
    /// </summary>
    public string? PlaylistImageUrl
    {
        get => _playlistImageUrl;
        private set => this.RaiseAndSetIfChanged(ref _playlistImageUrl, value);
    }

    /// <summary>
    /// The playlist owner's display name.
    /// </summary>
    public string OwnerName
    {
        get => _ownerName;
        private set => this.RaiseAndSetIfChanged(ref _ownerName, value);
    }

    /// <summary>
    /// Whether the current user owns this playlist.
    /// </summary>
    public bool IsOwner
    {
        get => _isOwner;
        private set => this.RaiseAndSetIfChanged(ref _isOwner, value);
    }

    /// <summary>
    /// Whether the playlist is public.
    /// </summary>
    public bool IsPublic
    {
        get => _isPublic;
        private set => this.RaiseAndSetIfChanged(ref _isPublic, value);
    }

    /// <summary>
    /// Number of followers (for non-owned playlists).
    /// </summary>
    public int FollowerCount
    {
        get => _followerCount;
        private set => this.RaiseAndSetIfChanged(ref _followerCount, value);
    }

    /// <summary>
    /// Formatted follower count.
    /// </summary>
    public string FollowerCountFormatted => FollowerCount switch
    {
        0 => "",
        < 1000 => $"{FollowerCount} followers",
        < 1_000_000 => $"{FollowerCount / 1000.0:N1}K followers",
        _ => $"{FollowerCount / 1_000_000.0:N1}M followers"
    };

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
    public PlaylistSortColumn CurrentSortColumn
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
                this.RaisePropertyChanged(nameof(IsSortingByAlbum));
                this.RaisePropertyChanged(nameof(IsSortingByAddedAt));
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
    /// Loading state for initial page load.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    /// <summary>
    /// Loading state specifically for tracks (used for shimmer effect).
    /// </summary>
    public bool IsLoadingTracks
    {
        get => _isLoadingTracks;
        set => this.RaiseAndSetIfChanged(ref _isLoadingTracks, value);
    }

    /// <summary>
    /// Total number of tracks (unfiltered).
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
            RemoveSelectedCommand.NotifyCanExecuteChanged();
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
    /// Filtered and sorted tracks collection for UI binding.
    /// </summary>
    public ReadOnlyObservableCollection<PlaylistTrackDto> FilteredTracks => _filteredTracks;

    // Sort indicator properties for column headers
    public bool IsSortingByTitle => CurrentSortColumn == PlaylistSortColumn.Title;
    public bool IsSortingByArtist => CurrentSortColumn == PlaylistSortColumn.Artist;
    public bool IsSortingByAlbum => CurrentSortColumn == PlaylistSortColumn.Album;
    public bool IsSortingByAddedAt => CurrentSortColumn == PlaylistSortColumn.AddedAt;

    /// <summary>
    /// Sort chevron glyph: up for ascending, down for descending.
    /// </summary>
    public string SortChevronGlyph => IsSortDescending ? "\uE70D" : "\uE70E";

    /// <summary>
    /// Whether remove is allowed (only for owned playlists).
    /// </summary>
    public bool CanRemove => IsOwner && HasSelection;

    public PlaylistViewModel(ILibraryDataService libraryDataService)
    {
        _libraryDataService = libraryDataService;

        // Create observable filter predicate from SearchQuery (throttled for performance)
        var filterPredicate = this.WhenAnyValue(x => x.SearchQuery)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
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
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(count => TotalTracks = count)
            .DisposeWith(_disposables);

        // Stop loading when tracks are added to the source
        _tracksSource.Connect()
            .WhereReasonsAre(ChangeReason.Add, ChangeReason.Refresh)
            .ObserveOn(RxApp.MainThreadScheduler)
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
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(duration => TotalDuration = duration)
            .DisposeWith(_disposables);

        // Update CanRemove when IsOwner or HasSelection changes
        this.WhenAnyValue(x => x.IsOwner)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(CanRemove)))
            .DisposeWith(_disposables);
    }

    private static Func<PlaylistTrackDto, bool> CreateFilterPredicate(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _ => true;

        var q = query.Trim();
        return t =>
            t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            t.ArtistName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            t.AlbumName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private static IComparer<PlaylistTrackDto> CreateSortComparer(PlaylistSortColumn column, bool descending)
    {
        return (column, descending) switch
        {
            (PlaylistSortColumn.Title, false) => SortExpressionComparer<PlaylistTrackDto>.Ascending(t => t.Title),
            (PlaylistSortColumn.Title, true) => SortExpressionComparer<PlaylistTrackDto>.Descending(t => t.Title),
            (PlaylistSortColumn.Artist, false) => SortExpressionComparer<PlaylistTrackDto>.Ascending(t => t.ArtistName),
            (PlaylistSortColumn.Artist, true) => SortExpressionComparer<PlaylistTrackDto>.Descending(t => t.ArtistName),
            (PlaylistSortColumn.Album, false) => SortExpressionComparer<PlaylistTrackDto>.Ascending(t => t.AlbumName),
            (PlaylistSortColumn.Album, true) => SortExpressionComparer<PlaylistTrackDto>.Descending(t => t.AlbumName),
            (PlaylistSortColumn.AddedAt, false) => SortExpressionComparer<PlaylistTrackDto>.Ascending(t => t.AddedAt),
            (PlaylistSortColumn.AddedAt, true) => SortExpressionComparer<PlaylistTrackDto>.Descending(t => t.AddedAt),
            _ => SortExpressionComparer<PlaylistTrackDto>.Descending(t => t.AddedAt)
        };
    }

    private static string FormatDuration(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} hr {ts.Minutes} min";
        return $"{ts.Minutes} min";
    }

    [RelayCommand]
    private async Task LoadAsync(string? playlistId)
    {
        if (string.IsNullOrEmpty(playlistId) || IsLoading) return;
        IsLoading = true;
        IsLoadingTracks = true;
        PlaylistId = playlistId;

        try
        {
            // Load playlist details and user playlists first (fast)
            var detailTask = _libraryDataService.GetPlaylistAsync(playlistId);
            var playlistsTask = _libraryDataService.GetUserPlaylistsAsync();

            await Task.WhenAll(detailTask, playlistsTask);

            var detail = await detailTask;
            PlaylistName = detail.Name;
            PlaylistDescription = detail.Description;
            PlaylistImageUrl = detail.ImageUrl;
            OwnerName = detail.OwnerName;
            IsOwner = detail.IsOwner;
            IsPublic = detail.IsPublic;
            FollowerCount = detail.FollowerCount;
            this.RaisePropertyChanged(nameof(FollowerCountFormatted));

            Playlists = await playlistsTask;

            // Header is ready, stop main loading
            IsLoading = false;

            // Now load tracks (may take longer, loading indicator will show)
            var tracks = await _libraryDataService.GetPlaylistTracksAsync(playlistId);
            _tracksSource.Edit(cache =>
            {
                cache.Clear();
                cache.AddOrUpdate(tracks);
            });

            // Handle empty playlist case - subscription won't fire if no items added
            if (tracks.Count == 0)
            {
                IsLoadingTracks = false;
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SortBy(string columnName)
    {
        if (!Enum.TryParse<PlaylistSortColumn>(columnName, out var column))
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
    private void PlayAll()
    {
        // TODO: Implement play all playlist tracks
    }

    [RelayCommand]
    private void Shuffle()
    {
        // TODO: Implement shuffle play playlist tracks
    }

    [RelayCommand]
    private void PlayTrack(object? track)
    {
        if (track is not ITrackItem trackItem) return;
        // TODO: Implement play specific track
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

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveSelectedAsync()
    {
        if (!CanRemove) return;

        var trackIds = SelectedItems.OfType<PlaylistTrackDto>().Select(t => t.Id).ToList();
        if (trackIds.Count == 0) return;

        await _libraryDataService.RemoveTracksFromPlaylistAsync(PlaylistId, trackIds);

        // Remove from local cache
        _tracksSource.Edit(cache =>
        {
            foreach (var id in trackIds)
            {
                cache.RemoveKey(id);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddToPlaylist(PlaylistSummaryDto? playlist)
    {
        if (playlist == null || !HasSelection) return;
        // TODO: Implement add selected tracks to playlist
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
