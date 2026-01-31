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

public sealed partial class ArtistsLibraryViewModel : ObservableObject, ITrackListViewModel
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly ICatalogService _catalogService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isLoadingDetails;

    [ObservableProperty]
    private ObservableCollection<LibraryArtistDto> _artists = [];

    [ObservableProperty]
    private ObservableCollection<LibraryArtistDto> _filteredArtists = [];

    [ObservableProperty]
    private LibraryArtistDto? _selectedArtist;

    [ObservableProperty]
    private ObservableCollection<ArtistAlbumGroupViewModel> _albumGroups = [];

    [ObservableProperty]
    private string _searchQuery = "";

    // Wrapper properties for selected artist (avoids null reference in x:Bind)
    [ObservableProperty]
    private string _selectedArtistName = "";

    [ObservableProperty]
    private string? _selectedArtistImageUrl;

    [ObservableProperty]
    private string _selectedArtistFollowers = "";

    [ObservableProperty]
    private int _selectedArtistAlbumCount;

    // Tracks panel (third column) properties
    [ObservableProperty]
    private ArtistAlbumItemViewModel? _selectedAlbumForTracks;

    [ObservableProperty]
    private bool _isTracksPanelVisible;

    [ObservableProperty]
    private ObservableCollection<AlbumTrackDto> _selectedAlbumTracks = [];

    [ObservableProperty]
    private bool _isLoadingSelectedAlbumTracks;

    // Wrapper properties for selected album
    [ObservableProperty]
    private string _selectedAlbumName = "";

    [ObservableProperty]
    private string? _selectedAlbumImageUrl;

    [ObservableProperty]
    private int _selectedAlbumYear;

    public ILibraryDataService LibraryDataService => _libraryDataService;

    public ArtistsLibraryViewModel(ILibraryDataService libraryDataService, ICatalogService catalogService)
    {
        _libraryDataService = libraryDataService;
        _catalogService = catalogService;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        // Skip if already loaded (for page cache restoration)
        if (IsLoading || Artists.Count > 0) return;

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
        var previousSelectedId = preserveSelection ? SelectedArtist?.Id : null;

        try
        {
            IsLoading = true;

            // Load artists and playlists in parallel
            var artistsTask = _libraryDataService.GetArtistsAsync();
            var playlistsTask = _libraryDataService.GetUserPlaylistsAsync();

            await Task.WhenAll(artistsTask, playlistsTask);

            var artists = await artistsTask;
            Playlists = await playlistsTask;

            Artists.Clear();
            foreach (var artist in artists)
            {
                Artists.Add(artist);
            }

            ApplyFilter();

            // Restore previous selection or select first
            if (previousSelectedId != null)
            {
                SelectedArtist = FilteredArtists.FirstOrDefault(a => a.Id == previousSelectedId);
            }

            if (SelectedArtist == null && FilteredArtists.Count > 0)
            {
                SelectedArtist = FilteredArtists[0];
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void PlayArtist()
    {
        if (SelectedArtist == null) return;
        // TODO: Play artist's top tracks via Wavee core
    }

    [RelayCommand]
    private void ShuffleArtist()
    {
        if (SelectedArtist == null) return;
        // TODO: Shuffle play artist's tracks via Wavee core
    }

    [RelayCommand]
    private void OpenArtistDetails()
    {
        if (SelectedArtist == null) return;
        Helpers.Navigation.NavigationHelpers.OpenArtist(SelectedArtist.Id, SelectedArtist.Name);
    }

    [RelayCommand]
    private void SelectAlbumForTracks(ArtistAlbumItemViewModel? album)
    {
        SelectedAlbumForTracks = album;
    }

    [RelayCommand]
    private void CloseTracksPanel()
    {
        SelectedAlbumForTracks = null;
    }

    [RelayCommand]
    private void OpenSelectedAlbum()
    {
        if (SelectedAlbumForTracks == null) return;
        Helpers.Navigation.NavigationHelpers.OpenAlbum(
            SelectedAlbumForTracks.Album.Id,
            SelectedAlbumForTracks.Album.Name);
    }

    partial void OnSelectedAlbumForTracksChanged(ArtistAlbumItemViewModel? value)
    {
        IsTracksPanelVisible = value != null;
        if (value != null)
        {
            SelectedAlbumName = value.Album.Name;
            SelectedAlbumImageUrl = value.Album.ImageUrl;
            SelectedAlbumYear = value.Album.Year;
            _ = LoadSelectedAlbumTracksAsync(value.Album.Id);
        }
        else
        {
            SelectedAlbumTracks.Clear();
        }
    }

    private async Task LoadSelectedAlbumTracksAsync(string albumId)
    {
        try
        {
            IsLoadingSelectedAlbumTracks = true;
            SelectedAlbumTracks.Clear();

            var tracks = await _catalogService.GetAlbumTracksAsync(albumId);
            foreach (var track in tracks)
            {
                SelectedAlbumTracks.Add(track);
            }
        }
        finally
        {
            IsLoadingSelectedAlbumTracks = false;
        }
    }

    partial void OnSelectedArtistChanged(LibraryArtistDto? value)
    {
        // Close tracks panel when artist changes
        SelectedAlbumForTracks = null;

        // Update wrapper properties
        SelectedArtistName = value?.Name ?? "";
        SelectedArtistImageUrl = value?.ImageUrl;
        SelectedArtistFollowers = value?.FollowerCountFormatted ?? "";
        SelectedArtistAlbumCount = value?.AlbumCount ?? 0;

        _ = LoadSelectedArtistDetailsAsync();
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    private async Task LoadSelectedArtistDetailsAsync()
    {
        if (SelectedArtist == null)
        {
            AlbumGroups.Clear();
            return;
        }

        try
        {
            IsLoadingDetails = true;

            var albums = await _libraryDataService.GetArtistAlbumsAsync(SelectedArtist.Id);

            // Group albums by type
            var groups = new[]
            {
                ("Albums", "Album", albums.Where(a => a.AlbumType == "Album")),
                ("Singles & EPs", "Single,EP", albums.Where(a => a.AlbumType is "Single" or "EP")),
                ("Compilations", "Compilation", albums.Where(a => a.AlbumType == "Compilation"))
            };

            AlbumGroups.Clear();
            foreach (var (name, type, groupAlbums) in groups)
            {
                var albumsList = groupAlbums.ToList();
                if (albumsList.Count > 0)
                {
                    AlbumGroups.Add(new ArtistAlbumGroupViewModel(
                        name,
                        type,
                        albumsList,
                        _catalogService,
                        onAlbumSelected: album => SelectedAlbumForTracks = album));
                }
            }
        }
        finally
        {
            IsLoadingDetails = false;
        }
    }

    private void ApplyFilter()
    {
        FilteredArtists.Clear();

        var query = SearchQuery?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(query)
            ? Artists
            : Artists.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var artist in filtered)
        {
            FilteredArtists.Add(artist);
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

    // Playback commands
    [RelayCommand]
    private void PlayTrack(object? track)
    {
        if (track is not AlbumTrackDto albumTrack) return;
        // TODO: Play specific track via Wavee core
    }

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
