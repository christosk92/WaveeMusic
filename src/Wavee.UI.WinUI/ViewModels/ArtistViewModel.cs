using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Wavee.Core.Data;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Data.Stores;
using Wavee.UI.WinUI.Helpers;
using Windows.UI;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ArtistViewModel : ObservableObject, ITabBarItemContent, IDisposable
{
    private const int PlayPendingTimeoutMs = 8000;
    private readonly IArtistService _artistService;
    private readonly ArtistStore _artistStore;
    private readonly IAlbumService _albumService;
    private readonly ILocationService _locationService;
    private readonly IPlaybackService _playbackService;
    private readonly IPlaybackStateService _playbackStateService;
    private CompositeDisposable? _subscriptions;
    private string? _appliedOverviewFor;
    private ArtistOverviewResult? _appliedOverview;
    private readonly IColorService _colorService;
    private readonly ITrackLikeService? _likeService;
    private readonly ISettingsService? _settingsService;
    private readonly ILogger? _logger;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private CancellationTokenSource? _discoCts;
    private CancellationTokenSource? _playPendingCts;
    private int _loadGeneration;
    private bool _disposed;

    // ── Backing data ──
    private readonly List<LazyReleaseItem> _allReleases = [];

    // ── UI-bound collections ──
    [ObservableProperty]
    private ObservableCollection<LazyTrackItem> _topTracks = [];

    [ObservableProperty]
    private IReadOnlyList<LazyReleaseItem> _albums = [];

    [ObservableProperty]
    private IReadOnlyList<LazyReleaseItem> _singles = [];

    [ObservableProperty]
    private IReadOnlyList<LazyReleaseItem> _compilations = [];

    // ── Non-reactive collections (simple lists) ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRelatedArtists))]
    private IReadOnlyList<RelatedArtistVm> _relatedArtists = [];

    public bool HasRelatedArtists => RelatedArtists.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConcerts))]
    private IReadOnlyList<ConcertVm> _concerts = [];

    [ObservableProperty]
    private ObservableCollection<LocationSearchResultVm> _locationSuggestions = [];

    /// <summary>Artist's external links (Twitter, Instagram, YouTube, etc.).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExternalLinks))]
    [NotifyPropertyChangedFor(nameof(HasConnectSection))]
    private IReadOnlyList<ArtistSocialLinkVm> _externalLinks = [];

    /// <summary>Top cities by listener count, with proportional bar widths.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopCities))]
    [NotifyPropertyChangedFor(nameof(HasConnectSection))]
    private IReadOnlyList<ArtistTopCityVm> _topCities = [];

    /// <summary>Photo URLs from the artist's gallery (largest variant).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGallery))]
    private IReadOnlyList<string> _galleryPhotos = [];

    public bool HasExternalLinks => ExternalLinks.Count > 0;
    public bool HasTopCities => TopCities.Count > 0;
    public bool HasConnectSection => HasExternalLinks || HasTopCities;
    public bool HasGallery => GalleryPhotos.Count > 0;

    [ObservableProperty]
    private string? _userLocationName;

    // ── Scalar properties ──

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _artistId;
    [ObservableProperty] private string? _artistName;
    [ObservableProperty] private string? _artistImageUrl;
    [ObservableProperty] private string? _headerImageUrl;
    [ObservableProperty] private string? _headerHeroColorHex;

    /// <summary>
    /// Spotify-extracted visual identity palette for the artist hero (3 contrast
    /// tiers). Code-behind picks a tier based on ActualTheme and uses BackgroundTinted
    /// as the page-wash colour, which reads richer than the single-hex HeroColorHex.
    /// Null when the API didn't return a visualIdentity block (older artists, etc.).
    /// </summary>
    [ObservableProperty] private ArtistPalette? _palette;

    // ── Palette-derived brushes ─────────────────────────────────────────
    // Built in ApplyTheme(isDark) from the active Palette tier (HigherContrast
    // for dark, HighContrast for light). Mirrors PlaylistViewModel/AlbumViewModel
    // patterns so all detail pages use the same alpha cadence + tier policy.
    // Null when no palette is available — bound XAML elements simply render
    // without tint.

    /// <summary>3 px vertical bar tint for section headers — matches the Home
    /// section accent treatment so Artist + Home read as one app.</summary>
    [ObservableProperty] private Brush? _sectionAccentBrush;

    /// <summary>4-stop horizontal gradient for the hero scrim. Tier
    /// BackgroundTinted top → Background bottom, fading right.</summary>
    [ObservableProperty] private Brush? _paletteHeroGradientBrush;

    /// <summary>Solid tier-accent brush for the primary Play button.</summary>
    [ObservableProperty] private Brush? _paletteAccentPillBrush;

    /// <summary>Luma-based contrast color (black/white) for text on the
    /// PaletteAccentPillBrush. Auto-picked so the Play label stays legible.</summary>
    [ObservableProperty] private Brush? _paletteAccentPillForegroundBrush;

    private bool _isDarkTheme = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthlyListenersDescription))]
    private string? _monthlyListeners;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorldRank))]
    [NotifyPropertyChangedFor(nameof(WorldRankNumberText))]
    private int? _worldRank;

    public bool HasWorldRank => WorldRank is > 0;

    public string? WorldRankNumberText
    {
        get
        {
            var rank = WorldRank;
            return rank is > 0 ? $"#{rank.Value:N0}" : null;
        }
    }

    /// <summary>Description-line variant for the About SettingsExpander header.</summary>
    public string MonthlyListenersDescription =>
        string.IsNullOrEmpty(MonthlyListeners)
            ? string.Empty
            : $"{MonthlyListeners} monthly listeners";
    [ObservableProperty] private long _followers;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBiography))]
    private string? _biography;
    public bool HasBiography => !string.IsNullOrWhiteSpace(Biography);
    [ObservableProperty] private bool _isVerified;
    [ObservableProperty] private bool _isFollowing;
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

    // Latest release
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLatestRelease))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseSubtitle))]
    private string? _latestReleaseName;
    [ObservableProperty] private string? _latestReleaseImageUrl;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLatestRelease))]
    private string? _latestReleaseUri;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestReleaseSubtitle))]
    private string? _latestReleaseDate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestReleaseSubtitle))]
    private int _latestReleaseTrackCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestReleaseSubtitle))]
    private string? _latestReleaseType;

    /// <summary>True when the artist has a latest release with valid metadata
    /// — drives Visibility on the rich card above the Albums grid.</summary>
    public bool HasLatestRelease =>
        !string.IsNullOrEmpty(LatestReleaseName) && !string.IsNullOrEmpty(LatestReleaseUri);

    /// <summary>Composite subtitle for the latest-release card:
    /// "{Type} · {Date} · {TrackCount} tracks". Skips empty parts.</summary>
    public string LatestReleaseSubtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrEmpty(LatestReleaseType)) parts.Add(LatestReleaseType!);
            if (!string.IsNullOrEmpty(LatestReleaseDate)) parts.Add(LatestReleaseDate!);
            if (LatestReleaseTrackCount > 0)
                parts.Add(LatestReleaseTrackCount == 1 ? "1 track" : $"{LatestReleaseTrackCount} tracks");
            return string.Join(" · ", parts);
        }
    }

    // Per-group total counts. Drive `Has*` flags so the Albums/Singles/Compilations
    // subtrees in ArtistPage.xaml can be x:Load-deferred — sparse artists (no
    // singles, no compilations) never instantiate those StackPanels at all.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbums))]
    private int _albumsTotalCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSingles))]
    private int _singlesTotalCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompilations))]
    private int _compilationsTotalCount;

    public bool HasAlbums => AlbumsTotalCount > 0;
    public bool HasSingles => SinglesTotalCount > 0;
    public bool HasCompilations => CompilationsTotalCount > 0;

    // Per-group view mode (Grid vs List)
    [ObservableProperty] private bool _albumsGridView = true;
    [ObservableProperty] private bool _singlesGridView = true;
    [ObservableProperty] private bool _compilationsGridView = true;

    /// <summary>
    /// Slider-driven scale for the Albums + Singles `UniformGridLayout` cell size.
    /// Multiplies a base of 160 × 220 px through `GridScaleToSizeConverter`.
    /// Persisted to <c>AppSettings.ArtistDiscographyGridScale</c>; mirror of the
    /// Library album scale slider (range 0.7–1.6).
    /// </summary>
    [ObservableProperty] private double _discographyGridScale = 1.0;

    // Per-group error state (background pagination failures)
    [ObservableProperty] private bool _hasAlbumsError;
    [ObservableProperty] private bool _hasSinglesError;
    [ObservableProperty] private bool _hasCompilationsError;

    // Top tracks pagination (Apple Music style)
    private const int RowsPerPage = 3;
    [ObservableProperty] private int _columnCount = 4;
    [ObservableProperty] private int _currentPage;

    private int TracksPerPage => RowsPerPage * ColumnCount;
    public int TotalPages => TopTracks.Count == 0 ? 0 : (int)Math.Ceiling((double)TopTracks.Count / TracksPerPage);
    public bool HasMultiplePages => TotalPages > 1;

    private List<LazyTrackItem>? _pagedTopTracksCache;
    public IEnumerable<LazyTrackItem> PagedTopTracks => _pagedTopTracksCache ??= BuildPagedTopTracks();

    // Top tracks selection
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

    private List<LazyTrackItem> BuildPagedTopTracks()
    {
        int start = CurrentPage * TracksPerPage;
        int available = TopTracks.Count - start;
        if (available <= 0) return [];
        int count = Math.Min(TracksPerPage, available);
        var result = new List<LazyTrackItem>(count);
        for (int i = 0; i < count; i++)
            result.Add(TopTracks[start + i]);
        return result;
    }

    // ── Expanded album detail ──
    [ObservableProperty] private LazyReleaseItem? _expandedAlbum;
    [ObservableProperty] private ObservableCollection<LazyTrackItem> _expandedAlbumTracks = [];
    [ObservableProperty] private bool _isLoadingExpandedTracks;

    // Pinned item + Watch feed
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPinnedItem))]
    [NotifyPropertyChangedFor(nameof(HasPinnedComment))]
    [NotifyPropertyChangedFor(nameof(PinnedBackdropImageUrl))]
    private ArtistPinnedItemResult? _pinnedItem;
    [ObservableProperty] private ArtistWatchFeedResult? _watchFeed;
    public bool HasPinnedItem => PinnedItem != null;
    public bool HasWatchFeed => WatchFeed != null;

    /// <summary>True when the pinned item has a non-empty artist note
    /// (Pathfinder's <c>comment</c> field). Drives Visibility on the
    /// italic quote line in the pinned card.</summary>
    public bool HasPinnedComment => !string.IsNullOrWhiteSpace(PinnedItem?.Comment);

    /// <summary>Best image URL to use as the pinned card's backdrop —
    /// Spotify's editorial <c>backgroundImageV2</c> when present, else
    /// falls back to the cover/playlist image. Both forms shipped from
    /// Pathfinder; using whichever is set keeps the card visually rich
    /// even when there's no dedicated backdrop.</summary>
    public string? PinnedBackdropImageUrl =>
        !string.IsNullOrWhiteSpace(PinnedItem?.BackgroundImageUrl)
            ? PinnedItem!.BackgroundImageUrl
            : PinnedItem?.ImageUrl;
    public bool HasConcerts => Concerts.Count > 0;

    // ── Location operations (delegated to ILocationService) ──

    public async Task<List<LocationSearchResult>> SearchLocationsAsync(string query, CancellationToken ct = default)
        => await _locationService.SearchAsync(query, ct);

    public async Task SaveLocationAsync(string geonameId, string? cityName)
    {
        await _locationService.SaveByGeonameIdAsync(geonameId, cityName);
        UserLocationName = cityName ?? _locationService.CurrentCity;
        RefreshNearUserFlags();
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

    public void RefreshNearUserFlags()
    {
        foreach (var c in Concerts)
            c.IsNearUser = _locationService.IsNearUser(c.City);
    }

    // ── Tab management ──
    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    // ── Constructor ──

    public ArtistViewModel(
        IArtistService artistService,
        ArtistStore artistStore,
        IAlbumService albumService,
        ILocationService locationService,
        IPlaybackService playbackService,
        IPlaybackStateService playbackStateService,
        IColorService colorService,
        ITrackLikeService? likeService = null,
        ISettingsService? settingsService = null,
        ILogger<ArtistViewModel>? logger = null)
    {
        _artistService = artistService;
        _artistStore = artistStore;
        _albumService = albumService;
        _locationService = locationService;
        _playbackService = playbackService;
        _playbackStateService = playbackStateService;
        _colorService = colorService;
        _likeService = likeService;
        _settingsService = settingsService;
        _logger = logger;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        AttachLongLivedServices();

        SelectedTopTracks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedTopTracksCount));
            OnPropertyChanged(nameof(HasTopTracksSelection));
        };

        // Hydrate the discography card-size scale from persisted settings,
        // clamped to the slider's range so a stale config can't render the
        // grid unusable.
        if (_settingsService != null)
        {
            var saved = _settingsService.Settings.ArtistDiscographyGridScale;
            _discographyGridScale = saved >= 0.7 && saved <= 1.6 ? saved : 1.0;
        }

        Diagnostics.LiveInstanceTracker.Register(this);
    }

    partial void OnDiscographyGridScaleChanged(double value)
    {
        // Mirror Library's GridScale persistence — clamp to slider range to
        // protect against out-of-bounds writes from external callers.
        var clamped = Math.Clamp(value, 0.7, 1.6);
        _settingsService?.Update(s => s.ArtistDiscographyGridScale = clamped);
    }

    // ── Initialization ──

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
        SyncArtistPlaybackState();

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
    /// (TopTracks, _allReleases, Albums, Singles, Compilations, RelatedArtists,
    /// Concerts, ExternalLinks, TopCities, GalleryPhotos) and the
    /// <c>_appliedOverviewFor</c> marker are intentionally preserved so a
    /// revisit to the same artist short-circuits in <see cref="ApplyOverviewState"/>
    /// without re-running the heavy <see cref="LoadAsync"/> path (which costs
    /// ~2 s on a popular artist: replaces section snapshots, reseeds
    /// virtualized item containers, kicks off background discography paging
    /// + release-color prefetch). The hero URLs are restored via
    /// <see cref="EnsureHeroUrls"/> in the Ready branch when LoadAsync is
    /// skipped.
    /// </summary>
    public void Hibernate()
    {
        Deactivate();

        // Drop transient UI-only state (selection / expanded-album drawer).
        SelectedTopTracks.Clear();
        ExpandedAlbumTracks.Clear();

        // Cancel any in-flight discography paging — populated lists stay.
        CancelAndDisposeDiscographyCts();

        // Null hero/avatar/pinned/latest-release image URL bindings so the
        // bound Image controls drop their BitmapImage references. WinUI
        // auto-releases the DirectX texture after the next frame in the live
        // tree (per docs/optimize-animations-and-media). On revisit,
        // EnsureHeroUrls reads from the cached BehaviorSubject value and
        // restores the URLs — single dispatcher tick, no LoadAsync re-run.
        ArtistImageUrl = null;
        HeaderImageUrl = null;
        LatestReleaseImageUrl = null;
        PinnedItem = null;
        OnPropertyChanged(nameof(HasPinnedItem));
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
                // Music-video catalog cache pre-warm. Runs unconditionally so
                // the cache is populated whether LoadAsync runs (fresh nav) or
                // we go down the EnsureHeroUrls path (cache-served re-show).
                NoteTopTracksHaveVideo(ready.Value);

                if (_appliedOverviewFor != expectedArtistId || !ReferenceEquals(_appliedOverview, ready.Value))
                {
                    _ = LoadAsync(ready.Value, expectedArtistId);
                }
                else
                {
                    // Same artist, stale-but-not-fresh — Hibernate may have
                    // null'd the hero URL bindings (texture release) without
                    // touching data collections. Restore the URLs from the
                    // cached overview without re-running the heavy LoadAsync.
                    EnsureHeroUrls(ready.Value);
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
    /// Restore the hero / avatar / latest-release / pinned-card image URLs from
    /// the cached overview after a Hibernate-triggered URL null-out. Cheap —
    /// six property assignments. Pairs with <see cref="Hibernate"/>.
    /// </summary>
    /// <summary>
    /// Populates the music-video catalog cache with the top-tracks' has-video
    /// flags. Called from <c>ApplyOverviewState</c> on every Ready state — both
    /// fresh navigations (where LoadAsync runs) and cache-served re-shows
    /// (where only EnsureHeroUrls runs). Harmless to call twice — the cache
    /// is idempotent.
    /// </summary>
    private void NoteTopTracksHaveVideo(ArtistOverviewResult overview)
    {
        if ((overview.TopTracks is null || overview.TopTracks.Count == 0)
            && overview.MusicVideoMappings.Count == 0)
            return;

        var videoMetadata = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.UI.WinUI.Services.IMusicVideoMetadataService>();
        if (videoMetadata is null) return;

        _logger?.LogInformation("[VideoCache] ArtistViewModel pre-warm: {Count} top tracks for {Artist}",
            overview.TopTracks?.Count ?? 0, ArtistId ?? "<unknown>");
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

    private void EnsureHeroUrls(ArtistOverviewResult overview)
    {
        if (string.IsNullOrEmpty(ArtistImageUrl))
            ArtistImageUrl = overview.ImageUrl;
        if (string.IsNullOrEmpty(HeaderImageUrl))
            HeaderImageUrl = overview.HeaderImageUrl;
        if (overview.LatestRelease != null && string.IsNullOrEmpty(LatestReleaseImageUrl))
            LatestReleaseImageUrl = overview.LatestRelease.ImageUrl;
        if (PinnedItem == null && overview.PinnedItem != null)
        {
            PinnedItem = overview.PinnedItem;
            OnPropertyChanged(nameof(HasPinnedItem));
        }
    }

    private void ResetForNewArtist()
    {
        ArtistName = null;
        ArtistImageUrl = null;
        HeaderImageUrl = null;
        HeaderHeroColorHex = null;
        Palette = null;
        MonthlyListeners = null;
        WorldRank = null;
        Followers = 0;
        Biography = null;
        IsVerified = false;
        IsFollowing = false;
        LatestReleaseName = null;
        LatestReleaseImageUrl = null;
        LatestReleaseUri = null;
        LatestReleaseDate = null;
        LatestReleaseTrackCount = 0;
        LatestReleaseType = null;
        PinnedItem = null;
        WatchFeed = null;
        HasData = false;
        CurrentPage = 0;
        ExpandedAlbum = null;
        ExpandedAlbumTracks.Clear();
        IsPlayPending = false;
        IsArtistContextPlaying = false;
        IsArtistContextPaused = false;

        TopTracks.Clear();
        _allReleases.Clear();
        Albums = [];
        Singles = [];
        Compilations = [];
        AlbumsTotalCount = 0;
        SinglesTotalCount = 0;
        CompilationsTotalCount = 0;
        HasAlbumsError = false;
        HasSinglesError = false;
        HasCompilationsError = false;
        RelatedArtists = [];
        Concerts = [];
        ExternalLinks = [];
        TopCities = [];
        GalleryPhotos = [];
    }

    private CancellationToken CreateFreshDiscographyToken()
    {
        CancelAndDisposeDiscographyCts();
        _discoCts = new CancellationTokenSource();
        return _discoCts.Token;
    }

    private void CancelAndDisposeDiscographyCts()
    {
        var cts = Interlocked.Exchange(ref _discoCts, null);
        if (cts == null) return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    public void PrefillFrom(ContentNavigationParameter nav)
    {
        if (!string.IsNullOrEmpty(nav.Title)) ArtistName = nav.Title;
        if (!string.IsNullOrEmpty(nav.ImageUrl)) ArtistImageUrl = nav.ImageUrl;
    }

    /// <summary>
    /// Distributes _allReleases into Albums, Singles, Compilations collections.
    /// </summary>
    private void DispatchReleases()
    {
        var albums = new List<LazyReleaseItem>();
        var singles = new List<LazyReleaseItem>();
        var compilations = new List<LazyReleaseItem>();

        foreach (var group in _allReleases
            .GroupBy(r => r.Data?.Type ?? InferTypeFromId(r.Id))
            .OrderBy(g => g.Key))
        {
            var sorted = group.OrderByDescending(r => r.Data?.ReleaseDate ?? DateTimeOffset.MinValue);
            var target = group.Key switch
            {
                "ALBUM" => albums,
                "SINGLE" => singles,
                "COMPILATION" => compilations,
                _ => null
            };

            if (target == null) continue;
            target.AddRange(sorted);
        }

        Albums = albums;
        Singles = singles;
        Compilations = compilations;
    }

    private static string InferTypeFromId(string id)
    {
        if (id.StartsWith("album-ph")) return "ALBUM";
        if (id.StartsWith("single-ph")) return "SINGLE";
        if (id.StartsWith("comp-ph")) return "COMPILATION";
        return "ALBUM";
    }

    // ── Load data from real Pathfinder API ──

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
        HasAlbumsError = false;
        HasSinglesError = false;
        HasCompilationsError = false;

        try
        {

            // ── Map scalar properties ──
            ArtistName = overview.Name ?? ArtistName;
            if (string.IsNullOrEmpty(ArtistImageUrl))
                ArtistImageUrl = overview.ImageUrl ?? ArtistImageUrl;
            HeaderImageUrl = overview.HeaderImageUrl;
            HeaderHeroColorHex = overview.HeroColorHex;
            Palette = overview.Palette;
            MonthlyListeners = overview.MonthlyListeners > 0
                ? overview.MonthlyListeners.ToString("N0")
                : null;
            WorldRank = overview.WorldRank;
            Followers = overview.Followers;
            Biography = overview.Biography;
            IsVerified = overview.IsVerified;

            // ── Latest release ──
            // Always overwrite — the BehaviorSubject can emit multiple times
            // for the same artist (cached stale → fresh) and a previous emit
            // may have populated these fields with a release that the latest
            // emit no longer has. Without explicit null-out the card stays
            // stuck on the prior emit's release. Same when navigating to an
            // artist with no LatestRelease at all.
            LatestReleaseName = overview.LatestRelease?.Name;
            LatestReleaseImageUrl = overview.LatestRelease?.ImageUrl;
            LatestReleaseUri = overview.LatestRelease?.Uri;
            LatestReleaseType = overview.LatestRelease?.Type;
            LatestReleaseTrackCount = overview.LatestRelease?.TrackCount ?? 0;
            LatestReleaseDate = overview.LatestRelease?.FormattedDate;

            // ── Top tracks (batch to avoid N+1 CollectionChanged events) ──
            var newTracks = new ObservableCollection<LazyTrackItem>();
            // Populate the music-video catalog cache as we map top tracks.
            // Avoids a redundant NPV roundtrip when the user clicks a track
            // they've already seen on this artist page.
            var videoMetadata = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<Wavee.UI.WinUI.Services.IMusicVideoMetadataService>();
            _logger?.LogInformation("[VideoCache] ArtistViewModel populating cache with {Count} top tracks (cacheResolved={HasCache})",
                overview.TopTracks.Count, videoMetadata is not null);
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
                    AlbumImageUrl = track.AlbumImageUrl,
                    AlbumUri = track.AlbumUri,
                    Duration = track.Duration,
                    PlayCountRaw = track.PlayCount,
                    ArtistNames = track.ArtistNames,
                    IsExplicit = track.IsExplicit,
                    IsPlayable = track.IsPlayable,
                    HasVideo = track.HasVideo
                };
                newTracks.Add(LazyTrackItem.Loaded(trackVm.Id, idx, trackVm));
                if (videoMetadata is not null && !string.IsNullOrEmpty(track.Uri))
                {
                    videoMetadata.NoteHasVideo(track.Uri, track.HasVideo);
                    _logger?.LogDebug("[VideoCache]   {Uri} → hasVideo={HasVideo}", track.Uri, track.HasVideo);
                }
                idx++;
            }

            if (videoMetadata is not null)
            {
                foreach (var mapping in overview.MusicVideoMappings)
                {
                    videoMetadata.NoteVideoUri(mapping.AudioTrackUri, mapping.VideoTrackUri);
                    _logger?.LogDebug("[VideoCache]   {AudioUri} -> {VideoUri}",
                        mapping.AudioTrackUri, mapping.VideoTrackUri);
                }
            }

            // Pad + shimmer placeholders
            var loadedCount = idx - 1;
            var pageSize = TracksPerPage > 0 ? TracksPerPage : 12;
            var remainder = loadedCount % pageSize;
            var padCount = remainder > 0 ? pageSize - remainder : 0;
            for (int i = 0; i < padCount + pageSize; i++)
            {
                newTracks.Add(LazyTrackItem.Placeholder($"placeholder-{idx}", idx));
                idx++;
            }
            TopTracks = newTracks;

            // ── Backfill missing cover art (background, parallel) ──
            // Spotify's getArtistOverview GraphQL response is inconsistent: many
            // tracks come back without albumOfTrack.coverArt populated. Resolve
            // them via the extended-metadata pipeline and patch the VMs.

            // ── Extended top tracks (background, parallel) ──

            // ── Releases ──
            _allReleases.Clear();
            AddReleasesToList(overview.Albums, "ALBUM", "album-ph", overview.AlbumsTotalCount);
            AddReleasesToList(overview.Singles, "SINGLE", "single-ph", overview.SinglesTotalCount);
            AddReleasesToList(overview.Compilations, "COMPILATION", "comp-ph", overview.CompilationsTotalCount);
            DispatchReleases();

            AlbumsTotalCount = overview.AlbumsTotalCount;
            SinglesTotalCount = overview.SinglesTotalCount;
            CompilationsTotalCount = overview.CompilationsTotalCount;

            // ── Background discography pagination ──

            // ── Related artists (batch swap) ──
            RelatedArtists = overview.RelatedArtists.Select(ra => new RelatedArtistVm
            {
                Id = ra.Id,
                Uri = ra.Uri,
                Name = ra.Name,
                ImageUrl = ra.ImageUrl
            }).ToList();

            // ── Concerts (batch swap) ──
            Concerts = overview.Concerts.Select(c => new ConcertVm
            {
                Title = c.Title,
                Venue = c.Venue,
                City = c.City,
                DateFormatted = c.Date != default
                    ? c.Date.ToString("MMM d").ToUpperInvariant()
                    : "",
                DayOfWeek = c.Date != default
                    ? c.Date.ToString("ddd").ToUpperInvariant()
                    : "",
                Year = c.Date != default ? c.Date.Year.ToString() : "",
                IsFestival = c.IsFestival,
                IsNearUser = c.IsNearUser,
                Uri = c.Uri
            }).ToList();

            UserLocationName = _locationService.CurrentCity;

            PinnedItem = overview.PinnedItem;
            WatchFeed = overview.WatchFeed;
            OnPropertyChanged(nameof(HasPinnedItem));
            OnPropertyChanged(nameof(HasWatchFeed));

            // ── Connect & Markets + Gallery (batch swap) ──
            ExternalLinks = overview.ExternalLinks.Select(l => new ArtistSocialLinkVm
            {
                Name = l.Name,
                Url = l.Url,
                Icon = Wavee.UI.WinUI.Styles.FluentGlyphs.ResolveSocialIcon(l.Url, l.Name)
            }).ToList();

            // Bar widths normalized against the largest city's listener count.
            var maxListeners = overview.TopCities.Count == 0
                ? 1L
                : overview.TopCities.Max(c => c.NumberOfListeners);
            TopCities = overview.TopCities.Take(5).Select(c => new ArtistTopCityVm
            {
                City = c.City,
                Country = c.Country,
                NumberOfListeners = c.NumberOfListeners,
                DisplayCount = FormatListenerCount(c.NumberOfListeners),
                RelativeWidth = maxListeners > 0
                    ? Math.Max(8, c.NumberOfListeners * 200.0 / maxListeners)
                    : 8
            }).ToList();

            GalleryPhotos = overview.GalleryPhotos.ToList();

            CurrentPage = 0;
            NotifyPaginationChanged();
            _ = StartDeferredArtistWorkAsync(artistId, generation, overview);
        }
        catch (SessionException)
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

    // ── Mapping helpers ──

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

        _ = EnrichMissingTopTrackImagesAsync(artistId, generation);
        _ = LoadExtendedTopTracksAsync(artistId, generation);

        var releasesSnapshot = _allReleases
            .Where(item => item.IsLoaded && item.Data != null)
            .Select(item => item.Data!)
            .ToList();
        _ = PrefetchReleaseColorsAsync(artistId, generation, releasesSnapshot);

        var discoToken = CreateFreshDiscographyToken();
        _ = Task.Run(() => FetchRemainingDiscographyAsync(
            artistId, generation,
            overview.Albums.Count, overview.AlbumsTotalCount,
            overview.Singles.Count, overview.SinglesTotalCount,
            overview.Compilations.Count, overview.CompilationsTotalCount,
            discoToken), discoToken);
    }

    private void AddReleasesToList(
        List<ArtistReleaseResult> releases,
        string type,
        string phPrefix,
        int totalCount)
    {
        int count = 0;
        foreach (var r in releases)
        {
            var vm = new ArtistReleaseVm
            {
                Id = r.Id,
                Uri = r.Uri,
                Name = r.Name,
                Type = type,
                ImageUrl = r.ImageUrl,
                ReleaseDate = r.ReleaseDate,
                TrackCount = r.TrackCount,
                Label = r.Label,
                Year = r.Year
            };
            _allReleases.Add(LazyReleaseItem.Loaded(vm.Id, count, vm));
            count++;
        }

        var maxPlaceholders = Math.Min(totalCount - count, 20);
        for (int i = count; i < count + maxPlaceholders; i++)
            _allReleases.Add(LazyReleaseItem.Placeholder($"{phPrefix}-{i}", i));
    }

    private async Task PrefetchReleaseColorsAsync(
        string artistId,
        int generation,
        IEnumerable<ArtistReleaseVm> releases)
    {
        var releasesByUrl = new Dictionary<string, List<ArtistReleaseVm>>(StringComparer.Ordinal);

        foreach (var release in releases)
        {
            if (!string.IsNullOrEmpty(release.ColorHex))
                continue;

            var imageUrl = SpotifyImageHelper.ToHttpsUrl(release.ImageUrl) ?? release.ImageUrl;
            if (string.IsNullOrWhiteSpace(imageUrl))
                continue;

            if (!releasesByUrl.TryGetValue(imageUrl, out var mapped))
            {
                mapped = [];
                releasesByUrl[imageUrl] = mapped;
            }

            mapped.Add(release);
        }

        if (releasesByUrl.Count == 0)
            return;

        try
        {
            var colors = await _colorService
                .GetColorsAsync(releasesByUrl.Keys.ToList())
                .ConfigureAwait(false);

            if (colors.Count == 0)
                return;

            if (!IsCurrentLoad(artistId, generation))
                return;

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!IsCurrentLoad(artistId, generation))
                    return;

                foreach (var (url, mappedReleases) in releasesByUrl)
                {
                    if (!colors.TryGetValue(url, out var color))
                        continue;

                    var hex = color.DarkHex ?? color.RawHex ?? color.LightHex;
                    if (string.IsNullOrEmpty(hex))
                        continue;

                    foreach (var release in mappedReleases)
                    {
                        if (string.IsNullOrEmpty(release.ColorHex))
                            release.ColorHex = hex;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to prefetch artist release colors for {Count} images", releasesByUrl.Count);
        }
    }

    // ── Background discography pagination ──

    private async Task FetchRemainingDiscographyAsync(
        string artistUri,
        int generation,
        int albumsLoaded, int albumsTotal,
        int singlesLoaded, int singlesTotal,
        int compilationsLoaded, int compilationsTotal,
        CancellationToken ct)
    {
        var tasks = new List<Task>();

        if (albumsLoaded < albumsTotal)
            tasks.Add(FetchDiscographyGroupAsync(artistUri, generation,
                "ALBUM", "album-ph", albumsLoaded, albumsTotal, ct));

        if (singlesLoaded < singlesTotal)
            tasks.Add(FetchDiscographyGroupAsync(artistUri, generation,
                "SINGLE", "single-ph", singlesLoaded, singlesTotal, ct));

        if (compilationsLoaded < compilationsTotal)
            tasks.Add(FetchDiscographyGroupAsync(artistUri, generation,
                "COMPILATION", "comp-ph", compilationsLoaded, compilationsTotal, ct));

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    // ── Album expand/collapse ──

    [RelayCommand]
    private async Task ExpandAlbum(LazyReleaseItem? album)
    {
        if (album == null || !album.IsLoaded || album.Data == null) return;

        if (ExpandedAlbum?.Id == album.Id)
        {
            CollapseAlbum();
            return;
        }

        ExpandedAlbum = album;
        IsLoadingExpandedTracks = true;
        ExpandedAlbumTracks.Clear();

        var trackCount = album.Data.TrackCount;
        if (trackCount <= 0)
        {
            trackCount = album.Data.Type switch
            {
                "SINGLE" => 2,
                "COMPILATION" => 20,
                _ => 12
            };
        }

        for (int i = 0; i < trackCount; i++)
            ExpandedAlbumTracks.Add(LazyTrackItem.Placeholder($"expanded-{i}", i + 1));

        try
        {
            var albumUri = album.Data.Uri ?? $"spotify:album:{album.Data.Id}";
            var tracks = await _albumService.GetTracksAsync(albumUri);

            for (int i = 0; i < Math.Min(tracks.Count, ExpandedAlbumTracks.Count); i++)
                ExpandedAlbumTracks[i] = LazyTrackItem.Loaded(tracks[i].Id, i + 1, tracks[i]);

            while (ExpandedAlbumTracks.Count > tracks.Count)
                ExpandedAlbumTracks.RemoveAt(ExpandedAlbumTracks.Count - 1);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load album tracks for {AlbumUri}", album.Data.Uri);
        }
        finally
        {
            IsLoadingExpandedTracks = false;
        }
    }

    [RelayCommand]
    private void CollapseAlbum()
    {
        ExpandedAlbum = null;
        IsLoadingExpandedTracks = false;
    }

    // ── Background discography pagination ──

    private async Task FetchDiscographyGroupAsync(
        string artistUri,
        int generation,
        string type,
        string phPrefix,
        int alreadyLoaded,
        int totalCount,
        CancellationToken ct)
    {
        try
        {
            const int pageSz = 20;
            var offset = alreadyLoaded;

            var allReleases = new List<(int Offset, List<ArtistReleaseResult> Items)>();
            while (offset < totalCount)
            {
                ct.ThrowIfCancellationRequested();
                var releases = await _artistService.GetDiscographyPageAsync(artistUri, type, offset, pageSz, ct);
                if (releases.Count == 0) break;
                allReleases.Add((offset, releases));
                offset += releases.Count;
            }

            if (allReleases.Count == 0) return;
            if (ct.IsCancellationRequested || !IsCurrentLoad(artistUri, generation))
                return;

            var createdReleaseVms = new List<ArtistReleaseVm>();
            var tcs = new TaskCompletionSource();
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (ct.IsCancellationRequested || !IsCurrentLoad(artistUri, generation))
                    {
                        tcs.SetResult();
                        return;
                    }

                    foreach (var (pageOffset, releases) in allReleases)
                    {
                        int i = pageOffset;
                        foreach (var r in releases)
                        {
                            var vm = new ArtistReleaseVm
                            {
                                Id = r.Id,
                                Uri = r.Uri,
                                Name = r.Name,
                                Type = type,
                                ImageUrl = r.ImageUrl,
                                ReleaseDate = r.ReleaseDate,
                                TrackCount = r.TrackCount,
                                Label = r.Label,
                                Year = r.Year
                            };
                            createdReleaseVms.Add(vm);

                            var phKey = $"{phPrefix}-{i}";
                            var existing = _allReleases.FirstOrDefault(x => x.Id == phKey);
                            if (existing != null)
                                existing.Populate(vm);
                            else
                                _allReleases.Add(LazyReleaseItem.Loaded(r.Id, i, vm));
                            i++;
                        }
                    }
                    DispatchReleases();
                    tcs.SetResult();
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            await tcs.Task;
            if (IsCurrentLoad(artistUri, generation))
                _ = PrefetchReleaseColorsAsync(artistUri, generation, createdReleaseVms);
        }
        catch (OperationCanceledException) { /* navigated away */ }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Discography {Type} fetch failed for {ArtistId}", type, artistUri);

            var tcsCleanup = new TaskCompletionSource();
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (!IsCurrentLoad(artistUri, generation))
                    {
                        tcsCleanup.SetResult();
                        return;
                    }

                    _allReleases.RemoveAll(i => !i.IsLoaded && i.Id.StartsWith(phPrefix));
                    DispatchReleases();

                    switch (type)
                    {
                        case "ALBUM": HasAlbumsError = true; break;
                        case "SINGLE": HasSinglesError = true; break;
                        case "COMPILATION": HasCompilationsError = true; break;
                    }

                    tcsCleanup.SetResult();
                }
                catch (Exception cleanupEx) { tcsCleanup.SetException(cleanupEx); }
            });

            try { await tcsCleanup.Task; }
            catch (Exception cleanupEx2) { _logger?.LogDebug(cleanupEx2, "Discography cleanup failed (non-critical)"); }
        }
    }

    // ── Commands ──

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
    private async Task RetryDiscographyAsync()
    {
        var albumsLoaded = Albums.Count(a => a.IsLoaded);
        var singlesLoaded = Singles.Count(s => s.IsLoaded);
        var compilationsLoaded = Compilations.Count(c => c.IsLoaded);

        HasAlbumsError = false;
        HasSinglesError = false;
        HasCompilationsError = false;

        var artistId = ArtistId;
        if (string.IsNullOrEmpty(artistId))
            return;

        var generation = Volatile.Read(ref _loadGeneration);
        var ct = CreateFreshDiscographyToken();

        await Task.Run(() => FetchRemainingDiscographyAsync(
            artistId, generation,
            albumsLoaded, AlbumsTotalCount,
            singlesLoaded, SinglesTotalCount,
            compilationsLoaded, CompilationsTotalCount,
            ct), ct);
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
            _dispatcherQueue.TryEnqueue(SyncArtistPlaybackState);
        }
    }

    private void SyncArtistPlaybackState()
    {
        bool isArtistContext = IsArtistContextActive();
        IsArtistContextPlaying = isArtistContext && _playbackStateService.IsPlaying;
        IsArtistContextPaused = isArtistContext && !_playbackStateService.IsPlaying;

        if (IsPlayPending && (!isArtistContext || IsArtistContextPlaying))
            SetPlayPending(false);
    }

    private bool IsArtistContextActive()
    {
        var artistId = ArtistId;
        var contextUri = _playbackStateService.CurrentContext?.ContextUri;
        if (string.IsNullOrWhiteSpace(artistId) || string.IsNullOrWhiteSpace(contextUri))
            return false;

        var canonicalArtistUri = artistId.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)
            ? artistId
            : $"spotify:artist:{artistId}";

        return string.Equals(contextUri, artistId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(contextUri, canonicalArtistUri, StringComparison.OrdinalIgnoreCase);
    }

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
        _ = ClearPlayPendingAfterTimeoutAsync(_playPendingCts.Token);
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

    [RelayCommand]
    private async Task PlayTopTracksAsync()
    {
        if (string.IsNullOrEmpty(ArtistId)) return;

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
                ArtistId,
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
        if (track == null || string.IsNullOrEmpty(ArtistId)) return;

        // Build rich QueueItems from TopTracks so remote clients receive per-track
        // uid + metadata (artist_uri, album_uri, album_title, title, track_player)
        // the same way Spotify desktop does. Without this, the published queue
        // comes across as bare track URIs with context_uri="spotify:internal:queue".
        // Mirrors PlaylistViewModel.BuildQueueAndPlay.
        var queueItems = new List<QueueItem>(TopTracks.Count);
        int startIndex = -1;
        foreach (var t in TopTracks)
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
            });
        }

        if (queueItems.Count == 0 || startIndex < 0)
        {
            // Clicked track isn't in the local TopTracks cache — fall back to
            // server-side context resolution.
            await _playbackService.PlayTrackInContextAsync(track.Uri, ArtistId,
                new PlayContextOptions { PlayOriginFeature = "artist_page" });
            return;
        }

        var context = new PlaybackContextInfo
        {
            ContextUri = ArtistId,
            Type = PlaybackContextType.Artist,
            Name = ArtistName,
            ImageUrl = ArtistImageUrl,
            // Matches context-resolve/v1/spotify:artist:{id}.metadata. Forwarded
            // into PlayerState.context_metadata so other clients render
            // "Playing from {artist}" correctly.
            FormatAttributes = new Dictionary<string, string>
            {
                ["context_description"] = ArtistName ?? string.Empty,
                ["artist_context_type"] = "km_artist",
            }
        };

        _playbackStateService.LoadQueue(queueItems, context, startIndex);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void ToggleAlbumsView() => AlbumsGridView = !AlbumsGridView;

    [RelayCommand]
    private void ToggleSinglesView() => SinglesGridView = !SinglesGridView;

    [RelayCommand]
    private void ToggleCompilationsView() => CompilationsGridView = !CompilationsGridView;

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

    // ── Pagination notifications ──

    private void NotifyPaginationChanged()
    {
        _pagedTopTracksCache = null;
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(HasMultiplePages));
        OnPropertyChanged(nameof(PagedTopTracks));
    }

    partial void OnCurrentPageChanged(int value)
    {
        ClearTopTracksSelection();
        NotifyPaginationChanged();
    }

    partial void OnColumnCountChanged(int value)
    {
        CurrentPage = 0;
        ClearTopTracksSelection();
        NotifyPaginationChanged();
    }

    partial void OnTopTracksChanged(ObservableCollection<LazyTrackItem> value) => NotifyPaginationChanged();

    partial void OnArtistNameChanged(string? value)
    {
        if (TabItemParameter != null && !string.IsNullOrEmpty(value))
        {
            TabItemParameter.Title = value;
            ContentChanged?.Invoke(this, TabItemParameter);
        }
    }

    // ── Extended top tracks ──

    /// <summary>
    /// Backfills <see cref="ArtistTopTrackVm.AlbumImageUrl"/> for any
    /// initial top tracks the GraphQL overview returned without cover art.
    /// Resolves the missing URLs via the extended-metadata pipeline (cache
    /// + batched TrackV4 fetch) and replaces the affected
    /// <see cref="LazyTrackItem"/> entries so <c>TrackItem</c> picks up
    /// the new <c>Track.ImageUrl</c> on the next layout pass.
    /// </summary>
    private async Task EnrichMissingTopTrackImagesAsync(string artistId, int generation)
    {
        try
        {
            if (!IsCurrentLoad(artistId, generation))
                return;

            // Snapshot URIs needing enrichment (called off-dispatcher right
            // after TopTracks is replaced — safe to read without a lock).
            var snapshot = TopTracks.ToList();
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
            if (!IsCurrentLoad(artistId, generation)) return;

            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (!IsCurrentLoad(artistId, generation))
                    return;

                bool anyPatched = false;
                for (int i = 0; i < TopTracks.Count; i++)
                {
                    var entry = TopTracks[i];
                    if (!entry.IsLoaded || entry.Data is not ArtistTopTrackVm vm) continue;
                    if (vm.Uri is not { Length: > 0 } uri) continue;
                    if (!string.IsNullOrEmpty(vm.AlbumImageUrl)) continue;
                    if (!images.TryGetValue(uri, out var imageUrl) || string.IsNullOrEmpty(imageUrl)) continue;

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
                        HasVideo = vm.HasVideo,
                    };
                    entry.Populate(patched);
                    anyPatched = true;
                }

                if (anyPatched)
                {
                    _pagedTopTracksCache = null;
                    OnPropertyChanged(nameof(PagedTopTracks));
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich missing top-track images");
        }
    }

    private async Task LoadExtendedTopTracksAsync(string artistUri, int generation)
    {
        try
        {
            var extendedTracks = await Task.Run(async () => await _artistService.GetExtendedTopTracksAsync(artistUri));
            if (extendedTracks.Count == 0) return;
            if (!IsCurrentLoad(artistUri, generation)) return;

            var existingUris = new HashSet<string>(
                TopTracks
                    .Where(i => i.IsLoaded && i.Data != null)
                    .Select(i => ((ArtistTopTrackVm)i.Data!).Uri ?? ""));

            var startIdx = TopTracks.Count(i => i.IsLoaded) + 1;

            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (!IsCurrentLoad(artistUri, generation))
                    return;

                // Remove all placeholder items
                for (int i = TopTracks.Count - 1; i >= 0; i--)
                {
                    if (!TopTracks[i].IsLoaded)
                        TopTracks.RemoveAt(i);
                }

                int idx = startIdx;
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
                        HasVideo = track.HasVideo
                    };

                    TopTracks.Add(LazyTrackItem.Loaded(trackVm.Id, idx, trackVm));
                    idx++;
                }

                _pagedTopTracksCache = null;
                OnPropertyChanged(nameof(TotalPages));
                OnPropertyChanged(nameof(HasMultiplePages));
                OnPropertyChanged(nameof(PagedTopTracks));
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load extended top tracks for {Artist}", artistUri);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DetachLongLivedServices();

        _subscriptions?.Dispose();
        _subscriptions = null;

        _playPendingCts?.Cancel();
        _playPendingCts?.Dispose();
        _playPendingCts = null;

        CancelAndDisposeDiscographyCts();
    }

    /// <summary>
    /// Theme-aware palette refresh. Page calls this on init + on
    /// ActualThemeChanged + after Palette lands. Mirrors PlaylistViewModel
    /// and AlbumViewModel: dark theme → HigherContrast (deepest), light →
    /// HighContrast (saturated but a step brighter). MinContrast is skipped —
    /// too pastel for white-on-tint text. When no palette is available the
    /// brushes are nulled so bound elements render untinted.
    /// </summary>
    public void ApplyTheme(bool isDark)
    {
        _isDarkTheme = isDark;

        var tier = Palette is null
            ? null
            : (isDark
                ? (Palette.HigherContrast ?? Palette.HighContrast)
                : (Palette.HighContrast ?? Palette.HigherContrast));

        if (tier == null)
        {
            // Fall back to system accent when no palette is available so the
            // Play button + avatar ring still render correctly on cold load.
            SectionAccentBrush = ResolveSystemBrush("AccentFillColorDefaultBrush");
            PaletteHeroGradientBrush = null;
            PaletteAccentPillBrush = ResolveSystemBrush("AccentFillColorDefaultBrush");
            PaletteAccentPillForegroundBrush = ResolveSystemBrush("TextOnAccentFillColorPrimaryBrush");
            return;
        }

        var bg = Color.FromArgb(255, tier.BackgroundR, tier.BackgroundG, tier.BackgroundB);
        var bgTint = Color.FromArgb(255, tier.BackgroundTintedR, tier.BackgroundTintedG, tier.BackgroundTintedB);

        // Use BackgroundTinted (the artist's actual cover-derived color) for
        // accents instead of TextAccent. TextAccent often resolves to Spotify's
        // brand green (#1DB954) regardless of the cover photo, which made every
        // artist accent look identical (and disconnected from the visual).
        var accentBase = TintColorHelper.BrightenForTint(bgTint, targetMax: 210);

        // Section bar — full-alpha lifted accent, matches Home AccentLineBrush.
        SectionAccentBrush = new SolidColorBrush(Color.FromArgb(255, accentBase.R, accentBase.G, accentBase.B));

        // Hero scrim — same alpha cadence used by AlbumViewModel/PlaylistViewModel.
        var heroGrad = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
        };
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(240, bgTint.R, bgTint.G, bgTint.B), Offset = 0.0 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(176, bg.R, bg.G, bg.B),         Offset = 0.35 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(80,  bg.R, bg.G, bg.B),         Offset = 0.65 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0,   bg.R, bg.G, bg.B),         Offset = 1.0 });
        PaletteHeroGradientBrush = heroGrad;

        // Play button — same lifted accent as the section bar so the page
        // reads as one color identity, with luma-based contrast text.
        PaletteAccentPillBrush = new SolidColorBrush(accentBase);
        var accentLuma = (accentBase.R * 299 + accentBase.G * 587 + accentBase.B * 114) / 1000;
        PaletteAccentPillForegroundBrush = new SolidColorBrush(
            accentLuma > 160 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255));
    }

    partial void OnPaletteChanged(ArtistPalette? value) => ApplyTheme(_isDarkTheme);

    private static Brush? ResolveSystemBrush(string resourceKey)
    {
        if (Microsoft.UI.Xaml.Application.Current?.Resources is { } res
            && res.TryGetValue(resourceKey, out var value)
            && value is Brush brush)
            return brush;
        return null;
    }

    /// <summary>
    /// Formats a long listener count like "1.2M" / "453K" / "812".
    /// Mirrors monthly-listener formatting used elsewhere in the app.
    /// </summary>
    private static string FormatListenerCount(long count)
    {
        if (count >= 1_000_000)
            return (count / 1_000_000.0).ToString("0.#") + "M";
        if (count >= 1_000)
            return (count / 1_000.0).ToString("0.#") + "K";
        return count.ToString("N0");
    }
}

