using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Data;
using Wavee.Core.Playlists;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.Models;
using Wavee.UI.Services.Playlists;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Stores;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels.Home;
using Wavee.UI.WinUI.ViewModels.Playlist;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Thin composer that owns three child VMs (<see cref="Header"/>,
/// <see cref="TrackList"/>, <see cref="Mutations"/>), wires their cross-
/// dependencies, and orchestrates the load lifecycle (activate / hibernate
/// / dispose, store subscription, detail apply, tracks fetch, mosaic hero
/// fallback, owner display-name resolution, addedBy resolution).
///
/// <para>The decomposition replaces the previous ~3300-line "god ViewModel"
/// that owned every playlist concern. Each child has a single responsibility
/// (envelope state / track table / mutation commands); they communicate via
/// the parent — no direct child-to-child references.</para>
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class PlaylistViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly PlaylistStore _playlistStore;
    private readonly Services.PlaylistMosaicService? _mosaicService;
    private readonly Services.IUserProfileResolver? _userProfileResolver;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;

    private CompositeDisposable? _subscriptions;
    private CancellationTokenSource? _tracksCts;
    private string? _tracksLoadedFor;
    private string? _tracksLoadInFlightFor;
    private string? _appliedDetailSignature;
    private string? _ownerResolutionKey;
    private string? _pendingFallbackMosaicPlaylistId;
    private int _recommendationsAutoLoadGeneration;
    private bool _disposed;

    [ObservableProperty]
    public partial string PlaylistId { get; set; } = "";

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Envelope state (name, owner, image, capabilities, palette,
    /// collaborators, follower count, layout mode, invite link, follow toggle).
    /// Constructor-initialized; never replaced.</summary>
    public PlaylistHeaderViewModel Header { get; }

    /// <summary>Track table state (snapshot, filter/sort, video filter,
    /// session-control chips, empty-state genres, selection-aware playback
    /// commands, reorder). Constructor-initialized.</summary>
    public PlaylistTrackListViewModel TrackList { get; }

    /// <summary>Launch "Refresh with swipes" for this playlist (editable, non-empty playlists only).</summary>
    [RelayCommand]
    private void OpenRefreshWithSwipes()
    {
        if (!Header.CanEditItems || TrackList.TotalTracks <= 0) return;
        Wavee.UI.WinUI.Helpers.Navigation.NavigationHelpers.OpenRefreshPlaylist(
            new Wavee.UI.WinUI.Data.Parameters.RefreshPlaylistParameter(PlaylistId, Header.PlaylistName ?? "Playlist"));
    }

    /// <summary>Mutation commands (rename, description, cover change/remove,
    /// delete, collab toggle, recommendations). Constructor-initialized.</summary>
    public PlaylistMutationCoordinator Mutations { get; }

    public PlaylistViewModel(
        ILibraryDataService libraryDataService,
        IPlaylistPermissionService playlistPermissionService,
        IPlaylistMutationService playlistMutationService,
        IPlaybackStateService playbackStateService,
        PlaylistStore playlistStore,
        PlaylistTrackFilterSorter playlistTrackFilterSorter,
        ILogger<PlaylistViewModel>? logger = null,
        Services.PlaylistMosaicService? mosaicService = null,
        Services.IUserProfileResolver? userProfileResolver = null,
        IAuthState? authState = null,
        IPlaylistCacheService? playlistCache = null,
        Services.IMusicVideoMetadataService? musicVideoMetadata = null,
        IHomeFeedService? homeFeedService = null)
    {
        _libraryDataService = libraryDataService;
        _playlistStore = playlistStore;
        _mosaicService = mosaicService;
        _userProfileResolver = userProfileResolver;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        Header = new PlaylistHeaderViewModel(
            libraryDataService,
            playlistMutationService,
            playlistPermissionService,
            userProfileResolver,
            authState,
            logger,
            tracksSnapshotProvider: () => TrackList.AllTracks,
            playlistIdProvider: () => PlaylistId);

        TrackList = new PlaylistTrackListViewModel(
            playlistMutationService,
            playbackStateService,
            playlistTrackFilterSorter,
            playlistCache,
            musicVideoMetadata,
            homeFeedService,
            logger,
            playlistIdProvider: () => PlaylistId,
            playlistNameProvider: () => Header.PlaylistName,
            playlistImageUrlProvider: () => Header.PlaylistImageUrl,
            playlistFormatAttributesProvider: () => Header.PlaylistFormatAttributes,
            playlistRevisionProvider: () => Header.CurrentRevision,
            canEditItemsProvider: () => Header.CanEditItems);

        Mutations = new PlaylistMutationCoordinator(
            playlistMutationService,
            playlistPermissionService,
            logger,
            playlistIdProvider: () => PlaylistId,
            playlistNameProvider: () => Header.PlaylistName,
            playlistNameSetter: v => Header.PlaylistName = v ?? string.Empty,
            playlistDescriptionProvider: () => Header.PlaylistDescription,
            playlistDescriptionSetter: v => Header.PlaylistDescription = v,
            isCollaborativeProvider: () => Header.IsCollaborative,
            isCollaborativeSetter: v => Header.IsCollaborative = v,
            canEditNameProvider: () => Header.CanEditName,
            canEditDescriptionProvider: () => Header.CanEditDescription,
            canEditPictureProvider: () => Header.CanEditPicture,
            canEditCollaborativeProvider: () => Header.CanEditCollaborative,
            canEditItemsProvider: () => Header.CanEditItems,
            canDeleteProvider: () => Header.CanDelete,
            totalTracksProvider: () => TrackList.TotalTracks,
            tracksSnapshotProvider: () => TrackList.AllTracks);

        // ── Cross-child wiring ──────────────────────────────────────────────
        // Header envelope changes → mutations notify CanExecute + tracklist
        // notifies CanRemove gate. The header reaches the parent first; we fan
        // out from there so children don't depend on each other directly.
        Header.PlaylistEnvelopeChanged += (_, _) =>
        {
            Mutations.NotifyPlaylistCapabilityCommandsChanged();
            TrackList.NotifyCapabilityGatesChanged();
            ScheduleRecommendationsAutoLoad();
        };

        // TrackList snapshot changes → header re-derives the AddedBy column
        // gate + the collaborator stack (both read from the snapshot accessor).
        TrackList.TracksChanged += (_, _) =>
        {
            Header.RefreshTrackDerivedState();
        };

        // TrackList totals → forward into header so MetaInlineLine /
        // CanShowRecommendations stay in sync, and re-evaluate the
        // recommendations auto-trigger.
        TrackList.AggregatesChanged += (_, _) =>
        {
            Header.SetTotalTracks(TrackList.TotalTracks);
            Header.SetTotalDuration(TrackList.TotalDuration);
            ScheduleRecommendationsAutoLoad();
        };

        Diagnostics.LiveInstanceTracker.Register(this);
    }

    private void ScheduleRecommendationsAutoLoad()
    {
        var playlistId = PlaylistId;
        if (string.IsNullOrEmpty(playlistId))
            return;

        var generation = ++_recommendationsAutoLoadGeneration;
        if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_disposed ||
                    generation != _recommendationsAutoLoadGeneration ||
                    !string.Equals(PlaylistId, playlistId, StringComparison.Ordinal))
                {
                    return;
                }

                Mutations.MaybeAutoLoadRecommendations();
            }))
        {
            Mutations.MaybeAutoLoadRecommendations();
        }
    }

    private void ScheduleVideoAvailabilityFetch(string playlistId, CancellationToken cancellationToken)
    {
        if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_disposed ||
                    !string.Equals(PlaylistId, playlistId, StringComparison.Ordinal) ||
                    cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                TrackList.TryTriggerVideoAvailabilityFetch(playlistId, cancellationToken);
            }))
        {
            TrackList.TryTriggerVideoAvailabilityFetch(playlistId, cancellationToken);
        }
    }

    /// <summary>
    /// Pick the initial layout mode for a navigation. Tries the cache first
    /// (definitive when present), otherwise keeps the safe cover fallback until
    /// playlist detail supplies an actual header image URL.
    /// </summary>
    private PlaylistLayoutMode ResolveInitialLayoutMode(string playlistId)
    {
        var cached = _playlistStore.PeekCached(playlistId);
        if (cached is not null)
        {
            return string.IsNullOrEmpty(cached.HeaderImageUrl)
                ? PlaylistLayoutMode.Cover
                : PlaylistLayoutMode.Banner;
        }

        return PlaylistLayoutMode.Cover;
    }

    // ── IsLoading fan-out ───────────────────────────────────────────────────

    partial void OnIsLoadingChanged(bool value)
    {
        _logger?.LogDebug("IsLoading -> {Value} (PlaylistId='{PlaylistId}')", value, PlaylistId);
        TrackList.SetParentIsLoading(value);
    }

    partial void OnHasErrorChanged(bool value)
        => TrackList.SetParentHasError(value);

    // ── Prefill / Activate / Deactivate / Hibernate / Dispose ───────────────

    /// <summary>
    /// Prefills the ViewModel with data already known from the source card.
    /// </summary>
    public void PrefillFrom(Data.Parameters.ContentNavigationParameter nav, bool clearMissing = false)
    {
        _logger?.LogInformation(
            "PrefillFrom: Uri='{Uri}', Title='{Title}', Subtitle='{Subtitle}', ImageUrl='{ImageUrl}'",
            nav.Uri, nav.Title, nav.Subtitle, nav.ImageUrl);

        if (!HasUsablePrefillTitle(nav.Title))
            _logger?.LogInformation(
                "PrefillFrom: skipping nav.Title='{Title}' (empty or generic 'Playlist' fallback)",
                nav.Title);

        Header.SetPlaylist(BuildPrefillEnvelope(
            nav.Uri,
            nav,
            clearMissing ? null : Header.Playlist,
            clearMissing));

        ApplyPrefillTrackCount(nav, clearMissing);
    }

    /// <summary>
    /// Wire this VM to the given playlist URI and start observing. Disposes any
    /// prior subscription (which cancels its inflight fetch). Call Deactivate()
    /// on navigation-away.
    /// </summary>
    public void Activate(
        string? playlistId,
        bool preserveHeaderPrefill = false,
        Data.Parameters.ContentNavigationParameter? prefill = null)
    {
        if (string.IsNullOrEmpty(playlistId))
        {
            _logger?.LogWarning("Activate called with empty playlistId");
            return;
        }

        _logger?.LogInformation(
            "Activate: playlistId='{PlaylistId}', current PlaylistName='{PlaylistName}'",
            playlistId, Header.PlaylistName);

        _subscriptions?.Dispose();
        _subscriptions = new CompositeDisposable();

        var isNewPlaylist = _tracksLoadedFor != playlistId;

        PlaylistId = playlistId;
        HasError = false;
        ErrorMessage = null;

        if (isNewPlaylist)
        {
            var previousEnvelope = Header.Playlist;
            Header.SetPlaylist(BuildActivationEnvelope(playlistId, previousEnvelope, preserveHeaderPrefill, prefill));

            Header.ResetForNewPlaylist();

            // Reset the track snapshot so RebuildCollaboratorsFromContext
            // doesn't compute the AddedBy gate against the previous playlist's
            // tracks (which produced wrong stale-true gate values during the
            // brief window between Activate and LoadTracksAsync — those would
            // latch into already-materializing ListView containers).
            TrackList.ResetForNewPlaylist();
            if (prefill is not null)
                ApplyPrefillTrackCount(prefill, clearMissing: true);
            _tracksLoadedFor = null;
            _tracksLoadInFlightFor = null;
            _recommendationsAutoLoadGeneration++;
            _appliedDetailSignature = null;
            _ownerResolutionKey = null;

            Mutations.ResetForNewPlaylist();

            _pendingFallbackMosaicPlaylistId = null;

            TrackList.IsLoadingTracks = true;

            // Force a false → true transition on IsLoading so the next Ready
            // emission (which sets false again) actually fires PropertyChanged →
            // ContentPageController.OnIsLoadingChanged → ScheduleCrossfade. Without
            // this, warm-cache navigations replay Ready directly: IsLoading stays
            // at false throughout, no transition fires, no crossfade runs, and the
            // previous playlist's hero stays painted at full opacity over the new
            // one's already-correct VM state.
            IsLoading = true;
        }

        // Seed layout mode before the subscription fires its first emission.
        // Only cached playlist detail is trusted here; URI prefixes are not
        // reliable enough to choose the header-image layout. ApplyDetail sets
        // the authoritative value once real detail arrives.
        Header.LayoutMode = ResolveInitialLayoutMode(playlistId);

        var streamSubscription = _playlistStore.Observe(playlistId)
            .Subscribe(
                state => _dispatcherQueue.TryEnqueue(() => ApplyDetailState(state, playlistId)),
                ex => _logger?.LogError(ex, "PlaylistStore stream faulted for {PlaylistId}", playlistId));
        _subscriptions.Add(streamSubscription);
    }

    private void ApplyPrefillTrackCount(Data.Parameters.ContentNavigationParameter nav, bool clearMissing)
    {
        // Seed TotalTracks so TrackDataGrid.LoadingRowCount renders the right
        // number of skeleton rows before the playlist contents resolve. Source
        // cards (sidebar rows, library lists, search hits) carry the count in
        // nav.TotalTracks; deep links and other sourceless paths fall back to
        // the grid's DefaultLoadingRowCount.
        if (nav.TotalTracks is { } prefillTracks && prefillTracks > 0)
            TrackList.TotalTracks = prefillTracks;
        else if (clearMissing)
            TrackList.TotalTracks = 0;
    }

    private PlaylistView? BuildActivationEnvelope(
        string playlistId,
        PlaylistView? previousEnvelope,
        bool preserveHeaderPrefill,
        Data.Parameters.ContentNavigationParameter? prefill)
    {
        if (prefill is not null)
            return BuildPrefillEnvelope(playlistId, prefill, existing: null, clearMissing: true);

        if (preserveHeaderPrefill && previousEnvelope is not null)
            return ResetTransientEnvelope(previousEnvelope, playlistId);

        return null;
    }

    private static PlaylistView BuildPrefillEnvelope(
        string playlistId,
        Data.Parameters.ContentNavigationParameter nav,
        PlaylistView? existing,
        bool clearMissing)
    {
        var envelope = existing ?? EmptyEnvelopeFor(playlistId);

        var name = HasUsablePrefillTitle(nav.Title)
            ? nav.Title!
            : clearMissing ? string.Empty : envelope.Name;

        // Don't surface mosaic URIs here — the Image converter can't render them,
        // so writing one into PlaylistImageUrl would flip the shimmer off and show
        // a blank gray rect until ApplyMosaicHeroAsync composes a real file:// URI.
        var imageUrl = !string.IsNullOrEmpty(nav.ImageUrl) && !SpotifyImageHelper.IsMosaicUri(nav.ImageUrl)
            ? nav.ImageUrl
            : clearMissing ? null : envelope.ImageUrl;

        var ownerName = !string.IsNullOrEmpty(nav.Subtitle)
            ? nav.Subtitle
            : clearMissing ? string.Empty : envelope.OwnerName;

        return ResetTransientEnvelope(envelope, playlistId) with
        {
            Name = name,
            ImageUrl = imageUrl,
            OwnerName = ownerName
        };
    }

    private static bool HasUsablePrefillTitle(string? title)
        => !string.IsNullOrEmpty(title)
           && !string.Equals(title, "Playlist", StringComparison.OrdinalIgnoreCase);

    private static PlaylistView ResetTransientEnvelope(PlaylistView source, string playlistId)
        => source with
        {
            Id = playlistId,
            Description = null,
            HeaderImageUrl = null,
            OwnerId = null,
            OwnerAvatarUrl = null,
            FormatAttributes = null,
            Revision = null,
            SessionControlGroupId = null,
            IsOwner = false,
            IsPublic = false,
            IsCollaborative = false,
            BasePermission = PlaylistBasePermission.Viewer,
            CanEditItems = false,
            CanAdministratePermissions = false,
            CanCancelMembership = false,
            CanAbuseReport = false,
            CanEditMetadata = false,
            CanEditName = false,
            CanEditDescription = false,
            CanEditPicture = false,
            CanEditCollaborative = false,
            CanDelete = false,
            Palette = null
        };

    private static PlaylistView EmptyEnvelopeFor(string playlistId)
        => new(
            Id: playlistId,
            Name: string.Empty,
            Description: null,
            ImageUrl: null,
            HeaderImageUrl: null,
            OwnerName: string.Empty,
            OwnerId: null,
            OwnerAvatarUrl: null,
            FormatAttributes: null,
            Revision: null,
            SessionControlGroupId: null,
            IsOwner: false,
            IsPublic: false,
            IsCollaborative: false,
            BasePermission: PlaylistBasePermission.Viewer,
            CanEditItems: false,
            CanAdministratePermissions: false,
            CanCancelMembership: false,
            CanAbuseReport: false,
            CanEditMetadata: false,
            CanEditName: false,
            CanEditDescription: false,
            CanEditPicture: false,
            CanEditCollaborative: false,
            CanDelete: false,
            Palette: null);

    public void Deactivate()
    {
        _logger?.LogInformation("Deactivate: playlistId='{PlaylistId}'", PlaylistId);
        _subscriptions?.Dispose();
        _subscriptions = null;
        _tracksCts?.Cancel();
        _tracksCts?.Dispose();
        _tracksCts = null;
        _recommendationsAutoLoadGeneration++;
        Header.Deactivate();
        TrackList.Deactivate();
    }

    /// <summary>
    /// Heavy-state release for cached pages going off-screen. Drops the track
    /// grid and collaborator state — these are the bound collections that pin
    /// the most realized item containers (and therefore composition memory)
    /// while the page sits invisible in the PageHost cache.
    ///
    /// Lightweight identity (PlaylistId, name, image URL, palette brushes) is
    /// preserved so the hero still renders correctly between re-Activate and
    /// the BehaviorSubject re-emitting. Setting <c>_tracksLoadedFor = null</c>
    /// forces Activate's <c>isNewPlaylist</c> branch on revisit so the grid
    /// loading skeleton shows before the warm store value lands.
    /// </summary>
    public void Hibernate()
    {
        _logger?.LogInformation("Hibernate: playlistId='{PlaylistId}'", PlaylistId);
        Deactivate();
        _tracksLoadedFor = null;
        _pendingFallbackMosaicPlaylistId = null;

        TrackList.Hibernate();
        Header.RefreshTrackDerivedState();
        _recommendationsAutoLoadGeneration++;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Deactivate();
        Header.Dispose();
        TrackList.Dispose();
        Mutations.Dispose();
    }

    // ── Detail apply (store observer) + tracks fetch orchestration ──────────

    private void ApplyDetailState(EntityState<PlaylistDetailDto> state, string expectedPlaylistId)
    {
        // Guard against late dispatch after Deactivate/Activate(other) took over.
        if (_disposed || PlaylistId != expectedPlaylistId)
            return;

        switch (state)
        {
            case EntityState<PlaylistDetailDto>.Initial:
                // Nothing to render yet — TrackDataGrid's loading skeleton is driven by IsLoadingTracks.
                IsLoading = true;
                break;

            case EntityState<PlaylistDetailDto>.Loading loading:
                // If we have previous data keep showing it; otherwise stay in shimmer.
                IsLoading = loading.Previous is null;
                break;

            case EntityState<PlaylistDetailDto>.Ready ready:
                var detailSignature = BuildDetailSignature(ready.Value);
                var duplicateDetail = string.Equals(
                    _appliedDetailSignature,
                    detailSignature,
                    StringComparison.Ordinal);
                if (!duplicateDetail)
                {
                    _appliedDetailSignature = detailSignature;
                    ApplyDetail(ready.Value);
                }
                IsLoading = false;
                // Always re-fetch on Ready. Initial replay (page re-visit) gets a
                // fresh read; later Ready pushes (from PlaylistCacheService.Changes
                // → PlaylistStore.Invalidate) deliver remote edits. LoadTracksAsync
                // keeps rows visible while it refetches — see its IsLoadingTracks guard.
                if (!duplicateDetail || _tracksLoadedFor != expectedPlaylistId)
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
            "Detail received: Name='{Name}', OwnerName='{OwnerName}', ImageUrl='{ImageUrl}', HeaderImageUrl='{HeaderImageUrl}', IsOwner={IsOwner}, FollowerCount={FollowerCount}",
            detail.Name, detail.OwnerName, detail.ImageUrl, detail.HeaderImageUrl, detail.IsOwner, detail.FollowerCount);

        var current = Header.Playlist ?? Header.EmptyPlaylistEnvelope();
        var nextName = current.Name;
        var nextDescription = current.Description;
        var nextImageUrl = current.ImageUrl;
        var nextOwnerName = current.OwnerName;

        // Guard against the generic 'Playlist' fallback the data layer returns
        // for editorial mixes whose name lookup isn't implemented.
        if (!string.IsNullOrEmpty(detail.Name)
            && !detail.Name.StartsWith("Unknown", StringComparison.Ordinal)
            && !string.Equals(detail.Name, "Playlist", StringComparison.OrdinalIgnoreCase))
        {
            nextName = detail.Name;
        }

        if (!string.IsNullOrEmpty(detail.Description))
            nextDescription = detail.Description;

        // Hero image resolution:
        //  - Direct HTTPS URL -> use verbatim.
        //  - spotify:mosaic:... -> delegate to PlaylistMosaicService for a real
        //    composed PNG (reuses the sidebar's disk-cache + inflight dedup).
        //  - null / empty -> still try the mosaic service with a null hint;
        //    it falls back to picking 4 unique album covers from the
        //    playlist's tracks. Only place the placeholder 3-line icon when
        //    the service returns null (truly empty playlist or fetch failure).
        if (!string.IsNullOrEmpty(detail.ImageUrl) && !SpotifyImageHelper.IsMosaicUri(detail.ImageUrl))
        {
            nextImageUrl = detail.ImageUrl;
        }
        else if (_mosaicService is not null && !string.IsNullOrEmpty(PlaylistId))
        {
            var playlistId = PlaylistId;
            var hint = detail.ImageUrl;
            if (!string.IsNullOrEmpty(hint))
            {
                // spotify:mosaic hints can be parsed directly, so this path does
                // not need to wait for the track list.
                _logger?.LogInformation(
                    "ApplyDetail: kicking off mosaic hero for '{PlaylistId}' (hint='{Hint}')",
                    playlistId, hint);
                _ = ApplyMosaicHeroAsync(playlistId, hint);
            }
            else
            {
                // No hint means mosaic composition needs the playlist tracks.
                // LoadTracksAsync is already fetching them, so let that path hand
                // its snapshot to the mosaic service instead of starting a second
                // GetPlaylistTracksAsync here.
                _pendingFallbackMosaicPlaylistId = playlistId;
            }
        }
        else
        {
            _logger?.LogWarning(
                "ApplyDetail: no hero path taken - ImageUrl='{Img}', mosaicService null? {MosaicNull}, PlaylistId='{PlaylistId}'",
                detail.ImageUrl ?? "null", _mosaicService is null, PlaylistId);
        }

        if (!string.IsNullOrEmpty(detail.OwnerName) && detail.OwnerName != "Unknown")
            nextOwnerName = detail.OwnerName;

        var nextOwnerId = string.IsNullOrWhiteSpace(detail.OwnerId)
            ? detail.OwnerName
            : detail.OwnerId;

        Header.SetPlaylist(current with
        {
            Id = detail.Id,
            Name = nextName,
            Description = nextDescription,
            ImageUrl = nextImageUrl,
            HeaderImageUrl = string.IsNullOrWhiteSpace(detail.HeaderImageUrl) ? null : detail.HeaderImageUrl,
            OwnerName = nextOwnerName,
            OwnerId = nextOwnerId,
            FormatAttributes = detail.FormatAttributes,
            Revision = detail.Revision,
            SessionControlGroupId = detail.SessionControlGroupId,
            IsOwner = detail.IsOwner,
            IsPublic = detail.IsPublic,
            IsCollaborative = detail.IsCollaborative,
            BasePermission = detail.BasePermission,
            CanEditItems = detail.Capabilities.CanEditItems,
            CanAdministratePermissions = detail.Capabilities.CanAdministratePermissions,
            CanCancelMembership = detail.Capabilities.CanCancelMembership,
            CanAbuseReport = detail.Capabilities.CanAbuseReport,
            CanEditMetadata = detail.Capabilities.CanEditMetadata,
            CanEditName = detail.Capabilities.CanEditName,
            CanEditDescription = detail.Capabilities.CanEditDescription,
            CanEditPicture = detail.Capabilities.CanEditPicture,
            CanEditCollaborative = detail.Capabilities.CanEditCollaborative,
            CanDelete = detail.Capabilities.CanDelete
        });

        // Authoritative layout decision now that the actual HeaderImageUrl is
        // known. Common case: matches the cached/default mode from Activate,
        // no visible change. Header-image playlists discovered on cold load
        // flip here and the page-level PropertyChanged hook on LayoutMode
        // fades in the newly-visible container.
        Header.LayoutMode = string.IsNullOrEmpty(Header.HeaderImageUrl)
            ? PlaylistLayoutMode.Cover
            : PlaylistLayoutMode.Banner;

        TrackList.BuildSessionControlChips(detail.SessionControlOptions);

        // The data layer sometimes hands us the raw `spotify:user:{id}` URI or bare id
        // in OwnerName (editorial / legacy accounts where the display-name lookup hasn't
        // been persisted). Detect that and resolve to a friendly name via the extended-
        // metadata UserProfile extension - cheap for repeat visits because the resolver
        // caches both hits and misses.
        if (_userProfileResolver is not null)
        {
            var ownerUri = ResolveOwnerProfileLookupKey(detail, Header.OwnerName);
            if (!string.IsNullOrEmpty(ownerUri))
            {
                var pinnedPlaylistId = PlaylistId;
                var ownerResolutionKey = string.Concat(pinnedPlaylistId, "|", ownerUri);
                if (!string.Equals(_ownerResolutionKey, ownerResolutionKey, StringComparison.Ordinal))
                {
                    _ownerResolutionKey = ownerResolutionKey;
                    _logger?.LogInformation(
                        "ApplyDetail: resolving owner display name for '{OwnerUri}' (playlist '{PlaylistId}')",
                        ownerUri, pinnedPlaylistId);
                    _ = ResolveOwnerDisplayNameAsync(ownerUri, pinnedPlaylistId);
                }
            }
        }
        else
        {
            _logger?.LogWarning(
                "ApplyDetail: _userProfileResolver is null - cannot resolve owner '{OwnerName}' / '{OwnerId}'",
                detail.OwnerName ?? "null", detail.OwnerId ?? "null");
        }

        Header.FollowerCount = detail.FollowerCount;

        // Popcount runs out-of-band - the data layer holds FollowerCount at 0
        // so the detail load doesn't block on a stat-only round trip. Kick off
        // the dedicated fetch here and let the chip shimmer until it resolves.
        // Same idea for the palette: Pathfinder fetchPlaylist is fired in
        // parallel and the hero tints in once the colour set arrives.
        if (!string.IsNullOrEmpty(PlaylistId))
        {
            _ = Header.LoadFollowerCountAsync(PlaylistId);
            _ = Header.LoadPaletteAsync(PlaylistId);
            // Seed the heart's saved-state from library membership — without this the
            // heart defaults to unsaved even for a playlist already in the sidebar/rootlist.
            _ = Header.RefreshFollowedStateAsync(PlaylistId);
        }

        _logger?.LogDebug(
            "[caps] VM ApplyDetail '{Id}': IsOwner={IsOwner} BasePerm={Base} | dto.Caps=[EditItems={EI},EditMeta={EM},Delete={DD},Admin={AD}] | VM gates=[CanEditName={CEN},CanEditDescription={CED},CanEditPicture={CEP},CanEditCollab={CEC},CanDelete={CD}]",
            PlaylistId, Header.IsOwner, Header.BasePermission,
            detail.Capabilities.CanEditItems, detail.Capabilities.CanEditMetadata,
            detail.Capabilities.CanDelete, detail.Capabilities.CanAdministratePermissions,
            Header.CanEditName, Header.CanEditDescription, Header.CanEditPicture, Header.CanEditCollaborative, Header.CanDelete);

        _ = ApplySecondaryHeaderStateAsync(PlaylistId);

        HasError = false;
        ErrorMessage = null;
    }

    private async Task ApplySecondaryHeaderStateAsync(string playlistId)
    {
        // Collaborator chips are below the hot path for navigation. Deferring
        // avoids several synchronous collection rebuilds during ApplyDetail.
        await Task.Delay(32);
        if (_disposed || PlaylistId != playlistId)
            return;

        Header.RefreshTrackDerivedState();
        _ = Header.LoadCollaboratorsCommand.ExecuteAsync(null);
    }

    private static string BuildDetailSignature(PlaylistDetailDto detail)
    {
        var caps = detail.Capabilities;
        return string.Join("|",
            detail.Id,
            detail.Name,
            detail.Description,
            detail.ImageUrl,
            detail.HeaderImageUrl,
            detail.OwnerName,
            detail.OwnerId,
            FormatAttributesSignature(detail.FormatAttributes),
            FormatRevision(detail.Revision),
            detail.SessionControlGroupId,
            detail.IsOwner,
            detail.IsPublic,
            detail.IsCollaborative,
            detail.BasePermission,
            caps.CanEditItems,
            caps.CanAdministratePermissions,
            caps.CanCancelMembership,
            caps.CanAbuseReport,
            caps.CanEditMetadata,
            caps.CanEditName,
            caps.CanEditDescription,
            caps.CanEditPicture,
            caps.CanEditCollaborative,
            caps.CanDelete,
            detail.FollowerCount);
    }

    private static string FormatAttributesSignature(IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is not { Count: > 0 })
            return string.Empty;

        return string.Join(
            ";",
            attributes
                .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                .Select(static kv => string.Concat(kv.Key, "=", kv.Value)));
    }

    private static string FormatRevision(object? revision)
        => revision switch
        {
            null => string.Empty,
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => revision.ToString() ?? string.Empty
        };

    private static string? ResolveOwnerProfileLookupKey(PlaylistDetailDto detail, string? currentOwnerName)
    {
        if (!string.IsNullOrWhiteSpace(detail.OwnerId))
            return detail.OwnerId;

        if (!string.IsNullOrWhiteSpace(detail.OwnerName)
            && detail.OwnerName.StartsWith("spotify:user:", StringComparison.Ordinal))
        {
            return detail.OwnerName;
        }

        if (!string.IsNullOrWhiteSpace(currentOwnerName)
            && currentOwnerName.StartsWith("spotify:user:", StringComparison.Ordinal))
        {
            return currentOwnerName;
        }

        return null;
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
            // Use GetProfileAsync (instead of GetDisplayNameAsync) so we also pick up
            // the owner's avatar URL — feeds the first slot of the collaborator stack.
            var profile = await _userProfileResolver
                .GetProfileAsync(ownerUri, CancellationToken.None)
                .ConfigureAwait(false);
            var displayName = profile?.DisplayName;
            var avatarUrl = profile?.AvatarUrl;
            if (string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(avatarUrl))
            {
                _logger?.LogWarning(
                    "ResolveOwnerDisplayNameAsync: resolver returned empty profile for '{OwnerUri}'",
                    ownerUri);
                return;
            }

            _logger?.LogInformation(
                "ResolveOwnerDisplayNameAsync: '{OwnerUri}' -> name='{DisplayName}' avatar={Avatar}",
                ownerUri, displayName ?? "<null>", string.IsNullOrEmpty(avatarUrl) ? "<null>" : "set");
            _dispatcherQueue.TryEnqueue(() =>
            {
                // Drop the result if navigation moved on; also drop if a fresher
                // value has already been written (e.g. the user typed a name while
                // the resolver was in flight — unlikely, but cheap to guard).
                if (_disposed || !string.Equals(PlaylistId, pinnedPlaylistId, StringComparison.Ordinal))
                    return;
                var changed = false;
                if (!string.IsNullOrWhiteSpace(displayName)
                    && !string.Equals(Header.OwnerName, displayName, StringComparison.Ordinal))
                {
                    Header.OwnerName = displayName;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(avatarUrl)
                    && !string.Equals(Header.OwnerAvatarUrl, avatarUrl, StringComparison.Ordinal))
                {
                    Header.OwnerAvatarUrl = avatarUrl;
                    changed = true;
                }
                if (changed)
                    Header.RefreshTrackDerivedState();
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
                Header.PlaylistImageUrl = fileUri;
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

    private async Task ApplyMosaicHeroFromTracksAsync(string playlistId, IReadOnlyList<PlaylistTrackDto> tracks)
    {
        if (_mosaicService is null || tracks.Count == 0) return;

        try
        {
            var path = await _mosaicService
                .GetMosaicFilePathFromTracksAsync(playlistId, tracks, CancellationToken.None)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(path))
                return;

            var fileUri = new Uri(path).AbsoluteUri;
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || !string.Equals(PlaylistId, playlistId, StringComparison.Ordinal))
                    return;
                if (string.IsNullOrWhiteSpace(Header.PlaylistImageUrl))
                    Header.PlaylistImageUrl = fileUri;
            });
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("ApplyMosaicHeroFromTracksAsync cancelled for {PlaylistId}", playlistId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ApplyMosaicHeroFromTracksAsync failed for {PlaylistId}", playlistId);
        }
    }

    private async Task<bool> RunOnUiAsync(string playlistId, CancellationToken ct, Action action)
    {
        ct.ThrowIfCancellationRequested();

        if (_dispatcherQueue.HasThreadAccess)
        {
            if (_disposed || !string.Equals(PlaylistId, playlistId, StringComparison.Ordinal))
                return false;

            action();
            return true;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.CanBeCanceled
            ? ct.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), tcs)
            : default;

        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }

                    if (_disposed || !string.Equals(PlaylistId, playlistId, StringComparison.Ordinal))
                    {
                        tcs.TrySetResult(false);
                        return;
                    }

                    action();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetResult(false);
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task LoadTracksAsync(string playlistId)
    {
        if (string.Equals(_tracksLoadInFlightFor, playlistId, StringComparison.Ordinal))
            return;

        _tracksLoadInFlightFor = playlistId;

        // Warm hit (revisiting a playlist whose tracks are already loaded): keep
        // the existing CTS alive and don't show the shimmer. The fetch still runs
        // (so remote edits land), but the apply path below diff-checks the result
        // against the cached snapshot and only ReplaceWith's the collection on a
        // real change — no row-churn, no shimmer flash, no scroll-position loss
        // on re-nav.
        var isWarmHit = _tracksLoadedFor == playlistId && TrackList.AllTracks.Count > 0;

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
                TrackList.IsLoadingTracks = true;

            // COLD open: paint clickable shimmer rows from the playlist skeletons
            // immediately, then stream TrackV4 metadata per-row instead of blocking
            // the whole list on the full batch. Warm revisits keep the diff-based
            // full apply below (TracksAreEquivalent / scroll preservation).
            if (!isWarmHit)
            {
                var skeletons = await _libraryDataService
                    .GetPlaylistTrackSkeletonsAsync(playlistId, ct)
                    .ConfigureAwait(false);
                if (skeletons.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var skeletonsApplied = await RunOnUiAsync(playlistId, ct, () =>
                    {
                        TrackList.ApplySkeletons(skeletons);
                        TrackList.IsLoadingTracks = false;
                        TrackList.ClearPendingSignalChip();
                    }).ConfigureAwait(false);

                    if (!skeletonsApplied)
                        return;

                    await _libraryDataService.StreamPlaylistTrackMetadataAsync(
                        skeletons,
                        dto =>
                        {
                            // Called off the UI thread per resolved track. EnqueueEnrich
                            // buffers + schedules a coalesced Low-priority drain, so the
                            // post-POST burst doesn't fire N dispatches in one frame.
                            if (_disposed || PlaylistId != playlistId || ct.IsCancellationRequested)
                                return;
                            TrackList.EnqueueEnrich(dto);
                        },
                        ct).ConfigureAwait(false);

                    ct.ThrowIfCancellationRequested();
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        if (_disposed || PlaylistId != playlistId)
                            return;

                        // Finalize runs after the last enrich batch drains (drain-driven),
                        // so it doesn't pre-empt the capped drains and re-burst the tail.
                        TrackList.MarkStreamingEnrichComplete(() =>
                        {
                            if (_disposed || PlaylistId != playlistId)
                                return;

                            ScheduleVideoAvailabilityFetch(playlistId, ct);
                            _tracksLoadedFor = playlistId;
                            _logger?.LogInformation(
                                "Tracks streamed: {Count} tracks for '{PlaylistId}' first3={First3}",
                                TrackList.AllTracks.Count, playlistId,
                                string.Join(",", TrackList.AllTracks.Take(3).Select(t => t.Id)));

                            if (string.Equals(_pendingFallbackMosaicPlaylistId, playlistId, StringComparison.Ordinal) &&
                                string.IsNullOrWhiteSpace(Header.PlaylistImageUrl))
                            {
                                _pendingFallbackMosaicPlaylistId = null;
                                _ = ApplyMosaicHeroFromTracksAsync(playlistId, TrackList.AllTracks.ToArray());
                            }

                            if (Header.ShouldShowAddedByColumn)
                                _ = Header.ResolveAddedByUsernamesAsync(playlistId, ct);
                        });
                    });
                    return;
                }
                // Empty playlist or no streaming store: fall through to the full apply.
            }

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
                if (isWarmHit && TrackList.TracksAreEquivalent(tracks))
                {
                    TrackList.ApplyVideoAvailabilityToCurrentTracks(tracks);
                    ScheduleVideoAvailabilityFetch(playlistId, ct);
                    _tracksLoadedFor = playlistId;
                    TrackList.IsLoadingTracks = false;
                    TrackList.ClearPendingSignalChip();
                    if (string.Equals(_pendingFallbackMosaicPlaylistId, playlistId, StringComparison.Ordinal) &&
                        string.IsNullOrWhiteSpace(Header.PlaylistImageUrl))
                    {
                        _pendingFallbackMosaicPlaylistId = null;
                        _ = ApplyMosaicHeroFromTracksAsync(playlistId, TrackList.AllTracks.ToArray());
                    }
                    _logger?.LogInformation(
                        "Tracks unchanged after refresh: {Count} same Ids for '{PlaylistId}' first3={First3}",
                        tracks.Count, playlistId,
                        string.Join(",", tracks.Take(3).Select(t => t.Id)));
                    return;
                }

                TrackList.ApplyTracks(tracks);
                ScheduleVideoAvailabilityFetch(playlistId, ct);
                _tracksLoadedFor = playlistId;
                TrackList.IsLoadingTracks = false;
                TrackList.ClearPendingSignalChip();
                _logger?.LogInformation(
                    "Tracks applied: {Count} tracks for '{PlaylistId}' first3={First3}",
                    TrackList.AllTracks.Count, playlistId,
                    string.Join(",", TrackList.AllTracks.Take(3).Select(t => t.Id)));

                // _allTracks is populated — the unique addedBy set is now derivable.
                // RebuildCollaboratorsFromContext is fired from the TracksChanged
                // event wired up in the constructor; the mosaic fallback runs here
                // because it's a parent-level orchestration concern (it can need
                // both the new track snapshot AND the previous PlaylistImageUrl).
                if (string.Equals(_pendingFallbackMosaicPlaylistId, playlistId, StringComparison.Ordinal) &&
                    string.IsNullOrWhiteSpace(Header.PlaylistImageUrl))
                {
                    _pendingFallbackMosaicPlaylistId = null;
                    _ = ApplyMosaicHeroFromTracksAsync(playlistId, TrackList.AllTracks.ToArray());
                }

                // Background addedBy resolution — fills AddedByDisplayName /
                // AddedByAvatarUrl on each DTO. Runs whenever the AddedBy column
                // will be visible (collab playlists OR any playlist where the
                // current user isn't the owner) so the cells don't fall back to
                // the long bare-id "@…" rendering. Captures the playlistId so a
                // stale resolution doesn't write into a swapped page.
                if (Header.ShouldShowAddedByColumn)
                    _ = Header.ResolveAddedByUsernamesAsync(playlistId, ct);
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
                TrackList.IsLoadingTracks = false;
                HasError = true;
                ErrorMessage = ErrorMapper.ToUserMessage(ex);
            });
        }
        finally
        {
            if (string.Equals(_tracksLoadInFlightFor, playlistId, StringComparison.Ordinal))
                _tracksLoadInFlightFor = null;
        }
    }

    // ── Page-level convenience commands ─────────────────────────────────────

    [RelayCommand]
    private void Retry()
    {
        HasError = false;
        ErrorMessage = null;
        _playlistStore.Invalidate(PlaylistId);
        _tracksLoadedFor = null;
    }

    /// <summary>
    /// Copies the playlist's open.spotify.com link to the clipboard. Synchronous,
    /// no backend; matches the album page's Share affordance.
    /// </summary>
    [RelayCommand]
    private void SharePlaylist()
    {
        if (string.IsNullOrEmpty(PlaylistId)) return;
        const string prefix = "spotify:playlist:";
        var bareId = PlaylistId.StartsWith(prefix, StringComparison.Ordinal)
            ? PlaylistId[prefix.Length..]
            : PlaylistId;

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText($"https://open.spotify.com/playlist/{bareId}");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<INotificationService>()?
                .Show("Link copied to clipboard", NotificationSeverity.Informational, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SharePlaylist failed for {PlaylistId}", PlaylistId);
        }
    }
}
