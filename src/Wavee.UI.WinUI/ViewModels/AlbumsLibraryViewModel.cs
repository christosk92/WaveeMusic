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

public enum AlbumsLibraryStage
{
    Grid,
    Details
}

public sealed partial class AlbumsLibraryViewModel : ObservableObject, ITrackListViewModel, IDisposable
{
    private const string PreferencesTabKey = "albums";

    private readonly ILibraryDataService _libraryDataService;
    private readonly IAlbumService _albumService;
    private readonly IPlaybackService _playbackService;
    private readonly ITrackLikeService? _likeService;
    private readonly ISettingsService? _settingsService;
    private readonly LibraryRecentsService? _libraryRecents;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _disposed;
    private bool _preferencesLoaded;
    private IReadOnlyDictionary<string, DateTimeOffset> _albumRecents =
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

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
    private LibrarySortBy _sortBy = LibrarySortBy.Recents;

    [ObservableProperty]
    private LibrarySortDirection _sortDirection = LibrarySortDirection.Descending;

    [ObservableProperty]
    private LibraryViewMode _viewMode = LibraryViewMode.DefaultGrid;

    [ObservableProperty]
    private double _gridScale = 1.0;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWideLayout))]
    [NotifyPropertyChangedFor(nameof(IsNarrowLayout))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowGridStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowDetailsStage))]
    [NotifyPropertyChangedFor(nameof(ShowBreadcrumbBar))]
    private bool _useNarrowLayout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNarrowGridStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowDetailsStage))]
    [NotifyPropertyChangedFor(nameof(ShowBreadcrumbBar))]
    private AlbumsLibraryStage _narrowStage = AlbumsLibraryStage.Grid;

    public ObservableCollection<string> BreadcrumbItems { get; } = [];
    public bool IsWideLayout => !UseNarrowLayout;
    public bool IsNarrowLayout => UseNarrowLayout;
    public bool ShowNarrowGridStage => UseNarrowLayout && NarrowStage == AlbumsLibraryStage.Grid;
    public bool ShowNarrowDetailsStage => UseNarrowLayout && NarrowStage == AlbumsLibraryStage.Details;
    public bool ShowBreadcrumbBar => UseNarrowLayout;

    public AlbumsLibraryViewModel(
        ILibraryDataService libraryDataService,
        IAlbumService albumService,
        IPlaybackService playbackService,
        ITrackLikeService? likeService = null,
        ISettingsService? settingsService = null,
        LibraryRecentsService? libraryRecents = null)
    {
        _libraryDataService = libraryDataService;
        _albumService = albumService;
        _playbackService = playbackService;
        _likeService = likeService;
        _settingsService = settingsService;
        _libraryRecents = libraryRecents;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        LoadPreferences();

        AttachLongLivedServices();
        if (_libraryRecents != null)
            // Best-effort prefetch; result arrives via RecentsChanged → re-applies sort.
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
            var map = await _libraryRecents.GetAlbumRecentsAsync().ConfigureAwait(false);
            _albumRecents = map;
            _dispatcherQueue.TryEnqueue(ApplyFilter);
        }
        catch
        {
            // Swallow — sort falls back to AddedAt.
        }
    }

    private void OnLibraryRecentsChanged()
    {
        if (_disposed || _libraryRecents == null) return;
        // The service raises on the UI dispatcher, but await the refetch off the UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                var map = await _libraryRecents.GetAlbumRecentsAsync().ConfigureAwait(false);
                _albumRecents = map;
                _dispatcherQueue.TryEnqueue(() =>
                {
                    ApplyFilter();
                    // Detail-panel metadata embeds the last-played line; refresh it so the
                    // currently-selected album picks up the timestamp without reselection.
                    if (SelectedAlbum is { } current)
                        SelectedAlbumMetadata = BuildSelectedAlbumMetadata(current);
                });
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

        if (Enum.TryParse<LibrarySortBy>(saved.SortBy, ignoreCase: true, out var sb))
            _sortBy = sb;
        if (Enum.TryParse<LibrarySortDirection>(saved.SortDirection, ignoreCase: true, out var sd))
            _sortDirection = sd;
        if (Enum.TryParse<LibraryViewMode>(saved.ViewMode, ignoreCase: true, out var vm))
            _viewMode = vm;
        if (saved.GridScale >= 0.5 && saved.GridScale <= 2.0)
            _gridScale = saved.GridScale;

        _preferencesLoaded = true;
    }

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
            entry.GridScale = GridScale;
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

    partial void OnGridScaleChanged(double value)
    {
        SavePreferences();
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
    private async Task PlayAlbumAsync()
    {
        if (SelectedAlbum == null) return;
        await _playbackService.PlayContextAsync(
            SelectedAlbum.Id,
            new PlayContextOptions { PlayOriginFeature = "album_library" });
    }

    [RelayCommand]
    private async Task ShuffleAlbumAsync()
    {
        if (SelectedAlbum == null) return;
        await _playbackService.PlayContextAsync(
            SelectedAlbum.Id,
            new PlayContextOptions { Shuffle = true, PlayOriginFeature = "album_library" });
    }

    [RelayCommand]
    private async Task PlayTrackAsync(object? track)
    {
        if (track is not AlbumTrackDto albumTrack || SelectedAlbum == null) return;
        await _playbackService.PlayTrackInContextAsync(albumTrack.Uri, SelectedAlbum.Id);
    }

    [RelayCommand]
    private void OpenAlbumDetails()
    {
        if (SelectedAlbum == null) return;
        // Pass the lean library data via ContentNavigationParameter so AlbumPage
        // can PrefillFrom(...) and render the hero (cover + name + artist) in
        // the first frame, without waiting for the AlbumStore Pathfinder fetch.
        Helpers.Navigation.NavigationHelpers.OpenAlbum(
            new Data.Parameters.ContentNavigationParameter
            {
                Uri = SelectedAlbum.Id,
                Title = SelectedAlbum.Name,
                Subtitle = SelectedAlbum.ArtistName,
                ImageUrl = SelectedAlbum.ImageUrl,
            },
            SelectedAlbum.Name);
    }

    [RelayCommand]
    private void OpenArtist()
    {
        if (SelectedAlbum?.ArtistId == null) return;
        // ArtistName ships as the title; the library album row doesn't carry a
        // separate artist image, so ImageUrl is left null and ArtistPage falls
        // back to the avatar URL once ArtistStore returns.
        Helpers.Navigation.NavigationHelpers.OpenArtist(
            new Data.Parameters.ContentNavigationParameter
            {
                Uri = SelectedAlbum.ArtistId,
                Title = SelectedAlbum.ArtistName,
            },
            SelectedAlbum.ArtistName);
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
            ? BuildSelectedAlbumMetadata(value)
            : "";

        if (UseNarrowLayout && value == null)
        {
            NarrowStage = AlbumsLibraryStage.Grid;
        }

        UpdateBreadcrumbs();

        _ = LoadSelectedAlbumTracksAsync();
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
                SetNarrowStage(preserveContext && SelectedAlbum != null
                    ? AlbumsLibraryStage.Details
                    : AlbumsLibraryStage.Grid);
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
            SetNarrowStage(preserveContext && SelectedAlbum != null
                ? AlbumsLibraryStage.Details
                : AlbumsLibraryStage.Grid);
        }
        else
        {
            UpdateBreadcrumbs();
        }
    }

    public void ShowAlbumsRoot()
    {
        SetNarrowStage(AlbumsLibraryStage.Grid);
    }

    public void ShowSelectedAlbumDetails(LibraryAlbumDto? album = null)
    {
        if (album != null)
        {
            SelectedAlbum = album;
        }

        if (SelectedAlbum == null)
        {
            return;
        }

        SetNarrowStage(AlbumsLibraryStage.Details);
    }

    private void SetNarrowStage(AlbumsLibraryStage stage)
    {
        NarrowStage = stage;
        UpdateBreadcrumbs();
    }

    private void UpdateBreadcrumbs()
    {
        BreadcrumbItems.Clear();
        BreadcrumbItems.Add(AppLocalization.GetString("Shell_SidebarAlbums"));

        if (UseNarrowLayout && NarrowStage == AlbumsLibraryStage.Details && SelectedAlbum != null)
        {
            BreadcrumbItems.Add(SelectedAlbum.Name);
        }

        OnPropertyChanged(nameof(ShowBreadcrumbBar));
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
            var tracks = await _albumService.GetTracksAsync(SelectedAlbum.Id);

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

    private void OnSaveStateChanged()
    {
        if (_disposed)
            return;

        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed)
                return;

            using var _p = Services.UiOperationProfiler.Instance?.Profile("AlbumLibrarySyncUI");
            if (_likeService == null) return;

            var removed = Albums.Where(a => !_likeService.IsSaved(SavedItemType.Album, a.Id)).ToList();
            foreach (var album in removed)
            {
                Albums.Remove(album);
            }

            if (SelectedAlbum != null && removed.Any(a => a.Id == SelectedAlbum.Id))
            {
                SelectedAlbum = null;
            }

            // Check for newly saved albums not yet in our collection
            var existingIds = Albums.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var savedIds = _likeService.GetSavedIds(SavedItemType.Album);
            var hasGhosts = Albums.Any(a => a.IsLoading);

            var newIds = savedIds
                .Select(bareId => $"spotify:album:{bareId}")
                .Where(uri => !existingIds.Contains(uri))
                .ToList();

            if (newIds.Count > 0)
            {
                // Add ghost entries immediately for instant UI feedback
                foreach (var uri in newIds)
                {
                    Albums.Add(new LibraryAlbumDto
                    {
                        Id = uri,
                        Name = "",
                        ArtistName = "",
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

            if (SelectedAlbum == null && FilteredAlbums.Count > 0)
            {
                SelectedAlbum = FilteredAlbums[0];
            }
        });
    }

    private void ApplyFilter()
    {
        var selectedId = SelectedAlbum?.Id;

        FilteredAlbums.Clear();

        var query = SearchQuery?.Trim() ?? "";
        IEnumerable<LibraryAlbumDto> filtered = string.IsNullOrEmpty(query)
            ? Albums
            : Albums.Where(a =>
                a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                a.ArtistName.Contains(query, StringComparison.OrdinalIgnoreCase));

        // When sorted by Recents, stamp each DTO with a "Played X ago" subtitle so the
        // list/grid templates can show it in place of the artist / added-date line.
        var showRecents = SortBy == LibrarySortBy.Recents;
        foreach (var album in SortAlbums(filtered))
        {
            album.RecentsSubtitle = showRecents && _albumRecents.TryGetValue(album.Id, out var ts)
                ? FormatRecentsSubtitle(ts)
                : null;
            FilteredAlbums.Add(album);
        }

        PreserveSelectedAlbumAfterFilter(selectedId);
    }

    private void PreserveSelectedAlbumAfterFilter(string? selectedId)
    {
        if (string.IsNullOrEmpty(selectedId))
            return;

        var selected = FilteredAlbums.FirstOrDefault(a =>
            string.Equals(a.Id, selectedId, StringComparison.OrdinalIgnoreCase));

        if (selected != null && !ReferenceEquals(SelectedAlbum, selected))
        {
            SelectedAlbum = selected;
        }
        else if (selected == null && string.Equals(SelectedAlbum?.Id, selectedId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedAlbum = null;
        }
    }

    /// <summary>
    /// Builds the single-line metadata string for the detail panel. Year + track count
    /// always show; we also append "Added MMM d, yyyy" and — when we have a last-played
    /// timestamp for this album — "Played Xh ago" so the detail panel reflects the user's
    /// relationship to the album at a glance, regardless of the current sort.
    /// </summary>
    private string BuildSelectedAlbumMetadata(LibraryAlbumDto album)
    {
        var parts = new List<string>();
        if (album.Year > 0) parts.Add(album.Year.ToString());
        parts.Add($"{album.TrackCount} tracks");
        if (album.AddedAt > DateTimeOffset.MinValue)
            parts.Add($"Added {album.AddedAt.LocalDateTime:MMM d, yyyy}");
        if (_albumRecents.TryGetValue(album.Id, out var lastPlayed))
            parts.Add(FormatRecentsSubtitle(lastPlayed));
        return string.Join(" • ", parts);
    }

    /// <summary>
    /// Human-friendly last-played formatter: "Played just now" / "Played 12m ago" /
    /// "Played 3h ago" / "Played 2d ago" / "Played Mar 15" for older entries.
    /// </summary>
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

    private IEnumerable<LibraryAlbumDto> SortAlbums(IEnumerable<LibraryAlbumDto> source)
    {
        var descending = SortDirection == LibrarySortDirection.Descending;

        return SortBy switch
        {
            // Recents = actual play recency from the Spotify private API (LibraryRecentsService).
            // Never-played items fall to the bottom (desc) or top (asc) via DateTimeOffset.MinValue.
            // Ties are broken by AddedAt descending so the ordering is stable.
            LibrarySortBy.Recents => descending
                ? source.OrderByDescending(a => LastPlayedOrMin(a)).ThenByDescending(a => a.AddedAt)
                : source.OrderBy(a => LastPlayedOrMin(a)).ThenByDescending(a => a.AddedAt),
            // RecentlyAdded keeps its original semantics (library save date).
            LibrarySortBy.RecentlyAdded => descending
                ? source.OrderByDescending(a => a.AddedAt)
                : source.OrderBy(a => a.AddedAt),
            LibrarySortBy.Alphabetical => descending
                ? source.OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            LibrarySortBy.Creator => descending
                ? source.OrderByDescending(a => a.ArtistName, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(a => a.ArtistName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            LibrarySortBy.ReleaseDate => descending
                ? source.OrderByDescending(a => a.Year).ThenByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(a => a.Year).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            _ => source
        };
    }

    private DateTimeOffset LastPlayedOrMin(LibraryAlbumDto album) =>
        _albumRecents.TryGetValue(album.Id, out var ts) ? ts : DateTimeOffset.MinValue;

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

    // Sorting track columns - no-op for album tracks (always in track order).
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

    // Multi-select commands
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PlaySelectedAsync()
    {
        if (!HasSelection) return;
        var trackUris = SelectedItems.OfType<AlbumTrackDto>().Select(t => t.Uri).ToList();
        if (trackUris.Count == 0) return;
        await _playbackService.PlayTracksAsync(trackUris);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PlayAfterAsync()
    {
        if (!HasSelection) return;
        foreach (var track in SelectedItems.OfType<AlbumTrackDto>())
            await _playbackService.PlayNextAsync(track.Uri);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AddSelectedToQueueAsync()
    {
        if (!HasSelection) return;
        foreach (var track in SelectedItems.OfType<AlbumTrackDto>())
            await _playbackService.AddToQueueAsync(track.Uri);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelected()
    {
        if (!HasSelection || _likeService == null) return;
        foreach (var track in SelectedItems.OfType<AlbumTrackDto>())
        {
            // Force currentlySaved=true so the toggle always lands on "unsaved" —
            // matches the menu label "Remove from library".
            _likeService.ToggleSave(SavedItemType.Track, track.Uri, currentlySaved: true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AddToPlaylistAsync(PlaylistSummaryDto? playlist)
    {
        if (playlist == null || !HasSelection) return;
        var trackIds = SelectedItems.OfType<AlbumTrackDto>().Select(t => t.Uri).ToList();
        if (trackIds.Count == 0) return;
        await _libraryDataService.AddTracksToPlaylistAsync(playlist.Id, trackIds);
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
