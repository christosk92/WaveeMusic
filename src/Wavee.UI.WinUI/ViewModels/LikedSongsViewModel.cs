using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wavee.Core.Session;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Sort column options for liked songs.
/// </summary>
public enum LikedSongsSortColumn { Title, Artist, Album, AddedAt }

/// <summary>
/// ViewModel for the Liked Songs page with imperative filtering and sorting.
/// </summary>
public sealed partial class LikedSongsViewModel : ObservableObject, ITrackListViewModel, IDisposable
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ITrackDescriptorFetcher _descriptorFetcher;
    private readonly ISession _session;
    private readonly IMusicVideoMetadataService? _musicVideoMetadata;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _disposed;
    private bool _syncAlreadyRequested;

    private List<LikedSongDto> _allSongs = [];
    // Cached result of GetLikedSongFiltersAsync so we can re-run BuildFilterChips after
    // descriptor enrichment without re-fetching the server-side filter list.
    private IReadOnlyList<LikedSongsFilterDto> _cachedFilters = Array.Empty<LikedSongsFilterDto>();
    private readonly DispatcherTimer _searchDebounceTimer;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private bool _showOnlyVideoTracks;

    [ObservableProperty]
    private LikedSongsSortColumn _currentSortColumn = LikedSongsSortColumn.AddedAt;

    [ObservableProperty]
    private bool _isSortDescending = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterChipsOrShimmer))]
    private bool _isTagsLoading;

    // Source of truth for which filter chip is currently selected.
    // Bound TwoWay to TokenView.SelectedItem. Property-changed hook triggers re-filtering.
    [ObservableProperty]
    private LikedSongsFilterChipViewModel? _selectedFilterChip;

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
    public ObservableCollection<LikedSongsFilterChipViewModel> FilterChips { get; } = [];
    public bool HasFilterChips => FilterChips.Count > 0;

    // Drives visibility of the chip-row container in XAML. The row should also be visible
    // while the shimmer is showing — so the layout doesn't pop in when chips arrive.
    public bool HasFilterChipsOrShimmer => HasFilterChips || IsTagsLoading;

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

    public int VideoTrackCount => _allSongs.Count(static song => song.HasVideo);
    public bool HasVideoTracks => VideoTrackCount > 0;
    public string VideoTrackFilterLabel => VideoTrackCount == 1 ? "1 video" : $"{VideoTrackCount} videos";

    public LikedSongsViewModel(
        ILibraryDataService libraryDataService,
        IPlaybackStateService playbackStateService,
        ITrackDescriptorFetcher descriptorFetcher,
        ISession session,
        IMusicVideoMetadataService? musicVideoMetadata = null,
        ILogger<LikedSongsViewModel>? logger = null)
    {
        _libraryDataService = libraryDataService;
        _playbackStateService = playbackStateService;
        _descriptorFetcher = descriptorFetcher;
        _session = session;
        _musicVideoMetadata = musicVideoMetadata;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplyFilterAndSort();
        };

        AttachLongLivedServices();
    }

    private bool _longLivedAttached;

    private void AttachLongLivedServices()
    {
        if (_longLivedAttached) return;
        _longLivedAttached = true;
        _libraryDataService.DataChanged += OnLibraryDataChanged;
        _descriptorFetcher.FetchCompleted += OnDescriptorFetchCompleted;
    }

    private void DetachLongLivedServices()
    {
        if (!_longLivedAttached) return;
        _longLivedAttached = false;
        _libraryDataService.DataChanged -= OnLibraryDataChanged;
        _descriptorFetcher.FetchCompleted -= OnDescriptorFetchCompleted;
    }

    private void OnDescriptorFetchCompleted(object? sender, EventArgs e)
    {
        // Dispatcher-marshal and re-run LoadAsync so LibraryDataService re-reads the freshly
        // populated extension_cache and DTOs come back with real descriptor tags.
        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed) return;
            try
            {
                await LoadAsync();
            }
            finally
            {
                IsTagsLoading = false;
            }
        });
    }

    partial void OnSearchQueryChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnShowOnlyVideoTracksChanged(bool value)
    {
        ApplyFilterAndSort();
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

    partial void OnSelectedFilterChipChanged(LikedSongsFilterChipViewModel? oldValue, LikedSongsFilterChipViewModel? newValue)
    {
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
        var selectedChip = SelectedFilterChip;

        if (selectedChip is { IsAllChip: false, Filter: { } selectedFilter })
        {
            filtered = filtered.Where(song => MatchesFilter(song, selectedFilter));
        }

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(s =>
                s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.ArtistName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.AlbumName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (ShowOnlyVideoTracks)
            filtered = filtered.Where(static song => song.HasVideo);

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

    private void NotifyVideoFilterProperties()
    {
        OnPropertyChanged(nameof(VideoTrackCount));
        OnPropertyChanged(nameof(HasVideoTracks));
        OnPropertyChanged(nameof(VideoTrackFilterLabel));

        if (!HasVideoTracks && ShowOnlyVideoTracks)
            ShowOnlyVideoTracks = false;
    }

    private static string FormatDuration(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} hr {ts.Minutes} min";
        return $"{ts.Minutes} min";
    }

    private void BuildFilterChips(IReadOnlyList<LikedSongsFilterDto> filters)
    {
        // Preserve user's selected chip across rebuild. When descriptor enrichment finishes
        // and we reshape the chip list, the previously selected chip should stay selected if
        // it still matches at least one song; otherwise we fall back to "All".
        var previouslySelectedQuery = SelectedFilterChip?.Filter?.Query;

        FilterChips.Clear();

        var matchingFilters = filters
            .Where(static filter => filter.IsSupported)
            .Where(filter => _allSongs.Any(song => MatchesFilter(song, filter)))
            .ToList();

        if (matchingFilters.Count == 0)
        {
            SelectedFilterChip = null;
            OnPropertyChanged(nameof(HasFilterChips));
            OnPropertyChanged(nameof(HasFilterChipsOrShimmer));
            ApplyFilterAndSort();
            return;
        }

        var allChip = new LikedSongsFilterChipViewModel
        {
            Label = "All",
            IsAllChip = true
        };
        FilterChips.Add(allChip);

        LikedSongsFilterChipViewModel? restoredChip = null;
        foreach (var filter in matchingFilters)
        {
            var chip = new LikedSongsFilterChipViewModel
            {
                Label = filter.Title,
                Filter = filter
            };
            if (previouslySelectedQuery != null &&
                string.Equals(previouslySelectedQuery, filter.Query, StringComparison.Ordinal))
            {
                restoredChip = chip;
            }
            FilterChips.Add(chip);
        }

        // Set selection AFTER populating FilterChips so TwoWay binding to TokenView.SelectedItem
        // resolves against the final collection. Setting SelectedFilterChip triggers
        // OnSelectedFilterChipChanged → ApplyFilterAndSort, so we don't call it again below.
        SelectedFilterChip = restoredChip ?? allChip;

        OnPropertyChanged(nameof(HasFilterChips));
        OnPropertyChanged(nameof(HasFilterChipsOrShimmer));
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
            var filtersTask = _libraryDataService.GetLikedSongFiltersAsync();

            await Task.WhenAll(songsTask, playlistsTask, filtersTask);

            var songs = await songsTask;
            _allSongs = songs.Select((s, i) => s with { OriginalIndex = i + 1 }).ToList();
            NotifyVideoFilterProperties();

            // If local DB is empty and we haven't already triggered a sync this session,
            // request one now. DataChanged will fire when sync completes → LoadAsync re-runs.
            if (_allSongs.Count == 0 && !_syncAlreadyRequested)
            {
                _syncAlreadyRequested = true;
                _logger?.LogInformation("Liked songs list is empty — requesting library sync");
                _libraryDataService.RequestSyncIfEmpty();
            }

            _cachedFilters = await filtersTask;
            UpdateAggregates();
            BuildFilterChips(_cachedFilters);
            ApplyFilterAndSort();

            Playlists = await playlistsTask;

            // Kick off async descriptor enrichment. On first open (no cached tags anywhere)
            // we show the shimmer; on subsequent opens the tags are already present so we
            // run the fetcher silently to refresh any stale entries.
            TryTriggerDescriptorFetch();
            TryTriggerVideoAvailabilityFetch();
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

    private void TryTriggerDescriptorFetch()
    {
        if (_allSongs.Count == 0) return;

        var hasAnyCachedTags = _allSongs.Any(s => s.Tags.Count > 0);
        if (!hasAnyCachedTags)
        {
            IsTagsLoading = true;
        }

        var uris = _allSongs.Select(s => s.Uri).ToList();

        // Fire-and-forget: fetcher internally serializes concurrent calls and raises
        // FetchCompleted when done. OnDescriptorFetchCompleted handles the follow-up.
        _ = Task.Run(async () =>
        {
            try
            {
                await _descriptorFetcher.EnqueueAsync(uris);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Descriptor enrichment failed");
                _dispatcherQueue.TryEnqueue(() => IsTagsLoading = false);
            }
        });
    }

    private void TryTriggerVideoAvailabilityFetch()
    {
        if (_musicVideoMetadata is null || _allSongs.Count == 0) return;

        var songsByUri = _allSongs
            .Where(song => !string.IsNullOrWhiteSpace(song.Uri))
            .GroupBy(song => song.Uri, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (songsByUri.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var availability = await _musicVideoMetadata
                    .EnsureAvailabilityAsync(songsByUri.Keys, CancellationToken.None)
                    .ConfigureAwait(false);

                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    var changed = false;
                    foreach (var entry in availability)
                    {
                        if (!songsByUri.TryGetValue(entry.Key, out var songs)) continue;
                        foreach (var song in songs)
                        {
                            if (song.HasVideo == entry.Value) continue;
                            song.HasVideo = entry.Value;
                            changed = true;
                        }
                    }

                    if (!changed) return;

                    NotifyVideoFilterProperties();
                    if (ShowOnlyVideoTracks)
                        ApplyFilterAndSort();
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Music-video availability enrichment failed");
            }
        });
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

        // The autoplay endpoint and dealer protocol both require a real Spotify
        // URI for the context — the canonical Liked Songs form is
        // `spotify:user:{username}:collection`. Passing the UI sentinel
        // "liked-songs" instead 400s /context-resolve/v1/autoplay and corrupts
        // the outgoing dealer state.
        var username = _session.GetUserData()?.Username;
        if (string.IsNullOrEmpty(username))
        {
            _logger?.LogWarning("Cannot start Liked Songs playback — no authenticated user");
            return;
        }
        var collectionUri = $"spotify:user:{username}:collection";

        var context = new PlaybackContextInfo
        {
            ContextUri = collectionUri,
            Type = PlaybackContextType.LikedSongs,
            Name = "Liked Songs"
        };

        _playbackStateService.LoadQueue(queueItems, context, startIndex);
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

    [RelayCommand]
    private void SelectFilterChip(LikedSongsFilterChipViewModel? chip)
    {
        if (chip == null || !FilterChips.Contains(chip))
            return;

        SelectedFilterChip = chip;
    }

    private static bool MatchesFilter(LikedSongDto song, LikedSongsFilterDto filter)
    {
        if (string.IsNullOrWhiteSpace(filter.TagValue) || song.Tags.Count == 0)
            return false;

        var normalizedFilter = NormalizeTag(filter.TagValue);
        if (string.IsNullOrEmpty(normalizedFilter))
            return false;

        return song.Tags.Any(tag => NormalizeTag(tag).Contains(normalizedFilter, StringComparison.Ordinal));
    }

    private static string NormalizeTag(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private void OnLibraryDataChanged(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed || IsLoading)
                return;

            await LoadAsync();
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DetachLongLivedServices();
        _searchDebounceTimer.Stop();
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

public sealed class LikedSongsFilterChipViewModel
{
    public string Label { get; init; } = "";

    public bool IsAllChip { get; init; }

    public LikedSongsFilterDto? Filter { get; init; }
}
