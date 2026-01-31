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
/// Sort column options for liked songs.
/// </summary>
public enum LikedSongsSortColumn { Title, Artist, Album, AddedAt }

/// <summary>
/// ViewModel for the Liked Songs page with reactive filtering and sorting using DynamicData.
/// </summary>
public sealed partial class LikedSongsViewModel : ReactiveObject, ITrackListViewModel, IDisposable
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly SourceCache<LikedSongDto, string> _songsSource = new(s => s.Id);
    private readonly ReadOnlyObservableCollection<LikedSongDto> _filteredSongs;
    private readonly CompositeDisposable _disposables = new();

    private string _searchQuery = "";
    private LikedSongsSortColumn _currentSortColumn = LikedSongsSortColumn.AddedAt;
    private bool _isSortDescending = true;
    private bool _isLoading;
    private int _totalSongs;
    private string _totalDuration = "";
    private IReadOnlyList<object> _selectedItems = Array.Empty<object>();
    private IReadOnlyList<PlaylistSummaryDto> _playlists = Array.Empty<PlaylistSummaryDto>();

    /// <summary>
    /// Search query for filtering songs.
    /// </summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set => this.RaiseAndSetIfChanged(ref _searchQuery, value);
    }

    /// <summary>
    /// Current sort column.
    /// </summary>
    public LikedSongsSortColumn CurrentSortColumn
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
    /// Loading state.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    /// <summary>
    /// Total number of liked songs (unfiltered).
    /// </summary>
    public int TotalSongs
    {
        get => _totalSongs;
        private set => this.RaiseAndSetIfChanged(ref _totalSongs, value);
    }

    /// <summary>
    /// Total duration of all liked songs formatted.
    /// </summary>
    public string TotalDuration
    {
        get => _totalDuration;
        private set => this.RaiseAndSetIfChanged(ref _totalDuration, value);
    }

    /// <summary>
    /// Currently selected items in the ListView (implements ITrackListViewModel).
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
    /// Selected tracks cast to LikedSongDto for internal use.
    /// </summary>
    private IEnumerable<LikedSongDto> SelectedTracks => SelectedItems.OfType<LikedSongDto>();

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
    /// Filtered and sorted songs collection for UI binding.
    /// </summary>
    public ReadOnlyObservableCollection<LikedSongDto> FilteredSongs => _filteredSongs;

    // Sort indicator properties for column headers
    public bool IsSortingByTitle => CurrentSortColumn == LikedSongsSortColumn.Title;
    public bool IsSortingByArtist => CurrentSortColumn == LikedSongsSortColumn.Artist;
    public bool IsSortingByAlbum => CurrentSortColumn == LikedSongsSortColumn.Album;
    public bool IsSortingByAddedAt => CurrentSortColumn == LikedSongsSortColumn.AddedAt;

    /// <summary>
    /// Sort chevron glyph: up for ascending, down for descending.
    /// </summary>
    public string SortChevronGlyph => IsSortDescending ? "\uE70D" : "\uE70E";

    public LikedSongsViewModel(ILibraryDataService libraryDataService)
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

        // Build reactive pipeline: Filter -> SortAndBind (more efficient than Sort + Bind)
        _songsSource.Connect()
            .Filter(filterPredicate)
            .SortAndBind(out _filteredSongs, sortComparer)
            .DisposeMany()
            .Subscribe()
            .DisposeWith(_disposables);

        // Compute total songs count from source
        _songsSource.CountChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(count => TotalSongs = count)
            .DisposeWith(_disposables);

        // Compute total duration from source
        _songsSource.Connect()
            .ToCollection()
            .Select(songs => FormatDuration(songs.Sum(s => s.Duration.TotalSeconds)))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(duration => TotalDuration = duration)
            .DisposeWith(_disposables);
    }

    private static Func<LikedSongDto, bool> CreateFilterPredicate(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _ => true;

        var q = query.Trim();
        return s =>
            s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.ArtistName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.AlbumName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private static IComparer<LikedSongDto> CreateSortComparer(LikedSongsSortColumn column, bool descending)
    {
        return (column, descending) switch
        {
            (LikedSongsSortColumn.Title, false) => SortExpressionComparer<LikedSongDto>.Ascending(s => s.Title),
            (LikedSongsSortColumn.Title, true) => SortExpressionComparer<LikedSongDto>.Descending(s => s.Title),
            (LikedSongsSortColumn.Artist, false) => SortExpressionComparer<LikedSongDto>.Ascending(s => s.ArtistName),
            (LikedSongsSortColumn.Artist, true) => SortExpressionComparer<LikedSongDto>.Descending(s => s.ArtistName),
            (LikedSongsSortColumn.Album, false) => SortExpressionComparer<LikedSongDto>.Ascending(s => s.AlbumName),
            (LikedSongsSortColumn.Album, true) => SortExpressionComparer<LikedSongDto>.Descending(s => s.AlbumName),
            (LikedSongsSortColumn.AddedAt, false) => SortExpressionComparer<LikedSongDto>.Ascending(s => s.AddedAt),
            (LikedSongsSortColumn.AddedAt, true) => SortExpressionComparer<LikedSongDto>.Descending(s => s.AddedAt),
            _ => SortExpressionComparer<LikedSongDto>.Descending(s => s.AddedAt)
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
    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            // Load songs and playlists in parallel
            var songsTask = _libraryDataService.GetLikedSongsAsync();
            var playlistsTask = _libraryDataService.GetUserPlaylistsAsync();

            await Task.WhenAll(songsTask, playlistsTask);

            var songs = await songsTask;
            _songsSource.Edit(cache =>
            {
                cache.Clear();
                cache.AddOrUpdate(songs);
            });

            Playlists = await playlistsTask;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SortBy(string columnName)
    {
        if (!Enum.TryParse<LikedSongsSortColumn>(columnName, out var column))
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
        // No manual refresh needed - DynamicData reacts automatically!
    }

    [RelayCommand]
    private void PlayAll()
    {
        // TODO: Implement play all liked songs
    }

    [RelayCommand]
    private void Shuffle()
    {
        // TODO: Implement shuffle play liked songs
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
        // TODO: Implement play after current track (insert into queue after current)
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddSelectedToQueue()
    {
        if (!HasSelection) return;
        // TODO: Implement add selected to queue (append to end)
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelected()
    {
        if (!HasSelection) return;
        // TODO: Implement remove selected from library
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddToPlaylist(PlaylistSummaryDto? playlist)
    {
        if (playlist == null || !HasSelection) return;
        // TODO: Implement add selected tracks to playlist via service
        // var trackIds = SelectedTracks.Select(t => t.Id).ToList();
        // await _libraryDataService.AddTracksToPlaylistAsync(playlist.Id, trackIds);
    }

    #region Explicit ITrackListViewModel ICommand Implementation

    // RelayCommand types implement ICommand, but explicit casts needed for interface
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
        _songsSource.Dispose();
    }
}
