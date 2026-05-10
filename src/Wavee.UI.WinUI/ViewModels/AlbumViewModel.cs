using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using ReactiveUI;
using Windows.UI;
using Wavee.Core.Data;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Controls.AvatarStack;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Data.Stores;
using Wavee.UI.WinUI.Diagnostics;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Sort column options for album tracks.
/// </summary>
public enum AlbumSortColumn { Title, Artist, TrackNumber }

/// <summary>
/// ViewModel for the Album detail page.
/// Album tracks are static after load — no reactive pipeline needed.
/// </summary>
public sealed partial class AlbumViewModel : ReactiveObject, ITrackListViewModel, ITabBarItemContent, IDisposable
{
    private readonly IAlbumService _albumService;
    private readonly AlbumStore _albumStore;
    private CompositeDisposable? _subscriptions;
    private string? _appliedDetailFor;
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ITrackLikeService? _likeService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;
    private bool _disposed;

    /// <summary>All loaded tracks (unfiltered). Null until loaded.</summary>
    private List<LazyTrackItem> _allTracks = [];
    private HashSet<string> _popularTrackIds = new(StringComparer.Ordinal);

    private string _albumId = "";
    private string _albumName = "";
    private string? _albumImageUrl;
    private string? _colorHex;
    private string _artistId = "";
    private string _artistName = "";
    private string? _artistImageUrl;
    private int _year;
    private string? _albumType;
    private bool _isSaved;
    private bool _isLoading;
    private bool _isLoadingTracks;
    private bool _hasError;
    private string? _errorMessage;
    private string? _label;
    private string? _releaseDateFormatted;
    private string? _copyrightsText;

    private string _searchQuery = "";
    private AlbumSortColumn _currentSortColumn = AlbumSortColumn.TrackNumber;
    private bool _isSortDescending = false;
    private int _totalTracks;
    private string _totalDuration = "";
    private IReadOnlyList<object> _selectedItems = Array.Empty<object>();
    private readonly ObservableCollection<PlaylistSummaryDto> _playlists = [];
    // Bound collections kept as stable instances and mutated in place. Assigning
    // a new list reference here forces ItemsRepeater/ListView to recycle every
    // realized container; mutating the same instance lets the binding stay
    // subscribed and avoids a full rebuild on cached-page restore.
    private readonly ObservableCollection<AlbumRelatedResult> _moreByArtist = [];
    private readonly ObservableCollection<AlbumMerchItemResult> _merchItems = [];

    // Multi-artist surfaces (header AvatarStack + flyout). _artists is the
    // album-billed list (drives the avatars), _allDistinctArtists is billed
    // followed by track-only artists deduped by URI (drives the flyout).
    private IReadOnlyList<AlbumArtistResult> _artists = [];
    private IReadOnlyList<AlbumArtistResult> _allDistinctArtists = [];
    private IReadOnlyList<AvatarStackItem> _artistAvatarItems = [];
    private IReadOnlyList<HeaderArtistLink> _headerArtistLinks = [];
    private int _overflowArtistCount;

    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    /// <summary>
    /// The album ID.
    /// </summary>
    public string AlbumId
    {
        get => _albumId;
        private set => this.RaiseAndSetIfChanged(ref _albumId, value);
    }

    /// <summary>
    /// The album name.
    /// </summary>
    public string AlbumName
    {
        get => _albumName;
        private set
        {
            this.RaiseAndSetIfChanged(ref _albumName, value);
            UpdateTabTitle();
        }
    }

    /// <summary>
    /// The album cover image URL.
    /// </summary>
    public string? AlbumImageUrl
    {
        get => _albumImageUrl;
        private set => this.RaiseAndSetIfChanged(ref _albumImageUrl, value);
    }

    /// <summary>
    /// Extracted dark color from the album cover art, as a hex string.
    /// Used as a tint for track placeholder backgrounds while album art loads.
    /// </summary>
    public string? ColorHex
    {
        get => _colorHex;
        private set => this.RaiseAndSetIfChanged(ref _colorHex, value);
    }

    /// <summary>
    /// The primary artist ID.
    /// </summary>
    public string ArtistId
    {
        get => _artistId;
        private set => this.RaiseAndSetIfChanged(ref _artistId, value);
    }

    /// <summary>
    /// The primary artist name.
    /// </summary>
    public string ArtistName
    {
        get => _artistName;
        private set => this.RaiseAndSetIfChanged(ref _artistName, value);
    }

    /// <summary>
    /// The primary artist's avatar image URL, surfaced from the album's
    /// <c>artists.items[0].visuals.avatarImage</c> so the page can render a small
    /// circular thumbnail next to the artist name without a second fetch.
    /// </summary>
    public string? ArtistImageUrl
    {
        get => _artistImageUrl;
        private set => this.RaiseAndSetIfChanged(ref _artistImageUrl, value);
    }

    /// <summary>
    /// Album-billed artists (from <c>albumUnion.artists.items</c>). Drives the
    /// stacked-avatar strip in the header and the inline names line below it.
    /// </summary>
    public IReadOnlyList<AlbumArtistResult> Artists
    {
        get => _artists;
        private set => this.RaiseAndSetIfChanged(ref _artists, value);
    }

    /// <summary>
    /// Every distinct artist on the album: billed artists first, then track-only
    /// contributors deduped by URI. Drives the artists Flyout opened from the
    /// avatar stack so users can navigate to featured guests not in the billing.
    /// </summary>
    public IReadOnlyList<AlbumArtistResult> AllDistinctArtists
    {
        get => _allDistinctArtists;
        private set
        {
            this.RaiseAndSetIfChanged(ref _allDistinctArtists, value);
            this.RaisePropertyChanged(nameof(HasMultipleArtists));
        }
    }

