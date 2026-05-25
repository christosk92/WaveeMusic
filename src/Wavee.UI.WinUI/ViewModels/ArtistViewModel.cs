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
using Wavee.Core.Data;
using Wavee.Core.Http;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Data.Stores;
using Wavee.UI.Helpers;
using Wavee.UI.Services.Artists;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels.Artist;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Thin composer that owns the six child VMs (<see cref="Header"/>,
/// <see cref="TopTracks"/>, <see cref="Discography"/>,
/// <see cref="RelatedArtists"/>, <see cref="Bio"/>, <see cref="Extras"/>),
/// wires their cross-dependencies, and orchestrates the load lifecycle
/// (Initialize / Hibernate / Dispose, store subscription, overview apply,
/// spotlight selection projection, background top-track image enrichment,
/// background discography pagination, on-device bio summary).
///
/// <para>The decomposition replaces the previous ~2570-line "god ViewModel"
/// that owned every artist-detail concern. Each child has a single
/// responsibility; they communicate via the parent — no direct child-to-
/// child references.</para>
/// </summary>
public sealed partial class ArtistViewModel : ObservableObject, ITabBarItemContent, IDisposable
{
    private readonly IMusicVideoMetadataService? _musicVideoMetadataService;
    private readonly Wavee.UI.Services.Infra.IBackgroundWorkRunner _backgroundWork;
    private readonly ArtistStore _artistStore;
    private readonly ILocationService _locationService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ITrackLikeService? _likeService;
    private readonly ILogger? _logger;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    private CompositeDisposable? _subscriptions;
    private string? _appliedOverviewFor;
    private ArtistOverviewResult? _appliedOverview;
    private string? _videoCatalogPrimedFor;
    private ArtistOverviewResult? _videoCatalogPrimedOverview;
    private int _loadGeneration;
    private bool _disposed;

    /// <summary>Header — hero image / verified / monthly listener traits,
    /// palette-driven brushes, tour banner projection. Constructor-init.</summary>
    public ArtistHeaderViewModel Header { get; }

    /// <summary>Top-tracks list, extended-fetch state, play commands,
    /// play-pending 8 s timeout, page-based pagination. Constructor-init.</summary>
    public ArtistTopTracksViewModel TopTracks { get; }

    /// <summary>Albums / singles / compilations / appears-on / popular
    /// releases. Capped projections + "See all" thresholds, background
    /// pagination, album expand/collapse. Constructor-init.</summary>
    public ArtistDiscographyViewModel Discography { get; }

    /// <summary>"Fans also like" related artists shelf. Constructor-init.</summary>
    public ArtistRelatedArtistsViewModel RelatedArtists { get; }

    /// <summary>Biography, peek-line, on-device AI summary. Constructor-init.</summary>
    public ArtistBioViewModel Bio { get; }

    /// <summary>Music videos, merch, playlists, external links, top cities,
    /// gallery photos, concerts. Constructor-init.</summary>
    public ArtistExtrasViewModel Extras { get; }

    public ArtistViewModel(
        IArtistService artistService,
        ArtistStore artistStore,
        IAlbumService albumService,
        ILocationService locationService,
        IPlaybackService playbackService,
        IPlaybackStateService playbackStateService,
        IColorService colorService,
        PaletteGradientCompositor paletteCompositor,
        DiscographyPaginationService discographyPaginationService,
        SpotlightSelectionService spotlightSelectionService,
        ITrackLikeService? likeService = null,
        ISettingsService? settingsService = null,
        ArtistBioSummarizer? bioSummarizer = null,
        AiCapabilities? aiCapabilities = null,
        IMusicVideoMetadataService? musicVideoMetadataService = null,
        Wavee.UI.Services.Infra.IBackgroundWorkRunner? backgroundWorkRunner = null,
        ILogger<ArtistViewModel>? logger = null)
    {
        _musicVideoMetadataService = musicVideoMetadataService;
        _artistStore = artistStore;
        _locationService = locationService;
        _playbackStateService = playbackStateService;
        _likeService = likeService;
        _backgroundWork = backgroundWorkRunner ?? new Wavee.UI.Services.Infra.BackgroundWorkRunner();
        _logger = logger;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // ── Child VMs ────────────────────────────────────────────────────────
        Extras = new ArtistExtrasViewModel(
            userLocationProvider: () => _locationService.CurrentCity,
            isNearUserChecker: city => _locationService.IsNearUser(city));

        Header = new ArtistHeaderViewModel(paletteCompositor)
        {
            ConcertsSnapshotProvider = () => Extras.Concerts,
        };

        TopTracks = new ArtistTopTracksViewModel(
            playbackService,
            playbackStateService,
            artistService,
            musicVideoMetadataService,
            _backgroundWork,
            logger,
            artistIdProvider: () => ArtistId,
            artistNameProvider: () => Header.ArtistName,
            artistImageUrlProvider: () => Header.ArtistImageUrl,
            isCurrentLoadProvider: gen => IsCurrentLoad(ArtistId ?? string.Empty, gen),
            currentGenerationProvider: () => Volatile.Read(ref _loadGeneration));

        Discography = new ArtistDiscographyViewModel(
            artistService,
            albumService,
            colorService,
            settingsService,
            discographyPaginationService,
            spotlightSelectionService,
            _backgroundWork,
            logger,
            artistIdProvider: () => ArtistId,
            artistNameProvider: () => Header.ArtistName,
            isCurrentLoadProvider: gen => IsCurrentLoad(ArtistId ?? string.Empty, gen),
            currentGenerationProvider: () => Volatile.Read(ref _loadGeneration));

        RelatedArtists = new ArtistRelatedArtistsViewModel();

        Bio = new ArtistBioViewModel(
            bioSummarizer,
            aiCapabilities,
            logger,
            biographyProvider: () => Header.Artist?.Biography,
            artistNameProvider: () => Header.ArtistName,
            monthlyListenersProvider: () => Header.MonthlyListeners,
            topTrackNamesProvider: () => TopTracks.Tracks
                .Where(t => t.IsLoaded && t.Data is { } d && !string.IsNullOrEmpty(d.Title))
                .Select(t => ((ITrackItem)t.Data!).Title!)
                .Where(s => !string.IsNullOrEmpty(s))
                .Take(5)
                .ToList());

        // ── Cross-child wiring ───────────────────────────────────────────────
        // Header envelope changes → Bio re-projects its bio-derived surfaces
        // (Biography/HeroBioLine/etc.) and the spotlight selection re-derives.
        Header.ArtistChanged += (_, _) =>
        {
            Bio.NotifyBiographyChanged();
            UpdateTabTitle(Header.ArtistName);
            RaiseSpotlightProjection();
        };

        // Extras concerts changed → Header re-projects tour-banner texts.
        Extras.ConcertsChanged += (_, _) =>
        {
            Header.NotifyConcertsChanged();
        };

        AttachLongLivedServices();

        Diagnostics.LiveInstanceTracker.Register(this);
    }

    // ── Tab management ──────────────────────────────────────────────────────

    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    // ── Top-level scalar state ──────────────────────────────────────────────

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _artistId;

    [ObservableProperty] private bool _isFollowing;

    /// <summary>True only when <c>?debug</c> was passed via the navigation
    /// parameter — gates the small "source-chip" pills on each V4A section
    /// header that name the GraphQL fragments backing it.</summary>
    [ObservableProperty] private bool _isDebugMode;

    // ── Spotlight projection (derived from Header + Discography) ────────────

    [ObservableProperty] private SpotlightMode _spotlightCardMode;
    [ObservableProperty] private string? _spotlightReleaseName;
    [ObservableProperty] private string? _spotlightReleaseImageUrl;
    [ObservableProperty] private string? _spotlightReleaseUri;
    [ObservableProperty] private string? _spotlightReleaseSubtitle;
    [ObservableProperty] private int _spotlightReleaseTrackCount;
    [ObservableProperty] private string _spotlightReleaseTagText = string.Empty;
    [ObservableProperty] private string _spotlightReleaseEyebrowText = string.Empty;
    [ObservableProperty] private string? _spotlightCommentText;
    [ObservableProperty] private bool _hasSpotlightRelease;
    [ObservableProperty] private bool _hasSpotlightComment;

    /// <summary>The Popular Releases column shown in the V4A magazine layout
    /// — derived from <see cref="SpotlightSelectionService"/>'s mode-dependent
    /// projection so the rows never collide with the hero spotlight.</summary>
    [ObservableProperty] private IReadOnlyList<LazyReleaseItem> _popularReleasesDisplayed = Array.Empty<LazyReleaseItem>();

    /// <summary>
    /// Recompute the spotlight selection by combining the header's pinned /
    /// latest-release envelope with discography's popular releases. Fired
    /// every time either source mutates.
    /// </summary>
    private void RaiseSpotlightProjection()
    {
        var selection = Discography.ComputeSpotlightSelection(
            Header.PinnedItem,
            Header.LatestRelease);

        SpotlightCardMode = selection.Mode;
        SpotlightReleaseName = selection.Name;
        SpotlightReleaseImageUrl = selection.ImageUrl;
        SpotlightReleaseUri = selection.Uri;
        SpotlightReleaseSubtitle = selection.Subtitle;
        SpotlightReleaseTrackCount = selection.TrackCount;
        SpotlightReleaseTagText = selection.TagText;
        SpotlightReleaseEyebrowText = selection.EyebrowText;
        SpotlightCommentText = selection.Comment;
        HasSpotlightRelease = !string.IsNullOrEmpty(selection.Name) && !string.IsNullOrEmpty(selection.Uri);
        HasSpotlightComment = HasSpotlightRelease && !string.IsNullOrEmpty(selection.Comment);

        // Project mode-dependent popular-releases column. The selection
        // service yields a list of lightweight rows; we wrap each in a
        // LazyReleaseItem so the PopularReleaseRow template can bind against
        // the existing ArtistReleaseVm contract.
        var displayed = new List<LazyReleaseItem>(selection.PopularReleasesDisplayed.Count);
        int idx = 0;
        foreach (var row in selection.PopularReleasesDisplayed)
        {
            // Find the matching LazyReleaseItem in Discography.PopularReleases
            // so the bound containers reuse the same instances and don't
            // recycle when the column reshuffles. Synthesise a virtual row
            // when no match exists (Pinned-mode latest-release fallback).
            var match = Discography.PopularReleases
                .FirstOrDefault(p => p.IsLoaded && p.Data is not null
                    && string.Equals(p.Data.Uri, row.Uri, StringComparison.Ordinal));
            if (match is not null)
            {
                displayed.Add(match);
            }
            else if (!string.IsNullOrEmpty(row.Uri) && !string.IsNullOrEmpty(row.Name))
            {
                displayed.Add(LazyReleaseItem.Loaded(row.Uri!, idx, new ArtistReleaseVm
                {
                    Id = row.Uri!,
                    Uri = row.Uri,
                    Name = row.Name,
                    ImageUrl = row.ImageUrl,
                    Type = row.Type ?? string.Empty,
                    TrackCount = row.TrackCount,
                    Year = row.Year,
                }));
            }
            idx++;
        }
        PopularReleasesDisplayed = displayed;
    }

    // ── Location operations (delegated to ILocationService) ─────────────────

    public async Task<List<LocationSearchResult>> SearchLocationsAsync(string query, CancellationToken ct = default)
        => await _locationService.SearchAsync(query, ct);

    public async Task SaveLocationAsync(string geonameId, string? cityName)
    {
        await _locationService.SaveByGeonameIdAsync(geonameId, cityName);
        Extras.UserLocationName = cityName ?? _locationService.CurrentCity;
        Extras.RefreshNearUserFlags();
    }

    public async Task<LocationSearchResult?> ResolveCurrentLocationAsync()
    {
        try
        {
            var geolocator = new Windows.Devices.Geolocation.Geolocator();
            var position = await geolocator.GetGeopositionAsync();
            var lat = position.Coordinate.Point.Position.Latitude;
            var lon = position.Coordinate.Point.Position.Longitude;

            var results = await _locationService.SearchByCoordinatesAsync(lat, lon);
            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve current location");
            return null;
        }
    }

    public void RefreshNearUserFlags() => Extras.RefreshNearUserFlags();

    // ── Initialization ──────────────────────────────────────────────────────

    public void Initialize(string artistId)
    {
        AttachLongLivedServices();

        // Reset on any artist-id change, including null→firstId. The earlier
        // guard `ArtistId != null && ArtistId != artistId` was defensive
        // against a redundant clear on the very first nav (everything's empty
        // anyway), but on a Required-cache reused page the same VM serves
        // many artists and the prior null-guard occasionally let stale state
        // through (TopTracks, ArtistName, MonthlyListeners) when navigating
        // X→Y in the same tab. Clearing on every change is harmless on first
        // nav and correct on every subsequent nav.
        if (ArtistId != artistId)
        {
            Interlocked.Increment(ref _loadGeneration);
            ResetForNewArtist();
            _appliedOverviewFor = null;
            _appliedOverview = null;
        }

        ArtistId = artistId;
        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Artist, artistId)
        {
            Title = "Artist"
        };
        RefreshFollowState();
        TopTracks.SyncArtistPlaybackState();

        // Drop any prior subscription (cancels its inflight fetch via refcount==0)
        // and start observing the new artist through the reactive store.
        _subscriptions?.Dispose();
        _subscriptions = new CompositeDisposable();

        var sub = _artistStore.Observe(artistId)
            .Subscribe(
                state => _dispatcherQueue.TryEnqueue(() => ApplyOverviewState(state, artistId)),
                ex => _logger?.LogError(ex, "ArtistStore stream faulted for {ArtistId}", artistId));
        _subscriptions.Add(sub);
    }

    private bool IsCurrentLoad(string artistId, int generation)
        => !_disposed
           && generation == Volatile.Read(ref _loadGeneration)
           && string.Equals(ArtistId, artistId, StringComparison.Ordinal);

    /// <summary>
    /// Dispose the store subscription; fetches for this VM stop and any
    /// TaskCanceledException propagation is avoided.
    /// </summary>
    public void Deactivate()
    {
        DetachLongLivedServices();
        _subscriptions?.Dispose();
        _subscriptions = null;
    }

    // Long-lived singleton subscriptions are attached lazily on first use and
    // detached on Hibernate so the (Transient) VM is not pinned by the singleton
    // services' invocation lists across navigations. Idempotent in both directions.
    private bool _longLivedAttached;

    private void AttachLongLivedServices()
    {
        if (_longLivedAttached) return;
        _longLivedAttached = true;
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
        _playbackStateService.PropertyChanged += OnPlaybackStateChanged;
    }

    private void DetachLongLivedServices()
    {
        if (!_longLivedAttached) return;
        _longLivedAttached = false;
        if (_likeService != null)
            _likeService.SaveStateChanged -= OnSaveStateChanged;
        _playbackStateService.PropertyChanged -= OnPlaybackStateChanged;
    }

    /// <summary>
    /// Light hibernation for cached pages going off-screen. Disposes the store
    /// subscription and releases the things that pin DirectX textures (hero /
    /// avatar / pinned-card / latest-release image URLs). Data collections
    /// (TopTracks, Discography, RelatedArtists, Concerts, ExternalLinks,
    /// TopCities, GalleryPhotos) and the <c>_appliedOverviewFor</c> marker
    /// are intentionally preserved so a revisit to the same artist
    /// short-circuits in <see cref="ApplyOverviewState"/> without re-running
    /// the heavy <see cref="LoadAsync"/> path (which costs ~2 s on a popular
    /// artist: replaces section snapshots, reseeds virtualized item
    /// containers, kicks off background discography paging + release-color
    /// prefetch). The hero URLs are restored via <see cref="EnsureHeroUrls"/>
    /// in the Ready branch when LoadAsync is skipped.
    /// </summary>
    public void Hibernate()
    {
        Deactivate();

        TopTracks.ClearTopTracksSelection();
        Discography.ExpandedAlbumTracks.Clear();
        Discography.CancelInflightWork();

        if (Header.Artist is { } artist)
        {
            Header.Artist = artist with
            {
                ArtistImageUrl = null,
                HeaderImageUrl = null,
                LatestRelease = artist.LatestRelease is null
                    ? null
                    : artist.LatestRelease with { ImageUrl = null },
                PinnedItem = null
            };
        }
    }

    private void ApplyOverviewState(EntityState<ArtistOverviewResult> state, string expectedArtistId)
    {
        if (_disposed || ArtistId != expectedArtistId)
            return;

        switch (state)
        {
            case EntityState<ArtistOverviewResult>.Initial:
                IsLoading = true;
                break;
            case EntityState<ArtistOverviewResult>.Loading loading:
                IsLoading = loading.Previous is null;
                break;
            case EntityState<ArtistOverviewResult>.Ready ready:
                if (_appliedOverviewFor != expectedArtistId || !ReferenceEquals(_appliedOverview, ready.Value))
                {
                    _backgroundWork.Run(_ => LoadAsync(ready.Value, expectedArtistId), "ArtistViewModel.LoadAsync");
                }
                else
                {
                    // Same artist, stale-but-not-fresh — Hibernate may have
                    // null'd the hero URL bindings (texture release) without
                    // touching data collections. Restore the URLs from the
                    // cached overview without re-running the heavy LoadAsync.
                    EnsureHeroUrls(ready.Value);
                    PrimeMusicVideoCatalogOnce(ready.Value, expectedArtistId);
                }
                IsLoading = false;
                break;
            case EntityState<ArtistOverviewResult>.Error error:
                HasError = true;
                ErrorMessage = error.Exception.Message;
                IsLoading = false;
                _logger?.LogError(error.Exception, "ArtistStore reported error for {ArtistId}", expectedArtistId);
                break;
        }
    }

    /// <summary>
    /// Populates the music-video catalog cache with the top-tracks' has-video
    /// flags once per applied overview. Cache-served re-shows can emit Ready
    /// repeatedly; re-writing the same video entries on every navigation adds
    /// allocation churn right on the artist page hot path.
    /// </summary>
    private void PrimeMusicVideoCatalogOnce(ArtistOverviewResult overview, string artistId)
    {
        if (ReferenceEquals(_videoCatalogPrimedOverview, overview)
            && string.Equals(_videoCatalogPrimedFor, artistId, StringComparison.Ordinal))
        {
            return;
        }

        if ((overview.TopTracks is null || overview.TopTracks.Count == 0)
            && overview.MusicVideoMappings.Count == 0)
            return;

        var videoMetadata = _musicVideoMetadataService;
        if (videoMetadata is null) return;

        _videoCatalogPrimedOverview = overview;
        _videoCatalogPrimedFor = artistId;

        _logger?.LogInformation("[VideoCache] ArtistViewModel pre-warm: {Count} top tracks for {Artist}",
            overview.TopTracks?.Count ?? 0, artistId);
        if (overview.TopTracks is not null)
        {
            foreach (var track in overview.TopTracks)
            {
                if (string.IsNullOrEmpty(track.Uri)) continue;
                videoMetadata.NoteHasVideo(track.Uri, track.HasVideo);
            }
        }

        foreach (var mapping in overview.MusicVideoMappings)
        {
            videoMetadata.NoteVideoUri(mapping.AudioTrackUri, mapping.VideoTrackUri);
            _logger?.LogDebug("[VideoCache]   {AudioUri} -> {VideoUri}",
                mapping.AudioTrackUri, mapping.VideoTrackUri);
        }
    }

    private static ArtistView BuildArtistView(
        ArtistOverviewResult overview,
        string? fallbackName = null,
        string? fallbackImageUrl = null)
    {
        return new ArtistView(
            Name: overview.Name ?? fallbackName,
            ArtistImageUrl: overview.ImageUrl ?? fallbackImageUrl,
            HeaderImageUrl: overview.HeaderImageUrl,
            HeaderHeroColorHex: overview.HeroColorHex,
            Palette: overview.Palette,
            MonthlyListeners: overview.MonthlyListeners > 0
                ? overview.MonthlyListeners.ToString("N0")
                : null,
            WorldRank: overview.WorldRank,
            Followers: overview.Followers,
            Biography: overview.Biography,
            IsVerified: overview.IsVerified,
            IsRegistered: overview.IsRegistered,
            LatestRelease: overview.LatestRelease,
            AlbumsTotalCount: overview.AlbumsTotalCount,
            SinglesTotalCount: overview.SinglesTotalCount,
            CompilationsTotalCount: overview.CompilationsTotalCount,
            PinnedItem: overview.PinnedItem,
            WatchFeed: overview.WatchFeed);
    }

    private void EnsureHeroUrls(ArtistOverviewResult overview)
    {
        if (Header.Artist is null)
        {
            Header.Artist = BuildArtistView(overview);
            return;
        }

        var latest = Header.Artist.LatestRelease;
        if (overview.LatestRelease is not null && string.IsNullOrEmpty(latest?.ImageUrl))
        {
            latest = (latest ?? overview.LatestRelease) with
            {
                ImageUrl = overview.LatestRelease.ImageUrl
            };
        }

        var next = Header.Artist with
        {
            ArtistImageUrl = string.IsNullOrEmpty(Header.Artist.ArtistImageUrl)
                ? overview.ImageUrl
                : Header.Artist.ArtistImageUrl,
            HeaderImageUrl = string.IsNullOrEmpty(Header.Artist.HeaderImageUrl)
                ? overview.HeaderImageUrl
                : Header.Artist.HeaderImageUrl,
            LatestRelease = latest,
            PinnedItem = Header.Artist.PinnedItem ?? overview.PinnedItem
        };

        if (!Equals(next, Header.Artist))
            Header.Artist = next;
    }

    private void ResetForNewArtist()
    {
        Header.Artist = null;
        Bio.ResetForNewArtist();
        IsFollowing = false;
        HasData = false;
        _videoCatalogPrimedOverview = null;
        _videoCatalogPrimedFor = null;

        TopTracks.ResetForNewArtist();
        Discography.ResetForNewArtist();

        // Clear below-the-fold sections synchronously during the artist swap.
        // A previous low-priority clear could run after a warm-cache Ready path
        // had already repopulated these collections, leaving discovery, videos,
        // concerts, merch, and popular/spotlight sections collapsed until the
        // next navigation.
        RelatedArtists.ResetForNewArtist();
        Extras.ResetForNewArtist();
        RaiseSpotlightProjection();
    }

    public void PrefillFrom(ContentNavigationParameter nav)
    {
        if (string.IsNullOrEmpty(nav.Title) && string.IsNullOrEmpty(nav.ImageUrl))
            return;

        var current = Header.Artist ?? new ArtistView(
            Name: null,
            ArtistImageUrl: null,
            HeaderImageUrl: null,
            HeaderHeroColorHex: null,
            Palette: null,
            MonthlyListeners: null,
            WorldRank: null,
            Followers: 0,
            Biography: null,
            IsVerified: false,
            IsRegistered: false,
            LatestRelease: null,
            AlbumsTotalCount: 0,
            SinglesTotalCount: 0,
            CompilationsTotalCount: 0,
            PinnedItem: null,
            WatchFeed: null);

        Header.Artist = current with
        {
            Name = !string.IsNullOrEmpty(nav.Title) ? nav.Title : current.Name,
            ArtistImageUrl = !string.IsNullOrEmpty(nav.ImageUrl) ? nav.ImageUrl : current.ArtistImageUrl
        };
    }

    // ── Load data from real Pathfinder API ──────────────────────────────────

    /// <summary>
    /// Apply a freshly-fetched ArtistOverviewResult from the ArtistStore and
    /// kick off the downstream cascade (extended tracks, discography pages,
    /// concerts, color prefetch). Called by ApplyOverviewState on each
    /// Ready emission; idempotent per (artistId, overview-ref).
    /// </summary>
    private async Task LoadAsync(ArtistOverviewResult overview, string artistId)
    {
        var generation = Volatile.Read(ref _loadGeneration);
        if (!IsCurrentLoad(artistId, generation)) return;
        _appliedOverviewFor = artistId;
        _appliedOverview = overview;
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;
        Discography.HasAlbumsError = false;
        Discography.HasSinglesError = false;
        Discography.HasCompilationsError = false;

        try
        {
            var fallbackName = Header.ArtistName;
            var fallbackImageUrl = Header.ArtistImageUrl;
            Header.Artist = BuildArtistView(overview, fallbackName, fallbackImageUrl);
            var releaseImageByUri = new Dictionary<string, string>(StringComparer.Ordinal);
            var releaseImageByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddReleaseImages(overview.Albums, releaseImageByUri, releaseImageByName);
            AddReleaseImages(overview.Singles, releaseImageByUri, releaseImageByName);
            AddReleaseImages(overview.Compilations, releaseImageByUri, releaseImageByName);
            PrimeMusicVideoCatalogOnce(overview, artistId);

            // -- Top tracks (batch to avoid N+1 CollectionChanged events) --
            var videoMetadata = _musicVideoMetadataService;
            var pageSize = TopTracks.PlaceholderPageSize;
            var estimatedCount = overview.TopTracks.Count + Math.Max(pageSize, 1);
            var newTracks = new List<LazyTrackItem>(estimatedCount);
            var topTrackVms = videoMetadata is null
                ? null
                : new List<ArtistTopTrackVm>(overview.TopTracks.Count);
            int idx = 1;
            foreach (var track in overview.TopTracks)
            {
                var trackVm = new ArtistTopTrackVm
                {
                    Id = track.Id,
                    Index = idx,
                    Title = track.Title,
                    Uri = track.Uri,
                    AlbumName = track.AlbumName,
                    AlbumImageUrl = track.AlbumImageUrl
                                    ?? TryGetReleaseImage(track, releaseImageByUri, releaseImageByName),
                    AlbumUri = track.AlbumUri,
                    Duration = track.Duration,
                    PlayCountRaw = track.PlayCount,
                    ArtistNames = track.ArtistNames,
                    IsExplicit = track.IsExplicit,
                    IsPlayable = track.IsPlayable,
                    HasCanvasVideo = track.HasVideo
                };
                newTracks.Add(LazyTrackItem.Loaded(trackVm.Id, idx, trackVm));
                topTrackVms?.Add(trackVm);
                idx++;
            }

            if (videoMetadata is not null && topTrackVms is { Count: > 0 })
            {
                // Light the music-video badge on rows whose Spotify track is
                // linked to a local music-video file. Fire-and-forget; the
                // VM's HasLinkedLocalVideo setter raises PropertyChanged so
                // TrackItem updates its badge live when the result lands.
                _backgroundWork.Run(
                    _ => videoMetadata.ApplyAvailabilityToAsync(
                        topTrackVms,
                        static t => t.Uri,
                        static (t, v) => t.HasLinkedLocalVideo = v,
                        CancellationToken.None),
                    "ArtistViewModel.ApplyMusicVideoAvailability");
            }

            // Pad + shimmer placeholders. The child exposes a PlaceholderPageSize
            // hint that uses its current ColumnCount × RowsPerPage, falling
            // back to 12 (the original 3 × 4 layout) when the repeater hasn't
            // reported a column count yet — matches the legacy fallback math.
            var loadedCount = idx - 1;
            var remainder = loadedCount % pageSize;
            var padCount = remainder > 0 ? pageSize - remainder : 0;
            for (int i = 0; i < padCount + pageSize; i++)
            {
                newTracks.Add(LazyTrackItem.Placeholder($"placeholder-{idx}", idx));
                idx++;
            }
            TopTracks.Tracks.ReplaceWith(newTracks);
            TopTracks.NotifyPaginationChanged();
            TopTracks.NotifyTopTracksFirst10Changed();

            // -- Backfill missing cover art (background, parallel) --
            // Spotify's getArtistOverview GraphQL response is inconsistent: many
            // tracks come back without albumOfTrack.coverArt populated. Resolve
            // them via the extended-metadata pipeline and patch the VMs.
            _backgroundWork.Run(_ => TopTracks.EnrichMissingTopTrackImagesAsync(artistId, generation),
                "ArtistViewModel.EnrichMissingTopTrackImages");

            // Apply below-the-fold sections after the first viewport has rendered.
            _backgroundWork.Run(_ => ApplySecondaryArtistSectionsAsync(artistId, generation, overview),
                "ArtistViewModel.ApplySecondaryArtistSections");

            // V4A: kick off the on-device "about this artist" excerpt for
            // every artist. Gated through AiCapabilities inside the summarizer
            // so non-Copilot+ PCs or disabled AI stay a cheap no-op.
            _backgroundWork.Run(_ => Bio.LoadBioSummaryAsync(artistId), "ArtistViewModel.LoadBioSummary");
        }
        catch (Wavee.Core.Session.SessionException)
        {
            HasError = true;
            ErrorMessage = "Connecting to Spotify…";
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ErrorMapper.ToUserMessage(ex);
            _logger?.LogError(ex, "Failed to load artist {ArtistId}", ArtistId);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ApplySecondaryArtistSectionsAsync(
        string artistId,
        int generation,
        ArtistOverviewResult overview)
    {
        // Let hero and top tracks render before applying below-the-fold
        // collections. Each section can invalidate many WinUI containers; split
        // the work into smaller dispatcher slices to avoid one large UI stall.
        await Task.Delay(32).ConfigureAwait(false);

        if (!IsCurrentLoad(artistId, generation))
            return;

        await RunOnDispatcherAsync(() =>
        {
            if (IsCurrentLoad(artistId, generation))
                Discography.ApplyOverview(overview);
        }).ConfigureAwait(false);

        await Task.Delay(1).ConfigureAwait(false);

        if (!IsCurrentLoad(artistId, generation))
            return;

        await RunOnDispatcherAsync(() =>
        {
            if (IsCurrentLoad(artistId, generation))
                RelatedArtists.ApplyOverview(overview);
        }).ConfigureAwait(false);

        await Task.Delay(1).ConfigureAwait(false);

        if (!IsCurrentLoad(artistId, generation))
            return;

        await RunOnDispatcherAsync(() =>
        {
            if (!IsCurrentLoad(artistId, generation))
                return;

            Extras.ApplyOverview(overview);
            RaiseSpotlightProjection();

            TopTracks.CurrentPage = 0;
            TopTracks.NotifyPaginationChanged();

            _backgroundWork.Run(_ => StartDeferredArtistWorkAsync(artistId, generation, overview),
                "ArtistViewModel.StartDeferredArtistWork");
        }).ConfigureAwait(false);
    }

    private Task RunOnDispatcherAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
        {
            tcs.SetCanceled();
        }

        return tcs.Task;
    }

    private async Task StartDeferredArtistWorkAsync(
        string artistId,
        int generation,
        ArtistOverviewResult overview)
    {
        // Let ArtistPage render hero/top-tracks before starting secondary work.
        // These tasks update below-the-fold images, color chips, extended tracks,
        // and remaining discography pages; starting them in the same dispatcher
        // slice as LoadAsync makes PlayerBar artist navigation feel heavy.
        await Task.Yield();
        await Task.Delay(48);

        if (!IsCurrentLoad(artistId, generation))
            return;

        _backgroundWork.Run(_ => TopTracks.LoadExtendedTopTracksAsync(artistId, generation),
            "ArtistViewModel.LoadExtendedTopTracks");

        var releasesSnapshot = Discography.SnapshotLoadedReleases();
        _backgroundWork.Run(_ => Discography.PrefetchReleaseColorsAsync(artistId, generation, releasesSnapshot),
            "ArtistViewModel.PrefetchReleaseColors");

        var discoToken = Discography.CreateFreshDiscographyToken();
        _backgroundWork.Run(_ => Task.Run(() => Discography.FetchRemainingDiscographyAsync(
            artistId, generation,
            overview.Albums.Count, overview.AlbumsTotalCount,
            overview.Singles.Count, overview.SinglesTotalCount,
            overview.Compilations.Count, overview.CompilationsTotalCount,
            discoToken), discoToken), "ArtistViewModel.FetchRemainingDiscography");
    }

    private static void AddReleaseImages(
        IEnumerable<ArtistReleaseResult> releases,
        IDictionary<string, string> byUri,
        IDictionary<string, string> byName)
    {
        foreach (var release in releases)
        {
            if (string.IsNullOrWhiteSpace(release.ImageUrl))
                continue;

            if (!string.IsNullOrWhiteSpace(release.Uri))
                byUri.TryAdd(release.Uri, release.ImageUrl);

            if (!string.IsNullOrWhiteSpace(release.Name))
                byName.TryAdd(release.Name, release.ImageUrl);
        }
    }

    private static string? TryGetReleaseImage(
        ArtistTopTrackResult track,
        IReadOnlyDictionary<string, string> releaseImageByUri,
        IReadOnlyDictionary<string, string> releaseImageByName)
    {
        if (!string.IsNullOrWhiteSpace(track.AlbumUri)
            && releaseImageByUri.TryGetValue(track.AlbumUri, out var byUri))
        {
            return byUri;
        }

        if (!string.IsNullOrWhiteSpace(track.AlbumName)
            && releaseImageByName.TryGetValue(track.AlbumName, out var byName))
        {
            return byName;
        }

        return null;
    }

    // ── Top-level commands ──────────────────────────────────────────────────

    [RelayCommand]
    private void Retry()
    {
        HasError = false;
        ErrorMessage = null;
        if (!string.IsNullOrEmpty(ArtistId))
        {
            _appliedOverviewFor = null;
            _appliedOverview = null;
            _artistStore.Invalidate(ArtistId);
        }
    }

    [RelayCommand]
    private void ToggleFollow()
    {
        if (string.IsNullOrEmpty(ArtistId) || _likeService == null) return;
        var wasSaved = IsFollowing;
        IsFollowing = !wasSaved;
        _likeService.ToggleSave(SavedItemType.Artist, ArtistId, wasSaved);
    }

    private void RefreshFollowState()
    {
        if (!string.IsNullOrEmpty(ArtistId) && _likeService != null)
            IsFollowing = _likeService.IsSaved(SavedItemType.Artist, ArtistId);
    }

    private void OnSaveStateChanged()
    {
        _dispatcherQueue?.TryEnqueue(RefreshFollowState);
    }

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentContext)
            or nameof(IPlaybackStateService.IsPlaying)
            or nameof(IPlaybackStateService.IsBuffering))
        {
            _dispatcherQueue.TryEnqueue(TopTracks.SyncArtistPlaybackState);
        }
    }

    /// <summary>Kicks the IPlaybackStateService radio endpoint with the current
    /// artist URI. Mirrors the Artist context-menu "Artist radio" affordance
    /// (<c>ArtistContextMenuBuilder</c>) so the V4A hero's pill button reuses
    /// the same code path. No-op when the artist hasn't loaded yet.</summary>
    [RelayCommand]
    private async Task PlayArtistRadioAsync()
    {
        if (string.IsNullOrEmpty(ArtistId)) return;
        var uri = ArtistId.StartsWith("spotify:artist:", StringComparison.Ordinal)
            ? ArtistId
            : $"spotify:artist:{ArtistId}";
        var name = Header.ArtistName is { Length: > 0 } n ? $"{n} Radio" : "Artist Radio";
        await _playbackStateService.StartRadioAsync(uri, name);
    }

    private void UpdateTabTitle(string? value)
    {
        if (TabItemParameter != null && !string.IsNullOrEmpty(value))
        {
            TabItemParameter.Title = value;
            ContentChanged?.Invoke(this, TabItemParameter);
        }
    }

    /// <summary>
    /// Theme-aware palette refresh. Page calls this on init + on
    /// ActualThemeChanged + after Palette lands. Delegates to the header VM
    /// which composes the palette-derived brushes via
    /// <see cref="PaletteGradientCompositor"/>.
    /// </summary>
    public void ApplyTheme(bool isDark) => Header.ApplyTheme(isDark);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DetachLongLivedServices();

        _subscriptions?.Dispose();
        _subscriptions = null;

        Header.Dispose();
        TopTracks.Dispose();
        Discography.Dispose();
        RelatedArtists.Dispose();
        Bio.Dispose();
        Extras.Dispose();
    }
}
