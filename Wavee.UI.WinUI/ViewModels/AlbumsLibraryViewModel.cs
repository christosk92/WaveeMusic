using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class AlbumsLibraryViewModel : ObservableObject, ITrackListViewModel
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly ICatalogService _catalogService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isLoadingTracks;

    [ObservableProperty]
    private ObservableCollection<LibraryAlbumDto> _albums = [];

    [ObservableProperty]
    private ObservableCollection<LibraryAlbumDto> _filteredAlbums = [];

    [ObservableProperty]
    private LibraryAlbumDto? _selectedAlbum;

    [ObservableProperty]
    private ObservableCollection<AlbumTrackDto> _selectedAlbumTracks = [];

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private TimeSpan _selectedAlbumDuration;

    // Wrapper properties for selected album (avoids null reference in x:Bind)
    [ObservableProperty]
    private string _selectedAlbumName = "";

    [ObservableProperty]
    private string _selectedAlbumArtist = "";

    [ObservableProperty]
    private int _selectedAlbumYear;

    [ObservableProperty]
    private int _selectedAlbumTrackCount;

    [ObservableProperty]
    private string? _selectedAlbumImageUrl;

    [ObservableProperty]
    private string _selectedAlbumMetadata = "";

    public AlbumsLibraryViewModel(ILibraryDataService libraryDataService, ICatalogService catalogService)
    {
        _libraryDataService = libraryDataService;
        _catalogService = catalogService;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        // Skip if already loaded (for page cache restoration)
        if (IsLoading || Albums.Count > 0) return;

        await LoadDataAsync(preserveSelection: false);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;

        await LoadDataAsync(preserveSelection: true);
    }

    private async Task LoadDataAsync(bool preserveSelection)
    {
        var previousSelectedId = preserveSelection ? SelectedAlbum?.Id : null;

        try
        {
            IsLoading = true;

            // Load albums and playlists in parallel
            var albumsTask = _libraryDataService.GetAlbumsAsync();
            var playlistsTask = _libraryDataService.GetUserPlaylistsAsync();

            await Task.WhenAll(albumsTask, playlistsTask);

            var albums = await albumsTask;
            Playlists = await playlistsTask;

            Albums.Clear();
            foreach (var album in albums)
            {
                Albums.Add(album);
            }

            ApplyFilter();

            // Restore previous selection or select first
            if (previousSelectedId != null)
            {
                SelectedAlbum = FilteredAlbums.FirstOrDefault(a => a.Id == previousSelectedId);
            }

            if (SelectedAlbum == null && FilteredAlbums.Count > 0)
            {
                SelectedAlbum = FilteredAlbums[0];
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void PlayAlbum()
    {
        if (SelectedAlbum == null) return;
        // TODO: Play album via Wavee core
    }

    [RelayCommand]
    private void ShuffleAlbum()
    {
        if (SelectedAlbum == null) return;
        // TODO: Shuffle play album via Wavee core
    }

    [RelayCommand]
    private void PlayTrack(object? track)
    {
        if (track is not AlbumTrackDto albumTrack || SelectedAlbum == null) return;
        // TODO: Play specific track via Wavee core
    }

    [RelayCommand]
    private void OpenAlbumDetails()
    {
        if (SelectedAlbum == null) return;
        Helpers.Navigation.NavigationHelpers.OpenAlbum(SelectedAlbum.Id, SelectedAlbum.Name);
    }

    [RelayCommand]
    private void OpenArtist()
    {
        if (SelectedAlbum?.ArtistId == null) return;
        Helpers.Navigation.NavigationHelpers.OpenArtist(SelectedAlbum.ArtistId, SelectedAlbum.ArtistName);
    }

    partial void OnSelectedAlbumChanged(LibraryAlbumDto? value)
    {
        // Update wrapper properties
        SelectedAlbumName = value?.Name ?? "";
        SelectedAlbumArtist = value?.ArtistName ?? "";
        SelectedAlbumYear = value?.Year ?? 0;
        SelectedAlbumTrackCount = value?.TrackCount ?? 0;
        SelectedAlbumImageUrl = value?.ImageUrl;
        SelectedAlbumMetadata = value != null
            ? $"{value.Year} • {value.TrackCount} tracks"
            : "";

        _ = LoadSelectedAlbumTracksAsync();
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    private async Task LoadSelectedAlbumTracksAsync()
    {
        if (SelectedAlbum == null)
        {
            SelectedAlbumTracks.Clear();
            SelectedAlbumDuration = TimeSpan.Zero;
            return;
        }

        try
        {
            IsLoadingTracks = true;
            var tracks = await _catalogService.GetAlbumTracksAsync(SelectedAlbum.Id);

            SelectedAlbumTracks.Clear();
            var totalDuration = TimeSpan.Zero;
            foreach (var track in tracks)
            {
                SelectedAlbumTracks.Add(track);
                totalDuration += track.Duration;
            }
            SelectedAlbumDuration = totalDuration;
        }
        finally
        {
            IsLoadingTracks = false;
        }
    }

    private void ApplyFilter()
    {
        FilteredAlbums.Clear();

        var query = SearchQuery?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(query)
            ? Albums
            : Albums.Where(a =>
                a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                a.ArtistName.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var album in filtered)
        {
            FilteredAlbums.Add(album);
        }
    }

    #region ITrackListViewModel Implementation

    // Selection tracking
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCount))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionHeaderText))]
    private IReadOnlyList<object> _selectedItems = Array.Empty<object>();

    public int SelectedCount => SelectedItems.Count;
    public bool HasSelection => SelectedItems.Count > 0;
    public string SelectionHeaderText => SelectedCount == 1
        ? "1 track selected"
        : $"{SelectedCount} tracks selected";

    // Sorting - no-op for album tracks (always in track order)
    [RelayCommand]
    private void SortBy(string? columnName) { }

    public string SortChevronGlyph => "";
    public bool IsSortingByTitle => false;
    public bool IsSortingByArtist => false;
    public bool IsSortingByAlbum => false;
    public bool IsSortingByAddedAt => false;

    // Playlists for "Add to playlist" menu
    [ObservableProperty]
    private IReadOnlyList<PlaylistSummaryDto> _playlists = Array.Empty<PlaylistSummaryDto>();

    // Multi-select commands
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlaySelected()
    {
        if (!HasSelection) return;
        // TODO: Play selected tracks via Wavee core
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlayAfter()
    {
        if (!HasSelection) return;
        // TODO: Play selected tracks after current track
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddSelectedToQueue()
    {
        if (!HasSelection) return;
        // TODO: Add selected tracks to queue
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelected()
    {
        if (!HasSelection) return;
        // TODO: Remove selected tracks from library
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddToPlaylist(PlaylistSummaryDto? playlist)
    {
        if (playlist == null || !HasSelection) return;
        // TODO: Add selected tracks to playlist
    }

    // Explicit ITrackListViewModel ICommand implementation
    ICommand ITrackListViewModel.SortByCommand => SortByCommand;
    ICommand ITrackListViewModel.PlayTrackCommand => PlayTrackCommand;
    ICommand ITrackListViewModel.PlaySelectedCommand => PlaySelectedCommand;
    ICommand ITrackListViewModel.PlayAfterCommand => PlayAfterCommand;
    ICommand ITrackListViewModel.AddSelectedToQueueCommand => AddSelectedToQueueCommand;
    ICommand ITrackListViewModel.RemoveSelectedCommand => RemoveSelectedCommand;
    ICommand ITrackListViewModel.AddToPlaylistCommand => AddToPlaylistCommand;

    #endregion

    partial void OnSelectedItemsChanged(IReadOnlyList<object> value)
    {
        PlaySelectedCommand.NotifyCanExecuteChanged();
        PlayAfterCommand.NotifyCanExecuteChanged();
        AddSelectedToQueueCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        AddToPlaylistCommand.NotifyCanExecuteChanged();
    }
}