    /// <summary>
    /// Avatar items for the header <c>AvatarStack</c>, projected from
    /// <see cref="Artists"/>. Pre-projected here so the XAML can bind directly
    /// without a converter per page.
    /// </summary>
    public IReadOnlyList<AvatarStackItem> ArtistAvatarItems
    {
        get => _artistAvatarItems;
        private set => this.RaiseAndSetIfChanged(ref _artistAvatarItems, value);
    }

    /// <summary>
    /// Number of additional distinct artists beyond <see cref="Artists"/>.
    /// Drives the trailing <c>+N</c> badge on the avatar stack — non-zero when
    /// the album has track-only contributors that aren't in the billing.
    /// </summary>
    public int OverflowArtistCount
    {
        get => _overflowArtistCount;
        private set => this.RaiseAndSetIfChanged(ref _overflowArtistCount, value);
    }

    /// <summary>
    /// Per-name link projections for the header artists line. Each entry
    /// carries <c>IsFirst</c> so the comma separator preceding the entry can
    /// hide on item 0 without page-side index logic.
    /// </summary>
    public IReadOnlyList<HeaderArtistLink> HeaderArtistLinks
    {
        get => _headerArtistLinks;
        private set => this.RaiseAndSetIfChanged(ref _headerArtistLinks, value);
    }

    /// <summary>
    /// True when the album has more than one distinct artist anywhere
    /// (billed + per-track unioned). Soundtracks, compilations, and any
    /// album with featured guests are detected here. Drives the per-row
    /// artist column on the album track grid — the default suppresses it on
    /// album pages because most albums are single-artist, but for collabs
    /// the artist names are essential context.
    /// </summary>
    public bool HasMultipleArtists => _allDistinctArtists.Count > 1;

    /// <summary>
    /// Release year.
    /// </summary>
    public int Year
    {
        get => _year;
        private set => this.RaiseAndSetIfChanged(ref _year, value);
    }

    /// <summary>
    /// Album type (Album, Single, EP, etc.).
    /// </summary>
    public string? AlbumType
    {
        get => _albumType;
        private set => this.RaiseAndSetIfChanged(ref _albumType, value);
    }

    public string? Label
    {
        get => _label;
        private set => this.RaiseAndSetIfChanged(ref _label, value);
    }

    public string? ReleaseDateFormatted
    {
        get => _releaseDateFormatted;
        private set => this.RaiseAndSetIfChanged(ref _releaseDateFormatted, value);
    }

    public string? CopyrightsText
    {
        get => _copyrightsText;
        private set => this.RaiseAndSetIfChanged(ref _copyrightsText, value);
    }

    /// <summary>
    /// Whether the album is saved to the user's library.
    /// </summary>
    public bool IsSaved
    {
        get => _isSaved;
        set => this.RaiseAndSetIfChanged(ref _isSaved, value);
    }

