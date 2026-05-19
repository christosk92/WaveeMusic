using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.Helpers.Artist;
using Wavee.UI.Models;
using Wavee.UI.Services.Infra;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels.Artist;

/// <summary>
/// Owns the artist's top-tracks list (initial + extended), the page-based
/// pagination state, the per-row selection collection and the
/// <c>PlayTopTracksCommand</c> / <c>PlayTrackCommand</c> + play-pending
/// timeout window. Extracted from <c>ArtistViewModel</c>.
///
/// <para>The VM does NOT subscribe to <c>IPlaybackStateService</c>'s
/// PropertyChanged itself — the parent owns the long-lived subscription and
/// pushes derived state into <see cref="SyncArtistPlaybackState"/>.</para>
/// </summary>
public sealed partial class ArtistTopTracksViewModel : ObservableObject, IDisposable
{
    // ── Tuning constants ─────────────────────────────────────────────────────
    private const int PlayPendingTimeoutMs = 8000;
    private const int RowsPerPage = 4;

    private readonly IPlaybackService _playbackService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly IArtistService _artistService;
    private readonly IMusicVideoMetadataService? _musicVideoMetadataService;
    private readonly IBackgroundWorkRunner _backgroundWork;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;

    private readonly Func<string?> _artistIdProvider;
    private readonly Func<string?> _artistNameProvider;
    private readonly Func<string?> _artistImageUrlProvider;
    private readonly Func<int, bool> _isCurrentLoadProvider;
    private readonly Func<int> _currentGenerationProvider;

    private CancellationTokenSource? _playPendingCts;

    public ArtistTopTracksViewModel(
        IPlaybackService playbackService,
        IPlaybackStateService playbackStateService,
        IArtistService artistService,
        IMusicVideoMetadataService? musicVideoMetadataService,
        IBackgroundWorkRunner backgroundWork,
        ILogger? logger,
        Func<string?> artistIdProvider,
        Func<string?> artistNameProvider,
        Func<string?> artistImageUrlProvider,
        Func<int, bool> isCurrentLoadProvider,
        Func<int> currentGenerationProvider)
    {
        _playbackService = playbackService;
        _playbackStateService = playbackStateService;
        _artistService = artistService;
        _musicVideoMetadataService = musicVideoMetadataService;
        _backgroundWork = backgroundWork;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _artistIdProvider = artistIdProvider;
        _artistNameProvider = artistNameProvider;
        _artistImageUrlProvider = artistImageUrlProvider;
        _isCurrentLoadProvider = isCurrentLoadProvider;
        _currentGenerationProvider = currentGenerationProvider;

        // Named handler so Dispose can -= against the same delegate.
        SelectedTopTracks.CollectionChanged += OnSelectedTopTracksChanged;
    }

    // ── UI-bound collections ────────────────────────────────────────────────

    private readonly ObservableCollection<LazyTrackItem> _tracks = [];

    /// <summary>
    /// Bound observable collection kept as a stable instance and mutated in
    /// place. Assigning a new reference forces ItemsRepeater to recycle every
    /// realized container; mutating the same instance lets the binding stay
    /// subscribed and avoids a full rebuild on cached-page restore.
    /// </summary>
    public ObservableCollection<LazyTrackItem> Tracks => _tracks;

