using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wavee.Core.Data;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Stores;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Sort column options for playlist tracks.
/// </summary>
public enum PlaylistSortColumn { Custom, Title, Artist, Album, AddedAt }

/// <summary>
/// ViewModel for the Playlist detail page with imperative filtering and sorting.
/// </summary>
public sealed partial class PlaylistViewModel : ObservableObject, ITrackListViewModel, IDisposable
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly PlaylistStore _playlistStore;
    private readonly Services.PlaylistMosaicService? _mosaicService;
    private readonly Services.IUserProfileResolver? _userProfileResolver;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;

    private List<PlaylistTrackDto> _allTracks = [];
    private readonly DispatcherTimer _searchDebounceTimer;
    private CompositeDisposable? _subscriptions;
    private CancellationTokenSource? _tracksCts;
    private string? _tracksLoadedFor;
    private bool _disposed;

    [ObservableProperty]
    private string _playlistId = "";

    [ObservableProperty]
    private string _playlistName = "";

    [ObservableProperty]
    private string? _playlistDescription;

    [ObservableProperty]
    private string? _playlistImageUrl;

    // Preserved playlist-level format attributes (editorial chrome +
    // recommender context). Populated when PlaylistDetailDto is received
    // and forwarded into the play command so PlayerState.context_metadata
    // reproduces what Spotify Connect clients expect to see.
    private IReadOnlyDictionary<string, string>? _playlistFormatAttributes;

    /// <summary>
    /// Wide hero image populated from the playlist's <c>header_image_url_desktop</c>
    /// format attribute. Only editorial / radio playlists carry one; for user-created
    /// playlists this stays null and the page falls back to the square cover.
    /// </summary>
    [ObservableProperty]
    private string? _headerImageUrl;

    [ObservableProperty]
    private string _ownerName = "";

    [ObservableProperty]
    private bool _isOwner;

    [ObservableProperty]
    private bool _isPublic;

    /// <summary>Server-reported base role on this playlist (Viewer/Contributor/Owner).</summary>
    [ObservableProperty]
    private PlaylistBasePermission _basePermission = PlaylistBasePermission.Viewer;

    /// <summary>Whether the current user can add/remove/reorder tracks. Drives edit CTAs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    private bool _canEditItems;

    /// <summary>Whether the current user can change collaborators / sharing.</summary>
    [ObservableProperty]
    private bool _canAdministratePermissions;

    /// <summary>Whether the current user can leave the playlist (collaborators only).</summary>
    [ObservableProperty]
    private bool _canCancelMembership;

    /// <summary>Whether the current user can submit an abuse report.</summary>
    [ObservableProperty]
    private bool _canAbuseReport;

    [ObservableProperty]
    private int _followerCount;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private PlaylistSortColumn _currentSortColumn = PlaylistSortColumn.Custom;

    [ObservableProperty]
    private bool _isSortDescending = false;

    [ObservableProperty]
    private bool _isLoading;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCount))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionHeaderText))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    private IReadOnlyList<object> _selectedItems = Array.Empty<object>();

    [ObservableProperty]
    private IReadOnlyList<PlaylistSummaryDto> _playlists = Array.Empty<PlaylistSummaryDto>();

    /// <summary>
    /// Track rows bound to TrackListView. Holds either real <see cref="PlaylistTrackDto"/>s
    /// or <see cref="LazyTrackItem"/> placeholders during the initial load — both
    /// implement <see cref="ITrackItem"/>, and TrackListView renders per-row shimmer
    /// for any LazyTrackItem whose IsLoaded is false.
    /// </summary>
    public ObservableCollection<ITrackItem> FilteredTracks { get; } = [];

    /// <summary>
    /// Formatted follower count.
    /// </summary>
    public string FollowerCountFormatted => FollowerCount switch
    {
        0 => "",
        < 1000 => $"{FollowerCount} followers",
        < 1_000_000 => $"{FollowerCount / 1000.0:N1}K followers",
        _ => $"{FollowerCount / 1_000_000.0:N1}M followers"
    };

    public int SelectedCount => SelectedItems.Count;
    public bool HasSelection => SelectedItems.Count > 0;
    public string SelectionHeaderText => SelectedCount == 1
        ? "1 track selected"
        : $"{SelectedCount} tracks selected";

    // Sort indicator properties for column headers
    public bool IsSortingByTitle => CurrentSortColumn == PlaylistSortColumn.Title;
    public bool IsSortingByArtist => CurrentSortColumn == PlaylistSortColumn.Artist;
    public bool IsSortingByAlbum => CurrentSortColumn == PlaylistSortColumn.Album;
    public bool IsSortingByAddedAt => CurrentSortColumn == PlaylistSortColumn.AddedAt;

    public string SortChevronGlyph => IsSortDescending ? "\uE70D" : "\uE70E";

    public bool CanRemove => CanEditItems && HasSelection;

    public PlaylistViewModel(
        ILibraryDataService libraryDataService,
        IPlaybackStateService playbackStateService,
        PlaylistStore playlistStore,
        ILogger<PlaylistViewModel>? logger = null,
        Services.PlaylistMosaicService? mosaicService = null,
        Services.IUserProfileResolver? userProfileResolver = null)
    {
        _libraryDataService = libraryDataService;
        _playbackStateService = playbackStateService;
        _playlistStore = playlistStore;
        _mosaicService = mosaicService;
        _userProfileResolver = userProfileResolver;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplyFilterAndSort();
        };

        // No more DataChanged subscription — the store pushes updates via the
        // subscription set up in Activate().
    }

    partial void OnSearchQueryChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnCurrentSortColumnChanged(PlaylistSortColumn value)
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

    partial void OnFollowerCountChanged(int value)
    {
        OnPropertyChanged(nameof(FollowerCountFormatted));
    }

    partial void OnPlaylistNameChanged(string value)
    {
        // First two stack frames are this method + the property setter; the third is the caller.
        var stack = new System.Diagnostics.StackTrace(skipFrames: 1, fNeedFileInfo: false);
        var caller = stack.FrameCount > 1 ? stack.GetFrame(1)?.GetMethod() : null;
        _logger?.LogDebug(
            "PlaylistName -> '{Value}' (PlaylistId='{PlaylistId}', IsLoading={IsLoading}, Caller={Caller})",
            value, PlaylistId, IsLoading,
            caller is null ? "<unknown>" : $"{caller.DeclaringType?.Name}.{caller.Name}");
    }

    partial void OnIsLoadingChanged(bool value)
    {
        _logger?.LogDebug("IsLoading -> {Value} (PlaylistId='{PlaylistId}')", value, PlaylistId);
    }

    partial void OnIsLoadingTracksChanged(bool value)
    {
        _logger?.LogDebug(
            "IsLoadingTracks -> {Value} (PlaylistId='{PlaylistId}', FilteredTracks.Count={Count})",
            value, PlaylistId, FilteredTracks.Count);
    }

    // Detail refresh now arrives via the store's push/invalidate stream
    // subscribed in Activate() — no manual DataChanged wire needed.

    partial void OnIsOwnerChanged(bool value)
    {
        // CanRemove now reads CanEditItems, but keep notifying in case bindings depend on IsOwner.
    }

    partial void OnCanEditItemsChanged(bool value)
    {
        FindRecommendedTracksCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
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
        IEnumerable<PlaylistTrackDto> filtered = _allTracks;

        if (!string.IsNullOrEmpty(query))
        {
            filtered = _allTracks.Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.ArtistName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.AlbumName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var sorted = (CurrentSortColumn, IsSortDescending) switch
        {
            (PlaylistSortColumn.Custom, false) => filtered.OrderBy(t => t.OriginalIndex),
            (PlaylistSortColumn.Custom, true) => filtered.OrderByDescending(t => t.OriginalIndex),
            (PlaylistSortColumn.Title, false) => filtered.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase),
            (PlaylistSortColumn.Title, true) => filtered.OrderByDescending(t => t.Title, StringComparer.OrdinalIgnoreCase),
            (PlaylistSortColumn.Artist, false) => filtered.OrderBy(t => t.ArtistName, StringComparer.OrdinalIgnoreCase),
            (PlaylistSortColumn.Artist, true) => filtered.OrderByDescending(t => t.ArtistName, StringComparer.OrdinalIgnoreCase),
            (PlaylistSortColumn.Album, false) => filtered.OrderBy(t => t.AlbumName, StringComparer.OrdinalIgnoreCase),
            (PlaylistSortColumn.Album, true) => filtered.OrderByDescending(t => t.AlbumName, StringComparer.OrdinalIgnoreCase),
            (PlaylistSortColumn.AddedAt, false) => filtered.OrderBy(t => t.AddedAt),
            (PlaylistSortColumn.AddedAt, true) => filtered.OrderByDescending(t => t.AddedAt),
            _ => filtered.OrderBy(t => t.OriginalIndex)
        };

        FilteredTracks.ReplaceWith(sorted.Cast<ITrackItem>());
    }

    private void UpdateAggregates()
    {
        TotalTracks = _allTracks.Count;
        var totalSeconds = _allTracks.Sum(t => t.Duration.TotalSeconds);
        TotalDuration = FormatDuration(totalSeconds);
    }

    private static string FormatDuration(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} hr {ts.Minutes} min";
        return $"{ts.Minutes} min";
    }

    /// <summary>
    /// Prefills the ViewModel with data already known from the source card.
    /// </summary>
    public void PrefillFrom(Data.Parameters.ContentNavigationParameter nav)
    {
        _logger?.LogInformation(
            "PrefillFrom: Uri='{Uri}', Title='{Title}', Subtitle='{Subtitle}', ImageUrl='{ImageUrl}'",
            nav.Uri, nav.Title, nav.Subtitle, nav.ImageUrl);

        // Several call sites pass the literal "Playlist" string as a fallback when
        // the source card has no title. Treat that as no title so the page shows
        // a shimmer rather than the page-type label.
        if (!string.IsNullOrEmpty(nav.Title)
            && !string.Equals(nav.Title, "Playlist", StringComparison.OrdinalIgnoreCase))
        {
            PlaylistName = nav.Title;
        }
        else
        {
            _logger?.LogInformation(
                "PrefillFrom: skipping nav.Title='{Title}' (empty or generic 'Playlist' fallback)",
                nav.Title);
        }
        // Don't surface mosaic URIs here — the Image converter can't render them, so
        // writing one into PlaylistImageUrl would flip the shimmer off and show a
        // blank gray rect until ApplyMosaicHeroAsync composes a real file:// URI.
        // Leaving the field null keeps the shimmer on until the composed mosaic
        // arrives and fades in via ImageFallbackBehavior.
        if (!string.IsNullOrEmpty(nav.ImageUrl)
            && !Helpers.SpotifyImageHelper.IsMosaicUri(nav.ImageUrl))
        {
            PlaylistImageUrl = nav.ImageUrl;
        }
        if (!string.IsNullOrEmpty(nav.Subtitle)) OwnerName = nav.Subtitle;
    }

    /// <summary>
    /// Wire this VM to the given playlist URI and start observing. Disposes any
    /// prior subscription (which cancels its inflight fetch). Call Deactivate()
    /// on navigation-away.
    /// </summary>
    public void Activate(string? playlistId)
    {
        if (string.IsNullOrEmpty(playlistId))
        {
            _logger?.LogWarning("Activate called with empty playlistId");
            return;
        }

        _logger?.LogInformation(
            "Activate: playlistId='{PlaylistId}', current PlaylistName='{PlaylistName}'",
            playlistId, PlaylistName);

        _subscriptions?.Dispose();
        _subscriptions = new CompositeDisposable();

        var isNewPlaylist = _tracksLoadedFor != playlistId;

        PlaylistId = playlistId;
        HasError = false;
        ErrorMessage = null;

        if (isNewPlaylist)
        {
            // Blank display fields so the previous playlist's data doesn't bleed
            // through while the new one loads. With NavigationCacheMode="Enabled"
            // the VM is reused across navigations; without this reset, PrefillFrom's
            // null-guards and ApplyDetail's empty-string guards would leave stale
            // values visible whenever the new playlist is missing a field (e.g.
            // editorial playlists with no description).
            PlaylistName = string.Empty;
            PlaylistDescription = null;
            PlaylistImageUrl = null;
            HeaderImageUrl = null;
            OwnerName = string.Empty;
            IsOwner = false;
            IsPublic = false;
            FollowerCount = 0;
            TotalTracks = 0;
            _playlistFormatAttributes = null;
            TotalDuration = string.Empty;
            HasAnyAddedAt = false;

            // Seed shimmer rows synchronously before any async work so the first
            // frame shows placeholders rather than an empty list.
            FilteredTracks.ReplaceWith(
                Enumerable.Range(0, 10).Select(i =>
                    (ITrackItem)LazyTrackItem.Placeholder($"ph-{i}", i + 1)));
            IsLoadingTracks = true;
        }

        var streamSubscription = _playlistStore.Observe(playlistId)
            .Subscribe(
                state => _dispatcherQueue.TryEnqueue(() => ApplyDetailState(state, playlistId)),
                ex => _logger?.LogError(ex, "PlaylistStore stream faulted for {PlaylistId}", playlistId));
        _subscriptions.Add(streamSubscription);

        // Rootlist (user playlists) for the "Add to playlist" flyout is secondary
        // — load it in the background instead of gating the detail render on it.
        // In a later pass this becomes its own store observation.
        _ = LoadRootlistAsync();
    }

    public void Deactivate()
    {
        _logger?.LogInformation("Deactivate: playlistId='{PlaylistId}'", PlaylistId);
        _subscriptions?.Dispose();
        _subscriptions = null;
        _tracksCts?.Cancel();
        _tracksCts?.Dispose();
        _tracksCts = null;
        // Search timer holds a Tick closure over `this`; stop it on nav-away so it
        // doesn't fire against a cached-but-hidden page.
        _searchDebounceTimer.Stop();
    }

    private void ApplyDetailState(EntityState<PlaylistDetailDto> state, string expectedPlaylistId)
    {
        // Guard against late dispatch after Deactivate/Activate(other) took over.
        if (_disposed || PlaylistId != expectedPlaylistId)
            return;

        switch (state)
        {
            case EntityState<PlaylistDetailDto>.Initial:
                // Nothing to render yet — shimmer already seeded in Activate.
                IsLoading = true;
                break;

            case EntityState<PlaylistDetailDto>.Loading loading:
                // If we have previous data keep showing it; otherwise stay in shimmer.
                IsLoading = loading.Previous is null;
                break;

            case EntityState<PlaylistDetailDto>.Ready ready:
                ApplyDetail(ready.Value);
                IsLoading = false;
                // Always re-fetch on Ready. Initial replay (page re-visit) gets a
                // fresh read; later Ready pushes (from PlaylistCacheService.Changes
                // → PlaylistStore.Invalidate) deliver remote edits. LoadTracksAsync
                // keeps rows visible while it refetches — see its IsLoadingTracks guard.
                _ = LoadTracksAsync(expectedPlaylistId);
                break;

            case EntityState<PlaylistDetailDto>.Error error:
                // Keep any previous rendered state; surface the error banner.
                HasError = true;
                ErrorMessage = ErrorMapper.ToUserMessage(error.Exception);
                IsLoading = false;
                _logger?.LogError(error.Exception, "PlaylistStore reported error for {PlaylistId}", expectedPlaylistId);
                break;
        }
    }

    private void ApplyDetail(PlaylistDetailDto detail)
    {
        _logger?.LogInformation(
            "Detail received: Name='{Name}', OwnerName='{OwnerName}', ImageUrl='{ImageUrl}', IsOwner={IsOwner}, FollowerCount={FollowerCount}",
            detail.Name, detail.OwnerName, detail.ImageUrl, detail.IsOwner, detail.FollowerCount);

        // Guard against the generic 'Playlist' fallback the data layer returns
        // for editorial mixes whose name lookup isn't implemented.
        if (!string.IsNullOrEmpty(detail.Name)
            && !detail.Name.StartsWith("Unknown")
            && !string.Equals(detail.Name, "Playlist", StringComparison.OrdinalIgnoreCase))
        {
            PlaylistName = detail.Name;
        }
        if (!string.IsNullOrEmpty(detail.Description))
            PlaylistDescription = detail.Description;
        // Hero image resolution:
        //  - Direct HTTPS URL → use verbatim.
        //  - spotify:mosaic:... → delegate to PlaylistMosaicService for a real
        //    composed PNG (reuses the sidebar's disk-cache + inflight dedup).
        //  - null / empty → still try the mosaic service with a null hint;
        //    it falls back to picking 4 unique album covers from the
        //    playlist's tracks. Only place the placeholder 3-line icon when
        //    the service returns null (truly empty playlist or fetch failure).
        if (!string.IsNullOrEmpty(detail.ImageUrl) && !Helpers.SpotifyImageHelper.IsMosaicUri(detail.ImageUrl))
        {
            PlaylistImageUrl = detail.ImageUrl;
        }
        else if (_mosaicService is not null && !string.IsNullOrEmpty(PlaylistId))
        {
            // Fire-and-forget: the compose can take a round-trip when the
            // playlist's tracks aren't yet in the warm cache. Guard against
            // races by checking PlaylistId hasn't changed (nav to a different
            // playlist) before assigning the result.
            var playlistId = PlaylistId;
            var hint = detail.ImageUrl;
            _logger?.LogInformation(
                "ApplyDetail: kicking off mosaic hero for '{PlaylistId}' (hint='{Hint}')",
                playlistId, hint ?? "null");
            _ = ApplyMosaicHeroAsync(playlistId, hint);
        }
        else
        {
            _logger?.LogWarning(
                "ApplyDetail: no hero path taken — ImageUrl='{Img}', mosaicService null? {MosaicNull}, PlaylistId='{PlaylistId}'",
                detail.ImageUrl ?? "null", _mosaicService is null, PlaylistId);
        }
        HeaderImageUrl = string.IsNullOrWhiteSpace(detail.HeaderImageUrl) ? null : detail.HeaderImageUrl;
        _playlistFormatAttributes = detail.FormatAttributes;
        if (!string.IsNullOrEmpty(detail.OwnerName) && detail.OwnerName != "Unknown")
            OwnerName = detail.OwnerName;

        // The data layer sometimes hands us the raw `spotify:user:{id}` URI or bare id
        // in OwnerName (editorial / legacy accounts where the display-name lookup hasn't
        // been persisted). Detect that and resolve to a friendly name via the extended-
        // metadata UserProfile extension — cheap for repeat visits because the resolver
        // caches both hits and misses.
        if (_userProfileResolver is not null)
        {
            var ownerUri = !string.IsNullOrWhiteSpace(detail.OwnerId)
                ? detail.OwnerId
                : (OwnerName is not null && LooksLikeUserIdentifier(OwnerName) ? OwnerName : null);
            if (!string.IsNullOrEmpty(ownerUri))
            {
                var pinnedPlaylistId = PlaylistId;
                _logger?.LogInformation(
                    "ApplyDetail: resolving owner display name for '{OwnerUri}' (playlist '{PlaylistId}')",
                    ownerUri, pinnedPlaylistId);
                _ = ResolveOwnerDisplayNameAsync(ownerUri, pinnedPlaylistId);
            }
        }
        else
        {
            _logger?.LogWarning(
                "ApplyDetail: _userProfileResolver is null — cannot resolve owner '{OwnerName}' / '{OwnerId}'",
                detail.OwnerName ?? "null", detail.OwnerId ?? "null");
        }

        IsOwner = detail.IsOwner;
        IsPublic = detail.IsPublic;
        FollowerCount = detail.FollowerCount;

        BasePermission = detail.BasePermission;
        CanEditItems = detail.Capabilities.CanEditItems;
        CanAdministratePermissions = detail.Capabilities.CanAdministratePermissions;
        CanCancelMembership = detail.Capabilities.CanCancelMembership;
        CanAbuseReport = detail.Capabilities.CanAbuseReport;

        HasError = false;
        ErrorMessage = null;
    }

    /// <summary>
    /// Heuristic: does this string look like a bare Spotify user id or a
    /// <c>spotify:user:{id}</c> URI rather than a human display name? Anything with
    /// whitespace or a non-prefix colon is assumed to be a real display name.
    /// </summary>
    private static bool LooksLikeUserIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.StartsWith("spotify:user:", StringComparison.Ordinal)) return true;
        // Bare ids: URL-safe slug, no spaces, no colons.
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == ' ' || c == ':' || c == '/') return false;
        }
        return true;
    }

    private async Task ResolveOwnerDisplayNameAsync(string ownerUri, string pinnedPlaylistId)
    {
        if (_userProfileResolver is null) return;
        try
        {
            // Same reasoning as ApplyMosaicHeroAsync — _tracksCts gets cancelled by
            // LoadTracksAsync immediately after we're spawned. Staleness is gated by
            // the PlaylistId check in the dispatcher enqueue below; the resolver
            // memoises results so the network call isn't wasted on quick re-navigations.
            var displayName = await _userProfileResolver
                .GetDisplayNameAsync(ownerUri, CancellationToken.None)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                _logger?.LogWarning(
                    "ResolveOwnerDisplayNameAsync: resolver returned empty display name for '{OwnerUri}'",
                    ownerUri);
                return;
            }

            _logger?.LogInformation(
                "ResolveOwnerDisplayNameAsync: '{OwnerUri}' -> '{DisplayName}'",
                ownerUri, displayName);
            _dispatcherQueue.TryEnqueue(() =>
            {
                // Drop the result if navigation moved on; also drop if a fresher
                // value has already been written (e.g. the user typed a name while
                // the resolver was in flight — unlikely, but cheap to guard).
                if (_disposed || !string.Equals(PlaylistId, pinnedPlaylistId, StringComparison.Ordinal))
                    return;
                if (string.Equals(OwnerName, displayName, StringComparison.Ordinal))
                    return;
                OwnerName = displayName;
            });
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("ResolveOwnerDisplayNameAsync cancelled for {OwnerUri}", ownerUri);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ResolveOwnerDisplayNameAsync failed for {OwnerUri}", ownerUri);
        }
    }

    private async Task ApplyMosaicHeroAsync(string playlistId, string? mosaicHint)
    {
        if (_mosaicService is null) return;

        try
        {
            // Don't tie this to _tracksCts: ApplyDetailState fires us synchronously and
            // then immediately calls LoadTracksAsync which Cancels+recreates _tracksCts —
            // that would always cancel us before the build returns. Staleness is already
            // handled by the PlaylistId check inside the dispatcher enqueue below.
            var path = await _mosaicService.GetMosaicFilePathAsync(playlistId, mosaicHint, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrEmpty(path))
            {
                _logger?.LogWarning(
                    "ApplyMosaicHeroAsync: GetMosaicFilePathAsync returned null path for '{PlaylistId}' (hint='{Hint}')",
                    playlistId, mosaicHint ?? "null");
                return;
            }

            var fileUri = new Uri(path).AbsoluteUri;
            _logger?.LogInformation(
                "ApplyMosaicHeroAsync: got mosaic file '{FileUri}' for '{PlaylistId}'",
                fileUri, playlistId);
            _dispatcherQueue.TryEnqueue(() =>
            {
                // Another navigation could have swapped PlaylistId mid-flight.
                if (_disposed || !string.Equals(PlaylistId, playlistId, StringComparison.Ordinal))
                    return;
                PlaylistImageUrl = fileUri;
            });
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("ApplyMosaicHeroAsync cancelled for {PlaylistId}", playlistId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ApplyMosaicHeroAsync failed for {PlaylistId}", playlistId);
        }
    }

    private async Task LoadTracksAsync(string playlistId)
    {
        // Warm hit (revisiting a playlist whose tracks are already loaded): keep
        // the existing CTS alive and don't show the shimmer. The fetch still runs
        // (so remote edits land), but the apply path below diff-checks the result
        // against _allTracks and only ReplaceWith's the collection on a real change
        // — no row-churn, no shimmer flash, no scroll-position loss on re-nav.
        var isWarmHit = _tracksLoadedFor == playlistId && _allTracks.Count > 0;

        if (!isWarmHit)
        {
            _tracksCts?.Cancel();
            _tracksCts?.Dispose();
            _tracksCts = new CancellationTokenSource();
        }
        _tracksCts ??= new CancellationTokenSource();
        var ct = _tracksCts.Token;

        try
        {
            // Silent refresh when we already have rows for this playlist on screen —
            // the continuation below will ReplaceWith the fresh list atomically, so
            // flashing shimmer in the meantime is jarring. First-time loads and
            // cross-playlist swaps still show the shimmer (Activate seeded it).
            if (_tracksLoadedFor != playlistId)
                IsLoadingTracks = true;
            var tracks = await _libraryDataService.GetPlaylistTracksAsync(playlistId, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || PlaylistId != playlistId)
                    return;

                // Cheap diff: same count + same ordered ids ⇒ refresh delivered no
                // change, so we can leave the existing collection (and rendered
                // ListView containers) in place. Saves a full ReplaceWith + the
                // associated row-rematerialization on every warm-hit revisit.
                if (isWarmHit && TracksAreEquivalent(_allTracks, tracks))
                {
                    _tracksLoadedFor = playlistId;
                    IsLoadingTracks = false;
                    return;
                }

                _allTracks = tracks.Select((t, i) => t with { OriginalIndex = i + 1 }).ToList();
                HasAnyAddedAt = _allTracks.Any(t => t.AddedAt.HasValue);
                UpdateAggregates();
                ApplyFilterAndSort();
                _tracksLoadedFor = playlistId;
                IsLoadingTracks = false;
                _logger?.LogInformation(
                    "Tracks applied: {Count} tracks for '{PlaylistId}'", _allTracks.Count, playlistId);
            });
        }
        catch (OperationCanceledException)
        {
            // Deactivate / re-activate raced us — silent.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LoadTracksAsync failed for {PlaylistId}", playlistId);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || PlaylistId != playlistId)
                    return;
                IsLoadingTracks = false;
                HasError = true;
                ErrorMessage = ErrorMapper.ToUserMessage(ex);
            });
        }
    }

    // Cheap "did the playlist actually change" check used by LoadTracksAsync's
    // warm-hit path. Compares count + per-index Id; doesn't care about other
    // fields (artist names, image URLs) since those rarely change for an
    // already-known track and would force a full ReplaceWith for nothing.
    private static bool TracksAreEquivalent(
        IReadOnlyList<PlaylistTrackDto> current,
        IReadOnlyList<PlaylistTrackDto> fetched)
    {
        if (current.Count != fetched.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].Id, fetched[i].Id, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private async Task LoadRootlistAsync()
    {
        try
        {
            var list = await _libraryDataService.GetUserPlaylistsAsync().ConfigureAwait(false);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed) return;
                Playlists = list;
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadRootlistAsync failed");
        }
    }

    [RelayCommand]
    private void Retry()
    {
        HasError = false;
        ErrorMessage = null;
        _playlistStore.Invalidate(PlaylistId);
        _tracksLoadedFor = null;
    }

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
        var index = FilteredTracks.ToList().FindIndex(t => t.Id == trackItem.Id);
        BuildQueueAndPlay(index >= 0 ? index : 0, shuffle: false);
    }

    private void BuildQueueAndPlay(int startIndex, bool shuffle)
    {
        if (FilteredTracks.Count == 0) return;

        var queueItems = FilteredTracks.Select(t => new QueueItem
        {
            TrackId = t.Id,
            Title = t.Title,
            ArtistName = t.ArtistName,
            AlbumArt = t.ImageUrl ?? PlaylistImageUrl,
            DurationMs = t.Duration.TotalMilliseconds,
            IsUserQueued = false,
            // Uid + Metadata round-trip from the playlist API (PlaylistTrackDto
            // was populated from CachedPlaylistItem.ItemId and FormatAttributes).
            // Published as ProvidedTrack.uid and ProvidedTrack.metadata so remote
            // clients see the same per-track decorations Spotify desktop emits.
            Uid = t.Uid,
            Metadata = t.FormatAttributes
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
            Name = PlaylistName,
            ImageUrl = PlaylistImageUrl,
            // Playlist-level format attributes from the API — forwarded verbatim
            // into PlayerState.context_metadata (format, request_id, tag,
            // source-loader, image_url, session_control_display.displayName.*, …).
            FormatAttributes = _playlistFormatAttributes
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

    /// <summary>
    /// Empty-state CTA — surfaces a recommendations flow when the user has edit
    /// capability on this playlist. Currently a stub: the recommendations endpoint
    /// isn't wired up yet, so we just log. Replace with the real flow (open
    /// recommended-tracks dialog or navigate to in-playlist search) when ready.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditItems))]
    private void FindRecommendedTracks()
    {
        _logger?.LogInformation(
            "FindRecommendedTracks invoked for playlist '{PlaylistId}' (CanEditItems={CanEditItems}) -- TODO wire recommendations service",
            PlaylistId, CanEditItems);
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveSelectedAsync()
    {
        if (!CanRemove) return;

        var trackIds = SelectedItems.OfType<PlaylistTrackDto>().Select(t => t.Id).ToList();
        if (trackIds.Count == 0) return;

        await _libraryDataService.RemoveTracksFromPlaylistAsync(PlaylistId, trackIds);

        var idsToRemove = trackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _allTracks.RemoveAll(t => idsToRemove.Contains(t.Id));
        UpdateAggregates();
        ApplyFilterAndSort();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddToPlaylist(PlaylistSummaryDto? playlist)
    {
        if (playlist == null || !HasSelection) return;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _subscriptions?.Dispose();
        _subscriptions = null;
        _tracksCts?.Cancel();
        _tracksCts?.Dispose();
        _tracksCts = null;
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