    /// <summary>
    /// Loading state for initial page load.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            var changed = _isLoading != value;
            _logger?.LogDebug(
                "[xfade][album-vm:{Id}] propset.isLoading old={Old} new={New} changed={Changed}",
                XfadeLog.Tag(_albumId), _isLoading, value, changed);
            this.RaiseAndSetIfChanged(ref _isLoading, value);
        }
    }

    /// <summary>
    /// Loading state specifically for tracks.
    /// </summary>
    public bool IsLoadingTracks
    {
        get => _isLoadingTracks;
        set => this.RaiseAndSetIfChanged(ref _isLoadingTracks, value);
    }

    /// <summary>
    /// Whether an error occurred during loading.
    /// </summary>
    public bool HasError
    {
        get => _hasError;
        set => this.RaiseAndSetIfChanged(ref _hasError, value);
    }

    /// <summary>
    /// Error message to display.
    /// </summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    /// <summary>
    /// Search query for filtering tracks.
    /// </summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            var old = _searchQuery;
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            if (old != value && _allTracks.Count > 0)
                ApplyFilterAndSort();
        }
    }

    /// <summary>
    /// Current sort column.
    /// </summary>
    public AlbumSortColumn CurrentSortColumn
    {
        get => _currentSortColumn;
        set
        {
            var old = _currentSortColumn;
            this.RaiseAndSetIfChanged(ref _currentSortColumn, value);
            if (old != value)
            {
                this.RaisePropertyChanged(nameof(IsSortingByTitle));
                this.RaisePropertyChanged(nameof(IsSortingByArtist));
            }
        }
    }

    /// <summary>
    /// Whether sort is descending.
    /// </summary>
    public bool IsSortDescending
    {
        get => _isSortDescending;
        set
        {
            var old = _isSortDescending;
            this.RaiseAndSetIfChanged(ref _isSortDescending, value);
            if (old != value)
            {
                this.RaisePropertyChanged(nameof(SortChevronGlyph));
            }
        }
    }

    /// <summary>
    /// Total number of tracks.
    /// </summary>
    public int TotalTracks
    {
        get => _totalTracks;
        private set => this.RaiseAndSetIfChanged(ref _totalTracks, value);
    }

    /// <summary>
    /// Total duration formatted.
    /// </summary>
    public string TotalDuration
    {
        get => _totalDuration;
        private set => this.RaiseAndSetIfChanged(ref _totalDuration, value);
    }

    /// <summary>
    /// Currently selected items in the ListView.
    /// </summary>
    public IReadOnlyList<object> SelectedItems
    {
        get => _selectedItems;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedItems, value);
            this.RaisePropertyChanged(nameof(SelectedCount));
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(SelectionHeaderText));
            PlaySelectedCommand.NotifyCanExecuteChanged();
            PlayAfterCommand.NotifyCanExecuteChanged();
            AddSelectedToQueueCommand.NotifyCanExecuteChanged();
            AddToPlaylistCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Number of selected tracks.
    /// </summary>
    public int SelectedCount => SelectedItems.Count;

    /// <summary>
    /// Whether any tracks are selected.
    /// </summary>
    public bool HasSelection => SelectedItems.Count > 0;

    /// <summary>
    /// Selection header text for the command bar.
    /// </summary>
    public string SelectionHeaderText => SelectedCount == 1
        ? "1 track selected"
        : $"{SelectedCount} tracks selected";

    /// <summary>
    /// User's playlists for "Add to playlist" menu.
    /// </summary>
    public IReadOnlyList<PlaylistSummaryDto> Playlists => _playlists;

    /// <summary>
    /// More albums by the same artist.
    /// </summary>
    public IReadOnlyList<AlbumRelatedResult> MoreByArtist => _moreByArtist;

    private bool _hasMoreByArtist;
    public bool HasMoreByArtist
    {
        get => _hasMoreByArtist;
        private set => this.RaiseAndSetIfChanged(ref _hasMoreByArtist, value);
    }

    private bool _hasNoRelatedAlbums;
    public bool HasNoRelatedAlbums
    {
        get => _hasNoRelatedAlbums;
        private set => this.RaiseAndSetIfChanged(ref _hasNoRelatedAlbums, value);
    }

    /// <summary>
    /// Merchandise items for this album.
    /// </summary>
    public IReadOnlyList<AlbumMerchItemResult> MerchItems => _merchItems;

    public bool HasMerch => MerchItems.Count > 0;

    // ── Alternate releases (deluxe / remaster / anniversary editions of THIS album) ──

    private readonly ObservableCollection<AlbumAlternateReleaseResult> _alternateReleases = [];
    public IReadOnlyList<AlbumAlternateReleaseResult> AlternateReleases => _alternateReleases;

    private bool _hasAlternateReleases;
    public bool HasAlternateReleases
    {
        get => _hasAlternateReleases;
        private set => this.RaiseAndSetIfChanged(ref _hasAlternateReleases, value);
    }

    // ── Pre-release ──

    private bool _isPreRelease;
    public bool IsPreRelease
    {
        get => _isPreRelease;
        private set => this.RaiseAndSetIfChanged(ref _isPreRelease, value);
    }

    private DateTimeOffset? _preReleaseEndDateTime;
    public DateTimeOffset? PreReleaseEndDateTime
    {
        get => _preReleaseEndDateTime;
        private set => this.RaiseAndSetIfChanged(ref _preReleaseEndDateTime, value);
    }

    /// <summary>"Coming Friday, May 2 at 22:00" — formatted local time. Null when not pre-release.</summary>
    private string? _preReleaseFormatted;
    public string? PreReleaseFormatted
    {
        get => _preReleaseFormatted;
        private set => this.RaiseAndSetIfChanged(ref _preReleaseFormatted, value);
    }

    /// <summary>"in 3 days" / "in 4 hours" — caption on the right of the banner.</summary>
    private string? _preReleaseRelative;
    public string? PreReleaseRelative
    {
        get => _preReleaseRelative;
        private set => this.RaiseAndSetIfChanged(ref _preReleaseRelative, value);
    }

    // ── Share ──

    private string? _shareUrl;
    public string? ShareUrl
    {
        get => _shareUrl;
        private set
        {
            this.RaiseAndSetIfChanged(ref _shareUrl, value);
            this.RaisePropertyChanged(nameof(CanShare));
        }
    }

    public bool CanShare => !string.IsNullOrEmpty(ShareUrl);

    // ── Theme-aware palette (from the album cover) ──

    private AlbumPalette? _albumPalette;
    private bool _isDarkTheme;

    /// <summary>Subtle page-wash brush tinted toward the album's color. Null when no palette.</summary>
    private Brush? _paletteBackdropBrush;
    public Brush? PaletteBackdropBrush
    {
        get => _paletteBackdropBrush;
        private set => this.RaiseAndSetIfChanged(ref _paletteBackdropBrush, value);
    }

    /// <summary>Gradient brush used on the hero ink overlay (palette-tinted, theme-aware).</summary>
    private Brush? _paletteHeroGradientBrush;
    public Brush? PaletteHeroGradientBrush
    {
        get => _paletteHeroGradientBrush;
        private set => this.RaiseAndSetIfChanged(ref _paletteHeroGradientBrush, value);
    }

    /// <summary>Accent pill background brush (album type pill in the hero). Null falls back to system accent.</summary>
    private Brush? _paletteAccentPillBrush;
    public Brush? PaletteAccentPillBrush
    {
        get => _paletteAccentPillBrush;
        private set => this.RaiseAndSetIfChanged(ref _paletteAccentPillBrush, value);
    }

    private Brush? _paletteAccentPillForegroundBrush;
    public Brush? PaletteAccentPillForegroundBrush
    {
        get => _paletteAccentPillForegroundBrush;
        private set => this.RaiseAndSetIfChanged(ref _paletteAccentPillForegroundBrush, value);
    }

    // ── Hero meta line ("12 songs · 38 min · 1980") ──

    private string? _metaInlineLine;
    public string? MetaInlineLine
    {
        get => _metaInlineLine;
        private set => this.RaiseAndSetIfChanged(ref _metaInlineLine, value);
    }

    /// <summary>"ALBUM" / "SINGLE" / "EP" / "COMPILATION", upper-cased for the hero pill.</summary>
    public string AlbumTypeUpper => AlbumType?.ToUpperInvariant() ?? "ALBUM";

    /// <summary>
    /// Filtered and sorted tracks for UI binding. Stable instance — mutate via
    /// <see cref="ObservableCollectionExtensions.ReplaceWith{T}"/> / Clear so the
    /// bound ListView keeps its CollectionChanged subscription across navs.
    /// </summary>
    private readonly ObservableCollection<LazyTrackItem> _filteredTracks = [];
    public IReadOnlyList<LazyTrackItem> FilteredTracks => _filteredTracks;

    // Sort indicator properties for column headers
    public bool IsSortingByTitle => CurrentSortColumn == AlbumSortColumn.Title;
    public bool IsSortingByArtist => CurrentSortColumn == AlbumSortColumn.Artist;
    public bool IsSortingByAlbum => false;
    public bool IsSortingByAddedAt => false;

    /// <summary>
    /// Sort chevron glyph: up for ascending, down for descending.
    /// </summary>
    public string SortChevronGlyph => IsSortDescending ? "\uE70D" : "\uE70E";

    public AlbumViewModel(
        IAlbumService albumService,
        AlbumStore albumStore,
        ILibraryDataService libraryDataService,
        IPlaybackStateService playbackStateService,
        ITrackLikeService? likeService = null,
        ILogger<AlbumViewModel>? logger = null)
    {
        _albumService = albumService;
        _albumStore = albumStore;
        _libraryDataService = libraryDataService;
        _playbackStateService = playbackStateService;
        _likeService = likeService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _logger = logger;

        AttachLongLivedServices();

        Diagnostics.LiveInstanceTracker.Register(this);
    }

    // Long-lived singleton subscriptions are attached lazily and detached on
    // Hibernate so the (Transient) VM is not pinned by singleton invocation lists.
    private bool _longLivedAttached;

    private void AttachLongLivedServices()
    {
        if (_longLivedAttached) return;
        _longLivedAttached = true;
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
    }

    private void DetachLongLivedServices()
    {
        if (!_longLivedAttached) return;
        _longLivedAttached = false;
        if (_likeService != null)
            _likeService.SaveStateChanged -= OnSaveStateChanged;
    }

    public void Initialize(string albumId, bool preserveHeaderPrefill = false)
    {
        AttachLongLivedServices();
        var branch = AlbumId != albumId ? "reset" : "same";
        _logger?.LogDebug(
            "[xfade][album-vm:{Id}] init incoming={Incoming} current={Current} branch={Branch}",
            XfadeLog.Tag(albumId), XfadeLog.Tag(albumId), XfadeLog.Tag(AlbumId), branch);
        if (AlbumId != albumId)
        {
            _appliedDetailFor = null;
            // Clear per-album state that ApplyDetailAsync only overwrites when the
            // new value is non-empty — otherwise a cached page swap leaves last
            // album's artist avatar / cover / palette / pills visible until the
            // new detail lands. PrefillFrom, called immediately after Activate, then
            // re-fills the parts the navigation parameter knows about (title, cover,
            // artist name); the AlbumStore push fills the rest.
            ArtistImageUrl = null;
            ArtistId = "";
            if (!preserveHeaderPrefill)
            {
                ArtistName = "";
                AlbumImageUrl = null;
                AlbumName = "";
            }
            Year = 0;
            AlbumType = null;
            Label = null;
            ReleaseDateFormatted = null;
            CopyrightsText = null;
            ShareUrl = null;
            IsPreRelease = false;
            PreReleaseFormatted = null;
            PreReleaseRelative = null;
            MetaInlineLine = null;
            // Defer the rare/below-the-fold shelves to a low-priority
            // dispatch — these are all x:Load-gated in AlbumPage.xaml so
            // their reset doesn't affect first paint. Cuts the
            // PropertyChanged storm before the hero/track-list shimmer
            // commits.
            _dispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    HasAlternateReleases = false;
                    _alternateReleases.Clear();
                    HasMoreByArtist = false;
                    _moreByArtist.Clear();
                    _merchItems.Clear();
                    this.RaisePropertyChanged(nameof(HasMerch));
                });
            this.RaisePropertyChanged(nameof(AlbumTypeUpper));
            PaletteBackdropBrush = null;
            PaletteAccentPillBrush = null;
            PaletteAccentPillForegroundBrush = null;
            PaletteHeroGradientBrush = null;

            // Force the page + grid back into the loading/skeleton states so the
            // cached page doesn't paint old tracks under the new header during the
            // gap between Activate and the AlbumStore's first push.
            IsLoading = !preserveHeaderPrefill || string.IsNullOrEmpty(AlbumImageUrl);
            IsLoadingTracks = true;
            _allTracks = [];
            _popularTrackIds.Clear();
            _filteredTracks.ReplaceWith(Enumerable.Range(0, 10)
                .Select(i => LazyTrackItem.Placeholder($"ph-{i}", i + 1)));
        }

        AlbumId = albumId;
        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Album, albumId)
        {
            Title = "Album"
        };
        if (preserveHeaderPrefill)
            UpdateTabTitle();

        RefreshSaveState();
    }

    /// <summary>
    /// Start observing the album detail through AlbumStore. Disposing the
    /// subscription on navigation-away cancels any inflight Pathfinder query.
    /// </summary>
    public void Activate(string albumId, bool preserveHeaderPrefill = false)
    {
        _logger?.LogDebug("[xfade][album-vm:{Id}] activate", XfadeLog.Tag(albumId));
        Initialize(albumId, preserveHeaderPrefill);

        _subscriptions?.Dispose();
        _subscriptions = new CompositeDisposable();

        var sub = _albumStore.Observe(albumId)
            .Subscribe(
                state => _dispatcherQueue.TryEnqueue(() => ApplyDetailState(state, albumId)),
                ex => _logger?.LogError(ex, "AlbumStore stream faulted for {AlbumId}", albumId));
        _subscriptions.Add(sub);
    }

    public void Deactivate()
    {
        DetachLongLivedServices();
        _subscriptions?.Dispose();
        _subscriptions = null;
    }

    /// <summary>
    /// Heavy-state release for cached pages going off-screen. Drops the track grid,
    /// More-by-artist, alternate releases, and merch — those are the bound
    /// collections that cost the most composition memory while the page sits
    /// invisible in the Frame cache. Lightweight identity (AlbumId, AlbumName,
    /// AlbumImageUrl, palette brushes) is preserved so the hero still renders
    /// correctly during the brief window between re-Activate and the
    /// AlbumStore's BehaviorSubject re-emitting the cached value.
    /// </summary>
    public void Hibernate()
    {
        Deactivate();
        _appliedDetailFor = null;

        _filteredTracks.Clear();
        _allTracks = [];
        _popularTrackIds.Clear();
        _alternateReleases.Clear();
        HasAlternateReleases = false;
        _moreByArtist.Clear();
        HasMoreByArtist = false;
        _merchItems.Clear();
        this.RaisePropertyChanged(nameof(HasMerch));
    }

    private void ApplyDetailState(EntityState<AlbumDetailResult> state, string expectedAlbumId)
    {
        if (_disposed || AlbumId != expectedAlbumId)
        {
            _logger?.LogDebug(
                "[xfade][album-vm:{Id}] state.skip expected={Expected} current={Current}",
                XfadeLog.Tag(expectedAlbumId), XfadeLog.Tag(expectedAlbumId), XfadeLog.Tag(AlbumId));
            return;
        }

        switch (state)
        {
            case EntityState<AlbumDetailResult>.Initial:
                _logger?.LogDebug("[xfade][album-vm:{Id}] state.initial isLoadingPre={Pre}", XfadeLog.Tag(expectedAlbumId), IsLoading);
                IsLoading = string.IsNullOrEmpty(AlbumImageUrl);
                IsLoadingTracks = true;
                break;
            case EntityState<AlbumDetailResult>.Loading loading:
                _logger?.LogDebug(
                    "[xfade][album-vm:{Id}] state.loading hasPrevious={HasPrev} hasImage={HasImage} isLoadingPre={Pre}",
                    XfadeLog.Tag(expectedAlbumId), loading.Previous is not null, !string.IsNullOrEmpty(AlbumImageUrl), IsLoading);
                IsLoading = loading.Previous is null && string.IsNullOrEmpty(AlbumImageUrl);
                break;
            case EntityState<AlbumDetailResult>.Ready ready:
                var willApply = _appliedDetailFor != expectedAlbumId || ready.Freshness == Freshness.Fresh;
                _logger?.LogDebug(
                    "[xfade][album-vm:{Id}] state.ready freshness={Freshness} appliedFor={AppliedFor} willApply={WillApply} isLoadingPre={Pre}",
                    XfadeLog.Tag(expectedAlbumId), ready.Freshness, XfadeLog.Tag(_appliedDetailFor), willApply, IsLoading);
                if (willApply)
                    _ = ApplyDetailAsync(ready.Value, expectedAlbumId);
                break;
            case EntityState<AlbumDetailResult>.Error error:
                _logger?.LogDebug("[xfade][album-vm:{Id}] state.error", XfadeLog.Tag(expectedAlbumId));
                HasError = true;
                ErrorMessage = error.Exception.Message;
                IsLoading = false;
                IsLoadingTracks = false;
                _logger?.LogError(error.Exception, "AlbumStore reported error for {AlbumId}", expectedAlbumId);
                break;
        }
    }

    private void UpdateTabTitle()
    {
        if (TabItemParameter != null && !string.IsNullOrEmpty(AlbumName))
        {
            TabItemParameter.Title = AlbumName;
            ContentChanged?.Invoke(this, TabItemParameter);
        }
    }

    private void ApplyFilterAndSort()
    {
        using var _p = Services.UiOperationProfiler.Instance?.Profile("AlbumFilterSort");
        IEnumerable<LazyTrackItem> result = _allTracks;

        // Filter
        var query = SearchQuery?.Trim();
        if (!string.IsNullOrEmpty(query))
        {
            result = result.Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.ArtistName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        // Sort
        result = (CurrentSortColumn, IsSortDescending) switch
        {
            (AlbumSortColumn.Title, false) => result.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase),
            (AlbumSortColumn.Title, true) => result.OrderByDescending(t => t.Title, StringComparer.OrdinalIgnoreCase),
            (AlbumSortColumn.Artist, false) => result.OrderBy(t => t.ArtistName, StringComparer.OrdinalIgnoreCase),
            (AlbumSortColumn.Artist, true) => result.OrderByDescending(t => t.ArtistName, StringComparer.OrdinalIgnoreCase),
            (AlbumSortColumn.TrackNumber, true) => result.OrderByDescending(t => t.OriginalIndex),
            _ => result // already in track order
        };

        _filteredTracks.ReplaceWith(result);
    }

    public bool IsPopularTrack(object? row)
    {
        if (_popularTrackIds.Count == 0)
            return false;

        return row switch
        {
            LazyTrackItem { Data: AlbumTrackDto track } => _popularTrackIds.Contains(track.Id),
            LazyTrackItem lazy => _popularTrackIds.Contains(lazy.Id),
            AlbumTrackDto track => _popularTrackIds.Contains(track.Id),
            ITrackItem track => _popularTrackIds.Contains(track.Id),
            _ => false
        };
    }

    private static HashSet<string> BuildPopularTrackIdSet(IReadOnlyList<AlbumTrackDto> tracks)
    {
        var count = tracks.Count switch
        {
            <= 0 => 0,
            <= 4 => 1,
            <= 9 => 2,
            _ => 3
        };

        return tracks
            .Where(t => t.PlayCount > 0 && !string.IsNullOrWhiteSpace(t.Id))
            .OrderByDescending(t => t.PlayCount)
            .Take(count)
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FormatDuration(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} hr {ts.Minutes} min";
        return $"{ts.Minutes} min";
    }

    /// <summary>"12 songs · 38 min · 1980" — null parts are skipped.</summary>
    private static string BuildMetaInlineLine(int trackCount, double totalSeconds, int year)
    {
        var parts = new List<string>(3);
        if (trackCount > 0) parts.Add(trackCount == 1 ? "1 song" : $"{trackCount} songs");
        if (totalSeconds > 0) parts.Add(FormatDuration(totalSeconds));
        if (year > 0) parts.Add(year.ToString());
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Returns ("Coming Friday, May 2 at 22:00", "in 3 days") or (null, null) when not pre-release
    /// or the date has already passed.
    /// </summary>
    private static (string?, string?) FormatPreRelease(bool isPreRelease, DateTimeOffset? releaseAt)
    {
        if (!isPreRelease || releaseAt == null || releaseAt <= DateTimeOffset.Now)
            return (null, null);

        var local = releaseAt.Value.ToLocalTime();
        var formatted = $"Coming {local:dddd, MMMM d} at {local:HH:mm}";

        var delta = local - DateTimeOffset.Now;
        string relative;
        if (delta.TotalDays >= 1)
        {
            var days = (int)Math.Ceiling(delta.TotalDays);
            relative = days == 1 ? "in 1 day" : $"in {days} days";
        }
        else if (delta.TotalHours >= 1)
        {
            var hours = (int)Math.Ceiling(delta.TotalHours);
            relative = hours == 1 ? "in 1 hour" : $"in {hours} hours";
        }
        else
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(delta.TotalMinutes));
            relative = minutes == 1 ? "in 1 minute" : $"in {minutes} minutes";
        }

        return (formatted, relative);
    }

    /// <summary>
    /// Theme-aware palette refresh. Called by the page on load and on
    /// ActualThemeChanged. Mirrors ConcertViewModel.ApplyTheme: dark theme uses
    /// HigherContrast (deepest), light theme uses HighContrast (saturated but a
    /// step brighter). MinContrast is skipped — too pastel for white overlay text.
    /// </summary>
    public void ApplyTheme(bool isDark)
    {
        _isDarkTheme = isDark;

        var tier = _albumPalette is null
            ? null
            : (isDark
                ? (_albumPalette.HigherContrast ?? _albumPalette.HighContrast)
                : (_albumPalette.HighContrast ?? _albumPalette.HigherContrast));

        if (tier == null)
        {
            PaletteBackdropBrush = null;
            PaletteHeroGradientBrush = null;
            PaletteAccentPillBrush = null;
            PaletteAccentPillForegroundBrush = null;
            return;
        }

        var bg = Color.FromArgb(255, tier.BackgroundR, tier.BackgroundG, tier.BackgroundB);
        var bgTint = Color.FromArgb(255, tier.BackgroundTintedR, tier.BackgroundTintedG, tier.BackgroundTintedB);
        // Lifted accent base — same shape as Artist/Show. Replaces raw TextAccent
        // (which was ≈ Spotify green for most albums) so the play button reads as
        // part of the cover-derived identity in both Light and Dark.
        var accentBase = TintColorHelper.BrightenForTint(bgTint, targetMax: 210);

        // Light mode: blend palette colors toward white before applying alpha so
        // dark covers don't drag the page dark. Dark mode unchanged.
        var heroBg     = isDark ? bg     : TintColorHelper.LightTint(bg);
        var heroBgTint = isDark ? bgTint : TintColorHelper.LightTint(bgTint);
        var washColor  = isDark ? bg     : TintColorHelper.LightTint(bg);

        PaletteBackdropBrush = new SolidColorBrush(Color.FromArgb(
            (byte)(isDark ? 60 : 38), washColor.R, washColor.G, washColor.B));

        var (a0, a1, a2, a3) = isDark ? (240, 176, 80, 0) : (140, 100, 50, 0);
        var heroGrad = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
        };
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a0, heroBgTint.R, heroBgTint.G, heroBgTint.B), Offset = 0.0 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a1, heroBg.R,     heroBg.G,     heroBg.B),     Offset = 0.35 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a2, heroBg.R,     heroBg.G,     heroBg.B),     Offset = 0.65 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a3, heroBg.R,     heroBg.G,     heroBg.B),     Offset = 1.0 });
        PaletteHeroGradientBrush = heroGrad;

        PaletteAccentPillBrush = new SolidColorBrush(accentBase);
        var accentLuma = (accentBase.R * 299 + accentBase.G * 587 + accentBase.B * 114) / 1000;
        PaletteAccentPillForegroundBrush = new SolidColorBrush(
            accentLuma > 160 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255));
    }

    /// <summary>
    /// Copy the album's public Spotify URL to the clipboard. Page wires this to
    /// the Share button + a confirmation toast via INotificationService.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShare))]
    private void Share()
    {
        if (string.IsNullOrEmpty(ShareUrl)) return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(ShareUrl);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    public void PrefillFrom(Data.Parameters.ContentNavigationParameter nav, bool clearMissing = false)
    {
        if (!string.IsNullOrEmpty(nav.Title)) AlbumName = nav.Title;
        else if (clearMissing) AlbumName = "";

        if (!string.IsNullOrEmpty(nav.ImageUrl)) AlbumImageUrl = nav.ImageUrl;
        else if (clearMissing) AlbumImageUrl = null;

        if (!string.IsNullOrEmpty(nav.Subtitle)) ArtistName = nav.Subtitle;
        else if (clearMissing) ArtistName = "";
    }

    /// <summary>
    /// Apply a pre-fetched AlbumDetailResult from the AlbumStore. Called by
    /// ApplyDetailState once the store emits Ready; drives tracklist build,
    /// related-albums, and the non-blocking merch fetch.
    /// </summary>
    private async Task ApplyDetailAsync(AlbumDetailResult detail, string albumId)
    {
        if (_disposed || AlbumId != albumId) return;
        _appliedDetailFor = albumId;
        HasError = false;
        ErrorMessage = null;

        try
        {
            // Show shimmer placeholders immediately if we don't have any tracks yet.
            if (_allTracks.Count == 0)
            {
                _filteredTracks.ReplaceWith(Enumerable.Range(0, 10)
                    .Select(i => LazyTrackItem.Placeholder($"ph-{i}", i + 1)));
            }

            // Rootlist for "Add to playlist" — fire-and-forget so it doesn't block detail render.
            _ = LoadRootlistAsync();

            // Map metadata (respect prefilled values from navigation)
            if (!string.IsNullOrEmpty(detail.Name))
                AlbumName = detail.Name;
            if (!string.IsNullOrEmpty(detail.CoverArtUrl) && string.IsNullOrEmpty(AlbumImageUrl))
                AlbumImageUrl = detail.CoverArtUrl;
            ColorHex = detail.ColorDarkHex;
            var firstArtist = detail.Artists.FirstOrDefault();
            if (firstArtist != null)
            {
                if (!string.IsNullOrEmpty(firstArtist.Uri)) ArtistId = firstArtist.Uri;
                if (!string.IsNullOrEmpty(firstArtist.Name)) ArtistName = firstArtist.Name;
                if (!string.IsNullOrEmpty(firstArtist.ImageUrl)) ArtistImageUrl = firstArtist.ImageUrl;
            }

            // Header multi-artist surfaces. Billed artists drive the avatar
            // strip; the flyout lists the union of billed + per-track
            // contributors deduped by URI so featured guests on individual
            // tracks become reachable from the header.
            var billed = detail.Artists ?? [];
            Artists = billed;
            ArtistAvatarItems = billed
                .Select(a => new AvatarStackItem(a.Name ?? "", a.ImageUrl))
                .ToList();
            HeaderArtistLinks = billed
                .Select((a, idx) => new HeaderArtistLink
                {
                    Name = a.Name ?? "",
                    Uri = a.Uri ?? "",
                    IsFirst = idx == 0
                })
                .ToList();
            var billedUris = new HashSet<string>(
                billed.Where(a => !string.IsNullOrEmpty(a.Uri)).Select(a => a.Uri!),
                StringComparer.Ordinal);
            var trackExtras = (detail.Tracks ?? [])
                .SelectMany(t => t.Artists ?? (IReadOnlyList<TrackArtistRef>)Array.Empty<TrackArtistRef>())
                .Where(a => !string.IsNullOrEmpty(a.Uri) && !billedUris.Contains(a.Uri))
                .GroupBy(a => a.Uri, StringComparer.Ordinal)
                .Select(g => g.First())
                .Select(a => new AlbumArtistResult
                {
                    Id = a.Id,
                    Uri = a.Uri,
                    Name = a.Name,
                    ImageUrl = null
                })
                .ToList();
            AllDistinctArtists = billed.Concat(trackExtras).ToList();
            OverflowArtistCount = trackExtras.Count;
            if (detail.ReleaseDate.Year > 0)
                Year = detail.ReleaseDate.Year;
            if (!string.IsNullOrEmpty(detail.Type))
                AlbumType = detail.Type;
            IsSaved = detail.IsSaved;
            Label = detail.Label;
            if (detail.ReleaseDate != default)
                ReleaseDateFormatted = detail.ReleaseDate.ToString("MMMM d, yyyy");
            if (detail.Copyrights?.Count > 0)
                CopyrightsText = string.Join("\n", detail.Copyrights.Select(c =>
                {
                    var prefix = c.Type == "P" ? "\u2117" : "\u00A9";
                    var text = c.Text?.TrimStart() ?? "";
                    if (text.StartsWith("\u00A9") || text.StartsWith("\u2117"))
                        return text;
                    return $"{prefix} {text}";
                }));

            RefreshSaveState();

            IsLoading = false;

            // Build real track list
            _allTracks = detail.Tracks
                .Select((t, i) => LazyTrackItem.Loaded(t.Id, i + 1, t))
                .ToList();
            _popularTrackIds = BuildPopularTrackIdSet(detail.Tracks);

            TotalTracks = _allTracks.Count;
            TotalDuration = FormatDuration(_allTracks.Sum(t => t.Duration.TotalSeconds));
            IsLoadingTracks = false;

            // Swap directly from shimmer placeholders to real tracks — one realization
            // burst instead of two, and no visible empty-state flash. The previous
            // "clear -> delay -> repopulate" pattern forced the ListView to tear down
            // every container, flash empty for 50 ms, then rebuild every container,
            // which read as "buggy and laggy" in the transition.
            ApplyFilterAndSort();

            // Related albums
            _moreByArtist.ReplaceWith(detail.MoreByArtist);
            HasMoreByArtist = MoreByArtist.Count > 0;
            HasNoRelatedAlbums = !HasMoreByArtist;

            // Alternate releases (deluxe / remaster / anniversary editions of THIS album)
            _alternateReleases.ReplaceWith(detail.AlternateReleases);
            HasAlternateReleases = AlternateReleases.Count > 0;

            // Pre-release banner
            IsPreRelease = detail.IsPreRelease;
            PreReleaseEndDateTime = detail.PreReleaseEndDateTime;
            (PreReleaseFormatted, PreReleaseRelative) = FormatPreRelease(detail.IsPreRelease, detail.PreReleaseEndDateTime);

            // Share URL (drives Share button enable + clipboard payload)
            ShareUrl = detail.ShareUrl;

            // Hero meta line: "12 songs · 38 min · 1980"
            MetaInlineLine = BuildMetaInlineLine(TotalTracks, _allTracks.Sum(t => t.Duration.TotalSeconds), Year);

            // Adopt the album's palette (if any) and rebuild the theme-aware brushes.
            _albumPalette = detail.Palette;
            ApplyTheme(_isDarkTheme);

            this.RaisePropertyChanged(nameof(AlbumTypeUpper));

            // Merch (non-blocking, loaded after main content)
            _ = LoadMerchAsync(albumId);
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            _logger?.LogError(ex, "Failed to load album {AlbumId}", albumId);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Retry()
    {
        HasError = false;
        ErrorMessage = null;
        if (!string.IsNullOrEmpty(AlbumId))
        {
            _appliedDetailFor = null;
            _albumStore.Invalidate(AlbumId);
        }
    }

    private async Task LoadRootlistAsync()
    {
        try
        {
            var list = await _libraryDataService.GetUserPlaylistsAsync().ConfigureAwait(false);
            if (_disposed)
                return;

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed)
                    _playlists.ReplaceWith(list);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadRootlistAsync failed (album)");
        }
    }

    [RelayCommand]
    private void SortBy(string columnName)
    {
        if (!Enum.TryParse<AlbumSortColumn>(columnName, out var column))
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

        ApplyFilterAndSort();
    }

    [RelayCommand]
    private void PlayAlbum()
    {
        BuildQueueAndPlay(0, shuffle: false);
    }

    [RelayCommand]
    private void ShuffleAlbum()
    {
        _playbackStateService.SetShuffle(true);
        BuildQueueAndPlay(0, shuffle: true);
    }

    [RelayCommand]
    private void ToggleSave()
    {
        if (_likeService == null || string.IsNullOrEmpty(AlbumId))
            return;

        var albumUri = NormalizeAlbumUri(AlbumId);
        var wasSaved = _likeService.IsSaved(SavedItemType.Album, albumUri);

        IsSaved = !wasSaved;
        _likeService.ToggleSave(SavedItemType.Album, albumUri, wasSaved);
    }

    private void RefreshSaveState()
    {
        if (_likeService == null || string.IsNullOrEmpty(AlbumId))
            return;

        IsSaved = _likeService.IsSaved(SavedItemType.Album, NormalizeAlbumUri(AlbumId));
    }

    private void OnSaveStateChanged()
    {
        _dispatcherQueue.TryEnqueue(RefreshSaveState);
    }

    private static string NormalizeAlbumUri(string albumIdOrUri)
    {
        const string prefix = "spotify:album:";
        if (albumIdOrUri.StartsWith(prefix, StringComparison.Ordinal))
            return albumIdOrUri;

        return $"{prefix}{albumIdOrUri}";
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
            AlbumArt = t.ImageUrl ?? AlbumImageUrl,
            DurationMs = t.Duration.TotalMilliseconds,
            IsUserQueued = false
        }).ToList();

        if (shuffle)
        {
            queueItems.Shuffle();
            startIndex = 0;
        }

        var context = new PlaybackContextInfo
        {
            ContextUri = AlbumId,
            Type = PlaybackContextType.Album,
            Name = AlbumName,
            ImageUrl = AlbumImageUrl
        };

        _playbackStateService.LoadQueue(queueItems, context, startIndex);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlaySelected()
    {
        if (!HasSelection) return;
        // TODO: Implement play selected tracks
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PlayAfter()
    {
        if (!HasSelection) return;
        // TODO: Implement play after current track
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddSelectedToQueue()
    {
        if (!HasSelection) return;
        // TODO: Implement add selected to queue
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        // Not applicable for albums - tracks can't be removed
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddToPlaylist(PlaylistSummaryDto? playlist)
    {
        if (playlist == null || !HasSelection) return;
        // TODO: Implement add selected tracks to playlist
    }

    [RelayCommand]
    private async Task OpenMerchItemAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to open merch URL: {Url}", url);
        }
    }

    private async Task LoadMerchAsync(string albumUri)
    {
        try
        {
            var items = await Task.Run(async () => await _albumService.GetMerchAsync(albumUri));
            _merchItems.ReplaceWith(items);
            this.RaisePropertyChanged(nameof(HasMerch));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load merch for {AlbumId}", albumUri);
        }
    }

    [RelayCommand]
    private void OpenArtist()
    {
        if (!string.IsNullOrEmpty(ArtistId))
        {
            Helpers.Navigation.NavigationHelpers.OpenArtist(ArtistId, ArtistName ?? "Artist");
        }
    }

    [RelayCommand]
    private void OpenLikedSongs()
    {
        Helpers.Navigation.NavigationHelpers.OpenLikedSongs();
    }

    [RelayCommand]
    private void OpenRelatedAlbum(string albumId)
    {
        var album = MoreByArtist.FirstOrDefault(a => a.Id == albumId);
        Helpers.Navigation.NavigationHelpers.OpenAlbum(albumId, album?.Name ?? "Album");
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Deactivate();
        DetachLongLivedServices();

        _allTracks.Clear();
        _popularTrackIds.Clear();
        _filteredTracks.Clear();
        _alternateReleases.Clear();
        _moreByArtist.Clear();
        _merchItems.Clear();
        _playlists.Clear();
    }
}