public sealed record ArtistSocialLinkVm
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public required FontAwesome6.EFontAwesomeIcon Icon { get; init; }
}

public sealed record ArtistTopCityVm
{
    public required string City { get; init; }
    public string? Country { get; init; }
    public long NumberOfListeners { get; init; }
    public required string DisplayCount { get; init; }
    public double RelativeWidth { get; init; }
}

// ── View Models (UI-layer records) ──

public sealed class ArtistTopTrackVm : Data.Contracts.ITrackItem
{
    public required string Id { get; init; }
    public int Index { get; set; }
    public string? Uri { get; init; }
    public string? AlbumImageUrl { get; init; }
    public string? AlbumUri { get; init; }
    public long PlayCountRaw { get; init; }
    public bool IsPlayable { get; init; }
    public bool HasVideo { get; init; }

    // ── ITrackItem implementation ──
    string Data.Contracts.ITrackItem.Uri => Uri ?? $"spotify:track:{Id}";
    string Data.Contracts.ITrackItem.Title => Title ?? "";
    string Data.Contracts.ITrackItem.ArtistName =>
        PlayCountRaw > 0 ? PlayCountFormatted : (ArtistNames ?? "");
    string Data.Contracts.ITrackItem.ArtistId => "";
    string Data.Contracts.ITrackItem.AlbumName => AlbumName ?? "";
    string Data.Contracts.ITrackItem.AlbumId => AlbumUri ?? "";
    string? Data.Contracts.ITrackItem.ImageUrl => AlbumImageUrl;
    TimeSpan Data.Contracts.ITrackItem.Duration => Duration;
    bool Data.Contracts.ITrackItem.IsExplicit => IsExplicit;
    string Data.Contracts.ITrackItem.DurationFormatted => DurationFormatted;
    int Data.Contracts.ITrackItem.OriginalIndex => Index;
    bool Data.Contracts.ITrackItem.IsLoaded => true;
    bool Data.Contracts.ITrackItem.HasVideo => HasVideo;

