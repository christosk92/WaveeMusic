using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ArtistsLibraryViewModel : ObservableObject, ITrackListViewModel
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly IArtistService _artistService;
    private readonly IAlbumService _albumService;
    private readonly IPlaybackService _playbackService;
    private readonly ITrackLikeService? _likeService;
    private readonly DispatcherQueue _dispatcherQueue;

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
    private string _selectedArtistAddedAt = "";

    [ObservableProperty]
    private int _selectedArtistAlbumCount;

    // Discography filter
    [ObservableProperty]
    private bool _showSavedOnly;

    private List<LibraryArtistAlbumDto> _allAlbums = [];
    private HashSet<string> _savedAlbumUris = [];

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

    public ArtistsLibraryViewModel(
        ILibraryDataService libraryDataService,
        IArtistService artistService,
        IAlbumService albumService,
        IPlaybackService playbackService,
        ITrackLikeService? likeService = null)
    {
        _libraryDataService = libraryDataService;
        _artistService = artistService;
        _albumService = albumService;
        _playbackService = playbackService;
        _likeService = likeService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
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
    private async Task PlayArtistAsync()
    {
        if (SelectedArtist == null) return;
        await _playbackService.PlayContextAsync(
            SelectedArtist.Id,
            new PlayContextOptions { PlayOriginFeature = "artist_library" });
    }

    [RelayCommand]
    private async Task ShuffleArtistAsync()
    {
        if (SelectedArtist == null) return;
        await _playbackService.PlayContextAsync(
            SelectedArtist.Id,
            new PlayContextOptions { Shuffle = true, PlayOriginFeature = "artist_library" });
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

            var tracks = await _albumService.GetTracksAsync(albumId);
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
        SelectedArtistAddedAt = value?.AddedAtFormatted ?? "";
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
            _allAlbums.Clear();
            AlbumGroups.Clear();
            return;
        }

        try
        {
            IsLoadingDetails = true;

            // Fetch full discography (single API call) + saved album URIs in parallel
            var discographyTask = _artistService.GetDiscographyAllAsync(SelectedArtist.Id, 0, 100);
            var savedAlbumsTask = _libraryDataService.GetAlbumsAsync();

            await Task.WhenAll(discographyTask, savedAlbumsTask);

            // Build saved URI set for cross-reference
            _savedAlbumUris = (await savedAlbumsTask).Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Map all results to DTOs with IsSaved, preserving original type from API
            var allReleases = await discographyTask;
            _allAlbums = allReleases.Select(r => new LibraryArtistAlbumDto
            {
                Id = r.Uri ?? $"spotify:album:{r.Id}",
                Name = r.Name ?? "Unknown",
                ImageUrl = r.ImageUrl,
                Year = r.Year,
                AlbumType = r.Type,
                IsSaved = _savedAlbumUris.Contains(r.Uri ?? $"spotify:album:{r.Id}")
            }).ToList();

            ApplyAlbumFilter();
        }
        finally
        {
            IsLoadingDetails = false;
        }
    }

    partial void OnShowSavedOnlyChanged(bool value)
    {
        ApplyAlbumFilter();
    }

    private void ApplyAlbumFilter()
    {
        var source = ShowSavedOnly ? _allAlbums.Where(a => a.IsSaved) : _allAlbums;

        var groups = new[]
        {
            ("Albums", "Album", source.Where(a => a.AlbumType is "ALBUM").ToList()),
            ("Singles & EPs", "Single,EP", source.Where(a => a.AlbumType is "SINGLE" or "EP").ToList()),
            ("Compilations", "Compilation", source.Where(a => a.AlbumType is "COMPILATION").ToList())
        };

        AlbumGroups.Clear();
        foreach (var (name, type, albumsList) in groups)
        {
            if (albumsList.Count > 0)
            {
                AlbumGroups.Add(new ArtistAlbumGroupViewModel(
                    name,
                    type,
                    albumsList,
                    _albumService,
                    _playbackService,
                    onAlbumSelected: album => SelectedAlbumForTracks = album));
            }
        }
    }


    private void OnSaveStateChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_likeService == null || Artists.Count == 0) return;

            // Remove artists that are no longer followed
            var removed = Artists.Where(a => !_likeService.IsSaved(SavedItemType.Artist, a.Id)).ToList();
            if (removed.Count == 0) return;

            foreach (var artist in removed)
            {
                Artists.Remove(artist);
            }

            // Clear selection if the selected artist was removed
            if (SelectedArtist != null && removed.Any(a => a.Id == SelectedArtist.Id))
            {
                SelectedArtist = null;
            }

            ApplyFilter();

            // Select first if nothing selected
            if (SelectedArtist == null && FilteredArtists.Count > 0)
            {
                SelectedArtist = FilteredArtists[0];
            }
        });
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
    private async Task PlayTrackAsync(object? track)
    {
        if (track is not AlbumTrackDto albumTrack) return;
        var albumId = SelectedAlbumForTracks?.Album?.Id;
        if (albumId != null)
            await _playbackService.PlayTrackInContextAsync(albumTrack.Uri, albumId);
        else
            await _playbackService.PlayTracksAsync([albumTrack.Uri]);
    }

    // Multi-select commands
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PlaySelectedAsync()
    {
        if (!HasSelection) return;
        var trackUris = SelectedAlbumTracks
            .Select(t => t.Uri)
            .ToList();
        if (trackUris.Count > 0)
            await _playbackService.PlayTracksAsync(trackUris);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PlayAfterAsync()
    {
        if (!HasSelection) return;
        foreach (var track in SelectedAlbumTracks)
            await _playbackService.AddToQueueAsync(track.Uri);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AddSelectedToQueueAsync()
    {
        if (!HasSelection) return;
        foreach (var track in SelectedAlbumTracks)
            await _playbackService.AddToQueueAsync(track.Uri);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelected()
    {
        if (!HasSelection) return;
        // TODO: Remove selected tracks from library (not a playback operation)
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddToPlaylist(PlaylistSummaryDto? playlist)
    {
        if (playlist == null || !HasSelection) return;
        // TODO: Add selected tracks to playlist (not a playback operation)
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