    /// <summary>First 10 top tracks — Spotify's ArtistOverview returns 10 by
    /// default (extendable up to 50), and the V4A magazine page pairs that
    /// dense list next to the popular-releases column. Static slice; raised
    /// manually after every Tracks rebuild.</summary>
    public IEnumerable<LazyTrackItem> TopTracksFirst10 =>
        _tracks.Count == 0
            ? Array.Empty<LazyTrackItem>()
            : _tracks.Take(10);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopTracksSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedTopTracksCount))]
    private ObservableCollection<LazyTrackItem> _selectedTopTracks = [];

    public int SelectedTopTracksCount => SelectedTopTracks.Count;
    public bool HasTopTracksSelection => SelectedTopTracks.Count > 0;

    public bool IsTopTrackSelected(LazyTrackItem item) => SelectedTopTracks.Contains(item);

    public void ClearTopTracksSelection()
    {
        if (SelectedTopTracks.Count == 0) return;
        SelectedTopTracks.Clear();
    }

    private void OnSelectedTopTracksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectedTopTracksCount));
        OnPropertyChanged(nameof(HasTopTracksSelection));
    }

    // ── Playback state mirrors ──────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistPlayButtonText))]
    private bool _isPlayPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistPlayButtonText))]
    private bool _isArtistContextPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistPlayButtonText))]
    private bool _isArtistContextPaused;

    public string ArtistPlayButtonText => IsArtistContextPlaying ? "Pause" : "Play";

    // ── Pagination ──────────────────────────────────────────────────────────

    [ObservableProperty] private int _columnCount = 1;
    [ObservableProperty] private int _currentPage;

    private int TracksPerPage => RowsPerPage * ColumnCount;

    /// <summary>Page-size hint used by the parent's LoadAsync to pad placeholder
    /// rows. Falls back to 12 (the original 3 cols × 4 rows layout) when the
    /// repeater hasn't reported its column count yet.</summary>
    internal int PlaceholderPageSize => TracksPerPage > 0 ? TracksPerPage : 12;

    public int TotalPages => Tracks.Count == 0 ? 0 : (int)Math.Ceiling((double)Tracks.Count / TracksPerPage);
    public bool HasMultiplePages => TotalPages > 1;

    // Stable ObservableCollection instance. Two mutation paths:
    //
    //  * ReconcileSliceInPlace — used after Tracks mutations from VM-internal
    //    work (LoadExtendedTopTracksAsync, image enrichment). Preserves rows
    //    whose LazyTrackItem reference didn't change so their CompositionImage
    //    LRU pins survive, killing the "loads then flickers away" pattern on
    //    the top-tracks band when extended tracks land ~250 ms after paint.
    //
    //  * RepopulateSlice — used for user-driven page changes and column-count
    //    changes. Clear + Add gives ItemsRepeater a Reset signal it handles
    //    cleanly; a long INCC.Replace sequence doesn't drive ItemsRepeater +
    //    NonVirtualizingLayout reliably enough for the pip-click swap.
    //
    // Either way the OC instance itself is stable — no PropertyChanged fires
    // on PagedTopTracks, so x:Bind never re-evaluates the ItemsSource.
    private readonly ObservableCollection<LazyTrackItem> _pagedTopTracks = new();
    public ObservableCollection<LazyTrackItem> PagedTopTracks => _pagedTopTracks;

    /// <summary>
    /// In-place reconcile: rows whose LazyTrackItem reference matches keep
    /// their container; mismatched rows fire one INCC.Replace each; growth /
    /// shrink uses Add / RemoveAt. Used after Tracks mutation
    /// (LoadExtendedTopTracksAsync, initial overview ReplaceWith) where most
    /// rows are unchanged — this avoids the full ItemsRepeater recycle that
    /// dropped the CompositionImage LRU pins and produced the visible
    /// "loads then flickers away" pattern on the top-tracks band.
    /// </summary>
    private void ReconcileSliceInPlace()
    {
        int start = CurrentPage * TracksPerPage;
        int available = Tracks.Count - start;
        int desired = available <= 0 ? 0 : Math.Min(TracksPerPage, available);

        for (int i = 0; i < desired; i++)
        {
            var newItem = Tracks[start + i];
            if (i < _pagedTopTracks.Count)
            {
                if (!ReferenceEquals(_pagedTopTracks[i], newItem))
                    _pagedTopTracks[i] = newItem;
            }
            else
            {
                _pagedTopTracks.Add(newItem);
            }
        }
        while (_pagedTopTracks.Count > desired)
            _pagedTopTracks.RemoveAt(_pagedTopTracks.Count - 1);
    }

    /// <summary>
    /// Reset semantics: Clear + Add the new slice. ItemsRepeater fully
    /// recycles every container. Used for explicit user-driven slice swaps
    /// — page change (every visible row IS a different track) and column-
    /// count change (slice shape changes). A long sequence of INCC.Replace
    /// events doesn't drive ItemsRepeater + NonVirtualizingLayout reliably;
    /// Reset does.
    /// </summary>
    private void RepopulateSlice()
    {
        _pagedTopTracks.Clear();
        int start = CurrentPage * TracksPerPage;
        int available = Tracks.Count - start;
        int desired = available <= 0 ? 0 : Math.Min(TracksPerPage, available);
        for (int i = 0; i < desired; i++)
            _pagedTopTracks.Add(Tracks[start + i]);
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < TotalPages - 1)
            CurrentPage++;
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPage > 0)
            CurrentPage--;
    }

    /// <summary>
    /// Called by the parent after a Tracks mutation (initial ReplaceWith,
    /// extended-tracks append, image enrichment). Uses in-place reconcile so
    /// rows whose LazyTrackItem reference is unchanged keep their realized
    /// container — that's what stops the artist top-tracks band from
    /// flickering when extended-tracks land ~250 ms after first paint.
    /// </summary>
    public void NotifyPaginationChanged()
    {
        ReconcileSliceInPlace();
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(HasMultiplePages));
    }

    partial void OnCurrentPageChanged(int value)
    {
        ClearTopTracksSelection();
        // Explicit user-driven page swap. Reset semantics — every visible row
        // becomes a different track, full recycle is the expected UX.
        RepopulateSlice();
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(HasMultiplePages));
    }

    partial void OnColumnCountChanged(int value)
    {
        ClearTopTracksSelection();
        // Slice shape changes (TracksPerPage shifts). Resetting CurrentPage to
        // 0 may or may not fire OnCurrentPageChanged depending on whether it
        // was already 0 — capture that up front so we run RepopulateSlice
        // exactly once in either case.
        var pageAlreadyZero = CurrentPage == 0;
        CurrentPage = 0;
        if (pageAlreadyZero)
        {
            RepopulateSlice();
            OnPropertyChanged(nameof(TotalPages));
            OnPropertyChanged(nameof(HasMultiplePages));
        }
    }

    // ── Reset / playback sync ───────────────────────────────────────────────

    public void ResetForNewArtist()
    {
        IsPlayPending = false;
        IsArtistContextPlaying = false;
        IsArtistContextPaused = false;
        CurrentPage = 0;
        Tracks.Clear();
        SelectedTopTracks.Clear();
        NotifyPaginationChanged();
    }

    /// <summary>
    /// Mirror the playback-state singleton's view onto this VM. Called by
    /// the parent from its long-lived PropertyChanged handler — keeps the
    /// child free of singleton subscriptions.
    /// </summary>
    public void SyncArtistPlaybackState()
    {
        bool isArtistContext = IsArtistContextActive();
        IsArtistContextPlaying = isArtistContext && _playbackStateService.IsPlaying;
        IsArtistContextPaused = isArtistContext && !_playbackStateService.IsPlaying;

        if (IsPlayPending && (!isArtistContext || IsArtistContextPlaying))
            SetPlayPending(false);
    }

    private bool IsArtistContextActive()
        => ArtistContextMatcher.IsActive(_playbackStateService.CurrentContext?.ContextUri, _artistIdProvider());

    private void SetPlayPending(bool pending)
    {
        if (IsPlayPending == pending)
            return;

        IsPlayPending = pending;
        _playPendingCts?.Cancel();
        _playPendingCts?.Dispose();
        _playPendingCts = null;

        if (!pending)
            return;

        _playPendingCts = new CancellationTokenSource();
        _backgroundWork.Run(_ => ClearPlayPendingAfterTimeoutAsync(_playPendingCts.Token), "ArtistTopTracksViewModel.ClearPlayPendingAfterTimeout");
    }

    private async Task ClearPlayPendingAfterTimeoutAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(PlayPendingTimeoutMs, ct);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!ct.IsCancellationRequested && IsPlayPending)
                {
                    SetPlayPending(false);
                    _playbackStateService.ClearBuffering();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ── Play commands ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task PlayTopTracksAsync()
    {
        var artistId = _artistIdProvider();
        if (string.IsNullOrEmpty(artistId)) return;

        PlaybackResult result;
        if (IsArtistContextPlaying)
        {
            result = await _playbackService.PauseAsync();
        }
        else if (IsArtistContextPaused)
        {
            SetPlayPending(true);
            _playbackStateService.NotifyBuffering(null);
            result = await _playbackService.ResumeAsync();
        }
        else
        {
            SetPlayPending(true);
            _playbackStateService.NotifyBuffering(null);
            result = await _playbackService.PlayContextAsync(
                artistId,
                new PlayContextOptions { PlayOriginFeature = "artist_page" });
        }

        if (!result.IsSuccess)
        {
            SetPlayPending(false);
            _playbackStateService.ClearBuffering();
            _logger?.LogWarning("PlayTopTracks failed: {Error}", result.ErrorMessage);
        }
    }

    [RelayCommand]
    private async Task PlayTrackAsync(ITrackItem? track)
    {
        var artistId = _artistIdProvider();
        if (track == null || string.IsNullOrEmpty(artistId)) return;

        // Build rich QueueItems from Tracks so remote clients receive per-track
        // uid + metadata (artist_uri, album_uri, album_title, title, track_player)
        // the same way Spotify desktop does. Without this, the published queue
        // comes across as bare track URIs with context_uri="spotify:internal:queue".
        // Mirrors PlaylistViewModel.BuildQueueAndPlay.
        var queueItems = new List<QueueItem>(Tracks.Count);
        int startIndex = -1;
        foreach (var t in Tracks)
        {
            if (!t.IsLoaded || t.Data is not ITrackItem item) continue;
            if (string.IsNullOrEmpty(item.Uri)) continue;

            if (startIndex < 0 && item.Uri == track.Uri)
                startIndex = queueItems.Count;

            var metadata = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(item.ArtistId))
                metadata["artist_uri"] = $"spotify:artist:{item.ArtistId}";
            if (!string.IsNullOrEmpty(item.AlbumId))
                metadata["album_uri"] = $"spotify:album:{item.AlbumId}";
            if (!string.IsNullOrEmpty(item.AlbumName))
                metadata["album_title"] = item.AlbumName;
            if (!string.IsNullOrEmpty(item.Title))
                metadata["title"] = item.Title;
            metadata["track_player"] = "audio";

            queueItems.Add(new QueueItem
            {
                TrackId = item.Id,
                Title = item.Title,
                ArtistName = item.ArtistName,
                AlbumArt = item.ImageUrl,
                DurationMs = item.Duration.TotalMilliseconds,
                IsUserQueued = false,
                // "toptrack{id}" matches the uid pattern Spotify's
                // context-resolve/v1/spotify:artist:{id} returns for page 0
                // (the top-tracks page). The server uses this to address a
                // specific instance for skip-to-uid.
                Uid = $"toptrack{item.Id}",
                Metadata = metadata,
                AlbumName = !string.IsNullOrEmpty(item.AlbumName) ? item.AlbumName : null,
                AlbumUri = !string.IsNullOrEmpty(item.AlbumId) ? $"spotify:album:{item.AlbumId}" : null,
                ArtistUri = !string.IsNullOrEmpty(item.ArtistId) ? $"spotify:artist:{item.ArtistId}" : null,
                IsExplicit = item.IsExplicit,
            });
        }

        if (queueItems.Count == 0 || startIndex < 0)
        {
            // Clicked track isn't in the local Tracks cache — fall back to
            // server-side context resolution.
            var fallbackResult = await _playbackService.PlayTrackInContextAsync(track.Uri, artistId,
                new PlayContextOptions { PlayOriginFeature = "artist_page" });
            if (!fallbackResult.IsSuccess)
            {
                _playbackStateService.ClearBuffering();
                _logger?.LogWarning("Play artist track failed: {Error}", fallbackResult.ErrorMessage);
            }
            return;
        }

        var artistName = _artistNameProvider();
        var artistImageUrl = _artistImageUrlProvider();
        var context = new PlaybackContextInfo
        {
            ContextUri = artistId,
            Type = PlaybackContextType.Artist,
            Name = artistName,
            ImageUrl = artistImageUrl,
            // Matches context-resolve/v1/spotify:artist:{id}.metadata. Forwarded
            // into PlayerState.context_metadata so other clients render
            // "Playing from {artist}" correctly.
            FormatAttributes = new Dictionary<string, string>
            {
                ["context_description"] = artistName ?? string.Empty,
                ["artist_context_type"] = "km_artist",
            }
        };

        var trackUris = queueItems.Select(item => item.TrackId).ToList();
        var result = await _playbackService.PlayTracksAsync(
            trackUris,
            startIndex,
            context,
            queueItems,
            CancellationToken.None);

        if (!result.IsSuccess)
        {
            _playbackStateService.ClearBuffering();
            _logger?.LogWarning("Play artist top-track queue failed: {Error}", result.ErrorMessage);
        }
    }

    // ── Track image enrichment + extended-tracks fetch ──────────────────────

    /// <summary>
    /// Backfills <see cref="ArtistTopTrackVm.AlbumImageUrl"/> for any
    /// initial top tracks the GraphQL overview returned without cover art.
    /// Resolves the missing URLs via the extended-metadata pipeline (cache
    /// + batched TrackV4 fetch) and populates the existing
    /// <see cref="LazyTrackItem"/> wrappers so visible <c>TrackItem</c>
    /// controls receive the image-property notification immediately.
    /// </summary>
    public async Task EnrichMissingTopTrackImagesAsync(string artistId, int generation)
    {
        try
        {
            if (!_isCurrentLoadProvider(generation))
                return;

            // Snapshot URIs needing enrichment (called off-dispatcher right
            // after Tracks is replaced — safe to read without a lock).
            var snapshot = Tracks.ToList();
            var missing = snapshot
                .Where(item => item.IsLoaded && item.Data is ArtistTopTrackVm vm
                               && !string.IsNullOrEmpty(vm.Uri)
                               && string.IsNullOrEmpty(vm.AlbumImageUrl))
                .Select(item => ((ArtistTopTrackVm)item.Data!).Uri!)
                .Distinct()
                .ToList();

            if (missing.Count == 0) return;

            var images = await Task.Run(() => _artistService.GetTrackImagesAsync(missing));
            if (images.Count == 0) return;
            if (!_isCurrentLoadProvider(generation)) return;

            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (!_isCurrentLoadProvider(generation))
                    return;

                bool anyPatched = false;
                for (int i = 0; i < Tracks.Count; i++)
                {
                    var entry = Tracks[i];
                    if (!entry.IsLoaded || entry.Data is not ArtistTopTrackVm vm) continue;
                    if (vm.Uri is not { Length: > 0 } uri) continue;
                    if (!string.IsNullOrEmpty(vm.AlbumImageUrl)) continue;
                    if (!ArtistTrackImageResolver.TryResolve(images, uri, out var imageUrl)
                        || string.IsNullOrEmpty(imageUrl)) continue;

                    var patched = new ArtistTopTrackVm
                    {
                        Id = vm.Id,
                        Index = vm.Index,
                        Title = vm.Title,
                        Uri = vm.Uri,
                        AlbumName = vm.AlbumName,
                        AlbumImageUrl = imageUrl,
                        AlbumUri = vm.AlbumUri,
                        Duration = vm.Duration,
                        PlayCountRaw = vm.PlayCountRaw,
                        ArtistNames = vm.ArtistNames,
                        IsExplicit = vm.IsExplicit,
                        IsPlayable = vm.IsPlayable,
                        HasCanvasVideo = vm.HasCanvasVideo,
                        HasLinkedLocalVideo = vm.HasLinkedLocalVideo,
                    };
                    entry.Populate(patched);
                    anyPatched = true;
                }

                if (anyPatched)
                {
                    OnPropertyChanged(nameof(TopTracksFirst10));
                }

                _logger?.LogInformation(
                    "Top-track image enrichment for {Artist}: requested={RequestedCount}, resolved={ResolvedCount}, patched={Patched}",
                    artistId,
                    missing.Count,
                    images.Count(kvp => !string.IsNullOrEmpty(kvp.Value)),
                    anyPatched);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich missing top-track images");
        }
    }

    /// <summary>
    /// Pull the artist's extended top-track list (10 → up to 50) via
    /// <see cref="IArtistService.GetExtendedTopTracksAsync"/>, strip the
    /// placeholder padding, then append any new entries that weren't already
    /// in the initial overview slice.
    /// </summary>
    public async Task LoadExtendedTopTracksAsync(string artistUri, int generation)
    {
        try
        {
            var extendedTracks = await Task.Run(async () => await _artistService.GetExtendedTopTracksAsync(artistUri));
            if (extendedTracks.Count == 0) return;
            if (!_isCurrentLoadProvider(generation)) return;

            var existingUris = new HashSet<string>(
                Tracks
                    .Where(i => i.IsLoaded && i.Data != null)
                    .Select(i => ((ArtistTopTrackVm)i.Data!).Uri ?? ""));

            var startIdx = Tracks.Count(i => i.IsLoaded) + 1;

            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (!_isCurrentLoadProvider(generation))
                    return;

                // Remove all placeholder items
                for (int i = Tracks.Count - 1; i >= 0; i--)
                {
                    if (!Tracks[i].IsLoaded)
                        Tracks.RemoveAt(i);
                }

                int idx = startIdx;
                var addedVms = new List<ArtistTopTrackVm>();
                foreach (var track in extendedTracks)
                {
                    if (existingUris.Contains(track.Uri ?? "")) continue;

                    var trackVm = new ArtistTopTrackVm
                    {
                        Id = track.Id,
                        Index = idx,
                        Title = track.Title,
                        Uri = track.Uri,
                        AlbumName = track.AlbumName,
                        AlbumImageUrl = track.AlbumImageUrl,
                        AlbumUri = track.AlbumUri,
                        Duration = track.Duration,
                        PlayCountRaw = track.PlayCount,
                        ArtistNames = track.ArtistNames,
                        IsExplicit = track.IsExplicit,
                        IsPlayable = track.IsPlayable,
                        HasCanvasVideo = track.HasVideo
                    };

                    Tracks.Add(LazyTrackItem.Loaded(trackVm.Id, idx, trackVm));
                    addedVms.Add(trackVm);
                    idx++;
                }

                NotifyPaginationChanged();

                if (addedVms.Count > 0)
                {
                    var videoMetadata = _musicVideoMetadataService;
                    if (videoMetadata is not null)
                    {
                        _backgroundWork.Run(
                            _ => videoMetadata.ApplyAvailabilityToAsync(
                                addedVms,
                                static t => t.Uri,
                                static (t, v) => t.HasLinkedLocalVideo = v,
                                CancellationToken.None),
                            "ArtistTopTracksViewModel.ApplyMusicVideoAvailability.AddedRows");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load extended top tracks for {Artist}", artistUri);
        }
    }

    /// <summary>
    /// Used by the parent during the initial overview apply. Recomputes the
    /// per-page slice + raises <see cref="TopTracksFirst10"/> so the V4A
    /// 10-track column updates after a Tracks.ReplaceWith. Pagination
    /// dependents are raised via <see cref="NotifyPaginationChanged"/>.
    /// </summary>
    public void NotifyTopTracksFirst10Changed()
    {
        OnPropertyChanged(nameof(TopTracksFirst10));
    }

    public void Dispose()
    {
        SelectedTopTracks.CollectionChanged -= OnSelectedTopTracksChanged;
        _playPendingCts?.Cancel();
        _playPendingCts?.Dispose();
        _playPendingCts = null;
    }
}