    // ── Public properties ──
    public string? Title { get; init; }
    public string? AlbumName { get; init; }
    public string? ArtistNames { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsExplicit { get; init; }

    public string PlayCountFormatted => PlayCountRaw.ToString("N0");

    public string DurationFormatted =>
        Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");

    private bool _isLiked;
    public bool IsLiked
    {
        get => _isLiked;
        set => SetField(ref _isLiked, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public sealed partial class ArtistReleaseVm : ObservableObject
{
    public string Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string Type { get; init; } // ALBUM, SINGLE, COMPILATION
    public string? ImageUrl { get; init; }
    public DateTimeOffset ReleaseDate { get; init; }
    public int TrackCount { get; init; }
    public string? Label { get; init; }
    public int Year { get; init; }

    [ObservableProperty]
    private string? _colorHex;
}

public sealed class RelatedArtistVm
{
    public string? Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
}

public sealed class ConcertVm : INotifyPropertyChanged
{
    public string? Title { get; init; }
    public string? Venue { get; init; }
    public string? City { get; init; }
    public string? DateFormatted { get; init; }
    public string? DayOfWeek { get; init; }
    public string? Year { get; init; }
    public bool IsFestival { get; init; }
    public string? Uri { get; init; }

    private bool _isNearUser;
    public bool IsNearUser
    {
        get => _isNearUser;
        set
        {
            if (_isNearUser == value) return;
            _isNearUser = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNearUser)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LocationSearchResultVm
{
    public string? GeonameId { get; init; }
    public string? CityName { get; init; }
    public string? CountryName { get; init; }
    public string DisplayName => $"{CityName}, {CountryName}";
}
