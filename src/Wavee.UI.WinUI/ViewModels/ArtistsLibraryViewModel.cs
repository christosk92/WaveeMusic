using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

public enum ArtistsLibraryStage
{
    Artists,
    Details,
    Tracks
}

public sealed partial class ArtistsLibraryViewModel : ObservableObject, ITrackListViewModel, IDisposable
{
    private const string PreferencesTabKey = "artists";

    private readonly ILibraryDataService _libraryDataService;
    private readonly IArtistService _artistService;
    private readonly IAlbumService _albumService;
    private readonly IPlaybackService _playbackService;
    private readonly ITrackLikeService? _likeService;
    private readonly ISettingsService? _settingsService;
    private readonly LibraryRecentsService? _libraryRecents;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _disposed;
    private bool _preferencesLoaded;
    private IReadOnlyDictionary<string, DateTimeOffset> _artistRecents =
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

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

    [ObservableProperty]
    private LibrarySortBy _sortBy = LibrarySortBy.Recents;

    [ObservableProperty]
    private LibrarySortDirection _sortDirection = LibrarySortDirection.Descending;

    [ObservableProperty]
    private LibraryViewMode _viewMode = LibraryViewMode.DefaultGrid;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWideLayout))]
    [NotifyPropertyChangedFor(nameof(IsNarrowLayout))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowArtistsStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowArtistDetailsStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowAlbumTracksStage))]
    [NotifyPropertyChangedFor(nameof(ShowBreadcrumbBar))]
    private bool _useNarrowLayout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNarrowArtistsStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowArtistDetailsStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowAlbumTracksStage))]
    [NotifyPropertyChangedFor(nameof(ShowBreadcrumbBar))]
    private ArtistsLibraryStage _narrowStage = ArtistsLibraryStage.Artists;

    public ObservableCollection<string> BreadcrumbItems { get; } = [];
    public bool IsWideLayout => !UseNarrowLayout;
    public bool IsNarrowLayout => UseNarrowLayout;
    public bool ShowNarrowArtistsStage => UseNarrowLayout && NarrowStage == ArtistsLibraryStage.Artists;
    public bool ShowNarrowArtistDetailsStage => UseNarrowLayout && NarrowStage == ArtistsLibraryStage.Details;
    public bool ShowNarrowAlbumTracksStage => UseNarrowLayout && NarrowStage == ArtistsLibraryStage.Tracks;
    public bool ShowBreadcrumbBar => UseNarrowLayout;

    public ILibraryDataService LibraryDataService => _libraryDataService;

    public ArtistsLibraryViewModel(
        ILibraryDataService libraryDataService,
        IArtistService artistService,
        IAlbumService albumService,
        IPlaybackService playbackService,
        ITrackLikeService? likeService = null,
        ISettingsService? settingsService = null,
        LibraryRecentsService? libraryRecents = null)
    {
        _libraryDataService = libraryDataService;
        _artistService = artistService;
        _albumService = albumService;
        _playbackService = playbackService;
        _likeService = likeService;
        _settingsService = settingsService;
        _libraryRecents = libraryRecents;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        LoadPreferences();

        AttachLongLivedServices();
        if (_libraryRecents != null)
            _ = PrefetchRecentsAsync();
    }

    private bool _longLivedAttached;

    private void AttachLongLivedServices()
    {
        if (_longLivedAttached) return;
        _longLivedAttached = true;
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
        if (_libraryRecents != null)
            _libraryRecents.RecentsChanged += OnLibraryRecentsChanged;
    }

    private void DetachLongLivedServices()
    {
        if (!_longLivedAttached) return;
        _longLivedAttached = false;
        if (_likeService != null)
            _likeService.SaveStateChanged -= OnSaveStateChanged;
        if (_libraryRecents != null)
            _libraryRecents.RecentsChanged -= OnLibraryRecentsChanged;
    }

    private async Task PrefetchRecentsAsync()
    {
        if (_libraryRecents == null) return;
        try
        {
            var map = await _libraryRecents.GetArtistRecentsAsync().ConfigureAwait(false);
            _artistRecents = map;
            _dispatcherQueue.TryEnqueue(ApplyFilter);
        }
        catch
        {
            // Ignore — sort falls back to AddedAt.
        }
    }

    private void OnLibraryRecentsChanged()
    {
        if (_disposed || _libraryRecents == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var map = await _libraryRecents.GetArtistRecentsAsync().ConfigureAwait(false);
                _artistRecents = map;
                _dispatcherQueue.TryEnqueue(ApplyFilter);
            }
            catch { /* ignore */ }
        });
    }

    private void LoadPreferences()
    {
        var prefs = _settingsService?.Settings.LibraryTabs;
        if (prefs == null || !prefs.TryGetValue(PreferencesTabKey, out var saved) || saved == null)
        {
            _preferencesLoaded = true;
            return;
        }

        if (Enum.TryParse<LibrarySortBy>(saved.SortBy, ignoreCase: true, out var sb) && IsAllowedSortKey(sb))
            _sortBy = sb;
        if (Enum.TryParse<LibrarySortDirection>(saved.SortDirection, ignoreCase: true, out var sd))
            _sortDirection = sd;
        if (Enum.TryParse<LibraryViewMode>(saved.ViewMode, ignoreCase: true, out var vm))
            _viewMode = vm;

        _preferencesLoaded = true;
    }

    // Creator + ReleaseDate don't apply to artists; the panel hides them, but guard
    // against a stale settings value (e.g. migrated from an album tab preference).
    private static bool IsAllowedSortKey(LibrarySortBy key) =>
        key is LibrarySortBy.Recents or LibrarySortBy.RecentlyAdded or LibrarySortBy.Alphabetical;

    private void SavePreferences()
    {
        if (!_preferencesLoaded || _settingsService == null) return;

        _settingsService.Update(s =>
        {
            if (!s.LibraryTabs.TryGetValue(PreferencesTabKey, out var entry) || entry == null)
            {
                entry = new LibraryTabPreferences();
                s.LibraryTabs[PreferencesTabKey] = entry;
            }

            entry.SortBy = SortBy.ToString();
            entry.SortDirection = SortDirection.ToString();
            entry.ViewMode = ViewMode.ToString();
        });

        _ = _settingsService.SaveAsync();
    }

    partial void OnSortByChanged(LibrarySortBy value)
    {
        ApplyFilter();
        SavePreferences();
    }

    partial void OnSortDirectionChanged(LibrarySortDirection value)
    {
        ApplyFilter();
        SavePreferences();
    }

    partial void OnViewModeChanged(LibraryViewMode value)
    {
        SavePreferences();
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
        // Pass the lean library data via ContentNavigationParameter so ArtistPage
        // can PrefillFrom(...) and render hero name + avatar in the first frame
        // without waiting on ArtistStore.
        Helpers.Navigation.NavigationHelpers.OpenArtist(
            new Data.Parameters.ContentNavigationParameter
            {
                Uri = SelectedArtist.Id,
                Title = SelectedArtist.Name,
                ImageUrl = SelectedArtist.ImageUrl,
            },
            SelectedArtist.Name);
    }

    [RelayCommand]
    private void SelectAlbumForTracks(ArtistAlbumItemViewModel? album)
    {
        SelectedAlbumForTracks = album;
        if (UseNarrowLayout && album != null)
        {
            SetNarrowStage(ArtistsLibraryStage.Tracks);
        }
    }

    [RelayCommand]
    private void CloseTracksPanel()
    {
        SelectedAlbumForTracks = null;
        if (UseNarrowLayout)
        {
            SetNarrowStage(SelectedArtist != null
                ? ArtistsLibraryStage.Details
                : ArtistsLibraryStage.Artists);
        }
    }

    [RelayCommand]
    private void OpenSelectedAlbum()
    {
        if (SelectedAlbumForTracks == null) return;
        // Pass the lean album data + the parent artist's name as Subtitle so
        // AlbumPage's hero shows cover + title + artist before the AlbumStore
        // Pathfinder fetch lands.
        var album = SelectedAlbumForTracks.Album;
        Helpers.Navigation.NavigationHelpers.OpenAlbum(
            new Data.Parameters.ContentNavigationParameter
            {
                Uri = album.Id,
                Title = album.Name,
                Subtitle = SelectedArtist?.Name,
                ImageUrl = album.ImageUrl,
            },
            album.Name);
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

        if (UseNarrowLayout)
        {
            NarrowStage = value != null
                ? ArtistsLibraryStage.Tracks
                : SelectedArtist != null
                    ? ArtistsLibraryStage.Details
                    : ArtistsLibraryStage.Artists;
        }

        UpdateBreadcrumbs();
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

        if (UseNarrowLayout && value == null)
        {
            NarrowStage = ArtistsLibraryStage.Artists;
        }

        UpdateBreadcrumbs();

        _ = LoadSelectedArtistDetailsAsync();
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    public void SetNarrowLayout(bool isNarrow, bool preserveContext)
    {
        if (UseNarrowLayout == isNarrow)
        {
            if (isNarrow)
            {
                SetNarrowStage(GetPreferredNarrowStage(preserveContext));
            }
            else
            {
                UpdateBreadcrumbs();
            }

            return;
        }

        UseNarrowLayout = isNarrow;

        if (isNarrow)
        {
            SetNarrowStage(GetPreferredNarrowStage(preserveContext));
        }
        else
        {
            UpdateBreadcrumbs();
        }
    }

    public void ShowArtistsRoot()
    {
        SelectedAlbumForTracks = null;
        SetNarrowStage(ArtistsLibraryStage.Artists);
    }

    public void ShowSelectedArtistDetails(LibraryArtistDto? artist = null)
    {
        if (artist != null)
        {
            SelectedArtist = artist;
        }

        if (SelectedArtist == null)
        {
            return;
        }

        SelectedAlbumForTracks = null;
        SetNarrowStage(ArtistsLibraryStage.Details);
    }

    public void ShowSelectedAlbumTracks(ArtistAlbumItemViewModel? album = null)
    {
        if (album != null)
        {
            SelectedAlbumForTracks = album;
        }

        if (SelectedAlbumForTracks == null)
        {
            return;
        }

        SetNarrowStage(ArtistsLibraryStage.Tracks);
    }

    private ArtistsLibraryStage GetPreferredNarrowStage(bool preserveContext)
    {
        if (!preserveContext)
        {
            return ArtistsLibraryStage.Artists;
        }

        if (SelectedAlbumForTracks != null)
        {
            return ArtistsLibraryStage.Tracks;
        }

        return SelectedArtist != null
            ? ArtistsLibraryStage.Details
            : ArtistsLibraryStage.Artists;
    }

    private void SetNarrowStage(ArtistsLibraryStage stage)
    {
        NarrowStage = stage;
        UpdateBreadcrumbs();
    }

    private void UpdateBreadcrumbs()
    {
        BreadcrumbItems.Clear();
        BreadcrumbItems.Add(AppLocalization.GetString("Shell_SidebarArtists"));

        if (!UseNarrowLayout)
        {
            OnPropertyChanged(nameof(ShowBreadcrumbBar));
            return;
        }

        if (NarrowStage is ArtistsLibraryStage.Details or ArtistsLibraryStage.Tracks && SelectedArtist != null)
        {
            BreadcrumbItems.Add(SelectedArtist.Name);
        }

        if (NarrowStage == ArtistsLibraryStage.Tracks && SelectedAlbumForTracks != null)
        {
            BreadcrumbItems.Add(SelectedAlbumForTracks.Album.Name);
        }

        OnPropertyChanged(nameof(ShowBreadcrumbBar));
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
        if (_disposed)
            return;

        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed)
                return;

            if (_likeService == null) return;

            // Remove artists that are no longer followed
            var removed = Artists.Where(a => !_likeService.IsSaved(SavedItemType.Artist, a.Id)).ToList();
            foreach (var artist in removed)
            {
                Artists.Remove(artist);
            }

            // Clear selection if the selected artist was removed
            if (SelectedArtist != null && removed.Any(a => a.Id == SelectedArtist.Id))
            {
                SelectedArtist = null;
            }

            // Check for newly followed artists not yet in our collection
            var existingIds = Artists.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var savedIds = _likeService.GetSavedIds(SavedItemType.Artist);
            var hasGhosts = Artists.Any(a => a.IsLoading);

            var newIds = savedIds
                .Select(bareId => $"spotify:artist:{bareId}")
                .Where(uri => !existingIds.Contains(uri))
                .ToList();

            if (newIds.Count > 0)
            {
                // Add ghost entries immediately for instant UI feedback
                foreach (var uri in newIds)
                {
                    Artists.Add(new LibraryArtistDto
                    {
                        Id = uri,
                        Name = "",
                        IsLoading = true,
                        AddedAt = DateTimeOffset.UtcNow
                    });
                }
            }
            else if (hasGhosts)
            {
                // Ghost entries exist — try to resolve them from DB
                await LoadDataAsync(preserveSelection: true);
                return;
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
        IEnumerable<LibraryArtistDto> filtered = string.IsNullOrEmpty(query)
            ? Artists
            : Artists.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        var showRecents = SortBy == LibrarySortBy.Recents;
        foreach (var artist in SortArtists(filtered))
        {
            artist.RecentsSubtitle = showRecents && _artistRecents.TryGetValue(artist.Id, out var ts)
                ? FormatRecentsSubtitle(ts)
                : null;
            FilteredArtists.Add(artist);
        }
    }

    private static string FormatRecentsSubtitle(DateTimeOffset playedAt)
    {
        var delta = DateTimeOffset.UtcNow - playedAt;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

        if (delta < TimeSpan.FromSeconds(60)) return "Played just now";
        if (delta < TimeSpan.FromMinutes(60)) return $"Played {(int)delta.TotalMinutes}m ago";
        if (delta < TimeSpan.FromHours(24)) return $"Played {(int)delta.TotalHours}h ago";
        if (delta < TimeSpan.FromDays(7)) return $"Played {(int)delta.TotalDays}d ago";
        return $"Played {playedAt.LocalDateTime:MMM d, yyyy}";
    }

    private IEnumerable<LibraryArtistDto> SortArtists(IEnumerable<LibraryArtistDto> source)
    {
        var descending = SortDirection == LibrarySortDirection.Descending;

        return SortBy switch
        {
            // Recents uses real play recency (LibraryRecentsService); never-played artists
            // fall to the bottom (desc) or top (asc) via DateTimeOffset.MinValue. Ties break
            // by AddedAt desc for a stable order.
            LibrarySortBy.Recents => descending
                ? source.OrderByDescending(a => LastPlayedOrMin(a)).ThenByDescending(a => a.AddedAt)
                : source.OrderBy(a => LastPlayedOrMin(a)).ThenByDescending(a => a.AddedAt),
            LibrarySortBy.RecentlyAdded => descending
                ? source.OrderByDescending(a => a.AddedAt)
                : source.OrderBy(a => a.AddedAt),
            LibrarySortBy.Alphabetical => descending
                ? source.OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            // Creator / ReleaseDate aren't offered for artists; fall through to RecentlyAdded.
            _ => descending
                ? source.OrderByDescending(a => a.AddedAt)
                : source.OrderBy(a => a.AddedAt)
        };
    }

    private DateTimeOffset LastPlayedOrMin(LibraryArtistDto artist) =>
        _artistRecents.TryGetValue(artist.Id, out var ts) ? ts : DateTimeOffset.MinValue;

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
    // Renamed from SortBy to avoid colliding with the LibrarySortBy observable
    // property that drives the library grid's global sort.
    [RelayCommand]
    private void SortTrackColumn(string? columnName) { }

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
    ICommand ITrackListViewModel.SortByCommand => SortTrackColumnCommand;
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DetachLongLivedServices();
    }
}
