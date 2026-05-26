using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wavee.Core.Playlists;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.Models;
using Wavee.UI.Services.Playlists;
using Wavee.UI.ViewModels.Helpers;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels.Contracts;
using Wavee.UI.WinUI.ViewModels.Home;

namespace Wavee.UI.WinUI.ViewModels.Playlist;

/// <summary>
/// Owns the playlist's track list surface — the loaded snapshot, filtered /
/// sorted projection bound to TrackDataGrid, search + video filter, sort
/// column, session-control chip row, empty-playlist genre grid, and the
/// selection-aware playback commands (Play / Play After / Add to queue /
/// Remove / Add to playlist / SortBy).
///
/// <para>This class implements <see cref="ITrackListViewModel"/> directly —
/// after the PlaylistViewModel decomposition the parent is no longer that
/// interface itself; XAML binds <c>Vm.TrackList</c> wherever an
/// <c>ITrackListViewModel</c>-shaped surface is expected.</para>
/// </summary>
public sealed partial class PlaylistTrackListViewModel
    : TrackListViewModelBase, ITrackListViewModel
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPlaylistMutationService _playlistMutationService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly IPlaylistCacheService? _playlistCache;
    private readonly IMusicVideoMetadataService? _musicVideoMetadata;
    private readonly IHomeFeedService? _homeFeedService;
    private readonly PlaylistTrackFilterSorter _filterSorter;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<string> _playlistIdProvider;
    private readonly Func<string?> _playlistNameProvider;
    private readonly Func<string?> _playlistImageUrlProvider;
    private readonly Func<IReadOnlyDictionary<string, string>?> _playlistFormatAttributesProvider;
    private readonly Func<byte[]?> _playlistRevisionProvider;
    private readonly Func<bool> _canEditItemsProvider;

    private readonly DispatcherTimer _searchDebounceTimer;
    private CancellationTokenSource? _sessionSignalCts;
    private CancellationTokenSource? _emptyPlaylistGenresCts;
    private SessionControlChipViewModel? _pendingSignalChip;
    private bool _suppressSessionSignal;
    private bool _emptyPlaylistGenresLoadStarted;
    private bool _disposed;

    private List<PlaylistTrackDto> _allTracks = [];

    /// <summary>Latest track snapshot — used by sibling VMs (header /
    /// mutations) via the snapshot accessor passed in from the parent.</summary>
    public IReadOnlyList<PlaylistTrackDto> AllTracks => _allTracks;

    public PlaylistTrackListViewModel(
        ILibraryDataService libraryDataService,
        IPlaylistMutationService playlistMutationService,
        IPlaybackStateService playbackStateService,
        PlaylistTrackFilterSorter filterSorter,
        IPlaylistCacheService? playlistCache,
        IMusicVideoMetadataService? musicVideoMetadata,
        IHomeFeedService? homeFeedService,
        ILogger? logger,
        Func<string> playlistIdProvider,
        Func<string?> playlistNameProvider,
        Func<string?> playlistImageUrlProvider,
        Func<IReadOnlyDictionary<string, string>?> playlistFormatAttributesProvider,
        Func<byte[]?> playlistRevisionProvider,
        Func<bool> canEditItemsProvider)
    {
        _libraryDataService = libraryDataService;
        _playlistMutationService = playlistMutationService;
        _playbackStateService = playbackStateService;
        _filterSorter = filterSorter;
        _playlistCache = playlistCache;
        _musicVideoMetadata = musicVideoMetadata;
        _homeFeedService = homeFeedService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _playlistIdProvider = playlistIdProvider;
        _playlistNameProvider = playlistNameProvider;
        _playlistImageUrlProvider = playlistImageUrlProvider;
        _playlistFormatAttributesProvider = playlistFormatAttributesProvider;
        _playlistRevisionProvider = playlistRevisionProvider;
        _canEditItemsProvider = canEditItemsProvider;

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplyFilterAndSort();
        };
    }

    private string PlaylistId => _playlistIdProvider() ?? string.Empty;

    // ── Bound collections ────────────────────────────────────────────────────

    // Stable instance mutated in place — see Tier 1 rationale on bound
    // collections elsewhere in the project. Used by the "Add to playlist"
    // dropdown bound through ItemsRepeater.
    private readonly ObservableCollection<PlaylistSummaryDto> _playlists = [];
    public IReadOnlyList<PlaylistSummaryDto> Playlists => _playlists;

    /// <summary>
    /// Track rows bound to TrackDataGrid. Initial loading is represented by the
    /// grid's lightweight skeleton rows via <see cref="IsLoadingTracks"/>; this
    /// collection only receives real rows once the track snapshot is available.
    /// </summary>
    public ObservableCollection<ITrackItem> FilteredTracks { get; } = [];

    /// <summary>
    /// Browse All genre chips shown in the empty playlist state. Populated lazily
    /// from the same Pathfinder browse-all response used by Home.
    /// </summary>
    public ObservableCollection<BrowseAllItem> EmptyPlaylistGenreItems { get; } = [];

    public bool HasEmptyPlaylistGenreItems => EmptyPlaylistGenreItems.Count > 0;
    public bool ShowEmptyPlaylistGenreGrid => ShowEmptyPlaylistState && HasEmptyPlaylistGenreItems;

    /// <summary>
    /// Session-control chip row — e.g. "Pop Rock", "K-Ballad". Populated from
    /// the playlist's <c>session_control_display.displayName.*</c> format
    /// attributes. Empty for playlists without the session-control chrome.
    /// </summary>
    public ObservableCollection<SessionControlChipViewModel> SessionControlChips { get; } = [];

    /// <summary>Drives the Visibility of the chip row; true iff the playlist has chips.</summary>
    public bool HasSessionControlChips => SessionControlChips.Count > 0;

    /// <summary>
    /// Currently-selected chip. Two-way bound to <c>TokenView.SelectedItem</c>.
    /// Setting this (when not suppressed and when the group id is known) fires
    /// a POST to the playlist signals endpoint and refreshes the track list.
    /// </summary>
    [ObservableProperty]
    private SessionControlChipViewModel? _selectedSessionControlChip;

    // ── Filter + sort state ──────────────────────────────────────────────────

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private bool _showOnlyVideoTracks;

    [ObservableProperty]
    private PlaylistSortColumn _currentSortColumn = PlaylistSortColumn.Custom;

    [ObservableProperty]
    private bool _isSortDescending = false;

    [ObservableProperty]
    private bool _isLoadingTracks;

    [ObservableProperty]
    private int _totalTracks;

    [ObservableProperty]
    private string _totalDuration = "";

    /// <summary>
    /// True when at least one loaded track has a non-null <c>AddedAt</c>. Editorial
    /// and radio playlists typically omit added-at entirely; the playlist page binds
    /// this to hide the Date Added grid column in that case.
    /// </summary>
    [ObservableProperty]
    private bool _hasAnyAddedAt;

    // Sort indicator properties for column headers
    public bool IsSortingByTitle => CurrentSortColumn == PlaylistSortColumn.Title;
    public bool IsSortingByArtist => CurrentSortColumn == PlaylistSortColumn.Artist;
    public bool IsSortingByAlbum => CurrentSortColumn == PlaylistSortColumn.Album;
    public bool IsSortingByAddedAt => CurrentSortColumn == PlaylistSortColumn.AddedAt;

    public string SortChevronGlyph => IsSortDescending ? "" : "";

    public int VideoTrackCount => _allTracks.Count(static track => track.HasVideo);
    public bool HasVideoTracks => VideoTrackCount > 0;
    public string VideoTrackFilterLabel => VideoTrackCount == 1 ? "1 video" : $"{VideoTrackCount} videos";

    /// <summary>
    /// True when the empty-playlist CTA + genre grid should render. Combines
    /// the parent's loading flags + this VM's track count — the parent forwards
    /// its IsLoading via <see cref="SetParentIsLoading"/>.
    /// </summary>
    public bool ShowEmptyPlaylistState =>
        !string.IsNullOrWhiteSpace(PlaylistId)
        && !_parentIsLoading
        && !IsLoadingTracks
        && TotalTracks == 0
        && !_parentHasError;

    /// <summary>Mirrors capability gate from header: can the user remove rows from
    /// the playlist? Combined with selection state by <see cref="CanRemove"/>.</summary>
    public bool CanRemove => _canEditItemsProvider() && HasSelection;

    /// <summary>True when the user is allowed to drag-reorder tracks within this
    /// list. We gate on owner-edit AND on "no sort applied" — applying a sort
    /// makes manual position meaningless, so the gesture is hidden in that mode.</summary>
    public bool CanReorderTracks => _canEditItemsProvider() && _currentSortColumn == PlaylistSortColumn.Custom;

    private bool _parentIsLoading;
    private bool _parentHasError;

    /// <summary>Parent forwards its top-level IsLoading flag here so the empty-state
    /// gate combines all three sources (metadata loading, track loading, error).</summary>
    public void SetParentIsLoading(bool value)
    {
        if (_parentIsLoading == value) return;
        _parentIsLoading = value;
        OnPropertyChanged(nameof(ShowEmptyPlaylistState));
        OnPropertyChanged(nameof(ShowEmptyPlaylistGenreGrid));
        MaybeLoadEmptyPlaylistGenres();
    }

    /// <summary>Parent forwards HasError flag for the empty-state gate.</summary>
    public void SetParentHasError(bool value)
    {
        if (_parentHasError == value) return;
        _parentHasError = value;
        OnPropertyChanged(nameof(ShowEmptyPlaylistState));
        OnPropertyChanged(nameof(ShowEmptyPlaylistGenreGrid));
    }

    /// <summary>Fired by the parent when the header's capability gates flip, so
    /// this VM's <see cref="CanRemove"/> and <see cref="CanReorderTracks"/>
    /// notify their command CanExecute changes.</summary>
    public void NotifyCapabilityGatesChanged()
    {
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanReorderTracks));
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Fired whenever the track snapshot is replaced. Subscribers
    /// (e.g. the header's collaborator-rebuild + ShouldShowAddedByColumn
    /// gate) react to this instead of holding direct references to the
    /// track list.</summary>
    public event EventHandler? TracksChanged;

    /// <summary>Fired whenever the totals change (TotalTracks / TotalDuration).
    /// The parent forwards into the header's <see cref="PlaylistHeaderViewModel.SetTotalTracks"/>
    /// / <see cref="PlaylistHeaderViewModel.SetTotalDuration"/> hooks.</summary>
    public event EventHandler? AggregatesChanged;

    // ── Source-gen partial methods ───────────────────────────────────────────

    partial void OnSearchQueryChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnShowOnlyVideoTracksChanged(bool value)
    {
        ApplyFilterAndSort();
    }

    partial void OnCurrentSortColumnChanged(PlaylistSortColumn value)
    {
        OnPropertyChanged(nameof(IsSortingByTitle));
        OnPropertyChanged(nameof(IsSortingByArtist));
        OnPropertyChanged(nameof(IsSortingByAlbum));
        OnPropertyChanged(nameof(IsSortingByAddedAt));
        // Switching to / from Custom toggles whether drag-reorder makes sense.
        OnPropertyChanged(nameof(CanReorderTracks));
        ApplyFilterAndSort();
    }

    partial void OnIsSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortChevronGlyph));
        ApplyFilterAndSort();
    }

    partial void OnTotalTracksChanged(int value)
    {
        OnPropertyChanged(nameof(ShowEmptyPlaylistState));
        OnPropertyChanged(nameof(ShowEmptyPlaylistGenreGrid));
        MaybeLoadEmptyPlaylistGenres();
        AggregatesChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnTotalDurationChanged(string value)
        => AggregatesChanged?.Invoke(this, EventArgs.Empty);

    partial void OnIsLoadingTracksChanged(bool value)
    {
        _logger?.LogDebug(
            "IsLoadingTracks -> {Value} (PlaylistId='{PlaylistId}', FilteredTracks.Count={Count})",
            value, PlaylistId, FilteredTracks.Count);
        OnPropertyChanged(nameof(ShowEmptyPlaylistState));
        OnPropertyChanged(nameof(ShowEmptyPlaylistGenreGrid));
        MaybeLoadEmptyPlaylistGenres();
    }

    // ── Selection ────────────────────────────────────────────────────────────

    protected override void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(CanRemove));
        PlaySelectedCommand.NotifyCanExecuteChanged();
        PlayAfterCommand.NotifyCanExecuteChanged();
        AddSelectedToQueueCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        AddToPlaylistCommand.NotifyCanExecuteChanged();
    }

    // ── Filter + sort plumbing ───────────────────────────────────────────────

    private void ApplyFilterAndSort()
        => FilteredTracks.ReplaceWith(BuildFilteredAndSortedTracks().Cast<ITrackItem>());

    private IReadOnlyList<PlaylistTrackDto> BuildFilteredAndSortedTracks()
        => _filterSorter.FilterAndSort(
            _allTracks,
            SearchQuery,
            ShowOnlyVideoTracks,
            CurrentSortColumn,
            IsSortDescending);

    private void ApplyFilterAndSortIntoExistingLoadingRows()
    {
        var rows = BuildFilteredAndSortedTracks().Cast<ITrackItem>().ToList();
        if (!FilteredTracks.Any(static row => !row.IsLoaded))
        {
            FilteredTracks.ReplaceWith(rows);
            return;
        }

        var merged = new List<ITrackItem>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < FilteredTracks.Count && FilteredTracks[i] is LazyTrackItem { IsLoaded: false } lazy)
            {
                lazy.Populate(rows[i]);
                merged.Add(lazy);
            }
            else
            {
                merged.Add(rows[i]);
            }
        }

        // One Reset instead of N Add/Remove/Replace notifications. TrackDataGrid
        // re-snapshots and reprojects on every source collection change, so adding
        // the post-placeholder tail one item at a time scales badly on large playlists.
        FilteredTracks.ReplaceWith(merged);
    }

    private void UpdateAggregates()
    {
        TotalTracks = _allTracks.Count;
        var totalSeconds = _allTracks.Sum(t => t.Duration.TotalSeconds);
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

    /// <summary>
    /// Replace the track snapshot with a freshly-loaded list. Re-numbers
    /// <see cref="PlaylistTrackDto.OriginalIndex"/> from 1, recomputes aggregates,
    /// then projects through the current filter/sort into the existing loading-row
    /// scaffolding. Fires <see cref="TracksChanged"/> so sibling VMs / the page
    /// can react.
    /// </summary>
    public void ApplyTracks(IReadOnlyList<PlaylistTrackDto> tracks)
    {
        _allTracks = tracks.Select((t, i) => t with { OriginalIndex = i + 1 }).ToList();
        HasAnyAddedAt = _allTracks.Any(t => t.AddedAt.HasValue);
        NotifyVideoFilterProperties();
        UpdateAggregates();
        ApplyFilterAndSortIntoExistingLoadingRows();
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// In-place HasVideo refresh for a warm-hit refresh that produced the same
    /// id sequence — no need to rebuild the bound collection, just patch the
    /// existing DTOs and re-derive the video filter properties.
    /// </summary>
    public void ApplyVideoAvailabilityToCurrentTracks(IReadOnlyList<PlaylistTrackDto> fetched)
    {
        if (_allTracks.Count == 0 || fetched.Count == 0) return;

        var availabilityByUri = fetched
            .Where(track => !string.IsNullOrWhiteSpace(track.Uri))
            .GroupBy(track => track.Uri, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().HasVideo, StringComparer.Ordinal);
        if (availabilityByUri.Count == 0) return;

        var changed = false;
        foreach (var track in _allTracks)
        {
            if (availabilityByUri.TryGetValue(track.Uri, out var hasVideo) && track.HasVideo != hasVideo)
            {
                track.HasVideo = hasVideo;
                changed = true;
            }
        }

        if (!changed) return;

        NotifyVideoFilterProperties();
        if (ShowOnlyVideoTracks)
            ApplyFilterAndSort();
    }

    /// <summary>
    /// Cheap "did the playlist actually change" check used by the parent's
    /// warm-hit refresh path. Compares count + per-index Id; doesn't care
    /// about other fields (artist names, image URLs) since those rarely
    /// change for an already-known track and would force a full ReplaceWith
    /// for nothing.
    /// </summary>
    public bool TracksAreEquivalent(IReadOnlyList<PlaylistTrackDto> fetched)
    {
        if (_allTracks.Count != fetched.Count) return false;
        for (int i = 0; i < _allTracks.Count; i++)
        {
            if (!string.Equals(_allTracks[i].Id, fetched[i].Id, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>Kicks off the music-video availability enrichment for the
    /// current snapshot. No-op when the service isn't wired.</summary>
    public void TryTriggerVideoAvailabilityFetch(string playlistId, CancellationToken cancellationToken)
    {
        if (_musicVideoMetadata is null || _allTracks.Count == 0)
            return;

        // Snapshot to avoid touching _allTracks from a Task.Run continuation.
        var snapshot = _allTracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Uri))
            .ToList();
        if (snapshot.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _musicVideoMetadata.ApplyAvailabilityToAsync(
                    snapshot,
                    static t => t.Uri,
                    (t, v) =>
                    {
                        if (t.HasVideo == v) return;
                        t.HasVideo = v;
                    },
                    cancellationToken).ConfigureAwait(false);

                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed || PlaylistId != playlistId)
                        return;
                    NotifyVideoFilterProperties();
                    if (ShowOnlyVideoTracks)
                        ApplyFilterAndSort();
                });
            }
            catch (OperationCanceledException)
            {
                // Playlist navigation superseded this enrichment pass.
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Music-video availability enrichment failed for playlist {PlaylistId}", playlistId);
            }
        }, CancellationToken.None);
    }

    // ── Session-control chips ────────────────────────────────────────────────

    /// <summary>
    /// Rebuild the session-control chip row from the DTO's pre-parsed options.
    /// Preserves the previously-selected option across refreshes (e.g. a Mercury
    /// push that bumps the revision but doesn't change the chip set) so the
    /// user's current selection survives. Suppresses the SelectedChip change
    /// handler while re-seeding so we don't re-send a signal for a noop.
    /// </summary>
    public void BuildSessionControlChips(IReadOnlyList<SessionControlOption>? options)
    {
        // One-shot dump of every FormatAttributes entry on any playlist that
        // has session-control chips. Used to identify which attribute carries
        // the base62 control-group id (the segment between "session_control_
        // display$" and the option key in the signal POST). Once the key is
        // pinned in SelectedListContentMapper.SessionControlGroupIdKeys this
        // log can be demoted or removed.
        if (options is { Count: > 0 })
        {
            var chipDump = string.Join(" | ", options.Select(o => $"{o.OptionKey}[{o.DisplayName}]→{o.SignalIdentifier ?? "<null>"}"));
            _logger?.LogInformation(
                "[session-control-chips] playlist={PlaylistId} options: {Chips}",
                PlaylistId, chipDump);
        }

        _suppressSessionSignal = true;
        try
        {
            var previouslySelectedKey = SelectedSessionControlChip?.OptionKey;

            if (options is null || options.Count == 0)
            {
                SessionControlChips.Clear();
                SelectedSessionControlChip = null;
                return;
            }

            // Fast path: if the new option set has the exact same OptionKeys
            // as the current chips, skip Clear+Add. A signal-driven refresh
            // returns the same chip set 99 % of the time, and rebuilding
            // makes the just-clicked chip flash through "removed → re-added
            // at server position → animated back to 0", which the user reads
            // as a bounce. Keeping the existing instances preserves both the
            // current SelectedSessionControlChip and any prior move-to-front,
            // so the click-time animation is the only one the user sees.
            if (SessionControlChipsAreEquivalent(SessionControlChips, options))
            {
                OnPropertyChanged(nameof(HasSessionControlChips));
                return;
            }

            SessionControlChips.Clear();

            // The server tells us which chip is currently active via the
            // `session_control.selected_signals` format attribute (value is
            // the fully-formed signal identifier — same shape we'd POST on a
            // click). Spotify writes it after each successful /signals call,
            // so on first load we use it to seed the selection, and across
            // refreshes a user-driven selection still takes priority.
            string? serverSelectedIdentifier = null;
            var attributes = _playlistFormatAttributesProvider();
            if (attributes is not null &&
                attributes.TryGetValue("session_control.selected_signals", out var raw) &&
                !string.IsNullOrWhiteSpace(raw))
            {
                // The key is plural in case Spotify ever ships a comma-list.
                // For single-select chip rows we just take the first entry.
                serverSelectedIdentifier = raw.Split(',', 2)[0].Trim();
            }

            SessionControlChipViewModel? restored = null;
            SessionControlChipViewModel? serverActive = null;
            foreach (var option in options)
            {
                var chip = new SessionControlChipViewModel
                {
                    OptionKey = option.OptionKey,
                    Label = option.DisplayName,
                    SignalIdentifier = option.SignalIdentifier
                };
                SessionControlChips.Add(chip);
                if (previouslySelectedKey is not null &&
                    string.Equals(previouslySelectedKey, option.OptionKey, StringComparison.Ordinal))
                {
                    restored = chip;
                }
                if (serverSelectedIdentifier is not null &&
                    chip.SignalIdentifier is not null &&
                    string.Equals(serverSelectedIdentifier, chip.SignalIdentifier, StringComparison.Ordinal))
                {
                    serverActive = chip;
                }
            }

            // Restore the user's prior pick if they had one, otherwise honour
            // the server's "currently active" signal, otherwise leave nothing
            // selected (Spotify's first-party UI shows the default visual when
            // no chip is active — same here).
            SelectedSessionControlChip = restored ?? serverActive;

            // Hoist the active chip to index 0 so the click-driven move-to-
            // front survives every BuildSessionControlChips rebuild that
            // follows the /signals refresh cycle. Without this, the refresh
            // re-seats the chip in server order and the user sees their pick
            // bounce back from the front to wherever the server placed it.
            var active = SelectedSessionControlChip;
            if (active is not null)
            {
                var idx = SessionControlChips.IndexOf(active);
                if (idx > 0)
                    SessionControlChips.Move(idx, 0);
            }
        }
        finally
        {
            _suppressSessionSignal = false;
        }

        OnPropertyChanged(nameof(HasSessionControlChips));
    }

    // Fires when SelectedSessionControlChip changes. Skips during Build and
    // when the VM is missing the bits needed to send a signal (no SpClient,
    // no group id, no revision). Otherwise POSTs and refreshes tracks.
    partial void OnSelectedSessionControlChipChanged(
        SessionControlChipViewModel? oldValue,
        SessionControlChipViewModel? newValue)
    {
        if (_suppressSessionSignal) return;
        if (newValue is null) return;
        if (ReferenceEquals(oldValue, newValue)) return;
        if (_playlistCache is null)
        {
            _logger?.LogDebug("Session control chip selected but PlaylistCache not wired; ignoring");
            return;
        }
        if (string.IsNullOrEmpty(newValue.SignalIdentifier))
        {
            _logger?.LogInformation(
                "Session control chip '{Option}' has no advertised signal identifier; click ignored.",
                newValue.OptionKey);
            _suppressSessionSignal = true;
            SelectedSessionControlChip = oldValue;
            _suppressSessionSignal = false;
            return;
        }
        var revision = _playlistRevisionProvider();
        if (revision is null || revision.Length == 0)
        {
            _logger?.LogWarning("Session control chip selected but no revision available; ignoring");
            return;
        }

        // Cancel any prior in-flight signal so only the latest click's response
        // is applied.
        _sessionSignalCts?.Cancel();
        _sessionSignalCts?.Dispose();
        _sessionSignalCts = new CancellationTokenSource();
        var ct = _sessionSignalCts.Token;

        // Per-chip loading chase: clear any previously-loading chip (prior
        // click superseded), light up the new one. The vendored
        // SessionTokenView wires its container's IsLoading DP to this flag
        // via a programmatic binding in PrepareContainerForItemOverride, so
        // the chase-border beam (PendingBorderBeam template part) starts
        // immediately on the clicked chip.
        foreach (var chip in SessionControlChips)
            chip.IsLoading = false;
        newValue.IsLoading = true;
        _pendingSignalChip = newValue;

        // Move-to-front: reorder the clicked chip to index 0. The
        // SessionTokenItem's Composition implicit Offset animation picks up
        // the layout change and slides each affected token to its new
        // position over ~250 ms.
        var currentIdx = SessionControlChips.IndexOf(newValue);
        if (currentIdx > 0)
        {
            _suppressSessionSignal = true;
            try
            {
                SessionControlChips.Move(currentIdx, 0);
            }
            finally
            {
                _suppressSessionSignal = false;
            }
        }

        IsLoadingTracks = true;

        _ = ApplySessionControlSignalAsync(oldValue, newValue, revision, ct);
    }

    private async Task ApplySessionControlSignalAsync(
        SessionControlChipViewModel? oldValue,
        SessionControlChipViewModel newValue,
        byte[] revision,
        CancellationToken ct)
    {
        var playlistId = PlaylistId;
        var requestId = Guid.NewGuid().ToString();
        // Use the server-advertised identifier verbatim — no client-side
        // derivation. Each chip has its own unique group id embedded; the
        // pair is held by newValue.SignalIdentifier.
        var signalKey = newValue.SignalIdentifier!;

        try
        {
            // POST + cache-apply collapse into one call: the mutation service
            // captures the POST response (the re-personalised SelectedListContent
            // Spotify returns inline — no follow-up GET needed because it races
            // the signal-processing pipeline; no /diff because editorial mixes
            // 509 it) and hands the bytes to the cache, which maps + persists
            // + emits Changes.
            var ok = await _playlistMutationService.SendPlaylistSignalAsync(
                playlistId,
                revision,
                signalKey,
                requestId,
                ct).ConfigureAwait(false);

            if (!ok || ct.IsCancellationRequested || PlaylistId != playlistId)
                return;

            // Don't clear IsLoading here. The chase beam should keep going
            // until LoadTracksAsync re-renders the post-signal track list
            // (the visible boundary). LoadTracksAsync does the clear when
            // it sees _pendingSignalChip is non-null.
            //
            // The Changes event from ApplyFreshContentAsync wakes the
            // PlaylistStore subscription that ApplyDetail listens to; the
            // store re-runs FetchAsync against the now-fresh hot cache and
            // re-emits Ready, which triggers ApplyDetail + LoadTracksAsync.
        }
        catch (OperationCanceledException)
        {
            // Superseded by another click; the newer handler's "clear all
            // chips' IsLoading" loop already turned this one off.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Session control signal failed: playlist={PlaylistId} key={SignalKey}", playlistId, signalKey);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (PlaylistId != playlistId) return;
                newValue.IsLoading = false;
                if (ReferenceEquals(_pendingSignalChip, newValue))
                    _pendingSignalChip = null;
                _suppressSessionSignal = true;
                SelectedSessionControlChip = oldValue;
                _suppressSessionSignal = false;
                IsLoadingTracks = false;
            });
        }
    }

    // Set-equality on OptionKeys for the chip row. Returns true when the
    // server-provided `incoming` options have the exact same keys as the
    // chips currently bound to the UI — order ignored. The fast-path in
    // BuildSessionControlChips short-circuits the rebuild when this is
    // true, so the click-time move-to-front survives the refresh cycle
    // without bouncing.
    private static bool SessionControlChipsAreEquivalent(
        IReadOnlyList<SessionControlChipViewModel> current,
        IReadOnlyList<SessionControlOption> incoming)
    {
        if (current.Count != incoming.Count) return false;
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in current)
            currentKeys.Add(c.OptionKey);
        foreach (var o in incoming)
        {
            if (!currentKeys.Contains(o.OptionKey))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Stop the per-chip chase-border beam. Called by the parent after the
    /// post-click track list has been applied (or determined unchanged) — the
    /// visible boundary the user expects the beam to track.
    /// </summary>
    public void ClearPendingSignalChip()
    {
        if (_pendingSignalChip is { } pending)
        {
            pending.IsLoading = false;
            _pendingSignalChip = null;
        }
    }

    // ── Empty-state genre grid ───────────────────────────────────────────────

    private void MaybeLoadEmptyPlaylistGenres()
    {
        if (!ShowEmptyPlaylistState || HasEmptyPlaylistGenreItems || _emptyPlaylistGenresLoadStarted)
            return;
        if (_homeFeedService is null || !_homeFeedService.IsAvailable)
            return;

        _emptyPlaylistGenresLoadStarted = true;
        _emptyPlaylistGenresCts?.Cancel();
        _emptyPlaylistGenresCts?.Dispose();
        _emptyPlaylistGenresCts = new CancellationTokenSource();
        _ = LoadEmptyPlaylistGenresAsync(_emptyPlaylistGenresCts.Token);
    }

    private async Task LoadEmptyPlaylistGenresAsync(CancellationToken ct)
    {
        try
        {
            if (_homeFeedService is null) return;
            var response = await _homeFeedService.GetBrowseAllAsync(ct).ConfigureAwait(false);
            if (response is null) return;
            var genres = BrowseAllGrouper
                .Genres(BrowseAllParser.Extract(response))
                .Take(18)
                .ToList();
            ct.ThrowIfCancellationRequested();

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || ct.IsCancellationRequested)
                    return;

                EmptyPlaylistGenreItems.Clear();
                foreach (var item in genres)
                    EmptyPlaylistGenreItems.Add(item);

                OnPropertyChanged(nameof(HasEmptyPlaylistGenreItems));
                OnPropertyChanged(nameof(ShowEmptyPlaylistGenreGrid));
            });
        }
        catch (OperationCanceledException)
        {
            _emptyPlaylistGenresLoadStarted = false;
        }
        catch (Exception ex)
        {
            _emptyPlaylistGenresLoadStarted = false;
            _logger?.LogDebug(ex, "LoadEmptyPlaylistGenresAsync failed");
        }
    }

    // ── Rootlist for the add-to-playlist flyout ──────────────────────────────

    /// <summary>Refreshes the destinations list bound by the "Add to playlist"
    /// flyout. Background — kicked off from the parent's Activate so the page
    /// doesn't gate its detail render on it.</summary>
    public async Task LoadRootlistAsync()
    {
        try
        {
            var list = await _libraryDataService.GetUserPlaylistsAsync().ConfigureAwait(false);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed) return;
                _playlists.ReplaceWith(list);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadRootlistAsync failed");
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SortBy(string? columnName)
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
    private void PlayTrack(object? track)
    {
        if (track is not ITrackItem trackItem) return;
        var index = FilteredTracks.ToList().FindIndex(t => t.Id == trackItem.Id);
        BuildQueueAndPlay(index >= 0 ? index : 0, shuffle: false);
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

    /// <summary>Enqueue every track of this playlist, in current sort order.
    /// Mirrors AlbumViewModel.AddAlbumToQueueCommand — same wire path
    /// (<c>IPlaybackStateService.AddToQueue</c>). Used by the labeled pill in
    /// the new action cluster.</summary>
    [RelayCommand]
    private void AddPlaylistToQueue()
    {
        if (_allTracks.Count == 0) return;
        var trackUris = _allTracks
            .Select(t => t.Uri)
            .Where(u => !string.IsNullOrEmpty(u))
            .Cast<string>()
            .ToList();
        if (trackUris.Count == 0) return;
        _playbackStateService.AddToQueue(trackUris);
    }

    /// <summary>Play-next every track of this playlist, in current sort order.
    /// Counterpart to <see cref="AddPlaylistToQueue"/> exposed via the SplitButton's
    /// dropdown. Inserts at the head of the user queue so the first track plays
    /// right after the current one, then the rest in order.</summary>
    [RelayCommand]
    private void PlayPlaylistNext()
    {
        if (_allTracks.Count == 0) return;
        var trackUris = _allTracks
            .Select(t => t.Uri)
            .Where(u => !string.IsNullOrEmpty(u))
            .Cast<string>()
            .ToList();
        if (trackUris.Count == 0) return;
        _playbackStateService.PlayNext(trackUris);
    }

    /// <summary>Seeds Spotify radio from this playlist's URI. Mirrors
    /// AlbumViewModel.StartAlbumRadioAsyncCommand.</summary>
    [RelayCommand]
    private async Task StartPlaylistRadioAsync()
    {
        if (string.IsNullOrEmpty(PlaylistId)) return;
        var seed = PlaylistId.StartsWith("spotify:playlist:", StringComparison.Ordinal)
            ? PlaylistId
            : $"spotify:playlist:{PlaylistId}";
        var displayName = _playlistNameProvider();
        var name = displayName is { Length: > 0 } n ? $"{n} Radio" : "Playlist Radio";
        await _playbackStateService.StartRadioAsync(seed, name);
    }

    /// <summary>Adds every track of THIS playlist to ANOTHER user playlist.
    /// Caller supplies the destination (typically picked from a flyout
    /// bound to <see cref="Playlists"/>). Filters out non-Spotify URIs.</summary>
    [RelayCommand]
    private async Task AddPlaylistToOtherPlaylistAsync(PlaylistSummaryDto? destination)
    {
        if (destination?.Id is not { Length: > 0 } destinationId) return;
        if (_allTracks.Count == 0) return;
        var trackUris = _allTracks
            .Select(t => t.Uri)
            .Where(u => SpotifyUriHelper.IsKind(u, SpotifyEntityKind.Track))
            .Cast<string>()
            .ToList();
        if (trackUris.Count == 0) return;

        try
        {
            await _playlistMutationService.AddTracksToPlaylistAsync(destinationId, trackUris).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AddPlaylistToOtherPlaylistAsync failed for {Source} → {Destination}",
                PlaylistId, destinationId);
        }
    }

    private void BuildQueueAndPlay(int startIndex, bool shuffle)
    {
        if (FilteredTracks.Count == 0) return;

        var fallbackImage = _playlistImageUrlProvider();
        var queueItems = FilteredTracks.Select(t => new QueueItem
        {
            TrackId = t.Id,
            Title = t.Title,
            ArtistName = t.ArtistName,
            AlbumArt = t.ImageUrl ?? fallbackImage,
            DurationMs = t.Duration.TotalMilliseconds,
            IsUserQueued = false,
            // Uid + Metadata round-trip from the playlist API (PlaylistTrackDto
            // was populated from CachedPlaylistItem.ItemId and FormatAttributes).
            // Published as ProvidedTrack.uid and ProvidedTrack.metadata so remote
            // clients see the same per-track decorations Spotify desktop emits.
            Uid = t.Uid,
            Metadata = t.FormatAttributes,
            AlbumName = t is PlaylistTrackDto p1 ? p1.AlbumName : null,
            AlbumUri = t is PlaylistTrackDto p2 && !string.IsNullOrEmpty(p2.AlbumId) ? p2.AlbumId : null,
            ArtistUri = string.IsNullOrEmpty(t.ArtistId) ? null : t.ArtistId,
            IsExplicit = t.IsExplicit,
            HasVideo = t.HasVideo,
        }).ToList();

        if (shuffle)
        {
            queueItems.Shuffle();
            startIndex = 0;
        }

        var context = new PlaybackContextInfo
        {
            ContextUri = PlaylistId,
            Type = PlaybackContextType.Playlist,
            Name = _playlistNameProvider() ?? string.Empty,
            ImageUrl = fallbackImage,
            // Playlist-level format attributes from the API — forwarded verbatim
            // into PlayerState.context_metadata (format, request_id, tag,
            // source-loader, image_url, session_control_display.displayName.*, …).
            FormatAttributes = _playlistFormatAttributesProvider()
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

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveSelectedAsync()
        => CanRemove
            ? RemoveTrackIdsAsync(SelectedItems.OfType<PlaylistTrackDto>().Select(t => t.Id).ToList())
            : Task.CompletedTask;

    /// <summary>
    /// Multi-track removal driven by the floating selection bar and the
    /// multi-select context menu — receives the explicit selection as a
    /// parameter (the <c>TrackDataGrid</c> owns selection state, not this VM's
    /// <see cref="SelectedItems"/>). Bound to <c>TrackDataGrid.MultiSelectRemoveCommand</c>.
    /// </summary>
    [RelayCommand]
    private Task RemoveTracksAsync(IReadOnlyList<ITrackItem>? tracks)
    {
        if (!_canEditItemsProvider() || tracks is null)
            return Task.CompletedTask;

        var ids = tracks
            .Select(t => t.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();
        return RemoveTrackIdsAsync(ids);
    }

    private async Task RemoveTrackIdsAsync(IReadOnlyList<string> trackIds)
    {
        if (trackIds.Count == 0) return;

        await _playlistMutationService.RemoveTracksFromPlaylistAsync(PlaylistId, trackIds);

        var idsToRemove = trackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _allTracks.RemoveAll(t => idsToRemove.Contains(t.Id));
        UpdateAggregates();
        ApplyFilterAndSort();
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddToPlaylist(PlaylistSummaryDto? playlist)
    {
        if (playlist == null || !HasSelection) return;
    }

    /// <summary>
    /// Drag-reorder a contiguous block of tracks within this playlist. Called
    /// from the WinUI ListView's drop handler after the user moves a row
    /// (or selection). Performs the optimistic local move immediately, then
    /// posts the change to Spotify; on failure restores the prior order and
    /// surfaces a notification.
    /// </summary>
    public async Task ReorderTracksAsync(int fromIndex, int length, int toIndex, CancellationToken ct = default)
    {
        if (!CanReorderTracks) return;
        if (length <= 0) return;
        if (string.IsNullOrEmpty(PlaylistId)) return;
        if (fromIndex < 0 || fromIndex >= _allTracks.Count) return;
        if (toIndex >= fromIndex && toIndex < fromIndex + length) return;

        var snapshot = _allTracks.ToList();
        try
        {
            // Local optimistic move (server is the source of truth for the
            // committed order, but we don't want to wait for the round-trip).
            var moving = _allTracks.GetRange(fromIndex, length);
            _allTracks.RemoveRange(fromIndex, length);
            var insertAt = toIndex > fromIndex ? toIndex - length : toIndex;
            insertAt = Math.Clamp(insertAt, 0, _allTracks.Count);
            _allTracks.InsertRange(insertAt, moving);
            UpdateAggregates();
            ApplyFilterAndSort();
            TracksChanged?.Invoke(this, EventArgs.Empty);

            await _playlistMutationService.ReorderTracksInPlaylistAsync(PlaylistId, fromIndex, length, toIndex, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to reorder tracks in playlist {PlaylistId} (from={From}, length={Length}, to={To})",
                PlaylistId, fromIndex, length, toIndex);
            _allTracks = snapshot;
            UpdateAggregates();
            ApplyFilterAndSort();
            TracksChanged?.Invoke(this, EventArgs.Empty);
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Couldn't reorder tracks", NotificationSeverity.Warning, TimeSpan.FromSeconds(4));
        }
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>Resets transient state for a fresh playlist activation. Called by
    /// the parent's Activate() during the isNewPlaylist branch — clears tracks,
    /// chips, aggregates, video filter, search query.</summary>
    public void ResetForNewPlaylist()
    {
        _allTracks = new List<PlaylistTrackDto>();
        ShowOnlyVideoTracks = false;
        NotifyVideoFilterProperties();
        TotalTracks = 0;
        TotalDuration = string.Empty;
        HasAnyAddedAt = false;

        _suppressSessionSignal = true;
        SessionControlChips.Clear();
        SelectedSessionControlChip = null;
        _suppressSessionSignal = false;
        OnPropertyChanged(nameof(HasSessionControlChips));
        _sessionSignalCts?.Cancel();
        _sessionSignalCts?.Dispose();
        _sessionSignalCts = null;

        if (FilteredTracks.Count > 0)
            FilteredTracks.Clear();

        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hibernate path — also dumps the empty-state genre grid and
    /// FilteredTracks so the cached page doesn't pin realized item containers
    /// while invisible in the Frame cache.</summary>
    public void Hibernate()
    {
        Deactivate();
        FilteredTracks.Clear();
        _allTracks = new List<PlaylistTrackDto>();
        ShowOnlyVideoTracks = false;
        NotifyVideoFilterProperties();
        _suppressSessionSignal = true;
        SessionControlChips.Clear();
        SelectedSessionControlChip = null;
        _suppressSessionSignal = false;
        OnPropertyChanged(nameof(HasSessionControlChips));
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancels in-flight async work that belongs to this VM (session
    /// signal POST, empty-state genres fetch, search debounce).</summary>
    public void Deactivate()
    {
        _sessionSignalCts?.Cancel();
        _sessionSignalCts?.Dispose();
        _sessionSignalCts = null;
        _emptyPlaylistGenresCts?.Cancel();
        _emptyPlaylistGenresCts?.Dispose();
        _emptyPlaylistGenresCts = null;
        // Search timer holds a Tick closure over `this`; stop it on nav-away so it
        // doesn't fire against a cached-but-hidden page.
        _searchDebounceTimer.Stop();
    }

    public void Dispose()
    {
        _disposed = true;
        Deactivate();
        FilteredTracks.Clear();
        _allTracks = new List<PlaylistTrackDto>();
        SessionControlChips.Clear();
        EmptyPlaylistGenreItems.Clear();
        SelectedItems = Array.Empty<object>();
        SelectedSessionControlChip = null;
    }

    // ── Explicit ITrackListViewModel ICommand implementation ─────────────────
    // The interface exposes commands as ICommand; the source-gen properties are
    // typed as IRelayCommand / IAsyncRelayCommand. Wire them through here.

    ICommand ITrackListViewModel.SortByCommand => SortByCommand;
    ICommand ITrackListViewModel.PlayTrackCommand => PlayTrackCommand;
    ICommand ITrackListViewModel.PlaySelectedCommand => PlaySelectedCommand;
    ICommand ITrackListViewModel.PlayAfterCommand => PlayAfterCommand;
    ICommand ITrackListViewModel.AddSelectedToQueueCommand => AddSelectedToQueueCommand;
    ICommand ITrackListViewModel.RemoveSelectedCommand => RemoveSelectedCommand;
    ICommand ITrackListViewModel.AddToPlaylistCommand => AddToPlaylistCommand;
}

/// <summary>
/// One chip in <see cref="PlaylistTrackListViewModel.SessionControlChips"/>.
/// Selected state is owned by the parent VM's
/// <see cref="PlaylistTrackListViewModel.SelectedSessionControlChip"/> property so it
/// two-way binds cleanly to <c>SessionTokenView.SelectedItem</c>. The
/// <see cref="IsLoading"/> flag bubbles up to the vendored
/// <c>SessionTokenItem.IsLoading</c> DP via an ItemContainerStyle binding,
/// driving the chase-around-border animation while the signal POST is
/// in flight.
/// </summary>
public sealed partial class SessionControlChipViewModel : ObservableObject
{
    /// <summary>Raw option key (e.g. <c>pop_rock</c>) — used for identity/matching.</summary>
    public required string OptionKey { get; init; }

    /// <summary>Human-readable label rendered in the chip (e.g. <c>Pop Rock</c>).</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Fully-formed signal identifier this chip posts on click (e.g.
    /// <c>session_control_display$24pGOSaKeoU6bobuwqnMbJ$pop</c>). Null when
    /// the server didn't advertise one — click short-circuits in that case.
    /// </summary>
    public string? SignalIdentifier { get; init; }

    [ObservableProperty]
    private bool _isLoading;
}
