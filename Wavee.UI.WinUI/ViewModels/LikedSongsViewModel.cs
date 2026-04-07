using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Sort column options for liked songs.
/// </summary>
public enum LikedSongsSortColumn { Title, Artist, Album, AddedAt }

/// <summary>
/// ViewModel for the Liked Songs page with imperative filtering and sorting.
/// </summary>
public sealed partial class LikedSongsViewModel : ObservableObject, ITrackListViewModel
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ILogger? _logger;

    private List<LikedSongDto> _allSongs = [];
    private readonly DispatcherTimer _searchDebounceTimer;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private LikedSongsSortColumn _currentSortColumn = LikedSongsSortColumn.AddedAt;

    [ObservableProperty]
    private bool _isSortDescending = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private int _totalSongs;

    [ObservableProperty]
    private string _totalDuration = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCount))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionHeaderText))]
    private IReadOnlyList<object> _selectedItems = Array.Empty<object>();

    [ObservableProperty]
    private IReadOnlyList<PlaylistSummaryDto> _playlists = Array.Empty<PlaylistSummaryDto>();

    public ObservableCollection<LikedSongDto> FilteredSongs { get; } = [];

    private IEnumerable<LikedSongDto> SelectedTracks => SelectedItems.OfType<LikedSongDto>();

    public int SelectedCount => SelectedItems.Count;
    public bool HasSelection => SelectedItems.Count > 0;
    public string SelectionHeaderText => SelectedCount == 1
        ? "1 track selected"
        : $"{SelectedCount} tracks selected";

    // Sort indicator properties for column headers
    public bool IsSortingByTitle => CurrentSortColumn == LikedSongsSortColumn.Title;
    public bool IsSortingByArtist => CurrentSortColumn == LikedSongsSortColumn.Artist;
    public bool IsSortingByAlbum => CurrentSortColumn == LikedSongsSortColumn.Album;
    public bool IsSortingByAddedAt => CurrentSortColumn == LikedSongsSortColumn.AddedAt;

    public string SortChevronGlyph => IsSortDescending ? "\uE70D" : "\uE70E";

    public LikedSongsViewModel(ILibraryDataService libraryDataService, IPlaybackStateService playbackStateService, ILogger<LikedSongsViewModel>? logger = null)
    {
        _libraryDataService = libraryDataService;
        _playbackStateService = playbackStateService;
        _logger = logger;

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplyFilterAndSort();
        };
    }

    partial void OnSearchQueryChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnCurrentSortColumnChanged(LikedSongsSortColumn value)
    {
        OnPropertyChanged(nameof(IsSortingByTitle));
        OnPropertyChanged(nameof(IsSortingByArtist));
        OnPropertyChanged(nameof(IsSortingByAlbum));
        OnPropertyChanged(nameof(IsSortingByAddedAt));
        ApplyFilterAndSort();
    }

    partial void OnIsSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortChevronGlyph));
        ApplyFilterAndSort();
    }

    partial void OnSelectedItemsChanged(IReadOnlyList<object> value)
    {
        PlaySelectedCommand.NotifyCanExecuteChanged();
        PlayAfterCommand.NotifyCanExecuteChanged();
        AddSelectedToQueueCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        AddToPlaylistCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFilterAndSort()
    {
        var query = SearchQuery?.Trim();
        IEnumerable<LikedSongDto> filtered = _allSongs;

        if (!string.IsNullOrEmpty(query))
        {
            filtered = _allSongs.Where(s =>
                s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.ArtistName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.AlbumName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var sorted = (CurrentSortColumn, IsSortDescending) switch
        {
            (LikedSongsSortColumn.Title, false) => filtered.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase),
            (LikedSongsSortColumn.Title, true) => filtered.OrderByDescending(s => s.Title, StringComparer.OrdinalIgnoreCase),
            (LikedSongsSortColumn.Artist, false) => filtered.OrderBy(s => s.ArtistName, StringComparer.OrdinalIgnoreCase),
            (LikedSongsSortColumn.Artist, true) => filtered.OrderByDescending(s => s.ArtistName, StringComparer.OrdinalIgnoreCase),
            (LikedSongsSortColumn.Album, false) => filtered.OrderBy(s => s.AlbumName, StringComparer.OrdinalIgnoreCase),
            (LikedSongsSortColumn.Album, true) => filtered.OrderByDescending(s => s.AlbumName, StringComparer.OrdinalIgnoreCase),
            (LikedSongsSortColumn.AddedAt, false) => filtered.OrderBy(s => s.AddedAt),
            (LikedSongsSortColumn.AddedAt, true) => filtered.OrderByDescending(s => s.AddedAt),
            _ => filtered.OrderByDescending(s => s.AddedAt)
        };

        FilteredSongs.ReplaceWith(sorted);
    }

    private void UpdateAggregates()
    {
        TotalSongs = _allSongs.Count;
        var totalSeconds = _allSongs.Sum(s => s.Duration.TotalSeconds);
        TotalDuration = FormatDuration(totalSeconds);
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
        HasError = false;
        ErrorMessage = null;

        try
        {
            var songsTask = _libraryDataService.GetLikedSongsAsync();
            var playlistsTask = _libraryDataService.GetUserPlaylistsAsync();

            await Task.WhenAll(songsTask, playlistsTask);

            var songs = await songsTask;
            _allSongs = songs.Select((s, i) => s with { OriginalIndex = i + 1 }).ToList();
            UpdateAggregates();
            ApplyFilterAndSort();

            Playlists = await playlistsTask;
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            _logger?.LogError(ex, "Failed to load liked songs");
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
        await LoadAsync();
    }

    [RelayCommand]
    private void SortBy(string? columnName)
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
    }

    [RelayCommand]
    private void PlayAll()
    {
        BuildQueueAndPlay(0, shuffle: false);
    }

    [RelayCommand]
    private void Shuffle()
    {
        _playbackStateService.SetShuffle(true);
        BuildQueueAndPlay(0, shuffle: true);
    }

    [RelayCommand]
    private void PlayTrack(object? track)
    {
        if (track is not ITrackItem trackItem) return;
        var index = FilteredSongs.ToList().FindIndex(t => t.Id == trackItem.Id);
        BuildQueueAndPlay(index >= 0 ? index : 0, shuffle: false);
    }

    private void BuildQueueAndPlay(int startIndex, bool shuffle)
    {
        if (FilteredSongs.Count == 0) return;

        var queueItems = FilteredSongs.Select(t => new QueueItem
        {
            TrackId = t.Id,
            Title = t.Title,
            ArtistName = t.ArtistName,
            AlbumArt = t.ImageUrl,
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
            ContextUri = "liked-songs",
            Type = PlaybackContextType.LikedSongs,
            Name = "Liked Songs"
        };

        _playbackStateService.LoadQueue(queueItems, context, startIndex);
        _playbackStateService.PlayPause();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlaySelected()
    {
        if (!HasSelection) return;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlayAfter()
    {
        if (!HasSelection) return;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddSelectedToQueue()
    {
        if (!HasSelection) return;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelected()
    {
        if (!HasSelection) return;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddToPlaylist(PlaylistSummaryDto? playlist)
    {
        if (playlist == null || !HasSelection) return;
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
}
